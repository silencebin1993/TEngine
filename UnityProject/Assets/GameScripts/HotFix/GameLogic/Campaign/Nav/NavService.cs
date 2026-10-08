using System;
using System.Collections.Generic;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;

namespace GameLogic.Campaign.Nav
{
    /// <summary>
    /// FG0-ARCH-06（FG14 FGR-ARC-015 层级寻路）：星球表面（家园所在的表面）的寻路服务——寻路内核（<see cref="NavKernel"/>，AOT + Burst）的桥接层，
    /// 与传送带的 BeltNetworkService、战斗的 CombatSite 同一角色。热更层碰寻路内核只经这里。
    ///
    /// 每个模拟步开头（<see cref="BeginStep"/>，由 WorldSimulation 调用；只看步序号，与镜头、帧率、倍速无关）：
    /// 1. 格网里通行可能变化的区块（建筑增删、地形改写、读档差异）推进内核镜像；
    /// 2. 到了采纳步的批次：在镜像上校验后交给请求方——家园战斗内核（机器、突袭者，AOT 直接交付）、行进中的队伍、巡逻；
    /// 3. 有区块变化时：各请求方检查手里的路线，被新障碍截断的重新要路线；
    /// 4. 收集家园战斗内核本步新发出的请求，没有批次在飞时调度下一批（工作线程）。
    /// 热更层开销 = O(变化的区块 + 本批结果条数 + 队伍 / 巡逻数)，与单位数、路线长度无关。
    /// 生命周期与家园同步：<see cref="Bind"/>（家园载入前）/ <see cref="Unload"/>（整个世界卸载）。
    /// </summary>
    public static class NavService
    {
        /// <summary>请求方标签（结果按它交付；存档里原样保留）。</summary>
        public const int OwnerHomeCombat = 1;
        public const int OwnerTransit = 2;
        public const int OwnerPatrol = 3;
        /// <summary>FG6-DEF-04：突袭导演的计划（排定突袭时沿地形算行进时间 = 预警时间；走后台长路线通道，见 <see cref="StartBackground"/>）。</summary>
        public const int OwnerRaidPlan = 4;

        public static NavKernel Kernel { get; private set; }
        public static CampaignState BoundState { get; private set; }
        public static string SurfaceId { get; private set; }

        /// <summary>旧原型地形（没有世界生成参数）只覆盖核心附近这么多区块（格网同步生成后推送）；之外视为地形未知。</summary>
        public const int PrototypeRadiusChunks = 4;

        private static HomeGridMap _map;
        private static byte[] _occBlock = new byte[64];
        private static readonly List<long> Dirty = new List<long>(64);
        private static readonly List<long> PeekScratch = new List<long>(64);
        private static bool _restoreFailed;
        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

        public static long PushedChunks { get; private set; }
        public static long StepsRun { get; private set; }
        public static double LastBeginStepMs { get; private set; }
        public static double MaxBeginStepMs { get; private set; }
        public static int LastDelivered { get; private set; }
        public static long TotalDelivered { get; private set; }
        public static long InvalidationPasses { get; private set; }
        public static string LastLoadProblem { get; private set; }

        public static bool IsBound => Kernel != null && !Kernel.IsDisposed;

        // ─────────────────────────────── 配置 ───────────────────────────────

        public static NavConfig ConfigFromTuning()
        {
            NavConfig c = NavConfig.Default;
            c.ChunkSize = GridContent.TuningInt("grid.chunk_size");
            c.LatencySteps = TuningInt("nav.latency_steps", c.LatencySteps);
            c.BatchMaxRequests = TuningInt("nav.batch_max_requests", c.BatchMaxRequests);
            c.BatchDistanceBudget = TuningInt("nav.batch_distance_budget", c.BatchDistanceBudget);
            c.SearchMarginChunks = TuningInt("nav.search_margin_chunks", c.SearchMarginChunks);
            c.MaxExpansions = TuningInt("nav.max_expansions", c.MaxExpansions);
            c.GoalSearchRadius = TuningInt("nav.goal_search_radius", c.GoalSearchRadius);
            c.StartSearchRadius = TuningInt("nav.start_search_radius", c.StartSearchRadius);
            c.SmoothLookahead = TuningInt("nav.smooth_lookahead", c.SmoothLookahead);
            c.CoordLimit = GridContent.TryGetTuning("world.coord_limit", out float lim) ? (int)Math.Round(lim) : c.CoordLimit;
            return c;
        }

        public static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Error($"[NavService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_nav.py 后重新生成）。");
            return fallback;
        }

        private static int TuningInt(string id, int fallback) => (int)Math.Round(Tuning(id, fallback));

