using System.Collections.Generic;
using System.Text;
using GameLogic.Localization;

namespace GameLogic.Campaign.Grid
{
    /// <summary>放置 / 旋转 / 拆除被拒的原因码（FGR-LOG-003、FG00 B06）。每个原因对应一个稳定的文本键。
    /// 顺序即显示优先级（先说“根本放不了”的，再说地形与占用，最后说缺料）。</summary>
    public enum GridBlockReason : byte
    {
        None = 0,
        NoRegion,
        UnknownType,
        NotPlaceable,
        Locked,
        MaxCount,
        Fog,
        CoreReserve,
        Terrain,
        NeedsTerrain,
        Pollution,
        Occupied,
        OccupiedBelt,
        OccupiedPipe,
        Obstacle,
        InsufficientScrap,
        NoBuilding,
        CannotDemolishCore,
        DemolishDamaged,
        Busy,
        CannotRotateCore,
        /// <summary>只能开局预置（placeable=0）的关键建筑：拆掉就再也造不回来，会软锁战役（FG00 B11），不允许拆除。</summary>
        NotRebuildable,
        /// <summary>FG0-ARCH-05（FGR-GEN-051）：超出世界坐标上限（world.coord_limit）。</summary>
        WorldLimit,
        // ── FG3-LOG-01（只追加：数值不进存档，但排在后面 = 显示优先级靠后）──
        /// <summary>装配站 / 仓库的出口通道（机器出厂、卸货的地方）会被别的建筑挡住。</summary>
        ExitBlocked,
        /// <summary>出口通道落在机器走不了的地形上（悬崖、水）。</summary>
        ExitTerrain,
        CannotRelocateCore,
        RelocateDamaged,
        RelocateSame,
        /// <summary>这座建筑已经在搬迁中（先取消新位置的虚影）。</summary>
        Relocating,
        /// <summary>正在施工的规划不能搬迁（取消后重新放置）。</summary>
        RelocateUnderConstruction,
        RelocateSourceGone,
        DragTooLong,
        BeltHasItems,
        ToolLocked,
        // ── FG3-LOG-04（只追加）──
        /// <summary>地下传送带没拖（入口 = 出口）或不是直线。</summary>
        UndergroundShape,
        /// <summary>地下传送带跨度超过这一等级的上限（FGR-LOG-023）。</summary>
        UndergroundSpan,
        /// <summary>地下已经有同方向的地下段经过。</summary>
        UndergroundOccupied,
        // ── FG3-LOG-05（只追加）──
        /// <summary>泵不在水源 / 油井上（FGR-LOG-041）。</summary>
        PumpNeedsSource,
        /// <summary>这段管线会把两种不同流体的网络接在一起（FGR-LOG-040）。</summary>
        PipeFluidConflict,
        /// <summary>阀门前后直接接着另一个阀门（FGR-LOG-043：中间至少隔一格管线）。</summary>
        PipeValveChained,
        // ── FG3-LOG-07（只追加）──
        /// <summary>这件东西没有可以复制的设置（例如传送带、不用电的建筑）。</summary>
        NoSettings,
        /// <summary>复制设置：两件不是同类或兼容类型（分流器的设置不能用在储罐上）。</summary>
        SettingsIncompatible,
        /// <summary>升级规划：这一件已经是最高等级 / 没有升级路线。</summary>
        NoUpgrade,
        /// <summary>升级规划：高一级还没解锁。</summary>
        UpgradeLocked,
        /// <summary>升级规划：这一件已经在升级中。</summary>
        Upgrading,
        /// <summary>复制 / 布局：件数超过上限（plan.layout_max_entries）。</summary>
        TooManyEntries,
        // ── FG6-DEF-01（只追加）──
        /// <summary>这种炮塔座没有能装的炮塔蓝图（先在蓝图编辑器里用固定底盘配一张，主组件决定轻型 / 重型）。</summary>
        TurretNoBlueprint,
    }

