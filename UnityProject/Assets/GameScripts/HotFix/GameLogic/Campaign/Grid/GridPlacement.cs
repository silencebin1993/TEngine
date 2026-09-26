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
                case GridBlockReason.NoBuilding: return new GridReason(code, "grid.reason.no_building");
                case GridBlockReason.CannotDemolishCore: return new GridReason(code, "grid.reason.cannot_demolish_core");
                case GridBlockReason.DemolishDamaged: return new GridReason(code, "grid.reason.demolish_damaged");
                case GridBlockReason.Busy: return new GridReason(code, "grid.reason.busy");
                case GridBlockReason.CannotRotateCore: return new GridReason(code, "grid.reason.cannot_rotate_core");
                case GridBlockReason.NotRebuildable: return new GridReason(code, "grid.reason.not_rebuildable");
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
