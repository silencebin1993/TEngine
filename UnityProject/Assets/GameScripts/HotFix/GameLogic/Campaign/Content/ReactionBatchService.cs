using System;
using System.Collections.Generic;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG10-REACT-01（FG02 FGR-FW-042“具名反应随阵营推进逐批解锁”）：正式流程里开放一批反应命名的唯一调用方。
    /// - 化工批（爆燃 / 腐蚀灼烧 / 胶结 / 溶蚀）：澄净的领地在战略地图上出现时开放（第二幕起，或玩家提前揭开它的危害带；与澄净反制情报行同一口径）。
    ///   开放前这几条照样按规则触发，只是不报名字、不进图鉴（NamedReactionCatalog.IsNamed）；开放后第一次触发才算首次（慢放 + 解锁图鉴，FG2-FW-04 的通用反馈）。
    /// - 开放时发一条通知，列出这一批可触发的反应名（不可触发的不列）。
    /// - 超频批由 FG11-REACT-01 加进 <see cref="OpenOnTerritoryVisible"/>；跨阵营批（FG-GAP-054）另定。
    /// 每游戏秒一次 O(批次)；与镜头无关（领地可见性记在存档里）。
    /// </summary>
    public static class ReactionBatchService
    {
        public const string NotifyType = "reaction_batch";

        /// <summary>批次 → 开放条件“这个阵营的领地在地图上显示”（批次 ID = 领地 ID）。</summary>
        private static readonly string[] OpenOnTerritoryVisible = { "clarity" };

        /// <summary>本次运行由正式流程开放的批次数（自检用）。</summary>
        public static int Opened { get; private set; }

        public static void ResetForTests() => Opened = 0;

        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            foreach (string batch in OpenOnTerritoryVisible)
            {
                if (NamedReactionCatalog.IsBatchOpen(s, batch))
                {
                    continue;
                }
                PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(batch);
                if (t != null && HomeExplorationService.TerritoryVisible(s, t))
                {
                    Open(s, batch);
                }
            }
        }

        /// <summary>开放一批并通知（已开放 / 批次不存在返回 false）。</summary>
        public static bool Open(CampaignState s, string batch)
        {
            if (!NamedReactionCatalog.OpenBatch(s, batch))
            {
                return false;
            }
            Opened++;
            var names = new List<string>();
            foreach (GameConfig.fg.Reaction row in NamedReactionCatalog.Rows)
            {
                if (row != null && row.Batch == batch && row.Reach == NamedReactionCatalog.Reachable)
                {
                    names.Add(GameText.Get(row.NameKey));
                }
            }
            NotificationCenter.Post(NotifyType, GameText.Format("reaction.batch.opened", GameText.Get("reaction.batch." + batch), string.Join("、", names)));
            return true;
        }

        /// <summary>这一批还没开放时，图鉴剪影下面写什么时候开放（有专门文本用专门的，否则用通用的）。</summary>
        public static string ClosedHint(string batch)
        {
            string key = "reaction.codex.closed_" + batch;
            return GameText.Has(key) ? GameText.Get(key) : GameText.Format("reaction.codex.closed", GameText.Get("reaction.batch." + batch));
        }

        /// <summary>图鉴里这条反应的打法提示（reaction.&lt;id&gt;.tip；没有返回 null）。</summary>
        public static string TipOf(GameConfig.fg.Reaction row)
        {
            if (row == null || string.IsNullOrEmpty(row.NameKey) || !row.NameKey.EndsWith(".name", StringComparison.Ordinal))
            {
                return null;
            }
            string key = row.NameKey.Substring(0, row.NameKey.Length - ".name".Length) + ".tip";
            return GameText.Has(key) ? GameText.Get(key) : null;
        }
    }
}
