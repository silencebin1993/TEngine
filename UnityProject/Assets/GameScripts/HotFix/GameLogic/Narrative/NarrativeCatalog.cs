using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Narrative
{
    /// <summary>资料库的一条条目（fg.TbNarrativeEntry 一行的运行时视图）。</summary>
    public sealed class NarrativeEntryDef
    {
        public string Id;
        /// <summary>投放方式：<see cref="NarrativeCatalog.SourceMemo"/> / Terminal / Intercept / Cutscene。</summary>
        public string Source;
        /// <summary>1～3 = 属于第几幕；0 = 按收到时的幕记。</summary>
        public int Act;
        /// <summary>剧情节点（进“剧情回顾”）。</summary>
        public bool Node;
        public string Trigger;
        public string TitleKey;
        public string BodyKey;
        /// <summary>收到后战役推进到第几幕（0 = 不改）。</summary>
        public int SetAct;
        public int SortOrder;
        /// <summary>演出的逐行字幕（按序号；非演出为空）。</summary>
        public readonly List<NarrativeLineDef> Lines = new List<NarrativeLineDef>();

        public bool IsCutscene => Source == NarrativeCatalog.SourceCutscene;
    }

    /// <summary>演出的一行字幕（fg.TbNarrativeLine）。</summary>
    public sealed class NarrativeLineDef
    {
        public string TextKey;
        public float HoldSeconds;
    }

    /// <summary>
    /// FG9-NAR-01（FG11 FGR-NAR-001 / 003 / 010）：资料库的内容表——fg.TbNarrativeEntry（四种投放方式的条目、触发条件、幕、剧情节点）与
    /// fg.TbNarrativeLine（演出逐行字幕）。只读；载入时逐行校验，坏行记 <see cref="Problems"/> 并跳过，运行时不抛异常。
    /// 数据源 tools/cell_tables/fgdata_narrative.py（数据核心资料 / 舰队片段 / 核心备忘 / 旧终端记录从各自的源数据派生）。
    /// 触发 → 条目一一对应（<see cref="TryGetByTrigger"/>，O(1)）。
    /// </summary>
    public static class NarrativeCatalog
    {
        public const string SourceMemo = "memo";
        public const string SourceTerminal = "terminal";
        public const string SourceIntercept = "intercept";
        public const string SourceCutscene = "cutscene";
        public static readonly string[] Sources = { SourceMemo, SourceTerminal, SourceIntercept, SourceCutscene };

        public const string TriggerNewGame = "new_game";
        public const string TriggerSite = "site:";
        public const string TriggerLeader = "leader:";
        public const string TriggerMemo = "memo:";
        public const string TriggerTerminal = "terminal:";
        public const string TriggerIntel = "intel:";
        public const string TriggerAnalysis = "analysis:";

        private static readonly Dictionary<string, NarrativeEntryDef> ById = new Dictionary<string, NarrativeEntryDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, NarrativeEntryDef> ByTrigger = new Dictionary<string, NarrativeEntryDef>(StringComparer.Ordinal);
        private static readonly List<NarrativeEntryDef> List = new List<NarrativeEntryDef>(32);
        private static readonly List<string> ProblemList = new List<string>();
        private static bool _loaded;

        /// <summary>FG9-UX-01：每次（重新）载入 +1（图鉴的资料库页签据此重建条目）。</summary>
        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<NarrativeEntryDef> All
        {
            get
            {
                EnsureLoaded();
                return List;
            }
        }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        public static bool TryGet(string id, out NarrativeEntryDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out def);
        }

        public static NarrativeEntryDef Find(string id) => TryGet(id, out NarrativeEntryDef d) ? d : null;

        public static bool TryGetByTrigger(string trigger, out NarrativeEntryDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(trigger) && ByTrigger.TryGetValue(trigger, out def);
        }

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
        }

        public static void ResetForTests() => _loaded = false;

        public static string SourceTextKey(string source) => "narrative.source." + (string.IsNullOrEmpty(source) ? "all" : source);

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            Revision++;
            ById.Clear();
            ByTrigger.Clear();
            List.Clear();
            ProblemList.Clear();
            TbNarrativeEntry entries = null;
            TbNarrativeLine lines = null;
            try
            {
                entries = ConfigSystem.Instance.Tables?.TbNarrativeEntry;
                lines = ConfigSystem.Instance.Tables?.TbNarrativeLine;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbNarrativeEntry / fg.TbNarrativeLine 失败：" + e.Message);
            }
            if (entries == null || lines == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbNarrativeEntry / fg.TbNarrativeLine 不存在（改 tools/cell_tables/fgdata_narrative.py 后重新生成）");
                }
                Log.Error("[NarrativeCatalog] " + ProblemList[0]);
                return;
            }
            foreach (NarrativeEntry row in entries.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id))
                {
                    continue;
                }
                if (Array.IndexOf(Sources, row.Source) < 0 || row.Act < 0 || row.Act > 3 || row.SetAct < 0 || row.SetAct > 3
                    || string.IsNullOrEmpty(row.Trigger) || string.IsNullOrEmpty(row.TitleKey) || string.IsNullOrEmpty(row.BodyKey))
                {
                    ProblemList.Add($"fg.TbNarrativeEntry {row.Id}：投放方式 / 幕 / 触发 / 文本键不合法，已跳过");
                    continue;
                }
                if (ByTrigger.ContainsKey(row.Trigger))
                {
                    ProblemList.Add($"fg.TbNarrativeEntry {row.Id}：触发 {row.Trigger} 已被 {ByTrigger[row.Trigger].Id} 使用，已跳过");
                    continue;
                }
                var def = new NarrativeEntryDef
                {
                    Id = row.Id, Source = row.Source, Act = row.Act, Node = row.Node != 0, Trigger = row.Trigger,
                    TitleKey = row.TitleKey, BodyKey = row.BodyKey, SetAct = row.SetAct, SortOrder = row.SortOrder,
                };
                ById[def.Id] = def;
                ByTrigger[def.Trigger] = def;
                List.Add(def);
            }
            var sorted = new List<NarrativeLine>(lines.DataList);
            sorted.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (NarrativeLine row in sorted)
            {
                if (row == null || !ById.TryGetValue(row.EntryId ?? string.Empty, out NarrativeEntryDef owner) || !owner.IsCutscene
                    || string.IsNullOrEmpty(row.TextKey) || row.HoldSeconds <= 0f)
                {
                    ProblemList.Add($"fg.TbNarrativeLine {row?.Id}：所属演出不存在 / 文本键为空 / 停留 ≤ 0，已跳过");
                    continue;
                }
                owner.Lines.Add(new NarrativeLineDef { TextKey = row.TextKey, HoldSeconds = row.HoldSeconds });
            }
            foreach (NarrativeEntryDef d in List)
            {
                if (d.IsCutscene && d.Lines.Count == 0)
                {
                    ProblemList.Add($"演出 {d.Id} 没有字幕行（播放时只显示正文）");
                }
            }
            List.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
        }
    }
}