        /// <summary>地形码 → 通行字节（fg.TbGridTerrain.navCost：0 = 全部类别不可通行；否则代价倍率写进高 4 位）。表里没有的码 = 不可通行。</summary>
        public static byte[] TerrainTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                t[i] = NavConst.Solid;
            }
            foreach (GridTerrain row in GridContent.Terrains)
            {
                if (row == null || row.Code < 0 || row.Code > 255)
                {
                    continue;
                }
                int cost = Math.Max(0, Math.Min(15, row.NavCost));
                t[row.Code] = cost == 0 ? NavConst.Solid : (byte)(cost << 4);
            }
            return t;
        }

        // ─────────────────────────────── 生命周期 ───────────────────────────────

        /// <summary>
        /// 为战役的星球表面建寻路内核（家园载入前调用）：镜像按格网建好基线（被修改过的 / 有建筑的区块；读档差异所在的区块先同步载入），
        /// 再从存档恢复排队的请求与待采纳的结果（<see cref="NavState"/>）。
        /// </summary>
        public static void Bind(CampaignState state)
        {
            Unload();
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            _map = HomeGridService.MapFor(state);
            SurfaceId = _map.SurfaceId;
            BoundState = state;
            NavConfig cfg = ConfigFromTuning();
            bool hasGen = _map.TerrainSource is WorldTerrainSource;
            WorldGenParams gen = default;
            WorldGenRect[] rects = null;
            WorldGenZone[] zones = null;
            if (hasGen)
            {
                var src = (WorldTerrainSource)_map.TerrainSource;
                gen = src.Params;
                rects = src.Rects;
                zones = src.Zones;
            }
            Kernel = new NavKernel(cfg, TerrainTable(), hasGen, gen, rects, zones);

            // 读档差异所在的区块：同步载入（套上差异），镜像以它为准（否则内核会按种子生成出“没被修改过”的样子）。
            foreach (ChunkDiffRecord r in state.World?.ChunkDiffs ?? Array.Empty<ChunkDiffRecord>())
            {
                if (r != null && r.SurfaceId == SurfaceId)
                {
                    _map.ChunkAt(new GridCell(r.ChunkX * _map.ChunkSize, r.ChunkY * _map.ChunkSize), out _);
                }
            }
            if (!hasGen)
            {
                // 旧原型地形：内核不会就地生成，核心附近固定范围的区块同步生成后全部推送（与镜头无关）。
                GridCell core = HomeGridService.CorePivot(state);
                ChunkAddress a = GridMath.Address(core, _map.ChunkSize);
                for (int dy = -PrototypeRadiusChunks; dy <= PrototypeRadiusChunks; dy++)
                {
                    for (int dx = -PrototypeRadiusChunks; dx <= PrototypeRadiusChunks; dx++)
                    {
                        _map.ChunkAt(new GridCell((a.ChunkX + dx) * _map.ChunkSize, (a.ChunkY + dy) * _map.ChunkSize), out _);
                    }
                }
            }
            var keys = new List<long>();
            foreach (HomeGridMap.Chunk c in _map.LoadedChunks)
            {
                if (!hasGen || c.Modified || c.HasStructures())
                {
                    keys.Add(HomeGridMap.Key(c.ChunkX, c.ChunkY));
                }
            }
            keys.Sort();
            foreach (long key in keys)
            {
                Push(key, asChange: false);
            }
            _map.ClearNavDirty();

            _restoreFailed = false;
            LastLoadProblem = null;
            string payload = state.Nav?.Payload;
            if (!string.IsNullOrEmpty(payload) && state.Nav.SurfaceId == SurfaceId)
            {
                NavLoadResult lr;
                try
                {
                    lr = Kernel.LoadPending(Convert.FromBase64String(payload));
                }
                catch (FormatException)
                {
                    lr = NavLoadResult.BadMagic;
                }
                if (lr != NavLoadResult.Ok)
                {
                    // 读不了：丢掉排队的请求，等路线的请求方全部重新要（载入完成后，AfterSitesLoaded）。如实告知。
                    _restoreFailed = true;
                    LastLoadProblem = lr.ToString();
                    Log.Warning($"[NavService] 寻路快照读不了（{lr}），等路线的单位将重新规划路线。");
                    NotificationCenter.Post("save_migrated", GameText.Get("nav.load.rebuilt"));
                }
            }
        }

        /// <summary>家园（及其战斗内核）载入完成后：寻路快照读不了时，让所有“等路线”的请求方重新发请求（不会永远等下去）。</summary>
        public static void AfterSitesLoaded(CampaignState state)
        {
            if (!_restoreFailed || !IsBound)
            {
                return;
            }
            _restoreFailed = false;
            CombatSites.Get(HomeValleyLayout.RegionId)?.ReissueAwaitingRoutes();
            WorldTransitSystem.ReissueAwaiting(state);
            WorldOutpostSystem.ReissueAwaiting(state);
            Defense.RaidDirectorService.ReissueAwaiting(state);
        }

        public static void Unload()
        {
            CombatSites.Get(HomeValleyLayout.RegionId)?.UnbindNav();
            Kernel?.Dispose();
            Kernel = null;
            _map = null;
            BoundState = null;
            Dirty.Clear();
        }

        /// <summary>家园战斗内核打开时绑定镜像（碰撞、路线检查都读它）。</summary>
        public static void BindCombat(CombatSite site)
        {
            if (site != null && IsBound)
            {
                site.BindNav(Kernel);
            }
        }

        // ─────────────────────────────── 格网变化 ───────────────────────────────

        private static void Push(long key, bool asChange)
        {
            HomeGridMap.Unkey(key, out int cx, out int cy);
            HomeGridMap.Chunk c = _map.TryGetLoaded(cx, cy) ?? _map.ChunkAt(new GridCell(cx * _map.ChunkSize, cy * _map.ChunkSize), out _);
            int oc = _map.OccupantCount;
            if (_occBlock.Length < oc)
            {
                _occBlock = new byte[Math.Max(oc, _occBlock.Length * 2)];
            }
            for (int i = 0; i < oc; i++)
            {
                _occBlock[i] = NavConst.BlockAll; // 建筑挡住全部移动类别
            }
            // FG6-DEF-02（FGR-DEF-010 / 011）：屏障 / 闸门按状态改挡路位——建成的闸门只挡敌方类别（己方机器通过，写死的规则）；
            // 还是虚影 / 已被摧毁的屏障与闸门不挡路（敌人打穿墙就能过去）。O(防御建筑数)，只在推进区块时。
            Defense.DefenseService.ApplyNavBlockBits(BoundState, _map, _occBlock, oc);
            Kernel.PushChunk(cx, cy, c.Terrain, c.Occupancy, _occBlock, oc, asChange);
            PushedChunks++;
        }

        /// <summary>把格网里通行可能变化的区块推进镜像（每步开头；放置预览与存档前也调用——只是提前，不改变哪一步生效）。</summary>
        public static void SyncGridChanges()
        {
            if (!IsBound || _map == null)
            {
                return;
            }
            HomeGridMap current = HomeGridService.BoundMap(BoundState);
            if (current != null && !ReferenceEquals(current, _map))
            {
                // 格网被重建（内容表重载 / 生成参数变化）：改指向新格网，把它的已修改 / 有建筑的区块整体推一遍（按变化处理）。
                Log.Warning("[NavService] 格网实例被替换，重新推送已修改 / 有建筑的区块。");
                _map = current;
                var keys = new List<long>();
                foreach (HomeGridMap.Chunk c in _map.LoadedChunks)
                {
                    if (c.Modified || c.HasStructures())
                    {
                        keys.Add(HomeGridMap.Key(c.ChunkX, c.ChunkY));
                    }
                }
                keys.Sort();
                foreach (long key in keys)
                {
                    Push(key, asChange: true);
                }
                _map.ClearNavDirty();
                return;
            }
            if (_map.NavDirtyCount == 0)
            {
                return;
            }
            _map.DrainNavDirty(Dirty);
            foreach (long key in Dirty)
            {
                Push(key, asChange: true);
            }
            Dirty.Clear();
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>一个模拟步开头的寻路流水线（见类注释）。</summary>
        public static void BeginStep(CampaignState state)
        {
            if (!IsBound || state == null || !ReferenceEquals(state, BoundState))
            {
                return;
            }
            Watch.Restart();
            long tick = GameClock.Ticks;
            SyncGridChanges();
            CombatSite home = CombatSites.Get(HomeValleyLayout.RegionId);
            LastDelivered = 0;
            if (Kernel.IsDue(tick))
            {
                Kernel.BeginAdoption();
                if (home != null)
                {
                    LastDelivered += home.DeliverRoutes(Kernel, OwnerHomeCombat);
                }
                int n = Kernel.ResultCount;
                for (int i = 0; i < n; i++)
                {
                    NavResult r = Kernel.Result(i);
                    if (!Kernel.ResultValid(i))
                    {
                        continue;
                    }
                    if (r.Request.OwnerTag == OwnerTransit)
                    {
                        LastDelivered += WorldTransitSystem.DeliverRoute(state, r) ? 1 : 0;
                    }
                    else if (r.Request.OwnerTag == OwnerPatrol)
                    {
                        LastDelivered += WorldOutpostSystem.DeliverRoute(state, r, tick) ? 1 : 0;
                    }
                }
                Kernel.EndAdoption();
                TotalDelivered += LastDelivered;
            }
            if (Kernel.ChangedCount > 0)
            {
                // 地形变了：手里的路线被新障碍截断的重新要（按当前位置）。只查“路线上的格子现在走不走得了”，没挡到的不动。
                int invalid = 0;
                if (home != null)
                {
                    invalid += home.InvalidateBlockedRoutes();
                }
                invalid += WorldTransitSystem.InvalidateRoutes(state);
                invalid += WorldOutpostSystem.InvalidateRoutes(state);
                Kernel.Invalidated += invalid;
                Kernel.ClearChanged();
                InvalidationPasses++;
            }
            if (home != null)
            {
                home.CollectNavRequests(Kernel, OwnerHomeCombat, tick);
            }
            Kernel.TrySchedule(tick);
            StepsRun++;
            Watch.Stop();
            LastBeginStepMs = Watch.Elapsed.TotalMilliseconds;
            if (LastBeginStepMs > MaxBeginStepMs)
            {
                MaxBeginStepMs = LastBeginStepMs;
            }
        }

        /// <summary>
        /// FG3-LOG-09（DEBT-FG0ARCH06-07）：热更层读采纳窗口里的一条路线的第 <paramref name="index"/> 个路点——队伍 / 巡逻交付路线只经本服务，不直接碰寻路内核。
        /// 只在 <see cref="BeginStep"/> 的采纳窗口里有效（内核在窗口外返回 default）。
        /// </summary>
        public static int2 ResultPoint(int index) => Kernel != null ? Kernel.ResultPoint(index) : default;

        /// <summary>FG3-LOG-09（DEBT-FG0ARCH06-07）：地形变化后查一条剩余路线是否还走得通（按当前格网；热更层只经本服务查）。没绑定时按“挡住”处理（重新要路线）。</summary>
        public static bool RouteClear(int2 start, IReadOnlyList<int2> points, int from, int cls) =>
            Kernel != null && Kernel.RouteClear(start, points, from, cls);

        /// <summary>
        /// FG6-E2E-01：这次地形变化（寻路内核的 Changed，下一次 ClearChanged 之前）有没有落在线段 <paramref name="a"/>–<paramref name="b"/> 外接框所在的区块里。
        /// 碰不到的路段不可能被这次变化新挡住（格线上的格都在外接框里），行进队伍据此只重查碰得到的路段（<see cref="WorldSim.WorldTransitSystem.InvalidateRoutes"/>）。没绑定时按“碰得到”处理。
        /// </summary>
        public static bool ChangedNear(int2 a, int2 b)
        {
            if (!IsBound)
            {
                return true;
            }
            IReadOnlyList<long> changed = Kernel.Changed;
            if (changed.Count == 0)
            {
                return false;
            }
            int size = Kernel.Config.ChunkSize;
            int cx0 = NavGridOps.FloorDiv(Math.Min(a.x, b.x), size);
            int cx1 = NavGridOps.FloorDiv(Math.Max(a.x, b.x), size);
            int cy0 = NavGridOps.FloorDiv(Math.Min(a.y, b.y), size);
            int cy1 = NavGridOps.FloorDiv(Math.Max(a.y, b.y), size);
            for (int i = 0; i < changed.Count; i++)
            {
                NavGridOps.Unkey(changed[i], out int cx, out int cy);
                if (cx >= cx0 && cx <= cx1 && cy >= cy0 && cy <= cy1)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>热更层的请求方（队伍、巡逻）发一条请求。</summary>
        public static void Request(int ownerTag, int ownerKey, int serial, byte cls, GridCell start, GridCell goal, bool allowPartial)
        {
            if (!IsBound)
            {
                return;
            }
            Kernel.Enqueue(new NavRequest
            {
                OwnerTag = ownerTag,
                OwnerKey = ownerKey,
                Serial = serial,
                Class = cls,
                Flags = allowPartial ? NavRequestFlags.AllowPartial : NavRequestFlags.None,
                Start = new int2(start.X, start.Y),
                Goal = new int2(goal.X, goal.Y),
                IssuedTick = GameClock.Ticks,
            });
        }

        /// <summary>
        /// FG6-DEF-04（DEBT-FG0ARCH06-09）：后台长路线通道开一条（独立的工作副本，与单位的常规批次并行，不占它们的 6 步延迟）。
        /// 突袭导演排定突袭时用它、在固定的较晚时刻（raid.route_latency_seconds 之后）<see cref="CollectBackground"/>：冷的长路线几百毫秒早已算完，采纳步不硬等；
        /// 结果只取决于开始那一刻的格网，确定。一次一条，正在算返回 false。没绑定返回 false。
        /// </summary>
        public static bool StartBackground(int ownerTag, int ownerKey, int serial, byte cls, GridCell start, GridCell goal, bool allowPartial)
        {
            if (!IsBound)
            {
                return false;
            }
            SyncGridChanges();
            return Kernel.StartBackground(new NavRequest
            {
                OwnerTag = ownerTag,
                OwnerKey = ownerKey,
                Serial = serial,
                Class = cls,
                Flags = allowPartial ? NavRequestFlags.AllowPartial : NavRequestFlags.None,
                Start = new int2(start.X, start.Y),
                Goal = new int2(goal.X, goal.Y),
                IssuedTick = GameClock.Ticks,
            });
        }

        public static bool BackgroundActive => IsBound && Kernel.BackgroundActive;
        public static int BackgroundOwnerKey => IsBound ? Kernel.BackgroundOwnerKey : -1;
        public static int BackgroundOwnerTag => IsBound ? Kernel.BackgroundOwnerTag : -1;
        public static int BackgroundSerial => IsBound ? Kernel.BackgroundSerial : 0;

        /// <summary>取回后台通道那一条的结果（还没算完就等）。</summary>
        public static bool CollectBackground(List<int2> into, out NavResult result)
        {
            result = default;
            return IsBound && Kernel.CollectBackground(into, out result);
        }

        public static void CancelBackground()
        {
            if (IsBound)
            {
                Kernel.CancelBackground();
            }
        }

        /// <summary>世界坐标 → 格子（与寻路内核、战斗内核同一换算：四舍五入到最近格心）。</summary>
        public static GridCell CellOf(double x, double y) => new GridCell((int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5));

        // ─────────────────────────────── 存档 ───────────────────────────────

        /// <summary>存档：把还没同步的格网变化推进镜像（只是提前，生效仍在下一步），写排队请求 / 待采纳结果 / 变化区块。</summary>
        public static void WriteTo(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            if (!IsBound || !ReferenceEquals(state, BoundState))
            {
                return;
            }
            SyncGridChanges();
            state.Nav.SurfaceId = SurfaceId;
            state.Nav.Payload = Convert.ToBase64String(Kernel.SerializePending());
        }

        // ─────────────────────────────── 放置预览（FGR-LOG-012 / FG03“放置会让某座建筑变得机器无法到达时，给出警告（不阻止）”）───────────────────────────────

        private static readonly List<int2> Sources = new List<int2>(64);
        private static readonly List<int2> Extra = new List<int2>(64);
        private static readonly List<int2> Probes = new List<int2>(512);
        private static readonly List<int> ProbeOwner = new List<int>(512);
        private static readonly List<BuildingRecord> Owners = new List<BuildingRecord>(32);
        private static readonly List<GridCell> Cells = new List<GridCell>(64);

        /// <summary>
        /// 放置预览的可达性：假设把 <paramref name="typeId"/> 放在（<paramref name="pivot"/>, <paramref name="rotation"/>），
        /// 从归还核心外一圈出发（机器的活动中心）做泛洪；原来能到、放下后到不了的建筑写进 <paramref name="newlyUnreachable"/>（显示名）。
        /// 区域 = 全部家园建筑外接矩形 + nav.preview_margin_cells。只在预览格 / 朝向变化时调用（Burst，毫秒级以下）。
        /// </summary>
        public static int PlacementCutsOff(CampaignState state, string typeId, GridCell pivot, int rotation, List<string> newlyUnreachable)
        {
            newlyUnreachable?.Clear();
            if (!IsBound || state == null || !ReferenceEquals(state, BoundState) || !GridContent.TryGetBuilding(typeId, out BuildingGrid g))
            {
                return 0;
            }
            // FG6-DEF-02 复审修复：闸门只挡敌方（写死的规则），放行机器——不会让任何建筑变得机器到不了。
            if (Defense.DefenseCatalog.KindOf(typeId) == Defense.DefenseKind.Gate)
            {
                return 0;
            }
            GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(rotation), Cells);
            return PlacementCutsOff(state, Cells, newlyUnreachable);
        }

        /// <summary>FG6-DEF-02（拖拽一段屏障）：假设把 <paramref name="footprint"/> 这些格子都挡上（己方类别），原来能到、放下后到不了的建筑。同一套 Burst 泛洪，只在拖拽终点换格时调用。</summary>
        public static int PlacementCutsOff(CampaignState state, IReadOnlyList<GridCell> footprint, List<string> newlyUnreachable)
        {
            newlyUnreachable?.Clear();
            if (!IsBound || state == null || !ReferenceEquals(state, BoundState) || footprint == null || footprint.Count == 0)
            {
                return 0;
            }
            SyncGridChanges();
            if (!HomeGridService.TryGetCoreBounds(state, out GridCell coreMin, out GridCell coreMax))
            {
                return 0;
            }
            Owners.Clear();
            Probes.Clear();
            ProbeOwner.Clear();
            Sources.Clear();
            Extra.Clear();
            int minX = coreMin.X, minY = coreMin.Y, maxX = coreMax.X, maxY = coreMax.Y;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore
                    || !GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid bg))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), bg.FootprintW, bg.FootprintH, GridMath.NormalizeRotation(b.Rotation), out GridCell bmin, out GridCell bmax);
                int owner = Owners.Count;
                Owners.Add(b);
                AddRing(bmin, bmax, owner);
                minX = Math.Min(minX, bmin.X);
                minY = Math.Min(minY, bmin.Y);
                maxX = Math.Max(maxX, bmax.X);
                maxY = Math.Max(maxY, bmax.Y);
            }
            if (Owners.Count == 0)
            {
                return 0;
            }
            foreach (GridCell c in footprint)
            {
                Extra.Add(new int2(c.X, c.Y));
                minX = Math.Min(minX, c.X);
                minY = Math.Min(minY, c.Y);
                maxX = Math.Max(maxX, c.X);
                maxY = Math.Max(maxY, c.Y);
            }
            for (int x = coreMin.X - 1; x <= coreMax.X + 1; x++)
            {
                Sources.Add(new int2(x, coreMin.Y - 1));
                Sources.Add(new int2(x, coreMax.Y + 1));
            }
            for (int y = coreMin.Y; y <= coreMax.Y; y++)
            {
                Sources.Add(new int2(coreMin.X - 1, y));
                Sources.Add(new int2(coreMax.X + 1, y));
            }
            int margin = Math.Max(2, (int)Math.Round(Tuning("nav.preview_margin_cells", 16f)));
            var min = new int2(minX - margin, minY - margin);
            var max = new int2(maxX + margin, maxY + margin);
            var before = new bool[Owners.Count];
            var after = new bool[Owners.Count];
            Kernel.Reachability(NavConst.ClassPlayer, min, max, Sources, null, Probes, ProbeOwner, before);
            Kernel.Reachability(NavConst.ClassPlayer, min, max, Sources, Extra, Probes, ProbeOwner, after);
            int n = 0;
            for (int i = 0; i < Owners.Count; i++)
            {
                if (before[i] && !after[i])
                {
                    n++;
                    newlyUnreachable?.Add(HomeGridService.DisplayName(Owners[i].BuildingTypeId));
                }
            }
            return n;
        }

        /// <summary>
        /// FG6-DEF-02（FG06 第 4 节“放置屏障时预览对流场的影响”）：敌方来路预览（镜像上的 Burst 距离场 + 下坡追踪，见 <see cref="NavKernel.FlowRoutes"/>）。
        /// 热更层碰寻路内核只经本服务（DEBT-FG0ARCH06-07）。镜像没绑定时返回 -1。
        /// </summary>
        public static int FlowRoutes(int cls, int2 min, int2 max, IReadOnlyList<int2> goals, IReadOnlyList<int2> extraBlocked, IReadOnlyList<int2> entries,
            List<int2> points, int[] offsets, int[] counts, int[] costs, IReadOnlyList<int2> breachCells = null, IReadOnlyList<int> breachPen = null) =>
            IsBound ? Kernel.FlowRoutes(cls, min, max, goals, extraBlocked, entries, points, offsets, counts, costs, breachCells, breachPen) : -1;

        /// <summary>FG6-DEF-02：镜像上这一格对某移动类别能不能走（来路预览找起点用）；镜像没绑定时 false。</summary>
        public static bool PassableNow(int x, int y, int cls) => IsBound && Kernel.Passable(x, y, cls);

        /// <summary>FG6-DEF-02：镜像实例的身份（换了寻路内核 = 缓存失效）。</summary>
        public static int MirrorIdentity => IsBound ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Kernel) : 0;

        /// <summary>FG2-FW-05：两座建筑之间能不能走通（机器搬运的路线）。</summary>
        public enum BuildingReach : byte
        {
            /// <summary>寻路镜像没绑定（家园没载入）或建筑不在格网里：无法判断。</summary>
            Unknown = 0,
            Connected = 1,
            /// <summary>出发建筑四周一圈都走不了（被建筑 / 地形围住）。</summary>
            FromEnclosed = 2,
            /// <summary>目标建筑四周一圈都走不了。</summary>
            ToEnclosed = 3,
            /// <summary>两边四周都有空地，但彼此之间没有通路（被隔断）。</summary>
            Disconnected = 4,
        }

        private static readonly List<int2> ReachSources = new List<int2>(64);
        private static readonly List<int2> ReachProbes = new List<int2>(64);
        private static readonly List<int> ReachOwner = new List<int>(64);
        private static readonly bool[] ReachResult = new bool[1];

        /// <summary>
        /// FG2-FW-05（FG02 第 5 章“固件在仓库里，但搬运路线被堵 → 说明被什么堵住”）：从 <paramref name="from"/> 外一圈可走的格出发做泛洪，
        /// 看能不能到达 <paramref name="to"/> 外一圈的任意可走格（玩家机器的通行类别，与放置预览同一个 Burst 泛洪）。
        /// 围住某一边的建筑显示名 / “地形”写进对应的 blockers（去重）。区域 = 两座建筑外接矩形 + nav.preview_margin_cells。
        /// 只在界面按间隔检查或事件触发时调用（O(区域格数)，Burst），不按帧。
        /// </summary>
        public static BuildingReach ReachBetween(CampaignState state, BuildingRecord from, BuildingRecord to, List<string> fromBlockers, List<string> toBlockers, string terrainLabel)
        {
            fromBlockers?.Clear();
            toBlockers?.Clear();
            if (!IsBound || state == null || !ReferenceEquals(state, BoundState) || from == null || to == null
                || !GridContent.TryGetBuilding(from.BuildingTypeId, out BuildingGrid fg) || !GridContent.TryGetBuilding(to.BuildingTypeId, out BuildingGrid tg))
            {
                return BuildingReach.Unknown;
            }
            SyncGridChanges();
            GridMath.FootprintBounds(new GridCell(from.GridX, from.GridY), fg.FootprintW, fg.FootprintH, GridMath.NormalizeRotation(from.Rotation), out GridCell fmin, out GridCell fmax);
            GridMath.FootprintBounds(new GridCell(to.GridX, to.GridY), tg.FootprintW, tg.FootprintH, GridMath.NormalizeRotation(to.Rotation), out GridCell tmin, out GridCell tmax);
            ReachSources.Clear();
            ReachProbes.Clear();
            ReachOwner.Clear();
            PassableRing(state, from, fmin, fmax, ReachSources, null, fromBlockers, terrainLabel);
            PassableRing(state, to, tmin, tmax, ReachProbes, ReachOwner, toBlockers, terrainLabel);
            if (ReachSources.Count == 0)
            {
                return BuildingReach.FromEnclosed;
            }
            if (ReachProbes.Count == 0)
            {
                return BuildingReach.ToEnclosed;
            }
            int margin = Math.Max(2, (int)Math.Round(Tuning("nav.preview_margin_cells", 16f)));
            var min = new int2(Math.Min(fmin.X, tmin.X) - margin, Math.Min(fmin.Y, tmin.Y) - margin);
            var max = new int2(Math.Max(fmax.X, tmax.X) + margin, Math.Max(fmax.Y, tmax.Y) + margin);
            ReachResult[0] = false;
            Kernel.Reachability(NavConst.ClassPlayer, min, max, ReachSources, null, ReachProbes, ReachOwner, ReachResult);
            return ReachResult[0] ? BuildingReach.Connected : BuildingReach.Disconnected;
        }

        private static readonly List<int2> SiteSources = new List<int2>(128);
        private static readonly List<int2> SiteProbes = new List<int2>(256);
        private static readonly List<int> SiteOwner = new List<int>(256);
        private static readonly List<int2> SiteRing = new List<int2>(64);

        /// <summary>
        /// FG4-ECO-10（FG04 FGR-ECO-072“某座建筑机器无法到达，施工或维修永远无法完成”）：一批施工 / 维修目标能不能被机器走到。
        /// 从归还核心外一圈与 <paramref name="machineCells"/>（家园机器此刻所在的格）出发做一次泛洪（玩家机器的通行类别，与放置预览同一个 Burst 泛洪），
        /// 每座目标外一圈只要有一格被泛洪到就是能到。四周一圈都走不了的目标 <paramref name="enclosed"/> = true，挡住它的建筑名 / 地形写进 <paramref name="blockers"/>。
        /// 区域 = 核心与全部目标的外接矩形 + nav.preview_margin_cells（机器格不在区域里的不当出发点）。返回 false = 寻路镜像没绑定，无法判断（调用方不改任何状态）。
        /// 只在检查间隔到了、且有施工 / 维修工单时调用（O(区域格数)，Burst），不按帧。
        /// </summary>
        public static bool SitesReachable(CampaignState state, IReadOnlyList<BuildingRecord> sites, IReadOnlyList<GridCell> machineCells,
            bool[] reached, bool[] enclosed, List<string>[] blockers, string terrainLabel)
        {
            if (!IsBound || state == null || !ReferenceEquals(state, BoundState) || sites == null || sites.Count == 0)
            {
                return false;
            }
            SyncGridChanges();
            if (!HomeGridService.TryGetCoreBounds(state, out GridCell coreMin, out GridCell coreMax))
            {
                return false;
            }
            SiteSources.Clear();
            SiteProbes.Clear();
            SiteOwner.Clear();
            int minX = coreMin.X, minY = coreMin.Y, maxX = coreMax.X, maxY = coreMax.Y;
            for (int i = 0; i < sites.Count; i++)
            {
                reached[i] = false;
                enclosed[i] = false;
                blockers?[i]?.Clear();
                BuildingRecord b = sites[i];
                if (b == null || !GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g))
                {
                    reached[i] = true; // 不在格网里的目标：无法判断，当作能到（不误报）。
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation), out GridCell bmin, out GridCell bmax);
                SiteRing.Clear();
                PassableRing(state, b, bmin, bmax, SiteRing, null, blockers?[i], terrainLabel);
                if (SiteRing.Count == 0)
                {
                    enclosed[i] = true;
                    continue;
                }
                foreach (int2 c in SiteRing)
                {
                    SiteProbes.Add(c);
                    SiteOwner.Add(i);
                }
                minX = Math.Min(minX, bmin.X);
                minY = Math.Min(minY, bmin.Y);
                maxX = Math.Max(maxX, bmax.X);
                maxY = Math.Max(maxY, bmax.Y);
            }
            if (SiteProbes.Count == 0)
            {
                return true;
            }
            int margin = Math.Max(2, (int)Math.Round(Tuning("nav.preview_margin_cells", 16f)));
            var min = new int2(minX - margin, minY - margin);
            var max = new int2(maxX + margin, maxY + margin);
            for (int x = coreMin.X - 1; x <= coreMax.X + 1; x++)
            {
                SiteSources.Add(new int2(x, coreMin.Y - 1));
                SiteSources.Add(new int2(x, coreMax.Y + 1));
            }
            for (int y = coreMin.Y; y <= coreMax.Y; y++)
            {
                SiteSources.Add(new int2(coreMin.X - 1, y));
                SiteSources.Add(new int2(coreMax.X + 1, y));
            }
            if (machineCells != null)
            {
                foreach (GridCell c in machineCells)
                {
                    if (c.X >= min.x && c.X <= max.x && c.Y >= min.y && c.Y <= max.y)
                    {
                        SiteSources.Add(new int2(c.X, c.Y));
                    }
                }
            }
            var result = new bool[sites.Count];
            Kernel.Reachability(NavConst.ClassPlayer, min, max, SiteSources, null, SiteProbes, SiteOwner, result);
            for (int i = 0; i < sites.Count; i++)
            {
                if (!reached[i] && !enclosed[i])
                {
                    reached[i] = result[i];
                }
            }
            return true;
        }

        /// <summary>建筑外一圈：可走的格进 <paramref name="into"/>；走不了的格上压着的建筑名（不含自己）或“地形”进 <paramref name="blockers"/>。</summary>
        private static void PassableRing(CampaignState state, BuildingRecord self, GridCell bmin, GridCell bmax, List<int2> into, List<int> owner, List<string> blockers, string terrainLabel)
        {
            for (int x = bmin.X - 1; x <= bmax.X + 1; x++)
            {
                for (int y = bmin.Y - 1; y <= bmax.Y + 1; y++)
                {
                    bool edge = x == bmin.X - 1 || x == bmax.X + 1 || y == bmin.Y - 1 || y == bmax.Y + 1;
                    if (!edge)
                    {
                        continue;
                    }
                    if (Kernel.Passable(x, y, NavConst.ClassPlayer))
                    {
                        into.Add(new int2(x, y));
                        owner?.Add(0);
                        continue;
                    }
                    if (blockers == null)
                    {
                        continue;
                    }
                    BuildingRecord b = HomeGridService.BuildingAt(state, new GridCell(x, y));
                    string name = b != null && !ReferenceEquals(b, self) ? HomeGridService.DisplayName(b.BuildingTypeId) : terrainLabel;
                    if (!string.IsNullOrEmpty(name) && !blockers.Contains(name))
                    {
                        blockers.Add(name);
                    }
                }
            }
        }

        private static void AddRing(GridCell bmin, GridCell bmax, int owner)
        {
            for (int x = bmin.X; x <= bmax.X; x++)
            {
                Probes.Add(new int2(x, bmin.Y - 1));
                ProbeOwner.Add(owner);
                Probes.Add(new int2(x, bmax.Y + 1));
                ProbeOwner.Add(owner);
            }
            for (int y = bmin.Y; y <= bmax.Y; y++)
            {
                Probes.Add(new int2(bmin.X - 1, y));
                ProbeOwner.Add(owner);
                Probes.Add(new int2(bmax.X + 1, y));
                ProbeOwner.Add(owner);
            }
        }

        // ─────────────────────────────── 文本 ───────────────────────────────

        public static string FailKey(NavFailReason r)
        {
            switch (r)
            {
                case NavFailReason.StartBlocked: return "nav.fail.start_blocked";
                case NavFailReason.GoalBlocked: return "nav.fail.goal_blocked";
                case NavFailReason.SearchLimit: return "nav.fail.search_limit";
                case NavFailReason.OutOfWorld: return "nav.fail.out_of_world";
                case NavFailReason.UnknownTerrain: return "nav.fail.unknown_terrain";
                default: return "nav.fail.unreachable";
            }
        }

        /// <summary>失败原因（当前语言）；目标在未探索区域时追加说明。</summary>
        public static string FailText(NavFailReason r, GridCell goal)
        {
            string text = GameText.Get(FailKey(r));
            if (_map != null && !_map.IsExploredNoLoad(goal))
            {
                text += GameText.Get("nav.fail.in_fog");
            }
            return text;
        }

        /// <summary>重置本会话的计数（新战役 / 读档）。</summary>
        public static void ResetCounters()
        {
            PushedChunks = 0;
            StepsRun = 0;
            LastBeginStepMs = 0;
            MaxBeginStepMs = 0;
            LastDelivered = 0;
            TotalDelivered = 0;
            InvalidationPasses = 0;
        }
    }
}
