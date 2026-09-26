using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Logistics
{
    // FG0-ARCH-02（FGR-ARC-004）：传送带内核的四个 Burst 作业。全部是单线程 IJob——
    //   规范顺序（按坐标）处理 + 整数定点 = 结果逐位确定，与线程数、调度时机、平台无关；
    //   15,000 格 / 30,000 件在单线程 Burst 下远低于 2 毫秒预算，不需要并行换取确定性风险。
    //
    // 步进语义（FG03 FGR-LOG-020/021/025）：
    //   - 每格单车道、最多 SlotsPerCell 件，相邻物品间距 ≥ CellLength / SlotsPerCell；每步前进 = 本格等级速度（定点单位 / 步）。
    //   - 下游优先：先算下游、再算上游，下游这一步腾出的空位上游这一步就能用上（满载直线整体匀速前进，不会一格一格“蠕动”）。
    //   - 环（首尾相连）：环上的物品整体按“最大可行前进量”求解（两遍 min-plus 回绕），满环也一直转（FG03 第 5 节“环上的物品一直转”）。
    //   - 汇入点（一格有 2～3 个上游）：按“轮到谁”交替汇入；轮到的那一路没有物品要进来时，别的路可以先进（不空等）。
    //   - 下游满 / 末端 / 输入端口满：停下，物品不消失（FGR-LOG-025），并记下堵塞原因。
    //   - 物品只会在格之间移动、被端口收下、或经 API 移出；每一步都记账，守恒可随时核对（FGT-LOG-006）。

    /// <summary>端口的内部存储（AoS：端口数量与建筑同量级，远少于格数）。</summary>
    internal struct BeltPort
    {
        public int Id;
        public byte Kind;
        public byte Alive;
        public int X;
        public int Y;
        public int Owner;
        public ushort Item;
        /// <summary>输出端口：推一件的间隔（步）；输入端口：消耗一件的间隔（步，0 = 不自动消耗）。</summary>
        public int Interval;
        public int Phase;
        /// <summary>输出端口待推数量（-1 = 无限）。</summary>
        public int Pending;
        /// <summary>输入端口缓存数量。</summary>
        public int Buffered;
        /// <summary>输入端口缓存上限（-1 = 不限，收下即消耗）。</summary>
        public int Cap;
        /// <summary>派生：输出端口所在格 / 输入端口的第一条来料格（-1 = 没接上）。</summary>
        public int Cell;
        public int Network;
        public long Total;
        public long Consumed;
        public long BlockedSteps;
        public int WindowAcc;
        public int W0;
        public int W1;
        public int W2;
        public int W3;
    }

    /// <summary>网络汇总（每步更新，热更层按网络号 O(1) 读取）。</summary>
    internal struct BeltNetAgg
    {
        public int Cells;
        public int Items;
        public int Blocked;
        public int HasCycle;
        public int Sources;
        public int Sinks;
        public int DelAcc;
        public int EmitAcc;
        public int D0, D1, D2, D3;
        public int E0, E1, E2, E3;
    }

    internal static class BeltCounters
    {
        public const int StepIndex = 0;
        public const int Emitted = 1;
        public const int Inserted = 2;
        public const int Delivered = 3;
        public const int Removed = 4;
        public const int CompletedBuckets = 5;
        public const int BlockedCells = 6;
        public const int CapacityViolations = 7;
        public const int ItemsOnBelts = 8;
        public const int Transfers = 9;
        public const int Length = 16;
    }

    internal struct BeltKeyIdx : IComparable<BeltKeyIdx>
    {
        public long Key;
        public int Idx;

        public int CompareTo(BeltKeyIdx other)
        {
            int c = Key.CompareTo(other.Key);
            return c != 0 ? c : Idx.CompareTo(other.Idx);
        }
    }

    internal struct BeltPortKey : IComparable<BeltPortKey>
    {
        public byte Kind;
        public long Key;
        public int Id;
        public int Idx;

        public int CompareTo(BeltPortKey other)
        {
            int c = Kind.CompareTo(other.Kind);
            if (c != 0)
            {
                return c;
            }
            c = Key.CompareTo(other.Key);
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }

    /// <summary>
    /// 拓扑重建（只在编辑后、下一步之前跑一次）：删掉墓碑格、按坐标 (y, x) 升序重排全部格（规范下标——
    /// 同一组传送带不论以什么顺序放下、是否经过存读档，下标都相同，于是处理顺序与结果都相同），
    /// 然后重算下游、输入端口、上游表、环、下游优先的处理顺序与网络号。O(N log N)。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct JobBeltRebuild : IJob
    {
        public NativeList<int> X;
        public NativeList<int> Y;
        public NativeList<byte> Dir;
        public NativeList<byte> Tier;
        public NativeList<byte> Alive;
        public NativeList<byte> Count;
        public NativeList<byte> Turn;
        public NativeList<byte> Block;
        public NativeList<byte> Ring;
        public NativeList<byte> FeedN;
        public NativeList<int> Next;
        public NativeList<int> Sink;
        public NativeList<int> Net;
        public NativeList<int> Flow;
        public NativeList<int> Feed;
        public NativeList<int> Pos;
        public NativeList<int> Delta;
        public NativeList<int> FlowB;
        public NativeList<ushort> Item;
        public NativeParallelHashMap<long, int> Lookup;
        public NativeList<int> RingCells;
        public NativeList<int2> Rings;
        public NativeList<int> RingOf;
        public NativeList<int> RingCount;
        public NativeList<int> Tree;
        public NativeList<BeltNetAgg> Nets;
        public NativeList<BeltPort> Ports;

        public void Execute()
        {
            int n = X.Length;
            var order = new NativeList<BeltKeyIdx>(math.max(1, n), Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                if (Alive[i] != 0)
                {
                    order.Add(new BeltKeyIdx { Key = BeltDirs.Key(X[i], Y[i]), Idx = i });
                }
            }
            order.Sort();
            int m = order.Length;

            Permute(X, order, 1);
            Permute(Y, order, 1);
            Permute(Dir, order, 1);
            Permute(Tier, order, 1);
            Permute(Count, order, 1);
            Permute(Turn, order, 1);
            Permute(Block, order, 1);
            Permute(Flow, order, 1);
            Permute(Pos, order, BeltConst.MaxSlots);
            Permute(Delta, order, BeltConst.MaxSlots);
            Permute(Item, order, BeltConst.MaxSlots);
            Permute(FlowB, order, BeltConst.Buckets);
            Alive.Resize(m, NativeArrayOptions.UninitializedMemory);
            Ring.Resize(m, NativeArrayOptions.UninitializedMemory);
            FeedN.Resize(m, NativeArrayOptions.UninitializedMemory);
            Next.Resize(m, NativeArrayOptions.UninitializedMemory);
            Sink.Resize(m, NativeArrayOptions.UninitializedMemory);
            Net.Resize(m, NativeArrayOptions.UninitializedMemory);
            Feed.Resize(m * BeltConst.MaxFeeders, NativeArrayOptions.UninitializedMemory);
            order.Dispose();

            Lookup.Clear();
            for (int i = 0; i < m; i++)
            {
                Alive[i] = 1;
                Ring[i] = 0;
                FeedN[i] = 0;
                Net[i] = -1;
                Lookup.TryAdd(BeltDirs.Key(X[i], Y[i]), i);
            }

            // 端口：去掉已删除的，按（种类，坐标，编号）规范排序。
            int pn = Ports.Length;
            var pOrder = new NativeList<BeltPortKey>(math.max(1, pn), Allocator.Temp);
            for (int p = 0; p < pn; p++)
            {
                BeltPort bp = Ports[p];
                if (bp.Alive != 0)
                {
                    pOrder.Add(new BeltPortKey { Kind = bp.Kind, Key = BeltDirs.Key(bp.X, bp.Y), Id = bp.Id, Idx = p });
                }
            }
            pOrder.Sort();
            var pTmp = new NativeArray<BeltPort>(pOrder.Length, Allocator.Temp);
            for (int j = 0; j < pOrder.Length; j++)
            {
                pTmp[j] = Ports[pOrder[j].Idx];
            }
            Ports.Resize(pOrder.Length, NativeArrayOptions.UninitializedMemory);
            var sinkAt = new NativeParallelHashMap<long, int>(math.max(1, pOrder.Length), Allocator.Temp);
            for (int j = 0; j < pTmp.Length; j++)
            {
                BeltPort bp = pTmp[j];
                bp.Cell = -1;
                bp.Network = -1;
                Ports[j] = bp;
                if (bp.Kind == (byte)BeltPortKind.Sink)
                {
                    sinkAt.TryAdd(BeltDirs.Key(bp.X, bp.Y), j);
                }
            }
            pTmp.Dispose();
            pOrder.Dispose();

            // 下游与输入端口：正前方有带、并且不是迎面相对（两条带头对头不相连）。
            for (int i = 0; i < m; i++)
            {
                int d = Dir[i];
                int fx = X[i] + BeltDirs.Dx(d);
                int fy = Y[i] + BeltDirs.Dy(d);
                long fk = BeltDirs.Key(fx, fy);
                int nx = -1;
                if (Lookup.TryGetValue(fk, out int j) && Dir[j] != BeltDirs.Opposite(d))
                {
                    nx = j;
                }
                Next[i] = nx;
                Sink[i] = -1;
                if (nx < 0 && sinkAt.TryGetValue(fk, out int sp))
                {
                    Sink[i] = sp;
                }
            }
            sinkAt.Dispose();

            // 上游表：按来自哪一侧（北、东、南、西）排序。
            for (int i = 0; i < m; i++)
            {
                int j = Next[i];
                if (j < 0)
                {
                    continue;
                }
                int side = SideOf(j, i);
                int k = FeedN[j];
                int b = j * BeltConst.MaxFeeders;
                int at = k;
                while (at > 0 && SideOf(j, Feed[b + at - 1]) > side)
                {
                    Feed[b + at] = Feed[b + at - 1];
                    at--;
                }
                Feed[b + at] = i;
                FeedN[j] = (byte)(k + 1);
            }
            for (int i = 0; i < m; i++)
            {
                if (FeedN[i] <= 1)
                {
                    Turn[i] = BeltConst.NoTurn;
                }
            }

            // 环：函数图（每格至多一个下游）里每个弱连通块至多一个环。环从环上下标最小的一格开始记。
            RingCells.Clear();
            Rings.Clear();
            var state = new NativeArray<byte>(m, Allocator.Temp);
            var path = new NativeList<int>(64, Allocator.Temp);
            for (int i = 0; i < m; i++)
            {
                if (state[i] != 0)
                {
                    continue;
                }
                path.Clear();
                int cur = i;
                while (cur >= 0 && state[cur] == 0)
                {
                    state[cur] = 1;
                    path.Add(cur);
                    cur = Next[cur];
                }
                if (cur >= 0 && state[cur] == 1)
                {
                    int minIdx = cur;
                    int walk = Next[cur];
                    int len = 1;
                    while (walk != cur)
                    {
                        if (walk < minIdx)
                        {
                            minIdx = walk;
                        }
                        walk = Next[walk];
                        len++;
                    }
                    int start = RingCells.Length;
                    walk = minIdx;
                    for (int k = 0; k < len; k++)
                    {
                        RingCells.Add(walk);
                        Ring[walk] = 1;
                        walk = Next[walk];
                    }
                    Rings.Add(new int2(start, len));
                }
                for (int k = 0; k < path.Length; k++)
                {
                    state[path[k]] = 2;
                }
            }
            path.Dispose();
            state.Dispose();

            // 下游优先的处理顺序 + 网络号（按首次出现的规范下标编号）。
            Tree.Clear();
            Nets.Clear();
            var queue = new NativeList<int>(math.max(1, m), Allocator.Temp);
            var ringOf = new NativeArray<int>(m, Allocator.Temp);
            for (int i = 0; i < m; i++)
            {
                ringOf[i] = -1;
            }
            for (int r = 0; r < Rings.Length; r++)
            {
                int2 rg = Rings[r];
                for (int k = 0; k < rg.y; k++)
                {
                    ringOf[RingCells[rg.x + k]] = r;
                }
            }
            RingOf.Resize(m, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < m; i++)
            {
                RingOf[i] = ringOf[i];
            }
            RingCount.Resize(Rings.Length, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < m; i++)
            {
                if (Net[i] >= 0)
                {
                    continue;
                }
                int netId;
                if (Ring[i] != 0)
                {
                    netId = Nets.Length;
                    Nets.Add(new BeltNetAgg { HasCycle = 1 });
                    int2 rg = Rings[ringOf[i]];
                    queue.Clear();
                    for (int k = 0; k < rg.y; k++)
                    {
                        int c = RingCells[rg.x + k];
                        Net[c] = netId;
                        queue.Add(c);
                    }
                }
                else if (Next[i] < 0)
                {
                    netId = Nets.Length;
                    Nets.Add(new BeltNetAgg());
                    Net[i] = netId;
                    Tree.Add(i);
                    queue.Clear();
                    queue.Add(i);
                }
                else
                {
                    // 非根、非环：它的根 / 环下标更大，稍后从那里 BFS 时会被带到。
                    continue;
                }
                for (int head = 0; head < queue.Length; head++)
                {
                    int c = queue[head];
                    int b = c * BeltConst.MaxFeeders;
                    int fnum = FeedN[c];
                    for (int k = 0; k < fnum; k++)
                    {
                        int f = Feed[b + k];
                        if (Ring[f] != 0)
                        {
                            continue;
                        }
                        Net[f] = netId;
                        Tree.Add(f);
                        queue.Add(f);
                    }
                }
            }
            queue.Dispose();
            ringOf.Dispose();

            for (int i = 0; i < m; i++)
            {
                BeltNetAgg a = Nets[Net[i]];
                a.Cells++;
                Nets[Net[i]] = a;
            }

            // 端口派生：输出端口 = (X,Y) 上的带；输入端口 = 第一条末端朝向它的带。
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort bp = Ports[p];
                if (bp.Kind == (byte)BeltPortKind.Source && Lookup.TryGetValue(BeltDirs.Key(bp.X, bp.Y), out int c))
                {
                    bp.Cell = c;
                    bp.Network = Net[c];
                    Ports[p] = bp;
                }
            }
            for (int i = 0; i < m; i++)
            {
                int sp = Sink[i];
                if (sp >= 0)
                {
                    BeltPort bp = Ports[sp];
                    if (bp.Cell < 0)
                    {
                        bp.Cell = i;
                        bp.Network = Net[i];
                        Ports[sp] = bp;
                    }
                }
            }
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort bp = Ports[p];
                if (bp.Network >= 0)
                {
                    BeltNetAgg a = Nets[bp.Network];
                    if (bp.Kind == (byte)BeltPortKind.Source)
                    {
                        a.Sources++;
                    }
                    else
                    {
                        a.Sinks++;
                    }
                    Nets[bp.Network] = a;
                }
            }
        }

        private int SideOf(int target, int feeder)
        {
            int dx = X[feeder] - X[target];
            int dy = Y[feeder] - Y[target];
            return dy > 0 ? 0 : dx > 0 ? 1 : dy < 0 ? 2 : 3;
        }

        private static void Permute<T>(NativeList<T> list, NativeList<BeltKeyIdx> order, int stride) where T : unmanaged
        {
            int m = order.Length;
            var tmp = new NativeArray<T>(math.max(1, m * stride), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int j = 0; j < m; j++)
            {
                int src = order[j].Idx * stride;
                int dst = j * stride;
                for (int k = 0; k < stride; k++)
                {
                    tmp[dst + k] = list[src + k];
                }
            }
            list.Resize(m * stride, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < m * stride; i++)
            {
                list[i] = tmp[i];
            }
            tmp.Dispose();
        }
    }

    /// <summary>一个固定步（1 / StepHz 游戏秒）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct JobBeltStep : IJob
    {
        public int CL;
        public int Spacing;
        public int V0;
        public int V1;
        public int V2;
        public int BucketSteps;
        public int SlotsPerCell;

        [ReadOnly] public NativeArray<int> X;
        [ReadOnly] public NativeArray<int> Y;
        [ReadOnly] public NativeArray<byte> Dir;
        [ReadOnly] public NativeArray<byte> Ring;
        [ReadOnly] public NativeArray<byte> Tier;
        [ReadOnly] public NativeArray<byte> FeedN;
        [ReadOnly] public NativeArray<int> Feed;
        [ReadOnly] public NativeArray<int> Next;
        [ReadOnly] public NativeArray<int> Sink;
        [ReadOnly] public NativeArray<int> Net;
        [ReadOnly] public NativeArray<int> RingCells;
        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int> RingOf;
        [ReadOnly] public NativeArray<int> Tree;
        public NativeArray<int> RingCount;
        public NativeArray<byte> Count;
        public NativeArray<byte> Turn;
        public NativeArray<byte> Block;
        public NativeArray<int> Flow;
        public NativeArray<int> FlowB;
        public NativeArray<int> Pos;
        public NativeArray<int> Delta;
        public NativeArray<ushort> Item;
        public NativeArray<BeltPort> Ports;
        public NativeArray<BeltNetAgg> Nets;
        public NativeArray<long> Counters;

        private const int S = BeltConst.MaxSlots;

        public void Execute()
        {
            for (int i = 0; i < Delta.Length; i++)
            {
                Delta[i] = 0;
            }
            for (int r = 0; r < Rings.Length; r++)
            {
                ProcessRing(r);
            }
            for (int k = 0; k < Tree.Length; k++)
            {
                ProcessTree(Tree[k]);
            }
            Emit();
            Consume();

            // 网络汇总与全局计数（O(格数)，Burst 下可忽略）。
            for (int nId = 0; nId < Nets.Length; nId++)
            {
                BeltNetAgg a = Nets[nId];
                a.Items = 0;
                a.Blocked = 0;
                Nets[nId] = a;
            }
            long blocked = 0;
            long items = 0;
            int n = Count.Length;
            for (int i = 0; i < n; i++)
            {
                int c = Count[i];
                items += c;
                bool b = Block[i] != 0;
                if (b)
                {
                    blocked++;
                }
                if (c != 0 || b)
                {
                    BeltNetAgg a = Nets[Net[i]];
                    a.Items += c;
                    if (b)
                    {
                        a.Blocked++;
                    }
                    Nets[Net[i]] = a;
                }
            }
            Counters[BeltCounters.BlockedCells] = blocked;
            Counters[BeltCounters.ItemsOnBelts] = items;

            long step = Counters[BeltCounters.StepIndex] + 1;
            Counters[BeltCounters.StepIndex] = step;
            if (BucketSteps > 0 && step % BucketSteps == 0)
            {
                RollBuckets();
            }
        }

        private int Speed(int c)
        {
            byte t = Tier[c];
            return t == 0 ? V0 : t == 1 ? V1 : V2;
        }

        private int SideOf(int target, int feeder)
        {
            int dx = X[feeder] - X[target];
            int dy = Y[feeder] - Y[target];
            return dy > 0 ? 0 : dx > 0 ? 1 : dy < 0 ? 2 : 3;
        }

        /// <summary>汇入点 <paramref name="n"/> 现在轮到哪一路（未设置 / 那一侧已经没有上游时 = 第一路）。</summary>
        private int Holder(int n)
        {
            int b = n * BeltConst.MaxFeeders;
            int t = Turn[n];
            int fn = FeedN[n];
            if (t != BeltConst.NoTurn)
            {
                for (int k = 0; k < fn; k++)
                {
                    if (SideOf(n, Feed[b + k]) == t)
                    {
                        return Feed[b + k];
                    }
                }
            }
            return Feed[b];
        }

        /// <summary>这一路这一步是否有物品要跨进下游（按它当前状态）。</summary>
        private bool Ready(int h)
        {
            int c = Count[h];
            return c > 0 && Pos[h * S] + Speed(h) >= CL;
        }

        /// <summary><paramref name="f"/> 刚汇入一件：轮到它后面的下一路。</summary>
        private void AdvanceTurn(int n, int f)
        {
            int fn = FeedN[n];
            if (fn <= 1)
            {
                return;
            }
            int b = n * BeltConst.MaxFeeders;
            int idx = 0;
            for (int k = 0; k < fn; k++)
            {
                if (Feed[b + k] == f)
                {
                    idx = k;
                    break;
                }
            }
            Turn[n] = (byte)SideOf(n, Feed[b + (idx + 1) % fn]);
        }

        /// <summary>
        /// 车道里紧跟在 <paramref name="n"/> 入口后面的那一路（它的物品与 n 的物品在同一条车道上，间距约束跨格成立）：
        /// 环上的格 = 环上的前一格；否则 = 正后方同向的一格；只有一路上游时 = 那一路（拐弯）。侧向汇入的物品不在车道上，直到汇入为止。
        /// </summary>
        private int InLaneOf(int n)
        {
            int fn = FeedN[n];
            if (fn == 0)
            {
                return -1;
            }
            int b = n * BeltConst.MaxFeeders;
            if (Ring[n] != 0)
            {
                for (int k = 0; k < fn; k++)
                {
                    if (Ring[Feed[b + k]] != 0)
                    {
                        return Feed[b + k];
                    }
                }
            }
            int d = Dir[n];
            int bx = X[n] - BeltDirs.Dx(d);
            int by = Y[n] - BeltDirs.Dy(d);
            for (int k = 0; k < fn; k++)
            {
                int f = Feed[b + k];
                if (X[f] == bx && Y[f] == by)
                {
                    return f;
                }
            }
            return fn == 1 ? Feed[b] : -1;
        }

        /// <summary><paramref name="f"/>（-1 = 输出端口）往 <paramref name="n"/> 入口放一件时，位置至少要多少：
        /// 与车道上紧跟其后的那一路的头一件保持一个间距（不在车道上的侧向来料不算）。</summary>
        private int MinInsertPos(int n, int f)
        {
            int lane = InLaneOf(n);
            if (lane < 0 || lane == f || Count[lane] == 0)
            {
                return 0;
            }
            return math.max(0, Pos[lane * S] - CL + Spacing);
        }

        /// <summary><paramref name="n"/> 入口现在最多能放到哪个位置（-1 = 放不下）。</summary>
        private int MaxInsertPos(int n)
        {
            int nc = Count[n];
            if (nc >= S || nc >= SlotsPerCell)
            {
                return -1;
            }
            return nc > 0 ? math.min(CL - 1, Pos[n * S + nc - 1] - Spacing) : CL - 1;
        }

        /// <summary>汇入点 <paramref name="n"/> 现在轮到 <paramref name="f"/> 以外的另一路、且那一路的头一件这一步要进来（只看轮次，不看下游空位）。</summary>
        private bool WantsYield(int n, int f)
        {
            if (FeedN[n] <= 1)
            {
                return false;
            }
            int h = Holder(n);
            return h != f && Ready(h);
        }

        /// <summary>
        /// 从 <paramref name="f"/> 汇入 <paramref name="n"/> 这一步是否让路（树上的一路用；环上的前一格见 <see cref="ProcessRing"/> 的让路名额）：
        /// n 是汇入点、现在轮到另一路、那一路的头一件这一步要进来；n 在环上时还要求环里还放得下一件（满环时不让路，环照常转）。
        /// </summary>
        private bool MustYield(int n, int f)
        {
            if (!WantsYield(n, f))
            {
                return false;
            }
            if (Ring[n] != 0)
            {
                int r = RingOf[n];
                return RingCount[r] < Rings[r].y * SlotsPerCell;
            }
            return true;
        }

        /// <summary>物品进入 <paramref name="c"/>（来自环外）：环上件数随之增加（让路判定用）。</summary>
        private void NoteEnter(int c)
        {
            if (Ring[c] != 0)
            {
                RingCount[RingOf[c]]++;
            }
        }

        private void ProcessTree(int c)
        {
            int cnt = Count[c];
            if (cnt == 0)
            {
                Block[c] = 0;
                return;
            }
            int v = Speed(c);
            int b = c * S;
            int nx = Next[c];
            int limit;
            byte reason;
            int sp = -1;
            int minIns = 0;
            if (nx >= 0)
            {
                reason = (byte)BeltBlock.DownstreamFull;
                bool inLane = c == InLaneOf(nx);
                if (MustYield(nx, c))
                {
                    // 让路：车道上的那一路停在入口后正好一个间距处（给侧向来料留出位置），侧向的一路停在入口。
                    limit = inLane ? CL - Spacing : CL - 1;
                    reason = (byte)BeltBlock.MergeWait;
                }
                else
                {
                    int maxIns = MaxInsertPos(nx);
                    minIns = MinInsertPos(nx, c);
                    limit = maxIns >= minIns ? CL + maxIns : CL - 1;
                }
                int nxc = Count[nx];
                if (inLane && nxc > 0)
                {
                    // 车道跨格间距：车道上的一路与下游格最后一件（可能是同一步刚从侧向汇入的那件）至少隔一个间距。
                    limit = math.min(limit, CL + Pos[nx * S + nxc - 1] - Spacing);
                }
            }
            else if (Sink[c] >= 0)
            {
                sp = Sink[c];
                BeltPort port = Ports[sp];
                bool sinkAccepts = port.Cap < 0 || port.Buffered < port.Cap;
                limit = sinkAccepts ? CL + CL : CL - 1;
                reason = (byte)BeltBlock.SinkFull;
            }
            else
            {
                limit = CL - 1;
                reason = (byte)BeltBlock.EndOfBelt;
            }

            bool blocked = false;
            int lim = limit;
            for (int k = 0; k < cnt; k++)
            {
                int p = Pos[b + k];
                int want = p + v;
                int np = want < lim ? want : lim;
                if (np < p)
                {
                    np = p;
                }
                if (k == 0 && np < want)
                {
                    blocked = true;
                }
                Delta[b + k] = np - p;
                Pos[b + k] = np;
                lim = np - Spacing;
            }

            if (Pos[b] >= CL)
            {
                ushort it = Item[b];
                // 汇入时至少放在“车道后方那一件 + 一个间距”处（最多比自然位置靠前不到一步，保证车道间距）。
                int np = math.max(Pos[b] - CL, minIns);
                int dl = Delta[b];
                for (int k = 1; k < cnt; k++)
                {
                    Pos[b + k - 1] = Pos[b + k];
                    Item[b + k - 1] = Item[b + k];
                    Delta[b + k - 1] = Delta[b + k];
                }
                Count[c] = (byte)(cnt - 1);
                Flow[c]++;
                Counters[BeltCounters.Transfers]++;
                if (nx >= 0)
                {
                    int nb = nx * S;
                    int nc = Count[nx];
                    Pos[nb + nc] = np;
                    Item[nb + nc] = it;
                    Delta[nb + nc] = dl;
                    Count[nx] = (byte)(nc + 1);
                    AdvanceTurn(nx, c);
                    NoteEnter(nx);
                }
                else
                {
                    BeltPort port = Ports[sp];
                    if (port.Cap >= 0)
                    {
                        port.Buffered++;
                    }
                    else
                    {
                        port.Consumed++;
                    }
                    port.Total++;
                    port.WindowAcc++;
                    Ports[sp] = port;
                    Counters[BeltCounters.Delivered]++;
                    BeltNetAgg a = Nets[Net[c]];
                    a.DelAcc++;
                    Nets[Net[c]] = a;
                }
            }
            Block[c] = blocked ? reason : (byte)0;
        }

        /// <summary>
        /// 一个环：把环上全部物品按环坐标（第 idx 格 × 格长 + 格内位置）排成升序，求每件的最大可行新位置：
        ///   x[k] = max(X[k], min(a[k], x[k+1] − 间距))，x[末] ≤ x[0] + 环长 − 间距（回绕）。
        /// 先按“领头那件也全速前进”的上界算一遍，再用回绕约束修正至多两遍——对任意等级混排、任意疏密都收敛到唯一解，
        /// 满环时整环同速转动（不会因为“先算谁”而卡死）。
        /// </summary>
        private void ProcessRing(int r)
        {
            int start = Rings[r].x;
            int len = Rings[r].y;
            int total = 0;
            for (int k = 0; k < len; k++)
            {
                total += Count[RingCells[start + k]];
            }
            RingCount[r] = total;
            if (total == 0)
            {
                for (int k = 0; k < len; k++)
                {
                    Block[RingCells[start + k]] = 0;
                }
                return;
            }
            long L = (long)len * CL;
            var oldX = new NativeArray<long>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var want = new NativeArray<long>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var cap = new NativeArray<long>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var x = new NativeArray<long>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var items = new NativeArray<ushort>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var oldIdx = new NativeArray<int>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var capped = new NativeArray<byte>(total, Allocator.Temp);
            // 让路名额 = 环上空位数：按环上顺序，前“空位数”个想让路（轮到支线且支线有货）的汇入点才让路。
            // 这样停住时“让路处的额外空隙之和 = 空位数 × 间距”分给不超过空位数个让路点，必有一处 ≥ 2 个间距，支线一定插得进去——
            // 环不会因为几处同时让路、把仅剩的空隙摊薄而整环停住；满环（名额 0）时不让路，照常转。
            int budget = len * SlotsPerCell - total;
            int w = 0;
            for (int idx = 0; idx < len; idx++)
            {
                int c = RingCells[start + idx];
                int cnt = Count[c];
                int b = c * S;
                int v = Speed(c);
                // 这一格的头一件要跨进环上的下一格：那一格是汇入点且轮到另一路（有货）、还有让路名额时让路。
                int nr = RingCells[start + (idx + 1) % len];
                bool yield = cnt > 0 && budget > 0 && WantsYield(nr, c);
                if (yield)
                {
                    budget--;
                }
                for (int k = cnt - 1; k >= 0; k--)
                {
                    long X0 = (long)idx * CL + Pos[b + k];
                    oldX[w] = X0;
                    long a = X0 + v;
                    long cp = long.MaxValue;
                    if (k == 0 && yield)
                    {
                        // 让路：停在入口后正好一个间距处——空隙攒到 2 个间距时支线就能插进去（再多停 1 单位就要 2 个间距 + 1，环差一件满时永远凑不够）。
                        cp = (long)idx * CL + CL - Spacing;
                    }
                    if (a > cp)
                    {
                        a = cp;
                        capped[w] = 1;
                    }
                    want[w] = X0 + v;
                    cap[w] = a;
                    items[w] = Item[b + k];
                    oldIdx[w] = idx;
                    w++;
                }
            }

            int n = total;
            long sp = Spacing;
            x[n - 1] = math.max(oldX[n - 1], math.min(cap[n - 1], cap[0] + L - sp));
            for (int k = n - 2; k >= 0; k--)
            {
                x[k] = math.max(oldX[k], math.min(cap[k], x[k + 1] - sp));
            }
            for (int pass = 0; pass < 2; pass++)
            {
                long lim = x[0] + L - sp;
                if (x[n - 1] <= lim)
                {
                    break;
                }
                x[n - 1] = math.max(oldX[n - 1], lim);
                for (int k = n - 2; k >= 0; k--)
                {
                    long nv = math.max(oldX[k], math.min(x[k], x[k + 1] - sp));
                    if (nv == x[k])
                    {
                        break;
                    }
                    x[k] = nv;
                }
            }

            // 堵塞原因（每格按它的头一件）：物品在环坐标里按格升序，头一件 = 这一格最后写入的那件。
            for (int k = 0; k < len; k++)
            {
                Block[RingCells[start + k]] = 0;
            }
            for (int j = 0; j < n; j++)
            {
                bool isHead = j == n - 1 || oldIdx[j + 1] != oldIdx[j];
                if (isHead && x[j] < want[j])
                {
                    Block[RingCells[start + oldIdx[j]]] = capped[j] != 0 && x[j] == cap[j]
                        ? (byte)BeltBlock.MergeWait
                        : (byte)BeltBlock.DownstreamFull;
                }
            }

            for (int k = 0; k < len; k++)
            {
                Count[RingCells[start + k]] = 0;
            }
            for (int j = 0; j < n; j++)
            {
                long nxp = x[j];
                if (nxp >= L)
                {
                    nxp -= L;
                }
                int idx = (int)(nxp / CL);
                int pos = (int)(nxp - (long)idx * CL);
                int c = RingCells[start + idx];
                int cnt = Count[c];
                if (cnt >= S)
                {
                    // 理论上不会发生（间距保证每格 ≤ 容量）；万一发生，留在原格原位，不丢物品，并计数供自检发现。
                    Counters[BeltCounters.CapacityViolations]++;
                    idx = oldIdx[j];
                    c = RingCells[start + idx];
                    pos = (int)(oldX[j] - (long)idx * CL);
                    cnt = Count[c];
                    x[j] = oldX[j];
                }
                Pos[c * S + cnt] = pos;
                Item[c * S + cnt] = items[j];
                Delta[c * S + cnt] = (int)(x[j] - oldX[j]);
                Count[c] = (byte)(cnt + 1);
                if (idx != oldIdx[j])
                {
                    int from = RingCells[start + oldIdx[j]];
                    Flow[from]++;
                    Counters[BeltCounters.Transfers]++;
                    AdvanceTurn(c, from);
                }
            }
            // 每格内按位置降序（头一件在前）。
            for (int k = 0; k < len; k++)
            {
                SortCell(RingCells[start + k]);
            }

            oldX.Dispose();
            want.Dispose();
            cap.Dispose();
            x.Dispose();
            items.Dispose();
            oldIdx.Dispose();
            capped.Dispose();
        }

        private void SortCell(int c)
        {
            int cnt = Count[c];
            int b = c * S;
            for (int i = 1; i < cnt; i++)
            {
                int p = Pos[b + i];
                ushort it = Item[b + i];
                int d = Delta[b + i];
                int j = i - 1;
                while (j >= 0 && Pos[b + j] < p)
                {
                    Pos[b + j + 1] = Pos[b + j];
                    Item[b + j + 1] = Item[b + j];
                    Delta[b + j + 1] = Delta[b + j];
                    j--;
                }
                Pos[b + j + 1] = p;
                Item[b + j + 1] = it;
                Delta[b + j + 1] = d;
            }
        }

        /// <summary>输出端口：到了间隔、还有货、所在格入口有空位，就在入口（位置 0）放上一件。</summary>
        private void Emit()
        {
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort port = Ports[p];
                if (port.Kind != (byte)BeltPortKind.Source || port.Pending == 0)
                {
                    continue;
                }
                if (port.Phase < port.Interval)
                {
                    port.Phase++;
                }
                if (port.Phase < port.Interval)
                {
                    Ports[p] = port;
                    continue;
                }
                int c = port.Cell;
                bool ok = c >= 0;
                if (ok)
                {
                    int cnt = Count[c];
                    // 入口（位置 0）要空出一个间距，且与车道上紧跟其后的那一路保持间距（环满时放不进）。
                    ok = MaxInsertPos(c) >= 0 && MinInsertPos(c, -1) == 0;
                    if (ok)
                    {
                        Pos[c * S + cnt] = 0;
                        Item[c * S + cnt] = port.Item;
                        Delta[c * S + cnt] = 0;
                        Count[c] = (byte)(cnt + 1);
                        NoteEnter(c);
                    }
                }
                if (ok)
                {
                    port.Phase = 0;
                    port.Total++;
                    port.WindowAcc++;
                    if (port.Pending > 0)
                    {
                        port.Pending--;
                    }
                    Counters[BeltCounters.Emitted]++;
                    BeltNetAgg a = Nets[Net[c]];
                    a.EmitAcc++;
                    Nets[Net[c]] = a;
                }
                else
                {
                    port.BlockedSteps++;
                }
                Ports[p] = port;
            }
        }

        /// <summary>输入端口：有缓存上限且设了消耗间隔的，按间隔消耗一件（模拟建筑取料；不设间隔的由建筑经 API 取）。</summary>
        private void Consume()
        {
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort port = Ports[p];
                if (port.Kind != (byte)BeltPortKind.Sink || port.Cap < 0 || port.Interval <= 0 || port.Buffered <= 0)
                {
                    continue;
                }
                port.Phase++;
                if (port.Phase >= port.Interval)
                {
                    port.Phase = 0;
                    port.Buffered--;
                    port.Consumed++;
                }
                Ports[p] = port;
            }
        }

        private void RollBuckets()
        {
            long done = Counters[BeltCounters.CompletedBuckets];
            int slot = (int)(done % BeltConst.Buckets);
            int n = Count.Length;
            for (int i = 0; i < n; i++)
            {
                FlowB[i * BeltConst.Buckets + slot] = Flow[i];
                Flow[i] = 0;
            }
            for (int nId = 0; nId < Nets.Length; nId++)
            {
                BeltNetAgg a = Nets[nId];
                switch (slot)
                {
                    case 0: a.D0 = a.DelAcc; a.E0 = a.EmitAcc; break;
                    case 1: a.D1 = a.DelAcc; a.E1 = a.EmitAcc; break;
                    case 2: a.D2 = a.DelAcc; a.E2 = a.EmitAcc; break;
                    default: a.D3 = a.DelAcc; a.E3 = a.EmitAcc; break;
                }
                a.DelAcc = 0;
                a.EmitAcc = 0;
                Nets[nId] = a;
            }
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort port = Ports[p];
                switch (slot)
                {
                    case 0: port.W0 = port.WindowAcc; break;
                    case 1: port.W1 = port.WindowAcc; break;
                    case 2: port.W2 = port.WindowAcc; break;
                    default: port.W3 = port.WindowAcc; break;
                }
                port.WindowAcc = 0;
                Ports[p] = port;
            }
            Counters[BeltCounters.CompletedBuckets] = done + 1;
        }
    }

    /// <summary>渲染缓冲：每格一个实例、每件物品一个实例（世界坐标 = 格坐标 × 格边长；格中心在整数坐标）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct JobBeltRender : IJob
    {
        public int CL;
        public float CellSize;
        public float StepHz;
        public int SlotsPerCell;
        public int V0;
        public int V1;
        public int V2;
        /// <summary>1 = 同时写物品实例；0 = 只写格实例（远景流动贴图不画物品）。</summary>
        public int WriteItems;
        [ReadOnly] public NativeArray<int> X;
        [ReadOnly] public NativeArray<int> Y;
        [ReadOnly] public NativeArray<byte> Dir;
        [ReadOnly] public NativeArray<byte> Tier;
        [ReadOnly] public NativeArray<byte> Count;
        [ReadOnly] public NativeArray<byte> Block;
        [ReadOnly] public NativeArray<int> Pos;
        [ReadOnly] public NativeArray<int> Delta;
        [ReadOnly] public NativeArray<ushort> Item;
        public NativeArray<BeltInstance> Cells;
        public NativeArray<BeltInstance> Items;
        public NativeArray<int> OutCounts;

        public void Execute()
        {
            int n = X.Length;
            int w = 0;
            float inv = 1f / CL;
            for (int i = 0; i < n; i++)
            {
                int d = Dir[i];
                float dx = BeltDirs.Dx(d);
                float dz = BeltDirs.Dy(d);
                float cx = X[i] * CellSize;
                float cz = Y[i] * CellSize;
                byte t = Tier[i];
                int v = t == 0 ? V0 : t == 1 ? V1 : V2;
                int cnt = Count[i];
                Cells[i] = new BeltInstance
                {
                    A = new float4(cx, cz, dx, dz),
                    B = new float4(v * StepHz * inv, cnt / (float)SlotsPerCell, Block[i], t),
                };
                if (WriteItems == 0)
                {
                    continue;
                }
                int b = i * BeltConst.MaxSlots;
                for (int k = 0; k < cnt && w < Items.Length; k++)
                {
                    float along = (Pos[b + k] * inv - 0.5f) * CellSize;
                    float moved = Delta[b + k] * inv * CellSize;
                    Items[w++] = new BeltInstance
                    {
                        A = new float4(cx + dx * along, cz + dz * along, dx * moved, dz * moved),
                        B = new float4(Item[b + k], 0f, 0f, 0f),
                    };
                }
            }
            OutCounts[0] = n;
            OutCounts[1] = w;
        }
    }

    /// <summary>状态哈希（FNV-1a 64 位）：步数、账本、每格（坐标、朝向、等级、轮次、物品位置与种类）、每个端口的状态。
    /// 不含统计窗口和堵塞标记（都是派生量）。用于确定性回放、存读档与后台一致性的逐位比对。</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct JobBeltHash : IJob
    {
        [ReadOnly] public NativeArray<int> X;
        [ReadOnly] public NativeArray<int> Y;
        [ReadOnly] public NativeArray<byte> Dir;
        [ReadOnly] public NativeArray<byte> Tier;
        [ReadOnly] public NativeArray<byte> Count;
        [ReadOnly] public NativeArray<byte> Turn;
        [ReadOnly] public NativeArray<int> Pos;
        [ReadOnly] public NativeArray<ushort> Item;
        [ReadOnly] public NativeArray<BeltPort> Ports;
        [ReadOnly] public NativeArray<long> Counters;
        public NativeArray<ulong> Out;

        private ulong _h;

        public void Execute()
        {
            _h = 14695981039346656037UL;
            Mix(Counters[BeltCounters.StepIndex]);
            Mix(Counters[BeltCounters.Emitted]);
            Mix(Counters[BeltCounters.Inserted]);
            Mix(Counters[BeltCounters.Delivered]);
            Mix(Counters[BeltCounters.Removed]);
            int n = X.Length;
            Mix(n);
            for (int i = 0; i < n; i++)
            {
                Mix(X[i]);
                Mix(Y[i]);
                Mix(Dir[i] | (Tier[i] << 8) | (Turn[i] << 16));
                int cnt = Count[i];
                Mix(cnt);
                int b = i * BeltConst.MaxSlots;
                for (int k = 0; k < cnt; k++)
                {
                    Mix(Pos[b + k]);
                    Mix(Item[b + k]);
                }
            }
            Mix(Ports.Length);
            for (int p = 0; p < Ports.Length; p++)
            {
                BeltPort bp = Ports[p];
                Mix(bp.Id);
                Mix(bp.Kind);
                Mix(bp.X);
                Mix(bp.Y);
                Mix(bp.Item);
                Mix(bp.Interval);
                Mix(bp.Phase);
                Mix(bp.Pending);
                Mix(bp.Buffered);
                Mix(bp.Cap);
                Mix(bp.Total);
                Mix(bp.Consumed);
                Mix(bp.BlockedSteps);
            }
            Out[0] = _h;
        }

        private void Mix(long v)
        {
            ulong u = (ulong)v;
            for (int i = 0; i < 8; i++)
            {
                _h ^= (u >> (i * 8)) & 0xFF;
                _h *= 1099511628211UL;
            }
        }
    }
}
