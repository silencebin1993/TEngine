using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-CHAIN-01（FG19 FGR-EMG-067 / 084 阶段 1 计数路径 / 180；ADR-EMG-001 ⑨）：因果链记录与可靠计数。全部在内核（Main/Sim，Burst 步进里跑），每处 O(1)：
    /// - 环形记录 <see cref="CombatData.EmgRing"/>：每次开链（出手 / 热更注入）、触发放子载荷、残骸、破片、规则结算、链深截断各写一条；链号为 0（没有程序的出手）不写，
    ///   所以 emg.stage = 0 的正式流程环一直是空的。不进快照、不进状态哈希。
    /// - 可靠计数：规则计数槽按稳定键开放寻址（<see cref="EmgRuleSlot"/>）、事件端口入队计数；有变化的规则槽进脏列表，热更层每步只读脏列表（FGR-EMG-067）。
    /// - 阶段 1 唯一的规则计数来源：连锁按“已标记”偏好确实跳到已标记目标（fw_marktag 的 grm.mod.chain，FGR-EMG-084）→ 计数 + 1、按需入队 evt.react。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>写一条因果链记录（环满覆盖最旧的）。链号 ≤ 0 不写——残骸例外：没有程序的击毁也记（Key = 残骸 Id，悬停“被谁击毁”用；残骸只在实体层开着时生成，正式流程照样不写）。</summary>
        internal static void EmgChainLog(ref CombatData d, EmgChainNode kind, int chain, byte depth, int key, int srcUnit, double2 pos, int other = 0, byte aux = 0, int parent = 0)
        {
            if (chain <= 0 && kind != EmgChainNode.Wreck)
            {
                return;
            }
            int head = d.EmgRingHead[0];
            d.EmgRing[head] = new EmgChainRec
            {
                Chain = chain, Parent = parent, Kind = kind, Depth = depth, Aux = aux, Key = key, SrcUnit = srcUnit, Other = other, Pos = pos, Time = d.Scalars[0].Time,
            };
            d.EmgRingHead[0] = (head + 1) & (EmgConst.ChainRing - 1);
            d.EmgRingHead[1] = math.min(d.EmgRingHead[1] + 1, EmgConst.ChainRing);
        }

        /// <summary>出手者槽位 → 单位 ID（−1 / 越界 = 0）。</summary>
        private static int EmgIdOf(ref CombatData d, int slot) => slot >= 0 && slot < d.Count ? d.Id[slot] : 0;

        /// <summary>
        /// 规则计数槽：按稳定键找已分配的槽，没有就占第一个空槽（槽位一经分配本场不挪，evt.react 的 Code 就是这个下标）。256 槽全满返回 −1（不该发生：阶段 4 全部具名规则约 70 条）。
        /// </summary>
        internal static int EmgRuleSlot(ref CombatData d, int key)
        {
            if (key == 0)
            {
                return -1;
            }
            int n = EmgConst.RuleSlots;
            int start = (int)((uint)key % (uint)n);
            for (int q = 0; q < n; q++)
            {
                int i = (start + q) & (n - 1);
                int k = d.EmgRuleKey[i];
                if (k == key)
                {
                    return i;
                }
                if (k == 0)
                {
                    d.EmgRuleKey[i] = key;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>规则计数 + 1（可靠，进快照）、记进给、进本步脏列表（同一步同一槽只进一次）。</summary>
        internal static int EmgRuleHit(ref CombatData d, int key, int src, int target, double2 pos)
        {
            int slot = EmgRuleSlot(ref d, key);
            if (slot < 0)
            {
                return -1;
            }
            d.EmgRuleCount[slot] = d.EmgRuleCount[slot] + 1;
            d.EmgRuleFeed[slot] = new EmgRuleFeed { Pos = pos, Src = src, Target = target };
            if (d.EmgDirtyMark[slot] == 0)
            {
                d.EmgDirtyMark[slot] = 1;
                d.EmgDirty.Add((short)slot);
            }
            return slot;
        }

        /// <summary>
        /// FGR-EMG-084 阶段 1 计数路径：连锁指令带“已标记”偏好（fw_marktag 写的 grm.mod.chain）且这一跳确实落在已标记目标上 → reaction_marktag 计数 + 1（按稳定键，
        /// <see cref="CombatConfig.EmgMarkTagKey"/>）、因果链记一条“规则”、按需入队 evt.react（Code = 计数槽下标，Other = 目标，Value = 累计次数）。
        /// 没有已标记目标时连锁本身就不跳（只有“已标记”偏好）或跳的不是已标记目标（还有导电 / 最近偏好）——都不发不计。
        /// </summary>
        internal static void EmgMarkJumpReact(ref CombatData d, int owner, int target, EmgInstrFlags pref)
        {
            if ((pref & EmgInstrFlags.PreferMarked) == 0 || target < 0 || target >= d.Count || !(d.MarkedUntil[target] > d.Scalars[0].Time))
            {
                return;
            }
            int key = d.Config.EmgMarkTagKey;
            double2 pos = d.Pos[target];
            int src = EmgIdOf(ref d, owner);
            int slot = EmgRuleHit(ref d, key, src, d.Id[target], pos);
            if (slot < 0)
            {
                return;
            }
            EmgChainLog(ref d, EmgChainNode.Rule, d.EmgCtx.Chain, d.EmgCtx.Depth, key, src, pos, d.Id[target]);
            EmgShotEvent(ref d, EmgEventKind.React, d.Id[target], d.EmgRuleCount[slot], 0f, (ushort)slot, pos);
        }

        /// <summary>
        /// ADR ⑨ 归属：最多 4 个单位 ID、按出现先后；已在里面的不重复；满 4 个时保留最早 3 个 + 最近 1 个（第 4 格被新来的替换）。0 不记。
        /// </summary>
        public static void EmgOwnersAdd(ref int4 owners, int id)
        {
            if (id == 0 || owners.x == id || owners.y == id || owners.z == id || owners.w == id)
            {
                return;
            }
            if (owners.x == 0)
            {
                owners.x = id;
            }
            else if (owners.y == 0)
            {
                owners.y = id;
            }
            else if (owners.z == 0)
            {
                owners.z = id;
            }
            else
            {
                owners.w = id; // 满了：最早 3 个不动，第 4 格永远是最近的那个
            }
        }
    }
}
