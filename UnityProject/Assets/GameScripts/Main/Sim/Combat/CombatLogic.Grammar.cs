using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-GRM-01（FG19 第 2.4 节语法端口、FGR-EMG-015 / 017 / 018；FG19A 第 1.1 节、第 2.1.1 节、第 3.1～3.2 节；ADR-EMG-001 ②③）：弹道修饰的内核动词。
    /// 内核只按指令行的动词码（<see cref="EmgVerb"/>，热更层按武器载体从 fg.TbPortCarrier 解析好）与参数执行，不认任何内容 ID（FGR-EMG-013）：
    /// · 飞行载荷（SpawnFlight 根弹体、无人机实体化子弹、子弹药）：寻的 Steer、真穿透 PassThrough、真反弹 Ricochet（单位面 / 障碍面分开计）、多份载荷 Multi、
    ///   折返 Return、绕行 Orbit、越障抛射 Lob、随机侧偏 Drift；生成时按自己节点的指令写进弹体（<see cref="EmgInitFlight"/>），每步在 StepProjectiles 里执行。
    /// · 即时出手（格斗 / 力场 / 布区）：根节点上的旧动词由热更层折进武器读法（CombatSite.FoldProgramReadings）；新动词在出手时读根节点执行——
    ///   格斗侧偏、力场多个小脉冲 / 中心游移 / 越障 / 不被残骸挡、布区多块小落区 / 落点散开 / 越障 / 越过残骸。
    /// · 连锁（旧动词 chain）按指令行的目标偏好挑目标：已标记 &gt; 导电 &gt; 最近，只有“已标记”一种偏好时只跳已标记（FGR-EMG-017、FG19A A5）。
    /// 随机一律用无状态哈希（<see cref="EmgRng"/>，FGR-EMG-213）。没有程序 / 程序里没有这些动词时逐字走旧逻辑。
    /// </summary>
    public static partial class CombatLogic
    {
        /// <summary>一个节点上的弹道修饰汇总（出手 / 生成时现算，不进快照；同一动词多行时幅度取大、偏好按位并，与编译器同键合并同一口径）。</summary>
        internal struct EmgMods
        {
            public float SteerMax, SteerRate, SteerRadius;
            public EmgInstrFlags SteerPref;
            public byte Pierce, BounceUnit, BounceObs;
            public float BounceRange;
            public byte Multi;
            public float MultiFan, MultiRatio, MultiRadius;
            public byte Return;
            public float ReturnRatio;
            public byte Orbit;
            public float OrbitRadius, OrbitLaps;
            public byte Lob;
            public float DriftAmount, DriftB, DriftEvade;
            public byte Chain;
            public float ChainRange, ChainFalloff;
            public EmgInstrFlags ChainPref;
            // EMG1-FW-01（DEBT-EMG1GRM01-05）：子节点上的旧读法动词（只在 node > 0 时汇总；根节点的由热更层折进读法）
            public float DmgMul, ArmorPierce, BlastR, BlastRatio, ExtraRatio, ExtraR, Reach, Knock, PullA, PullR, SweepR, SweepRatio, EchoRatio;
            public byte ExtraHits;
            public bool Any;
        }

        /// <summary>子节点旧动词行的参数：编译器按 fg.TbPortCarrier 的 mag 列置了 MagA / B / C 位的那一个乘声明幅度，其余原值。</summary>
        private static float MagOf(in EmgInstr x, float v, EmgInstrFlags bit) => (x.Flags & bit) != 0 ? v * x.Amount : v;

        /// <summary>EMG1-FW-01（DEBT-EMG1GRM01-03）：节点有没有读 <paramref name="port"/> 的读端行（触发 / 条件 / 感知 / 作用域；溢出行不算）。读端随修饰继承到子节点。O(节点指令数)。</summary>
        internal static bool EmgNodeReads(ref CombatData d, int prog, int node, ushort port)
        {
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
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Port == port && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>线段上有掩体（ter.cover：障碍或残骸）。</summary>
        private static bool EmgCoverBetween(ref CombatData d, double2 from, double2 to) =>
            (d.Obstacles.Length > 0 && !LineOfSight(ref d, from, to)) || (d.Wrecks.Length > 0 && EmgHashSegment(ref d, from, to, EmgHash.KindWreck, true, out _) >= 0);

        /// <summary>
        /// 多份载荷第 <paramref name="q"/> 份（共 <paramref name="n"/> 份，扇角 <paramref name="fan"/> 度）的方向。节点读 ter.cover（霰射的感知读端，FG19A 第 1.1 节第 3 行）时，
        /// 这一份在 <paramref name="dist"/> 米内被掩体挡住就按半个扇距一步步往外侧转（中间那份先左后右），最多 4 步，找到不被挡的方向为止；都被挡保持原方向。没读掩体 = 均分扇面。
        /// </summary>
        internal static float2 EmgPelletDir(ref CombatData d, int prog, int node, double2 from, float2 aim, double dist, int q, int n, float fan)
        {
            float off = n > 1 ? fan * ((float)q / (n - 1) - 0.5f) : 0f;
            float2 dir = Rotate(aim, math.radians(off));
            if (n <= 1 || !(dist > 0.5) || !EmgNodeReads(ref d, prog, node, EmgConst.PortCover) || !EmgCoverBetween(ref d, from, from + (double2)dir * dist))
            {
                return dir;
            }
            float step = math.max(5f, fan / (n - 1) * 0.5f);
            float side = math.abs(off) > 1e-3f ? math.sign(off) : 1f;
            for (int s = 1; s <= 4; s++)
            {
                for (int t = 0; t < (math.abs(off) > 1e-3f ? 1 : 2); t++)
                {
                    float sgn = t == 0 ? side : -side;
                    float2 alt = Rotate(aim, math.radians(off + sgn * step * s));
                    if (!EmgCoverBetween(ref d, from, from + (double2)alt * dist))
                    {
                        return alt;
                    }
                }
            }
            return dir;
        }

        private const uint SaltDrift = 0xD21Fu, SaltEvade = 0xE7A3u, SaltSwing = 0x3E1Au, SaltPulse = 0x9C55u;
        /// <summary>阶段 1 过渡的“导电”沾染位：浸湿（2）、导电粉尘（12）——与 fg.TbPortCarrier 排放映射 C 列同号（实体导电属性随 EMG1-ENT-01）。</summary>
        private const uint DefaultConductiveMask = (1u << 2) | (1u << 12);
        private const int MaxPellets = 8;
        private const int MaxInterceptsPerLap = 2;
        private const float MaxOrbitLaps = 9f;

        private static int Round(float v) => v <= 0f ? 0 : math.max(1, (int)math.round(v));

        /// <summary>读程序 <paramref name="prog"/> 第 <paramref name="node"/> 个节点的写端指令，汇总成 <see cref="EmgMods"/>。O(节点指令数 ≤ 128)。溢出行不执行。</summary>
        internal static bool EmgModsOf(ref CombatData d, int prog, int node, out EmgMods m)
        {
            m = default;
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
            int end = math.min(n.FirstInstr + n.InstrCount, d.EmgInstrs.Length);
            for (int i = n.FirstInstr; i < end; i++)
            {
                EmgInstr x = d.EmgInstrs[i];
                if (x.Role <= EmgConst.RoleLastRead || (x.Flags & EmgInstrFlags.Overflow) != 0)
                {
                    continue;
                }
                switch ((EmgVerb)x.Verb)
                {
                    case EmgVerb.Steer:
                        m.SteerMax = math.max(m.SteerMax, x.A * x.Amount);
                        m.SteerRate = math.max(m.SteerRate, x.B);
                        m.SteerRadius = math.max(m.SteerRadius, x.C);
                        m.SteerPref |= x.Flags & (EmgInstrFlags.PreferMarked | EmgInstrFlags.PreferHeat);
                        break;
                    case EmgVerb.PassThrough:
                        m.Pierce = (byte)math.max((int)m.Pierce, math.min(255, Round((x.A > 0f ? x.A : 1f) * x.Amount)));
                        break;
                    case EmgVerb.Ricochet:
                    {
                        byte count = (byte)math.min(255, Round((x.A > 0f ? x.A : 1f) * x.Amount));
                        int face = (int)math.round(x.Param2); // 声明 param2：1 = 单位面（跳弹），2 = 障碍 / 残骸 / 固态斑块面（壁撞），0 = 两种都算
                        if (face != 2)
                        {
                            m.BounceUnit = (byte)math.max((int)m.BounceUnit, count);
                        }
                        if (face != 1)
                        {
                            m.BounceObs = (byte)math.max((int)m.BounceObs, count);
                        }
                        m.BounceRange = math.max(m.BounceRange, x.B);
                        break;
                    }
                    case EmgVerb.Multi:
                        m.Multi = (byte)math.max((int)m.Multi, math.min(MaxPellets, Round(x.Amount)));
                        m.MultiFan = math.max(m.MultiFan, x.Param2);
                        m.MultiRatio = math.max(m.MultiRatio, x.A);
                        m.MultiRadius = math.max(m.MultiRadius, x.B);
                        break;
                    case EmgVerb.Return:
                        m.Return = 1;
                        m.ReturnRatio = math.max(m.ReturnRatio, x.A);
                        break;
                    case EmgVerb.Orbit:
                        m.Orbit = 1;
                        m.OrbitRadius = math.max(m.OrbitRadius, x.A);
                        m.OrbitLaps = math.min(MaxOrbitLaps, math.max(m.OrbitLaps, x.B)); // 绕行相位存在 ushort 定点（1/1024 弧度，上限约 10.2 圈）：圈数钳到 9，不会静默卡在上限绕不完
                        break;
                    case EmgVerb.Lob:
                        m.Lob = 1;
                        break;
                    case EmgVerb.Drift:
                        m.DriftAmount = math.max(m.DriftAmount, (x.A > 0f ? x.A : 1f) * x.Amount);
                        m.DriftB = math.max(m.DriftB, x.B);
                        m.DriftEvade = math.max(m.DriftEvade, x.C);
                        break;
                    case EmgVerb.Chain:
                        m.Chain = (byte)math.max((int)m.Chain, math.min(CombatConst.MaxReadingTargets, Round((x.A > 0f ? x.A : 1f) * x.Amount)));
                        m.ChainRange = math.max(m.ChainRange, x.B);
                        m.ChainFalloff = math.max(m.ChainFalloff, x.C);
                        m.ChainPref |= x.Flags & (EmgInstrFlags.PreferMarked | EmgInstrFlags.PreferConductive);
                        break;
                    // EMG1-FW-01（DEBT-EMG1GRM01-05）：子节点上的旧读法动词由子载荷自己结算（根节点的已折进读法，这里不再算一遍）
                    case EmgVerb.Dmg when node > 0:
                        m.DmgMul = (m.DmgMul > 0f ? m.DmgMul : 1f) * math.max(0f, MagOf(x, x.A, EmgInstrFlags.MagA));
                        break;
                    case EmgVerb.Armor when node > 0:
                        m.ArmorPierce = math.min(1f, m.ArmorPierce + math.max(0f, MagOf(x, x.A, EmgInstrFlags.MagA)));
                        break;
                    case EmgVerb.Blast when node > 0:
                        m.BlastR = math.max(m.BlastR, MagOf(x, x.A, EmgInstrFlags.MagA));
                        m.BlastRatio = math.max(m.BlastRatio, MagOf(x, x.B, EmgInstrFlags.MagB));
                        break;
                    case EmgVerb.Extra when node > 0:
                        m.ExtraHits = (byte)math.max((int)m.ExtraHits, math.min(CombatConst.MaxReadingTargets, Round(MagOf(x, x.A, EmgInstrFlags.MagA))));
                        m.ExtraRatio = math.max(m.ExtraRatio, MagOf(x, x.B, EmgInstrFlags.MagB));
                        m.ExtraR = math.max(m.ExtraR, MagOf(x, x.C, EmgInstrFlags.MagC));
                        break;
                    case EmgVerb.Reach when node > 0:
                        m.Reach += math.max(0f, MagOf(x, x.A, EmgInstrFlags.MagA));
                        break;
                    case EmgVerb.Knock when node > 0:
                        m.Knock = math.max(m.Knock, MagOf(x, x.A, EmgInstrFlags.MagA));
                        break;
                    case EmgVerb.Pull when node > 0:
                        m.PullA = math.max(m.PullA, MagOf(x, x.A, EmgInstrFlags.MagA));
                        m.PullR = math.max(m.PullR, MagOf(x, x.B, EmgInstrFlags.MagB));
                        break;
                    case EmgVerb.Sweep when node > 0:
                        m.SweepR = math.max(m.SweepR, MagOf(x, x.A, EmgInstrFlags.MagA));
                        m.SweepRatio = math.max(m.SweepRatio, MagOf(x, x.B, EmgInstrFlags.MagB));
                        break;
                    case EmgVerb.Echo when node > 0:
                        m.EchoRatio = math.max(m.EchoRatio, MagOf(x, x.C, EmgInstrFlags.MagC));
                        break;
                    default:
                        continue;
                }
                m.Any = true;
            }
            return m.Any;
        }

        private static float2 Rotate(float2 v, float radians)
        {
            math.sincos(radians, out float s, out float c);
            return new float2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private static bool Conductive(ref CombatData d, int k, double now)
        {
            uint mask = d.Config.EmgConductiveMask != 0u ? d.Config.EmgConductiveMask : DefaultConductiveMask;
            return (d.Status[k] & mask) != 0u && d.StatusUntil[k] > now;
        }

        // ─────────────────────────────── 挑目标（寻的 / 连锁 / 跳弹）───────────────────────────────

        /// <summary>
        /// 寻的挑目标（FG19A 第 0.3 节第 8 条：已标记 &gt; 热信号 &gt; 最近）：<paramref name="radius"/> 内、<paramref name="fwd"/> 前方半平面里的敌对可选中单位；
        /// 偏好“热信号”只在候选之间确有热差时生效（无热差取最近）。没有候选返回 −1。O(单位数)，只在生成 / 目标阵亡时调用。
        /// </summary>
        private static int EmgPickHoming(ref CombatData d, double2 pos, float2 fwd, float radius, byte want, EmgInstrFlags pref, int excludeId)
        {
            double now = d.Scalars[0].Time;
            double r2 = (double)radius * radius;
            int nearest = -1, marked = -1, hot = -1;
            double dn = double.MaxValue, dm = double.MaxValue, dh = double.MaxValue;
            float hMax = float.MinValue, hMin = float.MaxValue;
            for (int k = 0; k < d.Count; k++)
            {
                if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || d.Id[k] == excludeId)
                {
                    continue;
                }
                double2 to = d.Pos[k] - pos;
                double dist2 = math.lengthsq(to);
                if (dist2 > r2 || (math.lengthsq(fwd) > 1e-12f && math.dot((double2)fwd, to) < 0))
                {
                    continue;
                }
                if (dist2 < dn)
                {
                    dn = dist2;
                    nearest = k;
                }
                if (d.MarkedUntil[k] > now && dist2 < dm)
                {
                    dm = dist2;
                    marked = k;
                }
                float h = d.Heat[k];
                hMin = math.min(hMin, h);
                if (h > hMax || (h == hMax && dist2 < dh))
                {
                    hMax = h;
                    dh = dist2;
                    hot = k;
                }
            }
            if ((pref & EmgInstrFlags.PreferMarked) != 0 && marked >= 0)
            {
                return marked;
            }
            if ((pref & EmgInstrFlags.PreferHeat) != 0 && hot >= 0 && hMax - hMin > 1e-3f)
            {
                return hot;
            }
            return nearest;
        }

        /// <summary>
        /// 连锁挑下一跳（FGR-EMG-017：已标记 &gt; 导电 &gt; 最近；只有“已标记”一种偏好时只跳已标记）。<paramref name="hit"/> 里的不再跳。没有返回 −1。O(单位数)。
        /// </summary>
        internal static int EmgChainPick(ref CombatData d, double2 at, float range, byte want, ref FixedList128Bytes<int> hit, EmgInstrFlags pref)
        {
            double now = d.Scalars[0].Time;
            int nearest = -1, marked = -1, cond = -1;
            double dn = double.MaxValue, dm = double.MaxValue, dc = double.MaxValue;
            for (int k = 0; k < d.Count; k++)
            {
                if (d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || hit.Contains(k))
                {
                    continue;
                }
                double dist = math.distance(d.Pos[k], at);
                if (dist > range + d.Radius[k])
                {
                    continue;
                }
                if (dist < dn)
                {
                    dn = dist;
                    nearest = k;
                }
                if (d.MarkedUntil[k] > now && dist < dm)
                {
                    dm = dist;
                    marked = k;
                }
                if (Conductive(ref d, k, now) && dist < dc)
                {
                    dc = dist;
                    cond = k;
                }
            }
            if ((pref & EmgInstrFlags.PreferMarked) != 0)
            {
                if (marked >= 0)
                {
                    return marked;
                }
                if ((pref & ~EmgInstrFlags.PreferMarked & (EmgInstrFlags.PreferConductive | EmgInstrFlags.PreferHeat)) == 0)
                {
                    return -1; // 只有“已标记”一种偏好：只跳已标记目标（FG19A 第 1.2 节第 20 行）
                }
            }
            if ((pref & EmgInstrFlags.PreferConductive) != 0 && cond >= 0)
            {
                return cond;
            }
            return nearest;
        }

        /// <summary>武器程序根节点的连锁目标偏好（没有程序 / 没有连锁行为 0：沿用最近）。</summary>
        internal static EmgInstrFlags EmgChainPrefOf(ref CombatData d, in CombatWeapon wp) =>
            wp.Program >= 0 && EmgModsOf(ref d, wp.Program, 0, out EmgMods m) ? m.ChainPref : EmgInstrFlags.None;

        // ─────────────────────────────── 飞行载荷：生成时写入修饰 ───────────────────────────────

        /// <summary>
        /// 新生成的飞行载荷（根弹体 / 无人机子弹 / 子弹药）按自己节点的修饰写入弹道状态：穿透 / 反弹次数、寻的目标与修正额度、侧偏（无状态哈希）与提速、
        /// 折返、绕行（寿命按绕行路程延长）、抛射（寿命 = 到瞄准点的飞行时间，落到目标脚下）。<paramref name="aim"/> 是瞄准点，<paramref name="targetId"/> 是开火目标（0 = 没有）。
        /// </summary>
        internal static void EmgInitFlight(ref CombatData d, ref CombatProjectile pr, double2 aim, int targetId, bool root)
        {
            if (pr.Prog < 0 || !EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m))
            {
                return;
            }
            float speed = math.length(pr.Vel);
            float2 dir = speed > 1e-6f ? pr.Vel / speed : new float2(0f, 1f);
            pr.PierceLeft = m.Pierce;
            pr.BounceLeft = m.BounceUnit;
            pr.BounceObs = m.BounceObs;
            if (m.DriftAmount > 0f || m.DriftB > 0f)
            {
                float u = EmgRng.Unit((uint)pr.Id, (uint)pr.Chain, SaltDrift, 0u);
                dir = Rotate(dir, math.radians((2f * u - 1f) * m.DriftAmount));
                speed *= 1f + math.max(0f, m.DriftB);
                pr.Vel = dir * speed;
                pr.Mods |= EmgProjMods.Drift;
            }
            byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            pr.HomingTarget = targetId;
            if (m.SteerMax > 0f)
            {
                pr.SteerLeft = math.min(360f, m.SteerMax);
                pr.Mods |= EmgProjMods.Steer;
                if ((m.SteerPref & (EmgInstrFlags.PreferMarked | EmgInstrFlags.PreferHeat)) != 0 || targetId == 0)
                {
                    int k = EmgPickHoming(ref d, pr.Pos, dir, m.SteerRadius > 0f ? m.SteerRadius : 13f, want, m.SteerPref, 0);
                    if (k >= 0)
                    {
                        pr.HomingTarget = d.Id[k];
                    }
                }
            }
            if (m.Return != 0)
            {
                pr.Mods |= EmgProjMods.Return;
            }
            if (m.Orbit != 0 && speed > 1e-6f)
            {
                float radius = m.OrbitRadius > 0f ? m.OrbitRadius : 2.5f;
                float laps = m.OrbitLaps > 0f ? m.OrbitLaps : 1f;
                pr.Mods |= EmgProjMods.Orbit;
                pr.ModT = 0f;
                pr.Life += 2f * math.PI * radius * laps / speed; // FG19A 第 2.1.1 节：绕轨按额外路径长度 ÷ 弹速延长寿命
            }
            if (m.Lob != 0)
            {
                pr.Flags |= EmgProjFlags.Lob;
                if (root && speed > 1e-6f)
                {
                    pr.Life = (float)math.distance(aim, pr.Pos) / speed; // 落到瞄准点（目标脚下）
                }
            }
        }

        /// <summary>多份载荷（射弹 Multi）：本次出手分成几份、每份伤害比例与扇角（没有为 1 份、比例 1）。</summary>
        internal static int EmgPellets(ref CombatData d, in CombatWeapon wp, out float ratio, out float fan)
        {
            ratio = 1f;
            fan = 0f;
            if (wp.Program < 0 || !EmgModsOf(ref d, wp.Program, 0, out EmgMods m) || m.Multi <= 1)
            {
                return 1;
            }
            ratio = m.MultiRatio > 0f ? m.MultiRatio : 0.4f;
            fan = m.MultiFan > 0f ? m.MultiFan : 30f;
            return m.Multi;
        }

        // ─────────────────────────────── 飞行载荷：每步 ───────────────────────────────

        /// <summary>
        /// 每步移动前：寻的转向（转向率 × dt，累计不超过修正额度；目标阵亡在剩余额度内按同一偏好重选，没有就直飞）、折返（朝发射者飞，到身边终结）、
        /// 绕行（返回 true 并给出本步终点：绕发射者 <c>半径</c> 米转圈，转完圈数后朝目标飞）。
        /// </summary>
        internal static bool EmgMoveStep(ref CombatData d, ref CombatProjectile pr, float dt, out double2 next)
        {
            next = default;
            float speed = math.length(pr.Vel);
            if (speed < 1e-6f)
            {
                return false;
            }
            if ((pr.Mods & EmgProjMods.Orbit) != 0)
            {
                return EmgOrbitStep(ref d, ref pr, dt, speed, out next);
            }
            if ((pr.Mods & EmgProjMods.Returning) != 0)
            {
                int o = d.SlotOf(pr.Owner);
                if (o < 0 || !d.IsAlive(o))
                {
                    return false; // 发射者已不在：照当前方向飞到寿命结束
                }
                double2 to = d.Pos[o] - pr.Pos;
                double dist = math.length(to);
                if (dist <= ReturnHomeDist(ref d, o, pr))
                {
                    pr.Life = math.min(pr.Life, 0f); // 回到发射者身边：本步终结（evt.expire Code = 折返）
                    pr.Vel = float2.zero;
                    return false;
                }
                pr.Vel = (float2)(to / dist) * speed;
                return false;
            }
            if ((pr.Mods & EmgProjMods.Steer) == 0 || !(pr.SteerLeft > 0f))
            {
                return false;
            }
            EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m);
            int t = d.SlotOf(pr.HomingTarget);
            if (t < 0 || !d.IsAlive(t) || !d.Has(t, CombatUnitFlags.Targetable))
            {
                // FG19A 第 1.1 节第 1 行：目标中途阵亡时按同一偏好在剩余修正额度内重新选目标，没有目标就直飞。
                byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
                t = EmgPickHoming(ref d, pr.Pos, pr.Vel / speed, m.SteerRadius > 0f ? m.SteerRadius : 13f, want, m.SteerPref, pr.HomingTarget);
                if (t < 0)
                {
                    pr.HomingTarget = 0;
                    pr.Mods &= ~EmgProjMods.Steer;
                    return false;
                }
                pr.HomingTarget = d.Id[t];
            }
            double2 want2 = d.Pos[t] - pr.Pos;
            if (math.lengthsq(want2) < 1e-12)
            {
                return false;
            }
            float2 v = pr.Vel / speed;
            float2 w = (float2)math.normalize(want2);
            float ang = math.atan2(v.x * w.y - v.y * w.x, math.dot(v, w));
            float rate = math.radians(m.SteerRate > 0f ? m.SteerRate : 180f) * dt;
            float cap = math.min(rate, math.radians(pr.SteerLeft));
            float turn = math.clamp(ang, -cap, cap);
            pr.Vel = Rotate(v, turn) * speed;
            pr.SteerLeft = math.max(0f, pr.SteerLeft - math.degrees(math.abs(turn)));
            return false;
        }

        private static bool EmgOrbitStep(ref CombatData d, ref CombatProjectile pr, float dt, float speed, out double2 next)
        {
            next = default;
            int o = d.SlotOf(pr.Owner);
            EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m);
            float radius = m.OrbitRadius > 0f ? m.OrbitRadius : 2.5f;
            float laps = m.OrbitLaps > 0f ? m.OrbitLaps : 1f;
            bool sub = (pr.Flags & EmgProjFlags.Sub) != 0;
            if (!sub && (o < 0 || !d.IsAlive(o)))
            {
                EmgOrbitEnd(ref pr, in m); // 发射者已不在：不再绕，照当前方向飞
                return false;
            }
            double dTheta = speed / radius * dt;
            int lap = (int)math.floor(pr.ModT / (2f * math.PI));
            pr.ModT += (float)dTheta;
            if ((int)math.floor(pr.ModT / (2f * math.PI)) != lap)
            {
                pr.Intercepts = 0; // 拦截上限按每圈计（FG19A 第 1.4 节第 42 行：每圈 ≤ 2 发）
            }
            if (sub)
            {
                // EMG1-GRM-01 审查修复：子弹药继承的绕行不绕开火的机器（会从命中点一步瞬移回机器身边），而是从当前位置沿切线进入、
                // 绕自己出生点旁半径 r 的圆（每步按 弹速 / r 转向，圆心隐含在 位置 + 法向 × r，不需要额外字段）；位移 = 弹速 × dt，照常查障碍。
                pr.Vel = Rotate(pr.Vel, (float)dTheta);
                next = pr.Pos + (double2)(pr.Vel * dt);
            }
            else
            {
                double2 c = d.Pos[o];
                double2 rel = pr.Pos - c;
                double theta = math.lengthsq(rel) > 1e-12 ? math.atan2(rel.y, rel.x) : math.atan2(pr.Vel.y, pr.Vel.x);
                double th = theta + dTheta;
                next = c + new double2(math.cos(th), math.sin(th)) * radius;
                pr.Vel = new float2(-(float)math.sin(th), (float)math.cos(th)) * speed; // 切向（表现与侧后判定用）
            }
            if (pr.ModT >= 2f * math.PI * laps)
            {
                EmgOrbitEnd(ref pr, in m);
                int t = d.SlotOf(pr.HomingTarget);
                if (t >= 0 && d.IsAlive(t) && math.lengthsq(d.Pos[t] - next) > 1e-12)
                {
                    pr.Vel = (float2)math.normalize(d.Pos[t] - next) * speed; // 转完一圈：飞向目标
                }
            }
            return true;
        }

        /// <summary>绕行结束（转满圈数 / 发射者不在）：清绕行位与拦截计数，共用字段 ModQ 由绕行相位改回寻的额度（没有寻的为 0）。</summary>
        private static void EmgOrbitEnd(ref CombatProjectile pr, in EmgMods m)
        {
            pr.Mods &= ~EmgProjMods.Orbit;
            pr.Intercepts = 0;
            pr.SteerLeft = (pr.Mods & EmgProjMods.Steer) != 0 ? math.min(360f, m.SteerMax) : 0f;
        }

        /// <summary>
        /// 命中一个单位（已结算）之后：绕行中 / 折返中 / 还有穿透 / 还有单位面反弹时弹体继续（返回 true，弹体留在命中点、忽略刚命中的单位），否则 false（照旧终结）。
        /// 顺序：绕行（每个撞上的单位各算一次命中）→ 折返途中（回程命中 1 次）→ 穿透（比弹体轻的不消耗；阶段 1 单位没有质量属性，一律消耗 1 次）→ 反弹（改向 B 米内下一个目标）→ 开始折返。
        /// </summary>
        internal static bool EmgAfterUnitHit(ref CombatData d, ref CombatProjectile pr, int hit, int hitId, double2 hitPos)
        {
            if (pr.Mods == EmgProjMods.None && pr.PierceLeft == 0 && pr.BounceLeft == 0)
            {
                return false;
            }
            float speed = math.length(pr.Vel);
            if ((pr.Mods & EmgProjMods.Orbit) != 0)
            {
                pr.IgnoreUnit = hitId;
                pr.Pos = hitPos; // 停在命中点（本步已转过的相位已记进 ModT，下一步从这里接着绕）
                return true;
            }
            if ((pr.Mods & EmgProjMods.Returning) != 0)
            {
                pr.Mods |= EmgProjMods.ReturnHit;
                pr.IgnoreUnit = hitId;
                pr.Pos = hitPos;
                return true;
            }
            if (pr.PierceLeft > 0)
            {
                // EMG1-ENT-01（DEBT-EMG1GRM01-01；FG19A 第 1.1 节）：比弹体轻的目标（prop.mass）不消耗穿透次数，不轻于弹体的每穿一个消耗 1 次。
                if (EmgUnitMass(ref d, hit) >= EmgProjMass(ref d, pr))
                {
                    pr.PierceLeft--;
                }
                pr.Mods |= EmgProjMods.Pierced;
                pr.IgnoreUnit = hitId;
                pr.Pos = hitPos;
                EmgProjEvent(ref d, ref pr, EmgEventKind.Pierce, hitId, EmgEntKind.Unit, 0f, 0f, 0, hitPos);
                return true;
            }
            if (pr.BounceLeft > 0 && speed > 1e-6f)
            {
                EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m);
                byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
                var hitList = new FixedList128Bytes<int>();
                hitList.Add(hit);
                int k = EmgChainPick(ref d, hitPos, m.BounceRange > 0f ? m.BounceRange : 6f, want, ref hitList, EmgInstrFlags.None);
                if (k >= 0)
                {
                    float2 dir = (float2)math.normalize(d.Pos[k] - hitPos);
                    pr.BounceLeft--;
                    pr.Mods |= EmgProjMods.Bounced;
                    pr.IgnoreUnit = hitId;
                    pr.Pos = hitPos;
                    pr.Vel = dir * speed;
                    pr.HomingTarget = d.Id[k];
                    EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, hitId, EmgEntKind.Unit, dir.x, dir.y, EmgCode.FaceUnit, hitPos);
                    return true;
                }
            }
            if ((pr.Mods & EmgProjMods.Return) != 0 && speed > 1e-6f)
            {
                EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m);
                pr.Mods = (pr.Mods & ~EmgProjMods.Return) | EmgProjMods.Returning;
                pr.Damage *= m.ReturnRatio > 0f ? m.ReturnRatio : 0.6f;
                pr.IgnoreUnit = hitId;
                pr.Pos = hitPos;
                int o = d.SlotOf(pr.Owner);
                if (o >= 0)
                {
                    float back = (float)math.distance(d.Pos[o], hitPos) / speed;
                    pr.Life = math.max(pr.Life, back * 1.5f + 0.5f); // FG19A 第 2.1.1 节：回旋按额外路径长度 ÷ 弹速延长
                    pr.Vel = (float2)math.normalizesafe(d.Pos[o] - hitPos) * speed;
                }
                return true;
            }
            return false;
        }

        private static double ReturnHomeDist(ref CombatData d, int o, in CombatProjectile pr) => d.Radius[o] + pr.Radius + 0.6;

        /// <summary>折返中的弹体在发射者身边终结（寿命到的这一步离发射者在“身边”距离内）。</summary>
        internal static bool EmgReturnedHome(ref CombatData d, in CombatProjectile pr)
        {
            if ((pr.Mods & EmgProjMods.Returning) == 0)
            {
                return false;
            }
            int o = d.SlotOf(pr.Owner);
            return o >= 0 && d.IsAlive(o) && math.distance(d.Pos[o], pr.Prev) <= ReturnHomeDist(ref d, o, pr) + 1e-3;
        }

        /// <summary>撞单位后不再碰单位的载荷（折返途中已命中过 1 次：只飞回发射者）。</summary>
        internal static bool EmgSkipUnits(in CombatProjectile pr) => (pr.Mods & (EmgProjMods.Returning | EmgProjMods.ReturnHit)) == (EmgProjMods.Returning | EmgProjMods.ReturnHit);

        /// <summary>
        /// 撞上一个圆面（障碍；残骸面由 EMG1-ENT-01 以同一函数接上，<paramref name="face"/> = 残骸）且还有障碍面反弹次数：按撞点法线镜面反射，发 evt.bounce，返回 true。
        /// </summary>
        internal static bool EmgBounceFace(ref CombatData d, ref CombatProjectile pr, double2 at, double2 center, ushort face)
        {
            if (pr.BounceObs == 0 || (pr.Flags & EmgProjFlags.Lob) != 0)
            {
                return false;
            }
            double2 nd = at - center;
            float2 n = math.lengthsq(nd) > 1e-12 ? (float2)math.normalize(nd) : -math.normalizesafe(pr.Vel);
            float2 v = pr.Vel;
            pr.Vel = v - 2f * math.dot(v, n) * n;
            pr.Pos = at + (double2)(n * 0.02f);
            pr.BounceObs--;
            pr.Mods |= EmgProjMods.Bounced;
            pr.IgnoreUnit = 0;
            EmgProjEvent(ref d, ref pr, EmgEventKind.Bounce, 0, face == EmgCode.FaceWreck ? EmgEntKind.Wreck : EmgEntKind.None, n.x, n.y, face, at);
            return true;
        }

        /// <summary>侧偏过的弹体穿过偏折场时按概率不被反射（FG19A 第 1.1 节第 14 行：一半概率；每发一个无状态哈希，同一发结果固定）。
        /// EMG1-FW-01（DEBT-EMG1GRM01-03）：只有节点读 fld.deflect（乱流的条件读端）时才规避，没读 = 只剩侧偏（FG19A 第 1.8 节退化表）。</summary>
        internal static bool EmgEvadesDeflect(ref CombatData d, in CombatProjectile pr)
        {
            if ((pr.Mods & EmgProjMods.Drift) == 0 || !EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m) || !(m.DriftEvade > 0f)
                || !EmgNodeReads(ref d, pr.Prog, pr.Node, EmgConst.PortDeflect))
            {
                return false;
            }
            return EmgRng.Unit((uint)pr.Id, (uint)pr.Chain, SaltEvade, 0u) < m.DriftEvade;
        }

        /// <summary>子弹药节点上的连锁（继承来的 / 自己的 chain 行）：命中后按偏好跳几次，每跳伤害按衰减。主目标已结算。</summary>
        internal static void EmgSubChain(ref CombatData d, in CombatProjectile pr, int owner, int hit, double2 hitPos)
        {
            if (pr.Prog < 0 || !EmgModsOf(ref d, pr.Prog, pr.Node, out EmgMods m) || m.Chain == 0 || !(m.ChainRange > 0f))
            {
                return;
            }
            byte want = pr.Faction == CombatFaction.Player ? (byte)CombatFaction.Hostile : (byte)CombatFaction.Player;
            var hitList = new FixedList128Bytes<int>();
            hitList.Add(hit);
            double2 at = hitPos;
            float dmg = pr.Damage;
            ushort hc = d.EmgCtx.HitCode;
            d.EmgCtx.HitCode = EmgCode.HitChain;
            for (int q = 0; q < m.Chain; q++)
            {
                int k = EmgChainPick(ref d, at, m.ChainRange, want, ref hitList, m.ChainPref);
                if (k < 0)
                {
                    break;
                }
                hitList.Add(k);
                dmg *= m.ChainFalloff > 0f ? m.ChainFalloff : 1f;
                DamageUnit(ref d, k, dmg, owner);
                EmgMarkJumpReact(ref d, owner, k, m.ChainPref); // EMG1-CHAIN-01：按“已标记”偏好跳到已标记目标 → reaction_marktag 计数 + evt.react
                at = d.Pos[k];
            }
            d.EmgCtx.HitCode = hc;
        }

        /// <summary>
        /// 绕行中的弹体拦下撞上的敌方弹体（FG19A 第 1.4 节第 42 行：每圈 ≤ 2 发）。一步末尾统一做（不在逐发循环里删别的弹体），O(绕行弹体 × 弹体数)，
        /// 没有绕行弹体时 O(1) 跳过（调用方判）。
        /// </summary>
        internal static void EmgOrbitIntercept(ref CombatData d)
        {
            int n = d.Projectiles.Length;
            for (int i = 0; i < n; i++)
            {
                CombatProjectile o = d.Projectiles[i];
                if ((o.Mods & EmgProjMods.Orbit) == 0 || o.Intercepts >= MaxInterceptsPerLap || !(o.Life > 0f))
                {
                    continue;
                }
                if (!EmgNodeReads(ref d, o.Prog, o.Node, EmgConst.PortProjectile))
                {
                    continue; // EMG1-FW-01（DEBT-EMG1GRM01-03）：拦弹靠绕轨的 ent.projectile 感知读端；没读 = 只绕行（FG19A 第 1.8 节退化表）
                }
                for (int j = 0; j < n && o.Intercepts < MaxInterceptsPerLap; j++)
                {
                    CombatProjectile e = d.Projectiles[j];
                    if (j == i || e.Faction == o.Faction || !(e.Life > 0f))
                    {
                        continue;
                    }
                    double r = o.Radius + e.Radius + 0.3;
                    if (math.lengthsq(e.Pos - o.Pos) > r * r)
                    {
                        continue;
                    }
                    e.Life = -1f; // 被拦下：下面压实时去掉
                    d.Projectiles[j] = e;
                    o.Intercepts++;
                    EmgProjEvent(ref d, ref o, EmgEventKind.Absorb, e.Id, EmgEntKind.Projectile, e.Damage, 0f, 0, e.Pos);
                }
                d.Projectiles[i] = o;
            }
            int write = 0;
            for (int i = 0; i < n; i++)
            {
                CombatProjectile p = d.Projectiles[i];
                if (p.Life < 0f)
                {
                    CombatCounters c = d.Counters[0];
                    c.ProjectilesExpired++;
                    d.Counters[0] = c;
                    continue;
                }
                d.Projectiles[write++] = p;
            }
            d.Projectiles.ResizeUninitialized(write);
        }

        // ─────────────────────────────── 即时出手（格斗 / 力场 / 布区）───────────────────────────────

        /// <summary>出手的无状态随机（出手者 Id × 步数 × 盐 × 序号 → [0, 1)）：同一步同一出手者结果固定，存读档后照样复现。</summary>
        private static float ShotUnit(ref CombatData d, int a, uint salt, uint q) =>
            EmgRng.Unit((uint)(a >= 0 ? d.Id[a] : 0), (uint)d.Scalars[0].Steps, salt, q);

        /// <summary>格斗侧偏：挥击方向随机偏 ±(a × 幅度)°（返回弧度），扇形加宽 b 度（<paramref name="coneAdd"/>）。没有侧偏为 0。</summary>
        internal static float EmgSwingDrift(ref CombatData d, int a, in EmgMods m, out float coneAdd)
        {
            coneAdd = 0f;
            if (!(m.DriftAmount > 0f))
            {
                return 0f;
            }
            coneAdd = math.max(0f, m.DriftB);
            return math.radians((2f * ShotUnit(ref d, a, SaltSwing, 0u) - 1f) * m.DriftAmount);
        }

        /// <summary>力场中心游移 / 布区落点散开：± (a × 幅度) 米（两轴各一个哈希）。</summary>
        internal static double2 EmgCenterDrift(ref CombatData d, int a, in EmgMods m)
        {
            if (!(m.DriftAmount > 0f))
            {
                return double2.zero;
            }
            float ux = 2f * ShotUnit(ref d, a, SaltPulse, 1u) - 1f, uy = 2f * ShotUnit(ref d, a, SaltPulse, 2u) - 1f;
            return new double2(ux, uy) * m.DriftAmount;
        }

        /// <summary>
        /// 阶段 1 掩体规则（<see cref="CombatConfig.EmgCover"/>）：力场脉冲从 <paramref name="from"/> 到单位之间被障碍挡（曲射越过）或被残骸挡（贯穿越过）。规则关时恒 false（旧行为）。
        /// </summary>
        internal static bool EmgPulseBlocked(ref CombatData d, double2 from, double2 to, in EmgMods m)
        {
            if (d.Config.EmgCover == 0)
            {
                return false;
            }
            if (m.Lob == 0 && d.Obstacles.Length > 0 && !LineOfSight(ref d, from, to))
            {
                return true;
            }
            return m.Pierce == 0 && d.Wrecks.Length > 0 && EmgHashSegment(ref d, from, to, EmgHash.KindWreck, true, out _) >= 0;
        }

        /// <summary>
        /// 阶段 1 掩体规则：布区从出手者投向 <paramref name="to"/>，途中先撞障碍（曲射越过）或残骸（贯穿越过）就落在撞点前。规则关 / 没挡时原样返回。
        /// </summary>
        internal static double2 EmgDropPoint(ref CombatData d, double2 from, double2 to, in EmgMods m)
        {
            if (d.Config.EmgCover == 0)
            {
                return to;
            }
            double best = 2.0;
            if (m.Lob == 0 && d.Obstacles.Length > 0 && EmgHashSegment(ref d, from, to, EmgHash.KindObstacle, true, out double to1) >= 0)
            {
                best = math.min(best, to1);
            }
            if (m.Pierce == 0 && d.Wrecks.Length > 0 && EmgHashSegment(ref d, from, to, EmgHash.KindWreck, true, out double tw) >= 0)
            {
                best = math.min(best, tw);
            }
            if (best > 1.0)
            {
                return to;
            }
            double len = math.distance(from, to);
            double back = len > 1e-6 ? math.min(best, math.max(0.0, best - 0.3 / len)) : 0.0;
            return from + (to - from) * back;
        }

        /// <summary>
        /// 力场多个小脉冲（FG19A：一次脉冲分成 N 个小脉冲落在圈内 N 个方位，各 a 倍）：第 q 个中心 = 出手中心 + 半径 × 0.5 × 方向（从朝主目标起均分一圈），
        /// 小脉冲半径 = 力场半径 × b。主目标已结算；每个单位每个小脉冲各吃一次。
        /// </summary>
        internal static void EmgAuraPulses(ref CombatData d, int a, int t, double2 origin, float baseDamage, in CombatReading r, in EmgMods m)
        {
            byte want = WantOf(ref d, a);
            double2 toT = d.Pos[t] - origin;
            float start = math.lengthsq(toT) > 1e-12 ? (float)math.atan2(toT.y, toT.x) : 0f;
            float sub = r.Area * (m.MultiRadius > 0f ? m.MultiRadius : 0.5f);
            float ratio = m.MultiRatio > 0f ? m.MultiRatio : 0.4f;
            for (int q = 0; q < m.Multi; q++)
            {
                float ang = start + 2f * math.PI * q / m.Multi;
                double2 c = origin + new double2(math.cos(ang), math.sin(ang)) * (r.Area * 0.5f);
                for (int k = 0; k < d.Count; k++)
                {
                    if (k == t || k == a || d.Faction[k] != want || !d.IsAlive(k) || !d.Has(k, CombatUnitFlags.Targetable) || IsAir(ref d, k))
                    {
                        continue;
                    }
                    if (math.distance(d.Pos[k], c) > sub + d.Radius[k] || EmgPulseBlocked(ref d, origin, d.Pos[k], m))
                    {
                        continue;
                    }
                    float dealt = StrikeDamage(ref d, a, k, baseDamage * ratio, r, origin, false, 0f);
                    if (DamageUnit(ref d, k, dealt, a))
                    {
                        PerTarget(ref d, a, k, dealt, r, origin);
                    }
                }
            }
        }
    }
}
