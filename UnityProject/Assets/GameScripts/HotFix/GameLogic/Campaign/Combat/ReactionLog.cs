using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>反应日志里的一方（触发者 / 目标）：只存 ID，显示时按当前语言翻译。</summary>
    public struct ReactionLogParty
    {
        /// <summary>内核单位种类；<see cref="Known"/> = false 时是查不到的单位（已阵亡被回收等）。</summary>
        public CombatUnitKind Kind;
        public bool Known;
        /// <summary>机器的 LogicId（其余为 0）。</summary>
        public int LogicId;
        /// <summary>敌人的类型内容 ID（其余为空）。</summary>
        public string ContentId;
    }

    /// <summary>反应日志的一条：同一步里同一条反应合并成一条（次数 = 这一步触发了几次，触发者 / 目标 = 这一步最后一次的）。</summary>
    public sealed class ReactionLogEntry
    {
        public long Serial;
        public long Tick;
        public string SiteId;
        /// <summary>所属场次种类（<see cref="ReactionAttribution.KindExpedition"/> / <see cref="ReactionAttribution.KindRaid"/>；家园平时为空）。</summary>
        public string SessionKind;
        public string ReactionId;
        public bool Named;
        /// <summary>本存档里这条反应第一次报出名字的那一条。</summary>
        public bool First;
        public int Count;
        public ReactionLogParty Source;
        public ReactionLogParty Target;
        /// <summary>触发者身上参与这条反应的固件（产生的标签属于这条反应的配料）；炮塔原型 / 敌人没有固件时为空。</summary>
        public string[] FirmwareIds = Array.Empty<string>();
        public Vector2 Position;
    }

    /// <summary>反应日志筛选（FG2-FW-04 卡片“反应日志可以按远征和突袭筛选”）。</summary>
    public enum ReactionLogFilter
    {
        All = 0,
        Expedition = 1,
        Raid = 2,
    }

    /// <summary>
    /// FG2-FW-04（FG02 FGR-FW-043“反应日志：保留最近 50 条，每条写明触发者（机器或炮塔）、参与的固件、目标”；第 6 节“反应日志不存档（只保留本次会话）”）。
    /// - 由 <see cref="ReactionFeedback"/> 每步按反应计数的增量写入（可靠：不依赖可能被丢弃的提示事件），同一步同一条反应合并成一条“×N”。
    /// - 容量 fg.TbUiTuning reaction.log_capacity，超出丢最旧；换一个战役（新建 / 读档）时清空（“本次会话”= 这次载入之后）。
    /// - 显示文字在读取时按当前语言拼（触发者“#3”/ 敌人名 / “炮塔”，固件名，目标）。
    /// </summary>
    public static class ReactionLog
    {
        private static readonly List<ReactionLogEntry> Entries = new List<ReactionLogEntry>(64);
        private static CampaignState _owner;
        private static long _serial;

        public static int Revision { get; private set; }

        public static int Capacity => Math.Max(1, ReactionPopups.TuningInt("reaction.log_capacity", 50));

        /// <summary>最旧在前。</summary>
        public static IReadOnlyList<ReactionLogEntry> All => Entries;

        public static void Add(CampaignState state, ReactionLogEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            if (!ReferenceEquals(_owner, state))
            {
                Entries.Clear();
                _owner = state;
            }
            entry.Serial = ++_serial;
            Entries.Add(entry);
            int cap = Capacity;
            if (Entries.Count > cap)
            {
                Entries.RemoveRange(0, Entries.Count - cap);
            }
            Revision++;
        }

        /// <summary>按筛选取（最新在前）。</summary>
        public static List<ReactionLogEntry> Filtered(ReactionLogFilter filter, CampaignState state = null)
        {
            var list = new List<ReactionLogEntry>(Entries.Count);
            if (state != null && !ReferenceEquals(_owner, state))
            {
                return list; // 日志属于另一个战役（已读档换了战役）：不给看旧战役的。
            }
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                ReactionLogEntry e = Entries[i];
                if (filter == ReactionLogFilter.All
                    || (filter == ReactionLogFilter.Expedition && e.SessionKind == ReactionAttribution.KindExpedition)
                    || (filter == ReactionLogFilter.Raid && e.SessionKind == ReactionAttribution.KindRaid))
                {
                    list.Add(e);
                }
            }
            return list;
        }

        public static void Clear()
        {
            Entries.Clear();
            _owner = null;
            Revision++;
        }

        // ─────────────────────────────── 显示 ───────────────────────────────

        public static string PartyLabel(in ReactionLogParty p)
        {
            if (!p.Known)
            {
                return GameText.Get("reaction.log.unknown");
            }
            switch (p.Kind)
            {
                case CombatUnitKind.Machine:
                {
                    string label = p.LogicId > 0 ? FeedbackCues.MachineLabel(p.LogicId) : string.Empty;
                    return string.IsNullOrEmpty(label) ? GameText.Get("reaction.log.machine") : GameText.Format("reaction.log.machine_n", label);
                }
                case CombatUnitKind.Turret:
                    return GameText.Get("reaction.log.turret");
                case CombatUnitKind.Structure:
                    return GameText.Get("reaction.log.structure");
                default:
                {
                    string name = FeedbackCues.ContentName(p.ContentId);
                    return string.IsNullOrEmpty(name) ? GameText.Get("reaction.log.enemy") : name;
                }
            }
        }

        public static string FirmwareLabel(ReactionLogEntry e)
        {
            if (e?.FirmwareIds == null || e.FirmwareIds.Length == 0)
            {
                return GameText.Get("reaction.log.no_firmware");
            }
            var names = new List<string>(e.FirmwareIds.Length);
            foreach (string id in e.FirmwareIds)
            {
                names.Add(FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row) ? GameText.Get(row.NameKey) : GameText.Get("reaction.log.unknown"));
            }
            return string.Join(GameText.Get("reaction.attr.sep"), names);
        }

        /// <summary>一条日志的完整文字：“第 2 日 08:14 · 短路 ×3 · #3（冷却液、电弧链）→ 静默侦察机”。</summary>
        public static string Describe(CampaignState state, ReactionLogEntry e)
        {
            if (e == null)
            {
                return string.Empty;
            }
            string name = e.Named ? NamedReactionCatalog.NameOf(e.ReactionId) : GameText.Get("reaction.unnamed");
            string what = e.Count > 1 ? GameText.Format("reaction.popup.many", name, e.Count) : name;
            if (e.First)
            {
                what = GameText.Format("reaction.log.first", what);
            }
            return GameText.Format("reaction.log.line", GameLogic.Core.GameClock.FormatDayTime(e.Tick / (double)System.Math.Max(1, GameLogic.Core.GameClock.StepHz)), what, PartyLabel(e.Source), FirmwareLabel(e), PartyLabel(e.Target));
        }
    }
}
