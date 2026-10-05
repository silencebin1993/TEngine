using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;
using Unity.Mathematics;

namespace GameLogic.Campaign.Defense
{
    /// <summary>fg.TbSiegeUnit 的一行加上 fg.TbMechEnemy 的耐久 / 正面减伤（攻城单位的战斗参数）。</summary>
    public sealed class SiegeUnitDef
    {
        public string EnemyTypeId;
        public float Hp;
        public float FrontalReduction;
        public float Speed;
        public float Radius;
        public float Range;
        public float Damage;
        public float Cooldown;
        public float ProjectileSpeed;
        public float ProjectileRadius;
        public float StructureMult;
        public float HealAmount;
        public float HealRange;
        public float HealCooldown;
    }

    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032）：攻城的内容表——fg.TbSiegeRole（职能与头顶图标）、fg.TbSiegeTarget（职能 × 目标类别的偏好）、
    /// fg.TbSiegeCategory（建筑类型 → 目标类别）、fg.TbSiegeUnit（攻城单位的战斗参数）与 siege.* 调参。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>（攻城按“不展开”降级并写原因）。
    /// 数据源 tools/cell_tables/fgdata_siege.py。
    /// </summary>
    public static class SiegeCatalog
    {
        public const string RoleAssault = "assault";
        public const string RoleSabotage = "sabotage";
        public const string RoleSiege = "siege";
        public const string RoleRetreat = "retreat";

        private static readonly string[] CategoryNames = { "core", "power", "signal", "defense", "listening", "other" };
        private static readonly string[] RoleNames = { RoleAssault, RoleSabotage, RoleSiege, RoleRetreat };

        private static bool _loaded;
        private static readonly List<SiegeRole> RoleRows = new List<SiegeRole>(4);
        private static readonly Dictionary<string, byte> CategoryByType = new Dictionary<string, byte>(StringComparer.Ordinal);
        private static readonly Dictionary<string, SiegeUnitDef> UnitByEnemy = new Dictionary<string, SiegeUnitDef>(StringComparer.Ordinal);
        private static readonly int[] Masks = new int[CombatSiegeConst.RoleCount];
        private static readonly int[] Bias = new int[CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount];
        private static readonly List<string> ProblemList = new List<string>();
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        public static int Revision { get; private set; } = 1;

        public static void Reload()
        {
            _loaded = false;
            Revision++;
            EnsureLoaded();
        }

        // ── 调参 ──

        public static float TheaterRadius => Math.Max(16f, T("siege.theater_radius_cells", 140f));
        public static int TheaterMargin => Math.Max(4, (int)Math.Round(T("siege.theater_margin_cells", 24f)));
        public static int TheaterMaxCells => Math.Max(64, Math.Min(512, (int)Math.Round(T("siege.theater_max_cells", 288f))));
        public static float RetargetSeconds => Math.Max(0.05f, T("siege.retarget_seconds", 0.5f));
        public static int BreachBaseCost => Math.Max(0, (int)Math.Round(T("siege.breach_base_cost", 60f)));
        public static float BreachHpBand => Math.Max(1f, T("siege.breach_hp_band", 40f));
        public static int BreachCostPerBand => Math.Max(0, (int)Math.Round(T("siege.breach_cost_per_band", 30f)));

        /// <summary>破墙代价（与内核 CombatSiegeLogic 结构缓存同一公式）：siege.breach_base_cost + ceil(耐久 / siege.breach_hp_band) × siege.breach_cost_per_band；10 = 1 米。放置预览用。</summary>
        public static int BreachPenalty(float hp) => BreachBaseCost + (int)Math.Ceiling(Math.Max(0f, hp) / BreachHpBand) * BreachCostPerBand;
        public static float LossRetreatRatio => Math.Max(0.01f, Math.Min(1f, T("siege.loss_retreat_ratio", 0.7f)));
        public static float ExitRadius => Math.Max(0.5f, T("siege.exit_radius_cells", 2.5f));
        public static float RetreatMaxSeconds => Math.Max(10f, T("siege.retreat_max_seconds", 180f));
        public static float HpScale => Math.Max(0.01f, T("siege.hp_scale", 1f));
        public static float EliteHpScale => Math.Max(1f, T("siege.elite_hp_scale", 2f));
        public static float EliteDamageScale => Math.Max(1f, T("siege.elite_damage_scale", 1.5f));
        public static float SpawnSpacing => Math.Max(0.5f, T("siege.spawn_spacing", 1.6f));
        public static float CollateralRatio => Math.Max(0f, T("siege.collateral_ratio", 0.5f));
        public static float CollateralRadius => Math.Max(0f, T("siege.collateral_radius_cells", 1.5f));
        public static int CollateralDrainPerStep => Math.Max(1, (int)Math.Round(T("siege.collateral_drain_per_step", 32f)));
        public static float SiteHpFraction => Math.Max(0.01f, T("siege.site_hp_fraction", 0.35f));
        public static float SyncSeconds => Math.Max(0.05f, T("siege.sync_seconds", 0.5f));
        public static float BreachNotifySeconds => Math.Max(1f, T("siege.breach_notify_seconds", 20f));
        public static float GuardRadius => Math.Max(2f, T("siege.guard_radius_cells", 18f));
        public static float InterceptRadius => Math.Max(2f, T("siege.intercept_radius_cells", 16f));
        public static float RegroupSeconds => Math.Max(1f, T("siege.regroup_seconds", 8f));
        public static float InterceptMinTargetCells => Math.Max(0f, T("siege.intercept_min_target_cells", 60f));
        public static float InterceptMaxSeconds => Math.Max(5f, T("siege.intercept_max_seconds", 90f));
        public static float OverlayRefreshSeconds => Math.Max(0.05f, T("siege.overlay_refresh_seconds", 0.5f));
        public static int PerfUnits => Math.Max(1, (int)Math.Round(T("siege.perf.units", 200f)));
        public static float PerfFieldUpdateMs => Math.Max(0.1f, T("siege.perf.field_update_ms", 4f));
        public static float PerfStepMs => Math.Max(0.1f, T("siege.perf.step_ms", 2f));

