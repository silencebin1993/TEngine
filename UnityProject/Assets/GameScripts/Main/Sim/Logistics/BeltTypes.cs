using System;
using Unity.Mathematics;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG0-ARCH-02（FG14 FGR-ARC-004；FG03 FGR-LOG-020/021/025/090；FG15 FGR-SYS-041/042）传送带内核的常量。
    ///
    /// 位置一律用整数定点：一格传送带长 <see cref="CellLength"/> 个单位。48000 能被 4（每格容量）、20（步长频率）、60（秒 / 分）以及
    /// 规格初值的三档速度（60 / 120 / 240 件每分钟）整除，所以三档速度换算成“单位 / 步”都是整数（600 / 1200 / 2400），吞吐没有舍入误差，
    /// 结果在任何平台上逐位一致（不用浮点，不依赖 Burst 的浮点模式）。
    /// </summary>
    public static class BeltConst
    {
        /// <summary>一格传送带的长度（定点单位）。</summary>
        public const int CellLength = 48000;

        /// <summary>每格最多几个物品（存储布局的硬上限；表里的 logistics.slots_per_cell 只能在 1～4 之间）。</summary>
        public const int MaxSlots = 4;

        /// <summary>传送带等级数（T1～T3，FGR-LOG-020）。</summary>
        public const int TierCount = 3;

        /// <summary>吞吐统计的滚动快照个数（窗口 = 快照个数 × 快照间隔步数）。</summary>
        public const int Buckets = 4;

        /// <summary>“无限”：输出端口无限供货、输入端口自动消耗（性能场景和测试用）。</summary>
        public const int Unlimited = -1;

        public const int None = -1;

        /// <summary>一格最多几个上游（除正前方以外的三个方向）。</summary>
        public const int MaxFeeders = 3;

        /// <summary>合流“轮到谁”未设置。</summary>
        public const byte NoTurn = 255;

        /// <summary>坐标上限（绝对值）。与世界坐标上限 world.coord_limit 同一量级，远大于它时拒绝（防溢出）。</summary>
        public const int CoordLimit = 1 << 24;

        /// <summary>FG3-LOG-03：端口朝向“任意”（FG0-ARCH-02 的测试端口与旧存档）。建筑端口的朝向是端口朝外的方向（N/E/S/W）。</summary>
        public const byte AnyFace = 255;

        /// <summary>FG3-LOG-03：输入端口收什么——0 = 任何物品；<see cref="AcceptNone"/> = 什么都不收（建筑还没有这类物品的配方）；其余 = 只收这一种。</summary>
        public const ushort AcceptAny = 0;
        public const ushort AcceptNone = 0xFFFF;
        /// <summary>FG4-ECO-01：任何物品都收，但缓存里同一时刻只有一种（缓存种类记在端口的 Item 上）；家园仓库的输入口用它按种类入库。</summary>
        public const ushort AcceptAnyOneKind = 0xFFFE;
        /// <summary>FG4-ECO-03：只收“收货集合”（<see cref="BeltConfig.AcceptSetMask"/>，物品编号 1～63 的位掩码）里的物品，缓存里同一时刻只有一种（与 <see cref="AcceptAnyOneKind"/> 同一记法）；
        /// 不在集合里的物品让带停下（原因 <see cref="BeltBlock.SinkRejects"/>）。装配站的输入口用它只收机器材料。集合由热更层在建内核时写进配置（内容决定，不进存档）。</summary>
        public const ushort AcceptSet = 0xFFFD;
        /// <summary>FG5-RND-02：第二个收货集合（<see cref="BeltConfig.AcceptSet2Mask"/>），语义与 <see cref="AcceptSet"/> 相同（一次一种、不在集合里的让带停下）。
        /// 解析台的输入口用它只收敌方物品与残骸。集合同样由热更层在建内核时写进配置（内容决定，不进存档）。</summary>
        public const ushort AcceptSet2 = 0xFFFC;

        /// <summary>这种收法是不是“收货集合”（缓存里一次一种）。</summary>
        public static bool IsSetAccept(ushort accept) => accept == AcceptSet || accept == AcceptSet2;

        /// <summary>FG3-LOG-03（FGR-LOG-028）：露天传送带减速的上限（百分比）。</summary>
        public const int MaxSlowPercent = 90;

        /// <summary>FG3-LOG-04（FGR-LOG-022）：分流器每个输出口的过滤——0 = 全部物品；<see cref="FilterNone"/> = 这个口不出；其余 = 只放这一种。</summary>
        public const ushort FilterAny = 0;
        public const ushort FilterNone = 0xFFFF;

        /// <summary>FG3-LOG-04：分流比例每一边的份数上限（1～9，默认 1:1）。</summary>
        public const int RatioMax = 9;

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-023）：地下传送带入口到出口的距离硬上限（内核防御；按等级的跨度由热更层读表校验，初值 T1 4 / T2 6 / T3 8 格）。
        /// 距离 = 出口与入口相差的格数（跨度 + 1）。
        /// </summary>
        public const int MaxUndergroundDistance = 32;

        /// <summary>
        /// FG3-LOG-04：地下段所在的“地下层”坐标偏移。地下段的格与地面的格同在一张表里，但 y 坐标加上偏移（南北向 / 东西向各一层），
        /// 于是地下段不会与地面的传送带、建筑冲突，南北向与东西向的地下段可以交叉，同一方向的地下段不能重叠。
        /// 地面坐标 |y| ≤ <see cref="CoordLimit"/>（2^24），两层的取值范围互不相交，也都在 int 范围内。
        /// </summary>
        public const int UnderLayerNS = 1 << 26;
        public const int UnderLayerEW = 1 << 27;
    }

    /// <summary>FG3-LOG-04（FGR-LOG-022 / 023）：一格的种类。普通传送带之外是物流节点；地下段（<see cref="Underground"/>）不在地面上。</summary>
    public enum BeltNodeKind : byte
    {
        Belt = 0,
        /// <summary>分流器：从后方进，左右两口出（比例 / 优先输出口 / 每口过滤）。</summary>
        Splitter = 1,
        /// <summary>合流器：左右两侧进，从前方出（默认交替 / 优先输入口）。</summary>
        Merger = 2,
        /// <summary>地下传送带入口：从后方进，物品沿朝向走地下。</summary>
        UndergroundIn = 3,
        /// <summary>地下传送带出口：物品从地下冒出来继续向前（不从地面进料）。</summary>
        UndergroundOut = 4,
        /// <summary>地下段（入口与出口之间，坐标在地下层；不画、不占地面）。</summary>
        Underground = 5,
    }

    /// <summary>FG3-LOG-04：节点的左 / 右口（相对节点朝向；0 = 不设）。</summary>
    public enum BeltSide : byte
    {
        None = 0,
        Left = 1,
        Right = 2,
    }

    /// <summary>FG3-LOG-04：分流器一个输出口此刻的状态（悬停与节点面板读）。</summary>
    public enum BeltOutletState : byte
    {
        /// <summary>接上了、能收。</summary>
        Ok = 0,
        /// <summary>接上了，但下游这一格入口没有空位（满了 / 堵住了）。</summary>
        Full = 1,
        /// <summary>这一侧没有能接的传送带。</summary>
        Disconnected = 2,
        /// <summary>这个口的过滤不收分流器里头一件物品（或设成了“不出”）。</summary>
        Filtered = 3,
    }

    /// <summary>传送带朝向。与 GameLogic 的 GridDir 取值一致（N=0、E=1、S=2、W=3），格网 y 轴向北。</summary>
    public enum BeltDir : byte
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
    }

    /// <summary>内核 API 的结果（拒绝一律给稳定的原因码，热更层映射成文本键：IC-REQ-013）。</summary>
    public enum BeltResult : byte
    {
        Ok = 0,
        /// <summary>这一格已经有传送带。</summary>
        Occupied = 1,
        /// <summary>这一格没有传送带 / 没有这个端口。</summary>
        NotFound = 2,
        InvalidTier = 3,
        InvalidDirection = 4,
        /// <summary>这一格放不下更多物品（会与已有物品重叠）。</summary>
        NoSpace = 5,
        /// <summary>同一位置已经有同类端口 / 端口编号重复。</summary>
        PortOccupied = 6,
        PortNotFound = 7,
        InvalidArgument = 8,
        /// <summary>坐标超出内核上限。</summary>
        OutOfRange = 9,
        /// <summary>FG3-LOG-04：这种格不支持这个操作（例如地下传送带原地反转、给分流器改等级）。</summary>
        NotSupported = 10,
        /// <summary>FG3-LOG-04（FGR-LOG-023）：地下已经有同一方向（南北 / 东西）的地下段经过这里。</summary>
        UndergroundOccupied = 11,
        /// <summary>FG3-LOG-04（FGR-LOG-023）：地下传送带入口到出口太远（超过内核硬上限）。</summary>
        TooFar = 12,
    }

    /// <summary>一格传送带为什么停着（FGR-LOG-025“下游满了就停下，物品不会消失”；悬停说明原因）。</summary>
    public enum BeltBlock : byte
    {
        None = 0,
        /// <summary>传送带到头了，后面没有接任何东西。</summary>
        EndOfBelt = 1,
        /// <summary>下游传送带已满。</summary>
        DownstreamFull = 2,
        /// <summary>下游是建筑的输入端口，它暂时不收（缓存满）。</summary>
        SinkFull = 3,
        /// <summary>汇入点轮到另一路（交替汇入，FGR-LOG-021）。</summary>
        MergeWait = 4,
        /// <summary>FG3-LOG-03：下游输入端口不收这一件（只收指定物品，或建筑还不收物品）。</summary>
        SinkRejects = 5,
        /// <summary>FG3-LOG-04：正前方是物流节点，但它不从这一侧进料（分流器 / 地下入口只从后方进，合流器只从左右两侧进，地下出口不从地面进）。</summary>
        WrongSide = 6,
        /// <summary>FG3-LOG-04（FG03 第 5 节“分流器两个输出口都堵”）：能收这件物品的输出口都满了，分流器停止，上游随之堵塞。</summary>
        SplitterFull = 7,
        /// <summary>FG3-LOG-04：分流器没有输出口收这件物品（过滤不收 / 两侧都没接传送带）。</summary>
        SplitterNoOutlet = 8,
    }

    public enum BeltPortKind : byte
    {
        None = 0,
        /// <summary>建筑的输出端口：把物品推上 (X, Y) 这一格传送带。</summary>
        Source = 1,
        /// <summary>建筑的输入端口：位于建筑格 (X, Y)；正前方是 (X, Y) 的传送带末端把物品送进来。</summary>
        Sink = 2,
    }

    /// <summary>内核配置（全部来自 fg.TbHomeTuning 的 logistics.* 行，由热更层读表后传入；内核不读表）。</summary>
    [Serializable]
    public struct BeltConfig
    {
        /// <summary>每格容量（FG03 第 10 章初值 4）。</summary>
        public int SlotsPerCell;
        /// <summary>内核固定步长频率（FGR-ARC-004 初值 20 Hz）。</summary>
        public int StepHz;
        /// <summary>三档速度（件 / 分钟，FG03 第 10 章初值 60 / 120 / 240）。</summary>
        public int ItemsPerMinuteT1;
        public int ItemsPerMinuteT2;
        public int ItemsPerMinuteT3;
        /// <summary>吞吐统计快照间隔（内核步）。窗口 = <see cref="BeltConst.Buckets"/> 个间隔。</summary>
        public int BucketSteps;
        /// <summary>FG4-ECO-03：<see cref="BeltConst.AcceptSet"/> 收法的收货集合（位 i = 物品编号 i；只支持 1～63）。0 = 空集合（这种口什么都不收）。</summary>
        public ulong AcceptSetMask;
        /// <summary>FG5-RND-02：<see cref="BeltConst.AcceptSet2"/> 收法的收货集合（位 i = 物品编号 i；只支持 1～63）。0 = 空集合。</summary>
        public ulong AcceptSet2Mask;

        public static BeltConfig Default => new BeltConfig
        {
            SlotsPerCell = 4,
            StepHz = 20,
            ItemsPerMinuteT1 = 60,
            ItemsPerMinuteT2 = 120,
            ItemsPerMinuteT3 = 240,
            BucketSteps = 300,
        };

        public int ItemsPerMinute(int tier) => tier == 0 ? ItemsPerMinuteT1 : tier == 1 ? ItemsPerMinuteT2 : ItemsPerMinuteT3;

        /// <summary>配置是否可用（非法值时内核拒绝创建，热更层回落到 <see cref="Default"/> 并报错）。</summary>
        public bool IsValid(out string reason)
        {
            if (SlotsPerCell < 1 || SlotsPerCell > BeltConst.MaxSlots)
            {
                reason = $"slots_per_cell={SlotsPerCell} 不在 1～{BeltConst.MaxSlots}";
                return false;
            }
            if (StepHz < 1 || StepHz > 240)
            {
                reason = $"step_hz={StepHz} 不在 1～240";
                return false;
            }
            for (int t = 0; t < BeltConst.TierCount; t++)
            {
                int ipm = ItemsPerMinute(t);
                if (ipm < 1)
                {
                    reason = $"T{t + 1} 速度 {ipm} 件/分钟 非正";
                    return false;
                }
                // 每步位移必须小于物品间距（一步最多跨过一格边界 / 一个物品），否则算法前提不成立。
                long perStep = UnitsPerStep(ipm);
                if (perStep >= BeltConst.CellLength / SlotsPerCell)
                {
                    reason = $"T{t + 1} 速度 {ipm} 件/分钟 在 {StepHz} Hz 下每步位移 {perStep} ≥ 物品间距";
                    return false;
                }
            }
            if (BucketSteps < 1)
            {
                reason = $"bucket_steps={BucketSteps} 非正";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>吞吐 ipm（件 / 分钟）换算成每步位移（定点单位）：满载时每格 SlotsPerCell 件，
        /// 带速（格 / 秒）= ipm / 60 / SlotsPerCell；四舍五入到整数单位。</summary>
        public long UnitsPerStep(int ipm) =>
            ((long)ipm * BeltConst.CellLength + 30L * SlotsPerCell * StepHz) / (60L * SlotsPerCell * StepHz);
    }

    /// <summary>一格传送带的悬停数据（FGR-LOG-081：当前物品、速度、吞吐；FGR-LOG-025：堵塞原因）。</summary>
    public struct BeltCellInfo
    {
        public int X;
        public int Y;
        public BeltDir Dir;
        public byte Tier;
        public int Count;
        public BeltBlock Block;
        public int Network;
        /// <summary>下游是不是一格传送带；是的话 <see cref="NextX"/>/<see cref="NextY"/> 是它的坐标。</summary>
        public bool HasNext;
        public int NextX;
        public int NextY;
        /// <summary>接的输入端口编号（-1 = 没有）。</summary>
        public int SinkPortId;
        /// <summary>上游数（1 = 直线，2～3 = 汇入点）。</summary>
        public int Feeders;
        /// <summary>是不是环上的一格。</summary>
        public bool InLoop;
        /// <summary>最近窗口里流出这一格的物品数与窗口长度（游戏秒）。</summary>
        public int PassedInWindow;
        public float WindowSeconds;
        /// <summary>这一格现在的满载速度（件 / 分钟）：等级速度，露天且在减速天气下再乘 (100 − 减速%)（FGR-LOG-028）。</summary>
        public int RatedItemsPerMinute;
        /// <summary>FG3-LOG-03：这一档的设计速度（件 / 分钟，不含天气）。</summary>
        public int TierItemsPerMinute;
        /// <summary>FG3-LOG-03：这一格在顶棚下（不受天气减速）。</summary>
        public bool Covered;
        /// <summary>FG3-LOG-03：这一格现在被天气减速了多少（百分比；有顶棚或没有减速天气时为 0）。</summary>
        public int SlowPercent;
        /// <summary>FG3-LOG-04：这一格的种类（普通传送带 / 分流器 / 合流器 / 地下入口 / 地下出口 / 地下段）。</summary>
        public BeltNodeKind Kind;
        /// <summary>汇入点现在轮到哪一侧（0～3；255 = 未设置）。诊断 / 自检用（存读档逐字段比对）。</summary>
        public byte Turn;
        /// <summary>FG3-LOG-04：下游是地下段（地下传送带入口）。此时 <see cref="NextX"/>/<see cref="NextY"/> 是这条地下传送带的出口（地面坐标）。</summary>
        public bool NextUnderground;
        /// <summary>FG3-LOG-04：原因为 <see cref="BeltBlock.WrongSide"/> 时，正前方那个节点的种类与坐标。</summary>
        public BeltNodeKind FrontKind;
        public int FrontX;
        public int FrontY;
        public ushort Item0;
        public ushort Item1;
        public ushort Item2;
        public ushort Item3;
        public int Pos0;
        public int Pos1;
        public int Pos2;
        public int Pos3;

        public ushort ItemAt(int i) => i == 0 ? Item0 : i == 1 ? Item1 : i == 2 ? Item2 : Item3;
        public int PosAt(int i) => i == 0 ? Pos0 : i == 1 ? Pos1 : i == 2 ? Pos2 : Pos3;

        /// <summary>实测吞吐（件 / 分钟）。</summary>
        public float ThroughputPerMinute => WindowSeconds > 0f ? PassedInWindow * 60f / WindowSeconds : 0f;
    }

    /// <summary>FG3-LOG-08（FGR-LOG-082）：从某一格顺着下游追到堵塞源头的结果（<see cref="BeltKernel.TryTraceBlock"/>）。</summary>
    public struct BeltBlockTrace
    {
        public int StartX;
        public int StartY;
        /// <summary>往下游走了几格（0 = 起点自己就是源头）。</summary>
        public int Hops;
        /// <summary>整圈都满的环形传送带：走满一圈也没找到源头。</summary>
        public bool Looped;
        /// <summary>源头那一格的读数（<see cref="BeltCellInfo.Block"/> = 真正的原因；None = 这一格在动，只是吞吐到顶）。</summary>
        public BeltCellInfo End;
    }

    /// <summary>一个物流网络（弱连通的一组传送带）的吞吐统计（FGR-ARC-004 热更层只读的第二类数据）。</summary>
    public struct BeltNetworkStats
    {
        public int Network;
        public int Cells;
        public int Items;
        public int BlockedCells;
        public bool HasCycle;
        public int Sources;
        public int Sinks;
        /// <summary>最近窗口里送进输入端口 / 从输出端口推上来的物品数。</summary>
        public int DeliveredInWindow;
        public int EmittedInWindow;
        public float WindowSeconds;

        public float DeliveredPerMinute => WindowSeconds > 0f ? DeliveredInWindow * 60f / WindowSeconds : 0f;
        public float EmittedPerMinute => WindowSeconds > 0f ? EmittedInWindow * 60f / WindowSeconds : 0f;
    }

    /// <summary>一个端口的汇总（FGR-ARC-004 热更层只读的第一类数据：每座建筑的输入输出汇总）。</summary>
    public struct BeltPortInfo
    {
        public int Id;
        public BeltPortKind Kind;
        public int X;
        public int Y;
        public int Owner;
        /// <summary>接到的传送带格是否存在（输出端口：(X,Y) 上有没有带；输入端口：有没有带的末端朝向它）。</summary>
        public bool Connected;
        public int Network;
        public ushort ItemType;
        public int IntervalSteps;
        /// <summary>输出端口待推的物品数（-1 = 无限）。</summary>
        public int Pending;
        /// <summary>输入端口缓存里的物品数与上限（上限 -1 = 自动消耗、不限）。</summary>
        public int Buffered;
        public int BufferCap;
        /// <summary>累计推出 / 收下的物品数；输入端口累计消耗数。</summary>
        public long Total;
        public long Consumed;
        /// <summary>想推却推不上去的步数（堵塞诊断）。</summary>
        public long BlockedSteps;
        public int InWindow;
        public float WindowSeconds;
        /// <summary>FG3-LOG-03：端口朝外的方向（0～3；<see cref="BeltConst.AnyFace"/> = 任意）。</summary>
        public byte Face;
        /// <summary>FG3-LOG-03：输入端口收什么（<see cref="BeltConst.AcceptAny"/> / <see cref="BeltConst.AcceptNone"/> / 物品编号）。</summary>
        public ushort Accept;
        /// <summary>节拍计数（输出端口：距上次推出的步数；输入端口：消耗节拍）。诊断 / 自检用。</summary>
        public int Phase;

        public float PerMinute => WindowSeconds > 0f ? InWindow * 60f / WindowSeconds : 0f;
    }

    /// <summary>
    /// FG3-LOG-04（FGR-LOG-022 / 023；FGR-LOG-081 悬停“分流比例”）：一个物流节点的读数（热更层只读，O(1)）。
    /// 分流器：比例、优先输出口、每口过滤、两个输出口的连接与此刻状态、累计与最近窗口的分出件数；
    /// 合流器：优先输入口、两侧输入是否接上；地下传送带：入口 / 出口、距离、另一端、地下段里的件数与容量。
    /// </summary>
    public struct BeltNodeInfo
    {
        public BeltNodeKind Kind;
        public int X;
        public int Y;
        public BeltDir Dir;
        public byte Tier;
        /// <summary>这一格上的件数与原因（分流器的原因由节点阶段写入）。</summary>
        public int Count;
        public BeltBlock Block;
        public ushort HeadItem;

        // 分流器
        public int RatioL;
        public int RatioR;
        public BeltSide PriorityOut;
        public ushort FilterL;
        public ushort FilterR;
        public bool OutLConnected;
        public bool OutRConnected;
        public int OutLX;
        public int OutLY;
        public int OutRX;
        public int OutRY;
        public BeltOutletState OutLState;
        public BeltOutletState OutRState;
        public long SentL;
        public long SentR;
        public int SentLInWindow;
        public int SentRInWindow;
        public float WindowSeconds;
        /// <summary>分流器 / 地下入口：后方接了传送带（能进料）。</summary>
        public bool InputConnected;

        // 合流器
        public BeltSide PriorityIn;
        public bool InLConnected;
        public bool InRConnected;

        // 地下传送带（入口与出口都给出整条的数据）
        /// <summary>出口与入口相差的格数（跨度 = 距离 − 1）。</summary>
        public int Distance;
        public int EntranceX;
        public int EntranceY;
        public int ExitX;
        public int ExitY;
        /// <summary>入口、地下段、出口都在（读档坏块等异常下为 false）。</summary>
        public bool Intact;
        /// <summary>入口 + 地下段 + 出口上的件数与总容量。</summary>
        public int ItemsInside;
        public int Capacity;

        public float SentLPerMinute => WindowSeconds > 0f ? SentLInWindow * 60f / WindowSeconds : 0f;
        public float SentRPerMinute => WindowSeconds > 0f ? SentRInWindow * 60f / WindowSeconds : 0f;
    }

    /// <summary>物品守恒账（FGT-LOG-006）：在带数 = 推上 + 放入 − 收下 − 移出。</summary>
    public struct BeltLedger
    {
        public long Emitted;
        public long Inserted;
        public long Delivered;
        public long Removed;
        public long OnBelts;

        public long Expected => Emitted + Inserted - Delivered - Removed;
        public bool Balanced => Expected == OnBelts;
    }

    /// <summary>渲染实例（传送带格与物品共用同一布局，一个 GraphicsBuffer 一种）。
    /// 格：A = (x, z, dirX, dirZ)，B = (速度 格/秒, 占用率 0～1, 堵塞码, 等级)。
    /// 物品：A = (x, z, 上一步位移 dx, dz)，B = (物品种类, 0, 0, 0)。</summary>
    public struct BeltInstance
    {
        public float4 A;
        public float4 B;
    }

    public static class BeltDirs
    {
        public static int Dx(int dir) => dir == 1 ? 1 : dir == 3 ? -1 : 0;
        public static int Dy(int dir) => dir == 0 ? 1 : dir == 2 ? -1 : 0;
        public static int Opposite(int dir) => (dir + 2) & 3;

        /// <summary>FG3-LOG-04：朝向 <paramref name="dir"/> 时的左手方向（N→W、E→N、S→E、W→S）。</summary>
        public static int Left(int dir) => (dir + 3) & 3;

        /// <summary>FG3-LOG-04：朝向 <paramref name="dir"/> 时的右手方向（N→E、E→S、S→W、W→N）。</summary>
        public static int Right(int dir) => (dir + 1) & 3;

        /// <summary>坐标键：高 32 位 = y（有符号），低 32 位 = x 映射到无符号保序。按键升序 = 按 (y, x) 升序（内核的规范顺序）。</summary>
        public static long Key(int x, int y) => ((long)y << 32) | (uint)(x ^ int.MinValue);

        /// <summary>FG3-LOG-04：y 是不是地下层坐标（地面 |y| ≤ <see cref="BeltConst.CoordLimit"/>）。</summary>
        public static bool IsUnderY(int y) => y > BeltConst.CoordLimit;

        /// <summary>FG3-LOG-04：朝向 <paramref name="dir"/> 的地下段在地面 y 下方的地下层坐标（南北向 / 东西向各一层）。</summary>
        public static int UnderY(int y, int dir) => y + ((dir & 1) == 0 ? BeltConst.UnderLayerNS : BeltConst.UnderLayerEW);

        /// <summary>FG3-LOG-04：地下层坐标 → 地面 y（地面坐标原样返回）。</summary>
        public static int SurfaceY(int y)
        {
            if (!IsUnderY(y))
            {
                return y;
            }
            return y > (BeltConst.UnderLayerNS + BeltConst.UnderLayerEW) / 2 ? y - BeltConst.UnderLayerEW : y - BeltConst.UnderLayerNS;
        }
    }
}
