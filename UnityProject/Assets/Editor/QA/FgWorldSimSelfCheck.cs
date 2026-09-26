using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-01 整个世界同时运行（含统一时钟底座）的自动验收（FG14 FGR-ARC-002、009 与第 4 节通过标准；FG00 FGR-BASE-021；
    /// FG07 FGR-ENV-001；FG17 FGR-GEN-052 第 1、2 条、FGR-GEN-080 镜头飞跃）。全部起真实系统：真实的三个区域控制器（真实 Enter、
    /// 真实表现对象）、真实派遣事务（ExpeditionDepartureService.TryDepart）、真实撤离 / 放弃事务、真实存读档（写文件再读回）、
    /// 真实全局镜头（CameraDirector 绑真实 Camera）、真实输入路径（假键盘 → InputRouter → 动作），行为坏了会失败：
    /// A 数据：调参行、输入动作（3x / 回到核心已接入、切换关注点新增且可重绑）、文本中英齐全。
    /// B 统一时钟：固定步长；0.5x / 1x / 2x / 3x 每真实秒的步数；暂停零步且保留零头；接入锁 1x；单帧上限与超长帧截断；
    ///   第 N 日 HH:MM；存读档往返与步长频率变化时按游戏秒换算；通知时间用游戏日。
    /// C 整个世界同时运行（核心）：家园（生产 + 修复工单）+ 一支经真实派遣事务出发的远征队 + 一支行进中的突袭，同一个存档出发跑
    ///   30 分钟游戏时间三遍——A 镜头在三者之间飞跃 100 次以上、中途多次暂停与切换 0.5x～3x；B 镜头全程只看远征地点（家园从不被观察）；
    ///   C 完全不经过镜头（无头推进）。三遍结束时存档状态逐字段一致（FGR-BASE-021），并且家园在不被观察时真的推进了（生产完成、工单完成）。
    /// D 暂停与倍速矩阵：整个世界（家园、远征、突袭）同时停止；0.5x～3x 下同一段游戏时间结果与 1x 逐字段一致，真实时间按倍速缩放。
    /// E 负向：镜头飞跃途中存读档（不复制 / 不丢实体，时间轴不丢，恢复到远征地点）；远征全灭时镜头不被强行拉走、状态条提示、
    ///   确认放弃后回到家园；重复派遣被拒并给原因；已结束的远征地点不能定位且给原因；观察中的地点卸载后自动回家园。
    /// F 归属与定位：远征里发生的通知记在远征地点（镜头在家园时也一样），点击定位会跨地点飞过去；特效只画观察中的地点。
    /// G 种子无关（B25）：突袭出发点由规划层按种子派生，多个种子下都能到达；镜头范围由已探索区域推导。
    /// H 活跃区块：己方实体 / 行进中的队伍 / 被观察窗口；镜头离开星球后窗口不再计入。
    /// I 输入：Space 暂停整个世界、4 键 3x、Tab 依次切换关注点、Home 回到归还核心（全部走可重绑动作）。
    /// J 世界时间条：真 UXML + 布局探针（中英文 × UI 缩放）、按钮真回调改变统一时钟、关注点按钮飞镜头。
    /// K 性能：单步耗时（家园 + 远征 + 突袭）、热更层每帧开销与行进队伍数量无关。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgWorldSimSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const int RunSlot = 1;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static FakeReader _reader;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 整个世界同时运行")]
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
            Line("\n[整个世界] 整个世界同时运行与统一时钟（FG0-ARCH-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<string> originalRegion = NotificationCenter.RegionProvider;
            NotificationCenter.LocateDelegate originalLocate = NotificationCenter.LocateHandler;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            Func<string> originalCueSite = FeedbackCues.SiteProvider;
            Func<string> originalCueObserved = FeedbackCues.ObservedSiteProvider;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsim-selfcheck-" + Guid.NewGuid().ToString("N"));
            _reader = new FakeReader();
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
                InputRouter.DebugSetReader(_reader);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     $"编辑器编辑模式下直接起真实区域控制器（表现对象真实创建），没有渲染帧——性能数字是 Editor 托管代码，真机 IL2CPP 通常更快");

                Step(CheckData);
                Step(CheckClock);
                Step(CheckClockSave);
                Step(CheckWorldRunsTogether);
                Step(CheckPauseAndSpeedMatrix);
                Step(CheckFlightSaveLoad);
                Step(CheckAutoSaveWritesLivePositions);
                Step(CheckExpeditionWipeCamera);
                Step(CheckAttributionAndLocate);
                Step(CheckSeedIndependence);
                Step(CheckActivity);
                Step(CheckInput);
                Step(CheckWorldBar);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"整个世界自检抛异常：{e}");
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
                NotificationCenter.RegionProvider = originalRegion;
                NotificationCenter.LocateHandler = originalLocate;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                FeedbackCues.SiteProvider = originalCueSite;
                FeedbackCues.ObservedSiteProvider = originalCueObserved;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                WorldGenKernel_ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
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
            Line($"  · [整个世界] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void WorldGenKernel_ReleaseAll()
        {
            BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] tunings =
            {
                "clock.sim_step_hz", "clock.max_steps_per_frame", "clock.max_frame_seconds", "clock.day_seconds", "clock.start_hour",
                "clock.objective_recompute_seconds", "sim.activity_refresh_seconds", "camera.fly_seconds", "camera.explored_margin_cells",
                "camera.precision_safe_cells", "transit.raid_speed_cells_per_second", "transit.arrival_radius_cells",
                "camera.zoom_min_ortho", "camera.zoom_max_ortho", "camera.zoom_step",
            };
            var missing = tunings.Where(t => !GridContent.TryGetTuning(t, out _)).ToList();
            GridContent.TryGetTuning("clock.day_seconds", out float day);
            GridContent.TryGetTuning("camera.fly_seconds", out float fly);
            Expect(missing.Count == 0 && Mathf.Approximately(day, 1200f) && Mathf.Approximately(fly, 0.5f) && GameClock.StepHz == 60,
                $"调参行全部在 fg.TbHomeTuning（缺 {missing.Count}）；1 游戏日 = {day} 秒（FGR-ENV-001 20 分钟）、飞跃 {fly} 秒（FG17）、固定步长 {GameClock.StepHz} Hz");

            InputActionCatalog.TryGet(GameActionId.SpeedTriple, out InputActionDef triple);
            InputActionCatalog.TryGet(GameActionId.FocusHomeCore, out InputActionDef home);
            InputActionCatalog.TryGet(GameActionId.CycleWorldFocus, out InputActionDef cycle);
            Expect(triple != null && triple.Status == InputActionStatus.Wired && home != null && home.Status == InputActionStatus.Wired
                   && cycle != null && cycle.Status == InputActionStatus.Wired && GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus) == KeyCode.Tab
                   && GameSettings.KeyBindings.GetKey(GameActionId.SpeedTriple) == KeyCode.Alpha4 && GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore) == KeyCode.Home,
                "输入动作：速度 3x（4）、回到归还核心（Home）已接入玩法；新增“切换关注点”（Tab，战略上下文）登记在 fg.TbInputAction，可重绑");

            string[] keys =
            {
                "ui.world.day_time", "ui.world.pause", "ui.world.resume", "ui.world.status_paused", "ui.world.status_running", "ui.world.status_direct_locked",
                "ui.world.tip.time", "ui.world.tip.speed", "ui.world.tip.pause", "ui.world.tip.direct_locked", "ui.world.focus.title", "ui.world.focus.home",
                "ui.world.focus.expedition", "ui.world.focus.raid", "ui.world.focus.raid_arrived", "ui.world.focus.current", "ui.world.focus.tip",
                "ui.world.eta", "ui.world.site_unloaded", "ui.world.expedition_wiped_here", "ui.world.placeholder", "world.site.home_valley.name",
                "world.site.silent_ruins.name", "world.site.foundry_outpost.name", "world.transit.raid.label", "world.transit.raid.arrived_detail",
                "expedition.reason.underway", "input.action.cycle_world_focus.name",
            };
            var bad = new List<string>();
            foreach (string k in keys)
            {
                if (!GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                    || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    bad.Add(k);
                }
            }
            Expect(bad.Count == 0, $"{keys.Length} 个新文本键中英齐全（缺：{string.Join("、", bad)}）");
        }

        // ── B 统一时钟 ───────────────────────────────────────────────────────────

        private static void CheckClock()
        {
            GameClock.ResetSession();
            GameClock.ReloadTuning();
            var steps = new List<string>();
            bool ok = true;
            foreach (float speed in GameClock.Speeds)
            {
                GameClock.ResetSession();
                GameClock.SetSpeed(speed);
                int total = 0;
                for (int i = 0; i < 60; i++)
                {
                    total += GameClock.Advance(1f / 60f);
                }
                steps.Add($"{speed}x→{total}");
                ok &= Math.Abs(total - Mathf.RoundToInt(60 * speed)) <= 1;
            }
            Expect(ok && GameClock.Speeds.SequenceEqual(new[] { 0.5f, 1f, 2f, 3f }) && StrategyClock.AllowedMultipliers == GameClock.Speeds,
                $"固定步长 1/60 秒：1 真实秒内的模拟步数按倍速缩放（{string.Join("，", steps)}）；StrategyClock 与统一时钟是同一份倍率表（含 3x）");

            GameClock.ResetSession();
            GameClock.SetSpeed(2f);
            int before = GameClock.Advance(0.005f); // 攒 0.01 游戏秒的零头（不到一步）
            GameClock.SetPaused(true);
            int pausedSteps = 0;
            for (int i = 0; i < 120; i++)
            {
                pausedSteps += GameClock.Advance(1f / 60f);
            }
            float pausedScaled = GameClock.FrameScaledDt;
            GameClock.SetPaused(false);
            int resumed = GameClock.Advance(0.004f); // +0.008 游戏秒：只有带上暂停前的零头才够一步
            Expect(before == 0 && pausedSteps == 0 && pausedScaled == 0f && resumed == 1 && Mathf.Approximately(GameClock.Speed, 2f),
                $"暂停时 120 帧推进 0 步、本帧缩放时间为 0；继续后暂停前攒的零头不丢（第一帧就补上 {resumed} 步），暂停不改变所选倍速");

            GameClock.ResetSession();
            GameClock.SetSpeed(3f);
            GameClock.SetDirectLocked(true);
            int locked = 0;
            for (int i = 0; i < 60; i++)
            {
                locked += GameClock.Advance(1f / 60f);
            }
            GameClock.SetDirectLocked(false);
            Expect(locked == 60 && Mathf.Approximately(GameClock.EffectiveSpeed, 3f),
                $"接入（直控）时整个世界锁 1x：选 3x 也只推进 {locked} 步 / 秒；退出接入后恢复 3x");

            GameClock.ResetSession();
            GameClock.SetSpeed(3f);
            long droppedBefore = GameClock.DroppedSteps;
            int big = GameClock.Advance(5f); // 断点 / 加载后的超长帧
            Expect(big == 12 && GameClock.DroppedSteps > droppedBefore,
                $"超长帧：计入真实时间截到 clock.max_frame_seconds、单帧最多补 {big} 步（clock.max_steps_per_frame），多余的丢弃并计数（世界短暂变慢，不跳步）");

            GameClock.ResetSession();
            string t0 = GameClock.FormatDayTime(0);
            string t1 = GameClock.FormatDayTime(50);
            string t2 = GameClock.FormatDayTime(1200 * 18 / 24.0);
            int dayAfter = GameClock.DayOf(1200 * 18 / 24.0);
            string tNight = GameClock.FormatDayTime(1200 * 17.5 / 24.0);
            Expect(t0 == "第 1 日 06:00" && t1 == "第 1 日 07:00" && t2 == "第 2 日 00:00" && dayAfter == 2 && tNight == "第 1 日 23:30",
                $"游戏日：开局“{t0}”，50 游戏秒后“{t1}”（1 游戏小时 = 50 秒），18 游戏小时后跨日“{t2}”，“{tNight}”");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = GameClock.FormatDayTime(50);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en == "Day 1 07:00", $"英文：“{en}”");
        }

        private static void CheckClockSave()
        {
            CampaignState s = NewWorld(424242, dispatch: false);
            WorldSimulation.StepMany(60 * 125); // 125 游戏秒
            double seconds = GameClock.GameSeconds;
            long ticks = GameClock.Ticks;
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignSession.Set(Slot, r.State);
            WorldSimulation.LoadHome(resume: true);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(Slot);
            Expect(save.Success && r.Success && GameClock.Ticks == ticks && Math.Abs(GameClock.GameSeconds - seconds) < 1e-9
                   && r.State.Clock.StepHz == 60 && r.State.Clock.Day == 1 && meta.Day == 1 && Mathf.Approximately(r.State.PlaySeconds, 125f)
                   && Mathf.Approximately(meta.PlaySeconds, 125f),
                $"时钟随存档往返：{ticks} 步 / {seconds:F2} 游戏秒写进存档、读回后接着走；存档卡的“第几日”= {meta.Day}（DEBT-FG0SAVE01-06 关闭）、" +
                $"“游戏时长”= {meta.PlaySeconds} 秒（此前没有写入方、恒为 0）");

            // 旧档（FG0-ARCH-01 之前，只有 GameSeconds，没有步数）与步长频率不同的存档：按游戏秒换算，不丢时间。
            var legacy = new CampaignState { Clock = new GameClockState { GameSeconds = 900.5, Day = 0 } };
            GameClock.Bind(legacy);
            long legacyTicks = GameClock.Ticks;
            var other = new CampaignState { Clock = new GameClockState { GameSeconds = 10.0, Ticks = 300, StepHz = 30 } };
            GameClock.Bind(other);
            Expect(legacyTicks == (long)Math.Round(900.5 * 60) && legacy.Clock.Day == 2 && GameClock.Ticks == 600 && other.Clock.StepHz == 60,
                $"旧档只有游戏秒（900.5 秒，06:00 开局 → 已过午夜）→ {legacyTicks} 步并补写“第 {legacy.Clock.Day} 日”；30 Hz 时写的存档（300 步 = 10 秒）→ 当前 60 Hz 下 {GameClock.Ticks} 步");

            // 通知时间显示游戏日（DEBT-FG0UX01-07 前半）。
            CampaignState again = NewWorld(424243, dispatch: false);
            WorldSimulation.StepMany(60 * 50);
            NotificationEntry n = NotificationCenter.Post("storage_full", "x");
            string time = NotificationCenter.TimeText(n?.Members?.LastOrDefault());
            Expect(time == "第 1 日 07:00", $"通知时间写“第 N 日 HH:MM”（统一时钟接入后）：“{time}”");
            WorldSimulation.UnloadAll();
        }

        // ── C 整个世界同时运行（核心对照）──────────────────────────────────────────

        private const long ThirtyMinutesTicks = 60L * 60 * 30;

        private static void CheckWorldRunsTogether()
        {
            // 同一个出发存档：家园有生产队列与修复工单，远征队经真实派遣事务出发，一支突袭在路上。
            CampaignState start = NewWorld(20260925, dispatch: true);
            Expect(WorldSimulation.Home.IsLoaded && WorldSimulation.FracturedCity != null && WorldSimulation.FracturedCity.IsLoaded
                   && WorldView.ObservedSiteId == FracturedCityLayout.RegionId,
                "真实派遣事务（TryDepart）之后：家园仍在运行（不再 Exit）、远征地点已载入、镜头飞到远征地点");
            Expect(GameObject.Find("[HomeValley]") == null && GameObject.Find("[FracturedCityRoot]") != null && !WorldSimulation.Home.IsActive
                   && WorldSimulation.Home.IsLoaded && !WorldSimulation.Home.IsExpeditionPrepPanelOpen,
                "镜头在远征地点时：家园的表现对象隐藏（模拟照常）、远征地点的显示；家园的界面状态（远征准备面板）已收起");
            TransitGroupRecord raid = WorldTransitSystem.Groups(start).FirstOrDefault();
            Expect(raid != null && raid.State == TransitGroupState.Marching, $"一支突袭从“{raid?.OriginId}”出发，行进中（{raid?.UnitCount} 个单位，距家园 {Dist(raid):F0} 格）");
            int homeMachines = HomeMachineCount();
            string factoryBefore = FactoryDigest(start);
            WorkOrderRecord repair = start.WorkOrders.FirstOrDefault(o => o.TargetId != null && o.TargetId.Contains(HomeValleyLayout.BuildingTypeWarehouse));
            SaveBaseline();

            // A：镜头在家园 / 远征 / 突袭之间飞跃 100 次以上，中途暂停与切换倍速。
            var sw = Stopwatch.StartNew();
            RestoreBaseline(observe: FracturedCityLayout.RegionId);
            ExpeditionOrders orders = IssueExpeditionOrders();
            Expect(orders.Issued, $"远征编队下达命令（每一遍读档后同一时刻下达）：{orders.Description}");
            var cueBaseA = CueCounts();
            int flights = 0;
            int landed = 0;
            int pauses = 0;
            int frames = 0;
            string pendingTarget = null;
            string pendingSite = null;
            Vector2 pendingPos = Vector2.zero;
            int pendingSince = 0;
            float[] speeds = { 3f, 0.5f, 2f, 1f };
            bool homeUnobservedProgress = false;
            float factoryAtLeave = -1f;
            RunFrames(ThirtyMinutesTicks, 0.05f, frame =>
            {
                frames = frame;
                if (frame % 1000 == 0)
                {
                    GameClock.SetSpeed(speeds[(frame / 1000) % speeds.Length]);
                }
                if (frame % 1500 == 700)
                {
                    GameClock.SetPaused(true);
                    pauses++;
                }
                if (frame % 1500 == 730)
                {
                    GameClock.SetPaused(false);
                }
                if (pendingTarget != null && frame - pendingSince == 12)
                {
                    // 飞跃落地：镜头停在出发那一刻的目标位置（队伍与机器会继续移动，镜头不跟随），战略视角。
                    Vector2 focus = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
                    if (WorldView.ObservedSiteId == pendingSite && Vector2.Distance(focus, pendingPos) < 0.05f
                        && WorldView.Director.Mode == ViewMode.Strategy)
                    {
                        landed++;
                    }
                    pendingTarget = null;
                }
                if (frame % 105 == 50 && !GameClock.Paused)
                {
                    if (WorldView.CycleFocus())
                    {
                        flights++;
                        pendingTarget = WorldView.LastFocusTargetId;
                        WorldView.FocusTarget t = WorldView.FocusTargets(CampaignSession.Current).FirstOrDefault(x => x.Id == pendingTarget);
                        pendingSite = t.SiteId;
                        pendingPos = t.Position;
                        pendingSince = frame;
                    }
                }
                // 家园在镜头离开期间是否真的推进（生产进度）。
                bool homeObserved = WorldView.ObservedSiteId == HomeValleyLayout.RegionId;
                float p = FactoryProgress(CampaignSession.Current);
                if (!homeObserved && factoryAtLeave < 0f)
                {
                    factoryAtLeave = p;
                }
                else if (!homeObserved && p > factoryAtLeave + 0.01f)
                {
                    homeUnobservedProgress = true;
                }
                else if (homeObserved)
                {
                    factoryAtLeave = -1f;
                }
            });
            Dictionary<string, string> a = Snapshot(cueBaseA, out string aSummary);
            string expeditionEffect = orders.Measure(out bool expeditionMoved, out bool expeditionEngaged);
            long aFrames = frames;
            double aMs = sw.Elapsed.TotalMilliseconds;
            int aSwitches = WorldView.ObserveSwitchCount;
            Expect(flights >= 100 && landed >= flights - 2 && aSwitches >= 60 && pauses >= 3,
                $"A：30 分钟游戏时间（{ThirtyMinutesTicks} 步，{aFrames} 帧）里镜头在家园 / 远征 / 突袭之间飞跃 {flights} 次（落地核对 {landed} 次，跨表面切换 {aSwitches} 次），暂停 {pauses} 次，倍速在 0.5x～3x 之间切换");
            Expect(homeUnobservedProgress, "镜头不在家园时，家园的生产进度照样推进（FGR-BASE-021；DEBT-FG0ARCH04-14 关闭）");

            // B：镜头全程只看远征地点——家园从头到尾没有被观察。
            sw.Restart();
            RestoreBaseline(observe: FracturedCityLayout.RegionId);
            IssueExpeditionOrders();
            var cueBaseB = CueCounts();
            int bHomeObserved = 0;
            RunFrames(ThirtyMinutesTicks, 0.1f, frame =>
            {
                if (WorldView.ObservedSiteId == HomeValleyLayout.RegionId)
                {
                    bHomeObserved++;
                }
            });
            Dictionary<string, string> b = Snapshot(cueBaseB, out string bSummary);
            double bMs = sw.Elapsed.TotalMilliseconds;

            // C：完全不经过镜头与输入（无头推进）。
            sw.Restart();
            RestoreBaseline(observe: null);
            IssueExpeditionOrders();
            var cueBaseC = CueCounts();
            WorldSimulation.StepMany((int)(ThirtyMinutesTicks - GameClock.Ticks));
            Dictionary<string, string> c = Snapshot(cueBaseC, out string cSummary);
            double cMs = sw.Elapsed.TotalMilliseconds;

            // D：命令下达后镜头立即离开远征地点（飞回家园），此后 30 分钟一直看家园——远征队照样执行命令、照样交战。
            sw.Restart();
            RestoreBaseline(observe: FracturedCityLayout.RegionId);
            IssueExpeditionOrders();
            var cueBaseD = CueCounts();
            WorldView.FocusOn("home");
            int dRuinsObserved = 0;
            RunFrames(ThirtyMinutesTicks, 0.1f, frame =>
            {
                if (WorldView.ObservedSiteId == FracturedCityLayout.RegionId)
                {
                    dRuinsObserved++;
                }
            });
            Dictionary<string, string> d = Snapshot(cueBaseD, out _);
            double dMs = sw.Elapsed.TotalMilliseconds;
            PerfLines.Add($"30 分钟游戏时间：A（飞跃 + 暂停 + 变速，帧驱动）{aMs / 1000:F1} 秒，B（1x 帧驱动）{bMs / 1000:F1} 秒，C（无头）{cMs / 1000:F1} 秒，D（下令后镜头立即离开）{dMs / 1000:F1} 秒（Editor）");

            List<string> ab = DiffKeys(a, b);
            List<string> ac = DiffKeys(a, c);
            List<string> ad = DiffKeys(a, d);
            Expect(bHomeObserved == 0 && ab.Count == 0 && ac.Count == 0 && a.Count > 500,
                $"观察不改变结果：A（飞跃 100 次）、B（家园从未被观察）、C（无头）三遍的存档状态逐字段一致（{a.Count} 个字段，含机器位置 / 血量、工单、生产队列、" +
                $"敌人、突袭位置、时钟、反馈时刻计数、各类通知条数）" + (ab.Count + ac.Count > 0 ? $"；差异：A≠B {string.Join(" | ", ab.Take(6))}；A≠C {string.Join(" | ", ac.Take(6))}" : string.Empty));
            Line($"    {aSummary}");
            Expect(dRuinsObserved == 0 && ad.Count == 0,
                $"下令后镜头立即离开远征地点（D：此后远征地点被观察 {dRuinsObserved} 帧）：远征队照样执行移动 / 攻击命令，结果与 A 逐字段一致" +
                (ad.Count > 0 ? "；差异：" + string.Join(" | ", ad.Take(6)) : string.Empty));
            Expect(expeditionMoved && expeditionEngaged,
                $"远征侧真的在动（不是空转）：{expeditionEffect}；A / B / C / D 四遍逐字段一致即“有没有人看着，远征打得一样”");

            // 三件事都真的发生了（不是空转）：家园生产完成 / 工单完成、远征地点里有战斗或推进、突袭到达。
            bool produced = Parse(a, "·machines.home") > homeMachines || !string.Equals(FactoryDigestFrom(a), factoryBefore, StringComparison.Ordinal);
            bool raidArrived = a.TryGetValue("·raid.state", out string rs) && rs == TransitGroupState.Arrived.ToString();
            bool repaired = repair != null && a.TryGetValue("·warehouse.construction", out string wh) && wh == BuildingConstructionState.Operational.ToString();
            Expect(produced && raidArrived && repaired && Parse(a, "·clock.ticks") == ThirtyMinutesTicks,
                $"30 分钟里三方都在推进：家园机器 {homeMachines}→{Parse(a, "·machines.home")}（装配站出厂）、仓库修复 {a.GetValueOrDefault("·warehouse.construction")}、" +
                $"突袭 {a.GetValueOrDefault("·raid.state")}（位置 {a.GetValueOrDefault("·raid.pos")}）、远征 {a.GetValueOrDefault("·machines.ruins")} 台 / 敌人 {a.GetValueOrDefault("·enemies.ruins")}");
            WorldSimulation.UnloadAll();
        }

        // ── D 暂停与倍速矩阵 ─────────────────────────────────────────────────────

        private static void CheckPauseAndSpeedMatrix()
        {
            CampaignState start = NewWorld(777001, dispatch: true);
            SaveBaseline();
            const long ticks = 60L * 90; // 90 游戏秒

            // 暂停：整个世界（家园、远征、突袭）同时停止——真实时间走 300 帧，三方状态逐字段不变。
            RestoreBaseline(observe: HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(60 * 5);
            GameClock.SetPaused(true);
            var cueBase = CueCounts();
            Dictionary<string, string> before = Snapshot(cueBase, out _);
            for (int i = 0; i < 100; i++)
            {
                FrameOnce(0.05f);
            }
            WorldView.FocusOn("site:" + FracturedCityLayout.RegionId);
            for (int i = 0; i < 100; i++)
            {
                FrameOnce(0.05f);
            }
            WorldView.FocusOn(WorldTransitSystem.Groups(CampaignSession.Current).First().GroupId);
            for (int i = 0; i < 100; i++)
            {
                FrameOnce(0.05f);
            }
            Dictionary<string, string> after = Snapshot(cueBase, out _);
            List<string> pausedDiff = DiffKeys(before, after);
            Expect(pausedDiff.Count == 0 && GameRoot.IsWorldPaused,
                "暂停：真实时间走 300 帧、期间镜头在三处之间飞跃，家园 / 远征 / 突袭的状态逐字段不变（整个世界同时暂停）" +
                (pausedDiff.Count > 0 ? "；差异：" + string.Join(" | ", pausedDiff.Take(6)) : string.Empty));
            GameClock.SetPaused(false);

            // 倍速：同一段 90 游戏秒，0.5x / 1x / 2x / 3x 帧驱动与 1x 逐字段一致，真实时间按倍速缩放。
            Dictionary<string, string> baseline = null;
            var lines = new List<string>();
            bool allSame = true;
            bool realScaled = true;
            foreach (float speed in GameClock.Speeds.Concat(new[] { 1f }))
            {
                RestoreBaseline(observe: HomeValleyLayout.RegionId);
                GameClock.SetSpeed(speed);
                var cb = CueCounts();
                int frames = RunFrames(ticks, 1f / 60f, null);
                Dictionary<string, string> snap = Snapshot(cb, out _);
                float real = frames / 60f;
                lines.Add($"{speed}x：{frames} 帧 ≈ {real:F1} 真实秒");
                realScaled &= Math.Abs(real * speed - ticks / 60f) < 0.5f;
                if (baseline == null && Mathf.Approximately(speed, 0.5f))
                {
                    baseline = snap;
                    continue;
                }
                List<string> d = DiffKeys(baseline, snap);
                if (d.Count > 0)
                {
                    allSame = false;
                    lines.Add("差异 " + string.Join(" | ", d.Take(4)));
                }
            }
            Expect(allSame && realScaled,
                $"倍速矩阵：同一段 90 游戏秒（家园 + 远征 + 突袭）在 0.5x / 1x / 2x / 3x 下结果逐字段一致，真实时间按倍速缩放（{string.Join("；", lines)}）");

            CheckAutoPauseStopsAtTriggerStep();
            WorldSimulation.UnloadAll();
        }

        /// <summary>步内触发的暂停（紧急通知“突袭到达”自动暂停）停在触发它的那一步：3x、一帧批出多步时，剩下的步不再执行，
        /// 而是退回累计量，继续后照常执行（不丢时间）。另核对到达后突袭标记的菱形与立柱一起变色。</summary>
        private static void CheckAutoPauseStopsAtTriggerStep()
        {
            RestoreBaseline(observe: HomeValleyLayout.RegionId);
            CampaignState s = CampaignSession.Current;
            TransitGroupRecord g = WorldTransitSystem.Groups(s).First();
            double arrival = GameClock.TuningOr("transit.arrival_radius_cells", 12f);
            double dx = g.TargetX - g.PosX;
            double dy = g.TargetY - g.PosY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double lead = arrival + g.Speed * GameClock.StepSeconds * 2.5; // 第 3 步到达
            g.PosX = g.TargetX - dx / dist * lead;
            g.PosY = g.TargetY - dy / dist * lead;
            long pausedAt = -1;
            Func<bool> original = NotificationCenter.AutoPauseHandler;
            NotificationCenter.AutoPauseHandler = () =>
            {
                if (pausedAt < 0)
                {
                    pausedAt = GameClock.Ticks;
                }
                GameClock.SetPaused(true);
                return true;
            };
            try
            {
                GameClock.SetSpeed(3f);
                long before = GameClock.Ticks;
                FrameOnce(0.2f); // 3x：0.6 游戏秒 → 批出 clock.max_steps_per_frame 步
                long afterPause = GameClock.Ticks;
                bool stopped = pausedAt >= 0 && GameClock.Paused && afterPause == pausedAt + 1 && g.State == TransitGroupState.Arrived;
                for (int i = 0; i < 5; i++)
                {
                    FrameOnce(0.2f);
                }
                bool held = GameClock.Ticks == afterPause;
                GameClock.SetPaused(false);
                FrameOnce(0f); // 不加真实时间：只执行退回的那几步
                long refunded = GameClock.Ticks - afterPause;
                Expect(stopped && held && refunded > 0,
                    $"步内自动暂停（3x、突袭在第 {pausedAt - before + 1} 步到达）：暂停停在触发的那一步（{before}→{afterPause}，没有把本帧剩下的步跑完），" +
                    $"暂停期间不动（{held}）；继续后退回的 {refunded} 步照常执行（不丢时间）");

                WorldView.FocusOn(g.GroupId);
                for (int i = 0; i < 12; i++)
                {
                    FrameOnce(0.05f);
                }
                GameObject marker = GameObject.Find("RaidMarker_" + g.GroupId);
                Renderer[] parts = marker != null ? marker.GetComponentsInChildren<Renderer>() : Array.Empty<Renderer>();
                Expect(parts.Length == 2 && parts.All(r => r.sharedMaterial == parts[0].sharedMaterial) && g.State == TransitGroupState.Arrived,
                    $"突袭到达后标记整体换成到达色（{parts.Length} 个部件：{string.Join(" / ", parts.Select(r => r.name + "=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "null")))}）");
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = original;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        // ── E 负向：飞跃途中存读档 ─────────────────────────────────────────────────

        private static void CheckFlightSaveLoad()
        {
            CampaignState s = NewWorld(515151, dispatch: true);
            WorldSimulation.StepMany(60 * 20);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            FrameOnce(0.05f);
            WorldView.CycleFocus(); // 飞往远征地点，过渡中
            bool inFlight = WorldView.Director.InTransition;
            WorldSimulation.SyncAllForSave();
            int machinesBefore = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive);
            int homeBefore = HomeMachineCount();
            int ruinsBefore = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId);
            int buildings = s.BuildingRecords.Length;
            TransitGroupRecord raid = WorldTransitSystem.Groups(s).First();
            double rx = raid.PosX;
            long ticks = GameClock.Ticks;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignSession.Set(Slot, r.State);
            string observed = ResumeWorldLikeMenu();
            CampaignState loaded = CampaignSession.Current;
            TransitGroupRecord raid2 = WorldTransitSystem.Groups(loaded).FirstOrDefault();
            int ruinsMarkers = WorldSimulation.FracturedCity?.LiveMachineCount ?? -1;
            Expect(save.Success && r.Success && inFlight && MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive) == machinesBefore
                   && HomeMachineCount() == homeBefore && ruinsMarkers == ruinsBefore && loaded.BuildingRecords.Length == buildings
                   && raid2 != null && raid2.PosX == rx && WorldTransitSystem.Groups(loaded).Count == 1 && GameClock.Ticks == ticks
                   && observed == FracturedCityLayout.RegionId && WorldSimulation.Home.IsLoaded && !WorldView.Director.InTransition,
                $"镜头飞跃途中存读档：机器 {machinesBefore} 台（家园 {homeBefore} / 远征 {ruinsBefore}）、建筑 {buildings}、突袭 1 支（位置逐位一致）、时间轴 {ticks} 步都不多不少；" +
                $"读档后家园与远征同时恢复运行，镜头落在远征地点（存档时所在），不停在半截过渡里");

            // 读档后再存再读：不产生重复实体（第二次进入不复制）。
            WorldSimulation.StepMany(60);
            WorldSimulation.SyncAllForSave();
            int aliveBeforeSecond = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive);
            int buildingsBeforeSecond = CampaignSession.Current.BuildingRecords.Length;
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            RestoreResult r2 = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignSession.Set(Slot, r2.State);
            ResumeWorldLikeMenu();
            CampaignState reloaded = CampaignSession.Current;
            int logicIds = MachineRegistry.AllRecords.Count(m => m != null);
            int distinctLogic = MachineRegistry.AllRecords.Where(m => m != null).Select(m => m.LogicId).Distinct().Count();
            int buildingIds = reloaded.BuildingRecords.Select(b => b.BuildingId).Distinct().Count();
            int homeMarkers = WorldSimulation.Home.LiveMachineCount;
            // FG0-ARCH-03（DEBT-FG0ARCH01-03 收口）：机器逻辑句柄在各地点的战斗内核里（家园 + 远征 = 全部存活机器）；
            // 画面对象（MachineView）只在被观察的地点存在，不被观察的地点一个都没有（含隐藏的也不算）。
            string observedSite = WorldView.ObservedSiteId;
            int markerObjects = Object.FindObjectsByType<MachineView>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int observedAlive = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == observedSite);
            int handles = (WorldSimulation.Home?.LiveMachineCount ?? 0) + (WorldSimulation.FracturedCity?.LiveMachineCount ?? 0);
            int expectedMarkers = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && (m.RegionId == HomeValleyLayout.RegionId || m.RegionId == FracturedCityLayout.RegionId));
            Expect(logicIds == distinctLogic && MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive) == aliveBeforeSecond
                   && buildingIds == reloaded.BuildingRecords.Length && reloaded.BuildingRecords.Length == buildingsBeforeSecond
                   && homeMarkers == HomeMachineCount() && handles == expectedMarkers && markerObjects == observedAlive,
                $"再存再读：机器记录 {logicIds} 条无重复编号、建筑 {reloaded.BuildingRecords.Length} 座无重复 ID；战斗内核里的机器句柄 {handles} 个 = 家园与远征的存活机器 {expectedMarkers}（家园 {homeMarkers}）；" +
                $"场景里的机器表现对象 {markerObjects} 个 = 被观察地点（{observedSite}）的存活机器 {observedAlive}，不被观察的地点没有表现对象");
            WorldSimulation.UnloadAll();
        }

        // ── E 负向：自动存档不经手动同步也写回实时位置 ─────────────────────────────────

        /// <summary>家园在远征期间不再 Exit：自动存档（出发后确认、远征结算）必须自己把各地点机器的实时位置写回记录，
        /// 不能依赖调用方先手动同步。本段全程不调 SyncAllForSave / SyncActiveRegionForSave。</summary>
        private static void CheckAutoSaveWritesLivePositions()
        {
            NewWorld(424242, dispatch: true);
            // ① 出发后确认档（ExpeditionDepartureService 在派遣成功后写的最后一份自动档）：远征机器是远征地点里的实时坐标，
            //    不是家园还没回收的旧标记坐标；家园机器是家园实时坐标。
            LoadResult departed = CampaignSaveService.Load(Slot); // 只读存档文件，不改内存里的世界与机器注册表
            int departMismatch = CountPositionMismatch(departed.State, out int departChecked, out int departRuins);
            Expect(departed.Success && departChecked >= 3 && departRuins >= 3 && departMismatch == 0,
                $"出发后确认档：{departChecked} 台机器（远征 {departRuins} 台）的存档坐标与各自所在地点的实时坐标逐位一致（不一致 {departMismatch}）");

            // ② 远征进行中家园照常干活（修理工开往仓库），20 游戏秒后直接走自动档入口。
            int stale = 0;
            int seconds = 0;
            while (stale == 0 && seconds < 120)
            {
                WorldSimulation.StepMany(60);
                seconds++;
                stale = 0;
                foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive))
                {
                    Vector2? live = WorldSimulation.LivePosition(m.LogicId);
                    if (live.HasValue && m.WorldPosition != live.Value)
                    {
                        stale++;
                    }
                }
            }
            SaveResult save = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
            LoadResult r = CampaignSaveService.Load(Slot);
            int mismatch = CountPositionMismatch(r.State, out int checkedCount, out int ruins);
            Expect(stale > 0 && save.Success && r.Success && checkedCount >= 3 && mismatch == 0,
                $"远征期间自动存档（ExpeditionResolutionComplete 入口，不手动同步）：推进 {seconds} 游戏秒后 {stale} 台机器的记录落后于实时位置；" +
                $"读档后 {checkedCount} 台机器（远征 {ruins} 台）的坐标与存档那一刻的实时坐标逐位一致（不一致 {mismatch}）");
            WorldSimulation.UnloadAll();
        }

        /// <summary>存档里每台存活、当前有表现对象的机器：坐标是否与它所在地点的实时坐标逐位相等。</summary>
        private static int CountPositionMismatch(CampaignState saved, out int checkedCount, out int ruins)
        {
            checkedCount = 0;
            ruins = 0;
            int mismatch = 0;
            foreach (MachineRecord m in saved?.MachineRecords ?? Array.Empty<MachineRecord>())
            {
                if (m == null || !m.IsAlive)
                {
                    continue;
                }
                Vector2? live = WorldSimulation.LivePosition(m.LogicId);
                if (!live.HasValue)
                {
                    continue;
                }
                checkedCount++;
                ruins += m.RegionId == FracturedCityLayout.RegionId ? 1 : 0;
                if (m.WorldPosition != live.Value)
                {
                    mismatch++;
                    Line($"    · 机器 {m.LogicId}（{m.RegionId}）存档 {m.WorldPosition} ≠ 实时 {live.Value}");
                }
            }
            return mismatch;
        }

        // ── E 负向：远征全灭时的镜头 ───────────────────────────────────────────────

        private static void CheckExpeditionWipeCamera()
        {
            CampaignState s = NewWorld(626262, dispatch: true);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            int wipedCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionWiped);
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId).ToList())
            {
                MachineRegistry.MarkDeadByLogicId(m.LogicId);
            }
            RunFrames(GameClock.Ticks + 30, 0.05f, null);
            NotificationEntry wipe = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "expedition_wiped");
            string wipeRegion = wipe?.Members?.LastOrDefault()?.RegionId;
            Expect(WorldSimulation.FracturedCity.IsWiped && FeedbackCues.CountOf(FeedbackCueId.ExpeditionWiped) == wipedCues + 1
                   && WorldView.ObservedSiteId == HomeValleyLayout.RegionId && wipeRegion == FracturedCityLayout.RegionId,
                $"镜头在家园时远征全灭：镜头不被强行拉走（仍在家园），全灭通知记在远征地点（{wipeRegion}），可点定位过去");

            WorldView.FocusOn("site:" + FracturedCityLayout.RegionId);
            FrameOnce(0.05f);
            var bar = new GameObject("__fgsim_bar") { hideFlags = HideFlags.HideAndDontSave };
            string status;
            try
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/WorldBar.uxml");
                VisualElement root = vta.CloneTree();
                WorldBarHudUIToolkit hud = bar.AddComponent<WorldBarHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh();
                status = hud.StatusText;
            }
            finally
            {
                Object.DestroyImmediate(bar);
            }
            Expect(WorldView.ObservedSiteId == FracturedCityLayout.RegionId && status == GameText.Get("ui.world.expedition_wiped_here"),
                $"飞到全灭的远征地点：镜头留在那里等玩家确认放弃，世界时间条写明“{status}”");

            ExpeditionReturnService.ReturnResult abandon = ExpeditionReturnService.TryConfirmAbandon();
            FrameOnce(0.05f);
            Expect(abandon.Success && !WorldSimulation.FracturedCity.IsLoaded && WorldView.ObservedSiteId == HomeValleyLayout.RegionId
                   && WorldSimulation.Home.IsLoaded && s.CurrentRegionId == HomeValleyLayout.RegionId,
                "确认放弃远征：远征地点卸载，镜头回到家园（家园一直在运行，不重新进入），世界的当前地点回到家园");

            // 观察中的地点被其他路径卸载（暂离 / 调试退出）：下一帧自动回家园，不停在已卸载的地点上。
            GameRoot.StartFracturedCity(MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Select(m => m.LogicId).Take(1).ToArray());
            bool observedRuins = WorldView.ObservedSiteId == FracturedCityLayout.RegionId;
            int fallbackBefore = WorldView.FallbackToHomeCount;
            WorldSimulation.FracturedCity.Exit(evacuateSuccess: false);
            FrameOnce(0.05f);
            Expect(observedRuins && WorldView.ObservedSiteId == HomeValleyLayout.RegionId && WorldView.FallbackToHomeCount == fallbackBefore + 1,
                "观察中的远征地点被卸载：下一帧镜头自动回到家园");
            WorldSimulation.UnloadAll();
        }

        // ── F 归属与定位 ─────────────────────────────────────────────────────────

        private static void CheckAttributionAndLocate()
        {
            CampaignState s = NewWorld(737373, dispatch: true);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            RegionEnemyRecord enemy = s.RegionEnemies.First(e => e.RegionId == FracturedCityLayout.RegionId && e.IsAlive);

            // 模拟步期间“发生在哪个地点”= 正在推进的地点（不是镜头所在的地点）：镜头在家园时远征里阵亡一台机器，
            // 由远征地点的模拟步（受控机死亡 / 全灭侦测）发出的通知记在远征地点。
            MachineRecord victim = MachineRegistry.AllRecords.First(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId);
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId).ToList())
            {
                MachineRegistry.MarkDeadByLogicId(m.LogicId);
            }
            RunFrames(GameClock.Ticks + 5, 0.05f, null);
            NotificationEntry wiped = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "expedition_wiped");
            string region = wiped?.Members?.LastOrDefault()?.RegionId;
            Expect(WorldView.ObservedSiteId == HomeValleyLayout.RegionId && region == FracturedCityLayout.RegionId && GameRoot.ActiveRegionId == HomeValleyLayout.RegionId,
                $"事件归属：镜头在家园时远征地点模拟步里发出的通知记在远征地点（{region}）；模拟步之外（界面操作）归镜头所在地点（{GameRoot.ActiveRegionId}）");

            // 特效只画镜头正在观察的地点：远征地点的时刻不画在家园画面上（坐标重叠），家园的照画。
            Func<string, Sprite> savedResolver = FeedbackVfxPresenter.SpriteResolver;
            Func<string> savedSite = FeedbackCues.SiteProvider;
            var texture = new Texture2D(16, 16) { hideFlags = HideFlags.HideAndDontSave };
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 16f, 16f), new Vector2(0.5f, 0.5f), 16f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            var host = new GameObject("__fgsim_vfx") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                FeedbackVfxPresenter.SpriteResolver = _ => sprite;
                FeedbackVfxPresenter presenter = host.AddComponent<FeedbackVfxPresenter>();
                presenter.Tick(0f);
                FeedbackCues.SiteProvider = () => FracturedCityLayout.RegionId;
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
                presenter.Tick(1f);
                int remote = presenter.ActiveCount;
                FeedbackCues.SiteProvider = () => HomeValleyLayout.RegionId;
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, HomeValleyLayout.Core.Position);
                presenter.Tick(1.01f);
                int local = presenter.ActiveCount;
                Expect(remote == 0 && local == 1 && !FeedbackCues.IsInObservedSite(FracturedCityLayout.RegionId),
                    $"特效只画镜头所在地点的时刻：远征地点的命中 {remote} 个、家园的命中 {local} 个（远征的声音按保底音量）");
            }
            finally
            {
                FeedbackVfxPresenter.SpriteResolver = savedResolver;
                FeedbackCues.SiteProvider = savedSite;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
            }

            // 跨地点定位：通知记在远征地点，点定位（通知中心的真实定位入口）→ 镜头飞过去。
            WorldView.FocusOn("home");
            for (int i = 0; i < 12; i++)
            {
                FrameOnce(0.05f);
            }
            NotificationEntry located = NotificationCenter.Post("machine_destroyed", "x", new Vector3(enemy.Position.x, 0f, enemy.Position.y));
            located.Members[located.Members.Count - 1].RegionId = FracturedCityLayout.RegionId; // 归属已在上面单独验证：这里只验证“跨地点定位”
            bool ok = NotificationCenter.Locate(located, -1, out string key);
            for (int i = 0; i < 15; i++)
            {
                FrameOnce(0.05f);
            }
            Vector2 focus = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
            Expect(ok && key == null && WorldView.ObservedSiteId == FracturedCityLayout.RegionId && Vector2.Distance(focus, enemy.Position) < 1.5f,
                $"通知定位跨地点：镜头从家园飞到远征地点的事件位置（焦点 {focus}，DEBT-FG0UX01-07 后半关闭）");

            WorldSimulation.FracturedCity.Exit(evacuateSuccess: false);
            bool fail = !WorldView.Locate(FracturedCityLayout.RegionId, Vector3.zero, out string failKey);
            Expect(fail && failKey == "ui.world.site_unloaded" && !GameText.Get(failKey).StartsWith("⟦", StringComparison.Ordinal),
                $"远征已结束的通知再点定位：不飞、给出原因“{GameText.Get(failKey ?? string.Empty)}”");

            // 重复派遣被拒：远征在外时（家园照常可以回来看）不能再派第二支，原因可读。
            WorldSimulation.UnloadAll();
            CampaignState s2 = NewWorld(737374, dispatch: true);
            var roster = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Select(m => m.LogicId).ToArray();
            ExpeditionDepartureService.DepartureResult again = ExpeditionDepartureService.TryDepart(roster, true);
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s2);
            Expect(again.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && again.Reasons.Contains("expedition-underway")
                   && !snap.RegionReachable && snap.BlockedReason == "expedition-underway" && WorldSimulation.FracturedCity.IsLoaded,
                $"远征在外时再次派遣被拒（原因“{GameText.Get("expedition.reason.underway")}”），在外的远征不受影响");
            WorldSimulation.UnloadAll();
        }

        // ── G 种子无关（B25）─────────────────────────────────────────────────────

        private static void CheckSeedIndependence()
        {
            var origins = new HashSet<string>();
            var lines = new List<string>();
            bool allArrive = true;
            foreach (int seed in new[] { 11, 202, 3003, 40004, 505005 })
            {
                CampaignState s = NewWorld(seed, dispatch: false);
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "foundry", 5, out string failure);
                if (g == null)
                {
                    allArrive = false;
                    lines.Add($"种子 {seed} 派不出：{failure}");
                    continue;
                }
                origins.Add($"{g.PosX:F0},{g.PosY:F0}");
                double eta = WorldTransitSystem.EtaSeconds(g);
                WorldSimulation.StepMany((int)Math.Ceiling(eta * GameClock.StepHz) + 60);
                allArrive &= g.State == TransitGroupState.Arrived && Math.Abs(g.ArrivedAtTick - (long)Math.Ceiling(eta * 60)) <= 2;
                (Vector2 min, Vector2 max) = WorldView.PlanetBounds(s);
                bool inBounds = g.PosX >= min.x && g.PosX <= max.x && g.PosY >= min.y && g.PosY <= max.y;
                allArrive &= inBounds;
                lines.Add($"种子 {seed}：出发 ({g.PosX:F0},{g.PosY:F0}) → 预计 {eta:F0} 秒到达，实到第 {g.ArrivedAtTick} 步");
                WorldSimulation.UnloadAll();
            }
            WorldTransitSystem.DispatchRaidFromTerritory(new CampaignState(), "no_such_territory", 1, out string bad);
            Expect(allArrive && origins.Count == 5 && bad != null,
                $"突袭出发点由种子生成的规划层派生（5 个种子 5 个不同出发点），预计到达时间准确、镜头范围包含队伍；未知领地给出原因（{bad}）。{string.Join("；", lines)}");
        }

        // ── H 活跃区块 ───────────────────────────────────────────────────────────

        private static void CheckActivity()
        {
            CampaignState s = NewWorld(868686, dispatch: true);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            WorldSimulation.RefreshActivity(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            TransitGroupRecord g = WorldTransitSystem.Groups(s).First();
            ChunkAddress raidChunk = GridMath.Address(new GridCell((int)Math.Floor(g.PosX + 0.5), (int)Math.Floor(g.PosY + 0.5)), map.ChunkSize);
            ChunkAddress coreChunk = GridMath.Address(HomeGridService.CorePivot(s), map.ChunkSize);
            bool raidActive = WorldSimulation.IsChunkActive(HomeGridMap.Key(raidChunk.ChunkX, raidChunk.ChunkY));
            bool coreActive = WorldSimulation.IsChunkActive(HomeGridMap.Key(coreChunk.ChunkX, coreChunk.ChunkY));
            int observedCount = WorldSimulation.ActiveChunkCount;
            WorldView.FocusOn("site:" + FracturedCityLayout.RegionId);
            FrameOnce(0.05f);
            WorldSimulation.RefreshActivity(s);
            int unobservedCount = WorldSimulation.ActiveChunkCount;
            bool raidStill = WorldSimulation.IsChunkActive(HomeGridMap.Key(raidChunk.ChunkX, raidChunk.ChunkY));
            Expect(raidActive && coreActive && raidStill && unobservedCount < observedCount && HomeGridService.Streamer(s).KeepResident != null,
                $"活跃区块（FGR-GEN-052 第 1、2 条，世界模拟的生产调用）：有己方建筑 / 机器的、行进中的突袭所在的区块始终活跃；镜头离开星球后观察窗口不再计入" +
                $"（{observedCount} → {unobservedCount}）；流式加载的回收规则保留活跃区块（DEBT-FG0ARCH05-08 的 ARCH-01 部分关闭）");
            WorldSimulation.UnloadAll();
        }

        // ── I 输入 ───────────────────────────────────────────────────────────────

        private static void CheckInput()
        {
            CampaignState s = NewWorld(909090, dispatch: true);
            WorldView.FocusOn("home");
            for (int i = 0; i < 12; i++)
            {
                FrameOnce(0.05f);
            }
            _reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            FrameOnce(0.05f);
            bool paused = GameClock.Paused && GameRoot.IsWorldPaused;
            _reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            FrameOnce(0.05f);
            bool resumed = !GameClock.Paused;
            _reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.SpeedTriple));
            UiKitInputPump.ProcessWorldKeys();
            FrameOnce(0.05f);
            bool triple = Mathf.Approximately(GameClock.Speed, 3f);
            Expect(paused && resumed && triple, "Space 暂停 / 继续整个世界；4 键 = 3x（DEBT-FG0UX01-03 关闭）");

            string startSite = WorldView.ObservedSiteId;
            var visited = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                _reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus));
                FrameOnce(0.05f);
                for (int i = 0; i < 12; i++)
                {
                    FrameOnce(0.05f);
                }
                visited.Add(WorldView.LastFocusTargetId);
            }
            _reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore));
            FrameOnce(0.05f);
            for (int i = 0; i < 12; i++)
            {
                FrameOnce(0.05f);
            }
            Vector2 focus = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
            Expect(visited.Count == 3 && visited[0] == "site:" + FracturedCityLayout.RegionId && visited[1].StartsWith("transit-", StringComparison.Ordinal) && visited[2] == "home"
                   && WorldView.ObservedSiteId == HomeValleyLayout.RegionId && Vector2.Distance(focus, HomeValleyLayout.Core.Position) < 1.5f,
                $"Tab 依次切换关注点（从 {startSite} 出发：{string.Join(" → ", visited)}），Home 回到归还核心（焦点 {focus}）");

            // 改键后新键生效、旧键不再生效（可重绑）。
            var conflicts = new List<GameActionId>();
            RebindResult rebind = GameSettings.TryRebind(GameActionId.CycleWorldFocus, new InputChord(KeyCode.P), conflicts);
            string beforeLast = WorldView.LastFocusTargetId;
            _reader.Press(KeyCode.Tab);
            FrameOnce(0.05f);
            bool tabIgnored = WorldView.LastFocusTargetId == beforeLast;
            _reader.Press(KeyCode.P);
            FrameOnce(0.05f);
            bool jWorks = WorldView.LastFocusTargetId != beforeLast;
            GameSettings.ResetKeyBinding(GameActionId.CycleWorldFocus);
            Expect(tabIgnored && jWorks && GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus) == KeyCode.Tab,
                $"“切换关注点”改绑到 P（结果 {rebind}，冲突 {conflicts.Count}）后：Tab 不再触发、P 触发；恢复默认回到 Tab");
            WorldSimulation.UnloadAll();
        }

        // ── J 世界时间条 ─────────────────────────────────────────────────────────

        private static void CheckWorldBar()
        {
            CampaignState s = NewWorld(454545, dispatch: true);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            var go = new GameObject("__fgsim_worldbar") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/WorldBar.uxml");
                Expect(vta != null, "世界时间条 UXML 存在（Raw/UI/UiKit/WorldBar.uxml，YooAsset 收集目录）");
                VisualElement root = vta.CloneTree();
                WorldBarHudUIToolkit hud = go.AddComponent<WorldBarHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh();
                int targets = WorldView.FocusTargets(s).Count;
                Expect(hud.BarVisible && hud.DayTimeText == GameClock.FormatDayTime(GameClock.GameSeconds) && hud.FocusButtonCount == targets && targets == 3
                       && hud.FocusButtons[0].text.StartsWith("▸", StringComparison.Ordinal),
                    $"世界时间条：“{hud.DayTimeText}”、{hud.FocusButtonCount} 个关注点（{string.Join(" / ", hud.FocusButtons.Select(b => b.text))}），当前关注点有“▸”前缀");

                ClickButton(hud.SpeedButton(3));
                bool speed3 = Mathf.Approximately(GameClock.Speed, 3f);
                ClickButton(hud.PauseButton);
                hud.Refresh();
                bool pausedByButton = GameClock.Paused && hud.StatusText == GameText.Get("ui.world.status_paused")
                                      && hud.PauseButton.text == GameText.Get("ui.world.resume");
                ClickButton(hud.PauseButton);
                bool resumedByButton = !GameClock.Paused;
                TransitGroupRecord g = WorldTransitSystem.Groups(s).First();
                Vector2 raidAtClick = WorldTransitSystem.Position(g);
                ClickButton(hud.FocusButtons[2]);
                for (int i = 0; i < 12; i++)
                {
                    FrameOnce(0.05f);
                }
                hud.Refresh();
                Vector2 focus = new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);
                bool markerShown = WorldPlanetView.TryGetMarkerPosition(g.GroupId, out Vector3 markerPos) && WorldPlanetView.VisibleMarkerCount >= 1
                                   && Vector2.Distance(new Vector2(markerPos.x, markerPos.z), WorldTransitSystem.Position(g)) < 0.01f;
                Expect(speed3 && pausedByButton && resumedByButton && Vector2.Distance(focus, raidAtClick) < 0.05f && markerShown && WorldPlanetView.TerrainShown
                       && hud.FocusButtons[2].text.StartsWith("▸", StringComparison.Ordinal) && hud.SpeedButton(3).text.StartsWith("▸", StringComparison.Ordinal),
                    $"按钮走真实回调：点 3x → 整个世界 3x（{speed3}）；点暂停 → 已暂停、按钮变“继续”（{pausedByButton}）；再点继续（{resumedByButton}）；" +
                    $"点突袭关注点 → 镜头飞到突袭（焦点 {focus} / 点击时 {raidAtClick}），镜头附近才生成突袭标记且位置 = 正式数据（{markerShown}），" +
                    $"普通视角显示地貌层（{WorldPlanetView.TerrainShown}）；当前项与当前速度都有“▸”（{hud.FocusButtons[2].text} / {hud.SpeedButton(3).text}）");

                // 普通视角地貌层随镜头加载 / 卸载（DEBT-FG0ARCH05-02 的 FG0-ARCH-01 份额）：窗口跟随焦点，贴图块数不增长，飞走后远处的块被回收。
                WorldTerrainOverlay terrain = WorldPlanetView.Terrain;
                int raidCx = terrain?.WindowChunkX ?? int.MinValue;
                int raidCy = terrain?.WindowChunkY ?? int.MinValue;
                int raidTiles = terrain?.TileCount ?? -1;
                bool raidHasTile = terrain != null && terrain.HasTile(raidCx, raidCy);
                WorldView.FocusOn("home");
                for (int i = 0; i < 40; i++)
                {
                    FrameOnce(0.05f);
                }
                int homeCx = terrain?.WindowChunkX ?? int.MinValue;
                int homeCy = terrain?.WindowChunkY ?? int.MinValue;
                int homeTiles = terrain?.TileCount ?? -1;
                int radiusChunks = terrain?.WindowRadius ?? 0;
                bool farReleased = terrain != null && !terrain.HasTile(raidCx, raidCy);
                bool windowMoved = Math.Max(Math.Abs(homeCx - raidCx), Math.Abs(homeCy - raidCy)) > radiusChunks;
                int maxTiles = (2 * radiusChunks + 1) * (2 * radiusChunks + 1);
                Expect(terrain != null && raidHasTile && windowMoved && farReleased && raidTiles <= maxTiles && homeTiles <= maxTiles,
                    $"普通视角地貌层随镜头加载 / 卸载：突袭处窗口 ({raidCx},{raidCy}) {raidTiles} 块 → 回家园窗口 ({homeCx},{homeCy}) {homeTiles} 块（上限 {maxTiles}），" +
                    $"突袭处的贴图块已回收（{farReleased}），块数不随飞跃增长");

                // 建造入口只对镜头所在的家园（远征期间家园仍在运行、建造模式仍绑定，但镜头在别处时不给入口）。
                var buildGo = new GameObject("__fgsim_build") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    VisualElement buildRoot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml").CloneTree();
                    BuildModeHudUIToolkit build = buildGo.AddComponent<BuildModeHudUIToolkit>();
                    build.BindView(buildRoot);
                    WorldView.FocusOn("site:" + FracturedCityLayout.RegionId);
                    for (int i = 0; i < 12; i++)
                    {
                        FrameOnce(0.05f);
                    }
                    build.Refresh();
                    bool hiddenAway = !build.EntryVisible;
                    WorldView.FocusOn("home");
                    for (int i = 0; i < 12; i++)
                    {
                        FrameOnce(0.05f);
                    }
                    build.Refresh();
                    Expect(hiddenAway && build.EntryVisible, "建造入口：镜头在远征地点时不显示，回到家园后显示（建造只对眼前的家园）");
                }
                finally
                {
                    Object.DestroyImmediate(buildGo);
                }

                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                    {
                        string result = UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/UiKit/WorldBar.uxml", "WorldBar", stressFill: true,
                            prepare: r =>
                            {
                                var probeGo = new GameObject("__probe_worldbar") { hideFlags = HideFlags.HideAndDontSave };
                                WorldBarHudUIToolkit h = probeGo.AddComponent<WorldBarHudUIToolkit>();
                                h.BindView(r.panel.visualTree);
                                h.Refresh();
                                Object.DestroyImmediate(probeGo);
                            }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 WorldBar.uxml#WorldBar [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            WorldSimulation.UnloadAll();
        }

        // ── K 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(313131, dispatch: true);
            WorldSimulation.StepMany(600);
            var sw = Stopwatch.StartNew();
            const int n = 3600;
            WorldSimulation.StepMany(n);
            double perStep = sw.Elapsed.TotalMilliseconds / n;
            PerfLines.Add($"单个模拟步（家园 + 远征 + 1 支突袭）平均 {perStep:F3} ms、最大 {WorldSimulation.MaxStepMs:F2} ms；3x 下每渲染帧（60 fps）约 3 步 ≈ {perStep * 3:F2} ms（Editor 托管代码）");

            // 热更层每帧开销与行进中的队伍数量无关（队伍是聚合体，远离镜头的不生成表现对象）：1 支 vs 200 支，镜头在远征地点。
            WorldView.FocusOn("site:" + FracturedCityLayout.RegionId);
            FrameOnce(0.05f);
            double one = MeasureFrames(200);
            for (int i = 0; i < 199; i++)
            {
                WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, "test", 1, 5000 + i, 5000, 0, 0);
            }
            double many = MeasureFrames(200);
            WorldView.FocusOn("home");
            FrameOnce(0.05f);
            int visibleMarkers = WorldPlanetView.VisibleMarkerCount;
            PerfLines.Add($"世界每帧（镜头在远征地点，1x）：1 支队伍 {one:F3} ms / 帧，200 支队伍 {many:F3} ms / 帧（含 200 支队伍的逐步推进）；镜头回家园时只为窗口附近的队伍生成标记（{visibleMarkers} 个）");
            Expect(visibleMarkers <= 2 && many < one + 2.0,
                $"200 支远处的队伍：镜头附近才有表现对象（可见标记 {visibleMarkers}）；每帧开销增加 {many - one:F3} ms（队伍逐步推进是 O(队伍数) 的纯数据运算，逐单位模拟在 FG0-ARCH-03 内核）");
            // 正向：镜头飞到远处那一簇队伍旁边，标记确实生成（避免“标记逻辑整个坏掉、数量 0 也通过”）。
            TransitGroupRecord far = WorldTransitSystem.Groups(s).Last();
            WorldView.FocusOn(far.GroupId);
            for (int i = 0; i < 12; i++)
            {
                FrameOnce(0.05f);
            }
            int nearMarkers = WorldPlanetView.VisibleMarkerCount;
            bool farShown = WorldPlanetView.TryGetMarkerPosition(far.GroupId, out Vector3 farPos)
                            && Vector2.Distance(new Vector2(farPos.x, farPos.z), WorldTransitSystem.Position(far)) < 0.01f;
            WorldView.FocusOn("home");
            for (int i = 0; i < 12; i++)
            {
                FrameOnce(0.05f);
            }
            int backMarkers = WorldPlanetView.VisibleMarkerCount;
            Expect(nearMarkers >= 1 && farShown && backMarkers <= 2,
                $"镜头飞到远处一簇队伍旁：生成 {nearMarkers} 个可见标记（目标队伍的标记位置 = 正式数据：{farShown}）；飞回家园后可见标记回到 {backMarkers} 个");
            WorldSimulation.UnloadAll();
        }

        private static double MeasureFrames(int frames)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                FrameOnce(1f / 60f);
            }
            return sw.Elapsed.TotalMilliseconds / frames;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>新战役：经真实入口载入家园、镜头在家园。<paramref name="dispatch"/>=true 时准备好出征条件（信号塔 / 装配站运转、战斗履带与第 3 台机器），
        /// 家园排上生产与修复，经真实派遣事务把 3 台机器派到破碎都市，并派出一支从规划层领地出发的突袭。</summary>
        private static CampaignState NewWorld(int seed, bool dispatch)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgsim-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            if (!dispatch)
            {
                return s;
            }
            s.Scrap = 170;
            SetBuilding(s, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational);
            SetBuilding(s, HomeValleyLayout.BuildingTypeSignalTower, BuildingConstructionState.Operational);
            SetBuilding(s, HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational);
            HomeValleyPowerGrid.Recompute(s);
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, new Vector2(4f, -4f), 120f, 120f);
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, new Vector2(6f, -4f), 100f, 100f);
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, new Vector2(8f, -4f), 100f, 100f);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            int[] roster = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                Fail($"种子 {seed}：真实派遣事务没有成功（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return s;
            }
            // 家园留下的机器：排上装配站生产与仓库修复（远征期间家园照常干活）。
            MachineRecord worker = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId
                                                                                  && m.ChassisId == HomeValleyLayout.Erc001ChassisId);
            HomeValleyFactory.FactoryOpResult produce = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHaulerId);
            if (!produce.Success)
            {
                Fail($"种子 {seed}：家园装配站排产失败（{produce.FailureReason}）");
            }
            if (worker != null)
            {
                HomeValleyWorkOrders.WorkOrderOpResult repair = HomeValleyWorkOrders.TryCreateRepair(s, HomeValleyLayout.BuildingTypeWarehouse, worker.LogicId);
                if (!repair.Success)
                {
                    Fail($"种子 {seed}：家园仓库修复工单创建失败（{repair.FailureReason}）");
                }
            }
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 12, out string failure);
            if (raid == null)
            {
                Fail($"种子 {seed}：突袭派不出（{failure}）");
            }
            return s;
        }

        /// <summary>远征编队命令与“远征侧真的在动”的度量。</summary>
        private sealed class ExpeditionOrders
        {
            public bool Issued;
            public string Description = string.Empty;
            public readonly Dictionary<int, Vector2> Start = new Dictionary<int, Vector2>();
            public readonly Dictionary<int, float> StartHealth = new Dictionary<int, float>();
            public float EnemyHealth0;
            public int EnemiesAlive0;

            /// <summary>当前（一遍跑完、Snapshot 已写回实时状态之后）与下令时相比：机器最大位移、敌人 / 机器血量与存活变化。</summary>
            public string Measure(out bool moved, out bool engaged)
            {
                CampaignState s = CampaignSession.Current;
                float maxMove = 0f;
                float machineHpLost = 0f;
                int machinesLost = 0;
                foreach (KeyValuePair<int, Vector2> kv in Start)
                {
                    if (!MachineRegistry.TryGetRecord(kv.Key, out MachineRecord rec) || rec == null)
                    {
                        continue;
                    }
                    maxMove = Mathf.Max(maxMove, Vector2.Distance(rec.WorldPosition, kv.Value));
                    machineHpLost += Mathf.Max(0f, StartHealth[kv.Key] - (rec.IsAlive ? rec.Health : 0f));
                    machinesLost += rec.IsAlive ? 0 : 1;
                }
                List<RegionEnemyRecord> enemies = (s?.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                    .Where(e => e != null && e.RegionId == FracturedCityLayout.RegionId).ToList();
                float enemyHp = enemies.Where(e => e.IsAlive).Sum(e => e.Health);
                int enemiesAlive = enemies.Count(e => e.IsAlive);
                moved = maxMove > 2f;
                engaged = enemiesAlive < EnemiesAlive0 || enemyHp < EnemyHealth0 - 0.01f || machineHpLost > 0.01f;
                IReadOnlyList<string> events = WorldSimulation.FracturedCity?.SquadCommands.RecentEvents;
                string recent = events == null ? string.Empty : string.Join(" / ", events.Skip(Math.Max(0, events.Count - 3)));
                return $"远征机器最大位移 {maxMove:F1} 格；敌人存活 {EnemiesAlive0}→{enemiesAlive}、血量合计 {EnemyHealth0:F0}→{enemyHp:F0}；" +
                       $"远征机器损血 {machineHpLost:F0}、阵亡 {machinesLost} 台（编队最近事件：{recent}）";
            }
        }

        /// <summary>给破碎都市里的远征编队下达命令：编号最小的一台 Move 到它与目标敌人的中点，其余 Attack 离编队重心最近的巡逻敌人。
        /// Demo 的编队命令是控制器内存态、不进存档（FG-GAP-018），所以每一遍读档后在同一时刻（第 0 帧、时钟同一步）重新下达同样的命令。</summary>
        private static ExpeditionOrders IssueExpeditionOrders()
        {
            var o = new ExpeditionOrders();
            CampaignState s = CampaignSession.Current;
            FracturedCityController city = WorldSimulation.FracturedCity;
            if (s == null || city == null || !city.IsLoaded)
            {
                o.Description = "远征地点没有载入";
                return o;
            }
            int[] ids = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId)
                .OrderBy(m => m.LogicId).Select(m => m.LogicId).ToArray();
            Vector2 centroid = Vector2.zero;
            foreach (int id in ids)
            {
                Vector2? p = WorldSimulation.LivePosition(id);
                if (p.HasValue && MachineRegistry.TryGetRecord(id, out MachineRecord rec))
                {
                    o.Start[id] = p.Value;
                    o.StartHealth[id] = rec.Health;
                    centroid += p.Value;
                }
            }
            List<RegionEnemyRecord> enemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Where(e => e != null && e.IsAlive && e.RegionId == FracturedCityLayout.RegionId).ToList();
            o.EnemyHealth0 = enemies.Sum(e => e.Health);
            o.EnemiesAlive0 = enemies.Count;
            if (o.Start.Count < 2 || enemies.Count == 0)
            {
                o.Description = $"远征机器 {o.Start.Count} 台、巡逻敌人 {enemies.Count} 个，下不了命令";
                return o;
            }
            centroid /= o.Start.Count;
            RegionEnemyRecord target = enemies
                .OrderBy(e => (e.Position - centroid).sqrMagnitude)
                .ThenBy(e => e.EnemyInstanceId, StringComparer.Ordinal).First();
            // 攻击手 = 战斗履带（ERC-003，装配里有主武器出口）；移动手 = 其余编号最小的一台（工程底盘没有主武器）。
            int[] combat = ids.Where(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.ChassisId == HomeValleyLayout.Erc003ChassisId).ToArray();
            int[] others = ids.Except(combat).ToArray();
            if (combat.Length == 0 || others.Length == 0)
            {
                o.Description = $"远征编队里战斗履带 {combat.Length} 台、其它 {others.Length} 台，凑不齐攻击手与移动手";
                return o;
            }
            int mover = others[0];
            Vector2 moveTo = o.Start[mover] + (target.Position - o.Start[mover]) * 0.5f;
            city.SquadCommands.DebugSelectMany(combat);
            int attackers = city.SquadCommands.Selection.Count;
            city.SquadCommands.IssueAttack(target.EnemyInstanceId, paused: false);
            city.SquadCommands.DebugSelectMany(new[] { mover });
            int movers = city.SquadCommands.Selection.Count;
            city.SquadCommands.IssueMoveTo(moveTo, paused: false);
            city.SquadCommands.ClearSelection();
            bool moveActive = city.SquadCommands.TryGetActiveCommandKind(mover, out RegionCommandKind moveKind) && moveKind == RegionCommandKind.Move;
            bool attackActive = combat.Any(id => city.SquadCommands.TryGetActiveCommandKind(id, out RegionCommandKind k) && k == RegionCommandKind.Attack);
            o.Issued = attackers >= 1 && movers == 1 && moveActive && attackActive;
            o.Description = $"{attackers} 台战斗履带攻击最近的巡逻敌人（距编队 {Vector2.Distance(centroid, target.Position):F1} 格），1 台移动 " +
                            $"{Vector2.Distance(o.Start[mover], moveTo):F1} 格（Move 在执行 {moveActive}，Attack 在执行 {attackActive}）";
            return o;
        }

        private static void SetBuilding(CampaignState s, string typeId, BuildingConstructionState construction)
        {
            BuildingRecord record = s.BuildingRecords.First(b => b.BuildingTypeId == typeId);
            record.ConstructionState = construction;
        }

        /// <summary>把当前世界存成出发存档（真实存档路径：先写回实时状态、导出机器记录）。</summary>
        private static void SaveBaseline()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("出发存档写入失败：" + r.Message);
            }
        }

        /// <summary>每一遍都从出发存档的一份新拷贝读档（运行中的自动存档写进拷贝槽，不污染出发存档），按主菜单“继续”的顺序恢复整个世界。</summary>
        private static void RestoreBaseline(string observe)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读出发存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            var ruinsIds = r.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
            if (ruinsIds.Length > 0)
            {
                WorldSimulation.LoadFracturedCity(ruinsIds, resume: true);
            }
            if (observe != null)
            {
                WorldView.Observe(observe);
            }
        }

        /// <summary>与主菜单“继续”（GameRoot.ResumeCampaign）同一顺序：家园总是载入，存档时在外的远征一起载入，镜头放在远征地点。</summary>
        private static string ResumeWorldLikeMenu()
        {
            string region = GameRoot.ResolveResumeRegion(CampaignSession.Current);
            WorldSimulation.LoadHome(resume: true);
            if (region == FracturedCityLayout.RegionId)
            {
                GameRoot.ResumeFracturedCity();
            }
            else
            {
                WorldView.Observe(HomeValleyLayout.RegionId);
            }
            return WorldView.ObservedSiteId;
        }

        private static void FrameOnce(float realDt, long tickLimit = long.MaxValue)
        {
            // 离开世界（UnloadAll → InputRouter.Reset）会把输入后端复位成真键盘：每帧确认仍是本自检的假键盘。
            if (!ReferenceEquals(InputRouter.Reader, _reader))
            {
                InputRouter.DebugSetReader(_reader);
            }
            WorldSimulation.Frame(realDt, tickLimit);
            _reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        /// <summary>逐帧推进到第 <paramref name="targetTicks"/> 步（最后一帧精确停在目标步）。返回帧数。</summary>
        private static int RunFrames(long targetTicks, float realDt, Action<int> perFrame)
        {
            int frame = 0;
            int guard = 0;
            while (GameClock.Ticks < targetTicks && guard < 2_000_000)
            {
                perFrame?.Invoke(frame);
                FrameOnce(realDt, targetTicks);
                frame++;
                guard++;
            }
            return frame;
        }

        private static int HomeMachineCount() =>
            MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId);

        private static double Dist(TransitGroupRecord g) =>
            g == null ? 0 : Math.Sqrt((g.TargetX - g.PosX) * (g.TargetX - g.PosX) + (g.TargetY - g.PosY) * (g.TargetY - g.PosY));

        private static float FactoryProgress(CampaignState s)
        {
            float sum = 0f;
            foreach (FactoryQueueItemRecord q in s?.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>())
            {
                if (q != null)
                {
                    sum += q.Progress + (q.State == FactoryQueueState.Completed ? 1000f : 0f);
                }
            }
            return sum;
        }

        private static string FactoryDigest(CampaignState s) =>
            string.Join(";", (s.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>()).Select(q => q.State + ":" + q.Progress.ToString("R", CultureInfo.InvariantCulture)));

        private static string FactoryDigestFrom(Dictionary<string, string> snap) => snap.GetValueOrDefault("·factory") ?? string.Empty;

        private static long Parse(Dictionary<string, string> snap, string key) =>
            snap.TryGetValue(key, out string v) && long.TryParse(v, out long x) ? x : -1;

        private static Dictionary<FeedbackCueId, int> CueCounts()
        {
            var d = new Dictionary<FeedbackCueId, int>();
            foreach (FeedbackCueId id in Enum.GetValues(typeof(FeedbackCueId)))
            {
                d[id] = FeedbackCues.CountOf(id);
            }
            return d;
        }

        private static readonly Regex HexId = new Regex(@"(?<=[:_\-])[0-9a-f]{8}(?:[0-9a-f]{24})?(?![0-9a-f])", RegexOptions.Compiled);

        /// <summary>存档状态的逐字段快照（真实存档路径：写回实时状态 + 导出机器记录 → JsonUtility 序列化 → 拍平成“路径 = 值”）。
        /// 排除两类本来就按真实时间记录的内容：通知历史（FGR-UX-020 的聚合窗口按真实时间，所以只比较各类通知条数）与存档历史（写盘时间）；
        /// GUID 片段按出现顺序编号（同一顺序创建的实体编号相同）。另外加上反馈时刻计数与几个摘要项。</summary>
        private static Dictionary<string, string> Snapshot(Dictionary<FeedbackCueId, int> cueBase, out string summary)
        {
            CampaignState s = CampaignSession.Current;
            WorldSimulation.SyncAllForSave();
            MachineRegistry.ExportToCampaignState(s);
            string json = JsonUtility.ToJson(s);
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            MiniJson.Flatten(json, flat);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in flat.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Key.StartsWith("Notifications", StringComparison.Ordinal) || kv.Key.StartsWith("SaveHistory", StringComparison.Ordinal))
                {
                    continue;
                }
                string v = HexId.Replace(kv.Value, m =>
                {
                    if (!ids.TryGetValue(m.Value, out string token))
                    {
                        token = "#id" + ids.Count;
                        ids[m.Value] = token;
                    }
                    return token;
                });
                result[kv.Key] = v;
            }
            foreach (KeyValuePair<FeedbackCueId, int> kv in CueCounts())
            {
                result["·cue." + kv.Key] = (kv.Value - cueBase[kv.Key]).ToString(CultureInfo.InvariantCulture);
            }
            foreach (IGrouping<string, NotificationEntry> g in NotificationCenter.History.GroupBy(e => e.Type.Id))
            {
                result["·notify." + g.Key] = g.Sum(e => e.Count).ToString(CultureInfo.InvariantCulture);
            }
            result["·clock.ticks"] = GameClock.Ticks.ToString(CultureInfo.InvariantCulture);
            result["·machines.home"] = HomeMachineCount().ToString(CultureInfo.InvariantCulture);
            result["·machines.ruins"] = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == FracturedCityLayout.RegionId).ToString(CultureInfo.InvariantCulture);
            result["·enemies.ruins"] = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Count(e => e.RegionId == FracturedCityLayout.RegionId && e.IsAlive).ToString(CultureInfo.InvariantCulture);
            TransitGroupRecord raid = WorldTransitSystem.Groups(s).FirstOrDefault();
            result["·raid.state"] = raid?.State.ToString() ?? "none";
            result["·raid.pos"] = raid == null ? "none" : raid.PosX.ToString("R", CultureInfo.InvariantCulture) + "," + raid.PosY.ToString("R", CultureInfo.InvariantCulture);
            result["·factory"] = FactoryDigest(s);
            result["·warehouse.construction"] = s.BuildingRecords.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse)?.ConstructionState.ToString() ?? "none";
            summary = $"终态摘要：家园机器 {result["·machines.home"]}、远征机器 {result["·machines.ruins"]}、远征敌人 {result["·enemies.ruins"]}、突袭 {result["·raid.state"]}、生产队列 [{result["·factory"]}]、仓库 {result["·warehouse.construction"]}";
            return result;
        }

        private static List<string> DiffKeys(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var diffs = new List<string>();
            if (a == null || b == null)
            {
                diffs.Add("快照为空");
                return diffs;
            }
            foreach (KeyValuePair<string, string> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out string other))
                {
                    diffs.Add($"{kv.Key} 只在一边");
                }
                else if (!string.Equals(kv.Value, other, StringComparison.Ordinal))
                {
                    diffs.Add($"{kv.Key}: {Trim(kv.Value)} ≠ {Trim(other)}");
                }
            }
            foreach (string k in b.Keys)
            {
                if (!a.ContainsKey(k))
                {
                    diffs.Add($"{k} 只在另一边");
                }
            }
            return diffs;
        }

        private static string Trim(string v) => v.Length > 40 ? v.Substring(0, 40) + "…" : v;

        /// <summary>走按钮的真实点击回调（与冒烟测试同一做法：Clickable.Invoke）。</summary>
        private static void ClickButton(Button b)
        {
            if (b == null || b.clickable == null)
            {
                Fail("按钮不存在或没有点击回调");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
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
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail($"{check.Method.Name} 收尾抛异常：{e.Message}");
                }
                GameClock.ResetSession();
                _reader.EndFrame();
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
            _report.AppendLine(text);
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame() => _down.Clear();

            public bool GetKey(KeyCode key) => _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            // 光标在窗口外：不触发镜头的屏幕边缘推屏（batchmode 下 Screen 尺寸不可靠）。
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        /// <summary>最小 JSON 拍平器（只处理 JsonUtility 的输出：对象、数组、字符串、数字、布尔）。路径形如 a.b[3].c。</summary>
        private static class MiniJson
        {
            public static void Flatten(string json, Dictionary<string, string> into)
            {
                int i = 0;
                Value(json, ref i, string.Empty, into);
            }

            private static void Skip(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                {
                    i++;
                }
            }

            private static void Value(string s, ref int i, string path, Dictionary<string, string> into)
            {
                Skip(s, ref i);
                char c = s[i];
                if (c == '{')
                {
                    i++;
                    Skip(s, ref i);
                    if (s[i] == '}')
                    {
                        i++;
                        into[path] = "{}";
                        return;
                    }
                    while (true)
                    {
                        Skip(s, ref i);
                        string key = Str(s, ref i);
                        Skip(s, ref i);
                        i++; // ':'
                        Value(s, ref i, path.Length == 0 ? key : path + "." + key, into);
                        Skip(s, ref i);
                        if (s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        i++; // '}'
                        return;
                    }
                }
                if (c == '[')
                {
                    i++;
                    Skip(s, ref i);
                    if (s[i] == ']')
                    {
                        i++;
                        into[path] = "[]";
                        return;
                    }
                    int n = 0;
                    while (true)
                    {
                        Value(s, ref i, path + "[" + n + "]", into);
                        n++;
                        Skip(s, ref i);
                        if (s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        i++; // ']'
                        into[path + ".count"] = n.ToString(CultureInfo.InvariantCulture);
                        return;
                    }
                }
                if (c == '"')
                {
                    into[path] = Str(s, ref i);
                    return;
                }
                int start = i;
                while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }
                into[path] = s.Substring(start, i - start);
            }

            private static string Str(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        sb.Append(s[i]);
                        i++;
                    }
                    sb.Append(s[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
        }
    }
}
