using System.IO;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-CHAIN-01（FG19 FGR-EMG-067 / 180；ADR-EMG-001 ⑨）：因果链环与可靠计数的托管接口与快照段（Emg 段 15 规则计数、段 16 端口计数）。
    /// 环不进快照、不进状态哈希；可靠计数进快照（随 Emg 段哈希）。热更层：每步只读脏列表（O(本步变化的槽)），打开因果链叠加 / 悬停时才读环（O(环长)）。
    /// </summary>
    public sealed partial class CombatKernel
    {
        /// <summary>上一步结束时脏列表交出去了几条（下一步开始时去掉这几条；两步之间的即时调用产生的留到下一步一起交）。</summary>
        private int _dirtyPublished;

        private void EmgDirtyRetire()
        {
            int n = _d.EmgDirty.Length;
            int cut = System.Math.Min(_dirtyPublished, n);
            if (cut > 0)
            {
                _d.EmgDirty.RemoveRange(0, cut); // 去重标记已在交出时清掉：两步之间又命中的槽重新追加在未交出部分
            }
            _dirtyPublished = 0;
            int cutEra = System.Math.Min(_d.EmgEraDirtyPublished, _d.EmgEraDirty.Length);
            if (cutEra > 0)
            {
                _d.EmgEraDirty.RemoveRange(0, cutEra); // EMG1-FW-01：反应装甲块数脏列表同一口径
            }
            _d.EmgEraDirtyPublished = 0;
            int cutFf = System.Math.Min(_d.EmgFriendlyPublished, _d.EmgFriendly.Length);
            if (cutFf > 0)
            {
                _d.EmgFriendly.RemoveRange(0, cutFf); // 友伤命中列表同一口径
            }
            _d.EmgFriendlyPublished = 0;
        }

        /// <summary>交出本步脏列表并清掉这些槽的去重标记（修复：上一步刚交出的槽在两步之间又被即时出手命中时，要能再进下一步的列表）。热更层按累计差值读，同槽重复出现无害。</summary>
        private void EmgDirtyPublish()
        {
            int n = _d.EmgDirty.Length;
            for (int i = 0; i < n; i++)
            {
                _d.EmgDirtyMark[_d.EmgDirty[i]] = 0;
            }
            _dirtyPublished = n;
            _d.EmgEraDirtyPublished = _d.EmgEraDirty.Length;
            _d.EmgFriendlyPublished = _d.EmgFriendly.Length;
        }

        /// <summary>EMG1-FW-01（FGR-EMG-233 第 6 条）：本步爆炸反应装甲块数有变化的单位（热更层只读这个列表更新块数刻度 / 耗尽通知，O(本步变化数)）。</summary>
        public int EmgEraDirtyCount => _d.EmgEraDirty.Length;

        /// <summary>
        /// 自检：在“攻击者 <paramref name="attackerId"/> 的程序出手、链深 <paramref name="depth"/>”的上下文里让宿主吃一次即时命中后的起爆判定（走与 ReflectMelee 同一个起爆入口）。
        /// 只用来覆盖“链深已到 3：扣块与反伤照常、不放破片”这一分支（正式流程里即时命中都在出手根上，链深 0）。
        /// </summary>
        public void EraDetonateForTests(int attackerId, int hostId, float reflect, byte depth)
        {
            int a = _d.SlotOf(attackerId), t = _d.SlotOf(hostId);
            if (a < 0 || t < 0 || !CombatLogic.EmgEraArmed(ref _d, t))
            {
                return;
            }
            EmgCtx saved = _d.EmgCtx;
            EmgScalars es = _d.EmgS[0];
            int chain = es.NextChain++;
            _d.EmgS[0] = es;
            _d.EmgCtx = new EmgCtx { Prog = 0, Chain = chain, Owner = a, Depth = depth };
            CombatLogic.EmgEraDetonate(ref _d, a, t, reflect);
            _d.EmgCtx = saved;
            Touch();
        }

        /// <summary>EMG1-FW-01（FGR-EMG-184）：本步破片误伤同阵营单位的命中（x = 受伤单位 ID，y = 破片归属单位 ID）。O(本步友伤数)。</summary>
        public int EmgFriendlyCount => _d.EmgFriendly.Length;

        public int2 EmgFriendlyAt(int i) => i >= 0 && i < _d.EmgFriendly.Length ? _d.EmgFriendly[i] : default;

        public int EmgEraDirtyAt(int i) => i >= 0 && i < _d.EmgEraDirty.Length ? _d.EmgEraDirty[i] : 0;

        /// <summary>
        /// EMG1-FW-01：单位的爆炸反应装甲读数——完整块数 / 总块数 / 缺块掩码 / 下一块还要几秒（满块 0）。块数机制关（emg.stage = 0）或单位没有反伤读法时返回 false。O(1)。
        /// </summary>
        public bool TryGetEra(int unitId, out int intact, out int total, out ushort missMask, out float nextIn)
        {
            intact = total = 0;
            missMask = 0;
            nextIn = 0f;
            int i = _d.SlotOf(unitId);
            if (i < 0 || !CombatLogic.EmgEraArmed(ref _d, i))
            {
                return false;
            }
            total = CombatLogic.EmgEraN(ref _d);
            missMask = (ushort)(_d.EmgUnit[i].EraMiss & ((1 << total) - 1));
            intact = total - math.countbits((int)missMask);
            float period = CombatLogic.EmgEraPeriod(ref _d);
            nextIn = missMask == 0 ? 0f : math.max(0f, period - _d.EmgUnit[i].EraRefillT);
            return true;
        }

        // ─────────────────────────────── 环 ───────────────────────────────

        /// <summary>环里现有几条（≤ <see cref="EmgConst.ChainRing"/>）。</summary>
        public int EmgChainRecordCount => _d.EmgRingHead[1];

        /// <summary>第 <paramref name="i"/> 新的一条（0 = 最新）。O(1)。</summary>
        public bool TryGetEmgChainRecent(int i, out EmgChainRec rec)
        {
            int n = _d.EmgRingHead[1];
            if (i < 0 || i >= n)
            {
                rec = default;
                return false;
            }
            rec = _d.EmgRing[(_d.EmgRingHead[0] - 1 - i) & (EmgConst.ChainRing - 1)];
            return true;
        }

        /// <summary>
        /// 一条链上参与的单位（ADR ⑨ 归属，最多 4 个：按出现先后，超过时保留最早 3 个 + 最近 1 个）：从旧到新扫环（O(环长)，只在悬停 / 打开叠加时调），
        /// 本链及以它为父链的记录里的源单位 / 第二单位都算（破片的打击者、残骸的击毁者）。
        /// </summary>
        public int4 EmgChainOwners(int chain)
        {
            int4 o = int4.zero;
            int n = _d.EmgRingHead[1];
            for (int i = n - 1; i >= 0 && chain > 0; i--)
            {
                EmgChainRec r = _d.EmgRing[(_d.EmgRingHead[0] - 1 - i) & (EmgConst.ChainRing - 1)];
                if (r.Chain != chain && r.Parent != chain)
                {
                    continue;
                }
                bool destroyed = r.Kind == EmgChainNode.Wreck; // 残骸的源单位是被击毁者，不是归属
                if (!destroyed)
                {
                    CombatLogic.EmgOwnersAdd(ref o, r.SrcUnit);
                }
                if (r.Kind == EmgChainNode.Wreck)
                {
                    CombatLogic.EmgOwnersAdd(ref o, r.Other);
                }
            }
            return o;
        }

        /// <summary>一块残骸的归属（ADR ⑨ 4 个规则）：持久的击毁者（<see cref="EmgWreck.Killer"/>，环被覆盖 / 读档后仍在）+ 之后打碎它的单位（环里 Key = 残骸 Id 的破片记录，从旧到新）。O(残骸数 + 环长)，只在悬停时调。</summary>
        public int4 EmgWreckOwners(int wreckId)
        {
            int4 o = int4.zero;
            int wi = WreckIndexOf(wreckId);
            if (wi >= 0)
            {
                CombatLogic.EmgOwnersAdd(ref o, _d.Wrecks[wi].Killer);
            }
            int n = _d.EmgRingHead[1];
            for (int i = n - 1; i >= 0 && wreckId > 0; i--)
            {
                EmgChainRec r = _d.EmgRing[(_d.EmgRingHead[0] - 1 - i) & (EmgConst.ChainRing - 1)];
                if (r.Key != wreckId)
                {
                    continue;
                }
                if (r.Kind == EmgChainNode.Wreck)
                {
                    CombatLogic.EmgOwnersAdd(ref o, r.Other);
                }
                else if (r.Kind == EmgChainNode.Fragment)
                {
                    CombatLogic.EmgOwnersAdd(ref o, r.SrcUnit);
                }
            }
            return o;
        }

        /// <summary>测试 / 调试：直接写一条环记录（与内核写入同一函数）。</summary>
        public void EmgChainLogForTests(EmgChainNode kind, int chain, int srcUnit, int other = 0, int parent = 0, int key = 0) =>
            CombatLogic.EmgChainLog(ref _d, kind, chain, 0, key, srcUnit, double2.zero, other, 0, parent);

        // ─────────────────────────────── 悬停拾取（FGR-EMG-181；逐实体扫描在内核，热更层只拼一件的文字）───────────────────────────────

        /// <summary>离 <paramref name="at"/> 最近、圆心距 ≤ 残骸半径 + <paramref name="radius"/> 的残骸下标（没有 −1）。O(残骸数)。</summary>
        public int PickWreck(double2 at, float radius)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < _d.Wrecks.Length; i++)
            {
                EmgWreck w = _d.Wrecks[i];
                double dist = math.distance(w.Pos, at);
                if (dist <= w.Radius + radius && dist < bd)
                {
                    bd = dist;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>离 <paramref name="at"/> 最近的子弹药 / 破片（在飞、Flags 带 Sub 或 Fragment）的弹体下标（没有 −1）。O(弹体数)。</summary>
        public int PickEmgProjectile(double2 at, float radius)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < _d.Projectiles.Length; i++)
            {
                CombatProjectile p = _d.Projectiles[i];
                if ((p.Flags & (EmgProjFlags.Sub | EmgProjFlags.Fragment)) == 0 || !(p.Life > 0f))
                {
                    continue;
                }
                double dist = math.distance(p.Pos, at);
                if (dist <= p.Radius + radius && dist < bd)
                {
                    bd = dist;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>稳定 Id → 当前下标（残骸 / 弹体下标会因压实变；悬停按 Id 跟住同一件）。没有 −1。O(件数)，在内核里扫。</summary>
        public int WreckIndexOf(int id)
        {
            for (int i = 0; id > 0 && i < _d.Wrecks.Length; i++)
            {
                if (_d.Wrecks[i].Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        public int ProjectileIndexOf(int id)
        {
            for (int i = 0; id > 0 && i < _d.Projectiles.Length; i++)
            {
                if (_d.Projectiles[i].Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        // ─────────────────────────────── 可靠计数 ───────────────────────────────

        /// <summary>规则计数槽：稳定键（0 = 空槽）、累计次数、最后一次的位置 / 来源 / 目标（进给，不进快照）。</summary>
        public int EmgRuleKeyAt(int slot) => slot >= 0 && slot < EmgConst.RuleSlots ? _d.EmgRuleKey[slot] : 0;

        public int EmgRuleCountAt(int slot) => slot >= 0 && slot < EmgConst.RuleSlots ? _d.EmgRuleCount[slot] : 0;

        public EmgRuleFeed EmgRuleFeedAt(int slot) => slot >= 0 && slot < EmgConst.RuleSlots ? _d.EmgRuleFeed[slot] : default;

        /// <summary>按稳定键查累计次数（没有 0）。O(槽数)，只给自检 / 面板用。</summary>
        public int EmgRuleCountOfKey(int key)
        {
            for (int i = 0; key != 0 && i < EmgConst.RuleSlots; i++)
            {
                if (_d.EmgRuleKey[i] == key)
                {
                    return _d.EmgRuleCount[i];
                }
            }
            return 0;
        }

        /// <summary>本步有变化的规则计数槽（脏列表）：条数与第 <paramref name="i"/> 条的槽下标。热更层每步只读这里。</summary>
        public int EmgDirtyCount => _d.EmgDirty.Length;

        public int EmgDirtyAt(int i) => i >= 0 && i < _d.EmgDirty.Length ? _d.EmgDirty[i] : -1;

        /// <summary>按事件端口的可靠入队计数（含积压合并掉的；进快照）。</summary>
        public long EmgPortCountOf(EmgEventKind kind) => (int)kind > 0 && (int)kind < _d.EmgPortCount.Length ? _d.EmgPortCount[(int)kind] : 0;

        /// <summary>按实体种类的可靠生成计数：各实体的稳定 ID 每地点单调发号（进快照），发过几个 = 下一个号 − 1。</summary>
        public int EmgEntitySpawnedOf(EmgEntKind kind)
        {
            EmgScalars es = _d.EmgS[0];
            switch (kind)
            {
                case EmgEntKind.Projectile: return es.NextProjId - 1;
                case EmgEntKind.Drone: return es.NextDroneId - 1;
                case EmgEntKind.Wreck: return es.NextWreckId - 1;
                case EmgEntKind.Patch: return es.NextPatchId - 1;
                case EmgEntKind.Field: return es.NextFieldId - 1;
                case EmgEntKind.Zone: return es.NextZoneId - 1;
                default: return 0;
            }
        }

        /// <summary>有可靠计数（规则或端口）时为 true：状态哈希据此把 Emg 段混进来（程序被剥离后计数仍要进哈希）。</summary>
        private bool EmgHasCounts()
        {
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                if (_d.EmgRuleKey[i] != 0)
                {
                    return true;
                }
            }
            for (int k = 0; k < _d.EmgPortCount.Length; k++)
            {
                if (_d.EmgPortCount[k] != 0)
                {
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────── 快照（段 15 / 16）───────────────────────────────

        private void WriteChainSegments(BinaryWriter w, EmgSegmentWriter sw)
        {
            int used = 0;
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                if (_d.EmgRuleKey[i] != 0)
                {
                    used++;
                }
            }
            // 一条记录 = 槽下标 + 稳定键 + 次数（槽位本场不挪：evt.react 的 Code 是槽下标，读档后保持同一下标）
            sw.Begin(EmgConst.SegRuleCounts, 3, used);
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                if (_d.EmgRuleKey[i] == 0)
                {
                    continue;
                }
                if (sw.F()) w.Write((short)i);
                if (sw.F()) w.Write(_d.EmgRuleKey[i]);
                if (sw.F()) w.Write(_d.EmgRuleCount[i]);
                sw.EndRecord();
            }
            sw.End();
            sw.Begin(EmgConst.SegPortCounts, 1, _d.EmgPortCount.Length);
            for (int k = 0; k < _d.EmgPortCount.Length; k++)
            {
                if (sw.F()) w.Write(_d.EmgPortCount[k]);
                sw.EndRecord();
            }
            sw.End();
        }

        private static CombatLoadResult ParseRuleCounts(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n < 0 || n > EmgConst.RuleSlots)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int i = 0; i < n; i++)
            {
                sr.Record(i);
                int slot = sr.F() ? r.ReadInt16() : -1;
                int key = sr.F() ? r.ReadInt32() : 0;
                int count = sr.F() ? r.ReadInt32() : 0;
                if (!sr.RecordOk || slot < 0 || slot >= EmgConst.RuleSlots || key == 0 || count < 0 || staging.EmgRuleKey[slot] != 0)
                {
                    return CombatLoadResult.InvalidValue; // 槽越界 / 空键 / 负数 / 同一槽两条：整份拒绝
                }
                staging.EmgRuleKey[slot] = key;
                staging.EmgRuleCount[slot] = count;
            }
            return CombatLoadResult.Ok;
        }

        private static CombatLoadResult ParsePortCounts(BinaryReader r, EmgSegmentReader sr, ref CombatData staging)
        {
            int n = sr.Count;
            if (n < 0 || n > 4096)
            {
                return CombatLoadResult.InvalidValue;
            }
            for (int k = 0; k < n; k++)
            {
                sr.Record(k);
                long c = sr.F() ? r.ReadInt64() : 0;
                if (!sr.RecordOk || c < 0)
                {
                    return CombatLoadResult.InvalidValue;
                }
                if (k < staging.EmgPortCount.Length)
                {
                    staging.EmgPortCount[k] = c; // 以后事件种类变多 / 变少：多出的丢弃、缺的取 0
                }
            }
            return CombatLoadResult.Ok;
        }
    }
}
