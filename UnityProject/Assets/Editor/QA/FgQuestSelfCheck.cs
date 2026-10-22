using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.Objective;
using GameLogic.View;
using GameLogic.Campaign.Content;
using Luban;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-QST-01 任务系统与面板（FG08 FGR-QST-001～005、020；FG13 FGU-36；验收 FGT-EXP-009“任务：各类型的接取、追踪、完成、失败、重新开始”）的自检——起真实系统断言行为：
    /// A 数据（fg.TbQuest / fg.TbKeyMaterial、物品表来源文字一致、调参、文本中英、钩子、通知类型、快捷键、图鉴）；
    /// B 主线关键材料（按幕出现、首领被击败 → 关键材料进核心保管库、只发一次、前置解锁下一条主线）；
    /// C 主线护送（接车点出现货运车 → 负向：车被毁 → 失败、不能放弃 → 重新开始 → 新车 → 送到家园 → 蓝图）；
    /// D 支线（揭开支线地点 → 可接取、按种子抽取确定；调查 / 夺回数据（走开暂停）/ 摧毁监听节点 / 清剿精英 / 建立前哨 / 救援护送；负向：有时限到期 → 警告 → 失败；放弃）；
    /// E 追踪栏（最多 3 个、第 4 个被拒并写原因、OBJ 目标链不占位、取消后可追踪）；F 家园指引（完成后领取奖励、只领一次、设置里关闭 → 不出现不判定、打开接着进行）；
    /// G 读档对账（任务物品类 DEBT-FG0SAVE01-07、已移除的任务定义）；H 真文件存读档；I 暂停与 0.5x～3x、观察与否一致；K 界面（三栏 / 详情 / 按钮 / 二次确认 / 关键材料清单 /
    /// 追踪栏 / 折叠 / 飞过去 / 地图标记 / 暂停菜单开关 / 探针 / 中英）；L 种子集；M 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgQuestSelfCheck）；单段：bash tools/unity-validate.sh --segment FgQuestSelfCheck。
    /// </summary>
    public static class FgQuestSelfCheck
    {
        private const int Slot = 9;
        private const int RunSlot = 10;
        private const string LogUxml = "Assets/GameRes/Raw/UI/Objective/MissionLog.uxml";
        private const string RowUxml = "Assets/GameRes/Raw/UI/Objective/QuestRow.uxml";
        private const string HudUxml = "Assets/GameRes/Raw/UI/Objective/ObjectiveHud.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 任务系统与面板")]
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
            Line("\n[任务系统与面板] 主线 / 支线 / 家园指引、三栏面板、追踪栏、地图标记、时限警告、主线失败重新开始、关键材料清单（FG8-QST-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            bool originalGuide = GameSettings.HomeGuideQuestsEnabled;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgquest-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                AnalysisCatalog.Reload();
                LootCatalog.Reload();
                LootPickCatalog.Reload();
                BuildingOps.Reload();
                QuestCatalog.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                LootPickService.ResetSessionState();
                CargoService.ResetSessionState();
                CampaignObjectiveTracker.ResetQuestSession();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.SetHomeGuideQuestsEnabled(true);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckMainKeyMaterials);
                Step(CheckMainEscort);
                Step(CheckSideQuests);
                Step(CheckSideTimedAndAbandon);
                Step(CheckTracker);
                Step(CheckHomeGuide);
                Step(CheckReconcile);
                Step(CheckSaveLoad);
                Step(CheckEscortSaveLoad);
                Step(CheckSpeedAndObservation);
                Step(CheckUi);
                Step(CheckSeedSetAndPerf);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
                LootPickCatalog.ResetForTests();
                LootCatalog.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                WorldMapFilters.ResetAll();
                UiEscapeStack.ResetForTests();
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
                GameSettings.SetHomeGuideQuestsEnabled(originalGuide);
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
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  任务系统与面板：全部 {_pass} 项通过" : $"  任务系统与面板：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static readonly string[] UiKeys =
        {
            "quest.cat.main", "quest.cat.side", "quest.cat.guide", "quest.src.core_memo", "quest.src.intercept", "quest.src.terminal", "quest.src.ruin",
            "quest.state.active", "quest.state.done", "quest.state.failed", "quest.state.abandoned", "quest.state.available", "quest.state.claimable",
            "quest.log.title", "quest.log.status", "quest.log.col.active", "quest.log.col.done", "quest.log.col.failed", "quest.log.empty.active", "quest.log.empty.done",
            "quest.log.empty.failed", "quest.log.select", "quest.log.campaign", "quest.detail.title", "quest.detail.source", "quest.detail.goal", "quest.detail.progress",
            "quest.detail.where", "quest.detail.where_none", "quest.detail.reward", "quest.detail.reward_none", "quest.detail.time", "quest.detail.time_none",
            "quest.detail.time_warn", "quest.detail.time_total", "quest.detail.reason", "quest.detail.main_hint", "quest.btn.track", "quest.btn.untrack", "quest.btn.fly",
            "quest.btn.abandon", "quest.btn.abandon_confirm", "quest.btn.restart", "quest.btn.accept", "quest.btn.claim", "quest.btn.close", "quest.footer",
            "quest.keymat.title", "quest.keymat.row", "quest.keymat.have", "quest.keymat.missing", "quest.reason.track_full", "quest.reason.track_not_active",
            "quest.reason.track_chain", "quest.reason.abandon_main", "quest.reason.restart_not_failed", "quest.fail.escort_destroyed", "quest.fail.expired",
            "quest.fail.target_gone", "quest.fail.content_removed", "quest.notify.new", "quest.notify.new_side", "quest.notify.done", "quest.notify.failed",
            "quest.notify.failed_main", "quest.notify.warn", "quest.notify.escort", "quest.notify.escort_hit", "quest.notify.restarted", "quest.hud.title",
            "quest.hud.collapse", "quest.hud.expand", "quest.hud.row", "quest.hud.fly", "quest.hud.fly_tip", "quest.hud.time", "quest.hud.time_warn", "quest.hud.dist",
            "pause.home_guide", "pause.home_guide_tip", "ui.map.quest", "ui.map.quest_tracked", "stats.quest.title", "stats.quest.row", "save.notice.quest_item_removed",
            "save.notice.quest_removed", "codex.expedition.quests.title", "codex.expedition.quests.body", "codex.expedition.quests.hint", "input.action.focus_quest.name",
            "quest.log.row_suffix", "quest.log.item_status", "quest.log.item_join", "quest.keymat.have_used", "quest.reason.accept_not_available",
            "quest.reason.accept_no_target", "quest.reason.claim_done", "quest.reason.claim_not_done",
        };

        private static string Py(float v)
        {
            string r = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

        private static void CheckData()
        {
            // A0 源数据 fgdata_quest.py（fgdata.py --dump 的 QST / KEY 行）与运行时两张表逐字段一致（FgDataPipelineSelfCheck 把这两类行交给本段）。
            Tables tables = ConfigSystem.Instance.Tables;
            (int code, string output) = FgProductionSelfCheck.RunPython(FgProductionSelfCheck.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("QST\t", StringComparison.Ordinal) || l.StartsWith("KEY\t", StringComparison.Ordinal)).ToList();
            var rt = tables.TbQuest.DataList.Select(q => string.Join("\t", "QST", q.Id, q.Category, q.Source, q.TitleKey, q.GoalKey, q.Kind, q.Param, q.Count, q.Territory, q.Anchor, q.Unlock,
                    q.Prereq, Py(q.TimeHours), Py(q.WarnHours), q.TimerFrom, q.RewardItem, q.RewardAmount, q.RewardBlueprint, q.Restartable, q.Weight, q.SortOrder))
                .Concat(tables.TbKeyMaterial.DataList.Select(k => string.Join("\t", "KEY", k.Id, k.Kind, k.NameKey, k.QuestId, k.SourceKey, k.UsageKey, k.Act, k.SortOrder)))
                .ToList();
            bool table = code == 0 && src.Count == rt.Count && src.Count == 25 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            Expect(table, $"A0 源数据 fgdata_quest.py 与运行时两张表（任务 {tables.TbQuest.DataList.Count} / 关键材料 {tables.TbKeyMaterial.DataList.Count}）逐字段一致" +
                          (table ? string.Empty : $"（源 {src.Count} 行 / 运行时 {rt.Count} 行，python 退出码 {code}）"));

            IReadOnlyList<Quest> rows = QuestCatalog.Rows;
            int main = rows.Count(q => q.Category == QuestCatalog.CatMain);
            int side = rows.Count(q => q.Category == QuestCatalog.CatSide);
            int guide = rows.Count(q => q.Category == QuestCatalog.CatGuide);
            var sideKinds = new HashSet<string>(rows.Where(q => q.Category == QuestCatalog.CatSide).Select(q => q.Kind));
            bool kinds = new[] { QuestCatalog.KindReach, QuestCatalog.KindEscort, QuestCatalog.KindDestroy, QuestCatalog.KindHold, QuestCatalog.KindOwnOutpost }.All(sideKinds.Contains);
            bool mainRules = rows.Where(q => q.Category == QuestCatalog.CatMain).All(q => q.Restartable == 1) && rows.Where(q => q.Category != QuestCatalog.CatMain).All(q => q.Restartable == 0);
            Expect(QuestCatalog.LoadError == null && main == 6 && side == 7 && guide == 6 && kinds && mainRules,
                $"A1 fg.TbQuest：主线 {main}、支线 {side}（{string.Join("/", sideKinds)}，FGR-QST-001 的救援 / 摧毁监听节点 / 护送 / 夺回数据 / 清剿精英 / 调查 / 建立前哨）、家园指引 {guide}；主线全部可以重新开始、其余不能");

            IReadOnlyList<KeyMaterial> keys = QuestCatalog.KeyMaterials;
            var bad = new List<string>();
            foreach (KeyMaterial k in keys)
            {
                Quest q = QuestCatalog.Get(k.QuestId);
                if (q == null || q.Category != QuestCatalog.CatMain)
                {
                    bad.Add(k.Id + "：任务不是主线");
                    continue;
                }
                if (k.Kind == "item" && (q.RewardItem != k.Id || !ItemCatalog.TryGet(k.Id, out _) || !GameText.Get(ItemCatalog.TryGet(k.Id, out ItemDef d) ? d.SourceKey : "").Contains("主线")))
                {
                    bad.Add(k.Id + "：任务奖励 / 物品表来源文字不一致");
                }
                if (k.Kind == "blueprint" && q.RewardBlueprint != k.Id)
                {
                    bad.Add(k.Id + "：任务奖励蓝图不一致");
                }
            }
            string[] want = { "listening_array_core", "furnace_heart", "anticorrosion_blueprint", "reactor_core", "supercomputer_core", "megastructure_blueprint" };
            Expect(keys.Count == 6 && want.All(w => keys.Any(k => k.Id == w)) && bad.Count == 0,
                $"A2 FGR-QST-020 关键材料清单入表（fg.TbKeyMaterial {keys.Count} 行：{string.Join("、", keys.Select(k => GameText.Get(k.NameKey)))}），每行由一条主线任务给出、物品表来源文字一致" +
                (bad.Count > 0 ? "；问题：" + string.Join("；", bad) : string.Empty));

            bool tuning = CampaignObjectiveTracker.TrackMax == 3 && Math.Abs(CampaignObjectiveTracker.CheckSeconds - 1.0) < 1e-6 && Mathf.Approximately(CampaignObjectiveTracker.ReachRadius, 12f)
                          && Mathf.Approximately(CampaignObjectiveTracker.HoldRadius, 14f) && Mathf.Approximately(CampaignObjectiveTracker.OutpostRadius, 80f);
            Expect(tuning, $"A3 调参来自 fg.TbHomeTuning：追踪栏最多 {CampaignObjectiveTracker.TrackMax} 个（FGR-QST-003 初值 3）、检查 {CampaignObjectiveTracker.CheckSeconds} 游戏秒、到达 {CampaignObjectiveTracker.ReachRadius} 格、停留 {CampaignObjectiveTracker.HoldRadius} 格、前哨 {CampaignObjectiveTracker.OutpostRadius} 格");

            var missing = new List<string>();
            var keyList = UiKeys.Concat(rows.SelectMany(q => new[] { q.TitleKey, q.GoalKey })).Concat(keys.SelectMany(k => new[] { k.NameKey, k.SourceKey, k.UsageKey })).ToList();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keyList)
                {
                    string t = GameText.Get(k);
                    if (!GameText.Has(k) || string.IsNullOrEmpty(t) || GameText.ContainsMarker(t) || (lang == GameLanguage.En && ContainsCjk(t)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string[] hooks = { GuidanceHooks.QuestFirstNew, GuidanceHooks.QuestFirstTrack, GuidanceHooks.QuestFirstComplete, GuidanceHooks.QuestFirstFailed, GuidanceHooks.QuestFirstLogOpen,
                GuidanceHooks.QuestFirstSideFound, GuidanceHooks.QuestFirstEscort, GuidanceHooks.QuestFirstTimeWarning };
            bool hooksOk = hooks.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType("quest", out _) && NotificationCatalog.TryGetType("quest_alert", out _)
                          && NotificationCatalog.TypeForCue(Campaign.Feedback.FeedbackCueId.QuestAlert)?.Id == "quest_alert"
                          && NotificationCatalog.TypeForCue(Campaign.Feedback.FeedbackCueId.QuestNew)?.Id == "quest";
            InputAction act = ConfigSystem.Instance.Tables.TbInputAction.GetOrDefault("FocusQuest");
            bool input = act != null && act.DefaultBinding == "Q" && act.DefaultMods == "alt" && act.Status == "wired";
            bool codex = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.expedition.quests") != null;
            Expect(missing.Count == 0 && hooksOk && notify && input && codex,
                $"A4 文本 {keyList.Count} 个键中英齐全、英文没有中文（缺 {string.Join(",", missing.Take(6))}）；8 个引导钩子（只埋钩子）；通知类型“任务动态 / 任务警告”由任务反馈时刻转成；" +
                $"快捷键“镜头飞到追踪的任务”默认 Alt+Q 可重绑（{input}）；图鉴条目（{codex}）");
        }

        // ── B 主线关键材料 ────────────────────────────────────────────────────────

        private static void CheckMainKeyMaterials()
        {
            CampaignState s = NewWorld(8401);
            CampaignObjectiveTracker.Recompute(s);
            ObjectiveRecord silent = CampaignObjectiveTracker.FindQuest(s, "main.key.silent");
            ObjectiveRecord foundry = CampaignObjectiveTracker.FindQuest(s, "main.key.foundry");
            ObjectiveRecord clarity = CampaignObjectiveTracker.FindQuest(s, "main.key.clarity");
            PlannedTerritory st = WorldGenService.PlanFor(s)?.Find("silent");
            bool shown = silent?.State == ObjectiveState.Active && foundry?.State == ObjectiveState.Active && clarity == null
                         && st != null && silent.HasAnchor && silent.AnchorX == st.CenterX && silent.AnchorY == st.CenterY;
            NotificationEntry newNote = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == "quest");
            bool located = newNote?.Latest != null && newNote.Latest.HasLocation;
            Expect(shown && located,
                $"B1 第一幕的两条关键材料主线开局就出现（寂听主脑 / 主核心；第二幕的洗礼塔 {(clarity == null ? "未出现" : "已出现")}），位置 = 按种子生成的领地中心（{silent?.AnchorX},{silent?.AnchorY}）；“新任务”通知带位置（{located}）");

            int before = HomeInventory.Stock(s, "listening_array_core");
            bool marked = TerritoryControlService.MarkLeaderDefeated(s, "silent");
            StepSeconds(2);
            int after = HomeInventory.Stock(s, "listening_array_core");
            CampaignObjectiveTracker.Recompute(s);
            CampaignObjectiveTracker.Recompute(s);
            int again = HomeInventory.Stock(s, "listening_array_core");
            NotificationEntry done = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == "quest");
            Expect(marked && silent.State == ObjectiveState.Completed && after == before + 1 && again == after && done?.Latest?.DetailText.Contains("监听阵列核") == true
                   && CampaignObjectiveTracker.HasKeyMaterial(s, QuestCatalog.KeyMaterialOf("listening_array_core")),
                $"B2 首领 Story 的正式入口 MarkLeaderDefeated(静默) → 主线完成，监听阵列核进核心保管库 {before}→{after}（重复重算不再发：{again}），完成通知写奖励（“{done?.Latest?.DetailText}”）——DEBT-FG4ECO11-02 / FG5RND05-09 的来源接上");

            // B2b 负向（审查 P1）：关键材料被正式造价用掉后仍是“已获得”。超控阵列 T1 施工时，运料无人机按 HomeValleyConstruction.TryFetchItem
            // 用 HomeInventory.RemoveUpTo 从核心保管库取走监听阵列核（FgOverrideArraySelfCheck E 段走完整施工）；这里调用同一个出库入口。
            KeyMaterial lac = QuestCatalog.KeyMaterialOf("listening_array_core");
            int took = HomeInventory.RemoveUpTo(s, ItemCatalog.Find("listening_array_core"), 1);
            string lacRow = MissionLogUIToolkit.ComposeKeyMaterials(s).FirstOrDefault(l => l.Contains("监听阵列核")) ?? "";
            bool stillHave = took == 1 && HomeInventory.Stock(s, "listening_array_core") == 0 && CampaignObjectiveTracker.HasKeyMaterial(s, lac)
                             && CampaignObjectiveTracker.KeyMaterialStock(s, lac) == 0 && lacRow.Contains(GameText.Get("quest.keymat.have_used")) && !lacRow.Contains(GameText.Get("quest.keymat.missing"));
            HomeInventory.Add(s, "listening_array_core", 1, clampToSpace: false); // 拆除退回（FgOverrideArraySelfCheck E4）后清单回到“已获得”
            string lacRow2 = MissionLogUIToolkit.ComposeKeyMaterials(s).FirstOrDefault(l => l.Contains("监听阵列核")) ?? "";
            KeyMaterial furnace = QuestCatalog.KeyMaterialOf("furnace_heart");
            Expect(stillHave && lacRow2.Contains(GameText.Get("quest.keymat.have")) && !lacRow2.Contains(GameText.Get("quest.keymat.have_used")) && !CampaignObjectiveTracker.HasKeyMaterial(s, furnace),
                $"B2b 负向“关键材料被造价用掉”：监听阵列核投入施工后核心保管库 0 件，清单仍是“已获得（已投入使用）”（“{lacRow}”），HasKeyMaterial 仍为 true；退回后“{lacRow2}”；没拿到的熔炉心仍是“未获得”");

            bool prereqBefore = CampaignObjectiveTracker.FindQuest(s, "main.anticorrosion") == null;
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            StepSeconds(2);
            ObjectiveRecord anti = CampaignObjectiveTracker.FindQuest(s, "main.anticorrosion");
            // FG10-REG-01：接车点改在沉淀湖外圈酸沼带外缘、朝家园一侧（belt_outer）——拿到防腐涂层之前不用先穿过酸沼（原为领地里离核心最近的支线地点）。
            PlannedTerritory lake = WorldGenService.PlanFor(s)?.Find("clarity");
            bool outsideBelt = anti != null && lake != null && AcidMarshService.ZoneAt(s, new Vector2(anti.AnchorX, anti.AnchorY)) == null
                               && Vector2.Distance(new Vector2(anti.AnchorX, anti.AnchorY), new Vector2(lake.CenterX, lake.CenterY)) >= lake.OuterRadius;
            Expect(prereqBefore && foundry.State == ObjectiveState.Completed && HomeInventory.Stock(s, "furnace_heart") == 1 && anti?.State == ObjectiveState.Active && outsideBelt,
                $"B3 熔炉心进保管库后，第二幕主线“防腐涂层样品”按前置出现，接车点在沉淀湖外圈酸沼带外、朝家园一侧（{anti?.AnchorX},{anti?.AnchorY}，按规则定位，B25；FG10-REG-01）");
        }

        // ── C 主线护送：被毁 → 失败 → 重新开始 → 完成 ──────────────────────────────

        private static void CheckMainEscort()
        {
            CampaignState s = NewWorld(8402);
            CampaignObjectiveTracker.Recompute(s);
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            StepSeconds(2);
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(s, "main.anticorrosion");
            if (r == null)
            {
                Fail("C0 夹具：防腐涂层主线没有出现");
                return;
            }
            Vector2 site = new Vector2(r.AnchorX, r.AnchorY);
            int scout = Spawn(s, site + new Vector2(-3f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(2);
            int truck = r.EscortLogicId;
            bool spawned = r.Stage == 1 && truck > 0 && Rec(truck)?.IsAlive == true && Rec(truck).ChassisId == HomeValleyLayout.Erc002ChassisId && (Pos(truck) - site).magnitude <= 20f;
            bool notified = NotificationCenter.History.Any(e => e?.Type?.Id == "quest" && e.Members.Any(m => m.DetailText.Contains("货运车")));
            Expect(spawned && notified, $"C1 机器走到接车点 → 货运车（搬运轮式 {Name(truck)}，真实的己方机器）出现在旁边（{Pos(truck)}），发“货运车已出现”通知");

            MachineRegistry.ApplyDamage(truck, 1e6f);
            StepSeconds(2);
            bool failed = r.State == ObjectiveState.Failed && r.FailReasonKey == "quest.fail.escort_destroyed" && r.TrackOrder == 0;
            NotificationEntry alert = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == "quest_alert");
            bool noAbandon = !CampaignObjectiveTracker.TryAbandon(s, r.ObjectiveId, out string abandonWhy);
            Expect(failed && alert?.Latest?.DetailText.Contains("重新开始") == true && noAbandon && abandonWhy == GameText.Get("quest.reason.abandon_main"),
                $"C2 负向“护送目标被毁”：货运车被摧毁 → 主线失败，写原因（“{alert?.Latest?.DetailText}”）；主线不能放弃（“{abandonWhy}”）");

            bool restarted = CampaignObjectiveTracker.TryRestart(s, r.ObjectiveId, out string restartMsg);
            bool reset = r.State == ObjectiveState.Active && r.Attempt == 2 && r.Stage == 0 && r.EscortLogicId == 0 && r.FailReasonKey == null && r.TrackOrder > 0;
            bool twice = !CampaignObjectiveTracker.TryRestart(s, r.ObjectiveId, out string twiceWhy);
            StepSeconds(2);
            int truck2 = r.EscortLogicId;
            Expect(restarted && reset && twice && truck2 > 0 && truck2 != truck && Rec(truck2)?.IsAlive == true,
                $"C3 FGR-QST-004 主线失败可以重新开始（“{restartMsg}”，第 {r.Attempt} 次尝试、自动追踪），进行中不能再“重新开始”（“{twiceWhy}”）；机器还在接车点 → 新货运车 {Name(truck2)}");

            MoveTo(truck2, CoreNear(s));
            StepSeconds(2);
            bool bp = CampaignObjectiveTracker.HasKeyMaterial(s, QuestCatalog.KeyMaterialOf("anticorrosion_blueprint"));
            Expect(r.State == ObjectiveState.Completed && bp && Rec(truck2)?.IsAlive == true && MachineRegistry.TryGetRecord(scout, out _),
                $"C4 货运车开到家园（归还核心旁）→ 主线完成，防腐涂层蓝图记入（关键材料清单“已获得”），货运车留在你这里");
        }

        // ── D 支线 ────────────────────────────────────────────────────────────────

        private static void CheckSideQuests()
        {
            CampaignState s = NewWorld(8403);
            CampaignObjectiveTracker.Recompute(s);
            List<WorldFeature> real = AllSides(s);
            if (real.Count < 1)
            {
                Fail("D0 夹具：这个世界的阵营领地里没有支线地点");
                return;
            }
            var sites = new List<WorldFeature> { real[0], Synthetic(s, 1, 50f), Synthetic(s, 2, 55f), Synthetic(s, 3, 60f) };
            // 揭开（正式入口 RevealArea → 揭开扫描）→ 按（种子, 地点）抽一种，可接取，发通知。
            WorldFeature f0 = sites[0];
            Quest picked = CampaignObjectiveTracker.PickSide(s, f0);
            HomeGridService.RevealArea(s, new Vector2(f0.X, f0.Y), 12f);
            ObjectiveRecord r0 = CampaignObjectiveTracker.FindQuest(s, f0.Id);
            bool again = CampaignObjectiveTracker.PickSide(s, f0)?.Id == picked?.Id;
            bool noted = NotificationCenter.History.Any(e => e?.Type?.Id == "quest" && e.Members.Any(m => m.HasLocation && m.DetailText.Contains("发现支线")));
            Expect(r0 != null && r0.State == ObjectiveState.Available && r0.DefId == picked?.Id && again && r0.AnchorX == f0.X && r0.AnchorY == f0.Y && noted && r0.DeadlineTick < 0,
                $"D1 DEBT-FG8GEN02-03：揭开领地里的支线地点 → 支线“{QuestCatalog.Title(picked)}”可接取（按种子 + 地点抽取，重算相同），发带位置的“发现支线”通知；接取前不判定、不计时");

            // 调查：接取 → 机器到达 → 完成，技术数据奖励。
            ObjectiveRecord inv = Offer(s, sites[1], "side.investigate");
            int tech0 = s.TechData;
            bool accepted = CampaignObjectiveTracker.TryAccept(s, inv.ObjectiveId, out _);
            bool autoTracked = inv.TrackOrder > 0;
            int m1 = Spawn(s, new Vector2(sites[1].X + 2, sites[1].Y), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(2);
            Expect(accepted && autoTracked && inv.State == ObjectiveState.Completed && s.TechData == tech0 + 30 && inv.TrackOrder == 0,
                $"D2 FGT-EXP-009 接取 → 追踪（有空位自动追踪）→ 完成：调查异常信号，机器 {Name(m1)} 到达 → 完成，技术数据 +{s.TechData - tech0}，让出追踪栏");

            // 夺回数据：在地点停留累计，走开暂停不清零。
            ObjectiveRecord data = Offer(s, sites[2], "side.data");
            CampaignObjectiveTracker.TryAccept(s, data.ObjectiveId, out _);
            long expectDeadline = GameClock.Ticks + GameClock.TicksFor(48 * GameClock.HourSeconds);
            bool deadlineOk = Math.Abs(data.DeadlineTick - expectDeadline) <= GameClock.StepHz;
            Vector2 dsite = new Vector2(sites[2].X, sites[2].Y);
            int m2 = Spawn(s, dsite + new Vector2(2f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(11);
            long held = data.HoldTicks;
            MoveTo(m2, dsite + new Vector2(60f, 0f));
            StepSeconds(5);
            bool paused = data.HoldTicks == held && data.State == ObjectiveState.Active;
            MoveTo(m2, dsite + new Vector2(2f, 0f));
            StepSeconds(24);
            Expect(deadlineOk && held >= GameClock.TicksFor(9) && paused && data.State == ObjectiveState.Completed,
                $"D3 夺回数据：接取时写死时限（48 游戏小时，{deadlineOk}）；机器守在终端旁累计 {held / GameClock.StepHz} 秒，走开后暂停（不清零：{paused}），回来累计满 30 秒完成");

            // 摧毁监听节点 / 清剿精英：目标据点在接取时选定，被摧毁（正式入口 MarkDestroyed）→ 完成。
            // 目标据点要在支线地点所在的领地里：取所在领地还有活着的领地据点的真实地点（不够时在有据点的领地里放一个测试地点）。
            var fsites = real.Where(f => f.Id != f0.Id && WorldOutpostSystem.Outposts(s).Any(o => o != null && !o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == f.TerritoryId)).Take(2).ToList();
            while (fsites.Count < 2)
            {
                OutpostRecord any = WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory).Skip(fsites.Count).First();
                fsites.Add(new WorldFeature { Id = "side:qtest:terr:" + fsites.Count, Kind = WorldFeatureKind.SideSite, X = any.CellX + 3, Y = any.CellY, TerritoryId = any.TerritoryId });
            }
            fsites.Add(Synthetic(s, 5, 340f));
            ObjectiveRecord jam = Offer(s, fsites[0], "side.jammer");
            ObjectiveRecord elite = Offer(s, fsites[1], "side.elite");
            CampaignObjectiveTracker.TryAccept(s, jam.ObjectiveId, out _);
            CampaignObjectiveTracker.TryAccept(s, elite.ObjectiveId, out _);
            OutpostRecord jt = WorldOutpostSystem.Find(s, jam.TargetId);
            OutpostRecord et = WorldOutpostSystem.Find(s, elite.TargetId);
            string eliteTerr = WorldGenQuery.TerritoryAt(s, fsites[1].X, fsites[1].Y)?.Id;
            int maxTier = WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == eliteTerr).Select(o => o.Tier).DefaultIfEmpty(0).Max();
            string jprog = CampaignObjectiveTracker.ProgressText(s, jam);
            WorldOutpostSystem.MarkDestroyed(s, jam.TargetId);
            if (elite.TargetId != jam.TargetId)
            {
                WorldOutpostSystem.MarkDestroyed(s, elite.TargetId);
            }
            StepSeconds(2);
            Expect(jt != null && jt.TerritoryId == WorldGenQuery.TerritoryAt(s, fsites[0].X, fsites[0].Y)?.Id && et != null && et.Tier == maxTier && jprog.Contains(jt.Tier.ToString()) && jam.State == ObjectiveState.Completed && elite.State == ObjectiveState.Completed,
                $"D4 摧毁监听节点（目标 = 离地点最近的领地据点 {jt?.OutpostId}，进度“{jprog}”）与清剿精英（目标 = 等级最高的 {et?.Tier} 级）：据点被摧毁 → 完成");

            // 建立前哨：在地点附近建一座建筑 → 前哨站（离核心 300 格以外）→ 完成。
            ObjectiveRecord op = Offer(s, fsites[2], "side.outpost");
            CampaignObjectiveTracker.TryAccept(s, op.ObjectiveId, out _);
            GridCell oc = SiegeService.NearestPassable(new GridCell(fsites[2].X + 6, fsites[2].Y), 10);
            BuildingRecord b = F.Built(s, "turret_light", "quest_op", oc);
            F.Resync(s);
            StepSeconds(4);
            Expect(b != null && OwnOutpostService.All(s).Any(o => o.Intact > 0) && op.State == ObjectiveState.Completed,
                $"D5 建立前哨：在支线地点旁建成一座轻型炮塔 → 前哨站成立（{OwnOutpostService.All(s).Count} 个；建筑群 " +
                string.Join(",", WorldMapOwnClusters.For(s).Select(c => $"{c.Count}@{Math.Round(c.X)},{Math.Round(c.Y)}:{c.IsOutpost}")) + $"；地点 {fsites[2].X},{fsites[2].Y}）→ 完成");

            // 救援（支线护送）：到达 → 车出现 → 送回 → 完成，车留下。
            ObjectiveRecord res = Offer(s, sites[3], "side.rescue");
            CampaignObjectiveTracker.TryAccept(s, res.ObjectiveId, out _);
            bool noTimerYet = res.DeadlineTick < 0;
            Spawn(s, new Vector2(sites[3].X - 2, sites[3].Y), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(2);
            bool timerStarted = res.Stage == 1 && res.DeadlineTick > GameClock.Ticks;
            int truck = res.EscortLogicId;
            MoveTo(truck, CoreNear(s));
            StepSeconds(2);
            Expect(noTimerYet && timerStarted && res.State == ObjectiveState.Completed && Rec(truck)?.IsAlive == true,
                $"D6 救援遗迹里的机器：时限从接上货运车起算（接取时没有：{noTimerYet}，出现后开始：{timerStarted}），送到家园 → 完成，货运车 {Name(truck)} 留下");

            // D9 负向（审查 P2）：“摧毁据点”类在目标领地的据点已全被拔掉后才点接取 → 拒绝并写原因，任务留在“可接取”（不再接取即失败）；
            // 接取 / 领取的拒绝原因各有专用文案。
            string terr = jt?.TerritoryId;
            var noTargetSite = new WorldFeature { Id = "side:qtest:notarget", Kind = WorldFeatureKind.SideSite, X = (jt?.CellX ?? 0) + 4, Y = jt?.CellY ?? 0, TerritoryId = terr };
            ObjectiveRecord nt = Offer(s, noTargetSite, "side.jammer");
            foreach (OutpostRecord kill in WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == terr).ToList())
            {
                WorldOutpostSystem.MarkDestroyed(s, kill.OutpostId);
            }
            bool ntAccepted = CampaignObjectiveTracker.TryAccept(s, nt.ObjectiveId, out string ntWhy);
            StepSeconds(2);
            bool ntKept = nt.State == ObjectiveState.Available && nt.FailReasonKey == null;
            bool doneAccept = !CampaignObjectiveTracker.TryAccept(s, inv.ObjectiveId, out string doneWhy);
            bool claimTwice = !CampaignObjectiveTracker.TryClaim(s, inv.ObjectiveId, out string claimWhy);
            Expect(terr != null && !ntAccepted && ntWhy == GameText.Get("quest.reason.accept_no_target") && ntKept
                   && doneAccept && doneWhy == GameText.Get("quest.reason.accept_not_available") && claimTwice && claimWhy == GameText.Get("quest.reason.claim_done"),
                $"D9 负向“目标据点已全被摧毁”：接取被拒（“{ntWhy}”），任务留在可接取、不会接取即失败；已完成的任务再接取 → “{doneWhy}”，奖励已发再领取 → “{claimWhy}”");
        }

        private static void CheckSideTimedAndAbandon()
        {
            CampaignState s = NewWorld(8404);
            CampaignObjectiveTracker.Recompute(s);
            var sites = new List<WorldFeature> { Synthetic(s, 0, 60f), Synthetic(s, 1, 60f) };
            ObjectiveRecord data = Offer(s, sites[0], "side.data");
            CampaignObjectiveTracker.TryAccept(s, data.ObjectiveId, out _);
            Quest q = CampaignObjectiveTracker.DefOf(data);
            // 测试捷径：把到期时刻拉到 6 游戏秒后（真实的 48 游戏小时在 D3 断言；这里验证警告 → 到期 → 失败这条判定链本身）。
            data.DeadlineTick = GameClock.Ticks + GameClock.TicksFor(6);
            int alerts0 = AlertCount("快到期");
            StepSeconds(2);
            bool warned = data.DeadlineWarned && AlertCount("快到期") == alerts0 + 1 && CampaignObjectiveTracker.IsWarningTime(data) && data.State == ObjectiveState.Active;
            StepSeconds(3);
            bool once = AlertCount("快到期") == alerts0 + 1;
            StepSeconds(3);
            bool expired = data.State == ObjectiveState.Failed && data.FailReasonKey == "quest.fail.expired" && AlertCount("任务失败") >= 1;
            bool noRestart = !CampaignObjectiveTracker.TryRestart(s, data.ObjectiveId, out string why);
            Expect(warned && once && expired && noRestart && q.WarnHours > 0f,
                $"D7 负向“有时限的任务到期”：到期前 {q.WarnHours} 游戏小时内提前警告一次（{warned}/{once}），到期后标为失败并写原因“{GameText.Get(data.FailReasonKey ?? "")}”；支线不能重新开始（“{why}”）");

            ObjectiveRecord ab = Offer(s, sites[1], "side.convoy");
            bool armedFirst = CampaignObjectiveTracker.TryAbandon(s, ab.ObjectiveId, out string abMsg);
            var act = new List<QuestRowView>();
            var done = new List<QuestRowView>();
            var failed = new List<QuestRowView>();
            MissionLogUIToolkit.ComposeColumns(s, act, done, failed);
            Expect(armedFirst && ab.State == ObjectiveState.Abandoned && failed.Any(v => v.Id == ab.ObjectiveId && v.Text.Contains(GameText.Get("quest.state.abandoned")))
                   && failed.Any(v => v.Id == data.ObjectiveId && v.Text.Contains(GameText.Get("quest.fail.expired"))) && !act.Any(v => v.Id == ab.ObjectiveId),
                $"D8 支线可以放弃（“{abMsg}”）：放弃的与到期失败的都进“已失败”栏并写原因 / 状态");
        }

        // ── E 追踪栏 ──────────────────────────────────────────────────────────────

        private static void CheckTracker()
        {
            CampaignState s = NewWorld(8405);
            CampaignObjectiveTracker.Recompute(s);
            var sites = Enumerable.Range(0, 4).Select(i => Synthetic(s, i, 120f)).ToList();
            var ids = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                ObjectiveRecord r = Offer(s, sites[i], "side.investigate");
                CampaignObjectiveTracker.TryAccept(s, r.ObjectiveId, out _);
                ids.Add(r.ObjectiveId);
            }
            // 开局的两条主线 / 家园指引不自动追踪；接取的支线有空位时自动追踪：前三个进栏，第四个没有。
            int tracked = CampaignObjectiveTracker.TrackedCount(s);
            bool fourthOut = CampaignObjectiveTracker.FindQuest(s, ids[3]).TrackOrder == 0;
            bool refused = !CampaignObjectiveTracker.TrySetTracked(s, ids[3], true, out string fullWhy);
            bool chain = !CampaignObjectiveTracker.TrySetTracked(s, CampaignObjectiveTracker.Obj01, true, out string chainWhy);
            bool untrack = CampaignObjectiveTracker.TrySetTracked(s, ids[0], false, out _);
            bool now = CampaignObjectiveTracker.TrySetTracked(s, ids[3], true, out _);
            var list = new List<ObjectiveRecord>();
            CampaignObjectiveTracker.TrackedQuests(s, list);
            Expect(tracked == 3 && fourthOut && refused && fullWhy.Contains("3") && chain && chainWhy == GameText.Get("quest.reason.track_chain") && untrack && now
                   && list.Count == 3 && list[2].ObjectiveId == ids[3],
                $"E1 负向“同时追踪超过 3 个”：第 4 个被拒（“{fullWhy}”）；战役目标链不占追踪栏（“{chainWhy}”）；取消一个后可以追踪第 4 个（排在最后）");

            bool focus1 = CampaignObjectiveTracker.FocusNextTracked(s, out _);
            Vector2 a = CampaignObjectiveTracker.LastFlyTarget;
            CampaignObjectiveTracker.FocusNextTracked(s, out _);
            Vector2 b = CampaignObjectiveTracker.LastFlyTarget;
            Vector2? loc0 = CampaignObjectiveTracker.QuestLocation(s, list[0]);
            Vector2? loc1 = CampaignObjectiveTracker.QuestLocation(s, list[1]);
            Expect(loc0.HasValue && a == loc0.Value && loc1.HasValue && b == loc1.Value,
                $"E2 快捷键“镜头飞到追踪的任务”（Alt+Q）依次飞到追踪栏里的任务（{a} → {b}，与点通知定位同一入口；本环境没有镜头时 LocateHandler 为空 {focus1}）");
        }

        // ── F 家园指引 ────────────────────────────────────────────────────────────

        private static void CheckHomeGuide()
        {
            CampaignState s = NewWorld(8406);
            CampaignObjectiveTracker.Recompute(s);
            ObjectiveRecord turret = CampaignObjectiveTracker.FindQuest(s, "guide.turret");
            ObjectiveRecord drill = CampaignObjectiveTracker.FindQuest(s, "guide.drill");
            ObjectiveRecord night = CampaignObjectiveTracker.FindQuest(s, "guide.silent_night");
            bool offered = turret?.State == ObjectiveState.Active && drill?.State == ObjectiveState.Active && CampaignObjectiveTracker.FindQuest(s, "guide.lab") == null;
            int scrap0 = s.Scrap;
            GridCell? tc = F.FindFree(s, "turret_light", 6f, 30f);
            if (tc.HasValue)
            {
                F.Built(s, "turret_light", "qturret", tc.Value);
                F.Resync(s);
            }
            StepSeconds(1);
            bool doneNoReward = turret.State == ObjectiveState.Completed && !turret.RewardClaimed && CampaignObjectiveTracker.CanClaim(turret);
            int scrapAfterDone = s.Scrap;
            bool claimed = CampaignObjectiveTracker.TryClaim(s, turret.ObjectiveId, out _);
            int scrapAfterClaim = s.Scrap;
            bool twice = !CampaignObjectiveTracker.TryClaim(s, turret.ObjectiveId, out _);
            Expect(offered && doneNoReward && scrapAfterDone == scrap0 && claimed && scrapAfterClaim == scrap0 + 30 && twice && s.Scrap == scrapAfterClaim,
                $"F1 家园指引“守住家园”：建成轻型炮塔 → 完成，奖励不自动塞进经济（废料 {scrap0}→{scrapAfterDone}），在任务日志里领取 +30（→{scrapAfterClaim}），只能领一次");

            GameSettings.SetHomeGuideQuestsEnabled(false);
            GridCell? dc = F.FindFree(s, "power_pole", 8f, 40f); // 测试捷径：直接登记一座建成的提取钻（是否压在矿上与本断言无关，任务只读建筑记录）
            if (dc.HasValue)
            {
                F.Built(s, "extraction_drill", "qdrill", dc.Value);
                F.Resync(s);
            }
            night.TrackOrder = 0;
            CampaignObjectiveTracker.TrySetTracked(s, night.ObjectiveId, false, out _);
            StepSeconds(2);
            var act = new List<QuestRowView>();
            var done = new List<QuestRowView>();
            var failed = new List<QuestRowView>();
            MissionLogUIToolkit.ComposeColumns(s, act, done, failed);
            bool hidden = drill.State == ObjectiveState.Active && !act.Concat(done).Any(v => v.Id != null && v.Id.StartsWith("guide.", StringComparison.Ordinal))
                          && !CampaignObjectiveTracker.TrySetTracked(s, drill.ObjectiveId, true, out _) && CampaignObjectiveTracker.FindQuest(s, "guide.lab") == null;
            GameSettings.SetHomeGuideQuestsEnabled(true);
            StepSeconds(2);
            ObjectiveRecord lab = CampaignObjectiveTracker.FindQuest(s, "guide.lab");
            Expect(dc.HasValue && hidden && drill.State == ObjectiveState.Completed && lab?.State == ObjectiveState.Active,
                $"F2 设置里关闭家园指引：任务日志不列出、不能追踪、条件满足也不判定（提取钻已建成，任务仍进行中）；重新打开 → 接着判定完成，后续“开始研究”出现");

            // 撑过第一次静默夜：读静默夜的权威字段（测试捷径：直接写“已经开始过 1 次、现在没在进行”，正式流程由 SilentNightService 写）。
            s.Weather.SilentNight.Count = 1;
            s.Weather.SilentNight.Active = true;
            StepSeconds(1);
            bool notYet = night.State == ObjectiveState.Active;
            s.Weather.SilentNight.Active = false;
            StepSeconds(1);
            Expect(notYet && night.State == ObjectiveState.Completed,
                "F3 家园指引“熬过静默夜”：第一次静默夜进行中不算，结束后完成（读静默夜的权威字段）");
        }

        // ── G 读档对账 ────────────────────────────────────────────────────────────

        private static void CheckReconcile()
        {
            CampaignState s = NewWorld(8407);
            CampaignObjectiveTracker.Recompute(s);
            var ghost = new ObjectiveRecord { ObjectiveId = "side:removed", DefId = "side.removed_in_update", State = ObjectiveState.Active, TrackOrder = 5, Attempt = 1 };
            s.ObjectiveRecords = s.ObjectiveRecords.Append(ghost).ToArray();
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Concat(new[]
            {
                new RegionQuestItemRecord { SalvageInstanceId = "qtest:1", ContentId = "qtest_item", RegionId = HomeValleyLayout.RegionId, State = RegionQuestItemState.OnGround },
                new RegionQuestItemRecord { SalvageInstanceId = "qtest:2", ContentId = "qtest_item", RegionId = HomeValleyLayout.RegionId, State = RegionQuestItemState.Carried },
                new RegionQuestItemRecord { SalvageInstanceId = "qtest:3", ContentId = "qtest_item", RegionId = HomeValleyLayout.RegionId, State = RegionQuestItemState.Recovered },
            }).ToArray();
            int scrap0 = s.Scrap;
            int failed0 = s.Quests.Failed;
            SaveContentReconciler.OverrideForTests(RemovedTable(("qtest_item", "quest_item", "save.notice.unknown_content", 6, 2)));
            SaveNoticeRecord[] notes = SaveContentReconciler.ReconcileQuests(s, 1, 2);
            SaveNoticeRecord[] again = SaveContentReconciler.ReconcileQuests(s, 1, 2);
            SaveContentReconciler.ResetForTests();
            int left = s.RegionQuestItems.Count(q => q.ContentId == "qtest_item");
            bool recoveredKept = s.RegionQuestItems.Any(q => q.SalvageInstanceId == "qtest:3");
            Expect(notes.Length == 2 && again.Length == 0 && left == 1 && recoveredKept && s.Scrap == scrap0 + 12 && ghost.State == ObjectiveState.Failed
                   && ghost.FailReasonKey == "quest.fail.content_removed" && ghost.TrackOrder == 0 && s.Quests.Failed == failed0 + 1
                   && SaveContentReconciler.Render(notes[0]).Contains("12") ,
                $"G1 DEBT-FG0SAVE01-07 任务物品类（统计失败数 {failed0}→{s.Quests.Failed}）：已移除的任务物（没带回的 2 件）按 scrapRefund 转成 {s.Scrap - scrap0} 废料、已带回的历史记录保留；引用已移除定义的任务标为失败（游戏更新）并让出追踪栏；" +
                $"各通知一次（“{SaveContentReconciler.Render(notes[0])}”），再对账不重复");
        }

        // ── H 存读档 ──────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (ObjectiveRecord r in (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>()).Where(CampaignObjectiveTracker.IsQuestRecord).OrderBy(r => r.ObjectiveId, StringComparer.Ordinal))
            {
                sb.Append(r.ObjectiveId).Append('=').Append(r.DefId).Append(':').Append(r.State).Append(':').Append(r.AnchorX).Append(',').Append(r.AnchorY).Append(':')
                  .Append(r.TargetId).Append('/').Append(r.TargetBaseline).Append(':').Append(r.OfferedTick).Append('/').Append(r.DeadlineTick).Append('/').Append(r.DeadlineWarned)
                  .Append(':').Append(r.Stage).Append('/').Append(r.EscortLogicId).Append('/').Append(r.HoldTicks).Append(':').Append(r.FailReasonKey).Append(':').Append(r.Attempt)
                  .Append(':').Append(r.EndedTick).Append(':').Append(r.TrackOrder).Append(':').Append(r.RewardClaimed).Append('|');
            }
            QuestState q = s.Quests;
            sb.Append("qs:").Append(q.SideBaselined).Append('/').Append(q.TrackerCollapsed).Append('/').Append(q.Completed).Append('/').Append(q.Failed).Append('/').Append(q.Abandoned).Append('/').Append(q.Restarted);
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8408);
            CampaignObjectiveTracker.Recompute(s);
            var sites = Enumerable.Range(0, 3).Select(i => Synthetic(s, i, 60f)).ToList();
            ObjectiveRecord data = Offer(s, sites[0], "side.data");
            CampaignObjectiveTracker.TryAccept(s, data.ObjectiveId, out _);
            Offer(s, sites[1], "side.investigate");
            ObjectiveRecord ab = Offer(s, sites[2], "side.jammer");
            CampaignObjectiveTracker.TryAbandon(s, ab.ObjectiveId, out _);
            int m = Spawn(s, new Vector2(sites[0].X + 2, sites[0].Y), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(8);
            CampaignObjectiveTracker.SetTrackerCollapsed(s, true);
            long held = data.HoldTicks;
            string snap = Snapshot(s);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            string snap2 = back != null ? Snapshot(back) : null;
            Expect(sv.Success && back != null && snap2 == snap && held > 0,
                $"H1 存读档：进行中（停留累计 {held / GameClock.StepHz} 秒、时限、追踪）、可接取、已放弃、追踪栏折叠、统计逐项一致（{snap.Length} 字节快照）");
            if (back == null)
            {
                return;
            }
            ObjectiveRecord d2 = CampaignObjectiveTracker.FindQuest(back, data.ObjectiveId);
            StepSeconds(26);
            Expect(d2 != null && d2.State == ObjectiveState.Completed && back.Quests.TrackerCollapsed && MachineRegistry.TryGetRecord(m, out _),
                "H2 读档后接着累计停留时间并完成（不从 0 开始、不丢失）");
        }

        /// <summary>H3（审查 P2）：护送进行中（货运车已出现、在途）真文件存档 → 读档 → 接着开回家园完成。</summary>
        private static void CheckEscortSaveLoad()
        {
            CampaignState s = NewWorld(8412);
            CampaignObjectiveTracker.Recompute(s);
            TerritoryControlService.MarkLeaderDefeated(s, "foundry");
            StepSeconds(2);
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(s, "main.anticorrosion");
            if (r == null)
            {
                Fail("H3 夹具：防腐涂层主线没有出现");
                return;
            }
            Spawn(s, new Vector2(r.AnchorX - 3f, r.AnchorY), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            StepSeconds(2);
            int truck = r.EscortLogicId;
            if (r.Stage != 1 || truck <= 0 || Rec(truck)?.IsAlive != true)
            {
                Fail($"H3 夹具：货运车没有出现（阶段 {r.Stage}，车 {truck}）");
                return;
            }
            MoveTo(truck, new Vector2(r.AnchorX + 8f, r.AnchorY)); // 在途：离开接车点、还没到家园
            StepSeconds(2);
            string snap = Snapshot(s);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            ObjectiveRecord r2 = back != null ? CampaignObjectiveTracker.FindQuest(back, "main.anticorrosion") : null;
            bool same = back != null && Snapshot(back) == snap && r2 != null && r2.State == ObjectiveState.Active && r2.Stage == 1 && r2.EscortLogicId == truck && Rec(truck)?.IsAlive == true;
            if (!same)
            {
                Fail($"H3 护送途中存读档：存档 {sv.Success}，读档后任务 / 货运车不一致（阶段 {r2?.Stage}，车 {r2?.EscortLogicId}/{truck}，在册 {Rec(truck)?.IsAlive}）");
                return;
            }
            MoveTo(truck, CoreNear(back));
            StepSeconds(2);
            Expect(sv.Success && r2.State == ObjectiveState.Completed && Rec(truck)?.IsAlive == true
                   && CampaignObjectiveTracker.HasKeyMaterial(back, QuestCatalog.KeyMaterialOf("anticorrosion_blueprint")),
                "H3 护送途中（货运车在途）真文件存档 → 读档：任务阶段、货运车 LogicId 与快照逐项一致，接着开回家园 → 主线完成、蓝图记入");
        }

        // ── I 暂停 / 倍速 / 观察 ─────────────────────────────────────────────────

        private static void CheckSpeedAndObservation()
        {
            string RunAt(float speed, bool pauseMid, bool observe)
            {
                CampaignState s = NewWorld(8409, observe);
                CampaignObjectiveTracker.Recompute(s);
                var sites = new List<WorldFeature> { Synthetic(s, 0, 60f), Synthetic(s, 3, 60f) };
                ObjectiveRecord data = Offer(s, sites[0], "side.data");
                CampaignObjectiveTracker.TryAccept(s, data.ObjectiveId, out _);
                ObjectiveRecord inv = Offer(s, sites[1], "side.investigate");
                CampaignObjectiveTracker.TryAccept(s, inv.ObjectiveId, out _);
                Spawn(s, new Vector2(sites[0].X + 2, sites[0].Y), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
                Spawn(s, new Vector2(sites[1].X + 2, sites[1].Y), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
                GameClock.SetSpeed(speed);
                bool pausedOk = true;
                if (pauseMid)
                {
                    GameClock.SetPaused(true);
                    long h0 = data.HoldTicks;
                    for (int k = 0; k < 120; k++)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    pausedOk = data.HoldTicks == h0 && inv.State == ObjectiveState.Active;
                    GameClock.SetPaused(false);
                }
                long target = GameClock.Ticks + GameClock.StepHz * 12;
                for (int frames = 0; GameClock.Ticks < target && frames < 40000; frames++)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                return (pausedOk ? string.Empty : "PAUSE-MOVED ") + Snapshot(s);
            }
            string r05 = RunAt(0.5f, false, true);
            string r1 = RunAt(1f, true, true);
            string r3 = RunAt(3f, false, true);
            string hidden = RunAt(1f, false, false);
            Expect(r05 == r1 && r1 == r3 && r1 == hidden && !r1.Contains("PAUSE") && r1.Contains(":Completed:"),
                $"I1 B09 暂停与倍速 + FGR-BASE-021：同样的“夺回数据 + 调查”在 0.5x / 1x（暂停 2 秒真实时间期间不累计、不完成）/ 3x、以及镜头不在家园时，停留累计与完成的那一步逐项相同（{r1.Length} 字节快照）");
        }

        // ── K 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(8410);
            CampaignObjectiveTracker.Recompute(s);
            var sites = new List<WorldFeature> { Synthetic(s, 0, 80f), Synthetic(s, 1, 80f) };
            ObjectiveRecord avail = Offer(s, sites[0], "side.data");
            ObjectiveRecord timed = Offer(s, sites[1], "side.rescue");
            ObjectiveRecord main = CampaignObjectiveTracker.FindQuest(s, "main.key.silent");
            var row = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml);
            string colTitles = "", detailWhere = "", detailReward = "", detailTime = "", acceptMsg = "", abandonText = "", enAll = "", keyRow = "";
            bool uiDone = false, accepted = false, trackedAfterAccept = false, armed = false, abandoned = false, keyRows = false, flyShown = false, mainNoAbandon = false, hook = false;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(LogUxml, "MissionLogRoot", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_questlog") { hideFlags = HideFlags.HideAndDontSave };
                    MissionLogUIToolkit p = go.AddComponent<MissionLogUIToolkit>();
                    p.SetRowTemplateForTests(row);
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Render(s);
                    if (lang == GameLanguage.ZhCn && !uiDone)
                    {
                        uiDone = true;
                        hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.QuestFirstLogOpen);
                        colTitles = p.ColumnTitle(0) + " / " + p.ColumnTitle(1) + " / " + p.ColumnTitle(2);
                        keyRows = p.KeyRowCount == 6;
                        keyRow = p.KeyRowText(0);
                        // 主线：不能放弃（按钮不出现），可以飞过去。
                        p.Select(main.ObjectiveId);
                        mainNoAbandon = !MissionLogUIToolkit.ButtonShown(p.AbandonButton) && MissionLogUIToolkit.ButtonShown(p.FlyButton) && p.FlyButton.enabledSelf;
                        // 可接取的支线：写位置、奖励预览、时限；点“接取”→ 进行中并追踪。
                        p.Select(avail.ObjectiveId);
                        detailWhere = p.Detail.Where;
                        detailReward = p.Detail.Reward;
                        detailTime = p.Detail.Time;
                        flyShown = MissionLogUIToolkit.ButtonShown(p.FlyButton);
                        Click(p.AcceptButton);
                        accepted = avail.State == ObjectiveState.Active;
                        trackedAfterAccept = avail.TrackOrder > 0;
                        acceptMsg = p.MessageText;
                        // 放弃要二次确认（B04）：第一次只改成“确认放弃？”。
                        p.Select(timed.ObjectiveId);
                        Click(p.AbandonButton);
                        armed = timed.State == ObjectiveState.Available && p.AbandonButton.text == GameText.Get("quest.btn.abandon_confirm");
                        abandonText = p.AbandonButton.text;
                        Click(p.AbandonButton);
                        abandoned = timed.State == ObjectiveState.Abandoned;
                    }
                    else if (lang == GameLanguage.En)
                    {
                        p.Select(avail.ObjectiveId); // 本卡新增的文字（Demo 目标链的标题与清单仍是硬编码中文，DEBT-FG8QST01-07）
                        enAll = p.ColumnTitle(0) + p.ColumnTitle(1) + p.ColumnTitle(2) + p.StatusText + p.Detail.Title + p.Detail.Where + p.Detail.Reward + p.KeyRowText(0)
                                + string.Concat(Enumerable.Range(0, p.RowCount(0)).Where(i => p.RowView(0, i).Id == null || !p.RowView(0, i).Id.StartsWith("OBJ", StringComparison.Ordinal)).Select(i => p.RowText(0, i)));
                    }
                    p.SetOpen(false);
                    UnityEngine.Object.DestroyImmediate(go);
                });
                Expect(result.StartsWith("PASS"), $"K0 任务日志布局探针（{lang}，四种分辨率 + 超长文字）：{string.Join(" / ", result.Split('\n').Take(4))}");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(hook && colTitles.Contains("进行中") && colTitles.Contains("已完成") && colTitles.Contains("已失败") && keyRows && keyRow.Contains("监听阵列核") && keyRow.Contains("来源"),
                $"K1 FGU-36 三栏：“{colTitles}”；关键材料清单 6 行（“{keyRow}”）；第一次打开任务日志的钩子");
            Expect(detailWhere.Contains("支线地点") && detailReward.Contains("技术数据") && detailTime.Contains(GameText.Get("quest.detail.time_from_accept")) && flyShown && mainNoAbandon,
                $"K2 详情写明位置（“{detailWhere}”）、奖励预览（“{detailReward}”）、时限（“{detailTime}”）；主线没有“放弃”按钮、可以飞过去");
            Expect(accepted && trackedAfterAccept && acceptMsg.Length > 0 && armed && abandoned,
                $"K3 点“接取”→ 进行中并自动追踪（“{acceptMsg}”，{accepted}/{trackedAfterAccept}）；点“放弃”先变“{abandonText}”（{armed}）、再点才放弃（{abandoned}，B04）");
            Expect(enAll.Length > 0 && !ContainsCjk(enAll) && !GameText.ContainsMarker(enAll), $"K3b 英文界面没有中文（{new string(enAll.Where(ch => ch >= 0x4E00 && ch <= 0x9FFF).Take(20).ToArray())}）");

            // HUD 追踪栏：行文字、折叠（进存档）、飞过去。
            GameObject hgo = null;
            try
            {
                VisualElement hroot = F.MountUxml(HudUxml, out hgo);
                ObjectiveHudUIToolkit hud = hgo.AddComponent<ObjectiveHudUIToolkit>();
                hud.BindView(hroot);
                hud.RefreshTracker(s);
                string text0 = hud.TrackRowText(0);
                bool shown = hud.TrackerShown && hud.TrackRowShown(0) && text0.Contains(QuestCatalog.Title(CampaignObjectiveTracker.DefOf(avail)));
                Click(hud.TrackFlyButton(0));
                Vector2? loc = CampaignObjectiveTracker.QuestLocation(s, avail);
                bool flew = loc.HasValue && CampaignObjectiveTracker.LastFlyTarget == loc.Value;
                Click(hud.TrackerToggleButton);
                bool collapsed = s.Quests.TrackerCollapsed && !hud.TrackRowShown(0) && hud.TrackerShown;
                Click(hud.TrackerToggleButton);
                bool expanded = !s.Quests.TrackerCollapsed && hud.TrackRowShown(0);
                Expect(shown && flew && collapsed && expanded && text0.Contains("秒"),
                    $"K4 HUD 追踪栏：“{text0}”；点“飞”→ 镜头飞到任务地点；可以收起 / 展开（记进存档）");
            }
            finally
            {
                if (hgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(hgo);
                }
            }
            string hudProbe = UiToolkitLayoutProbe.Probe(HudUxml, "ObjectiveHudColumn", stressFill: true);
            Expect(hudProbe.StartsWith("PASS"), $"K5 目标条 + 追踪栏布局探针：{string.Join(" / ", hudProbe.Split('\n').Take(3))}");

            // 地图标记（FGR-QST-003；DEBT-FG3GEN01-06 任务点）。
            var model = new WorldMapModel();
            Vector2 at = new Vector2(avail.AnchorX, avail.AnchorY);
            var view = new WorldMapView { CenterX = at.x, CenterY = at.y, HalfWidth = 400, CanvasWidth = 800, CanvasHeight = 600 };
            model.Collect(s, view, 400);
            bool trackedIcon = model.Items.Any(i => i.Kind == WorldMapItemKind.QuestTracked && i.Id == "quest:" + avail.ObjectiveId && i.Label.Contains("追踪"));
            var view2 = new WorldMapView { CenterX = main.AnchorX, CenterY = main.AnchorY, HalfWidth = 400, CanvasWidth = 800, CanvasHeight = 600 };
            model.Collect(s, view2, 400);
            bool plainIcon = model.Items.Any(i => i.Kind == WorldMapItemKind.Quest && i.Id == "quest:" + main.ObjectiveId);
            WorldMapFilters.Set(WorldMapLayer.Markers, false);
            model.Collect(s, view2, 400);
            bool filtered = !model.Items.Any(i => i.Kind == WorldMapItemKind.Quest);
            WorldMapFilters.ResetAll();
            Expect(trackedIcon && plainIcon && filtered, "K6 战略地图 / 小地图：追踪中的任务换醒目样式（◆），其余进行中的任务也有标记；归在“标记”图层，可以筛掉");

            // 暂停菜单里的家园指引开关（接在已有设置段里）。
            GameObject pgo = null;
            try
            {
                VisualElement proot = F.MountUxml("Assets/GameRes/Raw/UI/UiKit/PauseMenu.uxml", out pgo);
                PauseMenuUIToolkit pm = pgo.AddComponent<PauseMenuUIToolkit>();
                pm.BindView(proot);
                pm.SyncReactionToggles();
                Toggle t = pm.HomeGuideToggle;
                bool initial = t != null && t.value;
                if (t != null)
                {
                    t.value = false;
                }
                bool off = !GameSettings.HomeGuideQuestsEnabled;
                if (t != null)
                {
                    t.value = true;
                }
                Expect(initial && off && GameSettings.HomeGuideQuestsEnabled && t.label == GameText.Get("pause.home_guide"),
                    $"K7 设置里关闭家园指引：暂停菜单设置段的开关“{t?.label}”，勾选立即生效");
            }
            finally
            {
                if (pgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(pgo);
                }
            }

            // 统计（B19）与 OBJ 目标通知带位置（DEBT-FG0UX01-12 目标类）。
            var lines = new List<(string Text, string Cls)>();
            CampaignObjectiveTracker.BuildStatsLines(s, lines);
            NotificationEntry objNew = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == "objective_new");
            Expect(lines.Count == 2 && lines[1].Text.Contains("放弃 1") && objNew?.Latest != null && objNew.Latest.HasLocation,
                $"K8 统计面板“{lines.ElementAtOrDefault(1).Text}”；战役目标的“新目标”通知带位置（点击可定位，{objNew?.Latest?.HasLocation}）");
        }

        // ── L 种子集 / M 性能 ────────────────────────────────────────────────────

        private static void CheckSeedSetAndPerf()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach (int seed in new[] { 111, 222, 333 })
            {
                CampaignState s = NewWorld(seed);
                CampaignObjectiveTracker.Recompute(s);
                ObjectiveRecord silent = CampaignObjectiveTracker.FindQuest(s, "main.key.silent");
                PlannedTerritory t = WorldGenService.PlanFor(s).Find("silent");
                WorldFeature f = AllSides(s).FirstOrDefault();
                Quest a = f != null ? CampaignObjectiveTracker.PickSide(s, f) : null;
                Quest b = f != null ? CampaignObjectiveTracker.PickSide(s, f) : null;
                bool one = silent != null && silent.AnchorX == t.CenterX && silent.AnchorY == t.CenterY && f != null && a != null && a.Id == b?.Id && WorldGenQuery.TerritoryAt(s, f.X, f.Y)?.Id == f.TerritoryId;
                ok &= one;
                lines.Add($"{seed}:{silent?.AnchorX},{silent?.AnchorY}/{f?.Id}={a?.Id}");
            }
            Expect(ok, $"L1 B25 种子集：3 个种子的主线地点 = 各自的领地中心，支线地点在领地里、抽到的支线由（种子, 地点）确定（{string.Join("；", lines)}）");

            CampaignState w = NewWorld(8411);
            CampaignObjectiveTracker.Recompute(w);
            // 后期规模：40 个进行中的支线（一个世界的真实支线地点只有十来个，这里是 3 倍以上的压测，用测试地点）。
            var all = Enumerable.Range(0, 40).Select(i => Synthetic(w, i, 150f + i * 7f)).ToList();
            int accepted = 0;
            foreach (WorldFeature f in all)
            {
                ObjectiveRecord r = Offer(w, f, accepted % 2 == 0 ? "side.data" : "side.investigate");
                if (CampaignObjectiveTracker.TryAccept(w, r.ObjectiveId, out _))
                {
                    accepted++;
                }
            }
            Vector2 near = CoreNear(w);
            for (int i = 0; i < 120; i++)
            {
                Spawn(w, near + new Vector2(i % 12, i / 12), HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            }
            StepSeconds(1);
            CampaignObjectiveTracker.ResetQuestStats();
            StepSeconds(5);
            double ms = CampaignObjectiveTracker.QuestCheckMaxMs;
            long checks = CampaignObjectiveTracker.QuestChecks;
            PerfGate.Expect(checks >= 4 && accepted >= 40,
                $"M1 性能：{accepted} 个进行中的到达 / 停留类支线 + {MachineRegistry.AllRecords.Count()} 台机器时，每 {CampaignObjectiveTracker.CheckSeconds} 游戏秒一次任务检查（{checks} 次），最长 {ms:0.###} ms（预算 {CampaignObjectiveTracker.QuestPerfMs} ms；Editor batchmode，只在检查时、不按帧）",
                new[] { PerfGate.Le(ms, CampaignObjectiveTracker.QuestPerfMs, "quest.check_max_ms") }, (o, m) => Expect(o, m), Line);
            PerfLines.Add($"任务：{accepted} 个进行中的支线 + {MachineRegistry.AllRecords.Count()} 台机器时一次检查最长 {ms:0.###} ms（Editor batchmode；真机 IL2CPP 归 FG15-SYS-02，DEBT-FG8QST01-06）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static ObjectiveRecord Offer(CampaignState s, WorldFeature f, string defId)
        {
            ObjectiveRecord r = CampaignObjectiveTracker.OfferSideForTests(s, f, defId);
            if (r == null || r.DefId != defId)
            {
                Fail($"夹具：在 {f?.Id} 上登记 {defId} 失败（已有 {r?.DefId}）");
            }
            return r;
        }

        /// <summary>
        /// 测试用的“支线地点”：核心外 <paramref name="dist"/> 格、第 <paramref name="i"/> 个方向上最近的可走格（真实的支线地点在每个世界只有寥寥几个，且都在敌方领地深处；
        /// 到达 / 停留 / 护送 / 追踪 / 存读档 / 倍速这些判定与地点在哪片领地无关，用核心附近的安全地点测，真实地点的揭开与抽取在 D1 / D4 / L1 断言）。
        /// </summary>
        private static WorldFeature Synthetic(CampaignState s, int i, float dist)
        {
            GridCell c = HomeGridService.CorePivot(s);
            float ang = (i % 8) * Mathf.PI / 4f + (i / 8) * 0.17f;
            var raw = new GridCell(c.X + Mathf.RoundToInt(Mathf.Cos(ang) * dist), c.Y + Mathf.RoundToInt(Mathf.Sin(ang) * dist));
            GridCell ok = SiegeService.NearestPassable(raw, 12);
            return new WorldFeature { Id = "side:qtest:" + i + ":" + Mathf.RoundToInt(dist), Kind = WorldFeatureKind.SideSite, X = ok.X, Y = ok.Y, TerritoryId = "silent" };
        }

        /// <summary>全部阵营领地里的支线地点。</summary>
        private static List<WorldFeature> AllSides(CampaignState s)
        {
            var all = new List<WorldFeature>();
            foreach (string t in new[] { "silent", "foundry", "clarity", "overclock" })
            {
                all.AddRange(SideSites(s, t));
            }
            return all;
        }

        /// <summary>某个领地里的支线地点，离敌方据点最远的在前（机器在那里停留时不被据点驻军 / 巡逻打断，测的是任务判定本身）。</summary>
        private static List<WorldFeature> SafeSideSites(CampaignState s, string territoryId)
        {
            List<WorldFeature> all = SideSites(s, territoryId);
            var posts = WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed).ToList();
            return all.OrderByDescending(f => posts.Count == 0 ? long.MaxValue : posts.Min(o => (long)(o.CellX - f.X) * (o.CellX - f.X) + (long)(o.CellY - f.Y) * (o.CellY - f.Y)))
                .ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>某个领地里的支线地点（按离核心由近到远）。</summary>
        private static List<WorldFeature> SideSites(CampaignState s, string territoryId)
        {
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(territoryId);
            var list = new List<WorldFeature>();
            if (t == null)
            {
                return list;
            }
            WorldGenQuery.FeaturesIn(s, t.CenterX - t.Radius, t.CenterY - t.Radius, t.CenterX + t.Radius, t.CenterY + t.Radius, list);
            GridCell core = HomeGridService.CorePivot(s);
            return list.Where(f => f.Kind == WorldFeatureKind.SideSite && f.TerritoryId == territoryId)
                .OrderBy(f => (long)(f.X - core.X) * (f.X - core.X) + (long)(f.Y - core.Y) * (f.Y - core.Y)).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        private static int AlertCount(string contains) =>
            NotificationCenter.History.Where(e => e?.Type?.Id == "quest_alert").SelectMany(e => e.Members).Count(m => m.DetailText.Contains(contains));

        private static TbRemovedContent RemovedTable(params (string id, string kind, string nameKey, int refund, int ver)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString(r.id);
                buf.WriteString(r.kind);
                buf.WriteString(r.nameKey);
                buf.WriteInt(r.refund);
                buf.WriteInt(r.ver);
            }
            return new TbRemovedContent(buf);
        }

        private static void StepSeconds(int seconds) => WorldSimulation.StepMany(GameClock.StepHz * seconds);

        private static bool ContainsCjk(string s) => s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static void Click(Button b)
        {
            if (b == null || !b.enabledInHierarchy)
            {
                return;
            }
            using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        /// <summary>测试捷径：把一台机器挪到 <paramref name="at"/>（战斗内核里的位置）。</summary>
        private static void MoveTo(int logicId, Vector2 at)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(at.x, at.y));
            }
        }

        private static Vector2 Pos(int logicId) =>
            MachineRegistry.TryGetLivePosition(logicId, out Vector2 p) ? p : MachineRegistry.TryGetRecord(logicId, out MachineRecord m) ? m.WorldPosition : Vector2.zero;

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static string Name(int id) => Rec(id) != null ? MachineNaming.Short(Rec(id)) : "#" + id;

        private static Vector2 CoreNear(CampaignState s)
        {
            GridCell c = HomeGridService.CorePivot(s);
            GridCell ok = SiegeService.NearestPassable(new GridCell(c.X + 5, c.Y - 5), 8);
            return new Vector2(ok.X, ok.Y);
        }

        private static CampaignState NewWorld(int seed, bool observe = true, int version = 3)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignObjectiveTracker.ResetQuestSession();
            CampaignState s = CampaignState.CreateNew("fgquest-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
            return s;
        }

        private static CampaignState Reload(int fromSlot)
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
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(1);
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at, string chassis, string blueprint)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            WorldSimulation.StepMany(2);
            return r.Success ? r.LogicId : 0;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgQuestSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.SetHomeGuideQuestsEnabled(true);
                WorldMapFilters.ResetAll();
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
