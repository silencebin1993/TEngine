using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-FUZZ-01 模糊测试器常驻（FG19 FGR-EMG-220～223 阶段 4；FG19A 第 7.2 节；FGT-EMG-070 常驻口径）：
    /// ① 按行为判死指令（DEBT-EMG1FUZZ01-02）：内核激活过的每条指令去掉后同场景重跑，行为指纹（单位位置 / 耐久 / 积热 / 状态、弹体、斑块 / 场 / 区域 / 残骸 / 无人机数、
    ///   出手与事件计数）逐项相同 = 死指令（条件永不成立时被它门控的指令、效果被载体忽略的指令都会被判出）；
    /// ② 规则表每行一例定向场景（DEBT-EMG2REACT02-02，FGR-EMG-015 规则四），进固定用例集走主跑 / 确定性 / 存读档三遍，内核按行计数；
    /// ③ 2～3 台己方机器协同（含中继）；④ 与上一次结果比退化（新的死指令、上一次触发过的规则行这次没触发）；⑤ 固定集 ≤ 10 分钟；
    /// ⑥ 新颖度空间：K 曲线（最后 3 批仍上升）与 13 个通道族的维度贡献表（每族 K 增量 &gt; 0；节拍 / 底盘 / 地形单列）；⑦ 涌现满载“加满”场景（斑块 1024、无人机 512、事件持续 512 / 步）。
    /// </summary>
    public static partial class FgEmergenceFuzzSelfCheck
    {
        /// <summary>行为指纹每隔这么多步混一次（加终局一次）。</summary>
        private const int PrintEvery = 30;

        private const int SquadCases = 24;
        private const float RuleSeconds = 12f;
        private const int FamilyBatch = 20;
        private const int CurveBatch = 50;

        /// <summary>规则行定向场景铺在这里：离本机交战区（原点附近 ±9 米）足够远，不被本机的弹幕打散，也不碰本机。</summary>
        private static readonly double2 RuleAt = new double2(-24.0, 18.0);

        /// <summary>
        /// 规则表里此刻没有条件来源、固定集打不到的行（不算通过、不静默：逐条报“已登记缺口”并写承接）。按推荐默认执行（2026-11-10）：冻结桥依赖的水面没有落地。
        /// </summary>
        internal static readonly Dictionary<string, string> KnownRuleGaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reaction_icebridge"] = "V08 冻结桥要水面，水面没有落地（挂 FG18-SEA-01，DEBT-EMG4TER01-01；阶段 4 只做熔坑）",
        };

        /// <summary>FG19 第 2.3 节 13 个通道族（fg.TbChannelPort.family）。节拍、底盘、地形是 FG19A 7.2 第 6 条③点名的薄弱族。</summary>
        internal static readonly string[] Families = { "grammar", "meta", "event", "matter", "energy", "entity", "field", "squad", "phase", "chassis", "terrain", "hijack", "tempo" };
        private static readonly string[] WeakFamilies = { "tempo", "chassis", "terrain" };

        // ═════════════════════════════════ ① 按行为判死 ═════════════════════════════════

        /// <summary>行为指纹：只取看得见的行为（不含程序表 / 指令表，否则去掉一条指令必然改哈希）。</summary>
        private static ulong MixPrint(ulong h, Arena a)
        {
            CombatKernel k = a.K;
            void M(long v) => h = (h ^ (ulong)v) * 0x100000001B3UL;
            void F(double v) => M(BitConverter.DoubleToInt64Bits(Math.Round(v, 4)));
            for (int s = 0; s < k.SlotCount; s++)
            {
                CombatUnitView v = k.ViewAt(s);
                M(v.Id);
                F(v.Position.x);
                F(v.Position.y);
                F(v.Health);
                F(v.Heat);
                F(Math.Min(v.MarkedUntil, 1e7)); // 被标记（场上可见的标记圈；evt.marked 写端的结果，#197）
                if (k.TryGetStatus(v.Id, out uint mask, out _, out _, out _, out _))
                {
                    M(mask);
                }
                // 单位的涌现状态（相态累积量：入侵度 / 动能 / 腐蚀……、温度、相态位、质量、属性档）：写端被内核执行过就会改它，哪怕本例时长里还没跨过阈值；
                // 写端被内核忽略时这些逐项不变（不含链号 / 最后击中者这类发号字段，去掉一条会改编号却不是行为）
                if (k.TryGetUnitEmg(v.Id, out short prof, out EmgUnitState es))
                {
                    M(prof);
                    F(es.Temp);
                    F(es.AccA.x);
                    F(es.AccA.y);
                    F(es.AccA.z);
                    F(es.AccA.w);
                    F(es.AccB.x);
                    F(es.AccB.y);
                    F(es.AccB.z);
                    M(es.PhaseMask);
                    F(es.Mass);
                    F(es.StickT);
                }
                M(k.HijackFlashed(v.Id) ? 1 : 0); // 被劫持敌机改刷了劫持者程序（FG19A N40，悬停可见）
            }
            M(k.ProjectileCount);
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p))
                {
                    F(p.Pos.x);
                    F(p.Pos.y);
                    F(p.Damage);
                }
            }
            M(k.DroneCount);
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone dr))
                {
                    F(dr.Pos.x);
                    F(dr.Pos.y);
                }
            }
            M(k.ZoneCount);
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z))
                {
                    F(z.Pos.x);
                    F(z.Pos.y);
                    F(z.Radius);
                }
            }
            M(k.PatchCount);
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter != 0)
                {
                    M(z.Matter);
                    F(z.Pos.x);
                    F(z.Pos.y);
                    F(z.Amount);
                    F(z.Temp);
                }
            }
            M(k.FieldCount);
            M(k.WireCount); // 桥接导线（场上可见）
            M(k.WreckCount);
            M(k.HijackCount);
            CombatCounters cc = k.Counters;
            M(cc.ShotsFired);
            M(cc.ProjectilesSpawned);
            M(cc.ProjectilesHit);
            M(cc.ProjectilesExpired);
            M(cc.ShotsReflected);
            M(cc.ZonesSpawned);
            M(cc.DronesLaunched);
            M(cc.DamageToHostile);
            M(cc.DamageToPlayer);
            EmgBusStats st = k.EmgBusStats;
            M(st.SubSpawned);
            M(st.FragSpawned);
            M(st.Impacts);
            M(st.Pushes);
            M(st.WreckAbsorbs);
            M(st.WreckBounces);
            M(st.RelayForwarded);
            M(st.PingsDelivered);
            M(st.BeatsEmitted);
            // 不混事件入队数：事件只在有读者时入队，去掉某事件唯一的读者入队数必变——那是监听不是行为（审查 P1）；事件带来的玩法结果已在上面的生成 / 命中 / 伤害 / 斑块里
            return h;
        }

        /// <summary>
        /// DEBT-EMG1FUZZ01-02：内核计过激活的每条指令（含条件 / 感知读端、无条件修饰、效果写端）去掉后、按原用例的场景布置重跑（计数关），行为指纹逐项相同 → 死指令。
        /// “条件永不成立”：被它门控的指令有没有都一样 → 那条被判死；条件后面什么都不门控 → 条件本身被判死。
        /// </summary>
        private static void JudgeBehavior(FuzzCase c, Pools pools, int steps, CaseResult r, ulong print, Arena pin)
        {
            for (int i = 0; i < r.Ordinals.Count; i++)
            {
                (string fw, FirmwareCarrier carrier, int ordinal, bool active) = r.Ordinals[i];
                if (!active || Structural(pin.Program, ordinal))
                {
                    continue;
                }
                if (SalvageMerged(pin.Program, ordinal) || MergedPartner(pin.Program, ordinal))
                {
                    r.MergedSteps++; // 内核同一事件只拆一次：根节点同一生效事件的多条回收写端合并（CombatLogic.EmgRootGated salvaged），后一条是幂等合并、不是死指令
                    continue;
                }
                if (!(c.Mates > 0 && c.Relay) && OnlyPings(pin.Program, ordinal))
                {
                    r.CondZero.Add($"{fw}（第 {ordinal + 1} 位，{carrier}；只广播 evt.ping，本例没有同一中继组的僚机，送达不了——同 SquadCondNodes 口径）");
                    continue;
                }
                if (!ProgramReads(pin.Program, EmgEventKind.Ping) && OnlyPings(pin.Program, ordinal))
                {
                    // 僚机与本机同一套程序（SpawnMates 同装配）：程序里没有收听 evt.ping 的指令 = 组里没有接收者，内核只计广播（CombatLogic.EmgPingOut 按接收者的根节点收听门找人）
                    r.CondZero.Add($"{fw}（第 {ordinal + 1} 位，{carrier}；只广播 evt.ping，同组僚机同一套程序、没有收听者，送达不了——同上口径）");
                    continue;
                }
                if (FullCircleDrift(pin, ordinal))
                {
                    r.CondZero.Add($"{fw}（第 {ordinal + 1} 位，{carrier}；写端只有侧偏 grm.mod.drift，本机格斗挥击已是整圈（扇形半角 {pin.W.Reading.Cone:F0}°），转动挥击方向不改变覆盖——已登记缺口 FG-GAP-215，预览暂不提示，承接 EMG4-CLEAN-01）");
                    continue;
                }
                if (ConstFalseGate(pin, ordinal) is string gate)
                {
                    r.CondZero.Add($"{fw}（第 {ordinal + 1} 位，{carrier}；{gate}——已登记缺口 FG-GAP-215，预览暂不提示，承接 EMG4-CLEAN-01）");
                    continue;
                }
                r.BehaviorChecked++;
                ulong p2 = 0;
                using (Arena x = BuildWithout(c, fw, ordinal, pools, pin))
                {
                    if (SameRows(pin.Program, x.Program))
                    {
                        // 去掉这一条后内核指令行逐行不变：同节点同键的另一来源（如混合固件里同一条父固件）顶上了它——编译期幂等合并的另一面，不是死指令
                        r.BehaviorChecked--;
                        r.MergedSteps++;
                        continue;
                    }
                    for (int s = 0; s < steps; s++)
                    {
                        x.StepOnce(s);
                        if (s % PrintEvery == PrintEvery - 1)
                        {
                            p2 = MixPrint(p2, x);
                        }
                    }
                    p2 = MixPrint(p2, x);
                }
                if (p2 != print)
                {
                    continue;
                }
                r.Ordinals[i] = (fw, carrier, ordinal, false);
                string d = $"{fw}（第 {ordinal + 1} 位，{carrier}；按行为：内核执行过，去掉这条后整例行为指纹逐项相同；{RowsOf(pin.Program, ordinal)}）";
                r.BehaviorDead.Add(d);
                r.Dead.Add(d);
            }
        }

        /// <summary>
        /// 这一位置的写端全是根节点回收（动词 salvage），且根节点另有一个位置的回收写端由同一事件生效——内核在 EmgRootGated 里同一事件只拆一次（多条回收写端合并），
        /// 与编译期“同节点同键幂等合并”同一性质：去掉其中一条行为不变，按幂等合并计、不判死。按动词 / 端口判，不按内容 ID。
        /// </summary>
        private static bool SalvageMerged(EmgProgramResult prog, int ordinal)
        {
            if (prog.KernelNodes.Count == 0)
            {
                return false;
            }
            EmgNode root = prog.KernelNodes[0];
            int end = Math.Min(root.FirstInstr + root.InstrCount, prog.KernelInstrs.Count);
            var trig = new Dictionary<int, HashSet<ushort>>();
            var salvage = new HashSet<int>();
            bool other = false;
            for (int i = root.FirstInstr; i < end; i++)
            {
                EmgInstr x = prog.KernelInstrs[i];
                if (x.Role == EmgConst.RoleTrigger)
                {
                    if (!trig.TryGetValue(x.Ordinal, out HashSet<ushort> t))
                    {
                        trig[x.Ordinal] = t = new HashSet<ushort>();
                    }
                    t.Add(x.Port);
                }
                else if (x.Role > EmgConst.RoleLastRead)
                {
                    if (x.Verb == (byte)EmgVerb.Salvage)
                    {
                        salvage.Add(x.Ordinal);
                    }
                    else if (x.Ordinal == ordinal)
                    {
                        other = true;
                    }
                }
            }
            if (other || !salvage.Contains(ordinal) || !trig.TryGetValue(ordinal, out HashSet<ushort> mine))
            {
                return false;
            }
            return salvage.Any(o => o != ordinal && trig.TryGetValue(o, out HashSet<ushort> t) && t.Overlaps(mine));
        }

        /// <summary>
        /// 编译期同节点同键幂等合并的另一面：程序里另有一条指令（如混合固件里同一条父固件）声明了同样的写端、因同键合并没有自己的写端行（写端合并进了这一位置的行，触发读端行还在）——
        /// 去掉这一条，那条就用自己的行顶上，行为不变。按幂等合并计、不判死。按写端口判，不按内容 ID。
        /// </summary>
        private static bool MergedPartner(EmgProgramResult prog, int ordinal)
        {
            var mine = new HashSet<ushort>(prog.KernelInstrs.Where(x => x.Ordinal == ordinal && x.Role > EmgConst.RoleLastRead).Select(x => x.Port));
            if (mine.Count == 0)
            {
                return false;
            }
            var withRows = new HashSet<int>(prog.KernelInstrs.Where(x => x.Role > EmgConst.RoleLastRead).Select(x => (int)x.Ordinal)); // 只看写端行：被合并的那条照样留着自己的触发读端行
            EmgCompiled cat = EmgCatalog.Current;
            return prog.Steps.Any(st => st.Ordinal != ordinal && !st.Overflow && st.Decls.Count > 0 && !withRows.Contains(st.Ordinal)
                                        && mine.All(pt => st.Decls.Any(di => cat.Decls[di].Dir == EmgDirs.Write && cat.Decls[di].Port == pt)));
        }

        /// <summary>两份程序的内核指令行（节点内的端口 / 角色 / 动词 / 参数，不看程序位置）是否逐行相同。</summary>
        private static bool SameRows(EmgProgramResult a, EmgProgramResult b)
        {
            IEnumerable<string> Rows(EmgProgramResult p) => p.KernelNodes.SelectMany((n, ni) => Enumerable.Range(n.FirstInstr, Math.Max(0, Math.Min(n.InstrCount, p.KernelInstrs.Count - n.FirstInstr)))
                .Select(i => p.KernelInstrs[i]).Select(x => $"{ni}|{x.Port}|{x.Role}|{x.Verb}|{x.Amount:R}|{x.A:R}|{x.B:R}|{x.C:R}|{x.Param2:R}|{x.Flags}")).OrderBy(s => s, StringComparer.Ordinal);
            return a.KernelNodes.Count == b.KernelNodes.Count && Rows(a).SequenceEqual(Rows(b));
        }

        /// <summary>
        /// FG-GAP-215（修复轮 1 第 2 棒，#182 / #328）：这一位置排在某条条件门（写 meta.if）之后，而那条门的条件对本例的底盘 / 载体恒不成立——
        /// 条件读 chs.airborne、本机底盘全程不离地（底盘特性决定，同一台机器换不了）；条件读 evt.marked、修复出口（目标是友军，标记只落在敌对单位上）。
        /// 被门控的指令有没有都一样，是条件型零反应，不是内核漏执行；预览没有“本底盘 / 本载体恒不成立”的提示，登记缺口。按端口 / 载体判，不认内容 ID。
        /// </summary>
        /// <summary>FG-GAP-215（#412）：格斗载体的挥击扇形半角已到 180°（整圈，如环刃），这一位置的写端只有侧偏（grm.mod.drift，内核 EmgSwingDrift 只转挥击方向）——整圈转向覆盖不变。按端口 / 读法判。</summary>
        private static bool FullCircleDrift(Arena pin, int ordinal)
        {
            if (pin.W.Reading.Carrier != CombatCarrier.Melee || pin.W.Reading.Cone < 179.9f)
            {
                return false;
            }
            EmgCompiled cat = EmgCatalog.Current;
            var writes = pin.Program.KernelInstrs.Where(x => x.Ordinal == ordinal && x.Role > EmgConst.RoleLastRead).ToList();
            return writes.Count > 0 && writes.All(x => cat.PortIdByCode.TryGetValue(x.Port, out string id) && id == "grm.mod.drift");
        }

        private static string ConstFalseGate(Arena pin, int ordinal)
        {
            EmgCompiled cat = EmgCatalog.Current;
            string Pid(ushort code) => cat.PortIdByCode.TryGetValue(code, out string id) ? id : string.Empty;
            bool airborne = pin.Airborne;
            foreach (int m in pin.Program.KernelInstrs.Where(x => x.Ordinal < ordinal && x.Role > EmgConst.RoleLastRead && Pid(x.Port) == "meta.if").Select(x => (int)x.Ordinal).Distinct())
            {
                foreach (EmgInstr x in pin.Program.KernelInstrs.Where(x => x.Ordinal == m && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead))
                {
                    string id = Pid(x.Port);
                    if (id == "chs.airborne" && !airborne)
                    {
                        return $"门控条件（第 {m + 1} 位）读 chs.airborne，本机底盘全程不离地";
                    }
                    if (id == "evt.marked" && pin.Program.HealOnly)
                    {
                        return $"门控条件（第 {m + 1} 位）读 evt.marked，修复出口的目标是友军、不会被标记";
                    }
                }
            }
            return null;
        }

        /// <summary>这一位置的写端只有“广播 evt.ping”（单机 / 没有中继的用例里没有收听者，广播送达不了，行为天然不变）。按端口判。</summary>
        private static bool OnlyPings(EmgProgramResult prog, int ordinal)
        {
            ushort ping = (ushort)(EmgConst.EventPortBase + (int)EmgEventKind.Ping);
            bool any = false;
            foreach (EmgInstr x in prog.KernelInstrs)
            {
                if (x.Ordinal == ordinal && x.Role > EmgConst.RoleLastRead)
                {
                    if (x.Port != ping)
                    {
                        return false;
                    }
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// 不做“去掉重跑”的位置：元程序（写 meta.*，改的是“下游怎么执行”：本例条件恒成立或下游只有一条时去掉也一样，但那不是死指令；没有下游段时编译器另给提示 emg.compile.meta_empty）
        /// 与只有读端、没有写端的位置。自带条件的效果固件（条件读端 + 效果写端，如“目标已标记才连锁”）照样去掉重跑：条件永不成立 → 效果从不结算 → 按行为判死（G 段负向证明）。按端口 / 角色判，不按内容 ID。
        /// </summary>
        private static bool Structural(EmgProgramResult prog, int ordinal)
        {
            bool write = false;
            foreach (EmgInstr x in prog.KernelInstrs)
            {
                if (x.Ordinal != ordinal)
                {
                    continue;
                }
                if (EmgCatalog.Current.PortIdByCode.TryGetValue(x.Port, out string id) && id.StartsWith("meta.", StringComparison.Ordinal))
                {
                    return true;
                }
                write |= x.Role > EmgConst.RoleLastRead;
            }
            return !write;
        }

        /// <summary>存读档不一致时定位：两边锁步续跑，报第一处状态哈希不同的步与不同的部分（单位字段 / 弹体 / 斑块 / 区域 / 场 / 无人机 / 残骸 / 涌现段）。</summary>
        private static string SaveLoadDiverge(FuzzCase c, Pools pools, int steps)
        {
            int half = steps / 2;
            using Arena a = Build(c, false, pools);
            for (int i = 0; i < half; i++)
            {
                a.StepOnce(i);
            }
            CombatSiteRecord rec = a.Site.Snapshot();
            var b = new Arena { Site = new CombatSite("fuzz_" + c.Site, ConfigFor(c)), Me = a.Me, Target = a.Target, Prism = a.Prism, HijackBait = a.HijackBait, KillStep = a.KillStep, CeaseStep = a.CeaseStep, T = a.T, BaitStep = a.BaitStep,
                Bait = a.Bait, BaitAt = a.BaitAt, MainAt = a.MainAt, Engage = a.Engage, W = a.W, Rule = a.Rule, Mates = a.Mates, Preheat = a.Preheat, PreheatFrom = a.PreheatFrom, PreheatTo = a.PreheatTo, SeizeStep = a.SeizeStep, SeizeUnit = a.SeizeUnit, BaitHold = a.BaitHold, BaitHp = a.BaitHp, CoolRetreat = a.CoolRetreat };
            try
            {
                if (a.Rule?.Melt != null)
                {
                    b.K.SetMeltGround(a.Rule.Melt);
                }
                b.K.Load(Convert.FromBase64String(rec.Payload));
                if (a.Obstacles.Count > 0)
                {
                    b.K.SetObstacles(a.Obstacles);
                }
                string atLoad = FieldDiff(a.K, b.K);
                for (int i = half; i < steps; i++)
                {
                    if (a.K.StateHash() != b.K.StateHash())
                    {
                        var diff = new List<string> { "读档当时逐字段：" + (atLoad ?? "相同"), "此刻逐字段：" + (FieldDiff(a.K, b.K) ?? "相同") };
                        for (int s = 0; s < Math.Max(a.K.SlotCount, b.K.SlotCount) && diff.Count < 4; s++)
                        {
                            CombatUnitView u = a.K.ViewAt(s), v = b.K.ViewAt(s);
                            if (u.Id != v.Id || math.distance(u.Position, v.Position) > 1e-9 || u.Health != v.Health || u.Heat != v.Heat)
                            {
                                diff.Add($"单位槽 {s}（{u.Id}/{v.Id}）位置 {u.Position.x:F3},{u.Position.y:F3} / {v.Position.x:F3},{v.Position.y:F3} 耐久 {u.Health:F2}/{v.Health:F2} 积热 {u.Heat:F2}/{v.Heat:F2}");
                            }
                        }
                        void Cmp(string what, long x, long y)
                        {
                            if (x != y)
                            {
                                diff.Add($"{what} {x}/{y}");
                            }
                        }
                        Cmp("弹体", a.K.ProjectileCount, b.K.ProjectileCount);
                        Cmp("斑块", a.K.PatchCount, b.K.PatchCount);
                        Cmp("区域", a.K.ZoneCount, b.K.ZoneCount);
                        Cmp("场", a.K.FieldCount, b.K.FieldCount);
                        Cmp("无人机", a.K.DroneCount, b.K.DroneCount);
                        Cmp("残骸", a.K.WreckCount, b.K.WreckCount);
                        Cmp("涌现段哈希", (long)a.K.EmgStateHash(), (long)b.K.EmgStateHash());
                        return $"读档后第 {i - half} 步（全局第 {i} 步）开始不同：{string.Join("；", diff)}";
                    }
                    a.StepOnce(i);
                    b.StepOnce(i);
                }
                return "锁步续跑没有找到不同（终局差异来自最后一步）";
            }
            finally
            {
                b.Dispose();
            }
        }

        /// <summary>逐字段（反射，含不进状态哈希的字段）比两个内核的斑块 / 区域 / 弹体 / 无人机，返回前 4 处不同（null = 都相同）。只给存读档定位用。</summary>
        private static string FieldDiff(CombatKernel x, CombatKernel y)
        {
            var bad = new List<string>();
            void Cmp<T>(string what, int n, Func<CombatKernel, int, (bool, T)> get)
            {
                for (int i = 0; i < n && bad.Count < 4; i++)
                {
                    (bool ok1, T u) = get(x, i);
                    (bool ok2, T v) = get(y, i);
                    if (ok1 != ok2)
                    {
                        bad.Add($"{what}[{i}] 有无 {ok1}/{ok2}");
                        continue;
                    }
                    foreach (System.Reflection.FieldInfo f in typeof(T).GetFields())
                    {
                        object p = f.GetValue(u), q = f.GetValue(v);
                        if (!Equals(p, q))
                        {
                            bad.Add($"{what}[{i}].{f.Name} {p}/{q}");
                            break;
                        }
                    }
                }
            }
            Cmp("斑块", Math.Max(x.PatchSlots, y.PatchSlots), (k, i) => (k.TryGetPatch(i, out CombatZone z), z));
            Cmp("区域", Math.Max(x.ZoneCount, y.ZoneCount), (k, i) => (k.TryGetZone(i, out CombatZone z), z));
            Cmp("弹体", Math.Max(x.ProjectileCount, y.ProjectileCount), (k, i) => (k.TryGetProjectile(i, out CombatProjectile p), p));
            Cmp("无人机", Math.Max(x.DroneCount, y.DroneCount), (k, i) => (k.TryGetDrone(i, out CombatDrone dr), dr));
            return bad.Count == 0 ? null : string.Join("；", bad);
        }

        /// <summary>定位入口：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmergenceFuzzSelfCheck.DiagSaveLoadFromCommandLine；BINGAMES_FUZZ_ONLY = 逗号分隔的固定集序号。</summary>
        public static void DiagSaveLoadFromCommandLine()
        {
            Env env = Env.Enter();
            try
            {
                UseStage(EmgCatalog.ImplementedStage);
                Pools pools = Pools.Build();
                WarmUp(pools);
                List<FuzzCase> cases = FixedCases(pools, FixedSeed, FixedCount);
                foreach (string part in (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_ONLY") ?? string.Empty).Split(','))
                {
                    if (int.TryParse(part, out int idx) && idx >= 0 && idx < cases.Count)
                    {
                        FuzzCase c = cases[idx];
                        Debug.Log($"[存读档定位] {c.Text}：{SaveLoadDiverge(c, pools, Mathf.RoundToInt(c.Seconds / Dt))}");
                    }
                }
            }
            finally
            {
                env.Exit();
            }
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(0);
            }
        }

        /// <summary>
        /// 按行为判死的定位入口（DEBT-EMG4FUZZ01-02 逐条查根因用）：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmergenceFuzzSelfCheck.DiagBehaviorFromCommandLine；
        /// BINGAMES_FUZZ_ONLY = 分号分隔的“固定集序号:固件 ID”，各打印原用例与去掉那条后的终局摘要（斑块按物质、场、区域、残骸、劫持、计数、事件入队）。
        /// </summary>
        public static void DiagBehaviorFromCommandLine()
        {
            Env env = Env.Enter();
            try
            {
                UseStage(EmgCatalog.ImplementedStage);
                Pools pools = Pools.Build();
                WarmUp(pools);
                List<FuzzCase> cases = FixedCases(pools, FixedSeed, FixedCount);
                foreach (string part in (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_ONLY") ?? string.Empty).Split(';'))
                {
                    string[] kv = part.Split(':');
                    if (kv.Length != 2 || !int.TryParse(kv[0], out int idx) || idx < 0 || idx >= cases.Count)
                    {
                        continue;
                    }
                    FuzzCase c = cases[idx];
                    int steps = Mathf.RoundToInt(c.Seconds / Dt);
                    if (kv[1].StartsWith("only=", StringComparison.Ordinal))
                    {
                        // 同一场景只装一条固件（本机槽）：看这条单独装时内核有没有执行
                        FuzzCase solo = Without(c, null);
                        solo.Uplinked = false;
                        solo.Uplink = Array.Empty<string>();
                        solo.Local = new[] { kv[1].Substring(5) };
                        using Arena so = Build(solo, false, pools);
                        for (int s = 0; s < steps; s++)
                        {
                            so.StepOnce(s);
                        }
                        Debug.Log($"[行为定位] {c.Text} 只装 {kv[1].Substring(5)}：{Summary(so)}；编译提示 {string.Join(",", so.Program.Notes.Select(n => n.Key + "@" + n.Ordinal))}");
                        continue;
                    }
                    using Arena pin = Build(c, true, pools);
                    var trace = new StringBuilder();
                    long lastSub = 0;
                    float lastHp = -1f, lastAcc = 0f;
                    for (int s = 0; s < steps; s++)
                    {
                        pin.StepOnce(s);
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "5" && pin.K.GameplayPending > 0)
                        {
                            CombatEvent[] evs = pin.K.DrainGameplay(256, out int en);
                            for (int q = 0; q < en; q++)
                            {
                                if (evs[q].Kind.ToString().Contains("Hijack"))
                                {
                                    trace.Append($" <s{s} {evs[q].Kind} u{evs[q].Unit} o{evs[q].Other} c{evs[q].Code} v{evs[q].Value:F1}>");
                                }
                            }
                        }
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "5" && pin.HijackBait >= 0 && pin.K.TryGetUnit(pin.HijackBait, out CombatUnitView b5) && pin.K.TryGetUnitEmg(pin.HijackBait, out _, out EmgUnitState e5)
                            && (b5.Health != lastHp || e5.AccB.z > lastAcc + 0.01f || e5.AccB.z < lastAcc - 0.05f))
                        {
                            // 碎靶逐击：掉血与入侵度（Hack 槽 = AccB.z）的先后
                            trace.Append($" [s{s} 血{lastHp:F1}→{b5.Health:F1} 入侵{lastAcc:F3}→{e5.AccB.z:F3} f{(int)b5.Faction} 活{((b5.Flags & CombatUnitFlags.Alive) != 0 ? 1 : 0)} 劫{pin.K.HijackCount}]");
                            lastHp = b5.Health;
                            lastAcc = e5.AccB.z;
                        }
                        else if (pin.HijackBait >= 0 && pin.K.TryGetUnitEmg(pin.HijackBait, out _, out EmgUnitState e6))
                        {
                            lastAcc = e6.AccB.z;
                        }
                        if (s % 6 == 5 && s >= 240 && s < 420 && pin.K.TryGetUnit(pin.Me, out CombatUnitView tv))
                        {
                            trace.Append($" {pin.T:F1}:{tv.Heat:F1}/{((tv.Flags & CombatUnitFlags.Overheated) != 0 ? "热" : "-")}/{tv.NextFireAt:F1}/{tv.Position.x:F1},{tv.Position.y:F1}/反{pin.K.Counters.ShotsReflected}/弹{pin.K.ProjectileCount}");
                        }
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "2" && s % 30 == 29 && pin.K.TryGetUnit(pin.Me, out CombatUnitView hv) && pin.K.TryGetUnitEmg(pin.Me, out _, out EmgUnitState hs))
                        {
                            trace.Append($" {pin.T:F1}:{hv.Heat:F1}/{((hv.Flags & CombatUnitFlags.Overheated) != 0 ? "热" : "-")}/{hv.Position.x:F1},{hv.Position.y:F1}/m{hs.PrevMatterMask:X}/ph{hs.PhaseMask:X}/T{hs.Temp:F0}");
                        }
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "4" && s % 15 == 14)
                        {
                            // 劫持轨迹：碎靶耐久 / 入侵度、劫持数、出手数
                            bool ba = pin.HijackBait >= 0 && pin.K.TryGetUnit(pin.HijackBait, out CombatUnitView bv);
                            pin.K.TryGetUnitEmg(pin.HijackBait, out _, out EmgUnitState bs);
                            pin.K.TryGetUnit(pin.HijackBait, out CombatUnitView bv2);
                            trace.Append($" [{pin.T:F1} 靶{(ba ? bv2.Health.ToString("F1") + "/f" + (int)bv2.Faction : "无")} acc{bs.AccA.x:F2},{bs.AccA.y:F2},{bs.AccA.z:F2},{bs.AccA.w:F2}|{bs.AccB.x:F2},{bs.AccB.y:F2},{bs.AccB.z:F2} 劫{pin.K.HijackCount} 闪{(pin.HijackBait >= 0 && pin.K.HijackFlashed(pin.HijackBait) ? 1 : 0)} 出{pin.K.Counters.ShotsFired} 杀{pin.K.Counters.KillsHostile}]");
                        }
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "3" && pin.K.EmgBusStats.SubSpawned != lastSub)
                        {
                            lastSub = pin.K.EmgBusStats.SubSpawned;
                            pin.K.TryGetUnit(pin.Me, out CombatUnitView mv3);
                            pin.K.TryGetUnit(pin.Target, out CombatUnitView tv3);
                            trace.Append($" [s{s} 子{lastSub} 命中{pin.K.Counters.ProjectilesHit} 我{mv3.Position.x:F2},{mv3.Position.y:F2} 目标{tv3.Position.x:F2},{tv3.Position.y:F2} 弹{pin.K.ProjectileCount}]");
                        }
                        if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_PROJ") == "1")
                        {
                            for (int pi = 0; pi < pin.K.ProjectileCount; pi++)
                            {
                                if (pin.K.TryGetProjectile(pi, out CombatProjectile pp))
                                {
                                    trace.Append($" [s{s} p{pp.Id} {pp.Pos.x:F2},{pp.Pos.y:F2} v{pp.Vel.x:F1},{pp.Vel.y:F1} f{(int)pp.Faction} fl{(int)pp.Flags} m{(int)pp.Mods} L{pp.Life:F2} ig{pp.IgnoreUnit}/t{pin.Target} r{pp.Radius:F2}]");
                                }
                            }
                        }
                    }
                    Debug.Log($"[行为定位] {c.Text} 轨迹 OverheatAt {pin.W.OverheatAt} HeatPerShot {pin.W.HeatPerShot} Dissipation {pin.W.Dissipation} RecoverBelow {pin.W.RecoverBelow} Cooldown {pin.W.Cooldown} Aim {pin.W.AimSeconds} Speed {pin.W.ProjectileSpeed} Range {pin.W.Range} BaitHold {pin.BaitHold}：{trace}");
                    int mb = pin.W.Program >= 0 && pin.K.TryGetProgram(pin.W.Program, out EmgProgram mp) && mp.NodeCount > 0 && pin.K.TryGetEmgNode(mp.FirstNode, out EmgNode mr) ? mr.FirstInstr : -1;
                    Debug.Log($"[行为定位] {c.Text} 原用例：{Summary(pin)}；行 {string.Join(" ", pin.Program.KernelInstrs.Select((x, j) => $"{x.Ordinal}:{x.Port}/{x.Role}/v{x.Verb}/f{(int)x.Flags}/a{(mb >= 0 ? pin.K.InstrActivations(mb + j) : -1)}"))}；编译提示 {string.Join(",", pin.Program.Notes.Select(n => n.Key + "@" + n.Ordinal))}；节点 {string.Join(" ", pin.Program.KernelNodes.Select(n => $"{n.FirstInstr}+{n.InstrCount}"))}；步 {string.Join(" ", pin.Program.Steps.Select(st => $"{st.Ordinal}:{st.OwnerId}:{st.Decls.Count}"))}");
                    using Arena x = Build(Without(c, kv[1]), false, pools, pin);
                    for (int s = 0; s < steps; s++)
                    {
                        x.StepOnce(s);
                    }
                    Debug.Log($"[行为定位] {c.Text} 去掉 {kv[1]}：{Summary(x)}");
                }
            }
            finally
            {
                env.Exit();
            }
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(0);
            }
        }

        private static string Summary(Arena a)
        {
            CombatKernel k = a.K;
            var mats = new SortedDictionary<int, (int n, double amt)>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter != 0)
                {
                    mats.TryGetValue(z.Matter, out var v);
                    mats[z.Matter] = (v.n + 1, v.amt + z.Amount);
                }
            }
            double hp = 0;
            int status = 0;
            for (int s = 0; s < k.SlotCount; s++)
            {
                CombatUnitView v = k.ViewAt(s);
                hp += v.Health;
                if (k.TryGetStatus(v.Id, out uint mask, out _, out _, out _, out _) && mask != 0)
                {
                    status++;
                }
            }
            CombatCounters cc = k.Counters;
            EmgBusStats st = k.EmgBusStats;
            string ev = string.Join(" ", Enumerable.Range(1, EmgConst.EventKinds).Where(e => k.EmgEnqueuedOf((EmgEventKind)e) > 0).Select(e => $"{(EmgEventKind)e}×{k.EmgEnqueuedOf((EmgEventKind)e)}"));
            return $"斑块 {string.Join(" ", mats.Select(m => $"{m.Key}×{m.Value.n}({m.Value.amt:F2})"))}；场 {k.FieldCount}、区域 {k.ZoneCount}、残骸 {k.WreckCount}、劫持 {k.HijackCount}、无人机 {k.DroneCount}、弹体 {k.ProjectileCount}；" +
                   $"总耐久 {hp:F2}、带状态 {status}；出手 {cc.ShotsFired} 命中 {cc.ProjectilesHit} 反射 {cc.ShotsReflected} 区域生成 {cc.ZonesSpawned} 伤敌 {cc.DamageToHostile:F1}；子载荷 {st.SubSpawned} 冲击 {st.Impacts} 推 {st.Pushes} 吸收 {st.WreckAbsorbs}；事件 {ev}";
        }

        /// <summary>用例的地点配置：灼地规则行（op = scorch）只在长期留存地点结算，那一例按家园口径开长期留存；其余同 <see cref="Config"/>。</summary>
        private static CombatConfig ConfigFor(FuzzCase c)
        {
            CombatConfig cfg = Config();
            if (c.RuleId != null && EmgRuleCatalog.Current.Ids.IndexOf(c.RuleId) is int ri && ri >= 0 && EmgRuleCatalog.Current.Rules[ri].Op == EmgRuleConst.OpScorch)
            {
                cfg.EmgPersist = 1;
            }
            if (c.Carrier == FirmwareCarrier.Summon)
            {
                // 修复轮 1 第 2 棒先造条件（#39）：召唤载体每架召唤物按持有条数占母机刷写带宽（FGR-EMG-036，默认 8 条），4 架即占满，
                // 被劫持的敌机只能带原生程序——劫持中继（改刷劫持者程序）这类按剩余带宽才生效的写端在默认带宽下没有成立的机会。按载体放宽场地带宽
                cfg.EmgFlashBandwidth = 32;
            }
            return cfg;
        }

        /// <summary>
        /// 去掉第 <paramref name="ordinal"/> 位那一条重建（场景按 <paramref name="pin"/>）。同一程序里同一固件有多份时只去这一份：逐个试去掉一份，取剩余固件序列等于“原程序去掉这一位”的那一个。
        /// </summary>
        private static Arena BuildWithout(FuzzCase c, string fw, int ordinal, Pools pools, Arena pin)
        {
            int up = c.Uplink.Length;
            if (c.Uplink.Count(id => id == fw) + c.Local.Count(id => id == fw) > 1)
            {
                string[] want = pin.Program.Steps.Where(s => s.Ordinal != ordinal).OrderBy(s => s.Ordinal).Select(s => s.FirmwareId).ToArray();
                for (int i = 0; i < up + c.Local.Length; i++)
                {
                    if ((i < up ? c.Uplink[i] : c.Local[i - up]) != fw)
                    {
                        continue;
                    }
                    FuzzCase one = Without(c, null);
                    if (i < up)
                    {
                        one.Uplink = c.Uplink.Where((_, j) => j != i).ToArray();
                    }
                    else
                    {
                        one.Local = c.Local.Where((_, j) => j != i - up).ToArray();
                    }
                    Arena x = Build(one, false, pools, pin);
                    if (x.Program.Steps.OrderBy(s => s.Ordinal).Select(s => s.FirmwareId).SequenceEqual(want))
                    {
                        return x;
                    }
                    x.Dispose();
                }
            }
            return Build(Without(c, fw), false, pools, pin); // 只有一份（或对不上位置）：按 ID 去掉
        }

        private static FuzzCase Without(FuzzCase c, string fw) => new FuzzCase
        {
            Index = c.Index, Chassis = c.Chassis, Comp = c.Comp, Func = c.Func, Site = c.Site, Faction = c.Faction, Tag = c.Tag, Carrier = c.Carrier,
            Uplink = c.Uplink.Where(id => id != fw).ToArray(), Local = c.Local.Where(id => id != fw).ToArray(), Uplinked = c.Uplinked, Enemies = c.Enemies, EnemySeed = c.EnemySeed,
            Seconds = c.Seconds, NoWorldSources = c.NoWorldSources, Mates = c.Mates, Relay = c.Relay, RuleId = c.RuleId,
        };

        /// <summary>按行规则命中（内核按行计数）与协同 / 节拍 / 地形 / 劫持等交互键（新颖度与维度贡献表用）。</summary>
        private static void CollectRows(Arena a, CaseResult r, EmgBusStats st)
        {
            EmgRuleCatalog.Compiled cat = EmgRuleCatalog.Current;
            for (int i = 0; i < cat.Rules.Count && i < cat.Ids.Count; i++)
            {
                int n = a.K.EmgRuleRowActivations(cat.Rules[i].IdKey);
                if (n > 0)
                {
                    r.RowHits[cat.Ids[i]] = n;
                    r.Keys.Add("rule:" + cat.Ids[i]);
                }
            }
            if (st.RelayForwarded > 0) r.Keys.Add("relay");
            if (st.PingsDelivered > 0) r.Keys.Add("ping");
            if (st.BeatsEmitted > 0) r.Keys.Add("beat");
            if (st.BeatSyncs > 0) r.Keys.Add("beat.sync");
            if (st.ObstacleHits > 0) r.Keys.Add("obstacle");
            if (st.CoverAbsorbed > 0) r.Keys.Add("cover");
        }

        // ═════════════════════════════════ ③ 协同 ═════════════════════════════════

        /// <summary>僚机：同一套武器 / 程序、同底盘特性、同一条攻击命令；装中继时本机与僚机都按正式入口登记（CombatSite.ApplyRelay，按声明判）。</summary>
        private static void SpawnMates(Arena a, FuzzCase c, int wi, bool fixedChassis, int target, double2 at)
        {
            if (c.Mates <= 0)
            {
                return;
            }
            CombatKernel k = a.K;
            if (c.Relay)
            {
                a.Site.ApplyRelay(a.Me, ComponentCatalog.StructRelayId);
            }
            for (int m = 0; m < c.Mates; m++)
            {
                var p = new double2(-0.3 - 0.4 * m, m % 2 == 0 ? 2.2 : -2.2); // 敌人只在 ±60° 扇面里，僚机站在扇面外侧
                int id = k.Spawn(new CombatSpawn
                {
                    ExtKey = 0, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.ExternalHealth,
                    Position = p, Home = p, Radius = 0.6f, Speed = fixedChassis ? 0f : 4f, Health = 2000f, MaxHealth = 2000f, Weapon = wi, BehaviorProfile = -1,
                });
                a.Site.ApplyChassisTrait(id, c.Chassis);
                if (c.Relay)
                {
                    a.Site.ApplyRelay(id, ComponentCatalog.StructRelayId);
                }
                k.IssueCommand(id, CombatCommandKind.Attack, at, target, 0.5f, a.Engage, 0.5f, false);
                a.Mates.Add(id);
            }
        }

        /// <summary>协同用例：随机装配 + 1～2 台僚机，四例里三例装中继；一半的用例程序里放一条收听 / 广播 evt.ping 的固件（按端口查）。</summary>
        private static void AddSquadCases(List<FuzzCase> list, Pools pools, System.Random rnd)
        {
            string[] pingFw = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && (pools.Reads(id, "evt.ping") || pools.Writes(id, "evt.ping")))
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < SquadCases; i++)
            {
                FuzzCase c = RandomCase(rnd, list.Count, pools, CaseSeconds, list.Count + i * 5);
                c.Mates = 1 + i % 2;
                c.Relay = i % 4 != 3 && CarrierReadings.CanMount(c.Chassis, ComponentCatalog.StructRelayId, out _, out _);
                if (pingFw.Length > 0 && i % 2 == 0)
                {
                    string fw = pingFw[i / 2 % pingFw.Length];
                    var prog = new List<string> { fw };
                    prog.AddRange(c.Program.Where(id => id != fw));
                    c.Uplinked = true;
                    c.Uplink = prog.Take(2).ToArray();
                    c.Local = prog.Skip(2).Take(2).ToArray();
                }
                c.Tag = "协同";
                EnsureSquadSources(c, pools);
                EnsureConditions(c, pools);
                list.Add(c);
            }
        }

        /// <summary>
        /// EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02）协同用例先造条件（按端口挑，不按内容 ID）：收听 evt.ping 的程序要组内有人广播——补一条“被标记时广播”的固件；
        /// 读 evt.marked 的程序（含刚补的广播者）要有标记来源——换上标记桅杆（功能组件，命中即标记）。都排在原程序前面：收听只在第一层生效，排在触发器后面会落进子节点。
        /// 僚机与本机同一套程序，组里每台都既标记、又广播、又收听。补的固件在本载体编译不出声明（如无人机子弹不产生“被标记”）就不补。
        /// </summary>
        private static void EnsureSquadSources(FuzzCase c, Pools pools)
        {
            var prog = c.Program.ToList();
            bool Compiles(string id) => EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { id }, c.Carrier).Steps.Any(st => st.Decls.Count > 0);
            string Pick(Func<string, bool> f) => pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && f(id) && Compiles(id)).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            var head = new List<string>();
            if (prog.Any(id => pools.Reads(id, "evt.ping")) && !prog.Any(id => pools.Writes(id, "evt.ping") && pools.Reads(id, "evt.marked")))
            {
                string caster = Pick(id => pools.Writes(id, "evt.ping") && pools.Reads(id, "evt.marked"));
                if (caster != null)
                {
                    head.Add(caster);
                }
            }
            // 标记来源用标记桅杆（func_marker：组件写 evt.marked，每次命中都标记）——第一层固件的 evt.marked 写端内核没有执行点（只在子节点落点结算，见 FG-GAP-214），补固件标记不了
            bool marker = (head.Count > 0 || prog.Any(id => pools.Reads(id, "evt.marked"))) && c.Func != ComponentCatalog.FuncMarkerId
                          && CarrierReadings.CanMount(c.Chassis, ComponentCatalog.FuncMarkerId, out _, out _);
            if (marker)
            {
                c.Func = ComponentCatalog.FuncMarkerId;
            }
            // 收听者排最后：触发器后面紧跟的那条落进它的子节点，排在收听者后面的广播者 / 被标记触发者会进子节点、只在收到脉冲后才执行（#59 实测）
            List<string> listeners = prog.Where(id => pools.Reads(id, "evt.ping")).ToList();
            if (head.Count == 0 && listeners.Count == 0)
            {
                c.Tag += marker ? " 补标记桅杆" : string.Empty;
                return;
            }
            prog.RemoveAll(id => head.Contains(id) || listeners.Contains(id));
            head.AddRange(prog);
            head.AddRange(listeners);
            c.Uplinked = true;
            c.Uplink = head.Take(2).ToArray();
            c.Local = head.Skip(2).Take(2).ToArray();
            c.Tag += marker ? " 补广播 + 标记桅杆" : " 补广播";
        }

        // ═════════════════════════════════ ② 规则行定向 ═════════════════════════════════

        internal sealed class RuleScene
        {
            public string Id;
            public GameConfig.fg.ChannelRule Row;
            public EmgRule Rule;
            public int Unit = -1;
            public int Shooter = -1;
            public int Foe = -1;
            /// <summary>熔坑行的可熔地面圆（x, y, 半径）；读档前要重新下发（派生量，不进快照）。</summary>
            public List<float3> Melt;
            public bool FieldDone;
        }

        private static IEnumerable<GameConfig.fg.ChannelRule> LiveRuleRows() =>
            ConfigSystem.Instance.Tables.TbChannelRule.DataList.Where(r => r != null && r.Stage <= EmgCatalog.EffectiveStage);

        /// <summary>规则表每行一例（登记缺口的行除外）：本机标准装配照常打主目标；两侧条件铺在 <see cref="RuleAt"/>。P03（同一次命中注热 × 单次动能）由本机自己打出：重炮 + 写 en.heat 的固件（按端口查）。</summary>
        private static void AddRuleCases(List<FuzzCase> list, Pools pools, System.Random rnd)
        {
            string heatFw = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id) && pools.Writes(id, "en.heat")).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            foreach (GameConfig.fg.ChannelRule row in LiveRuleRows())
            {
                if (KnownRuleGaps.ContainsKey(row.Id))
                {
                    continue;
                }
                bool selfHit = row.Cond == "same_hit";
                string comp = selfHit ? ComponentCatalog.CompCannonId : ComponentCatalog.CompGunId;
                var c = new FuzzCase
                {
                    Index = list.Count, Comp = comp, Carrier = CarrierOf(comp), Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                    Faction = pools.Factions[list.Count % pools.Factions.Count], Enemies = 3, EnemySeed = rnd.Next(), Uplinked = true,
                    Uplink = selfHit && heatFw != null ? new[] { heatFw, FgReadingMatrixSelfCheck.AddonFiller } : new[] { FgReadingMatrixSelfCheck.AddonFiller },
                    RuleId = row.Id, Seconds = row.Op == "scorch" ? 28f : row.Op == "meltpit" ? 16f : RuleSeconds, Tag = $"规则行 {row.Id}",
                };
                list.Add(c);
            }
        }

        private static byte MatterOf(ushort code) => code >= EmgRuleConst.CoatBase ? (byte)0 : EmgMatterConst.MatterOfPort(code);

        private static uint CoatMask(ushort code) => code >= EmgRuleConst.CoatBase ? 1u << (code - EmgRuleConst.CoatBase) : 0u;

        private static int SceneUnit(CombatKernel k, double2 at, float hp, int weapon = -1, CombatFaction faction = CombatFaction.Hostile) => k.Spawn(new CombatSpawn
        {
            ExtKey = 0, Kind = faction == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = faction, Behavior = faction == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (weapon >= 0 ? CombatUnitFlags.WeaponEnabled : CombatUnitFlags.None),
            Position = at, Home = at, Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(-1f, 0f),
        });

        /// <summary>按规则行的配对种类在 <see cref="RuleAt"/> 铺两侧条件（真实内核入口：斑块生成、能量注入、沾染、受寒、场、残骸）；后续注入见 <see cref="RuleStep"/>。</summary>
        private static RuleScene SetupRuleScene(Arena a, string id)
        {
            EmgRuleCatalog.Compiled cat = EmgRuleCatalog.Current;
            int ri = cat.Ids.IndexOf(id);
            GameConfig.fg.ChannelRule row = ConfigSystem.Instance.Tables.TbChannelRule.DataList.FirstOrDefault(r => r != null && r.Id == id);
            if (ri < 0 || row == null)
            {
                return null;
            }
            var s = new RuleScene { Id = id, Row = row, Rule = cat.Rules[ri] };
            CombatKernel k = a.K;
            EmgRule r = s.Rule;
            double2 p = RuleAt;
            switch (r.Pair)
            {
                case EmgRuleConst.PairMatterMatter:
                    k.SpawnPatch(MatterOf(r.A), p, 2.5f, 3f, 40f);
                    k.SpawnPatch(MatterOf(r.B), p + new double2(0.5, 0), 2.5f, 3f, 40f);
                    break;
                case EmgRuleConst.PairMatterEnergy:
                    k.SpawnPatch(MatterOf(r.A), p, 2.5f, 3f, 40f);
                    if (r.B == EmgConst.PortKinetic)
                    {
                        // 动能（E18～E20）：重弹寿命到正好落在固态斑块上（与 FgEmgRuleSelfCheck G 段同一造法：伤害 × 载体动能系数累计到耐久）
                        int heavy = a.Site.WeaponIndex(new CombatWeapon
                        {
                            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = 120f, Cooldown = 0f, Program = -1,
                            SpawnFlight = 1, ProjectileSpeed = 30f, ProjectileRadius = 0.15f, ProjectileLife = 10f / 30f,
                        });
                        s.Shooter = SceneUnit(k, p + new double2(0, -10), 5000f, heavy, CombatFaction.Player);
                        s.Foe = SceneUnit(k, p + new double2(0, 30), 5000f);
                    }
                    else if (row.Cond == "temp_le_for")
                    {
                        // 冷源：定温液氮（-196 度）擦边重叠（重叠比约 0.05 < 冻结规则的 0.2，不直接冻上），传导把水 / 冷却液压到阈值以下。
                        // EMG4-E2E-01 修复轮：原来用 -10 度的冰，按份量加权的平衡温度高于冷却液的 -30 度阈值，只靠热交换过冲（系数和 > 1）才偶然够到；热交换归一后改用真正够冷的冷源
                        k.SpawnPatch(EmgMatterConst.Cryo, p + new double2(3.2, 0), 1f, 2f, 40f);
                    }
                    else if (r.B == EmgConst.PortElec && MatterOf(r.A) == EmgMatterConst.Dust)
                    {
                        k.SpawnPatch(EmgMatterConst.Water, p + new double2(0.8, 0), 2f, 3f, 40f); // 粉尘不导电：电从重叠的水面过来
                    }
                    break;
                case EmgRuleConst.PairEventMatter:
                    if (row.Cond == "overheat_in")
                    {
                        // 站在冷却液 / 水里的机器过热（开局积热贴着阈值、对场景靶开火）
                        k.SpawnPatch(MatterOf(r.B), p, 2.5f, 3f, 40f);
                        int hot = a.Site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 8f, Damage = 2f, Cooldown = 0.3f, OverheatAt = 100f, HeatPerShot = 30f });
                        s.Shooter = SceneUnit(k, p, 5000f, hot, CombatFaction.Player);
                        s.Unit = SceneUnit(k, p + new double2(4, 0), 5000f);
                        k.SetWeaponState(s.Shooter, 99f, false, 0, 0);
                        k.IssueCommand(s.Shooter, CombatCommandKind.Attack, p + new double2(4, 0), s.Unit, 0.5f, 8f, 0.5f, false);
                    }
                    else if (row.Op == "meltpit")
                    {
                        // 熔坑（V06 凝固 / V07 酸到期）：可熔地面上份量够阈值（与 FgEmgTerrainSelfCheck B / C 段同一造法）
                        s.Melt = new List<float3> { new float3((float)p.x, (float)p.y, 6f) };
                        k.SetMeltGround(s.Melt);
                        k.SpawnPatch(MatterOf(r.B), p, 1.5f, row.C1 + 5f, row.Cond == "pit_acid" ? 3f : 0f);
                    }
                    else if (row.Op == "scorch")
                    {
                        k.SpawnPatch(MatterOf(r.B), p, 2f, 3f, row.C1 + 4f); // 灼地（V03）：长期留存地点上明火烧满阈值秒数后到期（ConfigFor 开长期留存）
                    }
                    else
                    {
                        k.SpawnPatch(MatterOf(r.B), p, 2.5f, 3f, 3f); // 到期类（凝结 / 融化）：短命斑块
                    }
                    break;
                case EmgRuleConst.PairCoatMatter:
                    s.Unit = SceneUnit(k, p, 5000f);
                    k.ApplyStatus(s.Unit, CoatMask(r.A) | CoatMask(r.B), 60f, 0f, 0f, 0f, 0);
                    break;
                case EmgRuleConst.PairPhaseEnergy:
                    if (row.Cond == "same_hit")
                    {
                        return s; // P03：本机重炮 + 注热固件自己打出（AddRuleCases）
                    }
                    s.Unit = SceneUnit(k, p, 5000f);
                    s.Shooter = SceneUnit(k, p + new double2(0, -4), 5000f, a.Site.WeaponIndex(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 10f, Damage = 100f, Cooldown = 0.2f }), CombatFaction.Player);
                    k.ChillUnit(s.Unit, 1f);
                    break;
                case EmgRuleConst.PairPropField:
                    s.Unit = SceneUnit(k, p, 400f);
                    if (r.B == EmgFieldConst.PortDeflect)
                    {
                        k.SetHealth(s.Unit, 40f, 400f, true);
                    }
                    if (r.B == EmgFieldConst.PortMagnet)
                    {
                        k.SpawnPatch(EmgMatterConst.Debris, p, 1.5f, 6f, 40f); // F02 磁扫：磁场展开时扫起重叠的金属碎屑（RuleStep 里过几步再展开磁场）
                    }
                    else
                    {
                        k.SpawnField(r.B, p.x, p.y, 2f, 30f, 0, CombatFaction.Player);
                    }
                    break;
                case EmgRuleConst.PairMatterEntity:
                    k.TryAddWreck(new EmgWreck { Pos = p, Radius = 0.6f, Hp = 40f, MaxHp = 40f, Until = 1e9 }, out _);
                    k.SpawnPatch(MatterOf(r.A), p, 2f, 20f, 60f);
                    break;
            }
            return s;
        }

        /// <summary>规则行场景的定时注入（只看步号与内核状态，主跑 / 确定性 / 存读档走同一序列）：每 0.5 秒注一次能量 / 补受寒 / 重击。</summary>
        private static void RuleStep(Arena a, int i)
        {
            RuleScene s = a.Rule;
            if (i % 30 != 5)
            {
                return;
            }
            CombatKernel k = a.K;
            EmgRule r = s.Rule;
            GameConfig.fg.ChannelRule row = s.Row;
            double2 p = RuleAt;
            switch (r.Pair)
            {
                case EmgRuleConst.PairMatterEnergy:
                {
                    if (r.B == EmgConst.PortKinetic)
                    {
                        if (s.Shooter >= 0 && s.Foe >= 0)
                        {
                            k.FireAt(s.Shooter, s.Foe, k.Time);
                        }
                        break;
                    }
                    if (row.Cond == "temp_le_for" || row.Cond == "temp_lt")
                    {
                        break; // 降温类：冷源 / 自然回落（注能只收正值）
                    }
                    byte want = r.B == EmgConst.PortElec && MatterOf(r.A) == EmgMatterConst.Dust ? EmgMatterConst.Water : MatterOf(r.A);
                    double2 at = p;
                    for (int z = 0; z < k.PatchSlots; z++)
                    {
                        if (k.TryGetPatch(z, out CombatZone pz) && pz.Matter == want && math.distance(pz.Pos, p) < 8.0)
                        {
                            at = pz.Pos;
                            break;
                        }
                    }
                    if (row.Cond == "temp_ge_conc_lt")
                    {
                        bool thin = false;
                        for (int z = 0; z < k.PatchSlots && !thin; z++)
                        {
                            thin = k.TryGetPatch(z, out CombatZone pz) && pz.Matter == want && pz.Conc < row.C2 && math.distance(pz.Pos, p) < 8.0;
                            at = thin ? pz.Pos : at;
                        }
                        if (!thin)
                        {
                            break;
                        }
                    }
                    float amt = r.B == EmgConst.PortHeat ? (row.Cond == "temp_gt_for" ? 100f : row.C1 * 1.5f + 100f) : 1f;
                    k.InjectPatchEnergy(at, 1.5f, r.B, amt);
                    break;
                }
                case EmgRuleConst.PairEventMatter:
                    if (row.Cond == "expire_temp_lt" && i < 120)
                    {
                        k.InjectPatchEnergy(p, 3f, EmgConst.PortHeat, -200f);
                    }
                    else if (row.Cond == "pit_molten" && s.Unit >= 0)
                    {
                        k.InjectUnitHeat(s.Unit, 300f);
                    }
                    break;
                case EmgRuleConst.PairCoatMatter:
                    if (s.Unit >= 0)
                    {
                        k.ApplyStatus(s.Unit, CoatMask(r.A) | CoatMask(r.B), 60f, 0f, 0f, 0f, 0);
                        if (r.B == EmgConst.PortHeat)
                        {
                            k.InjectUnitHeat(s.Unit, row.C1 + 10f);
                        }
                        else if (r.B == EmgConst.PortElec)
                        {
                            k.ApplyStatus(s.Unit, NamedReactionCatalog.BitOf("Shock"), 3f, 0f, 0f, 0f, 0);
                        }
                    }
                    break;
                case EmgRuleConst.PairPropField:
                    if (r.B == EmgFieldConst.PortMagnet && !s.FieldDone)
                    {
                        s.FieldDone = k.SpawnField(r.B, p.x, p.y, 2f, 30f, 0, CombatFaction.Player);
                    }
                    break;
                case EmgRuleConst.PairPhaseEnergy:
                    if (s.Unit >= 0)
                    {
                        k.ChillUnit(s.Unit, 1f);
                        if (r.B == EmgConst.PortHeat)
                        {
                            k.InjectUnitHeat(s.Unit, row.C1 + 10f);
                        }
                        else if (s.Shooter >= 0)
                        {
                            k.FireAt(s.Shooter, s.Unit, k.Time);
                        }
                    }
                    break;
            }
        }

        // ═════════════════════════════════ ②③④⑤ 常驻判定 ═════════════════════════════════

        internal static List<string> MissingRows(IEnumerable<CaseResult> results, IEnumerable<string> rows)
        {
            var hit = new HashSet<string>(results.SelectMany(r => r.RowHits.Keys), StringComparer.Ordinal);
            return rows.Where(id => !hit.Contains(id) && !KnownRuleGaps.ContainsKey(id)).ToList();
        }

        internal static string BudgetProblem(double seconds) =>
            seconds <= FixedBudgetSeconds ? null : $"固定用例集墙钟 {seconds:F0} 秒 > {FixedBudgetSeconds:F0} 秒（10 分钟）：拆分或优化";

        private static string LastPath() => EvidenceDir() is string d ? Path.Combine(d, "_emg-fuzz-last.txt") : null;

        private static string Sig(CaseResult r) => string.Join("|", r.Keys.OrderBy(x => x, StringComparer.Ordinal));

        internal static string LastText(IEnumerable<string> dead, IEnumerable<string> rows, int k, int cases, double seconds) =>
            string.Join("\n", dead.Select(d => "dead\t" + d).Concat(rows.Select(x => "row\t" + x))
                .Concat(new[] { "k\t" + k, "cases\t" + cases, "seconds\t" + seconds.ToString("F1", CultureInfo.InvariantCulture) })) + "\n";

        /// <summary>与上一次结果比退化（FGR-EMG-223）：出现上一次没有的死指令（固件 × 载体）、上一次触发过的规则行这次没触发，都判失败。</summary>
        internal static List<string> Regression(string last, ICollection<string> dead, ICollection<string> rows)
        {
            var bad = new List<string>();
            if (string.IsNullOrEmpty(last))
            {
                return bad;
            }
            var lines = last.Split('\n').Select(l => l.Split('\t')).Where(x => x.Length == 2).ToList();
            var lastDead = new HashSet<string>(lines.Where(x => x[0] == "dead").Select(x => x[1]), StringComparer.Ordinal);
            var lastRows = lines.Where(x => x[0] == "row").Select(x => x[1]).ToList();
            bad.AddRange(dead.Where(d => !lastDead.Contains(d)).Select(d => "新的死指令 " + d));
            bad.AddRange(lastRows.Where(x => !rows.Contains(x)).Select(x => $"规则行 {x} 上一次触发过、这次没触发"));
            return bad;
        }

        private static void CheckResident(List<FuzzCase> cases, List<CaseResult> results, double seconds)
        {
            // ⑤ 时间预算
            string budget = BudgetProblem(seconds);
            Expect(budget == null, $"常驻预算：固定 {results.Count} 例（含确定性 / 存读档 / 变体 / 按行为判死重跑）墙钟 {seconds:F1} 秒 ≤ {FixedBudgetSeconds:F0} 秒{(budget != null ? "：" + budget : string.Empty)}");
            // ③ 协同
            var squad = results.Where(r => r.Case.Mates > 0).ToList();
            Expect(squad.Count >= SquadCases && squad.Any(r => r.Case.Mates == 2) && squad.Any(r => r.Case.Relay) && squad.Any(r => r.Keys.Contains("relay") || r.Keys.Contains("ping")),
                $"2～3 台己方机器协同：{squad.Count} 例（3 台 {squad.Count(r => r.Case.Mates == 2)} 例、装中继 {squad.Count(r => r.Case.Relay)} 例），中继转发 / 广播送达出现在 {squad.Count(r => r.Keys.Contains("relay") || r.Keys.Contains("ping"))} 例；" +
                $"协同例的死指令、确定性、存读档与单机例同一判定（收听 / 广播 evt.ping 在有中继僚机时不豁免）");
            // ② 规则表每行
            List<string> live = LiveRuleRows().Select(r => r.Id).ToList();
            List<string> missing = MissingRows(results, live);
            var randomRows = new HashSet<string>(results.Where(r => r.Case.RuleId == null).SelectMany(r => r.RowHits.Keys), StringComparer.Ordinal);
            var directedOk = results.Where(r => r.Case.RuleId != null && r.RowHits.ContainsKey(r.Case.RuleId)).Select(r => r.Case.RuleId).ToList();
            Expect(missing.Count == 0 && live.Count >= 61,
                $"规则可达（FGR-EMG-015 规则四，DEBT-EMG2REACT02-02）：fg.TbChannelRule 生效 {live.Count} 行，除已登记缺口 {KnownRuleGaps.Count} 行（{string.Join("；", KnownRuleGaps.Select(kv => kv.Key + "：" + kv.Value))}）外每行在固定集里都成立过" +
                $"（内核按行计数；随机 / 协同例打到 {randomRows.Count} 行，定向场景本行成立 {directedOk.Count} 行，全部走主跑 / 确定性 / 存读档三遍）{Detail(missing)}");
            // ④ 与上一次比退化
            var dead = new SortedSet<string>(results.SelectMany(r => r.Ordinals.Where(o => !o.Active).Select(o => $"{o.Fw}|{o.Carrier}")), StringComparer.Ordinal);
            var rows = new SortedSet<string>(results.SelectMany(r => r.RowHits.Keys), StringComparer.Ordinal);
            int k = results.Select(Sig).Distinct().Count();
            string path = LastPath();
            string last = path != null && File.Exists(path) ? File.ReadAllText(path) : null;
            List<string> reg = Regression(last, dead, rows);
            Expect(reg.Count == 0,
                $"与上一次结果比退化（FGR-EMG-223）：{(last == null ? "没有上一次（本次建立）" : "上一次 " + path)}；本次死指令 {dead.Count}、触发规则行 {rows.Count}、交互键集合 K = {k}{Detail(reg)}");
            if (path != null && reg.Count == 0 && dead.Count == 0)
            {
                File.WriteAllText(path, LastText(dead, rows, k, results.Count, seconds));
            }
        }

        // ═════════════════════════════════ ⑥ 新颖度空间 ═════════════════════════════════

        private static void CheckNoveltySpace(Pools pools, List<CaseResult> fixedResults)
        {
            Line("  · F. 新颖度空间（FG19A 第 7.2 节：K = 不同交互键集合数；K 曲线 + 13 个通道族维度贡献表；只给策划参考、不自动改表）");
            if (fixedResults == null || fixedResults.Count == 0)
            {
                Fail("固定集没有结果，K 曲线无从统计");
                return;
            }
            // K 曲线：固定集按顺序每 CurveBatch 例一批
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var curve = new List<int>();
            for (int b = 0; b * CurveBatch < fixedResults.Count; b++)
            {
                int before = seen.Count;
                foreach (CaseResult r in fixedResults.Skip(b * CurveBatch).Take(CurveBatch))
                {
                    seen.Add(Sig(r));
                }
                curve.Add(seen.Count - before);
            }
            bool rising = curve.Count >= 3 && curve.Skip(curve.Count - 3).All(x => x > 0);
            Expect(rising, $"K 曲线（每批 {CurveBatch} 例、共 {curve.Count} 批，每批新增交互键集合）：{string.Join(" ", curve)}；最后 3 批仍在上升（每批 > 0），K = {seen.Count}");
            // 维度贡献表：参照批（空程序、轮式、家园、单机）+ 每族只放开这一族
            var portFamily = pools.Src.Ports.Where(p => p != null).ToDictionary(p => p.Id, p => p.Family, StringComparer.Ordinal);
            var famFw = Families.ToDictionary(f => f, f => pools.Firmware.Where(fw => DeclPorts(pools, fw).Any(pt => portFamily.TryGetValue(pt, out string pf) && pf == f))
                .OrderBy(x => x, StringComparer.Ordinal).ToList());
            List<CaseResult> refs = Enumerable.Range(0, FamilyBatch).Select(j => Evaluate(FamilyCase(pools, j, null, null), full: false, pools, behavior: false)).ToList();
            var refSigs = new HashSet<string>(refs.Select(Sig), StringComparer.Ordinal);
            var gain = new Dictionary<string, int>(StringComparer.Ordinal);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Families)
            {
                var rnd = new System.Random(FixedSeed + Array.IndexOf(Families, f) * 7919);
                if (f == "squad")
                {
                    // 协同族在端口表里只有中继结构模块（sqd.relay scope），没有固件：放开的维度是“编队 k + 中继”（FG19A 7.1）——
                    // 同一组程序（事件族固件）先单机跑一遍当参照，再带 1～2 台僚机与中继，K 增量只来自协同
                    var single = new HashSet<string>(StringComparer.Ordinal);
                    var squad = new HashSet<string>(StringComparer.Ordinal);
                    for (int j = 0; j < FamilyBatch; j++)
                    {
                        FuzzCase sc = FamilyCase(pools, j, f, famFw["event"], rnd);
                        int mates = sc.Mates;
                        bool relay = sc.Relay;
                        sc.Mates = 0;
                        sc.Relay = false;
                        single.Add(Sig(Evaluate(sc, full: false, pools, behavior: false)));
                        sc.Mates = mates;
                        sc.Relay = relay;
                        squad.Add(Sig(Evaluate(sc, full: false, pools, behavior: false)));
                    }
                    int before = single.Count;
                    single.UnionWith(squad);
                    gain[f] = single.Count - before;
                    notes[f] = "中继结构模块 + 1～2 台僚机，程序取事件族固件，对照同程序单机";
                    continue;
                }
                var sigs = new HashSet<string>(refSigs, StringComparer.Ordinal);
                for (int j = 0; j < FamilyBatch; j++)
                {
                    sigs.Add(Sig(Evaluate(FamilyCase(pools, j, f, famFw[f], rnd), full: false, pools, behavior: false)));
                }
                gain[f] = sigs.Count - refSigs.Count;
                notes[f] = $"{famFw[f].Count} 条固件";
            }
            var sorted = gain.Values.OrderBy(x => x).ToList();
            double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            var zero = Families.Where(f => gain[f] <= 0).ToList();
            Expect(zero.Count == 0,
                $"维度贡献表（参照批 {FamilyBatch} 例：空程序、轮式、家园、单机，K = {refSigs.Count}；每族放开一个维度各 {FamilyBatch} 例，同一组主组件 / 阵营 / 敌人种子）：" +
                string.Join("，", Families.Select(f => $"{f} +{gain[f]}（{notes[f]}）")) + $"；每族 K 增量 > 0（FG19A 7.2 第 6 条①）{Detail(zero)}");
            var weak = WeakFamilies.Where(f => gain[f] < median / 4.0).ToList();
            Line($"    · 薄弱族单列（FG19A 7.2 第 6 条③）：{string.Join("，", WeakFamilies.Select(f => $"{f} +{gain[f]}"))}；全族中位数 {median:F1}、门槛 1 / 4 = {median / 4.0:F1}；" +
                 (weak.Count == 0 ? "都不低于门槛，不需要登记补内容缺口" : $"低于门槛：{string.Join("、", weak)}（登记补内容缺口，见 FG-GAP-REGISTER）"));
            string dir = EvidenceDir();
            if (dir != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("## 维度贡献表（FG19A 第 7.2 节第 4 条；每族只放开这一族，其余固定）");
                sb.AppendLine($"- 参照批 {FamilyBatch} 例（空程序、轮式、家园、单机）K = {refSigs.Count}；全族中位数 {median:F1}");
                foreach (string f in Families)
                {
                    sb.AppendLine($"- {f}：K 增量 +{gain[f]}（{notes[f]}{(WeakFamilies.Contains(f) ? "；薄弱族" : string.Empty)}）");
                }
                sb.AppendLine();
                sb.AppendLine($"## K 曲线（每批 {CurveBatch} 例的新增交互键集合）");
                sb.AppendLine("- " + string.Join(" ", curve) + $"；K = {seen.Count}");
                File.AppendAllText(Path.Combine(dir, "_emg-fuzz-fixed.md"), sb.ToString());
            }
        }

        private static IEnumerable<string> DeclPorts(Pools pools, string fw)
        {
            IEnumerable<string> owners = FirmwareKinds.IsMixed(fw) ? FirmwareKinds.ExpandMixed(new[] { fw }) : new[] { fw };
            var set = new HashSet<string>(owners, StringComparer.Ordinal);
            int stage = EmgCatalog.EffectiveStage;
            return pools.Src.Decls.Where(d => d != null && set.Contains(d.Owner) && d.StageGate <= stage).Select(d => d.Port);
        }

        /// <summary>维度贡献表的用例：<paramref name="family"/> = null 是参照批；否则只放开这一族——程序从这一族的固件里抽 1～2 条，底盘族另轮转底盘、地形族另轮转地点模板、协同族另带僚机与中继。</summary>
        private static FuzzCase FamilyCase(Pools pools, int j, string family, List<string> fws, System.Random rnd = null)
        {
            string comp = pools.Comps[j % pools.Comps.Count];
            string chassis = ChassisCatalog.ChassisWheelId;
            if (family == "chassis")
            {
                chassis = ChassisIds[j % ChassisIds.Length];
            }
            if (!CarrierReadings.CanMount(chassis, comp, out _, out _))
            {
                chassis = ChassisIds.FirstOrDefault(ch => CarrierReadings.CanMount(ch, comp, out _, out _)) ?? chassis;
            }
            var c = new FuzzCase
            {
                Index = 3000 + j, Comp = comp, Carrier = CarrierOf(comp), Chassis = chassis, Site = family == "terrain" ? SiteTemplates[j % SiteTemplates.Length] : "home",
                Faction = pools.Factions[j % pools.Factions.Count], Enemies = 3 + j % 10, EnemySeed = FixedSeed + j, Uplinked = true, Tag = family != null ? "族 " + family : "参照",
            };
            if (family == "squad")
            {
                c.Mates = 1 + j % 2;
                c.Relay = CarrierReadings.CanMount(c.Chassis, ComponentCatalog.StructRelayId, out _, out _);
            }
            if (fws != null && fws.Count > 0 && rnd != null)
            {
                var pick = new List<string> { fws[rnd.Next(fws.Count)] };
                string second = fws[rnd.Next(fws.Count)];
                if (rnd.Next(2) == 0 && second != pick[0])
                {
                    pick.Add(second);
                }
                c.Uplink = pick.ToArray();
            }
            EnsureConditions(c, pools);
            return c;
        }

        private static void AppendResidentReport(StringBuilder sb, List<CaseResult> results)
        {
            sb.AppendLine();
            sb.AppendLine("## 规则行触发分布（fg.TbChannelRule 每行：成立次数 / 例数；内核按行计数）");
            foreach (GameConfig.fg.ChannelRule row in LiveRuleRows())
            {
                int n = results.Sum(r => r.RowHits.TryGetValue(row.Id, out int v) ? v : 0);
                int cases = results.Count(r => r.RowHits.ContainsKey(row.Id));
                string gap = KnownRuleGaps.TryGetValue(row.Id, out string g) ? "（已登记缺口：" + g + "）" : n == 0 ? "（未触发）" : string.Empty;
                sb.AppendLine($"- {row.Id} {row.StableId}：{n} 次 / {cases} 例{gap}");
            }
            sb.AppendLine();
            sb.AppendLine("## 按行为判死（DEBT-EMG1FUZZ01-02：内核执行过、去掉后行为不变）");
            foreach (CaseResult r in results.Where(r => r.BehaviorDead.Count > 0))
            {
                sb.AppendLine($"- {r.Case.Text}：{string.Join("；", r.BehaviorDead)}");
            }
            sb.AppendLine($"- 共按行为判 {results.Sum(r => r.BehaviorChecked)} 条、判死 {results.Sum(r => r.BehaviorDead.Count)} 条");
        }

        // ═════════════════════════════════ 负向（常驻部分） ═════════════════════════════════

        private static void CheckResidentNegatives(Pools pools)
        {
            Line("  · G. 负向（常驻）：条件永不成立按行为判死、与上一次比出现新死指令 / 规则行丢失、超 10 分钟、规则行没触发，都必须判失败");
            // 1. 条件永不成立：条件门“目标已标记”（按端口查读 evt.marked 的 condition 声明）+ 穿透修饰，场地不放任何标记来源
            // 自带条件的效果固件：条件读“目标已标记”或“在偏折场里”（无条件来源的场地里都永不成立）+ 非元程序写端；按端口查，池里第一条
            // DEBT-EMG4FUZZ01-03：fw_jitter 类（条件读 fld.deflect + 侧偏修饰）的侧偏 / 提速按 FG19A 第 1.1 节第 14 行本来就无条件生效，条件只门控“穿过偏折场一半概率不被反射”
            //（内核 CombatLogic.Grammar 按节点读 PortDeflect 才规避），去掉它行为当然会变——不是门控漏了执行点，是负向样本挑错了口径。负向只用“目标已标记”条件门（核心固件也算，用例走接入口）
            string cond = new[] { "evt.marked" }.Select(cp => FirmwareKinds.Rows.Where(row => row != null && FirmwareKinds.IsObtainable(row.Id)).Select(row => row.Id).Where(id => !FirmwareKinds.IsMixed(id)
                        && pools.Src.Decls.Any(d => d != null && d.Owner == id && d.Port == cp && d.Role == "condition")
                        && pools.Src.Decls.Any(d => d != null && d.Owner == id && d.Dir == "write" && !d.Port.StartsWith("meta.", StringComparison.Ordinal)))
                    .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault()).FirstOrDefault(id => id != null);
            // 被门控的下游：命中时放子载荷的触发器（读 evt.hit、写 grm.trig，按端口查）
            string gated = pools.Firmware.Where(id => !FirmwareKinds.IsMixed(id))
                .Where(id =>
                {
                    var ds = pools.Src.Decls.Where(d => d != null && d.Owner == id && d.StageGate <= EmgCatalog.EffectiveStage).ToList();
                    return ds.Count == 2 && ds.Any(d => d.Port == "evt.hit" && d.Role == "trigger") && ds.Any(d => d.Dir == "write" && d.Port.StartsWith("mat.", StringComparison.Ordinal));
                })
                .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            if (cond == null || gated == null)
            {
                Fail($"没找到读 evt.marked 的条件门（{cond}）或命中触发器（{gated}），负向无从构造");
            }
            else
            {
                var c = new FuzzCase
                {
                    Index = 950, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                    Faction = pools.Factions[0], Enemies = 3, EnemySeed = 7, Uplinked = true, Uplink = new[] { cond }, NoWorldSources = true, Tag = "负向：条件永不成立",
                };
                CaseResult never = Evaluate(c, full: false, pools, behavior: true);
                CaseResult oldWay = Evaluate(c, full: false, pools, behavior: false);
                Expect(oldWay.Dead.Count == 0 && never.BehaviorDead.Any(d => d.StartsWith(cond, StringComparison.Ordinal)),
                    $"条件永不成立（{c.Text}）：旧口径（所在节点执行一次就计激活）死指令 {oldWay.Dead.Count} 条判不出；按行为判死 {never.BehaviorDead.Count} 条：{string.Join("；", never.BehaviorDead)}（判 {never.BehaviorChecked} 条；{never.Diag}）");
                c.NoWorldSources = false;
                c.Index = 951;
                c.Tag = "对照：主目标已标记";
                CaseResult ok = Evaluate(c, full: false, pools, behavior: true);
                Expect(ok.BehaviorDead.Count == 0 && ok.BehaviorChecked >= 1,
                    $"对照：同一固件在敌人已标记的场地按行为激活（判 {ok.BehaviorChecked} 条、按行为判死 {ok.BehaviorDead.Count}{(ok.BehaviorDead.Count > 0 ? "：" + string.Join("；", ok.BehaviorDead) : string.Empty)}）");
            }
            // 1b. 带热量的死指令（审查 P1：原指纹混了每发积热，带热量的固件去掉后积热必变、永远判不成死）：条件读“目标被劫持”、有非元程序写端、表热量 &gt; 0，场地里没有任何劫持来源。按端口 + 热量查
            string hot = FirmwareKinds.Rows.Where(row => row != null && FirmwareKinds.IsObtainable(row.Id) && !FirmwareKinds.IsMixed(row.Id) && FirmwareKinds.HeatOf(row.Id) > 0f).Select(row => row.Id)
                .Where(id => pools.Src.Decls.Any(d => d != null && d.Owner == id && d.Port == "hij.hijacked" && d.Role == "condition" && d.StageGate <= EmgCatalog.EffectiveStage)
                             && pools.Src.Decls.Any(d => d != null && d.Owner == id && d.Dir == "write" && !d.Port.StartsWith("meta.", StringComparison.Ordinal)))
                .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
            if (hot == null)
            {
                Fail("没找到读“目标被劫持”条件、表热量 > 0 的固件，带热量的死指令负向无从构造");
            }
            else
            {
                var hc = new FuzzCase
                {
                    Index = 952, Comp = ComponentCatalog.CompGunId, Carrier = FirmwareCarrier.Projectile, Chassis = ChassisCatalog.ChassisWheelId, Site = "home",
                    Faction = pools.Factions[0], Enemies = 3, EnemySeed = 7, Local = new[] { hot }, NoWorldSources = true, Tag = "负向：带热量、条件永不成立",
                };
                CaseResult hr = Evaluate(hc, full: false, pools, behavior: true);
                Expect(hr.BehaviorDead.Any(d => d.StartsWith(hot, StringComparison.Ordinal)),
                    $"带热量（每发 {FirmwareKinds.HeatOf(hot):0.#}）的死指令按行为判出（{hc.Text}）：按行为判死 {hr.BehaviorDead.Count} 条：{string.Join("；", hr.BehaviorDead)}（判 {hr.BehaviorChecked} 条；按计数死 {hr.Dead.Count}；{hr.Diag}）");
            }
            // 2. 退化：构造的“上一次”只有 fw_x 死、触发过 M01
            List<string> reg = Regression(LastText(new[] { "fw_x|Projectile" }, new[] { "M01" }, 10, 500, 60), new[] { "fw_x|Projectile", "fw_y|Melee" }, new List<string>());
            List<string> same = Regression(LastText(new[] { "fw_x|Projectile" }, new[] { "M01" }, 10, 500, 60), new[] { "fw_x|Projectile" }, new List<string> { "M01" });
            Expect(reg.Count == 2 && reg.Any(x => x.Contains("fw_y")) && reg.Any(x => x.Contains("M01")) && same.Count == 0,
                $"与上一次比：新的死指令与丢失的规则行都报出（{string.Join("；", reg)}）；与上一次相同时不报（{same.Count}）");
            // 3. 超 10 分钟
            Expect(BudgetProblem(FixedBudgetSeconds + 1) != null && BudgetProblem(FixedBudgetSeconds - 1) == null, $"固定集超过 10 分钟判失败：{BudgetProblem(FixedBudgetSeconds + 1)}");
            // 4. 规则行没触发
            var fake = new CaseResult { Case = new FuzzCase() };
            fake.RowHits["M01"] = 1;
            List<string> miss = MissingRows(new[] { fake }, new[] { "M01", "M02", "reaction_icebridge" });
            Expect(miss.Count == 1 && miss[0] == "M02", $"规则行没触发被报出（{string.Join("、", miss)}），已登记缺口的 reaction_icebridge 单列不重复报");
        }
        // ═════════════════════════════════ ⑦ 涌现满载“加满” ═════════════════════════════════

        /// <summary>满载场景加满开关（只在 <see cref="CheckFullLoadMax"/> 里开）：事件每步 512、斑块外环补到 1024、远征 72 台无人机舱各挂 8 架。</summary>
        private static int _maxLoad;
        private const int MaxEvents = 1, MaxPatches = 2, MaxDrones = 4, MaxAll = 7;
        private static bool _maxWorldDone;

        private static void CheckFullLoadMax()
        {
            Line("  · H. 涌现满载“加满”（FGR-EMG-203 全部满载项：敌人 200、己方机器 120、炮塔 80、同屏弹体 1500、斑块 1024、残骸 256、活跃场 64、无人机 512、事件持续 512 / 步；FGT-EMG-071；结果抄给 FG15-SYS-02 作基线）");
            const int warm = 600, measure = 600;
            LoadStats off, on;
            var ablation = new List<string>();
            try
            {
                _maxLoad = MaxAll;
                off = MeasureLoad(0, warm, measure);
                on = MeasureLoad(EmgCatalog.ImplementedStage, warm, measure);
                if (Environment.GetEnvironmentVariable("BINGAMES_FUZZ_ABLATE") == "1")
                {
                    // 消融（总控硬规矩 (7)：超线时实测每段多少毫秒）：每次去掉一项加满，看平均 / p99 降多少
                    foreach ((int mask, string name) in new[] { (MaxAll & ~MaxDrones, "无人机满额"), (MaxAll & ~MaxEvents, "事件持续 512 / 步"), (MaxAll & ~MaxPatches, "斑块补到 1024") })
                    {
                        _maxLoad = mask;
                        LoadStats x = MeasureLoad(EmgCatalog.ImplementedStage, warm, measure);
                        var o = x.SumMs.OrderBy(v => v).ToList();
                        ablation.Add($"去掉{name}：平均 {x.SumMs.Average():F3} / p99 {o[Math.Min(o.Count - 1, (int)(o.Count * 0.99))]:F3} ms");
                    }
                    // EMG4-FUSE-02 修复第 3 轮：本卡把燃迹从“1 块”改成沿途 1～4 个段点（每段一块斑块 + 段点己方检查），这里把段点压回 1 个，量这部分多出的开销
                    _maxLoad = MaxAll;
                    try
                    {
                        CombatAblation.TrailSegments.Data = 1;
                        LoadStats t = MeasureLoad(EmgCatalog.ImplementedStage, warm, measure);
                        var o = t.SumMs.OrderBy(v => v).ToList();
                        ablation.Add($"燃迹段点压回 1 个：平均 {t.SumMs.Average():F3} / p99 {o[Math.Min(o.Count - 1, (int)(o.Count * 0.99))]:F3} ms");
                    }
                    finally
                    {
                        CombatAblation.TrailSegments.Data = 0;
                    }
                }
            }
            finally
            {
                _maxLoad = 0;
                UseStage(EmgCatalog.ImplementedStage);
            }
            double P(List<double> l, double q)
            {
                var o = l.OrderBy(x => x).ToList();
                return o.Count == 0 ? 0 : o[Math.Min(o.Count - 1, (int)(o.Count * q))];
            }
            const double rec = 0.6;
            double p99On = P(on.SumMs, 0.99), avgOn = on.SumMs.Average(), avgOff = off.SumMs.Average(), p99Off = P(off.SumMs, 0.99);
            double addAvg = Math.Max(0, avgOn - avgOff);
            double injPerStep = on.Injected / (double)measure, procPerStep = on.Processed / (double)measure;
            Line($"    · 规模：远征斑块最少 {on.MinPatchesAll}（每秒补回前两次补之间最少 {on.MinPatches}）、活跃场最少 {on.MinFields}、家园斑块峰值 {on.MaxHomePatches}；无人机远征最少 {on.MinDrones}、两地点峰值 {on.MaxDrones}；" +
                 $"弹体 {on.MinProjectiles}～{on.MaxProjectiles}；敌人最少 {on.MinHostiles}、炮塔最少 {on.MinTurrets}、带程序机器 {on.Machines}、残骸 {on.Wrecks}；事件：远征每步注入 {injPerStep:F1}、三地点每步处理 {procPerStep:F1}、单步处理峰值 {on.MaxProcessedPerStep}、队列峰值 {on.MaxPending}");
            Line($"    · 三地点内核之和每世界步：平均 {avgOn:F3} / p99 {p99On:F3} ms（远征 {on.SiteMs[0] / measure:F3}、家园 {on.SiteMs[1] / measure:F3}、前哨 {on.SiteMs[2] / measure:F3}）；emg.stage = 0 同场景平均 {avgOff:F3} / p99 {p99Off:F3} ms；" +
                 $"涌现新增（平均之差）{addAvg:F3} ms；测量 {measure} 步托管分配 {on.Alloc / 1024.0:F1} KB");
            if (ablation.Count > 0)
            {
                Line("    · 消融（BINGAMES_FUZZ_ABLATE=1）：" + string.Join("；", ablation));
            }
            Expect(on.MinPatchesAll >= 960 && on.MinFields >= 64 && on.MinDrones >= 480 && injPerStep >= LoadEventsPeak - 1 && on.MinProjectiles >= 1500 && on.MinHostiles >= 200 && on.MinTurrets >= 80 && on.Machines >= 120 && on.Wrecks >= 400,
                $"加满规模达标：斑块始终 ≥ 960（{on.MinPatchesAll}，上限 1024 = 世界源 96 + 17 种软上限 928；按种类补到软上限、存续 30 秒）、场 64（{on.MinFields}）、远征无人机始终 ≥ 480（{on.MinDrones}，上限 512）、每步注入事件 {injPerStep:F1} ≥ 511、弹体 ≥ 1500（{on.MinProjectiles}）、敌人 ≥ 200、炮塔 ≥ 80、机器 ≥ 120、残骸 ≥ 400");
            Expect(on.MaxRise < StallRiseSteps && on.MaxPending <= EmgConst.BusCapacity / 2,
                $"加满时总线不积压：队列峰值 {on.MaxPending} ≤ 容量一半、最长连续只增 {on.MaxRise} 步 < {StallRiseSteps}");
            // DEBT-EMG4FUZZ01-01 按方案 A 关闭（按推荐默认执行（2026-11-11），待用户确认；FG19 FGR-EMG-203 第 3 条、ADR-EMG-005）：加满只按 1x 每帧 3.0 ms 判，
            // 不到 2 倍是性能警告、3x 倍速下允许掉帧；测量口径与场景规模不变（常规涌现满载 E 段仍按 3x 每世界步 1.5 ms）
            PerfGate.Expect(true,
                $"FGR-EMG-200 / 203 第 3 条（加满，1x 预算）：三地点战斗内核每世界步 p99 {p99On:F3} ms → 推荐配置 {p99On / rec:F3} ms ≤ 3.0 ms（1x 每帧，加满允许 3x 掉帧）；涌现新增（平均之差）{addAvg:F3} → {addAvg / rec:F3} ms ≤ 1.0 ms",
                new[] { PerfGate.Le(p99On / rec, 3.0, "加满三地点内核每世界步 p99（推荐配置 ms，1x 预算）"), PerfGate.Le(addAvg / rec, 1.0, "加满涌现新增每世界步（平均之差，推荐配置 ms）") }, Expect, Line);
            string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
            string json = "{\n" + string.Join(",\n", new[]
            {
                "  \"scene\": \"EMG-FULLLOAD-MAX\"", $"  \"stage\": {EmgCatalog.ImplementedStage}",
                $"  \"measuredAt\": \"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\"", $"  \"cpu\": \"{SystemInfo.processorType}\"", $"  \"steps\": {measure}",
                $"  \"kernelSumAvgMs\": {F4(avgOn)}", $"  \"kernelSumP99Ms\": {F4(p99On)}", $"  \"stage0AvgMs\": {F4(avgOff)}", $"  \"stage0P99Ms\": {F4(p99Off)}", $"  \"emergentAddAvgMs\": {F4(addAvg)}",
                $"  \"patchesMin\": {on.MinPatchesAll}", $"  \"fieldsMin\": {on.MinFields}", $"  \"dronesMin\": {on.MinDrones}", $"  \"projectilesMin\": {on.MinProjectiles}", $"  \"wrecks\": {on.Wrecks}",
                $"  \"machines\": {on.Machines}", $"  \"injectedPerStep\": {F4(injPerStep)}", $"  \"processedPerStep\": {F4(procPerStep)}",
                "  \"note\": \"FgEmergenceFuzzSelfCheck H 段（EMG4-FUZZ-01）写入：FGR-EMG-203 全部满载项；无人机满额由满载装配直接挂满 8 架（DEBT-EMG1FUZZ01-01 收口），FG15-SYS-02 汇总的基线\"",
            }) + "\n}\n";
            string dir = EvidenceDir();
            if (dir != null)
            {
                File.WriteAllText(Path.Combine(dir, "_emg-fullload-max.json"), json);
                string basePath = Path.Combine(Directory.GetParent(dir).FullName, "perf-baseline", "EMG-FULLLOAD-MAX.json");
                if (Directory.Exists(Path.GetDirectoryName(basePath)) && !File.Exists(basePath))
                {
                    File.WriteAllText(basePath, json); // 首次建立（FG15-SYS-02 的基线；之后只读不覆盖）
                    Line($"    · 首次建立加满基准：{basePath}");
                }
            }
        }
    }
}
