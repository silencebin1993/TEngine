using System;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG3-LOG-05（FG03 FGR-LOG-040～045）管线与流体内核的常量。
    ///
    /// 体积一律用整数毫升（mL），不用浮点：每步的配额按“绝对步序号”取整差分（<see cref="PipeKernel.Budget"/>），
    /// 任何速率在任何步长下长期累计都精确等于“升 / 分钟 × 分钟数”，结果逐位确定。
    /// </summary>
    public static class PipeConst
    {
        /// <summary>管线等级数（T1、T2，FG03 第 10 章“管线吞吐 T1 600 升/分钟，T2 1,200”）。</summary>
        public const int TierCount = 2;

        /// <summary>流体编号上限（0 = 没有流体；1～<see cref="MaxFluid"/> = fg.TbFluid 的 id）。</summary>
        public const int MaxFluid = 200;

        /// <summary>优先级 1～4（沿用电网的优先级：1 最先得到，4 最后，FGR-LOG-042）。</summary>
        public const int PriorityMin = 1;
        public const int PriorityMax = 4;

        /// <summary>储罐（只进）默认优先级：最后才灌（先满足消费者）。</summary>
        public const int TankDefaultPriority = 4;

        /// <summary>阀门向下游输送时的优先级（固定：下游网络只拿本网络消费者用剩的）。</summary>
        public const int ValvePriority = 4;

        /// <summary>坐标上限（绝对值），与传送带内核一致。</summary>
        public const int CoordLimit = 1 << 24;

        public const int None = -1;

        /// <summary>存档格式版本。不认识的版本整体拒绝并原样保留（沿用 ADR-ARC-004 的纪律）。</summary>
        public const int FormatVersion = 1;
    }

    /// <summary>管线层上的件：管线、泵、储罐、阀门（都占一格）。</summary>
    public enum PipePieceKind : byte
    {
        Pipe = 0,
        Pump = 1,
        Tank = 2,
        Valve = 3,
    }

    /// <summary>FGR-LOG-043 储罐：双向（缓冲：有多余就存、不够就放）/ 只进（像消费者一样按优先级灌）/ 只出（只往外放）。</summary>
    public enum PipeTankMode : byte
    {
        Both = 0,
        InOnly = 1,
        OutOnly = 2,
    }

    /// <summary>编辑结果（热更层映射成文本键，B06）。</summary>
    public enum PipeResult : byte
    {
        Ok = 0,
        Occupied,
        NotFound,
        InvalidTier,
        InvalidDirection,
        InvalidArgument,
        /// <summary>FGR-LOG-040：会把两种不同流体的网络连在一起。</summary>
        FluidConflict,
        /// <summary>这一格的件不是要求的种类（例如对管线设储罐模式）。</summary>
        WrongKind,
        InvalidPriority,
        OutOfRange,
        InvalidFluid,
        /// <summary>阀门前后直接接着另一个阀门（阀门不属于网络，两阀对接时流体过不去）：中间至少隔一格管线。</summary>
        ValveChained,
    }

    /// <summary>一个网络为什么没有供给 / 供给不足（悬停与面板的根因，B06）。按位组合。</summary>
    [Flags]
    public enum PipeNetIssue : int
    {
        None = 0,
        /// <summary>网络里没有泵、没有有存量的储罐、也没有上游阀门送来。</summary>
        NoSource = 1,
        /// <summary>有需求但本步什么都没送到（供给为 0）。</summary>
        NoSupply = 2,
        /// <summary>供给不足：有需求没满足。</summary>
        Shortage = 4,
        /// <summary>吞吐被最低等级的管线卡住（供给与需求都比它大）。</summary>
        PipeLimited = 8,
        /// <summary>网络还没有流体（新铺的、刚冲洗过）。</summary>
        Empty = 16,
        /// <summary>泵在，但下游没人要（泵空转，不抽）。</summary>
        NoDemand = 32,
    }

    /// <summary>
    /// 内核配置（热更层读 fg.TbHomeTuning 的 logistics.pipe.* 行组装后传进来；内核不读表）。
    /// 速率单位：升 / 分钟（整数）。
    /// </summary>
    public struct PipeConfig
    {
        public int StepHz;
        public int LitersPerMinuteT1;
        public int LitersPerMinuteT2;
        public int PumpLitersPerMinute;
        public int TankLiters;
        public int TankLitersPerMinute;
        public int ValveLitersPerMinute;

        public static PipeConfig Default => new PipeConfig
        {
            StepHz = 20,
            LitersPerMinuteT1 = 600,
            LitersPerMinuteT2 = 1200,
            PumpLitersPerMinute = 300,
            TankLiters = 5000,
            TankLitersPerMinute = 1200,
            ValveLitersPerMinute = 1200,
        };

        public bool IsValid(out string reason)
        {
            if (StepHz < 1 || StepHz > 240)
            {
                reason = "StepHz 须在 1～240";
                return false;
            }
            if (LitersPerMinuteT1 < 1 || LitersPerMinuteT2 < LitersPerMinuteT1)
            {
                reason = "管线吞吐须 T1 >= 1 且 T2 >= T1";
                return false;
            }
            if (PumpLitersPerMinute < 1 || TankLiters < 1 || TankLitersPerMinute < 1 || ValveLitersPerMinute < 1)
            {
                reason = "泵 / 储罐 / 阀门的数值须 >= 1";
                return false;
            }
            if ((long)Math.Max(Math.Max(LitersPerMinuteT2, PumpLitersPerMinute), Math.Max(TankLitersPerMinute, ValveLitersPerMinute)) > 10_000_000L)
            {
                reason = "速率过大";
                return false;
            }
            reason = null;
            return true;
        }

        public int TierLitersPerMinute(int tier) => tier <= 0 ? LitersPerMinuteT1 : LitersPerMinuteT2;
    }

    /// <summary>一格管线层上的件（悬停、面板、自检读）。</summary>
    public struct PipeCellInfo
    {
        public int X;
        public int Y;
        public PipePieceKind Kind;
        public int Tier;
        /// <summary>阀门：流动方向（0 北 1 东 2 南 3 西，与传送带同一编码）；其余件为 0。</summary>
        public int Dir;
        /// <summary>这一格的流体（= 所在网络的流体；泵 = 水源的流体；阀门 = 缓冲里的流体）。</summary>
        public int Fluid;
        /// <summary>所在网络（阀门不属于任何网络，为 -1）。</summary>
        public int Network;
        /// <summary>连接掩码：bit d = 朝方向 d 的邻格连通。</summary>
        public int Mask;

        // 储罐
        public long TankStockMl;
        public long TankCapacityMl;
        public PipeTankMode TankMode;
        public int Priority;
        /// <summary>上一步灌进 / 放出的毫升（储罐）。</summary>
        public long TankInMl;
        public long TankOutMl;

        // 阀门
        public bool ValveOpen;
        public long ValveBufferMl;
        /// <summary>阀门上游 / 下游网络（-1 = 那一侧没接）。</summary>
        public int ValveFrom;
        public int ValveTo;
        /// <summary>上一步经过阀门的毫升。</summary>
        public long ValveFlowMl;
        /// <summary>阀门两侧流体不同，停止输送。</summary>
        public bool ValveFluidMismatch;

        // 泵
        /// <summary>上一步实际抽取的毫升（没人要时泵空转，为 0）。</summary>
        public long PumpedMl;
        public long PumpTotalMl;
    }

    /// <summary>一个流体网络的读数（FGR-LOG-042“悬停管线时，显示网络的供给、需求、储量和瓶颈管段”）。速率为“最近一步”换算的升 / 分钟。</summary>
    public struct PipeNetInfo
    {
        public int Id;
        public int Fluid;
        public int Cells;
        public int PipeCells;
        public int Pumps;
        public int Tanks;
        public int Consumers;
        public int ValvesIn;
        public int ValvesOut;
        /// <summary>网络里最低的管线等级（没有管线 = -1）。</summary>
        public int MinTier;
        /// <summary>最低等级的管线格数；网络里管线等级不一（真正的瓶颈管段）时 <see cref="Mixed"/> = true。</summary>
        public int MinTierCells;
        public bool Mixed;
        /// <summary>瓶颈管段（最低等级的第一格，按铺设顺序）。</summary>
        public int BottleneckX;
        public int BottleneckY;
        /// <summary>吞吐上限（升 / 分钟）= 最低等级管线的吞吐。</summary>
        public double CapLpm;
        /// <summary>最近一步：可用供给（泵 + 上游阀门 + 储罐可放出的，升 / 分钟）。</summary>
        public double SupplyLpm;
        /// <summary>最近一步：需求（消费者 + 只进储罐 + 下游阀门，升 / 分钟；双向储罐的补仓不算需求）。</summary>
        public double DemandLpm;
        /// <summary>最近一步：送到需求方的（升 / 分钟）。</summary>
        public double DeliveredLpm;
        /// <summary>最近一步：灌进双向储罐的余量（升 / 分钟）。</summary>
        public double BufferedLpm;
        /// <summary>最近一步：没满足的需求（升 / 分钟）。</summary>
        public double UnmetLpm;
        public long StoredMl;
        public long StorageCapMl;
        public PipeNetIssue Issues;
        /// <summary>最近一次有流动时的内核步序号（结冰接口：流量为 0 的时长 = 当前步 − 它）。</summary>
        public long LastFlowStep;
    }

    /// <summary>外部消费者（后续建筑配方接入：FG4-ECO-02 / 03），挂在一格管线件上，按优先级分配。</summary>
    public struct PipeConsumerInfo
    {
        public int Id;
        public int X;
        public int Y;
        public int Fluid;
        public int LitersPerMinute;
        public int Priority;
        public int Network;
        public long LastDemandMl;
        public long LastDeliveredMl;
        public long TotalDeliveredMl;
    }

    /// <summary>
    /// 存档快照（热更层写进 <c>PipeFluidState</c>）。每格一行（按内核里的顺序 = 铺设顺序），外部消费者一行。
    /// 网络编号、连接、网络流体都是派生量（读档后按拓扑重算），不进存档；每格的流体与“最近流动步”进存档。
    /// </summary>
    public sealed class PipeSnapshot
    {
        public int FormatVersion;
        public long StepIndex;
        public long TotalPumpedMl;
        public long TotalDeliveredMl;
        public long TotalFlushedMl;
        public long TotalRemovedMl;

        public int[] X = Array.Empty<int>();
        public int[] Y = Array.Empty<int>();
        public byte[] Kind = Array.Empty<byte>();
        public byte[] Tier = Array.Empty<byte>();
        public byte[] Dir = Array.Empty<byte>();
        public byte[] Fluid = Array.Empty<byte>();
        public long[] Stock = Array.Empty<long>();
        public byte[] Mode = Array.Empty<byte>();
        public byte[] Priority = Array.Empty<byte>();
        public byte[] Open = Array.Empty<byte>();
        public long[] Buffer = Array.Empty<long>();
        public byte[] BufferFluid = Array.Empty<byte>();
        public long[] LastFlow = Array.Empty<long>();
        public long[] PumpTotal = Array.Empty<long>();

        public int[] ConsumerId = Array.Empty<int>();
        public int[] ConsumerX = Array.Empty<int>();
        public int[] ConsumerY = Array.Empty<int>();
        public byte[] ConsumerFluid = Array.Empty<byte>();
        public int[] ConsumerLpm = Array.Empty<int>();
        public byte[] ConsumerPriority = Array.Empty<byte>();
        public long[] ConsumerTotal = Array.Empty<long>();
        public int NextConsumerId = 1;
    }

    /// <summary>渲染实例（与着色器 BinGames/PipeInstanced 的布局一致：A、B 两个 float4 = 32 字节）。
    /// A = (x, y, 种类 + 4 × 方向, 连接掩码)；B = (流体编号, 等级, 储罐液位 0～1, 标志：1 = 画图标、2 = 阀门关闭、4 = 阀门两侧流体不同 / 网络无供给)。</summary>
    public struct PipeInstance
    {
        public float Ax;
        public float Ay;
        public float Az;
        public float Aw;
        public float Bx;
        public float By;
        public float Bz;
        public float Bw;
    }
}
