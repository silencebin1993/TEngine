using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>一次覆盖采样：这个位置在不在与归还核心连通的信号覆盖里、离边缘还有多远、是哪个覆盖源罩着它。</summary>
    public readonly struct SignalCoverageSample
    {
        /// <summary>true = 在覆盖里（含“这个地点不受覆盖限制”）。</summary>
        public readonly bool Covered;
        /// <summary>这个地点有没有覆盖边界（false = 远征地点暂按“与核心连通”处理，DEBT-FG1SIG04-01）。</summary>
        public readonly bool Bounded;
        /// <summary>离覆盖边缘的格数：正 = 在里面还剩多少，负 = 已经出去多远。没有边界时为 +∞。</summary>
        public readonly float MarginCells;
        /// <summary>罩着它（或离它最近）的覆盖源的圆心与半径（地图预警画的就是这个圆）。</summary>
        public readonly Vector2 SourceCenter;
        public readonly float SourceRadius;
        public readonly SignalCoverageSourceKind SourceKind;

        public SignalCoverageSample(bool covered, bool bounded, float margin, Vector2 center, float radius, SignalCoverageSourceKind kind)
        {
            Covered = covered;
            Bounded = bounded;
            MarginCells = margin;
            SourceCenter = center;
            SourceRadius = radius;
            SourceKind = kind;
        }

        public static SignalCoverageSample Unbounded => new SignalCoverageSample(true, false, float.PositiveInfinity, Vector2.zero, 0f, SignalCoverageSourceKind.None);
    }

    public enum SignalCoverageSourceKind : byte
    {
        None = 0,
        /// <summary>归还核心（FGR-SIG-050 半径 150 格）。</summary>
        Core,
        /// <summary>信号塔（运转且有电；T1 300 格，T2 500 格）。</summary>
        Tower,
    }

    /// <summary>
    /// FG1-SIG-04（FGR-SIG-040“超出覆盖范围 → 断链”，卡片“地图上覆盖边缘的预警”）：信号覆盖的最小实现。
    ///
    /// 覆盖源（FGR-SIG-050）：归还核心（signal.coverage.core_radius，初值 150 格）+ 运转且有电的信号塔（signal.coverage.tower_radius，T1 初值 300 格）。
    /// 一个覆盖源只有与核心连通才提供覆盖——两个覆盖源“在彼此范围内”（距离 ≤ 两者半径中较小的一个）就算相连（广度优先，O(覆盖源²)，覆盖源只有几个）。
    /// 信号中继塔、机器上的中继模块、覆盖网络叠加层、远距离跳转属于 FG1-SIG-07：它们只需要往 <see cref="CollectSources"/> 里追加覆盖源。
    ///
    /// 地点：归还谷地（家园所在的星球表面）按上面的覆盖源算；远征地点（破碎都市、铸造前哨外围）目前是独立地点，暂按“与核心连通”处理
    /// ——否则 Demo 的远征内容在中继落地前就接入不了（DEBT-FG1SIG04-01 → FG1-SIG-07）。
    ///
    /// 开销：建筑表只在换了（增删建筑都会换成新数组）或 <see cref="Invalidate"/> 时全量扫一遍，记下核心与信号塔的下标（O(建筑数)，很少发生）；
    /// 平时按游戏时间每 signal.coverage.refresh_seconds 只重读这些下标上的记录（修好 / 通电 / 断电，O(覆盖源候选数)），与建筑总数无关；每次采样 O(覆盖源数)。
    /// 只有被接入的那台（每帧 1 次）与安全模式里的机器（每 0.5 游戏秒）会被采样——与敌人数、弹体数无关。
    /// </summary>
    public static class SignalCoverageService
    {
        private struct Source
        {
            public Vector2 Center;
            public float Radius;
            public SignalCoverageSourceKind Kind;
            public bool Connected;
        }

        private static readonly List<Source> Sources = new List<Source>(8);
        private static readonly Queue<int> Bfs = new Queue<int>(8);
        private static long _builtAtTick = long.MinValue;
        private static CampaignState _builtFor;
        private static int _builtBuildingCount = -1;
        // 建筑表索引：哪张表（数组引用）、核心在哪个下标、信号塔在哪些下标。
        private static BuildingRecord[] _indexedBuildings;
        private static int _coreIndex = -1;
        private static readonly List<int> TowerIndices = new List<int>(8);
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>自检注入：按地点 + 位置直接给出采样（null = 走正式覆盖源）。</summary>
        public static Func<string, Vector2, SignalCoverageSample?> OverrideForTests;

        /// <summary>覆盖源重建次数（自检核对“不是每帧重建”）。</summary>
        public static int RebuildCount { get; private set; }

        /// <summary>建筑表全量扫描次数（自检核对“平时不按建筑总数扫”）。</summary>
        public static int IndexCount { get; private set; }

        public static float CoreRadius => Tuning("signal.coverage.core_radius", 150f);
        public static float TowerRadius => Tuning("signal.coverage.tower_radius", 300f);
        public static float EdgeWarnCells => Tuning("signal.coverage.edge_warn_cells", 15f);
        private static float RefreshSeconds => Tuning("signal.coverage.refresh_seconds", 0.5f);

        /// <summary>这个地点有没有覆盖边界（只有归还谷地有；远征地点见类注释）。</summary>
        public static bool IsBoundedSite(string regionId) => regionId == HomeValleyLayout.RegionId;

        /// <summary>某台机器现在的覆盖采样（位置取实时位置；取不到时用记录里的位置）。</summary>
        public static SignalCoverageSample SampleMachine(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return SignalCoverageSample.Unbounded;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : rec.WorldPosition;
            return Sample(rec.RegionId, pos);
        }

        /// <summary>某个地点某个位置的覆盖采样。</summary>
        public static SignalCoverageSample Sample(string regionId, Vector2 position)
        {
            if (OverrideForTests != null)
            {
                SignalCoverageSample? o = OverrideForTests(regionId, position);
                if (o.HasValue)
                {
                    return o.Value;
                }
            }
            if (!IsBoundedSite(regionId))
            {
                return SignalCoverageSample.Unbounded;
            }
            EnsureSources(CampaignSession.Current);
            bool any = false;
            float bestMargin = float.NegativeInfinity;
            Source best = default;
            for (int i = 0; i < Sources.Count; i++)
            {
                Source src = Sources[i];
                if (!src.Connected)
                {
                    continue;
                }
                float margin = src.Radius - Vector2.Distance(position, src.Center);
                if (!any || margin > bestMargin)
                {
                    any = true;
                    bestMargin = margin;
                    best = src;
                }
            }
            if (!any)
            {
                return SignalCoverageSample.Unbounded; // 核心永远是第 0 个且连通，走不到这里；防御：不让家园整片变成“覆盖外”。
            }
            return new SignalCoverageSample(bestMargin >= 0f, true, bestMargin, best.Center, best.Radius, best.Kind);
        }

        /// <summary>建筑状态变了（信号塔修好 / 断电 / 被拆）时由调用方请求立即重建；否则按游戏时间节流重建。</summary>
        public static void Invalidate()
        {
            _builtAtTick = long.MinValue;
            _indexedBuildings = null;
        }

        /// <summary>当前覆盖源（自检、地图预警用）：圆心、半径、种类、是否与核心连通。</summary>
        public static int SourceCount
        {
            get
            {
                EnsureSources(CampaignSession.Current);
                return Sources.Count;
            }
        }

        public static bool TryGetSource(int index, out Vector2 center, out float radius, out SignalCoverageSourceKind kind, out bool connected)
        {
            EnsureSources(CampaignSession.Current);
            if (index < 0 || index >= Sources.Count)
            {
                center = default;
                radius = 0f;
                kind = SignalCoverageSourceKind.None;
                connected = false;
                return false;
            }
            Source s = Sources[index];
            center = s.Center;
            radius = s.Radius;
            kind = s.Kind;
            connected = s.Connected;
            return true;
        }

        private static void EnsureSources(CampaignState state)
        {
            BuildingRecord[] buildings = state?.BuildingRecords;
            int buildingCount = buildings?.Length ?? -1;
            // 换战役、建筑表换了（增删建筑都会换成新数组）或数量变了：重新索引（全量扫一遍，很少发生）。
            bool reindex = !ReferenceEquals(state, _builtFor) || !ReferenceEquals(buildings, _indexedBuildings) || buildingCount != _builtBuildingCount;
            long refreshTicks = Math.Max(1L, (long)Math.Round(RefreshSeconds * GameClock.StepHz));
            // 按游戏时间分桶（与帧率 / 是否被观察无关）：同一个桶里只建一次。
            long bucket = GameClock.Ticks / refreshTicks;
            if (!reindex && bucket == _builtAtTick)
            {
                return;
            }
            if (reindex)
            {
                _builtFor = state;
                _builtBuildingCount = buildingCount;
                _indexedBuildings = buildings;
                IndexBuildings(buildings);
            }
            _builtAtTick = bucket;
            RebuildCount++;
            Sources.Clear();
            CollectSources(state, buildings, Sources);
            Connect();
        }

        private static bool IsHomeBuilding(BuildingRecord b) => b.RegionId == null || b.RegionId == HomeValleyLayout.RegionId;

        /// <summary>记下核心与信号塔在建筑表里的下标（O(建筑数)，只在建筑表换了时做）。</summary>
        private static void IndexBuildings(BuildingRecord[] buildings)
        {
            IndexCount++;
            _coreIndex = -1;
            TowerIndices.Clear();
            if (buildings == null)
            {
                return;
            }
            for (int i = 0; i < buildings.Length; i++)
            {
                BuildingRecord b = buildings[i];
                if (b == null || !IsHomeBuilding(b))
                {
                    continue;
                }
                if (_coreIndex < 0 && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    _coreIndex = i;
                }
                else if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
                {
                    TowerIndices.Add(i);
                }
            }
        }

        /// <summary>收集覆盖源（下标 0 永远是归还核心）：只读索引记下的那几条记录的当前状态。FG1-SIG-07 在这里追加信号中继塔与中继模块。</summary>
        private static void CollectSources(CampaignState state, BuildingRecord[] buildings, List<Source> into)
        {
            BuildingRecord coreRecord = buildings != null && _coreIndex >= 0 && _coreIndex < buildings.Length ? buildings[_coreIndex] : null;
            // 核心建筑记录（格网唯一计算的几何中心）优先；没有记录时（只建了战役的测试）按开局布局的核心锚点。
            Vector2 corePos = coreRecord != null ? coreRecord.Position : (state != null ? HomeValleyLayout.Core.Position : Vector2.zero);
            into.Add(new Source { Center = corePos, Radius = CoreRadius, Kind = SignalCoverageSourceKind.Core });
            if (buildings == null)
            {
                return;
            }
            float towerRadius = TowerRadius;
            for (int k = 0; k < TowerIndices.Count; k++)
            {
                int i = TowerIndices[k];
                BuildingRecord b = i < buildings.Length ? buildings[i] : null;
                if (b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeSignalTower)
                {
                    continue;
                }
                // 信号塔只有修好（运转中）并且有电才提供覆盖（与它提供带宽的条件一致，fgdata_grid building.signal_tower.desc）。
                if (b.ConstructionState != BuildingConstructionState.Operational || b.PowerState != BuildingPowerState.Powered)
                {
                    continue;
                }
                into.Add(new Source { Center = b.Position, Radius = towerRadius, Kind = SignalCoverageSourceKind.Tower });
            }
        }

        /// <summary>从归还核心出发广度优先：两个覆盖源距离 ≤ 两者半径中较小的一个（“在彼此范围内”）就相连。</summary>
        private static void Connect()
        {
            if (Sources.Count == 0)
            {
                return;
            }
            Bfs.Clear();
            Source core = Sources[0];
            core.Connected = true;
            Sources[0] = core;
            Bfs.Enqueue(0);
            while (Bfs.Count > 0)
            {
                int i = Bfs.Dequeue();
                Source a = Sources[i];
                for (int j = 0; j < Sources.Count; j++)
                {
                    Source b = Sources[j];
                    if (b.Connected)
                    {
                        continue;
                    }
                    if (Vector2.Distance(a.Center, b.Center) <= Mathf.Min(a.Radius, b.Radius))
                    {
                        b.Connected = true;
                        Sources[j] = b;
                        Bfs.Enqueue(j);
                    }
                }
            }
        }

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v) && v > 0f)
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[SignalCoverageService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_signal.py 后重新生成）。");
            }
            return fallback;
        }

        /// <summary>世界卸载（回主菜单 / 回滚）时清掉覆盖源与建筑表索引：不再持有旧战役的引用。</summary>
        public static void Clear()
        {
            Sources.Clear();
            TowerIndices.Clear();
            _coreIndex = -1;
            _indexedBuildings = null;
            _builtFor = null;
            _builtBuildingCount = -1;
            _builtAtTick = long.MinValue;
        }

        public static void ResetForTests()
        {
            OverrideForTests = null;
            Clear();
            RebuildCount = 0;
            IndexCount = 0;
        }
    }
}
