using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Nav
{
    /// <summary>一个区块在某个移动类别下的抽象图（入口节点在节点池里的范围）。</summary>
    public struct NavChunkGraph
    {
        public int Built;
        public int NodeStart;
        public int NodeCount;
    }

    /// <summary>抽象图节点：区块边界上的一个入口格。区块内的边在第一次展开时才算（懒计算），区块失效后整组作废。</summary>
    public struct NavNode
    {
        public int2 Cell;
        public int Slot;
        public int Class;
        public int EdgeStart;
        /// <summary>-1 = 区块内的边还没算。</summary>
        public int EdgeCount;
    }

    public struct NavEdge
    {
        public int To;
        public int Cost;
    }

    /// <summary>工作副本（只在寻路作业里读写）：通行格网 + 各区块的抽象图缓存。缓存是格网内容的纯函数，与构建顺序无关。</summary>
    public struct NavWork : IDisposable
    {
        public NavGrid Grid;
        public NativeList<NavChunkGraph> Graphs;
        public NativeList<NavNode> Nodes;
        public NativeList<NavEdge> Edges;
        public NativeArray<NavCounters> Counters;

        public static NavWork Create(NavGrid grid) => new NavWork
        {
            Grid = grid,
            Graphs = new NativeList<NavChunkGraph>(256, Allocator.Persistent),
            Nodes = new NativeList<NavNode>(1024, Allocator.Persistent),
            Edges = new NativeList<NavEdge>(4096, Allocator.Persistent),
            Counters = new NativeArray<NavCounters>(1, Allocator.Persistent),
        };

        public bool IsCreated => Graphs.IsCreated;

        public void Dispose()
        {
            Grid.Dispose();
            if (Graphs.IsCreated)
            {
                Graphs.Dispose();
            }
            if (Nodes.IsCreated)
            {
                Nodes.Dispose();
            }
            if (Edges.IsCreated)
            {
                Edges.Dispose();
            }
            if (Counters.IsCreated)
            {
                Counters.Dispose();
            }
        }
    }

    /// <summary>最小堆的一项：按 (F, H, TieY, TieX, Item) 字典序——并列时按格子坐标决胜，与节点在池里的序号（构建顺序）无关。</summary>
    public struct NavHeapItem
    {
        public int F;
        public int H;
        public int TieY;
        public int TieX;
        public int Item;
        public int G;

        public bool Less(in NavHeapItem o)
        {
            if (F != o.F)
            {
                return F < o.F;
            }
            if (H != o.H)
            {
                return H < o.H;
            }
            if (TieY != o.TieY)
            {
                return TieY < o.TieY;
            }
            if (TieX != o.TieX)
            {
                return TieX < o.TieX;
            }
            return Item < o.Item;
        }
    }

    /// <summary>一次批处理的临时缓冲（Allocator.Temp，作业内分配）。</summary>
    public struct NavScratch : IDisposable
    {
        public NativeArray<int> Dist;
        public NativeArray<int> Parent;
        public NativeArray<int> StartDist;
        public NativeArray<int> GoalDist;
        /// <summary>抽象图 A* 的开放表。</summary>
        public NativeList<NavHeapItem> Heap;
        /// <summary>区块内 Dijkstra / 局部 A* 的堆（与抽象开放表分开：抽象搜索途中懒计算区块内边会调用区块内 Dijkstra）。</summary>
        public NativeList<NavHeapItem> LocalHeap;
        public NativeHashMap<int, int> G;
        public NativeHashMap<int, int> From;
        public NativeHashSet<int> Closed;
        public NativeList<int> AbsPath;
        public NativeList<int2> Cells;
        public NativeList<int2> Tmp;
        /// <summary>目标一侧封闭判断的泛洪（见 NavSearch.GoalEnclosedInBox；与 A* 的 G / From / Closed 分开，部分路线还要用它们）。</summary>
        public NativeHashSet<int> FloodSeen;
        public NativeList<int> FloodQueue;

        public static NavScratch Create(int cells, Allocator a) => new NavScratch
        {
            FloodSeen = new NativeHashSet<int>(256, a),
            FloodQueue = new NativeList<int>(256, a),
            Dist = new NativeArray<int>(cells, a),
            Parent = new NativeArray<int>(cells, a),
            StartDist = new NativeArray<int>(cells, a),
            GoalDist = new NativeArray<int>(cells, a),
            Heap = new NativeList<NavHeapItem>(1024, a),
            LocalHeap = new NativeList<NavHeapItem>(1024, a),
            G = new NativeHashMap<int, int>(1024, a),
            From = new NativeHashMap<int, int>(1024, a),
            Closed = new NativeHashSet<int>(1024, a),
            AbsPath = new NativeList<int>(256, a),
            Cells = new NativeList<int2>(1024, a),
            Tmp = new NativeList<int2>(256, a),
        };

        public void Dispose()
        {
            Dist.Dispose();
            Parent.Dispose();
            StartDist.Dispose();
            GoalDist.Dispose();
            Heap.Dispose();
            LocalHeap.Dispose();
            G.Dispose();
            From.Dispose();
            Closed.Dispose();
            AbsPath.Dispose();
            Cells.Dispose();
            Tmp.Dispose();
            FloodSeen.Dispose();
            FloodQueue.Dispose();
        }
    }

    /// <summary>
    /// FG0-ARCH-06（FGR-ARC-015）：层级寻路。
    ///
    /// 1. 区块连通图：每个区块、每个移动类别一组入口节点——相邻两区块边界上“两侧都能走”的连续段，短段取中点、长段取两端（再长加中点）；
    ///    两个区块从各自一侧算出的入口一一对应（同一段、同一位置）。区块内两入口之间的代价在节点第一次被展开时用区块内 Dijkstra 算出（懒计算）。
    /// 2. 查询：起终点先各自就近落到可走格；同区块先试局部 A*；否则起点 / 终点各做一次区块内 Dijkstra 接入抽象图，在抽象图上 A*
    ///    （搜索范围 = 起终点区块外接矩形 + 边距；展开数有上限）。
    /// 3. 细化：抽象路线的每一段在所在区块内做局部 A*，跨区块的一步直接相连；再按视线（超覆盖直线，斜穿格角要求两侧都能走）拉直成路点。
    /// 4. 确定性：堆并列时按格坐标决胜；邻居枚举顺序固定；区块按需生成是纯函数——同一请求在同一格网上，冷缓存与热缓存、任何构建顺序得到逐格相同的路线。
    /// 代价 = 两格代价倍率之和 × 5（直走）/ × 7（斜走），对称、整数。
    /// </summary>
    public static class NavSearch
    {
        /// <summary>8 个方向（固定顺序：东、北、西、南，再四个斜向）。邻居枚举顺序是确定性的一部分。</summary>
        public static int2 Dir8(int k)
        {
            switch (k)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(0, 1);
                case 2: return new int2(-1, 0);
                case 3: return new int2(0, -1);
                case 4: return new int2(1, 1);
                case 5: return new int2(-1, 1);
                case 6: return new int2(-1, -1);
                default: return new int2(1, -1);
            }
        }

        public static int Octile(int2 a, int2 b)
        {
            int dx = math.abs(a.x - b.x);
            int dy = math.abs(a.y - b.y);
            return NavConst.Ortho * math.max(dx, dy) + (NavConst.Diag - NavConst.Ortho) * math.min(dx, dy);
        }

        public static int StepCost(byte ca, byte cb, bool diag) =>
            (NavGridOps.CostOf(ca) + NavGridOps.CostOf(cb)) * (diag ? NavConst.Diag / 2 : NavConst.Ortho / 2);

        private static int2 ChunkOf(int2 c, int size) => new int2(NavGridOps.FloorDiv(c.x, size), NavGridOps.FloorDiv(c.y, size));

        // ─────────────────────────────── 堆 ───────────────────────────────

        public static void HeapPush(NativeList<NavHeapItem> h, in NavHeapItem it)
        {
            h.Add(it);
            int i = h.Length - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                NavHeapItem pi = h[p];
                if (!it.Less(pi))
                {
                    break;
                }
                h[i] = pi;
                i = p;
            }
            h[i] = it;
        }

        public static NavHeapItem HeapPop(NativeList<NavHeapItem> h)
        {
            NavHeapItem top = h[0];
            NavHeapItem last = h[h.Length - 1];
            h.RemoveAt(h.Length - 1);
            int n = h.Length;
            if (n == 0)
            {
                return top;
            }
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= n)
                {
                    break;
                }
                int r = l + 1;
                int c = r < n && h[r].Less(h[l]) ? r : l;
                if (!h[c].Less(last))
                {
                    break;
                }
                h[i] = h[c];
                i = c;
            }
            h[i] = last;
            return top;
        }

        // ─────────────────────────────── 区块图 ───────────────────────────────

        private static void EnsureGraphCapacity(ref NavWork w, int slot)
        {
            int need = (slot + 1) * NavConst.MaxClasses;
            while (w.Graphs.Length < need)
            {
                w.Graphs.Add(default);
            }
        }

        /// <summary>区块内某格在该类别下能不能走（不触发生成：槽位必须已存在）。</summary>
        private static bool LocalPass(ref NavWork w, int slot, int lx, int ly, int cls) =>
            !NavGridOps.IsBlockedClass(w.Grid.Cell[slot * w.Grid.Cells + ly * w.Grid.ChunkSize + lx], cls);

        /// <summary>把区块及其四邻的抽象图标记为失效（格网内容变化后，下一次用到时重建）。</summary>
        public static void InvalidateChunk(ref NavWork w, int cx, int cy)
        {
            for (int k = 0; k < 5; k++)
            {
                int2 d = k == 0 ? int2.zero : Dir8(k - 1);
                if (!w.Grid.Slots.TryGetValue(NavGridOps.Key(cx + d.x, cy + d.y), out int slot))
                {
                    continue;
                }
                EnsureGraphCapacity(ref w, slot);
                for (int c = 0; c < NavConst.MaxClasses; c++)
                {
                    w.Graphs[slot * NavConst.MaxClasses + c] = default;
                }
            }
        }

        /// <summary>抽象图缓存全部清空（垃圾节点过多时；只在没有作业在飞时调用）。</summary>
        public static void ResetGraphs(ref NavWork w)
        {
            w.Graphs.Clear();
            w.Nodes.Clear();
            w.Edges.Clear();
            NavCounters c = w.Counters[0];
            c.GraphResets++;
            w.Counters[0] = c;
        }

        /// <summary>保证区块在该类别下的入口节点已建好，返回槽位（地形未知返回 -1）。</summary>
        public static int EnsureChunkGraph(ref NavWork w, int cx, int cy, int cls)
        {
            int slot = NavGridOps.EnsureSlot(ref w.Grid, cx, cy);
            if (slot < 0)
            {
                return -1;
            }
            EnsureGraphCapacity(ref w, slot);
            int gi = slot * NavConst.MaxClasses + cls;
            if (w.Graphs[gi].Built == 1)
            {
                return slot;
            }
            int size = w.Grid.ChunkSize;
            int start = w.Nodes.Length;
            int bx = cx * size;
            int by = cy * size;
            for (int side = 0; side < 4; side++)
            {
                int2 d = side == 0 ? new int2(0, -1) : side == 1 ? new int2(1, 0) : side == 2 ? new int2(0, 1) : new int2(-1, 0);
                int nslot = NavGridOps.EnsureSlot(ref w.Grid, cx + d.x, cy + d.y);
                // 邻区块生成可能让格网列表扩容：本区块的槽位号不变，按下标取即可。
                int runStart = -1;
                for (int i = 0; i <= size; i++)
                {
                    bool ok = false;
                    if (i < size && nslot >= 0)
                    {
                        int ax, ay, qx, qy;
                        switch (side)
                        {
                            case 0: ax = i; ay = 0; qx = i; qy = size - 1; break;
                            case 1: ax = size - 1; ay = i; qx = 0; qy = i; break;
                            case 2: ax = i; ay = size - 1; qx = i; qy = 0; break;
                            default: ax = 0; ay = i; qx = size - 1; qy = i; break;
                        }
                        ok = LocalPass(ref w, slot, ax, ay, cls) && LocalPass(ref w, nslot, qx, qy, cls);
                    }
                    if (ok && runStart < 0)
                    {
                        runStart = i;
                    }
                    else if (!ok && runStart >= 0)
                    {
                        EmitRun(ref w, slot, cls, start, side, bx, by, size, runStart, i - 1);
                        runStart = -1;
                    }
                }
            }
            w.Graphs[gi] = new NavChunkGraph { Built = 1, NodeStart = start, NodeCount = w.Nodes.Length - start };
            NavCounters c = w.Counters[0];
            c.ChunkGraphsBuilt++;
            w.Counters[0] = c;
            return slot;
        }

        private static void EmitRun(ref NavWork w, int slot, int cls, int nodeStart, int side, int bx, int by, int size, int a, int b)
        {
            int len = b - a + 1;
            if (len <= 5)
            {
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, (a + b) / 2);
            }
            else if (len <= 16)
            {
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, a);
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, b);
            }
            else
            {
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, a);
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, (a + b) / 2);
                AddEntrance(ref w, slot, cls, nodeStart, side, bx, by, size, b);
            }
        }

        private static void AddEntrance(ref NavWork w, int slot, int cls, int nodeStart, int side, int bx, int by, int size, int i)
        {
            int2 cell;
            switch (side)
            {
                case 0: cell = new int2(bx + i, by); break;
                case 1: cell = new int2(bx + size - 1, by + i); break;
                case 2: cell = new int2(bx + i, by + size - 1); break;
                default: cell = new int2(bx, by + i); break;
            }
            for (int k = nodeStart; k < w.Nodes.Length; k++)
            {
                if (w.Nodes[k].Cell.x == cell.x && w.Nodes[k].Cell.y == cell.y)
                {
                    return; // 区块角上的格子可能同时是两条边的入口：只算一个节点。
                }
            }
            w.Nodes.Add(new NavNode { Cell = cell, Slot = slot, Class = cls, EdgeStart = 0, EdgeCount = -1 });
        }

        /// <summary>区块里位于某格的入口节点（没有返回 -1）。</summary>
        public static int FindNode(ref NavWork w, int slot, int cls, int2 cell)
        {
            NavChunkGraph g = w.Graphs[slot * NavConst.MaxClasses + cls];
            for (int k = g.NodeStart; k < g.NodeStart + g.NodeCount; k++)
            {
                NavNode n = w.Nodes[k];
                if (n.Cell.x == cell.x && n.Cell.y == cell.y)
                {
                    return k;
                }
            }
            return -1;
        }

        private static void EnsureEdges(ref NavWork w, ref NavScratch sc, int nodeIndex)
        {
            NavNode node = w.Nodes[nodeIndex];
            if (node.EdgeCount >= 0)
            {
                return;
            }
            LocalDijkstra(ref w, ref sc, node.Slot, node.Class, node.Cell, sc.Dist);
            NavChunkGraph g = w.Graphs[node.Slot * NavConst.MaxClasses + node.Class];
            int size = w.Grid.ChunkSize;
            int2 cb = ChunkOf(node.Cell, size) * size;
            int start = w.Edges.Length;
            int count = 0;
            for (int k = g.NodeStart; k < g.NodeStart + g.NodeCount; k++)
            {
                if (k == nodeIndex)
                {
                    continue;
                }
                int2 c = w.Nodes[k].Cell - cb;
                int d = sc.Dist[c.y * size + c.x];
                if (d < NavConst.Infinity)
                {
                    w.Edges.Add(new NavEdge { To = k, Cost = d });
                    count++;
                }
            }
            node.EdgeStart = start;
            node.EdgeCount = count;
            w.Nodes[nodeIndex] = node;
            NavCounters cn = w.Counters[0];
            cn.EdgeSetsBuilt++;
            w.Counters[0] = cn;
        }

        // ─────────────────────────────── 区块内搜索 ───────────────────────────────

        /// <summary>区块内（8 向、斜走不切角）从一格出发的最短代价，写入 <paramref name="dist"/>（按区块内下标）。起点走不了时全部为无穷。</summary>
        public static void LocalDijkstra(ref NavWork w, ref NavScratch sc, int slot, int cls, int2 from, NativeArray<int> dist)
        {
            int size = w.Grid.ChunkSize;
            int n = w.Grid.Cells;
            for (int i = 0; i < n; i++)
            {
                dist[i] = NavConst.Infinity;
            }
            int2 cb = ChunkOf(from, size) * size;
            int2 l0 = from - cb;
            if (!LocalPass(ref w, slot, l0.x, l0.y, cls))
            {
                return;
            }
            int baseI = slot * n;
            sc.LocalHeap.Clear();
            int s0 = l0.y * size + l0.x;
            dist[s0] = 0;
            HeapPush(sc.LocalHeap, new NavHeapItem { F = 0, TieY = l0.y, TieX = l0.x, Item = s0 });
            while (sc.LocalHeap.Length > 0)
            {
                NavHeapItem it = HeapPop(sc.LocalHeap);
                int i = it.Item;
                if (it.F > dist[i])
                {
                    continue;
                }
                int x = i % size;
                int y = i / size;
                byte ci = w.Grid.Cell[baseI + i];
                for (int k = 0; k < 8; k++)
                {
                    int2 d = Dir8(k);
                    int nx = x + d.x;
                    int ny = y + d.y;
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                    {
                        continue;
                    }
                    byte cj = w.Grid.Cell[baseI + ny * size + nx];
                    if (NavGridOps.IsBlockedClass(cj, cls))
                    {
                        continue;
                    }
                    bool diag = k >= 4;
                    if (diag && (NavGridOps.IsBlockedClass(w.Grid.Cell[baseI + y * size + nx], cls)
                                 || NavGridOps.IsBlockedClass(w.Grid.Cell[baseI + ny * size + x], cls)))
                    {
                        continue;
                    }
                    int j = ny * size + nx;
                    int nd = it.F + StepCost(ci, cj, diag);
                    if (nd < dist[j])
                    {
                        dist[j] = nd;
                        HeapPush(sc.LocalHeap, new NavHeapItem { F = nd, TieY = ny, TieX = nx, Item = j });
                    }
                }
            }
        }

        /// <summary>区块内局部 A*：从 <paramref name="a"/> 到 <paramref name="b"/>（同一区块），成功时把路线（不含 a、含 b）追加到 <paramref name="into"/>。</summary>
        public static bool LocalAStar(ref NavWork w, ref NavScratch sc, int slot, int cls, int2 a, int2 b, NativeList<int2> into)
        {
            if (a.x == b.x && a.y == b.y)
            {
                return true;
            }
            int size = w.Grid.ChunkSize;
            int n = w.Grid.Cells;
            int2 cb = ChunkOf(a, size) * size;
            int2 la = a - cb;
            int2 lb = b - cb;
            if (lb.x < 0 || lb.y < 0 || lb.x >= size || lb.y >= size)
            {
                return false;
            }
            if (!LocalPass(ref w, slot, la.x, la.y, cls) || !LocalPass(ref w, slot, lb.x, lb.y, cls))
            {
                return false;
            }
            for (int i = 0; i < n; i++)
            {
                sc.Dist[i] = NavConst.Infinity;
                sc.Parent[i] = -1;
            }
            int baseI = slot * n;
            int s0 = la.y * size + la.x;
            int t0 = lb.y * size + lb.x;
            sc.Dist[s0] = 0;
            sc.LocalHeap.Clear();
            int h0 = Octile(la, lb);
            HeapPush(sc.LocalHeap, new NavHeapItem { F = h0, H = h0, TieY = la.y, TieX = la.x, Item = s0, G = 0 });
            bool found = false;
            while (sc.LocalHeap.Length > 0)
            {
                NavHeapItem it = HeapPop(sc.LocalHeap);
                int i = it.Item;
                if (it.G > sc.Dist[i])
                {
                    continue;
                }
                if (i == t0)
                {
                    found = true;
                    break;
                }
                int x = i % size;
                int y = i / size;
                byte ci = w.Grid.Cell[baseI + i];
                for (int k = 0; k < 8; k++)
                {
                    int2 d = Dir8(k);
                    int nx = x + d.x;
                    int ny = y + d.y;
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                    {
                        continue;
                    }
                    byte cj = w.Grid.Cell[baseI + ny * size + nx];
                    if (NavGridOps.IsBlockedClass(cj, cls))
                    {
                        continue;
                    }
                    bool diag = k >= 4;
                    if (diag && (NavGridOps.IsBlockedClass(w.Grid.Cell[baseI + y * size + nx], cls)
                                 || NavGridOps.IsBlockedClass(w.Grid.Cell[baseI + ny * size + x], cls)))
                    {
                        continue;
                    }
                    int j = ny * size + nx;
                    int ng = it.G + StepCost(ci, cj, diag);
                    if (ng < sc.Dist[j])
                    {
                        sc.Dist[j] = ng;
                        sc.Parent[j] = i;
                        int h = Octile(new int2(nx, ny), lb);
                        HeapPush(sc.LocalHeap, new NavHeapItem { F = ng + h, H = h, TieY = ny, TieX = nx, Item = j, G = ng });
                    }
                }
            }
            if (!found)
            {
                return false;
            }
            sc.Tmp.Clear();
            int cur = t0;
            while (cur != s0 && cur >= 0)
            {
                sc.Tmp.Add(cb + new int2(cur % size, cur / size));
                cur = sc.Parent[cur];
            }
            for (int k = sc.Tmp.Length - 1; k >= 0; k--)
            {
                into.Add(sc.Tmp[k]);
            }
            return true;
        }

        // ─────────────────────────────── 起终点 ───────────────────────────────

        /// <summary>格子能走就是它；否则在半径内找欧氏距离最近的可走格（并列取 y 小、再 x 小）。</summary>
        public static bool Resolve(ref NavGrid g, int2 c, int cls, int radius, out int2 result)
        {
            if (NavGridOps.Passable(ref g, c, cls))
            {
                result = c;
                return true;
            }
            result = c;
            int best = int.MaxValue;
            bool found = false;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    int d2 = dx * dx + dy * dy;
                    if (d2 > best)
                    {
                        continue;
                    }
                    var q = new int2(c.x + dx, c.y + dy);
                    if (!NavGridOps.Passable(ref g, q, cls))
                    {
                        continue;
                    }
                    if (d2 < best || (d2 == best && (q.y < result.y || (q.y == result.y && q.x < result.x))))
                    {
                        best = d2;
                        result = q;
                        found = true;
                    }
                }
            }
            return found;
        }

        // ─────────────────────────────── 视线与平滑 ───────────────────────────────

        /// <summary>两格格心连线经过的全部格子（超覆盖：正好穿过格角时两侧的格子都算）都能走。</summary>
        public static bool LineClear(ref NavGrid g, int2 a, int2 b, int cls)
        {
            int dx = math.abs(b.x - a.x);
            int dy = math.abs(b.y - a.y);
            int sx = b.x > a.x ? 1 : -1;
            int sy = b.y > a.y ? 1 : -1;
            int x = a.x;
            int y = a.y;
            int n = 1 + dx + dy;
            int err = dx - dy;
            dx *= 2;
            dy *= 2;
            for (; n > 0; n--)
            {
                if (!NavGridOps.Passable(ref g, x, y, cls))
                {
                    return false;
                }
                if (n == 1)
                {
                    // 已到终点格：不再往外走（否则同格或正好 45° 时会多查终点外侧两格，误判被挡）。
                    break;
                }
                if (err > 0)
                {
                    x += sx;
                    err -= dy;
                }
                else if (err < 0)
                {
                    y += sy;
                    err += dx;
                }
                else
                {
                    // 正好穿过格角：两侧都要能走（不许斜着挤过两个障碍之间的缝）。
                    if (!NavGridOps.Passable(ref g, x + sx, y, cls) || !NavGridOps.Passable(ref g, x, y + sy, cls))
                    {
                        return false;
                    }
                    x += sx;
                    y += sy;
                    err += dx - dy;
                    n--;
                }
            }
            return true;
        }

        /// <summary>
        /// 把逐格路线（首项 = 起点）拉直成路点（不含起点）。贪心：从锚点向前延伸到第一处视线被挡的前一格。
        /// 前后两段同向共线时并成一段：前瞻上限只为限制视线检测的开销，不该在空地上切出多余的共线路点
        /// （中间点是格心、正好在连线上，两段经过的格子之并 = 整段经过的格子，视线结论不变）。
        /// </summary>
        public static void Smooth(ref NavGrid g, NativeList<int2> cells, int cls, int lookahead, NativeList<int2> into)
        {
            int last = cells.Length - 1;
            int anchor = 0;
            int first = into.Length;
            int2 segStart = cells[0];
            while (anchor < last)
            {
                int j = anchor + 1;
                while (j + 1 <= last && j + 1 - anchor <= lookahead && LineClear(ref g, cells[anchor], cells[j + 1], cls))
                {
                    j++;
                }
                int2 a = cells[anchor];
                int2 p = cells[j];
                if (into.Length > first && SameDirection(segStart, a, p))
                {
                    into[into.Length - 1] = p;
                }
                else
                {
                    into.Add(p);
                    segStart = a;
                }
                anchor = j;
            }
        }

        /// <summary>s→a 与 a→p 共线且同向（a 在 s、p 之间）。</summary>
        private static bool SameDirection(int2 s, int2 a, int2 p)
        {
            long ux = a.x - s.x;
            long uy = a.y - s.y;
            long vx = p.x - a.x;
            long vy = p.y - a.y;
            return ux * vy - uy * vx == 0 && ux * vx + uy * vy > 0;
        }

        public static float PolyLength(int2 start, NativeList<int2> pts, int from, int count)
        {
            float len = 0f;
            int2 prev = start;
            for (int i = from; i < from + count; i++)
            {
                len += math.distance((float2)prev, (float2)pts[i]);
                prev = pts[i];
            }
            return len;
        }

        // ─────────────────────────────── 一条请求 ───────────────────────────────

        private static int H(int2 c, int2 t) => Octile(c, t);

        /// <summary>区块里有没有哪个入口节点从该格可达（<paramref name="dist"/> 是区块内 Dijkstra 的结果）。</summary>
        private static bool AnyEntryReachable(ref NavWork w, int slot, int cls, int2 chunk, int size, NativeArray<int> dist)
        {
            NavChunkGraph cg = w.Graphs[slot * NavConst.MaxClasses + cls];
            int2 cb = chunk * size;
            for (int k = cg.NodeStart; k < cg.NodeStart + cg.NodeCount; k++)
            {
                int2 lc = w.Nodes[k].Cell - cb;
                if (dist[lc.y * size + lc.x] < NavConst.Infinity)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 目标一侧在搜索框里是否自成封闭区域：从目标能走到的本区块入口出发，沿抽象图（区块内边 + 跨区块相邻入口）泛洪；
        /// 走完都没碰到框外的可走格 = 目标所在的连通区域整个在框里。前向搜索已把起点一侧在框里的部分搜空却没碰到目标，
        /// 说明两边不连通，放大搜索框也找不到路（湖心岛、悬崖围死的高台）。超出 <paramref name="budget"/> 或碰到框外可走格 = 下不了结论。
        /// </summary>
        private static bool GoalEnclosedInBox(ref NavWork w, ref NavScratch sc, int tSlot, int cls, int2 tc0, int size,
            int minCx, int maxCx, int minCy, int maxCy, int budget, out int visited)
        {
            // FG3-LOG-09（DEBT-FG0ARCH06-11 ①）：泛洪处理过的节点数交给调用方计入展开数（受 nav.max_expansions 总量约束，也进计数器与性能基线）。
            visited = 0;
            sc.FloodSeen.Clear();
            sc.FloodQueue.Clear();
            NavChunkGraph tg = w.Graphs[tSlot * NavConst.MaxClasses + cls];
            int2 cb = tc0 * size;
            for (int k = tg.NodeStart; k < tg.NodeStart + tg.NodeCount; k++)
            {
                int2 lc = w.Nodes[k].Cell - cb;
                if (sc.GoalDist[lc.y * size + lc.x] < NavConst.Infinity && sc.FloodSeen.Add(k))
                {
                    sc.FloodQueue.Add(k);
                }
            }
            for (int head = 0; head < sc.FloodQueue.Length; head++)
            {
                if (head >= budget)
                {
                    return false;
                }
                visited = head + 1;
                int id = sc.FloodQueue[head];
                EnsureEdges(ref w, ref sc, id);
                NavNode node = w.Nodes[id];
                for (int e = node.EdgeStart; e < node.EdgeStart + node.EdgeCount; e++)
                {
                    int to = w.Edges[e].To;
                    if (sc.FloodSeen.Add(to))
                    {
                        sc.FloodQueue.Add(to);
                    }
                }
                int2 myChunk = ChunkOf(node.Cell, size);
                for (int k = 0; k < 4; k++)
                {
                    int2 c2 = node.Cell + Dir8(k);
                    int2 ch2 = ChunkOf(c2, size);
                    if (ch2.x == myChunk.x && ch2.y == myChunk.y)
                    {
                        continue;
                    }
                    if (ch2.x < minCx || ch2.x > maxCx || ch2.y < minCy || ch2.y > maxCy)
                    {
                        if (NavGridOps.Passable(ref w.Grid, c2, cls))
                        {
                            return false;
                        }
                        continue;
                    }
                    int slot2 = EnsureChunkGraph(ref w, ch2.x, ch2.y, cls);
                    if (slot2 < 0)
                    {
                        continue;
                    }
                    int m = FindNode(ref w, slot2, cls, c2);
                    if (m >= 0 && sc.FloodSeen.Add(m))
                    {
                        sc.FloodQueue.Add(m);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 一条请求的完整寻路。逐格路线写进 <paramref name="sc"/>.Cells（首项 = 落地后的起点），结果状态写进 <paramref name="res"/>；
        /// 抽象路线（S 之后的节点序号，G 用 <see cref="NavConst.NodeGoal"/>）留在 <paramref name="sc"/>.AbsPath 供同批共享。
        /// </summary>
        public static void FindCells(ref NavWork w, ref NavScratch sc, in NavRequest r, in NavConfig cfg, ref NavResult res, out int2 resolvedStart)
        {
            res.Request = r;
            res.Status = NavStatus.Failed;
            res.Reason = NavFailReason.None;
            sc.Cells.Clear();
            sc.AbsPath.Clear();
            resolvedStart = r.Start;
            int cls = r.Class;
            bool partial = (r.Flags & NavRequestFlags.AllowPartial) != 0;
            int size = w.Grid.ChunkSize;
            if (!NavGridOps.InWorld(ref w.Grid, r.Start.x, r.Start.y) || !NavGridOps.InWorld(ref w.Grid, r.Goal.x, r.Goal.y))
            {
                res.Reason = NavFailReason.OutOfWorld;
                return;
            }
            int2 sChunk0 = ChunkOf(r.Start, size);
            if (NavGridOps.EnsureSlot(ref w.Grid, sChunk0.x, sChunk0.y) < 0)
            {
                res.Reason = NavFailReason.UnknownTerrain;
                return;
            }
            if (!Resolve(ref w.Grid, r.Start, cls, cfg.StartSearchRadius, out int2 s))
            {
                res.Reason = NavFailReason.StartBlocked;
                return;
            }
            resolvedStart = s;
            int2 tChunk0 = ChunkOf(r.Goal, size);
            if (NavGridOps.EnsureSlot(ref w.Grid, tChunk0.x, tChunk0.y) < 0)
            {
                res.Reason = NavFailReason.UnknownTerrain;
                return;
            }
            bool goalOk = Resolve(ref w.Grid, r.Goal, cls, cfg.GoalSearchRadius, out int2 t);
            if (!goalOk)
            {
                if (!partial)
                {
                    res.Reason = NavFailReason.GoalBlocked;
                    return;
                }
                t = r.Goal;
            }
            sc.Cells.Add(s);
            if (goalOk && s.x == t.x && s.y == t.y)
            {
                res.Status = NavStatus.Ok;
                res.End = t;
                return;
            }

            int2 sc0 = ChunkOf(s, size);
            int2 tc0 = ChunkOf(t, size);
            int sSlot = EnsureChunkGraph(ref w, sc0.x, sc0.y, cls);
            int tSlot = EnsureChunkGraph(ref w, tc0.x, tc0.y, cls);
            if (sSlot < 0 || tSlot < 0)
            {
                res.Reason = NavFailReason.UnknownTerrain;
                return;
            }
            if (goalOk && sc0.x == tc0.x && sc0.y == tc0.y)
            {
                if (LocalAStar(ref w, ref sc, sSlot, cls, s, t, sc.Cells))
                {
                    res.Status = NavStatus.Ok;
                    res.End = t;
                    return;
                }
            }

            // 抽象搜索。
            int margin = math.max(0, cfg.SearchMarginChunks);
            int minCx = math.min(sc0.x, tc0.x) - margin;
            int maxCx = math.max(sc0.x, tc0.x) + margin;
            int minCy = math.min(sc0.y, tc0.y) - margin;
            int maxCy = math.max(sc0.y, tc0.y) + margin;
            LocalDijkstra(ref w, ref sc, sSlot, cls, s, sc.StartDist);
            if (goalOk)
            {
                LocalDijkstra(ref w, ref sc, tSlot, cls, t, sc.GoalDist);
            }
            // 起点或目标在自己区块里被围住（走不到任何区块入口）时，放大搜索框也没用：不重搜，免得真不可达时白白多搜几轮。
            bool canRetry = AnyEntryReachable(ref w, sSlot, cls, sc0, size, sc.StartDist)
                            && (!goalOk || AnyEntryReachable(ref w, tSlot, cls, tc0, size, sc.GoalDist));
            int hs = H(s, t);
            int best = NavConst.NodeStart;
            int bestH = hs;
            int expanded = 0;
            int floodedTotal = 0;
            bool found = false;
            bool limit = false;
            // 搜索框只是剪枝：框里搜空了但有邻居被框挡掉（clipped），说明可能要绕出框（长河、悬崖），
            // 放大边距重搜，而不是直接报“完全阻断”。展开数跨轮累计，总量仍受 MaxExpansions 约束。
            for (int attempt = 0; ; attempt++)
            {
                bool clipped = false;
                sc.Heap.Clear();
                sc.G.Clear();
                sc.From.Clear();
                sc.Closed.Clear();
                sc.G[NavConst.NodeStart] = 0;
                HeapPush(sc.Heap, new NavHeapItem { F = hs, H = hs, TieY = s.y, TieX = s.x, Item = NavConst.NodeStart, G = 0 });
                best = NavConst.NodeStart;
                bestH = hs;
                while (sc.Heap.Length > 0)
                {
                    NavHeapItem it = HeapPop(sc.Heap);
                    int id = it.Item;
                    if (sc.Closed.Contains(id))
                    {
                        continue;
                    }
                    sc.Closed.Add(id);
                    if (id == NavConst.NodeGoal)
                    {
                        found = true;
                        break;
                    }
                    if (++expanded > cfg.MaxExpansions)
                    {
                        limit = true;
                        break;
                    }
                    if (id != NavConst.NodeStart && it.H < bestH)
                    {
                        bestH = it.H;
                        best = id;
                    }
                    int g0 = it.G;
                    if (id == NavConst.NodeStart)
                    {
                        NavChunkGraph sg = w.Graphs[sSlot * NavConst.MaxClasses + cls];
                        int2 cb = sc0 * size;
                        for (int k = sg.NodeStart; k < sg.NodeStart + sg.NodeCount; k++)
                        {
                            int2 lc = w.Nodes[k].Cell - cb;
                            int d = sc.StartDist[lc.y * size + lc.x];
                            if (d < NavConst.Infinity)
                            {
                                Relax(ref w, ref sc, id, k, g0 + d, t);
                            }
                        }
                        continue;
                    }
                    EnsureEdges(ref w, ref sc, id);
                    NavNode node = w.Nodes[id];
                    for (int e = node.EdgeStart; e < node.EdgeStart + node.EdgeCount; e++)
                    {
                        NavEdge edge = w.Edges[e];
                        Relax(ref w, ref sc, id, edge.To, g0 + edge.Cost, t);
                    }
                    int2 myChunk = ChunkOf(node.Cell, size);
                    for (int k = 0; k < 4; k++)
                    {
                        int2 c2 = node.Cell + Dir8(k);
                        int2 ch2 = ChunkOf(c2, size);
                        if (ch2.x == myChunk.x && ch2.y == myChunk.y)
                        {
                            continue;
                        }
                        if (ch2.x < minCx || ch2.x > maxCx || ch2.y < minCy || ch2.y > maxCy)
                        {
                            clipped = true;
                            continue;
                        }
                        int slot2 = EnsureChunkGraph(ref w, ch2.x, ch2.y, cls);
                        if (slot2 < 0)
                        {
                            continue;
                        }
                        int m = FindNode(ref w, slot2, cls, c2);
                        if (m < 0)
                        {
                            continue;
                        }
                        byte ca = NavGridOps.CellAt(ref w.Grid, node.Cell.x, node.Cell.y);
                        byte cc = NavGridOps.CellAt(ref w.Grid, c2.x, c2.y);
                        Relax(ref w, ref sc, id, m, g0 + StepCost(ca, cc, false), t);
                    }
                    if (goalOk && node.Slot == tSlot)
                    {
                        int2 lc = node.Cell - tc0 * size;
                        int d = sc.GoalDist[lc.y * size + lc.x];
                        if (d < NavConst.Infinity)
                        {
                            RelaxGoal(ref sc, id, g0 + d, t);
                        }
                    }
                }
                if (found || limit || !clipped || !canRetry || attempt >= NavConst.MaxMarginRetries)
                {
                    break;
                }
                // 放大框之前：目标一侧若在框里自成封闭（湖心岛、围死的高台），就是真不可达——不白搜几轮，
                // 也不会因为展开数跨轮累计而把“被完全阻断”报成“超出寻路范围”。
                // FG3-LOG-09（DEBT-FG0ARCH06-11 ①）：泛洪访问的节点计入展开数——“其实可达、要绕出框”时白付的泛洪也受 nav.max_expansions 约束并进计数器。
                int flooded = 0;
                bool enclosed = goalOk && GoalEnclosedInBox(ref w, ref sc, tSlot, cls, tc0, size, minCx, maxCx, minCy, maxCy, NavConst.EnclosureFloodBudget, out flooded);
                expanded += flooded;
                floodedTotal += flooded;
                if (enclosed)
                {
                    break;
                }
                margin = margin * 2 + 4;
                minCx = math.min(sc0.x, tc0.x) - margin;
                maxCx = math.max(sc0.x, tc0.x) + margin;
                minCy = math.min(sc0.y, tc0.y) - margin;
                maxCy = math.max(sc0.y, tc0.y) + margin;
            }
            res.Expanded = expanded;
            NavCounters cn = w.Counters[0];
            cn.Expanded += expanded;
            cn.FloodVisited += floodedTotal;
            w.Counters[0] = cn;

            int endId;
            int2 partialCell = default;
            bool partialExtra = false;
            if (found)
            {
                endId = NavConst.NodeGoal;
                res.Status = NavStatus.Ok;
                res.End = t;
            }
            else
            {
                NavFailReason why = limit ? NavFailReason.SearchLimit : (goalOk ? NavFailReason.Unreachable : NavFailReason.GoalBlocked);
                if (!partial)
                {
                    res.Reason = why;
                    sc.Cells.Clear();
                    return;
                }
                // 部分路线：候选一 = 起点区块里起点能走到的、离目标最近的格；候选二 = “离目标最近的入口”所在区块里从该入口能走到的、离目标最近的格。
                // 取更近的（并列取起点区块：路更短）。不是只停在区块边界的入口上。
                int2 startBest = s;
                int startBestH = H(s, t);
                ScanClosest(ref w, sc.StartDist, sc0, size, t, ref startBest, ref startBestH);
                bool useNode = false;
                int2 nodeBest = default;
                if (best != NavConst.NodeStart)
                {
                    NavNode bn = w.Nodes[best];
                    LocalDijkstra(ref w, ref sc, bn.Slot, cls, bn.Cell, sc.Dist);
                    nodeBest = bn.Cell;
                    int nodeBestH = H(bn.Cell, t);
                    ScanClosest(ref w, sc.Dist, ChunkOf(bn.Cell, size), size, t, ref nodeBest, ref nodeBestH);
                    useNode = nodeBestH < startBestH;
                }
                res.Reason = why;
                if (!useNode)
                {
                    if (startBest.Equals(s))
                    {
                        sc.Cells.Clear();
                        return;
                    }
                    res.Status = NavStatus.Partial;
                    res.End = startBest;
                    if (!LocalAStar(ref w, ref sc, sSlot, cls, s, startBest, sc.Cells))
                    {
                        res.Status = NavStatus.Failed;
                        sc.Cells.Clear();
                    }
                    return;
                }
                res.Status = NavStatus.Partial;
                res.End = nodeBest;
                endId = best;
                partialCell = nodeBest;
                partialExtra = !nodeBest.Equals(w.Nodes[best].Cell);
            }

            // 抽象路线（倒序取出再翻转）。
            sc.AbsPath.Clear();
            int cur = endId;
            int guard = 0;
            while (cur != NavConst.NodeStart && guard++ < 1_000_000)
            {
                sc.AbsPath.Add(cur);
                if (!sc.From.TryGetValue(cur, out int prev))
                {
                    break;
                }
                cur = prev;
            }
            for (int a = 0, b = sc.AbsPath.Length - 1; a < b; a++, b--)
            {
                int tmp = sc.AbsPath[a];
                sc.AbsPath[a] = sc.AbsPath[b];
                sc.AbsPath[b] = tmp;
            }
            if (!Refine(ref w, ref sc, sSlot, tSlot, cls, s, t, 0))
            {
                res.Status = NavStatus.Failed;
                res.Reason = NavFailReason.Unreachable;
                sc.Cells.Clear();
                return;
            }
            if (partialExtra)
            {
                NavNode bn = w.Nodes[endId];
                if (!LocalAStar(ref w, ref sc, bn.Slot, cls, bn.Cell, partialCell, sc.Cells))
                {
                    res.End = bn.Cell;
                }
            }
        }

        /// <summary>在一个区块里（<paramref name="dist"/> = 从某格出发的区块内代价，无穷 = 走不到）找离 <paramref name="t"/> 最近（八向距离，并列取 y 小、再 x 小）的可达格。</summary>
        private static void ScanClosest(ref NavWork w, NativeArray<int> dist, int2 chunk, int size, int2 t, ref int2 bestCell, ref int bestH)
        {
            int2 cb = chunk * size;
            for (int i = 0; i < size * size; i++)
            {
                if (dist[i] >= NavConst.Infinity)
                {
                    continue;
                }
                var c = new int2(cb.x + i % size, cb.y + i / size);
                int h = H(c, t);
                if (h < bestH || (h == bestH && (c.y < bestCell.y || (c.y == bestCell.y && c.x < bestCell.x))))
                {
                    bestH = h;
                    bestCell = c;
                }
            }
        }

        private static void Relax(ref NavWork w, ref NavScratch sc, int from, int to, int ng, int2 t)
        {
            if (sc.Closed.Contains(to))
            {
                return;
            }
            if (sc.G.TryGetValue(to, out int old) && old <= ng)
            {
                return;
            }
            sc.G[to] = ng;
            sc.From[to] = from;
            int2 c = w.Nodes[to].Cell;
            int h = H(c, t);
            HeapPush(sc.Heap, new NavHeapItem { F = ng + h, H = h, TieY = c.y, TieX = c.x, Item = to, G = ng });
        }

        private static void RelaxGoal(ref NavScratch sc, int from, int ng, int2 t)
        {
            if (sc.G.TryGetValue(NavConst.NodeGoal, out int old) && old <= ng)
            {
                return;
            }
            sc.G[NavConst.NodeGoal] = ng;
            sc.From[NavConst.NodeGoal] = from;
            HeapPush(sc.Heap, new NavHeapItem { F = ng, H = 0, TieY = t.y, TieX = t.x, Item = NavConst.NodeGoal, G = ng });
        }

        /// <summary>把抽象路线 sc.AbsPath[from..] 细化成逐格路线，追加到 sc.Cells（sc.Cells 末项 = 当前所在格）。</summary>
        private static bool Refine(ref NavWork w, ref NavScratch sc, int sSlot, int tSlot, int cls, int2 s, int2 t, int from)
        {
            int prevId = NavConst.NodeStart;
            int2 prevCell = s;
            for (int i = from; i < sc.AbsPath.Length; i++)
            {
                int id = sc.AbsPath[i];
                if (id == NavConst.NodeGoal)
                {
                    if (!LocalAStar(ref w, ref sc, tSlot, cls, prevCell, t, sc.Cells))
                    {
                        return false;
                    }
                    prevCell = t;
                    prevId = id;
                    continue;
                }
                NavNode node = w.Nodes[id];
                if (prevId == NavConst.NodeStart)
                {
                    if (!LocalAStar(ref w, ref sc, sSlot, cls, prevCell, node.Cell, sc.Cells))
                    {
                        return false;
                    }
                }
                else
                {
                    NavNode pn = w.Nodes[prevId];
                    if (pn.Slot == node.Slot)
                    {
                        if (!LocalAStar(ref w, ref sc, node.Slot, cls, prevCell, node.Cell, sc.Cells))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        sc.Cells.Add(node.Cell);
                    }
                }
                prevCell = node.Cell;
                prevId = id;
            }
            return true;
        }

        // ─────────────────────────────── 批处理 ───────────────────────────────

        private struct RepKey : IEquatable<RepKey>
        {
            public int Class;
            public int Flags;
            public int2 Goal;
            public int2 StartChunk;

            public bool Equals(RepKey o) => Class == o.Class && Flags == o.Flags && Goal.Equals(o.Goal) && StartChunk.Equals(o.StartChunk);

            public override int GetHashCode() => (int)math.hash(new int4(Class * 31 + Flags, Goal.x, Goal.y, StartChunk.x * 7919 + StartChunk.y));
        }

        private struct Rep
        {
            public int CellsStart;
            public int CellsCount;
            public int2 FirstNode;
            public int2 StartChunk;
            public int2 End;
            public NavStatus Status;
            public NavFailReason Reason;
        }

        /// <summary>
        /// 处理一批请求（按顺序）。同一批里“同一类别、同一目标格、同一起点区块”的请求共享代表路线：
        /// 成员只在起点区块内做一次局部 A* 接到代表路线的第一个入口，其后的逐格路线直接复用（FGR-ARC-015）。
        /// </summary>
        public static void RunBatch(ref NavWork w, NativeArray<NavRequest> requests, int count, in NavConfig cfg, NativeList<NavResult> results, NativeList<int2> points)
        {
            var sc = NavScratch.Create(w.Grid.Cells, Allocator.Temp);
            var reps = new NativeHashMap<RepKey, int>(math.max(4, count), Allocator.Temp);
            var repList = new NativeList<Rep>(math.max(4, count), Allocator.Temp);
            var repCells = new NativeList<int2>(1024, Allocator.Temp);
            var smoothed = new NativeList<int2>(256, Allocator.Temp);
            int size = w.Grid.ChunkSize;
            for (int q = 0; q < count; q++)
            {
                NavRequest r = requests[q];
                var res = new NavResult { Request = r };
                NavCounters cn = w.Counters[0];
                cn.Requests++;
                w.Counters[0] = cn;
                var key = new RepKey { Class = r.Class, Flags = (int)r.Flags, Goal = r.Goal, StartChunk = ChunkOf(r.Start, size) };
                bool shared = false;
                int2 resolvedStart = r.Start;
                if (reps.TryGetValue(key, out int repIndex))
                {
                    Rep rep = repList[repIndex];
                    if (Resolve(ref w.Grid, r.Start, r.Class, cfg.StartSearchRadius, out int2 s))
                    {
                        int2 sch = ChunkOf(s, size);
                        if (sch.Equals(rep.StartChunk))
                        {
                            int slot = NavGridOps.EnsureSlot(ref w.Grid, sch.x, sch.y);
                            sc.Cells.Clear();
                            sc.Cells.Add(s);
                            if (slot >= 0 && LocalAStar(ref w, ref sc, slot, r.Class, s, rep.FirstNode, sc.Cells))
                            {
                                for (int k = rep.CellsStart + 1; k < rep.CellsStart + rep.CellsCount; k++)
                                {
                                    sc.Cells.Add(repCells[k]);
                                }
                                shared = true;
                                resolvedStart = s;
                                res.Status = rep.Status;
                                res.Reason = rep.Reason;
                                res.End = rep.End;
                                res.Shared = 1;
                            }
                        }
                    }
                }
                if (!shared)
                {
                    FindCells(ref w, ref sc, r, cfg, ref res, out resolvedStart);
                    cn = w.Counters[0];
                    cn.Searches++;
                    w.Counters[0] = cn;
                    // 有抽象路线的成功 / 部分结果登记为代表路线（同区块直连的不登记：成员自己算也很便宜）。
                    if ((res.Status == NavStatus.Ok || res.Status == NavStatus.Partial) && sc.AbsPath.Length > 0 && sc.AbsPath[0] >= 0 && !reps.ContainsKey(key))
                    {
                        int2 firstNode = w.Nodes[sc.AbsPath[0]].Cell;
                        int idxFirst = -1;
                        for (int k = 0; k < sc.Cells.Length; k++)
                        {
                            if (sc.Cells[k].Equals(firstNode))
                            {
                                idxFirst = k;
                                break;
                            }
                        }
                        if (idxFirst >= 0)
                        {
                            int startIdx = repCells.Length;
                            for (int k = idxFirst; k < sc.Cells.Length; k++)
                            {
                                repCells.Add(sc.Cells[k]);
                            }
                            reps.Add(key, repList.Length);
                            repList.Add(new Rep
                            {
                                CellsStart = startIdx,
                                CellsCount = sc.Cells.Length - idxFirst,
                                FirstNode = firstNode,
                                StartChunk = ChunkOf(resolvedStart, size),
                                End = res.End,
                                Status = res.Status,
                                Reason = res.Reason,
                            });
                        }
                    }
                }
                else
                {
                    cn = w.Counters[0];
                    cn.Shared++;
                    w.Counters[0] = cn;
                }

                res.PointStart = points.Length;
                if (res.Status == NavStatus.Ok || res.Status == NavStatus.Partial)
                {
                    // 起点被挡（机器站在建筑里）时，先走到落地后的起点格。
                    if (!resolvedStart.Equals(r.Start))
                    {
                        points.Add(resolvedStart);
                    }
                    smoothed.Clear();
                    Smooth(ref w.Grid, sc.Cells, r.Class, math.max(2, cfg.SmoothLookahead), smoothed);
                    for (int k = 0; k < smoothed.Length; k++)
                    {
                        points.Add(smoothed[k]);
                    }
                    res.PointCount = points.Length - res.PointStart;
                    res.Length = PolyLength(r.Start, points, res.PointStart, res.PointCount);
                    if (res.Status == NavStatus.Partial)
                    {
                        cn = w.Counters[0];
                        cn.Partial++;
                        w.Counters[0] = cn;
                    }
                }
                else
                {
                    res.PointCount = 0;
                    cn = w.Counters[0];
                    cn.Failed++;
                    w.Counters[0] = cn;
                }
                results.Add(res);
            }
            smoothed.Dispose();
            repCells.Dispose();
            repList.Dispose();
            reps.Dispose();
            sc.Dispose();
            NavCounters c2 = w.Counters[0];
            c2.Batches++;
            w.Counters[0] = c2;
        }

        /// <summary>路线从 <paramref name="start"/> 起依次经过的路点之间都没被挡（主线程按镜像检查：采纳前、地形变化后）。</summary>
        public static bool RouteClear(ref NavGrid g, int2 start, NativeArray<int2> pts, int from, int count, int cls)
        {
            int2 prev = start;
            bool startInside = !NavGridOps.Passable(ref g, start, cls);
            for (int i = from; i < from + count; i++)
            {
                int2 p = pts[i];
                if (startInside && i == from)
                {
                    // 起点在障碍里（机器站在建筑占地里 / 新障碍刚好放在脚下）：第一段只豁免“走出来”的那几格——
                    // FG3-LOG-09（DEBT-FG0ARCH06-11 ②）：从脚下第一个可走格起照常检查，同一段前方的新障碍这次就触发重规划，不再等撞上后靠卡住检测。
                    if (!LineClearAfterExit(ref g, prev, p, cls))
                    {
                        return false;
                    }
                    prev = p;
                    continue;
                }
                if (!LineClear(ref g, prev, p, cls))
                {
                    return false;
                }
                prev = p;
            }
            return true;
        }

        /// <summary>
        /// FG3-LOG-09（DEBT-FG0ARCH06-11 ②）：与 <see cref="LineClear"/> 同一走法，但起点所在的障碍（及紧接着的障碍格）不算挡——
        /// 一旦走到第一个可走格，之后的每一格（含穿格角的两侧）都要能走。一直没走出障碍就到了终点：按“走出来”处理（下一段从终点起照常检查）。
        /// </summary>
        public static bool LineClearAfterExit(ref NavGrid g, int2 a, int2 b, int cls)
        {
            int dx = math.abs(b.x - a.x);
            int dy = math.abs(b.y - a.y);
            int sx = b.x > a.x ? 1 : -1;
            int sy = b.y > a.y ? 1 : -1;
            int x = a.x;
            int y = a.y;
            int n = 1 + dx + dy;
            int err = dx - dy;
            dx *= 2;
            dy *= 2;
            bool outside = false;
            for (; n > 0; n--)
            {
                bool passable = NavGridOps.Passable(ref g, x, y, cls);
                if (outside && !passable)
                {
                    return false;
                }
                outside |= passable;
                if (n == 1)
                {
                    break;
                }
                if (err > 0)
                {
                    x += sx;
                    err -= dy;
                }
                else if (err < 0)
                {
                    y += sy;
                    err += dx;
                }
                else
                {
                    if (outside && (!NavGridOps.Passable(ref g, x + sx, y, cls) || !NavGridOps.Passable(ref g, x, y + sy, cls)))
                    {
                        return false;
                    }
                    x += sx;
                    y += sy;
                    err += dx - dy;
                    n--;
                }
            }
            return true;
        }

        // ─────────────────────────────── 可达性（放置预览）───────────────────────────────

        /// <summary>
        /// 在矩形区域内从若干起点格做 8 向泛洪（斜走不切角），<paramref name="extraBlocked"/> 视为被挡（放置预览：假设的新建筑占地）。
        /// 每个探测格属于一个 owner（建筑序号）；owner 只要有一个探测格被泛洪到就算“可达”。
        /// </summary>
        public static void FloodReach(ref NavGrid g, int cls, int2 min, int2 max, NativeArray<int2> sources, int sourceCount,
            NativeArray<int2> extraBlocked, int extraCount, NativeArray<int2> probes, NativeArray<int> probeOwner, int probeCount, NativeArray<byte> ownerReached)
        {
            int w = max.x - min.x + 1;
            int h = max.y - min.y + 1;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            var visited = new NativeArray<byte>(w * h, Allocator.Temp);
            var blocked = new NativeArray<byte>(w * h, Allocator.Temp);
            for (int i = 0; i < extraCount; i++)
            {
                int2 c = extraBlocked[i] - min;
                if (c.x >= 0 && c.y >= 0 && c.x < w && c.y < h)
                {
                    blocked[c.y * w + c.x] = 1;
                }
            }
            var queue = new NativeList<int>(1024, Allocator.Temp);
            for (int i = 0; i < sourceCount; i++)
            {
                int2 c = sources[i] - min;
                if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h)
                {
                    continue;
                }
                int idx = c.y * w + c.x;
                if (visited[idx] != 0 || blocked[idx] != 0 || !NavGridOps.Passable(ref g, sources[i], cls))
                {
                    continue;
                }
                visited[idx] = 1;
                queue.Add(idx);
            }
            int head = 0;
            while (head < queue.Length)
            {
                int idx = queue[head++];
                int x = idx % w;
                int y = idx / w;
                for (int k = 0; k < 8; k++)
                {
                    int2 d = Dir8(k);
                    int nx = x + d.x;
                    int ny = y + d.y;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    {
                        continue;
                    }
                    int j = ny * w + nx;
                    if (visited[j] != 0 || !Open(ref g, cls, min, blocked, w, nx, ny))
                    {
                        continue;
                    }
                    if (k >= 4 && (!Open(ref g, cls, min, blocked, w, nx, y) || !Open(ref g, cls, min, blocked, w, x, ny)))
                    {
                        continue;
                    }
                    visited[j] = 1;
                    queue.Add(j);
                }
            }
            for (int i = 0; i < probeCount; i++)
            {
                int2 c = probes[i] - min;
                if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h)
                {
                    continue;
                }
                if (visited[c.y * w + c.x] != 0)
                {
                    ownerReached[probeOwner[i]] = 1;
                }
            }
            queue.Dispose();
            blocked.Dispose();
            visited.Dispose();
        }

        private static bool Open(ref NavGrid g, int cls, int2 min, NativeArray<byte> blocked, int w, int lx, int ly) =>
            blocked[ly * w + lx] == 0 && NavGridOps.Passable(ref g, min.x + lx, min.y + ly, cls);
    }

    /// <summary>一批寻路请求（Burst，工作线程）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavBatchJob : IJob
    {
        public NavWork W;
        [ReadOnly] public NativeArray<NavRequest> Requests;
        public int Count;
        public NavConfig Cfg;
        public NativeList<NavResult> Results;
        public NativeList<int2> Points;

        public void Execute()
        {
            NavSearch.RunBatch(ref W, Requests, Count, in Cfg, Results, Points);
        }
    }

    /// <summary>主线程同步：放置预览的可达性泛洪（Burst，Run）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavFloodJob : IJob
    {
        public NavGrid G;
        public int Class;
        public int2 Min;
        public int2 Max;
        [ReadOnly] public NativeArray<int2> Sources;
        public int SourceCount;
        [ReadOnly] public NativeArray<int2> Extra;
        public int ExtraCount;
        [ReadOnly] public NativeArray<int2> Probes;
        [ReadOnly] public NativeArray<int> ProbeOwner;
        public int ProbeCount;
        public NativeArray<byte> OwnerReached;

        public void Execute()
        {
            NavSearch.FloodReach(ref G, Class, Min, Max, Sources, SourceCount, Extra, ExtraCount, Probes, ProbeOwner, ProbeCount, OwnerReached);
        }
    }

    /// <summary>主线程同步：路线在镜像上是否仍然畅通（Burst，Run）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavRouteClearJob : IJob
    {
        public NavGrid G;
        public int2 Start;
        [ReadOnly] public NativeArray<int2> Points;
        public int From;
        public int Count;
        public int Class;
        public NativeArray<byte> Out;

        public void Execute()
        {
            Out[0] = (byte)(NavSearch.RouteClear(ref G, Start, Points, From, Count, Class) ? 1 : 0);
        }
    }
}
