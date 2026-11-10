using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Combat;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// EMG2-DISC-01（FG19 FGR-EMG-190 / 183 / 212；回改 FG10-REACT-01 的 ReactionBatchService）：首次发现。取代按阵营批次开放命名：任何具名交互第一次结算即报名字、进图鉴、播首次反馈。
    /// - 具名交互的记录仍是 CampaignState.ReactionFirstTriggers（写入口 ReactionFeedback.RecordFirst）；语法条目记在旁边的 GrammarDiscoveries（缺省为空，不升 schema）。
    /// - “新发现”通知（类型 ID 沿用 reaction_batch，信息级，同类聚合）、引导钩子 emg_first_discovery、本场新发现数都从这里发。
    /// - 旧档：OpenReactionBatches 里的批次当时已通知过名字，这些反应第一次结算照常写记录与首次反馈，只是不再弹“新发现”（读档本身什么都不弹、不改记录）。
    /// </summary>
    public static class ReactionDiscoveryService
    {
        public const string NotifyType = "reaction_batch";

        /// <summary>本次运行发出的“新发现”通知条数（自检用）。</summary>
        public static int Posted { get; private set; }

        public static void ResetForTests()
        {
            Posted = 0;
            ExtremeMarked = 0;
            ExtremeRewarded = 0;
        }

        public static string GenEntry(int gen) => "gen." + gen.ToString(CultureInfo.InvariantCulture);

        public static string EventEntry(int eventKind) => "evt." + eventKind.ToString(CultureInfo.InvariantCulture);

        /// <summary>EMG3-META-01（DEBT-EMG2DISC01-03）：语法条目“第一次用到某种元程序”（种类 = 端口码 − 200）。</summary>
        public static string MetaEntry(int metaKind) => "meta." + metaKind.ToString(CultureInfo.InvariantCulture);

        /// <summary>RecordFirst 写入新记录之后调用。</summary>
        public static void OnReactionDiscovered(CampaignState state, string reactionId, string siteId, Vector3? at)
        {
            Discovered(state, siteId);
            if (!NamedReactionCatalog.WasAnnouncedByOldBatch(state, reactionId))
            {
                Post(GameText.Format("reaction.discovery.named", NamedReactionCatalog.NameOf(reactionId) ?? reactionId), at);
            }
        }

        /// <summary>写一条语法发现；已有返回 false（同一条只提示一次）。</summary>
        public static bool RecordGrammar(CampaignState state, string entryId, string siteId)
        {
            if (state == null || string.IsNullOrEmpty(entryId) || IsGrammarDiscovered(state, entryId))
            {
                return false;
            }
            var list = new List<GrammarDiscoveryRecord>(state.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>())
            {
                new GrammarDiscoveryRecord { EntryId = entryId, Tick = GameClock.Ticks, SiteId = siteId ?? string.Empty },
            };
            list.Sort((a, b) => string.CompareOrdinal(a.EntryId, b.EntryId));
            state.GrammarDiscoveries = list.ToArray();
            Progression.MechanicCodex.Unlock(Progression.MechanicCodex.GrammarEntryId(entryId)); // EMG4-CODEX-01：语法条目进组合图鉴（跨存档，D-17）
            Discovered(state, siteId);
            Post(GrammarText(entryId), null);
            return true;
        }

        public static bool IsGrammarDiscovered(CampaignState state, string entryId)
        {
            foreach (GrammarDiscoveryRecord r in state?.GrammarDiscoveries ?? Array.Empty<GrammarDiscoveryRecord>())
            {
                if (r != null && r.EntryId == entryId)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>语法条目的玩家文本（通知用；图鉴界面归 EMG4-CODEX-01）。</summary>
        public static string GrammarText(string entryId)
        {
            if (entryId.StartsWith("gen.", StringComparison.Ordinal))
            {
                return GameText.Format("reaction.discovery.gen", entryId.Substring(4));
            }
            if (entryId.StartsWith("meta.", StringComparison.Ordinal) && int.TryParse(entryId.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mk))
            {
                string pid = Emergence.EmgCatalog.Current.PortIdByCode.TryGetValue((ushort)(BinGames.Sim.Combat.EmgConst.PortMetaBase + mk), out string id) ? id : null;
                return GameText.Format("reaction.discovery.meta", pid != null ? GameText.Get("emg.port." + pid + ".name") : entryId);
            }
            return int.TryParse(entryId.Substring(entryId.IndexOf('.') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e)
                ? GameText.Format("reaction.discovery.event", Emergence.EmgProgramTreeText.EventName((BinGames.Sim.Combat.EmgEventKind)e))
                : entryId;
        }

        // ─────────────────────────────── EMG4-CODEX-01：极限奖励 / 发现计数 ───────────────────────────────

        /// <summary>技术数据奖励的事务号前缀（统计面板“组合图鉴极限”行，TechDataFlow.Codex）。</summary>
        public const string ExtremeTxPrefix = "codex-extreme:";
        public const string ExtremeNotifyType = "codex_extreme";

        /// <summary>本次运行记下的“极限”条数与发出的奖励次数（自检用）。</summary>
        public static int ExtremeMarked { get; private set; }
        public static int ExtremeRewarded { get; private set; }

        /// <summary>一次根出手结算多少次交互记“极限”（emg.codex.extreme_count；内核 CombatConfig.EmgExtremeCount 同一个值）。</summary>
        public static int ExtremeCount => Math.Max(1, (int)Math.Round(Feedback.ReactionPopups.Tuning("emg.codex.extreme_count", BinGames.Sim.Combat.EmgConst.DefaultExtremeCount)));

        public static int ExtremeReward => Math.Max(0, (int)Math.Round(Feedback.ReactionPopups.Tuning("emg.codex.extreme_reward", 20f)));

        /// <summary>
        /// FGR-EMG-194（FG19 第 14 节 #14 按推荐默认执行）：内核报来一次根出手达到链深 3 或结算 ≥ emg.codex.extreme_count 次交互，落在具名交互 <paramref name="reactionId"/> 上——
        /// 本存档第一次时在条目上记“极限”，并按事务号 codex-extreme:&lt;条目&gt; 给一次技术数据（经济账本去重：同一条只发一次，读档后也不重发）。只记录、不封堵、不改数值。
        /// 还没有发现记录的（理论上同一步先发现后记极限，不会出现）不记。返回 true = 这一次新记了极限。
        /// </summary>
        public static bool OnExtreme(CampaignState state, string reactionId, string siteId)
        {
            ReactionFirstTriggerRecord r = Combat.ReactionFeedback.FirstRecordOf(state, reactionId);
            if (r == null || r.Extreme)
            {
                return false;
            }
            int reward = ExtremeReward;
            string tx = ExtremeTxPrefix + reactionId;
            bool paid = false;
            ResourceTransactionRecord rec = CampaignEconomyLedger.Find(state, tx);
            if (reward > 0 && rec?.State != ResourceTransactionState.Committed)
            {
                if (rec == null)
                {
                    CampaignEconomyLedger.ProposeProduce(state, tx, reactionId, CampaignEconomyLedger.ResourceTechData, reward);
                    CampaignEconomyLedger.Reserve(state, tx);
                    CampaignEconomyLedger.MarkRunning(state, tx);
                }
                paid = CampaignEconomyLedger.Commit(state, tx).Success;
                if (!paid)
                {
                    return false; // 账本没记上：先不标极限，下一次达到极限再试（不吞奖励）
                }
                ExtremeRewarded++;
                Economy.HomeInventory.Touch();
            }
            r.Extreme = true;
            r.ExtremeTick = GameClock.Ticks;
            r.ExtremeSiteId = siteId ?? string.Empty;
            ExtremeMarked++;
            Progression.MechanicCodex.Touch();
            string name = NamedReactionCatalog.NameOf(reactionId) ?? reactionId;
            NotificationCenter.Post(ExtremeNotifyType, paid ? GameText.Format("codex.combo.extreme_notify", name, reward) : GameText.Format("codex.combo.extreme_notify_nopay", name), null);
            return true;
        }

        /// <summary>
        /// 成就“组合图鉴发现 N 种交互”（FG15-SYS-04）与结局统计发现数（FG13-END-06）的计数接口：组合图鉴里已解锁的具名交互条目数（跨存档，D-17）；
        /// <paramref name="withGrammar"/> 另加语法条目。只读图鉴解锁集合，O(条目数)。
        /// </summary>
        public static int DiscoveredInteractionCount(bool withGrammar = false) =>
            Progression.MechanicCodex.UnlockedCountOf(Progression.MechanicCodexKind.Reaction) + (withGrammar ? Progression.MechanicCodex.UnlockedCountOf(Progression.MechanicCodexKind.Grammar) : 0);

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

        private static void Discovered(CampaignState state, string siteId)
        {
            ReactionAttribution.AddDiscovery(state, siteId);
            GuidanceHooks.Raise(GuidanceHooks.EmgFirstDiscovery);
        }

        private static void Post(string detail, Vector3? at)
        {
            NotificationCenter.Post(NotifyType, detail, at);
            Posted++;
        }
    }
}
