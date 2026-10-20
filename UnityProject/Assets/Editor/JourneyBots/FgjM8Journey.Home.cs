using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.Objective;
using UnityEngine;
using UnityEngine.UIElements;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>FGJ-M8 第三段：投送站面板“在地图上选回收点”→ 地图上点远征队 → “呼叫回收”→ 满载的机器与伤员随舱回家、按“随投送舱返回”结算、货舱按远征卸货规则卸货 → 家园分拣第二个数据包 → 支线已完成。</summary>
    public static partial class FgjM8Journey
    {
        private const string DropHost = "[DropStationHost]";
        private const string MapHost = "[StrategicMapHost]";

        private static IEnumerable<JourneyStep> HomeSteps() => new[]
        {
            S("drop_fix", "事件夹具（DEBT-FG8E2E01-04）：投送站里按远征队存活台数装投送舱（每舱接 1 台，T1 站最多 4 个）、加 600 升燃油（投送舱由组件工坊生产、燃油走管线进站，FgDropPodSelfCheck 覆盖）", 10, ApplyDropFixture, TickDropFixture),
            S("build_open3", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("drop_pause", "按暂停键（战略暂停里选回收点，世界不往前走）", 10, c => FgjM1Journey.PressIf(GameActionId.TogglePause, !GameClock.Paused),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Done("战略暂停") : StepOutcome.Retry("没有暂停"), retries: 1),
            S("drop_open", "建造模式里左键点投送站，通用面板上点“投送站…”：投送站面板打开", 40, null,
                c => M.TickSubPanel(c, "station", "PrDrop", () => DropStationPanelUIToolkit.IsOpen && DropStationPanelUIToolkit.Instance != null && DropStationPanelUIToolkit.Instance.PanelVisible, "投送站面板打开"), retries: 2),
            S("drop_pick", "点“在地图上选回收点…”：战略地图进入选点模式；在地图上左键点远征队所在处（悬停先看回收预览）→ 回到投送站面板，回收预览写“接回机器 N 台”", 20,
                c => FgjM1Journey.ClickUi(c, DropHost, "DropPickRecall"), TickRecallPickAndPoint, retries: 2),
            S("drop_go", "点“呼叫回收”：投送舱起飞去接", 15, c => FgjM1Journey.ClickUi(c, DropHost, "DropRecallGo"), TickRecallCalled, retries: 1),
            S("drop_close", "按 Esc 关闭投送站面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !DropStationPanelUIToolkit.IsOpen, "投送站面板"), retries: 1),
            S("drop_resume", "按暂停键继续（投送舱起飞去接）", 10, c => FgjM1Journey.PressIf(GameActionId.TogglePause, GameClock.Paused),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done("继续") : StepOutcome.Retry("还在暂停"), retries: 1),
            S("build_close3", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("recalled", "投送舱接上满载的机器和伤员、飞回投送站旁卸下：远征按“随投送舱返回”结算，货舱按远征卸货规则卸进仓库，数据包留给家园分拣", 360, null, TickRecalled),
            S("home_pick", "点顶栏“待分拣”打开数据包面板（在家园直接分拣），点第一张“选这个”：选中的直接入库，没选的转成技术数据", 25, null, TickHomePick),
            S("home_close", "Esc 关闭待分拣面板：顶栏计数消失", 10, c => JourneyInput.PressAction(GameActionId.Cancel), TickHomeClosed, retries: 1),
            S("quest_done", "按任务日志键打开任务日志：支线在“已完成”栏，奖励已到账；再按一次关闭", 15, c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, true), TickQuestDone, retries: 1),
        };

        // ── 投送站：夹具与面板 ──────────────────────────────────────────────────────

        private static BuildingRecord Station(JourneyContext c) => P.BuildingAtPivot(St, DropPodService.StationTypeId, P.P(c, "station"));

        /// <summary>事件夹具：站里 2 个舱 + 600 升燃油（与冒烟 / FgDropPodSelfCheck 同一写法：舱从仓库按正式装舱入口 LoadPods 装进站）。</summary>
        private static void ApplyDropFixture(JourneyContext c)
        {
            CampaignState s = St;
            BuildingRecord st = Station(c);
            if (st == null)
            {
                return;
            }
            DropPodService.Sync(s);
            DropStationRecord rec = DropPodService.FindStation(s, st.BuildingId);
            if (rec == null)
            {
                return;
            }
            rec.HeldFuelMl = Math.Max(rec.HeldFuelMl, 600_000);
            int want = Mathf.Clamp(Team(c).Count(Alive), 2, 4);
            c.SetInt("pods.want", want);
            int have = DropPodService.PodCount(s, st.BuildingId);
            if (have < want)
            {
                HomeInventory.Add(s, ItemCatalog.Find(DropPodService.PodItemId), want - have, clampToSpace: false);
                DropPodService.LoadPods(s, rec, st, want);
            }
        }

        private static StepOutcome TickDropFixture(JourneyContext c)
        {
            BuildingRecord st = Station(c);
            DropStationRecord rec = st != null ? DropPodService.FindStation(St, st.BuildingId) : null;
            return rec != null && DropPodService.PodCount(St, st.BuildingId) >= c.GetInt("pods.want", 2) && rec.HeldFuelMl >= 600_000
                ? StepOutcome.Done($"事件夹具：投送站 {st.BuildingId} 有 {DropPodService.PodCount(St, st.BuildingId)} 个舱、燃油 {DropPodService.Liters(rec.HeldFuelMl)} 升")
                : StepOutcome.Fail("投送站夹具没有生效");
        }

        /// <summary>点“在地图上选回收点…”之后：地图进入选点模式（提示写“选择回收点”）就在地图上点远征队所在处（同一步里点，不等下一步）。</summary>
        private static StepOutcome TickRecallPickAndPoint(JourneyContext c)
        {
            if (!FgjM3Common.Done(c, "clicked"))
            {
                string hint = JourneyInput.FindUitk<Label>(MapHost, "MapHint")?.text ?? string.Empty;
                bool picking = StrategicMapUIToolkit.IsOpen && StrategicMapUIToolkit.PickActive && !DropStationPanelUIToolkit.IsOpen && hint.Contains("回收");
                if (!picking)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry($"没进入选点模式（地图 {StrategicMapUIToolkit.IsOpen}、选点 {StrategicMapUIToolkit.PickActive}、提示“{hint}”；{FgjM1Journey.UiFail(c)}）");
                }
                if (c.StepElapsed < 0.3)
                {
                    return StepOutcome.Wait;
                }
                c.Set("recall.hint", hint);
            }
            return TickRecallPoint(c);
        }

        /// <summary>回收点 = 存活成员的中心（都在回收半径里时）；否则取身边成员最多的那台的位置。</summary>
        private static Vector2 RecallTarget(JourneyContext c, out int inRange, out int alive)
        {
            List<int> team = Team(c).Where(Alive).ToList();
            alive = team.Count;
            float r = DropPodService.RecallRadius - 1.5f;
            Vector2 center = team.Select(PosOf).Aggregate(Vector2.zero, (a, b) => a + b) / Math.Max(1, team.Count);
            int n = team.Count(id => Vector2.Distance(PosOf(id), center) <= r);
            if (n == team.Count)
            {
                inRange = n;
                return center;
            }
            Vector2 best = center;
            int bestN = n;
            foreach (int id in team)
            {
                Vector2 p = PosOf(id);
                int k = team.Count(o => Vector2.Distance(PosOf(o), p) <= r);
                if (k > bestN)
                {
                    bestN = k;
                    best = p;
                }
            }
            inRange = bestN;
            return best;
        }

        private static StepOutcome TickRecallPoint(JourneyContext c)
        {
            DropStationPanelUIToolkit panel = DropStationPanelUIToolkit.Instance;
            if (FgjM3Common.Done(c, "clicked"))
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "clickedAt")) < 800)
                {
                    return StepOutcome.Wait;
                }
                panel?.Refresh(force: true);
                bool back = !StrategicMapUIToolkit.IsOpen && DropStationPanelUIToolkit.IsOpen && panel != null && panel.RecallPoint.HasValue;
                string preview = panel?.RecallPreviewLabel ?? string.Empty;
                int alive = Team(c).Count(Alive);
                if (!back || !preview.Contains("接回机器"))
                {
                    return StepOutcome.Retry($"点地图后：回到面板 {back}，回收预览“{preview}”（悬停“{c.Get("recall.hover")}”）");
                }
                if (!preview.Contains($"接回机器 {alive} 台") || !panel.RecallButton.enabledSelf)
                {
                    return StepOutcome.Fail($"回收预览没有接回全部 {alive} 台存活成员 / “呼叫回收”不可点：“{preview}”");
                }
                return StepOutcome.Done($"战略地图进入选点模式（提示“{c.Get("recall.hint")}”）；地图上点远征队所在处（{c.Get("recall.at")}）：悬停预览“{c.Get("recall.hover")}”；回到投送站面板，回收预览“{preview.Replace('\n', ' ')}”");
            }
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            VisualElement canvas = JourneyInput.FindUitk<VisualElement>(MapHost, "MapCanvas");
            if (map == null || canvas == null || !StrategicMapUIToolkit.PickActive || !StrategicMapUIToolkit.IsOpen)
            {
                return StepOutcome.Fail($"战略地图不在选点模式（面板 {map != null}、画布 {canvas != null}、选点 {StrategicMapUIToolkit.PickActive}、打开 {StrategicMapUIToolkit.IsOpen}、投送站面板 {DropStationPanelUIToolkit.IsOpen}）");
            }
            Vector2 target = RecallTarget(c, out int inRange, out int aliveN);
            Vector2 local = map.View.ToCanvas(target.x, target.y);
            if (!JourneyInput.ClickElementAt(canvas, local))
            {
                return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点地图失败：" + JourneyInput.LastUiFailure);
            }
            c.Set("recall.hover", map.HoverText.Replace('\n', ' '));
            c.Set("recall.at", $"({target.x:0},{target.y:0})，回收半径内 {inRange}/{aliveN} 台，离核心 {F(Vector2.Distance(target, CorePos))} 格");
            c.Set("recall.hp", string.Join("、", Team(c).Where(Alive).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? $"{Label(id)} {F(m.Health, "0")}/{F(m.MaxHealth, "0")}" : "?")));
            c.Set("recall.held", string.Join("、", LootPickService.Held(St).Where(h => h != null && Team(c).Contains(h.Machine)).Select(h => $"{Label(h.Machine)}:{h.ItemId}×{h.Amount}")));
            c.SetInt("recall.stock0", Delivered(c));
            FgjM3Common.Mark(c, "clicked");
            c.SetLong(FgjM3Common.SK(c, "clickedAt"), NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRecallCalled(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string msg = DropStationPanelUIToolkit.Instance?.MessageText ?? string.Empty;
            return msg.Contains("飞去回收")
                ? StepOutcome.Done($"点“呼叫回收”：反馈“{msg}”")
                : StepOutcome.Retry($"点“呼叫回收”后没起飞：“{msg}”（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickRecalled(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            if (AwayReportPanelUIToolkit.IsOpen)
            {
                c.Set("recall.report", $"离家报告自动打开（“{AwayReportPanelUIToolkit.Instance?.SummaryText}”）后点关闭");
                FgjM1Journey.ClickUi(c, "[AwayReportHost]", "AwayReportClose");
                return StepOutcome.Wait;
            }
            BuildingRecord st = Station(c);
            List<int> alive = Team(c).Where(Alive).ToList();
            bool home = st != null && alive.Count > 0 && alive.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.RegionId == HomeValleyLayout.RegionId
                                                                          && Vector2.Distance(PosOf(id), st.Position) < 18f);
            ExpeditionMarchState mrc = ExpeditionMarchService.Current(s);
            bool settled = !ExpeditionMarchService.IsAway(s) && !AwayReportService.IsOpen(s);
            if (!home || !settled)
            {
                if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                    c.Log($"回收中：{string.Join("、", alive.Select(id => $"{Label(id)} {(MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m.RegionId : "?")} ({PosOf(id).x:0},{PosOf(id).y:0})"))}；行军阶段 {mrc?.Phase}；离家报告在记 {AwayReportService.IsOpen(s)}");
                }
                return StepOutcome.Wait;
            }
            string item = c.Get("held.item");
            bool unloaded = !LootPickService.Held(s).Any(h => h != null && alive.Contains(h.Machine) && h.Kind == LootPickCatalog.KindItem);
            int stock = Delivered(c);
            if (!unloaded || stock <= c.GetInt("recall.stock0"))
            {
                return c.StepElapsed < 30 ? StepOutcome.Wait
                    : StepOutcome.Fail($"回家结算后货舱没按远征卸货规则卸货：货舱还有 [{string.Join("、", LootPickService.Held(s).Where(h => h != null && alive.Contains(h.Machine)).Select(h => h.ItemId + "×" + h.Amount))}]；{ItemCatalog.NameOf(item)}（{c.Get("held.kind")}）入库计数 {c.GetInt("recall.stock0")} → {stock}");
            }
            int packets = LootPickService.Count(s);
            return StepOutcome.Done($"投送舱把 {alive.Count} 台（回收时血量 {c.Get("recall.hp")}；货舱 {c.Get("recall.held")}）接回投送站旁；远征按“随投送舱返回”结算（{c.Get("recall.report", "离家报告没有自动打开")}）；" +
                                    $"货舱按远征卸货规则卸货：{ItemCatalog.NameOf(item)}（{c.Get("held.kind")}，{DeliveredWhere(c)}）{c.GetInt("recall.stock0")} → {stock}；待分拣数据包 {packets} 个留给家园");
        }

        /// <summary>分拣得到的那件货卸下后的去处计数（按物品协议：普通物品 = 仓库库存；敌方加密固件 / 已破解固件 = 固件芯片；其余 = 解析台待解析）。</summary>
        private static int Delivered(JourneyContext c)
        {
            CampaignState s = St;
            string kind = c.Get("held.kind");
            if (kind == LootPickCatalog.KindFirmware || kind == LootPickCatalog.KindCracked)
            {
                string fw = c.Get("held.target");
                return s.PrimitiveChips?.Count(x => x != null && x.CardDefId == fw) ?? 0;
            }
            return HomeInventory.Stock(s, c.Get("held.item"));
        }

        private static string DeliveredWhere(JourneyContext c)
        {
            string kind = c.Get("held.kind");
            return kind == LootPickCatalog.KindFirmware || kind == LootPickCatalog.KindCracked ? $"固件芯片 {c.Get("held.target")}" : "仓库库存";
        }

        // ── 家园分拣 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickHomePick(JourneyContext c)
        {
            CampaignState s = St;
            if (!LootPickPanelUIToolkit.IsOpen)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "openAt")) > 1500)
                {
                    c.SetLong(FgjM3Common.SK(c, "openAt"), NowMs());
                    FgjM1Journey.ClickUi(c, WorldBarHost, "WorldPacketCount");
                }
                return c.StepElapsed > 10 ? StepOutcome.Retry("点顶栏计数后待分拣面板没开：" + FgjM1Journey.UiFail(c)) : StepOutcome.Wait;
            }
            LootPickPanelUIToolkit panel = LootPickPanelUIToolkit.Instance;
            string id = c.Get(FgjM3Common.SK(c, "packet"), string.Empty);
            if (id.Length == 0)
            {
                if (c.StepElapsed < 1.0 || panel == null || string.IsNullOrEmpty(panel.CurrentPacketId))
                {
                    return StepOutcome.Wait;
                }
                id = panel.CurrentPacketId;
                LootPackageRecord p = LootPickService.Find(s, id);
                if (p == null || !panel.ChooseEnabled(0) || !panel.WhereText.Contains("入库"))
                {
                    return StepOutcome.Fail($"家园里不能直接分拣 {id}：去向“{panel.WhereText}”、原因“{panel.ReasonText}”");
                }
                c.Set(FgjM3Common.SK(c, "packet"), id);
                c.Set("home.where", panel.WhereText);
                c.SetLong("home.tech0", (long)s.TechData);
                if (!JourneyInput.ClickUitk("[LootPickPanelHost]", "LootPickChoose0"))
                {
                    return StepOutcome.Retry("点“选这个”失败：" + JourneyInput.LastUiFailure);
                }
                return StepOutcome.Wait;
            }
            if (LootPickService.Find(s, id) != null)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Fail($"点“选这个”后数据包 {id} 还在（反馈“{panel?.MessageText}”）");
            }
            return StepOutcome.Done($"家园分拣数据包 {id}：去向“{c.Get("home.where")}”，反馈“{panel?.MessageText}”；技术数据 {c.GetLong("home.tech0")} → {(long)s.TechData}");
        }

        private static StepOutcome TickHomeClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            WorldBarHudUIToolkit hud = WorldBarHudUIToolkit.Instance;
            hud?.Refresh();
            string count = hud?.PacketCountText ?? string.Empty;
            return !LootPickPanelUIToolkit.IsOpen && LootPickService.Count(St) == 0 && count.Length == 0
                ? StepOutcome.Done("Esc 关闭待分拣面板；没有待分拣的数据包，顶栏计数隐藏")
                : StepOutcome.Retry($"面板 {LootPickPanelUIToolkit.IsOpen}、数据包 {LootPickService.Count(St)}、顶栏“{count}”");
        }

        private static StepOutcome TickQuestDone(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            MissionLogUIToolkit ml = MissionLogUIToolkit.Instance;
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(St, c.Get("quest.id"));
            if (!MissionLogUIToolkit.IsOpen || ml == null || r == null)
            {
                return StepOutcome.Retry("任务日志没打开 / 支线不见了");
            }
            bool inDone = Enumerable.Range(0, ml.RowCount(1)).Any(i => ml.RowView(1, i).Id == r.ObjectiveId);
            if (r.State != ObjectiveState.Completed || !inDone)
            {
                return StepOutcome.Fail($"支线状态 {r.State}、在“已完成”栏 {inDone}");
            }
            JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, false);
            return StepOutcome.Done($"任务日志“{ml.ColumnTitle(1)}”栏：“{CampaignObjectiveTracker.QuestTitle(r)}”（{CampaignObjectiveTracker.ProgressText(St, r)}）");
        }
    }
}
