using System;
using System.Collections.Generic;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>突袭 HUD 一波到达后的战况（同一波的各支队伍合计；内核实时计数）。</summary>
    public struct RaidWaveFight
    {
        public int Groups;
        /// <summary>已展开攻城（或被拦截交战）的队伍数；0 = 刚到达、还没展开（下一步展开）。</summary>
        public int Engaged;
        public int Alive;
        public int Unfolded;
        public int Lost;
        public int Exited;
        public int Assault;
        public int Sabotage;
        public int Siege;
        /// <summary>撤退中的台数（内核里切到撤退的单位）。</summary>
        public int Retreating;
        public bool AnyRetreatOrder;
        public bool Intercepted;
        /// <summary>最早撤退的时间上限步（-1 = 没有）。</summary>
        public long LimitTick;
        /// <summary>还活着的单位重心（Alive &gt; 0 时有效）与队伍位置的平均（还没展开时用）。</summary>
        public Vector2 Centroid;
        public Vector2 GroupPos;
        public float ArriveX;
        public float ArriveY;
    }

    /// <summary>
    /// FG6-DEF-06（FG13 FGU-28“突袭预警与突袭 HUD：倒计时、方向、编成、剩余敌人”；FG06 FGR-DEF-042 观战的镜头跟随点）：突袭 HUD 的只读查询与文字。
    /// 预警条（<see cref="RaidDirectorService.IncomingWaves"/>）给出每一波；到达后这里按波合计还活着的敌人（职能、损失、撤退）——计数是战斗内核的一次查询（AOT，O(单位数)），
    /// 热更层只做 O(队伍数) 的合计；HUD 每 raid.hud.refresh_seconds 真实秒最多查一次（B18）。不改任何状态。
    /// </summary>
    public static class RaidHudService
    {
        private static readonly int[] RoleScratch = new int[4];

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        public static float RefreshSeconds => Math.Max(0.05f, Tuning("raid.hud.refresh_seconds", 0.25f));
        public static float PerfBudgetMs => Math.Max(0.01f, Tuning("raid.hud.perf_ms", 0.2f));

        /// <summary>有没有已到达、还没走完的突袭（家园或前哨站；观战按钮据此出现）。O(队伍数)。</summary>
        public static bool AnyActive(CampaignState s)
        {
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (IsActiveGroup(g))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>到达了还没撤走（含正在攻城）或在路上被拦截交战的突袭队伍。</summary>
        public static bool IsActiveGroup(TransitGroupRecord g) =>
            g != null && g.Kind == TransitGroupKind.Raid && (g.State == TransitGroupState.Arrived || g.Intercepted);

        /// <summary>某一波（<paramref name="wave"/>；0 = 全部到达的队伍）的战况。没有到达的队伍返回 false。</summary>
        public static bool TryWaveFight(CampaignState s, int wave, out RaidWaveFight f)
        {
            f = new RaidWaveFight { LimitTick = -1 };
            CombatSite site = HomeSite;
            long limit = WorldTransitSystem.TimeLimitTicks;
            double cx = 0, cy = 0, gx = 0, gy = 0;
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (!IsActiveGroup(g))
                {
                    continue;
                }
                if (wave > 0)
                {
                    RaidPlanRecord p = RaidDirectorService.FindPlan(s, g.PlanId);
                    if (p == null || p.Wave != wave)
                    {
                        continue;
                    }
                }
                f.Groups++;
                gx += g.PosX;
                gy += g.PosY;
                f.Intercepted |= g.Intercepted;
                f.AnyRetreatOrder |= g.SiegeRetreat;
                bool engaged = g.Engaged || g.Intercepted;
                if (!engaged)
                {
                    f.Alive += g.UnitCount; // 刚到达、下一步才展开：按队伍人数
                    continue;
                }
                f.Engaged++;
                int key = SiegeService.KeyOf(g);
                int alive = site != null ? site.CountSiegeGroup(key, RoleScratch) : 0;
                f.Alive += alive;
                f.Retreating += RoleScratch[0];
                f.Assault += RoleScratch[1];
                f.Sabotage += RoleScratch[2];
                f.Siege += RoleScratch[3];
                f.Unfolded += g.UnfoldedCount;
                f.Exited += g.ExitedCount;
                f.Lost += Math.Max(0, g.UnfoldedCount - alive - g.ExitedCount);
                if (site != null && alive > 0 && site.SiegeGroupCentroid(key, out Vector2 c) > 0)
                {
                    cx += c.x * alive;
                    cy += c.y * alive;
                }
                if (!g.SiegeRetreat && g.State == TransitGroupState.Arrived && limit > 0 && g.ArrivedAtTick >= 0)
                {
                    long t = g.ArrivedAtTick + limit;
                    f.LimitTick = f.LimitTick < 0 ? t : Math.Min(f.LimitTick, t);
                }
            }
            if (f.Groups == 0)
            {
                return false;
            }
            f.GroupPos = new Vector2((float)(gx / f.Groups), (float)(gy / f.Groups));
            int counted = f.Assault + f.Sabotage + f.Siege + f.Retreating;
            f.Centroid = counted > 0 ? new Vector2((float)(cx / counted), (float)(cy / counted)) : f.GroupPos;
            return true;
        }

        /// <summary>观战的镜头跟随点：全部到达的突袭里还活着的敌人的重心；都还没展开时 = 队伍位置。没有到达的突袭返回 false。</summary>
        public static bool FightFocus(CampaignState s, out Vector2 focus)
        {
            focus = Vector2.zero;
            if (!TryWaveFight(s, 0, out RaidWaveFight f))
            {
                return false;
            }
            focus = f.Alive > 0 && f.Engaged > 0 ? f.Centroid : f.GroupPos;
            return true;
        }

        /// <summary>全部到达的突袭还剩几台敌人（精简行 / 自检）。</summary>
        public static int TotalAlive(CampaignState s) => TryWaveFight(s, 0, out RaidWaveFight f) ? f.Alive : 0;

        /// <summary>正在被拆的墙（第一座；没有为空串）。</summary>
        public static string BreachName(CampaignState s)
        {
            CombatSite site = HomeSite;
            if (site == null)
            {
                return string.Empty;
            }
            int n = site.SiegeBreachCount;
            for (int i = 0; i < n; i++)
            {
                string id = SiegeService.BuildingIdOfUnit(s, site, site.SiegeBreachAt(i));
                BuildingRecord b = string.IsNullOrEmpty(id) ? null : HomeGridService.FindBuilding(s, id);
                if (b != null)
                {
                    return BuildingOps.NameOf(b);
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 已到达的一波（预警条同一行，FGU-28“剩余敌人”）：“第 1 波 · 正在攻打家园 · 来自东北 / 剩余敌人 12/20（▲… ⚡… ▣…）· 已损失 40% · 最晚 0:42 后撤退 / 正在拆：屏障”。
        /// 还没展开的写“正在展开”；被拦截的写“在路上被拦截”；撤退中写剩几台在离开。
        /// </summary>
        public static string ArrivedRowText(CampaignState s, in RaidWaveView v, long now)
        {
            RaidPlanRecord p = v.Lead;
            string wave = v.Forces > 1 ? GameText.Format("raid.warning.wave_merged", v.Wave, v.Forces) : GameText.Format("raid.warning.wave", v.Wave);
            string target = RaidDirectorService.TargetText(p);
            if (!TryWaveFight(s, v.Wave, out RaidWaveFight f))
            {
                return GameText.Format("raid.warning.row_arrived", wave, target, v.Units);
            }
            if (f.Engaged == 0)
            {
                return GameText.Format("raid.hud.unfolding", wave, target, f.Alive);
            }
            if (f.Intercepted && f.Engaged == f.Groups && !f.AnyRetreatOrder && f.LimitTick < 0)
            {
                return GameText.Format("raid.hud.intercepted", wave, f.Alive);
            }
            string head = GameText.Format("raid.hud.row_siege", wave, target, RaidDirectorService.WaveDirectionText(v));
            int pct = f.Unfolded > 0 ? Mathf.RoundToInt(100f * f.Lost / f.Unfolded) : 0;
            string body = GameText.Format("raid.hud.remaining", f.Alive, Math.Max(f.Unfolded, f.Alive), f.Assault, f.Sabotage, f.Siege, pct);
            string tail;
            if (f.AnyRetreatOrder && f.Retreating > 0)
            {
                tail = GameText.Format("raid.hud.retreating", f.Retreating);
            }
            else if (f.LimitTick >= 0)
            {
                tail = GameText.Format("raid.hud.limit_left", RaidDirectorService.Duration(f.LimitTick - now));
            }
            else
            {
                tail = string.Empty;
            }
            string breach = BreachName(s);
            string line2 = body + (tail.Length > 0 ? " · " + tail : string.Empty);
            return head + "\n" + line2 + (breach.Length > 0 ? "\n" + GameText.Format("raid.hud.breach", breach) : string.Empty);
        }

        /// <summary>
        /// 建造模式里的一行（DEBT-FG6DEF04-13：建造时也看得到突袭倒计时，且不挡建造栏）：有到达的突袭 = 剩余敌人；否则最近一波的倒计时与方向，多波时加“共 n 波”。
        /// 只有情报（还没预警）的计划不算进精简行。没有要显示的返回空串。
        /// </summary>
        public static string CompactText(CampaignState s, IReadOnlyList<RaidWaveView> waves, long now)
        {
            int incoming = 0;
            bool active = false;
            RaidWaveView first = default;
            bool haveFirst = false;
            foreach (RaidWaveView w in waves)
            {
                if (w.PlannedOnly)
                {
                    continue;
                }
                incoming++;
                active |= w.Arrived;
                if (!w.Arrived && (!haveFirst || w.ArrivalTick < first.ArrivalTick))
                {
                    first = w;
                    haveFirst = true;
                }
            }
            if (incoming == 0)
            {
                return string.Empty;
            }
            string text;
            if (active)
            {
                text = GameText.Format("raid.hud.compact_active", TotalAlive(s));
            }
            else
            {
                string wave = GameText.Format("raid.warning.wave", first.Wave);
                text = GameText.Format("raid.hud.compact_incoming", wave, RaidDirectorService.Duration(first.ArrivalTick - now), RaidDirectorService.WaveDirectionText(first));
            }
            return incoming > 1 ? text + GameText.Format("raid.hud.compact_more", incoming) : text;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
