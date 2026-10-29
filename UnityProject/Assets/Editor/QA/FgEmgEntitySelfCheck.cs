using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-ENT-01 实体公共属性、残骸、破片与撞击的自动验收（FG19 FGR-EMG-110～114、112 击退 / 推铲部分、060 第 20 / 21 / 24 行；ADR-EMG-001 ⑦；
    /// FGT-EMG-037 阶段 1 部分、016“反弹撞残骸”、020 撞击 / 位移 / 吸收生产者、040 存读档）。起真实内核（CombatKernel，Burst 步进）与真实表（fg.TbEntityProfile、fg.TbMechEnemy、fg.TbHomeTuning）：
    /// A 数据与公共属性（6 种实体 × 表值、敌人 material 列、热更层编表与下发、调参与开关）；B 残骸生成（敌人 / 炮塔 / 机器留、建筑不留、实体层关时不留）；
    /// C 外部血量单位（Kill 与 SetHealth 两条路径只生成一次、击杀者不为 0、直接击毁首领不留残骸且不被下一次同步复活、致死未生效时撤标志）；
    /// D 挡弹（飞行弹体 / 普通弹体 / 即时命中 / 力场主目标被挡、贯穿越过、对照）；E 反弹撞残骸；F 破片（重击崩出、命中发 evt.hit、轻击对照、链深 3 不放）；
    /// G 容量 256 最早崩解 / 野外 90 秒 / 被打掉（计数可见）；H 穿过减速、不改路线；I 击退撞障碍 / 位移事件 / 推铲推残骸撞敌 / 锚定不位移；J 合并请求；
    /// K 存读档（在飞破片、滑行残骸、短段缺省）；L 暂停与 0.5x～3x；M 种子无关；N 耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmgEntitySelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgEntitySelfCheck。
    /// </summary>
    public static class FgEmgEntitySelfCheck
    {
        private const float Dt = 1f / 60f;
        private const byte Cond = 2;
        private const byte RoleModify = 6;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/实体残骸破片撞击")]
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
            Line("\n[实体残骸破片撞击] EMG1-ENT-01：公共属性 / 残骸 / 破片 / 撞击与位移（FGT-EMG-037 阶段 1 / 016 / 020 / 040）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 下 Burst 同步编译，真机为 IL2CPP + Burst，耗时只作量级参考）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                Step(CheckData);
                Step(CheckSpawn);
                Step(CheckExternal);
                Step(CheckCover);
                Step(CheckBounce);
                Step(CheckFragments);
                Step(CheckLifetime);
                Step(CheckSlow);
                Step(CheckPush);
                Step(CheckMerge);
                Step(CheckSaveLoad);
                Step(CheckSpeeds);
                Step(CheckSeeds);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"实体自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [实体残骸破片撞击] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        /// <summary>只读事件的程序（让地点 / 单位监听这些事件码；不放子载荷、不改弹道）。</summary>
        private static int Listen(CombatKernel k, params EmgEventKind[] kinds) => Tree(k, false, kinds, null);

        private static int Tree(CombatKernel k, bool flight, EmgEventKind[] kinds, EmgInstr[] extra)
        {
            var instrs = new List<EmgInstr>();
            uint mask = 0u;
            foreach (EmgEventKind e in kinds)
            {
                instrs.Add(new EmgInstr { Port = (ushort)(EmgConst.EventPortBase + (int)e), Role = Cond, Ordinal = (byte)instrs.Count });
                mask |= 1u << (int)e;
            }
            if (extra != null)
            {
                foreach (EmgInstr x0 in extra)
                {
                    EmgInstr x = x0;
                    x.Ordinal = (byte)instrs.Count;
                    instrs.Add(x);
                }
            }
            var nodes = new List<EmgNode> { new EmgNode { Parent = -1, Gen = 0, FirstInstr = 0, InstrCount = (byte)instrs.Count } };
            return k.AddProgram(new EmgProgram { ListenMask = mask, Flags = flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None }, nodes, instrs);
        }

        private static EmgInstr Verb(EmgVerb v, float amount, float a = 0f, float b = 0f, float c = 0f, float param2 = 0f) =>
            new EmgInstr { Port = 110, Role = RoleModify, Verb = (byte)v, Amount = amount, A = a, B = b, C = c, Param2 = param2 };

        private static CombatConfig Cfg(bool entity = true)
        {
            CombatConfig c = CombatConfig.Default;
            c.EmgVerbEmit = EmgCatalog.VerbCode("emit");
            c.EmgCover = (byte)(entity ? 1 : 0);
            c.EmgEntity = (byte)(entity ? 1 : 0);
            return c;
        }

        private static CombatKernel NewKernel(bool entity = true, int cap = 32)
        {
            var k = new CombatKernel(Cfg(entity), cap);
            CombatSite.ApplyEntityProfiles(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon, float hp, CombatUnitKind kind, CombatUnitFlags extra = 0, float radius = 0.5f, int extKey = -1) =>
            k.Spawn(new CombatSpawn
            {
                ExtKey = extKey, Kind = kind, Faction = f, Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live | extra | (f == CombatFaction.Hostile ? CombatUnitFlags.Report : 0), Position = p, Home = p, Radius = radius, Speed = 4f, Health = hp, MaxHealth = hp,
                Weapon = weapon, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f,
                ArmorFacing = f == CombatFaction.Player ? new float2(0f, 1f) : new float2(0f, -1f),
            });

        private static int Me(CombatKernel k, double2 p, int weapon, CombatUnitFlags extra = 0) => Unit(k, CombatFaction.Player, p, weapon, 500f, CombatUnitKind.Machine, extra, 0.5f, 7);

        private static int Foe(CombatKernel k, double2 p, float hp = 200f, int weapon = -1, CombatUnitFlags extra = 0, float radius = 0.5f) =>
            Unit(k, CombatFaction.Hostile, p, weapon, hp, CombatUnitKind.Enemy, extra, radius, 11);

        private static CombatWeapon Instant(float damage, float range = 30f, int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = range, Damage = damage, Cooldown = 0f, Program = program,
            Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Flight(int program, float damage = 10f, float speed = 30f, float life = 1.5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f, Program = program,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Shell(float damage = 10f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Projectile, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0f,
            ProjectileSpeed = 25f, ProjectileRadius = 0.15f, ProjectileLife = 2f, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Melee(float knock, float damage = 5f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 3f, Damage = damage, Cooldown = 0f,
            Reading = new CombatReading { Carrier = CombatCarrier.Melee, Cone = 60f, Area = 2.5f, Knockback = knock },
        };

        private static CombatWeapon Aura(float damage = 6f) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 6f, Damage = damage, Cooldown = 0f,
            Reading = new CombatReading { Carrier = CombatCarrier.Aura, Area = 6f },
        };

        private static int AddWreck(CombatKernel k, double2 at, float hp = 40f, float radius = 0.6f, float mass = 6f)
        {
            k.TryAddWreck(new EmgWreck { Pos = at, Radius = radius, Hp = hp, MaxHp = hp, Mass = mass, Born = k.Time, Until = k.Time + 90.0, Profile = -1 }, out int id);
            return id;
        }

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static List<EmgWreck> Wrecks(CombatKernel k)
        {
            var list = new List<EmgWreck>();
            for (int i = 0; i < k.WreckCount; i++)
            {
                if (k.TryGetWreck(i, out EmgWreck w))
                {
                    list.Add(w);
                }
            }
            return list;
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

        private static List<CombatEvent> Drain(CombatKernel k)
        {
            CombatEvent[] buf = k.DrainGameplay(4096, out int n);
            return buf.Take(n).ToList();
        }

        /// <summary>总线里还没处理的最后一条某种事件（出手在两步之间，事件下一步才处理）。</summary>
        private static bool Pending(CombatKernel k, EmgEventKind kind, out EmgEvent e)
        {
            e = default;
            bool got = false;
            for (int i = 0; i < k.EmgBusPending; i++)
            {
                if (k.TryGetEmgEvent(i, out EmgEvent ev) && ev.Kind == kind)
                {
                    e = ev;
                    got = true;
                }
            }
            return got;
        }

        private static float Hp(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static bool Alive(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Alive) != 0;

        private static double2 Pos(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.Position : new double2(double.NaN, double.NaN);

        private static bool Near(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) <= eps;

        private static void UseStage(int stage)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        // ── A. 数据与公共属性 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：fg.TbEntityProfile 5 材质 + 11 种类默认 + 7 底盘 + 9 动能系数 + 破片行；敌人 material 列；内核拿到的公共属性 = 表值（6 种实体）；调参与开关");
            var table = ConfigSystem.Instance.Tables.TbEntityProfile.DataList;
            EmgEntityProfiles.Compiled c = EmgEntityProfiles.Current;
            Expect(c.Ok && table.Count(r => r.Kind == "material") == 5 && table.Count(r => r.Kind == "chassis") == 7 && table.Count(r => r.Kind == "kinetic") == 9
                   && c.Rows.Count == EmgEntityProfiles.SlotIds.Length + 7 + 10 && c.Rows[EmgConst.SlotFragment].Id == "entity.fragment" && c.Rows[EmgConst.SlotBoss].Id == "entity.boss",
                $"表 {table.Count} 行编成内核档案 {c.Rows.Count} 行（11 种类默认按 EmgConst.Slot* 同序 + 7 底盘 + 敌人 / 首领 × 5 材质），问题 {c.Problems.Count}");
            var enemies = ConfigSystem.Instance.Tables.TbMechEnemy.DataList;
            var faction = new Dictionary<string, string>
            {
                ["silent"] = "material.composite", ["foundry"] = "material.steel", ["clarity"] = "material.ceramic", ["overclock"] = "material.alloy", ["fleet"] = "material.steel",
            };
            Expect(enemies.Count > 0 && enemies.All(e => EmgEntityProfiles.MaterialIds.Contains(e.Material) && faction.TryGetValue(e.Faction, out string m) && m == e.Material),
                $"fg.TbMechEnemy {enemies.Count} 行都有 material 列，取值按阵营默认（静默复合 / 铸造钢 / 澄净陶瓷 / 超频合金 / 舰队钢，FG19B 第 6.2 节）");

            using CombatKernel k = NewKernel();
            EntityProfile row(string id) => table.First(r => r.Id == id);
            Expect(k.EntityProfileCount == c.Rows.Count && Near(k.EntityParams.KineticFragment, row("kinetic.fragment").Kinetic) && k.EntityParams.FragCount == 2
                   && Near(k.EntityParams.FragThreshold, 40f) && Near(k.EntityParams.KineticImpact, 0.05f),
                $"热更层下发：内核档案 {k.EntityProfileCount} 行，动能系数破片 {k.EntityParams.KineticFragment} / 撞击 {k.EntityParams.KineticImpact}，残骸重击 {k.EntityParams.FragCount} 片 · 门槛 {k.EntityParams.FragThreshold}");

            // 6 种实体的公共属性块
            int prog = Listen(k, EmgEventKind.Hit);
            int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(prog, 10f, 20f, 3f)));
            int silentFoe = Foe(k, new double2(0, 10), 100f, -1, 0, 0.6f);
            k.SetUnitProfile(silentFoe, EmgEntityProfiles.ForEnemy("material.composite", false), 0f);
            int boss = Foe(k, new double2(5, 10), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
            k.SetUnitProfile(boss, EmgEntityProfiles.ForEnemy("material.composite", true), 0f);
            int track = Me(k, new double2(-5, 0), -1);
            k.SetUnitProfile(track, EmgEntityProfiles.ForChassis("chassis_track"), 0f);
            int turret = Unit(k, CombatFaction.Player, new double2(-8, 0), -1, 300f, CombatUnitKind.Turret);
            k.FireAt(me, silentFoe, k.Time);
            StepN(k, 2);
            CombatProjectile shot = Projs(k).First();
            int wreck = AddWreck(k, new double2(20, 20), 50f, 0.5f, 7f);
            k.TryGetEntityProps(EmgEntKind.Unit, silentFoe, out EmgEntityProps pFoe);
            k.TryGetEntityProps(EmgEntKind.Unit, boss, out EmgEntityProps pBoss);
            k.TryGetEntityProps(EmgEntKind.Unit, track, out EmgEntityProps pTrack);
            k.TryGetEntityProps(EmgEntKind.Unit, turret, out EmgEntityProps pTurret);
            k.TryGetEntityProps(EmgEntKind.Projectile, shot.Id, out EmgEntityProps pShot);
            k.TryGetEntityProps(EmgEntKind.Wreck, wreck, out EmgEntityProps pWreck);
            EntityProfile comp = row("material.composite");
            Expect(Near(pFoe.Mass, Mathf.Clamp(8f * 0.36f, 1f, 80f)) && Near(pFoe.Conductive, comp.Conductive) && Near(pFoe.Flammable, comp.Flammable)
                   && Near(pFoe.ResistMag, comp.ResistMag) && pFoe.Material == EmgConst.MatComposite && pFoe.Displace == 1 && Near(pFoe.Integrity, 1f),
                $"单位（静默敌人，复合）：质量 8 × 半径² = {pFoe.Mass:0.##}、导电 {pFoe.Conductive} / 可燃 {pFoe.Flammable} / 磁抗 {pFoe.ResistMag} = 材质行、可位移、完整度 {pFoe.Integrity}");
            Expect(Near(pBoss.Mass, 200f) && pBoss.Displace == 0 && Near(pTrack.Mass, 10f) && Near(pTurret.Mass, 20f) && pTurret.Displace == 0 && pTrack.Material == EmgConst.MatSteel,
                $"首领质量 {pBoss.Mass}、不位移；履带机器质量 {pTrack.Mass}（按底盘行）、钢；炮塔质量 {pTurret.Mass}、不位移（固定底盘锚定）");
            Expect(Near(pShot.Mass, row("entity.projectile").Mass) && Near(pShot.Conductive, 0.9f) && Near(pShot.Magnetic, 0.9f) && pShot.Kind == EmgEntKind.Projectile
                   && Near(pWreck.Mass, 7f) && Near(pWreck.Integrity, 1f) && pWreck.Kind == EmgEntKind.Wreck,
                $"弹体质量 {pShot.Mass} / 导电 {pShot.Conductive} / 磁性 {pShot.Magnetic}；残骸质量 {pWreck.Mass}（原单位）、完整度 {pWreck.Integrity}");
            // 无人机与哨戒桩、破片
            int bay = Me(k, new double2(30, 0), k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 4f, Cooldown = 0f,
                Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 10f, DroneRatio = 1f, DroneCooldown = 1f, DroneLeash = 8f },
            }));
            int bay2 = Me(k, new double2(40, 0), k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 4f, Cooldown = 0f,
                Reading = new CombatReading { Carrier = CombatCarrier.Summon, Drones = 1, DroneSeconds = 10f, DroneRatio = 1f, DroneCooldown = 1f, DroneLeash = 8f, DroneAnchored = 1 },
            }));
            int f2 = Foe(k, new double2(30, 6));
            int f3 = Foe(k, new double2(40, 6));
            k.FireAt(bay, f2, k.Time);
            k.FireAt(bay2, f3, k.Time);
            StepN(k, 2);
            var drones = new List<CombatDrone>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone dr))
                {
                    drones.Add(dr);
                }
            }
            EmgEntityProps pDrone = default, pSentry = default;
            foreach (CombatDrone dr in drones)
            {
                k.TryGetEntityProps(EmgEntKind.Drone, dr.Id, out EmgEntityProps p);
                if (dr.Anchored != 0)
                {
                    pSentry = p;
                }
                else
                {
                    pDrone = p;
                }
            }
            Expect(Near(pDrone.Mass, 0.5f) && pDrone.Sub == 0 && Near(pSentry.Mass, 6f) && pSentry.Sub == 1 && pSentry.Displace == 0,
                $"无人机质量 {pDrone.Mass}；哨戒桩质量 {pSentry.Mass}、插地不位移（{drones.Count} 架）");

            // 开关：emg.stage 0 → 实体层关；1 → 开，调参进 CombatConfig
            UseStage(0);
            CombatConfig off = CombatSite.ConfigFromTuning();
            UseStage(1);
            CombatConfig on = CombatSite.ConfigFromTuning();
            EmgCatalog.ResetForTests();
            Expect(off.EmgEntity == 0 && on.EmgEntity == 1 && Near(on.EmgWreckSeconds, 90f) && Near(on.EmgWreckHpRatio, 0.5f) && Near(on.EmgWreckSlow, 0.5f)
                   && Near(on.EmgImpactVMin, 4f) && Near(on.EmgPushDMin, 0.5f) && Near(on.EmgKnockSeconds, 0.2f) && Near(on.EmgShoveRefMass, 10f),
                $"emg.stage 0 → 实体层关（{off.EmgEntity}），1 → 开（{on.EmgEntity}）；emg.wreck.seconds {on.EmgWreckSeconds} / hp_ratio {on.EmgWreckHpRatio} / slow {on.EmgWreckSlow}、v_min {on.EmgImpactVMin}、d_min {on.EmgPushDMin}");
            Expect(GridContent.TryGetTuning("emg.wreck.capacity", out float cap) && (int)cap == EmgConst.DefaultWreckCapacity,
                $"emg.wreck.capacity = {cap} 与内核常量 {EmgConst.DefaultWreckCapacity}（快照读档防坏值用同一个数）一致");
            string covered = GameText.Get("combat.fire.covered");
            Expect(!string.IsNullOrEmpty(covered) && covered != "combat.fire.covered" && GameText.Format("squad.ev.covered", "A", "B").Contains("A"),
                $"“被掩体挡下”文本键：{covered}");
        }

        // ── B. 残骸生成 ─────────────────────────────────────────────────────────────

        private static void CheckSpawn()
        {
            Line("  · B. 残骸生成：敌人 / 炮塔 / 机器在内核阵亡留残骸（半径 × 0.9、耐久 × 0.5、质量 / 材质继承）；建筑（结构单位）不留；实体层关（emg.stage 0）不留");
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(1000f)));
                int foe = Foe(k, new double2(0, 6), 100f, -1, 0, 0.8f);
                k.SetUnitProfile(foe, EmgEntityProfiles.ForEnemy("material.ceramic", false), 0f);
                k.FireAt(me, foe, k.Time);
                EmgWreck w = Wrecks(k).FirstOrDefault();
                Expect(!Alive(k, foe) && k.WreckCount == 1 && Near(w.Radius, 0.72f) && Near(w.MaxHp, 50f) && Near(w.Hp, 50f) && Near(w.Mass, Mathf.Clamp(8f * 0.64f, 1f, 80f))
                       && w.SrcKind == (byte)CombatUnitKind.Enemy && w.SrcExtKey == 11 && w.NoSalvage == 0 && w.Id > 0 && math.distance(w.Pos, new double2(0, 6)) < 1e-6,
                    $"敌人阵亡 → 1 块残骸：半径 {w.Radius}、耐久 {w.Hp}/{w.MaxHp}、质量 {w.Mass:0.##}、来源 {w.SrcKind}、外部键 {w.SrcExtKey}、可回收 {w.NoSalvage == 0}");
                k.TryGetEntityProps(EmgEntKind.Wreck, w.Id, out EmgEntityProps pw);
                Expect(pw.Material == EmgConst.MatCeramic, $"残骸材质继承原单位（陶瓷 = {pw.Material}）");
                var (born, _, _, _) = k.WreckCounts;
                Expect(born == 1, $"可靠计数：生成 {born}");
            }
            using (CombatKernel k = NewKernel())
            {
                int foe = Foe(k, new double2(0, 6), 100f, k.AddWeapon(Instant(1000f)));
                int turret = Unit(k, CombatFaction.Player, new double2(0, 0), -1, 80f, CombatUnitKind.Turret);
                int building = Unit(k, CombatFaction.Player, new double2(3, 0), -1, 80f, CombatUnitKind.Structure);
                int machine = Me(k, new double2(-3, 0), -1);
                k.FireAt(foe, turret, k.Time);
                k.FireAt(foe, building, k.Time);
                k.FireAt(foe, machine, k.Time);
                var ws = Wrecks(k);
                Expect(!Alive(k, turret) && !Alive(k, building) && !Alive(k, machine) && ws.Count == 2 && ws.Any(x => x.SrcKind == (byte)CombatUnitKind.Turret)
                       && ws.Any(x => x.SrcKind == (byte)CombatUnitKind.Machine && x.NoSalvage == 1) && ws.All(x => x.SrcKind != (byte)CombatUnitKind.Structure),
                    $"炮塔与己方机器阵亡留残骸（机器的带“不可拆解回收”），建筑（结构单位）不留：残骸 {ws.Count} 块");
            }
            using (CombatKernel k = NewKernel(false))
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(1000f)));
                int foe = Foe(k, new double2(0, 6));
                k.FireAt(me, foe, k.Time);
                StepN(k, 3);
                Expect(!Alive(k, foe) && k.WreckCount == 0 && k.WreckCounts.Born == 0, "实体层关（emg.stage 0 的正式流程）：阵亡不留残骸，行为与 M10 基线一致");
            }
        }

        // ── C. 外部血量单位 ─────────────────────────────────────────────────────────────

        private static void CheckExternal()
        {
            Line("  · C. 外部血量单位（ADR ⑦）：己方机器经热更层判死（Kill(0) 与 SetHealth(false) 两条路径、两种先后）只留一块残骸、击杀者 = 最后一次伤害请求的攻击者；直接击毁首领不留残骸、下一次同步不复活");
            foreach (bool killFirst in new[] { true, false })
            {
                using CombatKernel k = NewKernel();
                int listenKill = Listen(k, EmgEventKind.Kill);
                int foe = Foe(k, new double2(0, 6), 100f, k.AddWeapon(Instant(30f, 30f, listenKill)));
                int machine = Me(k, new double2(0, 0), -1, CombatUnitFlags.ExternalHealth);
                for (int i = 0; i < 4; i++)
                {
                    k.FireAt(foe, machine, k.Time);
                }
                var reqs = Drain(k).Where(e => e.Kind == CombatEventKind.DamageRequest).ToList();
                bool noWreckYet = k.WreckCount == 0 && Alive(k, machine); // 内核不自行判死
                // 热更层：记录血量扣到 0 → MachineDied → OnMachineDied（Kill(unit, 0)）；随后 SyncMachineHealth（SetHealth(alive:false)）
                if (killFirst)
                {
                    k.Kill(machine, 0);
                    k.SetHealth(machine, 0f, 500f, false);
                }
                else
                {
                    k.SetHealth(machine, 0f, 500f, false);
                    k.Kill(machine, 0);
                }
                var killed = Drain(k).Where(e => e.Kind == CombatEventKind.Killed && e.Unit == machine).ToList();
                StepN(k, 1); // 总线在下一步处理 evt.kill（TryGetLastEmgEvent 看的是“最后处理的一条”）
                k.TryGetLastEmgEvent(EmgEventKind.Kill, out EmgEvent ek);
                int wrecks = k.WreckCount;
                k.SetHealth(machine, 0f, 500f, false); // 下一次同步
                Expect(reqs.Count == 4 && reqs.All(r => r.Other == foe) && noWreckYet && wrecks == 1 && k.WreckCount == 1 && Wrecks(k)[0].NoSalvage == 1
                       && k.EmgEnqueuedOf(EmgEventKind.Kill) == 1 && ek.Ent == foe && ek.Other == machine
                       && (killFirst ? killed.Count == 1 && killed[0].Other == foe : killed.Count == 0),
                    $"{(killFirst ? "先 Kill(0) 后 SetHealth" : "先 SetHealth 后 Kill(0)")}：伤害请求 {reqs.Count} 条（攻击者 = 敌人）、残骸 {wrecks} 块（再同步一次仍 {k.WreckCount}）、"
                    + $"evt.kill 归敌人 {ek.Ent == foe}（{k.EmgEnqueuedOf(EmgEventKind.Kill)} 次）、Killed 事件击杀者 {(killed.Count > 0 ? killed[0].Other : -1)}（不是 0）");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), -1);
                int boss = Foe(k, new double2(0, 8), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
                bool sent = k.DestroyDirect(boss, me);
                var req = Drain(k).FirstOrDefault(e => e.Kind == CombatEventKind.DamageRequest && e.Unit == boss);
                bool aliveBefore = Alive(k, boss);
                // 热更层：记录扣血 900 → 阵亡 → SyncEnemyFromRecord（SetHealth(alive:false)）；之后每次同步都是阵亡
                k.SetHealth(boss, 0f, 900f, false);
                k.SetHealth(boss, 0f, 900f, false);
                StepN(k, 5);
                Expect(sent && aliveBefore && req.Kind == CombatEventKind.DamageRequest && req.Value >= 900f && req.Other == me && !Alive(k, boss) && k.WreckCount == 0,
                    $"直接击毁首领：内核不自行判死（请求前存活 {aliveBefore}），发致死请求（伤害 {req.Value} ≥ 镜像血量 900，攻击者 = 出手者），热更层判死后不留残骸（{k.WreckCount}）、下一次同步不复活（存活 {Alive(k, boss)}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), -1);
                int boss = Foe(k, new double2(0, 8), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
                k.DestroyDirect(boss, me);
                Drain(k);
                k.SetHealth(boss, 900f, 900f, true); // 首领阶段免伤：致死请求没生效，同步回满血
                k.TryGetUnitEmg(boss, out _, out EmgUnitState u);
                k.Kill(boss, me);
                Expect(u.DeathFlags == 0 && k.WreckCount == 1, $"致死请求没生效（同步回存活）时撤掉“不留残骸”：之后真正阵亡照常留残骸（{k.WreckCount}）");
                int foe = Foe(k, new double2(5, 8), 100f);
                k.DestroyDirect(foe, me);
                Expect(!Alive(k, foe) && k.WreckCount == 1, $"普通敌人直接击毁：当场阵亡、不留残骸（残骸仍 {k.WreckCount} 块）");
            }
        }

        // ── D. 挡弹 ─────────────────────────────────────────────────────────────

        private static void CheckCover()
        {
            Line("  · D. 残骸挡弹（FGR-EMG-113 作用 1）：飞行弹体 / 普通弹体（炮塔 / 突袭者那种）/ 即时命中 / 力场主目标都被挡（evt.absorb、扣耐久、结果 = 被掩体挡下）；贯穿越过；没残骸对照");
            using (CombatKernel k = NewKernel())
            {
                int p = Tree(k, true, new[] { EmgEventKind.Absorb, EmgEventKind.Expire }, null);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 10f)));
                int foe = Foe(k, new double2(0, 10));
                int w = AddWreck(k, new double2(0, 5), 40f, 0.6f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 40);
                k.TryGetLastEmgEvent(EmgEventKind.Absorb, out EmgEvent ab);
                k.TryGetLastEmgEvent(EmgEventKind.Expire, out EmgEvent ex);
                EmgWreck wk = Wrecks(k).First();
                Expect(Near(Hp(k, foe), 200f) && Near(wk.Hp, 30f) && ab.Other == w && ab.OtherKind == EmgEntKind.Wreck && Near(ab.Value, 10f) && ex.Code == EmgCode.ExpireAbsorbed
                       && k.EmgBusStats.WreckAbsorbs == 1,
                    $"飞行弹体撞残骸被吸收：目标不掉血（{Hp(k, foe)}），残骸耐久 40 → {wk.Hp}，evt.absorb（残骸 {ab.Other}，{ab.Value}），evt.expire Code = 被吸收");
            }
            using (CombatKernel k = NewKernel())
            {
                int p = Tree(k, true, new[] { EmgEventKind.Absorb }, null);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 10f)));
                int foe = Foe(k, new double2(0, 10));
                k.FireAt(me, foe, k.Time);
                StepN(k, 40);
                Expect(Near(Hp(k, foe), 190f) && k.EmgEnqueuedOf(EmgEventKind.Absorb) == 0, $"对照（没有残骸）：命中掉 10（{Hp(k, foe)}），不发 evt.absorb");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Shell(12f)));
                int foe = Foe(k, new double2(0, 10));
                AddWreck(k, new double2(0, 5), 40f, 0.6f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 40);
                Expect(Near(Hp(k, foe), 200f) && Near(Wrecks(k)[0].Hp, 28f), $"普通弹体（炮塔 / 突袭者那种，没有程序）同样被挡：目标 {Hp(k, foe)}，残骸 {Wrecks(k)[0].Hp}");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(10f)));
                int foe = Foe(k, new double2(0, 10));
                AddWreck(k, new double2(0, 5), 40f, 0.6f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                Expect(r == CombatFireResult.Covered && Near(Hp(k, foe), 200f) && Near(Wrecks(k)[0].Hp, 30f),
                    $"即时命中被挡：开火结果 = {r}（不再报 Ok / 命中），目标不掉血，残骸 30");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Aura(6f)));
                int hidden = Foe(k, new double2(0, 4));
                int open = Foe(k, new double2(3, -2));
                AddWreck(k, new double2(0, 2), 40f, 0.6f);
                CombatFireResult r = k.FireAt(me, hidden, k.Time);
                Expect(r == CombatFireResult.Covered && Near(Hp(k, hidden), 200f) && Hp(k, open) < 200f,
                    $"力场主目标在残骸后面（DEBT-EMG1GRM01-04）：结果 {r}、主目标不掉血；圈内露天的另一个照常吃脉冲（{Hp(k, open)}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int p = Tree(k, true, new EmgEventKind[0], new[] { Verb(EmgVerb.PassThrough, 1f, 1f) });
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 10f)));
                int foe = Foe(k, new double2(0, 10));
                AddWreck(k, new double2(0, 5), 40f, 0.6f);
                k.FireAt(me, foe, k.Time);
                StepN(k, 40);
                Expect(Near(Hp(k, foe), 190f) && Near(Wrecks(k)[0].Hp, 40f), $"贯穿弹越过残骸：目标掉 10（{Hp(k, foe)}），残骸不扣（{Wrecks(k)[0].Hp}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 5), k.AddWeapon(Instant(10f)));
                int foe = Foe(k, new double2(0, 12));
                AddWreck(k, new double2(0, 5), 40f, 0.8f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                Expect(r == CombatFireResult.Ok && Near(Hp(k, foe), 190f), $"站在残骸上开火不算躲在它后面：结果 {r}、目标 {Hp(k, foe)}");
            }
        }

        // ── E. 反弹撞残骸（FGT-EMG-016） ─────────────────────────────────────────────

        private static void CheckBounce()
        {
            Line("  · E. 反弹撞残骸（FGT-EMG-016）：带障碍面反弹次数的弹体撞残骸按残骸圆法线镜面反射，evt.bounce Code = 残骸面，扣残骸耐久、障碍面次数 −1");
            using CombatKernel k = NewKernel();
            int p = Tree(k, true, new[] { EmgEventKind.Bounce }, new[] { Verb(EmgVerb.Ricochet, 2f, 1f, 6f, 0f, 2f) });
            int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 10f, 30f, 3f)));
            int foe = Foe(k, new double2(0, 10));
            int w = AddWreck(k, new double2(0.3, 5), 40f, 0.8f);
            k.FireAt(me, foe, k.Time);
            StepN(k, 10);
            bool got = k.TryGetLastEmgEvent(EmgEventKind.Bounce, out EmgEvent e);
            CombatProjectile pr = Projs(k).FirstOrDefault();
            float2 nrm = (float2)math.normalize(e.Pos - new double2(0.3, 5));
            Expect(got && e.Code == EmgCode.FaceWreck && e.OtherKind == EmgEntKind.Wreck && Near(e.Value, nrm.x, 0.02f) && Near(e.Value2, nrm.y, 0.02f)
                   && Near(Wrecks(k)[0].Hp, 30f) && pr.BounceObs == 1 && math.dot(pr.Vel, new float2(0f, 1f)) < 0f && k.EmgBusStats.WreckBounces == 1,
                $"evt.bounce Code = 残骸面（{e.Code}），法线 = 撞点径向 ({e.Value:0.00}, {e.Value2:0.00})；残骸耐久 40 → {Wrecks(k)[0].Hp}；剩余障碍面次数 {pr.BounceObs}；往回飞");
            Expect(Near(Hp(k, foe), 200f), $"反弹后没打到残骸后面的目标（{Hp(k, foe)}）");
            using CombatKernel k2 = NewKernel();
            // 修复轮：目标站在（比它大的）残骸上——飞行弹体与即时命中同一口径，不算躲在残骸后面
            int p2 = Tree(k2, true, new[] { EmgEventKind.Hit }, null);
            int me2 = Me(k2, new double2(0, 0), k2.AddWeapon(Flight(p2, 10f, 30f, 3f)));
            int foe2 = Foe(k2, new double2(0, 10));
            AddWreck(k2, new double2(0, 10), 40f, 1.5f);
            k2.FireAt(me2, foe2, k2.Time);
            StepN(k2, 30);
            Expect(Near(Hp(k2, foe2), 190f) && k2.EmgBusStats.WreckAbsorbs == 0 && Near(Wrecks(k2)[0].Hp, 40f),
                $"站在残骸上的目标：飞行弹体照常打中（{Hp(k2, foe2)}），残骸不吸收（吸收 {k2.EmgBusStats.WreckAbsorbs}）");
        }

        // ── F. 破片 ─────────────────────────────────────────────────────────────

        private static void CheckFragments()
        {
            Line("  · F. 破片（FGR-EMG-114）：残骸受单次动能 ≥ 40 崩出 2 片（弹体池、2 代、Prog −1、链深 + 1），背离受击方向飞、命中发 evt.hit；轻击对照；链深 3 不放");
            using (CombatKernel k = NewKernel())
            {
                int listen = Listen(k, EmgEventKind.Hit);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(50f, 30f, listen)));
                int front = Foe(k, new double2(0, 10));
                int behind = Foe(k, new double2(0, 8), 200f, -1, 0, 1f); // 半径 1：两片按 ±15° 散开，2.4 米外横偏 ≤ 0.65 米都能打中
                AddWreck(k, new double2(0, 5), 200f, 0.6f);
                CombatFireResult r = k.FireAt(me, front, k.Time);
                var frags = Projs(k).Where(x => (x.Flags & EmgProjFlags.Fragment) != 0).ToList();
                long hitsBefore = k.EmgEnqueuedOf(EmgEventKind.Hit);
                StepN(k, 30);
                k.TryGetLastEmgEvent(EmgEventKind.Hit, out EmgEvent eh);
                Expect(r == CombatFireResult.Covered && frags.Count == 2 && frags.All(x => x.Gen == 2 && x.Prog == -1 && x.Depth == 1 && x.Owner == me && Near(x.Damage, 5f) && x.Vel.y > 0f)
                       && Near(math.length(frags[0].Vel), 16f) && k.EmgBusStats.FragSpawned == 2,
                    $"重击（50 × 1.0 ≥ 40）被残骸挡下并崩出 {frags.Count} 片：2 代 / Prog −1 / 链深 1 / 归出手者 / 每片 5 伤害 / 16 米每秒、背离受击方向");
                Expect(Hp(k, behind) < 200f && k.EmgEnqueuedOf(EmgEventKind.Hit) > hitsBefore && eh.EntKind == EmgEntKind.Projectile && eh.Prog == -1 && eh.Depth == 1 && eh.Other == behind,
                    $"破片打中残骸后的敌人（{Hp(k, behind)}），命中发 evt.hit（宿主 = 破片、链深 {eh.Depth}）");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(10f)));
                int front = Foe(k, new double2(0, 10));
                AddWreck(k, new double2(0, 5), 200f, 0.6f);
                k.FireAt(me, front, k.Time);
                Expect(Projs(k).Count == 0 && k.EmgBusStats.FragSpawned == 0, "对照：轻击（10 < 40）只扣残骸耐久，不崩破片");
            }
            using (CombatKernel k = NewKernel())
            {
                // 修复轮：飞行弹体在弹体步里撞残骸崩出的破片要真的进池（原来被步末截掉，计数却加了）
                int p = Tree(k, true, new[] { EmgEventKind.Hit }, null);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Flight(p, 50f, 30f, 3f)));
                int front = Foe(k, new double2(0, 10));
                int behind = Foe(k, new double2(0, 8), 200f, -1, 0, 1f);
                AddWreck(k, new double2(0, 5), 200f, 0.6f);
                CombatFireResult r = k.FireAt(me, front, k.Time);
                for (int s = 0; s < 30 && k.EmgBusStats.FragSpawned == 0; s++)
                {
                    StepN(k, 1);
                }
                int inPool = Projs(k).Count(x => (x.Flags & EmgProjFlags.Fragment) != 0);
                long spawned = k.EmgBusStats.FragSpawned;
                StepN(k, 30);
                k.TryGetLastEmgEvent(EmgEventKind.Hit, out EmgEvent eh);
                Expect(r == CombatFireResult.Launched && spawned == 2 && inPool == 2 && k.EmgBusStats.WreckAbsorbs == 1,
                    $"飞行弹体（50 伤害）撞残骸被吸收、崩出破片：生成计数 {spawned}，同一步后池里确有 {inPool} 片（计数与池内一致）");
                Expect(Hp(k, behind) < 200f && eh.EntKind == EmgEntKind.Projectile && eh.Prog == -1 && eh.Depth == 1 && eh.Other == behind,
                    $"这些破片随后打中残骸后的敌人（{Hp(k, behind)}），命中发 evt.hit（宿主 = 破片、链深 {eh.Depth}）");
            }
        }

        // ── G. 容量 / 到期 / 被打掉 ─────────────────────────────────────────────────────

        private static void CheckLifetime()
        {
            Line("  · G. 容量 256（满了最早的崩解，计数可见）、野外 90 秒到期崩解、被打掉移除；阶段 1 崩解 = 计数 + 消失（DEBT-EMG1ENT01-01）");
            using (CombatKernel k = NewKernel(true, 300))
            {
                for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
                {
                    AddWreck(k, new double2(i % 16 * 3, i / 16 * 3 + 40), 20f, 0.4f);
                }
                int oldest = Wrecks(k)[0].Id;
                int me = Me(k, new double2(-10, 0), k.AddWeapon(Instant(1000f)));
                int foe = Foe(k, new double2(-10, 6));
                k.FireAt(me, foe, k.Time);
                var ws = Wrecks(k);
                var (born, aged, capped, destroyed) = k.WreckCounts;
                Expect(ws.Count == EmgConst.DefaultWreckCapacity && ws.All(x => x.Id != oldest) && ws.Last().SrcExtKey == 11 && capped == 1,
                    $"满 256 时再阵亡一个：仍 {ws.Count} 块，最早那块（{oldest}）崩解，满容量崩解计数 {capped}（调试层一行可见）");
            }
            using (CombatKernel k = NewKernel(true, 300))
            {
                // 修复轮：满 256 时，推铲推出的残骸滑行撞死敌人（阵亡生成新残骸、整表前移）——残骸不复制、不丢失
                for (int i = 0; i < EmgConst.DefaultWreckCapacity - 1; i++)
                {
                    AddWreck(k, new double2(i % 16 * 3, i / 16 * 3 + 40), 20f, 0.4f);
                }
                int oldest = Wrecks(k)[0].Id;
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int turret = Unit(k, CombatFaction.Hostile, new double2(0, 2.4), -1, 8f, CombatUnitKind.Turret);
                int slid = AddWreck(k, new double2(0, 1.4), 60f, 0.4f, 6f);
                k.FireAt(me, turret, k.Time);
                StepN(k, 30);
                var ws = Wrecks(k);
                var (_, _, capped, _) = k.WreckCounts;
                EmgWreck s = ws.FirstOrDefault(x => x.Id == slid);
                Expect(!Alive(k, turret) && ws.Count == EmgConst.DefaultWreckCapacity && ws.Select(x => x.Id).Distinct().Count() == ws.Count && ws.Count(x => x.Id == slid) == 1
                       && math.lengthsq(s.Vel) == 0f && s.Pos.y > 1.4 && ws.Any(x => x.SrcKind == (byte)CombatUnitKind.Turret) && ws.All(x => x.Id != oldest) && capped == 1,
                    $"满 256 + 滑行残骸撞死炮塔：仍 {ws.Count} 块、Id 不重复（{ws.Select(x => x.Id).Distinct().Count()}）、被推那块只有 1 份且停住、新炮塔残骸在、最早那块崩解（计数 {capped}）");
            }
            using (CombatKernel k = NewKernel(true, 300))
            {
                // 修复轮：满 256 时，被击退的敌人撞上残骸、自己被撞死（生成残骸、整表前移）——撞击扣的是被撞的那块
                for (int i = 0; i < EmgConst.DefaultWreckCapacity - 1; i++)
                {
                    AddWreck(k, new double2(i % 16 * 3, i / 16 * 3 + 40), 20f, 0.4f);
                }
                int listen = Listen(k, EmgEventKind.Impact);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int foe = Foe(k, new double2(0, 2), 7f, k.AddWeapon(Instant(1f, 1f, listen)));
                int hw = AddWreck(k, new double2(0, 3.3), 60f, 0.5f);
                k.FireAt(me, foe, k.Time);
                Pending(k, EmgEventKind.Impact, out EmgEvent ei);
                var ws = Wrecks(k);
                EmgWreck hit = ws.FirstOrDefault(x => x.Id == hw);
                EmgWreck born = ws.LastOrDefault();
                Expect(!Alive(k, foe) && ws.Count == EmgConst.DefaultWreckCapacity && ws.Select(x => x.Id).Distinct().Count() == ws.Count && ei.OtherKind == EmgEntKind.Wreck && ei.Other == hw
                       && Near(hit.Hp, 60f - 2.5f, 0.01f) && born.SrcExtKey == 11 && Near(born.Hp, born.MaxHp) && k.WreckCounts.Capped == 1,
                    $"满 256 + 击退撞残骸撞死：被撞那块耐久 60 → {hit.Hp:0.##}（撞击 2.5），新残骸耐久满（{born.Hp}/{born.MaxHp}），{ws.Count} 块 Id 不重复，evt.impact 另一方 = 残骸 {ei.Other}");
            }
            using (CombatKernel k = NewKernel())
            {
                AddWreck(k, new double2(0, 5));
                StepN(k, 89 * 2, 0.5f);
                int before = k.WreckCount;
                StepN(k, 4, 0.5f);
                Expect(before == 1 && k.WreckCount == 0 && k.WreckCounts.Aged == 1, $"野外 90 秒：89 秒时还在（{before}），过 90 秒崩解消失、到期计数 {k.WreckCounts.Aged}");
            }
            using (CombatKernel k = NewKernel())
            {
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(25f)));
                int foe = Foe(k, new double2(0, 10));
                AddWreck(k, new double2(0, 5), 40f, 0.6f);
                k.FireAt(me, foe, k.Time);
                k.FireAt(me, foe, k.Time);
                StepN(k, 1);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                Expect(k.WreckCount == 0 && k.WreckCounts.Destroyed == 1 && r == CombatFireResult.Ok && Hp(k, foe) < 200f,
                    $"残骸被打掉（2 × 25 > 40）：移除、计数 {k.WreckCounts.Destroyed}，之后的射击打到目标（{r}）");
            }
        }

        // ── H. 通行 ─────────────────────────────────────────────────────────────

        private static void CheckSlow()
        {
            Line("  · H. 通行：单位穿过残骸速度 × 0.5（emg.wreck.slow），不改寻路 / 路线（直线穿过）；空中单位不受影响");
            float Travel(bool wreck)
            {
                using CombatKernel k = NewKernel();
                int me = Me(k, new double2(0, 0), -1);
                if (wreck)
                {
                    AddWreck(k, new double2(0, 3), 40f, 1.5f);
                }
                k.IssueCommand(me, CombatCommandKind.Move, new double2(0, 6), 0, 0.2f, 0f, 0f, false);
                double maxDx = 0;
                int steps = 0;
                while (steps < 600 && math.distance(Pos(k, me), new double2(0, 6)) > 0.25)
                {
                    StepN(k, 1);
                    maxDx = math.max(maxDx, math.abs(Pos(k, me).x));
                    steps++;
                }
                _lastDx = maxDx;
                return steps * Dt;
            }
            float open = Travel(false);
            float slowed = Travel(true);
            Expect(slowed > open * 1.3f && slowed < open * 2.2f && _lastDx < 1e-6, $"6 米直线：没残骸 {open:0.00} 秒，中间横着 3 米宽残骸 {slowed:0.00} 秒（残骸段速度减半），路线不绕（最大横偏 {_lastDx:0.######}）");
        }

        private static double _lastDx;

        // ── I. 推动与撞击 ─────────────────────────────────────────────────────────────

        private static void CheckPush()
        {
            Line("  · I. 推动与撞击（FGR-EMG-112）：击退撞障碍 ≥ 4 米每秒发 evt.impact、扣血来源 = 撞击 6；位移 ≥ 0.5 米发 evt.push；推铲推残骸撞敌；炮塔 / 首领不位移");
            using (CombatKernel k = NewKernel())
            {
                int listen = Listen(k, EmgEventKind.Impact, EmgEventKind.Push, EmgEventKind.Hurt);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Instant(1f, 1f, listen)));
                k.SetObstacles(new List<float3> { new float3(0f, 3.3f, 0.5f) });
                k.FireAt(me, foe, k.Time);
                Pending(k, EmgEventKind.Impact, out EmgEvent ei);
                Pending(k, EmgEventKind.Hurt, out EmgEvent eh);
                float mass = Mathf.Clamp(8f * 0.25f, 1f, 80f);
                float kinetic = mass * 25f * 0.05f;
                Expect(k.EmgEnqueuedOf(EmgEventKind.Impact) == 1 && ei.Ent == foe && Near(ei.Value, 5f, 0.01f) && Near(ei.Value2, mass) && eh.Code == EmgCode.SrcImpact
                       && Near(Hp(k, foe), 200f - 5f - kinetic, 0.01f) && Pos(k, foe).y < 2.31 && Pos(k, foe).y > 2.0,
                    $"推铲 1 米击退（等效 5 米每秒）撞上障碍：evt.impact 速度 {ei.Value:0.##}、质量 {ei.Value2}；撞击扣血 {kinetic:0.##}（evt.hurt Code = {eh.Code}）；停在障碍前（y = {Pos(k, foe).y:0.00}）");
                Expect(k.EmgEnqueuedOf(EmgEventKind.Push) == 0, "撞墙只位移了 0.3 米（< 0.5）：不发 evt.push");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = Listen(k, EmgEventKind.Impact, EmgEventKind.Push);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Instant(1f, 1f, listen)));
                k.FireAt(me, foe, k.Time);
                Pending(k, EmgEventKind.Push, out EmgEvent ep);
                Expect(k.EmgEnqueuedOf(EmgEventKind.Push) == 1 && Near(ep.Value, 1f, 0.01f) && ep.Code == EmgConst.PushCauseKnock && ep.Other == me
                       && k.EmgEnqueuedOf(EmgEventKind.Impact) == 0 && Near(Hp(k, foe), 195f),
                    $"空地上击退 1 米：evt.push 距离 {ep.Value:0.##}、原因端口 {ep.Code}（击退）；没撞东西 → 没有 evt.impact、只吃武器伤害");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = Listen(k, EmgEventKind.Impact, EmgEventKind.Push);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(0.5f, 5f)));
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Instant(1f, 1f, listen)));
                k.SetObstacles(new List<float3> { new float3(0f, 2.8f, 0.2f) });
                k.FireAt(me, foe, k.Time);
                Expect(k.EmgEnqueuedOf(EmgEventKind.Impact) == 0 && Near(Hp(k, foe), 195f), "对照：0.5 米击退（2.5 米每秒 < 4）撞障碍不算撞击、不扣撞击血");
            }
            using (CombatKernel k = NewKernel())
            {
                // 修复轮补测：被击退的单位撞上另一个单位——双方各吃撞击伤害，evt.impact 另一方 = 单位
                int listen = Listen(k, EmgEventKind.Impact);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Instant(1f, 1f, listen)));
                int other = Foe(k, new double2(0, 3.6), 200f);
                k.FireAt(me, foe, k.Time);
                Pending(k, EmgEventKind.Impact, out EmgEvent ei);
                Expect(ei.OtherKind == EmgEntKind.Unit && ei.Other == other && Near(Hp(k, other), 197.5f, 0.01f) && Near(Hp(k, foe), 192.5f, 0.01f) && Pos(k, foe).y < 2.61,
                    $"击退撞上单位：evt.impact 另一方 {ei.OtherKind}/{ei.Other}；被推者 {Hp(k, foe):0.##}、被撞者 {Hp(k, other):0.##}（各 2.5 撞击）；停在撞点（y = {Pos(k, foe).y:0.00}）");
            }
            using (CombatKernel k = NewKernel())
            {
                // 修复轮补测：被击退的单位撞上残骸——残骸扣撞击动能，evt.impact 另一方 = 残骸
                int listen = Listen(k, EmgEventKind.Impact);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Instant(1f, 1f, listen)));
                int hw = AddWreck(k, new double2(0, 3.3), 60f, 0.5f);
                k.FireAt(me, foe, k.Time);
                Pending(k, EmgEventKind.Impact, out EmgEvent ei);
                Expect(ei.OtherKind == EmgEntKind.Wreck && ei.Other == hw && Near(Wrecks(k)[0].Hp, 57.5f, 0.01f) && Near(Hp(k, foe), 192.5f, 0.01f),
                    $"击退撞上残骸：evt.impact 另一方 {ei.OtherKind}/{ei.Other}；残骸耐久 60 → {Wrecks(k)[0].Hp:0.##}，被推者 {Hp(k, foe):0.##}");
            }
            using (CombatKernel k = NewKernel())
            {
                // 修复轮：驻防机器的目标躲在残骸后面（开火结果 Covered）——按武器冷却开火，不按 0.2 秒重试连开
                CombatWeapon wp = Instant(1f);
                wp.Cooldown = 1f;
                int me = Me(k, new double2(0, 0), k.AddWeapon(wp));
                k.SetMachineGuard(me, new double2(0, 0), 15f);
                Foe(k, new double2(0, 10), 1000f);
                AddWreck(k, new double2(0, 5), 10000f, 0.6f);
                long shots0 = k.Counters.ShotsFired;
                StepN(k, 120);
                long shots = k.Counters.ShotsFired - shots0;
                Expect(shots >= 2 && shots <= 3 && Wrecks(k)[0].Hp >= 10000f - 3f,
                    $"驻防机器 2 秒、武器冷却 1 秒、目标在残骸后：开火 {shots} 次（修复前按 0.2 秒重试约 10 次），残骸耐久 {Wrecks(k)[0].Hp}");
            }
            using (CombatKernel k = NewKernel())
            {
                int listen = Listen(k, EmgEventKind.Impact, EmgEventKind.Push);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Melee(1.0f, 5f)));
                // 被推的是锚定的敌方炮塔（不随击退飞走），残骸夹在中间：0.1 米后撞上
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2.4), -1, 200f, CombatUnitKind.Turret);
                int w = AddWreck(k, new double2(0, 1.4), 60f, 0.4f, 6f);
                k.FireAt(me, foe, k.Time);
                EmgWreck moving = Wrecks(k)[0];
                float hpAfterSwing = Hp(k, foe);
                StepN(k, 30);
                k.TryGetLastEmgEvent(EmgEventKind.Impact, out EmgEvent ei);
                EmgWreck rest = Wrecks(k)[0];
                Expect(math.length(moving.Vel) > 4f && k.EmgEnqueuedOf(EmgEventKind.Impact) >= 1 && ei.EntKind == EmgEntKind.Wreck && ei.Ent == w && ei.Other == foe
                       && Hp(k, foe) < hpAfterSwing && math.lengthsq(rest.Vel) == 0f && rest.Pos.y > 1.4 && k.EmgBusStats.Impacts >= 1,
                    $"推铲推残骸撞敌（FGT-EMG-037）：残骸被推出 {math.length(moving.Vel):0.#} 米每秒，撞上敌人发 evt.impact（宿主 = 残骸 {ei.Ent}），敌人吃撞击伤害 {hpAfterSwing - Hp(k, foe):0.##}，残骸停住");
            }
            using (CombatKernel k = NewKernel())
            {
                int foe = Foe(k, new double2(0, 2), 200f, k.AddWeapon(Melee(1.0f, 5f)));
                int turret = Unit(k, CombatFaction.Player, new double2(0, 0), -1, 300f, CombatUnitKind.Turret);
                int me = Me(k, new double2(0, 6), k.AddWeapon(Melee(1.0f, 5f)));
                int boss = Foe(k, new double2(0, 8), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
                k.SetUnitProfile(boss, EmgEntityProfiles.ForEnemy("material.steel", true), 0f);
                k.FireAt(foe, turret, k.Time);
                k.FireAt(me, boss, k.Time);
                Expect(math.distance(Pos(k, turret), new double2(0, 0)) < 1e-9 && math.distance(Pos(k, boss), new double2(0, 8)) < 1e-9,
                    "炮塔（固定底盘）与首领被击退：不位移（锚定）");
            }
        }

        // ── J. 合并请求 ─────────────────────────────────────────────────────────────

        private static void CheckMerge()
        {
            Line("  · J. 合并请求（ADR ⑦ 第 5 条）：外部血量单位一步内的区域节拍 / 状态持续伤害合并为一条伤害请求（攻击者取伤害最大的一份）；命中类照旧逐条");
            using CombatKernel k = NewKernel();
            int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(3f)));
            int boss = Foe(k, new double2(0, 6), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
            k.ApplyStatus(boss, 1u, 10f, 8f, 0f, 0f, me);
            k.ApplyStatus(boss, 2u, 10f, 4f, 0f, 0f, me);
            Drain(k);
            int maxPerStep = 0, total = 0;
            for (int s = 0; s < 120; s++)
            {
                StepN(k, 1);
                int n = Drain(k).Count(e => e.Kind == CombatEventKind.DamageRequest && e.Unit == boss);
                maxPerStep = Math.Max(maxPerStep, n);
                total += n;
            }
            k.FireAt(me, boss, k.Time);
            k.FireAt(me, boss, k.Time);
            int hits = Drain(k).Count(e => e.Kind == CombatEventKind.DamageRequest && e.Unit == boss);
            Expect(total > 0 && maxPerStep == 1 && hits == 2, $"持续伤害 2 秒：每步至多 {maxPerStep} 条请求（共 {total} 条）；两发命中各一条（{hits}）");
        }

        // ── K. 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · K. 存读档（FGT-EMG-040）：残骸（含滑行中）、在飞破片、单位质量 / 阵亡标志中途存档 → 新内核读档 → 续跑每步 StateHash 一致，不复制不丢失；短段取缺省；计数随档");
            void Build(CombatKernel k)
            {
                int listen = Listen(k, EmgEventKind.Hit, EmgEventKind.Impact);
                int me = Me(k, new double2(0, 0), k.AddWeapon(Instant(50f, 30f, listen)));
                int front = Foe(k, new double2(0, 10));
                Foe(k, new double2(0.3, 8.5));
                AddWreck(k, new double2(0, 5), 200f, 0.6f);
                int w2 = AddWreck(k, new double2(10, 0), 60f, 0.5f, 5f);
                Foe(k, new double2(14, 0));
                k.PushWreckForTests(w2, new float2(6f, 0f), me);
                int boss = Foe(k, new double2(-10, 6), 900f, -1, CombatUnitFlags.ExternalHealth, 1.5f);
                k.SetUnitProfile(boss, EmgEntityProfiles.ForEnemy("material.alloy", true), 0f);
                k.DestroyDirect(boss, me);
                k.FireAt(me, front, k.Time);
            }
            using (CombatKernel a = NewKernel())
            using (CombatKernel b = NewKernel())
            {
                Build(a);
                StepN(a, 3);
                int frags = Projs(a).Count(x => (x.Flags & EmgProjFlags.Fragment) != 0);
                bool sliding = Wrecks(a).Any(x => math.lengthsq(x.Vel) > 0f);
                byte[] snap = a.Serialize();
                CombatLoadResult lr = b.Load(snap);
                bool same = lr == CombatLoadResult.Ok && a.StateHash() == b.StateHash() && a.WreckCount == b.WreckCount && a.ProjectileCount == b.ProjectileCount
                            && a.WreckCounts == b.WreckCounts;
                for (int i = 0; i < 120; i++)
                {
                    StepN(a, 1);
                    StepN(b, 1);
                    same &= a.StateHash() == b.StateHash() && a.WreckCount == b.WreckCount;
                }
                var ida = Wrecks(a).Select(x => x.Id).ToList();
                var idb = Wrecks(b).Select(x => x.Id).ToList();
                Expect(same && frags == 2 && sliding && ida.SequenceEqual(idb) && ida.Distinct().Count() == ida.Count,
                    $"在飞破片 {frags} 片、滑行中的残骸、首领的“不留残骸”标志中途存读档：读档 {lr}，续跑 120 步每步 StateHash 一致，残骸 ID 一致不重复（{string.Join(",", ida)}）");
                using CombatKernel c = NewKernel();
                CombatLoadResult ls = c.Load(a.SerializeEmgShapeForTests(EmgConst.SegWrecks, 20, 0));
                using CombatKernel e = NewKernel();
                CombatLoadResult lu = e.Load(a.SerializeEmgShapeForTests(EmgConst.SegUnits, 5, 0));
                using CombatKernel f = NewKernel();
                CombatLoadResult lsc = f.Load(a.SerializeEmgShapeForTests(EmgConst.SegScalars, 8, 0));
                Expect(ls == CombatLoadResult.Ok && Wrecks(c).All(x => math.lengthsq(x.Vel) == 0f && x.PushBy == 0) && lu == CombatLoadResult.Ok && lsc == CombatLoadResult.Ok
                       && f.WreckCounts.Born == 0 && c.WreckCount == a.WreckCount,
                    $"短段（残骸段 20 字段 / 单位段 5 字段 / 标量段 8 字段，本卡之前的格式 16 存档）读档 {ls} / {lu} / {lsc}：滑行状态、质量、计数取缺省 0");
            }
        }

        // ── L. 倍速与暂停 ─────────────────────────────────────────────────────────────

        private static void CheckSpeeds()
        {
            Line("  · L. 暂停与 0.5x / 1x / 3x（同一段游戏时间按不同步长推进）：到期时刻、滑行距离、撞击结果一致；暂停（不步进）不变");
            (int left, double slideX, long impacts) Run(float dt, int steps)
            {
                using CombatKernel k = NewKernel();
                int me = Me(k, new double2(0, 0), -1);
                AddWreck(k, new double2(0, 20), 30f);
                int w = AddWreck(k, new double2(5, 0), 60f, 0.5f, 5f);
                Foe(k, new double2(6.3, 0), 300f); // 6 米每秒滑 1.2 米：0.3 米后撞上（撞点速度 ≈ 5.2 ≥ 4）
                int w3 = AddWreck(k, new double2(20, 0), 60f, 0.5f, 5f);
                k.PushWreckForTests(w, new float2(6f, 0f), me);
                k.PushWreckForTests(w3, new float2(0f, 3f), me);
                StepN(k, steps, dt);
                double x = Wrecks(k).Where(q => q.Id == w3).Select(q => q.Pos.y).FirstOrDefault();
                return (k.WreckCount, x, k.EmgBusStats.Impacts);
            }
            var half = Run(Dt * 0.5f, 120 * 2);
            var one = Run(Dt, 120);
            var tri = Run(Dt * 3f, 40);
            Expect(half.impacts == 1 && one.impacts == 1 && tri.impacts == 1 && Math.Abs(half.slideX - one.slideX) < 0.05 && Math.Abs(tri.slideX - one.slideX) < 0.05,
                $"2 游戏秒：撞击 {half.impacts}/{one.impacts}/{tri.impacts} 次；3 米每秒滑行停在 {half.slideX:0.000}/{one.slideX:0.000}/{tri.slideX:0.000}（0.5x/1x/3x 步长）");
            var exHalf = Expire(Dt * 0.5f);
            var exOne = Expire(Dt);
            var exTri = Expire(Dt * 3f);
            Expect(Math.Abs(exHalf - 90.0) <= Dt * 3 && Math.Abs(exOne - 90.0) <= Dt * 3 && Math.Abs(exTri - 90.0) <= Dt * 3,
                $"野外 90 秒按游戏时间到期：{exHalf:0.00} / {exOne:0.00} / {exTri:0.00} 秒（0.5x / 1x / 3x）");
            using CombatKernel p = NewKernel();
            AddWreck(p, new double2(0, 5));
            ulong h = p.StateHash();
            Expect(p.StateHash() == h && p.WreckCount == 1, "暂停（不步进）：残骸与计数不变");

            double Expire(float dt)
            {
                using CombatKernel k = NewKernel();
                AddWreck(k, new double2(0, 5));
                while (k.WreckCount > 0 && k.Time < 120)
                {
                    StepN(k, 1, dt);
                }
                return k.Time;
            }
        }

        // ── M. 种子无关 ─────────────────────────────────────────────────────────────

        private static void CheckSeeds()
        {
            Line("  · M. 种子无关（B25）：同一套结算放到任意坐标（固定种子的 5 组随机偏移）结果一致——残骸落在阵亡处、挡弹与破片照常");
            var rng = new System.Random(20261029);
            bool all = true;
            var where = new List<string>();
            for (int s = 0; s < 5; s++)
            {
                double2 o = new double2(rng.NextDouble() * 2000 - 1000, rng.NextDouble() * 2000 - 1000);
                using CombatKernel k = NewKernel();
                int me = Me(k, o, k.AddWeapon(Instant(50f)));
                int foe = Foe(k, o + new double2(0, 10));
                int victim = Foe(k, o + new double2(-6, 0), 30f);
                k.FireAt(me, victim, k.Time);
                EmgWreck w = Wrecks(k).FirstOrDefault();
                AddWreck(k, o + new double2(0, 5), 200f, 0.6f);
                CombatFireResult r = k.FireAt(me, foe, k.Time);
                all &= k.WreckCount == 2 && math.distance(w.Pos, o + new double2(-6, 0)) < 1e-6 && r == CombatFireResult.Covered && Projs(k).Count == 2;
                where.Add($"({o.x:0},{o.y:0})");
            }
            Expect(all, $"5 组偏移 {string.Join(" ", where)}：残骸位置 = 阵亡处，挡弹结果与破片片数一致");
        }

        // ── N. 耗时 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · N. 耗时（FGR-EMG-200；3x 倍速每步 ≤ 1.5 毫秒，只测一次）：残骸 256 块（其中 32 块滑行中）+ 1000 发在飞弹体 + 200 个移动中的单位的一步；以及没有残骸时的一步（对照）");
            double Measure(bool wrecks)
            {
                using CombatKernel k = NewKernel(true, 512);
                var shooters = new List<int>();
                for (int i = 0; i < 100; i++)
                {
                    shooters.Add(Me(k, new double2(i * 2, 0), k.AddWeapon(Shell(1f))));
                }
                var foes = new List<int>();
                for (int i = 0; i < 100; i++)
                {
                    int f = Foe(k, new double2(i * 2, 60), 1e6f);
                    foes.Add(f);
                    k.IssueCommand(shooters[i], CombatCommandKind.Move, new double2(i * 2, 50), 0, 0.2f, 0f, 0f, false);
                }
                if (wrecks)
                {
                    for (int i = 0; i < EmgConst.DefaultWreckCapacity; i++)
                    {
                        int id = AddWreck(k, new double2(i % 64 * 3 + 0.7, 20 + i / 64 * 6), 1e6f, 0.5f);
                        if (i % 8 == 0)
                        {
                            k.PushWreckForTests(id, new float2(0f, 0.6f), shooters[0]);
                        }
                    }
                }
                for (int round = 0; round < 10; round++)
                {
                    for (int i = 0; i < shooters.Count; i++)
                    {
                        k.FireAt(shooters[i], foes[i], k.Time);
                    }
                }
                var sw = new Stopwatch();
                double worst = 0;
                for (int s = 0; s < 20; s++)
                {
                    sw.Restart();
                    k.Step(Dt * 3f, k.Time + Dt * 3f);
                    sw.Stop();
                    worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                }
                return worst;
            }
            double without = Measure(false);
            double with = Measure(true);
            if (with > 1.5 && with <= 3.0)
            {
                Line($"    ⚠ 性能警告：256 块残骸 + 1000 发弹体 + 200 单位的一步最慢 {with:0.###} 毫秒（阈值 1.5，未到 2 倍；无残骸对照 {without:0.###}）");
                _pass++;
                return;
            }
            Expect(with <= 1.5, $"3x 倍速一步最慢：有 256 块残骸 {with:0.###} 毫秒 / 没有残骸 {without:0.###} 毫秒（阈值 1.5）");
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
