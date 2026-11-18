using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-02（FG10 FGR-EVT-011～013，FGT-EVT-003）：掘进蠕虫——事件导演的 worm 处理器与它在世界步里的推进。
    /// - 导演负责何时、推迟（静默夜 / 剧情突袭 / 2 日一次）、预警计时（破土前 30 秒）、通知、历史、三选一；处理器负责落点、路线、生成、建筑受创、掉落、死亡与离开。
    /// - Prepare：震动值请求过的地点 → 最集中处、按（世界种子, 实例序号）定路线（之字形穿过采矿区、首尾相接）与节数（8～12），不依赖固定坐标（B25）。
    /// - OnWarn：裂纹沿路线蔓延（地图画路线、通知写原因）。OnStart：在星球战斗内核里生成分节链（各节 = 普通敌方单位，链在 Main/Sim 推进），该地点震动值归零。
    /// - 每个世界步 O(蠕虫数)：新倒下的节按 fg.TbLootDrop 来源 worm 掉落；钻出地面时压到 / 钻过的地表建筑受重创（每次钻出每座一次）；
    ///   击毁全部节或累计伤害达 worm.kill_damage_frac → 其余节一起倒下并掉落、向导演报告 success（导演给三选一）；时限到（导演 expired）→ 潜回地下、潜入时长后移除。
    /// 推进只看步序号与存档（链状态在内核快照段 39、处理器状态在 <see cref="VibrationState.Worms"/>），与观察无关（FGR-BASE-021）。
    /// </summary>
    public static class WormEventService
    {
        public const string EventId = "worm";
        public const int StateWarned = 0;
        public const int StateActive = 1;
        public const int StateLeaving = 2;
        private const uint SaltRoute = 0x57524D31u; // "WRM1"

        private static readonly List<Vector2> RouteScratch = new List<Vector2>(CombatWormConst.MaxRoute);
        private static readonly List<int> SegScratch = new List<int>(CombatWormConst.MaxSegments);
        private static readonly HashSet<string> HitScratch = new HashSet<string>(StringComparer.Ordinal);
        private static CombatSite _labeledSite;

        public static int Breakouts { get; private set; }

        /// <summary>事件导演的 worm 处理器（GameRoot 登记；自检各自登记）。</summary>
        public sealed class Handler : EventDirectorHandler
        {
            public override string EventId => WormEventService.EventId;
            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => WormEventService.Prepare(s, r);
            public override void OnWarn(CampaignState s, EventDirectorInstanceRecord r) => WormEventService.OnWarn(s, r);
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => WormEventService.OnStart(s, r);
            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) => WormEventService.OnEnd(s, r, outcome);
        }

        public static void ResetSessionState()
        {
            _labeledSite = null;
            Breakouts = 0;
        }

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        public static IReadOnlyList<WormRecord> Worms(CampaignState s) => VibrationService.StateOf(s)?.Worms ?? Array.Empty<WormRecord>();

        public static WormRecord Find(CampaignState s, int serial)
        {
            foreach (WormRecord w in Worms(s))
            {
                if (w != null && w.Serial == serial)
                {
                    return w;
                }
            }
            return null;
        }

        /// <summary>有蠕虫在预警 / 地面上，或有地点已请求、还没破土（同一时刻只安排一处，FGR-EVT-002 重大威胁）。</summary>
        public static bool HasPendingOrActive(CampaignState s)
        {
            foreach (WormRecord w in Worms(s))
            {
                if (w != null && w.State != StateLeaving)
                {
                    return true;
                }
            }
            foreach (VibrationSiteRecord r in VibrationService.Sites(s))
            {
                if (r != null && r.Level == VibrationService.LevelRequested)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool ActiveAt(CampaignState s, string siteId)
        {
            foreach (WormRecord w in Worms(s))
            {
                if (w != null && w.SiteId == siteId && w.State == StateActive)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 处理器 ─────────────────────────────────────────────────────────────

        public static bool Prepare(CampaignState s, EventDirectorInstanceRecord r)
        {
            VibrationState v = VibrationService.StateOf(s);
            if (v == null)
            {
                return false;
            }
            // 请求过的地点（离请求位置最近的那一处）；读档 / 旧状态下找不到时取震动值最高、已过警告线的地点。
            VibrationSiteRecord site = null;
            float best = float.MaxValue;
            foreach (VibrationSiteRecord x in v.Sites)
            {
                if (x != null && x.Level == VibrationService.LevelRequested)
                {
                    float d = (new Vector2(x.HotX, x.HotY) - new Vector2(r.X, r.Y)).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        site = x;
                    }
                }
            }
            if (site == null)
            {
                foreach (VibrationSiteRecord x in v.Sites)
                {
                    if (x != null && x.Value >= VibrationService.Warn && (site == null || x.Value > site.Value))
                    {
                        site = x;
                    }
                }
            }
            if (site == null)
            {
                return false;
            }
            site.Level = VibrationService.LevelRequested;
            VibrationService.ComputeHotspot(s, site);
            r.X = site.HotX;
            r.Y = site.HotY;
            r.RegionId = HomeValleyLayout.RegionId; // 家园、前哨站、星球上的远征队都在星球战斗内核里
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int segMin = Mathf.Clamp(Mathf.RoundToInt(T("worm.segments_min", 8f)), 1, CombatWormConst.MaxSegments);
            int segMax = Mathf.Clamp(Mathf.RoundToInt(T("worm.segments_max", 12f)), segMin, CombatWormConst.MaxSegments);
            int segs = segMin + (int)(WorldGenMath.Hash(seed, r.Serial, 1, SaltRoute) % (uint)(segMax - segMin + 1));
            BuildRoute(seed, r.Serial, new Vector2(site.HotX, site.HotY), RouteScratch);
            var rec = new WormRecord
            {
                Serial = r.Serial, Chain = v.NextChain++, SiteId = site.SiteId, State = StateWarned, HotX = site.HotX, HotY = site.HotY,
                CauseDrills = site.HotDrills, Segments = segs, RouteX = new float[RouteScratch.Count], RouteY = new float[RouteScratch.Count],
            };
            for (int i = 0; i < RouteScratch.Count; i++)
            {
                rec.RouteX[i] = RouteScratch[i].x;
                rec.RouteY[i] = RouteScratch[i].y;
            }
            var list = new List<WormRecord>(v.Worms) { rec };
            v.Worms = list.ToArray();
            return true;
        }

        /// <summary>路线：从最集中处出发，按（种子, 序号）定的方向之字形穿过采矿区，首尾相接（反复走）。顶点 = 1 + worm.route_legs（≤ 内核上限）。</summary>
        public static void BuildRoute(uint seed, int serial, Vector2 hot, List<Vector2> into)
        {
            into.Clear();
            float half = Math.Max(4f, T("worm.route_half_length", 28f));
            int legs = Mathf.Clamp(Mathf.RoundToInt(T("worm.route_legs", 4f)), 2, CombatWormConst.MaxRoute - 1);
            double theta = WorldGenMath.Hash(seed, serial, 2, SaltRoute) / 4294967296.0 * Math.PI * 2.0;
            into.Add(hot);
            for (int i = 1; i <= legs; i++)
            {
                double a = theta + Math.PI * 2.0 * i / (legs + 1);
                float r = half * (i % 2 == 1 ? 1f : 0.45f);
                into.Add(hot + new Vector2((float)Math.Cos(a) * r, (float)Math.Sin(a) * r));
            }
        }

        public static void OnWarn(CampaignState s, EventDirectorInstanceRecord r)
        {
            WormRecord w = Find(s, r.Serial);
            if (w == null)
            {
                return;
            }
            VibrationSiteRecord site = VibrationService.Site(VibrationService.StateOf(s), w.SiteId, false);
            string area = site != null ? VibrationService.AreaText(s, site) : string.Empty;
            // 破土前 30 秒：裂纹沿路线蔓延（地图画出路线；通知写何时、何处、原因）。
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, new Vector2(w.HotX, w.HotY),
                GameText.Format("vibration.notify.cracks", CalendarService.ShortDuration((r.StartTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz)),
                    area, area));
            VibrationService.MarkChanged();
        }

        public static void OnStart(CampaignState s, EventDirectorInstanceRecord r)
        {
            VibrationState v = VibrationService.StateOf(s);
            WormRecord w = Find(s, r.Serial);
            CombatSite site = HomeSite;
            if (v == null || w == null || site == null || site.IsDisposed)
            {
                EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeCancelled); // 星球没有载入：这次作罢，地点保留警告、稍后再请求
                return;
            }
            RouteScratch.Clear();
            for (int i = 0; i < w.RouteX.Length; i++)
            {
                RouteScratch.Add(new Vector2(w.RouteX[i], w.RouteY[i]));
            }
            float spacing = Math.Max(0.2f, T("worm.spacing", 1.6f));
            double s0 = (w.Segments - 1) * (double)spacing; // 尾在最集中处，整条从那里钻出
            float hp = T("worm.segment_hp", 600f) * DifficultyService.EnemyHealthMul(s);
            float bite = T("worm.bite_damage", 40f) * DifficultyService.EnemyDamageMul(s);
            int n = site.SpawnWorm(w.Chain, RouteScratch, w.Segments, hp, T("worm.segment_radius", 0.9f), spacing, T("worm.speed", 3f),
                T("worm.surface_seconds", 8f), T("worm.dive_seconds", 12f), T("worm.drag_mult", 0.5f), s0, bite, T("worm.bite_range", 2.2f), T("worm.bite_cooldown", 1.5f));
            if (n <= 0)
            {
                EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeCancelled);
                return;
            }
            w.State = StateActive;
            w.LastSurfacings = 0; // 下面第一次推进按“刚钻出”结算整条身子压到的建筑
            w.LastHeadS = s0;
            _labeledSite = site;
            v.Breakouts++;
            Breakouts++;
            // FGR-EVT-012“出现一次之后，震动值归零”。
            VibrationSiteRecord vs = VibrationService.Site(v, w.SiteId, false);
            string area = string.Empty;
            if (vs != null)
            {
                area = VibrationService.AreaText(s, vs);
                vs.Value = 0.0;
                vs.HotDrills = 0; // 这一轮结束：下一轮的最集中处重新算（原因已记在 w.CauseDrills）
                vs.Reached = false;
                vs.Level = VibrationService.LevelCalm;
                vs.DueTick = -1;
                vs.WarnTick = -1;
            }
            // 卡片“蠕虫出现后说明原因（哪片采矿区震动过高）”。
            string cause = w.CauseDrills > 0 ? GameText.Format("vibration.cause.drills", w.CauseDrills) : GameText.Get("vibration.cause.blasts");
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, new Vector2(w.HotX, w.HotY), GameText.Format("vibration.notify.cause", area, cause));
            StepOne(s, v, w, site);
            VibrationService.MarkChanged();
        }

        public static void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
        {
            VibrationState v = VibrationService.StateOf(s);
            WormRecord w = Find(s, r.Serial);
            if (v == null || w == null)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (outcome == EventDirectorService.OutcomePostponed)
            {
                Remove(v, w); // 撤回预警：地点仍是“已请求”，导演补上时重新 Prepare（新的实例序号）
                return;
            }
            if (outcome == EventDirectorService.OutcomeExpired && w.State == StateActive && site != null && site.SetWormLeaving(w.Chain))
            {
                // FGR-EVT-012“超过时限没被击退，它会潜回地下离开”。
                w.State = StateLeaving;
                w.LeaveTick = GameClock.Ticks;
                v.Escapes++;
                FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(w.HotX, w.HotY),
                    GameText.Format("vibration.notify.left", Popcount(w.DroppedMask), w.Segments));
                VibrationService.MarkChanged();
                return;
            }
            site?.RemoveWorm(w.Chain, despawn: true);
            Remove(v, w);
            if (outcome != EventDirectorService.OutcomeSuccess)
            {
                // 作罢（星球没载入 / 生成失败）：地点退回警告、马上可以再请求。
                VibrationSiteRecord vs = VibrationService.Site(v, w.SiteId, false);
                if (vs != null && vs.Level == VibrationService.LevelRequested)
                {
                    vs.Level = VibrationService.LevelWarn;
                    vs.DueTick = GameClock.Ticks;
                }
            }
            VibrationService.MarkChanged();
        }

        private static void Remove(VibrationState v, WormRecord w)
        {
            var list = new List<WormRecord>(v.Worms);
            list.Remove(w);
            v.Worms = list.ToArray();
        }

        // ── 每个世界步 ─────────────────────────────────────────────────────────

        /// <summary>世界步（事件导演之后）：在地面上 / 潜回地下的蠕虫各推进一次结算。O(蠕虫数)，没有蠕虫时一次长度比较。</summary>
        public static void WorldStep(CampaignState s)
        {
            VibrationState v = s?.Vibration;
            if (v?.Worms == null || v.Worms.Length == 0)
            {
                return;
            }
            CombatSite site = HomeSite;
            if (site == null || site.IsDisposed)
            {
                return;
            }
            WormRecord[] worms = v.Worms;
            for (int i = worms.Length - 1; i >= 0; i--)
            {
                WormRecord w = worms[i];
                if (w != null && w.State != StateWarned)
                {
                    StepOne(s, v, w, site);
                }
            }
        }

        private static void StepOne(CampaignState s, VibrationState v, WormRecord w, CombatSite site)
        {
            if (!site.TryGetWorm(w.Chain, out CombatWorm cw))
            {
                // 内核里没有这条链（旧快照 / 读档对不上）：按作罢收尾，不留半截事件。
                if (w.State == StateActive)
                {
                    EventDirectorService.Complete(s, w.Serial, EventDirectorService.OutcomeCancelled);
                }
                if (Find(s, w.Serial) == w)
                {
                    Remove(v, w);
                }
                return;
            }
            if (!ReferenceEquals(_labeledSite, site))
            {
                // 读档 / 内核重建后：各节的悬停名（单位标签不在快照里）补回来。
                _labeledSite = site;
                RelabelAll(s, site);
            }
            if (w.State == StateLeaving)
            {
                if (cw.PhaseT >= cw.DiveSeconds)
                {
                    site.RemoveWorm(w.Chain, despawn: true);
                    Remove(v, w);
                    VibrationService.MarkChanged();
                }
                return;
            }
            // 1. 新倒下的节掉落（FGR-EVT-013）。
            uint fresh = cw.DeadMask & ~w.DroppedMask;
            if (fresh != 0)
            {
                DropSegments(s, v, w, cw, fresh);
            }
            // 2. 钻出地面：刚钻出那一刻整条身子压到的、之后头部钻过的地表建筑受重创（每次钻出每座一次）。
            bool up = cw.Phase == CombatWormConst.PhaseSurface;
            if (up)
            {
                if (cw.Surfacings != w.LastSurfacings)
                {
                    w.LastSurfacings = cw.Surfacings;
                    w.HitIds = Array.Empty<string>();
                    HitAlong(s, v, w, cw, cw.S - (cw.SegCount - 1) * (double)cw.Spacing, cw.S);
                }
                else if (cw.S > w.LastHeadS)
                {
                    HitAlong(s, v, w, cw, w.LastHeadS, cw.S);
                }
            }
            w.LastHeadS = cw.S;
            w.LastUp = up;
            // 3. 死亡：全部节倒下，或累计伤害够了（FGR-EVT-012）。
            int alive = cw.SegCount - Popcount(cw.DeadMask);
            float frac = Mathf.Clamp01(T("worm.kill_damage_frac", 0.7f));
            if (alive <= 0 || (cw.MaxHpSum > 0f && cw.MaxHpSum - cw.HpSum >= frac * cw.MaxHpSum))
            {
                site.CopyWormSegments(w.Chain, SegScratch);
                uint rest = 0;
                for (int k = 0; k < SegScratch.Count; k++)
                {
                    if ((cw.DeadMask & (1u << k)) == 0)
                    {
                        site.KillWormSegment(SegScratch[k]);
                        rest |= 1u << k;
                    }
                }
                if (rest != 0)
                {
                    DropSegments(s, v, w, cw, rest & ~w.DroppedMask);
                }
                v.Kills++;
                FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(w.HotX, w.HotY),
                    GameText.Format("vibration.notify.killed", Popcount(cw.DeadMask), w.Segments));
                EventDirectorService.Complete(s, w.Serial, EventDirectorService.OutcomeSuccess); // 导演给三选一、写历史；OnEnd 移除链与记录
            }
        }

        private static void DropSegments(CampaignState s, VibrationState v, WormRecord w, in CombatWorm cw, uint mask)
        {
            for (int k = 0; k < cw.SegCount; k++)
            {
                if ((mask & (1u << k)) == 0)
                {
                    continue;
                }
                w.DroppedMask |= 1u << k;
                v.SegmentsKilled++;
                FieldLootService.OnWormSegment(s, CombatSite.WormPoint(cw, cw.S - k * (double)cw.Spacing));
            }
        }

        private static void HitAlong(CampaignState s, VibrationState v, WormRecord w, in CombatWorm cw, double from, double to)
        {
            if (to <= from)
            {
                return;
            }
            float frac = Mathf.Clamp01(T("worm.building_damage_frac", 0.6f));
            // “这次钻出已受过创”只认存档里的 HitIds（静态缓存会带着读档前 / 别的会话的条目）。HitIds 很短。
            HitScratch.Clear();
            HitScratch.UnionWith(w.HitIds);
            int seen = HitScratch.Count;
            int hits = 0;
            double step = 0.5;
            int samples = Math.Min(4096, (int)Math.Ceiling((to - from) / step) + 1);
            for (int i = 0; i < samples; i++)
            {
                Vector2 p = CombatSite.WormPoint(cw, Math.Min(to, from + i * step));
                BuildingRecord b = HomeGridService.BuildingAt(s, new GridCell(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)));
                if (b == null || !HitScratch.Add(b.BuildingId))
                {
                    continue;
                }
                float hp0 = RepairDroneService.DurabilityOf(s, b);
                if (BuildingOps.ApplyDamage(s, b.BuildingId, frac * BuildingOps.MaxDurability(b.BuildingTypeId), notify: false) || b.Health < hp0 - 0.001f)
                {
                    hits++;
                }
            }
            if (HitScratch.Count != seen)
            {
                var ids = new List<string>(HitScratch);
                ids.Sort(StringComparer.Ordinal);
                w.HitIds = ids.ToArray();
            }
            if (hits > 0)
            {
                w.BuildingsHit += hits;
                v.BuildingsHit += hits;
                FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, CombatSite.WormPoint(cw, to), GameText.Format("vibration.notify.buildings", hits));
            }
        }

        private static void RelabelAll(CampaignState s, CombatSite site)
        {
            foreach (WormRecord w in Worms(s))
            {
                if (w == null || w.State == StateWarned)
                {
                    continue;
                }
                site.CopyWormSegments(w.Chain, SegScratch);
                for (int k = 0; k < SegScratch.Count; k++)
                {
                    site.SetUnitLabel(SegScratch[k], "vibration.segment", (k + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        public static int Popcount(uint x)
        {
            int n = 0;
            while (x != 0)
            {
                x &= x - 1;
                n++;
            }
            return n;
        }

        /// <summary>地图 / 悬停：这条蠕虫的路线点（预警中 = 裂纹，在地面上 = 它要钻过的地方）。</summary>
        public static void CopyRoute(WormRecord w, List<Vector2> into)
        {
            into.Clear();
            for (int i = 0; i < (w?.RouteX?.Length ?? 0); i++)
            {
                into.Add(new Vector2(w.RouteX[i], w.RouteY[i]));
            }
        }
    }
}
