using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG0-ARCH-02 传送带内核（FG14 FGR-ARC-004；FG03 FGR-LOG-020/021/025/090；FG15 FGR-SYS-041/042）。
    ///
    /// - AOT 模块（BinGames.Sim），Burst + Jobs，数据用 SoA 布局（每格一列 NativeList），整数定点，结果逐位确定。
    /// - 固定步长：调用方按游戏时间以 <see cref="BeltConfig.StepHz"/>（初值 20 Hz）调用 <see cref="Step"/>；与渲染帧率、倍速无关。
    /// - 热更层只读三类数据：端口汇总（<see cref="TryGetPortInfo"/>，每座建筑的输入输出）、网络统计（<see cref="TryGetNetworkStats"/>）、
    ///   渲染缓冲（<see cref="PrepareRender"/>，由 <see cref="BeltRenderer"/> 实例化绘制）。逐格 / 逐物品的循环全部在这里。
    /// - 编辑（放 / 拆 / 反转 / 端口）只标记脏，下一次步进或查询前统一重建一次拓扑（规范下标 = 按坐标排序），
    ///   所以同一组传送带不论放置顺序、是否经过存读档，后续演化都逐位相同。
    /// - 存档：按网络分块序列化（<see cref="Serialize"/> / <see cref="Deserialize"/>），每块自带校验和；损坏的块单独丢弃并记账。
    /// - FG3-LOG-04：物流节点（<see cref="AddNode"/> 分流器 / 合流器、<see cref="AddUnderground"/> 地下传送带；设置 <see cref="SetSplitter"/> / <see cref="SetMergerPriority"/>；
    ///   读数 <see cref="TryGetNodeInfo"/>）。节点也是一格传送带，设置与状态放在按坐标规范排序的节点表里；地下段用地下层坐标（<see cref="BeltDirs.UnderY"/>）。
    /// 原生内存成对释放：<see cref="Dispose"/>。
    /// </summary>
    public sealed class BeltKernel : IDisposable
    {
        /// <summary>存档格式版本。4 = FG3-LOG-09（DEBT-FG0ARCH02-10：统计窗口进存档——网络块与端口块各多一段“统计尾段”，快照头多了已完成桶数；
        /// 读档后“实测吞吐 / 实测比例 / 端口实测”与存档前逐位一致，不再从零重新累计）；
        /// 3 = FG3-LOG-04（网络块每格多了种类与节点设置：分流器 / 合流器 / 地下传送带）；
        /// 2 = FG3-LOG-03（端口块多了朝向与收货过滤）；1 = FG0-ARCH-02。读档四种都认。</summary>
        public const int FormatVersion = 4;

        /// <summary>统计尾段的标记字节（格式 4 起，在网络块 / 端口块的正文末尾、校验和之前）。</summary>
        private const byte StatsMarker = (byte)'S';

        /// <summary>读档仍认的最老版本。</summary>
        public const int OldestReadableVersion = 1;

        private const int S = BeltConst.MaxSlots;

        private readonly BeltConfig _config;
        private readonly int _spacing;
        private readonly int _v0;
        private readonly int _v1;
        private readonly int _v2;
        /// <summary>FG3-LOG-03（FGR-LOG-028）：露天减速（百分比）与三档减速后的每步位移。派生量：天气（FG7）每步由热更层设定，不进存档。</summary>
        private int _slowPct;
        private int _s0;
        private int _s1;
        private int _s2;
        private NativeList<byte> _covered;
        private NativeParallelHashSet<long> _coveredKeys;

        private NativeList<int> _x;
        private NativeList<int> _y;
        private NativeList<byte> _dir;
        private NativeList<byte> _tier;
        private NativeList<byte> _alive;
        private NativeList<byte> _count;
        private NativeList<byte> _turn;
        private NativeList<byte> _block;
        private NativeList<byte> _ring;
        private NativeList<byte> _feedN;
        private NativeList<int> _next;
        private NativeList<int> _sink;
        private NativeList<int> _net;
        private NativeList<int> _flow;
        private NativeList<int> _feed;
        private NativeList<int> _pos;
        private NativeList<int> _delta;
        private NativeList<int> _flowB;
        private NativeList<ushort> _item;
        private NativeParallelHashMap<long, int> _lookup;
        private NativeList<int> _ringCells;
        private NativeList<int2> _rings;
        private NativeList<int> _ringOf;
        private NativeList<int> _ringCount;
        private NativeList<int> _tree;
        private NativeList<BeltNetAgg> _nets;
        private NativeList<BeltPort> _ports;
        /// <summary>FG3-LOG-04：物流节点（分流器 / 合流器 / 地下入口 / 地下出口）与每格的派生种类、节点下标、“正前方节点不从这一侧进料”标记。</summary>
        private NativeList<BeltNode> _nodes;
        private NativeList<byte> _kind;
        private NativeList<int> _nodeOf;
        private NativeList<byte> _edge;
        private NativeArray<long> _counters;
        private NativeArray<ulong> _hashOut;
        private NativeArray<int> _renderCounts;
        private NativeArray<BeltInstance> _renderCells;
        private NativeArray<BeltInstance> _renderItems;

        private bool _dirty;
        private bool _disposed;
        private int _aliveCells;
        private int _renderRevisionSeen = -1;
        private int _renderCellsSeen = -1;
        /// <summary>物品实例缓冲是否对应 <see cref="_renderRevisionSeen"/>（远景只准备格实例时为 false）。</summary>
        private bool _renderItemsValid;
        private readonly Stopwatch _watch = new Stopwatch();

        public BeltKernel(BeltConfig config, int initialCells = 256)
        {
            if (!config.IsValid(out string reason))
            {
                throw new ArgumentException("BeltConfig 非法：" + reason);
            }
            _config = config;
            _spacing = BeltConst.CellLength / config.SlotsPerCell;
            _v0 = (int)config.UnitsPerStep(config.ItemsPerMinuteT1);
            _v1 = (int)config.UnitsPerStep(config.ItemsPerMinuteT2);
            _v2 = (int)config.UnitsPerStep(config.ItemsPerMinuteT3);
            _s0 = _v0;
            _s1 = _v1;
            _s2 = _v2;
            int cap = Math.Max(16, initialCells);
            _covered = new NativeList<byte>(cap, Allocator.Persistent);
            _coveredKeys = new NativeParallelHashSet<long>(16, Allocator.Persistent);
            _x = new NativeList<int>(cap, Allocator.Persistent);
            _y = new NativeList<int>(cap, Allocator.Persistent);
            _dir = new NativeList<byte>(cap, Allocator.Persistent);
            _tier = new NativeList<byte>(cap, Allocator.Persistent);
            _alive = new NativeList<byte>(cap, Allocator.Persistent);
            _count = new NativeList<byte>(cap, Allocator.Persistent);
            _turn = new NativeList<byte>(cap, Allocator.Persistent);
            _block = new NativeList<byte>(cap, Allocator.Persistent);
            _ring = new NativeList<byte>(cap, Allocator.Persistent);
            _feedN = new NativeList<byte>(cap, Allocator.Persistent);
            _next = new NativeList<int>(cap, Allocator.Persistent);
            _sink = new NativeList<int>(cap, Allocator.Persistent);
            _net = new NativeList<int>(cap, Allocator.Persistent);
            _flow = new NativeList<int>(cap, Allocator.Persistent);
            _feed = new NativeList<int>(cap * BeltConst.MaxFeeders, Allocator.Persistent);
            _pos = new NativeList<int>(cap * S, Allocator.Persistent);
            _delta = new NativeList<int>(cap * S, Allocator.Persistent);
            _flowB = new NativeList<int>(cap * BeltConst.Buckets, Allocator.Persistent);
            _item = new NativeList<ushort>(cap * S, Allocator.Persistent);
            _lookup = new NativeParallelHashMap<long, int>(cap, Allocator.Persistent);
            _ringCells = new NativeList<int>(16, Allocator.Persistent);
            _rings = new NativeList<int2>(4, Allocator.Persistent);
            _ringOf = new NativeList<int>(cap, Allocator.Persistent);
            _ringCount = new NativeList<int>(4, Allocator.Persistent);
            _tree = new NativeList<int>(cap, Allocator.Persistent);
            _nets = new NativeList<BeltNetAgg>(8, Allocator.Persistent);
            _ports = new NativeList<BeltPort>(8, Allocator.Persistent);
            _nodes = new NativeList<BeltNode>(8, Allocator.Persistent);
            _kind = new NativeList<byte>(cap, Allocator.Persistent);
            _nodeOf = new NativeList<int>(cap, Allocator.Persistent);
            _edge = new NativeList<byte>(cap, Allocator.Persistent);
            _counters = new NativeArray<long>(BeltCounters.Length, Allocator.Persistent);
            _hashOut = new NativeArray<ulong>(1, Allocator.Persistent);
            _renderCounts = new NativeArray<int>(2, Allocator.Persistent);
            _renderCells = new NativeArray<BeltInstance>(cap, Allocator.Persistent);
            _renderItems = new NativeArray<BeltInstance>(cap * 2, Allocator.Persistent);
        }

        // ── 基本属性 ───────────────────────────────────────────────────────────

        public BeltConfig Config => _config;
        public bool IsDisposed => _disposed;
        public int Spacing => _spacing;
        public int UnitsPerStep(int tier) => tier == 0 ? _v0 : tier == 1 ? _v1 : _v2;

        /// <summary>FG3-LOG-03：露天格在当前天气下的每步位移（没有减速时与 <see cref="UnitsPerStep"/> 相同）。</summary>
        public int SlowedUnitsPerStep(int tier) => tier == 0 ? _s0 : tier == 1 ? _s1 : _s2;

        /// <summary>FG3-LOG-03（FGR-LOG-028“沙暴期间露天传送带减速”）：当前露天减速百分比（0 = 不减速）。</summary>
        public int ExposedSlowPercent => _slowPct;

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-028）：设定露天传送带的减速百分比（0～<see cref="BeltConst.MaxSlowPercent"/>）。天气系统（FG7-ENV-03）在沙暴开始 / 结束时调用；
        /// 顶棚下的格（<see cref="SetCovered"/>）不受影响。整数定点：每步位移 = 等级位移 × (100 − 百分比) / 100（向下取整，至少 1），结果仍逐位确定。
        /// 派生量不进存档（天气状态自己进存档，读档后重新设定）。返回是否合法。
        /// </summary>
        public bool SetExposedSlowPercent(int percent)
        {
            if (percent < 0 || percent > BeltConst.MaxSlowPercent)
            {
                return false;
            }
            if (percent == _slowPct)
            {
                return true;
            }
            _slowPct = percent;
            _s0 = Slowed(_v0, percent);
            _s1 = Slowed(_v1, percent);
            _s2 = Slowed(_v2, percent);
            Revision++;
            return true;
        }

        private static int Slowed(int v, int percent) => Math.Max(1, (int)((long)v * (100 - percent) / 100));

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-028“在顶棚覆盖下不受影响”）：标记 (x, y) 在顶棚下 / 露天。按坐标记，与这一格现在有没有传送带无关
        /// （顶棚先建、带后铺也生效）。顶棚建筑（FG7）放下 / 拆除时调用；派生量不进存档，读档后由顶棚重新登记。
        /// </summary>
        public void SetCovered(int x, int y, bool covered)
        {
            long key = BeltDirs.Key(x, y);
            if (covered)
            {
                if (_coveredKeys.Count() + 1 > _coveredKeys.Capacity)
                {
                    _coveredKeys.Capacity = Math.Max(16, _coveredKeys.Capacity * 2);
                }
                _coveredKeys.Add(key);
            }
            else
            {
                _coveredKeys.Remove(key);
            }
            if (_lookup.TryGetValue(key, out int i))
            {
                _covered[i] = covered ? (byte)1 : (byte)0;
                Revision++;
            }
        }

        public bool IsCovered(int x, int y) => _coveredKeys.Contains(BeltDirs.Key(x, y));
        public int CellCount => _aliveCells;
        public long StepIndex => _counters[BeltCounters.StepIndex];
        public int NetworkCount
        {
            get
            {
                EnsureTopology();
                return _nets.Length;
            }
        }
        public int PortCount
        {
            get
            {
                int n = 0;
                for (int p = 0; p < _ports.Length; p++)
                {
                    if (_ports[p].Alive != 0)
                    {
                        n++;
                    }
                }
                return n;
            }
        }
        /// <summary>最近一步堵塞的格数（O(1)，热更层用来发“第一次堵塞”的引导钩子）。</summary>
        public long BlockedCells => _counters[BeltCounters.BlockedCells];
        /// <summary>环内“放不下只好原地不动”的次数（理论上恒为 0；自检断言它）。</summary>
        public long CapacityViolations => _counters[BeltCounters.CapacityViolations];
        public long Transfers => _counters[BeltCounters.Transfers];
        /// <summary>拓扑重建次数（诊断：编辑后第一次步进 / 查询才重建）。</summary>
        public int RebuildCount { get; private set; }
        /// <summary>状态版本：每一步、每次拓扑重建、每次物品增减都递增（渲染缓冲据此判断要不要重填）。</summary>
        public int Revision { get; private set; }
        public double LastStepMs { get; private set; }
        public double MaxStepMs { get; private set; }
        public double LastRebuildMs { get; private set; }
        public double LastRenderPrepMs { get; private set; }
        /// <summary>物品实例缓冲被重填的次数（诊断：远景下不应增加）。</summary>
        public int RenderItemFills { get; private set; }
        /// <summary>读档时因校验失败被丢弃的网络块数与其中声明的物品数（记入 <see cref="BeltLedger.Removed"/>）。</summary>
        public int CorruptChunksDropped { get; private set; }
        public long CorruptItemsDropped { get; private set; }

        /// <summary>带上的物品总数（O(1)：每步末由内核逐格重数，编辑 API 增减同步）。</summary>
        public int ItemCount => (int)_counters[BeltCounters.ItemsOnBelts];

        /// <summary>逐格重数带上的物品（O(N)，与账本计数器相互独立；守恒核对用，不要每帧调用）。</summary>
        public int CountItemsSlow()
        {
            int n = 0;
            for (int i = 0; i < _count.Length; i++)
            {
                if (_alive[i] != 0)
                {
                    n += _count[i];
                }
            }
            return n;
        }

        public BeltLedger Ledger => new BeltLedger
        {
            Emitted = _counters[BeltCounters.Emitted],
            Inserted = _counters[BeltCounters.Inserted],
            Delivered = _counters[BeltCounters.Delivered],
            Removed = _counters[BeltCounters.Removed],
            OnBelts = CountItemsSlow(),
        };

        public void ResetMaxStepMs() => MaxStepMs = 0;

        // ── 编辑 ───────────────────────────────────────────────────────────────

        public bool HasCell(int x, int y) => _lookup.TryGetValue(BeltDirs.Key(x, y), out _);

        public BeltResult AddCell(int x, int y, BeltDir dir, int tier)
        {
            if ((byte)dir > 3)
            {
                return BeltResult.InvalidDirection;
            }
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltResult.InvalidTier;
            }
            if (Math.Abs(x) > BeltConst.CoordLimit || Math.Abs(y) > BeltConst.CoordLimit)
            {
                return BeltResult.OutOfRange;
            }
            return AddCellRaw(x, y, dir, tier);
        }

        /// <summary>加一格（不查地面坐标范围：地下段的格用地下层坐标）。调用方已校验朝向与等级。</summary>
        private BeltResult AddCellRaw(int x, int y, BeltDir dir, int tier)
        {
            long key = BeltDirs.Key(x, y);
            if (_lookup.TryGetValue(key, out _))
            {
                return BeltResult.Occupied;
            }
            int idx = _x.Length;
            _x.Add(x);
            _y.Add(y);
            _dir.Add((byte)dir);
            _tier.Add((byte)tier);
            _alive.Add(1);
            _covered.Add(_coveredKeys.Contains(key) ? (byte)1 : (byte)0);
            _count.Add(0);
            _turn.Add(BeltConst.NoTurn);
            _block.Add(0);
            _ring.Add(0);
            _feedN.Add(0);
            _next.Add(-1);
            _sink.Add(-1);
            _net.Add(-1);
            _flow.Add(0);
            for (int k = 0; k < BeltConst.MaxFeeders; k++)
            {
                _feed.Add(-1);
            }
            for (int k = 0; k < S; k++)
            {
                _pos.Add(0);
                _delta.Add(0);
                _item.Add(0);
            }
            for (int k = 0; k < BeltConst.Buckets; k++)
            {
                _flowB.Add(0);
            }
            if (_lookup.Count() + 1 > _lookup.Capacity)
            {
                _lookup.Capacity = Math.Max(16, _lookup.Capacity * 2);
            }
            _lookup.TryAdd(key, idx);
            _aliveCells++;
            _dirty = true;
            return BeltResult.Ok;
        }

        /// <summary>拆掉一格：带上的物品按顺序（头一件在前）写进 <paramref name="removed"/>，计入账本“移出”（由调用方送进仓库 / 变成地面物）。</summary>
        public BeltResult RemoveCell(int x, int y, List<ushort> removed = null)
        {
            if (BeltDirs.IsUnderY(y))
            {
                return BeltResult.NotSupported; // FG3-LOG-04：地下段随入口 / 出口一起拆。
            }
            long key = BeltDirs.Key(x, y);
            if (!_lookup.TryGetValue(key, out int i))
            {
                return BeltResult.NotFound;
            }
            if (_nodeIndex.TryGetValue(key, out int ni))
            {
                byte kind = _nodes[ni].Kind;
                if (kind == (byte)BeltNodeKind.UndergroundIn || kind == (byte)BeltNodeKind.UndergroundOut)
                {
                    // FG3-LOG-04：拆地下传送带的任何一端 = 拆掉整条（入口、地下段、出口），物品按流向顺序交还。
                    return RemoveUnderground(ni, removed);
                }
                KillNode(ni);
            }
            RemoveRaw(key, i, removed);
            return BeltResult.Ok;
        }

        private void RemoveRaw(long key, int i, List<ushort> removed)
        {
            TakeItems(i, removed);
            _alive[i] = 0;
            _lookup.Remove(key);
            _aliveCells--;
            _dirty = true;
        }

        // ── FG3-LOG-04：物流节点（FGR-LOG-022 分流器 / 合流器；FGR-LOG-023 地下传送带）──────────────────────

        /// <summary>坐标 → 节点下标（O(1)；加节点时登记，拓扑重建会重排节点，之后整表重建）。</summary>
        private readonly Dictionary<long, int> _nodeIndex = new Dictionary<long, int>();

        private void IndexNodes()
        {
            _nodeIndex.Clear();
            for (int n = 0; n < _nodes.Length; n++)
            {
                if (_nodes[n].Alive != 0)
                {
                    _nodeIndex[BeltDirs.Key(_nodes[n].X, _nodes[n].Y)] = n;
                }
            }
        }

        private void AppendNode(BeltNode node)
        {
            node.Alive = 1;
            node.Cell = -1;
            node.OutL = -1;
            node.OutR = -1;
            _nodes.Add(node);
            _nodeIndex[BeltDirs.Key(node.X, node.Y)] = _nodes.Length - 1;
            _dirty = true;
        }

        private void KillNode(int ni)
        {
            BeltNode nd = _nodes[ni];
            nd.Alive = 0;
            _nodes[ni] = nd;
            _nodeIndex.Remove(BeltDirs.Key(nd.X, nd.Y));
            _dirty = true;
        }

        /// <summary>活着的节点数（分流器、合流器、地下入口、地下出口各算一个）。</summary>
        public int NodeCount => _nodeIndex.Count;

        /// <summary>最近一步停着的分流器数（O(1)，热更层发“第一次分流器堵住”的引导钩子）。</summary>
        public long BlockedSplitters => _counters[BeltCounters.NodesBlocked];

        /// <summary>(x, y) 这一格的种类（没有格时返回 false）。不触发拓扑重建。</summary>
        public bool TryGetKind(int x, int y, out BeltNodeKind kind)
        {
            long key = BeltDirs.Key(x, y);
            kind = BeltNodeKind.Belt;
            if (!_lookup.TryGetValue(key, out _))
            {
                return false;
            }
            if (BeltDirs.IsUnderY(y))
            {
                kind = BeltNodeKind.Underground;
            }
            else if (_nodeIndex.TryGetValue(key, out int ni))
            {
                kind = (BeltNodeKind)_nodes[ni].Kind;
            }
            return true;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-022）：放一个分流器 / 合流器（本身也是一格传送带，有朝向与等级；默认比例 1:1、不设优先口、两口都放全部物品）。
        /// 分流器从后方进、左右两口出；合流器从左右两侧进、从前方出。
        /// </summary>
        public BeltResult AddNode(int x, int y, BeltDir dir, int tier, BeltNodeKind kind)
        {
            if (kind != BeltNodeKind.Splitter && kind != BeltNodeKind.Merger)
            {
                return BeltResult.InvalidArgument;
            }
            BeltResult r = AddCell(x, y, dir, tier);
            if (r != BeltResult.Ok)
            {
                return r;
            }
            AppendNode(new BeltNode { X = x, Y = y, Kind = (byte)kind, RatioL = 1, RatioR = 1 });
            return BeltResult.Ok;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-023“地下传送带用来跨越其他传送带或建筑”）：从入口 (x, y) 朝 <paramref name="dir"/> 放一条地下传送带，出口在
        /// <paramref name="distance"/> 格外（跨度 = 距离 − 1 格；按等级的跨度上限由调用方读表校验，这里只查硬上限）。
        /// 入口与出口占地面两格；中间的地下段在地下层，地面上有什么都不影响；同一方向（南北 / 东西）的地下段不能重叠，南北与东西可以交叉。
        /// 全有或全无：任何一格放不下，一格都不加。
        /// </summary>
        public BeltResult AddUnderground(int x, int y, BeltDir dir, int tier, int distance)
        {
            if ((byte)dir > 3)
            {
                return BeltResult.InvalidDirection;
            }
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltResult.InvalidTier;
            }
            if (distance < 1)
            {
                return BeltResult.InvalidArgument;
            }
            if (distance > BeltConst.MaxUndergroundDistance)
            {
                return BeltResult.TooFar;
            }
            int d = (int)dir;
            int ex = x + BeltDirs.Dx(d) * distance;
            int ey = y + BeltDirs.Dy(d) * distance;
            if (Math.Abs(x) > BeltConst.CoordLimit || Math.Abs(y) > BeltConst.CoordLimit || Math.Abs(ex) > BeltConst.CoordLimit || Math.Abs(ey) > BeltConst.CoordLimit)
            {
                return BeltResult.OutOfRange;
            }
            if (_lookup.TryGetValue(BeltDirs.Key(x, y), out _) || _lookup.TryGetValue(BeltDirs.Key(ex, ey), out _))
            {
                return BeltResult.Occupied;
            }
            for (int s = 1; s < distance; s++)
            {
                if (_lookup.TryGetValue(BeltDirs.Key(x + BeltDirs.Dx(d) * s, BeltDirs.UnderY(y + BeltDirs.Dy(d) * s, d)), out _))
                {
                    return BeltResult.UndergroundOccupied;
                }
            }
            AddCellRaw(x, y, dir, tier);
            for (int s = 1; s < distance; s++)
            {
                AddCellRaw(x + BeltDirs.Dx(d) * s, BeltDirs.UnderY(y + BeltDirs.Dy(d) * s, d), dir, tier);
            }
            AddCellRaw(ex, ey, dir, tier);
            AppendNode(new BeltNode { X = x, Y = y, Kind = (byte)BeltNodeKind.UndergroundIn, Span = distance });
            AppendNode(new BeltNode { X = ex, Y = ey, Kind = (byte)BeltNodeKind.UndergroundOut, Span = distance });
            return BeltResult.Ok;
        }

        /// <summary>FG3-LOG-04：(x, y) 是一条地下传送带的入口或出口时，给出整条的入口、出口与距离。不触发拓扑重建。</summary>
        public bool TryGetUndergroundEnds(int x, int y, out int2 entrance, out int2 exit, out int distance)
        {
            entrance = default;
            exit = default;
            distance = 0;
            long key = BeltDirs.Key(x, y);
            if (!_nodeIndex.TryGetValue(key, out int ni) || !_lookup.TryGetValue(key, out int i))
            {
                return false;
            }
            BeltNode nd = _nodes[ni];
            if (nd.Kind != (byte)BeltNodeKind.UndergroundIn && nd.Kind != (byte)BeltNodeKind.UndergroundOut)
            {
                return false;
            }
            int d = _dir[i];
            distance = nd.Span;
            int sign = nd.Kind == (byte)BeltNodeKind.UndergroundIn ? 1 : -1;
            int ox = x + sign * BeltDirs.Dx(d) * nd.Span;
            int oy = y + sign * BeltDirs.Dy(d) * nd.Span;
            entrance = sign > 0 ? new int2(x, y) : new int2(ox, oy);
            exit = sign > 0 ? new int2(ox, oy) : new int2(x, y);
            return true;
        }

        private BeltResult RemoveUnderground(int ni, List<ushort> removed)
        {
            BeltNode nd = _nodes[ni];
            _lookup.TryGetValue(BeltDirs.Key(nd.X, nd.Y), out int end);
            int d = _dir[end];
            TryGetUndergroundEnds(nd.X, nd.Y, out int2 ent, out int2 exi, out int distance);
            // 按流向：入口 → 地下段 → 出口（物品交还顺序与流向一致；地下段缺格时跳过）。
            RemoveAt(ent.x, ent.y, removed);
            for (int s = 1; s < distance; s++)
            {
                RemoveAt(ent.x + BeltDirs.Dx(d) * s, BeltDirs.UnderY(ent.y + BeltDirs.Dy(d) * s, d), removed);
            }
            RemoveAt(exi.x, exi.y, removed);
            return BeltResult.Ok;
        }

        private void RemoveAt(int x, int y, List<ushort> removed)
        {
            long key = BeltDirs.Key(x, y);
            if (_nodeIndex.TryGetValue(key, out int ni))
            {
                KillNode(ni);
            }
            if (_lookup.TryGetValue(key, out int i))
            {
                RemoveRaw(key, i, removed);
            }
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-022）：设分流器——比例左 : 右（各 1～<see cref="BeltConst.RatioMax"/>，默认 1:1）、优先输出口（不设 / 左 / 右）、
        /// 每口过滤（<see cref="BeltConst.FilterAny"/> 全部 / <see cref="BeltConst.FilterNone"/> 不出 / 物品编号 = 只放这种）。立刻生效；改比例时本轮计数清零。
        /// </summary>
        public BeltResult SetSplitter(int x, int y, int ratioL, int ratioR, BeltSide priority, ushort filterL, ushort filterR)
        {
            if (ratioL < 1 || ratioR < 1 || ratioL > BeltConst.RatioMax || ratioR > BeltConst.RatioMax || (byte)priority > 2)
            {
                return BeltResult.InvalidArgument;
            }
            if (!_nodeIndex.TryGetValue(BeltDirs.Key(x, y), out int ni))
            {
                return BeltResult.NotFound;
            }
            BeltNode nd = _nodes[ni];
            if (nd.Kind != (byte)BeltNodeKind.Splitter)
            {
                return BeltResult.NotSupported;
            }
            if (nd.RatioL != ratioL || nd.RatioR != ratioR)
            {
                nd.CntL = 0;
                nd.CntR = 0;
            }
            nd.RatioL = (byte)ratioL;
            nd.RatioR = (byte)ratioR;
            nd.PrioOut = (byte)priority;
            nd.FilterL = filterL;
            nd.FilterR = filterR;
            _nodes[ni] = nd;
            Revision++;
            return BeltResult.Ok;
        }

        /// <summary>FG3-LOG-04（FGR-LOG-022“合流器可以设置优先输入口”）：不设 = 两路交替；左 / 右 = 那一路有货时先走它。</summary>
        public BeltResult SetMergerPriority(int x, int y, BeltSide priority)
        {
            if ((byte)priority > 2)
            {
                return BeltResult.InvalidArgument;
            }
            if (!_nodeIndex.TryGetValue(BeltDirs.Key(x, y), out int ni))
            {
                return BeltResult.NotFound;
            }
            BeltNode nd = _nodes[ni];
            if (nd.Kind != (byte)BeltNodeKind.Merger)
            {
                return BeltResult.NotSupported;
            }
            nd.PrioIn = (byte)priority;
            _nodes[ni] = nd;
            Revision++;
            return BeltResult.Ok;
        }

        /// <summary>
        /// FG3-LOG-04：一个节点的读数（分流器 / 合流器 / 地下入口 / 地下出口；不是节点时返回 false）。
        /// O(1)（地下传送带另加 O(距离) 数地下段的件数），热更层悬停与节点面板用。
        /// </summary>
        public bool TryGetNodeInfo(int x, int y, out BeltNodeInfo info)
        {
            EnsureTopology();
            info = default;
            long key = BeltDirs.Key(x, y);
            if (!_nodeIndex.TryGetValue(key, out int ni) || !_lookup.TryGetValue(key, out int c))
            {
                return false;
            }
            BeltNode nd = _nodes[ni];
            int d = _dir[c];
            int completed = (int)Math.Min(_counters[BeltCounters.CompletedBuckets], BeltConst.Buckets);
            info = new BeltNodeInfo
            {
                Kind = (BeltNodeKind)nd.Kind,
                X = x,
                Y = y,
                Dir = (BeltDir)d,
                Tier = _tier[c],
                Count = _count[c],
                Block = (BeltBlock)_block[c],
                HeadItem = _count[c] > 0 ? _item[c * S] : (ushort)0,
                WindowSeconds = completed * _config.BucketSteps / (float)_config.StepHz,
            };
            switch ((BeltNodeKind)nd.Kind)
            {
                case BeltNodeKind.Splitter:
                {
                    int l = BeltDirs.Left(d);
                    int r = BeltDirs.Right(d);
                    info.RatioL = nd.RatioL;
                    info.RatioR = nd.RatioR;
                    info.PriorityOut = (BeltSide)nd.PrioOut;
                    info.FilterL = nd.FilterL;
                    info.FilterR = nd.FilterR;
                    info.OutLConnected = nd.OutL >= 0;
                    info.OutRConnected = nd.OutR >= 0;
                    info.OutLX = x + BeltDirs.Dx(l);
                    info.OutLY = y + BeltDirs.Dy(l);
                    info.OutRX = x + BeltDirs.Dx(r);
                    info.OutRY = y + BeltDirs.Dy(r);
                    info.OutLState = (BeltOutletState)nd.OutLState;
                    info.OutRState = (BeltOutletState)nd.OutRState;
                    info.SentL = nd.SentL;
                    info.SentR = nd.SentR;
                    for (int k = 0; k < completed; k++)
                    {
                        info.SentLInWindow += k == 0 ? nd.WL0 : k == 1 ? nd.WL1 : k == 2 ? nd.WL2 : nd.WL3;
                        info.SentRInWindow += k == 0 ? nd.WR0 : k == 1 ? nd.WR1 : k == 2 ? nd.WR2 : nd.WR3;
                    }
                    info.InputConnected = _feedN[c] > 0;
                    break;
                }
                case BeltNodeKind.Merger:
                {
                    info.PriorityIn = (BeltSide)nd.PrioIn;
                    int b = c * BeltConst.MaxFeeders;
                    for (int k = 0; k < _feedN[c]; k++)
                    {
                        int f = _feed[b + k];
                        int side = SideOf(c, f);
                        info.InLConnected |= side == BeltDirs.Left(d);
                        info.InRConnected |= side == BeltDirs.Right(d);
                    }
                    break;
                }
                default:
                {
                    // 地下传送带：入口与出口给出整条的数据。
                    TryGetUndergroundEnds(x, y, out int2 ent, out int2 exi, out int distance);
                    info.Distance = distance;
                    info.EntranceX = ent.x;
                    info.EntranceY = ent.y;
                    info.ExitX = exi.x;
                    info.ExitY = exi.y;
                    info.Capacity = (distance + 1) * _config.SlotsPerCell;
                    bool intact = true;
                    int inside = 0;
                    for (int s = 0; s <= distance; s++)
                    {
                        int cx = ent.x + BeltDirs.Dx(d) * s;
                        int cy = ent.y + BeltDirs.Dy(d) * s;
                        if (s > 0 && s < distance)
                        {
                            cy = BeltDirs.UnderY(cy, d);
                        }
                        if (_lookup.TryGetValue(BeltDirs.Key(cx, cy), out int ci))
                        {
                            inside += _count[ci];
                        }
                        else
                        {
                            intact = false;
                        }
                    }
                    info.Intact = intact;
                    info.ItemsInside = inside;
                    if (nd.Kind == (byte)BeltNodeKind.UndergroundIn)
                    {
                        info.InputConnected = _feedN[c] > 0;
                    }
                    break;
                }
            }
            return true;
        }

        private int SideOf(int target, int feeder)
        {
            int dx = _x[feeder] - _x[target];
            int dy = _y[feeder] - _y[target];
            return dy > 0 ? 0 : dx > 0 ? 1 : dy < 0 ? 2 : 3;
        }

        /// <summary>清带（FGR-LOG-026 的内核部分）：清空一格上的物品，写进 <paramref name="removed"/>，计入“移出”。</summary>
        public BeltResult ClearCell(int x, int y, List<ushort> removed = null)
        {
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            TakeItems(i, removed);
            return BeltResult.Ok;
        }

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-026 清带工具）：清空一批格上的物品，按物品种类汇总进 <paramref name="counts"/>（不存在的格跳过），全部计入“移出”。
        /// 返回清掉的件数。O(格数)，只在玩家操作时调用。
        /// </summary>
        public int ClearCells(IReadOnlyList<int2> cells, Dictionary<ushort, int> counts)
        {
            int total = 0;
            if (cells == null)
            {
                return 0;
            }
            foreach (int2 c in cells)
            {
                if (!_lookup.TryGetValue(BeltDirs.Key(c.x, c.y), out int i))
                {
                    continue;
                }
                int cnt = _count[i];
                if (cnt == 0)
                {
                    continue;
                }
                if (counts != null)
                {
                    for (int k = 0; k < cnt; k++)
                    {
                        ushort it = _item[i * S + k];
                        counts[it] = counts.TryGetValue(it, out int n) ? n + 1 : 1;
                    }
                }
                TakeItems(i, null);
                total += cnt;
            }
            return total;
        }

        /// <summary>FG3-LOG-03：一批格上现有的物品按种类汇总（清带前的预览与确认框用；不改状态）。返回件数。</summary>
        public int CountItems(IReadOnlyList<int2> cells, Dictionary<ushort, int> counts)
        {
            int total = 0;
            if (cells == null)
            {
                return 0;
            }
            foreach (int2 c in cells)
            {
                if (!_lookup.TryGetValue(BeltDirs.Key(c.x, c.y), out int i))
                {
                    continue;
                }
                int cnt = _count[i];
                for (int k = 0; k < cnt; k++)
                {
                    ushort it = _item[i * S + k];
                    if (counts != null)
                    {
                        counts[it] = counts.TryGetValue(it, out int n) ? n + 1 : 1;
                    }
                }
                total += cnt;
            }
            return total;
        }

        /// <summary>
        /// FG4-ECO-01（FG04 第 4 节“悬停物品图标显示总库存、各仓库分布”；FG-GAP-090“在途按物品种类”）：带上（地面格）的物品按种类计数，
        /// 累加进 <paramref name="countsByItem"/>（下标 = 物品编号；编号超出数组长度的计入最后一格）。<paramref name="network"/> &lt; 0 = 全部网络。
        /// 逐格循环留在 AOT（热更层不逐格）；O(格数 × 每格件数)，只在悬停缓存刷新 / 描述“等待材料”时调用，不按帧。返回计数件数。
        /// </summary>
        public int CountItemsByType(int network, int[] countsByItem)
        {
            if (countsByItem == null || countsByItem.Length == 0)
            {
                return 0;
            }
            if (network >= 0)
            {
                EnsureTopology();
            }
            int last = countsByItem.Length - 1;
            int total = 0;
            for (int i = 0; i < _count.Length; i++)
            {
                if (_alive[i] == 0 || (network >= 0 && _net[i] != network))
                {
                    continue;
                }
                int cnt = _count[i];
                for (int k = 0; k < cnt; k++)
                {
                    ushort it = _item[i * S + k];
                    countsByItem[it < last ? it : last]++;
                }
                total += cnt;
            }
            return total;
        }

        private void TakeItems(int i, List<ushort> removed)
        {
            int cnt = _count[i];
            for (int k = 0; k < cnt; k++)
            {
                removed?.Add(_item[i * S + k]);
            }
            _count[i] = 0;
            _counters[BeltCounters.Removed] += cnt;
            _counters[BeltCounters.ItemsOnBelts] -= cnt;
            Revision++;
        }

        /// <summary>原地改方向（FGR-LOG-020“可以原地反转方向”）：物品位置镜像（反转时头尾对调），不丢、不增。</summary>
        public BeltResult SetDirection(int x, int y, BeltDir dir)
        {
            if ((byte)dir > 3)
            {
                return BeltResult.InvalidDirection;
            }
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            // FG3-LOG-04：地下传送带（入口、出口、地下段）不能原地改方向（入口与出口的位置由方向决定）——拆掉重铺。分流器 / 合流器可以。
            if (TryGetKind(x, y, out BeltNodeKind kind) && (kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut || kind == BeltNodeKind.Underground))
            {
                return BeltResult.NotSupported;
            }
            if (_dir[i] == (byte)dir)
            {
                return BeltResult.Ok;
            }
            if (BeltDirs.Opposite(_dir[i]) == (byte)dir)
            {
                int cnt = _count[i];
                int b = i * S;
                var tp = new int[cnt];
                var ti = new ushort[cnt];
                for (int k = 0; k < cnt; k++)
                {
                    tp[k] = BeltConst.CellLength - 1 - _pos[b + k];
                    ti[k] = _item[b + k];
                }
                for (int k = 0; k < cnt; k++)
                {
                    _pos[b + k] = tp[cnt - 1 - k];
                    _item[b + k] = ti[cnt - 1 - k];
                    _delta[b + k] = 0;
                }
            }
            _dir[i] = (byte)dir;
            _turn[i] = BeltConst.NoTurn;
            _dirty = true;
            return BeltResult.Ok;
        }

        /// <summary>原地改等级（升级 / 降级）：只改速度，不影响拓扑。</summary>
        public BeltResult SetTier(int x, int y, int tier)
        {
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltResult.InvalidTier;
            }
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            if (TryGetKind(x, y, out BeltNodeKind kind) && kind != BeltNodeKind.Belt)
            {
                if (kind != BeltNodeKind.UndergroundIn && kind != BeltNodeKind.UndergroundOut)
                {
                    return BeltResult.NotSupported; // 分流器 / 合流器只有一种；地下段随两端一起改。
                }
                // FG3-LOG-04：地下传送带整条一起改等级（按等级的跨度上限由调用方先校验）。
                TryGetUndergroundEnds(x, y, out int2 ent, out int2 exi, out int distance);
                int d = _dir[i];
                for (int s = 0; s <= distance; s++)
                {
                    int cx = ent.x + BeltDirs.Dx(d) * s;
                    int cy = ent.y + BeltDirs.Dy(d) * s;
                    if (s > 0 && s < distance)
                    {
                        cy = BeltDirs.UnderY(cy, d);
                    }
                    if (_lookup.TryGetValue(BeltDirs.Key(cx, cy), out int ci))
                    {
                        _tier[ci] = (byte)tier;
                    }
                }
                Revision++;
                return BeltResult.Ok;
            }
            _tier[i] = (byte)tier;
            Revision++;
            return BeltResult.Ok;
        }

        /// <summary>在一格的指定位置放一件物品（测试 / 清带回填 / 建筑手工放料用）。位置必须与同格已有物品保持间距。计入“放入”。</summary>
        public BeltResult InsertItemAt(int x, int y, int pos, ushort item)
        {
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            if (pos < 0 || pos >= BeltConst.CellLength)
            {
                return BeltResult.InvalidArgument;
            }
            int cnt = _count[i];
            if (cnt >= _config.SlotsPerCell)
            {
                return BeltResult.NoSpace;
            }
            int b = i * S;
            int at = cnt;
            for (int k = 0; k < cnt; k++)
            {
                if (Math.Abs(_pos[b + k] - pos) < _spacing)
                {
                    return BeltResult.NoSpace;
                }
                if (_pos[b + k] < pos && at == cnt)
                {
                    at = k;
                }
            }
            for (int k = cnt; k > at; k--)
            {
                _pos[b + k] = _pos[b + k - 1];
                _item[b + k] = _item[b + k - 1];
                _delta[b + k] = _delta[b + k - 1];
            }
            _pos[b + at] = pos;
            _item[b + at] = item;
            _delta[b + at] = 0;
            _count[i] = (byte)(cnt + 1);
            _counters[BeltCounters.Inserted]++;
            _counters[BeltCounters.ItemsOnBelts]++;
            Revision++;
            return BeltResult.Ok;
        }

        /// <summary>在一格入口放一件（与输出端口同一条规则：入口要空出一个间距）。</summary>
        public BeltResult InsertItem(int x, int y, ushort item)
        {
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            int cnt = _count[i];
            if (cnt >= _config.SlotsPerCell || (cnt > 0 && _pos[i * S + cnt - 1] < _spacing))
            {
                return BeltResult.NoSpace;
            }
            return InsertItemAt(x, y, 0, item);
        }

        // ── 端口 ───────────────────────────────────────────────────────────────

        /// <summary>输出端口：每 <paramref name="intervalSteps"/> 步往 (x,y) 这一格传送带的入口推一件 <paramref name="item"/>；
        /// <paramref name="pending"/> = -1 表示无限供货（否则推完就停，可用 <see cref="AddSourceItems"/> 补货）。</summary>
        public BeltResult AddSource(int id, int x, int y, ushort item, int intervalSteps, int pending, int owner = 0, byte face = BeltConst.AnyFace)
        {
            if (intervalSteps < 1 || pending < -1 || (face > 3 && face != BeltConst.AnyFace))
            {
                return BeltResult.InvalidArgument;
            }
            if (FindPort(id) >= 0)
            {
                return BeltResult.PortOccupied;
            }
            for (int p = 0; p < _ports.Length; p++)
            {
                BeltPort o = _ports[p];
                if (o.Alive != 0 && o.Kind == (byte)BeltPortKind.Source && o.X == x && o.Y == y)
                {
                    return BeltResult.PortOccupied;
                }
            }
            _ports.Add(new BeltPort
            {
                Id = id, Kind = (byte)BeltPortKind.Source, Alive = 1, X = x, Y = y, Owner = owner, Item = item, Face = face, Accept = BeltConst.AcceptAny,
                Interval = intervalSteps, Phase = 0, Pending = pending, Cell = -1, Network = -1,
            });
            _portIndex[id] = _ports.Length - 1;
            _dirty = true;
            return BeltResult.Ok;
        }

        /// <summary>输入端口：建筑格 (x,y)；末端朝向这一格的传送带把物品送进来。<paramref name="bufferCap"/> = -1 表示收下即消耗（不限），
        /// 否则缓存满了就不收（上游堵塞，原因“下游 X 已满”）；<paramref name="consumeIntervalSteps"/> > 0 时每隔这么多步自动消耗一件。
        /// FG3-LOG-03：<paramref name="face"/> = 端口朝外的方向（只接从这一侧正对着它的带）；<paramref name="accept"/> = 只收哪种物品
        /// （<see cref="BeltConst.AcceptAny"/> 任何 / <see cref="BeltConst.AcceptNone"/> 都不收），不收的物品让带停下（原因 <see cref="BeltBlock.SinkRejects"/>）。</summary>
        public BeltResult AddSink(int id, int x, int y, int bufferCap, int consumeIntervalSteps, int owner = 0, byte face = BeltConst.AnyFace,
            ushort accept = BeltConst.AcceptAny)
        {
            if (bufferCap < -1 || consumeIntervalSteps < 0 || (face > 3 && face != BeltConst.AnyFace))
            {
                return BeltResult.InvalidArgument;
            }
            if (FindPort(id) >= 0)
            {
                return BeltResult.PortOccupied;
            }
            for (int p = 0; p < _ports.Length; p++)
            {
                BeltPort o = _ports[p];
                if (o.Alive != 0 && o.Kind == (byte)BeltPortKind.Sink && o.X == x && o.Y == y)
                {
                    return BeltResult.PortOccupied;
                }
            }
            _ports.Add(new BeltPort
            {
                Id = id, Kind = (byte)BeltPortKind.Sink, Alive = 1, X = x, Y = y, Owner = owner, Face = face, Accept = accept,
                Interval = consumeIntervalSteps, Cap = bufferCap, Cell = -1, Network = -1,
            });
            _portIndex[id] = _ports.Length - 1;
            _dirty = true;
            return BeltResult.Ok;
        }

        public BeltResult RemovePort(int id) => RemovePort(id, out _, out _);

        /// <summary>
        /// FG3-LOG-03：删掉一个端口，并交还它手里的物品——输出端口还没推上带的待推数（<paramref name="pending"/>，无限供货时为 0）、
        /// 输入端口缓存里还没被建筑取走的件数（<paramref name="buffered"/>）。调用方把它们送回仓库（建筑被拆 / 停用 / 转向时，物品不凭空消失）。
        /// </summary>
        public BeltResult RemovePort(int id, out int pending, out int buffered)
        {
            pending = 0;
            buffered = 0;
            int p = FindPort(id);
            if (p < 0)
            {
                return BeltResult.PortNotFound;
            }
            BeltPort bp = _ports[p];
            pending = bp.Kind == (byte)BeltPortKind.Source ? Math.Max(0, bp.Pending) : 0;
            buffered = bp.Kind == (byte)BeltPortKind.Sink ? Math.Max(0, bp.Buffered) : 0;
            bp.Alive = 0;
            bp.Pending = 0;
            bp.Buffered = 0;
            _ports[p] = bp;
            _dirty = true;
            return BeltResult.Ok;
        }

        /// <summary>FG3-LOG-03（FGR-LOG-021 仓库输出过滤）：输出端口改推另一种物品。调用方先用 <see cref="TakeSourcePending"/> 收回旧物品的待推数。</summary>
        public BeltResult SetSourceItem(int id, ushort item)
        {
            int p = FindPort(id);
            if (p < 0 || _ports[p].Kind != (byte)BeltPortKind.Source)
            {
                return BeltResult.PortNotFound;
            }
            BeltPort bp = _ports[p];
            bp.Item = item;
            _ports[p] = bp;
            return BeltResult.Ok;
        }

        /// <summary>FG3-LOG-03：收回输出端口还没推上带的待推数（返回件数，端口的待推数归 0；无限供货的端口返回 0、不变）。</summary>
        public int TakeSourcePending(int id)
        {
            int p = FindPort(id);
            if (p < 0 || _ports[p].Kind != (byte)BeltPortKind.Source || _ports[p].Pending <= 0)
            {
                return 0;
            }
            BeltPort bp = _ports[p];
            int n = bp.Pending;
            bp.Pending = 0;
            _ports[p] = bp;
            return n;
        }

        /// <summary>FG3-LOG-03：改输入端口收什么（<see cref="BeltConst.AcceptAny"/> / <see cref="BeltConst.AcceptNone"/> / 物品编号）。</summary>
        public BeltResult SetSinkAccept(int id, ushort accept)
        {
            int p = FindPort(id);
            if (p < 0 || _ports[p].Kind != (byte)BeltPortKind.Sink)
            {
                return BeltResult.PortNotFound;
            }
            BeltPort bp = _ports[p];
            // FG4-ECO-01：从“只收一种”改成“任何一种（一次一种）”时，缓存里已有的件就是原来那一种（旧存档的仓库输入口缓存着废料）。
            bool oneKindNow = bp.Accept == BeltConst.AcceptAnyOneKind || BeltConst.IsSetAccept(bp.Accept);
            if ((accept == BeltConst.AcceptAnyOneKind || BeltConst.IsSetAccept(accept)) && !oneKindNow && bp.Buffered > 0
                && bp.Accept != BeltConst.AcceptAny && bp.Accept != BeltConst.AcceptNone)
            {
                bp.Item = bp.Accept;
            }
            bp.Accept = accept;
            _ports[p] = bp;
            return BeltResult.Ok;
        }

        public BeltResult AddSourceItems(int id, int count)
        {
            int p = FindPort(id);
            if (p < 0 || _ports[p].Kind != (byte)BeltPortKind.Source)
            {
                return BeltResult.PortNotFound;
            }
            if (count < 0)
            {
                return BeltResult.InvalidArgument;
            }
            BeltPort bp = _ports[p];
            if (bp.Pending >= 0)
            {
                bp.Pending += count;
            }
            _ports[p] = bp;
            return BeltResult.Ok;
        }

        /// <summary>建筑从输入端口缓存取料（返回实际取到的数量）。</summary>
        public int TakeFromSink(int id, int count)
        {
            int p = FindPort(id);
            if (p < 0 || _ports[p].Kind != (byte)BeltPortKind.Sink || count <= 0)
            {
                return 0;
            }
            BeltPort bp = _ports[p];
            int n = Math.Min(count, Math.Max(0, bp.Buffered));
            bp.Buffered -= n;
            bp.Consumed += n;
            _ports[p] = bp;
            return n;
        }

        /// <summary>FG3-LOG-03：端口号 → 下标（O(1)；热更层每个内核步给每个建筑端口补货 / 取料，线性查找会变成 O(端口数²)）。
        /// 追加端口时登记；拓扑重建会重排端口，之后整表重建。</summary>
        private readonly Dictionary<int, int> _portIndex = new Dictionary<int, int>();

        private int FindPort(int id)
        {
            if (_portIndex.TryGetValue(id, out int p) && p < _ports.Length && _ports[p].Alive != 0 && _ports[p].Id == id)
            {
                return p;
            }
            return -1;
        }

        private void IndexPorts()
        {
            _portIndex.Clear();
            for (int p = 0; p < _ports.Length; p++)
            {
                if (_ports[p].Alive != 0)
                {
                    _portIndex[_ports[p].Id] = p;
                }
            }
        }

        /// <summary>FG4-ECO-01：端口的待推数（输出口）与缓存数（输入口），并给出端口手里物品的种类（输出口 = 要推的种类；按种类收货的输入口 = 缓存里那一种）。O(1)，不触发拓扑重建。</summary>
        public bool TryGetPortCounts(int id, out int pending, out int buffered, out ushort item)
        {
            int p = FindPort(id);
            if (p < 0)
            {
                pending = 0;
                buffered = 0;
                item = 0;
                return false;
            }
            pending = _ports[p].Pending;
            buffered = _ports[p].Buffered;
            item = _ports[p].Item;
            return true;
        }

        /// <summary>FG3-LOG-03：端口的待推数（输出口）与缓存数（输入口），O(1)，不触发拓扑重建（热更层每个内核步补货 / 取料用）。</summary>
        public bool TryGetPortCounts(int id, out int pending, out int buffered)
        {
            int p = FindPort(id);
            if (p < 0)
            {
                pending = 0;
                buffered = 0;
                return false;
            }
            pending = _ports[p].Pending;
            buffered = _ports[p].Buffered;
            return true;
        }

        /// <summary>
        /// FG3-LOG-03（清带拖框）：闭区间框里已建成的传送带格（按 (y, x) 升序）。框面积不超过格数时逐格查表，否则扫全部格再筛——
        /// 两者取小，O(min(框面积, 格数))，逐格循环留在 AOT（热更层不逐格）。只在玩家拖框时调用。
        /// </summary>
        public void CollectCellsInBox(int minX, int minY, int maxX, int maxY, List<int2> into) => CollectCellsInBox(minX, minY, maxX, maxY, into, out _);

        /// <summary>
        /// 同上；FG3-LOG-04：框下面的地下段也算进来（清带时地下的物品一并清走），<paramref name="surfaceCells"/> 是其中地面上的格数（界面显示“N 格传送带”用）。
        /// </summary>
        public void CollectCellsInBox(int minX, int minY, int maxX, int maxY, List<int2> into, out int surfaceCells)
        {
            into.Clear();
            surfaceCells = 0;
            if (maxX < minX || maxY < minY)
            {
                return;
            }
            long area = (long)(maxX - minX + 1) * (maxY - minY + 1);
            if (area <= _aliveCells)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (_lookup.TryGetValue(BeltDirs.Key(x, y), out int i) && _alive[i] != 0)
                        {
                            into.Add(new int2(x, y));
                            surfaceCells++;
                        }
                    }
                }
                // 地下段（南北层、东西层）：只在有节点时才查，普通场景没有额外开销。
                if (_nodeIndex.Count > 0)
                {
                    for (int layer = 0; layer < 2; layer++)
                    {
                        for (int y = minY; y <= maxY; y++)
                        {
                            int uy = BeltDirs.UnderY(y, layer);
                            for (int x = minX; x <= maxX; x++)
                            {
                                if (_lookup.TryGetValue(BeltDirs.Key(x, uy), out int i) && _alive[i] != 0)
                                {
                                    into.Add(new int2(x, uy));
                                }
                            }
                        }
                    }
                }
                return;
            }
            EnsureTopology();
            for (int i = 0; i < _x.Length; i++)
            {
                int x = _x[i];
                int y = BeltDirs.SurfaceY(_y[i]);
                if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                {
                    into.Add(new int2(x, _y[i]));
                    if (!BeltDirs.IsUnderY(_y[i]))
                    {
                        surfaceCells++;
                    }
                }
            }
        }

        /// <summary>FG3-LOG-03（清带点选一条带）：某一网络的全部格坐标（规范顺序；FG3-LOG-04：含地下段）。O(格数)，只在玩家操作时调用。</summary>
        public void CollectNetworkCells(int network, List<int2> into) => CollectNetworkCells(network, into, out _);

        /// <summary>同上，<paramref name="surfaceCells"/> 是其中地面上的格数（不含地下段）。</summary>
        public void CollectNetworkCells(int network, List<int2> into, out int surfaceCells)
        {
            EnsureTopology();
            into.Clear();
            surfaceCells = 0;
            if (network < 0)
            {
                return;
            }
            for (int i = 0; i < _x.Length; i++)
            {
                if (_net[i] == network)
                {
                    into.Add(new int2(_x[i], _y[i]));
                    if (_kind[i] != (byte)BeltNodeKind.Underground)
                    {
                        surfaceCells++;
                    }
                }
            }
        }

        // ── 步进 ───────────────────────────────────────────────────────────────

        /// <summary>编辑之后第一次步进 / 查询前重建拓扑（Burst，O(N log N)）。</summary>
        public void EnsureTopology()
        {
            ThrowIfDisposed();
            if (!_dirty)
            {
                return;
            }
            long t0 = _watch.ElapsedTicks;
            _watch.Start();
            int need = Math.Max(16, _aliveCells * 2);
            if (_lookup.Capacity < need)
            {
                _lookup.Capacity = need;
            }
            new JobBeltRebuild
            {
                X = _x, Y = _y, Dir = _dir, Tier = _tier, Alive = _alive, Count = _count, Turn = _turn, Block = _block,
                Ring = _ring, FeedN = _feedN, Next = _next, Sink = _sink, Net = _net, Flow = _flow, Feed = _feed,
                Pos = _pos, Delta = _delta, FlowB = _flowB, Item = _item, Covered = _covered, CoveredKeys = _coveredKeys, Lookup = _lookup, RingCells = _ringCells,
                Rings = _rings, RingOf = _ringOf, RingCount = _ringCount, Tree = _tree, Nets = _nets, Ports = _ports,
                Nodes = _nodes, Kind = _kind, NodeOf = _nodeOf, Edge = _edge,
            }.Run();
            _watch.Stop();
            LastRebuildMs = (_watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            _aliveCells = _x.Length;
            _dirty = false;
            RebuildCount++;
            IndexPorts();
            IndexNodes();
            // FG3-LOG-09：全局堵塞格数与网络汇总同一口径（O(网络数)）。
            long blocked = 0;
            for (int k = 0; k < _nets.Length; k++)
            {
                blocked += _nets[k].Blocked;
            }
            _counters[BeltCounters.BlockedCells] = blocked;
            Revision++;
        }

        /// <summary>一个固定步（1 / StepHz 游戏秒）。</summary>
        public void Step()
        {
            EnsureTopology();
            long t0 = _watch.ElapsedTicks;
            _watch.Start();
            new JobBeltStep
            {
                CL = BeltConst.CellLength, Spacing = _spacing, V0 = _v0, V1 = _v1, V2 = _v2, S0 = _s0, S1 = _s1, S2 = _s2, SlowOn = _slowPct > 0 ? 1 : 0,
                BucketSteps = _config.BucketSteps, AcceptSetMask = _config.AcceptSetMask, AcceptSet2Mask = _config.AcceptSet2Mask,
                SlotsPerCell = _config.SlotsPerCell,
                X = _x.AsArray(), Y = _y.AsArray(), Dir = _dir.AsArray(), Ring = _ring.AsArray(), Tier = _tier.AsArray(), Covered = _covered.AsArray(),
                FeedN = _feedN.AsArray(), Feed = _feed.AsArray(),
                Next = _next.AsArray(), Sink = _sink.AsArray(), Net = _net.AsArray(), RingCells = _ringCells.AsArray(),
                Rings = _rings.AsArray(), RingOf = _ringOf.AsArray(), RingCount = _ringCount.AsArray(), Tree = _tree.AsArray(),
                Count = _count.AsArray(), Turn = _turn.AsArray(),
                Block = _block.AsArray(), Flow = _flow.AsArray(), FlowB = _flowB.AsArray(), Pos = _pos.AsArray(),
                Delta = _delta.AsArray(), Item = _item.AsArray(), Ports = _ports.AsArray(), Nets = _nets.AsArray(),
                Counters = _counters,
                Kind = _kind.AsArray(), NodeOf = _nodeOf.AsArray(), Edge = _edge.AsArray(), Nodes = _nodes.AsArray(),
            }.Run();
            _watch.Stop();
            LastStepMs = (_watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
            Revision++;
        }

        public void StepMany(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Step();
            }
        }

        // ── 查询（热更层只读）──────────────────────────────────────────────────

        public bool TryGetCellInfo(int x, int y, out BeltCellInfo info)
        {
            EnsureTopology();
            info = default;
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return false;
            }
            info = CellInfoAt(i);
            return true;
        }

        /// <summary>第 i 格（规范下标，拓扑已整理）的读数。<see cref="TryGetCellInfo"/> 与堵塞追溯（<see cref="TryTraceBlock"/>）共用。</summary>
        private BeltCellInfo CellInfoAt(int i)
        {
            int x = _x[i];
            int y = _y[i];
            BeltCellInfo info;
            int cnt = _count[i];
            int b = i * S;
            int nx = _next[i];
            int completed = (int)Math.Min(_counters[BeltCounters.CompletedBuckets], BeltConst.Buckets);
            int passed = 0;
            for (int k = 0; k < completed; k++)
            {
                passed += _flowB[i * BeltConst.Buckets + k];
            }
            info = new BeltCellInfo
            {
                X = x,
                Y = y,
                Dir = (BeltDir)_dir[i],
                Tier = _tier[i],
                Count = cnt,
                Block = (BeltBlock)_block[i],
                Network = _net[i],
                HasNext = nx >= 0,
                NextX = nx >= 0 ? _x[nx] : 0,
                NextY = nx >= 0 ? _y[nx] : 0,
                SinkPortId = _sink[i] >= 0 ? _ports[_sink[i]].Id : -1,
                Feeders = _feedN[i],
                InLoop = _ring[i] != 0,
                PassedInWindow = passed,
                WindowSeconds = completed * _config.BucketSteps / (float)_config.StepHz,
                RatedItemsPerMinute = EffectiveItemsPerMinute(_tier[i], _covered[i] != 0),
                TierItemsPerMinute = _config.ItemsPerMinute(_tier[i]),
                Covered = _covered[i] != 0,
                SlowPercent = _covered[i] != 0 ? 0 : _slowPct,
                Item0 = cnt > 0 ? _item[b] : (ushort)0,
                Item1 = cnt > 1 ? _item[b + 1] : (ushort)0,
                Item2 = cnt > 2 ? _item[b + 2] : (ushort)0,
                Item3 = cnt > 3 ? _item[b + 3] : (ushort)0,
                Pos0 = cnt > 0 ? _pos[b] : -1,
                Pos1 = cnt > 1 ? _pos[b + 1] : -1,
                Pos2 = cnt > 2 ? _pos[b + 2] : -1,
                Pos3 = cnt > 3 ? _pos[b + 3] : -1,
                Kind = (BeltNodeKind)_kind[i],
                Turn = _turn[i],
            };
            // FG3-LOG-04：下游是地下段（地下传送带入口）时，下游坐标给这条地下传送带的出口（地面坐标），悬停写“地下段已满（通往出口 X）”。
            if (nx >= 0 && _kind[nx] == (byte)BeltNodeKind.Underground && _kind[i] == (byte)BeltNodeKind.UndergroundIn
                && TryGetUndergroundEnds(x, y, out _, out int2 exit, out _))
            {
                info.NextUnderground = true;
                info.NextX = exit.x;
                info.NextY = exit.y;
            }
            if (_edge[i] != 0)
            {
                int d = _dir[i];
                info.FrontX = x + BeltDirs.Dx(d);
                info.FrontY = y + BeltDirs.Dy(d);
                TryGetKind(info.FrontX, info.FrontY, out info.FrontKind);
            }
            return info;
        }

        /// <summary>
        /// FG3-LOG-08（FGR-LOG-082“为什么不工作”）：从 (x, y) 顺着下游追到堵塞的源头——只要这一格是“下游满了 / 等汇入轮次”就走到下游那一格，
        /// 直到遇到真正的原因（传送带到头、输入口满 / 不收、朝向不对、分流器两口都堵……）或一格没有堵（满载运行、只是吞吐到顶）。
        /// 环形传送带整圈都满时走满一圈就停（<see cref="BeltBlockTrace.Looped"/>）。O(追过的格数)，只在诊断刷新时调用（AOT，不在热更层逐格循环）。
        /// </summary>
        public bool TryTraceBlock(int x, int y, out BeltBlockTrace trace)
        {
            EnsureTopology();
            trace = default;
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return false;
            }
            int limit = _x.Length + 1;
            int hops = 0;
            while (hops < limit)
            {
                byte b = _block[i];
                int nx = _next[i];
                if ((b != (byte)BeltBlock.DownstreamFull && b != (byte)BeltBlock.MergeWait) || nx < 0)
                {
                    break;
                }
                i = nx;
                hops++;
            }
            trace = new BeltBlockTrace
            {
                StartX = x,
                StartY = y,
                Hops = hops,
                Looped = hops >= limit,
                End = CellInfoAt(i),
            };
            return true;
        }

        /// <summary>
        /// FG3-LOG-08：全部“堵塞源头”格——堵塞原因不是“下游满了 / 等汇入轮次”（那两种是被下游连累的）的堵塞格，按规范顺序，最多 <paramref name="max"/> 个；
        /// 另给出每个网络的堵塞格数都已由 <see cref="TryGetNetworkStats"/> 汇总。O(格数)，AOT；诊断每次刷新调用一次。
        /// </summary>
        public int CollectBlockRoots(List<BeltCellInfo> into, int max)
        {
            EnsureTopology();
            into.Clear();
            int total = 0;
            for (int i = 0; i < _x.Length; i++)
            {
                byte b = _block[i];
                if (b == (byte)BeltBlock.None || b == (byte)BeltBlock.DownstreamFull || b == (byte)BeltBlock.MergeWait)
                {
                    continue;
                }
                total++;
                if (into.Count < max)
                {
                    into.Add(CellInfoAt(i));
                }
            }
            return total;
        }

        /// <summary>FG3-LOG-08（叠加层标签）：每个网络的锚点格（规范顺序里这个网络的第一格地面格），z = 网络编号。一次 O(格数)，AOT。</summary>
        public void CollectNetworkAnchors(List<int3> into)
        {
            EnsureTopology();
            into.Clear();
            int nets = _nets.Length;
            if (nets == 0)
            {
                return;
            }
            var seen = new NativeArray<byte>(nets, Allocator.Temp);
            for (int i = 0; i < _x.Length; i++)
            {
                int n = _net[i];
                if (n < 0 || n >= nets || seen[n] != 0 || _kind[i] == (byte)BeltNodeKind.Underground)
                {
                    continue;
                }
                seen[n] = 1;
                into.Add(new int3(_x[i], _y[i], n));
            }
            seen.Dispose();
        }

        /// <summary>FG3-LOG-03：某一等级在当前天气下的满载速度（件 / 分钟，整数；露天减速时按减速后的每步位移换算）。</summary>
        public int EffectiveItemsPerMinute(int tier, bool covered)
        {
            if (covered || _slowPct == 0)
            {
                return _config.ItemsPerMinute(tier);
            }
            long units = SlowedUnitsPerStep(tier);
            return (int)(units * 60L * _config.StepHz * _config.SlotsPerCell / BeltConst.CellLength);
        }

        public int NetworkOf(int x, int y)
        {
            EnsureTopology();
            return _lookup.TryGetValue(BeltDirs.Key(x, y), out int i) ? _net[i] : -1;
        }

        public bool TryGetNetworkStats(int network, out BeltNetworkStats stats)
        {
            EnsureTopology();
            stats = default;
            if (network < 0 || network >= _nets.Length)
            {
                return false;
            }
            BeltNetAgg a = _nets[network];
            int completed = (int)Math.Min(_counters[BeltCounters.CompletedBuckets], BeltConst.Buckets);
            int del = 0;
            int emit = 0;
            for (int k = 0; k < completed; k++)
            {
                del += k == 0 ? a.D0 : k == 1 ? a.D1 : k == 2 ? a.D2 : a.D3;
                emit += k == 0 ? a.E0 : k == 1 ? a.E1 : k == 2 ? a.E2 : a.E3;
            }
            stats = new BeltNetworkStats
            {
                Network = network,
                Cells = a.Cells,
                Items = a.Items,
                BlockedCells = a.Blocked,
                HasCycle = a.HasCycle != 0,
                Sources = a.Sources,
                Sinks = a.Sinks,
                DeliveredInWindow = del,
                EmittedInWindow = emit,
                WindowSeconds = completed * _config.BucketSteps / (float)_config.StepHz,
            };
            return true;
        }

        public bool TryGetPortInfo(int id, out BeltPortInfo info)
        {
            EnsureTopology();
            info = default;
            int p = FindPort(id);
            if (p < 0)
            {
                return false;
            }
            BeltPort bp = _ports[p];
            int completed = (int)Math.Min(_counters[BeltCounters.CompletedBuckets], BeltConst.Buckets);
            int win = 0;
            for (int k = 0; k < completed; k++)
            {
                win += k == 0 ? bp.W0 : k == 1 ? bp.W1 : k == 2 ? bp.W2 : bp.W3;
            }
            info = new BeltPortInfo
            {
                Id = bp.Id,
                Kind = (BeltPortKind)bp.Kind,
                X = bp.X,
                Y = bp.Y,
                Owner = bp.Owner,
                Connected = bp.Cell >= 0,
                Network = bp.Network,
                ItemType = bp.Item,
                IntervalSteps = bp.Interval,
                Pending = bp.Pending,
                Buffered = bp.Buffered,
                BufferCap = bp.Cap,
                Total = bp.Total,
                Consumed = bp.Consumed,
                BlockedSteps = bp.BlockedSteps,
                InWindow = win,
                WindowSeconds = completed * _config.BucketSteps / (float)_config.StepHz,
                Face = bp.Face,
                Accept = bp.Accept,
                Phase = bp.Phase,
            };
            return true;
        }

        /// <summary>全部地面格的坐标（规范顺序；读档后同步格网传送带层、测试遍历用）。z = 朝向 | 等级 &lt;&lt; 8 | 种类 &lt;&lt; 16。
        /// FG3-LOG-04：地下段不在地面上，默认不含（<paramref name="includeUnderground"/> = true 时含，坐标是地下层坐标）。</summary>
        public void CollectCells(List<int3> into, bool includeUnderground = false)
        {
            EnsureTopology();
            into.Clear();
            for (int i = 0; i < _x.Length; i++)
            {
                if (!includeUnderground && _kind[i] == (byte)BeltNodeKind.Underground)
                {
                    continue;
                }
                into.Add(new int3(_x[i], _y[i], _dir[i] | (_tier[i] << 8) | (_kind[i] << 16)));
            }
        }

        /// <summary>FG4-ECO-08（统计面板“分流器分出量”，DEBT-FG3LOG04-04）：全部活着的分流器所在格，按 (x, y) 升序。O(节点数)，只在面板刷新时调用。</summary>
        public void CollectSplitters(List<int2> into)
        {
            into.Clear();
            for (int n = 0; n < _nodes.Length; n++)
            {
                if (_nodes[n].Alive != 0 && _nodes[n].Kind == (byte)BeltNodeKind.Splitter)
                {
                    into.Add(new int2(_nodes[n].X, _nodes[n].Y));
                }
            }
            into.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        }

        public void CollectPortIds(List<int> into)
        {
            into.Clear();
            for (int p = 0; p < _ports.Length; p++)
            {
                if (_ports[p].Alive != 0)
                {
                    into.Add(_ports[p].Id);
                }
            }
        }

        /// <summary>状态哈希（确定性回放、存读档、后台一致性比对用；不含统计窗口与堵塞标记）。</summary>
        public ulong ComputeStateHash()
        {
            EnsureTopology();
            new JobBeltHash
            {
                X = _x.AsArray(), Y = _y.AsArray(), Dir = _dir.AsArray(), Tier = _tier.AsArray(), Count = _count.AsArray(),
                Turn = _turn.AsArray(), Pos = _pos.AsArray(), Item = _item.AsArray(), Ports = _ports.AsArray(),
                Counters = _counters, Out = _hashOut, Kind = _kind.AsArray(), Nodes = _nodes.AsArray(),
            }.Run();
            return _hashOut[0];
        }

        // ── 渲染缓冲 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 把当前状态写进渲染缓冲（格实例 + 物品实例，Burst）。只在步进之后第一次调用时真的重填。
        /// <paramref name="includeItems"/> = false（远景流动贴图不画物品）时只重填格实例，物品实例留到回近景时再补一次；此时 <paramref name="itemCount"/> = 0。
        /// </summary>
        public void PrepareRender(float cellSize, out NativeArray<BeltInstance> cells, out int cellCount,
            out NativeArray<BeltInstance> items, out int itemCount, bool force = false, bool includeItems = true)
        {
            EnsureTopology();
            // FG3-LOG-04：地下段不画，格实例数可以少于格数——“格数变了”按上一次填缓冲时的格数判断，不按实例数。
            bool stale = force || _renderRevisionSeen != Revision || _renderCellsSeen != _x.Length;
            if (stale || (includeItems && !_renderItemsValid))
            {
                long t0 = _watch.ElapsedTicks;
                _watch.Start();
                int n = _x.Length;
                if (_renderCells.Length < n)
                {
                    _renderCells.Dispose();
                    _renderCells = new NativeArray<BeltInstance>(Math.Max(16, n * 3 / 2), Allocator.Persistent);
                }
                // 上界 = 格数 × 每格容量（不逐格数物品，扩容时一次到位）。
                int totalItems = n * _config.SlotsPerCell;
                if (includeItems && _renderItems.Length < totalItems)
                {
                    _renderItems.Dispose();
                    _renderItems = new NativeArray<BeltInstance>(Math.Max(16, totalItems), Allocator.Persistent);
                }
                new JobBeltRender
                {
                    CL = BeltConst.CellLength, CellSize = cellSize, StepHz = _config.StepHz, SlotsPerCell = _config.SlotsPerCell,
                    V0 = _v0, V1 = _v1, V2 = _v2, S0 = _s0, S1 = _s1, S2 = _s2, SlowOn = _slowPct > 0 ? 1 : 0, WriteItems = includeItems ? 1 : 0,
                    X = _x.AsArray(), Y = _y.AsArray(), Dir = _dir.AsArray(), Tier = _tier.AsArray(), Covered = _covered.AsArray(), Count = _count.AsArray(),
                    Block = _block.AsArray(), Pos = _pos.AsArray(), Delta = _delta.AsArray(), Item = _item.AsArray(), Kind = _kind.AsArray(),
                    Cells = _renderCells, Items = _renderItems, OutCounts = _renderCounts,
                }.Run();
                _watch.Stop();
                LastRenderPrepMs = (_watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                _renderRevisionSeen = Revision;
                _renderCellsSeen = n;
                _renderItemsValid = includeItems;
                if (includeItems)
                {
                    RenderItemFills++;
                }
            }
            cells = _renderCells;
            cellCount = _renderCounts[0];
            items = _renderItems;
            itemCount = _renderItemsValid ? _renderCounts[1] : 0;
        }

        // ── 存档（按网络分块）──────────────────────────────────────────────────

        /// <summary>
        /// 序列化：每个网络一块（格：坐标、朝向、等级、轮次、种类与节点设置、物品位置 / 种类 / 上一步位移），外加端口块与账本。
        /// 块格式（3）：'B''N' 版本 保留 | 格数 | 物品数 | 每格 (x, y, 朝向, 等级, 轮次, 种类, [节点设置], 件数, 件数 ×(位置, 种类, 上一步位移)) | FNV-32 校验和。
        /// 格式 4（FG3-LOG-09）在校验和之前追加统计尾段：'S' | 网络的本桶累计与 4 个桶（收下 / 推上，10 个变长整数）| 每格按块内顺序（本桶通过数 + 4 个桶 + 上一步的堵塞原因）|
        /// 每个分流器按块内顺序（左右口本桶累计 + 左右口各 4 个桶）。只逐格解析的旧工具看不到尾段，照常可用。
        /// 格式 1 / 2 没有种类、节点设置与上一步位移。
        /// <paramref name="formatVersion"/>：只认 3 与 4（当前）；写 3 只供兼容测试造旧存档，游戏里一律写当前格式。
        /// </summary>
        public BeltSnapshot Serialize(int formatVersion = FormatVersion)
        {
            EnsureTopology();
            if (formatVersion != FormatVersion && formatVersion != 3)
            {
                throw new ArgumentOutOfRangeException(nameof(formatVersion), "只能写格式 3 或当前格式 " + FormatVersion);
            }
            bool stats = formatVersion >= 4;
            var snap = new BeltSnapshot
            {
                FormatVersion = formatVersion,
                StepIndex = _counters[BeltCounters.StepIndex],
                Emitted = _counters[BeltCounters.Emitted],
                Inserted = _counters[BeltCounters.Inserted],
                Delivered = _counters[BeltCounters.Delivered],
                Removed = _counters[BeltCounters.Removed],
                CompletedBuckets = stats ? _counters[BeltCounters.CompletedBuckets] : 0,
            };
            int netCount = _nets.Length;
            var cellsOfNet = new List<int>[netCount];
            for (int k = 0; k < netCount; k++)
            {
                cellsOfNet[k] = new List<int>(_nets[k].Cells);
            }
            for (int i = 0; i < _x.Length; i++)
            {
                cellsOfNet[_net[i]].Add(i);
            }
            var w = new BeltByteWriter(256);
            for (int k = 0; k < netCount; k++)
            {
                w.Reset();
                w.U8((byte)'B');
                w.U8((byte)'N');
                w.U8((byte)formatVersion);
                w.U8(0);
                List<int> list = cellsOfNet[k];
                int items = 0;
                foreach (int i in list)
                {
                    items += _count[i];
                }
                w.I32(list.Count);
                w.I32(items);
                foreach (int i in list)
                {
                    w.I32(_x[i]);
                    w.I32(_y[i]);
                    w.U8(_dir[i]);
                    w.U8(_tier[i]);
                    w.U8(_turn[i]);
                    // 格式 3（FG3-LOG-04）：种类与节点设置 / 状态（分流器 31 字节、合流器 1 字节、地下两端各 4 字节、普通传送带与地下段 0 字节）。
                    WriteNode(w, i);
                    int cnt = _count[i];
                    w.U8((byte)cnt);
                    for (int s = 0; s < cnt; s++)
                    {
                        w.I32(_pos[i * S + s]);
                        w.U16(_item[i * S + s]);
                        // 格式 3（FG3-LOG-04）：上一步位移。它决定“这一步中途才空出来”的入口放置位置（输出端口、分流器交给输出口），
                        // 读档后接着跑要与不存档一直跑逐位一致就必须存；只影响后续演化，不进状态哈希（旧格式读进来为 0）。
                        w.U16((ushort)Math.Min(ushort.MaxValue, Math.Max(0, _delta[i * S + s])));
                    }
                }
                if (stats)
                {
                    WriteChunkStats(w, k, list);
                }
                w.U32(w.Checksum());
                snap.Networks.Add(new BeltSnapshot.Chunk { Cells = list.Count, Items = items, Bytes = w.ToArray() });
            }
            w.Reset();
            w.U8((byte)'B');
            w.U8((byte)'P');
            w.U8((byte)formatVersion);
            w.U8(0);
            int alivePorts = PortCount;
            w.I32(alivePorts);
            for (int p = 0; p < _ports.Length; p++)
            {
                BeltPort bp = _ports[p];
                if (bp.Alive == 0)
                {
                    continue;
                }
                w.I32(bp.Id);
                w.U8(bp.Kind);
                w.I32(bp.X);
                w.I32(bp.Y);
                w.I32(bp.Owner);
                w.U8(bp.Face); // 格式 2（FG3-LOG-03）
                w.U16(bp.Item);
                w.U16(bp.Accept); // 格式 2（FG3-LOG-03）
                w.I32(bp.Interval);
                w.I32(bp.Phase);
                w.I32(bp.Pending);
                w.I32(bp.Buffered);
                w.I32(bp.Cap);
                w.I64(bp.Total);
                w.I64(bp.Consumed);
                w.I64(bp.BlockedSteps);
            }
            if (stats)
            {
                // 格式 4 端口统计尾段：按上面的端口顺序，每个端口本桶累计 + 4 个桶（悬停 / 面板“实测 N 件/分钟”）。
                w.U8(StatsMarker);
                for (int p = 0; p < _ports.Length; p++)
                {
                    BeltPort bp = _ports[p];
                    if (bp.Alive == 0)
                    {
                        continue;
                    }
                    w.VarU(bp.WindowAcc);
                    w.VarU(bp.W0);
                    w.VarU(bp.W1);
                    w.VarU(bp.W2);
                    w.VarU(bp.W3);
                }
            }
            w.U32(w.Checksum());
            snap.Ports = w.ToArray();
            return snap;
        }

        /// <summary>格式 4 网络块的统计尾段（见 <see cref="Serialize"/>）。<paramref name="list"/> = 块内的格（与上面写格的顺序相同）。</summary>
        private void WriteChunkStats(BeltByteWriter w, int net, List<int> list)
        {
            w.U8(StatsMarker);
            BeltNetAgg a = _nets[net];
            w.VarU(a.DelAcc);
            w.VarU(a.EmitAcc);
            w.VarU(a.D0);
            w.VarU(a.D1);
            w.VarU(a.D2);
            w.VarU(a.D3);
            w.VarU(a.E0);
            w.VarU(a.E1);
            w.VarU(a.E2);
            w.VarU(a.E3);
            foreach (int i in list)
            {
                w.VarU(_flow[i]);
                int b = i * BeltConst.Buckets;
                for (int k = 0; k < BeltConst.Buckets; k++)
                {
                    w.VarU(_flowB[b + k]);
                }
                w.VarU(_block[i]); // 上一步的堵塞原因（派生量，存下来是为了读档瞬间的“堵塞格”读数与存档前一致）
            }
            foreach (int i in list)
            {
                int ni = _nodeOf[i];
                if (ni < 0 || _kind[i] != (byte)BeltNodeKind.Splitter)
                {
                    continue;
                }
                BeltNode nd = _nodes[ni];
                w.VarU(nd.AccL);
                w.VarU(nd.AccR);
                w.VarU(nd.WL0);
                w.VarU(nd.WL1);
                w.VarU(nd.WL2);
                w.VarU(nd.WL3);
                w.VarU(nd.WR0);
                w.VarU(nd.WR1);
                w.VarU(nd.WR2);
                w.VarU(nd.WR3);
            }
        }

        private void WriteNode(BeltByteWriter w, int i)
        {
            byte kind = _kind[i];
            w.U8(kind);
            int ni = _nodeOf[i];
            if (ni < 0)
            {
                return;
            }
            BeltNode nd = _nodes[ni];
            switch ((BeltNodeKind)kind)
            {
                case BeltNodeKind.Splitter:
                    w.U8(nd.RatioL);
                    w.U8(nd.RatioR);
                    w.U8(nd.PrioOut);
                    w.U16(nd.FilterL);
                    w.U16(nd.FilterR);
                    w.I32(nd.CntL);
                    w.I32(nd.CntR);
                    w.I64(nd.SentL);
                    w.I64(nd.SentR);
                    break;
                case BeltNodeKind.Merger:
                    w.U8(nd.PrioIn);
                    break;
                case BeltNodeKind.UndergroundIn:
                case BeltNodeKind.UndergroundOut:
                    w.I32(nd.Span);
                    break;
            }
        }

        /// <summary>
        /// 从快照恢复到一个空内核。坏块（魔数 / 版本 / 长度 / 校验和 / 数值非法 / 坐标重复 / 物品重叠）整块丢弃，
        /// 其声明的物品数记入“移出”（账本仍平衡），并在 <see cref="CorruptChunksDropped"/> 里如实报告；其余网络照常恢复。
        /// 端口块损坏时整块丢弃（端口可由建筑重新登记）。返回 false = 快照整体不可用（格式版本不认识 / 内核非空）。
        /// </summary>
        public bool Deserialize(BeltSnapshot snap, out string error)
        {
            ThrowIfDisposed();
            error = null;
            if (snap == null)
            {
                error = "snapshot_null";
                return false;
            }
            if (_x.Length != 0 || _ports.Length != 0)
            {
                error = "not_empty";
                return false;
            }
            if (snap.FormatVersion < OldestReadableVersion || snap.FormatVersion > FormatVersion)
            {
                error = "format_version";
                return false;
            }
            _counters[BeltCounters.StepIndex] = snap.StepIndex;
            _counters[BeltCounters.Emitted] = snap.Emitted;
            _counters[BeltCounters.Inserted] = snap.Inserted;
            _counters[BeltCounters.Delivered] = snap.Delivered;
            _counters[BeltCounters.Removed] = snap.Removed;
            // 格式 4：统计窗口的时间轴（旧格式为 0：读档后窗口从头累计，与格式 4 之前的行为一样）。
            _counters[BeltCounters.CompletedBuckets] = snap.FormatVersion >= 4 ? Math.Max(0, snap.CompletedBuckets) : 0;
            CorruptChunksDropped = 0;
            CorruptItemsDropped = 0;
            StatsDroppedOnLoad = 0;
            _pendingNetStats.Clear();
            var tmpItems = new List<(int pos, ushort item)>(S);
            var addedCells = new List<(int x, int y)>(64);
            foreach (BeltSnapshot.Chunk chunk in snap.Networks)
            {
                if (!TryLoadChunk(chunk, tmpItems, addedCells, out string why))
                {
                    // 回滚这一块已经加进去的格（FG3-LOG-04：连同它们的节点），按“移出”记账（物品不凭空消失：账本如实反映）。
                    foreach ((int x, int y) in addedCells)
                    {
                        long key = BeltDirs.Key(x, y);
                        if (_nodeIndex.TryGetValue(key, out int ni))
                        {
                            KillNode(ni);
                        }
                        if (_lookup.TryGetValue(key, out int i))
                        {
                            _count[i] = 0;
                            _alive[i] = 0;
                            _lookup.Remove(key);
                            _aliveCells--;
                        }
                    }
                    CorruptChunksDropped++;
                    CorruptItemsDropped += Math.Max(0, chunk?.Items ?? 0);
                    _counters[BeltCounters.Removed] += Math.Max(0, chunk?.Items ?? 0);
                    error = AppendCode(error, why);
                }
            }
            if (snap.Ports != null && snap.Ports.Length > 0 && !TryLoadPorts(snap.Ports, out string portWhy))
            {
                error = AppendCode(error, portWhy);
            }
            _dirty = true;
            EnsureTopology();
            ApplyPendingNetStats();
            _counters[BeltCounters.ItemsOnBelts] = CountItemsSlow();
            return true;
        }

        /// <summary>FG3-LOG-09：读档时统计尾段读不了（数值越界 / 长度不符）的网络块或端口块数——这些窗口从零累计（统计只影响读数，不影响物品与状态）。</summary>
        public int StatsDroppedOnLoad { get; private set; }

        /// <summary>格式 4：每个网络块的网络级窗口，按块里第一格的坐标暂存；拓扑重建（会清空网络汇总）之后再按那一格现在所属的网络写回。</summary>
        private readonly List<(long key, BeltNetAgg agg)> _pendingNetStats = new List<(long key, BeltNetAgg agg)>();

        private void ApplyPendingNetStats()
        {
            foreach ((long key, BeltNetAgg saved) in _pendingNetStats)
            {
                if (!_lookup.TryGetValue(key, out int i) || _net[i] < 0 || _net[i] >= _nets.Length)
                {
                    continue;
                }
                BeltNetAgg a = _nets[_net[i]];
                a.DelAcc = saved.DelAcc;
                a.EmitAcc = saved.EmitAcc;
                a.D0 = saved.D0;
                a.D1 = saved.D1;
                a.D2 = saved.D2;
                a.D3 = saved.D3;
                a.E0 = saved.E0;
                a.E1 = saved.E1;
                a.E2 = saved.E2;
                a.E3 = saved.E3;
                _nets[_net[i]] = a;
            }
            _pendingNetStats.Clear();
        }

        /// <summary>
        /// 格式 4 网络块的统计尾段（见 <see cref="Serialize"/>）：格与分流器按块内顺序对应 <paramref name="added"/>。
        /// 读不了（标记不对、数值读不出、长度对不上）→ 这一块的窗口全部清零并计数，不丢弃网络（统计是读数，不是状态）。
        /// </summary>
        private void ReadChunkStats(BeltByteReader r, List<(int x, int y)> added)
        {
            int n = added.Count;
            const int perCell = 2 + BeltConst.Buckets; // 本桶通过数 + 4 个桶 + 堵塞原因
            var flows = new int[n * perCell];
            var splitters = new List<(int node, int[] v)>();
            var net = new int[10];
            bool ok = r.U8() == StatsMarker;
            for (int k = 0; ok && k < net.Length; k++)
            {
                ok = r.TryVarU(out net[k]);
            }
            for (int k = 0; ok && k < flows.Length; k++)
            {
                ok = r.TryVarU(out flows[k]);
            }
            for (int c = 0; ok && c < n; c++)
            {
                long key = BeltDirs.Key(added[c].x, added[c].y);
                if (!_nodeIndex.TryGetValue(key, out int ni) || _nodes[ni].Kind != (byte)BeltNodeKind.Splitter)
                {
                    continue;
                }
                var v = new int[10];
                for (int k = 0; ok && k < v.Length; k++)
                {
                    ok = r.TryVarU(out v[k]);
                }
                splitters.Add((ni, v));
            }
            if (!ok || !r.AtEnd)
            {
                StatsDroppedOnLoad++;
                return;
            }
            for (int c = 0; c < n; c++)
            {
                if (!_lookup.TryGetValue(BeltDirs.Key(added[c].x, added[c].y), out int i))
                {
                    continue;
                }
                int f = c * perCell;
                _flow[i] = flows[f];
                for (int k = 0; k < BeltConst.Buckets; k++)
                {
                    _flowB[i * BeltConst.Buckets + k] = flows[f + 1 + k];
                }
                _block[i] = (byte)Math.Min(255, flows[f + 1 + BeltConst.Buckets]);
            }
            foreach ((int ni, int[] v) in splitters)
            {
                BeltNode nd = _nodes[ni];
                nd.AccL = v[0];
                nd.AccR = v[1];
                nd.WL0 = v[2];
                nd.WL1 = v[3];
                nd.WL2 = v[4];
                nd.WL3 = v[5];
                nd.WR0 = v[6];
                nd.WR1 = v[7];
                nd.WR2 = v[8];
                nd.WR3 = v[9];
                _nodes[ni] = nd;
            }
            if (n > 0)
            {
                _pendingNetStats.Add((BeltDirs.Key(added[0].x, added[0].y), new BeltNetAgg
                {
                    DelAcc = net[0], EmitAcc = net[1], D0 = net[2], D1 = net[3], D2 = net[4], D3 = net[5], E0 = net[6], E1 = net[7], E2 = net[8], E3 = net[9],
                }));
            }
        }

        /// <summary>读档问题的稳定原因码（逗号分隔、去重）；热更层映射成文本键 logistics.load.reason.&lt;码&gt;（内核不带玩家可见文本）。</summary>
        private static string AppendCode(string codes, string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return codes;
            }
            if (string.IsNullOrEmpty(codes))
            {
                return code;
            }
            foreach (string c in codes.Split(','))
            {
                if (c == code)
                {
                    return codes;
                }
            }
            return codes + "," + code;
        }

        private bool TryLoadChunk(BeltSnapshot.Chunk chunk, List<(int, ushort)> tmp, List<(int x, int y)> added, out string why)
        {
            added.Clear();
            why = null;
            byte[] data = chunk?.Bytes;
            if (data == null || data.Length < 16)
            {
                why = "chunk_empty";
                return false;
            }
            var r = new BeltByteReader(data);
            if (!r.VerifyChecksum())
            {
                why = "chunk_checksum";
                return false;
            }
            // 网络块的格式在 1 → 2 之间没有变化（只有端口块变了），两种版本号都认。
            byte chunkVersion = 0;
            if (r.U8() != 'B' || r.U8() != 'N' || (chunkVersion = r.U8()) < OldestReadableVersion || chunkVersion > FormatVersion)
            {
                why = "chunk_magic";
                return false;
            }
            r.U8();
            int cells = r.I32();
            int items = r.I32();
            if (cells < 0 || items < 0 || cells != chunk.Cells || items != chunk.Items)
            {
                why = "chunk_header";
                return false;
            }
            int seenItems = 0;
            bool nodes = chunkVersion >= 3;
            for (int c = 0; c < cells; c++)
            {
                if (!r.Has(8 + 3 + (nodes ? 1 : 0) + 1))
                {
                    why = "chunk_truncated";
                    return false;
                }
                int x = r.I32();
                int y = r.I32();
                byte dir = r.U8();
                byte tier = r.U8();
                byte turn = r.U8();
                // 格式 3（FG3-LOG-04）：种类 + 节点设置；格式 1 / 2 全是普通传送带。
                BeltNodeKind kind = BeltNodeKind.Belt;
                var node = new BeltNode { X = x, Y = y };
                if (nodes && !TryReadNode(r, ref node, out kind))
                {
                    why = "chunk_node";
                    return false;
                }
                if (kind == BeltNodeKind.Merger)
                {
                    // 与 AddNode 新建时逐字段相同（合流器不存比例，新建时记 1:1）；地下两端新建时比例为 0，也不存——读回同样为 0，状态哈希一致。
                    node.RatioL = 1;
                    node.RatioR = 1;
                }
                if (!r.Has(1))
                {
                    why = "chunk_truncated";
                    return false;
                }
                int cnt = r.U8();
                int itemBytes = nodes ? 8 : 6;
                if (cnt > _config.SlotsPerCell || !r.Has(cnt * itemBytes))
                {
                    why = "chunk_truncated";
                    return false;
                }
                // 地下段必须在地下层坐标、其余格必须在地面坐标范围内。
                bool under = BeltDirs.IsUnderY(y);
                if (dir > 3 || tier >= BeltConst.TierCount || under != (kind == BeltNodeKind.Underground)
                    || Math.Abs(x) > BeltConst.CoordLimit || (!under && Math.Abs(y) > BeltConst.CoordLimit))
                {
                    why = "chunk_cell";
                    return false;
                }
                BeltResult add = AddCellRaw(x, y, (BeltDir)dir, tier);
                if (add != BeltResult.Ok)
                {
                    why = "chunk_cell";
                    return false;
                }
                added.Add((x, y));
                if (kind != BeltNodeKind.Belt && kind != BeltNodeKind.Underground)
                {
                    node.Kind = (byte)kind;
                    AppendNode(node);
                }
                _lookup.TryGetValue(BeltDirs.Key(x, y), out int i);
                _turn[i] = turn == BeltConst.NoTurn || turn <= 3 ? turn : BeltConst.NoTurn;
                int last = int.MaxValue;
                for (int s = 0; s < cnt; s++)
                {
                    int pos = r.I32();
                    ushort item = r.U16();
                    int delta = nodes ? r.U16() : 0; // 格式 3：上一步位移（入口放置规则用）；旧格式为 0
                    if (pos < 0 || pos >= BeltConst.CellLength || (s > 0 && last - pos < _spacing) || delta > BeltConst.CellLength)
                    {
                        why = "chunk_item";
                        return false;
                    }
                    _pos[i * S + s] = pos;
                    _item[i * S + s] = item;
                    _delta[i * S + s] = delta;
                    last = pos;
                }
                _count[i] = (byte)cnt;
                seenItems += cnt;
            }
            if (seenItems != items)
            {
                why = "chunk_count";
                return false;
            }
            if (nodes && !UndergroundsIntact(added))
            {
                why = "chunk_node";
                return false;
            }
            if (chunkVersion >= 4)
            {
                ReadChunkStats(r, added);
            }
            return true;
        }

        /// <summary>FG3-LOG-04：读一格的种类与节点设置，数值非法时返回 false（整块丢弃）。</summary>
        private static bool TryReadNode(BeltByteReader r, ref BeltNode node, out BeltNodeKind kind)
        {
            byte k = r.U8();
            kind = (BeltNodeKind)k;
            switch (kind)
            {
                case BeltNodeKind.Belt:
                case BeltNodeKind.Underground:
                    return true;
                case BeltNodeKind.Splitter:
                    if (!r.Has(31))
                    {
                        return false;
                    }
                    node.RatioL = r.U8();
                    node.RatioR = r.U8();
                    node.PrioOut = r.U8();
                    node.FilterL = r.U16();
                    node.FilterR = r.U16();
                    node.CntL = r.I32();
                    node.CntR = r.I32();
                    node.SentL = r.I64();
                    node.SentR = r.I64();
                    return node.RatioL >= 1 && node.RatioL <= BeltConst.RatioMax && node.RatioR >= 1 && node.RatioR <= BeltConst.RatioMax && node.PrioOut <= 2
                           && node.CntL >= 0 && node.CntL <= BeltConst.RatioMax && node.CntR >= 0 && node.CntR <= BeltConst.RatioMax && node.SentL >= 0 && node.SentR >= 0;
                case BeltNodeKind.Merger:
                    if (!r.Has(1))
                    {
                        return false;
                    }
                    node.PrioIn = r.U8();
                    return node.PrioIn <= 2;
                case BeltNodeKind.UndergroundIn:
                case BeltNodeKind.UndergroundOut:
                    if (!r.Has(4))
                    {
                        return false;
                    }
                    node.Span = r.I32();
                    return node.Span >= 1 && node.Span <= BeltConst.MaxUndergroundDistance;
                default:
                    return false;
            }
        }

        /// <summary>
        /// FG3-LOG-04：一块网络里的地下传送带是否完整——每个入口的地下段与出口都在（同方向、同等级、出口记的距离相同），每个出口有对应的入口，
        /// 每格地下段都属于某个入口（地下段数 = Σ(距离 − 1)，坐标不重复）。不完整整块丢弃（原因 chunk_node）。
        /// </summary>
        private bool UndergroundsIntact(List<(int x, int y)> added)
        {
            int hidden = 0;
            int expectHidden = 0;
            foreach ((int x, int y) in added)
            {
                long key = BeltDirs.Key(x, y);
                _lookup.TryGetValue(key, out int i);
                if (BeltDirs.IsUnderY(y))
                {
                    hidden++;
                    continue;
                }
                if (!_nodeIndex.TryGetValue(key, out int ni))
                {
                    continue;
                }
                BeltNode nd = _nodes[ni];
                int d = _dir[i];
                if (nd.Kind == (byte)BeltNodeKind.UndergroundIn)
                {
                    expectHidden += nd.Span - 1;
                    for (int s = 1; s < nd.Span; s++)
                    {
                        if (!_lookup.TryGetValue(BeltDirs.Key(x + BeltDirs.Dx(d) * s, BeltDirs.UnderY(y + BeltDirs.Dy(d) * s, d)), out int h)
                            || _dir[h] != d || _tier[h] != _tier[i])
                        {
                            return false;
                        }
                    }
                    long ek = BeltDirs.Key(x + BeltDirs.Dx(d) * nd.Span, y + BeltDirs.Dy(d) * nd.Span);
                    if (!_lookup.TryGetValue(ek, out int e) || !_nodeIndex.TryGetValue(ek, out int en) || _nodes[en].Kind != (byte)BeltNodeKind.UndergroundOut
                        || _nodes[en].Span != nd.Span || _dir[e] != d || _tier[e] != _tier[i])
                    {
                        return false;
                    }
                }
                else if (nd.Kind == (byte)BeltNodeKind.UndergroundOut)
                {
                    long sk = BeltDirs.Key(x - BeltDirs.Dx(d) * nd.Span, y - BeltDirs.Dy(d) * nd.Span);
                    if (!_nodeIndex.TryGetValue(sk, out int sn) || _nodes[sn].Kind != (byte)BeltNodeKind.UndergroundIn || _nodes[sn].Span != nd.Span)
                    {
                        return false;
                    }
                }
            }
            return hidden == expectHidden;
        }

        private bool TryLoadPorts(byte[] data, out string why)
        {
            why = null;
            var r = new BeltByteReader(data);
            byte version = 0;
            if (!r.VerifyChecksum() || r.U8() != 'B' || r.U8() != 'P' || (version = r.U8()) < OldestReadableVersion || version > FormatVersion)
            {
                why = "ports_corrupt";
                return false;
            }
            r.U8();
            int n = r.I32();
            // 每个端口的字节数：格式 1 = 63；格式 2 多了朝向（1）与收货过滤（2）= 66。格式 4 的统计在全部记录之后的尾段里。
            int record = version >= 2 ? 66 : 63;
            if (n < 0 || !r.Has(n * record))
            {
                why = "ports_corrupt";
                return false;
            }
            // 格式 4：记录第 k 个端口在 _ports 里的下标（跳过的记录为 -1），读完记录再按同一顺序读统计尾段。
            var loaded = version >= 4 ? new int[n] : null;
            for (int k = 0; k < n; k++)
            {
                if (loaded != null)
                {
                    loaded[k] = -1;
                }
                int id = r.I32();
                byte kind = r.U8();
                int px = r.I32();
                int py = r.I32();
                int owner = r.I32();
                byte face = version >= 2 ? r.U8() : BeltConst.AnyFace;
                ushort item = r.U16();
                ushort accept = version >= 2 ? r.U16() : BeltConst.AcceptAny;
                var bp = new BeltPort
                {
                    Id = id,
                    Kind = kind,
                    Alive = 1,
                    X = px,
                    Y = py,
                    Owner = owner,
                    Face = face <= 3 ? face : BeltConst.AnyFace,
                    Item = item,
                    Accept = accept,
                    Interval = r.I32(),
                    Phase = r.I32(),
                    Pending = r.I32(),
                    Buffered = r.I32(),
                    Cap = r.I32(),
                    Total = r.I64(),
                    Consumed = r.I64(),
                    BlockedSteps = r.I64(),
                    Cell = -1,
                    Network = -1,
                };
                if ((bp.Kind != (byte)BeltPortKind.Source && bp.Kind != (byte)BeltPortKind.Sink) || FindPort(bp.Id) >= 0)
                {
                    continue;
                }
                _ports.Add(bp);
                _portIndex[bp.Id] = _ports.Length - 1;
                if (loaded != null)
                {
                    loaded[k] = _ports.Length - 1;
                }
            }
            if (loaded != null)
            {
                ReadPortStats(r, loaded);
            }
            return true;
        }

        /// <summary>格式 4 端口块的统计尾段：每个端口本桶累计 + 4 个桶。读不了 → 端口窗口从零累计并计数（端口状态照常恢复）。</summary>
        private void ReadPortStats(BeltByteReader r, int[] loaded)
        {
            var v = new int[loaded.Length * 5];
            bool ok = r.U8() == StatsMarker;
            for (int k = 0; ok && k < v.Length; k++)
            {
                ok = r.TryVarU(out v[k]);
            }
            if (!ok || !r.AtEnd)
            {
                StatsDroppedOnLoad++;
                return;
            }
            for (int k = 0; k < loaded.Length; k++)
            {
                int p = loaded[k];
                if (p < 0)
                {
                    continue;
                }
                BeltPort bp = _ports[p];
                bp.WindowAcc = v[k * 5];
                bp.W0 = v[k * 5 + 1];
                bp.W1 = v[k * 5 + 2];
                bp.W2 = v[k * 5 + 3];
                bp.W3 = v[k * 5 + 4];
                _ports[p] = bp;
            }
        }

        // ── 释放 ───────────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BeltKernel));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _x.Dispose();
            _y.Dispose();
            _dir.Dispose();
            _tier.Dispose();
            _alive.Dispose();
            _covered.Dispose();
            _coveredKeys.Dispose();
            _count.Dispose();
            _turn.Dispose();
            _block.Dispose();
            _ring.Dispose();
            _feedN.Dispose();
            _next.Dispose();
            _sink.Dispose();
            _net.Dispose();
            _flow.Dispose();
            _feed.Dispose();
            _pos.Dispose();
            _delta.Dispose();
            _flowB.Dispose();
            _item.Dispose();
            _lookup.Dispose();
            _ringCells.Dispose();
            _rings.Dispose();
            _ringOf.Dispose();
            _ringCount.Dispose();
            _tree.Dispose();
            _nets.Dispose();
            _ports.Dispose();
            _nodes.Dispose();
            _kind.Dispose();
            _nodeOf.Dispose();
            _edge.Dispose();
            _counters.Dispose();
            _hashOut.Dispose();
            _renderCounts.Dispose();
            _renderCells.Dispose();
            _renderItems.Dispose();
        }
    }

    /// <summary>内核快照（存档的中间形态；热更层把字节块转成 base64 写进 JSON 存档）。</summary>
    public sealed class BeltSnapshot
    {
        public sealed class Chunk
        {
            public int Cells;
            public int Items;
            public byte[] Bytes;
        }

        public int FormatVersion;
        public long StepIndex;
        public long Emitted;
        public long Inserted;
        public long Delivered;
        public long Removed;
        /// <summary>FG3-LOG-09（格式 4）：已经走满的统计桶数（统计窗口的“时间轴”；0 = 旧格式，读档后窗口从头累计）。</summary>
        public long CompletedBuckets;
        public readonly List<Chunk> Networks = new List<Chunk>();
        public byte[] Ports;

        public int TotalCells
        {
            get
            {
                int n = 0;
                foreach (Chunk c in Networks)
                {
                    n += c.Cells;
                }
                return n;
            }
        }

        public long TotalBytes
        {
            get
            {
                long n = Ports?.Length ?? 0;
                foreach (Chunk c in Networks)
                {
                    n += c.Bytes?.Length ?? 0;
                }
                return n;
            }
        }
    }

    internal sealed class BeltByteWriter
    {
        private byte[] _buf;
        private int _len;

        public BeltByteWriter(int capacity) => _buf = new byte[Math.Max(16, capacity)];

        public void Reset() => _len = 0;

        private void Ensure(int more)
        {
            if (_len + more > _buf.Length)
            {
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + more));
            }
        }

        public void U8(byte v)
        {
            Ensure(1);
            _buf[_len++] = v;
        }

        public void U16(ushort v)
        {
            Ensure(2);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
        }

        public void I32(int v) => U32((uint)v);

        public void U32(uint v)
        {
            Ensure(4);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)(v >> 16);
            _buf[_len++] = (byte)(v >> 24);
        }

        public void I64(long v)
        {
            U32((uint)v);
            U32((uint)(v >> 32));
        }

        /// <summary>FG3-LOG-09：非负整数的变长编码（每字节 7 位，高位 = 还有下一字节）。统计窗口多数是很小的数，1 字节就够；负数按 0 写。</summary>
        public void VarU(int v)
        {
            uint u = (uint)Math.Max(0, v);
            while (u >= 0x80)
            {
                U8((byte)(u | 0x80));
                u >>= 7;
            }
            U8((byte)u);
        }

        /// <summary>FNV-1a 32 位（覆盖到目前为止写入的全部字节）。</summary>
        public uint Checksum() => BeltByteReader.Fnv32(_buf, 0, _len);

        public byte[] ToArray()
        {
            var r = new byte[_len];
            Buffer.BlockCopy(_buf, 0, r, 0, _len);
            return r;
        }
    }

    internal sealed class BeltByteReader
    {
        private readonly byte[] _b;
        private readonly int _end;
        private int _at;

        public BeltByteReader(byte[] b)
        {
            _b = b ?? Array.Empty<byte>();
            _end = Math.Max(0, _b.Length - 4);
        }

        public static uint Fnv32(byte[] b, int start, int len)
        {
            uint h = 2166136261u;
            for (int i = start; i < start + len; i++)
            {
                h ^= b[i];
                h *= 16777619u;
            }
            return h;
        }

        public bool VerifyChecksum()
        {
            if (_b.Length < 4)
            {
                return false;
            }
            uint stored = (uint)(_b[_end] | (_b[_end + 1] << 8) | (_b[_end + 2] << 16) | (_b[_end + 3] << 24));
            return stored == Fnv32(_b, 0, _end);
        }

        public bool Has(int n) => _at + n <= _end;

        public byte U8() => _at < _end ? _b[_at++] : (byte)0;

        public ushort U16() => (ushort)(U8() | (U8() << 8));

        public int I32() => (int)U32();

        public uint U32() => (uint)(U8() | (U8() << 8) | (U8() << 16) | (U8() << 24));

        public long I64()
        {
            uint lo = U32();
            uint hi = U32();
            return (long)(((ulong)hi << 32) | lo);
        }

        /// <summary>FG3-LOG-09：读 <see cref="BeltByteWriter.VarU"/> 写的变长整数。越过结尾或超过 5 字节 / int 范围 → false（整块按坏块处理）。</summary>
        public bool TryVarU(out int v)
        {
            v = 0;
            uint u = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (_at >= _end)
                {
                    return false;
                }
                byte b = _b[_at++];
                u |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (u > int.MaxValue)
                    {
                        return false;
                    }
                    v = (int)u;
                    return true;
                }
            }
            return false;
        }

        /// <summary>读到了正文结尾（校验和之前）。</summary>
        public bool AtEnd => _at == _end;
    }
}
