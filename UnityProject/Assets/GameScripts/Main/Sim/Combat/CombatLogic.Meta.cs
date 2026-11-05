using System;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG3-META-01 元程序常量（FG19 第 2.4、3.4 节；种类码 = 端口码 − 200）。</summary>
    public static class EmgMetaConst
    {
        public const byte Repeat = 0, Loop = 1, If = 2, Random = 3, Reverse = 4, Defer = 5, Kinds = 6;
        /// <summary>运行态记录的模式：循环（宿主存续期间周期执行）/ 重放（复制：同位置再结算）/ 跟随重放（延后：到点取宿主当时位置，宿主已结束则在结束点）。</summary>
        public const byte RunLoop = 0, RunReplay = 1, RunFollow = 2;
        public const float DefaultGap = 0.1f, DefaultLoopPeriod = 0.5f, DefaultAnchor = 2f, DefaultDefer = 1f, DefaultDeferMax = 3f;
        public const int DefaultMaxExec = 4, DefaultLoopMax = 8;
        /// <summary>只给 &lt; 64 的程序序号做位运算（一把武器 ≤ 4 槽 × 混合 2 条）；序号 ≥ 64 的指令不受元程序约束。</summary>
        public const int MaxOrdinal = 64;

        public static int KindOf(ushort port) => port >= EmgConst.PortMetaBase && port < EmgConst.PortMetaBase + Kinds ? port - EmgConst.PortMetaBase : -1;
    }

    /// <summary>
    /// EMG3-META-01 元程序执行（FG19 FGR-EMG-040～043；ADR-EMG-001 ②⑩⑬）。
    /// “下游段” = 同一节点里程序序号大于元程序的全部非元程序指令（含其中触发器放出的子树，子节点按 <see cref="EmgNode.MetaMask"/> 记的触发器序号判）。
    /// 一次结算（事件入队，<see cref="EmgEmitHosted"/>）的求值顺序：条件 → 随机 → 复制（各层 ×2 相乘、≤ emg.meta.max_exec）→ 延后（每层 emg.meta.defer_seconds）；
    /// 复制 / 延后的再执行、循环的周期执行记进 <see cref="CombatData.MetaRun"/>（快照段 23），到点由 <see cref="StepEmgMeta"/> 按宿主执行。
    /// 反转在编译期出两棵树（程序表相邻两行），开火时按条件读端选用（<see cref="EmgTreeOf"/>）。随机用无状态哈希（因果链、宿主、结算时刻、地点种子）。
    /// 只认端口码与角色，不认任何内容 ID（FGR-EMG-013）。程序没有元程序（<see cref="EmgProgramFlags.HasMeta"/>）时每个入口 O(1) 早退出，行为与哈希逐字不变。
    /// </summary>
    public static partial class CombatLogic
    {
        private const uint SaltMetaRandom = 0x6D52u;

        internal static bool EmgMetaAllowed(ulong allow, byte ordinal) => ordinal >= EmgMetaConst.MaxOrdinal || ((allow >> ordinal) & 1UL) != 0UL;

        private static ulong EmgOrdBit(int o) => o >= 0 && o < EmgMetaConst.MaxOrdinal ? 1UL << o : 0UL;

        /// <summary>序号大于 <paramref name="o"/> 的位（= 元程序 o 的下游段候选）。</summary>
        private static ulong EmgAbove(int o) => o >= EmgMetaConst.MaxOrdinal - 1 ? 0UL : ~0UL << (o + 1);

        internal static bool EmgHasMeta(ref CombatData d, int prog) =>
            prog >= 0 && prog < d.EmgPrograms.Length && (d.EmgPrograms[prog].Flags & EmgProgramFlags.HasMeta) != 0;

        private static bool EmgNodeSpan(ref CombatData d, int prog, int node, out int first, out int end)
        {
            first = end = 0;
            if (prog < 0 || prog >= d.EmgPrograms.Length)
            {
                return false;
            }
            EmgProgram p = d.EmgPrograms[prog];
            if (node < 0 || node >= p.NodeCount || p.FirstNode + node >= d.EmgNodes.Length)
            {
                return false;
            }
            EmgNode n = d.EmgNodes[p.FirstNode + node];
            first = n.FirstInstr;
            end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            return true;
        }

        /// <summary>节点里的执行单位（非元程序指令的序号位）与元程序序号位。溢出行不算。O(节点指令数)。</summary>
        private static ulong EmgMetaScan(ref CombatData d, int first, int end, out ulong metas)
        {
            ulong all = 0UL;
            metas = 0UL;
            for (int i = first; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if ((x.Flags & EmgInstrFlags.Overflow) != 0)
                {
                    continue;
                }
                all |= EmgOrdBit(x.Ordinal);
                if (x.Role > EmgConst.RoleLastRead && EmgMetaConst.KindOf(x.Port) >= 0)
                {
                    metas |= EmgOrdBit(x.Ordinal);
                }
            }
            return all & ~metas;
        }

        /// <summary>序号 <paramref name="ord"/> 的元程序写端行（种类与行下标）；没有返回 −1。</summary>
        private static int EmgMetaRow(ref CombatData d, int first, int end, byte ord, out int kind)
        {
            for (int i = first; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Ordinal == ord && x.Role > EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0 && (kind = EmgMetaConst.KindOf(x.Port)) >= 0)
                {
                    return i;
                }
            }
            kind = -1;
            return -1;
        }

        /// <summary>这条元程序本次结算是否作用：它有事件触发读端时只在那个事件上作用（FG19A N04“命中时”），没有时每次结算都作用。</summary>
        private static bool EmgMetaApplies(ref CombatData d, int first, int end, byte ord, EmgEventKind ev)
        {
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ord && r.Role == EmgConst.RoleTrigger && r.Port > EmgConst.EventPortBase && r.Port <= EmgConst.EventPortBase + EmgConst.EventKinds)
                {
                    return r.Port == EmgConst.EventPortBase + (int)ev;
                }
            }
            return true;
        }

        private static float EmgHeatRatio(ref CombatData d, int i)
        {
            if (i < 0 || i >= d.Count)
            {
                return 0f;
            }
            int w = d.Weapon[i];
            float at = w >= 0 && w < d.Weapons.Length ? d.Weapons[w].OverheatAt : 0f;
            return at > 0f ? d.Heat[i] / at : 0f;
        }

        /// <summary>
        /// 条件读端（role = condition）求值，同一序号多条按“且”（FGR-EMG-040 条件 / 反转；FGR-EMG-043 条件列：对命中目标 / 主目标求值，没有目标时对自身）：
        /// evt.marked 目标被标记；prop.heat 本机武器积热比例 ≥ 幅度；prop.integrity 目标（没有目标 = 自身）剩余耐久比例 &lt; 幅度；phs.* 状态端口 = 处于该相态、累积量端口 = 累积量 &gt; 0 或已在相态；
        /// mat.* 目标或自身站在该物质里；chs.* 本机底盘状态（DEBT-EMG3CHS01-02：元程序的 chs.* 条件对弹体 / 区域宿主同样按出手者求值）。其余端口内核不认 = 假（模糊测试器按死指令报）。
        /// 幅度 ≤ 0 时按 0.5。O(节点指令数)。
        /// 布区宿主（<paramref name="zoneMat"/> ≠ <see cref="EmgNotZone"/>，FGR-EMG-043 条件列“对区域内首个单位 / 区域本身求值”）：目标 = 区域内首个单位（<see cref="EmgZoneSubj"/>），
        /// 没有目标时目标类端口（耐久 / 相态）为假、不退回出手者；mat.* 的“自身” = 区域中心所在的物质位；prop.heat / chs.* 仍按出手者。
        /// </summary>
        internal static bool EmgMetaCond(ref CombatData d, int first, int end, byte ord, int self, int target, uint zoneMat = EmgNotZone)
        {
            double now = d.Scalars[0].Time;
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ord && r.Role == EmgConst.RoleCondition && (r.Flags & EmgInstrFlags.Overflow) == 0 && !EmgCondRow(ref d, r, self, target, now, zoneMat))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>“宿主不是布区”（物质位不可能全 1：物质种类 &lt; 32）。</summary>
        private const uint EmgNotZone = uint.MaxValue;

        private static bool EmgCondRow(ref CombatData d, in EmgInstr r, int self, int target, double now, uint zoneMat)
        {
            bool zone = zoneMat != EmgNotZone;
            bool hasSelf = self >= 0 && self < d.Count;
            bool hasTarget = target >= 0 && target < d.Count;
            int who = hasTarget ? target : zone ? -1 : self;
            bool hasWho = who >= 0 && who < d.Count;
            float amt = r.Amount > 0f ? r.Amount : 0.5f;
            int p = r.Port;
            if (p == EmgConst.EventPortBase + (int)EmgEventKind.Marked)
            {
                return hasTarget && d.MarkedUntil[target] > now;
            }
            if (p == EmgConst.PortPropHeat)
            {
                return hasSelf && EmgHeatRatio(ref d, self) >= amt;
            }
            if (p == EmgConst.PortPropIntegrity)
            {
                return hasWho && d.MaxHp[who] > 0f && d.Hp[who] / d.MaxHp[who] < amt;
            }
            if (p >= EmgConst.PortPhsBase && p < EmgConst.PortPhsBase + 2 * EmgPhaseConst.Count)
            {
                int k = (p - EmgConst.PortPhsBase) >> 1;
                if (!hasWho || !d.EmgUnit.IsCreated || who >= d.EmgUnit.Length)
                {
                    return false;
                }
                if (InPhase(ref d, who, k))
                {
                    return true;
                }
                if (((p - EmgConst.PortPhsBase) & 1) == 1)
                {
                    return false; // 相态端口：只看是否处于相态
                }
                EmgUnitState u = d.EmgUnit[who];
                return (k < 4 ? u.AccA[k] : u.AccB[k - 4]) > 0f;
            }
            byte matter = EmgMatterConst.MatterOfPort(p);
            if (matter != 0)
            {
                uint bit = 1u << matter;
                return EmgStandsIn(ref d, who, bit) || (zone ? (zoneMat & bit) != 0u : EmgStandsIn(ref d, self, bit));
            }
            byte chs = ChsBitOfPort(r.Port);
            if (chs != 0)
            {
                return hasSelf && (ChsMask(ref d, self) & chs) != 0;
            }
            return false;
        }

        private static bool EmgStandsIn(ref CombatData d, int i, uint bit) =>
            i >= 0 && i < d.Count && d.EmgUnit.IsCreated && i < d.EmgUnit.Length && (d.EmgUnit[i].PrevMatterMask & bit) != 0u;

        /// <summary>
        /// 布区宿主的条件求值对象（FGR-EMG-043 条件列“对区域内首个单位 / 区域本身求值”）：<paramref name="target"/> &lt; 0 时取圈内第一个活着的敌对单位（按槽位，确定性）；
        /// 返回区域中心所在的物质位（mat.* 的“区域本身”），<paramref name="center"/> = 区域中心。区域找不到返回 0。O(单位数 + 本格斑块数)，只在带元程序的区域结算 / 落区时。
        /// </summary>
        private static uint EmgZoneSubj(ref CombatData d, int zoneId, ref int target, out double2 center)
        {
            center = default;
            for (int z = 0; z < d.Zones.Length; z++)
            {
                if (d.Zones[z].EmgId == zoneId && d.Zones[z].EmgProgRef > 0)
                {
                    center = d.Zones[z].Pos;
                    return EmgZoneSubjOf(ref d, d.Zones[z], ref target);
                }
            }
            return 0u;
        }

        internal static uint EmgZoneSubjOf(ref CombatData d, in CombatZone zn, ref int target)
        {
            for (int u = 0; target < 0 && u < d.Count; u++)
            {
                if (d.IsAlive(u) && d.Faction[u] != (byte)zn.Faction && math.distance(d.Pos[u], zn.Pos) <= zn.Radius + d.Radius[u])
                {
                    target = u;
                }
            }
            if (d.EmgS[0].PatchLive <= 0 && d.AcidShapes.Length == 0)
            {
                return 0u;
            }
            EmgHashEnsure(ref d);
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            uint mask = PatchesAt(ref d, zn.Pos, false, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
            hits.Dispose();
            return mask;
        }

        private static void EmgMetaSee(ref CombatData d, int kindBits)
        {
            if (kindBits == 0)
            {
                return;
            }
            EmgBusStats st = d.EmgStats[0];
            st.MetaSeen |= kindBits;
            d.EmgStats[0] = st;
        }

        /// <summary>随机元程序的无状态哈希键：因果链、宿主、结算时刻（毫秒）、元程序序号、地点种子（FGR-EMG-040 随机；ADR ⑬ 确定性，不进快照）。</summary>
        private static uint EmgMetaHash(ref CombatData d, uint chain, uint host, int ord, EmgEventKind ev) =>
            EmgRng.Hash(chain, host, SaltMetaRandom + (uint)ord * 131u + (uint)ev * 7919u, d.Config.EmgSiteSeed ^ (uint)(long)math.round(d.Scalars[0].Time * 1000.0));

        /// <summary>
        /// 条件 → 随机（纯函数，同一结算处处一致）：返回下游段仍执行的程序序号位。条件为假关掉它的整个下游段；随机从仍执行的下游单位里按哈希留 1 条。
        /// <paramref name="ev"/> = 结算事件（出手时的修饰判定用 evt.fire）。
        /// </summary>
        private static ulong EmgMetaGate(ref CombatData d, int first, int end, ulong units, ulong metas, EmgEventKind ev, int self, int target, uint keyA, uint keyB, ref int seen,
            uint zoneMat = EmgNotZone)
        {
            ulong allow = ~0UL;
            for (ulong m = metas; m != 0UL; m &= m - 1UL)
            {
                byte o = (byte)math.tzcnt(m);
                if (EmgMetaRow(ref d, first, end, o, out int kind) >= 0 && kind == EmgMetaConst.If && EmgMetaApplies(ref d, first, end, o, ev)
                    && (allow & EmgOrdBit(o)) != 0UL && !EmgMetaCond(ref d, first, end, o, self, target, zoneMat))
                {
                    allow &= ~EmgAbove(o);
                    seen |= 1 << EmgMetaConst.If;
                }
            }
            for (ulong m = metas; m != 0UL; m &= m - 1UL)
            {
                byte o = (byte)math.tzcnt(m);
                if (EmgMetaRow(ref d, first, end, o, out int kind) < 0 || kind != EmgMetaConst.Random || !EmgMetaApplies(ref d, first, end, o, ev) || (allow & EmgOrdBit(o)) == 0UL)
                {
                    continue;
                }
                ulong cand = units & allow & EmgAbove(o);
                int count = math.countbits(cand);
                if (count < 2)
                {
                    continue;
                }
                int pick = (int)(EmgMetaHash(ref d, keyA, keyB, o, ev) % (uint)count);
                ulong keep = cand;
                for (int k = 0; k < pick; k++)
                {
                    keep &= keep - 1UL;
                }
                keep &= ~(keep - 1UL); // 第 pick 个置位
                allow &= ~(cand & ~keep);
                seen |= 1 << EmgMetaConst.Random;
            }
            return allow;
        }

        /// <summary>
        /// 一次结算此刻执行的程序序号位：条件 / 随机之后，再去掉被延后的（到点由运行态执行）与累计延后超过 emg.meta.defer_total_max 的（不执行，编译期在延后行 A = 0 标出、预览写原因）。
        /// 子弹药落点 / 到期的效果（<see cref="EmgNodeEffects"/>）与入队时的执行（<see cref="EmgMetaSettle"/>）用同一个函数，同一次结算两处一致。
        /// </summary>
        internal static ulong EmgMetaAllowNow(ref CombatData d, int prog, int node, EmgEventKind ev, int self, int target, int chain, int hostEnt, uint zoneMat = EmgNotZone)
        {
            if (!EmgHasMeta(ref d, prog) || !EmgNodeSpan(ref d, prog, node, out int first, out int end))
            {
                return ulong.MaxValue;
            }
            ulong units = EmgMetaScan(ref d, first, end, out ulong metas);
            if (metas == 0UL)
            {
                return ulong.MaxValue;
            }
            int seen = 0;
            ulong allow = EmgMetaGate(ref d, first, end, units, metas, ev, self, target, (uint)chain, (uint)hostEnt, ref seen, zoneMat);
            EmgMetaDeferMasks(ref d, first, end, metas, ev, out ulong defer, out ulong bad);
            for (ulong u = units & allow; u != 0UL; u &= u - 1UL)
            {
                int o = math.tzcnt(u);
                ulong cover = EmgOrdBit(o) - 1UL;
                if ((cover & (defer | bad)) != 0UL)
                {
                    allow &= ~EmgOrdBit(o);
                }
            }
            return allow;
        }

        /// <summary>本次结算作用的延后元程序位与“累计超上限”的延后位（编译器在延后写端行的 A 写 0）。</summary>
        private static void EmgMetaDeferMasks(ref CombatData d, int first, int end, ulong metas, EmgEventKind ev, out ulong defer, out ulong bad)
        {
            defer = bad = 0UL;
            for (ulong m = metas; m != 0UL; m &= m - 1UL)
            {
                byte o = (byte)math.tzcnt(m);
                int row = EmgMetaRow(ref d, first, end, o, out int kind);
                if (row < 0 || kind != EmgMetaConst.Defer || !EmgMetaApplies(ref d, first, end, o, ev))
                {
                    continue;
                }
                defer |= EmgOrdBit(o);
                if (!(d.EmgInstrs[row].A > 0f))
                {
                    bad |= EmgOrdBit(o);
                }
            }
        }

        private static float EmgCfg(float v, float fallback) => v > 0f ? v : fallback;

        /// <summary>
        /// 事件入队时的元程序判定（<see cref="EmgEmitHosted"/> 调，只在程序有元程序时）：返回此刻执行的序号位；复制（×2 一层、各层相乘、≤ emg.meta.max_exec，间隔 emg.meta.repeat_gap，同位置）
        /// 与延后（每层 emg.meta.defer_seconds）的再执行按到点时刻分组记进运行态。O(节点指令数 × 下游单位数)。
        /// 位置（FGR-EMG-043；“出手点”统一按本次结算的结算点）：复制 = 结算点；延后 = 移动宿主（弹体 / 无人机）到点取它当时的位置、已结束在结束点（<see cref="EmgMetaHostEnd"/>），
        /// 布区在区域中心，当场结算的宿主（单位：格斗 / 力场 / 即时射弹 / 单位级事件）在结算点。
        /// 格斗 / 力场一次出手各命中：条件 / 随机都对主目标求值；副目标那条命中只重放根节点效果、不重放触发器（触发只按主目标一次，FGR-EMG-034 / 035，同 <see cref="EmgQueueChildren"/>）。
        /// </summary>
        internal static ulong EmgMetaSettle(ref CombatData d, in EmgEvent e, in EmgCtx host, float2 dir)
        {
            if (!EmgNodeSpan(ref d, e.Prog, e.Node, out int first, out int end))
            {
                return ulong.MaxValue;
            }
            ulong units = EmgMetaScan(ref d, first, end, out ulong metas);
            if (metas == 0UL)
            {
                return ulong.MaxValue;
            }
            int self = host.Owner >= 0 && host.Owner < d.Count ? host.Owner : -1;
            int target = EmgMetaTarget(ref d, e, host);
            uint zoneMat = EmgNotZone;
            double2 deferAt = e.Pos;
            if (host.EntKind == EmgEntKind.Zone)
            {
                zoneMat = EmgZoneSubj(ref d, host.Ent, ref target, out double2 center);
                deferAt = center;
            }
            bool moving = host.EntKind == EmgEntKind.Projectile || host.EntKind == EmgEntKind.Drone || host.EntKind == EmgEntKind.Zone;
            ulong noReplay = EmgSweep(host) && e.Kind == EmgEventKind.Hit && e.Other != host.MainTarget ? EmgTrigUnits(ref d, e.Prog, e.Node) : 0UL;
            int seen = 0;
            ulong allow = EmgMetaGate(ref d, first, end, units, metas, e.Kind, self, target, (uint)e.Chain, (uint)host.Ent, ref seen, zoneMat);
            EmgMetaDeferMasks(ref d, first, end, metas, e.Kind, out ulong defer, out ulong bad);
            ulong repeat = 0UL;
            for (ulong m = metas; m != 0UL; m &= m - 1UL)
            {
                byte o = (byte)math.tzcnt(m);
                int row = EmgMetaRow(ref d, first, end, o, out int kind);
                if (row >= 0 && kind == EmgMetaConst.Repeat && d.EmgInstrs[row].A > 0f && EmgMetaApplies(ref d, first, end, o, e.Kind))
                {
                    repeat |= EmgOrdBit(o);
                }
            }
            int maxExec = d.Config.EmgMetaMaxExec > 0 ? d.Config.EmgMetaMaxExec : EmgMetaConst.DefaultMaxExec;
            float gap = EmgCfg(d.Config.EmgMetaGap, EmgMetaConst.DefaultGap), deferSec = EmgCfg(d.Config.EmgMetaDefer, EmgMetaConst.DefaultDefer);
            Span<double> offs = stackalloc double[16];
            Span<ulong> masks = stackalloc ulong[16];
            Span<byte> follow = stackalloc byte[16];
            int groups = 0;
            for (ulong u = units & allow; u != 0UL; u &= u - 1UL)
            {
                int o = math.tzcnt(u);
                ulong bit = EmgOrdBit(o), cover = bit - 1UL;
                if ((cover & bad) != 0UL)
                {
                    allow &= ~bit; // 一条链上累计延后超上限：不执行
                    seen |= 1 << EmgMetaConst.Defer;
                    continue;
                }
                int kr = math.countbits(repeat & cover), kd = math.countbits(defer & cover);
                int count = math.min(1 << math.min(kr, 6), maxExec);
                double delay = kd * (double)deferSec;
                if (kd > 0)
                {
                    allow &= ~bit;
                    seen |= 1 << EmgMetaConst.Defer;
                }
                if (count > 1)
                {
                    seen |= 1 << EmgMetaConst.Repeat;
                }
                for (int j = 0; j < count; j++)
                {
                    double off = delay + j * (double)gap;
                    if (off <= 0.0)
                    {
                        continue; // 此刻这一次随结算执行
                    }
                    byte f = (byte)(kd > 0 && moving ? 1 : 0);
                    int g = 0;
                    while (g < groups && !(math.abs(offs[g] - off) < 1e-6 && follow[g] == f))
                    {
                        g++;
                    }
                    if (g == groups)
                    {
                        if (groups >= offs.Length)
                        {
                            EmgStat(ref d, 4); // 分组满了（emg.meta.max_exec 调得很大时）：与运行态满同一口径，拒绝并计数
                            continue;
                        }
                        offs[g] = off;
                        masks[g] = 0UL;
                        follow[g] = f;
                        groups++;
                    }
                    masks[g] |= bit;
                }
            }
            EmgMetaSee(ref d, seen);
            byte firstMeta = (byte)math.tzcnt(metas);
            for (int g = 0; g < groups; g++)
            {
                bool fl = follow[g] != 0;
                EmgMetaEnqueue(ref d, fl ? EmgMetaConst.RunFollow : EmgMetaConst.RunReplay, e.Prog, e.Node, firstMeta, masks[g] & ~noReplay, d.Scalars[0].Time + offs[g], 0.0,
                    e.Kind, fl ? deferAt : e.Pos, dir, e.Other != 0 && e.OtherKind == EmgEntKind.Unit ? e.Other : host.MainTarget, e.Chain, e.Depth, host);
            }
            return allow;
        }

        /// <summary>格斗 / 力场当场结算的宿主（一次出手对扇形 / 圈内每个单位各算一次命中）。</summary>
        private static bool EmgSweep(in EmgCtx host) =>
            host.EntKind == EmgEntKind.Unit && (host.Carrier == (byte)CombatCarrier.Melee || host.Carrier == (byte)CombatCarrier.Aura);

        /// <summary>条件的目标（FGR-EMG-043 条件列）：格斗 / 力场 = 主目标；其余 = 命中目标，没有时退回主目标。</summary>
        private static int EmgMetaTarget(ref CombatData d, in EmgEvent e, in EmgCtx host)
        {
            int t = EmgSweep(host) && host.MainTarget != 0 ? d.SlotOf(host.MainTarget) : -1;
            if (t < 0 && e.OtherKind == EmgEntKind.Unit && e.Other != 0)
            {
                t = d.SlotOf(e.Other);
            }
            if (t < 0 && host.MainTarget != 0)
            {
                t = d.SlotOf(host.MainTarget);
            }
            return t;
        }

        /// <summary>节点 <paramref name="node"/> 的直接子节点是哪些触发器（程序序号位）放出的。O(程序节点数)，只在格斗 / 力场副目标命中时。</summary>
        private static ulong EmgTrigUnits(ref CombatData d, int prog, int node)
        {
            EmgProgram p = d.EmgPrograms[prog];
            ulong bits = 0UL;
            for (int j = 1; j < p.NodeCount && p.FirstNode + j < d.EmgNodes.Length; j++)
            {
                EmgNode c = d.EmgNodes[p.FirstNode + j];
                if (c.Parent == node && c.MetaMask > 0)
                {
                    bits |= EmgOrdBit(c.MetaMask - 1);
                }
            }
            return bits;
        }

        /// <summary>移动宿主（弹体 / 无人机）结束：跟随它的延后记录改记结束点（FGR-EMG-043“弹体已结束则在结束点”）。O(运行态条数)；表空时 O(1)。</summary>
        internal static void EmgMetaHostEnd(ref CombatData d, EmgEntKind kind, int id, double2 at)
        {
            for (int i = 0; i < d.MetaRun.Length; i++)
            {
                EmgMetaRun m = d.MetaRun[i];
                if (m.Mode == EmgMetaConst.RunFollow && m.Host == id && m.HostKind == (byte)kind)
                {
                    m.Pos = at;
                    d.MetaRun[i] = m;
                }
            }
        }

        private static void EmgMetaEnqueue(ref CombatData d, byte mode, int prog, int node, byte ord, ulong mask, double dueAt, double until, EmgEventKind ev, double2 pos, float2 dir,
            int other, int chain, byte depth, in EmgCtx host)
        {
            if (mask == 0UL)
            {
                return;
            }
            if (d.MetaRun.Length >= EmgConst.PendingCapacity)
            {
                EmgStat(ref d, 4); // 与待生成队列同一口径：满了拒绝并计数（正常为 0）
                return;
            }
            d.MetaRun.Add(new EmgMetaRun
            {
                DueAt = dueAt, Unit = host.OwnerId, Prog = prog, Chain = chain, Node = (byte)node, Ordinal = ord, Host = host.Ent, HostKind = (byte)host.EntKind, Mode = mode,
                Ev = (byte)ev, Depth = depth, Mask = mask, Pos = pos, Dir = dir, Other = other, Weapon = host.Weapon, Damage = math.max(0f, host.Damage), Faction = host.Faction,
                Carrier = host.Carrier, PFlags = (ushort)host.PFlags, ZoneRadius = math.max(0f, host.ZoneRadius), ZoneLife = math.max(0f, host.ZoneLife), Until = until,
            });
        }

        /// <summary>
        /// 载荷出手 / 生成时（飞行弹体、子弹药、布区落区、子区、当场结算的出手）：返回被条件 / 随机关掉的下游修饰位（给 <see cref="EmgModsOf"/> 的 skip；FGR-EMG-040“条件为假时下游修饰不生效”、
        /// “随机选中才生效”），并为节点里作用于出手的循环开运行态（射弹 / 无人机 / 布区跟随宿主；<paramref name="anchorUntil"/> &gt; 0 = 格斗 / 力场 / 当场结算的“程序锚点”，在出手点执行到该时刻）。
        /// 程序没有元程序返回 0。O(节点指令数 × 元程序数)。
        /// </summary>
        internal static ulong EmgMetaLaunch(ref CombatData d, int prog, int node, int self, int target, in EmgCtx host, double2 pos, float2 dir, double anchorUntil, bool loops)
        {
            if (!EmgHasMeta(ref d, prog) || !EmgNodeSpan(ref d, prog, node, out int first, out int end))
            {
                return 0UL;
            }
            ulong units = EmgMetaScan(ref d, first, end, out ulong metas);
            if (metas == 0UL)
            {
                return 0UL;
            }
            int seen = 0;
            uint zoneMat = host.EntKind == EmgEntKind.Zone ? EmgZoneSubj(ref d, host.Ent, ref target, out _) : EmgNotZone; // 布区：对区域内首个单位 / 区域本身求值（FGR-EMG-043）
            ulong allow = EmgMetaGate(ref d, first, end, units, metas, EmgEventKind.Fire, self, target, (uint)host.Chain, (uint)host.Ent, ref seen, zoneMat);
            EmgMetaSee(ref d, seen);
            if (loops)
            {
                float period = EmgCfg(d.Config.EmgMetaLoopPeriod, EmgMetaConst.DefaultLoopPeriod);
                for (ulong m = metas; m != 0UL; m &= m - 1UL)
                {
                    byte o = (byte)math.tzcnt(m);
                    if (EmgMetaRow(ref d, first, end, o, out int kind) < 0 || kind != EmgMetaConst.Loop || (allow & EmgOrdBit(o)) == 0UL || !EmgMetaApplies(ref d, first, end, o, EmgEventKind.Fire))
                    {
                        continue;
                    }
                    ulong down = units & allow & EmgAbove(o);
                    double due = d.Scalars[0].Time + EmgLoopPeriod(ref d, first, end, o, self, period);
                    EmgMetaEnqueue(ref d, EmgMetaConst.RunLoop, prog, node, o, down, due, anchorUntil, EmgEventKind.None, pos, dir, host.MainTarget, host.Chain, host.Depth, host);
                }
            }
            return ~allow & units;
        }

        /// <summary>循环周期：0.5 秒；同一条循环带 prop.heat 感知读端时“本机越热周期越短”= 周期 × (1 − 0.5 × 积热比例)（FG19A N05），下限 0.1 秒。</summary>
        private static double EmgLoopPeriod(ref CombatData d, int first, int end, byte ord, int self, float period)
        {
            for (int j = first; j < end; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                if (r.Ordinal == ord && r.Role == EmgConst.RoleSense && r.Port == EmgConst.PortPropHeat)
                {
                    return math.max(0.1, period * (1.0 - 0.5 * math.saturate(EmgHeatRatio(ref d, self))));
                }
            }
            return period;
        }

        /// <summary>
        /// 反转（FGR-EMG-040；FG19A N07）：程序带逆序树（下一行）时按 meta.reverse 的条件读端（本机积热 ≥ 幅度）选逆序树，否则正序树。只在出手开始时调（每发一次）。
        /// </summary>
        internal static int EmgTreeOf(ref CombatData d, int prog, int self)
        {
            if (prog < 0 || prog + 1 >= d.EmgPrograms.Length || (d.EmgPrograms[prog].Flags & EmgProgramFlags.HasReverse) == 0
                || (d.EmgPrograms[prog + 1].Flags & EmgProgramFlags.IsReverse) == 0)
            {
                return prog;
            }
            EmgProgram p = d.EmgPrograms[prog];
            for (int k = 0; k < p.NodeCount && p.FirstNode + k < d.EmgNodes.Length; k++)
            {
                EmgNode n = d.EmgNodes[p.FirstNode + k];
                int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
                for (int i = n.FirstInstr; i < end; i++)
                {
                    EmgInstr x = d.EmgInstrs[i];
                    if (x.Role > EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0 && EmgMetaConst.KindOf(x.Port) == EmgMetaConst.Reverse)
                    {
                        bool rev = EmgMetaCond(ref d, n.FirstInstr, end, x.Ordinal, self, -1);
                        if (rev)
                        {
                            EmgMetaSee(ref d, 1 << EmgMetaConst.Reverse);
                        }
                        return rev ? prog + 1 : prog;
                    }
                }
            }
            return prog;
        }

        /// <summary>本次出手实际用的程序：出手上下文已按 <see cref="EmgTreeOf"/> 选了这把武器的逆序树时返回逆序树，否则武器自己的程序。</summary>
        internal static int EmgShotProg(ref CombatData d, int weaponProg)
        {
            int c = d.EmgCtx.Prog;
            return weaponProg >= 0 && c == weaponProg + 1 && c < d.EmgPrograms.Length && (d.EmgPrograms[weaponProg].Flags & EmgProgramFlags.HasReverse) != 0 ? c : weaponProg;
        }

        // ─────────────────────────────── 5e'. 运行态到点执行 ───────────────────────────────

        /// <summary>
        /// 每步（总线之后、待生成之前）处理元程序运行态：到点的重放按宿主执行一次下游段（复制 = 同位置；延后 = 宿主还在取它当时位置，已结束在结束点）；
        /// 循环每周期执行一次下游段（不论下游指令各自的事件），宿主结束 / 锚点到期 / 达 emg.meta.loop_max 次即结束。暂停不步进、倍速按游戏秒到点。
        /// O(运行态条数 × 查宿主)；表空时 O(1)（调用方先判长度）。
        /// </summary>
        internal static void StepEmgMeta(ref CombatData d)
        {
            int n = d.MetaRun.Length;
            if (n == 0)
            {
                return;
            }
            double now = d.Scalars[0].Time;
            int loopMax = d.Config.EmgMetaLoopMax > 0 ? d.Config.EmgMetaLoopMax : EmgMetaConst.DefaultLoopMax;
            float period = EmgCfg(d.Config.EmgMetaLoopPeriod, EmgMetaConst.DefaultLoopPeriod);
            int write = 0;
            for (int i = 0; i < n; i++)
            {
                EmgMetaRun m = d.MetaRun[i];
                if (now + 1e-9 < m.DueAt)
                {
                    d.MetaRun[write++] = m;
                    continue;
                }
                if (!EmgNodeSpan(ref d, m.Prog, m.Node, out int first, out int end))
                {
                    continue; // 程序表已换（读档坏下标）：丢弃，不执行
                }
                bool alive = EmgMetaHostAt(ref d, m, out double2 pos, out float2 dir);
                if (m.Mode == EmgMetaConst.RunLoop)
                {
                    bool anchor = m.Until > 0.0;
                    if (anchor ? now > m.Until + 1e-9 : !alive)
                    {
                        continue; // 宿主已结束 / 锚点到期：循环结束（FGR-EMG-040“直到宿主结束”）
                    }
                    if (anchor)
                    {
                        pos = m.Pos;
                        dir = m.Dir;
                    }
                    EmgMetaExec(ref d, m, pos, dir, true);
                    EmgMetaSee(ref d, 1 << EmgMetaConst.Loop);
                    m.Loop++;
                    if (m.Loop >= loopMax)
                    {
                        continue;
                    }
                    int self = d.SlotOf(m.Unit);
                    m.DueAt += EmgLoopPeriod(ref d, first, end, m.Ordinal, self, period);
                    m.Pos = pos;
                    d.MetaRun[write++] = m;
                    continue;
                }
                if (m.Mode != EmgMetaConst.RunFollow || !alive)
                {
                    pos = m.Pos;
                    dir = m.Dir;
                }
                EmgMetaExec(ref d, m, pos, dir, false);
            }
            int len = d.MetaRun.Length;
            for (int i = n; i < len; i++)
            {
                d.MetaRun[write++] = d.MetaRun[i]; // 本步执行中新记的（下游效果又触发了结算）留到它们自己的到点
            }
            d.MetaRun.ResizeUninitialized(write);
        }

        /// <summary>宿主此刻的位置与方向：弹体 / 区域按稳定 ID 找，单位按 ID；找不到 = 已结束（返回 false，位置取记录里的最后已知位置）。O(弹体数 / 区域数)。</summary>
        private static bool EmgMetaHostAt(ref CombatData d, in EmgMetaRun m, out double2 pos, out float2 dir)
        {
            pos = m.Pos;
            dir = m.Dir;
            switch ((EmgEntKind)m.HostKind)
            {
                case EmgEntKind.Projectile:
                    for (int p = 0; p < d.Projectiles.Length; p++)
                    {
                        CombatProjectile pr = d.Projectiles[p];
                        if (pr.Id == m.Host)
                        {
                            pos = pr.Pos;
                            dir = EmgDir((double2)pr.Vel);
                            return true;
                        }
                    }
                    return false;
                case EmgEntKind.Zone:
                    for (int z = 0; z < d.Zones.Length; z++)
                    {
                        CombatZone zn = d.Zones[z];
                        if (zn.EmgId == m.Host && zn.EmgProgRef > 0)
                        {
                            pos = zn.Pos;
                            return true;
                        }
                    }
                    return false;
                case EmgEntKind.Drone:
                    for (int q = 0; q < d.Drones.Length; q++)
                    {
                        CombatDrone dr = d.Drones[q];
                        if (dr.Id == m.Host)
                        {
                            pos = dr.Pos;
                            float2 v = EmgDir(dr.Pos - dr.Prev);
                            dir = math.lengthsq(v) > 0f ? v : m.Dir; // 悬停不动时沿用入队时的朝向
                            return true;
                        }
                    }
                    return false;
                case EmgEntKind.Unit:
                {
                    int s = d.SlotOf(m.Host);
                    if (s >= 0 && d.IsAlive(s))
                    {
                        pos = d.Pos[s];
                        return true;
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// 执行一次下游段（<see cref="EmgMetaRun.Mask"/> 里的序号）：根节点的内核效果写端（<see cref="EmgRootGated"/>）、子节点的写端与落点效果（<see cref="EmgChildWrites"/>、<see cref="EmgNodeEffects"/>），
        /// 以及下游触发器放出的子载荷（已唤起、直接进待生成表，链深到 3 不再生成）。<paramref name="loop"/> = 循环：不论下游指令各自的生效事件；否则只执行在原结算事件上生效的那些。
        /// </summary>
        private static void EmgMetaExec(ref CombatData d, in EmgMetaRun m, double2 pos, float2 dir, bool loop)
        {
            var e = new EmgEvent
            {
                Kind = (EmgEventKind)m.Ev, Pos = pos, Other = m.Other, OtherKind = m.Other != 0 ? EmgEntKind.Unit : EmgEntKind.None, Prog = m.Prog, Node = m.Node,
                Chain = m.Chain, Depth = m.Depth, Ent = m.Host, EntKind = (EmgEntKind)m.HostKind,
            };
            int owner = d.SlotOf(m.Unit);
            var host = new EmgCtx
            {
                Prog = m.Prog, Chain = m.Chain, Ent = m.Host, Owner = owner, Node = m.Node, Depth = m.Depth, EntKind = (EmgEntKind)m.HostKind, Damage = m.Damage, Weapon = m.Weapon,
                OwnerId = m.Unit, Faction = m.Faction, Carrier = m.Carrier, MainTarget = m.Other, ZoneRadius = m.ZoneRadius, ZoneLife = m.ZoneLife, PFlags = (EmgProjFlags)m.PFlags,
                HitCode = EmgCode.HitDirect,
            };
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx = host;
            if (m.Node == 0)
            {
                EmgRootGated(ref d, e, host, m.Mask, loop);
            }
            else
            {
                EmgChildWrites(ref d, e, host, m.Mask, loop);
                EmgNodeEffects(ref d, m.Prog, m.Node, pos, owner, (CombatFaction)m.Faction, m.Chain, m.Depth, m.Mask);
            }
            EmgMetaChildren(ref d, e, host, dir, m.Mask, loop);
            d.EmgCtx = saved;
        }

        /// <summary>下游触发器放出的子载荷（重放 / 循环）：直接记成已唤起的待生成请求，在本步 5e 生成；循环的子载荷沿宿主飞行方向扇形放出（与飞行中定时空爆同一读法）。</summary>
        private static void EmgMetaChildren(ref CombatData d, in EmgEvent e, in EmgCtx host, float2 dir, ulong mask, bool loop)
        {
            if (e.Prog < 0 || e.Prog >= d.EmgPrograms.Length)
            {
                return;
            }
            EmgProgram p = d.EmgPrograms[e.Prog];
            int maxChildren = EmgMaxChildren(ref d);
            bool checkedDepth = false;
            for (int j = 1; j < p.NodeCount; j++)
            {
                int ni = p.FirstNode + j;
                if (ni >= d.EmgNodes.Length)
                {
                    break;
                }
                EmgNode c = d.EmgNodes[ni];
                if (c.Parent != e.Node || c.TrigCount == 0 || c.MetaMask == 0 || !EmgMetaAllowed(mask, (byte)(c.MetaMask - 1)) || (!loop && c.TrigEvent != e.Kind))
                {
                    continue;
                }
                if (!checkedDepth)
                {
                    checkedDepth = true;
                    if (!EmgCanGenerate(ref d, e))
                    {
                        return; // 链深已到 3：不再生成后代（计 DepthCut）
                    }
                }
                if (d.EmgPending.Length >= EmgConst.PendingCapacity)
                {
                    EmgStat(ref d, 4);
                    continue;
                }
                d.EmgPending.Add(new EmgSpawn
                {
                    Kind = EmgConst.PendSub, Armed = 1, Node = (byte)j, Gen = c.Gen, Depth = (byte)math.min(e.Depth + 1, EmgConst.MaxDepth),
                    Count = (byte)math.min((int)c.TrigCount, maxChildren), SubForm = c.SubForm != 0 ? c.SubForm : EmgSubFormOf(host.Carrier),
                    Prog = e.Prog, Chain = e.Chain, Owner = host.OwnerId, Weapon = host.Weapon, Faction = host.Faction, Damage = host.Damage,
                    ZoneRadius = host.ZoneRadius, ZoneLife = host.ZoneLife, Flags = host.PFlags & EmgProjFlags.Heal, Pos = e.Pos, Dir = dir,
                    Other = !loop && e.Kind == EmgEventKind.Hit ? e.Other : 0, Ent = loop ? (int)EmgEventKind.Timer : (int)e.Kind,
                });
            }
        }
    }
}
