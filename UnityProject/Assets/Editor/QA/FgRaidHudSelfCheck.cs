using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-06 突袭 HUD、观战、自动暂停、防御总览的自动验收（FG06 FGR-DEF-042 / 070；FG13 FGU-27 / FGU-28；FGT-UX-004 的自动暂停部分；卡片负向“突袭期间打开其他面板”）。
    /// 全部起真实系统：真实家园（世界模拟、战斗内核攻城、管线内核、电网）、真实突袭导演（剧情突袭 → 出发 → 沿地形行进 → 到达展开）、真实通知中心与自动暂停、真 UXML 面板、真文件存读档。
    /// A 数据；B 自动暂停（前 3 次默认、按类型设置、同一波不重复、恢复默认、存读档、倍速下暂停在同一步）；C 突袭 HUD（剩余敌人 / 撤退 / 建造模式一行 / 布局）；
    /// D 观战（复用倍速、镜头跟随、手动移开暂停跟随、面板开着不飞、Esc 层级、突袭结束自动恢复、观战不改模拟）；E 防御总览（列表 / 筛选 / 批量 / 热力图 / 薄弱点 / 外围检查 / 布局）；
    /// F 战时预案的炮塔 / 陷阱补给优先（管线消费者优先级、抢流体的行为对照、可追溯、存读档）；G 预警 / 突袭音乐与提示音；H 面板开着时弹出条不挡点击；I 接入炮塔的读数；
    /// J 驻防巡逻；K 负向“突袭期间打开其他面板”；L 存读档与暂停 / 倍速矩阵；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgRaidHudSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgRaidHudSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 5;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string HudUxml = UiKitFolder + "RaidWarningHud.uxml";
        private const string OverviewUxml = UiKitFolder + "DefenseOverviewPanel.uxml";
        private const string NotifyUxml = UiKitFolder + "NotificationHud.uxml";
        private const string RosterUxml = UiKitFolder + "RosterPanel.uxml";
        private const string BuildUxml = UiKitFolder + "BuildModeHud.uxml";
        private const string L = "turret_light";
        private const string BpWet = "bp_rh_wet";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 突袭 HUD、观战、自动暂停、防御总览")]
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
            Line("\n[突袭 HUD、观战、自动暂停、防御总览] 剩余敌人、倍速观战、前 3 次突袭自动暂停、防御总览热力图与薄弱点、战时预案补给优先、驻防巡逻（FG6-DEF-06）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgraidhud-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                BuildingOps.Reload();
                BuildMaterials.Reload();
                ResearchCatalog.Reload();
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                SiegeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.ResetNotifyAutoPause();
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                RaidDirectorService.EnsureArrivalAutoPauseRegistered();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；覆盖栅格化 / 薄弱区段 / 攻城单位计数与重心在 AOT（Main/Sim），HUD / 总览 / 观战在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckAutoPause);
                Step(CheckAutoPauseWaveAndSave);
                Step(CheckAutoPauseTiming);
                Step(CheckAutoPauseSettingsUi);
                Step(CheckHudAndSpectate);
                Step(CheckSpectateKeysAndPausedStart);
                Step(CheckCompactLayout);
                Step(CheckOverviewCoverage);
                Step(CheckOverviewPanel);
                Step(CheckBoostToggleTwoRules);
                Step(CheckSupplyBoost);
                Step(CheckTrapSupplyBoost);
                Step(CheckMusic);
                Step(CheckToastPassthrough);
                Step(CheckTurretStrip);
                Step(CheckPatrol);
                Step(CheckPatrolPointsDestroyed);
                Step(CheckPatrolPointUnreachable);
                Step(CheckSpectateTiming);
                Step(CheckBackgroundEqualsObserved);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"突袭 HUD 自检抛异常：{e}");
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
                RaidSpectateService.ModalOpenProvider = null;
                NotificationHudUIToolkit.ModalOpenOverrideForTests = null;
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                DefenseOverviewPanelUIToolkit.InWorldOverrideForTests = false;
                RaidSpectateService.ResetSession();
                RaidAudioDirector.ResetSession();
                StandingRuleService.RaidActiveProvider = null;
                SiegeService.ResetSessionState();
                DefenseService.ResetSessionState();
                TurretService.ResetSessionState();
                RepairDroneService.ResetSessionState();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                foreach (object o in InputRouter.ModalOwnerList.ToArray())
                {
                    InputRouter.PopModal(o);
                }
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
                GameSettings.SetLanguage(originalLanguage);
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
            Line($"  · [突袭 HUD、观战、自动暂停、防御总览] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>与主菜单“新建”同一入口（v2 世界），家园有电、防御研发已完成。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RaidDirectorService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            UiEscapeStack.Clear();
            _seq = 9400;
            CampaignState s = CampaignState.CreateNew("fgraidhud-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 3000;
            F.PowerUp(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        /// <summary>离核心 <paramref name="dist"/> 格、敌方可走的一点（按 16 个方向依次试；B25）。</summary>
        private static Vector2 OutsidePoint(CampaignState s, float dist, int startDir = 0)
        {
            Vector2 c = CoreCenter(s);
            for (int k = 0; k < 16; k++)
            {
                float ang = ((startDir + k) % 16) * 22.5f * Mathf.Deg2Rad;
                var p = new Vector2(c.x + Mathf.Cos(ang) * dist, c.y + Mathf.Sin(ang) * dist);
                GridCell g = SiegeService.NearestPassable(NavService.CellOf(p.x, p.y), 6);
                if (NavService.PassableNow(g.X, g.Y, NavConst.ClassHostile))
                {
                    return new Vector2(g.X, g.Y);
                }
            }
            return c + new Vector2(dist, 0f);
        }

        /// <summary>
        /// 测试捷径：一支离到达半径只差几格、正朝家园行进的突袭队伍（编成按参数）。之后照常由世界模拟沿地形走到、“突袭到达”（真实的 WorldTransitSystem.Arrive 与通知），
        /// 再由攻城服务展开。<paramref name="planId"/> 给出时挂在那个计划上（同一波的几支）。
        /// </summary>
        private static TransitGroupRecord Incoming(CampaignState s, int startDir = 0, string planId = null, int units = 4)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 at = OutsidePoint(s, (float)WorldTransitSystem.ArrivalRadius + 6f, startDir);
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, "foundry", units, at.x, at.y, core.X, core.Y);
            g.Faction = "foundry";
            g.UnitIds = new[] { "foundry.strider" };
            g.UnitCounts = new[] { units };
            g.EliteCounts = new int[1];
            g.TargetKind = RaidDirectorService.TargetHome;
            if (planId != null)
            {
                g.PlanId = planId;
            }
            return g;
        }

        /// <summary>把一支已经展开的突袭整支消灭（测试捷径：内核移除单位，对账时记“被全歼”；正式交战由攻城 / 炮塔自检覆盖）。</summary>
        private static void Annihilate(CampaignState s)
        {
            CombatSite site = Site;
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s).Where(x => x != null && x.Kind == TransitGroupKind.Raid).ToList())
            {
                if (g.Engaged || g.Intercepted)
                {
                    site?.DespawnSiegeGroup(SiegeService.KeyOf(g));
                }
                else
                {
                    WorldTransitSystem.RemoveDestroyed(s, g);
                }
            }
            Seconds(1.5f);
        }

        /// <summary>正式旅程：剧情突袭 → 排定 → 预警 → 出发 → 沿地形行进 → 到达家园 → 按编成展开（与 FgSiegeSelfCheck F 段同一路径）。</summary>
        private static CampaignState FormalArrival(int seed, out TransitGroupRecord g, out RaidPlanRecord plan, bool observe = true)
        {
            CampaignState s = NewWorld(seed, observe);
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if (GameClock.Ticks < grace)
            {
                GameClock.SkipForTests(s, grace - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
            plan = p;
            g = null;
            if (p == null || !StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120))
            {
                return s;
            }
            long warn = p.WarnTick - 2;
            if (warn > GameClock.Ticks && WorldTransitSystem.Groups(s).All(x => x == null))
            {
                GameClock.SkipForTests(s, warn - GameClock.Ticks);
            }
            if (!StepUntil(() => p.State >= RaidDirectorService.StateDeparted, 900))
            {
                return s;
            }
            TransitGroupRecord found = WorldTransitSystem.Find(s, p.GroupId);
            if (found == null || !StepUntil(() => found.Engaged, 1800))
            {
                return s;
            }
            g = found;
            return s;
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            RaidDirectorService.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                fail = rr.Message;
                return null;
            }
            fail = null;
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return rr.State;
        }

        private static bool SaveSlot()
        {
            WorldSimulation.SyncAllForSave();
            return CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual).Success;
        }

        private static void Seconds(float sec) => F.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => F.StepUntil(done, maxGameSeconds);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string Short(string s) => s == null ? string.Empty : s.Length > 180 ? s.Substring(0, 180) + "…" : s;

        private static bool HasCjk(string text) => text != null && text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        /// <summary>测试用的自动暂停执行者：与 GameRoot 同一语义（世界在跑时暂停并返回 true，已经暂停返回 false）。</summary>
        private static bool PauseHandler()
        {
            if (GameClock.Paused)
            {
                return false;
            }
            GameClock.SetPaused(true);
            return true;
        }

        /// <summary>测试捷径：直接登记一座建成、满耐久的建筑（真实放置 / 施工由 FG3 / FG6 的自检覆盖）。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, 0),
                Health = BuildingOps.MaxDurability(type),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            TurretService.Sync(s);
            DefenseService.Sync(s);
            return r;
        }

        /// <summary>在离核心 <paramref name="from"/>～<paramref name="to"/> 格、朝 <paramref name="deg"/> 方向附近找一块能放 <paramref name="type"/> 的地方登记一座（按种子地形找，B25）。</summary>
        private static BuildingRecord PlaceAt(CampaignState s, string type, Vector2 near, float spread = 8f)
        {
            for (float d = 0f; d <= spread; d += 1f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float ang = a * 15f * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(near.x + Mathf.Cos(ang) * d), Mathf.RoundToInt(near.y + Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return Register(s, type, c);
                    }
                }
            }
            return null;
        }

        private static int Alive(TransitGroupRecord g) => Site?.CountSiegeGroup(SiegeService.KeyOf(g)) ?? 0;

        private static VisualElement Mount(string uxml, out GameObject go, int w = 1920, int h = 1080, float scale = 1f)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxml);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.scale = scale;
            settings.targetTexture = new RenderTexture(w, h, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgRaidHudSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private sealed class DummyPanel
        {
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static readonly string[] TuningKeys =
        {
            "raid.auto_pause_first_raids", "raid.spectate.default_speed", "raid.spectate.follow_seconds", "raid.spectate.follow_min_cells", "raid.spectate.manual_cells",
            "raid.music.volume", "raid.hud.refresh_seconds", "raid.hud.perf_ms", "defov.margin_cells", "defov.max_cells", "defov.weak_min_cells", "defov.weak_max",
            "defov.ring_extra_cells", "defov.ring_samples", "defov.refresh_seconds", "defov.perf_ms", "roster.patrol_max_points", "roster.patrol_arrive_cells",
            "roster.patrol_arrive_slack", "roster.patrol_retry_seconds",
        };

        private static void CheckData()
        {
            bool tuning = TuningKeys.All(k => GridContent.TryGetTuning(k, out _)) && RaidDirectorService.AutoPauseFirstRaids == 3
                          && Mathf.Approximately(RaidSpectateService.DefaultSpeed, 2f) && DefenseOverviewService.WeakMinCells >= 1;
            string[] keys =
            {
                "raid.hud.row_siege", "raid.hud.remaining", "raid.hud.limit_left", "raid.hud.retreating", "raid.hud.compact_active", "raid.hud.compact_incoming",
                "raid.spec.start", "raid.spec.stop", "raid.spec.tip", "raid.turret.title", "ui.notify.autopause_dynamic", "ui.notify.autopause_reset",
                "defov.title", "defov.summary", "defov.row.turret", "defov.heat.legend", "defov.weak.route", "defov.weak.ring", "defov.boost.on",
                "rules.edit.boost_supply_on", "rules.log.supply_boost", "turret.supply.boosted", "roster.patrol.route", "codex.raid.hud.title", "codex.defense.overview.body",
                "input.action.open_defense.name", "input.action.spectate_raid.name", "input.action.spectate_follow.name", "raid.spec.started_unpaused", "raid.spec.keys",
                "raid.spec.follow_tip", "rules.msg.boost_supply_off_all", "rules.msg.boost_supply_reenabled", "roster.patrol.skipped",
            };
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    string t = GameText.Get(k);
                    if (string.IsNullOrEmpty(t) || GameText.ContainsMarker(t) || (lang == GameLanguage.En && HasCjk(t)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string[] hooks = { GuidanceHooks.RaidHudFirstActive, GuidanceHooks.RaidFirstSpectate, GuidanceHooks.RaidFirstAutoPause, GuidanceHooks.DefenseOverviewFirstOpen,
                               GuidanceHooks.DefenseFirstWeakPoint, GuidanceHooks.RosterFirstPatrol };
            bool hooksOk = hooks.All(h => GuidanceHooks.Known.Contains(h));
            bool key = InputActionCatalog.TryGet(GameActionId.OpenDefense, out InputActionDef def) && def.Status == InputActionStatus.Wired
                       && InputActionCatalog.DefaultChord(GameActionId.OpenDefense).Key == KeyCode.E && (InputActionCatalog.DefaultChord(GameActionId.OpenDefense).Mods & InputModifier.Alt) != 0;
            // 审查修复（FG00 B02）：观战 / 跟随战斗也有快捷键（战略上下文、可重绑、已接线）。
            bool specKeys = InputActionCatalog.TryGet(GameActionId.SpectateRaid, out InputActionDef sd) && sd.Status == InputActionStatus.Wired && sd.Contexts == InputContext.Strategy
                            && InputActionCatalog.DefaultChord(GameActionId.SpectateRaid).Key == KeyCode.V && InputActionCatalog.DefaultChord(GameActionId.SpectateRaid).Mods == InputModifier.Alt
                            && InputActionCatalog.TryGet(GameActionId.SpectateFollow, out InputActionDef fd) && fd.Status == InputActionStatus.Wired && fd.Contexts == InputContext.Strategy
                            && InputActionCatalog.DefaultChord(GameActionId.SpectateFollow).Key == KeyCode.F && InputActionCatalog.DefaultChord(GameActionId.SpectateFollow).Mods == InputModifier.Ctrl;
            key &= specKeys;
            bool codex = MechanicCodex.Find("codex.raid.hud") != null && MechanicCodex.Find("codex.defense.overview") != null;
            Expect(tuning && missing.Count == 0 && hooksOk && key && codex,
                $"A1 数据：20 项调参（raid.auto_pause_first_raids = 3、观战默认 2x……）、文本中英齐全（缺 {string.Join(",", missing.Take(6))}）、6 个引导钩子、快捷键“防御总览”Alt+E / “观战”Alt+V / “跟随战斗”Ctrl+F 已接线（{key}，观战两键 {specKeys}）、两条图鉴（{codex}）");
        }

        // ── B 自动暂停（FGR-DEF-042 / FGR-UX-021 / FGT-UX-004）───────────────────────

        private static void CheckAutoPause()
        {
            CampaignState s = NewWorld(6301);
            NotificationCenter.AutoPauseHandler = PauseHandler;
            GameSettings.ResetNotifyAutoPause();
            try
            {
                var paused = new List<bool>();
                int autoNotes0 = NotifyCount("auto_paused");
                for (int i = 0; i < 4; i++)
                {
                    TransitGroupRecord g = Incoming(s, i * 4);
                    bool arrived = StepUntil(() => g.State == TransitGroupState.Arrived, 90);
                    paused.Add(arrived && GameClock.Paused);
                    GameClock.SetPaused(false);
                    Annihilate(s);
                }
                int arrivedWaves = RaidDirectorService.ArrivedWaves(s);
                bool firstThree = paused.Count == 4 && paused[0] && paused[1] && paused[2] && !paused[3];
                NotificationCatalog.TryGetType(RaidDirectorService.ArrivalNotifyType, out NotifyTypeDef arrivalDef);
                bool traced = NotifyCount("auto_paused") - autoNotes0 == 3 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstAutoPause) && arrivalDef != null
                              && NotificationCenter.History.Last(n => n.Type.Id == "auto_paused").SourceArg == arrivalDef.NameKey;
                Expect(firstThree && arrivedWaves == 4 && traced,
                    $"B1 FGR-DEF-042“突袭到达时自动暂停，默认对前 3 次突袭开启”：真实行进 → 到达 → “突袭到达”通知，前 3 次自动暂停、第 4 次不暂停（{string.Join("/", paused)}）；本存档计数 {arrivedWaves}；" +
                    "每次暂停都发一条写明是哪类通知触发的“已自动暂停”（FGR-BASE-020）、首次因突袭自动暂停的钩子");

                // 按类型设置（FGR-UX-021）：玩家打开“突袭到达”→ 第 5 次也暂停；关掉 → 不暂停；别的类型（突袭预警）不受影响。
                GameSettings.SetNotifyAutoPause(RaidDirectorService.ArrivalNotifyType, true);
                TransitGroupRecord g5 = Incoming(s, 2);
                bool on = StepUntil(() => g5.State == TransitGroupState.Arrived, 90) && GameClock.Paused;
                GameClock.SetPaused(false);
                Annihilate(s);
                GameSettings.SetNotifyAutoPause(RaidDirectorService.ArrivalNotifyType, false);
                TransitGroupRecord g6 = Incoming(s, 6);
                bool offArrived = StepUntil(() => g6.State == TransitGroupState.Arrived, 90);
                bool off = offArrived && !GameClock.Paused;
                Annihilate(s);
                NotificationCatalog.TryGetType("raid_warning", out NotifyTypeDef warnDef);
                bool otherType = warnDef != null && NotificationCenter.IsAutoPauseEnabled(warnDef) == warnDef.AutoPauseDefault && !NotificationCenter.HasDynamicAutoPauseDefault("raid_warning");
                // 恢复默认：清掉玩家的勾选 → 第 7 次（已过前 3 次）不暂停。
                GameSettings.ResetNotifyAutoPause();
                bool resetOk = !GameSettings.HasNotifyAutoPauseChoice(RaidDirectorService.ArrivalNotifyType)
                               && NotificationCatalog.TryGetType(RaidDirectorService.ArrivalNotifyType, out NotifyTypeDef arrDef) && !NotificationCenter.IsAutoPauseEnabled(arrDef);
                Expect(on && off && otherType && resetOk,
                    $"B2 自动暂停可以按类型设置：玩家打开“突袭到达”后第 5 次也暂停（{on}）、关掉后不暂停（{off}）；其它类型按各自默认值、不受影响（{otherType}）；“恢复默认”清掉勾选后回到“前 3 次”（{resetOk}）");
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = null;
                GameClock.SetPaused(false);
                GameSettings.ResetNotifyAutoPause();
            }
        }

        private static void CheckAutoPauseWaveAndSave()
        {
            CampaignState s = NewWorld(6302);
            NotificationCenter.AutoPauseHandler = PauseHandler;
            GameSettings.ResetNotifyAutoPause();
            try
            {
                // 同一波分两支先后到达：只算一次、只暂停一次（测试捷径：两支队伍挂在同一个计划上）。
                RaidDirectorState d = RaidDirectorService.StateOf(s);
                var plan = new RaidPlanRecord { PlanId = "raid-rh-test", Serial = 990, Wave = 77, State = RaidDirectorService.StateDeparted, TargetKind = RaidDirectorService.TargetHome };
                d.Plans = d.Plans.Append(plan).ToArray();
                TransitGroupRecord a = Incoming(s, 0, plan.PlanId);
                bool arrA = StepUntil(() => a.State == TransitGroupState.Arrived, 90);
                bool pausedA = GameClock.Paused;
                GameClock.SetPaused(false);
                TransitGroupRecord b = Incoming(s, 8, plan.PlanId);
                bool arrB = StepUntil(() => b.State == TransitGroupState.Arrived, 90);
                bool pausedB = GameClock.Paused;
                GameClock.SetPaused(false);
                int count = RaidDirectorService.ArrivedWaves(s);
                Expect(arrA && arrB && pausedA && !pausedB && count == 1 && d.CountedWaves.Contains(77),
                    $"B3 同一波分两支先后到达：本存档突袭次数只加 1（{count}），只在第一支到达时自动暂停（{pausedA} / {pausedB}）");
                Annihilate(s);
                d.Plans = d.Plans.Where(x => x.PlanId != plan.PlanId).ToArray();

                // 再到达两波 → 已到达 3 波；真文件存读档后计数不变，第 4 次（读档后）默认不暂停。
                for (int i = 0; i < 2; i++)
                {
                    TransitGroupRecord g = Incoming(s, 3 + i * 5);
                    StepUntil(() => g.State == TransitGroupState.Arrived, 90);
                    GameClock.SetPaused(false);
                    Annihilate(s);
                }
                int before = RaidDirectorService.ArrivedWaves(s);
                bool saved = SaveSlot();
                CampaignState l = RestoreSlot(out string fail);
                int after = l != null ? RaidDirectorService.ArrivedWaves(l) : -1;
                bool pausedAfter = true;
                if (l != null)
                {
                    TransitGroupRecord g4 = Incoming(l, 11);
                    StepUntil(() => g4.State == TransitGroupState.Arrived, 90);
                    pausedAfter = GameClock.Paused;
                    GameClock.SetPaused(false);
                }
                Expect(saved && before == 3 && after == 3 && !pausedAfter,
                    $"B4 存读档：本存档已到达 {before} 波 → 读档后 {after} 波（{fail}）；读档后的第 4 次突袭默认不再自动暂停（{!pausedAfter}）");
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = null;
                GameClock.SetPaused(false);
            }
        }

        /// <summary>0.5x / 1x / 2x / 3x 下按真实帧推进：突袭到达的那一步暂停，同一个种子各倍速暂停在同一步；暂停后 90 帧不再推进。</summary>
        private static void CheckAutoPauseTiming()
        {
            NotificationCenter.AutoPauseHandler = PauseHandler;
            GameSettings.ResetNotifyAutoPause();
            var ticks = new List<long>();
            bool held = true;
            try
            {
                foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
                {
                    CampaignState s = NewWorld(6303);
                    TransitGroupRecord g = Incoming(s, 5);
                    GameClock.SetSpeed(speed);
                    int frames = 0;
                    while (!GameClock.Paused && frames < 60 * 300)
                    {
                        WorldSimulation.Frame(1f / 60f);
                        frames++;
                    }
                    long at = GameClock.Ticks;
                    for (int i = 0; i < 90; i++)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    held &= GameClock.Paused && GameClock.Ticks == at && g.State == TransitGroupState.Arrived;
                    ticks.Add(g.State == TransitGroupState.Arrived ? g.ArrivedAtTick * 1000 + (at - g.ArrivedAtTick) : -1);
                    GameClock.SetPaused(false);
                    GameClock.SetSpeed(1f);
                }
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = null;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
            Expect(held && ticks.Count == 4 && ticks.All(t => t >= 0 && t == ticks[0]),
                $"B5 暂停与倍速矩阵：0.5x / 1x / 2x / 3x 下突袭在同一步到达并在那一步暂停（到达步×1000+暂停时多走的步：{string.Join(" / ", ticks)}），暂停后 90 帧世界不再推进（{held}）");
        }

        /// <summary>通知中心设置页（真 UXML）：“突袭到达”一行写明“默认：前 3 次突袭，本存档已到达 n 次”，勾选反映这一刻的实际值；“恢复默认”清掉玩家改过的勾选。</summary>
        private static void CheckAutoPauseSettingsUi()
        {
            CampaignState s = NewWorld(6304);
            GameSettings.ResetNotifyAutoPause();
            VisualElement root = Mount(NotifyUxml, out GameObject go);
            try
            {
                var hud = go.AddComponent<NotificationHudUIToolkit>();
                hud.BindView(root);
                NotificationHudUIToolkit.OpenCenter();
                // 打开设置页（与玩家点“设置”同一回调）。
                Button settings = root.Q<Button>("NotifySettingsToggle");
                using (var e = NavigationSubmitEvent.GetPooled())
                {
                    e.target = settings;
                    settings.SendEvent(e);
                }
                typeof(NotificationHudUIToolkit).GetMethod("RenderSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(hud, null);
                Toggle arrival = hud.AutoPauseList.Children().OfType<Toggle>().FirstOrDefault(t => (t.userData as NotifyTypeDef)?.Id == RaidDirectorService.ArrivalNotifyType);
                bool labelOk = arrival != null && arrival.label.Contains("前 3 次") && arrival.label.Contains("已到达 0 次") && arrival.value;
                // 本存档已经到达 3 次 → 默认值变成“不暂停”，勾选跟着变。
                RaidDirectorService.StateOf(s).ArrivedWaveCount = 3;
                typeof(NotificationHudUIToolkit).GetMethod("RenderSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(hud, null);
                bool afterThree = arrival != null && !arrival.value && arrival.label.Contains("已到达 3 次");
                // 玩家勾上 → 记进设置；“恢复默认”按钮清掉。
                arrival.value = true;
                bool choice = GameSettings.HasNotifyAutoPauseChoice(RaidDirectorService.ArrivalNotifyType) && GameSettings.IsNotifyAutoPauseEnabled(RaidDirectorService.ArrivalNotifyType, false);
                hud.ResetAutoPause();
                bool reset = !GameSettings.HasNotifyAutoPauseChoice(RaidDirectorService.ArrivalNotifyType) && !arrival.value && hud.AutoPauseResetButton != null
                             && hud.AutoPauseResetButton.text == GameText.Get("ui.notify.autopause_reset");
                Expect(labelOk && afterThree && choice && reset,
                    $"B6 设置页（FGR-UX-021 按通知类型勾选）：“{arrival?.label}”——新存档默认勾上（{labelOk}），到达 3 次后默认不勾（{afterThree}）；玩家勾上记进设置（{choice}）；“恢复默认”清掉（{reset}）");
            }
            finally
            {
                NotificationHudUIToolkit.CloseCenter();
                GameSettings.ResetNotifyAutoPause();
                Object.DestroyImmediate(go);
            }
        }

        // ── C / D / K 突袭 HUD、观战、负向“突袭期间打开其他面板” ─────────────────────

        private static void CheckHudAndSpectate()
        {
            CampaignState s = FormalArrival(6311, out TransitGroupRecord g, out RaidPlanRecord p);
            if (g == null)
            {
                Fail("C/D 准备：正式旅程没有到达展开（剧情突袭 → 出发 → 到达）");
                return;
            }
            VisualElement root = Mount(HudUxml, out GameObject go);
            RaidWarningHudUIToolkit.InWorldOverrideForTests = true;
            object dummy = new DummyPanel();
            try
            {
                var hud = go.AddComponent<RaidWarningHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh(force: true);
                int alive = Alive(g);
                string row = hud.RowText(0);
                bool remaining = hud.PanelVisible && hud.RowCount >= 1 && row.Contains(GameText.Format("raid.hud.remaining", alive, Math.Max(g.UnfoldedCount, alive), 0, 0, 0, 0).Split('（')[0])
                                 && row.Contains(RaidDirectorService.DirectionText(p)) && row.Contains("最晚") && RaidHudService.TotalAlive(s) == alive && alive > 0
                                 && hud.SpecBarVisible && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidHudFirstActive) && !GameText.ContainsMarker(row);
                Expect(remaining,
                    $"C1 FGU-28 突袭 HUD：正式到达展开后同一行改成战况——“{Short(row.Replace('\n', ' '))}”（剩余敌人 = 内核里还活着的 {alive} 台、职能、已损失、来袭方向、最晚多久后撤退）；观战栏出现（{hud.SpecBarVisible}）");

                // 撤退：损失过大 / 时间上限触发撤退后，行里写“撤退中：还剩 n 台”。
                CombatSite site = Site;
                SiegeService.OrderRetreat(s, site, g, SiegeService.ReasonLosses, alive, 0);
                Seconds(0.5f);
                hud.Refresh(force: true);
                string retreat = hud.RowText(0);
                Expect(retreat.Contains("撤退中") && !retreat.Contains("最晚"), $"C2 撤退：行里写“{Short(retreat.Replace('\n', ' '))}”");
                string en;
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh(force: true);
                en = hud.RowText(0) + hud.SpecStatusText + hud.SpecToggleButton.text;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(!HasCjk(en) && !GameText.ContainsMarker(en), $"C3 英文界面：突袭行与观战栏没有中文残留（“{Short(en.Replace('\n', ' '))}”）");

                // 观战（FGR-DEF-042）：点“观战”→ 2x、镜头飞到战斗重心；复用统一时钟的倍速（GameClock.Speed）。
                CampaignState s2 = FormalArrival(6312, out TransitGroupRecord g2, out RaidPlanRecord p2);
                if (g2 == null)
                {
                    Fail("D 准备：第二场正式旅程没有到达展开");
                    return;
                }
                hud.Refresh(force: true);
                float speed0 = GameClock.Speed;
                int fly0 = WorldView.FlyCount;
                hud.ClickSpectate();
                RaidHudService.FightFocus(s2, out Vector2 focus);
                bool started = RaidSpectateService.Active && Mathf.Approximately(GameClock.Speed, 2f) && WorldView.FlyCount > fly0
                               && Vector2.Distance(RaidSpectateService.LastTarget, focus) < 0.5f && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstSpectate)
                               && hud.SpecToggleButton.text == GameText.Get("raid.spec.stop") && hud.SpecSpeedButton(1).text.StartsWith("●");
                hud.ClickSpeed(2);
                bool triple = Mathf.Approximately(GameClock.Speed, 3f) && hud.SpecSpeedButton(2).text.StartsWith("●");
                hud.ClickPause();
                bool pausedBtn = GameClock.Paused && hud.SpecPauseButton.text == GameText.Get("raid.spec.resume");
                hud.ClickPause();
                Expect(started && triple && pausedBtn && !GameClock.Paused,
                    $"D1 倍速观战：点“观战”切到 2x（原来 {speed0}x）、镜头飞到战斗重心（{focus}）；观战栏 3x（{triple}）、暂停 / 继续（{pausedBtn}）都是统一时钟的同一个档位");

                // 跟随：敌人移动后镜头跟着飞；玩家自己把镜头移开 → 暂停跟随；点“跟随战斗”恢复。
                Seconds(3f);
                int f1 = RaidSpectateService.FollowFlights;
                RaidSpectateService.Tick(s2, RaidSpectateService.FollowSeconds + 0.1f);
                Vector2 far = focus + new Vector2(RaidSpectateService.ManualCells * 3f, 0f);
                WorldView.Director.SetStrategyView(new float2(far.x, far.y), WorldView.Director.StrategyOrthographicSize);
                RaidSpectateService.Tick(s2, 2f);
                bool manual = !RaidSpectateService.Following && RaidSpectateService.FollowPausedByCamera && RaidSpectateService.StatusText().Contains("跟随已暂停");
                hud.ClickFollow();
                bool resumed = RaidSpectateService.Following && RaidSpectateService.FollowFlights > f1;
                Expect(manual && resumed, $"D2 镜头跟随：玩家把镜头移开 {RaidSpectateService.ManualCells * 3f:0} 格后暂停跟随并写明（{manual}），点“跟随战斗”恢复并飞回（{resumed}）");

                // K 负向“突袭期间打开其他面板”：面板（模态）开着时——世界照常推进、HUD 照常刷新、观战镜头不在面板底下飞、弹出条不挡面板按钮；Esc 先关面板、再退出观战、再开暂停菜单。
                InputRouter.PushModal(dummy);
                bool closedPanel = false;
                UiEscapeStack.Push(dummy, () => { closedPanel = true; InputRouter.PopModal(dummy); UiEscapeStack.Remove(dummy); });
                long t0 = GameClock.Ticks;
                int rebuilds0 = hud.Rebuilds;
                int flights0 = RaidSpectateService.FollowFlights;
                WorldView.Director.SetStrategyView(new float2(focus.x + 5f, focus.y), WorldView.Director.StrategyOrthographicSize);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                    RaidSpectateService.Tick(s2, 1f / 60f);
                }
                RaidSpectateService.Tick(s2, RaidSpectateService.FollowSeconds + 0.1f);
                hud.Refresh();
                bool worldRuns = GameClock.Ticks > t0;
                bool hudRefresh = hud.Rebuilds > rebuilds0 && hud.PanelVisible;
                bool noFly = RaidSpectateService.FollowFlights == flights0 && RaidSpectateService.StatusText().Contains(GameText.Get("raid.spec.follow_panel"));
                bool esc1 = UiEscapeStack.CloseTop() && closedPanel && RaidSpectateService.Active;
                bool esc2 = UiEscapeStack.CloseTop() && !RaidSpectateService.Active && Mathf.Approximately(GameClock.Speed, speed0);
                Expect(worldRuns && hudRefresh && noFly && esc1 && esc2,
                    $"K1 负向“突袭期间打开其他面板”：面板开着时世界照常推进（{worldRuns}）、突袭条照常刷新（{hudRefresh}）、观战镜头不在面板底下飞（{noFly}）；Esc 先关面板（{esc1}）、再退出观战并恢复 {speed0}x（{esc2}）");

                // 面板开着时又一波到达：自动暂停照样触发（前 3 次之内）。
                NotificationCenter.AutoPauseHandler = PauseHandler;
                InputRouter.PushModal(dummy);
                TransitGroupRecord extra = Incoming(s2, 9);
                bool arrivedUnderPanel = StepUntil(() => extra.State == TransitGroupState.Arrived, 90);
                bool pausedUnderPanel = GameClock.Paused;
                GameClock.SetPaused(false);
                InputRouter.PopModal(dummy);
                NotificationCenter.AutoPauseHandler = null;
                Expect(arrivedUnderPanel && pausedUnderPanel, $"K2 面板开着时又一波突袭到达：照样自动暂停（{pausedUnderPanel}；本存档第 {RaidDirectorService.ArrivedWaves(s2)} 次）");

                // 突袭结束：观战自动结束并恢复开始前的速度。
                GameClock.SetSpeed(1f);
                RaidSpectateService.TryStart(s2, 3f, out _);
                Annihilate(s2);
                Seconds(2f);
                RaidSpectateService.Tick(s2, 0.1f);
                bool autoEnd = !RaidSpectateService.Active && RaidSpectateService.AutoEnds > 0 && Mathf.Approximately(GameClock.Speed, 1f)
                               && RaidSpectateService.LastMessage.Contains("突袭结束");
                bool noRaid = !RaidSpectateService.TryStart(s2, 2f, out string why) && why == GameText.Get("raid.spec.reason.no_raid");
                hud.Refresh(force: true);
                Expect(autoEnd && noRaid && !hud.SpecBarVisible,
                    $"D3 突袭结束时观战自动结束、速度恢复 1x（“{RaidSpectateService.LastMessage}”）；没有突袭时不能开观战并写明原因（“{why}”）；观战栏收起");
            }
            finally
            {
                InputRouter.PopModal(dummy);
                UiEscapeStack.Remove(dummy);
                NotificationCenter.AutoPauseHandler = null;
                RaidSpectateService.ResetSession();
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// DEBT-FG6DEF04-13：建造模式打开时突袭条收成一行（倒计时 / 剩余敌人），不挡建造栏——两个真 UXML 在四种分辨率（含 UI 缩放 150%）下比较：一行的底边在建造栏顶边之上。
        /// 另跑布局探针（整条 + 观战栏 + 接入炮塔读数，超长文字）。
        /// </summary>
        private static void CheckCompactLayout()
        {
            CampaignState s = NewWorld(6313);
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            GameClock.SkipForTests(s, Math.Max(0, grace - GameClock.Ticks));
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).First();
            StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            GameClock.SkipForTests(s, Math.Max(0, p.WarnTick - 2 - GameClock.Ticks));
            StepUntil(() => p.State >= RaidDirectorService.StateWarned, 20);
            VisualElement root = Mount(HudUxml, out GameObject go);
            RaidWarningHudUIToolkit.InWorldOverrideForTests = true;
            bool opened = false;
            try
            {
                var hud = go.AddComponent<RaidWarningHudUIToolkit>();
                hud.BindView(root);
                HomeValleyBuildMode bm = HomeValleyBuildMode.Current;
                if (bm != null)
                {
                    bm.Open();
                    opened = true;
                }
                hud.Refresh(force: true);
                string compact = hud.CompactText;
                bool compactOk = opened && !hud.PanelVisible && hud.CompactVisible && compact.Contains(RaidDirectorService.DirectionText(p)) && compact.Contains("后抵达");
                bm?.Close();
                opened = false;
                hud.Refresh(force: true);
                bool restored = hud.PanelVisible && !hud.CompactVisible;
                Expect(compactOk && restored,
                    $"C4 DEBT-FG6DEF04-13：建造模式打开时突袭条收成一行“{compact}”（倒计时、方向），关掉建造模式立即恢复整条（{restored}）");

                var res = new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3440, 1440) };
                var lines = new List<string>();
                bool all = true;
                foreach (Vector2Int r in res)
                {
                    foreach (float scale in new[] { 1f, 1.5f })
                    {
                        VisualElement hr = Mount(HudUxml, out GameObject hgo, r.x, r.y, scale);
                        VisualElement br = Mount(BuildUxml, out GameObject bgo, r.x, r.y, scale);
                        try
                        {
                            Label c = hr.Q<Label>("RaidWarnCompact");
                            c.RemoveFromClassList("uk-hidden");
                            c.text = new string('突', 120);
                            VisualElement panel = br.Q<VisualElement>("BuildPanel");
                            panel.RemoveFromClassList("uk-hidden");
                            UiToolkitLayoutProbe.ForceLayout(hr);
                            UiToolkitLayoutProbe.ForceLayout(br);
                            float cBottom = c.worldBound.yMax / Mathf.Max(1f, hr.worldBound.height);
                            float bTop = panel.worldBound.yMin / Mathf.Max(1f, br.worldBound.height);
                            bool ok = c.worldBound.height > 0f && cBottom <= bTop + 0.0005f && c.worldBound.xMax <= hr.worldBound.width + 0.5f;
                            all &= ok;
                            lines.Add($"{r.x}x{r.y}@{scale:0.0}:{cBottom:P1}≤{bTop:P1}{(ok ? string.Empty : "✗")}");
                        }
                        finally
                        {
                            Object.DestroyImmediate(hgo);
                            Object.DestroyImmediate(bgo);
                        }
                    }
                }
                Expect(all, "C5 建造模式里那一行在四种分辨率 × UI 缩放 100% / 150% 下都在建造栏顶边之上、不超出屏幕：" + string.Join("；", lines));

                string probe = UiToolkitLayoutProbe.Probe(HudUxml, "RaidWarnRoot", stressFill: true, prepare: rr =>
                {
                    foreach (string n in new[] { "RaidWarnPanel", "RaidSpecBar", "RaidTurretPanel" })
                    {
                        rr.panel.visualTree.Q<VisualElement>(n)?.RemoveFromClassList("uk-hidden");
                    }
                    foreach (Label lb in rr.panel.visualTree.Query<Label>().ToList())
                    {
                        lb.text = "超长文字压测超长文字压测超长文字压测超长文字压测超长文字压测超长文字压测";
                    }
                    foreach (Button b in rr.panel.visualTree.Query<Button>().ToList())
                    {
                        b.text = "按钮文字压测";
                    }
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "C6 突袭条布局探针（整条 + 观战栏 + 接入炮塔读数；四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                if (opened)
                {
                    HomeValleyBuildMode.Current?.Close();
                }
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── E 防御总览：覆盖、薄弱点、外围检查（FGR-DEF-070）────────────────────────

        private static void CheckOverviewCoverage()
        {
            CampaignState s = NewWorld(6321);
            Vector2 core = CoreCenter(s);
            BuildingRecord t1 = PlaceAt(s, L, core + new Vector2(10f, 0f));
            BuildingRecord t2 = PlaceAt(s, L, core + new Vector2(16f, 2f));
            WorldSimulation.StepMany(2);
            if (t1 == null || t2 == null)
            {
                Fail("E 准备：找不到放炮塔的地方");
                return;
            }
            float r1 = TurretService.RangeOfTurret(s, t1.BuildingId);
            float r2 = TurretService.RangeOfTurret(s, t2.BuildingId);
            DefenseCoverageMap map = DefenseOverviewService.BuildCoverage(s);
            // 逐格对照：随便取栅格里的一批格，覆盖数 = 射程圆包含它的炮塔数（直接按定义算）。
            int checkedCells = 0, wrong = 0;
            for (int y = map.Oy; y < map.Oy + map.H; y += 3)
            {
                for (int x = map.Ox; x < map.Ox + map.W; x += 3)
                {
                    int expect = (Vector2.Distance(new Vector2(x, y), t1.Position) <= r1 ? 1 : 0) + (Vector2.Distance(new Vector2(x, y), t2.Position) <= r2 ? 1 : 0);
                    if (DefenseOverviewService.CoverageAt(map, x, y) != expect)
                    {
                        wrong++;
                    }
                    checkedCells++;
                }
            }
            bool ringMode = map.RingMode && map.RouteXs.Count == 0;
            bool ringWeak = map.Weak.Count > 0 && map.Weak.All(w => w.Wave == 0 && !string.IsNullOrEmpty(w.Direction) && w.Run.Cells >= DefenseOverviewService.WeakMinCells)
                            && DefenseOverviewService.WeakText(map, 0).Contains("家园外围");
            Expect(r1 > 0f && r2 > 0f && checkedCells > 100 && wrong == 0 && map.Counted == 2 && map.Covered > 0,
                $"E1 覆盖热力图：两座炮塔（射程 {r1:0.#} / {r2:0.#} 米）按射程圆叠加，抽查 {checkedCells} 格覆盖数与定义逐格一致（错 {wrong}）；计入 {map.Counted} 座；范围 {map.W}×{map.H} 格");
            Expect(ringMode && ringWeak,
                $"E2 没有预测路线时按家园外围一圈检查（半径 {map.RingRadius:0}）：炮塔只在一侧，另一侧列出 {map.Weak.Count} 处薄弱点（“{Short(DefenseOverviewService.WeakText(map, 0))}”）");

            // 有预测路线：剧情突袭排定、预警 → 沿排定路线找没有覆盖的区段；在第一处薄弱点放一座炮塔 → 那一处消失（防线变了，结果跟着变）。
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            GameClock.SkipForTests(s, Math.Max(0, grace - GameClock.Ticks));
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).First();
            StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            GameClock.SkipForTests(s, Math.Max(0, p.WarnTick - 2 - GameClock.Ticks));
            StepUntil(() => p.State >= RaidDirectorService.StateWarned, 20);
            DefenseCoverageMap routed = DefenseOverviewService.BuildCoverage(s);
            bool routeMode = !routed.RingMode && routed.RouteXs.Count >= 1 && routed.RouteCellsWalked > 0;
            bool weakOnRoute = routed.Weak.Count > 0 && routed.Weak.All(w => w.Wave == p.Wave && DefenseOverviewService.CoverageAt(routed, w.Run.MidX, w.Run.MidY) == 0)
                               && DefenseOverviewService.WeakText(routed, 0).Contains("预测路线") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseFirstWeakPoint);
            Expect(routeMode && weakOnRoute,
                $"E3 薄弱点（FGR-DEF-070“沿预测的突袭路线标出火力覆盖不到的区段”）：已预警的第 {p.Wave} 波排定路线在家园范围里走了 {routed.RouteCellsWalked} 格，找到 {routed.Weak.Count} 处没有覆盖的区段（“{Short(DefenseOverviewService.WeakText(routed, 0))}”）");
            if (routed.Weak.Count > 0)
            {
                WeakPoint w0 = routed.Weak[0];
                BuildingRecord fix = PlaceAt(s, L, new Vector2(w0.Run.MidX, w0.Run.MidY), 6f);
                WorldSimulation.StepMany(2);
                DefenseCoverageMap after = DefenseOverviewService.BuildCoverage(s);
                bool filled = fix != null && DefenseOverviewService.CoverageAt(after, w0.Run.MidX, w0.Run.MidY) > 0
                              && !after.Weak.Any(w => Vector2.Distance(new Vector2(w.Run.MidX, w.Run.MidY), new Vector2(w0.Run.MidX, w0.Run.MidY)) < 2f);
                Expect(filled && after.Key != routed.Key, $"E4 在第一处薄弱点旁放一座炮塔：那一格有了覆盖、那一处薄弱点消失（剩 {after.Weak.Count} 处）；覆盖键随防线变化（{after.Key != routed.Key}）");
            }
            else
            {
                Fail("E4 前提：E3 没有找到薄弱点");
            }
        }

        private static void CheckOverviewPanel()
        {
            CampaignState s = NewWorld(6322);
            Vector2 core = CoreCenter(s);
            BuildingRecord t1 = PlaceAt(s, L, core + new Vector2(9f, 3f));
            BuildingRecord t2 = PlaceAt(s, L, core + new Vector2(-9f, -3f));
            BuildingRecord trap = PlaceAt(s, "trap_emitter", core + new Vector2(0f, 12f));
            List<MachineRecord> ms = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && MachineRoster.IsHome(m)).ToList();
            bool garrisonSet = ms.Count > 0 && MachineRoster.TrySetRole(s, ms[0].LogicId, MachineRole.Garrison, out _);
            WorldSimulation.StepMany(4);
            if (t1 == null || t2 == null)
            {
                Fail("E 准备：找不到放炮塔的地方");
                return;
            }
            TurretService.Find(s, t1.BuildingId).KillCount = 5;
            DefenseOverviewService.CollectRows(s, new List<DefenseRow>(), out DefenseSummary sum);
            VisualElement root = Mount(OverviewUxml, out GameObject go);
            DefenseOverviewPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<DefenseOverviewPanelUIToolkit>();
                panel.BindView(root);
                panel.SetOpen(true);
                int turretRows = Enumerable.Range(0, panel.RowsShown).Count(i => panel.ShownRow(i).Kind == DefenseRowKind.Turret);
                DefenseRow k5 = Enumerable.Range(0, panel.RowsShown).Select(panel.ShownRow).FirstOrDefault(r => r.Id == t1.BuildingId);
                bool garrisonRow = !garrisonSet || Enumerable.Range(0, panel.RowsShown).Any(i => panel.ShownRow(i).Kind == DefenseRowKind.Garrison && panel.ShownRow(i).Text.Contains("驻防"));
                bool trapRow = trap == null || Enumerable.Range(0, panel.RowsShown).Any(i => panel.ShownRow(i).Kind == DefenseRowKind.Trap);
                bool listOk = panel.PanelVisible && turretRows == 2 && k5 != null && k5.Kills == 5 && k5.Text.Contains("击毁 5") && k5.Text.Contains(TurretCatalog.ModeName(TurretService.Find(s, t1.BuildingId).TargetMode))
                              && garrisonRow && trapRow && panel.SummaryText.Contains("炮塔 2") && sum.Turrets == 2 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DefenseOverviewFirstOpen)
                              && panel.HeatTexture != null && panel.HeatTexture.width == panel.Coverage.W && panel.StatsText.Contains("计入 2 座");
                Color32 atTurret = panel.HeatTexture.GetPixel(Mathf.RoundToInt(t1.Position.x) - panel.Coverage.Ox, Mathf.RoundToInt(t1.Position.y) - panel.Coverage.Oy);
                bool whiteDot = atTurret.r > 240 && atTurret.g > 240 && atTurret.b > 240;
                Expect(listOk && whiteDot,
                    $"E5 FGU-27 防御总览（真 UXML）：汇总“{panel.SummaryText}”；列表 {panel.RowsShown} 行（炮塔 {turretRows} 行写状态、补给、目标模式、击毁 5；陷阱 {trapRow}；驻防机器 {garrisonRow}）；热力图 {panel.Coverage.W}×{panel.Coverage.H}、炮塔处白点（{whiteDot}）");

                // 筛选：只看炮塔 / 只看有问题的。
                panel.SetFilter(1);
                bool onlyTurrets = panel.RowsShown == 2 && Enumerable.Range(0, panel.RowsShown).All(i => panel.ShownRow(i).Kind == DefenseRowKind.Turret);
                panel.SetFilter(4);
                bool onlyProblems = Enumerable.Range(0, panel.RowsShown).All(i => panel.ShownRow(i).Problem) && (panel.RowsShown > 0 || panel.EmptyText == GameText.Get("defov.empty_filter"));
                panel.SetFilter(0);
                // 批量目标模式：选中即生效，全部炮塔改成同一模式；下拉回到提示项。
                int targetCode = TurretCatalog.Modes[2].Code;
                panel.SelectBatch(3);
                bool batch = TurretService.All(s).All(r => r.TargetMode == targetCode) && panel.BatchField.index == 0 && panel.MessageText.Length > 0;
                // 补给优先开关：没有战时预案时新建一条只开这一项的（可追溯：常驻规则里有它）。
                panel.ClickBoost();
                StandingRuleRecord war = StandingRuleService.Ordered(s).FirstOrDefault(r => r.Kind == StandingRuleService.KindWar);
                bool boost = war != null && war.BoostDefenseSupply && !war.BoostRepair && war.Enabled && panel.BoostText.Contains("开");
                Expect(onlyTurrets && onlyProblems && batch && boost,
                    $"E6 筛选（只看炮塔 {onlyTurrets} / 只看有问题的 {onlyProblems}）；批量目标模式选中即应用到全部炮塔（{batch}，“{panel.MessageText}”）；“突袭时炮塔 / 陷阱补给优先”新建战时预案（{boost}，“{panel.BoostText}”）");

                // 点一行打开炮塔面板；“定位”镜头飞过去；点薄弱点镜头飞过去并收起总览。
                int fly0 = WorldView.FlyCount;
                int turretIdx = Enumerable.Range(0, panel.RowsShown).First(i => panel.ShownRow(i).Kind == DefenseRowKind.Turret);
                // 审查修复（P2）：真炮塔面板（真 UXML）作为总览的子页盖在上面——总览不关、暂时隐藏；Esc 关掉炮塔面板后回到总览。
                VisualElement troot = Mount(UiKitFolder + "TurretPanel.uxml", out GameObject tgo);
                bool open;
                try
                {
                    var turretPanel = tgo.AddComponent<TurretPanelUIToolkit>();
                    turretPanel.BindView(troot);
                    bool clicked = panel.ClickRow(turretIdx);
                    bool stacked = TurretPanelUIToolkit.IsOpen && TurretPanelUIToolkit.BuildingId == panel.ShownRow(turretIdx).Id && DefenseOverviewPanelUIToolkit.IsOpen
                                   && ReferenceEquals(UiEscapeStack.CurrentPage, turretPanel) && UiEscapeStack.IsPageSuspended(panel);
                    bool back = UiEscapeStack.CloseTop() && !TurretPanelUIToolkit.IsOpen && DefenseOverviewPanelUIToolkit.IsOpen && ReferenceEquals(UiEscapeStack.CurrentPage, panel);
                    open = clicked && stacked && back;
                    if (!open)
                    {
                        Line($"    E7 子页：点击 {clicked}，叠放 {stacked}，Esc 回总览 {back}");
                    }
                }
                finally
                {
                    TurretPanelUIToolkit.Close();
                    Object.DestroyImmediate(tgo);
                }
                bool locate = panel.ClickLocate(turretIdx) && WorldView.FlyCount == fly0 + 1 && panel.PanelVisible;
                bool weak = panel.WeakShown > 0 && panel.ClickWeak(0) && !panel.PanelVisible && WorldView.FlyCount == fly0 + 2;
                Expect(open && locate && weak, $"E7 点一行打开它的炮塔面板（真 UXML，盖在总览上面、总览不关；Esc 关掉炮塔面板回到总览：{open}）；“定位”镜头飞过去、总览保持打开（{locate}）；点薄弱点镜头飞过去并收起总览（{weak}）");

                // 空状态 / 不在家园。
                CampaignState empty = NewWorld(6323);
                panel.SetOpen(true);
                panel.Refresh(force: true);
                bool emptyText = panel.EmptyText.Contains("建造菜单") && panel.StatsText == GameText.Get("defov.heat.none");
                panel.SetOpen(false);
                Expect(emptyText, $"E8 空状态：没有防御设施时写明怎么建（“{Short(panel.EmptyText)}”），热力图写“没有覆盖”（“{panel.StatsText}”）");

                string probe = UiToolkitLayoutProbe.Probe(OverviewUxml, "DefOvWindow", stressFill: true, prepare: rr =>
                {
                    foreach (string n in new[] { "DefOvRoot", "DefOvMessage", "DefOvEmpty" })
                    {
                        rr.panel.visualTree.Q<VisualElement>(n)?.RemoveFromClassList("uk-hidden");
                    }
                    foreach (Label lb in rr.panel.visualTree.Query<Label>().ToList())
                    {
                        lb.text = "超长文字压测超长文字压测超长文字压测超长文字压测超长文字压测超长文字压测超长文字压测";
                    }
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "E9 防御总览布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                DefenseOverviewPanelUIToolkit.InWorldOverrideForTests = false;
                if (DefenseOverviewPanelUIToolkit.IsOpen)
                {
                    DefenseOverviewPanelUIToolkit.Close();
                }
                Object.DestroyImmediate(go);
            }
        }

        // ── F 战时预案的“突袭时炮塔 / 陷阱补给优先”（承接 DEBT-FG4ECO06-03）──────────

        private static BuildingRecord WetTurret(CampaignState s, out GridCell near, out GridCell far)
        {
            near = default;
            far = default;
            BlueprintVersionRecord version = BlueprintCircuitBoard.CreateDefault(CarrierReadings.FixedChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_coolant" }).ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != BpWet)
                .Append(new BlueprintRecord { BlueprintId = BpWet, DisplayName = "冷却炮塔", ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            for (float d = 8f; d <= 22f; d += 1f)
            {
                GridCell? o = F.FindArea(s, 6, 4, d, d);
                if (!o.HasValue)
                {
                    continue;
                }
                GridCell pivot = F.At(o.Value, 3, 1);
                if (!HomeGridService.ValidatePlacement(s, L, pivot, 0, asPlayerPlacement: false, checkCost: false).Ok)
                {
                    continue;
                }
                BuildingRecord b = Register(s, L, pivot);
                if (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered)
                {
                    s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                    HomeGridService.MapFor(s);
                    continue;
                }
                var cells = new List<GridCell>();
                HomeGridService.FootprintOf(b, cells);
                int minX = cells.Min(x => x.X);
                int y = cells.Where(x => x.X == minX).Min(x => x.Y);
                near = new GridCell(minX - 1, y);
                far = new GridCell(minX - 2, y);
                if (!HomeGridService.ValidatePipeCell(s, near, PipePieceKind.Pipe).Ok || !HomeGridService.ValidatePipeCell(s, far, PipePieceKind.Pipe).Ok)
                {
                    s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                    HomeGridService.MapFor(s);
                    continue;
                }
                TurretService.TryAssignBlueprint(s, b.BuildingId, BpWet);
                TurretService.Sync(s);
                PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
                PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
                return b;
            }
            return null;
        }

        private static int CoolantFluid()
        {
            var needs = new List<TurretFluidNeed>(2);
            TurretCatalog.FluidNeeds(new[] { "fw_coolant" }, needs);
            return needs.Count > 0 ? needs[0].FluidId : 0;
        }

        /// <summary>一座接了冷却液管线的炮塔与一个优先级 2 的竞争消费者抢同一份很少的冷却液：返回炮塔分到的毫升数与炮塔消费者的优先级。</summary>
        private static long Contest(int seed, bool boost, out int prio, out string supplyLine, out string log, out int prioAfter)
        {
            prio = -1;
            prioAfter = -1;
            supplyLine = string.Empty;
            log = string.Empty;
            CampaignState s = NewWorld(seed);
            BuildingRecord t = WetTurret(s, out GridCell near, out GridCell far);
            if (t == null)
            {
                return -1;
            }
            StandingRuleService.TryCreate(s, StandingRuleService.KindWar, out StandingRuleRecord war, out _);
            StandingRuleService.TrySetBoost(s, war.Serial, false, out _);
            StandingRuleService.TrySetDefenseSupplyBoost(s, war.Serial, boost, out _);
            Seconds(1f);
            TurretRecord r = TurretService.Find(s, t.BuildingId);
            int coolant = CoolantFluid();
            int rival = PipeNetworkService.Kernel.AddConsumer(far.X, far.Y, coolant, 6000, 2);
            PipeNetworkService.Kernel.SetConsumerBuffer(rival, 10_000_000, 0);
            // 突袭到达家园（真实到达；战时预案按到达判定启动）。
            TransitGroupRecord g = Incoming(s, 4);
            StepUntil(() => g.State == TransitGroupState.Arrived, 90);
            StepUntil(() => war.Active, 10);
            Seconds(1.5f);
            prio = r.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(r.ConsumerIds[0]) : -1;
            supplyLine = TurretService.SupplyLine(s, t.BuildingId);
            log = string.Join("|", StandingRuleService.LogEntries(s).Select(e => StandingRuleService.Expand(s, e.Key, e.Args)));
            long before = r.ConsumerIds.Length > 0 && PipeNetworkService.Kernel.TryGetConsumer(r.ConsumerIds[0], out PipeConsumerInfo i0) ? i0.TotalDeliveredMl : 0;
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, coolant, 1500, 1500);
            Seconds(3f);
            long after = r.ConsumerIds.Length > 0 && PipeNetworkService.Kernel.TryGetConsumer(r.ConsumerIds[0], out PipeConsumerInfo i1) ? i1.TotalDeliveredMl : 0;
            PipeNetworkService.Kernel.RemoveProducer(feed, out _);
            // 突袭结束 → 恢复默认优先级。
            Annihilate(s);
            StepUntil(() => !war.Active, 10);
            Seconds(1.5f);
            prioAfter = r.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(r.ConsumerIds[0]) : -1;
            return after - before;
        }

        private static void CheckSupplyBoost()
        {
            long boosted = Contest(6331, true, out int prioOn, out string lineOn, out string logOn, out int prioOnAfter);
            long normal = Contest(6331, false, out int prioOff, out string lineOff, out _, out int prioOffAfter);
            bool prio = prioOn == PipeConst.PriorityMin && prioOff == TurretCatalog.PipePriority && prioOnAfter == TurretCatalog.PipePriority && prioOffAfter == TurretCatalog.PipePriority;
            bool behavior = boosted > normal && boosted > 0;
            bool traced = lineOn.Contains("补给优先") && lineOn.Contains("R") && !lineOff.Contains("补给优先") && logOn.Contains("提到最高");
            Expect(prio && behavior && traced,
                $"F1 DEBT-FG4ECO06-03 战时预案“突袭时炮塔 / 陷阱补给优先”：突袭到达后炮塔的管线消费者优先级 {prioOn}（默认 {TurretCatalog.PipePriority}，关着时 {prioOff}），突袭结束恢复（{prioOnAfter} / {prioOffAfter}）；" +
                $"与优先级 2 的竞争者抢 1.5 升冷却液：打开时炮塔分到 {boosted} 毫升、关着时 {normal} 毫升；补给一行写明是哪条规则（“{Short(lineOn)}”），规则日志写明提了几个补给口（{traced}）");

            // 存读档：规则的这一项、突袭中提过的优先级都在（管线快照）；读档后接着跑突袭结束照样恢复。
            CampaignState s = NewWorld(6332);
            BuildingRecord t = WetTurret(s, out _, out _);
            if (t == null)
            {
                Fail("F2 准备：找不到放炮塔并铺管线的地方");
                return;
            }
            StandingRuleService.ToggleDefenseSupplyBoost(s, out bool nowOn, out _);
            TransitGroupRecord g = Incoming(s, 4);
            StepUntil(() => g.State == TransitGroupState.Arrived, 90);
            Seconds(2f);
            TurretRecord r = TurretService.Find(s, t.BuildingId);
            int before = r.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(r.ConsumerIds[0]) : -1;
            bool saved = SaveSlot();
            CampaignState l = RestoreSlot(out string fail);
            TurretRecord lr = l != null ? TurretService.Find(l, t.BuildingId) : null;
            int loaded = lr != null && lr.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(lr.ConsumerIds[0]) : -2;
            bool ruleKept = l != null && StandingRuleService.Ordered(l).Any(x => x.Kind == StandingRuleService.KindWar && x.BoostDefenseSupply && x.Active);
            if (l != null)
            {
                Annihilate(l);
                Seconds(2f);
            }
            int restored = lr != null && lr.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(lr.ConsumerIds[0]) : -3;
            Expect(nowOn && saved && before == PipeConst.PriorityMin && loaded == before && ruleKept && restored == TurretCatalog.PipePriority,
                $"F2 存读档：突袭中存档（炮塔补给口优先级 {before}）→ 读档后 {loaded}、战时预案的“补给优先”与执行状态都在（{ruleKept}，{fail}）；读档后突袭结束照样恢复到 {restored}");
        }

        // ── G 预警 / 突袭的音乐与提示音 ─────────────────────────────────────────────

        private static void CheckMusic()
        {
            CampaignState s2 = NewWorld(6342);
            RaidAudioDirector.ResetSession();
            int calm = RaidAudioDirector.Evaluate(s2);
            RaidAudioDirector.Tick(s2, true, 0f, force: true);
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            GameClock.SkipForTests(s2, Math.Max(0, grace - GameClock.Ticks));
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s2, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p2 = RaidDirectorService.Plans(s2).OrderByDescending(x => x.Serial).First();
            StepUntil(() => p2.State >= RaidDirectorService.StateScheduled, 120);
            GameClock.SkipForTests(s2, Math.Max(0, p2.WarnTick - 2 - GameClock.Ticks));
            StepUntil(() => p2.State >= RaidDirectorService.StateWarned, 20);
            RaidAudioDirector.Tick(s2, true, 0f, force: true);
            bool warning = RaidAudioDirector.Phase == RaidAudioDirector.PhaseWarning && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicWarning
                           && RaidAudioDirector.LastSting == RaidAudioDirector.StingWarning;
            TransitGroupRecord found = null;
            StepUntil(() => p2.State >= RaidDirectorService.StateDeparted, 900);
            found = WorldTransitSystem.Find(s2, p2.GroupId);
            bool arrived = found != null && StepUntil(() => found.State == TransitGroupState.Arrived, 1800);
            RaidAudioDirector.Tick(s2, true, 0f, force: true);
            bool raid = arrived && RaidAudioDirector.Phase == RaidAudioDirector.PhaseRaid && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicRaid
                        && RaidAudioDirector.LastSting == RaidAudioDirector.StingRaid;
            StepUntil(() => found.Engaged, 30);
            Annihilate(s2);
            RaidAudioDirector.Tick(s2, true, 0f, force: true);
            bool end = RaidAudioDirector.Phase == RaidAudioDirector.PhaseCalm && RaidAudioDirector.CurrentMusic.Length == 0 && RaidAudioDirector.LastSting == RaidAudioDirector.StingEnd;
            string history = string.Join(">", RaidAudioDirector.History);
            bool assets = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Raw/Audios/Sfx/" + RaidAudioDirector.MusicWarning + ".wav") != null
                          && AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Raw/Audios/Sfx/" + RaidAudioDirector.MusicRaid + ".wav") != null;
            Expect(calm == RaidAudioDirector.PhaseCalm && warning && raid && end && history == "1>2>0" && assets,
                $"G1 卡片“预警和突袭的音乐与音效变化”：平静没有音乐层 → 预警（提示音 {RaidAudioDirector.StingWarning} + 低沉循环）→ 突袭到达（{RaidAudioDirector.StingRaid} + 鼓点循环）→ 结束（{RaidAudioDirector.StingEnd}、音乐停）；阶段 {history}；占位音乐资源在热更包（{assets}）");
        }

        // ── H 面板开着时弹出条不挡点击（DEBT-FG5E2E01-06）──────────────────────────

        private static void CheckToastPassthrough()
        {
            NewWorld(6351);
            VisualElement root = Mount(NotifyUxml, out GameObject go);
            try
            {
                var hud = go.AddComponent<NotificationHudUIToolkit>();
                hud.BindView(root);
                NotificationCenter.Post("raid_warning", "测试弹出条");
                hud.RenderToasts();
                UiToolkitLayoutProbe.ForceLayout(root);
                VisualElement toast = root.Query<VisualElement>(className: "uk-toast").ToList().FirstOrDefault(t => !t.ClassListContains("uk-hidden"));
                bool clickable = toast != null && toast.pickingMode == PickingMode.Position && !hud.ToastPassthrough;
                NotificationHudUIToolkit.ModalOpenOverrideForTests = () => true;
                hud.UpdateToastPassthrough();
                bool through = toast != null && toast.pickingMode == PickingMode.Ignore && hud.ToastPassthrough && toast.ClassListContains("uk-toast-passthrough");
                // 真实拾取：弹出条中心点拾取到的不是弹出条（面板开着时点下去落到下面的面板）。
                VisualElement picked = toast != null && toast.panel != null ? toast.panel.Pick(toast.worldBound.center) : null;
                bool pickThrough = picked == null || (picked != toast && !toast.Contains(picked));
                NotificationHudUIToolkit.ModalOpenOverrideForTests = () => false;
                hud.UpdateToastPassthrough();
                bool back = toast != null && toast.pickingMode == PickingMode.Position;
                Expect(clickable && through && pickThrough && back,
                    $"H1 DEBT-FG5E2E01-06：没有面板时弹出条可以点（{clickable}）；有面板（模态）开着时弹出条半透明、不拦截指针，弹出条中心点拾取落到下面（{through} / {pickThrough}）；面板关掉恢复可点（{back}）");
            }
            finally
            {
                NotificationHudUIToolkit.ModalOpenOverrideForTests = null;
                Object.DestroyImmediate(go);
            }
        }

        // ── I 接入炮塔时的读数（DEBT-FG6DEF01-10）──────────────────────────────────

        private static void CheckTurretStrip()
        {
            CampaignState s = NewWorld(6361);
            BuildingRecord t = PlaceAt(s, L, CoreCenter(s) + new Vector2(8f, 0f));
            WorldSimulation.StepMany(2);
            VisualElement root = Mount(HudUxml, out GameObject go);
            RaidWarningHudUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var hud = go.AddComponent<RaidWarningHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh(force: true);
                bool hidden = !hud.TurretVisible;
                TurretService.StateOf(s).UplinkTurretId = t.BuildingId; // 测试捷径：信号在这座炮塔里（接入流程由 FgTurretSelfCheck I 段覆盖）
                hud.Refresh(force: true);
                TurretService.TryGetReadout(s, t.BuildingId, out TurretReadout ro);
                bool shown = hud.TurretVisible && hud.TurretTitleText.Contains(ro.Name) && hud.TurretHeatText.Contains(Mathf.RoundToInt(ro.OverheatAt).ToString())
                             && hud.TurretHpText.Contains(Mathf.RoundToInt(ro.MaxHealth).ToString()) && hud.TurretSupplyText.Contains(GameText.Get("defov.supply_power_only"))
                             && hud.TurretLeaveButton.text.Length > 0;
                TurretService.StateOf(s).UplinkTurretId = string.Empty;
                hud.Refresh(force: true);
                Expect(hidden && shown && !hud.TurretVisible,
                    $"I1 DEBT-FG6DEF01-10 接入炮塔时左上角显示炮塔读数：“{hud.TurretTitleText}”热量条、耐久条、补给与退出接入按钮（{shown}）；不在炮塔里时收起");
            }
            finally
            {
                TurretService.StateOf(s).UplinkTurretId = string.Empty;
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── J 驻防巡逻（FGR-DEF-043，DEBT-FG4ECO07-02）─────────────────────────────

        private static void CheckPatrol()
        {
            CampaignState s = NewWorld(6371);
            List<MachineRecord> ms = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && MachineRoster.IsHome(m)).ToList();
            if (ms.Count == 0)
            {
                Fail("J 准备：家园里没有机器");
                return;
            }
            foreach (MachineRecord other in ms)
            {
                MachineRoster.TrySetRole(s, other.LogicId, MachineRole.Idle, out _);
            }
            int id = ms[0].LogicId;
            Vector2 core = CoreCenter(s);
            BuildingRecord a = PlaceAt(s, "barrier_t1", core + new Vector2(14f, 0f));
            BuildingRecord b = PlaceAt(s, "barrier_t1", core + new Vector2(-14f, 0f));
            // 负向：不是驻防岗不能巡逻。
            bool refuse = !MachineRoster.TryAddPatrolPoint(s, id, a.BuildingId, out string m1) && m1 == GameText.Get("roster.err.patrol_not_garrison");
            MachineRoster.TrySetRole(s, id, MachineRole.Garrison, out _);
            bool holding = StepUntil(() => HomeValleyWorkOrders.FindActiveOrderForMachine(s, id) is WorkOrderRecord o && o.Kind == WorkOrderKind.Garrison && o.State == WorkOrderState.InProgress, 150);
            bool add1 = MachineRoster.TryAddPatrolPoint(s, id, a.BuildingId, out _);
            bool dup = !MachineRoster.TryAddPatrolPoint(s, id, a.BuildingId, out string m2) && m2.Contains("最后一个点");
            bool add2 = MachineRoster.TryAddPatrolPoint(s, id, b.BuildingId, out _);
            bool badPoint = !MachineRoster.TryAddPatrolPoint(s, id, "home:nope#1", out string m3) && m3 == GameText.Get("roster.err.patrol_bad_point");
            MachineRegistry.TryGetRecord(id, out MachineRecord rec);
            long adv0 = SiegeService.PatrolAdvances;
            CombatSite site = Site;
            bool reachedA = StepUntil(() => site.TryGetMachinePosition(id, out Vector2 p) && Vector2.Distance(p, a.Position) <= 4f, 120);
            bool reachedB = StepUntil(() => site.TryGetMachinePosition(id, out Vector2 p) && Vector2.Distance(p, b.Position) <= 4f, 180);
            bool advanced = SiegeService.PatrolAdvances - adv0 >= 2;
            string text = MachineRoster.PatrolText(s, rec);
            Expect(refuse && holding && add1 && dup && add2 && badPoint && reachedA && reachedB && advanced && text.Contains(" → ") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RosterFirstPatrol),
                $"J1 FGR-DEF-043 驻防巡逻：不是驻防岗不能设（{refuse}）；驻防机器到岗后加两个巡逻点（重复 / 无效点拒绝 {dup} / {badPoint}），按顺序走到巡逻点 1（{reachedA}）再到巡逻点 2（{reachedB}），换点 {SiegeService.PatrolAdvances - adv0} 次；“{Short(text)}”");

            // 存读档：巡逻点与当前去的点都在；读档后接着巡逻。
            int idx = rec.PatrolIndex;
            bool saved = SaveSlot();
            CampaignState l = RestoreSlot(out string fail);
            bool kept = l != null && MachineRegistry.TryGetRecord(id, out MachineRecord lr) && lr.PatrolPoints != null && lr.PatrolPoints.Length == 2 && lr.PatrolIndex == idx;
            long adv1 = SiegeService.PatrolAdvances;
            bool continues = l != null && StepUntil(() => SiegeService.PatrolAdvances > adv1, 240);
            bool cleared = l != null && MachineRoster.TryClearPatrol(l, id, out _) && MachineRegistry.TryGetRecord(id, out MachineRecord cr) && (cr.PatrolPoints == null || cr.PatrolPoints.Length == 0);
            Expect(saved && kept && continues && cleared, $"J2 巡逻存读档：巡逻点与当前去的第 {idx} 个点读档后一致（{kept}，{fail}），读档后接着巡逻（{continues}）；“清空巡逻”回到守点（{cleared}）");

            // 机器名册（真 UXML）：驻防机器的详情里有巡逻行，下拉选中即加点。
            VisualElement root = Mount(RosterUxml, out GameObject go);
            try
            {
                var roster = go.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(root);
                RosterPanelUIToolkit.OpenDetail(id);
                roster.Refresh();
                bool row = roster.PatrolRowVisible && roster.PatrolNoteText == GameText.Get("roster.patrol.none") && roster.PatrolAddField.choices.Count > 2;
                roster.SelectPatrolPoint(1);
                MachineRegistry.TryGetRecord(id, out MachineRecord ur);
                bool picked = ur.PatrolPoints != null && ur.PatrolPoints.Length == 1;
                Expect(row && picked, $"J3 机器名册的驻防机器详情有“巡逻路线”一行（{row}），下拉选中即加一个巡逻点（{picked}）");
            }
            finally
            {
                RosterPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// J4 审查回归（P1）：巡逻点全部失效（突袭打成废墟）——驻防单不再反复“结束→重派”，机器回到驻防点后一直守着（守点不中断）。
        /// </summary>
        private static void CheckPatrolPointsDestroyed()
        {
            CampaignState s = NewWorld(6372);
            int id = PatrolMachine(s, out BuildingRecord a, out BuildingRecord b);
            if (id <= 0)
            {
                return;
            }
            CombatSite site = Site;
            bool reachedA = StepUntil(() => site.TryGetMachinePosition(id, out Vector2 p) && Vector2.Distance(p, a.Position) <= 4f, 120);
            // 两个巡逻点都被打成废墟（测试捷径：直接改施工状态；突袭拆墙由攻城自检覆盖）。
            a.ConstructionState = BuildingConstructionState.Damaged;
            b.ConstructionState = BuildingConstructionState.Damaged;
            MachineRegistry.TryGetRecord(id, out MachineRecord rec);
            Seconds(30f); // 回驻防点
            var orderIds = new HashSet<string>();
            int guarding = 0;
            long adv0 = SiegeService.PatrolAdvances;
            for (int i = 0; i < 60; i++)
            {
                Seconds(1f);
                if (!string.IsNullOrEmpty(rec.RoleOrderId))
                {
                    orderIds.Add(rec.RoleOrderId);
                }
                if (HomeValleyWorkOrders.FindActiveOrderForMachine(s, id) is WorkOrderRecord o && o.Kind == WorkOrderKind.Garrison && o.State == WorkOrderState.InProgress
                    && o.TargetId != a.BuildingId && o.TargetId != b.BuildingId)
                {
                    guarding++;
                }
            }
            long advanced = SiegeService.PatrolAdvances - adv0;
            bool stable = orderIds.Count == 1 && guarding == 60 && advanced == 0 && !MachineRoster.HasPatrol(s, rec) && rec.PatrolIndex == 0;
            // 修好一个巡逻点 → 接着巡逻。
            a.ConstructionState = BuildingConstructionState.Operational;
            bool resumes = StepUntil(() => site.TryGetMachinePosition(id, out Vector2 p) && Vector2.Distance(p, a.Position) <= 4f, 150);
            Expect(reachedA && stable && resumes,
                $"J4 负向“巡逻点全部被毁”：机器回到驻防点后 60 秒里只有 {orderIds.Count} 张驻防单（不反复结束 / 重派）、守点 {guarding}/60 秒不中断、没有换点（{advanced}）；" +
                $"修好一个巡逻点后接着巡逻（{resumes}）");
        }

        /// <summary>
        /// J5 审查回归（P1）：巡逻点被悬崖围死（到不了）——跳过它接着巡逻别的点；“到不了”只通知一次（不附驻防的恢复提示），重试期内不再开单；存读档后跳过表还在。
        /// </summary>
        private static void CheckPatrolPointUnreachable()
        {
            CampaignState s = NewWorld(6373);
            GridCell? open = FgNavSelfCheck.FindOpenArea(s, 5, 12, 40);
            if (!open.HasValue)
            {
                Fail("J5 准备：找不到空地放被围死的巡逻点");
                return;
            }
            GameConfig.fg.BuildingGrid bg = GridContent.Building("parts_workshop");
            GridCell center = open.Value;
            BuildingRecord pen = Register(s, "parts_workshop", new GridCell(center.X - bg.FootprintW / 2, center.Y - bg.FootprintH / 2));
            FgNavSelfCheck.CliffRing(s, center, Math.Max(bg.FootprintW, bg.FootprintH) / 2 + 2);
            int id = PatrolMachine(s, out BuildingRecord a, out _, pen);
            if (id <= 0)
            {
                return;
            }
            MachineRegistry.TryGetRecord(id, out MachineRecord rec);
            int Notes() => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == "unreachable").Sum(n => n.Count);
            int PenOrders() => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.Kind == WorkOrderKind.Garrison && o.TargetId == pen.BuildingId);
            int notes0 = Notes();
            int skip0 = MachineRoster.PatrolSkipNotices;
            long adv0 = SiegeService.PatrolAdvances;
            bool skipped = StepUntil(() => MachineRoster.PatrolSkipped(rec, pen.BuildingId), 150);
            int orders1 = PenOrders();
            Seconds(MachineRoster.PatrolRetrySeconds * 0.8f);
            int orders2 = PenOrders();
            bool keepsPatrolling = SiegeService.PatrolAdvances - adv0 >= 2;
            // 过了重试时间再试一次（又失败）：仍只算一条通知。
            Seconds(MachineRoster.PatrolRetrySeconds * 0.6f + 40f);
            int notes = Notes() - notes0;
            int skipNotes = MachineRoster.PatrolSkipNotices - skip0;
            string last = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "unreachable")?.Text ?? string.Empty;
            bool once = notes == 1 && skipNotes == 1 && last.Contains("巡逻点") && !last.Contains(GameText.Get("roster.garrison.resume_hint"));
            bool saved = SaveSlot();
            CampaignState l = RestoreSlot(out string fail);
            bool kept = l != null && MachineRegistry.TryGetRecord(id, out MachineRecord lr) && lr.PatrolSkipIds != null && lr.PatrolSkipIds.Contains(pen.BuildingId);
            Expect(skipped && orders1 >= 1 && orders2 == orders1 && keepsPatrolling && once && saved && kept,
                $"J5 负向“巡逻点到不了”：跳过它（{skipped}），重试期内去那一点的单 {orders1} → {orders2} 不增长、接着在别的点巡逻（换点 {SiegeService.PatrolAdvances - adv0} 次）；" +
                $"重试又失败后“到不了”通知共 {notes} 条（巡逻专用 {skipNotes} 条，不附驻防恢复提示：“{Short(last)}”）；存读档后跳过表还在（{kept}，{fail}）");
        }

        /// <summary>J4 / J5 的准备：其余机器闲置，第一台设驻防并加两个巡逻点（<paramref name="second"/> 给出时第二个点用它）。返回机器 ID（0 = 准备失败）。</summary>
        private static int PatrolMachine(CampaignState s, out BuildingRecord a, out BuildingRecord b, BuildingRecord second = null)
        {
            a = null;
            b = null;
            List<MachineRecord> ms = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && MachineRoster.IsHome(m)).ToList();
            if (ms.Count == 0)
            {
                Fail("巡逻准备：家园里没有机器");
                return 0;
            }
            foreach (MachineRecord other in ms)
            {
                MachineRoster.TrySetRole(s, other.LogicId, MachineRole.Idle, out _);
            }
            int id = ms[0].LogicId;
            Vector2 core = CoreCenter(s);
            a = PlaceAt(s, "barrier_t1", core + new Vector2(14f, 0f));
            b = second ?? PlaceAt(s, "barrier_t1", core + new Vector2(-14f, 0f));
            if (a == null || b == null)
            {
                Fail("巡逻准备：找不到放巡逻点的地方");
                return 0;
            }
            MachineRoster.TrySetRole(s, id, MachineRole.Garrison, out _);
            StepUntil(() => HomeValleyWorkOrders.FindActiveOrderForMachine(s, id) is WorkOrderRecord o && o.Kind == WorkOrderKind.Garrison && o.State == WorkOrderState.InProgress, 150);
            if (!MachineRoster.TryAddPatrolPoint(s, id, a.BuildingId, out string m1) || !MachineRoster.TryAddPatrolPoint(s, id, b.BuildingId, out string m2))
            {
                Fail("巡逻准备：加巡逻点失败");
                return 0;
            }
            return id;
        }

        // ── D4 观战的键盘入口与“暂停中开始观战”（审查修复：FG00 B02、ADR-DEF-006 决策 11）──────────

        private static void CheckSpectateKeysAndPausedStart()
        {
            CampaignState s = NewWorld(6312);
            TransitGroupRecord g = Incoming(s, 7, units: 6);
            NotificationCenter.AutoPauseHandler = PauseHandler;
            GameSettings.ResetNotifyAutoPause();
            try
            {
                bool arrived = StepUntil(() => g.State == TransitGroupState.Arrived, 90);
                bool autoPaused = GameClock.Paused; // 前 3 次突袭：到达那一步自动暂停
                StepUntil(() => g.Engaged, 1);
                int unpause0 = RaidSpectateService.UnpauseStarts;
                float speed0 = GameClock.Speed;
                // 键盘入口与按钮同一个方法（UiKitInputPump：SpectateRaid → Toggle、SpectateFollow → ToggleFollow）。
                bool started = RaidSpectateService.Toggle(s) && RaidSpectateService.Active && !GameClock.Paused && Mathf.Approximately(GameClock.Speed, RaidSpectateService.DefaultSpeed)
                               && RaidSpectateService.UnpauseStarts == unpause0 + 1 && RaidSpectateService.LastMessage.Contains("已继续");
                string startMsg = RaidSpectateService.LastMessage;
                // 观战中暂停 → 切档不解除暂停、不退出观战（决策 2）。
                GameClock.SetPaused(true);
                RaidSpectateService.TryStart(s, 3f, out _);
                bool pausedKept = GameClock.Paused && RaidSpectateService.Active && RaidSpectateService.UnpauseStarts == unpause0 + 1;
                GameClock.SetPaused(false);
                bool followOff = RaidSpectateService.ToggleFollow(s) && !RaidSpectateService.Following && RaidSpectateService.Active;
                bool followOn = RaidSpectateService.ToggleFollow(s) && RaidSpectateService.Following;
                bool stopped = RaidSpectateService.Toggle(s) && !RaidSpectateService.Active && Mathf.Approximately(GameClock.Speed, speed0);
                // 没在观战时按“跟随战斗” = 开始观战并跟随。
                bool followStarts = RaidSpectateService.ToggleFollow(s) && RaidSpectateService.Active && RaidSpectateService.Following;
                RaidSpectateService.Toggle(s);
                Annihilate(s);
                bool denied = !RaidSpectateService.Toggle(s) && !RaidSpectateService.ToggleFollow(s) && RaidSpectateService.LastMessage == GameText.Get("raid.spec.reason.no_raid");
                Expect(arrived && autoPaused && started && pausedKept && followOff && followOn && stopped && followStarts && denied,
                    $"D4 观战的键盘入口（Alt+V / Ctrl+F，与观战栏按钮同一入口）：突袭到达自动暂停（{autoPaused}）后开始观战 = 一并继续并写明（{started}，“{startMsg}”）；" +
                    $"观战中暂停再切档不解除暂停（{pausedKept}）；跟随开关（{followOff}/{followOn}）；再按一次停止并恢复 {speed0}x（{stopped}）；没在观战时按跟随键 = 开始观战并跟随（{followStarts}）；" +
                    $"没有突袭时两个键都拒绝并写明原因（{denied}）");
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = null;
                RaidSpectateService.ResetSession();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        // ── E10 防御总览“补给优先”一键开关：显示的规则与切换的规则同一口径（审查修复 P2）──────────

        private static void CheckBoostToggleTwoRules()
        {
            CampaignState s = NewWorld(6324);
            StandingRuleService.TryCreate(s, StandingRuleService.KindWar, out StandingRuleRecord r1, out _);
            StandingRuleService.TryCreate(s, StandingRuleService.KindWar, out StandingRuleRecord r2, out _);
            StandingRuleService.TrySetDefenseSupplyBoost(s, r2.Serial, true, out _);
            bool shown2 = StandingRuleService.DefenseSupplyBoostConfigured(s, out int serial) && serial == r2.Serial && r1.Enabled && !r1.BoostDefenseSupply;
            // 显示“开（R2）”→ 点一下 = 关掉（R1 不被打开）。
            bool off = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on1, out string msgOff) && !on1 && !r2.BoostDefenseSupply && !r1.BoostDefenseSupply
                       && !StandingRuleService.DefenseSupplyBoostConfigured(s, out _);
            // 显示“关”→ 打开第一条启用的（R1）；再点 = 关掉。
            bool onFirst = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on2, out _) && on2 && r1.BoostDefenseSupply && !r2.BoostDefenseSupply;
            bool offAgain = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on3, out _) && !on3 && !r1.BoostDefenseSupply;
            // R1 被玩家停用但带着这一项：显示“关”；点一下打开 R2（第一条启用的），不顺手启用 R1；再点只关 R2，R1 原样（停用 + 带着这一项）。
            StandingRuleService.TrySetDefenseSupplyBoost(s, r1.Serial, true, out _);
            StandingRuleService.TrySetEnabled(s, r1.Serial, false, out _);
            bool shownOff = !StandingRuleService.DefenseSupplyBoostConfigured(s, out _);
            bool picks2 = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on4, out _) && on4 && r2.BoostDefenseSupply && !r1.Enabled;
            bool off2 = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on5, out _) && !on5 && !r2.BoostDefenseSupply && !r1.Enabled && r1.BoostDefenseSupply;
            // 只剩停用的 R1：点一下打开并启用它，提示里写明。
            StandingRuleService.TryDelete(s, r2.Serial, out _);
            bool reenabled = StandingRuleService.ToggleDefenseSupplyBoost(s, out bool on6, out string msgOn) && on6 && r1.Enabled && msgOn.Contains("一并启用");
            Expect(shown2 && off && onFirst && offAgain && shownOff && picks2 && off2 && reenabled,
                $"E10 补给优先一键开关（两条战时预案）：显示“开（R2）”时点一下关掉（{off}，“{Short(msgOff)}”）、不会去打开 R1；显示“关”时打开第一条启用的（{onFirst}），再点关掉（{offAgain}）；" +
                $"停用的 R1 带着这一项时显示“关”（{shownOff}），点一下打开 R2、不顺手启用 R1（{picks2}），再点只关 R2（{off2}）；只剩停用的预案时打开并启用、写明（{reenabled}，“{Short(msgOn)}”）");
        }

        // ── F3 陷阱一侧的补给优先（审查修复 P2）──────────────────────────────────────

        private static void CheckTrapSupplyBoost()
        {
            CampaignState s = NewWorld(6333);
            BuildingRecord trap = null;
            GridCell near = default, far = default;
            for (float d = 9f; d <= 22f && trap == null; d += 1f)
            {
                GridCell? o = F.FindArea(s, 5, 1, d, d);
                if (!o.HasValue)
                {
                    continue;
                }
                BuildingRecord b = Register(s, DefenseCatalog.TrapTypeId, F.At(o.Value, 2, 0));
                if (!HomeValleyPowerGrid.IsConnected(s, b.BuildingId) || b.PowerState != BuildingPowerState.Powered
                    || !HomeGridService.ValidatePipeCell(s, F.At(o.Value, 1, 0), PipePieceKind.Pipe).Ok || !HomeGridService.ValidatePipeCell(s, F.At(o.Value, 0, 0), PipePieceKind.Pipe).Ok)
                {
                    s.BuildingRecords = s.BuildingRecords.Where(x => x != b).ToArray();
                    HomeGridService.MapFor(s);
                    continue;
                }
                trap = b;
                near = F.At(o.Value, 1, 0);
                far = F.At(o.Value, 0, 0);
            }
            if (trap == null)
            {
                Fail("F3 准备：找不到放陷阱发射器并铺管线的地方");
                return;
            }
            if (!MechanicalContentUnlock.IsUnlocked(s, "fw_oilleak"))
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append("fw_oilleak").ToArray(); // 测试捷径：固件已发现（同 FgDefenseStructuresSelfCheck）
            }
            DefenseOpResult fw = DefenseService.TrySetTrapFirmware(s, trap.BuildingId, "fw_oilleak");
            PipeNetworkService.TryPlace(s, near, PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, far, PipePieceKind.Pipe, 0, 0);
            int feed = PipeNetworkService.Kernel.AddProducer(far.X, far.Y, PipeNetworkService.FluidId("fuel"), 20000, 20000);
            Seconds(3f);
            DefenseRecord rec = DefenseService.Find(s, trap.BuildingId);
            int Prio(DefenseRecord r) => r != null && r.ConsumerId > 0 && PipeNetworkService.Kernel.TryGetConsumer(r.ConsumerId, out PipeConsumerInfo i) ? i.Priority : -1;
            int before = Prio(rec);
            StandingRuleService.ToggleDefenseSupplyBoost(s, out bool nowOn, out _);
            TransitGroupRecord g = Incoming(s, 4);
            StepUntil(() => g.State == TransitGroupState.Arrived, 90);
            Seconds(2f);
            int during = Prio(rec);
            string line = DefenseService.TryGetTrapReadout(s, trap.BuildingId, out TrapReadout ro) ? ro.SupplyLine : string.Empty;
            bool saved = SaveSlot();
            CampaignState l = RestoreSlot(out string fail);
            DefenseRecord lr = l != null ? DefenseService.Find(l, trap.BuildingId) : null;
            int loaded = Prio(lr);
            if (l != null)
            {
                Annihilate(l);
                Seconds(2f);
            }
            int after = Prio(lr);
            PipeNetworkService.Kernel.RemoveProducer(feed, out _);
            Expect(nowOn && before == DefenseCatalog.TrapPipePriority && during == PipeConst.PriorityMin && line.Contains("补给优先") && saved && loaded == during && after == DefenseCatalog.TrapPipePriority,
                $"F3 陷阱发射器的补给优先（装漏油固件 {fw.Ok}{(fw.Ok ? string.Empty : "：" + fw.Message)}）：平时管线消费者优先级 {before}（trap.pipe_priority），突袭期间 {during}，补给一行写明来源规则（“{Short(line)}”）；" +
                $"突袭中存读档后 {loaded}（{fail}），突袭结束恢复到 {after}");
        }

        // ── Q 家园后台运行与观察一致（FGR-BASE-021 / FG00 B24）──────────────────────────

        /// <summary>同一种子、同一组操作（驻防巡逻、炮塔补给优先、突袭到达与自动暂停）在观察 / 不观察家园时跑同样的帧：逐项签名。</summary>
        private static string RunBackground(bool observe, out string detail)
        {
            detail = string.Empty;
            CampaignState s = NewWorld(6396, observe);
            NotificationCenter.AutoPauseHandler = PauseHandler;
            GameSettings.ResetNotifyAutoPause();
            try
            {
                int id = PatrolMachine(s, out _, out _);
                BuildingRecord t = WetTurret(s, out _, out _);
                if (id <= 0 || t == null)
                {
                    return "准备失败";
                }
                StandingRuleService.ToggleDefenseSupplyBoost(s, out _, out _);
                MachineRegistry.TryGetRecord(id, out MachineRecord rec);
                long adv0 = SiegeService.PatrolAdvances;
                TransitGroupRecord g = Incoming(s, 4);
                // 观察：按真实帧推进（含被观察地点的 FrameUpdate 表现）；不观察：无头推进（WorldSimulation.StepMany，镜头不在家园——远征中 / 看别处时家园就是这样跑的）。
                int frames = 0;
                while (!GameClock.Paused && frames < 60 * 300)
                {
                    if (observe)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    else
                    {
                        WorldSimulation.StepMany(1);
                    }
                    frames++;
                }
                long pauseTick = GameClock.Ticks;
                bool observedNow = WorldView.IsObserved(WorldSimulation.Home.SiteId);
                GameClock.SetPaused(false);
                long target = GameClock.Ticks + GameClock.StepHz * 20;
                if (observe)
                {
                    int guard = 0;
                    while (GameClock.Ticks < target && guard++ < 60 * 300)
                    {
                        WorldSimulation.Frame(1f / 60f, target);
                    }
                }
                else
                {
                    WorldSimulation.StepMany((int)(target - GameClock.Ticks));
                }
                TurretRecord tr = TurretService.Find(s, t.BuildingId);
                int prio = tr != null && tr.ConsumerIds.Length > 0 ? TurretService.ConsumerPriorityOf(tr.ConsumerIds[0]) : -1;
                detail = $"观察={observedNow}";
                return $"pause@{pauseTick}|arrived={RaidDirectorService.ArrivedWaves(s)}|g={g.State}|prio={prio}|patrol={rec.PatrolIndex}/{SiegeService.PatrolAdvances - adv0}|{Signature(s)}";
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = null;
                GameClock.SetPaused(false);
            }
        }

        private static void CheckBackgroundEqualsObserved()
        {
            string seen = RunBackground(true, out string d1);
            string unseen = RunBackground(false, out string d2);
            bool ok = seen == unseen && seen.Contains("prio=" + PipeConst.PriorityMin) && seen.Contains("arrived=1") && d1 == "观察=True" && d2 == "观察=False";
            Expect(ok, $"Q1 FGR-BASE-021 家园后台一致：同一种子的突袭到达 + 自动暂停所在步、炮塔补给优先（消费者优先级）、驻防巡逻换点，在观察（{d1}）/ 不观察（{d2}）家园时跑同样的帧逐位一致" +
                       (seen == unseen ? $"（{Short(seen)}）" : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
        }

        // ── L 观战不改模拟：暂停与 0.5x～3x 矩阵 ────────────────────────────────────

        private static string Signature(CampaignState s)
        {
            CombatSite site = Site;
            return SiegeService.Snapshot(s) + "|H" + (site != null ? site.Kernel.StateHash().ToString("X16") : "-") + "|A" + RaidDirectorService.ArrivedWaves(s);
        }

        private static string RunSpectated(int seed, float speed, bool spectate, out bool pausedHeld)
        {
            pausedHeld = true;
            CampaignState s = NewWorld(seed);
            TransitGroupRecord g = Incoming(s, 3, units: 8);
            StepUntil(() => g.Engaged, 90);
            if (spectate)
            {
                RaidSpectateService.TryStart(s, speed, out _);
            }
            GameClock.SetPaused(true);
            string p0 = Signature(s);
            for (int i = 0; i < 60; i++)
            {
                WorldSimulation.Frame(1f / 60f);
                RaidSpectateService.Tick(s, 1f / 60f);
            }
            pausedHeld = Signature(s) == p0;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 20;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 300)
            {
                WorldSimulation.Frame(1f / 60f, target);
                RaidSpectateService.Tick(s, 1f / 60f);
                frames++;
            }
            RaidSpectateService.ResetSession();
            GameClock.SetSpeed(1f);
            return Signature(s);
        }

        private static void CheckSpectateTiming()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                foreach (bool spec in new[] { false, true })
                {
                    string snap = RunSpectated(6381, speed, spec, out bool held);
                    paused &= held;
                    reference ??= snap;
                    if (snap != reference && diff.Length == 0)
                    {
                        diff = $"\n{speed}x 观战={spec}：{Short(snap)}\n参照：{Short(reference)}";
                    }
                    same &= snap == reference;
                }
            }
            Expect(paused && same, "L1 暂停与倍速矩阵：观战中暂停 60 帧攻城不动；0.5x / 1x / 2x / 3x × 观战 / 不观战跑同样的 20 游戏秒攻城逐位一致（观战只改速度与镜头，不改模拟）" + diff);
        }

        // ── P 性能（只测一次，超线不到 2 倍记警告）──────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6391);
            Vector2 core = CoreCenter(s);
            int placed = 0;
            for (int ring = 0; ring < 8 && placed < 100; ring++)
            {
                for (int k = 0; k < 24 && placed < 100; k++)
                {
                    float ang = (k * 15f + ring * 7f) * Mathf.Deg2Rad;
                    float d = 10f + ring * 6f;
                    if (PlaceAt(s, L, core + new Vector2(Mathf.Cos(ang) * d, Mathf.Sin(ang) * d), 2f) != null)
                    {
                        placed++;
                    }
                }
            }
            WorldSimulation.StepMany(2);
            DefenseOverviewService.BuildCoverage(s); // 预热（JIT）
            var sw = Stopwatch.StartNew();
            const int Runs = 5;
            DefenseCoverageMap map = null;
            for (int i = 0; i < Runs; i++)
            {
                map = DefenseOverviewService.BuildCoverage(s, map);
            }
            sw.Stop();
            double perBuild = sw.Elapsed.TotalMilliseconds / Runs;
            var rows = new List<DefenseRow>();
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < Runs; i++)
            {
                DefenseOverviewService.CollectRows(s, rows, out _);
            }
            sw2.Stop();
            double perRows = sw2.Elapsed.TotalMilliseconds / Runs;
            // HUD：200 台攻城单位时一次战况刷新（各波剩余敌人 + 文字）。
            TransitGroupRecord g = Incoming(s, 3, units: 200);
            StepUntil(() => g.Engaged, 90);
            RaidHudService.TryWaveFight(s, 0, out _);
            var sw3 = Stopwatch.StartNew();
            const int HudRuns = 200;
            for (int i = 0; i < HudRuns; i++)
            {
                RaidHudService.TryWaveFight(s, 0, out _);
                RaidHudService.FightFocus(s, out _);
            }
            sw3.Stop();
            double perHud = sw3.Elapsed.TotalMilliseconds / HudRuns;
            double budget = DefenseOverviewService.PerfBudgetMs;
            double hudBudget = RaidHudService.PerfBudgetMs;
            string cpu = $"{SystemInfo.processorType.Trim()}，{SystemInfo.processorCount} 线程";
            PerfLines.Add($"防御总览重算覆盖 + 薄弱点（{placed} 座炮塔，{map.W}×{map.H} 格）平均 {perBuild:F3} ms（阈值 {budget} ms）；列表收集 {rows.Count} 行 {perRows:F3} ms；" +
                          $"突袭 HUD 战况刷新（{Alive(g)} 台攻城单位）{perHud:F4} ms（阈值 {hudBudget} ms）；Editor batchmode 影子工程，{cpu}；真机由 FG15-SYS-02 补测");
            bool buildOk = perBuild <= budget * 2;
            bool hudOk = perHud <= hudBudget * 2;
            if (perBuild > budget && buildOk)
            {
                PerfLines.Add($"性能警告：防御总览重算 {perBuild:F3} ms 超过阈值 {budget} ms（{perBuild / budget:F2} 倍，不到 2 倍）");
            }
            if (perHud > hudBudget && hudOk)
            {
                PerfLines.Add($"性能警告：突袭 HUD 刷新 {perHud:F4} ms 超过阈值 {hudBudget} ms（{perHud / hudBudget:F2} 倍，不到 2 倍）");
            }
            Expect(placed >= 60 && buildOk && hudOk,
                $"P1 性能：{placed} 座炮塔时防御总览重算 {perBuild:F3} ms（阈值 {budget} ms，2 倍以内算通过）；200 台攻城单位时 HUD 战况刷新 {perHud:F4} ms（阈值 {hudBudget} ms）");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

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

        private static void Expect(bool ok, string message)
        {
            if (ok)
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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
