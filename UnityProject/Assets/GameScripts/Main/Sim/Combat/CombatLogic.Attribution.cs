using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG2-DISC-01：本步敌方挨的一条交互伤害（已按“规则键 + 归属”合并）。Key = 0 = 没有规则键（排放的物质、相态持续伤害、无规则节点的破片）。</summary>
    public struct EmgAttrRow
    {
        public int Key;
        public int4 Owners;
        public double Damage;
    }

    /// <summary>EMG2-DISC-01：下一次 DamageUnit 属于哪条交互（瞬时量，不进快照）。</summary>
    public struct EmgAttrScope
    {
        public byte On;
        public int Key;
        public int Friendly;
        public int Chain;
        public int4 Owners;
    }

    /// <summary>友伤行的来源码：1～17 = 物质码；下面三个是物质以外的来源。</summary>
    public static class EmgFriendlyCode
    {
        /// <summary>不记友伤（普通破片打同阵营另走 <see cref="CombatLogic.EmgFriendlyHit"/>）。</summary>
        public const int None = -1;
        public const int Fragment = 0;
        public const int Energy = 1000;
        public const int Reaction = 1001;
    }

    /// <summary>
    /// EMG2-DISC-01（FG19 FGR-EMG-182 / 184；ADR ⑨）：交互伤害记账。规则产物（斑块、破片、规则附带伤害、相变效果）的伤害在进 DamageUnit 前登记归属，
    /// 进入统一扣血入口后取走：打敌方记一条交互伤害（按规则键 + 归属合并），打己方且归属里有己方单位记一条友伤。全部 O(本步不同组合数)，只在内核；结果不进快照、不进状态哈希。
    /// </summary>
    public static partial class CombatLogic
    {
        internal static void EmgAttrBegin(ref CombatData d, int key, in int4 owners, int friendly, int chain) =>
            d.EmgAttr = new EmgAttrScope { On = 1, Key = key, Owners = owners, Friendly = friendly, Chain = chain };

        /// <summary>
        /// 斑块沾染的持续伤害（如明火的燃烧位）在 StepStatus 里逐位结算，那里分不清来自斑块还是武器：在接触节拍按这一节拍的伤害记账。
        /// shortcut: 离开斑块后残留的那几秒不计、几块斑块同时沾同一位时按块各记一次，只影响交互伤害占比的精度；要算全需要每单位每状态位存来源（K1 预算不够）。
        /// </summary>
        internal static void EmgCoatDotNote(ref CombatData d, int u, in CombatZone z, int coatBit, float elapsed)
        {
            CombatStatusFx fx = d.StatusFx[coatBit];
            if (fx.Effect == CombatStatusFx.Dot && fx.Amount > 0f && (d.Status[u] & (1u << coatBit)) != 0u)
            {
                EmgAttrBegin(ref d, z.RuleKey, z.Owners, z.Matter, z.EmgChain);
                EmgAttrTake(ref d, u, fx.Amount * elapsed);
            }
        }

        /// <summary>破片命中：归属取破片所在因果链上的单位（规则键取链上最近的规则节点）；没有链时归放出它的单位。己方阵营放出的破片打同阵营走 EmgFriendlyHit，这里不重复记。</summary>
        internal static void EmgAttrFragment(ref CombatData d, in CombatProjectile pr)
        {
            int4 owners = EmgChainAttr(ref d, pr.Chain, out int key);
            if (math.all(owners == int4.zero))
            {
                EmgOwnersAdd(ref owners, pr.Owner);
            }
            EmgAttrBegin(ref d, key, owners, pr.Faction == CombatFaction.Player ? EmgFriendlyCode.None : EmgFriendlyCode.Fragment, pr.Chain);
        }

        /// <summary>统一扣血入口（EmgOnDamage）里调用：这次伤害若已登记归属，就地记账并清掉登记。</summary>
        internal static void EmgAttrTake(ref CombatData d, int t, float damage)
        {
            if (d.EmgAttr.On == 0)
            {
                return;
            }
            EmgAttrScope a = d.EmgAttr;
            d.EmgAttr.On = 0;
            if (!(damage > 0f))
            {
                return;
            }
            if (d.Faction[t] == (byte)CombatFaction.Player)
            {
                EmgFriendlyNote(ref d, d.Id[t], a);
                return;
            }
            for (int i = d.EmgAttrPublished; i < d.EmgAttrRows.Length; i++)
            {
                EmgAttrRow row = d.EmgAttrRows[i];
                if (row.Key == a.Key && math.all(row.Owners == a.Owners))
                {
                    row.Damage += damage;
                    d.EmgAttrRows[i] = row;
                    return;
                }
            }
            d.EmgAttrRows.Add(new EmgAttrRow { Key = a.Key, Owners = a.Owners, Damage = damage });
        }

        /// <summary>己方单位被己方留下的东西伤到（FGR-EMG-184）：同一“受伤者 + 留下者 + 来源”一步只记一条（持续接触按节拍扣血，不能刷列表）；因果链记“友伤”。
        /// 留下者优先取还在场的己方单位；已不在场的（可能阵亡了）交给热更层按机器登记判断；只有敌方单位的归属不算友伤。</summary>
        private static void EmgFriendlyNote(ref CombatData d, int victimId, in EmgAttrScope a)
        {
            if (a.Friendly == EmgFriendlyCode.None)
            {
                return;
            }
            int pick = 0, gone = 0;
            for (int q = 0; q < 4 && pick == 0; q++)
            {
                int id = a.Owners[q];
                if (id == 0)
                {
                    continue;
                }
                int slot = d.SlotOf(id);
                if (slot < 0)
                {
                    gone = gone != 0 ? gone : id;
                }
                else if (d.Faction[slot] == (byte)CombatFaction.Player)
                {
                    pick = id;
                }
            }
            pick = pick != 0 ? pick : gone;
            if (pick == 0)
            {
                return;
            }
            var row = new int3(victimId, pick, a.Friendly);
            for (int i = d.EmgFriendlyPublished; i < d.EmgFriendly.Length; i++)
            {
                if (math.all(d.EmgFriendly[i] == row))
                {
                    return;
                }
            }
            d.EmgFriendly.Add(row);
            // shortcut: 不在场的归属（pick == gone）内核分不清敌我，不写“友伤”节点，由热更层按机器登记决定发不发通知；机器注销时登记一起清掉，所以少的节点极少，要补得让行带链号。
            if (pick != gone)
            {
                int slotV = d.SlotOf(victimId);
                EmgChainLog(ref d, EmgChainNode.Friendly, a.Chain, d.EmgCtx.Depth, 0, victimId, slotV >= 0 ? d.Pos[slotV] : double2.zero, pick);
            }
        }

        /// <summary>规则结算里产物的归属：沿用这条规则的归属；不在规则里 = 只有排放者。</summary>
        private static int4 EmgOwnersOfNew(ref CombatData d, int ownerId)
        {
            int4 o = d.EmgRuleKeyNow != 0 ? d.EmgRuleOwnersNow : int4.zero;
            EmgOwnersAdd(ref o, ownerId > 0 ? ownerId : 0);
            return o;
        }

        /// <summary>两份归属按“先 a 后 b”合并（ADR ⑨：最多 4 个，满了保留最早 3 个 + 最近 1 个）。</summary>
        internal static int4 EmgOwnersMerge(in int4 a, in int4 b)
        {
            int4 o = a;
            for (int q = 0; q < 4; q++)
            {
                EmgOwnersAdd(ref o, b[q]);
            }
            return o;
        }

        /// <summary>
        /// 一条链上参与的单位（ADR ⑨ 归属，最多 4 个：按出现先后，超过时保留最早 3 个 + 最近 1 个）：从旧到新扫环（O(环长)）；本链及以它为父链的记录里的源单位 / 第二单位都算（破片的打击者、残骸的击毁者）。
        /// <paramref name="key"/> = 这条链最近一次规则结算的稳定键（没有 0）。
        /// </summary>
        internal static int4 EmgChainAttr(ref CombatData d, int chain, out int key)
        {
            key = 0;
            int4 o = int4.zero;
            int n = d.EmgRingHead[1];
            for (int i = n - 1; i >= 0 && chain > 0; i--)
            {
                EmgChainRec r = d.EmgRing[(d.EmgRingHead[0] - 1 - i) & (EmgConst.ChainRing - 1)];
                if (r.Chain != chain && r.Parent != chain)
                {
                    continue;
                }
                if (r.Kind == EmgChainNode.Rule)
                {
                    key = r.Key;
                }
                // 残骸的源单位是被击毁者、友伤节点的源单位是受伤者、相态节点的源单位是进入相态的单位：都不是归属，归属在第二单位
                if (r.Kind == EmgChainNode.Wreck || r.Kind == EmgChainNode.Friendly || r.Kind == EmgChainNode.Phase)
                {
                    EmgOwnersAdd(ref o, r.Other);
                }
                else
                {
                    EmgOwnersAdd(ref o, r.SrcUnit);
                }
            }
            return o;
        }
    }
}
