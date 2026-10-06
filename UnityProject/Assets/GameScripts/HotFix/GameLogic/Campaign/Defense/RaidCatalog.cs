using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>一类突袭触发（fg.TbRaidTrigger 一行的运行时视图）。</summary>
    public sealed class RaidTriggerDef
    {
        public string Id;
        public string NameKey;
        public string DescKey;
        public bool Exempt;
        public string LevelRule;
        /// <summary>触发来源系统还没做时的承接 Story（只有接口与自检）；已接入 = null。</summary>
        public string OpensIn;
        public int SortOrder;

        public string Name => GameText.Get(NameKey);
    }

    /// <summary>编成表的一行（fg.TbRaidUnit）。</summary>
    public sealed class RaidUnitDef
    {
        public string Id;
        public string Faction;
        public string EnemyTypeId;
        public string Role;
        public int Points;
        public int MinLevel;
        public float Weight;
        public int ElitePoints;
        public string[] Tags;

        public bool HasTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }
            foreach (string t in Tags)
            {
                if (t == tag)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// FG6-DEF-04（FG06 FGR-DEF-020～024；FG16 第 3、5 节）：突袭导演的内容表——fg.TbRaidTrigger / TbRaidLevel / TbRaidDifficulty / TbRaidUnit / TbRaidCounter / TbRaidStory
    /// 与 raid.* 调参（fg.TbHomeTuning）。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>、不抛异常（导演按“不发动突袭”降级并在日志写原因）。
    /// 数据源 tools/cell_tables/fgdata_raid.py。
    /// </summary>
    public static class RaidCatalog
    {
        public const string TriggerExposure = "exposure";
        public const string TriggerStory = "story";
        public const string TriggerSilentNight = "silent_night";
        public const string TriggerMegastructure = "megastructure";
        public const string TriggerPurification = "purification";
        public const string TriggerHarass = "harass";
        public const string StoryRuleFoundryCore = "foundry_core";
        /// <summary>化工阵营（针对净化塔的触发固定由它发动，FG09）。</summary>
        public const string FactionChemical = "clarity";

        private static readonly List<RaidTriggerDef> TriggerList = new List<RaidTriggerDef>(8);
        private static readonly Dictionary<string, RaidTriggerDef> TriggerById = new Dictionary<string, RaidTriggerDef>(StringComparer.Ordinal);
        private static readonly Dictionary<int, RaidLevel> LevelByLevel = new Dictionary<int, RaidLevel>();
        private static readonly Dictionary<string, RaidDifficulty> DifficultyById = new Dictionary<string, RaidDifficulty>(StringComparer.Ordinal);
        private static readonly List<RaidUnitDef> UnitList = new List<RaidUnitDef>(16);
        private static readonly Dictionary<string, RaidUnitDef> UnitById = new Dictionary<string, RaidUnitDef>(StringComparer.Ordinal);
        private static readonly List<RaidCounter> CounterList = new List<RaidCounter>(16);
        private static readonly List<RaidStory> StoryList = new List<RaidStory>(4);
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

        public static IReadOnlyList<RaidTriggerDef> Triggers
        {
            get
            {
                EnsureLoaded();
                return TriggerList;
            }
        }

        public static IReadOnlyList<RaidUnitDef> Units
        {
            get
            {
                EnsureLoaded();
                return UnitList;
            }
        }

        public static IReadOnlyList<RaidCounter> Counters
        {
            get
            {
                EnsureLoaded();
                return CounterList;
            }
        }

        public static IReadOnlyList<RaidStory> Stories
        {
            get
            {
                EnsureLoaded();
                return StoryList;
            }
        }

        public static bool TryGetTrigger(string id, out RaidTriggerDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && TriggerById.TryGetValue(id, out def);
        }

        public static string TriggerName(string id) => TryGetTrigger(id, out RaidTriggerDef d) ? d.Name : id ?? string.Empty;

        public static bool TryGetUnit(string id, out RaidUnitDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && UnitById.TryGetValue(id, out def);
        }

        /// <summary>FG6-DEF-08：编成行的种类序号（表顺序下标 + 1；0 = 不在表里）。攻城单位的外部键带着它，被击毁时据此知道是哪种敌人。</summary>
        public static int KindOf(RaidUnitDef def)
        {
            EnsureLoaded();
            int i = def != null ? UnitList.IndexOf(def) : -1;
            return i >= 0 ? i + 1 : 0;
        }

        /// <summary>FG6-DEF-08：种类序号 → 编成行（0 / 越界 = null）。</summary>
        public static RaidUnitDef UnitOfKind(int kind)
        {
            EnsureLoaded();
            return kind > 0 && kind <= UnitList.Count ? UnitList[kind - 1] : null;
        }

        /// <summary>某阵营在给定等级下能出现的单位（表顺序）。</summary>
        public static void UnitsFor(string faction, int level, List<RaidUnitDef> into)
        {
            EnsureLoaded();
            into.Clear();
            foreach (RaidUnitDef u in UnitList)
            {
                if (u.Faction == faction && u.MinLevel <= level)
                {
                    into.Add(u);
                }
            }
        }

        /// <summary>这个阵营有没有突袭编成（没有的阵营不会被选中发动突袭；化工 / 超频的单位在 FG10-FAC-01 / FG11-FAC-01）。</summary>
        public static bool HasUnits(string faction)
        {
            EnsureLoaded();
            foreach (RaidUnitDef u in UnitList)
            {
                if (u.Faction == faction)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>阵营对某个固件类别的对策（没有 = null，不反制）。</summary>
        public static RaidCounter CounterFor(string faction, string category)
        {
            EnsureLoaded();
            foreach (RaidCounter c in CounterList)
            {
                if (c.Faction == faction && c.Category == category)
                {
                    return c;
                }
            }
            return null;
        }

        public static RaidCounter CounterById(string id)
        {
            EnsureLoaded();
            foreach (RaidCounter c in CounterList)
            {
                if (c.Id == id)
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>等级的基础预算（表里没有的等级按最近的已登记等级；表坏了 = 100）。</summary>
        public static int LevelBudget(int level)
        {
            EnsureLoaded();
            if (LevelByLevel.TryGetValue(level, out RaidLevel row))
            {
                return Math.Max(1, row.Budget);
            }
            int best = -1;
            foreach (int l in LevelByLevel.Keys)
            {
                if (l <= level && l > best)
                {
                    best = l;
                }
            }
            return best >= 0 ? Math.Max(1, LevelByLevel[best].Budget) : 100;
        }

        public static string LevelName(int level)
        {
            EnsureLoaded();
            return LevelByLevel.TryGetValue(level, out RaidLevel row) ? GameText.Get(row.NameKey) : level.ToString();
        }

        /// <summary>难度行（存档的难度 ID 在表里没有时按“标准”）。</summary>
        public static RaidDifficulty Difficulty(string id)
        {
            EnsureLoaded();
            if (!string.IsNullOrEmpty(id) && DifficultyById.TryGetValue(id, out RaidDifficulty d))
            {
                return d;
            }
            return DifficultyById.TryGetValue("Standard", out RaidDifficulty std) ? std : null;
        }

        public static float DifficultyScale(string id) => Difficulty(id)?.Scale ?? 1f;
        public static float DifficultyFrequency(string id) => Math.Max(0.05f, Difficulty(id)?.Frequency ?? 1f);
        public static float DifficultyWarning(string id) => Math.Max(0.05f, Difficulty(id)?.Warning ?? 1f);
        public static bool StoryOnly(string id) => (Difficulty(id)?.StoryOnly ?? 0) == 1;

        // ── 调参（fg.TbHomeTuning raid.*）──

        public static float MinIntervalDays => Math.Max(0f, T("raid.min_interval_days", 1f));
        public static float MinWarningHours => Math.Max(0f, T("raid.min_warning_hours", 1.5f));
        public static float IntelLeadDays => Math.Max(0f, T("raid.intel_lead_days", 1f));
        public static float PlanLeadDays => Math.Max(0f, T("raid.plan_lead_days", 0.5f));
        public static float StoryPlanLeadDays => Math.Max(0f, T("raid.story_plan_lead_days", 0.25f));
        public static float RouteLatencySeconds => Math.Max(0.05f, T("raid.route_latency_seconds", 5f));
        public static float FirstRaidMinDays => Math.Max(0f, T("raid.first_raid_min_days", 5f));
        public static float FirstRaidScale => Math.Max(0.01f, T("raid.first_raid_scale", 0.6f));
        public static float IntervalBonusPerDay => Math.Max(0f, T("raid.interval_bonus_per_day", 0.1f));
        public static float IntervalBonusMax => Math.Max(0f, T("raid.interval_bonus_max", 0.5f));
        public static float Level4PeriodDays => Math.Max(0.05f, T("raid.level4_period_days", 2f));
        public static float ThresholdRearm => Math.Max(0f, T("raid.threshold_rearm", 10f));
        public static float HarassMinIntervalDays => Math.Max(0.05f, T("raid.harass_min_interval_days", 3f));
        public static float HarassQuietDays => Math.Max(0f, T("raid.harass_quiet_days", 2f));
        public static int MaxUnits => Math.Max(1, (int)Math.Round(T("raid.max_units", 200f)));
        public static float PostgameBase => Math.Max(1f, T("raid.postgame_base", 1200f));
        public static float PostgameGrowth => Math.Max(1f, T("raid.postgame_growth", 1.15f));
        public static float MergeWindowSeconds => Math.Max(0f, T("raid.merge_window_seconds", 1200f));
        public static float DestroyedOutpostIntervalBonus => Math.Max(0f, T("raid.destroyed_outpost_interval_bonus", 0.5f));
        public static float OriginMaxDistance => Math.Max(1f, T("raid.origin_max_distance", 1600f));
        public static float TargetHomeWeight => Math.Max(0f, T("raid.target_home_weight", 4f));
        public static int TargetOutpostMinBuildings => Math.Max(1, (int)Math.Round(T("raid.target_outpost_min_buildings", 3f)));
        public static float TargetProximityCells => Math.Max(1f, T("raid.target_proximity_cells", 400f));
        public static float TimeLimitHours => Math.Max(0f, T("raid.time_limit_hours", 1f));
        public static int HistoryMax => Math.Max(4, (int)Math.Round(T("raid.history_max", 32f)));
        public static int WarningRowsMax => Math.Max(1, (int)Math.Round(T("raid.warning_rows_max", 6f)));

        /// <summary>幕系数（FG16 第 5 节）。</summary>
        public static float ActCoef(int act) => act >= 3 ? T("raid.act3_coef", 2.4f) : act == 2 ? T("raid.act2_coef", 1.6f) : T("raid.act1_coef", 1f);

        public static void Reload()
        {
            _loaded = false;
            Revision++;
            EnsureLoaded();
        }

        public static void ResetForTests() => Reload();

        private static float T(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[RaidCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_raid.py 后重新生成）。");
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
            TriggerList.Clear();
            TriggerById.Clear();
            LevelByLevel.Clear();
            DifficultyById.Clear();
            UnitList.Clear();
            UnitById.Clear();
            CounterList.Clear();
            StoryList.Clear();
            ProblemList.Clear();
            TbRaidTrigger triggers = null;
            TbRaidLevel levels = null;
            TbRaidDifficulty difficulties = null;
            TbRaidUnit units = null;
            TbRaidCounter counters = null;
            TbRaidStory stories = null;
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                triggers = t?.TbRaidTrigger;
                levels = t?.TbRaidLevel;
                difficulties = t?.TbRaidDifficulty;
                units = t?.TbRaidUnit;
                counters = t?.TbRaidCounter;
                stories = t?.TbRaidStory;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbRaid* 失败：" + e.Message);
            }
            if (triggers == null || levels == null || units == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbRaidTrigger / fg.TbRaidLevel / fg.TbRaidUnit 不存在（改 tools/cell_tables/fgdata_raid.py 后重新生成）");
                }
                Log.Error("[RaidCatalog] " + ProblemList[0]);
                return;
            }
            foreach (RaidTrigger row in triggers.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || TriggerById.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbRaidTrigger：空 ID 或重复 ID");
                    continue;
                }
                string rule = row.LevelRule ?? string.Empty;
                if (rule != "exposure" && rule != "request" && rule != "tier" && rule != "harass")
                {
                    ProblemList.Add($"fg.TbRaidTrigger {row.Id}：等级规则 {rule} 不认识");
                    continue;
                }
                var def = new RaidTriggerDef
                {
                    Id = row.Id, NameKey = row.NameKey, DescKey = row.DescKey, Exempt = row.Exempt == 1, LevelRule = rule,
                    OpensIn = string.IsNullOrEmpty(row.OpensIn) || row.OpensIn == "none" ? null : row.OpensIn, SortOrder = row.SortOrder,
                };
                TriggerById[def.Id] = def;
                TriggerList.Add(def);
            }
            TriggerList.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            foreach (RaidLevel row in levels.DataList)
            {
                if (row == null || row.Budget <= 0 || LevelByLevel.ContainsKey(row.Level))
                {
                    ProblemList.Add("fg.TbRaidLevel：预算 ≤ 0 或等级重复");
                    continue;
                }
                LevelByLevel[row.Level] = row;
            }
            if (difficulties != null)
            {
                foreach (RaidDifficulty row in difficulties.DataList)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id) || row.Scale <= 0f || row.Frequency <= 0f || row.Warning <= 0f)
                    {
                        ProblemList.Add("fg.TbRaidDifficulty：空 ID 或倍率 ≤ 0");
                        continue;
                    }
                    DifficultyById[row.Id] = row;
                }
            }
            foreach (RaidUnit row in units.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || UnitById.ContainsKey(row.Id) || row.Points <= 0 || row.ElitePoints <= row.Points || row.Weight <= 0f)
                {
                    ProblemList.Add("fg.TbRaidUnit：空 / 重复 ID、点数 ≤ 0、精英点数不大于普通、或权重 ≤ 0");
                    continue;
                }
                string role = row.Role ?? string.Empty;
                if (role != "assault" && role != "sabotage" && role != "siege")
                {
                    ProblemList.Add($"fg.TbRaidUnit {row.Id}：职能 {role} 不认识");
                    continue;
                }
                string tags = row.Tags ?? string.Empty;
                var def = new RaidUnitDef
                {
                    Id = row.Id, Faction = row.Faction, EnemyTypeId = row.EnemyTypeId, Role = role, Points = row.Points, MinLevel = row.MinLevel,
                    Weight = row.Weight, ElitePoints = row.ElitePoints,
                    Tags = tags == "none" || tags.Length == 0 ? Array.Empty<string>() : tags.Split(','),
                };
                UnitById[def.Id] = def;
                UnitList.Add(def);
            }
            if (counters != null)
            {
                foreach (RaidCounter row in counters.DataList)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id) || row.Boost <= 1f)
                    {
                        ProblemList.Add("fg.TbRaidCounter：空 ID 或倍率 ≤ 1");
                        continue;
                    }
                    CounterList.Add(row);
                }
            }
            if (stories != null)
            {
                foreach (RaidStory row in stories.DataList)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id) || row.Rule != StoryRuleFoundryCore || row.Level < 1)
                    {
                        ProblemList.Add($"fg.TbRaidStory {row?.Id}：规则不认识或等级 < 1");
                        continue;
                    }
                    StoryList.Add(row);
                }
            }
            foreach (string p in ProblemList)
            {
                Log.Error("[RaidCatalog] " + p);
            }
        }
    }
}
