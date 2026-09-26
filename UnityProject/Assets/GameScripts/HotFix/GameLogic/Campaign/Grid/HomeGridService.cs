using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>一个端口在世界格网里的位置与朝向（随建筑旋转）。</summary>
    public readonly struct PortPlacement
    {
        public readonly string PortId;
        public readonly GridCell Cell;
        public readonly GridDir Dir;
        public readonly bool IsOutput;

        public PortPlacement(string portId, GridCell cell, GridDir dir, bool isOutput)
        {
            PortId = portId;
            Cell = cell;
            Dir = dir;
            IsOutput = isOutput;
        }
    }

    /// <summary>
    /// FG0-ARCH-04（FGR-ARC-001；FGR-LOG-001、003、012、013；FG-GAP-006）：家园格网的**唯一写入口**与查询入口。
    ///
    /// 真相：建筑的空间真相只有 <see cref="BuildingRecord"/> 的枢轴格（GridX / GridY）与朝向（Rotation）；占地、中心位置、端口
    /// 都由它和 fg.TbBuildingGrid / fg.TbBuildingPort 推导。<see cref="HomeGridMap"/> 的占用层是派生缓存：建筑记录数组被替换
    /// （新建 / 拆除 / 读档都会换数组）时整体重建，旋转时增量改写——任何时刻“占用 == 按记录重建”，自检逐格比对。
    ///
    /// 规划类操作（放置、旋转、拆除标记）只改战役状态，不依赖镜头或可视化对象：玩家在不在家园、画面有没有生成，结果都一样
    /// （FGR-BASE-021）；战略暂停下也可以规划，施工在恢复后才推进（施工由工作单驱动，暂停时不 Tick）。
    ///
    /// 性能：每次查询 O(1)（区块字典 + 数组下标）；放置校验 O(占地格数)；占用重建只在建筑记录变化时发生，O(建筑数 × 占地)，
    /// 不在每帧发生。同类数量用计数表维护，校验“数量上限”O(1)。
    /// </summary>
    public static class HomeGridService
    {
        /// <summary>当前开局布局 / 迁移版本。写入 GridState.LayoutVersion。</summary>
        public const int LayoutVersion = 1;

        /// <summary>家园所在的表面（FGR-ARC-014：家园、各阵营领地、白潮滩头都在同一个星球表面上）。</summary>
        public const string SurfaceId = WorldGenContent.EarthSurfaceId;

        private static CampaignState _state;
        private static HomeGridMap _map;
        private static BuildingRecord[] _recordsRef;
        private static int _contentRevision;
        private static int _worldRevision;
        private static WorldChunkStreamer _streamer;
        // 生成身份缓存（逐字段比较，查询热路径上不拼字符串）。
        private static string _keySourceId;
        private static int _keySeed;
        private static int _keyVersion;
        private static string _keySettings;
        private static int _keyCoreX;
        private static int _keyCoreY;
        private static int _occupancyRebuilds;
        private static readonly Dictionary<string, int> TypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BuildingRecord> ById = new Dictionary<string, BuildingRecord>(StringComparer.Ordinal);
        private static readonly List<GridCell> Scratch = new List<GridCell>(64);
        private static bool _hasCore;
        private static GridCell _coreMin;
        private static GridCell _coreMax;

        /// <summary>占用层整体重建的次数（自检用：证明重建只在记录变化时发生）。</summary>
        public static int OccupancyRebuildCount => _occupancyRebuilds;

        public static string Prefix => HomeValleyLayout.RegionId + ":";

        // ── 绑定与同步 ───────────────────────────────────────────────────────────

        /// <summary>取与该战役同步的格网（必要时迁移旧档、生成新图、重建占用）。</summary>
        public static HomeGridMap MapFor(CampaignState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            if (state.Grid == null || state.Grid.Explored == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            EnsureMigrated(state);
            if (!ReferenceEquals(state, _state) || _map == null || _contentRevision != GridContent.Revision || _worldRevision != WorldGenContent.Revision
                || !SameGenerationKey(state))
            {
                DisposeStreamer();
                _state = state;
                _contentRevision = GridContent.Revision;
                _worldRevision = WorldGenContent.Revision;
                _map = new HomeGridMap(GridContent.TuningInt("grid.chunk_size"), CreateTerrainSource(state), SurfaceId);
                // FG0-ARCH-05（FGR-GEN-060）：读档后被修改过的区块在生成时套上存档里的差异。
                _map.SetSavedDiffs(state.World?.ChunkDiffs);
                _map.SetExplored(state.Grid.Explored);
                RememberGenerationKey(state);
                _recordsRef = null;
                // FG0-ARCH-02：传送带层是派生缓存（真相在传送带内核），新图建好就套回去——否则换图后建筑能压到带上、有带的区块会被回收。
                Logistics.BeltNetworkService.ApplyGridLayer(state, _map);
            }
            if (!ReferenceEquals(_recordsRef, state.BuildingRecords))
            {
                RebuildOccupancy(state);
            }
            return _map;
        }

        /// <summary>丢掉缓存（测试注入表、换战役后调用；下次查询重新生成）。在飞的生成任务一并释放。</summary>
        public static void Invalidate()
        {
            DisposeStreamer();
            _state = null;
            _map = null;
            _recordsRef = null;
            WorldGenService.Invalidate();
        }

        /// <summary>与 <paramref name="state"/> 绑定的格网（本次会话已为它建过图）；没绑定返回 null，不会新建。存档时收集区块差异用。</summary>
        public static HomeGridMap BoundMap(CampaignState state) => state != null && ReferenceEquals(state, _state) ? _map : null;

        /// <summary>家园表面（地球）的区块流式加载器（FGR-GEN-050）。区域每帧用镜头焦点驱动它；换战役 / 换地形来源时自动重建。</summary>
        public static WorldChunkStreamer Streamer(CampaignState state)
        {
            HomeGridMap map = MapFor(state);
            if (_streamer == null || _streamer.IsDisposed || !ReferenceEquals(_streamer.Map, map))
            {
                _streamer?.Dispose();
                _streamer = new WorldChunkStreamer(map);
            }
            return _streamer;
        }

        /// <summary>区域卸载时调用：释放在飞生成任务的原生内存（格网本身保留，回家时继续用）。</summary>
        public static void ShutdownStreaming() => DisposeStreamer();

        private static void DisposeStreamer()
        {
            _streamer?.Dispose();
            _streamer = null;
        }

        private static bool SameGenerationKey(CampaignState state)
        {
            WorldGenState w = state.World;
            GridCell core = CorePivot(state);
            return string.Equals(_keySourceId, state.Grid.TerrainSourceId, StringComparison.Ordinal)
                   && _keySeed == (w?.WorldSeed ?? state.RandomSeed) && _keyVersion == (w?.GeneratorVersion ?? 0)
                   && string.Equals(_keySettings, w?.WorldSettingsId, StringComparison.Ordinal) && _keyCoreX == core.X && _keyCoreY == core.Y;
        }

        private static void RememberGenerationKey(CampaignState state)
        {
            WorldGenState w = state.World;
            GridCell core = CorePivot(state);
            _keySourceId = state.Grid.TerrainSourceId;
            _keySeed = w?.WorldSeed ?? state.RandomSeed;
            _keyVersion = w?.GeneratorVersion ?? 0;
            _keySettings = w?.WorldSettingsId;
            _keyCoreX = core.X;
            _keyCoreY = core.Y;
        }

        private static IGridTerrainSource CreateTerrainSource(CampaignState state)
        {
            // FG0-ARCH-05（FGR-GEN-061）：按存档记录的来源选择——worldgen = 世界生成器（按 WorldGenState.GeneratorVersion 的参数行）；
            // prototype-v1 = FG0-ARCH-05 之前存档的原型地形（生成器版本 0 的旧路径，保留不改）。
            if (string.IsNullOrEmpty(state.Grid.TerrainSourceId))
            {
                state.Grid.TerrainSourceId = state.World != null && state.World.GeneratorVersion >= 1 ? WorldTerrainSource.Id : GridTerrainPrototype.Id;
            }
            if (state.Grid.TerrainSourceId == WorldTerrainSource.Id && state.World != null && state.World.GeneratorVersion >= 1)
            {
                return WorldGenService.CreateSource(state, SurfaceId);
            }
            return new GridTerrainPrototype(state.World != null ? state.World.WorldSeed : state.RandomSeed, CorePivot(state));
        }

        private static void RebuildOccupancy(CampaignState state)
        {
            _occupancyRebuilds++;
            _map.ClearOccupancy();
            TypeCounts.Clear();
            ById.Clear();
            _hasCore = false;
            BuildingRecord[] records = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            foreach (BuildingRecord b in records)
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                ById[b.BuildingId] = b;
                TypeCounts[b.BuildingTypeId] = (TypeCounts.TryGetValue(b.BuildingTypeId, out int n) ? n : 0) + 1;
                if (!GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g))
                {
                    // 已从内容里移除的建筑类型（DEBT-FG0SAVE01-07）：记录保留、不占格（不报错，读档不失败）。
                    continue;
                }
                int rot = GridMath.NormalizeRotation(b.Rotation);
                GridMath.FootprintCells(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, rot, Scratch);
                _map.Occupy(b.BuildingId, Scratch);
                if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && !_hasCore)
                {
                    GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, rot, out _coreMin, out _coreMax);
                    _hasCore = true;
                }
            }
            _recordsRef = state.BuildingRecords;
        }

        // ── 开局布局与迁移（FGR-ARC-001“迁移”）───────────────────────────────────────

        /// <summary>归还核心的枢轴格。开局布局所有坐标都相对它（FG00 B25）。</summary>
        public static GridCell CorePivot(CampaignState state) =>
            state?.Grid != null ? new GridCell(state.Grid.CorePivotX, state.Grid.CorePivotY) : new GridCell(0, 0);

        /// <summary>开局布局里一个锚点的世界格（核心枢轴格 + 偏移）。</summary>
        public static GridCell AnchorCell(CampaignState state, string anchorId)
        {
            StartLayout row = GridContent.Layout(anchorId);
            GridCell core = CorePivot(state);
            return new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY);
        }

        /// <summary>新战役：写入格网域的初始状态（核心枢轴格、开局已探索区域、地形来源、布局版本）。幂等。</summary>
        public static void InitializeNewGrid(CampaignState state)
        {
            CampaignFgStateDomains.EnsureAll(state);
            if (state.Grid.LayoutVersion >= LayoutVersion)
            {
                return;
            }
            // 核心落点来自世界生成的起始区（FG3-GEN-01）；在那之前 = 原点。
            state.Grid.CorePivotX = 0;
            state.Grid.CorePivotY = 0;
            EnsureStartExplored(state);
            // FG0-ARCH-05：新战役（WorldGenState.GeneratorVersion >= 1）的地形来自世界生成器；版本 0 的战役沿用原型来源。
            if (state.World != null && state.World.GeneratorVersion >= 1)
            {
                WorldGenService.PresetFor(state); // 世界设置不存在时回退 default（只可能发生在从没生成过地形的战役上）
                state.Grid.TerrainSourceId = WorldTerrainSource.Id;
            }
            else
            {
                state.Grid.TerrainSourceId = GridTerrainPrototype.Id;
            }
            state.Grid.LayoutVersion = LayoutVersion;
        }

        private static void EnsureStartExplored(CampaignState state)
        {
            if (state.Grid.Explored != null && state.Grid.Explored.Length > 0)
            {
                return;
            }
            GridCell core = CorePivot(state);
            state.Grid.Explored = new[]
            {
                new ExploredAreaRecord { CenterX = core.X, CenterY = core.Y, Radius = GridContent.TuningInt("grid.explored_radius_start") },
            };
        }

        /// <summary>按开局布局表生成开局预置建筑记录（Demo 的固定锚点建筑 → 格网建筑）。只生成、不写回状态。</summary>
        public static List<BuildingRecord> CreateStartBuildings(CampaignState state)
        {
            InitializeNewGrid(state);
            GridCell core = CorePivot(state);
            var list = new List<BuildingRecord>();
            foreach (StartLayout row in GridContent.StartLayout)
            {
                if (row.Kind != "building")
                {
                    continue;
                }
                BuildingGrid g = GridContent.Building(row.TypeId);
                int rot = GridMath.NormalizeRotation(row.Rotation);
                var pivot = new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY);
                HomeValleyLayout.PowerProfile.TryGetValue(row.TypeId, out (float PowerDemand, int PowerPriority) profile);
                list.Add(new BuildingRecord
                {
                    BuildingId = Prefix + row.AnchorId,
                    BuildingTypeId = row.TypeId,
                    RegionId = HomeValleyLayout.RegionId,
                    Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rot),
                    Rotation = rot,
                    GridX = pivot.X,
                    GridY = pivot.Y,
                    Health = row.Health,
                    ConstructionState = row.State == "damaged" ? BuildingConstructionState.Damaged : BuildingConstructionState.Operational,
                    PowerPriority = profile.PowerPriority,
                    // 核心永远有电；其余先占位，进入家园时 HomeValleyPowerGrid.Recompute 按容量 / 优先级重新仲裁。
                    PowerState = row.TypeId == HomeValleyLayout.BuildingTypeCore ? BuildingPowerState.Powered : BuildingPowerState.NotApplicable,
                    Inventory = Array.Empty<CargoEntry>(),
                    QueueIds = Array.Empty<string>(),
                    BlockedReason = null,
                    InvestedScrap = 0,
                });
            }
            return list;
        }

        /// <summary>
        /// 旧档迁移：FG0-ARCH-04 之前的 v2 存档里建筑没有格网字段（GridState.LayoutVersion = 0）。按 Position 四舍五入得到枢轴格、
        /// 朝向规整到 90° 的倍数、按占地重算中心，再补开局已探索区域与地形来源。幂等；新战役不会走到这里（布局版本已写）。
        /// 迁移不删除任何记录：即使（理论上）两座建筑迁移后重叠，也只记 Warning，由自检发现，不让读档失败。
        /// </summary>
        public static void EnsureMigrated(CampaignState state)
        {
            if (state?.Grid == null || state.Grid.LayoutVersion >= LayoutVersion)
            {
                return;
            }
            bool any = false;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                any = true;
                int rot = GridMath.NormalizeRotation(b.Rotation);
                GridCell pivot = GridCell.FromWorld(b.Position);
                b.GridX = pivot.X;
                b.GridY = pivot.Y;
                b.Rotation = rot;
                if (GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g))
                {
                    b.Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rot);
                }
            }
            EnsureStartExplored(state);
            if (string.IsNullOrEmpty(state.Grid.TerrainSourceId))
            {
                // FG0-ARCH-05：FG0-ARCH-04 之前的存档生成器版本都是 0 → 原型地形；版本 >= 1 的战役（还没进过家园）→ 世界生成器。
                state.Grid.TerrainSourceId = state.World != null && state.World.GeneratorVersion >= 1 ? WorldTerrainSource.Id : GridTerrainPrototype.Id;
            }
            state.Grid.LayoutVersion = LayoutVersion;
            if (any)
            {
                Log.Info("[HomeGridService] 旧存档的家园建筑已迁移到格网（按位置取枢轴格，朝向规整到 90°）。");
            }
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────

        /// <summary>归还核心的占地范围（叠加层画核心通道用）；没有核心返回 false。</summary>
        public static bool TryGetCoreBounds(CampaignState state, out GridCell min, out GridCell max)
        {
            MapFor(state);
            min = _coreMin;
            max = _coreMax;
            return _hasCore;
        }

        public static BuildingRecord FindBuilding(CampaignState state, string buildingId)
        {
            MapFor(state);
            return buildingId != null && ById.TryGetValue(buildingId, out BuildingRecord b) ? b : null;
        }

        /// <summary>格子上的建筑；空格返回 null。</summary>
        public static BuildingRecord BuildingAt(CampaignState state, GridCell cell)
        {
            string id = MapFor(state).OccupantAt(cell);
            return id != null && ById.TryGetValue(id, out BuildingRecord b) ? b : null;
        }

        /// <summary>某类建筑的现有数量（含规划中、施工中的）。O(1)。</summary>
        public static int CountOfType(CampaignState state, string typeId)
        {
            MapFor(state);
            return typeId != null && TypeCounts.TryGetValue(typeId, out int n) ? n : 0;
        }

        public static bool IsUnlocked(CampaignState state, BuildingGrid row)
        {
            switch (row.UnlockRule)
            {
                case "always": return true;
                case "beacon": return HomeValleyBeacon.IsUnlocked(state);
                default: return false;
            }
        }

        /// <summary>玩家可以在建造模式里选的建筑（按表顺序）。</summary>
        public static void PlaceableTypes(List<BuildingGrid> into)
        {
            into.Clear();
            foreach (BuildingGrid g in GridContent.Buildings)
            {
                if (g.Placeable == 1)
                {
                    into.Add(g);
                }
            }
        }

        /// <summary>建筑占地格（当前朝向）。</summary>
        public static void FootprintOf(BuildingRecord b, List<GridCell> into)
        {
            BuildingGrid g = GridContent.Building(b.BuildingTypeId);
            GridMath.FootprintCells(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation), into);
        }

        /// <summary>某类建筑放在（枢轴格, 朝向）时的端口世界格与朝向。</summary>
        public static void PortsFor(string typeId, GridCell pivot, int rotation, List<PortPlacement> into)
        {
            into.Clear();
            foreach (BuildingPort p in GridContent.PortsOf(typeId))
            {
                GridMath.TryParseDir(p.Dir, out GridDir dir);
                into.Add(new PortPlacement(p.Id, GridMath.PortCell(pivot, p.LocalX, p.LocalY, rotation), GridMath.RotateDir(dir, rotation), p.Kind == "out"));
            }
        }

        public static void PortsOf(BuildingRecord b, List<PortPlacement> into) =>
            PortsFor(b.BuildingTypeId, new GridCell(b.GridX, b.GridY), GridMath.NormalizeRotation(b.Rotation), into);

        /// <summary>建筑名（当前语言）。</summary>
        public static string DisplayName(string typeId) =>
            FgContentTables.TryGetBuilding(typeId, out GameConfig.fg.Building row) ? Localization.GameText.Get(row.NameKey) : typeId ?? string.Empty;

        // ── 放置校验（FGR-LOG-003、012、013）──────────────────────────────────────────

        /// <summary>
        /// 校验把 <paramref name="typeId"/> 以（枢轴格, 朝向）放下是否合法，逐格给出原因。
        /// <paramref name="asPlayerPlacement"/>：true = 玩家新放置（要求可放置、已解锁、未到上限）；false = 移动 / 旋转已有建筑。
        /// <paramref name="ignoreBuildingId"/>：校验时忽略的建筑（旋转自己时不与自己冲突）。
        /// <paramref name="checkCost"/>：是否校验废料够不够（旋转不花钱）。
        /// </summary>
        public static GridPlacementResult ValidatePlacement(CampaignState state, string typeId, GridCell pivot, int rotation,
            bool asPlayerPlacement = true, string ignoreBuildingId = null, bool checkCost = true, GridPlacementResult into = null)
        {
            GridPlacementResult r = into ?? new GridPlacementResult();
            r.TypeId = typeId;
            r.Pivot = pivot;
            r.Rotation = GridMath.NormalizeRotation(rotation);
            r.Cells.Clear();
            r.CellOk.Clear();
            r.Reasons.Clear();
            r.ScrapCost = 0;
            r.BuildSeconds = 0f;

            HomeGridMap map = MapFor(state);
            if (!GridContent.TryGetBuilding(typeId, out BuildingGrid g))
            {
                r.Add(GridReason.Of(GridBlockReason.UnknownType));
                return r;
            }
            if (HomeValleyLayout.BuildProfile.TryGetValue(typeId, out (int ScrapCost, float Seconds) cost))
            {
                r.ScrapCost = cost.ScrapCost;
                r.BuildSeconds = cost.Seconds;
            }

            if (asPlayerPlacement)
            {
                if (g.Placeable != 1)
                {
                    r.Add(GridReason.Of(GridBlockReason.NotPlaceable));
                }
                else if (!IsUnlocked(state, g))
                {
                    r.Add(new GridReason(GridBlockReason.Locked, "grid.reason.locked", g.UnlockHintKey));
                }
                else if (g.MaxCount > 0 && CountOfType(state, typeId) >= g.MaxCount)
                {
                    r.Add(new GridReason(GridBlockReason.MaxCount, "grid.reason.max_count", g.MaxCount.ToString()));
                }
            }

            int blockLevel = GridContent.TuningInt("grid.pollution_block_level");
            int ring = GridContent.TuningInt("grid.core_reserve_ring");
            bool reserveApplies = typeId != HomeValleyLayout.BuildingTypeCore && _hasCore;
            bool needsTerrain = g.RequiredTerrain != "any";
            byte requiredCode = 0;
            bool requiredFound = false;
            if (needsTerrain)
            {
                GridContent.TryTerrainCode(g.RequiredTerrain, out requiredCode);
            }

            CollectObstacles(state);
            GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, r.Rotation, r.Cells);
            int worldLimit = GridContent.TuningInt("world.coord_limit");
            for (int i = 0; i < r.Cells.Count; i++)
            {
                GridCell c = r.Cells[i];
                if (!WorldCoord.WithinLimit(c, worldLimit))
                {
                    // FG0-ARCH-05（FGR-GEN-051）：超出世界坐标上限——不去生成那里的区块，直接给原因。
                    r.Add(new GridReason(GridBlockReason.WorldLimit, "grid.reason.world_limit", worldLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    r.CellOk.Add(false);
                    continue;
                }
                HomeGridMap.Chunk chunk = map.ChunkAt(c, out int idx);
                bool ok = true;
                if (chunk.Explored[idx] == 0)
                {
                    r.Add(GridReason.Of(GridBlockReason.Fog));
                    ok = false;
                }
                if (reserveApplies && c.X >= _coreMin.X - ring && c.X <= _coreMax.X + ring && c.Y >= _coreMin.Y - ring && c.Y <= _coreMax.Y + ring)
                {
                    r.Add(new GridReason(GridBlockReason.CoreReserve, "grid.reason.core_reserve", ring.ToString()));
                    ok = false;
                }
                byte t = chunk.Terrain[idx];
                GridTerrain terrain = GridContent.TerrainByCode(t);
                if (terrain == null || terrain.Buildable != 1)
                {
                    r.Add(new GridReason(GridBlockReason.Terrain, "grid.reason.terrain", terrain != null ? terrain.NameKey : t.ToString()));
                    ok = false;
                }
                if (needsTerrain && t == requiredCode)
                {
                    requiredFound = true;
                }
                byte p = chunk.Pollution[idx];
                if (p >= blockLevel)
                {
                    r.Add(new GridReason(GridBlockReason.Pollution, "grid.reason.pollution", p.ToString()));
                    ok = false;
                }
                int occ = chunk.Occupancy[idx];
                if (occ != 0)
                {
                    string other = map.OccupantId(occ);
                    if (other != ignoreBuildingId)
                    {
                        string otherType = other != null && ById.TryGetValue(other, out BuildingRecord ob) ? ob.BuildingTypeId : null;
                        r.Add(new GridReason(GridBlockReason.Occupied, "grid.reason.occupied", otherType != null ? DisplayName(otherType) : other ?? string.Empty));
                        ok = false;
                    }
                }
                if (chunk.Belt[idx] != 0)
                {
                    r.Add(GridReason.Of(GridBlockReason.OccupiedBelt));
                    ok = false;
                }
                if (chunk.Pipe[idx] != 0)
                {
                    r.Add(GridReason.Of(GridBlockReason.OccupiedPipe));
                    ok = false;
                }
                for (int k = 0; k < Obstacles.Count; k++)
                {
                    Obstacle o = Obstacles[k];
                    if (Math.Max(Math.Abs(c.X - o.Cell.X), Math.Abs(c.Y - o.Cell.Y)) <= o.Radius)
                    {
                        r.Add(new GridReason(GridBlockReason.Obstacle, "grid.reason.obstacle", o.NameKey));
                        ok = false;
                        break;
                    }
                }
                r.CellOk.Add(ok);
            }

            if (needsTerrain && !requiredFound)
            {
                GridTerrain req = GridContent.TerrainByCode(requiredCode);
                r.Add(new GridReason(GridBlockReason.NeedsTerrain, "grid.reason.needs_terrain", req != null ? req.NameKey : g.RequiredTerrain));
            }

            if (checkCost && r.ScrapCost > 0 && state.Scrap < r.ScrapCost)
            {
                r.Add(new GridReason(GridBlockReason.InsufficientScrap, "grid.reason.insufficient_scrap",
                    r.ScrapCost.ToString(), Mathf.FloorToInt(state.Scrap).ToString()));
            }
            return r;
        }

        /// <summary>
        /// FG0-ARCH-02：一格传送带能不能放。与建筑放置同一套逐格规则：世界坐标上限、迷雾、地形可建、污染、建筑占用、
        /// 已有传送带 / 管线、开局锚点障碍（残骸、靶、出口）。与建筑不同的一条：传送带**可以**放在归还核心外的保留通道上——
        /// 通道留给机器通行，地面传送带不挡路；否则核心的输入端口（在通道里）永远接不上带（FG00 B11 软锁）。
        /// </summary>
        public static GridPlacementResult ValidateBeltCell(CampaignState state, GridCell cell, GridPlacementResult into = null)
        {
            GridPlacementResult r = into ?? new GridPlacementResult();
            r.TypeId = "belt";
            r.Pivot = cell;
            r.Rotation = 0;
            r.Cells.Clear();
            r.CellOk.Clear();
            r.Reasons.Clear();
            r.ScrapCost = 0;
            r.BuildSeconds = 0f;
            r.Cells.Add(cell);
            if (state == null)
            {
                r.Add(GridReason.Of(GridBlockReason.NoRegion));
                r.CellOk.Add(false);
                return r;
            }
            HomeGridMap map = MapFor(state);
            int worldLimit = GridContent.TuningInt("world.coord_limit");
            if (!WorldCoord.WithinLimit(cell, worldLimit))
            {
                r.Add(new GridReason(GridBlockReason.WorldLimit, "grid.reason.world_limit", worldLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                r.CellOk.Add(false);
                return r;
            }
            CollectObstacles(state);
            int blockLevel = GridContent.TuningInt("grid.pollution_block_level");
            HomeGridMap.Chunk chunk = map.ChunkAt(cell, out int idx);
            bool ok = true;
            if (chunk.Explored[idx] == 0)
            {
                r.Add(GridReason.Of(GridBlockReason.Fog));
                ok = false;
            }
            byte t = chunk.Terrain[idx];
            GridTerrain terrain = GridContent.TerrainByCode(t);
            if (terrain == null || terrain.Buildable != 1)
            {
                r.Add(new GridReason(GridBlockReason.Terrain, "grid.reason.terrain", terrain != null ? terrain.NameKey : t.ToString()));
                ok = false;
            }
            byte p = chunk.Pollution[idx];
            if (p >= blockLevel)
            {
                r.Add(new GridReason(GridBlockReason.Pollution, "grid.reason.pollution", p.ToString()));
                ok = false;
            }
            int occ = chunk.Occupancy[idx];
            if (occ != 0)
            {
                string other = map.OccupantId(occ);
                string otherType = other != null && ById.TryGetValue(other, out BuildingRecord ob) ? ob.BuildingTypeId : null;
                r.Add(new GridReason(GridBlockReason.Occupied, "grid.reason.occupied", otherType != null ? DisplayName(otherType) : other ?? string.Empty));
                ok = false;
            }
            if (chunk.Belt[idx] != 0)
            {
                r.Add(GridReason.Of(GridBlockReason.OccupiedBelt));
                ok = false;
            }
            if (chunk.Pipe[idx] != 0)
            {
                r.Add(GridReason.Of(GridBlockReason.OccupiedPipe));
                ok = false;
            }
            for (int k = 0; k < Obstacles.Count; k++)
            {
                Obstacle o = Obstacles[k];
                if (Math.Max(Math.Abs(cell.X - o.Cell.X), Math.Abs(cell.Y - o.Cell.Y)) <= o.Radius)
                {
                    r.Add(new GridReason(GridBlockReason.Obstacle, "grid.reason.obstacle", o.NameKey));
                    ok = false;
                    break;
                }
            }
            r.CellOk.Add(ok);
            return r;
        }

        /// <summary>不属于建筑、但不能被建筑压住的开局锚点：还没拆的残骸（拆解要走到跟前）、低威胁残骸靶（战斗目标）、
        /// 装配站与仓库的出口（新机器从这里出来、卸货走这里）。半径 = 开局布局 clearance 向下取整（切比雪夫距离）。
        /// 机器与地面物会移动，不进格网（机器寻路绕行由 FG0-ARCH-06 处理）。</summary>
        private readonly struct Obstacle
        {
            public readonly GridCell Cell;
            public readonly int Radius;
            public readonly string NameKey;

            public Obstacle(GridCell cell, int radius, string nameKey)
            {
                Cell = cell;
                Radius = radius;
                NameKey = nameKey;
            }
        }

        private static readonly List<Obstacle> Obstacles = new List<Obstacle>(8);

        private static void CollectObstacles(CampaignState state)
        {
            Obstacles.Clear();
            RegionRecord region = null;
            if (state.RegionRecords != null)
            {
                foreach (RegionRecord rr in state.RegionRecords)
                {
                    if (rr != null && rr.RegionId == HomeValleyLayout.RegionId)
                    {
                        region = rr;
                        break;
                    }
                }
            }
            GridCell core = CorePivot(state);
            foreach (StartLayout row in GridContent.StartLayout)
            {
                string nameKey;
                switch (row.Kind)
                {
                    case "wreckage":
                        if (region?.DestroyedNodeIds != null && Array.IndexOf(region.DestroyedNodeIds, row.AnchorId) >= 0)
                        {
                            continue; // 已拆解：空地可以用了。
                        }
                        nameKey = "grid.obstacle.wreckage";
                        break;
                    case "target":
                        nameKey = "grid.obstacle.target";
                        break;
                    case "exit":
                        nameKey = "grid.obstacle.exit";
                        break;
                    default:
                        continue;
                }
                Obstacles.Add(new Obstacle(new GridCell(core.X + row.OffsetX, core.Y + row.OffsetY), Mathf.FloorToInt(row.Clearance), nameKey));
            }
        }

        // ── 规划操作 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 放置（FGR-LOG-003 原型）：校验通过后生成规划中的建筑记录（占格）与一张新建工作单。
        /// <paramref name="machineLogicId"/> = 0：工作单进入待分配池，由空闲机器自动领取（ERD-WRK-002）；
        /// 非 0：玩家点选某台机器下令（Demo 建造位点选路径），直接指派。
        /// 废料在放置时预留（沿用 Demo 的资源账本流程）；取消时全额退回。
        /// </summary>
        public static GridOpResult TryPlace(CampaignState state, string typeId, GridCell pivot, int rotation, int machineLogicId = 0)
        {
            GridPlacementResult check = ValidatePlacement(state, typeId, pivot, rotation);
            if (!check.Ok)
            {
                return GridOpResult.Fail(check.Reasons[0], check);
            }
            string buildingId = NextBuildingId(state, typeId, out bool usedSerial);
            BuildingGrid g = GridContent.Building(typeId);
            Vector2 center = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, check.Rotation);
            HomeValleyWorkOrders.WorkOrderOpResult order = HomeValleyWorkOrders.TryCreateBuildAt(state, typeId, buildingId,
                pivot, check.Rotation, center, machineLogicId);
            if (!order.Success)
            {
                if (usedSerial)
                {
                    state.Grid.NextInstanceSerial--; // 没有真的用掉这个序号（下次仍从同一号开始，存档里不留空洞）。
                }
                GridReason reason = CampaignEconomyLedger.TryParseShortfall(order.FailureReason, out _, out float need, out float have)
                    ? new GridReason(GridBlockReason.InsufficientScrap, "grid.reason.insufficient_scrap", need.ToString("0"), have.ToString("0"))
                    : new GridReason(GridBlockReason.Busy, "grid.reason.busy");
                return GridOpResult.Fail(reason, check);
            }
            MapFor(state); // 记录数组已替换 → 立即重建占用（下一次查询就能看到新建筑）。
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.FirstPlacement);
            return new GridOpResult(GridOpResult.Kind.Placed, buildingId, check);
        }

        private static string NextBuildingId(CampaignState state, string typeId, out bool usedSerial)
        {
            usedSerial = false;
            string canonical = Prefix + typeId;
            MapFor(state);
            if (!ById.ContainsKey(canonical))
            {
                return canonical;
            }
            usedSerial = true;
            string id;
            do
            {
                id = canonical + "#" + state.Grid.NextInstanceSerial++;
            } while (ById.ContainsKey(id));
            return id;
        }

        /// <summary>
        /// 旋转一座已有建筑 90°（顺时针，FGR-LOG-003“放置前后都能旋转”）：绕枢轴格转，新占地必须合法（不与别的建筑、
        /// 地形、迷雾、核心通道冲突），端口随之转向。核心不能旋转；正在维修 / 拆除的建筑要等完成。不花材料、不改设置。
        /// </summary>
        public static GridOpResult TryRotate(CampaignState state, string buildingId)
        {
            BuildingRecord b = FindBuilding(state, buildingId);
            if (b == null)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.CannotRotateCore));
            }
            if (HasActiveOrder(state, b.BuildingId, WorkOrderKind.Repair) || HasActiveOrder(state, b.BuildingId, WorkOrderKind.Salvage))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy));
            }
            int oldRot = GridMath.NormalizeRotation(b.Rotation);
            int newRot = GridMath.NormalizeRotation(oldRot + 90);
            var pivot = new GridCell(b.GridX, b.GridY);
            GridPlacementResult check = ValidatePlacement(state, b.BuildingTypeId, pivot, newRot, asPlayerPlacement: false,
                ignoreBuildingId: b.BuildingId, checkCost: false);
            if (!check.Ok)
            {
                return GridOpResult.Fail(check.Reasons[0], check);
            }
            BuildingGrid g = GridContent.Building(b.BuildingTypeId);
            GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, oldRot, Scratch);
            _map.Release(b.BuildingId, Scratch);
            b.Rotation = newRot;
            b.Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, newRot);
            _map.Occupy(b.BuildingId, check.Cells);
            return new GridOpResult(GridOpResult.Kind.Rotated, b.BuildingId, check);
        }

        /// <summary>这类建筑拆掉后本局能否再造回来：placeable=0（只作开局预置）的建筑不能。核心另有规则（不能拆）。
        /// 格网表没加载（Demo 旧自检的编辑模式）时按“能重建”处理，保持旧行为。</summary>
        public static bool IsRebuildable(string typeId) =>
            !GridContent.TryGetBuilding(typeId, out BuildingGrid g) || g.Placeable == 1;

        /// <summary>
        /// 禁止拆除的建筑（FG00 B11 软锁保底）：本局无法重建（placeable=0，只作开局预置）的全部建筑——
        /// 开局发电机、仓库、信号塔、装配站、解析台、维修台。核心另有规则（<see cref="GridBlockReason.CannotDemolishCore"/>）。
        /// 理由：目标清单每次实时判定（<c>CampaignObjectiveCatalog.AllItemsDone</c>，单项达成不锁存），并按类型找开局建筑
        /// （OBJ-01 发电机、OBJ-02 仓库 + 装配站、OBJ-04 信号塔、OBJ-06 起解析台……）；拆掉任何一座本局造不回来的建筑，
        /// 引用它的目标就永远完不成，战役卡死。拆掉维修台 / 仓库还会让维修、入库永久失效。
        /// 这是原型期限制：FG3-LOG-01 / FG4 把这些建筑改成可放置后，本判定随 placeable=1 自动放开（见 DEBT-FG0ARCH04-08）。
        /// 所有拆除入口（拆除模式、Demo 的“点选机器再点建筑”）都经 <see cref="HomeValleyWorkOrders.TryCreateDemolishBuilding"/> 检查这一条。
        /// </summary>
        public static bool IsDemolishForbidden(string typeId) =>
            typeId != HomeValleyLayout.BuildingTypeCore && !IsRebuildable(typeId);

        /// <summary>拆除前是否需要二次确认（FGR-LOG-007；FG00 B04）：关键建筑（critical=1，目前可拆的只有信标）。
        /// 只对“真的会拆”的情况返回 true（禁止拆除的建筑直接拒绝，不弹确认）。</summary>
        public static bool DemolishNeedsConfirm(CampaignState state, string buildingId)
        {
            BuildingRecord b = FindBuilding(state, buildingId);
            return b != null && b.ConstructionState == BuildingConstructionState.Operational
                   && FindActiveDemolish(state, buildingId) == null
                   && !IsDemolishForbidden(b.BuildingTypeId)
                   && GridContent.TryGetBuilding(b.BuildingTypeId, out BuildingGrid g) && g.Critical == 1;
        }

        /// <summary>
        /// 拆除模式点一座建筑（切换语义，可逆）：
        /// - 已标记拆除 → 取消标记（撤回拆除工作单）；
        /// - 规划中 / 施工中（还没建成）→ 取消规划，预留的废料全额退回，记录与占格一起移除；
        /// - 已建成 → 生成拆除工作单（待分配池，机器上门拆除；返还沿用 Demo 规则：实际投入的一半，FG3-LOG-02 改为全额）；
        /// - 核心不能拆；本局无法重建的开局建筑不能拆（<see cref="IsDemolishForbidden"/>，FG00 B11）；受损的建筑先修复（Demo 规则）。
        /// </summary>
        public static GridOpResult TryToggleDemolish(CampaignState state, string buildingId)
        {
            BuildingRecord b = FindBuilding(state, buildingId);
            if (b == null)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
            }
            if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.CannotDemolishCore));
            }
            WorkOrderRecord demolish = FindActiveDemolish(state, buildingId);
            if (demolish != null)
            {
                HomeValleyWorkOrders.CancelOrder(state, demolish.WorkOrderId, MachinePositionOr(demolish, b.Position));
                return new GridOpResult(GridOpResult.Kind.DemolishUnmarked, buildingId);
            }
            if (b.ConstructionState == BuildingConstructionState.Planned || b.ConstructionState == BuildingConstructionState.MaterialReserved
                || b.ConstructionState == BuildingConstructionState.Building)
            {
                WorkOrderRecord build = FindActive(state, buildingId, WorkOrderKind.Build);
                if (build == null)
                {
                    return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy));
                }
                HomeValleyWorkOrders.CancelOrder(state, build.WorkOrderId, MachinePositionOr(build, b.Position));
                MapFor(state);
                return new GridOpResult(GridOpResult.Kind.PlanCancelled, buildingId);
            }
            if (IsDemolishForbidden(b.BuildingTypeId))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.NotRebuildable));
            }
            if (b.ConstructionState == BuildingConstructionState.Damaged)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.DemolishDamaged));
            }
            if (b.ConstructionState != BuildingConstructionState.Operational || HasActiveOrder(state, buildingId, WorkOrderKind.Repair))
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy));
            }
            HomeValleyWorkOrders.WorkOrderOpResult created = HomeValleyWorkOrders.TryCreateDemolishBuilding(state, buildingId, 0);
            if (!created.Success)
            {
                return GridOpResult.Fail(GridReason.Of(GridBlockReason.Busy));
            }
            return new GridOpResult(GridOpResult.Kind.DemolishMarked, buildingId);
        }

        /// <summary>这座建筑是否已被标记拆除（有未结束的拆除工作单）。</summary>
        public static bool IsMarkedForDemolish(CampaignState state, string buildingId) => FindActiveDemolish(state, buildingId) != null;

        private static Vector2 MachinePositionOr(WorkOrderRecord order, Vector2 fallback) =>
            order.AssignedMachineLogicId > 0 && MachineRegistry.TryGetLivePosition(order.AssignedMachineLogicId, out Vector2 p) ? p : fallback;

        private static WorkOrderRecord FindActiveDemolish(CampaignState state, string buildingId) => FindActive(state, buildingId, WorkOrderKind.Salvage);

        private static bool HasActiveOrder(CampaignState state, string buildingId, WorkOrderKind kind) => FindActive(state, buildingId, kind) != null;

        private static WorkOrderRecord FindActive(CampaignState state, string targetId, WorkOrderKind kind)
        {
            if (state.WorkOrders == null)
            {
                return null;
            }
            foreach (WorkOrderRecord o in state.WorkOrders)
            {
                if (o != null && o.Kind == kind && o.TargetId == targetId
                    && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed)
                {
                    return o;
                }
            }
            return null;
        }

        // ── 自检辅助 ─────────────────────────────────────────────────────────────

        /// <summary>按当前建筑记录从零重建一份占用快照（不动缓存），与缓存逐格比对用。</summary>
        public static Dictionary<GridCell, string> RebuildSnapshotFromRecords(CampaignState state)
        {
            var snap = new Dictionary<GridCell, string>();
            var cells = new List<GridCell>(32);
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || !GridContent.TryGetBuilding(b.BuildingTypeId, out _))
                {
                    continue;
                }
                FootprintOf(b, cells);
                foreach (GridCell c in cells)
                {
                    snap[c] = b.BuildingId;
                }
            }
            return snap;
        }
    }
}
