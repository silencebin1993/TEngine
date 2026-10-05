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
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-06 常驻规则的自动验收（FG04 FGR-ECO-030 / 031；FG13 FGU-15；FG00 FGR-BASE-020 / 021；FGT-ECO-003；卡片“规则的复制和常用预设；实体上显示‘由规则 R3 触发’；
    /// 第一次设置规则时的引导”；负向“两条规则争同一座工厂；规则引用的建筑被拆除”；承接 DEBT-FG1SIG03-01）。
    /// 全部起真实系统：真实家园（世界模拟、电网、生产服务、工单与机器、战斗内核里的机器移动）、真文件存读档、真 UXML 面板。
    /// A 数据（两张新表与源数据逐字段、七类、文本中英、钩子、图鉴、通知类型、按键）；B 默认规则（新战役“远征卸货”默认开启，旧档补一次）；
    /// C 不做玩家没要求的事（没有规则 / 规则停用时：低库存、受伤机器、被摧毁的建筑、突袭都不会引起任何动作）；
    /// D FGT-ECO-003 七类各触发一次（库存维持 → 排产并出产、补足后恢复原配方；阈值补给 → 机器送货进输入缓存；战时预案 → 暂停 + 维修单提到最高、突袭结束恢复；
    /// 静默夜预案 → 机器去驻防点 + 储能站只放电、结束恢复；机器维修 → 机器自己去维修台修满、修理中拒绝接入；远征卸货 → 货舱卸进库存；自动重建 → 重建单（材料不够等着、够了自动派））；
    /// E 冲突（两条规则争同一座工厂：按优先级、冲突警告、换优先级后翻转；战时预案与库存维持争同一座）；F 规则引用的建筑被拆除；G 玩家手动改动让位；
    /// H 停用 / 删除恢复原样；I 复制与常用预设；J 追溯（建筑面板、工单、机器、日志）与日志上限；K 真文件存读档；L 暂停与 0.5x～3x；M 观察 / 不观察一致；N 面板（真 UXML + 布局探针）；O 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgStandingRulesSelfCheck）。
    /// </summary>
    public static class FgStandingRulesSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 7000;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 常驻规则")]
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
            Line("\n[常驻规则] 七类规则、冲突与优先级、追溯与日志、让位与恢复、复制与预设、存读档、倍速、观察一致、面板（FG4-ECO-06）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgrules-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                     "规则检查 / 持有 / 日志在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），机器移动在战斗内核（AOT）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckDefaults);
                Step(CheckNothingWithoutRules);
                Step(CheckStockKeep);
                Step(CheckSupply);
                Step(CheckWarPlan);
                Step(CheckSilentNight);
                Step(CheckMachineRepair);
                Step(CheckRuleTakesHaulingMachine);
                Step(CheckExpeditionUnload);
                Step(CheckAutoRebuild);
                Step(CheckConflicts);
                Step(CheckTargetDemolished);
                Step(CheckMultiTargetPartlyMissing);
                Step(CheckPlayerOverride);
                Step(CheckRuleEditIsNotOverride);
                Step(CheckDisableDeleteRestore);
                Step(CheckCopyAndPresets);
                Step(CheckTraceAndLog);
                Step(CheckSaveLoad);
                Step(CheckSelfOrderLegAfterLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanel);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"常驻规则自检抛异常：{e}");
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
                StandingRuleService.ResetForTests();
                SignalUplinkService.OnRepairBayProvider = null;
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
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                RulesPanelUIToolkit.InWorldOverrideForTests = false;
                RulesPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
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
            Line($"  · [常驻规则] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            StandingRuleService.ResetForTests();
            _seq = 7000;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            // 开局的仓库是待修的残骸（Demo 起）：测试捷径直接修好（重建流程由 FG4-ECO-05 自检覆盖），让物品有地方存。
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return s;
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（真实放置 / 施工由 FG3 / FG4-ECO-05 覆盖），ID 按正式格式“类型#序号”，位置按种子地形找空地（B25）。</summary>
        private static BuildingRecord Place(CampaignState s, string typeId)
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, typeId, 8f, 26f);
            if (!at.HasValue)
            {
                return null;
            }
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = at.Value.X,
                GridY = at.Value.Y,
                Position = GridMath.FootprintCenter(at.Value, bg.FootprintW, bg.FootprintH, 0),
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

        private static BuildingRecord Home(CampaignState s, string typeId) => s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == typeId && b.RegionId == HomeValleyLayout.RegionId);

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) => FgProductionSelfCheck.P(s, b);

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        /// <summary>改动之后的下一个模拟步规则就检查一次。</summary>
        private static void Tick() => WorldSimulation.StepMany(2);

        private static int Stock(CampaignState s, string id) => HomeInventory.Stock(s, id);

        private static void Give(CampaignState s, string itemId, int n)
        {
            ItemCatalog.TryGet(itemId, out ItemDef d);
            HomeInventory.Add(s, d, n, clampToSpace: false);
        }

        private static void Take(CampaignState s, string itemId)
        {
            ItemCatalog.TryGet(itemId, out ItemDef d);
            HomeInventory.RemoveUpTo(s, d, HomeInventory.Stock(s, d));
        }

        private static StandingRuleRecord New(CampaignState s, string kind)
        {
            StandingRuleService.TryCreate(s, kind, out StandingRuleRecord r, out _);
            return r;
        }

        /// <summary>FG6-DEF-03：只数这座建筑的日志（新建自动重建规则会补排开局预置的残骸，规则的总日志数不再只有被测的这一座）。</summary>
        private static int LogsAt(CampaignState s, string key, int rule, string buildingId) =>
            StandingRuleService.LogEntries(s).Count(l => l.Key == key && l.Rule == rule && l.EntityId == StandingRuleService.BuildingKey(buildingId));

        private static int Logs(CampaignState s, string key, int rule = 0) =>
            StandingRuleService.LogEntries(s).Count(e => e.Key == key && (rule == 0 || e.Rule == rule));

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();

        private static WorkOrderRecord OrderOf(CampaignState s, WorkOrderKind kind, Func<WorkOrderRecord, bool> pick = null) =>
            (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).LastOrDefault(o => o != null && o.Kind == kind && (pick == null || pick(o)));

        /// <summary>只保留默认规则以外的规则（删掉默认的“远征卸货”，让测试只看自己设的规则）。</summary>
        private static void ClearRules(CampaignState s)
        {
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static bool Raid;

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] rk = lines.Where(l => l.StartsWith("RK\t", StringComparison.Ordinal)).ToArray();
            string[] rp = lines.Where(l => l.StartsWith("RP\t", StringComparison.Ordinal)).ToArray();
            string[] rkRun = ConfigSystem.Instance.Tables.TbRuleKind.DataList
                .Select(r => string.Join("\t", "RK", r.Id, r.NameKey, r.DescKey, r.ThresholdKey, I(r.ThresholdDefault), I(r.ThresholdMin), I(r.ThresholdMax), I(r.SortOrder))).ToArray();
            string[] rpRun = ConfigSystem.Instance.Tables.TbRulePreset.DataList
                .Select(r => string.Join("\t", "RP", r.Id, r.Kind, r.NameKey, r.ItemId, I(r.Threshold), I(r.Batch), r.RecipeId, I(r.BoostRepair), I(r.SortOrder))).ToArray();
            Expect(code == 0 && rk.Length == 7 && rk.SequenceEqual(rkRun) && rp.Length >= 7 && rp.SequenceEqual(rpRun),
                $"A1 fg.TbRuleKind（{rkRun.Length} 类）与 fg.TbRulePreset（{rpRun.Length} 条预设）运行时表与 fgdata_rules.py 逐字段一致（改了源数据没重新生成会失败）");
            bool kinds = StandingRuleService.AllKinds.Length == 7 && StandingRuleService.AllKinds.All(k => StandingRuleService.KindRow(k) != null)
                         && new[] { "stock_keep", "supply", "war_plan", "silent_night", "machine_repair", "expedition_unload", "auto_rebuild" }.SequenceEqual(StandingRuleService.AllKinds);
            Expect(kinds, "A2 七类规则（FGR-ECO-030）：库存维持 / 阈值补给 / 战时预案 / 静默夜预案 / 机器维修 / 远征卸货 / 自动重建，代码与表一一对应");
            // 文本：本 Story 用到的键中英都有（扫源码里的 "rules.*" / "work.*" 键，断言扫到的条数下限，防止正则写坏静默通过）。
            string src = File.ReadAllText(Path.Combine(root, "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/StandingRuleService.cs"))
                         + File.ReadAllText(Path.Combine(root, "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/StandingRuleText.cs"))
                         + File.ReadAllText(Path.Combine(root, "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/RulesPanelUIToolkit.cs"));
            var keys = System.Text.RegularExpressions.Regex.Matches(src, "\"((?:rules|work)\\.[a-z_\\.]+)\"").Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value).Where(k => !k.EndsWith(".", StringComparison.Ordinal) && !GridContent.TryGetTuning(k, out _)).Distinct().ToList();
            var missing = keys.Where(k => string.IsNullOrEmpty(GameText.Get(k, GameLanguage.ZhCn)) || GameText.Get(k, GameLanguage.ZhCn).Contains("⟦")
                                          || string.IsNullOrEmpty(GameText.Get(k, GameLanguage.En)) || GameText.Get(k, GameLanguage.En).Contains("⟦")).ToList();
            Expect(keys.Count >= 80 && missing.Count == 0, $"A3 文本键（B16）：源码引用的 {keys.Count} 个 rules.* / work.* 键中英都有" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing.Take(10))));
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.RulesPanelFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.RulesFirstCreated)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.RulesFirstTriggered) && GuidanceHooks.Known.Contains(GuidanceHooks.RulesFirstConflict);
            CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.economy.rules");
            NotifyType nt = ConfigSystem.Instance.Tables.TbNotifyType.GetOrDefault("standing_rule");
            InputAction ia = ConfigSystem.Instance.Tables.TbInputAction.GetOrDefault(nameof(GameActionId.OpenRules));
            Expect(hooks && cx != null && cx.Hooks.Contains("rules.first_created") && nt != null && nt.Tier == "warning" && ia != null && ia.DefaultBinding == "R" && ia.DefaultMods == "alt"
                   && ia.Status == "wired",
                "A4 引导钩子（第一次打开 / 新建 / 触发 / 冲突，内容在 FG15-UX-04）、图鉴“常驻规则”、通知类型 standing_rule（警告级，聚合、可定位）、按键“常驻规则”默认 Alt+R 已接入");
            Expect(StandingRuleService.CheckSeconds == 60.0 && StandingRuleService.LogMax == 100,
                $"A5 调参：规则每 {StandingRuleService.CheckSeconds} 游戏秒检查一次（FG04 第 7 节“每游戏分钟一次”）；日志保留 {StandingRuleService.LogMax} 条（第 6 节）");
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        // ── B 默认规则 ─────────────────────────────────────────────────────────────

        private static void CheckDefaults()
        {
            CampaignState s = NewWorld(6101);
            List<StandingRuleRecord> rules = StandingRuleService.Ordered(s);
            bool fresh = rules.Count == 1 && rules[0].Kind == StandingRuleService.KindUnload && rules[0].Enabled && rules[0].Priority == 1 && rules[0].Serial == 1
                         && s.StandingRules.DefaultsSeeded;
            // 旧档：规则域是空的、没补过默认规则 → 读进来补一次；再补一次不会重复。
            var old = new StandingRuleState { DefaultsSeeded = false, Rules = null, Holds = null, Log = null, Pending = null };
            CampaignFgStateDomains.EnsureRules(old);
            CampaignFgStateDomains.EnsureRules(old);
            bool migrated = old.Rules.Length == 1 && old.Rules[0].Kind == StandingRuleService.KindUnload && old.Rules[0].Enabled && old.Holds != null && old.Log != null && old.Pending != null;
            // 玩家删掉默认规则后不会被补回来。
            StandingRuleService.TryDelete(s, rules[0].Serial, out _);
            CampaignFgStateDomains.EnsureAll(s);
            bool stays = StandingRuleService.Ordered(s).Count == 0;
            Expect(fresh && migrated && stays, $"B 新战役默认带一条启用的“远征卸货”（R1，FGR-ECO-030“默认开启”）；旧档补一次、不重复；玩家删掉后不再补回（{fresh}/{migrated}/{stays}）");
        }

        // ── C 不做玩家没要求的事 ───────────────────────────────────────────────────

        private static void CheckNothingWithoutRules()
        {
            CampaignState s = NewWorld(6111);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            BuildingRecord victim = Place(s, "refinery_furnace");
            MachineRecord m = HomeMachines().First();
            MachineRegistry.ApplyDamage(m.LogicId, m.MaxHealth * 0.8f);
            int orders0 = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Length;
            BuildingOps.ApplyDamage(s, victim.BuildingId, 1000f);
            Raid = true;
            StandingRuleService.RaidActiveProvider = _ => Raid;
            Seconds(130f);
            // 再设一条规则但停用：同样什么都不做。
            StandingRuleRecord keep = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, keep.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, keep.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, keep.Serial, false, out _);
            Seconds(70f);
            bool nothing = P(s, ws).Recipe?.Id == "part" && victim.ConstructionState == BuildingConstructionState.Damaged
                           && OrderOf(s, WorkOrderKind.Repair, o => o.TargetId == victim.BuildingId) == null
                           && OrderOf(s, WorkOrderKind.MachineRepair) == null && StandingRuleService.Holds(s).Count == 0
                           && StandingRuleService.LogEntries(s).Count == 0 && !BuildingOps.IsDisabled(ws)
                           && (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).All(o => o.RuleSerial == 0);
            Expect(nothing && orders0 >= 0,
                "C 没有规则 / 规则停用时：仓库没有维修件、机器伤势 80%、建筑被摧毁、突袭进行中 200 游戏秒——工厂配方不变、不派送修 / 重建、不暂停任何建筑、没有持有与日志（FGR-BASE-020）");
            StandingRuleService.RaidActiveProvider = null;
        }

        // ── D FGT-ECO-003 七类各触发一次 ──────────────────────────────────────────────

        private static void CheckStockKeep()
        {
            CampaignState s = NewWorld(6121);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            StandingRuleRecord r = New(s, StandingRuleService.KindStock);
            bool createdOff = !r.Enabled;
            StandingRuleService.TrySetItem(s, r.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 3, out _);
            bool autoRecipe = r.RecipeId == "repair_kit";
            bool enabled = StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            bool switched = P(s, ws).Recipe?.Id == "repair_kit" && r.Active && Logs(s, "rules.log.stock_on", r.Serial) == 1
                            && StandingRuleService.DescribeBuilding(s, ws.BuildingId)?.Contains("R" + r.Serial) == true;
            // 真的出产：给零件输入，跑到出了维修件（产出在建筑输出缓存，由传送带 / 机器送进仓库；这里当作已送达，补进库存）。
            P(s, ws).Rec.In = new[] { new ItemStackRecord { ItemId = "part", Amount = 20 } };
            bool produced = StepUntil(() => ProductionService.Count(P(s, ws).Rec.Out, "repair_kit") >= 1, 60);
            // 下一次定时检查前不会改回（不每帧检查）。
            Give(s, "repair_kit", 3);
            Tick();
            bool notYet = P(s, ws).Recipe?.Id == "repair_kit";
            bool restored = StepUntil(() => P(s, ws).Recipe?.Id == "part", 70);
            bool logged = Logs(s, "rules.log.stock_off", r.Serial) == 1 && !r.Active && StandingRuleService.Holds(s).Count == 0;
            // 再次不足：再次接管（不是只触发一次）。
            Take(s, "repair_kit");
            bool again = StepUntil(() => P(s, ws).Recipe?.Id == "repair_kit", 70) && Logs(s, "rules.log.stock_on", r.Serial) == 2 && r.FireCount >= 2;
            Expect(createdOff && autoRecipe && enabled && switched && produced && notYet && restored && logged && again,
                $"D1 库存维持：新建先停用（缺物品 / 工厂）；选物品 + 工厂自动选产出它的配方；启用后下一步零件工坊从“零件”改为排产“维修件”并真的出产，建筑上写“由规则 R{r.Serial} 触发”；" +
                $"补足后到下一次定时检查才恢复“零件”（不每帧）并写日志；再次不足再次接管（{createdOff}/{autoRecipe}/{enabled}/{switched}/{produced}/{notYet}/{restored}/{logged}/{again}）");
        }

        private static void CheckSupply()
        {
            CampaignState s = NewWorld(6131);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            Give(s, "alloy", 30);
            StandingRuleRecord r = New(s, StandingRuleService.KindSupply);
            StandingRuleService.TrySetItem(s, r.Serial, "alloy", out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 5, out _);
            StandingRuleService.TrySetBatch(s, r.Serial, 10, out _);
            bool added = StandingRuleService.TryAddTarget(s, r.Serial, ws.BuildingId, out _);
            bool badTarget = !StandingRuleService.TryAddTarget(s, r.Serial, Home(s, HomeValleyLayout.BuildingTypeWarehouse).BuildingId, out string badWhy) && badWhy.Contains("不能选");
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            int room = ProductionService.InCapacity(P(s, ws), alloy);
            int expect = Math.Min(10, room);
            Tick();
            WorkOrderRecord o = OrderOf(s, WorkOrderKind.Deliver, x => x.TargetId == ws.BuildingId);
            bool ordered = o != null && o.RuleSerial == r.Serial && o.ReservedItemAmount == expect && Stock(s, "alloy") == 30 - expect && Logs(s, "rules.log.supply_send", r.Serial) == 1;
            bool one = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(x => x.Kind == WorkOrderKind.Deliver && HomeValleyWorkOrders.IsActive(x)) == 1;
            bool delivered = o != null && StepUntil(() => o.State == WorkOrderState.Completed, 120);
            int inBuf = ProductionService.Count(P(s, ws).Rec.In, "alloy");
            bool inside = delivered && inBuf + (int)P(s, ws).Rec.Completed >= expect - 1 && Logs(s, "rules.log.supply_done", r.Serial) >= 1 && o.AssignedMachineLogicId == 0;
            // 停用：还没送到的补给单取消、预留的合金全额退回。
            StandingRuleService.TrySetEnabled(s, r.Serial, false, out _);
            bool noActive = OrderOf(s, WorkOrderKind.Deliver, x => HomeValleyWorkOrders.IsActive(x)) == null;
            // 仓库没货：写明原因，不开空单；有货后自动继续。
            Take(s, "alloy");
            P(s, ws).Rec.In = Array.Empty<ItemStackRecord>();
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            bool noStock = r.IssueKey == "rules.issue.no_stock" && StandingRuleService.IssueText(r).Contains("合金") && OrderOf(s, WorkOrderKind.Deliver, x => HomeValleyWorkOrders.IsActive(x)) == null;
            Give(s, "alloy", 4);
            int expect2 = Math.Min(4, room);
            bool resumed = StepUntil(() => OrderOf(s, WorkOrderKind.Deliver, x => HomeValleyWorkOrders.IsActive(x))?.ReservedItemAmount == expect2, 70) && r.IssueKey.Length == 0;
            Expect(added && badTarget && ordered && one && delivered && inside && noActive && noStock && resumed,
                $"D2 阈值补给：零件工坊的合金少于 5 → 从仓库预留 {expect} 件（每次 10 件、不超过输入缓存余量 {room}）、开补给单（每座目标同时只一张），机器送进输入缓存（输入缓存 {inBuf}）；" +
                $"不是生产建筑的目标被拒并写明；停用时没送到的单子取消退回；仓库没货时写明原因不开空单，有货后（4 件）自动继续" +
                $"（{added}/{badTarget}/{ordered}/{one}/{delivered}/{inside}/{noActive}/{noStock}/{resumed}；单 {o?.State}/{o?.ReservedItemAmount}，库存 {Stock(s, "alloy")}，原因 {r.IssueKey}）");
        }

        private static void CheckWarPlan()
        {
            CampaignState s = NewWorld(6141);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            BuildingRecord f = Place(s, "refinery_furnace");
            StandingRuleRecord r = New(s, StandingRuleService.KindWar);
            bool on = r.Enabled && r.BoostRepair;
            StandingRuleService.TryAddTarget(s, r.Serial, ws.BuildingId, out _);
            Give(s, BuildingOps.RepairKitId, 10);
            BuildingOps.ApplyDamage(s, f.BuildingId, 50f);
            BuildingOps.TryOrderRepair(s, f.BuildingId, out _);
            WorkOrderRecord before = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId);
            int prevPriority = before?.Priority ?? -99;
            // 默认判定：有突袭部队到达家园、还没结算（FG6-DEF-04 接手前）。真实派一支、放到核心旁，下一步到达 → 规则在下一个模拟步立刻检查。
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out _);
            if (g != null)
            {
                g.PosX = g.TargetX;
                g.PosY = g.TargetY;
            }
            long t0 = GameClock.Ticks;
            bool started = StepUntil(() => BuildingOps.IsDisabled(ws), 5) && GameClock.Ticks - t0 < GameClock.StepHz * 5;
            WorkOrderRecord rep = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId);
            bool boosted = started && rep != null && rep.Priority == StandingRuleService.WarRepairPriority && rep.RuleSerial == r.Serial
                           && StandingRuleService.DescribeOrder(s, rep).Contains("R" + r.Serial) && Logs(s, "rules.log.war_on", r.Serial) == 1
                           && StandingRuleService.DescribeBuilding(s, ws.BuildingId).Contains("R" + r.Serial);
            // 突袭期间新开的维修单也立刻提到最高。
            BuildingRecord f2 = Place(s, "refinery_furnace");
            BuildingOps.ApplyDamage(s, f2.BuildingId, 50f);
            BuildingOps.TryOrderRepair(s, f2.BuildingId, out _);
            WorkOrderRecord rep2 = HomeValleyWorkOrders.FindActiveRepair(s, f2.BuildingId);
            bool newBoosted = rep2 != null && rep2.Priority == StandingRuleService.WarRepairPriority && rep2.RuleSerial == r.Serial;
            // 突袭结束（结算在 FG6；这里把到达的部队移走）→ 下一次定时检查恢复。
            s.Raids.InTransit = Array.Empty<TransitGroupRecord>();
            bool ended = StepUntil(() => !BuildingOps.IsDisabled(ws), 70);
            WorkOrderRecord rep2After = HomeValleyWorkOrders.Find(s, rep2?.WorkOrderId);
            bool restored = ended && (rep2After == null || !HomeValleyWorkOrders.IsActive(rep2After) || rep2After.Priority == 0) && Logs(s, "rules.log.war_off", r.Serial) == 1
                            && !r.Active && StandingRuleService.Holds(s).Count == 0;
            Expect(on && g != null && started && boosted && newBoosted && restored,
                $"D3 战时预案：突袭部队到达家园（默认判定）后不到 5 秒执行——暂停零件工坊、维修单优先级 {prevPriority} → 最高（{StandingRuleService.WarRepairPriority}）并写“由规则 R{r.Serial}”；" +
                $"突袭期间新开的维修单也立刻提到最高；突袭结束后恢复启用与原优先级（{on}/{g != null}/{started}/{boosted}/{newBoosted}/{restored}）");
        }

        private static void CheckSilentNight()
        {
            CampaignState s = NewWorld(6151);
            ClearRules(s);
            BuildingRecord st = Place(s, "energy_storage");
            MachineRecord m = HomeMachines().First();
            StandingRuleRecord r = New(s, StandingRuleService.KindSilent);
            bool off = !r.Enabled && StandingRuleService.ConfigIssue(s, r, out _, out _) != null;
            StandingRuleService.TryAddMachine(s, r.Serial, m.LogicId, out _);
            StandingRuleService.TryAddTarget(s, r.Serial, st.BuildingId, out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 30, out _);
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            bool noProvider = true;
            Seconds(65f);
            noProvider = !r.Active && OrderOf(s, WorkOrderKind.Garrison) == null;
            // FG7 接入后的时间窗：100 秒后开始、持续 60 秒。提前 30 秒 → 第 70 秒起执行。
            long start = GameClock.Ticks + GameClock.StepHz * 100L;
            long end = start + GameClock.StepHz * 60L;
            StandingRuleService.SilentNightWindowProvider = _ => (start, end);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            bool notYet = !r.Active && OrderOf(s, WorkOrderKind.Garrison) == null;
            bool fired = StepUntil(() => r.Active, 140) && GameClock.Ticks >= start - GameClock.StepHz * 30L;
            WorkOrderRecord o = OrderOf(s, WorkOrderKind.Garrison, x => x.AssignedMachineLogicId == m.LogicId);
            StorageSettings set = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId);
            bool acted = fired && o != null && o.RuleSerial == r.Serial && set.NoCharge && !set.NoDischarge && Logs(s, "rules.log.discharge", r.Serial) == 1;
            bool arrived = StepUntil(() => o.State == WorkOrderState.InProgress, 90);
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 at = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
            bool there = arrived && Vector2.Distance(at, core) < 12f && StandingRuleService.DescribeMachine(s, m.LogicId).Contains("R" + r.Serial);
            // 驻防中不会被自动派活。
            bool held = HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId) == o;
            bool over = StepUntil(() => !r.Active, 200);
            StorageSettings back = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId);
            bool restored = over && o.State == WorkOrderState.Completed && !back.NoCharge && !back.NoDischarge && Logs(s, "rules.log.silent_off", r.Serial) == 1;
            Expect(off && noProvider && notYet && acted && arrived && there && held && restored,
                $"D4 静默夜预案：没选机器 / 储能站时不能启用；静默夜未开放（FG7 前没有时间窗）时不触发；有时间窗后在开始前 30 秒执行——机器 #{m.DisplayNumber} 走到驻防点（归还核心）待命、" +
                $"不被自动派活，储能站切换为只放电；静默夜结束后机器收工、储能站恢复原设置（{off}/{noProvider}/{notYet}/{acted}/{arrived}/{there}/{held}/{restored}）");
            StandingRuleService.SilentNightWindowProvider = null;
        }

        private static void CheckMachineRepair()
        {
            CampaignState s = NewWorld(6161);
            ClearRules(s);
            BuildingRecord bay = Home(s, HomeValleyLayout.BuildingTypeRepairBay) ?? Place(s, "repair_bay");
            if (bay.ConstructionState != BuildingConstructionState.Operational)
            {
                bay.ConstructionState = BuildingConstructionState.Operational;
                bay.Health = BuildingOps.MaxDurability(bay.BuildingTypeId);
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            bool bayPowered = bay.PowerState == BuildingPowerState.Powered;
            MachineRecord m = HomeMachines().First();
            MachineRegistry.ApplyDamage(m.LogicId, m.MaxHealth * 0.4f);
            StandingRuleRecord r = New(s, StandingRuleService.KindRepair);
            bool on = r.Enabled && r.Threshold == 50;
            Seconds(65f);
            bool belowThreshold = OrderOf(s, WorkOrderKind.MachineRepair) == null;
            MachineRegistry.ApplyDamage(m.LogicId, m.MaxHealth * 0.3f);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            WorkOrderRecord o = OrderOf(s, WorkOrderKind.MachineRepair, x => x.AssignedMachineLogicId == m.LogicId);
            bool sent = o != null && o.TargetId == bay.BuildingId && o.RuleSerial == r.Serial && Logs(s, "rules.log.repair_send", r.Serial) == 1;
            bool arrived = StepUntil(() => o.State == WorkOrderState.InProgress, 120);
            bool onBay = arrived && StandingRuleService.IsOnRepairBay(s, m.LogicId) && BuildingStatusService.Evaluate(s, bay).Kind == BuildingStatusKind.Working;
            // 修理中拒绝接入（DEBT-FG1SIG03-01：“目标在维修台上”第一次有真实来源）。
            UplinkFailure uf = SignalUplinkService.Validate(s, m.LogicId, out string reason);
            bool refused = uf == UplinkFailure.OnRepairBay && reason != null && reason.Contains("维修台");
            float h0 = m.Health;
            bool healed = StepUntil(() => o.State == WorkOrderState.Completed, 120) && Mathf.Approximately(m.Health, m.MaxHealth);
            bool done = healed && Logs(s, "rules.log.repair_done", r.Serial) == 1 && !StandingRuleService.IsOnRepairBay(s, m.LogicId)
                        && HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId)?.Kind != WorkOrderKind.MachineRepair;
            // 没有能用的维修台：写明原因（只提醒一次），不派单。
            MachineRecord m2 = HomeMachines().Skip(1).FirstOrDefault() ?? m;
            bay.ConstructionState = BuildingConstructionState.Disabled;
            HomeValleyPowerGrid.Recompute(s);
            MachineRegistry.ApplyDamage(m2.LogicId, m2.MaxHealth * 0.7f);
            int notes0 = NotifyCount("standing_rule");
            StandingRuleService.RequestEvaluation(s);
            Tick();
            Seconds(61f);
            bool noBay = r.IssueKey == "rules.issue.no_bay" && OrderOf(s, WorkOrderKind.MachineRepair, x => x.AssignedMachineLogicId == m2.LogicId) == null
                         && NotifyCount("standing_rule") == notes0 + 1;
            Expect(bayPowered && on && belowThreshold && sent && arrived && onBay && refused && healed && done && noBay,
                $"D5 机器维修：伤势 40%（未超 50%）不送；70% 时送去维修台（{BuildingStatusService.Evaluate(s, bay).Kind}），机器自己走到、修理中维修台显示“正在修理”、接入被拒（{reason}）；" +
                $"耐久从 {h0:0} 修满后回到闲置；没有能用的维修台时写明原因、只提醒一次（{bayPowered}/{on}/{belowThreshold}/{sent}/{arrived}/{onBay}/{refused}/{healed}/{done}/{noBay}）");
        }

        /// <summary>家园里所有废料：库存 + 地面物 + 机器货舱（物品守恒断言用）。</summary>
        private static int ScrapEverywhere(CampaignState s) =>
            Mathf.FloorToInt(s.Scrap)
            + (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null && g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount)
            + MachineRegistry.AllRecords.Where(x => x?.Cargo != null).Sum(x => x.Cargo.Where(c => c != null && c.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(c => c.Amount));

        /// <summary>让机器 <paramref name="m"/> 接一张搬运单并已经拾取（货在货舱里，搬运单在途）。</summary>
        private static WorkOrderRecord HaulInFlight(CampaignState s, MachineRecord m, int amount, string tag)
        {
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, m.WorldPosition + new Vector2(1.5f, 0f), CampaignEconomyLedger.ResourceScrap, amount, "rules-selfcheck-" + tag);
            HomeValleyWorkOrders.WorkOrderOpResult res = HomeValleyWorkOrders.TryCreateHaul(s, g.GroundItemId, m.LogicId);
            if (!res.Success || !HomeValleyWorkOrders.OnArrivedAtHaulSource(s, res.WorkOrderId))
            {
                return null;
            }
            return HomeValleyWorkOrders.Find(s, res.WorkOrderId);
        }

        /// <summary>
        /// D8（审查修复 P1 负向）：规则把正在搬运（货已在货舱里）的机器调去送修 / 驻防时，搬运单按取消处理——货放回机器脚下的地面、可以再被搬走；
        /// 不能留在货舱里没人负责（之后接别的活会被覆盖而凭空消失）。家园废料总数守恒（FGT-ECO-001）。
        /// </summary>
        private static void CheckRuleTakesHaulingMachine()
        {
            CampaignState s = NewWorld(6165);
            ClearRules(s);
            BuildingRecord bay = Home(s, HomeValleyLayout.BuildingTypeRepairBay) ?? Place(s, "repair_bay");
            if (bay.ConstructionState != BuildingConstructionState.Operational)
            {
                bay.ConstructionState = BuildingConstructionState.Operational;
                bay.Health = BuildingOps.MaxDurability(bay.BuildingTypeId);
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            MachineRecord m = ms.First();
            MachineRecord m2 = ms.Skip(1).FirstOrDefault();
            WorkOrderRecord haul = HaulInFlight(s, m, 12, "d8a");
            int total0 = ScrapEverywhere(s);
            bool carrying = haul != null && haul.State == WorkOrderState.InProgress && m.Cargo.Length == 1 && m.Cargo[0].Amount == 12;
            // 送修：伤势 70% > 50%，规则在下一个检查步把它调走。
            MachineRegistry.ApplyDamage(m.LogicId, m.MaxHealth * 0.7f);
            StandingRuleRecord r = New(s, StandingRuleService.KindRepair);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            WorkOrderRecord rep = OrderOf(s, WorkOrderKind.MachineRepair, x => x.AssignedMachineLogicId == m.LogicId);
            bool repairDropped = carrying && rep != null && haul.State == WorkOrderState.Cancelled && (m.Cargo == null || m.Cargo.Length == 0)
                                 && (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap && g.Amount == 12
                                     && Vector2.Distance(g.Position, m.WorldPosition) < 4f)
                                 && ScrapEverywhere(s) == total0;
            string dbgA = $"搬运单 {haul?.State}，货舱 {m.Cargo?.Length ?? 0}，送修单 {rep?.State}，废料 {total0}→{ScrapEverywhere(s)}";
            // 驻防：第二台机器搬运途中，静默夜预案把它调去驻防点。
            bool garrisonDropped = true;
            string dbgB = "只有一台机器，跳过驻防";
            if (m2 != null)
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
                WorkOrderRecord haul2 = HaulInFlight(s, m2, 7, "d8b");
                int total1 = ScrapEverywhere(s);
                StandingRuleRecord sn = New(s, StandingRuleService.KindSilent);
                StandingRuleService.TryAddMachine(s, sn.Serial, m2.LogicId, out _);
                StandingRuleService.TrySetEnabled(s, sn.Serial, true, out _);
                long start = GameClock.Ticks + GameClock.StepHz * 10L;
                StandingRuleService.SilentNightWindowProvider = _ => (start, start + GameClock.StepHz * 200L);
                StandingRuleService.RequestEvaluation(s);
                Tick();
                WorkOrderRecord gar = OrderOf(s, WorkOrderKind.Garrison, x => x.AssignedMachineLogicId == m2.LogicId);
                garrisonDropped = haul2 != null && gar != null && haul2.State == WorkOrderState.Cancelled && (m2.Cargo == null || m2.Cargo.Length == 0)
                                  && ScrapEverywhere(s) == total1;
                dbgB = $"搬运单 {haul2?.State}，货舱 {m2.Cargo?.Length ?? 0}，驻防单 {gar?.State}，废料 {total1}→{ScrapEverywhere(s)}";
                StandingRuleService.SilentNightWindowProvider = null;
            }
            Expect(repairDropped && garrisonDropped,
                $"D8 规则调走正在搬运的机器（负向，FGT-ECO-001 守恒）：送修 / 驻防前搬运单取消，货舱里的废料放回机器脚下的地面（之后可再搬），家园废料总数不变" +
                $"（{repairDropped}/{garrisonDropped}；送修：{dbgA}；驻防：{dbgB}）");
        }

        private static void CheckExpeditionUnload()
        {
            CampaignState s = NewWorld(6171, scrap: 20);
            StandingRuleRecord r = StandingRuleService.Ordered(s).First(x => x.Kind == StandingRuleService.KindUnload);
            MachineRecord m = HomeMachines().First();
            m.Cargo = new[] { new CargoEntry { ResourceType = CampaignEconomyLedger.ResourceScrap, Amount = 15 } };
            int scrap0 = Mathf.FloorToInt(s.Scrap);
            StandingRuleService.OnExpeditionReturned(s, new[] { m.LogicId });
            Tick();
            bool unloaded = m.Cargo.Length == 0 && Mathf.FloorToInt(s.Scrap) == scrap0 + 15 && Logs(s, "rules.log.unload", r.Serial) == 1 && r.FireCount == 1;
            StandingRuleService.OnExpeditionReturned(s, new[] { m.LogicId });
            Tick();
            bool empty = Logs(s, "rules.log.unload_empty", r.Serial) == 1;
            // 玩家关掉远征卸货：货留在货舱里。
            StandingRuleService.TrySetEnabled(s, r.Serial, false, out _);
            m.Cargo = new[] { new CargoEntry { ResourceType = CampaignEconomyLedger.ResourceScrap, Amount = 5 } };
            StandingRuleService.OnExpeditionReturned(s, new[] { m.LogicId });
            Tick();
            bool kept = m.Cargo.Length == 1 && m.Cargo[0].Amount == 5 && Logs(s, "rules.log.unload") == 1 && Logs(s, "rules.log.unload_empty") == 1;
            Expect(unloaded && empty && kept, $"D6 远征卸货（默认开启）：远征队返回的下一个模拟步把货舱里的 15 废料卸进共用库存并写日志；没货时写“没有需要卸下的货物”；关掉后货留在货舱（{unloaded}/{empty}/{kept}）");
        }

        private static void CheckAutoRebuild()
        {
            CampaignState s = NewWorld(6181, scrap: 600);
            ClearRules(s);
            BuildingRecord f = Place(s, "refinery_furnace");
            StandingRuleRecord r = New(s, StandingRuleService.KindRebuild);
            bool on = r.Enabled && r.Targets.Length == 0;
            BuildingOps.ApplyDamage(s, f.BuildingId, 1000f);
            Tick();
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId);
            bool ordered = f.ConstructionState == BuildingConstructionState.Damaged && o != null && o.RuleSerial == r.Serial && LogsAt(s, "rules.log.rebuild", r.Serial, f.BuildingId) == 1
                           && StandingRuleService.DescribeBuilding(s, f.BuildingId).Contains("R" + r.Serial);
            bool rebuilt = StepUntil(() => f.ConstructionState == BuildingConstructionState.Operational, 240);
            // 范围：只重建精炼炉的规则不管零件工坊。
            StandingRuleService.TryAddTarget(s, r.Serial, "refinery_furnace", out _);
            BuildingRecord ws = Place(s, "parts_workshop");
            BuildingOps.ApplyDamage(s, ws.BuildingId, 1000f);
            Tick();
            bool scoped = HomeValleyWorkOrders.FindActiveRepair(s, ws.BuildingId) == null;
            // 材料不够：写明原因、不派单；废料够了在下一次定时检查自动派单。
            s.Scrap = 0;
            BuildingOps.ApplyDamage(s, f.BuildingId, 1000f);
            Tick();
            bool waiting = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) == null && LogsAt(s, "rules.log.rebuild_wait", r.Serial, f.BuildingId) == 1 && r.IssueKey == "rules.issue.rebuild_wait";
            // 等材料期间、没到检查步：排队的重试原样留着，不每个模拟步重建列表 / 数组（审查修复 P2，热更层每步不产生垃圾）。
            RuleEventRecord[] pend0 = s.StandingRules.Pending;
            long room = s.StandingRules.NextCheckTick - GameClock.Ticks;
            int quietSteps = (int)Math.Max(0L, Math.Min(10L, room - 2));
            WorldSimulation.StepMany(quietSteps);
            bool noChurn = quietSteps > 0 && pend0.Length == 1 && ReferenceEquals(pend0, s.StandingRules.Pending);
            s.Scrap = 500;
            bool retried = StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId) != null, 70) && r.IssueKey.Length == 0 && LogsAt(s, "rules.log.rebuild_wait", r.Serial, f.BuildingId) == 1;
            Expect(on && ordered && rebuilt && scoped && waiting && noChurn && retried,
                $"D7 自动重建：建筑被摧毁的下一个模拟步派重建单（写“由规则 R{r.Serial} 触发”）并由机器按原样重建；范围只含精炼炉时不管零件工坊；废料为 0 时写明原因不派单，" +
                $"等材料期间不到检查步不重建排队数组（{quietSteps} 步）；废料够了在下一次检查自动派单（{on}/{ordered}/{rebuilt}/{scoped}/{waiting}/{noChurn}/{retried}）");
        }

        // ── E 冲突 ───────────────────────────────────────────────────────────────

        private static void CheckConflicts()
        {
            CampaignState s = NewWorld(6191);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "structural", out _);
            StandingRuleRecord a = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, a.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, a.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, a.Serial, true, out _);
            StandingRuleRecord b = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, b.Serial, "part", out _);
            StandingRuleService.TrySetFactory(s, b.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, b.Serial, true, out _);
            string staticWarn = StandingRuleService.StaticConflictText(s, b);
            int notes0 = NotifyCount("standing_rule");
            Tick();
            bool first = P(s, ws).Recipe?.Id == "repair_kit" && b.ConflictWith == a.Serial && a.ConflictWith == 0 && Logs(s, "rules.log.conflict", b.Serial) == 1
                         && NotifyCount("standing_rule") == notes0 + 1 && staticWarn != null && staticWarn.Contains("R" + a.Serial)
                         && StandingRuleService.ConflictText(s, b).Contains("R" + a.Serial);
            // 冲突持续：不重复写日志 / 不重复提醒。
            Seconds(61f);
            bool quiet = Logs(s, "rules.log.conflict", b.Serial) == 1 && NotifyCount("standing_rule") == notes0 + 1;
            // 把 b 提到前面：下一步翻转（a 放手、恢复它接管前的配方，b 接管）。
            StandingRuleService.TryMove(s, b.Serial, -1, out _);
            Tick();
            bool flipped = b.Priority == 1 && a.Priority == 2 && P(s, ws).Recipe?.Id == "part" && a.ConflictWith == b.Serial && b.ConflictWith == 0;
            // 战时预案也要这座工厂（暂停），优先级更高 → 工厂被暂停，库存维持让出。
            StandingRuleRecord war = New(s, StandingRuleService.KindWar);
            StandingRuleService.TryAddTarget(s, war.Serial, ws.BuildingId, out _);
            StandingRuleService.TryMove(s, war.Serial, -1, out _);
            StandingRuleService.TryMove(s, war.Serial, -1, out _);
            StandingRuleService.RaidActiveProvider = _ => true;
            Tick();
            bool warWins = war.Priority == 1 && BuildingOps.IsDisabled(ws) && b.ConflictWith == war.Serial;
            StandingRuleService.RaidActiveProvider = _ => false;
            StandingRuleService.RequestEvaluation(s);
            Tick();
            bool back = !BuildingOps.IsDisabled(ws) && P(s, ws).Recipe?.Id == "part" && b.ConflictWith == 0;
            Expect(first && quiet && flipped && warWins && back,
                $"E 两条规则争同一座工厂（负向）：配置层面就显示冲突警告；按优先级 R{a.Serial} 执行（排产维修件）、R{b.Serial} 让出并显示“与 R{a.Serial} 争……”、写一条日志与一条警告（持续不刷屏）；" +
                $"上移后翻转；更高优先级的战时预案暂停这座工厂时库存维持让出，突袭结束后库存维持重新接管（{first}/{quiet}/{flipped}/{warWins}/{back}）");
            StandingRuleService.RaidActiveProvider = null;
        }

        // ── F 规则引用的建筑被拆除 ─────────────────────────────────────────────────

        private static void CheckTargetDemolished()
        {
            CampaignState s = NewWorld(6201);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            StandingRuleRecord r = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, r.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            bool held = StandingRuleService.Holds(s).Count == 1;
            // 真实拆除：机器上门拆（工单），拆完建筑消失。
            MachineRecord m = HomeMachines().First();
            HomeValleyWorkOrders.WorkOrderOpResult demo = HomeValleyWorkOrders.TryCreateDemolishBuilding(s, ws.BuildingId, m.LogicId);
            WorkOrderRecord d = demo.Success ? HomeValleyWorkOrders.Find(s, demo.WorkOrderId) : null;
            int notes0 = NotifyCount("standing_rule");
            bool gone = d != null && StepUntil(() => HomeGridService.FindBuilding(s, ws.BuildingId) == null, 240);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            bool reason = r.IssueKey == "rules.issue.target_missing" && StandingRuleService.IssueText(r).Contains("零件工坊") && StandingRuleService.Holds(s).Count == 0
                          && NotifyCount("standing_rule") == notes0 + 1 && Logs(s, "rules.log.issue", r.Serial) == 1;
            Seconds(61f);
            bool quiet = Logs(s, "rules.log.issue", r.Serial) == 1;
            StandingRuleService.TrySetEnabled(s, r.Serial, false, out _);
            bool refuse = !StandingRuleService.TrySetEnabled(s, r.Serial, true, out string why) && why.Contains("不存在");
            string row = StandingRuleService.ConditionText(s, r) + StandingRuleService.ActionText(s, r);
            Expect(held && gone && reason && quiet && refuse && row.Contains("零件工坊"),
                $"F 规则引用的建筑被拆除（负向）：拆完后规则不报错、放手，原因写“目标「零件工坊 #…」已不存在”，提醒一次不刷屏；停用后再启用被拒并写明（{held}/{gone}/{reason}/{quiet}/{refuse}；{why}）");
        }

        /// <summary>
        /// F2（审查修复 P1 负向）：多目标规则里一个目标被拆 / 被摧毁——只跳过那一个、写明原因并提醒一次，其余目标与维修优先照常；
        /// 全部目标都没了才整条不能执行。战时预案：突袭中拆掉暂停的 A，B 继续暂停、规则仍在执行、不写“突袭结束”、新开的维修单照样提到最高；
        /// 阈值补给：一座工坊被摧毁，它的补给单取消退回，另一座的补给单不受影响。
        /// </summary>
        private static void CheckMultiTargetPartlyMissing()
        {
            CampaignState s = NewWorld(6205);
            ClearRules(s);
            BuildingRecord a = Place(s, "parts_workshop");
            BuildingRecord b = Place(s, "parts_workshop");
            BuildingRecord f = Place(s, "refinery_furnace");
            StandingRuleRecord war = New(s, StandingRuleService.KindWar);
            StandingRuleService.TryAddTarget(s, war.Serial, a.BuildingId, out _);
            StandingRuleService.TryAddTarget(s, war.Serial, b.BuildingId, out _);
            StandingRuleService.RaidActiveProvider = _ => true;
            Tick();
            bool paused = BuildingOps.IsDisabled(a) && BuildingOps.IsDisabled(b) && war.Active;
            // 突袭中拆掉 A 的真实路径：暂停中的建筑不能拆（拆除模式写“忙碌”），玩家先手动启用它（规则让位），再让机器上门拆。
            BuildingOps.TrySetEnabled(s, a.BuildingId, true, out _);
            MachineRecord m = HomeMachines().First();
            HomeValleyWorkOrders.WorkOrderOpResult demo = HomeValleyWorkOrders.TryCreateDemolishBuilding(s, a.BuildingId, m.LogicId);
            int notes0 = NotifyCount("standing_rule");
            bool gone = demo.Success && StepUntil(() => HomeGridService.FindBuilding(s, a.BuildingId) == null, 240);
            bool bHeldMeanwhile = BuildingOps.IsDisabled(b);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            Seconds(61f);
            bool keepsB = gone && bHeldMeanwhile && BuildingOps.IsDisabled(b) && war.Active && war.Enabled && Logs(s, "rules.log.war_off", war.Serial) == 0
                          && war.IssueKey == "rules.issue.target_missing" && StandingRuleService.IssueText(war).Contains("零件工坊")
                          && NotifyCount("standing_rule") == notes0 + 1 && Logs(s, "rules.log.issue", war.Serial) == 1;
            string dbgKeep = $"拆除 {demo.Success}/{gone}（{demo.FailureReason}），B 暂停 {BuildingOps.IsDisabled(b)}，执行中 {war.Active}，启用 {war.Enabled}，原因 {war.IssueKey}「{StandingRuleService.IssueText(war)}」，" +
                             $"war_off {Logs(s, "rules.log.war_off", war.Serial)}，提醒 {NotifyCount("standing_rule") - notes0}，原因日志 {Logs(s, "rules.log.issue", war.Serial)}";
            Give(s, BuildingOps.RepairKitId, 10);
            BuildingOps.ApplyDamage(s, f.BuildingId, 50f);
            BuildingOps.TryOrderRepair(s, f.BuildingId, out _);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            WorkOrderRecord rep = HomeValleyWorkOrders.FindActiveRepair(s, f.BuildingId);
            bool boosted = rep != null && rep.Priority == StandingRuleService.WarRepairPriority && rep.RuleSerial == war.Serial;
            // 只暂停建筑、不提优先级的预案：目标全没了才整条不能执行（启用被拒并写明）。
            StandingRuleService.TrySetBoost(s, war.Serial, false, out _);
            StandingRuleService.TryRemoveTarget(s, war.Serial, b.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, war.Serial, false, out _);
            bool allGoneRefused = !StandingRuleService.TrySetEnabled(s, war.Serial, true, out string why) && why.Contains("不存在");
            StandingRuleService.RaidActiveProvider = null;
            string dbgWar = $"{dbgKeep}；维修单 {rep?.Priority}/{rep?.RuleSerial}；{why}";

            // 阈值补给：两座工坊各开一张补给单，其中一座被摧毁。
            CampaignState s2 = NewWorld(6206);
            ClearRules(s2);
            BuildingRecord w1 = Place(s2, "parts_workshop");
            BuildingRecord w2 = Place(s2, "parts_workshop");
            ProductionService.TrySetRecipe(s2, w1.BuildingId, "part", out _);
            ProductionService.TrySetRecipe(s2, w2.BuildingId, "part", out _);
            Give(s2, "alloy", 30);
            StandingRuleRecord sup = New(s2, StandingRuleService.KindSupply);
            StandingRuleService.TrySetItem(s2, sup.Serial, "alloy", out _);
            StandingRuleService.TrySetThreshold(s2, sup.Serial, 5, out _);
            StandingRuleService.TrySetBatch(s2, sup.Serial, 4, out _);
            StandingRuleService.TryAddTarget(s2, sup.Serial, w1.BuildingId, out _);
            StandingRuleService.TryAddTarget(s2, sup.Serial, w2.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s2, sup.Serial, true, out _);
            Tick();
            WorkOrderRecord o1 = OrderOf(s2, WorkOrderKind.Deliver, x => x.TargetId == w1.BuildingId);
            WorkOrderRecord o2 = OrderOf(s2, WorkOrderKind.Deliver, x => x.TargetId == w2.BuildingId);
            int r1 = o1?.ReservedItemAmount ?? -1;
            int r2 = o2?.ReservedItemAmount ?? -1;
            int stock1 = Stock(s2, "alloy");
            bool twoOrders = o1 != null && o2 != null && HomeValleyWorkOrders.IsActive(o1) && HomeValleyWorkOrders.IsActive(o2) && r1 > 0 && r2 > 0 && stock1 == 30 - r1 - r2;
            BuildingOps.ApplyDamage(s2, w1.BuildingId, 1000f);
            // 直接量规则这一次检查的效果（之后的模拟步里仓库输出口会按批把库存推上传送带缓存，属正常物流，不混进这条断言）。
            int stockDmg = Stock(s2, "alloy");
            StandingRuleService.Evaluate(s2);
            int stockEval = Stock(s2, "alloy");
            bool refunded = !HomeValleyWorkOrders.IsActive(o1) && stockEval == stockDmg + r1;
            Tick();
            bool supplyKeeps = twoOrders && refunded && w1.ConstructionState == BuildingConstructionState.Damaged && HomeValleyWorkOrders.IsActive(o2) && o2.ReservedItemAmount == r2
                               && sup.Enabled && sup.IssueKey == "rules.issue.target_missing" && StandingRuleService.IssueText(sup).Contains("零件工坊")
                               && StandingRuleService.FindHold(s2, "s:" + sup.Serial.ToString(CultureInfo.InvariantCulture) + ":" + w2.BuildingId) != null
                               && StandingRuleService.FindHold(s2, "s:" + sup.Serial.ToString(CultureInfo.InvariantCulture) + ":" + w1.BuildingId) == null;
            string dbgSup = $"单1 {o1?.State}，单2 {o2?.State}，原因 {sup.IssueKey}，合金 {stock1}，摧毁后 {stockDmg} → 检查后 {stockEval}（预留 {r1}/{r2}）";
            Expect(paused && keepsB && boosted && allGoneRefused && supplyKeeps,
                "F2 多目标规则的一个目标被拆 / 被摧毁（负向）：战时预案突袭中拆掉 A——B 继续暂停、规则仍在执行、不写“突袭结束”、原因写明 A 已不存在并只提醒一次、新开的维修单仍提到最高；" +
                "目标全没了且不提优先级时才不能启用；阈值补给一座被摧毁——它的补给单取消退回，另一座照常" +
                $"（{paused}/{keepsB}/{boosted}/{allGoneRefused}/{supplyKeeps}；战时：{dbgWar}；补给：{dbgSup}）");
        }

        // ── G 玩家手动改动让位 ─────────────────────────────────────────────────────

        private static void CheckPlayerOverride()
        {
            CampaignState s = NewWorld(6211);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            StandingRuleRecord r = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, r.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            ProductionService.TrySetRecipe(s, ws.BuildingId, "structural", out _);
            Seconds(61f);
            RuleHoldRecord h = StandingRuleService.FindHold(s, StandingRuleService.BuildingKey(ws.BuildingId));
            bool yielded = P(s, ws).Recipe?.Id == "structural" && h != null && h.Overridden && Logs(s, "rules.log.override", r.Serial) == 1
                           && StandingRuleService.DescribeBuilding(s, ws.BuildingId).Contains("改动");
            Seconds(61f);
            bool stillYielded = P(s, ws).Recipe?.Id == "structural" && Logs(s, "rules.log.override", r.Serial) == 1;
            // 条件解除（补足）：放手、不改回玩家的配方；下次不足再接管。
            Give(s, "repair_kit", 100);
            Seconds(61f);
            bool released = StandingRuleService.Holds(s).Count == 0 && P(s, ws).Recipe?.Id == "structural";
            Take(s, "repair_kit");
            bool retake = StepUntil(() => P(s, ws).Recipe?.Id == "repair_kit", 70);
            // 驻防机器被玩家接管（接入 / 下命令）= 取消、这次不再派。
            StandingRuleRecord sn = New(s, StandingRuleService.KindSilent);
            MachineRecord m = HomeMachines().First();
            StandingRuleService.TryAddMachine(s, sn.Serial, m.LogicId, out _);
            StandingRuleService.TrySetEnabled(s, sn.Serial, true, out _);
            long start = GameClock.Ticks + GameClock.StepHz * 10L;
            StandingRuleService.SilentNightWindowProvider = _ => (start, start + GameClock.StepHz * 200L);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            WorkOrderRecord g = OrderOf(s, WorkOrderKind.Garrison, x => x.AssignedMachineLogicId == m.LogicId);
            HomeValleyWorkOrders.OnMachinePossessed(s, m.LogicId);
            Seconds(65f);
            string gdbg = $"单 {g?.State}，驻防单 {(s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(x => x.Kind == WorkOrderKind.Garrison && x.RuleSerial == sn.Serial)}，持有 {StandingRuleService.FindHold(s, StandingRuleService.MachineKey(m.LogicId))?.Overridden}，执行中 {sn.Active}，原因 {sn.IssueKey}";
            bool noResend = g != null && g.State == WorkOrderState.Cancelled
                            && (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(x => x.Kind == WorkOrderKind.Garrison && x.RuleSerial == sn.Serial) == 1
                            && StandingRuleService.FindHold(s, StandingRuleService.MachineKey(m.LogicId))?.Overridden == true;
            Expect(yielded && stillYielded && released && retake && noResend,
                $"G 玩家手动改动让位（FGR-BASE-020 不和玩家抢）：规则接管的工厂被玩家改成“结构材”后规则不改回、标“你改动过”并只写一条日志；补足后放手不动玩家的配方，下次不足再接管；" +
                $"驻防的机器被玩家接管后单子取消、这一夜不再派（{yielded}/{stillYielded}/{released}/{retake}/{noResend}；{gdbg}）");
            StandingRuleService.SilentNightWindowProvider = null;
        }

        private static string RememberedRecipe(CampaignState s, string typeId) =>
            (s.Economy?.RecipeMemory ?? Array.Empty<RecipeMemoryRecord>()).FirstOrDefault(x => x != null && x.TypeId == typeId)?.RecipeId;

        /// <summary>
        /// G2（审查修复 P2）：库存维持接管期间玩家在面板换了规则的物品（配方跟着自动换）——工厂跟着切到新配方，不算“你手动改过”、不写让位日志；
        /// 规则的接管与恢复都不改写“这类建筑记住的上一次配方”（新建的同类建筑仍沿用玩家自己的选择，FGR-BASE-020）。
        /// </summary>
        private static void CheckRuleEditIsNotOverride()
        {
            CampaignState s = NewWorld(6213);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            bool remembered = RememberedRecipe(s, "parts_workshop") == "part";
            StandingRuleRecord r = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, r.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 50, out _);
            Take(s, "repair_kit");
            Take(s, "structural");
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            bool took = P(s, ws).Recipe?.Id == "repair_kit" && RememberedRecipe(s, "parts_workshop") == "part";
            // 接管期间换物品：配方自动换成“结构材”，工厂下一步跟着切换。
            StandingRuleService.TrySetItem(s, r.Serial, "structural", out _);
            bool autoRecipe = r.RecipeId == "structural";
            Tick();
            RuleHoldRecord h = StandingRuleService.FindHold(s, StandingRuleService.BuildingKey(ws.BuildingId));
            Seconds(61f);
            bool followed = autoRecipe && P(s, ws).Recipe?.Id == "structural" && h != null && !h.Overridden && h.Prev == "part" && h.Applied == "structural"
                            && Logs(s, "rules.log.override", r.Serial) == 0 && StandingRuleService.DescribeBuilding(s, ws.BuildingId)?.Contains("改动") != true
                            && RememberedRecipe(s, "parts_workshop") == "part";
            // 补足：恢复接管前（玩家自己选的）配方，记住的配方仍是玩家的选择。
            Give(s, "structural", 1000);
            bool restored = StepUntil(() => P(s, ws).Recipe?.Id == "part", 70) && StandingRuleService.Holds(s).Count == 0 && RememberedRecipe(s, "parts_workshop") == "part";
            // 新建的同类建筑沿用玩家的选择，不是规则临时排产的配方。
            BuildingRecord ws2 = Place(s, "parts_workshop");
            bool inherits = ws2 != null && P(s, ws2).Recipe?.Id == "part";
            string dbg = $"工厂 {P(s, ws).Recipe?.Id}，持有 {h?.Overridden}/{h?.Prev}/{h?.Applied}，让位日志 {Logs(s, "rules.log.override", r.Serial)}，记住 {RememberedRecipe(s, "parts_workshop")}，新建 {(ws2 != null ? P(s, ws2).Recipe?.Id : "-")}";
            Expect(remembered && took && followed && restored && inherits,
                "G2 改规则不算玩家改动（FGR-ECO-031 追溯不错算到玩家头上）：接管期间换物品后工厂跟着切到“结构材”、不标“你手动改过”、不写让位日志；补足后恢复玩家自己的“零件”；" +
                $"规则接管 / 恢复都不改写这类建筑记住的配方，新建的零件工坊沿用“零件”（{remembered}/{took}/{followed}/{restored}/{inherits}；{dbg}）");
        }

        // ── H 停用 / 删除恢复原样 ─────────────────────────────────────────────────

        private static void CheckDisableDeleteRestore()
        {
            CampaignState s = NewWorld(6221);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            BuildingRecord st = Place(s, "energy_storage");
            StandingRuleRecord war = New(s, StandingRuleService.KindWar);
            StandingRuleService.TryAddTarget(s, war.Serial, ws.BuildingId, out _);
            StandingRuleService.RaidActiveProvider = _ => true;
            Tick();
            bool paused = BuildingOps.IsDisabled(ws);
            StandingRuleService.TrySetEnabled(s, war.Serial, false, out string msg);
            bool resumed = !BuildingOps.IsDisabled(ws) && msg.Contains("恢复") && StandingRuleService.Holds(s).Count == 0;
            Seconds(61f);
            bool stays = !BuildingOps.IsDisabled(ws);
            // 玩家自己禁用的建筑：突袭开始时规则不碰，突袭结束也不替玩家启用。
            BuildingOps.TrySetEnabled(s, ws.BuildingId, false, out _);
            StandingRuleService.TrySetEnabled(s, war.Serial, true, out _);
            Tick();
            bool notMine = StandingRuleService.Holds(s).Count == 0;
            StandingRuleService.RaidActiveProvider = _ => false;
            Seconds(61f);
            bool playerKept = BuildingOps.IsDisabled(ws);
            // 删除：静默夜预案改过的储能站恢复原设置。
            StandingRuleRecord sn = New(s, StandingRuleService.KindSilent);
            StandingRuleService.TryAddTarget(s, sn.Serial, st.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, sn.Serial, true, out _);
            long start = GameClock.Ticks + GameClock.StepHz * 5L;
            StandingRuleService.SilentNightWindowProvider = _ => (start, start + GameClock.StepHz * 300L);
            StandingRuleService.RequestEvaluation(s);
            Tick();
            bool changed = HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).NoCharge;
            bool deleted = StandingRuleService.TryDelete(s, sn.Serial, out _) && !HomeValleyPowerGrid.GetStorageSettings(s, st.BuildingId).NoCharge
                           && StandingRuleService.Find(s, sn.Serial) == null;
            Expect(paused && resumed && stays && notMine && playerKept && changed && deleted,
                $"H 停用 / 删除：停用正在执行的战时预案立刻恢复被暂停的建筑；玩家自己禁用的建筑规则不碰、结束也不替玩家启用；删除静默夜预案时储能站恢复原设置（{paused}/{resumed}/{stays}/{notMine}/{playerKept}/{changed}/{deleted}）");
            StandingRuleService.RaidActiveProvider = null;
            StandingRuleService.SilentNightWindowProvider = null;
        }

        // ── I 复制与常用预设 ───────────────────────────────────────────────────────

        private static void CheckCopyAndPresets()
        {
            CampaignState s = NewWorld(6231);
            BuildingRecord ws = Place(s, "parts_workshop");
            int created = 0;
            bool presetsOk = true;
            foreach (RulePreset p in StandingRuleService.Presets)
            {
                if (!StandingRuleService.TryCreateFromPreset(s, p.Id, out StandingRuleRecord r, out _))
                {
                    presetsOk = false;
                    continue;
                }
                created++;
                presetsOk &= r.Kind == p.Kind && (p.ItemId == "none" || r.ItemId == p.ItemId) && (!StandingRuleService.HasThreshold(p.Kind) || r.Threshold == p.Threshold)
                             && (p.RecipeId == "none" || r.RecipeId == p.RecipeId);
                bool needsTarget = p.Kind == StandingRuleService.KindStock || p.Kind == StandingRuleService.KindSupply || p.Kind == StandingRuleService.KindSilent;
                presetsOk &= needsTarget ? !r.Enabled : r.Enabled;
            }
            StandingRuleRecord keep = StandingRuleService.Ordered(s).First(r => r.Kind == StandingRuleService.KindStock && r.ItemId == "repair_kit");
            StandingRuleService.TrySetFactory(s, keep.Serial, ws.BuildingId, out _);
            bool enable = StandingRuleService.TrySetEnabled(s, keep.Serial, true, out _) && keep.RecipeId == "repair_kit";
            int before = StandingRuleService.Ordered(s).Count;
            bool copied = StandingRuleService.TryCopy(s, keep.Serial, out StandingRuleRecord copy, out string msg);
            List<StandingRuleRecord> order = StandingRuleService.Ordered(s);
            bool copyOk = copied && !copy.Enabled && copy.Serial != keep.Serial && copy.ItemId == keep.ItemId && copy.Threshold == keep.Threshold && copy.Targets.SequenceEqual(keep.Targets)
                          && order.IndexOf(copy) == order.IndexOf(keep) + 1 && order.Count == before + 1 && order.Select(r => r.Priority).SequenceEqual(Enumerable.Range(1, order.Count))
                          && msg.Contains("停用");
            // 数量上限（B12）：超过上限拒绝并写明。
            while (StandingRuleService.Ordered(s).Count < StandingRuleService.MaxRules)
            {
                New(s, StandingRuleService.KindRebuild);
            }
            bool limit = !StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out _, out string why) && why.Contains(StandingRuleService.MaxRules.ToString())
                         && !StandingRuleService.TryCopy(s, keep.Serial, out _, out _);
            Expect(created == StandingRuleService.Presets.Count && presetsOk && enable && copyOk && limit,
                $"I 常用预设 {created} 条各建一条（物品 / 数值 / 配方按预设，要选目标的先停用）；选好工厂后可启用；复制 = 同样设置、新编号、排在正下方、先停用；规则数到上限 {StandingRuleService.MaxRules} 拒绝并写明" +
                $"（{presetsOk}/{enable}/{copyOk}/{limit}）");
        }

        // ── J 追溯与日志 ──────────────────────────────────────────────────────────

        private static void CheckTraceAndLog()
        {
            CampaignState s = NewWorld(6241);
            ClearRules(s);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            StandingRuleRecord r = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, r.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 1, out _);
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            Tick();
            // 建筑面板（真 UXML）显示“由规则 R… 触发”。
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            bool panelLine;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(ws.BuildingId);
                panel.Refresh();
                panelLine = panel.RuleLineText.Contains("R" + r.Serial) && panel.RuleLineText.Contains("维修件");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
            RuleLogRecord last = StandingRuleService.LogEntries(s).Last();
            string text = StandingRuleService.LogText(s, last);
            bool logText = text.Contains("R" + r.Serial) && text.Contains("维修件") && text.Contains("零件工坊") && last.HasPos && !GameText.ContainsMarker(text);
            GameSettings.SetLanguage(GameLanguage.En);
            string en = StandingRuleService.LogText(s, last);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool switches = en.Contains("Repair kit", StringComparison.OrdinalIgnoreCase) || en.Contains("repair", StringComparison.OrdinalIgnoreCase);
            // 日志上限 100：来回 70 次（每次两条），只留最近 100 条，序号连续递增。
            for (int i = 0; i < 70; i++)
            {
                Give(s, "repair_kit", 5);
                StandingRuleService.RequestEvaluation(s);
                Tick();
                Take(s, "repair_kit");
                StandingRuleService.RequestEvaluation(s);
                Tick();
            }
            IReadOnlyList<RuleLogRecord> log = StandingRuleService.LogEntries(s);
            bool capped = log.Count == 100 && log.Zip(log.Skip(1), (a, b) => b.Serial == a.Serial + 1).All(x => x) && log.Last().Serial > 100;
            Expect(panelLine && logText && switches && capped,
                $"J 追溯：建筑面板写“{StandingRuleService.DescribeBuilding(s, ws.BuildingId)}”；触发日志“{text}”带位置可定位、切换英文后同一条跟着翻译；日志只留最近 100 条（{panelLine}/{logText}/{switches}/{capped}）");
        }

        // ── K 存读档 ───────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s))
            {
                sb.Append('R').Append(r.Serial).Append(':').Append(r.Kind).Append('/').Append(r.Enabled).Append('/').Append(r.Priority).Append('/').Append(r.ItemId).Append('/')
                    .Append(r.Threshold).Append('/').Append(r.RecipeId).Append('/').Append(string.Join(",", r.Targets)).Append('/').Append(string.Join(",", r.Machines)).Append('/')
                    .Append(r.Active).Append('/').Append(r.FireCount).Append('/').Append(r.LastFiredTick).Append('/').Append(r.ConflictWith).Append('/').Append(r.IssueKey).Append('|');
            }
            foreach (RuleHoldRecord h in StandingRuleService.Holds(s).OrderBy(h => h.EntityId, StringComparer.Ordinal))
            {
                sb.Append('H').Append(h.Rule).Append(h.Kind).Append(h.EntityId.StartsWith("o:", StringComparison.Ordinal) ? "o" : h.EntityId).Append('/').Append(h.Prev).Append(h.PrevInt).Append(h.Overridden).Append('/').Append(h.Applied).Append('|');
            }
            foreach (RuleLogRecord e in StandingRuleService.LogEntries(s))
            {
                sb.Append('L').Append(e.Serial).Append(e.Key).Append(e.Tick).Append('|');
            }
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId).OrderBy(b => b.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(b.BuildingId).Append(':').Append((int)b.ConstructionState).Append('|');
            }
            foreach (ProducerRecord p in s.Economy.Producers.OrderBy(p => p.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(p.BuildingId).Append('=').Append(p.RecipeId).Append('/').Append(p.Completed).Append('|');
            }
            foreach (WorkOrderRecord o in (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o.RuleSerial > 0).OrderBy(o => o.CreatedTick).ThenBy(o => o.Kind))
            {
                sb.Append('O').Append(o.Kind).Append(o.RuleSerial).Append((int)o.State).Append(o.Priority).Append(o.ReservedItemAmount).Append('|');
            }
            sb.Append("kits=").Append(Stock(s, BuildingOps.RepairKitId)).Append(" alloy=").Append(Stock(s, "alloy")).Append(" scrap=").Append(Mathf.FloorToInt(s.Scrap))
                .Append(" next=").Append(s.StandingRules.NextCheckTick).Append(" pend=").Append(s.StandingRules.Pending.Length);
            return sb.ToString();
        }

        /// <summary>存读档 / 倍速 / 观察共用的场景：库存维持（零件工坊）、阈值补给（第二座工坊送合金）、战时预案（突袭进行中，暂停第三座）、自动重建（被摧毁的精炼炉等废料）。</summary>
        private static bool LayScenario(CampaignState s)
        {
            ClearRules(s);
            BuildingRecord a = Place(s, "parts_workshop");
            BuildingRecord b = Place(s, "parts_workshop");
            BuildingRecord c = Place(s, "parts_workshop");
            BuildingRecord f = Place(s, "refinery_furnace");
            if (a == null || b == null || c == null || f == null)
            {
                return false;
            }
            ProductionService.TrySetRecipe(s, a.BuildingId, "part", out _);
            ProductionService.TrySetRecipe(s, b.BuildingId, "part", out _);
            P(s, a).Rec.In = new[] { new ItemStackRecord { ItemId = "part", Amount = 12 }, new ItemStackRecord { ItemId = "alloy", Amount = 4 } };
            Give(s, "alloy", 25);
            StandingRuleRecord keep = New(s, StandingRuleService.KindStock);
            StandingRuleService.TrySetItem(s, keep.Serial, "repair_kit", out _);
            StandingRuleService.TrySetFactory(s, keep.Serial, a.BuildingId, out _);
            StandingRuleService.TrySetThreshold(s, keep.Serial, 2, out _);
            StandingRuleService.TrySetEnabled(s, keep.Serial, true, out _);
            StandingRuleRecord sup = New(s, StandingRuleService.KindSupply);
            StandingRuleService.TrySetItem(s, sup.Serial, "alloy", out _);
            StandingRuleService.TryAddTarget(s, sup.Serial, b.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, sup.Serial, true, out _);
            StandingRuleRecord war = New(s, StandingRuleService.KindWar);
            StandingRuleService.TryAddTarget(s, war.Serial, c.BuildingId, out _);
            New(s, StandingRuleService.KindRebuild);
            s.Raids.InTransit = new[] { new TransitGroupRecord { GroupId = "transit-test", Kind = TransitGroupKind.Raid, State = TransitGroupState.Arrived, UnitCount = 2,
                PosX = HomeValleyLayout.Core.Position.x, PosY = HomeValleyLayout.Core.Position.y, ArrivedAtTick = 1, RouteX = Array.Empty<int>(), RouteY = Array.Empty<int>() } };
            s.Scrap = 0;
            BuildingOps.ApplyDamage(s, f.BuildingId, 1000f);
            return true;
        }

        /// <summary>场景中途：突袭结束、废料到账（自动重建在下一次检查派单）。</summary>
        private static void ContinueScenario(CampaignState s)
        {
            s.Raids.InTransit = Array.Empty<TransitGroupRecord>();
            s.Scrap = 400;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(6251, scrap: 0);
            if (!LayScenario(s))
            {
                Fail("K 放不下场景");
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 30 + 7);
            ContinueScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3 + 1);
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 90);
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
                WorldSimulation.StepMany(GameClock.StepHz * 90);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            bool fields = before.Contains("disabled") && before.Contains("rules.log.") && before.Contains("pend=");
            Expect(save.Success && loaded1 == before && fields,
                "K1 真文件存读档：规则（设置、启用、优先级、执行中、触发次数与时间、冲突、原因）、持有（旧值、让位标记）、触发日志、排队事件、下一次检查的步写进存档，读档后逐字段一致" +
                (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "K2 读档后接着跑 90 游戏秒（库存维持出产与补足恢复、补给送达、突袭结束恢复、自动重建派单与施工），与不存档一直跑逐位一致；同一存档读两次结果一致" +
                (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        /// <summary>
        /// K3（审查修复 P2）：规则在世界模拟步里刚派出送修单、机器的移动要到下一步才发出——存档恰好落在这个空档时，读档后机器按单子出发去维修台，
        /// 不原地不动等看门狗误报“路径受阻”。
        /// </summary>
        private static void CheckSelfOrderLegAfterLoad()
        {
            CampaignState s = NewWorld(6255);
            ClearRules(s);
            BuildingRecord bay = Home(s, HomeValleyLayout.BuildingTypeRepairBay) ?? Place(s, "repair_bay");
            if (bay.ConstructionState != BuildingConstructionState.Operational)
            {
                bay.ConstructionState = BuildingConstructionState.Operational;
                bay.Health = BuildingOps.MaxDurability(bay.BuildingTypeId);
            }
            HomeValleyPowerGrid.Recompute(s);
            Seconds(1f);
            MachineRecord m = HomeMachines().First();
            MachineRegistry.ApplyDamage(m.LogicId, m.MaxHealth * 0.7f);
            New(s, StandingRuleService.KindRepair);
            StandingRuleService.RequestEvaluation(s);
            WorldSimulation.StepMany(1); // 规则在这一步派单；移动在下一步的工单 Tick 才发出。
            WorkOrderRecord o = OrderOf(s, WorkOrderKind.MachineRepair, x => x.AssignedMachineLogicId == m.LogicId);
            bool gap = o != null && o.State == WorkOrderState.Reserved && HomeValleyWorkOrders.PendingLegCount > 0;
            string orderId = o?.WorkOrderId;
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("K3 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            int resumed = HomeValleyController.LastResumedPendingLegs;
            WorkOrderRecord lo = HomeValleyWorkOrders.Find(rr.State, orderId);
            bool arrived = lo != null && StepUntil(() => lo.State == WorkOrderState.InProgress || !HomeValleyWorkOrders.IsActive(lo), 120)
                           && lo.State == WorkOrderState.InProgress && lo.FailureReason != "path-blocked";
            Expect(gap && save.Success && resumed >= 1 && arrived,
                $"K3 存档落在“送修单已派、机器还没出发”的空档：读档后机器按单子重新出发（重发 {resumed} 条）、走到维修台开始修理，没有误报“路径受阻”" +
                $"（{gap}/{save.Success}/{arrived}；单 {lo?.State}/{lo?.FailureReason}）");
        }

        // ── L / M 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 0);
            pausedHeld = true;
            if (!LayScenario(s))
            {
                return "放不下";
            }
            WorldSimulation.StepMany(GameClock.StepHz * 5);
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
            long target = GameClock.Ticks + GameClock.StepHz * 70;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 500)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            ContinueScenario(s);
            target = GameClock.Ticks + GameClock.StepHz * 70;
            while (GameClock.Ticks < target && frames < 60 * 1000)
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
                string snap = RunScenario(6261, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下" && reference.Contains("rules.log.war_off") && reference.Contains("rules.log.rebuild"),
                "L 暂停中（120 帧）规则不检查、不派单、不写日志；0.5x / 1x / 2x / 3x 跑同样的 140 游戏秒（库存维持、补给、突袭与结束、自动重建）规则、持有、日志（含触发时刻）、工单逐字段一致"
                + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6262, true, 1f, false, out _);
            string unseen = RunScenario(6262, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下",
                "M 同一组规则在观察与不观察家园时跑 140 游戏秒逐字段一致（FGR-BASE-021：规则只在世界模拟步里运行）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── N 面板 ───────────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(6271);
            BuildingRecord ws = Place(s, "parts_workshop");
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "RulesPanel.uxml", out GameObject go);
            RulesPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                RulesPanelUIToolkit panel = go.AddComponent<RulesPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RulesRow.uxml"));
                RulesPanelUIToolkit.Open();
                panel.Refresh();
                bool opened = panel.PanelVisible && panel.VisibleRowCount == 1 && panel.RowText(0, "RrId").Contains("远征卸货") && panel.CountText.Contains("1")
                              && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RulesPanelFirstOpen);
                // 从预设新建（下拉框选中即生效）：维修件保底 → 选中、先停用、编辑区显示物品 / 工厂 / 配方 / 数值。
                int presetIndex = panel.NewPresetField.choices.FindIndex(c => c.Contains("维修件保底"));
                RulesPanelUIToolkit.PickForTests(panel.NewPresetField, presetIndex);
                StandingRuleRecord r = StandingRuleService.Find(s, panel.SelectedSerial);
                bool created = r != null && r.Kind == StandingRuleService.KindStock && panel.VisibleRowCount == 2 && panel.EditorRowVisible("RulesRowFactory")
                               && panel.EditorRowVisible("RulesRowThreshold") && !panel.EditorRowVisible("RulesRowMachines") && panel.MessageText.Contains("停用")
                               && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RulesFirstCreated);
                // 选工厂：下拉框里有这座零件工坊。
                int fi = panel.FactoryField.choices.FindIndex(c => c.Contains("零件工坊"));
                RulesPanelUIToolkit.PickForTests(panel.FactoryField, fi);
                bool factory = r.Targets.Length == 1 && r.Targets[0] == ws.BuildingId && r.RecipeId == "repair_kit";
                int t0 = r.Threshold;
                panel.StepThreshold(1);
                bool threshold = r.Threshold >= t0 + 1 && panel.ThresholdText.Contains(r.Threshold.ToString());
                // 启用（行内按钮）。
                int row = Enumerable.Range(0, panel.VisibleRowCount).First(i => panel.RowSerial(i) == r.Serial);
                panel.ToggleRow(row);
                bool enabled = r.Enabled && panel.RowText(row, "RrState").Contains("已启用");
                // 复制、上移、删除（确认框）。
                panel.Copy(row);
                bool copied = panel.VisibleRowCount == 3 && !StandingRuleService.Find(s, panel.SelectedSerial).Enabled;
                int copyRow = Enumerable.Range(0, panel.VisibleRowCount).First(i => panel.RowSerial(i) == panel.SelectedSerial);
                panel.Move(copyRow, -1);
                bool moved = StandingRuleService.Find(s, panel.SelectedSerial).Priority < r.Priority;
                int delRow = Enumerable.Range(0, panel.VisibleRowCount).First(i => panel.RowSerial(i) == panel.SelectedSerial);
                panel.AskDelete(delRow);
                bool asked = panel.PendingDeleteConfirm && UiConfirmDialog.IsOpen && panel.VisibleRowCount == 3;
                UiConfirmDialog.Confirm();
                bool deleted = panel.VisibleRowCount == 2 && !panel.PendingDeleteConfirm;
                // 两条规则争同一座工厂：行上显示冲突警告。
                RulesPanelUIToolkit.PickForTests(panel.NewPresetField, panel.NewPresetField.choices.FindIndex(c => c.Contains("零件保底")));
                StandingRuleRecord r2 = StandingRuleService.Find(s, panel.SelectedSerial);
                RulesPanelUIToolkit.PickForTests(panel.FactoryField, panel.FactoryField.choices.FindIndex(c => c.Contains("零件工坊")));
                panel.ToggleRow(Enumerable.Range(0, panel.VisibleRowCount).First(i => panel.RowSerial(i) == r2.Serial));
                panel.Refresh();
                int r2Row = Enumerable.Range(0, panel.VisibleRowCount).First(i => panel.RowSerial(i) == r2.Serial);
                bool warn = panel.RowWarnVisible(r2Row) && panel.RowText(r2Row, "RrWarn").Contains("R" + r.Serial);
                // 机器维修：编辑区显示机器范围，添加 / 移除机器。
                RulesPanelUIToolkit.PickForTests(panel.NewKindField, panel.NewKindField.choices.FindIndex(c => c.Contains("机器维修")));
                StandingRuleRecord rr = StandingRuleService.Find(s, panel.SelectedSerial);
                bool machineRow = panel.EditorRowVisible("RulesRowMachines") && panel.MachinesText.Contains("任一");
                RulesPanelUIToolkit.PickForTests(panel.MachineAddField, 1);
                bool added = rr.Machines.Length == 1 && panel.MachinesText.Contains("#");
                RulesPanelUIToolkit.PickForTests(panel.MachineRemoveField, 1);
                bool removed = rr.Machines.Length == 0;
                // 日志页：触发后有条目，点一条镜头跳过去。
                Tick();
                panel.ShowLog(true);
                bool log = panel.LogLabelCount >= 1 && panel.LogLabelText(0).Contains("R" + r.Serial) && panel.JumpToLog(0)
                           && Vector2.Distance(panel.LastJumpPosition, ws.Position) < 0.01f;
                panel.ShowLog(false);
                string all = panel.CountText + panel.EditTitleText + panel.EditInfoText + panel.RowText(0, "RrWhen") + panel.RowText(0, "RrThen") + panel.LogCountText;
                bool markers = !GameText.ContainsMarker(all);
                Expect(opened && created && factory && threshold && enabled && copied && moved && asked && deleted && warn && machineRow && added && removed && log && markers,
                    $"N1 常驻规则面板（真 UXML，FGU-15）：打开显示默认规则；从预设新建（下拉框选中即生效）、选工厂自动配方、数值 ±、行内启用、复制、上移、删除先确认；冲突警告；" +
                    $"机器维修的机器范围添加 / 移除；日志页条目可点击定位（{opened}/{created}/{factory}/{threshold}/{enabled}/{copied}/{moved}/{asked}/{deleted}/{warn}/{machineRow}/{added}/{removed}/{log}/{markers}）");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "RulesPanel.uxml", "RulesPanelWindow");
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "N2 布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n')[0]);
                string pauseProbe = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow");
                Expect(!pauseProbe.Contains("FAIL"), "N3 暂停菜单加了“常驻规则”按钮后布局探针仍通过：" + pauseProbe.Split('\n')[0]);
            }
            finally
            {
                RulesPanelUIToolkit.Close();
                RulesPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── O 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6281);
            ClearRules(s);
            var factories = new List<BuildingRecord>();
            for (int i = 0; i < 40; i++)
            {
                BuildingRecord b = Place(s, "parts_workshop");
                if (b == null)
                {
                    break;
                }
                factories.Add(b);
            }
            // 规则数上限：一半库存维持（各管一座工厂）、一半阈值补给（各管一座），全部启用、条件成立。
            int n = 0;
            for (int i = 0; i < factories.Count && n < StandingRuleService.MaxRules - 2; i++, n++)
            {
                StandingRuleRecord r = New(s, i % 2 == 0 ? StandingRuleService.KindStock : StandingRuleService.KindSupply);
                if (r.Kind == StandingRuleService.KindStock)
                {
                    StandingRuleService.TrySetItem(s, r.Serial, "part", out _);
                    StandingRuleService.TrySetFactory(s, r.Serial, factories[i].BuildingId, out _);
                }
                else
                {
                    ProductionService.TrySetRecipe(s, factories[i].BuildingId, "part", out _);
                    StandingRuleService.TrySetItem(s, r.Serial, "alloy", out _);
                    StandingRuleService.TryAddTarget(s, r.Serial, factories[i].BuildingId, out _);
                }
                StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            }
            New(s, StandingRuleService.KindRepair);
            Give(s, "alloy", 50);
            Tick();
            StandingRuleService.ResetStats();
            var sw = Stopwatch.StartNew();
            const int Rounds = 20;
            for (int i = 0; i < Rounds; i++)
            {
                StandingRuleService.Evaluate(s);
            }
            sw.Stop();
            double perEval = sw.Elapsed.TotalMilliseconds / Rounds;
            // 不到检查步的世界步：规则部分只看一个步序号（O(1)）。
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 1000; i++)
            {
                StandingRuleService.WorldStep(s, s.StandingRules.NextCheckTick - 10, GameClock.StepHz);
            }
            double perIdleUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / 1000.0;
            int count = StandingRuleService.Ordered(s).Count;
            PerfLines.Add($"{count} 条规则（{factories.Count} 座工厂、{s.BuildingRecords.Length} 座建筑、{HomeMachines().Count} 台机器）一次检查平均 {perEval:F3} ms（每游戏分钟一次）；不到检查步的世界步 {perIdleUs:F2} µs");
            PerfGate.Expect(count >= 20 && perEval >= 0,
                $"O 性能：{count} 条规则一次检查 {perEval:F3} ms（阈值 2 ms；每游戏分钟才一次，3x 时每真实秒约 0.05 次）；不到检查步时每步 {perIdleUs:F2} µs（阈值 5 µs，O(1)）。Editor batchmode；真机 HybridCLR 另测（FG15-SYS-02）",
                new[] { PerfGate.Le(perEval, 2.0, "一次检查 ms"), PerfGate.Le(perIdleUs, 5.0, "空闲步 µs") }, Expect, Line);
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
                StandingRuleService.RaidActiveProvider = null;
                StandingRuleService.SilentNightWindowProvider = null;
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
