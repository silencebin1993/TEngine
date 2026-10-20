using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.Objective;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M8 第二段（星球表面）：领地边缘建前哨站（中继、维修、炮塔）+ 家园投送站 → 精英残部（事件夹具）右键攻击打掉两台精英、攒下两个数据包 →
    /// 战略暂停里野外分拣一个（装进货舱）→ 这台阵亡（事件夹具）、货物掉落、队友捡回 → 前哨站建成 → 接取支线（事件夹具改登记成“在这里建立前哨站”）、在支线地点旁建第二个前哨站完成它。
    /// </summary>
    public static partial class FgjM8Journey
    {
        private const string Territory = "silent";
        private const string RemnantId = "remnant:silent:fgjm8";

        private static IEnumerable<JourneyStep> FieldSteps() => new[]
        {
            S("out_plan", "规划前哨站：在第一座中继塔的覆盖里、离核心 300 格以外、紧贴静默领地边界外侧（不在敌控领地里，免得持续被骚扰）、尽量靠近领地里的支线地点，放信号中继塔 + 维修无人机站 + 轻型炮塔；核心旁另放一座投送站", 15, null, TickOutpostPlan),
            S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("out_place", "建造栏依次选中继塔、维修无人机站、轻型炮塔在前哨站位置左键放下，再选投送站在核心旁放下（虚影；远处的格子镜头先平移过去）", 300, null, c => P.TickActions(c, "m8o", 40), retries: 2),
            S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),

            S("elite_fix", "事件夹具（DEBT-FG8E2E01-04）：静默领地的精英残部（2 台精英）刷新在待命的远征队附近——正式刷新要先收复领地、再等 7 个游戏日", 10, ApplyEliteFixture, TickEliteFixture),
            S("team_sel", "左键点一台远征队机器，再按住 Shift 左键加选其余几台", 40, null, TickSelectTeam),
            S("elite_move", "右键点精英残部那边的地面（右键地面 = 移动）：远征队走近、残部醒来就地展开成两台精英", 150, null, TickEliteEngaged),
            S("elite_killed", "右键点精英（右键敌人 = 攻击）：两台精英被打掉，各留下一个待分拣数据包（不弹面板，顶栏计数）", 300, null, TickEliteKilled),
            S("field_pause", "按暂停键（战略暂停：野外分拣只在暂停里做）", 10, c => FgjM1Journey.PressIf(GameActionId.TogglePause, !GameClock.Paused),
                c => c.StepElapsed < 0.5 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Done("战略暂停") : StepOutcome.Retry("没有暂停"), retries: 1),
            S("field_pick", "点顶栏“待分拣”打开数据包面板，点第一张“选这个”：选中的东西装进携带者的货舱，没选的转成技术数据", 25, null, TickFieldPick),
            S("field_close", "Esc 关闭待分拣面板，再按暂停键继续", 15, null, TickFieldClose),
            S("death_fix", "事件夹具（DEBT-FG8E2E01-04）：货舱里装着分拣物品的那台被打毁（内核里一次致命伤害，走正式阵亡事件）：货舱物品掉在原地、它带的数据包交给队友", 20, ApplyDeathFixture, TickDeath),
            S("pick_sel", "左键点离掉落处最近的队友", 25, c => { if (c.GetInt("pick.auto") == 0) FgjM1Journey.ClickMachine(c, "pickY"); },
                c => c.GetInt("pick.auto") == 1 ? StepOutcome.Done("掉落处已被走近的队友自动捡起，跳过") : FgjM1Journey.TickSelected(c, "pickY"), retries: 4),
            S("pickup", "右键点掉落处的地面（右键地面 = 移动）：队友走进拾取半径，把掉落的货装进自己的货舱", 120, null, TickPickup),
            S("outpost_built", "家园机器取料走过去施工：三座建成，自动归为一个前哨站（有名字、离核心 300 格以外），前哨站的中继塔接进覆盖网；投送站建成接电", 900, null, TickOutpostBuilt),
            S("q_fix", "事件夹具（DEBT-FG8E2E01-05）：静默领地支线地点按种子抽到的是“摧毁离地点最近的据点”（目标据点离地点 130 多格、等级 3 驻军 6，远在覆盖外）；改登记成同一地点的“在这里建立前哨站”支线（可接取）", 10, ApplyQuestFixture, TickQuestFixture),
            S("q_open", "按任务日志键（默认 J）打开任务日志", 10, c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, true),
                c => c.StepElapsed < 0.6 ? StepOutcome.Wait : MissionLogUIToolkit.IsOpen ? StepOutcome.Done("任务日志打开") : StepOutcome.Retry("任务日志没打开"), retries: 1),
            S("q_accept", "左键点这条支线，点“接取”：支线开始判定", 20, null, TickQuestAccept),
            S("q_close", "再按任务日志键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, false),
                c => c.StepElapsed < 0.6 ? StepOutcome.Wait : !MissionLogUIToolkit.IsOpen ? StepOutcome.Done("任务日志关闭") : StepOutcome.Retry("任务日志还开着"), retries: 1),
            S("q_plan", "规划第二个前哨站：在前哨站中继塔的覆盖里、支线地点旁（任务判定半径内）放一座信号中继塔", 10, null, TickQuestOutpostPlan),
            S("build_open4", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("q_place", "建造栏选信号中继塔，在支线地点旁左键放下", 90, null, c => P.TickActions(c, "m8q", 40), retries: 2),
            S("build_close4", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("q_done", "家园机器走过去建成：支线地点旁出现一个前哨站 → 支线判定完成（通知、奖励到账）", 600, null, TickQuestDone2),
        };

        // ── 前哨站规划 ──────────────────────────────────────────────────────────────

        private static WorldFeature SideSite(CampaignState s)
        {
            GridCell core = HomeGridService.CorePivot(s);
            var feats = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, core.X - 900, core.Y - 900, core.X + 900, core.Y + 900, feats);
            return feats.Where(f => f.Kind == WorldFeatureKind.SideSite && f.TerritoryId == Territory)
                .OrderBy(f => Vector2.Distance(new Vector2(f.X, f.Y), CorePos)).FirstOrDefault();
        }

        private static StepOutcome TickOutpostPlan(JourneyContext c)
        {
            CampaignState s = St;
            WorldFeature site = SideSite(s);
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(Territory);
            if (site == null || t == null)
            {
                return StepOutcome.Fail("静默领地里找不到支线地点");
            }
            GridCell relay1 = P.P(c, "relay");
            var r1 = new Vector2(relay1.X, relay1.Y);
            var sitePos = new Vector2(site.X, site.Y);
            var center = new Vector2(t.CenterX, t.CenterY);
            float relayR = SignalCoverageService.RelayTowerRadius * SignalCoverageService.HomeRadiusFactor(s);
            var avoid = new HashSet<long>();
            GridCell pr = default;
            float best = float.MaxValue;
            // 候选：第一座中继塔覆盖里（离它 120～190 格）、离核心 310 格以外、在静默领地边界外 12～40 格（敌控领地里的建筑群会被持续骚扰），挑离支线地点最近的。
            for (float d = 120f; d <= relayR - 10f; d += 6f)
            {
                for (int a = 0; a < 72; a++)
                {
                    float ang = a * 5f * Mathf.Deg2Rad;
                    Vector2 p = r1 + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    float toEdge = Vector2.Distance(p, center) - t.Radius;
                    float score = Vector2.Distance(p, sitePos);
                    if (score >= best || toEdge < 12f || toEdge > 40f || Vector2.Distance(p, CorePos) < 310f
                        || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered)
                    {
                        continue;
                    }
                    if (FindSpot(s, RelayType, p, 2, avoid, out GridCell g) && HomeGridService.MapFor(s).IsExplored(g))
                    {
                        pr = g;
                        best = score;
                    }
                }
            }
            if (best == float.MaxValue)
            {
                return StepOutcome.Fail($"第一座中继塔 {Cell(relay1)} 的覆盖里找不到离核心 300 格外、在静默领地边界外侧、能放前哨站中继塔的已探索空地");
            }
            // 支线目标据点（“离地点最近”的那个领地据点，与任务判定同一规则）的位置，规划远征队走过去的落点。
            OutpostRecord target = WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory)
                .OrderBy(o => Vector2.Distance(new Vector2(o.CellX, o.CellY), sitePos)).FirstOrDefault();
            if (target != null)
            {
                c.Log($"支线地点 {site.Id} 附近的领地据点：最近的 {target.OutpostId}（{target.CellX},{target.CellY}，离地点 {F(Vector2.Distance(new Vector2(target.CellX, target.CellY), sitePos))} 格，等级 {target.Tier}、驻军 {target.Garrison}）");
            }
            Reserve(avoid, RelayType, pr, 2);
            var prPos = new Vector2(pr.X, pr.Y);
            if (!FindSpot(s, RepairType, prPos + new Vector2(-6f, 0f), 8, avoid, out GridCell rb))
            {
                return StepOutcome.Fail($"前哨站 {Cell(pr)} 旁放不下维修无人机站");
            }
            Reserve(avoid, RepairType, rb, 2);
            if (!FindSpot(s, TurretType, prPos + new Vector2(6f, 0f), 8, avoid, out GridCell tu))
            {
                return StepOutcome.Fail($"前哨站 {Cell(pr)} 旁放不下轻型炮塔");
            }
            Reserve(avoid, TurretType, tu, 2);
            var coreAvoid = new HashSet<long>();
            if (!FindSpot(s, DropPodService.StationTypeId, CorePos + new Vector2(0f, -16f), 16, coreAvoid, out GridCell ds))
            {
                return StepOutcome.Fail("核心旁放不下投送站");
            }
            P.SetP(c, "op.relay", pr);
            P.SetP(c, "op.repair", rb);
            P.SetP(c, "op.turret", tu);
            P.SetP(c, "station", ds);
            c.Set("station.type", DropPodService.StationTypeId);
            c.Set("site.id", site.Id);
            P.SetActions(c, "m8o", new[]
            {
                P.ActB(new P.Placed { Key = "op.relay", Type = RelayType, Pivot = pr, Rot = 0, Label = "前哨站信号中继塔" }),
                P.ActB(new P.Placed { Key = "op.repair", Type = RepairType, Pivot = rb, Rot = 0, Label = "维修无人机站" }),
                P.ActB(new P.Placed { Key = "op.turret", Type = TurretType, Pivot = tu, Rot = 0, Label = "轻型炮塔" }),
                P.ActB(new P.Placed { Key = "station", Type = DropPodService.StationTypeId, Pivot = ds, Rot = 0, Label = "投送站" }),
            });
            float edgeOut = Vector2.Distance(prPos, center) - t.Radius;
            return StepOutcome.Done($"前哨站：中继塔 {Cell(pr)}（离核心 {F(Vector2.Distance(prPos, CorePos))} 格、离第一座中继塔 {F(Vector2.Distance(prPos, r1))} 格、在静默领地边界外 {F(edgeOut)} 格、" +
                                    $"离支线地点 {site.Id} {F(Vector2.Distance(prPos, sitePos))} 格），维修无人机站 {Cell(rb)}，轻型炮塔 {Cell(tu)}；投送站 {Cell(ds)}（离核心 {F(Vector2.Distance(new Vector2(ds.X, ds.Y), CorePos))} 格）");
        }

        // ── 精英残部 ────────────────────────────────────────────────────────────────

        private static Vector2 HoldAt(JourneyContext c)
        {
            string[] f = c.Get("hold.at", "0,0").Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 事件夹具：按 <see cref="TerritoryControlService"/> 精英残部刷新的同一个入口（<see cref="WorldOutpostSystem.SpawnRemnant"/>）在待命的远征队附近放一处 2 台精英的残部
        /// （正式刷新要收复领地后每 7 个游戏日，FGJ-M8 按轻量口径不演收复）。醒来、展开、交战、击毁、掉落、数据包全走正式系统。
        /// </summary>
        private static void ApplyEliteFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (WorldOutpostSystem.Find(s, RemnantId) != null)
            {
                return;
            }
            Vector2 hold = HoldAt(c);
            Vector2 away = (hold - CorePos).normalized;
            List<OutpostRecord> others = WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed).ToList();
            for (int k = 0; k < 36; k++)
            {
                float ang = (k % 12) * 30f * Mathf.Deg2Rad;
                float dist = 32f + (k / 12) * 16f;
                Vector2 dir = new Vector2(away.x * Mathf.Cos(ang) - away.y * Mathf.Sin(ang), away.x * Mathf.Sin(ang) + away.y * Mathf.Cos(ang));
                Vector2 p = hold + dir * dist;
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), 8);
                var gp = new Vector2(g.X, g.Y);
                // 离别的据点 / 侦察巢至少 60 格：交战只牵动这一处残部（远征队走近别的据点会叫醒它的驻军）。
                if (Vector2.Distance(gp, hold) < 24f || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, gp).Covered || !HomeGridService.MapFor(s).IsExplored(g)
                    || others.Any(o => Vector2.Distance(new Vector2(o.CellX, o.CellY), gp) < 60f))
                {
                    continue;
                }
                WorldOutpostSystem.SpawnRemnant(s, RemnantId, Territory, g, 1, 2);
                c.SetInt("pk0", LootPickService.Count(s));
                c.SetLong("tech0", (long)s.TechData);
                return;
            }
        }

        private static StepOutcome TickEliteFixture(JourneyContext c)
        {
            OutpostRecord o = WorldOutpostSystem.Find(St, RemnantId);
            return o != null && !o.Destroyed && o.Garrison == 2
                ? StepOutcome.Done($"事件夹具：精英残部 {o.OutpostId}（{o.CellX},{o.CellY}，离待命的远征队 {F(Vector2.Distance(new Vector2(o.CellX, o.CellY), HoldAt(c)))} 格，2 台精英）")
                : StepOutcome.Fail("待命的远征队附近找不到覆盖里、已探索、能走的地方放精英残部");
        }

        /// <summary>左键点第一台、Shift 左键加选其余存活的远征队成员（真实点击机器露出来的部分）。</summary>
        private static StepOutcome TickSelectTeam(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<int> team = Team(c).Where(Alive).ToList();
            IReadOnlyList<int> sel = GameRoot.HomeValley.SquadCommands.Selection;
            if (team.Count > 0 && sel.Count == team.Count && team.All(sel.Contains))
            {
                return StepOutcome.Done($"选择集 [{string.Join("、", sel.Select(Label))}]");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(FgjM3Common.SK(c, "k"));
            if (k >= team.Count)
            {
                return StepOutcome.Retry($"点完一遍后选择集是 [{string.Join("、", sel.Select(Label))}]，应为 [{string.Join("、", team.Select(Label))}]");
            }
            int id = team[k];
            if (!FgjM1Journey.HomeMarker(id, out HomeValleyMachineMarker m))
            {
                return StepOutcome.Fail($"{Label(id)} 不在家园战斗内核里");
            }
            if (!JourneyCommon.PanToward(m.Position, 0.15f))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.TryVisiblePointOf(m.View.transform, out Vector3 screen, out _))
            {
                FgjM3Common.PanTo(m.Position);
                return StepOutcome.Wait;
            }
            if (k == 0)
            {
                JourneyInput.ClickScreen(screen);
            }
            else
            {
                JourneyInput.ShiftClickScreen(screen);
            }
            c.SetInt(FgjM3Common.SK(c, "k"), k + 1);
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            return StepOutcome.Wait;
        }

        private static int Cmd(int logicId)
        {
            CombatSite site = Home;
            return site != null && site.TryGetMachineUnit(logicId, out int u) && site.TryGetCommand(u, out CombatCommand cmd) ? (int)cmd.Kind : -1;
        }

        /// <summary>右键地面（移动）：选中的远征队成员都接到移动命令。</summary>
        private static bool RightClickMove(JourneyContext c, string k, Vector2 ground)
        {
            if (c.GetInt(FgjM3Common.SK(c, k)) == 0)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(ground, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, k), 1);
                    c.SetLong(FgjM3Common.SK(c, k + ".at"), NowMs());
                }
                return false;
            }
            return NowMs() - c.GetLong(FgjM3Common.SK(c, k + ".at")) > 600;
        }

        private static StepOutcome TickEliteEngaged(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            OutpostRecord o = WorldOutpostSystem.Find(St, RemnantId);
            if (o == null)
            {
                return StepOutcome.Fail("精英残部不见了");
            }
            var at = new Vector2(o.CellX, o.CellY);
            Vector2 toward = (HoldAt(c) - at).normalized;
            if (!RightClickMove(c, "rc", at + toward * 6f))
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "moveChecked")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "moveChecked"), 1);
                List<int> team = Team(c).Where(Alive).ToList();
                int moving = team.Count(id => Cmd(id) == (int)CombatCommandKind.Move);
                c.Log($"右键地面：{moving}/{team.Count} 台接到移动命令（{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
                if (moving == 0)
                {
                    return StepOutcome.Retry($"右键地面后远征队没有接到移动命令（{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
            }
            if (o.Engaged && Home != null && Home.CountSiegeGroup(o.EngageKey) > 0)
            {
                c.SetInt("elite.units", Home.CountSiegeGroup(o.EngageKey));
                return StepOutcome.Done($"远征队走近：精英残部醒来就地展开成 {Home.CountSiegeGroup(o.EngageKey)} 台精英（迎战）");
            }
            return StepOutcome.Wait;
        }

        /// <summary>右键点离残部最近的活敌人（右键敌人 = 攻击），打死了就右键下一台；两台都没了 = 残部被击溃，数据包 +2。</summary>
        private static StepOutcome TickEliteKilled(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            OutpostRecord o = WorldOutpostSystem.Find(s, RemnantId);
            CombatSite site = Home;
            if (o == null || site == null)
            {
                return StepOutcome.Fail("精英残部 / 家园战斗内核不见了");
            }
            var at = new Vector2(o.CellX, o.CellY);
            int left = o.Engaged ? site.CountSiegeGroup(o.EngageKey) : 0;
            if ((int)(c.StepElapsed / 1.5) > c.GetInt(FgjM3Common.SK(c, "log")) && c.StepElapsed < 30)
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 1.5));
                int hu = site.FindNearestHostile(at, 60f);
                string hv = hu > 0 && site.Kernel.TryGetUnit(hu, out CombatUnitView ev) ? $"最近敌人 #{hu} {F(ev.Health, "0")}/{F(ev.MaxHealth, "0")} 命令 {ev.Command}" : "没有敌人";
                string mv = string.Join("、", Team(c).Select(id => site.TryGetMachineUnit(id, out int mu) && site.Kernel.TryGetUnit(mu, out CombatUnitView v)
                    ? $"{Label(id)} {F(v.Health, "0")} 命令{v.Command}→{v.CommandTarget} 下次开火 {F(v.NextFireAt, "0.0")}" : $"{Label(id)} 无单位"));
                c.Log($"[{F(c.StepElapsed, "0.0")}s] {hv}；{mv}");
                int hostiles = 0;
                foreach (int tid in Team(c))
                {
                    if (site.HostileNear(PosOf(tid), 30f))
                    {
                        hostiles++;
                    }
                }
                string awake = string.Join("、", WorldOutpostSystem.Outposts(s).Where(x => x != null && x.Engaged).Select(x => $"{x.OutpostId}({x.Kind},{site.CountSiegeGroup(x.EngageKey)})"));
                c.Log($"附近有敌人的成员 {hostiles}；迎战中的据点 [{awake}]；");
                c.Log($"残部：迎战 {o.Engaged}、摧毁 {o.Destroyed}、剩 {left}、驻军 {o.Garrison}；野外击毁 {FieldLootService.KillEvents} 次、击溃 {TerritoryControlService.RoutedCount}、收拢 {TerritoryControlService.RegroupCount}；" +
                      $"数据包 {LootPickService.Count(s)}；远征队 {string.Join("、", Team(c).Select(id => $"{Label(id)} 命令{Cmd(id)} {(MachineRegistry.TryGetRecord(id, out MachineRecord mr) ? F(mr.Health, "0") : "?")} 离残部 {F(Vector2.Distance(PosOf(id), at))}"))}");
            }
            if (!o.Destroyed && !o.Engaged)
            {
                // 收拢（附近没有己方机器满 regroup 秒）：像玩家一样再右键残部那边的地面走近。
                if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "reAt")) > 4000 && FgjM3Common.TryClick(at, 1))
                {
                    c.SetLong(FgjM3Common.SK(c, "reAt"), NowMs());
                    c.SetInt("elite.reapproach", c.GetInt("elite.reapproach") + 1);
                }
                return StepOutcome.Wait;
            }
            if (left > 0)
            {
                if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 3000)
                {
                    int u = site.FindNearestHostile(at, 60f);
                    if (u > 0 && site.TryGetUnitPosition(u, out double2 p) && FgjM3Common.TryClick(new Vector2((float)p.x, (float)p.y), 1))
                    {
                        c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                        c.SetInt("elite.rc", c.GetInt("elite.rc") + 1);
                        int attacking = Team(c).Where(Alive).Count(id => Cmd(id) == (int)CombatCommandKind.Attack);
                        if (attacking > c.GetInt("elite.attackMax"))
                        {
                            c.SetInt("elite.attackMax", attacking);
                        }
                    }
                }
                return StepOutcome.Wait;
            }
            int packets = LootPickService.Count(s) - c.GetInt("pk0");
            List<LootPackageRecord> mine = LootPickService.Packets(s).Where(p => p != null && p.Source == LootPickService.SourceElite).ToList();
            if (packets < 2)
            {
                return c.StepElapsed < 20 ? StepOutcome.Wait
                    : StepOutcome.Fail($"两台精英被打掉后数据包只多了 {packets} 个（{string.Join("、", mine.Select(p => p.PacketId + "@" + p.Holder))}）");
            }
            List<int> team = Team(c);
            string hp = string.Join("、", team.Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") + "/" + F(m.MaxHealth, "0") : "阵亡")}" : "?"));
            WorldBarHudUIToolkit hud = WorldBarHudUIToolkit.Instance;
            hud?.Refresh();
            string count = hud?.PacketCountText ?? string.Empty;
            bool holders = mine.All(p => team.Contains(p.Holder));
            if (!holders || count != GameText.Format("loot.pick.hud", LootPickService.Count(s)) || LootPickPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail($"数据包携带者不是远征队 / 顶栏计数“{count}”不对 / 面板被弹出（{LootPickPanelUIToolkit.IsOpen}）");
            }
            c.Set("elite.packets", string.Join(",", mine.Select(p => p.PacketId)));
            return StepOutcome.Done($"两台精英被打掉（右键攻击 {c.GetInt("elite.rc")} 次，最多 {c.GetInt("elite.attackMax")} 台同时在执行攻击命令）：数据包 +{packets}（{string.Join("、", mine.Select(p => $"{p.PacketId} 由 {Label(p.Holder)} 携带"))}），" +
                                    $"不弹面板、顶栏“{count}”；远征队血量 {hp}");
        }

        // ── 野外分拣 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickFieldPick(JourneyContext c)
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
            string packet = panel?.CurrentPacketId ?? string.Empty;
            if (c.Get(FgjM3Common.SK(c, "packet"), string.Empty).Length == 0)
            {
                if (c.StepElapsed < 1.0 || panel == null || packet.Length == 0)
                {
                    return StepOutcome.Wait;
                }
                LootPackageRecord p = LootPickService.Find(s, packet);
                if (p == null || !panel.ChooseEnabled(0))
                {
                    return StepOutcome.Fail($"野外（战略暂停中）不能分拣 {packet}：“{panel.ReasonText}”");
                }
                c.Set(FgjM3Common.SK(c, "packet"), packet);
                c.SetInt("field.holder", p.Holder);
                c.SetLong("field.tech0", (long)s.TechData);
                c.Set("field.where", panel.WhereText);
                if (!JourneyInput.ClickUitk("[LootPickPanelHost]", "LootPickChoose0"))
                {
                    return StepOutcome.Retry("点“选这个”失败：" + JourneyInput.LastUiFailure);
                }
                return StepOutcome.Wait;
            }
            string id = c.Get(FgjM3Common.SK(c, "packet"));
            LootHeldRecord held = LootPickService.Held(s).FirstOrDefault(h => h != null && h.HeldId == LootPickService.HeldPrefix + id);
            if (LootPickService.Find(s, id) != null || held == null)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Fail($"点“选这个”后数据包 {id} 还在 / 货舱里没有分拣物品（反馈“{panel?.MessageText}”）");
            }
            c.Set("held.id", held.HeldId);
            c.Set("held.item", held.ItemId);
            c.Set("held.kind", held.Kind);
            c.Set("held.target", held.TargetId ?? string.Empty);
            c.SetInt("held.amount", held.Amount);
            c.SetInt("held.machine", held.Machine);
            return StepOutcome.Done($"野外分拣数据包 {id}（去向“{c.Get("field.where")}”）：选中的 {ItemCatalog.NameOf(held.ItemId)} ×{held.Amount} 装进 {Label(held.Machine)} 的货舱（占 {held.Space}），" +
                                    $"技术数据 {c.GetLong("field.tech0")} → {(long)s.TechData}；反馈“{panel?.MessageText}”");
        }

        private static StepOutcome TickFieldClose(JourneyContext c)
        {
            if (LootPickPanelUIToolkit.IsOpen)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "esc")) > 800)
                {
                    c.SetLong(FgjM3Common.SK(c, "esc"), NowMs());
                    JourneyInput.PressAction(GameActionId.Cancel);
                }
                return StepOutcome.Wait;
            }
            if (GameClock.Paused)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "unpause")) > 1000)
                {
                    c.SetLong(FgjM3Common.SK(c, "unpause"), NowMs());
                    JourneyInput.PressAction(GameActionId.TogglePause);
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done("Esc 关闭待分拣面板、按暂停键继续");
        }

        // ── 阵亡掉落与捡回 ──────────────────────────────────────────────────────────

        /// <summary>事件夹具：装着分拣物品的那台在内核里受到一次致命伤害（与被精英打死同一条正式阵亡事件：MachineRegistry 阵亡翻转 → LootPickService / CargoService / 黑匣子）。</summary>
        private static void ApplyDeathFixture(JourneyContext c)
        {
            int x = c.GetInt("held.machine");
            c.Set("death.pos", $"{PosOf(x).x.ToString("R", CultureInfo.InvariantCulture)},{PosOf(x).y.ToString("R", CultureInfo.InvariantCulture)}");
            c.Set("death.packets", string.Join(",", LootPickService.Packets(St).Where(p => p != null && p.Holder == x).Select(p => p.PacketId)));
            if (Home != null && Home.TryGetMachineUnit(x, out int u))
            {
                Home.Kernel.Damage(u, 1e7f, 0);
            }
        }

        private static StepOutcome TickDeath(JourneyContext c)
        {
            CampaignState s = St;
            int x = c.GetInt("held.machine");
            if (Alive(x))
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(x)} 受到致命伤害后还活着");
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            string heldId = c.Get("held.id");
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, heldId);
            string[] f = c.Get("death.pos").Split(',');
            var at = new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
            bool stillHeldByDead = LootPickService.Held(s).Any(h => h != null && h.Machine == x);
            List<string> moved = c.Get("death.packets", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            List<string> passed = moved.Where(id => LootPickService.Find(s, id) is LootPackageRecord p && p.Holder != x && Team(c).Contains(p.Holder) && Alive(p.Holder)).ToList();
            LootHeldRecord picked = LootPickService.Held(s).FirstOrDefault(h => h != null && h.Machine != x && h.ItemId == c.Get("held.item") && Team(c).Contains(h.Machine));
            if (stillHeldByDead || passed.Count != moved.Count || (g == null && picked == null))
            {
                return StepOutcome.Fail($"{Label(x)} 阵亡后：货舱里还记着东西 {stillHeldByDead}；它带的数据包 [{string.Join(",", moved)}] 交给队友的 [{string.Join(",", passed)}]；地上的货 {(g == null ? "没有" : "有")}");
            }
            int y = Team(c).Where(id => id != x && Alive(id)).OrderBy(id => Vector2.Distance(PosOf(id), at)).FirstOrDefault();
            c.SetInt("pickY", y);
            if (g == null)
            {
                c.SetInt("pick.auto", 1);
                c.SetInt("pickY", picked.Machine);
                return StepOutcome.Done($"{Label(x)} 阵亡：货舱里的 {ItemCatalog.NameOf(c.Get("held.item"))} 掉在原地，随即被走近的 {Label(picked.Machine)} 自动捡起（拾取半径 {F(CargoService.PickupRadius)} 格）；它带的数据包 [{string.Join(",", moved)}] 交给队友");
            }
            return StepOutcome.Done($"{Label(x)} 阵亡：货舱里的 {ItemCatalog.NameOf(c.Get("held.item"))} ×{g.Amount} 掉在原地 ({g.Position.x:0},{g.Position.y:0})（通知写明位置），它带的数据包 [{string.Join(",", moved)}] 交给队友；" +
                                    $"离掉落处最近的队友 {Label(y)}（{F(Vector2.Distance(PosOf(y), g.Position))} 格）");
        }

        private static StepOutcome TickPickup(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int y = c.GetInt("pickY");
            string item = c.Get("held.item");
            if (c.GetInt("pick.auto") == 1)
            {
                return StepOutcome.Done($"已由 {Label(y)} 自动捡回");
            }
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, c.Get("held.id"));
            LootHeldRecord picked = LootPickService.Held(s).FirstOrDefault(h => h != null && h.ItemId == item && Team(c).Contains(h.Machine) && Alive(h.Machine));
            if (picked != null && (g == null || g.Amount <= 0))
            {
                return StepOutcome.Done($"{Label(picked.Machine)} 走到掉落处，把 {ItemCatalog.NameOf(item)} ×{picked.Amount} 装进自己的货舱（地上的货没了）");
            }
            if (g == null)
            {
                return StepOutcome.Fail("掉落的货不见了，也没有队友的货舱里有它（被敌方回收？）");
            }
            if (!RightClickMove(c, "rc", g.Position))
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "checked")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "checked"), 1);
                if (Cmd(y) != (int)CombatCommandKind.Move && Vector2.Distance(PosOf(y), g.Position) > CargoService.PickupRadius)
                {
                    return StepOutcome.Retry($"右键掉落处后 {Label(y)} 没有接到移动命令（{GameRoot.HomeValley?.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
            }
            return StepOutcome.Wait;
        }

        // ── 前哨站建成 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickOutpostBuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            var parts = new[] { ("op.relay", RelayType), ("op.repair", RepairType), ("op.turret", TurretType), ("station", DropPodService.StationTypeId) };
            var ready = new List<BuildingRecord>();
            foreach ((string key, string type) in parts)
            {
                if (P.BuildingReady(s, type, P.P(c, key), out BuildingRecord b))
                {
                    ready.Add(b);
                }
            }
            if (ready.Count < parts.Length)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"施工中：已建成 {ready.Count}/{parts.Length}（{string.Join("、", ready.Select(b => b.BuildingTypeId))}）；废料 {s.Scrap}；" +
                          $"{Label(c.GetInt("workerA"))} 在 ({PosOf(c.GetInt("workerA")).x:0},{PosOf(c.GetInt("workerA")).y:0})、{Label(c.GetInt("workerB"))} 在 ({PosOf(c.GetInt("workerB")).x:0},{PosOf(c.GetInt("workerB")).y:0})");
                }
                return StepOutcome.Wait;
            }
            GridCell pr = P.P(c, "op.relay");
            var prPos = new Vector2(pr.X, pr.Y);
            string oid = OwnOutpostService.OutpostIdAt(s, prPos);
            OwnOutpostRecord o = string.IsNullOrEmpty(oid) ? null : OwnOutpostService.Find(s, oid);
            IReadOnlyList<BuildingRecord> members = o != null ? OwnOutpostService.Members(s, o.OutpostId) : Array.Empty<BuildingRecord>();
            bool all3 = ready.Take(3).All(b => members.Any(x => x.BuildingId == b.BuildingId));
            BuildingRecord station = ready[3];
            if (o == null || !all3 || OwnOutpostService.DistanceFromCore(s, o) <= 300f)
            {
                return c.StepElapsed < 600 ? StepOutcome.Wait : StepOutcome.Fail($"三座建成后没有归为一个离核心 300 格以外的前哨站（{oid}，成员 {members.Count}）");
            }
            if (station.PowerState != BuildingPowerState.Powered)
            {
                return StepOutcome.Fail($"投送站建成但没电（{station.PowerState}）");
            }
            return StepOutcome.Done($"前哨站“{OwnOutpostService.NameOf(o)}”（{o.OutpostId}，离核心 {F(OwnOutpostService.DistanceFromCore(s, o))} 格）：中继塔 / 维修无人机站 / 轻型炮塔 建成（电：{string.Join("、", ready.Take(3).Select(b => b.BuildingTypeId + " " + b.PowerState))}）；" +
                                    $"投送站 {station.BuildingId} 建成有电；废料 {s.Scrap}");
        }

        // ── 支线 ────────────────────────────────────────────────────────────────────

        private static ObjectiveRecord SideQuest(JourneyContext c) =>
            St.ObjectiveRecords?.FirstOrDefault(r => r != null && r.SiteId == c.Get("site.id") && CampaignObjectiveTracker.IsQuestRecord(r));

        /// <summary>
        /// 事件夹具：在静默领地的支线地点上按 FG8-QST-01 的自检入口（<see cref="CampaignObjectiveTracker.OfferSideForTests"/>，跳过按哈希抽取，其余与发现地点时相同）
        /// 登记“在这里建立前哨站”（side.outpost）。按种子抽到的“摧毁离地点最近的据点”目标离地点 130 多格、等级 3 驻军 6，M8 开局的四台机器打不下来（轻量口径不为凑剧情改数值）。
        /// 接取、判定、完成、奖励全走正式系统。
        /// </summary>
        private static void ApplyQuestFixture(JourneyContext c)
        {
            CampaignState s = St;
            WorldFeature site = SideSite(s);
            if (site == null)
            {
                return;
            }
            c.Set("site.id", site.Id);
            c.Set("quest.drawn", CampaignObjectiveTracker.PickSide(s, site)?.Id ?? string.Empty);
            ObjectiveRecord r = CampaignObjectiveTracker.OfferSideForTests(s, site, "side.outpost");
            c.Set("quest.id", r?.ObjectiveId ?? string.Empty);
        }

        private static StepOutcome TickQuestFixture(JourneyContext c)
        {
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(St, c.Get("quest.id"));
            Vector2? at = r != null ? CampaignObjectiveTracker.QuestLocation(St, r) : null;
            return r != null && r.State == ObjectiveState.Available && r.DefId == "side.outpost" && at.HasValue
                ? StepOutcome.Done($"事件夹具：支线地点 {c.Get("site.id")}（按种子抽到 {c.Get("quest.drawn")}）登记为可接取的“{CampaignObjectiveTracker.QuestTitle(r)}”（{r.DefId}，地点 ({at.Value.x:0},{at.Value.y:0})，判定半径 {F(CampaignObjectiveTracker.OutpostRadius)} 格）")
                : StepOutcome.Fail($"支线夹具没有生效（{r?.DefId} {r?.State}）");
        }

        private static StepOutcome TickQuestOutpostPlan(JourneyContext c)
        {
            CampaignState s = St;
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(s, c.Get("quest.id"));
            Vector2? at = r != null ? CampaignObjectiveTracker.QuestLocation(s, r) : null;
            if (!at.HasValue)
            {
                return StepOutcome.Fail("支线没有地点");
            }
            GridCell r2 = P.P(c, "op.relay");
            var relay2 = new Vector2(r2.X, r2.Y);
            float relayR = SignalCoverageService.RelayTowerRadius * SignalCoverageService.HomeRadiusFactor(s);
            float radius = CampaignObjectiveTracker.OutpostRadius - 15f;
            GridCell best = default;
            float bestD = float.MaxValue;
            for (float d = 0f; d <= radius; d += 4f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    Vector2 p = at.Value + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    float fromRelay = Vector2.Distance(p, relay2);
                    if (fromRelay > relayR - 8f || fromRelay >= bestD || Vector2.Distance(p, CorePos) < 310f || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered)
                    {
                        continue;
                    }
                    if (FindSpot(s, RelayType, p, 2, null, out GridCell g) && HomeGridService.MapFor(s).IsExplored(g))
                    {
                        best = g;
                        bestD = fromRelay;
                    }
                }
            }
            if (bestD == float.MaxValue)
            {
                return StepOutcome.Fail($"支线地点 ({at.Value.x:0},{at.Value.y:0}) 旁 {F(radius)} 格内找不到前哨站中继塔 {Cell(r2)} 覆盖里、已探索、能放中继塔的空地");
            }
            P.SetActions(c, "m8q", new[] { P.ActB(new P.Placed { Key = "q.relay", Type = RelayType, Pivot = best, Rot = 0, Label = "支线地点旁的信号中继塔" }) });
            P.SetP(c, "q.relay", best);
            return StepOutcome.Done($"第二个前哨站：信号中继塔 {Cell(best)}（离支线地点 {F(Vector2.Distance(new Vector2(best.X, best.Y), at.Value))} 格、离前哨站中继塔 {F(bestD)} 格、离核心 {F(Vector2.Distance(new Vector2(best.X, best.Y), CorePos))} 格）");
        }

        private static StepOutcome TickQuestDone2(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(s, c.Get("quest.id"));
            GridCell g = P.P(c, "q.relay");
            bool built = P.BuildingReady(s, RelayType, g, out BuildingRecord b);
            if (r == null)
            {
                return StepOutcome.Fail("支线不见了");
            }
            if (r.State == ObjectiveState.Failed)
            {
                return StepOutcome.Fail($"支线失败：{r.FailReasonKey}");
            }
            if (r.State != ObjectiveState.Completed)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    BuildingRecord ghost = P.BuildingAtPivot(s, RelayType, g);
                    c.Log($"支线进行中：中继塔 {(built ? "建成" : ghost != null ? ghost.ConstructionState.ToString() : "没有")}；前哨站 {OwnOutpostService.All(s).Count} 个；" +
                          $"{Label(c.GetInt("workerA"))} 在 ({PosOf(c.GetInt("workerA")).x:0},{PosOf(c.GetInt("workerA")).y:0})、{Label(c.GetInt("workerB"))} 在 ({PosOf(c.GetInt("workerB")).x:0},{PosOf(c.GetInt("workerB")).y:0})");
                }
                return StepOutcome.Wait;
            }
            string oid = OwnOutpostService.OutpostIdAt(s, new Vector2(g.X, g.Y));
            return StepOutcome.Done($"中继塔 {Cell(g)} 建成（{(built ? b.PowerState.ToString() : "?")}），归为“{OwnOutpostService.NameOf(s, oid)}”（{oid}）：支线“{CampaignObjectiveTracker.QuestTitle(r)}”完成（{CampaignObjectiveTracker.ProgressText(s, r)}；废料 {s.Scrap}）");
        }

        private static StepOutcome TickQuestAccept(JourneyContext c)
        {
            MissionLogUIToolkit ml = MissionLogUIToolkit.Instance;
            string id = c.Get("quest.id");
            ObjectiveRecord r = CampaignObjectiveTracker.FindQuest(St, id);
            if (ml == null || r == null)
            {
                return StepOutcome.Fail("任务日志 / 支线记录不见了");
            }
            if (r.State == ObjectiveState.Active)
            {
                return StepOutcome.Done($"点“接取”：支线“{CampaignObjectiveTracker.QuestTitle(r)}”进行中（{CampaignObjectiveTracker.ProgressText(St, r)}；{CampaignObjectiveTracker.WhereText(St, r)}）；反馈“{ml.MessageText}”");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 800)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            if (ml.Selected != id)
            {
                for (int col = 0; col < 3; col++)
                {
                    for (int i = 0; i < ml.RowCount(col); i++)
                    {
                        if (ml.RowView(col, i).Id == id)
                        {
                            return JourneyInput.ClickElement(ml.RowElement(col, i)) || JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点支线那一行失败：" + JourneyInput.LastUiFailure);
                        }
                    }
                }
                return c.StepElapsed > 6 ? StepOutcome.Fail($"任务日志里没有这条支线的行（{id}）") : StepOutcome.Wait;
            }
            return JourneyInput.ClickElement(ml.AcceptButton) || JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点“接取”失败：" + JourneyInput.LastUiFailure);
        }
    }
}