    /// <summary>一条原因：原因码 + 文本键 + 参数（参数本身若是文本键，显示时按当前语言解析）。</summary>
    public readonly struct GridReason
    {
        public readonly GridBlockReason Code;
        public readonly string TextKey;
        public readonly string[] Args;

        public GridReason(GridBlockReason code, string textKey, params string[] args)
        {
            Code = code;
            TextKey = textKey;
            Args = args ?? System.Array.Empty<string>();
        }

        /// <summary>当前语言的原因文本。参数若是文本键（含点且能查到）先翻译。</summary>
        public string Describe()
        {
            if (Args.Length == 0)
            {
                return GameText.Get(TextKey);
            }
            var args = new object[Args.Length];
            for (int i = 0; i < Args.Length; i++)
            {
                string a = Args[i] ?? string.Empty;
                args[i] = a.IndexOf('.') > 0 && GameText.Has(a) ? GameText.Get(a) : a;
            }
            return GameText.Format(TextKey, args);
        }

        public static GridReason Of(GridBlockReason code)
        {
            switch (code)
            {
                case GridBlockReason.NoRegion: return new GridReason(code, "grid.reason.no_region");
                case GridBlockReason.UnknownType: return new GridReason(code, "grid.reason.unknown_type");
                case GridBlockReason.NotPlaceable: return new GridReason(code, "grid.reason.not_placeable");
                case GridBlockReason.Fog: return new GridReason(code, "grid.reason.fog");
                case GridBlockReason.OccupiedBelt: return new GridReason(code, "grid.reason.occupied_belt");
                case GridBlockReason.OccupiedPipe: return new GridReason(code, "grid.reason.occupied_pipe");
                // FG3-LOG-05：带参数的原因由 ValidatePipeCell / PlanPipePath 构造，这里给无参数时的稳定文本。
                case GridBlockReason.PumpNeedsSource: return new GridReason(code, "grid.reason.pump_needs_source_plain");
                case GridBlockReason.PipeFluidConflict: return new GridReason(code, "grid.reason.pipe_fluid_conflict_plain");
                case GridBlockReason.PipeValveChained: return new GridReason(code, "logistics.pipe.reason.valve_chained");
                case GridBlockReason.NoBuilding: return new GridReason(code, "grid.reason.no_building");
                case GridBlockReason.CannotDemolishCore: return new GridReason(code, "grid.reason.cannot_demolish_core");
                case GridBlockReason.DemolishDamaged: return new GridReason(code, "grid.reason.demolish_damaged");
                case GridBlockReason.Busy: return new GridReason(code, "grid.reason.busy");
                case GridBlockReason.CannotRotateCore: return new GridReason(code, "grid.reason.cannot_rotate_core");
                case GridBlockReason.NotRebuildable: return new GridReason(code, "grid.reason.not_rebuildable");
                case GridBlockReason.CannotRelocateCore: return new GridReason(code, "grid.reason.cannot_relocate_core");
                case GridBlockReason.RelocateDamaged: return new GridReason(code, "grid.reason.relocate_damaged");
                case GridBlockReason.RelocateSame: return new GridReason(code, "grid.reason.relocate_same");
                case GridBlockReason.Relocating: return new GridReason(code, "grid.reason.relocating");
                case GridBlockReason.RelocateUnderConstruction: return new GridReason(code, "grid.reason.relocate_building");
                case GridBlockReason.RelocateSourceGone: return new GridReason(code, "grid.reason.relocate_source_gone");
                // FG3-LOG-04：跨度超限与地下重叠的正式原因带参数（等级名、上限、交叉格），由 HomeGridService 构造；这里只给无参兜底。
                case GridBlockReason.UndergroundShape: return new GridReason(code, "grid.reason.under_not_straight");
                // FG3-LOG-07：规划工具的原因（带参数的由 PlanningService / PlanSettings 构造，这里给无参数时的稳定文本）。
                case GridBlockReason.NoSettings: return new GridReason(code, "plan.reason.no_settings_plain");
                case GridBlockReason.SettingsIncompatible: return new GridReason(code, "plan.reason.incompatible_plain");
                case GridBlockReason.NoUpgrade: return new GridReason(code, "plan.reason.no_upgrade");
                case GridBlockReason.UpgradeLocked: return new GridReason(code, "plan.reason.upgrade_locked_plain");
                case GridBlockReason.Upgrading: return new GridReason(code, "plan.reason.upgrading");
                case GridBlockReason.TooManyEntries: return new GridReason(code, "plan.reason.too_many_plain");
                // FG6-DEF-01：带参数（轻型 / 重型）的由 TurretService.HasPlaceableBlueprint 构造，这里给无参数时的稳定文本。
                case GridBlockReason.TurretNoBlueprint: return new GridReason(code, "turret.reason.no_blueprint_plain");
                default: return new GridReason(code, "grid.reason.unknown_type");
            }
        }
    }

