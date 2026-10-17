using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
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
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG7-ENV-05 迷雾、探索与家园遗迹的自动验收（FG07 FGR-ENV-050 / 051 / 060；验收 FGT-ENV-007）。
    /// 全部起真实系统：真实统一时钟与世界模拟（机器探索在家园模拟步里、信号覆盖在步首评估）、真实信号塔覆盖、真实寻路与工单（机器走过去回收）、
    /// 真实经济账本（技术数据）、真文件存读档、真 UXML 战略地图。
    /// D1 数据；F1 FGT-ENV-007 迷雾不能建 → 信号塔覆盖后能建、迷雾外区块按需生成（三个种子）；F2 覆盖半径变大后迷雾跟着揭开（DEBT-FG7ENV04-06）；
    /// M1 机器沿走过的路探索（采样补缝、瞬移不补、真实行走）；R1 遗迹点发现与通知（开局 / 旧档不补发、去重、一次揭开很多时合并、种子决定）；
    /// R2 回收（右键指定机器 / 地图派空闲机器、技术数据入账、背景资料、统计、标记）；H1 区域危害（领地 / 外圈危害带、悬停说明、第一次揭开通知、后幕领地揭开后显示）；
    /// N1 负向矩阵；T1 暂停与 0.5x～3x；T2 观察一致；S1 真文件存读档；U1 界面（战略地图图例 / 悬停 / 遗迹面板、布局探针、英文）；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgExplorationSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgExplorationSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgExplorationSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static HomeValleyController _home;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 迷雾、探索与家园遗迹")]
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
            Line("\n[迷雾、探索与家园遗迹] 迷雾拦建造 / 信号塔覆盖与机器沿路探索、迷雾外区块按需生成、遗迹点发现与回收、区域危害标记 / 图例 / 悬停、倍速 / 观察 / 存读档、界面（FG7-ENV-05）");
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
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgexplore-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                Campaign.Content.FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                HomeExplorationService.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "探索 / 遗迹 / 危害都在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），只在真正揭开新区域、悬停 / 右键时发生；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFogAndTower);
                Step(CheckCoverageGrowth);
                Step(CheckMachineTrail);
                Step(CheckRelicDiscovery);
                Step(CheckRecovery);
                Step(CheckHazards);
                Step(CheckNegative);
                Step(CheckTimingMatrix);
                Step(CheckObserved);
                Step(CheckSaveLoad);
                Step(CheckUi);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"迷雾与探索自检抛异常：{e}");
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
                WorldPlanetView.Shutdown();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                ResetServices();
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                _home = null;
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
            Line($"  · [迷雾、探索与家园遗迹] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);

        private static void ResetServices()
        {
            PurificationService.ResetForTests();
            WeatherService.ResetForTests();
            RoofService.ResetForTests();
            SilentNightService.ResetForTests();
            StandingRuleService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            CalendarService.ResetForTests();
            DayNightCycle.ResetForTests();
            PowerEnvironment.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            IntelService.ResetSessionState();
            ResearchService.ResetForTests();
            OverlayService.ResetForTests();
            HomeExplorationService.ResetForTests();
        }

        /// <summary>新战役（主菜单“新建”同一条 CampaignState.CreateNew → LoadHome 路径），天气 / 净化走正式入口，开局发电机修好（建筑有电）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            ResetServices();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgexplore-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            _home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(_home.SiteId);
            }
            s.Scrap = 600;
            SignalCoreService.EnsureInitialized(s);
            SignalCoverageService.Invalidate();
            WeatherService.Install();
            PurificationService.Install();
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator || b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation))
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(1);
            return s;
        }

        private static BuildingRecord Tower(CampaignState s) => s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);

        /// <summary>场景夹具：把开局的信号塔修好通电（修理工单本身由 FG1-SIG-07 / FG4-ECO-05 覆盖）。覆盖在下一次步首评估时生效。</summary>
        private static void RepairTower(CampaignState s)
        {
            BuildingRecord t = Tower(s);
            t.ConstructionState = BuildingConstructionState.Operational;
            t.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
        }

        /// <summary>走到下一次信号覆盖评估（步首，每 signal.coverage.refresh_seconds 游戏秒一次）。</summary>
        private static void Boundary()
        {
            int e0 = SignalCoverageService.EvaluateCount;
            int g = 0;
            while (SignalCoverageService.EvaluateCount == e0 && g++ < 120)
            {
                WorldSimulation.StepMany(1);
            }
        }

        private static bool StepUntil(Func<bool> done, long maxSteps)
        {
            int chunk = Math.Max(1, Hz / 10);
            for (long i = 0; i < maxSteps; i += chunk)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(chunk);
            }
            return done();
        }

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static string LastDetail(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId)?.Latest?.Detail ?? string.Empty;

        private static string AllDetails(string typeId) =>
            string.Join(" | ", NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Select(e => e.Latest?.Detail ?? string.Empty));

        /// <summary>
        /// 领地危害的取样点：有领地模板的世界（FG8-GEN-02 起，生成器 v3）领地里只有模板分布的危害区算危害（FGR-GEN-023），取这块领地第一个危害区的圆心；
        /// 没有模板的旧世界（v1 / v2）整块领地都算，取领地中心（与 FG7-ENV-05 一致）。
        /// </summary>
        private static Vector2Int HazardProbe(WorldPlan plan, PlannedTerritory t)
        {
            if (plan != null && plan.HasTemplates && plan.TemplateFor(t.Id) != null)
            {
                PlannedHazardZone z = plan.HazardZones.FirstOrDefault(h => h.TerritoryId == t.Id);
                if (z != null)
                {
                    return new Vector2Int(z.CenterX, z.CenterY);
                }
            }
            return new Vector2Int(t.CenterX, t.CenterY);
        }

        private static List<WorldFeature> Relics(CampaignState s, int half)
        {
            GridCell c = HomeGridService.CorePivot(s);
            var list = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, c.X - half, c.Y - half, c.X + half, c.Y + half, list);
            return list.Where(f => f.Kind == WorldFeatureKind.Relic).OrderBy(f => f.Distance).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>离 <paramref name="around"/> 最近、在迷雾里且除了迷雾没有别的阻挡的格（放电塔）。按种子地形找（B25）。</summary>
        private static GridCell? FindFogCell(CampaignState s, Vector2 around, int radius)
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
                        GridPlacementResult v = HomeGridService.ValidatePlacement(s, "power_pole", c, 0, checkCost: false);
                        if (v.Reasons.Count > 0 && v.Reasons.All(x => x.Code == GridBlockReason.Fog))
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        private static bool HasFog(GridPlacementResult v) => v.Reasons.Any(x => x.Code == GridBlockReason.Fog);

        private static string Masked(string json) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-f]{32}", "#"), ":(build|recover|demolish|salvage):[0-9a-f]{8}", ":$1:#"),
                "\"[A-Za-z]*Utc\":\"[^\"]*\"", "\"Utc\":\"#\""),
            "\"HasLocation\":(true|false),\"RegionId\":\"[^\"]*\"", "\"HasLocation\":$1,\"RegionId\":\"#\""), "\\s+", " ");

        private static string FirstDiff(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 80);
            return $"第 {i} 字符：…{a.Substring(from, Math.Min(160, a.Length - from))}… ≠ …{b.Substring(from, Math.Min(160, b.Length - from))}…";
        }

        // ── D1 数据 ──────────────────────────────────────────────────────────────

        private static string Py(float v)
        {
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_explore as p;print('\\n'.join(l for l in p.dump_lines() if l.startswith('RLR') or l.startswith('HZD')))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("RLR\t", StringComparison.Ordinal) || l.StartsWith("HZD\t", StringComparison.Ordinal)).ToList();
            var rt = t.TbRelicRing.DataList.Select(r => string.Join("\t", "RLR", r.Id, r.MinDistance, r.TechData, Py(r.RecoverSeconds), r.NameKey, r.LoreKeys, r.SortOrder))
                .Concat(t.TbHazard.DataList.Select(h => string.Join("\t", "HZD", h.Id, h.TerritoryId, h.BeltKind, h.NameKey, h.EffectKey, h.CounterKey, h.Color, h.StoryId, h.SortOrder))).ToList();
            bool table = code == 0 && src.Count == 8 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            IReadOnlyList<RelicRing> rings = HomeExplorationService.Rings;
            bool ringsOk = rings.Count == 4 && rings[0].MinDistance == 0 && rings.Zip(rings.Skip(1), (a, b) => b.MinDistance > a.MinDistance && b.TechData >= a.TechData).All(x => x)
                           && HomeExplorationService.RingFor(0) == rings[0] && HomeExplorationService.RingFor(rings[1].MinDistance) == rings[1]
                           && HomeExplorationService.RingFor(rings[1].MinDistance - 1) == rings[0] && HomeExplorationService.RingFor(100000) == rings[3];
            bool hazards = HomeExplorationService.Hazards.Count == 4
                           && new[] { ("silent", "emi_noise"), ("foundry", "slag_heat"), ("clarity", "acid_marsh"), ("overclock", "heat_radiation") }
                               .All(p => HomeExplorationService.HazardForTerritory(p.Item1)?.Id == p.Item2)
                           && HomeExplorationService.HazardForBelt("acid")?.Id == "acid_marsh" && HomeExplorationService.HazardForBelt("heat")?.Id == "heat_radiation"
                           && HomeExplorationService.HazardForBelt("none") == null;
            bool notify = t.TbNotifyType.GetOrDefault("relic_found")?.AwaySection == "event" && t.TbNotifyType.GetOrDefault("relic_recovered")?.AwaySection == "research"
                          && t.TbNotifyType.GetOrDefault("hazard_found")?.AwaySection == "event";
            bool hooks = new[] { GuidanceHooks.EnvFirstRelicFound, GuidanceHooks.EnvFirstRelicRecovered, GuidanceHooks.EnvFirstHazardSeen, GuidanceHooks.EnvFirstFogBlocked }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool codex = new[] { "codex.env.exploration", "codex.env.hazard" }
                .All(id => t.TbCodexEntry.GetOrDefault(id) is var c && c != null && GameText.Has(c.TitleKey) && GameText.Has(c.BodyKey));
            bool tune = GridContent.TryGetTuning("explore.trail_step", out float step) && GridContent.TryGetTuning("grid.explore_machine_radius", out float rad) && step <= rad / 2f
                        && GridContent.TryGetTuning("explore.trail_gap_max", out float gap) && gap > step && GridContent.TryGetTuning("relic.pick_radius", out float pick) && pick > 0f;
            var keys = new List<string>
            {
                "relic.notify.found", "relic.notify.found_more", "relic.notify.recovered", "relic.hover.title", "relic.hover.distance", "relic.hover.available", "relic.hover.ordered",
                "relic.hover.ordered_pool", "relic.hover.ordered_machine", "relic.hover.recovered", "relic.hover.hint", "relic.reason.fog", "relic.reason.recovered", "relic.reason.ordered",
                "relic.reason.missing", "relic.order.machine", "relic.order.pool", "relic.work.label", "stats.research.src.relic", "hazard.hover.title_zone", "hazard.hover.title_belt",
                "hazard.hover.effect", "hazard.hover.counter", "hazard.hover.pending", "hazard.notify.found", "ui.map.legend.title", "ui.map.legend.hint", "ui.map.territory_hazard",
                "ui.map.relic.recovered", "ui.map.relic.ordered", "ui.map.relic.recover", "ui.map.rich_ore", "ui.map.relic.recover_all", "relic.order.pool_all",
                "relic.reason.none_recoverable",
            };
            foreach (RelicRing r in rings)
            {
                keys.Add(r.NameKey);
                keys.AddRange(r.LoreKeys.Split(','));
            }
            foreach (Hazard h in HomeExplorationService.Hazards)
            {
                keys.Add(h.NameKey);
                keys.Add(h.EffectKey);
                keys.Add(h.CounterKey);
            }
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            Expect(table && ringsOk && hazards && notify && hooks && codex && tune && missing.Count == 0,
                $"D1 数据：fg.TbRelicRing / fg.TbHazard 与源数据逐行一致（源 {src.Count} 行 / 表 {rt.Count} 行）{table}；四个距离环从 0 格起、越远技术数据不减、按距离取环 {ringsOk}；" +
                $"四个阵营领地各一种危害、酸沼带 / 高温辐射带对应酸沼 / 高温辐射 {hazards}；通知 3 类进离家报告 {notify}；引导钩子 {hooks}；图鉴“迷雾、探索与遗迹点 / 区域危害” {codex}；" +
                $"沿路采样间距 ≤ 探索半径一半 {tune}；中英文本齐全（缺 {string.Join("、", missing)}）");
        }

        // ── F1 FGT-ENV-007 迷雾：未探索不能建造；信号塔覆盖后可以建造 ────────────────

        private static void CheckFogAndTower()
        {
            foreach (int seed in new[] { 8801, 8802, 8803 })
            {
                CampaignState s = NewWorld(seed);
                HomeGridMap map = HomeGridService.MapFor(s);
                GridCell core = HomeGridService.CorePivot(s);
                BuildingRecord tower = Tower(s);
                float ang = (seed % 8) * 45f * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                GridCell? probeAt = FindFogCell(s, new Vector2(core.X, core.Y) + dir * 120f, 30);
                // 迷雾外的区块：离核心 240 格、还没生成过的一块（按需生成，FGR-GEN-050）。
                var far = new GridCell(core.X + Mathf.RoundToInt(-dir.y * 240f), core.Y + Mathf.RoundToInt(dir.x * 240f));
                ChunkAddress fa = GridMath.Address(far, map.ChunkSize);
                bool farUnloaded = map.TryGetLoaded(fa.ChunkX, fa.ChunkY) == null && !map.IsExploredNoLoad(far);
                if (probeAt == null)
                {
                    Fail($"F1 种子 {seed}：离核心 120 格附近找不到只被迷雾挡住的空地");
                    continue;
                }
                GridCell probe = probeAt.Value;
                GridPlacementResult before = HomeGridService.ValidatePlacement(s, "power_pole", probe, 0, checkCost: false);
                string fogText = before.Reasons.FirstOrDefault(x => x.Code == GridBlockReason.Fog).Describe() ?? string.Empty;
                int reveal0 = HomeGridService.RevealCount;
                RepairTower(s);
                Boundary();
                GridPlacementResult after = HomeGridService.ValidatePlacement(s, "power_pole", probe, 0, checkCost: false);
                bool towerCircle = s.Grid.Explored.Any(e => Math.Abs(e.CenterX - Mathf.RoundToInt(tower.Position.x)) <= 1 && Math.Abs(e.CenterY - Mathf.RoundToInt(tower.Position.y)) <= 1
                                                            && e.Radius >= 290);
                bool farExplored = map.IsExplored(far); // 查询这一格 = 按需生成它所在的区块
                bool farGenerated = map.TryGetLoaded(fa.ChunkX, fa.ChunkY) != null;
                Expect(HasFog(before) && fogText.Length > 0 && after.Ok && towerCircle && HomeGridService.RevealCount > reveal0 && farUnloaded && farExplored && farGenerated,
                    $"F1 FGT-ENV-007 种子 {seed}：离核心 120 格的 {probe} 在迷雾里放电塔被拒（原因“{fogText}”）；信号塔修好通电后它的覆盖圆（≥ 290 格）记为已探索，同一格可以建造；" +
                    $"离核心 240 格、之前没生成过的区块（{fa.ChunkX}, {fa.ChunkY}）揭开后第一次查询时才生成 {farUnloaded}/{farGenerated}");
            }
        }

        // ── F2 覆盖半径变大后迷雾跟着揭开（DEBT-FG7ENV04-06）─────────────────────────

        private static void CheckCoverageGrowth()
        {
            CampaignState s = NewWorld(8805);
            RepairTower(s);
            Boundary();
            BuildingRecord tower = Tower(s);
            float r0 = s.Grid.Explored.Where(e => Math.Abs(e.CenterX - Mathf.RoundToInt(tower.Position.x)) <= 1 && Math.Abs(e.CenterY - Mathf.RoundToInt(tower.Position.y)) <= 1)
                .Select(e => (float)e.Radius).DefaultIfEmpty(0f).Max();
            HomeGridMap map = HomeGridService.MapFor(s);
            // 原覆盖圆外、新半径（×1.1）内的一格。
            var ring = new GridCell(Mathf.RoundToInt(tower.Position.x + r0 * 1.05f), Mathf.RoundToInt(tower.Position.y));
            bool fogBefore = !map.IsExploredNoLoad(ring);
            // 归还 50,000 格里程碑（信号覆盖 +10%）已达成：与 PurificationService 发奖励同一路径（记进存档 → 覆盖网络失效重建）。
            PurificationState ps = PurificationService.StateOf(s);
            ps.Reached = ps.Reached.Append("m50000").ToArray();
            ps.ReachedTicks = ps.ReachedTicks.Append(GameClock.Ticks).ToArray();
            SignalCoverageService.Invalidate();
            Boundary();
            float r1 = s.Grid.Explored.Where(e => Math.Abs(e.CenterX - Mathf.RoundToInt(tower.Position.x)) <= 1 && Math.Abs(e.CenterY - Mathf.RoundToInt(tower.Position.y)) <= 1)
                .Select(e => (float)e.Radius).DefaultIfEmpty(0f).Max();
            bool open = map.IsExploredNoLoad(ring);
            Expect(r0 >= 290f && fogBefore && Math.Abs(r1 - Mathf.Floor(r0 * 1.1f)) <= 1.5f && open,
                $"F2 归还 50,000 格后信号覆盖 +10%：信号塔覆盖圆从 {r0} 格记成 {r1} 格，原覆盖外的 {ring} 跟着揭开（之前在迷雾里 {fogBefore}）——关闭 DEBT-FG7ENV04-06");
        }

        // ── M1 机器沿走过的路探索 ─────────────────────────────────────────────────

        private static Vector2 ClearDirection(CampaignState s, GridCell core, float from, float to)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            byte water = GridContent.TerrainCode("water");
            for (int a = 0; a < 32; a++)
            {
                float ang = a * (360f / 32f) * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                bool clear = true;
                for (float k = from; k <= to && clear; k += 1f)
                {
                    var c = new GridCell(Mathf.RoundToInt(core.X + d.x * k), Mathf.RoundToInt(core.Y + d.y * k));
                    byte t = map.GetTerrain(c);
                    clear = t != cliff && t != water && HomeGridService.BuildingAt(s, c) == null;
                }
                if (clear)
                {
                    return d;
                }
            }
            return Vector2.zero;
        }

        private static bool LineExplored(HomeGridMap map, Vector2 o, Vector2 d, float from, float to, out float firstGap)
        {
            firstGap = -1f;
            for (float k = from; k <= to; k += 1f)
            {
                if (!map.IsExploredNoLoad(GridCell.FromWorld(o + d * k)))
                {
                    firstGap = k;
                    return false;
                }
            }
            return true;
        }

        private static void CheckMachineTrail()
        {
            CampaignState s = NewWorld(8811);
            GridCell core = HomeGridService.CorePivot(s);
            var o = new Vector2(core.X, core.Y);
            HomeValleyMachineMarker m = _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid);
            if (m == null || !MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec))
            {
                Fail("M1 家园没有可用的机器");
                return;
            }
            Vector2 d = ClearDirection(s, core, 25f, 80f);
            if (d == Vector2.zero)
            {
                d = Vector2.right;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            // 采样补缝：上一次检查在 30 格处，这次在 70 格处（中间 40 格从没被检查过）。
            rec.ExploreHasLast = false;
            MachineExploration.TryRevealAlong(s, m.LogicId, o + d * 30f);
            bool gapBefore = !map.IsExploredNoLoad(GridCell.FromWorld(o + d * 52f));
            int samples0 = MachineExploration.TrailSamples;
            MachineExploration.TryRevealAlong(s, m.LogicId, o + d * 70f);
            bool continuous = LineExplored(map, o, d, 0f, 78f, out float gap1);
            bool lastSaved = rec.ExploreHasLast && Vector2.Distance(rec.ExploreLast, o + d * 70f) < 0.01f;
            // 瞬移：再一次检查在 170 格处（距离 100 > explore.trail_gap_max），中间不补。
            MachineExploration.TryRevealAlong(s, m.LogicId, o + d * 170f);
            bool teleportGap = !map.IsExploredNoLoad(GridCell.FromWorld(o + d * 120f)) && map.IsExploredNoLoad(GridCell.FromWorld(o + d * 170f));
            // 对照：同一种子、只看脚下（FG3-LOG-01 的旧做法）——中间留缝。
            CampaignState c = NewWorld(8811);
            HomeGridMap cmap = HomeGridService.MapFor(c);
            MachineExploration.TryRevealAround(c, o + d * 30f);
            MachineExploration.TryRevealAround(c, o + d * 70f);
            bool controlGap = !LineExplored(cmap, o, d, 0f, 78f, out float gap2);
            Expect(gapBefore && continuous && lastSaved && teleportGap && controlGap && MachineExploration.TrailSamples > samples0,
                $"M1 机器沿走过的路探索：两次检查之间走了 40 格，沿路按 {GridContent.Tuning("explore.trail_step")} 格采样补探索，0～78 格整条线已探索（补了 {MachineExploration.TrailSamples - samples0} 个点）；" +
                $"对照只看脚下时第 {gap2} 格留缝；相距 100 格当作瞬移不补（第 120 格仍是迷雾）；检查点记进机器记录（存档） {lastSaved}" + (continuous ? string.Empty : $"；缝在第 {gap1} 格"));

            // 真实行走：选一台机器下令走进迷雾 80 格，模拟步里的机器探索（每步轮流检查一台）把它走过的每一处都记为已探索。
            d = Vector2.zero;
            m = null;
            foreach (int seed in new[] { 8811, 8812, 8813, 8814 })
            {
                s = NewWorld(seed);
                core = HomeGridService.CorePivot(s);
                o = new Vector2(core.X, core.Y);
                d = ClearDirection(s, core, 22f, 82f);
                m = _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid);
                if (d != Vector2.zero && m != null)
                {
                    break;
                }
            }
            if (m == null || d == Vector2.zero)
            {
                Fail("M1 真实行走：找不到机器或一条没有悬崖 / 水面的直线");
                return;
            }
            map = HomeGridService.MapFor(s);
            // 场景夹具：机器先站到开局探索圆里、离核心 22 格处（避开开局建筑），检查点清空。
            m.SetPosition(o + d * 22f);
            if (MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord wr))
            {
                wr.ExploreHasLast = false;
            }
            Vector2 target = o + d * 80f;
            bool fogAtTarget = !map.IsExploredNoLoad(GridCell.FromWorld(target));
            m.CommandMoveTo(new Vector3(target.x, 0f, target.y));
            var path = new List<Vector2>();
            for (int i = 0; i < 240 * Hz; i++)
            {
                WorldSimulation.StepMany(1);
                if (i % 3 == 0)
                {
                    path.Add(m.Position);
                }
                if (!m.IsMoving && i > Hz)
                {
                    break;
                }
            }
            path.Add(m.Position);
            float walked = Vector2.Distance(path[0], path[path.Count - 1]);
            var unexplored = path.Where(p => !map.IsExploredNoLoad(GridCell.FromWorld(p))).ToList();
            Expect(fogAtTarget && walked >= 45f && unexplored.Count == 0 && map.IsExploredNoLoad(GridCell.FromWorld(m.Position)),
                $"M1 真实行走：机器从核心旁走进迷雾 {walked:0} 格（目标 80 格处原是迷雾 {fogAtTarget}），记录的 {path.Count} 个轨迹点全部已探索（未探索 {unexplored.Count} 个）——走到哪里探索到哪里，不靠地格");
        }

        // ── R1 遗迹点发现与通知 ─────────────────────────────────────────────────────

        private static void CheckRelicDiscovery()
        {
            var lists = new Dictionary<int, string>();
            foreach (int seed in new[] { 8821, 8822, 8823 })
            {
                CampaignState s = NewWorld(seed);
                // 开局后（新战役通知历史清空 / 沿用都不影响）走 1 游戏秒：没有任何“发现遗迹点”。
                int startNotices = CountOf(HomeExplorationService.NotifyRelicFound);
                WorldSimulation.StepMany(Hz);
                HomeGridMap map = HomeGridService.MapFor(s);
                List<WorldFeature> relics = Relics(s, 700);
                lists[seed] = string.Join(",", relics.Select(r => r.Id + "@" + r.X + "," + r.Y));
                // 开局：开局探索圆里的遗迹点在第一次揭开新区域前静默补登（不发通知）；开局本身不发任何“发现遗迹点”。
                var startRelics = relics.Where(r => map.IsExploredNoLoad(new GridCell(r.X, r.Y))).ToList();
                bool quietStart = CountOf(HomeExplorationService.NotifyRelicFound) == startNotices;
                WorldFeature target = relics.FirstOrDefault(r => r.Distance >= 50 && r.Distance <= 400 && !map.IsExploredNoLoad(new GridCell(r.X, r.Y)));
                if (target == null)
                {
                    Fail($"R1 种子 {seed}：400 格内没有迷雾里的遗迹点");
                    continue;
                }
                bool hiddenBefore = !HomeExplorationService.IsFound(s, target.Id) && HomeExplorationService.RelicAt(s, new Vector2(target.X, target.Y)) == null;
                int n0 = CountOf(HomeExplorationService.NotifyRelicFound);
                HomeGridService.RevealArea(s, new Vector2(target.X + 3, target.Y), 10f);
                bool found = HomeExplorationService.IsFound(s, target.Id) && HomeExplorationService.RelicAt(s, new Vector2(target.X + 1, target.Y)) == target.Id;
                int n1 = CountOf(HomeExplorationService.NotifyRelicFound);
                string detail = LastDetail(HomeExplorationService.NotifyRelicFound);
                RelicRing ring = HomeExplorationService.RingFor(target.Distance);
                bool text = detail.Contains($"（{target.X}, {target.Y}）") && detail.Contains(GameText.Get(ring.NameKey)) && detail.Contains(target.Distance.ToString(CultureInfo.InvariantCulture));
                HomeGridService.RevealArea(s, new Vector2(target.X, target.Y), 30f);
                bool noRepeat = CountOf(HomeExplorationService.NotifyRelicFound) == n1;
                bool hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstRelicFound);
                bool baseline = quietStart && HomeExplorationService.StateOf(s).Baselined && startRelics.All(r => HomeExplorationService.IsFound(s, r.Id));
                Expect(baseline && hiddenBefore && found && n1 == n0 + 1 && text && noRepeat && hook,
                    $"R1 种子 {seed}：开局不发通知 {quietStart}，开局探索圆里的 {startRelics.Count} 个遗迹点在第一次揭开时静默补登 {baseline}；迷雾里的遗迹点 {target.Id}（{target.X}, {target.Y}，离核心 {target.Distance} 格，{GameText.Get(ring.NameKey)}）" +
                    $"在揭开前查不到 {hiddenBefore}，被揭开时记进存档并发一条通知“{detail}”，再揭开一次不重复通知 {noRepeat}，引导钩子 {hook}");

                // 一次揭开一大片（信号塔 300 格覆盖那种）：逐个发前 relic.notify_max 条，其余合成一条“又发现 N 个”。
                int max = Mathf.RoundToInt(GridContent.Tuning("relic.notify_max"));
                int f0 = HomeExplorationService.StateOf(s).RelicsFound.Length;
                int c0 = CountOf(HomeExplorationService.NotifyRelicFound);
                HomeGridService.RevealArea(s, new Vector2(HomeGridService.CorePivot(s).X, HomeGridService.CorePivot(s).Y), 640f);
                int newly = HomeExplorationService.StateOf(s).RelicsFound.Length - f0;
                int posted = CountOf(HomeExplorationService.NotifyRelicFound) - c0;
                bool inside = relics.Where(r => Vector2.Distance(new Vector2(r.X, r.Y), new Vector2(HomeGridService.CorePivot(s).X, HomeGridService.CorePivot(s).Y)) <= 638f)
                    .All(r => HomeExplorationService.IsFound(s, r.Id));
                bool merged = newly <= max ? posted == newly : posted == max + 1 && LastDetail(HomeExplorationService.NotifyRelicFound).Contains((newly - max).ToString(CultureInfo.InvariantCulture));
                Expect(newly > 0 && inside && merged,
                    $"R1 种子 {seed}：一次揭开 640 格新发现 {newly} 个遗迹点（圈内全部记为发现 {inside}），通知 {posted} 条（逐个最多 {max} 条，其余合成“又发现 N 个”）");
            }
            CampaignState again = NewWorld(8821);
            string replay = string.Join(",", Relics(again, 700).Select(r => r.Id + "@" + r.X + "," + r.Y));
            Expect(lists.Count == 3 && replay == lists[8821] && lists[8821] != lists[8822] && lists[8822] != lists[8823] && lists.Values.All(v => v.Length > 0),
                "R1 B25 遗迹点由世界种子决定：同一种子重开一次位置完全相同，三个种子各不相同（生成器按（种子, 分布格）放点，不写死坐标）");
        }

        // ── R2 回收 ──────────────────────────────────────────────────────────────

        private static void CheckRecovery()
        {
            CampaignState s = NewWorld(8831);
            RepairTower(s);
            Boundary();
            HomeGridMap map = HomeGridService.MapFor(s);
            List<WorldFeature> found = Relics(s, 320).Where(r => HomeExplorationService.IsFound(s, r.Id) && map.IsExploredNoLoad(new GridCell(r.X, r.Y))).ToList();
            HomeValleyMachineMarker m = _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid && MachineRegistry.TryGetRecord(x.LogicId, out MachineRecord r0)
                                                                                      && (r0.ChassisId == HomeValleyLayout.Erc001ChassisId || r0.ChassisId == HomeValleyLayout.Erc002ChassisId));
            if (found.Count < 2 || m == null)
            {
                Fail($"R2 信号塔覆盖内找不到两处遗迹点（{found.Count}）或搬运机");
                return;
            }
            // 右键：选中的机器 + 镜头到遗迹点的射线（与玩家右键同一条路径：情境命令 → 下令模板 → 赶路 → 到场工作）。
            WorldFeature a = found[0];
            HomeExplorationService.TryGetRelic(s, a.Id, out HomeExplorationService.RelicInfo ia);
            MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord mr);
            int jobs0 = mr.JobsCompleted;
            long tech0 = s.TechData;
            long income0 = TechDataFlow.IncomeOf(s, TechDataFlow.Relic);
            int rec0 = CountOf(HomeExplorationService.NotifyRelicRecovered);
            bool handled = _home.ContextRelicForTests(m, new Ray(new Vector3(a.X + 0.6f, 40f, a.Y), Vector3.down));
            WorkOrderRecord oa = HomeExplorationService.ActiveOrder(s, a.Id);
            bool assigned = handled && oa != null && oa.AssignedMachineLogicId == m.LogicId && oa.Kind == WorkOrderKind.Salvage
                            && Math.Abs(oa.Duration - ia.Ring.RecoverSeconds) < 0.01f;
            string hoverOrdered = HomeExplorationService.TryDescribeRelic(s, a.Id, out string ht, out string hb) ? hb : string.Empty;
            bool doneA = StepUntil(() => HomeExplorationService.IsRecovered(s, a.Id), 600L * Hz);
            string detail = LastDetail(HomeExplorationService.NotifyRelicRecovered);
            bool paid = s.TechData - tech0 == ia.Ring.TechData && TechDataFlow.IncomeOf(s, TechDataFlow.Relic) - income0 == ia.Ring.TechData
                        && HomeExplorationService.StateOf(s).TechData == ia.Ring.TechData;
            bool lore = detail.Contains(HomeExplorationService.LoreFor(ia)) && detail.Contains("+" + ia.Ring.TechData) && CountOf(HomeExplorationService.NotifyRelicRecovered) == rec0 + 1;
            bool job = oa != null && oa.State == WorkOrderState.Completed && mr.JobsCompleted >= jobs0 + 1;
            var stats = new List<(string Text, string Cls)>();
            ResearchStats.Build(s, stats);
            bool statLine = stats.Any(l => l.Text.Contains(GameText.Get("stats.research.src.relic")) && l.Text.Contains(ia.Ring.TechData.ToString(CultureInfo.InvariantCulture)));
            Expect(assigned && hoverOrdered.Contains(MachineNaming.Short(m.LogicId)) && doneA && paid && lore && job && statLine
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstRelicRecovered),
                $"R2 右键遗迹点派 {MachineNaming.Short(m.LogicId)} 回收 {a.Id}（{GameText.Get(ia.Ring.NameKey)}，工期 {ia.Ring.RecoverSeconds} 游戏秒）：工单指派给这台机器，悬停写“{hoverOrdered.Split('\n').LastOrDefault()}”；" +
                $"机器走过去干完后记为已回收，技术数据 +{ia.Ring.TechData}（经济账本，统计面板“遗迹回收”行）{paid}，通知带背景资料“{detail}”，机器完成工作 +1 {job}");

            // 审查修复（P1，FGR-BASE-020 / RTS 右键情境命令）：已回收的遗迹点、别的机器正在回收的遗迹点附近右键 = 移动
            // （返回 false，调用方按移动处理），而且不取消机器手上的工单；这台机器自己已经在回收的，右键返回 true、什么都不改。
            WorldFeature b = found[1];
            HomeValleyMachineMarker m2 = _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid && x.LogicId != m.LogicId);
            HomeValleyWorkOrders.WorkOrderOpResult busy = HomeExplorationService.OrderRecovery(s, b.Id, m.LogicId);
            WorkOrderRecord busyOrder = HomeExplorationService.ActiveOrder(s, b.Id);
            WorkOrderRecord m2Before = m2 != null ? HomeValleyWorkOrders.FindActiveOrderForMachine(s, m2.LogicId) : null;
            bool onRecovered = _home.ContextRelicForTests(m, new Ray(new Vector3(a.X + 0.6f, 40f, a.Y), Vector3.down));
            bool onOthers = m2 != null && _home.ContextRelicForTests(m2, new Ray(new Vector3(b.X + 0.6f, 40f, b.Y), Vector3.down));
            bool onOwn = _home.ContextRelicForTests(m, new Ray(new Vector3(b.X + 0.6f, 40f, b.Y), Vector3.down));
            bool kept = busy.Success && busyOrder != null && HomeValleyWorkOrders.IsActive(busyOrder) && busyOrder.AssignedMachineLogicId == m.LogicId
                        && HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId) == busyOrder && HomeExplorationService.ActiveOrder(s, b.Id) == busyOrder
                        && (m2 == null || HomeValleyWorkOrders.FindActiveOrderForMachine(s, m2.LogicId) == m2Before);
            // 告警里的工单名（P2）：遗迹回收单写“回收遗迹”，不写“拆解”。
            string alarmKind = HomeValleyAlarms.WorkKindName(busyOrder);
            Expect(m2 != null && !onRecovered && !onOthers && onOwn && kept && alarmKind == GameText.Get("relic.work.label"),
                $"R2 右键已回收的遗迹点附近：不当成派工（交给移动）{!onRecovered}；右键别的机器正在回收的遗迹点：同样交给移动 {!onOthers}；" +
                $"{MachineNaming.Short(m.LogicId)} 手上回收 {b.Id} 的工单不被取消、不换人 {kept}；它自己再右键这处返回已处理、什么都不改 {onOwn}；告警里写“{alarmKind}”");
            HomeValleyWorkOrders.CancelOrder(s, busyOrder?.WorkOrderId, new Vector2(m.Position3.x, m.Position3.z)); // 收尾：下面照常测地图入口（待分配池）。

            // 战略地图“派空闲机器回收”= 进待分配池，空闲的搬运机领走。
            HomeValleyWorkOrders.WorkOrderOpResult pool = HomeExplorationService.OrderRecovery(s, b.Id, 0);
            WorkOrderRecord ob = HomeExplorationService.ActiveOrder(s, b.Id);
            bool poolReady = pool.Success && ob != null && (ob.State == WorkOrderState.Ready || ob.AssignedMachineLogicId > 0);
            bool doneB = StepUntil(() => HomeExplorationService.IsRecovered(s, b.Id), 600L * Hz);
            bool pickedUp = ob != null && ob.State == WorkOrderState.Completed && ob.AssignedMachineLogicId > 0;
            // 世界里的标记：回收过的遗迹点换成矮一截的灰色方尖碑。
            WorldPlanetView.SetVisible(true);
            WorldPlanetView.Tick(s, new GridCell(b.X, b.Y));
            bool marker = WorldPlanetView.RecoveredRelicMarkerCount >= 1 && WorldPlanetView.VisibleRelicMarkerCount >= 1;
            WorldPlanetView.Shutdown();
            Expect(poolReady && doneB && pickedUp && marker,
                $"R2 地图入口“派空闲机器回收” {b.Id}：工单进待分配池，空闲搬运机领走并完工 {doneB}；世界里回收过的遗迹点标记换样式（已回收 {WorldPlanetView.RecoveredRelicMarkerCount} / 可见 {WorldPlanetView.VisibleRelicMarkerCount}）");
        }

        // ── H1 区域危害 ────────────────────────────────────────────────────────────

        private static void CheckHazards()
        {
            foreach (int seed in new[] { 8841, 8842, 8843 })
            {
                CampaignState s = NewWorld(seed);
                WorldPlan plan = WorldGenService.PlanFor(s);
                var faction = plan.Territories.Where(t => t.IsFaction).ToList();
                bool zones = faction.Count == 4 && faction.All(t =>
                {
                    Vector2Int hp = HazardProbe(plan, t);
                    Hazard h = HomeExplorationService.HazardAt(s, hp.x, hp.y, false, out PlannedTerritory at, out bool belt);
                    return h != null && h == HomeExplorationService.HazardForTerritory(t.Id) && at == t && !belt;
                });
                var belted = faction.Where(t => t.HazardWidth > 0).ToList();
                bool belts = belted.Count == 2 && belted.All(t =>
                {
                    int x = t.CenterX + t.Radius + t.HazardWidth / 2;
                    Hazard h = HomeExplorationService.HazardAt(s, x, t.CenterY, false, out PlannedTerritory at, out bool belt);
                    return h != null && belt && at == t && h.BeltKind == t.HazardKind;
                });
                bool outside = HomeExplorationService.HazardAt(s, HomeGridService.CorePivot(s).X, HomeGridService.CorePivot(s).Y, false, out _, out _) == null;
                PlannedTerritory act1 = faction.First(t => t.Act == 1);
                PlannedTerritory act2 = faction.First(t => t.Act == 2 && t.HazardWidth > 0);
                Vector2Int p1 = HazardProbe(plan, act1);
                bool desc1 = HomeExplorationService.TryDescribeHazard(s, p1.x, p1.y, out string t1, out string b1);
                Hazard h1 = HomeExplorationService.HazardForTerritory(act1.Id);
                bool text1 = desc1 && t1.Contains(GameText.Get(h1.NameKey)) && t1.Contains(GameText.Get(act1.NameKey)) && b1.Contains(GameText.Get(h1.EffectKey))
                             && b1.Contains(GameText.Get(h1.CounterKey)) && b1.Contains(GameText.Get("hazard.hover.pending"));
                bool beltDesc = HomeExplorationService.TryDescribeHazard(s, act2.CenterX + act2.Radius + act2.HazardWidth / 2, act2.CenterY, out _, out _);
                bool hiddenAct2 = !beltDesc && !HomeExplorationService.TerritoryVisible(s, act2);
                // 第一次揭开（探索圆碰到第二幕领地的外圈）：通知一次，之后这块领地在地图上显示、悬停可说明。
                int n0 = CountOf(HomeExplorationService.NotifyHazardFound);
                Hazard h2 = HomeExplorationService.HazardForTerritory(act2.Id);
                var edge = new Vector2(act2.CenterX + act2.OuterRadius + 30, act2.CenterY);
                HomeGridService.RevealArea(s, edge, 40f);
                int n1 = CountOf(HomeExplorationService.NotifyHazardFound);
                string detail = LastDetail(HomeExplorationService.NotifyHazardFound);
                bool seen = HomeExplorationService.HazardSeen(s, h2.Id) && HomeExplorationService.TerritoryVisible(s, act2);
                bool beltNow = HomeExplorationService.TryDescribeHazard(s, act2.CenterX + act2.Radius + act2.HazardWidth / 2, act2.CenterY, out string bt, out string bb)
                               && bt.Contains(GameText.Get(act2.HazardKey));
                HomeGridService.RevealArea(s, edge + new Vector2(20, 0), 60f);
                bool once = CountOf(HomeExplorationService.NotifyHazardFound) == n1;
                var model = new WorldMapModel();
                var view = new WorldMapView { CenterX = 0, CenterY = 0, HalfWidth = 2600, CanvasWidth = 900, CanvasHeight = 700 };
                model.Collect(s, view, 200);
                bool mapCircle = model.Circles.Any(c => c.Layer == WorldMapLayer.Territory && c.HazardId == h2.Id && c.Label.Contains(GameText.Get(h2.NameKey)))
                                 && model.Circles.Any(c => c.Layer == WorldMapLayer.Territory && c.HazardId == h1.Id && c.Tint == HomeExplorationService.HazardColor(h1));
                Expect(zones && belts && outside && text1 && hiddenAct2 && n1 == n0 + 1 && detail.Contains(GameText.Get(h2.CounterKey)) && seen && beltNow && once && mapCircle
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstHazardSeen),
                    $"H1 种子 {seed}：四个阵营领地各按表标一种危害、外圈危害带对应酸沼 / 高温辐射，核心处没有危害；悬停（{t1}）写效果、应对办法与“当前只作标记”；" +
                    $"第二幕领地揭开前不显示、不说明 {hiddenAct2}，探索圆碰到它的外圈时通知一次“{detail}”，之后地图显示它的圈（危害名 + 图例颜色）、悬停说明危害带 {beltNow}，再碰不重复 {once}");
            }
        }

        // ── N1 负向矩阵 ────────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            CampaignState s = NewWorld(8851);
            HomeGridMap map = HomeGridService.MapFor(s);
            List<WorldFeature> relics = Relics(s, 500);
            WorldFeature fogRelic = relics.FirstOrDefault(r => !map.IsExploredNoLoad(new GridCell(r.X, r.Y)));
            if (fogRelic == null)
            {
                Fail("N1 找不到迷雾里的遗迹点");
                return;
            }
            // 1. 迷雾里的遗迹点：不能下回收单（原因），右键 / 悬停查不到（不泄露情报）。
            HomeValleyWorkOrders.WorkOrderOpResult r1 = HomeExplorationService.OrderRecovery(s, fogRelic.Id, 0);
            bool fog = !r1.Success && HomeValleyWorkOrders.DescribeCommandFailure(r1.FailureReason) == GameText.Get("relic.reason.fog")
                       && HomeExplorationService.ActiveOrder(s, fogRelic.Id) == null && HomeExplorationService.RelicAt(s, new Vector2(fogRelic.X, fogRelic.Y)) == null
                       && !HomeExplorationService.TryDescribeRelic(s, fogRelic.Id, out _, out _);
            Expect(fog, $"N1-1 迷雾里的遗迹点 {fogRelic.Id}：拒绝回收（“{HomeValleyWorkOrders.DescribeCommandFailure(r1.FailureReason)}”），右键 / 悬停都查不到它");

            // 2. 不存在的遗迹点（格式不对 / 这个分布格没有遗迹点）。
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            string empty = null;
            for (int mx = 3; mx < 60 && empty == null; mx++)
            {
                WorldFeatures.CellFeatures(ctx, mx, 7, out WorldFeature rr, out _);
                if (rr == null)
                {
                    empty = "relic:" + mx.ToString(CultureInfo.InvariantCulture) + ":7";
                }
            }
            HomeValleyWorkOrders.WorkOrderOpResult r2a = HomeExplorationService.OrderRecovery(s, "relic:x", 0);
            HomeValleyWorkOrders.WorkOrderOpResult r2b = HomeExplorationService.OrderRecovery(s, empty ?? "relic:0:0:0", 0);
            Expect(!r2a.Success && !r2b.Success && HomeValleyWorkOrders.DescribeCommandFailure(r2b.FailureReason) == GameText.Get("relic.reason.missing"),
                $"N1-2 不存在的遗迹点（格式不对 / {empty} 这个分布格没有遗迹点）：拒绝，原因“{GameText.Get("relic.reason.missing")}”");

            // 3. 同一处遗迹重复下单（待分配池）：第二次拒绝；取消后可以重新下单。
            HomeGridService.RevealArea(s, new Vector2(fogRelic.X, fogRelic.Y), 8f);
            HomeValleyWorkOrders.WorkOrderOpResult first = HomeExplorationService.OrderRecovery(s, fogRelic.Id, 0);
            HomeValleyWorkOrders.WorkOrderOpResult second = HomeExplorationService.OrderRecovery(s, fogRelic.Id, 0);
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, fogRelic.Id);
            HomeValleyWorkOrders.CancelOrder(s, o?.WorkOrderId, Vector2.zero);
            bool cancelled = HomeExplorationService.ActiveOrder(s, fogRelic.Id) == null && !HomeExplorationService.IsRecovered(s, fogRelic.Id) && s.TechData >= 0;
            HomeValleyWorkOrders.WorkOrderOpResult again = HomeExplorationService.OrderRecovery(s, fogRelic.Id, 0);
            Expect(first.Success && !second.Success && HomeValleyWorkOrders.DescribeCommandFailure(second.FailureReason) == GameText.Get("relic.reason.ordered") && cancelled && again.Success,
                "N1-3 同一处遗迹重复下单：第二次拒绝（“已经有机器在回收”）；取消回收单后遗迹不算回收、不发技术数据，可以重新下单");

            // 4. 已回收的遗迹：不能再下单；重复完工不重复发技术数据（事务号幂等）。
            WorkOrderRecord o2 = HomeExplorationService.ActiveOrder(s, fogRelic.Id);
            long t0 = s.TechData;
            int paid1 = HomeExplorationService.CompleteRecovery(s, o2);
            long t1 = s.TechData;
            int paid2 = HomeExplorationService.CompleteRecovery(s, o2);
            HomeValleyWorkOrders.CancelOrder(s, o2?.WorkOrderId, Vector2.zero);
            HomeValleyWorkOrders.WorkOrderOpResult r4 = HomeExplorationService.OrderRecovery(s, fogRelic.Id, 0);
            Expect(paid1 > 0 && t1 - t0 == paid1 && paid2 == 0 && s.TechData == t1 && !r4.Success
                   && HomeValleyWorkOrders.DescribeCommandFailure(r4.FailureReason) == GameText.Get("relic.reason.recovered"),
                $"N1-4 已回收的遗迹：再下单拒绝（“{GameText.Get("relic.reason.recovered")}”）；重复完工不重复发技术数据（+{paid1} 只一次）");

            // 5. 在迷雾里放置：拒绝（FGT-ENV-007 的负向）；整圈已探索时再揭开一次不追加圆、不发通知。
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? fogCell = FindFogCell(s, new Vector2(core.X - 150, core.Y + 30), 40);
            int circles = s.Grid.Explored.Length;
            int notices = CountOf(HomeExplorationService.NotifyRelicFound);
            bool repeat = !HomeGridService.RevealArea(s, new Vector2(core.X, core.Y), 5f);
            Expect(fogCell != null && HasFog(HomeGridService.ValidatePlacement(s, "power_pole", fogCell.Value, 0, checkCost: false)) && repeat && s.Grid.Explored.Length == circles
                   && CountOf(HomeExplorationService.NotifyRelicFound) == notices,
                $"N1-5 迷雾里放电塔（{fogCell}）被拒（原因：迷雾）；已探索区域里再揭开一次不追加圆、不发通知");

            // 6. 机器探索：瞬移不补（M1 已测）；没有机器记录时只看脚下——已探索的核心处不追加圆，迷雾格脚下追加一个圆、格子变成已探索，也不留检查点。
            // 审查修复（P2）：此前写成 “|| true” 恒真，只证明不抛异常。
            int c6 = s.Grid.Explored.Length;
            bool atCore = MachineExploration.TryRevealAlong(s, -12345, new Vector2(core.X, core.Y));
            bool coreSame = !atCore && s.Grid.Explored.Length == c6;
            bool atFog = fogCell != null && MachineExploration.TryRevealAlong(s, -12345, new Vector2(fogCell.Value.X, fogCell.Value.Y));
            bool noRec = coreSame && atFog && s.Grid.Explored.Length == c6 + 1 && map.IsExploredNoLoad(fogCell.Value)
                         && !MachineRegistry.TryGetRecord(-12345, out _);
            // 7. 第二幕领地揭开前：地图悬停不说明（不剧透）。
            PlannedTerritory act2 = WorldGenService.PlanFor(s).Territories.First(t => t.IsFaction && t.Act == 2);
            bool noSpoiler = !HomeExplorationService.TryDescribeHazard(s, act2.CenterX, act2.CenterY, out _, out _);
            // 图例同一规则：第二幕领地揭开前，图例行只写危害名、不写领地名。
            Expect(noRec && noSpoiler,
                $"N1-6/7 没有机器记录时只看脚下（核心处不追加 {coreSame}，迷雾格追加一个圆 {atFog}）；第二幕领地揭开前地图悬停不说明它的危害（不剧透；图例同一规则见 U1）");
        }

        // ── T1 暂停与倍速 / T2 观察一致 ─────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames, out bool recovered)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            frames = 0;
            recovered = false;
            RepairTower(s);
            Boundary();
            HomeGridMap map = HomeGridService.MapFor(s);
            WorldFeature relic = Relics(s, 320).FirstOrDefault(r => HomeExplorationService.IsFound(s, r.Id) && map.IsExploredNoLoad(new GridCell(r.X, r.Y)));
            if (relic == null || !HomeExplorationService.OrderRecovery(s, relic.Id, 0).Success)
            {
                return "no-relic";
            }
            WorldSimulation.StepMany(Hz);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                WorldSimulation.SyncAllForSave();
                string p0 = JsonUtility.ToJson(s);
                long t0 = GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                WorldSimulation.SyncAllForSave();
                pausedHeld = JsonUtility.ToJson(s) == p0 && GameClock.Ticks == t0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + 200L * Hz;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            recovered = HomeExplorationService.IsRecovered(s, relic.Id);
            return Masked(JsonUtility.ToJson(s)) + "|explore=" + JsonUtility.ToJson(HomeExplorationService.StateOf(s)) + $"|tech={s.TechData}|circles={s.Grid.Explored.Length}";
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true, recoveredAll = true;
            string diff = string.Empty;
            var shown = new List<string>();
            long steps = 200L * Hz;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(8861, true, speed, true, out bool held, out int frames, out bool rec);
                paused &= held;
                recoveredAll &= rec;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed * GameClock.TicksFor(1f / 60f) - steps) <= 6f * Math.Max(1, GameClock.TicksFor(1f / 60f));
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && recoveredAll && reference != "no-relic",
                $"T1 暂停与 0.5x～3x（暂停 {paused}、逐字段一致 {same}、帧数成反比 {proportional}、期间回收完成 {recoveredAll}）：暂停中 120 帧整份战役状态不动；{string.Join(" / ", shown)} 跑同样的 200 游戏秒" +
                "（机器赶路、回收工期、技术数据入账、沿路探索），整份战役状态 JSON 逐字段一致" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObserved()
        {
            string seen = RunScenario(8862, true, 1f, false, out _, out _, out bool r1);
            string unseen = RunScenario(8862, false, 1f, false, out _, out _, out bool r2);
            Expect(seen == unseen && seen != "no-relic" && r1 && r2,
                "T2 家园在观察与不观察时（机器走去回收遗迹、沿路探索、技术数据入账）整份战役状态逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S1 真文件存读档 ──────────────────────────────────────────────────────

        private static string Snap(CampaignState s, string relicId)
        {
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, relicId) ?? s.WorkOrders?.LastOrDefault(w => w != null && w.TargetId == relicId);
            // 机器记录读登记表里的活记录（战役状态里的那份只在存档时写回）。
            string trail = string.Join(";", (s.MachineRecords ?? Array.Empty<MachineRecord>()).Where(m => m != null).OrderBy(m => m.LogicId)
                .Select(m => MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord live) ? live : m)
                .Select(m => $"{m.LogicId}:{m.ExploreHasLast}:{m.ExploreLast.x:0.00},{m.ExploreLast.y:0.00}"));
            return $"t={GameClock.Ticks}|explore={JsonUtility.ToJson(HomeExplorationService.StateOf(s))}|tech={s.TechData}|order={o?.State}:{o?.Progress:0.00}:{o?.AssignedMachineLogicId}" +
                   $"|circles={s.Grid.Explored.Length}|trail={trail}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8871);
            RepairTower(s);
            Boundary();
            HomeGridMap map = HomeGridService.MapFor(s);
            List<WorldFeature> found = Relics(s, 320).Where(r => HomeExplorationService.IsFound(s, r.Id) && map.IsExploredNoLoad(new GridCell(r.X, r.Y))).ToList();
            if (found.Count < 2)
            {
                Fail("S1 信号塔覆盖内找不到两处遗迹点");
                return;
            }
            // 先回收一处（存档里有“已回收”），再下第二处的单，回收进行中存档。
            HomeExplorationService.OrderRecovery(s, found[0].Id, 0);
            StepUntil(() => HomeExplorationService.IsRecovered(s, found[0].Id), 600L * Hz);
            string relicId = found[1].Id;
            HomeExplorationService.OrderRecovery(s, relicId, 0);
            // 机器到场、回收干到一半时存档（赶路途中存档的情况见下一条）。
            StepUntil(() => HomeExplorationService.ActiveOrder(s, relicId) is WorkOrderRecord w && w.State == WorkOrderState.InProgress && w.Progress >= 5f, 600L * Hz);
            string before = Snap(s, relicId);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = GameClock.Ticks + 200L * Hz;
            void Continue() => WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            Continue();
            string continuous = Snap(s, relicId);
            string loaded = null, after = null;
            for (int round = 0; round < 2; round++)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                ResetServices();
                ProductionService.ResetForTests();
                WeatherService.Install();
                PurificationService.Install();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    Fail("S1 读档失败：" + rr.Message);
                    return;
                }
                CampaignState l = rr.State;
                CampaignSession.Set(Slot, l);
                _home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(_home.SiteId);
                string ld = Snap(l, relicId);
                Continue();
                string af = Snap(l, relicId);
                if (round == 0)
                {
                    loaded = ld;
                    after = af;
                }
                else if (ld != loaded || af != after)
                {
                    after = "第二次读档不同：" + af;
                }
            }
            bool content = before.Contains(found[0].Id) && before.Contains("\"Baselined\":true") && before.Contains(":True:");
            Expect(save.Success && loaded == before && after == continuous && content && continuous.Contains("order=Completed"),
                $"S1 回收进行中真文件存读档（发现 / 回收过的遗迹点、揭开过的危害、回收单进度、机器探索检查点、已探索圆）：读回与存前逐项一致 {loaded == before}，" +
                $"接着跑与一直跑一致 {after == continuous}（两次读档相同）\n    存前 {before}" + (loaded == before ? string.Empty : "\n    读回 " + loaded) +
                (after == continuous ? string.Empty : "\n    一直跑 " + continuous + "\n    读档后 " + after));

            // 旧档（没有探索域）读回来：空域，第一次揭开新区域前按已探索区域静默补登，不补发通知。
            HomeExplorationState old = JsonUtility.FromJson<HomeExplorationState>("{}");
            CampaignState legacy = NewWorld(8872);
            // 旧档的已探索区域：一个 160 格的圆（相当于修好过信号塔的旧档），直接写进存档域（不走揭开入口、不发通知）。
            GridCell lc = HomeGridService.CorePivot(legacy);
            legacy.Grid.Explored = legacy.Grid.Explored.Append(new ExploredAreaRecord { CenterX = lc.X, CenterY = lc.Y, Radius = 160 }).ToArray();
            HomeGridService.MapFor(legacy).SetExplored(legacy.Grid.Explored);
            legacy.Grid.Exploration = null;
            CampaignFgStateDomains.EnsureAll(legacy);
            int notices0 = HomeExplorationService.RelicNotices;
            int foundN0 = HomeExplorationService.FoundCount;
            HomeGridMap lm = HomeGridService.MapFor(legacy);
            List<WorldFeature> lr = Relics(legacy, 200).Where(r => lm.IsExploredNoLoad(new GridCell(r.X, r.Y))).ToList();
            // 审查修复（P2）：读回后还没揭开过新区域时，“回收全部已发现的遗迹”就把已探索区域里的遗迹点算进来（补登，幂等、不发通知），与地图图标 / 右键单个回收一致。
            int recoverableBefore = HomeExplorationService.RecoverableRelics(legacy).Count;
            HomeExplorationState lst = HomeExplorationService.StateOf(legacy);
            bool zeroTicks = lst.RelicsFoundTicks.Length == lst.RelicsFound.Length && lst.RelicsFoundTicks.All(t => t == 0L);
            bool quietBaseline = lst.Baselined && recoverableBefore == lr.Count && HomeExplorationService.RelicNotices == notices0 && zeroTicks;
            HomeGridService.RevealArea(legacy, new Vector2(HomeGridService.CorePivot(legacy).X + 900, HomeGridService.CorePivot(legacy).Y), 4f);
            // 收紧（审查 P2，原来只断言 ≤ 1）：通知只来自新揭开的圆里新发现的遗迹点（逐个一条），补登的一条都没有。
            int newlyNotified = HomeExplorationService.FoundCount - foundN0;
            bool silent = lr.Count > 0 && lr.All(r => HomeExplorationService.IsFound(legacy, r.Id))
                          && newlyNotified == lst.RelicsFound.Length - lr.Count && HomeExplorationService.RelicNotices - notices0 == newlyNotified;
            Expect(old.RelicsFound != null && old.RelicsFound.Length == 0 && !old.Baselined && quietBaseline && silent,
                $"S1 旧档（没有探索域）读回来 = 空域；揭开新区域前“回收全部”就算上已探索区域里的 {recoverableBefore}/{lr.Count} 处（静默补登、记第 0 步）{quietBaseline}；" +
                $"之后揭开新区域只为新发现的 {newlyNotified} 处发通知（通知 {HomeExplorationService.RelicNotices - notices0} 条），补登的 0 条 {silent}");
        }

        // ── U1 界面 ───────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            var go = new GameObject("__fgexplore_map") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                CampaignState s = NewWorld(8881);
                RepairTower(s);
                Boundary();
                HomeGridMap map = HomeGridService.MapFor(s);
                WorldFeature relic = Relics(s, 300).FirstOrDefault(r => HomeExplorationService.IsFound(s, r.Id) && map.IsExploredNoLoad(new GridCell(r.X, r.Y)));
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                StrategicMapUIToolkit panel = go.AddComponent<StrategicMapUIToolkit>();
                panel.BindView(root);
                StrategicMapUIToolkit.Open();
                panel.SetViewForTests(0, 0, 1000, 900, 700);
                panel.Tick(force: true);
                // 图例：四种危害各一行，颜色块 = 表里的图例色，文字 = 危害名（所在领地）。
                IReadOnlyList<Hazard> hz = HomeExplorationService.Hazards;
                bool legend = panel.LegendRowsVisible == 4 && Enumerable.Range(0, 4).All(i => panel.LegendText(i).Contains(GameText.Get(hz[i].NameKey))
                                                                                               && panel.LegendColor(i) == HomeExplorationService.HazardColor(hz[i]));
                // 悬停：指着第一幕领地中心 → 危害名、效果、应对；指着核心 → 清空。
                PlannedTerritory act1 = WorldGenService.PlanFor(s).Territories.First(t => t.IsFaction && t.Act == 1);
                Hazard h1 = HomeExplorationService.HazardForTerritory(act1.Id);
                Vector2Int hp1 = HazardProbe(WorldGenService.PlanFor(s), act1);
                panel.HoverAt(panel.View.ToCanvas(hp1.x, hp1.y));
                string hover = panel.HoverText;
                panel.HoverAt(panel.View.ToCanvas(HomeGridService.CorePivot(s).X + 3, HomeGridService.CorePivot(s).Y + 3));
                bool hoverOk = hover.Contains(GameText.Get(h1.NameKey)) && hover.Contains(GameText.Get(h1.EffectKey)) && hover.Contains(GameText.Get(h1.CounterKey))
                               && panel.HoverText.Length == 0;
                Expect(legend && hoverOk, $"U1 战略地图危害图例 {panel.LegendRowsVisible} 行（颜色 + 名称，不只靠颜色）；指着危害区悬停说明“{hover.Replace('\n', ' ')}”，指着空地清空");

                // 审查修复（P2，不剧透）：地图上还不显示的领地，图例行只写危害名、不写领地名；显示的领地写“危害名（领地名）”。
                var uplan = WorldGenService.PlanFor(s);
                int hiddenRows = 0;
                int shownRows = 0;
                bool legendSpoiler = Enumerable.Range(0, 4).All(i =>
                {
                    PlannedTerritory lt = uplan.Find(hz[i].TerritoryId);
                    if (lt == null)
                    {
                        return true;
                    }
                    bool vis = HomeExplorationService.TerritoryVisible(s, lt);
                    hiddenRows += vis ? 0 : 1;
                    shownRows += vis ? 1 : 0;
                    return vis == panel.LegendText(i).Contains(GameText.Get(lt.NameKey));
                });
                // 审查修复（P2，帧率）：平移 / 缩放时模型每帧重建，但可回收遗迹数按版本号 + 0.2 秒节流缓存，不每帧重算。
                int recount0 = panel.RecoverableRecounts;
                var panSw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 12; i++)
                {
                    panel.SetViewForTests(i * 37, -i * 23, 1000 + i * 40, 900, 700);
                    panel.Tick(force: false);
                }
                panSw.Stop();
                int recounts = panel.RecoverableRecounts - recount0;
                int allowed = (int)(panSw.Elapsed.TotalSeconds / 0.2) + 1;
                panel.SetViewForTests(0, 0, 1000, 900, 700);
                panel.Tick(force: true);
                Expect(legendSpoiler && hiddenRows >= 1 && shownRows >= 1 && recounts <= allowed,
                    $"U1 图例不剧透：地图上不显示的领地 {hiddenRows} 行只写危害名、显示的 {shownRows} 行写领地名；平移 / 缩放 12 帧（{panSw.Elapsed.TotalMilliseconds:0} ms）可回收遗迹数只重算 {recounts} 次（上限 {allowed}）");

                if (relic == null)
                {
                    Fail("U1 信号塔覆盖内没有遗迹点");
                    return;
                }
                panel.SetViewForTests(relic.X, relic.Y, 120, 900, 700);
                panel.Tick(force: true);
                Vector2 at = panel.View.ToCanvas(relic.X, relic.Y);
                WorldMapItem item = panel.Model.Items.FirstOrDefault(i => i.Id == relic.Id);
                RelicRing ring = HomeExplorationService.RingFor(relic.Distance);
                panel.ClickAt(at);
                bool selected = panel.SelectedRelic == relic.Id && panel.RelicPanelVisible && panel.RelicPanelText.Contains(GameText.Get(ring.NameKey))
                                && panel.RelicPanelText.Contains(ring.TechData.ToString(CultureInfo.InvariantCulture)) && panel.RelicRecoverButton.enabledSelf
                                && item.Label == GameText.Get(ring.NameKey) && StrategicMapUIToolkit.IsOpen;
                bool ordered = panel.RecoverSelectedRelic();
                panel.Tick(force: true);
                WorldMapItem afterItem = panel.Model.Items.FirstOrDefault(i => i.Id == relic.Id);
                bool afterOk = ordered && !panel.RelicRecoverButton.enabledSelf && afterItem.Label.Contains("回收中") && panel.FeedbackText.Contains("待分配")
                               && panel.RelicPanelText.Contains("回收中");
                bool deny = !panel.RecoverSelectedRelic() && panel.FeedbackText == GameText.Get("relic.reason.ordered");
                Expect(selected && afterOk && deny,
                    $"U1 点战略地图上的遗迹点：不飞走，右侧面板写“{panel.RelicPanelText.Replace('\n', ' ')}”；点“派空闲机器回收”进待分配池，按钮变灰、图标标签写“回收中”；" +
                    "再点一次拒绝并写原因");
                // 批量（B02）：“派机器回收全部已发现的遗迹（N 处）”——其余每处各一张待分配的回收单；之后按钮变灰，再点写原因。
                int rest = HomeExplorationService.RecoverableRelics(s).Count;
                string allText = panel.RelicRecoverAllButton.text;
                int ordered2 = panel.RecoverAllRelics();
                int active = HomeExplorationService.StateOf(s).RelicsFound.Count(id => HomeExplorationService.ActiveOrder(s, id) != null);
                bool allOk = rest > 0 && allText.Contains(rest.ToString(CultureInfo.InvariantCulture)) && ordered2 == rest && HomeExplorationService.RecoverableRelics(s).Count == 0
                             && !panel.RelicRecoverAllButton.enabledSelf && active >= rest + 1 && panel.RecoverAllRelics() == 0
                             && panel.FeedbackText == GameText.Get("relic.reason.none_recoverable");
                Expect(allOk, $"U1 批量“{allText}”：其余 {rest} 处各下一张待分配回收单（下了 {ordered2} 张，进行中 {active} 张），按钮变灰，再点写“{GameText.Get("relic.reason.none_recoverable")}”");

                // 英文。
                GameSettings.SetLanguage(GameLanguage.En);
                HomeExplorationService.TryDescribeRelic(s, relic.Id, out string et, out string eb);
                HomeExplorationService.TryDescribeHazard(s, hp1.x, hp1.y, out string ht, out string hb);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(eb.Contains("cells from the core") && hb.Contains("Effect:") && hb.Contains("Counter:"), $"U1 英文：遗迹 “{et}” / 危害 “{ht}” 的说明都有英文");

                // 布局探针（四种分辨率 + 超长文字 + USS 体检）：遗迹面板与图例展开。
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "StrategicMap.uxml", "MapWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("MapRoot")?.RemoveFromClassList("uk-hidden");
                    rr.panel.visualTree.Q<VisualElement>("RelicEditor")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "U1 战略地图（含遗迹面板、危害图例）布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                if (StrategicMapUIToolkit.IsOpen && StrategicMapUIToolkit.Instance != null)
                {
                    StrategicMapUIToolkit.Instance.SetOpen(false);
                }
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── P1 性能 ──────────────────────────────────────────────────────────────

        private static void Perf(string name, double ms, double budget, string note)
        {
            string cpu = $"测量环境 Editor batchmode / Mono JIT，{SystemInfo.processorCount} 线程";
            if (ms <= budget)
            {
                Expect(true, $"P1 {name} {ms:0.000} ms ≤ {budget} ms（{note}；{cpu}）");
            }
            else if (ms <= budget * 2)
            {
                Expect(true, $"P1 {name} {ms:0.000} ms（性能警告：超过 {budget} ms 但不到 2 倍；{note}；{cpu}）");
                PerfLines.Add($"警告 {name} {ms:0.000} ms > {budget} ms");
            }
            else
            {
                Fail($"P1 {name} {ms:0.000} ms 超过 {budget} ms 的 2 倍（{note}；{cpu}）");
            }
            PerfLines.Add($"{name} {ms:0.000} ms（预算 {budget} ms）");
        }

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(8891);
            GridCell core = HomeGridService.CorePivot(s);
            var rnd = new System.Random(5);
            // 已探索圆很多时（500 个，探索过的面积很大的后期存档）：机器沿路探索的常见情况（走在已探索区里）。
            for (int i = 0; i < 500; i++)
            {
                HomeGridService.RevealArea(s, new Vector2(core.X + 200 + (i % 25) * 20, core.Y - 250 + (i / 25) * 20), 12f);
            }
            HomeValleyMachineMarker m = _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid);
            int id = m?.LogicId ?? -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 2000;
            for (int i = 0; i < n; i++)
            {
                MachineExploration.TryRevealAlong(s, id, new Vector2(core.X + 300 + (i % 40), core.Y + (i % 7)));
            }
            sw.Stop();
            Perf("机器沿路探索（走在已探索区，500 个已探索圆）每次", sw.Elapsed.TotalMilliseconds / n, 0.05, "每个模拟步只检查 1 台机器");
            // 悬停 / 右键查遗迹点：每帧最多一次。
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                HomeExplorationService.RelicAt(s, new Vector2(core.X + rnd.Next(-300, 300), core.Y + rnd.Next(-300, 300)));
            }
            sw.Stop();
            Perf("悬停 / 右键查遗迹点每次", sw.Elapsed.TotalMilliseconds / n, 0.05, "点位按分布格缓存");
            // 揭开新区域：机器探索圆（12 格）与信号塔覆盖（300 格，一次性）。
            HomeGridService.RevealArea(s, new Vector2(core.X - 400, core.Y - 400), 12f);
            double small = HomeExplorationService.LastRevealMs;
            HomeGridService.RevealArea(s, new Vector2(core.X - 900, core.Y + 900), 300f);
            double big = HomeExplorationService.LastRevealMs;
            HomeGridService.RevealArea(s, new Vector2(core.X + 3000, core.Y + 3000), 2000f);
            double huge = HomeExplorationService.LastRevealMs;
            Perf("揭开 12 格（机器探索）找遗迹 / 危害", small, 0.5, "只在真正走进迷雾时发生");
            Perf("揭开 300 格（信号塔接上网络）找遗迹 / 危害", big, 5.0, "一次性，覆盖源接上网络时");
            Perf("揭开 2000 格（极端）找遗迹 / 危害", huge, 60.0, "一次性；约 3900 个分布格");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgExplorationSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                OverlayService.ResetForTests();
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

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
