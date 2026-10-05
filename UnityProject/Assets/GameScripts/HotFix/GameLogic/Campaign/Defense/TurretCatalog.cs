using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>FG6-DEF-01：一个作战组件装在炮塔上的参数（fg.TbTurretProfile 一行的运行时视图）。</summary>
    public sealed class TurretProfileDef
    {
        public string ComponentId;
        /// <summary>light = 轻型 2×2（turret_light）；heavy = 重型 3×3（turret_heavy）。</summary>
        public string Size;
        public float Range;
        public float TurnRate;
        public float ProjectileSpeed;
        public float ProjectileRadius;
        public string TypeId => Size == TurretCatalog.SizeHeavy ? TurretCatalog.HeavyTypeId : TurretCatalog.LightTypeId;
        public bool FiresProjectiles => ProjectileSpeed > 0f;
    }

    /// <summary>FG6-DEF-01：一种目标模式（fg.TbTurretTargetMode 一行）。<see cref="Code"/> = 内核 CombatTargetMode 的值。</summary>
    public sealed class TurretModeDef
    {
        public string Id;
        public int Code;
        public string NameKey;
        public string DescKey;
        public int SortOrder;
        public string Name => GameText.Get(NameKey);
        public string Description => GameText.Get(DescKey);
    }

    /// <summary>FG6-DEF-01：一种流体需求（每发消耗多少升）。</summary>
    public struct TurretFluidNeed
    {
        public int FluidId;
        public float LitersPerShot;
    }

    /// <summary>
    /// FG6-DEF-01（FG06 FGR-DEF-001～004）：炮塔的内容表——fg.TbTurretProfile（主作战组件 → 占地 / 射程 / 转速 / 弹速）、fg.TbTurretFluid（流体类固件每发消耗）、
    /// fg.TbTurretTargetMode（五种目标模式）与 turret.* 调参。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>，运行时不抛异常。数据源 tools/cell_tables/fgdata_defense.py。
    /// </summary>
    public static class TurretCatalog
    {
        public const string LightTypeId = "turret_light";
        public const string HeavyTypeId = "turret_heavy";
        public const string SizeLight = "light";
        public const string SizeHeavy = "heavy";

        private static readonly Dictionary<string, TurretProfileDef> Profiles = new Dictionary<string, TurretProfileDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<TurretFluidNeed>> FluidByFirmware = new Dictionary<string, List<TurretFluidNeed>>(StringComparer.Ordinal);
        private static readonly List<TurretModeDef> ModeList = new List<TurretModeDef>(5);
        private static readonly Dictionary<int, TurretModeDef> ModeByCode = new Dictionary<int, TurretModeDef>();
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

        public static bool IsTurretType(string typeId) => typeId == LightTypeId || typeId == HeavyTypeId;

        public static string SizeOfType(string typeId) => typeId == HeavyTypeId ? SizeHeavy : SizeLight;

        public static string SizeName(string size) => GameText.Get(size == SizeHeavy ? "turret.size.heavy" : "turret.size.light");

        public static bool TryGetProfile(string componentId, out TurretProfileDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(componentId) && Profiles.TryGetValue(componentId, out def);
        }

        /// <summary>按面板排序的五种目标模式。</summary>
        public static IReadOnlyList<TurretModeDef> Modes
        {
            get
            {
                EnsureLoaded();
                return ModeList;
            }
        }

        public static bool TryGetMode(int code, out TurretModeDef def)
        {
            EnsureLoaded();
            return ModeByCode.TryGetValue(code, out def);
        }

        public static string ModeName(int code) => TryGetMode(code, out TurretModeDef d) ? d.Name : code.ToString();

        /// <summary>
        /// 这套生效固件每发要消耗的流体（按流体合并：同一种流体的每发升数相加）。混合固件按两个来源固件各自的需求相加（<see cref="FirmwareKinds.ExpandMixed"/>），
        /// 不按固件 ID 写特例。没有流体类固件 = 空。只在装配结算时调用（不按帧）。
        /// </summary>
        public static void FluidNeeds(IEnumerable<string> effectiveFirmware, List<TurretFluidNeed> into)
        {
            EnsureLoaded();
            into.Clear();
            if (effectiveFirmware == null)
            {
                return;
            }
            foreach (string fw in FirmwareKinds.ExpandMixed(effectiveFirmware))
            {
                if (string.IsNullOrEmpty(fw) || !FluidByFirmware.TryGetValue(fw, out List<TurretFluidNeed> needs))
                {
                    continue;
                }
                foreach (TurretFluidNeed n in needs)
                {
                    int at = into.FindIndex(x => x.FluidId == n.FluidId);
                    if (at >= 0)
                    {
                        TurretFluidNeed m = into[at];
                        m.LitersPerShot += n.LitersPerShot;
                        into[at] = m;
                    }
                    else
                    {
                        into.Add(n);
                    }
                }
            }
            into.Sort((a, b) => a.FluidId.CompareTo(b.FluidId));
        }

        // ── 调参（turret.*）──

        public static float AimToleranceDeg => Math.Max(0.5f, Tuning("turret.aim_tolerance_deg", 6f));
        public static float SyncSeconds => Math.Max(0.05f, Tuning("turret.sync_seconds", 0.5f));
        public static int MagazineShots => Math.Max(1, (int)Math.Round(Tuning("turret.supply.magazine_shots", 10f)));
        public static int BufferShots => Math.Max(1, (int)Math.Round(Tuning("turret.supply.buffer_shots", 40f)));
        public static int SupplyLpm => Math.Max(1, (int)Math.Round(Tuning("turret.supply.lpm", 120f)));
        public static int PipePriority => Math.Max(1, Math.Min(4, (int)Math.Round(Tuning("turret.supply.pipe_priority", 3f))));
        public static int LowShots => Math.Max(0, (int)Math.Round(Tuning("turret.supply.low_shots", 5f)));
        public static float NotifyCooldownSeconds => Math.Max(1f, Tuning("turret.notify.cooldown_seconds", 30f));
        public static int RingSegments => Math.Max(12, Math.Min(256, (int)Math.Round(Tuning("turret.ring.segments", 64f))));

        /// <summary>炮塔座等级的全方位减伤（T1 = 0）。</summary>
        public static float TierArmor(int tier) => tier >= 3 ? Tuning("turret.tier.armor.t3", 0.35f) : tier == 2 ? Tuning("turret.tier.armor.t2", 0.2f) : 0f;

        public static void Reload()
        {
            _loaded = false;
            Revision++;
            EnsureLoaded();
        }

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[TurretCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_defense.py 后重新生成）。");
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
            Profiles.Clear();
            FluidByFirmware.Clear();
            ModeList.Clear();
            ModeByCode.Clear();
            ProblemList.Clear();
            GameConfig.Tables tables = null;
            try
            {
                tables = ConfigSystem.Instance.Tables;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取配置表失败：" + e.Message);
            }
            if (tables?.TbTurretProfile == null || tables.TbTurretFluid == null || tables.TbTurretTargetMode == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbTurretProfile / fg.TbTurretFluid / fg.TbTurretTargetMode 不存在（改 tools/cell_tables/fgdata_defense.py 后重新生成）");
                }
                Log.Error("[TurretCatalog] " + ProblemList[0]);
                return;
            }
            foreach (TurretProfile row in tables.TbTurretProfile.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.ComponentId) || Profiles.ContainsKey(row.ComponentId))
                {
                    ProblemList.Add("fg.TbTurretProfile：空 ID 或重复 ID");
                    continue;
                }
                if ((row.Size != SizeLight && row.Size != SizeHeavy) || !(row.Range > 0f) || row.TurnRate < 0f || row.ProjectileSpeed < 0f || row.ProjectileRadius < 0f)
                {
                    ProblemList.Add($"fg.TbTurretProfile {row.ComponentId}：占地 / 射程 / 转速 / 弹速不合法，跳过");
                    continue;
                }
                Profiles[row.ComponentId] = new TurretProfileDef
                {
                    ComponentId = row.ComponentId,
                    Size = row.Size,
                    Range = row.Range,
                    TurnRate = row.TurnRate,
                    ProjectileSpeed = row.ProjectileSpeed,
                    ProjectileRadius = row.ProjectileSpeed > 0f ? Math.Max(0.05f, row.ProjectileRadius) : 0f,
                };
            }
            foreach (TurretFluid row in tables.TbTurretFluid.DataList)
            {
                int fluid = row != null ? PipeNetworkService.FluidId(row.Fluid) : 0;
                if (row == null || string.IsNullOrEmpty(row.FirmwareId) || fluid <= 0 || !(row.LitersPerShot > 0f))
                {
                    ProblemList.Add($"fg.TbTurretFluid {row?.FirmwareId}：流体不认识或每发消耗不 > 0，跳过");
                    continue;
                }
                if (!FluidByFirmware.TryGetValue(row.FirmwareId, out List<TurretFluidNeed> l))
                {
                    FluidByFirmware[row.FirmwareId] = l = new List<TurretFluidNeed>(1);
                }
                l.Add(new TurretFluidNeed { FluidId = fluid, LitersPerShot = row.LitersPerShot });
            }
            foreach (TurretTargetMode row in tables.TbTurretTargetMode.DataList)
            {
                if (row == null || row.Code < 0 || row.Code > 4 || ModeByCode.ContainsKey(row.Code))
                {
                    ProblemList.Add($"fg.TbTurretTargetMode {row?.Id}：code 不在 0～4 或重复，跳过");
                    continue;
                }
                var d = new TurretModeDef { Id = row.Id, Code = row.Code, NameKey = row.NameKey, DescKey = row.DescKey, SortOrder = row.SortOrder };
                ModeList.Add(d);
                ModeByCode[d.Code] = d;
            }
            ModeList.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            if (ModeByCode.Count != 5)
            {
                ProblemList.Add($"fg.TbTurretTargetMode 只有 {ModeByCode.Count} 种目标模式（FGR-DEF-003 要五种）");
            }
            foreach (string p in ProblemList)
            {
                Log.Error("[TurretCatalog] " + p);
            }
        }
    }
}
