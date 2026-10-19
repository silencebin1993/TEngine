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
using GameLogic.Campaign.Primitive;
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
    /// FG8-EXP-04 遗迹与人类遗产的自动验收（FG08 FGR-EXP-040；FG17 FGR-GEN-033；卡片验收“收集流程的旅程片段”、负向“货舱满装不下：留在原地，下次可取”）。
    /// 全部起真实系统：真实统一时钟与世界模拟、真实信号塔覆盖、真实寻路与工单（机器走过去回收 / 取回）、真实货舱占用（LootPickService）、
    /// 真实入库检查（CargoService 每秒检查，与黑匣子同一套）、真实经济账本 / 固件存放 / 解锁记录、真文件存读档、真 UXML 面板（保管库 / 建筑面板 / 图鉴）。
    /// D1 数据；P1 放置（种子集：每件恰好一处、在距离范围里、生成器的遗迹点、互不重复、剧情需要的可到达、确定性、与先后无关、旧档已回收的不选）；
    /// J1 收集流程旅程片段（右键遗迹派机器 → 找到遗产装进货舱 → 带回核心入库 → 展柜 / 收集页 / 图鉴）；E1 四类效果；N1 负向矩阵（货舱满留在原地、
    /// 下次可取、仍满时继续留着、携带者阵亡掉落再取、迷雾 / 已入库、建造压遗迹点 FG-GAP-127）；T1 暂停与 0.5x～3x；T2 观察一致；S1 真文件存读档；U1 界面；P2 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgHeritageSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgHeritageSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgHeritageSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        /// <summary>放置检查的种子集（与 FG17 种子测试集同口径：不同方向、不同领地布局）。</summary>
        private static readonly int[] SeedSet = { 8831, 8861, 9001, 12345, 777, 31337 };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static HomeValleyController _home;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 遗迹与人类遗产")]
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
            Line("\n[遗迹与人类遗产] 约 20 件遗产按种子放进生成器的遗迹点、回收时装进货舱、带回入库与四类效果、展柜 / 收集页 / 图鉴、货舱满留在原地、建造压遗迹点、倍速 / 观察 / 存读档（FG8-EXP-04）");
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
            string originalCodex = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgheritage-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                HeritageService.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_mechanics.json");
                MechanicCodex.ResetForTests();
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "遗产在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行）：挑遗迹点每个世界一次，其余只在下令 / 完工 / 面板刷新 / 建造校验时发生；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckPlacement);
                Step(CheckJourney);
                Step(CheckEffects);
                Step(CheckCargoFull);
                Step(CheckDeathAndMisc);
                Step(CheckExtraction);
                Step(CheckReach);
                Step(CheckBuildBlock);
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
                Fail($"遗迹与人类遗产自检抛异常：{e}");
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
                HeritageVaultPanelUIToolkit.InWorldOverrideForTests = false;
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
                MechanicCodex.FilePathOverrideForTests = originalCodex;
                MechanicCodex.ResetForTests();
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
            Line($"  · [遗迹与人类遗产] 断言通过 {_pass}，失败 {_fail}");
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
            HeritageService.ResetForTests();
        }

        /// <summary>新战役（主菜单“新建”同一条 CampaignState.CreateNew → LoadHome 路径），开局发电机 / 装配站修好。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            ResetServices();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgheritage-" + seed, "Standard", seed);
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

        /// <summary>场景夹具：开局的信号塔修好通电，走到下一次覆盖评估（覆盖内的遗迹点被揭开）。</summary>
        private static void CoverHome(CampaignState s)
        {
            BuildingRecord t = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);
            t.ConstructionState = BuildingConstructionState.Operational;
            t.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
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

        private static List<WorldFeature> ExploredRelics(CampaignState s, int half)
        {
            GridCell c = HomeGridService.CorePivot(s);
            var list = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, c.X - half, c.Y - half, c.X + half, c.Y + half, list);
            HomeGridMap map = HomeGridService.MapFor(s);
            return list.Where(f => f.Kind == WorldFeatureKind.Relic && HomeExplorationService.IsFound(s, f.Id) && map.IsExploredNoLoad(new GridCell(f.X, f.Y)))
                .OrderBy(f => f.Distance).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// 测试捷径（只为不让机器走 250～900 格）：把这件遗产挪到信号塔覆盖内的一处遗迹点（与原来放在那里的遗产互换，仍是一件一处）。
        /// 放置本身（距离范围 / 种子 / 唯一）由 P1 用正式结果单独验收。
        /// </summary>
        private static void Fixture(CampaignState s, string heritageId, string relicId)
        {
            HeritageService.EnsurePlaced(s);
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            int i = Array.IndexOf(st.HeritageIds, heritageId);
            int j = Array.IndexOf(st.HeritageRelics, relicId);
            if (j >= 0)
            {
                st.HeritageRelics[j] = st.HeritageRelics[i];
            }
            st.HeritageRelics[i] = relicId;
            HeritageService.Touch();
        }

        private static HomeValleyMachineMarker Hauler(int skip = 0)
        {
            return _home.MachineMarkersForTests.Where(x => x != null && x.IsValid && MachineRegistry.TryGetRecord(x.LogicId, out MachineRecord r)
                                                           && (r.ChassisId == HomeValleyLayout.Erc001ChassisId || r.ChassisId == HomeValleyLayout.Erc002ChassisId))
                .OrderBy(x => x.LogicId).Skip(skip).FirstOrDefault();
        }

        private static HomeValleyMachineMarker MarkerOf(int logicId) => _home.MachineMarkersForTests.FirstOrDefault(x => x != null && x.IsValid && x.LogicId == logicId);

        private static Ray RayAt(int x, int y) => new Ray(new Vector3(x + 0.6f, 40f, y), Vector3.down);

        /// <summary>真实移动命令：把机器开回核心附近（玩家右键地面同一个移动入口）。</summary>
        private static void SendHome(CampaignState s, int logicId)
        {
            GridCell core = HomeGridService.CorePivot(s);
            MarkerOf(logicId)?.CommandMoveTo(new Vector3(core.X + 9f, 0f, core.Y + 1f));
        }

        private static void FillHold(CampaignState s, int logicId)
        {
            MachineRegistry.TryGetRecord(logicId, out MachineRecord m);
            int cap = LootPickCatalog.CapacityOf(s, m);
            LootPacketState st = LootPickService.StateOf(s);
            st.Held = (st.Held ?? Array.Empty<LootHeldRecord>()).Append(new LootHeldRecord
            {
                HeldId = "test:fill:" + logicId, Machine = logicId, Kind = LootPickCatalog.KindItem, ItemId = "combat_component", Amount = 1, Space = cap,
            }).ToArray();
        }

        private static void EmptyHold(CampaignState s, int logicId)
        {
            LootPacketState st = LootPickService.StateOf(s);
            st.Held = st.Held.Where(h => h == null || h.HeldId != "test:fill:" + logicId).ToArray();
        }

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static string LastDetail(string typeId) =>
            NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId)?.Latest?.Detail ?? string.Empty;

        private static string AllDetails(string typeId) =>
            string.Join(" | ", NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Select(e => e.Latest?.Detail ?? string.Empty));

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

        private static string Placement(CampaignState s)
        {
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            return string.Join(";", st.HeritageIds.Select((id, i) => id + "@" + st.HeritageRelics[i]));
        }

        // ── D1 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_heritage as p;print('\\n'.join(l for l in p.dump_lines() if l.startswith('HER')))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("HER\t", StringComparison.Ordinal)).ToList();
            var rt = t.TbHeritage.DataList.Select(h => string.Join("\t", "HER", h.Id, h.Category, h.NameKey, h.DescKey, h.Act, h.MinDistance, h.MaxDistance, h.UnlockId, h.TechData, h.Story, h.SortOrder)).ToList();
            bool table = code == 0 && src.Count == rt.Count && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            IReadOnlyList<Heritage> all = HeritageService.All;
            bool count = all.Count >= 16 && all.Count <= 24 && HeritageService.Categories.All(c => all.Any(h => h.Category == c));
            // 预制固件 = 固件表里获取途径为“人类遗产”的那几条，一一对应（否则有的固件永远拿不到）。
            var relicFw = FirmwareKinds.Rows.Where(r => r.Source == "relic").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fwRows = all.Where(h => h.Category == HeritageService.CatFirmware).Select(h => h.UnlockId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            bool fw = relicFw.Count > 0 && relicFw.SequenceEqual(fwRows);
            bool acts = all.All(h => h.Act >= 1 && h.Act <= 3 && h.MinDistance < h.MaxDistance) && Enumerable.Range(1, 3).All(a => all.Any(h => h.Act == a))
                        && all.Where(h => h.Act == 2).Min(h => h.MinDistance) >= all.Where(h => h.Act == 1).Max(h => h.MinDistance);
            bool notify = t.TbNotifyType.GetOrDefault(HeritageService.NotifyType)?.AwaySection == "research";
            bool hooks = new[] { GuidanceHooks.HeritageFirstFound, GuidanceHooks.HeritageFirstLeftBehind, GuidanceHooks.HeritageFirstDelivered, GuidanceHooks.HeritageVaultFirstOpen }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool codex = t.TbCodexEntry.GetOrDefault("codex.heritage") is var c && c != null && GameText.Has(c.TitleKey) && GameText.Has(c.BodyKey);
            var keys = new List<string>
            {
                "heritage.notify.found", "heritage.notify.left", "heritage.notify.picked", "heritage.notify.still_full", "heritage.notify.delivered", "heritage.notify.dropped",
                "heritage.effect.blueprint", "heritage.effect.firmware", "heritage.effect.memento", "heritage.effect.archive", "heritage.state.unknown", "heritage.state.ground",
                "heritage.state.carried", "heritage.state.obtained", "relic.hover.heritage_left", "ui.map.relic.heritage_left", "relic.order.heritage", "relic.order.heritage_pool",
                "heritage.work.label", "grid.reason.relic_site", "fwlib.origin.heritage", "heritage.panel.open", "heritage.vault.title", "heritage.vault.status",
                "heritage.vault.status_moving", "heritage.vault.tab.showcase", "heritage.vault.tab.collection", "heritage.vault.empty_showcase", "heritage.vault.detail_hint",
                "heritage.vault.detail_locked", "heritage.vault.footer", "codex.tab.heritage", "codex.heritage.locked_hint", "heritage.cargo.name", "heritage.name_fmt",
            };
            foreach (string cat in HeritageService.Categories)
            {
                keys.Add("heritage.cat." + cat);
                keys.Add("heritage.cat." + cat + ".effect");
            }
            foreach (Heritage h in all)
            {
                keys.Add(h.NameKey);
                keys.Add(h.DescKey);
            }
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            bool space = HeritageService.Space == 2 && GridContent.TryGetTuning("heritage.pickup_seconds", out float ps) && ps > 0f && GridContent.TuningInt("relic.block_radius") >= 0
                         && new[] { "heritage.reach_check_seconds", "heritage.reach_latency_seconds", "heritage.reach_near_cells", "heritage.reach_max_repicks" }.All(k => GridContent.TryGetTuning(k, out float rv) && rv > 0f);
            Expect(table && count && fw && acts && notify && hooks && codex && missing.Count == 0 && space,
                $"D1 数据：fg.TbHeritage 与源数据逐行一致（源 {src.Count} 行 / 表 {rt.Count} 行）{table}；{all.Count} 件、四类齐全 {count}；预制固件 = 固件表里获取途径为人类遗产的 {relicFw.Count} 条一一对应 {fw}；" +
                $"三幕都有、后幕更远 {acts}；通知进离家报告“研究与解析” {notify}；引导钩子 {hooks}；图鉴“遗迹与人类遗产” {codex}；占货舱 {HeritageService.Space} 格 {space}；中英文本齐全（缺 {string.Join("、", missing)}）");
        }

        // ── P1 放置（FGR-GEN-033）───────────────────────────────────────────────

        private static void CheckPlacement()
        {
            var lines = new List<string>();
            bool allOk = true;
            var placements = new List<string>();
            foreach (int seed in SeedSet)
            {
                CampaignState s = CampaignState.CreateNew("fgheritage-place-" + seed, "Standard", seed);
                CampaignSession.Set(Slot, s);
                HeritageService.EnsurePlaced(s);
                HomeExplorationState st = HomeExplorationService.StateOf(s);
                var bad = new List<string>();
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (Heritage h in HeritageService.All)
                {
                    string relic = HeritageService.RelicOf(s, h.Id);
                    if (relic == null || !HomeExplorationService.TryGetRelic(s, relic, out HomeExplorationService.RelicInfo info) || info.Kind != WorldFeatureKind.Relic)
                    {
                        bad.Add(h.Id + " 没有遗迹点");
                        continue;
                    }
                    if (info.Distance < h.MinDistance || info.Distance > h.MaxDistance)
                    {
                        bad.Add($"{h.Id} 距离 {info.Distance} 不在 {h.MinDistance}～{h.MaxDistance}");
                    }
                    if (!used.Add(relic))
                    {
                        bad.Add(h.Id + " 与别的遗产共用 " + relic);
                    }
                    PlannedTerritory tt = WorldGenQuery.TerritoryAt(s, info.X, info.Y);
                    if (tt != null && !tt.IsFaction)
                    {
                        bad.Add(h.Id + " 在白潮滩头预留区");
                    }
                    if (h.Story == 1 && (HomeExplorationService.HazardAt(s, info.X, info.Y, false, out _, out _) != null || !WorldGenQuery.IsWalkableGenerated(s, info.X, info.Y)))
                    {
                        bad.Add(h.Id + "（剧情需要）在危害区或不可走");
                    }
                    if (HeritageService.AtRelic(s, relic)?.Id != h.Id || HeritageService.StatusOf(s, h.Id) != HeritageStatus.Hidden)
                    {
                        bad.Add(h.Id + " 反查不一致");
                    }
                }
                // 确定性：同一种子从头再来一次结果相同；先在世界里转一大圈（揭开大片区域、查点位、悬停）再挑，结果也相同（与玩家先去哪里无关）。
                string first = Placement(s);
                CampaignState again = CampaignState.CreateNew("fgheritage-place2-" + seed, "Standard", seed);
                CampaignSession.Set(Slot, again);
                GridCell core = HomeGridService.CorePivot(again);
                HomeGridService.RevealArea(again, new Vector2(core.X + 600, core.Y - 400), 400f);
                HomeExplorationService.RelicAt(again, new Vector2(core.X + 300, core.Y));
                HomeExplorationService.RecoverableRelics(again);
                HeritageService.EnsurePlaced(again);
                bool same = Placement(again) == first;
                bool ok = bad.Count == 0 && same && st.HeritageIds.Length == HeritageService.All.Count && !string.IsNullOrEmpty(st.HeritageWorld);
                allOk &= ok;
                placements.Add(first);
                lines.Add($"种子 {seed}：{(ok ? "通过" : "失败")}" + (bad.Count > 0 ? "（" + string.Join("；", bad.Take(4)) + "）" : string.Empty) + (same ? string.Empty : "（两次结果不同）"));
            }
            bool differ = placements.Distinct().Count() == placements.Count;
            Expect(allOk && differ,
                $"P1 种子集 {SeedSet.Length} 个：每件遗产恰好放在一处生成器给出的遗迹点里、在它那一幕的距离范围内、互不共用、不在白潮滩头预留区，剧情需要的不在危害区且地面可走；" +
                $"同一种子两次挑选（第二次先揭开大片区域、查点位）结果一致，不同种子各不相同 {differ}\n    " + string.Join("\n    ", lines));

            // 旧档：存档里已经回收过的遗迹点（遗产这个功能上线前的旧档）不再选——否则那件遗产永远拿不到。
            CampaignState a = CampaignState.CreateNew("fgheritage-old-a", "Standard", SeedSet[0]);
            CampaignSession.Set(Slot, a);
            HeritageService.EnsurePlaced(a);
            string taken = HeritageService.RelicOf(a, HeritageService.All[0].Id);
            CampaignState legacy = CampaignState.CreateNew("fgheritage-old-b", "Standard", SeedSet[0]);
            CampaignSession.Set(Slot, legacy);
            HomeExplorationState lst = HomeExplorationService.StateOf(legacy);
            lst.RelicsRecovered = lst.RelicsRecovered.Append(taken).ToArray();
            lst.RelicsRecoveredTicks = lst.RelicsRecoveredTicks.Append(0L).ToArray();
            lst.HeritageIds = null;
            lst.HeritageRelics = null;
            lst.HeritageObtained = null;
            CampaignFgStateDomains.EnsureAll(legacy);
            HeritageService.EnsurePlaced(legacy);
            string moved = HeritageService.RelicOf(legacy, HeritageService.All[0].Id);
            bool oldOk = taken != null && moved != null && moved != taken && lst.HeritageIds.Length == HeritageService.All.Count
                         && HeritageService.All.All(h => HeritageService.RelicOf(legacy, h.Id) != taken);
            Expect(oldOk, $"P1 旧档（没有遗产字段、{taken} 已经回收过）：读回空字段不报错，挑选时跳过已回收的遗迹点，{HeritageService.All[0].Id} 改放到 {moved}");
        }

        // ── J1 收集流程旅程片段（卡片验收）───────────────────────────────────────

        private static void CheckJourney()
        {
            CampaignState s = NewWorld(8901);
            CoverHome(s);
            List<WorldFeature> near = ExploredRelics(s, 320);
            HomeValleyMachineMarker m = Hauler();
            if (near.Count < 1 || m == null)
            {
                Fail($"J1 信号塔覆盖内找不到遗迹点（{near.Count}）或搬运机");
                return;
            }
            Heritage memento = HeritageService.All.First(h => h.Category == HeritageService.CatMemento);
            WorldFeature relic = near[0];
            Fixture(s, memento.Id, relic.Id);
            HomeExplorationService.TryGetRelic(s, relic.Id, out HomeExplorationService.RelicInfo info);
            string hoverBefore = HomeExplorationService.TryDescribeRelic(s, relic.Id, out _, out string hb0) ? hb0 : string.Empty;
            int found0 = CountOf(HeritageService.NotifyType);
            // 1. 选中搬运机，右键遗迹点（与玩家同一条情境命令路径）。
            bool handled = _home.ContextRelicForTests(m, RayAt(relic.X, relic.Y));
            bool recovered = StepUntil(() => HomeExplorationService.IsRecovered(s, relic.Id), 900L * Hz);
            RegionQuestItemRecord q = HeritageService.FindItem(s, memento.Id);
            bool carried = q != null && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == m.LogicId
                           && HeritageService.StatusOf(s, memento.Id) == HeritageStatus.Carried;
            MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord mr);
            int used = LootPickService.UsedSpace(s, mr);
            var lines = new List<CargoLine>();
            CargoService.LinesOf(s, m.LogicId, lines);
            bool cargoLine = lines.Any(l => l.Kind == CargoService.LineQuest && l.Text.Contains(GameText.Get(memento.NameKey)));
            string foundText = LastDetail(HeritageService.NotifyType);
            bool notified = CountOf(HeritageService.NotifyType) == found0 + 1 && foundText.Contains(GameText.Get(memento.NameKey)) && foundText.Contains(MachineNaming.Short(m.LogicId))
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.HeritageFirstFound);
            bool noSpoiler = !hoverBefore.Contains(GameText.Get(memento.NameKey));
            Expect(handled && recovered && carried && used >= HeritageService.Space && cargoLine && notified && noSpoiler,
                $"J1 ①右键 {info.Ring?.Id} 遗迹 {relic.Id}（{info.Distance} 格）派 {MachineNaming.Short(m.LogicId)} 回收：回收前悬停不剧透遗产 {noSpoiler}；完工后找到“{GameText.Get(memento.NameKey)}”装进它的货舱（占用 {used} 格，货舱面板列出 {cargoLine}），" +
                $"通知“{foundText}”");
            // 2. 带回核心：玩家把机器开回家（移动命令），到核心 / 仓库附近即入库。
            int notes0 = CountOf(HeritageService.NotifyType);
            SendHome(s, m.LogicId);
            bool home = StepUntil(() => HeritageService.IsObtained(s, memento.Id), 1200L * Hz);
            bool showcase = HeritageService.Showcase(s).Contains(memento.Id) && q.State == RegionQuestItemState.Recovered && HeritageService.StatusOf(s, memento.Id) == HeritageStatus.Obtained;
            string delivered = LastDetail(HeritageService.NotifyType);
            bool codex = MechanicCodex.IsUnlocked(HeritageService.CodexIdOf(memento.Id)) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.HeritageFirstDelivered);
            HeritageService.Counts c = HeritageService.CountsOf(s);
            bool counts = c.Obtained == 1 && c.Total == HeritageService.All.Count && c.ObtainedBy[Array.IndexOf(HeritageService.Categories, HeritageService.CatMemento)] == 1;
            string status = HeritageService.StatusText(s);
            Expect(home && showcase && CountOf(HeritageService.NotifyType) == notes0 + 1 && delivered.Contains(GameText.Get("heritage.effect.memento")) && codex && counts
                   && status.Contains($"1 / {HeritageService.All.Count}"),
                $"J1 ②开回核心附近：纪念物存入核心保管库（展柜里有它 {showcase}），通知“{delivered}”，图鉴“人类遗产”条目解锁 {codex}，收集进度“{status.Split('\n')[0]}”");
            // 3. 同一处遗迹不会再出第二件；已入库的遗产再走一次入库不重复（幂等）。
            HeritageService.OnDelivered(s, q);
            bool once = HomeExplorationService.StateOf(s).HeritageObtained.Count(x => x == memento.Id) == 1 && HeritageService.OnRelicRecovered(s, relic.Id, relic.X, relic.Y, "x", m.LogicId) == null;
            Expect(once, "J1 ③每件遗产在这个世界只出现一次：再入库 / 再触发这处遗迹都不生成第二件");
        }

        // ── E1 四类效果 ──────────────────────────────────────────────────────────

        private static void CheckEffects()
        {
            CampaignState s = NewWorld(8902);
            HomeValleyMachineMarker m = Hauler();
            HeritageService.EnsurePlaced(s);
            var results = new List<string>();
            bool all = true;
            foreach (string cat in HeritageService.Categories)
            {
                Heritage h = HeritageService.All.First(x => x.Category == cat);
                string relic = HeritageService.RelicOf(s, h.Id);
                long tech0 = s.TechData;
                int chips0 = s.PrimitiveChips?.Length ?? 0;
                // 回收完工的同一入口（机器在核心旁：装进货舱后下一次入库检查就入库）。
                Heritage got = HeritageService.OnRelicRecovered(s, relic, Mathf.RoundToInt(m.Position3.x), Mathf.RoundToInt(m.Position3.z), "test", m.LogicId);
                bool done = StepUntil(() => HeritageService.IsObtained(s, h.Id), 10L * Hz);
                bool ok;
                string what;
                switch (cat)
                {
                    case HeritageService.CatBlueprint:
                        ok = s.UnlockedContentIds.Contains(h.UnlockId);
                        what = "蓝图登记（解锁记录 " + h.UnlockId + "）";
                        break;
                    case HeritageService.CatFirmware:
                        PrimitiveChipRecord chip = s.PrimitiveChips.LastOrDefault(p => p.CardDefId == h.UnlockId);
                        ok = s.UnlockedContentIds.Contains(h.UnlockId) && chip != null && chip.Origin == PrimitiveInventory.OriginHeritage && s.PrimitiveChips.Length == chips0 + 1
                             && Campaign.Content.MechanicalContentUnlock.IsUnlocked(s, h.UnlockId) && FirmwareLibrary.IsObtained(s, h.UnlockId)
                             && FirmwareLibrary.OriginText(chip.Origin) == GameText.Get("fwlib.origin.heritage");
                        what = $"固件 {h.UnlockId} 解锁、存放里多一枚来源“人类遗产”的芯片、固件库算已获得";
                        break;
                    case HeritageService.CatArchive:
                        ok = s.TechData - tech0 == h.TechData && CampaignEconomyLedger.Find(s, HeritageService.InstanceIdOf(h.Id)) != null
                             && HomeExplorationService.StateOf(s).HeritageTechData == h.TechData;
                        what = $"技术数据 +{h.TechData}（经济账本事务 {HeritageService.InstanceIdOf(h.Id)}）";
                        break;
                    default:
                        ok = HeritageService.Showcase(s).Contains(h.Id);
                        what = "进展柜";
                        break;
                }
                // 幂等：再入库一次不重复发放。
                RegionQuestItemRecord q = HeritageService.FindItem(s, h.Id);
                long tech1 = s.TechData;
                int chips1 = s.PrimitiveChips.Length;
                HeritageService.OnDelivered(s, q);
                bool idem = s.TechData == tech1 && s.PrimitiveChips.Length == chips1;
                ok &= got == h && done && idem && MechanicCodex.IsUnlocked(HeritageService.CodexIdOf(h.Id));
                all &= ok;
                results.Add($"{HeritageService.CategoryName(cat)}「{GameText.Get(h.NameKey)}」：{what} {ok}");
            }
            Expect(all, "E1 四类遗产带回后按类别生效（重复入库不重复发放、图鉴解锁）：" + string.Join("；", results));
        }

        // ── N1 负向：货舱满 → 留在原地，下次可取 ─────────────────────────────────

        private static void CheckCargoFull()
        {
            CampaignState s = NewWorld(8903);
            CoverHome(s);
            List<WorldFeature> near = ExploredRelics(s, 320);
            HomeValleyMachineMarker m = Hauler();
            if (near.Count < 1 || m == null)
            {
                Fail($"N1 信号塔覆盖内找不到遗迹点（{near.Count}）或搬运机");
                return;
            }
            Heritage h = HeritageService.All.First(x => x.Category == HeritageService.CatArchive);
            WorldFeature relic = near[0];
            Fixture(s, h.Id, relic.Id);
            FillHold(s, m.LogicId);
            MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord mr);
            int free0 = LootPickService.FreeSpace(s, mr);
            _home.ContextRelicForTests(m, RayAt(relic.X, relic.Y));
            bool recovered = StepUntil(() => HomeExplorationService.IsRecovered(s, relic.Id), 900L * Hz);
            RegionQuestItemRecord q = HeritageService.FindItem(s, h.Id);
            bool left = q != null && q.State == RegionQuestItemState.OnGround && Mathf.Abs(q.Position.x - relic.X) < 0.01f && Mathf.Abs(q.Position.y - relic.Y) < 0.01f
                        && HeritageService.StatusOf(s, h.Id) == HeritageStatus.OnGround && HeritageService.LeftAtRelic(s, relic.X, relic.Y) == q;
            string note = LastDetail(HeritageService.NotifyType);
            HomeExplorationService.TryDescribeRelic(s, relic.Id, out _, out string hover);
            var feats = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, relic.X, relic.Y, relic.X, relic.Y, feats);
            string label = HomeExplorationService.MapLabel(s, feats.First(f => f.Id == relic.Id), out _);
            bool listed = HomeExplorationService.RecoverableRelics(s).Contains(q?.SalvageInstanceId);
            bool tech = s.TechData > 0 && !HeritageService.IsObtained(s, h.Id);
            Expect(free0 == 0 && recovered && left && note.Contains("装不下") && note.Contains(GameText.Get(h.NameKey)) && hover.Contains(GameText.Get(h.NameKey))
                   && label.Contains("遗产留在原地") && listed && tech && GameSettings.HasSeenGuidanceHook(GuidanceHooks.HeritageFirstLeftBehind),
                $"N1 货舱满（空 {free0} 格）的机器回收放着遗产的遗迹：遗迹照常回收（技术数据照发），遗产留在遗迹点上 {left}；通知“{note}”；悬停写“{hover.Split('\n').LastOrDefault()}”；" +
                $"地图标签“{label}”；“派机器回收全部”把它列进去 {listed}");

            // 下次可取：同一台（仍然满）右键已回收的遗迹 → 取回单（换成遗产实例）→ 到场仍装不下 → 继续留在原地并说明。
            int notes0 = CountOf(HeritageService.NotifyType);
            bool rc1 = _home.ContextRelicForTests(m, RayAt(relic.X, relic.Y));
            WorkOrderRecord o1 = HomeExplorationService.ActiveOrder(s, q.SalvageInstanceId);
            bool pickOrder = rc1 && o1 != null && o1.AssignedMachineLogicId == m.LogicId && Math.Abs(o1.Duration - HeritageService.PickupSeconds) < 0.01f
                             && HomeValleyAlarms.WorkKindName(o1) == GameText.Get("heritage.work.label");
            bool done1 = StepUntil(() => o1 != null && o1.State == WorkOrderState.Completed, 300L * Hz);
            bool stillLeft = q.State == RegionQuestItemState.OnGround && CountOf(HeritageService.NotifyType) == notes0 + 1 && LastDetail(HeritageService.NotifyType).Contains("仍然装不下");
            // 腾出货舱后再右键：装进货舱。
            EmptyHold(s, m.LogicId);
            bool rc2 = _home.ContextRelicForTests(m, RayAt(relic.X, relic.Y));
            bool loaded = StepUntil(() => q.State == RegionQuestItemState.Carried, 300L * Hz) && q.CarrierLogicId == m.LogicId && LastDetail(HeritageService.NotifyType).Contains("取走了");
            string label2 = HomeExplorationService.MapLabel(s, feats.First(f => f.Id == relic.Id), out _);
            Expect(pickOrder && done1 && stillLeft && rc2 && loaded && !label2.Contains("遗产留在原地"),
                $"N1 下次可取：右键已回收的遗迹 = 派这台机器去取遗产（取回单 {HeritageService.PickupSeconds} 秒，告警里写“{GameText.Get("heritage.work.label")}”）{pickOrder}；货舱仍满时到场继续留在原地并说明 {stillLeft}；" +
                $"腾出货舱后再右键装进货舱 {loaded}，地图标签恢复“{label2}”");
            // 待分配池（战略地图入口）同样可以：先放回地上。
            q.State = RegionQuestItemState.OnGround;
            q.CarrierLogicId = 0;
            q.Position = new Vector2(relic.X, relic.Y);
            HomeValleyWorkOrders.WorkOrderOpResult pool = HomeExplorationService.OrderRecovery(s, relic.Id, 0);
            WorkOrderRecord po = HomeExplorationService.ActiveOrder(s, q.SalvageInstanceId);
            HomeValleyWorkOrders.WorkOrderOpResult dup = HomeExplorationService.OrderRecovery(s, relic.Id, 0);
            bool poolOk = pool.Success && po != null && !dup.Success && HomeValleyWorkOrders.DescribeCommandFailure(dup.FailureReason) == GameText.Get("relic.reason.ordered")
                          && StepUntil(() => q.State == RegionQuestItemState.Carried, 600L * Hz);
            Expect(poolOk, "N1 地图“派空闲机器回收”指着留着遗产的遗迹 = 下一张待分配取回单，空闲搬运机领走并取回；重复下单拒绝并写原因");
        }

        // ── N1 负向：携带者阵亡、迷雾、无效目标 ─────────────────────────────────────

        private static void CheckDeathAndMisc()
        {
            CampaignState s = NewWorld(8904);
            HomeValleyMachineMarker m = Hauler();
            HomeValleyMachineMarker m2 = Hauler(1);
            HeritageService.EnsurePlaced(s);
            Heritage h = HeritageService.All.First(x => x.Category == HeritageService.CatFirmware);
            // 机器先走到入库范围之外的空地；在那里遗产出现（回收完工同一入口，没有机器 → 留在原地），装进它的货舱，再让它阵亡 → 掉在阵亡处。
            GridCell core = HomeGridService.CorePivot(s);
            // 携带者真实走到核心外 60 格（仓库 / 核心的入库范围之外）再阵亡。
            if (MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord mrec) && mrec.IsInFactory)
            {
                HomeValleyFactory.ReleaseFromFactory(m.LogicId);
            }
            // 阵亡点：离核心与所有仓库都超出入库范围、地面可走的一处（按种子地形找，B25）。
            Vector2 spot = new Vector2(core.X + 60, core.Y);
            foreach (Vector2 d in new[] { new Vector2(60, 0), new Vector2(-60, 0), new Vector2(0, 60), new Vector2(0, -60), new Vector2(90, 40), new Vector2(-90, -40), new Vector2(40, -90) })
            {
                Vector2 p = new Vector2(core.X, core.Y) + d;
                if (!LootPickService.IsDepotPosition(s, HomeValleyLayout.RegionId, p) && WorldGenQuery.IsWalkableGenerated(s, Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)))
                {
                    spot = p;
                    break;
                }
            }
            MarkerOf(m.LogicId)?.CommandMoveTo(new Vector3(spot.x, 0f, spot.y));
            bool walked = StepUntil(() => (new Vector2(m.Position3.x, m.Position3.z) - spot).magnitude < 2f, 300L * Hz)
                          && !LootPickService.IsDepotPosition(s, HomeValleyLayout.RegionId, new Vector2(m.Position3.x, m.Position3.z));
            Vector2 deathAt = new Vector2(m.Position3.x, m.Position3.z);
            HeritageService.OnRelicRecovered(s, HeritageService.RelicOf(s, h.Id), Mathf.RoundToInt(deathAt.x), Mathf.RoundToInt(deathAt.y), "test", 0);
            RegionQuestItemRecord q = HeritageService.FindItem(s, h.Id);
            bool groundNoMachine = q != null && q.State == RegionQuestItemState.OnGround && LastDetail(HeritageService.NotifyType).Contains("装不下");
            WorldSimulation.StepMany(2 * Hz);
            groundNoMachine &= q.State == RegionQuestItemState.OnGround; // 入库范围外的地上遗产不会自己入库
            bool loadedM = HeritageService.TryLoad(s, q, m.LogicId, out _);
            MachineRegistry.ApplyDamage(m.LogicId, 1e6f);
            bool dropped = walked && loadedM && q.State == RegionQuestItemState.OnGround && q.CarrierLogicId == 0 && (q.Position - deathAt).magnitude < 3f
                           && LastDetail(HeritageService.NotifyType).Contains("阵亡") && !HeritageService.IsObtained(s, h.Id);
            string ground = HeritageService.GroundAt(s, q.Position);
            bool rc = m2 != null && _home.ContextRelicForTests(m2, RayAt(Mathf.RoundToInt(q.Position.x), Mathf.RoundToInt(q.Position.y)));
            bool picked = rc && StepUntil(() => q.State == RegionQuestItemState.Carried && q.CarrierLogicId == m2.LogicId, 600L * Hz);
            SendHome(s, m2.LogicId);
            bool homeOk = StepUntil(() => HeritageService.IsObtained(s, h.Id), 900L * Hz);
            Expect(groundNoMachine && dropped && ground == q.SalvageInstanceId && picked && homeOk,
                $"N1 没有机器（待分配）完工时遗产留在原地 {groundNoMachine}；携带者阵亡 → 遗产掉在阵亡处、永不消失、通知写位置 {dropped}；另一台右键那里去取 {picked}、带回入库 {homeOk}" +
                $"（走到 {walked}，阵亡处 {deathAt}，遗产 {q.State} @ {q.Position}，地上实例 {ground}，第二台 {(m2 != null ? MachineNaming.Short(m2.LogicId) : "无")}）");

            // 无效 / 迷雾目标：不存在的实例、已入库的遗产不能下取回单；迷雾里的遗迹照旧拒绝。
            HomeValleyWorkOrders.WorkOrderOpResult none = HomeExplorationService.OrderRecovery(s, HeritageService.InstanceIdOf("no_such"), 0);
            HomeValleyWorkOrders.WorkOrderOpResult obtained = HomeExplorationService.OrderRecovery(s, q.SalvageInstanceId, 0);
            string far = HeritageService.RelicOf(s, HeritageService.All.Last().Id);
            HomeValleyWorkOrders.WorkOrderOpResult fog = HomeExplorationService.OrderRecovery(s, far, 0);
            bool rejects = !none.Success && !obtained.Success && !fog.Success
                           && HomeValleyWorkOrders.DescribeCommandFailure(fog.FailureReason) == GameText.Get("relic.reason.fog")
                           && HomeValleyWorkOrders.DescribeCommandFailure(obtained.FailureReason) == GameText.Get("relic.reason.missing");
            Expect(rejects, "N1 负向：不存在的遗产 / 已入库的遗产不能下取回单（写原因）；放着遗产的远处遗迹还在迷雾里时照旧拒绝“" + GameText.Get("relic.reason.fog") + "”");
        }

        // ── X1 远征地点撤离结算里的遗产（修复轮 1，FGT-EXP-008 携带物不丢）─────────────────

        /// <summary>入库范围外、地面可走的一处（按种子地形找，B25）。</summary>
        private static Vector2 AwaySpot(CampaignState s, int salt)
        {
            GridCell core = HomeGridService.CorePivot(s);
            foreach (Vector2 d in new[] { new Vector2(60, 0), new Vector2(-60, 0), new Vector2(0, 60), new Vector2(0, -60), new Vector2(90, 40), new Vector2(-90, -40), new Vector2(40, -90) })
            {
                Vector2 p = new Vector2(core.X, core.Y) + d + new Vector2(salt, 0);
                if (!LootPickService.IsDepotPosition(s, HomeValleyLayout.RegionId, p) && WorldGenQuery.IsWalkableGenerated(s, Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)))
                {
                    return p;
                }
            }
            return new Vector2(core.X + 60 + salt, core.Y);
        }

        /// <summary>遗产出现在 <paramref name="at"/>（回收完工同一入口，没有机器 → 留在原地），再装进这台机器的货舱。</summary>
        private static RegionQuestItemRecord GiveHeritage(CampaignState s, Heritage h, Vector2 at, int machine)
        {
            HeritageService.OnRelicRecovered(s, HeritageService.RelicOf(s, h.Id), Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), "test", 0);
            RegionQuestItemRecord q = HeritageService.FindItem(s, h.Id);
            if (q != null && machine > 0)
            {
                HeritageService.TryLoad(s, q, machine, out _);
            }
            return q;
        }

        private static void CheckExtraction()
        {
            CampaignState s = NewWorld(8906);
            HomeValleyMachineMarker m = Hauler();
            HomeValleyMachineMarker m2 = Hauler(1);
            if (m == null || m2 == null)
            {
                Fail("X1 找不到两台搬运机");
                return;
            }
            HeritageService.EnsurePlaced(s);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            MachineRecord r1 = MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord a) ? a : null;
            MachineRecord r2 = MachineRegistry.TryGetRecord(m2.LogicId, out MachineRecord b) ? b : null;
            string home = r1.RegionId;
            Heritage archive = HeritageService.All.First(x => x.Category == HeritageService.CatArchive);
            Heritage firmware = HeritageService.All.First(x => x.Category == HeritageService.CatFirmware);
            Heritage memento = HeritageService.All.First(x => x.Category == HeritageService.CatMemento);
            Heritage blueprint = HeritageService.All.First(x => x.Category == HeritageService.CatBlueprint);
            Heritage memento2 = HeritageService.All.Where(x => x.Category == HeritageService.CatMemento).Skip(1).First();
            bool notLostCity(RegionQuestItemRecord x) => !(FracturedCityRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(x.SalvageInstanceId);

            // ① 队员（在家园星球捡到遗产，测试捷径：直接记在破碎都市）撤离成功 → 入库生效；② 家园上另一台带着遗产、没参加远征 → 原样不动。
            RegionQuestItemRecord qa = GiveHeritage(s, archive, AwaySpot(s, 0), m.LogicId);
            RegionQuestItemRecord qm = GiveHeritage(s, memento, AwaySpot(s, 3), m2.LogicId);
            long tech0 = s.TechData;
            int lostCount = BlackBoxService.CarriedNonBoxItems(s);
            r1.RegionId = FracturedCityLayout.RegionId;
            FracturedCityRegion.ResolveExtraction(s, new[] { m.LogicId });
            bool delivered = qa.State == RegionQuestItemState.Recovered && HeritageService.IsObtained(s, archive.Id) && HeritageService.StatusOf(s, archive.Id) == HeritageStatus.Obtained
                             && s.TechData - tech0 == archive.TechData && MechanicCodex.IsUnlocked(HeritageService.CodexIdOf(archive.Id)) && notLostCity(qa);
            bool untouched = qm.State == RegionQuestItemState.Carried && qm.CarrierLogicId == m2.LogicId && !HeritageService.IsObtained(s, memento.Id) && notLostCity(qm);
            Expect(delivered && untouched && lostCount == 0,
                $"X1 破碎都市撤离：队员带着遗产撤离成功 → 入库并按类别生效（技术数据 +{s.TechData - tech0}，图鉴解锁）{delivered}；家园上另一台带着遗产、没参加这次远征 → 原样留在它的货舱（{qm.State} / 携带者 {qm.CarrierLogicId}）{untouched}；" +
                $"全灭字幕的“遗失件数”不算遗产（{lostCount}）");

            // ③ 队员在破碎都市没能撤离（活着、不在幸存名单）→ 遗产掉在它的位置、留在那片区域、通知写位置，不 Lost。
            RegionQuestItemRecord qb = GiveHeritage(s, blueprint, AwaySpot(s, 6), m.LogicId);
            int n0 = CountOf(HeritageService.NotifyType);
            FracturedCityRegion.ResolveExtraction(s, Array.Empty<int>());
            bool dropped = qb.State == RegionQuestItemState.OnGround && qb.CarrierLogicId == 0 && qb.RegionId == FracturedCityLayout.RegionId && notLostCity(qb)
                           && !HeritageService.IsObtained(s, blueprint.Id) && CountOf(HeritageService.NotifyType) == n0 + 1
                           && LastDetail(HeritageService.NotifyType).Contains(GameText.Get(blueprint.NameKey)) && qm.State == RegionQuestItemState.Carried;
            Expect(dropped, $"X1 负向：携带者在破碎都市没能撤离 → 遗产掉在它最后的位置（{qb.RegionId} @ {qb.Position}）、不标遗失、通知“{LastDetail(HeritageService.NotifyType)}”；家园上那台仍带着 {qm.State}");

            // ④ 铸造前哨：遗产的 RegionId 是家园（捡起的地方），携带者在铸造前哨撤离成功 → 同样入库（原先按物品区域筛选会漏掉它，留在货舱等回家）。
            r1.RegionId = FoundryOutpostLayout.RegionId;
            RegionQuestItemRecord qf = GiveHeritage(s, firmware, AwaySpot(s, 9), m.LogicId);
            int chips0 = s.PrimitiveChips?.Length ?? 0;
            FoundryOutpostRegion.ResolveExtraction(s, new[] { m.LogicId });
            bool foundry = qf.State == RegionQuestItemState.Recovered && HeritageService.IsObtained(s, firmware.Id) && (s.PrimitiveChips?.Length ?? 0) == chips0 + 1
                           && qm.State == RegionQuestItemState.Carried;
            r1.RegionId = home;

            // ⑤ 旧版撤离结算留下的坏数据：实例已是 Recovered、保管库里没有 → 进入战役时补一次入库。
            RegionQuestItemRecord qr = GiveHeritage(s, memento2, AwaySpot(s, 12), 0);
            qr.State = RegionQuestItemState.Recovered;
            bool before = !HeritageService.IsObtained(s, memento2.Id);
            HeritageService.OnCampaignEntered(s);
            bool reconciled = before && HeritageService.IsObtained(s, memento2.Id) && HeritageService.Showcase(s).Contains(memento2.Id);
            Expect(foundry && reconciled,
                $"X1 铸造前哨撤离：携带者撤离成功，遗产（区域记的是捡起它的家园）同样入库、固件芯片 +1 {foundry}；旧档里“已带回却没入库”的遗产，进入战役时补入库 {reconciled}");

            // ⑥ 审查 P2：遗产离开地面后，指着它的取回单作废（不白跑到核心）。
            RegionQuestItemRecord qp = HeritageService.FindItem(s, blueprint.Id);
            qp.RegionId = HomeValleyLayout.RegionId;
            qp.Position = AwaySpot(s, 15);
            HomeValleyWorkOrders.WorkOrderOpResult order = HomeExplorationService.OrderRecovery(s, qp.SalvageInstanceId, 0);
            WorkOrderRecord po = HomeExplorationService.ActiveOrder(s, qp.SalvageInstanceId);
            bool loaded = HeritageService.TryLoad(s, qp, m.LogicId, out _);
            bool cancelled = order.Success && po != null && loaded && po.State == WorkOrderState.Cancelled && HomeExplorationService.ActiveOrder(s, qp.SalvageInstanceId) == null;
            Expect(cancelled, $"X1 遗产被别的机器装走后，待分配的取回单作废（{po?.State}），不会有机器白跑到核心");
        }

        // ── P2 剧情遗产可达（FGR-GEN-033 保证可以到达，层级寻路校验）──────────────────────

        private static void CheckReach()
        {
            CampaignState s = NewWorld(8907);
            HeritageService.EnsurePlaced(s);
            HomeExplorationState st = HomeExplorationService.StateOf(s);
            List<Heritage> story = HeritageService.All.Where(h => h.Story == 1).ToList();
            int req0 = HeritageService.ReachRequests;
            bool resolved = StepUntil(() => HeritageService.ReachResolved(s), 300L * Hz);
            var lines = new List<string>();
            bool allOk = story.Count > 0;
            GridCell core = HomeGridService.CorePivot(s);
            var pts = new List<Unity.Mathematics.int2>();
            foreach (Heritage h in story)
            {
                string relic = HeritageService.RelicOf(s, h.Id);
                HomeExplorationService.TryGetRelic(s, relic, out HomeExplorationService.RelicInfo info);
                // 独立复核：自己从核心到遗迹点再发一条玩家通行类别的寻路（不经遗产服务），确认真的有路。
                bool path = false;
                if (GameLogic.Campaign.Nav.NavService.BackgroundHeld)
                {
                    GameLogic.Campaign.Nav.NavService.CancelBackground();
                }
                if (GameLogic.Campaign.Nav.NavService.StartBackground(99, 1, 1, BinGames.Sim.Nav.NavConst.ClassPlayer, core, new GridCell(info.X, info.Y), allowPartial: true)
                    && GameLogic.Campaign.Nav.NavService.CollectBackground(pts, out BinGames.Sim.Nav.NavResult res))
                {
                    long dx = res.End.x - info.X, dy = res.End.y - info.Y;
                    path = pts.Count > 0 && (res.Status == BinGames.Sim.Nav.NavStatus.Ok || (res.Status == BinGames.Sim.Nav.NavStatus.Partial && dx * dx + dy * dy <= 4));
                }
                bool ok = HeritageService.ReachOf(s, h.Id) == HeritageService.ReachOk && path;
                allOk &= ok;
                lines.Add($"{h.Id}@{relic}（{info.Distance} 格）：结论 {HeritageService.ReachOf(s, h.Id)}，独立寻路 {path}");
            }
            Expect(resolved && allOk && HeritageService.ReachRequests - req0 >= story.Count,
                $"P2 剧情遗产 {story.Count} 件：世界步里逐件做层级寻路可达校验（发出 {HeritageService.ReachRequests - req0} 条），结论都“可达”，独立复核从核心到遗迹点都有路：" + string.Join("；", lines));

            // 负向夹具：把第一件剧情遗产所在的遗迹点当作被封闭（寻路结果判“走不到”）→ 改挑下一处并再校验通过，换掉的点记进存档；读档后仍在。
            Heritage first = story[0];
            int i = Array.IndexOf(st.HeritageIds, first.Id);
            string orig = st.HeritageRelics[i];
            st.HeritageReach[i] = HeritageService.ReachPending;
            HeritageService.ForceUnreachableForTests = r => r == orig;
            // 途中存档：在算的那一条先取出结果记进存档（采纳步不变），之后照常在采纳步交到。
            bool inFlight = StepUntil(() => st.HeritageReachRelic == orig, 30L * Hz);
            long adoptAt = st.HeritageReachTick;
            WorldSimulation.SyncAllForSave();
            bool stashed = inFlight && st.HeritageReachResult != HeritageService.ReachResultOnLane && st.HeritageReachTick == adoptAt && st.HeritageReachRelic == orig
                           && !GameLogic.Campaign.Nav.NavService.BackgroundHeld;
            Expect(stashed, $"P2 途中存档：在算的可达校验（{st.HeritageReachRelic}，采纳步 {adoptAt}）先取出结果 {st.HeritageReachResult} 记进存档、通道空出来，采纳步不变");
            bool moved =StepUntil(() => HeritageService.ReachOf(s, first.Id) == HeritageService.ReachOk && HeritageService.RelicOf(s, first.Id) != orig, 120L * Hz);
            string now = HeritageService.RelicOf(s, first.Id);
            HomeExplorationService.TryGetRelic(s, now, out HomeExplorationService.RelicInfo ni);
            var feats = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, ni.X, ni.Y, ni.X, ni.Y, feats);
            WorldFeature nf = feats.FirstOrDefault(f => f.Id == now);
            bool fine = moved && nf != null && ni.Distance >= first.MinDistance && ni.Distance <= first.MaxDistance && HeritageService.Eligible(s, nf, true)
                        && st.HeritageReachRejected.Contains(first.Id + "@" + orig) && HeritageService.AtRelic(s, orig) == null;
            var copy = JsonUtility.FromJson<HomeExplorationState>(JsonUtility.ToJson(st));
            bool saved = copy.HeritageReachRejected.SequenceEqual(st.HeritageReachRejected) && copy.HeritageReach.SequenceEqual(st.HeritageReach);
            Expect(fine && saved, $"P2 负向：剧情遗产 {first.Id} 所在的 {orig} 走不到 → 改放到 {now}（{ni.Distance} 格，仍在 {first.MinDistance}～{first.MaxDistance}、合格），再校验可达 {moved}；换掉的点进存档、读回一致 {saved}");

            // 负向：怎么换都走不到 → 换满 heritage.reach_max_repicks 次后结论“走不到”、写错误日志，不再无限重试。
            Heritage second = story.Count > 1 ? story[1] : story[0];
            int j = Array.IndexOf(st.HeritageIds, second.Id);
            st.HeritageReach[j] = HeritageService.ReachPending;
            HeritageService.ForceUnreachableForTests = _ => true;
            int reqBefore = HeritageService.ReachRequests;
            bool failed = StepUntil(() => HeritageService.ReachOf(s, second.Id) == HeritageService.ReachFailed, 300L * Hz);
            int rejected = st.HeritageReachRejected.Count(e => e.StartsWith(second.Id + "@", StringComparison.Ordinal));
            int maxRepicks = GridContent.TryGetTuning("heritage.reach_max_repicks", out float mr) ? Mathf.RoundToInt(mr) : 4;
            int reqAfter = HeritageService.ReachRequests;
            WorldSimulation.StepMany(10 * Hz);
            bool stopped = HeritageService.ReachRequests == reqAfter;
            HeritageService.ForceUnreachableForTests = null;
            Expect(failed && rejected == maxRepicks + 1 && stopped && reqAfter - reqBefore >= maxRepicks + 1,
                $"P2 负向：{second.Id} 怎么换都走不到 → 换 {rejected - 1} 次（上限 {maxRepicks}）后结论“走不到”{failed}、之后不再发请求 {stopped}");
        }

        // ── N1 负向：建造压遗迹点（FG-GAP-127）───────────────────────────────────────

        private static bool RelicBlocked(CampaignState s, int x, int y)
        {
            GridPlacementResult r = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalTower, new GridCell(x, y), 0);
            return r.Reasons.Any(z => z.Code == GridBlockReason.RelicSite);
        }

        private static void CheckBuildBlock()
        {
            CampaignState s = NewWorld(8905);
            CoverHome(s);
            List<WorldFeature> near = ExploredRelics(s, 320);
            if (near.Count < 2)
            {
                Fail($"N1 建造：信号塔覆盖内找不到两处遗迹点（{near.Count}）");
                return;
            }
            WorldFeature a = near[0];
            WorldFeature b = near[1];
            bool blockA = RelicBlocked(s, a.X, a.Y);
            GridPlacementResult ra = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalTower, new GridCell(a.X, a.Y), 0);
            string reason = blockA ? ra.Reasons.First(z => z.Code == GridBlockReason.RelicSite).Describe() : string.Empty;
            // 审查 P2：还没发现的遗迹点照样挡建造，但原因不写坐标（不泄露迷雾里的点位）。
            HomeExplorationState est = HomeExplorationService.StateOf(s);
            string[] found0 = est.RelicsFound;
            est.RelicsFound = found0.Where(x => x != a.Id).ToArray();
            GridPlacementResult rh = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalTower, new GridCell(a.X, a.Y), 0);
            GridReason hidden = rh.Reasons.FirstOrDefault(z => z.Code == GridBlockReason.RelicSite);
            string hiddenText = hidden.Code == GridBlockReason.RelicSite ? hidden.Describe() : string.Empty;
            est.RelicsFound = found0;
            Expect(hiddenText.Length > 0 && !hiddenText.Contains(a.X.ToString(CultureInfo.InvariantCulture)) && hiddenText == GameText.Get("grid.reason.relic_site_plain"),
                $"N1 负向：还没发现的遗迹点照样挡建造，原因“{hiddenText}”不带坐标");
            // 回收后（没有遗产）可以建；遗产留在原地时仍然挡着。
            Heritage h = HeritageService.All.First(x => x.Category == HeritageService.CatMemento);
            Fixture(s, h.Id, b.Id);
            HomeExplorationService.OrderRecovery(s, a.Id, 0);
            StepUntil(() => HomeExplorationService.IsRecovered(s, a.Id), 900L * Hz);
            bool freeA = !RelicBlocked(s, a.X, a.Y);
            HeritageService.EnsurePlaced(s);
            HomeExplorationService.StateOf(s).RelicsRecovered = HomeExplorationService.StateOf(s).RelicsRecovered.Append(b.Id).ToArray();
            HomeExplorationService.StateOf(s).RelicsRecoveredTicks = HomeExplorationService.StateOf(s).RelicsRecoveredTicks.Append(GameClock.Ticks).ToArray();
            HeritageService.OnRelicRecovered(s, b.Id, b.X, b.Y, "test", 0);
            bool blockB = RelicBlocked(s, b.X, b.Y);
            Expect(blockA && reason.Contains(a.X.ToString(CultureInfo.InvariantCulture)) && freeA && blockB,
                $"N1 FG-GAP-127 建造压遗迹点：没回收的遗迹点上不能放建筑，原因“{reason}”；回收完（没有遗产）后可以建 {freeA}；遗产留在原地的地方仍然挡着 {blockB}");
        }

        // ── T1 暂停与倍速 / T2 观察一致 ─────────────────────────────────────────────

        private static string Flow(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames, out bool carried)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            frames = 0;
            carried = false;
            CoverHome(s);
            List<WorldFeature> near = ExploredRelics(s, 320);
            if (near.Count == 0)
            {
                return "no-relic";
            }
            Heritage h = HeritageService.All.First(x => x.Category == HeritageService.CatArchive);
            Fixture(s, h.Id, near[0].Id);
            if (!HomeExplorationService.OrderRecovery(s, near[0].Id, 0).Success)
            {
                return "no-order";
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
            long target = GameClock.Ticks + 240L * Hz;
            while (GameClock.Ticks < target && frames < 60 * 1500)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            carried = HeritageService.StatusOf(s, h.Id) == HeritageStatus.Carried;
            return Masked(JsonUtility.ToJson(s)) + "|heritage=" + HeritageService.Snapshot(s) + $"|tech={s.TechData}";
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true, carriedAll = true;
            string diff = string.Empty;
            var shown = new List<string>();
            long steps = 240L * Hz;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = Flow(8911, true, speed, true, out bool held, out int frames, out bool car);
                paused &= held;
                carriedAll &= car;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed * GameClock.TicksFor(1f / 60f) - steps) <= 6f * Math.Max(1, GameClock.TicksFor(1f / 60f));
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && carriedAll && reference != "no-relic" && reference != "no-order",
                $"T1 暂停与 0.5x～3x（暂停 {paused}、逐字段一致 {same}、帧数成反比 {proportional}、期间找到遗产并装进货舱 {carriedAll}）：{string.Join(" / ", shown)} 跑同样的 240 游戏秒" +
                "（机器赶路、回收工期、遗产装货舱、技术数据入账），整份战役状态 JSON 逐字段一致" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObserved()
        {
            string seen = Flow(8912, true, 1f, false, out _, out _, out bool c1);
            string unseen = Flow(8912, false, 1f, false, out _, out _, out bool c2);
            Expect(seen == unseen && c1 && c2 && seen != "no-relic",
                "T2 家园在观察与不观察时（机器回收遗迹、找到遗产装进货舱）整份战役状态逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S1 真文件存读档 ──────────────────────────────────────────────────────

        private static string Snap(CampaignState s) =>
            $"t={GameClock.Ticks}|{HeritageService.Snapshot(s)}|unlock={string.Join(",", (s.UnlockedContentIds ?? Array.Empty<string>()).Where(x => x.StartsWith("heritage_") || x.StartsWith("fw_")).OrderBy(x => x, StringComparer.Ordinal))}" +
            $"|chips={(s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(p => p.Origin == PrimitiveInventory.OriginHeritage)}|tech={s.TechData}|world={HomeExplorationService.StateOf(s).HeritageWorld}";

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8921);
            CoverHome(s);
            List<WorldFeature> near = ExploredRelics(s, 320);
            HomeValleyMachineMarker m = Hauler();
            if (near.Count < 1 || m == null)
            {
                Fail("S1 信号塔覆盖内找不到遗迹点或搬运机");
                return;
            }
            // 一件已入库（固件）、一件留在遗迹里（货舱满）、其余还藏着。
            Heritage fw = HeritageService.All.First(x => x.Category == HeritageService.CatFirmware);
            HeritageService.OnRelicRecovered(s, HeritageService.RelicOf(s, fw.Id), Mathf.RoundToInt(m.Position3.x), Mathf.RoundToInt(m.Position3.z), "test", m.LogicId);
            StepUntil(() => HeritageService.IsObtained(s, fw.Id), 10L * Hz);
            Heritage arc = HeritageService.All.First(x => x.Category == HeritageService.CatArchive);
            Fixture(s, arc.Id, near[0].Id);
            FillHold(s, m.LogicId);
            HomeExplorationService.OrderRecovery(s, near[0].Id, m.LogicId);
            StepUntil(() => HomeExplorationService.IsRecovered(s, near[0].Id), 900L * Hz);
            EmptyHold(s, m.LogicId);
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            // 存档后接着跑：派机器去取留下的遗产。
            HomeExplorationService.OrderRecovery(s, near[0].Id, 0);
            long until = GameClock.Ticks + 300L * Hz;
            void Continue() => WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            Continue();
            string continuous = Snap(s);
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
            HeritageService.OnCampaignEntered(l);
            string loaded = Snap(l);
            HomeExplorationService.OrderRecovery(l, near[0].Id, 0);
            Continue();
            string after = Snap(l);
            bool content = before.Contains(fw.Id) && before.Contains("|obt=" + fw.Id) && before.Contains(arc.Id + "@" + near[0].Id + ":2");
            bool picked = continuous.Contains(arc.Id + "@" + near[0].Id + ":3");
            Expect(save.Success && loaded == before && after == continuous && content && picked,
                $"S1 真文件存读档（遗产放在哪、已入库的、留在遗迹里的位置、解锁记录、固件芯片、技术数据）：读回与存前逐项一致 {loaded == before}；读档后派机器去取与一直跑一致 {after == continuous}" +
                $"\n    存前 {before}" + (loaded == before ? string.Empty : "\n    读回 " + loaded) + (after == continuous ? string.Empty : "\n    一直跑 " + continuous + "\n    读档后 " + after));
        }

        // ── U1 界面 ───────────────────────────────────────────────────────────────

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

        private static void CheckUi()
        {
            CampaignState s = NewWorld(8931);
            HeritageService.EnsurePlaced(s);
            VisualElement root = F.MountUxml(UiKitFolder + "HeritageVaultPanel.uxml", out GameObject go);
            VisualElement proot = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
            HeritageVaultPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<HeritageVaultPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "BlackBoxRow.uxml"));
                var prod = pgo.AddComponent<ProductionPanelUIToolkit>();
                prod.BindView(proot);
                InputRouter.Reset();
                int n = HeritageService.All.Count;

                // 入口：归还核心的建筑面板“保管库…”；别的建筑没有。
                string coreId = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore).BuildingId;
                ProductionPanelUIToolkit.Open(coreId);
                bool shown = prod.VaultButton != null && !prod.VaultButton.ClassListContains("bn-hidden") && prod.VaultButton.text == GameText.Get("heritage.panel.open");
                Click(prod.VaultButton);
                bool opened = HeritageVaultPanelUIToolkit.IsOpen && panel.PanelVisible && !ProductionPanelUIToolkit.IsOpen && panel.CurrentTab == HeritageVaultPanelUIToolkit.TabCollection
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.HeritageVaultFirstOpen);
                panel.Refresh(force: true);
                bool emptyCollection = panel.RowCount == n && panel.RowText(0).StartsWith("？？？") && panel.StatusText.Contains($"0 / {n}") && panel.DetailText == GameText.Get("heritage.vault.detail_hint");
                panel.SelectRow(0);
                bool lockedDetail = panel.Selected == HeritageService.Collection()[0] && panel.DetailText.Contains("还没找到") && !panel.DetailText.Contains(GameText.Get(HeritageService.All[0].NameKey));
                Click(panel.TabShowcaseButton);
                bool emptyShowcase = panel.CurrentTab == HeritageVaultPanelUIToolkit.TabShowcase && panel.RowCount == 0 && panel.EmptyText == GameText.Get("heritage.vault.empty_showcase");
                HeritageVaultPanelUIToolkit.Close();
                string other = s.BuildingRecords.First(x => x != null && BuildingOps.IsWarehouse(x)).BuildingId;
                ProductionPanelUIToolkit.Open(other);
                bool hiddenElse = prod.VaultButton.ClassListContains("bn-hidden");
                ProductionPanelUIToolkit.Close();
                // 正式入口二：左键归还核心 = 归还核心面板（FG1-SIG-01），页眉“保管库…”。
                VisualElement croot = F.MountUxml(UiKitFolder + "SignalCorePanel.uxml", out GameObject cgo);
                bool coreEntry;
                try
                {
                    var hud = cgo.AddComponent<UI.SignalCore.SignalCoreHudUIToolkit>();
                    hud.BindView(croot);
                    hud.SetOpen(true);
                    hud.Refresh();
                    bool btn = hud.VaultButton != null && hud.VaultButton.text == GameText.Get("heritage.panel.open");
                    Click(hud.VaultButton);
                    coreEntry = btn && HeritageVaultPanelUIToolkit.IsOpen && !UI.SignalCore.SignalCoreHudUIToolkit.IsOpen;
                    HeritageVaultPanelUIToolkit.Close();
                }
                finally
                {
                    Object.DestroyImmediate(cgo);
                }
                Expect(coreEntry, "U1 左键归还核心打开的归还核心面板页眉有“保管库…”，点它关掉核心面板、打开核心保管库");
                Expect(shown && opened && emptyCollection && lockedDetail && emptyShowcase && hiddenElse,
                    $"U1 归还核心的建筑面板有“{prod.VaultButton?.text}”入口、点它打开核心保管库（收集页）{opened}；收集页 {panel.RowCount}/{n} 行，没找到的写“？？？ · 类别 · 第几幕范围”不剧透、点开写去哪找 {lockedDetail}；" +
                    $"展柜空状态“{panel.EmptyText}”；别的建筑不显示入口 {hiddenElse}");

                // 有数据：一件纪念物入库、一件留在原地、一件装在货舱。
                HomeValleyMachineMarker m = Hauler();
                Heritage mem = HeritageService.All.First(x => x.Category == HeritageService.CatMemento);
                HeritageService.OnRelicRecovered(s, HeritageService.RelicOf(s, mem.Id), Mathf.RoundToInt(m.Position3.x), Mathf.RoundToInt(m.Position3.z), "test", m.LogicId);
                StepUntil(() => HeritageService.IsObtained(s, mem.Id), 10L * Hz);
                Heritage arc = HeritageService.All.First(x => x.Category == HeritageService.CatArchive);
                GridCell core = HomeGridService.CorePivot(s);
                HeritageService.OnRelicRecovered(s, HeritageService.RelicOf(s, arc.Id), core.X + 80, core.Y, "test", 0);
                HeritageVaultPanelUIToolkit.Open(HeritageVaultPanelUIToolkit.TabShowcase);
                panel.Refresh(force: true);
                string showcaseText = panel.RowText(0);
                bool showcaseRow = panel.RowCount == 1 && showcaseText.Contains(GameText.Get(mem.NameKey)) && showcaseText.Contains("已入库");
                panel.SelectRow(0);
                bool memDetail = panel.DetailText.Contains(GameText.Get(mem.DescKey)) && panel.DetailText.Contains(GameText.Get("heritage.cat.memento.effect"));
                Click(panel.TabCollectionButton);
                int arcRow = HeritageService.Collection().IndexOf(arc.Id);
                bool groundRow = panel.RowText(arcRow).Contains(GameText.Get(arc.NameKey)) && panel.RowText(arcRow).Contains("等机器来取");
                bool status = panel.StatusText.Contains($"1 / {n}") && panel.StatusText.Contains("另有 1 件") && panel.TabCollectionButton.text.Contains($"1/{n}")
                              && panel.TabShowcaseButton.text.Contains("1");
                // 英文。
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh(force: true);
                bool en = panel.TitleText == "Core vault" && panel.StatusText.StartsWith("Collected") && panel.RowText(arcRow).Contains("waiting for a machine");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.Refresh(force: true);
                Expect(showcaseRow && memDetail && groundRow && status && en,
                    $"U1 有数据：展柜列出入库的纪念物“{showcaseText}”，点开看描述（类别作用 + 背景故事）{memDetail}；收集页写留在原地的“{panel.RowText(arcRow)}”；" +
                    $"收集进度“{panel.StatusText.Replace('\n', ' ')}”；英文 {en}");
                HeritageVaultPanelUIToolkit.Close();
                bool esc = !HeritageVaultPanelUIToolkit.IsOpen;

                // 图鉴收集页：“人类遗产”页签 = 全部遗产，入库的解锁、没入库的是剪影 + 类别与第几幕。
                // 图鉴跨存档：先清掉前面几段解锁过的（临时图鉴文件），再按本存档补解锁。
                try
                {
                    File.Delete(MechanicCodex.FilePathOverrideForTests);
                }
                catch
                {
                    // 没有文件也可以。
                }
                MechanicCodex.ResetForTests();
                HeritageService.OnCampaignEntered(s);
                var entries = new List<MechanicCodexEntry>();
                MechanicCodex.EntriesIn(MechanicCodex.TabHeritage, null, entries, s);
                MechanicCodexEntry locked = entries.First(e => e.ContentId == arc.Id);
                string hint = MechanicCodex.Hint(locked, s);
                bool codexTab = MechanicCodex.Tabs.Contains(MechanicCodex.TabHeritage) && entries.Count == n && MechanicCodex.CountIn(MechanicCodex.TabHeritage) == n
                                && MechanicCodex.UnlockedCountIn(MechanicCodex.TabHeritage) == 1 && MechanicCodex.IsUnlocked(HeritageService.CodexIdOf(mem.Id))
                                && !MechanicCodex.IsUnlocked(HeritageService.CodexIdOf(arc.Id)) && hint.Contains(GameText.Get("heritage.cat.archive")) && !hint.Contains(GameText.Get(arc.NameKey))
                                && MechanicCodex.Body(entries.First(e => e.ContentId == mem.Id), s).Contains(GameText.Get(mem.DescKey));
                VisualTreeAsset codexUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "MechanicCodexPanel.uxml");
                bool tabButton = codexUxml != null && codexUxml.CloneTree().Q<Button>("CodexTabHeritage") != null;
                Expect(codexTab && tabButton && esc,
                    $"U1 图鉴收集页：“{GameText.Get("codex.tab.heritage")}”页签 {entries.Count} 条（已解锁 {MechanicCodex.UnlockedCountIn(MechanicCodex.TabHeritage)}），没收集的只写“{hint}”不剧透；页签按钮在 UXML 里 {tabButton}");

                // 布局探针（四种分辨率 + 超长文字 + USS 体检）。
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "HeritageVaultPanel.uxml", "HeritageWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("HeritageRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "U1 核心保管库布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                HeritageVaultPanelUIToolkit.Close();
                ProductionPanelUIToolkit.Close();
                HeritageVaultPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(pgo);
            }
        }

        // ── P2 性能 ──────────────────────────────────────────────────────────────

        private static void Perf(string name, double ms, double budget, string note)
        {
            string cpu = $"测量环境 Editor batchmode / Mono JIT，{SystemInfo.processorCount} 线程";
            if (ms <= budget)
            {
                Expect(true, $"P2 {name} {ms:0.000} ms ≤ {budget} ms（{note}；{cpu}）");
            }
            else if (ms <= budget * 2)
            {
                Expect(true, $"P2 {name} {ms:0.000} ms（性能警告：超过 {budget} ms 但不到 2 倍；{note}；{cpu}）");
                PerfLines.Add($"警告 {name} {ms:0.000} ms > {budget} ms");
            }
            else
            {
                Fail($"P2 {name} {ms:0.000} ms 超过 {budget} ms 的 2 倍（{note}；{cpu}）");
            }
            PerfLines.Add($"{name} {ms:0.000} ms（预算 {budget} ms）");
        }

        private static void CheckPerformance()
        {
            CampaignState s = CampaignState.CreateNew("fgheritage-perf", "Standard", 8941);
            CampaignSession.Set(Slot, s);
            HeritageService.EnsurePlaced(s);
            Perf($"每个世界挑一次遗迹点（{HeritageService.All.Count} 件，{HeritageService.LastPlaceQueries} 个方框查询）", HeritageService.LastPlaceMs, 400.0, "一次性；之后读存档");
            GridCell core = HomeGridService.CorePivot(s);
            var rnd = new System.Random(3);
            var cells = new List<GridCell>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 2000;
            for (int i = 0; i < n; i++)
            {
                int x = core.X + rnd.Next(-300, 300);
                int y = core.Y + rnd.Next(-300, 300);
                HeritageService.BlockingPointsIn(s, x - 2, y - 2, x + 2, y + 2, cells);
            }
            sw.Stop();
            Perf("建造校验查挡建造的遗迹点每次（3×3 + 半径 1）", sw.Elapsed.TotalMilliseconds / n, 0.05, "放置预览每帧一次；点位按分布格缓存");
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                HeritageService.LeftAtRelic(s, core.X + i % 50, core.Y);
                HeritageService.StatusOf(s, HeritageService.All[i % HeritageService.All.Count].Id);
            }
            sw.Stop();
            Perf("地图标签 / 悬停查遗产状态每次", sw.Elapsed.TotalMilliseconds / n, 0.05, "O(遗产数 + 关键物数)，只在地图刷新 / 悬停时");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgHeritageSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
