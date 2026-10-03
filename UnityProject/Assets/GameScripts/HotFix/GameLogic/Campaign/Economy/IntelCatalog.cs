using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>一类情报（fg.TbIntelKind 一行的运行时视图）。</summary>
    public sealed class IntelKindDef
    {
        public string Id;
        public string NameKey;
        public string DescKey;
        public string Glyph;
        public string Color;
        public int SortOrder;
        public float DecipherSeconds;
        /// <summary>有效期（游戏秒）；0 = 突袭预报，有效到预报的抵达窗口结束。</summary>
        public float ValiditySeconds;
        /// <summary>条件的种类：raid / expedition / boss / tier / act。</summary>
        public string ConditionKind;
        /// <summary>tier / act 的数字参数。</summary>
        public int ConditionArg;
        /// <summary>数据来源系统还没做时的承接 Story（这一类不会被破译）；就绪 = null。</summary>
        public string OpensIn;
        public string CodexId;

        public string Name => GameText.Get(NameKey);
        public bool IsReady => string.IsNullOrEmpty(OpensIn);
    }

    /// <summary>一个可以出弱点情报的首领（fg.TbIntelBoss）。</summary>
    public sealed class IntelBossDef
    {
        public string Id;
        public string NameKey;
        public string Rule;
        public string PhasesKey;
        public string WeaknessKey;
        public string AdviceKey;
        public int SortOrder;

        public string Name => GameText.Get(NameKey);
    }

    /// <summary>一个舰队信号片段（fg.TbIntelFragment）。</summary>
    public sealed class IntelFragmentDef
    {
        public string Id;
        public int Act;
        public int SortOrder;
        public string TextKey;
    }

    /// <summary>
    /// FG5-RND-05（FG05 FGR-RND-050～052）：情报的内容表——fg.TbIntelKind（五类情报）、fg.TbBuildingStack（多座同类建筑的边际加成，全游戏统一口径）、
    /// fg.TbIntelBoss（首领弱点）、fg.TbIntelFragment（舰队信号片段）与 intel.* 调参。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>、不抛异常。
    /// 数据源 tools/cell_tables/fgdata_intel.py。
    /// </summary>
    public static class IntelCatalog
    {
        public const string TypeId = "listening_post";
        public const string KindRaid = "raid_forecast";
        public const string KindCounter = "counter_preview";
        public const string KindBoss = "boss_weakness";
        public const string KindWeather = "weather";
        public const string KindFleet = "fleet_fragment";
        public const string BossRuleFoundryCore = "foundry_core";

        private static readonly List<IntelKindDef> Kinds = new List<IntelKindDef>(8);
        private static readonly Dictionary<string, IntelKindDef> KindById = new Dictionary<string, IntelKindDef>(StringComparer.Ordinal);
        private static readonly List<IntelBossDef> BossList = new List<IntelBossDef>(4);
        private static readonly List<IntelFragmentDef> FragmentList = new List<IntelFragmentDef>(8);
        private static readonly Dictionary<string, float[]> StackByType = new Dictionary<string, float[]>(StringComparer.Ordinal);
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

        /// <summary>五类情报（按优先级 / 面板顺序）。</summary>
        public static IReadOnlyList<IntelKindDef> All
        {
            get
            {
                EnsureLoaded();
                return Kinds;
            }
        }

        public static IReadOnlyList<IntelBossDef> Bosses
        {
            get
            {
                EnsureLoaded();
                return BossList;
            }
        }

        public static IReadOnlyList<IntelFragmentDef> Fragments
        {
            get
            {
                EnsureLoaded();
                return FragmentList;
            }
        }

        public static bool TryGet(string kindId, out IntelKindDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(kindId) && KindById.TryGetValue(kindId, out def);
        }

        public static string KindName(string kindId) => TryGet(kindId, out IntelKindDef d) ? d.Name : kindId ?? string.Empty;

        public static bool TryGetBoss(string bossId, out IntelBossDef def)
        {
            EnsureLoaded();
            def = null;
            foreach (IntelBossDef b in BossList)
            {
                if (b.Id == bossId)
                {
                    def = b;
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetFragment(string id, out IntelFragmentDef def)
        {
            EnsureLoaded();
            def = null;
            foreach (IntelFragmentDef f in FragmentList)
            {
                if (f.Id == id)
                {
                    def = f;
                    return true;
                }
            }
            return false;
        }

        // ── 多座同类建筑叠加（fg.TbBuildingStack；FGR-RND-052）──

        /// <summary>第 <paramref name="nth"/> 座（1 起）工作中的 <paramref name="typeId"/> 贡献多少速度。表里登记了这类建筑：按表（没登记的第 n 座 = 0）；
        /// 没登记的建筑类型：每座都是 1（各自独立、产出相加）。</summary>
        public static float StackBonus(string typeId, int nth)
        {
            EnsureLoaded();
            if (nth < 1)
            {
                return 0f;
            }
            if (!StackByType.TryGetValue(typeId ?? string.Empty, out float[] rows))
            {
                return 1f;
            }
            return nth <= rows.Length ? rows[nth - 1] : 0f;
        }

        /// <summary><paramref name="count"/> 座工作中的同类建筑合计的速度倍率（千分之一精度，存档里的进度按整数累计）。</summary>
        public static int StackRateMilli(string typeId, int count)
        {
            int milli = 0;
            for (int n = 1; n <= count; n++)
            {
                milli += (int)Math.Round(StackBonus(typeId, n) * 1000f);
            }
            return milli;
        }

        /// <summary>登记了几座有加成（第几座起不再提速 = 返回值 + 1）。</summary>
        public static int StackRows(string typeId)
        {
            EnsureLoaded();
            return StackByType.TryGetValue(typeId ?? string.Empty, out float[] rows) ? rows.Length : 0;
        }

        // ── 调参（fg.TbHomeTuning intel.*）──

        public static float RaidWindowFrac => Math.Max(0f, Tuning("intel.raid_window_frac", 0.15f));
        public static float RaidWindowMinSeconds => Math.Max(0f, Tuning("intel.raid_window_min_seconds", 20f));
        public static int KeepPerKind => Math.Max(1, (int)Math.Round(Tuning("intel.keep_per_kind", 6f)));
        public static float MapArrowCells => Math.Max(10f, Tuning("intel.map_arrow_cells", 80f));

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
                Log.Error($"[IntelCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_intel.py 后重新生成）。");
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
            KindById.Clear();
            BossList.Clear();
            FragmentList.Clear();
            StackByType.Clear();
            ProblemList.Clear();
            TbIntelKind kinds = null;
            TbBuildingStack stack = null;
            TbIntelBoss bosses = null;
            TbIntelFragment fragments = null;
            try
            {
                kinds = ConfigSystem.Instance.Tables?.TbIntelKind;
                stack = ConfigSystem.Instance.Tables?.TbBuildingStack;
                bosses = ConfigSystem.Instance.Tables?.TbIntelBoss;
                fragments = ConfigSystem.Instance.Tables?.TbIntelFragment;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbIntelKind / fg.TbBuildingStack / fg.TbIntelBoss / fg.TbIntelFragment 失败：" + e.Message);
            }
            if (kinds == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbIntelKind 不存在（改 tools/cell_tables/fgdata_intel.py 后重新生成）");
                }
                Log.Error("[IntelCatalog] " + ProblemList[0]);
                return;
            }
            foreach (IntelKind row in kinds.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || KindById.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbIntelKind：空 ID 或重复 ID");
                    continue;
                }
                string cond = row.Condition ?? string.Empty;
                int colon = cond.IndexOf(':');
                string head = colon >= 0 ? cond.Substring(0, colon) : cond;
                int arg = 0;
                if (colon >= 0 && !int.TryParse(cond.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out arg))
                {
                    ProblemList.Add($"fg.TbIntelKind {row.Id}：条件 {cond} 的数字不合法");
                    continue;
                }
                if (head != "raid" && head != "expedition" && head != "boss" && head != "tier" && head != "act")
                {
                    ProblemList.Add($"fg.TbIntelKind {row.Id}：条件 {cond} 不认识");
                    continue;
                }
                if (row.DecipherSeconds <= 0f)
                {
                    ProblemList.Add($"fg.TbIntelKind {row.Id}：破译时间要 > 0");
                    continue;
                }
                var def = new IntelKindDef
                {
                    Id = row.Id, NameKey = row.NameKey, DescKey = row.DescKey, Glyph = row.Glyph, Color = row.Color, SortOrder = row.SortOrder,
                    DecipherSeconds = row.DecipherSeconds, ValiditySeconds = Math.Max(0f, row.ValiditySeconds), ConditionKind = head, ConditionArg = arg,
                    OpensIn = string.IsNullOrEmpty(row.OpensIn) || row.OpensIn == "none" ? null : row.OpensIn, CodexId = row.CodexId,
                };
                KindById[def.Id] = def;
                Kinds.Add(def);
            }
            Kinds.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            if (stack != null)
            {
                var tmp = new Dictionary<string, SortedDictionary<int, float>>(StringComparer.Ordinal);
                foreach (BuildingStack row in stack.DataList)
                {
                    if (row == null || string.IsNullOrEmpty(row.TypeId) || row.Nth < 1)
                    {
                        ProblemList.Add("fg.TbBuildingStack：建筑类型为空或第几座 < 1");
                        continue;
                    }
                    if (!tmp.TryGetValue(row.TypeId, out SortedDictionary<int, float> d))
                    {
                        tmp[row.TypeId] = d = new SortedDictionary<int, float>();
                    }
                    d[row.Nth] = Math.Max(0f, row.Bonus);
                }
                foreach (KeyValuePair<string, SortedDictionary<int, float>> kv in tmp)
                {
                    var arr = new float[kv.Value.Count];
                    int i = 0;
                    foreach (KeyValuePair<int, float> e in kv.Value)
                    {
                        if (e.Key != i + 1)
                        {
                            ProblemList.Add($"fg.TbBuildingStack {kv.Key}：第几座必须从 1 连续编号");
                        }
                        arr[i++] = e.Value;
                    }
                    StackByType[kv.Key] = arr;
                }
            }
            if (bosses != null)
            {
                foreach (IntelBoss row in bosses.DataList)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id))
                    {
                        continue;
                    }
                    if (row.Rule != BossRuleFoundryCore)
                    {
                        ProblemList.Add($"fg.TbIntelBoss {row.Id}：规则 {row.Rule} 代码里没有");
                        continue;
                    }
                    BossList.Add(new IntelBossDef
                    {
                        Id = row.Id, NameKey = row.NameKey, Rule = row.Rule, PhasesKey = row.PhasesKey, WeaknessKey = row.WeaknessKey,
                        AdviceKey = row.AdviceKey, SortOrder = row.SortOrder,
                    });
                }
                BossList.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            }
            if (fragments != null)
            {
                foreach (IntelFragment row in fragments.DataList)
                {
                    if (row != null && !string.IsNullOrEmpty(row.Id))
                    {
                        FragmentList.Add(new IntelFragmentDef { Id = row.Id, Act = row.Act, SortOrder = row.SortOrder, TextKey = row.TextKey });
                    }
                }
                FragmentList.Sort((a, b) => a.Act != b.Act ? a.Act.CompareTo(b.Act) : a.SortOrder.CompareTo(b.SortOrder));
            }
            if (ProblemList.Count > 0)
            {
                Log.Error("[IntelCatalog] " + string.Join("；", ProblemList));
            }
        }
    }
}
