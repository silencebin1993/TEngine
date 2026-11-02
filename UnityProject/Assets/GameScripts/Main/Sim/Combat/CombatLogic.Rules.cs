using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-RULE-01（FG19 FGR-EMG-084 / 085 / 088 / 090～093；FG19B 第 1.4、2 节；ADR-EMG-001 ⑥ ⑬ ⑯）：通道规则引擎与能量传导——全部逐斑块 / 逐单位逻辑在这里（Main/Sim）。
    /// - 规则：fg.TbChannelRule 编成 <see cref="EmgRule"/>（≤ 256），按物质码索引、索引内按优先级升序；同一宿主一个节拍只结算优先级最小的一条，其余成立的留到下一节拍（计数 RulesDeferred）。
    ///   温度 / 重叠 / 动能条件在斑块节拍求值；电荷条件在传导节拍（BFS 之后、衰减之前）求值（FG19B E14“每传导节拍放电”）；事件条件在生产点内联求值（斑块寿命到 / 单位过热）。
    ///   每次结算：可靠计数 + 1（按 named 的稳定键）、因果链记“规则”、按需入队 evt.react（Code = 计数槽下标，Value = 累计次数）。内核只认码与阈值，不认规则名（FGR-EMG-013）。
    /// - 传导（每 emg.conduct.tick = 0.25 秒，按步序号对齐，存读档 / 倍速不改相位）：重叠斑块热交换 + 高于常温的回温；电沿导电斑块 / 残骸有界 BFS（每跳 × 0.7 × 接收导电，≤ 6 跳，
    ///   低于 q_min 停，节点访问 ≤ 256、超出的能量留在节点上顺延）；站在热斑块里的单位吃热（进武器积热，过热停火）；单位电荷 / 磁化衰减；磁化沿磁性接触在实体之间传。
    /// - 动能：不存储、不传播，只被读——落到固态斑块（冰 / 玻化层 / 冷渣 / 固化胶块）上累计，越过耐久由 E18～E20 结算（FGR-EMG-093）。
    /// - AI 避险（ADR ⑯）：教义自选站位（守点交战走位）的期望方向叠加有害斑块排斥力；玩家显式命令不施加、下令时只警告一次。
    /// </summary>
    public static partial class CombatLogic
    {
        // ─────────────────────────────── 配置 ───────────────────────────────

        private static float ConductTickOf(ref CombatData d) => d.Config.EmgConductTick > 0f ? d.Config.EmgConductTick : EmgMatterConst.DefaultTick;
        private static float FalloffOf(ref CombatData d) => d.Config.EmgConductFalloff > 0f ? d.Config.EmgConductFalloff : EmgRuleConst.DefaultFalloff;
        private static float QMinOf(ref CombatData d) => d.Config.EmgConductQMin > 0f ? d.Config.EmgConductQMin : EmgRuleConst.DefaultQMin;
        private static float ShareOf(ref CombatData d) => d.Config.EmgConductShare > 0f ? d.Config.EmgConductShare : EmgRuleConst.DefaultUnitShare;
        private static float DecayOf(ref CombatData d) => d.Config.EmgConductDecay > 0f ? d.Config.EmgConductDecay : EmgRuleConst.DefaultPatchDecay;
        private static float BigROf(ref CombatData d) => d.Config.EmgConductBigR > 0f ? d.Config.EmgConductBigR : EmgRuleConst.DefaultBigRadius;
        private static int HopsOf(ref CombatData d) => d.Config.EmgConductHops > 0 ? d.Config.EmgConductHops : EmgRuleConst.DefaultMaxHops;
        private static int VisitsOf(ref CombatData d) => d.Config.EmgConductVisits > 0 ? d.Config.EmgConductVisits : EmgRuleConst.DefaultMaxVisits;
        private static float AmbientOf(ref CombatData d) => d.Config.EmgHeatAmbient != 0f ? d.Config.EmgHeatAmbient : EmgRuleConst.DefaultAmbient;
        private static float ExchangeOf(ref CombatData d) => d.Config.EmgHeatExchange > 0f ? d.Config.EmgHeatExchange : EmgRuleConst.DefaultExchange;
        private static float HeatGainOf(ref CombatData d) => d.Config.EmgHeatGain > 0f ? d.Config.EmgHeatGain : EmgRuleConst.DefaultPatchGain;
        private static float CoolRateOf(ref CombatData d) => d.Config.EmgHeatCool > 0f ? d.Config.EmgHeatCool : EmgRuleConst.DefaultCoolRate;
        private const double HeatFixScale = 65536.0;

        private static long FixHeat(float v) => (long)math.round(v * HeatFixScale);

        private static float UnitScaleOf(ref CombatData d) => d.Config.EmgHeatUnitScale > 0f ? d.Config.EmgHeatUnitScale : EmgRuleConst.DefaultUnitOverheatScale;
        private static float UnitGainOf(ref CombatData d) => d.Config.EmgHeatUnitGain > 0f ? d.Config.EmgHeatUnitGain : EmgRuleConst.DefaultUnitGain;

        /// <summary>传导节拍折合几步（0.25 秒 ÷ 步长 ≈ 15）；倍速只改每帧步数、不改步长，所以相位与结果在 0.5x～3x 下一致。</summary>
        internal static int ConductSteps(ref CombatData d, float dt) => math.max(1, (int)math.round(ConductTickOf(ref d) / math.max(dt, 1e-4f)));

        /// <summary>规则在哪张索引里：a 侧物质（matter_matter / matter_energy）→ [0, 18)；b 侧物质（event_matter）→ [18, 36)；其余 pairKind（承接卡）−1。</summary>
        internal static int EmgRuleIndexSlot(in EmgRule r)
        {
            if (r.Pair == EmgRuleConst.PairMatterMatter || r.Pair == EmgRuleConst.PairMatterEnergy)
            {
                int m = EmgMatterConst.MatterOfPort(r.A);
                return m > 0 ? m : -1;
            }
            if (r.Pair == EmgRuleConst.PairEventMatter)
            {
                int m = EmgMatterConst.MatterOfPort(r.B);
                return m > 0 ? EmgMatterConst.Count + 1 + m : -1;
            }
            return -1;
        }

        private static bool EmgRulesOn(ref CombatData d) => d.EmgRules.IsCreated && d.EmgRules.Length > 0 && EmgPatchesOn(ref d);

        private static bool ChargeCond(byte c) => c == EmgRuleConst.CondChargeGe || c == EmgRuleConst.CondChargeGeConcGe;

        // ─────────────────────────────── 斑块节拍上的规则 ───────────────────────────────

        /// <summary>
        /// 斑块节拍（TickPatch 衰减之后）：按优先级找第一条成立的 matter_matter / matter_energy 规则并结算（电荷条件除外——在传导节拍求值）。
        /// 返回 true = 本块被转化 / 消失（调用方不再做合并）。迁入斑块与常驻世界源不做宿主（世界源“不被规则转化或消耗”，FG19B 第 1.4 节第 11 条）。
        /// </summary>
        internal static bool EmgPatchRules(ref CombatData d, int i, double now)
        {
            if (!EmgRulesOn(ref d))
            {
                return false;
            }
            CombatZone z = d.Patches[i];
            if (!PatchAlive(z) || PatchWorld(z) || IsLegacy(z))
            {
                return false;
            }
            int2 ix = d.EmgRuleIdx[z.Matter];
            int chosen = -1, partner = -1, satisfied = 0;
            bool condTimed = false;
            for (int k = ix.x; k < ix.x + ix.y; k++)
            {
                int ri = d.EmgRuleOrder[k];
                EmgRule r = d.EmgRules[ri];
                if (r.Op == EmgRuleConst.OpDeferred || ChargeCond(r.Cond))
                {
                    continue;
                }
                int other = -1;
                bool ok;
                if (r.Pair == EmgRuleConst.PairMatterMatter)
                {
                    other = EmgRulePartner(ref d, i, EmgMatterConst.MatterOfPort(r.B), r.C1, r.Cond == EmgRuleConst.CondOverlapDry, r.Op == EmgRuleConst.OpMoveOver);
                    ok = other >= 0;
                }
                else
                {
                    ok = EmgEnergyCond(ref d, ref z, r, now, ref condTimed);
                }
                if (!ok)
                {
                    continue;
                }
                satisfied++;
                if (chosen < 0)
                {
                    chosen = ri;
                    partner = other;
                }
            }
            if (condTimed)
            {
                d.Patches[i] = z; // 持续条件的起始时刻（CondSince）
            }
            if (chosen < 0)
            {
                return false;
            }
            if (satisfied > 1)
            {
                EmgScalars es = d.EmgS[0];
                es.RulesDeferred += satisfied - 1;
                d.EmgS[0] = es;
            }
            return EmgApplyRule(ref d, d.EmgRules[chosen], i, partner, now);
        }

        /// <summary>温度 / 浓度 / 动能 / 持续温度条件（宿主 = 斑块）。持续条件用 <see cref="CombatZone.CondSince"/> 记起始时刻（进快照）。</summary>
        private static bool EmgEnergyCond(ref CombatData d, ref CombatZone z, in EmgRule r, double now, ref bool timed)
        {
            switch (r.Cond)
            {
                case EmgRuleConst.CondTempGe: return z.Temp >= r.C1;
                case EmgRuleConst.CondTempLt: return z.Temp < r.C1;
                case EmgRuleConst.CondTempGeConcLt: return z.Temp >= r.C1 && z.Conc < r.C2;
                case EmgRuleConst.CondTempGeConcGe: return z.Temp >= r.C1 && z.Conc >= r.C2;
                case EmgRuleConst.CondKineticGe:
                {
                    float need = r.C1 > 0f ? r.C1 : MatterOf(ref d, z.Matter).Hp;
                    return need > 0f && z.Kinetic >= need;
                }
                case EmgRuleConst.CondTempLeFor:
                case EmgRuleConst.CondTempGtFor:
                {
                    bool hold = r.Cond == EmgRuleConst.CondTempLeFor ? z.Temp <= r.C1 : z.Temp > r.C1;
                    if (!hold)
                    {
                        if (z.CondSince != 0)
                        {
                            z.CondSince = 0;
                            timed = true;
                        }
                        return false;
                    }
                    if (z.CondSince == 0)
                    {
                        z.CondSince = now;
                        timed = true;
                    }
                    return now - z.CondSince >= r.C2 - 1e-6;
                }
                default: return false;
            }
        }

        /// <summary>
        /// matter_matter 的另一方：与宿主重叠比 ≥ <paramref name="minRatio"/> 的 <paramref name="matter"/> 斑块（任一层；世界源可做另一方，迁入斑块不参与），重叠比最大者、平局取 Id 小。
        /// <paramref name="dry"/> = 要求对方没被油覆盖（M12 / M13）；<paramref name="cover"/> = 浮油只对还没覆盖、或油还没铺到中心的水面成立（否则每拍重复计数）。
        /// </summary>
        private static int EmgRulePartner(ref CombatData d, int i, int matter, float minRatio, bool dry, bool cover)
        {
            if (matter <= 0)
            {
                return -1;
            }
            EmgHashEnsure(ref d);
            CombatZone z = d.Patches[i];
            int best = -1;
            double bestRatio = 0;
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
                        if (j == i || j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone o = d.Patches[j];
                        if (o.Matter != matter || IsLegacy(o) || (dry && (o.PFlags & EmgMatterConst.FlagOilCovered) != 0))
                        {
                            continue;
                        }
                        if (cover && (o.PFlags & EmgMatterConst.FlagOilCovered) != 0 && math.distance(o.Pos, z.Pos) < 0.05)
                        {
                            continue;
                        }
                        double ratio = OverlapRatio(z.Pos, z.Radius, o.Pos, o.Radius);
                        if (ratio + 1e-9 < minRatio || ratio <= 0)
                        {
                            continue;
                        }
                        if (ratio > bestRatio || (ratio == bestRatio && best >= 0 && o.EmgId < d.Patches[best].EmgId))
                        {
                            bestRatio = ratio;
                            best = j;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        // ─────────────────────────────── 结算 ───────────────────────────────

        /// <summary>结算一条规则（宿主斑块 <paramref name="i"/>，另一方 <paramref name="j"/>，−1 = 没有）。返回 true = 宿主被转化 / 消失。</summary>
        private static bool EmgApplyRule(ref CombatData d, in EmgRule r, int i, int j, double now)
        {
            CombatZone z = d.Patches[i];
            bool partnerWorld = j >= 0 && PatchWorld(d.Patches[j]);
            float a = z.Amount, b = j >= 0 ? d.Patches[j].Amount : 0f;
            // 热导致的原地转化：链与链深取热源（最热的重叠邻居）的，不加链深（FG19 第 4.2 节“原地转化取来源 Depth 的最大值”）
            if (r.Pair == EmgRuleConst.PairMatterEnergy && (r.Cond == EmgRuleConst.CondTempGe || r.Cond == EmgRuleConst.CondTempGeConcGe || r.Cond == EmgRuleConst.CondTempGeConcLt))
            {
                int src = EmgHottestNeighbor(ref d, i);
                if (src >= 0 && d.Patches[src].EmgChain > 0)
                {
                    CombatZone s = d.Patches[src];
                    z.EmgChain = s.EmgChain;
                    z.EmgDepth = (byte)math.max((int)z.EmgDepth, (int)s.EmgDepth);
                    if (s.Owner > 0)
                    {
                        z.Owner = s.Owner;
                    }
                    d.Patches[i] = z;
                }
            }
            double2 pos = z.Pos;
            int chain = z.EmgChain;
            byte depth = z.EmgDepth;
            int owner = z.Owner;
            bool hostGone = false;
            float consumedB = 0f;
            switch (r.Op)
            {
                case EmgRuleConst.OpConvert:
                {
                    float frac = 1f;
                    if (j >= 0 && !partnerWorld && r.ConsB > 0f && r.ConsB * a > b + 1e-4f)
                    {
                        frac = math.saturate(b / (r.ConsB * a)); // 另一方不够付：只转化够付的那一份（M04“液氮不够只冻结一部分”）
                    }
                    float p1 = r.P1 != 0f ? r.P1 : 1f;
                    float amount = p1 * a * frac + r.P2 * b;
                    consumedB = EmgConsumePartner(ref d, j, partnerWorld, r.ConsB, a * frac);
                    if (frac >= 0.999f)
                    {
                        EmgConvertInPlace(ref d, i, r, amount, now);
                        hostGone = true;
                    }
                    else
                    {
                        z = d.Patches[i];
                        z.Amount = a * (1f - frac);
                        z.Radius *= math.sqrt(1f - frac);
                        d.Patches[i] = z;
                        EmgRuleSpawn(ref d, r.Product, pos, math.max(0.3f, z.Radius * math.sqrt(frac / math.max(1e-3f, 1f - frac))), amount, r.P3, r.Life, owner, chain, depth);
                    }
                    break;
                }
                case EmgRuleConst.OpMoveOver:
                {
                    CombatZone o = d.Patches[j];
                    z.Pos = o.Pos;
                    z.Radius = math.min(PatchMaxRadiusOf(ref d), math.max(z.Radius, o.Radius));
                    z.R0 = math.max(z.R0, z.Radius);
                    d.Patches[i] = z;
                    if (!partnerWorld)
                    {
                        o.PFlags |= EmgMatterConst.FlagOilCovered;
                        d.Patches[j] = o;
                    }
                    MarkHashDirty(ref d);
                    break;
                }
                case EmgRuleConst.OpDilute:
                {
                    float amt = math.min(a, r.P1 * b);
                    z.Amount = a - amt;
                    d.Patches[i] = z;
                    if (!partnerWorld)
                    {
                        EmgResizePatch(ref d, j, b + amt);
                    }
                    if (z.Amount <= 0.01f)
                    {
                        KillPatch(ref d, i);
                        hostGone = true;
                    }
                    break;
                }
                case EmgRuleConst.OpDouse:
                {
                    float wet = b * (float)OverlapRatio(z.Pos, z.Radius, d.Patches[j].Pos, d.Patches[j].Radius);
                    float used = math.min(a, math.max(0.01f, wet));
                    consumedB = partnerWorld ? used : math.min(b, used);
                    if (!partnerWorld)
                    {
                        EmgResizePatch(ref d, j, b - consumedB);
                    }
                    EmgResizePatch(ref d, i, a - used);
                    hostGone = !PatchAlive(d.Patches[i]);
                    break;
                }
                case EmgRuleConst.OpErode:
                {
                    z.Hp -= r.P1;
                    d.Patches[i] = z;
                    consumedB = EmgConsumePartner(ref d, j, partnerWorld, r.ConsB, a);
                    if (z.Hp <= 0f)
                    {
                        EmgConvertInPlace(ref d, i, r, r.P2 * a, now);
                        hostGone = true;
                    }
                    break;
                }
                case EmgRuleConst.OpSplit:
                {
                    float amt = r.P1 * a;
                    z.Temp = r.P2;
                    d.Patches[i] = z;
                    EmgResizePatch(ref d, i, a - amt);
                    EmgRuleSpawn(ref d, r.Product, pos, z.Radius, amt, r.P3, r.Life, owner, chain, depth);
                    hostGone = !PatchAlive(d.Patches[i]);
                    break;
                }
                case EmgRuleConst.OpSpawn:
                {
                    EmgRuleSpawn(ref d, r.Product, pos, r.P2 > 0f ? r.P2 : z.Radius, r.P1 * a, r.P3, r.Life, owner, chain, depth);
                    if (r.ConsA > 0f)
                    {
                        EmgResizePatch(ref d, i, a * (1f - r.ConsA));
                        hostGone = !PatchAlive(d.Patches[i]);
                    }
                    break;
                }
                case EmgRuleConst.OpBurst:
                {
                    float rad = math.min(z.Radius + r.P1, r.P2 > 0f ? r.P2 : z.Radius + r.P1);
                    KillPatch(ref d, i);
                    hostGone = true;
                    EmgUnitsInCircle(ref d, pos, rad, r.SideValue, owner, false, 0f);
                    EmgBurstPatches(ref d, pos, rad, r.P4, r.P3);
                    break;
                }
                case EmgRuleConst.OpArc:
                    EmgUnitsInCircle(ref d, pos, z.Radius, r.SideValue, owner, true, r.P1 * z.Charge);
                    break;
                case EmgRuleConst.OpFragment:
                {
                    KillPatch(ref d, i);
                    hostGone = true;
                    int n = math.max(1, (int)math.round(r.P1));
                    float off = (uint)math.hash(new int2(z.EmgId, n)) % 360u * (math.PI / 180f);
                    for (int f = 0; f < n; f++)
                    {
                        float ang = off + f * (2f * math.PI / n);
                        EmgSpawnFragment(ref d, pos, new float2(math.cos(ang), math.sin(ang)), owner, CombatFaction.Neutral, (byte)math.min(depth + 1, EmgConst.MaxDepth), chain,
                            r.P2, r.P3, r.P4);
                    }
                    break;
                }
                default:
                    return false;
            }
            if (r.By > 0)
            {
                float byAmount = r.ByAmount * (r.Pair == EmgRuleConst.PairMatterMatter ? (consumedB > 0f ? consumedB : b) : a);
                if (byAmount > 0.01f)
                {
                    EmgRuleSpawn(ref d, r.By, pos, math.max(0.5f, z.Radius), byAmount, r.ByTemp, r.ByLife, owner, chain, depth);
                }
            }
            if (r.Side == EmgRuleConst.SideDamage && r.SideValue > 0f && r.Op != EmgRuleConst.OpBurst && r.Op != EmgRuleConst.OpArc)
            {
                EmgUnitsInCircle(ref d, pos, math.max(0.5f, z.Radius), r.SideValue, owner, false, 0f);
            }
            EmgRuleSettle(ref d, r, chain, depth, owner, pos, z.EmgId);
            return hostGone;
        }

        /// <summary>
        /// 原地转化：物质 / 层 / 份量 / 温度 / 耐久 / 寿命换成产物的；面积乘 p4（&gt; 0 时）；导电不到 0.3 的产物清电荷（M14“带电水洼当场断电”）；动能、持续条件清零；
        /// 不加链深（FG19 第 4.2 节）。产物定温（明火）取物质初温。
        /// </summary>
        private static void EmgConvertInPlace(ref CombatData d, int i, in EmgRule r, float amount, double now)
        {
            CombatZone z = d.Patches[i];
            EmgMatter m = MatterOf(ref d, r.Product);
            if (!m.Valid || amount <= 0.01f)
            {
                KillPatch(ref d, i);
                return;
            }
            z.Matter = r.Product;
            z.Layer = m.Layer;
            z.Amount = amount;
            z.Temp = m.FixedTemp != 0 || r.P3 <= EmgRuleConst.KeepTemp ? (m.FixedTemp != 0 ? m.TempInit : z.Temp) : r.P3;
            if (r.P4 > 0f)
            {
                z.Radius *= math.sqrt(r.P4);
            }
            z.Radius = math.min(z.Radius, PatchMaxRadiusOf(ref d));
            z.R0 = z.Radius;
            z.Hp = m.Solid != 0 ? m.Hp : 0f;
            z.Kinetic = 0f;
            z.CondSince = 0;
            z.BurnSeconds = 0f;
            z.Conc = 1f;
            if (m.Conductive < EmgRuleConst.DefaultConductMinStrength)
            {
                z.Charge = 0f;
            }
            z.PFlags = (byte)(z.PFlags & ~EmgMatterConst.FlagOilCovered);
            float life = r.Life > 0f ? r.Life : m.LifeFor(amount) * (r.LifeMul > 0f ? r.LifeMul : 1f);
            z.Born = now;
            z.Until = now + life;
            z.NextTick = now + z.TickInterval;
            d.Patches[i] = z;
            MarkHashDirty(ref d);
        }

        /// <summary>扣另一方份量（consB ≥ 0：× a 份量；&lt; 0：b 自身的比例）。世界源不扣（视为无穷大），返回值仍是名义消耗量。</summary>
        private static float EmgConsumePartner(ref CombatData d, int j, bool world, float consB, float a)
        {
            if (j < 0 || consB == 0f)
            {
                return 0f;
            }
            float b = d.Patches[j].Amount;
            float amt = consB > 0f ? math.min(b, consB * a) : b * math.min(1f, -consB);
            if (!world)
            {
                EmgResizePatch(ref d, j, b - amt);
            }
            return amt;
        }

        /// <summary>改斑块份量（面积守恒缩放半径）；≤ 0.01 时消失。</summary>
        private static void EmgResizePatch(ref CombatData d, int j, float amount)
        {
            CombatZone o = d.Patches[j];
            if (!PatchAlive(o))
            {
                return;
            }
            if (amount <= 0.01f)
            {
                KillPatch(ref d, j);
                return;
            }
            float k = o.Amount > 0f ? amount / o.Amount : 1f;
            o.Radius = math.min(PatchMaxRadiusOf(ref d), o.Radius * math.sqrt(k));
            o.Amount = amount;
            d.Patches[j] = o;
            MarkHashDirty(ref d);
        }

        private static void MarkHashDirty(ref CombatData d)
        {
            EmgScalars es = d.EmgS[0];
            es.HashDirty = 1;
            d.EmgS[0] = es;
        }

        /// <summary>
        /// 规则产物新斑块（事件驱动的生成，链深 + 1；链深已到 3 不生成，记“链深截断”）。<paramref name="temp"/> ≤ −998 = 物质初温；<paramref name="life"/> ≤ 0 = 物质默认存续。
        /// 本步生成额度用完时走待生成表顺延（那时温度取物质初温）。
        /// </summary>
        private static void EmgRuleSpawn(ref CombatData d, byte matter, double2 pos, float radius, float amount, float temp, float life, int owner, int chain, byte depth)
        {
            if (matter == 0 || amount <= 0.01f)
            {
                return;
            }
            if (depth >= EmgConst.MaxDepth)
            {
                EmgChainLog(ref d, EmgChainNode.DepthCut, chain, depth, matter, owner, pos);
                return;
            }
            byte nd = (byte)(depth + 1);
            if (d.EmgS[0].PatchSpawnedStep < PatchSpawnPerStepOf(ref d))
            {
                int at = EmgPlacePatch(ref d, matter, pos, math.max(0.3f, radius), amount, life, owner, chain, nd, 0, 0);
                if (at >= 0 && temp > EmgRuleConst.KeepTemp && MatterOf(ref d, matter).FixedTemp == 0)
                {
                    CombatZone n = d.Patches[at];
                    if (n.Matter == matter)
                    {
                        n.Temp = temp;
                        d.Patches[at] = n;
                    }
                }
                return;
            }
            EmgRequestPatch(ref d, matter, pos, math.max(0.3f, radius), amount, life, owner, chain, nd);
        }

        /// <summary>计数 + 因果链“规则”+ 按需 evt.react（宿主 = 斑块，Code = 计数槽下标，Value = 累计次数）。</summary>
        private static void EmgRuleSettle(ref CombatData d, in EmgRule r, int chain, byte depth, int ownerId, double2 pos, int patchId)
        {
            int slot = EmgRuleHit(ref d, r.Key, ownerId, 0, pos);
            EmgScalars es = d.EmgS[0];
            es.RulesSettled++;
            d.EmgS[0] = es;
            EmgChainLog(ref d, EmgChainNode.Rule, chain, depth, r.Key, ownerId, pos, patchId, r.Product);
            if (slot >= 0)
            {
                EmgEntityEvent(ref d, EmgEventKind.React, EmgEntKind.Patch, patchId, EmgEntKind.None, 0, d.EmgRuleCount[slot], 0f, (ushort)slot, pos);
            }
        }

        /// <summary>与斑块 <paramref name="i"/> 重叠、比它热的邻居里最热的一块（热源；用于热导致的原地转化继承链）。O(本块覆盖格登记数)，只在结算时。</summary>
        private static int EmgHottestNeighbor(ref CombatData d, int i)
        {
            CombatZone z = d.Patches[i];
            int best = -1;
            float bt = z.Temp;
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
                        if (j == i || j >= d.Patches.Length)
                        {
                            continue;
                        }
                        CombatZone o = d.Patches[j];
                        if (!PatchAlive(o) || o.Temp <= bt || math.distance(o.Pos, z.Pos) >= o.Radius + z.Radius)
                        {
                            continue;
                        }
                        bt = o.Temp;
                        best = j;
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            return best;
        }

        /// <summary>
        /// 圈内地面单位（不分阵营：物质中立，FGR-EMG-083）：附带伤害 <paramref name="damage"/>（同阵营 × emg.matter.friendly_scale，走 DamageFrom，evt.hurt Code = 斑块）；
        /// <paramref name="arc"/> 时另给电荷累积量 <paramref name="charge"/>（FG19B E14：单位 phs.charge + 0.6 × 斑块电荷）。O(单位数)，只在规则结算时。
        /// </summary>
        private static void EmgUnitsInCircle(ref CombatData d, double2 pos, float radius, float damage, int ownerId, bool arc, float charge)
        {
            int src = ownerId > 0 ? d.SlotOf(ownerId) : -1;
            float fs = d.Config.EmgFriendlyScale > 0f ? d.Config.EmgFriendlyScale : 1f;
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || d.Kind[u] == (byte)CombatUnitKind.Structure || IsAir(ref d, u) || math.distance(d.Pos[u], pos) > radius + d.Radius[u] * 0.5f)
                {
                    continue;
                }
                if (arc && charge > 0f)
                {
                    EmgAddAcc(ref d, u, EmgPhaseConst.Charge, charge, src >= 0 && src < d.Count ? d.Id[src] : 0); // EMG2-PHS-01：电荷累积量统一入口（电抗、免疫窗、上限、满了麻痹）
                }
                if (damage > 0f)
                {
                    float dmg = src >= 0 && d.Faction[src] == d.Faction[u] ? damage * fs : damage;
                    if (dmg > 0f)
                    {
                        DamageFrom(ref d, u, dmg, src, EmgCode.SrcPatch);
                    }
                }
            }
        }

        /// <summary>二次起爆（topo.burst）：圈内斑块升温（定温源除外）、固态斑块累计动能。</summary>
        private static void EmgBurstPatches(ref CombatData d, double2 pos, float radius, float heat, float kinetic)
        {
            for (int j = 0; j < d.Patches.Length; j++)
            {
                CombatZone o = d.Patches[j];
                if (!PatchAlive(o) || IsLegacy(o) || PatchWorld(o) || math.distance(o.Pos, pos) > radius + o.Radius)
                {
                    continue;
                }
                EmgMatter m = MatterOf(ref d, o.Matter);
                if (m.FixedTemp == 0)
                {
                    o.Temp += heat;
                }
                if (m.Solid != 0)
                {
                    o.Kinetic += kinetic;
                }
                d.Patches[j] = o;
            }
        }

        /// <summary>有效抗性（ADR ⑦：档案值与运行时覆盖取大）；k：0 寒、1 热、2 电、3 腐蚀、4 磁、5 入侵。</summary>
        internal static float EmgResist(ref CombatData d, int u, int k)
        {
            EmgProfile p = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, u));
            EmgUnitState st = d.EmgUnit[u];
            float prof = k switch { 0 => p.ResistCold, 1 => p.ResistHeat, 2 => p.ResistElec, 3 => p.ResistCorrode, 4 => p.ResistMag, _ => p.ResistHack };
            byte ov = k switch { 0 => st.Res0, 1 => st.Res1, 2 => st.Res2, 3 => st.Res3, 4 => st.Res4, _ => st.Res5 };
            float floor = k == EmgPhaseConst.ResCorrode && d.PlayerAcidResist > 0f && d.Faction[u] == (byte)CombatFaction.Player && d.Kind[u] == (byte)CombatUnitKind.Machine
                ? d.PlayerAcidResist : 0f; // ADR ⑦：防腐涂层作己方机器腐蚀抗性下限
            return math.saturate(math.max(math.max(prof, ov / 255f), floor));
        }

        // ─────────────────────────────── 事件 × 物质（内联求值，不经总线）───────────────────────────────

        /// <summary>斑块自然到期（寿命到 / 稀释到底）：event_matter 里 a = evt.expire、b = 本物质的规则按优先级取第一条成立的结算（V01 凝结、V02 冰化）。</summary>
        internal static void EmgExpireRules(ref CombatData d, int i)
        {
            if (!EmgRulesOn(ref d))
            {
                return;
            }
            CombatZone z = d.Patches[i];
            if (!PatchAlive(z) || IsLegacy(z) || PatchWorld(z))
            {
                return;
            }
            int2 ix = d.EmgRuleIdx[EmgMatterConst.Count + 1 + z.Matter];
            for (int k = ix.x; k < ix.x + ix.y; k++)
            {
                EmgRule r = d.EmgRules[d.EmgRuleOrder[k]];
                bool ok = r.Op == EmgRuleConst.OpSpawn && (r.Cond == EmgRuleConst.CondExpire || (r.Cond == EmgRuleConst.CondExpireTempLt && z.Temp < r.C1));
                if (!ok)
                {
                    continue;
                }
                // 到期的产物落在地面层（V01“在地面层落下水斑块”），份量按到期时的份量
                EmgRuleSpawn(ref d, r.Product, z.Pos, math.max(0.5f, z.Radius), r.P1 * z.Amount, r.P3, r.Life, z.Owner, z.EmgChain, z.EmgDepth);
                EmgRuleSettle(ref d, r, z.EmgChain, z.EmgDepth, z.Owner, z.Pos, z.EmgId);
                return;
            }
        }

        /// <summary>单位过热（积热首次越过阈值）：站在 b 物质斑块里时按 event_matter 的 evt.overheat 规则喷蒸汽（V04 / V05），消耗那块斑块固定份量。</summary>
        internal static void EmgOverheatRules(ref CombatData d, int u)
        {
            if (!EmgRulesOn(ref d) || d.EmgS[0].PatchLive <= 0 || IsAir(ref d, u))
            {
                return;
            }
            EmgHashEnsure(ref d);
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            PatchesAt(ref d, d.Pos[u], false, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
            int bestRule = -1, bestPatch = -1;
            short bestPrio = short.MaxValue;
            for (int h = 0; h < hits.Length; h++)
            {
                CombatZone z = d.Patches[hits[h].x];
                int2 ix = d.EmgRuleIdx[EmgMatterConst.Count + 1 + z.Matter];
                for (int k = ix.x; k < ix.x + ix.y; k++)
                {
                    int ri = d.EmgRuleOrder[k];
                    EmgRule r = d.EmgRules[ri];
                    if (r.Op == EmgRuleConst.OpVent && r.Cond == EmgRuleConst.CondOverheatIn && r.Priority < bestPrio)
                    {
                        bestPrio = r.Priority;
                        bestRule = ri;
                        bestPatch = hits[h].x;
                        break;
                    }
                }
            }
            hits.Dispose();
            if (bestRule < 0)
            {
                return;
            }
            EmgRule br = d.EmgRules[bestRule];
            CombatZone bz = d.Patches[bestPatch];
            int owner = d.Id[u];
            int chain = bz.EmgChain;
            byte depth = bz.EmgDepth;
            EmgRuleSpawn(ref d, br.Product, d.Pos[u], br.P1, br.P2, br.P3, br.Life, owner, chain, depth);
            if (!PatchWorld(bz))
            {
                EmgResizePatch(ref d, bestPatch, bz.Amount - br.P4);
            }
            EmgRuleSettle(ref d, br, chain, depth, owner, d.Pos[u], bz.EmgId);
        }

        // ─────────────────────────────── 能量注入 ───────────────────────────────

        /// <summary>
        /// 在 <paramref name="pos"/> 处 <paramref name="radius"/> 米内的斑块注入能量（FG19B 第 1.4 节第 4 条）：热 → 升温 Q × patch_gain ÷ 份量（定温源不变）；电 → 导电 ≥ 0.3 的斑块加电荷。
        /// 迁入斑块不参与。返回注入到的斑块数。O(覆盖格登记数)。
        /// </summary>
        internal static int EmgInjectPatches(ref CombatData d, double2 pos, float radius, ushort port, float amount, int chain, byte depth)
        {
            if (!EmgPatchesOn(ref d) || d.EmgS[0].PatchLive <= 0 || !(amount > 0f))
            {
                return 0;
            }
            EmgHashEnsure(ref d);
            radius = math.max(0.5f, radius);
            int n = 0;
            int x0 = EmgHash.CellOf(pos.x - radius), x1 = EmgHash.CellOf(pos.x + radius);
            int y0 = EmgHash.CellOf(pos.y - radius), y1 = EmgHash.CellOf(pos.y + radius);
            var seen = new NativeList<int>(8, Allocator.Temp);
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
                        if (j >= d.Patches.Length || ContainsInt(seen, j))
                        {
                            continue;
                        }
                        seen.Add(j);
                        CombatZone o = d.Patches[j];
                        if (!PatchAlive(o) || IsLegacy(o) || math.distance(o.Pos, pos) > o.Radius + radius)
                        {
                            continue;
                        }
                        EmgMatter m = MatterOf(ref d, o.Matter);
                        if (port == EmgConst.PortHeat)
                        {
                            if (m.FixedTemp != 0 || PatchWorld(o))
                            {
                                continue;
                            }
                            o.Temp += amount * HeatGainOf(ref d) / math.max(0.5f, o.Amount);
                        }
                        else if (port == EmgConst.PortElec)
                        {
                            if (m.Conductive < EmgRuleConst.DefaultConductMinStrength)
                            {
                                continue;
                            }
                            o.Charge = math.min(4f, o.Charge + amount);
                        }
                        else
                        {
                            continue;
                        }
                        if (o.EmgChain <= 0 && chain > 0)
                        {
                            o.EmgChain = chain;
                            o.EmgDepth = depth;
                        }
                        d.Patches[j] = o;
                        n++;
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            seen.Dispose();
            return n;
        }

        /// <summary>
        /// 单位吃热（FG19B 第 1.4 节第 4 条：进武器积热，同时结构积热 + Q ÷ 300）：积热首次越过过热阈值时与开火积热同一套处理（停火迟滞、提示、evt.overheat、V04 / V05）。
        /// 热抗按有效抗性折减。没有武器 / 没有过热阈值的单位只记结构积热。
        /// </summary>
        internal static void EmgAddUnitHeat(ref CombatData d, int u, float q)
        {
            if (!(q > 0f) || !d.IsAlive(u))
            {
                return;
            }
            q *= 1f - EmgResist(ref d, u, 1);
            if (!(q > 0f))
            {
                return;
            }
            EmgAddAcc(ref d, u, EmgPhaseConst.Overheat, q / UnitScaleOf(ref d), 0, raw: true); // EMG2-PHS-01：结构积热累积量（已按热抗折减；满了熔穿）
            PhaseHeat(ref d, u, q); // EMG2-PHS-01（P02）：冻结中单次注热 ≥ 阈值 → 热震立即解冻
            int w = d.Weapon[u];
            if (w >= 0 && w < d.Weapons.Length && d.Weapons[w].OverheatAt > 0f)
            {
                CombatWeapon wp = d.Weapons[w];
                // 环境 / 命中注入的热不把武器积热推过 2 × 过热阈值（开火积热过热即停火自然有界；站在明火里持续吃热不能无界增长）
                float heat = math.max(d.Heat[u], math.min(d.Heat[u] + q, 2f * wp.OverheatAt));
                d.Heat[u] = heat;
                if (heat >= wp.OverheatAt && !d.Has(u, CombatUnitFlags.Overheated))
                {
                    d.Set(u, CombatUnitFlags.Overheated, true);
                    Cue(ref d, CombatEventKind.Overheat, u, 0, heat, d.Pos[u], 0);
                    EmgOnOverheat(ref d, u, heat);
                }
            }
        }

        /// <summary>
        /// 命中注能（FG19 FGR-EMG-090：固件写端 role = inject）：本次伤害出自带程序的载荷时，节点里写 en.heat / en.elec / en.mag 的注入行（触发事件是命中或没有触发读端；
        /// 内核根节点事件门控行 RootKernel 由 EmgRootGated 执行，这里跳过）对目标单位与命中点 param2 米内的斑块注入。O(节点指令数)，只在带程序载荷的命中时。
        /// </summary>
        internal static void EmgHitInject(ref CombatData d, int t, in EmgCtx c)
        {
            if (c.Prog < 0 || c.Prog >= d.EmgPrograms.Length)
            {
                return;
            }
            bool patchesOn = EmgPatchesOn(ref d); // 壁撞推动只看实体层，不依赖物质层
            EmgProgram p = d.EmgPrograms[c.Prog];
            if (c.Node >= p.NodeCount || p.FirstNode + c.Node >= d.EmgNodes.Length)
            {
                return;
            }
            EmgNode n = d.EmgNodes[p.FirstNode + c.Node];
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            ushort hitPort = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Hit);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == EmgConst.PortKinetic && (c.EntKind == EmgEntKind.Unit || c.EntKind == EmgEntKind.Zone))
                {
                    EmgWallSlamPush(ref d, t, c, x, n, end);
                    continue;
                }
                if (!patchesOn || x.Verb != (byte)EmgVerb.Inject || x.Role <= EmgConst.RoleLastRead || (x.Flags & (EmgInstrFlags.Overflow | EmgInstrFlags.RootKernel)) != 0
                    || (x.Port != EmgConst.PortHeat && x.Port != EmgConst.PortElec && x.Port != EmgConst.PortMag) || !(x.Amount > 0f))
                {
                    continue;
                }
                bool gatedElsewhere = false;
                for (int j = n.FirstInstr; j < end && !gatedElsewhere; j++)
                {
                    EmgInstr r = d.EmgInstrs[j];
                    gatedElsewhere = r.Ordinal == x.Ordinal && r.Role == EmgConst.RoleTrigger && r.Port != hitPort;
                }
                if (gatedElsewhere)
                {
                    continue;
                }
                double2 at = d.Pos[t];
                if (x.Port == EmgConst.PortHeat)
                {
                    EmgAddUnitHeat(ref d, t, x.Amount);
                }
                else if (x.Port == EmgConst.PortElec)
                {
                    EmgAddAcc(ref d, t, EmgPhaseConst.Charge, x.Amount, EmgIdOf(ref d, c.Owner));
                }
                else
                {
                    EmgAddAcc(ref d, t, EmgPhaseConst.Magnetize, x.Amount, EmgIdOf(ref d, c.Owner));
                }
                if (x.Port != EmgConst.PortMag && EmgFirstPatchInject(ref d, c, i - n.FirstInstr))
                {
                    EmgInjectPatches(ref d, at, math.max(0.5f, x.Param2), x.Port, x.Amount, c.Chain, c.Depth);
                }
                EmgChainLog(ref d, EmgChainNode.Inject, c.Chain, c.Depth, x.Port, EmgIdOf(ref d, c.Owner), at, d.Id[t]);
            }
        }

        /// <summary>
        /// 同一次出手（链 + 节点 + 步）的同一条注入指令对斑块只注一次（FG19B 第 1.4 节第 4 条按一次注入）：范围载荷命中 N 个单位时，单位侧逐个注入、斑块侧不乘 N。
        /// 记在不进快照的 <see cref="EmgScalars"/> 里，只在同一步内有效（读档后步序号不同，不会误判）。没有链号或指令位超出 64 时不去重。
        /// </summary>
        private static bool EmgFirstPatchInject(ref CombatData d, in EmgCtx c, int slot)
        {
            if (c.Chain <= 0 || slot < 0 || slot >= 64)
            {
                return true;
            }
            EmgScalars es = d.EmgS[0];
            long step = d.Scalars[0].Steps;
            if (es.InjChain != c.Chain || es.InjNode != c.Node || es.InjStep != step)
            {
                es.InjChain = c.Chain;
                es.InjNode = c.Node;
                es.InjStep = step;
                es.InjMask = 0ul;
            }
            ulong bit = 1ul << slot;
            bool first = (es.InjMask & bit) == 0ul;
            es.InjMask |= bit;
            d.EmgS[0] = es;
            return first;
        }

        /// <summary>
        /// 壁撞在格斗 / 力场 / 布区（FG19A 第 1.1 节第 16 行、卡片负向）：节点里“写 en.kinetic、且同一条指令读 ter.obstacle 条件”的行把命中目标朝远离出手者的方向推开
        /// a 米（fg.TbPortCarrier：格斗 2.4 / 力场 2 / 布区 1.6）；推动途中撞上障碍 / 残骸 / 单位时由实体层按“被推动实体撞障碍”结算——evt.impact（速度 / 质量）+ 撞击动能伤害
        /// （DamageFrom，Code = 撞击）。没撞上 = 只是推开（开阔地不冲击）。按端口判断，不按固件 ID（FGR-EMG-013）。
        /// </summary>
        private static void EmgWallSlamPush(ref CombatData d, int t, in EmgCtx c, in EmgInstr x, in EmgNode n, int end)
        {
            if (x.Role <= EmgConst.RoleLastRead || (x.Flags & EmgInstrFlags.Overflow) != 0 || !(x.A > 0f) || !EmgEntOn(ref d) || c.Owner < 0 || c.Owner >= d.Count || t == c.Owner)
            {
                return;
            }
            bool gated = false;
            for (int j = n.FirstInstr; j < end && !gated; j++)
            {
                EmgInstr r = d.EmgInstrs[j];
                gated = r.Ordinal == x.Ordinal && r.Port == EmgConst.PortObstacle && r.Role >= EmgConst.RoleTrigger && r.Role <= EmgConst.RoleLastRead
                        && (r.Flags & EmgInstrFlags.Overflow) == 0;
            }
            if (!gated || !d.IsAlive(t))
            {
                return;
            }
            // 撞击伤害经 DamageUnit 回到 EmgOnDamage：推动期间清掉程序上下文，撞击不再触发注能 / 推动（否则被撞的单位又被推、无限递归）
            EmgCtx saved = d.EmgCtx;
            d.EmgCtx.Prog = -1;
            Displace(ref d, t, d.Pos[c.Owner], -x.A, 0.0, c.Owner, EmgConst.PortKinetic);
            d.EmgCtx = saved;
        }

        /// <summary>
        /// 动能落地（FGR-EMG-093 / FG19B 第 1.4 节第 7 条：撞击、二次起爆、抛射弹落地、破片落点）：<paramref name="pos"/> 所在的固态斑块累计动能，越过耐久由 E18～E20 在它的节拍结算。
        /// 动能本身不存储、不传播（只累计在固态斑块上）。O(本格登记数)。
        /// </summary>
        internal static void EmgKineticAt(ref CombatData d, double2 pos, float kinetic)
        {
            if (!(kinetic > 0f) || d.EmgS[0].PatchLive <= 0 || !EmgPatchesOn(ref d))
            {
                return;
            }
            EmgHashEnsure(ref d);
            if (!d.EmgHash.Cells.TryGetFirstValue(EmgHash.Key(EmgHash.CellOf(pos.x), EmgHash.CellOf(pos.y)), out int e, out NativeParallelMultiHashMapIterator<long> it))
            {
                return;
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
                CombatZone o = d.Patches[j];
                if (!PatchAlive(o) || IsLegacy(o) || MatterOf(ref d, o.Matter).Solid == 0 || math.distance(o.Pos, pos) > o.Radius)
                {
                    continue;
                }
                o.Kinetic += kinetic;
                d.Patches[j] = o;
                EmgScalars es = d.EmgS[0];
                es.KineticDeposits++;
                d.EmgS[0] = es;
            }
            while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
        }

        // ─────────────────────────────── 传导节拍 ───────────────────────────────

        /// <summary>
        /// 每 emg.conduct.tick（按步序号对齐）一次：① 斑块热交换与回温；② 电的有界 BFS；③ 电荷条件规则（E14～E17）；④ 斑块电荷衰减；⑤ 站在热斑块里的单位吃热、单位电荷 / 磁化衰减、磁化 BFS。
        /// 节点访问合计 ≤ emg.conduct.max_visits，到上限即停、剩余能量留在节点上顺延（不丢）。同层节点按 Id 升序访问。
        /// </summary>
        internal static void StepConduct(ref CombatData d, float dt)
        {
            if (!EmgPatchesOn(ref d) || d.Scalars[0].Steps % ConductSteps(ref d, dt) != 0)
            {
                return;
            }
            bool any = d.EmgS[0].PatchLive > 0;
            if (!any && !EmgAnyUnitEnergy(ref d))
            {
                return;
            }
            EmgScalars es = d.EmgS[0];
            es.ConductTicks++;
            d.EmgS[0] = es;
            // 热交换、电、磁化各有自己的访问预算（规格的 ≤ 256 是给电 BFS 的；共用会让热源一多电就饿死）；超出时按节拍序号轮转起点，没轮到的下一节拍先处理（顺延，不固定截断）
            int budget = VisitsOf(ref d);
            int phase = (int)((d.Scalars[0].Steps / ConductSteps(ref d, dt)) & 0x3fffffff);
            int visits = 0;
            if (any)
            {
                EmgHashEnsure(ref d);
                visits += EmgHeatExchange(ref d, budget, phase);
                visits += EmgElecBfs(ref d, budget, phase);
                EmgChargeRules(ref d);
                EmgDecayCharge(ref d);
            }
            visits += EmgUnitEnergyTick(ref d, budget, phase);
            es = d.EmgS[0];
            es.ConductVisits += visits;
            es.ConductLastVisits = visits;
            d.EmgS[0] = es;
        }

        private static bool EmgAnyUnitEnergy(ref CombatData d)
        {
            for (int u = 0; u < d.Count; u++)
            {
                EmgUnitState st = d.EmgUnit[u];
                if (st.AccA.z > 0f || st.AccB.x > 0f)
                {
                    return true;
                }
            }
            return false;
        }

        private static void EmgCarried(ref CombatData d)
        {
            EmgScalars es = d.EmgS[0];
            es.ConductCarried++;
            d.EmgS[0] = es;
        }

        /// <summary>
        /// 热交换（FG19B 第 1.4 节第 2、3 条）：偏离常温的斑块（热源）按 Id 升序，与重叠邻居交换 ΔT_a = 0.4 × 重叠比 × (T_b − T_a) × m_b ÷ (m_a + m_b)（地面 / 气体之间 × 0.5，
        /// 定温源视质量无穷大、自己不变）；高于常温的每秒回落 40 度。先算增量再统一写回（结果与访问顺序无关）。返回访问数。
        /// </summary>
        private static int EmgHeatExchange(ref CombatData d, int budget, int phase)
        {
            float amb = AmbientOf(ref d), ex = ExchangeOf(ref d), cool = CoolRateOf(ref d) * ConductTickOf(ref d);
            var src = new NativeList<int2>(16, Allocator.Temp); // (Id, 下标)
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && !IsLegacy(z) && math.abs(z.Temp - amb) > 0.5f)
                {
                    src.Add(new int2(z.EmgId, i));
                }
            }
            if (src.Length == 0)
            {
                src.Dispose();
                return 0;
            }
            src.Sort(new Int2XComparer());
            // 定点累加（1/65536 度）：整数加法与邻居遍历顺序无关——空间哈希的桶内顺序随槽位插入顺序变，读档会重排槽位，浮点累加顺序不同会让续跑分叉（EMG2-PHS-01 模糊存读档 #61）
            var dT = new NativeArray<long>(d.Patches.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var isSrc = new NativeArray<byte>(d.Patches.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);
            int n = math.min(src.Length, budget);
            int start = 0;
            if (n < src.Length)
            {
                EmgCarried(ref d);
                start = (int)((long)phase * n % src.Length); // 轮转：Id 靠后的热源每 ⌈热源数 ÷ 预算⌉ 个节拍至少处理一次
            }
            for (int s = 0; s < n; s++)
            {
                isSrc[src[(start + s) % src.Length].y] = 1;
            }
            for (int s = 0; s < n; s++)
            {
                int i = src[(start + s) % src.Length].y;
                CombatZone z = d.Patches[i];
                bool fixI = MatterOf(ref d, z.Matter).FixedTemp != 0 || PatchWorld(z);
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
                            if (j == i || j >= d.Patches.Length)
                            {
                                continue;
                            }
                            CombatZone o = d.Patches[j];
                            // 一个邻居可能登记在多个格里：只在它与本块外接格的第一个公共格处理一次
                            if (!PatchAlive(o) || IsLegacy(o) || EmgHash.CellOf(math.max(o.Pos.x - o.Radius, z.Pos.x - z.Radius)) != cx
                                || EmgHash.CellOf(math.max(o.Pos.y - o.Radius, z.Pos.y - z.Radius)) != cy)
                            {
                                continue;
                            }
                            double ratio = OverlapRatio(z.Pos, z.Radius, o.Pos, o.Radius);
                            if (ratio <= 0)
                            {
                                continue;
                            }
                            float lf = z.Layer != o.Layer ? 0.5f : 1f;
                            bool fixJ = MatterOf(ref d, o.Matter).FixedTemp != 0 || PatchWorld(o);
                            float k = ex * (float)ratio * lf;
                            float wj = fixJ ? 1f : fixI ? 0f : o.Amount / math.max(1e-3f, z.Amount + o.Amount);
                            dT[i] += FixHeat(k * (o.Temp - z.Temp) * wj);
                            if (isSrc[j] == 0)
                            {
                                float wi = fixI ? 1f : fixJ ? 0f : z.Amount / math.max(1e-3f, z.Amount + o.Amount);
                                dT[j] += FixHeat(k * (z.Temp - o.Temp) * wi);
                            }
                        }
                        while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                    }
                }
            }
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (!PatchAlive(z) || IsLegacy(z) || MatterOf(ref d, z.Matter).FixedTemp != 0 || PatchWorld(z))
                {
                    continue;
                }
                float t = z.Temp + (float)(dT[i] * (1.0 / HeatFixScale));
                if (isSrc[i] != 0 && t > amb)
                {
                    t = math.max(amb, t - cool);
                }
                if (t != z.Temp)
                {
                    z.Temp = t;
                    d.Patches[i] = z;
                }
            }
            dT.Dispose();
            isSrc.Dispose();
            src.Dispose();
            return n;
        }

        private static bool ContainsInt(NativeList<int> list, int v)
        {
            for (int k = 0; k < list.Length; k++)
            {
                if (list[k] == v)
                {
                    return true;
                }
            }
            return false;
        }

        private struct Int2XComparer : System.Collections.Generic.IComparer<int2>
        {
            public int Compare(int2 a, int2 b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);
        }

        /// <summary>
        /// 电的有界 BFS（FGR-EMG-091 / FG19B 第 1.4 节第 5 条）：起点 = 电荷 ≥ q_min 的导电斑块、残骸与站在导电斑块里的带电单位（按种类、Id 升序）；
        /// 边 = 斑块 / 残骸圆重叠，以及单位与它脚下的导电斑块（单位按接触点判定“站在里面”，与沾染同一口径）。接收量 = 来源 × 0.7 × 接收方导电；
        /// 单位从斑块分得 unit_share × 斑块电荷 × (1 − 电抗)（FG19B 第 5 条），比它现有的大且 ≥ q_min 才写入并继续。
        /// “≤ 6 跳”是单个传导节拍内的口径（FG19B 第 5 条“每个传导节拍，从带电节点出发……最多 6 跳”）；跨节拍的余电由每节拍 × patch_decay 衰减自然收敛（见 FG19B 第 1.4 节第 5 条注）。
        /// 半径 &gt; 8 米或常驻的斑块不做中继（只在注入点附近直接带电）。返回访问数；到上限即停，起点超出预算时按节拍序号轮转（能量留在节点上，下一节拍接着传）。
        /// </summary>
        private static int EmgElecBfs(ref CombatData d, int budget, int phase)
        {
            float qmin = QMinOf(ref d), fall = FalloffOf(ref d), bigR = BigROf(ref d), share = ShareOf(ref d);
            int hops = HopsOf(ref d);
            var queue = new NativeList<int4>(16, Allocator.Temp); // (种类 0 斑块 / 1 残骸 / 2 单位, 下标, 跳数, Id)
            // 站在导电斑块里的地面单位（O(单位数)，只在本节拍有带电节点时）：斑块 → 单位、单位 → 斑块两张表
            uint condMask = 0u;
            for (int m = 1; m <= EmgMatterConst.Count; m++)
            {
                condMask |= MatterOf(ref d, m).Conductive >= EmgRuleConst.DefaultConductMinStrength ? 1u << m : 0u;
            }
            var inPatch = new NativeParallelMultiHashMap<int, int>(16, Allocator.Temp);
            var unitIn = new NativeParallelMultiHashMap<int, int>(16, Allocator.Temp);
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || d.Kind[u] == (byte)CombatUnitKind.Structure || (d.EmgUnit[u].PrevMatterMask & condMask) == 0u || IsAir(ref d, u))
                {
                    continue;
                }
                hits.Clear();
                PatchesAt(ref d, d.Pos[u], false, ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
                bool any = false;
                for (int h = 0; h < hits.Length; h++)
                {
                    int j = hits[h].x;
                    if (MatterOf(ref d, d.Patches[j].Matter).Conductive >= EmgRuleConst.DefaultConductMinStrength)
                    {
                        inPatch.Add(j, u);
                        unitIn.Add(u, j);
                        any = true;
                    }
                }
                if (any && d.EmgUnit[u].AccA.z >= qmin)
                {
                    queue.Add(new int4(2, u, 0, d.Id[u]));
                }
            }
            hits.Dispose();
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && !IsLegacy(z) && z.Charge >= qmin && z.Radius <= bigR && !PatchWorld(z))
                {
                    queue.Add(new int4(0, i, 0, z.EmgId));
                }
            }
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck wk = d.Wrecks[w];
                if (wk.Hp > 0f && wk.Charge >= qmin)
                {
                    queue.Add(new int4(1, w, 0, wk.Id));
                }
            }
            if (queue.Length == 0)
            {
                queue.Dispose();
                inPatch.Dispose();
                unitIn.Dispose();
                return 0;
            }
            queue.Sort(new Int4KindIdComparer());
            RotateSeeds(ref queue, phase, budget);
            // 每项按入队时的电荷出发（起点 = 本节拍开始时的余电）：出队时重读会读到本节拍刚被上游抬高的值，让下游以 0 跳重新出发、突破单节拍跳数上限
            var qv = new NativeList<float>(queue.Length, Allocator.Temp);
            for (int s = 0; s < queue.Length; s++)
            {
                int4 sd = queue[s];
                qv.Add(sd.x == 0 ? d.Patches[sd.y].Charge : sd.x == 1 ? d.Wrecks[sd.y].Charge : d.EmgUnit[sd.y].AccA.z);
            }
            int visits = 0;
            for (int h = 0; h < queue.Length; h++)
            {
                if (visits >= budget)
                {
                    EmgCarried(ref d);
                    break;
                }
                visits++;
                int4 nd = queue[h];
                double2 pos;
                float rad, q = qv[h];
                if (nd.x == 0)
                {
                    CombatZone z = d.Patches[nd.y];
                    pos = z.Pos;
                    rad = z.Radius;
                }
                else if (nd.x == 1)
                {
                    EmgWreck wk = d.Wrecks[nd.y];
                    pos = wk.Pos;
                    rad = wk.Radius;
                }
                else
                {
                    pos = d.Pos[nd.y];
                    rad = 0f;
                }
                if (nd.z >= hops || q * fall < qmin)
                {
                    continue;
                }
                if (nd.x == 2)
                {
                    // 单位 → 它脚下的导电斑块（按普通一跳：× 0.7 × 斑块导电）
                    if (unitIn.TryGetFirstValue(nd.y, out int pj, out NativeParallelMultiHashMapIterator<int> uit))
                    {
                        do
                        {
                            EmgElecToPatch(ref d, ref queue, ref qv, pj, q * fall * MatterOf(ref d, d.Patches[pj].Matter).Conductive, nd.z + 1, qmin, bigR);
                        }
                        while (unitIn.TryGetNextValue(out pj, ref uit));
                    }
                    continue;
                }
                if (nd.x == 0 && inPatch.TryGetFirstValue(nd.y, out int pu, out NativeParallelMultiHashMapIterator<int> pit))
                {
                    // 斑块 → 站在里面的单位：分得 unit_share × 斑块电荷，按有效电抗折减（累积量上限 2，与命中注能同口径）
                    do
                    {
                        EmgUnitState st = d.EmgUnit[pu];
                        float recv = math.min(2f, q * share * (1f - EmgResist(ref d, pu, 2)));
                        if (recv >= qmin && recv > st.AccA.z + 1e-4f && EmgAddAcc(ref d, pu, EmgPhaseConst.Charge, recv - st.AccA.z, 0, raw: true, enterNow: false) > 0f)
                        {
                            queue.Add(new int4(2, pu, nd.z + 1, d.Id[pu]));
                            qv.Add(recv);
                        }
                    }
                    while (inPatch.TryGetNextValue(out pu, ref pit));
                }
                int x0 = EmgHash.CellOf(pos.x - rad), x1 = EmgHash.CellOf(pos.x + rad);
                int y0 = EmgHash.CellOf(pos.y - rad), y1 = EmgHash.CellOf(pos.y + rad);
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
                            byte kind = (byte)d.EmgHash.Entries[e].w;
                            int j = d.EmgHash.Refs[e];
                            if (kind == EmgHash.KindPatch && j < d.Patches.Length && !(nd.x == 0 && j == nd.y))
                            {
                                CombatZone o = d.Patches[j];
                                if (math.distance(o.Pos, pos) >= o.Radius + rad)
                                {
                                    continue;
                                }
                                EmgElecToPatch(ref d, ref queue, ref qv, j, q * fall * MatterOf(ref d, o.Matter).Conductive, nd.z + 1, qmin, bigR);
                            }
                            else if (kind == EmgHash.KindWreck && j < d.Wrecks.Length && !(nd.x == 1 && j == nd.y))
                            {
                                EmgWreck wk = d.Wrecks[j];
                                float cond = EmgProfileAt(ref d, wk.ProfileRef).Conductive;
                                if (!(wk.Hp > 0f) || cond < EmgRuleConst.DefaultConductMinStrength || math.distance(wk.Pos, pos) >= wk.Radius + rad)
                                {
                                    continue;
                                }
                                float recv = q * fall * cond;
                                if (recv >= qmin && recv > wk.Charge + 1e-4f)
                                {
                                    wk.Charge = recv;
                                    d.Wrecks[j] = wk;
                                    queue.Add(new int4(1, j, nd.z + 1, wk.Id));
                                    qv.Add(recv);
                                }
                            }
                        }
                        while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                    }
                }
            }
            queue.Dispose();
            qv.Dispose();
            inPatch.Dispose();
            unitIn.Dispose();
            return visits;
        }

        /// <summary>一跳传到斑块 <paramref name="j"/>：导电 ≥ 0.3、接收量 ≥ q_min 且比它现有的大才写入；大斑块 / 常驻斑块只带电不中继。</summary>
        private static void EmgElecToPatch(ref CombatData d, ref NativeList<int4> queue, ref NativeList<float> qv, int j, float recv, int hop, float qmin, float bigR)
        {
            CombatZone o = d.Patches[j];
            if (!PatchAlive(o) || IsLegacy(o) || MatterOf(ref d, o.Matter).Conductive < EmgRuleConst.DefaultConductMinStrength || recv < qmin || recv <= o.Charge + 1e-4f)
            {
                return;
            }
            o.Charge = recv;
            d.Patches[j] = o;
            if (o.Radius <= bigR && !PatchWorld(o))
            {
                queue.Add(new int4(0, j, hop, o.EmgId));
                qv.Add(recv);
            }
        }

        /// <summary>起点数超过访问预算时按节拍序号轮转起点（Id 升序排好后从 phase × 预算 处开始），保证每个起点轮流被处理、不因 Id 靠后永远饿死。</summary>
        private static void RotateSeeds<T>(ref NativeList<T> l, int phase, int budget) where T : unmanaged
        {
            int len = l.Length;
            int start = len > budget && budget > 0 ? (int)((long)phase * budget % len) : 0;
            if (start == 0)
            {
                return;
            }
            var tmp = new NativeArray<T>(len, Allocator.Temp);
            for (int s = 0; s < len; s++)
            {
                tmp[s] = l[(start + s) % len];
            }
            for (int s = 0; s < len; s++)
            {
                l[s] = tmp[s];
            }
            tmp.Dispose();
        }

        private struct Int4KindIdComparer : System.Collections.Generic.IComparer<int4>
        {
            public int Compare(int4 a, int4 b) => a.x != b.x ? a.x.CompareTo(b.x) : a.w.CompareTo(b.w);
        }

        /// <summary>电荷条件的规则（E14～E17）在传导节拍、BFS 之后求值：带电斑块按 Id 升序，每块只结算优先级最小的一条成立的规则（同一宿主一个节拍一条）。</summary>
        private static void EmgChargeRules(ref CombatData d)
        {
            if (!EmgRulesOn(ref d))
            {
                return;
            }
            float qmin = QMinOf(ref d);
            var hosts = new NativeList<int2>(8, Allocator.Temp);
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && !IsLegacy(z) && !PatchWorld(z) && z.Charge >= qmin)
                {
                    hosts.Add(new int2(z.EmgId, i));
                }
            }
            hosts.Sort(new Int2XComparer());
            double now = d.Scalars[0].Time;
            for (int h = 0; h < hosts.Length; h++)
            {
                int i = hosts[h].y;
                CombatZone z = d.Patches[i];
                if (!PatchAlive(z) || z.EmgId != hosts[h].x)
                {
                    continue;
                }
                int2 ix = d.EmgRuleIdx[z.Matter];
                int chosen = -1, satisfied = 0;
                for (int k = ix.x; k < ix.x + ix.y; k++)
                {
                    int ri = d.EmgRuleOrder[k];
                    EmgRule r = d.EmgRules[ri];
                    if (r.Op == EmgRuleConst.OpDeferred || !ChargeCond(r.Cond))
                    {
                        continue;
                    }
                    bool ok = z.Charge >= r.C1 && (r.Cond != EmgRuleConst.CondChargeGeConcGe || z.Conc >= r.C2);
                    if (!ok)
                    {
                        continue;
                    }
                    satisfied++;
                    if (chosen < 0)
                    {
                        chosen = ri;
                    }
                }
                if (chosen < 0)
                {
                    continue;
                }
                if (satisfied > 1)
                {
                    EmgScalars es = d.EmgS[0];
                    es.RulesDeferred += satisfied - 1;
                    d.EmgS[0] = es;
                }
                EmgApplyRule(ref d, d.EmgRules[chosen], i, -1, now);
            }
            hosts.Dispose();
        }

        /// <summary>斑块 / 残骸电荷每传导节拍 × emg.conduct.patch_decay，低于 q_min 归零。</summary>
        private static void EmgDecayCharge(ref CombatData d)
        {
            float k = DecayOf(ref d), qmin = QMinOf(ref d);
            for (int i = 0; i < d.Patches.Length; i++)
            {
                CombatZone z = d.Patches[i];
                if (PatchAlive(z) && z.Charge > 0f)
                {
                    z.Charge = z.Charge * k < qmin ? 0f : z.Charge * k;
                    d.Patches[i] = z;
                }
            }
            for (int w = 0; w < d.Wrecks.Length; w++)
            {
                EmgWreck wk = d.Wrecks[w];
                if (wk.Charge > 0f)
                {
                    wk.Charge = wk.Charge * k < qmin ? 0f : wk.Charge * k;
                    d.Wrecks[w] = wk;
                }
            }
        }

        /// <summary>
        /// 单位侧（O(单位数)，每传导节拍一次）：站在热斑块里（温度高于常温 30 度以上的最热一块）吃热 = unit_gain × (T − 常温)（FGR-EMG-092 持续加热让武器过热停火，敌我都算）；
        /// 电荷 / 磁化累积量的衰减与相态在 StepPhase（fg.TbPhaseRule，EMG2-PHS-01）；磁化沿磁性接触在单位之间有界传播。返回访问数。
        /// </summary>
        private static int EmgUnitEnergyTick(ref CombatData d, int budget, int phase)
        {
            float amb = AmbientOf(ref d), gain = UnitGainOf(ref d), qmin = QMinOf(ref d), fall = FalloffOf(ref d);
            bool patches = d.EmgS[0].PatchLive > 0;
            var hits = new NativeList<int2>(EmgMatterConst.ContactPerUnit + 1, Allocator.Temp);
            var magQ = new NativeList<int2>(4, Allocator.Temp); // (单位 Id, 槽位)
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u))
                {
                    continue;
                }
                EmgUnitState st = d.EmgUnit[u];
                if (patches && st.PrevMatterMask != 0u && d.Kind[u] != (byte)CombatUnitKind.Structure)
                {
                    hits.Clear();
                    PatchesAt(ref d, d.Pos[u], IsAir(ref d, u), ref hits, EmgMatterConst.ContactPerUnit, legacyInHits: false);
                    float hot = amb;
                    for (int h = 0; h < hits.Length; h++)
                    {
                        hot = math.max(hot, d.Patches[hits[h].x].Temp);
                    }
                    if (hot > amb + 30f)
                    {
                        EmgAddUnitHeat(ref d, u, gain * (hot - amb));
                        st = d.EmgUnit[u];
                    }
                }
                if (st.AccB.x >= qmin)
                {
                    magQ.Add(new int2(d.Id[u], u));
                }
                // EMG2-PHS-01（关 DEBT-EMG2RULE01-02）：电荷 / 磁化度的衰减、阈值与相态改由 fg.TbPhaseRule 在 StepPhase 每步结算，这里不再借斑块衰减系数
            }
            hits.Dispose();
            int visits = 0;
            if (magQ.Length > 0)
            {
                magQ.Sort(new Int2XComparer());
                RotateSeeds(ref magQ, phase, budget);
                int hopsMax = HopsOf(ref d);
                var hop = new NativeList<int>(magQ.Length, Allocator.Temp);
                var mq = new NativeList<float>(magQ.Length, Allocator.Temp); // 每项按入队时的磁化度出发（起点 = 衰减后的余量），理由同电 BFS
                for (int s = 0; s < magQ.Length; s++)
                {
                    hop.Add(0);
                    mq.Add(d.EmgUnit[magQ[s].y].AccB.x);
                }
                for (int h = 0; h < magQ.Length; h++)
                {
                    if (visits >= budget)
                    {
                        EmgCarried(ref d);
                        break;
                    }
                    visits++;
                    int u = magQ[h].y;
                    float q = mq[h];
                    if (hop[h] >= hopsMax || q * fall < qmin)
                    {
                        continue;
                    }
                    for (int v = 0; v < d.Count; v++)
                    {
                        if (v == u || !d.IsAlive(v) || math.distance(d.Pos[u], d.Pos[v]) > d.Radius[u] + d.Radius[v] + 0.2f)
                        {
                            continue;
                        }
                        float mag = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, v)).Magnetic;
                        if (mag < EmgRuleConst.DefaultConductMinStrength)
                        {
                            continue;
                        }
                        EmgUnitState sv = d.EmgUnit[v];
                        float recv = q * fall * mag * (1f - EmgResist(ref d, v, 4));
                        if (recv >= qmin && recv > sv.AccB.x + 1e-4f && EmgAddAcc(ref d, v, EmgPhaseConst.Magnetize, recv - sv.AccB.x, 0, raw: true, enterNow: false) > 0f)
                        {
                            magQ.Add(new int2(d.Id[v], v));
                            hop.Add(hop[h] + 1);
                            mq.Add(recv);
                        }
                    }
                }
                hop.Dispose();
                mq.Dispose();
            }
            magQ.Dispose();
            return visits;
        }

        // ─────────────────────────────── AI 避险（FGR-EMG-088 / ADR ⑯）───────────────────────────────

        /// <summary>斑块对单位有害吗：明火 / 熔融等有接触伤害或会挂燃烧 / 腐蚀 / 污染沾染的，或带电（≥ 0.2）的导电液体；对应伤害类型的有效抗性满（≥ 1）时不算有害。</summary>
        internal static bool EmgPatchHarmful(ref CombatData d, in CombatZone z, int u)
        {
            EmgMatter m = MatterOf(ref d, z.Matter);
            if (!m.Valid)
            {
                return false;
            }
            // 迁入斑块仍按区域载荷结算、只伤对面阵营（与 ZoneTick 同一口径）：己方的燃迹 / 驻留区对己方不算有害
            if (IsLegacy(z) && z.Faction != CombatFaction.Neutral
                && d.Faction[u] != (byte)(z.Faction == CombatFaction.Hostile ? CombatFaction.Player : CombatFaction.Hostile))
            {
                return false;
            }
            if (m.Conductive >= EmgRuleConst.DefaultConductMinStrength && z.Charge >= 0.2f && EmgResist(ref d, u, 2) < 1f)
            {
                return true;
            }
            int bit = m.CoatBit;
            if (bit == 0 || z.Matter == EmgMatterConst.Fire || z.Matter == EmgMatterConst.Molten)
            {
                return EmgResist(ref d, u, 1) < 1f;
            }
            if (bit == 6 || bit == 13)
            {
                return EmgResist(ref d, u, 3) < 1f;
            }
            return m.ContactDps > 0f;
        }

        /// <summary>
        /// 教义自选站位的移动（守点交战走位 / 回守点）：把“有害斑块排斥力”叠加到期望方向上（只查本单位与目标点周围空间哈希格里对本机有害的斑块），返回调整后的下一步位置。
        /// 只作用于 AI 驾驶的己方机器：接入 / 直控（Possessed）、关了教义（NoHazardAvoid）、离地单位不受约束。施加排斥时计数，每传导节拍最多在因果链记一条“由教义触发：避开危险物质”。
        /// </summary>
        internal static double2 EmgAvoidAdjust(ref CombatData d, int i, double2 pos, double2 next)
        {
            if (d.EmgS[0].PatchLive <= 0 || !EmgPatchesOn(ref d) || d.Faction[i] != (byte)CombatFaction.Player || d.Has(i, CombatUnitFlags.NoHazardAvoid)
                || d.Has(i, CombatUnitFlags.Possessed) || IsAir(ref d, i))
            {
                return next;
            }
            double2 step = next - pos;
            double len = math.length(step);
            if (len < 1e-6)
            {
                return next;
            }
            EmgHashEnsure(ref d);
            float margin = d.Config.EmgAvoidMargin > 0f ? d.Config.EmgAvoidMargin : EmgRuleConst.DefaultAvoidMargin;
            float weight = d.Config.EmgAvoidWeight > 0f ? d.Config.EmgAvoidWeight : EmgRuleConst.DefaultAvoidWeight;
            double2 dirv = step / len;
            double2 push = 0;
            int firstMatter = 0, firstChain = 0;
            // 两个探点（脚下前方 0.5 米、前方 3 米）：排斥只取垂直于前进方向的分量（侧滑绕开，不倒退、不原地顶牛）；正对圆心时固定往左手边绕（确定性）
            for (int pi = 0; pi < 2; pi++)
            {
            double2 probe = pos + dirv * (pi == 0 ? 0.5 : 3.0);
            float look = margin + d.Radius[i] + PatchMaxRadiusOf(ref d);
            int x0 = EmgHash.CellOf(probe.x - look), x1 = EmgHash.CellOf(probe.x + look);
            int y0 = EmgHash.CellOf(probe.y - look), y1 = EmgHash.CellOf(probe.y + look);
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
                        if (!PatchAlive(z) || z.Layer != EmgMatterConst.LayerGround || !EmgPatchHarmful(ref d, z, i))
                        {
                            continue;
                        }
                        double2 away = probe - z.Pos;
                        double dist = math.length(away) - z.Radius - d.Radius[i];
                        if (dist >= margin)
                        {
                            continue;
                        }
                        double2 lat = away - math.dot(away, dirv) * dirv;
                        double ll = math.length(lat);
                        double2 side = ll > 1e-3 ? lat / ll : new double2(-dirv.y, dirv.x);
                        push += side * math.min(2.0, (margin - dist) / margin);
                        if (firstMatter == 0)
                        {
                            firstMatter = z.Matter;
                            firstChain = z.EmgChain;
                        }
                    }
                    while (d.EmgHash.Cells.TryGetNextValue(out e, ref it));
                }
            }
            }
            if (math.lengthsq(push) < 1e-9)
            {
                return next;
            }
            double2 want = dirv + push * weight;
            double wl = math.length(want);
            if (wl < 1e-6)
            {
                return pos; // 正面顶住：原地等（不硬闯）
            }
            EmgScalars es = d.EmgS[0];
            es.AvoidPushes++;
            d.EmgS[0] = es;
            if (d.Scalars[0].Steps % 15 == (d.Id[i] % 15))
            {
                if (firstChain <= 0)
                {
                    // 危险斑块没有链（敌方 / 世界源 / 迁入区域）：给这次教义动作开一条新链，保证因果链里查得到“由教义触发”（B24）
                    es = d.EmgS[0];
                    firstChain = es.NextChain++;
                    d.EmgS[0] = es;
                }
                EmgChainLog(ref d, EmgChainNode.Doctrine, firstChain, 0, firstMatter, d.Id[i], pos, 0, 1);
            }
            return pos + want / wl * len;
        }

        /// <summary>
        /// 玩家显式移动 / 攻击移动路线是否穿过对本机有害的斑块（下令时判一次；路线不改写）：返回第一块（沿线最近）有害斑块的下标，−1 = 不穿过。O(斑块数)，只在下令时。
        /// </summary>
        internal static int EmgPathHazard(ref CombatData d, int i, double2 from, double2 to)
        {
            if (d.EmgS[0].PatchLive <= 0 || !EmgPatchesOn(ref d) || IsAir(ref d, i))
            {
                return -1;
            }
            int best = -1;
            double bestT = double.MaxValue;
            for (int j = 0; j < d.Patches.Length; j++)
            {
                CombatZone z = d.Patches[j];
                if (!PatchAlive(z) || z.Layer != EmgMatterConst.LayerGround || !EmgPatchHarmful(ref d, z, i))
                {
                    continue;
                }
                double t = SegmentCircleT(from, to, z.Pos, z.Radius + d.Radius[i] * 0.5f);
                if (t >= 0 && t <= 1.0 && t < bestT)
                {
                    bestT = t;
                    best = j;
                }
            }
            return best;
        }
    }
}
