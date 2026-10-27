using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using GameLogic.Campaign.Env;
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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
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
    /// FG8-EXP-02（FG08 FGR-EXP-010 行军 / 011 远程指挥与覆盖 / 012 远征队状态；FG01 FGR-SIG-053；验收 FGT-EXP-002、012）自检。
    /// 全部走正式入口与真实系统：出发用 <see cref="ExpeditionDepartureService.TryDepart"/>，行军由家园战斗内核真实推进（层级寻路、真实交战），
    /// 命令用 <see cref="ExpeditionMarchService.IssueCommand"/> / 世界栏按钮，覆盖用真实覆盖网（中继塔记录），存读档走真文件。
    /// - M1 数据：文本键中英两列、调参初值（6 / 5 / 3.5 格/秒）、提示与通知类型；
    /// - M2 锚点（第 0 批，B25）：4 个种子各自确定、在领地内、可通行、从核心走得到（层级寻路），同种子可复现、不同种子不同；
    /// - M3 出发：行军开始（不载入地点、不抢镜头、速度取最慢、内核命令指向锚点），远征在外的各处判据、名册“远征中”、不接家园工作；
    /// - M4 出发事务失败整体回滚；M5 真实行军到达（用时 ≈ 路线 / 速度、整队同行、到达通知不抢镜头、进入地点）；
    /// - M6 观察 / 不观察 / 0.5x～3x / 暂停结果一致（含途中交战，FGT-EXP-012）；M7 行军中存读档：存档后不读档接着走（内存里那份）与读档接着走，到达的步与到达后状态一致（FGT-EXP-011 行军部分）；
    /// - M8 覆盖：走出覆盖前提醒、覆盖外只执行最后命令、不能接入、铺中继回到覆盖后控制恢复、返回家园结算（FGT-EXP-002）；
    /// - M9 途中静默夜；M10 途中家园遇袭（不自动召回，玩家可召回；镜头随队走远 = 玩家在远征，DEBT-FG6DEF07-06；最慢成员阵亡后提速）；M11 地形阻挡绕行与路线截断；M11b 覆盖外截断按最后的“前进”自动重试（FG-GAP-136）（停下、说明、打通后再前进能到）；
    /// - M12 到达失败（真实行军走到集结点：地点不开放 / 载入中途出错整体回滚，队伍照最后的“前进”在集结点等候、到点自动重试进入；集结点在覆盖外时玩家命令被拒）；
    /// - M15 途中遇敌（据点 / 巡逻 / 突袭各一次、读档后不重复）与危害带提示（进入一次、离开复位、再进再提示）；M13 界面（世界栏行军条、战略地图、名册、跟随镜头、布局探针）；M14 旧档兼容。
    /// 测试捷径（登记在证据里）：区域解锁 / 重新锁定直接改区域状态、覆盖用中继塔记录夹具、截断用悬崖地形夹具、M12 用故障注入点、M15 的据点（SpawnOutpost）与临时危害带（规划层 HazardWidth）夹具。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgExpeditionMarchSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionMarchSelfCheck.RunFrom
    /// </summary>
    public static class FgExpeditionMarchSelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const int KeepSlot = 9;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        private static readonly string[] TextKeys =
        {
            "exp.check.hard.destination_pending", "exp.check.hard.destination_unreachable", "exp.check.hard.home_not_loaded",
            "exp.state.standby", "exp.state.marching", "exp.state.engaged", "exp.state.returning", "exp.state.returned",
            "exp.cue.tag", "exp.cue.out_of_coverage", "exp.cue.back_in_coverage", "exp.cue.edge", "exp.cue.edge_detail", "exp.cue.halted",
            "exp.cue.halted_detail", "exp.cue.arrival_failed", "exp.cue.returned", "exp.cue.returned_detail", "exp.cue.arrived", "exp.cue.encounter",
            "exp.cue.encounter.outpost", "exp.cue.encounter.patrol", "exp.cue.encounter.raid", "exp.cue.hazard", "exp.cue.hazard_detail",
            "exp.cue.wiped_on_march", "exp.cmd.advance", "exp.cmd.hold", "exp.cmd.return", "exp.cmd.follow", "exp.cmd.goto_site",
            "exp.cmd.tip.advance", "exp.cmd.tip.hold", "exp.cmd.tip.return", "exp.cmd.tip.coverage", "exp.march.bar", "exp.march.bar_eta",
            "exp.march.bar_rally", "exp.march.bar_site", "exp.march.bar_none", "exp.march.halted.route", "exp.march.halted.arrival",
            "exp.march.last_order", "exp.follow.started", "exp.follow.stopped", "exp.follow.none", "exp.focus.march", "exp.map.label",
            "exp.map.eta", "exp.map.eta_planning", "exp.map.eta_hold", "exp.map.destination", "exp.roster.place",
        };

        private static readonly FeedbackCueId[] MarchCues =
        {
            FeedbackCueId.ExpeditionMarchOutOfCoverage, FeedbackCueId.ExpeditionMarchBackInCoverage, FeedbackCueId.ExpeditionMarchEdge,
            FeedbackCueId.ExpeditionMarchHalted, FeedbackCueId.ExpeditionMarchArrivalFailed, FeedbackCueId.ExpeditionMarchReturned,
            FeedbackCueId.ExpeditionMarchArrived, FeedbackCueId.ExpeditionMarchEncounter, FeedbackCueId.ExpeditionMarchHazard,
        };

        /// <summary>单段入口（批处理）：bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionMarchSelfCheck.RunFrom</summary>
        public static void RunFrom()
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
            Line("\n[远征行军] 锚点（种子 / 可达）、出发事务与回滚、真实行军到达、观察 / 倍速 / 暂停一致、存读档接着走、覆盖内外命令与接入、途中静默夜 / 家园遇袭、地形绕行与截断、到达失败回滚、界面（FG8-EXP-02）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgmarch-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckAnchors);
                Step(CheckDeparture);
                Step(CheckDepartureRollback);
                Step(CheckMarchToArrival);
                Step(CheckObservationAndSpeed);
                Step(CheckSaveLoadContinue);
                Step(CheckCoverageCommands);
                Step(CheckSilentNight);
                Step(CheckHomeRaid);
                Step(CheckObstacleAndTruncation);
                Step(CheckTruncationOutOfCoverage);
                Step(CheckArrivalFailure);
                Step(CheckEncountersAndHazard);
                Step(CheckSelfDefense);
                Step(CheckUi);
                Step(CheckLegacySave);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                ExpeditionMarchService.ArrivalFaultInjectionForTests = null;
                ExpeditionMarchService.BeginFaultInjectionForTests = null;
                ExpeditionChecklistService.RouteProvider = ExpeditionChecklistService.DefaultRoute;
                CameraDirector.RealDeltaTime = originalDelta;
                SignalCoverageService.OverrideForTests = null;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
            }
            Line($"  通过 {_pass} 项，失败 {_fail} 项");
            return _fail;
        }

        // ── M1 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            var missing = new List<string>();
            foreach (string k in TextKeys.Concat(ExpeditionTextKeys.ReasonCodes.Select(ExpeditionTextKeys.ReasonKey)))
            {
                if (!GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh))
                {
                    missing.Add(k + "(中)");
                }
                if (!GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    missing.Add(k + "(英)");
                }
            }
            bool probe = !GameText.TryGet("exp.__march_missing_probe__", GameLanguage.ZhCn, out _);
            Expect(missing.Count == 0 && probe, $"M1 文本键 {TextKeys.Length} 个 + 原因 {ExpeditionTextKeys.ReasonCodes.Length} 个中英两列都在（缺：{(missing.Count == 0 ? "无" : string.Join("、", missing))}）；反证不存在的键查不到（{probe}）");

            bool w = GridContent.TryGetTuning("expedition.march.speed.wheel", out float wheel) && Mathf.Approximately(wheel, 6f);
            bool h = GridContent.TryGetTuning("expedition.march.speed.hover", out float hover) && Mathf.Approximately(hover, 5f);
            bool t = GridContent.TryGetTuning("expedition.march.speed.track", out float track) && Mathf.Approximately(track, 3.5f);
            bool mapped = Mathf.Approximately(ExpeditionMarchService.ChassisSpeed(HomeValleyLayout.Erc001ChassisId), 6f)
                          && Mathf.Approximately(ExpeditionMarchService.ChassisSpeed(HomeValleyLayout.Erc002ChassisId), 6f)
                          && Mathf.Approximately(ExpeditionMarchService.ChassisSpeed(HomeValleyLayout.Erc003ChassisId), 3.5f)
                          && Mathf.Approximately(ExpeditionMarchService.ChassisSpeed(ChassisCatalog.ChassisHoverId), 5f)
                          && Mathf.Approximately(ExpeditionMarchService.ChassisSpeed(HomeValleyLayout.ErcRescueChassisId), 3.5f);
            bool otherTuning = new[] { "expedition.march.arrive_radius", "expedition.march.home_radius", "expedition.march.engage_radius", "expedition.encounter.radius_cells",
                "expedition.march.arrival_retry_seconds", "raid.away.march_focus_cells",
                "expedition.anchor.max_attempts", "expedition.anchor.jitter_deg", "expedition.anchor.radial_min", "expedition.anchor.radial_max" }.All(k => GridContent.TryGetTuning(k, out float v) && v > 0f);
            Expect(w && h && t && mapped && otherTuning,
                $"M1 行军速度入 Luban 表（FG08 初值：搬运轮式 {wheel} / 维修悬浮 {hover} / 战斗履带 {track} 格/秒）；ERC-001/002 → 轮式、ERC-003 → 履带、应急机按最慢（{mapped}）；其余行军 / 锚点调参都在（{otherTuning}）");
            // 审查 P2（B12）：到达半径随编队放大——三台一排、行距 2.5 格，第 13 台站位离锚点约 10.3 格，超过表里的 10 格。
            float r2 = ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseOutbound, 2);
            float r13 = ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseOutbound, 13);
            float r30 = ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseOutbound, 30);
            float farthest13 = new Vector2(2.5f, 10f).magnitude; // 第 13 台（下标 12）的站位 (1×2.5, −4×2.5)
            bool radius = Mathf.Approximately(r2, ExpeditionMarchService.ArriveRadius) && r13 >= farthest13 + 1.5f && r30 > r13
                          && ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseReturning, 2) >= ExpeditionMarchService.HomeArriveRadius;
            Expect(radius, $"M1 到达判定半径随编队放大（B12）：2 台 {r2:0.0} 格（= 表里的 {ExpeditionMarchService.ArriveRadius:0}）、13 台 {r13:0.0} 格（第 13 台站位离锚点 {farthest13:0.0} 格 + 单位到达误差）、30 台 {r30:0.0} 格——人多也不会永远“到不了”被误判截断");

            var bad = new List<string>();
            foreach (FeedbackCueId cue in MarchCues)
            {
                FeedbackCueDef def = FeedbackCueCatalog.Get(cue);
                if (def == null || string.IsNullOrEmpty(def.SfxId) || def.ResolvedCaption != GameText.Get(def.CaptionKey) || def.ResolvedTag != GameText.Get("exp.cue.tag"))
                {
                    bad.Add(cue.ToString());
                }
            }
            NotifyTypeDef arrived = NotificationCatalog.TypeForCue(FeedbackCueId.ExpeditionMarchArrived);
            NotifyTypeDef halted = NotificationCatalog.TypeForCue(FeedbackCueId.ExpeditionMarchHalted);
            NotifyTypeDef encounter = NotificationCatalog.TypeForCue(FeedbackCueId.ExpeditionMarchEncounter);
            NotifyTypeDef edge = NotificationCatalog.TypeForCue(FeedbackCueId.ExpeditionMarchEdge);
            Expect(bad.Count == 0 && arrived?.Id == "expedition_march" && halted?.Id == "expedition_alert" && encounter?.Id == "expedition_alert" && edge?.Id == "expedition_alert",
                $"M1 行军提示 {MarchCues.Length} 个：有音效、标签 / 正文走文本键（坏：{(bad.Count == 0 ? "无" : string.Join("、", bad))}）；到达 → 远征行军通知、截断 / 遇敌 / 快出覆盖 → 远征警报通知");
        }

        // ── M2 锚点（第 0 批）──────────────────────────────────────────────────────

        private static void CheckAnchors()
        {
            var lines = new List<string>();
            var anchors = new Dictionary<int, (int x, int y, float len)>();
            bool all = true;
            double maxMs = 0;
            foreach (int seed in new[] { 17, 2929, 404040, 42 })
            {
                CampaignState s = NewWorld(seed);
                var sw = Stopwatch.StartNew();
                bool resolved = ExpeditionSiteAnchors.EnsureResolved(s);
                sw.Stop();
                maxMs = Math.Max(maxMs, sw.Elapsed.TotalMilliseconds);
                GridCell core = HomeGridService.CorePivot(s);
                foreach (string site in ExpeditionSiteAnchors.SiteIds)
                {
                    ExpeditionSiteAnchorRecord r = ExpeditionSiteAnchors.Find(s, site);
                    PlannedTerritory t = WorldGenQuery.Plan(s)?.Find(ExpeditionSiteAnchors.TerritoryIdOf(site));
                    bool ok = resolved && r != null && r.State == ExpeditionSiteAnchors.StateResolved && t != null && r.TerritoryId == t.Id;
                    float toCenter = r == null || t == null ? float.MaxValue : Vector2.Distance(new Vector2(r.X, r.Y), new Vector2(t.CenterX, t.CenterY));
                    bool inside = toCenter <= t?.Radius + 0.5f && r != null && !r.Fallback;
                    bool passable = r != null && NavService.Kernel != null && NavService.Kernel.Passable(r.X, r.Y, NavConst.ClassPlayer);
                    List<Vector2> route = ExpeditionSiteAnchors.Route(s, site);
                    bool routeOk = route != null && route.Count >= 2 && Vector2.Distance(route[route.Count - 1], new Vector2(r.X, r.Y)) < 0.5f
                                   && Vector2.Distance(route[0], new Vector2(core.X, core.Y)) <= 8f;
                    bool clear = routeOk && NavService.RouteClear(new int2((int)route[0].x, (int)route[0].y),
                        route.Skip(1).Select(p => new int2((int)p.x, (int)p.y)).ToList(), 0, NavConst.ClassPlayer);
                    float straight = Vector2.Distance(new Vector2(core.X, core.Y), new Vector2(r?.X ?? 0, r?.Y ?? 0));
                    bool lenOk = r != null && r.RouteLength >= straight - 2f;
                    bool towardCore = t != null && r != null && Vector2.Distance(new Vector2(core.X, core.Y), new Vector2(r.X, r.Y)) < t.Distance;
                    bool one = ok && inside && passable && routeOk && clear && lenOk && towardCore;
                    all &= one;
                    if (site == FracturedCityLayout.RegionId)
                    {
                        anchors[seed] = (r?.X ?? 0, r?.Y ?? 0, r?.RouteLength ?? 0f);
                    }
                    lines.Add($"种子 {seed} {site}：({r?.X},{r?.Y}) 距领地中心 {toCenter:0}/{t?.Radius} 格、路线 {route?.Count ?? 0} 点 {r?.RouteLength:0} 格（直线 {straight:0}）、试探 {r?.Attempt + 1} 次{(r != null && r.Moved ? "、已挪位" : string.Empty)}{(one ? string.Empty : "【不合格】")}");
                }
            }
            // 同一种子再开一局：锚点逐格相同（只由种子 + 规划层决定）。
            CampaignState again = NewWorld(17);
            ExpeditionSiteAnchors.EnsureResolved(again);
            ExpeditionSiteAnchorRecord ra = ExpeditionSiteAnchors.Find(again, FracturedCityLayout.RegionId);
            bool repro = ra != null && anchors.TryGetValue(17, out var first) && first.x == ra.X && first.y == ra.Y && Mathf.Approximately(first.len, ra.RouteLength);
            bool varied = anchors.Values.Select(a => (a.x, a.y)).Distinct().Count() >= 3;
            Expect(all && repro && varied,
                $"M2 锚点（B25，FGR-GEN-024 放置规则的最小部分）：4 个种子 × 2 个地点都在所属领地内（朝核心一侧）、可通行、从归还核心有畅通的层级寻路路线（路线不短于直线）；同种子再开一局完全相同（{repro}）、不同种子不同（{varied}）。{string.Join("；", lines)}");
            PerfLines.Add($"锚点解析（两个地点，后台寻路通道同步取回）每局最长 {maxMs:F1} ms（Editor 批处理，含冷区块生成；只在第一次打开派遣检查单 / 出发时发生一次）");
            ExpectPerf(all, $"M2 锚点解析一次性开销 ≤ 300 ms（实测最长 {maxMs:F1} ms）", PerfGate.Le(maxMs, 300.0, "锚点解析 ms"));
        }

        // ── M3 出发 ───────────────────────────────────────────────────────────────

        private static void CheckDeparture()
        {
            CampaignState s = NewWorld(5101);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(6f, -6f));
            int c = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(8f, -6f));
            WorldSimulation.StepMany(3);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            int countBefore = ruins.ExpeditionCount;
            string observedBefore = WorldView.ObservedSiteId;
            float2 focusBefore = WorldView.Director.StrategyFocus;
            int flightsBefore = WorldView.Director.FlightCount, flyBefore = WorldView.FlyCount, switchBefore = WorldView.ObserveSwitchCount;
            int departCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionDepart);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            CampaignState now = CampaignSession.Current;
            ExpeditionMarchState m = ExpeditionMarchService.Current(now);
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool phase = dep.Success && m.Phase == ExpeditionMarchService.PhaseOutbound && m.SiteId == FracturedCityLayout.RegionId && m.Members.SequenceEqual(new[] { a, b }.OrderBy(x => x));
            bool noSite = WorldSimulation.ActiveExpedition == null && Rec(a).RegionId == HomeValleyLayout.RegionId && Rec(b).RegionId == HomeValleyLayout.RegionId;
            // 不抢镜头：注视点不动，也没有任何飞行 / 切换关注地点 / 自动跟随（2026-10-17 冒烟误报的复核：真实光标边缘推屏不算出发动镜头）。
            bool camera = WorldView.ObservedSiteId == observedBefore && math.distance(WorldView.Director.StrategyFocus, focusBefore) < 0.5f
                          && WorldView.Director.FlightCount == flightsBefore && WorldView.FlyCount == flyBefore && WorldView.ObserveSwitchCount == switchBefore
                          && !WorldView.Director.IsFollowing;
            bool speed = Mathf.Approximately(m.Speed, 3.5f) && Mathf.Approximately(home.UnitSpeed(Unit(home, a)), 3.5f) && Mathf.Approximately(home.UnitSpeed(Unit(home, b)), 3.5f)
                         && Mathf.Approximately(home.UnitSpeed(Unit(home, c)), HomeValleyMachineMarker.MoveSpeed);
            bool commands = CommandTowards(home, a, anchor, 6f) && CommandTowards(home, b, anchor, 6f);
            Expect(phase && noSite && camera && speed && commands && FeedbackCues.CountOf(FeedbackCueId.ExpeditionDepart) == departCues + 1,
                $"M3 出发 = 开始行军（FGR-EXP-010）：成员留在星球上、没有载入远征地点（{noSite}）、镜头不动（{camera}）；行军速度取最慢的那台：轮式 + 履带 → {m.Speed} 格/秒，两台的内核速度都是它、队外机器不变（{speed}）；两台的内核命令都指向目的地锚点 ({anchor.x:0},{anchor.y:0})（{commands}）；出发提示一次");

            WorldSimulation.StepMany(2);
            bool away = ExpeditionMarchService.IsAway(now) && SignalCoreService.ExpeditionUnderway && !SignalCoreService.CanEdit(now, out _);
            bool roster = MachineRoster.EffectiveRole(now, Rec(a)) == MachineRole.OnExpedition && !MachineRoster.IsHome(Rec(a)) && MachineRoster.IsHome(Rec(c))
                          && MachineRoster.PlaceText(now, Rec(a)) == GameText.Format("exp.roster.place", GameText.Get("exp.state.marching"));
            bool noWork = HomeValleyWorkOrders.TakenByPlayer != null && HomeValleyWorkOrders.TakenByPlayer(a) && HomeValleyWorkOrders.TakenByPlayer(b) && !HomeValleyWorkOrders.TakenByPlayer(c);
            bool counted = FracturedCityRegion.Find(now).ExpeditionCount == countBefore + 1;
            bool report = now.Stats?.AwayReports != null && now.Stats.AwayReports.HasOpen && now.Stats.AwayReports.Open.RegionId == FracturedCityLayout.RegionId;
            ExpeditionDepartureService.DepartureResult second = ExpeditionDepartureService.TryDepart(new[] { c }, true);
            bool blocked = second.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && second.Reasons.Contains("expedition-underway")
                           && ExpeditionDepartureService.BuildPrepSnapshot(now).BlockedReason == "expedition-underway";
            Expect(away && roster && noWork && counted && report && blocked,
                $"M3 远征在外各处同一判据（行军阶段地点还没载入）：信号核锁定（{away}）、名册“{MachineRoster.PlaceText(now, Rec(a))}”且不算在家园（{roster}）、不接家园工作单 / 规则派遣（{noWork}）、出击 +1（{counted}）、离家报告已开（{report}）、再派第二支被拒（{blocked}）");
        }

        // ── M4 出发事务回滚 ──────────────────────────────────────────────────────

        private static void CheckDepartureRollback()
        {
            CampaignState s = NewWorld(5102);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            int before = FracturedCityRegion.Find(s).ExpeditionCount;
            int machines = MachineRegistry.AllRecords.Count();
            ExpeditionMarchService.BeginFaultInjectionForTests = () => throw new InvalidOperationException("selfcheck-begin-fault");
            ExpeditionDepartureService.DepartureResult dep;
            try
            {
                dep = ExpeditionDepartureService.TryDepart(new[] { a }, true);
            }
            finally
            {
                ExpeditionMarchService.BeginFaultInjectionForTests = null;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionMarchState m = ExpeditionMarchService.Current(now);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool rolled = dep.Outcome == ExpeditionDepartureService.DepartureOutcome.RolledBack && !ReferenceEquals(now, s)
                          && dep.Reasons.Any(r => r.StartsWith("transaction-failed-rolled-back", StringComparison.Ordinal));
            bool clean = m.Phase == ExpeditionMarchService.PhaseNone && !ExpeditionMarchService.IsAway(now) && FracturedCityRegion.Find(now).ExpeditionCount == before
                         && !now.Stats.AwayReports.HasOpen && Rec(a)?.RegionId == HomeValleyLayout.RegionId && MachineRegistry.AllRecords.Count() == machines
                         && home != null && Mathf.Approximately(home.UnitSpeed(Unit(home, a)), HomeValleyMachineMarker.MoveSpeed);
            Expect(rolled && clean && ExpeditionChecklistService.DescribeHard(dep.Reasons[0]) == GameText.Get("exp.check.hard.transaction_failed"),
                $"M4 出发事务里行军开到一半出错（{dep.Outcome}）：整体回滚到出发前档——没有行军、出击次数不变、离家报告没开、机器还在家园且速度正常、机器数不变（{clean}）；玩家看到“{GameText.Get("exp.check.hard.transaction_failed")}”");
        }

        // ── M5 真实行军到达 ──────────────────────────────────────────────────────

        private static void CheckMarchToArrival()
        {
            CampaignState s = NewWorld(5103);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M5 前提：出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            float routeLen = ExpeditionSiteAnchors.RouteLength(now, FracturedCityLayout.RegionId);
            int arrivedCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchArrived);
            int arrivals = ExpeditionMarchService.ArriveCount;
            long t0 = GameClock.Ticks;
            double firstEta = -1;
            double laterEta = -1;
            float maxSpread = 0f;
            float maxWheelSpeed = 0f;
            Vector2 lastA = Vector2.zero;
            long lastATick = -1;
            float lastDist = float.MaxValue;
            bool approaching = true;
            int samples = 0;
            var sw = Stopwatch.StartNew();
            long steps = 0;
            bool done = StepUntil(() =>
            {
                ExpeditionMarchState m = ExpeditionMarchService.Current(CampaignSession.Current);
                if (m.Phase == ExpeditionMarchService.PhaseAtSite)
                {
                    return true;
                }
                double eta = ExpeditionMarchService.EtaSeconds(CampaignSession.Current);
                if (eta >= 0 && firstEta < 0)
                {
                    firstEta = eta;
                }
                else if (eta >= 0 && GameClock.Ticks - t0 > 1200 && laterEta < 0)
                {
                    laterEta = eta;
                }
                CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
                if (home != null && home.TryGetMachinePosition(a, out Vector2 pa) && home.TryGetMachinePosition(b, out Vector2 pb))
                {
                    maxSpread = Mathf.Max(maxSpread, Vector2.Distance(pa, pb));
                    if (lastATick < 0 || GameClock.Ticks - lastATick >= GameClock.StepHz)
                    {
                        if (lastATick >= 0)
                        {
                            maxWheelSpeed = Mathf.Max(maxWheelSpeed, Vector2.Distance(pa, lastA) / ((GameClock.Ticks - lastATick) / (float)GameClock.StepHz));
                        }
                        lastA = pa;
                        lastATick = GameClock.Ticks;
                    }
                    float d = Vector2.Distance((pa + pb) * 0.5f, anchor);
                    if (++samples % 20 == 0)
                    {
                        approaching &= d < lastDist + 3f;
                        lastDist = d;
                    }
                }
                return false;
            }, 20000, onSteps: n => steps += n);
            sw.Stop();
            double seconds = (GameClock.Ticks - t0) / (double)GameClock.StepHz;
            double expected = routeLen / 3.5;
            CampaignState after = CampaignSession.Current;
            ExpeditionMarchState mm = ExpeditionMarchService.Current(after);
            bool atSite = done && mm.Phase == ExpeditionMarchService.PhaseAtSite && WorldSimulation.ActiveExpedition?.SiteId == FracturedCityLayout.RegionId
                          && Rec(a).RegionId == FracturedCityLayout.RegionId && Rec(b).RegionId == FracturedCityLayout.RegionId;
            bool timing = seconds >= expected * 0.85 && seconds <= expected * 1.8;
            bool eta = firstEta > 0 && laterEta > 0 && laterEta < firstEta;
            bool cohesion = maxWheelSpeed > 2f && maxWheelSpeed <= 3.5f * 1.1f;
            bool notified = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchArrived) == arrivedCues + 1 && ExpeditionMarchService.ArriveCount == arrivals + 1
                            && NotificationCenter.History.Any(e => e.Type?.Id == "expedition_march");
            bool noHijack = WorldView.ObservedSiteId == HomeValleyLayout.RegionId;
            ExpeditionMarchService.TeamStatus st = ExpeditionMarchService.StatusOf(after);
            bool status = st == ExpeditionMarchService.TeamStatus.Standby || st == ExpeditionMarchService.TeamStatus.Engaged;
            Expect(atSite && timing && eta && cohesion && approaching && notified && noHijack && status,
                $"M5 真实行军（家园战斗内核推进，FGR-EXP-010）：{seconds:0.0} 游戏秒走到锚点并进入破碎都市（路线 {routeLen:0} 格 ÷ 3.5 格/秒 ≈ {expected:0.0} 秒；{timing}）；" +
                $"预计到达时间随行进变小（{firstEta:0.0} → {laterEta:0.0}）；轮式那台（本身 6 格/秒）每游戏秒最多走 {maxWheelSpeed:0.00} 格 = 按履带的 3.5 走（{cohesion}；两台各自寻路，最大相距 {maxSpread:0.0} 格）、一路越走越近（{approaching}）；" +
                $"到达提示与“远征行军”通知各一次（{notified}）、镜头不被抢走（仍在家园：{noHijack}）；进入地点后状态“{GameText.Get(ExpeditionMarchService.StatusTextKey(st))}”");
            PerfLines.Add($"真实行军 {steps} 步（{seconds:0.0} 游戏秒）墙钟 {sw.Elapsed.TotalSeconds:0.0} 秒（Editor 批处理、家园 + 星球内核全部在跑；含两台机器沿路生成区块）");
            ExpectPerf(atSite, $"M5 行军中每步平均墙钟（家园全部系统 + 行军判定）≤ 4 ms：{sw.Elapsed.TotalMilliseconds / Math.Max(1, steps):F3} ms/步",
                PerfGate.Le(sw.Elapsed.TotalMilliseconds / Math.Max(1, steps), 4.0, "行军每步 ms"));
        }

        // ── M6 观察 / 不观察 / 倍速 / 暂停（FGT-EXP-012）──────────────────────────────

        private static void CheckObservationAndSpeed()
        {
            if (!BuildMarchSave(5104, withRaiders: true))
            {
                return;
            }
            const int steps = 900;
            RestoreSave(Slot);
            long target = GameClock.Ticks + steps;
            WorldSimulation.StepMany(steps);
            string headless = Digest(CampaignSession.Current);
            bool engagedSeen = ExpeditionMarchService.Current(CampaignSession.Current).Engaged || HostilesNearTeam();

            RestoreSave(Slot);
            var reader = new SilentReader();
            InputRouter.DebugSetReader(reader);
            int frame = 0;
            bool followed = WorldView.ToggleFollowExpedition();
            while (GameClock.Ticks < target && frame < 5000)
            {
                if (frame % 120 == 60)
                {
                    WorldView.FocusHomeCore(); // 镜头飞回家园看一眼（停止跟随）
                }
                else if (frame % 120 == 0 && frame > 0)
                {
                    WorldView.ToggleFollowExpedition(); // 再跟上远征队
                }
                WorldSimulation.Frame(1f / 60f, target);
                frame++;
            }
            InputRouter.DebugSetReader(null);
            string observed = Digest(CampaignSession.Current);

            var bySpeed = new List<string>();
            foreach (float speed in new[] { 0.5f, 2f, 3f })
            {
                RestoreSave(Slot);
                GameClock.SetSpeed(speed);
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                bySpeed.Add(Digest(CampaignSession.Current));
            }
            RestoreSave(Slot);
            string beforePause = Digest(CampaignSession.Current);
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            string paused = Digest(CampaignSession.Current);
            GameClock.SetPaused(false);
            Expect(followed && observed == headless && bySpeed.All(x => x == headless) && beforePause == paused,
                $"M6 后台一致（FGT-EXP-012 / FGR-BASE-021）：行军途中有敌人在路边（交战 / 敌情：{engagedSeen}），镜头跟随远征队、飞回家园、再跟上（{frame} 帧）的一遍与从不观察的无头一遍逐字段一致（{observed == headless}）；" +
                $"0.5x / 2x / 3x 跑到同一步结果相同（{bySpeed.All(x => x == headless)}）；暂停 2 秒真实时间不推进（{beforePause == paused}）");
        }

        // ── M7 行军中存读档 ──────────────────────────────────────────────────────

        private static void CheckSaveLoadContinue()
        {
            if (!BuildMarchSave(5105, withRaiders: false))
            {
                return;
            }
            // 审查修复（P1）：对照组必须是“从没读过档”的那份——存档后不卸载世界，直接用存档那一刻的内存状态接着走（A）；
            // 再读这份档接着走（B）。两边比到达的那一步与到达后的状态：有字段没进存档（内核路线进度、寻路待处理请求、补下命令标记……）就会分叉。
            // A 到达时会在当前槽位自动存档，先把行军档另存一份给 B 读。
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(KeepSlot), true);
            CampaignState live = CampaignSession.Current;
            string atSave = Digest(live);
            long arriveA = RunToArrival();
            string afterA = ArrivalDigest();
            bool liveKept = ReferenceEquals(CampaignSession.Current, live);
            // B：读档 → 读档后行军记录、内核命令、速度都在 → 接着走到到达。
            RestoreSave(KeepSlot);
            CampaignState s = CampaignSession.Current;
            string atLoad = Digest(s);
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            ExpeditionSiteAnchors.TryGet(s, m.SiteId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool resumed = m.Phase == ExpeditionMarchService.PhaseOutbound && m.Members.All(id => CommandTowards(home, id, anchor, 6f) && Mathf.Approximately(home.UnitSpeed(Unit(home, id)), m.Speed));
            long arriveB = RunToArrival();
            string afterB = ArrivalDigest();
            Expect(!ReferenceEquals(s, live) && liveKept && resumed && arriveA > 0 && arriveA == arriveB && afterA == afterB,
                $"M7 行军中存读档（FGT-EXP-011 行军部分 / B10）：A 存档后不读档、用内存里那份接着走，第 {arriveA} 步到达；B 读同一份档（读档后行军记录、成员的内核命令与行军速度都在：{resumed}）接着走，第 {arriveB} 步到达；" +
                $"到达后逐字段一致（{afterA == afterB}）、不复制机器；存档那一刻与读档后的整体指纹{(atSave == atLoad ? "也一致" : "不同（只作参考：内核 / 寻路的临时缓存可以不同，结果必须相同）")}");
        }

        // ── M8 覆盖内外的命令、接入、返回 ─────────────────────────────────────────

        private static void CheckCoverageCommands()
        {
            CampaignState s = NewWorld(5106);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M8 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(30);
            // 覆盖内：待命（每台守在原地）→ 前进。
            bool holdOk = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Hold, out string f1);
            bool guarded = home.TryGetCommand(Unit(home, a), out CombatCommand ca) && ca.Kind == CombatCommandKind.Guard;
            bool standby = ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Standby && ExpeditionMarchService.EtaSeconds(now) < 0;
            bool advOk = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Advance, out string f2) && CommandTowards(home, a, anchor, 6f);
            Expect(holdOk && guarded && standby && advOk && f1 == null && f2 == null,
                $"M8 覆盖内可以随时改命令（FGR-EXP-011）：“待命”生效（内核守备命令、状态待命、没有预计到达时间：{guarded && standby}），再“前进”恢复往锚点走（{advOk}）");

            int edgeCues = ExpeditionMarchService.EdgeCueCount;
            bool edgeWhileCovered = false;
            StepUntil(() =>
            {
                ExpeditionMarchState m = ExpeditionMarchService.Current(CampaignSession.Current);
                if (!edgeWhileCovered && ExpeditionMarchService.EdgeCueCount > edgeCues)
                {
                    edgeWhileCovered = m.InCoverage;
                }
                return !m.InCoverage;
            }, 12000);
            ExpeditionMarchState mo = ExpeditionMarchService.Current(now);
            int leftCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchOutOfCoverage);
            bool outside = !mo.InCoverage && leftCues > 0;
            int rejects = ExpeditionMarchService.CommandRejectCount;
            bool returnRejected = !ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Return, out string why) && why == "out-of-coverage"
                                  && ExpeditionMarchService.CommandRejectCount == rejects + 1 && mo.Command == (int)ExpeditionMarchService.Command.Advance && mo.Phase == ExpeditionMarchService.PhaseOutbound;
            int outId = mo.Members.FirstOrDefault(id => !SignalCoverageService.CanReceiveCommand(id));
            UplinkRequestResult up = SignalUplinkService.Request(outId, UplinkSource.MachineList, startCamera: false);
            bool noUplink = outId != 0 && !up.Accepted && up.Failure == UplinkFailure.OutOfCoverage;
            if (up.Accepted)
            {
                SignalUplinkService.CancelByPlayer();
            }
            float d0 = DistanceToGoal(home, a, anchor);
            WorldSimulation.StepMany(180);
            float d1 = DistanceToGoal(home, a, anchor);
            bool lastOrder = CommandTowards(home, a, anchor, 6f) && d1 < d0 - 5f;
            Expect(edgeWhileCovered && outside && returnRejected && noUplink && lastOrder,
                $"M8 走出覆盖前先提醒（提醒时还在覆盖里：{edgeWhileCovered}），走出后提醒“{GameText.Get("exp.cue.out_of_coverage")}”；覆盖外“返回”被拒（原因 {why}）、不能接入其中在覆盖外的那台 #{outId}（{up.Failure}），" +
                $"队伍继续执行最后的命令往锚点走（3 秒内又近了 {d0 - d1:0.0} 格：{lastOrder}）（FGR-SIG-053）");

            // 铺中继回到覆盖：控制自动恢复（能接入、能下命令）。
            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(now, ExpeditionSiteAnchors.Route(now, FracturedCityLayout.RegionId), "fgmarch_relay");
            bool back = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).InCoverage, 300);
            UplinkRequestResult up2 = SignalUplinkService.Request(outId, UplinkSource.MachineList, startCamera: false);
            bool uplinkBack = up2.Accepted;
            SignalUplinkService.CancelByPlayer();
            bool retOk = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Return, out _);
            bool returning = ExpeditionMarchService.Current(now).Phase == ExpeditionMarchService.PhaseReturning
                             && ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Returning
                             && CommandTowards(home, a, HomeValleyLayout.Core.Position, 6f);
            Expect(relays > 0 && back && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchBackInCoverage) > 0 && uplinkBack && retOk && returning,
                $"M8 沿路线铺 {relays} 座中继塔回到覆盖：回到覆盖提醒、能接入（{uplinkBack}{(uplinkBack ? string.Empty : "：" + up2.Failure)}）、能下命令——“返回家园”后状态“返程中”、往归还核心走（{returning}）");

            int reportsBefore = AwayReportService.Recent(now).Count;
            bool home2 = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Phase == ExpeditionMarchService.PhaseReturned, 20000);
            AwayReportRecord rep = AwayReportService.Recent(now).FirstOrDefault();
            bool settled = home2 && !now.Stats.AwayReports.HasOpen && rep != null && rep.Outcome == AwayReportService.OutcomeEvacuated && AwayReportService.Recent(now).Count == reportsBefore + 1
                           && ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Returned && !ExpeditionMarchService.IsAway(now)
                           && MachineRoster.IsHome(Rec(a)) && Mathf.Approximately(home.UnitSpeed(Unit(home, a)), HomeValleyMachineMarker.MoveSpeed)
                           && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchReturned) > 0;
            Expect(settled,
                $"M8 走回家园 = 已返回（FGR-EXP-012）：离家报告按“返回”结算、远征不在外、机器回到家园（名册算在家）、速度恢复、返回提示（{settled}）");
        }

        // ── M9 途中静默夜 ────────────────────────────────────────────────────────

        private static void CheckSilentNight()
        {
            CampaignState s = NewWorld(5107);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M9 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            SilentNightService.Install();
            try
            {
            SilentNightService.ScheduleForTests(now, 1.0, 6.0, raid: false);
            bool started = StepUntil(() => SilentNightService.IsActiveNow(), 600);
            bool rejected = !ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Hold, out string why) && why == "silent-night";
            bool safe = SignalLinkService.IsInSafeMode(now, a);
            float d0 = DistanceToGoal(home, a, anchor);
            WorldSimulation.StepMany(120);
            bool keeps = DistanceToGoal(home, a, anchor) < d0 - 3f && CommandTowards(home, a, anchor, 6f);
            bool over = StepUntil(() => !SilentNightService.IsActiveNow(), 900);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool cmdBack = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Hold, out string why2);
            Expect(started && rejected && safe && keeps && over && cmdBack,
                $"M9 途中静默夜（开始：{started}）：远程命令被拒（原因 {why}，FGR-ENV-011）、成员进入安全模式（{safe}）、队伍照最后的命令继续往锚点走（{keeps}）；静默夜结束后命令恢复（{cmdBack}{(cmdBack ? string.Empty : "：" + why2)}）");
            }
            finally
            {
                SilentNightService.ResetForTests();
                GameClock.SetPaused(false);
            }
        }

        // ── M10 途中家园遇袭 ─────────────────────────────────────────────────────

        private static void CheckHomeRaid()
        {
            CampaignState s = NewWorld(5108);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            // 沿路线铺中继（覆盖夹具）：走到核心覆盖外之后玩家仍能下“返回家园”，本段测的是遇袭时的判定与召回，不是覆盖。
            ExpeditionSiteAnchors.EnsureResolved(s);
            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId), "fgmarch_relay_r");
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M10 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(1500);
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(now, "foundry", 3, out _);
            WorldSimulation.StepMany(240);
            ExpeditionMarchState m = ExpeditionMarchService.Current(now);
            bool raidOn = raid != null && WorldTransitSystem.Groups(now).Any(g => g.GroupId == raid.GroupId)
                          && WorldView.FocusTargets(now).Any(t => t.IsRaid);
            bool notRecalled = m.Phase == ExpeditionMarchService.PhaseOutbound && m.Command == (int)ExpeditionMarchService.Command.Advance
                               && CommandTowards(home, a, anchor, 6f) && CommandTowards(home, b, anchor, 6f) && ExpeditionMarchService.IsMarchMember(now, a);

            // DEBT-FG6DEF07-06（承接本卡）：行军中（地点还没载入）镜头跟着远征队走远 = 玩家在远征，远征决策条照常判定；镜头在家园 = 不在远征。
            Vector2 core = HomeValleyLayout.Core.Position;
            float focusCells = HomeRaidAlertService.MarchFocusCells;
            bool far = StepUntil(() => ExpeditionMarchService.TryTeamCenter(CampaignSession.Current, out Vector2 c, out _) && Vector2.Distance(c, core) > focusCells + 20f, 8000);
            WorldView.EnsureCamera();
            InputRouter.DebugSetReader(new SilentReader());
            bool atHomeNotAway = WorldView.IsObserved(HomeValleyLayout.RegionId) && !HomeRaidAlertService.IsAway(now);
            bool followed = WorldView.ToggleFollowExpedition();
            for (int i = 0; i < 40; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            bool awayWhileFollowing = followed && WorldView.IsObserved(HomeValleyLayout.RegionId) && HomeRaidAlertService.IsAway(now);
            bool shows = HomeRaidAlertService.Evaluate(now, GameClock.Ticks, out HomeRaidAlert alert) && alert.Away;
            WorldView.FocusHomeCore();
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            InputRouter.DebugSetReader(null);
            bool backHome = !HomeRaidAlertService.IsAway(now) && !WorldView.FollowingExpedition;
            Expect(far && atHomeNotAway && awayWhileFollowing && backHome,
                $"M10 途中家园遇袭时“玩家在远征”的判定（DEBT-FG6DEF07-06）：远征队走出 {focusCells:0} 格（{far}），镜头在家园 = 不在远征（{atHomeNotAway}）；点“跟随远征队”、镜头随队走远 = 在远征（{awayWhileFollowing}；决策条判定 {shows}，遇袭波数 {alert.Waves}）；" +
                $"镜头飞回家园 = 不在远征（{backHome}）。判定只读镜头焦点，不改模拟");

            bool recall = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Return, out string why);
            float d0 = DistanceToGoal(home, a, HomeValleyLayout.Core.Position);
            WorldSimulation.StepMany(180);
            float d1 = DistanceToGoal(home, a, HomeValleyLayout.Core.Position);
            bool homeward = d1 < d0 - 3f && ExpeditionMarchService.Current(now).Phase == ExpeditionMarchService.PhaseReturning;
            Expect(relays > 0 && raidOn && notRecalled && recall && homeward,
                $"M10 途中家园遇袭：有突袭往家园来（关注点条里有它：{raidOn}），远征队不会被自动召回、照原命令往锚点走（FGR-BASE-020：{notRecalled}）；玩家在覆盖内下“返回家园”生效、往回走（离核心 {d0:0} → {d1:0} 格：{recall && homeward}{(recall ? string.Empty : "：" + why)}）");

            // 审查 P2：最慢的那台（履带 3.5）途中阵亡 → 行军速度按存活成员里最慢的重算（轮式 6），内核速度跟着改。
            float before = ExpeditionMarchService.Current(now).Speed;
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            ExpeditionMarchState m2 = ExpeditionMarchService.Current(now);
            bool faster = Mathf.Approximately(before, 3.5f) && !Rec(a).IsAlive && Mathf.Approximately(m2.Speed, 6f) && Mathf.Approximately(home.UnitSpeed(Unit(home, b)), 6f)
                          && m2.Phase == ExpeditionMarchService.PhaseReturning;
            Expect(faster, $"M10 最慢的那台（履带）途中阵亡：行军速度 {before} → {m2.Speed} 格/秒（存活的轮式那台），内核速度跟着改（{faster}；FGR-EXP-010“取决于最慢的那台”按存活成员算）");
        }

        // ── M11 地形阻挡与截断 ───────────────────────────────────────────────────

        private static void CheckObstacleAndTruncation()
        {
            CampaignState s = NewWorld(5109);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionSiteAnchors.EnsureResolved(s);
            ExpeditionSiteAnchors.TryGet(s, FracturedCityLayout.RegionId, out Vector2 anchor);
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            GridCell core = HomeGridService.CorePivot(s);
            // （a）地形阻挡：在核心与锚点的直线中段横一道 2 格厚、80 格长的悬崖墙（场景夹具，改的是真实格网），成员要绕过去。
            Vector2 c0 = new Vector2(core.X, core.Y);
            Vector2 dir = (anchor - c0).normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            Vector2 mid = Vector2.Lerp(c0, anchor, 0.55f);
            var wall = new List<(GridCell Cell, byte Was)>();
            for (int k = -40; k <= 40; k++)
            {
                for (int t = 0; t < 2; t++)
                {
                    Vector2 p = mid + side * k + dir * t;
                    var cell = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    if (wall.Any(w => w.Cell.X == cell.X && w.Cell.Y == cell.Y))
                    {
                        continue;
                    }
                    wall.Add((cell, map.GetTerrain(cell)));
                    map.SetTerrain(cell, cliff);
                }
            }
            WorldSimulation.StepMany(2);
            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId), "fgmarch_relay_b");
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M11 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            bool onCliff = false;
            bool crossed = false;
            // 走到墙后（离锚点比墙近）为止：全程不踏上悬崖格。
            StepUntil(() =>
            {
                if (home.TryGetMachinePosition(a, out Vector2 p))
                {
                    GridCell cell = NavService.CellOf(p.x, p.y);
                    onCliff |= map.GetTerrain(cell) == cliff;
                    crossed = Vector2.Distance(p, anchor) < Vector2.Distance(mid, anchor) - 8f;
                }
                return crossed;
            }, 12000);
            Expect(relays > 0 && crossed && !onCliff && wall.Count >= 120,
                $"M11 地形阻挡：核心与锚点之间横一道 {wall.Count} 格悬崖墙，成员沿层级寻路绕过去（已越过墙：{crossed}），全程没有踏上悬崖格（{!onCliff}）");

            // （b）截断：在锚点周围围一圈悬崖（半径 ArriveRadius+4～+5）→ 走不进去 → 停在最近可达点、原地待命、说明原因（不瞬移、不卡死）。
            int ringR = Mathf.CeilToInt(ExpeditionMarchService.ArriveRadius) + 4;
            var ring = new List<(GridCell Cell, byte Was)>();
            for (int dx = -ringR - 3; dx <= ringR + 3; dx++)
            {
                for (int dy = -ringR - 3; dy <= ringR + 3; dy++)
                {
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < ringR || d > ringR + 2.5f)
                    {
                        continue;
                    }
                    var cell = new GridCell(Mathf.RoundToInt(anchor.x) + dx, Mathf.RoundToInt(anchor.y) + dy);
                    ring.Add((cell, map.GetTerrain(cell)));
                    map.SetTerrain(cell, cliff);
                }
            }
            int haltCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted);
            int arrivals = ExpeditionMarchService.ArriveCount;
            bool halted = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Halted == ExpeditionMarchService.HaltRouteTruncated, 15000);
            ExpeditionMarchState m = ExpeditionMarchService.Current(now);
            bool outside = home.TryGetMachinePosition(a, out Vector2 pa) && Vector2.Distance(pa, anchor) > ringR - 1f && Rec(a).RegionId == HomeValleyLayout.RegionId;
            bool explained = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted) == haltCues + 1 && ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Standby
                             && ExpeditionMarchService.EtaSeconds(now) < 0 && ExpeditionMarchService.ArriveCount == arrivals && m.Phase == ExpeditionMarchService.PhaseOutbound;
            Expect(halted && outside && explained,
                $"M11 路线截断：锚点被悬崖围住，成员停在圈外（{outside}），状态“待命”、提示“{GameText.Get("exp.cue.halted")}”一次、没有预计到达时间、没有到达（{explained}）");

            // 打通（地形还原）后再下“前进”：能走进去、到达（不软锁）。
            foreach ((GridCell Cell, byte Was) r in ring)
            {
                map.SetTerrain(r.Cell, r.Was);
            }
            WorldSimulation.StepMany(2);
            bool adv = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Advance, out string why);
            bool arrived = adv && StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Phase == ExpeditionMarchService.PhaseAtSite, 6000);
            foreach ((GridCell Cell, byte Was) w in wall)
            {
                map.SetTerrain(w.Cell, w.Was);
            }
            Expect(adv && arrived, $"M11 打通后玩家再下“前进”（{(adv ? "生效" : why)}）：走进去并进入远征地点（{arrived}）——截断不会软锁");
        }

        /// <summary>
        /// FG9-E2E-01 复审修复（FG-GAP-136 / IC-REQ-022 路径失败不软锁）：第一幕节奏下（没有中继、没有投送站）队伍在覆盖外被截断，玩家下不了命令——
        /// 行军服务按最后的“前进”到点自动重试（FGR-EXP-011 覆盖外执行最后一条命令）；仍走不进去只记日志不重复提示；打通后下一次重试走进去、进入地点。
        /// </summary>
        private static void CheckTruncationOutOfCoverage()
        {
            CampaignState s = NewWorld(5113);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionSiteAnchors.EnsureResolved(s);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M11b 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            HomeGridMap map = HomeGridService.MapFor(now);
            byte cliff = GridContent.TerrainCode("cliff");
            int ringR = Mathf.CeilToInt(ExpeditionMarchService.ArriveRadius) + 4;
            var ring = new List<(GridCell Cell, byte Was)>();
            int retrySteps = Mathf.CeilToInt(ExpeditionMarchService.ArrivalRetrySeconds * GameClock.StepHz);
            try
            {
                // 走出核心覆盖（没有中继）之后，锚点四周塌成悬崖（场景夹具，改的是真实格网）：路上才发现走不进去。
                bool outside = StepUntil(() => !ExpeditionMarchService.CanCommand(CampaignSession.Current, out string r) && r == "out-of-coverage", 20000);
                if (!outside)
                {
                    Fail("M11b 前提：行军 20000 步内没有走出核心覆盖");
                    return;
                }
                for (int dx = -ringR - 3; dx <= ringR + 3; dx++)
                {
                    for (int dy = -ringR - 3; dy <= ringR + 3; dy++)
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d < ringR || d > ringR + 2.5f)
                        {
                            continue;
                        }
                        var cell = new GridCell(Mathf.RoundToInt(anchor.x) + dx, Mathf.RoundToInt(anchor.y) + dy);
                        ring.Add((cell, map.GetTerrain(cell)));
                        map.SetTerrain(cell, cliff);
                    }
                }
                WorldSimulation.StepMany(2);
                int cues0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted);
                int retry0 = ExpeditionMarchService.HaltRetryCount;
                bool halted = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Halted == ExpeditionMarchService.HaltRouteTruncated, 20000);
                ExpeditionMarchState m = ExpeditionMarchService.Current(now);
                bool rejected = !ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Advance, out string why) && why == "out-of-coverage";
                bool cued = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted) == cues0 + 1 && m.ArrivalRetryTick > GameClock.Ticks;
                // 还围着：到点自动重试一次，仍走不进去 → 再停下、不重复提示，命令仍是“前进”。
                long at = m.ArrivalRetryTick;
                StepUntil(() => GameClock.Ticks > at + 3, retrySteps + 30, chunk: 1);
                bool reHalted = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Halted == ExpeditionMarchService.HaltRouteTruncated, 4000);
                bool quiet = ExpeditionMarchService.HaltRetryCount >= retry0 + 1 && reHalted && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHalted) == cues0 + 1
                             && m.Command == (int)ExpeditionMarchService.Command.Advance && m.Phase == ExpeditionMarchService.PhaseOutbound;
                Expect(halted && rejected && cued && quiet,
                    $"M11b 覆盖外被截断（没有中继 / 投送站）：停下并提示“{GameText.Get("exp.cue.halted")}”一次（{halted && cued}），玩家“前进”被拒（{why}：{rejected}）；" +
                    $"{ExpeditionMarchService.ArrivalRetrySeconds:0} 游戏秒后按最后的“前进”自动重试（{ExpeditionMarchService.HaltRetryCount - retry0} 次），仍走不进去再停下、不重复提示、命令不变（{quiet}）");

                // 打通（地形还原）：下一次自动重试走进去、进入远征地点——玩家不用任何操作，不软锁。
                foreach ((GridCell cell, byte was) in ring)
                {
                    map.SetTerrain(cell, was);
                }
                ring.Clear();
                WorldSimulation.StepMany(2);
                bool arrived = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Phase == ExpeditionMarchService.PhaseAtSite, retrySteps + 8000);
                Expect(arrived && ExpeditionMarchService.HaltRetryCount >= retry0 + 2,
                    $"M11b 打通后下一次自动重试走进去并进入远征地点（{arrived}，自动重试共 {ExpeditionMarchService.HaltRetryCount - retry0} 次）——覆盖外截断不软锁");
            }
            finally
            {
                foreach ((GridCell cell, byte was) in ring)
                {
                    map.SetTerrain(cell, was);
                }
            }
        }

        // ── M12 到达失败 ─────────────────────────────────────────────────────────

        private static void CheckArrivalFailure()
        {
            // 审查修复（P1）：全程走正式入口——真实出发、真实行军走到集结点、真实到达判定与自动重试；不用快进（ArriveNowForTests 会绕过覆盖判定）。
            // 没有中继：集结点在核心覆盖外（锚点离核心 300 格以上，核心覆盖 150 格），玩家此时下不了命令——恢复不能依赖玩家命令（FG00 B11）。
            CampaignState s = NewWorld(5110);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M12 前提：出发失败（{dep.Outcome}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            int machines = MachineRegistry.AllRecords.Count();
            int retrySteps = Mathf.CeilToInt(ExpeditionMarchService.ArrivalRetrySeconds * GameClock.StepHz);

            // （a）快到集结点时区域被重新锁定（测试捷径：直接改区域状态）→ 真实到达判定失败：不存档、不载入、机器不动，最后的命令仍是“前进”。
            bool close = StepUntil(() => DistanceToGoal(home, a, anchor) < ExpeditionMarchService.ArriveRadius + 60f, 20000);
            RegionRecord ruins = FracturedCityRegion.Find(now);
            ruins.State = RegionState.Locked;
            int fails = ExpeditionMarchService.ArrivalFailCount;
            int failCues = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchArrivalFailed);
            bool failed = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Halted == ExpeditionMarchService.HaltArrivalFailed, 4000, chunk: 1);
            ExpeditionMarchState m = ExpeditionMarchService.Current(now);
            bool soft = close && failed && m.Phase == ExpeditionMarchService.PhaseOutbound && m.Command == (int)ExpeditionMarchService.Command.Advance && m.ArrivalFailReason == "site-unavailable"
                        && WorldSimulation.ActiveExpedition == null && Rec(a).RegionId == HomeValleyLayout.RegionId && ExpeditionMarchService.ArrivalFailCount == fails + 1
                        && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchArrivalFailed) == failCues + 1 && ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Standby
                        && DistanceToGoal(home, a, anchor) <= ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseOutbound, 1);
            // 负向：集结点在覆盖外，玩家的“前进”被拒（out-of-coverage）——不能靠玩家重下命令恢复。
            bool outOfReach = !ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Advance, out string why) && why == "out-of-coverage";
            // 还锁着时到点重试一次：再失败，同一原因不重复提示，最后的命令不变。
            long retryAt = m.ArrivalRetryTick;
            StepUntil(() => GameClock.Ticks > retryAt + 3, retrySteps + 30, chunk: 1);
            bool quiet = ExpeditionMarchService.ArrivalFailCount == fails + 2 && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchArrivalFailed) == failCues + 1
                         && m.Halted == ExpeditionMarchService.HaltArrivalFailed && m.Command == (int)ExpeditionMarchService.Command.Advance && m.ArrivalRetryTick > retryAt
                         && WorldSimulation.ActiveExpedition == null;
            Expect(soft && outOfReach && quiet,
                $"M12 到达时地点不开放（走到集结点：{close}）：不载入、机器不动、在集结点等候（Halted = 到达失败、原因 {m.ArrivalFailReason}），最后的命令仍是“前进”（不替玩家改成待命），提示“{GameText.Get("exp.cue.arrival_failed")}”一次（{soft}）；" +
                $"集结点在覆盖外，玩家的“前进”被拒（{why}：{outOfReach}）；{ExpeditionMarchService.ArrivalRetrySeconds:0} 游戏秒后按“前进”自动重试，仍不开放再失败、同一原因不重复提示（{quiet}）");

            // （b）解锁后，下一次自动重试时载入中途出错（故障注入，只生效一次）→ 整体回滚到到达前档，队伍留在集结点，最后的命令仍是“前进”。
            ExpeditionMarchService.ArrivalFaultInjectionForTests = () =>
            {
                ExpeditionMarchService.ArrivalFaultInjectionForTests = null;
                throw new InvalidOperationException("selfcheck-arrival-fault");
            };
            int rollbacks = ExpeditionMarchService.ArrivalRollbackCount;
            ruins.State = RegionState.Available;
            bool rolledBack = StepUntil(() => ExpeditionMarchService.ArrivalRollbackCount > rollbacks, retrySteps + 60, chunk: 1);
            ExpeditionMarchService.ArrivalFaultInjectionForTests = null;
            CampaignState restored = CampaignSession.Current;
            ExpeditionMarchState m2 = ExpeditionMarchService.Current(restored);
            bool rolled = rolledBack && !ReferenceEquals(restored, now) && ExpeditionMarchService.ArrivalRollbackCount == rollbacks + 1
                          && m2.Phase == ExpeditionMarchService.PhaseOutbound && m2.Halted == ExpeditionMarchService.HaltArrivalFailed && m2.ArrivalFailReason == "transaction-failed"
                          && m2.Command == (int)ExpeditionMarchService.Command.Advance
                          && WorldSimulation.ActiveExpedition == null && WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded
                          && Rec(a).RegionId == HomeValleyLayout.RegionId && MachineRegistry.AllRecords.Count() == machines;
            string rolledText = $"战役状态换成读回的那份 {!ReferenceEquals(restored, now)}、回滚计数 {ExpeditionMarchService.ArrivalRollbackCount - rollbacks}、阶段 {m2?.Phase}、停下 {m2?.Halted}（{m2?.ArrivalFailReason}）、命令 {m2?.Command}、地点 {WorldSimulation.ActiveExpedition?.SiteId ?? "无"}、机器在 {Rec(a)?.RegionId}、机器数 {MachineRegistry.AllRecords.Count()}/{machines}";
            // 再下一次自动重试（没有故障）：进入地点。全程没有玩家命令、没有快进。
            int arrivals = ExpeditionMarchService.ArriveCount;
            bool retry = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Phase == ExpeditionMarchService.PhaseAtSite, retrySteps + 240, chunk: 1)
                         && WorldSimulation.ActiveExpedition?.SiteId == FracturedCityLayout.RegionId && Rec(a).RegionId == FracturedCityLayout.RegionId
                         && ExpeditionMarchService.ArriveCount == arrivals + 1 && MachineRegistry.AllRecords.Count() == machines
                         && string.IsNullOrEmpty(ExpeditionMarchService.Current(CampaignSession.Current).ArrivalFailReason);
            Expect(rolled && retry,
                $"M12 到达事务载入中途出错：整体回滚到到达前档（{rolledText}：{rolled}），队伍在集结点照“前进”等候；下一次自动重试进入地点（{retry}）——到达失败不会软锁");
        }

        // ── M15 途中遇敌与危害提示（卡片“必须同时交付”）─────────────────────────────

        /// <summary>
        /// 审查修复（P1）：遇敌 / 危害提示的行为测试，全程真实出发、真实行军（不改队伍位置）。场景夹具（测试捷径，登记在证据里）：
        /// 路线 35% 处、偏离路线 25 格放一个敌方据点（带一支巡逻，WorldOutpostSystem.SpawnOutpost）；出发时从目的地领地派一支突袭往家园走（与队伍相向）；
        /// 破碎都市所属领地 silent 的外圈临时加一条 60 格宽的危害带（规划层 HazardWidth 原为 0，测完还原）；沿路线铺中继（让“返回家园”能下）。
        /// </summary>
        private static void CheckEncountersAndHazard()
        {
            CampaignState s = NewWorld(5113);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionSiteAnchors.EnsureResolved(s);
            List<Vector2> route = ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId);
            PlannedTerritory silent = WorldGenQuery.Plan(s)?.Find(ExpeditionSiteAnchors.TerritoryIdOf(FracturedCityLayout.RegionId));
            if (route == null || route.Count < 2 || silent == null)
            {
                Fail($"M15 前提：没有锚点路线或规划层领地（路线 {route?.Count ?? 0} 点、领地 {silent != null}）");
                return;
            }
            Vector2 mid = PointAlong(route, 0.35f, out Vector2 dir);
            Vector2 oc = mid + new Vector2(-dir.y, dir.x) * 25f;
            OutpostRecord outpost = WorldOutpostSystem.SpawnOutpost(s, silent.Id, new GridCell(Mathf.RoundToInt(oc.x), Mathf.RoundToInt(oc.y)), withPatrol: true);
            PatrolRecord patrol = WorldOutpostSystem.Patrols(s).LastOrDefault(p => p.OutpostId == outpost.OutpostId);
            int relays = FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, route, "fgmarch_relay_e");
            var hazardFixtures = new List<(PlannedTerritory T, int Width, string Key)>();
            void AddHazard(CampaignState st)
            {
                PlannedTerritory t = WorldGenQuery.Plan(st)?.Find(silent.Id);
                if (t != null && !hazardFixtures.Any(h => ReferenceEquals(h.T, t)))
                {
                    hazardFixtures.Add((t, t.HazardWidth, t.HazardKey));
                    t.HazardWidth = 60;
                    t.HazardKey = "world.hazard.acid";
                }
            }
            try
            {
                AddHazard(s);
                ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
                if (!dep.Success || patrol == null)
                {
                    Fail($"M15 前提：出发失败（{dep.Outcome}）或没有巡逻（{patrol != null}）");
                    return;
                }
                CampaignState now = CampaignSession.Current;
                AddHazard(now);
                TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(now, silent.Id, 3, out _);
                Vector2 outpostAt = new Vector2(outpost.CellX, outpost.CellY);
                int enc0 = ExpeditionMarchService.EncounterCueCount;
                int encCue0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchEncounter);

                // （a）走近据点：进入 40 格那一刻据点提示一次（巡逻在据点附近折返，同样各一次）。
                bool metOutpost = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).EncounteredIds.Contains(outpost.OutpostId), 12000);
                ExpeditionMarchState m = ExpeditionMarchService.Current(now);
                bool inRadius = ExpeditionMarchService.TryTeamCenter(now, out Vector2 c0, out _) && Vector2.Distance(c0, outpostAt) <= ExpeditionMarchService.EncounterRadius + 8f;
                bool dedupBefore = m.EncounteredIds.Distinct().Count() == m.EncounteredIds.Length
                                   && ExpeditionMarchService.EncounterCueCount - enc0 == m.EncounteredIds.Length
                                   && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchEncounter) - encCue0 == m.EncounteredIds.Length;
                bool alertNotified = NotificationCenter.History.Any(e => e.Type?.Id == "expedition_alert");
                bool keepsOrder = m.Command == (int)ExpeditionMarchService.Command.Advance && m.Phase == ExpeditionMarchService.PhaseOutbound;

                // （b）提示后马上存档、读档，接着在据点 40 格内走 4 秒：已提示过的不再提示（ID 进了存档），新出现的才提示。
                SaveResult saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                string[] savedIds = m.EncounteredIds.ToArray();
                RestoreSave(Slot);
                CampaignState loaded = CampaignSession.Current;
                AddHazard(loaded);
                ExpeditionMarchState ml = ExpeditionMarchService.Current(loaded);
                bool idsKept = savedIds.All(id => ml.EncounteredIds.Contains(id));
                int enc1 = ExpeditionMarchService.EncounterCueCount;
                bool stillNear = false;
                for (int i = 0; i < 4; i++)
                {
                    WorldSimulation.StepMany(GameClock.StepHz);
                    stillNear |= ExpeditionMarchService.TryTeamCenter(loaded, out Vector2 c1, out _) && Vector2.Distance(c1, outpostAt) <= ExpeditionMarchService.EncounterRadius;
                }
                int added = ml.EncounteredIds.Length - savedIds.Length;
                bool noRepeat = saved.Success && idsKept && stillNear && ml.EncounteredIds.Count(x => x == outpost.OutpostId) == 1
                                && ExpeditionMarchService.EncounterCueCount - enc1 == added && ml.EncounteredIds.Distinct().Count() == ml.EncounteredIds.Length;

                // （c）巡逻、迎面来的突袭队伍：各提示一次（每秒判定，同一个只一次）。
                int enc2 = ExpeditionMarchService.EncounterCueCount;
                int len2 = ml.EncounteredIds.Length;
                bool metAll = StepUntil(() =>
                {
                    ExpeditionMarchState mm = ExpeditionMarchService.Current(CampaignSession.Current);
                    return mm.EncounteredIds.Contains(patrol.PatrolId) && (raid == null || mm.EncounteredIds.Contains(raid.GroupId)) || mm.Phase != ExpeditionMarchService.PhaseOutbound;
                }, 12000);
                bool patrolMet = ml.EncounteredIds.Contains(patrol.PatrolId);
                bool raidMet = raid != null && ml.EncounteredIds.Contains(raid.GroupId);
                bool dedupAll = ml.EncounteredIds.Distinct().Count() == ml.EncounteredIds.Length && ExpeditionMarchService.EncounterCueCount - enc2 == ml.EncounteredIds.Length - len2;
                Expect(metOutpost && inRadius && dedupBefore && alertNotified && keepsOrder && noRepeat && patrolMet && raidMet && dedupAll,
                    $"M15 途中遇敌提示（FGR-EXP-010 / 必须同时交付）：真实行军走近路边的敌方据点，进入 {ExpeditionMarchService.EncounterRadius:0} 格时提示一次（{metOutpost && inRadius}）、“远征警报”通知（{alertNotified}）、" +
                    $"只提示不改命令（{keepsOrder}）；提示后存读档，接着在据点 40 格内走 4 秒（{stillNear}）不再提示已提示过的（{savedIds.Length} 个 ID 进了存档：{noRepeat}）；" +
                    $"巡逻（{patrolMet}）与迎面来的突袭队伍（{raidMet}）也各提示一次，全程每个对象只一次（{dedupBefore && dedupAll}，共 {ml.EncounteredIds.Length} 个：{string.Join("、", ml.EncounteredIds)}）");

                // （d）危害带：队伍中心进入领地外圈的危害带提示一次、在带里不重复；进到领地里面后复位；玩家下“返回家园”往外走，再穿过危害带时再提示一次。
                int haz0 = ExpeditionMarchService.HazardCueCount;
                int hazCue0 = FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHazard);
                Vector2 tc = new Vector2(silent.CenterX, silent.CenterY);
                bool inBelt = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).HazardZoneId == silent.Id, 12000);
                bool once = ExpeditionMarchService.HazardCueCount == haz0 + 1 && FeedbackCues.CountOf(FeedbackCueId.ExpeditionMarchHazard) == hazCue0 + 1;
                WorldSimulation.StepMany(GameClock.StepHz * 2);
                bool noRepeatInside = ExpeditionMarchService.HazardCueCount == haz0 + 1 || ml.Phase != ExpeditionMarchService.PhaseOutbound;
                bool reset = StepUntil(() => ExpeditionMarchService.TryTeamCenter(CampaignSession.Current, out Vector2 c, out _) && Vector2.Distance(c, tc) < silent.Radius - 2f
                                             && ExpeditionMarchService.Current(CampaignSession.Current).HazardZoneId.Length == 0, 3000, chunk: 5)
                             && ml.Phase == ExpeditionMarchService.PhaseOutbound;
                bool back = ExpeditionMarchService.IssueCommand(loaded, ExpeditionMarchService.Command.Return, out string why);
                bool again = back && StepUntil(() => ExpeditionMarchService.HazardCueCount == haz0 + 2, 6000);
                bool leftAgain = again && StepUntil(() => ExpeditionMarchService.TryTeamCenter(CampaignSession.Current, out Vector2 c, out _) && Vector2.Distance(c, tc) > silent.OuterRadius + 2f
                                                         && ExpeditionMarchService.Current(CampaignSession.Current).HazardZoneId.Length == 0, 6000);
                Expect(relays > 0 && inBelt && once && noRepeatInside && reset && again && leftAgain && ExpeditionMarchService.HazardCueCount == haz0 + 2,
                    $"M15 危害带提示：真实行军进入领地 {silent.Id} 外圈 60 格的危害带时提示一次（{inBelt && once}，“{GameText.Format("exp.cue.hazard_detail", GameText.Get("world.hazard.acid"))}”）、在带里不重复（{noRepeatInside}）；" +
                    $"进到领地里面后复位（{reset}）；玩家下“返回家园”（{(back ? "生效" : why)}）往外走，再穿过危害带时再提示一次（{again}），出了危害带再复位（{leftAgain}）。只提示不伤害（DEBT-FG8EXP02-03）");
            }
            finally
            {
                foreach ((PlannedTerritory T, int Width, string Key) h in hazardFixtures)
                {
                    h.T.HazardWidth = h.Width;
                    h.T.HazardKey = h.Key;
                }
            }
        }

        /// <summary>折线上按长度比例取一点，并给出那一段的方向（单位向量）。</summary>
        private static Vector2 PointAlong(List<Vector2> route, float t, out Vector2 dir)
        {
            float total = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                total += Vector2.Distance(route[i - 1], route[i]);
            }
            float want = total * Mathf.Clamp01(t);
            for (int i = 1; i < route.Count; i++)
            {
                float seg = Vector2.Distance(route[i - 1], route[i]);
                if (seg > 0f && want <= seg)
                {
                    dir = (route[i] - route[i - 1]) / seg;
                    return route[i - 1] + dir * want;
                }
                want -= seg;
            }
            dir = (route[route.Count - 1] - route[0]).normalized;
            return route[route.Count - 1];
        }

        // ── M13 界面 ─────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(5111);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionTeamService.TryRename(s, "测试队", out _);
            ExpeditionTeamService.TrySetColor(s, 3);
            GameObject go = null;
            try
            {
                VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out go);
                WorldBarHudUIToolkit hud = go.AddComponent<WorldBarHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh();
                bool idle = hud.MarchStatusText == GameText.Format("exp.march.bar", "测试队", GameText.Get("exp.state.standby")) && hud.MarchAdvanceButton.ClassListContains("uk-hidden")
                            && hud.MarchFollowButton.ClassListContains("uk-hidden");

                ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
                CampaignState now = CampaignSession.Current;
                WorldSimulation.StepMany(30);
                hud.Refresh();
                Color team = ExpeditionTeamService.ColorOf(3);
                bool row = dep.Success && hud.MarchRowShown && hud.MarchStatusText.StartsWith("测试队", StringComparison.Ordinal)
                           && hud.MarchStatusText.Contains(GameText.Get("exp.state.marching")) && ColorClose(hud.MarchSwatchStyleColor, team)
                           && !hud.MarchAdvanceButton.ClassListContains("uk-hidden") && hud.MarchHoldButton.enabledSelf && hud.MarchFollowButton.text == GameText.Get("exp.cmd.follow");
                // 世界栏按钮（正式入口）：待命 → 内核守备命令。
                CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
                bool clickedHold = Click(hud.MarchHoldButton) && home.TryGetCommand(Unit(home, a), out CombatCommand ca) && ca.Kind == CombatCommandKind.Guard;
                bool clickedAdv = Click(hud.MarchAdvanceButton) && ExpeditionMarchService.StatusOf(now) == ExpeditionMarchService.TeamStatus.Marching;
                WorldSimulation.StepMany(20); // 等寻路交回路线（6 步固定延迟）
                hud.Refresh();
                double etaNow = ExpeditionMarchService.EtaSeconds(now);
                bool eta = etaNow > 0 && hud.MarchStatusText == GameText.Format("exp.march.bar_eta", "测试队", GameText.Get("exp.state.marching"), GameClock.FormatGameDuration(etaNow));
                Expect(idle && row && clickedHold && clickedAdv && eta,
                    $"M13 世界栏远征行军条（FGR-EXP-012 HUD 可见）：在家时“{GameText.Format("exp.march.bar", "测试队", GameText.Get("exp.state.standby"))}”、行军时“{hud.MarchStatusText}”（队名、状态、预计到达时间、队伍颜色色块）；按钮“待命 / 前进”走正式命令（{clickedHold} / {clickedAdv}）");

                // 跟随镜头（一键跟随远征队）。
                WorldView.EnsureCamera();
                InputRouter.DebugSetReader(new SilentReader()); // 没有鼠标在屏幕边缘（边缘平移会按玩家平移停止跟随）
                bool follow = Click(hud.MarchFollowButton) && WorldView.FollowingExpedition;
                for (int i = 0; i < 30; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                InputRouter.DebugSetReader(null);
                ExpeditionMarchService.TryTeamCenter(now, out Vector2 center, out _);
                float2 focus = WorldView.Director.StrategyFocus;
                bool tracks = follow && math.distance(focus, new float2(center.x, center.y)) < 6f;
                bool stop = Click(hud.MarchFollowButton) && !WorldView.FollowingExpedition;
                Expect(tracks && stop, $"M13 “跟随远征队”（开始跟随 {follow}）：镜头跟着队伍中心走（焦点距队伍 {math.distance(focus, new float2(center.x, center.y)):0.0} 格：{tracks}），再点一次停止（{stop}）");

                // 覆盖外：按钮置灰，原因行写原因与最后的命令。
                SignalCoverageService.OverrideForTests = (site, p) => site == HomeValleyLayout.RegionId
                    ? new SignalCoverageSample(false, true, -50f, Vector2.zero, 150f, SignalCoverageSourceKind.Core)
                    : (SignalCoverageSample?)null;
                hud.Refresh();
                bool greyed = !hud.MarchHoldButton.enabledSelf && hud.MarchReasonText.StartsWith(GameText.Get("exp.march.reason.out_of_coverage"), StringComparison.Ordinal)
                              && hud.MarchReasonText.Contains(GameText.Format("exp.march.last_order", GameText.Get("exp.cmd.advance")));
                string reasonShown = hud.MarchReasonText;
                SignalCoverageService.OverrideForTests = null;
                hud.Refresh();
                Expect(greyed, $"M13 覆盖外：远程命令按钮置灰，原因行“{reasonShown}”写明收不到新命令与最后的命令（覆盖用注入采样只为界面这一条；行为见 M8）");

                // 战略地图：远征队图标（队伍颜色）、剩余路线（队伍颜色）、目的地锚点。
                ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
                GridCell core = HomeGridService.CorePivot(now);
                var model = new WorldMapModel();
                var view = new WorldMapView { CenterX = (core.X + anchor.x) / 2, CenterY = (core.Y + anchor.y) / 2, HalfWidth = 800, CanvasWidth = 1000, CanvasHeight = 800 };
                WorldMapFilters.Set(WorldMapLayer.Own, true);
                model.Collect(now, view, 400);
                WorldMapItem icon = model.Items.FirstOrDefault(i => i.Kind == WorldMapItemKind.Expedition);
                bool iconOk = icon.Id != null && ColorClose(icon.Tint, team) && icon.Label.StartsWith("测试队", StringComparison.Ordinal);
                bool goal = model.Items.Any(i => i.Kind == WorldMapItemKind.ExpeditionGoal && Math.Abs(i.X - anchor.x) < 0.5 && Math.Abs(i.Y - anchor.y) < 0.5);
                int routeLines = model.Lines.Count(l => ColorClose(l.Tint, team));
                Expect(iconOk && goal && routeLines >= 1,
                    $"M13 战略地图（FG08 第 7 节）：远征队图标用队伍颜色、标签“{icon.Label}”；剩余路线 {routeLines} 段用队伍颜色；目的地锚点标出（{goal}）");

                // 名册 + 关注点条。
                bool rosterTxt = MachineRoster.PlaceText(now, Rec(a)) == GameText.Format("exp.roster.place", GameText.Get("exp.state.marching"));
                bool focusItem = WorldView.FocusTargets(now).Any(t => t.Id == WorldView.ExpeditionFocusId && t.Label.StartsWith("测试队", StringComparison.Ordinal));
                // 进地点后：跟随按钮变“前往远征地点”，点了镜头切过去。
                ExpeditionMarchService.ArriveNowForTests(now, observeSite: false);
                hud.Refresh();
                bool gotoText = hud.MarchFollowButton.text == GameText.Get("exp.cmd.goto_site") && hud.MarchAdvanceButton.ClassListContains("uk-hidden");
                bool gone = Click(hud.MarchFollowButton) && WorldView.ObservedSiteId == FracturedCityLayout.RegionId;
                bool siteText = hud.MarchStatusText.Contains(GameText.Get("world.site." + FracturedCityLayout.RegionId + ".name"));
                Expect(rosterTxt && focusItem && gotoText && gone && siteText,
                    $"M13 名册写“{GameText.Format("exp.roster.place", GameText.Get("exp.state.marching"))}”（{rosterTxt}）、关注点条有远征队（{focusItem}）；进地点后行军条“{hud.MarchStatusText}”、按钮变“前往远征地点”并能切过去（{gotoText && gone}）");

                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", "WorldBar", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("WorldBar")?.RemoveFromClassList("uk-hidden");
                    foreach (string n in new[] { "WorldMarchAdvance", "WorldMarchHold", "WorldMarchReturn", "WorldMarchFollow", "WorldMarchReason" })
                    {
                        rr.panel.visualTree.Q<VisualElement>(n)?.RemoveFromClassList("uk-hidden");
                    }
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "M13 世界栏（含远征行军条全部按钮与原因行）布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
        }

        // ── M14 旧档兼容 ─────────────────────────────────────────────────────────

        private static void CheckLegacySave()
        {
            CampaignState s = NewWorld(5112);
            string json = JsonUtility.ToJson(s.Grid.ExpeditionTeam);
            // 本 Story 之前的档：远征队没有 March / Anchors 字段。
            var old = JsonUtility.FromJson<ExpeditionTeamState>("{\"Name\":\"\",\"ColorIndex\":2,\"Members\":[]}");
            s.Grid.ExpeditionTeam = old;
            CampaignFgStateDomains.EnsureAll(s);
            bool defaults = s.Grid.ExpeditionTeam.March != null && s.Grid.ExpeditionTeam.March.Phase == ExpeditionMarchService.PhaseNone
                            && s.Grid.ExpeditionTeam.Anchors != null && s.Grid.ExpeditionTeam.ColorIndex == 2 && json.Contains("\"March\"");
            // 读档时正在远征地点里的旧档（地点载入了、行军记录是空的）→ 记为“在地点”。
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(2);
            GameRoot.StartFracturedCity(new[] { a });
            WorldSimulation.StepMany(2);
            ExpeditionMarchState m = ExpeditionMarchService.Current(CampaignSession.Current);
            bool adopted = m.Phase == ExpeditionMarchService.PhaseAtSite && m.SiteId == FracturedCityLayout.RegionId && m.Members.Contains(a) && ExpeditionMarchService.IsAway(CampaignSession.Current);
            Expect(defaults && adopted,
                $"M14 旧档兼容（只加字段、不升域版本）：没有行军 / 锚点字段的旧档读进来是“没有远征在外”、锚点用到时再解析（{defaults}）；读档时已在远征地点里的旧档记为“在地点”（{adopted}）");
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        /// <summary>新世界：家园载入、破碎都市解锁（测试捷径：直接置 Available；真实解锁由 FG1 / FG5 覆盖）。信号塔不修（覆盖只有归还核心的 150 格）。</summary>
        // ── M21 星球上的交战（FG8-E2E-01 修复：FGJ-M8 实跑发现远征队对驻军 / 精英 / 巡逻不还手、右键敌人只会移动）──

        /// <summary>
        /// M21：（a）行军中（在执行“前进”）身边的精英残部醒来交战，行军中的成员不改道（不替玩家改命令）；（b）“待命”的成员按 AI 教义对 engage_radius 内最近的敌人下内核攻击命令、
        /// 真的把它打死（内核击毁事件 → 野外掉落 + 精英数据包）；（c）右键敌人 = 攻击也认家园战斗内核里的敌对单位（选中 → 右键敌人位置 → 攻击命令、目标就是这个单位）。
        /// </summary>
        private static void CheckSelfDefense()
        {
            CampaignState s = NewWorld(5121);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionSiteAnchors.EnsureResolved(s);
            FgExpeditionChecklistSelfCheck.PlaceRelayChain(s, ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId), "fgmarch_relay_d");
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"M21 前提：出发失败（{dep.Outcome}：{string.Join("；", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            RaidUnitDef def = RaidCatalog.Units.Where(u => u != null && u.Faction == "silent").OrderBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault() ?? RaidCatalog.Units.First();

            // （a）行军中：身边刷一处 1 台精英的残部（与正式精英残部同一入口），它醒来交战；行军中的成员不改道（仍往锚点走、不下自卫命令）。
            ExpeditionMarchService.TryTeamCenter(now, out Vector2 center, out _);
            GridCell rc = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(center.x + 6f), Mathf.RoundToInt(center.y)), 6);
            OutpostRecord remnant = WorldOutpostSystem.SpawnRemnant(now, "remnant:silent:fgmarch_m21", "silent", rc, 1, 1);
            int sd0 = ExpeditionMarchService.SelfDefenseCount;
            int packets0 = LootPickService.Count(now);
            long kills0 = FieldLootService.KillEvents;
            bool woke = StepUntil(() => remnant != null && remnant.Engaged && home.CountSiegeGroup(remnant.EngageKey) > 0, GameClock.StepHz * 6, 5);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool keepMarching = woke && CommandTowards(home, a, anchor, 6f) && CommandTowards(home, b, anchor, 6f) && ExpeditionMarchService.SelfDefenseCount == sd0;

            // （b）待命：能开火的成员按 AI 教义（内核守点教义）交战并真的把它打死（击毁事件 → 野外掉落 + 精英数据包）。
            // 复审修复（P1）的负向断言：没有攻击出口的 ERC-001 不交给教义（不接攻击命令、不设守点，“待命”的守备命令一直保留）；
            // 交战的成员不离开守备点超过 engage_radius（追击有边界）；打完回到守备点并恢复“待命”。
            bool held = ExpeditionMarchService.IssueCommand(now, ExpeditionMarchService.Command.Hold, out string why);
            WorldSimulation.StepMany(2);
            int ua = Unit(home, a);
            int ub = Unit(home, b);
            bool capable = home.CanMachineAttack(a) && !home.CanMachineAttack(b);
            Vector2 postA = home.TryGetCommand(ua, out CombatCommand ga) && ga.Kind == CombatCommandKind.Guard ? new Vector2((float)ga.Pos.x, (float)ga.Pos.y) : default;
            float radius = ExpeditionMarchService.EngageRadius;
            float maxAway = 0f;
            bool bBad = false;
            bool bDead = false;
            int bSamples = 0;
            string bWhy = null;
            void Watch()
            {
                if (home.TryGetMachinePosition(a, out Vector2 pa))
                {
                    maxAway = Mathf.Max(maxAway, Vector2.Distance(pa, postA));
                }
                if (!home.IsUnitAlive(ub))
                {
                    bDead = true; // 被精英打毁（不是教义问题）：之后不再采样
                    return;
                }
                if (home.TryGetMachinePosition(b, out Vector2 pbb) && home.FindNearestHostile(pbb, radius) > 0)
                {
                    bSamples++; // 旧实现在这种时刻会给它下攻击命令
                }
                bool hasB = home.TryGetCommand(ub, out CombatCommand cbb);
                bool bAttack = hasB && cbb.Kind == CombatCommandKind.Attack;
                bool bGuardCmd = hasB && cbb.Kind == CombatCommandKind.Guard;
                bool bPost = home.TryGetMachineGuard(b, out _, out float rb) && rb > 0f;
                if ((bAttack || bPost || !bGuardCmd) && bWhy == null)
                {
                    bWhy = $"命令 {(hasB ? cbb.Kind.ToString() : "无")}、守点半径 {rb:0.0}";
                }
                bBad |= bAttack || bPost || !bGuardCmd;
            }
            bool engaged = StepUntil(() =>
            {
                Watch();
                return home.TryGetMachineGuard(a, out Vector2 pg, out float rg) && rg > 0f && Vector2.Distance(pg, postA) < 0.5f
                       && (!home.TryGetCommand(ua, out CombatCommand c0) || c0.Kind == CombatCommandKind.None);
            }, GameClock.StepHz * 20, 5);
            bool killed = StepUntil(() =>
            {
                Watch();
                return remnant != null && remnant.Destroyed;
            }, GameClock.StepHz * 60, 10);
            bool byMachine = FieldLootService.KillEvents > kills0 && LootPickService.Count(now) > packets0;
            bool restored = StepUntil(() =>
            {
                Watch();
                return home.TryGetCommand(ua, out CombatCommand c1) && c1.Kind == CombatCommandKind.Guard && DistanceToGoal(home, a, postA) <= 2f;
            }, GameClock.StepHz * 30, 10);
            bool bounded = maxAway <= radius + 1.5f;
            Expect(remnant != null && woke && keepMarching && held && capable && engaged && killed && byMachine && ExpeditionMarchService.SelfDefenseCount > sd0,
                $"M21 星球上的交战（AI 教义自卫）：行军途中身边的精英残部醒来交战（{woke}），行军中的成员不改道（仍往锚点走 {keepMarching}）；下“待命”后（{held}{(held ? string.Empty : "：" + why)}）" +
                $"能开火的成员把守备点让给内核守点教义（{engaged}，半径 {radius:0} 格，自卫 {ExpeditionMarchService.SelfDefenseCount - sd0} 成员·秒）并把它打死（残部击溃 {killed}；击毁事件走野外掉落、精英留下数据包 {byMachine}）；开火能力判定 {capable}");
            Expect(capable && !bBad && bSamples >= 3 && bounded && restored,
                $"M21 负向（复审 P1）：没有攻击出口的 ERC-001 不接攻击命令、不设守点、“待命”守备命令一直保留（{!bBad}{(bWhy != null ? "：" + bWhy : string.Empty)}{(bDead ? "；ERC-001 中途被精英打毁，之后不采样" : string.Empty)}；它身边 {radius:0} 格内有敌人的采样 {bSamples} 次，至少 3 次）；交战成员离守备点最远 {maxAway:0.0} 格（上限 {radius + 1.5f:0.0}，{bounded}）；" +
                $"打完回到守备点 2 格内并恢复“待命”（{restored}）");

            // （c）右键敌人 = 攻击：核心旁一台家园机器（在覆盖里），选中它、右键敌人位置 → 攻击命令，目标就是那个内核单位。
            HomeValleyController hv = WorldSimulation.Home;
            int e = Spawn(now, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(-6f, -8f));
            WorldSimulation.StepMany(3);
            home.TryGetMachinePosition(e, out Vector2 pb);
            int u3 = SiegeService.SpawnOne(home, 990003, pb + new Vector2(10f, 0f), pb + new Vector2(10f, 0f), def, false);
            home.TryGetUnitPosition(u3, out double2 p3);
            hv.SquadCommands.ClearSelection();
            hv.SquadCommands.SelectSingle(e);
            hv.SquadCommands.IssueContextCommand(Vector3.zero, new Vector2((float)p3.x, (float)p3.y), false);
            bool rcAttack = home.TryGetCommand(Unit(home, e), out CombatCommand cb) && cb.Kind == CombatCommandKind.Attack && cb.Target == u3;
            hv.SquadCommands.ClearSelection();
            Expect(u3 > 0 && rcAttack,
                $"M21 右键敌人 = 攻击（FG-GAP-022）也认家园战斗内核里的敌对单位：选中 {MachineNaming.Short(e)} 右键敌人 → {cb.Kind} 命令，目标 {cb.Target}（应为 {u3}）");
        }

        private static CampaignState NewWorld(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgmarch-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return s;
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, core + offset, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static int Unit(CombatSite site, int logicId) => site != null && site.TryGetMachineUnit(logicId, out int u) ? u : 0;

        private static bool CommandTowards(CombatSite site, int logicId, Vector2 goal, float tolerance)
        {
            int unit = Unit(site, logicId);
            return unit != 0 && site.TryGetCommand(unit, out CombatCommand cmd) && (cmd.Kind == CombatCommandKind.Move)
                   && math.distance(cmd.Pos, new double2(goal.x, goal.y)) <= tolerance;
        }

        private static float DistanceToGoal(CombatSite site, int logicId, Vector2 goal) =>
            site != null && site.TryGetMachinePosition(logicId, out Vector2 p) ? Vector2.Distance(p, goal) : float.MaxValue;

        private static bool HostilesNearTeam()
        {
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            return home != null && ExpeditionMarchService.TryTeamCenter(CampaignSession.Current, out Vector2 c, out _)
                   && home.Kernel.FindNearest(new double2(c.x, c.y), 60f, CombatFaction.Hostile) != 0;
        }

        private static bool ColorClose(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;

        private static bool StepUntil(Func<bool> done, int maxSteps, int chunk = 10, Action<int> onSteps = null)
        {
            for (int i = 0; i < maxSteps; i += chunk)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(chunk);
                onSteps?.Invoke(chunk);
            }
            return done();
        }

        /// <summary>出发并走一段（约 10 游戏秒）后存档：<paramref name="withRaiders"/> 时在路边放几个敌人（真实交战，FGT-EXP-012）。</summary>
        private static bool BuildMarchSave(int seed, bool withRaiders)
        {
            CampaignState s = NewWorld(seed);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f));
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a, b }, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            if (!dep.Success)
            {
                Fail($"行军存档前提：出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return false;
            }
            CampaignState now = CampaignSession.Current;
            WorldSimulation.StepMany(600);
            if (withRaiders && ExpeditionMarchService.TryTeamCenter(now, out Vector2 c, out _) && ExpeditionSiteAnchors.TryGet(now, FracturedCityLayout.RegionId, out Vector2 anchor))
            {
                CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
                Vector2 ahead = c + (anchor - c).normalized * 30f;
                CombatBench.Spec spec = CombatBench.FromTuning();
                CombatBench.SpawnRaidGroup(home, ahead, c, 3, spec);
            }
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("行军存档写入失败：" + r.Message);
                return false;
            }
            return true;
        }

        private static void RestoreSave(int fromSlot)
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
                Fail("读行军存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
        }

        /// <summary>一直推进到远征队进入地点（返回那一步；到不了返回 -1）。</summary>
        private static long RunToArrival()
        {
            bool ok = StepUntil(() => ExpeditionMarchService.Current(CampaignSession.Current).Phase == ExpeditionMarchService.PhaseAtSite, 20000, chunk: 1);
            return ok ? GameClock.Ticks : -1;
        }

        private static string ArrivalDigest()
        {
            CampaignState s = CampaignSession.Current;
            var sb = new StringBuilder();
            sb.Append("tick=").Append(GameClock.Ticks).Append(";site=").Append(WorldSimulation.ActiveExpedition?.SiteId);
            sb.Append(";march=").Append(JsonUtility.ToJson(ExpeditionMarchService.Current(s)));
            foreach (MachineRecord m in (s.MachineRecords ?? Array.Empty<MachineRecord>()).OrderBy(x => x.LogicId))
            {
                sb.Append(";m=").Append(m.LogicId).Append('@').Append(m.RegionId).Append(':').Append(m.Health.ToString("R"));
            }
            sb.Append(";n=").Append(MachineRegistry.AllRecords.Count());
            return sb.ToString();
        }

        private static string Digest(CampaignState s)
        {
            var sb = new StringBuilder();
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            sb.Append("tick=").Append(GameClock.Ticks).Append(";home=").Append(home?.Kernel.StateHash().ToString("X16"));
            sb.Append(";nav=").Append(NavService.Kernel?.PendingHash().ToString("X16"));
            sb.Append(";march=").Append(JsonUtility.ToJson(ExpeditionMarchService.Current(s)));
            sb.Append(";anchors=").Append(string.Join("|", (s.Grid.ExpeditionTeam.Anchors ?? Array.Empty<ExpeditionSiteAnchorRecord>()).Select(a => a.SiteId + a.X + "," + a.Y)));
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                sb.Append(";g=").Append(JsonUtility.ToJson(g));
            }
            foreach (MachineRecord m in (s.MachineRecords ?? Array.Empty<MachineRecord>()).OrderBy(x => x.LogicId))
            {
                sb.Append(";m=").Append(m.LogicId).Append('@').Append(m.RegionId);
            }
            sb.Append(";site=").Append(WorldSimulation.ActiveExpedition?.SiteId);
            return sb.ToString();
        }

        private static bool Click(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private sealed class SilentReader : IInputReader
        {
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static void ExpectPerf(bool functionalOk, string message, params PerfGate.Metric[] perf) =>
            PerfGate.Expect(functionalOk, message, perf, Expect, Line);

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
