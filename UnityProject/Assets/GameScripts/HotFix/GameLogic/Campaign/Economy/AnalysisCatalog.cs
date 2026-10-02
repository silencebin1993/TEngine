using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>一类可以送进解析台的物品（fg.TbAnalysisKind 一行的运行时视图）。</summary>
    public sealed class AnalysisKindDef
    {
        public string ItemId;
        public ItemDef Item;
        /// <summary>解析一件的游戏秒（1x；残骸 = 处理一件）。</summary>
        public float Seconds;
        public int TechFirst;
        public int TechRepeat;
        /// <summary>解析后物品是否消耗（加密固件 = 否：变成已破解的固件芯片，FGR-RND-021）。</summary>
        public bool Consumed;
        /// <summary>走解析队列（三类敌方物品）；否 = 队列空闲时处理（残骸，FGR-RND-024）。</summary>
        public bool Queued;
        public int SortOrder;

        public string Name => Item != null ? Item.Name : ItemId;
    }

    /// <summary>数据核心首次解析读到的一份资料（fg.TbAnalysisLore）。</summary>
    public sealed class AnalysisLoreDef
    {
        public string Id;
        public string TitleKey;
        public string BodyKey;
        public int SortOrder;

        public string Title => GameText.Get(TitleKey);
        public string Body => GameText.Get(BodyKey);
    }

    /// <summary>
    /// FG5-RND-02（FG05 FGR-RND-020～024）：解析台 2.0 的内容表——fg.TbAnalysisKind（三类敌方物品与残骸的时间 / 产出 / 是否消耗）、
    /// fg.TbAnalysisLore（背景资料）与 analysis.* 调参。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>，运行时不抛异常。
    /// 数据源 tools/cell_tables/fgdata_analysis.py。
    /// </summary>
    public static class AnalysisCatalog
    {
        public const string UnparsedModuleId = "unparsed_module";
        public const string EncryptedFirmwareId = "encrypted_firmware";
        public const string DataCoreId = "data_core";
        public const string WreckId = "enemy_wreck";

        private static readonly Dictionary<string, AnalysisKindDef> Kinds = new Dictionary<string, AnalysisKindDef>(StringComparer.Ordinal);
        private static readonly List<AnalysisKindDef> KindList = new List<AnalysisKindDef>(4);
        private static readonly Dictionary<string, AnalysisLoreDef> LoreById = new Dictionary<string, AnalysisLoreDef>(StringComparer.Ordinal);
        private static readonly List<AnalysisLoreDef> LoreList = new List<AnalysisLoreDef>(8);
        private static readonly List<string> ProblemList = new List<string>();
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);
        private static bool _loaded;
        private static ulong _beltMask;

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        public static IReadOnlyList<AnalysisKindDef> AllKinds
        {
            get
            {
                EnsureLoaded();
                return KindList;
            }
        }

        public static IReadOnlyList<AnalysisLoreDef> Lore
        {
            get
            {
                EnsureLoaded();
                return LoreList;
            }
        }

        /// <summary>解析台输入口的收货集合（位 i = 传送带物品编号 i）：三类敌方物品 + 残骸。建传送带内核时写进配置。</summary>
        public static ulong BeltMask
        {
            get
            {
                EnsureLoaded();
                return _beltMask;
            }
        }

        public static bool TryGetKind(string itemId, out AnalysisKindDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(itemId) && Kinds.TryGetValue(itemId, out def);
        }

        public static bool TryGetKindByBelt(ushort beltId, out AnalysisKindDef def)
        {
            EnsureLoaded();
            foreach (AnalysisKindDef k in KindList)
            {
                if (k.Item != null && k.Item.BeltId == beltId)
                {
                    def = k;
                    return true;
                }
            }
            def = null;
            return false;
        }

        public static bool TryGetLore(string id, out AnalysisLoreDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && LoreById.TryGetValue(id, out def);
        }

        /// <summary>FGR-RND-020：队列最多几项（初值 8）。</summary>
        public static int QueueCapacity => Math.Max(1, (int)Math.Round(Tuning("analysis.queue.capacity", 8f)));

        /// <summary>FGR-RND-024：残骸缓存最多几件。</summary>
        public static int WreckBufferCap => Math.Max(1, (int)Math.Round(Tuning("analysis.wreck.buffer", 20f)));

        /// <summary>队列里保留几条最近结束的记录（面板“最近结束”）。</summary>
        public static int HistoryKeep => Math.Max(0, (int)Math.Round(Tuning("analysis.history.keep", 6f)));

        public static void Reload()
        {
            _loaded = false;
            Revision++;
            EnsureLoaded();
        }

        public static void ResetForTests() => Reload();

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[AnalysisCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_analysis.py 后重新生成）。");
            }
            return fallback;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            Kinds.Clear();
            KindList.Clear();
            LoreById.Clear();
            LoreList.Clear();
            ProblemList.Clear();
            _beltMask = 0UL;
            TbAnalysisKind kinds = null;
            TbAnalysisLore lore = null;
            try
            {
                kinds = ConfigSystem.Instance.Tables?.TbAnalysisKind;
                lore = ConfigSystem.Instance.Tables?.TbAnalysisLore;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbAnalysisKind / fg.TbAnalysisLore 失败：" + e.Message);
            }
            if (kinds == null || lore == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbAnalysisKind / fg.TbAnalysisLore 不存在（改 tools/cell_tables/fgdata_analysis.py 后重新生成）");
                }
                Log.Error("[AnalysisCatalog] " + ProblemList[0]);
                return;
            }
            foreach (AnalysisKind row in kinds.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id))
                {
                    continue;
                }
                if (!ItemCatalog.TryGet(row.Id, out ItemDef item) || item.Form != ItemForm.Solid || item.BeltId <= 0 || item.BeltId >= 64)
                {
                    ProblemList.Add($"fg.TbAnalysisKind {row.Id}：不是物品表里能上传送带的固体（编号 1～63）");
                    continue;
                }
                if (row.Seconds <= 0f || row.TechFirst < 0 || row.TechRepeat < 0)
                {
                    ProblemList.Add($"fg.TbAnalysisKind {row.Id}：时间要 > 0、技术数据 >= 0");
                    continue;
                }
                var def = new AnalysisKindDef
                {
                    ItemId = row.Id, Item = item, Seconds = row.Seconds, TechFirst = row.TechFirst, TechRepeat = row.TechRepeat,
                    Consumed = row.Consumed != 0, Queued = row.Queued != 0, SortOrder = row.SortOrder,
                };
                Kinds[def.ItemId] = def;
                KindList.Add(def);
                _beltMask |= 1UL << item.BeltId;
            }
            KindList.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            foreach (string need in new[] { UnparsedModuleId, EncryptedFirmwareId, DataCoreId, WreckId })
            {
                if (!Kinds.ContainsKey(need))
                {
                    ProblemList.Add($"fg.TbAnalysisKind 缺 {need}");
                }
            }
            if (Kinds.TryGetValue(EncryptedFirmwareId, out AnalysisKindDef fw) && fw.Consumed)
            {
                ProblemList.Add("FGR-RND-021：加密固件解析后不消耗（consumed = 0）");
            }
            if (Kinds.TryGetValue(WreckId, out AnalysisKindDef wk) && wk.Queued)
            {
                ProblemList.Add("FGR-RND-024：残骸在队列空闲时处理（queued = 0）");
            }
            foreach (AnalysisLore row in lore.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || LoreById.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbAnalysisLore：空 ID 或重复 ID");
                    continue;
                }
                var def = new AnalysisLoreDef { Id = row.Id, TitleKey = row.TitleKey, BodyKey = row.BodyKey, SortOrder = row.SortOrder };
                LoreById[def.Id] = def;
                LoreList.Add(def);
            }
            LoreList.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            if (ProblemList.Count > 0)
            {
                Log.Error("[AnalysisCatalog] " + string.Join("；", ProblemList));
            }
        }
    }
}