    /// <summary>一次放置校验的结果。<see cref="Ok"/> = 没有任何阻止原因。</summary>
    public sealed class GridPlacementResult
    {
        public string TypeId;
        public GridCell Pivot;
        public int Rotation;
        public readonly List<GridCell> Cells = new List<GridCell>(25);
        public readonly List<GridReason> Reasons = new List<GridReason>(2);
        /// <summary>每个占地格是否合法（可视化逐格着色；与 <see cref="Cells"/> 一一对应）。</summary>
        public readonly List<bool> CellOk = new List<bool>(25);
        public int ScrapCost;
        public float BuildSeconds;
        /// <summary>FG0-ARCH-06：只警告、不阻止的提示（当前语言，已拼好）——例如“放下后机器将无法到达某建筑”（FGR-LOG-012）。</summary>
        public readonly List<string> Warnings = new List<string>(1);
        /// <summary>FG3-LOG-06：放置预览的说明行（不是警告；当前语言，已拼好）——例如“接入电网 2”“覆盖 5 座建筑”。</summary>
        public readonly List<string> Notes = new List<string>(2);

        public bool Ok => Reasons.Count == 0;

        public bool Has(GridBlockReason code)
        {
            for (int i = 0; i < Reasons.Count; i++)
            {
                if (Reasons[i].Code == code)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>同一原因码只记一次（逐格校验时不会刷出 25 条“已被占用”）。</summary>
        public void Add(GridReason reason)
        {
            if (Has(reason.Code))
            {
                return;
            }
            int at = Reasons.Count;
            while (at > 0 && Reasons[at - 1].Code > reason.Code)
            {
                at--;
            }
            Reasons.Insert(at, reason);
        }

        /// <summary>全部原因（当前语言），用“；”连接。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Reasons.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(GameText.Language == GameLanguage.En ? "; " : "；");
                }
                sb.Append(Reasons[i].Describe());
            }
            return sb.ToString();
        }
    }

    /// <summary>规划操作（放置 / 旋转 / 拆除标记）的结果。</summary>
    public readonly struct GridOpResult
    {
        public enum Kind : byte
        {
            Failed,
            Placed,
            Rotated,
            DemolishMarked,
            DemolishUnmarked,
            PlanCancelled,
            // ── FG3-LOG-01 ──
            /// <summary>已建成的建筑：生成了“搬迁目标”虚影与施工工作单（完工后原建筑拆走）。</summary>
            RelocationPlanned,
            /// <summary>还没开工的规划（含搬迁目标虚影）：直接挪到新位置。</summary>
            PlanMoved,
            BeltsPlaced,
            BeltsRemoved,
            BatchDemolished,
            // ── FG3-LOG-02 ──
            /// <summary>“优先建造这一片”：框里的施工改成了最高优先级。</summary>
            Prioritized,
            // ── FG3-LOG-03 ──
            /// <summary>清带：选中传送带上的物品送进了仓库（放不下的经确认丢弃）。</summary>
            BeltsCleared,
            /// <summary>原地反转了一格传送带。</summary>
            BeltReversed,
            // ── FG3-LOG-07 ──
            /// <summary>粘贴：放下了一批虚影（合法的部分；非法的部分没放并写明原因）。</summary>
            Pasted,
            /// <summary>升级规划：生成了升级施工。</summary>
            UpgradePlanned,
            /// <summary>复制设置：写到了目标上。</summary>
            SettingsApplied,
        }

        public readonly Kind Outcome;
        public readonly string BuildingId;
        public readonly GridPlacementResult Placement;
        public readonly GridReason? Reason;

        public GridOpResult(Kind outcome, string buildingId, GridPlacementResult placement = null, GridReason? reason = null)
        {
            Outcome = outcome;
            BuildingId = buildingId;
            Placement = placement;
            Reason = reason;
        }

        public bool Success => Outcome != Kind.Failed;

        public static GridOpResult Fail(GridReason reason, GridPlacementResult placement = null) =>
            new GridOpResult(Kind.Failed, null, placement, reason);

        /// <summary>失败原因（当前语言）；成功返回空串。</summary>
        public string Describe()
        {
            if (Success)
            {
                return string.Empty;
            }
            if (Placement != null && Placement.Reasons.Count > 0)
            {
                return Placement.Describe();
            }
            return Reason?.Describe() ?? string.Empty;
        }

        /// <summary>第一个原因码（自检断言用）。</summary>
        public GridBlockReason FirstReason
        {
            get
            {
                if (Placement != null && Placement.Reasons.Count > 0)
                {
                    return Placement.Reasons[0].Code;
                }
                return Reason?.Code ?? GridBlockReason.None;
            }
        }
    }
}
