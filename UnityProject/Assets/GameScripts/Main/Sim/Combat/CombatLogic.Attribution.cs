using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG11-ATTR-01（ADR-EMG-001 ⑨ 更正；FGR-EMG-182 按推荐默认执行（2026-11-12），待用户确认）：同一产物的归属——链上最多 8 个单位 ID，按出现先后（前 4 格 Lo、后 4 格 Hi，连续填）。
    /// 满 8 格时先挤掉在场的非己方机器（敌方 / 炮塔 / 结构，它们本来就不分交互伤害），再挤最早的己方；超过 8 台机器按 DEBT-EMG2DISC01-06。
    /// </summary>
    public struct EmgOwners
    {
        public const int Cap = 8;
        public int4 Lo;
        public int4 Hi;

        public int this[int q]
        {
            get => q < 4 ? Lo[q] : Hi[q - 4];
            set
            {
                if (q < 4)
                {
                    Lo[q] = value;
                }
                else
                {
                    Hi[q - 4] = value;
                }
            }
        }

        public bool IsEmpty => math.all(Lo == 0) && math.all(Hi == 0);

        public bool Has(int id) => id != 0 && (math.any(Lo == id) || math.any(Hi == id));

        /// <summary>已填的格数（连续填，第一个 0 之前）。</summary>
        public int Count
        {
            get
            {
                int n = 0;
                while (n < Cap && this[n] != 0)
                {
                    n++;
                }
                return n;
            }
        }

        public bool Same(in EmgOwners o) => math.all(Lo == o.Lo) && math.all(Hi == o.Hi);

        public static EmgOwners Of(int a, int b = 0, int c = 0, int d = 0) => new EmgOwners { Lo = new int4(a, b, c, d) };

        public override string ToString() => $"o({Lo.x},{Lo.y},{Lo.z},{Lo.w},{Hi.x},{Hi.y},{Hi.z},{Hi.w})";
    }

    /// <summary>EMG2-DISC-01：本步敌方挨的一条交互伤害（已按“规则键 + 归属”合并）。Key = 0 = 没有规则键（排放的物质、相态持续伤害、无规则节点的破片）。</summary>
    public struct EmgAttrRow
    {
        public int Key;
        public EmgOwners Owners;
        public double Damage;
    }

    /// <summary>EMG2-DISC-01：下一次 DamageUnit 属于哪条交互（瞬时量，不进快照）。FG11-ATTR-01：<see cref="Share"/> = 这次伤害里算交互伤害的比例（状态持续伤害按斑块挂上的位占比）。</summary>
    public struct EmgAttrScope
    {
        public byte On;
        public int Key;
        public int Friendly;
        public int Chain;
        public EmgOwners Owners;
        public float Share;
    }

    /// <summary>
    /// FG11-ATTR-01 ②（DEBT-EMG2DISC01-01）：单位身上一个沾染位最后一次由斑块挂上时，那块斑块的归因（规则键、归属、物质码、链号）。
    /// 稀疏表（只有被斑块沾上的单位 × 位才有一行，不进 EmgUnitState）；位到期 / 被清除 / 单位阵亡后删行（<see cref="CombatLogic.StepCoatAttr"/>），别的来源再挂同一位时删行（最后挂上的为准）。进快照段 37。
    /// </summary>
    public struct EmgCoatAttr
    {
        public int Unit;
        public int Key;
        public int Matter;
        public int Chain;
        public EmgOwners Owners;
        public byte Bit;
    }

    /// <summary>FG11-ATTR-01 ④（DEBT-EMG2DISC01-05③）：持久“链号 → 最近规则键 + 链上归属”（写环时同步登记，环被覆盖 / 读档后 EmgChainAttr 查它）。进快照段 38。</summary>
    public struct EmgChainOwn
    {
        public int Chain;
        public int Key;
        public EmgOwners Owners;
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
    /// 进入统一扣血入口后取走：打敌方记一条交互伤害（按规则键 + 归属合并），打己方且归属里有己方单位记一条友伤。全部 O(本步不同组合数)，只在内核；伤害行不进快照、不进状态哈希。
    /// FG11-ATTR-01：只改统计归因，不改血量、胜负与任何玩法结果——这里没有一处改伤害数值、扣血次数或结算顺序。
    /// </summary>
    public static partial class CombatLogic
    {
        internal static void EmgAttrBegin(ref CombatData d, int key, in EmgOwners owners, int friendly, int chain) =>
            d.EmgAttr = new EmgAttrScope { On = 1, Key = key, Owners = owners, Friendly = friendly, Chain = chain, Share = 1f };

        /// <summary>
        /// FG11-ATTR-01 ②（DEBT-EMG2DISC01-01，取代接触节拍估算）：StepStatus 这一拍状态持续伤害扣血前调用。单位身上的持续伤害 = 各持续伤害位（每秒 × 层数）取大，
        /// 所以这一拍算交互伤害的份额 = 由斑块挂上的持续伤害位里最大的那份 ÷ 总的每秒伤害（不超过 1），归属取那一位的斑块归因。离开斑块后残留的秒数照算、两块斑块沾同一位只有一行（只记一次）。
        /// O(沾染来源表)，表空时一次长度比较。
        /// </summary>
        internal static void EmgCoatDotBegin(ref CombatData d, int k)
        {
            if (d.CoatAttr.Length == 0 || !(d.StatusDps[k] > 0f))
            {
                return;
            }
            int unitId = d.Id[k];
            uint status = d.Status[k];
            ulong stacks = d.StatusStacks[k];
            float best = 0f;
            int bestRow = -1;
            for (int i = 0; i < d.CoatAttr.Length; i++)
            {
                EmgCoatAttr a = d.CoatAttr[i];
                if (a.Unit != unitId || (status & (1u << a.Bit)) == 0u)
                {
                    continue;
                }
                CombatStatusFx fx = d.StatusFx[a.Bit];
                float dps = fx.Effect == CombatStatusFx.Dot ? fx.Amount * math.max(1, (int)((stacks >> (a.Bit * 2)) & 3UL)) : 0f;
                if (dps > best)
                {
                    best = dps;
                    bestRow = i;
                }
            }
            if (bestRow < 0)
            {
                return;
            }
            EmgCoatAttr r = d.CoatAttr[bestRow];
            EmgAttrBegin(ref d, r.Key, r.Owners, r.Matter, r.Chain);
            d.EmgAttr.Share = math.min(1f, best / d.StatusDps[k]);
        }

        /// <summary>沾染来源表里（单位 Id, 位）的下标；没有返回 −1。</summary>
        private static int EmgCoatAttrOf(ref CombatData d, int unitId, int bit)
        {
            for (int i = 0; i < d.CoatAttr.Length; i++)
            {
                if (d.CoatAttr[i].Unit == unitId && d.CoatAttr[i].Bit == bit)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// ApplyStatus 挂上 <paramref name="bits"/> 之后：这一次来自斑块（<see cref="CombatData.EmgCoatIn"/> 开着）→ 按（单位, 位）登记 / 覆盖那块斑块的归因；
        /// 别的来源（武器、规则附带）→ 删掉这几位的旧行（最后挂上的为准）。O(沾染来源表)，表空且不是斑块时一次比较。
        /// </summary>
        internal static void EmgCoatAttrNote(ref CombatData d, int k, uint bits)
        {
            bits &= ~CombatConst.StatusBitZoneSlow;
            if (bits == 0u || (d.EmgCoatIn.On == 0 && d.CoatAttr.Length == 0))
            {
                return;
            }
            int unitId = d.Id[k];
            while (bits != 0u)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1u;
                int at = EmgCoatAttrOf(ref d, unitId, b);
                if (d.EmgCoatIn.On != 0)
                {
                    var row = new EmgCoatAttr { Unit = unitId, Bit = (byte)b, Key = d.EmgCoatIn.Key, Owners = d.EmgCoatIn.Owners, Matter = d.EmgCoatIn.Friendly, Chain = d.EmgCoatIn.Chain };
                    if (at >= 0)
                    {
                        d.CoatAttr[at] = row;
                    }
                    else
                    {
                        d.CoatAttr.Add(row);
                    }
                }
                else if (at >= 0)
                {
                    d.CoatAttr.RemoveAtSwapBack(at);
                }
            }
        }

        /// <summary>
        /// 一个沾染位此刻的全部来源（③ 单位层反应用）：斑块挂上的取那块斑块的归属（多台车共有的斑块全部算），否则取挂它的单位（<see cref="CombatData.StatusBitSource"/>）。
        /// 都是持久数据（进快照），读档前后一致。
        /// </summary>
        private static void EmgCoatSourcesAdd(ref CombatData d, int u, int bit, ref EmgOwners o)
        {
            if (bit < 0 || bit >= CombatConst.StatusBitStride || (d.Status[u] & (1u << bit)) == 0u)
            {
                return;
            }
            int at = d.CoatAttr.Length > 0 ? EmgCoatAttrOf(ref d, d.Id[u], bit) : -1;
            if (at >= 0)
            {
                EmgOwners src = d.CoatAttr[at].Owners;
                for (int q = 0; q < EmgOwners.Cap; q++)
                {
                    EmgOwnersAdd(ref d, ref o, src[q]);
                }
                return;
            }
            EmgOwnersAdd(ref d, ref o, d.StatusBitSource[u * CombatConst.StatusBitStride + bit]);
        }

        /// <summary>每步：删掉单位已不在场 / 阵亡、或那一位已不在身上（到期、被消耗、被清除）的沾染来源行。O(沾染来源表)，表空时一次比较。</summary>
        internal static void StepCoatAttr(ref CombatData d)
        {
            for (int i = d.CoatAttr.Length - 1; i >= 0; i--)
            {
                EmgCoatAttr a = d.CoatAttr[i];
                int u = d.SlotOf(a.Unit);
                if (u < 0 || !d.IsAlive(u) || (d.Status[u] & (1u << a.Bit)) == 0u)
                {
                    d.CoatAttr.RemoveAt(i); // 保序删除：下标顺序进快照与哈希，连续跑与读档后一致
                }
            }
        }

        /// <summary>FG11-ATTR-01 ③：挂上沾染位的来源单位（布局同 StatusBitUntil：单位 × 32 位）；位到期 / 被清时归 0。</summary>
        internal static void EmgBitSourceClear(ref CombatData d, int k, uint bits)
        {
            int baseIdx = k * CombatConst.StatusBitStride;
            while (bits != 0u)
            {
                int b = math.tzcnt(bits);
                bits &= bits - 1u;
                d.StatusBitSource[baseIdx + b] = 0;
            }
        }

        // ─────────────────────────────── 相态来源（FG11-ATTR-01 ① / ④）───────────────────────────────

        /// <summary>单位 <paramref name="u"/> 第 <paramref name="k"/> 个相态（含累积阶段）的来源单位 Id（0 = 来源不明：环境 / 斑块没有归属）。</summary>
        internal static int EmgPhaseSrcOf(ref CombatData d, int u, int k) => d.EmgPhaseSrc[u * EmgPhaseConst.Count + k];

        internal static void EmgPhaseSrcPut(ref CombatData d, int u, int k, int id) => d.EmgPhaseSrc[u * EmgPhaseConst.Count + k] = id;

        /// <summary>
        /// 站在斑块里（接触节拍）：这块斑块会给哪个相态加累积量（液氮 → 寒量、酸 / 污泥 → 腐蚀度、明火 → 着火度），就把斑块的放出者记成那个相态累积阶段的来源
        /// （PhaseContact 每步按脚下物质加累积量时不知道是哪块斑块，DEBT-EMG1CHAIN01-04“能查脚下斑块就查”）。已在相态里的不改（相态归进入它的来源）。
        /// </summary>
        private static void EmgPhaseSrcFromPatch(ref CombatData d, int u, in CombatZone z)
        {
            int k = z.Matter switch
            {
                EmgMatterConst.Cryo => EmgPhaseConst.Chill,
                EmgMatterConst.Acid => EmgPhaseConst.Corrode,
                EmgMatterConst.Sludge => EmgPhaseConst.Corrode,
                EmgMatterConst.Fire => EmgPhaseConst.Ignite,
                _ => -1,
            };
            int owner = z.Owners[0] != 0 ? z.Owners[0] : (z.Owner > 0 ? z.Owner : 0);
            if (k < 0 || owner == 0 || !d.EmgPhaseSrc.IsCreated || (d.EmgUnit[u].PhaseMask & (1 << k)) != 0)
            {
                return;
            }
            EmgPhaseSrcPut(ref d, u, k, owner);
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
            double part = damage * (double)a.Share;
            if (!(part > 0.0))
            {
                return;
            }
            for (int i = d.EmgAttrPublished; i < d.EmgAttrRows.Length; i++)
            {
                EmgAttrRow row = d.EmgAttrRows[i];
                if (row.Key == a.Key && row.Owners.Same(a.Owners))
                {
                    row.Damage += part;
                    d.EmgAttrRows[i] = row;
                    return;
                }
            }
            d.EmgAttrRows.Add(new EmgAttrRow { Key = a.Key, Owners = a.Owners, Damage = part });
        }

        /// <summary>破片命中：归属取破片所在因果链上的单位（规则键取链上最近的规则节点；环里没有时查持久链归属表）；都没有时归放出它的单位。己方阵营放出的破片打同阵营走 EmgFriendlyHit，这里不重复记。</summary>
        internal static void EmgAttrFragment(ref CombatData d, in CombatProjectile pr)
        {
            EmgOwners owners = EmgChainAttr(ref d, pr.Chain, out int key);
            if (owners.IsEmpty)
            {
                EmgOwnersAdd(ref d, ref owners, pr.Owner);
            }
            EmgAttrBegin(ref d, key, owners, pr.Faction == CombatFaction.Player ? EmgFriendlyCode.None : EmgFriendlyCode.Fragment, pr.Chain);
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
            for (int q = 0; q < EmgOwners.Cap && pick == 0; q++)
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
        private static EmgOwners EmgOwnersOfNew(ref CombatData d, int ownerId)
        {
            EmgOwners o = d.EmgRuleKeyNow != 0 ? d.EmgRuleOwnersNow : default;
            EmgOwnersAdd(ref d, ref o, ownerId > 0 ? ownerId : 0);
            return o;
        }

        /// <summary>
        /// FG11-ATTR-01（ADR ⑨ 更正）：归属里加一个单位——已在里面的不重复、0 不记、按出现先后连续填满 8 格。满了：新来的是在场的非己方机器（敌方 / 炮塔 / 结构）就不进；
        /// 否则先挤掉最早一个在场的非己方机器，没有就挤掉最早的那个（其后的前移，新来的放最后）。不在场的单位（已阵亡 / 撤离的机器）按己方留着——它们的伤害热更层仍按机器登记分。
        /// </summary>
        public static void EmgOwnersAdd(ref CombatData d, ref EmgOwners owners, int id)
        {
            if (id == 0 || owners.Has(id))
            {
                return;
            }
            int n = owners.Count;
            if (n < EmgOwners.Cap)
            {
                owners[n] = id;
                return;
            }
            if (!EmgOwnerCredited(ref d, id))
            {
                return;
            }
            int drop = 0;
            for (int q = 0; q < EmgOwners.Cap; q++)
            {
                if (!EmgOwnerCredited(ref d, owners[q]))
                {
                    drop = q;
                    break;
                }
            }
            for (int q = drop; q < EmgOwners.Cap - 1; q++)
            {
                owners[q] = owners[q + 1];
            }
            owners[EmgOwners.Cap - 1] = id;
        }

        /// <summary>可能分到交互伤害的单位：在场的己方机器，或已不在场（内核分不清，按己方留着）。</summary>
        private static bool EmgOwnerCredited(ref CombatData d, int id)
        {
            int s = d.SlotOf(id);
            return s < 0 || (d.Faction[s] == (byte)CombatFaction.Player && d.Kind[s] == (byte)CombatUnitKind.Machine);
        }

        /// <summary>两份归属按“先 a 后 b”合并（ADR ⑨ 更正：最多 8 个，满了按 <see cref="EmgOwnersAdd"/> 挤出）。</summary>
        internal static EmgOwners EmgOwnersMerge(ref CombatData d, in EmgOwners a, in EmgOwners b)
        {
            EmgOwners o = a;
            for (int q = 0; q < EmgOwners.Cap; q++)
            {
                EmgOwnersAdd(ref d, ref o, b[q]);
            }
            return o;
        }

        /// <summary>
        /// 一条链上参与的单位（ADR ⑨ 归属，最多 8 个，按出现先后）：从旧到新扫环（O(环长)）；本链及以它为父链的记录里的源单位 / 第二单位都算（破片的打击者、残骸的击毁者）。
        /// <paramref name="key"/> = 这条链最近一次规则结算的稳定键（没有 0）。FG11-ATTR-01 ④：环里找不到这条链（被覆盖 / 刚读档）时查持久链归属表（<see cref="EmgChainOwnOf"/>）。
        /// </summary>
        internal static EmgOwners EmgChainAttr(ref CombatData d, int chain, out int key)
        {
            key = 0;
            EmgOwners o = default;
            bool found = false;
            int n = d.EmgRingHead[1];
            for (int i = n - 1; i >= 0 && chain > 0; i--)
            {
                EmgChainRec r = d.EmgRing[(d.EmgRingHead[0] - 1 - i) & (EmgConst.ChainRing - 1)];
                if (r.Chain != chain && r.Parent != chain)
                {
                    continue;
                }
                found = true;
                if (r.Kind == EmgChainNode.Rule)
                {
                    key = r.Key;
                }
                EmgOwnersAdd(ref d, ref o, EmgChainRecOwner(r));
            }
            if ((!found || key == 0) && chain > 0)
            {
                int at = EmgChainOwnOf(ref d, chain);
                if (at >= 0 && (!found || d.EmgChainOwns[at].Key != 0))
                {
                    // 环里只剩这条链的后段（规则节点与最早的出手者已被挤掉）：键取表里的，归属表在前、环在后合并（FG11-ATTR-01 审查修复）
                    key = d.EmgChainOwns[at].Key;
                    o = EmgOwnersMerge(ref d, d.EmgChainOwns[at].Owners, o);
                }
            }
            return o;
        }

        /// <summary>残骸的源单位是被击毁者、友伤节点的源单位是受伤者、相态节点的源单位是进入相态的单位：都不是归属，归属在第二单位。</summary>
        private static int EmgChainRecOwner(in EmgChainRec r) =>
            r.Kind == EmgChainNode.Wreck || r.Kind == EmgChainNode.Friendly || r.Kind == EmgChainNode.Phase ? r.Other : r.SrcUnit;

        /// <summary>持久链归属表里链号 <paramref name="chain"/> 的下标（直映射：链号 &amp; (表长 − 1)，格里是别的链返回 −1）。</summary>
        internal static int EmgChainOwnOf(ref CombatData d, int chain)
        {
            if (chain <= 0 || !d.EmgChainOwns.IsCreated)
            {
                return -1;
            }
            int at = chain & (EmgConst.ChainOwnSlots - 1);
            return d.EmgChainOwns[at].Chain == chain ? at : -1;
        }

        /// <summary>
        /// 写环时同步登记持久链归属（EmgChainLog 调用）：与 EmgChainAttr 扫环同一口径——记录归它自己的链与父链，规则节点更新“最近规则键”。
        /// 直映射：格里是更早的链就换成这条（链号单调递增）；是更新的链就不写（较早的链已被挤出，读它时退回放出者，表长 512 条链 ≥ 环 256 条记录覆盖的链数）。O(1)。
        /// </summary>
        private static void EmgChainOwnNote(ref CombatData d, int chain, EmgChainNode kind, int key, int owner)
        {
            if (chain <= 0 || !d.EmgChainOwns.IsCreated)
            {
                return;
            }
            int at = chain & (EmgConst.ChainOwnSlots - 1);
            EmgChainOwn c = d.EmgChainOwns[at];
            if (c.Chain != chain)
            {
                if (c.Chain > chain)
                {
                    return;
                }
                c = new EmgChainOwn { Chain = chain };
            }
            if (kind == EmgChainNode.Rule)
            {
                c.Key = key;
            }
            EmgOwnersAdd(ref d, ref c.Owners, owner);
            d.EmgChainOwns[at] = c;
        }
    }
}
