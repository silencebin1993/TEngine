using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG4-E2E-01：FGJ-M4 第 1 步之后的各段（与 <see cref="FgjM4Journey"/> 同一条旅程）：
    /// ② 真实配方产线上的堵塞诊断、布局库、撤销 / 重做、升级（DEBT-FG3E2E01-01）；③“零件保持 50 个”规则自动排产；④ 精炼塔酸液堵塞、接废液池恢复；
    /// ⑤ 远征一次、回来看离家报告并与独立计数核对（后台一致）；⑥ 清空所有机器、应急打印救回家园。
    /// </summary>
    public static partial class FgjM4Journey
    {
        internal const string LayoutName = "精炼炉（FGJ-M4）";

        private static IEnumerable<JourneyStep> PhaseSteps() => new[]
        {
            // ── 第 1 步后半：真实配方产线上再走一遍堵塞诊断、布局库、撤销 / 重做、升级（DEBT-FG3E2E01-01）──
            S("haul_sel", "左键点新造的搬运机（停在装配站出口，等第一条命令）", 20, c => FgjM1Journey.ClickMachine(c, "hauler"), c => FgjM1Journey.TickSelected(c, "hauler"), retries: 4),
            S("haul_out", "右键点旁边的空地：搬运机驶出装配站出口、让出出口（右键地面 = 移动）", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "hauler"), retries: 3),
            S("rate", "测金属提取钻的实测速率（60 游戏秒）：后面离家期间的产量拿它对照", 120, null, TickObservedRate),
            S("jam_open", "按建造键打开建造模式（手上不拿条目）", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("jam", "等金属矿带中段有货的一格，鼠标指着它误按旋转键：这一格原地反转", 60, null, TickJam, retries: 2),
            S("jam_stall", "产线停了：金属提取钻输出堵塞、不再出矿", 60, null, TickJamStalled),
            S("diag_open", "按“为什么不工作”键（默认 Ctrl+O）：提取钻那一条追到顶牛的那一格", 30, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, true),
                TickJamDiagnosis, retries: 1),
            S("diag_click", "点根源那一条：镜头飞到那一格（建造模式不退出）", 15, null, TickJamLocate, retries: 2),
            S("fix", "指着误转的那一格再按旋转键：转回原来的方向", 20, null, TickJamFix, retries: 2),
            S("flow_back", "修好了：提取钻重新出矿，“为什么不工作”里提取钻那一条消失", 90, null, TickJamFlowBack),
            S("diag_close", "再按 Ctrl+O 关闭“为什么不工作”", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, false),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !DiagnosisPanelUIToolkit.IsOpen ? StepOutcome.Done("关闭“为什么不工作”") : StepOutcome.Retry("还开着"), retries: 1),
            S("copy_key", "按复制键（默认 Ctrl+C）进入复制模式", 10, c => JourneyInput.PressAction(GameActionId.Copy),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.CopyMode ? StepOutcome.Done("进入复制模式（状态行“" + FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault() + "”）") : StepOutcome.Retry("没进复制模式"), retries: 1),
            S("copy_box", "按住左键拖框框住精炼炉：松开后复制下来（带配方设置）、直接进入粘贴", 20, null, TickCopyFurnace, retries: 2),
            S("paste_exit", "右键退出粘贴", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.PasteMode, "退出粘贴，建造模式还开着"), retries: 2),
            S("lib_open", "按布局库键（默认 Ctrl+B）打开布局库", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, true),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : LayoutLibraryPanelUIToolkit.IsOpen ? StepOutcome.Done("布局库打开") : StepOutcome.Retry("布局库没打开"), retries: 1),
            S("lib_save", "名字框输入“" + LayoutName + "”，点“保存剪贴板”：布局库多一行（带缩略图）", 15, null, TickLibSaved, retries: 1),
            S("lib_close", "再按布局库键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, false),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !LayoutLibraryPanelUIToolkit.IsOpen ? StepOutcome.Done("布局库关闭") : StepOutcome.Retry("布局库还开着"), retries: 1),
            S("site2", "按种子地形另找一块放得下精炼炉的空地（离工厂块 8 格外），方向键平移镜头过去", 60, null, TickSite2),
            S("lib_open2", "按布局库键打开布局库", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, true),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : LayoutLibraryPanelUIToolkit.IsOpen ? StepOutcome.Done("布局库打开") : StepOutcome.Retry("布局库没打开"), retries: 1),
            S("lib_place", "点“" + LayoutName + "”那一行的“放置”：面板关掉，布局跟着鼠标", 10, null, TickLibPlace, retries: 1),
            S("paste_aim", "鼠标移过去让精炼炉落在那块空地上：预览 1 件能放", 30, null, TickPasteAim, retries: 2),
            S("paste_click", "左键放下：精炼炉虚影（带配方“合金（矿）”），整次放置是撤销栈里的一步", 15, null, TickPasted, retries: 2),
            S("paste_exit2", "右键退出粘贴", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.PasteMode, "退出粘贴"), retries: 2),
            S("undo", "按撤销键（默认 Ctrl+Z）：刚放下的虚影取消", 10, c => JourneyInput.PressAction(GameActionId.Undo), TickUndone, retries: 1),
            S("redo", "按重做键（默认 Ctrl+Y）：虚影放回原处", 10, c => JourneyInput.PressAction(GameActionId.Redo), TickRedone, retries: 1),
            S("up_key", "按升级键（默认 U）进入升级规划", 10, c => JourneyInput.PressAction(GameActionId.UpgradePlan),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.UpgradeMode ? StepOutcome.Done("进入升级规划") : StepOutcome.Retry("没进升级规划"), retries: 1),
            S("up_boxes", "沿成品干线一段一段拖框：每格生成 T1 → T2 升级施工", 60, null, TickUpgradeBoxes, retries: 1),
            S("up_exit", "右键退出升级规划", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.UpgradeMode, "退出升级规划"), retries: 2),
            S("build_close3", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("up_wait", "机器送差额材料、逐格升完：成品干线全线 T2；复制的精炼炉建成，配方仍是“合金（矿）”（布局带着配方设置），没有来料写“缺料”", 600, null, TickUpgradedAndCopyBuilt),

            // ── 第 2 步：设置“零件保持 50 个”的规则，看它自动排产 ──
            S("rules_open", "按常驻规则键（默认 Alt+R）打开常驻规则面板", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenRules, () => RulesPanelUIToolkit.IsOpen, true),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : RulesPanelUIToolkit.IsOpen ? StepOutcome.Done("常驻规则面板打开（“" + RulesPanelUIToolkit.Instance?.CountText + "”）") : StepOutcome.Retry("没打开"), retries: 1),
            S("rule_new", "“从预设新建”下拉选“零件保底 50 件（零件工坊）”：新建一条库存维持规则（先停用）", 15, null, TickRuleFromPreset, retries: 1),
            S("rule_factory", "工厂下拉选做结构材的那座零件工坊", 15, null, TickRuleFactory, retries: 1),
            S("rule_enable", "点这一行的“启用”", 15, null, TickRuleEnable, retries: 1),
            S("rules_close", "点“关闭”关闭常驻规则面板", 10, c => FgjM1Journey.ClickUi(c, P.RulesHost, "RulesPanelClose"),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !RulesPanelUIToolkit.IsOpen ? StepOutcome.Done("关闭常驻规则面板") : StepOutcome.Retry("还开着：" + FgjM1Journey.UiFail(c)), retries: 1),
            S("rule_fires", "规则按每游戏分钟检查一次：零件少于 50 件时，那座零件工坊被改成排产“零件”（这一单可追溯到规则）", 240, null, TickRuleFired),
            S("rule_panel_open", "建造模式里点那座零件工坊：通用面板写“由规则 R… 触发：排产……”", 30, null, c => P.TickOpenPanel(c, c.Get("pwS.type"), P.P(c, "pwS")), retries: 2),
            S("rule_panel_read", "通用面板的规则行写明是哪条规则改的", 10, null, TickRuleLineOnPanel),
            S("rule_panel_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
            S("rule_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("rule_done", "零件补到 50 件：规则把那座零件工坊恢复为“结构材”，触发日志里有开始 / 补足两条", 600, null, TickRuleRestocked),
        };

        // ── 实测速率 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickObservedRate(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord d = P.Bld(c, "drillM");
            BuildingRecord f = P.Bld(c, "furnace");
            if (!FgjM3Common.Done(c, "t0"))
            {
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.SetLong(FgjM3Common.SK(c, "d0"), P.Completed(d));
                c.SetLong(FgjM3Common.SK(c, "f0"), P.Completed(f));
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            double dt = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            if (dt < 60.0)
            {
                return StepOutcome.Wait;
            }
            double ore = (P.Completed(d) - c.GetLong(FgjM3Common.SK(c, "d0"))) / dt * 60.0;
            double alloy = (P.Completed(f) - c.GetLong(FgjM3Common.SK(c, "f0"))) / dt * 60.0;
            ProducerDef pd = P.Prod(d)?.Def;
            double design = pd != null && pd.CycleSeconds > 0 ? 60.0 / pd.CycleSeconds * Math.Max(1, pd.CycleAmount) : 0;
            c.Set("rateOre", ore.ToString("R", CultureInfo.InvariantCulture));
            c.Set("rateAlloy", alloy.ToString("R", CultureInfo.InvariantCulture));
            return ore > 0 && alloy > 0 && ore <= design * 1.05
                ? StepOutcome.Done($"{dt:F0} 游戏秒里金属提取钻出矿 {ore:F1} 份 / 分钟（表里 {design:F0}）、精炼炉出合金 {alloy:F1} 次 / 分钟；库存 {P.StockLine("alloy", "structural", "part", "electronic", "combat_component")}")
                : StepOutcome.Fail($"实测速率不对：出矿 {ore:F1}（表里 {design:F0}）、合金 {alloy:F1}");
        }

        // ── 堵塞 → 诊断 → 修好 ─────────────────────────────────────────────────────────

        private static StepOutcome TickJam(JourneyContext c)
        {
            string pick = FgjM3Common.SK(c, "pick");
            if (FgjM3Common.Mode.SelectedEntryId != null)
            {
                return StepOutcome.Retry("建造模式手上还拿着条目：" + FgjM3Common.Mode.SelectedEntryId);
            }
            if (!FgjM3Common.Done(c, "pick"))
            {
                List<GridCell> route = P.LoadPath(c, "path.drillM");
                List<int> dirs = FgjM3Common.RouteDirs(route, c.GetInt("path.drillM.dir"));
                GridCell found = default;
                int fd = -1;
                for (int k = 0; k < route.Count && fd < 0; k++)
                {
                    int i = route.Count / 2 + ((k & 1) == 0 ? k / 2 : -(k / 2 + 1));
                    if (i <= 0 || i >= route.Count - 1 || dirs[i] != dirs[i - 1] || dirs[i] != dirs[i + 1])
                    {
                        continue;
                    }
                    if (FgjM3Common.BeltInfo(route[i], out BeltCellInfo info) && info.Count > 0)
                    {
                        found = route[i];
                        fd = dirs[i];
                    }
                }
                if (fd < 0)
                {
                    JourneyCommon.ResumeIfAutoPaused(c);
                    return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("金属矿带段内一直没有有货的直格");
                }
                FgjM3Common.SetCell(c, pick + ".cell", found);
                c.SetInt(pick + ".dir", fd);
                FgjM3Common.Mark(c, "pick");
            }
            GridCell x = FgjM3Common.GetCell(c, pick + ".cell");
            int dir = c.GetInt(pick + ".dir");
            if (!FgjM3Common.HoverThenPress(c, "rot", x, GameActionId.Rotate))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rot.press") < 500)
            {
                return StepOutcome.Wait;
            }
            bool reversed = FgjM3Common.BeltInfo(x, out BeltCellInfo now) && (int)now.Dir == P.Opp(dir);
            if (!reversed)
            {
                return StepOutcome.Retry($"按旋转键后 {P.Cell(x)} 没有反转（状态行“{FgjM3Common.Mode.StatusText}”）");
            }
            FgjM3Common.SetCell(c, "jam", x);
            c.SetInt("jamDir", dir);
            c.SetLong("jamAtMs", FgjM3Common.NowMs());
            c.SetLong("jamAtTick", GameClock.Ticks);
            return StepOutcome.Done($"指着金属矿带的 {P.Cell(x)}（格上 {now.Count} 件货）按旋转键：原地反转，状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”");
        }

        private static StepOutcome TickJamStalled(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            ProductionService.Producer d = P.Prod(P.Bld(c, "drillM"));
            long done = d?.Rec.Completed ?? -1;
            string k = FgjM3Common.SK(c, "last");
            if (c.GetLong(k, -2) != done)
            {
                c.SetLong(k, done);
                c.SetLong(k + ".tick", GameClock.Ticks);
                return StepOutcome.Wait;
            }
            double still = (GameClock.Ticks - c.GetLong(k + ".tick")) / (double)GameClock.StepHz;
            return still >= 6.0 && d.State == ProdState.OutputBlocked
                ? StepOutcome.Done($"产线停了：{still:F0} 游戏秒里金属提取钻停在 {done} 次，状态“{ProductionService.StateText(d)}：{ProductionService.ReasonText(St, d)}”")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickJamDiagnosis(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            if (!DiagnosisPanelUIToolkit.IsOpen || panel == null || !panel.PanelVisible)
            {
                return StepOutcome.Retry("按 Ctrl+O 后“为什么不工作”没有打开");
            }
            string drill = HomeGridService.DisplayName(P.Drill);
            GridCell x = FgjM3Common.GetCell(c, "jam");
            GridCell up = P.Step(x, P.Opp(c.GetInt("jamDir")));
            int row = Enumerable.Range(0, panel.RowCount).FirstOrDefault(i => panel.SubjectText(i).Contains(drill)) is int r && panel.RowCount > 0 && panel.SubjectText(r).Contains(drill) ? r : -1;
            int root = -1;
            var steps = new List<string>();
            for (int i = 0; i < panel.StepButtonCount; i++)
            {
                DiagStep st = panel.StepTarget(i);
                if (st == null)
                {
                    continue;
                }
                steps.Add($"{st.Code}:{st.Text}");
                string t = st.Text ?? string.Empty;
                bool nearPair = st.HasPosition && (Vector2.Distance(st.Position, FgjM3Common.Ground(x)) <= 0.6f || Vector2.Distance(st.Position, FgjM3Common.Ground(up)) <= 0.6f);
                bool namesX = t.Contains($"（{x.X}, {x.Y}）") || (nearPair && Vector2.Distance(st.Position, FgjM3Common.Ground(x)) <= 0.6f);
                if (root < 0 && nearPair && namesX && (t.Contains("朝向相反") || t.Contains("朝向反了")))
                {
                    root = i;
                }
            }
            if (row < 0 || root < 0)
            {
                return c.StepElapsed < 20 ? StepOutcome.Wait
                    : StepOutcome.Fail($"“为什么不工作”没有追到误转的 {P.Cell(x)}：提取钻那一行 {row}；各条 [{string.Join(" | ", steps)}]");
            }
            // FGJ-M4 抓到并修掉两处：经分流器供料的建筑、以及输入带中途一格转反（带被截成两段）时，原来都写“输入带上没有能供应的建筑”。
            // 这条产线里每座缺料的建筑上游都有产出它的建筑，不该出现这一句。
            List<string> falseNone = steps.Where(t => t.StartsWith(DiagCode.ProdNoSupplier + ":", StringComparison.Ordinal)).ToList();
            if (falseNone.Count > 0)
            {
                return StepOutcome.Fail($"“为什么不工作”误判没有供应者（上游明明接着产出它的建筑，经分流器或被转反的一格截断）：{string.Join(" | ", falseNone)}");
            }
            c.SetInt("diagRoot", root);
            c.SetInt("fly0", WorldView.FlyCount);
            return StepOutcome.Done($"“为什么不工作”列出 {panel.RowCount} 个停工对象，其中“{panel.SubjectText(row)}”；根源“{panel.StepTarget(root).Text}”点名误转的 {P.Cell(x)}（各条 [{string.Join(" → ", steps)}]）");
        }

        private static StepOutcome TickJamLocate(JourneyContext c)
        {
            DiagStep target = DiagnosisPanelUIToolkit.Instance?.StepTarget(c.GetInt("diagRoot"));
            if (target == null)
            {
                return StepOutcome.Fail("诊断面板上找不到根源那一条");
            }
            // 镜头此刻就停在根源附近（误转时指着那一格，镜头已经在那里）：先按方向键把镜头平移开，点根源时“飞过去”才看得出来（玩家的镜头通常在别处）。
            if (!FgjM3Common.Done(c, "away"))
            {
                Unity.Mathematics.float2 f0 = WorldView.Director.StrategyFocus;
                if (Mathf.Abs(f0.x - target.Position.x) < 6f && Mathf.Abs(f0.y - target.Position.y) < 6f)
                {
                    if (!JourneyInput.Holding)
                    {
                        FgjM3Common.PanTo(new Vector2(target.Position.x + 14f, target.Position.y + 10f));
                    }
                    return StepOutcome.Wait;
                }
                if (JourneyInput.Holding)
                {
                    return StepOutcome.Wait;
                }
                FgjM3Common.Mark(c, "away");
                c.SetInt("fly0", WorldView.FlyCount);
                return StepOutcome.Wait;
            }
            // 根源那一条在面板滚动区的可见区外时，像玩家一样先在列表上滚滚轮（被下面的说明文字盖住也算不在可见区）。
            VisualElement btn = JourneyInput.FindUitk<VisualElement>(FgjM3Common.DiagHost, "DgStep" + c.GetInt("diagRoot").ToString(CultureInfo.InvariantCulture));
            ScrollView sv = null;
            for (VisualElement e = btn?.parent; e != null && sv == null; e = e.parent)
            {
                sv = e as ScrollView;
            }
            if (!FgjM3Common.Done(c, "click") && sv != null && !JourneyInput.ScrollIntoView(sv, btn))
            {
                c.SetInt("diagScrolls", c.GetInt("diagScrolls") + 1);
                return c.GetInt("diagScrolls") > 40 ? StepOutcome.Fail("根源那一条滚不到可见区：" + JourneyInput.LastUiFailure) : StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "click", () =>
                {
                    if (!JourneyInput.ClickElement(btn))
                    {
                        c.Set("uiFail", JourneyInput.LastUiFailure);
                        return false;
                    }
                    return true;
                }))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 1500)
            {
                return StepOutcome.Wait;
            }
            DiagStep st = DiagnosisPanelUIToolkit.Instance?.StepTarget(c.GetInt("diagRoot"));
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            bool flew = WorldView.FlyCount > c.GetInt("fly0") && st != null && Mathf.Abs(f.x - st.Position.x) < 1.5f && Mathf.Abs(f.y - st.Position.y) < 1.5f;
            double secs = (FgjM3Common.NowMs() - c.GetLong("jamAtMs")) / 1000.0;
            return flew && FgjM3Common.BuildOpen && DiagnosisPanelUIToolkit.IsOpen
                ? StepOutcome.Done($"点“{st.Text}”：镜头飞到 ({st.Position.x:F0}, {st.Position.y:F0})，建造模式与面板都还开着；从误转到点中根源 {secs:F1} 真实秒（机器人口径）")
                : StepOutcome.Retry($"点根源后镜头没飞过去（飞行次数 {WorldView.FlyCount}，焦点 ({f.x:F1}, {f.y:F1})；{c.Get("uiFail")}）");
        }

        private static StepOutcome TickJamFix(JourneyContext c)
        {
            GridCell x = FgjM3Common.GetCell(c, "jam");
            int dir = c.GetInt("jamDir");
            if (!FgjM3Common.HoverThenPress(c, "rot", x, GameActionId.Rotate))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rot.press") < 500)
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.BeltInfo(x, out BeltCellInfo info) && (int)info.Dir == dir
                ? StepOutcome.Done($"指着 {P.Cell(x)} 再按旋转键：转回朝{FgjM3Common.DirName(dir)}")
                : StepOutcome.Retry($"再按旋转键后 {P.Cell(x)} 朝{FgjM3Common.DirName((int)info.Dir)}");
        }

        private static StepOutcome TickJamFlowBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            long done = P.Completed(P.Bld(c, "drillM"));
            if (!FgjM3Common.Done(c, "d0"))
            {
                c.SetLong(FgjM3Common.SK(c, "d0v"), done);
                FgjM3Common.Mark(c, "d0");
                return StepOutcome.Wait;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            string drill = HomeGridService.DisplayName(P.Drill);
            bool listed = panel != null && Enumerable.Range(0, panel.RowCount).Any(i => panel.SubjectText(i).Contains(drill));
            long gained = done - c.GetLong(FgjM3Common.SK(c, "d0v"));
            return gained >= 4 && !listed
                ? StepOutcome.Done($"修好后金属提取钻又出了 {gained} 份矿；“为什么不工作”里提取钻那一条消失（剩 {panel?.RowCount} 个停工对象）")
                : StepOutcome.Wait;
        }

        // ── 布局库：精炼炉（带配方设置）存进布局库、在另一处放一份、撤销 / 重做 ─────────────────────

        private static StepOutcome TickCopyFurnace(JourneyContext c)
        {
            GridCell pivot = P.P(c, "furnace");
            GridMath.FootprintBounds(pivot, 3, 3, 0, out GridCell min, out GridCell max);
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(min), FgjM3Common.Ground(max))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "drag") < 800)
            {
                return StepOutcome.Wait;
            }
            PlanEntryBlock clip = HomeValleyBuildMode.Clipboard;
            int n = PlanEntries.CountOf(clip);
            bool furnace = clip != null && clip.Ids.Count(x => x == P.Furnace) == 1;
            return n == 1 && furnace && FgjM3Common.Mode.PasteMode
                ? StepOutcome.Done($"拖框 {P.Cell(min)}～{P.Cell(max)}：复制了 1 件（精炼炉），直接进入粘贴")
                : StepOutcome.Retry($"复制结果不对：{n} 件（{string.Join(",", clip?.Ids ?? Array.Empty<string>())}）、粘贴模式 {FgjM3Common.Mode.PasteMode}");
        }

        private static StepOutcome TickRightClickExit(JourneyContext c, Func<bool> exited, string what)
        {
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            var at = new Vector2(Mathf.Round(f.x), Mathf.Round(f.y));
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(at, 1)))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rc") < 500)
            {
                return StepOutcome.Wait;
            }
            return exited() && FgjM3Common.BuildOpen ? StepOutcome.Done("右键" + what) : StepOutcome.Retry("右键后没有" + what);
        }

        private static int LibRow(string name)
        {
            LayoutLibraryPanelUIToolkit panel = LayoutLibraryPanelUIToolkit.Instance;
            for (int i = 0; panel != null && i < 64; i++)
            {
                string n = panel.RowName(i);
                if (string.IsNullOrEmpty(n))
                {
                    break;
                }
                if (n == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private static StepOutcome TickLibSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            LayoutLibraryPanelUIToolkit panel = LayoutLibraryPanelUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "save"))
            {
                TextField name = JourneyInput.FindUitk<TextField>(FgjM3Common.LibHost, "LayoutLibraryName");
                if (name == null || !JourneyInput.ClickElement(name))
                {
                    return StepOutcome.Retry("点不到布局库的名字框：" + JourneyInput.LastUiFailure);
                }
                c.SetInt("lib0", LayoutLibrary.Count);
                name.value = LayoutName;
                if (!JourneyInput.ClickUitk(FgjM3Common.LibHost, "LayoutLibrarySave"))
                {
                    return StepOutcome.Retry("点不到“保存剪贴板”：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "save");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            panel?.Refresh();
            int row = LibRow(LayoutName);
            bool ok = LayoutLibrary.Count == c.GetInt("lib0") + 1 && row >= 0 && panel != null && panel.RowThumbnail(row) != null && File.Exists(LayoutLibrary.FilePath);
            return ok
                ? StepOutcome.Done($"布局库多一行“{panel.RowName(row)}”（{panel.RowInfo(row)}，带缩略图）")
                : StepOutcome.Retry($"保存后布局库 {LayoutLibrary.Count} 个（原 {c.GetInt("lib0")}），找不到“{LayoutName}”");
        }

        private static StepOutcome TickSite2(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "found"))
            {
                GridCell o = P.P(c, "plan.origin");
                HashSet<long> avoid = P.BaseAvoid(s);
                foreach (string k in c.Get("plan.used", string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    avoid.Add(long.Parse(k, CultureInfo.InvariantCulture));
                }
                GridCell? pick = null;
                for (int r = 8; r <= 40 && pick == null; r++)
                {
                    for (int dy = -r; dy <= r && pick == null; dy++)
                    {
                        for (int dx = -r; dx <= r && pick == null; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            {
                                continue;
                            }
                            var p = new GridCell(o.X + dx, o.Y + dy);
                            if (!P.Placeable(s, P.Furnace, p, 0, avoid))
                            {
                                continue;
                            }
                            // 四周留一圈空（端口外侧格也空着），不贴着别的东西。
                            bool clear = true;
                            for (int yy = -2; yy <= 2 && clear; yy++)
                            {
                                for (int xx = -2; xx <= 2 && clear; xx++)
                                {
                                    clear &= !avoid.Contains(P.Key(new GridCell(p.X + xx, p.Y + yy)));
                                }
                            }
                            if (clear)
                            {
                                pick = p;
                            }
                        }
                    }
                }
                if (pick == null)
                {
                    return StepOutcome.Fail("工厂块 8～40 格外按种子地形找不到放得下精炼炉的空地");
                }
                P.SetP(c, "copy", pick.Value);
                FgjM3Common.Mark(c, "found");
            }
            GridCell at = P.P(c, "copy");
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Clear(FgjM3Common.Ground(at), out _))
            {
                FgjM3Common.PanTo(FgjM3Common.Ground(at));
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"另一块空地 {P.Cell(at)}（离工厂块原点 {Vector2.Distance(FgjM3Common.Ground(at), FgjM3Common.Ground(P.P(c, "plan.origin"))):F0} 格），方向键平移镜头到那里");
        }

        private static StepOutcome TickLibPlace(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "place"))
            {
                int row = LibRow(LayoutName);
                if (row < 0)
                {
                    return StepOutcome.Fail($"布局库里没有“{LayoutName}”");
                }
                Button place = LayoutLibraryPanelUIToolkit.Instance.RowButton(row, "LlPlace");
                if (place == null || !JourneyInput.ClickElement(place))
                {
                    return StepOutcome.Retry("点不到“放置”：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "place");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.9)
            {
                return StepOutcome.Wait;
            }
            return !LayoutLibraryPanelUIToolkit.IsOpen && FgjM3Common.Mode.PasteMode && PlanEntries.CountOf(FgjM3Common.Mode.PasteSource) == 1
                ? StepOutcome.Done("点“放置”：布局库关掉，建造模式进入粘贴（1 件跟着鼠标）")
                : StepOutcome.Retry($"点“放置”后：布局库开着 {LayoutLibraryPanelUIToolkit.IsOpen}、粘贴模式 {FgjM3Common.Mode.PasteMode}");
        }

        private static StepOutcome TickPasteAim(JourneyContext c)
        {
            HomeValleyBuildMode mode = FgjM3Common.Mode;
            if ((mode.PasteQuarter & 3) != 0)
            {
                if (JourneyInput.Holding || FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "rot")) < 300)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "rot"), FgjM3Common.NowMs());
                JourneyInput.PressAction(GameActionId.Rotate);
                return StepOutcome.Wait;
            }
            GridCell target = P.P(c, "copy");
            string ak = FgjM3Common.SK(c, "anchor");
            if (string.IsNullOrEmpty(c.Get(ak)))
            {
                FgjM3Common.SetCell(c, ak, target);
                c.Set(ak, "1");
            }
            GridCell anchor = FgjM3Common.GetCell(c, ak);
            string hk = "hover" + anchor.X + "_" + anchor.Y;
            if (!FgjM3Common.Once(c, hk, () => FgjM3Common.TryHover(FgjM3Common.Ground(anchor))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, hk) < 500)
            {
                return StepOutcome.Wait;
            }
            PastePlan pv = mode.PastePreview;
            PasteItem item = pv?.Items.FirstOrDefault(i => i.Id == P.Furnace);
            if (pv == null || item == null || mode.HoverCell != anchor)
            {
                return FgjM3Common.SinceMs(c, hk) < 2000 ? StepOutcome.Wait : StepOutcome.Retry("粘贴预览没有跟着鼠标");
            }
            if (item.Cell != target)
            {
                if (c.GetInt(FgjM3Common.SK(c, "adjust")) >= 3)
                {
                    return StepOutcome.Fail($"对不准：精炼炉落在 {P.Cell(item.Cell)}，要落在 {P.Cell(target)}");
                }
                c.SetInt(FgjM3Common.SK(c, "adjust"), c.GetInt(FgjM3Common.SK(c, "adjust")) + 1);
                FgjM3Common.SetCell(c, ak, new GridCell(anchor.X + target.X - item.Cell.X, anchor.Y + target.Y - item.Cell.Y));
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (pv.OkCount != 1 || pv.BadCount != 0)
            {
                return StepOutcome.Fail($"对准后预览不能放：能放 {pv.OkCount}、不能放 {pv.BadCount}（“{info.Replace("\n", " ")}”）");
            }
            FgjM3Common.SetCell(c, "anchor2", anchor);
            return StepOutcome.Done($"鼠标移到 {P.Cell(anchor)}：精炼炉落在 {P.Cell(target)}，预览 1 件能放（“{info.Replace("\n", " ")}”）");
        }

        private static StepOutcome TickPasted(JourneyContext c)
        {
            GridCell anchor = FgjM3Common.GetCell(c, "anchor2");
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(anchor))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 600)
            {
                return StepOutcome.Wait;
            }
            PlanApplyResult r = FgjM3Common.Mode.LastPaste;
            BuildingRecord ghost = P.BuildingAtPivot(St, P.Furnace, P.P(c, "copy"));
            return r != null && r.PlacedBuildings == 1 && ghost != null && HomeValleyController.IsPlannedGhost(ghost) && PlanHistory.PeekUndo(St) == PlanStepKind.Paste
                ? StepOutcome.Done($"左键放下精炼炉虚影 {P.Cell(P.P(c, "copy"))}，撤销栈顶是“{PlanHistory.StepName(PlanStepKind.Paste)}”")
                : StepOutcome.Retry($"放下后：结果建筑 {r?.PlacedBuildings} 座 / 传送带管线 {r?.PlacedPieces} 件、虚影 {ghost != null}、撤销栈顶 {PlanHistory.PeekUndo(St)}");
        }

        private static StepOutcome TickUndone(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord ghost = P.BuildingAtPivot(St, P.Furnace, P.P(c, "copy"));
            string status = FgjM3Common.Mode.StatusText ?? string.Empty;
            return ghost == null && PlanHistory.PeekRedo(St) == PlanStepKind.Paste && status.Contains(GameText.Format("plan.undo.done", PlanHistory.StepName(PlanStepKind.Paste)))
                ? StepOutcome.Done($"Ctrl+Z：精炼炉虚影取消；状态行“{status.Split('\n').FirstOrDefault()}”；重做栈顶是“{PlanHistory.StepName(PlanHistory.PeekRedo(St))}”")
                : StepOutcome.Retry($"撤销后虚影还在 {ghost != null}、重做栈顶 {PlanHistory.PeekRedo(St)}、状态行“{status}”");
        }

        private static StepOutcome TickRedone(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord ghost = P.BuildingAtPivot(St, P.Furnace, P.P(c, "copy"));
            return ghost != null && PlanHistory.PeekUndo(St) == PlanStepKind.Paste
                ? StepOutcome.Done($"Ctrl+Y：精炼炉虚影放回原处 {P.Cell(P.P(c, "copy"))}；状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”")
                : StepOutcome.Retry($"重做后虚影在 {ghost != null}、撤销栈顶 {PlanHistory.PeekUndo(St)}");
        }

        // ── 升级成品干线 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickUpgradeBoxes(JourneyContext c)
        {
            List<GridCell> route = P.LoadPath(c, "path.r_trunk");
            List<(int a, int b, int dir)> runs = FgjM3Common.Runs(route, c.GetInt("path.r_trunk.dir"));
            string ri = FgjM3Common.SK(c, "run");
            int k = c.GetInt(ri);
            if (k >= runs.Count)
            {
                int planned = route.Count(p => HomeValleyConstruction.TryFindUpgradeCell(St, p, out _, out _));
                return planned == route.Count && PlanHistory.PeekUndo(St) == PlanStepKind.Upgrade
                    ? StepOutcome.Done($"{runs.Count} 次拖框：成品干线 {planned} 格全部生成 T1 → T2 升级施工（状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”）")
                    : StepOutcome.Retry($"拖框后升级施工 {planned}/{route.Count} 格");
            }
            (int a, int b, int dir) run = runs[k];
            string rk = "u" + k.ToString(CultureInfo.InvariantCulture);
            if (!FgjM3Common.Once(c, rk, () => FgjM3Common.TryDrag(FgjM3Common.Ground(route[run.a]), FgjM3Common.Ground(route[run.b]))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, rk) < 600)
            {
                return StepOutcome.Wait;
            }
            c.SetInt(ri, k + 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickUpgradedAndCopyBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> route = P.LoadPath(c, "path.r_trunk");
            int min = FgjM3Common.MinTier(route, out int max, out int missing);
            bool pending = route.Any(p => HomeValleyConstruction.TryFindUpgradeCell(St, p, out _, out _));
            bool copyBuilt = P.BuildingReady(St, P.Furnace, P.P(c, "copy"), out BuildingRecord copy);
            ProductionService.Producer cp = copyBuilt ? P.Prod(copy) : null;
            if (!(min == 1 && max == 1 && missing == 0 && !pending) || cp == null)
            {
                return StepOutcome.Wait;
            }
            if (cp.Recipe == null || cp.Recipe.Id != "alloy_ore")
            {
                return StepOutcome.Fail($"复制的精炼炉建成了，配方却是 {cp.Recipe?.Id ?? "没选"}（布局应带着配方设置）");
            }
            if (c.StepElapsed < 3)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"成品干线 {route.Count} 格升到 T2；复制的精炼炉建成，配方“{cp.Recipe.Name}”（布局带着配方设置），状态“{ProductionService.StateText(cp)}：{ProductionService.ReasonText(St, cp)}”；废料 {St.Scrap}");
        }

        // ── 常驻规则：零件保持 50 个 ──────────────────────────────────────────────────

        private static StepOutcome TickRuleFromPreset(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            if (panel == null || !RulesPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("常驻规则面板没开");
            }
            if (!FgjM3Common.Done(c, "pick"))
            {
                DropdownField d = panel.NewPresetField;
                int idx = d?.choices.FindIndex(x => x.Contains("零件保底")) ?? -1;
                bool? ok = idx < 0 ? false : P.PickableInView(d);
                if (ok == null)
                {
                    return StepOutcome.Wait;
                }
                if (ok == false)
                {
                    return StepOutcome.Retry($"“从预设新建”下拉里没有“零件保底”或点不到（{JourneyInput.LastUiFailure}）");
                }
                c.SetInt("rules0", StandingRuleService.Ordered(St).Count);
                d.value = d.choices[idx]; // 与在弹出菜单里点那一项同一个值变化回调
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            StandingRuleRecord r = StandingRuleService.Find(St, panel.SelectedSerial);
            if (r == null || r.Kind != StandingRuleService.KindStock || r.ItemId != "part" || r.Threshold != 50 || r.Enabled)
            {
                return StepOutcome.Retry($"新建后选中的规则不对：{r?.Kind}/{r?.ItemId}/{r?.Threshold}/启用 {r?.Enabled}（“{panel.MessageText}”）");
            }
            c.SetInt("ruleSerial", r.Serial);
            return StepOutcome.Done($"新建规则 R{r.Serial}：库存维持，零件少于 {r.Threshold} 件时排产（先停用；“{panel.MessageText}”；编辑区“{panel.EditTitleText}”）");
        }

        private static StepOutcome TickRuleFactory(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            StandingRuleRecord r = StandingRuleService.Find(St, c.GetInt("ruleSerial"));
            BuildingRecord pws = P.Bld(c, "pwS");
            if (panel == null || r == null || pws == null)
            {
                return StepOutcome.Fail("规则 / 零件工坊不见了");
            }
            if (r.Targets.Length == 1 && r.Targets[0] == pws.BuildingId)
            {
                bool picked = c.GetLong(FgjM3Common.SK(c, "at")) > 0;
                return StepOutcome.Done(picked
                    ? $"工厂下拉选“{StandingRuleService.BuildingLabel(St, pws.BuildingId)}”：规则作用于做结构材的那座零件工坊（配方 {r.RecipeId}）"
                    : $"工厂下拉已是“{StandingRuleService.BuildingLabel(St, pws.BuildingId)}”（预设默认第一座零件工坊，正是做结构材的那座，不用改）：规则配方 {r.RecipeId}");
            }
            if (NowStepMs(c) < 600)
            {
                return StepOutcome.Wait;
            }
            DropdownField d = panel.FactoryField;
            string want = StandingRuleService.BuildingLabel(St, pws.BuildingId);
            // 下拉菜单把 / # % & 换成全角（DropdownChoices.Sanitize：半角是菜单路径与快捷键语法），按玩家看到的那一项找。
            string shown = want.Replace('/', '／').Replace('#', '＃').Replace('%', '％').Replace('&', '＆');
            int idx = d?.choices.FindIndex(x => x == want || x == shown) ?? -1;
            bool? clicked = idx < 0 ? false : P.PickableInView(d);
            if (clicked == null)
            {
                return StepOutcome.Wait;
            }
            if (clicked == false)
            {
                return StepOutcome.Retry($"工厂下拉里没有“{want}”或点不到（{string.Join("、", d?.choices ?? new List<string>())}；{JourneyInput.LastUiFailure}）");
            }
            c.SetLong(FgjM3Common.SK(c, "at"), FgjM3Common.NowMs());
            d.value = d.choices[idx];
            return StepOutcome.Wait;
        }

        private static long NowStepMs(JourneyContext c) => FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "at"));

        private static StepOutcome TickRuleEnable(JourneyContext c)
        {
            RulesPanelUIToolkit panel = RulesPanelUIToolkit.Instance;
            StandingRuleRecord r = StandingRuleService.Find(St, c.GetInt("ruleSerial"));
            if (panel == null || r == null)
            {
                return StepOutcome.Fail("规则不见了");
            }
            if (r.Enabled)
            {
                int row0 = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowSerial(i) == r.Serial);
                return StepOutcome.Done($"点“启用”：R{r.Serial} 已启用（行“{panel.RowText(row0, "RrState")}｜{panel.RowText(row0, "RrWhen")}｜{panel.RowText(row0, "RrThen")}”）");
            }
            // 选完工厂后面板当帧重建行：等它排好版（玩家也不会在同一帧里点），再点这一行的“启用”。
            if (c.StepElapsed < 0.6 || NowStepMs(c) < 600)
            {
                return StepOutcome.Wait;
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
            c.SetLong(FgjM3Common.SK(c, "at"), FgjM3Common.NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRuleFired(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int serial = c.GetInt("ruleSerial");
            BuildingRecord pws = P.Bld(c, "pwS");
            ProductionService.Producer p = P.Prod(pws);
            RuleHoldRecord h = StandingRuleService.FindHold(St, StandingRuleService.BuildingKey(pws.BuildingId));
            int stock = P.Stock("part");
            if (p?.Recipe == null || p.Recipe.Id != "part" || h == null || h.Rule != serial)
            {
                if (stock >= 50 && c.StepElapsed > 70)
                {
                    return StepOutcome.Fail($"零件已有 {stock} 件，规则一直没有接管（规则条件不满足，旅程要的是“少于 50 件时排产”）");
                }
                return StepOutcome.Wait;
            }
            RuleLogRecord on = StandingRuleService.LogEntries(St).LastOrDefault(l => l.Rule == serial && l.Key == "rules.log.stock_on");
            c.SetLong("ruleOnTick", GameClock.Ticks);
            c.SetInt("ruleOnStock", stock);
            return on != null
                ? StepOutcome.Done($"R{serial} 接管：零件 {stock} 件 < 50，零件工坊改为排产“{p.Recipe.Name}”（原来“{h.Prev}”）；触发日志“{StandingRuleService.LogText(St, on)}”")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickRuleLineOnPanel(JourneyContext c)
        {
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            if (panel == null || !ProductionPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("通用面板没开");
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string line = JourneyInput.FindUitk<Label>(P.PanelHost, "BpRuleLine")?.text ?? string.Empty;
            string tag = "R" + c.GetInt("ruleSerial").ToString(CultureInfo.InvariantCulture);
            return line.Contains(tag) && !GameText.ContainsMarker(line)
                ? StepOutcome.Done($"通用面板规则行“{line.Replace("\n", " ")}”；配方行“{panel.RecipeLineText.Split('\n').FirstOrDefault()}”")
                : StepOutcome.Fail($"通用面板没写是哪条规则改的（规则行“{line}”）");
        }

        private static StepOutcome TickRuleRestocked(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int serial = c.GetInt("ruleSerial");
            BuildingRecord pws = P.Bld(c, "pwS");
            ProductionService.Producer p = P.Prod(pws);
            RuleLogRecord off = StandingRuleService.LogEntries(St).LastOrDefault(l => l.Rule == serial && l.Key == "rules.log.stock_off");
            int stock = P.Stock("part");
            if (off == null || p?.Recipe == null || p.Recipe.Id != "structural")
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"零件 {stock} 件，零件工坊配方 {p?.Recipe?.Id}");
                }
                return StepOutcome.Wait;
            }
            double mins = (GameClock.Ticks - c.GetLong("ruleOnTick")) / (double)GameClock.StepHz / 60.0;
            return StepOutcome.Done($"零件补到 {stock} 件（接管时 {c.GetInt("ruleOnStock")} 件，{mins:F1} 游戏分钟）：R{serial} 把零件工坊恢复为“{p.Recipe.Name}”；日志“{StandingRuleService.LogText(St, off)}”");
        }
    }
}