        private static float T(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[SiegeCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_siege.py 后重新生成）。");
            }
            return fallback;
        }

        // ── 表 ──

        /// <summary>建筑类型的攻城目标类别位（没有登记 = 其它建筑）。</summary>
        public static byte CategoryOf(string typeId)
        {
            EnsureLoaded();
            return typeId != null && CategoryByType.TryGetValue(typeId, out byte c) ? c : CombatSiegeConst.CatOther;
        }

        public static string CategoryName(byte bit)
        {
            int i = bit == 0 ? -1 : math.tzcnt((int)bit);
            return i >= 0 && i < CategoryNames.Length ? CategoryNames[i] : "other";
        }

        public static bool TryGetUnit(string enemyTypeId, out SiegeUnitDef def)
        {
            EnsureLoaded();
            def = null;
            return enemyTypeId != null && UnitByEnemy.TryGetValue(enemyTypeId, out def);
        }

        public static CombatSiegeRole RoleOf(string role) =>
            role == RoleAssault ? CombatSiegeRole.Assault : role == RoleSabotage ? CombatSiegeRole.Sabotage : role == RoleSiege ? CombatSiegeRole.Siege : CombatSiegeRole.None;

        /// <summary>职能的显示名（文本键）。</summary>
        public static string RoleName(CombatSiegeRole role)
        {
            EnsureLoaded();
            string key = role == CombatSiegeRole.Assault ? RoleAssault : role == CombatSiegeRole.Sabotage ? RoleSabotage : role == CombatSiegeRole.Siege ? RoleSiege : RoleRetreat;
            foreach (SiegeRole r in RoleRows)
            {
                if (r.Role == key)
                {
                    return GameText.Get(r.NameKey);
                }
            }
            return key;
        }

        public static string RoleDesc(int roleIndex)
        {
            EnsureLoaded();
            string key = roleIndex >= 0 && roleIndex < RoleNames.Length ? RoleNames[roleIndex] : RoleRetreat;
            foreach (SiegeRole r in RoleRows)
            {
                if (r.Role == key)
                {
                    return GameText.Get(r.DescKey);
                }
            }
            return key;
        }

        /// <summary>内核职能表：目标类别位（4 项）。</summary>
        public static IReadOnlyList<int> RoleMasks
        {
            get
            {
                EnsureLoaded();
                return Masks;
            }
        }

        /// <summary>内核职能表：偏好代价（4 × 6；10 = 1 米）。</summary>
        public static IReadOnlyList<int> RoleBias
        {
            get
            {
                EnsureLoaded();
                return Bias;
            }
        }

