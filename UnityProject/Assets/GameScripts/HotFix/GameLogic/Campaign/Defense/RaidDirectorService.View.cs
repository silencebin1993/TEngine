using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Defense
{
    /// <summary>预警条的一行 = 一波突袭（合并的计划同一波）。</summary>
    public struct RaidWaveView
    {
        public int Wave;
        /// <summary>这一波的主计划（剧情一方 / 最早抵达的那个）。</summary>
        public RaidPlanRecord Lead;
        public int Forces;
        public int Units;
        /// <summary>计划抵达步（出发后按队伍的实时预计）。</summary>
        public long ArrivalTick;
        public long DepartTick;
        /// <summary>只有情报（还没发预警）：有监听站提前破译出的计划中的突袭。</summary>
        public bool PlannedOnly;
        public bool Assembling;
        public bool Arrived;
        /// <summary>有有效的突袭预报（阵营、规模、编成可见）。</summary>
        public bool IntelKnown;
        public float ArriveX;
        public float ArriveY;
    }

    /// <summary>突袭导演的只读查询与文字（预警条、情报、地图、暴露面板、自检共用；不改状态）。</summary>
    public static partial class RaidDirectorService
    {
        private static readonly List<RaidWaveView> WaveScratch = new List<RaidWaveView>(8);

        // ─────────────────────────────── 文字 ───────────────────────────────

        public static string Duration(long ticks) => GameClock.FormatGameDuration(Math.Max(0, ticks) / (double)Math.Max(1, GameClock.StepHz));

        /// <summary>
        /// 来袭方向向量（FGR-DEF-024；预警条、预警通知、突袭预报、地图箭头与标签同一口径，复审 P2）：从目标看预计抵达点——沿地形的路线第一次进入到达半径的那一点；
        /// 还没排定（没有抵达点）或抵达点与目标重合时退回“从目标看出发地”。
        /// </summary>
        public static void ApproachVector(RaidPlanRecord p, out double dx, out double dy)
        {
            dx = 0;
            dy = 0;
            if (p == null)
            {
                dx = 1;
                return;
            }
            if (p.ArrivalTick >= 0)
            {
                dx = p.ArriveX - p.TargetX;
                dy = p.ArriveY - p.TargetY;
            }
            if (dx * dx + dy * dy < 1e-12)
            {
                dx = p.OriginX - p.TargetX;
                dy = p.OriginY - p.TargetY;
            }
            if (dx * dx + dy * dy < 1e-12)
            {
                dx = 1;
                dy = 0;
            }
        }

        /// <summary>来袭方向（8 方位文字，<see cref="ApproachVector"/>）。</summary>
        public static string DirectionText(RaidPlanRecord p)
        {
            ApproachVector(p, out double dx, out double dy);
            return IntelService.DirectionName((float)dx, (float)dy);
        }

        /// <summary>
        /// 预警条一行 / 地图箭头的来袭方向：从这一波主计划的目标看这一波的预计抵达点（出发后按队伍的实时预计，路线中途重算过也跟着变）；
        /// 抵达点与目标重合时退回 <see cref="ApproachVector"/>。
        /// </summary>
        public static void WaveApproachVector(in RaidWaveView v, out double dx, out double dy)
        {
            RaidPlanRecord p = v.Lead;
            if (p == null)
            {
                dx = 1;
                dy = 0;
                return;
            }
            dx = v.ArriveX - p.TargetX;
            dy = v.ArriveY - p.TargetY;
            if (dx * dx + dy * dy < 1e-12 || p.ArrivalTick < 0)
            {
                ApproachVector(p, out dx, out dy);
            }
        }

        public static string WaveDirectionText(in RaidWaveView v)
        {
            WaveApproachVector(v, out double dx, out double dy);
            return IntelService.DirectionName((float)dx, (float)dy);
        }

        public static string TargetText(RaidPlanRecord p)
        {
            if (p?.TargetKind == TargetOutpost)
            {
                int count = 0;
                foreach (WorldMapOwnClusters.Cluster c in WorldMapOwnClusters.For(CampaignSession.Current))
                {
                    if (c.Id == p.TargetId)
                    {
                        count = c.Count;
                    }
                }
                return GameText.Format("raid.target.outpost", count);
            }
            return GameText.Get("raid.target.home");
        }

        public static string OriginText(RaidPlanRecord p) =>
            p?.OriginKind == OriginOutpost ? GameText.Get("raid.origin.outpost") : GameText.Get("raid.origin.fog");

        /// <summary>编成文字：“步行机 ×4、护甲机 ×2（精英 1）”。</summary>
        public static string CompositionText(RaidPlanRecord p)
        {
            if (p == null || p.UnitIds.Length == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < p.UnitIds.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(GameText.Get("intel.list_sep"));
                }
                string name = p.UnitIds[i];
                if (RaidCatalog.TryGetUnit(p.UnitIds[i], out RaidUnitDef u))
                {
                    string key = "enemy." + u.EnemyTypeId.Replace("enemy_", string.Empty) + ".name";
                    name = GameText.Has(key) ? GameText.Get(key) : u.EnemyTypeId;
                }
                int elite = i < p.EliteCounts.Length ? p.EliteCounts[i] : 0;
                sb.Append(elite > 0
                    ? GameText.Format("raid.warning.comp_elite", name, p.UnitCounts[i], elite)
                    : GameText.Format("raid.warning.comp_item", name, p.UnitCounts[i]));
            }
            return GameText.Format("raid.warning.comp", sb.ToString());
        }

        /// <summary>反制文字：“针对你的引信与弹芯：加厚装甲”或“没有针对性反制”。</summary>
        public static string CounterText(RaidPlanRecord p)
        {
            RaidCounter c = RaidCatalog.CounterById(p?.Counter);
            if (c == null)
            {
                return GameText.Get("raid.warning.counter_none");
            }
            return GameText.Format("raid.warning.counter", GameText.Get("raid.category." + c.Category), GameText.Get(c.NameKey));
        }

        public static string FactionText(RaidPlanRecord p) => WorldTransitSystem.OriginName(p?.Faction);

        // ─────────────────────────────── 预警条 / 地图 / 情报 ───────────────────────────────

        /// <summary>这个计划有没有有效的突袭预报（监听站破译 / 数据核心）。</summary>
        public static bool IntelKnown(CampaignState s, RaidPlanRecord p, long now) =>
            p != null && IntelService.HasValid(s, IntelCatalog.KindRaid, p.PlanId, now, out _);

        /// <summary>计划能不能被监听站预报（FGR-DEF-024“最早提前 1 个游戏日”）：路线就绪、还没出发、离发预警不超过 raid.intel_lead_days。</summary>
        public static bool Forecastable(RaidPlanRecord p, long now) =>
            p != null && (p.State == StateScheduled || p.State == StateWarned) && p.WarnTick >= 0 && now >= p.WarnTick - DayTicks(RaidCatalog.IntelLeadDays);

        /// <summary>行进中的队伍算不算“被发现”（FGR-DEF-023“行进途中可以被发现”）：走进已探索区域，或监听站正跟着它（有有效预报）。</summary>
        public static bool IsDiscovered(CampaignState s, TransitGroupRecord g, long now)
        {
            if (g == null)
            {
                return false;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            if (map != null && map.IsExploredNoLoad(new GridCell((int)Math.Round(g.PosX), (int)Math.Round(g.PosY))))
            {
                return true;
            }
            return !string.IsNullOrEmpty(g.PlanId) && IntelService.HasValid(s, IntelCatalog.KindRaid, g.PlanId, now, out _);
        }

        /// <summary>
        /// 预警条的各波（按抵达时间）：已发预警的（集结 / 在路上 / 已到达还没撤退）+ 只有情报的计划中突袭。O(计划数)，返回共享列表（调用方不要留着）。
        /// </summary>
        public static IReadOnlyList<RaidWaveView> IncomingWaves(CampaignState s, long now)
        {
            WaveScratch.Clear();
            foreach (RaidPlanRecord p in StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                if (p == null || p.State >= StateEnded)
                {
                    continue;
                }
                bool intel = IntelKnown(s, p, now);
                var v = new RaidWaveView { Wave = p.Wave, Lead = p, Forces = 1, Units = p.UnitTotal, ArrivalTick = p.ArrivalTick, DepartTick = p.DepartTick, IntelKnown = intel,
                    ArriveX = p.ArriveX, ArriveY = p.ArriveY };
                if (p.State == StateDeparted)
                {
                    TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
                    if (g == null || g.State == TransitGroupState.Retreating)
                    {
                        continue;
                    }
                    v.Units = g.UnitCount;
                    if (g.State == TransitGroupState.Marching)
                    {
                        v.ArrivalTick = now + GameClock.TicksFor(WorldTransitSystem.EtaSeconds(g));
                        WorldTransitSystem.PredictArrival(g, out double ax, out double ay);
                        v.ArriveX = (float)ax;
                        v.ArriveY = (float)ay;
                    }
                    else
                    {
                        v.Arrived = true;
                        v.ArriveX = (float)g.PosX;
                        v.ArriveY = (float)g.PosY;
                    }
                }
                else if (p.State == StateWarned)
                {
                    v.Assembling = p.DepartTick > now;
                }
                else if (p.State == StateScheduled && intel)
                {
                    v.PlannedOnly = true;
                }
                else
                {
                    continue;
                }
                int at = -1;
                for (int i = 0; i < WaveScratch.Count; i++)
                {
                    if (WaveScratch[i].Wave == v.Wave)
                    {
                        at = i;
                    }
                }
                if (at < 0)
                {
                    WaveScratch.Add(v);
                    continue;
                }
                RaidWaveView w = WaveScratch[at];
                w.Forces++;
                w.Units += v.Units;
                w.IntelKnown |= v.IntelKnown;
                if (v.Lead.Exempt && !w.Lead.Exempt)
                {
                    w.Lead = v.Lead;
                }
                // 整行“已到达”要等每一支都到了；还有没到的，就显示还没到的那一支里最早的倒计时（复审 P2：先到的一支不盖住后面的倒计时）。
                bool takeV = w.Arrived != v.Arrived ? w.Arrived : v.ArrivalTick < w.ArrivalTick;
                if (takeV)
                {
                    w.ArrivalTick = v.ArrivalTick;
                    w.ArriveX = v.ArriveX;
                    w.ArriveY = v.ArriveY;
                }
                w.Arrived &= v.Arrived;
                w.Assembling &= v.Assembling;
                w.PlannedOnly &= v.PlannedOnly;
                WaveScratch[at] = w;
            }
            WaveScratch.Sort((a, b) => a.ArrivalTick != b.ArrivalTick ? a.ArrivalTick.CompareTo(b.ArrivalTick) : a.Wave.CompareTo(b.Wave));
            return WaveScratch;
        }

        /// <summary>预警条一行的文字（倒计时、方向、目标；有情报时加阵营 / 规模 / 编成 / 反制）。</summary>
        public static string WaveRowText(CampaignState s, in RaidWaveView v, long now)
        {
            RaidPlanRecord p = v.Lead;
            string wave = v.Forces > 1 ? GameText.Format("raid.warning.wave_merged", v.Wave, v.Forces) : GameText.Format("raid.warning.wave", v.Wave);
            string dir = WaveDirectionText(v);
            string target = TargetText(p);
            string head;
            if (v.Arrived)
            {
                head = GameText.Format("raid.warning.row_arrived", wave, target, v.Units);
            }
            else if (v.PlannedOnly)
            {
                head = GameText.Format("raid.warning.row_planned", wave, Duration(p.WarnTick - now), dir);
            }
            else if (v.Assembling)
            {
                head = GameText.Format("raid.warning.row_assembling", wave, dir, Duration(v.DepartTick - now), Duration(v.ArrivalTick - now), target);
            }
            else
            {
                head = GameText.Format("raid.warning.row", wave, dir, Duration(v.ArrivalTick - now), target);
            }
            if (!v.IntelKnown)
            {
                return head + "\n" + GameText.Get("raid.warning.comp_unknown");
            }
            return head + "\n" + GameText.Format("raid.warning.faction", FactionText(p), RaidCatalog.LevelName(p.Level), v.Units) + " · "
                   + CompositionText(p) + " · " + CounterText(p);
        }

        /// <summary>预警条一行的悬停说明（触发、目标、出发地、预计抵达、编成 / 反制）。</summary>
        public static string WaveTip(CampaignState s, in RaidWaveView v, long now)
        {
            RaidPlanRecord p = v.Lead;
            var trig = new StringBuilder();
            foreach (string t in p.Triggers)
            {
                if (trig.Length > 0)
                {
                    trig.Append(GameText.Get("intel.list_sep"));
                }
                trig.Append(RaidCatalog.TriggerName(t));
            }
            string arrive = GameClock.FormatDayTime(Math.Max(0, v.ArrivalTick) / (double)Math.Max(1, GameClock.StepHz));
            string comp = v.IntelKnown ? CompositionText(p) : GameText.Get("raid.warning.comp_unknown");
            string counter = v.IntelKnown ? CounterText(p) : string.Empty;
            // 文本表里不放换行：逐行一个键，这里按行拼接；没有情报时不写“针对”一行。
            var sb = new StringBuilder();
            sb.Append(GameText.Format("raid.warning.tip.trigger", trig.ToString())).Append('\n');
            sb.Append(GameText.Format("raid.warning.tip.target", TargetText(p))).Append('\n');
            sb.Append(GameText.Format("raid.warning.tip.origin", OriginText(p), WaveDirectionText(v))).Append('\n');
            sb.Append(GameText.Format("raid.warning.tip.arrive", arrive)).Append('\n');
            sb.Append(comp).Append('\n');
            if (!string.IsNullOrEmpty(counter))
            {
                sb.Append(counter).Append('\n');
            }
            sb.Append(GameText.Format("raid.warning.tip.click", InputDisplay.ForAction(GameActionId.CycleWorldFocus))).Append('\n');
            sb.Append(GameText.Get("raid.warning.placeholder"));
            return sb.ToString();
        }

        /// <summary>暴露面板的“家园突袭”一行：开局宽限 / 正在逼近 / 已排定 / 没有。</summary>
        public static string StatusLine(CampaignState s, long now)
        {
            IReadOnlyList<RaidWaveView> waves = IncomingWaves(s, now);
            int incoming = 0;
            long nearest = long.MaxValue;
            foreach (RaidWaveView w in waves)
            {
                if (!w.PlannedOnly)
                {
                    incoming++;
                    nearest = Math.Min(nearest, w.ArrivalTick);
                }
            }
            string body;
            if (incoming > 0)
            {
                body = GameText.Format("exposure.panel.raid_warned", incoming, Duration(nearest - now));
            }
            else
            {
                int planned = 0;
                long warn = long.MaxValue;
                foreach (RaidPlanRecord p in StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>())
                {
                    if (p != null && p.State <= StateScheduled)
                    {
                        planned++;
                        if (p.WarnTick >= 0)
                        {
                            warn = Math.Min(warn, p.WarnTick);
                        }
                    }
                }
                if (planned > 0)
                {
                    body = GameText.Format("exposure.panel.raid_planned", planned, warn == long.MaxValue ? "?" : Duration(warn - now));
                }
                else if (now < GraceEndTick)
                {
                    body = GameText.Format("exposure.panel.raid_grace", GameClock.DayOf(GraceEndTick / (double)Math.Max(1, GameClock.StepHz)));
                }
                else
                {
                    body = GameText.Get("exposure.panel.raid_none");
                }
            }
            return GameText.Format("exposure.panel.raid_line", body);
        }

        // ─────────────────────────────── 自检快照 ───────────────────────────────

        /// <summary>导演状态的确定性快照（自检对照：存读档 / 暂停与倍速 / 观察与不观察逐字段一致）。</summary>
        public static string Snapshot(CampaignState s)
        {
            RaidDirectorState d = StateOf(s);
            if (d == null)
            {
                return "none";
            }
            var sb = new StringBuilder(256);
            sb.Append("init=").Append(d.Initialized).Append(";thr=").Append(d.ThresholdFired).Append(";l4=").Append(d.Level4NextTick).Append(";harass=").Append(d.HarassNextTick)
              .Append(";last=").Append(d.LastArrivalTick).Append(";any=").Append(d.LastAnyArrivalTick).Append(";count=").Append(d.RaidCount).Append(";pg=").Append(d.PostgameCount)
              .Append(";next=").Append(d.NextPlanSerial).Append('/').Append(d.NextWaveSerial).Append(";pending=").Append(d.Pending.Length)
              .Append(";oct=").Append(string.Join(",", d.DestroyedByOctant)).Append(";lane=").Append(d.LaneBusyUntilTick).Append(";stories=").Append(string.Join(",", d.StoriesFired))
              .Append(";rx=").Append(d.TriggersReceived).Append('/').Append(d.TriggersSkipped).Append('/').Append(d.TriggersMerged).Append('|');
            foreach (RaidPlanRecord p in d.Plans)
            {
                if (p == null)
                {
                    continue;
                }
                sb.Append(p.PlanId).Append(':').Append(p.State).Append(':').Append(p.Trigger).Append(':').Append(string.Join("+", p.Triggers)).Append(':').Append(p.Level)
                  .Append(':').Append(p.Budget).Append(':').Append(p.Faction).Append(':').Append(p.Counter).Append(':').Append(p.TargetKind).Append('@').Append(p.TargetX).Append(',')
                  .Append(p.TargetY).Append(':').Append(p.OriginKind).Append('=').Append(p.OriginId).Append('@').Append(p.OriginX).Append(',').Append(p.OriginY).Append(':');
                for (int i = 0; i < p.UnitIds.Length; i++)
                {
                    sb.Append(p.UnitIds[i]).Append('x').Append(p.UnitCounts[i]).Append('e').Append(p.EliteCounts[i]).Append(',');
                }
                sb.Append(':').Append(p.RouteState).Append(':').Append(p.RouteX.Length).Append(':').Append(p.TravelTicks).Append(':').Append(p.WarnTick).Append('/')
                  .Append(p.DepartTick).Append('/').Append(p.ArrivalTick).Append(':').Append(p.GroupId).Append(':').Append(p.ArrivedTick).Append(':')
                  .Append(p.ArriveX.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.ArriveY.ToString("R", CultureInfo.InvariantCulture))
                  .Append(":rev").Append(p.Revision).Append(":ra").Append(p.RouteAdoptTick).Append('/').Append(p.RouteReadyTick).Append('/').Append(p.NavSerial).Append('|');
            }
            foreach (RaidHistoryRecord h in d.History)
            {
                if (h != null)
                {
                    sb.Append('H').Append(h.PlanId).Append(':').Append(h.EndReason).Append(':').Append(h.ArrivedTick).Append(':').Append(h.EndTick).Append('|');
                }
            }
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (g != null)
                {
                    sb.Append('G').Append(g.GroupId).Append(':').Append(g.State).Append(':').Append(g.PlanId).Append(':').Append(g.UnitCount).Append(':')
                      .Append(g.PosX.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(g.PosY.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                      .Append(g.ArrivedAtTick).Append(':').Append(g.RetreatTick).Append('|');
                }
            }
            return sb.ToString();
        }
    }
}
