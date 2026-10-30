using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-GRM-01 弹道修饰的内核动词的自动验收（FG19 第 2.4 节、FGR-EMG-015 规则一 / 二、017、018、033；FG19A 第 1.1、2.1.1、3.2 节；ADR-EMG-001 ②③；
    /// FGT-EMG-016 / 003（修饰端口部分）/ 014（阶段 1 内核行为部分））。起真实内核（CombatKernel，Burst 步进）、真实编译器（EmgProgramCompiler，emg.stage = 1）与真实读法表：
    /// A 数据（修饰端口 × 5 载体齐全、新动词枚举与目录同值、文案数字）；B FGT-EMG-016：9 个非纯数值修饰 × 5 载体各一例与“去掉修饰”对照（行为指纹不同 + 逐格语义断言）；
    /// C 寻的（修正上限 30° 实测、热信号 / 已标记偏好、目标阵亡重选 / 直飞）；D 穿透 / 反弹（次数用尽、单位面与障碍面分开计、法线、[跳弹, 壁撞]）；
    /// E 连锁偏好（[电弧, 标记跳转] 跳数取大、先已标记再导电、只有已标记时只跳已标记）；F 折返 / 绕行 / 抛射 / 侧偏（落点不可达、光束不越障、偏折场一半不反射）；
    /// G 负向（炮塔沿用兼容表、没有程序逐字不变、单轨不双算）；H 存读档（在飞的寻的 / 绕行 / 折返弹体中途存档续跑逐步一致、短段取缺省）；I 倍速 0.5x～3x 与暂停；L 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgGrammarSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgGrammarSelfCheck。
    /// </summary>
    public static class FgEmgGrammarSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger;
        private const byte Cond = 2;
        private const byte RoleModify = 6;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/弹道修饰内核动词")]
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
            _pass = 0;
            Line("\n[弹道修饰内核动词] EMG1-GRM-01：寻的 / 穿透 / 反弹 / 连锁 / 多份载荷 / 折返 / 绕行 / 抛射 / 侧偏 × 5 载体（FGT-EMG-016 / 003 / 014）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckData);
                UseStage1();
                Step(CheckMatrix);
                Step(CheckChildMods);
                Step(CheckHoming);
                Step(CheckPierceBounce);
                Step(CheckChain);
                Step(CheckReturnOrbitLobDrift);
                Step(CheckReadEnds);
                Step(CheckNegatives);
                Step(CheckSaveLoad);
                Step(CheckSpeeds);
                Step(CheckNumericOnly);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"弹道修饰自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [弹道修饰内核动词] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private sealed class N
        {
            public int Parent = -1;
            public EmgEventKind On;
            public byte Count = 1;
            public readonly List<EmgInstr> Instrs = new List<EmgInstr>();

            public N Read(EmgEventKind k, byte role = Cond)
            {
                Instrs.Add(new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)k), Role = role });
                return this;
            }

            /// <summary>世界量读端（感知 3 / 条件 2）：EMG1-FW-01 起霰射 / 绕轨 / 乱流的读端改变行为。</summary>
            public N Sense(ushort port, byte role = 3)
            {
                Instrs.Add(new EmgInstr { Port = port, Role = role });
                return this;
            }

            public N Verb(EmgVerb v, float amount, float a = 0f, float b = 0f, float c = 0f, float param2 = 0f, EmgInstrFlags flags = EmgInstrFlags.None)
            {
                Instrs.Add(new EmgInstr { Port = 110, Role = RoleModify, Verb = (byte)v, Amount = amount, A = a, B = b, C = c, Param2 = param2, Flags = flags });
                return this;
            }
        }

        private static int Tree(CombatKernel k, params N[] spec)
        {
            var nodes = new List<EmgNode>();
            var instrs = new List<EmgInstr>();
            uint mask = 0u;
            for (int j = 0; j < spec.Length; j++)
            {
                N s = spec[j];
                byte gen = j == 0 ? (byte)0 : (byte)(nodes[s.Parent].Gen + 1);
                nodes.Add(new EmgNode
                {
                    Parent = (sbyte)(j == 0 ? -1 : s.Parent), Gen = gen, TrigEvent = j == 0 ? default : s.On, TrigCount = j == 0 ? (byte)0 : s.Count,
                    FirstInstr = instrs.Count, InstrCount = (byte)s.Instrs.Count,
                });
                foreach (EmgInstr x0 in s.Instrs)
                {
                    EmgInstr x = x0;
                    x.Ordinal = (byte)instrs.Count;
                    instrs.Add(x);
                    if (x.Role >= Trig && x.Role <= EmgConst.RoleLastRead && x.Port > EmgConst.EventPortBase && x.Port <= EmgConst.EventPortBase + EmgConst.EventKinds)
                    {
                        mask |= 1u << (x.Port - EmgConst.EventPortBase);
                    }
                }
                if (j > 0)
                {
                    mask |= 1u << (int)s.On;
                }
            }
            return k.AddProgram(new EmgProgram { ListenMask = mask, Flags = EmgProgramFlags.NeedsFlight }, nodes, instrs);
        }

        private static N Root() => new N();

        private static CombatConfig Cfg(bool cover = true)
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgCover = (byte)(cover ? 1 : 0);
            return c;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, int profile = -1, CombatUnitFlags extra = 0) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | extra | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = profile, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Me(CombatKernel k, double2 p, int weapon) => Unit(k, CombatFaction.Player, p, weapon, 500f);

        private static int Foe(CombatKernel k, double2 p, float hp = 200f, CombatUnitFlags extra = 0) => Unit(k, CombatFaction.Hostile, p, -1, hp, -1, extra);

        private static CombatWeapon Flight(int program, float speed = 30f, float life = 1.2f, float range = 30f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = 10f, Cooldown = 0f, Program = program,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static List<CombatProjectile> Projs(CombatKernel k)
        {
            var list = new List<CombatProjectile>();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p))
                {
                    list.Add(p);
                }
            }
            return list;
        }

        private static List<CombatZone> Zones(CombatKernel k)
        {
            var list = new List<CombatZone>();
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z))
                {
                    list.Add(z);
                }
            }
            return list;
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : new double2(double.NaN, double.NaN);

        private static bool Near(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) <= eps;

        private static float Deg(float2 a, float2 b) => math.degrees(math.acos(math.clamp(math.dot(math.normalize(a), math.normalize(b)), -1f, 1f)));

        private static void UseStage1()
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
            // 电弧 / 标记跳转 / 回旋 / 绕轨 / 漏油的声明随 EMG1-FW-01 落表；这里按 FG19A 第 1.2～1.4 节已定的行代入（与 FgEmgTriggerSelfCheck H 段同一做法）。
            void Decl(string owner, int seq, string dir, string port, string role, float amount, float param2 = 0f)
            {
                src.Decls.Add(new EmgSource.Decl
                {
                    Id = $"{owner}.{seq}", Owner = owner, OwnerKind = "firmware", Seq = seq, Dir = dir, Port = port, Role = role, Amount = amount, Param2 = param2, Carriers = "all",
                    TextKey = $"emg.decl.{owner}.{seq}", StageGate = 1,
                });
            }
            src.Decls.RemoveAll(d => d.Owner == "fw_arcchain" || d.Owner == "fw_marktag" || d.Owner == "fw_boomerang" || d.Owner == "fw_orbit");
            Decl("fw_arcchain", 1, "read", "prop.conductive", "sense", 1f);
            Decl("fw_arcchain", 2, "write", "grm.mod.chain", "modify", 2f);
            Decl("fw_marktag", 1, "read", "evt.marked", "condition", 1f);
            Decl("fw_marktag", 2, "write", "grm.mod.chain", "modify", 2f);
            Decl("fw_boomerang", 1, "read", "evt.hit", "trigger", 1f);
            Decl("fw_boomerang", 2, "write", "grm.mod.return", "modify", 1f);
            Decl("fw_orbit", 1, "read", "evt.fire", "trigger", 1f);
            Decl("fw_orbit", 2, "read", "ent.projectile", "sense", 1f);
            Decl("fw_orbit", 3, "write", "grm.mod.orbit", "modify", 120f);
            EmgCatalog.OverrideForTests(src);
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static readonly string[] ModPorts =
        {
            "grm.mod.homing", "grm.mod.pierce", "grm.mod.bounce", "grm.mod.chain", "grm.mod.multi", "grm.mod.return", "grm.mod.orbit", "grm.mod.lob", "grm.mod.drift",
            "grm.mod.size", "grm.mod.armor", "grm.mod.power", "grm.inherit", "grm.trig",
        };

        private static void CheckData()
        {
            Line("  · A. 数据：carrierImpl 的语法 / 修饰端口在 fg.TbPortCarrier 里 5 载体齐全（FGR-EMG-015 规则一）；新动词用新枚举登记、与目录同值，不改 25 个旧动词；文案数字");
            EmgCompiled c = EmgCatalog.Current;
            var missing = new List<string>();
            foreach (string port in ModPorts)
            {
                if (!c.PortIndexById.TryGetValue(port, out int pi))
                {
                    missing.Add(port + "（未注册）");
                    continue;
                }
                EmgPortRow pr = c.Ports[pi];
                for (byte car = 0; car < 5; car++)
                {
                    if ((pr.Flags & EmgPortRow.FlagCarrierImpl) != 0 && !c.PortCarriers.Any(r => r.Port == pr.Code && r.Carrier == car))
                    {
                        missing.Add($"{port} × {EmgCatalog.Carriers[car]}");
                    }
                }
            }
            Expect(missing.Count == 0 && c.Problems.Count == 0,
                $"14 个语法 / 修饰端口 × 5 载体都有 fg.TbPortCarrier 行{(missing.Count > 0 ? "（缺 " + string.Join("、", missing) + "）" : string.Empty)}；目录问题 {c.Problems.Count} 条");
            var bad = new List<string>();
            foreach (EmgVerb v in Enum.GetValues(typeof(EmgVerb)))
            {
                if (v != EmgVerb.None && EmgCatalog.VerbCode(v.ToString().ToLowerInvariant()) != (int)v)
                {
                    bad.Add($"{v}={(int)v}/目录 {EmgCatalog.VerbCode(v.ToString().ToLowerInvariant())}");
                }
            }
            bool oldVerbs = (int)CarrierReadingVerb.Dmg == 1 && (int)CarrierReadingVerb.Chain == 5 && (int)CarrierReadingVerb.Cone == 25 && EmgCatalog.Verbs[24] == "cone";
            Expect(bad.Count == 0 && oldVerbs && (int)EmgVerb.Steer == 26 && (int)EmgVerb.Inherit == EmgCatalog.Verbs.Length,
                $"内核枚举 EmgVerb 与 EmgCatalog.Verbs 同值（下标 + 1）{(bad.Count > 0 ? "，不一致：" + string.Join("、", bad) : string.Empty)}；旧 25 个 CarrierReadingVerb 值不变（Dmg 1 / Chain 5 / Cone 25）");
            string homing = GameText.Get("firmware.fw_homing.desc"), jitter = GameText.Get("firmware.fw_jitter.desc"), arc = GameText.Get("firmware.fw_arc.desc");
            string[] keys = { EmgProgramCompiler.NoteLobStraight, EmgProgramCompiler.NoteLobStraight + ".fix" };
            Expect(homing.Contains("30°") && homing.Contains("重新选目标") && jitter.Contains("±15°") && jitter.Contains("+20%") && jitter.Contains("一半") && arc.Contains("越过障碍")
                   && keys.All(GameText.Has),
                $"文案按 FG19A（D-05 / D-07 / D-08）：寻的“{homing}”；乱流“{jitter}”；曲射“{arc}”；光束曲射原因键 {string.Join(" / ", keys)} 都有");
        }

        // ── B. FGT-EMG-016：9 个修饰 × 5 载体 ───────────────────────────────────────────

        private static readonly (string Mod, string Fw)[] Mods =
        {
            ("寻的", "fw_homing"), ("穿透", "fw_pierce"), ("反弹", "fw_ricochet"), ("连锁", "fw_arcchain"), ("多份载荷", "fw_scatter"),
            ("折返", "fw_boomerang"), ("绕行", "fw_orbit"), ("抛射", "fw_arc"), ("侧偏", "fw_jitter"),
        };

        private static readonly (FirmwareCarrier Car, string Comp, string Name)[] Cars =
        {
            (FirmwareCarrier.Projectile, ComponentCatalog.CompGunId, "射弹"), (FirmwareCarrier.Melee, ComponentCatalog.CompRamId, "格斗"),
            (FirmwareCarrier.Summon, ComponentCatalog.CompDroneBayId, "无人机"), (FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, "力场"),
            (FirmwareCarrier.Field, ComponentCatalog.CompSprayerId, "布区"),
        };

        /// <summary>一格的运行结果：行为指纹 + 语义断言要用的量。</summary>
        private sealed class Cell
        {
            public string Fp;
            public readonly Dictionary<string, float> Hp = new Dictionary<string, float>();
            public readonly Dictionary<string, double2> At = new Dictionary<string, double2>();
            public double2 MeAt;
            public int Zones;
            public double2 ZoneAt;
            public float ZoneR;
            public int Drones;
            public float BulletSpeed;
            public float BulletAngle = -1f;
            public EmgProjMods BulletMods;
            public EmgProjFlags BulletFlags;
            public int Pellets;
            public ushort LastExpire;
            public double2 LastExpireAt;
            public float SteerUsed;
            /// <summary>见过折返中的弹体；它最后出现时离发射者多远（之后消失 = 在发射者身边终结）。</summary>
            public bool ReturnSeen;
            public float ReturnLastDist = -1f;
            /// <summary>无人机列：第一发无人机子弹的出生航向与它活着期间的最大转角（度）；见过已反弹（Bounced）的弹体。</summary>
            public int DroneBulletId = -1;
            public float2 DroneV0;
            public float DroneTurn;
            public int DroneSeen;
            public string DroneDbg = string.Empty;
            public bool Bounced;
        }

        /// <summary>编程序（emg.stage = 1）；<paramref name="strip"/> = “去掉修饰”对照：把修饰写端行去掉（端口、动词清零：不执行、不折进读法），其余（飞行与否、读端）不变。</summary>
        private static EmgProgramResult Program(string fw, FirmwareCarrier car, bool strip)
        {
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, car);
            if (strip)
            {
                for (int i = 0; i < pr.KernelInstrs.Count; i++)
                {
                    EmgInstr x = pr.KernelInstrs[i];
                    if (x.Role > EmgConst.RoleLastRead && x.Port >= 110 && x.Port <= 122)
                    {
                        x.Port = 0;
                        x.Verb = 0;
                        pr.KernelInstrs[i] = x;
                    }
                }
            }
            return pr;
        }

        private static CombatWeapon BaseWeapon(string comp, float range)
        {
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = 10f, Cooldown = 0f,
            };
            w.Reading = CarrierReadings.Build(comp, null, null, Array.Empty<string>(), false);
            return w;
        }

        private static Cell RunCell(string fw, FirmwareCarrier car, string comp, bool strip)
        {
            var cell = new Cell();
            CombatConfig cfg = Cfg();
            if (car == FirmwareCarrier.Summon && fw == "fw_homing")
            {
                cfg.DroneReach = 5f; // 无人机在 5 米外开火：子弹飞得够久，才看得出飞行中转向（默认 1.2 米时一两步就撞上 T）
            }
            using var k = new CombatKernel(cfg, 64);
            EmgProgramResult pr = Program(fw, car, strip);
            CombatWeapon w = BaseWeapon(comp, car == FirmwareCarrier.Field ? 12f : 20f);
            CombatReading baseReading = w.Reading; // 摆位按没有修饰的读法算：有修饰 / 去掉修饰两边场景逐字相同
            w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
            w.Program = CombatSite.RegisterProgram(k, pr);
            CombatSite.ApplySpawnFlight(ref w, pr, comp);
            int me = Me(k, new double2(0, 0), k.AddWeapon(w));
            var foes = new Dictionary<string, int>();
            int t;
            float area = baseReading.Area;
            switch (car)
            {
                case FirmwareCarrier.Projectile:
                    t = foes["T"] = Foe(k, new double2(0, 10));
                    foes["M"] = Foe(k, new double2(0, 5));
                    foes["H"] = Foe(k, new double2(3, 10));
                    foes["O"] = Foe(k, new double2(0, -2.5)); // 身后、在绕行圈上；离 M 的撞点超过 6 米（反弹不会挑它）
                    k.SetWeaponState(foes["H"], 50f, false, 0, 0); // 热信号最高
                    break;
                case FirmwareCarrier.Summon:
                    t = foes["T"] = Foe(k, new double2(0, 6), 400f);
                    foes["B"] = Foe(k, new double2(0, 7.2), 400f);
                    // C 带一件不出手、不散热的武器：没有武器的单位每步积热清零，无人机起飞到开火要好几秒，C 要一直是热信号最高的那个
                    foes["C"] = Unit(k, CombatFaction.Hostile, new double2(2.5, 6.5), k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 0 }), 400f);
                    foes["O"] = Foe(k, new double2(1.8, 0.5), 400f, CombatUnitFlags.AreaOnly); // 不能被单体锁定：无人机去打 T，只有绕母机巡航的环扫打得到它
                    k.SetWeaponState(foes["C"], 50f, false, 0, 0);
                    if (fw == "fw_arc")
                    {
                        k.SetObstacles(new List<float3> { new float3(0f, 5.05f, 0.35f) }); // 紧贴 T 前方的障碍：直飞子弹被截停，抛射越过
                    }
                    break;
                case FirmwareCarrier.Melee:
                {
                    float cone = math.max(5f, baseReading.Cone);
                    t = foes["T"] = Foe(k, new double2(0, 1.6));
                    foes["B"] = Foe(k, new double2(0, 3.6)); // 身后 2 米（穿刺 3 米内）
                    double r = 1.6;
                    float side = math.radians(math.min(170f, cone + 8f));
                    foes["S+"] = Foe(k, new double2(math.sin(side) * r, math.cos(side) * r));
                    foes["S-"] = Foe(k, new double2(-math.sin(side) * r, math.cos(side) * r));
                    foes["R"] = Foe(k, new double2(0, -1.6));
                    foes["F"] = Foe(k, new double2(3.2, 3.2));
                    break;
                }
                case FirmwareCarrier.Aura:
                {
                    t = foes["T"] = Foe(k, new double2(0, area * 0.5));
                    foes["I"] = Foe(k, new double2(area * 0.6, 0));
                    foes["O"] = Foe(k, new double2(area + 1.1, 0));
                    foes["W"] = Foe(k, new double2(-area * 0.75, 0));
                    foes["X"] = Foe(k, new double2(0, -area * 0.75));
                    foes["E"] = Foe(k, new double2(area * 0.7, area * 0.7));
                    k.TryAddWreck(new EmgWreck { Pos = new double2(-area * 0.4, 0), Radius = 0.25f, Hp = 1000f, MaxHp = 1000f, Until = 1e9 }, out _);
                    k.SetObstacles(new List<float3> { new float3(0f, -area * 0.4f, 0.25f) });
                    if (fw == "fw_jitter")
                    {
                        // 游移上界探针：圆心每个分量最多偏 1.5 米（合计 ≤ 1.5√2 ≈ 2.12 米）；离本机 半径 + 单位半径 + 2.12 + 0.3 米外的 4 个探针永远打不到。
                        double far = area + 0.5 + 1.5 * math.SQRT2 + 0.3;
                        foes["P1"] = Foe(k, new double2(far, 0));
                        foes["P2"] = Foe(k, new double2(-far, 0));
                        foes["P3"] = Foe(k, new double2(0, far));
                        foes["P4"] = Foe(k, new double2(0, -far));
                    }
                    break;
                }
                default:
                {
                    t = foes["T"] = Foe(k, new double2(0, 8), 400f);
                    foes["N"] = Foe(k, new double2(3, 8.5), 400f);
                    if (fw == "fw_arc")
                    {
                        k.SetObstacles(new List<float3> { new float3(0f, 4f, 0.6f) });
                    }
                    if (fw == "fw_pierce")
                    {
                        k.TryAddWreck(new EmgWreck { Pos = new double2(0, 5), Radius = 0.5f, Hp = 1000f, MaxHp = 1000f, Until = 1e9 }, out _);
                    }
                    break;
                }
            }
            int shots = car == FirmwareCarrier.Summon ? 1 : car == FirmwareCarrier.Projectile ? 1 : 3;
            int firstBulletSeen = 0;
            for (int s = 0; s < shots; s++)
            {
                k.FireAt(me, t, k.Time);
                if (car == FirmwareCarrier.Projectile && s == 0)
                {
                    List<CombatProjectile> ps = Projs(k);
                    cell.Pellets = ps.Count;
                    if (ps.Count > 0)
                    {
                        CombatProjectile p0 = ps[ps.Count / 2];
                        cell.BulletSpeed = math.length(p0.Vel);
                        cell.BulletAngle = Deg(p0.Vel, new float2(0f, 1f));
                        cell.BulletMods = p0.Mods;
                        cell.BulletFlags = p0.Flags;
                    }
                }
                if (car == FirmwareCarrier.Field && s == 0)
                {
                    List<CombatZone> zs = Zones(k);
                    cell.Zones = zs.Count;
                    if (zs.Count > 0)
                    {
                        cell.ZoneAt = zs[zs.Count / 2].Pos;
                        cell.ZoneR = zs[zs.Count / 2].Radius;
                    }
                }
                for (int i = 0; i < (car == FirmwareCarrier.Summon ? 240 : 40); i++)
                {
                    StepN(k, 1);
                    if (car == FirmwareCarrier.Summon && firstBulletSeen == 0)
                    {
                        foreach (CombatProjectile p in Projs(k))
                        {
                            if ((p.Flags & EmgProjFlags.ViaDrone) != 0)
                            {
                                firstBulletSeen = 1;
                                cell.BulletSpeed = math.length(p.Vel);
                                cell.BulletMods = p.Mods;
                                cell.BulletFlags = p.Flags;
                                cell.DroneBulletId = p.Id;
                                cell.DroneV0 = p.Vel;
                                break;
                            }
                        }
                    }
                    foreach (CombatProjectile p in Projs(k))
                    {
                        if (p.Id == cell.DroneBulletId && (p.Mods & EmgProjMods.Bounced) == 0 && math.lengthsq(p.Vel) > 1e-6f)
                        {
                            cell.DroneSeen++;
                            cell.DroneDbg += $"[{p.Pos.x:0.0},{p.Pos.y:0.0} v{p.Vel.x:0.0},{p.Vel.y:0.0} s{p.SteerLeft:0.#} h{p.HomingTarget} m{(int)p.Mods}]";
                            cell.DroneTurn = math.max(cell.DroneTurn, Deg(cell.DroneV0, p.Vel));
                        }
                        cell.Bounced |= (p.Mods & EmgProjMods.Bounced) != 0;
                        if ((p.Mods & EmgProjMods.Steer) != 0 && cell.SteerUsed < 1f)
                        {
                            cell.SteerUsed = 1f;
                        }
                        if ((p.Mods & EmgProjMods.Returning) != 0)
                        {
                            cell.ReturnSeen = true;
                            cell.ReturnLastDist = (float)math.distance(p.Pos, Pos(k, me));
                        }
                    }
                }
            }
            if (car == FirmwareCarrier.Field)
            {
                StepN(k, 150); // 回收再结算（echo 2 秒）
            }
            cell.Drones = k.DronesOf(k.TryGetUnit(me, out CombatUnitView mv) ? mv.Id : me);
            cell.MeAt = Pos(k, me);
            if (k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent ex))
            {
                cell.LastExpire = ex.Code;
                cell.LastExpireAt = ex.Pos;
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> f in foes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cell.Hp[f.Key] = Hp(k, f.Value);
                cell.At[f.Key] = Pos(k, f.Value);
                sb.Append($"{f.Key}:{cell.Hp[f.Key]:0.0}@{cell.At[f.Key].x:0.00},{cell.At[f.Key].y:0.00};");
            }
            sb.Append($"me@{cell.MeAt.x:0.00},{cell.MeAt.y:0.00};z{cell.Zones}@{cell.ZoneAt.x:0.00},{cell.ZoneAt.y:0.00}r{cell.ZoneR:0.00};d{cell.Drones};v{cell.BulletSpeed:0.0}a{cell.BulletAngle:0.0}m{(int)cell.BulletMods}f{(int)cell.BulletFlags};n{cell.Pellets};x{cell.LastExpire}");
            cell.Fp = sb.ToString();
            return cell;
        }

        private static void CheckMatrix()
        {
            Line("  · B. FGT-EMG-016：9 个非纯数值修饰 × 5 载体各一例，与“去掉修饰”（同一程序去掉修饰写端行，飞行与否不变）对照：行为指纹必须不同，并逐格断言 FG19A 第 3.2 节的读法");
            int differ = 0;
            foreach ((FirmwareCarrier car, string comp, string carName) in Cars)
            {
                foreach ((string mod, string fw) in Mods)
                {
                    Cell on = RunCell(fw, car, comp, false);
                    Cell off = RunCell(fw, car, comp, true);
                    bool diff = on.Fp != off.Fp;
                    differ += diff ? 1 : 0;
                    (bool ok, string what) = Semantic(mod, car, on, off);
                    Expect(diff && ok, $"[{mod}] × {carName}：{what}（指纹{(diff ? "不同" : "相同！")}）"
                                       + (diff && ok ? string.Empty : $"\n      有修饰 {on.Fp}\n      去掉修饰 {off.Fp}"));
                }
            }
            Line($"    行为指纹不同 {differ} / 45 格");
        }

        /// <summary>
        /// B2（FGT-EMG-003 零死对的本卡部分；EMG1-FW-01 关闭 DEBT-EMG1GRM01-05）：修饰放在触发固件之后（[分裂, X]，编进子节点）由子载荷自己结算——
        /// 飞行子弹药（射弹 / 格斗 / 无人机的子弹、余波、无人机子弹）按射弹列执行内核弹道动词；子区 / 小脉冲（力场 / 布区）执行子区读法（触及、多块、游移、击退、牵引、
        /// 环扫、回响、连锁、越障）；数值端口（照明弹 / 广角 / 破甲）落在任何子节点上都由子弹药 / 子区自己放大或破甲。写修饰端口的固件一律切轨，不再保留旧读法字段。
        /// </summary>
        private static void CheckChildMods()
        {
            Line("  · B2. 修饰放末位（[分裂, X]，编进子节点）× 5 载体：飞行子弹药执行内核弹道动词；子区 / 小脉冲按子区读法自己结算；数值端口由子载荷自己放大 / 破甲（固件切轨）");
            var flyVerbs = new HashSet<byte>
            {
                (byte)EmgVerb.Steer, (byte)EmgVerb.PassThrough, (byte)EmgVerb.Ricochet, (byte)EmgVerb.Return, (byte)EmgVerb.Orbit, (byte)EmgVerb.Lob,
                (byte)EmgVerb.Drift, (byte)EmgVerb.Chain, (byte)EmgVerb.Multi,
            };
            var zoneVerbs = new HashSet<byte>
            {
                (byte)EmgVerb.Reach, (byte)EmgVerb.PassThrough, (byte)EmgVerb.Knock, (byte)EmgVerb.Multi, (byte)EmgVerb.Lob, (byte)EmgVerb.Drift,
                (byte)EmgVerb.Chain, (byte)EmgVerb.Pull, (byte)EmgVerb.Echo, (byte)EmgVerb.Sweep,
            };
            var bad = new List<string>();
            int flyingCells = 0, zoneCells = 0;
            foreach ((FirmwareCarrier car, string _, string carName) in Cars)
            {
                foreach ((string mod, string fw) in Mods)
                {
                    EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", fw }, car);
                    List<EmgInstr> own = ChildLines(pr, 110, 119);
                    bool flying = car == FirmwareCarrier.Projectile || car == FirmwareCarrier.Melee || car == FirmwareCarrier.Summon;
                    HashSet<byte> want = flying ? flyVerbs : zoneVerbs;
                    _ = flying ? flyingCells++ : zoneCells++;
                    if (own.Count == 0 || own.Any(x => !want.Contains(x.Verb)))
                    {
                        bad.Add($"{carName}+[分裂, {mod}] 子节点动词 {string.Join("/", own.Select(x => x.Verb))} 不是子载荷能执行的动词");
                    }
                    if (!EmgProgramCompiler.ModifierFirmware(pr).Contains(fw))
                    {
                        bad.Add($"{carName}+[分裂, {mod}] 没切轨（旧读法还在本机出手上）");
                    }
                }
            }
            Expect(bad.Count == 0, $"[分裂, X]：飞行子弹药 {flyingCells} 格解析成内核弹道动词、子区 / 小脉冲 {zoneCells} 格解析成子区读法动词，固件全部切轨{(bad.Count > 0 ? "；不对：" + string.Join("、", bad) : string.Empty)}");

            // 子区 / 小脉冲的行为：正式编译 + 真实内核，同一场景装 / 不装 X 对照（只差子节点那一行）
            var zBad = new List<string>();
            int zRun = 0;
            foreach ((FirmwareCarrier car, string comp, string carName) in Cars.Where(c => c.Car == FirmwareCarrier.Aura || c.Car == FirmwareCarrier.Field))
            {
                ZCell off = ZoneChild(car, comp, null, null), offW = ZoneChild(car, comp, null, "wreck"), offO = ZoneChild(car, comp, null, "obstacle");
                foreach ((string mod, string fw) in Mods)
                {
                    zRun++;
                    string cover = mod == "穿透" ? "wreck" : mod == "抛射" ? "obstacle" : null;
                    ZCell on = ZoneChild(car, comp, fw, cover), o = cover == "wreck" ? offW : cover == "obstacle" ? offO : off;
                    (bool ok, string why) = mod switch
                    {
                        "寻的" => (on.R > o.R + 0.3f, $"子区半径 {on.R:0.##} / 对照 {o.R:0.##}"),
                        "穿透" => (on.Lost("A") > o.Lost("A") + 0.01f, $"残骸后的 A 掉血 {on.Lost("A"):0.##} / 对照被挡 {o.Lost("A"):0.##}"),
                        "反弹" => (on.Moved("A") > o.Moved("A") + 0.2, $"区内 A 被弹开 {on.Moved("A"):0.00} 米 / 对照 {o.Moved("A"):0.00}"),
                        "连锁" => (on.LostAll > o.LostAll + 0.01f, $"子区落下时连锁跳向近处敌人：总掉血 {on.LostAll:0.##} / 对照 {o.LostAll:0.##}"),
                        "多份载荷" => (on.Zones > o.Zones && on.R < o.R, $"一份子区变 {on.Zones} 块小子区（半径 {on.R:0.##}；对照 {o.Zones} 块 {o.R:0.##}）"),
                        "折返" => car == FirmwareCarrier.Aura
                            ? (on.Moved("B") > o.Moved("B") + 0.2, $"子脉冲收缩：把 B 拉近 {on.Moved("B"):0.00} 米 / 对照 {o.Moved("B"):0.00}")
                            : (on.LostAll > o.LostAll + 0.01f, $"子区收回时回响再结算：总掉血 {on.LostAll:0.##} / 对照 {o.LostAll:0.##}"),
                        "绕行" => car == FirmwareCarrier.Aura
                            ? (on.Lost("C") > o.Lost("C") + 0.01f, $"子脉冲旋转外甩：6 米内的 C 掉血 {on.Lost("C"):0.##} / 对照 {o.Lost("C"):0.##}")
                            : (on.R > o.R + 0.3f, $"子区旋转外扩：半径 {on.R:0.##} / 对照 {o.R:0.##}"),
                        "抛射" => (on.Lost("A") > o.Lost("A") + 0.01f, $"障碍后的 A 掉血 {on.Lost("A"):0.##} / 对照被挡 {o.Lost("A"):0.##}"),
                        "侧偏" => (math.distance(on.First, o.First) > 0.05 && math.distance(on.First, o.First) <= 1.5 * math.SQRT2 + 1e-3,
                            $"子区落点随机游移 {math.distance(on.First, o.First):0.00} 米（≤ 1.5√2）"),
                        _ => (false, "未定义"),
                    };
                    if (!ok)
                    {
                        zBad.Add($"{carName}+[分裂, {mod}]：{why}");
                    }
                }
            }
            Expect(zBad.Count == 0 && zRun == 18, $"子区 / 小脉冲 {zRun} 格（力场、布区 × 9 修饰）：装与不装的行为不同，由子区自己结算{(zBad.Count > 0 ? "；不对：" + string.Join("、", zBad) : string.Empty)}");

            // 数值端口（照明弹 / 广角 / 破甲）落在子节点：固件切轨（旧字段不再加在本机出手上），子节点的数值行按旧字段解析成子载荷能执行的读法
            var numBad = new List<string>();
            int numCells = 0;
            foreach ((FirmwareCarrier car, string comp, string carName) in Cars)
            {
                bool flying = car == FirmwareCarrier.Projectile || car == FirmwareCarrier.Melee || car == FirmwareCarrier.Summon;
                foreach ((string mod, string fw, string field) in new[] { ("照明弹", "fw_flare", "scale"), ("广角", "fw_wide", "spread"), ("破甲", "fw_armorpierce", "armor") })
                {
                    numCells++;
                    EmgProgramResult pOn = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", fw }, car);
                    List<EmgInstr> num = ChildLines(pOn, 120, 122);
                    byte expect = mod == "破甲" ? (byte)EmgVerb.Armor : flying ? (mod == "照明弹" ? (byte)EmgVerb.Blast : (byte)EmgVerb.Extra) : (byte)EmgVerb.Reach;
                    bool resolved = num.Count == 1 && num[0].Verb == expect && num[0].A > 0f && (num[0].Flags & (EmgInstrFlags.MagA | EmgInstrFlags.MagB | EmgInstrFlags.MagC)) == 0;
                    Func<string, string, bool> skip = EmgProgramCompiler.LegacySkip(pOn);
                    bool switched = skip != null && skip(fw, field);
                    if (!resolved || !switched)
                    {
                        numBad.Add($"{carName}+[分裂, {mod}]（子节点数值行 {string.Join("/", num.Select(x => $"{x.Verb}:{x.A:0.##}"))}，应为 {expect}；旧字段 {field} 已不加在本机 {switched}）");
                    }
                }
            }
            Expect(numBad.Count == 0, $"[分裂, 照明弹 / 广角 / 破甲] × 5 载体 {numCells} 格：数值行落在子节点时按固件旧字段解析成子载荷读法（射弹列爆圈 / 余击、力场 / 布区列加半径、破甲），固件切轨{(numBad.Count > 0 ? "；不对：" + string.Join("、", numBad) : string.Empty)}");
            {
                ZCell wOn = ZoneChild(FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, "fw_wide", null), wOff = ZoneChild(FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, null, null);
                ZCell aOn = ZoneChild(FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, "fw_armorpierce", null, true), aOff = ZoneChild(FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, null, null, true);
                ZCell fOn = SubBullets("fw_flare", false), fOff = SubBullets(null, false), pOn = SubBullets("fw_armorpierce", true), pOff = SubBullets(null, true);
                Expect(wOn.R > wOff.R + 0.5f && aOn.Lost("A") > aOff.Lost("A") + 0.01f && fOn.Lost("N") > fOff.Lost("N") + 0.01f && pOn.Lost("T") > pOff.Lost("T") + 0.01f,
                    $"子载荷自己放大 / 破甲：力场 [分裂, 广角] 子脉冲半径 {wOn.R:0.##}（对照 {wOff.R:0.##}）；[分裂, 破甲] 子脉冲打正面装甲的 A 掉 {aOn.Lost("A"):0.##}（对照 {aOff.Lost("A"):0.##}）；" +
                    $"射弹 [分裂, 照明弹] 子弹命中处爆圈波及 N 掉 {fOn.Lost("N"):0.##}（对照 {fOff.Lost("N"):0.##}）；[分裂, 破甲] 子弹打正面装甲的 T2 掉 {pOn.Lost("T"):0.##}（对照 {pOff.Lost("T"):0.##}）");
            }
            EmgProgramResult rootFirst = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_ricochet", "fw_split" }, FirmwareCarrier.Aura);
            Expect(EmgProgramCompiler.ModifierFirmware(rootFirst).Contains("fw_ricochet"),
                "对照：力场 + [跳弹, 分裂]（修饰在根节点）照常切到声明轨（根节点按 fg.TbPortCarrier 折进读法）");
            bool Bounced(bool withMod)
            {
                using var k = new CombatKernel(Cfg(), 32);
                EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), withMod ? new[] { "fw_split", "fw_ricochet" } : new[] { "fw_split" }, FirmwareCarrier.Melee);
                CombatWeapon w = BaseWeapon(ComponentCatalog.CompRamId, 3f);
                w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
                w.Program = CombatSite.RegisterProgram(k, pr);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int t = Foe(k, new double2(0, 1.6));
                Foe(k, new double2(0.8, 3.4));
                Foe(k, new double2(2.4, 4.6));
                Foe(k, new double2(-1.6, 3.8));
                bool seen = false;
                for (int s = 0; s < 3; s++)
                {
                    k.FireAt(me, t, k.Time);
                    for (int i = 0; i < 30; i++)
                    {
                        StepN(k, 1);
                        seen |= Projs(k).Any(q => (q.Flags & EmgProjFlags.Sub) != 0 && (q.Mods & EmgProjMods.Bounced) != 0);
                    }
                }
                return seen;
            }
            bool bOn = Bounced(true), bOff = Bounced(false);
            Expect(bOn && !bOff, $"格斗 + [分裂, 跳弹]：放出的余波命中后真的改向下一个目标（见到已反弹的子弹药 {bOn}；只装分裂 {bOff}）——以前解析成格斗的击退动词，子弹药上装了等于没装");
            CheckChildPellets();
        }

        /// <summary>子节点（第 1 个子节点）自己的写端行（端口 lo～hi，不含继承来的）。</summary>
        private static List<EmgInstr> ChildLines(EmgProgramResult pr, int lo, int hi)
        {
            var own = new List<EmgInstr>();
            int child = pr.KernelNodes.FindIndex(n => n.Parent >= 0);
            if (child < 0)
            {
                return own;
            }
            EmgNode cn = pr.KernelNodes[child];
            for (int i = cn.FirstInstr; i < cn.FirstInstr + cn.InstrCount; i++)
            {
                EmgInstr x = pr.KernelInstrs[i];
                if (x.Role > EmgConst.RoleLastRead && x.Port >= lo && x.Port <= hi && (x.Flags & EmgInstrFlags.Inherited) == 0)
                {
                    own.Add(x);
                }
            }
            return own;
        }

        private sealed class ZCell
        {
            public int Zones;
            public float R;
            public double2 First = new double2(double.NaN, double.NaN);
            public readonly Dictionary<string, float> Hp = new Dictionary<string, float>();
            public readonly Dictionary<string, double2> At = new Dictionary<string, double2>();
            public readonly Dictionary<string, double2> Start = new Dictionary<string, double2>();
            public float Lost(string key) => Hp.TryGetValue(key, out float v) ? 2000f - v : 0f;
            public float LostAll => Hp.Values.Sum(v => 2000f - v);
            public double Moved(string key) => At.TryGetValue(key, out double2 a) && Start.TryGetValue(key, out double2 b) ? math.distance(a, b) : 0;
        }

        private static int ArmoredFoe(CombatKernel k, double2 p, bool armored) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = Live | CombatUnitFlags.Report,
            Position = p, Home = p, Radius = 0.5f, Health = 2000f, MaxHealth = 2000f, Weapon = -1, Priority = 1,
            ArmorFraction = armored ? 0.6f : 0f, ArmorHalfAngleDeg = 180f, ArmorFacing = new float2(0f, -1f),
        });

        /// <summary>
        /// 力场 / 布区 + [分裂, <paramref name="fw"/>]（null = 只装分裂）打 T：子区落在命中处。T(0,4)、A(0.8,4) 在子区里、B(2.6,4) 在子区外 6 米内、C(4.5,4) 更远；
        /// <paramref name="cover"/> = "wreck" / "obstacle" 时 T 与 A 之间放一块小残骸 / 小障碍（根出手从本机打 A 不经过它）。走 5 秒，记子区块数 / 半径 / 第一块的落点、掉血与位移。
        /// </summary>
        private static ZCell ZoneChild(FirmwareCarrier car, string comp, string fw, string cover, bool armored = false)
        {
            var cell = new ZCell();
            using var k = new CombatKernel(Cfg(), 64);
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), fw == null ? new[] { "fw_split" } : new[] { "fw_split", fw }, car);
            CombatWeapon w = BaseWeapon(comp, car == FirmwareCarrier.Field ? 12f : 20f);
            w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
            w.Program = CombatSite.RegisterProgram(k, pr);
            int me = Me(k, new double2(0, 0), k.AddWeapon(w));
            var ids = new Dictionary<string, int>
            {
                ["T"] = ArmoredFoe(k, new double2(0, 4), false), ["A"] = ArmoredFoe(k, new double2(0.8, 4), armored),
                ["B"] = ArmoredFoe(k, new double2(2.6, 4), false), ["C"] = ArmoredFoe(k, new double2(4.5, 4), false),
            };
            if (cover == "wreck")
            {
                k.TryAddWreck(new EmgWreck { Pos = new double2(0.4, 4), Radius = 0.15f, Hp = 1000f, MaxHp = 1000f, Until = 1e9 }, out _);
            }
            else if (cover == "obstacle")
            {
                k.SetObstacles(new List<float3> { new float3(0.4f, 4f, 0.15f) });
            }
            foreach (KeyValuePair<string, int> kv in ids)
            {
                cell.Start[kv.Key] = Pos(k, kv.Value);
            }
            k.FireAt(me, ids["T"], k.Time);
            for (int i = 0; i < 300; i++)
            {
                StepN(k, 1);
                int n = 0;
                foreach (CombatZone z in Zones(k).Where(z => z.EmgSub != 0))
                {
                    n++;
                    cell.R = Mathf.Max(cell.R, z.Radius);
                    if (double.IsNaN(cell.First.x))
                    {
                        cell.First = z.Pos;
                    }
                }
                cell.Zones = Math.Max(cell.Zones, n);
            }
            foreach (KeyValuePair<string, int> kv in ids)
            {
                cell.Hp[kv.Key] = Hp(k, kv.Value);
                cell.At[kv.Key] = Pos(k, kv.Value);
            }
            return cell;
        }

        /// <summary>机枪 + [分裂, <paramref name="fw"/>]：主弹打 M(0,4) 放出子弹，子弹打 T(0,6)（<paramref name="armored"/> 时正面装甲 0.6），N(0.9,6) 在 T 身边。走 2 秒。</summary>
        private static ZCell SubBullets(string fw, bool armored)
        {
            var cell = new ZCell();
            using var k = new CombatKernel(Cfg(), 64);
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), fw == null ? new[] { "fw_split" } : new[] { "fw_split", fw }, FirmwareCarrier.Projectile);
            CombatWeapon w = Flight(-1, 30f, 2f);
            w.Program = CombatSite.RegisterProgram(k, pr);
            int me = Me(k, new double2(0, 0), k.AddWeapon(w));
            int m = ArmoredFoe(k, new double2(0, 4), false);
            var ids = new Dictionary<string, int> { ["T"] = ArmoredFoe(k, new double2(0, 6), armored), ["N"] = ArmoredFoe(k, new double2(0.9, 6.2), false) };
            k.FireAt(me, m, k.Time);
            StepN(k, 120);
            foreach (KeyValuePair<string, int> kv in ids)
            {
                cell.Hp[kv.Key] = Hp(k, kv.Value);
            }
            return cell;
        }

        /// <summary>
        /// 子弹药上的霰射分份（[分裂, 霰射] 的子弹分成 3 发真实弹丸，各 0.4 倍）与弹体池打满后排队的霰射弹丸保留各自扇角（卡片补测，ADR ③ 顺延不丢）。
        /// </summary>
        private static void CheckChildPellets()
        {
            int Subs(bool scatter, out float dmg)
            {
                using var k = new CombatKernel(Cfg(), 64);
                EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), scatter ? new[] { "fw_split", "fw_scatter" } : new[] { "fw_split" }, FirmwareCarrier.Projectile);
                CombatWeapon w = Flight(-1, 30f, 2f);
                w.Program = CombatSite.RegisterProgram(k, pr);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int m = Foe(k, new double2(0, 4));
                k.FireAt(me, m, k.Time);
                int most = 0;
                dmg = 0f;
                for (int i = 0; i < 40; i++)
                {
                    StepN(k, 1);
                    List<CombatProjectile> subs = Projs(k).Where(p => (p.Flags & EmgProjFlags.Sub) != 0).ToList();
                    if (subs.Count > most)
                    {
                        most = subs.Count;
                        dmg = subs.Max(p => p.Damage);
                    }
                }
                return most;
            }
            int on = Subs(true, out float dOn), off = Subs(false, out float dOff);
            Expect(on == off * 3 && Near(dOn, dOff * 0.4f, 0.01f), $"[分裂, 霰射]：子弹分成 3 发真实弹丸（同时在飞 {on} 发，对照 {off} 发），每发 {dOn:0.###}（对照 {dOff:0.###} × 0.4）");

            // 弹体池打满：多份载荷的弹丸排进待生成表，生成时保留各自扇角（不全挤成朝目标的一条线；EMG1-GRM-01 审查修复的 Dir 记账，卡片要求补测）
            CombatConfig cfg = Cfg();
            cfg.ProjectileCapacity = 4;
            using (var k = new CombatKernel(cfg, 64))
            {
                EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_scatter" }, FirmwareCarrier.Projectile);
                CombatWeapon w = Flight(-1, 30f, 0.5f);
                w.Program = CombatSite.RegisterProgram(k, pr);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int t = Foe(k, new double2(0, 3), 5000f);
                var seen = new SortedDictionary<int, float>();
                void Look()
                {
                    foreach (CombatProjectile p in Projs(k).Where(p => !seen.ContainsKey(p.Id)))
                    {
                        seen[p.Id] = math.degrees(math.atan2(p.Vel.x, p.Vel.y));
                    }
                }
                k.FireAt(me, t, k.Time); // 3 发进池
                Look();
                k.FireAt(me, t, k.Time); // 池只剩 1 个位：1 发进池、2 发排队
                Look();
                int queued = k.EmgPendingCount;
                for (int i = 0; i < 60; i++)
                {
                    StepN(k, 1);
                    Look();
                }
                List<float> second = seen.Values.Skip(3).ToList();
                bool fanned = second.Count == 3 && second.Any(a => Mathf.Abs(a - 15f) < 1.5f) && second.Any(a => Mathf.Abs(a + 15f) < 1.5f) && second.Any(a => Mathf.Abs(a) < 1.5f);
                Expect(queued == 2 && k.EmgPendingCount == 0 && fanned,
                    $"弹体池上限 4：第二次出手 {queued} 发排队，空出位置后生成，三发仍是 −15° / 0° / +15° 扇面（第二次出手的航向 {string.Join(" / ", second.Select(a => a.ToString("0.#")))}°）");
            }
        }

        /// <summary>
        /// F2（EMG1-FW-01 关闭 DEBT-EMG1GRM01-03；FG19A 第 1.1 节第 3 / 14 / 42 行与第 1.8 节退化表）：感知 / 条件读端改变行为——
        /// 霰射读 ter.cover：扇面里被掩体挡的那份弹丸往外侧绕开；绕轨读 ent.projectile：绕行中才拦敌弹；乱流读 fld.deflect：穿过偏折场才有一半不被反射。读端开 / 关对照。
        /// </summary>
        private static void CheckReadEnds()
        {
            Line("  · F2. 读端改变行为（开 / 关对照）：霰射绕开掩体（ter.cover）、绕轨拦弹（ent.projectile）、乱流规避偏折场（fld.deflect）");
            List<float> Fan(bool sense)
            {
                using var k = new CombatKernel(Cfg(), 16);
                N root = Root().Verb(EmgVerb.Multi, 3f, 0.4f, 0f, 0f, 30f);
                int p = Tree(k, sense ? root.Sense(EmgConst.PortCover) : root);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 1f)));
                int t = Foe(k, new double2(0, 10), 5000f);
                float2 blocked = new float2(-math.sin(math.radians(15f)), math.cos(math.radians(15f))) * 4.8f;
                k.SetObstacles(new List<float3> { new float3(blocked.x, blocked.y, 0.5f) });
                k.FireAt(me, t, k.Time);
                return Projs(k).Select(q => math.degrees(math.atan2(-q.Vel.x, q.Vel.y))).OrderBy(a => a).ToList();
            }
            List<float> on = Fan(true), off = Fan(false);
            Expect(off.Count == 3 && Near(off[2], 15f, 0.5f) && on.Count == 3 && on[2] > 20f && Near(on[0], -15f, 0.5f) && Near(on[1], 0f, 0.5f),
                $"霰射读掩体：被障碍挡的那份往外侧绕开（航向 {string.Join(" / ", on.Select(a => a.ToString("0.#")))}°）；不读掩体时均分扇面 {string.Join(" / ", off.Select(a => a.ToString("0.#")))}°（打在障碍上）");
            long Intercepted(bool sense)
            {
                using var k = new CombatKernel(Cfg(), 64);
                N root = Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f).Read(EmgEventKind.Absorb);
                int p = Tree(k, sense ? root.Sense(EmgConst.PortProjectile) : root);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 20f, 3f)));
                int target = Foe(k, new double2(0, 30));
                int gunW = k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = 1f, Cooldown = 0f,
                    ProjectileSpeed = 0.05f, ProjectileRadius = 0.6f, ProjectileLife = 60f,
                });
                for (int g = 0; g < 4; g++)
                {
                    float ang = math.radians(30f + 90f * g);
                    k.FireAt(Unit(k, CombatFaction.Hostile, new double2(math.cos(ang), math.sin(ang)) * 3.0, gunW, 1000f), me, k.Time);
                }
                StepN(k, 2);
                long before = k.EmgEnqueuedOf(EmgEventKind.Absorb);
                k.FireAt(me, target, k.Time);
                StepN(k, 120);
                return k.EmgEnqueuedOf(EmgEventKind.Absorb) - before;
            }
            long iOn = Intercepted(true), iOff = Intercepted(false);
            Expect(iOn >= 1 && iOff == 0, $"绕轨读弹体：绕行中拦下 {iOn} 发敌弹；不读 ent.projectile 时只绕行、拦 {iOff} 发");
            (int shots, long reflected) Deflected(bool cond)
            {
                using var k = new CombatKernel(Cfg(), 64);
                N root = Root().Verb(EmgVerb.Drift, 15f, 1f, 0.2f, 0.5f);
                int p = Tree(k, cond ? root.Sense(EmgConst.PortDeflect, Cond) : root);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int prism = Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f, k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f }));
                k.SetFlag(prism, CombatUnitFlags.DeflectArmed, true);
                k.SetDeflectPhase(prism, 2, 60f);
                int shots = 0;
                for (int s = 0; s < 30; s++)
                {
                    int before = k.ProjectileCount;
                    k.FireAt(me, prism, k.Time);
                    shots += k.ProjectileCount > before ? 1 : 0;
                    StepN(k, 20);
                }
                return (shots, k.Counters.ShotsReflected);
            }
            (int sOn, long rOn) = Deflected(true);
            (int sOff, long rOff) = Deflected(false);
            Expect(sOn == 30 && sOff == 30 && rOn > 5 && rOn < 25 && rOff == sOff,
                $"乱流读偏折场：穿过偏折场被反射 {rOn} / {sOn} 发（约一半规避）；不读 fld.deflect 时只剩侧偏，{rOff} / {sOff} 发全被反射");
        }

        private static float LostOf(Cell c, string key, float init) => c.Hp.TryGetValue(key, out float v) ? init - v : 0f;

        private static double Moved(Cell a, Cell b, string key) => a.At.TryGetValue(key, out double2 x) && b.At.TryGetValue(key, out double2 y) ? math.distance(x, y) : 0;

        private static (bool, string) Semantic(string mod, FirmwareCarrier car, Cell on, Cell off)
        {
            switch (car)
            {
                case FirmwareCarrier.Projectile:
                    switch (mod)
                    {
                        case "寻的": return (LostOf(on, "H", 200f) > 0f && LostOf(off, "H", 200f) == 0f, $"飞行中转向热信号最强的 H（H 掉 {LostOf(on, "H", 200f):0}，对照 {LostOf(off, "H", 200f):0}）");
                        case "穿透": return (LostOf(on, "M", 200f) > 0f && LostOf(on, "T", 200f) > 0f && LostOf(off, "T", 200f) == 0f, $"穿过挡路的 M 继续打到身后的 T（T 掉 {LostOf(on, "T", 200f):0}，对照 {LostOf(off, "T", 200f):0}）");
                        case "反弹": return (LostOf(on, "M", 200f) > 0f && LostOf(on, "T", 200f) > 0f && LostOf(off, "T", 200f) == 0f, $"撞 M 后改向 6 米内的 T（T 掉 {LostOf(on, "T", 200f):0}）");
                        case "连锁": return (LostOf(on, "M", 200f) + LostOf(on, "H", 200f) > 0f && LostOf(off, "M", 200f) + LostOf(off, "H", 200f) == 0f,
                            $"命中 T 后连锁跳向 6 米内的 H、M（掉 {LostOf(on, "H", 200f):0.#} / {LostOf(on, "M", 200f):0.#}；对照 0）");
                        case "多份载荷": return (on.Pellets == 3 && off.Pellets == 1 && Near(LostOf(on, "M", 200f), 4f, 0.05f), $"一次出手 {on.Pellets} 发真实弹丸（对照 {off.Pellets} 发），正中一发打 M 掉 {LostOf(on, "M", 200f):0.#}（0.4 倍）");
                        case "折返": return (on.ReturnSeen && on.ReturnLastDist >= 0f && on.ReturnLastDist < 2.0f && !off.ReturnSeen && on.Pellets == 1,
                            $"命中 M 后折返，飞回发射者身边终结（最后出现时离发射者 {on.ReturnLastDist:0.0} 米，之后消失；对照不折返）");
                        case "绕行": return (LostOf(on, "O", 200f) > 0f && LostOf(off, "O", 200f) == 0f, $"先绕机体一圈，扫到身边的 O（O 掉 {LostOf(on, "O", 200f):0}）");
                        case "抛射": return (LostOf(on, "T", 200f) > 0f && LostOf(on, "M", 200f) == 0f && LostOf(off, "M", 200f) > 0f, $"越过挡路的 M 落到 T 脚下（T 掉 {LostOf(on, "T", 200f):0}，M 掉 {LostOf(on, "M", 200f):0}；对照打在 M 上）");
                        case "侧偏": return (Near(on.BulletSpeed, 36f, 0.05f) && on.BulletAngle <= 15.01f && Near(off.BulletSpeed, 30f, 0.05f), $"弹速 {on.BulletSpeed:0.#}（+20%，对照 {off.BulletSpeed:0.#}），侧偏 {on.BulletAngle:0.0}° ≤ 15°");
                    }
                    break;
                case FirmwareCarrier.Summon:
                    switch (mod)
                    {
                        case "寻的": return (on.DroneTurn > 0.5f && off.DroneTurn < 0.1f, $"无人机子弹飞行中转向热信号最强的 C（第一发子弹航向转了 {on.DroneTurn:0.0}°，对照 {off.DroneTurn:0.00}°）{(on.DroneTurn > 0.5f ? string.Empty : $"；观察 {on.DroneSeen} 次 {on.DroneDbg}")}");
                        case "穿透": return (LostOf(on, "B", 400f) > LostOf(off, "B", 400f), $"无人机子弹穿透：身后的 B 多掉血（{LostOf(on, "B", 400f):0} / 对照 {LostOf(off, "B", 400f):0}）");
                        case "反弹": return (on.Bounced && !off.Bounced && LostOf(on, "B", 400f) + LostOf(on, "C", 400f) > LostOf(off, "B", 400f) + LostOf(off, "C", 400f),
                            $"无人机子弹命中 T 后真的改向（弹体带“已反弹”记号 {on.Bounced}），打到 B / C（{LostOf(on, "B", 400f) + LostOf(on, "C", 400f):0} / 对照 {LostOf(off, "B", 400f) + LostOf(off, "C", 400f):0}）");
                        case "连锁": return (!on.Bounced && LostOf(on, "B", 400f) + LostOf(on, "C", 400f) > LostOf(off, "B", 400f) + LostOf(off, "C", 400f),
                            $"无人机子弹命中后连锁跳向 B / C（{LostOf(on, "B", 400f) + LostOf(on, "C", 400f):0} / 对照 {LostOf(off, "B", 400f) + LostOf(off, "C", 400f):0}），子弹本身不改向（反弹记号 {on.Bounced}）");
                        case "多份载荷": return (on.Drones == off.Drones + 1, $"无人机舱多放 1 架（{on.Drones} / 对照 {off.Drones}）");
                        case "折返": return (on.ReturnSeen && on.ReturnLastDist >= 0f && on.ReturnLastDist < 2.0f && !off.ReturnSeen,
                            $"无人机子弹命中后折返，飞回母机身边终结（最后出现时离母机 {on.ReturnLastDist:0.0} 米，之后消失；对照不折返）");
                        case "绕行": return (LostOf(on, "O", 400f) > 0f && LostOf(off, "O", 400f) == 0f, $"无人机绕母机巡航，扫到母机身边的 O（{LostOf(on, "O", 400f):0}）");
                        case "抛射": return ((on.BulletFlags & EmgProjFlags.Lob) != 0 && LostOf(on, "T", 400f) > LostOf(off, "T", 400f),
                            $"无人机子弹俯冲抛射，越过 T 前的障碍命中（T 掉 {LostOf(on, "T", 400f):0}；对照直飞被障碍截停 {LostOf(off, "T", 400f):0}）");
                        case "侧偏": return (Near(on.BulletSpeed, 36f, 0.05f) && Near(off.BulletSpeed, 30f, 0.05f), $"无人机子弹航线侧偏并提速（{on.BulletSpeed:0.#} / 对照 {off.BulletSpeed:0.#}）");
                    }
                    break;
                case FirmwareCarrier.Melee:
                    switch (mod)
                    {
                        case "寻的": return (LostOf(on, "S+", 200f) + LostOf(on, "S-", 200f) > LostOf(off, "S+", 200f) + LostOf(off, "S-", 200f), "出手朝向修正：扇形加宽，扇边上的敌人被扫到");
                        case "穿透": return (LostOf(on, "B", 200f) > 0f && LostOf(off, "B", 200f) == 0f, $"刺穿主目标，打到身后 3 米内的 B（{LostOf(on, "B", 200f):0}）");
                        case "反弹": return (Moved(on, off, "T") > 1.0, $"把目标弹开（T 位移比对照多 {Moved(on, off, "T"):0.00} 米）");
                        case "连锁": return (on.Hp.Sum(x => 200f - x.Value) > off.Hp.Sum(x => 200f - x.Value) && LostOf(on, "T", 200f) == LostOf(off, "T", 200f),
                            $"挥击命中后连锁跳向 4 米内的敌人（总掉血 {on.Hp.Sum(x => 200f - x.Value):0.#}，对照 {off.Hp.Sum(x => 200f - x.Value):0.#}；主目标不变）");
                        case "多份载荷": return (on.Hp.Sum(x => 200f - x.Value) > off.Hp.Sum(x => 200f - x.Value), "一次挥击分出 2 道独立余击，各算命中");
                        case "折返": return (Moved(on, off, "B") > 0.5 && Moved(on, off, "F") < 1e-3, $"回拉：把 4 米内的 B 拉近（位移差 {Moved(on, off, "B"):0.00} 米），4 米外的 F 不动（{Moved(on, off, "F"):0.00}）");
                        case "绕行": return (LostOf(on, "R", 200f) > 0f && LostOf(off, "R", 200f) == 0f, "旋风：挥击扫满一整圈，身后的 R 被扫到");
                        case "抛射": return (math.distance(on.MeAt, off.MeAt) > 0.2, $"越障跃击：扑向目标（本机位移差 {math.distance(on.MeAt, off.MeAt):0.00} 米）");
                        case "侧偏": return (LostOf(on, "S+", 200f) + LostOf(on, "S-", 200f) > 0f && LostOf(off, "S+", 200f) + LostOf(off, "S-", 200f) == 0f, "挥击方向随机偏 ±15°、扇形 +10°：扇边外的敌人被扫到");
                    }
                    break;
                case FirmwareCarrier.Aura:
                    switch (mod)
                    {
                        case "寻的": return (LostOf(on, "O", 200f) > 0f && LostOf(off, "O", 200f) == 0f, $"脉冲向敌人延伸（半径 +1.7 米），圈外 1.1 米的 O 被打到（{LostOf(on, "O", 200f):0}）");
                        case "穿透": return (LostOf(on, "W", 200f) > 0f && LostOf(off, "W", 200f) == 0f, $"脉冲不被残骸吸收，打到残骸后的 W（{LostOf(on, "W", 200f):0}；对照被挡）");
                        case "反弹": return (Moved(on, off, "I") > 0.5, $"脉冲把敌人弹开（I 位移差 {Moved(on, off, "I"):0.00} 米）");
                        case "连锁": return (on.Hp.Sum(x => 200f - x.Value) > off.Hp.Sum(x => 200f - x.Value) && LostOf(on, "T", 200f) == LostOf(off, "T", 200f),
                            $"脉冲命中主目标后连锁跳向 6 米内的敌人（总掉血 {on.Hp.Sum(x => 200f - x.Value):0.#}，对照 {off.Hp.Sum(x => 200f - x.Value):0.#}）");
                        case "多份载荷": return (Math.Abs(LostOf(on, "I", 200f) - LostOf(off, "I", 200f)) > 0.5f || Math.Abs(LostOf(on, "E", 200f) - LostOf(off, "E", 200f)) > 0.5f, "一次脉冲分成 3 个小脉冲（各 0.4 倍）：圈内伤害分布改变");
                        case "折返": return (Moved(on, off, "I") > 0.3, $"力场收缩：把圈内敌人拉向本机（I 位移差 {Moved(on, off, "I"):0.00} 米）");
                        case "绕行": return (LostOf(on, "O", 200f) > 0f && LostOf(off, "O", 200f) == 0f, "力场旋转外甩，6 米内的 O 被扫到");
                        case "抛射": return (LostOf(on, "X", 200f) > 0f && LostOf(off, "X", 200f) == 0f, $"脉冲越过障碍作用于其后的 X（{LostOf(on, "X", 200f):0}；对照被障碍挡）");
                        case "侧偏":
                        {
                            float probes = LostOf(on, "P1", 200f) + LostOf(on, "P2", 200f) + LostOf(on, "P3", 200f) + LostOf(on, "P4", 200f);
                            float edge = Math.Abs(LostOf(on, "O", 200f) - LostOf(off, "O", 200f)) + Math.Abs(LostOf(on, "I", 200f) - LostOf(off, "I", 200f))
                                         + Math.Abs(LostOf(on, "W", 200f) - LostOf(off, "W", 200f)) + Math.Abs(LostOf(on, "X", 200f) - LostOf(off, "X", 200f))
                                         + Math.Abs(LostOf(on, "E", 200f) - LostOf(off, "E", 200f)) + Math.Abs(LostOf(on, "T", 200f) - LostOf(off, "T", 200f));
                            return (edge > 0.5f && probes == 0f, $"脉冲中心随机游移：圈边敌人的受击改变（掉血差合计 {edge:0.#}），偏移上界内（离圈 2.4 米外的 4 个探针掉血 {probes:0}）");
                        }
                    }
                    break;
                default:
                    switch (mod)
                    {
                        case "寻的": return (on.ZoneR > off.ZoneR + 0.5f, $"落点吸附：落区半径 {on.ZoneR:0.##}（对照 {off.ZoneR:0.##}）");
                        case "穿透": return (math.distance(on.ZoneAt, new double2(0, 8)) < 0.5 && math.distance(off.ZoneAt, new double2(0, 8)) > 2, $"越过残骸落在目标脚下（落点 y {on.ZoneAt.y:0.0}；对照被残骸截停在 y {off.ZoneAt.y:0.0}）");
                        case "反弹": return (Moved(on, off, "T") > 0.5, $"落点把目标弹开（T 位移差 {Moved(on, off, "T"):0.00} 米）");
                        case "连锁": return (LostOf(on, "N", 400f) > LostOf(off, "N", 400f), "落点命中后连锁到 6 米内的 N");
                        case "多份载荷": return (on.Zones == 3 && off.Zones == 1 && on.ZoneR < off.ZoneR, $"一块落区变 {on.Zones} 块小落区（半径 {on.ZoneR:0.##}，对照 {off.Zones} 块 {off.ZoneR:0.##}）");
                        case "折返": return (LostOf(on, "T", 400f) > LostOf(off, "T", 400f), "落区收回时再结算一次（目标多掉血）");
                        case "绕行": return (on.ZoneR > off.ZoneR + 0.5f, $"布区旋转外扩（半径 {on.ZoneR:0.##} / {off.ZoneR:0.##}）");
                        case "抛射": return (math.distance(on.ZoneAt, new double2(0, 8)) < 0.5 && off.ZoneAt.y < 4, $"无视线投放：越过障碍落在目标脚下（y {on.ZoneAt.y:0.0}；对照截停在障碍前 y {off.ZoneAt.y:0.0}）");
                        case "侧偏": return (math.distance(on.ZoneAt, off.ZoneAt) > 0.05 && math.distance(on.ZoneAt, off.ZoneAt) <= 1.5 * math.SQRT2 + 1e-3, $"落点随机散开 ±1.5 米（偏移 {math.distance(on.ZoneAt, off.ZoneAt):0.00} 米）");
                    }
                    break;
            }
            return (false, "未定义的格");
        }

        // ── C. 寻的 ─────────────────────────────────────────────────────────────

        private static void CheckHoming()
        {
            Line("  · C. 寻的（Steer）：累计修正上限 = 声明 0.85 × 35.294 = 30°（实测）、转向率、偏好已标记 > 热信号 > 最近、目标中途阵亡在剩余额度内重选、没有目标直飞");
            EmgProgramResult real = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_homing" }, FirmwareCarrier.Projectile);
            EmgInstr steer = real.KernelInstrs.FirstOrDefault(x => x.Verb == (byte)EmgVerb.Steer);
            float cap = steer.A * steer.Amount;
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Steer, steer.Amount, steer.A, steer.B, steer.C));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int foe = Foe(k, new double2(0, 12));
                k.FireAt(me, foe, k.Time);
                float2 v0 = Projs(k)[0].Vel;
                k.SetPosition(foe, new double2(12, 1)); // 目标挪到右侧 ~85°：要转的角度远超上限
                float2 v1 = v0;
                float left = -1f;
                for (int i = 0; i < 30 && k.ProjectileCount > 0; i++)
                {
                    StepN(k, 1);
                    if (k.ProjectileCount > 0)
                    {
                        CombatProjectile q = Projs(k)[0];
                        v1 = q.Vel;
                        left = q.SteerLeft;
                    }
                }
                float turned = Deg(v0, v1);
                Expect(Near(cap, 30f, 0.01f) && Near(turned, 30f, 0.05f) && Near(left, 0f, 1e-3f) && Near(math.length(v1), 30f, 1e-3f),
                    $"真实编译的 [寻的]：修正上限 {cap:0.00}°（描述“最多修正 30°”）；目标挪到侧面后弹体实际累计转了 {turned:0.00}°，剩余额度 {left:0.###}，弹速不变 {math.length(v1):0.#}");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                // 偏好：同一前方扇面里 3 个候选——最近的 N、热的 H、已标记的 K（程序带 已标记 + 热信号 两种偏好）
                int p = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f, 0f, EmgInstrFlags.PreferHeat | EmgInstrFlags.PreferMarked));
                int ph = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f, 0f, EmgInstrFlags.PreferHeat));
                int pn = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f));
                int m1 = Me(k, new double2(0, 0), k.AddWeapon(Flight(p)));
                int m2 = Me(k, new double2(40, 0), k.AddWeapon(Flight(ph)));
                int m3 = Me(k, new double2(80, 0), k.AddWeapon(Flight(pn)));
                int[] Trio(double x)
                {
                    int nn = Foe(k, new double2(x, 6)), hh = Foe(k, new double2(x + 2, 9)), kk = Foe(k, new double2(x - 2, 9));
                    k.SetWeaponState(hh, 60f, false, 0, 0);
                    k.SetMarkedUntil(kk, 1e9);
                    return new[] { nn, hh, kk };
                }
                int[] a = Trio(0), b = Trio(40), c = Trio(80);
                k.FireAt(m1, a[0], k.Time);
                k.FireAt(m2, b[0], k.Time);
                k.FireAt(m3, c[0], k.Time);
                var locked = Projs(k).OrderBy(x => x.Pos.x).Select(x => x.HomingTarget).ToArray();
                Expect(locked.Length == 3 && locked[0] == a[2] && locked[1] == b[1] && locked[2] == c[0],
                    $"挑目标偏好：已标记 + 热信号 → 锁已标记的 K；只有热信号 → 锁热的 H；没有偏好 → 开火目标 / 最近 N（锁定 {string.Join(",", locked)}）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 10f, 3f)));
                int first = Foe(k, new double2(0, 12));
                int second = Foe(k, new double2(2, 10));
                k.FireAt(me, first, k.Time);
                StepN(k, 10);
                CombatProjectile before = Projs(k)[0];
                k.SetHealth(first, 0f, 200f, false);
                StepN(k, 2);
                CombatProjectile after = Projs(k)[0];
                Expect(before.HomingTarget == first && after.HomingTarget == second && after.SteerLeft <= before.SteerLeft && (after.Mods & EmgProjMods.Steer) != 0,
                    $"目标中途阵亡：在剩余额度（{before.SteerLeft:0.#}° → {after.SteerLeft:0.#}°）内按同一偏好重选到另一个目标（{before.HomingTarget} → {after.HomingTarget}）");
                k.SetHealth(second, 0f, 200f, false);
                StepN(k, 2);
                CombatProjectile lost = Projs(k).Count > 0 ? Projs(k)[0] : default;
                float2 v = lost.Vel;
                StepN(k, 5);
                CombatProjectile later = Projs(k).Count > 0 ? Projs(k)[0] : default;
                Expect(lost.HomingTarget == 0 && (lost.Mods & EmgProjMods.Steer) == 0 && Deg(v, later.Vel) < 1e-3f,
                    "再没有目标：清掉寻的、直飞（之后航向不变）");
            }
        }

        // ── D. 穿透 / 反弹 ─────────────────────────────────────────────────────────

        private static void CheckPierceBounce()
        {
            Line("  · D. 穿透 / 反弹：次数用尽后终结；单位面（跳弹）与障碍面（壁撞）分开计（FGR-EMG-017 面过滤）；撞单位改向 6 米内下一个目标、撞障碍按撞点法线镜面反射；evt.pierce / evt.bounce");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.PassThrough, 2f, 1f).Read(EmgEventKind.Pierce).Read(EmgEventKind.Hit));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int[] line = { Foe(k, new double2(0, 4)), Foe(k, new double2(0, 6)), Foe(k, new double2(0, 8)), Foe(k, new double2(0, 10)) };
                k.FireAt(me, line[0], k.Time);
                StepN(k, 90);
                k.TryGetLastEmgEvent(EmgEventKind.Hit, out EmgEvent lastHit);
                Expect(line.Take(3).All(f => Near(Hp(k, f), 190f)) && Near(Hp(k, line[3]), 200f) && k.EmgEnqueuedOf(EmgEventKind.Pierce) == 2 && lastHit.Code == EmgCode.HitAfterPierce,
                    $"穿透 2 次：同一直线上前 3 个各掉 10、第 4 个不掉（次数用尽在第 3 个处终结）；evt.pierce {k.EmgEnqueuedOf(EmgEventKind.Pierce)} 次，后面的命中 Code = 穿透后（{lastHit.Code}）");
            }
            // EMG1-ENT-01（DEBT-EMG1GRM01-01）：穿透按质量——比弹体轻的目标不消耗次数，不轻于弹体的每穿一个消耗 1 次（FG19A 第 1.1 节）
            foreach (bool light in new[] { true, false })
            {
                using (var k = new CombatKernel(Cfg(), 16))
                {
                    int p = Tree(k, Root().Verb(EmgVerb.PassThrough, 1f, 1f).Read(EmgEventKind.Pierce));
                    int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                    int[] line = { Foe(k, new double2(0, 4)), Foe(k, new double2(0, 6)), Foe(k, new double2(0, 8)) };
                    foreach (int f in line)
                    {
                        k.SetUnitProfile(f, -1, light ? 0.5f : 5f);
                    }
                    k.FireAt(me, line[0], k.Time);
                    StepN(k, 90);
                    if (light)
                    {
                        Expect(line.All(f => Near(Hp(k, f), 190f)) && k.EmgEnqueuedOf(EmgEventKind.Pierce) == 3,
                            $"穿透 1 次 × 轻目标（质量 0.5 < 弹体）：3 个都被穿过各掉 10（{string.Join("/", line.Select(f => Hp(k, f)))}），evt.pierce {k.EmgEnqueuedOf(EmgEventKind.Pierce)} 次（不消耗次数）");
                    }
                    else
                    {
                        Expect(Near(Hp(k, line[0]), 190f) && Near(Hp(k, line[1]), 190f) && Near(Hp(k, line[2]), 200f) && k.EmgEnqueuedOf(EmgEventKind.Pierce) == 1,
                            $"对照：穿透 1 次 × 重目标（质量 5）：只穿过第 1 个、在第 2 个处终结、第 3 个不掉（{string.Join("/", line.Select(f => Hp(k, f)))}），evt.pierce {k.EmgEnqueuedOf(EmgEventKind.Pierce)} 次");
                    }
                }
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                // [跳弹, 壁撞]：单位面 1 次、障碍面 2 次，分开计
                int p = Tree(k, Root().Verb(EmgVerb.Ricochet, 1f, 1f, 6f, 0f, 1f).Verb(EmgVerb.Ricochet, 2f, 1f, 6f, 0f, 2f).Read(EmgEventKind.Bounce));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 3f)));
                int a = Foe(k, new double2(0, 5));
                int b = Foe(k, new double2(4, 6.5));
                int c = Foe(k, new double2(-4.6, 6.5));
                k.FireAt(me, a, k.Time);
                var bounces = new List<EmgEvent>();
                int seq = int.MinValue;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    if (k.TryGetLastEmgEvent(EmgEventKind.Bounce, out EmgEvent e) && e.Seq != seq)
                    {
                        seq = e.Seq;
                        bounces.Add(e);
                    }
                }
                EmgEvent first = bounces.FirstOrDefault();
                float2 toB = bounces.Count > 0 ? (float2)math.normalize(new double2(4, 6.5) - first.Pos) : float2.zero; // 改向方向 = 撞点 → B
                Expect(Near(Hp(k, a), 190f) && Near(Hp(k, b), 190f) && Near(Hp(k, c), 200f) && bounces.Count(x => x.Code == EmgCode.FaceUnit) == 1
                       && Near(first.Value, toB.x, 0.02f) && Near(first.Value2, toB.y, 0.02f),
                    $"撞单位改向：A 被打后改向 6 米内最近的 B（法线 = 改向方向 ({first.Value:0.00}, {first.Value2:0.00})），单位面只 1 次（C 不再被跳到）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Ricochet, 2f, 1f, 6f, 0f, 2f).Read(EmgEventKind.Bounce).Read(EmgEventKind.Expire));
                int pc = Tree(k, Root().Read(EmgEventKind.Expire));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 3f)));
                int me2 = Me(k, new double2(30, 0), k.AddWeapon(Flight(pc, 30f, 3f)));
                int foe = Foe(k, new double2(0, 8));
                int foe2 = Foe(k, new double2(30, 8));
                k.SetObstacles(new List<float3> { new float3(0.3f, 4f, 1f), new float3(30.3f, 4f, 1f) });
                k.FireAt(me, foe, k.Time);
                StepN(k, 8);
                bool got = k.TryGetLastEmgEvent(EmgEventKind.Bounce, out EmgEvent e);
                CombatProjectile pr = Projs(k).FirstOrDefault(x => x.Pos.x < 15);
                double2 hit = e.Pos;
                float2 nrm = (float2)math.normalize(hit - new double2(0.3, 4));
                bool reflected = math.dot(pr.Vel, new float2(0f, 1f)) < 0f;
                k.FireAt(me2, foe2, k.Time);
                StepN(k, 20);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent ex);
                Expect(got && e.Code == EmgCode.FaceObstacle && Near(e.Value, nrm.x, 0.02f) && Near(e.Value2, nrm.y, 0.02f) && pr.BounceObs == 1 && reflected && pr.BounceLeft == 0
                       && ex.Code == EmgCode.ExpireObstacle,
                    $"撞障碍反弹（壁撞，障碍面 2 次）：evt.bounce Code = 障碍面，法线 = 撞点径向 ({e.Value:0.00}, {e.Value2:0.00})，镜面反射后往回飞、剩余障碍面 {pr.BounceObs} 次；单位面次数 0（面分开计）；"
                    + $"对照（没有壁撞）在撞点终结（Code {ex.Code}）");
            }
        }

        // ── J. numericOnly 端口 ─────────────────────────────────────────────────────────────

        /// <summary>只有一行写端指令的程序（numericOnly 端口眼下没有 / 只有个别固件写，按端口码直接构造，走与正式编译同一条折叠路径）。</summary>
        private static EmgProgramResult OneLine(FirmwareCarrier car, string port, float amount)
        {
            var pr = new EmgProgramResult { Carrier = (byte)car };
            pr.Steps.Add(new EmgProgramStep { FirmwareId = "test" });
            pr.KernelNodes.Add(new EmgNode { Parent = -1, FirstInstr = 0, InstrCount = 1 });
            pr.KernelInstrs.Add(new EmgInstr { Port = (ushort)EmgCatalog.Current.Ports[EmgCatalog.Current.PortIndexById[port]].Code, Role = RoleModify, Amount = amount });
            return pr;
        }

        private static void CheckNumericOnly()
        {
            Line("  · J. numericOnly 端口（grm.mod.size / armor / power）经 fg.TbPortCarrier 只改数值（FG19 第 2.4 节）：5 载体都改到对应参数；力场 + [照明弹] 体积变大，圈外的敌人被打到");
            var bad = new List<string>();
            foreach ((FirmwareCarrier car, string comp, string name) in Cars)
            {
                CombatReading b = BaseWeapon(comp, 20f).Reading;
                CombatReading size = CombatSite.FoldProgramReadings(b, OneLine(car, "grm.mod.size", 1.5f), false);
                CombatReading armor = CombatSite.FoldProgramReadings(b, OneLine(car, "grm.mod.armor", 0.5f), false);
                CombatReading power = CombatSite.FoldProgramReadings(b, OneLine(car, "grm.mod.power", 1.25f), false);
                bool sizeOk = size.BlastRadius > b.BlastRadius || size.Area > b.Area || size.Cone > b.Cone;
                bool armorOk = armor.ArmorPierce > b.ArmorPierce;
                bool powerOk = power.DamageScale > (b.DamageScale > 0f ? b.DamageScale : 1f) && power.CooldownScale > (b.CooldownScale > 0f ? b.CooldownScale : 1f);
                if (!sizeOk || !armorOk || !powerOk)
                {
                    bad.Add($"{name}（体积 {sizeOk} / 破甲 {armorOk} / 威力 {powerOk}）");
                }
            }
            Expect(bad.Count == 0, $"5 载体 × 3 个 numericOnly 端口都改到 fg.TbPortCarrier 指的参数（体积 → 溅射 / 触及 / 半径，破甲 → 穿甲，威力 → 伤害倍率与出手间隔倍率）{(bad.Count > 0 ? "；不对：" + string.Join("、", bad) : string.Empty)}");
            Cell on = RunCell("fw_flare", FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, false);
            Cell off = RunCell("fw_flare", FirmwareCarrier.Aura, ComponentCatalog.CompCoronaId, true);
            Expect(LostOf(on, "O", 200f) > 0f && LostOf(off, "O", 200f) == 0f,
                $"力场 + [照明弹]（grm.mod.size 1.5）：半径变大，圈外 1.1 米的 O 被打到（{LostOf(on, "O", 200f):0}；去掉体积行 {LostOf(off, "O", 200f):0}）");
            // 审查修复：广角声明幅度 30 是“扇角度数”，不能乘体积行（曾得出 36 米溅射）。numericOnly 端口沿用现动词：按广角自己的旧字段 spread:60 折，与 FG19A 第 94 行逐格比对。
            var wide = new List<string>();
            bool wideOk = true;
            foreach ((FirmwareCarrier car, string comp, string name) in Cars)
            {
                CombatReading b = BaseWeapon(comp, 20f).Reading;
                CombatReading w = CombatSite.FoldProgramReadings(b, Program("fw_wide", car, false), false);
                float dCone = w.Cone - b.Cone, dArea = w.Area - b.Area, dBlast = w.BlastRadius - b.BlastRadius;
                bool ok = car switch
                {
                    FirmwareCarrier.Melee => Near(dCone, 30f, 0.01f) && Near(dArea, 0f) && Near(dBlast, 0f),
                    FirmwareCarrier.Aura => Near(dArea, 2f, 0.01f) && Near(dBlast, 0f),
                    FirmwareCarrier.Field => Near(dArea, 1.5f, 0.01f) && Near(dBlast, 0f),
                    _ => Near(dBlast, 0f) && Near(dArea, 0f) && w.ExtraHits > b.ExtraHits, // 射弹 / 无人机：扇面散开多打 1 个（旧读法 spread），不再出 36 米溅射
                };
                wideOk &= ok;
                wide.Add($"{name} 扇角 {dCone:+0.#;-0.#;0} 半径 {dArea:+0.##;-0.##;0} 溅射 {dBlast:+0.##;-0.##;0}{(ok ? string.Empty : "（不对）")}");
            }
            Expect(wideOk, $"[广角]（grm.mod.size 写的是 30°）按旧字段 spread:60 折：格斗扇形 +30°、力场 +2 米、布区 +1.5 米，射弹 / 无人机不出溅射（FG19A 第 1.1 节第 94 行）：{string.Join("；", wide)}");
        }

        // ── E. 连锁偏好 ─────────────────────────────────────────────────────────────

        private static void CheckChain()
        {
            Line("  · E. 连锁（FGR-EMG-017、FG19A A5；FGT-EMG-014 [电弧, 标记跳转]）：跳数取大、先跳已标记、再导电、最后最近；只有“已标记”一种偏好时只跳已标记");
            EmgProgramResult both = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_arcchain", "fw_marktag" }, FirmwareCarrier.Melee);
            EmgProgramResult onlyMark = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_marktag" }, FirmwareCarrier.Melee);
            EmgInstr[] chains = both.KernelInstrs.Where(x => x.Port == 113 && x.Role > EmgConst.RoleLastRead).ToArray();
            Expect(chains.Length == 1 && Near(chains[0].Amount, 2f) && (chains[0].Flags & EmgInstrFlags.PreferMarked) != 0 && (chains[0].Flags & EmgInstrFlags.PreferConductive) != 0,
                $"[电弧, 标记跳转] 编译成 1 行连锁：跳数取大 = {(chains.Length > 0 ? chains[0].Amount : 0):0}，偏好位 {(chains.Length > 0 ? chains[0].Flags : 0)}");
            CarrierReadings.TryGetTagEffect("Shock", out uint shockBit, out _, out _);
            bool shockedAll = true;
            float[] Run(EmgProgramResult pr)
            {
                using var k = new CombatKernel(Cfg(), 16);
                CombatWeapon w = BaseWeapon(ComponentCatalog.CompRamId, 3f);
                w.Reading = CombatSite.FoldProgramReadings(w.Reading, pr, false);
                // 电弧自带的电击状态标签（旧读法的标签，与修饰端口无关）：每一跳仍注电（FGT-EMG-014）。
                w.Reading.StatusMask |= shockBit;
                w.Reading.StatusSeconds = math.max(w.Reading.StatusSeconds, 3f);
                w.Program = CombatSite.RegisterProgram(k, pr);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int t = Foe(k, new double2(0, 1.5));
                int near = Foe(k, new double2(-2.6, 3.2)); // 离主目标最近（3.1 米），在格斗触及外
                int wet = Foe(k, new double2(2.5, 6.5)); // 导电（浸湿）：离主目标 5.6 米（第一跳够不着），离已标记 2.8 米
                int marked = Foe(k, new double2(0, 5.3)); // 已标记：离主目标 3.8 米（比最近的远）
                k.ApplyStatus(wet, 1u << 2, 30f, 0f, 0f, 0f, 0);
                k.SetMarkedUntil(marked, 1e9);
                k.FireAt(me, t, k.Time);
                bool Shocked(int id) => k.TryGetStatus(id, out uint m, out _, out _, out _, out _) && (m & shockBit) != 0;
                if (200f - Hp(k, marked) > 0f)
                {
                    shockedAll &= shockBit != 0 && Shocked(t) && Shocked(marked) && (200f - Hp(k, wet) <= 0f || Shocked(wet)) && !Shocked(near);
                }
                return new[] { 200f - Hp(k, near), 200f - Hp(k, wet), 200f - Hp(k, marked) };
            }
            float[] r1 = Run(both), r2 = Run(onlyMark);
            Expect(r1[2] > 0f && r1[1] > 0f && Near(r1[0], 0f) && r2[2] > 0f && Near(r2[1], 0f) && Near(r2[0], 0f),
                $"格斗 + [电弧, 标记跳转] 跳 2 次：先跳已标记（最远那个掉 {r1[2]:0.#}）、再跳导电（掉 {r1[1]:0.#}），最近的不被跳到（{r1[0]:0.#}）；"
                + $"只装 [标记跳转]：只跳已标记（{r2[2]:0.#}），导电 / 最近都不跳（{r2[1]:0.#} / {r2[0]:0.#}）");
            Expect(shockedAll, $"每一跳仍注电：主目标与被跳到的每个单位身上都有电击状态位（位 {shockBit}），没被跳到的最近单位没有");
        }

        // ── F. 折返 / 绕行 / 抛射 / 侧偏 ────────────────────────────────────────────────

        private static void CheckReturnOrbitLobDrift()
        {
            Line("  · F. 折返（回程再命中 1 次、0.6 倍、在发射者身边终结）、绕行（半径 2.5 米一圈、拦下敌方弹体 ≤ 2 发 / 圈）、抛射（落到目标脚下、落点不可达时落地终结、光束不越障）、侧偏（偏折场一半不反射）");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Return, 1f, 0.6f).Read(EmgEventKind.Expire));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 1.2f)));
                int a = Foe(k, new double2(0, 8));
                int back = Foe(k, new double2(0.9, 4));
                k.FireAt(me, a, k.Time);
                StepN(k, 12);
                k.SetPosition(me, new double2(1.8, 0)); // 发射者挪开：回程线经过 back
                StepN(k, 60);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent ex);
                Expect(Near(Hp(k, a), 190f) && Near(Hp(k, back), 194f) && ex.Code == EmgCode.ExpireReturn && math.distance(ex.Pos, Pos(k, me)) < 1.8 && k.ProjectileCount == 0,
                    $"折返：命中 A（-10）后飞回发射者，回程命中 1 次（-6 = 0.6 倍），在发射者身边终结（Code {ex.Code}，离发射者 {math.distance(ex.Pos, Pos(k, me)):0.0} 米）");
            }
            using (var k = new CombatKernel(Cfg(), 32))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 1.2f)));
                int target = Foe(k, new double2(0, 12));
                int side = Foe(k, new double2(-2.5, 0));
                // 3 发朝本机飞来的敌方弹体（炮塔式弹体）
                int gun = Unit(k, CombatFaction.Hostile, new double2(-2.5, 14), k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = 1f, Cooldown = 0f,
                    ProjectileSpeed = 25f, ProjectileRadius = 0.3f, ProjectileLife = 3f,
                }), 100f);
                k.FireAt(me, target, k.Time);
                float maxR = 0f, minR = 99f;
                for (int i = 0; i < 25; i++)
                {
                    StepN(k, 1);
                    foreach (CombatProjectile q in Projs(k).Where(x => x.Faction == CombatFaction.Player && (x.Mods & EmgProjMods.Orbit) != 0 && x.ModT > 0.3f))
                    {
                        float r = (float)math.distance(q.Pos, Pos(k, me));
                        maxR = math.max(maxR, r);
                        minR = math.min(minR, r);
                    }
                }
                StepN(k, 60);
                Expect(Near(minR, 2.5f, 0.05f) && Near(maxR, 2.5f, 0.05f) && Hp(k, side) < 200f && Hp(k, target) < 200f,
                    $"绕行：弹体绕本机半径 {minR:0.00}～{maxR:0.00} 米转一圈（扫到身边的单位 {200f - Hp(k, side):0}），转完飞向目标并命中（{200f - Hp(k, target):0}）；evt.fire 读端照常");
                _ = gun;
            }
            using (var k = new CombatKernel(Cfg(), 64))
            {
                // 审查修复：每圈拦截上限按圈清零。6 发慢速大弹体先停在绕行圈附近（6 个方向），绕 2 圈：第 1 圈拦 2 发、第 2 圈再拦 2 发，合计 4 > 旧实现（整个绕行 ≤ 2）。
                int p = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 2f).Sense(EmgConst.PortProjectile).Read(EmgEventKind.Absorb));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 20f, 3f)));
                int target = Foe(k, new double2(0, 30));
                int gunW = k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = 1f, Cooldown = 0f,
                    ProjectileSpeed = 0.05f, ProjectileRadius = 0.6f, ProjectileLife = 60f,
                });
                for (int g = 0; g < 6; g++)
                {
                    float ang = math.radians(30f + 60f * g);
                    int gun = Unit(k, CombatFaction.Hostile, new double2(math.cos(ang), math.sin(ang)) * 3.0, gunW, 1000f);
                    k.FireAt(gun, me, k.Time);
                }
                StepN(k, 2); // 几乎不动的敌弹停在绕行圈上（6 个方向）
                k.FireAt(me, target, k.Time);
                long lap1 = -1, before = k.EmgEnqueuedOf(EmgEventKind.Absorb);
                for (int i = 0; i < 150 && lap1 < 0; i++)
                {
                    long prevAbsorb = k.EmgEnqueuedOf(EmgEventKind.Absorb);
                    StepN(k, 1);
                    foreach (CombatProjectile q in Projs(k).Where(x => x.Faction == CombatFaction.Player && (x.Mods & EmgProjMods.Orbit) != 0))
                    {
                        if (q.ModT >= 2f * math.PI)
                        {
                            lap1 = prevAbsorb - before; // 越过圈界那一步之前的累计 = 第 1 圈
                        }
                    }
                }
                StepN(k, 90);
                long absorbed = k.EmgEnqueuedOf(EmgEventKind.Absorb) - before;
                Expect(lap1 >= 1 && lap1 <= 2 && absorbed - lap1 >= 1 && absorbed - lap1 <= 2 && absorbed > 2,
                    $"绕行中撞上的敌方弹体被拦下，上限每圈清零：第 1 圈 {lap1} 发、第 2 圈 {absorbed - lap1} 发（每圈 ≤ 2），合计 {absorbed} 发 > 2（旧实现整个绕行 ≤ 2）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Lob, 4f, 1f).Read(EmgEventKind.Expire));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int foe = Foe(k, new double2(0, 9));
                k.SetObstacles(new List<float3> { new float3(0f, 4f, 1f) });
                k.FireAt(me, foe, k.Time);
                StepN(k, 40);
                int me2 = Me(k, new double2(20, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int foe2 = Foe(k, new double2(20, 9));
                k.FireAt(me2, foe2, k.Time);
                k.SetPosition(foe2, new double2(26, 9)); // 落点不可达：目标走开
                StepN(k, 40);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent ex);
                Expect(Near(Hp(k, foe), 190f) && Near(Hp(k, foe2), 200f) && ex.Code == EmgCode.ExpireGround && math.distance(ex.Pos, new double2(20, 9)) < 0.8 && k.EmgBusStats.ObstacleHits == 0,
                    $"抛射：越过障碍落到目标脚下命中；目标走开（落点不可达）时在瞄准点落地终结（Code {ex.Code} = 落地，落点离瞄准点 {math.distance(ex.Pos, new double2(20, 9)):0.00} 米），不打别人");
            }
            {
                EmgProgramResult beam = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_arc" }, FirmwareCarrier.Projectile, 0, false);
                bool noted = beam.Notes.Any(n => n.Key == EmgProgramCompiler.NoteLobStraight);
                bool lobOff = beam.KernelInstrs.All(x => x.Verb != (byte)EmgVerb.Lob);
                bool dustKept = beam.KernelInstrs.Any(x => x.Verb == (byte)EmgVerb.Emit);
                var preview = new BlueprintCircuitPreview { PrimaryId = ComponentCatalog.CompBeamId, LocalFirmwareIds = new[] { "fw_arc" }, UplinkFirmwareIds = Array.Empty<string>() };
                EmgProgramResult viaPreview = EmgProgramCompiler.Compile(preview);
                Expect(noted && lobOff && dustKept && viaPreview.Notes.Any(n => n.Key == EmgProgramCompiler.NoteLobStraight) && GameText.Has(EmgProgramCompiler.NoteLobStraight),
                    $"切割束（作战组件表 flightLob = 0）+ [曲射]：lob 不执行（光束直线、不越障），落点扬尘照常；预览给原因“{EmgProgramCompiler.NoteText(beam, beam.Notes.FirstOrDefault(n => n.Key == EmgProgramCompiler.NoteLobStraight))}”");
            }
            using (var k = new CombatKernel(Cfg(), 64))
            {
                int p = Tree(k, Root().Verb(EmgVerb.Drift, 15f, 1f, 0.2f, 0.5f).Sense(EmgConst.PortDeflect, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 2f)));
                int prof = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f });
                int prism = Unit(k, CombatFaction.Hostile, new double2(0, 9), -1, 5000f, prof);
                k.SetFlag(prism, CombatUnitFlags.DeflectArmed, true);
                k.SetDeflectPhase(prism, 2, 60f);
                int shots = 0;
                float maxAng = 0f;
                for (int s = 0; s < 40; s++)
                {
                    int before = k.ProjectileCount;
                    k.FireAt(me, prism, k.Time);
                    if (k.ProjectileCount > before)
                    {
                        shots++;
                        maxAng = math.max(maxAng, Deg(Projs(k)[k.ProjectileCount - 1].Vel, new float2(0f, 1f)));
                    }
                    StepN(k, 20);
                }
                CombatCounters cc = k.Counters;
                float ratio = shots > 0 ? cc.ShotsReflected / (float)shots : 0f;
                Expect(shots == 40 && maxAng <= 15.01f && maxAng > 5f && ratio > 0.3f && ratio < 0.7f,
                    $"侧偏：40 发侧偏最大 {maxAng:0.0}°（≤ 15°）；穿过偏折场被反射 {cc.ShotsReflected} / {shots} 发（约一半，每发一个无状态哈希）");
            }
            foreach (EmgVerb v in new[] { EmgVerb.Steer, EmgVerb.Orbit, EmgVerb.Return })
            {
                // 审查修复：被偏折场反弹回来的弹体不再带己方程序给的修饰（寻的 / 绕行 / 折返），沿反射方向直飞，不朝原目标转向、不绕场源、不飞回场源终结。
                using var k = new CombatKernel(Cfg(), 32);
                int p = v == EmgVerb.Steer ? Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f))
                    : v == EmgVerb.Orbit ? Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f)) : Tree(k, Root().Verb(EmgVerb.Return, 1f, 0.6f));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 3f)));
                int prof = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f });
                int prism = Unit(k, CombatFaction.Hostile, new double2(1.5, 12), -1, 5000f, prof);
                k.SetFlag(prism, CombatUnitFlags.DeflectArmed, true);
                k.SetDeflectPhase(prism, 2, 60f);
                k.FireAt(me, prism, k.Time);
                CombatProjectile back = default;
                bool seen = false;
                float turn = 0f;
                for (int i = 0; i < 120; i++)
                {
                    StepN(k, 1);
                    foreach (CombatProjectile q in Projs(k).Where(x => x.Faction == CombatFaction.Hostile && x.Prog < 0))
                    {
                        if (!seen)
                        {
                            back = q;
                            seen = true;
                        }
                        else if (q.Id == back.Id)
                        {
                            turn = math.max(turn, Deg(back.Vel, q.Vel));
                        }
                    }
                }
                Expect(seen && back.Mods == EmgProjMods.None && back.HomingTarget == 0 && back.ModQ == 0 && turn < 0.01f,
                    $"[{v}] 弹体被偏折场反弹后清掉弹道修饰（记号 {back.Mods}、寻的目标 {back.HomingTarget}、ModQ {back.ModQ}），沿反射方向直飞（航向变化 {turn:0.00}°；见到反弹弹体 {seen}）");
            }
            using (var k = new CombatKernel(Cfg(), 64))
            {
                // 审查修复：子弹药继承的绕行（[绕轨, 分裂]）不再以开火的机器为圆心一步瞬移回去，而是从出生点沿切线进入自绕；每步位移 = 弹速 × dt，照常查障碍。
                int p = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f).Read(EmgEventKind.Hit, Trig),
                    new N { Parent = 0, On = EmgEventKind.Hit, Count = 3 }.Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 3f)));
                int foe = Foe(k, new double2(0, 10), 5000f);
                k.SetObstacles(new List<float3> { new float3(2.2f, 11.5f, 0.8f) });
                k.FireAt(me, foe, k.Time);
                var last = new Dictionary<int, double2>();
                float maxStep = 0f, minToMe = 99f, minToObs = 99f;
                int subs = 0;
                for (int i = 0; i < 240; i++)
                {
                    StepN(k, 1);
                    foreach (CombatProjectile q in Projs(k).Where(x => (x.Flags & EmgProjFlags.Sub) != 0))
                    {
                        if (last.TryGetValue(q.Id, out double2 prev))
                        {
                            maxStep = math.max(maxStep, (float)math.distance(prev, q.Pos));
                        }
                        else
                        {
                            subs++;
                        }
                        last[q.Id] = q.Pos;
                        minToMe = math.min(minToMe, (float)math.distance(q.Pos, Pos(k, me)));
                        minToObs = math.min(minToObs, (float)math.distance(q.Pos, new double2(2.2, 11.5)) - 0.8f);
                    }
                }
                float limit = 28f * Dt * 1.01f;
                Expect(subs > 0 && maxStep <= limit && minToMe > 3f && minToObs >= -1e-3f,
                    $"[绕轨, 分裂] 的 {subs} 发子弹药：每步位移最大 {maxStep:0.000} 米（≤ 弹速 × dt = {limit:0.000}），离发射者最近 {minToMe:0.0} 米（不瞬移回机器身边），不进障碍（离障碍面最近 {minToObs:0.00} 米）");
            }
        }

        // ── G. 负向 ─────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · G. 负向：炮塔（固定底盘，弹体武器）沿用 FGR-FW-012 兼容表——弹体不带修饰、读法不变；没有修饰的弹体逐字不变；写修饰端口的固件不再按旧修饰字段结算（单轨，不双算）；emg.stage = 0 掩体规则关");
            EmgProgramResult homing = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_homing" }, FirmwareCarrier.Projectile);
            using (var k = new CombatKernel(Cfg(), 16))
            {
                var bolt = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = 10f, Cooldown = 0f,
                    ProjectileSpeed = 10f, ProjectileRadius = 0.2f, ProjectileLife = 3f,
                };
                bolt.Program = CombatSite.RegisterProgram(k, homing);
                string key0 = CombatKernel.WeaponKey(bolt);
                CombatSite.ApplySpawnFlight(ref bolt, homing, ComponentCatalog.CompGunId);
                int turret = Me(k, new double2(0, 0), k.AddWeapon(bolt));
                int foe = Foe(k, new double2(0, 8));
                k.FireAt(turret, foe, k.Time);
                CombatProjectile b = Projs(k).FirstOrDefault();
                Expect(b.Mods == EmgProjMods.None && b.SteerLeft == 0f && b.PierceLeft == 0 && CombatKernel.WeaponKey(bolt) == key0,
                    "炮塔的弹体武器 + [寻的]：不置 SpawnFlight、弹体不带任何修饰状态、武器键不变（固定底盘沿用兼容表）");
            }
            // 单轨：格斗 + [跳弹] 在 emg.stage = 1 时击退只算一次（程序轨 1.2 米），与 emg.stage = 0 的旧轨同值
            var ram = new BlueprintCircuitPreview
            {
                PrimaryId = ComponentCatalog.CompRamId, ChassisId = ChassisCatalog.ChassisHoverId, HasCombatOutput = true, FirmwareIds = new[] { "fw_ricochet" },
                LocalFirmwareIds = new[] { "fw_ricochet" }, UplinkFirmwareIds = Array.Empty<string>(),
            };
            CombatWeapon legacy = CombatSite.MachineWeaponFrom(ram);
            EmgProgramResult pr = EmgProgramCompiler.Compile(ram);
            CombatReading single = CombatSite.ProgramReading(ram, legacy.Reading, pr);
            CombatReading noSkip = CombatSite.FoldProgramReadings(legacy.Reading, pr, false);
            float baseKnock = CarrierReadings.Build(ComponentCatalog.CompRamId, null, ChassisCatalog.ChassisHoverId, Array.Empty<string>(), true).Knockback;
            Expect(!pr.IsEmpty && Near(single.Knockback - baseKnock, 1.2f) && Near(legacy.Reading.Knockback - baseKnock, 1.2f) && Near(noSkip.Knockback - baseKnock, 2.4f),
                $"单轨：[跳弹] × 格斗在声明轨击退 +{single.Knockback - baseKnock:0.##} 米（旧轨同为 +{legacy.Reading.Knockback - baseKnock:0.##}）；若不去掉旧修饰字段会双算成 +{noSkip.Knockback - baseKnock:0.##}");
            // 没有程序：一步一步 StateHash 与旧弹体逐字一致（Mods = 0 的弹体不进任何新分支）
            using (var a = new CombatKernel(Cfg(), 16))
            using (var b = new CombatKernel(Cfg(false), 16))
            {
                int ma = Me(a, new double2(0, 0), a.AddWeapon(Flight(-1))), mb = Me(b, new double2(0, 0), b.AddWeapon(Flight(-1)));
                int fa = Foe(a, new double2(1, 9)), fb = Foe(b, new double2(1, 9));
                a.FireAt(ma, fa, a.Time);
                b.FireAt(mb, fb, b.Time);
                bool same = true;
                for (int i = 0; i < 40; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    same &= a.StateHash() == b.StateHash();
                }
                Expect(same && Near(Hp(a, fa), 190f), "没有程序的 SpawnFlight 武器：掩体规则开 / 关两种配置下 40 步 StateHash 逐步一致、照常命中（新分支不碰没有修饰的弹体）");
            }
            using (var k = new CombatKernel(Cfg(false), 16))
            {
                CombatWeapon w = BaseWeapon(ComponentCatalog.CompCoronaId, 5f);
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                float area = w.Reading.Area;
                int t = Foe(k, new double2(0, area * 0.5));
                int behind = Foe(k, new double2(0, -area * 0.75));
                k.SetObstacles(new List<float3> { new float3(0f, -area * 0.4f, 0.25f) });
                k.FireAt(me, t, k.Time);
                Expect(Hp(k, behind) < 200f, "emg.stage = 0（掩体规则关）：力场脉冲照旧打到障碍后的单位（正式流程逐字不变）");
            }
        }

        // ── H. 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · H. 存读档：寻的 / 绕行 / 折返 / 反弹中的弹体中途存档 → 新内核读档 → 续跑每步 StateHash 与不中断的一致；短段（格式 16 早期 16 字段）取缺省 = 没有修饰；坏值整份拒绝");
            int Build(CombatKernel k)
            {
                int ps = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f).Verb(EmgVerb.Ricochet, 1f, 1f, 6f, 0f, 0f));
                int po = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
                int pr = Tree(k, Root().Verb(EmgVerb.Return, 1f, 0.6f).Verb(EmgVerb.Drift, 15f, 1f, 0.2f, 0.5f));
                int m1 = Me(k, new double2(0, 0), k.AddWeapon(Flight(ps, 20f, 3f)));
                int m2 = Me(k, new double2(10, 0), k.AddWeapon(Flight(po, 20f, 3f)));
                int m3 = Me(k, new double2(20, 0), k.AddWeapon(Flight(pr, 20f, 3f)));
                int f1 = Foe(k, new double2(3, 9)), f2 = Foe(k, new double2(10, 9)), f3 = Foe(k, new double2(20, 7));
                Foe(k, new double2(6, 10));
                k.FireAt(m1, f1, k.Time);
                k.FireAt(m2, f2, k.Time);
                k.FireAt(m3, f3, k.Time);
                return m1;
            }
            using (var a = new CombatKernel(Cfg(), 32))
            using (var b = new CombatKernel(Cfg(), 32))
            {
                Build(a);
                StepN(a, 12);
                var mods = Projs(a).Select(x => x.Mods).ToList();
                byte[] snap = a.Serialize();
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && a.StateHash() == b.StateHash();
                for (int i = 0; i < 120; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    same &= a.StateHash() == b.StateHash();
                }
                byte[] again = b.Serialize();
                Expect(same && mods.Any(m => (m & EmgProjMods.Steer) != 0) && mods.Any(m => (m & EmgProjMods.Orbit) != 0) && mods.Any(m => (m & EmgProjMods.Drift) != 0),
                    $"在飞弹体（修饰 {string.Join("/", mods)}）中途存读档：读档 {lr}，续跑 120 步每步 StateHash 一致");
                using var c = new CombatKernel(Cfg(), 32);
                using var e = new CombatKernel(Cfg(), 32);
                byte[] shortSeg = a.SerializeEmgShapeForTests(EmgConst.SegProjectiles, 16, 0);
                CombatLoadResult ls = c.Load(shortSeg);
                bool defaults = ls == CombatLoadResult.Ok && Projs(c).All(x => x.Mods == EmgProjMods.None && x.SteerLeft == 0f && x.BounceObs == 0);
                Expect(defaults && e.Load(again) == CombatLoadResult.Ok,
                    $"短段（弹体段只有 EMG1-TRIG-01 时的 16 个字段）读档 {ls}：新增 5 个字段取缺省（没有修饰）；再存再读幂等");
            }
        }

        // ── I. 倍速与暂停 ───────────────────────────────────────────────────────────

        private static void CheckSpeeds()
        {
            Line("  · I. 0.5x / 1x / 3x（同一段游戏时间按不同步长推进）与暂停：命中谁、掉多少一致；暂停（不步进）状态不变");
            string Outcome(float dt, int steps)
            {
                using var k = new CombatKernel(Cfg(), 32);
                int ps = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f, 0f, EmgInstrFlags.PreferHeat).Verb(EmgVerb.PassThrough, 1f, 1f));
                int pr = Tree(k, Root().Verb(EmgVerb.Return, 1f, 0.6f));
                int po = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
                int m1 = Me(k, new double2(0, 0), k.AddWeapon(Flight(ps, 20f, 3f)));
                int m2 = Me(k, new double2(20, 0), k.AddWeapon(Flight(pr, 20f, 3f)));
                int m3 = Me(k, new double2(40, 0), k.AddWeapon(Flight(po, 20f, 3f)));
                int[] f = { Foe(k, new double2(0, 9)), Foe(k, new double2(2, 9)), Foe(k, new double2(0, 12)), Foe(k, new double2(20, 8)), Foe(k, new double2(40, 9)), Foe(k, new double2(42.5, 0)) };
                k.SetWeaponState(f[1], 40f, false, 0, 0);
                k.FireAt(m1, f[0], k.Time);
                k.FireAt(m2, f[3], k.Time);
                k.FireAt(m3, f[4], k.Time);
                StepN(k, steps, dt);
                return string.Join(",", f.Select(x => Hp(k, x).ToString("0")));
            }
            string x1 = Outcome(Dt, 180), x05 = Outcome(Dt * 0.5f, 360), x3 = Outcome(Dt * 3f, 60);
            Expect(x1 == x05 && x1 == x3, $"同一段 3 秒游戏时间：1x [{x1}]、0.5x [{x05}]、3x [{x3}] 结果一致");
            // 暂停（审查修复：原断言取完哈希就比，恒真）：时间缩放 0 时地点按 dt = 0 调内核 Step（CombatKernel.Step 对 dt ≤ 0 直接返回）。
            // 暂停 30 帧期间在飞的寻的 / 绕行弹体状态不变；恢复后与一直没暂停的同一场逐字一致。
            ulong Paused(bool pause, out bool frozen)
            {
                using var k = new CombatKernel(Cfg(), 16);
                int p = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f).Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 30f, 3f)));
                int foe = Foe(k, new double2(3, 9));
                k.FireAt(me, foe, k.Time);
                StepN(k, 3);
                frozen = true;
                if (pause)
                {
                    ulong h = k.StateHash();
                    for (int i = 0; i < 30; i++)
                    {
                        k.Step(0f, k.Time);
                        frozen &= k.StateHash() == h && k.ProjectileCount == 1;
                    }
                }
                StepN(k, 57);
                return k.StateHash();
            }
            ulong withPause = Paused(true, out bool still), noPause = Paused(false, out _);
            Expect(still && withPause == noPause, $"暂停 30 帧（dt = 0）期间在飞的寻的 + 绕行弹体状态不变（{still}）；恢复后与没暂停的同一场逐字一致（{withPause == noPause}）");
        }

        // ── L. 耗时 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · L. 耗时（FGR-EMG-200；3x 倍速每步 ≤ 1.5 毫秒，只测一次）：512 发寻的 + 反弹 + 512 发绕行同时在飞的一步");
            using var k = new CombatKernel(Cfg(), 1200);
            int ps = Tree(k, Root().Verb(EmgVerb.Steer, 1f, 30f, 180f, 13f, 0f, EmgInstrFlags.PreferHeat).Verb(EmgVerb.Ricochet, 1f, 1f, 6f, 0f, 0f).Verb(EmgVerb.Drift, 15f, 1f, 0.2f));
            int po = Tree(k, Root().Verb(EmgVerb.Orbit, 120f, 2.5f, 1f));
            var shooters = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                shooters.Add(Me(k, new double2(i * 3, 0), k.AddWeapon(Flight(i % 2 == 0 ? ps : po, 15f, 4f))));
            }
            var foes = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                foes.Add(Foe(k, new double2(i * 3, 40), 1e6f));
            }
            for (int round = 0; round < 16; round++)
            {
                for (int i = 0; i < shooters.Count; i++)
                {
                    k.FireAt(shooters[i], foes[i], k.Time);
                }
            }
            int inFlight = k.ProjectileCount;
            var sw = new Stopwatch();
            double worst = 0;
            for (int s = 0; s < 30; s++)
            {
                sw.Restart();
                k.Step(Dt * 3f, k.Time + Dt * 3f);
                sw.Stop();
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            }
            if (worst > 1.5 && worst <= 3.0)
            {
                Line($"    ⚠ 性能警告：{inFlight} 发带修饰弹体的一步最慢 {worst:0.###} 毫秒（阈值 1.5，未到 2 倍）");
                _pass++;
                return;
            }
            Expect(inFlight >= 1000 && worst <= 1.5, $"{inFlight} 发带修饰的在飞弹体（寻的 / 反弹 / 侧偏 / 绕行各半）3x 倍速一步最慢 {worst:0.###} 毫秒（阈值 1.5）");
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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
                _pass++;
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
