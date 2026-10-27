using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Expedition;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>FGJ-M8 第一段：派遣检查单（路线走出覆盖）→ 沿途建信号中继塔 → 检查单复核 → 出发、沿地形行军到破碎都市（途中进入静默领地）→ 正式撤离 → 远征队在覆盖里待命。</summary>
    public static partial class FgjM8Journey
    {
        private static IEnumerable<JourneyStep> ExpeditionSteps() => new[]
        {
            S("prep_open", "左键点信号塔打开远征准备面板（派遣检查单）", 20, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                FgjM1Journey.TickPrepOpen, retries: 2),
            S("prep_pick", "名单表里勾选远征队四台", 40, null, TickPick),
            S("chk_cov", "检查单提醒区写“路线有一段会走出信号覆盖（约 N 格）”——与按真实行军路线（层级寻路）采样的覆盖一致", 15, null, c => TickCoverage(c, true)),
            S("prep_close", "按 Esc 关闭远征准备面板", 10, c => JourneyInput.PressAction(GameActionId.Cancel), TickPrepClosed, retries: 1),
            S("relay_plan", "规划信号中继塔：放在行军路线还在覆盖里的最后一段（离覆盖边缘最近、能放下）", 10, null, TickRelayPlan),
            S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("relay_place", "建造栏选“信号中继塔”，在规划格左键放下（虚影）", 90, null, c => P.TickActions(c, "m8r", 40), retries: 2),
            S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("relay_built", "家园机器取料走过去施工：中继塔建成、接进与核心连通的覆盖网，路线走出覆盖的长度变短", 600, null, TickRelayBuilt),
            S("prep_open2", "再左键点信号塔打开远征准备面板", 20, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                FgjM1Journey.TickPrepOpen, retries: 2),
            S("prep_pick2", "名单表里勾选远征队四台", 40, null, TickPick),
            S("chk_cov2", "检查单提醒区按中继塔之后的覆盖重写（走出覆盖的格数变少 / 不再提醒）", 15, null, c => TickCoverage(c, false)),
            S("depart", "点“确认出发”（有在办工作时确认中断）：沿地形行军；点“跟随远征队”+ 3 倍速，跟着走到破碎都市", 260,
                c => FgjM1Journey.ClickUi(c, PrepHost, "DepartButton"), TickDepart, retries: 1),
            S("march_terr", "途中进入破碎都市所在的静默领地：顶部领地名称提示、“进入敌方领地”提醒、行军记下当前领地（DEBT-FG8GEN02-08 领地片段）", 10, null, TickTerritory),
            S("fc_uplink", "命令栏机器列表点搬运机：接入", 25, c => { c.SetInt("other", Team(c)[0]); FgjM1Journey.ClickMachineList(c, Team(c)[0]); }, TickUplinked, retries: 3),
            S("drive_evac", "开着搬运机（WASD）回撤离点", 150, null, TickDriveToEvac),
            S("evac_hold", "在撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3),
            S("evac_confirm", "点“确认撤离”：远征队从地点入口回到星球表面、状态“返程中”，到家才结算（FG8-EXP-05）", 30,
                c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"), TickEvacuated, retries: 1),
            S("team_hold", "远征队收得到远程命令（在与核心连通的覆盖里）时点行军条“待命”：队伍停在原地", 300, null, TickHold),
        };

        // ── 派遣检查单 ──────────────────────────────────────────────────────────────

        /// <summary>名单表里逐行勾选远征队（行上的勾选框，真实点击；不在可见区就像玩家一样滚滚轮）。</summary>
        private static StepOutcome TickPick(JourneyContext c)
        {
            List<int> roster = Team(c);
            int idx = c.GetInt(FgjM3Common.SK(c, "pick"));
            if (idx < roster.Count)
            {
                if (c.StepElapsed < (idx + 1) * 0.6)
                {
                    return StepOutcome.Wait;
                }
                Toggle t = FgjM1Journey.RowToggle(roster[idx]);
                if (t == null)
                {
                    return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"名单表里找不到 {Label(roster[idx])} 这一行");
                }
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>(PrepHost, "MachineList"), t))
                {
                    int w = c.GetInt(FgjM3Common.SK(c, "wheel")) + 1;
                    c.SetInt(FgjM3Common.SK(c, "wheel"), w);
                    return w > 60 ? StepOutcome.Fail($"{Label(roster[idx])} 这一行滚不进可见区：{JourneyInput.LastUiFailure}") : StepOutcome.Wait;
                }
                if (!t.value && !JourneyInput.ClickElement(t))
                {
                    return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Fail($"勾选 {Label(roster[idx])} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetInt(FgjM3Common.SK(c, "pick"), idx + 1);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < (roster.Count + 1) * 0.6)
            {
                return StepOutcome.Wait;
            }
            int[] sel = ExpeditionPrepPanelUIToolkit.Instance?.SelectedIdsForTests ?? Array.Empty<int>();
            string summary = JourneyInput.FindUitk<Label>(PrepHost, "SummaryLabel")?.text ?? string.Empty;
            string reasons = JourneyInput.FindUitk<Label>(PrepHost, "ReasonsLabel")?.text ?? string.Empty;
            return roster.All(sel.Contains) && sel.Length == roster.Count && reasons.Length == 0
                ? StepOutcome.Done($"勾选 {string.Join("、", roster.Select(Label))}：“{summary.Replace('\n', ' ')}”")
                : StepOutcome.Fail($"勾选后名单不对：选中 [{string.Join(",", sel)}]；“{summary}”“{reasons}”");
        }

        /// <summary>检查单提醒区（真实 UI 标签）与按真实行军路线采样的覆盖一致：第一次必须提醒走出覆盖；中继塔之后格数变少（或不再提醒）。</summary>
        private static StepOutcome TickCoverage(JourneyContext c, bool first)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            ExpeditionPrepPanelUIToolkit prep = ExpeditionPrepPanelUIToolkit.Instance;
            if (prep == null || !GameRoot.HomeValley.IsExpeditionPrepPanelOpen)
            {
                return StepOutcome.Fail("远征准备面板没开着");
            }
            string label = JourneyInput.FindUitk<Label>(PrepHost, "ChecklistRemindLabel")?.text ?? string.Empty;
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
            IReadOnlyList<Vector2> route = ExpeditionChecklistService.RouteProvider?.Invoke(s, snap.Target);
            if (route == null || route.Count < 2)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"目的地锚点 / 行军路线还没定（{ExpeditionSiteAnchors.Find(s, FracturedCityLayout.RegionId)?.State}）");
            }
            float outside = SignalCoverageService.RouteOutsideLength(HomeValleyLayout.RegionId, route, out Vector2 firstOut);
            ExpeditionChecklistService.Checklist chk = ExpeditionChecklistService.Build(s, prep.SelectedIdsForTests, snap);
            ExpeditionChecklistService.Item cov = chk.Reminders.FirstOrDefault(r => r.Code == "coverage");
            string want = outside > 0f ? GameText.Format("exp.check.rem.coverage", Mathf.CeilToInt(outside)) : null;
            string prefix = GameText.Get("exp.check.rem.coverage").Split('（')[0];
            float routeLen = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                routeLen += Vector2.Distance(route[i - 1], route[i]);
            }
            string all = string.Join(" / ", chk.Reminders.Select(r => r.Code));
            if (first)
            {
                if (outside <= 0f || want == null || cov.Text != want || !label.Contains(want))
                {
                    return StepOutcome.Fail($"第一次检查单应提醒走出覆盖：路线 {F(routeLen)} 格、走出覆盖 {F(outside)} 格；提醒 [{all}]；面板“{label.Replace('\n', ' ')}”");
                }
                c.Set("cov.out1", F(outside, "R"));
                c.Set("cov.first", $"{firstOut.x:0},{firstOut.y:0}");
                return StepOutcome.Done($"检查单提醒区：“{want}”（行军路线 {route.Count} 段 {F(routeLen)} 格，从 ({firstOut.x:0},{firstOut.y:0}) 起走出核心信号塔覆盖；其余提醒 [{all}]）");
            }
            float out1 = float.Parse(c.Get("cov.out1", "0"), CultureInfo.InvariantCulture);
            bool ok = outside < out1 - 1f && (want == null ? !label.Contains(prefix) && string.IsNullOrEmpty(cov.Code) : cov.Text == want && label.Contains(want));
            if (!ok)
            {
                return StepOutcome.Fail($"中继塔之后检查单没有按新覆盖重写：走出覆盖 {F(out1)} → {F(outside)} 格；提醒 [{all}]；面板“{label.Replace('\n', ' ')}”");
            }
            c.SetInt("banner0", WorldPlacesHudUIToolkit.Instance?.BannerShowCount ?? 0);
            c.SetInt("terrCue0", ExpeditionMarchService.TerritoryCueCount);
            return StepOutcome.Done(want == null
                ? $"中继塔之后整条路线都在与核心连通的覆盖里（走出覆盖 {F(out1)} → 0 格）：检查单不再提醒走出覆盖（其余提醒 [{all}]）"
                : $"中继塔之后检查单改写为“{want}”（走出覆盖 {F(out1)} → {F(outside)} 格；其余提醒 [{all}]）");
        }

        private static StepOutcome TickPrepClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !GameRoot.HomeValley.IsExpeditionPrepPanelOpen ? StepOutcome.Done("Esc 关闭远征准备面板") : StepOutcome.Retry("Esc 后远征准备面板还开着");
        }

        // ── 信号中继塔 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickRelayPlan(JourneyContext c)
        {
            CampaignState s = St;
            List<Vector2> route = ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId);
            if (route == null || route.Count < 2)
            {
                return StepOutcome.Fail("行军路线没定");
            }
            List<Vector2> dense = Densify(route, 4f);
            Vector2 core = CorePos;
            float edge = SignalCoverageService.TowerRadius * SignalCoverageService.HomeRadiusFactor(s);
            int last = -1;
            for (int i = 0; i < dense.Count; i++)
            {
                if (SignalCoverageService.Sample(HomeValleyLayout.RegionId, dense[i]).Covered && Vector2.Distance(dense[i], core) <= edge - 12f)
                {
                    last = i;
                }
            }
            for (int i = last; i >= 0; i--)
            {
                if (FindSpot(s, RelayType, dense[i], 6, null, out GridCell pivot) && HomeGridService.MapFor(s).IsExplored(pivot))
                {
                    P.SetActions(c, "m8r", new[] { P.ActB(new P.Placed { Key = "relay", Type = RelayType, Pivot = pivot, Rot = 0, Label = "信号中继塔" }) });
                    P.SetP(c, "relay", pivot);
                    return StepOutcome.Done($"中继塔放在 {Cell(pivot)}（离核心 {F(Vector2.Distance(new Vector2(pivot.X, pivot.Y), core))} 格，核心信号塔覆盖半径 {F(edge)}；路线第一处走出覆盖在 ({c.Get("cov.first")})）");
                }
            }
            return StepOutcome.Fail($"路线上覆盖里的最后一段找不到能放中继塔的地方（最后一个覆盖点下标 {last}/{dense.Count}）");
        }

        private static StepOutcome TickRelayBuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            GridCell pivot = P.P(c, "relay");
            if (!P.BuildingReady(s, RelayType, pivot, out BuildingRecord b))
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    BuildingRecord g = P.BuildingAtPivot(s, RelayType, pivot);
                    c.Log($"中继塔施工中：{g?.ConstructionState}；废料 {s.Scrap}；" +
                          $"{Label(c.GetInt("workerA"))} 在 {PosOf(c.GetInt("workerA"))}、{Label(c.GetInt("workerB"))} 在 {PosOf(c.GetInt("workerB"))}");
                }
                return StepOutcome.Wait;
            }
            if (c.GetLong(FgjM3Common.SK(c, "builtAt")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "builtAt"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "builtAt")) < 2500)
            {
                return StepOutcome.Wait; // 覆盖网按刷新间隔重建
            }
            List<Vector2> route = ExpeditionSiteAnchors.Route(s, FracturedCityLayout.RegionId);
            float outside = SignalCoverageService.RouteOutsideLength(HomeValleyLayout.RegionId, route, out _);
            float out1 = float.Parse(c.Get("cov.out1", "0"), CultureInfo.InvariantCulture);
            var relayPos = new Vector2(pivot.X, pivot.Y);
            SignalCoverageSample beyond = SignalCoverageService.Sample(HomeValleyLayout.RegionId, relayPos + (relayPos - CorePos).normalized * (SignalCoverageService.RelayTowerRadius * 0.8f));
            if (outside >= out1 - 1f || !beyond.Covered)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait
                    : StepOutcome.Fail($"中继塔建成后覆盖没有延伸：路线走出覆盖 {F(out1)} → {F(outside)} 格；塔外 {F(SignalCoverageService.RelayTowerRadius * 0.8f)} 格处覆盖 {beyond.Covered}（{beyond.SourceKind}）；电 {b.PowerState}");
            }
            return StepOutcome.Done($"中继塔 {Cell(pivot)} 建成（{b.PowerState}），接进与核心连通的覆盖网：路线走出覆盖 {F(out1)} → {F(outside)} 格；废料 {s.Scrap}");
        }

        /// <summary>
        /// 出发后跟着走到破碎都市（<see cref="FgjM1Journey.TickDeparted"/>：跟随远征队 + 3 倍速、到达后镜头跟进地点）。
        /// 途中“路线被截断”（有成员没能走到、原地待命，行军条写明原因）时像玩家一样在覆盖里点行军条“前进”重新下令（最多 3 次，报告逐次写明谁卡在哪）。
        /// </summary>
        internal static StepOutcome TickDepart(JourneyContext c) => TickDepart(c, 190);

        /// <summary>
        /// FG9-E2E-01：FGJ-M9A / FGJ-M9 共用（行军时限 <paramref name="marchLimit"/> 真实秒）。
        /// 复审修复（FG-GAP-136）：截断夹具已删——“路线被截断”时队伍在覆盖外、玩家发不了“前进”，由产品按最后的命令自动重试（ExpeditionMarchService.HaltRetryCount），旅程只等、只记次数。
        /// </summary>
        internal static StepOutcome TickDepart(JourneyContext c, double marchLimit)
        {
            CampaignState s = St;
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            if (m != null && m.Phase == ExpeditionMarchService.PhaseOutbound && m.Halted == ExpeditionMarchService.HaltRouteTruncated
                && c.GetInt("adv.n") < 3 && NowMs() - c.GetLong("adv.at") > 3000 && ExpeditionMarchService.CanCommand(s, out _))
            {
                UnityEngine.UIElements.Button adv = JourneyInput.FindUitk<UnityEngine.UIElements.Button>(WorldBarHost, "WorldMarchAdvance");
                if (adv != null && JourneyInput.IsClickable(adv) && JourneyInput.ClickElement(adv))
                {
                    ExpeditionMarchService.TryGetGoal(s, out Vector2 goal);
                    string who = string.Join("、", Team(c).Where(Alive).Select(id => $"{Label(id)} ({PosOf(id).x:0},{PosOf(id).y:0}) 离目的地 {F(Vector2.Distance(PosOf(id), goal))} 格"));
                    c.SetInt("adv.n", c.GetInt("adv.n") + 1);
                    c.SetLong("adv.at", NowMs());
                    c.Set("adv.log", c.Get("adv.log", string.Empty) + $"［第 {c.GetInt("adv.n")} 次：{who}］");
                    c.Log($"行军条“路线被截断”：点“前进”重新下令（第 {c.GetInt("adv.n")} 次；{who}）");
                }
            }
            if (c.Get("retry0") == null)
            {
                c.SetInt("retry0", ExpeditionMarchService.HaltRetryCount);
            }
            int retried = ExpeditionMarchService.HaltRetryCount - c.GetInt("retry0");
            if (retried > c.GetInt("retry.seen"))
            {
                c.SetInt("retry.seen", retried);
                c.Log($"行军“路线被截断”、队伍在覆盖外：行军服务按最后的“前进”自动重试（第 {retried} 次，FG-GAP-136 产品修复）");
            }
            return FgjM1Journey.TickDeparted(c, marchLimit);
        }

        // ── 行军途中：静默领地 ───────────────────────────────────────────────────────

        private static StepOutcome TickTerritory(JourneyContext c)
        {
            CampaignState s = St;
            int banner = (WorldPlacesHudUIToolkit.Instance?.BannerShowCount ?? 0) - c.GetInt("banner0");
            int cues = ExpeditionMarchService.TerritoryCueCount - c.GetInt("terrCue0");
            string terr = ExpeditionMarchService.Current(s)?.TerritoryId ?? string.Empty;
            return banner > 0 && cues > 0 && terr.Length > 0
                ? StepOutcome.Done($"途中进入阵营领地：顶部领地名称提示 {banner} 次（最近一次“{WorldPlacesHudUIToolkit.Instance?.BannerTitleText}”）、“进入敌方领地”提醒 {cues} 次、行军记下当前领地 {terr}")
                : StepOutcome.Fail($"途中没有进入领地的提示：名称提示 {banner} 次、提醒 {cues} 次、行军领地“{terr}”");
        }

        // ── 破碎都市：接入、开回撤离点、撤离 ──────────────────────────────────────────

        private static StepOutcome TickUplinked(JourneyContext c)
        {
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
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {Label(b)}（镜头直控）");
        }

        private static StepOutcome TickDriveToEvac(JourneyContext c)
        {
            FgjM2Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int b = c.GetInt("other");
            if (!FgjM1Journey.FcPos(b, out Vector2 at))
            {
                return StepOutcome.Fail("找不到接入的机器");
            }
            Vector2 evac = FracturedCityLayout.EntryEvac.Position;
            float d = Vector2.Distance(at, evac);
            if (d <= FracturedCityController.InteractRange - 0.8f)
            {
                JourneyInput.ReleaseKeys();
                return StepOutcome.Done($"{Label(b)} 开到撤离点（离撤离点 {d:F1} 格）");
            }
            if (!JourneyInput.Holding)
            {
                int stuck = c.GetInt(FgjM3Common.SK(c, "stuck"));
                float lastD = float.TryParse(c.Get(FgjM3Common.SK(c, "last")), NumberStyles.Float, CultureInfo.InvariantCulture, out float l) ? l : float.MaxValue;
                if (d > lastD - 0.2f)
                {
                    c.SetInt(FgjM3Common.SK(c, "stuck"), stuck + 1);
                }
                c.Set(FgjM3Common.SK(c, "last"), d.ToString("R", CultureInfo.InvariantCulture));
                FgjM1Journey.DriveToward(at, evac, 0.4, stuck % 4 == 3 ? 1 : 0);
            }
            return StepOutcome.Wait;
        }

        /// <summary>撤离后（FG8-EXP-05）：远征队从地点入口出现在星球表面、状态“返程中”，离家报告还在记（到家才结算）；按设置自动打开的离家报告点“关闭”。</summary>
        private static StepOutcome TickEvacuated(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded)
            {
                return c.StepElapsed > 10 ? StepOutcome.Retry("点“确认撤离”后破碎都市还在（" + FgjM1Journey.UiFail(c) + "）") : StepOutcome.Wait;
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            if (AwayReportPanelUIToolkit.IsOpen)
            {
                FgjM1Journey.ClickUi(c, "[AwayReportHost]", "AwayReportClose");
                return c.StepElapsed > 8 ? StepOutcome.Retry("离家报告关不掉（" + FgjM1Journey.UiFail(c) + "）") : StepOutcome.Wait;
            }
            CampaignState s = St;
            List<int> team = Team(c);
            List<string> bad = team.Where(id => !MachineRegistry.TryGetRecord(id, out MachineRecord r) || !r.IsAlive || r.RegionId != HomeValleyLayout.RegionId).Select(Label).ToList();
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            bool returning = m != null && m.Phase == ExpeditionMarchService.PhaseReturning;
            if (bad.Count > 0 || !returning || !AwayReportService.IsOpen(s))
            {
                return StepOutcome.Fail($"撤离后：不在星球上的成员 [{string.Join("、", bad)}]；行军阶段 {m?.Phase}（应为返程中）；离家报告还在记 {AwayReportService.IsOpen(s)}");
            }
            ExpeditionMarchService.TryTeamCenter(s, out Vector2 center, out int alive);
            UnityEngine.UIElements.Button triple = JourneyInput.FindUitk<UnityEngine.UIElements.Button>(WorldBarHost, "WorldSpeed3");
            if (triple != null && JourneyInput.IsClickable(triple))
            {
                JourneyInput.ClickElement(triple);
            }
            return StepOutcome.Done($"撤离：{alive} 台从破碎都市入口回到星球表面（队伍中心 ({center.x:0},{center.y:0})，离核心 {F(Vector2.Distance(center, CorePos))} 格），" +
                                    $"状态“{GameText.Get(ExpeditionMarchService.StatusTextKey(ExpeditionMarchService.StatusOf(s)))}”、离家报告到家才结算；点 3 倍速");
        }

        /// <summary>等远征队全体收得到远程命令（覆盖里、不在静默夜），点行军条“待命”。</summary>
        private static StepOutcome TickHold(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            if (m == null)
            {
                return StepOutcome.Fail("没有在外的远征队");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                if (m.Command == (int)ExpeditionMarchService.Command.Hold)
                {
                    ExpeditionMarchService.TryTeamCenter(s, out Vector2 center, out int alive);
                    c.Set("hold.at", $"{center.x.ToString("R", CultureInfo.InvariantCulture)},{center.y.ToString("R", CultureInfo.InvariantCulture)}");
                    return StepOutcome.Done($"点“待命”：{alive} 台停在 ({center.x:0},{center.y:0})（离核心 {F(Vector2.Distance(center, CorePos))} 格），状态“{GameText.Get(ExpeditionMarchService.StatusTextKey(ExpeditionMarchService.StatusOf(s)))}”");
                }
                return c.StepElapsed - c.GetLong(FgjM3Common.SK(c, "at")) / 1000.0 > 3 ? StepOutcome.Retry($"点“待命”后命令仍是 {(ExpeditionMarchService.Command)m.Command}（{ExpeditionMarchService.LastRejectReason}）") : StepOutcome.Wait;
            }
            if (!ExpeditionMarchService.CanCommand(s, out string reason))
            {
                if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                    ExpeditionMarchService.TryTeamCenter(s, out Vector2 center, out _);
                    c.Log($"还收不到远程命令（{reason}）：队伍中心 ({center.x:0},{center.y:0})，离核心 {F(Vector2.Distance(center, CorePos))} 格，返程中");
                }
                return StepOutcome.Wait;
            }
            UnityEngine.UIElements.Button hold = JourneyInput.FindUitk<UnityEngine.UIElements.Button>(WorldBarHost, "WorldMarchHold");
            if (hold == null || !JourneyInput.IsClickable(hold))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.ClickElement(hold))
            {
                return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点“待命”失败：" + JourneyInput.LastUiFailure);
            }
            c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
            c.SetLong(FgjM3Common.SK(c, "at"), (long)(c.StepElapsed * 1000));
            return StepOutcome.Wait;
        }
    }
}
