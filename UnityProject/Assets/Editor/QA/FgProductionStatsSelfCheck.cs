using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using BinGames.Sim.Logistics;
using Unity.Mathematics;
using GameLogic.Campaign.Regions;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-08 统计、顶栏与产线规划助手（FG04 FGR-ECO-050 / 051 / 052；FG13 FGU-06 / 13 / 14；FGT-ECO-008）自检。全部起真实世界（家园、电网、生产服务、世界步）跑：
    /// A 数据（调参四个窗口 = 1 分钟 / 10 分钟 / 1 小时 / 10 小时、文本中英、图鉴条目与钩子、快捷键 Alt+T、通知类型）；
    /// B 记账与窗口（产量 / 消耗速率、与建筑自己的完成数守恒、窗口滑出、未满窗口按已统计时长）；C 速率曲线；
    /// D 持续赤字（不到 10 分钟不警告、超过发一次警告与钩子、恢复后清除；短暂赤字不警告；只产不耗的物品不算）；
    /// E 瓶颈与建筑效率（缺料的物品与建筑、效率排序、查询才扫描建筑）；F 固定到顶栏（默认废料、重复、上限、取消、目标）；
    /// G 规划助手（按表算建筑 / 原料 / 副产品 / 电力、换配方、负向、只计算不改世界）；H FGT-ECO-008（按规划搭建产线、只按规划量供原料，实测产量与目标误差 ≤ 5%；产能与电力）；
    /// I 统计面板（真 UXML：Alt+T、五个页签、固定、曲线、打开建筑面板、规划与固定为目标、空状态、布局探针）；J 资源顶栏（真 UXML：格子、达标、右键取消固定、节流、布局探针）；
    /// K 真文件存读档；L 暂停与 0.5x～3x；M 观察一致（FGR-BASE-021）；N 性能与“不每帧遍历”。
    /// </summary>
    public static class FgProductionStatsSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 6000;
        private static double _fakeNow = 1000;

        [MenuItem("BinGames/QA/自检/FG 统计、顶栏与产线规划助手")]
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
            Line("\n[统计、顶栏与产线规划助手] 生产统计四窗口、持续赤字、瓶颈与效率、资源顶栏、规划助手、FGT-ECO-008、面板、存读档、倍速、观察一致、性能（FG4-ECO-08）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgstats-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "统计记账 / 查询 / 规划 / 顶栏在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网 / 传送带 / 管线内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckLedgerAndWindows);
                Step(CheckLedgerSources);
                Step(CheckConstructionStats);
                Step(CheckDeficit);
                Step(CheckBottlenecksAndEfficiency);
                Step(CheckPins);
                Step(CheckPlannerMath);
                Step(CheckPlannerMatchesBuiltLine);
                Step(CheckFlowEnergy);
                Step(CheckPanel);
                Step(CheckTopBar);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance);
                HomeValleyWorkOrders.ResetSessionState();
                StatsPanelUIToolkit.InWorldOverrideForTests = false;
                StatsPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
            }
            finally
            {
                StatsPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                WorldBarHudUIToolkit.ResourceClock = () => Time.realtimeSinceStartupAsDouble;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
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
                WorldSimulation.UnloadAll();
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
            Line($"  · [统计、顶栏与产线规划助手] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 400)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            _seq = 6000;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            return s;
        }

        /// <summary>测试捷径：登记一座已建成的建筑（真实放置 / 施工由 FG3-LOG-02 覆盖），ID 按正式格式“类型#序号”。</summary>
        private static BuildingRecord Built(CampaignState s, string typeId, GridCell pivot)
        {
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, bg.FootprintW, bg.FootprintH, 0),
                Health = BuildingOps.MaxDurability(typeId),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return r;
        }

        /// <summary>按种子地形在核心附近找一块能放下的空地，新登记一座（B25：不写死坐标）。失败时 null。</summary>
        private static BuildingRecord Place(CampaignState s, string type)
        {
            if (!GridContent.TryGetBuilding(type, out BuildingGrid g))
            {
                return null;
            }
            string terrain = g.RequiredTerrain == "any" ? null : g.RequiredTerrain.Split('|')[0];
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 7; r <= 30; r++)
            {
                for (int a = 0; a < 48; a++)
                {
                    float ang = a * 7.5f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * r), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * r));
                    if (terrain != null)
                    {
                        if (!HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            continue;
                        }
                        FgProductionSelfCheck.SetFootprint(s, type, c, terrain);
                    }
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return Built(s, type, c);
                    }
                }
            }
            return null;
        }

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) => b == null ? null : FgProductionSelfCheck.P(s, b);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static ItemDef Def(string id) => ItemCatalog.Find(id);

        private static bool Powered(CampaignState s, BuildingRecord b) =>
            b != null && (b.PowerState == BuildingPowerState.Powered || !HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId)
                          || HomeValleyLayout.PowerProfile[b.BuildingTypeId].PowerDemand <= 0f);

        /// <summary>推进 <paramref name="sec"/> 游戏秒，每半秒调用一次 <paramref name="each"/>（当作下游一直在取货 / 上游在送料）。</summary>
        private static void Run(float sec, Action each)
        {
            for (float t = 0f; t < sec - 0.001f; t += 0.5f)
            {
                Seconds(0.5f);
                each?.Invoke();
            }
        }

        private static void Drain(CampaignState s, params BuildingRecord[] bs)
        {
            foreach (BuildingRecord b in bs)
            {
                ProductionService.Producer p = P(s, b);
                if (p != null)
                {
                    p.Rec.Out = Array.Empty<ItemStackRecord>();
                }
            }
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static List<NotificationEntry> Notes(string type)
        {
            var list = new List<NotificationEntry>();
            NotificationCenter.Query(null, type, list);
            return list;
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            int[] windows = Enumerable.Range(0, ProductionStats.TierCount).Select(ProductionStats.WindowSeconds).ToArray();
            bool tiers = windows.SequenceEqual(new[] { 60, 600, 3600, 36000 }) && ProductionStats.BucketCount == 30;
            string[] tuning =
            {
                "eco.stats.buckets", "eco.stats.tier0.bucket_seconds", "eco.stats.tier1.bucket_seconds", "eco.stats.tier2.bucket_seconds", "eco.stats.tier3.bucket_seconds",
                "eco.stats.deficit_minutes", "eco.stats.deficit_probe_seconds", "eco.stats.deficit_check_seconds", "eco.stats.panel_refresh_seconds", "eco.stats.bottleneck_top",
                "eco.topbar.max_pins", "eco.topbar.refresh_seconds", "eco.topbar.rate_window", "eco.topbar.max_subnets", "eco.topbar.target_tolerance",
                "eco.planner.max_depth", "eco.planner.default_rate", "eco.planner.max_rate",
            };
            string[] missing = tuning.Where(id => !GridContent.TryGetTuning(id, out _)).ToArray();
            bool deficit10 = Mathf.Approximately(ProductionStats.DeficitMinutes, 10f);
            // 文本：本 Story 的键中英两套都有。
            string[] keys =
            {
                "stats.tab.production", "stats.tab.buildings", "stats.tab.bottleneck", "stats.tab.planner", "stats.tab.combat", "stats.prod.empty", "stats.deficit.line",
                "stats.bn.item", "stats.buildings.row", "planner.row", "planner.no_auto_build", "planner.error.rate", "topbar.pin", "topbar.add", "topbar.power", "topbar.exposure",
                "stats.window.0", "stats.window.3", "codex.economy.stats.title", "codex.economy.stats.body", "notify.type.eco_deficit.single", "input.action.open_stats.name",
            };
            var badText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    if (!GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k)))
                    {
                        badText.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CodexEntry codex = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.economy.stats");
            bool codexOk = codex != null && codex.Hooks.Contains(GuidanceHooks.StatsProductionFirstOpen) && codex.Hooks.Contains(GuidanceHooks.PlannerFirstPlan)
                           && codex.Hooks.Contains(GuidanceHooks.StatsFirstDeficit) && codex.Hooks.Contains(GuidanceHooks.StatsFirstPin);
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.StatsProductionFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.StatsFirstDeficit)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.StatsFirstPin) && GuidanceHooks.Known.Contains(GuidanceHooks.PlannerFirstPlan);
            bool action = InputActionCatalog.TryGet(GameActionId.OpenStats, out InputActionDef act) && act.DefaultChord.Key == KeyCode.T && act.DefaultChord.Mods == InputModifier.Alt
                          && act.Status == InputActionStatus.Wired;
            bool notify = NotificationCatalog.TryGetType("eco_deficit", out NotifyTypeDef nt) && nt.Tier == NotifyLevel.Warning;
            // 源数据（tools/cell_tables/fgdata_stats.py）与运行时表一致：图鉴条目逐字段。
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string dump) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string cx = dump.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("CX\tcodex.economy.stats\t", StringComparison.Ordinal)) ?? string.Empty;
            string runtime = codex == null ? string.Empty : string.Join("\t", "CX", codex.Id, codex.Tab, codex.TitleKey, codex.BodyKey, codex.HintKey, codex.Links, codex.Hooks,
                codex.SortOrder.ToString(CultureInfo.InvariantCulture));
            Expect(tiers && missing.Length == 0 && deficit10 && badText.Count == 0 && codexOk && hooks && action && notify && code == 0 && cx == runtime,
                $"A 数据：四个时间窗口 {string.Join("/", windows)} 秒（各 {ProductionStats.BucketCount} 桶）；赤字阈值 {ProductionStats.DeficitMinutes} 分钟；调参齐全（缺 {string.Join(",", missing)}）；" +
                $"文本中英齐全（缺 {string.Join(",", badText)}）；图鉴“生产统计与产线规划”带四个钩子、源数据与运行时逐字段一致（{cx == runtime}）；快捷键 OpenStats 默认 Alt+T 已接入；通知类型 eco_deficit 为警告级");
        }

        // ── B / C 记账、窗口与曲线 ───────────────────────────────────────────────────────

        private static void CheckLedgerAndWindows()
        {
            CampaignState s = NewWorld(8101, scrap: 100);
            BuildingRecord f = Place(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 4000 } };
            long recBefore = ProductionStats.RecordCount;
            Run(120f, () => Drain(s, f));
            ItemDef alloy = Def("alloy");
            ItemDef scrap = Def(ItemCatalog.ScrapId);
            ProductionStats.ItemRate a1 = ProductionStats.RateOf(s, 0, alloy);
            ProductionStats.ItemRate a10 = ProductionStats.RateOf(s, 1, alloy);
            ProductionStats.ItemRate s10 = ProductionStats.RateOf(s, 1, scrap);
            long completed = P(s, f).Rec.Completed;
            var outs = new List<ItemStackRecord>();
            ProductionService.TryWindowStats(P(s, f), outs, out int done, out _);
            int bucketOut = outs.FirstOrDefault(o => o.ItemId == "alloy")?.Amount ?? 0;
            float eff10 = ProductionStats.EffectiveSeconds(s, 1);
            // 6 秒一份合金、每份 4 废料：满速 10 合金 / 分、40 废料 / 分。
            bool rates = Math.Abs(a10.ProducedPerMinute - 10f) <= 1f && Math.Abs(s10.ConsumedPerMinute - 40f) <= 4f && Math.Abs(a1.ProducedPerMinute - 10f) <= 1.5f;
            bool conserved = a10.Produced == completed && a10.Produced == bucketOut && done == completed && s10.Consumed == (completed + (P(s, f).Rec.Running ? 1 : 0)) * 4
                             && ProductionStats.RecordCount > recBefore;
            bool effective = Math.Abs(eff10 - 120f) < 1f;
            Expect(rates && conserved && effective,
                $"B1 记账（FGR-ECO-050）：精炼炉满料 2 分钟——合金产出 {F(a10.ProducedPerMinute)}/分（1 分钟窗口 {F(a1.ProducedPerMinute)}）、废料消耗 {F(s10.ConsumedPerMinute)}/分；" +
                $"窗口合计 {a10.Produced} = 建筑累计完成 {completed} = 建筑 10 分钟桶产出 {bucketOut}（守恒）；废料消耗 {s10.Consumed} = 开工份数 × 4；窗口未满时按已统计 {F(eff10)} 秒算速率");
            // 窗口滑出：断料后 70 秒，1 分钟窗口清零、10 分钟窗口仍在。
            P(s, f).Rec.In = Array.Empty<ItemStackRecord>();
            Run(70f, () => Drain(s, f));
            ProductionStats.ItemRate a1b = ProductionStats.RateOf(s, 0, alloy);
            ProductionStats.ItemRate a10b = ProductionStats.RateOf(s, 1, alloy);
            Expect(a1b.Produced == 0 && a1b.Consumed == 0 && a10b.Produced >= a10.Produced && a10b.Produced > 0,
                $"B2 窗口滑出：断料 70 秒后 1 分钟窗口产出 {a1b.Produced}（已滑出），10 分钟窗口仍是 {a10b.Produced}");
            // C 速率曲线：每桶之和 = 窗口合计；点数 = 桶数；最近的桶在最后。
            var pArr = new float[64];
            var cArr = new float[64];
            int n1 = ProductionStats.Series(s, 1, alloy, pArr, cArr);
            float sum1 = pArr.Take(n1).Sum();
            int n2 = ProductionStats.Series(s, 1, scrap, pArr, cArr);
            float sumC = cArr.Take(n2).Sum();
            bool tail = pArr.Skip(n2 - 3).Take(3).All(v => v == 0f); // 最后 60 秒断料：最近 3 个 20 秒桶没有产出（废料不产出）
            int nOld = ProductionStats.Series(s, 1, alloy, pArr, cArr);
            bool recentZero = pArr[nOld - 1] == 0f && pArr[nOld - 2] == 0f && pArr.Take(nOld).Max() > 0f;
            Expect(n1 == ProductionStats.BucketCount && Math.Abs(sum1 - a10b.Produced) < 0.01f && Math.Abs(sumC - ProductionStats.RateOf(s, 1, scrap).Consumed) < 0.01f && tail && recentZero,
                $"C 速率曲线（FGU-13）：{n1} 个点（每点 {ProductionStats.TierBucketSeconds(1)} 秒），合金各点之和 {F(sum1)} = 窗口产出 {a10b.Produced}，废料各点消耗之和 {F(sumC)} = 窗口消耗；断料后的最近几点为 0");
        }

        // ── B3 其它来源：经济账本收支、维修件 ─────────────────────────────────────────────

        private static void CheckLedgerSources()
        {
            CampaignState s = NewWorld(8105, scrap: 600);
            ItemDef scrap = Def(ItemCatalog.ScrapId);
            // 开局仓库是待修的残骸：经面板“重建 / 维修”派单 → 机器修好（废料走经济账本）。
            BuildingRecord wh = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeWarehouse);
            if (wh != null && wh.ConstructionState == BuildingConstructionState.Damaged)
            {
                BuildingOps.TryOrderRepair(s, wh.BuildingId, out _);
                FgProductionSelfCheck.StepUntil(() => wh.ConstructionState == BuildingConstructionState.Operational, 300);
            }
            // 维修件：开单预留、取消全额退回（不计消耗）、再开单、机器修完（计消耗）。
            BuildingRecord f = Place(s, "refinery_furnace");
            HomeInventory.Add(s, Def(BuildingOps.RepairKitId), 20, clampToSpace: false);
            BuildingOps.ApplyDamage(s, f.BuildingId, 60f);
            int kits = BuildingOps.RepairKitsFor(f);
            bool ordered = BuildingOps.TryOrderRepair(s, f.BuildingId, out _) && BuildingOps.TryCancelRepair(s, f.BuildingId, out _);
            long afterCancel = ProductionStats.RateOf(s, 1, Def(BuildingOps.RepairKitId)).Consumed;
            ordered &= BuildingOps.TryOrderRepair(s, f.BuildingId, out _);
            bool repaired = ordered && FgProductionSelfCheck.StepUntil(() => !BuildingOps.IsWorn(f), 300);
            long kitUsed = ProductionStats.RateOf(s, 1, Def(BuildingOps.RepairKitId)).Consumed;
            // 账本守恒：统计里的废料消耗 / 产出 = 经济账本已结算的支出 / 收入（这一局里废料只经账本进出）。
            long ledgerOut = 0, ledgerIn = 0;
            foreach (ResourceTransactionRecord t in s.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
            {
                if (t == null || t.State != ResourceTransactionState.Committed || t.ResourceType != CampaignEconomyLedger.ResourceScrap)
                {
                    continue;
                }
                if (t.Requested < 0f)
                {
                    ledgerIn += (long)Math.Round(-t.Requested);
                }
                else
                {
                    ledgerOut += (long)Math.Round(t.Reserved);
                }
            }
            ProductionStats.ItemRate sr = ProductionStats.RateOf(s, 1, scrap);
            Expect(repaired && afterCancel == 0 && kitUsed == kits && kits > 0 && sr.Consumed == ledgerOut && sr.Produced == ledgerIn && ledgerOut > 0,
                $"B3 其它来源进统计：维修件取消时不计、机器修完计消耗 {kitUsed} = 预留 {kits}；废料的统计消耗 {sr.Consumed} / 产出 {sr.Produced} = 经济账本已结算的支出 {ledgerOut} / 收入 {ledgerIn}（账目守恒）");
        }

        // ── B4 施工材料：取料 / 取消只是搬运，建成才算消耗，拆回算产出（审查修复 FGR-ECO-050）─────────────────

        private static long[] TierSums(CampaignState s, ItemDef item, bool produced)
        {
            var r = new long[ProductionStats.TierCount];
            for (int t = 0; t < r.Length; t++)
            {
                ProductionStats.ItemRate x = ProductionStats.RateOf(s, t, item);
                r[t] = produced ? x.Produced : x.Consumed;
            }
            return r;
        }

        private static bool AllDelta(long[] now, long[] before, long expect) => Enumerable.Range(0, now.Length).All(t => now[t] - before[t] == expect);

        private static string Deltas(long[] now, long[] before) => string.Join("/", Enumerable.Range(0, now.Length).Select(t => (now[t] - before[t]).ToString(CultureInfo.InvariantCulture)));

        /// <summary>按种子地形在核心附近找一处能放下的位置，按玩家路径放一座施工虚影（B25：不写死坐标）。返回建筑 ID（放不下 = null）。</summary>
        private static string PlaceGhost(CampaignState s, string type)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 8; r <= 30; r++)
            {
                for (int a = 0; a < 48; a++)
                {
                    float ang = a * 7.5f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * r), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * r));
                    if (!HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: true, checkCost: false).Ok)
                    {
                        continue;
                    }
                    GridOpResult res = HomeGridService.TryPlace(s, type, c, 0);
                    if (res.Success)
                    {
                        return res.BuildingId;
                    }
                }
            }
            return null;
        }

        private static void CheckConstructionStats()
        {
            CampaignState s = NewWorld(8106, scrap: 30);
            // 去掉开局自带的生产建筑（回收站等），这一局里废料只经施工 / 拆除进出，统计增量可以逐件核对。
            s.BuildingRecords = s.BuildingRecords.Where(b => !ProducerCatalog.IsProducer(b.BuildingTypeId)).ToArray();
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            ItemDef scrap = Def(ItemCatalog.ScrapId);
            long[] c0 = TierSums(s, scrap, false);
            long[] p0 = TierSums(s, scrap, true);
            // 负向：库存只有 30（造价 60）——机器取料 30 运到现场、施工停在一半等材料；此时一件都没用掉。
            string id = PlaceGhost(s, "generator_2");
            BuildingRecord ghost = id == null ? null : HomeGridService.FindBuilding(s, id);
            WorkOrderRecord order = id == null ? null : HomeValleyWorkOrders.FindActiveBuild(s, id);
            bool stalled = order != null && FgProductionSelfCheck.StepUntil(() => order.State == WorkOrderState.Waiting && ghost.ConstructionDelivered == 30, 240);
            bool fetchedNone = AllDelta(TierSums(s, scrap, false), c0, 0) && AllDelta(TierSums(s, scrap, true), p0, 0) && s.Scrap == 0;
            string afterFetch = Deltas(TierSums(s, scrap, false), c0);
            // 玩家取消这座虚影：30 全额退回仓库；四个窗口的废料消耗 / 产出都还是 0（没有虚假消耗，也不进持续赤字）。
            bool refunded = id != null && HomeGridService.TryToggleDemolish(s, id).Outcome == GridOpResult.Kind.PlanCancelled && s.Scrap == 30;
            bool cancelNone = AllDelta(TierSums(s, scrap, false), c0, 0) && AllDelta(TierSums(s, scrap, true), p0, 0);
            Expect(stalled && fetchedNone && refunded && cancelNone,
                $"B4 施工材料只是搬运（负向：取料后取消）：机器把 30 废料从仓库运到现场时四个窗口的废料消耗增量 {afterFetch}；取消施工 30 全额退回（库存 {s.Scrap}），" +
                $"消耗 / 产出增量仍为 {Deltas(TierSums(s, scrap, false), c0)} / {Deltas(TierSums(s, scrap, true), p0)}（{stalled}/{fetchedNone}/{refunded}/{cancelNone}）");
            // 正常：材料够 → 建成时计消耗 60（= 投入）；拆除全额返还 60 → 计产出 60；库存回到原值，净值 0。
            s.Scrap = 100;
            string id2 = PlaceGhost(s, "generator_2");
            BuildingRecord g2 = id2 == null ? null : HomeGridService.FindBuilding(s, id2);
            bool built = g2 != null && FgProductionSelfCheck.StepUntil(() => g2.ConstructionState == BuildingConstructionState.Operational, 400);
            bool usedAtBuild = built && AllDelta(TierSums(s, scrap, false), c0, g2.InvestedScrap) && g2.InvestedScrap == 60 && AllDelta(TierSums(s, scrap, true), p0, 0) && s.Scrap == 40;
            string atBuild = Deltas(TierSums(s, scrap, false), c0);
            if (built)
            {
                HomeGridService.TryToggleDemolish(s, id2);
            }
            bool gone = built && FgProductionSelfCheck.StepUntil(() => HomeGridService.FindBuilding(s, id2) == null, 240);
            bool refundProduced = gone && AllDelta(TierSums(s, scrap, true), p0, 60) && AllDelta(TierSums(s, scrap, false), c0, 60) && s.Scrap == 100;
            // 传送带：每格建成时计这一格的造价（规划取消 / 剩料退回不计）。
            GridCell? area = FgProductionSelfCheck.FindArea(s, 7, 3);
            long[] c1 = TierSums(s, scrap, false);
            bool beltOk = false;
            int beltCost = 0;
            if (area.HasValue)
            {
                GridCell a = FgProductionSelfCheck.At(area.Value, 1, 1);
                GridOpResult bp = HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, FgProductionSelfCheck.At(a, 3, 0), BeltDir.East);
                PlannedBeltRecord plan = s.Grid?.PlannedBelts?.LastOrDefault();
                beltCost = plan != null ? plan.ScrapPerCell * 4 : 0;
                bool beltsBuilt = bp.Success && plan != null && FgProductionSelfCheck.StepUntil(() => (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).All(p => p.PlanId != plan.PlanId), 400);
                beltOk = beltsBuilt && beltCost > 0 && AllDelta(TierSums(s, scrap, false), c1, beltCost);
            }
            Expect(usedAtBuild && refundProduced && beltOk,
                $"B4 建成才算消耗：发电机建成时四个窗口废料消耗增量 {atBuild}（= 投入 {g2?.InvestedScrap}）；拆除返还 60 记为产出（产出增量 {Deltas(TierSums(s, scrap, true), p0)}，库存回到 {s.Scrap}）；" +
                $"4 格传送带建成计消耗 {beltCost}（{usedAtBuild}/{refundProduced}/{beltOk}）");

            // 复审修复（FGR-ECO-050）：升级差额在升级完工（换位）时计消耗，取料不计；升级后拆除按“原造价 + 差额”全额返还记产出 → 建成 + 升级 + 拆除净值 0。
            s.Scrap = 100;
            long[] cb = TierSums(s, scrap, false);
            long[] pb = TierSums(s, scrap, true);
            string pid = PlaceGhost(s, "power_pole");
            BuildingRecord pole = pid == null ? null : HomeGridService.FindBuilding(s, pid);
            bool poleBuilt = pole != null && FgProductionSelfCheck.StepUntil(() => pole.ConstructionState == BuildingConstructionState.Operational, 400);
            int baseCost = pole?.InvestedScrap ?? 0;
            long[] cu = TierSums(s, scrap, false);
            GridOpResult up = poleBuilt ? HomeGridService.TryUpgrade(s, pid) : default;
            BuildingRecord upGhost = up.Success ? HomeGridService.FindBuilding(s, up.BuildingId) : null;
            int diff = upGhost?.ConstructionRequired ?? 0;
            // 负向：差额运到现场、尚未完工的那一刻不计消耗（只是搬运）。
            bool fetchedOnly = upGhost != null && FgProductionSelfCheck.StepUntil(() => upGhost.ConstructionDelivered == diff
                || HomeGridService.FindBuilding(s, up.BuildingId) == null, 400);
            bool noUseBeforeDone = fetchedOnly && (HomeGridService.FindBuilding(s, up.BuildingId) == null || AllDelta(TierSums(s, scrap, false), cu, 0));
            bool upgraded = upGhost != null && FgProductionSelfCheck.StepUntil(() => HomeGridService.FindBuilding(s, pid)?.BuildingTypeId == "power_pole_t2"
                && HomeGridService.FindBuilding(s, up.BuildingId) == null, 400);
            BuildingRecord pole2 = HomeGridService.FindBuilding(s, pid);
            string atUpgrade = Deltas(TierSums(s, scrap, false), cu);
            bool upgradeUsed = upgraded && diff > 0 && AllDelta(TierSums(s, scrap, false), cu, diff) && pole2.InvestedScrap == baseCost + diff;
            if (upgraded)
            {
                HomeGridService.TryToggleDemolish(s, pid);
            }
            bool poleGone = upgraded && FgProductionSelfCheck.StepUntil(() => HomeGridService.FindBuilding(s, pid) == null, 400);
            long[] cEnd = TierSums(s, scrap, false);
            long[] pEnd = TierSums(s, scrap, true);
            bool netZero = poleGone && AllDelta(pEnd, pb, baseCost + diff) && AllDelta(cEnd, cb, baseCost + diff) && s.Scrap == 100;
            Expect(poleBuilt && noUseBeforeDone && upgradeUsed && netZero,
                $"B4 升级差额（FGR-ECO-050）：电塔建成投入 {baseCost}；升级 T1 → T2 差额 {diff} 运到现场时不计，完工时四个窗口消耗增量 {atUpgrade}（= 差额），投入变为 {pole2?.InvestedScrap}；" +
                $"拆除返还记产出增量 {Deltas(pEnd, pb)}、建成 + 升级消耗增量 {Deltas(cEnd, cb)}，库存回到 {s.Scrap}（净值 0）（{poleBuilt}/{noUseBeforeDone}/{upgradeUsed}/{netZero}）");
        }

        // ── D 持续赤字 ───────────────────────────────────────────────────────────────

        private static void CheckDeficit()
        {
            CampaignState s = NewWorld(8111, scrap: 100);
            BuildingRecord f = Place(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
            int notesBefore = Notes("eco_deficit").Sum(e => e.Count);
            int warnBefore = ProductionStats.DeficitWarnings;
            Run(9f * 60f, () => Drain(s, f));
            bool early = ProductionStats.IsDeficit(s, ItemCatalog.ScrapId, out bool warnedEarly) && !warnedEarly && ProductionStats.DeficitWarnings == warnBefore
                         && Notes("eco_deficit").Sum(e => e.Count) == notesBefore;
            bool alloyOk = !ProductionStats.IsDeficit(s, "alloy", out _);
            Run(2.5f * 60f, () => Drain(s, f));
            bool warned = ProductionStats.IsDeficit(s, ItemCatalog.ScrapId, out bool w) && w && ProductionStats.DeficitWarnings == warnBefore + 1;
            List<NotificationEntry> notes = Notes("eco_deficit");
            string text = notes.FirstOrDefault()?.Text ?? string.Empty;
            bool notified = notes.Sum(e => e.Count) == notesBefore + 1 && text.Contains("废料") && text.Contains("分钟") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.StatsFirstDeficit)
                            && !GameText.ContainsMarker(text);
            Run(3f * 60f, () => Drain(s, f));
            bool once = ProductionStats.DeficitWarnings == warnBefore + 1;
            P(s, f).Rec.In = Array.Empty<ItemStackRecord>();
            Run(3f * 60f, () => Drain(s, f));
            bool cleared = !ProductionStats.IsDeficit(s, ItemCatalog.ScrapId, out _) && ProductionStats.Deficits(s).Count == 0;
            Expect(early && alloyOk && warned && notified && once && cleared,
                $"D1 持续赤字（FGR-ECO-050）：精炼炉一直吃废料、没有废料来源——9 分钟时已记为赤字但不警告（{early}）；只产不耗的合金不算赤字；超过 10 分钟发一次警告“{text}”与引导钩子；" +
                $"继续赤字不重复警告（{once}）；停止消耗后检测窗口过去即清除（{cleared}）");
            // D2 短暂赤字（5 分钟）不警告。
            CampaignState s2 = NewWorld(8112, scrap: 100);
            BuildingRecord f2 = Place(s2, "refinery_furnace");
            ProductionService.TrySetRecipe(s2, f2.BuildingId, "alloy_scrap", out _);
            P(s2, f2).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 10000 } };
            int warn2 = ProductionStats.DeficitWarnings;
            Run(5f * 60f, () => Drain(s2, f2));
            bool wasDeficit = ProductionStats.IsDeficit(s2, ItemCatalog.ScrapId, out _);
            P(s2, f2).Rec.In = Array.Empty<ItemStackRecord>();
            Run(10f * 60f, () => Drain(s2, f2));
            Expect(wasDeficit && ProductionStats.DeficitWarnings == warn2 && ProductionStats.Deficits(s2).Count == 0,
                $"D2 负向：只赤字 5 分钟就恢复——期间记为赤字（{wasDeficit}），恢复后清除，始终不发警告（警告 {ProductionStats.DeficitWarnings - warn2} 次）");
        }

        // ── E 瓶颈与建筑效率 ──────────────────────────────────────────────────────────

        private static void CheckBottlenecksAndEfficiency()
        {
            CampaignState s = NewWorld(8121, scrap: 100);
            BuildingRecord bench = Place(s, "electronics_bench");
            BuildingRecord ws = Place(s, "parts_workshop");
            BuildingRecord f = Place(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, bench.BuildingId, "electronic", out _);
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 2 } }; // 有合金、缺稀土矿
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 10000 } };
            bool powered = Powered(s, bench) && Powered(s, ws) && Powered(s, f);
            long scansBefore = ProductionStats.BuildingScans;
            Run(180f, () => Drain(s, f));
            bool noScan = ProductionStats.BuildingScans == scansBefore; // 世界跑了 3 分钟、没有人查询：统计没有逐建筑扫描
            var bns = new List<ProductionStats.Bottleneck>();
            ProductionStats.CollectBottlenecks(s, bns);
            ProductionStats.Bottleneck rare = bns.FirstOrDefault(b => b.Item.Id == "rare_earth_ore");
            ProductionStats.Bottleneck al = bns.FirstOrDefault(b => b.Item.Id == "alloy");
            bool rareOk = rare != null && rare.Buildings.Count == 1 && rare.Buildings[0].Building == bench && Math.Abs(rare.Seconds - 180f) <= 10f;
            bool alloyOk = al != null && al.Buildings.Any(b => b.Building == ws) && al.Buildings.All(b => b.Building != f) && Math.Abs(al.Seconds - 180f) <= 10f;
            bool noFurnace = bns.All(b => b.Buildings.All(x => x.Building != f));
            long scansAfter = ProductionStats.BuildingScans;
            var effs = new List<ProductionStats.BuildingEfficiency>();
            ProductionStats.CollectEfficiencies(s, effs);
            ProductionStats.BuildingEfficiency ef = effs.FirstOrDefault(e => e.Building == f);
            ProductionStats.BuildingEfficiency eb = effs.FirstOrDefault(e => e.Building == bench);
            int iF = effs.IndexOf(ef), iB = effs.IndexOf(eb);
            bool effOk = ef != null && eb != null && ef.Efficiency > 0.85f && eb.Efficiency >= 0f && eb.Efficiency < 0.05f && iB < iF && ef.Outputs.Any(o => o.ItemId == "alloy");
            Expect(powered && noScan && rareOk && alloyOk && noFurnace && effOk && scansAfter > scansBefore,
                $"E 瓶颈与建筑效率（FGR-ECO-050）：最近 10 分钟——稀土矿缺 {F(rare?.Seconds ?? 0)} 秒（电子组装台）、合金缺 {F(al?.Seconds ?? 0)} 秒（零件工坊），正常工作的精炼炉不在列；" +
                $"效率：精炼炉 {ef?.Efficiency:P0}、缺料的电子组装台 {eb?.Efficiency:P0}（低的在前）；世界跑 3 分钟没人查询时统计不扫描建筑（{noScan}），查询时才扫描（有电 {powered}）");
        }

        // ── F 固定到顶栏 ─────────────────────────────────────────────────────────────

        private static void CheckPins()
        {
            CampaignState s = NewWorld(8131);
            IReadOnlyList<PinnedItemRecord> def = ResourcePins.Pins(s);
            bool dflt = def.Count == 1 && def[0].ItemId == ItemCatalog.ScrapId && s.Stats.Production.PinsInitialized;
            int rev = ResourcePins.Revision;
            bool pin = ResourcePins.TryPin(s, "alloy", null, out string m1) && ResourcePins.IsPinned(s, "alloy") && ResourcePins.Revision > rev && m1.Contains("合金")
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.StatsFirstPin);
            bool again = !ResourcePins.TryPin(s, "alloy", null, out string m2) && m2.Contains("已经固定");
            bool unknown = !ResourcePins.TryPin(s, "no_such_item", null, out string m3) && m3.Contains("没有这种物品");
            var ids = ItemCatalog.Items.Where(d => d.Id != ItemCatalog.ScrapId && d.Id != "alloy").Select(d => d.Id).ToList();
            int k = 0;
            while (ResourcePins.Pins(s).Count < ResourcePins.MaxPins && k < ids.Count)
            {
                ResourcePins.TryPin(s, ids[k++], null, out _);
            }
            string m4 = string.Empty;
            bool full = ResourcePins.Pins(s).Count == ResourcePins.MaxPins && !ResourcePins.TryPin(s, ids[k], null, out m4)
                        && m4.Contains(ResourcePins.MaxPins.ToString(CultureInfo.InvariantCulture)) && !ResourcePins.IsPinned(s, ids[k]);
            bool unpin = ResourcePins.TryUnpin(s, "alloy", out string m5) && !ResourcePins.IsPinned(s, "alloy") && m5.Contains("取消固定")
                         && !ResourcePins.TryUnpin(s, "alloy", out string m6) && m6.Contains("没有固定");
            foreach (PinnedItemRecord p in ResourcePins.Pins(s).ToArray())
            {
                ResourcePins.TryUnpin(s, p.ItemId, out _);
            }
            bool staysEmpty = ResourcePins.Pins(s).Count == 0 && ProductionStats.Ensure(s).Pins.Length == 0;
            bool target = ResourcePins.TryPin(s, "precision_part", 10f, out string m7) && Math.Abs(ResourcePins.Find(s, "precision_part").TargetPerMinute - 10f) < 0.001f && m7.Contains("目标")
                          && ResourcePins.TryPin(s, "precision_part", 12f, out _) && Math.Abs(ResourcePins.Find(s, "precision_part").TargetPerMinute - 12f) < 0.001f
                          && ResourcePins.Pins(s).Count == 1;
            ResourcePins.PinView v = ResourcePins.View(s, ResourcePins.Find(s, "precision_part"));
            bool view = v.HasTarget && !v.Reached && v.Stock == HomeInventory.Stock(s, "precision_part");
            Expect(dflt && pin && again && unknown && full && unpin && staysEmpty && target && view,
                $"F 资源顶栏固定（FGR-ECO-051）：新游戏默认固定废料；任意物品可固定（钩子）；重复固定“{m2}”；未知物品拒绝；满 {ResourcePins.MaxPins} 种后再固定被拒“{m4}”；" +
                $"取消固定与重复取消；全部取消后不再自动补回废料；带目标固定（{m7}）与改目标；无产量时显示未达标");
        }

        // ── G 规划助手 ───────────────────────────────────────────────────────────────

        private static ProductionPlanner.RecipeRow Row(ProductionPlanner.Result r, string recipeId) => r.Rows.FirstOrDefault(x => x.Recipe.Id == recipeId);
        private static ProductionPlanner.RawRow Raw(ProductionPlanner.Result r, string itemId) => r.Raws.FirstOrDefault(x => x.Item.Id == itemId);

        private static void CheckPlannerMath()
        {
            CampaignState s = NewWorld(8141);
            string before = string.Join("|", s.BuildingRecords.Length, s.WorkOrders?.Length ?? 0, s.Scrap, JsonUtility.ToJson(s.Economy), s.ResourceTransactions?.Length ?? 0);
            ItemDef pp = Def("precision_part");
            ProductionPlanner.Result r = ProductionPlanner.Plan(pp, 10f);
            // 期望值直接按表算：一座满速每分钟 60 ÷ 秒数 份。
            RecipeDef rp = ItemCatalog.Recipes.First(x => x.Id == "precision_part");
            RecipeDef rPart = ItemCatalog.Recipes.First(x => x.Id == "part");
            RecipeDef rEl = ItemCatalog.Recipes.First(x => x.Id == "electronic");
            string alloyDefault = ProductionPlanner.RecipesFor("alloy")[0].Id;
            RecipeDef rAlloy = ItemCatalog.Recipes.First(x => x.Id == alloyDefault);
            double In(RecipeDef rec, string item) => rec.Lines.Where(l => l.Role == RecipeRole.In && l.Item.Id == item).Sum(l => l.Amount);
            double Out(RecipeDef rec, string item) => rec.Lines.Where(l => l.Role == RecipeRole.Out && l.Item.Id == item).Sum(l => l.Amount);
            double cP = 10.0 / Out(rp, "precision_part");
            double partNeed = cP * In(rp, "part"), elNeed = cP * In(rp, "electronic");
            double cPart = partNeed / Out(rPart, "part"), cEl = elNeed / Out(rEl, "electronic");
            double alloyNeed = cPart * In(rPart, "alloy") + cEl * In(rEl, "alloy");
            double cAlloy = alloyNeed / Out(rAlloy, "alloy");
            bool rows = r.Ok && r.Rows[0].Recipe.Id == "precision_part"
                        && Math.Abs(Row(r, "precision_part").Buildings - cP * rp.Seconds / 60.0) < 1e-6 && Row(r, "precision_part").BuildingsToBuild == (int)Math.Ceiling(cP * rp.Seconds / 60.0 - 1e-6)
                        && Math.Abs(Row(r, "part").Buildings - cPart * rPart.Seconds / 60.0) < 1e-6
                        && Math.Abs(Row(r, "electronic").Buildings - cEl * rEl.Seconds / 60.0) < 1e-6
                        && Math.Abs(Row(r, alloyDefault).Buildings - cAlloy * rAlloy.Seconds / 60.0) < 1e-6;
            double rareNeed = cEl * In(rEl, "rare_earth_ore");
            bool raws = Math.Abs(Raw(r, "rare_earth_ore").PerMinute - rareNeed) < 1e-6 && Raw(r, "rare_earth_ore").ExtractorType == "extraction_drill"
                        && Raw(r, "rare_earth_ore").ExtractorsToBuild >= 1;
            double power = r.Rows.Sum(x => x.Buildings * ProductionPlanner.PowerOf(x.BuildingType));
            double powerBuild = r.Rows.Sum(x => x.BuildingsToBuild * ProductionPlanner.PowerOf(x.BuildingType));
            bool powerOk = Math.Abs(r.Power - power) < 1e-6 && Math.Abs(r.PowerToBuild - powerBuild) < 1e-6 && r.PowerToBuild > 0;
            bool capacity = Math.Abs(r.TargetCapacityPerMinute - Row(r, "precision_part").BuildingsToBuild * 60.0 / rp.Seconds * Out(rp, "precision_part")) < 1e-6;
            Expect(rows && raws && powerOk && capacity,
                $"G1 规划（FGR-ECO-052）：每分钟 10 个精密零件 → 电子组装台(精密) {F(Row(r, "precision_part")?.Buildings ?? 0)} 座 → 建 {Row(r, "precision_part")?.BuildingsToBuild}；零件工坊 {F(Row(r, "part")?.Buildings ?? 0)}；" +
                $"电子组装台(电子) {F(Row(r, "electronic")?.Buildings ?? 0)}；精炼炉({alloyDefault}) {F(Row(r, alloyDefault)?.Buildings ?? 0)}；稀土矿 {F(rareNeed)}/分 → 提取钻 {Raw(r, "rare_earth_ore")?.ExtractorsToBuild} 座；" +
                $"电力 {F(r.Power)}（按要建 {F(r.PowerToBuild)}）；取整后产能 {F(r.TargetCapacityPerMinute)}/分——与按表独立计算一致");
            // 换配方：合金改用另一条配方，原料随之变化。
            List<RecipeDef> alloyOpts = ProductionPlanner.RecipesFor("alloy");
            string other = alloyOpts.Count > 1 ? alloyOpts[1].Id : null;
            ProductionPlanner.Result r2 = other == null ? null : ProductionPlanner.Plan(pp, 10f, new Dictionary<string, string> { ["alloy"] = other });
            bool switched = r2 != null && r2.Ok && Row(r2, other) != null && Row(r2, alloyDefault) == null && Row(r, alloyDefault).Alternatives.Any(x => x.Id == other)
                            && r2.Raws.Select(x => x.Item.Id).OrderBy(x => x).SequenceEqual(r.Raws.Select(x => x.Item.Id).OrderBy(x => x)) == false;
            // 副产品与流体：燃油（精炼塔，副产品酸液）每分钟 60 升 → 原油用流体泵。
            ProductionPlanner.Result rf = ProductionPlanner.Plan(Def("fuel"), 60f);
            bool byproduct = rf.Ok && rf.Byproducts.Any(b => b.Item.Id == "acid" && b.PerMinute > 0) && Raw(rf, "crude")?.ExtractorType == "fluid_pump";
            // 原料作为目标：金属矿每分钟 60 → 提取钻。
            ProductionPlanner.Result ro = ProductionPlanner.Plan(Def("metal_ore"), 60f);
            bool rawTarget = ro.Ok && ro.Rows.Count == 0 && Raw(ro, "metal_ore")?.ExtractorType == "extraction_drill" && ro.TargetCapacityPerMinute >= 60f;
            Expect(switched && byproduct && rawTarget,
                $"G2 换配方（合金 {alloyDefault} → {other}，原料随之变化）；副产品（燃油 60 升/分 → 酸液 {F(rf.Byproducts.FirstOrDefault()?.PerMinute ?? 0)} 升/分，原油用流体泵）；原料本身作目标（金属矿 → 提取钻 {Raw(ro, "metal_ore")?.ExtractorsToBuild} 座）");
            // 负向：没选目标、产量非法、不能生产的物品；Targets 不含不能生产 / 采集的物品。
            ProductionPlanner.Result e1 = ProductionPlanner.Plan(null, 10f);
            ProductionPlanner.Result e2 = ProductionPlanner.Plan(pp, 0f);
            ProductionPlanner.Result e3 = ProductionPlanner.Plan(pp, -5f);
            ProductionPlanner.Result e4 = ProductionPlanner.Plan(pp, float.NaN);
            ProductionPlanner.Result e5 = ProductionPlanner.Plan(pp, ProductionPlanner.MaxRate * 2f);
            ProductionPlanner.Result e6 = ProductionPlanner.Plan(Def("human_legacy"), 1f);
            List<ItemDef> targets = ProductionPlanner.Targets();
            bool negatives = !e1.Ok && e1.Error.Contains("目标") && !e2.Ok && !e3.Ok && !e4.Ok && !e5.Ok && e2.Error.Contains("大于 0") && !e6.Ok && e6.Error.Contains("不能用产线生产")
                             && targets.Any(d => d.Id == "precision_part") && targets.Any(d => d.Id == "metal_ore") && targets.All(d => d.Id != "human_legacy" && d.Id != "machine");
            string after = string.Join("|", s.BuildingRecords.Length, s.WorkOrders?.Length ?? 0, s.Scrap, JsonUtility.ToJson(s.Economy), s.ResourceTransactions?.Length ?? 0);
            Expect(negatives && before == after,
                $"G3 负向：没选目标“{e1.Error}”、产量 0 / 负 / NaN / 超上限“{e2.Error}”、不能生产“{e6.Error}”都拒绝并说明；可选目标 {targets.Count} 种（不含远征物 / 机器）；" +
                $"规划只计算——建筑、工单、库存、事务在规划前后完全不变（FGR-BASE-020）");
        }

        // ── H FGT-ECO-008：按规划搭建，实测误差 ≤ 5% ─────────────────────────────────────

        private static void CheckPlannerMatchesBuiltLine()
        {
            CampaignState s = NewWorld(8151, scrap: 100);
            ItemDef target = Def("precision_part");
            // 合金用废料配方：原料只有废料与稀土矿，全部由测试按规划量送进去（模拟上游采集）。
            var choices = new Dictionary<string, string> { ["alloy"] = "alloy_scrap" };
            ProductionPlanner.Result plan = ProductionPlanner.Plan(target, 10f, choices);
            float demandBefore = HomeValleyPowerGrid.GetSummary(s).TotalDemand;
            var byRecipe = new Dictionary<string, List<BuildingRecord>>();
            bool placed = plan.Ok;
            foreach (ProductionPlanner.RecipeRow row in plan.Rows)
            {
                var list = new List<BuildingRecord>();
                for (int i = 0; i < row.BuildingsToBuild; i++)
                {
                    BuildingRecord b = Place(s, row.BuildingType);
                    if (b == null)
                    {
                        placed = false;
                        continue;
                    }
                    ProductionService.TrySetRecipe(s, b.BuildingId, row.Recipe.Id, out _); // 只有一条配方的建筑本来就是它（设置会被拒绝，这里只核对结果）
                    placed &= P(s, b)?.Recipe?.Id == row.Recipe.Id;
                    list.Add(b);
                }
                byRecipe[row.Recipe.Id] = list;
            }
            Seconds(1f);
            HomeValleyPowerGrid.Recompute(s);
            bool allPowered = byRecipe.Values.SelectMany(x => x).All(b => Powered(s, b));
            float demandAfter = HomeValleyPowerGrid.GetSummary(s).TotalDemand;
            // 每种物品的消费者（配方行）与它在规划里的需求量：按“已送达 ÷ 规划需求”最少的配方先送（相当于按规划比例设好的分流器），配方内送给缓存最少的那座。
            var consumers = new Dictionary<string, List<(ProductionPlanner.RecipeRow Row, double Demand)>>();
            foreach (ProductionPlanner.RecipeRow row in plan.Rows)
            {
                foreach (RecipeLine l in row.Recipe.Lines.Where(l => l.Role == RecipeRole.In))
                {
                    if (!consumers.TryGetValue(l.Item.Id, out var cl))
                    {
                        consumers[l.Item.Id] = cl = new List<(ProductionPlanner.RecipeRow, double)>();
                    }
                    cl.Add((row, row.CyclesPerMinute * l.Amount));
                }
            }
            var delivered = new Dictionary<string, double>();
            void Give(string itemId, int amount)
            {
                if (!consumers.TryGetValue(itemId, out var cl))
                {
                    return;
                }
                for (int n = 0; n < amount; n++)
                {
                    (ProductionPlanner.RecipeRow row, double demand) = cl.OrderBy(c => (delivered.TryGetValue(c.Row.Recipe.Id + "/" + itemId, out double d) ? d : 0) / c.Demand).First();
                    string key = row.Recipe.Id + "/" + itemId;
                    delivered[key] = (delivered.TryGetValue(key, out double dd) ? dd : 0) + 1;
                    BuildingRecord b = byRecipe[row.Recipe.Id].OrderBy(x => ProductionService.Count(P(s, x).Rec.In, itemId)).First();
                    ProductionService.Add(ref P(s, b).Rec.In, itemId, 1);
                }
            }
            var rawAcc = new Dictionary<string, double>();
            long madeTotal = 0;
            long madeMeasured = 0;
            bool measuring = false;
            void Tick()
            {
                // 原料：按规划量每半秒送一次（累积零头）。
                foreach (ProductionPlanner.RawRow raw in plan.Raws)
                {
                    double acc = (rawAcc.TryGetValue(raw.Item.Id, out double a) ? a : 0) + raw.PerMinute / 120.0;
                    int whole = (int)Math.Floor(acc + 1e-9);
                    rawAcc[raw.Item.Id] = acc - whole;
                    Give(raw.Item.Id, whole);
                }
                // 中间品：从上游输出缓存搬到下游（传送带）；目标产物被“仓库”收走。
                foreach (List<BuildingRecord> group in byRecipe.Values)
                {
                    foreach (BuildingRecord b in group)
                    {
                        ProductionService.Producer p = P(s, b);
                        ItemStackRecord[] outs = p.Rec.Out;
                        p.Rec.Out = Array.Empty<ItemStackRecord>();
                        foreach (ItemStackRecord o in outs ?? Array.Empty<ItemStackRecord>())
                        {
                            if (o == null || o.Amount <= 0)
                            {
                                continue;
                            }
                            if (o.ItemId == target.Id)
                            {
                                madeTotal += o.Amount;
                                if (measuring)
                                {
                                    madeMeasured += o.Amount;
                                }
                                continue;
                            }
                            Give(o.ItemId, o.Amount);
                        }
                    }
                }
            }
            Run(300f, Tick); // 预热 5 分钟：缓存与在制品稳定
            measuring = true;
            Run(600f, Tick); // 实测 10 分钟
            float statsRate = ProductionStats.RateOf(s, 1, target).ProducedPerMinute;
            float countRate = madeMeasured / 10f;
            float errStats = Math.Abs(statsRate - 10f) / 10f;
            float errCount = Math.Abs(countRate - 10f) / 10f;
            // 产能：目标那一组建筑满料满速 2 分钟，实测与“取整后产能”比。
            ProductionPlanner.RecipeRow top = plan.Rows[0];
            int capMade = 0;
            Run(120f, () =>
            {
                foreach (BuildingRecord b in byRecipe[top.Recipe.Id])
                {
                    ProductionService.Producer p = P(s, b);
                    p.Rec.In = top.Recipe.Lines.Where(l => l.Role == RecipeRole.In).Select(l => new ItemStackRecord { ItemId = l.Item.Id, Amount = l.Amount * 4 }).ToArray();
                    capMade += ProductionService.Count(p.Rec.Out, target.Id);
                    p.Rec.Out = Array.Empty<ItemStackRecord>();
                }
            });
            float capRate = capMade / 2f;
            float errCap = (float)(Math.Abs(capRate - plan.TargetCapacityPerMinute) / plan.TargetCapacityPerMinute);
            float powerDelta = demandAfter - demandBefore;
            Expect(placed && allPowered && errStats <= 0.05f && errCount <= 0.05f && errCap <= 0.05f && Math.Abs(powerDelta - (float)plan.PowerToBuild) <= 0.5f,
                $"H FGT-ECO-008：按规划（每分钟 10 个精密零件，合金走废料配方）搭建 {byRecipe.Values.Sum(x => x.Count)} 座建筑、只按规划量送原料（{string.Join("、", plan.Raws.Select(x => x.Item.Name + " " + F(x.PerMinute) + "/分"))}），" +
                $"预热 5 分钟后实测 10 分钟：统计面板速率 {F(statsRate)}/分（误差 {errStats:P1}）、出货计数 {F(countRate)}/分（误差 {errCount:P1}）；" +
                $"满料产能实测 {F(capRate)}/分 vs 规划取整后产能 {F(plan.TargetCapacityPerMinute)}（误差 {errCap:P1}）；电网需要增加 {F(powerDelta)} = 规划电力 {F(plan.PowerToBuild)}（全部有电 {allPowered}）");
        }

        // ── O 物流与能源（B19：DEBT-FG3LOG03-08 / 04-04 / 05-06 / 06-04、DEBT-FG4ECO04-03）──────────────────────

        private static void CheckFlowEnergy()
        {
            CampaignState s = NewWorld(8201, scrap: 100);
            // 电力：用电超过发电 → 低优先级断电；曲线里的缺电点数 × 采样秒 = 缺电时长（与电网面板同一条曲线独立核对）。
            var hogs = new List<BuildingRecord>();
            for (int i = 0; i < 24; i++)
            {
                BuildingRecord b = Place(s, "refinery_furnace");
                if (b != null)
                {
                    hogs.Add(b);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            // 传送带：源 → 3 格 → 分流器 → 左右各 2 格 → 两个收货口（模拟仓库）。
            GridCell? area = FgProductionSelfCheck.FindArea(s, 9, 7, 26f, 40f) ?? FgProductionSelfCheck.FindArea(s, 9, 7);
            bool beltLaid = false;
            GridCell sp = default;
            if (area.HasValue)
            {
                GridCell o = FgProductionSelfCheck.At(area.Value, 0, 3);
                beltLaid = FgProductionSelfCheck.Belts(s, o, BeltDir.East, 3);
                sp = FgProductionSelfCheck.At(o, 3, 0);
                beltLaid &= BeltNetworkService.TryPlaceNode(s, sp, BeltDir.East, 2, BeltNodeKind.Splitter).Ok;
                if (beltLaid && BeltNetworkService.Kernel.TryGetNodeInfo(sp.X, sp.Y, out BeltNodeInfo ni))
                {
                    foreach ((int x, int y) in new[] { (ni.OutLX, ni.OutLY), (ni.OutRX, ni.OutRY) })
                    {
                        var c = new GridCell(x, y);
                        beltLaid &= FgProductionSelfCheck.Belts(s, c, BeltDir.East, 2);
                        FgProductionSelfCheck.TestSink(s, FgProductionSelfCheck.At(c, 2, 0));
                    }
                }
                FgProductionSelfCheck.TestSource(s, o, ItemCatalog.ScrapId, 200);
            }
            ProductionStats.NoteFuelOut(s); // 燃油耗尽的真实路径（燃油发电机烧空）由 FgEnergySelfCheck 覆盖；这里只核对计数进统计面板与存档
            Seconds(150f);
            var lines = new List<(string Text, string Cls)>();
            FlowEnergyStats.Build(s, lines);
            string all = string.Join(" / ", lines.Select(l => l.Text));
            // 电力独立核对。
            var subs = new List<int>();
            HomeValleyPowerGrid.SubnetsBySerial(subs);
            bool powerOk = subs.Count > 0;
            int shortTotal = 0;
            foreach (int si in subs)
            {
                HomeValleyPowerGrid.TryGetSubnetInfo(s, si, out PowerSubnetInfo info);
                if (!HomeValleyPowerGrid.Kernel.TryGetCurve(info.Serial, out PowerCurve c) || c.Count == 0)
                {
                    powerOk = false;
                    continue;
                }
                double sup = 0;
                int shortPts = 0;
                for (int i = 0; i < c.Count; i++)
                {
                    c.Get(i, out float a, out float d, out float dl, out _);
                    sup += a;
                    shortPts += dl + 0.5f < d ? 1 : 0;
                }
                shortTotal += shortPts;
                string expect = GameText.Format("stats.flow.power_row", HomeValleyPowerGrid.SubnetName(info.Serial), ProductionStats.UiNumber((float)(sup / c.Count)), "",
                    "", "", ProductionStats.UiNumber(shortPts * HomeValleyPowerGrid.SampleSeconds / 60f), "");
                string head = expect.Substring(0, expect.IndexOf('（'));
                string shortTxt = "缺电 " + ProductionStats.UiNumber(shortPts * HomeValleyPowerGrid.SampleSeconds / 60f) + " 分钟";
                powerOk &= lines.Any(l => l.Text.StartsWith(head, StringComparison.Ordinal) && l.Text.Contains(shortTxt));
            }
            // 传送带与分流器：读数与内核一致，并且真的送到了货、两口都分出过。
            BeltKernel k = BeltNetworkService.Kernel;
            bool beltOk = beltLaid && k != null;
            long sentL = 0, sentR = 0;
            if (beltOk && k.TryGetNodeInfo(sp.X, sp.Y, out BeltNodeInfo info2))
            {
                sentL = info2.SentL;
                sentR = info2.SentR;
                beltOk &= lines.Any(l => l.Text.StartsWith($"分流器（{sp.X}, {sp.Y}）：累计左口 {sentL} 件 · 右口 {sentR} 件", StringComparison.Ordinal)) && sentL > 0 && sentR > 0;
            }
            bool netRow = lines.Any(l => l.Text.StartsWith("网络 1：送达 ", StringComparison.Ordinal)) && lines.Any(l => l.Text.StartsWith("清带：送进仓库 " + s.Belts.ClearedToStorage, StringComparison.Ordinal));
            // 管线：与内核累计一致；燃油耗尽次数进统计。
            PipeKernel pk = PipeNetworkService.Kernel;
            string pipeExpect = pk == null ? "还没有管线" : GameText.Format("stats.flow.pipe_row", ProductionStats.UiNumber(pk.TotalPumpedMl / 1000f), ProductionStats.UiNumber(pk.TotalDeliveredMl / 1000f),
                ProductionStats.UiNumber(pk.TotalFlushedMl / 1000f), ProductionStats.UiNumber(pk.TotalRemovedMl / 1000f));
            bool pipeOk = lines.Any(l => l.Text == pipeExpect) || (pk != null && pk.TotalPumpedMl + pk.TotalDeliveredMl + pk.TotalFlushedMl + pk.TotalRemovedMl == 0 && all.Contains("还没有管线"));
            bool fuel = s.Stats.Production.FuelOuts == 1;
            Expect(hogs.Count >= 20 && powerOk && shortTotal > 0 && beltOk && netRow && pipeOk && fuel && !GameText.ContainsMarker(all),
                $"O 物流与能源（B19）：{hogs.Count} 座精炼炉压垮电网——每个电网的平均发电、缺电时长（曲线 {shortTotal} 个缺电点）与电网面板同一曲线一致；传送带网络送达速率与清带累计；" +
                $"分流器左右口累计 {sentL} / {sentR}（与内核一致）；管线累计与内核一致；燃油耗尽次数进统计（{fuel}）（{powerOk}/{beltOk}/{netRow}/{pipeOk}）\n{all}");
        }

        // ── I 统计面板（真 UXML）──────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public bool GetKey(KeyCode key) => key == Down || Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        /// <summary>与真实点击同一个回调（Clickable.Invoke）；按钮为空 / 没有 Clickable 记失败。</summary>
        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private static string Rows(StatsPanelUIToolkit p) => string.Join(" / ", Enumerable.Range(0, p.VisibleRowCount).Select(p.RowText));

        private static void CheckPanel()
        {
            // 空状态（卡片负向“还没有任何产线时的空状态”）：家园没有生产建筑。
            CampaignState blank = NewWorld(8160);
            foreach (BuildingRecord b in blank.BuildingRecords.Where(b => ProducerCatalog.IsProducer(b.BuildingTypeId)).ToArray())
            {
                blank.BuildingRecords = blank.BuildingRecords.Where(x => x != b).ToArray();
            }
            HomeValleyPowerGrid.Recompute(blank);
            FgProductionSelfCheck.Resync(blank);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "StatsPanel.uxml", out GameObject go);
            VisualElement proot = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
            StatsPanelUIToolkit.InWorldOverrideForTests = true;
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            StatsPanelUIToolkit.Clock = () => _fakeNow;
            try
            {
                StatsPanelUIToolkit panel = go.AddComponent<StatsPanelUIToolkit>();
                panel.BindView(root);
                // 真 UXML 的建筑面板（I3 核对“打开面板”真的显示了这座建筑，而不只是记下了请求）。
                ProductionPanelUIToolkit ppanel = pgo.AddComponent<ProductionPanelUIToolkit>();
                ppanel.BindView(proot);
                var keys = new Keys();
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                keys.Held.Add(KeyCode.LeftAlt);
                keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.OpenStats);
                UiKitInputPump.ProcessLibraryKeys();
                keys.Down = KeyCode.None;
                keys.Held.Clear();
                InputRouter.DebugClearConsumedKeys();
                bool opened = StatsPanelUIToolkit.IsOpen && panel.PanelVisible && panel.CurrentTab == StatsTab.Production;
                bool emptyProd = panel.EmptyText.Contains("还没有任何产线") && panel.VisibleRowCount == 0;
                string emptyText = panel.EmptyText;
                Click(panel.TabButton(StatsTab.Buildings));
                bool emptyB = panel.EmptyText.Contains("还没有任何产线");
                Click(panel.TabButton(StatsTab.Bottlenecks));
                bool emptyBn = panel.EmptyText.Contains("还没有任何产线");
                Click(panel.TabButton(StatsTab.Planner));
                bool plannerWorks = panel.LastPlan != null && panel.LastPlan.Ok && panel.VisibleRowCount > 0; // 没有产线也能规划
                Expect(opened && emptyProd && emptyB && emptyBn && plannerWorks && GameSettings.HasSeenGuidanceHook(GuidanceHooks.StatsProductionFirstOpen),
                    $"I1 空状态（卡片负向）：Alt+T 打开统计面板默认在“生产”页；家园没有产线时生产 / 建筑效率 / 瓶颈三页都显示“{emptyText}”；规划助手照常可用" +
                    $"（{opened}/{emptyProd}/{emptyB}/{emptyBn}/{plannerWorks}）");
                StatsPanelUIToolkit.Close();

                // 有产线：精炼炉（满料）+ 缺稀土的电子组装台。
                CampaignState s = NewWorld(8161, scrap: 100);
                BuildingRecord f = Place(s, "refinery_furnace");
                BuildingRecord bench = Place(s, "electronics_bench");
                ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
                ProductionService.TrySetRecipe(s, bench.BuildingId, "electronic", out _);
                P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 10000 } };
                P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 1 } };
                Run(90f, () => Drain(s, f));
                StatsPanelUIToolkit.Open();
                Click(panel.TabButton(StatsTab.Production));
                string prod = Rows(panel);
                int alloyRow = panel.FindRow("合金：产出");
                int scrapRow = panel.FindRow("废料：产出");
                bool prodRows = alloyRow >= 0 && scrapRow >= 0 && panel.RowText(alloyRow).Contains("/分") && panel.RowButtonVisible(alloyRow, 0) && panel.CountText.Contains("种物品")
                                && !GameText.ContainsMarker(prod + panel.FooterText + panel.SectionText);
                // 固定按钮（行内）：合金固定到顶栏，再点取消。
                Click(panel.RowButton(alloyRow, 0));
                bool pinned = ResourcePins.IsPinned(s, "alloy") && panel.MessageText.Contains("已固定");
                alloyRow = panel.FindRow("合金：产出");
                bool pinTag = panel.RowText(alloyRow).Contains("[已固定]");
                Click(panel.RowButton(alloyRow, 0));
                bool unpinned = !ResourcePins.IsPinned(s, "alloy");
                // 点一行看曲线：曲线点数 = 桶数，与统计同源。
                panel.ClickRow(panel.FindRow("合金：产出"));
                var pa = new float[64];
                var ca = new float[64];
                ProductionStats.Series(s, panel.CurrentWindow, Def("alloy"), pa, ca);
                bool curve = panel.SelectedItemId == "alloy" && panel.CurveVisible && panel.CurvePoints == ProductionStats.BucketCount && panel.CurveTitleText.Contains("合金")
                             && Enumerable.Range(0, panel.CurvePoints).All(i => Math.Abs(panel.CurveProduced(i) - pa[i]) < 1e-4f) && panel.CurveLegendText.Contains("方块");
                // 切窗口：1 小时。
                Click(panel.WindowButton(2));
                bool window = panel.CurrentWindow == 2 && panel.RowText(0).Contains("1 小时");
                // 节流：节流期内不重建；时间到了才重读。
                int rb = panel.Rebuilds;
                panel.Refresh();
                bool throttled = panel.Rebuilds == rb;
                _fakeNow += 2;
                panel.Refresh();
                bool reread = panel.Rebuilds == rb + 1;
                Expect(opened && prodRows && pinned && pinTag && unpinned && curve && window && throttled && reread,
                    $"I2 生产页（FGU-13）：每种物品一行（“{panel.RowText(panel.FindRow("合金：产出"))}”），行内“固定 / 取消固定”、点一行看速率曲线（{panel.CurvePoints} 点、与统计同源、产出 / 消耗形状不同），" +
                    $"切到“1 小时”窗口；打开时按 {GridContent.Tuning("eco.stats.panel_refresh_seconds")} 真实秒节流重读（节流 {throttled}，到点重读 {reread}）" +
                    $"（{opened}/{prodRows}/{pinned}/{pinTag}/{unpinned}/{curve}/{window}）");
                // 建筑效率页：效率低的在前；“打开面板”关掉统计并打开该建筑面板（不必在家园）。
                // 审查修复：镜头先离开家园、去观察远征地点（破碎都市），再从统计面板远程打开家园建筑的面板。
                string homeSite = WorldSimulation.Home?.SiteId;
                FracturedCityRegion.EnsureRegionRecordSeeded(s);
                FracturedCityRegion.Find(s).State = RegionState.Available;
                FracturedCityController city = WorldSimulation.LoadFracturedCity(Array.Empty<int>(), resume: false);
                bool away = city != null && city.IsLoaded && WorldView.Observe(city.SiteId) && WorldView.ObservedSiteId == city.SiteId && city.SiteId != homeSite;
                string observedAt = WorldView.ObservedSiteId;
                if (!StatsPanelUIToolkit.IsOpen)
                {
                    StatsPanelUIToolkit.Open();
                }
                Click(panel.TabButton(StatsTab.Buildings));
                int bRow = panel.FindRow("电子组装台");
                int fRow = panel.FindRow("精炼炉");
                bool effRows = bRow >= 0 && fRow >= 0 && bRow < fRow && panel.RowText(fRow).Contains("效率") && panel.RowButtonVisible(fRow, 0);
                string effAll = Rows(panel);
                Click(panel.RowButton(fRow, 0));
                ppanel.Refresh();
                bool jumped = away && !StatsPanelUIToolkit.IsOpen && panel.LastJumpBuildingId == f.BuildingId && WorldView.ObservedSiteId == city.SiteId
                              && ProductionPanelUIToolkit.IsOpen && ppanel.PanelVisible && ProductionPanelUIToolkit.BuildingId == f.BuildingId && ppanel.TitleText.Contains("精炼炉");
                string jumpTitle = ppanel.TitleText;
                ProductionPanelUIToolkit.Close();
                if (homeSite != null)
                {
                    WorldView.Observe(homeSite);
                }
                // 瓶颈页：稀土矿 → 电子组装台，可打开面板。
                StatsPanelUIToolkit.OpenTab(StatsTab.Bottlenecks);
                int rare = panel.FindRow("稀土矿");
                bool bnRows = rare >= 0 && panel.RowText(rare + 1).Contains("电子组装台") && panel.RowButtonVisible(rare + 1, 0) && panel.CountText.Contains("缺料");
                string bnAll = Rows(panel);
                Click(panel.RowButton(rare + 1, 0));
                bool jumped2 = ProductionPanelUIToolkit.IsOpen && ppanel.PanelVisible && ProductionPanelUIToolkit.BuildingId == bench.BuildingId && !StatsPanelUIToolkit.IsOpen;
                ProductionPanelUIToolkit.Close();
                Expect(effRows && jumped && bnRows && jumped2,
                    $"I3 建筑效率页（缺料的电子组装台排在精炼炉前）与瓶颈页（“{panel.RowText(rare)}” → “{panel.RowText(rare + 1)}”）：“打开面板”收起统计并远程打开该建筑面板（DEBT-FG0ARCH01-07）：" +
                    $"镜头在 {observedAt}（离开家园）时打开，真 UXML 面板可见、标题“{jumpTitle}”" +
                    $"（{away}/{effRows}/{jumped}/{bnRows}/{jumped2}）" + (effRows && bnRows ? string.Empty : $"\n效率页：{effAll}\n瓶颈页：{bnAll}"));
                // 规划页（FGU-14）：下拉选目标、输入产量（非法 → 原因）、换配方、固定为目标。
                StatsPanelUIToolkit.OpenTab(StatsTab.Planner, "precision_part");
                bool picked = panel.PlannerTargetId == "precision_part" && panel.LastPlan.Ok && panel.FindRow("电子组装台 ×") >= 0 && panel.FindRow("不会自动建造") >= 0;
                RulesPanelUIToolkit.PickForTests(panel.PlannerTargetField, panel.PlannerTargetField.choices.IndexOf("零件"));
                bool viaDropdown = panel.PlannerTargetId == "part" && panel.LastPlan.Target.Id == "part";
                RulesPanelUIToolkit.PickForTests(panel.PlannerTargetField, panel.PlannerTargetField.choices.IndexOf("精密零件"));
                panel.SetPlannerRateText("abc");
                bool bad = !panel.LastPlan.Ok && panel.RowText(0).Contains("大于 0");
                string badText = panel.RowText(0);
                panel.SetPlannerRateText("20");
                bool rate20 = panel.LastPlan.Ok && Math.Abs(panel.LastPlan.TargetPerMinute - 20f) < 1e-4;
                int alloyPlanRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowText(i).Contains("精炼炉 ×"));
                string beforeRecipe = panel.LastPlan.Rows.First(x => x.ForItem.Id == "alloy").Recipe.Id;
                Click(panel.RowButton(alloyPlanRow, 0));
                string afterRecipe = panel.LastPlan.Rows.First(x => x.ForItem.Id == "alloy").Recipe.Id;
                Click(panel.PlannerPinButton);
                PinnedItemRecord tp = ResourcePins.Find(s, "precision_part");
                bool pinTarget = tp != null && Math.Abs(tp.TargetPerMinute - 20f) < 1e-4 && panel.MessageText.Contains("目标");
                Expect(picked && viaDropdown && bad && rate20 && beforeRecipe != afterRecipe && pinTarget && GameSettings.HasSeenGuidanceHook(GuidanceHooks.PlannerFirstPlan),
                    $"I4 规划助手页（FGU-14）：从生产页 / 瓶颈页“规划”直达；下拉换目标；产量填“abc”给原因“{badText}”；填 20 重算；“换配方”把合金从 {beforeRecipe} 换成 {afterRecipe}；" +
                    $"“固定到顶栏作为目标”写入目标 {tp?.TargetPerMinute}（{panel.MessageText}）");
                Click(panel.TabButton(StatsTab.Flow));
                bool flowTab = panel.CurrentTab == StatsTab.Flow && panel.FindRow("电力（每个电网") >= 0 && panel.FindRow("传送带") >= 0 && panel.FindRow("管线") >= 0
                               && !GameText.ContainsMarker(Rows(panel) + panel.FooterText);
                Expect(flowTab, $"I4b “物流与能源”页签：电力 / 传送带 / 管线三段（“{panel.RowText(1)}”）");
                // 战斗页仍可用（FG2-FW-04）。
                Click(panel.TabButton(StatsTab.Combat));
                bool combat = panel.SectionText == "战斗 · 反应伤害归因" && panel.EmptyText.Contains("还没有远征或突袭");
                Expect(combat, "I5 “战斗”页签：反应伤害归因（筛选与空状态）照常");
                StatsPanelUIToolkit.Close();
                foreach (StatsTab tab in new[] { StatsTab.Production, StatsTab.Planner, StatsTab.Flow })
                {
                    string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "StatsPanel.uxml", "StatsPanelWindow", stressFill: true, prepare: r =>
                    {
                        var probeGo = new GameObject("__probe_stats") { hideFlags = HideFlags.HideAndDontSave };
                        StatsPanelUIToolkit p = probeGo.AddComponent<StatsPanelUIToolkit>();
                        p.BindView(r.panel.visualTree);
                        p.SelectTab(tab);
                        Object.DestroyImmediate(probeGo);
                        r.panel.visualTree.Q<VisualElement>("StatsPanelRoot")?.RemoveFromClassList("uk-hidden");
                    });
                    Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), $"I6 统计面板布局探针（{tab} 页，四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n')[0]);
                }
                string probeAll = UiToolkitLayoutProbe.Probe(UiKitFolder + "StatsPanel.uxml", "StatsPanelWindow", stressFill: true, prepare: r =>
                {
                    var probeGo = new GameObject("__probe_stats_all") { hideFlags = HideFlags.HideAndDontSave };
                    StatsPanelUIToolkit p = probeGo.AddComponent<StatsPanelUIToolkit>();
                    p.BindView(r.panel.visualTree);
                    p.SelectTab(StatsTab.Production);
                    p.SetShowAllItems(true);
                    Object.DestroyImmediate(probeGo);
                    r.panel.visualTree.Q<VisualElement>("StatsPanelRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probeAll.Contains("PASS") && !probeAll.Contains("FAIL"), "I6 统计面板布局探针（生产页“全部物品”+ 搜索框，四种分辨率 + 超长文字 + USS 体检）：" + (probeAll.Contains("FAIL") ? probeAll : probeAll.Split('\n')[0]));
            }
            finally
            {
                StatsPanelUIToolkit.Close();
                ProductionPanelUIToolkit.Close();
                StatsPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                StatsPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(pgo);
            }
        }

        // ── J 资源顶栏（真 UXML）──────────────────────────────────────────────────────

        private static void CheckTopBar()
        {
            CampaignState s = NewWorld(8171, scrap: 100);
            BuildingRecord f = Place(s, "refinery_furnace");
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 10000 } };
            Run(120f, () => Drain(s, f));
            ResourcePins.TryPin(s, "alloy", 5f, out _); // 目标 5/分：满速 10/分 → 达标
            ResourcePins.TryPin(s, "precision_part", 3f, out _); // 没有产量 → 未达标
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            VisualElement sroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "StatsPanel.uxml", out GameObject sgo);
            WorldBarHudUIToolkit.ResourceClock = () => _fakeNow;
            StatsPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                WorldBarHudUIToolkit bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                StatsPanelUIToolkit stats = sgo.AddComponent<StatsPanelUIToolkit>();
                stats.BindView(sroot);
                bar.RefreshResources(s, force: true);
                int iScrap = bar.FindResourceChip("废料");
                int iAlloy = bar.FindResourceChip("合金");
                int iPp = bar.FindResourceChip("精密零件");
                string all = string.Join(" | ", Enumerable.Range(0, bar.ResourceChipCount).Select(bar.ResourceChipText));
                bool chips = iScrap >= 0 && iAlloy >= 0 && iPp >= 0 && bar.ResourceChipText(iAlloy).Contains("达标") && !bar.ResourceChipText(iAlloy).Contains("未达标")
                             && bar.ResourceChipText(iPp).Contains("未达标") && bar.FindResourceChip("+ 固定物品") >= 0 && bar.FindResourceChip("技术数据") >= 0
                             && bar.FindResourceChip("研究点") >= 0 && bar.FindResourceChip("暴露") >= 0 && (bar.FindResourceChip("电网") >= 0 || bar.FindResourceChip("电力") >= 0)
                             && bar.ResourceChipText(iScrap).Contains(s.Scrap.ToString(CultureInfo.InvariantCulture)) && bar.ResourceChipTip(iAlloy).Contains("右键取消固定")
                             && !GameText.ContainsMarker(all);
                // 左键：打开统计面板生产页并选中这个物品。
                Click(bar.ResourceChip(iAlloy));
                bool opened = StatsPanelUIToolkit.IsOpen && stats.CurrentTab == StatsTab.Production && stats.SelectedItemId == "alloy";
                StatsPanelUIToolkit.Close();
                // 右键：取消固定（B02），顶栏立刻少一格。
                int before = bar.ResourceChipCount;
                bar.RightClickResourceChip(iPp);
                bar.RefreshResources(s);
                bool unpinned = !ResourcePins.IsPinned(s, "precision_part") && bar.ResourceChipCount == before - 1 && bar.FindResourceChip("精密零件") < 0;
                // J2（审查修复 FGR-ECO-051“任意物品”）：顶栏“+ 固定物品”→ 统计面板“全部物品”→ 搜索一个从没有收支、也不能生产的物品 → 行内“固定”→ 顶栏多出这一格。
                List<ItemDef> plannable = ProductionPlanner.Targets();
                bool Quiet(ItemDef d) => ProductionStats.RateOf(s, ProductionStats.TierCount - 1, d).Produced == 0 && ProductionStats.RateOf(s, ProductionStats.TierCount - 1, d).Consumed == 0;
                ItemDef odd = ItemCatalog.Items.FirstOrDefault(d => d.Id == "supercomputer_core" && !plannable.Contains(d) && Quiet(d))
                              ?? ItemCatalog.Items.FirstOrDefault(d => !plannable.Contains(d) && !ResourcePins.IsPinned(s, d.Id) && Quiet(d));
                Click(bar.ResourceChip(bar.FindResourceChip("+ 固定物品")));
                bool allOpen = StatsPanelUIToolkit.IsOpen && stats.CurrentTab == StatsTab.Production && stats.ShowAllItems && stats.ItemSearchVisible
                               && stats.VisibleRowCount == ItemCatalog.Items.Count + 1;
                stats.ItemSearchField.value = odd?.Name ?? "?";
                int oddRow = odd == null ? -1 : stats.FindRow(odd.Name + "：库存");
                bool filtered = oddRow >= 0 && stats.VisibleRowCount < ItemCatalog.Items.Count + 1 && stats.RowButtonVisible(oddRow, 0) && !stats.RowButtonVisible(oddRow, 1);
                string oddText = stats.RowText(oddRow);
                if (oddRow >= 0)
                {
                    Click(stats.RowButton(oddRow, 0));
                }
                bool pinnedOdd = odd != null && ResourcePins.IsPinned(s, odd.Id) && stats.MessageText.Contains("已固定");
                bar.RefreshResources(s, force: true);
                bool onBar = odd != null && bar.FindResourceChip(odd.Name) >= 0;
                stats.ItemSearchField.value = "zz不存在的物品zz";
                bool none = stats.FindRow("没有名字里含") >= 0;
                stats.ItemSearchField.value = string.Empty;
                Click(stats.AllItemsButton);
                bool backToActive = !stats.ShowAllItems && !stats.ItemSearchVisible;
                StatsPanelUIToolkit.Close();
                Expect(odd != null && allOpen && filtered && pinnedOdd && onBar && none && backToActive,
                    $"J2 任意物品固定（FGR-ECO-051）：顶栏“+ 固定物品”打开“全部物品”（{ItemCatalog.Items.Count} 种 + 标题）；搜索“{odd?.Name}”（从没有收支、不能生产）→“{oddText}”→ 固定 → 顶栏出现该格；" +
                    $"搜不到时写明；“只看有收支的”切回（{allOpen}/{filtered}/{pinnedOdd}/{onBar}/{none}/{backToActive}）");
                // 节流：没有变化时 refresh_seconds 内不重建；时间到了才重读。
                int rb = bar.ResourceRebuilds;
                for (int i = 0; i < 30; i++)
                {
                    bar.RefreshResources(s);
                }
                bool throttled = bar.ResourceRebuilds == rb;
                _fakeNow += 1;
                bar.RefreshResources(s);
                bool reread = bar.ResourceRebuilds == rb + 1;
                // 英文：同一套格子换成英文。
                GameSettings.SetLanguage(GameLanguage.En);
                bar.RefreshResources(s);
                bool en = bar.FindResourceChip("Tech data") >= 0 && bar.FindResourceChip("Exposure") >= 0;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string wprobe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", "WorldBar");
                Expect(chips && opened && unpinned && throttled && reread && en && !wprobe.Contains("FAIL"),
                    $"J 资源顶栏（FGR-ECO-051，FGU-06）：“{all}”——固定物品带库存与产量、目标达标 / 未达标用文字写明；“+ 固定物品”、电网、技术数据、研究点、暴露；" +
                    $"左键打开统计生产页并选中、右键取消固定；{GridContent.Tuning("eco.topbar.refresh_seconds")} 秒节流（节流 {throttled}，到点 {reread}）；英文 {en}；布局探针：{wprobe.Split('\n')[0]}" +
                    $"（{chips}/{opened}/{unpinned}）");
            }
            finally
            {
                StatsPanelUIToolkit.Close();
                StatsPanelUIToolkit.InWorldOverrideForTests = false;
                WorldBarHudUIToolkit.ResourceClock = () => Time.realtimeSinceStartupAsDouble;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(sgo);
            }
        }

        // ── K 真文件存读档 ─────────────────────────────────────────────────────────────

        private static bool LayScenario(CampaignState s)
        {
            BuildingRecord f = Place(s, "refinery_furnace");
            BuildingRecord bench = Place(s, "electronics_bench");
            if (f == null || bench == null)
            {
                return false;
            }
            ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
            ProductionService.TrySetRecipe(s, bench.BuildingId, "electronic", out _);
            P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
            P(s, bench).Rec.In = new[] { new ItemStackRecord { ItemId = "alloy", Amount = 1 } };
            ResourcePins.TryPin(s, "alloy", 8f, out _);
            // 审查修复（FG04 §6 统计存档）：油井泵 → 两格管线 → 废液池（布局同 FgProductionSelfCheck E2），K2 核对读档后废液池的销毁量不丢。
            GridCell? o = FgProductionSelfCheck.FindArea(s, 9, 5);
            if (o == null)
            {
                return false;
            }
            GridCell well = FgProductionSelfCheck.At(o.Value, 2, 2);
            FgProductionSelfCheck.SetTerrain(s, well, "oil");
            bool laid = PipeNetworkService.TryPlace(s, well, PipePieceKind.Pump, 0, 0).Ok
                        && PipeNetworkService.TryPlace(s, FgProductionSelfCheck.At(o.Value, 3, 2), PipePieceKind.Pipe, 0, 0).Ok
                        && PipeNetworkService.TryPlace(s, FgProductionSelfCheck.At(o.Value, 4, 2), PipePieceKind.Pipe, 0, 0).Ok;
            BuildingRecord pond = FgProductionSelfCheck.Built(s, "waste_pond", "kpond", FgProductionSelfCheck.At(o.Value, 6, 2));
            FgProductionSelfCheck.PowerUp(s);
            return laid && pond != null;
        }

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.Stats.Production));
            foreach (ProductionService.Producer p in ProductionService.All.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                sb.Append('|').Append(p.Id).Append(':').Append(p.Rec.Completed);
                foreach (ProducerStatBucket b in p.Rec.Stats ?? Array.Empty<ProducerStatBucket>())
                {
                    if (b?.Starve != null && b.Starve.Length > 0)
                    {
                        sb.Append(';').Append(b.Index).Append('=').Append(string.Join(",", b.Starve.Select(x => x.ItemId + "x" + x.Amount)));
                    }
                }
            }
            return sb.ToString();
        }

        private static void DrainAll(CampaignState s)
        {
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                p.Rec.Out = Array.Empty<ItemStackRecord>();
            }
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8181, scrap: 100);
            if (!LayScenario(s))
            {
                Fail("K 放不下场景");
                return;
            }
            Run(11f * 60f, () => DrainAll(s));
            string before = Snapshot(s);
            bool rich = s.Stats.Production.Deficits.Any(d => d.ItemId == ItemCatalog.ScrapId && d.Warned) && s.Stats.Production.Pins.Any(p => p.ItemId == "alloy" && p.TargetPerMinute > 0)
                        && before.Contains("rare_earth_orex") && ProductionStats.RateOf(s, 1, Def("crude")).Consumed > 0; // 废液池在销毁原油
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Run(60f, () => DrainAll(s));
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                Run(60f, () => DrainAll(rr.State));
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            Expect(save.Success && rich && loaded1 == before,
                "K1 真文件存读档：四个时间窗口的统计桶、持续赤字（已警告）、顶栏固定与目标、每座建筑的缺料桶写进存档，读档后逐字段一致" + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "K2 读档后接着跑 60 游戏秒（生产、记账、赤字检查、缺料累计、废液池按存档基准对账的销毁量），与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            // 旧存档（没有生产统计域）：读进来补空域，从读档那一刻开始统计，默认固定废料。
            var old = new CampaignState();
            old.Stats = new StatsState { Production = null };
            CampaignFgStateDomains.EnsureAll(old);
            Expect(old.Stats.Production != null && old.Stats.Production.Tiers.Length == 0 && old.Stats.Production.StartTick == -1 && old.Stats.Production.Pins.Length == 0,
                "K3 旧存档没有生产统计域：补空域（不崩溃、不伪造历史数据），第一次记账时才开始统计");
        }

        // ── L / M 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 100);
            pausedHeld = true;
            if (!LayScenario(s))
            {
                return "放不下";
            }
            Run(30f, () => DrainAll(s));
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                    DrainAll(s);
                }
                pausedHeld = Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 90;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 500)
            {
                WorldSimulation.Frame(1f / 60f, target);
                DrainAll(s);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(8191, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下",
                "L 暂停中（120 帧）统计桶、缺料累计、赤字都不动；0.5x / 1x / 2x / 3x 跑同样的 90 游戏秒，四个窗口的统计桶与缺料桶逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(8192, true, 1f, false, out _);
            string unseen = RunScenario(8192, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "M 同一组生产与统计在观察与不观察家园时跑 90 游戏秒逐字段一致（FGR-BASE-021：家园后台结果与观察一致）"
                                                     + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── N 性能与“不每帧遍历” ─────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(8199, scrap: 100);
            var furnaces = new List<BuildingRecord>();
            for (int i = 0; i < 60; i++)
            {
                BuildingRecord f = Place(s, i % 3 == 0 ? "electronics_bench" : "refinery_furnace");
                if (f == null)
                {
                    break;
                }
                if (f.BuildingTypeId == "refinery_furnace")
                {
                    ProductionService.TrySetRecipe(s, f.BuildingId, "alloy_scrap", out _);
                    P(s, f).Rec.In = new[] { new ItemStackRecord { ItemId = ItemCatalog.ScrapId, Amount = 100000 } };
                }
                else
                {
                    ProductionService.TrySetRecipe(s, f.BuildingId, "electronic", out _); // 缺料：每步记缺料
                }
                furnaces.Add(f);
            }
            for (int g = 0; g < 40 && HomeValleyPowerGrid.GetSummary(s).TotalDemand > HomeValleyPowerGrid.GetSummary(s).TotalSupply; g++)
            {
                if (Place(s, HomeValleyLayout.BuildingTypeGenerator2) == null)
                {
                    break;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            int poweredCount = furnaces.Count(b => Powered(s, b));
            for (int i = 0; i < ResourcePins.MaxPins; i++)
            {
                ResourcePins.TryPin(s, ItemCatalog.Items[i].Id, 5f, out _);
            }
            // 记账：60 座生产建筑跑 3 游戏分钟，统计记账的平均耗时；生产步（含缺料累计）最大耗时。
            long rc0 = ProductionStats.RecordCount;
            double rm0 = ProductionStats.RecordMs;
            long scans0 = ProductionStats.BuildingScans;
            Run(10f, () => DrainAll(s)); // 预热（首次调用的 JIT 不算进生产步最大耗时）
            ProductionService.ResetStepStats();
            Run(170f, () => DrainAll(s));
            long records = ProductionStats.RecordCount - rc0;
            double perRecordUs = (ProductionStats.RecordMs - rm0) * 1000.0 / Math.Max(1, records);
            double step = ProductionService.MaxStepMs;
            bool noScan = ProductionStats.BuildingScans == scans0;
            // 查询：面板 / 顶栏刷新一次的耗时（10 小时窗口的全部物品、瓶颈、效率、规划、顶栏 8 格）。
            var rates = new List<ProductionStats.ItemRate>();
            var bns = new List<ProductionStats.Bottleneck>();
            var effs = new List<ProductionStats.BuildingEfficiency>();
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < 20; r++)
            {
                ProductionStats.CollectRates(s, 3, rates);
            }
            double ratesMs = sw.Elapsed.TotalMilliseconds / 20.0;
            sw.Restart();
            for (int r = 0; r < 20; r++)
            {
                ProductionStats.CollectBottlenecks(s, bns);
                ProductionStats.CollectEfficiencies(s, effs);
            }
            double scanMs = sw.Elapsed.TotalMilliseconds / 20.0;
            sw.Restart();
            for (int r = 0; r < 20; r++)
            {
                ProductionPlanner.Plan(Def("megastructure_part") ?? Def("precision_part"), 10f);
                ProductionPlanner.Plan(Def("precision_part"), 10f);
            }
            double planMs = sw.Elapsed.TotalMilliseconds / 40.0;
            sw.Restart();
            for (int r = 0; r < 20; r++)
            {
                foreach (PinnedItemRecord p in ResourcePins.Pins(s))
                {
                    ResourcePins.View(s, p);
                }
            }
            double barMs = sw.Elapsed.TotalMilliseconds / 20.0;
            var flow = new List<(string Text, string Cls)>();
            sw.Restart();
            for (int r = 0; r < 20; r++)
            {
                FlowEnergyStats.Build(s, flow);
            }
            double flowMs = sw.Elapsed.TotalMilliseconds / 20.0;
            Expect(furnaces.Count >= 30 && poweredCount >= 30 && records > 100 && noScan,
                $"N1 场景：{furnaces.Count} 座生产建筑（{poweredCount} 座有电；2/3 满料、1/3 缺料）跑 3 游戏分钟，记账 {records} 次；期间统计从不逐建筑扫描（扫描只在打开面板查询时发生）");
            string perf = $"记账每次 {perRecordUs:F2} µs；生产步（含缺料累计）最大 {step:F3} ms；面板查询：10 小时窗口全部物品 {ratesMs:F3} ms、瓶颈 + 效率 {scanMs:F3} ms、规划一次 {planMs:F3} ms；顶栏 8 格 {barMs:F3} ms；物流与能源页 {flowMs:F3} ms";
            PerfGate.Expect(true,
                $"N2 性能（Editor batchmode；真机 HybridCLR 另测 FG15-SYS-02）：{perf}（阈值：记账 5 µs、生产步 2 ms、各项查询 2 ms、顶栏 0.5 ms）",
                new[]
                {
                    PerfGate.Le(perRecordUs, 5.0, "记账每次 µs"), PerfGate.Le(step, 2.0, "生产步最大 ms"), PerfGate.Le(ratesMs, 2.0, "全部物品查询 ms"),
                    PerfGate.Le(scanMs, 2.0, "瓶颈 + 效率 ms"), PerfGate.Le(planMs, 2.0, "规划 ms"), PerfGate.Le(barMs, 0.5, "顶栏 ms"), PerfGate.Le(flowMs, 2.0, "物流与能源页 ms"),
                }, Expect, Line);
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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
                UiConfirmDialog.DiscardAll();
                UiEscapeStack.Clear();
                InputRouter.SetBuildMode(false);
                InputRouter.BuildDragActive = false;
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                HomeValleyPowerGrid.OverrideNodesForTests(null);
                PowerEnvironment.ResetForTests();
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