        /// <summary>职能色（突击 / 破坏 / 攻城 / 撤退中；表里的图标颜色），突袭路径叠加层画路线用。</summary>
        public static UnityEngine.Color RoleColor(int roleIndex)
        {
            EnsureLoaded();
            string key = roleIndex >= 0 && roleIndex < RoleNames.Length ? RoleNames[roleIndex] : RoleRetreat;
            foreach (SiegeRole r in RoleRows)
            {
                if (r.Role == key)
                {
                    int c = r.IconColor;
                    return new UnityEngine.Color(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 0.95f);
                }
            }
            return new UnityEngine.Color(0.95f, 0.2f, 0.15f, 0.95f);
        }
        /// <summary>头顶职能图标（突击 / 破坏 / 攻城 / 撤退中）：x = 形状序号，y = 打包颜色。</summary>
        public static float2[] BuildRoleVisuals()
        {
            EnsureLoaded();
            var v = new float2[4];
            for (int r = 0; r < 4; r++)
            {
                v[r] = new float2(-1f, 0f);
                foreach (SiegeRole row in RoleRows)
                {
                    if (row.Role == RoleNames[r])
                    {
                        v[r] = new float2(row.IconShape, row.IconColor);
                        break;
                    }
                }
            }
            return v;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            RoleRows.Clear();
            CategoryByType.Clear();
            UnitByEnemy.Clear();
            ProblemList.Clear();
            Array.Clear(Masks, 0, Masks.Length);
            for (int i = 0; i < Bias.Length; i++)
            {
                Bias[i] = CombatSiegeConst.Inf;
            }
            TbSiegeRole roles = null;
            TbSiegeTarget targets = null;
            TbSiegeCategory cats = null;
            TbSiegeUnit units = null;
            TbMechEnemy enemies = null;
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                roles = t?.TbSiegeRole;
                targets = t?.TbSiegeTarget;
                cats = t?.TbSiegeCategory;
                units = t?.TbSiegeUnit;
                enemies = t?.TbMechEnemy;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbSiege* 失败：" + e.Message);
            }
            if (roles == null || targets == null || cats == null || units == null || enemies == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbSiegeRole / TbSiegeTarget / TbSiegeCategory / TbSiegeUnit 不存在（改 tools/cell_tables/fgdata_siege.py 后重新生成）");
                }
                Log.Error("[SiegeCatalog] " + ProblemList[0]);
                return;
            }
            foreach (SiegeRole row in roles.DataList)
            {
                if (row == null || Array.IndexOf(RoleNames, row.Role) < 0)
                {
                    ProblemList.Add($"fg.TbSiegeRole {row?.Role}：职能不认识");
                    continue;
                }
                RoleRows.Add(row);
            }
            foreach (SiegeTarget row in targets.DataList)
            {
                int r = row == null ? -1 : Array.IndexOf(RoleNames, row.Role);
                int c = row == null ? -1 : Array.IndexOf(CategoryNames, row.Category);
                if (r < 0 || r >= CombatSiegeConst.RoleRetreat || c < 0 || !(row.BiasMeters >= 0f))
                {
                    ProblemList.Add($"fg.TbSiegeTarget {row?.Id}：职能 / 类别不认识或偏好 < 0");
                    continue;
                }
                Masks[r] |= 1 << c;
                Bias[r * CombatSiegeConst.CategoryCount + c] = (int)Math.Round(row.BiasMeters * 10f);
            }
            foreach (SiegeCategory row in cats.DataList)
            {
                int c = row == null ? -1 : Array.IndexOf(CategoryNames, row.Category);
                if (c < 0 || string.IsNullOrEmpty(row.TypeId))
                {
                    ProblemList.Add($"fg.TbSiegeCategory {row?.TypeId}：类别不认识");
                    continue;
                }
                CategoryByType[row.TypeId] = (byte)(1 << c);
            }
            foreach (SiegeUnit row in units.DataList)
            {
                MechEnemy e = row != null ? enemies.GetOrDefault(row.EnemyTypeId) : null;
                if (row == null || e == null || row.Speed <= 0f || row.Range <= 0f || row.Damage <= 0f || row.Cooldown <= 0f || row.ProjectileSpeed <= 0f)
                {
                    ProblemList.Add($"fg.TbSiegeUnit {row?.EnemyTypeId}：敌人不在 fg.TbMechEnemy 或参数 ≤ 0");
                    continue;
                }
                UnitByEnemy[row.EnemyTypeId] = new SiegeUnitDef
                {
                    EnemyTypeId = row.EnemyTypeId,
                    Hp = Math.Max(1f, e.MaxHp),
                    FrontalReduction = Math.Max(0f, Math.Min(0.95f, e.FrontalDamageReduction)),
                    Speed = row.Speed,
                    Radius = Math.Max(0.2f, row.Radius),
                    Range = row.Range,
                    Damage = row.Damage,
                    Cooldown = row.Cooldown,
                    ProjectileSpeed = row.ProjectileSpeed,
                    ProjectileRadius = Math.Max(0.05f, row.ProjectileRadius),
                    StructureMult = Math.Max(0.01f, row.StructureMult),
                    HealAmount = Math.Max(0f, row.HealAmount),
                    HealRange = Math.Max(0f, row.HealRange),
                    HealCooldown = Math.Max(0f, row.HealCooldown),
                };
            }
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (!UnitByEnemy.ContainsKey(u.EnemyTypeId))
                {
                    ProblemList.Add($"fg.TbRaidUnit {u.Id}：敌人 {u.EnemyTypeId} 在 fg.TbSiegeUnit 里没有攻城参数（展开时按原型参数）");
                }
            }
            if (ProblemList.Count > 0)
            {
                Log.Error("[SiegeCatalog] " + string.Join("；", ProblemList));
            }
        }
    }
}
