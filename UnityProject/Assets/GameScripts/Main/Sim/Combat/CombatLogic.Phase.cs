using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG2-PHS-01（FG19 FGR-EMG-100～103；FG19B §3.3 / §3.4；ADR-EMG-001 ⑥ ⑦ ⑭ #5 #6 #7 #9）：单位上 7 个累积量与相态。
    /// - 累积：<see cref="EmgAddAcc"/> 是唯一入口（受寒、注热、注电、注磁、酸 / 火接触、带寒 / 带酸 / 带火标签都走它）：免疫窗内不累积；定时相态（冻结、麻痹）期间不累积；
    ///   增益 × (1 − 有效抗性)（档案值与运行时覆盖取大，己方机器的腐蚀抗性不低于防腐涂层 <see cref="CombatData.PlayerAcidResist"/>）；到上限不再增长；越过进入阈值当场进入相态。
    /// - 每步 <see cref="StepPhase"/>（O(单位)，只在 Main/Sim）：衰减（冻结前受寒：燃烧 / 武器积热过半加快）、定时退出 / 阈值退出（停留满 minDwell）、免疫窗倒计时、相态期间效果。
    ///   计时都是“剩余秒”倒计时，按游戏秒推进：暂停时不推进，0.5x～3x 倍速只改每帧步数（同一游戏秒结果相同）。
    /// - 相态效果：冻结 / 麻痹 = 不能移动、不能开火（开火结果码 Frozen / Stunned）；冻结受单次动能 ≥ 24 × 脆性 → 碎裂（P01），受单次注热 ≥ 30 → 热震立即解冻（P02）；
    ///   熔穿 = 进入时身下熔融金属、期间每 2 秒再漏，正面装甲失效；麻痹 = 进入时向 4 米内 ≤ 3 个导体放电；破甲 = 身下金属碎屑、质量 × 0.6、装甲失效；
    ///   磁化 = 4 米内弹体不分阵营转向本单位（弱磁源）；燃烧 = 持续伤害 4 × L（与燃烧标签取大，不叠两份）、对自身注热、进水 / 冷却液 / 液氮立即熄灭。
    /// - 进入 / 退出：发 evt.phase（Code 低 4 位 = 累积量下标，位 4 = 退出，位 5 = 被打断）、因果链“相变”节点、表现提示 <see cref="CombatEventKind.PhaseCue"/>（可丢；可靠计数在 EmgScalars）。
    /// 状态全在 <see cref="EmgUnitState"/>（快照 17 单位段第 10～31 字段），规则在 <see cref="CombatData.EmgPhases"/>（fg.TbPhaseRule，表数据）。按端口 / 属性生效，不认内容 ID（FGR-EMG-013 / 103）。
    /// </summary>
    public static partial class CombatLogic
    {
        private const ushort PortFrozen = 902;

        public static float AccOf(in EmgUnitState s, int k) => k < 4 ? s.AccA[k] : s.AccB[k - 4];

        private static void AccPut(ref EmgUnitState s, int k, float v)
        {
            if (k < 4)
            {
                s.AccA[k] = v;
            }
            else
            {
                s.AccB[k - 4] = v;
            }
        }

        public static float PhsTOf(in EmgUnitState s, int k) => k < 4 ? s.PhsTA[k] : s.PhsTB[k - 4];

        private static void PhsTPut(ref EmgUnitState s, int k, float v)
        {
            if (k < 4)
            {
                s.PhsTA[k] = v;
            }
            else
            {
                s.PhsTB[k - 4] = v;
            }
        }

        internal static bool InPhase(ref CombatData d, int i, int k) => i >= 0 && i < d.Count && (d.EmgUnit[i].PhaseMask & (1 << k)) != 0;

        /// <summary>被冻结（寒量满，FGR-EMG-101）：不能移动、不能开火。云端 FrozenT 并入相态位（ADR ⑭ #5）。</summary>
        internal static bool IsFrozen(ref CombatData d, int i) => InPhase(ref d, i, EmgPhaseConst.Chill);

        /// <summary>被麻痹（电荷满）：1 秒不能行动。</summary>
        internal static bool IsStunned(ref CombatData d, int i) => InPhase(ref d, i, EmgPhaseConst.Charge);

        /// <summary>不能移动（冻结或麻痹）。</summary>
        internal static bool IsImmobile(ref CombatData d, int i) => (d.EmgUnit[i].PhaseMask & ((1 << EmgPhaseConst.Chill) | (1 << EmgPhaseConst.Charge))) != 0;

        /// <summary>不能开火的原因（冻结优先；都不是 = Ok）。开火结果码 Frozen 保留，来源改为相态（ADR ⑭ #6）。</summary>
        internal static CombatFireResult ImmobileFire(ref CombatData d, int a) =>
            IsFrozen(ref d, a) ? CombatFireResult.Frozen : IsStunned(ref d, a) ? CombatFireResult.Stunned : CombatFireResult.Ok;

        /// <summary>破甲 / 熔穿期间装甲失效（FGR-EMG-101 附带）。其余时候 = 原装甲比例。</summary>
        internal static float ArmorOf(ref CombatData d, int i) =>
            (d.EmgUnit[i].PhaseMask & ((1 << EmgPhaseConst.Corrode) | (1 << EmgPhaseConst.Overheat))) != 0 ? 0f : d.Armor[i].x;

        /// <summary>破甲期间的质量倍率（FG19B §3.3 phase_corrode：× 0.6，更容易被推走）。</summary>
        internal static float PhaseMassMul(ref CombatData d, int i) =>
            (d.EmgUnit[i].PhaseMask & (1 << EmgPhaseConst.Corrode)) != 0 ? math.max(0.05f, d.EmgPhases[EmgPhaseConst.Corrode].P2) : 1f;

        // ─────────────────────────────── 累积 ───────────────────────────────

        /// <summary>
        /// 累积量唯一入口。<paramref name="raw"/> = 调用方已经按抗性折减过（注热）。<paramref name="enterNow"/> = false 时只累积、进入留给下一步的 StepPhase（放电连锁用，避免递归）。
        /// 返回实际增加量（免疫窗 / 定时相态中 / 全抗 / 到上限 = 0）。
        /// </summary>
        internal static float EmgAddAcc(ref CombatData d, int u, int k, float amount, int sourceId = 0, bool raw = false, bool enterNow = true)
        {
            if (!(amount > 0f) || u < 0 || u >= d.Count || !d.IsAlive(u) || k < 0 || k >= EmgPhaseConst.Count)
            {
                return 0f;
            }
            EmgPhaseRule r = d.EmgPhases[k];
            EmgUnitState s = d.EmgUnit[u];
            bool inPhase = (s.PhaseMask & (1 << k)) != 0;
            if ((!inPhase && PhsTOf(s, k) > 0f) || (inPhase && r.ExitMode == EmgPhaseConst.ExitTimed))
            {
                return 0f; // 免疫窗（ADR ⑥ guard，沿用云端 ThawGuardT）/ 冻结、麻痹期间
            }
            float gain = raw ? amount : amount * (1f - EmgResist(ref d, u, r.Resist));
            float before = AccOf(s, k);
            float after = math.min(math.max(r.Cap, r.Enter), before + math.max(0f, gain));
            if (!(after > before))
            {
                return 0f;
            }
            AccPut(ref s, k, after);
            d.EmgUnit[u] = s;
            if (enterNow && !inPhase && r.ExitMode != EmgPhaseConst.ExitDeath && after >= r.Enter)
            {
                EnterPhase(ref d, u, k, sourceId);
            }
            return after - before;
        }

        /// <summary>直接把累积量置为某值（P02 热震把腐蚀度置满、C01 置满着火度这类“置 1.0”）：不看抗性，看免疫窗与上限；到阈值当场进入。</summary>
        internal static void EmgSetAcc(ref CombatData d, int u, int k, float value, int sourceId = 0)
        {
            float cur = AccOf(d.EmgUnit[u], k);
            if (value > cur)
            {
                EmgAddAcc(ref d, u, k, value - cur, sourceId, raw: true);
            }
        }

        private static void EnterPhase(ref CombatData d, int u, int k, int sourceId)
        {
            EmgPhaseRule r = d.EmgPhases[k];
            EmgUnitState s = d.EmgUnit[u];
            s.PhaseMask = (ushort)(s.PhaseMask | (1 << k));
            PhsTPut(ref s, k, math.max(0f, r.MinDwell));
            if (k == EmgPhaseConst.Charge)
            {
                AccPut(ref s, k, 0f); // FG19B §3.3：放电后电荷清零
            }
            d.EmgUnit[u] = s;
            EmgScalars es = d.EmgS[0];
            es.PhaseEnters++;
            d.EmgS[0] = es;
            double2 at = d.Pos[u];
            int owner = d.Id[u];
            int chain = d.EmgCtx.Chain;
            byte depth = d.EmgCtx.Depth;
            switch (k)
            {
                case EmgPhaseConst.Chill:
                {
                    CombatCounters cc = d.Counters[0];
                    cc.Freezes++;
                    d.Counters[0] = cc;
                    if (d.Kind[u] == (byte)CombatUnitKind.Turret)
                    {
                        d.AimReadyAt[u] = 0; // 重炮瞄准到一半被冻住：这次瞄准作废（云端同口径）
                    }
                    Cue(ref d, CombatEventKind.ClarityCue, u, 0, r.MinDwell, at, 7); // 云端冻结提示保留做表现（ADR ⑭ #7）
                    break;
                }
                case EmgPhaseConst.Overheat:
                    EmgRequestPatch(ref d, EmgMatterConst.Molten, at, math.max(0.6f, d.Radius[u]), math.max(0f, r.P1), 0f, owner, chain, depth);
                    break;
                case EmgPhaseConst.Charge:
                    PhaseDischarge(ref d, u, r);
                    break;
                case EmgPhaseConst.Corrode:
                    EmgRequestPatch(ref d, EmgMatterConst.Debris, at, math.max(0.6f, d.Radius[u]), math.max(0f, r.P1), 0f, owner, chain, depth);
                    break;
            }
            PhaseNotify(ref d, u, (ushort)k, sourceId);
        }

        private static void ExitPhase(ref CombatData d, int u, int k, bool broken)
        {
            EmgPhaseRule r = d.EmgPhases[k];
            EmgUnitState s = d.EmgUnit[u];
            if ((s.PhaseMask & (1 << k)) == 0)
            {
                return;
            }
            s.PhaseMask = (ushort)(s.PhaseMask & ~(1 << k));
            PhsTPut(ref s, k, math.max(0f, r.Guard));
            if (k == EmgPhaseConst.Chill || k == EmgPhaseConst.Charge || broken)
            {
                AccPut(ref s, k, 0f); // 解冻后寒量归零（云端同口径）；被打断（碎裂 / 热震 / 进水）的清零
            }
            d.EmgUnit[u] = s;
            PhaseNotify(ref d, u, (ushort)(k | EmgPhaseConst.CodeExit | (broken ? EmgPhaseConst.CodeBroken : 0)), 0);
        }

        private static void PhaseNotify(ref CombatData d, int u, ushort code, int sourceId)
        {
            double2 at = d.Pos[u];
            int k = code & 15;
            Cue(ref d, CombatEventKind.PhaseCue, u, sourceId, AccOf(d.EmgUnit[u], k), at, (byte)code);
            if (d.EmgCtx.Chain > 0)
            {
                EmgChainLog(ref d, EmgChainNode.Phase, d.EmgCtx.Chain, d.EmgCtx.Depth, 901 + 2 * k + 1, d.Id[u], at, sourceId, (byte)code);
            }
            if (d.EmgListen != 0u && EmgSiteListens(ref d, EmgEventKind.Phase))
            {
                EmgUnitEvent(ref d, EmgEventKind.Phase, u, sourceId, AccOf(d.EmgUnit[u], k), 0f, code, at);
            }
        }

        /// <summary>麻痹放电（FG19B §2.9 topo.arc：0.5 电荷 → 4 米内 ≤ 3 个导体）：导电 ≥ 0.3 的活单位，按距离、同距按 Id 升序；只累积不当场进入（放电连锁由下一步结算，受 guard 2 秒限制）。</summary>
        private static void PhaseDischarge(ref CombatData d, int u, in EmgPhaseRule r)
        {
            int max = (int)math.max(0f, r.P3);
            if (max <= 0 || !(r.P1 > 0f) || !(r.P2 > 0f))
            {
                return;
            }
            var picks = new NativeList<float2>(max + 1, Allocator.Temp); // (距离, 槽位)
            for (int v = 0; v < d.Count; v++)
            {
                if (v == u || !d.IsAlive(v) || d.Kind[v] == (byte)CombatUnitKind.Structure)
                {
                    continue;
                }
                float dist = (float)math.distance(d.Pos[u], d.Pos[v]);
                if (dist > r.P2 || !EmgUnitConducts(ref d, v)) // EMG2-REACT-02：C07 泥浆单位不进传导图，放电也跳不到
                {
                    continue;
                }
                int at = picks.Length;
                for (int j = 0; j < picks.Length; j++)
                {
                    float2 o = picks[j];
                    if (dist < o.x || (dist == o.x && d.Id[v] < d.Id[(int)o.y]))
                    {
                        at = j;
                        break;
                    }
                }
                if (at < max)
                {
                    picks.InsertRangeWithBeginEnd(at, at + 1);
                    picks[at] = new float2(dist, v);
                    if (picks.Length > max)
                    {
                        picks.RemoveAt(picks.Length - 1);
                    }
                }
            }
            for (int j = 0; j < picks.Length; j++)
            {
                int v = (int)picks[j].y;
                EmgAddAcc(ref d, v, EmgPhaseConst.Charge, r.P1, d.Id[u], enterNow: false);
                Cue(ref d, CombatEventKind.PhaseCue, v, d.Id[u], r.P1, d.Pos[v], 64); // 64 = 电弧连线（表现：从放电者到导体）
            }
            picks.Dispose();
        }

        /// <summary>
        /// 挂标签 = 带寒 / 带酸 / 带火载荷（FG19B §3.1 / §3.3）：冻结位不再是持续标签（易伤 +25% 取消，FG19 第 14 节 #7），换成寒量 tagGain 并从这次的位里去掉
        /// （位 3 只作冻结相态的显示，头顶图标读相态块）；酸蚀位 → 腐蚀度 tagGain、燃烧位 → 着火度 tagGain（标签照常挂）。返回去掉冻结位后的掩码。
        /// </summary>
        internal static uint PhaseTagGain(ref CombatData d, int k, uint mask, int source, out float elec)
        {
            elec = 0f;
            int src = source >= 0 && source < d.Count ? d.Id[source] : 0;
            // EMG2-REACT-02（FG19B §3.1 位 4，DEBT-EMG2PHS01-04）：电击不再是持续标签 = 一次 en.elec 注入（phase_charge.tagGain）；本位只留 0.5 秒“受过电”闪烁（不易伤、不叠层）。
            if (d.ShockBit != 0u && (mask & d.ShockBit) != 0u)
            {
                mask &= ~d.ShockBit;
                elec = d.EmgPhases[EmgPhaseConst.Charge].TagGain;
                double flash = d.Scalars[0].Time + 0.5;
                int idx = k * CombatConst.StatusBitStride + math.tzcnt(d.ShockBit);
                d.Status[k] |= d.ShockBit;
                d.StatusBitUntil[idx] = math.max(d.StatusBitUntil[idx], flash);
                d.StatusUntil[k] = math.max(d.StatusUntil[k], flash);
                if (!EmgElecShed(ref d, k, elec))
                {
                    EmgAddAcc(ref d, k, EmgPhaseConst.Charge, elec, src); // C04 成立时本单位不得电荷（沾染行把电跳走）
                }
            }
            if (d.FrozenBit != 0u && (mask & d.FrozenBit) != 0u)
            {
                mask &= ~d.FrozenBit;
                if (d.Clarity[k].Air == 0)
                {
                    EmgAddAcc(ref d, k, EmgPhaseConst.Chill, d.EmgPhases[EmgPhaseConst.Chill].TagGain, src);
                }
            }
            if (d.AcidBit != 0u && (mask & d.AcidBit) != 0u)
            {
                EmgAddAcc(ref d, k, EmgPhaseConst.Corrode, d.EmgPhases[EmgPhaseConst.Corrode].TagGain, src);
            }
            if (d.FireBit != 0u && (mask & d.FireBit) != 0u)
            {
                EmgAddAcc(ref d, k, EmgPhaseConst.Ignite, d.EmgPhases[EmgPhaseConst.Ignite].TagGain, src);
            }
            return mask;
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>
        /// 每步逐单位（StepClarity 的单位循环里，取代云端 StepChill）：免疫窗倒计时、衰减、阈值进入（直接写累积量的来源，如磁化传播、导电水洼）、
        /// 相态期间效果与退出、接触增益（站在液氮 / 酸 / 污泥 / 明火里）。没有任何累积量 / 相态 / 计时的单位一条比较就返回。
        /// </summary>
        internal static void StepPhase(ref CombatData d, int i, float dt, double now)
        {
            EmgUnitState s0 = d.EmgUnit[i];
            uint matter = s0.PrevMatterMask;
            if (s0.PhaseMask == 0 && math.all(s0.AccA == 0f) && math.all(s0.AccB == 0f) && math.all(s0.PhsTA == 0f) && math.all(s0.PhsTB == 0f) && matter == 0u)
            {
                return;
            }
            if (matter != 0u && !IsAir(ref d, i))
            {
                PhaseContact(ref d, i, matter, dt);
            }
            for (int k = 0; k < EmgPhaseConst.Count && d.IsAlive(i); k++)
            {
                EmgPhaseRule r = d.EmgPhases[k];
                EmgUnitState s = d.EmgUnit[i];
                float acc = AccOf(s, k);
                float t = PhsTOf(s, k);
                bool inPhase = (s.PhaseMask & (1 << k)) != 0;
                if (!inPhase && acc == 0f && t == 0f)
                {
                    continue;
                }
                float rate = 1f;
                if (k == EmgPhaseConst.Chill)
                {
                    rate = ThawRate(ref d, i, now, r);
                }
                else if (k == EmgPhaseConst.Overheat && d.WetBit != 0u && (d.Status[i] & d.WetBit) != 0u && d.StatusActive(i, now))
                {
                    rate = math.max(1f, r.P4); // 浸湿时结构积热散得快
                }
                if (inPhase)
                {
                    if (r.ExitMode == EmgPhaseConst.ExitTimed)
                    {
                        t -= dt * rate; // 冻结：燃烧 / 高热解冻更快（云端 FrozenT -= dt × thaw）
                    }
                    else
                    {
                        t = math.max(0f, t - dt);
                        acc = math.max(0f, acc - r.Decay * rate * dt);
                    }
                    PhsTPut(ref s, k, t);
                    AccPut(ref s, k, acc);
                    d.EmgUnit[i] = s;
                    bool done = r.ExitMode == EmgPhaseConst.ExitTimed ? t <= 0f : acc < r.Exit && t <= 0f;
                    if (done)
                    {
                        ExitPhase(ref d, i, k, false);
                        continue;
                    }
                    PhaseDuring(ref d, i, k, r, dt, now, matter);
                }
                else
                {
                    if (t > 0f)
                    {
                        t = math.max(0f, t - dt);
                    }
                    acc = math.max(0f, acc - r.Decay * rate * dt);
                    PhsTPut(ref s, k, t);
                    AccPut(ref s, k, acc);
                    d.EmgUnit[i] = s;
                    if (t <= 0f && acc >= r.Enter && r.ExitMode != EmgPhaseConst.ExitDeath)
                    {
                        EnterPhase(ref d, i, k, 0);
                    }
                }
            }
        }

        /// <summary>受寒消退 / 冻结倒计时倍率：燃烧 + (p2 − 1)、武器积热过半 + (p3 − 1)（云端 1 + 2 + 1）。</summary>
        private static float ThawRate(ref CombatData d, int i, double now, in EmgPhaseRule r)
        {
            float thaw = 1f;
            if (IsBurning(ref d, i, now) || InPhase(ref d, i, EmgPhaseConst.Ignite))
            {
                thaw += math.max(0f, r.P2 - 1f);
            }
            int w = d.Weapon[i];
            if (w >= 0 && w < d.Weapons.Length)
            {
                float oh = d.Weapons[w].OverheatAt;
                if (oh > 0f && d.Heat[i] >= oh * 0.5f)
                {
                    thaw += math.max(0f, r.P3 - 1f);
                }
            }
            return thaw;
        }

        /// <summary>站在物质里（上一个接触节拍的物质掩码）：冻结中碰明火 / 熔融 → 热震、液氮 → 寒量、酸（含酸沼虚拟源）/ 污泥 → 腐蚀度（浸湿 × 0.5）、明火 → 着火度（× (p4 + 可燃)）。</summary>
        private static void PhaseContact(ref CombatData d, int i, uint matter, float dt)
        {
            // EMG2-PHS-01 审查修复（第 1 轮）：冻结中被推进 / 站进明火、熔融斑块 → 热震（FG19B P02 第二条触发，不看热抗折减后的单次热量；洗礼塔泄压阶段“先冻再烧”的玩家路径）
            if ((matter & ((1u << EmgMatterConst.Fire) | (1u << EmgMatterConst.Molten))) != 0u && IsFrozen(ref d, i))
            {
                ThermalShock(ref d, i, PhaseEnergyRule(ref d, EmgConst.PortHeat));
            }
            if ((matter & (1u << EmgMatterConst.Cryo)) != 0u && d.EmgPhases[EmgPhaseConst.Chill].ContactGain > 0f && !LegacyCryoUnder(ref d, i))
            {
                EmgAddAcc(ref d, i, EmgPhaseConst.Chill, d.EmgPhases[EmgPhaseConst.Chill].ContactGain * dt);
            }
            EmgPhaseRule c = d.EmgPhases[EmgPhaseConst.Corrode];
            float corrode = ((matter & (1u << EmgMatterConst.Acid)) != 0u ? c.ContactGain : 0f) + ((matter & (1u << EmgMatterConst.Sludge)) != 0u ? c.P4 : 0f);
            if (corrode > 0f)
            {
                bool wet = d.WetBit != 0u && (d.Status[i] & d.WetBit) != 0u && d.StatusActive(i, d.Scalars[0].Time);
                EmgAddAcc(ref d, i, EmgPhaseConst.Corrode, corrode * dt * (wet ? c.P3 : 1f));
            }
            EmgPhaseRule f = d.EmgPhases[EmgPhaseConst.Ignite];
            if ((matter & (1u << EmgMatterConst.Fire)) != 0u && f.ContactGain > 0f)
            {
                float flam = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, i)).Flammable;
                EmgAddAcc(ref d, i, EmgPhaseConst.Ignite, f.ContactGain * (f.P4 + flam) * dt);
            }
        }

        /// <summary>迁入的液氮池（云端喷淋池迁入斑块）自己按区域节拍加受寒（ApplyChill，数值与云端一致）：站在它里面不再按接触重复加。</summary>
        private static bool LegacyCryoUnder(ref CombatData d, int i)
        {
            for (int p = 0; p < d.Patches.Length; p++)
            {
                CombatZone z = d.Patches[p];
                if (PatchAlive(z) && IsLegacy(z) && z.Chill > 0f && math.distance(z.Pos, d.Pos[i]) <= z.Radius + d.Radius[i])
                {
                    return true;
                }
            }
            return false;
        }

        private static void PhaseDuring(ref CombatData d, int i, int k, in EmgPhaseRule r, float dt, double now, uint matter)
        {
            switch (k)
            {
                case EmgPhaseConst.Overheat:
                    // 每 p3 秒再漏 p2 份熔融金属（按游戏时刻跨格判定，不另存计时；暂停不推进）
                    if (r.P3 > 0f && r.P2 > 0f && math.floor(now / r.P3) != math.floor((now - dt) / r.P3))
                    {
                        EmgRequestPatch(ref d, EmgMatterConst.Molten, d.Pos[i], math.max(0.6f, d.Radius[i]), r.P2, 0f, d.Id[i], 0, 0);
                    }
                    break;
                case EmgPhaseConst.Ignite:
                {
                    if ((matter & ((1u << EmgMatterConst.Water) | (1u << EmgMatterConst.Coolant) | (1u << EmgMatterConst.Cryo))) != 0u)
                    {
                        ExitPhase(ref d, i, k, true); // 进水 / 冷却液 / 液氮：立即熄灭
                        return;
                    }
                    float acc = AccOf(d.EmgUnit[i], k);
                    int level = math.clamp(1 + (int)math.floor((acc - r.Enter) / math.max(0.01f, r.P1)), 1, 3);
                    BurnTick(ref d, i, level, dt, now);
                    if (r.P2 > 0f)
                    {
                        EmgAddUnitHeat(ref d, i, r.P2 * dt);
                    }
                    if (r.P3 > 0f && (matter & ((1u << EmgMatterConst.Oil) | (1u << EmgMatterConst.Sludge) | (1u << EmgMatterConst.Polymer))) != 0u)
                    {
                        EmgInjectPatches(ref d, d.Pos[i], d.Radius[i], EmgConst.PortHeat, r.P3 * dt * 4f, 0, 0); // 向脚下可燃物放热（≈ 每传导节拍 p3）
                    }
                    break;
                }
            }
        }

        /// <summary>燃烧持续伤害 = 燃烧标签每层伤害 × L；身上同时挂着燃烧标签时只补差（取大，不叠两份）。</summary>
        private static void BurnTick(ref CombatData d, int i, int level, float dt, double now)
        {
            if (d.FireBit == 0u)
            {
                return;
            }
            FxOf(ref d, d.FireBit, out float perStack, out _, out _);
            if (!(perStack > 0f))
            {
                return;
            }
            int tagStacks = 0;
            if ((d.Status[i] & d.FireBit) != 0u && d.StatusActive(i, now))
            {
                int b = math.tzcnt(d.FireBit);
                if (d.StatusBitUntil[i * CombatConst.StatusBitStride + b] > now)
                {
                    tagStacks = (int)((d.StatusStacks[i] >> (b * 2)) & 3UL);
                }
            }
            float dps = perStack * math.max(0, level - tagStacks);
            if (dps > 0f)
            {
                DamageFrom(ref d, i, dps * dt, -1, EmgCode.SrcZone);
            }
        }

        /// <summary>磁化（弱磁源）：4 米内弹体不分阵营按 p2 度 / 秒 × 弹体磁性转向本单位（FG19B §2.9 topo.redirect）。只在本步有磁化单位时 O(磁化单位 × 弹体)。</summary>
        /// <summary>
        /// 磁化单位（弱磁源，FG19B §3.3）把 p1 米内非固态、磁性 ≥ 0.6 的斑块（金属碎屑）以 p3 米 / 秒 × 磁性拉向自己；贴到单位身边（0.5 米内）就停。
        /// O(磁化单位 × 斑块)，没有磁化单位时一遍单位扫描就返回（Main/Sim）。
        /// </summary>
        private static void EmgMagnetizedDebris(ref CombatData d, in EmgPhaseRule r, float dt)
        {
            bool moved = false;
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || (d.EmgUnit[u].PhaseMask & (1 << EmgPhaseConst.Magnetize)) == 0)
                {
                    continue;
                }
                for (int j = 0; j < d.Patches.Length; j++)
                {
                    CombatZone z = d.Patches[j];
                    if (!PatchAlive(z) || PatchWorld(z))
                    {
                        continue;
                    }
                    EmgMatter m = MatterOf(ref d, z.Matter);
                    if (m.Solid != 0 || m.Magnetic < 0.6f)
                    {
                        continue;
                    }
                    double2 to = d.Pos[u] - z.Pos;
                    double dist = math.length(to);
                    if (dist > r.P1 || dist < 0.5)
                    {
                        continue;
                    }
                    z.Pos += to / dist * math.min(dist - 0.5, r.P3 * m.Magnetic * dt);
                    d.Patches[j] = z;
                    moved = true;
                }
            }
            if (moved)
            {
                MarkHashDirty(ref d);
            }
        }

        internal static void StepMagnetized(ref CombatData d, float dt)
        {
            EmgPhaseRule r = d.EmgPhases[EmgPhaseConst.Magnetize];
            if (r.P1 > 0f && r.P3 > 0f && d.EmgS[0].PatchLive > 0)
            {
                EmgMagnetizedDebris(ref d, r, dt);
            }
            if (!(r.P1 > 0f) || !(r.P2 > 0f) || d.Projectiles.Length == 0)
            {
                return;
            }
            float mag = EmgProfileAt(ref d, EmgConst.SlotProjectile).Magnetic;
            float turn = math.radians(r.P2) * math.max(0f, mag) * dt;
            if (!(turn > 0f))
            {
                return;
            }
            for (int u = 0; u < d.Count; u++)
            {
                if (!d.IsAlive(u) || (d.EmgUnit[u].PhaseMask & (1 << EmgPhaseConst.Magnetize)) == 0)
                {
                    continue;
                }
                for (int p = 0; p < d.Projectiles.Length; p++)
                {
                    CombatProjectile pr = d.Projectiles[p];
                    if (pr.Owner == d.Id[u])
                    {
                        continue;
                    }
                    double2 to = d.Pos[u] - pr.Pos;
                    double dist = math.length(to);
                    float speed = math.length(pr.Vel);
                    if (dist > r.P1 || dist < 1e-4 || !(speed > 0f))
                    {
                        continue;
                    }
                    float cur = math.atan2(pr.Vel.y, pr.Vel.x);
                    float want = (float)math.atan2(to.y, to.x);
                    float delta = math.atan2(math.sin(want - cur), math.cos(want - cur));
                    float a = cur + math.clamp(delta, -turn, turn);
                    pr.Vel = new float2(math.cos(a), math.sin(a)) * speed;
                    d.Projectiles[p] = pr;
                }
            }
        }

        // ─────────────────────────────── 冻结的打断（P01 碎裂 / P02 热震）───────────────────────────────

        /// <summary>冻结中受单次动能（弹体命中：伤害 × 载体动能系数）：≥ P01 阈值 × 目标脆性 → 碎裂：破片 6 片（半径 &gt; 1.5 米 8 片）、立即退出冻结进入免疫窗、附带伤害；完整度 ≤ 0.25 直接击毁不留残骸。</summary>
        internal static void PhaseKinetic(ref CombatData d, int t, float kinetic, int attacker)
        {
            if (!(kinetic > 0f) || !IsFrozen(ref d, t) || !d.IsAlive(t))
            {
                return;
            }
            float brittle = EmgProfileAt(ref d, EmgUnitProfileIndex(ref d, t)).Brittle;
            brittle = brittle > 0f ? brittle : 1f; // 没填脆性的档案按 1（钢）
            int ri = PhaseEnergyRule(ref d, EmgConst.PortKinetic);
            float threshold = ri >= 0 ? d.EmgRules[ri].C1 : 24f;
            if (kinetic < threshold * brittle)
            {
                return;
            }
            float side = ri >= 0 && d.EmgRules[ri].Side == EmgRuleConst.SideDamage ? d.EmgRules[ri].SideValue : 20f;
            int attackerId = attacker >= 0 && attacker < d.Count ? d.Id[attacker] : 0;
            ExitPhase(ref d, t, EmgPhaseConst.Chill, true);
            EmgEntityParams ep = d.EmgEntParams[0];
            int n = d.Radius[t] > 1.5f ? 8 : 6;
            EmgRule fr = ri >= 0 ? d.EmgRules[ri] : default;
            for (int j = 0; j < n && d.EmgCtx.Depth < EmgConst.MaxDepth; j++) // “一锤碎一片”由链深封顶（FG19B §2.9）
            {
                float a = 2f * math.PI * j / n;
                EmgSpawnFragment(ref d, d.Pos[t], new float2(math.cos(a), math.sin(a)), d.Id[t], CombatFaction.Neutral, (byte)(d.EmgCtx.Depth + 1), d.EmgCtx.Chain,
                    fr.P2 > 0f ? fr.P2 : ep.FragDamage, fr.P3 > 0f ? fr.P3 : ep.FragSpeed, fr.P4 > 0f ? fr.P4 : ep.FragRange); // FG19B §2.9 碎裂 P01：每片 10 / 18 米每秒 / 7 米
            }
            if (ri >= 0)
            {
                EmgRuleSettle(ref d, d.EmgRules[ri], d.EmgCtx.Chain, d.EmgCtx.Depth, attackerId, d.Pos[t], 0);
            }
            // 审查修复（第 1 轮 P2）：附带伤害要再走 DamageUnit，但不是这发弹体的又一次命中——临时摘掉宿主程序（Prog = −1），
            // EmgOnDamage 不再重入命中注能（同一发不注两次）；直接击毁的外部血量致死请求由 EmgDestroyDirect 同样摘掉，普通单位走 Kill，击杀归属照旧。
            bool kill = d.MaxHp[t] > 0f && d.Hp[t] / d.MaxHp[t] <= 0.25f;
            EmgCtx saved = d.EmgCtx;
            if (!kill)
            {
                EmgCtx side0 = saved;
                side0.Prog = -1;
                d.EmgCtx = side0;
            }
            if (kill)
            {
                EmgDestroyDirect(ref d, t, attacker);
            }
            else if (side > 0f)
            {
                DamageFrom(ref d, t, side, attacker, EmgCode.SrcZone);
            }
            d.EmgCtx = saved;
        }

        /// <summary>冻结中受单次注热 ≥ P02 阈值（或 phase_chill.p4）→ 热震：立即解冻进入免疫窗，崩出 4 片装甲破片，腐蚀度置满进入破甲。</summary>
        internal static void PhaseHeat(ref CombatData d, int t, float q)
        {
            if (!(q > 0f) || !IsFrozen(ref d, t))
            {
                return;
            }
            int ri = PhaseEnergyRule(ref d, EmgConst.PortHeat);
            float threshold = ri >= 0 ? d.EmgRules[ri].C1 : d.EmgPhases[EmgPhaseConst.Chill].P4;
            if (q < threshold)
            {
                return;
            }
            ThermalShock(ref d, t, ri);
        }

        /// <summary>P02 热震结算（单次注热达阈值，或冻结中站进明火 / 熔融斑块，FG19B P02“单次注入 ≥ 30，或被推进明火 / 熔融斑块”）：立即解冻、崩 4 片装甲破片、腐蚀度置满进入破甲。</summary>
        private static void ThermalShock(ref CombatData d, int t, int ri)
        {
            ExitPhase(ref d, t, EmgPhaseConst.Chill, true);
            EmgEntityParams ep = d.EmgEntParams[0];
            EmgRule fr = ri >= 0 ? d.EmgRules[ri] : default;
            for (int j = 0; j < 4; j++)
            {
                float a = 2f * math.PI * j / 4f + 0.39f;
                EmgSpawnFragment(ref d, d.Pos[t], new float2(math.cos(a), math.sin(a)), d.Id[t], CombatFaction.Neutral, (byte)(d.EmgCtx.Depth + 1), d.EmgCtx.Chain,
                    fr.P2 > 0f ? fr.P2 : ep.FragDamage, fr.P3 > 0f ? fr.P3 : ep.FragSpeed, fr.P4 > 0f ? fr.P4 : ep.FragRange); // FG19B §2.9 热震装甲破片：每片 6 / 14 米每秒 / 4 米
            }
            if (ri >= 0)
            {
                EmgRuleSettle(ref d, d.EmgRules[ri], d.EmgCtx.Chain, d.EmgCtx.Depth, 0, d.Pos[t], 0);
            }
            EmgSetAcc(ref d, t, EmgPhaseConst.Corrode, 1f);
        }

        /// <summary>一次命中的动能系数（FG19B §6.3，按出手者武器的载体）：重炮 2、射弹 1、格斗 2.2、无人机 0.5、力场 0.2、布区 0.3。</summary>
        private static float HitKineticOf(ref CombatData d, int attacker)
        {
            int w = d.Weapon[attacker];
            if (w < 0 || w >= d.Weapons.Length)
            {
                return 0f;
            }
            CombatWeapon wp = d.Weapons[w];
            EmgEntityParams ep = d.EmgEntParams[0];
            if (wp.Mode == CombatWeaponMode.Cannon)
            {
                return ep.KineticHeavy;
            }
            if (wp.BeamHit != 0)
            {
                return ep.KineticBeam; // EMG2-REACT-02（DEBT-EMG2PHS01-01，FG19B §6.3）：光束动能系数 0，碎不了冻结目标
            }
            return wp.Reading.Carrier switch
            {
                CombatCarrier.Projectile => ep.KineticProjectile,
                CombatCarrier.Melee => ep.KineticMelee,
                CombatCarrier.Summon => ep.KineticDrone,
                CombatCarrier.Aura => ep.KineticAura,
                CombatCarrier.Field => ep.KineticField,
                _ => 0f,
            };
        }

        /// <summary>相态 × 能量规则行（P01 冻结 × 动能、P02 冻结 × 热）：按端口码找，不认规则 ID。没有 = −1（用表缺省阈值）。</summary>
        private static int PhaseEnergyRule(ref CombatData d, ushort energyPort)
        {
            for (int i = 0; i < d.EmgRules.Length; i++)
            {
                EmgRule r = d.EmgRules[i];
                if (r.Pair == EmgRuleConst.PairPhaseEnergy && r.A == PortFrozen && r.B == energyPort)
                {
                    return i;
                }
            }
            return -1;
        }

        // ─────────────────────────────── 抗性覆盖 ───────────────────────────────

        /// <summary>运行时抗性覆盖（ADR ⑦ SetResist）：k = 抗性列，value 0～1。</summary>
        internal static void SetResistOverride(ref CombatData d, int u, int k, float value)
        {
            EmgUnitState s = d.EmgUnit[u];
            byte b = (byte)math.clamp((int)math.round(math.saturate(value) * 255f), 0, 255);
            switch (k)
            {
                case 0: s.Res0 = b; break;
                case 1: s.Res1 = b; break;
                case 2: s.Res2 = b; break;
                case 3: s.Res3 = b; break;
                case 4: s.Res4 = b; break;
                default: s.Res5 = b; break;
            }
            d.EmgUnit[u] = s;
        }

        /// <summary>
        /// 免疫位 → 抗性覆盖（ADR ⑭ #9）：酸蚀位 → 腐蚀抗性 1、冻结位 → 寒抗 1、电击位 → 电抗 1；只有这一位刚从掩码里摘掉（<paramref name="before"/> 有、现在没有）时才把满值退回 0（别的覆盖值、直接 SetResist 写的满值不动）。
        /// 单位登记、SetImmuneMask、读档（快照 15 / 16 的 ImmuneMask，以及 17 版早期没写覆盖的档；before = 0，只挂不摘）都走这里，结果与当场设置一致。
        /// </summary>
        internal static void SyncImmuneResist(ref CombatData d, int u, uint before = 0u)
        {
            uint m = d.Clarity[u].ImmuneMask;
            EmgUnitState s = d.EmgUnit[u];
            s.Res3 = MapImmune(s.Res3, d.AcidBit, m, before);
            s.Res0 = MapImmune(s.Res0, d.FrozenBit, m, before);
            s.Res2 = MapImmune(s.Res2, d.ShockBit, m, before);
            d.EmgUnit[u] = s;
        }

        private static byte MapImmune(byte cur, uint bit, uint mask, uint before) =>
            bit == 0u ? cur : (mask & bit) != 0u ? (byte)255 : (before & bit) != 0u && cur == 255 ? (byte)0 : cur;

        /// <summary>写 15 / 16 版快照（自检造旧档）：寒量 / 冻结剩余 / 解冻免疫折回云端字段（<see cref="MigrateClarityPhase"/> 的逆映射）。</summary>
        internal static CombatClarityUnit ClarityForOldFormat(ref CombatData d, int u)
        {
            CombatClarityUnit c = d.Clarity[u];
            EmgUnitState s = d.EmgUnit[u];
            bool frozen = (s.PhaseMask & (1 << EmgPhaseConst.Chill)) != 0;
            c.Chill = math.saturate(s.AccA.x);
            c.FrozenT = frozen ? math.max(0f, s.PhsTA.x) : 0f;
            c.ThawGuardT = frozen ? 0f : math.max(0f, s.PhsTA.x);
            return c;
        }

        /// <summary>读档后：云端单位受寒 / 冻结 / 解冻免疫（CombatClarityUnit.Chill / FrozenT / ThawGuardT）并入寒量累积量与相态（ADR ⑭ #5、⑩ 15 → 17 映射），旧字段清零；免疫位映射成抗性覆盖。</summary>
        internal static void MigrateClarityPhase(ref CombatData d)
        {
            for (int u = 0; u < d.Count; u++)
            {
                CombatClarityUnit c = d.Clarity[u];
                if (c.Chill > 0f || c.FrozenT > 0f || c.ThawGuardT > 0f)
                {
                    EmgUnitState s = d.EmgUnit[u];
                    if (c.FrozenT > 0f)
                    {
                        s.PhaseMask = (ushort)(s.PhaseMask | (1 << EmgPhaseConst.Chill));
                        s.AccA.x = math.max(s.AccA.x, 1f);
                        s.PhsTA.x = c.FrozenT;
                    }
                    else
                    {
                        s.AccA.x = math.max(s.AccA.x, c.Chill);
                        s.PhsTA.x = math.max(s.PhsTA.x, c.ThawGuardT);
                    }
                    d.EmgUnit[u] = s;
                    c.Chill = 0f;
                    c.FrozenT = 0f;
                    c.ThawGuardT = 0f;
                    d.Clarity[u] = c;
                }
                if (c.ImmuneMask != 0u)
                {
                    SyncImmuneResist(ref d, u);
                }
            }
        }
    }
}
