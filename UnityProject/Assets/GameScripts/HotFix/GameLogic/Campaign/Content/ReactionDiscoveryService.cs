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

        public static void ResetForTests() => Posted = 0;

        public static string GenEntry(int gen) => "gen." + gen.ToString(CultureInfo.InvariantCulture);

        public static string EventEntry(int eventKind) => "evt." + eventKind.ToString(CultureInfo.InvariantCulture);

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
            return int.TryParse(entryId.Substring(entryId.IndexOf('.') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e)
                ? GameText.Format("reaction.discovery.event", Emergence.EmgProgramTreeText.EventName((BinGames.Sim.Combat.EmgEventKind)e))
                : entryId;
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
