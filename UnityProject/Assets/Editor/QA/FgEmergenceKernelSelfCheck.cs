using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-KRN-01 内核涌现结构与快照格式 16 的自动验收（FG19 FGR-EMG-045、061、202、211、213；FGT-EMG-040、042；ADR-EMG-001 ②③④⑦⑨⑩⑬）。
    /// 起真实内核（BinGames.Sim.Combat.CombatKernel，Burst 步进）断言行为：
    /// A 结构布局（事件 56 字节、程序 / 节点 / 指令 16 / 16 / 28 字节；4096 弹体满池内存增量在 ADR ⑬ 估算内；29 个事件码 = 端口表 evt.* 码 − 300）；
    /// B 程序表为空时逐字不变：同一场景在 M10 基线代码（TEngine e869a26e）上实测的状态哈希、格式 15 / 14 快照字节哈希与本代码一致；发号只是簿记；
    /// C 结构往返（FGT-EMG-040）：程序表（去重 / 只追加）、带程序的武器与弹体（宿主五元组、每发一条新链）、总线、残骸、单位段存读档逐位一致、续跑一致、读档后去重与发号接着走；
    ///   本阶段较早提交写出的短段取缺省、较晚提交写出的长段与新段跳过；
    /// D 旧快照格式 14 / 15：读回后弹体 Prog = −1、0 代、按读入顺序补发 Id，总线空、无残骸、武器无程序；续跑发号不冲突；
    /// E 回滚（ADR T6）：新快照被只认旧格式的代码读到 → UnknownFormat、内核原样，地点按记录重建（重建分支见 FgCombatKernelSelfCheck.CheckCorruptSnapshotRebuild）；
    /// F 负向（坏程序形状、程序表满、事件码 / 链深 / 队列满、残骸满、武器指向不存在的程序、快照里越界 / 重复 / 非数 / 截断）；
    /// G 确定性（FGT-EMG-042 内核层：中途存读档续跑 = 连续跑；无状态哈希随机与求值顺序无关）。倍速 / 暂停 / 观察的世界层断言在 FgReadingMatrixSelfCheck H 段。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmergenceKernelSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmergenceKernelSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmergenceKernelSelfCheck
    {
        // 金标准：EMG1-KRN-01 开工前在 M10 基线代码（TEngine e869a26e，快照格式 15）上跑 GoldenKernel + 600 步的实测值（影子工程 batchmode，2026-10-28）。
        // 后续卡片有意改变“没有程序的单位”的行为时，在证据里说明并更新这组常数。
        private const ulong GoldenHash = 0x6055836F7484BD2EUL;
        private const ulong GoldenF15 = 0xE3B961AB60991ABDUL;
        private const ulong GoldenF14 = 0x24E82626234382C4UL;
        private const long GoldenProjectiles = 131;
        private const long GoldenDrones = 2;
        private const float Dt = 1f / 60f;
        private const int NeverIssuedChain = 1 << 24; // 远大于任何自检里发过的链号

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/QA/自检/涌现内核结构与快照 16")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[涌现内核] EMG1-KRN-01：内核涌现结构、程序表、总线、残骸与快照格式 16（FGT-EMG-040 / 042）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckLayout);
                Step(CheckGolden);
                Step(CheckRoundTrip);
                Step(CheckOldFormats);
                Step(CheckRollback);
                Step(CheckNegatives);
                Step(CheckDeterminism);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            return _fail;
        }

        // ── 场景与样本 ───────────────────────────────────────────────────────────

        /// <summary>金标准场景：4 座弹体炮塔 + 1 座无人机炮塔（即时 + 无人机载体）对 10 个突袭者，数值全是字面量（不读表，只随内核代码变化）。</summary>
        internal static CombatKernel GoldenKernel(out int turretWeapon)
        {
            var k = new CombatKernel(CombatConfig.Default, 64);
            int tw = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 26f, Damage = 14f, Cooldown = 0.5f, ProjectileSpeed = 24f, ProjectileRadius = 0.2f });
            int rw = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 16f, Damage = 6f, Cooldown = 1.2f, ProjectileSpeed = 18f, ProjectileRadius = 0.15f });
            int dw = k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 22f, Damage = 10f, Cooldown = 1f,
                Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 2, DroneSeconds = 3f, DroneRatio = 0.4f, DroneCooldown = 0.5f, DroneLeash = 8f },
            });
            int prof = k.AddProfile(new CombatBehaviorProfile { Speed = 3.5f, SenseRange = 60f });
            for (int i = 0; i < 4; i++)
            {
                SpawnTurret(k, new double2(-6 + 4 * i, 0), tw);
            }
            SpawnTurret(k, new double2(0, -2), dw);
            const CombatUnitFlags f = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.RemoveOnDeath;
            for (int i = 0; i < 10; i++)
            {
                var p = new double2(-9 + 2 * i, 24 + (i % 3) * 2);
                k.Spawn(new CombatSpawn { ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.Raider, Flags = f, Position = p, Home = new double2(0, 0), Radius = 0.6f, Speed = 3.5f, Health = 120f, MaxHealth = 120f, Weapon = rw, BehaviorProfile = prof, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f) });
            }
            turretWeapon = tw;
            return k;
        }

        private static int SpawnTurret(CombatKernel k, double2 p, int weapon)
        {
            const CombatUnitFlags f = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.RemoveOnDeath;
            return k.Spawn(new CombatSpawn { ExtKey = -1, Kind = CombatUnitKind.Turret, Faction = CombatFaction.Player, Behavior = CombatBehavior.HoldFire, Flags = f, Position = p, Home = p, Radius = 1f, Health = 400f, MaxHealth = 400f, Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFacing = new float2(0f, -1f) });
        }

        internal static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + 1.0 / 60.0);
            }
        }

        internal static EmgProgram SampleProgram(float heat) => new EmgProgram { Flags = EmgProgramFlags.NeedsFlight, ListenMask = 1u << (int)EmgEventKind.Timer, HeatPerShot = heat };

        /// <summary>根节点 2 条指令 + 1 代定时子节点 1 条（含 1 条继承副本标记），节点按序铺满指令段。</summary>
        internal static EmgNode[] SampleNodes() => new[]
        {
            new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = 2 },
            new EmgNode { Parent = 0, Gen = 1, TrigEvent = EmgEventKind.Timer, TrigCount = 3, TrigParam = 0.5f, FirstInstr = 2, InstrCount = 1, SubForm = 1, InheritMask = 1 },
        };

        internal static EmgInstr[] SampleInstrs() => new[]
        {
            new EmgInstr { Port = 101, Role = 1, Verb = 26, Ordinal = 0, Amount = 1f },
            new EmgInstr { Port = 308, Role = 2, Verb = 33, Ordinal = 1, Amount = 0.5f, A = 3f },
            new EmgInstr { Port = 101, Role = 1, Verb = 26, Ordinal = 0, Amount = 1f, Flags = EmgInstrFlags.Inherited },
        };

        /// <summary>给内核铺一份涌现状态（FgReadingMatrixSelfCheck G / H 段也用）：程序 1 行、热更注入 2 条事件、残骸 1 个、<paramref name="unitId"/> 的单位段。返回程序下标。</summary>
        public static int Seed(CombatKernel k, int unitId)
        {
            int p = k.AddProgram(SampleProgram(1f), SampleNodes(), SampleInstrs());
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Uplink, EntKind = EmgEntKind.Unit, Ent = unitId, Prog = -1, Pos = new double2(1, 2) });
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Timer, Depth = 1, EntKind = EmgEntKind.Projectile, Ent = 1, Prog = p, Node = 1, Value = 0.5f, Code = 308, Pos = new double2(-3, 5) });
            k.TryAddWreck(new EmgWreck { Pos = new double2(3, 4), Radius = 0.9f, Hp = 40f, MaxHp = 50f, Mass = 6f, Profile = 2, Born = k.Time, Until = k.Time + 90, SrcKind = (byte)CombatUnitKind.Enemy, SrcExtKey = 7, Coat = 0x3 }, out _);
            k.SetUnitEmg(unitId, 3, new EmgUnitState { PrevMatterMask = 0x5, LastHitBy = 2, LastHitChain = 1 });
            return p;
        }

        // ── A. 结构布局 ─────────────────────────────────────────────────────────

        private static void CheckLayout()
        {
            Line("  · A. 结构布局（FGR-EMG-061 / ADR ② ④ ⑦ ⑬）");
            int ev = UnsafeUtility.SizeOf<EmgEvent>(), pg = UnsafeUtility.SizeOf<EmgProgram>(), nd = UnsafeUtility.SizeOf<EmgNode>(), ins = UnsafeUtility.SizeOf<EmgInstr>();
            Expect(ev == 56 && pg == 16 && nd == 16 && ins == 28, $"事件记录 {ev} 字节（ADR ④ 定 56）；程序 / 节点 / 指令 {pg} / {nd} / {ins} 字节（ADR ② 定 16 / 16 / 28）");

            int proj = UnsafeUtility.SizeOf<CombatProjectile>();
            int inc = proj - 64; // 格式 15 的 CombatProjectile = 64 字节
            int cap = CombatConfig.Default.ProjectileCapacity;
            long pool = (long)inc * cap;
            Expect(inc > 0 && inc <= 52 && cap == 4096 && pool <= 52L * 4096,
                $"弹体结构 {proj} 字节（+{inc}，含 Id / 程序 / 节点 / 代 / 剩余穿透 / 剩余反弹 / 链深 / 沾染 / 质量 / 链 / 触发计数 / 寻的目标 / 物质掩码）；弹体池按容量 {cap} 预分配，满池增量 {pool / 1024.0:F0} KB ≤ ADR ⑬ 估算（约 +52 字节 / 发 ≈ 208 KB）");
            int unit = UnsafeUtility.SizeOf<EmgUnitState>() + sizeof(short);
            int wreck = UnsafeUtility.SizeOf<EmgWreck>();
            Expect(unit <= 92 && wreck * EmgConst.DefaultWreckCapacity <= 30 * 1024,
                $"每单位涌现段 {unit} 字节（基本段 + 档案下标；ADR ⑬ 全量约 92 字节 / 单位，阶段 2 起追加）；残骸 {wreck} 字节 × {EmgConst.DefaultWreckCapacity} = {wreck * EmgConst.DefaultWreckCapacity / 1024.0:F1} KB ≤ 30 KB");

            EmgCompiled c = EmgCatalog.Current;
            var bad = new List<string>();
            for (int code = 1; code <= EmgConst.EventKinds; code++)
            {
                string want = "evt." + ((EmgEventKind)code).ToString().ToLowerInvariant();
                if (!c.PortIdByCode.TryGetValue((ushort)(EmgConst.EventPortBase + code), out string id) || id != want)
                {
                    bad.Add($"{code}→{id ?? "无"}（应为 {want}）");
                }
            }
            int evtPorts = c.PortIdByCode.Values.Count(v => v.StartsWith("evt.", StringComparison.Ordinal));
            int kinds = Enum.GetValues(typeof(EmgEventKind)).Length - 1;
            Expect(bad.Count == 0 && evtPorts == EmgConst.EventKinds && kinds == EmgConst.EventKinds,
                $"内核事件枚举 {kinds} 条 = 端口表 evt.* {evtPorts} 条，码 = 端口码 − 300 逐条对上（FGR-EMG-060）{(bad.Count > 0 ? "；不符：" + string.Join("、", bad) : string.Empty)}");
        }

        // ── B. 程序表为空时逐字不变 ─────────────────────────────────────────────

        private static void CheckGolden()
        {
            Line("  · B. 程序表为空时行为与状态哈希逐字不变（对 M10 基线代码实测的金标准）");
            using CombatKernel k = GoldenKernel(out int tw);
            StepN(k, 600);
            ulong h = k.StateHash();
            ulong f15 = Fnv64(k.SerializeFormatForTests(15));
            ulong f14 = Fnv64(k.SerializeFormatForTests(14));
            CombatCounters cn = k.Counters;
            Expect(h == GoldenHash && f15 == GoldenF15 && f14 == GoldenF14 && cn.ProjectilesSpawned == GoldenProjectiles && cn.DronesLaunched == GoldenDrones,
                $"同一场景 600 步：状态哈希 {h:X16}（基线 {GoldenHash:X16}）、格式 15 快照字节哈希 {f15:X16}（基线 {GoldenF15:X16}）、格式 14 {f14:X16}（基线 {GoldenF14:X16}）、" +
                $"弹体 {cn.ProjectilesSpawned} 发 / 无人机 {cn.DronesLaunched} 架（基线 {GoldenProjectiles} / {GoldenDrones}）——结算、存档字节、哈希都与改动前相同");

            var ids = new HashSet<int>();
            bool projOk = true;
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                k.TryGetProjectile(i, out CombatProjectile p);
                projOk &= p.Prog == -1 && p.Chain == 0 && p.Gen == 0 && p.Id > 0 && ids.Add(p.Id);
            }
            var dids = new HashSet<int>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                k.TryGetDrone(i, out CombatDrone d);
                projOk &= d.Id > 0 && dids.Add(d.Id);
            }
            EmgScalars es = k.EmgScalars;
            Expect(projOk && k.ProjectileCount > 0 && es.NextProjId == GoldenProjectiles + 1 && es.NextDroneId == GoldenDrones + 1 && es.NextChain == 1
                   && k.ProgramCount == 0 && k.EmgBusPending == 0 && k.WreckCount == 0,
                $"发号只是簿记：在飞 {k.ProjectileCount} 发弹体 Id 唯一、Prog = −1、0 代、无链；下一个弹体 / 无人机号 {es.NextProjId} / {es.NextDroneId}（= 已发数 + 1），链号未动（{es.NextChain}）");

            k.TryGetWeapon(tw, out CombatWeapon w);
            CombatWeapon explicitNone = w;
            explicitNone.Program = -1;
            Expect(w.Program == -1 && w.ProgramRef == 0 && CombatKernel.WeaponKey(w) == CombatKernel.WeaponKey(explicitNone),
                "没有程序的武器：结构默认值即 Program = −1，去重键与显式写 −1 相同（格式 15 的键不变，空程序表下武器表去重不分叉）");
        }

        // ── C. 结构往返（FGT-EMG-040）────────────────────────────────────────────

        private static void CheckRoundTrip()
        {
            Line("  · C. 结构往返（FGT-EMG-040）：程序表 / 带程序的武器与弹体 / 总线 / 残骸 / 单位段");
            using CombatKernel k = GoldenKernel(out int tw);
            StepN(k, 300);
            int p = k.AddProgram(SampleProgram(1f), SampleNodes(), SampleInstrs());
            int pSame = k.AddProgram(SampleProgram(1f), SampleNodes(), SampleInstrs());
            int pOther = k.AddProgram(SampleProgram(2f), SampleNodes(), SampleInstrs());
            Expect(p == 0 && pSame == p && pOther == 1 && k.ProgramCount == 2 && k.EmgNodeCount == 4 && k.EmgInstrCount == 6,
                $"程序按完整字节去重、只追加：同一程序第二次登记返回原下标 {pSame}，热量不同的程序追加为 {pOther}；表 {k.ProgramCount} 行 / 节点 {k.EmgNodeCount} / 指令 {k.EmgInstrCount}");

            k.TryGetWeapon(tw, out CombatWeapon tws);
            CombatWeapon gw = tws;
            gw.Program = p;
            int wi = k.AddWeapon(gw);
            int gun = SpawnTurret(k, new double2(10, 0), wi);
            Expect(CombatKernel.WeaponKey(gw) != CombatKernel.WeaponKey(tws) && k.TryGetWeapon(wi, out CombatWeapon back) && back.Program == p,
                "只差程序下标的两把武器去重键不同（各占一行），武器表里读回的程序下标不变");
            StepN(k, 120);
            var chains = new HashSet<int>();
            int progShots = 0;
            bool hostOk = true;
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                k.TryGetProjectile(i, out CombatProjectile pr);
                if (pr.Weapon == wi)
                {
                    progShots++;
                    hostOk &= pr.Prog == p && pr.Node == 0 && pr.Gen == 0 && pr.Chain > 0 && chains.Add(pr.Chain);
                }
                else
                {
                    hostOk &= pr.Prog == -1 && pr.Chain == 0;
                }
            }
            Expect(progShots > 0 && hostOk,
                $"带程序的炮塔发出的 {progShots} 发在飞弹体：Prog = {p}、根节点、0 代、各开一条新因果链（{chains.Count} 条互不相同）；没有程序的弹体 Prog = −1、无链");

            int seeded = Seed(k, gun);
            byte[] snap = k.Serialize();
            using var b = new CombatKernel(k.Config, 64);
            CombatLoadResult lr = b.Load(snap);
            bool same = lr == CombatLoadResult.Ok && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion == 16
                        && b.StateHash() == k.StateHash() && b.EmgStateHash() == k.EmgStateHash() && b.ProgramCount == k.ProgramCount
                        && b.EmgBusPending == 2 && b.WreckCount == 1 && b.TryGetUnitEmg(gun, out short prof, out EmgUnitState us) && prof == 3 && us.LastHitBy == 2
                        && b.TryGetEmgEvent(1, out EmgEvent e1) && k.TryGetEmgEvent(1, out EmgEvent e0) && e1.Seq == e0.Seq && e1.Chain == e0.Chain && e1.Kind == EmgEventKind.Timer
                        && b.TryGetWreck(0, out EmgWreck wk) && wk.Id == 1 && wk.SrcExtKey == 7
                        && Enumerable.Range(0, k.WeaponCount).All(i => k.TryGetWeapon(i, out CombatWeapon x) && b.TryGetWeapon(i, out CombatWeapon y) && CombatKernel.WeaponKey(x) == CombatKernel.WeaponKey(y));
            Expect(seeded == p && same,
                $"格式 {CombatKernel.PeekFormat(snap)} 快照读回：状态哈希、涌现哈希逐位一致；程序 {b.ProgramCount} 行、武器程序下标、事件 2 条（Seq / 链不变）、残骸 1 个、单位段都在");

            StepN(k, 120);
            StepN(b, 120);
            int again = b.AddProgram(SampleProgram(1f), SampleNodes(), SampleInstrs());
            Expect(b.StateHash() == k.StateHash() && b.EmgStateHash() == k.EmgStateHash() && b.EmgScalars.NextProjId == k.EmgScalars.NextProjId
                   && b.EmgScalars.NextChain == k.EmgScalars.NextChain && again == p && b.ProgramCount == 2,
                $"读档后续跑 2 秒与不存档连续跑一致（下一个弹体号 {b.EmgScalars.NextProjId}、链号 {b.EmgScalars.NextChain} 接着发）；读档后同一程序再登记仍返回 {again}（去重索引按表重建，在飞载荷的下标不变）");

            // EMG1-BUS-01 起总线每步排空：再注入一条未处理事件，让下面“总线序号由最后一条事件 + 1 推出”的短段口径仍有事件可推。
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Uplink, EntKind = EmgEntKind.Unit, Ent = gun, Prog = -1, Pos = new double2(1, 2) });
            // 短段：本阶段较早提交写出的记录少几个字段。
            byte[] shortUnits = k.SerializeEmgShapeForTests(EmgConst.SegUnits, 1, 0);
            byte[] shortWrecks = k.SerializeEmgShapeForTests(EmgConst.SegWrecks, 12, 0);
            byte[] shortScalars = k.SerializeEmgShapeForTests(EmgConst.SegScalars, 1, 0);
            using var s1 = new CombatKernel(k.Config, 64);
            using var s2 = new CombatKernel(k.Config, 64);
            using var s3 = new CombatKernel(k.Config, 64);
            CombatLoadResult r1 = s1.Load(shortUnits), r2 = s2.Load(shortWrecks), r3 = s3.Load(shortScalars);
            bool u1 = r1 == CombatLoadResult.Ok && s1.TryGetUnitEmg(gun, out short p1, out EmgUnitState st1) && p1 == 3 && st1.PrevMatterMask == 0 && st1.LastHitBy == 0;
            bool w2 = r2 == CombatLoadResult.Ok && s2.TryGetWreck(0, out EmgWreck w2k) && w2k.Id == 1 && w2k.Hp == 40f && w2k.SrcExtKey == 0 && w2k.Profile == -1 && w2k.Coat == 0;
            int maxDrone = 0;
            for (int i = 0; i < s3.DroneCount; i++)
            {
                s3.TryGetDrone(i, out CombatDrone dr);
                maxDrone = Math.Max(maxDrone, dr.Id);
            }
            bool sc3 = r3 == CombatLoadResult.Ok && s3.EmgScalars.NextProjId == k.EmgScalars.NextProjId && s3.EmgScalars.NextDroneId > maxDrone
                       && s3.EmgScalars.NextChain >= 1 && s3.EmgScalars.EmgSeq == k.EmgScalars.EmgSeq && s3.StateHash() != 0;
            Expect(u1 && w2 && sc3,
                $"短段（较早提交写出）：单位段只有档案下标 → 读回档案 3、其余取缺省（{r1}）；残骸段只到 Id → Hp / Id 保留，外部键 0、档案 −1、沾染 0（{r2}）；发号器只有弹体号 → 其余由已用最大号 + 1 补齐、总线序号由最后一条事件 + 1 推出（{r3}）");

            // 长段 + 新段：较晚提交写出的记录多了尾部字段、多了一个不认识的段。
            byte[] longer = k.SerializeEmgShapeForTests(0, int.MaxValue, 4);
            using var l1 = new CombatKernel(k.Config, 64);
            CombatLoadResult rl = l1.Load(longer);
            Expect(rl == CombatLoadResult.Ok && l1.StateHash() == k.StateHash() && l1.EmgStateHash() == k.EmgStateHash() && longer.Length > snap.Length,
                $"长段（每条记录多 4 字节、字段数 +1）与不认识的新段：按段长度跳过未知部分，读回与正常快照逐位一致（{rl}，快照 {longer.Length} / {snap.Length} 字节）");
        }

        // ── D. 旧快照格式 14 / 15 ───────────────────────────────────────────────

        private static void CheckOldFormats()
        {
            Line("  · D. 旧快照格式 14 / 15 读入（ADR ⑩ 缺省：旧弹体 Prog = −1、0 代、按读入顺序补发 Id，队列空、无残骸）");
            using CombatKernel k = GoldenKernel(out int tw);
            StepN(k, 400);
            ulong plain = k.StateHash();
            byte[] plain15 = k.SerializeFormatForTests(15);
            k.TryGetWeapon(tw, out CombatWeapon tws);
            CombatWeapon gw = tws;
            Seed(k, 1);
            gw.Program = 0;
            SpawnTurret(k, new double2(10, 0), k.AddWeapon(gw));
            StepN(k, 60);
            foreach (int fmt in new[] { 15, 14 })
            {
                byte[] bytes = k.SerializeFormatForTests(fmt);
                using var c = new CombatKernel(k.Config, 64);
                CombatLoadResult r = c.Load(bytes);
                bool ok = r == CombatLoadResult.Ok && CombatKernel.PeekFormat(bytes) == fmt && c.ProgramCount == 0 && c.EmgBusPending == 0 && c.WreckCount == 0
                          && c.SlotCount == k.SlotCount && c.ProjectileCount == k.ProjectileCount;
                for (int i = 0; ok && i < c.WeaponCount; i++)
                {
                    ok &= c.TryGetWeapon(i, out CombatWeapon w) && w.Program == -1 && w.SpawnFlight == 0;
                }
                for (int i = 0; ok && i < c.ProjectileCount; i++)
                {
                    ok &= c.TryGetProjectile(i, out CombatProjectile p) && p.Prog == -1 && p.Gen == 0 && p.Chain == 0 && p.Id == i + 1;
                }
                for (int i = 0; ok && i < c.DroneCount; i++)
                {
                    ok &= c.TryGetDrone(i, out CombatDrone d) && d.Id == i + 1;
                }
                ok &= c.TryGetUnitEmg(1, out short prof, out EmgUnitState st) && prof == -1 && st.LastHitBy == 0;
                EmgScalars es = c.EmgScalars;
                ok &= es.NextProjId == c.ProjectileCount + 1 && es.NextDroneId == c.DroneCount + 1 && es.NextChain == 1 && es.EmgSeq == 0;
                StepN(c, 90);
                var ids = new HashSet<int>();
                bool fresh = true;
                for (int i = 0; i < c.ProjectileCount; i++)
                {
                    c.TryGetProjectile(i, out CombatProjectile p);
                    fresh &= p.Id > 0 && ids.Add(p.Id);
                }
                Expect(ok && fresh && c.Counters.ProjectilesSpawned > 0,
                    $"格式 {fmt}（{r}）：单位 {c.SlotCount} / 在飞弹体 {k.ProjectileCount} 照读；武器无程序、弹体 Prog = −1 / 0 代 / 按读入顺序补发 Id 1～{k.ProjectileCount}、无人机同理、单位档案 −1；" +
                    $"程序表、总线、残骸为空；续跑 1.5 秒新弹体接着发号不冲突（在飞 {c.ProjectileCount} 发 Id 唯一）");
            }
            using var g = new CombatKernel(k.Config, 64);
            CombatLoadResult rg = g.Load(plain15);
            Expect(rg == CombatLoadResult.Ok && g.StateHash() == plain,
                $"没有涌现状态时，格式 15 旧快照读回的状态哈希 = 原内核（{plain:X16}；补发的 Id 只是簿记，不改结算）");
        }

        // ── E. 回滚（ADR T6）──────────────────────────────────────────────────

        private static void CheckRollback()
        {
            Line("  · E. 回滚（ADR ⑩ / ⑮、T6）：新快照被只认旧格式的代码读到");
            using CombatKernel k = GoldenKernel(out _);
            StepN(k, 200);
            Seed(k, 1);
            byte[] future = k.Serialize();
            WriteInt(future, 4, CombatConst.FormatVersion + 1); // 以“本代码 = 旧代码、快照来自下一版”模拟：同一条 UnknownFormat 分支
            FixChecksum(future);
            using CombatKernel old = GoldenKernel(out _);
            StepN(old, 30);
            ulong before = old.StateHash();
            CombatLoadResult r = old.Load(future);
            Expect(r == CombatLoadResult.UnknownFormat && old.StateHash() == before && CombatKernel.PeekFormat(future) == CombatConst.FormatVersion + 1,
                $"更新格式的快照：{r}，整块不读、内核保持原样（哈希不变），格式号仍可读出（{CombatKernel.PeekFormat(future)}）；" +
                "地点层 CombatSites.Open → TryRestore 失败 → 通知“combat.load.rebuilt” → 按机器 / 敌人记录重建（在飞载荷、事件队列、残骸丢失；该分支由 FgCombatKernelSelfCheck.CheckCorruptSnapshotRebuild 实跑）");
        }

        // ── F. 负向 ───────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · F. 负向矩阵");
            using CombatKernel k = GoldenKernel(out int tw);
            StepN(k, 200);
            int p = Seed(k, 1);

            EmgNode[] Nodes(Action<EmgNode[]> m)
            {
                EmgNode[] n = SampleNodes();
                m(n);
                return n;
            }
            var bad = new (string Name, EmgNode[] Nodes, EmgInstr[] Instrs)[]
            {
                ("没有节点", Array.Empty<EmgNode>(), Array.Empty<EmgInstr>()),
                ("17 个节点", Enumerable.Range(0, 17).Select(j => new EmgNode { Parent = (sbyte)(j == 0 ? -1 : 0), Gen = (byte)(j == 0 ? 0 : 1) }).ToArray(), Array.Empty<EmgInstr>()),
                ($"{EmgConst.MaxInstr + 1} 条指令", new[] { new EmgNode { Parent = -1, InstrCount = EmgConst.MaxInstr + 1 } }, Enumerable.Repeat(new EmgInstr { Amount = 1f }, EmgConst.MaxInstr + 1).ToArray()),
                ("根节点有父", Nodes(n => n[0].Parent = 0), SampleInstrs()),
                ("父节点在后", Nodes(n => n[1].Parent = 1), SampleInstrs()),
                ("代号跳级", Nodes(n => n[1].Gen = 2), SampleInstrs()),
                ("第 3 代", new[]
                {
                    new EmgNode { Parent = -1, Gen = 0 }, new EmgNode { Parent = 0, Gen = 1 }, new EmgNode { Parent = 1, Gen = 2 }, new EmgNode { Parent = 2, Gen = 3 },
                }, Array.Empty<EmgInstr>()),
                ("事件码 30", Nodes(n => n[1].TrigEvent = (EmgEventKind)30), SampleInstrs()),
                ("指令段不铺满", Nodes(n => n[1].FirstInstr = 1), SampleInstrs()),
                ("指令幅度非数", SampleNodes(), SampleInstrs().Select((x, j) => { if (j == 1) x.Amount = float.NaN; return x; }).ToArray()),
            };
            int rows = k.ProgramCount;
            var leaked = bad.Where(b => k.AddProgram(SampleProgram(1f), b.Nodes, b.Instrs) != -1).Select(b => b.Name).ToList();
            Expect(leaked.Count == 0 && k.ProgramCount == rows && k.AddProgram(new EmgProgram { HeatPerShot = -1f }, SampleNodes(), SampleInstrs()) == -1,
                $"{bad.Length + 1} 种坏程序形状（没有节点 / 超 16 节点 / 超 {EmgConst.MaxInstr} 指令 / 根有父 / 父在后 / 代号跳级 / 第 3 代 / 事件码 30 / 指令段不铺满 / 非数 / 负热量）全部拒绝（−1），表不变{(leaked.Count > 0 ? "；漏放：" + string.Join("、", leaked) : string.Empty)}");

            using (var full = new CombatKernel(CombatConfig.Default, 8))
            {
                int last = -1;
                for (int i = 0; i < EmgConst.MaxPrograms; i++)
                {
                    last = full.AddProgram(SampleProgram(i), SampleNodes(), SampleInstrs());
                }
                int over = full.AddProgram(SampleProgram(EmgConst.MaxPrograms), SampleNodes(), SampleInstrs());
                int dup = full.AddProgram(SampleProgram(5), SampleNodes(), SampleInstrs());
                Expect(last == EmgConst.MaxPrograms - 1 && over == -1 && dup == 5 && full.ProgramCount == EmgConst.MaxPrograms,
                    $"程序表满（{EmgConst.MaxPrograms} 行）：新程序拒绝（{over}），已有的程序照常按键找回（{dup}）");
            }

            int pending = k.EmgBusPending;
            bool rejects = !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.None, Prog = -1 }) && !k.InjectEmgEvent(new EmgEvent { Kind = (EmgEventKind)30, Prog = -1 })
                           && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Depth = 4, Prog = -1 }) && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Prog = 7 })
                           && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Prog = -1, Value = float.NaN })
                           // 审查 P1 / P2：写入口曾比读档宽——链 −1、没发过的链、节点超出程序节点数，注入时就拒绝（否则存档后整份读不回 / 读档推高发号器）
                           && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Prog = -1, Chain = -1 }) && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Prog = -1, Chain = NeverIssuedChain })
                           && !k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Hit, Prog = p, Node = 200 }) && k.EmgBusPending == pending;
            using (var busy = new CombatKernel(CombatConfig.Default, 8))
            {
                int ok = 0;
                for (int i = 0; i < EmgConst.BusCapacity + 1; i++)
                {
                    ok += busy.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Pickup, Prog = -1, Ent = i }) ? 1 : 0;
                }
                busy.TryGetEmgEvent(EmgConst.BusCapacity - 1, out EmgEvent lastEv);
                Expect(rejects && ok == EmgConst.BusCapacity && busy.EmgBusPending == EmgConst.BusCapacity && lastEv.Seq == EmgConst.BusCapacity - 1 && lastEv.Chain == EmgConst.BusCapacity,
                    $"注入事件：码 0 / 30、链深 4、程序越界、非数、链 −1、没发过的链、节点 200（超出程序节点数）拒绝；队列满 {EmgConst.BusCapacity} 条后再注入拒绝（不丢已有的、Seq 连续、每条热更注入各开一条链）");
            }

            int wrecks = k.WreckCount;
            bool wbad = !k.TryAddWreck(new EmgWreck { Hp = 10f, MaxHp = 5f, Profile = -1 }, out _) && !k.TryAddWreck(new EmgWreck { Pos = new double2(double.NaN, 0), Profile = -1 }, out _)
                        && !k.TryAddWreck(new EmgWreck { Profile = -2 }, out _) && !k.TryAddWreck(new EmgWreck { Chain = NeverIssuedChain }, out _) && k.WreckCount == wrecks
                        && new EmgWreck().Profile == -1; // 结构默认即“种类默认”，ENT-01 生成残骸时漏写不会误指第 0 号档案
            using (var yard = new CombatKernel(CombatConfig.Default, 8))
            {
                int added = 0;
                for (int i = 0; i < EmgConst.DefaultWreckCapacity + 1; i++)
                {
                    added += yard.TryAddWreck(new EmgWreck { Pos = new double2(i, 0), Hp = 1f, MaxHp = 1f, Profile = -1 }, out _) ? 1 : 0;
                }
                yard.TryGetWreck(EmgConst.DefaultWreckCapacity - 1, out EmgWreck lastW);
                Expect(wbad && added == EmgConst.DefaultWreckCapacity && yard.WreckCount == EmgConst.DefaultWreckCapacity && lastW.Id == EmgConst.DefaultWreckCapacity,
                    $"残骸：耐久超上限 / 位置非数 / 档案 −2 / 没发过的链拒绝，结构默认档案为 −1；满 {EmgConst.DefaultWreckCapacity} 个后再登记拒绝（崩解为碎屑归 EMG1-ENT-01），稳定 Id 1～{EmgConst.DefaultWreckCapacity}");
            }

            k.TryGetWeapon(tw, out CombatWeapon w);
            CombatWeapon dangling = w;
            dangling.Program = k.ProgramCount + 3;
            int weapons = k.WeaponCount;
            bool threw = false;
            try
            {
                k.AddWeapon(dangling);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }
            CombatWeapon flies = w;
            flies.SpawnFlight = 2;
            bool threwFlight = false;
            try
            {
                k.AddWeapon(flies);
            }
            catch (ArgumentOutOfRangeException)
            {
                threwFlight = true;
            }
            bool unitRejects = !k.SetUnitEmg(999999, 1, default) && !k.SetUnitEmg(1, -2, default) && !k.SetUnitEmg(1, -1, new EmgUnitState { LastHitBy = -1 })
                               && !k.SetUnitEmg(1, -1, new EmgUnitState { LastHitChain = -1 }) && !k.SetUnitEmg(1, -1, new EmgUnitState { LastHitChain = NeverIssuedChain })
                               && k.SetUnitEmg(1, 3, new EmgUnitState { PrevMatterMask = 0x5, LastHitBy = 2, LastHitChain = 1 }); // 对照：单位 1 存在、Seed 写过的合法值照收（状态不变），上面的拒绝不是因为单位不存在
            Expect(threw && threwFlight && k.WeaponCount == weapons && unitRejects,
                "武器指向不存在的程序 / SpawnFlight 2：AddWeapon 直接报错（不悄悄改成“没有程序”）、武器表不变；不存在的单位 / 档案 −2 / LastHitBy −1 / LastHitChain −1 / 没发过的链写单位段拒绝");

            // 快照里的坏值：按段头定位记录、改值、重算校验和 → 整份拒绝，内核原样。
            CombatWeapon gw = w;
            gw.Program = p;
            gw.SpawnFlight = 1; // 边界值：写入口收下的，末尾的完好快照必须原样读回
            k.AddWeapon(gw);
            StepN(k, 30);
            // EMG1-BUS-01 起总线每步排空：存档前补 2 条未处理事件（其中 1 条带程序），坏值用例才有事件记录可改。
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Uplink, EntKind = EmgEntKind.Unit, Ent = 1, Prog = -1, Pos = new double2(1, 2) });
            k.InjectEmgEvent(new EmgEvent { Kind = EmgEventKind.Timer, Depth = 1, EntKind = EmgEntKind.Projectile, Ent = 1, Prog = p, Node = 1, Value = 0.5f, Code = 308, Pos = new double2(-3, 5) });
            byte[] snap = k.Serialize();
            var cases = new List<(string Name, byte[] Data, CombatLoadResult Want)>();
            (int at, int stride) wseg = FindSegment(snap, EmgConst.SegWeapons, 5, k.WeaponCount); // EMG1-FLASH-01：2 → 5 个字段（尾部 +3 刷写表头）
            cases.Add(("武器程序下标越界", Patch(snap, d => WriteInt(d, wseg.at + (k.WeaponCount - 1) * wseg.stride, 99)), CombatLoadResult.InvalidValue));
            cases.Add(("武器程序下标 −5", Patch(snap, d => WriteInt(d, wseg.at + (k.WeaponCount - 1) * wseg.stride, -5)), CombatLoadResult.InvalidValue));
            (int at, int stride) bseg = FindSegment(snap, EmgConst.SegBus, 16, k.EmgBusPending);
            cases.Add(("事件码 31", Patch(snap, d => d[bseg.at] = 31), CombatLoadResult.InvalidValue));
            cases.Add(("事件链深 4", Patch(snap, d => d[bseg.at + 1] = 4), CombatLoadResult.InvalidValue));
            cases.Add(("事件链 −1", Patch(snap, d => WriteInt(d, bseg.at + 20, -1)), CombatLoadResult.InvalidValue));
            int progEv = Enumerable.Range(0, k.EmgBusPending).Where(i => k.TryGetEmgEvent(i, out EmgEvent ev) && ev.Prog >= 0).DefaultIfEmpty(-1).First();
            if (progEv >= 0)
            {
                cases.Add(("事件节点 200", Patch(snap, d => d[bseg.at + progEv * bseg.stride + 28] = 200), CombatLoadResult.InvalidValue)); // 记录内偏移：码/深/种/种 4 + Code×2 4 + Ent/Other/Prog/Chain/Seq 20 = 28
            }
            (int at, int stride) kseg = FindSegment(snap, EmgConst.SegWrecks, 27, k.WreckCount); // EMG1-ENT-01：20 → 26 个字段（尾部 +6 滑行 / 推动）；EMG1-CHAIN-01：+1 击毁者
            cases.Add(("残骸耐久非数", Patch(snap, d => WriteInt(d, kseg.at + 44, BitConverter.SingleToInt32Bits(float.NaN))), CombatLoadResult.InvalidValue));
            if (k.ProjectileCount >= 2)
            {
                (int at, int stride) pseg = FindSegment(snap, EmgConst.SegProjectiles, 20, k.ProjectileCount); // EMG1-GRM-01：16 → 20 个字段
                cases.Add(("弹体 Id 重复", Patch(snap, d => WriteInt(d, pseg.at + pseg.stride, BitConverter.ToInt32(d, pseg.at))), CombatLoadResult.InvalidValue));
                cases.Add(("弹体程序下标越界", Patch(snap, d => WriteInt(d, pseg.at + 4, 50)), CombatLoadResult.InvalidValue));
            }
            (int at, int stride) nseg = FindSegment(snap, EmgConst.SegNodes, 10, k.EmgNodeCount);
            cases.Add(("节点代号 3", Patch(snap, d => d[nseg.at + nseg.stride + 1] = 3), CombatLoadResult.InvalidValue));
            cases.Add(("段长度越过快照结尾", Patch(snap, d => WriteInt(d, kseg.at - 14 + 4, 0x3FFFFFF0)), CombatLoadResult.Truncated));
            var wrong = new List<string>();
            using (var target = GoldenKernel(out _))
            {
                StepN(target, 10);
                ulong h0 = target.StateHash();
                foreach ((string name, byte[] data, CombatLoadResult want) in cases)
                {
                    CombatLoadResult got = target.Load(data);
                    if (got != want || target.StateHash() != h0)
                    {
                        wrong.Add($"{name}→{got}");
                    }
                }
                CombatLoadResult good = target.Load(snap);
                Expect(wrong.Count == 0 && wseg.at > 0 && bseg.at > 0 && kseg.at > 0 && nseg.at > 0 && progEv >= 0 && good == CombatLoadResult.Ok && target.StateHash() == k.StateHash(),
                    $"快照坏值 {cases.Count} 种（{string.Join(" / ", cases.Select(c => c.Name))}）：整份拒绝、内核保持原样；同一份完好快照照常读回{(wrong.Count > 0 ? "；不符：" + string.Join("、", wrong) : string.Empty)}");
            }
        }

        // ── G. 确定性（FGT-EMG-042 内核层）────────────────────────────────────────

        private static void CheckDeterminism()
        {
            Line("  · G. 确定性（FGT-EMG-042 内核层；倍速 / 暂停 / 观察的世界层在 FgReadingMatrixSelfCheck H 段）");
            using CombatKernel k = GoldenKernel(out int tw);
            StepN(k, 150);
            k.TryGetWeapon(tw, out CombatWeapon w);
            int p = Seed(k, 2);
            w.Program = p;
            SpawnTurret(k, new double2(-12, 0), k.AddWeapon(w));
            byte[] snap = k.Serialize();
            using var cont = new CombatKernel(k.Config, 64);
            using var split = new CombatKernel(k.Config, 64);
            cont.Load(snap);
            split.Load(snap);
            StepN(cont, 240);
            StepN(split, 120);
            byte[] mid = split.Serialize();
            using var resumed = new CombatKernel(k.Config, 64);
            CombatLoadResult r = resumed.Load(mid);
            StepN(resumed, 120);
            Expect(r == CombatLoadResult.Ok && resumed.StateHash() == cont.StateHash() && resumed.EmgStateHash() == cont.EmgStateHash() && resumed.EmgScalars.NextChain > 1,
                $"带程序与涌现状态的场景：连续跑 4 秒 = 跑 2 秒存读档再跑 2 秒（状态哈希 {cont.StateHash():X16}，涌现哈希一致，链号 {resumed.EmgScalars.NextChain}）；内核单线程 IJob，不存在“线程数”差异");

            var fwd = new float[1000];
            var rev = new float[1000];
            for (int i = 0; i < 1000; i++)
            {
                fwd[i] = EmgRng.Unit((uint)i, 7u, 3u, 12345u);
            }
            for (int i = 999; i >= 0; i--)
            {
                rev[i] = EmgRng.Unit((uint)i, 7u, 3u, 12345u);
            }
            float mean = fwd.Average();
            bool inRange = fwd.All(v => v >= 0f && v < 1f);
            bool saltDiffers = Enumerable.Range(0, 100).Count(i => EmgRng.Unit((uint)i, 7u, 3u, 12345u) != EmgRng.Unit((uint)i, 7u, 4u, 12345u)) >= 99;
            Expect(fwd.SequenceEqual(rev) && inRange && mean > 0.45f && mean < 0.55f && saltDiffers,
                $"内核随机 = 无状态哈希（FGR-EMG-213 / ADR T10：Sim/Combat 里没有随机数流可沿用）：正序 / 倒序求值结果相同、落在 [0, 1)、均值 {mean:F3}、换用途盐值结果不同");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        /// <summary>按段头（段标记、字段数、记录数，且段尾紧跟下一个段标记）在快照里找 Emg 段，返回第一条记录的位置与记录长度。</summary>
        internal static (int at, int stride) FindSegment(byte[] d, int tag, int fields, int count)
        {
            for (int i = 0; i + 14 <= d.Length - 4; i++)
            {
                if (BitConverter.ToInt32(d, i) != tag || BitConverter.ToUInt16(d, i + 8) != fields || BitConverter.ToInt32(d, i + 10) != count)
                {
                    continue;
                }
                int len = BitConverter.ToInt32(d, i + 4);
                int end = i + 8 + len;
                if (len < 6 || end > d.Length - 4 || (count > 0 && (len - 6) % count != 0))
                {
                    continue;
                }
                if (tag < EmgConst.SegWrecks && (end + 4 > d.Length || BitConverter.ToInt32(d, end) != tag + 1))
                {
                    continue;
                }
                return (i + 14, count > 0 ? (len - 6) / count : 0);
            }
            return (-1, 0);
        }

        internal static byte[] Patch(byte[] src, Action<byte[]> edit)
        {
            byte[] d = (byte[])src.Clone();
            edit(d);
            FixChecksum(d);
            return d;
        }

        internal static void WriteInt(byte[] d, int at, int v)
        {
            byte[] b = BitConverter.GetBytes(v);
            Buffer.BlockCopy(b, 0, d, at, 4);
        }

        /// <summary>重算快照末尾的 FNV-1a 32 校验和（与 CombatKernel.Serialize 同一算法），把坏值逼到数值校验这一层。</summary>
        private static void FixChecksum(byte[] d)
        {
            int body = d.Length - 4;
            uint h = 2166136261u;
            for (int i = 0; i < body; i++)
            {
                h ^= d[i];
                h *= 16777619u;
            }
            WriteInt(d, body, unchecked((int)h));
        }

        private static ulong Fnv64(byte[] b)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte x in b)
            {
                h ^= x;
                h *= 1099511628211UL;
            }
            return h;
        }

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
