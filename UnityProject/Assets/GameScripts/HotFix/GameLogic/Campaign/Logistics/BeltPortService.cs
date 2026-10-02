using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>
    /// FG3-LOG-03（FG03 FGR-LOG-021“传送带朝向建筑的输入端口时把物品送进建筑；建筑的输出端口把物品推上传送带；仓库有输入和输出端口，输出端口可以设置过滤器”）：
    /// 真实建筑的端口 ↔ 传送带内核端口。唯一写入口（绑定表 <see cref="BeltItemState.PortBindings"/>、库存与端口之间的物品转移）。
    ///
    /// - 对账（<see cref="Sync"/>）：每 logistics.port.sync_seconds 游戏秒（按世界步序号取整，与观察无关）一次，O(建筑数 × 每座端口数)：
    ///   运转中的家园建筑按 fg.TbBuildingPort（随建筑旋转）登记内核端口——输入口在建筑格上、只接“末端正对着它”的带；输出口推到端口外侧那一格。
    ///   建筑建成 / 修好 → 接上；受损 / 关停 / 被拆 → 删端口，端口手里的物品（待推、缓存）退回仓库（放不下落地 + 搬运单，不消失）；转向 / 搬迁 → 删了在新位置重新登记。
    /// - 转移（<see cref="Pump"/>）：内核每走一步之后一次，O(端口数)（与传送带格数、物品数无关）。
    ///   role=store 的输入口：缓存存进家园仓库（放得下多少存多少；满了缓存不收、带停下，原因写“仓库满了”）；
    ///   role=store 的输出口：按过滤器从库存取出最多 logistics.port.source_buffer 件交给端口待推（在途量），推不上去就等着；过滤改变或建筑停用时退回。
    ///   role=none：建筑还没有收发物品的配方（FG4-ECO），输入口什么都不收（带停下，原因写明）、输出口不推。
    /// - 守恒：家园库存 + 端口待推 + 端口缓存 + 带上物品 = 常数（除了玩家的清带丢弃与拆除返还），自检长跑核对。
    /// 不做玩家没要求的事（FGR-BASE-020）：只有玩家把带接到端口上才有物品流动；输出过滤由玩家在端口面板里设（默认“全部可存物品”）。
    /// </summary>
    public static class BeltPortService
    {
        /// <summary>建筑端口的内核端口号从这里起分配（小于它的端口号留给测试 / 旧存档里的手工端口，互不冲突）。</summary>
        public const int PortIdBase = 1000000;

        /// <summary>过滤：全部可存物品 / 停止输出。</summary>
        public const int FilterAll = -1;
        public const int FilterOff = 0;

        /// <summary>一个已绑定的建筑端口（运行时索引；真相在 <see cref="BeltPortBindingRecord"/> 与内核端口块）。</summary>
        public sealed class Binding
        {
            public int PortId;
            public string BuildingId;
            public string PortKey;
            public bool IsOutput;
            public bool Store;
            /// <summary>FG4-ECO-02：生产建筑自己的输入 / 输出缓存（role = prod，转移由 <see cref="Economy.ProductionService.PumpPort"/> 做）。</summary>
            public bool Prod;
            /// <summary>端口所在的建筑格。</summary>
            public GridCell PortCell;
            /// <summary>端口朝外的方向（随建筑旋转）。</summary>
            public GridDir Face;
            /// <summary>端口外侧那一格（输出口推到这里；输入口的来料带在这里）。</summary>
            public GridCell BeltCell;
            public BeltPortBindingRecord Record;
        }

        /// <summary>端口面板的一行（界面只读）。</summary>
        public sealed class PortView
        {
            public string PortKey;
            public bool IsOutput;
            public bool Store;
            public bool Prod;
            public GridDir Face;
            public GridCell PortCell;
            public GridCell BeltCell;
            /// <summary>建筑在运转（端口启用）。</summary>
            public bool Active;
            public bool Bound;
            public bool Connected;
            public int PortId = -1;
            public int Filter = FilterAll;
            /// <summary>FG4-ECO-05（FG-GAP-093）：仓库输出口“每种至少留 N 件”。</summary>
            public int Keep;
            public BeltPortInfo Info;
            public string Title;
            public string StateLine;
            public string StatsLine;
            public string AcceptLine;
            /// <summary>问题与办法（没接带 / 仓库满 / 没货 / 推不上去 / 建筑不收），空 = 没问题。</summary>
            public string IssueLine;
        }

        private static CampaignState _state;
        private static BeltPortBindingRecord[] _indexed;
        private static readonly List<Binding> Bindings = new List<Binding>(16);
        private static readonly Dictionary<int, Binding> ById = new Dictionary<int, Binding>();
        private static readonly Dictionary<string, Binding> ByKey = new Dictionary<string, Binding>(StringComparer.Ordinal);
        private static readonly Dictionary<GridCell, Binding> SourceByBeltCell = new Dictionary<GridCell, Binding>();
        private static readonly List<Binding> Scratch = new List<Binding>(16);
        private static readonly HashSet<string> DesiredKeys = new HashSet<string>(StringComparer.Ordinal);
        private static bool _connectedHookChecked;
        private static bool _acceptMigrated;
        private static bool _outputBlockedHookChecked;
        private static ulong _lastSignature;
        private static int _lastBindingCount = -1;
        private static bool _retryPending;

        /// <summary>绑定变化（接上 / 断开 / 过滤）时 +1：端口面板与悬停按它刷新。</summary>
        public static int Revision { get; private set; }
        /// <summary>诊断：对账次数与最近一次新增 / 删除的端口数；转移次数。</summary>
        public static int SyncCount { get; private set; }
        public static int LastSyncAdded { get; private set; }
        public static int LastSyncRemoved { get; private set; }
        public static long PumpCount { get; private set; }
        /// <summary>最近一次 <see cref="Pump"/> 的耗时（毫秒，诊断 / 性能自检）。</summary>
        public static double LastPumpMs { get; private set; }
        public static int Count => Bindings.Count;
        public static IReadOnlyList<Binding> All => Bindings;

        private static int SinkBuffer => Math.Max(1, GridContent.TuningInt("logistics.port.sink_buffer"));
        private static int SourceBuffer => Math.Max(1, GridContent.TuningInt("logistics.port.source_buffer"));
        private static int SourceInterval => Math.Max(1, GridContent.TuningInt("logistics.port.source_interval_steps"));

        // ── 生命周期（BeltNetworkService.Load / Unload 调用）──────────────────────────

        /// <summary>读档 / 接入战役：按存档里的绑定表重建索引（内核里已经不存在的端口——坏块丢弃——解绑，下一次对账重新登记）。不在这里对账：
        /// 对账只在固定的世界步上发生，读档后与“不存档一直跑”逐步一致。</summary>
        public static void OnLoad(CampaignState state)
        {
            _state = state;
            _connectedHookChecked = false;
            _outputBlockedHookChecked = false;
            _lastSignature = 0;
            _lastBindingCount = -1;
            _retryPending = false;
            if (state?.Belts == null)
            {
                Clear();
                return;
            }
            BeltPortBindingRecord[] recs = state.Belts.PortBindings ?? Array.Empty<BeltPortBindingRecord>();
            var keep = new List<BeltPortBindingRecord>(recs.Length);
            foreach (BeltPortBindingRecord r in recs)
            {
                if (r != null && BeltNetworkService.IsRunning && BeltNetworkService.Kernel.TryGetPortCounts(r.PortId, out _, out _))
                {
                    keep.Add(r);
                }
            }
            if (keep.Count != recs.Length)
            {
                state.Belts.PortBindings = keep.ToArray();
            }
            RebuildIndex(state);
        }

        public static void OnUnload()
        {
            _state = null;
            Clear();
        }

        private static void Clear()
        {
            _indexed = null;
            Bindings.Clear();
            ById.Clear();
            ByKey.Clear();
            SourceByBeltCell.Clear();
            Revision++;
        }

        private static string KeyOf(string buildingId, string portKey) => buildingId + "|" + portKey;

        /// <summary>按存档里的绑定表重建运行时索引（只在绑定表换了时，O(端口数)）。</summary>
        private static void RebuildIndex(CampaignState state)
        {
            _acceptMigrated = false;
            Bindings.Clear();
            ById.Clear();
            ByKey.Clear();
            SourceByBeltCell.Clear();
            _indexed = state?.Belts?.PortBindings;
            if (_indexed == null)
            {
                Revision++;
                return;
            }
            foreach (BeltPortBindingRecord r in _indexed)
            {
                BuildingRecord b = r != null ? HomeGridService.FindBuilding(state, r.BuildingId) : null;
                BuildingPort row = FindRow(b?.BuildingTypeId, r?.PortKey);
                if (b == null || row == null)
                {
                    // 建筑已经不在（例如读档前刚被拆）：保留记录，下一次对账会把它解绑并退回物品。
                    if (r != null)
                    {
                        var orphan = new Binding { PortId = r.PortId, BuildingId = r.BuildingId, PortKey = r.PortKey, Record = r };
                        Bindings.Add(orphan);
                        ById[r.PortId] = orphan;
                        ByKey[KeyOf(r.BuildingId, r.PortKey)] = orphan;
                    }
                    continue;
                }
                Binding bind = Make(b, row, r);
                Bindings.Add(bind);
                ById[bind.PortId] = bind;
                ByKey[KeyOf(bind.BuildingId, bind.PortKey)] = bind;
                if (bind.IsOutput)
                {
                    SourceByBeltCell[bind.BeltCell] = bind;
                }
            }
            Bindings.Sort((a, c) => a.PortId.CompareTo(c.PortId));
            Revision++;
        }

        private static BuildingPort FindRow(string typeId, string portKey)
        {
            if (typeId == null || portKey == null)
            {
                return null;
            }
            foreach (BuildingPort p in GridContent.PortsOf(typeId))
            {
                if (p.Id == portKey)
                {
                    return p;
                }
            }
            return null;
        }

        private static Binding Make(BuildingRecord b, BuildingPort row, BeltPortBindingRecord rec)
        {
            int rot = GridMath.NormalizeRotation(b.Rotation);
            GridMath.TryParseDir(row.Dir, out GridDir dir);
            GridDir face = GridMath.RotateDir(dir, rot);
            GridCell cell = GridMath.PortCell(new GridCell(b.GridX, b.GridY), row.LocalX, row.LocalY, rot);
            Vector2Int v = GridMath.DirVector(face);
            return new Binding
            {
                PortId = rec?.PortId ?? -1,
                BuildingId = b.BuildingId,
                PortKey = row.Id,
                IsOutput = row.Kind == "out",
                Store = row.Role == "store",
                Prod = row.Role == "prod",
                PortCell = cell,
                Face = face,
                BeltCell = new GridCell(cell.X + v.x, cell.Y + v.y),
                Record = rec,
            };
        }

        /// <summary>这座建筑的端口现在该不该启用：家园里运转中的建筑（虚影、搬迁目标、受损、关停的都不启用）。</summary>
        public static bool IsActive(BuildingRecord b) =>
            b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational
            && !HomeGridService.IsRelocationGhost(b) && GridContent.TryGetBuilding(b.BuildingTypeId, out _);

        private static void EnsureIndex(CampaignState state)
        {
            if (!ReferenceEquals(state, _state) || !ReferenceEquals(state?.Belts?.PortBindings, _indexed))
            {
                _state = state;
                RebuildIndex(state);
            }
        }

        // ── 每个世界步（BeltNetworkService.WorldStep 在内核步之后调用）──────────────────

        /// <summary>对账间隔（世界步）。</summary>
        public static int SyncTicks(int worldHz) =>
            Math.Max(1, Mathf.RoundToInt(GridContent.Tuning("logistics.port.sync_seconds") * Math.Max(1, worldHz)));

        /// <summary>世界步末：到了对账的步就对账；内核这一步走过就做一次转移。只看步序号，与镜头 / 帧率 / 倍速无关。</summary>
        public static void Step(CampaignState state, long ticksAfter, int worldHz, bool kernelStepped)
        {
            if (state == null || !BeltNetworkService.IsRunning || BeltNetworkService.SavedDataPreserved)
            {
                return;
            }
            if (ticksAfter % SyncTicks(worldHz) == 0)
            {
                Sync(state);
                CheckConnectedHook();
                CheckOutputBlockedHook();
            }
            if (kernelStepped)
            {
                Pump(state);
            }
        }

        /// <summary>
        /// 对账：运转中的家园建筑按端口表登记内核端口；不该启用的端口删掉并把物品退回仓库；位置 / 朝向变了的重新登记（过滤保留）。
        /// O(建筑数 × 每座端口数)，每游戏秒一次。
        /// </summary>
        public static void Sync(CampaignState state)
        {
            EnsureIndex(state);
            // 稳态零分配：建筑（ID、状态、位置、朝向）与绑定都没变、上次也没有登记失败的端口时直接返回（O(建筑数) 的整数混合，不分配）。
            ulong sig = Signature(state);
            if (sig == _lastSignature && !_retryPending && Bindings.Count == _lastBindingCount && ReferenceEquals(state.Belts.PortBindings, _indexed))
            {
                return;
            }
            SyncCount++;
            _retryPending = false;
            int added = 0;
            int removed = 0;
            DesiredKeys.Clear();
            BuildingRecord[] records = state.BuildingRecords ?? Array.Empty<BuildingRecord>();
            // 1) 该启用的端口：已绑定且位置没变 → 留着；位置变了 → 先删（物品退回）再在新位置登记。
            foreach (BuildingRecord b in records)
            {
                if (!IsActive(b))
                {
                    continue;
                }
                foreach (BuildingPort row in GridContent.PortsOf(b.BuildingTypeId))
                {
                    string key = KeyOf(b.BuildingId, row.Id);
                    DesiredKeys.Add(key);
                    Binding want = Make(b, row, null);
                    if (ByKey.TryGetValue(key, out Binding have))
                    {
                        if (have.PortCell == want.PortCell && have.Face == want.Face && have.IsOutput == want.IsOutput && have.Store == want.Store && have.Prod == want.Prod)
                        {
                            continue;
                        }
                        int filter = have.Record?.Filter ?? FilterAll;
                        Unbind(state, have, b.Position);
                        removed++;
                        if (Bind(state, b, row, filter))
                        {
                            added++;
                        }
                        continue;
                    }
                    if (Bind(state, b, row, FilterAll))
                    {
                        added++;
                    }
                }
            }
            // 2) 不该启用的（建筑不在 / 不运转 / 表里没有这个端口了）：删掉，物品退回。
            Scratch.Clear();
            foreach (Binding bind in Bindings)
            {
                if (!DesiredKeys.Contains(KeyOf(bind.BuildingId, bind.PortKey)))
                {
                    Scratch.Add(bind);
                }
            }
            foreach (Binding bind in Scratch)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, bind.BuildingId);
                Unbind(state, bind, b?.Position ?? new Vector2(bind.PortCell.X, bind.PortCell.Y));
                removed++;
            }
            LastSyncAdded = added;
            LastSyncRemoved = removed;
            if (added > 0 || removed > 0)
            {
                Revision++;
            }
            _lastSignature = Signature(state);
            _lastBindingCount = Bindings.Count;
        }

        /// <summary>第一次有端口接上传送带时发引导钩子（每个对账步查一次，钩子发过后不再查；不分配）。</summary>
        private static void CheckConnectedHook()
        {
            if (_connectedHookChecked || !BeltNetworkService.IsRunning)
            {
                return;
            }
            foreach (Binding bind in Bindings)
            {
                if (BeltNetworkService.Kernel.TryGetPortInfo(bind.PortId, out BeltPortInfo info) && info.Connected)
                {
                    _connectedHookChecked = true;
                    GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstPortConnected);
                    break;
                }
            }
        }

        /// <summary>
        /// FG3-LOG-08（卡片“第一次出现堵塞时的引导”）：建筑的输出口第一次推不出去（端口有待推物品、外侧那格传送带堵着）时发引导钩子。
        /// 每个对账步查一次 O(端口数)；钩子发过（本机设置里记着）后不再查。引导内容在 FG15-UX-04。
        /// </summary>
        private static void CheckOutputBlockedHook()
        {
            if (_outputBlockedHookChecked || !BeltNetworkService.IsRunning)
            {
                return;
            }
            if (GameLogic.Settings.GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstOutputBlocked))
            {
                _outputBlockedHookChecked = true;
                return;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            foreach (Binding bind in Bindings)
            {
                if (bind.IsOutput && k.TryGetPortInfo(bind.PortId, out BeltPortInfo info) && info.Connected && info.Pending > 0
                    && k.TryGetCellInfo(bind.BeltCell.X, bind.BeltCell.Y, out BeltCellInfo head) && head.Block != BeltBlock.None && head.Count > 0)
                {
                    _outputBlockedHookChecked = true;
                    GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstOutputBlocked);
                    break;
                }
            }
        }

        /// <summary>建筑与端口相关的状态指纹（ID、类型、区域、施工状态、枢轴格、朝向），O(建筑数)、不分配。只在进程内比较（字符串哈希不跨进程）。</summary>
        private static ulong Signature(CampaignState state)
        {
            ulong h = 14695981039346656037UL;
            BuildingRecord[] records = state?.BuildingRecords;
            if (records == null)
            {
                return h;
            }
            for (int i = 0; i < records.Length; i++)
            {
                BuildingRecord b = records[i];
                if (b == null)
                {
                    continue;
                }
                h = Mix(h, (ulong)(uint)(b.BuildingId?.GetHashCode() ?? 0));
                h = Mix(h, (ulong)(uint)(b.BuildingTypeId?.GetHashCode() ?? 0));
                h = Mix(h, (ulong)(uint)(b.RegionId?.GetHashCode() ?? 0));
                h = Mix(h, (ulong)(uint)b.ConstructionState);
                h = Mix(h, (ulong)(uint)b.GridX);
                h = Mix(h, (ulong)(uint)b.GridY);
                h = Mix(h, (ulong)(uint)GridMath.NormalizeRotation(b.Rotation));
                h = Mix(h, (ulong)(uint)(b.RelocateFromId?.GetHashCode() ?? 0));
            }
            return Mix(h, (ulong)(uint)records.Length);
        }

        private static ulong Mix(ulong h, ulong v)
        {
            h ^= v;
            return h * 1099511628211UL;
        }

        private static int AllocateId(CampaignState state)
        {
            int id = Math.Max(PortIdBase, state.Belts.NextPortId);
            while (BeltNetworkService.Kernel.TryGetPortCounts(id, out _, out _) || ById.ContainsKey(id))
            {
                id++;
            }
            state.Belts.NextPortId = id + 1;
            return id;
        }

        private static bool Bind(CampaignState state, BuildingRecord b, BuildingPort row, int filter)
        {
            Binding bind = Make(b, row, null);
            int id = AllocateId(state);
            BeltOpResult r;
            byte face = (byte)bind.Face;
            if (bind.IsOutput)
            {
                ushort item = bind.Store ? ItemForFilter(filter) : bind.Prod ? Economy.ProductionService.SourceItemFor(state, b) : (ushort)0;
                r = BeltNetworkService.TryAddSource(state, id, bind.BeltCell, item, SourceInterval, 0, 0, face);
            }
            else
            {
                string nameKey = FgContentTables.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.Building brow) ? brow.NameKey : null;
                // FG4-ECO-01：家园仓库 / 核心的输入口收全部可存物品（缓存一次只放一种，入库时按种类；关闭 DEBT-FG3LOG03-02）。
                // FG4-ECO-02：生产建筑的输入口只收当前配方要的那种固体（回收站收任何固体，一次一种；没选配方 = 不收）。
                // FG4-ECO-03：多输入口的生产建筑按口分配配方里的固体；装配站的输入口收机器材料（收货集合）。
                ushort accept = bind.Store ? BeltConst.AcceptAnyOneKind : bind.Prod ? Economy.ProductionService.SinkAcceptFor(state, b, row.Id) : BeltConst.AcceptNone;
                r = BeltNetworkService.TryAddSink(state, id, bind.PortCell, SinkBuffer, 0, nameKey, 0, face, accept);
            }
            if (!r.Ok)
            {
                // 登记不上（例如两座建筑的输出口推到同一格）：不绑定，端口面板写“没接上”；下一次对账再试。
                _retryPending = true;
                return false;
            }
            bind.PortId = id;
            bind.Record = new BeltPortBindingRecord { PortId = id, BuildingId = b.BuildingId, PortKey = row.Id, Filter = filter };
            BeltPortBindingRecord[] old = state.Belts.PortBindings ?? Array.Empty<BeltPortBindingRecord>();
            var next = new BeltPortBindingRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = bind.Record;
            state.Belts.PortBindings = next;
            _indexed = next;
            // 按端口号升序插入（转移按这个顺序处理；与读档后按端口号重建的索引同一顺序，存读档前后逐步一致）。
            int at = Bindings.Count;
            while (at > 0 && Bindings[at - 1].PortId > id)
            {
                at--;
            }
            Bindings.Insert(at, bind);
            ById[id] = bind;
            ByKey[KeyOf(bind.BuildingId, bind.PortKey)] = bind;
            if (bind.IsOutput)
            {
                SourceByBeltCell[bind.BeltCell] = bind;
            }
            Revision++;
            return true;
        }

        /// <summary>删掉一个建筑端口：内核端口删掉，端口手里的物品（输出口待推、输入口缓存）退回仓库（放不下落地 + 搬运单）。</summary>
        private static void Unbind(CampaignState state, Binding bind, Vector2 at)
        {
            ushort item = 0;
            if (BeltNetworkService.Kernel.TryGetPortInfo(bind.PortId, out BeltPortInfo info))
            {
                item = bind.IsOutput || info.Accept == BeltConst.AcceptAnyOneKind || BeltConst.IsSetAccept(info.Accept)
                    ? info.ItemType
                    : (info.Accept != BeltConst.AcceptAny && info.Accept != BeltConst.AcceptNone ? info.Accept : BeltItems.ScrapId);
                if (item == 0)
                {
                    item = BeltItems.ScrapId;
                }
            }
            BeltNetworkService.TryRemovePort(state, bind.PortId, out int pending, out int buffered);
            int back = pending + buffered;
            if (back > 0 && item != 0)
            {
                HomeValleyConstruction.ReturnMaterials(state, at, BeltItems.ResourceOf(item), back,
                    "port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            BeltPortBindingRecord[] old = state.Belts.PortBindings ?? Array.Empty<BeltPortBindingRecord>();
            var list = new List<BeltPortBindingRecord>(old.Length);
            foreach (BeltPortBindingRecord r in old)
            {
                if (r != null && r.PortId != bind.PortId)
                {
                    list.Add(r);
                }
            }
            state.Belts.PortBindings = list.ToArray();
            _indexed = state.Belts.PortBindings;
            Bindings.Remove(bind);
            ById.Remove(bind.PortId);
            ByKey.Remove(KeyOf(bind.BuildingId, bind.PortKey));
            if (bind.IsOutput && SourceByBeltCell.TryGetValue(bind.BeltCell, out Binding s) && ReferenceEquals(s, bind))
            {
                SourceByBeltCell.Remove(bind.BeltCell);
            }
            Revision++;
        }

        /// <summary>过滤 → 输出口推哪种物品（指定物品 = 那一种；“全部”时登记初值为废料，之后由 <see cref="Pump"/> 按库存轮转）。</summary>
        public static ushort ItemForFilter(int filter)
        {
            if (filter > 0 && filter < ushort.MaxValue)
            {
                return (ushort)filter;
            }
            return BeltItems.ScrapId;
        }

        /// <summary>
        /// 转移：仓库输入口的缓存按种类存进家园仓库；仓库输出口按过滤从库存补足待推数。O(端口数)，内核每走一步之后一次。
        /// 按端口号升序处理（同一状态同一结果）。FG4-ECO-01：
        /// - 输入口缓存一次只放一种（内核 <see cref="BeltConst.AcceptAnyOneKind"/>），按那一种的剩余空间入库；放不下就留在缓存里，带停下（原因“下游已满”）。
        /// - 输出口过滤“全部可存物品”：上一批推完（待推为 0）时轮到下一种有库存的物品（按物品表顺序），一次只推一种；指定物品时只推那一种。
        /// </summary>
        public static void Pump(CampaignState state)
        {
            EnsureIndex(state);
            if (Bindings.Count == 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeltKernel k = BeltNetworkService.Kernel;
            if (!_acceptMigrated)
            {
                MigrateStoreAccept(k);
            }
            int sourceBuffer = SourceBuffer;
            // 仓库是否运转只查一次（O(建筑数)），剩余空间按当前库存现算——整次转移 O(端口数)。
            int warehouse = -1;
            foreach (Binding bind in Bindings)
            {
                if ((!bind.Store && !bind.Prod) || bind.Record == null || !k.TryGetPortCounts(bind.PortId, out int pending, out int buffered, out ushort kind))
                {
                    continue;
                }
                if (bind.Prod)
                {
                    Economy.ProductionService.PumpPort(state, bind, k, pending, buffered, kind, sourceBuffer);
                    continue;
                }
                if (bind.IsOutput)
                {
                    int filter = bind.Record.Filter;
                    if (filter == FilterOff || pending >= sourceBuffer)
                    {
                        continue;
                    }
                    int keep = Math.Max(0, bind.Record.Keep); // FG4-ECO-05（FG-GAP-093）：每种在家园库存里至少留 keep 件。
                    ushort item = filter > 0 ? ItemForFilter(filter) : kind;
                    if (filter == FilterAll)
                    {
                        // 一批（logistics.port.source_buffer 件）推完才补下一批、并换到下一种有库存的物品：否则一直有库存的那一种（推出去又转回仓库）会永远占着输出口。
                        // 内核每步最多推一件、推完的那一步之后就补，入口不会空等（吞吐与只推一种时相同）。
                        if (pending > 0)
                        {
                            continue;
                        }
                        ushort next = NextStocked(state, kind, keep);
                        if (next != 0 && next != kind)
                        {
                            k.SetSourceItem(bind.PortId, next);
                        }
                        item = next != 0 ? next : kind;
                    }
                    Economy.ItemDef def = BeltItems.Def(item);
                    if (!Economy.HomeInventory.IsBeltStorable(def))
                    {
                        continue;
                    }
                    int take = Economy.HomeInventory.RemoveUpTo(state, def, Math.Min(sourceBuffer - Math.Max(0, pending),
                        Math.Max(0, Economy.HomeInventory.Stock(state, def) - keep)));
                    if (take > 0)
                    {
                        k.AddSourceItems(bind.PortId, take);
                    }
                }
                else if (buffered > 0)
                {
                    Economy.ItemDef def = BeltItems.Def(kind != 0 ? kind : BeltItems.ScrapId);
                    if (!Economy.HomeInventory.IsBeltStorable(def))
                    {
                        // 物品表里没有的编号（旧存档 / 表删掉了某种物品）：放到这座建筑旁边的地上（按编号落地，不消失），输入口不被它堵死。
                        int n0 = k.TakeFromSink(bind.PortId, buffered);
                        if (n0 > 0)
                        {
                            BuildingRecord owner = HomeGridService.FindBuilding(state, bind.BuildingId);
                            HomeValleyConstruction.ReturnMaterials(state, owner?.Position ?? new Vector2(bind.PortCell.X, bind.PortCell.Y), BeltItems.ResourceOf(kind), n0,
                                "port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":unknown:" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                        }
                        continue;
                    }
                    if (warehouse < 0)
                    {
                        warehouse = Economy.HomeInventory.WarehouseOperational(state) ? 1 : 0;
                    }
                    int n = Math.Min(buffered, Economy.HomeInventory.Space(state, def, warehouse == 1));
                    if (n > 0)
                    {
                        Economy.HomeInventory.Add(state, def, k.TakeFromSink(bind.PortId, n), clampToSpace: false);
                    }
                }
            }
            PumpCount++;
            LastPumpMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>“全部可存物品”的下一种：按物品表顺序，从 <paramref name="after"/> 的下一种开始找第一种有库存的（转一圈都没有返回 0）。O(物品种类)，只在一批推完时调用。</summary>
        private static ushort NextStocked(CampaignState state, ushort after, int keep = 0)
        {
            IReadOnlyList<Economy.ItemDef> items = Economy.ItemCatalog.Items;
            int n = items.Count;
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (items[i].BeltId == after)
                {
                    start = i + 1;
                    break;
                }
            }
            for (int step = 0; step < n; step++)
            {
                Economy.ItemDef d = items[(start + step) % n];
                // FG5-RND-02（ADR-RND-002）：敌方物品与残骸不进“全部可存物品”的轮转——它们只该去解析台 / 回收站，混进普通产线会堵带；
                // 玩家要用传送带把它们送出去时，把输出口过滤设成那一种（照常推）。
                if (Economy.HomeInventory.IsBeltStorable(d) && Economy.HomeInventory.Stock(state, d) > keep && !Economy.AnalysisCatalog.TryGetKind(d.Id, out _))
                {
                    return d.BeltId;
                }
            }
            return 0;
        }

        /// <summary>输出口按过滤此刻有没有可推的库存（“全部”= 任何一种可存物品都没有）。</summary>
        public static bool OutputStockEmpty(CampaignState state, int filter, int keep = 0) =>
            filter == FilterAll ? NextStocked(state, 0, keep) == 0 : BeltItems.Stock(state, ItemForFilter(filter)) <= keep;

        /// <summary>
        /// FG4-ECO-05（FG-GAP-093）：设置仓库输出口“保留 N 件”——每种物品在家园库存里至少留 N 件，多出来的才推上传送带（0 = 不保留）。
        /// 立刻生效（下一步起按新数补货；已经推上带的不收回）。返回是否改了；不能改时写明原因。
        /// </summary>
        public static bool TrySetKeep(CampaignState state, string buildingId, string portKey, int keep, out string reason)
        {
            reason = null;
            EnsureIndex(state);
            if (!ByKey.TryGetValue(KeyOf(buildingId, portKey), out Binding bind) || bind.Record == null)
            {
                reason = GameText.Get("logistics.reason.port_not_found");
                return false;
            }
            if (!bind.IsOutput || !bind.Store)
            {
                reason = GameText.Get("logistics.port.role_none");
                return false;
            }
            keep = Math.Max(0, keep);
            if (bind.Record.Keep == keep)
            {
                return false;
            }
            bind.Record.Keep = keep;
            Revision++;
            return true;
        }

        /// <summary>“保留 N 件”下拉的预设（logistics.port.keep.1～6；0 = 不保留排第一）。</summary>
        public static void KeepPresets(List<int> into)
        {
            into.Clear();
            into.Add(0);
            for (int i = 1; i <= 6; i++)
            {
                if (GridContent.TryGetTuning("logistics.port.keep." + i.ToString(CultureInfo.InvariantCulture), out float v) && v > 0f && !into.Contains(Mathf.RoundToInt(v)))
                {
                    into.Add(Mathf.RoundToInt(v));
                }
            }
        }

        public static string KeepName(int keep) => keep <= 0 ? GameText.Get("logistics.port.keep_none") : GameText.Format("logistics.port.keep_n", keep);

        /// <summary>FG4-ECO-01 读档迁移：FG3-LOG-03 起的存档里仓库输入口“只收废料”，改成“全部可存物品（一次一种）”；缓存里已有的件就是废料（内核迁移时记下）。
        /// FG4-ECO-03：装配站输入口从“什么都不收”改成收机器材料。
        /// 每次重建索引后做一次，O(端口数)。</summary>
        private static void MigrateStoreAccept(BeltKernel k)
        {
            _acceptMigrated = true;
            foreach (Binding bind in Bindings)
            {
                if (bind.Store && !bind.IsOutput && bind.PortId >= 0 && k.TryGetPortInfo(bind.PortId, out BeltPortInfo info) && info.Accept != BeltConst.AcceptAnyOneKind)
                {
                    k.SetSinkAccept(bind.PortId, BeltConst.AcceptAnyOneKind);
                }
                // FG4-ECO-03 读档迁移：FG3-LOG-03 起的存档里装配站输入口“什么都不收”，改成收机器材料（收货集合）。
                else if (bind.Prod && !bind.IsOutput && bind.PortId >= 0 && bind.PortKey != null
                         && bind.PortKey.StartsWith(HomeValleyLayout.BuildingTypeAssemblyStation + ".", StringComparison.Ordinal)
                         && k.TryGetPortInfo(bind.PortId, out BeltPortInfo ai) && ai.Accept != BeltConst.AcceptSet)
                {
                    k.SetSinkAccept(bind.PortId, BeltConst.AcceptSet);
                }
                // FG5-RND-02 读档迁移：FG3-LOG-03 起的存档里解析台输入口“什么都不收”，改成收敌方物品与残骸（第二个收货集合）。
                else if (bind.Prod && !bind.IsOutput && bind.PortId >= 0 && HomeValleyAnalysis.IsBenchPort(bind.PortKey)
                         && k.TryGetPortInfo(bind.PortId, out BeltPortInfo bi) && bi.Accept != BeltConst.AcceptSet2)
                {
                    k.SetSinkAccept(bind.PortId, BeltConst.AcceptSet2);
                }
            }
        }

        // ── 玩家操作（端口面板）──────────────────────────────────────────────────

        /// <summary>
        /// 设置仓库输出口的过滤（FGR-LOG-021）：-1 全部可存物品 / 0 停止输出 / 物品编号。端口手里还没推上带的旧物品退回仓库；
        /// 立刻生效（下一步起按新过滤补货）。返回是否改了；不能改时 <paramref name="reason"/> 写明原因。
        /// </summary>
        public static bool TrySetFilter(CampaignState state, string buildingId, string portKey, int filter, out string reason)
        {
            reason = null;
            EnsureIndex(state);
            if (!ByKey.TryGetValue(KeyOf(buildingId, portKey), out Binding bind) || bind.Record == null)
            {
                reason = GameText.Get("logistics.reason.port_not_found");
                return false;
            }
            if (!bind.IsOutput || !bind.Store)
            {
                reason = GameText.Get("logistics.port.role_none");
                return false;
            }
            if (filter > 0 && !BeltItems.IsStorable((ushort)Math.Min(filter, ushort.MaxValue - 1)))
            {
                reason = GameText.Get("ui.build.clear_confirm_reason_kind");
                return false;
            }
            if (bind.Record.Filter == filter)
            {
                return false;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            int back = k.TakeSourcePending(bind.PortId);
            if (k.TryGetPortInfo(bind.PortId, out BeltPortInfo info) && back > 0)
            {
                BuildingRecord b = HomeGridService.FindBuilding(state, bind.BuildingId);
                HomeValleyConstruction.ReturnMaterials(state, b?.Position ?? new Vector2(bind.PortCell.X, bind.PortCell.Y), BeltItems.ResourceOf(info.ItemType), back,
                    "port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":filter:" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            k.SetSourceItem(bind.PortId, ItemForFilter(filter));
            bind.Record.Filter = filter;
            Revision++;
            return true;
        }

        public static string FilterName(int filter) =>
            filter == FilterOff ? GameText.Get("logistics.port.filter_off")
            : filter == FilterAll ? GameText.Get("logistics.port.filter_all")
            : GameText.Format("logistics.port.filter_item", BeltItems.Name((ushort)Math.Min(filter, ushort.MaxValue - 1)));

        // ── 查询（悬停、端口面板、堵塞原因）───────────────────────────────────────

        private static readonly HashSet<int> NetScratch = new HashSet<int>();
        private static readonly int[] KindScratch = new int[1024];

        /// <summary>
        /// FG3-E2E-01（M3 出口旅程抓到）：家园仓库（store）输出口推上传送带、此刻还在带上（或已从库存取出等着推上）的件数——在途不算库存。
        /// 施工“等待材料”的原因行据此写明“另有 N 件在传送带上”与办法。按输出口所在的传送带网络去重求和。
        /// 开销 O(端口绑定数)（遍历全部绑定、只取仓库输出口；每个网络的件数是内核汇总值，O(1)）；只在描述“等待材料”时调用，
        /// 施工队列一次刷新只算一次（<see cref="Regions.HomeValleyConstruction.CollectQueue"/>），不按帧、不按施工单数倍增。
        /// 先按存档对齐索引（暂停中改了端口绑定也读到新列表）。
        /// 口径（FG4-ECO-01，FG-GAP-090）：只数 <paramref name="item"/> 这一种（默认废料 = 施工材料）——输出口待推的件只在端口此刻推的就是这种时计入，
        /// 网络里的件按种类数（内核 <see cref="BeltKernel.CountItemsByType"/>，逐格在 AOT，O(该网络格数)），别的物品不会被算成在途废料。
        /// </summary>
        public static int StoreItemsOnBelts(CampaignState state) => StoreItemsOnBelts(state, BeltItems.ScrapId);

        public static int StoreItemsOnBelts(CampaignState state, ushort item)
        {
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            if (k == null || state == null || !ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                return 0;
            }
            EnsureIndex(state);
            int total = 0;
            foreach (Binding b in Bindings)
            {
                if (b == null || !b.Store || !b.IsOutput || b.PortId < 0 || !k.TryGetPortInfo(b.PortId, out BeltPortInfo info))
                {
                    continue;
                }
                if (!info.Connected)
                {
                    continue; // 没接带的输出口只是预取了几件等着推（在途量上限），不是“在传送带上”
                }
                if (info.ItemType == item)
                {
                    total += Math.Max(0, info.Pending);
                }
                if (info.Network >= 0 && NetScratch.Add(info.Network) && k.TryGetNetworkStats(info.Network, out BeltNetworkStats st) && st.Items > 0)
                {
                    Array.Clear(KindScratch, 0, KindScratch.Length);
                    k.CountItemsByType(info.Network, KindScratch);
                    total += item < KindScratch.Length - 1 ? KindScratch[item] : 0;
                }
            }
            NetScratch.Clear();
            return total;
        }

        /// <summary>
        /// FG4-ECO-01（物品悬停“分布”里的“建筑端口缓存（在途）”）：全部建筑端口手里的物品按种类累加进 <paramref name="countsByItem"/>
        /// （输出口 = 已从库存取出、等着推上带的件；按种类收货的输入口 = 缓存里那一种）。下标 = 物品编号，超出数组的计入最后一格。O(端口绑定数)，只在悬停缓存刷新时调用。
        /// </summary>
        public static void CollectPortItems(CampaignState state, int[] countsByItem)
        {
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            if (k == null || state == null || countsByItem == null || countsByItem.Length == 0 || !ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                return;
            }
            EnsureIndex(state);
            int last = countsByItem.Length - 1;
            foreach (Binding b in Bindings)
            {
                if (b == null || b.PortId < 0 || !k.TryGetPortCounts(b.PortId, out int pending, out int buffered, out ushort item))
                {
                    continue;
                }
                if (item == 0 && b.Prod && !b.IsOutput)
                {
                    // FG4-ECO-02：生产建筑只收一种物品的输入口，内核不记种类：就是当前配方要的那种固体。
                    item = Economy.ProductionService.SinkAcceptFor(state, HomeGridService.FindBuilding(state, b.BuildingId), b.PortKey);
                    if (item == BeltConst.AcceptNone || item == BeltConst.AcceptAnyOneKind || BeltConst.IsSetAccept(item))
                    {
                        item = 0;
                    }
                }
                if (item == 0)
                {
                    continue;
                }
                int n = Math.Max(0, pending) + Math.Max(0, buffered);
                if (n > 0)
                {
                    countsByItem[item < last ? item : last] += n;
                }
            }
        }

        /// <summary>
        /// FG4-ECO-01：家园仓库输入口此刻是不是因为“仓库放不下”而不收——按缓存里那一种（缓存空时按废料）看剩余空间。
        /// <paramref name="kind"/> 给出那一种（端口面板、根因诊断写“放不下 X”）。O(1)（仓库是否运转 O(建筑数)，只在面板 / 诊断时调用）。
        /// </summary>
        public static bool StoreFull(CampaignState state, int portId, out Economy.ItemDef kind)
        {
            kind = null;
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            if (k == null || state == null || !k.TryGetPortCounts(portId, out _, out int buffered, out ushort item))
            {
                return false;
            }
            kind = BeltItems.Def(buffered > 0 && item != 0 ? item : BeltItems.ScrapId);
            return kind != null && Economy.HomeInventory.Space(state, kind) <= 0;
        }

        public static bool TryGetBinding(int portId, out Binding bind)
        {
            EnsureIndex(_state);
            return ById.TryGetValue(portId, out bind);
        }

        public static Binding Find(string buildingId, string portKey)
        {
            EnsureIndex(_state);
            return ByKey.TryGetValue(KeyOf(buildingId, portKey), out Binding b) ? b : null;
        }

        /// <summary>这一格传送带是不是某个建筑输出口的推送格（悬停写“起点：从 X 的输出口接货”）。</summary>
        public static bool TryFindSourceAt(GridCell beltCell, out Binding bind)
        {
            EnsureIndex(_state);
            return SourceByBeltCell.TryGetValue(beltCell, out bind);
        }

        public static string BuildingName(string buildingId)
        {
            BuildingRecord b = _state != null ? HomeGridService.FindBuilding(_state, buildingId) : null;
            return b != null ? HomeGridService.DisplayName(b.BuildingTypeId) : buildingId ?? string.Empty;
        }

        /// <summary>端口面板：这座建筑按端口表的每个端口一行（启用与否、接没接上、统计、问题与办法）。O(端口数)。</summary>
        public static void CollectViews(CampaignState state, BuildingRecord b, List<PortView> into)
        {
            into.Clear();
            if (state == null || b == null)
            {
                return;
            }
            EnsureIndex(state);
            bool active = IsActive(b);
            string buildingName = HomeGridService.DisplayName(b.BuildingTypeId);
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            foreach (BuildingPort row in GridContent.PortsOf(b.BuildingTypeId))
            {
                Binding shape = Make(b, row, null);
                var v = new PortView
                {
                    PortKey = row.Id,
                    IsOutput = shape.IsOutput,
                    Store = shape.Store,
                    Prod = shape.Prod,
                    Face = shape.Face,
                    PortCell = shape.PortCell,
                    BeltCell = shape.BeltCell,
                    Active = active,
                };
                string dirName = GameText.Get(GridMath.DirTextKey(shape.Face));
                v.Title = GameText.Format(v.IsOutput ? "logistics.port.out" : "logistics.port.in", dirName);
                if (ByKey.TryGetValue(KeyOf(b.BuildingId, row.Id), out Binding bind) && k != null && k.TryGetPortInfo(bind.PortId, out BeltPortInfo info))
                {
                    v.Bound = true;
                    v.PortId = bind.PortId;
                    v.Filter = bind.Record?.Filter ?? FilterAll;
                    v.Keep = bind.Record?.Keep ?? 0;
                    v.Info = info;
                    v.Connected = info.Connected;
                }
                if (v.Prod)
                {
                    // FG4-ECO-02：生产建筑的口——输入口写收什么（当前配方 / 回收站任何固体 / 没选配方不收），输出口写推什么。
                    v.AcceptLine = Economy.ProductionService.PortAcceptLine(state, b, v.IsOutput, row.Id);
                }
                else if (!v.Store)
                {
                    v.AcceptLine = v.IsOutput ? null : GameText.Get("logistics.port.accept_none");
                    v.IssueLine = GameText.Get("logistics.port.role_none");
                }
                else if (!v.IsOutput)
                {
                    v.AcceptLine = GameText.Get("logistics.port.accept_all");
                }
                if (!active)
                {
                    v.StateLine = GameText.Format("logistics.port.inactive", GameText.Get(StateKey(b.ConstructionState)));
                }
                else if (v.Connected)
                {
                    v.StateLine = GameText.Get("logistics.port.connected");
                }
                else
                {
                    string towards = GameText.Get(GridMath.DirTextKey((GridDir)(((int)shape.Face + 2) & 3)));
                    v.StateLine = v.IsOutput
                        ? GameText.Format("logistics.port.disconnected_out", shape.BeltCell.X, shape.BeltCell.Y)
                        : GameText.Format("logistics.port.disconnected_in", shape.BeltCell.X, shape.BeltCell.Y, towards);
                }
                if (v.Bound)
                {
                    string perMin = v.Info.WindowSeconds > 0f
                        ? GameText.Format("logistics.port.per_minute", v.Info.PerMinute.ToString("0.#", CultureInfo.InvariantCulture))
                        : GameText.Get("logistics.hover.measuring");
                    v.StatsLine = v.IsOutput
                        ? GameText.Format("logistics.port.stats_out", v.Info.Total, Math.Max(0, v.Info.Pending), perMin)
                        : GameText.Format("logistics.port.stats_in", v.Info.Total, v.Info.Buffered, v.Info.BufferCap, perMin);
                    if (v.Prod && active && v.Connected && v.IsOutput && v.Info.Pending > 0 && k != null
                        && k.TryGetCellInfo(shape.BeltCell.X, shape.BeltCell.Y, out BeltCellInfo prodHead) && prodHead.Block != BeltBlock.None && prodHead.Count > 0)
                    {
                        v.IssueLine = GameText.Get("logistics.port.out_blocked");
                    }
                    if (v.Store && active && v.Connected)
                    {
                        // FG4-ECO-01：输入口按缓存里那一种（没有缓存时按废料）判断仓库满不满。
                        Economy.ItemDef inDef = BeltItems.Def(v.Info.Buffered > 0 && v.Info.ItemType != 0 ? v.Info.ItemType : BeltItems.ScrapId);
                        if (!v.IsOutput && inDef != null && Economy.HomeInventory.Space(state, inDef) <= 0)
                        {
                            v.IssueLine = GameText.Format("logistics.port.store_full_item", inDef.Name, Economy.HomeInventory.Stock(state, inDef),
                                Economy.HomeInventory.Capacity(state, inDef));
                        }
                        else if (v.IsOutput && v.Filter != FilterOff && v.Info.Pending <= 0 && v.Keep > 0 && !OutputStockEmpty(state, v.Filter)
                                 && OutputStockEmpty(state, v.Filter, v.Keep))
                        {
                            // FG4-ECO-05（FG-GAP-093）：有库存，只是没超过“保留 N 件”——写明是保留数拦住的，不是没货。
                            ushort it = v.Filter > 0 ? ItemForFilter(v.Filter) : NextStocked(state, 0);
                            v.IssueLine = GameText.Format("logistics.port.state.keeping", BeltItems.Name(it), BeltItems.Stock(state, it), v.Keep);
                        }
                        else if (v.IsOutput && v.Filter != FilterOff && v.Info.Pending <= 0 && OutputStockEmpty(state, v.Filter))
                        {
                            v.IssueLine = GameText.Format("logistics.port.store_empty",
                                v.Filter == FilterAll ? GameText.Get("logistics.port.filter_all") : BeltItems.Name(ItemForFilter(v.Filter)));
                        }
                        else if (v.IsOutput && v.Info.Pending > 0 && k != null && k.TryGetCellInfo(shape.BeltCell.X, shape.BeltCell.Y, out BeltCellInfo head)
                                 && head.Block != BeltBlock.None && head.Count > 0)
                        {
                            v.IssueLine = GameText.Get("logistics.port.out_blocked");
                        }
                    }
                }
                into.Add(v);
            }
        }

        private static string StateKey(BuildingConstructionState s)
        {
            switch (s)
            {
                case BuildingConstructionState.Damaged: return "logistics.port.state.damaged";
                case BuildingConstructionState.Planned:
                case BuildingConstructionState.MaterialReserved:
                case BuildingConstructionState.Building:
                    return "logistics.port.state.planned";
                default: return "logistics.port.state.other";
            }
        }
    }
}
