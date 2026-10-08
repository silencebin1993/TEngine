using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Analysis;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M6 准备段：解析台数据复原“漏油”“燃迹”（火墙的两条固件）→ 蓝图编辑器把默认炮塔蓝图的固件换成燃迹、标出接入口（远征中跳回家园接入炮塔要用）→
    /// 装配站生产两台出征机器 → 研究完成后在常驻规则面板新建“自动重建”（整个家园）→ 炮塔面板给全部炮塔设同一个目标模式（批量）。
    /// </summary>
    public static partial class FgjM6Journey
    {
        /// <summary>全部炮塔批量设成的目标模式：先打正在破坏建筑的敌人（护住屏障与发电 / 信号建筑）。</summary>
        private const int BatchMode = (int)BinGames.Sim.Combat.CombatTargetMode.SiegeFirst;

        private static IEnumerable<JourneyStep> PrepSteps() => new[]
        {
            // ── 火墙的两条固件：解析台数据复原 ──
            S("ana_open", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
            S("ana_oil", "“数据复原”下拉选漏油，点“复原”：弹出确认框", 10, c => { MarkLabSpend(c); FgjM2Common.PickAndRestore(c, D.FwOil); }, c => FgjM2Common.TickConfirmShown(c, D.FwOil), retries: 1),
            S("ana_oil_ok", "确认框点“复原”：漏油解锁（技术数据 −复原费）", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, D.FwOil)),
            S("ana_burn", "下拉选燃迹，点“复原”：弹出确认框", 10, c => { MarkLabSpend(c); FgjM2Common.PickAndRestore(c, D.FwBurn); }, c => FgjM2Common.TickConfirmShown(c, D.FwBurn), retries: 1),
            S("ana_burn_ok", "确认框点“复原”：燃迹解锁", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, D.FwBurn)),
            S("ana_close", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),

            // ── 燃迹炮塔蓝图：默认炮塔蓝图换固件（接入口开局就标好了，保留；存成新版本；已经建成的六座炮塔仍装旧版本）──
            S("cb_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, D.CircuitHost, "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                FgjM1Journey.TickEditorOpen, retries: 1),
            S("cb_pick", "在蓝图列表点“基础炮塔·连射器”", 10, ClickTurretRow, TickTurretOpen, retries: 1),
            S("cb_fw", "固件槽 1 下拉选燃迹（替换寻的）", 10, c => FgjM2Common.PickFirmwareSlot0(c, D.FwBurn), c => FgjM2Common.TickFirmwareSlot0(c, D.FwBurn), retries: 1),
            S("cb_save", "点“保存”：默认炮塔蓝图存成新版本（燃迹 + 开局标好的接入口），之后放下的炮塔装它", 10, c => FgjM1Journey.ClickUi(c, D.CircuitHost, "SaveButton"), TickTurretSaved, retries: 1),
            S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),

            // ── 出征用的两台机器：装配站生产 ERC-003 ──
            S("fac_open", "左键点装配站打开生产面板", 10, c =>
                {
                    c.SetInt("verE", FgjM1Journey.Erc003Record()?.ActiveVersion ?? 0);
                    FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen);
                },
                FgjM1Journey.TickFactoryOpen, retries: 2),
            S("fac_sub", "“缺材料时用废料代付”没开就点开（家园没有零件产线，机器用废料按当量补）", 10, null, c => TickSubstitute(c, true), retries: 1),
            S("fac_p1", "点“生产 ERC-003”", 10, FgjM2Common.ClickProduce, c => TickQueued(c, 1), retries: 1),
            S("fac_p2", "再点一次“生产 ERC-003”", 10, FgjM2Common.ClickProduce, c => TickQueued(c, 2), retries: 1),
            S("fac_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                FgjM1Journey.TickFactoryClosed, retries: 2),
            S("wait_m1", "等第一台出厂（废料不够时等回收站拆出来）", 900, null, c => TickProducedNth(c, "m1")),
            S("m1_sel", "左键点新机（停在装配站出口）", 20, c => FgjM1Journey.ClickMachine(c, "m1"), c => FgjM1Journey.TickSelected(c, "m1"), retries: 4),
            S("m1_out", "右键点旁边的空地：新机驶出出口（右键地面 = 移动）", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "m1"), retries: 3),
            S("wait_m2", "等第二台出厂", 900, null, c => TickProducedNth(c, "m2", "m1")),
            S("m2_sel", "左键点第二台新机", 20, c => FgjM1Journey.ClickMachine(c, "m2"), c => FgjM1Journey.TickSelected(c, "m2"), retries: 4),
            S("m2_out", "右键点旁边的空地：第二台驶出出口", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "m2"), retries: 3),

            // ── 自动重建：研究完成后在常驻规则面板新建（不圈区域 = 整个家园）──
            S("wait_rebuild", "等研究：防御 · 陷阱发射器、防御 · 维修无人机站、防御 · 自动重建", 2400, null, c => D.TickResearched(c, D.NodeTrap, D.NodeDrone, D.NodeRebuild)),
            S("rules_open", "按常驻规则键（默认 Alt+R）打开常驻规则面板", 10, c => JourneyInput.PressAction(GameActionId.OpenRules), TickRulesOpen, retries: 1),
            S("rules_new", "“新建”下拉选“自动重建”：新建一条规则（不圈区域 = 整个家园，研究已解锁）", 15, null, TickRuleCreated, retries: 1),
            S("rules_close", "点“关闭”关闭常驻规则面板", 10, c => FgjM1Journey.ClickUi(c, D.RulesHost, "RulesPanelClose"), TickRulesClosed, retries: 1),

            // ── 炮塔目标模式：批量 ──
            S("tm_open", "建造模式里左键点第一座炮塔，通用面板上点“炮塔…”：炮塔面板打开", 30, null,
                c => M.TickSubPanel(c, "t1", "PrTurret", () => TurretPanelUIToolkit.IsOpen, "炮塔面板打开"), retries: 2),
            S("tm_mode", "点“先打正在破坏建筑的”，再点“全部炮塔都用这个模式”（批量）", 15, null, TickBatchMode, retries: 1),
            S("tm_close", "Esc 关掉炮塔面板与通用面板", 15, null, c => M.TickCloseAll(c, () => !TurretPanelUIToolkit.IsOpen, "炮塔面板"), retries: 1),
            S("tm_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("ready", "防线就绪（等第一次突袭）：六座炮塔、护盾、屏障圈、精炼链出燃油、自动重建规则启用；界面回到普通战略视角", 20, null, TickReady),
        };

        // ── 解析台：数据复原（结果核对不比废料——回收站同时在往家园送废料）──────────────────

        /// <summary>复原前记下实验室 / 复原两项技术数据支出（复原那一刻实验室可能刚好扣了一笔）。</summary>
        private static void MarkLabSpend(JourneyContext c)
        {
            c.SetLong("lab0", TechDataFlow.ExpenseOf(St, TechDataFlow.Lab));
            c.SetLong("restore0", TechDataFlow.ExpenseOf(St, TechDataFlow.Restore));
        }

        private static StepOutcome TickRestored(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int cost = FirmwareRestoreService.CostOf(fwId);
            bool unlocked = MechanicalContentUnlock.IsUnlocked(s, fwId);
            bool cracked = !FirmwareKinds.IsRaw(s, fwId);
            bool turret = FirmwareKinds.CanInstall(s, fwId, FirmwareHost.Turret, out _);
            bool trap = FirmwareKinds.CanInstall(s, fwId, FirmwareHost.Trap, out _);
            string result = AnalysisPanelUIToolkit.Instance?.ResultText ?? string.Empty;
            // 实验室同时在把技术数据转成研究点：期望值扣掉这段时间实验室的支出（技术数据收支按来源分项，FG5-E2E-01）。
            long lab = TechDataFlow.ExpenseOf(s, TechDataFlow.Lab) - c.GetLong("lab0");
            bool ok = !UiConfirmDialog.IsOpen && unlocked && cracked && turret && trap && s.TechData == c.GetInt("tech0") - cost - lab
                      && TechDataFlow.ExpenseOf(s, TechDataFlow.Restore) - c.GetLong("restore0") == cost;
            if (!ok)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait
                    : StepOutcome.Fail($"复原“{M.FwName(fwId)}”后状态不对：解锁 {unlocked}、已破解 {cracked}、能装进炮塔 {turret}、能装进陷阱 {trap}、技术数据 {c.GetInt("tech0")} → {s.TechData}（应 −{cost}，同期实验室支出 {lab}）；结果“{result}”");
            }
            return StepOutcome.Done($"点确认框的“复原”：“{result}”；技术数据 {c.GetInt("tech0")} → {s.TechData}（复原 −{cost}、同期实验室 −{lab}）；{M.FwName(fwId)} 已解锁、能装进炮塔蓝图与陷阱发射器");
        }

        // ── 蓝图编辑器：默认炮塔蓝图 ────────────────────────────────────────────────────

        private static BlueprintRecord TurretBlueprint() => St?.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == TurretService.DefaultLightBlueprintId);

        private static void ClickTurretRow(JourneyContext c)
        {
            BlueprintRecord r = TurretBlueprint();
            ScrollView list = JourneyInput.FindUitk<ScrollView>(D.CircuitHost, "BlueprintList");
            Button row = list?.Query<Button>("OpenButton").ToList()
                .FirstOrDefault(b => r != null && b.text != null && b.text.StartsWith(r.DisplayName, StringComparison.Ordinal));
            if (row != null)
            {
                JourneyInput.ScrollIntoView(list, row);
            }
            c.Set("rowText", row?.text ?? string.Empty);
            c.SetInt("bpTurret0", r?.ActiveVersion ?? 0);
            c.Set("uiFail", row != null && JourneyInput.IsClickable(row) && JourneyInput.ClickElement(row) ? string.Empty : row == null ? "列表里找不到默认炮塔蓝图" : JourneyInput.LastUiFailure);
        }

        /// <summary>
        /// 开局（放炮塔之前）：默认炮塔蓝图标一个接入口、存成新版本——之后造的炮塔都能接入（FGR-DEF-014：已建炮塔钉住建造时的版本，接入口要在建之前标）。
        /// 远征途中跳回家园时接入离来袭方向最近的那一座亲自开火。
        /// </summary>
        internal static IEnumerable<JourneyStep> UplinkPortSteps() => new[]
        {
            S("cb0_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, D.CircuitHost, "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                FgjM1Journey.TickEditorOpen, retries: 1),
            S("cb0_pick", "在蓝图列表点“基础炮塔·连射器”", 10, ClickTurretRow, TickTurretOpen, retries: 1),
            S("cb0_slot", "点选导线经过的空格", 10, ClickPathSlot, TickSlotPicked, retries: 1),
            S("cb0_mark", "点“标为接入口”", 10, c => FgjM1Journey.ClickUi(c, D.CircuitHost, "UplinkToggleButton"), TickMarked, retries: 1),
            S("cb0_save", "点“保存”：默认炮塔蓝图存成新版本（寻的 + 接入口），之后造的炮塔都能接入", 10, c => FgjM1Journey.ClickUi(c, D.CircuitHost, "SaveButton"), TickUplinkSaved, retries: 1),
            S("cb0_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
        };

        private static StepOutcome TickUplinkSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BlueprintRecord r = TurretBlueprint();
            BlueprintVersionRecord v = r?.Versions?.FirstOrDefault(x => x.Version == r.ActiveVersion);
            BlueprintCircuitBoard saved = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            string label = JourneyInput.FindUitk<Label>(D.CircuitHost, "SaveResultLabel")?.text ?? string.Empty;
            bool ok = saved != null && r.ActiveVersion > c.GetInt("bpTurret0") && saved.HasUplink && !saved.FirmwareSlots.Contains(D.FwBurn)
                      && TurretService.ValidateBlueprint(St, r.BlueprintId, TurretCatalog.SizeLight, out _, out _, out _, out string why);
            if (!ok)
            {
                return StepOutcome.Retry($"保存后默认炮塔蓝图不是“带接入口”的新版本（v{r?.ActiveVersion}，接入口 {saved?.HasUplink}；“{label}”）");
            }
            c.SetInt("bpUplink", v.Version);
            return StepOutcome.Done($"保存成功：“{TurretService.BlueprintName(r)}”现役 v{v.Version}（固件 [{string.Join("、", saved.FirmwareSlots.Where(x => !string.IsNullOrEmpty(x)).Select(M.FwName))}]、{saved.UplinkSlot} 号格接入口）；“{label}”");
        }

        private static StepOutcome TickTurretOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = FgjM1Journey.Panel()?.Board;
            return b != null && CarrierReadings.IsFixedChassis(b.ChassisId) && b.PrimaryId == ComponentCatalog.CompGunId
                ? StepOutcome.Done($"打开“{c.Get("rowText")}”（固定底盘、主组件连射器、固件 [{string.Join("、", b.FirmwareSlots.Where(x => !string.IsNullOrEmpty(x)).Select(M.FwName))}]）")
                : StepOutcome.Retry($"没有打开默认炮塔蓝图（{FgjM1Journey.UiFail(c)}；当前底盘 {b?.ChassisId}）");
        }

        private static void ClickPathSlot(JourneyContext c)
        {
            BlueprintCircuitBoard board = FgjM1Journey.Panel()?.Board;
            int target = -1;
            for (int i = 1; board != null && i < BlueprintCircuitLayout.SlotCount - 1; i++)
            {
                if (string.IsNullOrEmpty(board.SlotContentIds[i]) && board.IsOnSourceSinkPath(i))
                {
                    target = i;
                    break;
                }
            }
            c.SetInt("uplinkSlot", target);
            if (target > 0)
            {
                FgjM1Journey.ClickUi(c, D.CircuitHost, "Slot" + target);
            }
        }

        private static StepOutcome TickSlotPicked(JourneyContext c)
        {
            int slot = c.GetInt("uplinkSlot", -1);
            if (slot <= 0)
            {
                return StepOutcome.Fail("炮塔蓝图没有导线经过的空格（按蓝图现找，不写死格号）");
            }
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            CircuitUplinkView v = FgjM1Journey.Panel()?.UplinkView;
            return v?.ToggleButton != null && v.ToggleButton.text == GameText.Get("circuit.uplink.mark")
                ? StepOutcome.Done($"点选 {slot} 号格，检查器出现“{v.ToggleButton.text}”")
                : StepOutcome.Retry($"点选 {slot} 号格后检查器没有“标为接入口”（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickMarked(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            int slot = c.GetInt("uplinkSlot");
            CircuitBoardPanelUIToolkit p = FgjM1Journey.Panel();
            return p?.Board != null && p.Board.HasUplink && p.Board.UplinkSlot == slot && p.UplinkView.SlotShowsUplink(slot)
                ? StepOutcome.Done($"{slot} 号格成为接入口（双态预览：AI 驾驶“{p.UplinkView.AiLine(0)}”｜你接入时“{p.UplinkView.UplinkedLine(0)}”）")
                : StepOutcome.Retry($"{slot} 号格没有成为接入口（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickTurretSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BlueprintRecord r = TurretBlueprint();
            BlueprintVersionRecord v = r?.Versions?.FirstOrDefault(x => x.Version == r.ActiveVersion);
            BlueprintCircuitBoard saved = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            string label = JourneyInput.FindUitk<Label>(D.CircuitHost, "SaveResultLabel")?.text ?? string.Empty;
            bool ok = saved != null && r.ActiveVersion > c.GetInt("bpTurret0") && saved.FirmwareSlots.Contains(D.FwBurn) && saved.HasUplink
                      && TurretService.ValidateBlueprint(St, r.BlueprintId, TurretCatalog.SizeLight, out _, out _, out _, out string why);
            if (!ok)
            {
                return StepOutcome.Retry($"保存后默认炮塔蓝图不是“燃迹 + 接入口”的新版本（v{r?.ActiveVersion}，固件 [{string.Join("、", saved?.FirmwareSlots ?? Array.Empty<string>())}]，接入口 {saved?.HasUplink}；“{label}”）");
            }
            c.SetInt("bpBurn", v.Version);
            int old = TurretKeys().Count(k => TurretService.Find(St, P.Bld(c, k)?.BuildingId)?.BlueprintVersion == c.GetInt("bpTurret0"));
            return StepOutcome.Done($"保存成功：“{TurretService.BlueprintName(r)}”现役 v{v.Version}（燃迹、{saved.UplinkSlot} 号格接入口，能装上轻型炮塔座）；已建成的 {old} 座炮塔仍装 v{c.GetInt("bpTurret0")}；“{label}”");
        }

        private static IEnumerable<string> TurretKeys() => FgjM6Common.TurretKeys;

        // ── 装配站 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickSubstitute(JourneyContext c, bool on)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (AssemblyMaterials.ScrapSubstitute(St) == on)
            {
                if (FgjM3Common.Done(c, "tog") && FgjM3Common.SinceMs(c, "tog") < 600)
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Done($"“缺材料时用废料代付”已{(on ? "开" : "关")}（“{JourneyInput.FindUitk<Label>(P.FactoryHost, "SubstituteNote")?.text}”）");
            }
            Toggle t = JourneyInput.FindUitk<Toggle>(P.FactoryHost, "SubstituteToggle");
            if (!FgjM3Common.Once(c, "tog", () => JourneyInput.ClickElement(t)))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tog") < 1200 ? StepOutcome.Wait : StepOutcome.Retry($"点了代付开关后它还{(on ? "关" : "开")}着：" + JourneyInput.LastUiFailure);
        }

        private static int QueuedErc003() =>
            St.FactoryQueues?.Count(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id
                                         && q.State != FactoryQueueState.Completed && q.State != FactoryQueueState.Cancelled && q.State != FactoryQueueState.Failed) ?? 0;

        private static StepOutcome TickQueued(JourneyContext c, int want)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            int n = QueuedErc003();
            return n >= want
                ? StepOutcome.Done($"生产队列里 ERC-003 v{c.GetInt("verE")} 共 {n} 台待生产")
                : StepOutcome.Retry($"点“生产”后队列里只有 {n} 台 ERC-003（{FgjM1Journey.UiFail(c)}；{JourneyInput.FindUitk<Label>(P.FactoryHost, "ProduceHintLabel")?.text}）");
        }

        /// <summary>等一台新的 ERC-003 出厂（不是 <paramref name="exclude"/> 里记下的那几台），记到 <paramref name="idKey"/>。</summary>
        private static StepOutcome TickProducedNth(JourneyContext c, string idKey, params string[] exclude)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            var skip = new HashSet<int>(exclude.Select(k => c.GetInt(k)));
            MachineRecord m = MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.ChassisId == HomeValleyLayout.Erc003ChassisId && x.RegionId == HomeValleyLayout.RegionId
                                                                     && !skip.Contains(x.LogicId)).OrderBy(x => x.LogicId).FirstOrDefault();
            if (m == null || !FgjM1Journey.HomeMarker(m.LogicId, out _))
            {
                FactoryQueueItemRecord item = St.FactoryQueues?.FirstOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id
                                                                                   && (q.State == FactoryQueueState.Failed || q.State == FactoryQueueState.Cancelled));
                if (item != null)
                {
                    return StepOutcome.Fail($"ERC-003 生产 {item.State}：{item.BlockedReason}");
                }
                return StepOutcome.Wait;
            }
            c.SetInt(idKey, m.LogicId);
            return StepOutcome.Done($"{FgjM1Journey.Label(m.LogicId)} 出厂（ERC-003 v{m.BlueprintVersion}），停在装配站出口；废料 {St.Scrap}");
        }

        // ── 常驻规则：自动重建 ─────────────────────────────────────────────────────────

        private static StepOutcome TickRulesOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return RulesPanelUIToolkit.IsOpen ? StepOutcome.Done($"按 {InputDisplay.ForAction(GameActionId.OpenRules)} 打开常驻规则面板") : StepOutcome.Retry("按常驻规则键后面板没有打开");
        }

        private static StepOutcome TickRuleCreated(JourneyContext c)
        {
            CampaignState s = St;
            RulesPanelUIToolkit p = RulesPanelUIToolkit.Instance;
            StandingRuleRecord made = StandingRuleService.Ordered(s).FirstOrDefault(r => r.Kind == StandingRuleService.KindRebuild);
            if (made != null && FgjM3Common.Done(c, "pick"))
            {
                if (c.StepElapsed < 0.8)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt("rule", made.Serial);
                return made.Enabled && (made.Zones?.Length ?? 0) == 0
                    ? StepOutcome.Done($"“新建”下拉选“自动重建”：规则 R{made.Serial} 新建并启用（没有区域 = 整个家园）；面板“{p?.ZonesText}”")
                    : StepOutcome.Fail($"新建的自动重建规则 R{made.Serial} 没启用或带了区域（启用 {made.Enabled}、区域 {made.Zones?.Length}）：{StandingRuleService.ConfigIssue(s, made, out _, out _)}");
            }
            if (p == null || !RulesPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("常驻规则面板没开");
            }
            p.Refresh();
            DropdownField d = p.NewKindField;
            string want = GameText.Get("rules.kind.auto_rebuild.name");
            int idx = d?.choices?.FindIndex(x => x != null && x.Contains(want)) ?? -1;
            if (idx < 0)
            {
                return StepOutcome.Fail($"“新建”下拉里没有“{want}”（{string.Join("、", d?.choices ?? new List<string>())}）");
            }

            if (!JourneyInput.IsClickable(d))
            {
                return M.UiRetry("“新建”下拉点不到：");
            }
            // 下拉选择：设值 = 玩家在弹出菜单里点那一项（同一个 ChangeEvent 回调）。
            d.value = d.choices[idx];
            FgjM3Common.Mark(c, "pick");
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRulesClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !RulesPanelUIToolkit.IsOpen ? StepOutcome.Done("点“关闭”：常驻规则面板关闭") : StepOutcome.Retry("常驻规则面板还开着：" + FgjM1Journey.UiFail(c));
        }

        // ── 炮塔面板：批量目标模式 ──────────────────────────────────────────────────────

        private static StepOutcome TickBatchMode(JourneyContext c)
        {
            CampaignState s = St;
            TurretPanelUIToolkit panel = TurretPanelUIToolkit.Instance;
            if (panel == null || !TurretPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("炮塔面板没开");
            }
            panel.Refresh(force: true);
            List<TurretRecord> all = (TurretService.StateOf(s)?.Turrets ?? Array.Empty<TurretRecord>()).ToList();
            if (!FgjM3Common.Done(c, "mode"))
            {
                int slot = -1;
                for (int i = 0; i < 5; i++)
                {
                    if (panel.ModeCodeAt(i) == BatchMode)
                    {
                        slot = i;
                    }
                }
                if (slot < 0 || !JourneyInput.ClickElement(panel.ModeButton(slot)))
                {
                    return M.UiRetry("点不到目标模式按钮：");
                }
                c.SetInt("modeSlot", slot);
                FgjM3Common.Mark(c, "mode");
                c.SetLong(FgjM3Common.SK(c, "mAt"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "mAt")) < 600)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "all"))
            {
                if (!JourneyInput.ClickElement(panel.ModeAllButton))
                {
                    return M.UiRetry("点不到“全部炮塔都用这个模式”：");
                }
                FgjM3Common.Mark(c, "all");
                c.SetLong(FgjM3Common.SK(c, "aAt"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "aAt")) < 800)
            {
                return StepOutcome.Wait;
            }
            int same = all.Count(t => t.TargetMode == BatchMode);
            return all.Count >= 6 && same == all.Count && panel.ModeButton(c.GetInt("modeSlot")).text.StartsWith("● ", StringComparison.Ordinal)
                ? StepOutcome.Done($"点“{TurretCatalog.ModeName(BatchMode)}”再点“全部炮塔都用这个模式”：{same}/{all.Count} 座炮塔都改成这个模式（消息“{panel.MessageText}”）")
                : StepOutcome.Retry($"批量设置后只有 {same}/{all.Count} 座是“{TurretCatalog.ModeName(BatchMode)}”（消息“{panel.MessageText}”）");
        }

        private static long NowMs() => FgjM2Common.NowMs();

        private static StepOutcome TickReady(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            if (FgjM3Common.BuildOpen || ProductionPanelUIToolkit.IsOpen || TurretPanelUIToolkit.IsOpen || RulesPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("界面没有回到普通战略视角");
            }
            RaidPlanRecord p1 = RaidDirectorService.FindPlan(s, c.Get("plan1"));
            StandingRuleRecord rule = StandingRuleService.Find(s, c.GetInt("rule"));
            int turrets = TurretService.StateOf(s)?.Turrets?.Length ?? 0;
            return p1 != null && rule != null && rule.Enabled
                ? StepOutcome.Done($"防线就绪（第 {GameClock.Ticks} 步，第 {GameClock.DayOf(GameClock.GameSeconds)} 日）：炮塔 {turrets} 座、屏障 {D.WallsBuilt(c, out int wt)}/{wt}、自动重建 R{rule.Serial} 启用；" +
                                   $"等第一次突袭 {D.PlanLine(p1)}；暴露 {s.SignalExposure:F1}（广播{(s.SignalTowerBroadcastOff ? "已关闭" : "开着")}）；废料 {s.Scrap}")
                : StepOutcome.Fail($"防线就绪时缺东西：第一次突袭 {D.PlanLine(p1)}、自动重建规则 {(rule == null ? "没有" : rule.Enabled ? "启用" : "停用")}");
        }
    }
}
