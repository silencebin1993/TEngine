using System;
using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-MAT-01（FG19 FGR-EMG-080～083；FG19B 第 1、8 节；ADR-EMG-001 ⑤ B′）：物质斑块层——全部逐斑块 / 逐单位逻辑都在这里（Main/Sim，Burst 内核），热更层不遍历斑块。
    /// - 生成（排放写端 / 残骸崩解 / 规则产物 / 热更注入）：先并入重叠的同物质同层斑块；某物质到软上限 → 就近并入 6 米内同种，再不行同种最早提前到期；
    ///   战斗斑块到总上限（容量 − 世界源保留额）→ 并入最近同物质，再不行同层最早提前到期；世界源只占保留额、不被挤掉。每步生成 ≤ emg.patch.spawn_per_step，其余进待生成表顺延。
    ///   降级全部计数（<see cref="EmgScalars"/> Patch*），热更层调试行可见。
    /// - 节拍：每块自带 NextTick 错相，到期斑块按 (NextTick, Id) 升序每步最多结算 emg.patch.per_step 块，其余 NextTick 不变、下一步优先（不按下标分桶）；
    ///   内容 = 衰减（蒸干 / 燃尽在最后 25% 缩半径，气体扩散稀释）、明火排烟、同物质同层重叠合并（Owner 与因果链取份量大者并记“合并”）、到期。
    /// - 单位接触：单位按槽位分 0.25 秒一轮的桶（每步约 1/15），经空间哈希只查本单位所在格的斑块（≤ 4 块），不认阵营（中立）：挂沾染位、直接接触伤害走 DamageFrom（evt.hurt Code = 斑块 4）、
    ///   直接减速；离地单位不吃地面层；进 / 离物质发 evt.enter / evt.leave（Code = 物质码）。
    /// - 弹体：每步线段经空间哈希查穿过的斑块，挂沾染（命中时带给目标）；进 / 离发 evt.enter / evt.leave（宿主 = 载荷，位置 = 斑块中心，FG19A N14 入场引信）。
    /// 斑块列表不压实：被合并 / 到期的槽位记 Matter = 0（墓碑），生成时复用第一个空槽（下标稳定，空间哈希只在增删 / 外接格变大时重建）；快照只写活着的斑块。
    /// </summary>
    public static partial class CombatLogic
    {
        // ─────────────────────────────── 配置 ───────────────────────────────

        internal static bool EmgPatchesOn(ref CombatData d) => d.Config.EmgPatchOn != 0 && d.EmgMatters.IsCreated && d.EmgMatters[EmgMatterConst.Oil].Valid;

        private static int PatchCapOf(ref CombatData d) => d.Config.EmgPatchCapacity > 0 ? math.min(d.Config.EmgPatchCapacity, EmgMatterConst.DefaultCapacity) : EmgMatterConst.DefaultCapacity;

        private static int PatchReserveOf(ref CombatData d) =>
            math.min(d.Config.EmgPatchReserve > 0 ? d.Config.EmgPatchReserve : EmgMatterConst.DefaultReserveWorld, PatchCapOf(ref d));

        private static int PatchPerStepOf(ref CombatData d) => d.Config.EmgPatchPerStep > 0 ? d.Config.EmgPatchPerStep : EmgMatterConst.DefaultPerStep;

        private static int PatchSpawnPerStepOf(ref CombatData d) => d.Config.EmgPatchSpawnPerStep > 0 ? d.Config.EmgPatchSpawnPerStep : EmgMatterConst.DefaultSpawnPerStep;

        private static int PatchNeighborsOf(ref CombatData d) => d.Config.EmgPatchNeighbors > 0 ? d.Config.EmgPatchNeighbors : EmgMatterConst.DefaultMaxNeighbors;

        private static float PatchTickOf(ref CombatData d) => d.Config.EmgPatchTick > 0f ? d.Config.EmgPatchTick : EmgMatterConst.DefaultTick;

        private static float PatchMaxRadiusOf(ref CombatData d) => d.Config.EmgPatchMaxRadius > 0f ? d.Config.EmgPatchMaxRadius : EmgMatterConst.DefaultMaxRadius;

        private static float PatchSoftMergeOf(ref CombatData d) => d.Config.EmgPatchSoftMerge > 0f ? d.Config.EmgPatchSoftMerge : EmgMatterConst.DefaultSoftMergeRadius;

        private static float GasMinConcOf(ref CombatData d) => d.Config.EmgGasMinConc > 0f ? d.Config.EmgGasMinConc : EmgMatterConst.DefaultMinConc;

        internal static void EmgHashEnsurePublic(ref CombatData d) => EmgHashEnsure(ref d);

        internal static EmgMatter MatterOf(ref CombatData d, int m) => m >= 1 && m <= EmgMatterConst.Count && d.EmgMatters.IsCreated ? d.EmgMatters[m] : default;

        private static bool PatchAlive(in CombatZone z) => z.Matter != 0;

        private static bool PatchWorld(in CombatZone z) => (z.PFlags & EmgMatterConst.FlagWorld) != 0;

        // ─────────────────────────────── 生成 ───────────────────────────────

        /// <summary>
        /// 请求生成一块斑块（排放写端 / 残骸崩解 / 规则产物 / 热更注入）。<paramref name="seconds"/> ≤ 0 = 按物质默认存续（fg.TbMatter lifetime）。
        /// 本步生成额度用完时进待生成表顺延（生成时才开始计寿命，FGR-EMG-063）；返回 true = 已生成或已顺延，false = 层没开 / 参数不合法 / 被降级丢弃（计数）。
        /// </summary>
        internal static bool EmgRequestPatch(ref CombatData d, byte matter, double2 pos, float radius, float amount, float seconds, int ownerId, int chain, byte depth,
            byte flags = 0, byte phase = 0)
        {
            // 写入口与读档（PendPatchValid）同口径：顺延记录写得进去就一定读得回来
            if (!EmgPatchesOn(ref d) || !MatterOf(ref d, matter).Valid || !(radius > 0f) || !math.isfinite(radius) || !math.isfinite(amount) || !math.isfinite(seconds)
                || !math.all(math.isfinite(pos)) || depth > EmgConst.MaxDepth || chain < 0 || phase > EmgMatterConst.PhaseFromWreck)
            {
                return false;
            }
            EmgScalars es = d.EmgS[0];
            if ((flags & EmgMatterConst.FlagWorld) == 0 && es.PatchSpawnedStep >= PatchSpawnPerStepOf(ref d))
            {
                if (d.EmgPending.Length >= EmgConst.PendingCapacity)
                {
                    es.PatchDropped++;
                    d.EmgS[0] = es;
                    return false;
                }
                es.PatchDeferred++;
                d.EmgS[0] = es;
                d.EmgPending.Add(new EmgSpawn
                {
                    Kind = EmgConst.PendPatch, Armed = 1, Count = 1, SubForm = matter, Pos = pos, ZoneRadius = radius, Damage = amount, ZoneLife = seconds,
                    Owner = ownerId, Chain = chain, Depth = depth, Gen = flags, Faction = phase, Prog = -1, Weapon = -1,
                });
                return true;
            }
            return EmgPlacePatch(ref d, matter, pos, radius, amount, seconds, ownerId, chain, depth, flags, phase) >= 0;
        }

        /// <summary>待生成表里顺延的斑块：本步还有额度就生成（返回 true = 处理完，不再留在表里）。</summary>
        private static bool EmgPendPatch(ref CombatData d, in EmgSpawn s)
        {
            if (!EmgPatchesOn(ref d))
            {
                return true; // 层被关（emg.stage 回落）：作废
            }
            if (d.EmgS[0].PatchSpawnedStep >= PatchSpawnPerStepOf(ref d))
            {
                return false;
            }
            EmgPlacePatch(ref d, s.SubForm, s.Pos, s.ZoneRadius, s.Damage, s.ZoneLife, s.Owner, s.Chain, s.Depth, s.Gen, s.Faction);
            return true;
        }

        /// <summary>
        /// 步首：顺延斑块先占本步生成额度（先进先出；复审 P2：原先 5e 才处理，本步新请求先用掉额度，最早顺延的可能一直轮不到）。
        /// 处理完的只置 Count = 0 不挪位置（ArmCursor 记的是表内下标），5e 压缩时移走。O(待生成数)，只在待生成表非空时。
        /// </summary>
        internal static void StepPendingPatches(ref CombatData d)
        {
            int quota = PatchSpawnPerStepOf(ref d);
            for (int i = 0; i < d.EmgPending.Length && d.EmgS[0].PatchSpawnedStep < quota; i++)
            {
                EmgSpawn s = d.EmgPending[i];
                if (s.Kind == EmgConst.PendPatch && s.Count != 0 && EmgPendPatch(ref d, s))
                {
                    s.Count = 0;
                    d.EmgPending[i] = s;
                }
            }
        }

        /// <summary>真正落一块斑块（含合并与容量降级）。返回斑块下标（并入已有斑块时是那一块），−1 = 丢弃。一次 O(斑块数) 扫描，生成每步 ≤ 32 块。</summary>
        internal static int EmgPlacePatch(ref CombatData d, byte matter, double2 pos, float radius, float amount, float seconds, int ownerId, int chain, byte depth,
            byte flags, byte phase)
        {
            EmgMatter m = MatterOf(ref d, matter);
            bool world = (flags & EmgMatterConst.FlagWorld) != 0;
            float maxR = PatchMaxRadiusOf(ref d);
            radius = world ? radius : math.min(radius, maxR);
            amount = amount > 0f ? amount : 1f;
            double now = d.Scalars[0].Time;
            float life = seconds > 0f ? seconds : m.LifeFor(amount);
            EmgScalars es = d.EmgS[0];

            // 一次扫描：同种数量 / 战斗与世界源数量 / 重叠最多的可合并同种 / 最近同种（软上限 6 米内与总上限不限距离）/ 同种最早 / 同层最早
            int sameCount = 0, combat = 0, worldN = 0, free = -1;
            int bestOverlap = -1, nearest = -1, nearSoft = -1, oldestSame = -1, oldestLayer = -1;
            double bestRatio = 0, nearD = double.MaxValue, nearSoftD = double.MaxValue;
            float soft = PatchSoftMergeOf(ref d);
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (!PatchAlive(z))
                {
                    if (free < 0)
                    {
                        free = i;
                    }
                    continue;
                }
                if (PatchWorld(z))
                {
                    worldN++;
                    continue;
                }
                combat++;
                if (IsLegacy(z))
                {
                    continue; // EMG2-MAT-02：迁入斑块只占总数，不当合并对象、不被软 / 总上限挤掉（等价迁移期按区域时长到期）
                }
                if (z.Layer == m.Layer && (oldestLayer < 0 || Older(z, d.Patches[oldestLayer])))
                {
                    oldestLayer = i;
                }
                if (z.Matter != matter)
                {
                    continue;
                }
                sameCount++;
                if (oldestSame < 0 || Older(z, d.Patches[oldestSame]))
                {
                    oldestSame = i;
                }
                double dist = math.distance(z.Pos, pos);
                if (dist < nearD || (dist == nearD && z.EmgId < d.Patches[nearest].EmgId))
                {
                    nearD = dist;
                    nearest = i;
                }
                if (dist <= soft && (dist < nearSoftD || (dist == nearSoftD && z.EmgId < d.Patches[nearSoft].EmgId)))
                {
                    nearSoftD = dist;
                    nearSoft = i;
                }
                if (m.MergeOverlap > 0f && !world)
                {
                    double ratio = OverlapRatio(z.Pos, z.Radius, pos, radius);
                    if (ratio >= m.MergeOverlap && (ratio > bestRatio || (ratio == bestRatio && z.EmgId < d.Patches[bestOverlap].EmgId)))
                    {
                        bestRatio = ratio;
                        bestOverlap = i;
                    }
                }
            }
            var incoming = new CombatZone
            {
                Pos = pos, Radius = radius, R0 = radius, Amount = amount, Matter = matter, Layer = m.Layer, PFlags = flags, Phase = phase, Owner = ownerId,
                Owners = new int4(ownerId > 0 ? ownerId : 0, 0, 0, 0), EmgChain = chain, EmgDepth = depth, Born = now, Until = world ? double.MaxValue : now + life,
                Temp = m.TempInit, Conc = 1f, Hp = m.Solid != 0 ? m.Hp : 0f, Faction = CombatFaction.Neutral, Kind = EmgMatterConst.ZoneKindPatch,
            };
            if (!world)
            {
                // 1. 与重叠的同种合并（份量相加、面积守恒）
                if (bestOverlap >= 0 && EmgMergeInto(ref d, bestOverlap, incoming, false))
                {
                    return bestOverlap;
                }
                // 2. 软上限（FG19B §8.1）：就近并入 6 米内同种（不受重叠阈值限制）→ 同种最早提前到期
                if (m.Quota > 0 && sameCount >= m.Quota)
                {
                    if (nearSoft >= 0 && EmgMergeInto(ref d, nearSoft, incoming, true))
                    {
                        es = d.EmgS[0];
                        es.PatchSoftMerged++;
                        d.EmgS[0] = es;
                        return nearSoft;
                    }
                    if (oldestSame < 0)
                    {
                        return DropPatch(ref d);
                    }
                    KillPatch(ref d, oldestSame);
                    es = d.EmgS[0];
                    es.PatchSoftExpired++;
                    d.EmgS[0] = es;
                    combat--;
                    if (oldestLayer == oldestSame)
                    {
                        oldestLayer = -1;
                    }
                    free = free < 0 || oldestSame < free ? oldestSame : free;
                }
                // 3. 总上限（FGR-EMG-082）：并入最近同物质 → 同层最早提前到期（世界源不参与）
                if (combat >= PatchCapOf(ref d) - PatchReserveOf(ref d))
                {
                    if (nearest >= 0 && PatchAlive(d.Patches[nearest]) && EmgMergeInto(ref d, nearest, incoming, true))
                    {
                        es = d.EmgS[0];
                        es.PatchCapMerged++;
                        d.EmgS[0] = es;
                        return nearest;
                    }
                    if (oldestLayer < 0 || !PatchAlive(d.Patches[oldestLayer]))
                    {
                        oldestLayer = OldestOfLayer(ref d, m.Layer);
                    }
                    if (oldestLayer < 0)
                    {
                        return DropPatch(ref d);
                    }
                    KillPatch(ref d, oldestLayer);
                    es = d.EmgS[0];
                    es.PatchCapExpired++;
                    d.EmgS[0] = es;
                    free = free < 0 || oldestLayer < free ? oldestLayer : free;
                }
            }
            else if (worldN >= PatchReserveOf(ref d))
            {
                return DropPatch(ref d); // 世界源保留额满（48 摊液洼 + 8 首领灌注 + 余量，正常到不了）
            }
            es = d.EmgS[0];
            incoming.EmgId = es.NextPatchId++;
            float tick = PatchTickOf(ref d);
            incoming.TickInterval = tick;
            incoming.NextTick = now + tick;
            es.PatchesBorn++;
            es.PatchSpawnedStep++;
            es.PatchLive++;
            es.HashDirty = 1;
            d.EmgS[0] = es;
            int at;
            if (free >= 0 && free < d.Patches.Length)
            {
                d.Patches[free] = incoming;
                at = free;
            }
            else
            {
                at = d.Patches.Length;
                d.Patches.Add(incoming);
            }
            return at;
        }

        private static int DropPatch(ref CombatData d)
        {
            EmgScalars es = d.EmgS[0];
            es.PatchDropped++;
            d.EmgS[0] = es;
            return -1;
        }

        private static bool Older(in CombatZone a, in CombatZone b) => a.Born < b.Born || (a.Born == b.Born && a.EmgId < b.EmgId);

        private static int OldestOfLayer(ref CombatData d, byte layer)
        {
            int best = -1;
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && !PatchWorld(z) && !IsLegacy(z) && z.Layer == layer && (best < 0 || Older(z, d.Patches[best])))
                {
                    best = i;
                }
            }
            return best;
        }

        /// <summary>斑块消失（到期 / 被合并 / 被挤掉）：记墓碑，槽位留给下一块（下标稳定）。</summary>
        private static void KillPatch(ref CombatData d, int i)
        {
            CombatZone z = d.Patches[i];
            if (!PatchAlive(z))
            {
                return;
            }
            z.Matter = 0;
            d.Patches[i] = z;
            EmgScalars es = d.EmgS[0];
            es.PatchLive = math.max(0, es.PatchLive - 1);
            es.HashDirty = 1;
            d.EmgS[0] = es;
        }

        /// <summary>
        /// 把 <paramref name="b"/> 并入第 <paramref name="into"/> 块（FGR-EMG-082）：份量相加、面积守恒求新半径（超过单块半径上限就不合并，返回 false）、
        /// 中心 / 温度 / 电荷 / 浓度按份量加权（强制并入且两块相距超过半径和 + 软合并半径时中心不动）；Owner、归属与因果链取份量大者，链深取大，因果链记一条“合并”（不加链深，FGR-EMG-061）。
        /// <paramref name="force"/> = 软 / 总上限的就近并入（不受重叠阈值限制，但仍受半径上限）。
        /// </summary>
        private static bool EmgMergeInto(ref CombatData d, int into, in CombatZone b, bool force)
        {
            CombatZone a = d.Patches[into];
            float maxR = PatchMaxRadiusOf(ref d);
            float r = math.sqrt(a.Radius * a.Radius + b.Radius * b.Radius);
            if (r > maxR + 1e-4f)
            {
                if (!force)
                {
                    return false;
                }
                r = maxR; // 就近并入时半径封顶（份量照加；FG19B §8.1“仍然不行”才让最早的到期，这里优先保住新排放）
            }
            float total = a.Amount + b.Amount;
            float wa = total > 0f ? a.Amount / total : 0.5f, wb = 1f - wa;
            bool bWins = b.Amount > a.Amount;
            int otherChain = bWins ? a.EmgChain : b.EmgChain;
            // 总上限的就近并入不限距离：两块离得远时只加份量、中心不动（复审 P2：按份量加权会让已有斑块瞬移到两边都没排放过的中点）
            if (!force || math.distance(a.Pos, b.Pos) <= a.Radius + b.Radius + PatchSoftMergeOf(ref d))
            {
                a.Pos = a.Pos * wa + b.Pos * wb;
            }
            a.Temp = a.Temp * wa + b.Temp * wb;
            a.Charge = a.Charge * wa + b.Charge * wb;
            a.Conc = a.Conc * wa + b.Conc * wb;
            a.Amount = total;
            a.Radius = r;
            a.R0 = math.max(a.R0, r);
            a.Until = math.max(a.Until, b.Until);
            a.EmgDepth = (byte)math.max((int)a.EmgDepth, (int)b.EmgDepth);
            if (bWins)
            {
                a.Owner = b.Owner;
                a.Owners = b.Owners;
                a.EmgChain = b.EmgChain;
                a.Phase = b.Phase;
            }
            d.Patches[into] = a;
            EmgScalars es = d.EmgS[0];
            es.HashDirty = 1;
            d.EmgS[0] = es;
            // EMG1-CHAIN-01 / ADR ⑨：合并记一条（Key = 斑块 Id，Other = 被并入那条链，Aux = 物质码）
            EmgChainLog(ref d, EmgChainNode.Merge, a.EmgChain, a.EmgDepth, a.EmgId, a.Owner > 0 ? a.Owner : 0, a.Pos, otherChain, a.Matter);
            return true;
        }

        /// <summary>两圆重叠面积 ÷ 较小圆面积（0～1）。</summary>
        internal static double OverlapRatio(double2 pa, float ra, double2 pb, float rb)
        {
            double dd = math.distance(pa, pb);
            double r1 = ra, r2 = rb;
            if (r1 <= 0 || r2 <= 0 || dd >= r1 + r2)
            {
                return 0;
            }
            double small = math.min(r1, r2);
            if (dd <= math.abs(r1 - r2))
            {
                return 1;
            }
            double a1 = r1 * r1 * math.acos(math.clamp((dd * dd + r1 * r1 - r2 * r2) / (2 * dd * r1), -1, 1));
            double a2 = r2 * r2 * math.acos(math.clamp((dd * dd + r2 * r2 - r1 * r1) / (2 * dd * r2), -1, 1));
            double a3 = 0.5 * math.sqrt(math.max(0, (-dd + r1 + r2) * (dd + r1 - r2) * (dd - r1 + r2) * (dd + r1 + r2)));
            return math.saturate((a1 + a2 - a3) / (math.PI * small * small));
        }

        // ─────────────────────────────── 节拍 ───────────────────────────────

        private struct PatchDue : IComparable<PatchDue>
        {
            public double T;
            public int Id;
            public int Index;

            public int CompareTo(PatchDue o) => T < o.T ? -1 : T > o.T ? 1 : Id.CompareTo(o.Id);
        }

        /// <summary>5c. 斑块节拍 + 单位接触（StepReadings / StepWrecks 之后、总线之前：本步的 evt.enter / evt.leave 本步处理）。没有斑块时 O(1)。</summary>
        internal static void StepMatter(ref CombatData d, float dt)
        {
            // EMG2-MAT-02：酸沼是虚拟世界源（不占斑块槽），有酸沼时同样要走接触（evt.enter / leave 与 PrevMatterMask）
            bool marsh = d.AcidShapes.Length > 0 && EmgPatchesOn(ref d);
            StepConduct(ref d, dt); // EMG2-RULE-01：传导节拍（每 15 步一次，按步序号对齐；没有斑块也要衰减 / 传单位身上的电荷与磁化）
            if (d.EmgS[0].PatchLive <= 0 && !marsh)
            {
                return;
            }
            if (d.EmgS[0].PatchLive > 0)
            {
                StepPatchTicks(ref d);
            }
            if (d.EmgS[0].PatchLive > 0 || marsh)
            {
                StepPatchContact(ref d, dt);
            }
            else
            {
                FlushMatterLeave(ref d);
            }
        }

        /// <summary>最后一块斑块消失的那一步：还记着物质掩码的单位当场发 evt.leave 并清掩码（之后没有斑块就不再走接触，O(1)）。一次 O(单位)，只在斑块层清空时。</summary>
        private static void FlushMatterLeave(ref CombatData d)
        {
            var none = new NativeList<int2>(1, Allocator.Temp);
            for (int u = 0; u < d.Count; u++)
            {
                EmgUnitState st = d.EmgUnit[u];
                uint prev = st.PrevMatterMask;
                if (prev == 0u)
                {
                    continue;
                }
                st.PrevMatterMask = 0u;
                d.EmgUnit[u] = st;
                if (d.IsAlive(u))
                {
                    EmgMatterEdges(ref d, u, 0u, prev, none);
                }
            }
            none.Dispose();
        }

        private static void StepPatchTicks(ref CombatData d)
        {
            double now = d.Scalars[0].Time;
            var due = new NativeList<PatchDue>(64, Allocator.Temp);
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && !IsLegacy(z) && z.NextTick <= now + 1e-9)
                {
                    due.Add(new PatchDue { T = z.NextTick, Id = z.EmgId, Index = i });
                }
            }
            if (due.Length == 0)
            {
                due.Dispose();
                return;
            }
            due.Sort();
            int n = math.min(due.Length, PatchPerStepOf(ref d));
            for (int k = 0; k < n; k++)
            {
                int i = due[k].Index;
                CombatZone z = d.Patches[i];
                if (!PatchAlive(z) || z.EmgId != due[k].Id)
                {
                    continue; // 本步被合并 / 挤掉
                }
                float elapsed = (float)math.clamp(now - (z.NextTick - z.TickInterval), 0.0, 4.0 * math.max(z.TickInterval, 0.01f));
                TickPatch(ref d, i, elapsed, now);
                z = d.Patches[i];
                if (PatchAlive(z))
                {
                    z.NextTick += z.TickInterval;
                    if (z.NextTick <= now)
                    {
                        z.NextTick = now + z.TickInterval;
                    }
                    d.Patches[i] = z;
                }
            }
            due.Dispose();
        }

        private static void TickPatch(ref CombatData d, int i, float elapsed, double now)
        {
            CombatZone z = d.Patches[i];
            EmgMatter m = MatterOf(ref d, z.Matter);
            if (PatchWorld(z))
            {
                return; // 常驻世界源：不衰减、不合并（由来源控制）
            }
            double life = math.max(1e-3, z.Until - z.Born);
            double left = z.Until - now;
            bool expire = left <= 0;
            float oldR = z.Radius;
            if (z.Growth > 0f && !IsLegacy(z))
            {
                // EMG2-RECL-01（蔓延）：液体斑块按节拍向四周扩到目标半径 R0（份量不变），到了就停
                z.Radius = math.min(z.R0, z.Radius + z.Growth * elapsed);
                if (z.Radius >= z.R0 - 1e-4f)
                {
                    z.Growth = 0f;
                }
            }
            switch (m.Decay)
            {
                case EmgMatterConst.DecayDilute:
                    z.Radius = math.min(PatchMaxRadiusOf(ref d), z.Radius + m.Spread * elapsed);
                    z.Conc = z.Radius > 0f ? math.min(z.Conc, (z.R0 / z.Radius) * (z.R0 / z.Radius)) : 0f;
                    expire |= z.Conc < GasMinConcOf(ref d);
                    break;
                case EmgMatterConst.DecayKeep:
                    break;
                default:
                    // 蒸干 / 燃尽 / 吸热 / 冷却：存续最后 25% 半径线性缩到 0（“快干了”可读，FG19B §8.2）
                    float f = (float)(left / (0.25 * life));
                    if (f < 1f)
                    {
                        if (z.Growth > 0f)
                        {
                            z.R0 = z.Radius; // 蔓延扩到一半就开始干缩：停扩，从当前半径开始缩（R0 还是目标半径的话这一拍半径会跳大）
                            z.Growth = 0f;
                        }
                        z.Radius = z.R0 * math.max(0f, f);
                    }
                    break;
            }
            if (m.Decay == EmgMatterConst.DecayBurn && m.Emit != 0 && m.EmitEvery > 0f)
            {
                z.BurnSeconds += elapsed;
                if (z.BurnSeconds >= m.EmitEvery)
                {
                    z.BurnSeconds -= m.EmitEvery;
                    d.Patches[i] = z;
                    // 燃烧排烟是物质自身属性（FG19B §1.2 明火“每 2 秒向气体层排 1 份烟”），原地产物：链深不加
                    EmgRequestPatch(ref d, m.Emit, z.Pos, math.max(z.Radius, 0.5f), 1f, 0f, z.Owner, z.EmgChain, z.EmgDepth);
                    z = d.Patches[i];
                }
            }
            if (m.Solid == EmgMatterConst.SolidBlock && z.Kinetic >= z.Hp)
            {
                z.Hp = 0f; // EMG2-REACT-01（FG19B §1.4 第 7 条、§2.5“动能（E 通用）”）：阻路固态没有专属 E 行，累计动能到耐久即碎，无产物
            }
            if (m.Solid != 0 && z.Hp <= 0f)
            {
                expire = true;
            }
            if (expire || z.Radius <= 0.05f || z.Amount <= 0.01f)
            {
                d.Patches[i] = z;
                if (!(m.Solid != 0 && z.Hp <= 0f))
                {
                    EmgExpireRules(ref d, i); // EMG2-RULE-01：event_matter（evt.expire × 本物质：V01 凝结、V02 冰化）在生产点内联求值
                }
                KillPatch(ref d, i);
                EmgScalars es = d.EmgS[0];
                es.PatchesExpired++;
                d.EmgS[0] = es;
                return;
            }
            if (EmgHash.CellOf(z.Pos.x - z.Radius) < EmgHash.CellOf(z.Pos.x - oldR) || EmgHash.CellOf(z.Pos.x + z.Radius) > EmgHash.CellOf(z.Pos.x + oldR)
                || EmgHash.CellOf(z.Pos.y - z.Radius) < EmgHash.CellOf(z.Pos.y - oldR) || EmgHash.CellOf(z.Pos.y + z.Radius) > EmgHash.CellOf(z.Pos.y + oldR))
            {
                EmgScalars es = d.EmgS[0];
                es.HashDirty = 1; // 外接格变大才重建（缩小时旧登记仍覆盖，查询按真实几何判）
                d.EmgS[0] = es;
            }
            d.Patches[i] = z;
            if (m.StructDps > 0f)
            {
                // EMG2-MAT-02（ADR ⑤ / DEBT-EMG2MAT01-02）：建筑 / 传送带 / 管线吃斑块——按斑块聚合进设施受击脏列表（热更层按格结算，不遍历斑块）；明火另标燃烧（烧产线）
                AddStructHit(ref d, z.EmgId, z.Pos, z.Radius, m.StructDps * elapsed, z.Matter, z.Matter == EmgMatterConst.Fire ? EmgMatterConst.HitFire : (byte)0);
            }
            if (EmgPatchRules(ref d, i, now))
            {
                return; // EMG2-RULE-01：本块被规则转化 / 消失（同一宿主一个节拍只结算一条），本拍不再合并
            }
            EmgTickNeighbors(ref d, i, m);
        }

        /// <summary>邻接（空间哈希，最多 emg.patch.max_neighbors 块最近的）：同物质同层重叠比 ≥ 阈值的并进来；液氮与温度 &gt; 0 度的斑块重叠时每节拍份量 −5%。</summary>
        private static void EmgTickNeighbors(ref CombatData d, int i, in EmgMatter m)
        {
            bool absorb = m.Decay == EmgMatterConst.DecayAbsorb;
            if (m.MergeOverlap <= 0f && !absorb)
            {
                return;
            }
            EmgHashEnsure(ref d);
            CombatZone z = d.Patches[i];
            int cap = PatchNeighborsOf(ref d);
            var near = new NativeList<int2>(cap + 1, Allocator.Temp); // (下标, 距离 × 1000)
            int x0 = EmgHash.CellOf(z.Pos.x - z.Radius), x1 = EmgHash.CellOf(z.Pos.x + z.Radius);
            int y0 = EmgHash.CellOf(z.Pos.y - z.Radius), y1 = EmgHash.CellOf(z.Pos.y + z.Radius);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
                    {
                        continue;
                    }
                    do
                    {
                        if ((byte)d.EmgHash.Entries[e].w != EmgHash.KindPatch)
                        {
                            continue;
                        }
                        int j = d.EmgHash.Refs[e];
                        if (j == i || j >= d.Patches.Length || ContainsX(near, j))
                        {
                            continue;
                        }
                        CombatZone o = d.Patches[j];
                        if (!PatchAlive(o) || o.Layer != z.Layer || IsLegacy(o))
                        {
                            continue; // EMG2-MAT-02：迁入斑块不合并、不被吸收（按区域载荷与时长结算）
                        }
                        double dist = math.distance(o.Pos, z.Pos);
                        if (dist >= o.Radius + z.Radius)
                        {
                            continue;
                        }
                        InsertNearest(ref d, ref near, new int2(j, (int)math.min(dist * 1000.0, int.MaxValue)), cap);
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            for (int k = 0; k < near.Length; k++)
            {
                int j = near[k].x;
                CombatZone o = d.Patches[j];
                z = d.Patches[i];
                if (!PatchAlive(o) || !PatchAlive(z))
                {
                    continue;
                }
                if (absorb && o.Temp > 0f)
                {
                    z.Amount *= 0.95f;
                    d.Patches[i] = z;
                }
                if (o.Matter == z.Matter && !PatchWorld(o) && m.MergeOverlap > 0f && OverlapRatio(z.Pos, z.Radius, o.Pos, o.Radius) >= m.MergeOverlap
                    && EmgMergeInto(ref d, i, o, false))
                {
                    KillPatch(ref d, j);
                    EmgScalars es = d.EmgS[0];
                    es.PatchesMerged++;
                    d.EmgS[0] = es;
                }
            }
            near.Dispose();
        }

        private static bool ContainsX(NativeList<int2> list, int x)
        {
            for (int k = 0; k < list.Length; k++)
            {
                if (list[k].x == x)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 按 (距离, 斑块 Id) 升序插入并截到 <paramref name="cap"/> 个（确定性：与哈希遍历顺序无关）。同距离按 Id 而不按下标：
        /// 快照只写活斑块（读档后墓碑被压实、下标变了），按下标断平局会让读档续跑与连续跑选中不同的斑块（FGT-EMG-042）。
        /// </summary>
        private static void InsertNearest(ref CombatData d, ref NativeList<int2> list, int2 v, int cap)
        {
            int pos = list.Length;
            while (pos > 0 && (list[pos - 1].y > v.y || (list[pos - 1].y == v.y && d.Patches[list[pos - 1].x].EmgId > d.Patches[v.x].EmgId)))
            {
                pos--;
            }
            if (pos >= cap)
            {
                return;
            }
            list.Add(v);
            for (int k = list.Length - 1; k > pos; k--)
            {
                list[k] = list[k - 1];
            }
            list[pos] = v;
            if (list.Length > cap)
            {
                list.ResizeUninitialized(cap);
            }
        }

        // ─────────────────────────────── 单位接触 ───────────────────────────────

        /// <summary>
        /// 单位按槽位分桶（桶数 = 节拍 ÷ 步长，0.25 秒 ≈ 15 桶），每步只处理一桶；倍速只改每帧步数、不改步长，所以 0.5x～3x 结果一致（FGR-EMG-213）。
        /// 建筑（血量在热更层）不在这里结算——按 ADR ⑤ 聚合进受击脏列表归 EMG2-MAT-02（DEBT-EMG2MAT01-02）；炮塔血量在内核，直接结算。
        /// </summary>
        private static void StepPatchContact(ref CombatData d, float dt)
        {
            float tick = PatchTickOf(ref d);
            int buckets = math.max(1, (int)math.round(tick / math.max(dt, 1e-4f)));
            int bucket = (int)(d.Scalars[0].Steps % buckets);
            float elapsed = buckets * dt;
            EmgHashEnsure(ref d);
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            for (int u = bucket; u < d.Count; u += buckets)
            {
                if (!d.IsAlive(u) || d.Kind[u] == (byte)CombatUnitKind.Structure)
                {
                    continue;
                }
                hits.Clear();
                bool air = IsAir(ref d, u);
                uint mask = PatchesAt(ref d, d.Pos[u], air, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
                EmgUnitState st = d.EmgUnit[u];
                uint prev = st.PrevMatterMask;
                if (mask != prev)
                {
                    st.PrevMatterMask = mask;
                    d.EmgUnit[u] = st;
                    EmgMatterEdges(ref d, u, mask, prev, hits);
                }
                for (int k = 0; k < hits.Length && d.IsAlive(u); k++)
                {
                    if (hits[k].x >= 0 && !IsLegacy(d.Patches[hits[k].x]))
                    {
                        EmgPatchTouch(ref d, u, hits[k].x, elapsed); // 迁入斑块的接触由区域节拍结算（不双算）
                    }
                }
            }
            hits.Dispose();
        }

        /// <summary>evt.enter / evt.leave（单位宿主；Code = 物质码，Other = 进入的斑块 Id，位置 = 斑块中心 / 单位处）。只有本单位程序读这两个事件码时才入队（按需发，FGR-EMG-062）。</summary>
        private static void EmgMatterEdges(ref CombatData d, int u, uint mask, uint prev, NativeList<int2> hits)
        {
            uint entered = mask & ~prev, left = prev & ~mask;
            while (entered != 0u)
            {
                int b = math.tzcnt(entered);
                entered &= entered - 1u;
                int p = HitOfMatter(ref d, hits, b);
                if (p < 0)
                {
                    p = FirstPatchOfMatterAt(ref d, d.Pos[u], b); // 只有迁入斑块带来的物质（不在接触名额里）：按点再查一次（只在进入的那一步）
                }
                EmgUnitEvent(ref d, EmgEventKind.Enter, u, p >= 0 ? d.Patches[p].EmgId : 0, 0f, 0f, (ushort)b, p >= 0 ? d.Patches[p].Pos : d.Pos[u], EmgEntKind.Patch);
            }
            while (left != 0u)
            {
                int b = math.tzcnt(left);
                left &= left - 1u;
                EmgUnitEvent(ref d, EmgEventKind.Leave, u, 0, 0f, 0f, (ushort)b, d.Pos[u], EmgEntKind.Patch);
            }
        }

        private static int HitOfMatter(ref CombatData d, NativeList<int2> hits, int matter)
        {
            for (int k = 0; k < hits.Length; k++)
            {
                if (d.Patches[hits[k].x].Matter == matter)
                {
                    return hits[k].x;
                }
            }
            return -1;
        }

        /// <summary>
        /// 点 <paramref name="p"/> 所在的活斑块（最多 <paramref name="cap"/> 块最近的，按 (距离, 下标) 升序写进 <paramref name="hits"/>）；返回物质掩码（位 = 物质码）。
        /// <paramref name="airborne"/> = 离地实体：地面层斑块不算（FG19B §1.4 第 1 条）。O(本格登记数)。
        /// </summary>
        internal static uint PatchesAt(ref CombatData d, double2 p, bool airborne, ref NativeList<int2> hits, int cap, bool legacyInHits = true)
        {
            // EMG2-MAT-02（ADR ⑤ / ⑭ #13；FG19B §1.4 第 11 条）：酸沼 = 常驻 mat.acid 虚拟世界源——不实体化为斑块、不进 EmgHash、不占槽，按点是否在危害区查询；
            // 只并进物质掩码（evt.enter / leave、PrevMatterMask、规则引擎读），接触伤害仍是 StepAcidMarsh（不另挂沾染，不双算）；离地实体不吃（地面层）。
            uint mask = !airborne && d.AcidShapes.Length > 0 && InAcid(ref d, p) ? 1u << EmgMatterConst.Acid : 0u;
            if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(p.x), EmgHash.CellOf(p.y)), out int e, out NativeParallelMultiHashMapIterator<long> it))
            {
                return mask;
            }
            do
            {
                if ((byte)d.EmgHash.Entries[e].w != EmgHash.KindPatch)
                {
                    continue;
                }
                int j = d.EmgHash.Refs[e];
                if (j >= d.Patches.Length)
                {
                    continue;
                }
                CombatZone z = d.Patches[j];
                if (!PatchAlive(z) || (airborne && z.Layer == EmgMatterConst.LayerGround))
                {
                    continue;
                }
                double dist = math.distance(z.Pos, p);
                if (dist > z.Radius)
                {
                    continue;
                }
                mask |= 1u << z.Matter;
                if (!legacyInHits && (z.PFlags & EmgMatterConst.FlagLegacy) != 0)
                {
                    continue; // 迁入斑块只并进掩码、不占接触名额（它们的接触由区域节拍结算，占名额会挤掉真物质斑块的沾染）
                }
                InsertNearest(ref d, ref hits, new int2(j, (int)math.min(dist * 1000.0, int.MaxValue)), cap);
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
            return mask;
        }

        /// <summary>单位在一块斑块里结算一个接触节拍：沾染位（保留 coatSeconds；状态效果沿用现有标签）、直接接触伤害（DamageFrom，evt.hurt Code = 斑块 4；同阵营 × friendly_scale）、直接减速。</summary>
        private static void EmgPatchTouch(ref CombatData d, int u, int p, float elapsed)
        {
            CombatZone z = d.Patches[p];
            EmgMatter m = MatterOf(ref d, z.Matter);
            int src = z.Owner > 0 ? d.SlotOf(z.Owner) : -1;
            if (m.CoatBit >= 0 && m.CoatBit < 31)
            {
                ApplyStatus(ref d, u, 1u << m.CoatBit, m.CoatSeconds, 0f, 0f, 0f, src);
            }
            if (z.Matter == EmgMatterConst.Molten && d.IsAlive(u))
            {
                EmgCoatReact(ref d, u, 0u, MoltenContactHeat, 0f, src); // EMG2-REACT-02（FG19B C01 / C05“进入明火 / 熔融斑块”，DEBT-EMG2REACT01-04）：沾油 / 带酸的单位踩熔融金属
            }
            if (m.ContactSlow > 0f && d.IsAlive(u))
            {
                ApplyStatus(ref d, u, CombatConst.StatusBitZoneSlow, elapsed + 0.05f, 0f, m.ContactSlow, 0f, src);
            }
            if (m.ContactDps > 0f && d.IsAlive(u))
            {
                float dmg = m.ContactDps * elapsed;
                if (src >= 0 && d.Faction[src] == d.Faction[u])
                {
                    dmg *= d.Config.EmgFriendlyScale > 0f ? d.Config.EmgFriendlyScale : 1f;
                }
                if (dmg > 0f)
                {
                    DamageFrom(ref d, u, dmg, src, EmgCode.SrcPatch);
                }
            }
        }

        // ─────────────────────────────── 弹体 ───────────────────────────────

        /// <summary>
        /// 弹体本步线段 Prev → <paramref name="next"/> 穿过的斑块（FGR-EMG-083 第 3 条“弹体同样可沾染”）：挂该物质的沾染位（每发每种只记一次：位已在就不重复）；
        /// 终点所在物质与上一步做差，发 evt.enter / evt.leave（宿主 = 这发载荷，位置 = 斑块中心）。抛射弹在空中不碰地面层。O(线段覆盖格内的斑块)。
        /// </summary>
        internal static void EmgProjMatter(ref CombatData d, ref CombatProjectile pr, double2 next, bool airborne)
        {
            EmgHashEnsure(ref d);
            double2 a = pr.Prev;
            int x0 = EmgHash.CellOf(math.min(a.x, next.x)), x1 = EmgHash.CellOf(math.max(a.x, next.x));
            int y0 = EmgHash.CellOf(math.min(a.y, next.y)), y1 = EmgHash.CellOf(math.max(a.y, next.y));
            uint mask = 0u;
            int enterPatch = -1;
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(cx, cy), out int e, out NativeParallelMultiHashMapIterator<long> it))
                    {
                        continue;
                    }
                    do
                    {
                        if ((byte)d.EmgHash.Entries[e].w != EmgHash.KindPatch)
                        {
                            continue;
                        }
                        int j = d.EmgHash.Refs[e];
                        if (j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone z = d.Patches[j];
                        if (!PatchAlive(z) || (airborne && z.Layer == EmgMatterConst.LayerGround))
                        {
                            continue;
                        }
                        double r2 = (double)z.Radius * z.Radius;
                        bool crossed = math.distancesq(a, z.Pos) <= r2 || math.distancesq(next, z.Pos) <= r2 || SegmentCircleT(a, next, z.Pos, z.Radius) >= 0;
                        if (!crossed)
                        {
                            continue;
                        }
                        EmgMatter m = MatterOf(ref d, z.Matter);
                        if (m.CoatBit >= 0 && m.CoatBit < 31)
                        {
                            pr.Coat |= 1u << m.CoatBit; // EMG2-REACT-02（DEBT-EMG2MAT02-03）：迁入斑块同样给弹体沾染（预期差异，M10-BASELINE）
                        }
                        EmgProjPatchRules(ref d, ref pr, z, next); // EMG2-FLD-01：失锁 / 去火 / 拖尾 / 玻化层与冰面反射
                        if (math.distancesq(next, z.Pos) <= r2)
                        {
                            uint bit = 1u << z.Matter;
                            if ((mask & bit) == 0u && (pr.PrevMatterMask & bit) == 0u && (enterPatch < 0 || z.EmgId < d.Patches[enterPatch].EmgId))
                            {
                                enterPatch = j;
                            }
                            mask |= bit;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            uint prev = pr.PrevMatterMask;
            if (mask == prev)
            {
                return;
            }
            pr.PrevMatterMask = mask;
            if (pr.Prog < 0)
            {
                return;
            }
            uint entered = mask & ~prev, left = prev & ~mask;
            while (entered != 0u)
            {
                int b = math.tzcnt(entered);
                entered &= entered - 1u;
                int p = enterPatch >= 0 && d.Patches[enterPatch].Matter == b ? enterPatch : FirstPatchOfMatterAt(ref d, next, b);
                EmgProjEvent(ref d, ref pr, EmgEventKind.Enter, p >= 0 ? d.Patches[p].EmgId : 0, EmgEntKind.Patch, 0f, 0f, (ushort)b, p >= 0 ? d.Patches[p].Pos : next);
            }
            while (left != 0u)
            {
                int b = math.tzcnt(left);
                left &= left - 1u;
                EmgProjEvent(ref d, ref pr, EmgEventKind.Leave, 0, EmgEntKind.Patch, 0f, 0f, (ushort)b, next);
            }
        }

        private static int FirstPatchOfMatterAt(ref CombatData d, double2 p, int matter)
        {
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            PatchesAt(ref d, p, false, ref hits, EmgMatterConst.ContactPerUnit);
            int r = HitOfMatter(ref d, hits, matter);
            hits.Dispose();
            return r;
        }

        /// <summary>命中时把弹体沾上的物质带给目标（FGR-EMG-083 第 3 条）：按各物质的沾染保留秒挂上（不认阵营）。</summary>
        internal static void EmgCoatOnHit(ref CombatData d, int t, in CombatProjectile pr)
        {
            uint left = pr.Coat;
            int src = d.SlotOf(pr.Owner);
            if ((pr.FieldOnce & EmgFieldConst.OnceCharged) != 0)
            {
                EmgFieldRule cr = FieldRule(ref d, EmgFieldConst.CoronaPayload);
                if (FieldLive(cr) && cr.Amount > 0f)
                {
                    EmgAddAcc(ref d, t, EmgPhaseConst.Charge, cr.Amount, pr.Owner); // EMG2-FLD-01（FGR-EMG-120）：穿过己方电晕场带的电，命中注入 en.elec
                }
            }
            sbyte cryoBit = MatterOf(ref d, EmgMatterConst.Cryo).CoatBit;
            if (cryoBit >= 0 && cryoBit < 32 && (pr.Coat & (1u << cryoBit)) != 0u)
            {
                EmgFieldRule kr = FieldRule(ref d, EmgFieldConst.CryoPayload);
                if (FieldLive(kr) && kr.Amount > 0f)
                {
                    EmgAddAcc(ref d, t, EmgPhaseConst.Chill, kr.Amount, pr.Owner); // EMG2-FLD-01 修复轮（FG19B fieldrule_cryo_payload“命中 phs.chill +0.3”）：穿过液氮雾带的寒，命中注入寒量（沾染位本身不进相态累积）
                }
            }
            while (left != 0u)
            {
                int b = math.tzcnt(left);
                left &= left - 1u;
                float sec = 3f;
                for (int m = 1; m <= EmgMatterConst.Count; m++)
                {
                    EmgMatter mm = d.EmgMatters[m];
                    if (mm.CoatBit == b)
                    {
                        sec = mm.CoatSeconds > 0f ? mm.CoatSeconds : sec;
                        break;
                    }
                }
                ApplyStatus(ref d, t, 1u << b, sec, 0f, 0f, 0f, src);
            }
        }

        // ─────────────────────────────── 残骸崩解（DEBT-EMG1ENT01-01）───────────────────────────────

        /// <summary>残骸崩解（满 256 让位 / 野外到期）：在原地转成一块金属碎屑斑块（FGR-EMG-113“转化，不消失”）；斑块层没开时只计数（阶段 1 行为）。</summary>
        internal static void EmgWreckToDebris(ref CombatData d, in EmgWreck k)
        {
            if (!EmgPatchesOn(ref d))
            {
                return;
            }
            if (EmgRequestPatch(ref d, EmgMatterConst.Debris, k.Pos, math.max(k.Radius, 0.5f), 1f, 0f, 0, k.Chain, 0, 0, EmgMatterConst.PhaseFromWreck))
            {
                EmgScalars es = d.EmgS[0];
                es.WrecksToDebris++;
                d.EmgS[0] = es;
            }
        }

        // ─────────────────────────────── 排放写端（阶段 2：斑块）───────────────────────────────

        /// <summary>排放指令（mat.* 写端）在斑块层开着时生成斑块：A = 半径、B = 秒数（fg.TbPortCarrier）、份量 = 声明幅度（没写取 1）。返回 true = 已按斑块处理（调用方不再落驻留区）。</summary>
        internal static bool EmgEmitPatch(ref CombatData d, in EmgInstr x, double2 pos, float radius, float seconds, int ownerSlot, int chain, byte depth)
        {
            byte matter = EmgMatterConst.MatterOfPort(x.Port);
            if (matter == 0 || !EmgPatchesOn(ref d))
            {
                return false;
            }
            int owner = ownerSlot >= 0 && ownerSlot < d.Count ? d.Id[ownerSlot] : 0;
            EmgRequestPatch(ref d, matter, pos, radius, x.Amount > 0f ? x.Amount : 1f, seconds, owner, chain, depth);
            return true;
        }
    }
}
