using System;
using System.Collections.Generic;
using System.Diagnostics;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-050、052；FGR-ARC-013）：一张表面的区块流式加载。
    ///
    /// 每帧（主线程）<see cref="Tick"/>：
    /// 1. 需要的区块（常驻、不可回收）= 镜头周围 world.view_radius_chunks 半径的窗口，只在镜头跨区块时重算、O(窗口面积)，按离镜头的距离排序。
    ///    已探索范围外扩 world.pregen_margin_chunks 的预生成区只是“生成过一次”的触发条件：探索范围变化时**增量**地把新增部分排进
    ///    预生成队列（窗口优先），生成后与其它纯地形区块一样可以被回收（FG17 第 7 节“其余只保留模拟所需的数据”）；
    ///    触发过的区块只记键，不会因为回收而反复重新生成。
    /// 2. 工作线程上最多 world.stream.max_inflight 个 Burst 任务；完成的任务在 world.stream.integrate_budget_ms 的预算内接入格网
    ///    （每帧至少接入 1 个，保证前进）。主线程从不等待工作线程——还没好的区块由表现层显示“生成中”占位。
    /// 3. 常驻区块超过 world.stream.max_resident_chunks 时，回收离镜头最远的纯地形、未修改、没有建筑 / 传送带 / 管线、不在镜头窗口里的区块。
    /// 远距离飞跃时旧窗口里还没开工的请求直接丢弃，已开工的完成后照常接入（数据仍然有效）。
    /// 每帧开销与世界大小、探索面积、实体数量无关：O(在飞任务数 + 本帧调度数)；重算窗口只在跨区块时发生、O(窗口面积)；
    /// 预生成只在探索范围变化时处理新增 / 变化的探索记录。
    /// 旧版本（原型地形）没有内核路径：同一套调度改为主线程逐块生成（每帧同样受预算约束）。
    /// </summary>
    public sealed class WorldChunkStreamer : IDisposable
    {
        private readonly HomeGridMap _map;
        private readonly WorldTerrainSource _kernelSource;
        private readonly List<WorldGenJob> _inflight = new List<WorldGenJob>(16);
        private readonly HashSet<long> _inflightKeys = new HashSet<long>();
        private readonly List<long> _queue = new List<long>(256);
        private readonly HashSet<long> _desired = new HashSet<long>();
        /// <summary>预生成队列（已探索范围 + 边距里还没生成的区块），窗口队列排空后才用空槽开工。</summary>
        private readonly List<long> _pregenQueue = new List<long>(256);
        /// <summary>预生成触发过的区块（只记键）：生成后可被回收，回收后不会因为探索范围再变化而重新生成。</summary>
        private readonly HashSet<long> _pregenSeen = new HashSet<long>();
        /// <summary>已处理过的探索记录（按下标记住中心与半径），探索范围变化时只处理新增 / 变化的记录。</summary>
        private readonly List<(int x, int y, int r)> _pregenProcessed = new List<(int, int, int)>();
        private int _pregenHead;
        private int _pregenMargin = -1;
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly Stopwatch _integrateSw = new Stopwatch();
        private byte[] _terrainBuf;
        private byte[] _pollutionBuf;
        private int _queueHead;
        private bool _hasFocus;
        private int _focusCx;
        private int _focusCy;
        private int _desiredExploredRevision = -1;
        private int _viewRadius = -1;
        private bool _disposed;

        public HomeGridMap Map => _map;
        public int InFlightCount => _inflight.Count;
        /// <summary>还在排队、没开工的区块数。</summary>
        public int QueuedCount
        {
            get
            {
                return Pending(_queue, _queueHead) + Pending(_pregenQueue, _pregenHead);
            }
        }
        public int DesiredCount => _desired.Count;
        /// <summary>预生成触发过的区块数（自检：探索范围再变化时不重复触发）。</summary>
        public int PregenTriggeredCount => _pregenSeen.Count;

        private int Pending(List<long> queue, int head)
        {
            int n = 0;
            for (int i = head; i < queue.Count; i++)
            {
                long k = queue[i];
                HomeGridMap.Unkey(k, out int cx, out int cy);
                if (!_map.IsChunkLoaded(cx, cy) && !_inflightKeys.Contains(k))
                {
                    n++;
                }
            }
            return n;
        }
        public int TotalScheduled { get; private set; }
        public int TotalIntegrated { get; private set; }
        /// <summary>结果被丢弃的任务数（区块已被同步查询先生成）。</summary>
        public int TotalDiscarded { get; private set; }
        public int TotalEvicted { get; private set; }
        public int TickCount { get; private set; }
        public double LastTickMs { get; private set; }
        public double MaxTickMs { get; private set; }
        public double MaxIntegrateChunkMs { get; private set; }
        public double TotalIntegrateMs { get; private set; }
        public int FocusChunkX => _focusCx;
        public int FocusChunkY => _focusCy;
        /// <summary>本流式加载器用内核（工作线程）生成；false = 旧版本原型地形，主线程生成。</summary>
        public bool UsesKernel => _kernelSource != null;

        public WorldChunkStreamer(HomeGridMap map)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _kernelSource = map.TerrainSource as WorldTerrainSource;
            int n = map.ChunkSize * map.ChunkSize;
            _terrainBuf = new byte[n];
            _pollutionBuf = new byte[n];
        }

        public void ResetMetrics()
        {
            MaxTickMs = 0;
            MaxIntegrateChunkMs = 0;
            TotalIntegrateMs = 0;
        }

        public bool IsReady(int cx, int cy) => _map.IsChunkLoaded(cx, cy);

        /// <summary>镜头（焦点格）周围 <paramref name="radius"/> 个区块内还没生成好的区块数（表现层“生成中”提示用）。</summary>
        public int PendingAround(GridCell focus, int radius)
        {
            ChunkAddress a = GridMath.Address(focus, _map.ChunkSize);
            int n = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int cx = a.ChunkX + dx;
                    int cy = a.ChunkY + dy;
                    if (_map.TerrainSource.ChunkExists(cx, cy, _map.ChunkSize) && !_map.IsChunkLoaded(cx, cy))
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>每帧调用（主线程）。<paramref name="focus"/> = 镜头焦点所在格。暂停与倍速都不影响（与游戏时间无关）。</summary>
        public void Tick(GridCell focus)
        {
            if (_disposed)
            {
                return;
            }
            _sw.Restart();
            TickCount++;
            int viewRadius = Math.Max(0, GridContent.TuningInt("world.view_radius_chunks"));
            ChunkAddress a = GridMath.Address(ClampToLimit(focus), _map.ChunkSize);
            bool moved = !_hasFocus || a.ChunkX != _focusCx || a.ChunkY != _focusCy;
            if (moved || viewRadius != _viewRadius)
            {
                _hasFocus = true;
                _focusCx = a.ChunkX;
                _focusCy = a.ChunkY;
                _viewRadius = viewRadius;
                RebuildDesired();
            }
            if (_desiredExploredRevision != _map.ExploredRevision)
            {
                UpdatePregen();
            }

            Integrate(budgetMs: GridContent.Tuning("world.stream.integrate_budget_ms"));
            ScheduleMore();
            EvictIfNeeded();
            _sw.Stop();
            LastTickMs = _sw.Elapsed.TotalMilliseconds;
            if (LastTickMs > MaxTickMs)
            {
                MaxTickMs = LastTickMs;
            }
        }

        private GridCell ClampToLimit(GridCell c)
        {
            int limit = GridContent.TuningInt("world.coord_limit");
            return new GridCell(Math.Max(-limit, Math.Min(limit, c.X)), Math.Max(-limit, Math.Min(limit, c.Y)));
        }

        private void RebuildDesired()
        {
            _desired.Clear();
            _queue.Clear();
            _queueHead = 0;
            for (int dy = -_viewRadius; dy <= _viewRadius; dy++)
            {
                for (int dx = -_viewRadius; dx <= _viewRadius; dx++)
                {
                    AddDesired(_focusCx + dx, _focusCy + dy);
                }
            }
            SortByFocus(_queue, 0);
        }

        /// <summary>探索范围变化时调用：只处理新增 / 变化的探索记录，把其中还没触发过的区块排进预生成队列。</summary>
        private void UpdatePregen()
        {
            _desiredExploredRevision = _map.ExploredRevision;
            int margin = Math.Max(0, GridContent.TuningInt("world.pregen_margin_chunks"));
            if (margin != _pregenMargin)
            {
                _pregenMargin = margin;
                _pregenProcessed.Clear();
            }
            ExploredAreaRecord[] areas = _map.ExploredAreas ?? Array.Empty<ExploredAreaRecord>();
            if (_pregenProcessed.Count > areas.Length)
            {
                _pregenProcessed.RemoveRange(areas.Length, _pregenProcessed.Count - areas.Length);
            }
            if (_pregenHead > 0 && _pregenHead >= _pregenQueue.Count / 2)
            {
                _pregenQueue.RemoveRange(0, _pregenHead);
                _pregenHead = 0;
            }
            int firstNew = _pregenQueue.Count;
            int size = _map.ChunkSize;
            for (int i = 0; i < areas.Length; i++)
            {
                ExploredAreaRecord e = areas[i];
                (int x, int y, int r) sig = e == null ? (0, 0, -1) : (e.CenterX, e.CenterY, e.Radius);
                if (i < _pregenProcessed.Count && _pregenProcessed[i] == sig)
                {
                    continue;
                }
                // 同一条记录只是半径变大（中心不变，探索范围的常见增长方式）：只扫新增的环带，不重扫已处理过的内部。
                bool grew = false;
                ChunkAddress oldMin = default;
                ChunkAddress oldMax = default;
                if (i < _pregenProcessed.Count)
                {
                    (int x, int y, int r) old = _pregenProcessed[i];
                    if (old.r >= 0 && old.x == sig.x && old.y == sig.y && sig.r >= old.r)
                    {
                        grew = true;
                        oldMin = GridMath.Address(new GridCell(old.x - old.r, old.y - old.r), size);
                        oldMax = GridMath.Address(new GridCell(old.x + old.r, old.y + old.r), size);
                    }
                    _pregenProcessed[i] = sig;
                }
                else
                {
                    _pregenProcessed.Add(sig);
                }
                if (sig.r < 0)
                {
                    continue;
                }
                ChunkAddress min = GridMath.Address(new GridCell(sig.x - sig.r, sig.y - sig.r), size);
                ChunkAddress max = GridMath.Address(new GridCell(sig.x + sig.r, sig.y + sig.r), size);
                for (int cy = min.ChunkY - margin; cy <= max.ChunkY + margin; cy++)
                {
                    bool rowInsideOld = grew && cy >= oldMin.ChunkY - margin && cy <= oldMax.ChunkY + margin;
                    for (int cx = min.ChunkX - margin; cx <= max.ChunkX + margin; cx++)
                    {
                        if (rowInsideOld && cx >= oldMin.ChunkX - margin && cx <= oldMax.ChunkX + margin)
                        {
                            cx = oldMax.ChunkX + margin; // 跳过已处理的内部，循环 ++ 后从右侧环带继续
                            continue;
                        }
                        if (!_map.TerrainSource.ChunkExists(cx, cy, size))
                        {
                            continue;
                        }
                        long k = HomeGridMap.Key(cx, cy);
                        if (_pregenSeen.Add(k) && !_map.IsChunkLoaded(cx, cy))
                        {
                            _pregenQueue.Add(k);
                        }
                    }
                }
            }
            SortByFocus(_pregenQueue, firstNew);
        }

        private void SortByFocus(List<long> list, int from)
        {
            if (list.Count - from < 2)
            {
                return;
            }
            int fx = _focusCx;
            int fy = _focusCy;
            list.Sort(from, list.Count - from, Comparer<long>.Create((p, q) =>
            {
                HomeGridMap.Unkey(p, out int px, out int py);
                HomeGridMap.Unkey(q, out int qx, out int qy);
                int dp = Math.Max(Math.Abs(px - fx), Math.Abs(py - fy));
                int dq = Math.Max(Math.Abs(qx - fx), Math.Abs(qy - fy));
                if (dp != dq)
                {
                    return dp.CompareTo(dq);
                }
                return p.CompareTo(q);
            }));
        }

        private void AddDesired(int cx, int cy)
        {
            if (!_map.TerrainSource.ChunkExists(cx, cy, _map.ChunkSize))
            {
                return;
            }
            long k = HomeGridMap.Key(cx, cy);
            if (_desired.Add(k) && !_map.IsChunkLoaded(cx, cy))
            {
                _queue.Add(k);
            }
        }

        private void Integrate(float budgetMs)
        {
            if (_inflight.Count == 0)
            {
                return;
            }
            _integrateSw.Restart();
            int integrated = 0;
            for (int i = 0; i < _inflight.Count; i++)
            {
                WorldGenJob job = _inflight[i];
                if (!job.IsCompleted)
                {
                    continue;
                }
                if (integrated > 0 && _integrateSw.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    break; // 预算用完，下一帧再接。
                }
                double before = _integrateSw.Elapsed.TotalMilliseconds;
                AdoptJob(job);
                double took = _integrateSw.Elapsed.TotalMilliseconds - before;
                if (took > MaxIntegrateChunkMs)
                {
                    MaxIntegrateChunkMs = took;
                }
                TotalIntegrateMs += took;
                _inflight.RemoveAt(i);
                i--;
                integrated++;
            }
        }

        private void AdoptJob(WorldGenJob job)
        {
            job.CopyResult(_terrainBuf, _pollutionBuf);
            _inflightKeys.Remove(HomeGridMap.Key(job.ChunkX, job.ChunkY));
            job.Release();
            if (_map.TryAdopt(job.ChunkX, job.ChunkY, _terrainBuf, _pollutionBuf))
            {
                TotalIntegrated++;
            }
            else
            {
                TotalDiscarded++;
            }
        }

        private void ScheduleMore()
        {
            int maxInflight = Math.Max(1, GridContent.TuningInt("world.stream.max_inflight"));
            bool any = false;
            bool stop = false;
            // 镜头窗口优先；窗口排空后才用空槽做预生成。
            ScheduleFrom(_queue, ref _queueHead, maxInflight, ref any, ref stop);
            if (!stop && _queueHead >= _queue.Count)
            {
                ScheduleFrom(_pregenQueue, ref _pregenHead, maxInflight, ref any, ref stop);
            }
            if (any)
            {
                WorldGenKernel.Kick();
            }
        }

        private void ScheduleFrom(List<long> queue, ref int head, int maxInflight, ref bool any, ref bool stop)
        {
            while (head < queue.Count && _inflight.Count < maxInflight)
            {
                long k = queue[head++];
                if (_inflightKeys.Contains(k))
                {
                    continue;
                }
                HomeGridMap.Unkey(k, out int cx, out int cy);
                if (_map.IsChunkLoaded(cx, cy))
                {
                    continue;
                }
                if (_kernelSource != null)
                {
                    _inflight.Add(_kernelSource.Schedule(cx, cy, 0));
                    _inflightKeys.Add(k);
                    any = true;
                }
                else
                {
                    // 旧版本原型地形：主线程逐块生成，每帧一块（与内核路径同一套队列与顺序）。
                    _map.ChunkAt(new GridCell(cx * _map.ChunkSize, cy * _map.ChunkSize), out _);
                    TotalIntegrated++;
                    TotalScheduled++;
                    stop = true;
                    return;
                }
                TotalScheduled++;
            }
        }

        private readonly List<(long key, int dist)> _evictScratch = new List<(long, int)>();

        private void EvictIfNeeded()
        {
            int cap = Math.Max(64, GridContent.TuningInt("world.stream.max_resident_chunks"));
            if (_map.LoadedChunkCount <= cap + 64)
            {
                return; // 超出 64 块才回收一次，摊薄 O(常驻数) 的扫描。
            }
            _evictScratch.Clear();
            foreach (HomeGridMap.Chunk c in _map.LoadedChunks)
            {
                long k = HomeGridMap.Key(c.ChunkX, c.ChunkY);
                if (_desired.Contains(k) || c.Modified)
                {
                    continue; // 镜头窗口里的与已修改的不回收；已探索 / 预生成区的纯地形区块照样可回收（生成过一次即可）。
                }
                _evictScratch.Add((k, Math.Max(Math.Abs(c.ChunkX - _focusCx), Math.Abs(c.ChunkY - _focusCy))));
            }
            _evictScratch.Sort((p, q) => q.dist.CompareTo(p.dist));
            int need = _map.LoadedChunkCount - cap;
            for (int i = 0; i < _evictScratch.Count && need > 0; i++)
            {
                HomeGridMap.Unkey(_evictScratch[i].key, out int cx, out int cy);
                if (_map.TryEvict(cx, cy))
                {
                    TotalEvicted++;
                    need--;
                }
            }
        }

        /// <summary>等所有在飞任务完成并接入（关停、自检、需要立刻得到结果时）。不开新任务。</summary>
        public void CompleteInFlight()
        {
            for (int i = 0; i < _inflight.Count; i++)
            {
                _inflight[i].Complete();
            }
            Integrate(float.MaxValue);
        }

        /// <summary>把当前需要集合全部生成完（自检用：反复 Tick 直到没有排队与在飞的任务，最多 <paramref name="maxTicks"/> 次）。</summary>
        public int DrainForTests(GridCell focus, int maxTicks = 100000)
        {
            int ticks = 0;
            do
            {
                Tick(focus);
                CompleteInFlight();
                ticks++;
            }
            while ((_inflight.Count > 0 || QueuedCount > 0) && ticks < maxTicks);
            return ticks;
        }

        /// <summary>释放全部在飞任务的原生内存（区域卸载 / 换战役时调用）。幂等。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (WorldGenJob job in _inflight)
            {
                job.Release();
            }
            _inflight.Clear();
            _inflightKeys.Clear();
            _queue.Clear();
            _desired.Clear();
            _pregenQueue.Clear();
            _pregenSeen.Clear();
            _pregenProcessed.Clear();
        }

        public bool IsDisposed => _disposed;
    }
}
