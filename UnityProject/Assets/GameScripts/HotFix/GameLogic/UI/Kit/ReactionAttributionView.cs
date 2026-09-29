using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-04：伤害归因的显示行（统计面板 <see cref="StatsPanelUIToolkit"/> 与反应记录面板“伤害归因”页签共用，同一份数据同一种写法）。
    /// 每行 = 文字 + 可选 USS 类（rl-row-title 标题行 / rl-row-dim 淡色说明），行由面板按数量建 Label。只在面板刷新时调用，O(场次 × 每场反应条数)。
    /// </summary>
    internal static class ReactionAttributionView
    {
        /// <summary>每场明细（最新在前）：“远征 · 破碎都市 第 2 次　白天 3 08:00 ～ …　敌方共受伤害 1,200”，下面逐条“短路 25%（3 次）”。</summary>
        public static int AppendSessions(CampaignState state, ReactionLogFilter filter, List<(string Text, string Cls)> lines)
        {
            int shown = 0;
            IReadOnlyList<ReactionSessionRecord> sessions = ReactionAttribution.Sessions(state);
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                ReactionSessionRecord s = sessions[i];
                if (!ReactionAttribution.Matches(s, filter))
                {
                    continue;
                }
                shown++;
                string when = s.EndTick < 0
                    ? GameText.Format("reaction.attr.ongoing", TickText(s.StartTick))
                    : GameText.Format("reaction.attr.ended", TickText(s.StartTick), TickText(s.EndTick));
                lines.Add((ReactionAttribution.Title(s) + "　" + when + "　" + GameText.Format("reaction.attr.total", UiFormat.Number(System.Math.Round(s.TotalDamage))), "rl-row-title"));
                List<ReactionAttribution.Share> shares = ReactionAttribution.SharesOf(s);
                if (shares.Count == 0)
                {
                    lines.Add(("　" + GameText.Get("reaction.attr.none"), "rl-row-dim"));
                    continue;
                }
                foreach (ReactionAttribution.Share sh in shares)
                {
                    lines.Add(("　" + GameText.Format("reaction.attr.item", ReactionAttribution.DisplayName(state, sh.ReactionId), ReactionAttribution.Percent(sh.Fraction), sh.Count), null));
                }
            }
            return shown;
        }

        /// <summary>累计段（统计面板）：“累计（N 场）”“敌方共受伤害 X，其中反应额外伤害占 Y%”，下面逐条反应的合计占比与次数。没有符合筛选的场次时不写，返回 0。</summary>
        public static int AppendTotals(CampaignState state, ReactionLogFilter filter, List<(string Text, string Cls)> lines)
        {
            List<ReactionAttribution.Share> totals = ReactionAttribution.Aggregate(state, filter, out double total, out double reaction, out int sessions);
            if (sessions == 0)
            {
                return 0;
            }
            lines.Add((GameText.Format("stats.panel.totals_title", sessions), "rl-row-title"));
            double fraction = total > 0 ? System.Math.Min(1.0, reaction / total) : 0;
            lines.Add(("　" + GameText.Format("stats.panel.totals_line", UiFormat.Number(System.Math.Round(total)), ReactionAttribution.Percent(fraction)), null));
            if (totals.Count == 0)
            {
                lines.Add(("　" + GameText.Get("reaction.attr.none"), "rl-row-dim"));
                return sessions;
            }
            foreach (ReactionAttribution.Share sh in totals)
            {
                lines.Add(("　" + GameText.Format("reaction.attr.item", ReactionAttribution.DisplayName(state, sh.ReactionId), ReactionAttribution.Percent(sh.Fraction), sh.Count), null));
            }
            return sessions;
        }

        public static string TickText(long tick) => GameClock.FormatDayTime(tick / (double)System.Math.Max(1, GameClock.StepHz));
    }
}
