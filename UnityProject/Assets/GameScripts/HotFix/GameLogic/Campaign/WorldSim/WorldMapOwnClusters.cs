using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG3-GEN-01（FGR-GEN-080“己方建筑群与前哨”）：把星球表面上的己方建筑聚成“建筑群”，给战略地图 / 小地图出图标。
    ///
    /// - 聚合：按 map.own_cluster_cells 格一个聚合格统计建筑（枢轴格），相邻（八邻接）的非空聚合格连成一群；图标在群里建筑的平均位置，标签带座数。
    /// - 归还核心所在的那一群由核心图标代表（不重复出图标）；离核心超过 map.own_outpost_distance 格的群标成“前哨站”
    ///   （FG08 FGR-EXP-017 的初值 300 格；前哨站作为正式实体——改名、仓库、驻守岗位——归 FG8 的前哨站 Story）。
    /// - 只统计星球表面（家园格网区域）的建筑；规划中的虚影也算（玩家已经下了命令，地图上要看得到它在哪），已摧毁的不算。
    /// 开销：聚合 O(建筑数)，只在建筑列表换了（放置 / 拆除都会换新数组）、表版本或核心变了时重做；平时每次查询 O(1) 返回缓存。
    /// FG6-DEF-04 复审：从 UI 层（WorldMapShared.cs）挪到 Campaign 层——突袭导演选前哨站目标也读它，模拟层不再依赖 UI 层；战略地图 / 小地图照旧调用。
    /// </summary>
    public static class WorldMapOwnClusters
    {
        public struct Cluster
        {
            public string Id;
            public double X;
            public double Y;
            public int Count;
            public bool ContainsCore;
            public bool IsOutpost;
        }

        private static readonly List<Cluster> Clusters = new List<Cluster>(8);
        private static readonly Dictionary<long, int> BucketCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, long> BucketSumX = new Dictionary<long, long>();
        private static readonly Dictionary<long, long> BucketSumY = new Dictionary<long, long>();
        private static readonly HashSet<long> Visited = new HashSet<long>();
        private static readonly Stack<long> Frontier = new Stack<long>();
        private static readonly List<long> Keys = new List<long>();
        private static CampaignState _state;
        private static BuildingRecord[] _records;
        private static int _gridRevision = -1;
        private static int _coreX;
        private static int _coreY;

        /// <summary>重算次数（自检用：证明只在建筑列表变化时重算）。</summary>
        public static int RebuildCount { get; private set; }

        public static IReadOnlyList<Cluster> For(CampaignState state)
        {
            if (state == null)
            {
                Clusters.Clear();
                _state = null;
                _records = null;
                return Clusters;
            }
            GridCell core = HomeGridService.CorePivot(state);
            if (ReferenceEquals(state, _state) && ReferenceEquals(state.BuildingRecords, _records) && _gridRevision == GridContent.Revision
                && _coreX == core.X && _coreY == core.Y)
            {
                return Clusters;
            }
            _state = state;
            _records = state.BuildingRecords;
            _gridRevision = GridContent.Revision;
            _coreX = core.X;
            _coreY = core.Y;
            Rebuild(state, core);
            return Clusters;
        }

        private static long Key(long bx, long by) => (bx << 32) ^ (uint)by;

        private static void Rebuild(CampaignState state, GridCell core)
        {
            RebuildCount++;
            Clusters.Clear();
            BucketCount.Clear();
            BucketSumX.Clear();
            BucketSumY.Clear();
            Visited.Clear();
            Keys.Clear();
            int size = Math.Max(4, GridContent.TuningInt("map.own_cluster_cells"));
            double outpostDistance = GridContent.Tuning("map.own_outpost_distance");
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != Campaign.Regions.HomeValleyLayout.RegionId || b.ConstructionState == BuildingConstructionState.Destroyed)
                {
                    continue;
                }
                long k = Key(FloorDiv(b.GridX, size), FloorDiv(b.GridY, size));
                if (!BucketCount.TryGetValue(k, out int n))
                {
                    Keys.Add(k);
                }
                BucketCount[k] = n + 1;
                BucketSumX[k] = (BucketSumX.TryGetValue(k, out long sx) ? sx : 0L) + b.GridX;
                BucketSumY[k] = (BucketSumY.TryGetValue(k, out long sy) ? sy : 0L) + b.GridY;
            }
            long coreKey = Key(FloorDiv(core.X, size), FloorDiv(core.Y, size));
            // 按建筑列表里第一次出现的顺序遍历聚合格（确定、与字典内部顺序无关）；八邻接连通 = 一群。
            foreach (long start in Keys)
            {
                if (!Visited.Add(start))
                {
                    continue;
                }
                int count = 0;
                long sumX = 0, sumY = 0;
                long minKey = start;
                bool hasCore = false;
                Frontier.Clear();
                Frontier.Push(start);
                while (Frontier.Count > 0)
                {
                    long k = Frontier.Pop();
                    count += BucketCount[k];
                    sumX += BucketSumX[k];
                    sumY += BucketSumY[k];
                    minKey = Math.Min(minKey, k);
                    hasCore |= k == coreKey;
                    long bx = k >> 32;
                    long by = (int)(uint)k;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            long nk = Key(bx + dx, by + dy);
                            if ((dx != 0 || dy != 0) && BucketCount.ContainsKey(nk) && Visited.Add(nk))
                            {
                                Frontier.Push(nk);
                            }
                        }
                    }
                }
                double cx = (double)sumX / count;
                double cy = (double)sumY / count;
                double dist = Math.Sqrt((cx - core.X) * (cx - core.X) + (cy - core.Y) * (cy - core.Y));
                Clusters.Add(new Cluster
                {
                    Id = "own:" + minKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    X = cx,
                    Y = cy,
                    Count = count,
                    ContainsCore = hasCore,
                    IsOutpost = !hasCore && dist > outpostDistance,
                });
            }
        }

        private static long FloorDiv(int v, int d) => v >= 0 ? v / d : -((-(long)v + d - 1) / d);
    }
}
