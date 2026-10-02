using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>靶子原型（fg.TbRangeTarget.archetype）。</summary>
    public enum RangeTargetArchetype : byte
    {
        Basic = 0,
        Heavy = 1,
        Fast = 2,
        Swarm = 3,
        Shield = 4,
    }

    /// <summary>一种靶子（fg.TbRangeTarget 一行的运行时视图）。</summary>
    public sealed class RangeTargetDef
    {
        public string Id;
        public string NameKey;
        public string DescKey;
        public RangeTargetArchetype Archetype;
        /// <summary>空 = 一开始就有；否则击败过其中任意一种敌人（fg.TbMechEnemy.id）即解锁。</summary>
        public string[] UnlockEnemies = Array.Empty<string>();
        public float Hp;
        public float Radius;
        public int Count;
        public float Armor;
        public float ArmorHalfAngle;
        public float Speed;
        public float PatrolRadius;
        public int SortOrder;

        public bool AlwaysUnlocked => UnlockEnemies.Length == 0;
        public string Name => GameText.Get(NameKey);
        public string Description => GameText.Get(DescKey);
    }

    /// <summary>
    /// FG5-RND-03（FG05 FGR-RND-030～033）：靶场的内容表——fg.TbRangeTarget（靶子类型）与 range.* 调参。只读；载入时逐行校验，
    /// 表坏了记 <see cref="Problems"/>，运行时不抛异常。数据源 tools/cell_tables/fgdata_range.py。
    /// </summary>
    public static class TestRangeCatalog
    {
        public const string TypeId = "test_range";

        private static readonly Dictionary<string, RangeTargetDef> ById = new Dictionary<string, RangeTargetDef>(StringComparer.Ordinal);
        private static readonly List<RangeTargetDef> List = new List<RangeTargetDef>(8);
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

        /// <summary>全部靶子类型（按面板排序）。</summary>
        public static IReadOnlyList<RangeTargetDef> Targets
        {
            get
            {
                EnsureLoaded();
                return List;
            }
        }

        public static bool TryGet(string id, out RangeTargetDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out def);
        }

        /// <summary>FG05 第 7 节：同时存在的仿真投影上限（全部靶场合计，初值 6）。</summary>
        public static int ProjectionCap => Math.Max(1, (int)Math.Round(Tuning("range.projection.max", 6f)));

        /// <summary>每座靶场的靶位数（远端 2 排 × 4 列）。</summary>
        public static int SlotCount => Math.Max(1, Math.Min(16, (int)Math.Round(Tuning("range.target.slots", 8f))));

        public static float MaxTestSeconds => Math.Max(5f, Tuning("range.test.max_seconds", 180f));
        public static float SampleSeconds => Math.Max(0.05f, Tuning("range.sample_seconds", 0.5f));
        public static int CurvePoints => Math.Max(2, (int)Math.Round(Tuning("range.curve.points", 60f)));
        public static int HistoryKeep => Math.Max(2, (int)Math.Round(Tuning("range.history.keep", 8f)));
        public static int PresetCap => Math.Max(1, (int)Math.Round(Tuning("range.presets.max", 8f)));
        public static float RespawnSeconds => Math.Max(0.1f, Tuning("range.target.respawn_seconds", 2f));
        public static float AttackRange => Math.Max(1f, Tuning("range.attack_range", 7f));
        public static float RetargetSeconds => Math.Max(0.05f, Tuning("range.retarget_seconds", 0.5f));
        public static float SwarmSpread => Math.Max(0.1f, Tuning("range.swarm.spread", 0.9f));
        public static float EdgeMargin => Math.Max(0.1f, Tuning("range.edge_margin", 0.6f));

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
                Log.Error($"[TestRangeCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_range.py 后重新生成）。");
            }
            return fallback;
        }

        private static RangeTargetArchetype? ParseArchetype(string s) => s switch
        {
            "basic" => RangeTargetArchetype.Basic,
            "heavy" => RangeTargetArchetype.Heavy,
            "fast" => RangeTargetArchetype.Fast,
            "swarm" => RangeTargetArchetype.Swarm,
            "shield" => RangeTargetArchetype.Shield,
            _ => null,
        };

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            ById.Clear();
            List.Clear();
            ProblemList.Clear();
            TbRangeTarget table = null;
            try
            {
                table = ConfigSystem.Instance.Tables?.TbRangeTarget;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbRangeTarget 失败：" + e.Message);
            }
            if (table == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbRangeTarget 不存在（改 tools/cell_tables/fgdata_range.py 后重新生成）");
                }
                Log.Error("[TestRangeCatalog] " + ProblemList[0]);
                return;
            }
            foreach (RangeTarget row in table.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || ById.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbRangeTarget：空 ID 或重复 ID");
                    continue;
                }
                RangeTargetArchetype? arch = ParseArchetype(row.Archetype);
                if (arch == null)
                {
                    ProblemList.Add($"fg.TbRangeTarget {row.Id}：原型 {row.Archetype} 不认识");
                    continue;
                }
                if (row.Hp <= 0f || row.Radius <= 0f || row.Count < 1 || row.Armor < 0f || row.Armor > 0.9f)
                {
                    ProblemList.Add($"fg.TbRangeTarget {row.Id}：生命 / 半径 > 0、数量 >= 1、减伤 0～0.9");
                    continue;
                }
                var unlock = new List<string>();
                if (row.UnlockEnemies != "always")
                {
                    foreach (string part in (row.UnlockEnemies ?? string.Empty).Split(','))
                    {
                        string e = part.Trim();
                        if (e.Length > 0)
                        {
                            unlock.Add(e);
                        }
                    }
                    if (unlock.Count == 0)
                    {
                        ProblemList.Add($"fg.TbRangeTarget {row.Id}：解锁条件为空（一开始就有的写 always）");
                        continue;
                    }
                }
                var def = new RangeTargetDef
                {
                    Id = row.Id, NameKey = row.NameKey, DescKey = row.DescKey, Archetype = arch.Value, UnlockEnemies = unlock.ToArray(),
                    Hp = row.Hp, Radius = row.Radius, Count = row.Count, Armor = row.Armor, ArmorHalfAngle = row.ArmorHalfAngle,
                    Speed = row.Speed, PatrolRadius = row.PatrolRadius, SortOrder = row.SortOrder,
                };
                ById[def.Id] = def;
                List.Add(def);
            }
            List.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            if (!List.Exists(t => t.AlwaysUnlocked))
            {
                ProblemList.Add("fg.TbRangeTarget：至少一种靶子要一开始就有（B11）");
            }
            if (ProblemList.Count > 0)
            {
                Log.Error("[TestRangeCatalog] " + string.Join("；", ProblemList));
            }
        }
    }
}
