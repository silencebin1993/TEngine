using System;
using System.Collections.Generic;
using System.IO;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Nav
{
    /// <summary>寻路快照读回的结果（热更层映射成文本键）。</summary>
    public enum NavLoadResult : byte
    {
        Ok = 0,
        Empty = 1,
        BadMagic = 2,
        UnknownFormat = 3,
        BadChecksum = 4,
        Truncated = 5,
        InvalidValue = 6,
    }

    /// <summary>采纳前的整批校验（Burst，Run）：路线在镜像上仍然畅通才采纳，否则原请求重新排队。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavValidateJob : IJob
    {
        public NavGrid G;
        [ReadOnly] public NativeArray<NavResult> Results;
        [ReadOnly] public NativeArray<int2> Points;
        public NativeArray<byte> Valid;

        public void Execute()
        {
            for (int i = 0; i < Results.Length; i++)
            {
                NavResult r = Results[i];
                bool ok = r.Status != NavStatus.Ok && r.Status != NavStatus.Partial
                          || NavSearch.RouteClear(ref G, r.Request.Start, Points, r.PointStart, r.PointCount, r.Request.Class);
                Valid[i] = (byte)(ok ? 1 : 0);
            }
        }
    }

    /// <summary>FG6-DEF-04：镜像 → 后台工作副本同步（Burst，主线程 Run）：内容变了 / 新出现的区块复制过去并让相关抽象图失效。返回变了的区块数。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavMirrorSyncJob : IJob
    {
        public NavGrid From;
        public NavWork To;
        public NativeArray<int> Changed;

        public void Execute()
        {
            int n = 0;
            for (int i = 0; i < From.SlotKeys.Length; i++)
            {
                long key = From.SlotKeys[i];
                if (NavGridOps.CopyChunk(ref From, ref To.Grid, key))
                {
                    NavGridOps.Unkey(key, out int cx, out int cy);
                    NavSearch.InvalidateChunk(ref To, cx, cy);
                    n++;
                }
            }
            Changed[0] = n;
        }
    }

    /// <summary>
    /// FG0-ARCH-06（FG14 FGR-ARC-015；FG15 长距离寻路 ≤ 20 毫秒、工作线程）：一张表面的寻路内核。
    ///
    /// 流水线（热更层 NavService 每个模拟步开头调用一次，全部 O(变化的区块 + 结果条数)，与单位数无关）：
    /// 1. 地形变化：热更层把变化的区块（地形层 + 占用层）推进<b>镜像</b>（<see cref="PushChunk"/>），内容真的变了才记为变化。
    /// 2. 采纳：批次到了固定的采纳步（调度步 + <see cref="NavConfig.LatencySteps"/>）才等它完成、在镜像上逐条校验（路线在这期间被挡的重新排队）、
    ///    交给请求方。工作线程快慢、帧率、倍速都不影响“哪一步采纳” → 结果确定。
    /// 3. 调度：没有批次在飞时，把镜像里变过的区块复制进<b>工作副本</b>（相关抽象图失效），按队列顺序取一批（条数上限 + 起终点距离预算，
    ///    与缓存冷热无关 → 分批确定），在工作线程上跑（<see cref="NavBatchJob"/>）。
    /// 镜像只在主线程读写（战斗内核碰撞、路线失效检查、放置预览）；工作副本只在作业里读写。两者纯地形区块都按种子就地生成，逐字节相同。
    /// 存档：排队的请求、已算好还没采纳的结果（存档时强制完成）、上次同步后变化的区块，一起写进快照（<see cref="SerializePending"/>），
    /// 读档接着跑与不存档逐步一致。
    /// </summary>
    public sealed class NavKernel : IDisposable
    {
        public NavConfig Config { get; }

        private NavGrid _mirror;
        private NavWork _work;
        private readonly List<NavRequest> _queue = new List<NavRequest>(64);
        private NativeArray<NavRequest> _batch;
        private int _batchCount;
        private NativeList<NavResult> _results;
        private NativeList<int2> _points;
        private NativeArray<byte> _valid;
        private JobHandle _handle;
        private bool _batchActive;
        private bool _adopting;
        /// <summary>批次在飞期间有地形变化推进镜像（asChange）：采纳时“失败”结果也要重算一次（在调度时的旧工作副本上算出的，屏障拆除 / 净化打通后可能已经可达）。</summary>
        private bool _changedInFlight;
        private long _scheduleTick;
        private long _dueTick;
        private readonly HashSet<long> _pendingApply = new HashSet<long>();
        private readonly HashSet<(byte, byte, int, int, int, int)> _groupScratch = new HashSet<(byte, byte, int, int, int, int)>();
        private readonly List<long> _applyScratch = new List<long>();
        private readonly HashSet<long> _changedSet = new HashSet<long>();
        private readonly List<long> _changed = new List<long>();
        private NativeArray<byte> _tmpTerrain;
        private NativeArray<int> _tmpOcc;
        private NativeArray<byte> _tmpOccBlock;
        private NativeArray<byte> _tmpCells;
        private NavCounters _counters;
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        // ── FG6-DEF-04（DEBT-FG0ARCH06-09）：后台长路线通道——独立的工作副本，一次一条，调用方在固定的较晚时刻取结果 ──
        private NavWork _bg;
        private NativeArray<NavRequest> _bgReq;
        private NativeList<NavResult> _bgRes;
        private NativeList<int2> _bgPts;
        private NativeArray<int> _bgChanged;
        private JobHandle _bgHandle;
        private bool _bgActive;

        /// <summary>后台通道正在算一条（还没被取走）。</summary>
        public bool BackgroundActive => _bgActive;
        /// <summary>后台通道那一条已经算完（没有在算也算“完”）。只读查询，不改变任何结果。</summary>
        public bool BackgroundDone => !_bgActive || _bgHandle.IsCompleted;
        public int BackgroundOwnerTag { get; private set; } = -1;
        public int BackgroundOwnerKey { get; private set; } = -1;
        public int BackgroundSerial { get; private set; }
        /// <summary>后台通道调度过的条数 / 取结果时还没算完、主线程被迫等的次数 / 上一次同步镜像变了的区块数与耗时（性能证据）。</summary>
        public long BackgroundJobs { get; private set; }
        public long BackgroundLateCompletes { get; private set; }
        public int LastBackgroundSyncedChunks { get; private set; }
        public double LastBackgroundSyncMs { get; private set; }

        public static int LiveKernels { get; private set; }

        public NavKernel(in NavConfig config, byte[] terrainCell, bool hasGen, in WorldGenParams gen, WorldGenRect[] rects, WorldGenZone[] zones)
        {
            NavConfig c = config;
            c.ChunkSize = math.max(4, c.ChunkSize);
            c.LatencySteps = math.max(1, c.LatencySteps);
            c.BatchMaxRequests = math.max(1, c.BatchMaxRequests);
            c.BatchDistanceBudget = math.max(1, c.BatchDistanceBudget);
            c.MaxExpansions = math.max(64, c.MaxExpansions);
            c.SmoothLookahead = math.max(2, c.SmoothLookahead);
            Config = c;
            _mirror = NavGrid.Create(c.ChunkSize, terrainCell, hasGen, gen, rects, zones, c.CoordLimit, 64);
            _work = NavWork.Create(NavGrid.Create(c.ChunkSize, terrainCell, hasGen, gen, rects, zones, c.CoordLimit, 64));
            _batch = new NativeArray<NavRequest>(c.BatchMaxRequests, Allocator.Persistent);
            _results = new NativeList<NavResult>(64, Allocator.Persistent);
            _points = new NativeList<int2>(1024, Allocator.Persistent);
            _valid = new NativeArray<byte>(c.BatchMaxRequests, Allocator.Persistent);
            int n = c.ChunkSize * c.ChunkSize;
            _tmpTerrain = new NativeArray<byte>(n, Allocator.Persistent);
            _tmpOcc = new NativeArray<int>(n, Allocator.Persistent);
            _tmpOccBlock = new NativeArray<byte>(64, Allocator.Persistent);
            _tmpCells = new NativeArray<byte>(n, Allocator.Persistent);
            _bg = NavWork.Create(NavGrid.Create(c.ChunkSize, terrainCell, hasGen, gen, rects, zones, c.CoordLimit, 64));
            _bgReq = new NativeArray<NavRequest>(1, Allocator.Persistent);
            _bgRes = new NativeList<NavResult>(1, Allocator.Persistent);
            _bgPts = new NativeList<int2>(256, Allocator.Persistent);
            _bgChanged = new NativeArray<int>(1, Allocator.Persistent);
            HasGen = hasGen;
            LiveKernels++;
        }

        public bool IsDisposed => !_mirror.IsCreated;
        public bool HasGen { get; }

        /// <summary>镜像（主线程）：战斗内核绑定它做碰撞与路线检查。</summary>
        public NavGrid Mirror => _mirror;

        public int MirrorChunkCount => _mirror.IsCreated ? _mirror.ChunkCount : 0;
        public int QueueCount => _queue.Count;
        public bool BatchActive => _batchActive;
        public long DueTick => _dueTick;
        public long ScheduleTick => _scheduleTick;
        public int BatchCount => _batchCount;
        public long LateCompletes { get; private set; }
        public long Requeued { get; private set; }
        public long Invalidated { get; set; }
        public double LastBatchWallMs { get; private set; }
        public double LastAdoptMs { get; private set; }
        public double LastScheduleMs { get; private set; }

        /// <summary>内核计数（上一次批次完成时的快照；作业在飞时不读工作副本）。</summary>
        public NavCounters Counters
        {
            get
            {
                NavCounters c = _counters;
                c.LateCompletes = LateCompletes;
                c.Requeued = Requeued;
                c.Invalidated = Invalidated;
                return c;
            }
        }

        public void Dispose()
        {
            if (!_mirror.IsCreated)
            {
                return;
            }
            _handle.Complete();
            _bgHandle.Complete();
            _bgActive = false;
            _bg.Dispose();
            _bgReq.Dispose();
            _bgRes.Dispose();
            _bgPts.Dispose();
            _bgChanged.Dispose();
            _mirror.Dispose();
            _work.Dispose();
            _batch.Dispose();
            _results.Dispose();
            _points.Dispose();
            _valid.Dispose();
            _tmpTerrain.Dispose();
            _tmpOcc.Dispose();
            _tmpOccBlock.Dispose();
            _tmpCells.Dispose();
            _queue.Clear();
            LiveKernels--;
        }

        // ─────────────────────────────── 镜像 ───────────────────────────────

        /// <summary>
        /// 推送一个区块（地形层 + 占用层；<paramref name="occupantBlock"/>[k] = 第 k+1 个占用者挡哪些移动类别）。
        /// 内容真的变了（或镜像里原来没有）才记为待复制进工作副本；<paramref name="asChange"/> 时同时记为“上次同步后变化的区块”（触发路线失效检查）。
        /// </summary>
        public bool PushChunk(int cx, int cy, byte[] terrain, int[] occupancy, byte[] occupantBlock, int occupantCount, bool asChange)
        {
            int n = Config.ChunkSize * Config.ChunkSize;
            if (terrain == null || occupancy == null || terrain.Length < n || occupancy.Length < n)
            {
                throw new ArgumentException("区块数据长度不符");
            }
            NativeArray<byte>.Copy(terrain, _tmpTerrain, n);
            NativeArray<int>.Copy(occupancy, _tmpOcc, n);
            int oc = math.max(0, occupantCount);
            if (_tmpOccBlock.Length < math.max(1, oc))
            {
                _tmpOccBlock.Dispose();
                _tmpOccBlock = new NativeArray<byte>(math.max(64, oc * 2), Allocator.Persistent);
            }
            for (int k = 0; k < oc; k++)
            {
                _tmpOccBlock[k] = occupantBlock != null && k < occupantBlock.Length ? occupantBlock[k] : NavConst.BlockAll;
            }
            new NavComputeChunkJob
            {
                TerrainCell = _mirror.TerrainCell,
                Terrain = _tmpTerrain,
                Occupancy = _tmpOcc,
                OccupantBlock = _tmpOccBlock,
                OccupantCount = oc,
                ChunkSize = Config.ChunkSize,
                ChunkX = cx,
                ChunkY = cy,
                CoordLimit = Config.CoordLimit,
                Out = _tmpCells,
            }.Run();
            long key = NavGridOps.Key(cx, cy);
            bool changed = NavGridOps.SetChunk(ref _mirror, key, _tmpCells, 0);
            if (changed)
            {
                _pendingApply.Add(key);
                if (asChange)
                {
                    MarkChanged(key);
                    if (_batchActive)
                    {
                        _changedInFlight = true;
                    }
                }
            }
            return changed;
        }

        /// <summary>记一个“上次同步后变化的区块”（读档时恢复存档那一刻还没同步的变化）。</summary>
        public void MarkChanged(long key)
        {
            if (_changedSet.Add(key))
            {
                _changed.Add(key);
            }
        }

        public int ChangedCount => _changed.Count;
        public IReadOnlyList<long> Changed => _changed;

        public void ClearChanged()
        {
            _changed.Clear();
            _changedSet.Clear();
        }

        public bool Passable(int x, int y, int cls) => NavGridOps.Passable(ref _mirror, x, y, cls);

        public byte CellAt(int x, int y) => NavGridOps.CellAt(ref _mirror, x, y);

        /// <summary>路线（从 <paramref name="start"/> 依次经过 <paramref name="pts"/>[from..]）在镜像上是否仍然畅通。</summary>
        public bool RouteClear(int2 start, IReadOnlyList<int2> pts, int from, int cls)
        {
            int count = pts == null ? 0 : pts.Count - from;
            if (count <= 0)
            {
                return true;
            }
            var arr = new NativeArray<int2>(count, Allocator.TempJob);
            var outFlag = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    arr[i] = pts[from + i];
                }
                new NavRouteClearJob { G = _mirror, Start = start, Points = arr, From = 0, Count = count, Class = cls, Out = outFlag }.Run();
                return outFlag[0] != 0;
            }
            finally
            {
                arr.Dispose();
                outFlag.Dispose();
            }
        }

        /// <summary>
        /// 放置预览的可达性（镜像，Burst 泛洪）：在矩形区域里从 <paramref name="sources"/> 出发，把 <paramref name="extraBlocked"/> 当作障碍，
        /// 每个 owner 的探测格只要有一个被泛洪到就是可达。<paramref name="reached"/> 长度 = owner 数。
        /// </summary>
        public void Reachability(int cls, int2 min, int2 max, IReadOnlyList<int2> sources, IReadOnlyList<int2> extraBlocked,
            IReadOnlyList<int2> probes, IReadOnlyList<int> probeOwner, bool[] reached)
        {
            int ownerCount = reached.Length;
            var src = ToNative(sources);
            var extra = ToNative(extraBlocked);
            var pr = ToNative(probes);
            var po = new NativeArray<int>(math.max(1, probeOwner?.Count ?? 0), Allocator.TempJob);
            var outR = new NativeArray<byte>(math.max(1, ownerCount), Allocator.TempJob);
            try
            {
                for (int i = 0; i < (probeOwner?.Count ?? 0); i++)
                {
                    po[i] = probeOwner[i];
                }
                new NavFloodJob
                {
                    G = _mirror,
                    Class = cls,
                    Min = min,
                    Max = max,
                    Sources = src,
                    SourceCount = sources?.Count ?? 0,
                    Extra = extra,
                    ExtraCount = extraBlocked?.Count ?? 0,
                    Probes = pr,
                    ProbeOwner = po,
                    ProbeCount = probes?.Count ?? 0,
                    OwnerReached = outR,
                }.Run();
                for (int i = 0; i < ownerCount; i++)
                {
                    reached[i] = outR[i] != 0;
                }
            }
            finally
            {
                src.Dispose();
                extra.Dispose();
                pr.Dispose();
                po.Dispose();
                outR.Dispose();
            }
        }

        /// <summary>
        /// FG6-DEF-02（FG06 第 4 节“放置屏障时预览对流场的影响”）：敌方路线预览（镜像，Burst）。以 <paramref name="goals"/> 为终点、<paramref name="extraBlocked"/> 视为被挡，
        /// 从每个 <paramref name="entries"/> 起点沿距离下降追踪出一条路线（格子序列，写进 <paramref name="points"/>）。<paramref name="costs"/>[i] = 代价（-1 = 不可达），
        /// <paramref name="offsets"/> / <paramref name="counts"/> = 第 i 条路线在 points 里的位置。返回可达的条数。
        /// </summary>
        public int FlowRoutes(int cls, int2 min, int2 max, IReadOnlyList<int2> goals, IReadOnlyList<int2> extraBlocked, IReadOnlyList<int2> entries,
            List<int2> points, int[] offsets, int[] counts, int[] costs)
        {
            points?.Clear();
            int ec = entries?.Count ?? 0;
            var g = ToNative(goals);
            var extra = ToNative(extraBlocked);
            var en = ToNative(entries);
            var pts = new NativeList<int2>(256, Allocator.TempJob);
            var info = new NativeArray<int3>(math.max(1, ec), Allocator.TempJob);
            try
            {
                new NavFlowRouteJob
                {
                    G = _mirror,
                    Class = cls,
                    Min = min,
                    Max = max,
                    Goals = g,
                    GoalCount = goals?.Count ?? 0,
                    Extra = extra,
                    ExtraCount = extraBlocked?.Count ?? 0,
                    Entries = en,
                    EntryCount = ec,
                    Points = pts,
                    Info = info,
                }.Run();
                int reachable = 0;
                for (int i = 0; i < ec; i++)
                {
                    int3 inf = info[i];
                    if (offsets != null && i < offsets.Length)
                    {
                        offsets[i] = inf.x;
                    }
                    if (counts != null && i < counts.Length)
                    {
                        counts[i] = inf.y;
                    }
                    if (costs != null && i < costs.Length)
                    {
                        costs[i] = inf.z;
                    }
                    if (inf.z >= 0)
                    {
                        reachable++;
                    }
                }
                if (points != null)
                {
                    for (int i = 0; i < pts.Length; i++)
                    {
                        points.Add(pts[i]);
                    }
                }
                return reachable;
            }
            finally
            {
                g.Dispose();
                extra.Dispose();
                en.Dispose();
                pts.Dispose();
                info.Dispose();
            }
        }

        private static NativeArray<int2> ToNative(IReadOnlyList<int2> list)
        {
            var a = new NativeArray<int2>(math.max(1, list?.Count ?? 0), Allocator.TempJob);
            for (int i = 0; i < (list?.Count ?? 0); i++)
            {
                a[i] = list[i];
            }
            return a;
        }

        // ─────────────────────────────── 请求 ───────────────────────────────

        public void Enqueue(in NavRequest r) => _queue.Add(r);

        /// <summary>把战斗内核本步新发出的请求整体转进队列（按内核里的发出顺序），返回条数。热更层 O(1) 次调用。</summary>
        public int CollectFromCombat(CombatKernel k, int ownerTag, long tick)
        {
            if (k == null || k.IsDisposed)
            {
                return 0;
            }
            ref CombatData d = ref k.Data;
            int n = d.NavOut.Length;
            for (int q = 0; q < n; q++)
            {
                CombatNavRequest nr = d.NavOut[q];
                _queue.Add(new NavRequest
                {
                    OwnerTag = ownerTag,
                    OwnerKey = nr.UnitId,
                    Serial = nr.Serial,
                    Class = nr.Class,
                    Flags = (NavRequestFlags)nr.Flags,
                    Start = nr.Start,
                    Goal = nr.Goal,
                    IssuedTick = tick,
                });
            }
            d.NavOut.Clear();
            return n;
        }

        /// <summary>丢掉某个请求方的全部排队请求（请求方卸载时）。</summary>
        public int DropQueued(int ownerTag)
        {
            return _queue.RemoveAll(r => r.OwnerTag == ownerTag);
        }

        public NavRequest QueuedAt(int i) => _queue[i];

        // ─────────────────────────────── 批次 ───────────────────────────────

        public bool IsDue(long tick) => _batchActive && tick >= _dueTick;

        /// <summary>
        /// 采纳开始：等批次完成（到了采纳步还没完成 = 主线程被迫等待，计入 <see cref="LateCompletes"/>），在镜像上逐条校验；
        /// 校验不过的（路线在这期间被新建筑挡住）原请求按结果顺序重新排队。之后用 <see cref="ResultCount"/> 等读取，<see cref="EndAdoption"/> 结束。
        /// </summary>
        public void BeginAdoption()
        {
            if (!_batchActive)
            {
                return;
            }
            _watch.Restart();
            if (!_handle.IsCompleted)
            {
                LateCompletes++;
            }
            _handle.Complete();
            _handle = default;
            _counters = _work.Counters[0];
            int n = _results.Length;
            if (_valid.Length < n)
            {
                _valid.Dispose();
                _valid = new NativeArray<byte>(math.max(n, 16), Allocator.Persistent);
            }
            if (n > 0)
            {
                var arr = new NativeArray<byte>(n, Allocator.TempJob);
                try
                {
                    new NavValidateJob { G = _mirror, Results = _results.AsArray(), Points = _points.AsArray(), Valid = arr }.Run();
                    for (int i = 0; i < n; i++)
                    {
                        NavResult ri = _results[i];
                        // 失败结果是在调度时的工作副本上算的：在飞期间地形变了就重算一次（只一次，见 RetriedAfterChange）。
                        bool staleFailure = _changedInFlight && ri.Status == NavStatus.Failed
                                            && (ri.Request.Flags & NavRequestFlags.RetriedAfterChange) == 0;
                        if (staleFailure)
                        {
                            arr[i] = 0;
                        }
                        _valid[i] = arr[i];
                        if (arr[i] == 0)
                        {
                            NavRequest again = ri.Request;
                            if (staleFailure)
                            {
                                again.Flags |= NavRequestFlags.RetriedAfterChange;
                            }
                            _queue.Add(again);
                            Requeued++;
                        }
                    }
                }
                finally
                {
                    arr.Dispose();
                }
            }
            _adopting = true;
            _watch.Stop();
            LastAdoptMs = _watch.Elapsed.TotalMilliseconds;
        }

        public int ResultCount => _adopting ? _results.Length : 0;

        // 只在采纳期间可读（批次已完成）；作业在飞时读会触发安全检查异常。
        public NavResult Result(int i) => _adopting ? _results[i] : default;

        public bool ResultValid(int i) => _adopting && _valid[i] != 0;

        public int2 ResultPoint(int i) => _adopting ? _points[i] : default;

        /// <summary>把本批里属于 <paramref name="ownerTag"/> 的有效结果交给战斗内核（单位还在等这条才采纳）。返回采纳条数。</summary>
        public int DeliverCombat(CombatKernel k, int ownerTag)
        {
            if (!_adopting || k == null || k.IsDisposed)
            {
                return 0;
            }
            int applied = 0;
            NativeArray<int2> pts = _points.AsArray();
            for (int i = 0; i < _results.Length; i++)
            {
                NavResult r = _results[i];
                if (r.Request.OwnerTag != ownerTag || _valid[i] == 0)
                {
                    continue;
                }
                if (k.ApplyRoute(r.Request.OwnerKey, r.Request.Serial, r.Status, r.Reason, pts, r.PointStart, r.PointCount, r.End))
                {
                    applied++;
                }
            }
            return applied;
        }

        public void EndAdoption()
        {
            if (!_adopting)
            {
                return;
            }
            _adopting = false;
            _batchActive = false;
            // 标志只描述“在飞的这一批”：采纳完就清掉。否则批次结束后它还留着，写进存档与 PendingHash，
            // 读档（只在有批次在飞时恢复它）却是 false——存读档往返不一致。
            _changedInFlight = false;
            _results.Clear();
            _points.Clear();
            _batchCount = 0;
        }

        /// <summary>估算一条请求的工作量（起终点八向距离，格）：与缓存冷热无关，分批因此是确定的。</summary>
        public static int EstimateCost(in NavRequest r) => math.max(1, NavSearch.Octile(r.Start, r.Goal) / NavConst.Ortho);

        /// <summary>没有批次在飞时：把镜像里变过的区块复制进工作副本，按队列顺序取一批放到工作线程上跑，采纳步 = <paramref name="tick"/> + 延迟。</summary>
        public bool TrySchedule(long tick)
        {
            if (_batchActive || _queue.Count == 0)
            {
                return false;
            }
            _watch.Restart();
            ApplyPendingToWork();
            int n = 0;
            long budget = 0;
            _groupScratch.Clear();
            while (n < _queue.Count && n < Config.BatchMaxRequests)
            {
                NavRequest q = _queue[n];
                // 同一批里“同一类别、同一目标格、同一起点区块”的请求会共享代表路线：只有第一条按距离计工作量，其余各计 1。
                var key = (q.Class, (byte)q.Flags, q.Goal.x, q.Goal.y, NavGridOps.FloorDiv(q.Start.x, Config.ChunkSize), NavGridOps.FloorDiv(q.Start.y, Config.ChunkSize));
                int est = _groupScratch.Add(key) ? EstimateCost(q) : 1;
                if (n > 0 && budget + est > Config.BatchDistanceBudget)
                {
                    break;
                }
                budget += est;
                _batch[n] = q;
                n++;
            }
            _queue.RemoveRange(0, n);
            _batchCount = n;
            _results.Clear();
            _points.Clear();
            var job = new NavBatchJob { W = _work, Requests = _batch, Count = n, Cfg = Config, Results = _results, Points = _points };
            _handle = job.Schedule();
            JobHandle.ScheduleBatchedJobs();
            _batchActive = true;
            _changedInFlight = false;
            _scheduleTick = tick;
            _dueTick = tick + Config.LatencySteps;
            _watch.Stop();
            LastScheduleMs = _watch.Elapsed.TotalMilliseconds;
            return true;
        }

        private void ApplyPendingToWork()
        {
            if (_pendingApply.Count == 0)
            {
                return;
            }
            _applyScratch.Clear();
            _applyScratch.AddRange(_pendingApply);
            _applyScratch.Sort();
            foreach (long key in _applyScratch)
            {
                if (NavGridOps.CopyChunk(ref _mirror, ref _work.Grid, key))
                {
                    NavGridOps.Unkey(key, out int cx, out int cy);
                    NavSearch.InvalidateChunk(ref _work, cx, cy);
                }
            }
            _pendingApply.Clear();
            // 失效的区块图留在池里成了垃圾：节点池过大时整体清空（缓存是格网的纯函数，清空不改变任何结果）。
            if (_work.Nodes.Length > 400_000 || _work.Edges.Length > 4_000_000)
            {
                NavSearch.ResetGraphs(ref _work);
            }
        }

        // ─────────────────────────────── 后台长路线通道（FG6-DEF-04 / DEBT-FG0ARCH06-09）───────────────────────────────

        /// <summary>
        /// 后台通道开一条长路线：先把镜像里变过 / 新的区块同步进后台工作副本（相关抽象图失效），再在工作线程上算。与常规批次并行（各用各的工作副本），
        /// 不占常规批次的名额与延迟——单位的路线照常 6 步采纳。调用方（突袭导演）在自己定的、足够晚的固定时刻 <see cref="CollectBackground"/>：
        /// 冷启动（途经区块当场按种子生成 + 建抽象图）几百毫秒在那之前早已算完，采纳不硬等；结果只取决于开始那一刻的格网（与工作线程快慢无关），所以是确定的。
        /// 一次一条：正在算时返回 false（调用方下一步再试）。
        /// </summary>
        public bool StartBackground(in NavRequest r)
        {
            if (_bgActive)
            {
                return false;
            }
            _watch.Restart();
            new NavMirrorSyncJob { From = _mirror, To = _bg, Changed = _bgChanged }.Run();
            LastBackgroundSyncedChunks = _bgChanged[0];
            // 失效的区块图留在池里成了垃圾：节点池过大时整体清空（缓存是格网的纯函数，清空不改变任何结果）。
            if (_bg.Nodes.Length > 400_000 || _bg.Edges.Length > 4_000_000)
            {
                NavSearch.ResetGraphs(ref _bg);
            }
            _bgReq[0] = r;
            _bgRes.Clear();
            _bgPts.Clear();
            _bgHandle = new NavBatchJob { W = _bg, Requests = _bgReq, Count = 1, Cfg = Config, Results = _bgRes, Points = _bgPts }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            _bgActive = true;
            BackgroundOwnerTag = r.OwnerTag;
            BackgroundOwnerKey = r.OwnerKey;
            BackgroundSerial = r.Serial;
            BackgroundJobs++;
            _watch.Stop();
            LastBackgroundSyncMs = _watch.Elapsed.TotalMilliseconds;
            return true;
        }

        /// <summary>取回后台通道那一条的结果（路点按顺序放进 <paramref name="into"/>，结果的 PointStart 置 0）。还没算完就等（计入 <see cref="BackgroundLateCompletes"/>）。</summary>
        public bool CollectBackground(List<int2> into, out NavResult result)
        {
            result = default;
            into?.Clear();
            if (!_bgActive)
            {
                return false;
            }
            if (!_bgHandle.IsCompleted)
            {
                BackgroundLateCompletes++;
            }
            _bgHandle.Complete();
            _bgHandle = default;
            _bgActive = false;
            BackgroundOwnerTag = -1;
            BackgroundOwnerKey = -1;
            if (_bgRes.Length == 0)
            {
                return false;
            }
            result = _bgRes[0];
            if (into != null)
            {
                for (int i = result.PointStart; i < result.PointStart + result.PointCount; i++)
                {
                    into.Add(_bgPts[i]);
                }
            }
            result.PointStart = 0;
            return true;
        }

        /// <summary>放弃后台通道那一条（调用方不再要了：计划被合并 / 取消、换了战役）。会等它算完再释放。</summary>
        public void CancelBackground()
        {
            _bgHandle.Complete();
            _bgHandle = default;
            _bgActive = false;
            BackgroundOwnerTag = -1;
            BackgroundOwnerKey = -1;
        }

        /// <summary>等在飞的批次完成（不采纳）。存档、放置预览、自检前调用；采纳仍在原定的采纳步。</summary>
        public void CompleteInFlight()
        {
            _handle.Complete();
            _handle = default;
        }

        // ─────────────────────────────── 同步查询（自检 / 性能）───────────────────────────────

        /// <summary>
        /// 立即算一条（不经队列）：<paramref name="onWorker"/> = 调度到工作线程再等它完成（测“工作线程耗时”），否则主线程 Burst。
        /// 必须没有批次在飞。<paramref name="ms"/> = 从调度到完成的墙钟时间。
        /// </summary>
        public NavResult FindNow(in NavRequest r, List<int2> into, bool onWorker, out double ms)
        {
            if (_batchActive)
            {
                throw new InvalidOperationException("有寻路批次在飞，不能同步查询");
            }
            ApplyPendingToWork();
            var req = new NativeArray<NavRequest>(1, Allocator.TempJob);
            var res = new NativeList<NavResult>(1, Allocator.TempJob);
            var pts = new NativeList<int2>(256, Allocator.TempJob);
            try
            {
                req[0] = r;
                var job = new NavBatchJob { W = _work, Requests = req, Count = 1, Cfg = Config, Results = res, Points = pts };
                _watch.Restart();
                if (onWorker)
                {
                    JobHandle h = job.Schedule();
                    JobHandle.ScheduleBatchedJobs();
                    h.Complete();
                }
                else
                {
                    job.Run();
                }
                _watch.Stop();
                ms = _watch.Elapsed.TotalMilliseconds;
                _counters = _work.Counters[0];
                NavResult result = res[0];
                into?.Clear();
                if (into != null)
                {
                    for (int i = result.PointStart; i < result.PointStart + result.PointCount; i++)
                    {
                        into.Add(pts[i]);
                    }
                }
                result.PointStart = 0;
                return result;
            }
            finally
            {
                req.Dispose();
                res.Dispose();
                pts.Dispose();
            }
        }

        /// <summary>工作副本里已有的区块数 / 已建的区块图数（自检：冷热缓存对照）。</summary>
        public int WorkChunkCount
        {
            get
            {
                CompleteInFlight();
                return _work.Grid.ChunkCount;
            }
        }

        /// <summary>清空工作副本的抽象图缓存（自检：证明冷缓存与热缓存结果相同）。</summary>
        public void ResetWorkGraphsForTests()
        {
            CompleteInFlight();
            NavSearch.ResetGraphs(ref _work);
        }

        // ─────────────────────────────── 快照 ───────────────────────────────

        /// <summary>
        /// 排队的请求、在飞批次的结果（强制完成；带调度步与采纳步）、上次同步后变化的区块。格式版本 <see cref="NavConst.FormatVersion"/>，末尾 FNV-1a 32 校验和。
        /// 格网本身不进快照：读档时按格网（种子 + 区块差异 + 建筑）重建镜像。
        /// </summary>
        public byte[] SerializePending(IReadOnlyList<long> extraChanged = null)
        {
            CompleteInFlight();
            using var ms = new MemoryStream(256 + _queue.Count * 48 + _results.Length * 72 + _points.Length * 8);
            using var w = new BinaryWriter(ms);
            w.Write(NavConst.Magic);
            w.Write(NavConst.FormatVersion);
            w.Write(_queue.Count);
            foreach (NavRequest r in _queue)
            {
                WriteRequest(w, r);
            }
            w.Write(_batchActive);
            w.Write(_scheduleTick);
            w.Write(_dueTick);
            w.Write(_changedInFlight);
            int rn = _batchActive ? _results.Length : 0;
            w.Write(rn);
            for (int i = 0; i < rn; i++)
            {
                NavResult r = _results[i];
                WriteRequest(w, r.Request);
                w.Write((byte)r.Status);
                w.Write((byte)r.Reason);
                w.Write(r.Shared);
                w.Write(r.PointStart);
                w.Write(r.PointCount);
                w.Write(r.End.x);
                w.Write(r.End.y);
                w.Write(r.Length);
                w.Write(r.Expanded);
            }
            int pn = _batchActive ? _points.Length : 0;
            w.Write(pn);
            for (int i = 0; i < pn; i++)
            {
                w.Write(_points[i].x);
                w.Write(_points[i].y);
            }
            var changed = new List<long>(_changed);
            if (extraChanged != null)
            {
                foreach (long k in extraChanged)
                {
                    if (!_changedSet.Contains(k))
                    {
                        changed.Add(k);
                    }
                }
            }
            w.Write(changed.Count);
            foreach (long k in changed)
            {
                w.Write(k);
            }
            w.Flush();
            byte[] body = ms.ToArray();
            uint sum = Fnv32(body, body.Length);
            var result = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = (byte)sum;
            result[body.Length + 1] = (byte)(sum >> 8);
            result[body.Length + 2] = (byte)(sum >> 16);
            result[body.Length + 3] = (byte)(sum >> 24);
            return result;
        }

        /// <summary>从快照恢复排队请求、待采纳结果与变化区块。失败时内核保持原样。</summary>
        public NavLoadResult LoadPending(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return NavLoadResult.Empty;
            }
            if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != NavConst.Magic)
            {
                return NavLoadResult.BadMagic;
            }
            int version = BitConverter.ToInt32(data, 4);
            if (version != NavConst.FormatVersion && version != 1)
            {
                return NavLoadResult.UnknownFormat;
            }
            int bodyLen = data.Length - 4;
            uint expect = (uint)(data[bodyLen] | (data[bodyLen + 1] << 8) | (data[bodyLen + 2] << 16) | (data[bodyLen + 3] << 24));
            if (Fnv32(data, bodyLen) != expect)
            {
                return NavLoadResult.BadChecksum;
            }
            var queue = new List<NavRequest>();
            var results = new List<NavResult>();
            var points = new List<int2>();
            var changed = new List<long>();
            bool active;
            long sched;
            long due;
            bool changedInFlight = false;
            try
            {
                using var ms = new MemoryStream(data, 0, bodyLen, false);
                using var r = new BinaryReader(ms);
                r.ReadUInt32();
                r.ReadInt32();
                int qn = r.ReadInt32();
                if (qn < 0 || qn > 1 << 20)
                {
                    return NavLoadResult.InvalidValue;
                }
                for (int i = 0; i < qn; i++)
                {
                    queue.Add(ReadRequest(r));
                }
                active = r.ReadBoolean();
                sched = r.ReadInt64();
                due = r.ReadInt64();
                if (version >= 2)
                {
                    changedInFlight = r.ReadBoolean();
                }
                int rn = r.ReadInt32();
                if (rn < 0 || rn > 1 << 16)
                {
                    return NavLoadResult.InvalidValue;
                }
                for (int i = 0; i < rn; i++)
                {
                    var res = new NavResult
                    {
                        Request = ReadRequest(r),
                        Status = (NavStatus)r.ReadByte(),
                        Reason = (NavFailReason)r.ReadByte(),
                        Shared = r.ReadByte(),
                        PointStart = r.ReadInt32(),
                        PointCount = r.ReadInt32(),
                        End = new int2(r.ReadInt32(), r.ReadInt32()),
                        Length = r.ReadSingle(),
                        Expanded = r.ReadInt32(),
                    };
                    results.Add(res);
                }
                int pn = r.ReadInt32();
                if (pn < 0 || pn > 1 << 24)
                {
                    return NavLoadResult.InvalidValue;
                }
                for (int i = 0; i < pn; i++)
                {
                    points.Add(new int2(r.ReadInt32(), r.ReadInt32()));
                }
                int cn = r.ReadInt32();
                if (cn < 0 || cn > 1 << 20)
                {
                    return NavLoadResult.InvalidValue;
                }
                for (int i = 0; i < cn; i++)
                {
                    changed.Add(r.ReadInt64());
                }
                if (ms.Position != bodyLen)
                {
                    return NavLoadResult.Truncated;
                }
            }
            catch (EndOfStreamException)
            {
                return NavLoadResult.Truncated;
            }
            foreach (NavResult res in results)
            {
                if (res.PointStart < 0 || res.PointCount < 0 || res.PointStart + res.PointCount > points.Count || res.Status > NavStatus.Failed)
                {
                    return NavLoadResult.InvalidValue;
                }
            }
            CompleteInFlight();
            _queue.Clear();
            _queue.AddRange(queue);
            _results.Clear();
            _points.Clear();
            foreach (NavResult res in results)
            {
                _results.Add(res);
            }
            foreach (int2 p in points)
            {
                _points.Add(p);
            }
            _batchActive = active;
            _batchCount = results.Count;
            _scheduleTick = sched;
            _dueTick = due;
            _changedInFlight = active && changedInFlight;
            _adopting = false;
            ClearChanged();
            foreach (long k in changed)
            {
                MarkChanged(k);
            }
            return NavLoadResult.Ok;
        }

        private static void WriteRequest(BinaryWriter w, in NavRequest r)
        {
            w.Write(r.OwnerTag);
            w.Write(r.OwnerKey);
            w.Write(r.Serial);
            w.Write(r.Class);
            w.Write((byte)r.Flags);
            w.Write(r.Start.x);
            w.Write(r.Start.y);
            w.Write(r.Goal.x);
            w.Write(r.Goal.y);
            w.Write(r.IssuedTick);
        }

        private static NavRequest ReadRequest(BinaryReader r) => new NavRequest
        {
            OwnerTag = r.ReadInt32(),
            OwnerKey = r.ReadInt32(),
            Serial = r.ReadInt32(),
            Class = r.ReadByte(),
            Flags = (NavRequestFlags)r.ReadByte(),
            Start = new int2(r.ReadInt32(), r.ReadInt32()),
            Goal = new int2(r.ReadInt32(), r.ReadInt32()),
            IssuedTick = r.ReadInt64(),
        };

        public static int PeekFormat(byte[] data)
        {
            if (data == null || data.Length < 8 || BitConverter.ToUInt32(data, 0) != NavConst.Magic)
            {
                return -1;
            }
            return BitConverter.ToInt32(data, 4);
        }

        private static uint Fnv32(byte[] data, int length)
        {
            uint h = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            return h;
        }

        /// <summary>待处理状态的哈希（排队请求 + 待采纳结果 + 路点）：自检比对“读档接着跑 = 不存档”。</summary>
        public ulong PendingHash()
        {
            CompleteInFlight();
            ulong h = 14695981039346656037UL;
            void Mix(long v)
            {
                for (int b = 0; b < 8; b++)
                {
                    h ^= (byte)(v >> (b * 8));
                    h *= 1099511628211UL;
                }
            }
            Mix(_queue.Count);
            foreach (NavRequest r in _queue)
            {
                Mix(r.OwnerTag);
                Mix(r.OwnerKey);
                Mix(r.Serial);
                Mix(r.Goal.x);
                Mix(r.Goal.y);
            }
            Mix(_batchActive ? 1 : 0);
            Mix(_dueTick);
            Mix(_changedInFlight ? 1 : 0);
            if (_batchActive)
            {
                for (int i = 0; i < _results.Length; i++)
                {
                    NavResult r = _results[i];
                    Mix(r.Request.OwnerKey);
                    Mix(r.Request.Serial);
                    Mix((int)r.Status);
                    Mix(r.PointCount);
                    for (int k = r.PointStart; k < r.PointStart + r.PointCount; k++)
                    {
                        Mix(_points[k].x);
                        Mix(_points[k].y);
                    }
                }
            }
            return h;
        }
    }
}
