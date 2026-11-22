using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-02（FG10 FGR-EVT-010 / 011，FGT-EVT-002）：震动值按地点统计——家园、每个己方前哨站、每支远征队所在的地方各自累积。
    /// - 来源：工作中的提取钻（生产步里，每分钟 + vibrationPerMinute × 等级，减震基座 env.damper.radius 格内 × env.damper.mult；在哪个地点按建筑群认）；
    ///   半径 ≥ env.vibration.blast_radius 的爆炸（内核在各爆炸点记下，每个世界步取走；在哪个地点按前哨站建筑群 / 离核心距离 / 远征队中心认）。
    /// - 衰减：每分钟 eco.vibration.decay_per_minute（与来源同在生产步里按游戏时间积分，0～eco.vibration.max）。
    /// - 阈值（每 env.vibration.check_seconds 一次，O(地点数)）：50 预兆（震颤 + 提示音，第一次发引导钩子）、80 警告（env.vibration.warn_hours 内会来、
    ///   地图标出最集中处；回落到 80 以下解除）、到 100 或警告到点 → 向事件导演请求 worm（地点 = 最集中处）；推迟 / 互斥 / 破土预警计时由导演负责。
    ///   “事件频率：关闭”时只累积、不警告不请求（FGR-EVT-006）。
    /// 推进只看步序号与存档，与观察无关（FGR-BASE-021）；热更层每步 O(本步爆炸处数 ≤ 16)，阈值检查 O(地点数)，最集中处只在警告 / 请求那一刻 O(该地点提取钻数²)。
    /// </summary>
    public static class VibrationService
    {
        public const string HomeSite = "home";
        public const string TeamPrefix = "team:";
        public const int LevelCalm = 0;
        public const int LevelOmen = 1;
        public const int LevelWarn = 2;
        public const int LevelRequested = 3;

        private static readonly Dictionary<string, double> RateScratch = new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> DrillScratch = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<float4> BlastScratch = new List<float4>(16);
        private static readonly List<BuildingRecord> Dampers = new List<BuildingRecord>(8);
        private static BuildingRecord[] _damperIndexed;
        private static CombatSite _blastSite;

        // ── 调参 ──
        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static double Max => ProductionService.VibrationMax;
        public static double Omen => T("env.vibration.omen", 50f);
        public static double Warn => T("env.vibration.warn", 80f);
        public static double Burst => T("env.vibration.burst", 100f);
        public static double DecayPerMinute => Math.Max(0.0, T("eco.vibration.decay_per_minute", 2f));
        public static float DamperRadius => Math.Max(0f, T("env.damper.radius", 10f));
        public static float DamperMult => Mathf.Clamp01(T("env.damper.mult", 0.5f));
        public static float BlastRadius => Math.Max(0.1f, T("env.vibration.blast_radius", 4f));
        public static double BlastAdd => Math.Max(0.0, T("env.vibration.blast_add", 3f));
        private static long CheckTicks => Math.Max(1, GameClock.TicksFor(Math.Max(0.05, T("env.vibration.check_seconds", 1f))));
        private static long HourTicks(double hours) => GameClock.TicksFor(hours * GameClock.DaySeconds / 24.0);

        /// <summary>任何地点的震动状态变了 +1（面板 / 地图据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;

        public static void MarkChanged() => Revision++;

        public static void ResetSessionState()
        {
            RateScratch.Clear();
            DrillScratch.Clear();
            Dampers.Clear();
            _damperIndexed = null;
            _blastSite = null;
            Revision++;
        }

        public static VibrationState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Vibration == null || s.Vibration.Sites == null || s.Vibration.Worms == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            VibrationState v = s.Vibration;
            if (!v.Migrated)
            {
                // 旧档（FG4-ECO-02 起只有一个家园标量）：迁进家园那一行，之后 EconomyState.Vibration 不再写。
                v.Migrated = true;
                double old = s.Economy?.Vibration ?? 0.0;
                if (old > 0.0)
                {
                    Site(v, HomeSite, true).Value = Math.Min(Max, old);
                }
                if (s.Economy != null)
                {
                    s.Economy.Vibration = 0.0;
                }
            }
            return v;
        }

        public static VibrationSiteRecord Site(VibrationState v, string siteId, bool create)
        {
            foreach (VibrationSiteRecord r in v.Sites)
            {
                if (r != null && r.SiteId == siteId)
                {
                    return r;
                }
            }
            if (!create)
            {
                return null;
            }
            var rec = new VibrationSiteRecord { SiteId = siteId };
            var list = new List<VibrationSiteRecord>(v.Sites) { rec };
            v.Sites = list.ToArray();
            Revision++;
            return rec;
        }

        public static double Value(CampaignState s, string siteId) => Site(StateOf(s), siteId, false)?.Value ?? 0.0;

        public static IReadOnlyList<VibrationSiteRecord> Sites(CampaignState s) => StateOf(s)?.Sites ?? Array.Empty<VibrationSiteRecord>();

        /// <summary>地点名（家园 / 前哨站名 / 远征队 N）。</summary>
        public static string SiteName(CampaignState s, string siteId)
        {
            if (string.IsNullOrEmpty(siteId) || siteId == HomeSite)
            {
                return GameText.Get("vibration.site.home");
            }
            if (siteId.StartsWith(TeamPrefix, StringComparison.Ordinal))
            {
                return GameText.Format("vibration.site.team", siteId.Substring(TeamPrefix.Length));
            }
            string n = OwnOutpostService.NameOf(s, siteId);
            return string.IsNullOrEmpty(n) ? siteId : n;
        }

        // ── 来源：提取钻（生产步调用）────────────────────────────────────────────

        /// <summary>这座提取钻所在的地点：在己方前哨站的建筑群里 = 那个前哨站，否则家园。</summary>
        public static string SiteOfDrill(CampaignState s, BuildingRecord b) =>
            b == null ? HomeSite : OwnOutpostService.OutpostIdAt(s, b.Position) ?? HomeSite;

        /// <summary>这座提取钻此刻每分钟加多少震动（FGR-EVT-010：T1 / T2 / T3 = 基础 × 1 / 2 / 3；减震基座覆盖 ×env.damper.mult）。</summary>
        public static double DrillRate(CampaignState s, BuildingRecord b, float basePerMinute) =>
            b == null || basePerMinute <= 0f ? 0.0 : basePerMinute * Math.Max(1, b.Tier) * (IsDamped(s, b) ? DamperMult : 1f);

        /// <summary>这座提取钻在某座正常运转（建成、未被禁用、未被摧毁）的减震基座半径内。O(减震基座数)。</summary>
        public static bool IsDamped(CampaignState s, BuildingRecord drill)
        {
            if (drill == null)
            {
                return false;
            }
            IndexDampers(s);
            float r2 = DamperRadius * DamperRadius;
            foreach (BuildingRecord d in Dampers)
            {
                if (d.ConstructionState == BuildingConstructionState.Operational || (d.ConstructionState == BuildingConstructionState.Damaged && d.Health > 0f))
                {
                    if ((d.Position - drill.Position).sqrMagnitude <= r2)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void IndexDampers(CampaignState s)
        {
            BuildingRecord[] all = s?.BuildingRecords;
            if (ReferenceEquals(all, _damperIndexed))
            {
                return;
            }
            _damperIndexed = all;
            Dampers.Clear();
            foreach (BuildingRecord b in all ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == DamperTypeId && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b))
                {
                    Dampers.Add(b);
                }
            }
        }

        public const string DamperTypeId = "damper_base";

        /// <summary>生产步开始：清空本步各地点的来源合计。</summary>
        public static void BeginProductionStep()
        {
            RateScratch.Clear();
            DrillScratch.Clear();
        }

        /// <summary>生产步里一座在工作的提取钻：把它的震动记到所在地点。</summary>
        public static void AddDrill(CampaignState s, BuildingRecord b, float basePerMinute)
        {
            double rate = DrillRate(s, b, basePerMinute);
            if (rate <= 0.0)
            {
                return;
            }
            string site = SiteOfDrill(s, b);
            RateScratch[site] = (RateScratch.TryGetValue(site, out double r) ? r : 0.0) + rate;
            DrillScratch[site] = (DrillScratch.TryGetValue(site, out int n) ? n : 0) + 1;
        }

        /// <summary>生产步末：各地点按 <paramref name="minutes"/> 游戏分钟积分（来源 − 衰减），钳在 0～上限。O(地点数)。</summary>
        public static void Integrate(CampaignState s, double minutes)
        {
            VibrationState v = StateOf(s);
            if (v == null || minutes <= 0)
            {
                return;
            }
            foreach (KeyValuePair<string, double> kv in RateScratch)
            {
                Site(v, kv.Key, true);
            }
            double decay = DecayPerMinute;
            double max = Max;
            double burst = Burst;
            bool prune = false;
            foreach (VibrationSiteRecord r in v.Sites)
            {
                double rate = RateScratch.TryGetValue(r.SiteId, out double x) ? x : 0.0;
                r.LastNetPerMinute = rate - decay;
                r.Reached |= r.Value >= burst; // 爆炸 / 读档带来的 100 在衰减前记下
                r.Value = Math.Max(0.0, Math.Min(max, r.Value + (rate - decay) * minutes));
                r.Reached |= r.Value >= burst;
                if (r.Value <= 0.0 && r.Level == LevelCalm)
                {
                    r.HotDrills = 0; // 完全平静：上一轮的采矿区不再算数（之后的爆炸重新定最集中处）
                }
                prune |=r.Value <= 0.0 && r.Level == LevelCalm && r.SiteId != HomeSite && rate <= 0.0;
            }
            if (prune)
            {
                // 远征队回来 / 前哨站拆了：归零又没有来源的地点不再列出（家园一直在）。
                var keep = new List<VibrationSiteRecord>(v.Sites.Length);
                foreach (VibrationSiteRecord r in v.Sites)
                {
                    if (r.Value > 0.0 || r.Level != LevelCalm || r.SiteId == HomeSite || RateScratch.ContainsKey(r.SiteId))
                    {
                        keep.Add(r);
                    }
                }
                v.Sites = keep.ToArray();
                Revision++;
            }
        }

        /// <summary>本生产步这个地点在工作的提取钻座数（面板）。</summary>
        public static int WorkingDrills(string siteId) => DrillScratch.TryGetValue(siteId ?? HomeSite, out int n) ? n : 0;

        // ── 来源：大规模爆炸 ────────────────────────────────────────────────────

        /// <summary>星球格 <paramref name="p"/> 属于哪个地点：己方前哨站建筑群 → 那个前哨站；离核心 env.vibration.home_radius 内 → 家园；
        /// 某支远征队中心 env.vibration.team_radius 内 → 那支远征队；都不是 → null（野外没有己方存在，不累积）。</summary>
        public static string SiteOfPoint(CampaignState s, Vector2 p)
        {
            string op = OwnOutpostService.OutpostIdAt(s, p);
            if (op != null)
            {
                return op;
            }
            Vector2 core = SignalUplinkService.CorePosition(s);
            float home = T("env.vibration.home_radius", 160f);
            if ((p - core).sqrMagnitude <= home * home)
            {
                return HomeSite;
            }
            float tr = T("env.vibration.team_radius", 40f);
            if (ExpeditionMarchService.TryTeamCenter(s, out Vector2 c, out _) && (p - c).sqrMagnitude <= tr * tr)
            {
                return TeamPrefix + (ExpeditionMarchService.Current(s)?.TeamNumber ?? 1).ToString(CultureInfo.InvariantCulture);
            }
            ExpeditionExtraTeam[] extras = ExpeditionMarchService.Extras(s);
            for (int i = 0; i < extras.Length; i++)
            {
                string hit = ExpeditionMarchService.ReadExtra(s, i, () =>
                    ExpeditionMarchService.TryTeamCenter(s, out Vector2 ec, out _) && (p - ec).sqrMagnitude <= tr * tr
                        ? TeamPrefix + (ExpeditionMarchService.Current(s)?.TeamNumber ?? i + 2).ToString(CultureInfo.InvariantCulture)
                        : null);
                if (hit != null)
                {
                    return hit;
                }
            }
            return null;
        }

        /// <summary>一处大规模爆炸（<paramref name="count"/> 次）：记到所在地点，最集中处取这里（该地点没有提取钻时）。返回记到的地点（null = 野外）。</summary>
        public static string AddBlast(CampaignState s, Vector2 at, int count)
        {
            VibrationState v = StateOf(s);
            string siteId = v != null && count > 0 ? SiteOfPoint(s, at) : null;
            if (siteId == null)
            {
                return null;
            }
            VibrationSiteRecord r = Site(v, siteId, true);
            r.Value = Math.Min(Max, r.Value + BlastAdd * count);
            r.BlastCount += count;
            r.LastBlastTick = GameClock.Ticks;
            if (r.HotDrills == 0)
            {
                r.HotX = at.x;
                r.HotY = at.y;
            }
            v.Blasts += count;
            Revision++;
            return siteId;
        }

        // ── 每个世界步 ─────────────────────────────────────────────────────────

        /// <summary>世界步（事件导演之前）：取走家园内核本步的大规模爆炸；每 env.vibration.check_seconds 按地点检查阈值。只看步序号，与观察无关。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            VibrationState v = StateOf(s);
            if (v == null)
            {
                return;
            }
            CombatSite home = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;
            if (home != null && !home.IsDisposed)
            {
                if (!ReferenceEquals(home, _blastSite))
                {
                    _blastSite = home;
                    home.SetBlastMinRadius(BlastRadius); // 派生量：内核建好 / 读档后写一次
                }
                if (home.DrainBlasts(BlastScratch) > 0)
                {
                    foreach (float4 b in BlastScratch)
                    {
                        AddBlast(s, new Vector2(b.x, b.y), Mathf.Max(1, Mathf.RoundToInt(b.z)));
                    }
                    BlastScratch.Clear();
                }
            }
            if (v.NextCheckTick >= 0 && tick < v.NextCheckTick)
            {
                return;
            }
            v.NextCheckTick = tick + CheckTicks;
            CheckThresholds(s, v, tick);
        }

        /// <summary>按地点检查阈值（自检直接调用；生产路径由 <see cref="WorldStep"/> 定时调）。</summary>
        public static void CheckThresholds(CampaignState s, VibrationState v, long tick)
        {
            bool off = EventDirectorService.Frequency(s) == EventDirectorService.FrequencyOff;
            bool busy = WormEventService.HasPendingOrActive(s);
            foreach (VibrationSiteRecord r in v.Sites)
            {
                if (r == null)
                {
                    continue;
                }
                double val = r.Value;
                int before = r.Level;
                bool reached = r.Reached || val >= Burst;
                r.Reached = false;
                if (r.Level == LevelCalm && val >= Omen)
                {
                    r.Level = LevelOmen;
                    OmenCue(s, v, r, tick);
                }
                else if (r.Level == LevelOmen && val < Omen)
                {
                    r.Level = LevelCalm;
                }
                // 80 警告（“事件频率：关闭”时不警告、不请求：蠕虫不会来）。
                if (!off && r.Level <= LevelOmen && val >= Warn)
                {
                    r.Level = LevelWarn;
                    r.WarnTick = tick;
                    WormEventDef(out long warnTicks);
                    r.DueTick = tick + Math.Max(0, HourTicks(T("env.vibration.warn_hours", 1f)) - warnTicks - EventDirectorService.CheckTicks);
                    ComputeHotspot(s, r);
                    // “1 个游戏小时内会来”只在到点能请求、导演也不会推迟时才承诺；否则写明要等什么。
                    string later = busy ? GameText.Get("vibration.wait.busy")
                        : EventDirectorService.BlockReason(s, EventDirectorService.Def(WormEventService.EventId), r.DueTick) is { Length: > 0 } why ? EventDirectorService.BlockText(why) : null;
                    string within = CalendarService.ShortDuration((r.DueTick + warnTicks + EventDirectorService.CheckTicks - tick) / (double)Math.Max(1, GameClock.StepHz));
                    FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventWarning, new Vector2(r.HotX, r.HotY), later == null
                        ? GameText.Format("vibration.notify.warn", SiteName(s, r.SiteId), Fmt(val), within, AreaText(s, r))
                        : GameText.Format("vibration.notify.warn_later", SiteName(s, r.SiteId), Fmt(val), within, AreaText(s, r), later));
                }
                else if (r.Level == LevelWarn && (val < Warn || off))
                {
                    r.Level = val >= Omen ? LevelOmen : LevelCalm;
                    r.DueTick = -1;
                    FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(r.HotX, r.HotY),
                        GameText.Format("vibration.notify.lifted", SiteName(s, r.SiteId), Fmt(val)));
                }
                // 100，或警告满 1 个游戏小时：请求事件导演（同一时刻只有一处在等；推迟 / 互斥 / 破土预警由导演负责）。
                if (r.Level == LevelWarn && busy && reached)
                {
                    r.Reached = true; // 等当前这条蠕虫结束后立即请求
                }
                else if (r.Level == LevelWarn && !busy && (reached || tick >= r.DueTick))
                {
                    ComputeHotspot(s, r);
                    if (EventDirectorService.Request(s, WormEventService.EventId, at: new Vector2(r.HotX, r.HotY)))
                    {
                        r.Level = LevelRequested;
                        v.Requests++;
                        busy = true;
                    }
                    else
                    {
                        r.DueTick = tick + HourTicks(1.0); // 被拒（例如事件频率刚改成关闭）：过一个游戏小时再试
                    }
                }
                if (r.Level != before)
                {
                    Revision++;
                }
            }
        }

        private static void WormEventDef(out long warnTicks)
        {
            EventDirectorDef def = EventDirectorService.Def(WormEventService.EventId);
            warnTicks = def != null && def.WarningSeconds > 0 ? Math.Max(1, GameClock.TicksFor(def.WarningSeconds)) : 0;
        }

        private static void OmenCue(CampaignState s, VibrationState v, VibrationSiteRecord r, long tick)
        {
            long cool = HourTicks(T("env.vibration.omen_cooldown_hours", 2f));
            if (r.LastOmenTick >= 0 && tick - r.LastOmenTick < cool)
            {
                return;
            }
            r.LastOmenTick = tick;
            ComputeHotspot(s, r);
            // 预兆：地面轻微震颤 + 环境音效（提示音与事件预警同一组，自带地点）；第一次到 50 发引导钩子（说明内容在 FG15-UX-04）。
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(r.HotX, r.HotY),
                GameText.Format("vibration.notify.omen", SiteName(s, r.SiteId), Fmt(r.Value)), "sfx_beacon_launch");
            if (!v.OmenSeen)
            {
                v.OmenSeen = true;
                GuidanceHooks.Raise(GuidanceHooks.VibrationFirstOmen);
            }
        }

        private static string Fmt(double x) => x.ToString("0", CultureInfo.InvariantCulture);

        // ── 最集中处（采矿区）──────────────────────────────────────────────────

        private static readonly List<ProductionService.Producer> DrillScratchList = new List<ProductionService.Producer>(32);

        /// <summary>
        /// 震动最集中处：这个地点里在工作的提取钻，每座算 env.vibration.hotspot_radius 内（含自己）的贡献之和，取最大的那座一带的加权中心；
        /// 没有在工作的提取钻 = 上次算出的采矿区（这一轮算过的话）/ 最近一次大规模爆炸处 / 远征队中心 / 地点中心。O(该地点提取钻数²)，只在警告 / 请求那一刻。
        /// </summary>
        public static void ComputeHotspot(CampaignState s, VibrationSiteRecord r)
        {
            ProductionService.CollectVibrationSources(s, r.SiteId, DrillScratchList, int.MaxValue);
            if (DrillScratchList.Count > 0)
            {
                float rad = Math.Max(1f, T("env.vibration.hotspot_radius", 12f));
                float r2 = rad * rad;
                double best = -1;
                Vector2 bestAt = default;
                int bestN = 0;
                foreach (ProductionService.Producer a in DrillScratchList)
                {
                    double sum = 0, wx = 0, wy = 0;
                    int n = 0;
                    foreach (ProductionService.Producer b in DrillScratchList)
                    {
                        if ((a.Building.Position - b.Building.Position).sqrMagnitude <= r2)
                        {
                            double w = DrillRate(s, b.Building, b.Def.VibrationPerMinute);
                            sum += w;
                            wx += w * b.Building.Position.x;
                            wy += w * b.Building.Position.y;
                            n++;
                        }
                    }
                    if (sum > best + 1e-9)
                    {
                        best = sum;
                        bestAt = sum > 0 ? new Vector2((float)(wx / sum), (float)(wy / sum)) : a.Building.Position;
                        bestN = n;
                    }
                }
                r.HotX = bestAt.x;
                r.HotY = bestAt.y;
                r.HotDrills = bestN;
                DrillScratchList.Clear();
                return;
            }
            if (r.HotDrills > 0)
            {
                return; // 钻都停了（听警告关停 / 输出满停工 / 推迟后再 Prepare）：仍是上次算出的采矿区与座数；破土或归零平静时才清（OnStart / Integrate）
            }
            if (r.LastBlastTick >= 0)
            {
                return; // 最近一次大规模爆炸处（AddBlast 已写）
            }
            if (r.SiteId == HomeSite)
            {
                Vector2 c = SignalUplinkService.CorePosition(s);
                r.HotX = c.x;
                r.HotY = c.y;
            }
            else if (r.SiteId.StartsWith(TeamPrefix, StringComparison.Ordinal) && ExpeditionMarchService.TryTeamCenter(s, out Vector2 tc, out _))
            {
                r.HotX = tc.x;
                r.HotY = tc.y;
            }
            else if (OwnOutpostService.Find(s, r.SiteId) is { } op)
            {
                r.HotX = op.X;
                r.HotY = op.Y;
            }
        }

        /// <summary>“哪片采矿区震动过高”的文字（通知、地图、事件悬停共用）。</summary>
        public static string AreaText(CampaignState s, VibrationSiteRecord r) =>
            GameText.Format(r.HotDrills > 0 ? "vibration.area" : "vibration.area_blast", SiteName(s, r.SiteId),
                Mathf.RoundToInt(r.HotX).ToString(CultureInfo.InvariantCulture), Mathf.RoundToInt(r.HotY).ToString(CultureInfo.InvariantCulture));

        public static string StateText(CampaignState s, VibrationSiteRecord r, long now)
        {
            switch (r.Level)
            {
                case LevelOmen:
                    return GameText.Get("vibration.state.omen");
                case LevelWarn:
                    return WormEventService.HasPendingOrActive(s)
                        ? GameText.Get("vibration.state.waiting") // 当前那条蠕虫结束（且到点 / 到过 100）后才请求：不承诺倒计时
                        : GameText.Format("vibration.state.warn", CalendarService.ShortDuration(Math.Max(0, r.DueTick - now) / (double)Math.Max(1, GameClock.StepHz)));
                case LevelRequested:
                    if (WormEventService.ActiveAt(s, r.SiteId))
                    {
                        return GameText.Get("vibration.state.active");
                    }
                    EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s, WormEventService.EventId);
                    return rec != null && rec.Pending && !string.IsNullOrEmpty(rec.BlockedKey)
                        ? GameText.Format("vibration.state.held", EventDirectorService.BlockText(rec.BlockedKey)) // 导演推迟（静默夜 / 重大威胁间隔 / 冷却……）
                        : GameText.Get("vibration.state.requested");
                default:
                    return GameText.Get("vibration.state.calm");
            }
        }

        // ── 面板 ───────────────────────────────────────────────────────────────

        /// <summary>统计面板“物流与能源”页的环境段：每个地点的震动值、状态、每分钟净变化与主要来源（FGR-EVT-010“显示在环境面板……列出主要的震动来源”）。
        /// O(地点数 × 该地点提取钻数)，只在面板打开刷新时调用。</summary>
        public static void BuildStatsLines(CampaignState s, List<(string Text, string Cls)> into)
        {
            VibrationState v = StateOf(s);
            if (v == null || into == null)
            {
                return;
            }
            into.Add((GameText.Get("vibration.stats.title"), "st-row-title"));
            if (v.Sites.Length == 0)
            {
                into.Add((GameText.Get("vibration.stats.none"), "st-row-dim"));
                return;
            }
            long now = GameClock.Ticks;
            int shown = Mathf.Max(1, GridContent.TuningInt("eco.prod.sources_shown"));
            foreach (VibrationSiteRecord r in v.Sites)
            {
                into.Add((GameText.Format("vibration.stats.row", SiteName(s, r.SiteId), Fmt(r.Value), Fmt(Max), StateText(s, r, now),
                    (r.LastNetPerMinute >= 0 ? "+" : string.Empty) + r.LastNetPerMinute.ToString("0.#", CultureInfo.InvariantCulture)),
                    r.Level >= LevelWarn ? "st-row-warn" : null));
                string src = SourcesText(s, r.SiteId, shown);
                if (r.BlastCount > 0)
                {
                    string blast = GameText.Format("vibration.stats.source_blast", r.BlastCount,
                        CalendarService.ShortDuration(Math.Max(0, now - r.LastBlastTick) / (double)Math.Max(1, GameClock.StepHz)));
                    src = string.IsNullOrEmpty(src) ? blast : src + Sep + blast;
                }
                if (!string.IsNullOrEmpty(src))
                {
                    into.Add((GameText.Format("vibration.stats.sources", src), "st-row-dim"));
                }
            }
        }

        private static string Sep => GameText.Get("common.clause_sep");

        /// <summary>某个地点的主要震动来源（提取钻，按此刻每分钟增量从大到小，最多 <paramref name="max"/> 座）。</summary>
        public static string SourcesText(CampaignState s, string siteId, int max)
        {
            ProductionService.CollectVibrationSources(s, siteId, DrillScratchList, max);
            if (DrillScratchList.Count == 0)
            {
                return string.Empty;
            }
            var parts = new List<string>(DrillScratchList.Count);
            foreach (ProductionService.Producer p in DrillScratchList)
            {
                parts.Add(GameText.Format("prod.panel.vibration_source", BuildingOps.NameOf(p.Building), p.Building.GridX, p.Building.GridY,
                    DrillRate(s, p.Building, p.Def.VibrationPerMinute).ToString("0.#", CultureInfo.InvariantCulture))
                    + (IsDamped(s, p.Building) ? GameText.Format("vibration.stats.damped", DamperMult.ToString("0.##", CultureInfo.InvariantCulture)) : string.Empty));
            }
            DrillScratchList.Clear();
            return string.Join(Sep, parts);
        }
    }
}
