using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG8-OUT-01（FG08 FGR-EXP-017～019；FG04 FGR-ECO-040 前哨劳动；FG13 FGU-47；FGT-EXP-004）：己方前哨站。<b>唯一写入口</b>（<see cref="GridState.OwnOutposts"/>）。
    ///
    /// - <b>是什么</b>：离核心 map.own_outpost_distance（300）格以外的己方建筑群（<see cref="WorldMapOwnClusters"/> 的聚合，规划中的虚影与废墟也算），自动归为一个前哨站。
    ///   <b>不设第二个核心</b>：前哨站只是“有名字、可以被派驻机器、能看总览”的建筑群；全部被摧毁不会导致战役失败（只有归还核心被毁才失败，FGR-DEF-053）。
    /// - <b>稳定身份</b>：建筑群的聚合 ID 随建筑增删会变，所以每个前哨站存一条记录（编号 op-N、名字、锚点建筑、上次位置）。认领顺序：锚点建筑所在的群 →
    ///   没认领到的群按上次位置就近（≤ 2 个聚合格）认领 → 还没主的群新开一条（发“建立前哨站”通知）。两条记录认领到同一群 = 合并（编号小的留下，前哨劳动的机器改指向它）；
    ///   群没了（建筑都拆掉 / 并进了家园那一群）= 记录删除（前哨劳动的机器停下待命，通知一次）。
    /// - <b>何时推进</b>：<see cref="WorldStep"/> 每 outpost.sync_seconds 游戏秒对账一次（只看步序号，与观察无关，FGR-BASE-021）；记录的增删改与通知只在这里发生。
    ///   查询（<see cref="OutpostIdAt"/> / <see cref="Members"/> 等）只按已有记录重建运行时索引，不改存档、不发通知——所以界面开不开、镜头在哪，结果一样。
    /// - <b>开销</b>：索引只在建筑列表换了（放置 / 拆除都会换新数组）、聚合重算或记录变化时重建，O(建筑数)；平时查询 O(1)。对账每次 O(前哨站的建筑数)。
    /// </summary>
    public static partial class OwnOutpostService
    {
        public const string IdPrefix = "op-";
        public const string NotifyFounded = "outpost_founded";
        public const string NotifyWiped = "outpost_wiped";
        public const string NotifyRemoved = "outpost_removed";

        // ── 调参 ─────────────────────────────────────────────────────────────

        public static float SyncSeconds => Math.Max(0.1f, GameClock.TuningOr("outpost.sync_seconds", 1f));
        /// <summary>至少几座建筑（含虚影）的建筑群才算前哨站。</summary>
        public static int MinBuildings => Math.Max(1, (int)Math.Round(GameClock.TuningOr("outpost.min_buildings", 1f)));
        /// <summary>前哨劳动的机器没活干时，离驻点超过这么多格就走回去。</summary>
        public static float StationRadius => Math.Max(2f, GameClock.TuningOr("outpost.station_radius", 20f));
        /// <summary>施工包着陆后，落点这么多格内的施工现场直接收下材料。</summary>
        public static float PackRadius => Math.Max(2f, GameClock.TuningOr("outpost.pack_radius", 24f));
        /// <summary>认领时按上次位置就近：最多几个聚合格远。</summary>
        public static float RematchBuckets => Math.Max(0.5f, GameClock.TuningOr("outpost.rematch_buckets", 2f));

        // ── 运行时索引（不存档）─────────────────────────────────────────────

        private static CampaignState _state;
        private static BuildingRecord[] _records;
        private static int _clusterRebuild = -1;
        private static int _indexedRevision = -1;
        private static int _gridRevision = -1;
        /// <summary>聚合群下标 → 认领它的记录（没有 = null）。</summary>
        private static readonly List<OwnOutpostRecord> ClusterOwner = new List<OwnOutpostRecord>(8);
        /// <summary>记录编号 → 它认领的聚合群下标。</summary>
        private static readonly Dictionary<string, int> ClusterOf = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>聚合群下标 → 群里的建筑（家园区域、不是已摧毁的）。</summary>
        private static readonly List<List<BuildingRecord>> ClusterMembers = new List<List<BuildingRecord>>(8);
        private static readonly List<BuildingRecord> NoMembers = new List<BuildingRecord>(0);

        /// <summary>记录变了（新增 / 删除 / 改名 / 合并）+1：界面、地图标签据此重建。</summary>
        public static int Revision { get; private set; } = 1;
        /// <summary>索引重建次数（自检：只在建筑列表 / 记录变化时重建）。</summary>
        public static int IndexBuilds { get; private set; }
        public static int SyncCount { get; private set; }
        public static double LastSyncMs { get; private set; }
        public static double LastIndexMs { get; private set; }

        public static void ResetSessionState()
        {
            _state = null;
            _records = null;
            _clusterRebuild = -1;
            _indexedRevision = -1;
            ClusterOwner.Clear();
            ClusterOf.Clear();
            ClusterMembers.Clear();
            IndexBuilds = 0;
            SyncCount = 0;
            Revision++;
        }

        public static OwnOutpostState StateOf(CampaignState s) => s?.Grid?.OwnOutposts;

        public static IReadOnlyList<OwnOutpostRecord> All(CampaignState s) => StateOf(s)?.Outposts ?? Array.Empty<OwnOutpostRecord>();

        /// <summary>读档 / 新档补全：数组补空、空 / 重复编号的记录去掉、名字清洗、编号计数补齐。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Grid == null)
            {
                return;
            }
            OwnOutpostState d = s.Grid.OwnOutposts ??= new OwnOutpostState();
            d.Outposts ??= Array.Empty<OwnOutpostRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keep = new List<OwnOutpostRecord>(d.Outposts.Length);
            int max = 0;
            foreach (OwnOutpostRecord o in d.Outposts)
            {
                if (o == null || string.IsNullOrEmpty(o.OutpostId) || !seen.Add(o.OutpostId))
                {
                    continue;
                }
                o.CustomName = MachineNaming.Sanitize(o.CustomName) ?? string.Empty;
                o.AnchorBuildingId ??= string.Empty;
                o.Count = Math.Max(0, o.Count);
                o.Intact = Math.Max(0, o.Intact);
                o.Serial = Math.Max(1, o.Serial);
                max = Math.Max(max, o.Serial);
                keep.Add(o);
            }
            if (keep.Count != d.Outposts.Length)
            {
                d.Outposts = keep.ToArray();
            }
            d.NextSerial = Math.Max(Math.Max(1, d.NextSerial), max + 1);
            d.Founded = Math.Max(0, d.Founded);
            d.WipedOut = Math.Max(0, d.WipedOut);
            d.PacksDelivered = Math.Max(0, d.PacksDelivered);
            d.PackUnitsPoured = Math.Max(0, d.PackUnitsPoured);
            d.BeltUnitsPoured = Math.Max(0, d.BeltUnitsPoured);
        }

        // ── 查询 ─────────────────────────────────────────────────────────────

        public static bool HasAny(CampaignState s) => All(s).Count > 0;

        public static OwnOutpostRecord Find(CampaignState s, string outpostId)
        {
            if (string.IsNullOrEmpty(outpostId))
            {
                return null;
            }
            foreach (OwnOutpostRecord o in All(s))
            {
                if (o != null && o.OutpostId == outpostId)
                {
                    return o;
                }
            }
            return null;
        }

        /// <summary>玩家看到的名字：起过名用起的名，否则“前哨站 N”。</summary>
        public static string NameOf(OwnOutpostRecord o) =>
            o == null ? GameText.Get("outpost.name.unknown")
            : !string.IsNullOrEmpty(o.CustomName) ? o.CustomName
            : GameText.Format("outpost.name.default", o.Serial);

        public static string NameOf(CampaignState s, string outpostId) => NameOf(Find(s, outpostId));

        /// <summary>离核心多远（格，按上次对账的位置）。</summary>
        public static float DistanceFromCore(CampaignState s, OwnOutpostRecord o)
        {
            if (o == null)
            {
                return 0f;
            }
            GridCell core = HomeGridService.CorePivot(s);
            return Mathf.Sqrt((o.X - core.X) * (o.X - core.X) + (o.Y - core.Y) * (o.Y - core.Y));
        }

        /// <summary>(x, y) 属于哪个前哨站（在群里或紧挨着群的聚合格；不属于任何前哨站返回 null）。O(1)（索引有效时）。</summary>
        public static string OutpostIdAt(CampaignState s, Vector2 p)
        {
            if (s == null || All(s).Count == 0)
            {
                return null;
            }
            EnsureIndex(s);
            int i = WorldMapOwnClusters.ClusterIndexNear(s, Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
            return i >= 0 && i < ClusterOwner.Count ? ClusterOwner[i]?.OutpostId : null;
        }

        /// <summary>认领第 <paramref name="clusterIndex"/> 群（<see cref="WorldMapOwnClusters.For"/> 的下标）的前哨站；没有返回 null。地图标签用。</summary>
        public static OwnOutpostRecord OwnerOfCluster(CampaignState s, int clusterIndex)
        {
            if (s == null || All(s).Count == 0)
            {
                return null;
            }
            EnsureIndex(s);
            return clusterIndex >= 0 && clusterIndex < ClusterOwner.Count ? ClusterOwner[clusterIndex] : null;
        }

        /// <summary>一个前哨站现在的建筑（家园区域、不含已摧毁的；虚影与废墟都在）。按建筑数组顺序。</summary>
        public static IReadOnlyList<BuildingRecord> Members(CampaignState s, string outpostId)
        {
            if (s == null || string.IsNullOrEmpty(outpostId))
            {
                return NoMembers;
            }
            EnsureIndex(s);
            return ClusterOf.TryGetValue(outpostId, out int i) && i >= 0 && i < ClusterMembers.Count ? ClusterMembers[i] : NoMembers;
        }

        /// <summary>离 <paramref name="p"/> 最近的前哨站（按上次位置；同距离按编号）。没有返回 null。</summary>
        public static OwnOutpostRecord Nearest(CampaignState s, Vector2 p)
        {
            OwnOutpostRecord best = null;
            float bestD = float.MaxValue;
            foreach (OwnOutpostRecord o in All(s))
            {
                if (o == null)
                {
                    continue;
                }
                float d = (new Vector2(o.X, o.Y) - p).sqrMagnitude;
                if (d < bestD - 1e-4f || (Mathf.Abs(d - bestD) <= 1e-4f && best != null && o.Serial < best.Serial))
                {
                    best = o;
                    bestD = d;
                }
            }
            return best;
        }

        /// <summary>建筑是不是还立着（运转 / 停用 / 受损但没被打穿）。</summary>
        public static bool IsStanding(BuildingRecord b) =>
            b != null && (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled
                          || (b.ConstructionState == BuildingConstructionState.Damaged && b.Health > 0f));

        /// <summary>建筑是不是废墟（被摧毁、等维修 / 重建）。</summary>
        public static bool IsRuin(BuildingRecord b) => b != null && b.ConstructionState == BuildingConstructionState.Damaged && b.Health <= 0f;

        /// <summary>前哨站全毁：有废墟、一座立着的都没有（只有虚影的新前哨站不算）。</summary>
        public static bool IsWipedOut(CampaignState s, OwnOutpostRecord o)
        {
            if (o == null)
            {
                return false;
            }
            bool ruin = false;
            foreach (BuildingRecord b in Members(s, o.OutpostId))
            {
                if (IsStanding(b))
                {
                    return false;
                }
                ruin |= IsRuin(b);
            }
            return ruin;
        }

        /// <summary>前哨劳动的机器没活干时去的驻点：立着的建筑里离群中心最近的一座（没有立着的就任一座；同距离按 ID）。</summary>
        public static BuildingRecord StationBuilding(CampaignState s, OwnOutpostRecord o)
        {
            if (o == null)
            {
                return null;
            }
            BuildingRecord best = null;
            bool bestStanding = false;
            float bestD = float.MaxValue;
            var c = new Vector2(o.X, o.Y);
            foreach (BuildingRecord b in Members(s, o.OutpostId))
            {
                bool standing = IsStanding(b);
                float d = (b.Position - c).sqrMagnitude;
                bool better = best == null || (standing && !bestStanding) || (standing == bestStanding && (d < bestD - 1e-4f
                              || (Mathf.Abs(d - bestD) <= 1e-4f && string.CompareOrdinal(b.BuildingId, best.BuildingId) < 0)));
                if (better)
                {
                    best = b;
                    bestStanding = standing;
                    bestD = d;
                }
            }
            return best;
        }

        /// <summary>常驻这个前哨站、活着的前哨劳动机器数。O(机器数)。</summary>
        public static int LaborCount(CampaignState s, string outpostId)
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.Role == MachineRole.OutpostLabor && m.OutpostId == outpostId && MachineRoster.IsHome(m))
                {
                    n++;
                }
            }
            return n;
        }

        // ── 改名 ─────────────────────────────────────────────────────────────

        /// <summary>改名（总览面板；规则同机器改名：去掉首尾空白，1～16 个字，不能有 &lt; &gt; 与控制字符）。失败时名字不变，<paramref name="message"/> 写原因。</summary>
        public static bool TryRename(CampaignState s, string outpostId, string raw, out string message)
        {
            OwnOutpostRecord o = Find(s, outpostId);
            if (o == null)
            {
                message = GameText.Get("outpost.err.unknown");
                return false;
            }
            MachineNaming.NameError err = MachineNaming.Validate(raw, out string clean);
            if (err != MachineNaming.NameError.None)
            {
                message = MachineNaming.ErrorText(err, clean);
                return false;
            }
            if (string.Equals(o.CustomName, clean, StringComparison.Ordinal))
            {
                message = GameText.Get("roster.name.same");
                return false;
            }
            string before = NameOf(o);
            o.CustomName = clean;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.OutpostFirstRenamed);
            message = GameText.Format("outpost.rename.ok", before, clean);
            return true;
        }

        public static bool TryResetName(CampaignState s, string outpostId, out string message)
        {
            OwnOutpostRecord o = Find(s, outpostId);
            if (o == null)
            {
                message = GameText.Get("outpost.err.unknown");
                return false;
            }
            if (string.IsNullOrEmpty(o.CustomName))
            {
                message = GameText.Get("roster.name.same");
                return false;
            }
            o.CustomName = string.Empty;
            Revision++;
            message = GameText.Format("outpost.rename.reset", NameOf(o));
            return true;
        }

        // ── 索引（只读：不改存档、不发通知）──────────────────────────────────

        private static void EnsureIndex(CampaignState s)
        {
            IReadOnlyList<WorldMapOwnClusters.Cluster> clusters = WorldMapOwnClusters.For(s);
            if (ReferenceEquals(s, _state) && ReferenceEquals(s.BuildingRecords, _records) && _clusterRebuild == WorldMapOwnClusters.RebuildCount
                && _indexedRevision == Revision && _gridRevision == GridContent.Revision)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _state = s;
            _records = s.BuildingRecords;
            _clusterRebuild = WorldMapOwnClusters.RebuildCount;
            _indexedRevision = Revision;
            _gridRevision = GridContent.Revision;
            IndexBuilds++;
            ClusterOwner.Clear();
            ClusterOf.Clear();
            for (int i = 0; i < ClusterMembers.Count; i++)
            {
                ClusterMembers[i].Clear();
            }
            while (ClusterMembers.Count < clusters.Count)
            {
                ClusterMembers.Add(new List<BuildingRecord>(8));
            }
            for (int i = 0; i < clusters.Count; i++)
            {
                ClusterOwner.Add(null);
            }
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || b.ConstructionState == BuildingConstructionState.Destroyed)
                {
                    continue;
                }
                int ci = WorldMapOwnClusters.ClusterIndexAt(s, b.GridX, b.GridY);
                if (ci >= 0 && ci < clusters.Count)
                {
                    ClusterMembers[ci].Add(b);
                }
            }
            // 1) 锚点建筑所在的群（编号小的先认领：合并时编号小的留下）。
            OwnOutpostRecord[] recs = SortedBySerial(s);
            foreach (OwnOutpostRecord o in recs)
            {
                BuildingRecord anchor = string.IsNullOrEmpty(o.AnchorBuildingId) ? null : HomeGridService.FindBuilding(s, o.AnchorBuildingId);
                if (anchor == null || anchor.ConstructionState == BuildingConstructionState.Destroyed || anchor.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                int ci = WorldMapOwnClusters.ClusterIndexAt(s, anchor.GridX, anchor.GridY);
                if (ci >= 0 && ci < clusters.Count && clusters[ci].IsOutpost && ClusterOwner[ci] == null)
                {
                    ClusterOwner[ci] = o;
                    ClusterOf[o.OutpostId] = ci;
                }
            }
            // 2) 锚点没了 / 锚点所在的群已被别的记录认领：按上次位置就近认领还没主的前哨群。
            float maxD = RematchBuckets * WorldMapOwnClusters.BucketCells;
            foreach (OwnOutpostRecord o in recs)
            {
                if (ClusterOf.ContainsKey(o.OutpostId))
                {
                    continue;
                }
                int best = -1;
                double bestD = (double)maxD * maxD;
                for (int i = 0; i < clusters.Count; i++)
                {
                    if (!clusters[i].IsOutpost || ClusterOwner[i] != null)
                    {
                        continue;
                    }
                    double dx = clusters[i].X - o.X;
                    double dy = clusters[i].Y - o.Y;
                    double d = dx * dx + dy * dy;
                    if (d <= bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                if (best >= 0)
                {
                    ClusterOwner[best] = o;
                    ClusterOf[o.OutpostId] = best;
                }
            }
            LastIndexMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static OwnOutpostRecord[] SortedBySerial(CampaignState s)
        {
            var list = new List<OwnOutpostRecord>(All(s));
            list.RemoveAll(o => o == null);
            list.Sort((a, b) => a.Serial != b.Serial ? a.Serial.CompareTo(b.Serial) : string.CompareOrdinal(a.OutpostId, b.OutpostId));
            return list.ToArray();
        }

        // ── 对账（世界步；唯一改记录的地方）───────────────────────────────────

        /// <summary>每 outpost.sync_seconds 游戏秒对账一次（家园载入时；只看步序号，与观察无关）。</summary>
        public static void WorldStep(CampaignState state, long ticks, int worldHz)
        {
            if (state?.Grid == null)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(SyncSeconds * Math.Max(1, worldHz)));
            if (ticks % every != 0)
            {
                return;
            }
            Sync(state);
        }

        /// <summary>
        /// 对账：认领 / 新开 / 合并 / 删除记录，更新位置与座数，全毁通知（每次全毁一次）。旧档第一次对账静默补登（不发“建立”通知）。
        /// 返回这次有没有改记录。O(建筑数)（只在索引需要重建时）+ O(前哨站的建筑数)。
        /// </summary>
        public static bool Sync(CampaignState s)
        {
            if (s?.Grid == null)
            {
                return false;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureState(s);
            OwnOutpostState d = s.Grid.OwnOutposts;
            SyncCount++;
            EnsureIndex(s);
            IReadOnlyList<WorldMapOwnClusters.Cluster> clusters = WorldMapOwnClusters.For(s);
            bool changed = false;
            bool announce = d.Seeded;
            int minB = MinBuildings;
            var founded = new List<OwnOutpostRecord>(2);
            // 1) 记录：认领到的群还是前哨群且座数够 → 留下（更新锚点、位置）；否则删除。
            var keep = new List<OwnOutpostRecord>(d.Outposts.Length);
            var removed = new List<OwnOutpostRecord>(1);
            foreach (OwnOutpostRecord o in d.Outposts)
            {
                if (o == null)
                {
                    continue;
                }
                if (ClusterOf.TryGetValue(o.OutpostId, out int ci) && ci < clusters.Count && clusters[ci].IsOutpost && clusters[ci].Count >= minB)
                {
                    keep.Add(o);
                    continue;
                }
                removed.Add(o);
            }
            // 2) 还没主的前哨群：新开记录。
            for (int i = 0; i < clusters.Count; i++)
            {
                if (!clusters[i].IsOutpost || clusters[i].Count < minB || ClusterOwner[i] != null)
                {
                    continue;
                }
                var o = new OwnOutpostRecord
                {
                    Serial = d.NextSerial++,
                    FoundedTick = GameClock.Ticks,
                };
                o.OutpostId = IdPrefix + o.Serial.ToString(CultureInfo.InvariantCulture);
                keep.Add(o);
                founded.Add(o);
                ClusterOwner[i] = o;
                ClusterOf[o.OutpostId] = i;
                changed = true;
            }
            if (removed.Count > 0 || founded.Count > 0)
            {
                d.Outposts = keep.ToArray();
                changed = true;
            }
            // 3) 删除的记录：前哨劳动的机器改指向并进去的那个前哨站（按它上次位置找当前所在的群），找不到就停下待命并通知。
            foreach (OwnOutpostRecord gone in removed)
            {
                int ci = WorldMapOwnClusters.ClusterIndexNear(s, Mathf.RoundToInt(gone.X), Mathf.RoundToInt(gone.Y));
                OwnOutpostRecord heir = ci >= 0 && ci < ClusterOwner.Count && clusters[ci].IsOutpost ? ClusterOwner[ci] : null;
                int moved = 0;
                int stranded = 0;
                foreach (MachineRecord m in MachineRegistry.AllRecords)
                {
                    if (m == null || m.OutpostId != gone.OutpostId)
                    {
                        continue;
                    }
                    if (heir != null && heir != gone)
                    {
                        m.OutpostId = heir.OutpostId;
                        moved++;
                    }
                    else if (m.IsAlive && m.Role == MachineRole.OutpostLabor)
                    {
                        stranded++;
                    }
                }
                if (moved > 0)
                {
                    MachineRoster.Touch();
                }
                if (announce)
                {
                    string detail = heir != null && heir != gone
                        ? GameText.Format("outpost.notify.merged", NameOf(gone), NameOf(heir), moved)
                        : GameText.Format("outpost.notify.removed", NameOf(gone), stranded);
                    NotificationCenter.Post(NotifyRemoved, detail, new Vector3(gone.X, 0f, gone.Y));
                }
            }
            // 4) 位置、座数、锚点、全毁通知。
            foreach (OwnOutpostRecord o in d.Outposts)
            {
                if (!ClusterOf.TryGetValue(o.OutpostId, out int ci) || ci >= clusters.Count)
                {
                    continue;
                }
                WorldMapOwnClusters.Cluster c = clusters[ci];
                o.X = (float)c.X;
                o.Y = (float)c.Y;
                List<BuildingRecord> members = ClusterMembers[ci];
                int intact = 0;
                bool ruin = false;
                string minId = null;
                bool anchorHere = false;
                foreach (BuildingRecord b in members)
                {
                    if (IsStanding(b))
                    {
                        intact++;
                    }
                    ruin |= IsRuin(b);
                    if (minId == null || string.CompareOrdinal(b.BuildingId, minId) < 0)
                    {
                        minId = b.BuildingId;
                    }
                    anchorHere |= b.BuildingId == o.AnchorBuildingId;
                }
                o.Count = members.Count;
                o.Intact = intact;
                if (!anchorHere && minId != null)
                {
                    o.AnchorBuildingId = minId; // 锚点被拆 / 新记录：换成群里 ID 最小的一座（确定）
                    changed = true;
                }
                bool wiped = ruin && intact == 0;
                if (wiped && !o.WipedNotified)
                {
                    o.WipedNotified = true;
                    d.WipedOut++;
                    // FGR-EXP-019：前哨站全部被摧毁不导致战役失败；虚影 / 废墟保留，自动重建规则照常生效。
                    NotificationCenter.Post(NotifyWiped, GameText.Format("outpost.notify.wiped", NameOf(o), members.Count), new Vector3(o.X, 0f, o.Y));
                    GuidanceHooks.Raise(GuidanceHooks.OutpostFirstWiped);
                }
                else if (!wiped && o.WipedNotified && intact > 0)
                {
                    o.WipedNotified = false;
                }
            }
            foreach (OwnOutpostRecord o in founded)
            {
                d.Founded++;
                if (announce)
                {
                    float dist = DistanceFromCore(s, o);
                    NotificationCenter.Post(NotifyFounded, GameText.Format("outpost.notify.founded", NameOf(o), Mathf.RoundToInt(dist), o.Count, InputDisplay.ForAction(GameActionId.OpenOutposts)),
                        new Vector3(o.X, 0f, o.Y));
                    GuidanceHooks.Raise(GuidanceHooks.OutpostFirstFounded);
                }
            }
            d.Seeded = true;
            if (changed)
            {
                Revision++;
                MachineRoster.Touch();
            }
            LastSyncMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return changed;
        }

        // ── 施工包（投送舱送到前哨施工现场）──────────────────────────────────

        /// <summary>
        /// FGR-EXP-018“投送舱送施工包”：施工包着陆——落点 outpost.pack_radius 格内还缺这种材料的施工现场（建筑虚影；近的先、同距离按 ID）直接收下材料，
        /// 返回收下的数量（其余由调用方落在地上）。材料从投送舱进现场，与机器运到现场同一笔“现场材料”（取消虚影时全额退回仓库）。O(建筑数)，只在着陆时调用。
        /// </summary>
        public static int PourPack(CampaignState s, Vector2 at, string itemId, int amount)
        {
            if (s == null || amount <= 0 || !Economy.ItemCatalog.TryGet(itemId, out Economy.ItemDef item))
            {
                return 0;
            }
            string resource = item.Id == Economy.ItemCatalog.ScrapId ? CampaignEconomyLedger.ResourceScrap : item.ResourceType;
            if (string.IsNullOrEmpty(resource))
            {
                return 0;
            }
            float r2 = PackRadius * PackRadius;
            var sites = new List<(BuildingRecord B, float D)>(4);
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || !HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                float d = (b.Position - at).sqrMagnitude;
                if (d <= r2 && HomeValleyConstruction.SiteNeed(b, resource) > 0)
                {
                    sites.Add((b, d));
                }
            }
            sites.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : string.CompareOrdinal(a.B.BuildingId, b.B.BuildingId));
            int left = amount;
            foreach ((BuildingRecord b, float _) in sites)
            {
                if (left <= 0)
                {
                    break;
                }
                left -= HomeValleyConstruction.DeliverToSite(s, b, resource, left);
            }
            int poured = amount - left;
            if (poured > 0)
            {
                OwnOutpostState d = StateOf(s);
                if (d != null)
                {
                    d.PackUnitsPoured += poured;
                }
                GuidanceHooks.Raise(GuidanceHooks.OutpostFirstPack);
            }
            return poured;
        }

        /// <summary>传送带送进施工现场输入口的材料计数（总览 / 统计读）。</summary>
        internal static void CountBeltPoured(CampaignState s, int n)
        {
            OwnOutpostState d = StateOf(s);
            if (d != null && n > 0)
            {
                d.BeltUnitsPoured += n;
            }
        }
    }
}
