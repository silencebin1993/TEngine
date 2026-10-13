using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.WorldSim;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using E = GameLogic.EditorTools.JourneyBots.FgjM7Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG7-E2E-01：M7 出口旅程 FGJ-M7（里程碑文档 FG-M7；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度）、全程正式输入（ADR-QA-017 三条通道），
    /// RTS 口径：左键选择、右键地面 = 移动 / 右键工作目标（遗迹点）= 派工，按钮只切状态 / 发起动作；等待段用正式 3 倍速键。
    ///
    /// 里程碑原文：看日历，为第一次静默夜做准备（储能、规则）→ 静默夜期间确认规则表里每一项 → 撑过去，捡回风暴残骸 → 酸雨中把关键产线盖上顶棚 →
    /// 建净化塔，让归还面积达到第一个里程碑，拿到奖励。
    ///
    /// 轻量口径（用户 2026-10-12）：一条正向旅程走通核心循环、每个新核心系统至少被真实输入触发一次并断言；不为凑剧情改数值。只有两处“日程”测试捷径（只改日期，效果全走正式推进，见 <see cref="ApplyScheduleFixture"/>）：
    /// 第一次静默夜从第 5 日夜挪到准备就绪后的那个夜里（正式间隔下 3 倍速也要等约 31 分钟，超过旅程 30 分钟上限）、酸雨排到静默夜前后（第一段恶劣天气最早第 3 日、种类随机）。
    /// 归还面积第一个里程碑（5,000 格）在 T1 净化塔下要很多座塔和多个游戏日，旅程只证明“净化 → 归还面积真实增长”，并按实测速度写出可达性估算；里程碑奖励由 FgPurificationSelfCheck M1 断言，可达性交真人试玩（DEBT-FG7E2E01-02）。
    /// </summary>
    public static partial class FgjM7Journey
    {
        public const string Id = "FGJ-M7";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private static CampaignState St => CampaignSession.Current;

        private static readonly string[] BatchA = { "recycler", "lab1", "lab2", "blender", "purifier" };

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),

                // ── 1. 看日历（正式排定：第一次静默夜在第 5 日夜里）──
                S("cal_open", "按日历键（默认 Alt+L）打开日历面板", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, true), c => TickCalendar(c, false), retries: 1),
                S("cal_close", "再按日历键关闭日历面板", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenCalendar, () => CalendarPanelUIToolkit.IsOpen, false), TickCalendarClosed, retries: 1),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库、发电机、信号塔，拆两处开局残骸拿废料（与 FGJ-M5 / M6 同一套正式输入）──
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep1", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("sel_a2", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_tw", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("sel_b2", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage", "右键点开局残骸（情境命令：拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完（废料 +60）", 180, null, FgjM2Common.TickSalvageDone),
                S("wait_rep2", "等信号塔修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeSignalTower)),
                S("salv2_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸（情境命令：拆解）", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完（废料约 +60）", 180, null, FgjM3Common.TickWreck2Done),

                // ── 2. 规划与第一批建筑：回收站、两座仿真实验室、调配站 + 净化塔 + 泵 + 两条管线（冷却液线）、需要时发电机 2 ──
                S("plan", "看地形：回收站压在废墟上、两座仿真实验室；离核心最近的水源上放泵，调配站（水 + 稀土矿 → 冷却液）与净化塔（半径内污染格最多）都在核心配电范围里", 30, null, TickPlan),
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m7_place1", "放发电机 2（要时）、回收站与它到仓库输入口的带、两座仿真实验室、调配站、净化塔（建造栏“环境”分类）、水源上的泵、泵 → 调配站南口的水管、调配站东口 → 净化塔南口的冷却液管", 360, null,
                    c => P.TickActions(c, "m7a", 40), retries: 2),
                S("build_close1", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("rt_open", "按研发树键（默认 K）打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("rt_queue", "依次点分支筛选、左键点节点排进研究队列：能源 · 电塔 T2、环境 · 顶棚、能源 · 燃油发电机、能源 · 储能站", 90, null,
                    c => D.TickQueueAll(c, new[] { E.NodePole, E.NodeRoof, E.NodeFuelGen, E.NodeStorage }), retries: 2),
                S("rt_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),
                S("m7_built1", "机器取料施工：回收站、两座实验室、调配站、净化塔、泵与两条管线建成接电", 1500, null, TickBuiltFirst),

                // ── 3. 稀土：矿脉在迷雾里就先派机器走过去（右键地面 = 移动；机器走到的地方被探索），再放提取钻 + 传送带到调配站 ──
                S("rare_find", "找离调配站输入口最近的稀土矿脉：已探索就直接规划，在迷雾里就要先探路", 10, null, TickRareFind),
                S("ex_sel", "（矿脉在迷雾里时）左键点一台机器", 20, c => { if (Need(c, "ex.need")) FgjM1Journey.ClickMachine(c, "workerA"); },
                    c => Need(c, "ex.need") ? FgjM1Journey.TickSelected(c, "workerA") : StepOutcome.Done("矿脉已探索，跳过"), retries: 4),
                S("ex_move", "（矿脉在迷雾里时）右键点矿脉那边的地面：机器接到移动命令走进迷雾", 30, c => { if (Need(c, "ex.need")) RightClickGround(c, "ex.target"); },
                    c => Need(c, "ex.need") ? TickMoveOrdered(c, "workerA", "ex.target") : StepOutcome.Done("跳过"), retries: 3),
                S("ex_done", "（矿脉在迷雾里时）机器走过去，路上与矿脉一带被探索（迷雾揭开）", 400, null, TickExplored),
                S("drill_plan", "规划稀土提取钻（至少一格压在矿脉上）与传送带到调配站西侧输入口；不在配电范围里时串 T2 电塔", 30, null, TickDrillPlan),
                S("wait_pole", "（要电塔时）等研究：能源 · 电塔 T2", 900, null, c => c.GetInt("m7.poleN") > 0 ? D.TickResearched(c, E.NodePole) : StepOutcome.Done("不用电塔，跳过")),
                S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m7_place2", "放 T2 电塔（要时）、稀土提取钻、钻 → 调配站的传送带（按住左键拖）", 300, null, c => P.TickActions(c, "m7d", 40), retries: 2),
                S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("m7_built2", "提取钻、电塔与传送带建成接电；稀土矿沿带送进调配站", 1200, null, TickBuiltDrill),
                S("coolant", "调配站出冷却液（水 + 稀土矿），冷却液管送进净化塔：净化塔有电、有冷却液，开始从中心向外逐圈降低污染", 900, null, TickPurifying),
                S("returned", "净化回 0 级的格子计入归还面积：顶栏“已归还 N 格”真实增长", 900, null, TickReturned),
            };
            steps.AddRange(NightSteps());
            return new JourneyDef
            {
                Id = Id,
                Title = "M7 出口：看日历为第一次静默夜做准备（储能、静默夜预案）→ 静默夜期间逐项确认规则 → 撑过去捡回风暴残骸 → 酸雨中给关键产线盖顶棚 → 建净化塔让归还面积增长",
                Seed = TestSeed,
                TotalTimeoutSeconds = 3600,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "returned", "rules_close", "roof_hp" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static bool Need(JourneyContext c, string key) => c.GetInt(key) == 1;

        private static long NowMs() => FgjM2Common.NowMs();

        private static string Cell(GridCell g) => FgjM3Common.Cell(g);

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM2Common.IsolateCodex(c);
                FgjM3Journey.IsolateLayoutLibrary(c);
            });

        // ── 日历 ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 日历面板打开：今天起 N 个游戏日的时间轴 + 事件列表；<paramref name="afterFixture"/> = false 时核对正式排定（第一次静默夜在第 5 日夜里），
        /// = true 时核对挪过来的静默夜与酸雨都在事件列表里、带倒计时。
        /// </summary>
        private static StepOutcome TickCalendar(JourneyContext c, bool afterFixture)
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
            SilentNightState sn = SilentNightService.StateOf(s);
            List<string> ev = cal.EventTexts.ToList();
            string silent = ev.FirstOrDefault(t => t.Contains("静默夜")) ?? string.Empty;
            string evLine = string.Join(" / ", ev.Select(t => t.Replace("\n", " ")));
            if (!afterFixture)
            {
                int interval = SilentNightService.IntervalDays(s);
                bool ok = cal.DayRowCount == CalendarService.Days && sn != null && sn.NextDay == interval && silent.Length > 0 && cal.NowText.Contains("第 ");
                c.SetLong("sn.formalStart", sn?.StartTick ?? -1);
                return ok
                    ? StepOutcome.Done($"日历 {cal.DayRowCount} 个游戏日（{cal.NowText}）：第一次静默夜按正式排定在第 {sn.NextDay} 日（间隔 {interval} 日）{GameClock.FormatHhMm(sn.StartTick / (double)GameClock.StepHz)} 开始；事件“{evLine}”")
                    : StepOutcome.Fail($"日历不对：{cal.DayRowCount} 天（应 {CalendarService.Days}）、下一次静默夜第 {sn?.NextDay} 日（应 {interval}）、静默夜事件“{silent}”；全部事件“{evLine}”");
            }
            string rain = ev.FirstOrDefault(t => t.Contains("酸雨")) ?? string.Empty;
            bool ok2 = sn != null && sn.NextDay == c.GetInt("sn.day") && silent.Length > 0 && rain.Length > 0;
            return ok2
                ? StepOutcome.Done($"再看日历（{cal.NowText}）：今晚的静默夜“{silent.Replace("\n", " ")}”、酸雨“{rain.Replace("\n", " ")}”都在时间轴上")
                : StepOutcome.Fail($"日历上没有挪过来的静默夜 / 酸雨：下一次静默夜第 {sn?.NextDay} 日（应 {c.GetInt("sn.day")}）；事件“{evLine}”");
        }

        private static StepOutcome TickCalendarClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            return !CalendarPanelUIToolkit.IsOpen ? StepOutcome.Done("日历面板关闭") : StepOutcome.Retry("再按日历键后日历面板还开着");
        }

        // ── 规划 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            var specs = new[]
            {
                new M.SiteSpec { Key = "lab1", Type = M.Lab, MinR = 4 },
                new M.SiteSpec { Key = "lab2", Type = M.Lab, MinR = 4 },
            };
            int extra = P.BuildingDemand(E.Blender) + P.BuildingDemand(E.Purifier) + P.BuildingDemand(P.Drill) + P.BuildingDemand(E.Storage);
            P.FactoryPlan plan = M.PlanSites(s, true, false, specs, extra, out string why);
            if (plan == null)
            {
                return StepOutcome.Fail("规划不出来：" + why);
            }
            E.CoolantPlan cp = E.PlanCoolant(s, plan, out why);
            if (cp == null)
            {
                return StepOutcome.Fail("冷却液线规划不出来：" + why);
            }
            M.SavePlan(c, plan);
            P.SetP(c, "m7.pump", cp.Pump);
            P.SavePath(c, "path.water", cp.Water);
            P.SavePath(c, "path.coolant", cp.Coolant);
            P.SetP(c, "m7.itemIn", cp.ItemIn);
            c.SetInt("m7.itemDir", cp.ItemGoalDir);
            var a = new List<string>();
            foreach (GridCell g in plan.Generators)
            {
                a.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {Cell(g)}");
            }
            a.Add(P.ActB(plan.B("recycler")));
            a.Add(P.ActR(P.BeltT1, "path.r_recycler", plan.R("r_recycler").GoalDir, "回收站废料带 → 仓库输入口"));
            a.Add(P.ActB(plan.B("lab1")));
            a.Add(P.ActB(plan.B("lab2")));
            a.Add(P.ActB(plan.B("blender")));
            a.Add(P.ActB(plan.B("purifier")));
            a.Add(P.ActC(P.PumpTool, cp.Pump, "水源上的泵"));
            a.Add(P.ActQ(P.PipeT1, "path.water", "水管 → 调配站南口"));
            a.Add(P.ActQ(P.PipeT1, "path.coolant", "冷却液管：调配站东口 → 净化塔南口"));
            P.SetActions(c, "m7a", a);
            c.SetLong("ret0", PurificationService.ReturnedArea(s));
            return StepOutcome.Done($"规划（全部在归还核心配电范围里）：{M.Describe(plan, core)}；泵 {Cell(cp.Pump)}、水管 {cp.Water.Count} 格、冷却液管 {cp.Coolant.Count} 格；" +
                                   $"净化塔 {Cell(cp.Purifier)} 半径 {PurificationService.RadiusOfTier(E.Purifier, 1)} 格内有 {cp.Score} 格污染；开局归还面积 {c.GetLong("ret0")} 格");
        }

        // ── 施工 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickBuiltFirst(JourneyContext c)
        {
            StepOutcome o = M.TickBuilt(c, BatchA, new[] { "r_recycler" }, true, "第一批建筑");
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            CampaignState s = St;
            List<GridCell> pipes = P.LoadPath(c, "path.water").Concat(P.LoadPath(c, "path.coolant")).ToList();
            int left = pipes.Count(x => !PipeNetworkService.TryGetPiece(x, out _, out _));
            bool pump = PipeNetworkService.TryGetPiece(P.P(c, "m7.pump"), out _, out _);
            if (left > 0 || !pump)
            {
                return c.StepElapsed < 1450 ? StepOutcome.Wait : StepOutcome.Fail($"建筑都建成了，管线还差 {left} 格、泵 {(pump ? "已建" : "没建")}");
            }
            return StepOutcome.Done(o.Message + $"；泵与两条管线 {pipes.Count} 格建成");
        }

        // ── 稀土与探索 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickRareFind(JourneyContext c)
        {
            CampaignState s = St;
            GridCell itemIn = P.P(c, "m7.itemIn");
            if (!E.NearestRare(s, itemIn, out GridCell rare))
            {
                return StepOutcome.Fail($"调配站输入口 {Cell(itemIn)} 周围 90 格内没有稀土矿脉（起始区保证 64 格内有）");
            }
            P.SetP(c, "ex.target", rare);
            HomeGridMap map = HomeGridService.MapFor(s);
            bool explored = map.IsExplored(rare) && map.IsExplored(new GridCell(rare.X + 2, rare.Y)) && map.IsExplored(new GridCell(rare.X - 2, rare.Y));
            c.SetInt("ex.need", explored ? 0 : 1);
            int dist = Math.Abs(rare.X - itemIn.X) + Math.Abs(rare.Y - itemIn.Y);
            return StepOutcome.Done(explored
                ? $"最近的稀土矿脉 {Cell(rare)}（离调配站输入口 {dist} 格）已探索"
                : $"最近的稀土矿脉 {Cell(rare)}（离调配站输入口 {dist} 格）在迷雾里：建不了，要先派机器走过去探路");
        }

        /// <summary>右键地面（RTS：选中的机器移动过去）。地面点不在画面里 / 被界面挡住时先平移镜头，这次不点。</summary>
        internal static void RightClickGround(JourneyContext c, string cellKey)
        {
            Vector2 p = FgjM3Common.Ground(P.P(c, cellKey));
            c.SetLong("rc.at", 0);
            if (FgjM3Common.TryClick(p, 1))
            {
                c.SetLong("rc.at", Math.Max(1, NowMs()));
            }
        }

        internal static StepOutcome TickMoveOrdered(JourneyContext c, string machineKey, string cellKey)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (c.GetLong("rc.at") == 0)
            {
                // 镜头还在平移：平移结束后再点一次（同一次尝试里补点）。
                if (!JourneyInput.Holding)
                {
                    RightClickGround(c, cellKey);
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong("rc.at") < 500)
            {
                return StepOutcome.Wait;
            }
            int id = c.GetInt(machineKey);
            CombatSite site = GameRoot.HomeValley?.Combat;
            bool moving = site != null && site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
                          && (cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.Move || cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.WorkMove);
            return moving
                ? StepOutcome.Done($"右键地面 {Cell(P.P(c, cellKey))}：{FgjM1Journey.Label(id)} 接到移动命令")
                : StepOutcome.Retry($"右键地面后 {FgjM1Journey.Label(id)} 没有接到移动命令（{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        private static StepOutcome TickExplored(JourneyContext c)
        {
            if (!Need(c, "ex.need"))
            {
                return StepOutcome.Done("矿脉已探索，跳过");
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            GridCell rare = P.P(c, "ex.target");
            HomeGridMap map = HomeGridService.MapFor(s);
            bool ok = map.IsExplored(rare) && map.IsExplored(new GridCell(rare.X + 2, rare.Y)) && map.IsExplored(new GridCell(rare.X - 2, rare.Y));
            if (!ok)
            {
                return StepOutcome.Wait;
            }
            FgjM2Common.HomePos(c.GetInt("workerA"), out Vector2 at);
            return StepOutcome.Done($"{FgjM1Journey.Label(c.GetInt("workerA"))} 走到 {at.x:F0},{at.y:F0}：矿脉 {Cell(rare)} 一带探索完（机器走过的地方迷雾揭开；本次会话机器追加已探索圆 {MachineExploration.Reveals} 个）");
        }

        private static StepOutcome TickDrillPlan(JourneyContext c)
        {
            CampaignState s = St;
            GridCell itemIn = P.P(c, "m7.itemIn");
            var reserved = P.LoadPath(c, "path.water").Concat(P.LoadPath(c, "path.coolant")).Concat(P.LoadPath(c, "path.r_recycler")).Concat(new[] { P.P(c, "m7.pump") }).ToList();
            P.FactoryPlan plan = E.PlanDrill(s, itemIn, c.GetInt("m7.itemDir"), reserved, out List<GridCell> poles, out string why);
            if (plan == null)
            {
                return StepOutcome.Fail("稀土提取钻规划不出来：" + why);
            }
            P.Placed drill = plan.B("drillR");
            P.Route route = plan.R("r_rare");
            P.Remember(c, drill);
            P.SavePath(c, "path.r_rare", route.Path);
            c.Set("m7.poles", FgjM3Common.EncodeCells(poles));
            c.SetInt("m7.poleN", poles.Count);
            var a = new List<string>();
            foreach (GridCell pole in poles)
            {
                a.Add(P.ActB(new P.Placed { Type = P.PoleT2, Pivot = pole, Rot = 0, Label = "T2 电塔 " + Cell(pole) }));
            }
            a.Add(P.ActB(drill));
            a.Add(P.ActR(P.BeltT1, "path.r_rare", route.GoalDir, route.Label));
            P.SetActions(c, "m7d", a);
            return StepOutcome.Done($"稀土提取钻 {Cell(drill.Pivot)}、传送带 {route.Path.Count} 格到调配站西侧输入口 {Cell(itemIn)}；T2 电塔 {poles.Count} 座");
        }

        private static StepOutcome TickBuiltDrill(JourneyContext c)
        {
            StepOutcome o = M.TickBuilt(c, new[] { "drillR" }, new[] { "r_rare" }, false, "提取钻与传送带");
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            CampaignState s = St;
            List<GridCell> poles = FgjM3Common.DecodeCells(c.Get("m7.poles"));
            int poleLeft = poles.Count(g => !P.BuildingReady(s, P.PoleT2, g, out _));
            if (poleLeft > 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done(o.Message);
        }

        // ── 净化 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPurifying(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord blender = P.Bld(c, "blender");
            BuildingRecord pur = P.Bld(c, "purifier");
            ProductionService.Producer bp = P.Prod(blender);
            ProductionService.Producer pp = P.Prod(pur);
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"调配站：完成 {P.Completed(blender)} 次（{(bp != null ? ProductionService.StateText(bp) + "：" + ProductionService.ReasonText(s, bp) : "?")}）；" +
                      $"净化塔：{(pp != null ? ProductionService.StateText(pp) + "：" + ProductionService.ReasonText(s, pp) : "?")}；稀土矿库存 {P.Stock("rare_earth_ore")}；{P.PowerLine(s)}");
            }
            if (bp == null || pp == null || P.Completed(blender) <= 0 || !pp.Rec.PurifySupplied)
            {
                return StepOutcome.Wait;
            }
            PurificationService.FrontOf(s, pur, out int front, out int rings, out int left);
            c.SetLong("ret1", PurificationService.ReturnedArea(s));
            return StepOutcome.Done($"调配站已出 {P.Completed(blender)} 批冷却液；净化塔有电、有冷却液（{PurificationService.WorkingText(s, pp)?.Replace("\n", " ")}）：第 {front} / {rings} 环，还要 {left} 次降级；" +
                                   $"预计 {PurificationService.SecondsToFinish(s, pur, pp.Rec) / GameClock.HourSeconds:F1} 游戏小时净化完；归还面积 {c.GetLong("ret1")} 格");
        }

        private static StepOutcome TickReturned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            long now = PurificationService.ReturnedArea(s);
            if (now <= c.GetLong("ret1"))
            {
                return StepOutcome.Wait;
            }
            TopBarCheck(out string bar);
            c.SetLong("ret2", now);
            c.SetLong("ret2.tick", GameClock.Ticks);
            return StepOutcome.Done($"归还面积 {c.GetLong("ret1")} → {now} 格（净化回 0 级的格子）；顶栏 / 日历页脚“{bar}”");
        }

        private static bool TopBarCheck(out string text)
        {
            text = PurificationService.CalendarText(St) ?? string.Empty;
            return text.Length > 0;
        }

        // ── 收尾 ──────────────────────────────────────────────────────────────────────

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            c.Log(FgjM3Common.FrameReport("冷却液线施工 / 净化 / 酸雨与顶棚 / 静默夜"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
