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
using BinGames.Sim.Signal;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
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
    /// FG1-SIG-07 信号覆盖网络与远距离跳转的自动验收（FG01 FGR-SIG-050～053；FGT-SIG-007；FGJ-M1 第 7、8 步；卡片负向：
    /// 中继塔被摧毁导致网络断开、机器在覆盖边缘反复进出、冷却期间反复按键、跳转途中目标阵亡）。
    /// 全部起真实系统：整个世界（家园 / 破碎都市控制器 + 战斗内核 + 统一时钟 + 全局镜头 + 区域接管系统每帧 Tick + 步首覆盖评估）、真实存档文件、
    /// 真 UXML HUD 与命令栏、真按键（经 InputRouter 与界面快捷键泵）——行为坏了会失败：
    /// A 数据；B 覆盖源与连通（核心 / 信号塔 T1·T2 / 中继塔链 / 断开的中继 / 损坏的中继 / 中继模块随机器移动；Burst 与托管连通逐项对照）；
    /// C 断开处高亮与通知（中继塔被摧毁 → 下游断开、机器走出覆盖）；D 覆盖之外（下命令被拒、只执行最后的命令、不能接入、不派家园工单、回到覆盖自动恢复）；
    /// E 远距离跳转（过渡 1.5 秒世界照常运行、冷却 10 游戏秒、近距离不受限、跨地点）；F 跳回家园 / 上一台（快捷键与 HUD 按钮，FGJ-M1 第 7、8 步）；
    /// G 负向（跳转途中目标阵亡、Esc、暂停、跳回家园后远征队全灭、边缘反复进出、冷却中连按）；H 暂停与 0.5x～3x；I 后台与观察一致；J 存读档；
    /// K 性能；L 界面（HUD、命令栏、叠加层、布局探针、中英）；M 覆盖扩张探索（FGR-LOG-013）；N 派遣检查单提醒接口；O 真实建造 / 拆除中继塔；P 远征地点覆盖。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgSignalNetworkSelfCheck
    {
        private const string HudUxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const string CommandBarUxmlPath = "Assets/GameRes/Raw/UI/RegionCommand/RegionCommandBar.uxml";
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_sig07_cannon_up";
        private const string BpGunPlain = "bp_selfcheck_sig07_gun";
        private const string BpRelay = "bp_selfcheck_sig07_relay";
        private const float FrameDt = 0.05f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 信号覆盖网络与远距离跳转")]
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
            Line("\n[覆盖网络] 信号覆盖网络与远距离跳转（FG1-SIG-07）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            Func<float> originalNotifyClock = NotificationCenter.Clock;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig07-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                NotificationCenter.Clock = () => _fakeNow;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"Burst 编译={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行，" +
                     "连通计算与逐机器覆盖评估在 AOT Burst 作业里（真机同为 Burst AOT），数字只作量级参考（真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSources);
                Step(CheckRelayModule);
                Step(CheckConnectKernel);
                Step(CheckCutHighlight);
                Step(CheckOutsideCoverage);
                Step(CheckWorkOrdersOutside);
                Step(CheckFarJump);
                Step(CheckCrossSiteAndHotkeys);
                Step(CheckNegatives);
                Step(CheckEdgeFlapping);
                Step(CheckPauseSpeed);
                Step(CheckBackgroundConsistency);
                Step(CheckSaveLoad);
                Step(CheckReveal);
                Step(CheckRouteInterface);
                Step(CheckRealBuildAndDemolish);
                Step(CheckExpeditionSite);
                Step(CheckPerformance);
                Step(CheckUi);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"覆盖网络自检抛异常：{e}");
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
                SignalUplinkService.ResetForTests();
                SignalLinkService.ResetForTests();
                SignalCoverageService.ResetForTests();
                SignalCoverageOverlayView.ResetForTests();
                SignalLinkView.Clear();
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                UiTooltip.ResetForTests();
                GameClock.ResetSession();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                NotificationCenter.Clock = originalNotifyClock;
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
            Line($"  · [覆盖网络] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：覆盖半径 / 跳转门槛 / 过渡 / 冷却读表；信号中继塔入建筑表；快捷键已接线；通知类型、引导钩子、文本键中英齐全");
            var expect = new Dictionary<string, float>
            {
                ["signal.coverage.core_radius"] = 150f,
                ["signal.coverage.tower_radius"] = 300f,
                ["signal.coverage.tower_t2_radius"] = 500f,
                ["signal.coverage.relay_tower_radius"] = 200f,
                ["signal.coverage.relay_module_radius"] = 80f,
                ["signal.coverage.expedition_uplink_radius"] = 150f,
                ["signal.jump.far_distance_cells"] = 500f,
                ["signal.jump.far_transition_seconds"] = 1.5f,
                ["signal.jump.far_cooldown_seconds"] = 10f,
            };
            var bad = expect.Where(kv => !GridContent.TryGetTuning(kv.Key, out float v) || !Mathf.Approximately(v, kv.Value)).Select(kv => kv.Key).ToList();
            if (GridContent.TryGetTuning("signal.coverage.relay_move_epsilon_cells", out _))
            {
                bad.Add("relay_move_epsilon_cells 应已删除（审查修复：连通缓存不再按移动距离近似）");
            }
            Expect(bad.Count == 0 && Mathf.Approximately(SignalCoverageService.TowerT2Radius, 500f) && Mathf.Approximately(SignalCoverageService.RelayTowerRadius, 200f)
                   && Mathf.Approximately(SignalCoverageService.RelayModuleRadius, 80f) && Mathf.Approximately(SignalUplinkService.FarDistanceCells, 500f)
                   && Mathf.Approximately(SignalUplinkService.FarTransitionSeconds, 1.5f) && Mathf.Approximately(SignalUplinkService.FarCooldownSeconds, 10f),
                $"fg.TbHomeTuning（FGR-SIG-050～052 初值）：信号塔 T2 500、中继塔 200、中继模块 80、远征接入点 150 格；跳转门槛 500 格、过渡 1.5 秒、冷却 10 秒{(bad.Count > 0 ? "——不符：" + string.Join(",", bad) : string.Empty)}");

            bool gridOk = GridContent.TryGetBuilding(HomeValleyLayout.BuildingTypeSignalRelay, out BuildingGrid g) && g.FootprintW == 2 && g.FootprintH == 2
                          && g.Placeable == 1 && g.Critical == 1 && g.MaxCount == 0 && g.UnlockRule == "always";
            Building row = FgContentTables.Buildings.FirstOrDefault(b => b.TypeId == HomeValleyLayout.BuildingTypeSignalRelay);
            bool rowOk = row != null && row.BuildScrap > 0 && row.BuildSeconds > 0f && Mathf.Approximately(row.PowerDemand, 0f)
                         && HomeValleyLayout.BuildProfile.ContainsKey(HomeValleyLayout.BuildingTypeSignalRelay);
            var placeable = new List<BuildingGrid>();
            HomeGridService.PlaceableTypes(placeable);
            Expect(gridOk && rowOk && placeable.Any(p => p.TypeId == HomeValleyLayout.BuildingTypeSignalRelay)
                   && GameText.Get("building.signal_relay.name") == "信号中继塔" && GameText.Get("building.signal_relay.name", GameLanguage.En) == "Signal Relay Tower",
                $"信号中继塔（FG04 2×2）入表：可放置、关键建筑（拆除要确认）、新建 {row?.BuildScrap} 废料 / {row?.BuildSeconds} 秒、不用电；出现在建造栏");

            var wired = new[] { (GameActionId.JumpHome, KeyCode.H), (GameActionId.JumpPreviousMachine, KeyCode.J), (GameActionId.ToggleOverlay, KeyCode.O) };
            var notWired = wired.Where(w => !InputActionCatalog.TryGet(w.Item1, out InputActionDef def) || def.Status != InputActionStatus.Wired
                                             || InputActionCatalog.DefaultChord(w.Item1).Key != w.Item2).Select(w => w.Item1.ToString()).ToList();
            Expect(notWired.Count == 0 && InputActionCatalog.TryGet(GameActionId.JumpHome, out InputActionDef jh) && (jh.Contexts & InputContext.Strategy) != 0
                   && (jh.Contexts & InputContext.Uplink) != 0,
                $"快捷键“跳回家园 H / 跳回上一台 J / 叠加层 O”已接线（不再弹“后续版本开放”），战略与接入上下文都能用，可重绑{(notWired.Count == 0 ? string.Empty : "——未接线：" + string.Join(",", notWired))}");

            var types = new[] { "signal_coverage_left", "signal_coverage_back", "signal_relay_cut" };
            Expect(types.All(t => NotificationCatalog.TryGetType(t, out NotifyTypeDef d) && d.AggregateWindowSeconds > 0f && d.KeepInHistory),
                "通知类型：走出覆盖（警告）、回到覆盖（信息）、中继断开（警告）已登记，同类聚合、进历史");
            var hooks = new[] { GuidanceHooks.SignalCoverageFirstOverlay, GuidanceHooks.SignalCoverageFirstLeft, GuidanceHooks.SignalRelayFirstCut, GuidanceHooks.SignalFirstFarJump };
            Expect(hooks.All(h => GuidanceHooks.Known.Contains(h)), "引导钩子（首次叠加层 / 首次走出覆盖 / 首次中继断开 / 首次远距离跳转）登记进 GuidanceHooks.Known（内容在 FG15-UX-04）");

            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files =
            {
                "Campaign/Signal/SignalCoverageService.cs", "Campaign/Signal/SignalUplinkService.cs", "Campaign/Regions/RegionSquadCommandSystem.cs",
                "UI/SignalCore/SignalCoreHudUIToolkit.cs", "UI/RegionCommand/RegionCommandBarUIToolkit.cs",
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)), "\"(signal\\.(?:coverage|jump|overlay|command|route)\\.[a-z_.]+|signal\\.uplink\\.reason\\.(?:jump_cooldown|no_previous|other_site))\""))
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
            Expect(keys.Count >= 30 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个覆盖网络 / 跳转文本键（下限 30）都在 fg.TbLocText 里、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");
            Expect(ComponentCatalog.TryGet(ComponentCatalog.StructRelayId, out MechanicalContentDef relay) && relay.ValuesSummary.Contains("80") && relay.DebtId == null,
                "结构件“信号中继”就是信号中继模块（装在机器结构槽里的移动中继，80 格），DEBT-ER4CONTENT01-07 关闭");
        }

        // ── B. 覆盖源与连通（FGR-SIG-050）───────────────────────────────────────

        private static void CheckSources()
        {
            Line("  · B. 覆盖源与连通：核心 150；信号塔修好通电 T1 300 / T2 500；中继塔立在已连通覆盖里才接上（链式延伸），断开的中继不提供覆盖；损坏的中继不算覆盖源");
            CampaignState s = NewHome(8701);
            Vector2 core = SignalUplinkService.CorePosition(s);
            BuildingRecord tower = Tower(s);
            string home = HomeValleyLayout.RegionId;
            Expect(SignalCoverageService.Sample(home, core + new Vector2(140f, 0f)).Covered && !SignalCoverageService.Sample(home, core + new Vector2(160f, 0f)).Covered
                   && SignalCoverageService.SourceCount == 1 && Info(home, 0).Kind == SignalCoverageSourceKind.Core && Info(home, 0).Connected,
                "开局：只有归还核心（信号塔损坏），离核心 140 格在覆盖里、160 格在覆盖外");
            RepairTower(s);
            Vector2 t = tower.Position;
            SignalCoverageSample t1 = SignalCoverageService.Sample(home, t + new Vector2(290f, 0f));
            SignalCoverageService.TowerTierProvider = b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower ? 2 : 1;
            SignalCoverageService.Invalidate();
            SignalCoverageSample t2 = SignalCoverageService.Sample(home, t + new Vector2(490f, 0f));
            SignalCoverageService.TowerTierProvider = null;
            SignalCoverageService.Invalidate();
            bool t1Out = !SignalCoverageService.Sample(home, t + new Vector2(490f, 0f)).Covered;
            Expect(t1.Covered && t1.SourceKind == SignalCoverageSourceKind.Tower && Mathf.Approximately(t1.SourceRadius, 300f)
                   && t2.Covered && Mathf.Approximately(t2.SourceRadius, 500f) && t1Out,
                "信号塔修好并通电：T1 覆盖 300 格；等级接口（FG4-ECO-05 原地升级）给 T2 时 500 格；回到 T1 后 490 格处又在覆盖外");

            BuildingRecord r1 = AddRelay(s, "r1", t + new Vector2(280f, 0f));
            SignalCoverageSample viaR1 = SignalCoverageService.Sample(home, t + new Vector2(470f, 0f));
            BuildingRecord lone = AddRelay(s, "lone", t + new Vector2(700f, 0f));
            int iLone = IndexOf(home, lone.BuildingId);
            bool loneCut = iLone >= 0 && !Info(home, iLone).Connected && !SignalCoverageService.Sample(home, lone.Position).Covered
                           && SignalCoverageService.DisconnectedCount(home) == 1;
            Expect(viaR1.Covered && viaR1.SourceKind == SignalCoverageSourceKind.RelayTower && Mathf.Approximately(viaR1.SourceRadius, 200f) && loneCut,
                "中继塔 r1 立在信号塔覆盖里：接上网络、覆盖延伸到 480 格；离得太远的中继塔与核心断开：它脚下也在覆盖外（断开 1 个）");
            BuildingRecord r2 = AddRelay(s, "r2", t + new Vector2(470f, 0f));
            bool stillCut = SignalCoverageService.DisconnectedCount(home) == 1;
            BuildingRecord r3 = AddRelay(s, "r3", t + new Vector2(650f, 0f));
            iLone = IndexOf(home, lone.BuildingId);
            int iR3 = IndexOf(home, r3.BuildingId);
            Expect(stillCut && SignalCoverageService.DisconnectedCount(home) == 0 && Info(home, iLone).Connected && Info(home, iLone).Parent == iR3
                   && SignalCoverageService.Sample(home, lone.Position + new Vector2(190f, 0f)).Covered,
                "补上 r2（离 lone 仍超过 200 格，仍断开）、r3 后整条链接上：lone 由 r3 接进网络（父节点 = r3），覆盖延伸到 890 格");
            r2.ConstructionState = BuildingConstructionState.Damaged;
            SignalCoverageService.Invalidate();
            bool damagedCut = SignalCoverageService.DisconnectedCount(home) == 2 && !SignalCoverageService.Sample(home, r3.Position).Covered
                              && IndexOf(home, r2.BuildingId) < 0;
            r2.ConstructionState = BuildingConstructionState.Operational;
            SignalCoverageService.Invalidate();
            Expect(damagedCut && SignalCoverageService.DisconnectedCount(home) == 0,
                "中间的 r2 损坏：它不再是覆盖源，下游 r3、lone 与核心断开（2 个）；修好后重新接上");
            tower.PowerState = BuildingPowerState.Brownout;
            SignalCoverageService.Invalidate();
            bool brownCut = SignalCoverageService.DisconnectedCount(home) == 4 && !SignalCoverageService.Sample(home, r1.Position).Covered;
            tower.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
            Expect(brownCut, "信号塔缺电：它不提供覆盖，立在它覆盖里的整条中继链与核心断开（中继塔自己不用电，但接不上网络也没用）");
        }

        private static void CheckRelayModule()
        {
            Line("  · B2. 信号中继模块（结构槽 = 信号中继的机器）：随机器移动的 80 格覆盖；立在已连通覆盖里才接上；进装配站 / 阵亡不再是覆盖源；连通缓存只在覆盖源逐位不变时沿用；被接入的中继机器余量不算自己的圈");
            CampaignState s = NewHome(8702);
            Vector2 core = SignalUplinkService.CorePosition(s);
            string home = HomeValleyLayout.RegionId;
            int rm = SpawnHome(BpRelay, new Vector2(6f, -4f));
            int gun = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Expect(SignalCoverageService.IsRelayMachine(rm) && !SignalCoverageService.IsRelayMachine(gun),
                "装配登记时认出中继模块：结构槽 = 信号中继的那台是中继机器，普通机器不是（按装配登记变化维护，不扫机器表）");
            Place(site, rm, core + new Vector2(140f, 0f));
            Boundary();
            int iRm = FindModule(home, rm);
            SignalCoverageSample via = SignalCoverageService.Sample(home, core + new Vector2(210f, 0f));
            Expect(iRm > 0 && Info(home, iRm).Connected && via.Covered && via.SourceKind == SignalCoverageSourceKind.RelayModule
                   && !SignalCoverageService.Sample(home, core + new Vector2(230f, 0f)).Covered,
                "中继机器停在核心覆盖边上（140 格）：接上网络，覆盖延伸到 220 格（230 格仍在外）");
            Place(site, rm, core + new Vector2(300f, 0f));
            Boundary();
            iRm = FindModule(home, rm);
            Expect(iRm > 0 && !Info(home, iRm).Connected && !SignalCoverageService.Sample(home, core + new Vector2(300f, 0f)).Covered
                   && SignalCoverageService.DisconnectedCount(home) == 1,
                "中继机器走到覆盖外（300 格）：与核心断开，不提供覆盖（覆盖源跟着机器移动，时间桶开头读一次位置）");
            // 审查修复（P2）：连通缓存是纯函数——覆盖源逐位完全相同才沿用；挪一点点也重算（原先“移动不到 1 格沿用”让连通取决于上次在哪一刻算过）。
            Place(site, rm, core + new Vector2(140f, 0f));
            Boundary();
            Vector2 still0 = Pos(site, rm);
            int bfs0 = SignalCoverageService.BfsCount;
            Boundary();
            bool still = Pos(site, rm) == still0;
            int bfsSame = SignalCoverageService.BfsCount - bfs0;
            Place(site, rm, core + new Vector2(140.4f, 0.3f));
            Boundary();
            int bfsSmall = SignalCoverageService.BfsCount - bfs0 - bfsSame;
            Expect(still && bfsSame == 0 && bfsSmall >= 1,
                $"覆盖源一点没变：沿用上次的连通结果（重算 {bfsSame} 次）；中继机器挪了 0.5 格：照样重算（{bfsSmall} 次，不再按 1 格近似）");
            Place(site, rm, core + new Vector2(149.6f, 0f));
            Boundary();
            bool edgeIn = Info(home, FindModule(home, rm)).Connected;
            Place(site, rm, core + new Vector2(150.4f, 0f));
            Boundary();
            bool edgeOut = FindModule(home, rm) > 0 && !Info(home, FindModule(home, rm)).Connected;
            Expect(edgeIn && edgeOut,
                "中继机器停在连通门槛边上：149.6 格接上；只挪 0.8 格到 150.4 格（圆心出了核心覆盖）立即断开——结果只取决于此刻的位置，继续玩与读档后一致");

            // 审查修复（P2）：被接入的中继机器，覆盖余量按“除它自己（以及只经由它接进网络的覆盖源）以外的连通覆盖”算，FG1-SIG-04 的边缘预警照常触发。
            Place(site, rm, core + new Vector2(140f, 0f));
            Boundary();
            CommitVia(rm);
            Frames(3);
            SignalCoverageSample selfSample = SignalCoverageService.SampleMachine(rm);
            bool edgeWarn = SignalLinkService.EdgeWarningActive;
            SignalCoverageSourceKind watchedKind = SignalLinkService.WatchedSample.SourceKind;
            float watchedMargin = SignalLinkService.WatchedSample.MarginCells;
            SignalUplinkService.RequestJumpHome(); // 离核心 140 格 = 近距离：立即回到核心。
            Frames(2);
            Expect(selfSample.Covered && selfSample.SourceKind == SignalCoverageSourceKind.Core && Math.Abs(selfSample.MarginCells - 10f) < 1f
                   && edgeWarn && watchedKind == SignalCoverageSourceKind.Core && watchedMargin < SignalCoverageService.EdgeWarnCells && SignalPresence.AtCore,
                $"接入停在核心覆盖边上（140 格）的中继机器：余量 {selfSample.MarginCells:F1} 格按核心算（不是自己跟着走的 80 格圈），边缘预警触发（{watchedMargin:F1} < {SignalCoverageService.EdgeWarnCells:F0}）");
            MachineRegistry.TryGetRecord(rm, out MachineRecord rec);
            rec.IsInFactory = true;
            Boundary();
            bool gone = FindModule(home, rm) < 0;
            rec.IsInFactory = false;
            Boundary();
            bool back = FindModule(home, rm) > 0;
            MachineRegistry.ApplyDamage(rm, 999999f);
            WorldSimulation.StepMany(2);
            Boundary();
            Expect(gone && back && FindModule(home, rm) < 0, "中继机器进装配站：不再是覆盖源；出来后恢复；阵亡：不再是覆盖源");
        }

        private static void CheckConnectKernel()
        {
            Line("  · B3. 连通计算（AOT Burst 作业）与托管实现逐项一致：80 组随机覆盖源（2～80 个，半径取 80 / 150 / 200 / 300 / 500）");
            var rng = new System.Random(8703);
            float[] radii = { 80f, 150f, 200f, 300f, 500f };
            int mismatch = 0;
            int connectedTotal = 0;
            for (int c = 0; c < 80; c++)
            {
                int n = 2 + rng.Next(79);
                var src = new List<float3>(n);
                for (int i = 0; i < n; i++)
                {
                    src.Add(new float3(rng.Next(-1200, 1200), rng.Next(-1200, 1200), radii[rng.Next(radii.Length)]));
                }
                var ca = new byte[n];
                var pa = new int[n];
                var cb = new byte[n];
                var pb = new int[n];
                SignalNetKernel.Connect(src, 1, ca, pa);
                SignalNetKernel.ConnectManaged(src, 1, cb, pb);
                for (int i = 0; i < n; i++)
                {
                    if (ca[i] != cb[i] || pa[i] != pb[i])
                    {
                        mismatch++;
                    }
                    connectedTotal += ca[i];
                    // 规则本身：接上的（非根）覆盖源，圆心一定落在父节点的覆盖里。
                    if (i > 0 && ca[i] != 0 && math.distance(src[i].xy, src[pa[i]].xy) > src[pa[i]].z + 1e-3f)
                    {
                        mismatch++;
                    }
                }
            }
            Expect(mismatch == 0 && connectedTotal > 80, $"80 组随机覆盖源：Burst 与托管的连通 / 父节点逐项相同，接上的圆心都在父节点覆盖里（接上 {connectedTotal} 个，不一致 {mismatch} 处）");
        }

        // ── C. 断开处高亮与通知 ──────────────────────────────────────────────────

        private static void CheckCutHighlight()
        {
            Line("  · C. 中继塔被摧毁导致网络断开：下游中继断开（通知 + 叠加层红圈与断开标记），远处的机器走出覆盖（通知）；修好后恢复");
            CampaignState s = NewHome(8704);
            string home = HomeValleyLayout.RegionId;
            BuildingRecord tower = Tower(s);
            RepairTower(s);
            Vector2 t = tower.Position;
            AddRelay(s, "c1", t + new Vector2(280f, 0f));
            BuildingRecord c2 = AddRelay(s, "c2", t + new Vector2(470f, 0f));
            BuildingRecord c3 = AddRelay(s, "c3", t + new Vector2(650f, 0f));
            int m = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Place(site, m, t + new Vector2(700f, 0f));
            SignalCoverageOverlayView.SetEnabled(true);
            Boundary();
            Boundary();
            Frames(1);
            int rings0 = SignalCoverageOverlayView.DrawnRings;
            Expect(!site.IsMachineOutOfCoverage(m) && rings0 == SignalCoverageService.SourceCount && SignalCoverageOverlayView.DrawnCut == 0
                   && SignalCoverageOverlayView.VisibleCutBadges == 0,
                $"整条链连通：700 格外的机器在覆盖里；叠加层画出 {rings0} 个覆盖圈，没有断开标记");
            int cut0 = NotifyCount("signal_relay_cut");
            int left0 = NotifyCount("signal_coverage_left");
            int leftStat0 = SignalCoverageService.LeftCount;
            c2.ConstructionState = BuildingConstructionState.Destroyed; // 摧毁的来源（突袭打建筑）在 FG6-DEF；这里直接置状态。
            Boundary();
            Frames(1);
            NotificationEntry cutEntry = LastOf("signal_relay_cut");
            NotificationMember cutMember = cutEntry?.Members.LastOrDefault();
            int i3 = IndexOf(home, c3.BuildingId);
            bool ringCut = i3 >= 0 && SignalCoverageOverlayView.RingShown(i3, out bool cutStyle) && cutStyle;
            Expect(SignalCoverageService.DisconnectedCount(home) == 1 && NotifyCount("signal_relay_cut") == cut0 + 1 && cutMember != null
                   && cutMember.Detail == GameText.Format("signal.coverage.relay_disconnected", GameText.Get("signal.coverage.kind.relay_tower"))
                   && cutMember.HasLocation && Vector2.Distance(new Vector2(cutMember.Location.x, cutMember.Location.z), c3.Position) < 1f && cutMember.RegionId == home,
                $"c2 被摧毁：下游 c3 与核心断开，发“中继断开”通知（“{cutMember?.Detail}”，可定位到 c3，记在家园）");
            Expect(ringCut && SignalCoverageOverlayView.DrawnCut == 1 && SignalCoverageOverlayView.VisibleCutBadges == 1,
                "叠加层高亮断开的位置：c3 画成红色粗圈，圆心上方一枚“断开的信号”标记（不只靠颜色）");
            NotificationMember leftMember = LastOf("signal_coverage_left")?.Members.LastOrDefault();
            Expect(site.IsMachineOutOfCoverage(m) && NotifyCount("signal_coverage_left") == left0 + 1 && SignalCoverageService.LeftCount == leftStat0 + 1
                   && leftMember != null && leftMember.Detail == GameText.Format("signal.coverage.left", SignalPresence.MachineLabel(m)),
                $"那台机器因此走出覆盖：内核标为覆盖外，发“走出覆盖”通知（“{leftMember?.Detail}”）");
            int back0 = NotifyCount("signal_coverage_back");
            c2.ConstructionState = BuildingConstructionState.Operational;
            Boundary();
            Frames(1);
            Expect(SignalCoverageService.DisconnectedCount(home) == 0 && !site.IsMachineOutOfCoverage(m) && NotifyCount("signal_coverage_back") == back0 + 1
                   && SignalCoverageOverlayView.DrawnCut == 0 && SignalCoverageOverlayView.VisibleCutBadges == 0,
                "c2 修好：c3 重新接上，红圈与标记撤掉；机器回到覆盖，发“回到覆盖”通知");
            SignalCoverageOverlayView.SetEnabled(false);
        }

        // ── D. 覆盖之外（FGR-SIG-053）──────────────────────────────────────────

        private static void CheckOutsideCoverage()
        {
            Line("  · D. 覆盖之外：派去覆盖外的命令照样下达但先提醒；到了之后收不到新命令（移动 / 守备 / 停止 / 派工）、不能接入，只执行最后的命令；信号塔修好后自动恢复");
            CampaignState s = NewHome(8705);
            Vector2 core = SignalUplinkService.CorePosition(s);
            HomeValleyController home = WorldSimulation.Home;
            int a = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = home.Combat;
            Place(site, a, core + new Vector2(100f, 0f));
            Place(site, b, core + new Vector2(100f, 4f));
            WorldSimulation.StepMany(1);
            int warn0 = RegionSquadCommandSystem.RouteWarnings;
            home.SquadCommands.DebugSelectMany(new[] { a, b });
            Vector2 target = core + new Vector2(200f, 0f);
            home.SquadCommands.IssueMoveTo(target, paused: false);
            string ev = home.SquadCommands.RecentEvents.LastOrDefault() ?? string.Empty;
            bool movingA = home.SquadCommands.TryGetActiveCommandKind(a, out RegionCommandKind ka) && ka == RegionCommandKind.Move;
            bool movingB = home.SquadCommands.TryGetActiveCommandKind(b, out RegionCommandKind kb) && kb == RegionCommandKind.Move;
            Expect(movingA && movingB && RegionSquadCommandSystem.RouteWarnings == warn0 + 1 && ev == GameText.Get("signal.command.target_outside"),
                $"派两台去覆盖外（离核心 200 格）：命令照样下达（玩家可以有意这么做），同时提醒“{ev}”");
            int left0 = SignalCoverageService.LeftCount;
            int guard = 0;
            while (guard++ < 60 * 60 && (Vector2.Distance(Pos(site, a), target) > 4f || Vector2.Distance(Pos(site, b), target) > 4f))
            {
                WorldSimulation.StepMany(1);
            }
            Boundary();
            Expect(site.IsMachineOutOfCoverage(a) && site.IsMachineOutOfCoverage(b) && SignalCoverageService.LeftCount >= left0 + 2,
                $"两台走到目标（{guard} 步）：途中越过覆盖边缘，内核把它们标为覆盖外，发“走出覆盖”通知");

            int rej0 = RegionSquadCommandSystem.OutOfCoverageRejects;
            home.SquadCommands.DebugSelectMany(new[] { a, b });
            home.SquadCommands.IssueMoveTo(core + new Vector2(10f, 0f), paused: false);
            string denied = home.SquadCommands.RecentEvents.Reverse().FirstOrDefault(e => e.StartsWith("2 台", StringComparison.Ordinal)) ?? string.Empty;
            // “只执行最后的命令”：还挂着命令的，也只能是原来那条（目标仍是 200 格处），不会是刚下的“回核心”。
            bool KeepsLast(int id) => !site.TryGetMachineUnit(id, out int u) || !site.TryGetCommand(u, out CombatCommand c)
                                      || Vector2.Distance(new Vector2((float)c.Pos.x, (float)c.Pos.y), target) < 3f;
            bool noMove = KeepsLast(a) && KeepsLast(b);
            home.SquadCommands.IssueGuardHere(paused: false);
            home.SquadCommands.IssueRetreat(paused: false);
            home.SquadCommands.Stop();
            Vector2 pa = Pos(site, a);
            WorldSimulation.StepMany(60 * 5);
            float drift = Vector2.Distance(pa, Pos(site, a));
            Expect(noMove && RegionSquadCommandSystem.OutOfCoverageRejects == rej0 + 8
                   && denied == GameText.Format("signal.command.out_of_coverage", "2", SignalPresence.MachineLabel(a) + GameText.Get("signal.uplink.status.list_sep") + SignalPresence.MachineLabel(b))
                   && drift < 1.5f,
                $"覆盖外收不到命令：移动 / 守备 / 撤退 / 停止都没下达（被拒 {RegionSquadCommandSystem.OutOfCoverageRejects - rej0} 台次，“{denied}”），5 秒里原地不动 {drift:F2} 格（只执行最后的命令，不自作主张走回来）");
            UplinkRequestResult up = SignalUplinkService.Request(a, UplinkSource.MachineList);
            Expect(!up.Accepted && up.Failure == UplinkFailure.OutOfCoverage && up.Text.Contains("中继"),
                $"覆盖外的机器不能接入：“{up.Text}”（第 5 章：提示需要信号中继塔或中继模块）");

            int back0 = SignalCoverageService.BackCount;
            RepairTower(s);
            Boundary();
            Expect(!site.IsMachineOutOfCoverage(a) && !site.IsMachineOutOfCoverage(b) && SignalCoverageService.BackCount >= back0 + 2,
                "信号塔修好并通电（300 格）：两台回到覆盖，控制自动恢复（发“回到覆盖”通知）");
            home.SquadCommands.DebugSelectMany(new[] { a, b });
            home.SquadCommands.IssueMoveTo(core + new Vector2(10f, 0f), paused: false);
            Expect(home.SquadCommands.TryGetActiveCommandKind(a, out RegionCommandKind k2) && k2 == RegionCommandKind.Move
                   && SignalUplinkService.Validate(s, a, out _) == UplinkFailure.None,
                "回到覆盖后：新的移动命令收得到，也可以接入");
        }

        private static void CheckWorkOrdersOutside()
        {
            Line("  · D2. 家园派工也是远程命令：覆盖外的空闲机器不会被派工；回到覆盖后下一轮自动派到它");
            CampaignState s = NewHome(8706);
            Vector2 core = SignalUplinkService.CorePosition(s);
            CombatSite site = WorldSimulation.Home.Combat;
            MachineOpResult hr = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                core + new Vector2(8f, -6f), 100f, 100f);
            WorldSimulation.StepMany(2);
            int h = hr.LogicId;
            int exiled = ExileOthers(site, h);
            Place(site, h, core + new Vector2(260f, 0f));
            Boundary();
            // 建造单走正式放置入口，不指定机器（进分配池，由家园自动派工）。
            GridCell cell = FindCell(s, core + new Vector2(24f, 18f), 20, fogOnly: false);
            GridOpResult placed = HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeSignalRelay, cell, 0);
            WorldSimulation.StepMany(60);
            WorkOrderRecord wo = s.WorkOrders?.FirstOrDefault(o => o.TargetId == placed.BuildingId);
            bool unassigned = wo != null && wo.AssignedMachineLogicId == 0;
            Place(site, h, core + new Vector2(20f, 0f));
            Boundary();
            WorldSimulation.StepMany(60);
            wo = s.WorkOrders?.FirstOrDefault(o => o.TargetId == placed.BuildingId);
            Expect(placed.Success && unassigned && wo != null && wo.AssignedMachineLogicId == h,
                $"唯一能施工的搬运机在覆盖外（其余 {exiled} 台也挪到覆盖外）：建造单 1 游戏秒内没人领；它回到覆盖后下一轮派给它（#{wo?.AssignedMachineLogicId}）");
        }

        // ── E. 远距离跳转（FGR-SIG-051、052）───────────────────────────────────

        private static void CheckFarJump()
        {
            Line("  · E. 远距离跳转：连通网络里 600 格外的机器也能跳；超过 500 格过渡 1.5 秒（世界照常运行）、冷却 10 游戏秒；冷却中近距离切换不受限，远距离被拒");
            CampaignState s = NewHome(8707);
            Vector2 core = SignalUplinkService.CorePosition(s);
            BuildingRecord tower = Tower(s);
            RepairTower(s);
            AddRelay(s, "j1", tower.Position + new Vector2(280f, 0f));
            AddRelay(s, "j2", tower.Position + new Vector2(470f, 0f));
            int near = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int far = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            int far2 = SpawnHome(BpGunPlain, new Vector2(10f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Place(site, far, core + new Vector2(600f, 0f));
            Place(site, far2, core + new Vector2(612f, 6f));
            Boundary();
            long t0 = GameClock.Ticks;
            int eval0 = SignalCoverageService.EvaluateCount;
            UplinkRequestResult r = SignalUplinkService.Request(far, UplinkSource.MachineList);
            string line = SignalUplinkService.StatusLine(s);
            bool pendingFar = SignalUplinkService.PendingIsFar && Math.Abs(SignalUplinkService.PendingDistance - 600f) < 8f;
            int frames = 0;
            while (SignalUplinkService.IsPending && frames < 80)
            {
                Frames(1);
                frames++;
            }
            long ticks = GameClock.Ticks - t0;
            Expect(r.Accepted && pendingFar && line.Contains("60") && frames >= 29 && frames <= 32 && ticks >= 85
                   && SignalCoverageService.EvaluateCount > eval0 && SignalPresence.CurrentMachineLogicId == far,
                $"从归还核心跳到 600 格外的机器：远距离跳转（“{line}”），过渡 {frames} 帧 ≈ 1.5 秒，期间世界照常推进 {ticks} 步（覆盖评估也在跑），信号进入目标");
            double cd = SignalUplinkService.JumpCooldownRemaining(s);
            Expect(Math.Abs(cd - 10.0) <= 0.2 && SignalUplinkService.FarJumpCount == 1, $"跳转完成后冷却 {cd:F2} 游戏秒（FGR-SIG-052 初值 10）");

            int accepted0 = SignalUplinkService.AcceptedCount;
            UplinkRequestResult hop = SignalUplinkService.Request(far2, UplinkSource.MachineList);
            bool hopNear = hop.Accepted && !SignalUplinkService.PendingIsFar;
            int hf = 0;
            while (SignalUplinkService.IsPending && hf < 40)
            {
                Frames(1);
                hf++;
            }
            Expect(hopNear && hf <= 8 && SignalPresence.CurrentMachineLogicId == far2 && SignalUplinkService.FarJumpCount == 1,
                $"冷却中跳到旁边 13 格的机器：近距离（0.35 秒，{hf} 帧），不受冷却限制，不重置冷却");

            long ready0 = s.SignalCore.JumpCooldownReadyTick;
            int rej0 = SignalUplinkService.RejectedCount;
            accepted0 = SignalUplinkService.AcceptedCount;
            UplinkRequestResult denied = default;
            for (int i = 0; i < 10; i++)
            {
                denied = SignalUplinkService.Request(near, UplinkSource.MachineList);
                Frames(1);
            }
            string cdText = SignalUplinkService.FailureText(UplinkFailure.JumpCooldown, near);
            Expect(!denied.Accepted && denied.Failure == UplinkFailure.JumpCooldown && SignalUplinkService.RejectedCount - rej0 == 10
                   && SignalUplinkService.AcceptedCount == accepted0 && s.SignalCore.JumpCooldownReadyTick == ready0
                   && SignalPresence.CurrentMachineLogicId == far2 && Regex.IsMatch(denied.Text, "\\d") && cdText.Contains("500"),
                $"冷却中连按 10 次跳回家园附近的机器（600 格）：每次都被拒（“{denied.Text}”），不排队、不延长冷却、信号不动");

            GameClock.SetPaused(true);
            double cdPaused = SignalUplinkService.JumpCooldownRemaining(s);
            Frames(40);
            double cdAfterPause = SignalUplinkService.JumpCooldownRemaining(s);
            GameClock.SetPaused(false);
            Expect(Math.Abs(cdPaused - cdAfterPause) < 1e-9, $"冷却按游戏时间：战略暂停 2 秒真实时间，冷却不走（{cdPaused:F2} → {cdAfterPause:F2}）");
            int g = 0;
            while (SignalUplinkService.JumpCooldownRemaining(s) > 0 && g++ < 60 * 12)
            {
                WorldSimulation.StepMany(1);
            }
            UplinkRequestResult after = SignalUplinkService.Request(near, UplinkSource.MachineList);
            bool afterFar = SignalUplinkService.PendingIsFar;
            int af = 0;
            while (SignalUplinkService.IsPending && af < 60)
            {
                Frames(1);
                af++;
            }
            Expect(after.Accepted && afterFar && SignalPresence.CurrentMachineLogicId == near && SignalUplinkService.FarJumpCount == 2,
                "冷却结束后：远距离跳回家园附近的机器成功（再次冷却）");
        }

        // ── F. 跨地点、跳回家园 / 上一台（FGJ-M1 第 7、8 步）─────────────────────

        private static void CheckCrossSiteAndHotkeys()
        {
            Line("  · F. 跨地点跳转（镜头留在原处直到到达）；按 H 跳回家园（过渡 1.5 秒）、再按 J 跳回远征队（冷却中被拒，冷却后成功）——FGJ-M1 第 7、8 步；HUD 按钮同一入口");
            CampaignState s = NewHome(8708);
            int h1 = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int c1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -22f), 20000f);
            int c2 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(6f, -22f), 20000f);
            WorldSimulation.StepMany(2);
            FracturedCityController city = OpenCity(s, c1, c2);
            WorldView.Observe(WorldSimulation.Home.SiteId);
            CommitVia(h1);
            bool nearFromCore = SignalUplinkService.FarJumpCount == 0 && SignalPresence.CurrentMachineLogicId == h1;
            // 接入前给 h1 一条守备命令：离开后按最后的命令继续（沿用 FC-REQ-051）。
            UplinkRequestResult r = SignalUplinkService.Request(c1, UplinkSource.MachineList);
            string line = SignalUplinkService.StatusLine(s);
            bool infDist = SignalUplinkService.PendingIsFar && float.IsPositiveInfinity(SignalUplinkService.PendingDistance);
            Frames(5);
            bool stayed = WorldView.ObservedSiteId == HomeValleyLayout.RegionId && SignalPresence.CurrentMachineLogicId == h1 && SignalUplinkService.IsPending;
            int f = 0;
            while (SignalUplinkService.IsPending && f < 60)
            {
                Frames(1);
                f++;
            }
            Frames(12);
            Expect(nearFromCore && r.Accepted && infDist && line.Contains(GameText.Get("signal.jump.other_site"))
                   && stayed && SignalPresence.CurrentMachineLogicId == c1 && WorldView.ObservedSiteId == city.SiteId && city.PossessedMachineLogicId == c1
                   && WorldSimulation.Home.PossessedMachineLogicId == null && WorldView.Director.Mode == ViewMode.Direct
                   && SignalUplinkService.PreviousMachine(s) == h1 && SignalUplinkService.JumpCooldownRemaining(s) > 9,
                $"从家园的机器跳到破碎都市的机器：跨地点 = 远距离（“{line}”）；过渡中镜头与信号留在原处，到达后镜头切到破碎都市并进直控，家园那台交还 AI；“上一台”= {SignalPresence.MachineLabel(h1)}");
            WaitCooldown(s);
            // FGJ-M1 第 7 步：按 H 跳回家园。
            int homeCount0 = SignalUplinkService.JumpHomeCount;
            PressPump(Key(GameActionId.JumpHome));
            bool jumping = SignalUplinkService.IsJumpingHome && WorldView.ObservedSiteId == city.SiteId && SignalPresence.CurrentMachineLogicId == c1;
            string homeLine = SignalUplinkService.StatusLine(s);
            f = 0;
            while (SignalUplinkService.IsJumpingHome && f < 60)
            {
                Frames(1);
                f++;
            }
            Frames(12);
            Expect(jumping && homeLine.StartsWith(GameText.Get("signal.jump.home_button"), StringComparison.Ordinal) && f >= 29 && f <= 32
                   && SignalPresence.AtCore && WorldView.ObservedSiteId == HomeValleyLayout.RegionId && city.PossessedMachineLogicId == null
                   && SignalUplinkService.JumpHomeCount == homeCount0 + 1 && SignalUplinkService.JumpCooldownRemaining(s) > 9,
                $"FGJ-M1 第 7 步：按 H（“{homeLine}”）过渡 {f} 帧后信号回到归还核心、镜头回到家园；从远征地点回家算远距离，开始冷却");
            PressPump(Key(GameActionId.JumpHome));
            Expect(SignalPresence.AtCore && !SignalUplinkService.IsJumpingHome && SignalUplinkService.JumpHomeCount == homeCount0 + 2
                   && SignalUplinkService.LastFeedbackText == GameText.Get("signal.jump.home_camera"),
                "信号已在核心时再按 H：只把镜头飞回家园（没有过渡、不受冷却限制）");
            Frames(15); // 镜头飞行落地（飞行途中发起接入会按“镜头正在拉回战略视角”明确拒绝）。
            // FGJ-M1 第 8 步：按 J 再跳回远征队——冷却中被拒，冷却后成功。
            PressPump(Key(GameActionId.JumpPreviousMachine));
            bool deniedCd = SignalUplinkService.LastFailure == UplinkFailure.JumpCooldown && SignalPresence.AtCore;
            WaitCooldown(s);
            PressPump(Key(GameActionId.JumpPreviousMachine));
            bool pend = SignalUplinkService.IsPending && SignalUplinkService.PendingTargetLogicId == c1 && SignalUplinkService.PendingSource == UplinkSource.JumpPrevious;
            f = 0;
            while (SignalUplinkService.IsPending && f < 60)
            {
                Frames(1);
                f++;
            }
            Frames(12);
            Expect(deniedCd && pend && SignalPresence.CurrentMachineLogicId == c1 && WorldView.ObservedSiteId == city.SiteId && WorldView.Director.Mode == ViewMode.Direct,
                "FGJ-M1 第 8 步：刚回家就按 J 被冷却拒绝；冷却结束再按 J，信号跳回远征队的那台机器（镜头切过去进直控）");
            // J 在同一地点：Tab 到 c2 后按 J 回 c1（近距离）。
            PressPump(Key(GameActionId.CycleControlTarget));
            int tf = 0;
            while (SignalUplinkService.IsPending && tf < 20)
            {
                Frames(1);
                tf++;
            }
            bool onC2 = SignalPresence.CurrentMachineLogicId == c2;
            int farJumps = SignalUplinkService.FarJumpCount;
            PressPump(Key(GameActionId.JumpPreviousMachine));
            tf = 0;
            while (SignalUplinkService.IsPending && tf < 20)
            {
                Frames(1);
                tf++;
            }
            Expect(onC2 && SignalPresence.CurrentMachineLogicId == c1 && SignalUplinkService.FarJumpCount == farJumps,
                "远征地点里 Tab 到另一台后按 J：回到上一台（同一地点、近距离，不进冷却）");

            // HUD 按钮：与快捷键同一入口。
            VisualElement hudRoot = MountUxml(HudUxmlPath, out GameObject hudGo);
            SignalCoreHudUIToolkit hud = hudGo.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(hudRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                hud.Refresh();
                Click(hud.JumpHomeButton);
                bool viaButton = SignalUplinkService.IsJumpingHome;
                int bf = 0;
                while (SignalUplinkService.IsJumpingHome && bf < 60)
                {
                    Frames(1);
                    bf++;
                }
                Frames(12);
                hud.Refresh();
                string prevText = hud.JumpPrevText;
                Click(hud.JumpPrevButton);
                bool prevDenied = SignalUplinkService.LastFailure == UplinkFailure.JumpCooldown;
                int toggles0 = SignalCoverageOverlayView.ToggleCount;
                Click(hud.CoverageToggleButton);
                hud.Refresh();
                bool overlayOn = SignalCoverageOverlayView.Enabled && hud.CoverageToggleText == GameText.Get("signal.overlay.button_on");
                Click(hud.CoverageToggleButton);
                Expect(viaButton && SignalPresence.AtCore && prevText.Contains(GameText.Get("signal.jump.prev_button")) && Regex.IsMatch(prevText, "\\d")
                       && prevDenied && overlayOn && !SignalCoverageOverlayView.Enabled && SignalCoverageOverlayView.ToggleCount == toggles0 + 2,
                    $"HUD 按钮：点“跳回家园”= 按 H；冷却中“上一台”按钮写明冷却（“{prevText}”），点了被拒；“覆盖网络”按钮开关叠加层");
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(hudGo);
            }
        }

        // ── G. 负向 ──────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · G. 负向：跳转途中目标阵亡 / Esc / 战略暂停；跳回家园途中 Esc（真实按键：HUD 压取消层 → 取消键 → 界面快捷键泵）；跳回家园途中信号自己离开；没有上一台；跳回家园后远征队全灭；近距离跳回家园立即完成");
            CampaignState s = NewHome(8709);
            Vector2 core = SignalUplinkService.CorePosition(s);
            BuildingRecord tower = Tower(s);
            RepairTower(s);
            AddRelay(s, "n1", tower.Position + new Vector2(280f, 0f));
            AddRelay(s, "n2", tower.Position + new Vector2(470f, 0f));
            int h = SpawnHome(BpGunPlain, new Vector2(4f, -4f));
            int far = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            int far2 = SpawnHome(BpGunPlain, new Vector2(10f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Place(site, far, core + new Vector2(600f, 0f));
            Place(site, far2, core + new Vector2(610f, 0f));
            Boundary();

            UplinkRequestResult none = SignalUplinkService.RequestJumpPrevious();
            Expect(!none.Accepted && none.Failure == UplinkFailure.NoPrevious && none.Text == GameText.Get("signal.uplink.reason.no_previous"),
                $"还没接入过任何机器时按 J：拒绝并说明（“{none.Text}”）");

            SignalUplinkService.Request(far, UplinkSource.MachineList);
            Frames(10);
            MachineRegistry.ApplyDamage(far, 999999f);
            Frames(2);
            Expect(!SignalUplinkService.IsPending && SignalUplinkService.LastCancel == UplinkCancelReason.TargetDead && SignalPresence.AtCore
                   && SignalUplinkService.JumpCooldownRemaining(s) <= 0 && SignalUplinkService.FarJumpCount == 0,
                "跳转途中目标阵亡：取消，信号留在原处（归还核心），不开始冷却");
            Frames(15);

            // 审查修复（P1）：Esc 走真实路径——HUD 每帧按“有没有能取消的过渡”在取消栈里压层，取消键由界面快捷键泵处理（与 Play 里 LateUpdate 同一段）。
            VisualElement escRoot = MountUxml(HudUxmlPath, out GameObject escGo);
            SignalCoreHudUIToolkit escHud = escGo.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                escHud.BindView(escRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                SignalUplinkService.Request(far2, UplinkSource.MachineList);
                Frames(5);
                int pauseFrame0 = UiKitInputPump.LastPauseMenuFrame;
                bool layerFar = PressEscViaHud(escHud);
                Frames(1);
                escHud.Refresh();
                Expect(layerFar && !SignalUplinkService.IsPending && SignalUplinkService.LastCancel == UplinkCancelReason.PlayerCancelled && SignalPresence.AtCore
                       && SignalUplinkService.JumpCooldownRemaining(s) <= 0 && !PauseMenuUIToolkit.IsOpen && UiKitInputPump.LastPauseMenuFrame == pauseFrame0
                       && UiEscapeStack.Count == 0,
                    "远距离跳转途中按 Esc（真实按键）：取消，信号留在原处，不开始冷却，暂停菜单不弹、取消栈清空");
                Frames(15);
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(escGo);
            }

            SignalUplinkService.Request(far2, UplinkSource.MachineList);
            Frames(4);
            GameClock.SetPaused(true);
            float rem0 = SignalUplinkService.PendingRemaining;
            long ticks0 = GameClock.Ticks;
            Frames(60);
            float rem1 = SignalUplinkService.PendingRemaining;
            bool held = SignalUplinkService.IsPending && Math.Abs(rem0 - rem1) < 1e-6f && GameClock.Ticks == ticks0;
            GameClock.SetPaused(false);
            int f = 0;
            while (SignalUplinkService.IsPending && f < 60)
            {
                Frames(1);
                f++;
            }
            Expect(held && SignalPresence.CurrentMachineLogicId == far2, $"跳转途中战略暂停 3 秒：过渡不走（剩 {rem1:F2} 秒）、世界不走；继续后完成");

            WaitCooldown(s);
            VisualElement homeEscRoot = MountUxml(HudUxmlPath, out GameObject homeEscGo);
            SignalCoreHudUIToolkit homeEscHud = homeEscGo.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                homeEscHud.BindView(homeEscRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                UplinkRequestResult hj = SignalUplinkService.RequestJumpHome();
                bool farHome = SignalUplinkService.IsJumpingHome;
                Frames(5);
                int homes0 = SignalUplinkService.JumpHomeCount;
                int pauseFrame1 = UiKitInputPump.LastPauseMenuFrame;
                bool layerHome = PressEscViaHud(homeEscHud);
                Frames(40);
                homeEscHud.Refresh();
                Expect(hj.Accepted && farHome && layerHome && !SignalUplinkService.IsJumpingHome && SignalPresence.CurrentMachineLogicId == far2
                       && SignalUplinkService.LastFeedbackText == GameText.Get("signal.jump.cancelled_home") && SignalUplinkService.JumpCooldownRemaining(s) <= 0
                       && SignalUplinkService.JumpHomeCount == homes0 && !PauseMenuUIToolkit.IsOpen && UiKitInputPump.LastPauseMenuFrame == pauseFrame1
                       && UiEscapeStack.Count == 0,
                    $"从 610 格外跳回家园途中按 Esc（真实按键）：HUD 压了取消层，取消跳回家园，信号留在那台机器里，不开始冷却，暂停菜单不弹（“{SignalUplinkService.LastFeedbackText}”）");
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(homeEscGo);
            }

            // 审查修复（P2）：跳回家园途中信号自己离开了那台机器（这里模拟玩家把镜头拉回战略 = 主动离开）：到点只把镜头飞回家园，不开始冷却。
            UplinkRequestResult hj2 = SignalUplinkService.RequestJumpHome();
            bool farHome2 = SignalUplinkService.IsJumpingHome;
            Frames(3);
            RegionControlSystem homeControl = RegionControlSystem.ForRegion(HomeValleyLayout.RegionId);
            homeControl?.ReleaseToStrategy(RegionControlChangeReason.PlayerRequest);
            bool leftMid = SignalPresence.AtCore;
            int hw = 0;
            while (SignalUplinkService.IsJumpingHome && hw < 80)
            {
                Frames(1);
                hw++;
            }
            Expect(hj2.Accepted && farHome2 && leftMid && !SignalUplinkService.IsJumpingHome && SignalPresence.AtCore
                   && SignalUplinkService.JumpCooldownRemaining(s) <= 0 && SignalUplinkService.LastFeedbackText == GameText.Get("signal.jump.home_done"),
                "跳回家园途中信号已经离开那台机器（回到核心）：到点只把镜头飞回家园，不开始远距离跳转冷却");

            // 审查修复（P2）：跨地点跳转到点提交前先在目标地点校验（只校验、不提交）：阵亡的目标不能提交，校验失败时不切镜头。
            RegionControlFailure okFail = RegionControlFailure.Ineligible;
            RegionControlFailure factoryFail = RegionControlFailure.None;
            bool canNear = homeControl != null && homeControl.CanCommitUplink(far2, out okFail);
            MachineRegistry.TryGetRecord(far2, out MachineRecord far2Rec);
            bool farWasInFactory = far2Rec != null && far2Rec.IsInFactory;
            if (far2Rec != null)
            {
                far2Rec.IsInFactory = true;
            }
            bool canFactory = homeControl != null && homeControl.CanCommitUplink(far2, out factoryFail);
            if (far2Rec != null)
            {
                far2Rec.IsInFactory = farWasInFactory;
            }
            Expect(canNear && okFail == RegionControlFailure.None && !canFactory && factoryFail == RegionControlFailure.Ineligible && SignalPresence.AtCore,
                $"接管预校验：能接管的目标通过；进了装配站的目标不通过（{factoryFail}），且只校验不提交（信号仍在核心）");
            Frames(30); // 让“拉回战略”的镜头落地（落地前发起接入会被“镜头正在拉回战略视角”拒绝）。
            WaitCooldown(s);
            CommitViaFar(far2);

            // 近距离跳回家园：立即完成、不冷却。
            CommitViaFar(h);
            int homeCount = SignalUplinkService.JumpHomeCount;
            double cdBefore = SignalUplinkService.JumpCooldownRemaining(s);
            UplinkRequestResult nearHome = SignalUplinkService.RequestJumpHome();
            Expect(nearHome.Accepted && !SignalUplinkService.IsJumpingHome && SignalPresence.AtCore && SignalUplinkService.JumpHomeCount == homeCount + 1
                   && Math.Abs(SignalUplinkService.JumpCooldownRemaining(s) - cdBefore) < 1e-6,
                "信号在家园附近的机器里（离核心不到 500 格）时按 H：立即回到核心，不开始新的冷却");
            Frames(15);

            // 跳回家园后远征队全灭：按全灭处理，信号留在家园、镜头不被拉走。
            int c1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -22f), 400f);
            FracturedCityController city = OpenCity(s, c1);
            WorldView.Observe(WorldSimulation.Home.SiteId);
            WaitCooldown(s);
            CommitViaFar(c1);
            SignalUplinkService.RequestJumpHome();
            int hf = 0;
            while (SignalUplinkService.IsJumpingHome && hf < 60)
            {
                Frames(1);
                hf++;
            }
            MachineRegistry.ApplyDamage(c1, 999999f);
            Frames(20);
            Expect(SignalPresence.AtCore && WorldView.ObservedSiteId == HomeValleyLayout.RegionId && city.PossessedMachineLogicId == null,
                "跳回家园后远处的远征队全灭：信号留在家园，镜头不被拉到远征地点（按全灭处理，FGR-EXP-031）");
        }

        private static void CheckEdgeFlapping()
        {
            Line("  · G2. 机器在覆盖边缘反复进出：只在步首的定期评估（每 0.5 游戏秒）判定一次，通知按类聚合不刷屏");
            CampaignState s = NewHome(8710);
            Vector2 core = SignalUplinkService.CorePosition(s);
            int m = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Place(site, m, core + new Vector2(140f, 0f));
            Boundary();
            Boundary();
            int eval0 = SignalCoverageService.EvaluateCount;
            int left0 = SignalCoverageService.LeftCount;
            int back0 = SignalCoverageService.BackCount;
            int entries0 = NotificationCenter.History.Count(e => e.Type?.Id == "signal_coverage_left");
            int toggles = 0;
            bool lastOut = false;
            for (int i = 0; i < 240; i++)
            {
                // 每个 0.5 秒桶的主边交替（每次定期评估都看到另一边），桶中间再额外跨两次边缘（最坏的反复进出）。
                bool outSide = (i / 30) % 2 == 0;
                if (i % 30 >= 10 && i % 30 < 20)
                {
                    outSide = !outSide;
                }
                Place(site, m, core + new Vector2(outSide ? 152f : 148f, 0f));
                AdvanceRealTime(1f / 60f);
                WorldSimulation.StepMany(1);
                bool o = site.IsMachineOutOfCoverage(m);
                if (o != lastOut)
                {
                    toggles++;
                    lastOut = o;
                }
            }
            int evals = SignalCoverageService.EvaluateCount - eval0;
            int changes = SignalCoverageService.LeftCount - left0 + SignalCoverageService.BackCount - back0;
            int entries = NotificationCenter.History.Count(e => e.Type?.Id == "signal_coverage_left") - entries0;
            Expect(evals == 8 && toggles == evals && changes == evals && entries <= 1,
                $"4 游戏秒里机器每 0.25 秒跨一次覆盖边缘：只评估 {evals} 次（每 0.5 游戏秒），状态跟着变 {toggles} 次（不会每步抖动），走出通知聚合成 {entries} 条");
        }

        // ── H. 暂停与倍速 ──────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · H. 0.5x～3x：从归还核心发起的远距离过渡按真实 1.5 秒（期间世界按倍速推进）；跳转冷却按游戏时间 10 秒到期；机器走出覆盖的那一步与倍速无关；暂停中什么都不走");
            float[] speeds = { 0.5f, 1f, 2f, 3f };
            var lines = new List<string>();
            var leftTicks = new List<long>();
            var cdGame = new List<double>();
            var transSteps = new List<long>();
            foreach (float speed in speeds)
            {
                CampaignState s = NewHome(8711);
                Vector2 core = SignalUplinkService.CorePosition(s);
                BuildingRecord tower = Tower(s);
                RepairTower(s);
                AddRelay(s, "p1", tower.Position + new Vector2(280f, 0f));
                AddRelay(s, "p2", tower.Position + new Vector2(470f, 0f));
                int far = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
                int walker = SpawnHome(BpGunPlain, new Vector2(10f, -4f));
                WorldSimulation.StepMany(2);
                CombatSite site = WorldSimulation.Home.Combat;
                Place(site, far, core + new Vector2(600f, 0f));
                Place(site, walker, core + new Vector2(20f, 60f));
                Boundary();
                // ① 从归还核心（战略视角，倍速生效）远距离跳转：过渡是真实 1.5 秒，期间世界按倍速推进。
                GameClock.SetSpeed(speed);
                long t0 = GameClock.Ticks;
                SignalUplinkService.Request(far, UplinkSource.MachineList);
                int guard = 0;
                while (SignalUplinkService.IsPending && guard++ < 80)
                {
                    Frames(1);
                }
                transSteps.Add(GameClock.Ticks - t0);
                // ② 跳回家园（接入中锁 1x）→ 回到核心后按倍速走完 10 游戏秒冷却。
                WaitCooldown(s);
                SignalUplinkService.RequestJumpHome();
                guard = 0;
                while (SignalUplinkService.IsJumpingHome && guard++ < 80)
                {
                    Frames(1);
                }
                Frames(10);
                GameClock.SetSpeed(speed);
                double start = SignalUplinkService.JumpCooldownRemaining(s);
                double g0 = GameClock.GameSeconds;
                int frames = 0;
                while (SignalUplinkService.JumpCooldownRemaining(s) > 0 && frames < 2000)
                {
                    Frames(1);
                    frames++;
                }
                cdGame.Add(GameClock.GameSeconds - g0 - start);
                // ③ 暂停：冷却、覆盖评估都不走。
                // ④ 走出覆盖：同一条命令从整桶边界下达，看内核在第几步把它标为覆盖外。
                while (GameClock.Ticks % 30 != 0)
                {
                    WorldSimulation.StepMany(1);
                }
                long cmdTick = GameClock.Ticks;
                WorldSimulation.Home.SquadCommands.DebugSelectMany(new[] { walker });
                WorldSimulation.Home.SquadCommands.IssueMoveTo(core + new Vector2(20f, -360f), paused: false);
                long leftAt = -1;
                guard = 0;
                while (leftAt < 0 && guard++ < 6000)
                {
                    Frames(1);
                    if (site.IsMachineOutOfCoverage(walker))
                    {
                        leftAt = SignalCoverageService.LastEvaluatedTick - cmdTick;
                    }
                }
                leftTicks.Add(leftAt);
                GameClock.SetSpeed(1f);
                lines.Add($"{speed}x：过渡期间世界走了 {transSteps.Last()} 步、冷却多走了 {cdGame.Last():F3} 游戏秒（{frames} 帧）、走出覆盖在下令后第 {leftAt} 步");
            }
            bool ok = true;
            for (int i = 0; i < speeds.Length; i++)
            {
                // 过渡按真实 1.5 秒（30 帧）；期间世界一直在走：镜头进直控前按所选倍速，进直控后按“接入锁 1x”（FG0-ARCH-01 既有规则）。
                ok &= transSteps[i] >= 60 && transSteps[i] <= 90 * Math.Max(1f, speeds[i]) + 12 && (i == 0 || transSteps[i] >= transSteps[i - 1]);
                ok &= cdGame[i] >= -1e-6 && cdGame[i] <= 0.05 * speeds[i] + 0.02;
            }
            Expect(ok && leftTicks.All(x => x > 0 && x == leftTicks[0]), "倍速矩阵：" + string.Join("；", lines));

            // 暂停：评估、冷却、过渡都不走。
            CampaignState p = NewHome(8721);
            Vector2 pc = SignalUplinkService.CorePosition(p);
            BuildingRecord pt = Tower(p);
            RepairTower(p);
            AddRelay(p, "q1", pt.Position + new Vector2(280f, 0f));
            AddRelay(p, "q2", pt.Position + new Vector2(470f, 0f));
            int pf = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            Place(WorldSimulation.Home.Combat, pf, pc + new Vector2(600f, 0f));
            Boundary();
            CommitViaFar(pf);
            SignalUplinkService.RequestJumpHome();
            while (SignalUplinkService.IsJumpingHome)
            {
                Frames(1);
            }
            Frames(4);
            GameClock.SetPaused(true);
            int eval0 = SignalCoverageService.EvaluateCount;
            double cd0 = SignalUplinkService.JumpCooldownRemaining(p);
            long tick0 = GameClock.Ticks;
            Frames(60);
            bool frozen = SignalCoverageService.EvaluateCount == eval0 && Math.Abs(SignalUplinkService.JumpCooldownRemaining(p) - cd0) < 1e-9 && GameClock.Ticks == tick0;
            GameClock.SetPaused(false);
            Expect(frozen, "战略暂停 3 秒真实时间：覆盖评估不走、跳转冷却不走、世界不走");
        }

        // ── I. 后台与观察一致（FGR-BASE-021）───────────────────────────────────

        private static void CheckBackgroundConsistency()
        {
            Line("  · I. 覆盖外“只执行最后的命令、回到覆盖自动恢复”在看着家园（帧驱动）与看着远征地点（家园不被观察、无头推进）时逐步一致");
            string[] logs = new string[2];
            ulong[] hashes = new ulong[2];
            for (int run = 0; run < 2; run++)
            {
                CampaignState s = NewHome(8712);
                Vector2 core = SignalUplinkService.CorePosition(s);
                BuildingRecord tower = Tower(s);
                int a = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
                int b = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
                int rm = SpawnHome(BpRelay, new Vector2(12f, -4f));
                int c1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -22f), 20000f);
                WorldSimulation.StepMany(2);
                FracturedCityController city = OpenCity(s, c1);
                HomeValleyController home = WorldSimulation.Home;
                CombatSite site = home.Combat;
                WorldView.Observe(run == 0 ? home.SiteId : city.SiteId);
                while (GameClock.Ticks % 60 != 0)
                {
                    WorldSimulation.StepMany(1);
                }
                long t0 = GameClock.Ticks;
                long repairTick = t0 + 60 * 45 + 15;
                long endTick = t0 + 60 * 90;
                home.SquadCommands.DebugSelectMany(new[] { a, b });
                home.SquadCommands.IssueMoveTo(core + new Vector2(260f, 0f), paused: false);
                home.SquadCommands.DebugSelectMany(new[] { rm });
                home.SquadCommands.IssueMoveTo(core + new Vector2(140f, 10f), paused: false);
                var log = new StringBuilder();
                int seenEval = SignalCoverageService.EvaluateCount;
                void Advance(long until)
                {
                    while (GameClock.Ticks < until)
                    {
                        if (run == 0)
                        {
                            AdvanceRealTime(FrameDt);
                            InputRouter.DebugClearConsumedKeys();
                            WorldSimulation.Frame(FrameDt, until); // 被观察：帧驱动（镜头、输入、每帧采样都在），精确停在同一步。
                        }
                        else
                        {
                            WorldSimulation.StepMany(1); // 不被观察：无头推进。
                        }
                        if (SignalCoverageService.EvaluateCount != seenEval)
                        {
                            seenEval = SignalCoverageService.EvaluateCount;
                            log.Append(SignalCoverageService.LastEvaluatedTick - t0).Append(':')
                                .Append(site.IsMachineOutOfCoverage(a) ? 1 : 0).Append(site.IsMachineOutOfCoverage(b) ? 1 : 0)
                                .Append(SignalCoverageService.IsRelayMachine(rm) ? 'r' : '-').Append(';');
                        }
                    }
                }
                Advance(repairTick);
                tower.ConstructionState = BuildingConstructionState.Operational;
                tower.PowerState = BuildingPowerState.Powered;
                // 同一时刻新建成一座中继塔（建筑表换了）：被观察时帧里马上有人采样（界面 / 下令），网络在步首之前就懒重建过——
                // 断开通知与探索扩张仍必须在同一步发生（回归：懒重建吃掉版本变化，被观察时探索不扩张）。
                AddRelayQuiet(s, "bg", tower.Position + new Vector2(0f, 280f));
                if (run == 0)
                {
                    SignalCoverageService.Sample(HomeValleyLayout.RegionId, core);
                }
                Advance(endTick);
                log.Append("explored=").Append(string.Join(",", s.Grid.Explored.Select(e => e.CenterX + "/" + e.CenterY + "/" + e.Radius))).Append(';');
                logs[run] = log.ToString();
                hashes[run] = site.Kernel.StateHash();
            }
            Expect(logs[0].Length > 0 && logs[0] == logs[1] && hashes[0] == hashes[1],
                $"90 游戏秒逐 0.5 秒对照：每次覆盖评估的结果（两台在不在覆盖里）逐次相同（{logs[0].Split(';').Length - 1} 次评估），家园内核状态哈希一致（{hashes[0]:X16}）");
            Expect(logs[0].Contains(":11") && logs[0].Contains(":00") && Regex.Matches(logs[0].Substring(logs[0].IndexOf("explored=", StringComparison.Ordinal)), "/").Count >= 6,
                "对照日志里确实出现了“两台都在覆盖外”（走出去）与“两台都在覆盖里”（修塔后恢复），且修塔 / 新中继塔把覆盖记为已探索（两边同样多，测试不是空跑）");
        }

        // ── J. 存读档 ──────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · J. 存读档：远距离跳转冷却、最近接入的机器、中继塔、覆盖外标志、覆盖扩张的已探索区域都进存档；读回后第一次评估不重复发通知；跳回家园过渡中存档读回不被弹出；旧档缺字段 = 没有冷却");
            CampaignState s = NewHome(8713);
            Vector2 core = SignalUplinkService.CorePosition(s);
            BuildingRecord tower = Tower(s);
            RepairTower(s);
            AddRelay(s, "s1", tower.Position + new Vector2(280f, 0f));
            AddRelay(s, "s2", tower.Position + new Vector2(470f, 0f));
            int near = SpawnHome(BpGunPlain, new Vector2(4f, -4f));
            int far = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            int lost = SpawnHome(BpGunPlain, new Vector2(10f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            Place(site, far, core + new Vector2(560f, 0f));
            Place(site, lost, core + new Vector2(-400f, 0f));
            Boundary();
            Boundary();
            CommitVia(near);
            CommitViaFar(far);
            SignalUplinkService.RequestJumpHome();
            while (SignalUplinkService.IsJumpingHome)
            {
                Frames(1);
            }
            WorldSimulation.StepMany(120);
            double cdBefore = SignalUplinkService.JumpCooldownRemaining(s);
            string recentBefore = string.Join(",", s.SignalCore.RecentUplinks);
            int exploredBefore = s.Grid.Explored.Length;
            bool lostOutBefore = site.IsMachineOutOfCoverage(lost);
            int leftBefore = SignalCoverageService.LeftCount;
            SaveNow();
            CampaignState l = LoadLikeMenu();
            CombatSite ls = WorldSimulation.Home.Combat;
            bool lostOutLoaded = ls.IsMachineOutOfCoverage(lost);
            double cdLoaded = SignalUplinkService.JumpCooldownRemaining(l);
            int notifLeft0 = NotifyCount("signal_coverage_left");
            Boundary();
            Boundary();
            Expect(l != null && Math.Abs(cdLoaded - cdBefore) < 1e-6 && string.Join(",", l.SignalCore.RecentUplinks) == recentBefore
                   && l.BuildingRecords.Any(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalRelay) && l.Grid.Explored.Length == exploredBefore && exploredBefore >= 3,
                $"读档后：跳转冷却还剩 {cdBefore:F2} 秒（原样）、最近接入 [{recentBefore}]、中继塔、已探索区域 {exploredBefore} 个圆（含信号塔 / 中继塔扩张的）逐项一致");
            Expect(lostOutBefore && lostOutLoaded && ls.IsMachineOutOfCoverage(lost) && NotifyCount("signal_coverage_left") == notifLeft0,
                "覆盖外标志随内核快照进存档：读回来那台仍在覆盖外；读档后的评估不重复发“走出覆盖”通知");
            int prev = SignalUplinkService.PreviousMachine(l);
            Expect(prev == far, $"读档后“上一台机器”= {SignalPresence.MachineLabel(prev)}（存档前最后接入的那台）");
            // 审查修复（P1）：跳回家园的远距离过渡中存档 → 回主菜单读档（世界卸载；不调自检复位，与正式路径一样）：
            // 过渡不进存档，也不能作为运行时状态带进读档后的对局——读回来信号在存档时那台机器里，不被弹出、不开始冷却、状态行不显示“跳回家园”。
            WaitCooldown(l);
            CommitVia(far);
            WaitCooldown(l);
            UplinkRequestResult pendHome = SignalUplinkService.RequestJumpHome();
            bool pendingAtSave = SignalUplinkService.IsJumpingHome && SignalPresence.CurrentMachineLogicId == far;
            Frames(5);
            SaveNow();
            CampaignState l2 = LoadLikeMenu(resetUplink: false);
            bool clearedOnLoad = !SignalUplinkService.IsJumpingHome && SignalUplinkService.JumpHomeRemaining <= 0f;
            string rawHome = GameText.Get("signal.jump.pending_home");
            int argAt = rawHome.IndexOf("{0}", StringComparison.Ordinal);
            string homePrefix = argAt > 0 ? rawHome.Substring(0, argAt) : rawHome;
            bool statusHomeAfterLoad = l2 != null && SignalUplinkService.StatusLine(l2).Contains(homePrefix);
            int homesAfterLoad = SignalUplinkService.JumpHomeCount;
            Frames(60); // 3 秒，远超 1.5 秒过渡
            Expect(pendHome.Accepted && pendingAtSave && l2 != null && clearedOnLoad && !statusHomeAfterLoad
                   && SignalPresence.CurrentMachineLogicId == far && SignalUplinkService.JumpCooldownRemaining(l2) <= 0
                   && SignalUplinkService.JumpHomeCount == homesAfterLoad && WorldView.ObservedSiteId == HomeValleyLayout.RegionId,
                $"跳回家园过渡中存档后回主菜单读档：过渡随世界卸载清掉，读回来信号仍在 {SignalPresence.MachineLabel(far)}，3 秒后也没被弹出、没开始冷却，状态行没有“跳回家园”");

            SignalCoreState old = JsonUtility.FromJson<SignalCoreState>("{\"DomainVersion\":1,\"SlotPartIds\":[\"\",\"\"],\"UplinkMachineLogicId\":0}");
            Expect(old.JumpCooldownReadyTick == 0 && old.RecentUplinks != null && old.RecentUplinks.Length == 0,
                "旧档（没有冷却 / 最近接入字段）读回来 = 没有冷却、没有上一台（只加字段、不升域版本）");
        }

        // ── M. 覆盖扩张探索（FGR-LOG-013）──────────────────────────────────────

        private static void CheckReveal()
        {
            Line("  · M. 探索靠信号塔覆盖扩张（FGR-LOG-013）：修好通电的信号塔 / 接上网络的中继塔把覆盖记为已探索，之后能在那里建造；断开的中继不扩张");
            CampaignState s = NewHome(8714);
            Vector2 core = SignalUplinkService.CorePosition(s);
            BuildingRecord tower = Tower(s);
            GridCell probe = FindCell(s, core + new Vector2(120f, 0f), 30, fogOnly: true);
            bool fogBefore = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalRelay, probe, 0).Reasons.Any(r => r.Code == GridBlockReason.Fog);
            int reveal0 = HomeGridService.RevealCount;
            RepairTower(s);
            Boundary();
            GridPlacementResult after = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalRelay, probe, 0);
            bool towerRevealed = s.Grid.Explored.Any(e => Math.Abs(e.CenterX - Mathf.RoundToInt(tower.Position.x)) <= 1 && Math.Abs(e.CenterY - Mathf.RoundToInt(tower.Position.y)) <= 1 && e.Radius == 300);
            Expect(fogBefore && towerRevealed && after.Reasons.All(r => r.Code != GridBlockReason.Fog) && HomeGridService.RevealCount == reveal0 + 1,
                $"信号塔修好通电前，离核心 120 格的 {probe} 在迷雾里不能建；之后它的 300 格覆盖记为已探索，同一格不再因迷雾被拒");
            BuildingRecord lone = AddRelay(s, "far_lone", core + new Vector2(1500f, 0f));
            BuildingRecord ok = AddRelay(s, "ok", tower.Position + new Vector2(280f, 0f));
            Boundary();
            bool okRevealed = s.Grid.Explored.Any(e => e.Radius == 200 && Math.Abs(e.CenterX - Mathf.RoundToInt(ok.Position.x)) <= 1);
            bool loneRevealed = s.Grid.Explored.Any(e => Math.Abs(e.CenterX - Mathf.RoundToInt(lone.Position.x)) <= 1 && Math.Abs(e.CenterY - Mathf.RoundToInt(lone.Position.y)) <= 1);
            int n = s.Grid.Explored.Length;
            Boundary();
            Expect(okRevealed && !loneRevealed && s.Grid.Explored.Length == n,
                "接上网络的中继塔把 200 格覆盖记为已探索；断开的中继塔不扩张；同一个圆不重复记（只增不减、幂等）");
        }

        // ── N. 派遣检查单的提醒接口 ─────────────────────────────────────────────

        private static void CheckRouteInterface()
        {
            Line("  · N. 派遣检查单“路线有一段走出信号覆盖”的提醒接口（FGR-EXP-001 / FG8-EXP-01 调用；编队移动命令已在用）");
            CampaignState s = NewHome(8715);
            Vector2 core = SignalUplinkService.CorePosition(s);
            string home = HomeValleyLayout.RegionId;
            var route = new List<Vector2> { core, core + new Vector2(400f, 0f) };
            float outCore = SignalCoverageService.RouteOutsideLength(home, route, out Vector2 first);
            RepairTower(s);
            BuildingRecord tower = Tower(s);
            float outTower = SignalCoverageService.RouteOutsideLength(home, route, out Vector2 first2);
            var inside = new List<Vector2> { core, core + new Vector2(100f, 0f) };
            float none = SignalCoverageService.RouteOutsideLength(home, inside, out _);
            float unbounded = SignalCoverageService.RouteOutsideLength("selfcheck_unknown_site", route, out _);
            float expectTower = 400f - (tower.Position.x - core.x + Mathf.Sqrt(300f * 300f - (tower.Position.y - core.y) * (tower.Position.y - core.y)));
            Expect(Math.Abs(outCore - 250f) <= 8f && Math.Abs(first.x - core.x - 150f) <= 5f && Math.Abs(outTower - expectTower) <= 8f && first2.x > first.x
                   && none == 0f && unbounded == 0f,
                $"核心到 400 格的直线：只有核心时约 {outCore:F0} 格在覆盖外（第一次走出在 {first.x - core.x:F0} 格处）；修好信号塔后约 {outTower:F0} 格；全在覆盖里 = 0；没有边界的地点 = 0");
        }

        // ── O. 真实建造 / 拆除中继塔 ─────────────────────────────────────────────

        private static void CheckRealBuildAndDemolish()
        {
            Line("  · O. 正式入口：建造模式同一个放置入口放信号中继塔 → 搬运机施工完工 → 成为覆盖源并接上网络；拆除要二次确认（关键建筑）→ 拆掉后不再是覆盖源");
            CampaignState s = NewHome(8716);
            Vector2 core = SignalUplinkService.CorePosition(s);
            string home = HomeValleyLayout.RegionId;
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, home, core + new Vector2(8f, -6f), 100f, 100f);
            WorldSimulation.StepMany(2);
            GridCell cell = FindCell(s, core + new Vector2(24f, 18f), 20, fogOnly: false);
            float scrap0 = s.Scrap;
            GridOpResult placed = HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeSignalRelay, cell, 0);
            string id = placed.BuildingId;
            int g = 0;
            while (g++ < 60 * 120 && HomeGridService.FindBuilding(s, id)?.ConstructionState != BuildingConstructionState.Operational)
            {
                WorldSimulation.StepMany(1);
            }
            Boundary();
            int idx = IndexOf(home, id);
            Expect(placed.Success && idx > 0 && Info(home, idx).Kind == SignalCoverageSourceKind.RelayTower && Info(home, idx).Connected && s.Scrap < scrap0,
                $"放置（{cell}）→ 施工 {g} 步完工 → 成为连通的中继塔覆盖源（扣了 {scrap0 - s.Scrap:F0} 废料）");
            bool needsConfirm = HomeGridService.DemolishNeedsConfirm(s, id);
            GridOpResult demolish = HomeGridService.TryToggleDemolish(s, id);
            g = 0;
            while (g++ < 60 * 120 && HomeGridService.FindBuilding(s, id) != null)
            {
                WorldSimulation.StepMany(1);
            }
            Boundary();
            Expect(needsConfirm && demolish.Success && HomeGridService.FindBuilding(s, id) == null && IndexOf(home, id) < 0,
                $"信号中继塔是关键建筑：拆除要二次确认（B04）；拆掉（{g} 步）后不再是覆盖源");
        }

        // ── P. 远征地点 ─────────────────────────────────────────────────────────

        private static void CheckExpeditionSite()
        {
            Line("  · P. 远征地点（独立表面，DEBT-FG0ARCH01-01）：以接入点为根（150 格），中继模块机器在那里同样延伸覆盖；覆盖外的远征机器收不到命令");
            CampaignState s = NewHome(8717);
            string cityId = FracturedCityLayout.RegionId;
            int c1 = SpawnRegion(cityId, BpGunPlain, new Vector2(-6f, -22f), 20000f);
            int rm = SpawnRegion(cityId, BpRelay, new Vector2(6f, -22f), 20000f);
            MachineLoadoutRegistry.Register(s, rm, BpRelay, 1);
            FracturedCityController city = OpenCity(s, c1, rm);
            WorldSimulation.StepMany(2);
            MachineLoadoutRegistry.Register(s, rm, BpRelay, 1);
            CombatSite site = city.Combat;
            Vector2 entry = FracturedCityLayout.EntryEvac.Position;
            Place(site, c1, entry + new Vector2(0f, 200f));
            Place(site, rm, entry + new Vector2(0f, 60f));
            Boundary();
            bool outside = site.IsMachineOutOfCoverage(c1);
            SignalCoverageSample root = SignalCoverageService.Sample(cityId, entry + new Vector2(0f, 100f));
            Place(site, rm, entry + new Vector2(0f, 140f));
            Boundary();
            SignalCoverageSample viaModule = SignalCoverageService.Sample(cityId, entry + new Vector2(0f, 200f));
            Expect(outside && root.Covered && root.SourceKind == SignalCoverageSourceKind.ExpeditionUplink && viaModule.Covered
                   && viaModule.SourceKind == SignalCoverageSourceKind.RelayModule && !site.IsMachineOutOfCoverage(c1),
                "破碎都市：接入点 150 格内在覆盖里；离接入点 200 格的机器在覆盖外；中继模块机器走到 140 格处后把覆盖延伸到那里，它回到覆盖");
            Place(site, rm, entry);
            Boundary();
            WorldView.Observe(city.SiteId);
            city.SquadCommands.DebugSelectMany(new[] { c1 });
            int rej0 = RegionSquadCommandSystem.OutOfCoverageRejects;
            city.SquadCommands.IssueMoveTo(entry, paused: false);
            Expect(RegionSquadCommandSystem.OutOfCoverageRejects == rej0 + 1 && !city.SquadCommands.TryGetActiveCommandKind(c1, out _),
                "远征地点覆盖外的机器同样收不到编队命令");
        }

        // ── K. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · K. 性能：连通计算与逐机器覆盖评估在 Burst 作业里；热更层每帧 O(1)（叠加层版本没变不重画、非评估步直接返回）");
            var rng = new System.Random(8718);
            var src = new List<float3>(200);
            src.Add(new float3(0, 0, 150));
            for (int i = 1; i < 200; i++)
            {
                src.Add(new float3(rng.Next(-2000, 2000), rng.Next(-2000, 2000), i % 3 == 0 ? 80f : 200f));
            }
            var conn = new byte[200];
            var par = new int[200];
            SignalNetKernel.Connect(src, 1, conn, par);
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                var sw = Stopwatch.StartNew();
                SignalNetKernel.Connect(src, 1, conn, par);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double bfsMedian = times[times.Count / 2];
            PerfLines.Add($"连通计算（200 个覆盖源，AOT Burst 作业含托管↔原生拷贝）中位 {bfsMedian:F3} ms / 最大 {times.Last():F3} ms");
            ExpectPerf(true, $"200 个覆盖源的连通计算中位 {bfsMedian:F3} ms（上限 2 ms，每 0.5 游戏秒最多一次，覆盖源没变时不算）", PerfGate.Lt(bfsMedian, 2.0, "连通计算中位 ms"));

            using (var kernel = new CombatKernel(CombatConfig.Default, 2100))
            {
                for (int i = 0; i < 2000; i++)
                {
                    kernel.Spawn(new CombatSpawn
                    {
                        ExtKey = i + 1,
                        Kind = CombatUnitKind.Machine,
                        Faction = CombatFaction.Player,
                        Behavior = CombatBehavior.None,
                        Flags = CombatUnitFlags.Alive,
                        Position = new double2(rng.Next(-3000, 3000), rng.Next(-3000, 3000)),
                        Radius = 0.5f,
                        Speed = 5f,
                        Health = 100f,
                        MaxHealth = 100f,
                        Weapon = -1,
                        BehaviorProfile = -1,
                    });
                }
                var circles = src.Take(64).ToList();
                var changes = new List<int2>();
                kernel.EvaluateCoverage(circles, changes);
                int firstChanges = changes.Count;
                var evalTimes = new List<double>();
                for (int i = 0; i < 40; i++)
                {
                    var sw = Stopwatch.StartNew();
                    kernel.EvaluateCoverage(circles, changes);
                    sw.Stop();
                    evalTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                evalTimes.Sort();
                double evalMedian = evalTimes[evalTimes.Count / 2];
                PerfLines.Add($"逐机器覆盖评估（内核 Burst 作业）：2,000 台机器 × 64 个覆盖圆，中位 {evalMedian:F3} ms；第一次 {firstChanges} 台变化，之后没有变化时 0 条");
                ExpectPerf(changes.Count == 0 && firstChanges > 0,
                    $"2,000 台机器 × 64 个覆盖圆的覆盖评估中位 {evalMedian:F3} ms（上限 2 ms，每 0.5 游戏秒一次）；状态没变时不产生变化条目（热更层只处理变化）", PerfGate.Lt(evalMedian, 2.0, "覆盖评估中位 ms"));
            }

            CampaignState s = NewHome(8719);
            BuildingRecord tower = Tower(s);
            RepairTower(s);
            for (int i = 0; i < 30; i++)
            {
                AddRelayQuiet(s, "perf" + i, tower.Position + new Vector2(200f + i * 40f, (i % 2) * 30f));
            }
            SignalCoverageService.Invalidate();
            int m = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            for (int i = 0; i < 40; i++)
            {
                SpawnHome(BpGunPlain, new Vector2(8f + (i % 8) * 2f, -8f - (i / 8) * 2f));
            }
            WorldSimulation.StepMany(2);
            var boundary = new List<double>();
            var other = Stopwatch.StartNew();
            int nonBoundary = 0;
            for (int i = 0; i < 600; i++)
            {
                bool isBoundary = GameClock.Ticks % 30 == 0;
                if (isBoundary)
                {
                    var sw = Stopwatch.StartNew();
                    SignalCoverageService.BeginStep(s);
                    sw.Stop();
                    boundary.Add(sw.Elapsed.TotalMilliseconds);
                }
                else
                {
                    other.Start();
                    SignalCoverageService.BeginStep(s);
                    other.Stop();
                    nonBoundary++;
                }
                WorldSimulation.StepMany(1);
            }
            boundary.Sort();
            double bMedian = boundary[boundary.Count / 2];
            double perNonUs = other.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, nonBoundary);
            PerfLines.Add($"步首覆盖评估（家园 41 台机器 + 31 个覆盖源）：评估步中位 {bMedian:F3} ms，非评估步平均 {perNonUs:F2} µs");
            ExpectPerf(true, $"步首覆盖评估：每 0.5 游戏秒一次、中位 {bMedian:F3} ms（上限 1.5 ms）；其余步直接返回（{perNonUs:F2} µs）",
                PerfGate.Lt(bMedian, 1.5, "步首覆盖评估中位 ms"), PerfGate.Lt(perNonUs, 5.0, "其余步 µs"));

            SignalCoverageOverlayView.SetEnabled(true);
            Frames(1);
            int redraw0 = SignalCoverageOverlayView.RedrawCount;
            var ov = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                SignalCoverageOverlayView.FrameTick();
            }
            ov.Stop();
            SignalCoverageOverlayView.SetEnabled(false);
            double ovUs = ov.Elapsed.TotalMilliseconds * 1000.0 / 2000;
            ExpectPerf(SignalCoverageOverlayView.RedrawCount == redraw0,
                $"叠加层每帧：网络没变时不重画（重画 {SignalCoverageOverlayView.RedrawCount - redraw0} 次），平均 {ovUs:F2} µs", PerfGate.Lt(ovUs, 10.0, "叠加层每帧 µs"));

            var sel = new List<int>();
            foreach (MachineRecord r in MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId).Take(40))
            {
                sel.Add(r.LogicId);
            }
            var cmd = Stopwatch.StartNew();
            for (int rep = 0; rep < 50; rep++)
            {
                foreach (int id in sel)
                {
                    SignalCoverageService.CanReceiveCommand(id);
                }
            }
            cmd.Stop();
            double perSel = cmd.Elapsed.TotalMilliseconds / 50;
            PerfLines.Add($"下命令时逐台现采样：{sel.Count} 台 × 32 个覆盖源，每次下令 {perSel:F3} ms");
            ExpectPerf(true, $"下令时 {sel.Count} 台机器逐台判定覆盖：{perSel:F3} ms（只在下令那一刻，上限 1 ms）", PerfGate.Lt(perSel, 1.0, "下令判定 ms"));
        }

        // ── L. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · L. 界面：跳转条按钮与悬停提示（中英、随重绑）；命令栏“覆盖外”标记与说明；叠加层开关键 O；布局探针");
            CampaignState s = NewHome(8720);
            Vector2 core = SignalUplinkService.CorePosition(s);
            int a = SpawnHome(BpGunPlain, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            CombatSite site = WorldSimulation.Home.Combat;
            VisualElement hudRoot = MountUxml(HudUxmlPath, out GameObject hudGo);
            SignalCoreHudUIToolkit hud = hudGo.AddComponent<SignalCoreHudUIToolkit>();
            VisualElement barRoot = MountUxml(CommandBarUxmlPath, out GameObject barGo);
            var bar = barGo.AddComponent<GameLogic.UI.RegionCommand.RegionCommandBarUIToolkit>();
            try
            {
                hud.BindView(hudRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                bar.BindView(barRoot);
                hud.Refresh();
                string zhHome = hud.JumpHomeText;
                string tipHome = HoverTip(hud.JumpHomeButton);
                string tipPrev = HoverTip(hud.JumpPrevButton);
                string tipCov = HoverTip(hud.CoverageToggleButton);
                bool prevDisabled = !hud.JumpPrevButton.enabledSelf;
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh();
                string enHome = hud.JumpHomeText;
                string enTip = HoverTip(hud.JumpHomeButton);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                hud.Refresh();
                Expect(hud.JumpBarVisible && zhHome == "跳回家园" && enHome == "Jump home" && tipHome.Contains(InputDisplay.ForAction(GameActionId.JumpHome))
                       && tipHome.Contains("500") && tipHome.Contains("1.5") && tipPrev.Contains(GameText.Get("signal.jump.prev_none_name")) && prevDisabled
                       && tipCov.Contains(InputDisplay.ForAction(GameActionId.ToggleOverlay)) && !Regex.IsMatch(enTip, "[\\u4e00-\\u9fff]") && !GameText.ContainsMarker(enTip),
                    $"跳转条：“{zhHome}”/“{enHome}”；悬停写明快捷键与规则（“{tipHome.Replace("\n", " / ")}”）；还没有上一台时“上一台”按钮置灰并说明；覆盖网络按钮悬停写明图例");

                // 改绑“跳回家园”：悬停提示跟着变。
                KeyCode freeKey = KeyCode.None;
                foreach (KeyCode k in new[] { KeyCode.F7, KeyCode.F8, KeyCode.F10, KeyCode.F11, KeyCode.Keypad7, KeyCode.Keypad8 })
                {
                    if (GameSettings.KeyBindings.TryRebind(GameActionId.JumpHome, new InputChord(k), new List<GameActionId>()) == RebindResult.Ok)
                    {
                        freeKey = k;
                        break;
                    }
                }
                hud.Refresh();
                string tipRebound = HoverTip(hud.JumpHomeButton);
                GameSettings.KeyBindings.ResetToDefault(GameActionId.JumpHome);
                Expect(freeKey != KeyCode.None && tipRebound.Contains(InputDisplay.Key(freeKey)), $"改绑“跳回家园”到 {freeKey} 后悬停提示跟着写新键");

                // 按 O：叠加层开关（战略上下文）。
                int toggles0 = SignalCoverageOverlayView.ToggleCount;
                PressPump(Key(GameActionId.ToggleOverlay));
                Frames(1);
                bool on = SignalCoverageOverlayView.Enabled && SignalCoverageOverlayView.Visible && SignalCoverageOverlayView.DrawnRings == SignalCoverageService.SourceCount;
                PressPump(Key(GameActionId.ToggleOverlay));
                Frames(1);
                Expect(on && !SignalCoverageOverlayView.Enabled && !SignalCoverageOverlayView.Visible && SignalCoverageOverlayView.ToggleCount == toggles0 + 2,
                    "按 O：叠加层打开（画出全部覆盖圈）；再按一次关闭");

                // 命令栏：覆盖外的机器带“覆盖外”标记与说明。
                Place(site, a, core + new Vector2(260f, 0f));
                Boundary();
                bar.Refresh();
                string tipA = HoverTip(bar.CandidateTooltipTarget(a));
                string tipB = HoverTip(bar.CandidateTooltipTarget(b));
                Expect(bar.CandidateShowsOutOfCoverage(a) && !bar.CandidateShowsOutOfCoverage(b)
                       && tipA.Contains(GameText.Format("signal.coverage.list_tooltip", SignalPresence.MachineLabel(a))) && !tipB.Contains(GameText.Get("signal.coverage.list_tag")),
                    $"命令栏机器列表：{SignalPresence.MachineLabel(a)} 带“{GameText.Get("signal.coverage.list_tag")}”标记（橙色边框 + 文字），悬停写明收不到命令、怎么恢复；{SignalPresence.MachineLabel(b)} 没有");
                Place(site, a, core + new Vector2(20f, 0f));
                Boundary();
                bar.Refresh();
                Expect(!bar.CandidateShowsOutOfCoverage(a), "回到覆盖后列表标记撤掉");

                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach ((string uxml, string rootName) in new[] { (HudUxmlPath, "SignalJumpBar"), (HudUxmlPath, "SignalHudBar"), (CommandBarUxmlPath, "RegionCommandBarRoot") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(uxml, rootName, stressFill: true, uiScale: 1f);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 {Path.GetFileName(uxml)}#{rootName} [{lang}]：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
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

        // ── 世界与机器（与 FgSignalLinkSelfCheck 同一套准备方式）──────────────────

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig07-" + seed, "Standard", seed);
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
            SignalCoverageOverlayView.ResetForTests();
            SignalLinkView.Clear();
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            cannon.TrySetUplink(2);
            AddBlueprint(s, BpCannonUp, cannon);
            AddBlueprint(s, BpGunPlain, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            AddBlueprint(s, BpRelay, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, ComponentCatalog.StructRelayId, Array.Empty<string>()));
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

        /// <summary>把家园里除 <paramref name="keep"/> 以外的存活机器（开局自带的）挪到覆盖外 400 格，返回挪了几台。</summary>
        private static int ExileOthers(CombatSite site, params int[] keep)
        {
            Vector2 core = SignalUplinkService.CorePosition(CampaignSession.Current);
            int n = 0;
            foreach (MachineRecord r in MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId && !keep.Contains(x.LogicId)).ToList())
            {
                Place(site, r.LogicId, core + new Vector2(-400f - 3f * n, 60f));
                n++;
            }
            return n;
        }

        private static BuildingRecord Tower(CampaignState s) => s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);

        private static void RepairTower(CampaignState s)
        {
            BuildingRecord t = Tower(s);
            t.ConstructionState = BuildingConstructionState.Operational;
            t.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
        }

        /// <summary>场景夹具：一座已建成的信号中继塔（真实建造路径见 O 段）。</summary>
        private static BuildingRecord AddRelay(CampaignState s, string key, Vector2 at)
        {
            BuildingRecord r = AddRelayQuiet(s, key, at);
            SignalCoverageService.Invalidate();
            return r;
        }

        private static BuildingRecord AddRelayQuiet(CampaignState s, string key, Vector2 at)
        {
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":selfcheck_relay_" + key,
                BuildingTypeId = HomeValleyLayout.BuildingTypeSignalRelay,
                RegionId = HomeValleyLayout.RegionId,
                GridX = Mathf.FloorToInt(at.x),
                GridY = Mathf.FloorToInt(at.y),
                Position = at,
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerState = BuildingPowerState.NotApplicable,
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            return r;
        }

        /// <summary>找一个放中继塔的格：<paramref name="fogOnly"/> = 只因迷雾被拒（探索扩张的探针）；否则 = 完全合法。</summary>
        private static GridCell FindCell(CampaignState s, Vector2 around, int radius, bool fogOnly)
        {
            var from = new GridCell(Mathf.RoundToInt(around.x), Mathf.RoundToInt(around.y));
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        GridPlacementResult v = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalRelay, c, 0);
                        bool hit = fogOnly ? v.Reasons.Count > 0 && v.Reasons.All(x => x.Code == GridBlockReason.Fog) : v.Ok;
                        if (hit)
                        {
                            return c;
                        }
                    }
                }
            }
            Fail($"测试准备：{around} 附近 {radius} 格内找不到能放中继塔的空地");
            return from;
        }

        private static SignalCoverageSourceInfo Info(string site, int index)
        {
            SignalCoverageService.TryGetSiteSource(site, index, out SignalCoverageSourceInfo info);
            return info;
        }

        private static int IndexOf(string site, string buildingId)
        {
            int n = SignalCoverageService.SiteSourceCount(site);
            for (int i = 0; i < n; i++)
            {
                if (Info(site, i).BuildingId == buildingId)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindModule(string site, int logicId)
        {
            int n = SignalCoverageService.SiteSourceCount(site);
            for (int i = 0; i < n; i++)
            {
                SignalCoverageSourceInfo info = Info(site, i);
                if (info.Kind == SignalCoverageSourceKind.RelayModule && info.LogicId == logicId)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>推进到下一次步首覆盖评估完成（每 0.5 游戏秒一次）。</summary>
        private static void Boundary()
        {
            int e0 = SignalCoverageService.EvaluateCount;
            int g = 0;
            while (SignalCoverageService.EvaluateCount == e0 && g++ < 90)
            {
                WorldSimulation.StepMany(1);
            }
        }

        private static void WaitCooldown(CampaignState s)
        {
            int g = 0;
            while (SignalUplinkService.JumpCooldownRemaining(s) > 0 && g++ < 60 * 12)
            {
                WorldSimulation.StepMany(1);
            }
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(e => e.Type?.Id == typeId).Sum(e => e.Count);

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(e => e.Type?.Id == typeId);

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
            while (SignalUplinkService.IsPending && n++ < 60)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        /// <summary>远距离接入（先等冷却结束）。</summary>
        private static void CommitViaFar(int logicId)
        {
            WaitCooldown(CampaignSession.Current);
            CommitVia(logicId);
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

        /// <summary>
        /// 像主菜单“继续”一样读档：卸载整个世界 → 复位时钟 → 读档 → 载入家园 → 恢复接入。
        /// <paramref name="resetUplink"/> = false 时不调接入服务的自检复位（与正式路径 GameRoot.EndRun → 读档完全一样，用来证明运行时过渡由世界卸载本身清掉）。
        /// </summary>
        private static CampaignState LoadLikeMenu(bool resetUplink = true)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            if (resetUplink)
            {
                SignalUplinkService.ResetForTests();
            }
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

        private static KeyCode Key(GameActionId action) => GameSettings.KeyBindings.GetKey(action);

        /// <summary>
        /// 真实 Esc 路径：HUD 刷新（按“有没有能取消的过渡”在取消栈里压层 / 撤层）→ 按下取消键 → 界面快捷键泵的取消键处理（与 Play 里 LateUpdate 同一段）。
        /// 返回按键前取消栈顶是否有层（HUD 压的“Esc 取消接入 / 跳回家园”）。
        /// </summary>
        private static bool PressEscViaHud(SignalCoreHudUIToolkit hud)
        {
            hud.Refresh();
            bool layer = UiEscapeStack.Top != null;
            Keys.Down = Key(GameActionId.Cancel);
            InputRouter.DebugClearConsumedKeys();
            UiKitInputPump.Process();
            Keys.Down = KeyCode.None;
            hud.Refresh();
            return layer;
        }

        /// <summary>按一次键：一帧世界（地点与镜头的按键）+ 界面快捷键泵（跳回家园 / 上一台 / 叠加层在这里读，与 Play 里 LateUpdate 同一段）。</summary>
        private static void PressPump(KeyCode key)
        {
            Keys.Down = key;
            InputRouter.DebugClearConsumedKeys();
            _fakeNow += FrameDt;
            WorldSimulation.Frame(FrameDt);
            UiKitInputPump.ProcessWorldKeys();
            Keys.Down = KeyCode.None;
        }

        private static Vector2 Pos(CombatSite site, int logicId) =>
            site.TryGetMachinePosition(logicId, out Vector2 p) ? p : new Vector2(float.NaN, float.NaN);

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

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
            go = new GameObject("__FgSignalNetworkSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        /// <summary>点一个 UI Toolkit 按钮：走按钮自己的 Clickable（与鼠标点击同一回调）。</summary>
        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
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

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

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
