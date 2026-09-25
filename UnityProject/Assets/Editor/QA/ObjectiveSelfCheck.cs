using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.UI.Expedition;
using GameLogic.UI.Objective;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8 收尾（DEBT-ER6LOOP01-01 / AC-CAM-001 / ERD-UI-002）：战役目标条、任务日志与战役地图的回归闸门——
    /// OBJ-01～04 的激活/完成顺序与幂等、跳步与旧档的“被超越”补记、目标条与日志的真实文字、任务日志键、
    /// 两个面板在 UI 缩放 0.8/1.0/1.4 下塞满最长真实文字的布局。并入 <c>CellFrameworkValidate.RunAll</c>。
    /// 会清空机器登记表，Play 模式下跳过。
    /// </summary>
    public static class ObjectiveSelfCheck
    {
        private const string HudUxml = "Assets/GameRes/Raw/UI/Objective/ObjectiveHud.uxml";
        private const string LogUxml = "Assets/GameRes/Raw/UI/Objective/MissionLog.uxml";
        // FG0-UX-01（FGR-UX-060）：UI 缩放上限从 140% 提到 150%，极值按新上限测。
        private static readonly float[] UiScales = { 0.8f, 1f, 1.5f };

        // 与 FeedbackCueSelfCheck 同一张禁用词表（唯一来源 ThemeLexicon，含 0.2 名表禁用词）与内部 ID 形态。
        private static readonly Regex ForbiddenWords = ThemeLexicon.Forbidden;
        private static readonly Regex InternalIdPattern = new Regex(@"[a-z]+_[a-z0-9_]+|OBJ-\d|home_valley|placeholder|TODO", RegexOptions.IgnoreCase);

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：战役目标与任务日志")]
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
            Line("\n[目标] 战役目标条 / 任务日志 / 战役地图（ER8 收尾 DEBT-ER6LOOP01-01 / AC-CAM-001）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（自检会清空机器登记表）");
                return 0;
            }
            try
            {
                CheckCatalogText();
                CheckChecklistProgression();
                CheckSkippedStepSuperseded();
                CheckOldSaveSuperseded();
                CheckHudAndLogText();
                CheckExpeditionStatus();
                CheckKeyBinding();
                CheckPanelLayout();
            }
            catch (Exception e)
            {
                Fail($"目标自检抛异常：{e}");
            }
            finally
            {
                MachineRegistry.ResetForNewCampaign();
                FeedbackCues.ResetForTests();
            }
            return _fail;
        }

        // ── 目标表 ──────────────────────────────────────────────────

        private static void CheckCatalogText()
        {
            ObjectiveDef[] all = CampaignObjectiveCatalog.All;
            bool ordered = all.Length == 10;
            for (int i = 0; ordered && i < all.Length; i++)
            {
                ordered = all[i].Id == $"OBJ-{i + 1:00}";
            }
            Expect(ordered, $"目标表按顺序列出 OBJ-01～10（共 {all.Length} 项）");

            var bad = new List<string>();
            foreach (ObjectiveDef def in all)
            {
                if (def.Items == null || def.Items.Length == 0 || def.Items.Length > ObjectiveHudView.ItemSlots)
                {
                    bad.Add($"{def.Id} 清单项数 {def.Items?.Length ?? 0}（目标条只有 {ObjectiveHudView.ItemSlots} 行）");
                }
                if (CampaignObjectiveCatalog.RegionDisplayName(def.RegionId) == "未知区域")
                {
                    bad.Add($"{def.Id} 区域 {def.RegionId} 没有玩家可见名");
                }
                foreach (string text in new[] { def.Title }.Concat(def.Items?.Select(item => item.Label) ?? Enumerable.Empty<string>()))
                {
                    if (string.IsNullOrWhiteSpace(text) || IsLeaky(text))
                    {
                        bad.Add($"{def.Id} “{text}”");
                    }
                }
            }
            Expect(bad.Count == 0, "每个目标都有玩家可见标题、区域名与 1～4 项清单，文字无内部 ID/禁用词" +
                                   (bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad)));
        }

        // ── OBJ-01～04 顺序、幂等与提示 ──────────────────────────────────

        private static void CheckChecklistProgression()
        {
            MachineRegistry.ResetForNewCampaign();
            FeedbackCues.ResetForTests();
            CampaignState state = NewHomeState("objective-selfcheck-chain", 11);

            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj01) == ObjectiveState.Active
                   && State(state, CampaignObjectiveTracker.Obj02) == ObjectiveState.Locked
                   && CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj01,
                "新战役第一次重算：OBJ-01 进行中、OBJ-02 未解锁、当前目标＝OBJ-01");
            Expect(FeedbackCues.CountOf(FeedbackCueId.ObjectiveActivated) == 1, "OBJ-01 出现时发一次“新目标”提示");
            Expect(MarkerAt(state, GeneratorPos, out string marker), $"世界定位针标在发电机上（实际 {marker}）");

            CampaignObjectiveTracker.Recompute(state);
            CampaignObjectiveTracker.Recompute(state);
            Expect(FeedbackCues.CountOf(FeedbackCueId.ObjectiveActivated) == 1 && state.ObjectiveRecords.Length == 1,
                "重复重算不重复提示、不重复建记录");

            SetBuilding(state, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj01) == ObjectiveState.Completed
                   && State(state, CampaignObjectiveTracker.Obj02) == ObjectiveState.Active
                   && FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete) == 1
                   && HasEvent(state, $"objective_complete:{state.CampaignId}:{CampaignObjectiveTracker.Obj01}")
                   && state.CompletedObjectiveIds.Contains(CampaignObjectiveTracker.Obj01),
                "修好发电机 → OBJ-01 完成（记完成事件、写入已完成列表、提示一次），OBJ-02 接着出现");

            SetBuilding(state, HomeValleyLayout.BuildingTypeWarehouse, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            SetBuilding(state, HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational, BuildingPowerState.Unpowered);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj02) == ObjectiveState.Active,
                "装配站修好但没通电 → OBJ-02 仍进行中（“通电运转”一项未达成）");

            SetBuilding(state, HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational, BuildingPowerState.Powered);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj02) == ObjectiveState.Completed
                   && State(state, CampaignObjectiveTracker.Obj03) == ObjectiveState.Active,
                "装配站通电 → OBJ-02 完成，OBJ-03 出现");

            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id,
                HomeValleyLayout.RegionId, Vector2.zero, 120f, 120f);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj03) == ObjectiveState.Active,
                "只生产了战斗履带机、还没直控命中 → OBJ-03 仍进行中");
            Expect(MarkerAt(state, TargetPos, out marker), $"定位针移到下一步：低威胁残骸靶（实际 {marker}）");

            CampaignEventLedger.TryGrant(state, CampaignObjectiveCatalog.DirectHitEventId, "DirectHit", state.PlaySeconds, "selfcheck");
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj03) == ObjectiveState.Completed
                   && State(state, CampaignObjectiveTracker.Obj04) == ObjectiveState.Active,
                "直控命中事件授予 → OBJ-03 完成，OBJ-04 出现");

            SetBuilding(state, HomeValleyLayout.BuildingTypeSignalTower, BuildingConstructionState.Operational, BuildingPowerState.Powered);
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id,
                HomeValleyLayout.RegionId, Vector2.zero, 100f, 100f);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj04) == ObjectiveState.Active,
                $"信号塔修好但只有 2 台可出征（要 {ExpeditionDepartureService.MinRosterSize} 台）→ OBJ-04 仍进行中");

            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id,
                HomeValleyLayout.RegionId, Vector2.zero, 100f, 100f);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj04) == ObjectiveState.Completed
                   && State(state, CampaignObjectiveTracker.Obj05) == ObjectiveState.Active
                   && CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj05,
                "第 3 台可出征 → OBJ-04 完成，OBJ-05“破碎都市”在出发前就出现在目标条上");
            Expect(MarkerAt(state, SignalTowerPos, out marker), $"目标在远征区域时，家园定位针标在出发点信号塔（实际 {marker}）");
            Expect(state.CampaignPhase == CampaignPhase.Landing, $"OBJ-01～04 不推进战役阶段（仍为 Landing，实际 {state.CampaignPhase}）");

            for (int i = 0; i < 5; i++)
            {
                CampaignObjectiveTracker.Recompute(state);
            }
            int completionEvents = state.EventLedger.Count(e => e.EventId.StartsWith("objective_complete:", StringComparison.Ordinal));
            Expect(FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete) == 4 && completionEvents == 4
                   && FeedbackCues.CountOf(FeedbackCueId.ObjectiveActivated) == 5 && ActiveCount(state) == 1,
                $"再重算 5 次：完成提示仍 4 次、完成事件仍 4 条、出现提示 5 次、进行中目标恰 1 个" +
                $"（实际 {FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete)}/{completionEvents}/" +
                $"{FeedbackCues.CountOf(FeedbackCueId.ObjectiveActivated)}/{ActiveCount(state)}）");

            // OBJ-05：清单“全部达成”与追踪器判定同源。
            RegionRecord ruins = EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Available);
            ruins.ExpeditionCount = 1;
            AddQuest(state, FracturedCityLayout.RegionId, FracturedCityLayout.MarkerModuleContentId, RegionQuestItemState.Recovered);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj05) == ObjectiveState.Active
                   && !CampaignObjectiveCatalog.AllItemsDone(state, CampaignObjectiveTracker.Obj05),
                "只带回静默标记模块 → OBJ-05 仍进行中，清单也未全部达成");
            AddQuest(state, FracturedCityLayout.RegionId, FracturedCityLayout.ProtocolDataboxContentId, RegionQuestItemState.Recovered);
            CampaignObjectiveTracker.Recompute(state);
            Expect(State(state, CampaignObjectiveTracker.Obj05) == ObjectiveState.Completed
                   && CampaignObjectiveCatalog.AllItemsDone(state, CampaignObjectiveTracker.Obj05)
                   && CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj06,
                "两件关键物都带回 → OBJ-05 完成且清单全部达成（两边同源），当前目标＝OBJ-06");
            Expect(MarkerAt(state, AnalysisPos, out marker), $"OBJ-06 第一步去解析台（实际 {marker}）");
            state.UnlockedContentIds = new[] { ComponentCatalog.FuncMarkerId, FirmwareCatalog.FwMarkTagId };
            Expect(!CampaignObjectiveCatalog.TryGetHomeMarker(state, out _),
                "下一步是在蓝图编辑器里保存（不在具体位置）→ 定位针隐藏，不指错地方");
        }

        // ── 跳步与旧档 ────────────────────────────────────────────────

        /// <summary>玩家没做 OBJ-03 的直控命中就凑够机器出征：OBJ-03/04 被 OBJ-05 超越，静默补记完成。</summary>
        private static void CheckSkippedStepSuperseded()
        {
            MachineRegistry.ResetForNewCampaign();
            FeedbackCues.ResetForTests();
            CampaignState state = NewHomeState("objective-selfcheck-skip", 12);
            SetBuilding(state, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            SetBuilding(state, HomeValleyLayout.BuildingTypeWarehouse, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            SetBuilding(state, HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational, BuildingPowerState.Powered);
            CampaignObjectiveTracker.Recompute(state);
            Expect(CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj03, "跳步用例前置：当前目标停在 OBJ-03");

            int completeCues = FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete);
            RegionRecord ruins = EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Active);
            ruins.ExpeditionCount = 1;
            state.CurrentRegionId = FracturedCityLayout.RegionId;
            CampaignObjectiveTracker.OnDeparted(state, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            Expect(State(state, CampaignObjectiveTracker.Obj03) == ObjectiveState.Completed
                   && State(state, CampaignObjectiveTracker.Obj04) == ObjectiveState.Completed
                   && CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj05
                   && ActiveCount(state) == 1,
                "没直控命中就出征 → OBJ-03/04 被 OBJ-05 超越补记完成，目标条不再停在已走过的步骤");
            Expect(FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete) == completeCues,
                "补记完成是静默的：不弹“目标完成”");
            Expect(state.CampaignPhase == CampaignPhase.FirstExpedition, $"出征照常进入 FirstExpedition（实际 {state.CampaignPhase}）");
        }

        /// <summary>本改动之前的存档：OBJ-05 已有记录，OBJ-01～04 从未记录，直控命中事件也不存在。</summary>
        private static void CheckOldSaveSuperseded()
        {
            MachineRegistry.ResetForNewCampaign();
            FeedbackCues.ResetForTests();
            CampaignState state = NewHomeState("objective-selfcheck-oldsave", 13);
            SetBuilding(state, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Available).ExpeditionCount = 1;
            state.ObjectiveRecords = new[]
            {
                new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj05, State = ObjectiveState.Active, StartedAtPlaySeconds = 600f },
            };
            CampaignObjectiveTracker.Recompute(state);
            bool firstFourDone = new[]
            {
                CampaignObjectiveTracker.Obj01, CampaignObjectiveTracker.Obj02, CampaignObjectiveTracker.Obj03, CampaignObjectiveTracker.Obj04,
            }.All(id => State(state, id) == ObjectiveState.Completed);
            Expect(firstFourDone && CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj05 && ActiveCount(state) == 1,
                "旧档读入 → OBJ-01～04 补记完成，当前目标仍是 OBJ-05");
            Expect(FeedbackCues.CountOf(FeedbackCueId.ObjectiveComplete) == 0 && FeedbackCues.CountOf(FeedbackCueId.ObjectiveActivated) == 0,
                "旧档补记不弹任何目标提示（读档不刷屏）");
        }

        // ── 目标条与日志的真实文字 ─────────────────────────────────────────

        private static void CheckHudAndLogText()
        {
            MachineRegistry.ResetForNewCampaign();
            FeedbackCues.ResetForTests();
            CampaignState state = NewHomeState("objective-selfcheck-text", 14);
            var view = new ObjectiveHudView();

            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(!view.Visible, "第一次重算之前没有进行中的目标 → 目标条不显示");

            state.CurrentRegionId = HomeValleyLayout.RegionId;
            CampaignObjectiveTracker.Recompute(state);
            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(view.Visible && view.Title == "目标 1/10：修复归还谷地供电" && view.Region == "地点：归还谷地｜进度 0/1"
                   && view.ItemCount == 1 && view.ItemLabels[0] == "修复发电机" && !view.ItemDone[0],
                $"目标条：标题/地点/进度/清单（实际“{view.Title}”“{view.Region}”）");

            SetBuilding(state, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            SetBuilding(state, HomeValleyLayout.BuildingTypeWarehouse, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            CampaignObjectiveTracker.Recompute(state);
            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(view.Title == "目标 2/10：恢复仓库与装配站" && view.ItemCount == 2 && view.ItemDone[0] && !view.ItemDone[1]
                   && view.Region.EndsWith("进度 1/2", StringComparison.Ordinal),
                $"清单逐项打勾：仓库 √、装配站 ○、进度 1/2（实际“{view.Region}”）");

            state.CurrentRegionId = FracturedCityLayout.RegionId;
            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(view.Region.Contains("你现在在破碎都市"), $"人在别的区域时写明“你现在在哪”（实际“{view.Region}”）");

            MissionLogUIToolkit.ComposeObjectiveRow(state, 0, out ObjectiveState s0, out string st0, out string t0, out _);
            MissionLogUIToolkit.ComposeObjectiveRow(state, 1, out ObjectiveState s1, out string st1, out string t1, out string r1);
            MissionLogUIToolkit.ComposeObjectiveRow(state, 6, out ObjectiveState s6, out string st6, out string t6, out string r6);
            Expect(s0 == ObjectiveState.Completed && st0.StartsWith("已完成 ", StringComparison.Ordinal) && t0 == "1. 修复归还谷地供电",
                $"日志：已完成的目标可回看（实际“{st0}”“{t0}”）");
            Expect(s1 == ObjectiveState.Active && st1 == "进行中" && t1 == "2. 恢复仓库与装配站" && r1 == "归还谷地",
                $"日志：进行中的目标（实际“{st1}”“{t1}”“{r1}”）");
            Expect(s6 == ObjectiveState.Locked && st6 == "未解锁" && t6 == "7. ？？？" && r6 == string.Empty,
                $"日志：未解锁的目标不剧透（实际“{st6}”“{t6}”）");
            Expect(MissionLogUIToolkit.ComposeStatus(state).StartsWith("已完成 1/10", StringComparison.Ordinal), "日志页眉：已完成 1/10");

            MissionLogUIToolkit.ComposeRegionRow(state, FracturedCityLayout.RegionId, out string cityName, out string cityState, out string cityDetail);
            Expect(cityName == "破碎都市 ◆" && cityState == "未解锁" && cityDetail.Contains("你现在在这里")
                   && cityDetail.Contains("解锁条件：修复归还谷地的信号塔"),
                $"地图：当前所在区域加 ◆；未解锁时写明解锁条件（实际“{cityName}”“{cityState}”“{cityDetail}”）");
            MissionLogUIToolkit.ComposeRegionRow(state, FoundryOutpostLayout.RegionId, out _, out string foundryState, out string foundryDetail);
            Expect(foundryState == "未解锁" && foundryDetail.Contains("解锁条件：完成目标 6「解析并实装标记跳转」"),
                $"地图：铸造前哨外围写明需完成目标 6（实际“{foundryDetail}”）");
            MissionLogUIToolkit.ComposeRegionRow(state, HomeValleyLayout.RegionId, out _, out _, out string homeDetail);
            Expect(homeDetail.Contains("当前目标在这里：恢复仓库与装配站"), $"地图：当前目标所在区域标明（实际“{homeDetail}”）");

            EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Active).ExpeditionCount = 1;
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            MissionLogUIToolkit.ComposeRegionRow(state, FracturedCityLayout.RegionId, out _, out cityState, out cityDetail);
            Expect(cityState == "未肃清，可再出征" && cityDetail.Contains("已出征 1 次"),
                $"地图：出征过但人已回家，不写“远征中”（实际“{cityState}”“{cityDetail}”）");

            RegionRecord ruins = EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Cleared);
            ruins.ExpeditionCount = 2;
            AddQuest(state, FracturedCityLayout.RegionId, FracturedCityLayout.MarkerModuleContentId, RegionQuestItemState.Recovered);
            AddQuest(state, FracturedCityLayout.RegionId, FracturedCityLayout.ProtocolDataboxContentId, RegionQuestItemState.Recovered);
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            MissionLogUIToolkit.ComposeRegionRow(state, FracturedCityLayout.RegionId, out cityName, out cityState, out cityDetail);
            MissionLogUIToolkit.ComposeRegionRow(state, HomeValleyLayout.RegionId, out string homeName, out string homeState, out _);
            Expect(cityName == "破碎都市" && cityState == "已肃清" && cityDetail.Contains("已出征 2 次") && cityDetail.Contains("已带回：")
                   && homeName == "归还谷地 ◆" && homeState == "家园",
                $"地图：出征次数与带回内容、家园行（实际“{cityState}”“{cityDetail}”）");

            string gate = MissionLogUIToolkit.ComposeGateText(state);
            Expect(gate.Contains("○ 重炮未解析") && gate.Contains("○ 未保存熔穿过载蓝图") && gate.Contains("○ 没有现役机实装")
                   && gate.Contains("三灯全亮才能进入"),
                $"核心门三灯分别写明缺什么（UI-14，实际“{gate}”）");

            // 所有合成出来的文字：无内部 ID、无禁用词。
            var leaks = new List<string>();
            foreach (string text in AllComposedText(state, view))
            {
                if (IsLeaky(text))
                {
                    leaks.Add(text);
                }
            }
            Expect(leaks.Count == 0, "目标条/日志/地图合成的全部文字无内部 ID、无禁用词" + (leaks.Count == 0 ? string.Empty : "：" + string.Join("；", leaks)));
        }

        // ── 远征中的目标现状（UI-10）与核心进攻撤离用语 ─────────────────────────

        private static void CheckExpeditionStatus()
        {
            MachineRegistry.ResetForNewCampaign();
            FeedbackCues.ResetForTests();
            CampaignState state = NewHomeState("objective-selfcheck-expedition", 15);
            string marker = FracturedCityLayout.MarkerModuleContentId;
            string databox = FracturedCityLayout.ProtocolDataboxContentId;

            Expect(CampaignObjectiveCatalog.QuestStatusText(state, marker) == "摧毁监听节点后掉落"
                   && CampaignObjectiveCatalog.QuestStatusText(state, databox) == "读取协议终端后获得"
                   && CampaignObjectiveCatalog.QuestStatusText(state, FoundryOutpostLayout.CannonModuleContentId) == "击破步进炮后掉落"
                   && CampaignObjectiveCatalog.QuestStatusText(state, FoundryOutpostLayout.CoreDataContentId) == "摧毁主核心后掉落",
                "还没出现的关键物：写明怎么获得（标记器/数据盒/重炮/核心数据各写获得方式）");

            AddQuest(state, FracturedCityLayout.RegionId, marker, RegionQuestItemState.OnGround);
            Expect(CampaignObjectiveCatalog.QuestStatusText(state, marker) == "在地面，还没装车", "关键物在地面 → “在地面，还没装车”");

            int carrier = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id,
                FracturedCityLayout.RegionId, Vector2.zero, 120f, 120f).LogicId;
            MachineRegistry.TryGetRecord(carrier, out MachineRecord carrierRecord);
            state.RegionQuestItems.First(q => q.ContentId == marker).State = RegionQuestItemState.Carried;
            state.RegionQuestItems.First(q => q.ContentId == marker).CarrierLogicId = carrier;
            string carried = CampaignObjectiveCatalog.QuestStatusText(state, marker);
            Expect(carried == $"已装上 #{carrierRecord.DisplayNumber}，撤离后才算带回",
                $"装车不等于带回：写明装在哪台、撤离后才算（实际“{carried}”）");

            state.RegionQuestItems = state.RegionQuestItems.Append(new RegionQuestItemRecord
            {
                SalvageInstanceId = "selfcheck:databox-lost", RegionId = FracturedCityLayout.RegionId, ContentId = databox, State = RegionQuestItemState.Lost,
            }).Append(new RegionQuestItemRecord
            {
                SalvageInstanceId = "selfcheck:databox-respawn", RegionId = FracturedCityLayout.RegionId, ContentId = databox, State = RegionQuestItemState.OnGround,
            }).ToArray();
            Expect(CampaignObjectiveCatalog.QuestStatusText(state, databox) == "在地面，还没装车",
                "丢失后恢复柜补发了新实例 → 显示“还能捡”，不显示旧的“已丢失”");

            // 人在破碎都市：目标条逐项写现状 + 可选废料箱进度。
            SetBuilding(state, HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Operational, BuildingPowerState.NotApplicable);
            EnsureRegion(state, FracturedCityLayout.RegionId, RegionState.Active).ExpeditionCount = 1;
            RegionRecord ruins = state.RegionRecords.First(r => r.RegionId == FracturedCityLayout.RegionId);
            ruins.LootedContainerIds = new[] { FracturedCityLayout.Crate1Id };
            state.GroundItems = new[]
            {
                new GroundItemRecord { GroundItemId = "selfcheck:pile", RegionId = FracturedCityLayout.RegionId, ResourceType = "Scrap", Amount = 40 },
            };
            state.CurrentRegionId = FracturedCityLayout.RegionId;
            CampaignObjectiveTracker.OnDeparted(state, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            var view = new ObjectiveHudView();
            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(CampaignObjectiveTracker.CurrentObjectiveId(state) == CampaignObjectiveTracker.Obj05 && view.ItemCount == 3
                   && view.ItemText(0) == "从信号塔出发前往破碎都市"
                   && view.ItemText(1).EndsWith("：" + carried, StringComparison.Ordinal)
                   && view.ItemText(2).EndsWith("：在地面，还没装车", StringComparison.Ordinal),
                $"远征目标栏：已完成项不加注，关键物逐项写现状（实际“{view.ItemText(1)}”“{view.ItemText(2)}”）");
            Expect(view.Note == "可选：废料箱已开 1/3，地面还有 1 堆没装车", $"远征目标栏：可选废料箱进度（实际“{view.Note}”）");
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            ObjectiveHudUIToolkit.Compose(state, view);
            Expect(view.Note == string.Empty, "人不在该区域时不显示废料箱进度");

            // 核心进攻：节点进度、撤离面板列核心数据、撤离用语。
            FoundryOutpostRegion.EnsureRegionRecordSeeded(state);
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            string[] scoutKeys = ExpeditionReturnService.KeyContentIdsFor(state, FoundryOutpostLayout.RegionId, out bool scoutAssault, out _);
            Expect(!scoutAssault && scoutKeys.Length == 1 && scoutKeys[0] == FoundryOutpostLayout.CannonModuleContentId,
                "外围侦察撤离：关键物列重炮");

            foundry.State = RegionState.Cleared;
            FoundryOutpostCoreBoss.EnsureInitialized(state, foundry);
            ObjectiveDef obj09 = CampaignObjectiveCatalog.Get(CampaignObjectiveTracker.Obj09);
            string nodes = obj09.Items[0].Status(state);
            FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode1Id).IsAlive = false;
            string oneNode = obj09.Items[0].Status(state);
            Expect(nodes == "已摧毁 0/2" && oneNode == "已摧毁 1/2", $"核心进攻：供能节点进度（实际“{nodes}”→“{oneNode}”）");

            string[] coreKeys = ExpeditionReturnService.KeyContentIdsFor(state, FoundryOutpostLayout.RegionId, out bool assault, out bool destroyed);
            Expect(assault && !destroyed && coreKeys.Length == 1 && coreKeys[0] == FoundryOutpostLayout.CoreDataContentId,
                "核心进攻撤离：关键物改列核心数据（此前仍列早已带回的重炮）");

            string alive = ExpeditionReturnPanelUIToolkit.DescribeCoreAssault(Snapshot(false, false, RegionQuestItemState.OnGround));
            string wipe = ExpeditionReturnPanelUIToolkit.DescribeCoreAssault(Snapshot(true, false, RegionQuestItemState.OnGround));
            string noData = ExpeditionReturnPanelUIToolkit.DescribeCoreAssault(Snapshot(false, true, RegionQuestItemState.OnGround));
            string aboard = ExpeditionReturnPanelUIToolkit.DescribeCoreAssault(Snapshot(false, true, RegionQuestItemState.Carried));
            Expect(alive.Contains("本次核心进攻作废") && wipe.Contains("全灭") && noData.Contains("先装车再撤离") && aboard.Contains("返航信标")
                   && !(alive + wipe + noData + aboard).Contains("侦察"),
                $"核心进攻撤离用语四种情况各不相同，且不再说“侦察成功/核心区仍封锁”（实际“{aboard}”）");
            Expect(FeedbackCues.QuestItemName(FoundryOutpostLayout.CoreDataContentId) == "核心数据", "核心数据有玩家可见名（拾取字幕、撤离面板）");
        }

        private static ExpeditionReturnService.ReturnSnapshot Snapshot(bool wipe, bool bossDestroyed, RegionQuestItemState dataState) =>
            new ExpeditionReturnService.ReturnSnapshot(true, wipe, null,
                new[] { new ExpeditionReturnService.KeyTechEntry(FoundryOutpostLayout.CoreDataContentId, dataState, "selfcheck") },
                true, 0, FoundryOutpostLayout.RegionId, coreAssault: true, bossDestroyed: bossDestroyed);

        private static IEnumerable<string> AllComposedText(CampaignState state, ObjectiveHudView view)
        {
            ObjectiveHudUIToolkit.Compose(state, view);
            yield return view.Title;
            yield return view.Region;
            yield return view.Note;
            for (int i = 0; i < view.ItemCount; i++)
            {
                yield return view.ItemText(i);
            }
            for (int i = 0; i < CampaignObjectiveCatalog.All.Length; i++)
            {
                MissionLogUIToolkit.ComposeObjectiveRow(state, i, out _, out string st, out string title, out string region);
                yield return st;
                yield return title;
                yield return region;
            }
            foreach (string regionId in MissionLogUIToolkit.MapRegionIds)
            {
                MissionLogUIToolkit.ComposeRegionRow(state, regionId, out string name, out string rs, out string detail);
                yield return name;
                yield return rs;
                yield return detail;
            }
            yield return MissionLogUIToolkit.ComposeStatus(state);
            yield return MissionLogUIToolkit.ComposeGateText(state);
        }

        // ── 任务日志键 ────────────────────────────────────────────────

        private static void CheckKeyBinding()
        {
            // FG0-UX-01：默认键按 FG13 第 5 节合并为 L（J 让给“跳回上一台机器”）；冲突按上下文判定。
            KeyCode key = InputBindingSet.GetHardcodedDefault(GameActionId.ToggleMissionLog);
            var clash = new List<GameActionId>();
            InputBindingSet.CreateDefault().CollectConflicts(GameActionId.ToggleMissionLog, InputBindingSet.GetDefaultChord(GameActionId.ToggleMissionLog), clash);
            Expect(key == KeyCode.L && InputBindingSet.IsRebindable(GameActionId.ToggleMissionLog) && clash.Count == 0,
                $"任务日志键默认 L、可在设置里重绑、默认键位在任何上下文都不与其他动作冲突{(clash.Count == 0 ? string.Empty : "（冲突：" + string.Join(",", clash) + "）")}");
            Expect(ObjectiveHudUIToolkit.HintText().Contains(GameLogic.Settings.GameSettings.KeyBindings.GetKey(GameActionId.ToggleMissionLog).ToString()),
                "目标条提示里的按键跟随当前键位设置");
        }

        // ── 布局（塞满最长真实文字）────────────────────────────────────────

        private static void CheckPanelLayout()
        {
            string longestTitle = CampaignObjectiveCatalog.All.Select(d => d.Title).OrderByDescending(t => t.Length).First();
            string[] longestItems = CampaignObjectiveCatalog.All.SelectMany(d => d.Items).Select(i => i.Label)
                .OrderByDescending(t => t.Length).Take(ObjectiveHudView.ItemSlots).ToArray();

            void FillHud(VisualElement root)
            {
                SetLabel(root, "ObjectiveTitle", $"目标 10/10：{longestTitle}");
                SetLabel(root, "ObjectiveRegion", "地点：铸造前哨外围（你现在在归还谷地）｜进度 3/4");
                for (int i = 0; i < ObjectiveHudView.ItemSlots; i++)
                {
                    root.Q<VisualElement>("ObjectiveItem" + i)?.RemoveFromClassList("obj-item-hidden");
                    SetLabel(root, "ObjectiveItemMark" + i, "○");
                    SetLabel(root, "ObjectiveItemText" + i, longestItems[i % longestItems.Length] + "：已丢失，下次进入由恢复柜补发");
                }
                root.Q<Label>("ObjectiveNote")?.RemoveFromClassList("obj-item-hidden");
                SetLabel(root, "ObjectiveNote", "可选：废料箱已开 3/3，地面还有 12 堆没装车");
                SetLabel(root, "ObjectiveHint", "按 LeftBracket 查看任务日志与战役地图");
            }

            void FillLog(VisualElement root)
            {
                SetLabel(root, "MissionLogStatus", "已完成 10/10｜战役时间 99:59");
                for (int i = 0; i < CampaignObjectiveCatalog.All.Length; i++)
                {
                    SetLabel(root, "ObjRowState" + i, "已完成 99:59");
                    SetLabel(root, "ObjRowTitle" + i, $"{i + 1}. {longestTitle}");
                    SetLabel(root, "ObjRowRegion" + i, "铸造前哨外围");
                }
                for (int i = 0; i < 3; i++)
                {
                    SetLabel(root, "RegionRowName" + i, "铸造前哨外围 ◆");
                    SetLabel(root, "RegionRowState" + i, "远征中");
                    SetLabel(root, "RegionRowDetail" + i, "你现在在这里（◆）｜已出征 12 次｜已带回：静默标记模块、协议数据盒、铸造重炮模块、核心数据｜主核心：第二阶段");
                }
                SetLabel(root, "CoreGateLabel", "核心分区封锁门：重炮解析 √｜熔穿过载蓝图 √｜现役机实装 ○——三灯全亮才能进入核心分区");
            }

            ProbeAtScales(HudUxml, "ObjectiveHudRoot", FillHud, "目标条");
            ProbeAtScales(LogUxml, "MissionLogRoot", FillLog, "任务日志与战役地图");
        }

        private static void ProbeAtScales(string uxml, string root, Action<VisualElement> fill, string label)
        {
            var failed = new List<string>();
            string firstProblem = null;
            foreach (float scale in UiScales)
            {
                string result = UiToolkitLayoutProbe.Probe(uxml, root, true, UiToolkitLayoutProbe.DefaultPanelSettingsPath, fill, scale);
                if (!result.StartsWith("PASS", StringComparison.Ordinal))
                {
                    failed.Add(scale.ToString("0.0"));
                    firstProblem ??= result.Length > 600 ? result.Substring(0, 600) : result;
                }
            }
            Expect(failed.Count == 0, $"{label}塞满最长真实文字，在 UI 缩放 0.8/1.0/1.4 × 四种分辨率下无越界/截断" +
                                      (failed.Count == 0 ? string.Empty : $"（失败缩放 {string.Join(",", failed)}）\n{firstProblem}"));
        }

        private static void SetLabel(VisualElement root, string name, string text)
        {
            Label label = root.Q<Label>(name);
            if (label != null)
            {
                label.text = text;
            }
        }

        // ── 构造 ────────────────────────────────────────────────────

        private static CampaignState NewHomeState(string id, int seed)
        {
            CampaignState state = CampaignState.CreateNew(id, "Standard", seed);
            state.CurrentRegionId = HomeValleyLayout.RegionId;
            state.PlaySeconds = 125f;
            state.BuildingRecords = new[]
            {
                Building(HomeValleyLayout.BuildingTypeGenerator, GeneratorPos),
                Building(HomeValleyLayout.BuildingTypeWarehouse, new Vector2(-6f, 4f)),
                Building(HomeValleyLayout.BuildingTypeAssemblyStation, AssemblyPos),
                Building(HomeValleyLayout.BuildingTypeSignalTower, SignalTowerPos),
                Building(HomeValleyLayout.BuildingTypeAnalysisBench, AnalysisPos),
            };
            state.CombatTargets = new[]
            {
                new CombatTargetRecord { TargetId = "selfcheck:target", RegionId = HomeValleyLayout.RegionId, Position = TargetPos, Health = 40f, MaxHealth = 40f },
            };
            return state;
        }

        private static readonly Vector2 GeneratorPos = new Vector2(8f, 3f);
        private static readonly Vector2 AssemblyPos = new Vector2(-3f, -9f);
        private static readonly Vector2 SignalTowerPos = new Vector2(15f, 12f);
        private static readonly Vector2 AnalysisPos = new Vector2(-10f, -20f);
        private static readonly Vector2 TargetPos = new Vector2(20f, -4f);

        private static BuildingRecord Building(string typeId, Vector2 position) => new BuildingRecord
        {
            BuildingId = "selfcheck:" + typeId,
            BuildingTypeId = typeId,
            RegionId = HomeValleyLayout.RegionId,
            Position = position,
            ConstructionState = BuildingConstructionState.Damaged,
            PowerState = BuildingPowerState.Unpowered,
        };

        private static bool MarkerAt(CampaignState state, Vector2 expected, out string actual)
        {
            bool shown = CampaignObjectiveCatalog.TryGetHomeMarker(state, out Vector2 where);
            actual = shown ? where.ToString() : "不显示";
            return shown && (where - expected).sqrMagnitude < 0.0001f;
        }

        private static void SetBuilding(CampaignState state, string typeId, BuildingConstructionState construction, BuildingPowerState power)
        {
            BuildingRecord record = state.BuildingRecords.First(b => b.BuildingTypeId == typeId);
            record.ConstructionState = construction;
            record.PowerState = power;
        }

        private static RegionRecord EnsureRegion(CampaignState state, string regionId, RegionState regionState)
        {
            RegionRecord record = state.RegionRecords?.FirstOrDefault(r => r.RegionId == regionId);
            if (record == null)
            {
                record = new RegionRecord { RegionId = regionId };
                state.RegionRecords = (state.RegionRecords ?? Array.Empty<RegionRecord>()).Append(record).ToArray();
            }
            record.State = regionState;
            return record;
        }

        private static void AddQuest(CampaignState state, string regionId, string contentId, RegionQuestItemState questState)
        {
            var record = new RegionQuestItemRecord
            {
                SalvageInstanceId = $"selfcheck:{contentId}",
                RegionId = regionId,
                ContentId = contentId,
                State = questState,
            };
            state.RegionQuestItems = (state.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
                .Where(q => q.ContentId != contentId).Append(record).ToArray();
        }

        private static ObjectiveState State(CampaignState state, string id) => CampaignObjectiveTracker.StateOf(state, id);

        private static int ActiveCount(CampaignState state) =>
            CampaignObjectiveCatalog.All.Count(d => CampaignObjectiveTracker.StateOf(state, d.Id) == ObjectiveState.Active);

        private static bool HasEvent(CampaignState state, string eventId) =>
            state.EventLedger != null && state.EventLedger.Any(e => e.EventId == eventId);

        private static bool IsLeaky(string text) =>
            !string.IsNullOrEmpty(text) && (ForbiddenWords.IsMatch(text) || InternalIdPattern.IsMatch(text));

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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
