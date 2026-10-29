using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-TRIG-01 触发器、子载荷与根载荷实体化的自动验收（FG19 FGR-EMG-031 运行时、034、035、064；FG19A 第 2.1.1 节；ADR-EMG-001 ③、第 7 节第 2 / 6 条；
    /// FGT-EMG-011 / 022 / 040）。起真实内核（BinGames.Sim.Combat.CombatKernel，Burst 步进）、真实编译器（EmgProgramCompiler）与真实家园地点断言行为：
    /// A 数据（子形态 5 行、作战组件实体化列、排放映射、调参）；B FGT-EMG-011 每种可触发事件一例 + 去掉触发子节点的对照组；C 格斗 / 力场只按主目标、定时从出手起算；
    /// D FGT-EMG-022 代际与链深交叉；E 顺延不丢（弹体池满 / 同链存活上限 / 份数上限）；F SpawnFlight（已发射、命中致死才回报击毁、重炮瞄准与过载、无人机、不需要飞行逐字同旧）；
    /// G 障碍 / 残骸掩体；H [分裂, 漏油] 每颗子弹药落点一摊油、子弹药不读根读法、信号离开在飞子弹药按原程序走完；I 维修 / 治疗子弹药；
    /// J FGT-EMG-040 在飞子弹药 + 顺延请求 + 区域 / 回波宿主中途存读档；K 热更层（ApplySpawnFlight、预览、文本键、引导钩子）；L 耗时；
    /// W 真实家园地点：蓝图 [分裂, 漏油] 经装配编译进内核、引导钩子、0.5x / 1x / 3x 与暂停。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgTriggerSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmgTriggerSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmgTriggerSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Trig = EmgConst.RoleTrigger;
        private const byte Cond = 2;
        private const byte RoleEmit = 5;
        private const string Bp = "bp_emg_trig_split_oil";
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/触发器与子载荷")]
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
            Line("\n[触发器与子载荷] EMG1-TRIG-01：触发子载荷（位置 / 份数 / 伤害比例 / 代际 / 链深 / 顺延）、根载荷按需实体化（SpawnFlight）、障碍与掩体、宿主（FGT-EMG-011 / 022 / 040）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckData);
                Step(CheckTriggers);
                Step(CheckMainTargetAndTimer);
                Step(CheckDepth);
                Step(CheckBackpressure);
                Step(CheckSpawnFlight);
                Step(CheckObstacles);
                Step(CheckSplitOil);
                Step(CheckRepair);
                Step(CheckSaveLoad);
                Step(CheckHotfix);
                Step(CheckPerformance);
                Step(CheckWorld);
            }
            catch (Exception e)
            {
                Fail($"触发器自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            Line($"  · [触发器与子载荷] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>程序树里的一个节点：父节点下标、挂的事件、份数、子形态（0 = 按宿主载体）、本节点的指令行。</summary>
        private sealed class N
        {
            public int Parent = -1;
            public EmgEventKind On;
            public byte Count = 1;
            public byte SubForm;
            public readonly List<EmgInstr> Instrs = new List<EmgInstr>();

            public N Read(EmgEventKind k, byte role = Trig, float p = 0f)
            {
                Instrs.Add(new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)k), Role = role, Param2 = p });
                return this;
            }

            public N Add(EmgInstr x)
            {
                Instrs.Add(x);
                return this;
            }
        }

        private static N Root() => new N();

        private static N Child(int parent, EmgEventKind on, byte count = 1, byte subForm = 0) => new N { Parent = parent, On = on, Count = count, SubForm = subForm };

        /// <summary>按节点表登记程序（与编译器同一形状：FirstInstr 连续、子节点代号 = 父 + 1）；监听掩码 = 全部事件读端 + 子节点触发码。</summary>
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
                    Parent = (sbyte)(j == 0 ? -1 : s.Parent), Gen = gen, TrigEvent = j == 0 ? default : s.On, TrigCount = j == 0 ? (byte)0 : s.Count, SubForm = s.SubForm,
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
            return k.AddProgram(new EmgProgram { ListenMask = mask }, nodes, instrs);
        }

        private static CombatConfig Cfg()
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            return c;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, float maxHp = 0f, CombatBehavior b = CombatBehavior.None, int profile = -1, float armor = 0f) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = f == CombatFaction.Player ? CombatUnitKind.Machine : CombatUnitKind.Enemy, Faction = f,
                Behavior = f == CombatFaction.Player && b == CombatBehavior.None ? CombatBehavior.Commanded : b,
                Flags = Live | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = 0.5f, Health = hp, MaxHealth = maxHp > 0f ? maxHp : hp,
                Weapon = weapon, BehaviorProfile = profile, Priority = 1, ArmorFraction = armor, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Me(CombatKernel k, double2 p, int weapon, float hp = 100f) => Unit(k, CombatFaction.Player, p, weapon, hp);

        private static int Foe(CombatKernel k, double2 p, float hp, int weapon = -1, float armor = 0f) => Unit(k, CombatFaction.Hostile, p, weapon, hp, 0f, CombatBehavior.None, -1, armor);

        private static CombatWeapon Instant(int program, float damage = 10f, bool heat = false, float range = 30f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f,
            HeatPerShot = heat ? 10f : 0f, OverheatAt = heat ? 15f : 0f, RecoverBelow = heat ? 4f : 0f, Dissipation = heat ? 10f : 0f, Program = program,
        };

        private static CombatWeapon Bolt(int program, float life = 3f, float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = damage, Cooldown = 0f,
            ProjectileSpeed = 10f, ProjectileRadius = 0.2f, ProjectileLife = life, Program = program,
        };

        private static CombatWeapon Flight(CombatWeapon w, float speed = 30f, float radius = 0.15f, float life = 1.2f)
        {
            w.SpawnFlight = 1;
            w.ProjectileSpeed = speed;
            w.ProjectileRadius = radius;
            w.ProjectileLife = life;
            return w;
        }

        private static void StepN(CombatKernel k, int n)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static List<CombatProjectile> Projectiles(CombatKernel k, EmgProjFlags flag)
        {
            var list = new List<CombatProjectile>();
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (flag == EmgProjFlags.None || (p.Flags & flag) != 0))
                {
                    list.Add(p);
                }
            }
            return list;
        }

        private static List<CombatProjectile> Subs(CombatKernel k) => Projectiles(k, EmgProjFlags.Sub);

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

        private static bool Near(double2 a, double2 b, double eps = 1e-4) => math.distance(a, b) <= eps;

        private static bool Near(float a, float b, float eps = 1e-4f) => Mathf.Abs(a - b) <= eps;

        /// <summary>步进到 Splits 变化（最多 <paramref name="guard"/> 步）；返回走了几步（没变化返回 −1）。</summary>
        private static int StepUntilSplit(CombatKernel k, int guard = 400)
        {
            long s0 = k.EmgBusStats.Splits;
            for (int i = 1; i <= guard; i++)
            {
                StepN(k, 1);
                if (k.EmgBusStats.Splits != s0)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>逐步记下处理过的事件（按 TryGetLastEmgEvent 的 Seq 变化；同一步同一种多条时只记最后一条）。</summary>
        private static List<EmgEvent> Watch(CombatKernel k, int steps, params EmgEventKind[] kinds)
        {
            var got = new List<EmgEvent>();
            var last = kinds.ToDictionary(x => x, x => k.TryGetLastEmgEvent(x, out EmgEvent e) ? e.Seq : int.MinValue);
            for (int s = 0; s < steps; s++)
            {
                StepN(k, 1);
                foreach (EmgEventKind x in kinds)
                {
                    if (k.TryGetLastEmgEvent(x, out EmgEvent e) && e.Seq != last[x])
                    {
                        last[x] = e.Seq;
                        got.Add(e);
                    }
                }
            }
            return got;
        }

        private static float2 Dir(double2 from, double2 to) => (float2)math.normalize(to - from);

        private static float2 DirOf(in CombatProjectile p) => math.lengthsq(p.Vel) > 1e-12f ? math.normalize(p.Vel) : float2.zero;

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：fg.TbSubPayload 5 行（FG19A 第 2.1.1 节）写进内核、作战组件实体化列、mat.* 排放映射到读法驻留区（阶段 1，stageGate 对齐）、emg.* 调参");
            EmgCompiled c = EmgCatalog.Current;
            var want = new Dictionary<int, (float sp, float life, float lifeR, float r, float rR)>
            {
                [1] = (28f, 0.5f, 0f, 0.1f, 0f), [2] = (20f, 0.15f, 0f, 0.3f, 0f), [3] = (24f, 0.2f, 0f, 0.1f, 0f), [4] = (0f, 0.3f, 0f, 1.2f, 0f), [5] = (0f, 0f, 0.5f, 0f, 0.5f),
            };
            var bad = new List<string>();
            foreach (EmgSubFormRow s in c.SubForms)
            {
                if (!want.TryGetValue(s.Code, out var w) || s.Carrier + 1 != s.Code || !Near(s.Speed, w.sp) || !Near(s.Life, w.life) || !Near(s.LifeRatio, w.lifeR) || !Near(s.Radius, w.r) || !Near(s.RadiusRatio, w.rR))
                {
                    bad.Add($"{s.Code}（载体 {s.Carrier} 速 {s.Speed} 寿 {s.Life}/{s.LifeRatio} 半 {s.Radius}/{s.RadiusRatio}）");
                }
            }
            using var k = new CombatKernel(Cfg(), 8);
            bool initial = Enumerable.Range(1, 5).All(i => k.EmgSubFormOf(i).Equals(EmgSubForm.Default(i)));
            CombatSite.ApplyEmgSubForms(k);
            bool applied = c.SubForms.All(s => Near(k.EmgSubFormOf(s.Code).Speed, s.Speed) && Near(k.EmgSubFormOf(s.Code).Life, s.Life) && Near(k.EmgSubFormOf(s.Code).Radius, s.Radius)
                                               && Near(k.EmgSubFormOf(s.Code).LifeRatio, s.LifeRatio) && Near(k.EmgSubFormOf(s.Code).RadiusRatio, s.RadiusRatio));
            Expect(c.SubForms.Length == 5 && bad.Count == 0 && initial && applied,
                $"子形态 5 行（码 = 载体 + 1：子弹 28 / 0.5 / 0.1、余波 20 / 0.15 / 0.3、无人机子弹 24 / 0.2 / 0.1、小场脉冲原地 0.3 秒 / 1.2 米、子区域 = 母落区 × 0.5）与 FG19A 一致{(bad.Count > 0 ? "；不符：" + string.Join("、", bad) : string.Empty)}；内核缺省 = FG19A 初值，CombatSite.ApplyEmgSubForms 写入后逐行一致");

            var comps = new (string Id, string Flight, float Speed, float Radius, int Lob, float Kinetic)[]
            {
                ("comp_gun", "yes", 30f, 0.15f, 1, 1f), ("comp_beam", "yes", 45f, 0.12f, 0, 0f), ("comp_cannon", "yes", 22f, 0.35f, 1, 2f), ("comp_repairbeam", "yes", 40f, 0.12f, 0, 0f),
                ("comp_dronebay", "yes", 30f, 0.1f, 1, 0.5f), ("comp_sentry", "yes", 30f, 0.1f, 1, 0.5f),
                ("comp_ram", "no", 0f, 0f, 0, 2.2f), ("comp_shovel", "no", 0f, 0f, 0, 2.2f), ("comp_orbit", "no", 0f, 0f, 0, 2.2f), ("comp_claw", "no", 0f, 0f, 0, 2.2f),
                ("comp_corona", "no", 0f, 0f, 0, 0.2f), ("comp_sprayer", "no", 0f, 0f, 0, 0.3f), ("comp_pulser", "no", 0f, 0f, 0, 0.3f),
            };
            var cbad = new List<string>();
            int present = 0;
            foreach (var x in comps)
            {
                if (!CarrierReadings.TryGetComponent(x.Id, out GameConfig.fg.CombatComponent row))
                {
                    continue;
                }
                present++;
                bool ok = row.Flight == x.Flight && Near(row.FlightSpeed, x.Speed) && Near(row.FlightRadius, x.Radius) && Near(row.Kinetic, x.Kinetic)
                          && (x.Flight == "no" || (row.FlightLob == x.Lob && Near(row.FlightLifeScale, 1.2f)));
                if (!ok)
                {
                    cbad.Add($"{x.Id}（{row.Flight} {row.FlightSpeed}/{row.FlightRadius}/{row.FlightLifeScale} 抛射 {row.FlightLob} 动能 {row.Kinetic}）");
                }
            }
            Expect(present >= 10 && cbad.Count == 0,
                $"作战组件表实体化列 {present} 行与 FG19A 第 2.1.1 节一致（射弹 / 无人机允许实体化，弹速 / 半径 / 寿命系数 1.2 / 抛射 / 动能；格斗、力场、布区不实体化、弹速 0）{(cbad.Count > 0 ? "；不符：" + string.Join("、", cbad) : string.Empty)}");

            int emit = EmgCatalog.VerbCode("emit");
            ushort oil = c.PortIndexById.TryGetValue("mat.oil", out int oi) ? c.Ports[oi].Code : (ushort)0;
            EmgPortCarrierRow[] oilRows = c.PortCarriers.Where(r => r.Port == oil).ToArray();
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            bool oilOk = oilRows.Length == 5 && oilRows.All(r => r.Verb == emit && Near(r.A, 1.5f) && Near(r.B, 6f) && (int)math.round(r.C) >= 1 && 1u << ((int)math.round(r.C) - 1) == oilBit);
            int matRows = c.PortCarriers.Count(r => r.Verb == emit);
            Expect(emit > 0 && oil > 0 && oilOk && oilBit != 0u && matRows >= 9 * 5,
                $"排放（阶段 1 口径 3，FG19A 第 0.3 节）：mat.oil × 5 载体 → 动词 emit（码 {emit}）、驻留区半径 1.5 米、6 秒、C = 沾染位号 + 1 → 状态位 Oil（{oilBit:X}）；带 emit 动词的端口 × 载体行 {matRows} 行（9 种物质 × 5 载体）；临时值待 FG15-BAL-01");

            CombatConfig cfg = CombatSite.ConfigFromTuning();
            Expect(Near(cfg.EmgSubDmgRatio, 0.5f) && cfg.EmgMaxChildren == 4 && cfg.EmgMaxPayloads == 16 && cfg.EmgVerbEmit == emit && CombatConfig.Default.EmgVerbEmit == 0,
                $"fg.TbHomeTuning → 内核配置：emg.sub.dmg_ratio {cfg.EmgSubDmgRatio}、emg.trig.max_children {cfg.EmgMaxChildren}、emg.tree.max_payloads {cfg.EmgMaxPayloads}、排放动词码 {cfg.EmgVerbEmit}（内核缺省 0 = 不执行排放）");
        }

        // ── B. FGT-EMG-011：每种可触发事件一例 + 对照组 ─────────────────────────────────

        private sealed class TrigRun
        {
            public CombatKernel K;
            public int Me, Foe, Foe2, Foe3, Prog;
            public long Splits;
            public List<CombatProjectile> Subs;
            public EmgEvent Ev, Split;
            public bool HasEv, HasSplit;
        }

        private sealed class TrigCase
        {
            public string Name;
            public EmgEventKind Kind;
            public float Param;
            public bool Bolt;
            public float BoltLife = 3f;
            public float FoeHp = 15f;
            public bool Heat;
            public bool Shield;
            public int Code = -1;
            public Action<TrigRun> Act;
        }

        private static TrigRun RunTrigger(TrigCase tc, byte count, bool withChild)
        {
            var r = new TrigRun { K = new CombatKernel(Cfg(), 32) };
            CombatKernel k = r.K;
            N root = Root().Read(tc.Kind, Trig, tc.Param).Read(EmgEventKind.Split, Cond);
            r.Prog = withChild ? Tree(k, root, Child(0, tc.Kind, count)) : Tree(k, root);
            int w = k.AddWeapon(tc.Bolt ? Bolt(r.Prog, tc.BoltLife) : Instant(r.Prog, 10f, tc.Heat));
            r.Me = Me(k, new double2(0, 0), w);
            r.Foe = Foe(k, new double2(0, 4), tc.FoeHp);
            r.Foe2 = Foe(k, new double2(2, 5), 50f);
            r.Foe3 = Foe(k, new double2(-3, 5), 50f);
            if (tc.Shield)
            {
                k.SetShield(77, CombatFaction.Hostile, new double2(0, 4), 3f, 100f, 100f, true, 0f);
            }
            tc.Act(r);
            StepUntilSplit(k, 300);
            r.Splits = k.EmgBusStats.Splits;
            r.Subs = Subs(k);
            r.HasEv = k.TryGetLastEmgEvent(tc.Kind, out r.Ev);
            if (withChild)
            {
                StepN(k, 1);
                r.HasSplit = k.TryGetLastEmgEvent(EmgEventKind.Split, out r.Split);
            }
            return r;
        }

        private static void CheckTriggers()
        {
            Line("  · B. FGT-EMG-011：命中 / 定时 / 近炸 / 终结（寿命到、命中同一步、被吸收）/ 反弹 / 穿透 / 击杀 / 阵亡 / 被击 / 过热各一例——子载荷在事件位置放出、份数、伤害 = 母 × 0.5、1 代、链深 + 1、同一条链、evt.split；对照组（同程序去掉触发子节点）一份不放");
            var cases = new List<TrigCase>
            {
                new TrigCase { Name = "命中", Kind = EmgEventKind.Hit, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "定时", Kind = EmgEventKind.Timer, Param = 0.2f, Bolt = true, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "近炸", Kind = EmgEventKind.Prox, Param = 2f, Bolt = true, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "终结·寿命到", Kind = EmgEventKind.Expire, Bolt = true, BoltLife = 0.2f, Code = EmgCode.ExpireLife, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "终结·命中同一步", Kind = EmgEventKind.Expire, Bolt = true, FoeHp = 50f, Code = EmgCode.ExpireHit, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "终结·被吸收", Kind = EmgEventKind.Expire, Bolt = true, Shield = true, Code = EmgCode.ExpireAbsorbed, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase
                {
                    Name = "反弹", Kind = EmgEventKind.Bounce, Bolt = true,
                    Act = r => { r.K.FireAt(r.Me, r.Foe, r.K.Time); StepN(r.K, 3); r.K.EmgProjectileEventForTests(0, EmgEventKind.Bounce, 0, 0f, 0f, 0); },
                },
                new TrigCase
                {
                    Name = "穿透", Kind = EmgEventKind.Pierce, Bolt = true,
                    Act = r => { r.K.FireAt(r.Me, r.Foe, r.K.Time); StepN(r.K, 3); r.K.EmgProjectileEventForTests(0, EmgEventKind.Pierce, r.Foe, 0f, 0f, 0); },
                },
                new TrigCase { Name = "击杀", Kind = EmgEventKind.Kill, FoeHp = 8f, Act = r => r.K.FireAt(r.Me, r.Foe, r.K.Time) },
                new TrigCase { Name = "阵亡", Kind = EmgEventKind.Death, Act = r => r.K.Kill(r.Me, r.Foe2) },
                new TrigCase { Name = "被击", Kind = EmgEventKind.Hurt, Act = r => r.K.Damage(r.Me, 3f, r.Foe2) },
                new TrigCase
                {
                    Name = "过热", Kind = EmgEventKind.Overheat, Heat = true, FoeHp = 50f,
                    Act = r => { r.K.FireAt(r.Me, r.Foe, r.K.Time); StepN(r.K, 1); r.K.FireAt(r.Me, r.Foe, r.K.Time); },
                },
            };
            foreach (TrigCase tc in cases)
            {
                TrigRun on = RunTrigger(tc, 2, true);
                TrigRun off = RunTrigger(tc, 2, false);
                try
                {
                    EmgEvent e = on.Ev;
                    var s = on.Subs;
                    bool shape = on.HasEv && on.Splits == 1 && s.Count == 2 && s.All(p => p.Gen == 1 && p.Node == 1 && p.Prog == on.Prog && p.Depth == e.Depth + 1 && p.Chain == e.Chain && e.Chain > 0
                                                                                        && Near(p.Damage, 5f) && Near(p.Pos, e.Pos) && Near(p.Life, 0.5f) && Near(math.length(p.Vel), 28f, 1e-3f));
                    bool code = tc.Code < 0 || e.Code == tc.Code;
                    bool split = on.HasSplit && Near(on.Split.Value, 2f) && on.Split.Code == 1 && on.Split.Prog == on.Prog && on.Split.Chain == e.Chain && on.Split.Depth == e.Depth + 1;
                    string extra = string.Empty;
                    bool special = true;
                    if (tc.Kind == EmgEventKind.Hit && s.Count == 2)
                    {
                        // 命中：第 k 份指向命中点 5 米内第 k 近的其他敌对单位，不再打母载荷刚命中的那个。
                        special = s.All(p => p.IgnoreUnit == on.Foe) && math.dot(DirOf(s[0]), Dir(e.Pos, Pos(on.K, on.Foe2))) > 0.999f && math.dot(DirOf(s[1]), Dir(e.Pos, Pos(on.K, on.Foe3))) > 0.999f;
                        extra = "；第 1 / 2 份分别指向 5 米内最近 / 次近的其他敌人、不再打被命中者";
                    }
                    else if (tc.Kind == EmgEventKind.Hurt && s.Count == 2)
                    {
                        special = math.dot(DirOf(s[0]), Dir(Pos(on.K, on.Me), Pos(on.K, on.Foe2))) > 0.999f && Near(e.Pos, Pos(on.K, on.Me));
                        extra = "；在本单位处放出、第 1 份朝向攻击者";
                    }
                    else if (tc.Kind == EmgEventKind.Kill || tc.Kind == EmgEventKind.Death)
                    {
                        special = Near(e.Pos, new double2(0, tc.Kind == EmgEventKind.Kill ? 4 : 0));
                        extra = tc.Kind == EmgEventKind.Kill ? "；在阵亡者位置放出" : "；在本单位阵亡处放出";
                    }
                    bool control = off.HasEv && off.Splits == 0 && off.Subs.Count == 0 && off.K.EmgBusStats.SubSpawned == 0 && off.K.EmgPendingCount == 0;
                    Expect(shape && code && split && special && control,
                        $"{tc.Name}（evt.{tc.Kind.ToString().ToLowerInvariant()}{(tc.Code >= 0 ? " Code " + tc.Code : string.Empty)}）：放出 {s.Count} 份于事件位置 ({e.Pos.x:0.##}, {e.Pos.y:0.##})、伤害 {(s.Count > 0 ? s[0].Damage : 0f):0.##}、1 代、链深 {e.Depth}→{(s.Count > 0 ? s[0].Depth : -1)}、evt.split 份数 {on.Split.Value:0}{extra}；对照组：事件照发、放出 {off.Subs.Count} 份、Splits {off.Splits}"
                        + (shape && code && split && special && control ? string.Empty : $"（形状 {shape} 码 {code}/{e.Code} 分裂事件 {split} 专项 {special} 对照 {control}；Splits {on.Splits}）"));
                }
                finally
                {
                    on.K.Dispose();
                    off.K.Dispose();
                }
            }
        }

        // ── C. 格斗 / 力场只按主目标一次；格斗定时从出手起算、在出手点放 ─────────────────────────────

        private static void CheckMainTargetAndTimer()
        {
            Line("  · C. 格斗 / 力场：一次出手扇形 / 圈内每个单位各算一次命中，触发只按主目标一次；子形态余波 / 小场脉冲；格斗定时从出手起算、在出手点放（DEBT-EMG1BUS01-03）");
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 1));
                CombatWeapon w = Instant(p, 10f, false, 3f);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 60f };
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int main = Foe(k, new double2(0, 2), 100f);
                int f2 = Foe(k, new double2(1, 2.2), 100f);
                int f3 = Foe(k, new double2(-1, 2.2), 100f);
                CombatFireResult fr = k.FireAt(me, main, k.Time);
                long hits = k.EmgEnqueuedOf(EmgEventKind.Hit);
                StepN(k, 1);
                List<CombatProjectile> s = Subs(k);
                Expect(fr == CombatFireResult.Ok && hits == 3 && Hp(k, f2) < 100f && Hp(k, f3) < 100f && k.EmgBusStats.Splits == 1 && s.Count == 1
                       && Near(math.length(s[0].Vel), 20f, 1e-3f) && Near(s[0].Life, 0.15f) && Near(s[0].Radius, 0.3f) && Near(s[0].Pos, new double2(0, 2)),
                    $"格斗（扇形 3 米 / 60°）：一挥命中 {hits} 个单位（evt.hit × 3），触发只按主目标放 {s.Count} 份（Splits {k.EmgBusStats.Splits}）；子形态 = 短射程余波（速 {(s.Count > 0 ? math.length(s[0].Vel) : 0f):0.#}、0.15 秒、半径 0.3）在主目标处");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 1));
                CombatWeapon w = Instant(p, 10f, false, 3f);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Aura, Area = 3f };
                int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                int main = Foe(k, new double2(0, 2), 100f);
                Foe(k, new double2(2, 0), 100f);
                Foe(k, new double2(-2, 0), 100f);
                int z0 = k.ZoneCount;
                k.FireAt(me, main, k.Time);
                long hits = k.EmgEnqueuedOf(EmgEventKind.Hit);
                StepN(k, 1);
                List<CombatZone> subs = Zones(k).Where(z => z.EmgSub == 1).ToList();
                Expect(hits == 3 && k.EmgBusStats.Splits == 1 && subs.Count == 1 && k.ZoneCount == z0 + 1 && Near(subs[0].Radius, 1.2f) && Near((float)(subs[0].Until - subs[0].Born), 0.3f, 1e-3f)
                       && subs[0].EmgProgRef == p + 1 && subs[0].EmgGen == 1 && subs[0].EmgDepth == 1 && subs[0].EmgId > 0 && Near(subs[0].Pos, new double2(0, 2)),
                    $"力场（圈 3 米）：一次脉冲命中 {hits} 个单位，触发只按主目标放 1 份 = 子区（小场脉冲，半径 1.2、存续 0.3 秒，带程序 / 1 代 / 链深 1 / 稳定 ID）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Timer, Trig, 0.3f), Child(0, EmgEventKind.Timer, 1));
                CombatWeapon w = Instant(p, 10f, false, 3f);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 60f };
                int me = Me(k, new double2(1, 1), k.AddWeapon(w));
                int main = Foe(k, new double2(1, 3), 100f);
                double t0 = k.Time;
                k.FireAt(me, main, k.Time);
                int pending = k.EmgPendingCount;
                int steps = StepUntilSplit(k, 60);
                double waited = k.Time - t0;
                List<CombatProjectile> s = Subs(k);
                bool ok = k.TryGetLastEmgEvent(EmgEventKind.Timer, out EmgEvent te);
                Expect(pending == 1 && steps > 0 && waited >= 0.3 - 1e-6 && waited <= 0.3 + Dt + 1e-6 && ok && te.EntKind == EmgEntKind.Unit && te.Ent == me && Near(te.Value, 0.3f)
                       && s.Count == 1 && Near(s[0].Pos, new double2(1, 1)),
                    $"格斗定时 0.3 秒：出手那一刻记 1 条定时（不进弹体池），{waited:0.###} 秒后发 evt.timer（宿主 = 出手单位），子载荷在出手点 (1, 1) 放出");
            }
        }

        // ── D. FGT-EMG-022：代际与链深交叉 ─────────────────────────────────────────

        private static void CheckDepth()
        {
            Line("  · D. FGT-EMG-022（代际 × 链深）：根 → 1 代 → 2 代，2 代不再放；2 代子弹打中带“被击”程序的敌人（链深 2）→ 它放出链深 3 的子弹；那颗子弹命中时链深截止、请求作废并计数");
            using var k = new CombatKernel(Cfg(), 16);
            int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 1).Read(EmgEventKind.Hit), Child(1, EmgEventKind.Hit, 1).Read(EmgEventKind.Hit));
            int q = Tree(k, Root().Read(EmgEventKind.Hurt), Child(0, EmgEventKind.Hurt, 1).Read(EmgEventKind.Hit), Child(1, EmgEventKind.Hit, 1));
            int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p, 10f)), 200f);
            int x = Foe(k, new double2(0, 4), 100f);
            int b = Foe(k, new double2(2, 4), 100f);
            int f3 = Foe(k, new double2(3.5, 4), 100f, k.AddWeapon(Instant(q, 10f)));
            k.FireAt(me, x, k.Time);
            var seen = new Dictionary<int, CombatProjectile>();
            int maxDepth = 0;
            for (int i = 0; i < 240; i++)
            {
                StepN(k, 1);
                foreach (CombatProjectile pr in Subs(k))
                {
                    seen[pr.Id] = pr;
                    maxDepth = Math.Max(maxDepth, pr.Depth);
                }
            }
            var mine = seen.Values.Where(v => v.Prog == p).OrderBy(v => v.Gen).ToList();
            var theirs = seen.Values.Where(v => v.Prog == q).ToList();
            EmgBusStats st = k.EmgBusStats;
            bool chain = mine.Count == 2 && mine[0].Gen == 1 && mine[0].Depth == 1 && mine[1].Gen == 2 && mine[1].Depth == 2 && mine[0].Chain == mine[1].Chain;
            bool reaction = theirs.Count == 1 && theirs[0].Gen == 1 && theirs[0].Depth == 3 && theirs[0].Faction == CombatFaction.Hostile && theirs[0].Chain == mine[0].Chain;
            Expect(chain && reaction && maxDepth == 3 && st.SubSpawned == 3 && st.SpawnDropped >= 1 && st.DepthCut >= 1 && Hp(k, b) < 100f && Hp(k, f3) < 100f && Hp(k, me) < 200f,
                $"我方程序：1 代（链深 1）→ 2 代（链深 2）同一条链，2 代节点读 evt.hit 也不再放（共 {mine.Count} 颗）；敌方被击程序在链深 2 被打中 → 放出 1 代、链深 {(theirs.Count > 0 ? theirs[0].Depth : -1)} 的子弹打回我方机器；"
                + $"它命中时链深已到 3：DepthCut {st.DepthCut}、请求作废 {st.SpawnDropped}；全程最大链深 {maxDepth}，共生成 {st.SubSpawned} 颗"
                + (chain && reaction ? string.Empty : $"（我方 {string.Join("/", mine.Select(v => $"g{v.Gen}d{v.Depth}"))}；敌方 {string.Join("/", theirs.Select(v => $"g{v.Gen}d{v.Depth}"))}）"));
        }

        // ── E. 顺延不丢 ─────────────────────────────────────────────────────────────

        private static void CheckBackpressure()
        {
            Line("  · E. 顺延不丢（FGR-EMG-034 / 063，ADR 第 7 节第 6 条）：弹体池满时请求留在待生成表、空位出来才生成且那时才开始计寿命；同一条链同时存活 ≤ emg.tree.max_payloads；份数 ≤ emg.trig.max_children");
            CombatConfig cfg = Cfg();
            cfg.ProjectileCapacity = 3;
            using (var k = new CombatKernel(cfg, 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 2));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p)));
                int gunner = Me(k, new double2(20, 0), k.AddWeapon(Bolt(-1, 1f)));
                int foe = Foe(k, new double2(0, 4), 100f);
                Foe(k, new double2(2, 5), 100f);
                int far = Foe(k, new double2(20, 50), 100f);
                for (int i = 0; i < 3; i++)
                {
                    k.FireAt(gunner, far, k.Time);
                }
                int full = k.ProjectileCount;
                k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                long deferred = k.EmgBusStats.SpawnDeferred;
                int waitingSubs = Subs(k).Count, pending = k.EmgPendingCount;
                int steps = 0;
                List<CombatProjectile> first = null;
                for (; steps < 120 && first == null; steps++)
                {
                    StepN(k, 1);
                    List<CombatProjectile> s = Subs(k);
                    if (s.Count > 0)
                    {
                        first = s;
                    }
                }
                Expect(full == 3 && deferred > 0 && waitingSubs == 0 && pending == 1 && first != null && first.Count == 2 && first.All(x => Near(x.Life, 0.5f))
                       && k.EmgBusStats.SpawnDropped == 0 && k.EmgBusStats.SpawnLost == 0 && k.EmgBusStats.SubSpawned == 2,
                    $"弹体池 3 发已占满：触发后 2 份全部顺延（{deferred} 次顺延、待生成 {pending} 条、子弹药 0 颗）；{steps} 步后旧弹体到期，2 份同时生成，生成那一步寿命 = 0.5 秒（顺延不吃寿命）；作废 0、丢失 0");
            }
            cfg = Cfg();
            cfg.EmgMaxPayloads = 2;
            using (var k = new CombatKernel(cfg, 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 4));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p)));
                int foe = Foe(k, new double2(0, 4), 100f);
                k.FireAt(me, foe, k.Time);
                int maxLive = 0;
                for (int i = 0; i < 200; i++)
                {
                    StepN(k, 1);
                    maxLive = Math.Max(maxLive, Subs(k).Count);
                }
                Expect(maxLive == 2 && k.EmgBusStats.SubSpawned == 4 && k.EmgBusStats.SpawnDeferred > 0 && k.EmgPendingCount == 0,
                    $"emg.tree.max_payloads = 2、份数 4：同一条链同时存活最多 {maxLive} 颗，其余顺延，最终 {k.EmgBusStats.SubSpawned} 份全部生成、待生成表清空");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit).Read(EmgEventKind.Split, Cond), Child(0, EmgEventKind.Hit, 6));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p)));
                int foe = Foe(k, new double2(0, 4), 100f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                int now = Subs(k).Count;
                StepN(k, 1);
                bool sp = k.TryGetLastEmgEvent(EmgEventKind.Split, out EmgEvent se);
                Expect(now == 4 && sp && Near(se.Value, 4f) && k.EmgBusStats.SubSpawned == 4,
                    $"份数 6 > emg.trig.max_children 4：一次触发只放 {now} 份（evt.split 份数 {se.Value:0}）");
            }
        }

        // ── F. SpawnFlight ─────────────────────────────────────────────────────────────

        private static List<(CombatEventKind Kind, byte Code, byte Code2)> Outcomes(CombatKernel k, List<(CombatEventKind, byte, byte)> into)
        {
            CombatEvent[] ev = k.DrainGameplay(256, out int n);
            for (int i = 0; i < n; i++)
            {
                if (ev[i].Kind == CombatEventKind.AttackOutcome || ev[i].Kind == CombatEventKind.CommandEnded)
                {
                    into.Add((ev[i].Kind, ev[i].Code, ev[i].Code2));
                }
            }
            return into;
        }

        private static string Seq(List<(CombatEventKind Kind, byte Code, byte Code2)> l) =>
            string.Join(",", l.Select(x => x.Kind == CombatEventKind.AttackOutcome ? $"结果{(CombatFireResult)x.Code}/{x.Code2}" : $"结束{(CombatEndReason)x.Code}"));

        private static void CheckSpawnFlight()
        {
            Line("  · F. 根载荷按需实体化（ADR ③）：SpawnFlight 武器开火报“已发射”、当下不掉血、命中才结算；编队攻击命中致死才回报击毁；重炮仍先瞄准、熔穿过载照常；无人机开火实体化；不需要飞行与没有程序一致");
            using (var k = new CombatKernel(Cfg(), 16))
            using (var c = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(Instant(p))));
                int foe = Foe(k, new double2(0, 6), 50f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                float hp0 = Hp(k, foe);
                List<CombatProjectile> fl = Projectiles(k, EmgProjFlags.Flight);
                int steps = 0;
                for (; steps < 60 && Hp(k, foe) >= 50f; steps++)
                {
                    StepN(k, 1);
                }
                int pc = Tree(c, Root().Read(EmgEventKind.Hit, Cond));
                int cm = Me(c, new double2(0, 0), c.AddWeapon(Instant(pc)));
                int cf = Foe(c, new double2(0, 6), 50f);
                CombatFireResult rc = c.FireAt(cm, cf, c.Time);
                Expect(r == CombatFireResult.Launched && Near(hp0, 50f) && fl.Count == 1 && fl[0].Prog == p && fl[0].Chain > 0 && Near(math.length(fl[0].Vel), 30f, 1e-3f) && Near(fl[0].Radius, 0.15f)
                       && Near(Hp(k, foe), 40f) && k.EmgBusStats.FlightShots == 1 && steps >= 8 && rc == CombatFireResult.Ok && Near(Hp(c, cf), 40f) && c.ProjectileCount == 0,
                    $"即时武器 + 需要飞行：开火结果 {r}、当下目标仍 {hp0:0} 血、生成 1 发根弹体（速 30、半径 0.15，0 代、本次出手的链）；{steps} 步后命中扣 10（与即时命中同一结算）；不置 SpawnFlight 的同程序武器当场命中、不生成弹体");
            }
            foreach (bool flight in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 16);
                int p = Tree(k, Root().Read(EmgEventKind.Kill, Cond));
                CombatWeapon w = Instant(p);
                int me = Me(k, new double2(0, 0), k.AddWeapon(flight ? Flight(w) : w));
                int foe = Foe(k, new double2(0, 6), 10f);
                k.IssueCommand(me, CombatCommandKind.Attack, new double2(0, 6), foe, 0.5f, 20f, 1f, false);
                var seq = new List<(CombatEventKind, byte, byte)>();
                int firstEnd = -1;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    Outcomes(k, seq);
                    if (firstEnd < 0 && seq.Any(x => x.Item1 == CombatEventKind.CommandEnded))
                    {
                        firstEnd = i;
                    }
                }
                string got = Seq(seq);
                string want = flight
                    ? $"结果{CombatFireResult.Launched}/0,结果{CombatFireResult.Ok}/1,结束{CombatEndReason.TargetDestroyed}"
                    : $"结果{CombatFireResult.Ok}/1,结束{CombatEndReason.TargetDestroyed}";
                Expect(got == want && Hp(k, foe) <= 0f && k.TryGetUnit(me, out CombatUnitView mv) && mv.Command == CombatCommandKind.None && (!flight || firstEnd > 0),
                    flight
                        ? $"编队攻击（SpawnFlight）：开火当下回报 [已发射、未击毁]，弹体命中致死后内核补发 [结果 = 击毁] 并以“目标已击毁”结束命令（{got}；第 {firstEnd} 步，热更层沿用“击毁目标 …，交还 AI”）"
                        : $"对照（即时命中）：开火当步 [击毁 + 目标已击毁]（{got}）——事件种类与顺序和 SpawnFlight 的击毁回报一致");
            }
            CombatWeapon Cannon(int prog, bool fl)
            {
                var w = new CombatWeapon
                {
                    Mode = CombatWeaponMode.Cannon, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 20f, Cooldown = 1f, AimSeconds = 0.3f,
                    HeatPerShot = 10f, OverloadExtraHeat = 20f, OverheatAt = 100f, RecoverBelow = 10f, Dissipation = 0f, PierceBonus = 0.5f, Reaction = CombatReaction.MeltOverload, Program = prog,
                };
                return fl ? Flight(w, 22f, 0.35f, 1.6f) : w;
            }
            var results = new List<(CombatFireResult a, CombatFireResult b, float heat, float hp, EmgProjFlags flags)>();
            foreach (bool fl in new[] { true, false })
            {
                using var k = new CombatKernel(Cfg(), 16);
                int p = Tree(k, Root().Read(EmgEventKind.Hit, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Cannon(p, fl)));
                int foe = Foe(k, new double2(0, 6), 100f, -1, 0.5f);
                CombatFireResult a = k.FireAt(me, foe, k.Time);
                StepN(k, 20);
                CombatFireResult b = k.FireAt(me, foe, k.Time);
                k.TryGetUnit(me, out CombatUnitView mv);
                EmgProjFlags flags = Projectiles(k, EmgProjFlags.Flight).Select(x => x.Flags).FirstOrDefault();
                StepN(k, 40);
                results.Add((a, b, mv.Heat, Hp(k, foe), flags));
            }
            var f = results[0];
            var i0 = results[1];
            Expect(f.a == CombatFireResult.StillAiming && f.b == CombatFireResult.Launched && i0.a == CombatFireResult.StillAiming && i0.b == CombatFireResult.Ok
                   && Near(f.heat, 30f) && Near(i0.heat, 30f) && (f.flags & EmgProjFlags.Overload) != 0 && (f.flags & EmgProjFlags.Flight) != 0 && Near(f.hp, i0.hp) && f.hp < 100f,
                $"重炮 + 需要飞行：先瞄准（{f.a}），0.3 秒后真开火报 {f.b}；熔穿过载照常（积热 {f.heat:0} = 10 + 过载 20，与即时重炮 {i0.heat:0} 相同），弹体带过载记号，命中后目标剩 {f.hp:0.#} 血 = 即时重炮 {i0.hp:0.#}（穿甲在命中时加）");

            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit, Cond));
                CombatWeapon w = Instant(p, 6f);
                w.Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 6f, DroneRatio = 1f, DroneCooldown = 0.3f, DroneLeash = 8f };
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(w, 30f, 0.1f, 0.32f)));
                int foe = Foe(k, new double2(0, 4), 100f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                bool sawDroneShot = false;
                for (int i = 0; i < 180 && !sawDroneShot; i++)
                {
                    StepN(k, 1);
                    sawDroneShot = Projectiles(k, EmgProjFlags.ViaDrone).Any(x => (x.Flags & EmgProjFlags.Flight) != 0);
                }
                StepN(k, 30);
                Expect(r == CombatFireResult.Ok && k.DroneCount >= 1 && sawDroneShot && k.EmgBusStats.FlightShots >= 1 && Hp(k, foe) < 100f,
                    $"无人机舱 + 需要飞行：母机出手仍是放机（{r}），无人机每次开火实体化为根弹体（带“经无人机”记号），命中后目标剩 {Hp(k, foe):0.#} 血");
            }
            using (var a = new CombatKernel(Cfg(), 16))
            using (var b = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(a, Root().Read(EmgEventKind.Hit, Cond));
                int ma = Me(a, new double2(0, 0), a.AddWeapon(Instant(p)));
                int fa = Foe(a, new double2(0, 6), 50f);
                int mb = Me(b, new double2(0, 0), b.AddWeapon(Instant(-1)));
                int fb = Foe(b, new double2(0, 6), 50f);
                CombatFireResult ra = a.FireAt(ma, fa, a.Time), rb = b.FireAt(mb, fb, b.Time);
                StepN(a, 30);
                StepN(b, 30);
                Expect(ra == CombatFireResult.Ok && rb == CombatFireResult.Ok && Near(Hp(a, fa), Hp(b, fb)) && a.ProjectileCount == 0 && b.ProjectileCount == 0 && a.EmgBusStats.FlightShots == 0,
                    $"不需要飞行（SpawnFlight = 0）的程序：开火结果 {ra}、当场命中、不生成弹体，与没有程序的武器结果一致（没有程序的地点 StateHash 金标准见 FgEmergenceKernelSelfCheck B）");
            }
            // 审查修复：SpawnFlight 根弹体飞进展开的偏折场被反弹——掉头成敌方弹体，同时去掉 Flight / Heal / Sub 等己方载荷记号与 IgnoreUnit（否则命中致死会给场源补发击毁回报、治疗弹会去修敌人）。
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(Instant(p))));
                int prof = k.AddProfile(new CombatBehaviorProfile { DeflectRadius = 3f, DeflectReflect = 0.5f });
                int prism = Unit(k, CombatFaction.Hostile, new double2(0, 8), -1, 100f, 100f, CombatBehavior.None, prof);
                bool armed = k.SetFlag(prism, CombatUnitFlags.DeflectArmed, true) && k.SetDeflectPhase(prism, 2, 30f);
                int foe = Foe(k, new double2(0, 8.5), 50f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                CombatProjectile back = default;
                bool found = false;
                for (int i = 0; i < 40 && !found; i++)
                {
                    StepN(k, 1);
                    foreach (CombatProjectile x in Projectiles(k, EmgProjFlags.None))
                    {
                        if (x.Faction == CombatFaction.Hostile)
                        {
                            back = x;
                            found = true;
                        }
                    }
                }
                const EmgProjFlags own = EmgProjFlags.Flight | EmgProjFlags.Heal | EmgProjFlags.Sub | EmgProjFlags.Overload | EmgProjFlags.ViaDrone;
                Expect(armed && r == CombatFireResult.Launched && found && (back.Flags & own) == 0 && back.IgnoreUnit == 0 && back.Prog == -1 && back.Weapon == -1 && Near(Hp(k, foe), 50f),
                    $"SpawnFlight 根弹体打进展开的偏折场：被反弹成敌方弹体（记号 {back.Flags}，已去掉 Flight / Heal / Sub / 过载 / 经无人机，IgnoreUnit {back.IgnoreUnit}、不带程序与读法），场里的目标不掉血");
            }
        }

        // ── G. 障碍 / 掩体 ─────────────────────────────────────────────────────────────

        private static void CheckObstacles()
        {
            Line("  · G. 障碍圆收进 EmgHash，只对 SpawnFlight 弹体 / 子弹药做线段检测（炮塔弹体、即时命中不变）；即时命中结算前查残骸掩体（线段上没有时与旧行为一致）");
            var obstacle = new List<float3> { new float3(0f, 3f, 1f) };
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Hit, Cond).Read(EmgEventKind.Expire, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(Instant(p))));
                int foe = Foe(k, new double2(0, 6), 50f);
                k.SetObstacles(obstacle);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                StepN(k, 60);
                bool ex = k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent e);
                // 审查修复：EmgHash 的脏标记与待生成游标放在 EmgS（作业副本与主线程共用）——这一步重建过一次后，主线程看到的脏标记也是 0，不会每步整张重建。
                EmgScalars es = k.EmgScalars;
                Expect(r == CombatFireResult.Launched && k.EmgBusStats.ObstacleHits == 1 && Near(Hp(k, foe), 50f) && ex && e.Code == EmgCode.ExpireObstacle && e.Pos.y < 2.5 && k.ProjectileCount == 0
                       && es.HashDirty == 0,
                    $"SpawnFlight 弹体撞上挡在弹道上的障碍圆（0, 3, 半径 1）：在撞点 ({e.Pos.x:0.##}, {e.Pos.y:0.##}) 结束（evt.expire Code {e.Code} = 撞障碍），目标仍 {Hp(k, foe):0} 血；"
                    + $"EmgHash 重建一次后脏标记 {es.HashDirty}（内核一步的作业副本写回共享的 EmgS，不再每步重建）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(-1)));
                int gun = Me(k, new double2(10, 0), k.AddWeapon(Bolt(-1)));
                int foe = Foe(k, new double2(0, 6), 50f);
                int foe2 = Foe(k, new double2(10, 6), 50f);
                k.SetObstacles(new List<float3> { new float3(0f, 3f, 1f), new float3(10f, 3f, 1f) });
                k.FireAt(me, foe, k.Time);
                k.FireAt(gun, foe2, k.Time);
                StepN(k, 90);
                Expect(Near(Hp(k, foe), 40f) && Near(Hp(k, foe2), 40f) && k.EmgBusStats.ObstacleHits == 0,
                    $"同样的障碍：普通即时命中照常扣血（{Hp(k, foe):0}），炮塔式弹体（没有 SpawnFlight / 子载荷记号）穿过障碍命中（{Hp(k, foe2):0}）——回归面不扩");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int p = Tree(k, Root().Read(EmgEventKind.Absorb, Cond));
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p)));
                int foe = Foe(k, new double2(0, 6), 50f);
                bool added = k.TryAddWreck(new EmgWreck { Pos = new double2(0, 3), Radius = 1f, Hp = 30f, MaxHp = 30f, Until = 1e9 }, out int wid);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                k.TryGetWreck(0, out EmgWreck w1);
                bool ab = k.TryGetLastEmgEvent(EmgEventKind.Absorb, out EmgEvent ae);
                k.FireAt(me, foe, k.Time);
                k.FireAt(me, foe, k.Time);
                float coveredHp = Hp(k, foe);
                k.FireAt(me, foe, k.Time);
                k.TryGetWreck(0, out EmgWreck w0);
                Expect(added && r == CombatFireResult.Ok && Near(w1.Hp, 20f) && ab && ae.Other == wid && ae.OtherKind == EmgEntKind.Wreck && Near(ae.Value, 10f) && Near(coveredHp, 50f)
                       && Near(w0.Hp, 0f) && Near(Hp(k, foe), 40f) && k.EmgBusStats.CoverAbsorbed == 3,
                    $"残骸（0, 3，耐久 30）挡在即时命中线段上：目标获得掩护（3 发都被吸收，目标仍 {coveredHp:0} 血），残骸耐久 30 → {w1.Hp:0} → 0，evt.absorb（Other = 残骸 {wid}）；耐久归 0 后第 4 发照常命中（{Hp(k, foe):0}）");
            }
            using (var k = new CombatKernel(Cfg(), 16))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(-1)));
                int foe = Foe(k, new double2(0, 6), 50f);
                k.TryAddWreck(new EmgWreck { Pos = new double2(4, 3), Radius = 1f, Hp = 30f, MaxHp = 30f, Until = 1e9 }, out _);
                CombatWeapon melee = Instant(-1, 10f, false, 3f);
                melee.Reading = new CombatReading { Carrier = CombatCarrier.Melee, Area = 3f, Cone = 60f };
                int ram = Me(k, new double2(10, 0), k.AddWeapon(melee));
                int foe2 = Foe(k, new double2(10, 2), 50f);
                k.TryAddWreck(new EmgWreck { Pos = new double2(10, 1), Radius = 0.3f, Hp = 30f, MaxHp = 30f, Until = 1e9 }, out _);
                k.FireAt(me, foe, k.Time);
                k.FireAt(ram, foe2, k.Time);
                Expect(Near(Hp(k, foe), 40f) && Near(Hp(k, foe2), 40f) && k.EmgBusStats.CoverAbsorbed == 0,
                    "残骸不在线段上时即时命中照常；格斗不是射击线段，残骸挡在中间也照常命中（掩体只对射弹载体）");
            }
        }

        // ── H. [分裂, 漏油]（真实编译器）────────────────────────────────────────────

        private static void UseStage1()
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
            // 漏油的声明随 EMG1-FW-01 落表；这里按 FG19A 已定的两条（读 evt.expire 触发、写 mat.oil 排放）代入，与 FgEmgProgramSelfCheck 同一做法。
            src.Decls.RemoveAll(d => d.Owner == "fw_oilleak");
            src.Decls.Add(new EmgSource.Decl
            {
                Id = "fw_oilleak.1", Owner = "fw_oilleak", OwnerKind = "firmware", Seq = 1, Dir = "read", Port = "evt.expire", Role = "trigger", Amount = 1f, Carriers = "all",
                TextKey = "emg.decl.fw_oilleak.1", StageGate = 1,
            });
            src.Decls.Add(new EmgSource.Decl
            {
                Id = "fw_oilleak.2", Owner = "fw_oilleak", OwnerKind = "firmware", Seq = 2, Dir = "write", Port = "mat.oil", Role = "emit", Amount = 1f, Carriers = "all",
                TextKey = "emg.decl.fw_oilleak.2", StageGate = 1,
            });
            EmgCatalog.OverrideForTests(src);
        }

        private static void CheckSplitOil()
        {
            Line("  · H. [分裂, 漏油]（真实编译器，emg.stage = 1）：每颗子弹药落点各留一摊油（排放 → 读法驻留区 + 沾染位）；子弹药不读根读法、只执行自己节点的指令；信号离开 / 换武器时在飞子弹药按原程序走完");
            UseStage1();
            try
            {
                EmgCompiled c = EmgCatalog.Current;
                EmgProgramResult r = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", "fw_oilleak" }, FirmwareCarrier.Projectile);
                int emit = EmgCatalog.VerbCode("emit");
                uint oilBit = NamedReactionCatalog.BitOf("Oil");
                bool shape = c.EffectiveStage == 1 && r.KernelNodes.Count == 2 && r.KernelNodes[1].TrigEvent == EmgEventKind.Hit && r.KernelNodes[1].TrigCount >= 1
                             && (r.Program.Flags & EmgProgramFlags.NeedsFlight) == 0
                             && r.KernelInstrs.Skip(r.KernelNodes[1].FirstInstr).Take(r.KernelNodes[1].InstrCount).Any(x => x.Verb == emit && Near(x.A, 1.5f) && Near(x.B, 6f));
                int n = Math.Min((int)r.KernelNodes[1].TrigCount, 4);
                foreach (bool swap in new[] { false, true })
                {
                    using var k = new CombatKernel(Cfg(), 16);
                    int p = k.AddProgram(r.Program, r.KernelNodes, r.KernelInstrs);
                    CombatWeapon w = Instant(p);
                    w.Reading = new CombatReading { Knockback = 2f };
                    int me = Me(k, new double2(0, 0), k.AddWeapon(w));
                    int plain = k.AddWeapon(Instant(-1));
                    int a = Foe(k, new double2(0, 4), 200f);
                    int[] others = { Foe(k, new double2(2, 5), 100f), Foe(k, new double2(-2, 5), 100f), Foe(k, new double2(0, 8.5), 100f), Foe(k, new double2(3, 2), 100f) };
                    double2[] before = others.Select(o => Pos(k, o)).ToArray();
                    k.FireAt(me, a, k.Time);
                    StepN(k, 1);
                    int spawned = Subs(k).Count;
                    if (swap)
                    {
                        k.SetUnitWeapon(me, plain); // 信号离开 / 重编译：武器换成没有程序的那一把，已发射的子弹药留在世界里
                    }
                    StepN(k, 60);
                    List<CombatZone> oil = Zones(k).Where(z => z.StatusMask == oilBit && Near(z.Radius, 1.5f)).ToList();
                    int hitOthers = others.Count(o => Hp(k, o) < 100f);
                    bool exact = others.Where(o => Hp(k, o) < 100f).All(o => Near(Hp(k, o), 95f));
                    bool still = others.Select((o, i) => Near(Pos(k, o), before[i], 1e-6)).All(x => x);
                    bool rootMoved = Pos(k, a).y > 4.5;
                    Expect(shape && spawned == n && oil.Count == n && hitOthers >= 1 && exact && still && rootMoved && k.EmgBusStats.SubSpawned == n,
                        (swap ? "换成没有程序的武器后（信号离开口径 FGR-EMG-037 第 1 条）：" : "编译结果 0 g0: split | 1<0 g1 hit×" + n + ": oilleak（不需要飞行，根载荷仍是即时命中）；")
                        + $"命中放出 {spawned} 颗子弹药，每颗结算处各一摊油（{oil.Count} 块，半径 1.5、沾染位 Oil）；被子弹药打中的 {hitOthers} 个敌人各只掉 5 血、没有被根读法的击退推动（根目标被击退到 y = {Pos(k, a).y:0.#}）");
                }
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── I. 维修 / 治疗子弹药 ─────────────────────────────────────────────────────────────

        private static void CheckRepair()
        {
            Line("  · I. 维修（DEBT-EMG1BUS01-03）：带程序的维修单位每次修复 = 一次出手，修复命中 evt.hit Code = 修复；放出的治疗子弹药只碰己方、治疗另一友军、不伤敌");
            using var k = new CombatKernel(Cfg(), 16);
            int q = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 1));
            int prof = k.AddProfile(new CombatBehaviorProfile { EffectRange = 10f, EffectAmount = 5f, CycleSeconds = 0.2f, Speed = 0f });
            int healer = Unit(k, CombatFaction.Hostile, new double2(10, 10), k.AddWeapon(Instant(q)), 100f, 100f, CombatBehavior.Repair, prof);
            int h1 = Unit(k, CombatFaction.Hostile, new double2(12, 10), -1, 10f, 100f);
            int h2 = Unit(k, CombatFaction.Hostile, new double2(13.5, 10), -1, 90f, 100f);
            int me = Me(k, new double2(12.8, 10), -1, 100f);
            List<EmgEvent> hits = Watch(k, 60, EmgEventKind.Hit);
            float g = Hp(k, h2) - 90f;
            bool repairHit = hits.Any(e => e.Code == EmgCode.HitHeal && e.EntKind == EmgEntKind.Unit && e.Ent == healer && e.Other == h1);
            Expect(repairHit && k.EmgBusStats.SubSpawned >= 1 && g > 0f && Mathf.Abs(g / 2.5f - Mathf.Round(g / 2.5f)) < 1e-3f && Near(Hp(k, me), 100f) && Hp(k, h1) > 10f,
                $"敌方维修单位修复伤员：evt.hit Code {EmgCode.HitHeal}（修复，宿主 = 维修单位、Other = 伤员）；放出 {k.EmgBusStats.SubSpawned} 颗治疗子弹药（伤害 = 修复量 5 × 0.5），另一友军 +{g:0.#} 血（2.5 的整数倍），挡在路上的我方机器 {Hp(k, me):0} 血不受伤");
        }

        // ── J. FGT-EMG-040：中途存读档 ─────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · J. FGT-EMG-040：在飞子弹药 + 顺延请求 + 带程序的区域（布区落区、小场脉冲）+ 带程序的回波中途存档 → 新内核读档 → 续跑，与连续运行 StateHash / 涌现哈希逐位一致，不复制不丢");
            CombatConfig cfg = Cfg();
            cfg.ProjectileCapacity = 5;
            using var k = new CombatKernel(cfg, 32);
            int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 4));
            int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(p)));
            int gunner = Me(k, new double2(30, 0), k.AddWeapon(Bolt(-1, 1.5f)));
            int far = Foe(k, new double2(30, 60), 100f);
            int foe = Foe(k, new double2(0, 4), 200f);
            Foe(k, new double2(2, 5), 200f);
            int pa = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 1));
            CombatWeapon aura = Instant(pa, 10f, false, 3f);
            aura.Reading = new CombatReading { Carrier = CombatCarrier.Aura, Area = 3f };
            int au = Me(k, new double2(-20, 0), k.AddWeapon(aura));
            int af = Foe(k, new double2(-20, 2), 200f);
            int pf = Tree(k, Root().Read(EmgEventKind.Hit, Cond).Read(EmgEventKind.Expire, Cond).Read(EmgEventKind.Timer, Trig, 0.5f));
            CombatWeapon field = Instant(pf);
            field.Reading = new CombatReading { Carrier = CombatCarrier.Field, Area = 2f, FieldSeconds = 2f, FieldDpsRatio = 1f };
            int fu = Me(k, new double2(0, -20), k.AddWeapon(field));
            int ff = Foe(k, new double2(0, -16), 200f);
            int pe = Tree(k, Root().Read(EmgEventKind.Hit, Cond));
            CombatWeapon echo = Instant(pe);
            echo.Reading = new CombatReading { EchoCount = 1, EchoDelay = 0.5f, EchoRatio = 0.5f };
            int eu = Me(k, new double2(20, -20), k.AddWeapon(echo));
            int ef = Foe(k, new double2(20, -16), 200f);
            for (int i = 0; i < 3; i++)
            {
                k.FireAt(gunner, far, k.Time);
            }
            k.FireAt(me, foe, k.Time);
            k.FireAt(au, af, k.Time);
            k.FireAt(fu, ff, k.Time);
            k.FireAt(eu, ef, k.Time);
            StepN(k, 2);
            int subs = Subs(k).Count, pending = k.EmgPendingCount, echoes = k.EchoCount;
            int hostZones = Zones(k).Count(z => z.EmgProgRef > 0);
            byte[] snap = k.Serialize();
            ulong hSave = k.StateHash();
            int[] hostIdx = Zones(k).Select((z, i) => (z, i)).Where(x => x.z.EmgProgRef > 0).Select(x => x.i).ToArray();
            int zonesAtSave = k.ZoneCount;
            using var b = new CombatKernel(cfg, 32);
            CombatLoadResult lr = b.Load(snap);
            bool same0 = lr == CombatLoadResult.Ok && b.StateHash() == k.StateHash() && b.EmgStateHash() == k.EmgStateHash() && Subs(b).Count == subs && b.EmgPendingCount == pending
                         && b.EchoCount == echoes && Zones(b).Count(z => z.EmgProgRef > 0) == hostZones
                         && Zones(b).Where(z => z.EmgProgRef > 0).Select(z => (z.EmgId, z.EmgChain, z.EmgSub)).SequenceEqual(Zones(k).Where(z => z.EmgProgRef > 0).Select(z => (z.EmgId, z.EmgChain, z.EmgSub)));
            bool every = true;
            for (int i = 0; i < 150; i++)
            {
                StepN(k, 1);
                StepN(b, 1);
                every &= k.StateHash() == b.StateHash() && k.ProjectileCount == b.ProjectileCount && k.EmgPendingCount == b.EmgPendingCount && k.ZoneCount == b.ZoneCount;
            }
            Expect(subs >= 1 && pending >= 1 && echoes >= 1 && hostZones >= 2 && same0 && every && k.EmgStateHash() == b.EmgStateHash() && k.EmgPendingCount == 0,
                $"存档时：在飞子弹药 {subs} 颗、顺延请求 {pending} 条（弹体池 5 已满）、带程序的区域 {hostZones} 块、带程序的回波 {echoes} 个；读档后逐项相同，续跑 150 步每一步状态哈希、弹体数、待生成数、区域数都与连续运行一致（{k.StateHash():X16}）");

            Expect(b.Load(snap) == CombatLoadResult.Ok && b.StateHash() == hSave && Subs(b).Count == subs && b.EmgPendingCount == pending,
                "同一份快照再读一次：回到存档那一刻（读档幂等，子弹药 / 顺延请求不复制）；暂停 = 不步进，倍速只改每帧步数（内核步长固定），真实时钟的 0.5x / 1x / 3x 与暂停见 W 段");

            // 审查补充：段 11（待生成）/ 12（区域宿主）/ 13（回波宿主）的坏值——整份拒绝、内核原样。记录内偏移按 CombatKernel.WriteTriggerSegments 的字段顺序
            // （待生成：At 8 + Pos 16 + Dir 8 + Damage/ZoneRadius/ZoneLife 12 → Prog @44 … Node @72、Depth @73，共 83 字节；区域：ProgRef @0、Id @4、Chain @8、TrigUsed @12、Node @16、Depth @17 … 共 24；回波：ProgRef @0、Chain @4、Node @8、Depth @9，共 10）。
            (int at, int stride) ps = FgEmergenceKernelSelfCheck.FindSegment(snap, EmgConst.SegPending, 25, pending);
            (int at, int stride) zs = FgEmergenceKernelSelfCheck.FindSegment(snap, EmgConst.SegZones, 9, zonesAtSave);
            (int at, int stride) es = FgEmergenceKernelSelfCheck.FindSegment(snap, EmgConst.SegEchoes, 4, echoes);
            bool layout = ps.at > 0 && ps.stride == 83 && zs.at > 0 && zs.stride == 24 && es.at > 0 && es.stride == 10 && hostIdx.Length >= 2 && BitConverter.ToInt32(snap, es.at) > 0;
            var bad = new List<(string Name, byte[] Data)>();
            if (layout)
            {
                int z0 = zs.at + hostIdx[0] * zs.stride, z1 = zs.at + hostIdx[1] * zs.stride;
                bad.Add(("待生成请求程序下标越界", FgEmergenceKernelSelfCheck.Patch(snap, d => FgEmergenceKernelSelfCheck.WriteInt(d, ps.at + 44, 99))));
                bad.Add(("待生成请求链深 4", FgEmergenceKernelSelfCheck.Patch(snap, d => d[ps.at + 73] = 4)));
                bad.Add(("区域宿主程序下标越界", FgEmergenceKernelSelfCheck.Patch(snap, d => FgEmergenceKernelSelfCheck.WriteInt(d, z0, 99))));
                bad.Add(("区域宿主 Id 重复", FgEmergenceKernelSelfCheck.Patch(snap, d => FgEmergenceKernelSelfCheck.WriteInt(d, z1 + 4, BitConverter.ToInt32(d, z0 + 4)))));
                bad.Add(("区域宿主链深 4", FgEmergenceKernelSelfCheck.Patch(snap, d => d[z0 + 17] = 4)));
                bad.Add(("回波宿主程序下标越界", FgEmergenceKernelSelfCheck.Patch(snap, d => FgEmergenceKernelSelfCheck.WriteInt(d, es.at, 99))));
                bad.Add(("回波宿主节点 200", FgEmergenceKernelSelfCheck.Patch(snap, d => d[es.at + 8] = 200)));
            }
            using var t = new CombatKernel(cfg, 32);
            bool loaded = t.Load(snap) == CombatLoadResult.Ok;
            ulong h0 = t.StateHash();
            var wrong = new List<string>();
            foreach ((string name, byte[] data) in bad)
            {
                CombatLoadResult got = t.Load(data);
                if (got != CombatLoadResult.InvalidValue || t.StateHash() != h0)
                {
                    wrong.Add($"{name}→{got}");
                }
            }
            Expect(layout && loaded && bad.Count == 7 && wrong.Count == 0 && t.Load(snap) == CombatLoadResult.Ok && t.StateHash() == hSave,
                $"快照坏值 {bad.Count} 种（{string.Join(" / ", bad.Select(x => x.Name))}）：整份拒绝（InvalidValue）、内核保持原样；同一份完好快照照常读回{(wrong.Count > 0 ? "；不符：" + string.Join("、", wrong) : string.Empty)}");
        }

        // ── K. 热更层 ─────────────────────────────────────────────────────────────

        private static void CheckHotfix()
        {
            Line("  · K. 热更层：CombatSite.ApplySpawnFlight（需要飞行且组件允许实体化才置位，弹速 / 半径 / 寿命取表；格斗不实体化；不需要飞行逐字不变）、程序树预览“以飞行弹体发射”、文本键、引导钩子");
            UseStage1();
            try
            {
                EmgProgramResult homing = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_homing" }, FirmwareCarrier.Projectile);
                EmgProgramResult splitOil = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", "fw_oilleak" }, FirmwareCarrier.Projectile);
                bool needs = (homing.Program.Flags & EmgProgramFlags.NeedsFlight) != 0 && (splitOil.Program.Flags & EmgProgramFlags.NeedsFlight) == 0;
                CombatWeapon gun = Instant(0, 10f, false, 20f);
                string n1 = CombatSite.ApplySpawnFlight(ref gun, homing, ComponentCatalog.CompGunId);
                CombatWeapon ram = Instant(0, 10f, false, 3f);
                ram.Reading = new CombatReading { Carrier = CombatCarrier.Melee };
                string n2 = CombatSite.ApplySpawnFlight(ref ram, homing, ComponentCatalog.CompRamId);
                CombatWeapon plain = Instant(0, 10f, false, 20f);
                string key0 = CombatKernel.WeaponKey(plain);
                string n3 = CombatSite.ApplySpawnFlight(ref plain, splitOil, ComponentCatalog.CompGunId);
                CombatWeapon bay = Instant(0, 6f, false, 20f);
                bay.Reading = new CombatReading { Carrier = CombatCarrier.Summon, DroneLeash = 8f };
                string n4 = CombatSite.ApplySpawnFlight(ref bay, homing, ComponentCatalog.CompDroneBayId);
                Expect(needs && n1 == null && gun.SpawnFlight == 1 && gun.Mode == CombatWeaponMode.Instant && Near(gun.ProjectileSpeed, 30f) && Near(gun.ProjectileRadius, 0.15f) && Near(gun.ProjectileLife, 0.8f)
                       && n2 == null && ram.SpawnFlight == 0 && n3 == null && CombatKernel.WeaponKey(plain) == key0 && n4 == null && bay.SpawnFlight == 1 && Near(bay.ProjectileSpeed, 30f) && Near(bay.ProjectileLife, 0.32f),
                    $"[寻的]（需要飞行）+ 连射器：置 SpawnFlight、Mode 仍是即时、弹速 30 / 半径 0.15 / 寿命 = 射程 20 ÷ 30 × 1.2 = {gun.ProjectileLife:0.##} 秒；+ 液压刺锤（不实体化）不置位；"
                    + $"[分裂, 漏油]（不需要飞行）武器逐字不变（武器键相同）；+ 蜂群无人机舱：寿命按牵引绳 8 ÷ 30 × 1.2 = {bay.ProjectileLife:0.##} 秒");
                var rows = new List<EmgTreeRow>();
                EmgProgramTreeText.BuildRows(homing, rows);
                var rows2 = new List<EmgTreeRow>();
                EmgProgramTreeText.BuildRows(splitOil, rows2);
                string flightText = GameText.Get("emg.tree.spawn_flight");
                Expect(EmgProgramTreeText.SpawnsFlight(homing) && rows.Count(x => x.Kind == EmgTreeRowKind.Flight && x.Text == flightText) == 1 && rows.Count(x => x.Kind == EmgTreeRowKind.Heat) == 1
                       && !EmgProgramTreeText.SpawnsFlight(splitOil) && rows2.All(x => x.Kind != EmgTreeRowKind.Flight),
                    $"程序树预览：需要飞行的程序多一行“{flightText}”（ADR U4，待用户确认），热量行仍只有 1 行；不需要飞行的程序没有这一行");
                CheckHotfixFlightGuards(homing, splitOil);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            string[] keys = { "squad.ev.launched", "emg.tree.spawn_flight", CombatSite.NoteFlightMissing, EmgProgramTreeText.FixKey(CombatSite.NoteFlightMissing) };
            string missing = string.Join("、", keys.Where(x => !GameText.Has(x)));
            Expect(missing.Length == 0 && GuidanceHooks.Known.Contains(GuidanceHooks.EmgFirstTrigger) && GuidanceHooks.EmgFirstTrigger == "emg_first_trigger",
                $"文本键 {string.Join(" / ", keys)} 都有{(missing.Length > 0 ? "（缺 " + missing + "）" : string.Empty)}；引导钩子 emg_first_trigger 已登记（内容归 FG15-UX-04）");
        }

        /// <summary>K 段（审查修复）：ApplySpawnFlight 的两条不置位规则、真实编译的定时 / 近炸根弹体在飞行中放子载荷、不需要飞行的程序逐步 StateHash 不变。调用方已切到 emg.stage = 1。</summary>
        private static void CheckHotfixFlightGuards(EmgProgramResult homing, EmgProgramResult splitOil)
        {
            // 炮塔的射弹武器（Mode = 弹体）本来就飞：不置 SpawnFlight，寿命 / 武器键不变（“炮塔现有弹体不变”）。
            CombatWeapon turretBolt = Bolt(0, 0f);
            string tkey = CombatKernel.WeaponKey(turretBolt);
            string n5 = CombatSite.ApplySpawnFlight(ref turretBolt, homing, ComponentCatalog.CompGunId);
            // 维修束：装配编译没有攻击出口（HasOutput = 0），内核 FireAtCore 报 NoCombatOutput、根本不出手——不实体化、不放伤害子载荷（战场友军修复出手见 DEBT-EMG1TRIG01-03）。
            var repairPreview = new BlueprintCircuitPreview
            {
                PrimaryId = ComponentCatalog.CompRepairBeamId, ChassisId = ChassisCatalog.ChassisHoverId, HasCombatOutput = false, FirmwareIds = new[] { "fw_homing" },
            };
            CombatWeapon repair = CombatSite.MachineWeaponFrom(repairPreview);
            string rkey = CombatKernel.WeaponKey(repair);
            string n6 = CombatSite.ApplySpawnFlight(ref repair, homing, ComponentCatalog.CompRepairBeamId);
            bool repairSame = CombatKernel.WeaponKey(repair) == rkey;
            using (var k = new CombatKernel(Cfg(), 16))
            {
                repair.Program = CombatSite.RegisterProgram(k, homing);
                int me = Me(k, new double2(0, 0), k.AddWeapon(repair));
                int foe = Foe(k, new double2(0, 6), 50f);
                CombatFireResult rr = k.FireAt(me, foe, k.Time);
                StepN(k, 30);
                Expect(n5 == null && turretBolt.SpawnFlight == 0 && CombatKernel.WeaponKey(turretBolt) == tkey
                       && repair.HasOutput == 0 && n6 == null && repair.SpawnFlight == 0 && repairSame && rr == CombatFireResult.NoCombatOutput
                       && k.ProjectileCount == 0 && k.EmgPendingCount == 0 && Near(Hp(k, foe), 50f) && k.EmgBusStats.FlightShots == 0,
                    $"不置 SpawnFlight：炮塔射弹武器（本来就飞，寿命与武器键不变）；维修束 + [寻的]（没有攻击出口，开火 {rr}，不生成弹体、不记待生成请求、敌人不掉血；友军修复出手见 DEBT-EMG1TRIG01-03）");
            }

            // 编译器判“需要飞行”的主入口：根节点读定时 / 近炸（[延时引信] / [分叉]）。真实编译 → ApplySpawnFlight 置位 → 根弹体还在飞时就按定时 / 近炸放出子载荷。
            foreach (string fw in new[] { "fw_delayfuse", "fw_fork" })
            {
                EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, FirmwareCarrier.Projectile);
                bool needs = !pr.IsEmpty && (pr.Program.Flags & EmgProgramFlags.NeedsFlight) != 0;
                using var k = new CombatKernel(Cfg(), 16);
                CombatWeapon gw = Instant(CombatSite.RegisterProgram(k, pr), 10f, false, 30f);
                string note = CombatSite.ApplySpawnFlight(ref gw, pr, ComponentCatalog.CompGunId);
                int me = Me(k, new double2(0, 0), k.AddWeapon(gw));
                int foe = Foe(k, new double2(0, 25), 100f);
                CombatFireResult r = gw.SpawnFlight == 1 ? k.FireAt(me, foe, k.Time) : CombatFireResult.NoWeapon;
                bool whileFlying = false;
                for (int i = 0; i < 90; i++)
                {
                    StepN(k, 1);
                    whileFlying |= Subs(k).Count > 0 && Projectiles(k, EmgProjFlags.Flight).Count > 0;
                }
                Expect(needs && note == null && gw.SpawnFlight == 1 && r == CombatFireResult.Launched && whileFlying && k.EmgBusStats.Splits >= 1 && k.EmgBusStats.SubSpawned >= 1,
                    $"[{fw}]（真实编译，根节点读{(fw == "fw_delayfuse" ? "定时" : "近炸")}，需要飞行）+ 连射器：置 SpawnFlight、开火 {r}；根弹体还在飞时放出子载荷（放出 {k.EmgBusStats.Splits} 次、生成 {k.EmgBusStats.SubSpawned} 颗）");
            }

            // 不需要飞行的程序：经 ApplySpawnFlight 的武器与原武器在两个内核里逐步 StateHash 相同（热更层不碰武器，内核行为逐字不变）。
            using (var a = new CombatKernel(Cfg(), 16))
            using (var b = new CombatKernel(Cfg(), 16))
            {
                CombatWeapon wa = Instant(CombatSite.RegisterProgram(a, splitOil), 10f, false, 20f);
                CombatWeapon wb = Instant(CombatSite.RegisterProgram(b, splitOil), 10f, false, 20f);
                string na = CombatSite.ApplySpawnFlight(ref wa, splitOil, ComponentCatalog.CompGunId);
                int ma = Me(a, new double2(0, 0), a.AddWeapon(wa)), mb = Me(b, new double2(0, 0), b.AddWeapon(wb));
                int fa = Foe(a, new double2(0, 4), 200f), fb = Foe(b, new double2(0, 4), 200f);
                Foe(a, new double2(2, 5), 100f);
                Foe(b, new double2(2, 5), 100f);
                CombatFireResult ra = a.FireAt(ma, fa, a.Time), rb = b.FireAt(mb, fb, b.Time);
                bool every = a.StateHash() == b.StateHash();
                for (int i = 0; i < 60; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    every &= a.StateHash() == b.StateHash();
                }
                Expect(na == null && wa.SpawnFlight == 0 && ra == CombatFireResult.Ok && rb == ra && every && a.EmgBusStats.SubSpawned >= 1,
                    $"[分裂, 漏油]（不需要飞行）：经 ApplySpawnFlight 的武器与原武器开火结果相同（{ra}），60 步内每一步 StateHash 逐位一致（{a.StateHash():X16}）");
            }
        }

        // ── L. 耗时 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · L. 耗时（FGR-EMG-200；LANDING-PLAN 第 12 节 #2 推荐默认：3x 倍速也守 120 帧，每步 ≤ 1.5 毫秒）：128 次命中触发同一步各放 4 份（512 颗子弹药）、弹体池满时 512 条请求顺延");
            foreach (int cap in new[] { 4096, 64 })
            {
                CombatConfig cfg = Cfg();
                cfg.ProjectileCapacity = cap;
                using var k = new CombatKernel(cfg, 300);
                int p = Tree(k, Root().Read(EmgEventKind.Hit), Child(0, EmgEventKind.Hit, 4));
                int w = k.AddWeapon(Instant(p));
                var pairs = new List<(int me, int foe)>();
                for (int i = 0; i < 128; i++)
                {
                    double x = (i % 16) * 12.0, y = (i / 16) * 12.0;
                    pairs.Add((Me(k, new double2(x, y), w), Foe(k, new double2(x, y + 4), 1e6f)));
                }
                foreach (var pr in pairs)
                {
                    k.FireAt(pr.me, pr.foe, k.Time); // 预热（Burst 编译已在前面的段完成）
                }
                StepN(k, 90);
                foreach (var pr in pairs)
                {
                    k.FireAt(pr.me, pr.foe, k.Time);
                }
                var ms = new List<double>();
                for (int s = 0; s < 8; s++)
                {
                    StepN(k, 1);
                    ms.Add(k.LastStepMs);
                }
                double worst = ms.Max();
                bool ok = cap == 4096 ? k.EmgBusStats.SubSpawned >= 1024 : k.EmgBusStats.SpawnDeferred > 0;
                PerfGate.Expect(ok,
                    $"弹体池 {cap}：{(cap == 4096 ? "512 颗同一步生成" : "512 条请求顺延")}的内核一步最慢 {worst:0.###} 毫秒（8 步：{string.Join(" / ", ms.Select(x => x.ToString("0.###")))}；3x 每步上限 1.5 毫秒）",
                    new[] { PerfGate.Le(worst, 1.5, $"子载荷生成 / 顺延的一步（毫秒，池 {cap}）") }, Expect, Line);
            }
        }

        // ── W. 真实家园地点 ─────────────────────────────────────────────────────────────

        private static void CheckWorld()
        {
            Line("  · W. 真实家园地点（emg.stage = 1）：蓝图 [分裂, 漏油] 经装配编译进地点内核；命中放子载荷、落点各一摊油；第一次放子载荷发引导钩子 emg_first_trigger；同一种子的世界在 1x / 0.5x / 3x 下跑同样的游戏步数哈希一致、暂停不推进");
            UseStage1();
            var touched = new List<string>();
            Action<string> onTouch = id => touched.Add(id);
            GuidanceHooks.Touched += onTouch;
            try
            {
                var hashes = new List<ulong>();
                var oils = new List<int>();
                var hooks = new List<bool>();
                string info = string.Empty;
                bool compiledAll = true;
                ulong before = 0, paused = 1;
                foreach (float speed in new[] { 1f, 0.5f, 3f })
                {
                    if (!BuildWorld(out CombatSite site, out int unit, out CombatWeapon w, out CombatFireResult fr))
                    {
                        Fail("夹具：家园地点或测试机器没就位");
                        return;
                    }
                    CombatKernel k = site.Kernel;
                    compiledAll &= w.Program >= 0 && k.TryGetProgram(w.Program, out EmgProgram pg) && pg.NodeCount == 2 && w.SpawnFlight == 0 && fr == CombatFireResult.Ok;
                    touched.Clear();
                    GameClock.SetSpeed(speed);
                    long target = GameClock.Ticks + 90;
                    int guard = 0;
                    while (GameClock.Ticks < target && guard++ < 2000)
                    {
                        WorldSimulation.Frame(1f / 60f, target);
                    }
                    GameClock.SetSpeed(1f);
                    hashes.Add(k.StateHash());
                    oils.Add(Zones(k).Count(z => z.StatusMask == NamedReactionCatalog.BitOf("Oil")));
                    hooks.Add(touched.Contains(GuidanceHooks.EmgFirstTrigger));
                    info = $"内核武器程序 {w.Program}、开火 {fr}";
                    if (speed == 3f)
                    {
                        before = k.StateHash();
                        GameClock.SetPaused(true);
                        for (int i = 0; i < 60; i++)
                        {
                            WorldSimulation.Frame(1f / 60f);
                        }
                        paused = k.StateHash();
                        GameClock.SetPaused(false);
                    }
                }
                Expect(compiledAll && oils[0] >= 1 && oils.Distinct().Count() == 1 && hashes.Distinct().Count() == 1 && before == paused && hooks.All(x => x),
                    $"装配编译：{info}（2 个节点，不需要飞行、不置 SpawnFlight）；1.5 游戏秒内落下 {oils[0]} 摊油；引导钩子 emg_first_trigger {(hooks.All(x => x) ? "每个世界都发了" : "没发")}；"
                    + $"1x / 0.5x / 3x 同样 90 步哈希 {hashes.Distinct().Count()} 种、油摊 {string.Join("/", oils)}；暂停 60 帧哈希{(before == paused ? "不变" : "变了")}");
            }
            finally
            {
                GuidanceHooks.Touched -= onTouch;
                EmgCatalog.ResetForTests();
            }
        }

        /// <summary>同一种子的新世界：家园 + 一台装 [分裂, 漏油] 的连射器机器（装配登记走正式编译）+ 前方 4 个敌人，机器对第一个开火一次。</summary>
        private static bool BuildWorld(out CombatSite site, out int unit, out CombatWeapon w, out CombatFireResult fr)
        {
            site = null;
            unit = 0;
            w = default;
            fr = default;
            SignalUplinkService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            CampaignState s = FgProductionSelfCheck.NewWorld(9713, true, 2000);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_split", "fw_oilleak" });
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != Bp)
                .Append(new BlueprintRecord { BlueprintId = Bp, DisplayName = Bp, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, Bp, HomeValleyLayout.RegionId, new Vector2(4f, 4f), 200f, 200f);
            MachineLoadoutRegistry.Register(s, m.LogicId, Bp, 1);
            WorldSimulation.StepMany(2);
            site = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (site == null || !m.Success || !site.TryGetMachineUnit(m.LogicId, out unit) || !site.Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                return false;
            }
            CombatKernel k = site.Kernel;
            k.TryGetWeapon(v.Weapon, out w);
            double2 at = v.Position;
            int a = Foe(k, at + new double2(0, 4), 5000f);
            Foe(k, at + new double2(2, 5), 5000f);
            Foe(k, at + new double2(-2, 5), 5000f);
            Foe(k, at + new double2(0, 8.5), 5000f);
            fr = k.FireAt(unit, a, k.Time);
            return true;
        }

        // ── 断言 ─────────────────────────────────────────────────────────────

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
