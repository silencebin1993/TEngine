using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG4-E2E-01：FGJ-M4 第 3～5 步——精炼塔酸液堵塞、接废液池恢复（FGT-ECO-006）；远征一次、回来看离家报告（FGT-ECO-007，报告与独立计数逐项核对、
    /// 离家期间产量与观察时速率一致）；清空所有机器、应急打印把家园救回来（FGT-ECO-005 / FGR-ECO-070）。
    /// </summary>
    public static partial class FgjM4Journey
    {
        private static IEnumerable<JourneyStep> LaterSteps() => new[]
        {
            // ── 第 3 步：精炼塔的酸液堵住，接上废液池恢复 ──
            S("ref_plan", "看地形：离核心最近的油井；规划精炼塔（南口进原油、北口出燃油接储罐、东口的酸液先不接）与之后接废液池的位置", 30, null, TickRefineryPlan),
            S("ref_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("ref_place", "放精炼塔、燃油管线与储罐、油井上的泵、原油管线（建造栏点分类与条目、指着格子单击、按住左键分段拖）", 300, null, c => P.TickActions(c, "ref", 40), retries: 2),
            S("ref_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("ref_built", "机器施工：精炼塔、泵、管线、储罐建成、接上电，精炼塔开始炼燃油", 600, null, TickRefineryBuilt),
            S("acid_jam", "酸液没有去处：精炼塔输出堵塞，原因写“副产品酸液无处可去”", 300, null, TickAcidJam),
            S("tower_open", "建造模式里点精炼塔：通用面板写明原因与办法", 30, null, c => P.TickOpenPanel(c, P.Tower, P.P(c, "ref.tower")), retries: 2),
            S("tower_read", "通用面板的状态与原因：输出堵塞——副产品酸液无处可去（并写明在哪一格接管线）", 10, null, TickTowerReason),
            S("tower_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
            S("pond_place", "在精炼塔东边放废液池、一格管线把酸液口与废液池连起来", 120, null, c => P.TickActions(c, "pond", 40), retries: 2),
            S("pond_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("pond_ok", "废液池建成、接上电：酸液流进废液池销毁，精炼塔恢复工作，燃油进储罐", 600, null, TickPondRecovered),

            // ── 第 4 步：远征一次，回来看离家报告 ──
            // 远征前的准备（与 FGJ-M1～M3 同一条件）：破碎都市要“信号塔可用 + 家园有一台 ERC-003”才解锁——信号塔开局就修好了（repair_tw），
            // 这里在装配站造一台 ERC-003。
            S("e3_pan", "方向键平移镜头，让装配站进画面", 20, null, c => TickBuildingOnScreen(c, HomeValleyLayout.BuildingTypeAssemblyStation)),
            S("e3_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                FgjM1Journey.TickFactoryOpen, retries: 1),
            S("e3_sub", "打开“缺材料时用废料代付”（这台不必等产线：仓库里有的产线材料先用，缺的用废料补）", 10, null, c => TickSubstitute(c, true), retries: 1),
            S("e3_produce", "点“生产 ERC-003”：入队", 10, c => { c.SetInt("verE3", FgjM1Journey.Erc003Record()?.ActiveVersion ?? 1); FgjM2Common.ClickProduce(c); },
                c => FgjM2Common.TickProduceQueued(c, "verE3"), retries: 1),
            S("e3_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                FgjM1Journey.TickFactoryClosed, retries: 1),
            S("exp_ready", "等 ERC-003 出厂：破碎都市解锁（信号塔开局已修好）", 400, null, TickExpeditionReady),
            S("e3_sel", "左键点新出厂的 ERC-003", 20, c => FgjM1Journey.ClickMachine(c, "e3"), c => FgjM1Journey.TickSelected(c, "e3"), retries: 4),
            S("e3_out", "右键点旁边的空地：ERC-003 驶出装配站出口（右键地面 = 移动）", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "e3"), retries: 3),
            S("prep_open", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                FgjM1Journey.TickPrepOpen, retries: 2),
            S("prep_pick", "勾选出征名单（三台）", 20, null, FgjM3Common.TickRosterAny),
            S("prep_depart", "点“出发”：镜头飞到破碎都市，家园没人看着（记下离家时的各项累计）", 30, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), TickDeparted, retries: 1),
            S("away", "在破碎都市待 90 游戏秒以上，这期间家园一直没人观察", 160, null, TickAway),
            S("uplink", "机器列表点一台远征机器：接入", 25, c => FgjM1Journey.ClickMachineList(c, c.GetInt("other")), TickUplinked, retries: 3),
            S("evac_hold", "开到撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3),
            S("evac_confirm", "点“确认撤离”：远征队返回家园", 30, c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"), FgjM1Journey.TickReturnedHome, retries: 1),
            S("report", "离家报告回家时自动打开过这次的报告；按离家报告键（默认 Alt+H）再打开：离家期间各物品产量与提取钻 / 精炼炉的完成次数逐项对得上", 30, null, TickAwayReport),
            S("report_click", "点报告里一条可定位的条目：面板收起、镜头飞过去（或打开对应面板）", 20, null, TickReportClick, retries: 1),
            S("report_close", "收起点开的面板", 15, null, TickCloseOpened, retries: 1),
            S("bg_check", "后台一致：离家期间金属提取钻出矿速率 = 观察时实测速率（家园 0 帧被观察）", 20, null, TickBackgroundConsistent),

            // ── 第 5 步：清空所有机器，看应急打印把家园救回来 ──
            S("wipe_pause", "按暂停键（默认空格）：战略暂停", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Done("战略暂停（世界不走）") : StepOutcome.Retry("没暂停"), retries: 1),
            S("wipe_open", "按建造键打开建造模式（暂停中照样能规划）", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("wipe_ghost", "放一座 T2 电塔的虚影（暂停中不施工，留给之后的机器建）", 60, null, TickWipeGhost, retries: 2),
            S("wipe_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("wipe", "伤害夹具：家园与远征回来的机器全部被击毁（家园还没有正式突袭，DEBT-FG4E2E01-01；伤害走生产代码的伤害入口）", 15, ApplyWipeFixture, TickWiped),
            S("wipe_resume", "按暂停键继续", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done("继续运行") : StepOutcome.Retry("还暂停着"), retries: 1),
            S("print", "归还核心应急打印一台搬运机（今天还没打印过、能施工的机器少于 2 台），发可定位通知", 60, null, TickEmergencyPrint),
            S("rescued", "家园救回来：打印出的搬运机把电塔虚影建成；产线在整个过程中照常运转", 300, null, TickRescued),
        };

        // ── 远征前的准备 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickBuildingOnScreen(JourneyContext c, string typeId)
        {
            BuildingRecord b = FgjM1Journey.Building(typeId);
            if (b == null)
            {
                return StepOutcome.Fail($"家园里没有 {typeId}");
            }
            return JourneyCommon.PanToward(b.Position) ? StepOutcome.Done($"{HomeGridService.DisplayName(typeId)}在画面里（{b.Position.x:F0}, {b.Position.y:F0}）") : StepOutcome.Wait;
        }

        private static StepOutcome TickExpeditionReady(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord tower = FgjM1Journey.Building(HomeValleyLayout.BuildingTypeSignalTower);
            bool towerOk = tower != null && tower.ConstructionState == BuildingConstructionState.Operational;
            bool erc3 = MachineRegistry.AllRecords.Any(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            ExpeditionDepartureService.ExpeditionTarget target = ExpeditionDepartureService.ResolveTarget(St);
            if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                FactoryQueueItemRecord item = St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
                BuildingRecord asm = AssemblyMaterials.Station(St);
                c.Log($"信号塔 {tower?.ConstructionState}、ERC-003 出厂 {erc3}（队列 {item?.State}，进度 {item?.Progress:F2}，{(item != null ? HomeValleyFactory.DescribeWait(item) ?? item.BlockedReason : "-")}；装配站 {asm?.PowerState}）；废料 {St.Scrap}；{P.PowerLine(St)}");
            }
            if (towerOk && erc3 && target == ExpeditionDepartureService.ExpeditionTarget.SilentRuins)
            {
                MachineRecord e3 = MachineRegistry.AllRecords.Where(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId == HomeValleyLayout.Erc003ChassisId)
                    .OrderByDescending(m => m.LogicId).First();
                c.SetInt("e3", e3.LogicId);
                return StepOutcome.Done($"{FgjM3Common.Label(e3.LogicId)}（ERC-003）出厂，停在装配站出口（占着出口 = {e3.IsInFactory}）：破碎都市解锁，可以远征");
            }
            if (tower != null && !towerOk && FgjM1Journey.RepairOrder(HomeValleyLayout.BuildingTypeSignalTower) == null && c.StepElapsed > 5)
            {
                return StepOutcome.Fail($"信号塔还没修好，修复工单却没了（废料 {St.Scrap}）");
            }
            return StepOutcome.Wait;
        }

        // ── 精炼塔 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickRefineryPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.3)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            var used = new HashSet<long>();
            foreach (string k in c.Get("plan.used", string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                used.Add(long.Parse(k, CultureInfo.InvariantCulture));
            }
            GridCell copy = P.P(c, "copy");
            foreach (GridCell cell in P.Footprint(P.Furnace, copy, 0))
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        used.Add(P.Key(new GridCell(cell.X + dx, cell.Y + dy)));
                    }
                }
            }
            FgjM4Common.RefineryPlan rp = P.PlanRefinery(s, used, out string why);
            if (rp == null)
            {
                return StepOutcome.Fail("精炼塔规划不出：" + why);
            }
            P.SetP(c, "ref.tower", rp.Tower);
            P.SetP(c, "ref.pond", rp.Pond);
            P.SetP(c, "ref.pump", rp.Pump);
            P.SetP(c, "ref.fuel", rp.FuelPipe);
            P.SetP(c, "ref.tank", rp.Tank);
            P.SetP(c, "ref.acid", rp.AcidPipe);
            P.SavePath(c, "path.crude", rp.Crude);
            P.SavePath(c, "path.fuel", new List<GridCell> { rp.FuelPipe });
            P.SavePath(c, "path.acid", new List<GridCell> { rp.AcidPipe });
            c.Set("ref.poles", FgjM3Common.EncodeCells(rp.Poles));
            var acts = new List<string>();
            foreach (GridCell pole in rp.Poles)
            {
                acts.Add($"B|{P.PoleT2}|{pole.X}|{pole.Y}|0|T2 电塔 {P.Cell(pole)}");
            }
            acts.Add($"B|{P.Tower}|{rp.Tower.X}|{rp.Tower.Y}|0|精炼塔 {P.Cell(rp.Tower)}");
            acts.Add(P.ActQ(P.PipeT1, "path.fuel", "燃油口外一格管线"));
            acts.Add(P.ActC(P.TankTool, rp.Tank, "储罐（接燃油）"));
            acts.Add(P.ActC(P.PumpTool, rp.Pump, "油井上的泵"));
            acts.Add(P.ActQ(P.PipeT1, "path.crude", "原油管线 → 精炼塔南口"));
            P.SetActions(c, "ref", acts);
            P.SetActions(c, "pond", new[]
            {
                $"B|{P.Pond}|{rp.Pond.X}|{rp.Pond.Y}|0|废液池 {P.Cell(rp.Pond)}",
                P.ActQ(P.PipeT1, "path.acid", "酸液口与废液池之间一格管线"),
            });
            GridCell core = HomeGridService.CorePivot(s);
            return StepOutcome.Done($"油井 {P.Cell(rp.Pump)}（离核心 {Vector2.Distance(FgjM3Common.Ground(rp.Pump), FgjM3Common.Ground(core)):F1} 格）；精炼塔 {P.Cell(rp.Tower)}、" +
                                    $"原油管线 {rp.Crude.Count} 格、燃油接储罐 {P.Cell(rp.Tank)}、酸液口外 {P.Cell(rp.AcidPipe)} 先空着（之后废液池 {P.Cell(rp.Pond)}）；T2 电塔 {rp.Poles.Count} 座");
        }

        private static StepOutcome TickRefineryBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var missing = new List<string>();
            if (!P.BuildingReady(s, P.Tower, P.P(c, "ref.tower"), out BuildingRecord tower))
            {
                missing.Add("精炼塔");
            }
            foreach (GridCell pole in FgjM3Common.DecodeCells(c.Get("ref.poles")))
            {
                if (!P.BuildingReady(s, P.PoleT2, pole, out _))
                {
                    missing.Add("电塔" + P.Cell(pole));
                }
            }
            var pipes = P.LoadPath(c, "path.crude").Concat(P.LoadPath(c, "path.fuel")).Concat(new[] { P.P(c, "ref.tank"), P.P(c, "ref.pump") }).ToList();
            int unbuilt = pipes.Count(p => !P.RouteBuilt(s, new List<GridCell> { p }, false));
            if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                c.Log($"施工中：还没建成 [{string.Join(",", missing)}]、管线还差 {unbuilt} 格；废料 {s.Scrap}；{P.PowerLine(s)}");
            }
            if (missing.Count > 0 || unbuilt > 0 || tower.PowerState != BuildingPowerState.Powered)
            {
                return StepOutcome.Wait;
            }
            long pumped = FgjM3Common.PumpTotalMl(P.P(c, "ref.pump"));
            return pumped > 0 && P.Completed(tower) >= 1
                ? StepOutcome.Done($"精炼塔与管线建成、接上电：泵已抽原油 {pumped / 1000.0:F1} 升，精炼塔完成 {P.Completed(tower)} 次；储罐燃油 {FgjM3Common.TankMl(P.P(c, "ref.tank")) / 1000.0:F1} 升")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickAcidJam(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord tower = P.BuildingAtPivot(St, P.Tower, P.P(c, "ref.tower"));
            ProductionService.Producer p = P.Prod(tower);
            string reason = p != null ? ProductionService.ReasonText(St, p) : string.Empty;
            if (p == null || p.State != ProdState.OutputBlocked || !reason.Contains("酸液"))
            {
                return StepOutcome.Wait;
            }
            c.SetLong("jamTowerDone", p.Rec.Completed);
            return StepOutcome.Done($"精炼塔完成 {p.Rec.Completed} 次后停下：“{ProductionService.StateText(p)}：{reason.Replace("\n", " ")}”");
        }

        private static StepOutcome TickTowerReason(JourneyContext c)
        {
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            if (panel == null || !ProductionPanelUIToolkit.IsOpen || c.StepElapsed < 0.6)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail("精炼塔的通用面板没开");
            }
            string state = panel.StateText;
            string reason = panel.ReasonText;
            GridCell acid = P.P(c, "ref.acid");
            bool ok = reason.Contains("酸液") && reason.Contains("无处可去") && reason.Contains($"（{acid.X}, {acid.Y}）") && !Localization.GameText.ContainsMarker(state + reason);
            return ok
                ? StepOutcome.Done($"通用面板：“{state}”“{reason.Replace("\n", " / ")}”（写明在 {P.Cell(acid)} 铺一格管线）")
                : StepOutcome.Fail($"通用面板没写明酸液无处可去与办法：“{state}”“{reason}”");
        }

        private static StepOutcome TickPondRecovered(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!P.BuildingReady(s, P.Pond, P.P(c, "ref.pond"), out BuildingRecord pond) || pond.PowerState != BuildingPowerState.Powered
                || !P.RouteBuilt(s, P.LoadPath(c, "path.acid"), false))
            {
                return StepOutcome.Wait;
            }
            BuildingRecord tower = P.BuildingAtPivot(s, P.Tower, P.P(c, "ref.tower"));
            ProductionService.Producer tp = P.Prod(tower);
            if (!FgjM3Common.Done(c, "t0"))
            {
                c.SetLong(FgjM3Common.SK(c, "tower0"), tp.Rec.Completed);
                c.SetLong(FgjM3Common.SK(c, "tank0"), FgjM3Common.TankMl(P.P(c, "ref.tank")));
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            long more = tp.Rec.Completed - c.GetLong(FgjM3Common.SK(c, "tower0"));
            long fuel = FgjM3Common.TankMl(P.P(c, "ref.tank")) - c.GetLong(FgjM3Common.SK(c, "tank0"));
            ProductionService.Producer pp = P.Prod(pond);
            string pondLine = pp != null ? ProductionService.StateText(pp) + "：" + ProductionService.ReasonText(s, pp) : string.Empty;
            return more >= 2 && fuel > 0
                ? StepOutcome.Done($"废液池建成接电：精炼塔又完成 {more} 次（堵住时停在 {c.GetLong("jamTowerDone")} 次）、储罐燃油 +{fuel / 1000.0:F1} 升；废液池“{pondLine.Replace("\n", " ")}”；精炼塔“{ProductionService.StateText(tp)}”")
                : StepOutcome.Wait;
        }

        // ── 远征 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickDeparted(JourneyContext c)
        {
            StepOutcome o = FgjM1Journey.TickDeparted(c);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            List<int> roster = FgjM1Journey.Roster(c);
            c.SetInt("other", roster[0]);
            c.SetLong("away.tick", GameClock.Ticks);
            foreach (string k in new[] { "drillM", "drillR", "furnace", "pwS", "pwP", "eb", "cw" })
            {
                c.SetLong("away." + k, P.Completed(P.Bld(c, k)));
            }
            c.SetLong("away.tower", P.Completed(P.BuildingAtPivot(St, P.Tower, P.P(c, "ref.tower"))));
            c.SetInt("away.homeFrames", 0);
            c.SetInt("away.frames", 0);
            c.SetInt("away.reports0", AwayReportService.Recent(St).Count);
            c.SetInt("away.auto0", AwayReportPanelUIToolkit.Instance?.AutoOpenCount ?? 0);
            return StepOutcome.Done(o.Message + $"；离家时：金属提取钻累计 {c.GetLong("away.drillM")} 次、精炼炉 {c.GetLong("away.furnace")} 次、精炼塔 {c.GetLong("away.tower")} 次（第 {c.GetLong("away.tick")} 步）");
        }

        private static void CountObserved(JourneyContext c)
        {
            int f = Time.frameCount;
            if (c.GetInt("away.lastFrame") == f)
            {
                return;
            }
            c.SetInt("away.lastFrame", f);
            c.SetInt("away.frames", c.GetInt("away.frames") + 1);
            if (WorldView.ObservedSiteId == HomeValleyLayout.RegionId)
            {
                c.SetInt("away.homeFrames", c.GetInt("away.homeFrames") + 1);
            }
        }

        private static StepOutcome TickAway(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CountObserved(c);
            double dt = (GameClock.Ticks - c.GetLong("away.tick")) / (double)GameClock.StepHz;
            if (dt < 90.0)
            {
                return StepOutcome.Wait;
            }
            return c.GetInt("away.homeFrames") == 0
                ? StepOutcome.Done($"在破碎都市 {dt:F0} 游戏秒（{c.GetInt("away.frames")} 帧，镜头一直在破碎都市，家园没人观察）")
                : StepOutcome.Fail($"远征期间有 {c.GetInt("away.homeFrames")} 帧在看家园（这一段要证明“不观察”）");
        }

        private static StepOutcome TickUplinked(JourneyContext c)
        {
            CountObserved(c);
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {FgjM3Common.Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {FgjM3Common.Label(b)}（准备开到撤离点）");
        }

        private static long ProducedIn(AwayReportRecord r, string itemId) =>
            (r.Produced ?? Array.Empty<ItemAmountRecord>()).Where(x => x != null && x.ItemId == itemId).Sum(x => x.Amount);

        private static StepOutcome TickAwayReport(JourneyContext c)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            AwayReportPanelUIToolkit panel = AwayReportPanelUIToolkit.Instance;
            AwayReportRecord latest = AwayReportService.Recent(St).FirstOrDefault();
            // 回家那一步（FgjM1Journey.TickReturnedHome）看到报告自动打开后点了关闭：这里先确认“自动打开过、打开的就是这次的报告”，
            // 再按离家报告键（默认 Alt+H）重新打开来逐项核对（玩家回头再看报告的做法）。
            bool auto = panel != null && latest != null && AwayReportService.Recent(St).Count > c.GetInt("away.reports0")
                        && panel.AutoOpenCount > c.GetInt("away.auto0") && panel.ShownSerial == latest.Serial;
            if (!auto)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait
                    : StepOutcome.Fail($"回家后离家报告没有自动打开这次的报告（报告 {AwayReportService.Recent(St).Count} 份，原 {c.GetInt("away.reports0")} 份；自动打开 {panel?.AutoOpenCount} 次，原 {c.GetInt("away.auto0")} 次）");
            }
            if (!AwayReportPanelUIToolkit.IsOpen)
            {
                if (!FgjM3Common.Once(c, "reopen", () => { JourneyInput.PressAction(GameActionId.OpenAwayReport); return true; }))
                {
                    return StepOutcome.Wait;
                }
                return FgjM3Common.SinceMs(c, "reopen") < 5000 ? StepOutcome.Wait : StepOutcome.Fail("按离家报告键（默认 Alt+H）后离家报告没有打开");
            }
            panel.Refresh();
            long t0 = c.GetLong("away.tick");
            double dt = (latest.EndTick - latest.StartTick) / (double)GameClock.StepHz;
            // 独立计数：各建筑在报告窗口里的完成次数之差（从离家那一刻记下的累计算起；报告从出发事务那一刻开到撤离事务结束）。
            long dDrill = P.Completed(P.Bld(c, "drillM")) - c.GetLong("away.drillM");
            long dFurn = P.Completed(P.Bld(c, "furnace")) - c.GetLong("away.furnace");
            long ore = ProducedIn(latest, "metal_ore");
            long alloy = ProducedIn(latest, "alloy");
            // 报告窗口与“离家时记下的累计”起点可能差几步（出发事务与这一步记录之间），允许每项差一个周期。
            bool oreOk = Math.Abs(ore - dDrill) <= 1;
            bool alloyOk = Math.Abs(alloy - dFurn) <= 1;
            bool shown = panel.ShownSerial == latest.Serial && panel.VisibleRowCount > 0 && !Localization.GameText.ContainsMarker(panel.SummaryText);
            var rows = Enumerable.Range(0, Math.Min(panel.VisibleRowCount, 12)).Select(i => panel.RowText(i).Replace("\n", " ")).ToList();
            c.SetLong("rep.ore", ore);
            c.SetLong("rep.start", latest.StartTick);
            c.SetLong("rep.end", latest.EndTick);
            return shown && oreOk && alloyOk && ore > 0
                ? StepOutcome.Done($"离家报告第 {latest.Serial} 份回家时自动打开过，按 Alt+H 再打开（“{panel.SummaryText}”，{panel.VisibleRowCount} 行，离家 {dt:F0} 游戏秒）：报告里金属矿 +{ore}、合金 +{alloy}，" +
                                   $"与提取钻 / 精炼炉在这段时间的完成次数（{dDrill} / {dFurn}）一致；前几行 [{string.Join(" | ", rows)}]")
                : StepOutcome.Fail($"离家报告与实际不一致：显示 {shown}、金属矿 {ore} vs 提取钻 {dDrill}、合金 {alloy} vs 精炼炉 {dFurn}；[{string.Join(" | ", rows)}]");
        }

        private static StepOutcome TickReportClick(JourneyContext c)
        {
            AwayReportPanelUIToolkit panel = AwayReportPanelUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "click"))
            {
                int row = -1;
                for (int i = 0; panel != null && i < panel.VisibleRowCount && row < 0; i++)
                {
                    AwayLine l = panel.Line(i);
                    if (l != null && l.IsEntry && (l.Action == AwayLineAction.Building || l.Action == AwayLineAction.Locate || l.Action == AwayLineAction.StatsItem))
                    {
                        row = i;
                    }
                }
                if (row < 0)
                {
                    return StepOutcome.Fail("离家报告里没有可定位 / 可打开的条目");
                }
                c.Set("repRow", panel.RowText(row).Replace("\n", " "));
                c.Set("repAction", panel.Line(row).Action.ToString());
                bool? clicked = P.ClickInView(panel.RowButton(row));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return StepOutcome.Retry("点不到那一条：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "click");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            return !AwayReportPanelUIToolkit.IsOpen && panel.LastClicked != null
                ? StepOutcome.Done($"点“{c.Get("repRow")}”：离家报告收起，{c.Get("repAction")}（统计面板开着 {StatsPanelUIToolkit.IsOpen}、建筑面板开着 {ProductionPanelUIToolkit.IsOpen}）")
                : StepOutcome.Retry($"点了之后离家报告还开着 {AwayReportPanelUIToolkit.IsOpen}");
        }

        private static StepOutcome TickCloseOpened(JourneyContext c)
        {
            if (StatsPanelUIToolkit.IsOpen)
            {
                if (!FgjM3Common.Once(c, "stats", () => JourneyInput.ClickUitk("[StatsPanelHost]", "StatsPanelClose")))
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            if (ProductionPanelUIToolkit.IsOpen)
            {
                if (!FgjM3Common.Once(c, "prod", () => JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose")))
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            if (FgjM3Common.BuildOpen)
            {
                if (!FgjM3Common.Once(c, "build", () => { FgjM3Common.PressBuild(false); return true; }))
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done("点开的面板都收起来了");
        }

        private static StepOutcome TickBackgroundConsistent(JourneyContext c)
        {
            double observed = double.Parse(c.Get("rateOre", "0"), CultureInfo.InvariantCulture);
            long start = c.GetLong("rep.start");
            long end = c.GetLong("rep.end");
            double dt = (end - start) / (double)GameClock.StepHz;
            // 只算离家报告的窗口（出发事务到撤离事务，家园一帧都没被看）：窗口里的出矿数来自报告，报告又已与提取钻完成次数逐项核对过（report 步）。
            double awayRate = c.GetLong("rep.ore") / Math.Max(1.0, dt) * 60.0;
            bool ok = observed > 0 && Math.Abs(awayRate - observed) <= Math.Max(1.5, observed * 0.05) && c.GetInt("away.homeFrames") == 0;
            return ok
                ? StepOutcome.Done($"离家期间（报告窗口 {dt:F0} 游戏秒，{c.GetInt("away.frames")} 帧里看家园 {c.GetInt("away.homeFrames")} 帧）金属提取钻出矿 {awayRate:F1} 份 / 分钟，观察时实测 {observed:F1}：后台与观察一致")
                : StepOutcome.Fail($"后台产量与观察时不一致：离家 {awayRate:F1} 份 / 分钟、观察时 {observed:F1}、看家园 {c.GetInt("away.homeFrames")} 帧");
        }

        // ── 清空所有机器 → 应急打印 ──────────────────────────────────────────────────

        private static StepOutcome TickWipeGhost(JourneyContext c)
        {
            CampaignState s = St;
            if (string.IsNullOrEmpty(c.Get("wipe.set")))
            {
                GridCell core = HomeGridService.CorePivot(s);
                HashSet<long> avoid = P.BaseAvoid(s);
                GridCell? at = null;
                for (int r = 6; r <= 20 && at == null; r++)
                {
                    for (int dy = -r; dy <= r && at == null; dy++)
                    {
                        for (int dx = -r; dx <= r && at == null; dx++)
                        {
                            var g = new GridCell(core.X + dx, core.Y + dy);
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && !avoid.Contains(P.Key(g)) && !P.BeltPlannedOrBuilt(s, g) && !P.PipePlannedOrBuilt(s, g)
                                && HomeGridService.ValidatePlacement(s, P.PoleT2, g, 0, checkCost: false).Ok && FgjM3Common.Clear(FgjM3Common.Ground(g), out _))
                            {
                                at = g;
                            }
                        }
                    }
                }
                if (at == null)
                {
                    return StepOutcome.Fail("核心附近画面里找不到能放电塔的空地");
                }
                P.SetP(c, "wipe.ghost", at.Value);
                P.SetActions(c, "wipeg", new[] { $"B|{P.PoleT2}|{at.Value.X}|{at.Value.Y}|0|T2 电塔虚影 {P.Cell(at.Value)}" });
                c.Set("wipe.set", "1");
            }
            StepOutcome o = P.TickActions(c, "wipeg", 30);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            BuildingRecord b = P.BuildingAtPivot(s, P.PoleT2, P.P(c, "wipe.ghost"));
            return b != null && HomeValleyController.IsPlannedGhost(b) && GameClock.Paused
                ? StepOutcome.Done($"暂停中放下 T2 电塔虚影 {P.Cell(P.P(c, "wipe.ghost"))}（不施工）")
                : StepOutcome.Fail($"电塔虚影不对：在 {b != null}、虚影 {b != null && HomeValleyController.IsPlannedGhost(b)}、暂停 {GameClock.Paused}");
        }

        /// <summary>
        /// 伤害夹具（DEBT-FG4E2E01-01，沿用 DEBT-FG1E2E01-04 的写法）：家园还没有正式突袭（FG6-DEF-04），机器全灭只能用夹具造出来——
        /// 对全部活着的己方机器施加致死伤害，走生产代码的伤害入口 <see cref="MachineRegistry.ApplyDamage"/>（与战斗结算同一入口）。之后的应急打印、施工全是正式流程。
        /// </summary>
        private static void ApplyWipeFixture(JourneyContext c)
        {
            CampaignState s = St;
            c.SetInt("wipe.prints0", SoftlockService.StateOf(s)?.PrintCount ?? 0);
            c.SetLong("wipe.drill0", P.Completed(P.Bld(c, "drillM")));
            c.SetLong("wipe.tick0", GameClock.Ticks);
            c.SetInt("wipe.notes0", NotificationCenter.History.Count);
            var ids = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && (string.IsNullOrEmpty(m.FactionId) || m.FactionId == "Player")).Select(m => m.LogicId).ToList();
            c.Set("wipe.ids", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            foreach (int id in ids)
            {
                MachineRegistry.ApplyDamage(id, 999999f);
            }
        }

        private static StepOutcome TickWiped(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            int alive = SoftlockService.CountHomeMachines();
            int any = MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && (string.IsNullOrEmpty(m.FactionId) || m.FactionId == "Player"));
            return alive == 0 && any == 0
                ? StepOutcome.Done($"{c.Get("wipe.ids").Split(',').Length} 台机器全部被击毁：能施工的机器 0 台（暂停中，应急打印要等世界走起来）")
                : StepOutcome.Fail($"还有 {any} 台活着（能施工 {alive}）");
        }

        private static StepOutcome TickEmergencyPrint(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            SoftlockState st = SoftlockService.StateOf(St);
            if (st == null || st.PrintCount <= c.GetInt("wipe.prints0") || !MachineRegistry.TryGetRecord(st.LastPrintLogicId, out MachineRecord m) || !m.IsAlive)
            {
                return StepOutcome.Wait;
            }
            NotificationEntry note = NotificationCenter.History.Skip(c.GetInt("wipe.notes0")).LastOrDefault(e => e.Type?.Id == "emergency_rescue");
            if (note == null)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail("应急打印了，但没有通知");
            }
            c.SetInt("printed", m.LogicId);
            double secs = (GameClock.Ticks - c.GetLong("wipe.tick0")) / (double)GameClock.StepHz;
            return m.ChassisId == HomeValleyLayout.Erc002ChassisId
                ? StepOutcome.Done($"世界走起来 {secs:F1} 游戏秒后归还核心应急打印 {FgjM3Common.Label(m.LogicId)}（搬运机，第 {st.PrintCount} 次，游戏日 {st.LastPrintDay}）；通知“{note.Text}”")
                : StepOutcome.Fail($"应急打印的不是搬运机：{m.ChassisId}");
        }

        private static StepOutcome TickRescued(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!P.BuildingReady(St, P.PoleT2, P.P(c, "wipe.ghost"), out _))
            {
                return StepOutcome.Wait;
            }
            long drill = P.Completed(P.Bld(c, "drillM")) - c.GetLong("wipe.drill0");
            return drill > 0
                ? StepOutcome.Done($"打印出的 {FgjM3Common.Label(c.GetInt("printed"))} 把电塔虚影建成（家园重新有劳动力）；机器全灭期间产线照常运转（金属提取钻又出了 {drill} 份矿）")
                : StepOutcome.Fail("电塔建成了，产线却停了");
        }
    }
}
