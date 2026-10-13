using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
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
    /// FG7-ENV-04 污染、净化塔与归还面积的自动验收（FG07 FGR-ENV-040～043；验收 FGT-ENV-005 / 006）。
    /// 全部起真实系统：真实统一时钟与世界模拟（净化在生产步里按游戏时间推进、里程碑在世界步里检查）、正式入口 <see cref="PurificationService.Install"/>、
    /// 真实管线内核供冷却液、真实电网、真实酸雨结算、真文件存读档、真 UXML 世界时间条 / 生产面板、真叠加层。
    /// D1 数据；G1 建造限制矩阵（三个种子）；P1 FGT-ENV-005 净化（速度、从中心向外、半径边界、缺冷却液暂停与恢复、归还面积、地图底图）；P2 T2 / T3（研究门槛、半径、冷却液、耗电）；
    /// Z1 净化区挡酸雨；R1 FGT-ENV-006 再污染（受污染不摧毁、天气加倍、净化后解除、摧毁后清理）；M1 归还面积里程碑（天气 -25%、稀土富矿、信号 +10%、成就与结局、再污染不收回）；
    /// B1 稀土富矿种子测试集；N1 负向矩阵；T1 暂停与 0.5x～3x；T2 观察一致；S1 真文件存读档；U1 界面；P3 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgPurificationSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgPurificationSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgPurificationSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string Tower = "purifier";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 污染、净化塔与归还面积")]
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
            Line("\n[污染、净化塔与归还面积] 建造限制、净化塔三级（速度 / 半径 / 缺冷却液）、净化区挡酸雨、再污染与受污染、归还面积与里程碑、倍速 / 观察 / 存读档、界面（FG7-ENV-04）");
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
            Func<bool> originalResearchGate = ResearchGate.TreeAvailableOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgpurify-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                // 本段测的是研发树开放后的真门槛（净化塔 T2 / T3 要研究）：全量自检里旧段按开放前的规则跑（LegacyGatesOpenForTests），这里显式打开，收尾还原。
                ResearchGate.TreeAvailableOverrideForTests = () => true;
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                Campaign.Content.FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "净化推进在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行）：每座塔每 2 游戏小时一次 O(半径²) 整数读写；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckBuildRestriction);
                Step(CheckPurifyCore);
                Step(CheckTiers);
                Step(CheckZone);
                Step(CheckRepollution);
                Step(CheckMilestones);
                Step(CheckRichSeeds);
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
                Fail($"污染与净化自检抛异常：{e}");
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
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                ResearchGate.TreeAvailableOverrideForTests = originalResearchGate;
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
            Line($"  · [污染、净化塔与归还面积] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);
        private static long HourTicks => GameClock.TicksFor(GameClock.HourSeconds);
        private static long StepT => PurificationService.StepTicks;

        /// <summary>新世界（家园、种子地形），天气 / 净化走正式入口；开局发电机修好 + 核心旁一座发电机 2（测试用建筑有电）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600, bool power = true)
        {
            PurificationService.ResetForTests();
            WeatherService.ResetForTests();
            RoofService.ResetForTests();
            SilentNightService.ResetForTests();
            StandingRuleService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            CalendarService.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            DayNightCycle.ResetForTests();
            IntelService.ResetSessionState();
            ResearchService.ResetForTests();
            OverlayService.ResetForTests();
            CampaignState s = F.NewWorld(seed, observe, scrap);
            SignalCoreService.EnsureInitialized(s);
            SignalCoverageService.Invalidate();
            WeatherService.Install();
            PurificationService.Install();
            if (power)
            {
                F.PowerUp(s);
            }
            WorldSimulation.StepMany(1);
            return s;
        }

        internal sealed class Rig
        {
            public BuildingRecord Tower;
            public GridCell Pivot;
            public GridCell FeedCell;
            public int Feed = -1;
        }

        /// <summary>核心配电半径内一块 3×6 的空地：北半边放净化塔（3×3），南口外铺两格管线，最南一格挂测试供冷却液点（相当于“上游有调配站 / 储罐”）。按种子地形找（B25）。</summary>
        internal static Rig LayTower(CampaignState s, string key, bool feed, long liters = 4000, GridCell? pivotOverride = null)
        {
            GridCell pivot;
            if (pivotOverride.HasValue)
            {
                pivot = pivotOverride.Value;
            }
            else
            {
                GridCell? o = F.FindArea(s, 3, 6, 8f, 22f);
                if (o == null)
                {
                    return null;
                }
                pivot = F.At(o.Value, 1, 4);
            }
            var rig = new Rig { Pivot = pivot, FeedCell = F.At(pivot, 0, -3) };
            rig.Tower = F.Built(s, Tower, key, pivot);
            PipeNetworkService.TryPlace(s, F.At(pivot, 0, -2), PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, rig.FeedCell, PipePieceKind.Pipe, 0, 0);
            if (feed)
            {
                Feed(rig, liters);
            }
            F.Resync(s);
            return rig;
        }

        internal static void Feed(Rig r, long liters)
        {
            r.Feed = PipeNetworkService.Kernel.AddProducer(r.FeedCell.X, r.FeedCell.Y, PipeNetworkService.FluidId("coolant"), Math.Max(1000L, liters * 1000L), liters * 1000L);
        }

        internal static void StopFeed(Rig r)
        {
            if (r.Feed >= 0)
            {
                PipeNetworkService.Kernel.RemoveProducer(r.Feed, out _);
                r.Feed = -1;
            }
        }

        private static int RingOf(GridCell c, GridCell cell)
        {
            long dx = cell.X - c.X, dy = cell.Y - c.Y;
            return PurificationService.RingOf((int)(dx * dx + dy * dy));
        }

        /// <summary>中心 <paramref name="c"/> 半径 <paramref name="r"/> 内（dx² + dy² ≤ r²）每格回调（格、环号）。</summary>
        private static void ForCells(GridCell c, int r, Action<GridCell, int> act)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 <= r * r)
                    {
                        act(new GridCell(c.X + dx, c.Y + dy), PurificationService.RingOf(d2));
                    }
                }
            }
        }

        /// <summary>第 k 环里的最高污染等级。</summary>
        private static int MaxRing(CampaignState s, GridCell c, int k)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            int max = 0;
            ForCells(c, k, (cell, ring) =>
            {
                if (ring == k)
                {
                    max = Math.Max(max, map.GetPollution(cell));
                }
            });
            return max;
        }

        /// <summary>半径内每环的最高等级串（“2,1,1,…”）。</summary>
        private static string RingLevels(CampaignState s, GridCell c, int r)
        {
            var parts = new List<string>(r + 1);
            for (int k = 0; k <= r; k++)
            {
                parts.Add(MaxRing(s, c, k).ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts);
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

        /// <summary>测试夹具：远处（离核心 600 格外，生成时有污染）的格子直接改成 0 级，代表“已经净化回来的”格子（归还面积 +n）。按种子地形找，不写死坐标。</summary>
        private static int ReturnFarCells(CampaignState s, int n, ref int cursor)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            int done = 0;
            int width = 256;
            int guard = 0;
            while (done < n && guard++ < 4_000_000)
            {
                int i = cursor++;
                var c = new GridCell(core.X + 600 + i % width, core.Y + 600 + i / width);
                if (map.GetPollution(c) > 0)
                {
                    map.SetPollution(c, 0);
                    done++;
                }
            }
            return done;
        }

        /// <summary>中心附近一座建筑能放的格（ValidatePlacement Ok，离 <paramref name="c"/> 的距离在 [min, max]），按种子地形找。</summary>
        private static GridCell? FindSpot(CampaignState s, string typeId, GridCell c, float min, float max)
        {
            int r = (int)Math.Ceiling(max);
            GridCell? best = null;
            for (int d = (int)Math.Floor(min); d <= r && best == null; d++)
            {
                for (int dy = -d; dy <= d && best == null; dy++)
                {
                    for (int dx = -d; dx <= d && best == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d)
                        {
                            continue;
                        }
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist < min || dist > max)
                        {
                            continue;
                        }
                        var cell = new GridCell(c.X + dx, c.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, typeId, cell, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            best = cell;
                        }
                    }
                }
            }
            return best;
        }

        private static string Py(float v)
        {
            double d = Math.Round(v, 6);
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("E") ? s : s + ".0";
        }

        // ── D1 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            float Tune(string id) => GridContent.TryGetTuning(id, out float v) ? v : float.NaN;
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_purify as p;print('\\n'.join(l for l in p.dump_lines() if l.startswith('RM')))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("RM\t", StringComparison.Ordinal)).ToList();
            var rt = t.TbReturnMilestone.DataList.Select(r => string.Join("\t", "RM", r.Id, r.Cells, r.Reward, Py(r.Value), r.NameKey, r.DescKey, r.MemoTitleKey, r.MemoBodyKey, r.SortOrder)).ToList();
            bool table = code == 0 && src.Count == 4 && src.OrderBy(x => x).SequenceEqual(rt.OrderBy(x => x));
            bool ms = PurificationService.AllMilestones.Select(m => m.Cells).SequenceEqual(new[] { 5000, 20000, 50000, 100000 })
                      && PurificationService.AllMilestones.Select(m => m.Reward).SequenceEqual(new[] { "weather_damage", "rich_ore", "signal_coverage", "achievement" });
            bool tune = Math.Abs(Tune("purify.hours_per_level") - 2f) < 1e-4f && StepT == GameClock.TicksFor(2 * GameClock.HourSeconds);
            bool prod = ProducerCatalog.TryGet(Tower, out ProducerDef def) && def.Mode == ProducerMode.Purifier && def.FluidPorts.Count == 1
                        && !def.FluidPorts[0].IsOutput && def.FluidPorts[0].Fluid?.Id == "coolant" && def.FluidLpm > 0f && def.InBatches >= 1
                        && ProducerCatalog.Problems.Count == 0;
            bool grid = GridContent.TryGetBuilding(Tower, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 3 && g.FootprintH == 3 && g.Placeable == 1
                        && g.Category == "environment" && g.UnlockRule == "always";
            var tiers = t.TbBuildingTier.DataList.Where(r => r.TypeId == Tower).OrderBy(r => r.Tier).ToList();
            bool tierRows = tiers.Count == 3 && tiers.Select(r => (int)Math.Round(r.Value)).SequenceEqual(new[] { 8, 12, 16 })
                            && tiers[1].UnlockRule == "research:environment.purifier_t2" && tiers[2].UnlockRule == "research:environment.purifier_t3"
                            && tiers.Select(r => r.PowerDemand).Distinct().Count() == 3;
            bool nodes = ResearchCatalog.TryGet("environment.purifier_t2", out ResearchNodeDef n2) && n2.IsReady && n2.Unlocks.Contains("tier:purifier.t2")
                         && ResearchCatalog.TryGet("environment.purifier_t3", out ResearchNodeDef n3) && n3.IsReady && n3.Unlocks.Contains("tier:purifier.t3")
                         && n3.KeyItems.Any(k => k.Item?.Id == "reactor_core");
            bool notify = new[] { "purify_milestone", "core_memo", "purify_stalled", "pollution_raised", "contamination_cleared" }
                .All(id => t.TbNotifyType.GetOrDefault(id)?.AwaySection == "weather");
            bool hooks = new[] { GuidanceHooks.EnvFirstPollutionSeen, GuidanceHooks.EnvFirstPurifier, GuidanceHooks.EnvFirstPurified, GuidanceHooks.EnvFirstReturnMilestone,
                GuidanceHooks.EnvFirstRepollution }.All(h => GuidanceHooks.Known.Contains(h));
            bool codex = new[] { "codex.env.pollution", "codex.env.purification", "codex.env.returned_area" }
                .All(id => t.TbCodexEntry.GetOrDefault(id) is var c && c != null && GameText.Has(c.TitleKey) && GameText.Has(c.BodyKey));
            string[] keys =
            {
                "building.purifier.name", "building.purifier.desc", "building.tier.value.purifier", "prod.place.purifier", "prod.reason.purify_working", "prod.reason.purify_done",
                "prod.reason.no_coolant", "prod.panel.purifier", "prod.panel.purify_front", "prod.panel.purify_progress", "purify.place.note", "purify.place.exempt",
                "purify.hover.title", "purify.hover.level0", "purify.hover.level1", "purify.hover.level_block", "purify.hover.returned", "purify.hover.zone", "purify.hover.eta",
                "purify.hover.paused", "purify.hover.uncovered", "purify.hover.contaminated", "purify.building.contaminated", "overlay.label.purifier", "overlay.label.returned",
                "topbar.returned", "topbar.returned_tip", "topbar.returned_tip_hint", "purify.cal.next", "purify.cal.line", "purify.notify.milestone", "purify.notify.rich_ore", "purify.notify.stalled",
                "purify.notify.repolluted", "purify.notify.recovered", "purify.ending_line", "purify.achievement.name", "purify.memo.m5000.title", "purify.memo.m100000.body",
            };
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            Expect(table && ms && tune && prod && grid && tierRows && nodes && notify && hooks && codex && missing.Count == 0,
                $"D1 数据：fg.TbReturnMilestone 与源数据逐行一致（源 {src.Count} 行 / 表 {rt.Count} 行）{table}，里程碑 5,000 / 20,000 / 50,000 / 100,000 依次是天气 / 稀土富矿 / 信号 / 成就 {ms}；" +
                $"净化速度每格每 2 游戏小时 1 级（{StepT} 步）{tune}；净化塔 = 生产模式 purifier、只有一个冷却液输入口 {prod}；3×3、建造菜单“环境”页、开局可建 {grid}；" +
                $"三级半径 8 / 12 / 16、T2 / T3 由研究回写门槛、每级耗电不同 {tierRows}；研发节点“环境 · 净化塔 T2 / T3”已开放、T3 要反应核 {nodes}；通知 5 类进离家报告“天气与环境”段 {notify}；" +
                $"引导钩子 {hooks}；图鉴“污染 / 净化塔 / 归还面积” {codex}；中英文本齐全（缺 {string.Join("、", missing)}）");
        }

        // ── G1 建造限制（FGR-ENV-040 / FGR-LOG-012）──────────────────────────────

        private static void CheckBuildRestriction()
        {
            var seen = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 7421, 7422, 7423 })
            {
                CampaignState s = NewWorld(seed, power: false);
                HomeGridMap map = HomeGridService.MapFor(s);
                GridCell? at = F.FindArea(s, 3, 3, 8f, 22f);
                if (at == null)
                {
                    all = false;
                    seen.Add($"种子 {seed}：找不到空地");
                    continue;
                }
                GridCell pc = F.At(at.Value, 1, 1);
                var cells = new List<GridCell>();
                GridMath.FootprintCells(pc, 3, 3, 0, cells);
                string row = string.Empty;
                bool ok = true;
                foreach (byte lv in new byte[] { 0, 1, 2, 3 })
                {
                    foreach (GridCell c in cells)
                    {
                        map.SetPollution(c, lv);
                    }
                    GridPlacementResult pole = HomeGridService.ValidatePlacement(s, "power_pole", pc, 0, asPlayerPlacement: false, checkCost: false);
                    bool poleBlocked = !pole.Ok && pole.Reasons.Any(r => r.Code == GridBlockReason.Pollution) && pole.Describe().Contains("污染过重");
                    string poleWarn = string.Join(" ", pole.Warnings);
                    GridPlacementResult pur = HomeGridService.ValidatePlacement(s, Tower, pc, 0, asPlayerPlacement: true, checkCost: false);
                    string purNotes = string.Join(" ", pur.Notes);
                    bool belt = HomeGridService.ValidateBeltCell(s, pc).Ok;
                    bool pipe = HomeGridService.ValidatePipeCell(s, pc, PipePieceKind.Pipe).Ok;
                    bool expect = lv >= 2
                        ? poleBlocked && pur.Ok && purNotes.Contains("重污染") && belt && !pipe
                        : pole.Ok && pur.Ok && belt && pipe && (lv == 1 ? poleWarn.Contains("污染") : !poleWarn.Contains("污染"));
                    ok &= expect;
                    row += $" {lv}级：电塔{(pole.Ok ? "可" : "拒")}/净化塔{(pur.Ok ? "可" : "拒")}/传送带{(belt ? "可" : "拒")}/管线{(pipe ? "可" : "拒")}";
                }
                // 迷雾仍然拦净化塔（只豁免污染）。
                GridCell far = new GridCell(pc.X + 400, pc.Y + 400);
                GridPlacementResult fog = HomeGridService.ValidatePlacement(s, Tower, far, 0, asPlayerPlacement: true, checkCost: false);
                ok &= !fog.Ok && fog.Reasons.Any(r => r.Code == GridBlockReason.Fog);
                all &= ok;
                seen.Add($"种子 {seed}（{pc}）{row}");
            }
            Expect(all, "G1 建造限制（三个种子、按种子找空地）：0 级都能建；1 级能建但写“污染…天气损伤加倍”的警告；2 / 3 级普通建筑（电塔）被拒并写“污染过重（N 级），需要先净化”，" +
                        "净化塔照样能放并写“净化塔可以建在重污染地上”，传送带能铺、管线不能铺；迷雾里的净化塔仍被拒（只豁免污染）\n    " + string.Join("\n    ", seen));
        }

        // ── P1 FGT-ENV-005 净化 ──────────────────────────────────────────────────

        private static void CheckPurifyCore()
        {
            CampaignState s = NewWorld(7401);
            Rig rig = LayTower(s, "p1", feed: false);
            if (rig == null)
            {
                Fail("P1 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ProductionService.Producer p = F.P(s, rig.Tower);
            GridCell c = rig.Pivot;
            int R = PurificationService.RadiusOf(rig.Tower);
            bool powered = rig.Tower.PowerState == BuildingPowerState.Powered && R == 8 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstPurifier);
            // 夹具：中心格 2 级，第 1～8 环 1 级；第 9、10 环（半径外）1 级，验证半径边界。
            ForCells(c, R + 2, (cell, ring) => map.SetPollution(cell, (byte)(ring == 0 ? 2 : 1)));
            long area0 = map.ReturnedCells;
            int stall0 = CountOf("purify_stalled");
            int steps0 = PurificationService.StepCount;
            // ① 没接冷却液：1.2 次降级的时长里一格不动，状态“缺流体”、原因写缺冷却液与接口在哪，通知一次。
            WorldSimulation.StepMany((int)(StepT * 1.2));
            string reason = F.Reason(s, p);
            bool noCoolant = p.State == ProdState.MissingFluid && p.Reason == ProdReason.NoCoolant && !p.Rec.PurifySupplied && PurificationService.StepCount == steps0
                             && map.GetPollution(c) == 2 && p.Rec.PurifyTicks == 0 && CountOf("purify_stalled") == stall0 + 1 && reason.Contains("缺冷却液")
                             && LastDetail("purify_stalled").Contains("冷却液");
            // ② 接上冷却液：累计满 2 游戏小时（只算有电有冷却液的时间）才降第一次——中心格 2 → 1，第 1 环不动。
            Feed(rig, 20000);
            F.Resync(s);
            StepUntil(() => p.Rec.PurifyTicks >= StepT - 30, StepT + 3 * Hz);
            bool notYet = PurificationService.StepCount == steps0 && map.GetPollution(c) == 2;
            StepUntil(() => PurificationService.StepCount > steps0, 400);
            long t1 = GameClock.Ticks;
            bool first = map.GetPollution(c) == 1 && MaxRing(s, c, 1) == 1 && p.Rec.PurifyTicks < 6 && p.State == ProdState.Working;
            StepUntil(() => PurificationService.StepCount > steps0 + 1, StepT + 400);
            long interval = GameClock.Ticks - t1;
            bool speed = Math.Abs(interval - StepT) <= 6 && map.GetPollution(c) == 0 && MaxRing(s, c, 1) == 1;
            StepUntil(() => PurificationService.StepCount > steps0 + 2, StepT + 400);
            bool ring1 = MaxRing(s, c, 0) == 0 && MaxRing(s, c, 1) == 0 && MaxRing(s, c, 2) == 1;
            // ③ 供给中断：刚降完一次就断冷却液 → 机内存量（inBatches 秒）用完即暂停，进度冻结、已净化的格子不变、通知一次；接回后从冻结的进度接着走。
            int stall1 = CountOf("purify_stalled");
            StopFeed(rig);
            StepUntil(() => !p.Rec.PurifySupplied, StepT);
            long frozen = p.Rec.PurifyTicks;
            string snap = RingLevels(s, c, R + 2);
            int stepsAtPause = PurificationService.StepCount;
            WorldSimulation.StepMany((int)StepT);
            // （断供后机内缓存与管线里残留的冷却液还会再用一阵，冻结点落在哪由残留量决定；这里只断言冻结后不再推进。）
            bool paused = p.Rec.PurifyTicks == frozen && RingLevels(s, c, R + 2) == snap && PurificationService.StepCount == stepsAtPause && p.Reason == ProdReason.NoCoolant
                          && CountOf("purify_stalled") == stall1 + 1 && frozen >= 0 && frozen < StepT;
            int front = Enumerable.Range(0, R + 1).First(k => MaxRing(s, c, k) > 0);
            int frontLevel = MaxRing(s, c, front);
            Feed(rig, 20000);
            F.Resync(s);
            long resumeAt = GameClock.Ticks;
            StepUntil(() => PurificationService.StepCount > stepsAtPause, StepT + 400);
            long resumed = GameClock.Ticks - resumeAt;
            bool resume = resumed <= StepT - frozen + 3 * Hz && resumed >= StepT - frozen - 6 && MaxRing(s, c, front) == frontLevel - 1
                          && (front + 1 > R || MaxRing(s, c, front + 1) == 1);
            // ④ 走完全程：从中心向外一环一环，第 8 环清完即全部净化；第 9、10 环（半径外）保持 1 级；之后继续维持净化区、照常消耗冷却液。
            StepUntil(() => PurificationService.IsDone(s, p), 8 * StepT);
            long coolant0 = p.Rec.CoolantUsedMl;
            WorldSimulation.StepMany(Hz * 10);
            bool done = Enumerable.Range(0, R + 1).All(k => MaxRing(s, c, k) == 0) && MaxRing(s, c, R + 1) == 1 && MaxRing(s, c, R + 2) == 1
                        && p.State == ProdState.Working && p.Reason == ProdReason.PurifyDone && F.Reason(s, p).Contains("全部净化") && p.Rec.PurifySupplied
                        && p.Rec.CoolantUsedMl > coolant0;
            // 冷却液用量精确到毫升：10 游戏秒用量 = 30 升 / 分钟 × 10 / 60 = 5 升。
            long used = p.Rec.CoolantUsedMl - coolant0;
            bool rate = Math.Abs(used - (long)Math.Round(ProductionService.PurifierLpm(p) * 1000.0 * 10 / 60.0)) <= 2;
            // ⑤ 归还面积 = 生成时有污染、现在 0 级的格子数（与逐格重数一致）。
            long expected = 0;
            ForCells(c, R + 2, (cell, ring) =>
            {
                HomeGridMap.Chunk ch = map.ChunkAt(cell, out int idx);
                if (ch.BasePollution != null && ch.BasePollution[idx] > 0 && ch.Pollution[idx] == 0)
                {
                    expected++;
                }
            });
            bool area = map.ReturnedCells - area0 == expected && map.ReturnedCells == map.RecountReturnedCells();
            // ⑥ 地图底图（DEBT-FG3GEN01-08 机制）：净化回来的格子（生成时有污染、现在 0 级）进覆盖表，底图按它重画。
            var overrides = new List<Unity.Mathematics.int4>();
            map.CollectCellOverrides(overrides);
            GridCell? returnedCell = null;
            ForCells(c, R, (cell, ring) =>
            {
                HomeGridMap.Chunk ch = map.ChunkAt(cell, out int idx);
                if (returnedCell == null && ch.BasePollution != null && ch.BasePollution[idx] > 0 && ch.Pollution[idx] == 0)
                {
                    returnedCell = cell;
                }
            });
            bool mapOverlay = returnedCell == null
                ? expected == 0
                : overrides.Any(o => o.x == returnedCell.Value.X && o.y == returnedCell.Value.Y && o.w == 0);
            Expect(powered && noCoolant && notYet && first && speed && ring1 && paused && resume && done && rate && area && mapOverlay,
                $"P1 FGT-ENV-005 净化（种子 7401，T1 半径 {R}）：有电 {powered}；没接冷却液 1.2 个周期一格不动、状态缺流体“{reason.Replace("\n", " / ")}”、通知一次 {noCoolant}；" +
                $"接上后累计满 2 游戏小时才降第一次 {notYet}→{first}；两次降级间隔 {interval} 步（应 {StepT}）{speed}；从中心向外：中心 2 级先降两次、再降第 1 环 {ring1}；" +
                $"断供：冻结进度 {frozen} 步、已净化的格子不变、只通知一次 {paused}；接回后 {resumed} 步完成下一次（剩余 {StepT - frozen}）{resume}；" +
                $"全程走完半径内全 0、半径外第 {R + 1} / {R + 2} 环仍 1 级、继续维持净化区 {done}；冷却液 10 秒用 {used} 毫升 {rate}；" +
                $"归还面积 +{map.ReturnedCells - area0}（生成时有污染、现在 0 级 = {expected}；重数 {map.RecountReturnedCells()}）{area}；地图底图覆盖被净化的格子 {mapOverlay}");
        }

        // ── P2 T2 / T3 ───────────────────────────────────────────────────────────

        private static void CheckTiers()
        {
            CampaignState s = NewWorld(7402);
            Rig rig = LayTower(s, "p2", feed: true, liters: 20000);
            if (rig == null)
            {
                Fail("P2 找不到放净化塔的空地");
                return;
            }
            StepUntil(() => F.P(s, rig.Tower).Rec.PurifySupplied, 4 * Hz);
            ProductionService.Producer p = F.P(s, rig.Tower);
            bool lockedT2 = !HomeGridService.CanUpgradeNow(s, rig.Tower, out _, out int to0, out GridReason why0);
            bool enqueue = ResearchService.TryEnqueue(s, "environment.purifier_t2", out string enqueueReason);
            ResearchService.CompleteForTests(s, "environment.purifier_t2");
            bool canT2 = HomeGridService.CanUpgradeNow(s, rig.Tower, out _, out int to2, out _) && to2 == 2;
            // 测试捷径：升级施工完成（原地升级的施工流程由 FG4-ECO-05 覆盖）。
            rig.Tower.Tier = 2;
            BuildingOps.OnUpgradeCompleted(s, rig.Tower);
            HomeValleyPowerGrid.Recompute(s);
            ProductionService.Sync(s, force: true);
            int h = p.Rec.FluidHandles.Length > 0 ? p.Rec.FluidHandles[0] : -1;
            bool reg = h >= 0 && PipeNetworkService.Kernel.TryGetConsumer(h, out PipeConsumerInfo ci)
                       && ci.LitersPerMinute == (int)Math.Ceiling(ProductionService.PurifierLpm(p) * Math.Max(1.0, GridContent.Tuning("eco.prod.fluid_intake_factor")))
                       && ci.CapacityMl == ProductionService.PurifierBufferMl(p);
            bool t2 = PurificationService.RadiusOf(rig.Tower) == 12 && Math.Abs(ProductionService.PurifierLpm(p) - 45.0) < 1e-6 && Math.Abs(HomeValleyPowerGrid.DemandOf(rig.Tower) - 18f) < 1e-3f && reg;
            bool geo2 = Geometry(s, rig.Tower, 12);
            bool lockedT3 = !HomeGridService.CanUpgradeNow(s, rig.Tower, out _, out _, out _);
            ResearchService.CompleteForTests(s, "environment.purifier_t3");
            bool canT3 = HomeGridService.CanUpgradeNow(s, rig.Tower, out _, out int to3, out _) && to3 == 3;
            rig.Tower.Tier = 3;
            BuildingOps.OnUpgradeCompleted(s, rig.Tower);
            HomeValleyPowerGrid.Recompute(s);
            ProductionService.Sync(s, force: true);
            bool t3 = PurificationService.RadiusOf(rig.Tower) == 16 && Math.Abs(ProductionService.PurifierLpm(p) - 60.0) < 1e-6 && Math.Abs(HomeValleyPowerGrid.DemandOf(rig.Tower) - 24f) < 1e-3f;
            bool geo3 = Geometry(s, rig.Tower, 16);
            // 环表：半径 R 的环数 R + 1，格数 = dx² + dy² ≤ R² 的格子数。
            bool rings = new[] { 8, 12, 16 }.All(r =>
            {
                int[][] rr = PurificationService.Rings(r);
                int n = 0;
                ForCells(new GridCell(0, 0), r, (_, __) => n++);
                return rr.Length == r + 1 && rr.Sum(x => x.Length) == n && rr[0].Length == 1;
            });
            Expect(lockedT2 && enqueue && canT2 && t2 && geo2 && lockedT3 && canT3 && t3 && geo3 && rings,
                $"P2 三级：没研究时不能升 T2（{why0.Code}）{lockedT2}，研发节点能加入队列（{enqueueReason}）{enqueue}；研究后能原地升 T2 {canT2}；T2 半径 12、冷却液 45 升 / 分钟、耗电 18、管线消费者按新用量重登 {t2}；" +
                $"T2 半径 12 的格子被净化、13 不动 {geo2}；T3 要再研究 {lockedT3}→{canT3}；T3 半径 16、冷却液 60、耗电 24 {t3}；16 净化、17 不动 {geo3}；环表格数与环数正确 {rings}");
        }

        /// <summary>半径 r 的环上每格 1 级、r + 1 环上每格 1 级、里面全 0：降级直到不再降——r 环清零、r + 1 环不动（几何边界）。</summary>
        private static bool Geometry(CampaignState s, BuildingRecord tower, int r)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell c = PurificationService.CenterOf(tower);
            ForCells(c, r + 1, (cell, ring) => map.SetPollution(cell, (byte)(ring >= r ? 1 : 0)));
            ProducerRecord rec = F.P(s, tower).Rec;
            for (int i = 0; i < 4; i++)
            {
                if (PurificationService.PurifyStep(s, tower, rec) == 0)
                {
                    break;
                }
            }
            return MaxRing(s, c, r) == 0 && MaxRing(s, c, r + 1) == 1;
        }

        // ── Z1 净化区挡酸雨（FG07 第 3.3 节“不在净化区里的建筑……”）──────────────────

        private static void CheckZone()
        {
            CampaignState s = NewWorld(7403);
            Rig rig = LayTower(s, "z1", feed: true, liters: 50000);
            if (rig == null)
            {
                Fail("Z1 找不到放净化塔的空地");
                return;
            }
            int R = PurificationService.RadiusOf(rig.Tower);
            GridCell? inA = FindSpot(s, "power_pole", rig.Pivot, 3f, R - 1.5f);
            GridCell? outB = FindSpot(s, "power_pole", rig.Pivot, R + 3f, R + 10f);
            if (inA == null || outB == null)
            {
                Fail("Z1 找不到放测试电塔的格子");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            map.SetPollution(inA.Value, 0);
            map.SetPollution(outB.Value, 0);
            BuildingRecord a = F.Built(s, "power_pole", "z1_in", inA.Value);
            BuildingRecord b = F.Built(s, "power_pole", "z1_out", outB.Value);
            StepUntil(() => F.P(s, rig.Tower).Rec.PurifySupplied, 4 * Hz);
            bool installed = WeatherService.PurifiedProvider != null && PurificationService.InPurifiedZone(s, inA.Value) && !PurificationService.InPurifiedZone(s, outB.Value);
            float a0 = a.Health, b0 = b.Health;
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 1.0, 6 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.AcidRain), 4 * Hz);
            WorldSimulation.StepMany((int)(2 * HourTicks));
            float aMid = a.Health, bMid = b.Health;
            bool shielded = Math.Abs(aMid - a0) < 1e-4f && bMid < b0;
            // 断冷却液：净化区随之消失（不在净化 = 没有净化区），里面的电塔也开始掉耐久。
            Rig r2 = rig;
            StopFeed(r2);
            StepUntil(() => !F.P(s, rig.Tower).Rec.PurifySupplied, StepT);
            float a1 = a.Health;
            WorldSimulation.StepMany((int)(2 * HourTicks));
            bool lost = !PurificationService.InPurifiedZone(s, inA.Value) && a.Health < a1;
            Expect(installed && shielded && lost,
                $"Z1 净化区挡酸雨：正式入口接上了判定、范围内 / 外判断正确 {installed}；酸雨 2 游戏小时：范围内露天电塔耐久 {a0}→{aMid} 不掉、范围外 {b0}→{bMid} 掉 {shielded}；" +
                $"净化塔断冷却液后净化区消失、里面的电塔 {a1}→{a.Health} 开始掉 {lost}");
        }

        // ── R1 FGT-ENV-006 再污染 ────────────────────────────────────────────────

        private static void CheckRepollution()
        {
            CampaignState s = NewWorld(7404);
            GridCell? area = F.FindArea(s, 3, 6, 8f, 22f);
            if (area == null)
            {
                Fail("R1 找不到空地");
                return;
            }
            GridCell pivot = F.At(area.Value, 1, 4);
            HomeGridMap map = HomeGridService.MapFor(s);
            // 两座同类建筑：X 在将来净化塔的范围里（第 3～6 格），Y 在远处做对照；两座脚下都清成 0 级。
            GridCell? xAt = FindSpot(s, "blending_station", pivot, 4f, 6f);
            GridCell? yAt = FindSpot(s, "blending_station", pivot, 14f, 20f);
            if (xAt == null || yAt == null)
            {
                Fail("R1 找不到放测试建筑的格子");
                return;
            }
            var xc = new List<GridCell>();
            GridMath.FootprintCells(xAt.Value, 3, 3, 0, xc);
            var yc = new List<GridCell>();
            GridMath.FootprintCells(yAt.Value, 3, 3, 0, yc);
            foreach (GridCell c in xc.Concat(yc))
            {
                map.SetPollution(c, 0);
            }
            BuildingRecord x = F.Built(s, "blending_station", "r1_x", xAt.Value);
            BuildingRecord y = F.Built(s, "blending_station", "r1_y", yAt.Value);
            GridCell corner = xc.First(c => c != xAt.Value);
            float hx = x.Health;
            int raised0 = CountOf("pollution_raised");
            long areaBefore = map.ReturnedCells;
            RepolluteResult res = PurificationService.Repollute(s, corner, 0, 2, "purify.source.test");
            string hover = HomeValleyBuildMode.DescribeBuilding(s, x);
            string cellLine = PurificationService.StatusLine(s, corner, false) ?? string.Empty;
            bool contaminated = res.CellsRaised == 1 && res.LevelsRaised == 2 && res.BuildingsContaminated == 1 && PurificationService.IsContaminated(s, x.BuildingId)
                                && !PurificationService.IsContaminated(s, y.BuildingId) && x.ConstructionState == BuildingConstructionState.Operational && Math.Abs(x.Health - hx) < 1e-4f
                                && map.GetPollution(corner) == 2 && map.GetPollution(xAt.Value) == 0 && hover.Contains("受污染") && cellLine.Contains("受污染的建筑")
                                && CountOf("pollution_raised") == raised0 + 1 && LastDetail("pollution_raised").Contains("受污染")
                                && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstRepollution);
            // 酸雨 8 游戏小时：受污染的 X（枢轴格 0 级）掉的耐久是对照 Y 的 2 倍（两座各自的整数预算按 ID 错开相位，各有 ±1 的取整，合计容差 3）。
            float x0 = x.Health, y0 = y.Health;
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 1.0, 8 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.AcidRain), 4 * Hz);
            StepUntil(() => !WeatherService.IsActive(s, WeatherService.AcidRain), (long)(8.5 * HourTicks));
            float dx = x0 - x.Health, dy = y0 - y.Health;
            bool doubled = dy >= 6f && Math.Abs(dx - 2f * dy) <= 3.01f && dx >= 1.7f * dy;
            // 净化塔把那一格净化到 0 级 → 受污染解除、通知；对照的再污染后被摧毁 → 静默移出名单。
            int cleared0 = CountOf("contamination_cleared");
            Rig rig = LayTower(s, "r1", feed: true, liters: 20000, pivotOverride: pivot);
            StepUntil(() => !PurificationService.IsContaminated(s, x.BuildingId), 3 * StepT + 6 * Hz);
            bool recovered = !PurificationService.IsContaminated(s, x.BuildingId) && map.GetPollution(corner) == 0 && CountOf("contamination_cleared") == cleared0 + 1
                             && LastDetail("contamination_cleared").Contains("解除") && !HomeValleyBuildMode.DescribeBuilding(s, x).Contains("受污染");
            GridCell ycorner = yc.First(c => c != yAt.Value);
            PurificationService.Repollute(s, ycorner, 0, 1, "purify.source.test");
            bool yIn = PurificationService.IsContaminated(s, y.BuildingId);
            // 摧毁走正式入口（突袭 / 天气归零同一个）：被摧毁的建筑留在原地（耐久 0、可修），仍在受污染的格子上——保持“受污染”，修好后照样天气加倍直到净化。
            bool yDead = HomeValleyPowerGrid.ApplyBuildingDestroyed(s, y.BuildingId);
            WorldSimulation.StepMany(2 * Hz);
            bool keptWhenDestroyed = yDead && PurificationService.IsContaminated(s, y.BuildingId);
            // 测试捷径：拆除完工、记录移除（机器拆除流程由 FG3-LOG-02 覆盖）→ 静默移出名单，不发“解除”。
            s.BuildingRecords = s.BuildingRecords.Where(b => b != null && b.BuildingId != y.BuildingId).ToArray();
            WorldSimulation.StepMany(2 * Hz);
            map = HomeGridService.MapFor(s);
            bool removed = yIn && keptWhenDestroyed && !PurificationService.IsContaminated(s, y.BuildingId) && CountOf("contamination_cleared") == cleared0 + 1;
            // 已经 3 级的格子再污染：不再升级、不发通知。
            map.SetPollution(ycorner, 3);
            int raised1 = CountOf("pollution_raised");
            RepolluteResult none = PurificationService.Repollute(s, ycorner, 0, 2, "purify.source.test");
            bool capped = none.CellsRaised == 0 && CountOf("pollution_raised") == raised1;
            Expect(contaminated && doubled && recovered && removed && capped && rig != null && areaBefore >= 0,
                $"R1 FGT-ENV-006 再污染：一片格子 +2 级，上面的建筑进入“受污染”、不摧毁、耐久不变，悬停写“{hover.Split('\n').FirstOrDefault(l => l.Contains("受污染"))}”，通知与引导钩子 {contaminated}；" +
                $"酸雨 4 小时受污染的 X 掉 {dx:0.#}、对照 Y 掉 {dy:0.#}（加倍）{doubled}；净化塔把那一格净化到 0 级后“受污染”解除并通知 {recovered}；受污染的建筑被摧毁（留在原地可修）仍受污染、拆除完工后静默移出名单 {removed}；已 3 级的格子再污染不升级不通知 {capped}");
        }

        // ── M1 归还面积里程碑（FGR-ENV-042）─────────────────────────────────────────

        private static void CheckMilestones()
        {
            CampaignState s = NewWorld(7405);
            HomeGridMap map = HomeGridService.MapFor(s);
            string memo = null;
            Action<string> onMemo = id => memo = id;
            GameEvent.AddEventListener(PurificationService.CoreMemoEvent, onMemo);
            try
            {
                // 4 小时酸雨的耐久损失（1 级污染的格子上：每小时 2%），里程碑之前做基准。
                GridCell? pa = F.FindFree(s, "power_pole", 8f, 24f);
                if (pa == null)
                {
                    Fail("M1 找不到放电塔的格子");
                    return;
                }
                map.SetPollution(pa.Value, 1);
                BuildingRecord p1 = F.Built(s, "power_pole", "m1_a", pa.Value);
                float lossBefore = AcidLoss(s, p1);
                int cursor = 0;
                long a0 = map.ReturnedCells;
                ReturnFarCells(s, 4999, ref cursor);
                WorldSimulation.StepMany(Hz + 1);
                bool notYet = !PurificationService.IsReached(s, "m5000") && map.ReturnedCells == a0 + 4999;
                int ms0 = CountOf("purify_milestone"), memo0 = CountOf("core_memo");
                ReturnFarCells(s, 1, ref cursor);
                WorldSimulation.StepMany(Hz + 1);
                bool m5 = PurificationService.IsReached(s, "m5000") && Math.Abs(PurificationService.WeatherDamageFactor(s) - 0.75) < 1e-9
                          && CountOf("purify_milestone") == ms0 + 1 && LastDetail("purify_milestone").Contains("5,000") && CountOf("core_memo") == memo0 + 1
                          && LastDetail("core_memo").Contains("第一片净土") && memo == "m5000" && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstReturnMilestone)
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstPurified);
                string memo5 = LastDetail("core_memo"), memoId5 = memo;
                // 里程碑之后同样条件的另一座电塔：损失 ≈ 之前的 75%。
                map.SetPollution(F.At(pa.Value, 0, 0), 1);
                GridCell? pb = F.FindFree(s, "power_pole", 8f, 24f);
                map.SetPollution(pb.Value, 1);
                BuildingRecord p2 = F.Built(s, "power_pole", "m1_b", pb.Value);
                float lossAfter = AcidLoss(s, p2);
                bool weather = lossBefore >= 7f && Math.Abs(lossAfter - 0.75f * lossBefore) <= 1.01f
                               && WeatherService.MinuteDamage(100.0, 2, 59, 0.75) <= WeatherService.MinuteDamage(100.0, 2, 59, 1.0);
                // 20,000：揭示稀土富矿（按生成规则找、落在稀土矿脉上、揭示这一圈），压在上面的提取钻每份产出 ×2。
                ReturnFarCells(s, 15000, ref cursor);
                WorldSimulation.StepMany(Hz + 1);
                PurificationState d = PurificationService.StateOf(s);
                RichDepositRecord dep = d.RichDeposits?.FirstOrDefault();
                bool rich = PurificationService.IsReached(s, "m20000") && dep != null && dep.Radius == PurificationService.RichRadius;
                bool oreOk = false, explored = false, drill2 = false, drill1 = false;
                if (dep != null)
                {
                    var dc = new GridCell(dep.X, dep.Y);
                    map.TerrainSource.Sample(dc.X, dc.Y, out byte terr, out _);
                    oreOk = terr == GridContent.TerrainCode("ore_rare");
                    explored = map.ChunkAt(dc, out int ei).Explored[ei] == 1 && LastDetail("purify_milestone").Contains($"{dep.X}, {dep.Y}");
                    BuildingRecord drill = F.Built(s, "extraction_drill", "m1_drill", dc);
                    ProductionService.Producer dp = F.P(s, drill);
                    drill2 = DrillYield(s, drill, dp) == 2 && PurificationService.DrillYieldMult(s, dp) == 2;
                    int samples = 0;
                    if (PurificationService.TryFindRichCell(s, out GridCell other, ref samples))
                    {
                        BuildingRecord drillB = F.Built(s, "extraction_drill", "m1_drill_b", other);
                        drill1 = PurificationService.DrillYieldMult(s, F.P(s, drillB)) == 1;
                    }
                }
                string cal = PurificationService.CalendarText(s);
                // 50,000：家园信号覆盖 +10%（核心覆盖源半径 × 1.1）。
                SignalCoverageService.Invalidate();
                SignalCoverageService.TryGetSource(0, out _, out float coreR0, out _, out _);
                ReturnFarCells(s, 30000, ref cursor);
                WorldSimulation.StepMany(Hz + 1);
                SignalCoverageService.TryGetSource(0, out _, out float coreR1, out _, out _);
                bool signal = PurificationService.IsReached(s, "m50000") && Math.Abs(coreR1 - coreR0 * 1.1f) < 0.01f && Math.Abs(PurificationService.CoverageFactor(s) - 1.1f) < 1e-5f;
                // 100,000：成就与结局专属文本。
                bool achBefore = PurificationService.AchievementReached(s);
                ReturnFarCells(s, 50000, ref cursor);
                WorldSimulation.StepMany(Hz + 1);
                bool ach = !achBefore && PurificationService.AchievementReached(s) && PurificationService.EndingLineKey(s) == "purify.ending_line" && GameText.Has("purify.ending_line")
                           && PurificationService.NextMilestone(s) == null && PurificationService.NextMilestoneText(s).Contains("全部");
                // 再污染已归还的格子：归还面积减少，已达成的奖励不收回。
                long before = map.ReturnedCells;
                GridCell core = HomeGridService.CorePivot(s);
                RepolluteResult rr = PurificationService.Repollute(s, new GridCell(core.X + 600 + 10, core.Y + 600 + 10), 3, 1, "purify.source.test");
                bool keep = map.ReturnedCells < before && map.ReturnedCells == map.RecountReturnedCells() && d.Reached.Length == 4 && Math.Abs(PurificationService.WeatherDamageFactor(s) - 0.75) < 1e-9
                            && rr.CellsRaised > 0;
                Expect(notYet && m5 && weather && rich && oreOk && explored && drill2 && drill1 && cal.Contains("稀土富矿") && signal && ach && keep,
                    $"M1 归还面积里程碑：4,999 格不触发 {notYet}；5,000 格达成（核心备忘通知“{memo5}”、事件 {memoId5}、钩子）{m5}；" +
                    $"天气损伤 -25%（同条件电塔 4 小时酸雨 {lossBefore:0.#} → {lossAfter:0.#}）{weather}；20,000 格揭示稀土富矿（{dep?.X}, {dep?.Y}）{rich}、落在稀土矿脉上 {oreOk}、已探索且通知带位置 {explored}、" +
                    $"压在上面的提取钻每份 ×2 {drill2}、富矿外的 ×1 {drill1}、日历写明 {cal.Contains("稀土富矿")}；50,000 格核心覆盖半径 {coreR0:0.#} → {coreR1:0.#} {signal}；100,000 格成就与结局文本 {ach}；" +
                    $"再污染已归还的格子：归还面积 {before} → {map.ReturnedCells}（重数一致）、奖励不收回 {keep}");
            }
            finally
            {
                GameEvent.RemoveEventListener(PurificationService.CoreMemoEvent, onMemo);
            }
        }

        /// <summary>同一段 4 游戏小时的酸雨里这座建筑掉的耐久。</summary>
        private static float AcidLoss(CampaignState s, BuildingRecord b)
        {
            float h0 = b.Health;
            WeatherService.ScheduleForTests(s, WeatherService.AcidRain, 1.0, 4.2 * GameClock.HourSeconds);
            StepUntil(() => WeatherService.IsActive(s, WeatherService.AcidRain), 4 * Hz);
            StepUntil(() => !WeatherService.IsActive(s, WeatherService.AcidRain), (long)(4.5 * HourTicks));
            return h0 - b.Health;
        }

        /// <summary>直接推进这座提取钻（测试捷径：标成有电；电网与正式推进由 FG4-ECO-02 覆盖），返回每完成一份的产出件数。</summary>
        private static int DrillYield(CampaignState s, BuildingRecord drill, ProductionService.Producer p)
        {
            if (p == null)
            {
                return 0;
            }
            for (int i = 0; i < 200 && p.Rec.Completed < 1; i++)
            {
                drill.PowerState = BuildingPowerState.Powered;
                ProductionService.Step(s, 3, Hz);
            }
            int have = ProductionService.Count(p.Rec.Out, "rare_earth_ore");
            return p.Rec.Completed > 0 ? (int)(have / p.Rec.Completed) : 0;
        }

        // ── B1 稀土富矿：种子测试集（B25）──────────────────────────────────────────

        private static void CheckRichSeeds()
        {
            var rows = new List<string>();
            bool all = true;
            double maxMs = 0;
            foreach (int seed in new[] { 7431, 7432, 7433, 7434, 7435 })
            {
                CampaignState s = NewWorld(seed, power: false);
                int n1 = 0, n2 = 0;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool f1 = PurificationService.TryFindRichCell(s, out GridCell a, ref n1);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                bool f2 = PurificationService.TryFindRichCell(s, out GridCell b, ref n2);
                maxMs = Math.Max(maxMs, ms);
                HomeGridMap map = HomeGridService.MapFor(s);
                map.TerrainSource.Sample(a.X, a.Y, out byte terr, out _);
                GridCell core = HomeGridService.CorePivot(s);
                double dist = Math.Sqrt((a.X - core.X) * (double)(a.X - core.X) + (a.Y - core.Y) * (double)(a.Y - core.Y));
                bool ok = f1 && f2 && a == b && terr == GridContent.TerrainCode("ore_rare") && dist <= PurificationService.SearchRadius;
                all &= ok;
                rows.Add($"种子 {seed}：{a}（离核心 {dist:0} 格，采样 {n1} 次，{ms:0.0} 毫秒）{(ok ? "符合" : "不符合")}"); // 明细行不用 ✓：unity-validate.sh 按 ✓ 计数，明细不算单独的断言（与本段计数器一致）
            }
            PerfLines.Add($"稀土富矿落点搜索最慢 {maxMs:0.0} 毫秒（只在归还 20,000 格时一次）");
            PerfGate.Expect(all, "B1 稀土富矿按生成规则找（先沿规划层的稀土矿带、再粗采样）：五个种子都找得到、落在稀土矿脉上、在搜索半径内、同一种子两次结果相同（B25）\n    " + string.Join("\n    ", rows),
                new[] { PerfGate.Le(maxMs, 50.0, "搜索毫秒") }, Expect, Line);
        }

        // ── N1 负向矩阵 ───────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            CampaignState s = NewWorld(7406);
            Rig rig = LayTower(s, "n1", feed: true, liters: 20000);
            if (rig == null)
            {
                Fail("N1 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ProductionService.Producer p = F.P(s, rig.Tower);
            ForCells(rig.Pivot, 8, (cell, ring) => map.SetPollution(cell, 1));
            StepUntil(() => p.Rec.PurifySupplied, 4 * Hz);
            // ① 玩家关停：状态“已关停”、不推进、不算净化区；重新启用后接着走。
            bool off = BuildingOps.TrySetEnabled(s, rig.Tower.BuildingId, false, out string offMsg);
            long ticks0 = p.Rec.PurifyTicks;
            WorldSimulation.StepMany((int)(StepT / 2));
            bool disabled = off && p.State == ProdState.Disabled && p.Rec.PurifyTicks == ticks0 && !p.Rec.PurifySupplied && !PurificationService.InPurifiedZone(s, rig.Pivot);
            BuildingOps.TrySetEnabled(s, rig.Tower.BuildingId, true, out _);
            WorldSimulation.StepMany(2 * Hz);
            bool reenabled = p.Rec.PurifySupplied && p.Rec.PurifyTicks > ticks0;
            // ② 没电（放在电力覆盖外）：状态“缺电”、不推进、原因写电。
            GridCell? farPivot = null;
            GridCell core = HomeGridService.CorePivot(s);
            for (int d = 30; d <= 38 && farPivot == null; d++)
            {
                farPivot = FindSpot(s, Tower, core, d, d + 0.9f);
            }
            bool noPower = true;
            string noPowerText = "（没有找到电力覆盖外的空地，跳过）";
            if (farPivot != null)
            {
                BuildingRecord far = F.Built(s, Tower, "n1_far", farPivot.Value);
                HomeValleyPowerGrid.Recompute(s);
                WorldSimulation.StepMany((int)(StepT / 3));
                ProductionService.Producer fp = F.P(s, far);
                if (far.PowerState != BuildingPowerState.Powered)
                {
                    noPowerText = F.Reason(s, fp);
                    noPower = fp.State == ProdState.NoPower && fp.Rec.PurifyTicks == 0 && !fp.Rec.PurifySupplied && noPowerText.Contains("电");
                }
            }
            // ③ 净化塔被摧毁：已净化的格子保持不变、归还面积不变、不再推进。
            StepUntil(() => MaxRing(s, rig.Pivot, 0) == 0, StepT + 4 * Hz);
            long area = map.ReturnedCells;
            string snap = RingLevels(s, rig.Pivot, 8);
            int steps0 = PurificationService.StepCount;
            bool died = BuildingOps.ApplyDamage(s, rig.Tower.BuildingId, 100000f, notify: false);
            WorldSimulation.StepMany((int)(StepT * 1.2));
            bool sameRings = RingLevels(s, rig.Pivot, 8) == snap, sameArea = map.ReturnedCells == area, noZone = !PurificationService.InPurifiedZone(s, rig.Pivot),
                 noSteps = PurificationService.StepCount == steps0;
            bool destroyed = died && sameRings && sameArea && noZone && noSteps;
            // ④ 空参数的再污染：不改任何东西。
            RepolluteResult zero = PurificationService.Repollute(s, rig.Pivot, 2, 0);
            RepolluteResult neg = PurificationService.Repollute(s, rig.Pivot, -1, 1);
            bool noop = zero.CellsRaised == 0 && neg.CellsRaised == 0 && RingLevels(s, rig.Pivot, 8) == snap;
            // ⑤ 两座重叠的塔各自推进（重叠处更快；待用户复核的决策）。
            CampaignState s2 = NewWorld(7407);
            Rig a = LayTower(s2, "n1_a", feed: true, liters: 20000);
            bool overlap = false;
            if (a != null)
            {
                GridCell? bp = FindSpot(s2, Tower, a.Pivot, 5f, 7f);
                if (bp != null)
                {
                    BuildingRecord bt = F.Built(s2, Tower, "n1_b", bp.Value);
                    int s0 = PurificationService.StepCount;
                    var bf = new Rig { Tower = bt, Pivot = bp.Value, FeedCell = F.At(bp.Value, 0, -3) };
                    PipeNetworkService.TryPlace(s2, F.At(bp.Value, 0, -2), PipePieceKind.Pipe, 0, 0);
                    PipeNetworkService.TryPlace(s2, bf.FeedCell, PipePieceKind.Pipe, 0, 0);
                    Feed(bf, 20000);
                    F.Resync(s2);
                    WorldSimulation.StepMany((int)(StepT + 4 * Hz));
                    overlap = PurificationService.StepCount >= s0 + 2;
                }
            }
            // ⑥ 修复轮 1：冷却液口外那一格是 2 级以上（塔压在成片重污染里）——放置预览警告“管线铺不过去”、转 180° 口朝干净一侧后不再警告；
            //    照样放下的塔管线确实铺不上，缺冷却液的原因与“净化暂停”通知都写明挪到边缘，而不是让玩家去接一条铺不了的管线。
            bool intake = false;
            string intakeWarn = "（没有找到合适的空地）", intakeReason = string.Empty, intakeNotify = string.Empty;
            GridCell? ip = null;
            GridCell core2 = HomeGridService.CorePivot(s2);
            for (int d = 8; d <= 22 && ip == null; d++)
            {
                for (int dy = -d; dy <= d && ip == null; dy++)
                {
                    for (int dx = -d; dx <= d && ip == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d)
                        {
                            continue;
                        }
                        var c = new GridCell(core2.X + dx, core2.Y + dy);
                        if (HomeGridService.ValidatePlacement(s2, Tower, c, 0, asPlayerPlacement: false, checkCost: false).Ok
                            && HomeGridService.ValidatePipeCell(s2, F.At(c, 0, -2), PipePieceKind.Pipe).Ok && HomeGridService.ValidatePipeCell(s2, F.At(c, 0, 2), PipePieceKind.Pipe).Ok)
                        {
                            ip = c;
                        }
                    }
                }
            }
            if (ip != null)
            {
                HomeGridMap map2 = HomeGridService.MapFor(s2);
                GridCell south = F.At(ip.Value, 0, -2), north = F.At(ip.Value, 0, 2);
                map2.SetPollution(south, 2);
                map2.SetPollution(north, 0);
                GridPlacementResult w0 = HomeGridService.ValidatePlacement(s2, Tower, ip.Value, 0, asPlayerPlacement: true, checkCost: false);
                GridPlacementResult w180 = HomeGridService.ValidatePlacement(s2, Tower, ip.Value, 180, asPlayerPlacement: true, checkCost: false);
                intakeWarn = string.Join(" ", w0.Warnings);
                bool preview = w0.Ok && intakeWarn.Contains("管线铺不过去") && intakeWarn.Contains($"（{south.X}, {south.Y}）") && w180.Ok && !string.Join(" ", w180.Warnings).Contains("管线铺不过去");
                int stalled0 = CountOf("purify_stalled");
                BuildingRecord ct = F.Built(s2, Tower, "n1_c", ip.Value);
                F.Resync(s2);
                bool pipeRefused = !PipeNetworkService.TryPlace(s2, south, PipePieceKind.Pipe, 0, 0).Ok;
                WorldSimulation.StepMany(3 * Hz);
                ProductionService.Producer cp = F.P(s2, ct);
                intakeReason = F.Reason(s2, cp);
                intakeNotify = LastDetail("purify_stalled");
                intake = preview && pipeRefused && cp.State == ProdState.MissingFluid && intakeReason.Contains("管线铺不过去") && intakeReason.Contains("边缘")
                         && CountOf("purify_stalled") > stalled0 && intakeNotify.Contains("管线铺不过去");
            }
            Expect(disabled && reenabled && noPower && destroyed && noop && overlap && intake,
                $"N1 负向：玩家关停 → 已关停、不推进、不算净化区（{offMsg}）{disabled}，启用后接着走 {reenabled}；电力覆盖外的塔缺电、不推进（“{noPowerText.Replace("\n", " / ")}”）{noPower}；" +
                $"塔被摧毁（{died}）→ 已净化的格子不变 {sameRings}、归还面积不变 {sameArea}、不再算净化区 {noZone}、不再推进 {noSteps}；空参数的再污染不改任何东西 {noop}；两座重叠的塔各自推进 {overlap}；" +
                $"冷却液口外那一格 2 级：预览警告“{intakeWarn}”、转 180° 不再警告、管线铺不上、缺冷却液原因“{intakeReason.Replace("\n", " / ")}”、通知“{intakeNotify}” {intake}");
        }

        // ── T1 暂停与 0.5x～3x、T2 观察一致 ─────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            frames = 0;
            Rig rig = LayTower(s, "tm", feed: true, liters: 20000);
            if (rig == null)
            {
                return "no-rig";
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ForCells(rig.Pivot, 8, (cell, ring) => map.SetPollution(cell, (byte)(ring <= 2 ? 1 : 0)));
            WorldSimulation.StepMany(3 * Hz);
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
            long target = GameClock.Ticks + StepT + 600;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            ProducerRecord rec = F.P(s, rig.Tower).Rec;
            return Masked(JsonUtility.ToJson(s)) + "|purify=" + JsonUtility.ToJson(PurificationService.StateOf(s)) +
                   $"|rec={rec.PurifyTicks},{rec.PurifiedLevels},{rec.CoolantUsedMl},{rec.PurifySupplied},{rec.Progress}|rings={RingLevels(s, rig.Pivot, 8)}|area={map.ReturnedCells}";
        }

        private static string Masked(string json) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-f]{32}", "#"), ":build:[0-9a-f]{8}", ":build:#"), "\"[A-Za-z]*Utc\":\"[^\"]*\"", "\"Utc\":\"#\""),
            "\"HasLocation\":(true|false),\"RegionId\":\"[^\"]*\"", "\"HasLocation\":$1,\"RegionId\":\"#\"");

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

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            string diff = string.Empty;
            var shown = new List<string>();
            long steps = StepT + 600;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7441, true, speed, true, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed * GameClock.TicksFor(1f / 60f) - steps) <= 6f * Math.Max(1, GameClock.TicksFor(1f / 60f));
                shown.Add($"{speed}x {frames} 帧");
            }
            bool purified = reference.Contains("rings=0,1,1,") || reference.Contains("rings=0,");
            Expect(paused && same && proportional && purified,
                $"T1 暂停与 0.5x～3x（暂停 {paused}、逐字段一致 {same}、帧数成反比 {proportional}、期间完成一次降级 {purified}）：暂停中 120 帧整份战役状态不动；{string.Join(" / ", shown)} 跑同样的一次降级 + 600 步，" +
                "整份战役状态 JSON、净化进度、冷却液用量、各环等级逐字段一致" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObserved()
        {
            string seen = RunScenario(7442, true, 1f, false, out _, out _);
            string unseen = RunScenario(7442, false, 1f, false, out _, out _);
            Expect(seen == unseen && seen != "no-rig", "T2 家园在观察与不观察时跨过一次降级（冷却液消耗、净化进度、各环等级、归还面积）整份战役状态逐字段一致（FGR-BASE-021）" +
                                                       (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S1 真文件存读档 ──────────────────────────────────────────────────────

        private static string Snap(CampaignState s, string towerId, GridCell pivot, string contaminatedId)
        {
            BuildingRecord b = HomeGridService.FindBuilding(s, towerId);
            ProducerRecord rec = b != null && ProductionService.TryGet(s, towerId, out ProductionService.Producer p) ? p.Rec : null;
            HomeGridMap map = HomeGridService.MapFor(s);
            return $"t={GameClock.Ticks}|purify={JsonUtility.ToJson(PurificationService.StateOf(s))}|rec={rec?.PurifyTicks},{rec?.PurifiedLevels},{rec?.CoolantUsedMl},{rec?.PurifySupplied}" +
                   $"|rings={RingLevels(s, pivot, 8)}|area={map.ReturnedCells}/{map.RecountReturnedCells()}|contam={PurificationService.IsContaminated(s, contaminatedId)}" +
                   $"|wx={PurificationService.WeatherDamageFactor(s):0.00}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7408);
            Rig rig = LayTower(s, "s1", feed: true, liters: 50000);
            if (rig == null)
            {
                Fail("S1 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ForCells(rig.Pivot, 8, (cell, ring) => map.SetPollution(cell, (byte)(ring <= 4 ? 2 : 1)));
            GridCell? yAt = FindSpot(s, "power_pole", rig.Pivot, 14f, 20f);
            BuildingRecord y = yAt != null ? F.Built(s, "power_pole", "s1_y", yAt.Value) : null;
            if (y != null)
            {
                PurificationService.Repollute(s, yAt.Value, 0, 1, "purify.source.test");
            }
            int cursor = 0;
            ReturnFarCells(s, 5000, ref cursor);
            // 净化进行中（两次降级之后再走半个周期）存档。
            StepUntil(() => F.P(s, rig.Tower).Rec.PurifiedLevels > 0 && PurificationService.StepCount >= 2, 3 * StepT);
            WorldSimulation.StepMany((int)(StepT / 2));
            string yid = y?.BuildingId ?? "none";
            string before = Snap(s, rig.Tower.BuildingId, rig.Pivot, yid);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = GameClock.Ticks + StepT + 300;
            void Continue() => WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            Continue();
            string continuous = Snap(s, rig.Tower.BuildingId, rig.Pivot, yid);
            string loaded = null, after = null;
            for (int round = 0; round < 2; round++)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                PurificationService.ResetForTests();
                PurificationService.Install();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    Fail("S1 读档失败：" + rr.Message);
                    return;
                }
                CampaignState l = rr.State;
                CampaignSession.Set(Slot, l);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                string ld = Snap(l, rig.Tower.BuildingId, rig.Pivot, yid);
                Continue();
                string af = Snap(l, rig.Tower.BuildingId, rig.Pivot, yid);
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
            bool ok = save.Success && loaded == before && after == continuous && before.Contains("contam=True") && before.Contains("wx=0.75");
            Expect(ok, $"S1 净化进行中真文件存读档（净化进度、各环等级、归还面积（读档按差异重数）、受污染名单、已达成的里程碑）：读回与存前逐项一致 {loaded == before}，" +
                       $"接着跑与一直跑一致 {after == continuous}（两次读档相同）\n    存前 {before}" + (loaded == before ? string.Empty : "\n    读回 " + loaded) +
                       (after == continuous ? string.Empty : "\n    一直跑 " + continuous + "\n    读档后 " + after));
        }

        // ── U1 界面 ───────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(7409);
            Rig rig = LayTower(s, "u1", feed: true, liters: 20000);
            if (rig == null)
            {
                Fail("U1 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ForCells(rig.Pivot, 8, (cell, ring) => map.SetPollution(cell, (byte)(ring <= 3 ? 2 : 0)));
            StepUntil(() => F.P(s, rig.Tower).Rec.PurifySupplied, 4 * Hz);
            // 悬停格子（建造模式状态行）：干净的空地不打扰；有污染写等级、影响、哪座塔在净化、多久降到 0 级。
            GridCell ring2 = new GridCell(rig.Pivot.X + 2, rig.Pivot.Y);
            GridCell cleanCell = new GridCell(rig.Pivot.X + 6, rig.Pivot.Y);
            // 远处（净化塔范围外）的一格：取对角线上第一个已探索的格子（按种子的探索区，不写死坐标）。
            GridCell far = new GridCell(rig.Pivot.X + 12, rig.Pivot.Y + 12);
            for (int d = 30; d >= 12; d--)
            {
                var c = new GridCell(rig.Pivot.X + d, rig.Pivot.Y + d);
                if (map.IsExploredNoLoad(c))
                {
                    far = c;
                    break;
                }
            }
            map.SetPollution(far, 1);
            string clean = PurificationService.StatusLine(s, cleanCell, false);
            string cleanOverlay = PurificationService.StatusLine(s, cleanCell, true) ?? string.Empty;
            string level2 = PurificationService.StatusLine(s, ring2, false) ?? string.Empty;
            string farLine = PurificationService.StatusLine(s, far, false) ?? string.Empty;
            // 修复轮 1（P2）：迷雾里的格子只写“未探索”、不读污染，也不为悬停去生成区块。
            GridCell fogCell = new GridCell(rig.Pivot.X + 400, rig.Pivot.Y + 400);
            int gen0 = map.SyncGeneratedCount;
            string fogOverlay = PurificationService.StatusLine(s, fogCell, true) ?? string.Empty;
            string fogPlain = PurificationService.StatusLine(s, fogCell, false);
            bool fogOk = !map.IsExploredNoLoad(fogCell) && fogOverlay.Contains("未探索") && !fogOverlay.Contains("污染 ") && fogPlain == null && map.SyncGeneratedCount == gen0;
            bool hover = clean == null && cleanOverlay.Contains("污染 0 级") && cleanOverlay.Contains("净化区") && level2.Contains("污染 2 级") && level2.Contains("不能建造")
                         && level2.Contains("降到 0 级") && farLine.Contains("污染 1 级") && farLine.Contains("加倍") && farLine.Contains("没有净化塔覆盖")
                         && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstPollutionSeen) && fogOk;
            // 暂停时写原因。
            BuildingOps.TrySetEnabled(s, rig.Tower.BuildingId, false, out _);
            WorldSimulation.StepMany(Hz);
            string pausedLine = PurificationService.StatusLine(s, ring2, false) ?? string.Empty;
            BuildingOps.TrySetEnabled(s, rig.Tower.BuildingId, true, out _);
            WorldSimulation.StepMany(2 * Hz);
            bool pausedOk = pausedLine.Contains("暂停");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = PurificationService.StatusLine(s, ring2, false) ?? string.Empty;
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool english = en.Contains("pollution level 2") && en.Contains("level 0");
            // 叠加层：污染叠加层画每座塔的范围与净化前沿，标签写半径 / 第几环、核心处写已归还。
            OverlayService.Set(OverlayKind.Pollution);
            OverlayService.RedrawNow();
            bool overlay = OverlayService.DrawnPurifiers == 1 && OverlayService.DrawnMarkers >= 2 && OverlayService.Labels.Any(l => l.Text.Contains("半径 8") && l.Text.Contains("环"))
                           && OverlayService.Labels.Any(l => l.Text.Contains("已归还"));
            OverlayService.Set(OverlayKind.None);
            // 顶栏“已归还 N 格”（悬停写口径与下一个里程碑）、日历页脚的归还面积段。
            VisualElement root = F.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            bool topbar = false;
            string chip = string.Empty, tip = string.Empty;
            try
            {
                var bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                bar.RefreshResources(s, force: true);
                for (int i = 0; i < bar.ResourceChipCount; i++)
                {
                    if (bar.ResourceChipText(i).Contains("已归还"))
                    {
                        chip = bar.ResourceChipText(i);
                        tip = bar.ResourceChipTip(i);
                    }
                }
                topbar = chip.Contains("已归还") && tip.Contains("下一个里程碑") && tip.Contains("5,000");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            string cal = PurificationService.CalendarText(s);
            bool calendar = cal.Contains("已归还") && cal.Contains("下一个里程碑");
            // 统计面板“物流与能源”页的环境段：已归还 / 下一个里程碑、按游戏小时的趋势（跨过 2 个整点后出现）、净化塔在净化 / 暂停。
            WorldSimulation.StepMany((int)(2 * HourTicks + Hz));
            var statsLines = new List<(string Text, string Cls)>();
            PurificationService.BuildStatsLines(s, statsLines);
            string stats = string.Join(" / ", statsLines.Select(l => l.Text));
            bool statsOk = stats.Contains("环境 · 净化与归还面积") && stats.Contains("已归还") && stats.Contains("最近") && stats.Contains("净化塔 1 座");
            // 生产面板：净化半径、冷却液、前沿与预计完成时间；进度条 = 离下一次降级的进度。
            VisualElement proot = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            bool panelOk = false;
            string detail = string.Empty, progress = string.Empty;
            try
            {
                ProductionPanelUIToolkit panel = pgo.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(proot);
                ProductionPanelUIToolkit.Open(rig.Tower.BuildingId);
                panel.Refresh();
                detail = panel.DetailText;
                progress = panel.ProgressText;
                panelOk = panel.PanelVisible && detail.Contains("净化半径 8 格") && detail.Contains("净化前沿") && progress.Contains("下一次降级") && panel.StateText.Contains("工作中");
            }
            finally
            {
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(pgo);
            }
            // 放置预览：半径内多少格要净化、大约多久。
            GridPlacementResult pr = HomeGridService.ValidatePlacement(s, Tower, F.At(rig.Pivot, 0, 0), 0, asPlayerPlacement: false, checkCost: false);
            bool preview = pr.Notes.Any(n => n.Contains("净化半径 8 格"));
            Expect(hover && pausedOk && english && overlay && topbar && calendar && statsOk && panelOk && preview,
                $"U1 界面：悬停格子——干净空地不打扰、叠加层开着写“{cleanOverlay.Replace("\n", " / ")}”、2 级写“{level2.Replace("\n", " / ")}”、远处 1 级写“{farLine.Replace("\n", " / ")}”、迷雾格写“{fogOverlay.Replace("\n", " / ")}”且不生成区块 {fogOk} {hover}；" +
                $"塔暂停时写原因 {pausedOk}；英文 {english}；污染叠加层画范围圈与前沿、标签 {overlay}；顶栏“{chip}”悬停“{tip.Replace("\n", " / ")}” {topbar}；日历“{cal.Replace("\n", " / ")}” {calendar}；统计面板“{stats}” {statsOk}；" +
                $"生产面板“{detail.Replace("\n", " / ")}”“{progress}” {panelOk}；放置预览写半径内要净化多少格 {preview}");
        }

        // ── P3 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7410);
            Rig rig = LayTower(s, "p3", feed: true, liters: 20000);
            if (rig == null)
            {
                Fail("P3 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            rig.Tower.Tier = 3;
            BuildingOps.OnUpgradeCompleted(s, rig.Tower);
            ProducerRecord rec = F.P(s, rig.Tower).Rec;
            // 最坏情况一：T3 半径 16 内全是 3 级（每次降一整环）；最坏情况二：只有最外一环有污染（每次扫完整个圆）。
            ForCells(rig.Pivot, 16, (cell, ring) => map.SetPollution(cell, 3));
            PurificationService.ResetStepStats();
            for (int i = 0; i < 10; i++)
            {
                PurificationService.PurifyStep(s, rig.Tower, rec);
            }
            double fullMs = PurificationService.MaxStepMs;
            ForCells(rig.Pivot, 16, (cell, ring) => map.SetPollution(cell, (byte)(ring == 16 ? 3 : 0)));
            PurificationService.ResetStepStats();
            for (int i = 0; i < 3; i++)
            {
                PurificationService.PurifyStep(s, rig.Tower, rec);
            }
            double scanMs = PurificationService.MaxStepMs;
            // 40 座塔（夹具：直接登记、标成正在净化）时净化区查询的单次开销（酸雨每分钟只对真有整点伤害的目标查）。
            for (int i = 0; i < 40; i++)
            {
                BuildingRecord b = F.Built(s, Tower, "p3_" + i, new GridCell(rig.Pivot.X + 200 + (i % 8) * 40, rig.Pivot.Y + 200 + (i / 8) * 40));
                ProducerRecord r = F.P(s, b)?.Rec;
                if (r != null)
                {
                    r.PurifySupplied = i % 2 == 0;
                }
            }
            int towers = PurificationService.PurifiersOf(s).Count;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int hits = 0;
            for (int i = 0; i < 10000; i++)
            {
                hits += PurificationService.InPurifiedZone(s, new GridCell(rig.Pivot.X + 200 + (i % 300), rig.Pivot.Y + 200 + (i / 50) % 200)) ? 1 : 0;
            }
            double zoneUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency / 10000.0;
            // 修复轮 1（P2）：塔的进度缓存按范围区块失效——范围外的格网改动不让任何塔重扫；范围内的改动只让碰到的塔重扫；全部同时失效时测一次全量重算。
            List<ProductionService.Producer> all = PurificationService.PurifiersOf(s).ToList();
            foreach (ProductionService.Producer p in all)
            {
                PurificationService.FrontOf(s, p.Building, out _, out _, out _);
            }
            int scans0 = PurificationService.InfoScans;
            map.SetPollution(new GridCell(rig.Pivot.X - 300, rig.Pivot.Y - 300), 2); // 远离所有塔
            foreach (ProductionService.Producer p in all)
            {
                PurificationService.FrontOf(s, p.Building, out _, out _, out _);
            }
            int farScans = PurificationService.InfoScans - scans0;
            BuildingRecord near = all.Select(p => p.Building).First(b => b.GridX == rig.Pivot.X + 200 && b.GridY == rig.Pivot.Y + 200);
            Toggle(map, new GridCell(near.GridX + 3, near.GridY));
            scans0 = PurificationService.InfoScans;
            foreach (ProductionService.Producer p in all)
            {
                PurificationService.FrontOf(s, p.Building, out _, out _, out _);
            }
            int nearScans = PurificationService.InfoScans - scans0;
            bool cacheOk = farScans == 0 && nearScans >= 1;
            foreach (ProductionService.Producer p in all)
            {
                Toggle(map, new GridCell(p.Building.GridX + 2, p.Building.GridY + 2)); // 每座塔的范围里各改一格：全部失效
            }
            scans0 = PurificationService.InfoScans;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (ProductionService.Producer p in all)
            {
                PurificationService.FrontOf(s, p.Building, out _, out _, out _);
            }
            double allMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int allScans = PurificationService.InfoScans - scans0;
            PerfLines.Add($"T3 一次降级（整环 3 级）最慢 {fullMs:0.000} 毫秒、只剩最外环（扫完整个圆）最慢 {scanMs:0.000} 毫秒；{towers} 座塔时净化区查询平均 {zoneUs:0.00} 微秒（命中 {hits}）；" +
                          $"进度缓存：范围外改动重扫 {farScans} 座、范围内改动重扫 {nearScans} 座、{allScans} 座同时失效全量重算 {allMs:0.000} 毫秒");
            PerfGate.Expect(towers >= 41 && hits > 0 && cacheOk && allScans == all.Count,
                $"P3 性能（Editor batchmode，Mono JIT；真机 HybridCLR 解释执行另测）：T3 一次降级 ≤ 1 毫秒（整环 {fullMs:0.000} / 扫圆 {scanMs:0.000}）、每座塔每 2 游戏小时只做一次；{towers} 座塔净化区查询 ≤ 20 微秒（{zoneUs:0.00}）；" +
                $"进度缓存按范围区块失效（范围外改动重扫 {farScans} 座、范围内 {nearScans} 座）{cacheOk}，{allScans} 座同时失效全量重算 ≤ 5 毫秒（{allMs:0.000}）",
                new[] { PerfGate.Le(fullMs, 1.0, "整环降级毫秒"), PerfGate.Le(scanMs, 1.0, "扫圆毫秒"), PerfGate.Le(zoneUs, 20.0, "净化区查询微秒"), PerfGate.Le(allMs, 5.0, "全部塔同时重算毫秒") }, Expect, Line);
        }

        /// <summary>把一格的污染改成另一个值（1 ↔ 2），保证真的改动了格网。</summary>
        private static void Toggle(HomeGridMap map, GridCell c) => map.SetPollution(c, (byte)(map.GetPollution(c) == 1 ? 2 : 1));

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgPurificationSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
