using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>FG6-DEF-02：防御建筑的种类（屏障三级算一种）。</summary>
    public enum DefenseKind : byte
    {
        None = 0,
        Barrier = 1,
        Gate = 2,
        Shield = 3,
        Trap = 4,
    }

    /// <summary>FG6-DEF-02：护盾状态机的一个状态（fg.TbShieldState 一行的运行时视图）。</summary>
    public sealed class ShieldStateDef
    {
        public string Id;
        public int Code;
        public string NameKey;
        public string DescKey;
        public bool Initial;
        public bool Absorbs;
        public float Seconds;
        public string Next;
        public string OnDepleted;
        public string OnPowerLost;
        public string OnPowerBack;
        public float RegenPerSec;
        public float PowerMul;
        public float EnterHp;
        /// <summary>复审修复：状态行文本键（{0} 护盾值 {1} 上限 {2} 剩余秒 {3} 耗电）与分类（working / idle / nopower）——代码不认状态 ID。</summary>
        public string StatusKey;
        public string StatusKind;
        public string Name => GameText.Get(NameKey);
        public string Description => GameText.Get(DescKey);
    }

    /// <summary>FG6-DEF-02：陷阱发射器装一枚固件铺什么（fg.TbTrapProfile 一行的运行时视图）。<see cref="FluidId"/> = 0：只用电。</summary>
    public sealed class TrapProfileDef
    {
        public string FirmwareId;
        public string Tag;
        public uint StatusBit;
        public int FluidId;
        public float LitersPerLay;
        public float Dps;
        public string NameKey;
        public int SortOrder;
        public string FieldName => GameText.Get(NameKey);
    }

    /// <summary>
    /// FG6-DEF-02（FG06 FGR-DEF-010～013）：屏障 / 闸门 / 护盾发生器 / 陷阱发射器的内容表——建筑类型、fg.TbShieldState（护盾状态机）、fg.TbTrapProfile（陷阱固件 → 场地）
    /// 与 defense.* / shield.* / trap.* 调参。只读；载入时逐行校验，表坏了记 <see cref="Problems"/>，运行时不抛异常。数据源 tools/cell_tables/fgdata_structures.py。
    /// </summary>
    public static class DefenseCatalog
    {
        public const string BarrierT1 = "barrier_t1";
        public const string BarrierT2 = "barrier_t2";
        public const string BarrierT3 = "barrier_t3";
        public const string GateTypeId = "gate";
        public const string ShieldTypeId = "shield_gen";
        public const string TrapTypeId = "trap_emitter";

        /// <summary>陷阱铺设方式（存档存整数，顺序不能改）。</summary>
        public const int PatternLine = 0;
        public const int PatternArea = 1;

        private static readonly Dictionary<string, ShieldStateDef> States = new Dictionary<string, ShieldStateDef>(StringComparer.Ordinal);
        private static readonly List<ShieldStateDef> StateList = new List<ShieldStateDef>(5);
        private static readonly Dictionary<string, TrapProfileDef> Traps = new Dictionary<string, TrapProfileDef>(StringComparer.Ordinal);
        private static readonly List<TrapProfileDef> TrapList = new List<TrapProfileDef>(8);
        private static readonly List<string> ProblemList = new List<string>();
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);
        private static ShieldStateDef _initial;
        private static bool _loaded;
        private static int _tagRevision = -1;

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        public static DefenseKind KindOf(string typeId)
        {
            switch (typeId)
            {
                case BarrierT1:
                case BarrierT2:
                case BarrierT3:
                    return DefenseKind.Barrier;
                case GateTypeId:
                    return DefenseKind.Gate;
                case ShieldTypeId:
                    return DefenseKind.Shield;
                case TrapTypeId:
                    return DefenseKind.Trap;
                default:
                    return DefenseKind.None;
            }
        }

        public static bool IsDefenseType(string typeId) => KindOf(typeId) != DefenseKind.None;

        /// <summary>按住拖拽铺设的建筑（1×1 的屏障与闸门）。</summary>
        public static bool IsDraggable(string typeId)
        {
            DefenseKind k = KindOf(typeId);
            return k == DefenseKind.Barrier || k == DefenseKind.Gate;
        }

        /// <summary>挡路的防御建筑（屏障挡全部移动类别，闸门只挡敌方）。</summary>
        public static bool BlocksMovement(string typeId)
        {
            DefenseKind k = KindOf(typeId);
            return k == DefenseKind.Barrier || k == DefenseKind.Gate;
        }

        // ── 护盾状态机 ──

        public static IReadOnlyList<ShieldStateDef> ShieldStates
        {
            get
            {
                EnsureLoaded();
                return StateList;
            }
        }

        public static ShieldStateDef InitialState
        {
            get
            {
                EnsureLoaded();
                return _initial;
            }
        }

        public static bool TryGetState(string id, out ShieldStateDef def)
        {
            EnsureLoaded();
            def = null;
            return !string.IsNullOrEmpty(id) && id != "none" && States.TryGetValue(id, out def);
        }

        // ── 陷阱 ──

        /// <summary>按面板排序的陷阱参数（能装进陷阱发射器的固件）。</summary>
        public static IReadOnlyList<TrapProfileDef> TrapProfiles
        {
            get
            {
                EnsureLoaded();
                RefreshTagBits();
                return TrapList;
            }
        }

        public static bool TryGetTrap(string firmwareId, out TrapProfileDef def)
        {
            EnsureLoaded();
            RefreshTagBits();
            def = null;
            return !string.IsNullOrEmpty(firmwareId) && Traps.TryGetValue(firmwareId, out def);
        }

        public static string PatternName(int pattern) =>
            pattern == PatternArea
                ? GameText.Format("trap.pattern.area", TrapAreaRadius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                : GameText.Format("trap.pattern.line", TrapLineLength.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));

        public static string PatternShortName(int pattern) => GameText.Get(pattern == PatternArea ? "trap.pattern.area_short" : "trap.pattern.line_short");

        // ── 调参 ──

        public static float SyncSeconds => Math.Max(0.05f, Tuning("defense.sync_seconds", 0.5f));
        public static int PreviewMarginCells => Math.Max(4, (int)Math.Round(Tuning("defense.preview.margin_cells", 24f)));
        public static int PreviewMaxRoutes => Math.Max(1, Math.Min(8, (int)Math.Round(Tuning("defense.preview.max_routes", 4f))));
        public static float ShieldRadius => Math.Max(1f, Tuning("shield.radius", 9f));
        public static float ShieldCapacity => Math.Max(1f, Tuning("shield.capacity", 400f));
        public static float ShieldPowerPerDps => Math.Max(0f, Tuning("shield.power_per_dps", 0.1f));
        public static float ShieldPowerBand => Math.Max(0.1f, Tuning("shield.power_band", 2f));
        public static float ShieldPowerExtraCap => Math.Max(0f, Tuning("shield.power_extra_cap", 30f));
        public static float ShieldLoadWindowSeconds => Math.Max(0.5f, Tuning("shield.load_window_seconds", 4f));
        public static float ShieldNotifyCooldownSeconds => Math.Max(1f, Tuning("shield.notify.cooldown_seconds", 20f));
        public static int ShieldRingSegments => Math.Max(12, Math.Min(256, (int)Math.Round(Tuning("shield.ring.segments", 72f))));
        public static float TrapLineLength => Math.Max(1f, Tuning("trap.line_length", 8f));
        public static float TrapAreaRadius => Math.Max(1f, Tuning("trap.area_radius", 4f));
        public static float TrapZoneRadius => Math.Max(0.25f, Tuning("trap.zone_radius", 1f));
        public static float TrapLaySeconds => Math.Max(0.1f, Tuning("trap.lay_seconds", 1f));
        public static float TrapZoneSeconds => Math.Max(TrapLaySeconds + 0.05f, Tuning("trap.zone_seconds", 1.6f));
        public static int TrapBufferLays => Math.Max(1, (int)Math.Round(Tuning("trap.buffer_lays", 30f)));
        public static int TrapSupplyLpm => Math.Max(1, (int)Math.Round(Tuning("trap.supply_lpm", 120f)));
        public static int TrapPipePriority => Math.Max(1, Math.Min(4, (int)Math.Round(Tuning("trap.pipe_priority", 3f))));
        public static float TrapNotifyCooldownSeconds => Math.Max(1f, Tuning("trap.notify.cooldown_seconds", 30f));
        /// <summary>复审修复：家园同时存在的陷阱场地上限（与读法区域分开计数，战斗内核 <c>CombatConfig.FieldZoneCapacity</c>）。</summary>
        public static int TrapFieldCapacity => Math.Max(16, (int)Math.Round(Tuning("trap.capacity.fields", 512f)));
        public static float TrapAreaSpacing => Math.Max(0.5f, Tuning("trap.area_spacing", 1.5f));
        public static float TrapLineSpacing => Math.Max(0.5f, Tuning("trap.line_spacing", 1.2f));
        public static float TrapLineOffset => Math.Max(0f, Tuning("trap.line_offset", 0.5f));

        /// <summary>
        /// 复审修复：护盾耗尽后关闭多久（面板规则说明用）= 第一个吸收状态的耗尽去向那个状态的时长；表里找不到时 0。不认状态 ID。
        /// </summary>
        public static float OverloadSeconds
        {
            get
            {
                EnsureLoaded();
                foreach (ShieldStateDef d in StateList)
                {
                    if (d.Absorbs && TryGetState(d.OnDepleted, out ShieldStateDef dep))
                    {
                        return dep.Seconds;
                    }
                }
                return 0f;
            }
        }

        public static void Reload()
        {
            _loaded = false;
            _tagRevision = -1;
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
                Log.Error($"[DefenseCatalog] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_structures.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>状态标签表可能比本目录晚载入 / 被重载：标签位按标签表的修订号重算（O(陷阱参数数)，只在修订号变化时）。</summary>
        private static void RefreshTagBits()
        {
            if (_tagRevision == StatusTagCatalog.Revision)
            {
                return;
            }
            _tagRevision = StatusTagCatalog.Revision;
            foreach (TrapProfileDef d in TrapList)
            {
                d.StatusBit = NamedReactionCatalog.BitOf(d.Tag);
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            States.Clear();
            StateList.Clear();
            Traps.Clear();
            TrapList.Clear();
            ProblemList.Clear();
            _initial = null;
            GameConfig.Tables tables = null;
            try
            {
                tables = ConfigSystem.Instance.Tables;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取配置表失败：" + e.Message);
            }
            if (tables?.TbShieldState == null || tables.TbTrapProfile == null)
            {
                if (ProblemList.Count == 0)
                {
                    ProblemList.Add("fg.TbShieldState / fg.TbTrapProfile 不存在（改 tools/cell_tables/fgdata_structures.py 后重新生成）");
                }
                Log.Error("[DefenseCatalog] " + ProblemList[0]);
                return;
            }
            foreach (ShieldState row in tables.TbShieldState.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.Id) || States.ContainsKey(row.Id))
                {
                    ProblemList.Add("fg.TbShieldState：空 ID 或重复 ID");
                    continue;
                }
                var d = new ShieldStateDef
                {
                    Id = row.Id,
                    Code = row.Code,
                    NameKey = row.NameKey,
                    DescKey = row.DescKey,
                    Initial = row.Initial == 1,
                    Absorbs = row.Absorbs == 1,
                    Seconds = Math.Max(0f, row.Seconds),
                    Next = row.Next ?? "none",
                    OnDepleted = row.OnDepleted ?? "none",
                    OnPowerLost = row.OnPowerLost ?? "none",
                    OnPowerBack = row.OnPowerBack ?? "none",
                    RegenPerSec = Math.Max(0f, row.RegenPerSec),
                    PowerMul = Math.Max(0f, row.PowerMul),
                    EnterHp = row.EnterHp,
                    StatusKey = string.IsNullOrEmpty(row.StatusKey) ? "bs.reason.shield_charging" : row.StatusKey,
                    StatusKind = string.IsNullOrEmpty(row.StatusKind) ? "idle" : row.StatusKind,
                };
                States[d.Id] = d;
                StateList.Add(d);
                if (d.Initial)
                {
                    if (_initial != null)
                    {
                        ProblemList.Add("fg.TbShieldState：不止一个初始状态，取第一个");
                    }
                    else
                    {
                        _initial = d;
                    }
                }
            }
            StateList.Sort((a, b) => a.Code.CompareTo(b.Code));
            foreach (ShieldStateDef d in StateList)
            {
                foreach (string t in new[] { d.Next, d.OnDepleted, d.OnPowerLost, d.OnPowerBack })
                {
                    if (t != "none" && !States.ContainsKey(t))
                    {
                        ProblemList.Add($"fg.TbShieldState {d.Id}：跳转目标 {t} 不存在");
                    }
                }
                if (d.Seconds > 0f && d.Next == "none")
                {
                    ProblemList.Add($"fg.TbShieldState {d.Id}：有持续时间却没有 next");
                }
                if (d.Absorbs && d.OnDepleted == "none")
                {
                    ProblemList.Add($"fg.TbShieldState {d.Id}：吸收状态没有耗尽去向");
                }
            }
            if (_initial == null && StateList.Count > 0)
            {
                ProblemList.Add("fg.TbShieldState：没有初始状态，取第一行");
                _initial = StateList[0];
            }
            foreach (TrapProfile row in tables.TbTrapProfile.DataList)
            {
                if (row == null || string.IsNullOrEmpty(row.FirmwareId) || Traps.ContainsKey(row.FirmwareId))
                {
                    ProblemList.Add("fg.TbTrapProfile：空 ID 或重复 ID");
                    continue;
                }
                int fluid = row.Fluid == "none" ? 0 : PipeNetworkService.FluidId(row.Fluid);
                if (row.Fluid != "none" && fluid <= 0)
                {
                    ProblemList.Add($"fg.TbTrapProfile {row.FirmwareId}：流体 {row.Fluid} 不认识，跳过");
                    continue;
                }
                var d = new TrapProfileDef
                {
                    FirmwareId = row.FirmwareId,
                    Tag = row.Tag,
                    FluidId = fluid,
                    LitersPerLay = fluid > 0 ? Math.Max(0.001f, row.LitersPerLay) : 0f,
                    Dps = Math.Max(0f, row.Dps),
                    NameKey = row.NameKey,
                    SortOrder = row.SortOrder,
                };
                Traps[d.FirmwareId] = d;
                TrapList.Add(d);
            }
            TrapList.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            foreach (string p in ProblemList)
            {
                Log.Error("[DefenseCatalog] " + p);
            }
        }
    }
}
