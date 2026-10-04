using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M5 收尾段：第 2 步后半（研究完成 → 建造菜单“新” → 放置 → 施工 → 监听站出情报 → 点通知）、研发加成来源行、
    /// 第 7 步（突袭部队出发 → 突袭预报 → 点通知定位 → 情报面板 → 在地图上查看 → 部队到达后预报标“已过时”）、黑匣子分析完、统计面板“研发”页。
    /// </summary>
    public static partial class FgjM5Journey
    {
        private static IEnumerable<JourneyStep> LateSteps() => new[]
        {
            // ── 第 2 步后半：研究完成 → 新建筑 ──
            S("rt_done", "研究完成：“采集优化 I”“监听站”都完成（通知写明解锁了什么）；建造菜单里监听站标“新”", 900, null, TickResearchDone),
            S("bonus_open", "建造模式里左键点回收站：通用面板打开", 40, null, c => P.TickOpenPanel(c, P.Recycler, P.P(c, "recycler")), retries: 2),
            S("bonus_read", "通用面板写“研发加成：工作速度 +10%（采集优化 I +10%……）”（B13 数值来源；与回收站真实周期一致）", 10, null, TickBonusLine),
            S("bonus_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
            S("post_new", "建造栏点监听站所在的分类：监听站那一项写着“新”", 20, null, TickPostMarkedNew, retries: 2),
            S("post_place", "选中监听站，指着留给它的空地单击：放下监听站的虚影（研究完成后才能放）", 90, null, c => P.TickActions(c, "m5c", 40), retries: 2),
            S("build_close4", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("post_built", "机器取料施工：监听站建成、接上电，开始破译", 900, null, c => M.TickBuilt(c, new[] { "post" }, Array.Empty<string>(), false, "监听站")),
            S("intel_new", "监听站破译出第一条情报（敌方反制预览）：通知“新情报”；点通知弹出条：情报面板打开", 400, null, c => TickToastOpens(c, "intel_new")),
            S("intel_close1", "按 Esc 关闭情报面板", 10, null, c => M.TickCloseAll(c, () => !IntelPanelUIToolkit.IsOpen, "情报面板"), retries: 1),

            // ── 第 7 步：在情报面板看到下一次突袭的预报 ──
            S("raid_fixture", "进度夹具：一支突袭部队从敌方领地出发（突袭导演在 FG6-DEF-04；出发之后的沿地形行进、破译、预报全是正式流程；DEBT-FG5E2E01-04）", 10, ApplyRaidFixture, TickRaidFixture),
            S("intel_raid", "监听站插队破译突袭预报：通知“突袭预报”（带预计抵达点）；点通知弹出条：镜头飞到预计抵达点", 300, null, TickRaidForecastClick),
            S("intel_open", "按情报键（默认 Y）打开情报面板：突袭预报一行写明时间窗口、阵营、规模、方向，带“在地图上查看”", 15,
                c => JourneyInput.PressToggleTo(GameActionId.OpenIntel, () => IntelPanelUIToolkit.IsOpen, true), TickIntelRaidRow, retries: 1),
            S("intel_map", "点“在地图上查看”：情报面板收起、战略地图打开，来袭方向画着箭头", 15, null, TickIntelMap, retries: 1),
            S("intel_map_close", "按 Esc 关闭战略地图", 10, null, c => M.TickCloseAll(c, () => !StrategicMapUIToolkit.IsOpen, "战略地图"), retries: 1),
            S("raid_arrive", "突袭部队到达家园（M5 还没有攻城，FG6-DEF）：这条预报变成“已过时（部队已经到达）”，不删除", 600, null, TickForecastOutdated),
            S("intel_open2", "按情报键打开情报面板：预报那一行写“已过时”、没有“在地图上查看”", 15,
                c => JourneyInput.PressToggleTo(GameActionId.OpenIntel, () => IntelPanelUIToolkit.IsOpen, true), TickIntelOutdatedRow, retries: 1),
            S("intel_close2", "按 Esc 关闭情报面板", 10, null, c => M.TickCloseAll(c, () => !IntelPanelUIToolkit.IsOpen, "情报面板"), retries: 1),

            // ── 黑匣子分析完 → 纪念墙；统计面板“研发”页 ──
            S("bb_done", "陈列馆分析完冷却液机的黑匣子：技术数据 +20 逐点入账（统计记“黑匣子陈列馆”收入），这期间实验室照常取技术数据换研究点", 1200, null, TickBlackBoxDone),
            S("bb_open", "建造模式里左键点陈列馆，通用面板上点“陈列馆…”", 40, null,
                c => M.TickSubPanel(c, "gallery", "PrBlackBox", () => BlackBoxPanelUIToolkit.IsOpen && BlackBoxPanelUIToolkit.Instance != null && BlackBoxPanelUIToolkit.Instance.PanelVisible, "陈列馆面板打开"), retries: 2),
            S("bb_read", "黑匣子页写冷却液机的黑匣子“已分析”；点“纪念墙”页签：它的名字、编号、阵亡地点与经历", 20, null, TickBlackBoxPanel, retries: 1),
            S("bb_close", "按 Esc 关闭陈列馆面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !BlackBoxPanelUIToolkit.IsOpen, "陈列馆面板"), retries: 1),
            S("build_close5", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("stats_open", "按统计键（默认 Alt+T）打开统计面板，点“研发”页签", 15, c => JourneyInput.PressToggleTo(GameActionId.OpenStats, () => StatsPanelUIToolkit.IsOpen, true), TickStatsResearch, retries: 1),
            S("stats_read", "研发页：技术数据收入按来源（黑匣子 +20）、支出按去处（实验室 / 模拟熔合 / 正式熔合 / 数据复原），熔合 / 情报 / 黑匣子的累计与这一趟真实做过的逐项一致", 15, null, TickStatsRows),
            S("stats_close", "点“关闭”关闭统计面板", 10, c => FgjM1Journey.ClickUi(c, M.StatsHost, "StatsPanelClose"),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !StatsPanelUIToolkit.IsOpen ? StepOutcome.Done("关闭统计面板") : StepOutcome.Retry("统计面板还开着：" + FgjM1Journey.UiFail(c)), retries: 1),
        };

        // ── 研究完成 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickResearchDone(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            bool gather = ResearchService.IsCompleted(s, M.NodeGather);
            bool post = ResearchService.IsCompleted(s, M.NodePost);
            if (!gather || !post)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"研究中：研究点 {s.Research.Points}、队列 [{string.Join(",", s.Research.Queue)}]、实验室 {ResearchService.BuiltLabCount(s)} 座、技术数据 {s.TechData}；状态“{ResearchService.StatusText(s)}”");
                }
                return StepOutcome.Wait;
            }
            string text = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == "research_done" && e.Text != null && e.Text.Contains(M.Name(M.Post)))?.Text;
            bool unlocked = BuildCatalog.IsUnlocked(s, "research:" + M.NodePost);
            bool isNew = ResearchService.IsNew(s, M.Post);
            return text != null && unlocked && isNew
                ? StepOutcome.Done($"研究完成：通知“{text}”；“{M.NodeName(M.NodeGather)}”也完成（效果已生效）；监听站解锁并在建造菜单标“新”；实验室累计产出研究点 {s.Research.PointsProduced}、取用技术数据 {s.Research.TechConsumed}")
                : StepOutcome.Fail($"研究完成后：通知“{text}”、监听站解锁 {unlocked}、标新 {isNew}");
        }

        private static StepOutcome TickBonusLine(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            string line = panel?.ResearchLineText ?? string.Empty;
            BuildingRecord rec = P.Bld(c, "recycler");
            double factor = ResearchService.SpeedFactor(St, rec?.BuildingTypeId);
            string want = M.NodeName(M.NodeGather);
            return line.Contains("+10%") && line.Contains(want) && Math.Abs(factor - 1.1) < 1e-6
                ? StepOutcome.Done($"回收站通用面板：“{line}”（与真实工作速度倍率 ×{factor:0.##} 同一读口）")
                : StepOutcome.Fail($"研发加成来源行不对：“{line}”；真实倍率 ×{factor:0.###}");
        }

        private static StepOutcome TickPostMarkedNew(JourneyContext c)
        {
            if (!FgjM3Common.BuildOpen)
            {
                if (M.NowMs() - c.GetLong(FgjM3Common.SK(c, "open")) > 1200)
                {
                    c.SetLong(FgjM3Common.SK(c, "open"), M.NowMs());
                    FgjM3Common.PressBuild(true);
                }
                return StepOutcome.Wait;
            }
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            if (!BuildCatalog.TryGet(M.Post, out BuildEntry e) || hud == null)
            {
                return StepOutcome.Fail("建造目录里没有监听站");
            }
            if (!FgjM3Common.CatalogueExpanded(out string whyCat))
            {
                return whyCat == null ? StepOutcome.Wait : StepOutcome.Retry(whyCat);
            }
            if (hud.SelectedCategoryId != e.CategoryId)
            {
                int cat = GridContent.Categories.ToList().FindIndex(x => x.Id == e.CategoryId);
                c.Set(FgjM3Common.SK(c, "tab"), cat >= 0 ? hud.CategoryText(cat) : string.Empty);
                if (!FgjM3Common.Once(c, "cat", () => JourneyInput.ClickUitk(FgjM3Common.BuildHost, "BuildCat" + cat.ToString(CultureInfo.InvariantCulture))))
                {
                    return StepOutcome.Wait;
                }
                return FgjM3Common.SinceMs(c, "cat") < 1500 ? StepOutcome.Wait : StepOutcome.Retry("点不到监听站所在的分类：" + JourneyInput.LastUiFailure);
            }
            string item = string.Empty;
            for (int i = 0; i < hud.ItemCount; i++)
            {
                if (hud.ItemId(i) == M.Post)
                {
                    item = hud.Root?.Q<Button>("BuildItem" + i)?.text ?? string.Empty;
                }
            }
            string mark = GameText.Get("research.build.new");
            return item.StartsWith(mark, StringComparison.Ordinal)
                ? StepOutcome.Done($"分类页签“{c.Get(FgjM3Common.SK(c, "tab"))}”里监听站写“{item.Split('\n')[0]}”（研究完成后新解锁）")
                : StepOutcome.Fail($"监听站那一项没有“新”标记：“{item}”");
        }

        // ── 情报：点通知 ────────────────────────────────────────────────────────────

        /// <summary>通知弹出条里找 <paramref name="typeId"/> 那一条（UI Toolkit 弹出条，userData = 通知条目），派发真实指针事件点它。返回 null = 还没出现。</summary>
        private static bool? ClickToast(string typeId, out NotificationEntry entry)
        {
            entry = null;
            VisualElement list = JourneyInput.FindUitk<VisualElement>("[NotificationHudHost]", "ToastList");
            if (list == null)
            {
                return null;
            }
            foreach (VisualElement t in list.Children())
            {
                if (t.userData is NotificationEntry e && e.Type?.Id == typeId && JourneyInput.IsClickable(t))
                {
                    entry = e;
                    return JourneyInput.ClickElement(t);
                }
            }
            return null;
        }

        private static StepOutcome TickToastOpens(JourneyContext c, string typeId)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            if (IntelPanelUIToolkit.IsOpen && FgjM3Common.Done(c, "click"))
            {
                IntelPanelUIToolkit p = IntelPanelUIToolkit.Instance;
                return c.StepElapsed < 0.5 ? StepOutcome.Wait
                    : StepOutcome.Done($"点通知弹出条“{c.Get(FgjM3Common.SK(c, "text"))}”：情报面板打开（{p?.RowCount} 条；“{p?.StatusText?.Split('\n')[0]}”）");
            }
            if (!FgjM3Common.Done(c, "click"))
            {
                bool? clicked = ClickToast(typeId, out NotificationEntry e);
                if (clicked == null)
                {
                    if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                    {
                        c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                        c.Log($"等情报：工作中的监听站 {IntelService.WorkingCount(St)} 座、正在破译“{IntelService.StateOf(St)?.CurrentKind}”");
                    }
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到通知弹出条：");
                }
                c.Set(FgjM3Common.SK(c, "text"), e.Text);
                FgjM3Common.Mark(c, "click");
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "click") < 2000 ? StepOutcome.Wait : StepOutcome.Retry("点了通知弹出条，情报面板没打开");
        }

        /// <summary>
        /// 进度夹具（DEBT-FG5E2E01-04）：派一支突袭部队——正式的突袭导演在 FG6-DEF-04；这里调的是导演将来调用的同一入口（世界行进系统按领地派出），
        /// 之后的沿地形行进、到达、监听站破译、预报、地图箭头全是正式流程。
        /// </summary>
        private static void ApplyRaidFixture(JourneyContext c)
        {
            CampaignState s = St;
            WorldPlan plan = WorldGenService.PlanFor(s);
            string territory = plan?.Territories.FirstOrDefault(t => t.IsFaction && t.Act == 1)?.Id ?? "silent";
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(s, territory, 3, out string fail);
            c.Set("raid", raid?.GroupId ?? string.Empty);
            c.Set("raidFail", fail ?? string.Empty);
            c.Set("raidFrom", territory);
        }

        private static StepOutcome TickRaidFixture(JourneyContext c) =>
            c.Get("raid").Length > 0
                ? StepOutcome.Done($"进度夹具：突袭部队 {c.Get("raid")} 从领地 {c.Get("raidFrom")} 出发（导演在 FG6-DEF-04；DEBT-FG5E2E01-04）")
                : StepOutcome.Fail("派不出突袭部队：" + c.Get("raidFail"));

        private static StepOutcome TickRaidForecastClick(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            IntelRecord f = M.RaidForecastFor(c.Get("raid"));
            if (!FgjM3Common.Done(c, "click"))
            {
                if (f == null)
                {
                    return StepOutcome.Wait;
                }
                bool? clicked = ClickToast("intel_raid", out NotificationEntry e);
                if (clicked == null)
                {
                    return c.StepElapsed < 280 ? StepOutcome.Wait : StepOutcome.Fail("突袭预报出来了，却没有“突袭预报”通知弹出条");
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到通知弹出条：");
                }
                c.Set(FgjM3Common.SK(c, "text"), e.Text);
                FgjM3Common.Mark(c, "click");
                return StepOutcome.Wait;
            }
            // 修复轮：点通知后镜头是一段“飞过去”的过渡（CameraDirector.FlyStrategyTo），过渡期间输入域是 None、所有按键按设计都不收（InputScope.None 的约定：
            // 镜头在动时接受操作，玩家必然打空）。上一版只等 1.5 秒就按情报键，偶尔正赶上还在飞，第一次按键被丢（FGJ-M5 诊断轨迹证实）；玩家看得到镜头在飞，等它停下再按。
            if (FgjM3Common.SinceMs(c, "click") < 1500 || WorldView.Director.InTransition)
            {
                return StepOutcome.Wait;
            }
            Unity.Mathematics.float2 focus = WorldView.Director.StrategyFocus;
            float d = Vector2.Distance(new Vector2(focus.x, focus.y), new Vector2(f.ArriveX, f.ArriveY));
            return d < 20f
                ? StepOutcome.Done($"监听站破译出突袭预报（{IntelService.Summary(St, f, GameClock.Ticks)}）；点通知弹出条“{c.Get(FgjM3Common.SK(c, "text"))}”：镜头飞到预计抵达点（离它 {d:F1} 格）")
                : StepOutcome.Retry($"点通知后镜头离预计抵达点 {d:F1} 格（焦点 {focus.x:F0},{focus.y:F0}；抵达点 {f.ArriveX:F0},{f.ArriveY:F0}）");
        }

        private static StepOutcome TickIntelRaidRow(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            IntelPanelUIToolkit p = IntelPanelUIToolkit.Instance;
            p?.Refresh(force: true);
            if (!IntelPanelUIToolkit.IsOpen || p == null)
            {
                return StepOutcome.Retry("按情报键后情报面板没打开（" + JourneyInput.LastKeyTrace + "）");
            }
            string kind = IntelCatalog.TryGet(IntelCatalog.KindRaid, out IntelKindDef def) ? def.Name : "突袭预报";
            for (int i = 0; i < p.RowCount; i++)
            {
                if (p.RowText(i).Contains(kind) && p.RowMapVisible(i))
                {
                    c.SetInt("intelRow", i);
                    return StepOutcome.Done($"情报面板：“{p.RowText(i).Replace("\n", " / ")}”，有“在地图上查看”");
                }
            }
            return StepOutcome.Fail($"情报面板里没有可在地图上查看的突袭预报（{p.RowCount} 行）");
        }

        private static StepOutcome TickIntelMap(JourneyContext c)
        {
            IntelPanelUIToolkit p = IntelPanelUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "map"))
            {
                bool? clicked = P.ClickInView(p?.RowMapButton(c.GetInt("intelRow")));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到“在地图上查看”：");
                }
                FgjM3Common.Mark(c, "map");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            bool arrow = StrategicMapUIToolkit.IsOpen && !IntelPanelUIToolkit.IsOpen && map != null && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.RaidForecast)
                         && map.Model.Lines.Any(l => l.Forecast);
            return arrow
                ? StepOutcome.Done("点“在地图上查看”：情报面板收起、战略地图打开，突袭预报的来袭方向箭头与标签在地图上")
                : StepOutcome.Retry($"战略地图 {StrategicMapUIToolkit.IsOpen}、情报面板 {IntelPanelUIToolkit.IsOpen}、箭头 {map?.Model?.Lines?.Any(l => l.Forecast)}");
        }

        private static StepOutcome TickForecastOutdated(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            IntelRecord f = M.RaidForecastFor(c.Get("raid"));
            TransitGroupRecord g = WorldTransitSystem.Groups(St).FirstOrDefault(x => x != null && x.GroupId == c.Get("raid"));
            if (f == null)
            {
                return StepOutcome.Fail("突袭预报不见了（已过时的情报不应删除）");
            }
            if (!f.Outdated)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"突袭部队 {g?.State}，预计还要 {WorldTransitSystem.EtaSeconds(g):F0} 游戏秒");
                }
                return StepOutcome.Wait;
            }
            if (f.OutdatedReason != IntelService.ReasonArrived || g == null || g.State != TransitGroupState.Arrived)
            {
                return StepOutcome.Fail($"预报过时原因 {f.OutdatedReason}、部队 {g?.State}");
            }
            // 修复轮（审查 P2）：FGT-RND-008“时间窗口、阵营、方向与实际到来的一致”自动判定（原来只把数字写进日志）。
            // 口径与 FgIntelSelfCheck.CheckRaidForecast 相同：到达步落在预报窗口里；阵营 = 派出领地、规模 = 部队台数；实际到达点相对核心的方向与预报方向偏差 ≤ 25° 且同一方位。
            GridCell core = HomeGridService.CorePivot(St);
            double ax = g.PosX - core.X, ay = g.PosY - core.Y;
            double angF = Math.Atan2(f.DirY, f.DirX) * 180 / Math.PI;
            double angA = Math.Atan2(ay, ax) * 180 / Math.PI;
            double diff = Math.Abs(((angF - angA) % 360 + 540) % 360 - 180);
            bool window = g.ArrivedAtTick >= f.WindowFromTick && g.ArrivedAtTick <= f.WindowToTick;
            bool faction = f.Faction == c.Get("raidFrom") && g.OriginId == c.Get("raidFrom") && f.Units == g.UnitCount;
            bool direction = diff <= 25.0 && IntelService.Octant(ax, ay) == IntelService.Octant(f.DirX, f.DirY);
            string facts = $"到达第 {g.ArrivedAtTick} 步、预报窗口 {f.WindowFromTick}～{f.WindowToTick} 步；阵营 {f.Faction} / 派出 {c.Get("raidFrom")}，规模 {f.Units} / {g.UnitCount}；" +
                           $"方向 预报{IntelService.DirectionName(f.DirX, f.DirY)} / 实际{IntelService.DirectionName((float)ax, (float)ay)}（偏差 {diff:F1}°）";
            return window && faction && direction
                ? StepOutcome.Done($"突袭部队到达，预报与实际一致（{facts}）：预报标“已过时（部队已经到达）”，仍留在情报列表里")
                : StepOutcome.Fail($"突袭预报与实际到来不一致：窗口 {window}、阵营与规模 {faction}、方向 {direction}（{facts}）");
        }

        private static StepOutcome TickIntelOutdatedRow(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            IntelPanelUIToolkit p = IntelPanelUIToolkit.Instance;
            p?.Refresh(force: true);
            if (!IntelPanelUIToolkit.IsOpen || p == null)
            {
                return StepOutcome.Retry("按情报键后情报面板没打开（" + JourneyInput.LastKeyTrace + "）");
            }
            string kind = IntelCatalog.TryGet(IntelCatalog.KindRaid, out IntelKindDef def) ? def.Name : "突袭预报";
            for (int i = 0; i < p.RowCount; i++)
            {
                string t = p.RowText(i);
                if (t.Contains(kind) && t.Contains("已过时") && !p.RowMapVisible(i))
                {
                    return StepOutcome.Done($"情报面板：“{t.Replace("\n", " / ")}”（不删除、没有“在地图上查看”）");
                }
            }
            return StepOutcome.Fail($"情报面板里没有“已过时”的突袭预报（{p.RowCount} 行）");
        }

        // ── 黑匣子分析完 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickBlackBoxDone(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int victim = c.GetInt("victim");
            BlackBoxRecord box = BlackBoxService.StateOf(St)?.Boxes?.FirstOrDefault(b => b != null && b.MachineLogicId == victim);
            if (box == null)
            {
                return StepOutcome.Fail("陈列馆队列里没有冷却液机的黑匣子");
            }
            if (!box.Done)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"黑匣子分析中：已入账 {box.PointsGranted} 点（工作中的陈列馆 {BlackBoxService.WorkingCount(St)} 座）");
                }
                return StepOutcome.Wait;
            }
            long income = TechDataFlow.IncomeOf(St, TechDataFlow.BlackBox);
            long lab = TechDataFlow.ExpenseOf(St, TechDataFlow.Lab) - c.GetLong("bb.lab0");
            int per = BlackBoxService.PointsPerBox;
            return box.PointsGranted == per && income == per && lab > 0
                ? StepOutcome.Done($"黑匣子分析完：技术数据 +{box.PointsGranted}（统计“黑匣子陈列馆”收入 {income}）；同一时期仿真实验室又从技术数据里取了 {lab} 件换研究点（技术数据进同一个库存，研究照常用上）")
                : StepOutcome.Fail($"黑匣子入账 {box.PointsGranted}（应 {per}）、统计收入 {income}、实验室同期取用 {lab}");
        }

        private static StepOutcome TickBlackBoxPanel(JourneyContext c)
        {
            BlackBoxPanelUIToolkit p = BlackBoxPanelUIToolkit.Instance;
            p?.Refresh(force: true);
            int victim = c.GetInt("victim");
            if (p == null || !BlackBoxPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("陈列馆面板没开");
            }
            if (!FgjM3Common.Done(c, "boxes"))
            {
                string row = null;
                for (int i = 0; i < p.RowCount; i++)
                {
                    if (p.RowLogicId(i) == victim)
                    {
                        row = p.RowText(i);
                    }
                }
                if (row == null || p.CurrentTab != BlackBoxPanelUIToolkit.TabBoxes)
                {
                    return StepOutcome.Fail($"黑匣子页没有冷却液机（{p.RowCount} 行，页签 {p.CurrentTab}）");
                }
                c.Set(FgjM3Common.SK(c, "boxRow"), row);
                if (!JourneyInput.ClickUitk(M.BlackBoxHost, "BlackBoxTabMemorial"))
                {
                    return M.UiRetry("点不到“纪念墙”页签：");
                }
                FgjM3Common.Mark(c, "boxes");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            string wall = null;
            for (int i = 0; i < p.RowCount; i++)
            {
                if (p.RowLogicId(i) == victim)
                {
                    wall = p.RowText(i);
                }
            }
            return p.CurrentTab == BlackBoxPanelUIToolkit.TabMemorial && wall != null && wall.Contains("阵亡") && wall.Contains("经历")
                ? StepOutcome.Done($"黑匣子页“{c.Get(FgjM3Common.SK(c, "boxRow")).Split('\n')[0]}”；纪念墙页“{wall.Split('\n')[0]}”（名字 · 编号 · 型号、阵亡地点与时间、经历、黑匣子去向）")
                : StepOutcome.Fail($"纪念墙页不对：页签 {p.CurrentTab}、“{wall}”");
        }

        // ── 统计面板“研发”页 ──────────────────────────────────────────────────────────

        private static StepOutcome TickStatsResearch(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            StatsPanelUIToolkit p = StatsPanelUIToolkit.Instance;
            if (!StatsPanelUIToolkit.IsOpen || p == null)
            {
                return StepOutcome.Retry("按统计键后统计面板没打开");
            }
            if (p.CurrentTab == StatsTab.Research)
            {
                return c.StepElapsed < 1.2 ? StepOutcome.Wait : StepOutcome.Done($"统计面板“研发”页：“{p.SectionText}”，{p.VisibleRowCount} 行");
            }
            if (!FgjM3Common.Once(c, "tab", () => JourneyInput.ClickUitk(M.StatsHost, "StatsTabResearch")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tab") < 1500 ? StepOutcome.Wait : StepOutcome.Retry("点“研发”页签没切过去：" + JourneyInput.LastUiFailure);
        }

        private static StepOutcome TickStatsRows(JourneyContext c)
        {
            StatsPanelUIToolkit p = StatsPanelUIToolkit.Instance;
            p?.Refresh(force: true);
            if (p == null || p.CurrentTab != StatsTab.Research)
            {
                return StepOutcome.Fail("统计面板不在“研发”页");
            }
            CampaignState s = St;
            int restore = FirmwareRestoreServiceCost();
            var checks = new List<(string what, string text)>
            {
                ("开局技术数据", GameText.Format("stats.research.tech_stock", s.TechData, ResearchService.StartTechData, TechDataFlow.TotalIncome(s), TechDataFlow.TotalExpense(s))),
                ("黑匣子收入 = 一个黑匣子的点数", GameText.Format("stats.research.income", GameText.Get("stats.research.src.blackbox"), BlackBoxService.PointsPerBox)),
                ("数据复原支出 = 两次复原费", GameText.Format("stats.research.expense", GameText.Get("stats.research.sink.restore"), restore)),
                ("模拟熔合支出 = 两次模拟费", GameText.Format("stats.research.expense", GameText.Get("stats.research.sink.fusion_sim"), FusionCatalog.SimTech * 2)),
                ("正式熔合支出 = 一次正式熔合", GameText.Format("stats.research.expense", GameText.Get("stats.research.sink.fusion"), c.GetInt("fu.jobTech"))),
            };
            var missing = new List<string>();
            foreach ((string what, string text) in checks)
            {
                if (p.FindRow(text) < 0)
                {
                    missing.Add($"{what}（应有“{text}”）");
                }
            }
            FusionState f = FusionService.StateOf(s);
            bool fusionOk = f != null && f.Simulations == 2 && f.SimulationMisses == 1 && f.Fused == 1
                            && p.FindRow(GameText.Format("stats.research.fusion_row", 2, 1, 1, 0, f.TechSpent, f.Discovered.Length, FusionCatalog.Recipes.Count, f.Clues.Length)) >= 0;
            IntelState it = IntelService.StateOf(s);
            bool intelOk = it != null && it.Produced >= 2 && it.Interruptions == 0
                           && p.FindRow(GameText.Format("stats.research.intel_row", it.Produced, 0, IntelService.ValidCount(s, GameClock.Ticks))) >= 0;
            bool labOk = TechDataFlow.ExpenseOf(s, TechDataFlow.Lab) == s.Research.TechConsumed && s.Research.TechConsumed > 0;
            // 修复轮（审查 P2）：面板读的是同一组数，只核对“面板照抄了”证明不了账能对上——这里直接核对收支恒等式（新档：库存 = 开局带来 + 累计收入 − 累计支出），
            // 并单独核对解析台破解那一笔（ana_cracked 时记下的增量）：等于表里加密固件的“首次技术数据”（fg.TbAnalysisKind encrypted_firmware；FGR-RND-021：
            // 加密固件破解是“固件本身变成已破解”，表里配 0、不给技术数据），并且就是统计页“解析台”收入的全部（这一趟解析台没解析别的）。
            long income = TechDataFlow.TotalIncome(s);
            long expense = TechDataFlow.TotalExpense(s);
            bool ledgerOk = s.Research.TechStart == ResearchService.StartTechData && s.TechData == s.Research.TechStart + income - expense;
            long crack = c.GetLong("ana.crack");
            long analysis = TechDataFlow.IncomeOf(s, TechDataFlow.Analysis);
            int crackTech = AnalysisCatalog.TryGetKind(AnalysisCatalog.EncryptedFirmwareId, out AnalysisKindDef kindDef) ? kindDef.TechFirst : -1;
            bool analysisOk = crackTech >= 0 && crack == crackTech && analysis == crack
                              && p.FindRow(GameText.Format("stats.research.income", GameText.Get("stats.research.src.analysis"), analysis)) >= 0;
            if (missing.Count > 0 || !fusionOk || !intelOk || !labOk || !ledgerOk || !analysisOk)
            {
                return StepOutcome.Fail($"研发页不对：缺 [{string.Join("；", missing)}]；熔合 {fusionOk}（{f?.Simulations}/{f?.SimulationMisses}/{f?.Fused}）、情报 {intelOk}（{it?.Produced}/{it?.Interruptions}）、实验室支出 = 实验室取用 {labOk}；" +
                                        $"收支恒等式 {ledgerOk}（库存 {s.TechData}，开局 {s.Research.TechStart} + 收入 {income} − 支出 {expense} = {s.Research.TechStart + income - expense}）；" +
                                        $"解析台收入 {analysisOk}（统计 {analysis}、破解那一笔 {crack}、表里加密固件首次 {crackTech}）");
            }
            return StepOutcome.Done($"研发页逐项对得上：库存 {s.TechData} = 开局 {s.Research.TechStart} + 收入 {income} − 支出 {expense}；解析台破解那一笔 {crack}（= 表里加密固件首次 {crackTech}，" +
                                    $"破解不给技术数据）= “解析台”收入；黑匣子收入 {BlackBoxService.PointsPerBox}、" +
                                    $"数据复原支出 {restore}、模拟熔合 {FusionCatalog.SimTech * 2}、正式熔合 {c.GetInt("fu.jobTech")}、" +
                                    $"实验室 {s.Research.TechConsumed}；熔合 模拟 2（无反应 1）、完成 1；情报累计 {it.Produced} 条、破译中断 0 次；{p.VisibleRowCount} 行");
        }

        private static int FirmwareRestoreServiceCost() =>
            Campaign.Signal.FirmwareRestoreService.CostOf(FgjM2Common.FwWet) + Campaign.Signal.FirmwareRestoreService.CostOf(FgjM2Common.FwShock);
    }
}
