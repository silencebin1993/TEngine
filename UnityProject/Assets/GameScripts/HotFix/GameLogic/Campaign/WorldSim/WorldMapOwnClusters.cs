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
    /// - FG8-OUT-01 审查修复（FG08 FGR-EXP-017“前哨站也可以用电塔接回家园电网”）：电塔只传电、不把两片建筑连成一群——只有电塔的聚合格紧挨着某一群时并进那一群，
    ///   本身不再往外连；彼此相连、不挨着任何实体建筑的电塔聚合格自成“电塔群”（不算前哨站）。否则一排电塔接回家园电网，前哨站就并进家园、名字与前哨劳动全丢。
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
        /// <summary>FG8-OUT-01：聚合格 → 它属于第几群（与 <see cref="Clusters"/> 同时重建；前哨站按位置认领工单 / 通知用，O(1) 查询）。</summary>
        private static readonly Dictionary<long, int> BucketCluster = new Dictionary<long, int>();
        /// <summary>FG8-OUT-01 审查修复：聚合格里电塔以外的建筑座数（0 = 只有电塔，不往外连）。</summary>
        private static readonly Dictionary<long, int> BucketSolid = new Dictionary<long, int>();
        private static readonly List<long> AccMinKey = new List<long>();
        private static readonly List<int> AccCount = new List<int>();
        private static readonly List<long> AccSumX = new List<long>();
        private static readonly List<long> AccSumY = new List<long>();
        private static readonly List<bool> AccCore = new List<bool>();
        private static readonly List<bool> AccPolesOnly = new List<bool>();
        private static int _size = 16;
        private static CampaignState _state;
        private static BuildingRecord[] _records;
        private static int _gridRevision = -1;
        private static int _coreX;
        private static int _coreY;

        /// <summary>重算次数（自检用：证明只在建筑列表变化时重算）。</summary>
        public static int RebuildCount { get; private set; }

        /// <summary>FG8-OUT-01：聚合格边长（格；上一次重建时的 map.own_cluster_cells）。</summary>
        public static int BucketCells => _size;

        /// <summary>FG8-OUT-01：(x, y) 所在聚合格属于第几群（<see cref="For"/> 返回的列表下标）；那一格没有己方建筑返回 -1。O(1)（缓存有效时）。</summary>
        public static int ClusterIndexAt(CampaignState state, int x, int y)
        {
            For(state);
            return BucketCluster.TryGetValue(Key(FloorDiv(x, _size), FloorDiv(y, _size)), out int i) ? i : -1;
        }

        /// <summary>
        /// FG8-OUT-01：(x, y) 所在或紧挨着（八邻接）的聚合格属于第几群——工作地点常在建筑旁边一两格（施工现场、搬运点），落在群边缘外一格时也算这一群。
        /// 自己那一格优先；邻格有多群时取下标最小的（确定）。都没有返回 -1。O(9)。
        /// </summary>
        public static int ClusterIndexNear(CampaignState state, int x, int y)
        {
            For(state);
            long bx = FloorDiv(x, _size);
            long by = FloorDiv(y, _size);
            if (BucketCluster.TryGetValue(Key(bx, by), out int own))
            {
                return own;
            }
            int best = -1;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (BucketCluster.TryGetValue(Key(bx + dx, by + dy), out int i) && (best < 0 || i < best))
                    {
                        best = i;
                    }
                }
            }
            return best;
        }

        public static IReadOnlyList<Cluster> For(CampaignState state)
        {
            if (state == null)
            {
                Clusters.Clear();
                BucketCluster.Clear();
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
            BucketCluster.Clear();
            BucketSolid.Clear();
            AccMinKey.Clear();
            AccCount.Clear();
            AccSumX.Clear();
            AccSumY.Clear();
            AccCore.Clear();
            AccPolesOnly.Clear();
            int size = Math.Max(4, GridContent.TuningInt("map.own_cluster_cells"));
            _size = size;
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
                if (!Campaign.Regions.HomeValleyPowerGrid.IsPoleType(b.BuildingTypeId))
                {
                    BucketSolid[k] = (BucketSolid.TryGetValue(k, out int solid) ? solid : 0) + 1;
                }
            }
            long coreKey = Key(FloorDiv(core.X, size), FloorDiv(core.Y, size));
            // 按建筑列表里第一次出现的顺序遍历聚合格（确定、与字典内部顺序无关）。
            // 1) 有实体建筑（电塔以外）的聚合格八邻接连通 = 一群。
            foreach (long start in Keys)
            {
                if (Solid(start) > 0 && Visited.Add(start))
                {
                    Grow(start, coreKey, solidPass: true);
                }
            }
            // 2) 只有电塔的聚合格：紧挨着某个实体聚合格时并进那一群（邻格有多群取下标最小的，确定），本身不再往外连（FG8-OUT-01 审查修复：电塔只传电）。
            foreach (long k in Keys)
            {
                if (Solid(k) > 0 || Visited.Contains(k))
                {
                    continue;
                }
                long bx = k >> 32;
                long by = (int)(uint)k;
                int best = -1;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        long nk = Key(bx + dx, by + dy);
                        if ((dx != 0 || dy != 0) && Solid(nk) > 0 && BucketCluster.TryGetValue(nk, out int ci) && (best < 0 || ci < best))
                        {
                            best = ci;
                        }
                    }
                }
                if (best >= 0)
                {
                    Visited.Add(k);
                    Accumulate(best, k, coreKey);
                }
            }
            // 3) 剩下只有电塔、彼此相连的聚合格自成“电塔群”（不算前哨站）。
            foreach (long start in Keys)
            {
                if (Solid(start) == 0 && Visited.Add(start))
                {
                    Grow(start, coreKey, solidPass: false);
                }
            }
            for (int i = 0; i < AccCount.Count; i++)
            {
                double cx = (double)AccSumX[i] / AccCount[i];
                double cy = (double)AccSumY[i] / AccCount[i];
                double dist = Math.Sqrt((cx - core.X) * (cx - core.X) + (cy - core.Y) * (cy - core.Y));
                Clusters.Add(new Cluster
                {
                    Id = "own:" + AccMinKey[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    X = cx,
                    Y = cy,
                    Count = AccCount[i],
                    ContainsCore = AccCore[i],
                    IsOutpost = !AccCore[i] && !AccPolesOnly[i] && dist > outpostDistance,
                });
            }
        }

        private static int Solid(long k) => BucketSolid.TryGetValue(k, out int n) ? n : 0;

        /// <summary>从 <paramref name="start"/> 起八邻接扩一群：实体轮只连实体聚合格，电塔轮只连还没归属的电塔聚合格。</summary>
        private static void Grow(long start, long coreKey, bool solidPass)
        {
            int ci = AccCount.Count;
            AccMinKey.Add(start);
            AccCount.Add(0);
            AccSumX.Add(0L);
            AccSumY.Add(0L);
            AccCore.Add(false);
            AccPolesOnly.Add(!solidPass);
            Frontier.Clear();
            Frontier.Push(start);
            while (Frontier.Count > 0)
            {
                long k = Frontier.Pop();
                Accumulate(ci, k, coreKey);
                long bx = k >> 32;
                long by = (int)(uint)k;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        long nk = Key(bx + dx, by + dy);
                        if ((dx != 0 || dy != 0) && BucketCount.ContainsKey(nk) && (Solid(nk) > 0) == solidPass && Visited.Add(nk))
                        {
                            Frontier.Push(nk);
                        }
                    }
                }
            }
        }

        private static void Accumulate(int ci, long k, long coreKey)
        {
            BucketCluster[k] = ci;
            AccCount[ci] += BucketCount[k];
            AccSumX[ci] += BucketSumX[k];
            AccSumY[ci] += BucketSumY[k];
            AccMinKey[ci] = Math.Min(AccMinKey[ci], k);
            AccCore[ci] |= k == coreKey;
        }

        private static long FloorDiv(int v, int d) => v >= 0 ? v / d : -((-(long)v + d - 1) / d);
    }
}
