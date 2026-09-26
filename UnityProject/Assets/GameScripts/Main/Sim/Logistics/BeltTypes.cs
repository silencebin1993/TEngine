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
        /// <summary>这一档的设计速度（件 / 分钟）。</summary>
        public int RatedItemsPerMinute;
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

        public float PerMinute => WindowSeconds > 0f ? InWindow * 60f / WindowSeconds : 0f;
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

        /// <summary>坐标键：高 32 位 = y（有符号），低 32 位 = x 映射到无符号保序。按键升序 = 按 (y, x) 升序（内核的规范顺序）。</summary>
        public static long Key(int x, int y) => ((long)y << 32) | (uint)(x ^ int.MinValue);
    }
}
