using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-05：家园之外的两件事——
    /// - <b>拦截 / 追击</b>（承接 DEBT-FG6DEF04-03，FGR-DEF-023“行进途中可以被拦截”、FGR-DEF-032“途中可以被追击”）：行进中 / 撤退中的突袭聚合体，
    ///   己方机器走到 siege.intercept_radius_cells 以内时就地展开成战斗单位（与到达展开同一套编成），按原型突袭者的规则就地交战；
    ///   附近没有己方机器满 siege.regroup_seconds 后，幸存者收拢回聚合体继续走（人数按幸存者）；全灭 = 被全歼。展开期间聚合体不推进。
    /// - <b>驻防机器守点交战</b>（承接 DEBT-FG4ECO07-02 的“守点交战”，FGR-DEF-043“按教义交战”）：驻防岗、已在驻防点待命的机器在内核里标上驻防点与 siege.guard_radius_cells，
    ///   空闲时朝驻防点半径内最近的敌人交战（射程内原地打、射程外在半径内靠近、没敌人回驻防点；与编队攻击同一套开火结算）；有命令时听命令。驻防点进快照（存读档一致）。
    /// 热更层每 siege.sync_seconds O(行进队伍数 + 驻防机器数)，查询是内核 O(单位数) 的一次调用。
    /// </summary>
    public static partial class SiegeService
    {
        public static int InterceptCount { get; private set; }
        public static int InterceptRegroups { get; private set; }
        public static long GuardShots { get; private set; }

        private static void StepIntercepts(CampaignState state, CombatSite site, long ticksBefore, int worldHz)
        {
            long every = Math.Max(1, (long)Math.Round(SiegeCatalog.SyncSeconds * worldHz));
            if (ticksBefore % every != 0)
            {
                return;
            }
            TransitGroupRecord[] groups = state.Raids?.InTransit;
            if (groups == null || groups.Length == 0)
            {
                return;
            }
            float radius = SiegeCatalog.InterceptRadius;
            double arrival = WorldTransitSystem.ArrivalRadius;
            List<TransitGroupRecord> finished = null;
            for (int i = 0; i < groups.Length; i++)
            {
                TransitGroupRecord g = groups[i];
                if (g == null || g.Kind != TransitGroupKind.Raid)
                {
                    continue;
                }
                var at = new Vector2((float)g.PosX, (float)g.PosY);
                if (g.Intercepted)
                {
                    int key = KeyOf(g);
                    int alive = site.CountSiegeGroup(key);
                    if (alive == 0)
                    {
                        (finished ??= new List<TransitGroupRecord>()).Add(g);
                        continue;
                    }
                    bool stalemate = g.InterceptTick >= 0 && GameClock.Ticks - g.InterceptTick >= GameClock.TicksFor(SiegeCatalog.InterceptMaxSeconds);
                    if (!stalemate && site.FindNearestMachine(at, radius * 1.5f) > 0)
                    {
                        g.LastContactTick = GameClock.Ticks;
                    }
                    else if (stalemate || (g.LastContactTick >= 0 && GameClock.Ticks - g.LastContactTick >= GameClock.TicksFor(SiegeCatalog.RegroupSeconds)))
                    {
                        Regroup(site, g, alive, stalemate);
                    }
                    continue;
                }
                double toTarget = Math.Sqrt((g.TargetX - g.PosX) * (g.TargetX - g.PosX) + (g.TargetY - g.PosY) * (g.TargetY - g.PosY));
                // 快到目标的不拦截（照常到达展开、用流场攻城）；刚收拢的不马上再拦（僵持到点收拢后要走出这片）。
                bool moving = g.State == TransitGroupState.Retreating
                              || (g.State == TransitGroupState.Marching && toTarget > Math.Max(arrival * 2.0, SiegeCatalog.InterceptMinTargetCells));
                if (moving && g.InterceptTick >= 0 && GameClock.Ticks - g.InterceptTick < GameClock.TicksFor(SiegeCatalog.InterceptMaxSeconds))
                {
                    continue;
                }
                if (!moving || g.UnitCount <= 0)
                {
                    continue;
                }
                if (site.FindNearestMachine(at, radius) > 0)
                {
                    Intercept(state, site, g);
                }
            }
            if (finished != null)
            {
                foreach (TransitGroupRecord g in finished)
                {
                    RaidPlanRecord p = RaidDirectorService.FindPlan(state, g.PlanId);
                    if (p != null)
                    {
                        p.EndReason = RaidDirectorService.EndDestroyed;
                    }
                    Annihilations++;
                    Hook(GuidanceHooks.SiegeFirstDestroyed);
                    NotificationCenter.Post("raid_destroyed", GameText.Format("siege.notify.destroyed", g.UnfoldedCount), new Vector3((float)g.PosX, 0f, (float)g.PosY));
                    WorldTransitSystem.RemoveDestroyed(state, g);
                }
                StandingRuleService.NotifyRaidArrived(state);
            }
        }

        /// <summary>行进中的聚合体被拦截：就地展开（人数按聚合体现在的人数，编成按原编成的顺序截取）、聚合体停住。</summary>
        public static int Intercept(CampaignState state, CombatSite site, TransitGroupRecord g)
        {
            int key = KeyOf(g);
            if (key <= 0 || g.Intercepted)
            {
                return 0;
            }
            GridCell at = NearestPassable(NavService.CellOf(g.PosX, g.PosY), 8);
            BuildUnitList(g);
            if (UnitScratch.Count > g.UnitCount)
            {
                UnitScratch.RemoveRange(g.UnitCount, UnitScratch.Count - g.UnitCount);
            }
            var home = new Vector2(at.X, at.Y);
            int step = Math.Max(1, Mathf.RoundToInt(SiegeCatalog.SpawnSpacing));
            int placed = 0;
            int next = 0;
            for (int ring = 0; ring <= 32 && next < UnitScratch.Count; ring++)
            {
                for (int dy = -ring; dy <= ring && next < UnitScratch.Count; dy++)
                {
                    for (int dx = -ring; dx <= ring && next < UnitScratch.Count; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring || !NavService.PassableNow(at.X + dx * step, at.Y + dy * step, BinGames.Sim.Nav.NavConst.ClassHostile))
                        {
                            continue;
                        }
                        (RaidUnitDef def, bool elite) = UnitScratch[next++];
                        int unit = SpawnOne(site, key, new Vector2(at.X + dx * step, at.Y + dy * step), home, def, elite);
                        if (unit > 0)
                        {
                            site.SetSiegeUnitMode(unit, CombatSiegeMode.Skirmish);
                            placed++; // 复审修复（P2）：只数真的生成了的（展开台数不虚高；全部生成失败时 placed = 0，下面不当成拦截）
                        }
                        else
                        {
                            LastProblem = "拦截展开生成失败（战斗内核已满？）：" + (def?.EnemyTypeId ?? "(无编成)");
                        }
                    }
                }
            }
            if (placed == 0)
            {
                return 0; // 一台都没生成出来（内核满）：不算拦截，聚合体照常行进（否则下一次对账“内核里没有单位”会被误记成被全歼）
            }
            g.Intercepted = true;
            g.UnfoldedCount = placed;
            g.LastContactTick = GameClock.Ticks;
            g.InterceptTick = GameClock.Ticks;
            InterceptCount++;
            NotificationCenter.Post("raid_siege", GameText.Format("siege.notify.intercepted", placed), new Vector3(at.X, 0f, at.Y));
            return placed;
        }

        /// <summary>拦截交战结束（附近没有己方机器了）：幸存者收拢回聚合体，人数按幸存者，接着原来的行进 / 撤退。</summary>
        private static void Regroup(CombatSite site, TransitGroupRecord g, int alive, bool stalemate)
        {
            site.DespawnSiegeGroup(KeyOf(g));
            g.UnitCount = Math.Max(1, alive);
            g.Intercepted = false;
            g.LastContactTick = -1;
            // 机器走开了 → 随时可以再被拦；互相够不着僵持到点 → 收拢后一段时间内不再拦（让它走出这片，不在原地反复展开）。
            g.InterceptTick = stalemate ? GameClock.Ticks : -1;
            InterceptRegroups++;
        }

        // ─────────────────────────────── 驻防机器守点交战 ───────────────────────────────

        private static void StepGuards(CampaignState state, CombatSite site)
        {
            if (site == null || site.IsDisposed)
            {
                return;
            }
            float radius = SiegeCatalog.GuardRadius;
            long shots = site.SiegeStats.GuardShots;
            GuardShots = shots;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                bool guard = false;
                Vector2 post = default;
                if (m.Role == MachineRole.Garrison && (HomeValleyWorkOrders.TakenByPlayer == null || !HomeValleyWorkOrders.TakenByPlayer(m.LogicId)))
                {
                    WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(state, m.LogicId);
                    if (o != null && o.Kind == WorkOrderKind.Garrison && o.State == WorkOrderState.InProgress)
                    {
                        guard = true; // 已在驻防点待命（还在路上 / 被接管暂停 / 玩家正在用 = 不守点，FGR-BASE-020）
                        post = HomeValleyWorkOrders.ResolveWorkPosition(state, o);
                    }
                }
                site.SetMachineGuard(m.LogicId, post, guard ? radius : 0f);
            }
        }
    }
}
