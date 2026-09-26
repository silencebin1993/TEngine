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
    /// 原生内存成对释放：<see cref="Dispose"/>。
    /// </summary>
    public sealed class BeltKernel : IDisposable
    {
        public const int FormatVersion = 1;

        private const int S = BeltConst.MaxSlots;

        private readonly BeltConfig _config;
        private readonly int _spacing;
        private readonly int _v0;
        private readonly int _v1;
        private readonly int _v2;

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
        private NativeArray<long> _counters;
        private NativeArray<ulong> _hashOut;
        private NativeArray<int> _renderCounts;
        private NativeArray<BeltInstance> _renderCells;
        private NativeArray<BeltInstance> _renderItems;

        private bool _dirty;
        private bool _disposed;
        private int _aliveCells;
        private int _renderRevisionSeen = -1;
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
            int cap = Math.Max(16, initialCells);
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
            if (!_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
            {
                return BeltResult.NotFound;
            }
            TakeItems(i, removed);
            _alive[i] = 0;
            _lookup.Remove(BeltDirs.Key(x, y));
            _aliveCells--;
            _dirty = true;
            return BeltResult.Ok;
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
        public BeltResult AddSource(int id, int x, int y, ushort item, int intervalSteps, int pending, int owner = 0)
        {
            if (intervalSteps < 1 || pending < -1)
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
                Id = id, Kind = (byte)BeltPortKind.Source, Alive = 1, X = x, Y = y, Owner = owner, Item = item,
                Interval = intervalSteps, Phase = 0, Pending = pending, Cell = -1, Network = -1,
            });
            _dirty = true;
            return BeltResult.Ok;
        }

        /// <summary>输入端口：建筑格 (x,y)；末端朝向这一格的传送带把物品送进来。<paramref name="bufferCap"/> = -1 表示收下即消耗（不限），
        /// 否则缓存满了就不收（上游堵塞，原因“下游 X 已满”）；<paramref name="consumeIntervalSteps"/> > 0 时每隔这么多步自动消耗一件。</summary>
        public BeltResult AddSink(int id, int x, int y, int bufferCap, int consumeIntervalSteps, int owner = 0)
        {
            if (bufferCap < -1 || consumeIntervalSteps < 0)
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
                Id = id, Kind = (byte)BeltPortKind.Sink, Alive = 1, X = x, Y = y, Owner = owner,
                Interval = consumeIntervalSteps, Cap = bufferCap, Cell = -1, Network = -1,
            });
            _dirty = true;
            return BeltResult.Ok;
        }

        public BeltResult RemovePort(int id)
        {
            int p = FindPort(id);
            if (p < 0)
            {
                return BeltResult.PortNotFound;
            }
            BeltPort bp = _ports[p];
            bp.Alive = 0;
            _ports[p] = bp;
            _dirty = true;
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

        private int FindPort(int id)
        {
            for (int p = 0; p < _ports.Length; p++)
            {
                if (_ports[p].Alive != 0 && _ports[p].Id == id)
                {
                    return p;
                }
            }
            return -1;
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
                Pos = _pos, Delta = _delta, FlowB = _flowB, Item = _item, Lookup = _lookup, RingCells = _ringCells,
                Rings = _rings, RingOf = _ringOf, RingCount = _ringCount, Tree = _tree, Nets = _nets, Ports = _ports,
            }.Run();
            _watch.Stop();
            LastRebuildMs = (_watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            _aliveCells = _x.Length;
            _dirty = false;
            RebuildCount++;
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
                CL = BeltConst.CellLength, Spacing = _spacing, V0 = _v0, V1 = _v1, V2 = _v2, BucketSteps = _config.BucketSteps,
                SlotsPerCell = _config.SlotsPerCell,
                X = _x.AsArray(), Y = _y.AsArray(), Dir = _dir.AsArray(), Ring = _ring.AsArray(), Tier = _tier.AsArray(),
                FeedN = _feedN.AsArray(), Feed = _feed.AsArray(),
                Next = _next.AsArray(), Sink = _sink.AsArray(), Net = _net.AsArray(), RingCells = _ringCells.AsArray(),
                Rings = _rings.AsArray(), RingOf = _ringOf.AsArray(), RingCount = _ringCount.AsArray(), Tree = _tree.AsArray(),
                Count = _count.AsArray(), Turn = _turn.AsArray(),
                Block = _block.AsArray(), Flow = _flow.AsArray(), FlowB = _flowB.AsArray(), Pos = _pos.AsArray(),
                Delta = _delta.AsArray(), Item = _item.AsArray(), Ports = _ports.AsArray(), Nets = _nets.AsArray(),
                Counters = _counters,
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
                RatedItemsPerMinute = _config.ItemsPerMinute(_tier[i]),
                Item0 = cnt > 0 ? _item[b] : (ushort)0,
                Item1 = cnt > 1 ? _item[b + 1] : (ushort)0,
                Item2 = cnt > 2 ? _item[b + 2] : (ushort)0,
                Item3 = cnt > 3 ? _item[b + 3] : (ushort)0,
                Pos0 = cnt > 0 ? _pos[b] : -1,
                Pos1 = cnt > 1 ? _pos[b + 1] : -1,
                Pos2 = cnt > 2 ? _pos[b + 2] : -1,
                Pos3 = cnt > 3 ? _pos[b + 3] : -1,
            };
            return true;
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
            };
            return true;
        }

        /// <summary>全部格的坐标（规范顺序；读档后同步格网传送带层、测试遍历用）。</summary>
        public void CollectCells(List<int3> into)
        {
            EnsureTopology();
            into.Clear();
            for (int i = 0; i < _x.Length; i++)
            {
                into.Add(new int3(_x[i], _y[i], _dir[i] | (_tier[i] << 8)));
            }
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
                Counters = _counters, Out = _hashOut,
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
            bool stale = force || _renderRevisionSeen != Revision || _renderCounts[0] != _x.Length;
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
                    V0 = _v0, V1 = _v1, V2 = _v2, WriteItems = includeItems ? 1 : 0,
                    X = _x.AsArray(), Y = _y.AsArray(), Dir = _dir.AsArray(), Tier = _tier.AsArray(), Count = _count.AsArray(),
                    Block = _block.AsArray(), Pos = _pos.AsArray(), Delta = _delta.AsArray(), Item = _item.AsArray(),
                    Cells = _renderCells, Items = _renderItems, OutCounts = _renderCounts,
                }.Run();
                _watch.Stop();
                LastRenderPrepMs = (_watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                _renderRevisionSeen = Revision;
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
        /// 序列化：每个网络一块（格：坐标、朝向、等级、轮次、物品位置与种类），外加端口块与账本。
        /// 块格式：'B''N' 版本 保留 | 格数 | 物品数 | 每格 (x, y, 朝向, 等级, 轮次, 件数, 件数 ×(位置, 种类)) | FNV-32 校验和。
        /// </summary>
        public BeltSnapshot Serialize()
        {
            EnsureTopology();
            var snap = new BeltSnapshot
            {
                FormatVersion = FormatVersion,
                StepIndex = _counters[BeltCounters.StepIndex],
                Emitted = _counters[BeltCounters.Emitted],
                Inserted = _counters[BeltCounters.Inserted],
                Delivered = _counters[BeltCounters.Delivered],
                Removed = _counters[BeltCounters.Removed],
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
                w.U8(FormatVersion);
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
                    int cnt = _count[i];
                    w.U8((byte)cnt);
                    for (int s = 0; s < cnt; s++)
                    {
                        w.I32(_pos[i * S + s]);
                        w.U16(_item[i * S + s]);
                    }
                }
                w.U32(w.Checksum());
                snap.Networks.Add(new BeltSnapshot.Chunk { Cells = list.Count, Items = items, Bytes = w.ToArray() });
            }
            w.Reset();
            w.U8((byte)'B');
            w.U8((byte)'P');
            w.U8(FormatVersion);
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
                w.U16(bp.Item);
                w.I32(bp.Interval);
                w.I32(bp.Phase);
                w.I32(bp.Pending);
                w.I32(bp.Buffered);
                w.I32(bp.Cap);
                w.I64(bp.Total);
                w.I64(bp.Consumed);
                w.I64(bp.BlockedSteps);
            }
            w.U32(w.Checksum());
            snap.Ports = w.ToArray();
            return snap;
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
            if (snap.FormatVersion != FormatVersion)
            {
                error = "format_version";
                return false;
            }
            _counters[BeltCounters.StepIndex] = snap.StepIndex;
            _counters[BeltCounters.Emitted] = snap.Emitted;
            _counters[BeltCounters.Inserted] = snap.Inserted;
            _counters[BeltCounters.Delivered] = snap.Delivered;
            _counters[BeltCounters.Removed] = snap.Removed;
            CorruptChunksDropped = 0;
            CorruptItemsDropped = 0;
            var tmpItems = new List<(int pos, ushort item)>(S);
            var addedCells = new List<(int x, int y)>(64);
            foreach (BeltSnapshot.Chunk chunk in snap.Networks)
            {
                if (!TryLoadChunk(chunk, tmpItems, addedCells, out string why))
                {
                    // 回滚这一块已经加进去的格，按“移出”记账（物品不凭空消失：账本如实反映）。
                    foreach ((int x, int y) in addedCells)
                    {
                        if (_lookup.TryGetValue(BeltDirs.Key(x, y), out int i))
                        {
                            _count[i] = 0;
                            _alive[i] = 0;
                            _lookup.Remove(BeltDirs.Key(x, y));
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
            _counters[BeltCounters.ItemsOnBelts] = CountItemsSlow();
            return true;
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
            if (r.U8() != 'B' || r.U8() != 'N' || r.U8() != FormatVersion)
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
            for (int c = 0; c < cells; c++)
            {
                if (!r.Has(8 + 4))
                {
                    why = "chunk_truncated";
                    return false;
                }
                int x = r.I32();
                int y = r.I32();
                byte dir = r.U8();
                byte tier = r.U8();
                byte turn = r.U8();
                int cnt = r.U8();
                if (cnt > _config.SlotsPerCell || !r.Has(cnt * 6))
                {
                    why = "chunk_truncated";
                    return false;
                }
                BeltResult add = AddCell(x, y, (BeltDir)dir, tier);
                if (add != BeltResult.Ok)
                {
                    why = "chunk_cell";
                    return false;
                }
                added.Add((x, y));
                _lookup.TryGetValue(BeltDirs.Key(x, y), out int i);
                _turn[i] = turn == BeltConst.NoTurn || turn <= 3 ? turn : BeltConst.NoTurn;
                int last = int.MaxValue;
                for (int s = 0; s < cnt; s++)
                {
                    int pos = r.I32();
                    ushort item = r.U16();
                    if (pos < 0 || pos >= BeltConst.CellLength || (s > 0 && last - pos < _spacing))
                    {
                        why = "chunk_item";
                        return false;
                    }
                    _pos[i * S + s] = pos;
                    _item[i * S + s] = item;
                    _delta[i * S + s] = 0;
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
            return true;
        }

        private bool TryLoadPorts(byte[] data, out string why)
        {
            why = null;
            var r = new BeltByteReader(data);
            if (!r.VerifyChecksum() || r.U8() != 'B' || r.U8() != 'P' || r.U8() != FormatVersion)
            {
                why = "ports_corrupt";
                return false;
            }
            r.U8();
            int n = r.I32();
            if (n < 0 || !r.Has(n * 63))
            {
                why = "ports_corrupt";
                return false;
            }
            for (int k = 0; k < n; k++)
            {
                var bp = new BeltPort
                {
                    Id = r.I32(),
                    Kind = r.U8(),
                    Alive = 1,
                    X = r.I32(),
                    Y = r.I32(),
                    Owner = r.I32(),
                    Item = r.U16(),
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
            }
            return true;
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
    }
}
