using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>一条熔合配方（fg.TbFusionRecipe 一行的运行时视图）。</summary>
    public sealed class FusionRecipeDef
    {
        public string Id;
        /// <summary>产出的混合固件（fg.TbFirmwareKind 里 mixA / mixB 不为 none 的行）。</summary>
        public string MixId;
        public string ParentA;
        public string ParentB;
        /// <summary>配方书类别（fuse / limiter / fluid / em）。</summary>
        public string Family;
        /// <summary>线索对应的具名反应（fg.TbReaction；一条父固件带它的标签 A、另一条带标签 B）。</summary>
        public string Reaction;
        public string NameKey;
        public int SortOrder;

        public string Name => GameText.Get(NameKey);

        /// <summary>这条配方是不是 <paramref name="a"/> + <paramref name="b"/>（不分先后）。</summary>
        public bool Matches(string a, string b) =>
            (ParentA == a && ParentB == b) || (ParentA == b && ParentB == a);

        public bool HasParent(string fw) => ParentA == fw || ParentB == fw;
    }

    /// <summary>
    /// FG5-RND-04（FG05 FGR-RND-040～045；FG02 FGR-FW-050）：熔合的内容表——fg.TbFusionRecipe（约 30 条配方）、fg.TbFusionMerge（读法字段合并上限）与 fusion.* 调参。
    /// 只读；载入时逐行校验（父固件是表里两条不同的正式固件、混合固件行的 mixA / mixB 与配方一致、类别合法、反应存在），表坏了记 <see cref="Problems"/>、不抛异常。
    /// 配方书“每个类别还剩几个未发现”按本表的条目数实时算（<see cref="CountInFamily"/>），不写死数字。数据源 tools/cell_tables/fgdata_fusion.py。
    /// </summary>
    public static class FusionCatalog
    {
        public const string TypeId = "circuit_synth";
        public static readonly string[] Families = { "fuse", "limiter", "fluid", "em" };

        private static readonly Dictionary<string, FusionRecipeDef> ById = new Dictionary<string, FusionRecipeDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FusionRecipeDef> ByMix = new Dictionary<string, FusionRecipeDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FusionRecipeDef> ByPair = new Dictionary<string, FusionRecipeDef>(StringComparer.Ordinal);
        private static readonly List<FusionRecipeDef> List = new List<FusionRecipeDef>(32);
        private static readonly Dictionary<string, float> Caps = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<string> ProblemList = new List<string>();
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);
        private static bool _loaded;

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        /// <summary>全部配方（按配方书排序）。</summary>
        public static IReadOnlyList<FusionRecipeDef> Recipes
        {
            get
            {
                EnsureLoaded();
                return List;
            }
        }

        public static bool TryGet(string recipeId, out FusionRecipeDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(recipeId) && ById.TryGetValue(recipeId, out def);
        }

        /// <summary>这条混合固件是哪条配方的产物。</summary>
        public static bool TryGetByMix(string mixId, out FusionRecipeDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(mixId) && ByMix.TryGetValue(mixId, out def);
        }

        /// <summary>两条固件（不分先后）有没有熔合配方。O(1)。</summary>
        public static bool TryGetByPair(string a, string b, out FusionRecipeDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && ByPair.TryGetValue(PairKey(a, b), out def);
        }

        public static string PairKey(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? a + "+" + b : b + "+" + a;

        /// <summary>这一类别的配方条数（配方书“还剩几个未发现”的分母，随表变化）。<paramref name="family"/> 为空 = 全部。</summary>
        public static int CountInFamily(string family)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(family))
            {
                return List.Count;
            }
            int n = 0;
            foreach (FusionRecipeDef d in List)
            {
                if (d.Family == family)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>FGR-FW-050：读法字段相加后的上限（表 fg.TbFusionMerge）。没登记的字段不设上限（check 保证都登记了）。</summary>
        public static bool TryGetCap(string field, out float cap)
        {
            EnsureLoaded();
            return Caps.TryGetValue(field ?? string.Empty, out cap);
        }

        /// <summary>按合并规则算混合固件的读法字段（字段 → 幅度，顺序：A 的在前，B 新增的接在后面）。自检用它核对表里的 readFields。</summary>
        public static List<(string Field, float Magnitude)> MergeFields(string parentA, string parentB)
        {
            var order = new List<string>();
            var sums = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (string p in new[] { parentA, parentB })
            {
                foreach ((string field, float mag) in CarrierReadings.FieldsOf(p))
                {
                    if (!sums.ContainsKey(field))
                    {
                        order.Add(field);
                        sums[field] = 0f;
                    }
                    sums[field] += mag;
                }
            }
            var list = new List<(string, float)>(order.Count);
            foreach (string f in order)
            {
                float v = sums[f];
                if (TryGetCap(f, out float cap))
                {
                    v = Math.Min(v, cap);
                }
                list.Add((f, v));
            }
            return list;
        }

        public static string FamilyName(string family) => GameText.Get(string.IsNullOrEmpty(family) ? "fusion.family.all" : "fusion.family." + family);

        // ── 调参（fg.TbHomeTuning fusion.*；FG05 第 10 节初值）──

        public static int SimTech => Math.Max(0, (int)Math.Round(Tuning("fusion.sim_tech", 5f)));
        public static int FormalTech => Math.Max(0, (int)Math.Round(Tuning("fusion.formal_tech", 30f)));
        public static int FormalSubstrate => Math.Max(0, (int)Math.Round(Tuning("fusion.formal_substrate", 2f)));
        public static float FormalSeconds => Math.Max(1f, Tuning("fusion.formal_seconds", 30f));
        public static int BurnSubstrate => Math.Max(1, (int)Math.Round(Tuning("fusion.burn_substrate", 3f)));
        public static int QueueMax => Math.Max(1, (int)Math.Round(Tuning("fusion.queue_max", 4f)));
        public static int LoadBonus => Math.Max(0, (int)Math.Round(Tuning("fusion.load_bonus", 1f)));
        public static int CluePartialMin => Math.Max(1, (int)Math.Round(Tuning("fusion.clue_partial_min", 3f)));
        public static int ClueFullMin => Math.Max(CluePartialMin, (int)Math.Round(Tuning("fusion.clue_full_min", 12f)));
        public static int CluePerSession => Math.Max(1, (int)Math.Round(Tuning("fusion.clue_per_session", 3f)));
        public static int ClueKeep => Math.Max(4, (int)Math.Round(Tuning("fusion.clue_keep", 60f)));
        public static int PendingKeep => Math.Max(1, (int)Math.Round(Tuning("fusion.pending_keep", 8f)));
        public static int SimLogKeep => Math.Max(4, (int)Math.Round(Tuning("fusion.sim_log_keep", 40f)));

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
                Log.Error($"[FusionCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_fusion.py 后重新生成）。");
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
            ById.Clear();
            ByMix.Clear();
            ByPair.Clear();
            List.Clear();
            Caps.Clear();
            ProblemList.Clear();
            TbFusionRecipe table = null;
            TbFusionMerge merge = null;
            try
            {
                table = ConfigSystem.Instance.Tables?.TbFusionRecipe;
                merge = ConfigSystem.Instance.Tables?.TbFusionMerge;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbFusionRecipe / fg.TbFusionMerge 失败：" + e.Message);
            }
            if (merge != null)
            {
                foreach (FusionMerge row in merge.DataList)
                {
                    if (row != null && !string.IsNullOrEmpty(row.Field))
                    {
                        Caps[row.Field] = row.Cap;
                    }
                }
            }
            if (table == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbFusionRecipe 不存在（改 tools/cell_tables/fgdata_fusion.py 后重新生成）");
                }
                Log.Error("[FusionCatalog] " + ProblemList[0]);
                return;
            }
            foreach (FusionRecipe row in table.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || ById.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbFusionRecipe：空 ID 或重复 ID");
                    continue;
                }
                if (string.IsNullOrEmpty(row.ParentA) || string.IsNullOrEmpty(row.ParentB) || row.ParentA == row.ParentB
                    || !FirmwareKinds.IsFirmware(row.ParentA) || !FirmwareKinds.IsFirmware(row.ParentB)
                    || FirmwareKinds.IsMixed(row.ParentA) || FirmwareKinds.IsMixed(row.ParentB))
                {
                    ProblemList.Add($"fg.TbFusionRecipe {row.Id}：父固件必须是两条不同的正式固件（混合固件不能再熔合）");
                    continue;
                }
                if (!FirmwareKinds.TryGetParents(row.MixId, out string ma, out string mb)
                    || !((ma == row.ParentA && mb == row.ParentB) || (ma == row.ParentB && mb == row.ParentA)))
                {
                    ProblemList.Add($"fg.TbFusionRecipe {row.Id}：产物 {row.MixId} 在固件表里不是这两条父固件的混合固件");
                    continue;
                }
                if (Array.IndexOf(Families, row.Family) < 0)
                {
                    ProblemList.Add($"fg.TbFusionRecipe {row.Id}：类别 {row.Family} 不认识");
                    continue;
                }
                string key = PairKey(row.ParentA, row.ParentB);
                if (ByPair.ContainsKey(key) || ByMix.ContainsKey(row.MixId))
                {
                    ProblemList.Add($"fg.TbFusionRecipe {row.Id}：父固件对或产物重复");
                    continue;
                }
                if (!NamedReactionCatalog.TryGet(row.Reaction, out _))
                {
                    ProblemList.Add($"fg.TbFusionRecipe {row.Id}：反应 {row.Reaction} 不在 fg.TbReaction");
                }
                var def = new FusionRecipeDef
                {
                    Id = row.Id, MixId = row.MixId, ParentA = row.ParentA, ParentB = row.ParentB, Family = row.Family,
                    Reaction = row.Reaction, NameKey = row.NameKey, SortOrder = row.SortOrder,
                };
                ById[def.Id] = def;
                ByMix[def.MixId] = def;
                ByPair[key] = def;
                List.Add(def);
            }
            List.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            if (ProblemList.Count > 0)
            {
                Log.Error("[FusionCatalog] " + string.Join("；", ProblemList));
            }
        }
    }
}
