using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-SIG-04 断链与安全模式的自动验收（FG01 FGR-SIG-040、041；FGT-SIG-006；卡片负向：边缘反复进出、连续死亡回弹、没有回弹目标时回到核心）。
    /// 全部起真实系统：整个世界（家园 / 破碎都市控制器 + 战斗内核 + 统一时钟 + 全局镜头 + 区域接管系统每帧 Tick）、真实存档文件、真 UXML HUD 与命令栏——
    /// 行为坏了会失败：
    /// A 数据（调参读表、文本键中英、图标、反馈种类）；B FGT-SIG-006 四种原因各触发一次（走出覆盖 / 干扰场 / 静默夜 / 阵亡），机器进入安全模式、
    /// 只跑本地常规固件；B' 覆盖源（核心、信号塔、断开的信号塔）；C 负向（边缘反复进出、安全模式边缘反复进出、连续死亡回弹、没有合适回弹目标、
    /// 过渡中静默夜开始）；D 暂停与倍速；E 家园后台与观察一致；F 存读档；G 性能；H 界面（HUD 预警行、命令栏机器列表标记、头顶图标、地图预警圈）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgSignalLinkSelfCheck
    {
        private const string HudUxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const string CommandBarUxmlPath = "Assets/GameRes/Raw/UI/RegionCommand/RegionCommandBar.uxml";
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_sig04_cannon_up";
        private const string BpGunPlain = "bp_selfcheck_sig04_gun";
        private const float FrameDt = 0.05f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<SignalLinkBreak> Breaks = new List<SignalLinkBreak>();
        private static readonly List<SignalSafeModeChange> SafeChanges = new List<SignalSafeModeChange>();
        private static readonly List<float> NightWarnings = new List<float>();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 断链与安全模式")]
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
            Line("\n[断链] 断链与安全模式（FG1-SIG-04）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig04-selfcheck-" + Guid.NewGuid().ToString("N"));
            Action<SignalLinkBreak> onBreak = b => Breaks.Add(b);
            Action<SignalSafeModeChange> onSafe = c => SafeChanges.Add(c);
            Action<float> onNight = t => NightWarnings.Add(t);
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => FrameDt;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                TEngine.GameEvent.AddEventListener(SignalLinkService.LinkBrokenEvent, onBreak);
                TEngine.GameEvent.AddEventListener(SignalLinkService.SafeModeChangedEvent, onSafe);
                TEngine.GameEvent.AddEventListener(SignalLinkService.SilentNightWarningEvent, onNight);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckCoverageBreak);
                Step(CheckCoverageSources);
                Step(CheckJamBreak);
                Step(CheckSilentNight);
                Step(CheckDeathRebound);
                Step(CheckNoSuitableTarget);
                Step(CheckEdgeFlapping);
                Step(CheckWarningLineRegressions);
                Step(CheckPauseSpeed);
                Step(CheckBackgroundConsistency);
                Step(CheckSaveLoad);
                Step(CheckPerformance);
                Step(CheckUi);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"断链自检抛异常：{e}");
            }
            finally
            {
                TEngine.GameEvent.RemoveEventListener(SignalLinkService.LinkBrokenEvent, onBreak);
                TEngine.GameEvent.RemoveEventListener(SignalLinkService.SafeModeChangedEvent, onSafe);
                TEngine.GameEvent.RemoveEventListener(SignalLinkService.SilentNightWarningEvent, onNight);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                SignalUplinkService.ResetForTests();
                SignalLinkService.ResetForTests();
                SignalCoverageService.ResetForTests();
                SignalLinkView.Clear();
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                UiTooltip.ResetForTests();
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
            Line($"  · [断链] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：覆盖 / 宽限 / 安全模式调参读表；代码用到的 signal.link.* / signal.safe_mode.* 文本键中英齐全；安全模式图标与预警反馈种类已登记");
            var expect = new Dictionary<string, float>
            {
                ["signal.coverage.core_radius"] = 150f,
                ["signal.coverage.tower_radius"] = 300f,
                ["signal.coverage.edge_warn_cells"] = 15f,
                ["signal.coverage.edge_warn_hysteresis_cells"] = 3f,
                ["signal.coverage.refresh_seconds"] = 0.5f,
                ["signal.link.grace_seconds"] = 2f,
                ["signal.link.warn_repeat_seconds"] = 3f,
                ["signal.safe_mode.exit_seconds"] = 2f,
                ["signal.safe_mode.check_seconds"] = 0.5f,
            };
            var bad = expect.Where(kv => !GridContent.TryGetTuning(kv.Key, out float v) || !Mathf.Approximately(v, kv.Value)).Select(kv => kv.Key).ToList();
            Expect(bad.Count == 0 && Mathf.Approximately(SignalCoverageService.CoreRadius, 150f) && Mathf.Approximately(SignalCoverageService.TowerRadius, 300f)
                   && Mathf.Approximately(SignalLinkService.GraceSeconds, 2f) && Mathf.Approximately(SignalLinkService.SafeModeExitSeconds, 2f)
                   && Mathf.Approximately(SignalLinkService.EdgeWarnHysteresisCells, 3f),
                $"fg.TbHomeTuning：核心覆盖 150 格、信号塔 300 格（FGR-SIG-050 初值）、边缘预警 15 格（回差 3 格）、宽限 2 秒、安全模式退出 2 秒{(bad.Count > 0 ? "——不符：" + string.Join(",", bad) : string.Empty)}");

            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files = { "Campaign/Signal/SignalLinkService.cs", "Campaign/Regions/RegionControlSystem.cs", "UI/RegionCommand/RegionCommandBarUIToolkit.cs" };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)), "\"(signal\\.(?:link|safe_mode)\\.[a-z_.]+)\""))
                {
                    if (!GridContent.TryGetTuning(m.Groups[1].Value, out _))
                    {
                        keys.Add(m.Groups[1].Value);
                    }
                }
            }
            var missing = keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k, GameLanguage.En))
                                          || GameText.Get(k, GameLanguage.En) == GameText.Get(k, GameLanguage.ZhCn)
                                          || Regex.IsMatch(GameText.Get(k, GameLanguage.En), "[\\u4e00-\\u9fff]")).ToList();
            Expect(keys.Count >= 16 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个 signal.link.* / signal.safe_mode.* 文本键（下限 16）都在 fg.TbLocText 里、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");
            string controlSrc = File.ReadAllText(Path.Combine(logic, "Campaign/Regions/RegionControlSystem.cs"));
            Expect(!controlSrc.Contains("秒内离开干扰区可恢复") && !controlSrc.Contains("失联，控制权已收回"),
                "区域接管系统里原来硬编码的中文失联提示已改走文本键");

            string png = Path.Combine(Application.dataPath, "GameRes/Raw/UI/Icons/" + SignalLinkService.SafeModeIconId + ".png");
            Expect(ContentIcons.AllIconIds().Contains(SignalLinkService.SafeModeIconId) && File.Exists(png) && File.Exists(png + ".meta"),
                $"安全模式图标 {SignalLinkService.SafeModeIconId} 登记进图标预载表，贴图与 .meta 存在（盾形 + 断开的信号，外形不同于建筑状态图标）");
            FeedbackCueDef warn = FeedbackCueCatalog.Get(FeedbackCueId.SignalLinkWarning);
            FeedbackCueDef lost = FeedbackCueCatalog.Get(FeedbackCueId.SignalLost);
            Expect(warn != null && warn.Id == FeedbackCueId.SignalLinkWarning && !string.IsNullOrEmpty(warn.SfxId) && lost != null && !string.IsNullOrEmpty(lost.SfxId),
                $"反馈种类：断链用“失联”（{lost?.SfxId}），接近覆盖边缘 / 静默夜将至用新增的“信号预警”（{warn?.SfxId}）");
        }

        // ── B1. 走出覆盖（FGT-SIG-006 ①）＋ 地图预警 ─────────────────────────────

        private static void CheckCoverageBreak()
        {
            Line("  · B1. FGT-SIG-006 ①走出覆盖：接近边缘预警（HUD + 预警音 + 地图覆盖圈）→ 走出去宽限 2 秒 → 弹回归还核心、机器进入安全模式只跑本地常规固件 → 回到覆盖 2 秒后退出");
            CampaignState s = NewHome(8401);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            HomeValleyController home = WorldSimulation.Home;
            CombatSite site = home.Combat;
            CommitVia(a);
            SignalCoverageSample atCore = SignalCoverageService.Sample(HomeValleyLayout.RegionId, MachinePos(site, a));
            Vector2 core = atCore.SourceCenter;
            Expect(atCore.Covered && atCore.Bounded && atCore.SourceKind == SignalCoverageSourceKind.Core && Mathf.Approximately(atCore.SourceRadius, 150f)
                   && Vector2.Distance(core, HomeValleyLayout.Core.Position) < 3f,
                $"家园里接入的机器在归还核心的覆盖里（圆心 {core}，半径 {atCore.SourceRadius}，还剩 {atCore.MarginCells:F1} 格）；信号塔开局损坏不提供覆盖");

            AdvanceRealTime(5f);
            int warnCues0 = FeedbackCues.CountOf(FeedbackCueId.SignalLinkWarning);
            Place(site, a, core + new Vector2(139.5f, 0f));
            Frames(2);
            string statusEdge = SignalUplinkService.StatusLine(s);
            Expect(SignalLinkService.EdgeWarningActive && !SignalLinkService.InGrace && SignalPresence.CurrentMachineLogicId == a
                   && statusEdge == SignalLinkService.WarningLine && statusEdge.Contains(SignalPresence.MachineLabel(a)) && statusEdge.Contains("10")
                   && FeedbackCues.CountOf(FeedbackCueId.SignalLinkWarning) == warnCues0 + 1,
                $"离边缘 10 格：HUD 预警“{statusEdge}”，预警音 1 次，信号仍在机器里");
            Expect(SignalLinkView.RingVisible && !SignalLinkView.RingDanger && Mathf.Approximately(SignalLinkView.RingRadius, 150f)
                   && Vector2.Distance(SignalLinkView.RingCenter, core) < 1e-3f && SignalLinkView.RingPointCount >= 64,
                $"地图上画出罩着它的覆盖圈（黄色，圆心 = 归还核心，半径 {SignalLinkView.RingRadius}，{SignalLinkView.RingPointCount} 个点）");

            BinGames.Sim.Combat.CombatReaction reactUplinked = WeaponOf(site, a).Reaction;
            int lost0 = FeedbackCues.CountOf(FeedbackCueId.SignalLost);
            int breaks0 = Breaks.Count;
            double graceStart = GameClock.GameSeconds;
            Place(site, a, core + new Vector2(160f, 0f));
            Frames(2);
            string graceLine = SignalUplinkService.StatusLine(s);
            Expect(SignalLinkService.InGrace && SignalLinkService.GraceReason == SignalLinkBreakReason.OutOfCoverage && SignalPresence.CurrentMachineLogicId == a
                   && home.Control.Availability == RegionControlAvailability.Suspended && SignalLinkView.RingDanger
                   && FeedbackCues.CountOf(FeedbackCueId.SignalLost) == lost0 + 1
                   && graceLine == GameText.Format("signal.link.warn.out_of_coverage", SignalPresence.MachineLabel(a), "2"),
                $"走出覆盖 10 格：进入宽限（“{graceLine}”），失联预警音 1 次，覆盖圈变红，信号还在机器里");
            int n = 0;
            while (SignalPresence.CurrentMachineLogicId == a && n++ < 120)
            {
                Frames(1);
            }
            double graceUsed = GameClock.GameSeconds - graceStart;
            Frames(12);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
            MachineCombatResolution pilot = MachineLoadoutRegistry.ResolveForPilot(s, a, s.RandomSeed);
            MachineCombatResolution ai = MachineLoadoutRegistry.ResolveForAi(s, a, s.RandomSeed);
            Expect(SignalPresence.AtCore && home.PossessedMachineLogicId == null && WorldView.Director.Mode == ViewMode.Strategy
                   && Math.Abs(graceUsed - 2.0) <= 2 * FrameDt + 1e-6 && SignalLinkService.LastBreakReason == SignalLinkBreakReason.OutOfCoverage
                   && Breaks.Count == breaks0 + 1 && Breaks.Last().LogicId == a && Breaks.Last().EnteredSafeMode && Breaks.Last().Reason == SignalLinkBreakReason.OutOfCoverage,
                $"宽限 {graceUsed:F2} 游戏秒耗尽：断链（事件 1 次），信号弹回归还核心，镜头回到战略");
            Expect(SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.break.out_of_coverage", SignalPresence.MachineLabel(a))
                   && FeedbackCues.CountOf(FeedbackCueId.SignalLost) == lost0 + 2,
                $"弹回时 HUD 给原因、有音效：“{SignalUplinkService.LastFeedbackText}”");
            Expect(SignalLinkService.IsInSafeMode(s, a) && SignalLinkService.SafeModeReason(s, a) == SignalLinkBreakReason.OutOfCoverage
                   && !info.Uplinked && pilot.Success && pilot.Preview.UplinkFirmwareIds.Length == 0
                   && Mathf.Approximately(pilot.Preview.TotalNormalizedDamage, ai.Preview.TotalNormalizedDamage)
                   && reactUplinked != BinGames.Sim.Combat.CombatReaction.None && WeaponOf(site, a).Reaction == BinGames.Sim.Combat.CombatReaction.None,
                $"安全模式：接入口为空、只跑本地常规固件（结算 = AI 配置；武器反应 {reactUplinked} → 无，不再带信号核的过载），不再被信号驾驶");
            HomeValleyMachineMarker marker = null;
            site.TryGetMachineMarker(a, out marker);
            Expect(marker?.View != null && SignalLinkView.BadgeWanted(a) && SignalLinkView.BadgeFor(a).transform.parent == marker.View.transform,
                "头顶挂上安全模式图标（挂在机器表现对象下，随机器移动）");
            UplinkFailure again = SignalUplinkService.Validate(s, a, out string againText);
            Expect(again == UplinkFailure.OutOfCoverage && !string.IsNullOrEmpty(againText),
                $"覆盖外的机器不能再接入：“{againText}”");

            // 回到覆盖里：条件消失持续 2 游戏秒才退出（与是否被观察无关，世界模拟步里判定）。
            int exits0 = SignalLinkService.SafeModeExitCount;
            Place(site, a, core + new Vector2(20f, 0f));
            double back = GameClock.GameSeconds;
            int guard = 0;
            while (SignalLinkService.IsInSafeMode(s, a) && guard++ < 60 * 6)
            {
                WorldSimulation.StepMany(1);
            }
            double took = GameClock.GameSeconds - back;
            Frames(1);
            Expect(!SignalLinkService.IsInSafeMode(s, a) && took >= 2.0 - 1e-6 && took <= 2.0 + SignalLinkService.SafeModeCheckSeconds + 1e-6
                   && SignalLinkService.SafeModeExitCount == exits0 + 1 && SignalUplinkService.LastFeedbackText == GameText.Format("signal.safe_mode.exit", SignalPresence.MachineLabel(a))
                   && !SignalLinkView.BadgeWanted(a),
                $"回到覆盖里 {took:F2} 游戏秒后退出安全模式（“{SignalUplinkService.LastFeedbackText}”），头顶图标收起");
            Expect(SignalUplinkService.Validate(s, a, out _) == UplinkFailure.None && !SignalLinkView.RingVisible,
                "退出后可以再接入；信号不在机器里时地图预警圈收起");
        }

        // ── B'. 覆盖源 ───────────────────────────────────────────────────────────

        private static void CheckCoverageSources()
        {
            Line("  · B'. 覆盖源（FGR-SIG-050 本 Story 部分）：信号塔修好并通电后 300 格覆盖；与核心不连通的信号塔不提供覆盖；远征地点以接入点为根（FG1-SIG-07）");
            CampaignState s = NewHome(8402);
            BuildingRecord tower = s.BuildingRecords.FirstOrDefault(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            Vector2 far = core + new Vector2(200f, 0f);
            bool beforeRepair = !SignalCoverageService.Sample(HomeValleyLayout.RegionId, far).Covered;
            tower.ConstructionState = BuildingConstructionState.Operational;
            tower.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
            SignalCoverageSample withTower = SignalCoverageService.Sample(HomeValleyLayout.RegionId, far);
            tower.PowerState = BuildingPowerState.Unpowered;
            SignalCoverageService.Invalidate();
            bool unpowered = !SignalCoverageService.Sample(HomeValleyLayout.RegionId, far).Covered;
            tower.PowerState = BuildingPowerState.Powered;
            Expect(beforeRepair && withTower.Covered && withTower.SourceKind == SignalCoverageSourceKind.Tower && Mathf.Approximately(withTower.SourceRadius, 300f) && unpowered,
                $"离核心 200 格：信号塔损坏时在覆盖外；修好通电后被信号塔覆盖（半径 {withTower.SourceRadius}）；断电后又回到覆盖外");

            var lonely = new BuildingRecord
            {
                BuildingId = "selfcheck:lonely_tower",
                BuildingTypeId = HomeValleyLayout.BuildingTypeSignalTower,
                RegionId = HomeValleyLayout.RegionId,
                Position = core + new Vector2(2000f, 0f),
                ConstructionState = BuildingConstructionState.Operational,
                PowerState = BuildingPowerState.Powered,
            };
            s.BuildingRecords = s.BuildingRecords.Append(lonely).ToArray();
            SignalCoverageService.Invalidate();
            SignalCoverageSample atLonely = SignalCoverageService.Sample(HomeValleyLayout.RegionId, lonely.Position);
            int connected = 0;
            int disconnected = 0;
            for (int i = 0; i < SignalCoverageService.SourceCount; i++)
            {
                SignalCoverageService.TryGetSource(i, out _, out _, out _, out bool c);
                if (c)
                {
                    connected++;
                }
                else
                {
                    disconnected++;
                }
            }
            Expect(!atLonely.Covered && connected == 2 && disconnected == 1,
                $"离核心 2000 格的信号塔与核心不连通（覆盖源 {connected} 个连通、{disconnected} 个断开）：它脚下也在覆盖外");
            // FG1-SIG-07 改了规则（DEBT-FG1SIG04-01 关闭）：远征地点有覆盖边界——以远征接入点为根（150 格罩住整个 Demo 地点），离得太远就在覆盖外。
            SignalCoverageSample cityFar = SignalCoverageService.Sample(FracturedCityLayout.RegionId, new Vector2(9999f, 9999f));
            SignalCoverageSample cityIn = SignalCoverageService.Sample(FracturedCityLayout.RegionId, FracturedCityLayout.EntryEvac.Position + new Vector2(0f, 20f));
            Expect(!cityFar.Covered && cityFar.Bounded && cityIn.Covered && cityIn.SourceKind == SignalCoverageSourceKind.ExpeditionUplink,
                "远征地点（破碎都市）有覆盖边界（FG1-SIG-07）：接入点附近在覆盖里，远处（9999, 9999）在覆盖外");
        }

        // ── B2. 干扰场（FGT-SIG-006 ②）───────────────────────────────────────────

        private static void CheckJamBreak()
        {
            Line("  · B2. FGT-SIG-006 ②进入干扰场：宽限 2 秒 → 弹回归还核心、安全模式；离开干扰场 2 秒后退出");
            CampaignState s = NewState(8403);
            EquipOverload(s);
            int r1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -24f), 20000f);
            FracturedCityController city = OpenCity(s, r1);
            WorldView.Observe(city.SiteId);
            // 接入前给它下“守备”：断链后要继续执行这条命令（FGR-SIG-041“继续执行最后一条命令或 AI 教义”，沿用 FC-REQ-051 的释放规则）。
            city.SquadCommands.DebugSelectMany(new[] { r1 });
            city.SquadCommands.IssueGuardHere(paused: false);
            CommitVia(r1);
            AdvanceRealTime(5f);
            Vector2 node = FracturedCityLayout.ListeningNode.Position;
            Vector2 inJam = node + new Vector2(FracturedCityLayout.JammerRadius - 1.5f, 0f);
            int breaks0 = Breaks.Count;
            double start = GameClock.GameSeconds;
            Place(city.Combat, r1, inJam);
            Frames(1);
            string warn = SignalUplinkService.StatusLine(s);
            Expect(SignalLinkService.InGrace && SignalLinkService.GraceReason == SignalLinkBreakReason.Jammed && SignalPresence.CurrentMachineLogicId == r1
                   && warn.Contains(SignalPresence.MachineLabel(r1)),
                $"进入干扰场：宽限中（“{warn}”）");
            int n = 0;
            while (SignalPresence.CurrentMachineLogicId == r1 && n++ < 120)
            {
                Place(city.Combat, r1, inJam);
                Frames(1);
            }
            double used = GameClock.GameSeconds - start;
            Expect(SignalPresence.AtCore && city.PossessedMachineLogicId == null && SignalLinkService.LastBreakReason == SignalLinkBreakReason.Jammed
                   && Breaks.Count == breaks0 + 1 && Math.Abs(used - 2.0) <= 2 * FrameDt + 1e-6
                   && SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.break.jammed", SignalPresence.MachineLabel(r1)),
                $"宽限 {used:F2} 游戏秒耗尽：断链，信号弹回归还核心（“{SignalUplinkService.LastFeedbackText}”）");
            city.Combat.TryGetMachineWeapon(r1, out MachineWeaponInfo info);
            bool guardKept = city.SquadCommands.TryGetActiveCommandKind(r1, out RegionCommandKind kind) && kind == RegionCommandKind.Guard;
            Expect(guardKept, $"断链后机器继续执行接入前的最后一条命令（{kind}），不是原地发呆也不是自作主张");
            Expect(SignalLinkService.IsInSafeMode(s, r1) && SignalLinkService.SafeModeReason(s, r1) == SignalLinkBreakReason.Jammed && !info.Uplinked
                   && SignalUplinkService.Validate(s, r1, out _) == UplinkFailure.Jammed,
                "机器进入安全模式（原因：干扰场），不再按接入结算；在干扰场里不能再接入");
            Place(city.Combat, r1, node + new Vector2(FracturedCityLayout.JammerRadius + 6f, 0f));
            int guard = 0;
            while (SignalLinkService.IsInSafeMode(s, r1) && guard++ < 60 * 6)
            {
                WorldSimulation.StepMany(1);
            }
            Expect(!SignalLinkService.IsInSafeMode(s, r1) && SignalUplinkService.Validate(s, r1, out _) == UplinkFailure.None,
                "离开干扰场 2 秒后退出安全模式，可以再接入");
        }

        // ── B3. 静默夜（FGT-SIG-006 ③，预留接口）─────────────────────────────────

        private static void CheckSilentNight()
        {
            Line("  · B3. FGT-SIG-006 ③静默夜（只有接口）：预警入口；判定入口一打开立即弹回；“静默夜开始”事件入口；过渡中开始 → 取消；静默夜里不能接入；结束 2 秒后退出安全模式");
            try
            {
                CampaignState s = NewHome(8404);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                int b = SpawnHome(BpCannonUp, new Vector2(8f, -4f));
                int c = SpawnHome(BpGunPlain, new Vector2(12f, -4f));
                WorldSimulation.StepMany(2);
                EquipOverload(s);
                CommitVia(a);
                int warnCues = FeedbackCues.CountOf(FeedbackCueId.SignalLinkWarning);
                NightWarnings.Clear();
                SignalLinkService.AnnounceSilentNight(29.2f);
                Expect(SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.warn.silent_night", "30") && NightWarnings.Count == 1
                       && FeedbackCues.CountOf(FeedbackCueId.SignalLinkWarning) == warnCues + 1 && SignalPresence.CurrentMachineLogicId == a,
                    $"静默夜预警入口（FG07 调）：“{SignalUplinkService.LastFeedbackText}”，预警音，预警事件；信号还在机器里");

                // 判定入口：下一帧区域接管系统就断链（不给宽限）。
                SignalUplinkService.SilentNightProvider = () => true;
                Frames(1);
                Expect(SignalPresence.AtCore && SignalLinkService.LastBreakReason == SignalLinkBreakReason.SilentNight
                       && SignalLinkService.IsInSafeMode(s, a) && SignalLinkService.SafeModeReason(s, a) == SignalLinkBreakReason.SilentNight
                       && SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.break.silent_night", SignalPresence.MachineLabel(a)),
                    $"静默夜判定入口为真：同一帧弹回归还核心，机器进入安全模式（“{SignalUplinkService.LastFeedbackText}”）");
                UplinkRequestResult denied = SignalUplinkService.Request(b, UplinkSource.MachineList);
                Expect(!denied.Accepted && denied.Failure == UplinkFailure.SilentNight, $"静默夜里接入被拒：“{denied.Text}”");
                WorldSimulation.StepMany(60 * 5);
                Expect(SignalLinkService.IsInSafeMode(s, a), "静默夜持续 5 游戏秒：安全模式一直保持");
                SignalUplinkService.SilentNightProvider = null;
                int guard = 0;
                while (SignalLinkService.IsInSafeMode(s, a) && guard++ < 60 * 6)
                {
                    WorldSimulation.StepMany(1);
                }
                Expect(!SignalLinkService.IsInSafeMode(s, a), "静默夜结束 2 游戏秒后退出安全模式");

                // “静默夜开始”事件入口：立即断链（不必等下一帧）；过渡中开始 → 取消，信号留在原处。
                CommitVia(b);
                bool broke = SignalLinkService.OnSilentNightStarted();
                Expect(broke && SignalPresence.AtCore && SignalLinkService.IsInSafeMode(s, b) && SignalLinkService.LastBreakReason == SignalLinkBreakReason.SilentNight,
                    "“静默夜开始”事件入口（FG07 调）：立即弹回，机器进入安全模式");
                Expect(!SignalLinkService.OnSilentNightStarted(), "信号已经在归还核心时再调一次：什么也不做（不重复断链）");
                SignalUplinkService.Request(c, UplinkSource.MachineList);
                bool pend = SignalUplinkService.IsPending;
                SignalLinkService.OnSilentNightStarted();
                Expect(pend && !SignalUplinkService.IsPending && SignalPresence.AtCore && SignalUplinkService.LastCancel == UplinkCancelReason.TargetUnavailable
                       && SignalUplinkService.LastFeedbackText.Contains(GameText.Get("signal.uplink.reason.silent_night")),
                    $"接入过渡中静默夜开始：取消接入，信号留在原处（“{SignalUplinkService.LastFeedbackText}”）");
            }
            finally
            {
                SignalUplinkService.SilentNightProvider = null;
            }
        }

        // ── B4 / C2. 阵亡与连续死亡回弹（FGT-SIG-006 ④）──────────────────────────

        private static void CheckDeathRebound()
        {
            Line("  · B4 / C2. FGT-SIG-006 ④阵亡：沿用死亡回弹（最近的能接入的机器）；连续阵亡逐台回弹；最后一台阵亡回到归还核心");
            CampaignState s = NewHome(8405);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpCannonUp, new Vector2(7f, -4f));
            int c = SpawnHome(BpGunPlain, new Vector2(16f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            CommitVia(a);
            // 开局家园自带的机器挪到覆盖外（不是合适的回弹目标），这里只看测试的三台。
            int exiled = ExileOthers(site, a, b, c);
            int lost0 = FeedbackCues.CountOf(FeedbackCueId.SignalLost);
            int breaks0 = Breaks.Count;
            MachineRegistry.ApplyDamage(a, 999999f);
            Frames(2);
            site.TryGetMachineWeapon(b, out MachineWeaponInfo bInfo);
            Expect(SignalPresence.CurrentMachineLogicId == b && WorldSimulation.Home.PossessedMachineLogicId == b && bInfo.Uplinked
                   && SignalLinkService.LastBreakReason == SignalLinkBreakReason.MachineDestroyed && SignalLinkService.LastReboundLogicId == b
                   && SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.break.destroyed_rebound", SignalPresence.MachineLabel(a), SignalPresence.MachineLabel(b))
                   && FeedbackCues.CountOf(FeedbackCueId.SignalLost) == lost0 + 1 && !SignalLinkService.IsInSafeMode(s, a) && !SignalLinkService.IsInSafeMode(s, b),
                $"{SignalPresence.MachineLabel(a)} 阵亡：信号弹到最近的 {SignalPresence.MachineLabel(b)}（插入信号核固件），给原因与音效（“{SignalUplinkService.LastFeedbackText}”）；没有机器进入安全模式");
            MachineRegistry.ApplyDamage(b, 999999f);
            Frames(2);
            bool toC = SignalPresence.CurrentMachineLogicId == c && SignalLinkService.LastReboundLogicId == c;
            MachineRegistry.ApplyDamage(c, 999999f);
            Frames(12);
            Expect(toC && SignalPresence.AtCore && WorldSimulation.Home.PossessedMachineLogicId == null && WorldView.Director.Mode == ViewMode.Strategy
                   && SignalUplinkService.LastFeedbackText == GameText.Format("signal.link.break.destroyed_core", SignalPresence.MachineLabel(c))
                   && Breaks.Count == breaks0 + 3 && Breaks.Skip(breaks0).Select(x => x.ReboundLogicId).SequenceEqual(new[] { b, c, 0 })
                   && FeedbackCues.CountOf(FeedbackCueId.SignalLost) == lost0 + 3 && SignalLinkService.SafeModeCount(s) == 0,
                $"连续阵亡（开局自带的 {exiled} 台已挪到覆盖外）：{SignalPresence.MachineLabel(b)} → {SignalPresence.MachineLabel(c)} → 没有能接入的机器了，回到归还核心、镜头回战略（“{SignalUplinkService.LastFeedbackText}”），每次断链各一次原因与音效");
        }

        // ── C3. 没有合适的回弹目标 ───────────────────────────────────────────────

        private static void CheckNoSuitableTarget()
        {
            Line("  · C3. 没有合适的回弹目标时回到归还核心：唯一的另一台在覆盖外（家园）/ 在干扰场里（破碎都市）");
            CampaignState s = NewHome(8406);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            Place(site, b, core + new Vector2(170f, 0f));
            int exiled = ExileOthers(site, a, b);
            WorldSimulation.StepMany(1);
            CommitVia(a);
            MachineRegistry.ApplyDamage(a, 999999f);
            Frames(2);
            Expect(SignalPresence.AtCore && SignalLinkService.LastReboundLogicId == 0 && SignalLinkService.LastBreakReason == SignalLinkBreakReason.MachineDestroyed
                   && !SignalLinkService.IsInSafeMode(s, b),
                $"另一台 {SignalPresence.MachineLabel(b)}（以及开局自带的 {exiled} 台）都在覆盖外：不回弹给它们，信号回到归还核心");

            CampaignState s2 = NewState(8407);
            EquipOverload(s2);
            int r1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -24f), 20000f);
            int r2 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-4f, -24f), 20000f);
            FracturedCityController city = OpenCity(s2, r1, r2);
            WorldView.Observe(city.SiteId);
            Place(city.Combat, r2, FracturedCityLayout.ListeningNode.Position + new Vector2(FracturedCityLayout.JammerRadius - 2f, 0f));
            WorldSimulation.StepMany(1);
            CommitVia(r1);
            MachineRegistry.ApplyDamage(r1, 999999f);
            Frames(2);
            Expect(SignalPresence.AtCore && city.PossessedMachineLogicId == null && SignalLinkService.LastReboundLogicId == 0,
                $"唯一的另一台 {SignalPresence.MachineLabel(r2)} 在干扰场里（虽然更近也不回弹）：信号回到归还核心");
        }

        // ── C4. 预警行回归（第 1 轮修复）──────────────────────────────────────────

        private static void CheckWarningLineRegressions()
        {
            Line("  · C4. 预警行：宽限逐秒倒计时；别的机器退出安全模式不盖住接入中那台的预警；离开后原地重新接入同一台边缘预警行照常出现；回弹与发起接入同一套目标条件");
            CampaignState s = NewHome(8412);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            HomeValleyController home = WorldSimulation.Home;
            CombatSite site = home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            CommitVia(a);
            AdvanceRealTime(5f);

            // ① 宽限倒计时：HUD 状态行是逐秒变化的 WarningLine，不是宽限开始那一刻的固定文本。
            Place(site, a, core + new Vector2(160f, 0f));
            Frames(2);
            string g2 = SignalUplinkService.StatusLine(s);
            Frames(20);
            string g1 = SignalUplinkService.StatusLine(s);
            string label = SignalPresence.MachineLabel(a);
            Expect(SignalLinkService.InGrace && g2 == GameText.Format("signal.link.warn.out_of_coverage", label, "2")
                   && g1 == GameText.Format("signal.link.warn.out_of_coverage", label, "1") && g1 == SignalLinkService.WarningLine,
                $"宽限中 HUD 倒计时：“{g2}”→ 1 游戏秒后“{g1}”");
            Place(site, a, core + new Vector2(139.5f, 0f));
            Frames(2);

            // ② 别的机器退出安全模式（3 秒普通反馈）不盖住接入中那台的边缘预警。
            s.SignalCore.SafeModes = (s.SignalCore.SafeModes ?? Array.Empty<SignalSafeModeRecord>()).Append(new SignalSafeModeRecord
            {
                LogicId = b,
                Reason = (int)SignalLinkBreakReason.OutOfCoverage,
                SinceGameSeconds = GameClock.GameSeconds,
                ClearSinceGameSeconds = -1,
            }).ToArray();
            int guard = 0;
            while (SignalLinkService.IsInSafeMode(s, b) && guard++ < 20 * 6)
            {
                Place(site, a, core + new Vector2(139.5f, 0f));
                Frames(1);
            }
            string afterExit = SignalUplinkService.StatusLine(s);
            Expect(!SignalLinkService.IsInSafeMode(s, b) && SignalUplinkService.LastFeedbackText == GameText.Format("signal.safe_mode.exit", SignalPresence.MachineLabel(b))
                   && SignalLinkService.EdgeWarningActive && afterExit == SignalLinkService.WarningLine && afterExit.Contains(label) && afterExit.Contains("10"),
                $"{SignalPresence.MachineLabel(b)} 退出安全模式（反馈“{SignalUplinkService.LastFeedbackText}”走字幕），HUD 状态行仍是 {label} 的边缘预警“{afterExit}”");

            // ③ 边缘预警中离开，再原地接入同一台：预警行照常出现（旧的变化键已随 ClearWatch 清掉）。
            home.Control.ReleaseToStrategy();
            Frames(12);
            bool left = SignalPresence.AtCore && SignalLinkService.WarningLine.Length == 0;
            AdvanceRealTime(5f);
            CommitVia(a);
            Place(site, a, core + new Vector2(139.5f, 0f));
            Frames(2);
            string again = SignalUplinkService.StatusLine(s);
            Expect(left && SignalPresence.CurrentMachineLogicId == a && SignalLinkService.EdgeWarningActive
                   && again == SignalLinkService.WarningLine && again.Contains(label) && again.Contains("10"),
                $"边缘预警中离开（预警行清空），原地重新接入同一台：HUD 预警行照常出现“{again}”");

            // ④ 回弹与发起接入同一套目标条件（只少“地点正被观察”）：同一台、同一处境下两边给出同样的结论。
            Place(site, a, core + new Vector2(20f, 0f));
            home.Control.ReleaseToStrategy();
            Frames(12);
            Vector2 bPos = MachinePos(site, b);
            Func<bool> night0 = SignalUplinkService.SilentNightProvider;
            Func<int, bool> bay0 = SignalUplinkService.OnRepairBayProvider;
            Func<int, bool> delivery0 = SignalUplinkService.InDeliveryProvider;
            var pairs = new List<string>();
            bool same = true;
            void Compare(string what, UplinkFailure expected)
            {
                UplinkFailure v = SignalUplinkService.Validate(s, b, out _);
                UplinkFailure r = SignalUplinkService.ReboundTargetFailure(home.Control, b, MachinePos(site, b));
                same &= v == expected && r == expected;
                pairs.Add($"{what}：{v}/{r}");
            }
            try
            {
                Compare("正常", UplinkFailure.None);
                SignalUplinkService.OnRepairBayProvider = id => id == b;
                Compare("维修台", UplinkFailure.OnRepairBay);
                SignalUplinkService.OnRepairBayProvider = bay0;
                SignalUplinkService.InDeliveryProvider = id => id == b;
                Compare("投送途中", UplinkFailure.InDelivery);
                SignalUplinkService.InDeliveryProvider = delivery0;
                SignalUplinkService.SilentNightProvider = () => true;
                Compare("静默夜", UplinkFailure.SilentNight);
                SignalUplinkService.SilentNightProvider = night0;
                Place(site, b, core + new Vector2(170f, 0f));
                WorldSimulation.StepMany(1);
                Compare("覆盖外", UplinkFailure.OutOfCoverage);
            }
            finally
            {
                SignalUplinkService.SilentNightProvider = night0;
                SignalUplinkService.OnRepairBayProvider = bay0;
                SignalUplinkService.InDeliveryProvider = delivery0;
                Place(site, b, bPos);
            }
            Expect(same, "回弹目标条件与发起接入逐条一致（发起 / 回弹）：" + string.Join("；", pairs));
        }

        // ── C1. 边缘反复进出 ─────────────────────────────────────────────────────

        private static void CheckEdgeFlapping()
        {
            Line("  · C1. 边缘反复进出：接入中每 0.5 秒进出一次覆盖边缘不断链、预警音防刷、地图预警圈不闪；安全模式里反复进出不退出，稳定回来 2 秒才退出一次");
            CampaignState s = NewHome(8408);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            CommitVia(a);
            AdvanceRealTime(5f);
            int breaks0 = Breaks.Count;
            int lost0 = FeedbackCues.CountOf(FeedbackCueId.SignalLost);
            int restored0 = FeedbackCues.CountOf(FeedbackCueId.SignalRestored);
            Place(site, a, core + new Vector2(141f, 0f));
            Frames(2);
            bool ringAlways = true;
            const int cycles = 12;
            for (int i = 0; i < cycles; i++)
            {
                Place(site, a, core + new Vector2(152f, 0f));
                for (int k = 0; k < 10; k++)
                {
                    Frames(1);
                    ringAlways &= SignalLinkView.RingVisible;
                }
                Place(site, a, core + new Vector2(141f, 0f));
                for (int k = 0; k < 10; k++)
                {
                    Frames(1);
                    ringAlways &= SignalLinkView.RingVisible;
                }
            }
            int lostCues = FeedbackCues.CountOf(FeedbackCueId.SignalLost) - lost0;
            int restoredCues = FeedbackCues.CountOf(FeedbackCueId.SignalRestored) - restored0;
            Expect(SignalPresence.CurrentMachineLogicId == a && Breaks.Count == breaks0 && lostCues >= 1 && lostCues <= 5 && restoredCues <= 5,
                $"接入中 {cycles} 次进出覆盖边缘（每次出去 0.5 秒 < 宽限 2 秒）：不断链；预警音 {lostCues} 次、恢复音 {restoredCues} 次（同一台 3 秒内只响一次，不是 {cycles} 次）");
            Expect(ringAlways, "反复进出期间地图预警圈一直显示（边缘内是黄色预警、出去是红色宽限；回差 3 格，不闪）");

            // 安全模式里反复进出：条件每次只消失 1 秒（< 2 秒），不退出。
            Place(site, a, core + new Vector2(160f, 0f));
            Frames(50);
            bool safe = SignalLinkService.IsInSafeMode(s, a);
            int exits0 = SignalLinkService.SafeModeExitCount;
            int changes0 = SafeChanges.Count;
            for (int i = 0; i < 6; i++)
            {
                Place(site, a, core + new Vector2(140f, 0f));
                WorldSimulation.StepMany(60);
                Place(site, a, core + new Vector2(158f, 0f));
                WorldSimulation.StepMany(60);
            }
            bool stayed = SignalLinkService.IsInSafeMode(s, a) && SignalLinkService.SafeModeExitCount == exits0 && SafeChanges.Count == changes0;
            Place(site, a, core + new Vector2(120f, 0f));
            WorldSimulation.StepMany(60 * 3);
            Expect(safe && stayed && !SignalLinkService.IsInSafeMode(s, a) && SignalLinkService.SafeModeExitCount == exits0 + 1 && SafeChanges.Count == changes0 + 1,
                "安全模式里 6 次进出覆盖边缘（每次回来只待 1 秒）：图标不闪、不退出；稳定回来后退出恰好一次");
        }

        // ── D. 暂停与倍速 ────────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · D. 暂停与 0.5x～3x：宽限按游戏秒（暂停不走）；安全模式退出在各档倍速下同一游戏时长");
            CampaignState s = NewHome(8409);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            CommitVia(a);
            Place(site, a, core + new Vector2(160f, 0f));
            Frames(10);
            GameClock.SetPaused(true);
            Frames(80);
            bool heldPaused = SignalPresence.CurrentMachineLogicId == a && SignalLinkService.InGrace;
            GameClock.SetPaused(false);
            int n = 0;
            while (SignalPresence.CurrentMachineLogicId == a && n++ < 80)
            {
                Frames(1);
            }
            Expect(heldPaused && SignalPresence.AtCore && n >= 25 && n <= 32,
                $"宽限走了 0.5 秒后暂停 4 真实秒：信号还在、宽限没走；恢复后再过 {n} 帧（≈1.5 秒）才断链");

            var times = new List<double>();
            var pauses = new List<bool>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState t = NewHome(8410 + (int)(speed * 10));
                int m = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                WorldSimulation.StepMany(2);
                EquipOverload(t);
                CombatSite st = WorldSimulation.Home.Combat;
                Vector2 c = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
                CommitVia(m);
                Place(st, m, c + new Vector2(160f, 0f));
                int g = 0;
                while (SignalPresence.CurrentMachineLogicId == m && g++ < 120)
                {
                    Frames(1);
                }
                Frames(12);
                GameClock.SetSpeed(speed);
                Place(st, m, c + new Vector2(20f, 0f));
                double back = GameClock.GameSeconds;
                GameClock.SetPaused(true);
                Frames(40);
                pauses.Add(SignalLinkService.IsInSafeMode(t, m) && Math.Abs(GameClock.GameSeconds - back) < 1e-9);
                GameClock.SetPaused(false);
                g = 0;
                while (SignalLinkService.IsInSafeMode(t, m) && g++ < 400)
                {
                    Frames(1);
                }
                times.Add(GameClock.GameSeconds - back);
                GameClock.SetSpeed(1f);
            }
            Expect(pauses.All(x => x), "安全模式退出计时：暂停 2 真实秒一点没走（各档倍速）");
            Expect(times.All(x => x >= 2.0 - 1e-6 && x <= 2.0 + SignalLinkService.SafeModeCheckSeconds + 3 * FrameDt + 1e-6),
                $"0.5x / 1x / 2x / 3x 下回到覆盖后退出安全模式的游戏时长：{string.Join("，", times.Select(x => x.ToString("F2")))} 秒（都在 2 秒 + 至多一个 0.5 秒检查间隔 + 一帧以内，按游戏时间）");
        }

        // ── E. 家园后台与观察一致（FGR-BASE-021）────────────────────────────────

        private static void CheckBackgroundConsistency()
        {
            Line("  · E. 家园机器的安全模式退出判定在世界模拟步里：看着家园与看着远征地点时，退出发生在同一步");
            long[] exitTicks = new long[2];
            for (int run = 0; run < 2; run++)
            {
                CampaignState s = NewHome(8420);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                int r = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -24f), 20000f);
                WorldSimulation.StepMany(2);
                EquipOverload(s);
                CombatSite site = WorldSimulation.Home.Combat;
                Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
                CommitVia(a);
                Place(site, a, core + new Vector2(160f, 0f));
                int g = 0;
                while (SignalPresence.CurrentMachineLogicId == a && g++ < 120)
                {
                    Frames(1);
                }
                FracturedCityController city = OpenCity(s, r);
                if (run == 1)
                {
                    WorldView.Observe(city.SiteId);
                }
                else
                {
                    WorldView.Observe(WorldSimulation.Home.SiteId);
                }
                // 两次运行从同一个整步边界开始，只差“看着哪里”。
                while (GameClock.Ticks % 60 != 0)
                {
                    WorldSimulation.StepMany(1);
                }
                Place(site, a, core + new Vector2(20f, 0f));
                long t0 = GameClock.Ticks;
                g = 0;
                while (SignalLinkService.IsInSafeMode(s, a) && g++ < 60 * 6)
                {
                    WorldSimulation.StepMany(1);
                }
                exitTicks[run] = GameClock.Ticks - t0;
            }
            Expect(exitTicks[0] == exitTicks[1] && exitTicks[0] > 0,
                $"看着家园：回到覆盖后第 {exitTicks[0]} 步退出；看着破碎都市：第 {exitTicks[1]} 步退出（逐步一致）");
        }

        // ── F. 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · F. 存读档：安全模式（原因、进入时刻、条件消失计时）进存档；读档后头顶图标恢复，退出计时接着走；旧档缺字段 = 没有安全模式");
            CampaignState s = NewHome(8430);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            CommitVia(a);
            Place(site, a, core + new Vector2(160f, 0f));
            int g = 0;
            while (SignalPresence.CurrentMachineLogicId == a && g++ < 120)
            {
                Frames(1);
            }
            SignalSafeModeRecord before = s.SignalCore.SafeModes.Single();
            string beforeJson = JsonUtility.ToJson(before);
            SaveNow();
            CampaignState l = LoadLikeMenu();
            Frames(1);
            SignalSafeModeRecord after = l?.SignalCore?.SafeModes?.SingleOrDefault();
            Expect(after != null && JsonUtility.ToJson(after) == beforeJson && SignalLinkService.IsInSafeMode(l, a) && SignalPresence.AtCore,
                $"读档后安全模式逐字段一致（{beforeJson}），信号在归还核心");
            Expect(SignalLinkView.BadgeWanted(a), "读档重建机器表现后头顶安全模式图标恢复");

            // 条件消失计时进行到一半时存档：读回来接着计，不从头算。
            CombatSite ls = WorldSimulation.Home.Combat;
            Place(ls, a, core + new Vector2(20f, 0f));
            double back = GameClock.GameSeconds;
            WorldSimulation.StepMany(60);
            double clearSince = l.SignalCore.SafeModes.Single().ClearSinceGameSeconds;
            SaveNow();
            CampaignState l2 = LoadLikeMenu();
            double clearSince2 = l2.SignalCore.SafeModes.Single().ClearSinceGameSeconds;
            g = 0;
            while (SignalLinkService.IsInSafeMode(l2, a) && g++ < 60 * 6)
            {
                WorldSimulation.StepMany(1);
            }
            double took = GameClock.GameSeconds - back;
            Expect(clearSince >= 0 && Math.Abs(clearSince - clearSince2) < 1e-9 && took >= 2.0 - 1e-6 && took <= 2.0 + SignalLinkService.SafeModeCheckSeconds + 1e-6,
                $"回到覆盖 1 秒时存档，读档后接着计时：共 {took:F2} 游戏秒退出（条件消失时刻 {clearSince:F2} 原样读回）");

            SignalCoreState old = JsonUtility.FromJson<SignalCoreState>("{\"DomainVersion\":1,\"SlotPartIds\":[\"\",\"\"],\"UplinkMachineLogicId\":0}");
            Expect(old.SafeModes != null && old.SafeModes.Length == 0, "旧档（没有安全模式字段）读回来 = 没有机器在安全模式里（只加字段、不升域版本）");
        }

        // ── G. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · G. 性能：接入中每帧链路判定 O(覆盖源数)、覆盖源不是每帧重建；安全模式退出判定 O(安全模式机器数) 且只在检查的那一步");
            CampaignState s = NewHome(8440);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            for (int i = 0; i < 40; i++)
            {
                SpawnHome(BpGunPlain, new Vector2(6f + (i % 8) * 2f, -8f - (i / 8) * 2f));
            }
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CommitVia(a);
            RegionControlSystem control = WorldSimulation.Home.Control;
            control.Tick(0f);
            int rebuild0 = SignalCoverageService.RebuildCount;
            const int calls = 5000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < calls; i++)
            {
                control.Tick(0f);
            }
            sw.Stop();
            double perTickUs = sw.Elapsed.TotalMilliseconds * 1000.0 / calls;
            PerfLines.Add($"接入中区域接管系统每帧 Tick（含覆盖采样与预警）平均 {perTickUs:F2} µs（家园 41 台机器，{calls} 次，同一模拟步内覆盖源重建 {SignalCoverageService.RebuildCount - rebuild0} 次）");
            Expect(perTickUs < 50.0 && SignalCoverageService.RebuildCount - rebuild0 == 0,
                $"每帧链路判定 {perTickUs:F2} µs（上限 50 µs），同一步内覆盖源不重建（与机器总数无关：只采样被接入的那一台）");

            // 安全模式退出判定：注入 200 台安全模式机器（记录在未载入的远征地点，走记录位置），量检查那一步。
            var records = new List<SignalSafeModeRecord>();
            for (int i = 0; i < 200; i++)
            {
                int id = SpawnRegion(FoundryOutpostLayout.RegionId, BpGunPlain, new Vector2(i, -20f));
                records.Add(new SignalSafeModeRecord { LogicId = id, Reason = (int)SignalLinkBreakReason.Jammed, SinceGameSeconds = 0, ClearSinceGameSeconds = -1 });
            }
            var perCheck = new List<double>();
            int checks0 = SignalLinkService.SafeModeChecks;
            long every = (long)Math.Round(SignalLinkService.SafeModeCheckSeconds * GameClock.StepHz);
            for (int rep = 0; rep < 20; rep++)
            {
                s.SignalCore.SafeModes = records.Select(r => new SignalSafeModeRecord { LogicId = r.LogicId, Reason = r.Reason, ClearSinceGameSeconds = -1 }).ToArray();
                while (GameClock.Ticks % every != 0)
                {
                    WorldSimulation.StepMany(1);
                }
                var one = Stopwatch.StartNew();
                SignalLinkService.SimStep(s);
                one.Stop();
                perCheck.Add(one.Elapsed.TotalMilliseconds);
                WorldSimulation.StepMany(1);
            }
            var nonCheck = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                if (GameClock.Ticks % every != 0)
                {
                    SignalLinkService.SimStep(s);
                }
            }
            nonCheck.Stop();
            perCheck.Sort();
            double median = perCheck[perCheck.Count / 2];
            PerfLines.Add($"安全模式退出判定：200 台安全模式机器，检查那一步中位 {median:F3} ms、最大 {perCheck.Last():F3} ms；非检查步 1000 次共 {nonCheck.Elapsed.TotalMilliseconds:F3} ms");
            Expect(perCheck.Count == 20 && SignalLinkService.SafeModeChecks - checks0 >= 20 && median < 1.0,
                $"200 台安全模式机器的检查步中位 {median:F3} ms（上限 1 ms，每 0.5 游戏秒一次）");
            s.SignalCore.SafeModes = Array.Empty<SignalSafeModeRecord>();

            // 后期规模：建筑数 ×10（填充的建筑不是覆盖源），覆盖源的定期重建不按建筑总数扫（只重读核心与信号塔的下标）。
            BuildingRecord[] original = s.BuildingRecords;
            int baseCount = original.Length;
            FieldInfo builtAt = typeof(SignalCoverageService).GetField("_builtAtTick", BindingFlags.NonPublic | BindingFlags.Static);
            double RebuildUs(int reps)
            {
                var w = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++)
                {
                    builtAt?.SetValue(null, long.MinValue); // 模拟“换了一个时间桶”：只重建覆盖源，不重新索引。
                    SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero);
                }
                w.Stop();
                return w.Elapsed.TotalMilliseconds * 1000.0 / reps;
            }
            SignalCoverageService.Invalidate();
            SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero);
            double base1 = RebuildUs(2000);
            // 至少 ×10，且总数不少于 2000 座（后期规模）。
            var fillers = new BuildingRecord[Math.Max(baseCount * 9, 2000 - baseCount)];
            for (int i = 0; i < fillers.Length; i++)
            {
                fillers[i] = new BuildingRecord
                {
                    BuildingId = "selfcheck:filler:" + i,
                    BuildingTypeId = "selfcheck_filler",
                    RegionId = HomeValleyLayout.RegionId,
                    Position = new Vector2(1000f + i, 1000f),
                };
            }
            try
            {
                s.BuildingRecords = original.Concat(fillers).ToArray();
                int index0 = SignalCoverageService.IndexCount;
                var reindex = Stopwatch.StartNew();
                SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero);
                reindex.Stop();
                int index1 = SignalCoverageService.IndexCount;
                double big = RebuildUs(2000);
                int index2 = SignalCoverageService.IndexCount;
                PerfLines.Add($"覆盖源定期重建：建筑 {baseCount} 座时每次 {base1:F2} µs，扩到 {s.BuildingRecords.Length} 座时每次 {big:F2} µs；建筑表换了才全量索引一次（{reindex.Elapsed.TotalMilliseconds * 1000.0:F1} µs）");
                Expect(index1 == index0 + 1 && index2 == index1 && big < 20.0,
                    $"建筑 {baseCount} → {s.BuildingRecords.Length} 座：定期重建 2000 次不再扫建筑表（索引次数 {index1 - index0} → 之后 +{index2 - index1}），每次 {big:F2} µs（上限 20 µs，与建筑总数无关）");
            }
            finally
            {
                s.BuildingRecords = original;
                SignalCoverageService.Invalidate();
            }
        }

        // ── H. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · H. 界面：HUD 状态行的预警与原因（中英）；命令栏机器列表的安全模式标记与说明（中英）；样式表通过布局探针");
            CampaignState s = NewHome(8450);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CombatSite site = WorldSimulation.Home.Combat;
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            VisualElement hudRoot = MountUxml(HudUxmlPath, out GameObject hudGo);
            SignalCoreHudUIToolkit hud = hudGo.AddComponent<SignalCoreHudUIToolkit>();
            VisualElement barRoot = MountUxml(CommandBarUxmlPath, out GameObject barGo);
            var bar = barGo.AddComponent<GameLogic.UI.RegionCommand.RegionCommandBarUIToolkit>();
            try
            {
                hud.BindView(hudRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                bar.BindView(barRoot);
                CommitVia(a);
                AdvanceRealTime(5f);
                Place(site, a, core + new Vector2(141f, 0f));
                Frames(2);
                hud.Refresh();
                string zh = hud.UplinkStatusText;
                GameSettings.SetLanguage(GameLanguage.En);
                Frames(1);
                hud.Refresh();
                string en = hud.UplinkStatusText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(zh.Contains(SignalPresence.MachineLabel(a)) && zh.Contains("覆盖边缘") && en.Contains("cells") && !Regex.IsMatch(en, "[\\u4e00-\\u9fff]") && !GameText.ContainsMarker(en),
                    $"HUD 状态行在接近覆盖边缘时显示预警：“{zh}”；英文“{en}”");

                Place(site, a, core + new Vector2(160f, 0f));
                int g = 0;
                while (SignalPresence.CurrentMachineLogicId == a && g++ < 120)
                {
                    Frames(1);
                }
                Frames(2);
                hud.Refresh();
                bar.Refresh();
                string reason = hud.UplinkStatusText;
                string tip = HoverTip(bar.CandidateTooltipTarget(a));
                string tipB = HoverTip(bar.CandidateTooltipTarget(b));
                Expect(reason == GameText.Format("signal.link.break.out_of_coverage", SignalPresence.MachineLabel(a)),
                    $"断链后 HUD 状态行写明原因（即使信号已回到核心）：“{reason}”");
                Expect(bar.CandidateShowsSafeMode(a) && !bar.CandidateShowsSafeMode(b) && tip.StartsWith(SignalPresence.MachineLabel(a) + "\n" + SignalLinkService.SafeModeTooltip(s, a))
                       && tip.Contains(GameText.Get("signal.link.reason_name.out_of_coverage")) && tip.Contains(InputDisplay.ForAction(GameActionId.ToggleCameraView))
                       && tipB.Length > 0 && !tipB.Contains(GameText.Get("signal.link.reason_name.out_of_coverage")),
                    $"命令栏机器列表：{SignalPresence.MachineLabel(a)} 带安全模式标记（盾形图标 + “{GameText.Get("signal.safe_mode.list_tag")}”），悬停提示“{tip.Replace("\n", " / ")}”；{SignalPresence.MachineLabel(b)} 没有标记，悬停只有接入说明");
                // 标记在编号按钮外侧：真实排版后编号按钮与盾形图标 + “安全”字样不重叠（按钮文字不参与 flex，塞进按钮里会压在一起）。
                UiToolkitLayoutProbe.ForceLayout(barRoot);
                VisualElement itemA = bar.CandidateTooltipTarget(a);
                Button numA = itemA?.Q<Button>();
                VisualElement tagA = itemA?.Q<VisualElement>(className: "cmd-candidate-safe-tag");
                Rect nb = numA?.worldBound ?? default;
                Rect tb = tagA?.worldBound ?? default;
                Expect(numA != null && tagA != null && tagA.resolvedStyle.display == DisplayStyle.Flex && tb.width > 1f && nb.width > 1f
                       && tb.xMin >= nb.xMax - 0.5f && numA.text.StartsWith("#"),
                    $"安全模式那一行排版：编号按钮“{numA?.text}”[{nb.xMin:F0}～{nb.xMax:F0}] 与标记 [{tb.xMin:F0}～{tb.xMax:F0}] 左右排开不重叠");
                GameSettings.SetLanguage(GameLanguage.En);
                bar.Refresh();
                string tipEn = HoverTip(bar.CandidateTooltipTarget(a));
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                bar.Refresh();
                Expect(tipEn.Contains("Safe mode") && !Regex.IsMatch(tipEn, "[\\u4e00-\\u9fff]") && !GameText.ContainsMarker(tipEn),
                    $"机器列表悬停提示随语言：“{tipEn.Replace("\n", " / ")}”");
                Place(site, a, core + new Vector2(20f, 0f));
                WorldSimulation.StepMany(60 * 3);
                bar.Refresh();
                string tipAfter = HoverTip(bar.CandidateTooltipTarget(a));
                Expect(!bar.CandidateShowsSafeMode(a) && tipAfter.Length > 0 && !tipAfter.Contains(GameText.Get("signal.link.reason_name.out_of_coverage")),
                    "退出安全模式后列表标记与悬停说明里的安全模式段随之撤掉（接入说明仍在）");

                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    string result = UiToolkitLayoutProbe.Probe(CommandBarUxmlPath, "RegionCommandBarRoot", stressFill: true, uiScale: 1f);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 RegionCommandBar.uxml#RegionCommandBarRoot [{lang}]（含新增的安全模式样式）：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(hudGo);
                Object.DestroyImmediate(barGo);
            }
        }

        // ── 世界与机器（与 FgSignalUplinkSelfCheck 同一套准备方式）──────────────────

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig04-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            foreach (string type in new[] { HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeAssemblyStation })
            {
                BuildingRecord r = s.BuildingRecords.FirstOrDefault(x => x.BuildingTypeId == type);
                if (r != null)
                {
                    r.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            AddBlueprints(s);
            SignalCoverageService.Invalidate();
            return s;
        }

        private static CampaignState NewState(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig04-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            AddBlueprints(s);
            return s;
        }

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            InputRouter.DebugSetReader(Keys);
            Keys.Down = KeyCode.None;
            UiEscapeStack.Clear();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            SignalLinkService.ResetForTests();
            SignalCoverageService.ResetForTests();
            SignalLinkView.Clear();
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            CircuitOpResult up = cannon.TrySetUplink(2);
            if (!up.Success)
            {
                Fail("测试准备：重炮蓝图 2 号格标接入口失败：" + up.Message);
            }
            AddBlueprint(s, BpCannonUp, cannon);
            AddBlueprint(s, BpGunPlain, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static int SpawnHome(string bp, Vector2 at)
        {
            int id = SpawnRegion(HomeValleyLayout.RegionId, bp, HomeSpot(at));
            CircuitOpResult r = MachineLoadoutRegistry.Register(CampaignSession.Current, id, bp, 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记装配失败：{r.Message}");
            }
            return id;
        }

        private static Vector2 HomeSpot(Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 away = (core - dummy).sqrMagnitude > 1e-4f ? (core - dummy).normalized : Vector2.right;
            Vector2 p = core + offset;
            int guard = 0;
            while (Vector2.Distance(p, dummy) < HomeValleyCombatTargets.EngageRange + 4f && guard++ < 20)
            {
                p += away * 3f;
            }
            return p;
        }

        /// <summary>把家园里除 <paramref name="keep"/> 以外的存活机器（开局自带的）挪到覆盖外 400 格，返回挪了几台。</summary>
        private static int ExileOthers(CombatSite site, params int[] keep)
        {
            Vector2 core = SignalCoverageService.Sample(HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            int n = 0;
            foreach (MachineRecord r in MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId && !keep.Contains(x.LogicId)).ToList())
            {
                Place(site, r.LogicId, core + new Vector2(400f + 3f * n, 60f));
                n++;
            }
            return n;
        }

        private static int SpawnRegion(string region, string bp, Vector2 at, float hp = 400f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static FracturedCityController OpenCity(CampaignState s, params int[] ids)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(ids, resume: false);
        }

        private static void EquipOverload(CampaignState s)
        {
            if (SignalCoreService.SlotContentId(s, 0) == FirmwareCatalog.FwOverloadId)
            {
                return;
            }
            string chip = s.PrimitiveChips?.FirstOrDefault(c => c != null && c.CardDefId == FirmwareCatalog.FwOverloadId && c.State == PrimitiveChipState.Bag)?.PartId;
            if (chip == null)
            {
                SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, FirmwareCatalog.FwOverloadId);
                if (!pr.Success)
                {
                    Fail($"测试准备：刻印过载失败（{pr.Message}）");
                    return;
                }
                chip = pr.CreatedId;
            }
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, chip, 0);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!r.Success)
            {
                Fail("测试准备：过载装入 1 号槽失败：" + r.Message);
            }
        }

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 40)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        private static void SaveNow()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("存档写入失败：" + r.Message);
            }
        }

        private static CampaignState LoadLikeMenu()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            SignalLinkService.ResetForTests();
            SignalCoverageService.ResetForTests();
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(Slot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            SignalUplinkService.RestoreAfterLoad();
            return r.State;
        }

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt);
            }
        }

        private static void AdvanceRealTime(float seconds) => _fakeNow += seconds;

        private static CombatWeapon WeaponOf(CombatSite site, int logicId)
        {
            int idx = site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) ? info.WeaponIndex : -1;
            return idx >= 0 && site.Kernel.TryGetWeapon(idx, out CombatWeapon w) ? w : default;
        }

        private static Vector2 MachinePos(CombatSite site, int logicId) =>
            site.TryGetMachinePosition(logicId, out Vector2 p) ? p : new Vector2(float.NaN, float.NaN);

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        /// <summary>
        /// 模拟悬停：走 UiTooltip 的正式绑定（没 Attach 过的元素 NotifyEnter 直接忽略，所以这也核对了“挂上了运行时悬停提示”），
        /// 过了出现延迟后读提示面板要显示的内容（标题 + 正文）。VisualElement.tooltip 只在编辑器界面生效，不能拿它当玩家看到的说明。
        /// </summary>
        private static string HoverTip(VisualElement target)
        {
            Func<float> clock0 = UiTooltip.Clock;
            Func<bool> pin0 = UiTooltip.PinHeld;
            float t = 10000f;
            try
            {
                UiTooltip.Clock = () => t;
                UiTooltip.PinHeld = () => false;
                UiTooltip.Hide();
                if (target == null)
                {
                    return string.Empty;
                }
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                TooltipContent c = UiTooltip.Content;
                return c == null ? string.Empty : (c.Title ?? string.Empty) + "\n" + (c.Body ?? string.Empty);
            }
            finally
            {
                UiTooltip.Hide();
                UiTooltip.Clock = clock0;
                UiTooltip.PinHeld = pin0;
            }
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgSignalLinkSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        // ── 报告 ──────────────────────────────────────────────────────────────────

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
    }
}
