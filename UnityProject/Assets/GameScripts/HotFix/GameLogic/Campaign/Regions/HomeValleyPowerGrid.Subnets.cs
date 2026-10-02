using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>一个电力节点类型（fg.TbPowerNode 一行）。</summary>
    public readonly struct PowerNodeDef
    {
        public readonly string TypeId;
        public readonly float CoverRadius;
        /// <summary>储能容量（电·分钟）。</summary>
        public readonly float StorageCapacity;
        public readonly float StorageRate;

        public PowerNodeDef(string typeId, float coverRadius, float storageCapacity, float storageRate)
        {
            TypeId = typeId;
            CoverRadius = coverRadius;
            StorageCapacity = storageCapacity;
            StorageRate = storageRate;
        }
    }

    /// <summary>一座建筑在电网里的位置与结果（悬停、面板、叠加层读取）。</summary>
    public readonly struct BuildingPowerInfo
    {
        public readonly int Entity;
        /// <summary>电网下标（-1 = 未接入 / 不是电网实体）。</summary>
        public readonly int Subnet;
        public readonly PowerUse Use;
        public readonly bool IsNode;
        public readonly bool IsConsumer;
        public readonly bool IsProducer;
        public readonly bool IsStorage;

        public BuildingPowerInfo(int entity, int subnet, PowerUse use, bool isNode, bool isConsumer, bool isProducer, bool isStorage)
        {
            Entity = entity;
            Subnet = subnet;
            Use = use;
            IsNode = isNode;
            IsConsumer = isConsumer;
            IsProducer = isProducer;
            IsStorage = isStorage;
        }
    }

    /// <summary>
    /// FG3-LOG-06：电网内核在热更层的绑定（家园所在表面的 <see cref="PowerKernel"/>，AOT）、读表、世界步节拍、存读档与读数。
    /// 性能纪律：拓扑只在 <see cref="Recompute"/>（状态变化点）重建；<see cref="WorldStep"/> 每个世界步 O(1)，每游戏秒一次储能积分（只有有储能时）、
    /// 每 power.sample_seconds 游戏秒一次曲线采样（O(电网数)，在内核里）；悬停 / 面板读数 O(1) 或 O(一个电网)。
    /// </summary>
    public static partial class HomeValleyPowerGrid
    {
        /// <summary>归还核心的虚拟配电中心（没有核心建筑记录时）在存档里的锚点名。</summary>
        public const string VirtualHubAnchor = "@core";

        private static PowerKernel _kernel;
        private static CampaignState _state;
        private static bool _preserveSaved;
        private static bool _legacyCheckPending;
        private static bool _bindWasLegacySave;
        private static bool _suppressFeedback;
        private static int _lastPoleCount;
        private static PowerEntity[] _entities = new PowerEntity[64];
        private static BuildingRecord[] _recordAt = new BuildingRecord[64];
        private static int _entityCount;
        private static readonly Dictionary<string, int> KeyOf = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> IdOfKey = new List<string>(64);
        private static readonly Dictionary<string, int> EntityOf = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<BuildingRecord> Sorted = new List<BuildingRecord>(64);

        private static readonly Dictionary<string, PowerNodeDef> NodeDefs = new Dictionary<string, PowerNodeDef>(StringComparer.Ordinal);
        private static int _nodeDefRevision = -1;
        private static List<PowerNodeDef> _nodeOverride;

        public static PowerKernel Kernel => _kernel;
        public static CampaignState BoundState => _state;
        public static bool SavedDataPreserved => _preserveSaved;
        public static string LastLoadError { get; private set; }
        public static int LastLoadDropped { get; private set; }
        public static int LastSplitNotices { get; private set; }
        public static int LastUnconnectedNotices { get; private set; }
        public static long SecondsStepped { get; private set; }
        public static int EntityCount => _entityCount;

        // ── 读表（fg.TbPowerNode + power.* 调参）─────────────────────────────────────

        private static void EnsureNodeDefs()
        {
            if (_nodeOverride == null && _nodeDefRevision == GridContent.Revision && NodeDefs.Count > 0)
            {
                return;
            }
            NodeDefs.Clear();
            if (_nodeOverride != null)
            {
                foreach (PowerNodeDef d in _nodeOverride)
                {
                    NodeDefs[d.TypeId] = d;
                }
                return;
            }
            GameConfig.fg.TbPowerNode table = ConfigSystem.Instance?.Tables?.TbPowerNode;
            if (table != null)
            {
                foreach (GameConfig.fg.PowerNode row in table.DataList)
                {
                    if (row != null && !string.IsNullOrEmpty(row.TypeId))
                    {
                        NodeDefs[row.TypeId] = new PowerNodeDef(row.TypeId, row.CoverRadius, row.StorageCapacity, row.StorageRate);
                    }
                }
            }
            _nodeDefRevision = GridContent.Revision;
        }

        /// <summary>自检注入电力节点表（例如给某种建筑加储能来验证储能结算与曲线）；null = 还原真表。</summary>
        public static void OverrideNodesForTests(IEnumerable<PowerNodeDef> defs)
        {
            _nodeOverride = defs != null ? new List<PowerNodeDef>(defs) : null;
            _nodeDefRevision = -1;
            NodeDefs.Clear();
        }

        public static bool TryGetNodeDef(string typeId, out PowerNodeDef def)
        {
            EnsureNodeDefs();
            def = default;
            return typeId != null && NodeDefs.TryGetValue(typeId, out def);
        }

        /// <summary>这种建筑的电力覆盖半径（格）；0 = 不是电力节点。</summary>
        public static float CoverRadiusOf(string typeId) => TryGetNodeDef(typeId, out PowerNodeDef d) ? d.CoverRadius : 0f;

        /// <summary>电塔（玩家可放、只做覆盖的电力节点，不含归还核心）。</summary>
        public static bool IsPoleType(string typeId) =>
            typeId != HomeValleyLayout.BuildingTypeCore && CoverRadiusOf(typeId) > 0f;

        /// <summary>这种建筑和电网有关（节点 / 用电 / 发电 / 储能）：放置预览与叠加层只对这些显示电力信息。</summary>
        public static bool IsPowerRelevantType(string typeId)
        {
            if (typeId == null)
            {
                return false;
            }
            return CoverRadiusOf(typeId) > 0f || HomeValleyLayout.PowerProfile.ContainsKey(typeId) || HomeValleyLayout.PowerSupplyProfile.ContainsKey(typeId)
                   || (TryGetNodeDef(typeId, out PowerNodeDef d) && d.StorageCapacity > 0f);
        }

        public static float SampleSeconds => Math.Max(1f, TuningOr("power.sample_seconds", 10f));
        public static int CurveSamples => Math.Max(2, (int)Math.Round(TuningOr("power.curve_samples", 90f)));
        private static int PreviewMaxNodes => Math.Max(4, (int)Math.Round(TuningOr("power.preview_max_nodes", 64f)));

        private static float TuningOr(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Error($"[HomeValleyPowerGrid] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_power.py 后重新生成）。");
            return fallback;
        }

        // ── 绑定与存读档 ─────────────────────────────────────────────────────────

        /// <summary>内核绑定在一个战役上；换战役时先把旧战役的曲线 / 储能写回它自己的存档域，再从新战役的存档域恢复。</summary>
        private static PowerKernel EnsureKernel(CampaignState state)
        {
            if (_kernel != null && ReferenceEquals(_state, state))
            {
                _suppressFeedback = false;
                return _kernel;
            }
            Unbind();
            CampaignFgStateDomains.EnsureAll(state);
            _state = state;
            _kernel = new PowerKernel(CurveSamples);
            _summaryValid = false;
            SummaryVersion++;
            _preserveSaved = false;
            LastLoadError = null;
            LastLoadDropped = 0;
            _lastPoleCount = 0;
            SecondsStepped = 0;
            KeyOf.Clear();
            IdOfKey.Clear();
            PowerGridState saved = state.Power;
            _bindWasLegacySave = saved.DomainVersion < 2 && HasOperationalBuildings(state);
            _legacyCheckPending = true;
            _suppressFeedback = _bindWasLegacySave;
            if (saved.FormatVersion > 0)
            {
                if (!_kernel.Restore(ToSnapshot(saved), out string err, out int dropped))
                {
                    // 不认识的格式 / 形状不对：曲线从零开始、储能按空，原始数据保留不覆盖（这局的存档不写电网域）。
                    _preserveSaved = true;
                    LastLoadError = err;
                    Log.Error($"[HomeValleyPowerGrid] 电网存档无法读取（{err}），原始数据保留不覆盖");
                    NotificationCenter.Post("save_migrated", GameText.Format("power.notify.load_unreadable", saved.FormatVersion));
                }
                else
                {
                    LastLoadDropped = dropped;
                    if (dropped > 0)
                    {
                        Log.Warning($"[HomeValleyPowerGrid] 电网存档有 {dropped} 条记录损坏已丢弃");
                    }
                }
            }
            return _kernel;
        }

        private static bool HasOperationalBuildings(CampaignState state)
        {
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                // 旧存档：除核心外（新战役里核心开局就写成有电）有用电建筑已经被旧电网仲裁过；新战役的开局建筑还是 NotApplicable。
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational
                    && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                    && HomeValleyLayout.PowerProfile.ContainsKey(b.BuildingTypeId) && b.PowerState != BuildingPowerState.NotApplicable)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>解除绑定（卸载世界 / 换战役）：先把曲线与储能写回绑定的战役。</summary>
        public static void Unbind()
        {
            if (_kernel != null && _state != null)
            {
                WriteTo(_state);
            }
            _kernel = null;
            _state = null;
            _summaryValid = false;
            SummaryVersion++;
            _orderSourceArray = null;
            _orderCache.Clear();
            _orderCachePriorities.Clear();
            _entityCount = 0;
            EntityOf.Clear();
            _preserveSaved = false;
            _legacyCheckPending = false;
            _suppressFeedback = false;
        }

        /// <summary>存档前（WorldSimulation.SyncAllForSave）：曲线、电网编号锚点、储能存量写进 <see cref="PowerGridState"/>。</summary>
        public static void WriteTo(CampaignState state)
        {
            if (_kernel == null || state == null || !ReferenceEquals(state, _state) || _preserveSaved)
            {
                return;
            }
            PowerSnapshot s = _kernel.Serialize();
            PowerGridState p = state.Power ??= new PowerGridState();
            p.DomainVersion = 3; // FG4-ECO-04：加储能站设置、曲线的各类别发电
            p.FormatVersion = s.FormatVersion;
            p.NextSerial = s.NextSerial;
            p.SubnetSerials = s.SubnetSerials;
            p.SubnetAnchors = new string[s.SubnetAnchorKeys.Length];
            for (int i = 0; i < p.SubnetAnchors.Length; i++)
            {
                p.SubnetAnchors[i] = IdForKey(s.SubnetAnchorKeys[i]);
            }
            p.CurveCounts = s.CurveCounts;
            p.CurveSupply = s.CurveSupply;
            p.CurveDemand = s.CurveDemand;
            p.CurveDelivered = s.CurveDelivered;
            p.CurveStored = s.CurveStored;
            p.CurveClassSupply = s.CurveClassSupply;
            p.StorageIds = new string[s.StorageKeys.Length];
            for (int i = 0; i < p.StorageIds.Length; i++)
            {
                p.StorageIds[i] = IdForKey(s.StorageKeys[i]);
            }
            p.StorageStored = s.StorageStored;
        }

        private static PowerSnapshot ToSnapshot(PowerGridState p)
        {
            var snap = new PowerSnapshot
            {
                FormatVersion = p.FormatVersion,
                NextSerial = p.NextSerial,
                SubnetSerials = p.SubnetSerials ?? Array.Empty<int>(),
                CurveCounts = p.CurveCounts ?? Array.Empty<int>(),
                CurveSupply = p.CurveSupply ?? Array.Empty<float>(),
                CurveDemand = p.CurveDemand ?? Array.Empty<float>(),
                CurveDelivered = p.CurveDelivered ?? Array.Empty<float>(),
                CurveStored = p.CurveStored ?? Array.Empty<float>(),
                CurveClassSupply = p.CurveClassSupply ?? Array.Empty<float>(),
                StorageStored = p.StorageStored ?? Array.Empty<double>(),
            };
            string[] anchors = p.SubnetAnchors ?? Array.Empty<string>();
            snap.SubnetAnchorKeys = new int[anchors.Length];
            for (int i = 0; i < anchors.Length; i++)
            {
                snap.SubnetAnchorKeys[i] = KeyForId(anchors[i]);
            }
            string[] ids = p.StorageIds ?? Array.Empty<string>();
            snap.StorageKeys = new int[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                snap.StorageKeys[i] = KeyForId(ids[i]);
            }
            return snap;
        }

        private static int KeyForId(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId) || buildingId == VirtualHubAnchor)
            {
                return PowerKernel.VirtualHubKey;
            }
            if (!KeyOf.TryGetValue(buildingId, out int key))
            {
                key = IdOfKey.Count;
                IdOfKey.Add(buildingId);
                KeyOf[buildingId] = key;
            }
            return key;
        }

        private static string IdForKey(int key) => key >= 0 && key < IdOfKey.Count ? IdOfKey[key] : VirtualHubAnchor;

        // ── 组装内核输入 ─────────────────────────────────────────────────────────

        private static readonly Comparison<BuildingRecord> AllocationOrder = (a, b) =>
        {
            int c = a.PowerPriority.CompareTo(b.PowerPriority);
            return c != 0 ? c : string.CompareOrdinal(a.BuildingId, b.BuildingId);
        };

        /// <summary>组装时每种建筑只查一次表（一次重算里种类只有十几种，建筑可能上千座）。</summary>
        private struct TypePowerInfo
        {
            public bool HasGrid;
            public int FootprintW;
            public int FootprintH;
            public PowerNodeDef Node;
            public bool HasSupply;
            public float Supply;
            public bool HasDemand;
            public float Demand;
            /// <summary>FG4-ECO-11：这类建筑的耗电按等级变（fg.TbBuildingTier.powerDemand，超控阵列 80 / 120 / 160）；只有它才逐座查等级。</summary>
            public bool TierDemand;
            // FG4-ECO-04：发电类别（曲线分类、太阳能系数、燃油按负荷）。
            public byte SourceClass;
            public PowerSourceKind SourceKind;
        }

        private static readonly Dictionary<string, TypePowerInfo> TypeInfoCache = new Dictionary<string, TypePowerInfo>(StringComparer.Ordinal);

        private static TypePowerInfo TypeInfo(string typeId)
        {
            if (typeId == null)
            {
                return default;
            }
            if (!TypeInfoCache.TryGetValue(typeId, out TypePowerInfo t))
            {
                if (GridContent.TryGetBuilding(typeId, out GameConfig.fg.BuildingGrid g))
                {
                    t.HasGrid = true;
                    t.FootprintW = g.FootprintW;
                    t.FootprintH = g.FootprintH;
                }
                TryGetNodeDef(typeId, out t.Node);
                t.HasSupply = HomeValleyLayout.PowerSupplyProfile.TryGetValue(typeId, out t.Supply);
                t.HasDemand = HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
                t.Demand = prof.PowerDemand;
                t.TierDemand = t.HasDemand && HasTierDemand(typeId);
                if (t.HasSupply || typeId == HomeValleyLayout.BuildingTypeCore)
                {
                    PowerSourceDef src = SourceOf(typeId);
                    t.SourceClass = (byte)Math.Max(0, Math.Min(PowerKernel.MaxSourceClasses - 1, src.ClassIndex));
                    t.SourceKind = src.Kind;
                }
                TypeInfoCache[typeId] = t;
            }
            return t;
        }

        // 分配顺序复用：上一次排好的顺序 + 当时的记录数组与各自优先级。记录数组没换、每个位置还是同一个对象、优先级都没变
        // （完工 / 损毁 / 修复 / 关停 / 旋转这类只改状态的重算）时直接沿用，不再按字符串重排——O(建筑) 的引用比较代替 O(建筑 log 建筑) 的字符串比较。
        private static BuildingRecord[] _orderSourceArray;
        private static BuildingRecord[] _orderSourceCopy = Array.Empty<BuildingRecord>();
        private static int[] _orderPriorities = Array.Empty<int>();
        private static bool[] _orderInRegion = Array.Empty<bool>();
        private static readonly List<BuildingRecord> _orderCache = new List<BuildingRecord>(64);
        private static readonly List<int> _orderCachePriorities = new List<int>(64);
        private static readonly HashSet<BuildingRecord> _orderCurrent = new HashSet<BuildingRecord>();
        private static readonly HashSet<BuildingRecord> _orderKeptSet = new HashSet<BuildingRecord>();
        private static readonly List<BuildingRecord> _orderKept = new List<BuildingRecord>(64);
        private static readonly List<BuildingRecord> _orderAdded = new List<BuildingRecord>(16);
        private static readonly IComparer<BuildingRecord> AllocationComparer = Comparer<BuildingRecord>.Create((x, y) => AllocationOrder(x, y));

        /// <summary>自检读：最近一次组装是否沿用了上一次的分配顺序。</summary>
        public static bool LastAssembleReusedOrder { get; private set; }
        /// <summary>自检读：最近一次组装是否在上一次的顺序上增量插入（新增 / 移除少量建筑）。</summary>
        public static bool LastAssembleIncremental { get; private set; }

        /// <summary>
        /// 记录数组换了（新增 / 移除建筑、改了优先级）：沿用上一次顺序里还在、优先级没变的建筑（相对顺序不变），
        /// 只把新来的几座排好后二分插进去——O(建筑) 的引用比较 + O(新增 × log 建筑) 的字符串比较。新增太多（首次绑定、读档）时返回 false，整体重排。
        /// </summary>
        private static bool TryIncrementalOrder(BuildingRecord[] records)
        {
            if (_orderCache.Count == 0)
            {
                return false;
            }
            _orderCurrent.Clear();
            foreach (BuildingRecord b in records)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId)
                {
                    _orderCurrent.Add(b);
                }
            }
            _orderKept.Clear();
            _orderKeptSet.Clear();
            for (int i = 0; i < _orderCache.Count; i++)
            {
                BuildingRecord b = _orderCache[i];
                if (_orderCurrent.Contains(b) && b.PowerPriority == _orderCachePriorities[i])
                {
                    _orderKept.Add(b);
                    _orderKeptSet.Add(b);
                }
            }
            int added = _orderCurrent.Count - _orderKept.Count;
            if (added > Math.Max(16, _orderCurrent.Count / 8))
            {
                return false;
            }
            _orderAdded.Clear();
            foreach (BuildingRecord b in records)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && !_orderKeptSet.Contains(b))
                {
                    _orderAdded.Add(b);
                }
            }
            _orderAdded.Sort(AllocationOrder);
            // 两段都已有序：新来的逐个二分找插入点（kept 里第一个比它大的位置），再一次线性拼起来。
            int k = 0;
            foreach (BuildingRecord a in _orderAdded)
            {
                int pos = _orderKept.BinarySearch(a, AllocationComparer);
                if (pos < 0)
                {
                    pos = ~pos;
                }
                for (; k < pos; k++)
                {
                    Sorted.Add(_orderKept[k]);
                }
                Sorted.Add(a);
            }
            for (; k < _orderKept.Count; k++)
            {
                Sorted.Add(_orderKept[k]);
            }
            return true;
        }

        private static bool CanReuseOrder(BuildingRecord[] records)
        {
            if (records == null || !ReferenceEquals(records, _orderSourceArray) || records.Length != _orderSourceCopy.Length)
            {
                return false;
            }
            for (int i = 0; i < records.Length; i++)
            {
                BuildingRecord b = records[i];
                if (!ReferenceEquals(b, _orderSourceCopy[i]) || (b != null && (b.PowerPriority != _orderPriorities[i] || (b.RegionId == HomeValleyLayout.RegionId) != _orderInRegion[i])))
                {
                    return false;
                }
            }
            return true;
        }

        private static void RememberOrder(BuildingRecord[] records)
        {
            _orderSourceArray = records;
            int n = records?.Length ?? 0;
            if (_orderSourceCopy.Length != n)
            {
                _orderSourceCopy = new BuildingRecord[n];
                _orderPriorities = new int[n];
                _orderInRegion = new bool[n];
            }
            for (int i = 0; i < n; i++)
            {
                _orderSourceCopy[i] = records[i];
                _orderPriorities[i] = records[i]?.PowerPriority ?? 0;
                _orderInRegion[i] = records[i] != null && records[i].RegionId == HomeValleyLayout.RegionId;
            }
            _orderCache.Clear();
            _orderCache.AddRange(Sorted);
            _orderCachePriorities.Clear();
            foreach (BuildingRecord b in Sorted)
            {
                _orderCachePriorities.Add(b.PowerPriority);
            }
        }

        /// <summary>按建筑记录组装内核实体表（按“优先级 → 建筑编号”排好 = 分配顺序，AC-ECO-004）。只在拓扑变化点：
        /// 记录数组变了（新增 / 移除建筑）或有优先级改动时 O(建筑 log 建筑) 重排，否则沿用上一次的顺序（O(建筑)）。</summary>
        private static void BuildEntities(CampaignState state)
        {
            Sorted.Clear();
            TypeInfoCache.Clear(); // 每次重算重新查一遍表（表热重载 / 自检覆盖节点定义都能生效），十几种类型，开销可忽略
            BuildingRecord core = null;
            BuildingRecord[] records = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            LastAssembleReusedOrder = CanReuseOrder(records);
            LastAssembleIncremental = false;
            if (LastAssembleReusedOrder)
            {
                Sorted.AddRange(_orderCache);
                // 核心按数组顺序找第一座（与重排路径一致）。
                foreach (BuildingRecord b in records)
                {
                    if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                    {
                        core = b;
                        break;
                    }
                }
            }
            else
            {
                LastAssembleIncremental = TryIncrementalOrder(records);
                if (!LastAssembleIncremental)
                {
                    Sorted.Clear();
                    foreach (BuildingRecord b in records)
                    {
                        if (b != null && b.RegionId == HomeValleyLayout.RegionId)
                        {
                            Sorted.Add(b);
                        }
                    }
                    Sorted.Sort(AllocationOrder);
                }
                foreach (BuildingRecord b in records)
                {
                    if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                    {
                        core = b;
                        break;
                    }
                }
                RememberOrder(records);
            }
            int n = Sorted.Count + (core == null ? 1 : 0);
            if (_entities.Length < n)
            {
                int cap = Math.Max(n, _entities.Length * 2);
                _entities = new PowerEntity[cap];
                _recordAt = new BuildingRecord[cap];
            }
            EntityOf.Clear();
            int w = 0;
            float baseSupply = HomeValleyLayout.BaseCoreSupply;
            float coreRadius = CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            // FG4-ECO-04：燃油发电机有没有油（ProducerRecord.Fueled）、储能站设置（PowerGridState）——只在组装时读一次。
            CollectFueled(state);
            CollectStorageSettings(state);
            byte coreClass = TypeInfo(HomeValleyLayout.BuildingTypeCore).SourceClass;
            int energyBuilt = 0;
            if (core == null)
            {
                // 没有核心建筑记录（自检里的合成战役 / 极旧的存档）：在核心枢轴格放一个虚拟配电中心，承载核心自带的基础供电。
                GridCell pivot = HomeGridService.CorePivot(state);
                _recordAt[w] = null;
                _entities[w++] = new PowerEntity
                {
                    Key = PowerKernel.VirtualHubKey,
                    MinX = pivot.X, MinY = pivot.Y, MaxX = pivot.X, MaxY = pivot.Y,
                    NodeRadius = coreRadius, Conducts = true,
                    Supply = baseSupply, SupplyOn = true, SourceClass = coreClass,
                };
            }
            foreach (BuildingRecord b in Sorted)
            {
                TypePowerInfo ti = TypeInfo(b.BuildingTypeId);
                GridCell min, max;
                if (ti.HasGrid)
                {
                    GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), ti.FootprintW, ti.FootprintH, GridMath.NormalizeRotation(b.Rotation), out min, out max);
                }
                else
                {
                    min = max = new GridCell(b.GridX, b.GridY);
                }
                bool operational = b.ConstructionState == BuildingConstructionState.Operational;
                bool isCore = ReferenceEquals(b, core);
                PowerNodeDef def = ti.Node;
                var e = new PowerEntity
                {
                    Key = KeyForId(b.BuildingId),
                    MinX = min.X, MinY = min.Y, MaxX = max.X, MaxY = max.Y,
                    NodeRadius = def.CoverRadius,
                    // 归还核心的配电与它的基础供电一样不依赖建造状态（只有被摧毁时不导电）；电塔要运转中才导电。
                    Conducts = def.CoverRadius > 0f && (isCore ? b.ConstructionState != BuildingConstructionState.Destroyed : operational),
                    Priority = b.PowerPriority,
                };
                if (isCore)
                {
                    e.Supply = baseSupply;
                    e.SupplyOn = true;
                    e.SourceClass = coreClass;
                }
                if (ti.HasSupply && !isCore)
                {
                    // FG4-ECO-04：发电类别；燃油发电机按负荷出力、没油时不发电（满额照记，开关由 SetGeneratorFueled 改）。
                    e.SourceClass = ti.SourceClass;
                    e.Dispatchable = ti.SourceKind == PowerSourceKind.Fuel;
                    e.Supply = ti.Supply;
                    e.SupplyOn = operational && (!e.Dispatchable || FueledScratch.ContainsKey(b.BuildingId));
                    if (operational && ti.SourceKind != PowerSourceKind.Fixed)
                    {
                        energyBuilt++;
                    }
                }
                else if (operational && ti.HasSupply)
                {
                    e.Supply += ti.Supply;
                    e.SupplyOn = true;
                }
                if (operational && ti.HasDemand)
                {
                    e.Demand = ti.TierDemand ? DemandOf(b) : ti.Demand;
                    e.DemandOn = true;
                }
                if (def.StorageCapacity > 0f && def.StorageRate > 0f)
                {
                    e.StorageCapacity = def.StorageCapacity * 60.0 * Economy.ResearchService.CapacityFactor(state, "energy_storage"); // FG5-RND-01：研发“电芯扩容”
                    e.StorageRate = def.StorageRate;
                    e.StorageOn = operational;
                    if (StorageScratch.TryGetValue(b.BuildingId, out StorageSettings ss))
                    {
                        e.StorageNoCharge = ss.NoCharge;
                        e.StorageNoDischarge = ss.NoDischarge;
                        e.StorageReserve = (byte)ss.Reserve;
                    }
                    if (operational)
                    {
                        energyBuilt++;
                    }
                }
                EntityOf[b.BuildingId] = w;
                _recordAt[w] = b;
                _entities[w++] = e;
            }
            _entityCount = w;
            // FG4-ECO-04（B14 引导钩子）：第一次建成能源建筑（燃油发电机 / 太阳能阵列 / 储能站）。
            if (energyBuilt > _lastEnergyCount && !_suppressFeedback)
            {
                GuidanceHooks.Raise(GuidanceHooks.EnergyFirstBuilt);
            }
            _lastEnergyCount = energyBuilt;
        }

        private static void FootprintBounds(BuildingRecord b, out GridCell min, out GridCell max)
        {
            if (GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g))
            {
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(b.Rotation), out min, out max);
                return;
            }
            min = max = new GridCell(b.GridX, b.GridY);
        }

        // ── 时间：储能积分与曲线采样 ─────────────────────────────────────────────

        /// <summary>
        /// 世界模拟的一个固定步（WorldSimulation 在传送带 / 管线之后调用）。只看绝对步序号：每跨过一个游戏秒做一次储能积分（没有储能时什么都不做），
        /// 每 power.sample_seconds 游戏秒给每个电网的曲线记一个点。暂停不走、倍速只是一帧多走几步、与有没有人观察无关（FGR-BASE-021）。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null || worldHz <= 0)
            {
                return;
            }
            long after = ticksBefore + 1;
            if (after % worldHz != 0)
            {
                return;
            }
            if (_kernel == null || !ReferenceEquals(state, _state))
            {
                Recompute(state);
            }
            long second = after / worldHz;
            SecondsStepped++;
            // FG4-ECO-04：太阳能系数（昼夜 / 天气接口，O(1)）；变了重新结算并写回（发电变了，汇总与供电结果都可能变）。
            bool envChanged = RefreshEnvironment(state, _kernel);
            if (envChanged)
            {
                _kernel.Settle();
            }
            if (_kernel.AnyStorage && _kernel.StepSecond())
            {
                ApplyResults(state, topologyChanged: false);
            }
            else if (envChanged)
            {
                ApplyResults(state, topologyChanged: false);
            }
            if (_kernel.AnyStorage)
            {
                CheckStorageEmpty();
            }
            // FG4-ECO-09：远征在外时每游戏秒累加离家报告的发电（按类别）/ 需要 / 实际供上 / 储能充放与缺电秒数（O(电网 × 类别)）。
            Economy.AwayReportService.OnPowerSecond(state, _kernel);
            long every = Math.Max(1L, (long)Math.Round(SampleSeconds));
            if (second % every == 0)
            {
                _kernel.Sample();
            }
        }

        // ── 建筑损毁（FG6 突袭 / FG7 天气的接入点）──────────────────────────────────

        /// <summary>
        /// 一座已建成的建筑被摧毁：变成受损（可以修复，修好即重新接入），电网立即重算——电塔被摧毁时电网可能断成几段，各自结算，并发“电网断开”警告（FG03 第 5 节）。
        /// 目前没有正式的损毁来源（突袭损毁建筑在 FG6-DEF-05，FG-GAP-084）；本 Story 的负向测试经此入口摧毁电塔。
        /// </summary>
        public static bool ApplyBuildingDestroyed(CampaignState state, string buildingId)
        {
            BuildingRecord b = HomeGridService.FindBuilding(state, buildingId);
            if (b == null || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore
                || (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled))
            {
                return false;
            }
            b.DisabledWhenDestroyed = b.ConstructionState == BuildingConstructionState.Disabled;
            b.ConstructionState = BuildingConstructionState.Damaged;
            b.Health = 0f;
            BuildingVisualFeed.Mark(b);
            Recompute(state);
            // FG4-ECO-05（卡片负向“升级中被摧毁”；FGR-ECO-013）：摧毁的回调——取消升级 / 搬迁与受损维修（材料全额退回）、通知。
            Economy.BuildingOps.OnBuildingDestroyed(state, b);
            return true;
        }

        // ── 读数 ─────────────────────────────────────────────────────────────

        public static string SubnetName(int serial) => GameText.Format("power.subnet.name", serial);

        public static string Num(float v) => Mathf.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>一座建筑在电网里的位置（绑定的战役 + 上一次重算的结果；O(1)）。</summary>
        public static bool TryGetBuildingPower(CampaignState state, string buildingId, out BuildingPowerInfo info)
        {
            info = default;
            if (_kernel == null || !ReferenceEquals(state, _state) || buildingId == null || !EntityOf.TryGetValue(buildingId, out int i) || i >= _entityCount)
            {
                return false;
            }
            PowerEntity e = _entities[i];
            bool node = e.NodeRadius > 0f;
            info = new BuildingPowerInfo(i, _kernel.SubnetOf(i), _kernel.UseOf(i), node, HomeValleyLayout.PowerProfile.ContainsKey(_recordAt[i].BuildingTypeId),
                HomeValleyLayout.PowerSupplyProfile.ContainsKey(_recordAt[i].BuildingTypeId) || _recordAt[i].BuildingTypeId == HomeValleyLayout.BuildingTypeCore,
                e.StorageCapacity > 0);
            return true;
        }

        /// <summary>第 i 个内核实体对应的建筑记录（虚拟配电中心为 null）。</summary>
        public static BuildingRecord RecordAt(int entity) => entity >= 0 && entity < _entityCount ? _recordAt[entity] : null;

        /// <summary>FG4-ECO-08（资源顶栏“电力（各子网）”）：电网 s 上一次结算的读数（编号、发电、需要、实际供上、储能）。O(1)；
        /// <paramref name="state"/> 不是当前绑定的战役或下标越界时返回 false。</summary>
        public static bool TryGetSubnetInfo(CampaignState state, int s, out PowerSubnetInfo info)
        {
            info = default;
            if (_kernel == null || !ReferenceEquals(state, _state) || s < 0 || s >= _kernel.SubnetCount)
            {
                return false;
            }
            info = _kernel.Subnet(s);
            return true;
        }

        /// <summary>电网 s 的第一行读数（名字、发电、实际用电 / 需要）。</summary>
        public static string DescribeSubnetLine(int s)
        {
            PowerSubnetInfo n = _kernel.Subnet(s);
            return GameText.Format("power.hover.subnet", SubnetName(n.Serial), Num(n.Supply), Num(n.Delivered), Num(n.Demand));
        }

        /// <summary>电网 s 的详细读数（悬停 / 面板共用）：发电与用电、储能、成员、缺口。</summary>
        public static void AppendSubnetLines(StringBuilder sb, int s)
        {
            PowerSubnetInfo n = _kernel.Subnet(s);
            sb.Append(DescribeSubnetLine(s));
            string sources = DescribeSources(s);
            if (sources.Length > 0)
            {
                sb.Append('\n').Append(sources);
            }
            if (n.DispatchSupply > 0f)
            {
                sb.Append('\n').Append(GameText.Format("power.hover.fuel_load", Mathf.RoundToInt(n.DispatchLoad * 100f), Num(n.DispatchSupply * n.DispatchLoad), Num(n.DispatchSupply)));
            }
            if (n.StorageUnits > 0)
            {
                string flow = n.StorageFlow > 0f ? GameText.Format("power.hover.storage_charging", Num(n.StorageFlow))
                    : n.StorageFlow < 0f ? GameText.Format("power.hover.storage_discharging", Num(-n.StorageFlow))
                    : GameText.Get("power.hover.storage_idle");
                sb.Append('\n').Append(GameText.Format("power.hover.storage", Minutes(n.Stored), Minutes(n.StorageCapacity), flow));
            }
            sb.Append('\n').Append(GameText.Format("power.hover.members", n.Nodes, n.Consumers, n.Brownouts, n.Producers));
            if (n.Brownouts > 0)
            {
                sb.Append('\n').Append(GameText.Format("power.hover.short", Num(n.Demand - n.Delivered)));
            }
            if (n.ReserveHeld > 0)
            {
                sb.Append('\n').Append(GameText.Format("power.hover.reserve_held", n.ReserveHeld));
            }
        }

        private static string Minutes(double powerSeconds) => (powerSeconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>一座建筑的电力读数行（建造模式状态行、世界悬停、面板）。和电网无关的建筑返回 false。</summary>
        public static bool TryDescribeBuilding(CampaignState state, BuildingRecord b, out string text)
        {
            text = null;
            if (b == null || !IsPowerRelevantType(b.BuildingTypeId) || !TryGetBuildingPower(state, b.BuildingId, out BuildingPowerInfo info))
            {
                return false;
            }
            var sb = new StringBuilder(160);
            PowerEntity e = _entities[info.Entity];
            if (info.IsNode && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore)
            {
                if (!e.Conducts)
                {
                    sb.Append(GameText.Get("power.state.node_dead"));
                }
                else
                {
                    sb.Append(GameText.Format("power.state.node", SubnetName(_kernel.Subnet(info.Subnet).Serial), Num(e.NodeRadius)));
                }
            }
            else if (info.IsConsumer && !e.DemandOn)
            {
                // 关停 / 受损 / 施工中：不参与电网结算（关停写明，其余由建筑自己的状态行说明）。
                if (b.ConstructionState == BuildingConstructionState.Disabled)
                {
                    sb.Append(GameText.Get("power.panel.state_disabled"));
                }
            }
            else if (info.IsConsumer)
            {
                switch (info.Use)
                {
                    case PowerUse.Powered:
                        sb.Append(GameText.Get("power.state.powered"));
                        break;
                    case PowerUse.Brownout:
                        sb.Append(GameText.Format("power.state.brownout", b.PowerPriority));
                        break;
                    default:
                        sb.Append(GameText.Get("power.state.unconnected"));
                        break;
                }
                sb.Append(" · ").Append(GameText.Format("power.hover.priority", b.PowerPriority));
            }
            else if (info.IsStorage)
            {
                // FG4-ECO-04：储能站——存量 / 容量 · 状态（充电 / 放电 / 已满 / 已空）· 设置。
                sb.Append(DescribeStorage(state, b, info));
            }
            else if (e.SupplyOn || (info.IsProducer && e.Supply > 0f))
            {
                // FG4-ECO-04：实际送进电网的电（太阳能乘光照、燃油乘负荷；没油写明）。
                string extra = DescribeGeneratorExtra(state, b, info);
                if (e.SupplyOn || info.Subnet < 0)
                {
                    sb.Append(info.Subnet >= 0
                        ? GameText.Format("power.state.producer", SubnetName(_kernel.Subnet(info.Subnet).Serial), Num(OutputOf(state, b.BuildingId)))
                        : GameText.Get("power.state.producer_unconnected"));
                }
                if (!string.IsNullOrEmpty(extra))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(extra);
                }
            }
            else if (info.Subnet < 0)
            {
                sb.Append(GameText.Get("power.subnet.none"));
            }
            if (info.Subnet >= 0)
            {
                sb.Append('\n');
                AppendSubnetLines(sb, info.Subnet);
            }
            sb.Append('\n').Append(GameText.Format("power.hover.open_panel", InputDisplay.ForAction(GameActionId.OpenPowerGrid)));
            text = sb.ToString();
            return true;
        }

        // ── 放置预览（FGR-LOG-003“超出电力覆盖（只警告，不阻止）”；FG03 第 4 节“放置时预览电力覆盖”）──────────────

        /// <summary>
        /// 给一次放置预览补电力信息：电塔写会接入 / 连起哪些电网、覆盖多少座建筑（其中多少座现在没接入），附近没有节点时警告“孤立的电网”；
        /// 用电 / 发电建筑写会接入哪个电网，超出覆盖时警告（不阻止，FGR-LOG-003）。只在预览变化时调用（O(附近节点)，电塔另加 O(建筑)）。
        /// </summary>
        public static void AppendPlacementNotes(CampaignState state, GridPlacementResult r)
        {
            if (state == null || r == null || !r.Ok || !IsPowerRelevantType(r.TypeId))
            {
                return;
            }
            PowerKernel k = _kernel != null && ReferenceEquals(state, _state) ? _kernel : null;
            if (k == null)
            {
                Recompute(state);
                k = _kernel;
            }
            if (!GridContent.TryGetBuilding(r.TypeId, out GameConfig.fg.BuildingGrid g))
            {
                return;
            }
            GridMath.FootprintBounds(r.Pivot, g.FootprintW, g.FootprintH, r.Rotation, out GridCell min, out GridCell max);
            float radius = CoverRadiusOf(r.TypeId);
            if (radius > 0f)
            {
                float cx = (min.X + max.X) * 0.5f;
                float cy = (min.Y + max.Y) * 0.5f;
                var nets = new List<int>(4);
                k.SubnetsReachableFrom(cx, cy, radius, nets, PreviewMaxNodes);
                if (nets.Count == 0)
                {
                    r.Warnings.Add(GameText.Get("power.preview.pole_isolated"));
                }
                else
                {
                    var names = new List<string>(nets.Count);
                    foreach (int s in nets)
                    {
                        names.Add(SubnetName(k.Subnet(s).Serial));
                    }
                    string sep = GameText.Language == GameLanguage.En ? ", " : "、";
                    r.Notes.Add(nets.Count == 1 ? GameText.Format("power.preview.joins", names[0]) : GameText.Format("power.preview.links", string.Join(sep, names)));
                }
                k.CountCoverable(cx, cy, radius, out int covered, out int nowUnconnected);
                r.Notes.Add(GameText.Format("power.preview.pole_covers", covered, nowUnconnected));
                return;
            }
            int node = k.CoveringNode(min.X, min.Y, max.X, max.Y, out _);
            if (node >= 0)
            {
                r.Notes.Add(GameText.Format("power.preview.joins", SubnetName(k.Subnet(k.SubnetOf(node)).Serial)));
            }
            else
            {
                float d = k.NearestNodeDistance(min.X, min.Y, max.X, max.Y);
                r.Warnings.Add(float.IsPositiveInfinity(d)
                    ? GameText.Get("power.preview.uncovered_none")
                    : GameText.Format("power.preview.uncovered", Num(d)));
            }
        }

        // ── 拆除确认（FG00 B04）────────────────────────────────────────────────

        /// <summary>拆掉这座建筑会不会让电网断开 / 让正在用电的建筑失去连接（拆除前二次确认用）。只在玩家点拆除时调用。</summary>
        public static bool TryGetRemovalImpact(CampaignState state, string buildingId, out int parts, out int orphans, out string subnetName)
        {
            parts = 1;
            orphans = 0;
            subnetName = null;
            if (!TryGetBuildingPower(state, buildingId, out BuildingPowerInfo info) || !info.IsNode || info.Subnet < 0)
            {
                return false;
            }
            _kernel.RemovalImpact(info.Entity, out parts, out orphans);
            subnetName = SubnetName(_kernel.Subnet(info.Subnet).Serial);
            return parts > 1 || orphans > 0;
        }

        /// <summary>拆除确认框里的电力后果行（没有后果返回空列表）。</summary>
        public static void AppendRemovalLines(CampaignState state, string buildingId, List<string> into)
        {
            if (!TryGetRemovalImpact(state, buildingId, out int parts, out int orphans, out string name))
            {
                return;
            }
            if (parts > 1)
            {
                into.Add(GameText.Format("power.demolish.split", name, parts));
            }
            if (orphans > 0)
            {
                into.Add(GameText.Format("power.demolish.orphans", orphans));
            }
        }

        /// <summary>框选拆除：这批建筑一起拆掉后，按整体拓扑有几个电网会断开、几座建筑会失去电网连接（FG00 B04；FG-GAP-086）。
        /// 只在提交框选拆除时调用一次（O(受影响电网)，逐实体的部分在内核）。</summary>
        public static bool TryGetBatchRemovalImpact(CampaignState state, IReadOnlyList<string> buildingIds, out int splitGrids, out int orphans)
        {
            splitGrids = 0;
            orphans = 0;
            if (_kernel == null || !ReferenceEquals(state, _state) || buildingIds == null || buildingIds.Count == 0)
            {
                return false;
            }
            var entities = new List<int>(buildingIds.Count);
            foreach (string id in buildingIds)
            {
                if (id != null && EntityOf.TryGetValue(id, out int i) && i < _entityCount)
                {
                    entities.Add(i);
                }
            }
            _kernel.RemovalImpact(entities, out splitGrids, out orphans);
            return splitGrids > 0 || orphans > 0;
        }

        /// <summary>框选拆除确认框里的电力后果行。</summary>
        public static void AppendBatchRemovalLines(int splitGrids, int orphans, List<string> into)
        {
            if (splitGrids > 0)
            {
                into.Add(GameText.Format("power.demolish.batch_split", splitGrids));
            }
            if (orphans > 0)
            {
                into.Add(GameText.Format("power.demolish.orphans", orphans));
            }
        }

        // ── 电网列表（面板读取）───────────────────────────────────────────────

        /// <summary>按电网编号升序的电网下标。</summary>
        public static void SubnetsBySerial(List<int> into)
        {
            into.Clear();
            if (_kernel == null)
            {
                return;
            }
            for (int s = 0; s < _kernel.SubnetCount; s++)
            {
                into.Add(s);
            }
            into.Sort((a, b) => _kernel.Subnet(a).Serial.CompareTo(_kernel.Subnet(b).Serial));
        }

        /// <summary>电网 s 的用电建筑（按供电先后 = 分配顺序）。</summary>
        public static void ConsumersOf(int s, List<BuildingRecord> into)
        {
            into.Clear();
            if (_kernel == null)
            {
                return;
            }
            for (int i = 0; i < _entityCount; i++)
            {
                // 用电建筑（含关停的：面板上能重新启用）；受损 / 施工中的不列。
                if (_recordAt[i] != null && _kernel.SubnetOf(i) == s && HomeValleyLayout.PowerProfile.ContainsKey(_recordAt[i].BuildingTypeId)
                    && (_entities[i].DemandOn || _recordAt[i].ConstructionState == BuildingConstructionState.Disabled))
                {
                    into.Add(_recordAt[i]);
                }
            }
        }

        /// <summary>运转中却不在任何覆盖里的用电 / 发电建筑。</summary>
        public static void UnconnectedBuildings(List<BuildingRecord> into)
        {
            into.Clear();
            if (_kernel == null)
            {
                return;
            }
            for (int i = 0; i < _entityCount; i++)
            {
                if (_recordAt[i] != null && _kernel.SubnetOf(i) < 0 && (_entities[i].DemandOn || (_entities[i].SupplyOn && _entities[i].NodeRadius <= 0f)))
                {
                    into.Add(_recordAt[i]);
                }
            }
        }

        /// <summary>电网 s 的定位点（它的第一个节点的中心）。</summary>
        public static Vector2 SubnetAnchorPosition(int s)
        {
            int node = _kernel.Subnet(s).FirstNode;
            if (node < 0)
            {
                return Vector2.zero;
            }
            PowerEntity e = _entities[node];
            return new Vector2((e.MinX + e.MaxX) * 0.5f, (e.MinY + e.MaxY) * 0.5f);
        }

        /// <summary>第 i 个内核实体的中心（叠加层）。</summary>
        public static Vector2 EntityCenter(int i)
        {
            PowerEntity e = _entities[i];
            return new Vector2((e.MinX + e.MaxX) * 0.5f, (e.MinY + e.MaxY) * 0.5f);
        }

        public static PowerEntity EntityAt(int i) => _entities[i];

        /// <summary>性能自检：忘掉上一次的分配顺序，下一次重算走整体重排（模拟首次绑定 / 读档）。</summary>
        public static void ForgetOrderForTests()
        {
            _orderSourceArray = null;
            _orderCache.Clear();
            _orderCachePriorities.Clear();
        }

        /// <summary>自检之间复位（不写回任何战役）。</summary>
        public static void ResetForTests()
        {
            _kernel = null;
            _state = null;
            _summaryValid = false;
            SummaryVersion++;
            _orderSourceArray = null;
            _orderCache.Clear();
            _orderCachePriorities.Clear();
            _entityCount = 0;
            EntityOf.Clear();
            KeyOf.Clear();
            IdOfKey.Clear();
            _preserveSaved = false;
            _legacyCheckPending = false;
            _suppressFeedback = false;
            LastSplitNotices = 0;
            LastUnconnectedNotices = 0;
            LastStorageEmptyNotices = 0;
            EmptyNotified.Clear();
            _lastEnergyCount = 0;
            OverrideNodesForTests(null);
            _sourceRevision = -1;
        }
    }
}
