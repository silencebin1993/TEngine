using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
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
    /// EMG2-FLD-01 场与载体互作（FG19 FGR-EMG-112、120～122；FG19B 第 5 节 fg.TbFieldCarrierRule；FGT-EMG-036、037 场推动部分、073 阶段 2 部分）：
    /// 起真实内核（CombatSite 同一套表写入），断言 6 种实体被场推动 / 不动、弹体穿场与斑块的载荷改写、容量 64、存读档、暂停与 0.5x～3x、声明轨固件与部件场的正式路径。
    /// </summary>
    public static class FgEmgFieldSelfCheck
    {
        private const float Dt = 1f / 60f;
        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;
        private const ushort Pull = EmgFieldConst.PortPull, Push = EmgFieldConst.PortPush, Vortex = EmgFieldConst.PortVortex, Magnet = EmgFieldConst.PortMagnet,
            Deflect = EmgFieldConst.PortDeflect, Corona = EmgFieldConst.PortCorona;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/场与载体互作")]
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
            Line("\n[场与载体互作] EMG2-FLD-01：6 种场 × 6 种实体、弹体穿场 / 穿斑块的载荷改写、容量 64、存读档、倍速、声明轨与部件场（FGT-EMG-036 / 037 / 073）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；内核单线程 IJob + Burst（Editor 同步编译，真机 IL2CPP + Burst，耗时只作量级参考）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                EmgEntityProfiles.Reload();
                EmgMatterCatalog.Reload();
                EmgRuleCatalog.Reload();
                EmgPhaseCatalog.Reload();
                EmgFieldCatalog.Reload();
                Step(CheckTable);
                Step(CheckEntities);
                Step(CheckProjectileFields);
                Step(CheckCoronaDeflect);
                Step(CheckPatchRules);
                Step(CheckMagSweep);
                Step(CheckCapacityAndEdges);
                Step(CheckSaveLoadSpeed);
                Step(CheckDeclTrack);
                Step(CheckTexts);
                Step(CheckPerf);
            }
            catch (Exception e)
            {
                Fail($"场自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            Line($"  · [场与载体互作] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────

        private static CombatKernel NewKernel(int cap = 64)
        {
            CombatConfig c = CombatSite.ConfigFromTuning();
            c.NavEnabled = 0;
            var k = new CombatKernel(c, cap);
            CombatSite.ApplyEmgSubForms(k);
            CombatSite.ApplyEntityProfiles(k);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            CombatSite.ApplyFieldRules(k);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, double2 p, int weapon = -1, float hp = 400f, CombatUnitKind kind = CombatUnitKind.Enemy)
        {
            CombatUnitKind kk = f == CombatFaction.Player && kind == CombatUnitKind.Enemy ? CombatUnitKind.Machine : kind;
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = kk, Faction = f, Behavior = f == CombatFaction.Player ? CombatBehavior.Commanded : CombatBehavior.None,
                Flags = Live, Position = p, Home = p, Radius = 0.5f, Speed = 0f, Health = hp, MaxHealth = hp, Weapon = weapon, BehaviorProfile = -1, Priority = 1,
            });
            if (f == CombatFaction.Hostile && kk == CombatUnitKind.Enemy)
            {
                k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy("material.steel", false), 0f);
            }
            return id;
        }

        private static CombatWeapon Flight(float damage, float speed = 20f, float life = 3f, int program = -1) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 60f, Damage = damage, Cooldown = 0f, Program = program,
            SpawnFlight = 1, ProjectileSpeed = speed, ProjectileRadius = 0.15f, ProjectileLife = life, Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
        };

        private static CombatWeapon Instant(float damage, int program = -1, CombatCarrier carrier = CombatCarrier.Projectile) => new CombatWeapon
        {
            Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = damage, Cooldown = 0.5f, Program = program,
            Reading = new CombatReading { Carrier = carrier },
        };

        private static void StepN(CombatKernel k, int n, float dt = Dt)
        {
            for (int s = 0; s < n; s++)
            {
                k.Step(dt, k.Time + dt);
            }
        }

        private static double2 Pos(CombatKernel k, int id) => k.TryGetPosition(id, out double2 p) ? p : new double2(double.NaN, double.NaN);

        private static CombatProjectile Proj(CombatKernel k, int i = 0) => k.TryGetProjectile(i, out CombatProjectile p) ? p : default;

        private static float Heading(float2 v) => math.degrees(math.atan2(v.y, v.x));

        private static EmgWreck Wreck(CombatKernel k, int id) => k.TryGetWreck(k.WreckIndexOf(id), out EmgWreck w) ? w : default;

        private static int AddWreck(CombatKernel k, double2 p, float mass = 10f)
        {
            k.TryAddWreck(new EmgWreck { Pos = p, Born = k.Time, Until = k.Time + 90.0, Radius = 0.5f, Hp = 60f, MaxHp = 60f, Mass = mass, Temp = 20f }, out int id);
            return id;
        }

        // ── A. 表 ────────────────────────────────────────────────────────────

        private static void CheckTable()
        {
            Line("  · A. fg.TbFieldCarrierRule 20 行按 FG19B 第 5 节表序编进内核；负向：坏表整表拒绝");
            EmgFieldCatalog.Compiled c = EmgFieldCatalog.Current;
            using CombatKernel k = NewKernel();
            bool same = c.Ok && c.Rules.Count == EmgFieldConst.RuleCount && c.Ids.SequenceEqual(EmgFieldCatalog.Order);
            for (int i = 0; same && i < EmgFieldConst.RuleCount; i++)
            {
                EmgFieldRule a = k.FieldRuleAt(i), b = c.Rules[i];
                same = a.Amount == b.Amount && a.Param == b.Param && a.Faction == b.Faction && a.FactionAlt == b.FactionAlt && a.Impl == b.Impl;
            }
            int kernelRows = c.Rules.Count(r => r.Impl == EmgFieldConst.ImplKernel), coatRows = c.Rules.Count(r => r.Impl == EmgFieldConst.ImplCoat);
            Expect(same && kernelRows == 14 && coatRows == 2, $"表 {c.Rules.Count} 行、行序 = 内核行号；内核结算 {kernelRows} 行、沾染通用路径 {coatRows} 行、承接卡 {c.Rules.Count - kernelRows - coatRows} 行（DEBT-EMG2FLD01-01 / 02）");
            var bad = new List<EmgFieldRule>(c.Rules);
            bad[3] = new EmgFieldRule { Amount = -1f, Faction = 2, Impl = 1 };
            bool shortRej = !k.SetFieldRules(c.Rules.Take(19).ToList());
            bool badRej = !k.SetFieldRules(bad) && k.FieldRuleAt(3).Amount == c.Rules[3].Amount;
            Expect(shortRej && badRej, "负向：19 行表、负幅度行 → 内核整表拒绝，保留原表");
        }

        // ── B. 6 种实体 ──────────────────────────────────────────────────────

        private static void CheckEntities()
        {
            Line("  · B. 场推动 6 种实体（FGR-EMG-112，FGT-EMG-037）：单位 / 残骸 / 无人机 / 弹体 / 破片动、哨戒桩与固定底盘不动；撞障碍发 evt.impact");
            using CombatKernel k = NewKernel();
            int me = Unit(k, CombatFaction.Player, new double2(-20, -20));
            int foe = Unit(k, CombatFaction.Hostile, new double2(3, 0));
            int turret = Unit(k, CombatFaction.Hostile, new double2(-3, 0), kind: CombatUnitKind.Turret);
            int ally = Unit(k, CombatFaction.Player, new double2(0, 3));
            int wreck = AddWreck(k, new double2(0, -3));
            int drone = k.AddDroneForTests(new double2(2.5, 2.5), ally, CombatFaction.Player, false);
            int post = k.AddDroneForTests(new double2(-2.5, 2.5), ally, CombatFaction.Player, true);
            double2 d0 = k.TryGetDrone(drone, out CombatDrone dd0) ? dd0.Pos : default;
            k.SpawnField(Pull, 0, 0, 5f, 0.5f, me, CombatFaction.Player);
            k.SpawnField(Pull, 0, 0, 5f, 0.5f, foe, CombatFaction.Hostile); // 敌方同心牵引场：只拉己方单位与己方无人机
            StepN(k, 20);
            double foeD = math.length(Pos(k, foe)), turD = math.length(Pos(k, turret)), allyD = math.length(Pos(k, ally));
            double wreckD = math.length(Wreck(k, wreck).Pos);
            bool droneMoved = k.TryGetDrone(drone, out CombatDrone dd1) && math.distance(dd1.Pos, d0) > 0.3;
            bool postStill = k.TryGetDrone(post, out CombatDrone pp) && math.distance(pp.Pos, new double2(-2.5, 2.5)) < 1e-6;
            Expect(foeD < 2.0 && math.abs(turD - 3.0) < 1e-6 && allyD < 2.5 && wreckD < 2.8 && droneMoved && postStill,
                $"牵引场 0.33 秒：敌人 3 → {foeD:0.00} 米、炮塔（固定底盘）不动 {turD:0.00}、己方机器被敌方牵引场拉近 {allyD:0.00}、残骸 3 → {wreckD:0.00}、无人机被拉动 {droneMoved}、哨戒桩不动 {postStill}");
            // 弹体：敌方弹体穿过己方推斥场被推偏；己方弹体穿过己方推斥场不受影响（fieldrule_force_payload：弹体 hostile）
            using CombatKernel a = NewKernel();
            int p1 = Unit(a, CombatFaction.Player, new double2(0, 0), a.AddWeapon(Flight(5f)));
            int f1 = Unit(a, CombatFaction.Hostile, new double2(0, 20), a.AddWeapon(Flight(5f)));
            a.SpawnField(Vortex, 0, 10, 3f, 2f, p1, CombatFaction.Player);
            a.FireAt(f1, p1, a.Time);
            a.FireAt(p1, f1, a.Time);
            StepN(a, 32);
            float2 enemyV = float2.zero, ownV = float2.zero;
            for (int i = 0; i < a.ProjectileCount; i++)
            {
                CombatProjectile pr = Proj(a, i);
                if (pr.Faction == CombatFaction.Hostile)
                {
                    enemyV = pr.Vel;
                }
                else
                {
                    ownV = pr.Vel;
                }
            }
            Expect(math.abs(enemyV.x) > 0.5f && math.abs(ownV.x) < 1e-4f && a.EmgScalars.ProjPushed > 0,
                $"弹体：敌方弹体穿过己方涡旋场被卷偏（横向速度 {enemyV.x:0.00}）；己方弹体穿过己方涡旋场不受影响（{ownV.x:0.000}）");
            // 破片（任意阵营）：穿过磁场弯向场心（FGT-EMG-073 阶段 2 部分：fieldrule_magnet_payload）；对照：无场直飞
            float FragBend(bool field)
            {
                using CombatKernel b = NewKernel();
                int owner = Unit(b, CombatFaction.Hostile, new double2(-30, 30));
                if (field)
                {
                    b.SpawnField(Magnet, 4, 3, 5f, 2f, owner, CombatFaction.Player);
                }
                b.SpawnFragmentForTests(new double2(0, 0), new float2(1f, 0f), owner, CombatFaction.Hostile, 5f, 12f, 30f);
                StepN(b, 20);
                return b.ProjectileCount > 0 ? Heading(Proj(b).Vel) : float.NaN;
            }
            float bent = FragBend(true), straight = FragBend(false);
            Expect(bent > 5f && math.abs(straight) < 1e-3f, $"爆炸反应装甲 / 普通破片穿过磁场被弯向场心：航向 {bent:0.0}°（对照无场 {straight:0.0}°）");
            // 撞击：轻敌人被己方推斥场推着撞上障碍 → evt.impact；对照没有障碍不发
            long Impacts(bool obstacle)
            {
                using CombatKernel b = NewKernel();
                int owner = Unit(b, CombatFaction.Player, new double2(-30, -30));
                int light = Unit(b, CombatFaction.Hostile, new double2(1.0, 0));
                if (obstacle)
                {
                    b.SetObstacles(new[] { new float3(2.6f, 0f, 0.5f) });
                }
                b.SpawnField(Push, 0, 0, 3f, 0.8f, owner, CombatFaction.Player);
                StepN(b, 50);
                return b.EmgBusStats.Impacts;
            }
            long imp = Impacts(true), noImp = Impacts(false);
            Expect(imp == 1 && noImp == 0, $"被推斥环推着撞上障碍 → evt.impact {imp} 次（修复轮接触锁存：贴住后不再每步重复结算，应为 1；对照无障碍 {noImp} 次）");
            // 修复轮：牵引场把两个敌人拉到一起——贴住后不再每步互撞（审查实测原来 0.8 秒 31 次）
            using CombatKernel g = NewKernel();
            int gOwner = Unit(g, CombatFaction.Player, new double2(-30, -30));
            int gIn = Unit(g, CombatFaction.Hostile, new double2(1.2, 0));
            int gOut = Unit(g, CombatFaction.Hostile, new double2(3.0, 0));
            g.SpawnField(Pull, 0, 0, 5f, 1f, gOwner, CombatFaction.Player);
            StepN(g, 60);
            long gImp = g.EmgBusStats.Impacts;
            double gGap = math.distance(Pos(g, gOut), Pos(g, gIn));
            Expect(gImp <= 2 && gGap >= 0.99, $"牵引把两个敌人拉到一起：撞击 {gImp} 次（≤ 2），贴住后不穿模（间距 {gGap:0.00} 米）");
        }

        // ── C. 弹体 × 磁场 / 牵引 ─────────────────────────────────────────────

        private static void CheckProjectileFields()
        {
            Line("  · C. 弹体穿场（FGR-EMG-120）：己方磁场把敌我弹体都弯回来、牵引把敌弹吸进己方场、两场叠加、无人机穿场");
            float Bend(CombatFaction shooter, bool field, ushort port = Magnet)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(5f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 24), k.AddWeapon(Flight(5f)));
                if (field)
                {
                    k.SpawnField(port, 3, 12, 4f, 3f, me, CombatFaction.Player);
                }
                if (shooter == CombatFaction.Player)
                {
                    k.FireAt(me, foe, k.Time);
                }
                else
                {
                    k.FireAt(foe, me, k.Time);
                }
                StepN(k, 40);
                return k.ProjectileCount > 0 ? Proj(k).Vel.x : float.NaN;
            }
            float own = Bend(CombatFaction.Player, true), enemy = Bend(CombatFaction.Hostile, true), ctl = Bend(CombatFaction.Player, false);
            Expect(own > 0.5f && enemy > 0.5f && math.abs(ctl) < 1e-4f, $"己方磁场：己方弹体横向 {own:0.00}、敌方弹体横向 {enemy:0.00} 都弯向场心（any）；对照无场 {ctl:0.000}");
            float pullEnemy = Bend(CombatFaction.Hostile, true, Pull), pullOwn = Bend(CombatFaction.Player, true, Pull);
            Expect(pullEnemy > 0.5f && math.abs(pullOwn) < 1e-4f, $"己方牵引场把敌弹吸向场心（横向 {pullEnemy:0.00}），己方弹体不受自己牵引（{pullOwn:0.000}）");
            // 两个场同时作用一个单位：位移相加
            double Moved(int fields)
            {
                using CombatKernel k = NewKernel();
                int owner = Unit(k, CombatFaction.Player, new double2(-30, -30));
                int foe = Unit(k, CombatFaction.Hostile, new double2(4, 0));
                for (int i = 0; i < fields; i++)
                {
                    k.SpawnField(Pull, 0, 0, 6f, 1f, owner + i * 0, CombatFaction.Player);
                    k.SpawnField(Vortex, 0, 0, 6f, 1f, owner, CombatFaction.Player);
                }
                StepN(k, 6);
                return math.distance(Pos(k, foe), new double2(4, 0));
            }
            double one = Moved(1), none = Moved(0);
            using CombatKernel s = NewKernel();
            int o2 = Unit(s, CombatFaction.Player, new double2(-30, -30));
            int f2 = Unit(s, CombatFaction.Hostile, new double2(4, 0));
            s.SpawnField(Pull, 0, 0, 6f, 1f, o2, CombatFaction.Player);
            StepN(s, 6);
            double pullOnly = math.distance(Pos(s, f2), new double2(4, 0));
            Expect(one > pullOnly + 0.05 && none < 1e-9, $"牵引 + 涡旋同时作用：0.1 秒位移 {one:0.000} 米 > 只有牵引 {pullOnly:0.000} 米（速度相加）；没有场 {none:0}");
        }

        // ── D. 电晕 / 偏折 ───────────────────────────────────────────────────

        private static void CheckCoronaDeflect()
        {
            Line("  · D. 电晕场：穿过己方电晕场的弹体带电、命中注电（每发一次）；场内敌人持续充电。偏折场：敌方弹体被反射并改为己方");
            (float charge, int charged, int count) RunCorona(bool field, bool enemyShot)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 14), k.AddWeapon(Flight(1f)));
                if (field)
                {
                    k.SpawnField(Corona, 0, 4, 2f, 3f, me, CombatFaction.Player);
                    k.SpawnField(Corona, 0, 7, 2f, 3f, me + 100000, CombatFaction.Player); // 第二个电晕场：同一发弹体只记一次
                }
                k.FireAt(enemyShot ? foe : me, enemyShot ? me : foe, k.Time);
                StepN(k, 60);
                return (k.AccumOf(enemyShot ? me : foe, EmgPhaseConst.Charge), k.EmgScalars.ProjCharged, 0);
            }
            (float c1, int n1, _) = RunCorona(true, false);
            (float c0, _, _) = RunCorona(false, false);
            (float ce, int ne, _) = RunCorona(true, true);
            Expect(c1 > 0.05f && c1 <= 0.25f && n1 == 1 && c0 < 1e-6f && ne == 0 && ce < 1e-6f,
                $"己方弹体穿过两个己方电晕场：带电记 {n1} 次、命中敌人电荷 {c1:0.000}（注入 0.25 × (1 − 电抗)，之后按 0.4 / 秒衰减）；对照无场 {c0:0}；敌方弹体穿过己方电晕场不带电（记 {ne}、电荷 {ce:0}）");
            using CombatKernel u = NewKernel();
            int owner = Unit(u, CombatFaction.Player, new double2(-30, -30));
            int inside = Unit(u, CombatFaction.Hostile, new double2(1, 0));
            int ownIn = Unit(u, CombatFaction.Player, new double2(-1, 0));
            u.SpawnField(Corona, 0, 0, 3f, 2f, owner, CombatFaction.Player);
            StepN(u, 60);
            float ci = u.AccumOf(inside, EmgPhaseConst.Charge), co = u.AccumOf(ownIn, EmgPhaseConst.Charge);
            Expect(ci > 0.003f && co < 1e-6f, $"电晕场内 1 秒：敌人电荷 {ci:0.000} > 0（0.3 / 秒 × (1 − 电抗)，边充边按 0.4 / 秒衰减；hostile）、己方单位 {co:0}");
            // 偏折：己方偏折场反射敌方弹体（hostile），己方弹体穿过不受影响
            using CombatKernel d = NewKernel();
            int me = Unit(d, CombatFaction.Player, new double2(0, 0), d.AddWeapon(Flight(10f)));
            int foe = Unit(d, CombatFaction.Hostile, new double2(0, 20), d.AddWeapon(Flight(10f)));
            d.SpawnField(Deflect, 0, 10, 2.5f, 3f, me, CombatFaction.Player);
            d.FireAt(foe, me, d.Time);
            d.FireAt(me, foe, d.Time);
            StepN(d, 30);
            int playerUp = 0, reflected = 0;
            for (int i = 0; i < d.ProjectileCount; i++)
            {
                CombatProjectile pr = Proj(d, i);
                playerUp += pr.Faction == CombatFaction.Player && pr.Vel.y > 0f ? 1 : 0;
                reflected += (pr.FieldOnce & EmgFieldConst.OnceDeflect) != 0 ? 1 : 0;
            }
            Expect(d.EmgScalars.ProjReflected == 1 && reflected == 1 && playerUp == 2,
                $"己方偏折场：敌方弹体被反射 {d.EmgScalars.ProjReflected} 次并改为己方、掉头飞向敌人（向上飞的己方弹体 {playerUp} 发 = 原己方 1 + 反射 1）；发 evt.bounce");
        }

        // ── E. 斑块 × 弹体 ───────────────────────────────────────────────────

        private static void CheckPatchRules()
        {
            Line("  · E. 弹体穿斑块（FG19B 第 5 节）：烟失锁 0.5 秒、蒸汽 0.3 秒（DEBT-EMG2REACT01-01）；液氮去火；带油穿明火拖尾；破片撞玻化层 / 冰面反弹、直射弹体不反弹");
            (ushort lockAt, float turnDuring, float turnAfter) Homing(byte matter)
            {
                EmgProgramResult prog = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_homing" }, FirmwareCarrier.Projectile);
                using CombatKernel k = NewKernel();
                int pi = CombatSite.RegisterProgram(k, prog);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 10f, 4f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 30));
                if (matter != 0)
                {
                    k.SpawnPatch(matter, new double2(0, 1.5), 1.2f, 1f, 20f);
                }
                k.FireAt(me, foe, k.Time);
                ushort lk = 0;
                for (int i = 0; i < 18; i++) // 0.3 秒：弹体已穿过斑块；记失锁剩余的最大值
                {
                    k.Step(Dt, k.Time + Dt);
                    lk = (ushort)math.max((int)lk, (int)Proj(k).LockQ);
                }
                if (matter == EmgMatterConst.Steam)
                {
                    StepN(k, 0);
                }
                k.SetPosition(foe, new double2(25, 25));
                float h0 = Heading(Proj(k).Vel);
                StepN(k, 6);
                float h1 = Heading(Proj(k).Vel);
                StepN(k, 36);
                float h2 = Heading(Proj(k).Vel);
                return (lk, math.abs(h1 - h0), math.abs(h2 - h1));
            }
            var smoke = Homing(EmgMatterConst.Smoke);
            var steam = Homing(EmgMatterConst.Steam);
            var none = Homing(0);
            Expect(smoke.lockAt > 400 && smoke.lockAt <= 500 && smoke.turnDuring < 0.01f && smoke.turnAfter > 1f
                   && steam.lockAt > 0 && steam.lockAt <= 300 && none.lockAt == 0 && none.turnDuring > 0.5f,
                $"寻的弹穿过烟：失锁最大剩余 {smoke.lockAt} 毫秒（0.5 秒）、目标换位后 0.1 秒内转向 {smoke.turnDuring:0.00}°、失锁结束后转向 {smoke.turnAfter:0.0}°；蒸汽剩余 {steam.lockAt} 毫秒（≤ 300）；对照无斑块立即转向 {none.turnDuring:0.0}°");
            // 液氮去火 / 带油穿明火拖尾
            (uint coat, int trail, int patches) Run(params (byte m, double x)[] patches)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 15f, 4f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(40, 0));
                foreach ((byte m, double x) in patches)
                {
                    k.SpawnPatch(m, new double2(x, 0), 1f, 1f, 30f);
                }
                int before = k.PatchCount;
                k.FireAt(me, foe, k.Time);
                StepN(k, 90);
                return (Proj(k).Coat, k.EmgScalars.TrailPatches, k.PatchCount - before);
            }
            uint fireBit = NamedReactionCatalog.BitOf("Fire");
            var fireOnly = Run((EmgMatterConst.Fire, 3));
            var fireCryo = Run((EmgMatterConst.Fire, 3), (EmgMatterConst.Cryo, 7));
            var oilFire = Run((EmgMatterConst.Oil, 3), (EmgMatterConst.Fire, 6));
            Expect((fireOnly.coat & fireBit) != 0 && (fireCryo.coat & fireBit) == 0 && oilFire.trail >= 3 && oilFire.trail <= 6 && fireOnly.trail == 0,
                $"穿过明火带火；再穿液氮去掉带火；带油的弹体穿过明火沿途拖出明火 {oilFire.trail} 块（≤ 6）；只带火不拖尾（{fireOnly.trail}）");
            // 修复轮（FG19B fieldrule_cryo_payload“命中 phs.chill +0.3”）：穿过液氮雾的弹体命中后目标寒量 > 0；对照无雾 = 0
            float ChillOnHit(bool cryo)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 15f, 4f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(12, 0));
                if (cryo)
                {
                    k.SpawnPatch(EmgMatterConst.Cryo, new double2(5, 0), 1f, 1f, 30f);
                }
                k.FireAt(me, foe, k.Time);
                float best = 0f;
                for (int i = 0; i < 70; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                    best = math.max(best, k.AccumOf(foe, EmgPhaseConst.Chill));
                }
                return best;
            }
            float chillCryo = ChillOnHit(true), chillNone = ChillOnHit(false);
            Expect(chillCryo > 0.05f && chillNone < 1e-6f, $"穿过液氮雾带寒：命中后目标寒量 {chillCryo:0.00}（规则 +0.3 × (1 − 寒抗)）；对照无雾 {chillNone:0.000}");
            // 玻化层 / 冰面
            float Reflect(byte matter, bool fragment)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 12f, 3f)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(30, 0));
                k.SpawnPatch(matter, new double2(5, 0), 1.5f, 1f, 30f);
                if (fragment)
                {
                    k.SpawnFragmentForTests(new double2(0, 0), new float2(1f, 0f), me, CombatFaction.Player, 5f, 12f, 30f);
                }
                else
                {
                    k.FireAt(me, foe, k.Time);
                }
                StepN(k, 30);
                return k.ProjectileCount > 0 ? Proj(k).Vel.x : float.NaN;
            }
            float glassFrag = Reflect(EmgMatterConst.Glass, true), iceFrag = Reflect(EmgMatterConst.Ice, true), glassShot = Reflect(EmgMatterConst.Glass, false);
            Expect(glassFrag < -1f && iceFrag < -1f && glassShot > 1f, $"破片撞玻化层反弹（vx {glassFrag:0.0}）、撞冰面反弹（{iceFrag:0.0}）；直射弹体飞越玻化层不反射（{glassShot:0.0}，FG19B 第 5 节取舍 1）");
        }

        // ── F. 磁扫 F02 ──────────────────────────────────────────────────────

        private static void CheckMagSweep()
        {
            Line("  · F. 磁扫（F02，DEBT-EMG2RULE01-01 的 F 行）：磁场展开时把重叠的金属碎屑吸起成破片飞向场心；对照：水不被吸");
            (int frags, int patches) Sweep(byte matter)
            {
                using CombatKernel k = NewKernel();
                int me = Unit(k, CombatFaction.Player, new double2(-30, -30));
                k.SpawnPatch(matter, new double2(3, 0), 1f, 8f, 30f);
                int p0 = k.PatchCount;
                k.SpawnField(Magnet, 0, 0, 5f, 1f, me, CombatFaction.Player);
                StepN(k, 2);
                int frags = 0;
                int fragCount = 0;
                for (int i = 0; i < k.ProjectileCount; i++)
                {
                    frags += (Proj(k, i).Flags & EmgProjFlags.Fragment) != 0 ? 1 : 0;
                }
                fragCount = frags;
                StepN(k, 20); // 吸空的碎屑在下一个斑块节拍移除
                return (fragCount, p0 - k.PatchCount);
            }
            var debris = Sweep(EmgMatterConst.Debris);
            var water = Sweep(EmgMatterConst.Water);
            Expect(debris.frags == 4 && debris.patches == 1 && water.frags == 0 && water.patches == 0,
                $"8 份金属碎屑被吸起成 {debris.frags} 片破片（每 2 份 1 片）、碎屑斑块吸空消失 {debris.patches}；对照水斑块：破片 {water.frags}、消失 {water.patches}");
        }

        // ── G. 容量与边界 ─────────────────────────────────────────────────────

        private static void CheckCapacityAndEdges()
        {
            Line("  · G. 同时活跃的场 ≤ 64（超出不生效、计数可见）；场到期时正在被推动的实体；暂停不推进");
            using CombatKernel k = NewKernel();
            int owner = Unit(k, CombatFaction.Player, new double2(-50, -50));
            for (int i = 0; i < 64; i++)
            {
                k.SpawnField(Pull, 100 + i * 20, 100, 1f, 5f, owner + i + 1, CombatFaction.Player);
            }
            int foe = Unit(k, CombatFaction.Hostile, new double2(3, 0));
            bool ok65 = k.SpawnField(Pull, 0, 0, 5f, 5f, owner, CombatFaction.Player);
            StepN(k, 10);
            Expect(!ok65 && k.FieldCount == 64 && k.EmgScalars.FieldsCapped == 1 && math.distance(Pos(k, foe), new double2(3, 0)) < 1e-9,
                $"第 65 个场不生效：活跃 {k.FieldCount}、满额计数 {k.EmgScalars.FieldsCapped}、它位置上的敌人不被拉动");
            // 修复轮：满额在调试层可见——CombatSite.EmgFieldDebugLine（场满额每次增加时 WatchFields 记一行 Log.Info，同 WatchWrecks）
            using (var site = new CombatSite("emg-field-cap", CombatSite.ConfigFromTuning()))
            {
                for (int i = 0; i < 65; i++)
                {
                    site.Kernel.SpawnField(Pull, 100 + i * 20, 100, 1f, 5f, 1000 + i, CombatFaction.Player);
                }
                string dbg = site.EmgFieldDebugLine();
                Expect(dbg.Contains("活跃场 64/64") && dbg.Contains("满额未生效 1"), $"调试层一行可见：{dbg}");
            }
            // 修复轮（审查 P2）：emg.field.capacity 是固定值（内核常量 + 读档上限同一个 EmgMatterConst.FieldCapacity），调参表与内核必须一致
            (bool capOk, float capV) = EmgCatalog.FromTables().Tuning("emg.field.capacity");
            Expect(capOk && (int)capV == EmgMatterConst.FieldCapacity, $"调参键 emg.field.capacity = {capV}（固定值）与内核容量 {EmgMatterConst.FieldCapacity} 一致");
            // 到期：残骸在场里被拉动，场到期后照常减速停住；单位离场即停
            using CombatKernel e = NewKernel();
            int o = Unit(e, CombatFaction.Player, new double2(-30, -30));
            int w = AddWreck(e, new double2(4, 0), 2f);
            int u = Unit(e, CombatFaction.Hostile, new double2(0, 4));
            e.SpawnField(Pull, 0, 0, 6f, 0.25f, o, CombatFaction.Player);
            StepN(e, 15);
            float vEnd = math.length(Wreck(e, w).Vel);
            double2 uEnd = Pos(e, u);
            StepN(e, 60);
            Expect(e.FieldCount == 0 && e.EmgScalars.FieldsExpired == 1 && math.length(Wreck(e, w).Vel) < 0.05f && math.distance(Pos(e, u), uEnd) < 1e-9 && vEnd > 0f,
                $"场到期（0.25 秒）时残骸正以 {vEnd:0.00} 米 / 秒被拉动 → 1 秒后停住；单位离场即停、不再位移");
            // 暂停：dt = 0 不推动、场不到期
            using CombatKernel p = NewKernel();
            int po = Unit(p, CombatFaction.Player, new double2(-30, -30));
            int pf = Unit(p, CombatFaction.Hostile, new double2(3, 0));
            p.SpawnField(Pull, 0, 0, 5f, 0.5f, po, CombatFaction.Player);
            for (int i = 0; i < 120; i++)
            {
                p.Step(0f, p.Time);
            }
            Expect(p.FieldCount == 1 && math.distance(Pos(p, pf), new double2(3, 0)) < 1e-9, "暂停（dt = 0）120 步：场不到期、敌人不被拉动");
        }

        // ── H. 存读档 / 倍速 ─────────────────────────────────────────────────

        private static void CheckSaveLoadSpeed()
        {
            Line("  · H. 存读档（活跃场 / 弹体失锁与只记一次位 / 武器部件场）与 0.5x～3x 一致");
            EmgProgramResult prog = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_homing" }, FirmwareCarrier.Projectile);
            using CombatKernel a = NewKernel();
            using CombatKernel b = NewKernel();
            int pi = CombatSite.RegisterProgram(a, prog);
            CombatWeapon fw = Flight(1f, 10f, 4f, pi);
            int me = Unit(a, CombatFaction.Player, new double2(0, 0), a.AddWeapon(fw));
            CombatWeapon aura = Instant(3f, -1, CombatCarrier.Aura);
            aura.FieldPort = Corona;
            aura.FieldRadius = 4f;
            int auraW = a.AddWeapon(aura);
            int foe = Unit(a, CombatFaction.Hostile, new double2(0, 30));
            a.SpawnPatch(EmgMatterConst.Smoke, new double2(0, 1.5), 1.2f, 1f, 20f);
            a.SpawnField(Vortex, 2, 10, 4f, 3f, me, CombatFaction.Player);
            a.FireAt(me, foe, a.Time);
            StepN(a, 18);
            CombatProjectile before = Proj(a);
            CombatLoadResult lr = b.Load(a.Serialize());
            CombatProjectile after = Proj(b);
            bool fieldSame = b.FieldCount == a.FieldCount && b.FieldAt(0).Id == a.FieldAt(0).Id && b.FieldAt(0).Until == a.FieldAt(0).Until && b.FieldAt(0).Port == Vortex;
            StepN(a, 120);
            StepN(b, 120);
            Expect(lr == CombatLoadResult.Ok && fieldSame && after.LockQ == before.LockQ && after.LockQ > 0 && after.FieldOnce == before.FieldOnce && a.StateHash() == b.StateHash(),
                $"失锁途中存档 → 读档：活跃场一致、弹体失锁 {after.LockQ} 毫秒与只记一次位一致；各续跑 120 步状态哈希一致（{lr}）");
            string wa = CombatKernel.WeaponKey(aura), wn = CombatKernel.WeaponKey(Instant(3f, -1, CombatCarrier.Aura));
            Expect(wa != wn && auraW >= 0, "带部件场的武器与不带的武器按完整字节分行（不会被去重并成一行）");
            // 倍速：同一个牵引场 0.6 秒的位移与步长无关
            var moved = new List<double>();
            foreach (float dt in new[] { 1f / 120f, 1f / 60f, 1f / 20f })
            {
                using CombatKernel k = NewKernel();
                int o = Unit(k, CombatFaction.Player, new double2(-30, -30));
                int f = Unit(k, CombatFaction.Hostile, new double2(4, 0));
                k.SpawnField(Pull, 0, 0, 6f, 0.5f, o, CombatFaction.Player);
                StepN(k, (int)math.round(0.6f / dt), dt);
                moved.Add(math.distance(Pos(k, f), new double2(4, 0)));
            }
            Expect(moved.Max() - moved.Min() < 0.2 && moved.Min() > 0.5, $"0.5x / 1x / 3x（步长 1/120、1/60、1/20）：牵引位移 {string.Join(" / ", moved.Select(m => m.ToString("0.00")))} 米，差 < 0.2 米");
        }

        // ── I. 声明轨 ────────────────────────────────────────────────────────

        private static void CheckDeclTrack()
        {
            Line("  · I. 正式路径：fld.* 接通后 9 个 owner 切到声明轨；牵引 / 磁聚命中处展开真场；接入冲击只在接入时放；部件场（电晕场）；emg.stage = 0 回旧轨");
            string[] owners = { "fw_tractor", "fw_magnet", "fw_vortex", "fw_deflect", "fw_shockwave", "fw_uplinkpulse", "comp_shovel", "comp_corona", "comp_pulser" };
            var off = owners.Where(o => !EmgCatalog.IsOwnerOnDeclTrack(o)).ToList();
            Expect(off.Count == 0 && !EmgCatalog.IsPendingPort("fld.magnet", EmgRole.Spawn) && EmgCatalog.IsPendingPort("evt.react", EmgRole.Trigger),
                $"fld.* 不再是未接通端口（evt.react 仍是，按端口判；ent.wreck 已由 EMG2-RECL-01 接通）；写 fld.* 的 9 个 owner 全部走声明轨{(off.Count > 0 ? "；未切：" + string.Join("、", off) : string.Empty)}");
            (int fields, ushort port, float mag, double pulled) Fire(string fw)
            {
                EmgProgramResult prog = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, FirmwareCarrier.Projectile);
                using CombatKernel k = NewKernel();
                int pi = prog.IsEmpty ? -1 : CombatSite.RegisterProgram(k, prog);
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(5f, pi)));
                int foe = Unit(k, CombatFaction.Hostile, new double2(0, 6));
                int near = Unit(k, CombatFaction.Hostile, new double2(2.5, 6));
                k.FireAt(me, foe, k.Time);
                k.Step(Dt, k.Time + Dt);
                int n = k.FieldCount;
                ushort port = n > 0 ? k.FieldAt(0).Port : (ushort)0;
                StepN(k, 20);
                return (n, port, k.AccumOf(foe, EmgPhaseConst.Magnetize), math.distance(Pos(k, near), new double2(2.5, 6)));
            }
            var tractor = Fire("fw_tractor");
            var magnet = Fire("fw_magnet");
            Expect(tractor.fields == 1 && tractor.port == Pull && tractor.pulled > 0.5 && magnet.fields == 1 && magnet.port == Magnet && magnet.mag > 0.25f,
                $"牵引（声明 fld.pull 4 米 0.5 秒）命中处展开牵引场 {tractor.fields} 个、旁边敌人被拉 {tractor.pulled:0.00} 米（FG-GAP-180：≥ 0.5 米口径）；磁聚展开磁场（端口 {magnet.port}）、目标磁化度 {magnet.mag:0.00}");
            try
            {
                EmgSource src0 = EmgCatalog.FromTables();
                Func<string, (bool ok, float value)> t0 = src0.Tuning;
                src0.Tuning = id => id == EmgCatalog.StageKey ? (true, 0f) : t0(id);
                EmgCatalog.OverrideForTests(src0);
                var legacy = Fire("fw_tractor");
                Expect(legacy.fields == 0, $"对照 emg.stage = 0：牵引回旧读法，不生成场（{legacy.fields}）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            // 部件场：电晕场主部件 → 武器 FieldPort = fld.corona；推铲的推斥由击退读法执行（不另开场）
            CombatWeapon cw = CombatSite.WithPartField("comp_corona", Instant(3f, -1, CombatCarrier.Aura));
            CombatWeapon sw = Instant(3f, -1, CombatCarrier.Melee);
            sw.Reading.Knockback = 1f;
            sw = CombatSite.WithPartField("comp_shovel", sw);
            CombatWeapon gw = CombatSite.WithPartField("comp_gun", Instant(3f));
            using CombatKernel c = NewKernel();
            int me2 = Unit(c, CombatFaction.Player, new double2(0, 0), c.AddWeapon(cw));
            int f2 = Unit(c, CombatFaction.Hostile, new double2(0, 2));
            c.FireAt(me2, f2, c.Time);
            int born1 = c.EmgScalars.FieldsBorn;
            c.FireAt(me2, f2, c.Time + 0.6);
            Expect(cw.FieldPort == Corona && cw.FieldRadius == 4f && sw.FieldPort == 0 && gw.FieldPort == 0 && c.FieldCount == 1 && born1 == 1 && c.FieldAt(0).Pos.Equals(new double2(0, 0)),
                $"电晕场部件 → 武器部件场 fld.corona 4 米，出手在本机处展开、再次出手续期不叠（场 {c.FieldCount} 个）；推铲（击退读法已在推）与连射器不开场");
            // 修复轮：部件场在 WithProgram 出口统一写——炮塔（TurretService）与试验场（TestRangeService）都经 WithProgram，电晕主组件同样得到 fld.corona；弹体武器出手（投送分支之前）同样展开
            using (var site = new CombatSite("emg-field-turret", CombatSite.ConfigFromTuning()))
            {
                CombatWeapon tw = site.WithProgram(new BlueprintCircuitPreview { PrimaryId = "comp_corona", FirmwareIds = Array.Empty<string>() }, Instant(3f));
                CombatWeapon tw2 = site.WithProgram(new BlueprintCircuitPreview { PrimaryId = "comp_corona", FirmwareIds = new[] { "fw_tractor" } }, Instant(3f));
                CombatWeapon pw = tw;
                pw.Mode = CombatWeaponMode.Projectile;
                pw.ProjectileSpeed = 20f;
                pw.ProjectileRadius = 0.15f;
                pw.ProjectileLife = 2f;
                using CombatKernel tk = NewKernel();
                int tme = Unit(tk, CombatFaction.Player, new double2(0, 0), tk.AddWeapon(pw));
                int tf = Unit(tk, CombatFaction.Hostile, new double2(0, 5));
                tk.FireAt(tme, tf, tk.Time);
                Expect(tw.FieldPort == Corona && tw2.FieldPort == Corona && tk.FieldCount == 1 && tk.FieldAt(0).Port == Corona,
                       $"炮塔 / 试验场同一路径：电晕主组件经 WithProgram（空程序 {tw.FieldPort}、带固件 {tw2.FieldPort}）都写上部件场；弹体武器出手展开电晕场 {tk.FieldCount} 个");
            }

            // 修复轮（FgFirmwareMigrationSelfCheck I 段）：偏折 = 被击 × 宿主载体，5 种载体各按 FG19A 第 1.4 节偏折载体表展开（读法 5 句与之一一对应）
            var dfl = new List<string>();
            foreach (CombatCarrier cc in new[] { CombatCarrier.Projectile, CombatCarrier.Melee, CombatCarrier.Summon, CombatCarrier.Aura, CombatCarrier.Field })
            {
                EmgProgramResult dp = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_deflect" }, (FirmwareCarrier)(byte)cc);
                using CombatKernel k = NewKernel();
                CombatWeapon w = Instant(1f, dp.IsEmpty ? -1 : CombatSite.RegisterProgram(k, dp), cc);
                w.Reading.Area = 6f;
                w.Reading.FieldSeconds = 3f;
                int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(w));
                double2 foeAt = cc == CombatCarrier.Melee ? new double2(0, 1.5) : new double2(0, 2);
                int foe = Unit(k, CombatFaction.Hostile, foeAt, k.AddWeapon(Instant(5f)));
                if (cc == CombatCarrier.Summon)
                {
                    k.AddDroneForTests(new double2(3, 1), me, CombatFaction.Player, true);
                    k.AddDroneForTests(new double2(-3, -1), me, CombatFaction.Player, true);
                }
                k.FireAt(foe, me, k.Time);
                if (k.FieldCount == 0)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                EmgField f = k.FieldAt(0);
                double2 want = cc switch
                {
                    CombatCarrier.Projectile => new double2(0, 1.25),
                    CombatCarrier.Summon => new double2(3, 1),
                    CombatCarrier.Field => new double2(0, 1),
                    _ => new double2(0, 0),
                };
                float wantR = cc == CombatCarrier.Aura ? 6f : 2.5f;
                double wantLife = cc == CombatCarrier.Field ? 3.0 : 1.5;
                bool ok = k.FieldCount == 1 && f.Port == Deflect && math.distance(f.Pos, want) < 0.05 && math.abs(f.Radius - wantR) < 1e-3f
                          && math.abs(f.Until - f.Born - wantLife) < 1e-3 && (cc != CombatCarrier.Melee || math.distance(Pos(k, foe), new double2(0, 0)) > 2.3);
                if (!ok)
                {
                    dfl.Add($"{cc}：场 {k.FieldCount} 个、端口 {f.Port}、位置 ({f.Pos.x:0.00},{f.Pos.y:0.00})、半径 {f.Radius:0.0}、存续 {f.Until - f.Born:0.0}、攻击者距本机 {math.distance(Pos(k, foe), new double2(0, 0)):0.00}");
                }
            }
            Expect(dfl.Count == 0,
                "偏折被击时按载体展开：射弹场心偏向攻击者 1.25 米、格斗把近身攻击者弹到 2.5 米外、无人机在离攻击者最近的本机无人机处、力场半径 = 力场 6 米、布区落在本机与攻击者正中且存续 = 布区 3 秒"
                + (dfl.Count > 0 ? "；不符：" + string.Join("；", dfl) : string.Empty));
            CheckDeflectCooldown();
            CheckHitFieldCarriers();
            CheckGasFields();
        }

        /// <summary>修复轮：偏折内置冷却 3 秒（fieldrule_deflect_payload.param；FG19A 第 43 行）——3 秒内连续挨打 10 次只展开 1 个场；冷却跨存读档保留；冷却过后再挨打才展开下一个。</summary>
        private static void CheckDeflectCooldown()
        {
            EmgProgramResult dp = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_deflect" }, FirmwareCarrier.Projectile);
            using CombatKernel k = NewKernel();
            using CombatKernel kb = NewKernel();
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Instant(1f, dp.IsEmpty ? -1 : CombatSite.RegisterProgram(k, dp))), 4000f);
            CombatWeapon fast = Instant(5f);
            fast.Cooldown = 0f;
            int foe = Unit(k, CombatFaction.Hostile, new double2(0, 2), k.AddWeapon(fast));
            for (int i = 0; i < 10; i++)
            {
                k.FireAt(foe, me, k.Time);
                StepN(k, 15);
            }
            int born10 = k.EmgScalars.FieldsBorn;
            CombatLoadResult lr = kb.Load(k.Serialize());
            int cd = kb.TryGetUnitEmg(me, out _, out EmgUnitState st) ? st.FieldCdQ : 0;
            kb.FireAt(foe, me, kb.Time);
            StepN(kb, 2);
            int inCd = kb.FieldCount;
            StepN(kb, 42);
            kb.FireAt(foe, me, kb.Time);
            StepN(kb, 2);
            int afterCd = kb.FieldCount;
            Expect(born10 == 1 && lr == CombatLoadResult.Ok && cd != 0 && inCd == 0 && afterCd == 1,
                $"偏折 2.5 秒内挨打 10 次只展开 {born10} 个场（内置冷却 3 秒）；存读档后冷却刻度仍在（{cd}，{lr}）→ 冷却中再挨打场 {inCd} 个；冷却过后再挨打展开 {afterCd} 个");
        }

        /// <summary>修复轮（FG19A 第 1.4 节电磁场控 5 载体）：命中写端的场按宿主载体展开——力场以本机为心、半径 = 力场半径，一次脉冲打中多个目标也只有 1 个场；格斗在身前（至多半个半径处）。</summary>
        private static void CheckHitFieldCarriers()
        {
            EmgProgramResult tp = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_tractor" }, (FirmwareCarrier)(byte)CombatCarrier.Aura);
            using CombatKernel k = NewKernel();
            CombatWeapon aw = Instant(1f, tp.IsEmpty ? -1 : CombatSite.RegisterProgram(k, tp), CombatCarrier.Aura);
            aw.Reading.Area = 7f;
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(aw));
            int f1 = Unit(k, CombatFaction.Hostile, new double2(0, 3));
            int f2 = Unit(k, CombatFaction.Hostile, new double2(4, 0));
            Unit(k, CombatFaction.Hostile, new double2(-3, -3));
            k.FireAt(me, f1, k.Time);
            if (k.FieldCount == 0)
            {
                k.Step(Dt, k.Time + Dt);
            }
            int n1 = k.FieldCount;
            EmgField af = n1 > 0 ? k.FieldAt(0) : default;
            k.FireAt(me, f1, k.Time + 0.6);
            k.Step(Dt, k.Time + Dt);
            int n2 = k.FieldCount;
            StepN(k, 15);
            double f2d = math.length(Pos(k, f2));
            Expect(n1 == 1 && n2 == 1 && af.Port == Pull && math.length(af.Pos) < 1e-6 && math.abs(af.Radius - 7f) < 1e-3f && f2d < 3.9,
                $"力场 + 牵引：以本机为心展开 1 个牵引场（{n1} 个，半径 {af.Radius:0.0} = 力场 7 米），再次脉冲续期仍 {n2} 个（不按命中目标数叠场）；旁边敌人被拉近到 {f2d:0.00} 米");
            EmgProgramResult mp = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_vortex" }, (FirmwareCarrier)(byte)CombatCarrier.Melee);
            using CombatKernel m = NewKernel();
            int mme = Unit(m, CombatFaction.Player, new double2(0, 0), m.AddWeapon(Instant(1f, mp.IsEmpty ? -1 : CombatSite.RegisterProgram(m, mp), CombatCarrier.Melee)));
            int mfoe = Unit(m, CombatFaction.Hostile, new double2(0, 2.5));
            m.FireAt(mme, mfoe, m.Time);
            if (m.FieldCount == 0)
            {
                m.Step(Dt, m.Time + Dt);
            }
            EmgField mf = m.FieldCount > 0 ? m.FieldAt(0) : default;
            double want = math.min(2.5, mf.Radius * 0.5);
            Expect(m.FieldCount == 1 && mf.Port == Vortex && math.abs(mf.Pos.y - want) < 0.05 && math.abs(mf.Pos.x) < 1e-6,
                $"格斗 + 涡流：涡旋场展开在身前 ({mf.Pos.x:0.00},{mf.Pos.y:0.00})（本机朝目标 {want:0.00} 米处，不是命中点 2.5 米）");
        }

        /// <summary>修复轮（FGR-EMG-112；FG19B 第 9 节第 4 条）：推斥 / 牵引 / 涡旋推动气体层斑块，涡旋把变稀的粉尘重新聚浓到尘爆阈值 0.6；磁场不推气体；对照无场只会继续变稀。</summary>
        private static void CheckGasFields()
        {
            (double moved, float conc, float conc0) Gas(ushort port)
            {
                using CombatKernel k = NewKernel();
                int owner = Unit(k, CombatFaction.Player, new double2(-30, -30));
                k.SpawnPatch(EmgMatterConst.Dust, new double2(2, 0), 1f, 2f, 30f);
                StepN(k, 120);
                k.TryGetPatch(0, out CombatZone z0);
                if (port != 0)
                {
                    k.SpawnField(port, 0, 0, 5f, 1f, owner, CombatFaction.Player);
                }
                StepN(k, 45);
                k.TryGetPatch(0, out CombatZone z1);
                return (math.distance(z1.Pos, z0.Pos), z1.Conc, z0.Conc);
            }
            var vortex = Gas(Vortex);
            var pull = Gas(Pull);
            var magnet = Gas(Magnet);
            var none = Gas(0);
            Expect(vortex.conc0 < 0.6f && vortex.conc >= 0.6f && vortex.moved > 0.2 && pull.moved > 0.2 && magnet.moved < 1e-9 && none.moved < 1e-9 && none.conc <= none.conc0,
                $"粉尘散开到浓度 {vortex.conc0:0.00} 后：涡旋场卷动 {vortex.moved:0.00} 米并聚浓到 {vortex.conc:0.00}（≥ 0.6 尘爆阈值）；牵引推动 {pull.moved:0.00} 米；磁场不推气体（{magnet.moved:0.000}）；对照无场不动、浓度 {none.conc0:0.00} → {none.conc:0.00}");
        }

        // ── J. 文本 ──────────────────────────────────────────────────────────

        private static void CheckTexts()
        {
            Line("  · J. 磁聚描述改回 FG19A 第 1.4 节第 38 行（FG-GAP-156 机制部分），读法不再写附带「充能」「磁化」");
            string desc = GameText.Get("firmware.fw_magnet.desc");
            string read = GameText.Get("firmware.fw_magnet.read.projectile");
            Expect(desc.Contains("拉向一点汇聚") && desc.Contains("磁化度") && !desc.Contains("吸近一小段") && read.Contains("磁场") && !read.Contains("充能"),
                $"磁聚描述：{desc}｜射弹读法：{read}");
            var tags = EmgProgramCompiler.LiveTagsOf("fw_magnet");
            Expect(!tags.Contains("Charged") && !tags.Contains("Magnet"), $"磁聚声明轨退役旧标签 Charged / Magnet（剩 {string.Join(";", tags)}）");
        }

        // ── K. 性能 ──────────────────────────────────────────────────────────

        private static void CheckPerf()
        {
            Line("  · K. 性能（只测一次）：64 个场 + 300 个单位 + 40 个残骸 + 600 发弹体，每步耗时");
            using CombatKernel k = NewKernel(512);
            int me = Unit(k, CombatFaction.Player, new double2(0, 0), k.AddWeapon(Flight(1f, 20f, 3f)));
            var foes = new List<int>();
            for (int i = 0; i < 300; i++)
            {
                foes.Add(Unit(k, CombatFaction.Hostile, new double2((i % 20) * 3 - 30, (i / 20) * 3 + 5)));
            }
            for (int i = 0; i < 40; i++)
            {
                AddWreck(k, new double2(i - 20, -5));
            }
            for (int i = 0; i < 64; i++)
            {
                k.SpawnField((ushort)(Pull + i % 6), (i % 8) * 8 - 30, (i / 8) * 6, 4f, 30f, me + i + 1, CombatFaction.Player);
            }
            for (int s = 0; s < 600; s++)
            {
                k.FireAt(me, foes[s % foes.Count], k.Time);
            }
            StepN(k, 5);
            var sw = Stopwatch.StartNew();
            StepN(k, 60);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / 60.0;
            const double budget = 2.0;
            if (ms > budget * 2)
            {
                Fail($"每步 {ms:0.000} ms 超过阈值 {budget} ms 的 2 倍");
            }
            else
            {
                Expect(true, $"每步 {ms:0.000} ms（阈值 {budget} ms{(ms > budget ? "，超线不到 2 倍：性能警告" : string.Empty)}；场 {k.FieldCount}、弹体 {k.ProjectileCount}）");
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

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

        private static void Line(string s) => _report.AppendLine(s);
    }
}
