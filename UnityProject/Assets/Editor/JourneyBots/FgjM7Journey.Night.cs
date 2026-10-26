using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.WorldSim;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using E = GameLogic.EditorTools.JourneyBots.FgjM7Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M7 后半段：家园遗迹（右键遗迹点派工 / 右键已回收的遗迹点 = 移动，DEBT-FG7ENV05-07）→ 研究储能站、放储能站、常驻规则“静默夜预案”→ 两处日程捷径 + 再看日历 →
    /// 酸雨中给关键产线盖顶棚（顶棚下不掉耐久、露天的掉）→ 静默夜（预案准时执行、规则表逐项、静默夜中存读档 FGT-ENV-008）→ 结束总结、风暴残骸搬回仓库 → 归还面积继续增长与可达性估算。
    /// </summary>
    public static partial class FgjM7Journey
    {
        private static IEnumerable<JourneyStep> NightSteps() => new List<JourneyStep>
        {
            // ── 4. 家园遗迹（FGR-ENV-051）：右键遗迹点 = 派选中的机器回收 ──
            S("relic_find", "找离核心最近、还没回收的遗迹点（按种子分布）：已被探索就直接派工，在迷雾里先派机器走过去", 10, null, TickRelicFind),
            S("relic_sel", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
            S("relic_walk", "（遗迹点在迷雾里时）右键遗迹点那边的地面：机器移动过去", 30, c => { if (Need(c, "relic.need")) RightClickGround(c, "relic.pos"); },
                c => Need(c, "relic.need") ? TickMoveOrdered(c, "workerA", "relic.pos") : StepOutcome.Done("遗迹点已发现，跳过"), retries: 3),
            S("relic_seen", "遗迹点被探索揭开：记为发现（地图上出现遗迹标记）", 400, null, TickRelicSeen),
            S("relic_sel2", "左键点同一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
            S("relic_rc", "右键点遗迹点（情境命令：回收）：回收单派给这台机器", 30, c => RightClickGround(c, "relic.pos"), TickRelicOrdered, retries: 3),
            S("relic_done", "机器走过去干完：遗迹点记为已回收，技术数据入账，通知带背景资料", 600, null, TickRelicRecovered),

            // ── 5. 为静默夜做准备：储能站 + 常驻规则“静默夜预案”──
            S("wait_storage", "等研究：能源 · 燃油发电机、能源 · 储能站", 1500, null, c => D.TickResearched(c, E.NodeFuelGen, E.NodeStorage)),
            S("storage_plan", "在核心配电范围里找储能站的位置", 10, null, TickStoragePlan),
            S("build_open3", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("m7_place3", "建造栏“能源”分类选储能站，指着格子单击放下", 120, null, c => P.TickActions(c, "m7s", 40), retries: 2),
            S("build_close3", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("relic_busy", "等一台家园机器手上有工单（储能站施工）", 120, null, TickBusyMachine),
            S("relic_sel3", "左键点这台手上有工单的机器", 20, c => FgjM1Journey.ClickMachine(c, "busy.id"), c => FgjM1Journey.TickSelected(c, "busy.id"), retries: 4),
            S("relic_rc2", "右键点已经回收过的遗迹点：当作移动（RTS 右键地面），手上的工单不取消", 30, c => RightClickGround(c, "relic.pos"), TickRecoveredRelicMove, retries: 3),
            S("storage_built", "储能站建成", 900, null, c => M.TickBuilt(c, new[] { "storage" }, Array.Empty<string>(), false, "储能站")),
            S("rules_open", "按常驻规则键（默认 Alt+R）打开常驻规则面板", 10, c => JourneyInput.PressAction(GameActionId.OpenRules), TickRulesOpen, retries: 1),
            S("rules_new", "“从预设新建”下拉选“静默夜前 30 秒回驻防点”：新建一条静默夜预案", 20, null, TickSilentRuleCreated, retries: 1),
            S("rules_target", "“储能站”一栏选刚建好的储能站：静默夜前把它切成只放电", 20, null, TickSilentRuleTarget, retries: 2),
            S("rules_enable", "点这一行的“启用”", 20, null, TickSilentRuleEnable, retries: 2),
            S("rules_close", "点“关闭”关闭常驻规则面板", 10, c => FgjM1Journey.ClickUi(c, P.RulesHost, "RulesPanelClose"), TickRulesClosed, retries: 1),

            // ── 6. 日程捷径（只改日期）+ 再看日历 ──
            S("fixture", "测试捷径（只改日程）：第一次静默夜挪到准备好之后的那个夜里、酸雨排到静默夜前后（两者的预警 / 开始 / 效果 / 结束全走正式推进）", 10, null, ApplyScheduleFixture),
            S("cal2_open", "按日历键再打开日历：今晚的静默夜与酸雨都在时间轴上、带倒计时", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, true), c => TickCalendar(c, true), retries: 1),
            S("cal2_close", "再按日历键关闭日历", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, false), TickCalendarClosed, retries: 1),

            // ── 7. 酸雨中把关键产线盖上顶棚（FGR-ENV-020 / 030）──
            S("rain_start", "酸雨按日程预警、开始（“天气预警”“天气”两条通知；世界时间条写当前天气）", 300, null, TickRainStarted),
            S("wait_roof", "等研究：环境 · 顶棚", 900, null, c => D.TickResearched(c, E.NodeRoof)),
            S("roof_plan", "挑要盖的关键产线（不在净化区里、正在淋雨的稀土提取钻；不行就挑别的露天建筑）与对照的露天建筑", 10, null, TickRoofPlan),
            S("build_open4", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("roof_drag", "建造栏“环境”分类选顶棚，按住左键从一角拖到另一角（4×4 一块，盖住整座建筑）", 60, null, TickRoofDrag, retries: 2),
            S("build_close4", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("roof_built", "机器取料铺好顶棚：那座建筑整座在顶棚下", 600, null, TickRoofBuilt),
            S("roof_hp", "酸雨继续两个游戏小时：顶棚下的建筑耐久不再掉，露天的照样掉", 300, null, TickRoofHp),

            // ── 8. 静默夜（FGR-ENV-010～015）──
            S("sn_warn", "静默夜按日程预警（“静默夜预警”通知；顶部提示信号会被弹回）", 300, null, TickSilentWarned),
            S("pre_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("pre_place", "趁静默夜还没开始再放三座储能站（这些施工单在静默夜开始前就有）", 120, null, c => P.TickActions(c, "m7p", 40), retries: 2),
            S("pre_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("sn_plan", "静默夜预案准时执行：开始前 30 秒储能站切成只放电（规则日志写明）", 300, null, TickSilentPlanApplied),
            S("sn_start", "静默夜开始（默认自动暂停，按暂停键继续）：顶部横幅“信号中断”带剩余时间（画面压暗 0.25 游戏小时淡入，由 FgSilentNightSelfCheck L1 断言）；信号在核心、接入口全空；突袭必定发生（导演收到静默夜触发）", 300, null, TickSilentStarted),
            S("speed1", "按 1 倍速键（静默夜只有 4 个游戏小时，逐项确认）", 10, c => JourneyInput.PressAction(GameActionId.SpeedNormal), c => TickSpeed(c, 1f), retries: 1),
            S("sn_sel", "左键点一台机器（查看照常可用）", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
            S("sn_uplink", "按接入键（默认 M）：接入被拒，写明静默夜", 15, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickUplinkRejected, retries: 1),
            S("sn_move", "右键地面：远程命令不可用，机器不动、写明静默夜", 20, c => SilentRightClick(c), TickMoveRejected, retries: 2),
            S("sn_bopen", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("sn_place", "静默夜里放一座 T2 电塔虚影（新的施工规划可以放）", 120, null, c => P.TickActions(c, "m7n", 40), retries: 2),
            S("sn_bclose", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("sn_held", "新放的施工单暂缓到静默夜结束（施工队列写明），开始前就有的施工单照常执行", 20, null, TickHeldAndContinued),
            S("sn_rules_open", "按常驻规则键打开常驻规则面板（查看照常可用）", 10, c => JourneyInput.PressAction(GameActionId.OpenRules), TickRulesOpen, retries: 1),
            S("sn_rule_toggle", "点静默夜预案那一行的“停用”：修改规则被拒，写明原因与剩余时间", 20, null, TickRuleEditRejected, retries: 2),
            S("sn_rules_close", "点“关闭”关闭常驻规则面板", 10, c => FgjM1Journey.ClickUi(c, P.RulesHost, "RulesPanelClose"), TickRulesClosed, retries: 1),
            S("sn_cal_open", "按日历键打开日历（统计 / 面板照常可用）：静默夜进行中、剩余时间", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, true), TickCalendarDuringNight, retries: 1),
            S("sn_cal_close", "再按日历键关闭日历", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, false), TickCalendarClosed, retries: 1),
            // FGT-ENV-008：静默夜 + 酸雨同时进行中存读档。
            S("sn_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
            S("sn_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
            S("sn_menu", "回到主菜单：存档里静默夜 / 天气 / 预案持有 / 暂缓施工单与存档那一刻逐项一致", 60, null, TickMenuAfterSave),
            S("sn_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
            S("sn_loaded", "读档进入家园：静默夜剩余时间、酸雨、突袭计划、暂缓施工单逐项一致，横幅照旧", 120, null, TickLoaded),
            S("speed3b", "按 3 倍速键", 10, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickSpeed(c, 3f), retries: 1),
            S("sn_end", "撑过去：静默夜按日程结束，结束总结通知（击退 / 损失 / 捡到）；风暴残骸出现在家园地面上；暂缓的施工单开工；预案恢复储能站原设置", 600, null, TickSilentEnded),
            S("debris_toast", "点“静默夜结束”通知弹出条：镜头飞到第一处风暴残骸", 30, null, TickDebrisToast, retries: 1),
            S("debris_home", "机器把风暴残骸搬回仓库（技术数据缓存 / 敌方残骸 / 未解析模块入库）", 900, null, TickDebrisHome),
            S("final", "净化塔整夜照常运转：归还面积继续增长；按实测净化速度估算第一个里程碑（5,000 格）的可达路径", 600, null, TickFinal),
        };

        // ── 遗迹 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickRelicFind(JourneyContext c)
        {
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            var list = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, core.X - 400, core.Y - 400, core.X + 400, core.Y + 400, list);
            WorldFeature relic = list.Where(f => f.Kind == WorldFeatureKind.Relic && !HomeExplorationService.IsRecovered(s, f.Id)).OrderBy(f => f.Distance).FirstOrDefault();
            if (relic == null)
            {
                return StepOutcome.Fail("家园 400 格内没有遗迹点");
            }
            c.Set("relic.id", relic.Id);
            P.SetP(c, "relic.pos", new GridCell(relic.X, relic.Y));
            bool found = HomeExplorationService.IsFound(s, relic.Id);
            c.SetInt("relic.need", found ? 0 : 1);
            c.SetLong("relic.tech0", TechDataFlow.IncomeOf(s, TechDataFlow.Relic));
            return StepOutcome.Done($"最近的遗迹点 {relic.Id}（{relic.X}, {relic.Y}，离核心 {relic.Distance} 格）{(found ? "已发现" : "还在迷雾里，先派机器走过去")}");
        }

        private static StepOutcome TickRelicSeen(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string id = c.Get("relic.id");
            if (!HomeExplorationService.IsFound(s, id))
            {
                return StepOutcome.Wait;
            }
            int n = NotificationCenter.History.Where(e => e?.Type?.Id == HomeExplorationService.NotifyRelicFound).Sum(e => e.Count);
            return StepOutcome.Done(Need(c, "relic.need")
                ? $"机器走近、迷雾揭开：遗迹点 {id} 记为发现（“发现遗迹点”通知 {n} 条）"
                : $"遗迹点 {id} 已发现");
        }

        private static StepOutcome TickRelicOrdered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (c.GetLong("rc.at") == 0)
            {
                if (!JourneyInput.Holding)
                {
                    RightClickGround(c, "relic.pos");
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong("rc.at") < 600)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            string id = c.Get("relic.id");
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, id);
            int me = c.GetInt("workerA");
            if (o == null || o.AssignedMachineLogicId != me || o.Kind != WorkOrderKind.Salvage)
            {
                return StepOutcome.Retry($"右键遗迹点后没有派给 {FgjM1Journey.Label(me)} 的回收单（工单 {o?.State}/{o?.AssignedMachineLogicId}；{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
            }
            HomeExplorationService.TryGetRelic(s, id, out HomeExplorationService.RelicInfo info);
            c.SetInt("relic.techRing", info.Ring?.TechData ?? 0);
            return StepOutcome.Done($"真实鼠标右键遗迹点 {id}：回收单派给 {FgjM1Journey.Label(me)}（工期 {o.Duration:F0} 游戏秒，{GameText.Get(info.Ring?.NameKey ?? string.Empty)}）");
        }

        private static StepOutcome TickRelicRecovered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string id = c.Get("relic.id");
            if (!HomeExplorationService.IsRecovered(s, id))
            {
                WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, id);
                if (o == null)
                {
                    return StepOutcome.Fail($"遗迹点 {id} 还没回收，回收单却没了");
                }
                return StepOutcome.Wait;
            }
            long gain = TechDataFlow.IncomeOf(s, TechDataFlow.Relic) - c.GetLong("relic.tech0");
            NotificationEntry n = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == HomeExplorationService.NotifyRelicRecovered);
            int want = c.GetInt("relic.techRing");
            return gain == want && want > 0 && n != null
                ? StepOutcome.Done($"遗迹点 {id} 记为已回收：经济账本“遗迹回收”技术数据收入 +{gain}（这一环应给 {want}；现有 {s.TechData}，实验室同时在消耗）；通知“{n.Text?.Replace("\n", " ")}”")
                : StepOutcome.Fail($"回收完成但入账不对：账本“遗迹回收”收入 +{gain}（这一环应给 {want}）、通知 {(n == null ? "没有" : "有")}");
        }

        private static StepOutcome TickBusyMachine(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            foreach (string k in new[] { "workerA", "workerB" })
            {
                int id = c.GetInt(k);
                WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(s, id);
                if (o != null && o.Kind == WorkOrderKind.Build)
                {
                    c.SetInt("busy.id", id);
                    c.Set("busy.order", o.WorkOrderId);
                    return StepOutcome.Done($"{FgjM1Journey.Label(id)} 手上有施工单 {o.WorkOrderId}（{o.State}）");
                }
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRecoveredRelicMove(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (c.GetLong("rc.at") == 0)
            {
                if (!JourneyInput.Holding)
                {
                    RightClickGround(c, "relic.pos");
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong("rc.at") < 600)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int id = c.GetInt("busy.id");
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(s, id);
            string want = c.Get("busy.order");
            WorkOrderRecord kept = s.WorkOrders?.FirstOrDefault(x => x != null && x.WorkOrderId == want);
            bool keptOk = kept != null && HomeValleyWorkOrders.IsActive(kept) || kept != null && kept.State == WorkOrderState.Completed;
            CombatSite site = GameRoot.HomeValley?.Combat;
            bool moving = site != null && site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
                          && (cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.Move || cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.WorkMove);
            bool noNewRecovery = HomeExplorationService.ActiveOrder(s, c.Get("relic.id")) == null;
            if (!moving && c.GetInt(FgjM3Common.SK(c, "seenMove")) == 0)
            {
                return StepOutcome.Retry($"右键已回收的遗迹点后 {FgjM1Journey.Label(id)} 没有接到移动命令（{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
            }
            return keptOk && noNewRecovery
                ? StepOutcome.Done($"右键已回收的遗迹点 = 移动：{FgjM1Journey.Label(id)} 接到移动命令；手上的施工单 {want} 没被取消（{kept.State}，现在手上 {o?.WorkOrderId ?? "无"}）；没有开新的回收单")
                : StepOutcome.Fail($"右键已回收的遗迹点后：原施工单 {want} {(kept == null ? "不见了" : kept.State.ToString())}、新回收单 {(!noNewRecovery ? "开了" : "没有")}");
        }

        // ── 储能站与规则 ──────────────────────────────────────────────────────────────

        private static List<GridCell> Reserved(JourneyContext c) =>
            P.LoadPath(c, "path.water").Concat(P.LoadPath(c, "path.coolant")).Concat(P.LoadPath(c, "path.r_recycler")).Concat(P.LoadPath(c, "path.r_rare"))
                .Concat(new[] { P.P(c, "m7.pump") }).Concat(FgjM3Common.DecodeCells(c.Get("m7.poles"))).ToList();

        private static P.FactoryPlan ReservedPlan(JourneyContext c)
        {
            var plan = new P.FactoryPlan { Origin = HomeGridService.CorePivot(St) };
            foreach (GridCell x in Reserved(c))
            {
                plan.Used.Add(P.Key(x));
            }
            foreach (string k in new[] { "storage", "storage2", "storage3", "storage4" })
            {
                if (c.Get(k + ".type", string.Empty).Length > 0)
                {
                    M.ReserveIn(plan, c.Get(k + ".type"), P.P(c, k), 0);
                }
            }
            return plan;
        }

        private static StepOutcome TickStoragePlan(JourneyContext c)
        {
            CampaignState s = St;
            P.FactoryPlan plan = ReservedPlan(c);
            if (!M.FindSite(s, plan, E.Storage, 4, 26, out GridCell g))
            {
                return StepOutcome.Fail("核心配电范围里放不下储能站");
            }
            P.Placed st = new P.Placed { Key = "storage", Type = E.Storage, Pivot = g, Rot = 0, Label = HomeGridService.DisplayName(E.Storage) };
            P.Remember(c, st);
            P.SetActions(c, "m7s", new[] { P.ActB(st) });
            return StepOutcome.Done($"储能站放在 {Cell(g)}（核心配电范围里）");
        }

        private static StepOutcome TickRulesOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return RulesPanelUIToolkit.IsOpen ? StepOutcome.Done("常驻规则面板打开") : StepOutcome.Retry("按常驻规则键后面板没有打开");
        }

        private static StepOutcome TickRulesClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !RulesPanelUIToolkit.IsOpen ? StepOutcome.Done("常驻规则面板关闭") : StepOutcome.Retry("常驻规则面板还开着：" + FgjM1Journey.UiFail(c));
        }

        private static StepOutcome TickSilentRuleCreated(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            if (panel == null || !RulesPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("常驻规则面板没开");
            }
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "pick"))
            {
                panel.Refresh();
                DropdownField d = panel.NewPresetField;
                string want = GameText.Get("rules.preset.silent_default");
                int idx = d?.choices.FindIndex(x => x != null && x.Contains("静默夜前")) ?? -1;
                bool? ok = idx < 0 ? false : P.PickableInView(d);
                if (ok == null)
                {
                    return StepOutcome.Wait;
                }
                if (ok == false)
                {
                    return StepOutcome.Retry($"“从预设新建”下拉里没有“{want}”或点不到（{string.Join("、", d?.choices ?? new List<string>())}；{JourneyInput.LastUiFailure}）");
                }
                d.value = d.choices[idx]; // 与在弹出菜单里点那一项同一个值变化回调
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            StandingRuleRecord r = StandingRuleService.Find(s, panel.SelectedSerial);
            if (r == null || r.Kind != StandingRuleService.KindSilent)
            {
                return StepOutcome.Retry($"新建后选中的不是静默夜预案：{r?.Kind}（“{panel.MessageText}”）");
            }
            c.SetInt("rule", r.Serial);
            return StepOutcome.Done($"新建静默夜预案 R{r.Serial}：提前 {r.Threshold} 秒执行（编辑区“{panel.EditTitleText}”；“{panel.MessageText}”）");
        }

        private static StepOutcome TickSilentRuleTarget(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            CampaignState s = St;
            StandingRuleRecord r = StandingRuleService.Find(s, c.GetInt("rule"));
            BuildingRecord st = P.Bld(c, "storage");
            if (panel == null || r == null || st == null)
            {
                return StepOutcome.Fail("规则 / 储能站不见了");
            }
            if (r.Targets.Contains(st.BuildingId))
            {
                return StepOutcome.Done($"储能站一栏：“{panel.TargetsText}”（R{r.Serial} 作用于 {StandingRuleService.BuildingLabel(s, st.BuildingId)}）");
            }
            if (FgjM3Common.SinceMs(c, "pickAt") >= 0 && FgjM3Common.SinceMs(c, "pickAt") < 700)
            {
                return StepOutcome.Wait;
            }
            DropdownField d = panel.TargetAddField;
            string want = StandingRuleService.BuildingLabel(s, st.BuildingId);
            string shown = want.Replace('/', '／').Replace('#', '＃').Replace('%', '％').Replace('&', '＆');
            int idx = d?.choices.FindIndex(x => x == want || x == shown) ?? -1;
            bool? ok = idx < 0 ? false : P.PickableInView(d);
            if (ok == null)
            {
                return StepOutcome.Wait;
            }
            if (ok == false)
            {
                return StepOutcome.Retry($"储能站下拉里没有“{want}”或点不到（{string.Join("、", d?.choices ?? new List<string>())}；{JourneyInput.LastUiFailure}）");
            }
            d.value = d.choices[idx];
            FgjM3Common.Once(c, "pickAt", () => true);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickSilentRuleEnable(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            CampaignState s = St;
            StandingRuleRecord r = StandingRuleService.Find(s, c.GetInt("rule"));
            if (panel == null || r == null)
            {
                return StepOutcome.Fail("规则不见了");
            }
            if (r.Enabled)
            {
                int row0 = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowSerial(i) == r.Serial);
                return StepOutcome.Done($"R{r.Serial} 已启用（行“{panel.RowText(row0, "RrState")}｜{panel.RowText(row0, "RrWhen")}｜{panel.RowText(row0, "RrThen")}”）");
            }
            if (c.StepElapsed < 0.6 || (FgjM3Common.SinceMs(c, "clk") >= 0 && FgjM3Common.SinceMs(c, "clk") < 700))
            {
                return StepOutcome.Wait;
            }
            string issue = StandingRuleService.ConfigIssue(s, r, out _, out _);
            if (issue != null)
            {
                return StepOutcome.Fail($"R{r.Serial} 设置不完整，启用不了：{issue}");
            }
            int row = Enumerable.Range(0, panel.VisibleRowCount).Where(i => panel.RowSerial(i) == r.Serial).DefaultIfEmpty(-1).First();
            bool? clicked = row < 0 ? false : P.ClickInView(panel.RowButton(row, "RrToggle"));
            if (clicked == null)
            {
                return StepOutcome.Wait;
            }
            if (clicked == false)
            {
                return StepOutcome.Retry($"点不到 R{r.Serial} 那一行的“启用”：{JourneyInput.LastUiFailure}");
            }
            FgjM3Common.Once(c, "clk", () => true);
            return StepOutcome.Wait;
        }

        // ── 日程捷径 ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 测试捷径（只改日程，登记在 ADR-QA-024 夹具表）：
        /// ① <see cref="SilentNightService.PlanDayForTests"/>：第一次静默夜改到“预警还在 6 个游戏小时以后”的最近一个夜里——时刻、预警提前量、持续时间与正式相同，只改日期
        ///    （正式标准第 7 日（间隔 + 首次推迟），3 倍速下也要等约 31 分钟真实时间，超过旅程 30 分钟上限；正式排定已在 cal_open 断言）；
        /// ② <see cref="WeatherService.ScheduleForTests"/>：酸雨 1 个游戏小时后开始，下到静默夜结束后 2 个游戏小时（持续时间在 0.5～1.5 日之内）——第一段恶劣天气最早第 3 日、种类按种子随机。
        /// 之后的预警、开始、效果、结束全部由世界模拟按正式流程推进。
        /// </summary>
        private static StepOutcome ApplyScheduleFixture(JourneyContext c)
        {
            CampaignState s = St;
            long now = GameClock.Ticks;
            double hour = GameClock.HourSeconds;
            int today = GameClock.DayOf(GameClock.GameSeconds);
            int day = -1;
            long warn = 0, start = 0, end = 0;
            for (int d = today; d <= today + 3; d++)
            {
                SilentNightService.PlanTicks(d, out warn, out start, out end);
                if (warn > now + GameClock.TicksFor(6 * hour))
                {
                    day = d;
                    break;
                }
            }
            if (day < 0 || !SilentNightService.PlanDayForTests(s, day))
            {
                return StepOutcome.Fail($"静默夜日程捷径没生效（第 {day} 日）");
            }
            double hz = Math.Max(1, GameClock.StepHz);
            double startIn = hour;
            double dur = (end - now) / hz - startIn + 2 * hour;
            dur = Math.Max(0.5 * GameClock.DaySeconds, Math.Min(1.5 * GameClock.DaySeconds, dur));
            c.SetInt("wx.warn0", WeatherService.WarnCount);
            bool rain = WeatherService.ScheduleForTests(s, WeatherService.AcidRain, startIn, dur);
            if (!rain)
            {
                return StepOutcome.Fail("酸雨日程捷径没生效（正在下别的恶劣天气？）");
            }
            c.SetInt("sn.day", day);
            c.SetLong("sn.warn", warn);
            c.SetLong("sn.start", start);
            c.SetLong("sn.end", end);
            c.SetInt("sn.warn0", SilentNightService.WarnCount);
            return StepOutcome.Done($"静默夜：第 {c.GetLong("sn.formalStart") / (long)hz / (long)GameClock.DaySeconds + 1} 日的正式排定改到第 {day} 日夜里（{GameClock.FormatHhMm(start / hz)}～{GameClock.FormatHhMm(end / hz)}，" +
                                   $"预警 {GameClock.FormatHhMm(warn / hz)}，与正式同一套时刻与时长）；酸雨 {startIn / hour:F0} 个游戏小时后开始、持续 {dur / GameClock.DaySeconds:F2} 日（覆盖静默夜）");
        }

        // ── 酸雨与顶棚 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickRainStarted(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!WeatherService.IsActive(s, WeatherService.AcidRain))
            {
                return StepOutcome.Wait;
            }
            WorldBarHudUIToolkit bar = WorldBarHudUIToolkit.Instance;
            bar?.Refresh();
            string row = bar?.CalendarExtraText ?? string.Empty;
            bool warned = WeatherService.WarnCount > c.GetInt("wx.warn0") && NotificationCenter.History.Any(e => e?.Type?.Id == "weather_warning")
                          && NotificationCenter.History.Any(e => e?.Type?.Id == "weather");
            return warned && row.Contains("酸雨")
                ? StepOutcome.Done($"酸雨按日程预警、开始（两条通知都在历史里）；世界时间条“{row}”")
                : StepOutcome.Fail($"酸雨开始了但预警 / 通知 / 时间条不对：预警 {WeatherService.WarnCount - c.GetInt("wx.warn0")} 次、时间条“{row}”");
        }

        private static readonly string[] RoofCandidates = { "drillR", "lab1", "lab2", "recycler", "storage" };

        private static bool RoofOk(CampaignState s, BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational && !RoofService.IsBuildingCovered(s, b)
            && !PurificationService.InPurifiedZone(s, new GridCell(b.GridX, b.GridY));

        private static StepOutcome TickRoofPlan(JourneyContext c)
        {
            CampaignState s = St;
            string target = RoofCandidates.FirstOrDefault(k => RoofOk(s, P.Bld(c, k)) && RoofBoxFor(s, P.Bld(c, k), out _, out _, out RoofPlan p) && p.Ok && p.New > 0);
            if (target == null)
            {
                return StepOutcome.Fail("找不到能盖顶棚的露天建筑（都在净化区里或顶棚放不下）");
            }
            BuildingRecord b = P.Bld(c, target);
            RoofBoxFor(s, b, out GridCell a, out GridCell z, out RoofPlan plan);
            P.SetP(c, "roof.a", a);
            P.SetP(c, "roof.b", z);
            c.Set("roof.target", target);
            List<string> refs = RoofCandidates.Concat(new[] { "blender" }).Where(k => k != target && RoofOk(s, P.Bld(c, k))).ToList();
            if (refs.Count == 0)
            {
                return StepOutcome.Fail("没有能做对照的露天建筑（其余都在净化区里）");
            }
            c.Set("roof.refs", string.Join(",", refs));
            return StepOutcome.Done($"要盖：{HomeGridService.DisplayName(b.BuildingTypeId)} {Cell(new GridCell(b.GridX, b.GridY))}（耐久 {b.Health:F1}，正在淋雨）——顶棚 {plan.New} 块（{Cell(a)}～{Cell(z)}，每块 {plan.ScrapPerBlock} 废料）；" +
                                   $"对照（露天、不在净化区）：{string.Join("、", refs.Select(k => HomeGridService.DisplayName(c.Get(k + ".type"))))}");
        }

        private static bool RoofBoxFor(CampaignState s, BuildingRecord b, out GridCell a, out GridCell z, out RoofPlan plan)
        {
            a = z = default;
            plan = null;
            if (b == null)
            {
                return false;
            }
            var fp = new List<GridCell>(16);
            HomeGridService.FootprintOf(b, fp);
            int n = RoofService.BlockCells;
            int bx0 = RoofService.BlockOf(fp.Min(x => x.X)), bx1 = RoofService.BlockOf(fp.Max(x => x.X));
            int by0 = RoofService.BlockOf(fp.Min(x => x.Y)), by1 = RoofService.BlockOf(fp.Max(x => x.Y));
            a = new GridCell(bx0 * n, by0 * n);
            z = new GridCell(bx1 * n + n - 1, by1 * n + n - 1);
            plan = RoofService.PlanBox(s, a, z);
            return true;
        }

        private static StepOutcome TickRoofDrag(JourneyContext c)
        {
            CampaignState s = St;
            GridCell a = P.P(c, "roof.a");
            GridCell z = P.P(c, "roof.b");
            int n = RoofService.BlockCells;
            bool AllPlanned()
            {
                for (int bx = RoofService.BlockOf(a.X); bx <= RoofService.BlockOf(z.X); bx++)
                {
                    for (int by = RoofService.BlockOf(a.Y); by <= RoofService.BlockOf(z.Y); by++)
                    {
                        if (!RoofService.IsPlannedBlock(s, bx, by) && !RoofService.HasBlock(s, bx, by))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (info.Contains("顶棚") && P.Mode != null && P.Mode.Drag == HomeValleyBuildMode.DragKind.RoofBox)
            {
                c.Set("roof.dragInfo", info.Replace("\n", " "));
            }
            if (AllPlanned())
            {
                return StepOutcome.Done($"真实鼠标拖出顶棚虚影（{RoofService.PlannedCount(s)} 块待施工）：拖的时候 HUD 写“{c.Get("roof.dragInfo", string.Empty)}”；状态行“{P.Mode?.StatusText?.Replace("\n", " / ")}”");
            }
            if (!FgjM3Common.EntrySelected(RoofService.ToolId))
            {
                if (FgjM3Common.SinceMs(c, "pick") >= 0 && FgjM3Common.SinceMs(c, "pick") < 400)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt(FgjM3Common.SK(c, "pick"), 0);
                FgjM3Common.Once(c, "pick", () => true);
                if (!FgjM3Common.PickEntry(RoofService.ToolId, out string why, out bool scrolling) && !scrolling)
                {
                    return StepOutcome.Retry("建造栏选不中顶棚：" + why);
                }
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "drag"))
            {
                if (JourneyInput.Holding)
                {
                    return StepOutcome.Wait;
                }
                if (FgjM3Common.TryDrag(FgjM3Common.Ground(a), FgjM3Common.Ground(z)))
                {
                    FgjM3Common.Mark(c, "drag");
                    c.SetLong(FgjM3Common.SK(c, "dragAt"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "dragAt")) < 2500)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Retry($"松开后顶棚没成虚影（{Cell(a)}～{Cell(z)}；状态行“{P.Mode?.StatusText}”）");
        }

        private static StepOutcome TickRoofBuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord b = P.Bld(c, c.Get("roof.target"));
            if (b == null || !RoofService.IsBuildingCovered(s, b))
            {
                int tick = (int)(c.StepElapsed / 30);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"顶棚施工：已建 {RoofService.BuiltCount(s)} 块、待施工 {RoofService.PlannedCount(s)} 块；废料 {s.Scrap}");
                }
                if (!WeatherService.IsActive(s, WeatherService.AcidRain))
                {
                    return StepOutcome.Fail("顶棚还没铺好酸雨就停了（日程捷径的持续时间不够）");
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"顶棚铺好（已建 {RoofService.BuiltCount(s)} 块）：{HomeGridService.DisplayName(b.BuildingTypeId)} 整座在顶棚下（耐久 {b.Health:F1}）");
        }

        private static StepOutcome TickRoofHp(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string target = c.Get("roof.target");
            string[] refs = c.Get("roof.refs").Split(',');
            if (!FgjM3Common.Done(c, "t0"))
            {
                FgjM3Common.Mark(c, "t0");
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.Set(FgjM3Common.SK(c, "hp." + target), E.Hp(P.Bld(c, target)).ToString("R", CultureInfo.InvariantCulture));
                foreach (string k in refs)
                {
                    c.Set(FgjM3Common.SK(c, "hp." + k), E.Hp(P.Bld(c, k)).ToString("R", CultureInfo.InvariantCulture));
                }
                return StepOutcome.Wait;
            }
            if (!WeatherService.IsActive(s, WeatherService.AcidRain))
            {
                return StepOutcome.Fail("测量期间酸雨停了");
            }
            if (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0")) < GameClock.TicksFor(2 * GameClock.HourSeconds))
            {
                return StepOutcome.Wait;
            }
            float Hp0(string k) => float.Parse(c.Get(FgjM3Common.SK(c, "hp." + k)), CultureInfo.InvariantCulture);
            float t0 = Hp0(target);
            float t1 = E.Hp(P.Bld(c, target));
            var dropped = new List<string>();
            var lines = new List<string>();
            foreach (string k in refs)
            {
                BuildingRecord rb = P.Bld(c, k);
                float h0 = Hp0(k);
                float h1 = E.Hp(rb);
                lines.Add($"{HomeGridService.DisplayName(c.Get(k + ".type"))} {h0:F1}→{h1:F1}");
                if (rb != null && h1 < h0 - 0.05f && !RoofService.IsBuildingCovered(s, rb))
                {
                    dropped.Add(k);
                }
            }
            return t1 >= t0 - 0.001f && dropped.Count > 0
                ? StepOutcome.Done($"酸雨两个游戏小时：顶棚下的{HomeGridService.DisplayName(c.Get(target + ".type"))}耐久 {t0:F1}→{t1:F1}（不掉）；露天对照 {string.Join("、", lines)}（掉了 {dropped.Count} 座）")
                : StepOutcome.Fail($"顶棚没挡住酸雨或对照没淋到：顶棚下 {t0:F1}→{t1:F1}；对照 {string.Join("、", lines)}");
        }

        // ── 静默夜 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickSilentWarned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (SilentNightService.WarnCount <= c.GetInt("sn.warn0") || !NotificationCenter.History.Any(e => e?.Type?.Id == "silent_night_warning"))
            {
                return StepOutcome.Wait;
            }
            NotificationEntry n = NotificationCenter.History.Last(e => e?.Type?.Id == "silent_night_warning");
            double secs = (c.GetLong("sn.start") - GameClock.Ticks) / (double)GameClock.StepHz;
            // 开始前再放一座储能站（开始前就有的施工单：静默夜里照常执行）。
            // 三座：两台机器一座座取料施工要几十游戏秒，静默夜开始那一刻至少还有一张没干完（第一次续跑实测一座 8 秒就建完）。
            var acts = new List<string>();
            var at = new List<string>();
            foreach (string k in new[] { "storage2", "storage3", "storage4" })
            {
                P.FactoryPlan plan = ReservedPlan(c);
                if (!M.FindSite(St, plan, E.Storage, 4, 26, out GridCell g))
                {
                    return StepOutcome.Fail("核心配电范围里放不下第 " + (acts.Count + 2) + " 座储能站");
                }
                P.Placed stn = new P.Placed { Key = k, Type = E.Storage, Pivot = g, Rot = 0, Label = HomeGridService.DisplayName(E.Storage) + " " + Cell(g) };
                P.Remember(c, stn);
                acts.Add(P.ActB(stn));
                at.Add(Cell(g));
            }
            P.SetActions(c, "m7p", acts);
            return StepOutcome.Done($"静默夜预警：“{n.Text?.Replace("\n", " ")}”（还有 {secs:F0} 游戏秒开始）；趁开始前再放三座储能站 {string.Join("、", at)}");
        }

        private static StepOutcome TickSilentPlanApplied(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int serial = c.GetInt("rule");
            RuleLogRecord on = StandingRuleService.LogEntries(s).LastOrDefault(l => l.Rule == serial && l.Key == "rules.log.silent_on");
            BuildingRecord st = P.Bld(c, "storage");
            RuleHoldRecord h = st != null ? StandingRuleService.FindHold(s, StandingRuleService.BuildingKey(st.BuildingId)) : null;
            if (on == null || h == null || h.Rule != serial)
            {
                if (SilentNightService.IsActive(s))
                {
                    return StepOutcome.Fail($"静默夜已经开始，预案还没执行（日志 {(on == null ? "没有" : "有")}、储能站持有 {(h == null ? "没有" : "R" + h.Rule)}）");
                }
                return StepOutcome.Wait;
            }
            double lead = (c.GetLong("sn.start") - on.Tick) / (double)GameClock.StepHz;
            c.Set("hold.prev", h.Prev ?? string.Empty);
            return Math.Abs(lead - StandingRuleService.Find(s, serial).Threshold) <= 2.0
                ? StepOutcome.Done($"预案准时执行：开始前 {lead:F1} 游戏秒，日志“{StandingRuleService.LogText(s, on)}”；储能站由 R{serial} 持有（原设置“{h.Prev}”）")
                : StepOutcome.Fail($"预案执行时刻不对：开始前 {lead:F1} 游戏秒（应 {StandingRuleService.Find(s, serial).Threshold}）");
        }

        private static StepOutcome TickSilentStarted(JourneyContext c)
        {
            CampaignState s = St;
            SilentNightState sn = SilentNightService.StateOf(s);
            if (!sn.Active)
            {
                JourneyCommon.ResumeIfAutoPaused(c);
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "paused"))
            {
                // “静默夜开始”默认自动暂停（FGR-UX-021）：先看到暂停，再像玩家一样按暂停键继续。
                FgjM3Common.Mark(c, "paused");
                c.SetInt(FgjM3Common.SK(c, "wasPaused"), GameClock.Paused ? 1 : 0);
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            if (GameClock.Paused)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "snap"))
            {
                FgjM3Common.Mark(c, "snap");
                // 开始那一刻手上的施工单（第二座储能站等）与地面物（之后对比风暴残骸）。
                var snapOrders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.Kind == WorkOrderKind.Build && HomeValleyWorkOrders.IsActive(o)).Select(o => o.WorkOrderId).ToList();
                c.Set("sn.orders", string.Join(",", snapOrders));
                c.Set("sn.ground", string.Join(",", (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g != null).Select(g => g.GroundItemId)));
                c.SetLong("sn.ret", PurificationService.ReturnedArea(s));
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            WorldBarHudUIToolkit bar = WorldBarHudUIToolkit.Instance;
            string banner = bar?.SilentBannerText ?? string.Empty;
            bool atCore = SignalPresence.AtCore && SignalUplinkService.CurrentMachine(s) == 0;
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            bool raid = (d?.Pending ?? Array.Empty<RaidTriggerRecord>()).Any(t => t != null && t.Kind == RaidCatalog.TriggerSilentNight)
                        || RaidDirectorService.Plans(s).Any(p => p != null && Array.IndexOf(p.Triggers ?? Array.Empty<string>(), RaidCatalog.TriggerSilentNight) >= 0);
            float dim = SilentNightService.DimFactor(s, GameClock.GameSeconds);
            bool notified = NotificationCenter.History.Any(e => e?.Type?.Id == "silent_night");
            string orders = c.Get("sn.orders");
            bool ok = banner.Contains("信号中断") && banner.Contains("剩余") && atCore && raid && notified && orders.Length > 0;
            if (ok)
            {
                // 静默夜里要放的 T2 电塔虚影（新的施工规划）：核心配电范围里找一格。
                if (!M.FindSite(s, ReservedPlan(c), P.PoleT2, 3, 20, out GridCell pole))
                {
                    return StepOutcome.Fail("核心配电范围里找不到放 T2 电塔的格子");
                }
                c.Set("sn.pole", FgjM3Common.EncodeCells(new[] { pole }));
                P.SetActions(c, "m7n", new[] { P.ActB(new P.Placed { Type = P.PoleT2, Pivot = pole, Rot = 0, Label = "T2 电塔 " + Cell(pole) }) });
            }
            return ok
                ? StepOutcome.Done($"静默夜开始（第 {sn.Count} 次；开始时自动暂停 {c.GetInt(FgjM3Common.SK(c, "wasPaused")) == 1}，按暂停键继续）：横幅“{banner}”、压暗系数 {dim:F2}；信号在核心、没有机器被接入（接入口全空）；" +
                                   $"突袭导演收到静默夜触发；开始前就有的施工单 [{orders}]")
                : StepOutcome.Fail($"静默夜开始后不对：横幅“{banner}”、信号在核心 {atCore}、突袭触发 {raid}、通知 {notified}、开始前的施工单 [{orders}]");
        }

        private static StepOutcome TickSpeed(JourneyContext c, float want)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return Mathf.Approximately(GameClock.Speed, want) ? StepOutcome.Done($"{want:0.#} 倍速") : StepOutcome.Retry($"倍速 {GameClock.Speed}x");
        }

        private static StepOutcome TickUplinkRejected(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!SilentNightService.IsActive(St))
            {
                return StepOutcome.Fail("静默夜已经结束（逐项确认太慢）");
            }
            return SignalPresence.AtCore && SignalUplinkService.LastFailure == UplinkFailure.SilentNight
                ? StepOutcome.Done($"静默夜中接入被拒：“{SignalUplinkService.LastFeedbackText}”")
                : StepOutcome.Retry($"按接入键后：信号在核心 {SignalPresence.AtCore}、最近一次失败 {SignalUplinkService.LastFailure}");
        }

        private static void SilentRightClick(JourneyContext c)
        {
            int id = c.GetInt("workerB");
            FgjM2Common.HomePos(id, out Vector2 at);
            P.SetP(c, "sn.mv", new GridCell(Mathf.RoundToInt(at.x) + 6, Mathf.RoundToInt(at.y)));
            c.SetInt("sn.rej0", RegionSquadCommandSystem.OutOfCoverageRejects);
            RightClickGround(c, "sn.mv");
        }

        private static StepOutcome TickMoveRejected(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (c.GetLong("rc.at") == 0)
            {
                if (!JourneyInput.Holding)
                {
                    RightClickGround(c, "sn.mv");
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong("rc.at") < 600)
            {
                return StepOutcome.Wait;
            }
            int id = c.GetInt("workerB");
            string ev = GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault() ?? string.Empty;
            int rej = RegionSquadCommandSystem.OutOfCoverageRejects - c.GetInt("sn.rej0");
            CombatSite site = GameRoot.HomeValley?.Combat;
            GridCell mv = P.P(c, "sn.mv");
            bool movingThere = site != null && site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
                               && cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.Move && Vector2.Distance(new Vector2((float)cmd.Pos.x, (float)cmd.Pos.y), new Vector2(mv.X, mv.Y)) < 2f;
            return rej > 0 && ev.Contains("静默夜") && !movingThere
                ? StepOutcome.Done($"右键地面：远程命令被拒（{rej} 次），机器没有接到移动命令；提示“{ev}”")
                : StepOutcome.Retry($"右键地面后：拒绝 {rej} 次、提示“{ev}”、机器朝那里移动 {movingThere}");
        }

        private static StepOutcome TickHeldAndContinued(JourneyContext c)
        {
            CampaignState s = St;
            if (!SilentNightService.IsActive(s))
            {
                return StepOutcome.Fail("静默夜已经结束（逐项确认太慢）");
            }
            List<GridCell> poleCells = FgjM3Common.DecodeCells(c.Get("sn.pole"));
            BuildingRecord pole = poleCells.Count > 0 ? P.BuildingAtPivot(s, P.PoleT2, poleCells[0]) : null;
            WorkOrderRecord po = pole != null ? s.WorkOrders?.LastOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == pole.BuildingId && HomeValleyWorkOrders.IsActive(o)) : null;
            bool held = po != null && SilentNightService.IsConstructionHeld(s, po);
            var pre = c.Get("sn.orders").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var preStates = pre.Select(id => s.WorkOrders?.FirstOrDefault(o => o != null && o.WorkOrderId == id)).ToList();
            bool preOk = preStates.All(o => o == null || o.State == WorkOrderState.Completed || (HomeValleyWorkOrders.IsActive(o) && !SilentNightService.IsConstructionHeld(s, o)));
            string preLine = string.Join("、", pre.Zip(preStates, (id, o) => $"{id}:{(o == null ? "已完成并清理" : o.State.ToString())}"));
            var q = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, q);
            return held && preOk
                ? StepOutcome.Done($"静默夜里新放的 T2 电塔：施工单 {po.WorkOrderId} 暂缓（“{SilentNightService.HoldText(s)}”）；开始前就有的施工单照常执行（{preLine}）；施工队列 {q.Count} 项")
                : StepOutcome.Fail($"施工暂缓 / 照常不对：新电塔 {(pole == null ? "没有虚影" : po == null ? "没有施工单" : "暂缓 " + held)}；开始前的单 {preLine}");
        }

        private static StepOutcome TickRuleEditRejected(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            CampaignState s = St;
            StandingRuleRecord r = StandingRuleService.Find(s, c.GetInt("rule"));
            if (panel == null || r == null || !RulesPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("常驻规则面板 / 规则不见了");
            }
            if (!SilentNightService.IsActive(s))
            {
                return StepOutcome.Fail("静默夜已经结束（逐项确认太慢）");
            }
            if (!FgjM3Common.Done(c, "clk"))
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt(FgjM3Common.SK(c, "rej0"), SilentNightService.RejectCount);
                int row = Enumerable.Range(0, panel.VisibleRowCount).Where(i => panel.RowSerial(i) == r.Serial).DefaultIfEmpty(-1).First();
                bool? clicked = row < 0 ? false : P.ClickInView(panel.RowButton(row, "RrToggle"));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return StepOutcome.Retry($"点不到 R{r.Serial} 那一行的“停用”：{JourneyInput.LastUiFailure}");
                }
                FgjM3Common.Mark(c, "clk");
                c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 600)
            {
                return StepOutcome.Wait;
            }
            bool rejected = SilentNightService.RejectCount > c.GetInt(FgjM3Common.SK(c, "rej0")) && SilentNightService.LastReject.Contains("静默夜");
            return rejected && r.Enabled
                ? StepOutcome.Done($"点“停用”被拒：R{r.Serial} 仍启用；原因“{SilentNightService.LastReject}”（面板“{panel.MessageText}”）")
                : StepOutcome.Fail($"静默夜里改规则没被拒：启用 {r.Enabled}、拒绝 {SilentNightService.RejectCount - c.GetInt(FgjM3Common.SK(c, "rej0"))} 次、原因“{SilentNightService.LastReject}”");
        }

        private static StepOutcome TickCalendarDuringNight(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CalendarPanelUIToolkit cal = CalendarPanelUIToolkit.Instance;
            if (!CalendarPanelUIToolkit.IsOpen || cal == null || !cal.PanelVisible)
            {
                return StepOutcome.Retry("按日历键后日历面板没有打开");
            }
            cal.Refresh();
            CampaignState s = St;
            double left = SilentNightService.RemainingSeconds(s);
            string silent = cal.EventTexts.FirstOrDefault(t => t.Contains("静默夜")) ?? string.Empty;
            return SilentNightService.IsActive(s) && left > 0 && silent.Length > 0
                ? StepOutcome.Done($"静默夜里日历照常打开（{cal.NowText}）：“{silent.Replace("\n", " ")}”，剩余 {left / GameClock.HourSeconds:F1} 游戏小时")
                : StepOutcome.Fail($"静默夜里日历不对：进行中 {SilentNightService.IsActive(s)}、剩余 {left:F0} 秒、事件“{silent}”");
        }

        // ── FGT-ENV-008：静默夜 + 酸雨进行中存读档 ─────────────────────────────────────────

        /// <summary>存读档要逐项一致的环境状态：静默夜日程 / 进行中 / 暂缓施工单、天气段、预案持有、突袭计划里的静默夜触发（与世界步无关的稳定字段）。</summary>
        private static string EnvDigest(CampaignState s)
        {
            SilentNightState sn = s?.Weather?.SilentNight;
            WeatherFrontRecord active = s == null ? null : WeatherService.ActiveFront(s);
            string holds = string.Join(",", (s == null ? (IReadOnlyList<RuleHoldRecord>)Array.Empty<RuleHoldRecord>() : StandingRuleService.Holds(s)).Where(h => h != null).Select(h => $"{h.EntityId}@R{h.Rule}:{h.Prev}"));
            int raidSilent = s == null ? 0 : RaidDirectorService.Plans(s).Count(p => p != null && Array.IndexOf(p.Triggers ?? Array.Empty<string>(), RaidCatalog.TriggerSilentNight) >= 0)
                                         + (RaidDirectorService.StateOf(s)?.Pending ?? Array.Empty<RaidTriggerRecord>()).Count(t => t != null && t.Kind == RaidCatalog.TriggerSilentNight);
            return $"静默夜 第{sn?.NextDay}日 进行中{sn?.Active} 第{sn?.Count}次 {sn?.StartTick}～{sn?.EndTick} 暂缓[{string.Join(",", sn?.HeldOrderIds ?? Array.Empty<string>())}]；" +
                   $"天气 {active?.Kind}#{active?.Serial} {active?.StartTick}～{active?.EndTick}；预案持有 [{holds}]；静默夜突袭 {raidSilent}";
        }

        private static StepOutcome TickPauseForSave(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            CampaignState s = St;
            if (!SilentNightService.IsActive(s) || !WeatherService.IsActive(s, WeatherService.AcidRain))
            {
                return StepOutcome.Fail($"存档时应在静默夜 + 酸雨里：静默夜 {SilentNightService.IsActive(s)}、天气 {WeatherService.ActiveKind(s)}");
            }
            c.Set("pre", EnvDigest(s));
            c.SetLong("preTicks", GameClock.Ticks);
            c.SetLong("preLeft", s.Weather.SilentNight.EndTick - GameClock.Ticks);
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：{c.Get("pre")}；静默夜还剩 {c.GetLong("preLeft") / (double)GameClock.StepHz:F0} 游戏秒");
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            string got = EnvDigest(onDisk.State);
            return got == c.Get("pre")
                ? StepOutcome.Done($"回到主菜单；存档里的环境状态与存档那一刻逐项一致：{got}")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {got}");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive || c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            string now = EnvDigest(s);
            long dTicks = GameClock.Ticks - c.GetLong("preTicks");
            long left = s.Weather.SilentNight.EndTick - GameClock.Ticks;
            string banner = WorldBarHudUIToolkit.Instance?.SilentBannerText ?? string.Empty;
            bool same = now == c.Get("pre") && dTicks >= 0 && dTicks <= GameClock.StepHz * 5 && left == c.GetLong("preLeft") - dTicks;
            return same && banner.Contains("信号中断")
                ? StepOutcome.Done($"读档进入家园（已走 {dTicks} 步）：{now}；静默夜剩余 {left / (double)GameClock.StepHz:F0} 游戏秒（= 存档时剩余 − 读档后走的步数）；横幅“{banner}”")
                : StepOutcome.Fail($"读档后不一致（已走 {dTicks} 步，剩余 {left} / 存档时 {c.GetLong("preLeft")}，横幅“{banner}”）：\n      存档前 {c.Get("pre")}\n      读档后 {now}");
        }

        // ── 结束与风暴残骸 ──────────────────────────────────────────────────────────────

        private static readonly string[] DebrisItems = { "data_core", "enemy_wreck", "unparsed_module" };

        private static StepOutcome TickSilentEnded(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            SilentNightState sn = SilentNightService.StateOf(s);
            if (sn.Active)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "end"))
            {
                FgjM3Common.Mark(c, "end");
                c.SetLong(FgjM3Common.SK(c, "endAt"), NowMs());
                foreach (string item in DebrisItems)
                {
                    c.SetInt("stock0." + item, P.Stock(item));
                }
                // 已有的送货单（之后新开的 = 入库后按玩家设的去向再送出去的，例如敌方残骸送回收站 / 分析台，FG6）。
                c.Set("deliver0", string.Join(",", (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.Kind == WorkOrderKind.Deliver).Select(o => o.WorkOrderId)));
                var before = new HashSet<string>(c.Get("sn.ground").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                List<GroundItemRecord> debris = (s.GroundItems ?? Array.Empty<GroundItemRecord>())
                    .Where(g => g != null && g.RegionId == HomeValleyLayout.RegionId && !before.Contains(g.GroundItemId) && DebrisItems.Contains(g.ResourceType)).ToList();
                c.Set("debris.ids", string.Join(",", debris.Select(g => g.GroundItemId)));
                c.Set("debris.amt", string.Join(",", DebrisItems.Select(i => debris.Where(g => g.ResourceType == i).Sum(g => g.Amount).ToString(CultureInfo.InvariantCulture))));
                if (debris.Count > 0)
                {
                    P.SetP(c, "debris.first", GridCell.FromWorld(debris[0].Position));
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "endAt")) < 1500)
            {
                return StepOutcome.Wait;
            }
            string summary = sn.LastSummary ?? string.Empty;
            int n = c.Get("debris.ids").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
            int serial = c.GetInt("rule");
            bool restored = StandingRuleService.LogEntries(s).Any(l => l.Rule == serial && l.Key == "rules.log.silent_off");
            BuildingRecord st = P.Bld(c, "storage");
            bool released = st != null && StandingRuleService.FindHold(s, StandingRuleService.BuildingKey(st.BuildingId)) == null;
            List<GridCell> poleCells = FgjM3Common.DecodeCells(c.Get("sn.pole"));
            BuildingRecord pole = poleCells.Count > 0 ? P.BuildingAtPivot(s, P.PoleT2, poleCells[0]) : null;
            WorkOrderRecord po = pole != null ? s.WorkOrders?.LastOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == pole.BuildingId) : null;
            bool poleGo = pole != null && (pole.ConstructionState == BuildingConstructionState.Operational || (po != null && !SilentNightService.IsConstructionHeld(s, po)));
            bool notified = NotificationCenter.History.Any(e => e?.Type?.Id == "silent_night_end");
            bool ok = notified && summary.Contains("损失") && n >= 2 && restored && released && poleGo;
            return ok
                ? StepOutcome.Done($"静默夜结束：总结“{summary.Replace("\n", " ")}”；风暴残骸 {n} 处（物品 {c.Get("debris.amt")}，按 {string.Join("/", DebrisItems)}）；" +
                                   $"预案恢复储能站原设置（日志“恢复原状”）；静默夜里放的 T2 电塔不再暂缓（{(pole.ConstructionState == BuildingConstructionState.Operational ? "已建成" : po?.State.ToString())}）；下一次静默夜第 {sn.NextDay} 日")
                : StepOutcome.Fail($"静默夜结束后不对：通知 {notified}、总结“{summary}”、残骸 {n} 处、预案恢复 {restored}/{released}、电塔开工 {poleGo}");
        }

        private static StepOutcome TickDebrisToast(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            GridCell first = P.P(c, "debris.first");
            if (!FgjM3Common.Done(c, "clk"))
            {
                bool? clicked = D.ClickToast("silent_night_end", out NotificationEntry e);
                if (clicked == null)
                {
                    return c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Retry("“静默夜结束”通知弹出条没出现 / 已经收起");
                }
                if (clicked == false)
                {
                    return StepOutcome.Retry("点不到“静默夜结束”弹出条：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "clk");
                c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 1500)
            {
                return StepOutcome.Wait;
            }
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            float d = Vector2.Distance(new Vector2(f.x, f.y), new Vector2(first.X, first.Y));
            return d < 8f
                ? StepOutcome.Done($"点“静默夜结束”弹出条：镜头飞到第一处风暴残骸 {Cell(first)}（镜头中心离它 {d:F1} 格）")
                : StepOutcome.Fail($"点弹出条后镜头没到风暴残骸：镜头中心 ({f.x:F0}, {f.y:F0})，残骸 {Cell(first)}");
        }

        private static StepOutcome TickDebrisHome(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var ids = new HashSet<string>(c.Get("debris.ids").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            int left = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => g != null && ids.Contains(g.GroundItemId));
            int[] amt = c.Get("debris.amt").Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var deliver0 = new HashSet<string>(c.Get("deliver0").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            int Routed(string item) => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .Where(o => o != null && o.Kind == WorkOrderKind.Deliver && !deliver0.Contains(o.WorkOrderId) && o.ReservedItemId == item).Sum(o => o.ReservedItemAmount);
            var gains = DebrisItems.Select((item, i) => (item, gain: P.Stock(item) - c.GetInt("stock0." + item) + Routed(item), want: amt[i])).ToList();
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"风暴残骸还在地上 {left} 处；入库 {string.Join("、", gains.Select(g => $"{g.item} +{g.gain}/{g.want}"))}；{D.WorkDiag(s)}");
            }
            // 地面物在机器拾取时就消失了（货在机器货舱里），要等搬运单交付完（单子终结）再看入库。
            List<WorkOrderRecord> hauls = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.Kind == WorkOrderKind.Haul && ids.Contains(o.TargetId)).ToList();
            WorkOrderRecord failed = hauls.FirstOrDefault(o => o.State == WorkOrderState.Failed || o.State == WorkOrderState.Cancelled);
            if (failed != null)
            {
                return StepOutcome.Fail($"风暴残骸的搬运单 {failed.WorkOrderId} {failed.State}（{failed.FailureReason}）");
            }
            int pending = hauls.Count(o => HomeValleyWorkOrders.IsActive(o));
            if (left > 0 || pending > 0)
            {
                return StepOutcome.Wait;
            }
            bool ok = gains.All(g => g.gain >= g.want) && hauls.Count > 0 && hauls.All(o => o.State == WorkOrderState.Completed);
            return ok
                ? StepOutcome.Done($"风暴残骸全部搬回仓库（搬运单 {hauls.Count} 张全部交付）：{string.Join("、", gains.Select(g => $"{GameLogic.Campaign.Economy.ItemCatalog.NameOf(g.item)} +{g.gain}（其中入库后按残骸去向再送出 {Routed(g.item)}）"))}（{c.StepElapsed:F0} 秒，3 倍速）")
                : StepOutcome.Fail($"残骸从地上消失了，但入库不对：{string.Join("、", gains.Select(g => $"{g.item} +{g.gain}/应 {g.want}"))}");
        }

        private static StepOutcome TickFinal(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            long ret = PurificationService.ReturnedArea(s);
            long snRet = c.GetLong("sn.ret");
            BuildingRecord pur = P.Bld(c, "purifier");
            ProductionService.Producer pp = P.Prod(pur);
            if (ret <= c.GetLong("ret2") && c.StepElapsed < 590)
            {
                return StepOutcome.Wait;
            }
            if (ret <= c.GetLong("ret2"))
            {
                return StepOutcome.Fail($"归还面积没有继续增长：{c.GetLong("ret2")} → {ret}");
            }
            // 可达性估算（DEBT-FG7E2E01-02）：按本局这座 T1 塔的实测——半径内污染格、从开始到现在的降级速度。
            int radius = PurificationService.RadiusOfTier(E.Purifier, 1);
            int polluted0 = E.PollutedInRadius(s, P.P(c, "purifier"), radius) + (int)(ret - c.GetLong("ret0"));
            double hours = (GameClock.Ticks - c.GetLong("ret2.tick")) / (double)GameClock.StepHz / GameClock.HourSeconds;
            double finishH = pp != null ? PurificationService.SecondsToFinish(s, pur, pp.Rec) / GameClock.HourSeconds : -1;
            int perT1 = Math.Max(1, (int)(Math.PI * radius * radius));
            int perT2 = Math.Max(1, (int)(Math.PI * 12 * 12));
            ReturnMilestoneDef next = PurificationService.NextMilestone(s);
            int target = next?.Cells ?? 5000;
            return StepOutcome.Done($"归还面积 {c.GetLong("ret0")} → {c.GetLong("ret2")}（第一次增长）→ {snRet}（静默夜开始时）→ {ret} 格：净化塔整夜照常运转（静默夜不拦生产）；" +
                                   $"这座塔还要 {finishH:F1} 游戏小时净化完；{PurificationService.NextMilestoneText(s)?.Replace("\n", " ")}。" +
                                   $"可达性估算：一座 T1 塔半径 {radius} 格约 {perT1} 格、T2 半径 12 约 {perT2} 格（全是污染格时），{target:N0} 格约要 {Math.Ceiling(target / (double)perT1)} 座 T1 或 {Math.Ceiling(target / (double)perT2)} 座 T2" +
                                   $"（每座 T1 冷却液 30 升 / 分钟，一座调配站满速 1,500 升 / 分钟够约 50 座）——M7 内可达但要成片铺塔，列为待用户试玩项");
        }
    }
}
