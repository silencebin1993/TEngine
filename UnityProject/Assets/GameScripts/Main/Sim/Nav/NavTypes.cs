using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace BinGames.Sim.Nav
{
    /// <summary>
    /// FG0-ARCH-06（FG14 FGR-ARC-015 层级寻路；FG15 长距离寻路 2,000 格 ≤ 20 毫秒、工作线程）：寻路内核的常量。
    ///
    /// 内核只认格子（1 格 = 1 米，格 (X, Y) 的中心在世界 (X, Y)）、区块（与格网同边长，初值 32）与“移动类别”，
    /// 不认识建筑、机器、突袭之类的玩法概念：热更层把地形码 → 通行字节的对照表、建筑占地是否挡路传进来。
    /// 每格一个字节：高 4 位 = 通行代价倍率（1～15），低 4 位 = 各移动类别是否被挡（第 c 位 = 类别 c 被挡）。
    /// </summary>
    public static class NavConst
    {
        /// <summary>存档快照格式版本（<see cref="NavKernel.SerializePending"/>）。</summary>
        public const int FormatVersion = 1;

        /// <summary>快照魔数 “NAV1”。</summary>
        public const uint Magic = 0x3156414E;

        /// <summary>移动类别上限（低 4 位）。</summary>
        public const int MaxClasses = 4;

        /// <summary>己方地面单位（机器、远征队）。</summary>
        public const byte ClassPlayer = 0;

        /// <summary>敌方地面单位（突袭者、巡逻）。FG06 的闸门“己方可过、敌方等同屏障”靠两个类别区分。</summary>
        public const byte ClassHostile = 1;

        public const byte BlockAll = 0x0F;

        /// <summary>完全不能走的格子（地形不可通行 / 超出世界 / 地形未知）。</summary>
        public const byte Solid = (1 << 4) | BlockAll;

        /// <summary>直走一格的基础代价（两格代价倍率的平均 × 10）；斜走 × 14。整数运算，结果与平台无关。</summary>
        public const int Ortho = 10;
        public const int Diag = 14;

        public const int NodeStart = -2;
        public const int NodeGoal = -3;
        public const int Infinity = int.MaxValue / 4;
    }

    /// <summary>一次寻路的结果状态。</summary>
    public enum NavStatus : byte
    {
        None = 0,
        Ok = 1,
        /// <summary>目标不可达，允许部分路线（突袭）时走到离目标最近的可达点。</summary>
        Partial = 2,
        Failed = 3,
    }

    /// <summary>寻路失败原因（热更层映射成稳定的文本键 nav.fail.*，B06）。</summary>
    public enum NavFailReason : byte
    {
        None = 0,
        /// <summary>起点周围一圈都走不了（被建筑 / 悬崖 / 水完全围住）。</summary>
        StartBlocked = 1,
        /// <summary>目标点及其附近都走不了（压在建筑、悬崖或水上）。</summary>
        GoalBlocked = 2,
        /// <summary>目标被完全阻断：从起点出发找不到任何通路。</summary>
        Unreachable = 3,
        /// <summary>超出寻路搜索范围（绕路过长或太远）。</summary>
        SearchLimit = 4,
        /// <summary>超出世界坐标上限（world.coord_limit）。</summary>
        OutOfWorld = 5,
        /// <summary>地形未知（旧原型地形只覆盖家园附近的区块）。</summary>
        UnknownTerrain = 6,
    }

    [Flags]
    public enum NavRequestFlags : byte
    {
        None = 0,
        /// <summary>目标不可达时返回走到最近可达点的部分路线（敌方行军：FG06 攻城在 FG6-DEF-05）。</summary>
        AllowPartial = 1,
    }

    /// <summary>一条寻路请求。<see cref="OwnerTag"/> / <see cref="OwnerKey"/> / <see cref="Serial"/> 由请求方给出，结果原样带回（请求方据此认领、丢弃过期结果）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavRequest
    {
        public int OwnerTag;
        public int OwnerKey;
        public int Serial;
        public byte Class;
        public NavRequestFlags Flags;
        public short Pad;
        public int2 Start;
        public int2 Goal;
        public long IssuedTick;
    }

    /// <summary>一条寻路结果。路点在 <see cref="NavKernel"/> 的结果路点池里 [PointStart, PointStart + PointCount)，不含起点格。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavResult
    {
        public NavRequest Request;
        public NavStatus Status;
        public NavFailReason Reason;
        /// <summary>1 = 复用了同一批里同一目标、同一起点区块的代表路线（FGR-ARC-015“同一目标的多个单位共享路线”）。</summary>
        public byte Shared;
        public byte Pad;
        public int PointStart;
        public int PointCount;
        /// <summary>实际终点格（目标格被占时是就近可达格；部分路线时是最近可达点）。</summary>
        public int2 End;
        /// <summary>路线长度（格，按路点折线）。</summary>
        public float Length;
        /// <summary>抽象图上展开的节点数（统计 / 性能证据）。</summary>
        public int Expanded;
    }

    /// <summary>寻路内核配置（热更层读 nav.* 调参行组装；内核不读表）。</summary>
    [Serializable]
    public struct NavConfig
    {
        public int ChunkSize;
        /// <summary>结果在请求批次调度后第几个模拟步被采纳（固定延迟：与工作线程快慢、帧率、倍速无关 → 确定性）。</summary>
        public int LatencySteps;
        /// <summary>一批最多处理几条请求。</summary>
        public int BatchMaxRequests;
        /// <summary>一批请求的估算工作量上限（起终点八向距离之和，格）；第一条总会被处理。与缓存冷热无关 → 分批确定。</summary>
        public int BatchDistanceBudget;
        /// <summary>抽象搜索范围：起终点区块外接矩形向外放宽的区块数。</summary>
        public int SearchMarginChunks;
        /// <summary>单条请求最多展开的抽象节点数。</summary>
        public int MaxExpansions;
        /// <summary>目标格走不了时，在多大半径（格）内找就近可达格。</summary>
        public int GoalSearchRadius;
        /// <summary>起点格走不了时（机器站在建筑里）找就近可达格的半径。</summary>
        public int StartSearchRadius;
        /// <summary>路线平滑时一个锚点最多向前看几个格。</summary>
        public int SmoothLookahead;
        /// <summary>世界坐标上限（格，world.coord_limit）；超出视为不可通行。</summary>
        public int CoordLimit;

        public static NavConfig Default => new NavConfig
        {
            ChunkSize = 32,
            LatencySteps = 6,
            BatchMaxRequests = 64,
            BatchDistanceBudget = 6000,
            SearchMarginChunks = 4,
            MaxExpansions = 40000,
            GoalSearchRadius = 6,
            StartSearchRadius = 6,
            SmoothLookahead = 48,
            CoordLimit = 1_000_000,
        };
    }

    /// <summary>汇总计数（统计 / 自检 / 性能证据）。</summary>
    [Serializable]
    public struct NavCounters
    {
        public long Requests;
        public long Batches;
        public long Searches;
        public long Shared;
        public long Failed;
        public long Partial;
        public long ChunksGenerated;
        public long ChunkGraphsBuilt;
        public long EdgeSetsBuilt;
        public long Expanded;
        public long LateCompletes;
        public long Invalidated;
        public long Requeued;
        public long GraphResets;
    }
}
