using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
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
using GameLogic.UI.Analysis;
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
    /// FG5-RND-02 解析台 2.0 的自动验收（FG05 FGR-RND-020～024；FG13 FGU-23；FGT-RND-003；负向“解析中途断电”“队列满时传送带堵塞”）。
    /// 全部起真实系统：真实家园（世界模拟、电网、传送带内核与端口、仓库）、真实解析台队列、真文件存读档、真 UXML 解析台面板。
    /// A 数据（两张新表与源数据逐字段、敌方残骸物品、入口角色、收货集合、调参、文本中英、钩子与图鉴）；
    /// B FGT-RND-003（三类物品首次 / 重复：未解析模块解锁蓝图 + 技术数据 → 只给技术数据；加密固件变成已破解的固件芯片、不消耗；固件库里的未破解芯片破解后芯片不动；
    ///   数据核心技术数据 + 资料 → 只给技术数据；身份不明的按重复、加密固件原样退回）；C 预览（第一次遇到显示“？？”、已知直接写结果）；
    /// D 取消原样退回（排队中 / 解析中，物品与身份都回去；芯片不动；已结束的不能取消）；E 队列 8 项（第 9 项被拒、物品留在仓库）；
    /// F 传送带入口（敌方物品逐件入队、取最早的身份；队列满时带停下并写明原因、空出一项后自动接着收；非敌方物品被拒；残骸进缓存）；
    /// G 负向：解析中途断电（暂停、进度保留、来电后继续、总时长不变）、禁用、解析台被毁（物品退回）；H 残骸（队列空闲时逐件处理、队列来活让路、缺电停住、送入按钮）；
    /// I 状态与空闲提示（面板 / 建筑状态）；J 真文件存读档（身份清单、队列与进度、残骸、资料、已破解状态与芯片）；K 暂停与 0.5x～3x；L 观察 / 不观察一致；
    /// M 读档对账（已移除内容）；N 物资分布（解析台 / 区域任务物）；O 引导钩子与图鉴；P 面板（真 UXML）；Q 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgAnalysisBenchSelfCheck）。
    /// </summary>
    public static class FgAnalysisBenchSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string AnalysisUxml = "Assets/GameRes/Raw/UI/Analysis/AnalysisPanel.uxml";
        private const string AnalysisRowUxml = "Assets/GameRes/Raw/UI/Analysis/templates/AnalysisQueueRow.uxml";
        private const int Slot = 6;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 解析台 2.0")]
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
            Line("\n[解析台 2.0] 队列 8 项、三类敌方物品首次 / 重复、加密固件不消耗、结果预览、取消退回、传送带入口、残骸（FG5-RND-02）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fganalysis-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                AnalysisCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                HomeValleyAnalysis.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；解析台队列与面板在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），传送带逐格在 AOT 内核；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFirstAndRepeat);
                Step(CheckPreview);
                Step(CheckCancel);
                Step(CheckCapacity);
                Step(CheckBeltIntake);
                Step(CheckBeltRejects);
                Step(CheckPowerLoss);
                Step(CheckDisabledAndDestroyed);
                Step(CheckChipConsumedMidQueue);
                Step(CheckWrecks);
                Step(CheckStatus);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckRemovedContent);
                Step(CheckDistribution);
                Step(CheckHookAndCodex);
                Step(CheckPanel);
                Step(CheckRowIconNoLeak);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"解析台自检抛异常：{e}");
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
                SaveContentReconciler.OverrideAnalysisLiveForTests(null);
                HomeValleyAnalysis.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
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
            Line($"  · [解析台 2.0] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            HomeValleyAnalysis.ResetSessionState();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            EnsureWarehouse(s);
            BuildingRecord bench = HomeValleyAnalysis.FindBench(s);
            if (bench != null)
            {
                bench.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：确保有一座运转中的仓库（敌方物品先进仓库；仓库的施工与维修由 FG3-LOG-02 / FG4-ECO-05 覆盖）。</summary>
        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "ana_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
        }

        private static BuildingRecord Bench(CampaignState s) => HomeValleyAnalysis.FindBench(s);

        private static ItemDef Def(string id) => ItemCatalog.Find(id);

        private static AnalysisKindDef Kind(string id)
        {
            AnalysisCatalog.TryGetKind(id, out AnalysisKindDef d);
            return d;
        }

        private static int Stock(CampaignState s, string id) => HomeInventory.Stock(s, Def(id));

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static AnalysisQueueItemRecord Q(CampaignState s, string id) => HomeValleyAnalysis.Find(s, id);

        private static bool RunToEnd(CampaignState s, string queueId, int maxSeconds = 120) =>
            StepUntil(() => Q(s, queueId) != null && !HomeValleyAnalysis.IsActiveEntry(Q(s, queueId)), maxSeconds);

        /// <summary>一件还没解锁的组件 / 模块（按 ID 排序取第 <paramref name="skip"/> 个；不依赖固定内容）。</summary>
        private static string LockedModule(CampaignState s, int skip = 0) =>
            MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Where(id => !FirmwareKinds.IsFirmware(id) && !MechanicalContentUnlock.IsUnlocked(s, id))
                .Skip(skip).FirstOrDefault();

        /// <summary>一枚还没破解的敌方加密固件（按 ID 排序取第 <paramref name="skip"/> 个）。</summary>
        private static string RawFirmware(CampaignState s, int skip = 0) =>
            MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Where(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.IsRaw(s, id))
                .Skip(skip).FirstOrDefault();

        private static int ChipCount(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static string Snapshot(CampaignState s)
        {
            AnalysisBenchState a = s.Research.Analysis;
            var sb = new StringBuilder();
            sb.Append("tech=").Append(s.TechData)
                .Append(" unlocked=").Append(string.Join(",", (s.UnlockedContentIds ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)))
                .Append(" q=").Append(string.Join(";", (s.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>()).OrderBy(q => q.QueueItemId, StringComparer.Ordinal)
                    .Select(q => $"{q.QueueItemId}|{q.Source}|{q.ItemId}|{q.TargetId}|{q.TagId}|{q.State}|{q.Progress.ToString("F3", CultureInfo.InvariantCulture)}|{q.Duration}|{q.BlockedReason}|{q.FirstTime}|{q.TechGained}")))
                .Append(" tags=").Append(string.Join(";", a.Tags.Select(t => t.TagId + "|" + t.ItemId + "|" + t.TargetId)))
                .Append(" wreck=").Append(a.WreckBuffer).Append('|').Append(a.WreckProgress.ToString("F3", CultureInfo.InvariantCulture)).Append('|').Append(a.WrecksProcessed)
                .Append(" lore=").Append(string.Join(",", a.LoreRead))
                .Append(" stat=").Append(a.TechFromAnalysis).Append('|').Append(a.TechFromWrecks).Append('|').Append(a.Completed).Append('|').Append(a.NextSerial)
                .Append(" stock=").Append(string.Join(",", AnalysisCatalog.AllKinds.Select(k => k.ItemId + ":" + Stock(s, k.ItemId))))
                .Append(" chips=").Append(string.Join(",", (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Select(c => c.CardDefId + ":" + c.State).OrderBy(x => x, StringComparer.Ordinal)));
            return sb.ToString();
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = FgProductionSelfCheck.RunPython(FgProductionSelfCheck.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("AK\t") || l.StartsWith("AL\t")).ToArray();
            var rt = new List<string>();
            rt.AddRange(t.TbAnalysisKind.DataList.Select(r => string.Join("\t", "AK", r.Id, FgProductionSelfCheck.Fl(r.Seconds), r.TechFirst, r.TechRepeat, r.Consumed, r.Queued, r.SortOrder)));
            rt.AddRange(t.TbAnalysisLore.DataList.Select(r => string.Join("\t", "AL", r.Id, r.TitleKey, r.BodyKey, r.SortOrder)));
            Expect(code == 0 && src.Length == rt.Count && src.SequenceEqual(rt) && rt.Count >= 6,
                $"A1 fg.TbAnalysisKind / fg.TbAnalysisLore 与源数据 fgdata_analysis.py 逐字段一致（{src.Length} 行）" + (code == 0 ? (src.SequenceEqual(rt) ? string.Empty : $"\n源：{string.Join(" / ", src)}\n表：{string.Join(" / ", rt)}") : "：" + output));

            bool kinds = AnalysisCatalog.Problems.Count == 0 && AnalysisCatalog.AllKinds.Count == 4
                         && !Kind(AnalysisCatalog.EncryptedFirmwareId).Consumed && Kind(AnalysisCatalog.UnparsedModuleId).Consumed && Kind(AnalysisCatalog.DataCoreId).Consumed
                         && !Kind(AnalysisCatalog.WreckId).Queued && Kind(AnalysisCatalog.UnparsedModuleId).Queued
                         && AnalysisCatalog.QueueCapacity == 8;
            ItemDef wreck = Def(AnalysisCatalog.WreckId);
            bool item = wreck != null && wreck.Form == ItemForm.Solid && wreck.BeltId == 18 && wreck.Tier == "expedition" && wreck.RecycleScrap > 0 && !GameText.ContainsMarker(wreck.Name);
            ulong mask = AnalysisCatalog.BeltMask;
            bool maskOk = AnalysisCatalog.AllKinds.All(k => ((mask >> k.Item.BeltId) & 1UL) != 0) && ((mask >> BeltItems.ScrapId) & 1UL) == 0
                          && BeltNetworkService.ReadConfig().AcceptSet2Mask == mask && BeltNetworkService.ReadConfig().AcceptSetMask == AssemblyMaterials.BeltMask;
            GameConfig.fg.BuildingPort port = GridContent.PortsOf(HomeValleyLayout.BuildingTypeAnalysisBench).FirstOrDefault(p => p.Kind == "in");
            Expect(kinds && item && maskOk && port != null && port.Role == "prod",
                $"A2 三类敌方物品 + 残骸（加密固件不消耗、残骸在队列空闲时处理）、队列初值 8（FGR-RND-020）；新物品“敌方残骸”（固体、传送带编号 18、远征物、可回收成废料 {wreck?.RecycleScrap}）；" +
                $"解析台输入口角色 = prod（DEBT-FG3LOG03-01 解析台部分），传送带内核第二个收货集合 = 这 4 种（不含废料；装配站的集合不变）" +
                (AnalysisCatalog.Problems.Count > 0 ? "；问题：" + string.Join("；", AnalysisCatalog.Problems) : string.Empty));

            string[] keys =
            {
                "analysis.panel.title", "analysis.panel.held_title", "analysis.panel.queue_title", "analysis.panel.enqueue", "analysis.panel.held_empty", "analysis.status.idle",
                "analysis.status.full", "analysis.preview.unknown", "analysis.preview.first_blueprint", "analysis.preview.first_firmware", "analysis.preview.first_lore",
                "analysis.result.module_first", "analysis.result.firmware", "analysis.result.chip", "analysis.result.core_first", "analysis.reason.queue_full",
                "analysis.wreck.line", "analysis.lore.line", "analysis.port.accept", "item.dist.analysis", "item.dist.quest", "save.notice.analysis_removed",
                "codex.research.analysis.title", "codex.research.analysis.body", "bs.reason.idle_analysis", "bs.reason.wreck_analysis", "bs.reason.analysis_no_power",
                "item.enemy_wreck.name", "item.enemy_wreck.source",
                // 修复轮：原硬编码中文的字幕与 Demo 任务物名改走文本键；新的受阻原因
                "analysis.feedback.bench_destroyed", "analysis.feedback.quest_missing", "analysis.result.quest_first", "analysis.result.quest_repeat",
                "analysis.blocked.chip_missing", "analysis.blocked.content_removed",
            };
            var missing = new List<string>();
            foreach (HomeValleyAnalysis.YieldInfo yi in HomeValleyAnalysis.YieldTable.Values)
            {
                if (string.IsNullOrEmpty(yi.NameKey) || !GameText.TryGet(yi.NameKey, GameLanguage.ZhCn, out string yz) || string.IsNullOrWhiteSpace(yz)
                    || !GameText.TryGet(yi.NameKey, GameLanguage.En, out string ye) || string.IsNullOrWhiteSpace(ye))
                {
                    missing.Add(yi.NameKey ?? "(Demo 任务物名)");
                }
            }
            foreach (string k in keys)
            {
                if (!GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrWhiteSpace(zh) || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrWhiteSpace(en))
                {
                    missing.Add(k);
                }
            }
            foreach (AnalysisLoreDef l in AnalysisCatalog.Lore)
            {
                if (!GameText.Has(l.TitleKey) || !GameText.Has(l.BodyKey))
                {
                    missing.Add(l.Id);
                }
            }
            bool idleText = GameText.Get("analysis.status.idle") == "解析台空闲：没有待解析的物品" && GameText.Get("bs.reason.idle_analysis") == "解析台空闲：没有待解析的物品";
            bool unknown = GameText.Get("analysis.preview.unknown") == "？？" && GameText.Get("analysis.preview.first_blueprint").Contains("首次解析将解锁新蓝图");
            bool hook = GuidanceHooks.Known.Contains(GuidanceHooks.AnalysisFirstEnemyItem)
                        && t.TbCodexEntry.DataList.Any(e => e.Id == "codex.research.analysis" && e.Hooks.Contains(GuidanceHooks.AnalysisFirstEnemyItem));
            bool removedKind = t.TbRemovedContent.DataList.All(r => r.Kind == "primitive_chip" || r.Kind == "building" || r.Kind == "analysis");
            Expect(missing.Count == 0 && idleText && unknown && hook && removedKind,
                "A3 新文本中英两套（面板 / 状态 / 预览 / 结果 / 原因 / 残骸 / 资料 / 端口 / 分布 / 读档通知 / 图鉴）；空闲提示原文“解析台空闲：没有待解析的物品”（FG05 第 4 节）；未知显示“？？”走文本键；" +
                "“第一次拿到敌方物品”钩子登记、图鉴“解析台与敌方物品”以它解锁；已移除内容表多了 analysis 类别" + (missing.Count > 0 ? "；缺：" + string.Join("、", missing) : string.Empty));
        }

        // ── B FGT-RND-003 ────────────────────────────────────────────────────────

        private static void CheckFirstAndRepeat()
        {
            CampaignState s = NewWorld(7101);
            BuildingRecord bench = Bench(s);
            bool powered = bench != null && HomeValleyAnalysis.BenchWorking(bench);
            Expect(powered, $"B0 测试准备：开局的解析台运转、有电（{bench?.ConstructionState} / {bench?.PowerState}）");
            string mod = LockedModule(s);
            string fw = RawFirmware(s);
            string fw2 = RawFirmware(s, 1);
            string lore = AnalysisCatalog.Lore.FirstOrDefault()?.Id;
            if (mod == null || fw == null || fw2 == null || lore == null)
            {
                Fail($"测试准备：找不到未解锁的模块 / 未破解的敌方固件 / 资料（{mod} / {fw} / {fw2} / {lore}）");
                return;
            }
            AnalysisKindDef km = Kind(AnalysisCatalog.UnparsedModuleId);
            AnalysisKindDef kf = Kind(AnalysisCatalog.EncryptedFirmwareId);
            AnalysisKindDef kc = Kind(AnalysisCatalog.DataCoreId);

            // 未解析模块：首次 / 重复
            int got = HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "test", 2);
            int stock0 = Stock(s, AnalysisCatalog.UnparsedModuleId);
            int tech0 = s.TechData;
            HomeValleyAnalysis.AnalysisOpResult r1 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            bool took = r1.Success && Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 - 1 && s.Research.Analysis.Tags.Count(t => t.ItemId == AnalysisCatalog.UnparsedModuleId) == 1;
            bool done1 = RunToEnd(s, r1.QueueItemId);
            AnalysisQueueItemRecord q1 = Q(s, r1.QueueItemId);
            bool first = done1 && q1.State == AnalysisQueueState.Completed && q1.FirstTime && MechanicalContentUnlock.IsUnlocked(s, mod) && s.TechData == tech0 + km.TechFirst && q1.TechGained == km.TechFirst;
            int tech1 = s.TechData;
            HomeValleyAnalysis.AnalysisOpResult r2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            bool done2 = r2.Success && RunToEnd(s, r2.QueueItemId);
            AnalysisQueueItemRecord q2 = Q(s, r2.QueueItemId);
            bool repeat = done2 && !q2.FirstTime && s.TechData == tech1 + km.TechRepeat && Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 - 2
                          && s.UnlockedContentIds.Count(id => id == mod) == 1;
            Expect(got == 2 && took && first && repeat,
                $"B1 未解析模块（身份 {mod}）：送进解析台从仓库取走一件、取走它的身份；首次解析解锁对应蓝图 + 技术数据 +{km.TechFirst}；重复解析只给技术数据 +{km.TechRepeat}、不再解锁；两件都被消耗" +
                $"（取走 {took}，首次 {first}，重复 {repeat}，技术数据 {tech0}→{tech1}→{s.TechData}）");

            // 加密固件：首次 / 重复，都变成已破解的固件芯片，不消耗
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.EncryptedFirmwareId, fw, "test", 2);
            int chips0 = ChipCount(s, fw);
            int techF0 = s.TechData;
            HomeValleyAnalysis.AnalysisOpResult f1 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.EncryptedFirmwareId, null);
            bool fd1 = f1.Success && RunToEnd(s, f1.QueueItemId);
            bool cracked = fd1 && !FirmwareKinds.IsRaw(s, fw) && MechanicalContentUnlock.IsUnlocked(s, fw) && ProductionService.IsBurnable(s, fw) && Q(s, f1.QueueItemId).FirstTime;
            bool kept1 = ChipCount(s, fw) == chips0 + 1 && s.PrimitiveChips.Any(c => c.CardDefId == fw && c.SourceSalvageId == "analysis:" + f1.QueueItemId);
            HomeValleyAnalysis.AnalysisOpResult f2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.EncryptedFirmwareId, null);
            bool fd2 = f2.Success && RunToEnd(s, f2.QueueItemId);
            bool kept2 = fd2 && ChipCount(s, fw) == chips0 + 2 && !Q(s, f2.QueueItemId).FirstTime && Q(s, f2.QueueItemId).State == AnalysisQueueState.Completed;
            Expect(cracked && kept1 && kept2 && s.TechData == techF0 + kf.TechFirst + kf.TechRepeat,
                $"B2 加密固件（{fw}）：首次解析后固件本身变成已破解（解锁刻录蓝图：固件刻录台可以刻），这件物品变成固件库里的一枚已破解芯片——不消耗；重复解析同样变成已破解芯片" +
                $"（芯片 {chips0}→{ChipCount(s, fw)}，破解 {cracked}）");

            // 固件库里的未破解芯片：引用破解，芯片不动
            string partId = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-chip", fw2, out _);
            PrimitiveChipRecord chip = s.PrimitiveChips.FirstOrDefault(c => c.PartId == partId);
            PrimitiveChipState chipState = chip?.State ?? PrimitiveChipState.Bag;
            HomeValleyAnalysis.AnalysisOpResult c1 = HomeValleyAnalysis.TryEnqueueChip(s, partId);
            HomeValleyAnalysis.AnalysisOpResult cDup = HomeValleyAnalysis.TryEnqueueChip(s, partId);
            bool cd = c1.Success && RunToEnd(s, c1.QueueItemId);
            PrimitiveChipRecord after = s.PrimitiveChips.FirstOrDefault(c => c.PartId == partId);
            HomeValleyAnalysis.AnalysisOpResult cAgain = HomeValleyAnalysis.TryEnqueueChip(s, partId);
            Expect(partId != null && cd && !FirmwareKinds.IsRaw(s, fw2) && after != null && after.State == chipState && after.CardDefId == fw2
                   && !cDup.Success && cDup.FailureReason == "already-queued" && !cAgain.Success && cAgain.FailureReason == "already-cracked",
                $"B3 固件库里的未破解芯片（{fw2}）送去破解：同一种不能重复排队；破解后芯片原样留在原处（{chipState}）、不消耗；已破解的再送被拒（“这枚固件已经破解”）");

            // 数据核心：首次 / 重复
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, lore, "test", 2);
            int techC0 = s.TechData;
            HomeValleyAnalysis.AnalysisOpResult d1 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.DataCoreId, null);
            int intel0 = s.Research.Intel?.Records?.Length ?? 0;
            bool dd1 = d1.Success && RunToEnd(s, d1.QueueItemId) && s.Research.Analysis.LoreRead.Contains(lore) && s.TechData == techC0 + kc.TechFirst;
            // FG5-RND-05（FGR-RND-021 情报部分，DEBT-FG5RND02-03）：首次解读数据核心附带一条情报（来源写“数据核心”）；重复解读不再给。
            IntelRecord coreIntel = s.Research.Intel?.Records?.LastOrDefault();
            bool intelFirst = dd1 && (s.Research.Intel?.Records?.Length ?? 0) == intel0 + 1 && coreIntel != null && coreIntel.Source == IntelService.SourceDataCore;
            HomeValleyAnalysis.AnalysisOpResult d2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.DataCoreId, null);
            bool dd2 = d2.Success && RunToEnd(s, d2.QueueItemId) && s.Research.Analysis.LoreRead.Length == 1 && s.TechData == techC0 + kc.TechFirst + kc.TechRepeat;
            int intelAfter = s.Research.Intel?.Records?.Length ?? 0;
            Expect(dd1 && dd2 && Stock(s, AnalysisCatalog.DataCoreId) == 0 && HomeValleyAnalysis.LoreLine(s).Contains(AnalysisCatalog.Lore[0].Title),
                $"B4 数据核心（{lore}）：首次技术数据 +{kc.TechFirst} 并解读一份资料（资料栏写出标题）；重复只给技术数据 +{kc.TechRepeat}；两件都被消耗");
            Expect(intelFirst && intelAfter == intel0 + 1,
                $"B4b FG5-RND-05（FGR-RND-021 情报部分）：首次解读数据核心附带一条情报（{IntelCatalog.KindName(coreIntel?.Kind)}，来源“数据核心”），重复解读不再给（情报 {intel0} → {intelAfter}）");

            // 身份不明
            HomeInventory.Add(s, Def(AnalysisCatalog.UnparsedModuleId), 1);
            HomeInventory.Add(s, Def(AnalysisCatalog.EncryptedFirmwareId), 1);
            int techU = s.TechData;
            HomeValleyAnalysis.AnalysisOpResult u1 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            bool ud = u1.Success && RunToEnd(s, u1.QueueItemId) && s.TechData == techU + km.TechRepeat && !Q(s, u1.QueueItemId).FirstTime;
            HomeValleyAnalysis.AnalysisOpResult u2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.EncryptedFirmwareId, null);
            bool uf = u2.Success && RunToEnd(s, u2.QueueItemId) && Q(s, u2.QueueItemId).State == AnalysisQueueState.Failed
                      && Q(s, u2.QueueItemId).BlockedReason == "unidentified-firmware" && Stock(s, AnalysisCatalog.EncryptedFirmwareId) == 1;
            Expect(ud && uf, "B5 身份不明的未解析模块按重复解析（只给技术数据）；身份不明的加密固件破解不了，原样退回仓库（不让玩家因为解析失去物品）");
        }

        // ── C 预览 ──────────────────────────────────────────────────────────────

        private static void CheckPreview()
        {
            CampaignState s = NewWorld(7102);
            string mod = LockedModule(s);
            string fw = RawFirmware(s);
            string lore = AnalysisCatalog.Lore.FirstOrDefault()?.Id;
            string pm = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.UnparsedModuleId, mod);
            string pf = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.EncryptedFirmwareId, fw);
            string pc = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.DataCoreId, lore);
            bool unknown = pm.StartsWith("？？") && pm.Contains("首次解析将解锁新蓝图") && pf.StartsWith("？？") && pf.Contains("不消耗") && pc.StartsWith("？？")
                           && !HomeValleyAnalysis.IsKnown(s, AnalysisCatalog.UnparsedModuleId, mod);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "test", 1);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, lore, "test", 1);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            string inQueue = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.UnparsedModuleId, Q(s, r.QueueItemId)?.TargetId);
            RunToEnd(s, r.QueueItemId);
            HomeValleyAnalysis.AnalysisOpResult rc = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.DataCoreId, null);
            RunToEnd(s, rc.QueueItemId);
            string km = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.UnparsedModuleId, mod);
            string kc = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.DataCoreId, lore);
            string unid = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.UnparsedModuleId, string.Empty);
            string time = HomeValleyAnalysis.TimeLine(Kind(AnalysisCatalog.UnparsedModuleId).Seconds);
            bool known = !km.Contains("？？") && km.Contains("+" + Kind(AnalysisCatalog.UnparsedModuleId).TechRepeat) && !kc.Contains("？？") && kc.Contains("+" + Kind(AnalysisCatalog.DataCoreId).TechRepeat)
                         && unid.Contains("身份不明") && time.Contains("秒") && time.Contains("耗电") && inQueue.StartsWith("？？");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = HomeValleyAnalysis.Preview(s, HomeValleyAnalysis.SourceItem, AnalysisCatalog.UnparsedModuleId, LockedModule(s));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(unknown && known && en.StartsWith("??") && !GameText.ContainsMarker(pm + pf + pc + km + kc + unid + time + en),
                $"C 结果预览（FGR-RND-022）：第一次遇到的类型显示“？？”并提示“首次解析将解锁新蓝图”（模块“{pm}”，固件“{pf}”，资料“{pc}”）；已知类型直接写会得到什么（“{km}” / “{kc}”）；" +
                $"身份不明写“按重复解析”；每项写时间与耗电（“{time}”）；英文“{en}”");
        }

        // ── D 取消 ──────────────────────────────────────────────────────────────

        private static void CheckCancel()
        {
            CampaignState s = NewWorld(7103);
            string mod = LockedModule(s);
            string mod2 = LockedModule(s, 1);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "a", 1);
            Seconds(1f);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod2, "b", 1);
            string tagsBefore = string.Join(";", s.Research.Analysis.Tags.Select(t => t.TagId + "|" + t.TargetId));
            int stock0 = Stock(s, AnalysisCatalog.UnparsedModuleId);
            int tech0 = s.TechData;
            string tagA = s.Research.Analysis.Tags.First(t => t.TargetId == mod).TagId;
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, tagA);
            bool removed = r.Success && Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 - 1 && s.Research.Analysis.Tags.All(t => t.TagId != tagA) && Q(s, r.QueueItemId).TargetId == mod;
            Seconds(Kind(AnalysisCatalog.UnparsedModuleId).Seconds * 0.4f);
            bool running = Q(s, r.QueueItemId).State == AnalysisQueueState.Running && Q(s, r.QueueItemId).Progress > 0f;
            HomeValleyAnalysis.AnalysisOpResult c = HomeValleyAnalysis.TryCancel(s, r.QueueItemId);
            string tagsAfter = string.Join(";", s.Research.Analysis.Tags.Select(t => t.TagId + "|" + t.TargetId));
            bool back = c.Success && Q(s, r.QueueItemId).State == AnalysisQueueState.Cancelled && Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 && tagsAfter == tagsBefore
                        && s.TechData == tech0 && !MechanicalContentUnlock.IsUnlocked(s, mod);
            // 排队中（还没开工）的取消
            HomeValleyAnalysis.AnalysisOpResult a1 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            HomeValleyAnalysis.AnalysisOpResult a2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            WorldSimulation.StepMany(2);
            bool queued = Q(s, a2.QueueItemId).State == AnalysisQueueState.Queued;
            HomeValleyAnalysis.TryCancel(s, a2.QueueItemId);
            HomeValleyAnalysis.TryCancel(s, a1.QueueItemId);
            bool back2 = Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 && string.Join(";", s.Research.Analysis.Tags.Select(t => t.TagId + "|" + t.TargetId)) == tagsBefore;
            // 已结束的不能取消
            HomeValleyAnalysis.AnalysisOpResult a3 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            RunToEnd(s, a3.QueueItemId);
            HomeValleyAnalysis.AnalysisOpResult late = HomeValleyAnalysis.TryCancel(s, a3.QueueItemId);
            // 芯片取消：芯片不动
            string fw = RawFirmware(s);
            string part = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-cancel", fw, out _);
            int chips = ChipCount(s, fw);
            HomeValleyAnalysis.AnalysisOpResult cc = HomeValleyAnalysis.TryEnqueueChip(s, part);
            HomeValleyAnalysis.TryCancel(s, cc.QueueItemId);
            Expect(removed && running && back && queued && back2 && !late.Success && HomeValleyAnalysis.ReasonText(late.FailureReason) == GameText.Get("analysis.reason.not_cancellable")
                   && ChipCount(s, fw) == chips && FirmwareKinds.IsRaw(s, fw),
                $"D 取消原样退回（FGR-RND-023）：解析中途（进度 {Kind(AnalysisCatalog.UnparsedModuleId).Seconds * 0.4f:0.#} 秒）取消，物品回仓库、点名送的那条身份按原来的先后放回（身份清单逐条一致）、" +
                "不给技术数据不解锁；排队中的取消同样退回；已完成的不能取消（写明原因）；固件芯片的取消芯片不动、仍未破解");
        }

        // ── E 队列 8 项 ─────────────────────────────────────────────────────────

        private static void CheckCapacity()
        {
            CampaignState s = NewWorld(7104);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, string.Empty, "test", 10);
            int ok = 0;
            for (int i = 0; i < 8; i++)
            {
                ok += HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null).Success ? 1 : 0;
            }
            int stock = Stock(s, AnalysisCatalog.UnparsedModuleId);
            HomeValleyAnalysis.AnalysisOpResult ninth = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            string fw = RawFirmware(s);
            string part = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-cap", fw, out _);
            HomeValleyAnalysis.AnalysisOpResult chip = HomeValleyAnalysis.TryEnqueueChip(s, part);
            bool full = HomeValleyAnalysis.QueueFull(s) && HomeValleyAnalysis.StatusLine(s).Contains("队列已满");
            Expect(ok == 8 && !ninth.Success && ninth.FailureReason == "queue-full" && Stock(s, AnalysisCatalog.UnparsedModuleId) == stock && stock == 2
                   && !chip.Success && chip.FailureReason == "queue-full" && full && HomeValleyAnalysis.ReasonText(ninth.FailureReason).Contains("8"),
                $"E 队列 8 项（FGR-RND-020）：前 8 件入队，第 9 件与固件芯片都被拒（“{HomeValleyAnalysis.ReasonText(ninth.FailureReason)}”），物品留在仓库（{stock} 件）；状态行写“队列已满”");
        }

        // ── F 传送带入口 ─────────────────────────────────────────────────────────

        private static BeltPortService.Binding BenchIn(CampaignState s) =>
            BeltPortService.Find(Bench(s).BuildingId, GridContent.PortsOf(HomeValleyLayout.BuildingTypeAnalysisBench).First(p => p.Kind == "in").Id);

        /// <summary>从解析台入口外侧往外铺 <paramref name="len"/> 格朝向入口的传送带，返回最远那一格（来料口挂在这里）。</summary>
        private static GridCell? LayFeed(CampaignState s, int len)
        {
            BeltPortService.Binding bind = BenchIn(s);
            if (bind == null)
            {
                return null;
            }
            Vector2Int v = GridMath.DirVector(bind.Face);
            var far = new GridCell(bind.BeltCell.X + v.x * (len - 1), bind.BeltCell.Y + v.y * (len - 1));
            var dir = (BeltDir)(((int)bind.Face + 2) % 4);
            return FgProductionSelfCheck.Belts(s, far, dir, len) ? far : (GridCell?)null;
        }

        private static void CheckBeltIntake()
        {
            CampaignState s = NewWorld(7105);
            string mod = LockedModule(s);
            string mod2 = LockedModule(s, 1);
            // 两件带身份的模块先在仓库，然后被“推上传送带”（库存 -2，带上 +2），带上一共 12 件。
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "a", 1);
            Seconds(0.5f);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod2, "b", 1);
            HomeInventory.RemoveUpTo(s, Def(AnalysisCatalog.UnparsedModuleId), 2);
            GridCell? far = LayFeed(s, 4);
            if (!far.HasValue)
            {
                Fail("测试准备：解析台入口外侧铺不下 4 格传送带（地形 / 建筑挡住）");
                return;
            }
            FgProductionSelfCheck.Resync(s);
            FgProductionSelfCheck.TestSource(s, far.Value, AnalysisCatalog.UnparsedModuleId, 20);
            bool filled = StepUntil(() => HomeValleyAnalysis.ActiveCount(s) == 8, 60);
            Seconds(10f);
            AnalysisQueueItemRecord[] act = s.AnalysisQueues.Where(HomeValleyAnalysis.IsActiveEntry).OrderBy(q => q.CreatedTick).ToArray();
            bool order = act.Length == 8 && act[0].TargetId == mod && act[1].TargetId == mod2 && act.Skip(2).All(q => string.IsNullOrEmpty(q.TargetId))
                         && s.Research.Analysis.Tags.Length == 0 && act.All(q => q.Source == HomeValleyAnalysis.SourceItem);
            BeltPortService.Binding bind = BenchIn(s);
            BeltNetworkService.Kernel.TryGetPortCounts(bind.PortId, out _, out int buffered, out _);
            int onBelt = FgProductionSelfCheck.OnBelts(far.Value, (BeltDir)(((int)bind.Face + 2) % 4), 4);
            string reason = (BeltNetworkService.DescribeCell(bind.BeltCell) ?? string.Empty);
            bool jammed = onBelt > 0 && onBelt + buffered == 12 && reason.Contains("队列已满");
            // 空出一项：后面的件自动接着收
            HomeValleyAnalysis.TryCancel(s, act[7].QueueItemId);
            bool refill = StepUntil(() => HomeValleyAnalysis.ActiveCount(s) == 8, 30);
            int intake = HomeValleyAnalysis.BeltIntakeCount;
            Expect(filled && order && jammed && refill && intake >= 9,
                $"F1 传送带送进解析台入口（DEBT-FG3LOG03-01）：敌方物品逐件入队到 8 项，按先来先用取走身份（第 1、2 件是 {mod} / {mod2}，其余身份不明）；" +
                $"队列满时拒绝新物品、留在原处（带上 + 入口 {onBelt}+{buffered} 件），带停下的原因写“{reason}”；取消一项后入口自动接着收（入队累计 {intake} 件）");
        }

        private static void CheckBeltRejects()
        {
            CampaignState s = NewWorld(7106);
            GridCell? far = LayFeed(s, 3);
            if (!far.HasValue)
            {
                Fail("测试准备：解析台入口外侧铺不下 3 格传送带");
                return;
            }
            FgProductionSelfCheck.Resync(s);
            BeltPortService.Binding bind = BenchIn(s);
            FgProductionSelfCheck.TestSource(s, far.Value, ItemCatalog.ScrapId, 3);
            Seconds(10f);
            string reason = (BeltNetworkService.DescribeCell(bind.BeltCell) ?? string.Empty);
            int onBelt = FgProductionSelfCheck.OnBelts(far.Value, (BeltDir)(((int)bind.Face + 2) % 4), 3);
            bool rejected = onBelt == 3 && HomeValleyAnalysis.ActiveCount(s) == 0 && reason.Contains("解析台只收敌方物品");
            BeltNetworkService.Kernel.TryGetPortInfo(bind.PortId, out BeltPortInfo info);
            string accept = ProductionService.PortAcceptLine(s, Bench(s), false, bind.PortKey);

            CampaignState w = NewWorld(7107);
            GridCell? far2 = LayFeed(w, 3);
            FgProductionSelfCheck.Resync(w);
            int wsrc = -1;
            if (far2.HasValue)
            {
                wsrc = FgProductionSelfCheck.TestSource(w, far2.Value, AnalysisCatalog.WreckId, 5);
            }
            bool wrecks = far2.HasValue && StepUntil(() => w.Research.Analysis.WreckBuffer + w.Research.Analysis.WrecksProcessed == 5, 40) && HomeValleyAnalysis.ActiveCount(w) == 0;
            Expect(rejected && info.Accept == BeltConst.AcceptSet2 && accept.Contains("8") && wrecks,
                $"F2 非敌方物品（废料）送到解析台入口被拒：留在带上、带停下（原因“{reason}”）；端口面板写收什么（“{accept}”）；残骸由传送带送进来进残骸缓存、不占队列");

            // 读档迁移：FG3-LOG-03 起的旧存档里解析台输入口“什么都不收”，端口索引重建（与读档同一路径）时改成第二个收货集合，敌方物品能入队。
            BeltPortService.Binding wb = BenchIn(w);
            bool migrated = false;
            bool fedAfter = false;
            if (wb != null && far2.HasValue)
            {
                BeltNetworkService.Kernel.RemovePort(wsrc);
                BeltNetworkService.Kernel.SetSinkAccept(wb.PortId, BeltConst.AcceptNone);
                bool wasNone = BeltNetworkService.Kernel.TryGetPortInfo(wb.PortId, out BeltPortInfo none) && none.Accept == BeltConst.AcceptNone;
                w.Belts.PortBindings = w.Belts.PortBindings.ToArray(); // 绑定表换了引用 → 端口索引重建
                Seconds(2f);
                wb = BenchIn(w);
                migrated = wasNone && wb != null && BeltNetworkService.Kernel.TryGetPortInfo(wb.PortId, out BeltPortInfo pm) && pm.Accept == BeltConst.AcceptSet2;
                FgProductionSelfCheck.TestSource(w, far2.Value, AnalysisCatalog.UnparsedModuleId, 1);
                fedAfter = StepUntil(() => HomeValleyAnalysis.ActiveCount(w) == 1, 30);
            }
            Expect(migrated && fedAfter, $"F3 旧存档迁移：解析台输入口从“什么都不收”改为收敌方物品与残骸（端口索引重建时一次），之后传送带送来的未解析模块照常入队（迁移 {migrated} / 入队 {fedAfter}）");
        }

        // ── G 负向：断电 / 禁用 / 被毁 ───────────────────────────────────────────

        private static List<BuildingRecord> Generators(CampaignState s) =>
            s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                                         && HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId) && b.ConstructionState == BuildingConstructionState.Operational).ToList();

        private static void CheckPowerLoss()
        {
            CampaignState s = NewWorld(7108);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, LockedModule(s), "test", 1);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            float dur = Kind(AnalysisCatalog.UnparsedModuleId).Seconds;
            Seconds(dur * 0.5f);
            float p0 = Q(s, r.QueueItemId).Progress;
            List<BuildingRecord> gens = Generators(s);
            foreach (BuildingRecord g in gens)
            {
                g.ConstructionState = BuildingConstructionState.Destroyed;
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            float p1 = Q(s, r.QueueItemId).Progress;
            Seconds(20f);
            AnalysisQueueItemRecord q = Q(s, r.QueueItemId);
            BuildingStatus np = BuildingStatusService.Evaluate(s, Bench(s));
            string statusLine = HomeValleyAnalysis.StatusLine(s);
            bool paused = Bench(s).PowerState != BuildingPowerState.Powered && q.State == AnalysisQueueState.WaitingPower && q.BlockedReason == "analysis-bench-unpowered"
                          && Math.Abs(q.Progress - p1) < 1e-4f && p1 >= p0 && statusLine.Contains("缺电") && np.Kind == BuildingStatusKind.NoPower; // 电网供电不足时由通用电力状态（power.brownout，写明优先级）先报
            string diag = $"{Bench(s).PowerState}/{q.State}/{q.BlockedReason}/{q.Progress:0.###}/{p0:0.###}/{np.ReasonCode}/{statusLine}";
            foreach (BuildingRecord g in gens)
            {
                g.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            bool resumed = RunToEnd(s, r.QueueItemId, 60) && Q(s, r.QueueItemId).State == AnalysisQueueState.Completed;
            Expect(gens.Count > 0 && paused && resumed && p1 > 0f,
                $"G1 解析中途断电（FG05 第 5 节）：暂停、进度保留（{p1:0.##}/{dur:0.#} 秒，断电 20 秒不动）、队列行写“断电暂停”、状态行与建筑状态写缺电（“{np.Reason}”）；来电后接着解析并完成" +
                (paused && resumed ? string.Empty : $"（{diag}；恢复 {resumed}）"));
        }

        private static void CheckDisabledAndDestroyed()
        {
            CampaignState s = NewWorld(7109);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, LockedModule(s), "test", 2);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            Seconds(3f);
            float p0 = Q(s, r.QueueItemId).Progress;
            bool off = BuildingOps.TrySetEnabled(s, Bench(s).BuildingId, false, out string msg);
            Seconds(5f);
            AnalysisQueueItemRecord q = Q(s, r.QueueItemId);
            bool disabled = off && q.State == AnalysisQueueState.WaitingPower && q.BlockedReason == "analysis-bench-disabled" && Math.Abs(q.Progress - p0) < 0.2f;
            BuildingOps.TrySetEnabled(s, Bench(s).BuildingId, true, out _);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            bool back = Q(s, r.QueueItemId).State == AnalysisQueueState.Running;

            // 被毁（走正式摧毁入口 BuildingOps.ApplyDamage → HomeValleyPowerGrid.ApplyBuildingDestroyed，写的是“可重建虚影”Damaged）：
            // 在办的物品退回家园、身份放回，残骸缓存退回；之后进度不再推进、面板与传送带都不再收货。
            HomeValleyAnalysis.AnalysisOpResult r2 = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "test", 2);
            HomeValleyAnalysis.TrySendWrecks(s, out int wrecksSent);
            GridCell? far = LayFeed(s, 3);
            FgProductionSelfCheck.Resync(s);
            int Ground(string id) => (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == Def(id).ResourceType).Sum(g => g.Amount);
            int stock = Stock(s, AnalysisCatalog.UnparsedModuleId);
            int ground = Ground(AnalysisCatalog.UnparsedModuleId);
            int wreckHome = Stock(s, AnalysisCatalog.WreckId) + Ground(AnalysisCatalog.WreckId);
            bool killed = BuildingOps.ApplyDamage(s, Bench(s).BuildingId, BuildingOps.MaxDurability(Bench(s).BuildingTypeId) + 1f);
            WorldSimulation.StepMany(2);
            float pDead = Q(s, r.QueueItemId).Progress;
            bool ghost = Bench(s).ConstructionState == BuildingConstructionState.Damaged && !HomeValleyAnalysis.BenchWorking(Bench(s))
                         && BuildingStatusService.Evaluate(s, Bench(s)).Kind == BuildingStatusKind.Destroyed;
            bool failed = Q(s, r.QueueItemId).State == AnalysisQueueState.Failed && Q(s, r2.QueueItemId).State == AnalysisQueueState.Failed
                          && Q(s, r.QueueItemId).BlockedReason == "analysis-bench-destroyed"
                          && Stock(s, AnalysisCatalog.UnparsedModuleId) + Ground(AnalysisCatalog.UnparsedModuleId) == stock + ground + 2 && s.Research.Analysis.Tags.Length == 2
                          && s.Research.Analysis.WreckBuffer == 0 && Stock(s, AnalysisCatalog.WreckId) + Ground(AnalysisCatalog.WreckId) == wreckHome + wrecksSent
                          && HomeValleyAnalysis.StatusLine(s) == GameText.Get("analysis.status.destroyed");
            HomeValleyAnalysis.AnalysisOpResult afterDestroy = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            HomeValleyAnalysis.AnalysisOpResult wreckAfter = HomeValleyAnalysis.TrySendWrecks(s, out _);
            int intake0 = HomeValleyAnalysis.BeltIntakeCount;
            if (far.HasValue)
            {
                FgProductionSelfCheck.TestSource(s, far.Value, AnalysisCatalog.UnparsedModuleId, 2);
            }
            Seconds(10f);
            bool frozen = Math.Abs(Q(s, r.QueueItemId).Progress - pDead) < 1e-4f && HomeValleyAnalysis.ActiveCount(s) == 0
                          && HomeValleyAnalysis.BeltIntakeCount == intake0 && s.Research.Analysis.WreckBuffer == 0;
            // 重建完成（测试捷径：直接写回运转；重建工单由 FG4-ECO-05 覆盖）：恢复收货。
            Bench(s).ConstructionState = BuildingConstructionState.Operational;
            Bench(s).Health = BuildingOps.MaxDurability(Bench(s).BuildingTypeId);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            HomeValleyAnalysis.AnalysisOpResult rebuilt = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            Expect(disabled && back && killed && ghost && failed && frozen && far.HasValue && !afterDestroy.Success && afterDestroy.FailureReason == "no-bench"
                   && !wreckAfter.Success && wreckAfter.FailureReason == "no-bench" && rebuilt.Success,
                $"G2 禁用解析台：暂停、进度保留、写“已禁用”（{msg}），启用后接着做；解析台被正式摧毁入口打到 0（只剩可重建虚影，建筑状态“已摧毁”）：在办的两项中止、物品退回家园（仓库或落地由机器搬回）、身份放回、残骸缓存退回；" +
                $"之后进度不再推进、面板送入与传送带都不再收货（“{HomeValleyAnalysis.ReasonText("no-bench")}”）；重建后恢复收货" +
                $"（摧毁 {killed} / 虚影 {ghost} / 作废退回 {failed} / 冻结 {frozen} / 重建 {rebuilt.Success}）");
        }

        private static void CheckChipConsumedMidQueue()
        {
            CampaignState s = NewWorld(7119);
            string fw = RawFirmware(s);
            string part = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-consume", fw, out _);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueChip(s, part);
            Seconds(1f);
            // 合成台把这枚芯片当材料消耗（正式路径：预留 → 提交消耗）
            bool reserved = PrimitiveInventory.TryReserveForCraft(s, part, "fganalysis-craft").Success;
            bool consumed = PrimitiveInventory.ConsumeReservedMaterial(s, part, "fganalysis-craft").Success;
            int tech0 = s.TechData;
            RunToEnd(s, r.QueueItemId);
            AnalysisQueueItemRecord q = Q(s, r.QueueItemId);
            Expect(r.Success && reserved && consumed && q != null && q.State == AnalysisQueueState.Failed && q.BlockedReason == "chip-missing" && FirmwareKinds.IsRaw(s, fw)
                   && s.TechData == tech0 && !MechanicalContentUnlock.IsUnlocked(s, fw) && HomeValleyAnalysis.BlockedText(q.BlockedReason) == GameText.Get("analysis.blocked.chip_missing"),
                $"G3 负向：固件库里的芯片排进解析队列后被合成消耗——完成时发现芯片已不在家园：这一项失败（“{HomeValleyAnalysis.BlockedText(q?.BlockedReason)}”），不破解、不给技术数据（不能材料与破解结果都拿到）");
        }

        // ── H 残骸 ──────────────────────────────────────────────────────────────

        private static void CheckWrecks()
        {
            CampaignState s = NewWorld(7110);
            AnalysisKindDef wk = Kind(AnalysisCatalog.WreckId);
            HomeValleyAnalysis.AnalysisOpResult none = HomeValleyAnalysis.TrySendWrecks(s, out _);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "test", 3);
            bool noTags = s.Research.Analysis.Tags.Length == 0;
            HomeValleyAnalysis.AnalysisOpResult sent = HomeValleyAnalysis.TrySendWrecks(s, out int n);
            int tech0 = s.TechData;
            Seconds(wk.Seconds + 0.5f);
            bool one = s.Research.Analysis.WrecksProcessed == 1 && s.TechData == tech0 + wk.TechFirst && s.Research.Analysis.WreckBuffer == 2;
            Seconds(wk.Seconds * 0.5f);
            float half = s.Research.Analysis.WreckProgress;
            // 队列来活：残骸让路、进度不清零
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, string.Empty, "test", 1);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            Seconds(5f);
            bool yielded = Math.Abs(s.Research.Analysis.WreckProgress - half) < 1e-4f && Q(s, r.QueueItemId).State == AnalysisQueueState.Running;
            RunToEnd(s, r.QueueItemId);
            Seconds(wk.Seconds);
            bool resumed = s.Research.Analysis.WrecksProcessed == 2;
            // 缺电停住
            List<BuildingRecord> gens = Generators(s);
            gens.ForEach(g => g.ConstructionState = BuildingConstructionState.Destroyed);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            float p = s.Research.Analysis.WreckProgress;
            Seconds(wk.Seconds * 2f);
            bool held = Math.Abs(s.Research.Analysis.WreckProgress - p) < 1e-4f && s.Research.Analysis.WrecksProcessed == 2;
            gens.ForEach(g => g.ConstructionState = BuildingConstructionState.Operational);
            HomeValleyPowerGrid.Recompute(s);
            Seconds(wk.Seconds + 1f);
            bool done = s.Research.Analysis.WrecksProcessed == 3 && s.Research.Analysis.WreckBuffer == 0 && s.Research.Analysis.TechFromWrecks == 3 * wk.TechFirst;
            Expect(!none.Success && none.FailureReason == "wreck-none" && noTags && sent.Success && n == 3 && one && yielded && resumed && held && done
                   && HomeValleyAnalysis.WreckLine(s).Contains("已处理 3 件"),
                $"H 残骸（FGR-RND-024）：仓库没有残骸时“送进来”被拒；送进 3 件，队列空闲时每 {wk.Seconds:0.#} 秒处理一件、技术数据 +{wk.TechFirst}；队列来活时让路（进度 {half:0.#} 秒保留）、" +
                "做完接着处理；缺电停住、来电继续；残骸不登记身份");
        }

        // ── I 状态与空闲提示 ─────────────────────────────────────────────────────

        private static void CheckStatus()
        {
            CampaignState s = NewWorld(7111);
            BuildingStatus idle = BuildingStatusService.Evaluate(s, Bench(s));
            string line0 = HomeValleyAnalysis.StatusLine(s);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, string.Empty, "test", 1);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            Seconds(2f);
            BuildingStatus work = BuildingStatusService.Evaluate(s, Bench(s));
            string line1 = HomeValleyAnalysis.StatusLine(s);
            RunToEnd(s, r.QueueItemId);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "test", 1);
            HomeValleyAnalysis.TrySendWrecks(s, out _);
            Seconds(1f);
            BuildingStatus wreck = BuildingStatusService.Evaluate(s, Bench(s));
            Expect(idle.Kind == BuildingStatusKind.Idle && idle.ReasonCode == "analysis.idle" && idle.Reason == "解析台空闲：没有待解析的物品" && line0 == idle.Reason
                   && work.Kind == BuildingStatusKind.Working && work.ReasonCode == "analysis" && line1.Contains("正在解析")
                   && wreck.Kind == BuildingStatusKind.Working && wreck.ReasonCode == "analysis.wreck",
                $"I 解析台空闲时的提示（FG05 第 4 节）：建筑状态与面板状态行都写“{idle.Reason}”；解析中写“{line1}”；队列空闲处理残骸写“{wreck.Reason}”");
        }

        // ── J 存读档 ────────────────────────────────────────────────────────────

        private static void LayScenario(CampaignState s)
        {
            string mod = LockedModule(s);
            string fw = RawFirmware(s);
            string lore = AnalysisCatalog.Lore.First().Id;
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "scn", 2);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.EncryptedFirmwareId, fw, "scn", 1);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, lore, "scn", 1);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "scn", 4);
            HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.EncryptedFirmwareId, null);
            HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.DataCoreId, null);
            HomeValleyAnalysis.TrySendWrecks(s, out _);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7112);
            LayScenario(s);
            string fw = s.AnalysisQueues.First(q => q.ItemId == AnalysisCatalog.EncryptedFirmwareId).TargetId;
            Seconds(Kind(AnalysisCatalog.EncryptedFirmwareId).Seconds + Kind(AnalysisCatalog.UnparsedModuleId).Seconds * 0.5f);
            string before = Snapshot(s);
            bool crackedBefore = !FirmwareKinds.IsRaw(s, fw);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(60f);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded, out bool cracked)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                ResearchService.ResetForTests();
                HomeValleyAnalysis.ResetSessionState();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                cracked = false;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                cracked = !FirmwareKinds.IsRaw(rr.State, fw) && ChipCount(rr.State, fw) == 1;
                Seconds(60f);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1, out bool cracked1);
            string second = RunFromSave(out _, out _);
            Expect(save.Success && crackedBefore && loaded1 == before && cracked1 && before.Contains("tags=") && before.Contains("|Running|"),
                "J1 真文件存读档：身份清单、队列（来源 / 种类 / 身份 / 状态 / 进度）、残骸缓存与进度、读过的资料、统计、序号写进存档，读档后逐字段一致；" +
                "加密固件破解后“不被消耗”的状态（已破解、固件库里的芯片）读档后仍在" + (loaded1 == before ? string.Empty : $"\n存前：{before}\n读后：{loaded1}"));
            Expect(first == continuous && second == continuous,
                "J2 读档后接着跑 60 游戏秒与不存档连续跑逐字段一致（两次读档同一结果）" + (first == continuous ? string.Empty : $"\n连续：{continuous}\n读档：{first}"));
        }

        // ── K / L 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe);
            pausedHeld = true;
            LayScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 100;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 900)
            {
                WorldSimulation.Frame(1f / 60f, target);
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
                string snap = RunScenario(7201, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("|Completed|"),
                "K 暂停中（120 帧）解析与残骸都不动；0.5x / 1x / 2x / 3x 跑同样的 100 游戏秒（三类物品解析、残骸处理）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7202, true, 1f, false, out _);
            string unseen = RunScenario(7202, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("|Completed|"), "L 同一组解析与残骸在观察与不观察家园时跑 100 游戏秒逐字段一致（FGR-BASE-021：远征时家园照常解析）"
                                                                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── M 读档对账 ──────────────────────────────────────────────────────────

        private static void CheckRemovedContent()
        {
            CampaignState s = NewWorld(7113);
            string mod = LockedModule(s);
            string keep = LockedModule(s, 1);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "test", 2);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, keep, "test", 1);
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, s.Research.Analysis.Tags.First(t => t.TargetId == mod).TagId);
            int Home() => Stock(s, AnalysisCatalog.UnparsedModuleId)
                          + (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == Def(AnalysisCatalog.UnparsedModuleId).ResourceType).Sum(g => g.Amount);
            int home0 = Home();
            int active0 = HomeValleyAnalysis.ActiveCount(s);
            SaveContentReconciler.OverrideAnalysisLiveForTests(id => id != mod);
            SaveNoticeRecord[] notes;
            try
            {
                notes = SaveContentReconciler.ReconcileAnalysis(s, 2, 3);
            }
            finally
            {
                SaveContentReconciler.OverrideAnalysisLiveForTests(null);
            }
            SaveNoticeRecord[] again = SaveContentReconciler.ReconcileAnalysis(s, 2, 3);
            bool kept = home0 == 2 && active0 == 1 && Home() == 3 && HomeValleyAnalysis.ActiveCount(s) == 0 && string.IsNullOrEmpty(Q(s, r.QueueItemId).TargetId);
            var heldM = new List<HomeValleyAnalysis.HeldEntry>();
            HomeValleyAnalysis.CollectHeld(s, heldM);
            int unparsedStock = Stock(s, AnalysisCatalog.UnparsedModuleId);
            bool unidentifiedRow = heldM.Count(h => h.ItemId == AnalysisCatalog.UnparsedModuleId && string.IsNullOrEmpty(h.TargetId)) == (unparsedStock > 1 ? 1 : 0)
                                   && heldM.Where(h => h.ItemId == AnalysisCatalog.UnparsedModuleId).Sum(h => h.Count) == unparsedStock;
            Expect(kept && unidentifiedRow && notes.Length == 1 && Q(s, r.QueueItemId).State == AnalysisQueueState.Failed && Q(s, r.QueueItemId).BlockedReason == "content-removed"
                   && s.Research.Analysis.Tags.All(t => t.TargetId != mod) && s.Research.Analysis.Tags.Any(t => t.TargetId == keep) && again.Length == 0
                   && SaveContentReconciler.Render(notes[0]).Length > 0 && s.SaveHistory.Notices.Any(n => n.NoticeId == notes[0].NoticeId),
                $"M 读档对账（DEBT-FG0SAVE01-07 解析类）：解析台里引用已移除内容的在办项作废、物品按“身份不明”退回家园（家园件数 {home0}→{Home()}，守恒，不转废料、不丢失），" +
                $"身份清单里引用它的身份去掉（其余不动），待解析清单里身份不明的合并成一行；按内容通知一次（“{(notes.Length > 0 ? SaveContentReconciler.Render(notes[0]) : string.Empty)}”）；再对一次不重复" +
                $"（守恒 {kept} / 合并 {unidentifiedRow}）");
        }

        // ── N 物资分布 ──────────────────────────────────────────────────────────

        private static void CheckDistribution()
        {
            CampaignState s = NewWorld(7114);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, string.Empty, "test", 3);
            HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "test", 2);
            HomeValleyAnalysis.TrySendWrecks(s, out _);
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Append(new RegionQuestItemRecord
            {
                SalvageInstanceId = "fganalysis-quest", ContentId = FracturedCityLayout.MarkerModuleContentId, RegionId = FracturedCityLayout.RegionId, State = RegionQuestItemState.Recovered,
            }).ToArray();
            ItemDistribution.Invalidate();
            ItemDistributionView v = ItemDistribution.Get(s, Def(AnalysisCatalog.UnparsedModuleId));
            ItemDistributionView w = ItemDistribution.Get(s, Def(AnalysisCatalog.WreckId));
            long Part(ItemDistributionView view, string key) => view.Parts.Where(p => p.Key == key).Sum(p => p.Value);
            bool ok = Part(v, "item.dist.warehouse") == 2 && Part(v, "item.dist.analysis") == 1 && Part(v, "item.dist.quest") == 1 && v.Total == 4
                      && Part(w, "item.dist.analysis") == 2 && HomeValleyAnalysis.PhysicalCount(s, Def(AnalysisCatalog.UnparsedModuleId)) == 2;
            // 身份对账：仓库里少了一件（被分解），多出来的最新身份去掉
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, AnalysisCatalog.Lore[0].Id, "a", 1);
            Seconds(0.5f);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, AnalysisCatalog.Lore[1].Id, "b", 1);
            HomeInventory.RemoveUpTo(s, Def(AnalysisCatalog.DataCoreId), 1);
            ItemDistribution.Invalidate();
            int trimmed = HomeValleyAnalysis.ReconcileTags(s, d => HomeValleyAnalysis.PhysicalCount(s, d));
            bool trim = trimmed == 1 && s.Research.Analysis.Tags.Count(t => t.ItemId == AnalysisCatalog.DataCoreId) == 1
                        && s.Research.Analysis.Tags.First(t => t.ItemId == AnalysisCatalog.DataCoreId).TargetId == AnalysisCatalog.Lore[0].Id;
            Expect(ok && trim,
                "N 物资悬停分布（DEBT-FG4ECO01-04）：未解析模块写“仓库 2 / 解析台 1 / 区域任务物（待解析）1”，总数 4；残骸缓存算“解析台”；" +
                "身份清单比家园实有件数多（被回收站分解等）时去掉最新的多余身份");
        }

        // ── O 钩子与图鉴 ────────────────────────────────────────────────────────

        private static void CheckHookAndCodex()
        {
            CampaignState s = NewWorld(7115);
            bool before = !s.Research.Analysis.FirstItemSeen;
            int touch0 = GuidanceHooks.TouchCount;
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, AnalysisCatalog.Lore[0].Id, "test", 1);
            int touch1 = GuidanceHooks.TouchCount;
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, AnalysisCatalog.Lore[0].Id, "test", 1);
            bool once = GuidanceHooks.TouchCount == touch1;
            CampaignState q = NewWorld(7116);
            HomeValleyAnalysis.NoteEnemyItemSeen(q);
            Expect(before && s.Research.Analysis.FirstItemSeen && touch1 == touch0 + 1 && once && GameSettings.HasSeenGuidanceHook(GuidanceHooks.AnalysisFirstEnemyItem)
                   && MechanicCodex.IsUnlocked("codex.research.analysis") && q.Research.Analysis.FirstItemSeen,
                "O 第一次拿到敌方物品：触发引导钩子“analysis.first_enemy_item”（只埋钩子，引导内容在 FG15-UX-04；同一存档只扫一次），图鉴“解析台与敌方物品”随之解锁；Demo 区域任务物带回 / 敌方加密固件发放走同一入口");
        }

        // ── P 面板（真 UXML）──────────────────────────────────────────────────────

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

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(7117);
            VisualElement root = FgProductionSelfCheck.MountUxml(AnalysisUxml, out GameObject go);
            try
            {
                AnalysisPanelUIToolkit panel = go.AddComponent<AnalysisPanelUIToolkit>();
                panel.BindForTests(root, AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AnalysisRowUxml));
                panel.RefreshNow();
                Button enqueue = root.Q<Button>("EnqueueButton");
                DropdownField held = root.Q<DropdownField>("WarehouseDropdown");
                bool empty = panel.StatusText == "解析台空闲：没有待解析的物品" && panel.HeldChoices.Count == 0 && !enqueue.enabledSelf
                             && held.value == GameText.Get("analysis.panel.held_empty") && panel.QueueTitleText.Contains("0/8") && panel.WreckText.Contains("0/20")
                             && root.Q<Label>("Title").text == "解析台" && enqueue.text == "送进解析台";
                Expect(empty, $"P1 解析台面板（真 UXML）：空闲状态“{panel.StatusText}”；待解析清单为空时显示占位“{held.value}”、送入按钮禁用；队列“{panel.QueueTitleText}”；残骸栏“{panel.WreckText}”");

                string mod = LockedModule(s);
                string fw = RawFirmware(s);
                HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, mod, "test", 2);
                PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-panel", fw, out _);
                panel.RefreshNow();
                bool listed = panel.HeldChoices.Count == 2 && held.choices.Count == 2 && held.choices[0].Contains("×2") && held.choices[0].Contains("？？")
                              && held.choices[1].Contains("加密固件") && panel.HeldPreviewText.Contains("？？") && panel.HeldPreviewText.Contains("秒");
                Expect(listed, $"P2 待解析清单按身份合并（“{held.choices.FirstOrDefault()}”）、固件库里的未破解芯片单列（“{held.choices.ElementAtOrDefault(1)}”）；所选那一行的预览“{panel.HeldPreviewText}”");

                bool clicked = Click(enqueue);
                panel.RefreshNow();
                string qid = panel.RowQueueId(0);
                bool queued = clicked && qid != null && Q(s, qid)?.TargetId == mod && Stock(s, AnalysisCatalog.UnparsedModuleId) == 1 && panel.QueueTitleText.Contains("1/8")
                              && panel.RowText(0, "Reason").Contains("？？") && panel.ResultText.Contains("已送进解析台");
                panel.SelectHeld(1);
                Click(enqueue);
                panel.RefreshNow();
                bool chipQueued = s.AnalysisQueues.Any(q => q.Source == HomeValleyAnalysis.SourceChip && q.TargetId == fw && HomeValleyAnalysis.IsActiveEntry(q));
                Expect(queued && chipQueued, $"P3 点“送进解析台”：队列第 1 行“{panel.RowText(0, "Name")} · {panel.RowText(0, "State")} · {panel.RowText(0, "Reason")}”，结果行“{panel.ResultText}”；选中第二行再点，芯片排进队列");

                Button rowCancel = panel.RowQueueId(0) != null ? root.Q<ScrollView>("QueueList").Query<Button>("RowCancel").First() : null;
                bool cancelled = Click(rowCancel);
                panel.RefreshNow();
                bool back = cancelled && Q(s, qid).State == AnalysisQueueState.Cancelled && Stock(s, AnalysisCatalog.UnparsedModuleId) == 2 && panel.ResultText.Contains("原样退回");
                Expect(back, $"P4 行内“×”取消：物品原样退回（仓库 {Stock(s, AnalysisCatalog.UnparsedModuleId)} 件），结果行“{panel.ResultText}”");

                Button wreckBtn = root.Q<Button>("WreckButton");
                Click(wreckBtn);
                panel.RefreshNow();
                bool wreckDenied = panel.ResultText == GameText.Get("analysis.reason.wreck_none");
                HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "test", 2);
                Click(wreckBtn);
                panel.RefreshNow();
                Expect(wreckDenied && s.Research.Analysis.WreckBuffer == 2 && panel.WreckText.Contains("2/20") && panel.ResultText.Contains("2"),
                    $"P5 残骸栏：仓库没有残骸时写原因；有残骸时“把仓库里的残骸送进来”送进缓存（“{panel.WreckText}”）");

                string probe = UiToolkitLayoutProbe.Probe(AnalysisUxml, "AnalysisPanelRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("AnalysisPanelRoot")?.AddToClassList("ana-root-visible");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "P6 解析台面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>P7：队列行图标不泄露未知身份（第一次遇到的敌方加密固件预览写“？？”，图标也不能先显示那枚固件）；固件库里的芯片本来就认识，照常显示。</summary>
        private static void CheckRowIconNoLeak()
        {
            CampaignState s = NewWorld(7120);
            VisualElement root = FgProductionSelfCheck.MountUxml(AnalysisUxml, out GameObject go);
            try
            {
                AnalysisPanelUIToolkit panel = go.AddComponent<AnalysisPanelUIToolkit>();
                panel.BindForTests(root, AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AnalysisRowUxml));
                string fw = RawFirmware(s);
                string fwChip = RawFirmware(s, 1);
                HomeValleyAnalysis.Acquire(s, AnalysisCatalog.EncryptedFirmwareId, fw, "test", 1);
                HomeValleyAnalysis.AnalysisOpResult ri = HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.EncryptedFirmwareId, null);
                string part = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fganalysis-icon", fwChip, out _);
                HomeValleyAnalysis.AnalysisOpResult rc = HomeValleyAnalysis.TryEnqueueChip(s, part);
                panel.RefreshNow();
                List<VisualElement> icons = root.Q<ScrollView>("QueueList").Query<VisualElement>("Icon").ToList();
                int iItem = -1;
                int iChip = -1;
                for (int i = 0; i < icons.Count; i++)
                {
                    if (panel.RowQueueId(i) == ri.QueueItemId)
                    {
                        iItem = i;
                    }
                    else if (panel.RowQueueId(i) == rc.QueueItemId)
                    {
                        iChip = i;
                    }
                }
                string itemIcon = iItem >= 0 ? icons[iItem].userData as string : "missing-row";
                string chipIcon = iChip >= 0 ? icons[iChip].userData as string : "missing-row";
                bool hidden = ri.Success && iItem >= 0 && string.IsNullOrEmpty(itemIcon) && panel.RowText(iItem, "Reason").Contains("？？");
                bool chipShown = rc.Success && iChip >= 0 && chipIcon == GameLogic.UI.Common.ContentIcons.IconIdFor(fwChip);
                Expect(hidden && chipShown,
                    $"P7 队列行图标不泄露身份：第一次遇到的敌方加密固件行预览“{(iItem >= 0 ? panel.RowText(iItem, "Reason") : string.Empty)}”、图标留空（该固件图标 {GameLogic.UI.Common.ContentIcons.IconIdFor(fw) ?? "无"}，行上 {itemIcon ?? "空"}）；" +
                    $"固件库里的芯片照常显示自己的图标（{chipIcon ?? "空"}）");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── Q 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7118);
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.UnparsedModuleId, string.Empty, "perf", 8);
            for (int i = 0; i < 8; i++)
            {
                HomeValleyAnalysis.TryEnqueueItem(s, AnalysisCatalog.UnparsedModuleId, null);
            }
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.WreckId, null, "perf", 20);
            HomeValleyAnalysis.TrySendWrecks(s, out _);
            float dt = 1f / GameClock.StepHz;
            for (int i = 0; i < 200; i++)
            {
                HomeValleyAnalysis.Tick(s, 0f);
            }
            var sw = Stopwatch.StartNew();
            const int steps = 20000;
            for (int i = 0; i < steps; i++)
            {
                HomeValleyAnalysis.Tick(s, 0f); // dt = 0：只量每步的判定开销（队列 8 项 + 残骸缓存），不推进
            }
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            // 待解析清单：200 条身份 + 面板刷新一次
            HomeValleyAnalysis.Acquire(s, AnalysisCatalog.DataCoreId, AnalysisCatalog.Lore[0].Id, "perf", 200);
            var list = new List<HomeValleyAnalysis.HeldEntry>();
            HomeValleyAnalysis.CollectHeld(s, list);
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                HomeValleyAnalysis.CollectHeld(s, list);
            }
            sw2.Stop();
            double perList = sw2.Elapsed.TotalMilliseconds / 100.0;
            PerfLines.Add($"解析台每个世界步（队列 8 项 + 残骸 20 件）{perStep * 1000.0:F2} µs；待解析清单（200 条身份）每次 {perList:F3} ms（只在面板刷新时，0.2 秒一次）；dt={dt:F3}");
            PerfGate.Expect(true,
                $"Q 性能：解析台每个世界步 {perStep:F4} ms（阈值 0.02 ms，O(队列长度)，队列长度有上限）；待解析清单 {perList:F3} ms（阈值 1 ms，只在面板刷新时）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perStep, 0.02, "解析台每步 ms"), PerfGate.Le(perList, 1.0, "待解析清单 ms") }, Expect, Line);
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

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

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
