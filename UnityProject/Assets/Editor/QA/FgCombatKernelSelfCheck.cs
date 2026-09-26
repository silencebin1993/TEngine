using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-03 战斗内核接入的自动验收（FG14 FGR-ARC-003 / 第 4 节通过标准；FG15 FGR-SYS-041/042；FG06 第 5、7 节；
    /// FG-GAP-018；DEBT-FG0ARCH05-01 第 ② 项；DEBT-FG0ARCH01-03）。全部起真实系统：真实 Burst 内核（CombatKernel）、真实地点控制器
    /// （家园 / 破碎都市 / 铸造前哨经 WorldSimulation 载入）、真实编队命令系统与接管系统、真实装配解析（MachineLoadoutRegistry → 装配编译）、
    /// 真实存读档（CampaignAutoSaveService 写文件 → CampaignRestoreOrchestrator 读回）。行为坏了会失败：
    /// A 数据：combat.* 调参行与文本键中英齐全。
    /// B 内核语义（无场景）：弹体飞行 / 命中 / 过期 / 不误伤友军；重炮过热迟滞与散热（Demo 数值）；编队命令到达 / 受阻放弃 / 工作赶路对齐；
    ///   大量单位同一步阵亡（事件按步限量排空、跨步保留、存读档不丢）；网格选目标 = 暴力扫描；确定性回放与中途存读档；快照损坏 / 版本不认识；
    ///   离原点一百万格的位置精度。
    /// C Demo 战斗回归（真实控制器）：编队命令（移动 / 攻击 / 守备 / 撤退 / 暂停排队）、接管（切换 / 失败码 / 死亡回弹 / 干扰拒绝 / 接管中命令冻结 / 直控移动）、
    ///   敌人 AI（侦察标记与后撤、干扰机开火与清标记、护甲机、步进炮瞄准线、维修机）、反应（标记跳转、熔穿过载穿甲与耐热反制）、首领（阶段、可伤性、主核心开火、侧后 +20%）、
    ///   家园训练靶自动交战。
    /// D 世界集成：存读档后编队命令继续执行（FG-GAP-018）且与不存档对照一致；观察 / 不观察一致；暂停与 0.5x～3x 一致；区域切换不泄漏内核、热量随机器走；
    ///   不被观察的地点没有表现对象；快照损坏按记录重建并通知；突袭原型在三个种子下成立（B25）。
    /// E 性能：200 敌人 + 80 炮塔 + ≥1,500 弹体的内核单步耗时、热更层每步事件数与托管分配、渲染缓冲。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgCombatKernelSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const float Dt = 1f / 60f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 战斗内核")]
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
            PerfLines.Clear();
            Line("\n[战斗内核] 战斗内核接入（FG0-ARCH-03）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgcombat-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"Burst 编译={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}（作业声明同步编译）；图形设备 {SystemInfo.graphicsDeviceType}。" +
                     "内核数字是 Editor 下的 Burst 作业；真机（IL2CPP，Burst AOT）内核相同或更快，热更层走 HybridCLR 解释执行另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckKernelProjectiles);
                Step(CheckKernelCannonHeat);
                Step(CheckKernelCommands);
                Step(CheckKernelMassDeath);
                Step(CheckKernelGridMatchesBruteForce);
                Step(CheckKernelDeterminismAndSnapshot);
                Step(CheckKernelSnapshotNegatives);
                Step(CheckPrecisionFarFromOrigin);
                Step(CheckDemoSquadCommands);
                Step(CheckDemoTakeover);
                Step(CheckDemoFracturedCityAi);
                Step(CheckDemoMarkJump);
                Step(CheckDemoFoundryAiAndOverload);
                Step(CheckDemoBoss);
                Step(CheckHomeAutoEngage);
                Step(CheckSaveLoadCommandsContinue);
                Step(CheckObservationIndependence);
                Step(CheckPauseSpeedMatrix);
                Step(CheckRegionSwitch);
                Step(CheckRosterRevisionGating);
                Step(CheckMassDeathThroughSave);
                Step(CheckObservationMaterials);
                Step(CheckWorkArrivalAfterLoad);
                Step(CheckBridgeBoundaryScan);
                Step(CheckCorruptSnapshotRebuild);
                Step(CheckRaidSeedIndependence);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"战斗内核自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                GameClock.ResetSession();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                GameSettings.SetLanguage(originalLanguage);
                UiEscapeStack.Clear();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [战斗内核] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] ids =
            {
                "combat.grid_cell_m", "combat.events.gameplay_per_step", "combat.events.cues_per_step", "combat.projectile_capacity",
                "combat.compact_ratio", "combat.render.unit_height", "combat.perf.enemies", "combat.perf.turrets", "combat.perf.min_projectiles",
                "combat.perf.step_budget_ms", "combat.bench.raider_hp", "combat.bench.turret_range", "combat.perf.projectile_speed",
            };
            var missing = ids.Where(id => !GridContent.TryGetTuning(id, out _)).ToList();
            Expect(missing.Count == 0, $"combat.* 调参行都在 fg.TbHomeTuning（{ids.Length} 行抽查{(missing.Count == 0 ? string.Empty : "；缺：" + string.Join(",", missing))}）");
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            Expect(cfg.MaxGameplayEventsPerStep == 64 && cfg.MaxCueEventsPerStep == 24 && cfg.ProjectileCapacity == 4096 && Mathf.Approximately(cfg.GridCell, 8f),
                $"内核配置来自表：每步玩法事件 {cfg.MaxGameplayEventsPerStep}、提示事件 {cfg.MaxCueEventsPerStep}、弹体上限 {cfg.ProjectileCapacity}、网格 {cfg.GridCell} 米");
            Expect(CombatSite.Tuning("combat.perf.enemies", 0) == 200f && CombatSite.Tuning("combat.perf.turrets", 0) == 80f
                   && CombatSite.Tuning("combat.perf.min_projectiles", 0) == 1500f && CombatSite.Tuning("combat.perf.step_budget_ms", 0) == 6f,
                "性能场景规模 = 规格（200 敌人、80 炮塔、1,500 弹体、内核 ≤ 6 ms）");
            string[] keys =
            {
                "combat.fire.no_attacker", "combat.fire.no_weapon", "combat.fire.no_output", "combat.fire.overheated", "combat.fire.cooldown",
                "combat.fire.target_dead", "combat.fire.target_missing", "combat.fire.out_of_range", "combat.fire.not_hostile", "combat.fire.invulnerable",
                "combat.load.rebuilt", "combat.load.reason.bad_magic", "combat.load.reason.unknown_format", "combat.load.reason.bad_checksum",
                "combat.load.reason.truncated", "combat.load.reason.invalid_value", "combat.load.reason.bad_payload",
                "combat.site.home", "combat.site.fractured_city", "combat.site.foundry_outpost", "combat.placeholder",
            };
            var noText = new List<string>();
            foreach (string k in keys)
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string zh = GameText.Get(k);
                GameSettings.SetLanguage(GameLanguage.En);
                string en = GameText.Get(k);
                if (!GameText.Has(k) || string.IsNullOrEmpty(zh) || string.IsNullOrEmpty(en) || zh == en || zh.Contains("⟦"))
                {
                    noText.Add(k);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(noText.Count == 0, $"{keys.Length} 个 combat.* 文本键中英齐全{(noText.Count == 0 ? string.Empty : "；缺：" + string.Join(",", noText))}");
        }

        // ── B 内核语义（无场景）──────────────────────────────────────────────────

        private static CombatKernel NewKernel()
        {
            var k = new CombatKernel(CombatSite.ConfigFromTuning(), 64);
            return k;
        }

        private static int Unit(CombatKernel k, CombatFaction f, CombatBehavior b, CombatUnitKind kind, double2 pos, float hp, int weapon, int profile = -1,
            CombatUnitFlags extra = CombatUnitFlags.None, float radius = 0.8f, float speed = 0f)
        {
            return k.Spawn(new CombatSpawn
            {
                ExtKey = -1,
                Kind = kind,
                Faction = f,
                Behavior = b,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | extra,
                Position = pos,
                Home = pos,
                Radius = radius,
                Speed = speed,
                Health = hp,
                MaxHealth = hp,
                Weapon = weapon,
                BehaviorProfile = profile,
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, -1f),
            });
        }

        private static void StepKernel(CombatKernel k, int steps, ref double t)
        {
            for (int i = 0; i < steps; i++)
            {
                k.Step(Dt, t);
                t += Dt;
                k.DrainGameplay(1 << 20, out _);
                k.ClearCues();
            }
        }

        private static void CheckKernelProjectiles()
        {
            using CombatKernel k = NewKernel();
            int w = k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 30f, Damage = 14f, Cooldown = 10f, ProjectileSpeed = 10f, ProjectileRadius = 0.2f,
            });
            int turret = Unit(k, CombatFaction.Player, CombatBehavior.HoldFire, CombatUnitKind.Turret, new double2(0, 0), 400f, w, radius: 1f);
            int raider = Unit(k, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(20, 0), 100f, -1, radius: 0.6f);
            int friend = Unit(k, CombatFaction.Player, CombatBehavior.None, CombatUnitKind.Structure, new double2(10, 0), 50f, -1, radius: 0.6f);
            double t = 0;
            StepKernel(k, 1, ref t);
            bool fired = k.ProjectileCount == 1 && k.Counters.ProjectilesSpawned == 1;
            int stepsToHit = 1;
            while (k.ProjectileCount > 0 && stepsToHit < 600)
            {
                StepKernel(k, 1, ref t);
                stepsToHit++;
            }
            k.TryGetUnit(raider, out CombatUnitView rv);
            k.TryGetUnit(friend, out CombatUnitView fv);
            // 距离 20 − 炮塔半径 1 − 弹体半径 0.2（出膛点）− 命中半径 0.8 ≈ 18 米，弹速 10 → 约 1.8 秒（108 步）。
            Expect(fired && Mathf.Approximately(rv.Health, 86f) && Mathf.Approximately(fv.Health, 50f) && k.Counters.ProjectilesHit == 1
                   && stepsToHit >= 100 && stepsToHit <= 115,
                $"弹体直线飞行、命中才结算：第 1 步出膛 1 枚，{stepsToHit} 步后命中突袭者（耐久 100→{rv.Health}），挡在弹道上的友军不受伤（{fv.Health}）");

            // 目标瞬移走：弹体飞满寿命后过期，不命中任何东西。
            int w2 = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 30f, Damage = 14f, Cooldown = 100f, ProjectileSpeed = 10f, ProjectileRadius = 0.2f });
            k.SetUnitWeapon(turret, w2);
            k.SetCycle(turret, 0f, 0f);
            StepKernel(k, 1, ref t);
            long expiredBefore = k.Counters.ProjectilesExpired;
            k.SetPosition(raider, new double2(0, 200));
            StepKernel(k, 300, ref t);
            k.TryGetUnit(raider, out rv);
            Expect(k.ProjectileCount == 0 && k.Counters.ProjectilesExpired == expiredBefore + 1 && Mathf.Approximately(rv.Health, 86f),
                $"目标离开后弹体飞满寿命（射程 × 1.25 / 弹速）过期：过期 +{k.Counters.ProjectilesExpired - expiredBefore}，突袭者耐久仍 {rv.Health}");

            // 弹体池满：超出的开火不生成弹体、计数。
            var cfg = CombatSite.ConfigFromTuning();
            cfg.ProjectileCapacity = 4;
            using var small = new CombatKernel(cfg, 16);
            int ws = small.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 50f, Damage = 1f, Cooldown = 0.01f, ProjectileSpeed = 1f, ProjectileRadius = 0.1f });
            for (int i = 0; i < 6; i++)
            {
                Unit(small, CombatFaction.Player, CombatBehavior.HoldFire, CombatUnitKind.Turret, new double2(i * 2, 0), 100f, ws);
            }
            Unit(small, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(0, 40), 1000f, -1);
            double t2 = 0;
            StepKernel(small, 3, ref t2);
            Expect(small.ProjectileCount == 4 && small.Counters.ProjectilesRefused > 0,
                $"弹体池上限 4：同时存在 {small.ProjectileCount} 枚，超出的 {small.Counters.ProjectilesRefused} 次开火被拒绝并计数（combat.projectile_capacity）");
        }

        private static void CheckKernelCannonHeat()
        {
            using CombatKernel k = NewKernel();
            // Demo 数值：重炮 55 伤害、3 秒冷却、1 秒瞄准线、积热 40（熔穿过载 +25）、100 过热 / 60 恢复、散热 10/秒（散热鳍 +5）。
            var wp = new CombatWeapon
            {
                Mode = CombatWeaponMode.Cannon, Reaction = CombatReaction.MeltOverload, Damage = FracturedCityLayout.CannonBaseDamage,
                Cooldown = FracturedCityLayout.CannonCooldownSeconds, AimSeconds = FracturedCityLayout.CannonAimSeconds,
                HeatPerShot = FracturedCityLayout.CannonBaseHeatPerShot, OverloadExtraHeat = FracturedCityLayout.OverloadExtraHeatPerShot,
                OverheatAt = FracturedCityLayout.WeaponHeatOverheatThreshold, RecoverBelow = FracturedCityLayout.WeaponHeatRecoverThreshold,
                Dissipation = FracturedCityLayout.WeaponHeatDissipationPerSecond, HeatSinkBonus = FracturedCityLayout.HeatSinkBonusDissipationPerSecond,
                PierceBonus = FracturedCityLayout.OverloadArmorPierceBonus,
            };
            int w = k.AddWeapon(wp);
            int m = Unit(k, CombatFaction.Player, CombatBehavior.Commanded, CombatUnitKind.Machine, new double2(0, 0), 100f, w, extra: CombatUnitFlags.ExternalHealth);
            int e = Unit(k, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(0, 10), 5000f, -1);
            double t = 0;
            CombatFireResult r1 = k.FireAt(m, e, t);
            CombatFireResult r2 = k.FireAt(m, e, t + 0.5);
            StepKernel(k, 60, ref t); // 1 秒
            CombatFireResult r3 = k.FireAt(m, e, t);
            k.TryGetUnit(m, out CombatUnitView mv);
            k.TryGetUnit(e, out CombatUnitView ev);
            float heatAfterShot = mv.Heat;
            CombatFireResult r4 = k.FireAt(m, e, t + 0.1);
            Expect(r1 == CombatFireResult.StillAiming && r2 == CombatFireResult.StillAiming && r3 == CombatFireResult.Ok && r4 == CombatFireResult.Cooldown
                   && Mathf.Approximately(ev.Health, 5000f - 55f) && Mathf.Approximately(heatAfterShot, 65f),
                $"重炮两段式：第一次调用开始 1 秒瞄准线（{r1}）、瞄准中（{r2}）、到点开火（{r3}，55 伤害，积热 40+25={heatAfterShot}）、3 秒冷却内（{r4}）");
            // 散热：2 秒后 65 − 20 = 45。
            StepKernel(k, 120, ref t);
            k.TryGetUnit(m, out mv);
            Expect(Mathf.Abs(mv.Heat - 45f) < 0.05f, $"散热 10/秒：2 秒后热量 {mv.Heat:F2}（期望 45）");
            // 散热鳍 +5：2 秒 −30。
            k.SetFlag(m, CombatUnitFlags.HeatSink, true);
            k.SetWeaponState(m, 80f, false, 0, 0);
            StepKernel(k, 120, ref t);
            k.TryGetUnit(m, out mv);
            Expect(Mathf.Abs(mv.Heat - 50f) < 0.05f, $"散热鳍额外 +5/秒：80 → 2 秒后 {mv.Heat:F2}（期望 50）");
            // 过热迟滞：热量 ≥ 100 停火，降到 60 以下才恢复。
            k.SetFlag(m, CombatUnitFlags.HeatSink, false);
            k.SetWeaponState(m, 95f, false, t - 1.0, 0);
            CombatFireResult r5 = k.FireAt(m, e, t);
            k.TryGetUnit(m, out mv);
            bool overheated = (mv.Flags & CombatUnitFlags.Overheated) != 0;
            StepKernel(k, 180, ref t); // 3 秒：160 → 130
            CombatFireResult r6 = k.FireAt(m, e, t);
            StepKernel(k, 7 * 60 + 6, ref t); // 再 7.1 秒：130 → 59
            k.TryGetUnit(m, out mv);
            CombatFireResult r7 = k.FireAt(m, e, t);
            Expect(r5 == CombatFireResult.Ok && overheated && r6 == CombatFireResult.Overheated && mv.Heat < 60f && r7 == CombatFireResult.StillAiming,
                $"过热迟滞：95 再开一炮 → 160 过热（{overheated}）；3 秒后仍 > 60 拒绝（{r6}）；降到 {mv.Heat:F1} < 60 恢复（重新开始瞄准：{r7}）");

            // 正面装甲 40%：熔穿过载穿甲 +30%（有效减伤 10%）；耐热反制：穿甲无效（40%）。
            int armored = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(0, 10), Home = new double2(0, 10), Radius = 0.8f,
                Health = 1000f, MaxHealth = 1000f, Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorFraction = 0.4f, ArmorHalfAngleDeg = 75f,
                ArmorFacing = new float2(0f, -1f),
            });
            k.SetWeaponState(m, 0f, false, 0, 0);
            float d1 = CannonShot(k, m, armored, ref t);
            k.SetFlag(armored, CombatUnitFlags.HeatResistant, true);
            float d2 = CannonShot(k, m, armored, ref t);
            k.SetFlag(armored, CombatUnitFlags.HeatResistant, false);
            k.SetPosition(m, new double2(0, 20)); // 从背后打：不减伤。
            float d3 = CannonShot(k, m, armored, ref t);
            Expect(Mathf.Abs(d1 - 49.5f) < 0.01f && Mathf.Abs(d2 - 33f) < 0.01f && Mathf.Abs(d3 - 55f) < 0.01f,
                $"护甲机正面 40% 减伤：熔穿过载穿甲 → {d1:F1}（55×0.9）；耐热反制抵消穿甲 → {d2:F1}（55×0.6）；侧后命中 → {d3:F1}（不减伤）");
        }

        private static float CannonShot(CombatKernel k, int m, int target, ref double t)
        {
            k.TryGetUnit(target, out CombatUnitView before);
            k.FireAt(m, target, t);
            StepKernel(k, 61, ref t);
            k.FireAt(m, target, t);
            k.TryGetUnit(target, out CombatUnitView after);
            StepKernel(k, 181, ref t);
            k.SetWeaponState(m, 0f, false, 0, 0);
            return before.Health - after.Health;
        }

        private static void CheckKernelCommands()
        {
            using CombatKernel k = NewKernel();
            int m = Unit(k, CombatFaction.Player, CombatBehavior.Commanded, CombatUnitKind.Machine, new double2(0, 0), 100f, -1,
                extra: CombatUnitFlags.ExternalHealth, radius: 0.9f, speed: 6f);
            // 走不动的机器（速度 0，例如瘫痪）：距离不再缩短 → 每秒核对一次，三次不达标放弃（Demo TrackProgressAndMaybeGiveUp：第一次核对只记距离）。
            int stuckUnit = Unit(k, CombatFaction.Player, CombatBehavior.Commanded, CombatUnitKind.Machine, new double2(50, 0), 100f, -1,
                extra: CombatUnitFlags.ExternalHealth, radius: 0.9f, speed: 0f);
            double t = 0;
            k.IssueCommand(stuckUnit, CombatCommandKind.Move, new double2(50, 20), 0, 1.2f, 6f, 1.2f, false);
            int strikes = 0;
            int ended = 0;
            byte endReason = 0;
            for (int i = 0; i < 60 * 12 && ended == 0; i++)
            {
                k.Step(Dt, t);
                t += Dt;
                CombatEvent[] ev = k.DrainGameplay(1000, out int n);
                for (int j = 0; j < n; j++)
                {
                    if (ev[j].Kind == CombatEventKind.CommandStuckStrike)
                    {
                        strikes++;
                    }
                    if (ev[j].Kind == CombatEventKind.CommandEnded)
                    {
                        ended = i + 1;
                        endReason = ev[j].Code;
                    }
                }
                k.ClearCues();
            }
            Expect(ended >= 238 && ended <= 246 && endReason == (byte)CombatEndReason.Stuck && strikes == 2,
                $"路径受阻：走不动的机器受阻提示 {strikes} 次后第 3 次放弃（第 {ended} 步 ≈ 4 秒，原因 {(CombatEndReason)endReason}），不永远站桩");

            // 移动到达：进入到达半径 1.2 即结束；速度 6 米/秒。
            k.SetObstacles(new List<float3>());
            k.SetPosition(m, new double2(0, 0));
            k.IssueCommand(m, CombatCommandKind.Move, new double2(12, 0), 0, 1.2f, 6f, 1.2f, false);
            int arrivedAt = RunUntil(k, CombatEventKind.CommandEnded, 600, ref t, out CombatEvent end);
            k.TryGetPosition(m, out double2 p);
            Expect(end.Code == (byte)CombatEndReason.Arrived && arrivedAt >= 108 && arrivedAt <= 112 && math.distance(p, new double2(12, 0)) <= 1.2,
                $"编队移动：{arrivedAt} 步到达（(12−1.2)/6 秒 ≈ 108 步），停在距目标 {math.distance(p, new double2(12, 0)):F2} 米处，命令结束");

            // 守备：到位后不结束。
            k.IssueCommand(m, CombatCommandKind.Guard, new double2(double.NaN, double.NaN), 0, 0.3f, 6f, 1.2f, false);
            RunUntil(k, CombatEventKind.CommandEnded, 180, ref t, out CombatEvent guardEnd);
            Expect(guardEnd.Kind == CombatEventKind.None && k.TryGetCommand(m, out CombatCommand gc) && gc.Kind == CombatCommandKind.Guard,
                "守备命令到位后持续（3 秒后仍在守备）");

            // 工作赶路：进入 1.2 米即对齐到目标点、发到达事件。
            k.IssueCommand(m, CombatCommandKind.WorkMove, new double2(p.x, p.y + 9), 0, 1.2f, 0f, 0f, false);
            int workAt = RunUntil(k, CombatEventKind.WorkArrived, 600, ref t, out _);
            k.TryGetPosition(m, out double2 wp);
            Expect(workAt > 0 && math.distance(wp, new double2(p.x, p.y + 9)) < 1e-9,
                $"工作赶路：{workAt} 步到达并对齐到目标点（偏差 {math.distance(wp, new double2(p.x, p.y + 9)):E1} 米）");

            // 接入：受控机不执行编队命令，位移来自直控输入（6 米/秒），命令保留。
            k.IssueCommand(m, CombatCommandKind.Move, new double2(wp.x + 50, wp.y), 0, 1.2f, 6f, 1.2f, false);
            k.SetFlag(m, CombatUnitFlags.Possessed, true);
            k.SetDirectInput(m, new float2(0f, 1f));
            StepKernel(k, 60, ref t);
            k.TryGetPosition(m, out double2 dp);
            bool kept = k.TryGetCommand(m, out CombatCommand kc) && kc.Kind == CombatCommandKind.Move;
            Expect(Math.Abs(dp.x - wp.x) < 1e-6 && Math.Abs(dp.y - wp.y - 6.0) < 0.01 && kept,
                $"接入中：直控 1 秒位移 {dp.y - wp.y:F3} 米（期望 6）、编队命令不推进但保留（{kept}）");
            k.SetFlag(m, CombatUnitFlags.Possessed, false);
            k.SetDirectInput(m, float2.zero);
        }

        private static int RunUntil(CombatKernel k, CombatEventKind kind, int maxSteps, ref double t, out CombatEvent found)
        {
            found = default;
            for (int i = 0; i < maxSteps; i++)
            {
                k.Step(Dt, t);
                t += Dt;
                CombatEvent[] ev = k.DrainGameplay(1000, out int n);
                for (int j = 0; j < n; j++)
                {
                    if (ev[j].Kind == kind)
                    {
                        found = ev[j];
                        k.ClearCues();
                        return i + 1;
                    }
                }
                k.ClearCues();
            }
            return 0;
        }

        private static void CheckKernelMassDeath()
        {
            using CombatKernel k = NewKernel();
            int w = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 80f, Damage = 1000f, Cooldown = 5f });
            var enemies = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                enemies.Add(Unit(k, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(i % 20, 30 + i / 20), 50f, -1,
                    extra: CombatUnitFlags.Report));
            }
            for (int i = 0; i < 200; i++)
            {
                Unit(k, CombatFaction.Player, CombatBehavior.HoldFire, CombatUnitKind.Turret, new double2(i % 20, -(i / 20)), 100f, w);
            }
            double t = 0;
            k.Step(Dt, t);
            t += Dt;
            int dead = enemies.Count(id => !k.IsAlive(id));
            int queued = k.GameplayPending;
            int maxPer = k.Config.MaxGameplayEventsPerStep;
            // 每步只取 N 条（热更层开销与阵亡数无关）；第二步前存档，剩下的事件随快照走。
            var kills = new HashSet<int>();
            int damaged = 0;
            int steps = 0;
            int maxDrained = 0;
            CombatEvent[] first = k.DrainGameplay(maxPer, out int n1);
            maxDrained = Math.Max(maxDrained, n1);
            Tally(first, n1, kills, ref damaged);
            steps++;
            byte[] snap = k.Serialize();
            using var reloaded = NewKernel();
            CombatLoadResult lr = reloaded.Load(snap);
            int pendingAfterLoad = reloaded.GameplayPending;
            while (reloaded.GameplayPending > 0 && steps < 100)
            {
                reloaded.Step(Dt, t);
                t += Dt;
                CombatEvent[] ev = reloaded.DrainGameplay(maxPer, out int n);
                maxDrained = Math.Max(maxDrained, n);
                Tally(ev, n, kills, ref damaged);
                reloaded.ClearCues();
                steps++;
            }
            Expect(dead == 200 && queued == 400 && lr == CombatLoadResult.Ok && pendingAfterLoad == queued - n1 && kills.Count == 200 && damaged == 200
                   && maxDrained <= maxPer && steps == (int)Math.Ceiling(queued / (double)maxPer),
                $"200 个敌人同一步阵亡：玩法事件 {queued} 条（受伤 + 阵亡），每步至多交 {maxPer} 条、{steps} 步排空；中途存读档后队列 {pendingAfterLoad} 条原样保留；" +
                $"阵亡事件恰好 {kills.Count} 个不重不漏");

            // 匿名单位（突袭规模）阵亡即移除：墓碑达到一半时步首压实，存活单位 ID 不变、相对顺序不变，渲染缓冲同步。
            using CombatKernel k2 = NewKernel();
            int w2 = k2.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 80f, Damage = 1000f, Cooldown = 5f });
            var raiders = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                raiders.Add(Unit(k2, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(i % 20, 30 + i / 20), 50f, -1,
                    extra: CombatUnitFlags.RemoveOnDeath | CombatUnitFlags.Instanced));
            }
            var turrets = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                turrets.Add(Unit(k2, CombatFaction.Player, CombatBehavior.HoldFire, CombatUnitKind.Turret, new double2(i % 20, -(i / 20)), 100f, w2,
                    extra: CombatUnitFlags.Instanced));
            }
            double t2 = 0;
            StepKernel(k2, 1, ref t2); // 200 座炮塔同一步打死 200 个突袭者
            int deadRaiders = raiders.Count(id => !k2.IsAlive(id));
            StepKernel(k2, 1, ref t2); // 墓碑 200 ≥ 一半 → 步首压实
            int slots = k2.SlotCount;
            bool idsStable = turrets.All(id => k2.Exists(id)) && raiders.All(id => !k2.Exists(id));
            bool orderKept = true;
            for (int i = 0; i < turrets.Count; i++)
            {
                orderKept &= k2.ViewAt(i).Id == turrets[i];
            }
            var units = new Unity.Collections.NativeList<CombatInstance>(64, Unity.Collections.Allocator.TempJob);
            var projs = new Unity.Collections.NativeList<CombatInstance>(64, Unity.Collections.Allocator.TempJob);
            k2.PrepareRender(units, projs, double2.zero);
            int drawn = units.Length;
            units.Dispose();
            projs.Dispose();
            Expect(deadRaiders == 200 && slots == 200 && idsStable && orderKept && k2.Counters.Compactions >= 1 && drawn == 200 && k2.Counters.KillsHostile == 200,
                $"匿名突袭者同一步阵亡 {deadRaiders} 个：下一步压实为 {slots} 个槽位（压实 {k2.Counters.Compactions} 次），炮塔 ID 与顺序不变（{idsStable && orderKept}），实例化绘制 {drawn} 个");
        }

        private static void Tally(CombatEvent[] ev, int n, HashSet<int> kills, ref int damaged)
        {
            for (int i = 0; i < n; i++)
            {
                if (ev[i].Kind == CombatEventKind.Killed)
                {
                    kills.Add(ev[i].Unit);
                }
                else if (ev[i].Kind == CombatEventKind.Damaged)
                {
                    damaged++;
                }
            }
        }

        private static void CheckKernelGridMatchesBruteForce()
        {
            using CombatKernel k = NewKernel();
            var rng = new System.Random(20260926);
            k.SetObstacles(new List<float3> { new float3(5, 5, 3), new float3(-8, 2, 2), new float3(0, -10, 4) });
            var players = new List<int>();
            var hostiles = new List<int>();
            for (int i = 0; i < 120; i++)
            {
                var pos = new double2(rng.NextDouble() * 80 - 40, rng.NextDouble() * 80 - 40);
                if (i % 3 == 0)
                {
                    players.Add(Unit(k, CombatFaction.Player, CombatBehavior.None, CombatUnitKind.Machine, pos, 100f, -1));
                }
                else
                {
                    hostiles.Add(Unit(k, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, pos, (float)(20 + rng.Next(80)), -1));
                }
            }
            // 并列距离：两个敌人与某台机器等距。
            k.SetPosition(hostiles[0], new double2(1, 0));
            k.SetPosition(hostiles[1], new double2(-1, 0));
            k.SetPosition(players[0], new double2(0, 0));
            int mismatch = 0;
            int checks = 0;
            foreach (int p in players)
            {
                foreach (float range in new[] { 6f, 15f, 40f })
                {
                    foreach (bool los in new[] { false, true })
                    {
                        int viaGrid = k.QueryTarget(p, range, los, CombatTargetMode.Nearest);
                        int brute = BruteNearest(k, p, range, los);
                        checks++;
                        if (viaGrid != brute)
                        {
                            mismatch++;
                        }
                    }
                }
            }
            int tie = k.QueryTarget(players[0], 3f, false, CombatTargetMode.Nearest);
            Expect(mismatch == 0 && checks == players.Count * 6 && tie == hostiles[1],
                $"空间网格选目标 = 暴力扫描（{checks} 组：3 种射程 × 有无视线；不一致 {mismatch}）；等距并列取槽位靠后者（同 Demo 按列表扫描“≤ 当前最佳就替换”）");
        }

        private static int BruteNearest(CombatKernel k, int seekerId, float range, bool los)
        {
            k.TryGetUnit(seekerId, out CombatUnitView s);
            int best = 0;
            int bestSlot = -1;
            double bestDist = range;
            for (int slot = 0; slot < k.SlotCount; slot++)
            {
                CombatUnitView v = k.ViewAt(slot);
                if (!v.Alive || v.Faction != CombatFaction.Hostile || v.Id == seekerId)
                {
                    continue;
                }
                double d = math.distance(s.Position, v.Position);
                if (d > bestDist)
                {
                    continue;
                }
                if (los && !k.LineOfSight(s.Position, v.Position))
                {
                    continue;
                }
                if (d < bestDist || slot > bestSlot)
                {
                    bestDist = d;
                    best = v.Id;
                    bestSlot = slot;
                }
            }
            return best;
        }

        private static CombatSite PerfSite(int enemies, int turrets, out CombatBench.Spec spec)
        {
            var site = new CombatSite("selfcheck_perf", CombatSite.ConfigFromTuning());
            spec = CombatBench.PerfSpec();
            CombatBench.SpawnPerfScenario(site, Vector2.zero, enemies, turrets, spec);
            return site;
        }

        private static void CheckKernelDeterminismAndSnapshot()
        {
            ulong RunOnce(bool reloadMidway, out long projectilesSeen)
            {
                projectilesSeen = 0;
                CombatSite site = PerfSite(40, 16, out _);
                double t = 0;
                try
                {
                    for (int i = 0; i < 900; i++)
                    {
                        if (reloadMidway && i == 450)
                        {
                            byte[] snap = site.Kernel.Serialize();
                            CombatSiteRecord rec = site.Snapshot();
                            site.Dispose();
                            site = new CombatSite("selfcheck_perf", CombatSite.ConfigFromTuning());
                            if (!site.TryRestore(null, rec, out string key))
                            {
                                Fail("确定性对照：中途读档失败 " + key);
                            }
                            _ = snap;
                        }
                        site.Step(Dt, t);
                        t += Dt;
                        projectilesSeen = Math.Max(projectilesSeen, site.Kernel.ProjectileCount);
                    }
                    return site.Kernel.StateHash();
                }
                finally
                {
                    site.Dispose();
                }
            }
            ulong a = RunOnce(false, out long pa);
            ulong b = RunOnce(false, out long pb);
            ulong c = RunOnce(true, out long pc);
            Expect(a == b && a == c && pa > 0,
                $"确定性回放：40 突袭者 + 16 炮塔跑 900 步，两次运行状态哈希一致（{a:X16}），第 450 步存档再读档接着跑也一致（{c:X16}）；期间最多 {pa} 枚弹体在飞");
        }

        private static void CheckKernelSnapshotNegatives()
        {
            using CombatKernel k = NewKernel();
            int w = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 1, Range = 10f, Damage = 1f, Cooldown = 1f });
            Unit(k, CombatFaction.Player, CombatBehavior.HoldFire, CombatUnitKind.Turret, new double2(0, 0), 100f, w);
            Unit(k, CombatFaction.Hostile, CombatBehavior.None, CombatUnitKind.Enemy, new double2(3, 0), 100f, -1);
            double t = 0;
            StepKernel(k, 10, ref t);
            byte[] good = k.Serialize();
            ulong h = k.StateHash();
            byte[] flipped = (byte[])good.Clone();
            flipped[flipped.Length / 2] ^= 0x5A;
            byte[] future = (byte[])good.Clone();
            future[4] = 99;
            byte[] truncated = good.Take(good.Length / 2).ToArray();
            using var target = NewKernel();
            CombatLoadResult r1 = target.Load(flipped);
            CombatLoadResult r2 = target.Load(future);
            CombatLoadResult r3 = target.Load(truncated);
            CombatLoadResult r4 = target.Load(new byte[] { 1, 2, 3 });
            int slotsAfterFailures = target.SlotCount;
            CombatLoadResult r5 = target.Load(good);
            Expect(r1 == CombatLoadResult.BadChecksum && r2 == CombatLoadResult.UnknownFormat && (r3 == CombatLoadResult.BadChecksum || r3 == CombatLoadResult.Truncated)
                   && r4 == CombatLoadResult.BadMagic && slotsAfterFailures == 0 && r5 == CombatLoadResult.Ok && target.StateHash() == h
                   && CombatKernel.PeekFormat(future) == 99,
                $"快照负向：翻一个字节 → {r1}；不认识的格式版本 → {r2}（版本号可读出 99，原数据由上层保留）；截断 → {r3}；乱码 → {r4}；失败都不改动内核；完好快照读回哈希一致");
        }

        private static void CheckPrecisionFarFromOrigin()
        {
            using CombatKernel k = NewKernel();
            var start = new double2(1_000_000.3, -1_000_000.7);
            int m = Unit(k, CombatFaction.Player, CombatBehavior.Commanded, CombatUnitKind.Machine, start, 100f, -1,
                extra: CombatUnitFlags.ExternalHealth, radius: 0.9f, speed: 6f);
            double t = 0;
            k.IssueCommand(m, CombatCommandKind.WorkMove, start + new double2(10.123456, 0.25), 0, 1.2f, 0f, 0f, false);
            RunUntil(k, CombatEventKind.WorkArrived, 600, ref t, out _);
            k.TryGetPosition(m, out double2 p);
            double err = math.distance(p, start + new double2(10.123456, 0.25));
            using var reloaded = NewKernel();
            reloaded.Load(k.Serialize());
            reloaded.TryGetPosition(m, out double2 q);
            int chunk = GridContent.TuningInt("grid.chunk_size");
            WorldCoord wc = WorldCoord.FromWorld(q.x, q.y, chunk);
            WorldCoord origin = WorldCoord.FromWorld(1_000_000, -1_000_000, chunk);
            Vector3 render = wc.ToRenderPosition(origin.ChunkX, origin.ChunkY, chunk);
            double renderX = (double)(origin.ChunkX * (long)chunk) + render.x;
            double renderZ = (double)(origin.ChunkY * (long)chunk) + render.z;
            double renderErr = Math.Sqrt((renderX - q.x) * (renderX - q.x) + (renderZ - q.y) * (renderZ - q.y));
            Expect(err < 1e-6 && p.x == q.x && p.y == q.y && renderErr < 0.001,
                $"离原点一百万格：移动 10 米后位置误差 {err:E1} 米；存读档逐位一致；按区块索引 + 区块内偏移换算到画面误差 {renderErr * 1000:F4} 毫米（< 1 毫米，DEBT-FG0ARCH05-01 ②）");
        }

        // ── C Demo 战斗回归（真实控制器）─────────────────────────────────────────

        private static CampaignState NewCampaign(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgcombat-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            return s;
        }

        private const string BpGun = "bp_selfcheck_gun";
        private const string BpMarkJump = "bp_selfcheck_markjump";
        private const string BpCannon = "bp_selfcheck_cannon";
        private const string BpOverload = "bp_selfcheck_overload";

        private static void MakeBlueprints(CampaignState s)
        {
            void Add(string id, string primary, string utility, string structure, params string[] fw)
            {
                if (s.BlueprintRecords.Any(b => b.BlueprintId == id))
                {
                    return;
                }
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = id,
                    DisplayName = id,
                    ActiveVersion = 1,
                    Versions = new[]
                    {
                        new BlueprintVersionRecord
                        {
                            Version = 1, ChassisId = HomeValleyLayout.Erc003ChassisId, PrimaryId = primary, UtilityId = utility,
                            StructureId = structure, OrderedFirmwareIds = fw,
                        },
                    },
                }).ToArray();
            }
            Add(BpGun, ComponentCatalog.CompGunId, null, null);
            Add(BpMarkJump, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null, FirmwareCatalog.FwMarkTagId);
            Add(BpCannon, ComponentCatalog.CompCannonId, null, null);
            Add(BpOverload, ComponentCatalog.CompCannonId, null, null, FirmwareCatalog.FwOverloadId);
        }

        private static int Spawn(string region, string bp, Vector2 at, float hp = 120f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
            if (!r.Success)
            {
                Fail($"登记测试机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static FracturedCityController OpenFracturedCity(CampaignState s, params int[] ids)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(ids, resume: false);
        }

        private static FoundryOutpostController OpenFoundry(CampaignState s, params int[] ids)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFoundryOutpost(ids, resume: false);
        }

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static void PlaceEnemy(CombatSite site, string enemyId, Vector2 at)
        {
            if (site.TryGetEnemyUnit(enemyId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static float Hp(int logicId) => MachineRegistry.TryGetRecord(logicId, out MachineRecord r) ? r.Health : -1f;

        private static RegionEnemyRecord Enemy(CampaignState s, string id) => s.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == id);

        private static float WeaponDamage(CampaignState s, int logicId) =>
            MachineLoadoutRegistry.ResolveForAi(s, logicId, s.RandomSeed) is var r && r.Success ? Mathf.Max(0f, r.Preview.TotalNormalizedDamage) : -1f;

        private static void CheckDemoSquadCommands()
        {
            CampaignState s = NewCampaign(7101);
            MakeBlueprints(s);
            int a = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            int b = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(2f, -24f));
            FracturedCityController city = OpenFracturedCity(s, a, b);
            CombatSite site = city.Combat;
            Expect(site != null && site.MachineCount == 2 && city.LiveMachineCount == 2 && site.EnemyIds.Count() == 3,
                $"破碎都市进场：2 台机器、3 个敌人（2 侦察 + 1 干扰）进战斗内核（机器 {site?.MachineCount}，敌人 {site?.EnemyIds.Count()}）");
            RegionSquadCommandSystem squad = city.SquadCommands;

            // 移动：到达目标（距离 ≤ 1.2）后交还，事件文本与 Demo 一致。
            squad.DebugSelectMany(new[] { a });
            squad.IssueMoveTo(new Vector2(-2f, -18f), paused: false);
            bool moving = squad.TryGetActiveCommandKind(a, out RegionCommandKind k1) && k1 == RegionCommandKind.Move;
            WorldSimulation.StepMany(90);
            site.TryGetMachinePosition(a, out Vector2 pa);
            bool arrivedText = squad.RecentEvents.Any(e => e.Contains($"机器 #{a} 已到达目标，交还 AI"));
            Expect(moving && arrivedText && Vector2.Distance(pa, new Vector2(-2f, -18f)) <= 1.2f && !squad.TryGetActiveCommandKind(a, out _),
                $"编队移动：命令在内核里执行（下达后 {k1}），1.5 秒内到达（距目标 {Vector2.Distance(pa, new Vector2(-2f, -18f)):F2} 米）、文本“已到达目标，交还 AI”、命令结束");

            // 暂停中下达 = 排队；恢复后第一步开始执行。
            GameClock.SetPaused(true);
            squad.DebugSelectMany(new[] { b });
            squad.IssueMoveTo(new Vector2(6f, -24f), paused: true);
            int queued = squad.QueuedCommandCount;
            WorldSimulation.Frame(0.5f);
            site.TryGetMachinePosition(b, out Vector2 pbPaused);
            GameClock.SetPaused(false);
            WorldSimulation.StepMany(30);
            site.TryGetMachinePosition(b, out Vector2 pbAfter);
            Expect(queued == 1 && Vector2.Distance(pbPaused, new Vector2(2f, -24f)) < 1e-3f && pbAfter.x > 2.5f && squad.QueuedCommandCount == 0
                   && squad.RecentEvents.Any(e => e.Contains("已排队")),
                $"暂停中下达的移动排队（{queued} 条，暂停期间不动），恢复后开始执行（0.5 秒后 x={pbAfter.x:F2}），“已排队”清零");

            // 守备：持续；撤退：回到安全点。
            squad.DebugSelectMany(new[] { a });
            squad.IssueGuardHere(paused: false);
            WorldSimulation.StepMany(120);
            bool guarding = squad.TryGetActiveCommandKind(a, out RegionCommandKind k2) && k2 == RegionCommandKind.Guard;
            squad.IssueRetreat(paused: false);
            WorldSimulation.StepMany(120);
            site.TryGetMachinePosition(a, out Vector2 pr);
            Expect(guarding && Vector2.Distance(pr, FracturedCityLayout.EntryEvac.Position) <= 1.2f
                   && squad.RecentEvents.Any(e => e.Contains($"机器 #{a} 已撤到安全点，交还 AI")),
                $"守备持续 2 秒仍在守备；撤退回到入口安全区（距 {Vector2.Distance(pr, FracturedCityLayout.EntryEvac.Position):F2} 米），文本“已撤到安全点”");

            // 攻击：追到 6 米接战距离，每 1.2 秒一次，伤害 = 装配编译出的伤害；击毁后交还并掉落。
            // 攻击驻守的静默干扰机（侦察机受威胁会后撤，节奏不固定；干扰机不动，便于断言 1.2 秒一发）。
            RegionEnemyRecord scout = Enemy(s, FracturedCityLayout.JammerSpawnId);
            float dmg = WeaponDamage(s, a);
            float hp0 = scout.Health;
            squad.IssueAttack(scout.EnemyInstanceId, paused: false);
            int stepsUntilFirstHit = 0;
            for (int i = 0; i < 600 && Enemy(s, scout.EnemyInstanceId).Health >= hp0; i++)
            {
                WorldSimulation.StepMany(1);
                stepsUntilFirstHit++;
            }
            site.TryGetMachinePosition(a, out Vector2 atHit);
            site.TryGetEnemyPosition(scout.EnemyInstanceId, out Vector2 scoutAt);
            float firstDrop = hp0 - Enemy(s, scout.EnemyInstanceId).Health;
            WorldSimulation.StepMany(74); // 再 1.2 秒（72 步，浮点累计可能晚 1 步）= 第二发
            float secondDrop = hp0 - Enemy(s, scout.EnemyInstanceId).Health;
            int guard = 0;
            while (Enemy(s, scout.EnemyInstanceId).IsAlive && guard++ < 60 * 60)
            {
                WorldSimulation.StepMany(1);
            }
            bool killedText = squad.RecentEvents.Any(e => e.Contains($"击毁目标 {scout.EnemyInstanceId}"));
            bool loot = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.GroundItemId != null && g.GroundItemId.Contains(scout.EnemyInstanceId));
            Expect(dmg > 0f && Mathf.Abs(firstDrop - dmg) < 0.01f && Mathf.Abs(secondDrop - 2 * dmg) < 0.02f
                   && Vector2.Distance(atHit, scoutAt) <= 6.3f && !Enemy(s, scout.EnemyInstanceId).IsAlive && killedText
                   && (s.RegionRecords.First(r => r.RegionId == FracturedCityLayout.RegionId).DestroyedNodeIds ?? Array.Empty<string>()).Contains(scout.EnemyInstanceId),
                $"编队攻击：追到接战距离内（{Vector2.Distance(atHit, scoutAt):F1} 米 ≤ 6）开火，每发 {dmg:F1}（装配编译值），1.2 秒后第二发（累计 {secondDrop:F1}）；" +
                $"击毁后文本“击毁目标”、记为已清除{(loot ? "、掉落废料" : string.Empty)}");
        }

        private static void CheckDemoTakeover()
        {
            CampaignState s = NewCampaign(7102);
            MakeBlueprints(s);
            int a = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            int b = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(2f, -24f));
            int c = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(6f, -24f));
            FracturedCityController city = OpenFracturedCity(s, a, b, c);
            CombatSite site = city.Combat;
            RegionControlSystem control = city.Control;

            // 接管前给 b 下守备、给 c 下移动：接管 b 时守备冻结，释放后恢复；接管 c 再释放，移动被取消（AC-CTL-007）。
            city.SquadCommands.DebugSelectMany(new[] { b });
            city.SquadCommands.IssueGuardHere(paused: false);
            city.SquadCommands.DebugSelectMany(new[] { c });
            city.SquadCommands.IssueMoveTo(new Vector2(6f, -10f), paused: false);
            RegionControlSwitchResult r1 = control.TrySwitchControlledUnit(a);
            RegionControlSwitchResult r2 = control.TrySwitchControlledUnit(a);
            bool possessedFlag = site.TryGetMachineUnit(a, out int ua) && site.Kernel.TryGetUnit(ua, out CombatUnitView va) && (va.Flags & CombatUnitFlags.Possessed) != 0;
            Expect(r1.Success && !r2.Success && r2.Failure == RegionControlFailure.AlreadyControlled && city.PossessedMachineLogicId == a && possessedFlag,
                $"接管机器 #{a} 成功，内核里标为“正在接入”；重复接管同一台拒绝（{r2.Failure}）");

            // 直控移动：输入交给内核，6 米/秒；暂停时不动。
            HomeValleyMachineMarker ma = FindMarker(city, a);
            Vector2 p0 = ma.Position;
            ma.SetDirectInput(new Vector2(1f, 0f));
            WorldSimulation.StepMany(60);
            Vector2 p1 = ma.Position;
            GameClock.SetPaused(true);
            WorldSimulation.Frame(1f);
            Vector2 p2 = ma.Position;
            GameClock.SetPaused(false);
            InputRouter.SetGameplayPaused(false); // 暂停态按帧同步给输入；自检用无头步进，这里手动复位。
            ma.SetDirectInput(Vector2.zero);
            Expect(Mathf.Abs(p1.x - p0.x - 6f) < 0.01f && Mathf.Abs(p1.y - p0.y) < 1e-4f && (p2 - p1).sqrMagnitude < 1e-8f,
                $"直控移动在内核的模拟步里执行：1 秒位移 {p1.x - p0.x:F3} 米（6 米/秒），暂停时不动");

            // 接管 b（守备中）→ 守备冻结；Tab 切到 c（移动中）→ b 释放后守备恢复；c 释放时移动被取消。
            RegionControlSwitchResult r3 = control.TrySwitchControlledUnit(b);
            bool bGuardKept = city.SquadCommands.TryGetActiveCommandKind(b, out RegionCommandKind kb) && kb == RegionCommandKind.Guard;
            RegionControlSwitchResult r4 = control.TrySwitchControlledUnit(c);
            bool bGuardAfter = city.SquadCommands.TryGetActiveCommandKind(b, out RegionCommandKind kb2) && kb2 == RegionCommandKind.Guard;
            control.ReleaseToStrategy();
            bool cMoveCancelled = !city.SquadCommands.TryGetActiveCommandKind(c, out _);
            Expect(r3.Success && bGuardKept && r4.Success && bGuardAfter && cMoveCancelled,
                $"接管中守备保留、释放后照常（{kb2}）；移动命令在释放时取消（AC-CTL-007）");

            // 死亡回弹：受控机阵亡 → 回弹到最近的存活机器。
            control.TrySwitchControlledUnit(a);
            MachineRegistry.ApplyDamage(a, 9999f);
            bool killedInKernel = site.TryGetMachineUnit(a, out int deadUnit) && !site.Kernel.IsAlive(deadUnit);
            control.Tick(0.1f);
            int? rebound = city.PossessedMachineLogicId;
            Expect(killedInKernel && rebound.HasValue && rebound.Value != a && MachineRegistry.TryGetRecord(rebound.Value, out MachineRecord rr) && rr.IsAlive,
                $"受控机阵亡：内核同步标为阵亡（{killedInKernel}），控制权回弹到最近的存活机器 #{rebound}");

            // 干扰场里拒绝接管（ER5-REGION-01）：把一台机器放进监听节点 12 米内。
            control.ReleaseToStrategy();
            Place(site, b, FracturedCityLayout.ListeningNode.Position + new Vector2(3f, 0f));
            RegionControlSwitchResult r5 = control.TrySwitchControlledUnit(b);
            Expect(!r5.Success && r5.Failure == RegionControlFailure.SignalJammed,
                $"干扰场内拒绝接管（{r5.Failure}），判定读内核里的实时位置");
        }

        private static HomeValleyMachineMarker FindMarker(FracturedCityController city, int logicId) =>
            city.Combat.TryGetMachineMarker(logicId, out HomeValleyMachineMarker m) ? m : null;

        private static void CheckDemoFracturedCityAi()
        {
            CampaignState s = NewCampaign(7103);
            MakeBlueprints(s);
            int m = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            int j = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(2f, -24f));
            FracturedCityController city = OpenFracturedCity(s, m, j);
            CombatSite site = city.Combat;
            // 侦察机 1（出生点 (-10,-8)）：机器 5 米外视线内 → 受威胁后撤（不出牵引半径 5），8 秒周期标记它 10 秒。
            Place(site, m, new Vector2(-10f, -3f));
            Place(site, j, new Vector2(30f, -24f));
            double t0 = GameClock.GameSeconds;
            int markStep = -1;
            float maxLeash = 0f;
            for (int i = 0; i < 60 * 9; i++)
            {
                WorldSimulation.StepMany(1);
                site.TryGetEnemyPosition(FracturedCityLayout.Scout1SpawnId, out Vector2 sp);
                maxLeash = Mathf.Max(maxLeash, Vector2.Distance(sp, FracturedCityLayout.Scout1Spawn.Position));
                if (markStep < 0 && FracturedCityRegion.IsMachineMarked(s, m))
                {
                    markStep = i + 1;
                }
            }
            site.TryGetEnemyPosition(FracturedCityLayout.Scout1SpawnId, out Vector2 scoutNow);
            Expect(markStep >= 470 && markStep <= 482 && maxLeash > 0.5f && maxLeash <= FracturedCityLayout.ScoutFleeLeash + 0.01f,
                $"侦察机：周期 8 秒到点标记视线内最近的机器（第 {markStep} 步，约 {markStep / 60f:F2} 秒）；受威胁后撤、离出生点最远 {maxLeash:F2} 米（牵引 {FracturedCityLayout.ScoutFleeLeash}）");

            // 视线被挡：机器躲到锚点净空圈后面 → 周期到了也不标记（“标记不魔法穿墙”）。
            FracturedCityRegion.TryClearMark(s, m);
            site.TryGetMachineUnit(m, out int mu);
            site.Kernel.SetMarkedUntil(mu, 0);
            // 侦察机 2（出生点 (10,-8)），机器放在它北边 8 米、中间隔着干扰机锚点？改用终端锚点 (0,10)：侦察机 2 放到 (0,4.5)…… 直接构造：把侦察机 2 放在终端正下方，机器放在终端正上方。
            PlaceEnemy(site, FracturedCityLayout.Scout2SpawnId, new Vector2(0f, 7f));
            Place(site, m, new Vector2(0f, 13.5f));
            site.TryGetEnemyUnit(FracturedCityLayout.Scout2SpawnId, out int s2);
            site.Kernel.SetCycle(s2, 0.05f, 0f);
            site.TryGetEnemyUnit(FracturedCityLayout.Scout1SpawnId, out int s1);
            site.Kernel.SetCycle(s1, 100f, 0f);
            WorldSimulation.StepMany(6);
            bool blockedNoMark = !FracturedCityRegion.IsMachineMarked(s, m);
            Expect(blockedNoMark, "视线被锚点净空圈挡住：侦察机周期到了也不标记（不魔法穿墙）");

            // 干扰机（(4,4)）：8 米内有机器 → 周期到点开火 6 伤害，之后每 2.5 秒一次；12 米内机器身上的标记被清掉。
            Place(site, j, new Vector2(4f, -3f));
            site.TryGetEnemyUnit(FracturedCityLayout.JammerSpawnId, out int jam);
            site.Kernel.SetCycle(jam, 0.05f, 0f);
            site.TryGetMachineUnit(j, out int ju);
            site.Kernel.SetMarkedUntil(ju, GameClock.GameSeconds + 10);
            FracturedCityRegion.TryMarkMachine(s, j, 10f);
            float hpBefore = Hp(j);
            WorldSimulation.StepMany(6);
            float afterFirst = Hp(j);
            bool cleared = site.Kernel.TryGetUnit(ju, out CombatUnitView jv) && jv.MarkedUntil <= 0 && !FracturedCityRegion.IsMachineMarked(s, j);
            WorldSimulation.StepMany(150);
            float afterSecond = Hp(j);
            Expect(Mathf.Approximately(hpBefore - afterFirst, FracturedCityLayout.JammerAttackDamage) && Mathf.Approximately(hpBefore - afterSecond, 2 * FracturedCityLayout.JammerAttackDamage)
                   && cleared,
                $"干扰机：开火命中 {hpBefore - afterFirst:F0}（Demo {FracturedCityLayout.JammerAttackDamage}），2.5 秒后第二发（累计 {hpBefore - afterSecond:F0}）；半径 12 米内的标记被清掉（{cleared}）");
        }

        private static void CheckDemoMarkJump()
        {
            CampaignState s = NewCampaign(7104);
            MakeBlueprints(s);
            int m = Spawn(FracturedCityLayout.RegionId, BpMarkJump, new Vector2(-2f, -24f));
            FracturedCityController city = OpenFracturedCity(s, m);
            CombatSite site = city.Combat;
            MachineCombatResolution res = MachineLoadoutRegistry.ResolveForAi(s, m, s.RandomSeed);
            float d = Mathf.Max(0f, res.Preview.TotalNormalizedDamage);
            bool loadoutOk = res.Success && res.Preview.HasMarkerFunction && res.Preview.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId && d > 0f;
            // 两个侦察机相距 4 米、都离干扰机 12 米以外（干扰机会清掉半径内友军身上的标记）。
            string s1 = FracturedCityLayout.Scout1SpawnId;
            string s2 = FracturedCityLayout.Scout2SpawnId;
            PlaceEnemy(site, s1, new Vector2(-10f, -8f));
            PlaceEnemy(site, s2, new Vector2(-6f, -8f));
            float hp1 = Enemy(s, s1).Health;
            float hp2 = Enemy(s, s2).Health;
            // 第一发打 s2：打上标记，不跳转；第二发打 s1：打上标记（它此前没标记），不跳转；第三发打 s1：s1 已标记 → 跳到 s2（已标记、8 米内、视线通）。
            FracturedCityRegion.ActionResult r1 = FracturedCityRegion.TryAttackEnemy(s, m, s2, s.RandomSeed, isAiSource: false);
            FracturedCityRegion.ActionResult r2 = FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            float s2AfterTwo = Enemy(s, s2).Health;
            FracturedCityRegion.ActionResult r3 = FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            float e1 = Mathf.Max(0f, hp1 - 2 * d);
            float e2 = Mathf.Max(0f, hp2 - d - d * FracturedCityLayout.MarkJumpDamageFalloff);
            Expect(loadoutOk && r1.Success && r2.Success && r3.Success && Mathf.Approximately(s2AfterTwo, Mathf.Max(0f, hp2 - d))
                   && Mathf.Abs(Enemy(s, s1).Health - e1) < 0.01f && Mathf.Abs(Enemy(s, s2).Health - e2) < 0.01f,
                $"标记跳转：装配 = 连射器 + 标记器 + 标记跳转固件（单发 {d:F1}）；前两发只打标记不跳转，第三发命中已标记目标 → 跳到 8 米内已标记的另一个，" +
                $"伤害 × {FracturedCityLayout.MarkJumpDamageFalloff}（{hp2:F0}→{Enemy(s, s2).Health:F1}）");

            // 干扰机清友军标记：把两个侦察机拉到干扰机 12 米内，一步之后标记没了 → 再打不跳转。
            PlaceEnemy(site, s1, new Vector2(0f, -2f));
            PlaceEnemy(site, s2, new Vector2(2f, -2f));
            FracturedCityRegion.TryAttackEnemy(s, m, s2, s.RandomSeed, isAiSource: false);
            WorldSimulation.StepMany(1);
            site.TryGetEnemyUnit(s2, out int u2);
            bool clearedByJammer = site.Kernel.TryGetUnit(u2, out CombatUnitView v2) && (v2.MarkedUntil <= GameClock.GameSeconds || !v2.Alive);
            Expect(clearedByJammer, "静默干扰机清除 12 米内友军身上的标记（标记跳转的反制手段）");
        }

        private static void UnlockCoreGate(CampaignState s, int overloadMachine)
        {
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).Distinct().ToArray();
            CampaignEventLedger.TryGrant(s, BlueprintEditorService.ReactionChargeEventId(MechanicalReactionCatalog.ReactionMeltOverloadId), "BlueprintReactionCharge", 0f);
            _ = overloadMachine;
        }

        private static void CheckDemoFoundryAiAndOverload()
        {
            CampaignState s = NewCampaign(7105);
            MakeBlueprints(s);
            int mo = Spawn(FoundryOutpostLayout.RegionId, BpOverload, new Vector2(-2f, -20f), 400f);
            int mc = Spawn(FoundryOutpostLayout.RegionId, BpCannon, new Vector2(2f, -20f), 400f);
            int mr = Spawn(FoundryOutpostLayout.RegionId, BpGun, new Vector2(6f, -20f), 400f);
            FoundryOutpostController fo = OpenFoundry(s, mo, mc, mr);
            CombatSite site = fo.Combat;
            string left = FoundryOutpostLayout.ArmorBotLeftSpawnId;
            Expect(site != null && site.EnemyIds.Contains(left) && site.EnemyIds.Contains(FoundryOutpostLayout.StriderSpawnId)
                   && site.EnemyIds.Contains(FoundryOutpostLayout.RepairBotSpawnId),
                $"铸造前哨外围进场：护甲机 ×2、步进炮、维修机进战斗内核（{string.Join(",", site?.EnemyIds ?? Array.Empty<string>())}）");

            // 护甲机正面 40% 减伤；熔穿过载穿甲 30%；普通重炮不穿甲。机器站在左护甲机正面 6 米处开火（直控入口，两段式）。
            RegionEnemyRecord armor = Enemy(s, left);
            Vector2 facing = FoundryOutpostRegion.ArmorFacingOf(armor);
            Vector2 front = armor.Position + facing * 6f;
            Place(site, mo, front);
            Place(site, mc, front + new Vector2(0.5f, 0f));
            Place(site, mr, new Vector2(30f, -20f));
            float o = FoundryShot(s, mo, left);
            float c = FoundryShot(s, mc, left);
            site.TryGetMachineUnit(mo, out int mou);
            site.Kernel.TryGetUnit(mou, out CombatUnitView mov);
            Expect(Mathf.Abs(o - 55f * (1f - (FoundryOutpostLayout.ArmorBotFrontalReductionPct - FracturedCityLayout.OverloadArmorPierceBonus))) < 0.05f
                   && Mathf.Abs(c - 55f * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct)) < 0.05f,
                $"熔穿过载：正面打护甲机 {o:F1}（55 ×（1 − (0.4 − 0.3)））；普通重炮 {c:F1}（55 × 0.6）；实时位置判定正面（Demo 编队攻击此前读存档时才同步的记录位置）");

            // 护甲机反击：9 米内有机器 → 10 伤害 / 1.6 秒。
            float hpMo = Hp(mo);
            site.TryGetEnemyUnit(left, out int lu);
            site.Kernel.SetCycle(lu, 0.05f, 0f);
            WorldSimulation.StepMany(6);
            Expect(hpMo - Hp(mo) >= FoundryOutpostLayout.ArmorBotAttackDamage - 0.01f,
                $"护甲机驻守开火：6 米内的机器挨了 {hpMo - Hp(mo):F0}（Demo {FoundryOutpostLayout.ArmorBotAttackDamage}）");

            // 步进炮：发现目标先 1 秒瞄准线，到点重新取射程内最近目标开火（20 伤害）；目标躲出射程则落空。
            site.TryGetEnemyUnit(FoundryOutpostLayout.StriderSpawnId, out int su);
            Place(site, mr, FoundryOutpostLayout.StriderSpawn.Position + new Vector2(0f, -8f));
            Place(site, mo, new Vector2(-30f, -20f));
            Place(site, mc, new Vector2(-32f, -20f));
            site.Kernel.SetCycle(su, 0.02f, 0f);
            float hpR = Hp(mr);
            WorldSimulation.StepMany(30); // 瞄准中
            float midAim = Hp(mr);
            WorldSimulation.StepMany(40); // 1 秒到点开火
            float afterFire = Hp(mr);
            Expect(Mathf.Approximately(midAim, hpR) && Mathf.Approximately(hpR - afterFire, FoundryOutpostLayout.StriderAttackDamage),
                $"步进炮：瞄准线期间不伤害（{hpR - midAim:F0}），1 秒后开火 {hpR - afterFire:F0}（Demo {FoundryOutpostLayout.StriderAttackDamage}）");
            site.Kernel.SetCycle(su, 0.02f, 0f);
            WorldSimulation.StepMany(10);
            Place(site, mr, new Vector2(40f, -20f)); // 瞄准中躲出射程
            float beforeDodge = Hp(mr);
            WorldSimulation.StepMany(70);
            Expect(Mathf.Approximately(Hp(mr), beforeDodge), "步进炮瞄准中目标躲出 14 米射程：本次落空、不伤害");

            // 维修机：没有威胁时走向血量百分比最低的友军，进入 6 米按 3 秒周期治疗 15。
            RegionEnemyRecord right = Enemy(s, FoundryOutpostLayout.ArmorBotRightSpawnId);
            FoundryOutpostRegion.TryDamageEnemy(s, right.EnemyInstanceId, 60f);
            float hurt = right.Health;
            int guard = 0;
            while (right.Health <= hurt && guard++ < 60 * 20)
            {
                WorldSimulation.StepMany(1);
            }
            site.TryGetEnemyPosition(FoundryOutpostLayout.RepairBotSpawnId, out Vector2 rb);
            Expect(Mathf.Approximately(right.Health - hurt, FoundryOutpostLayout.RepairBotHealAmount) && Vector2.Distance(rb, right.Position) <= FoundryOutpostLayout.RepairBotHealRange + 0.01f,
                $"维修机：走到血量最低的友军 {FoundryOutpostLayout.RepairBotHealRange} 米内（{Vector2.Distance(rb, right.Position):F1} 米）治疗 {right.Health - hurt:F0}（{guard} 步）");

            // 耐热反制：锁定 HeatResistant 后重新进场，熔穿过载的穿甲被抵消。
            WorldSimulation.UnloadAll();
            CampaignState s2 = NewCampaign(7106);
            MakeBlueprints(s2);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s2);
            FoundryOutpostRegion.Find(s2).AdaptationId = AdaptationCatalog.HeatResistant;
            int mo2 = Spawn(FoundryOutpostLayout.RegionId, BpOverload, new Vector2(-2f, -20f), 400f);
            FoundryOutpostController fo2 = OpenFoundry(s2, mo2);
            RegionEnemyRecord armor2 = Enemy(s2, left);
            Place(fo2.Combat, mo2, armor2.Position + FoundryOutpostRegion.ArmorFacingOf(armor2) * 6f);
            float hr = FoundryShot(s2, mo2, left);
            Expect(Mathf.Abs(hr - 55f * (1f - FoundryOutpostLayout.ArmorBotFrontalReductionPct)) < 0.05f,
                $"耐热反制：熔穿过载打耐热护甲机只剩 {hr:F1}（穿甲被抵消，基础伤害不变）");
        }

        private static float FoundryShot(CampaignState s, int attacker, string target)
        {
            RegionEnemyRecord e = Enemy(s, target);
            float before = e.Health;
            FoundryOutpostRegion.TryAttackEnemy(s, attacker, target, s.RandomSeed, isAiSource: false, attackerPosition: Vector2.zero);
            WorldSimulation.StepMany(61);
            float mid = e.Health;
            FoundryOutpostRegion.TryAttackEnemy(s, attacker, target, s.RandomSeed, isAiSource: false, attackerPosition: Vector2.zero);
            float after = e.Health;
            // 瞄准的这 1 秒里，维修机可能给它回血——只取开火那一下的差值。
            return mid - after;
        }

        private static void CheckDemoBoss()
        {
            CampaignState s = NewCampaign(7107);
            MakeBlueprints(s);
            int mo = Spawn(FoundryOutpostLayout.RegionId, BpOverload, new Vector2(-2f, -20f), 2000f);
            int mg = Spawn(FoundryOutpostLayout.RegionId, BpGun, new Vector2(2f, -20f), 2000f);
            UnlockCoreGate(s, mo);
            FoundryOutpostController fo = OpenFoundry(s, mo, mg);
            CombatSite site = fo.Combat;
            RegionRecord region = FoundryOutpostRegion.Find(s);
            // 越过核心分区封锁线：步末存安全档 + 首领初始化（下一步），内核随记录对账出三个首领单位。
            Place(site, mg, new Vector2(4f, 36f));
            Place(site, mo, new Vector2(-30f, -20f));
            WorldSimulation.StepMany(3);
            bool init = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Shielded;
            bool unitsIn = site.TryGetEnemyUnit(FoundryOutpostLayout.CoreNode1Id, out int n1) && site.TryGetEnemyUnit(FoundryOutpostLayout.CoreNode2Id, out int n2)
                           && site.TryGetEnemyUnit(FoundryOutpostLayout.MainCoreId, out int coreUnit);
            site.TryGetEnemyUnit(FoundryOutpostLayout.MainCoreId, out coreUnit);
            FracturedCityRegion.ActionResult shieldedHit = FoundryOutpostRegionShim(s, mg, FoundryOutpostLayout.MainCoreId);
            Expect(init && unitsIn && !shieldedHit.Success && shieldedHit.FailureReason.Contains(FoundryOutpostCoreBoss.DisplayPhaseText(region)),
                $"首领：越线后初始化为护盾阶段、三个首领单位进内核；护盾阶段打主核心被拒（{shieldedHit.FailureReason}）");

            // 护盾阶段主核心不开火。
            float hpBefore = Hp(mg);
            WorldSimulation.StepMany(180);
            float shieldedDamage = hpBefore - Hp(mg);
            // 两个供能节点摧毁 → 阶段一：主核心可伤、16 米内视线通的机器每 2 秒挨 18。
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode1Id, 9999f);
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.CoreNode2Id, 9999f);
            bool phase1 = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Phase1;
            WorldSimulation.StepMany(1);
            float p1Start = Hp(mg);
            WorldSimulation.StepMany(60 * 4 + 2);
            float p1Damage = p1Start - Hp(mg);
            Expect(Mathf.Approximately(shieldedDamage, 0f) && phase1 && (p1Damage == 2 * FoundryOutpostLayout.CoreAttackDamage || p1Damage == 3 * FoundryOutpostLayout.CoreAttackDamage),
                $"护盾阶段主核心不开火（3 秒损 {shieldedDamage:F0}）；两节点毁后阶段一，主核心 4 秒命中 {p1Damage:F0}（每 2 秒 {FoundryOutpostLayout.CoreAttackDamage}）");

            // 主核心掉到 60% 以下 → 过渡：不开火、不可伤、召唤一台维修机（进内核）；5 秒后阶段二。
            RegionEnemyRecord core = Enemy(s, FoundryOutpostLayout.MainCoreId);
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, core.Health - core.MaxHealth * 0.5f);
            bool transition = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Transition;
            WorldSimulation.StepMany(2);
            bool summonIn = site.TryGetEnemyUnit(FoundryOutpostLayout.CoreRepairBotSummonId, out _);
            float tStart = Hp(mg);
            WorldSimulation.StepMany(60 * 3);
            float transitionDamage = tStart - Hp(mg);
            WorldSimulation.StepMany(60 * 3);
            bool phase2 = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Phase2;
            Expect(transition && summonIn && Mathf.Approximately(transitionDamage, 0f) && phase2,
                $"过渡阶段：召唤的维修机进内核（{summonIn}）、主核心 3 秒不开火（{transitionDamage:F0}）；5 秒后进入阶段二（{phase2}）");

            // 阶段二侧后 +20%：同一把枪从正面 / 背面各打一发。
            float d = WeaponDamage(s, mg);
            Vector2 corePos = core.Position;
            Place(site, mg, corePos + FoundryOutpostLayout.MainCoreFacing * 5f);
            float h0 = core.Health;
            FoundryOutpostRegionShim(s, mg, core.EnemyInstanceId);
            float frontDmg = h0 - core.Health;
            Place(site, mg, corePos - FoundryOutpostLayout.MainCoreFacing * 5f);
            float h1 = core.Health;
            FoundryOutpostRegionShim(s, mg, core.EnemyInstanceId);
            float backDmg = h1 - core.Health;
            Expect(Mathf.Abs(frontDmg - d) < 0.01f && Mathf.Abs(backDmg - d * (1f + FoundryOutpostLayout.Phase2BackHitBonusPct)) < 0.01f,
                $"阶段二弱点：正面 {frontDmg:F2}、侧后 {backDmg:F2}（= 正面 × {1f + FoundryOutpostLayout.Phase2BackHitBonusPct}）");

            // 摧毁主核心 → 已摧毁 + 核心数据盒掉落一次。
            FoundryOutpostRegion.TryDamageEnemy(s, core.EnemyInstanceId, 99999f);
            bool destroyed = FoundryOutpostCoreBoss.GetState(region) == CoreBossState.Destroyed;
            bool dataDrop = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Count(q => q.ContentId == FoundryOutpostLayout.CoreDataContentId) == 1;
            WorldSimulation.StepMany(2);
            bool coreDeadInKernel = !site.IsEnemyAlive(core.EnemyInstanceId);
            Expect(destroyed && dataDrop && coreDeadInKernel, $"主核心摧毁：阶段 = 已摧毁，核心数据盒掉落 1 个，内核镜像同步为阵亡（{coreDeadInKernel}）");
        }

        private static FracturedCityRegion.ActionResult FoundryOutpostRegionShim(CampaignState s, int attacker, string target)
        {
            FoundryOutpostRegion.ActionResult r = FoundryOutpostRegion.TryAttackEnemy(s, attacker, target, s.RandomSeed, isAiSource: false, attackerPosition: Vector2.zero);
            return r.Success ? FracturedCityRegion.ActionResult.Ok() : FracturedCityRegion.ActionResult.Fail(r.FailureReason);
        }

        private static void CheckHomeAutoEngage()
        {
            CampaignState s = NewCampaign(7108);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            MachineRecord gunner = MachineRegistry.AllRecords.FirstOrDefault(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId);
            int logic = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                HomeValleyLayout.LowThreatTargetPosition + new Vector2(20f, 0f), 120f, 120f, "Player", 1).LogicId;
            MachineLoadoutRegistry.Register(s, logic, HomeValleyLayout.BlueprintErc003Id, 1);
            WorldSimulation.StepMany(2);
            _ = gunner;
            bool inKernel = home.Combat.TryGetMachineUnit(logic, out _);
            int before = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == logic);
            WorldSimulation.StepMany(60 * 3);
            int farAttacks = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == logic) - before;
            Place(home.Combat, logic, HomeValleyLayout.LowThreatTargetPosition + new Vector2(6f, 0f));
            WorldSimulation.StepMany(60 * 11);
            int nearAttacks = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == logic) - before - farAttacks;
            Expect(inKernel && farAttacks == 0 && nearAttacks >= 2 && nearAttacks <= 3,
                $"家园训练靶自动交战：射程外 3 秒不尝试（{farAttacks}），进入 {HomeValleyCombatTargets.EngageRange} 米后按 5 秒间隔尝试（11 秒 {nearAttacks} 次；打空后等再生），结算走同一出口");

            // 厂内机器（仍占用工厂出口）：不参与自动交战、也不消耗交战冷却（Demo TickAutoEngage 对厂内机器直接跳过）；驶出工厂后冷却已就绪，下一步就尝试。
            MachineRegistry.TryGetRecord(logic, out MachineRecord rec);
            rec.IsInFactory = true;
            CombatSites.SyncFactoryState(rec);
            home.Combat.TryGetMachineUnit(logic, out int unit);
            bool holdFlag = home.Combat.UnitHasFlag(unit, CombatUnitFlags.EngageHold);
            HomeValleyCombatTargets.ResetSessionState();
            WorldSimulation.StepMany(60 * 11);
            int heldAttempts = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == logic);
            home.Combat.Kernel.TryGetUnit(unit, out CombatUnitView held);
            HomeValleyFactory.ReleaseFromFactory(logic);
            bool released = !home.Combat.UnitHasFlag(unit, CombatUnitFlags.EngageHold);
            HomeValleyCombatTargets.ResetSessionState();
            WorldSimulation.StepMany(2);
            int releasedAttempts = HomeValleyCombatTargets.RecentEvents.Count(e => e.IsAiSource && e.AttackerLogicId == logic);
            home.Combat.Kernel.TryGetUnit(unit, out CombatUnitView after);
            Expect(holdFlag && heldAttempts == 0 && held.Cycle <= 0f && released && releasedAttempts == 1 && after.Cycle > 4.9f,
                $"厂内机器不自动交战、不消耗冷却：占用出口 11 秒尝试 {heldAttempts} 次、冷却 {held.Cycle:F2} 秒（未消耗）；驶出工厂后 2 步内尝试 {releasedAttempts} 次、冷却重置为 {after.Cycle:F2} 秒");
        }

        // ── D 世界集成 ───────────────────────────────────────────────────────────

        /// <summary>破碎都市战斗存档：两台机器（一台攻击远处的侦察机、一台移动）+ 一台守备，家园同时载入。</summary>
        private static void BuildCombatSave(int seed)
        {
            CampaignState s = NewCampaign(seed);
            MakeBlueprints(s);
            WorldSimulation.LoadHome(resume: false);
            int a = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            int b = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(2f, -24f));
            int c = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(6f, -24f));
            FracturedCityController city = OpenFracturedCity(s, a, b, c);
            WorldView.Observe(HomeValleyLayout.RegionId);
            city.SquadCommands.DebugSelectMany(new[] { a });
            city.SquadCommands.IssueAttack(FracturedCityLayout.Scout2SpawnId, paused: false);
            city.SquadCommands.DebugSelectMany(new[] { b });
            city.SquadCommands.IssueMoveTo(new Vector2(-12f, -12f), paused: false);
            city.SquadCommands.DebugSelectMany(new[] { c });
            city.SquadCommands.IssueGuardHere(paused: false);
            city.SquadCommands.ClearSelection();
            WorldSimulation.StepMany(30);
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("战斗存档写入失败：" + r.Message);
            }
        }

        private static void RestoreSave(int fromSlot, bool corrupt = false, bool dropDomain = false, bool loadCity = true)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读战斗存档失败：" + r.Message);
                return;
            }
            if (corrupt)
            {
                foreach (CombatSiteRecord rec in r.State.Combat.Sites)
                {
                    if (rec.SiteId == FracturedCityLayout.RegionId)
                    {
                        char[] chars = rec.Payload.ToCharArray();
                        chars[chars.Length / 2] = chars[chars.Length / 2] == 'A' ? 'B' : 'A';
                        rec.Payload = new string(chars);
                    }
                }
            }
            if (dropDomain)
            {
                r.State.Combat = null;
                CampaignFgStateDomains.EnsureAll(r.State);
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            if (loadCity)
            {
                int[] ids = r.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
                WorldSimulation.LoadFracturedCity(ids, resume: true);
            }
            WorldView.Observe(HomeValleyLayout.RegionId);
        }

        private static ulong SiteHash(string siteId) => CombatSites.Get(siteId)?.Kernel.StateHash() ?? 0;

        private static string RecordDigest()
        {
            CampaignState s = CampaignSession.Current;
            var sb = new StringBuilder();
            foreach (MachineRecord m in MachineRegistry.AllRecords.OrderBy(m => m.LogicId))
            {
                sb.Append($"{m.LogicId}:{m.RegionId}:{m.IsAlive}:{m.Health:F3};");
            }
            foreach (RegionEnemyRecord e in (s?.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).OrderBy(e => e.EnemyInstanceId, StringComparer.Ordinal))
            {
                sb.Append($"{e.EnemyInstanceId}:{e.IsAlive}:{e.Health:F3};");
            }
            return sb.ToString();
        }

        private static void CheckSaveLoadCommandsContinue()
        {
            BuildCombatSave(7201);
            ulong savedHash = SiteHash(FracturedCityLayout.RegionId);
            // 对照：读档后连续跑 600 步。
            RestoreSave(Slot);
            ulong restoredHash = SiteHash(FracturedCityLayout.RegionId);
            FracturedCityController city = WorldSimulation.FracturedCity;
            int[] ids = MachineRegistry.AllRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId).OrderBy(m => m.LogicId).Select(m => m.LogicId).ToArray();
            bool attack = city.SquadCommands.TryGetActiveCommandKind(ids[0], out RegionCommandKind k0) && k0 == RegionCommandKind.Attack;
            bool move = city.SquadCommands.TryGetActiveCommandKind(ids[1], out RegionCommandKind k1) && k1 == RegionCommandKind.Move;
            bool guard = city.SquadCommands.TryGetActiveCommandKind(ids[2], out RegionCommandKind k2) && k2 == RegionCommandKind.Guard;
            Vector2 before = city.Combat.TryGetMachinePosition(ids[1], out Vector2 pb) ? pb : Vector2.zero;
            WorldSimulation.StepMany(600);
            ulong straight = SiteHash(FracturedCityLayout.RegionId);
            string straightRecords = RecordDigest();
            Vector2 after = city.Combat.TryGetMachinePosition(ids[1], out Vector2 pa) ? pa : Vector2.zero;
            Expect(savedHash == restoredHash && attack && move && guard && Vector2.Distance(before, after) > 3f,
                $"FG-GAP-018：读档后内核状态逐位一致（存档时 {savedHash:X16} / 读回 {restoredHash:X16}；攻击 {attack}、移动 {move}、守备 {guard}），攻击 / 移动 / 守备三条编队命令都还在执行（移动的那台又走了 {Vector2.Distance(before, after):F1} 米）");
            // 中途再存再读一次，与连续跑的结果一致。
            RestoreSave(Slot);
            WorldSimulation.StepMany(300);
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot + 2, SaveReason.Manual);
            RestoreSave(Slot + 2);
            WorldSimulation.StepMany(300);
            ulong twice = SiteHash(FracturedCityLayout.RegionId);
            Expect(twice == straight && RecordDigest() == straightRecords,
                $"第 300 步存档再读档接着跑 300 步 = 不存档连续跑 600 步（内核哈希 {twice:X16}，机器 / 敌人记录一致）");
        }

        private static void CheckObservationIndependence()
        {
            BuildCombatSave(7202);
            RestoreSave(Slot);
            // A：镜头在破碎都市（表现对象、插值、实例化绘制都在跑），逐帧推进，其间来回飞跃。
            var reader = new SilentReader();
            InputRouter.DebugSetReader(reader);
            long target = GameClock.Ticks + 900;
            int frame = 0;
            while (GameClock.Ticks < target && frame < 5000)
            {
                if (frame % 120 == 0)
                {
                    WorldView.Observe(frame % 240 == 0 ? FracturedCityLayout.RegionId : HomeValleyLayout.RegionId);
                }
                WorldSimulation.Frame(1f / 60f, target);
                frame++;
            }
            WorldView.Observe(FracturedCityLayout.RegionId);
            bool viewsWhenObserved = Object.FindObjectsByType<MachineView>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(v => v.Marker != null && v.Marker.Site == WorldSimulation.FracturedCity.Combat) == WorldSimulation.FracturedCity.LiveMachineCount;
            WorldView.Observe(HomeValleyLayout.RegionId);
            bool noViewsWhenAway = GameObject.Find("[FracturedCityRoot]") == null
                                   && Object.FindObjectsByType<MachineView>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                       .All(v => v.Marker == null || v.Marker.Site != WorldSimulation.FracturedCity.Combat);
            ulong observed = SiteHash(FracturedCityLayout.RegionId);
            string observedRecords = RecordDigest();
            InputRouter.DebugSetReader(null);
            // B：从不观察破碎都市，无头推进同样的步数。
            RestoreSave(Slot);
            WorldSimulation.StepMany(900);
            ulong headless = SiteHash(FracturedCityLayout.RegionId);
            Expect(observed == headless && observedRecords == RecordDigest() && viewsWhenObserved && noViewsWhenAway,
                $"观察不改变结果（FGR-BASE-021）：镜头飞跃 {frame / 120} 次、逐帧渲染的一遍与从不观察的无头一遍，内核哈希一致（{observed:X16}）、记录一致；" +
                "被观察时每台机器都有表现对象，离开后表现对象全部销毁（DEBT-FG0ARCH01-03）");
        }

        private static void CheckPauseSpeedMatrix()
        {
            BuildCombatSave(7203);
            var hashes = new List<ulong>();
            var speeds = new[] { 0.5f, 1f, 2f, 3f };
            foreach (float speed in speeds)
            {
                RestoreSave(Slot);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + 60 * 30;
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                hashes.Add(SiteHash(FracturedCityLayout.RegionId));
                GameClock.SetSpeed(1f);
            }
            RestoreSave(Slot);
            ulong before = SiteHash(FracturedCityLayout.RegionId);
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            ulong paused = SiteHash(FracturedCityLayout.RegionId);
            GameClock.SetPaused(false);
            Expect(hashes.Distinct().Count() == 1 && before == paused,
                $"0.5x / 1x / 2x / 3x 跑同样 30 游戏秒，内核哈希一致（{hashes[0]:X16}）；暂停 2 秒真实时间内核不走步");
        }

        private static void CheckRegionSwitch()
        {
            CampaignState s = NewCampaign(7204);
            int kernelsBefore = CombatKernel.LiveKernels; // NewCampaign 已卸载整个世界：此刻只剩自检自己在用的内核（应为 0）
            MakeBlueprints(s);
            WorldSimulation.LoadHome(resume: false);
            int homeKernels = CombatKernel.LiveKernels - kernelsBefore;
            int m = Spawn(HomeValleyLayout.RegionId, BpOverload, HomeValleyLayout.Core.Position + new Vector2(4f, -4f));
            MachineLoadoutRegistry.Register(s, m, BpOverload, 1);
            WorldSimulation.StepMany(2);
            // 家园里给它 70 热量，派遣到破碎都市：热量随机器走。
            WorldSimulation.Home.Combat.TryGetMachineUnit(m, out int hu);
            WorldSimulation.Home.Combat.Kernel.SetWeaponState(hu, 70f, false, 0, 0);
            CombatSites.ExportMachine(m);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            // 与出发事务同一顺序：先导出、再改记录区域、再载入远征地点。
            MachineRegistry.TryGetRecord(m, out MachineRecord rec);
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { m }, resume: false);
            int both = CombatKernel.LiveKernels - kernelsBefore;
            city.Combat.TryGetMachineUnit(m, out int cu);
            city.Combat.Kernel.TryGetUnit(cu, out CombatUnitView cv);
            WorldSimulation.StepMany(1);
            bool leftHome = !WorldSimulation.Home.Combat.TryGetMachineUnit(m, out _);
            Expect(homeKernels == 1 && both == 2 && Mathf.Abs(cv.Heat - 70f) < 0.01f && leftHome && rec.RegionId == FracturedCityLayout.RegionId,
                $"区域切换：家园 1 个内核，派遣后 2 个；机器热量随机器走（远征内核里 {cv.Heat:F1}），下一步家园内核移除它（{leftHome}）");
            // 撤离回家：远征内核释放、记录写回；家园下一步把它建回来。
            city.Exit(evacuateSuccess: true);
            int afterEvac = CombatKernel.LiveKernels - kernelsBefore;
            WorldSimulation.StepMany(1);
            bool backHome = WorldSimulation.Home.Combat.TryGetMachineUnit(m, out int hu2);
            WorldSimulation.Home.Combat.Kernel.TryGetUnit(hu2, out CombatUnitView hv);
            WorldSimulation.UnloadAll();
            int afterUnload = CombatKernel.LiveKernels - kernelsBefore;
            Expect(afterEvac == 1 && backHome && hv.Heat > 60f && hv.Heat <= 70f && afterUnload == 0,
                $"撤离：远征内核释放（剩 {afterEvac} 个），机器回到家园内核（热量 {hv.Heat:F1}，按时间散过热）；整个世界卸载后 0 个内核（不泄漏原生容器）");
        }

        // ── 名册版本号（DEBT-FG0ARCH03-06 收口）：热更层的名册对账每步 O(1)，名册变化后与整套重算一致 ──

        private static void CheckRosterRevisionGating()
        {
            // 家园句柄对账：名册不变的 600 步一次都不扫；出厂（登记）/ 派遣（换地点）/ 阵亡后下一步与记录一致。
            CampaignState s = NewCampaign(7206);
            MakeBlueprints(s);
            Vector2 core = HomeValleyLayout.Core.Position;
            int h1 = Spawn(HomeValleyLayout.RegionId, BpGun, core + new Vector2(3f, -3f));
            int h2 = Spawn(HomeValleyLayout.RegionId, BpGun, core + new Vector2(-3f, -3f));
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldSimulation.StepMany(2);
            int syncs0 = home.RosterSyncCount;
            int rev0 = MachineRegistry.RosterRevision;
            WorldSimulation.StepMany(600);
            int idleSyncs = home.RosterSyncCount - syncs0;
            bool idleRevStable = MachineRegistry.RosterRevision == rev0;
            int h3 = Spawn(HomeValleyLayout.RegionId, BpGun, core + new Vector2(0f, -5f));
            WorldSimulation.StepMany(1);
            bool created = home.Combat.TryGetMachineUnit(h3, out _);
            MachineRegistry.TryGetRecord(h2, out MachineRecord r2);
            CombatSites.ExportMachine(h2);
            MachineRegistry.MoveToRegion(r2, FracturedCityLayout.RegionId);
            WorldSimulation.StepMany(1);
            bool moved = !home.Combat.TryGetMachineUnit(h2, out _);
            MachineRegistry.MarkDeadByLogicId(h1);
            WorldSimulation.StepMany(1);
            int changeSyncs = home.RosterSyncCount - syncs0 - idleSyncs;
            int[] expected = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId)
                .Select(m => m.LogicId).OrderBy(x => x).ToArray();
            int[] actual = home.Combat.MachineLogicIds
                .Where(id => home.Combat.TryGetMachineUnit(id, out int u) && home.Combat.Kernel.IsAlive(u)).OrderBy(x => x).ToArray();
            Expect(idleSyncs == 0 && idleRevStable && created && moved && changeSyncs == 3 && expected.SequenceEqual(actual) && home.LiveMachineCount == expected.Length,
                $"家园名册对账按版本号：名册不变的 600 步对账 {idleSyncs} 次（O(1)）；出厂 / 派遣 / 阵亡各触发 1 次（共 {changeSyncs}），" +
                $"之后内核里的存活机器 [{string.Join(",", actual)}] = 记录里在家园的存活机器 [{string.Join(",", expected)}]");

            // 铸造前哨核心门三灯：输入不变不重算；解锁（解析 + 充能）后亮、唯一的熔穿过载机阵亡后灭——每一步都与整套重算一致。
            CampaignState f = NewCampaign(7207);
            MakeBlueprints(f);
            int mo = Spawn(FoundryOutpostLayout.RegionId, BpOverload, new Vector2(-2f, -20f), 400f);
            int mg = Spawn(FoundryOutpostLayout.RegionId, BpGun, new Vector2(2f, -20f), 400f);
            FoundryOutpostController fo = OpenFoundry(f, mo, mg);
            RegionRecord region = FoundryOutpostRegion.Find(f);
            int mismatch = 0;
            void StepCompare(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    WorldSimulation.StepMany(1);
                    if (region.CoreGateUnlocked != FoundryOutpostRegion.ComputeCoreGateLights(f).AllReady)
                    {
                        mismatch++;
                    }
                }
            }
            StepCompare(2);
            int g0 = fo.CoreGateRecomputeCount;
            StepCompare(300);
            int idleGate = fo.CoreGateRecomputeCount - g0;
            bool lockedAtFirst = !region.CoreGateUnlocked;
            UnlockCoreGate(f, mo);
            StepCompare(3);
            bool unlocked = region.CoreGateUnlocked;
            MachineRegistry.MarkDeadByLogicId(mo);
            StepCompare(3);
            bool relocked = !region.CoreGateUnlocked;
            int gateRecomputes = fo.CoreGateRecomputeCount - g0;
            Expect(idleGate == 0 && lockedAtFirst && unlocked && relocked && mismatch == 0 && gateRecomputes >= 2 && gateRecomputes <= 4,
                $"核心门三灯按输入变化重算：输入不变的 300 步重算 {idleGate} 次；解析 + 充能后亮（{unlocked}）、唯一的熔穿过载机阵亡后灭（{relocked}），" +
                $"共重算 {gateRecomputes} 次，308 步里与整套重算不一致 {mismatch} 步");

            // 敌人记录索引：记录数组整体替换（重排）并原位换掉一个元素后，内核的受伤事件仍写到当前那个记录对象上。
            CampaignState c = NewCampaign(7208);
            MakeBlueprints(c);
            int gm = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            FracturedCityController city = OpenFracturedCity(c, gm);
            WorldSimulation.StepMany(1);
            string target = FracturedCityLayout.Scout1SpawnId;
            PlaceEnemy(city.Combat, target, new Vector2(-6f, -18f));
            FracturedCityRegion.ActionResult warm = FracturedCityRegion.TryAttackEnemy(c, gm, target, c.RandomSeed, isAiSource: false);
            c.RegionEnemies = Enumerable.Reverse(c.RegionEnemies).ToArray();
            RegionEnemyRecord before = Enemy(c, target);
            int idx = Array.IndexOf(c.RegionEnemies, before);
            RegionEnemyRecord replaced = JsonUtility.FromJson<RegionEnemyRecord>(JsonUtility.ToJson(before));
            c.RegionEnemies[idx] = replaced;
            float oldHp = before.Health;
            float newHp0 = replaced.Health;
            FracturedCityRegion.ActionResult hit = FracturedCityRegion.TryAttackEnemy(c, gm, target, c.RandomSeed, isAiSource: false);
            float d = WeaponDamage(c, gm);
            Expect(warm.Success && hit.Success && d > 0f && Mathf.Abs(newHp0 - replaced.Health - d) < 0.01f && Mathf.Approximately(before.Health, oldHp),
                $"敌人记录索引：记录数组重排 + 原位替换后，受伤事件写到当前记录（{newHp0:F1}→{replaced.Health:F1}，每发 {d:F1}），被换下的旧对象不变（{before.Health:F1}）");
        }

        private static void CheckCorruptSnapshotRebuild()
        {
            BuildCombatSave(7205);
            WorldSimulation.SyncAllForSave();
            var machinePos = MachineRegistry.AllRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId).ToDictionary(m => m.LogicId, m => m.WorldPosition);
            int historyBefore = NotificationCenter.History.Count;
            RestoreSave(Slot, corrupt: true);
            FracturedCityController city = WorldSimulation.FracturedCity;
            bool rebuilt = city != null && city.IsLoaded && city.Combat.MachineCount == machinePos.Count;
            bool posKept = machinePos.All(kv => city.Combat.TryGetMachinePosition(kv.Key, out Vector2 p) && Vector2.Distance(p, kv.Value) < 1e-3f);
            bool notified = NotificationCenter.History.Skip(Math.Max(0, historyBefore - 1)).Any(e => e.Latest != null && e.Latest.DetailText.Contains(GameText.Get("combat.site.fractured_city")));
            WorldSimulation.StepMany(60);
            RestoreSave(Slot, dropDomain: true);
            bool oldSaveOk = WorldSimulation.FracturedCity != null && WorldSimulation.FracturedCity.Combat.MachineCount == machinePos.Count;
            Expect(rebuilt && posKept && notified && oldSaveOk,
                "战斗快照损坏：通知玩家（“读不了，已按机器与敌人记录恢复”），按记录重建（位置保留）、能继续跑；没有战斗域的旧档同样按记录重建");
        }

        private static void CheckRaidSeedIndependence()
        {
            var results = new List<string>();
            var arrivals = new List<Vector2>();
            bool allOk = true;
            bool placeholderMarked = true;
            foreach (int seed in new[] { 11, 4242, 90001 })
            {
                CampaignState s = NewCampaign(seed);
                HomeValleyController home = WorldSimulation.LoadHome(resume: false);
                TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 12, out string fail);
                if (raid == null)
                {
                    allOk = false;
                    results.Add($"种子 {seed} 派不出突袭（{fail}）");
                    continue;
                }
                // 测试捷径：把行进中的队伍直接推到到达（行进本身由 FG0-ARCH-01 验证），展开成内核单位（FG6-DEF-05 的到达接口）。
                Vector2 core = HomeValleyLayout.Core.Position;
                var arrival = new Vector2((float)raid.TargetX, (float)raid.TargetY) + (new Vector2((float)(raid.PosX - raid.TargetX), (float)(raid.PosY - raid.TargetY))).normalized * 40f;
                arrivals.Add(arrival);
                CombatBench.Spec spec = CombatBench.FromTuning();
                List<int> raiders = CombatBench.SpawnRaidGroup(home.Combat, arrival, core, raid.UnitCount, spec);
                List<int> turrets = CombatBench.SpawnTurretRing(home.Combat, core, 10f, 8, spec);
                placeholderMarked &= home.Combat.PlaceholderMarked; // B22：原型单位生成即在调试层标记占位（combat.placeholder）
                double startDist = raiders.Average(id => home.Combat.Kernel.TryGetPosition(id, out double2 p) ? math.distance(p, new double2(core.x, core.y)) : 0);
                WorldSimulation.StepMany(60 * 25);
                int alive = raiders.Count(id => home.Combat.Kernel.IsAlive(id));
                double endDist = raiders.Where(id => home.Combat.Kernel.IsAlive(id))
                    .Select(id => home.Combat.Kernel.TryGetPosition(id, out double2 p) ? math.distance(p, new double2(core.x, core.y)) : 0).DefaultIfEmpty(0).Average();
                long shots = home.Combat.Kernel.Counters.ProjectilesSpawned;
                bool ok = raiders.Count == raid.UnitCount && shots > 0 && (alive < raiders.Count) && (alive == 0 || endDist < startDist);
                allOk &= ok;
                results.Add($"种子 {seed}：{raid.UnitCount} 个突袭者从 {raid.OriginId} 领地方向到达 ({arrival.x:F0},{arrival.y:F0})，25 秒后存活 {alive}，弹体 {shots} 枚");
                CombatBench.ClearPrototypeUnits(home.Combat);
            }
            // 到达点来自按种子生成的领地方位（不是写死的坐标）：不同种子的到达点至少有两处不同。
            int distinct = 0;
            for (int i = 0; i < arrivals.Count; i++)
            {
                bool unique = true;
                for (int j = 0; j < i; j++)
                {
                    unique &= Vector2.Distance(arrivals[i], arrivals[j]) > 1f;
                }
                distinct += unique ? 1 : 0;
            }
            Expect(allOk && distinct >= 2 && placeholderMarked, $"家园突袭原型不依赖固定坐标（B25，不同到达点 {distinct} 处）、生成即标记占位表现（B22）：" + string.Join("；", results));
        }

        // ── 审查修复第 1 轮：正式存档路径上的大量同时阵亡、观察切换的材质、读档后的工作赶路、桥接边界 ─────────────────

        /// <summary>包一层地点规则，逐个敌人数“阵亡结算”执行了几次（其余挂点原样转交）。</summary>
        private sealed class CountingRules : CombatSiteRules
        {
            private readonly CombatSiteRules _inner;
            public readonly Dictionary<string, int> Killed = new Dictionary<string, int>(StringComparer.Ordinal);

            public CountingRules(CombatSiteRules inner)
            {
                _inner = inner;
            }

            public override void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy)
            {
                Killed[enemy.EnemyInstanceId] = (Killed.TryGetValue(enemy.EnemyInstanceId, out int n) ? n : 0) + 1;
                _inner?.OnEnemyKilled(site, enemy);
            }

            public override void OnEnemyDamaged(CombatSite site, RegionEnemyRecord enemy, float damage) => _inner?.OnEnemyDamaged(site, enemy, damage);
            public override bool ApplyExternalEnemyDamage(CombatSite site, RegionEnemyRecord enemy, float damage) =>
                _inner != null && _inner.ApplyExternalEnemyDamage(site, enemy, damage);
            public override bool ApplyExternalEnemyHeal(CombatSite site, RegionEnemyRecord enemy, float amount) =>
                _inner != null && _inner.ApplyExternalEnemyHeal(site, enemy, amount);
            public override void ApplyExternalDamageByKey(CombatSite site, string key, float damage, int attackerLogicId) =>
                _inner?.ApplyExternalDamageByKey(site, key, damage, attackerLogicId);
            public override void OnMachineMarked(CombatSite site, int logicId, float seconds, RegionEnemyRecord scout) =>
                _inner?.OnMachineMarked(site, logicId, seconds, scout);
            public override void OnMarkCleared(CombatSite site, int logicId, RegionEnemyRecord jammer) => _inner?.OnMarkCleared(site, logicId, jammer);
            public override void OnMarkMissed(CombatSite site, RegionEnemyRecord scout) => _inner?.OnMarkMissed(site, scout);
            public override void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition) =>
                _inner?.OnPoiReached(site, poiIndex, logicId, machinePosition);
            public override void OnEngageRequest(CombatSite site, int logicId) => _inner?.OnEngageRequest(site, logicId);
            public override string InvulnerableDetail(RegionEnemyRecord enemy) => _inner != null ? _inner.InvulnerableDetail(enemy) : string.Empty;
        }

        private static bool SettledOnce(CampaignState s, IEnumerable<string> ids, out int cleared, out int lootOnce, out int dead)
        {
            RegionRecord region = FracturedCityRegion.Find(s);
            string[] destroyed = region?.DestroyedNodeIds ?? Array.Empty<string>();
            GroundItemRecord[] ground = s.GroundItems ?? Array.Empty<GroundItemRecord>();
            cleared = 0;
            lootOnce = 0;
            dead = 0;
            int total = 0;
            foreach (string id in ids)
            {
                total++;
                cleared += destroyed.Contains(id) ? 1 : 0;
                lootOnce += ground.Count(g => g.SalvageInstanceId == id + ":scrap") == 1 ? 1 : 0;
                dead += Enemy(s, id) is RegionEnemyRecord e && !e.IsAlive ? 1 : 0;
            }
            return cleared == total && lootOnce == total && dead == total;
        }

        private static void CheckMassDeathThroughSave()
        {
            // 200 个具名敌人同一步阵亡（一发范围伤害打死一片）：400 条玩法事件，每步只结算 64 条。紧接着在暂停中存档（暂停期间不走步，积压不排空），
            // 走正式存档路径（SaveWithExport → SyncAllForSave → 记录写回 + 内核快照）。之后三条路：接着跑 / 读档接着跑 / 读档后立刻撤离卸载——
            // 每个敌人的阵亡结算（记为已清除、掉落废料、击毁反馈）都恰好一次，不丢不重。
            const int n = 200;
            CampaignState s = NewCampaign(7209);
            MakeBlueprints(s);
            WorldSimulation.LoadHome(resume: false);
            int m = Spawn(FracturedCityLayout.RegionId, BpGun, new Vector2(-2f, -24f));
            FracturedCityController city = OpenFracturedCity(s, m);
            WorldView.Observe(HomeValleyLayout.RegionId);
            CombatSite site = city.Combat;
            var ids = new List<string>(n);
            var extra = new RegionEnemyRecord[n];
            for (int i = 0; i < n; i++)
            {
                string id = $"selfcheck_mass_{i:D3}";
                ids.Add(id);
                extra[i] = new RegionEnemyRecord
                {
                    EnemyInstanceId = id,
                    RegionId = FracturedCityLayout.RegionId,
                    EnemyTypeId = EnemyCatalog.ScoutId,
                    Position = new Vector2(-38f + (i % 20) * 4f, 30f + (i / 20) * 4f),
                    Health = 10f,
                    MaxHealth = 10f,
                    IsAlive = true,
                };
            }
            s.RegionEnemies = s.RegionEnemies.Concat(extra).ToArray();
            CombatDemoContent.ReconcileEnemies(site, s, FracturedCityLayout.RegionId);
            bool allInKernel = ids.All(id => site.TryGetEnemyUnit(id, out _));
            var counting = new CountingRules(site.Rules);
            site.Rules = counting;
            foreach (string id in ids)
            {
                site.TryGetEnemyUnit(id, out int u);
                site.Kernel.Damage(u, 9999f, 0);
            }
            WorldSimulation.StepMany(1);
            int settledBeforeSave = counting.Killed.Count;
            int pendingAtSave = site.PendingGameplayEvents;
            GameClock.SetPaused(true);
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot + 3, SaveReason.Manual);
            int aliveInRecordsAfterSave = ids.Count(id => Enemy(s, id).IsAlive);
            GameClock.SetPaused(false);

            // A：存档后接着跑，直到队列排空。
            int guard = 0;
            while (site.PendingGameplayEvents > 0 && guard++ < 120)
            {
                WorldSimulation.StepMany(1);
            }
            bool exactlyOnce = ids.All(id => counting.Killed.TryGetValue(id, out int c) && c == 1);
            bool settledA = SettledOnce(s, ids, out int clearedA, out int lootA, out int deadA);
            Expect(save.Success && allInKernel && settledBeforeSave > 0 && settledBeforeSave < n && pendingAtSave > 0
                   && aliveInRecordsAfterSave == n - settledBeforeSave && exactlyOnce && counting.Killed.Values.Sum() == n && settledA,
                $"大量同时阵亡 + 暂停存档（正式存档路径）：{n} 个具名敌人同一步阵亡，存档前只结算了 {settledBeforeSave} 个、队列里还有 {pendingAtSave} 条；" +
                $"存档写回后记录里仍“存活”（未结算）{aliveInRecordsAfterSave} 个（不提前翻转）；接着跑 {guard} 步排空后阵亡结算每个恰好 1 次（共 {counting.Killed.Values.Sum()} 次），" +
                $"已清除 {clearedA}、掉落各 1 份 {lootA}、记录阵亡 {deadA}");

            // B：读档接着跑。
            RestoreSave(Slot + 3);
            CampaignState r = CampaignSession.Current;
            CombatSite site2 = WorldSimulation.FracturedCity?.Combat;
            int pendingAfterLoad = site2?.PendingGameplayEvents ?? -1;
            int aliveAfterLoad = ids.Count(id => Enemy(r, id)?.IsAlive == true);
            guard = 0;
            while (site2 != null && site2.PendingGameplayEvents > 0 && guard++ < 120)
            {
                WorldSimulation.StepMany(1);
            }
            bool settledB = SettledOnce(r, ids, out int clearedB, out int lootB, out int deadB);
            Expect(pendingAfterLoad > 0 && aliveAfterLoad == n - settledBeforeSave && settledB,
                $"读档接着跑：积压队列随快照读回（{pendingAfterLoad} 条，存档时 {pendingAtSave} 条），未结算的 {aliveAfterLoad} 个在 {guard} 步内结算完：" +
                $"已清除 {clearedB}/{n}、掉落各 1 份 {lootB}/{n}、记录阵亡 {deadB}/{n}");

            // C：读档后不走步、立刻卸载（撤离 / 暂离 / 回主菜单）：内核释放前先把积压全部结算，不随队列丢掉。
            RestoreSave(Slot + 3);
            CampaignState c3 = CampaignSession.Current;
            int pendingBeforeExit = WorldSimulation.FracturedCity?.Combat?.PendingGameplayEvents ?? -1;
            WorldSimulation.FracturedCity?.Exit(evacuateSuccess: false);
            bool settledC = SettledOnce(c3, ids, out int clearedC, out int lootC, out int deadC);
            Expect(pendingBeforeExit > 0 && settledC,
                $"读档后立刻卸载地点：卸载前积压 {pendingBeforeExit} 条先全部结算（已清除 {clearedC}/{n}、掉落 {lootC}/{n}、记录阵亡 {deadC}/{n}），内核随后释放");
        }

        private static void CheckObservationMaterials()
        {
            // 地点不被观察就销毁表现对象、回来重建（ADR 第 5 节）：材质按颜色共享、与世界成对释放，镜头来回飞不再每次泄漏一批材质。
            BuildCombatSave(7210);
            RestoreSave(Slot);
            for (int i = 0; i < 2; i++)
            {
                WorldView.Observe(FracturedCityLayout.RegionId);
                WorldView.Observe(HomeValleyLayout.RegionId);
            }
            int before = Resources.FindObjectsOfTypeAll<Material>().Length;
            int paletteBefore = ViewMaterials.Count;
            const int flights = 20;
            for (int i = 0; i < flights; i++)
            {
                WorldView.Observe(FracturedCityLayout.RegionId);
                WorldView.Observe(HomeValleyLayout.RegionId);
            }
            int after = Resources.FindObjectsOfTypeAll<Material>().Length;
            int paletteAfter = ViewMaterials.Count;
            WorldSimulation.UnloadAll();
            int paletteUnloaded = ViewMaterials.Count;
            int afterUnload = Resources.FindObjectsOfTypeAll<Material>().Length;
            Expect(paletteBefore > 0 && after <= before && paletteAfter == paletteBefore && paletteUnloaded == 0 && afterUnload <= before - paletteBefore,
                $"观察切换不泄漏材质（FG14 §5-7 成对释放）：家园 ↔ 破碎都市来回 {flights} 次，全部材质 {before}→{after} 个、地点共享材质 {paletteBefore}→{paletteAfter} 份；" +
                $"整个世界卸载后共享材质 {paletteUnloaded} 份（材质总数 {afterUnload}）");
        }

        private static int StepsUntil(Func<bool> done, int max)
        {
            for (int i = 1; i <= max; i++)
            {
                WorldSimulation.StepMany(1);
                if (done())
                {
                    return i;
                }
            }
            return -1;
        }

        private static void CheckWorkArrivalAfterLoad()
        {
            // 工作赶路（派工）的到达回调只在内存里、赶路命令随内核快照进存档：读档后按在办订单重挂，走到了照常开工（不再等 5 秒停滞看门狗）。
            CampaignState s = NewCampaign(7211);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            s.Scrap = 170;
            BuildingRecord warehouse = s.BuildingRecords?.FirstOrDefault(b => b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse);
            if (warehouse != null)
            {
                warehouse.ConstructionState = BuildingConstructionState.Damaged;
            }
            int workerId = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId,
                HomeValleyLayout.Core.Position + new Vector2(6f, -4f), 100f, 100f).LogicId;
            WorldSimulation.StepMany(2);
            MachineRecord worker = MachineRegistry.TryGetRecord(workerId, out MachineRecord w) && HomeValleyWorkOrders.FindActiveOrderForMachine(s, workerId) == null ? w : null;
            HomeValleyWorkOrders.WorkOrderOpResult created = worker != null
                ? HomeValleyWorkOrders.TryCreateRepair(s, HomeValleyLayout.BuildingTypeWarehouse, worker.LogicId)
                : HomeValleyWorkOrders.WorkOrderOpResult.Fail("没有家园搬运机");
            WorkOrderRecord order = created.Success ? HomeValleyWorkOrders.Find(s, created.WorkOrderId) : null;
            if (order == null || !home.Combat.TryGetMachineMarker(worker.LogicId, out HomeValleyMachineMarker marker))
            {
                Fail($"读档后工作赶路：搭不起维修订单（{created.FailureReason}）");
                return;
            }
            string orderId = order.WorkOrderId;
            Vector2 dest = HomeValleyWorkOrders.ResolveWorkPosition(s, order);
            Place(home.Combat, worker.LogicId, dest + new Vector2(18f, 0f));
            marker.CommandMoveTo(new Vector3(dest.x, 1f, dest.y), () => HomeValleyWorkOrders.OnArrivedAtWork(CampaignSession.Current, orderId));
            WorldSimulation.StepMany(30);
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot + 4, SaveReason.Manual);
            bool movingAtSave = marker.IsMoving;
            // A：不存档接着跑。
            int stepsA = StepsUntil(() => HomeValleyWorkOrders.Find(CampaignSession.Current, orderId)?.State == WorkOrderState.InProgress, 900);
            // B：读档接着跑。
            RestoreSave(Slot + 4, loadCity: false);
            HomeValleyController home2 = WorldSimulation.Home;
            bool hooked = home2 != null && home2.LastResumedWorkArrivals == 1 && home2.Combat.TryGetMachineMarker(worker.LogicId, out HomeValleyMachineMarker m2)
                          && m2.IsMoving && m2.PendingArrivalAction != null;
            int stepsB = StepsUntil(() => HomeValleyWorkOrders.Find(CampaignSession.Current, orderId)?.State == WorkOrderState.InProgress, 900);
            Expect(save.Success && movingAtSave && stepsA > 0 && hooked && stepsB > 0 && Math.Abs(stepsA - stepsB) <= 1,
                $"读档后的工作赶路：存档时机器正在赶去修仓库（{movingAtSave}），读档后按在办订单重挂到达回调（{hooked}），" +
                $"走到即开工（读档后第 {stepsB} 步；不存档对照第 {stepsA} 步）");
        }

        private static string CodeOnly(string line)
        {
            int c = line.IndexOf("//", StringComparison.Ordinal);
            return c >= 0 ? line.Substring(0, c) : line;
        }

        private static readonly HashSet<string> NotATypeToken = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "new", "out", "ref", "in", "is", "as", "await", "throw", "case", "else", "using", "yield",
        };

        /// <summary>从第 <paramref name="line"/> 行往上找变量 <paramref name="name"/> 最近的声明，返回声明的类型名（找不到返回 null；var 返回 "var"）。</summary>
        private static string DeclaredTypeAbove(string[] lines, int line, string name)
        {
            var decl = new System.Text.RegularExpressions.Regex(@"\b([A-Za-z_][\w\.<>\[\]]*)\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b\s*(=|;|\)|,|\bin\b)");
            for (int i = line; i >= 0 && i >= line - 400; i--)
            {
                foreach (System.Text.RegularExpressions.Match mt in decl.Matches(CodeOnly(lines[i])))
                {
                    string type = mt.Groups[1].Value;
                    if (!NotATypeToken.Contains(type))
                    {
                        return type;
                    }
                }
            }
            return null;
        }

        private static void CheckBridgeBoundaryScan()
        {
            // FG14 §5 硬约束 3：热更层碰战斗内核的唯一入口是桥接层 Campaign/Combat/（CombatSite / CombatSites / CombatBench）。
            // 扫描全部热更玩法代码：桥接层以外不许出现 CombatKernel 类型，也不许经地点取 .Kernel（正则先用正反例自测，再要求桥接层内确实扫到命中，防止正则写坏后静默通过）。
            string root = Path.Combine(Application.dataPath, "GameScripts", "HotFix", "GameLogic");
            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            var direct = new System.Text.RegularExpressions.Regex(@"(\b[Ss]ite|_combat|\.Combat)\s*\??\.\s*Kernel\b|\bCombatKernel\b");
            bool regexOk = direct.IsMatch("_combat.Kernel.CountAlive(CombatFaction.Player)") && direct.IsMatch("_ctx.Site?.Kernel.IssueCommand(u)")
                           && direct.IsMatch("CombatKernel k = x;") && !direct.IsMatch("else if (!slot.Kernel.IsValid)") && !direct.IsMatch("BeltOpResult.Kernel(r)");
            var offenders = new List<string>();
            int bridgeHits = 0;
            // 名册版本号（ADR 第 4 节）：机器记录的 RegionId / IsAlive / BlueprintId / BlueprintVersion 只能经 MachineRegistry 写；
            // 改蓝图（改造）的写之后 4 行内必须调 MachineRegistry.NotifyLoadoutChanged。按“最近一次声明的类型是 MachineRecord”识别变量（var 声明计入“未识别”）。
            var write = new System.Text.RegularExpressions.Regex(@"\b(\w+)\.(RegionId|IsAlive|BlueprintId|BlueprintVersion)\s*=(?!=)");
            var rosterOffenders = new List<string>();
            int registryWrites = 0;
            int notifiedLoadoutWrites = 0;
            int unresolved = 0;
            foreach (string f in files)
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                string[] lines = File.ReadAllLines(f);
                bool bridge = rel.StartsWith("Campaign/Combat/", StringComparison.Ordinal);
                bool registry = rel == "Campaign/MachineRegistry.cs";
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = CodeOnly(lines[i]);
                    if (direct.IsMatch(code))
                    {
                        if (bridge)
                        {
                            bridgeHits++;
                        }
                        else
                        {
                            offenders.Add($"{rel}:{i + 1}");
                        }
                    }
                    foreach (System.Text.RegularExpressions.Match mt in write.Matches(code))
                    {
                        string type = DeclaredTypeAbove(lines, i, mt.Groups[1].Value);
                        if (type == "var" || type == null)
                        {
                            unresolved += code.Contains("MachineRecord") ? 1 : 0;
                            continue;
                        }
                        if (type != "MachineRecord")
                        {
                            continue;
                        }
                        if (registry)
                        {
                            registryWrites++;
                            continue;
                        }
                        string field = mt.Groups[2].Value;
                        bool notified = (field == "BlueprintId" || field == "BlueprintVersion")
                                        && Enumerable.Range(i + 1, 4).Any(k => k < lines.Length && lines[k].Contains("MachineRegistry.NotifyLoadoutChanged("));
                        if (notified)
                        {
                            notifiedLoadoutWrites++;
                        }
                        else
                        {
                            rosterOffenders.Add($"{rel}:{i + 1}（{field}）");
                        }
                    }
                }
            }
            Expect(regexOk && files.Length >= 300 && bridgeHits >= 3 && offenders.Count == 0,
                $"桥接边界（FG14 §5 硬约束 3）：扫描热更玩法代码 {files.Length} 个文件，Campaign/Combat/ 以外直接碰战斗内核 {offenders.Count} 处" +
                $"{(offenders.Count > 0 ? "：" + string.Join("，", offenders.Take(12)) : string.Empty)}（桥接层内命中 {bridgeHits} 处，正则正反例自测 {regexOk}）");
            Expect(registryWrites >= 2 && notifiedLoadoutWrites >= 2 && rosterOffenders.Count == 0,
                $"名册字段只经 MachineRegistry 写：MachineRegistry 内 {registryWrites} 处；改造写蓝图后紧跟 NotifyLoadoutChanged {notifiedLoadoutWrites} 处；" +
                $"其它地方直写 {rosterOffenders.Count} 处{(rosterOffenders.Count > 0 ? "：" + string.Join("，", rosterOffenders.Take(12)) : string.Empty)}（var 声明未识别 {unresolved} 处）");
        }

        // ── E 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            int enemies = (int)CombatSite.Tuning("combat.perf.enemies", 200);
            int turretCount = (int)CombatSite.Tuning("combat.perf.turrets", 80);
            int minProjectiles = (int)CombatSite.Tuning("combat.perf.min_projectiles", 1500);
            float budget = CombatSite.Tuning("combat.perf.step_budget_ms", 6f);
            CombatSite site = PerfSite(enemies, turretCount, out _);
            try
            {
                double t = 0;
                for (int i = 0; i < 600; i++)
                {
                    site.Step(Dt, t);
                    t += Dt;
                }
                var stepMs = new List<double>(600);
                var eventMs = new List<double>(600);
                int minProj = int.MaxValue;
                int maxProj = 0;
                long projSum = 0;
                int minEnemies = int.MaxValue;
                int minTurrets = int.MaxValue;
                int maxEvents = 0;
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 600; i++)
                {
                    site.Step(Dt, t);
                    t += Dt;
                    stepMs.Add(site.LastKernelMs);
                    eventMs.Add(site.LastEventsMs);
                    int pc = site.Kernel.ProjectileCount;
                    minProj = Math.Min(minProj, pc);
                    maxProj = Math.Max(maxProj, pc);
                    projSum += pc;
                    maxEvents = Math.Max(maxEvents, site.LastEventsProcessed);
                    if (i % 60 == 0)
                    {
                        minEnemies = Math.Min(minEnemies, site.Kernel.CountAlive(CombatFaction.Hostile));
                        minTurrets = Math.Min(minTurrets, site.Kernel.CountAlive(CombatFaction.Player, CombatUnitKind.Turret));
                    }
                }
                long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                stepMs.Sort();
                double avg = stepMs.Average();
                double p95 = stepMs[(int)(stepMs.Count * 0.95)];
                double max = stepMs[stepMs.Count - 1];
                double avgEvents = eventMs.Average();
                // 渲染缓冲（无图形设备时照样准备实例数据）。
                var renderer = new CombatRenderer();
                var sw = Stopwatch.StartNew();
                renderer.Draw(site.Kernel, null, 0.5f, double2.zero, 0.6f);
                double prepMs = sw.Elapsed.TotalMilliseconds;
                int drawnUnits = renderer.LastUnitInstances;
                int drawnProj = renderer.LastProjectileInstances;
                renderer.Dispose();
                int instancedAlive = site.Kernel.CountAlive(CombatFaction.Hostile) + site.Kernel.CountAlive(CombatFaction.Player, CombatUnitKind.Turret);
                PerfLines.Add($"性能场景（{enemies} 突袭者 + {turretCount} 炮塔）：测量 600 步里弹体同时存在 {minProj}～{maxProj} 枚（平均 {projSum / 600}），敌人最少 {minEnemies}、炮塔最少 {minTurrets}；" +
                              $"内核单步 平均 {avg:F3} ms / p95 {p95:F3} ms / 最大 {max:F3} ms（预算 {budget} ms）；热更层事件处理平均 {avgEvents:F4} ms/步、每步至多 {maxEvents} 条；" +
                              $"600 步托管分配 {alloc} 字节；渲染缓冲重填 {prepMs:F3} ms（单位 {drawnUnits}、弹体 {drawnProj}）");
                Expect(minProj >= minProjectiles && minEnemies >= enemies * 0.9 && minTurrets >= turretCount * 0.9,
                    $"规模达标：测量窗口内弹体始终 ≥ {minProjectiles}（最少 {minProj}），敌人 ≥ {enemies * 0.9:F0}（{minEnemies}）、炮塔 ≥ {turretCount * 0.9:F0}（{minTurrets}）同时在场");
                Expect(avg <= budget && p95 <= budget,
                    $"内核单步平均 {avg:F3} ms、p95 {p95:F3} ms ≤ {budget} ms（Editor Burst，本机 {SystemInfo.processorType}；推荐配置 i5-10400 的换算见 ADR）");
                Expect(maxEvents <= site.MaxEventsPerStep + site.Kernel.Config.MaxCueEventsPerStep && alloc < 64 * 1024,
                    $"热更层每步事件数有上限（至多 {maxEvents} 条 ≤ 玩法 {site.MaxEventsPerStep} + 提示 {site.Kernel.Config.MaxCueEventsPerStep}），600 步托管分配 {alloc / 1024.0:F1} KB（稳态接近 0）");
                Expect(drawnUnits == instancedAlive && drawnProj == site.Kernel.ProjectileCount,
                    $"实例化渲染缓冲 = 内核状态：单位 {drawnUnits} = 存活 {instancedAlive}，弹体 {drawnProj} = {site.Kernel.ProjectileCount}");
            }
            finally
            {
                site.Dispose();
            }
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        private sealed class SilentReader : IInputReader
        {
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
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
            finally
            {
                try
                {
                    GameClock.SetPaused(false);
                    GameClock.SetSpeed(1f);
                    InputRouter.DebugSetReader(null);
                    InputRouter.SetGameplayPaused(false);
                }
                catch
                {
                    // 收尾失败不影响下一段。
                }
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
