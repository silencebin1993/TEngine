using System;
using System.Collections.Generic;
using BinGames.Sim.Signal;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>一次覆盖采样：这个位置在不在与归还核心连通的信号覆盖里、离边缘还有多远、是哪个覆盖源罩着它。</summary>
    public readonly struct SignalCoverageSample
    {
        /// <summary>true = 在覆盖里（含“这个地点不受覆盖限制”）。</summary>
        public readonly bool Covered;
        /// <summary>这个地点有没有覆盖边界（家园所在的星球表面与两个远征地点都有；不认识的地点没有）。</summary>
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
        /// <summary>归还核心（FGR-SIG-050 半径 150 格）。网络的根。</summary>
        Core,
        /// <summary>信号塔（运转且有电；T1 300 格，T2 500 格）。</summary>
        Tower,
        /// <summary>FG1-SIG-07：信号中继塔（建成即提供，200 格）。</summary>
        RelayTower,
        /// <summary>FG1-SIG-07：机器结构槽里的信号中继模块（随机器移动，80 格）。</summary>
        RelayModule,
        /// <summary>FG1-SIG-07：远征地点的接入点（远征地点仍是独立表面时的根，DEBT-FG0ARCH01-01 → FG8-GEN-02）。</summary>
        ExpeditionUplink,
    }

    /// <summary>一个覆盖源（叠加层、自检、通知读取）。</summary>
    public readonly struct SignalCoverageSourceInfo
    {
        public readonly string SiteId;
        public readonly Vector2 Center;
        public readonly float Radius;
        public readonly SignalCoverageSourceKind Kind;
        public readonly bool Connected;
        /// <summary>建筑覆盖源的建筑 ID（机器 / 接入点为空）。</summary>
        public readonly string BuildingId;
        /// <summary>中继模块所在机器的 LogicId（其余为 0）。</summary>
        public readonly int LogicId;
        /// <summary>把它接进网络的那个覆盖源下标（根与断开的为 -1）。</summary>
        public readonly int Parent;

        public SignalCoverageSourceInfo(string siteId, Vector2 center, float radius, SignalCoverageSourceKind kind, bool connected, string buildingId, int logicId, int parent)
        {
            SiteId = siteId;
            Center = center;
            Radius = radius;
            Kind = kind;
            Connected = connected;
            BuildingId = buildingId;
            LogicId = logicId;
            Parent = parent;
        }
    }

    /// <summary>
    /// FG1-SIG-04 起、FG1-SIG-07 补全（FG01 FGR-SIG-050～053）：信号覆盖网络。
    ///
    /// 覆盖源（FGR-SIG-050）：归还核心（signal.coverage.core_radius，150 格）、运转且有电的信号塔（T1 300 / T2 500，T2 由 <see cref="TowerTierProvider"/> 判定，
    /// 原地升级在 FG4-ECO-05）、建成的信号中继塔（200 格，不用电）、机器结构槽里的信号中继模块（80 格，随机器移动）。
    /// 地点：家园所在的星球表面以归还核心为根；两个远征地点仍是独立表面（DEBT-FG0ARCH01-01），以“远征接入点”为根（派遣建立的链路，
    /// signal.coverage.expedition_uplink_radius），迁入星球领地后（FG8-GEN-02）由真实中继链取代。
    /// 连通：覆盖源的**圆心落在已连通覆盖源的覆盖里**才接上网络（“中继要立在已连通的覆盖里”），广度优先在 AOT（<see cref="SignalNetKernel"/>）里算；
    /// 与核心断开的中继不提供覆盖，地图叠加层高亮断开处，并发通知。
    ///
    /// 开销与确定性：
    /// - 建筑表只在换了（增删建筑都会换新数组）或 <see cref="Invalidate"/> 时全量扫一遍，记下核心 / 信号塔 / 中继塔的下标；平时只重读这些下标上的记录。
    /// - 中继模块的机器由装配登记变化（<see cref="MachineLoadoutRegistry.Changed"/>）维护索引，不扫机器表；位置在每个时间桶开头读一次（O(中继机器数)）。
    /// - 世界模拟步首（<see cref="BeginStep"/>）每 signal.coverage.refresh_seconds 游戏秒重建一次连通（覆盖源逐位完全相同时沿用上次结果——
    ///   连通只取决于当前覆盖源，与上次在哪一刻算过无关），再把连通的覆盖圆交给各地点战斗内核逐单位评估（Burst），只拿回状态变了的机器——
    ///   热更层每帧 O(1)，与机器总数无关；结果只取决于游戏时间与世界状态，与是否被观察无关（FGR-BASE-021）。
    /// - 每次采样 O(该地点覆盖源数)：只有被接入的那台（每帧）、下命令时选中的机器、机器列表可见的行会采样。
    /// </summary>
    public static class SignalCoverageService
    {
        private struct Source
        {
            public Vector2 Center;
            public float Radius;
            public SignalCoverageSourceKind Kind;
            public bool Connected;
            public string BuildingId;
            public int LogicId;
            public int Parent;
        }

        private sealed class Net
        {
            public readonly string SiteId;
            public readonly List<Source> Sources = new List<Source>(8);
            public readonly List<float3> Circles = new List<float3>(8);
            public readonly List<float3> ConnectedCircles = new List<float3>(8);
            public byte[] ConnectedBuf = new byte[8];
            public int[] ParentBuf = new int[8];
            public int Disconnected;
            /// <summary>上次真正做连通计算时的覆盖源（种类 / 半径 / 圆心），逐位完全相同时沿用连通结果（纯函数的缓存，结果与计算时刻无关）。</summary>
            public readonly List<float3> LastBfsCircles = new List<float3>(8);
            public readonly List<byte> LastBfsKinds = new List<byte>(8);
            public ulong Signature;

            public Net(string siteId)
            {
                SiteId = siteId;
            }
        }

        private struct RelaySnap
        {
            public int LogicId;
            public string SiteId;
            public Vector2 Position;
        }

        private static readonly Net HomeNet = new Net(HomeValleyLayout.RegionId);
        private static readonly Net CityNet = new Net(FracturedCityLayout.RegionId);
        private static readonly Net FoundryNet = new Net(FoundryOutpostLayout.RegionId);
        private static readonly Net[] Nets = { HomeNet, CityNet, FoundryNet };

        private static long _builtAtTick = long.MinValue;
        private static long _relayBucket = long.MinValue;
        private static CampaignState _builtFor;
        private static int _builtBuildingCount = -1;
        // 建筑表索引：哪张表（数组引用）、核心在哪个下标、信号塔 / 中继塔在哪些下标。
        private static BuildingRecord[] _indexedBuildings;
        private static int _coreIndex = -1;
        private static readonly List<int> TowerIndices = new List<int>(8);
        private static readonly List<int> RelayTowerIndices = new List<int>(8);
        private static readonly List<RelaySnap> RelaySnapshot = new List<RelaySnap>(8);
        private static readonly List<int> RelayScratch = new List<int>(8);
        private static readonly HashSet<int> RelayMachines = new HashSet<int>();
        private static bool _relayHooked;
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        // 通知去重：静态覆盖源（建筑）上次是否连通；地点战斗内核是否已做过第一次评估（第一次只建立基线，不发通知）。
        private static readonly Dictionary<string, bool> StaticConnected = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, CombatSite> PrimedSites = new Dictionary<string, CombatSite>(StringComparer.Ordinal);
        private static bool _staticPrimed;
        /// <summary>步首最后一次处理过（断开通知、探索扩张）的网络版本：采样时的懒重建也会让版本变化，步首必须按“上次处理以来有没有变”判断，
        /// 否则被观察时（帧里先采样过）与不被观察时结果不同。</summary>
        private static int _processedVersion = int.MinValue;
        private static readonly List<(int LogicId, bool Covered)> ChangeScratch = new List<(int, bool)>(8);

        /// <summary>自检注入：按地点 + 位置直接给出采样（null = 走正式覆盖源）。</summary>
        public static Func<string, Vector2, SignalCoverageSample?> OverrideForTests;

        /// <summary>FG4-ECO-05 接入：信号塔的等级（1 = T1 300 格，≥2 = T2 500 格）。null = 全部 T1（信号塔原地升级尚未落地）。</summary>
        public static Func<BuildingRecord, int> TowerTierProvider;

        /// <summary>覆盖源重建次数（自检核对“不是每帧重建”）。</summary>
        public static int RebuildCount { get; private set; }

        /// <summary>建筑表全量扫描次数（自检核对“平时不按建筑总数扫”）。</summary>
        public static int IndexCount { get; private set; }

        /// <summary>连通计算（广度优先）真正执行的次数（覆盖源没变时沿用上次结果，不计）。</summary>
        public static int BfsCount { get; private set; }

        /// <summary>覆盖网络（覆盖源、连通）变化的版本号：叠加层据此重画。</summary>
        public static int Version { get; private set; } = 1;

        /// <summary>步首定期评估的次数、最近一次评估的游戏步、机器走出 / 回到覆盖与中继断开的累计次数（自检 / 冒烟读取）。</summary>
        public static int EvaluateCount { get; private set; }
        public static long LastEvaluatedTick { get; private set; } = -1;
        public static int LeftCount { get; private set; }
        public static int BackCount { get; private set; }
        public static int CutCount { get; private set; }
        public static double LastEvaluateMs { get; private set; }

        public static float CoreRadius => Tuning("signal.coverage.core_radius", 150f);
        public static float TowerRadius => Tuning("signal.coverage.tower_radius", 300f);
        public static float TowerT2Radius => Tuning("signal.coverage.tower_t2_radius", 500f);
        public static float RelayTowerRadius => Tuning("signal.coverage.relay_tower_radius", 200f);
        public static float RelayModuleRadius => Tuning("signal.coverage.relay_module_radius", 80f);
        public static float ExpeditionUplinkRadius => Tuning("signal.coverage.expedition_uplink_radius", 150f);
        public static float EdgeWarnCells => Tuning("signal.coverage.edge_warn_cells", 15f);
        public static float RefreshSeconds => Tuning("signal.coverage.refresh_seconds", 0.5f);

        /// <summary>这个地点有没有覆盖边界：家园所在的星球表面与两个远征地点都有；不认识的地点（自检里的临时地点）没有。</summary>
        public static bool IsBoundedSite(string regionId) => NetFor(regionId) != null;

        private static Net NetFor(string siteId)
        {
            if (siteId == HomeValleyLayout.RegionId)
            {
                return HomeNet;
            }
            if (siteId == FracturedCityLayout.RegionId)
            {
                return CityNet;
            }
            return siteId == FoundryOutpostLayout.RegionId ? FoundryNet : null;
        }

        /// <summary>远征地点接入点的位置（手工锚点，Demo 地点的出生 / 撤离点）。</summary>
        public static Vector2 ExpeditionUplinkPosition(string siteId) =>
            siteId == FracturedCityLayout.RegionId ? FracturedCityLayout.EntryEvac.Position : FoundryOutpostLayout.EntryEvac.Position;

        // ─────────────────────────────── 采样 ───────────────────────────────

        /// <summary>某台机器现在的覆盖采样（位置取实时位置；取不到时用记录里的位置）。</summary>
        public static SignalCoverageSample SampleMachine(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return SignalCoverageSample.Unbounded;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : rec.WorldPosition;
            return Sample(rec.RegionId, pos, logicId);
        }

        /// <summary>
        /// 某个地点某个位置的覆盖采样。<paramref name="selfLogicId"/>：被采样的是哪台机器（0 = 只是一个位置）。那台机器装着信号中继模块时，
        /// 它自己的覆盖圈以及只经由它接进网络的覆盖源都不算（审查修复：自己的圈跟着自己走，对“自己何时掉出覆盖”没有贡献，
        /// 算进去会让覆盖余量恒约 80 格、FG1-SIG-04 的边缘预警失效）。只在采样中继机器时多走一遍父链，O(覆盖源数 × 链深)。
        /// </summary>
        public static SignalCoverageSample Sample(string regionId, Vector2 position, int selfLogicId = 0)
        {
            if (OverrideForTests != null)
            {
                SignalCoverageSample? o = OverrideForTests(regionId, position);
                if (o.HasValue)
                {
                    return o.Value;
                }
            }
            Net net = NetFor(regionId);
            if (net == null)
            {
                return SignalCoverageSample.Unbounded;
            }
            EnsureSources(CampaignSession.Current);
            bool any = false;
            float bestMargin = float.NegativeInfinity;
            Source best = default;
            List<Source> sources = net.Sources;
            int self = selfLogicId > 0 && RelayMachines.Contains(selfLogicId) ? FindModuleSource(sources, selfLogicId) : -1;
            for (int i = 0; i < sources.Count; i++)
            {
                Source src = sources[i];
                if (!src.Connected || (self >= 0 && ReachedVia(sources, i, self)))
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
                return SignalCoverageSample.Unbounded; // 根永远是第 0 个且连通，走不到这里；防御：不让整个地点变成“覆盖外”。
            }
            return new SignalCoverageSample(bestMargin >= 0f, true, bestMargin, best.Center, best.Radius, best.Kind);
        }

        private static int FindModuleSource(List<Source> sources, int logicId)
        {
            for (int i = 1; i < sources.Count; i++)
            {
                if (sources[i].Kind == SignalCoverageSourceKind.RelayModule && sources[i].LogicId == logicId)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>覆盖源 <paramref name="i"/> 是不是经由 <paramref name="via"/> 接进网络的（沿连通计算记下的父链往上找；含它自己）。</summary>
        private static bool ReachedVia(List<Source> sources, int i, int via)
        {
            for (int guard = 0; i >= 0 && guard <= sources.Count; guard++)
            {
                if (i == via)
                {
                    return true;
                }
                i = sources[i].Parent;
            }
            return false;
        }

        /// <summary>FGR-SIG-053：这台机器现在能不能收到远程命令（在与核心连通的覆盖里）。下命令时逐台现采样（O(覆盖源数)），不读定期评估的缓存。</summary>
        public static bool CanReceiveCommand(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return true;
            }
            return SampleMachine(logicId).Covered;
        }

        /// <summary>FGR-SIG-053：这台机器是否被标为在覆盖之外（步首定期评估写在内核单位上，O(1)；机器列表、冒烟读取）。</summary>
        public static bool IsMachineOutOfCoverage(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return false;
            }
            if (OverrideForTests != null)
            {
                return !SampleMachine(logicId).Covered; // 自检注入覆盖时，与下命令时的现采样同一口径。
            }
            CombatSite site = CombatSites.Get(rec.RegionId);
            return site != null && site.IsMachineOutOfCoverage(logicId);
        }

        /// <summary>
        /// FGR-SIG-053 / FGR-EXP-001“路线有一段会走出信号覆盖”的提醒接口（派遣检查单 FG8-EXP-01 调；编队移动命令也用它提醒）：
        /// 沿折线 <paramref name="route"/> 按 <paramref name="stepCells"/> 采样，返回走出覆盖的总长度（格，约数）与第一次走出去的位置。
        /// 没有边界的地点恒为 0。开销 O(路线长度 / 步长 × 覆盖源数)，只在下令 / 打开检查单时调用。
        /// </summary>
        public static float RouteOutsideLength(string siteId, IReadOnlyList<Vector2> route, out Vector2 firstOut, float stepCells = 4f)
        {
            firstOut = default;
            if (route == null || route.Count == 0 || !IsBoundedSite(siteId))
            {
                return 0f;
            }
            float step = Mathf.Max(0.5f, stepCells);
            float outside = 0f;
            bool found = false;
            if (!Sample(siteId, route[0]).Covered)
            {
                found = true;
                firstOut = route[0];
            }
            for (int i = 1; i < route.Count; i++)
            {
                Vector2 a = route[i - 1];
                Vector2 b = route[i];
                float len = Vector2.Distance(a, b);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int k = 1; k <= n; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, k / (float)n);
                    if (!Sample(siteId, p).Covered)
                    {
                        outside += len / n;
                        if (!found)
                        {
                            found = true;
                            firstOut = p;
                        }
                    }
                }
            }
            return outside;
        }

        /// <summary>建筑状态变了（信号塔修好 / 断电 / 被拆、中继塔建成 / 损毁）时由调用方请求立即重建；否则按游戏时间节流重建。</summary>
        public static void Invalidate()
        {
            _builtAtTick = long.MinValue;
            _indexedBuildings = null;
        }

        // ─────────────────────────────── 查询覆盖源 ───────────────────────────────

        /// <summary>家园（星球表面）的覆盖源个数（下标 0 = 归还核心）。</summary>
        public static int SourceCount => SiteSourceCount(HomeValleyLayout.RegionId);

        public static bool TryGetSource(int index, out Vector2 center, out float radius, out SignalCoverageSourceKind kind, out bool connected)
        {
            bool ok = TryGetSiteSource(HomeValleyLayout.RegionId, index, out SignalCoverageSourceInfo info);
            center = info.Center;
            radius = info.Radius;
            kind = info.Kind;
            connected = info.Connected;
            return ok;
        }

        public static int SiteSourceCount(string siteId)
        {
            Net net = NetFor(siteId);
            if (net == null)
            {
                return 0;
            }
            EnsureSources(CampaignSession.Current);
            return net.Sources.Count;
        }

        public static bool TryGetSiteSource(string siteId, int index, out SignalCoverageSourceInfo info)
        {
            info = default;
            Net net = NetFor(siteId);
            if (net == null)
            {
                return false;
            }
            EnsureSources(CampaignSession.Current);
            if (index < 0 || index >= net.Sources.Count)
            {
                return false;
            }
            Source s = net.Sources[index];
            info = new SignalCoverageSourceInfo(net.SiteId, s.Center, s.Radius, s.Kind, s.Connected, s.BuildingId, s.LogicId, s.Parent);
            return true;
        }

        /// <summary>这个地点有几个覆盖源与核心断开（叠加层图例、HUD 读取）。</summary>
        public static int DisconnectedCount(string siteId)
        {
            Net net = NetFor(siteId);
            if (net == null)
            {
                return 0;
            }
            EnsureSources(CampaignSession.Current);
            return net.Disconnected;
        }

        /// <summary>覆盖源的显示名（叠加层图例、通知）：建筑名 / “某机器的中继模块” / 远征接入点。</summary>
        public static string SourceLabel(in SignalCoverageSourceInfo s)
        {
            switch (s.Kind)
            {
                case SignalCoverageSourceKind.Core: return GameText.Get("signal.coverage.kind.core");
                case SignalCoverageSourceKind.Tower: return GameText.Get("signal.coverage.kind.tower");
                case SignalCoverageSourceKind.RelayTower: return GameText.Get("signal.coverage.kind.relay_tower");
                case SignalCoverageSourceKind.RelayModule: return GameText.Format("signal.coverage.kind.relay_module", SignalPresence.MachineLabel(s.LogicId));
                case SignalCoverageSourceKind.ExpeditionUplink: return GameText.Get("signal.coverage.kind.expedition_uplink");
                default: return string.Empty;
            }
        }

        // ─────────────────────────────── 中继模块索引 ───────────────────────────────

        /// <summary>装着信号中继模块（结构槽 = 信号中继）的机器数（自检读取）。</summary>
        public static int RelayMachineCount
        {
            get
            {
                EnsureRelayHook();
                return RelayMachines.Count;
            }
        }

        public static bool IsRelayMachine(int logicId)
        {
            EnsureRelayHook();
            return RelayMachines.Contains(logicId);
        }

        private static void EnsureRelayHook()
        {
            if (_relayHooked)
            {
                return;
            }
            _relayHooked = true;
            MachineLoadoutRegistry.Changed += OnLoadoutChanged;
            RebuildRelayIndex();
        }

        /// <summary>装配登记变了（登记 / 解绑 / 整表清空 / 接入与冷却的重算通知）：只看这一台的结构槽是不是信号中继（O(1) 次蓝图查找）。</summary>
        private static void OnLoadoutChanged(int logicId)
        {
            if (logicId <= 0)
            {
                RebuildRelayIndex();
                return;
            }
            bool relay = MachineLoadoutRegistry.TryGetStructureId(CampaignSession.Current, logicId, out string structure)
                         && structure == ComponentCatalog.StructRelayId;
            bool changed = relay ? RelayMachines.Add(logicId) : RelayMachines.Remove(logicId);
            if (changed)
            {
                _relayBucket = long.MinValue; // 中继集合变了：下次重建重新读位置。
            }
        }

        private static void RebuildRelayIndex()
        {
            RelayMachines.Clear();
            CampaignState s = CampaignSession.Current;
            foreach (int id in MachineLoadoutRegistry.RegisteredMachines)
            {
                if (MachineLoadoutRegistry.TryGetStructureId(s, id, out string structure) && structure == ComponentCatalog.StructRelayId)
                {
                    RelayMachines.Add(id);
                }
            }
            _relayBucket = long.MinValue;
        }

        /// <summary>中继机器的位置快照（每个时间桶读一次；按 LogicId 排序，结果与集合的插入顺序无关）。</summary>
        private static void SnapshotRelays()
        {
            RelaySnapshot.Clear();
            RelayScratch.Clear();
            RelayScratch.AddRange(RelayMachines);
            RelayScratch.Sort();
            foreach (int id in RelayScratch)
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord rec) || rec == null || !rec.IsAlive || rec.IsInFactory)
                {
                    continue;
                }
                if (NetFor(rec.RegionId) == null)
                {
                    continue;
                }
                Vector2 pos = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : rec.WorldPosition;
                RelaySnapshot.Add(new RelaySnap { LogicId = id, SiteId = rec.RegionId, Position = pos });
            }
        }

        // ─────────────────────────────── 重建 ───────────────────────────────

        private static long Bucket()
        {
            long refreshTicks = Math.Max(1L, (long)Math.Round(RefreshSeconds * GameClock.StepHz));
            return GameClock.Ticks / refreshTicks;
        }

        /// <summary>按需重建（采样 / 查询时）：换战役、建筑表换了、<see cref="Invalidate"/> 或进了新的时间桶。中继机器的位置每个时间桶只读一次。</summary>
        private static void EnsureSources(CampaignState state)
        {
            EnsureRelayHook();
            BuildingRecord[] buildings = state?.BuildingRecords;
            int buildingCount = buildings?.Length ?? -1;
            bool reindex = !ReferenceEquals(state, _builtFor) || !ReferenceEquals(buildings, _indexedBuildings) || buildingCount != _builtBuildingCount;
            long bucket = Bucket();
            if (!reindex && bucket == _builtAtTick)
            {
                return;
            }
            Rebuild(state, buildings, buildingCount, reindex, bucket);
        }

        private static void Rebuild(CampaignState state, BuildingRecord[] buildings, int buildingCount, bool reindex, long bucket)
        {
            if (reindex)
            {
                if (!ReferenceEquals(state, _builtFor))
                {
                    // 换战役：通知去重与基线从头来（新战役 / 读档后的第一次评估不发“走出覆盖”之类的通知）。
                    StaticConnected.Clear();
                    PrimedSites.Clear();
                    _staticPrimed = false;
                    _relayBucket = long.MinValue;
                    RebuildRelayIndex();
                }
                _builtFor = state;
                _builtBuildingCount = buildingCount;
                _indexedBuildings = buildings;
                IndexBuildings(buildings);
            }
            if (_relayBucket != bucket)
            {
                _relayBucket = bucket;
                SnapshotRelays();
            }
            _builtAtTick = bucket;
            RebuildCount++;
            bool changed = false;
            foreach (Net net in Nets)
            {
                net.Sources.Clear();
                CollectSources(state, buildings, net);
                changed |= Connect(net);
            }
            if (changed)
            {
                Version++;
            }
        }

        private static bool IsHomeBuilding(BuildingRecord b) => b.RegionId == null || b.RegionId == HomeValleyLayout.RegionId;

        /// <summary>记下核心、信号塔、中继塔在建筑表里的下标（O(建筑数)，只在建筑表换了时做）。</summary>
        private static void IndexBuildings(BuildingRecord[] buildings)
        {
            IndexCount++;
            _coreIndex = -1;
            TowerIndices.Clear();
            RelayTowerIndices.Clear();
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
                else if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalRelay)
                {
                    RelayTowerIndices.Add(i);
                }
            }
        }

        /// <summary>收集一个地点的覆盖源（下标 0 永远是根：归还核心 / 远征接入点），只读索引记下的那几条建筑记录的当前状态与中继机器快照。</summary>
        private static void CollectSources(CampaignState state, BuildingRecord[] buildings, Net net)
        {
            if (net == HomeNet)
            {
                BuildingRecord coreRecord = buildings != null && _coreIndex >= 0 && _coreIndex < buildings.Length ? buildings[_coreIndex] : null;
                // 核心建筑记录（格网唯一计算的几何中心）优先；没有记录时（只建了战役的测试）按开局布局的核心锚点。
                Vector2 corePos = coreRecord != null ? coreRecord.Position : (state != null ? HomeValleyLayout.Core.Position : Vector2.zero);
                net.Sources.Add(new Source { Center = corePos, Radius = CoreRadius, Kind = SignalCoverageSourceKind.Core, BuildingId = coreRecord?.BuildingId, Parent = -1 });
                if (buildings != null)
                {
                    for (int k = 0; k < TowerIndices.Count; k++)
                    {
                        BuildingRecord b = TowerIndices[k] < buildings.Length ? buildings[TowerIndices[k]] : null;
                        // 信号塔只有修好（运转中）并且有电才提供覆盖（与它提供带宽的条件一致，building.signal_tower.desc）。
                        if (b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeSignalTower
                            || b.ConstructionState != BuildingConstructionState.Operational || b.PowerState != BuildingPowerState.Powered)
                        {
                            continue;
                        }
                        int tier = TowerTierProvider?.Invoke(b) ?? 1;
                        net.Sources.Add(new Source { Center = b.Position, Radius = tier >= 2 ? TowerT2Radius : TowerRadius, Kind = SignalCoverageSourceKind.Tower, BuildingId = b.BuildingId, Parent = -1 });
                    }
                    for (int k = 0; k < RelayTowerIndices.Count; k++)
                    {
                        BuildingRecord b = RelayTowerIndices[k] < buildings.Length ? buildings[RelayTowerIndices[k]] : null;
                        // 中继塔建成即提供覆盖（不用电，待用户复核）；损坏 / 停用 / 摧毁 / 施工中都不提供。
                        if (b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeSignalRelay || b.ConstructionState != BuildingConstructionState.Operational)
                        {
                            continue;
                        }
                        net.Sources.Add(new Source { Center = b.Position, Radius = RelayTowerRadius, Kind = SignalCoverageSourceKind.RelayTower, BuildingId = b.BuildingId, Parent = -1 });
                    }
                }
            }
            else
            {
                net.Sources.Add(new Source
                {
                    Center = ExpeditionUplinkPosition(net.SiteId),
                    Radius = ExpeditionUplinkRadius,
                    Kind = SignalCoverageSourceKind.ExpeditionUplink,
                    Parent = -1,
                });
            }
            float moduleRadius = RelayModuleRadius;
            for (int i = 0; i < RelaySnapshot.Count; i++)
            {
                RelaySnap r = RelaySnapshot[i];
                if (r.SiteId != net.SiteId)
                {
                    continue;
                }
                net.Sources.Add(new Source { Center = r.Position, Radius = moduleRadius, Kind = SignalCoverageSourceKind.RelayModule, LogicId = r.LogicId, Parent = -1 });
            }
        }

        /// <summary>
        /// 连通（AOT 广度优先）。覆盖源的种类、圆心、半径与上次计算时逐位完全相同才沿用上次结果——这是纯函数的缓存，
        /// 结果只取决于当前覆盖源：被观察 / 不被观察、继续玩 / 读档后都一样（审查修复：原先“圆心移动不到 1 格沿用”会让连通取决于上次在哪一刻算过，
        /// 中继机器停在门槛 1 格内时两边可能分叉；200 个覆盖源算一次约 0.01 ms，不需要近似）。返回网络是否变了。
        /// </summary>
        private static bool Connect(Net net)
        {
            int n = net.Sources.Count;
            net.Circles.Clear();
            for (int i = 0; i < n; i++)
            {
                Source s = net.Sources[i];
                net.Circles.Add(new float3(s.Center.x, s.Center.y, s.Radius));
            }
            bool reuse = n == net.LastBfsCircles.Count;
            for (int i = 0; reuse && i < n; i++)
            {
                float3 a = net.Circles[i];
                float3 b = net.LastBfsCircles[i];
                if (net.LastBfsKinds[i] != (byte)net.Sources[i].Kind
                    || BitConverter.SingleToInt32Bits(a.x) != BitConverter.SingleToInt32Bits(b.x)
                    || BitConverter.SingleToInt32Bits(a.y) != BitConverter.SingleToInt32Bits(b.y)
                    || BitConverter.SingleToInt32Bits(a.z) != BitConverter.SingleToInt32Bits(b.z))
                {
                    reuse = false;
                }
            }
            if (!reuse)
            {
                if (net.ConnectedBuf.Length < n)
                {
                    net.ConnectedBuf = new byte[Math.Max(n, net.ConnectedBuf.Length * 2)];
                    net.ParentBuf = new int[net.ConnectedBuf.Length];
                }
                SignalNetKernel.Connect(net.Circles, 1, net.ConnectedBuf, net.ParentBuf);
                BfsCount++;
                net.LastBfsCircles.Clear();
                net.LastBfsCircles.AddRange(net.Circles);
                net.LastBfsKinds.Clear();
                for (int i = 0; i < n; i++)
                {
                    net.LastBfsKinds.Add((byte)net.Sources[i].Kind);
                }
            }
            net.Disconnected = 0;
            net.ConnectedCircles.Clear();
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < n; i++)
            {
                Source s = net.Sources[i];
                s.Connected = net.ConnectedBuf[i] != 0;
                s.Parent = net.ParentBuf[i];
                net.Sources[i] = s;
                if (s.Connected)
                {
                    net.ConnectedCircles.Add(net.Circles[i]);
                }
                else
                {
                    net.Disconnected++;
                }
                h = Mix(h, (ulong)s.Kind);
                h = Mix(h, (ulong)BitConverter.SingleToInt32Bits(s.Center.x));
                h = Mix(h, (ulong)BitConverter.SingleToInt32Bits(s.Center.y));
                h = Mix(h, (ulong)BitConverter.SingleToInt32Bits(s.Radius));
                h = Mix(h, s.Connected ? 1UL : 0UL);
            }
            bool changed = h != net.Signature;
            net.Signature = h;
            return changed;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            unchecked
            {
                h ^= v;
                h *= 1099511628211UL;
                return h;
            }
        }

        // ─────────────────────────────── 步首定期评估（FGR-SIG-053）───────────────────────────────

        /// <summary>
        /// 世界模拟每个固定步的开头调用：到了新的时间桶（每 signal.coverage.refresh_seconds 游戏秒）就重建连通（覆盖源没变时沿用），
        /// 再把各地点的连通覆盖圆交给战斗内核逐单位评估（Burst），状态变了的机器发通知（走出 / 回到覆盖）；静态中继断开发通知；
        /// 连通的信号塔 / 中继塔的覆盖记为已探索（FGR-LOG-013）。与是否被观察无关：观察组与无头推进在同一步得到同样的结果。
        /// </summary>
        public static void BeginStep(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            long refreshTicks = Math.Max(1L, (long)Math.Round(RefreshSeconds * GameClock.StepHz));
            if (GameClock.Ticks % refreshTicks != 0)
            {
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureRelayHook();
            BuildingRecord[] buildings = state.BuildingRecords;
            int buildingCount = buildings?.Length ?? -1;
            bool reindex = !ReferenceEquals(state, _builtFor) || !ReferenceEquals(buildings, _indexedBuildings) || buildingCount != _builtBuildingCount;
            long bucket = Bucket();
            // 步首一律按“此刻”的世界状态重建一次（帧里的懒重建可能发生在桶内较早的时刻）——保证观察组与无头推进在同一步读到同一份覆盖源。
            _relayBucket = long.MinValue;
            Rebuild(state, buildings, buildingCount, reindex, bucket);
            EvaluateCount++;
            LastEvaluatedTick = GameClock.Ticks;
            if (Version != _processedVersion || !_staticPrimed)
            {
                AfterTopologyChange(state);
                _processedVersion = Version;
            }
            EvaluateSites(state);
            sw.Stop();
            LastEvaluateMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>网络变了：静态中继 / 信号塔断开的发通知（第一次只建立基线）；连通的信号塔 / 中继塔覆盖记为已探索。</summary>
        private static void AfterTopologyChange(CampaignState state)
        {
            bool primed = _staticPrimed;
            _staticPrimed = true;
            foreach (Net net in Nets)
            {
                for (int i = 0; i < net.Sources.Count; i++)
                {
                    Source s = net.Sources[i];
                    if (string.IsNullOrEmpty(s.BuildingId) || s.Kind == SignalCoverageSourceKind.Core)
                    {
                        continue;
                    }
                    bool known = StaticConnected.TryGetValue(s.BuildingId, out bool was);
                    StaticConnected[s.BuildingId] = s.Connected;
                    if (!s.Connected && primed && (!known || was))
                    {
                        CutCount++;
                        var info = new SignalCoverageSourceInfo(net.SiteId, s.Center, s.Radius, s.Kind, false, s.BuildingId, 0, -1);
                        string label = SourceLabel(info);
                        string site = net.SiteId;
                        Vector2 at = s.Center;
                        WorldSimulation.RunAsSite(site, () => NotificationCenter.Post("signal_relay_cut",
                            GameText.Format("signal.coverage.relay_disconnected", label), new Vector3(at.x, 0f, at.y)));
                        GuidanceHooks.Raise(GuidanceHooks.SignalRelayFirstCut);
                    }
                    // FG03 FGR-LOG-013“探索靠信号塔覆盖范围扩张”：连通的信号塔 / 中继塔覆盖记为已探索（只增不减）。核心自己的覆盖不扩张（开局探索圆由表定）。
                    if (s.Connected && net == HomeNet && (s.Kind == SignalCoverageSourceKind.Tower || s.Kind == SignalCoverageSourceKind.RelayTower))
                    {
                        HomeGridService.RevealArea(state, s.Center, s.Radius);
                    }
                }
            }
        }

        /// <summary>各已载入地点：把连通的覆盖圆交给战斗内核逐单位评估，处理状态变了的机器（O(变化数)）。</summary>
        private static void EvaluateSites(CampaignState state)
        {
            foreach (Net net in Nets)
            {
                CombatSite site = CombatSites.Get(net.SiteId);
                if (site == null || site.IsDisposed || !WorldSimulation.IsSiteLoaded(net.SiteId))
                {
                    continue;
                }
                ChangeScratch.Clear();
                site.EvaluateCoverage(net.ConnectedCircles, ChangeScratch);
                bool primed = PrimedSites.TryGetValue(net.SiteId, out CombatSite seen) && ReferenceEquals(seen, site);
                PrimedSites[net.SiteId] = site;
                if (!primed || ChangeScratch.Count == 0)
                {
                    continue; // 这个地点（这份内核）第一次评估：只建立基线（读档 / 新载入不刷“走出覆盖”通知）。
                }
                bool anyBack = false;
                foreach ((int logicId, bool covered) in ChangeScratch)
                {
                    string label = SignalPresence.MachineLabel(logicId);
                    Vector3? at = site.TryGetMachinePosition(logicId, out Vector2 p) ? new Vector3(p.x, 0f, p.y) : (Vector3?)null;
                    if (covered)
                    {
                        BackCount++;
                        anyBack = true;
                        WorldSimulation.RunAsSite(net.SiteId, () => NotificationCenter.Post("signal_coverage_back", GameText.Format("signal.coverage.back", label), at));
                    }
                    else
                    {
                        LeftCount++;
                        WorldSimulation.RunAsSite(net.SiteId, () => NotificationCenter.Post("signal_coverage_left", GameText.Format("signal.coverage.left", label), at));
                        GuidanceHooks.Raise(GuidanceHooks.SignalCoverageFirstLeft);
                    }
                }
                if (anyBack && net == HomeNet)
                {
                    // 回到覆盖后控制自动恢复（FGR-SIG-053）：家园的工作分配下次重新考虑这些机器（覆盖外时不给它们派活）。
                    HomeValleyWorkOrders.MarkAssignmentDirty();
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
            foreach (Net net in Nets)
            {
                net.Sources.Clear();
                net.Circles.Clear();
                net.ConnectedCircles.Clear();
                net.LastBfsCircles.Clear();
                net.LastBfsKinds.Clear();
                net.Disconnected = 0;
                net.Signature = 0;
            }
            TowerIndices.Clear();
            RelayTowerIndices.Clear();
            RelaySnapshot.Clear();
            StaticConnected.Clear();
            PrimedSites.Clear();
            _staticPrimed = false;
            _processedVersion = int.MinValue;
            _coreIndex = -1;
            _indexedBuildings = null;
            _builtFor = null;
            _builtBuildingCount = -1;
            _builtAtTick = long.MinValue;
            _relayBucket = long.MinValue;
            Version++;
        }

        public static void ResetForTests()
        {
            OverrideForTests = null;
            TowerTierProvider = null;
            Clear();
            RebuildCount = 0;
            IndexCount = 0;
            BfsCount = 0;
            EvaluateCount = 0;
            LastEvaluatedTick = -1;
            LeftCount = 0;
            BackCount = 0;
            CutCount = 0;
            RebuildRelayIndex();
        }
    }
}
