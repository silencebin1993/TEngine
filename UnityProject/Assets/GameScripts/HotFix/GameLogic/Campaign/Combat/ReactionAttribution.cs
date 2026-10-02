using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG2-FW-04（FG02 FGR-FW-043“伤害归因：按反应统计每次远征和每次突袭的伤害占比，进入统计面板和离家报告”；第 6 节“伤害归因统计要存档”；
    /// 第 7 节“伤害归因统计在内核侧累加，热更层只读汇总”）：伤害归因的唯一写入口。
    /// 玩家入口：统计面板（<c>StatsPanelUIToolkit</c>，暂停菜单“统计”：累计 + 每场明细，可按远征 / 突袭筛选）、反应记录面板的“伤害归因”页签、撤离报告一行；
    /// 离家报告（FG4-ECO-09 / FG6-DEF-08）读 <see cref="Summary"/>，见 FG-GAP-055。
    ///
    /// - 场次：远征 = 一个远征区域的一次出击（区域记录的第几次出击，出发时开、撤离 / 放弃时关；读档后按区域记录接着记）；
    ///   突袭 = 家园的一次突袭（<see cref="BeginRaid"/> / <see cref="EndRaid"/>，由突袭结算 FG6-DEF-05 / 08 调用）。家园平时（训练靶）不算场次。
    /// - 数据：内核按反应累加“打在敌对阵营身上的反应额外伤害”与“敌对阵营受到的全部伤害”，<see cref="ReactionFeedback"/> 每步把增量交到这里（O(反应条数)）；
    ///   这里只做加法与场次归属，存进 <see cref="StatsState.ReactionSessions"/>（存档）。
    /// - 占比 = 这条反应的额外伤害 / 这一场敌对阵营受到的全部伤害。克制类（绝缘 / 淤塞）让目标少掉血，不算伤害（记次数、伤害 0）。
    /// - 场次上限 fg.TbUiTuning reaction.attribution_sessions：超出时丢最旧的已结束场次（进行中的不丢）。
    /// </summary>
    public static class ReactionAttribution
    {
        public const string KindExpedition = "expedition";
        public const string KindRaid = "raid";

        /// <summary>一条反应在一场里的占比（显示用，已按伤害从大到小排好）。</summary>
        public readonly struct Share
        {
            public readonly string ReactionId;
            public readonly int Count;
            public readonly double Damage;
            public readonly double Fraction;

            public Share(string reactionId, int count, double damage, double fraction)
            {
                ReactionId = reactionId;
                Count = count;
                Damage = damage;
                Fraction = fraction;
            }
        }

        public static int Revision { get; private set; }

        public static int SessionCapacity => Math.Max(1, ReactionPopups.TuningInt("reaction.attribution_sessions", 20));

        public static IReadOnlyList<ReactionSessionRecord> Sessions(CampaignState state) =>
            (IReadOnlyList<ReactionSessionRecord>)state?.Stats?.ReactionSessions ?? Array.Empty<ReactionSessionRecord>();

        /// <summary>地点当前归属的场次种类（反应日志筛选用）：远征区域 = expedition，家园有进行中的突袭 = raid，否则 null（家园平时 / 测试地点）。</summary>
        public static string KindOfSite(CampaignState state, string siteId)
        {
            ReactionSessionRecord s = Current(state, siteId, create: false);
            if (s != null)
            {
                return s.Kind;
            }
            return siteId != HomeValleyLayout.RegionId && ExpeditionOrdinal(state, siteId) > 0 ? KindExpedition : null;
        }

        /// <summary>地点当前的场次；<paramref name="create"/> 时远征区域没有就开一场（突袭只由 <see cref="BeginRaid"/> 开）。</summary>
        public static ReactionSessionRecord Current(CampaignState state, string siteId, bool create)
        {
            StatsState stats = state?.Stats;
            if (stats == null || string.IsNullOrEmpty(siteId))
            {
                return null;
            }
            stats.ReactionSessions ??= Array.Empty<ReactionSessionRecord>();
            if (siteId == HomeValleyLayout.RegionId)
            {
                return OpenOf(stats, KindRaid, siteId, null);
            }
            int ordinal = ExpeditionOrdinal(state, siteId);
            if (ordinal <= 0)
            {
                return null;
            }
            ReactionSessionRecord open = OpenOf(stats, KindExpedition, siteId, ordinal);
            if (open != null || !create)
            {
                return open;
            }
            // 这一次出击已经撤离结算过（区域在撤离后还没卸载时）：不再为它开新场。
            foreach (ReactionSessionRecord r in stats.ReactionSessions)
            {
                if (r != null && r.EndTick >= 0 && r.Kind == KindExpedition && r.SiteId == siteId && r.Ordinal == ordinal)
                {
                    return null;
                }
            }
            // 同一区域上一场没关（例如撤离前读档、旧流程）：新出击开新场之前把旧场关掉。
            foreach (ReactionSessionRecord r in stats.ReactionSessions)
            {
                if (r != null && r.EndTick < 0 && r.Kind == KindExpedition && r.SiteId == siteId)
                {
                    r.EndTick = GameClock.Ticks;
                    Economy.FusionService.OnSessionClosed(state, r); // FG5-RND-04：场次结束 → 仿真实验室分析战斗记录（线索）
                }
            }
            return Add(stats, KindExpedition, siteId, ordinal, string.Empty);
        }

        /// <summary>一步里敌对阵营受到的伤害（分母）。没有场次的地点不记。</summary>
        public static void AddDamage(CampaignState state, string siteId, double damage)
        {
            if (!(damage > 0))
            {
                return;
            }
            ReactionSessionRecord s = Current(state, siteId, create: true);
            if (s != null)
            {
                s.TotalDamage += damage;
                Revision++;
            }
        }

        /// <summary>一步里某条反应触发了 <paramref name="count"/> 次、打在敌对阵营身上的额外伤害 <paramref name="damage"/>。</summary>
        public static void AddReaction(CampaignState state, string siteId, string reactionId, int count, double damage)
        {
            if (count <= 0 && !(damage > 0) || string.IsNullOrEmpty(reactionId))
            {
                return;
            }
            ReactionSessionRecord s = Current(state, siteId, create: true);
            if (s == null)
            {
                return;
            }
            s.Reactions ??= Array.Empty<ReactionShareRecord>();
            ReactionShareRecord row = null;
            foreach (ReactionShareRecord r in s.Reactions)
            {
                if (r != null && r.ReactionId == reactionId)
                {
                    row = r;
                    break;
                }
            }
            if (row == null)
            {
                row = new ReactionShareRecord { ReactionId = reactionId };
                var grown = new ReactionShareRecord[s.Reactions.Length + 1];
                Array.Copy(s.Reactions, grown, s.Reactions.Length);
                grown[s.Reactions.Length] = row;
                // 按反应 ID 排序存（存档确定性，与触发先后无关）。
                Array.Sort(grown, (a, b) => string.CompareOrdinal(a.ReactionId, b.ReactionId));
                s.Reactions = grown;
            }
            row.Count += Math.Max(0, count);
            row.Damage += Math.Max(0, damage);
            Revision++;
        }

        /// <summary>突袭开始（FG6-DEF-05 突袭队到达家园、展开成单位时调用）：家园此后的伤害记到这一场。已有进行中的突袭时返回它（不重复开）。</summary>
        public static ReactionSessionRecord BeginRaid(CampaignState state, string raidKey, string siteId = HomeValleyLayout.RegionId)
        {
            StatsState stats = state?.Stats;
            if (stats == null)
            {
                return null;
            }
            stats.ReactionSessions ??= Array.Empty<ReactionSessionRecord>();
            ReactionSessionRecord open = OpenOf(stats, KindRaid, siteId, null);
            return open ?? Add(stats, KindRaid, siteId, 0, raidKey ?? string.Empty);
        }

        /// <summary>突袭结束（FG6-DEF-08 突袭结算时调用）。没有进行中的突袭返回 false。</summary>
        public static bool EndRaid(CampaignState state, string siteId = HomeValleyLayout.RegionId)
        {
            ReactionSessionRecord open = state?.Stats != null ? OpenOf(state.Stats, KindRaid, siteId, null) : null;
            if (open == null)
            {
                return false;
            }
            open.EndTick = GameClock.Ticks;
            Revision++;
            Economy.FusionService.OnSessionClosed(state, open); // FG5-RND-04：突袭结束 → 线索
            return true;
        }

        /// <summary>远征撤离 / 放弃（<see cref="ExpeditionReturnService"/>）：关掉这个区域进行中的场次。</summary>
        public static void CloseExpedition(CampaignState state, string regionId)
        {
            StatsState stats = state?.Stats;
            if (stats?.ReactionSessions == null)
            {
                return;
            }
            foreach (ReactionSessionRecord r in stats.ReactionSessions)
            {
                if (r != null && r.EndTick < 0 && r.Kind == KindExpedition && r.SiteId == regionId)
                {
                    r.EndTick = GameClock.Ticks;
                    Revision++;
                    Economy.FusionService.OnSessionClosed(state, r); // FG5-RND-04：远征撤离 / 放弃 → 线索
                }
            }
        }

        /// <summary>一场里各反应的占比（按伤害从大到小，同伤害按次数、再按 ID）。</summary>
        public static List<Share> SharesOf(ReactionSessionRecord s)
        {
            var list = new List<Share>();
            if (s?.Reactions == null)
            {
                return list;
            }
            double total = s.TotalDamage;
            foreach (ReactionShareRecord r in s.Reactions)
            {
                if (r == null)
                {
                    continue;
                }
                list.Add(new Share(r.ReactionId, r.Count, r.Damage, total > 0 ? Math.Min(1.0, r.Damage / total) : 0));
            }
            list.Sort(CompareShares);
            return list;
        }

        /// <summary>按伤害从大到小，同伤害按次数、再按 ID（显示顺序确定）。</summary>
        private static int CompareShares(Share a, Share b)
        {
            int c = b.Damage.CompareTo(a.Damage);
            if (c != 0)
            {
                return c;
            }
            c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : string.CompareOrdinal(a.ReactionId, b.ReactionId);
        }

        /// <summary>筛选对应的场次种类：全部 = null。</summary>
        public static string KindOf(ReactionLogFilter filter) =>
            filter == ReactionLogFilter.Expedition ? KindExpedition : filter == ReactionLogFilter.Raid ? KindRaid : null;

        /// <summary>场次是否符合筛选（全部 / 远征 / 突袭）。</summary>
        public static bool Matches(ReactionSessionRecord s, ReactionLogFilter filter)
        {
            string kind = KindOf(filter);
            return s != null && (kind == null || s.Kind == kind);
        }

        /// <summary>统计面板的累计（卡片“伤害归因进入统计面板”）：把存档里符合筛选的场次（最多 <see cref="SessionCapacity"/> 场，含进行中的）按反应合计。
        /// 占比 = 这条反应合计的额外伤害 / 这些场次敌方受到的全部伤害；顺序同 <see cref="SharesOf"/>。O(场次 × 每场反应条数)，只在面板刷新时调用。</summary>
        public static List<Share> Aggregate(CampaignState state, ReactionLogFilter filter, out double totalDamage, out double reactionDamage, out int sessions)
        {
            totalDamage = 0;
            reactionDamage = 0;
            sessions = 0;
            var sums = new Dictionary<string, (int Count, double Damage)>();
            foreach (ReactionSessionRecord s in Sessions(state))
            {
                if (!Matches(s, filter))
                {
                    continue;
                }
                sessions++;
                totalDamage += Math.Max(0, s.TotalDamage);
                if (s.Reactions == null)
                {
                    continue;
                }
                foreach (ReactionShareRecord r in s.Reactions)
                {
                    if (r == null || string.IsNullOrEmpty(r.ReactionId))
                    {
                        continue;
                    }
                    sums.TryGetValue(r.ReactionId, out (int Count, double Damage) cur);
                    sums[r.ReactionId] = (cur.Count + Math.Max(0, r.Count), cur.Damage + Math.Max(0, r.Damage));
                    reactionDamage += Math.Max(0, r.Damage);
                }
            }
            var list = new List<Share>(sums.Count);
            foreach (KeyValuePair<string, (int Count, double Damage)> kv in sums)
            {
                list.Add(new Share(kv.Key, kv.Value.Count, kv.Value.Damage, totalDamage > 0 ? Math.Min(1.0, kv.Value.Damage / totalDamage) : 0));
            }
            list.Sort(CompareShares);
            return list;
        }

        /// <summary>反应在归因 / 日志里的显示名：开放命名的显示机械名，没开放的显示“未知反应”（与战斗里不报名字一致）。</summary>
        public static string DisplayName(CampaignState state, string reactionId) =>
            NamedReactionCatalog.IsNamed(state, reactionId) ? NamedReactionCatalog.NameOf(reactionId) : GameText.Get("reaction.unnamed");

        /// <summary>一场的一行摘要（统计 / 撤离报告）：“反应伤害占比：短路 12%、热震 5%”；没有反应时写“本场没有打出反应”。</summary>
        public static string Summary(CampaignState state, ReactionSessionRecord s, int maxItems = 3)
        {
            if (s == null)
            {
                return GameText.Get("reaction.attr.none");
            }
            List<Share> shares = SharesOf(s);
            if (shares.Count == 0)
            {
                return GameText.Get("reaction.attr.none");
            }
            var parts = new List<string>();
            for (int i = 0; i < shares.Count && i < maxItems; i++)
            {
                parts.Add(GameText.Format("reaction.attr.item", DisplayName(state, shares[i].ReactionId), Percent(shares[i].Fraction), shares[i].Count));
            }
            return GameText.Format("reaction.attr.summary", string.Join(GameText.Get("reaction.attr.sep"), parts));
        }

        public static string Percent(double fraction) =>
            (Math.Round(fraction * 1000.0) / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>场次标题：“远征 · 破碎都市 第 2 次”/“突袭 · 家园”。</summary>
        public static string Title(ReactionSessionRecord s)
        {
            if (s == null)
            {
                return string.Empty;
            }
            string site = CombatSites.SiteName(s.SiteId);
            return s.Kind == KindRaid
                ? GameText.Format("reaction.attr.title_raid", site)
                : GameText.Format("reaction.attr.title_expedition", site, s.Ordinal);
        }

        private static int ExpeditionOrdinal(CampaignState state, string siteId)
        {
            RegionRecord[] regions = state?.RegionRecords;
            if (regions == null)
            {
                return 0;
            }
            foreach (RegionRecord r in regions)
            {
                if (r != null && r.RegionId == siteId)
                {
                    return r.ExpeditionCount;
                }
            }
            return 0;
        }

        private static ReactionSessionRecord OpenOf(StatsState stats, string kind, string siteId, int? ordinal)
        {
            ReactionSessionRecord[] all = stats.ReactionSessions;
            for (int i = all.Length - 1; i >= 0; i--)
            {
                ReactionSessionRecord r = all[i];
                if (r != null && r.EndTick < 0 && r.Kind == kind && r.SiteId == siteId && (!ordinal.HasValue || r.Ordinal == ordinal.Value))
                {
                    return r;
                }
            }
            return null;
        }

        private static ReactionSessionRecord Add(StatsState stats, string kind, string siteId, int ordinal, string raidKey)
        {
            var rec = new ReactionSessionRecord
            {
                SessionId = "rs-" + stats.NextReactionSessionSerial.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Kind = kind,
                SiteId = siteId,
                Ordinal = ordinal,
                RaidKey = raidKey ?? string.Empty,
                StartTick = GameClock.Ticks,
                EndTick = -1,
            };
            stats.NextReactionSessionSerial++;
            var list = new List<ReactionSessionRecord>(stats.ReactionSessions) { rec };
            int cap = SessionCapacity;
            // 超出上限：丢最旧的已结束场次；进行中的不丢。
            for (int i = 0; i < list.Count && list.Count > cap;)
            {
                if (list[i] != null && list[i].EndTick >= 0)
                {
                    list.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            stats.ReactionSessions = list.ToArray();
            Revision++;
            return rec;
        }
    }
}
