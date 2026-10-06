using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-07 远征中家园遇袭的自动验收（FG06 FGR-DEF-041；FG01 FGR-SIG-050 / 051；验收 FGT-DEF-006 / 007；卡片负向“跳回家园后远征队全灭”“家园在无人观察时被攻破（按失败规则）”）。
    /// 全部起真实系统：真实家园（世界模拟、战斗内核攻城）、真实突袭导演（剧情突袭 → 预警 → 出发 → 沿地形行进 → 到达展开）、真实出发事务（ExpeditionDepartureService.TryDepart，远征地点载入、镜头飞过去）、
    /// 真实信号接入与跳回家园（FG1-SIG-07）、真 UXML 突袭条（含远征小窗）、真文件存读档。
    /// A 数据；B 远征 HUD 紧急通知与家园小窗（内容 / 钩子 / Esc 层 / 在家不显示 / 补提示音 / 英文 / 布局）；
    /// C 两种选择的结算与后台一致（同一种子同一次突袭：无人观察的后台运行 / 跳回家园 / 留在远征队 / 跳回家园后远征队全灭，家园结果逐位一致）；
    /// N 负向（跳回家园后远征队全灭；家园无人观察时核心被打空、按失败规则；面板开着时跳回家园被拒并写明原因）；S 存读档；T 暂停与 0.5x～3x；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgHomeRaidAlertSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgHomeRaidAlertSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 5;
        private const string HudUxml = "Assets/GameRes/Raw/UI/UiKit/RaidWarningHud.uxml";
        private const string L = "turret_light";
        private const float FrameDt = 1f / 60f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq;
        private static float _fakeNow;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 远征中家园遇袭")]
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
            Line("\n[远征中家园遇袭] 远征 HUD 紧急通知（倒计时）与家园状态小窗、跳回家园 / 留在远征队两种结算、后台一致、负向、存读档、暂停与倍速（FG6-DEF-07）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fghomeraid-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                HomeRaidAlertView.RealTimeForTests = () => _fakeNow;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                RaidDirectorService.EnsureArrivalAutoPauseRegistered();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；攻城单位计数在 AOT（Main/Sim），远征小窗汇总在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckAlertAndHome);
                Step(CheckStingWhenDepartingLate);
                Step(CheckLayout);
                Step(CheckSettlementAndBackground);
                Step(CheckJumpRejected);
                Step(CheckJumpCancelAndBranches);
                Step(CheckUnplannedWave);
                Step(CheckCoreFloorUnobserved);
                Step(CheckSaveLoad);
                Step(CheckPauseAndSpeed);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"远征中家园遇袭自检抛异常：{e}");
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
                HomeRaidAlertService.AwayOverrideForTests = null;
                HomeRaidAlertService.ResetSession();
                HomeRaidAlertView.RealTimeForTests = null;
                SignalUplinkService.RealTimeForTests = null;
                SignalUplinkService.ResetForTests();
                SignalLinkService.ResetForTests();
                SignalCoverageService.ResetForTests();
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                RaidSpectateService.ResetSession();
                RaidAudioDirector.ResetSession();
                StandingRuleService.RaidActiveProvider = null;
                StandingRuleService.ResetForTests();
                MachineRoster.ResetForTests();
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
            Line($"  · [远征中家园遇袭] 断言通过 {_pass}，失败 {_fail}");
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
            StandingRuleService.ResetForTests();
            MachineRoster.ResetForTests();
            WorldSimulation.UnloadAll();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SignalCoverageService.ResetForTests();
            HomeRaidAlertService.ResetSession();
            HomeRaidAlertService.AwayOverrideForTests = null;
            RaidAudioDirector.ResetSession();
            GameClock.ResetSession();
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            UiEscapeStack.Clear();
            _seq = 9700;
            CampaignState s = CampaignState.CreateNew("fghomeraid-" + seed, "Standard", seed);
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
            // 测试只看自己设的规则：删掉新战役默认开启的“远征卸货”（远征回来才触发，这里不会回来，删掉免得干扰）。
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
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

        /// <summary>测试捷径：一支离到达半径只差几格、正朝家园行进的突袭队伍（没有计划，与 FgRaidHudSelfCheck 同一夹具）；之后到达、展开全走正式流程。</summary>
        private static TransitGroupRecord Incoming(CampaignState s, int startDir, int units)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 at = OutsidePoint(s, (float)WorldTransitSystem.ArrivalRadius + 6f, startDir);
            TransitGroupRecord g = WorldTransitSystem.Dispatch(s, TransitGroupKind.Raid, "foundry", units, at.x, at.y, core.X, core.Y);
            g.Faction = "foundry";
            g.UnitIds = new[] { "foundry.strider" };
            g.UnitCounts = new[] { units };
            g.EliteCounts = new int[1];
            g.TargetKind = RaidDirectorService.TargetHome;
            return g;
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset, float hp)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, CoreCenter(s) + offset, hp, hp);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        /// <summary>出发的前置（与 FgAwayReportSelfCheck 相同）：再造三台能出征的机器、信号塔与发电机修好（带宽）、破碎都市可出征。返回出征名单（3 台）。</summary>
        private static int[] PrepareDeparture(CampaignState s)
        {
            Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f), 120f);
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f), 100f);
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(8f, -4f), 100f);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
                {
                    b.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：信号塔修好（出发的带宽要够；修复流程由 FG4-ECO-05 覆盖）
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
        }

        private sealed class Scene
        {
            public CampaignState S;
            public RaidPlanRecord Plan;
            public int[] Roster = Array.Empty<int>();
            public string Site = string.Empty;
            public string Failure;
        }

        /// <summary>
        /// 正式旅程：剧情突袭排定 → 出发远征（真实出发事务，镜头飞到远征地点）→ 信号接入一台远征机器（玩家“在远征”的正常状态）→ 到预警时刻发预警。
        /// <paramref name="departAfterWarn"/> = 先发预警、玩家这时才出发。<paramref name="atHome"/> 在出发前（镜头还在家园）调用。
        /// 筹备期用 SkipForTests 跳过（中间没有导演事件，等于读一个更晚的存档），之后全走正式流程。
        /// </summary>
        private static Scene Prepare(int seed, bool departAfterWarn = false, Action<CampaignState> atHome = null)
        {
            var sc = new Scene();
            CampaignState s = NewWorld(seed);
            sc.S = s;
            long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
            if (GameClock.Ticks < grace)
            {
                GameClock.SkipForTests(s, grace - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
            sc.Plan = p;
            if (p == null || !StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120))
            {
                sc.Failure = $"剧情突袭没有排定（{p?.State}）";
                return sc;
            }
            long lead = departAfterWarn ? 2 : GameClock.StepHz * 20L;
            if (p.WarnTick - lead > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, p.WarnTick - lead - GameClock.Ticks);
            }
            if (departAfterWarn && !StepUntil(() => p.State >= RaidDirectorService.StateWarned, 30))
            {
                sc.Failure = "出发前没有发出预警";
                return sc;
            }
            atHome?.Invoke(s);
            sc.Roster = PrepareDeparture(s);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(sc.Roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                sc.Failure = $"真实出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）";
                return sc;
            }
            s = CampaignSession.Current;
            sc.S = s;
            sc.Site = WorldSimulation.ActiveExpedition?.SiteId ?? string.Empty;
            if (sc.Site.Length == 0 || WorldView.IsObserved(HomeValleyLayout.RegionId))
            {
                sc.Failure = $"出发后没有远征地点或镜头还在家园（{sc.Site}，观察 {WorldView.ObservedSiteId}）";
                return sc;
            }
            if (!Uplink(sc.Roster[0], out string why))
            {
                sc.Failure = "接入远征机器失败：" + why;
                return sc;
            }
            if (!StepUntil(() => p.State >= RaidDirectorService.StateWarned, 60))
            {
                sc.Failure = $"没有发出预警（计划 {p.State}）";
            }
            return sc;
        }

        private static bool Uplink(int id, out string why)
        {
            UplinkRequestResult r = SignalUplinkService.Request(id, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                why = $"{r.Failure}：{r.Text}";
                return false;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 300)
            {
                Frames(1);
            }
            why = SignalPresence.CurrentMachineLogicId == id ? string.Empty : $"接入没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，“{SignalUplinkService.LastFeedbackText}”）";
            return why.Length == 0;
        }

        private static void Frames(int n, long limit = long.MaxValue)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt, limit);
            }
        }

        /// <summary>帧驱动推进到第 <paramref name="target"/> 步（带步数上限：不同路径在完全相同的步上停下）。</summary>
        private static void FramesTo(long target)
        {
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 900)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt, target);
            }
        }

        private static void HeadlessTo(long target)
        {
            if (target > GameClock.Ticks)
            {
                WorldSimulation.StepMany((int)(target - GameClock.Ticks));
            }
        }

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => F.StepUntil(done, maxGameSeconds);

        private static string Short(string s) => s == null ? string.Empty : s.Length > 220 ? s.Substring(0, 220) + "…" : s.Replace('\n', ' ');

        private static bool HasCjk(string text) => text != null && text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static VisualElement Mount(string uxml, out GameObject go, int w = 1920, int h = 1080, float scale = 1f)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxml);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.scale = scale;
            settings.targetTexture = new RenderTexture(w, h, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgHomeRaidAlertSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static RaidWarningHudUIToolkit MountHud(out GameObject go)
        {
            VisualElement root = Mount(HudUxml, out go);
            RaidWarningHudUIToolkit.InWorldOverrideForTests = true;
            var hud = go.AddComponent<RaidWarningHudUIToolkit>();
            hud.BindView(root);
            return hud;
        }

        private static void Unmount(GameObject go)
        {
            RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
            UiEscapeStack.Clear();
        }

        private sealed class DummyPanel
        {
        }

        /// <summary>家园侧的结果签名（只看家园：攻城域快照 + 家园战斗内核状态哈希 + 每座建筑的状态与耐久 + 突袭历史 + 到达波数 + 行进中的队伍）。</summary>
        private static string HomeSignature(CampaignState s)
        {
            CombatSite site = Site;
            var sb = new StringBuilder(1024);
            sb.Append(SiegeService.Snapshot(s)).Append("|H").Append(site != null ? site.Kernel.StateHash().ToString("X16") : "-");
            sb.Append("|A").Append(RaidDirectorService.ArrivedWaves(s));
            foreach (RaidHistoryRecord h in RaidDirectorService.History(s))
            {
                sb.Append("|h").Append(h.Wave).Append(':').Append(h.EndReason);
            }
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (g != null && g.Kind == TransitGroupKind.Raid)
                {
                    sb.Append("|g").Append(g.State).Append(':').Append(g.UnitCount);
                }
            }
            int destroyed = 0;
            double hp = 0;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                destroyed += b.ConstructionState == BuildingConstructionState.Destroyed ? 1 : 0;
                hp += RepairDroneService.DurabilityOf(s, b);
                sb.Append('|').Append(b.BuildingTypeId).Append(':').Append((int)b.ConstructionState).Append(':')
                  .Append(RepairDroneService.DurabilityOf(s, b).ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append("|D").Append(destroyed).Append("|HP").Append(hp.ToString("F3", CultureInfo.InvariantCulture)).Append("|T").Append(GameClock.Ticks);
            return sb.ToString();
        }

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static readonly string[] TuningKeys =
        {
            "raid.away.refresh_seconds", "raid.away.home_refresh_seconds", "raid.away.perf_ms", "raid.away.decision_keep", "raid.away.status_seconds", "raid.away.core_low_ratio",
            "raid.away.intact_ratio",
        };

        private static readonly string[] TextKeys =
        {
            "raid.away.title", "raid.away.countdown_incoming", "raid.away.countdown_assembling", "raid.away.countdown_arrived", "raid.away.countdown_fighting",
            "raid.away.countdown_retreat", "raid.away.wave_unplanned", "raid.away.more", "raid.away.hint", "raid.away.home_title", "raid.away.home_title_wave", "raid.away.core",
            "raid.away.core_floor", "raid.away.core_lost", "raid.away.core_missing", "raid.away.keys", "raid.away.key_count", "raid.away.worst", "raid.away.worst_destroyed",
            "raid.away.keys_tip", "raid.away.enemies_arrived", "raid.away.enemies_incoming", "raid.away.jump", "raid.away.stay", "raid.away.jump_tip", "raid.away.stay_tip",
            "raid.away.ok_jump", "raid.away.ok_stay", "raid.away.err_no_raid", "raid.away.err_jump", "raid.away.chosen_jump", "raid.away.chosen_stay",
            "codex.raid.away.title", "codex.raid.away.body", "codex.raid.away.hint",
        };

        private static void CheckData()
        {
            bool tuning = TuningKeys.All(k => GridContent.TryGetTuning(k, out _)) && Mathf.Approximately(HomeRaidAlertService.RefreshSeconds, 0.25f)
                          && Mathf.Approximately(HomeRaidAlertService.HomeRefreshSeconds, 0.5f) && HomeRaidAlertService.DecisionKeep == 8
                          && Mathf.Approximately(HomeRaidAlertService.StatusSeconds, 6f) && Mathf.Approximately(HomeRaidAlertService.CoreLowRatio, 0.3f)
                          && Mathf.Approximately(HomeRaidAlertService.IntactRatio, 0.999f);
            // raid.away.refresh_seconds 真的驱动远征弹窗的刷新节奏（宿主每帧问 Due，到点才刷新）：0.25 s 内不到点，累计超过 0.25 s 到点。
            var cadenceProbe = new HomeRaidAlertView();
            bool cadence = cadenceProbe.Due(0f) && !cadenceProbe.Due(0.1f) && !cadenceProbe.Due(0.1f) && cadenceProbe.Due(0.1f);
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in TextKeys)
                {
                    string t = GameText.Get(k);
                    if (string.IsNullOrEmpty(t) || GameText.ContainsMarker(t) || (lang == GameLanguage.En && HasCjk(t)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.RaidAwayFirstAlert) && GuidanceHooks.Known.Contains(GuidanceHooks.RaidAwayFirstChoice);
            bool codex = MechanicCodex.Find("codex.raid.away") != null;
            bool jumpKey = InputActionCatalog.TryGet(GameActionId.JumpHome, out InputActionDef def) && def.Status == InputActionStatus.Wired;
            Expect(tuning && cadence && missing.Count == 0 && hooks && codex && jumpKey,
                $"A1 数据：{TuningKeys.Length} 项调参入 fg.TbHomeTuning（刷新 0.25 s 且真的驱动弹窗刷新节奏 {cadence}、小窗汇总 0.5 s、保留 8 波选择、提示 6 s、核心条变红 30%、完好 99.9%）、{TextKeys.Length} 个文本键中英齐全（缺 {string.Join(",", missing.Take(6))}）、2 个引导钩子登记、图鉴条目（{codex}）、“跳回家园”快捷键已接线（{jumpKey}）");
        }

        // ── B 远征 HUD：紧急通知与家园小窗 ─────────────────────────────────────────────

        private static void CheckAlertAndHome()
        {
            GameObject go = null;
            try
            {
                RaidWarningHudUIToolkit hud = null;
                Scene sc = Prepare(7101, atHome: s0 =>
                {
                    RaidAudioDirector.Tick(s0, true, 1f, force: true);
                });
                if (sc.Failure != null)
                {
                    Fail("B 准备：" + sc.Failure);
                    return;
                }
                CampaignState s = sc.S;
                hud = MountHud(out go);
                // 预警在玩家已在远征时发出：音乐阶段从平静变成预警（导演自己响提示音），弹窗不重复补一声。
                hud.Refresh(force: true);
                RaidAudioDirector.Tick(s, true, 1f, force: true);
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;
                RaidPlanRecord p = sc.Plan;
                string dir = RaidDirectorService.DirectionText(p);
                bool content = v.PanelVisible && v.PopupVisible && v.StayVisible && v.AlertTitleText == GameText.Get("raid.away.title") && v.HintText == GameText.Get("raid.away.hint")
                               && v.CountdownText.Contains("后抵达家园") && v.CountdownText.Contains(dir) && v.CountdownText.Contains(GameText.Format("raid.warning.wave", p.Wave))
                               && v.CoreText.StartsWith("归还核心") && v.CoreText.Contains("%") && v.KeysText.Contains("电力") && v.KeysText.Contains("防御")
                               && v.EnemiesText.Contains(p.UnitTotal.ToString(CultureInfo.InvariantCulture)) && v.JumpButton.text.Contains(InputDisplay.ForAction(GameActionId.JumpHome))
                               && v.StayButton.text.Contains(InputDisplay.ForAction(GameActionId.Cancel)) && !GameText.ContainsMarker(v.CountdownText + v.CoreText + v.KeysText + v.EnemiesText);
                bool hooks = GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidAwayFirstAlert) && v.PopupShows == 1 && v.Stings == 0;
                bool esc = UiEscapeStack.Top == v;
                Expect(content && hooks && esc,
                    $"B1 FGR-DEF-041 远征 HUD：玩家在远征地点（信号在远征机器里）时家园这一波发出预警 → 弹出“{v.AlertTitleText}”，倒计时“{v.CountdownText}”（波次、方向）、两个选择的说明；" +
                    $"家园小窗“{v.CoreText}”/“{Short(v.KeysText)}”/“{v.EnemiesText}”；第一次弹出发引导钩子（{hooks}，导演已响预警提示音、弹窗不重复补）；Esc 在取消栈顶 = 留在远征队（{esc}）");

                // 核心与关键建筑读数和实际一致（攻城期间读内核；这里还没到达，读记录）。
                var st = new HomeRaidStatus();
                HomeRaidAlertService.CollectHome(s, st);
                BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
                int keyTotal = s.BuildingRecords.Count(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                                                             && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Damaged
                                                                 || b.ConstructionState == BuildingConstructionState.Disabled || b.ConstructionState == BuildingConstructionState.Destroyed)
                                                             && SiegeCatalog.CategoryOf(b.BuildingTypeId) != CombatSiegeConst.CatOther && SiegeCatalog.CategoryOf(b.BuildingTypeId) != CombatSiegeConst.CatCore);
                bool readout = st.CoreFound && Mathf.Abs(st.CoreHp - BuildingOps.Durability(core)) < 0.01f && st.Total.Sum() == keyTotal && st.IncomingUnits == p.UnitTotal && !st.EnemiesArrived;
                // 受损的一座关键建筑写进“最危险”。
                BuildingRecord gen = s.BuildingRecords.FirstOrDefault(b => b != null && b.RegionId == HomeValleyLayout.RegionId && SiegeCatalog.CategoryOf(b.BuildingTypeId) == CombatSiegeConst.CatPower
                                                                           && b.ConstructionState == BuildingConstructionState.Operational);
                if (gen != null)
                {
                    gen.Health = BuildingOps.MaxDurability(gen.BuildingTypeId) * 0.25f;
                }
                hud.Refresh(force: true);
                bool worst = gen != null && v.KeysText.Contains(BuildingOps.NameOf(gen)) && v.KeysText.Contains("25%");
                if (gen != null)
                {
                    gen.Health = BuildingOps.MaxDurability(gen.BuildingTypeId);
                }
                Expect(readout && worst, $"B2 家园小窗读数与实际一致：核心 {st.CoreHp:F0}/{st.CoreMax:F0}、关键建筑 {st.Total.Sum()} 座（= 实际 {keyTotal}）、来袭 {st.IncomingUnits} 台；受损 25% 的发电机写成“最危险”（“{Short(v.KeysText)}”）");

                // 镜头回到家园：不是“远征中”，小窗收起、Esc 层撤掉（家园的突袭条照常显示）；再切回远征地点又出现（还没选择）。
                WorldView.Observe(HomeValleyLayout.RegionId);
                hud.Refresh(force: true);
                bool hiddenAtHome = !v.PanelVisible && UiEscapeStack.Top != v && hud.PanelVisible && !HomeRaidAlertService.IsAway(s);
                WorldView.Observe(sc.Site);
                hud.Refresh(force: true);
                bool back = v.PopupVisible && v.PopupShows == 1;
                Expect(hiddenAtHome && back, $"B3 只在远征中显示：镜头回到家园时小窗收起、Esc 层撤掉、家园突袭条照常（{hiddenAtHome}）；再看远征地点又弹出（同一波不重复发钩子 / 提示音，{back}）");

                // B3b 复审 P1 负向：弹窗出现时已有整页开着（例如机器名册），之后关掉这页、再改开别的页面——紧急通知不能被连带关闭、不能替玩家记下“留在远征队”。
                // 弹窗之后打开的页面在它上面：Esc 先关页面。
                object pageA = new DummyPanel();
                object pageB = new DummyPanel();
                object pageC = new DummyPanel();
                try
                {
                    WorldView.Observe(HomeValleyLayout.RegionId);
                    hud.Refresh(force: true);
                    UiEscapeStack.RegisterPage(pageA, new VisualElement());
                    UiEscapeStack.RegisterPage(pageB, new VisualElement());
                    UiEscapeStack.RegisterPage(pageC, new VisualElement());
                    UiEscapeStack.Push(pageA, () => UiEscapeStack.Remove(pageA)); // 名册先开着
                    WorldView.Observe(sc.Site);
                    hud.Refresh(force: true); // 这时家园遇袭的弹窗出现
                    int stayC = HomeRaidAlertService.StayCount;
                    int jumpC = HomeRaidAlertService.JumpCount;
                    bool openUnder = v.PopupVisible && UiEscapeStack.Contains(v) && UiEscapeStack.Contains(pageA);
                    UiEscapeStack.Remove(pageA); // 玩家点名册的关闭按钮
                    hud.Refresh(force: true);
                    bool afterClose = v.PopupVisible && UiEscapeStack.Contains(v) && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceNone;
                    UiEscapeStack.Push(pageB, () => UiEscapeStack.Remove(pageB)); // 改开统计
                    UiEscapeStack.Push(pageC, () => UiEscapeStack.Remove(pageC)); // 再改开战略地图（同级页面互斥，统计被关掉）
                    hud.Refresh(force: true);
                    bool afterSwitch = !UiEscapeStack.Contains(pageB) && UiEscapeStack.Contains(pageC) && v.PopupVisible && UiEscapeStack.Contains(v)
                                       && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceNone;
                    bool escPage = UiEscapeStack.CloseTop(); // Esc：先关上面的页面
                    hud.Refresh(force: true);
                    bool pageFirst = escPage && !UiEscapeStack.Contains(pageC) && UiEscapeStack.Top == v && v.PopupVisible
                                     && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceNone;
                    // “首次选择”钩子只在 Stay / TryJumpHome 成功时发：两个计数都没变 = 钩子没被这条路径触发。
                    bool untouched = HomeRaidAlertService.StayCount == stayC && HomeRaidAlertService.JumpCount == jumpC
                                     && (RaidDirectorService.StateOf(s).AwayDecisions?.Length ?? 0) == 0;
                    Expect(openUnder && afterClose && afterSwitch && pageFirst && untouched,
                        $"B3b 负向（复审 P1）：名册开着时弹出紧急通知（{openUnder}）→ 关掉名册，通知还在、这一波仍未选择（{afterClose}）→ 改开统计再改开战略地图，同样不受影响（{afterSwitch}）→ " +
                        $"Esc 先关上面的页面、通知回到栈顶（{pageFirst}）；没有记下任何选择、没发“首次选择”钩子（{untouched}）");
                }
                finally
                {
                    UiEscapeStack.UnregisterPage(pageA);
                    UiEscapeStack.UnregisterPage(pageB);
                    UiEscapeStack.UnregisterPage(pageC);
                }

                // 英文界面没有中文残留。
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh(force: true);
                string en = v.AlertTitleText + v.HintText + v.CountdownText + v.CoreText + v.KeysText + v.EnemiesText + v.JumpButton.text + v.StayButton.text;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                hud.Refresh(force: true);
                Expect(!HasCjk(en) && !GameText.ContainsMarker(en), $"B4 英文界面：远征小窗没有中文残留（“{Short(en)}”）");

                // 留在远征队（Esc 真实路径：取消栈顶 = 小窗）→ 弹窗收起、小窗留着、写明选择；家园状态继续显示。
                int stay0 = HomeRaidAlertService.StayCount;
                int signal0 = SignalPresence.CurrentMachineLogicId; // B3 把镜头切回家园时信号已按“离开地点”回到核心（FG1-SIG-07 既有规则），这里只核对“留下”不再动信号
                bool escClosed = UiEscapeStack.CloseTop();
                hud.Refresh(force: true);
                bool stayed = escClosed && HomeRaidAlertService.StayCount == stay0 + 1 && v.PanelVisible && !v.PopupVisible && !v.StayVisible
                              && v.ChosenText == GameText.Get("raid.away.chosen_stay") && v.HomeTitleText.Contains(GameText.Format("raid.warning.wave", p.Wave))
                              && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceStay && SignalPresence.CurrentMachineLogicId == signal0
                              && WorldView.ObservedSiteId == sc.Site && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidAwayFirstChoice) && UiEscapeStack.Top != v
                              && v.StatusText.Contains(InputDisplay.ForAction(GameActionId.JumpHome));
                Expect(stayed, $"B5 留在远征队（Esc 真实路径，{escClosed}）：弹窗收起、家园小窗留着（“{v.ChosenText}”，标题“{v.HomeTitleText}”），信号不动（{SignalPresence.CurrentMachineLogicId}）、镜头还在远征地点；" +
                               $"提示“{v.StatusText}”；之后 Esc 照常（不再压层）");
            }
            finally
            {
                Unmount(go);
            }
        }

        /// <summary>突袭早就预警了，玩家这时才出发远征：音乐阶段没变（导演不会再响），弹窗补一声预警提示音。</summary>
        private static void CheckStingWhenDepartingLate()
        {
            GameObject go = null;
            try
            {
                RaidWarningHudUIToolkit hud = null;
                Scene sc = Prepare(7102, departAfterWarn: true, atHome: s0 =>
                {
                    RaidAudioDirector.Tick(s0, true, 1f, force: true);
                    hud = MountHud(out go);
                    hud.Refresh(force: true); // 在家园时的刷新：记下“预警阶段已经开始”
                });
                if (sc.Failure != null || hud == null)
                {
                    Fail("B6 准备：" + sc.Failure);
                    return;
                }
                int stings0 = RaidAudioDirector.StingCount;
                RaidAudioDirector.Tick(sc.S, true, 1f, force: true);
                bool phaseSame = RaidAudioDirector.StingCount == stings0 && RaidAudioDirector.Phase == RaidAudioDirector.PhaseWarning;
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;
                bool sting = v.PopupVisible && v.Stings == 1 && RaidAudioDirector.StingCount == stings0 + 1 && RaidAudioDirector.LastSting == RaidAudioDirector.StingWarning;
                hud.Refresh(force: true);
                bool once = v.Stings == 1;
                Expect(phaseSame && sting && once && v.CountdownText.Contains("集结中"),
                    $"B6 突袭已预警、玩家这时才出发：音乐阶段没变（导演不再响，{phaseSame}），弹窗补一声预警提示音且只响一次（{sting}/{once}）；倒计时写集结中（“{v.CountdownText}”）");
            }
            finally
            {
                Unmount(go);
            }
        }

        private static void CheckLayout()
        {
            string probe = UiToolkitLayoutProbe.Probe(HudUxml, "RaidWarnRoot", stressFill: true, prepare: rr =>
            {
                foreach (string n in new[] { "RaidAwayPanel", "RaidAwayAlert", "RaidAwayHint", "RaidAwayStay", "RaidAwayChosen", "RaidAwayStatus", "RaidWarnPanel", "RaidSpecBar" })
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
            var res = new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3440, 1440) };
            var lines = new List<string>();
            bool all = true;
            foreach (Vector2Int r in res)
            {
                foreach (float scale in new[] { 1f, 1.5f })
                {
                    VisualElement root = Mount(HudUxml, out GameObject go, r.x, r.y, scale);
                    try
                    {
                        foreach (string n in new[] { "RaidAwayPanel", "RaidAwayAlert", "RaidAwayHint", "RaidAwayStay", "RaidAwayChosen", "RaidAwayStatus", "RaidWarnPanel" })
                        {
                            root.Q<VisualElement>(n)?.RemoveFromClassList("uk-hidden");
                        }
                        foreach (Label lb in root.Q<VisualElement>("RaidAwayPanel").Query<Label>().ToList())
                        {
                            lb.text = new string('袭', 90);
                        }
                        UiToolkitLayoutProbe.ForceLayout(root);
                        VisualElement panel = root.Q<VisualElement>("RaidAwayPanel");
                        Button jump = root.Q<Button>("RaidAwayJump");
                        Button stay = root.Q<Button>("RaidAwayStay");
                        Rect screen = root.worldBound;
                        bool ok = panel.worldBound.height > 0f && panel.worldBound.xMax <= screen.width + 0.5f && panel.worldBound.yMax <= screen.height + 0.5f
                                  && jump.worldBound.height >= 20f && stay.worldBound.height >= 20f && jump.worldBound.yMax <= panel.worldBound.yMax + 0.5f
                                  && stay.worldBound.yMax <= panel.worldBound.yMax + 0.5f;
                        all &= ok;
                        lines.Add($"{r.x}x{r.y}@{scale:0.0}:{panel.worldBound.yMax / Mathf.Max(1f, screen.height):P0}" +
                                  (ok ? string.Empty : $"✗(屏 {screen.width:0}×{screen.height:0}，小窗 {panel.worldBound}，跳回 {jump.worldBound}，留下 {stay.worldBound})"));
                    }
                    finally
                    {
                        Object.DestroyImmediate(go);
                    }
                }
            }
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL") && all,
                "B7 布局：突袭条布局探针（远征小窗 + 弹窗 + 突袭条 + 观战栏；四种分辨率 + 超长文字 + USS 体检）" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]) +
                "；超长文字时小窗在屏幕内、两个按钮不被挤掉（按钮行不进滚动区）：" + string.Join("；", lines));
        }

        // ── C 两种选择的结算与后台一致（FGT-DEF-006 / 007）─────────────────────────────

        private const int ModeBackground = 0;
        private const int ModeJump = 1;
        private const int ModeStay = 2;
        private const int ModeJumpThenWipe = 3;

        private sealed class RunResult
        {
            public string Signature = string.Empty;
            public string Detail = string.Empty;
            public bool Ok;
            public string EndReason = string.Empty;
            public bool AlertGoneAfter;
        }

        /// <summary>
        /// 同一种子、同一次突袭：出发远征并接入远征机器 → 家园这一波预警 → 出发 → 到达展开 → 攻城 10 秒后（同一步）按模式操作 →
        /// 到第 40 秒（同一步）测试捷径全歼突袭部队 → 再走 5 秒结算。无人观察（后台，无头推进）/ 跳回家园（帧驱动，镜头在家园）/ 留在远征队（帧驱动，镜头在远征地点）/
        /// 跳回家园后远征队全灭（第 20 秒远征队全部阵亡）。返回家园侧签名。
        /// </summary>
        private static RunResult RunMode(int mode)
        {
            var rr = new RunResult();
            GameObject go = null;
            try
            {
                Scene sc = Prepare(7111);
                if (sc.Failure != null)
                {
                    rr.Detail = "准备失败：" + sc.Failure;
                    return rr;
                }
                CampaignState s = sc.S;
                RaidPlanRecord p = sc.Plan;
                if (!StepUntil(() => p.State >= RaidDirectorService.StateDeparted, 900))
                {
                    rr.Detail = "没有出发";
                    return rr;
                }
                TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
                if (g == null || !StepUntil(() => g.Engaged, 1800))
                {
                    rr.Detail = "没有到达展开";
                    return rr;
                }
                long e = GameClock.Ticks;
                HeadlessTo(e + GameClock.StepHz * 10L);
                var d = new StringBuilder();
                RaidWarningHudUIToolkit hud = null;
                if (mode != ModeBackground)
                {
                    hud = MountHud(out go);
                    hud.Refresh(force: true);
                    HomeRaidAlertView v = hud.Away;
                    bool arrivedText = v.PopupVisible && v.CountdownText.Contains("正在攻打家园") && v.EnemiesText.StartsWith("剩余敌人");
                    d.Append($"到达后弹窗“{v.CountdownText}”/“{v.EnemiesText}”（{arrivedText}）；");
                    rr.Ok = arrivedText;
                    if (mode == ModeJump || mode == ModeJumpThenWipe)
                    {
                        bool clicked = v.ClickJump();
                        bool pending = SignalUplinkService.IsJumpingHome && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceJumpHome;
                        int guard = 0;
                        while (SignalUplinkService.IsJumpingHome && guard++ < 240)
                        {
                            Frames(1, e + GameClock.StepHz * 40L);
                        }
                        hud.Refresh(force: true);
                        FracturedCityController city = WorldSimulation.FracturedCity;
                        bool home = SignalPresence.AtCore && WorldView.IsObserved(HomeValleyLayout.RegionId) && !HomeRaidAlertService.IsAway(s) && !v.PanelVisible;
                        bool ai = city != null && city.IsLoaded && city.PossessedMachineLogicId == null
                                  && MachineRegistry.TryGetRecord(sc.Roster[0], out MachineRecord m0) && m0.IsAlive && m0.RegionId == sc.Site;
                        d.Append($"点“跳回家园”（{clicked}，1.5 秒过渡 {pending}）→ 信号在核心、镜头在家园、小窗收起（{home}）；被离开的远征机器交给 AI、还在远征地点（{ai}）；");
                        rr.Ok &= clicked && pending && home && ai && v.StatusText == GameText.Get("raid.away.ok_jump");
                    }
                    else if (mode == ModeStay)
                    {
                        bool clicked = v.ClickStay();
                        hud.Refresh(force: true);
                        bool stay = clicked && !v.PopupVisible && v.PanelVisible && v.ChosenText == GameText.Get("raid.away.chosen_stay") && SignalPresence.CurrentMachineLogicId == sc.Roster[0]
                                    && WorldView.ObservedSiteId == sc.Site;
                        d.Append($"点“留在远征队”→ 弹窗收起、信号留在远征机器、镜头在远征地点（{stay}）；");
                        rr.Ok &= stay;
                    }
                }
                else
                {
                    rr.Ok = true;
                }
                long wipeAt = e + GameClock.StepHz * 20L;
                long killAt = e + GameClock.StepHz * 40L;
                bool frames = mode != ModeBackground;
                if (mode == ModeJumpThenWipe)
                {
                    FramesTo(wipeAt);
                    int wiped0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionWiped);
                    foreach (int id in sc.Roster)
                    {
                        MachineRegistry.ApplyDamage(id, 999999f);
                    }
                    Frames(30, killAt);
                    hud?.Refresh(force: true);
                    bool wiped = sc.Roster.All(id => !MachineRegistry.TryGetRecord(id, out MachineRecord m) || !m.IsAlive);
                    bool handled = FeedbackCues.CountOf(FeedbackCueId.ExpeditionWiped) > wiped0 && SignalPresence.AtCore && WorldView.IsObserved(HomeValleyLayout.RegionId)
                                   && hud != null && !hud.Away.PanelVisible && hud.PanelVisible;
                    d.Append($"跳回家园后远征队全灭（{wiped}）：按全灭处理（全灭提示 {handled}），信号留在核心、镜头不被拉走，家园突袭条照常；");
                    rr.Ok &= wiped && handled;
                }
                if (frames)
                {
                    FramesTo(killAt);
                }
                else
                {
                    HeadlessTo(killAt);
                }
                // 测试捷径：同一步全歼突袭部队（击毁本身由炮塔 / 机器打出来，见炮塔与攻城自检），之后的对账、历史结算走正式流程。
                CombatSite site = Site;
                var ids = new List<int>();
                foreach (TransitGroupRecord rg in WorldTransitSystem.Groups(s).Where(x => x != null && x.Kind == TransitGroupKind.Raid && x.Engaged).ToList())
                {
                    ids.Clear();
                    site.SiegeRaiderIds(SiegeService.KeyOf(rg), ids);
                    foreach (int id in ids)
                    {
                        site.Kernel.Kill(id, 0);
                    }
                }
                long end = killAt + GameClock.StepHz * 5L;
                if (frames)
                {
                    FramesTo(end);
                }
                else
                {
                    HeadlessTo(end);
                }
                RaidHistoryRecord h = RaidDirectorService.History(s).LastOrDefault(x => x.PlanId == p.PlanId);
                rr.EndReason = h?.EndReason ?? "-";
                if (hud != null)
                {
                    hud.Refresh(force: true);
                    rr.AlertGoneAfter = !hud.Away.PanelVisible && !HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out _);
                }
                else
                {
                    rr.AlertGoneAfter = !HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out _);
                }
                rr.Signature = HomeSignature(s);
                rr.Detail = d.ToString();
                return rr;
            }
            finally
            {
                Unmount(go);
            }
        }

        private static void CheckSettlementAndBackground()
        {
            RunResult bg = RunMode(ModeBackground);
            RunResult jump = RunMode(ModeJump);
            RunResult stay = RunMode(ModeStay);
            RunResult wipe = RunMode(ModeJumpThenWipe);
            Expect(jump.Ok && jump.EndReason == RaidDirectorService.EndDestroyed && jump.AlertGoneAfter,
                $"C1 FGT-DEF-006 跳回家园：{jump.Detail}突袭结算（历史结束原因 {jump.EndReason}），结束后小窗消失（{jump.AlertGoneAfter}）");
            Expect(stay.Ok && stay.EndReason == RaidDirectorService.EndDestroyed && stay.AlertGoneAfter,
                $"C2 FGT-DEF-006 留在远征队：{stay.Detail}家园自己守，突袭照常结算（{stay.EndReason}），结束后远征 HUD 的小窗自动消失（{stay.AlertGoneAfter}）");
            bool same = bg.Signature.Length > 0 && bg.Signature == jump.Signature && bg.Signature == stay.Signature;
            string diff = same ? string.Empty
                : $"\n后台：{Short(bg.Signature)}\n跳回：{Short(jump.Signature)}\n留下：{Short(stay.Signature)}（{bg.Detail}）";
            Expect(same && bg.EndReason == RaidDirectorService.EndDestroyed,
                $"C3 FGT-DEF-007 / FGR-BASE-021 后台一致：同一次突袭在“无人观察（无头推进）”“跳回家园（镜头在家园、帧驱动）”“留在远征队（镜头在远征地点、帧驱动）”三种情况下，" +
                $"家园的攻城快照、战斗内核状态哈希、每座建筑的状态与耐久、突袭历史逐位一致（{(same ? Short(bg.Signature.Substring(0, Math.Min(80, bg.Signature.Length))) : "不一致")}…）" + diff);
            Expect(wipe.Ok && wipe.Signature == bg.Signature && wipe.EndReason == RaidDirectorService.EndDestroyed,
                $"N1 负向“跳回家园后远征队全灭”：{wipe.Detail}家园的突袭结果仍与后台运行逐位一致（{wipe.Signature == bg.Signature}）" +
                (wipe.Signature == bg.Signature ? string.Empty : $"\n全灭：{Short(wipe.Signature)}"));
        }

        // ── N 负向：面板开着时跳回家园被拒；家园无人观察时核心被打空 ───────────────────────────

        private static void CheckJumpRejected()
        {
            GameObject go = null;
            object dummy = new DummyPanel();
            try
            {
                Scene sc = Prepare(7121);
                if (sc.Failure != null)
                {
                    Fail("N2 准备：" + sc.Failure);
                    return;
                }
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                InputRouter.PushModal(dummy);
                int rej0 = HomeRaidAlertService.RejectedCount;
                bool ok = hud.Away.ClickJump();
                hud.Refresh(force: true);
                string reason = hud.Away.StatusText;
                bool rejected = !ok && HomeRaidAlertService.RejectedCount == rej0 + 1 && hud.Away.StatusText.StartsWith(GameText.Format("raid.away.err_jump", string.Empty).TrimEnd())
                                && HomeRaidAlertService.DecisionOf(sc.S, sc.Plan.Wave) == HomeRaidAlertService.ChoiceNone && hud.Away.PopupVisible && SignalPresence.CurrentMachineLogicId == sc.Roster[0];
                InputRouter.PopModal(dummy);
                bool ok2 = hud.Away.ClickJump();
                Expect(rejected && ok2,
                    $"N2 面板开着时点“跳回家园”：被拒并写明原因（“{reason}”），不记选择、弹窗还在、信号不动；关掉面板后再点就能跳（{ok2}，“{hud.Away.StatusText}”）");
            }
            finally
            {
                InputRouter.PopModal(dummy);
                Unmount(go);
            }
        }

        /// <summary>
        /// 复审 P2：跳回家园的 1.5 秒过渡被 Esc 取消 → 这一波的选择撤销、弹窗回来；过渡完成才写进存档（时刻 / 地点按发起时）。
        /// 判定分支：只有情报的突袭、目标是前哨站的突袭不弹（小窗显示着时按 H 回退到普通的跳回家园）；多波时写“共 n 波”、第一波选过后新来的一波照常弹出。
        /// </summary>
        private static void CheckJumpCancelAndBranches()
        {
            GameObject go = null;
            try
            {
                Scene sc = Prepare(7171);
                if (sc.Failure != null)
                {
                    Fail("N5 准备：" + sc.Failure);
                    return;
                }
                CampaignState s = sc.S;
                RaidPlanRecord p = sc.Plan;
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;

                // N5 过渡中取消。
                int cancel0 = HomeRaidAlertService.JumpCancelledCount;
                bool clicked = v.ClickJump();
                hud.Refresh(force: true);
                bool pending = clicked && SignalUplinkService.IsJumpingHome && HomeRaidAlertService.JumpPending && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceJumpHome
                               && (RaidDirectorService.StateOf(s).AwayDecisions?.Length ?? 0) == 0 && !v.PopupVisible;
                bool cancelled = SignalUplinkService.CancelByPlayer(); // FG1-SIG-07：过渡中按 Esc
                hud.Refresh(force: true);
                bool reverted = cancelled && !HomeRaidAlertService.JumpPending && HomeRaidAlertService.JumpCancelledCount == cancel0 + 1
                                && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceNone && v.PopupVisible
                                && SignalPresence.CurrentMachineLogicId == sc.Roster[0] && (RaidDirectorService.StateOf(s).AwayDecisions?.Length ?? 0) == 0;
                Expect(pending && reverted,
                    $"N5 负向（复审 P2）：点“跳回家园”后 1.5 秒过渡中只在内存里记下选择、弹窗收起、存档里没有记录（{pending}）；过渡被 Esc 取消 → 选择撤销、弹窗回来、信号还在远征机器（{reverted}）");

                // N6 只有情报的突袭（计划已排定、还没预警）不弹。
                int state0 = p.State;
                p.State = RaidDirectorService.StateScheduled;
                IntelService.GrantFromDataCore(s); // 一份数据核心的情报：这波计划中的突袭进预警条的“情报行”（PlannedOnly）
                bool plannedOnly = RaidDirectorService.IncomingWaves(s, GameClock.Ticks).Any(w => w.Wave == p.Wave && w.PlannedOnly);
                bool intelHidden = !HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert ai) || !ai.Show;
                p.State = state0;
                // N7 目标是前哨站的突袭不弹；这时小窗还显示着（读数滞后一个刷新周期）按 H → 回退到普通的跳回家园，不吞掉按键、不记选择。
                string target0 = p.TargetKind;
                p.TargetKind = RaidDirectorService.TargetOutpost;
                bool outpostHidden = !HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert ao) || !ao.Show;
                int jumps0 = HomeRaidAlertService.JumpCount;
                bool panelStale = v.PanelVisible;
                bool keyOk = v.PressJumpKey();
                bool fallback = panelStale && keyOk && SignalUplinkService.IsJumpingHome && HomeRaidAlertService.JumpCount == jumps0 && !HomeRaidAlertService.JumpPending;
                SignalUplinkService.CancelByPlayer();
                p.TargetKind = target0;
                hud.Refresh(force: true);
                Expect(plannedOnly && intelHidden && outpostHidden && fallback && v.PopupVisible,
                    $"N6 / N7 判定分支：只有情报的突袭不弹（情报行 {plannedOnly}，{intelHidden}）；目标是前哨站的突袭不弹（{outpostHidden}）；小窗读数滞后时按 H 回退到普通的跳回家园、不记选择（{fallback}）");

                // N8 多波：第一波选过“留在远征队”后，新来的第二波照常弹出，倒计时写“共 2 波”。
                bool stayed = v.ClickStay();
                hud.Refresh(force: true);
                bool firstDone = stayed && !v.PopupVisible && HomeRaidAlertService.DecisionOf(s, p.Wave) == HomeRaidAlertService.ChoiceStay;
                RaidDirectorState d = RaidDirectorService.StateOf(s);
                RaidPlanRecord[] plans0 = d.Plans;
                var q = (RaidPlanRecord)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(p, null);
                q.Wave = p.Wave + 1;
                q.Serial = p.Serial + 1000;
                q.PlanId = p.PlanId + "-selfcheck";
                q.ArrivalTick = p.ArrivalTick + GameClock.StepHz * 60L;
                q.DepartTick = p.DepartTick + GameClock.StepHz * 60L;
                q.State = RaidDirectorService.StateWarned;
                string more = string.Empty;
                bool second;
                try
                {
                    d.Plans = plans0.Append(q).ToArray();
                    hud.Refresh(force: true);
                    HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert am);
                    more = v.CountdownText;
                    second = v.PopupVisible && am.Waves == 2 && am.PopupWave == q.Wave && am.Lead.Wave == p.Wave && more.Contains(GameText.Format("raid.away.more", 2))
                             && HomeRaidAlertService.DecisionOf(s, q.Wave) == HomeRaidAlertService.ChoiceNone;
                }
                finally
                {
                    d.Plans = plans0;
                }
                Expect(firstDone && second, $"N8 多波：第一波选过“留在远征队”（{firstDone}）后新来的第二波照常弹出、倒计时写“{more}”（{second}）");

                // 过渡完成才写进存档（时刻 / 地点按发起时）。
                hud.Refresh(force: true);
                long clickTick = GameClock.Ticks;
                d.AwayDecisions = Array.Empty<RaidAwayDecisionRecord>();
                hud.Refresh(force: true);
                bool clicked2 = v.ClickJump();
                int guard = 0;
                while (SignalUplinkService.IsJumpingHome && guard++ < 240)
                {
                    Frames(1);
                }
                hud.Refresh(force: true);
                RaidAwayDecisionRecord rec = RaidDirectorService.StateOf(s).AwayDecisions?.FirstOrDefault(x => x != null && x.Wave == p.Wave);
                bool committed = clicked2 && !HomeRaidAlertService.JumpPending && rec != null && rec.Choice == HomeRaidAlertService.ChoiceJumpHome && rec.Tick == clickTick && rec.SiteId == sc.Site
                                 && SignalPresence.AtCore;
                Expect(committed, $"N9 跳回家园过渡完成后才写进存档：选择、时刻（第 {rec?.Tick} 步 = 点击时第 {clickTick} 步）、地点（{rec?.SiteId}）按发起时记（{committed}）");
            }
            finally
            {
                if (SignalUplinkService.IsJumpingHome)
                {
                    SignalUplinkService.CancelByPlayer();
                }
                Unmount(go);
            }
        }

        /// <summary>复审 P2：没有计划的突袭队伍（波次 0，调试 / 旧档）结束后清掉波次 0 的选择，下一支无计划的队伍照常弹出。</summary>
        private static void CheckUnplannedWave()
        {
            GameObject go = null;
            try
            {
                CampaignState s = NewWorld(7181);
                HomeRaidAlertService.AwayOverrideForTests = () => true;
                TransitGroupRecord g = Incoming(s, 4, 4);
                if (!StepUntil(() => g.State == TransitGroupState.Arrived, 90))
                {
                    Fail($"N10 准备：没有计划的突袭队伍没有到达（{g.State}）");
                    return;
                }
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;
                HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert a);
                bool popup = v.PopupVisible && a.Unplanned && a.PopupWave == 0;
                bool stayed = v.ClickStay();
                hud.Refresh(force: true);
                bool chosen = stayed && !v.PopupVisible && HomeRaidAlertService.DecisionOf(s, 0) == HomeRaidAlertService.ChoiceStay;
                TransitGroupState st0 = g.State;
                g.State = TransitGroupState.Retreating; // 测试捷径：这支队伍撤走了（结束）
                hud.Refresh(force: true);
                bool cleared = HomeRaidAlertService.DecisionOf(s, 0) == HomeRaidAlertService.ChoiceNone && !v.PanelVisible;
                g.State = st0; // 又来一支没有计划的队伍（同一夹具）
                hud.Refresh(force: true);
                bool again = v.PopupVisible;
                Expect(popup && chosen && cleared && again,
                    $"N10 没有计划的突袭队伍（波次 0）：弹出（{popup}）→ 留在远征队（{chosen}）→ 队伍结束后清掉这条选择（{cleared}）→ 下一支无计划的队伍照常弹出（{again}）");
            }
            finally
            {
                HomeRaidAlertService.AwayOverrideForTests = null;
                Unmount(go);
            }
        }

        private static string CoreFloorRun(bool observe, out long floorTick, out bool coreHit)
        {
            CampaignState s = NewWorld(7131, observe);
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            core.Health = 40f; // 测试捷径：核心只剩 40 耐久（让一小支突击部队在测试时长内打空它）
            Incoming(s, 2, 10);
            floorTick = -1;
            long end = GameClock.Ticks + GameClock.StepHz * 240L;
            int quarter = Math.Max(1, GameClock.StepHz / 4);
            while (GameClock.Ticks < end)
            {
                long next = Math.Min(end, GameClock.Ticks + quarter);
                if (observe)
                {
                    FramesTo(next);
                }
                else
                {
                    HeadlessTo(next);
                }
                if (floorTick < 0 && RepairDroneService.DurabilityOf(s, core) <= CombatSiegeConst.HealthFloor + 1e-4f)
                {
                    floorTick = GameClock.Ticks;
                    end = Math.Min(end, GameClock.Ticks + GameClock.StepHz * 3L);
                }
            }
            coreHit = RepairDroneService.DurabilityOf(s, core) < 40f - 0.01f;
            return HomeSignature(s) + "|F" + floorTick;
        }

        private static void CheckCoreFloorUnobserved()
        {
            string seen = CoreFloorRun(true, out long tSeen, out bool hitSeen);
            string unseen = CoreFloorRun(false, out long tUnseen, out bool hitUnseen);
            bool same = seen == unseen && tSeen > 0;
            Expect(same && hitSeen && hitUnseen && !WorldView.IsObserved(HomeValleyLayout.RegionId),
                $"N3 负向“家园在无人观察时被攻破”：同一支突击部队把核心打空——无人观察（无头）与观察（帧驱动）在同一步打到耐久下限（第 {tSeen} / {tUnseen} 步），家园结果逐位一致" +
                (same ? string.Empty : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
            // 远征 HUD 在核心耐久耗尽时写明；按失败规则（归还核心被摧毁 → 战役失败页，不看镜头在哪）。
            CampaignState s = CampaignSession.Current;
            GameObject go = null;
            try
            {
                HomeRaidAlertService.AwayOverrideForTests = () => true;
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;
                // FG6-DEF-08（DEBT-FG6DEF07-01 关闭）：攻城真实把核心打到耐久下限 = 归还核心被摧毁（攻城对账 RaidResultService.CheckCore），不再停在“耐久耗尽”——
                // 这里不再调调试入口，直接断言上面那次真实攻城（远征中、无人观察）已经触发失败规则。
                bool floorLine = HomeValleySoftlockGuard.IsCoreDestroyed(s) && RaidResultService.IsCoreLost(s) && v.PanelVisible
                                 && v.CoreText == GameText.Get("raid.away.core_lost");
                hud.Refresh(force: true);
                HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert a);
                bool lost = a.CoreLost && !a.Popup && v.CoreText == GameText.Get("raid.away.core_lost") && !v.JumpButton.enabledSelf && !v.PopupVisible
                            && WorldSimulation.Home.IsLoaded && HomeValleySoftlockGuard.IsCoreDestroyed(s) && !WorldView.IsObserved(HomeValleyLayout.RegionId);
                // 快捷键 H 与禁用的按钮一致（复审 P2）：被拒、写明原因、不跳。
                int jumps0 = HomeRaidAlertService.JumpCount;
                int homeJumps0 = SignalUplinkService.JumpHomeCount;
                bool keyRejected = !v.PressJumpKey() && HomeRaidAlertService.JumpCount == jumps0 && SignalUplinkService.JumpHomeCount == homeJumps0 && !SignalUplinkService.IsJumpingHome
                                   && HomeRaidAlertService.LastMessage == GameText.Format("raid.away.err_jump", GameText.Get("raid.away.core_lost"));
                lost &= keyRejected;
                Expect(floorLine && lost,
                    $"N4 按失败规则：攻城真实打空核心 → 归还核心被摧毁（{floorLine}，远征小窗写“{v.CoreText}”）；" +
                    $"核心被摧毁（失败规则入口）后不再弹选择、跳回按钮禁用、快捷键 H 同样被拒（{keyRejected}）、写“{v.CoreText}”，家园仍在运行且失败判定成立（失败页的显示条件不看镜头在哪，FG0-ARCH-01；" +
                    "失败页本身与“读取最近自动存档”由 FgRaidResultSelfCheck F 段断言）");
            }
            finally
            {
                HomeRaidAlertService.AwayOverrideForTests = null;
                Unmount(go);
            }
        }

        // ── S 存读档 ─────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            GameObject go = null;
            try
            {
                Scene sc = Prepare(7141);
                if (sc.Failure != null)
                {
                    Fail("S 准备：" + sc.Failure);
                    return;
                }
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                hud.Away.ClickStay();
                int wave = sc.Plan.Wave;
                long tick = RaidDirectorService.StateOf(sc.S).AwayDecisions.Last().Tick;
                WorldSimulation.SyncAllForSave();
                bool saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual).Success;
                Unmount(go);
                go = null;
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                SignalUplinkService.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                ResearchService.ResetForTests();
                IntelService.ResetSessionState();
                SiegeService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
                if (!r.Success)
                {
                    Fail("S1 读档失败：" + r.Message);
                    return;
                }
                CampaignSession.Set(Slot, r.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                CampaignState s2 = r.State;
                RaidAwayDecisionRecord[] list = RaidDirectorService.StateOf(s2).AwayDecisions;
                HomeRaidAlertService.AwayOverrideForTests = () => true;
                HomeRaidAlertService.Evaluate(s2, GameClock.Ticks, out HomeRaidAlert a);
                bool persisted = saved && list.Length == 1 && list[0].Wave == wave && list[0].Choice == HomeRaidAlertService.ChoiceStay && list[0].Tick == tick && list[0].SiteId == sc.Site
                                 && a.Show && !a.Popup;
                // 旧档（没有这个字段）：补成空数组，同一波照常弹出。
                RaidDirectorService.StateOf(s2).AwayDecisions = null;
                RaidDirectorService.EnsureState(s2);
                HomeRaidAlertService.Evaluate(s2, GameClock.Ticks, out HomeRaidAlert a2);
                bool legacy = RaidDirectorService.StateOf(s2).AwayDecisions != null && RaidDirectorService.StateOf(s2).AwayDecisions.Length == 0 && a2.Popup && a2.PopupWave == wave;
                Expect(persisted && legacy,
                    $"S1 存读档：选择“留在远征队”后真文件存档 → 读档，这一波的选择（波次 {wave}、留在远征队、第 {tick} 步、地点 {sc.Site}）还在，读回来不再重复弹出（{persisted}）；旧档没有这个字段 → 补成空、照常弹出（{legacy}）");
            }
            finally
            {
                HomeRaidAlertService.AwayOverrideForTests = null;
                Unmount(go);
            }
        }

        // ── T 暂停与 0.5x～3x ────────────────────────────────────────────────────────

        private static void CheckPauseAndSpeed()
        {
            GameObject go = null;
            try
            {
                Scene sc = Prepare(7151);
                if (sc.Failure != null)
                {
                    Fail("T 准备：" + sc.Failure);
                    return;
                }
                CampaignState s = sc.S;
                RaidWarningHudUIToolkit hud = MountHud(out go);
                hud.Refresh(force: true);
                HomeRaidAlertView v = hud.Away;
                GameClock.SetPaused(true);
                string c0 = v.CountdownText;
                long t0 = GameClock.Ticks;
                Frames(60);
                hud.Refresh(force: true);
                bool paused = GameClock.Ticks == t0 && v.CountdownText == c0 && v.PopupVisible;
                GameClock.SetPaused(false);
                // 接入机器时统一时钟锁 1x（FG0-ARCH-01 既有规则）：信号在远征机器里时点 3x 也按 1x 走，倒计时同样按游戏时间。
                GameClock.SetSpeed(3f);
                long l0 = GameClock.Ticks;
                Frames(30);
                long lockTicks = GameClock.Ticks - l0;
                bool locked = Math.Abs(lockTicks - (long)Math.Round(GameClock.StepHz * 30 * FrameDt)) <= 2;
                GameClock.SetSpeed(1f);
                // 退出接入、镜头留在远征地点（战略视角）：仍是“远征中”，0.5x～3x 都能用。
                RegionControlSystem.ForRegion(sc.Site)?.ReleaseToStrategy(RegionControlChangeReason.PlayerRequest);
                Frames(30);
                bool stillAway = HomeRaidAlertService.IsAway(s) && SignalPresence.AtCore;
                var lines = new List<string> { $"接入中点 3x 锁 1x：0.5 真实秒走 {lockTicks} 步{(locked ? string.Empty : "✗")}", $"退出接入后仍在远征地点（{stillAway}）" };
                bool speeds = locked && stillAway;
                foreach (float sp in new[] { 0.5f, 1f, 2f, 3f })
                {
                    GameClock.SetSpeed(sp);
                    long a0 = GameClock.Ticks;
                    long arrival = sc.Plan.ArrivalTick;
                    Frames(30);
                    long dTicks = GameClock.Ticks - a0;
                    hud.Refresh(force: true);
                    HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert a);
                    long expected = (long)Math.Round(sp * GameClock.StepHz * 30 * FrameDt);
                    bool ok = Math.Abs(dTicks - expected) <= 2 && v.CountdownText == HomeRaidAlertService.CountdownText(s, a, GameClock.Ticks)
                              && a.Lead.ArrivalTick - GameClock.Ticks == arrival - a0 - dTicks;
                    speeds &= ok;
                    lines.Add($"{sp}x：0.5 真实秒走 {dTicks} 步（应 {expected}）{(ok ? string.Empty : "✗")}");
                }
                GameClock.SetSpeed(1f);
                // 重新接入远征机器（远距离跳转冷却过了再接），再在暂停中点“跳回家园”：选择立即记下，过渡等继续后才走（世界不动、信号不动）；继续后完成。
                int cd = 0;
                while (SignalUplinkService.JumpCooldownRemaining(s) > 0 && cd++ < 60 * 30)
                {
                    WorldSimulation.StepMany(1);
                }
                bool reup = Uplink(sc.Roster[0], out string reWhy);
                hud.Refresh(force: true);
                lines.Add(reup ? "重新接入远征机器" : "重新接入失败：" + reWhy);
                GameClock.SetPaused(true);
                bool clicked = v.ClickJump();
                float rem0 = SignalUplinkService.JumpHomeRemaining;
                Frames(60);
                bool held = clicked && SignalUplinkService.IsJumpingHome && Math.Abs(SignalUplinkService.JumpHomeRemaining - rem0) < 1e-6f && SignalPresence.CurrentMachineLogicId == sc.Roster[0]
                            && HomeRaidAlertService.DecisionOf(s, sc.Plan.Wave) == HomeRaidAlertService.ChoiceJumpHome;
                GameClock.SetPaused(false);
                int guard = 0;
                while (SignalUplinkService.IsJumpingHome && guard++ < 240)
                {
                    Frames(1);
                }
                bool done = SignalPresence.AtCore && WorldView.IsObserved(HomeValleyLayout.RegionId);
                Expect(paused && speeds && reup && held && done,
                    $"T1 暂停与倍速矩阵：暂停 60 帧倒计时与世界都不动（“{c0}”）；0.5x / 1x / 2x / 3x 下倒计时按游戏时间走（{string.Join("；", lines)}）；" +
                    $"暂停中点“跳回家园”记下选择、过渡挂起（{held}），继续后完成（{done}）");
            }
            finally
            {
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                Unmount(go);
            }
        }

        // ── P 性能（只测一次，超线不到 2 倍记警告）──────────────────────────────────

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
            return r;
        }

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7161);
            Vector2 core = CoreCenter(s);
            int placed = 0;
            for (int ring = 0; ring < 12 && placed < 120; ring++)
            {
                for (int k = 0; k < 24 && placed < 120; k++)
                {
                    float ang = (k * 15f + ring * 7f) * Mathf.Deg2Rad;
                    var c = new GridCell(Mathf.RoundToInt(core.x + Mathf.Cos(ang) * (10f + ring * 5f)), Mathf.RoundToInt(core.y + Mathf.Sin(ang) * (10f + ring * 5f)));
                    if (HomeGridService.ValidatePlacement(s, L, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        Register(s, L, c);
                        HomeGridService.MapFor(s);
                        placed++;
                    }
                }
            }
            TurretService.Sync(s);
            // 测试夹具：再登记一批“其它”建筑记录（不进格网，只撑大汇总要扫的建筑数到后期规模的量级）。
            for (int i = 0; i < 1500; i++)
            {
                Register(s, HomeValleyLayout.BuildingTypeWarehouse, new GridCell(-4000 - i, -4000));
            }
            TransitGroupRecord g = Incoming(s, 3, 200);
            StepUntil(() => g.Engaged, 90);
            var st = new HomeRaidStatus();
            HomeRaidAlertService.CollectHome(s, st); // 预热（JIT）
            const int Runs = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Runs; i++)
            {
                HomeRaidAlertService.CollectHome(s, st);
            }
            sw.Stop();
            double perCollect = sw.Elapsed.TotalMilliseconds / Runs;
            HomeRaidAlertService.AwayOverrideForTests = () => true;
            HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out HomeRaidAlert a);
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < Runs; i++)
            {
                HomeRaidAlertService.Evaluate(s, GameClock.Ticks, out a);
                HomeRaidAlertService.CountdownText(s, a, GameClock.Ticks);
            }
            sw2.Stop();
            HomeRaidAlertService.AwayOverrideForTests = null;
            double perEval = sw2.Elapsed.TotalMilliseconds / Runs;
            double budget = HomeRaidAlertService.PerfBudgetMs;
            PerfLines.Add($"远征小窗汇总一次（扫 {st.Scanned} 座建筑，其中 {placed} 座炮塔；剩余敌人 {st.EnemiesAlive} 台由内核计数）平均 {perCollect:F4} ms（阈值 {budget} ms，每 0.5 真实秒最多一次）；" +
                          $"弹窗判定 + 倒计时文字 {perEval:F4} ms（每 0.25 真实秒一次）；Editor batchmode 影子工程，{SystemInfo.processorType.Trim()}，{SystemInfo.processorCount} 线程；真机由 FG15-SYS-02 补测");
            PerfGate.Expect(placed >= 60 && st.Scanned >= 1500 && st.EnemiesArrived && a.Show,
                $"P1 性能：{st.Scanned} 座建筑 + 200 台攻城单位时远征小窗汇总 {perCollect:F4} ms、判定与倒计时 {perEval:F4} ms（阈值 {budget} ms，2 倍以内算通过）",
                new[] { PerfGate.Le(perCollect, budget, "远征小窗汇总 ms"), PerfGate.Le(perEval, budget, "远征弹窗判定 ms") }, Expect, Line);
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
