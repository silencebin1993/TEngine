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
            public GridDir Face;
            public GridCell PortCell;
            public GridCell BeltCell;
            /// <summary>建筑在运转（端口启用）。</summary>
            public bool Active;
            public bool Bound;
            public bool Connected;
            public int PortId = -1;
            public int Filter = FilterAll;
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
                        if (have.PortCell == want.PortCell && have.Face == want.Face && have.IsOutput == want.IsOutput && have.Store == want.Store)
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
                ushort item = bind.Store ? ItemForFilter(filter) : (ushort)0;
                r = BeltNetworkService.TryAddSource(state, id, bind.BeltCell, item, SourceInterval, 0, 0, face);
            }
            else
            {
                string nameKey = FgContentTables.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.Building brow) ? brow.NameKey : null;
                ushort accept = bind.Store ? BeltItems.ScrapId : BeltConst.AcceptNone;
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
                item = bind.IsOutput ? info.ItemType : (info.Accept != BeltConst.AcceptAny && info.Accept != BeltConst.AcceptNone ? info.Accept : BeltItems.ScrapId);
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

        /// <summary>过滤 → 输出口推哪种物品（全部 = 第一种可存物品；目前只有废料）。</summary>
        public static ushort ItemForFilter(int filter)
        {
            if (filter > 0 && filter < ushort.MaxValue)
            {
                return (ushort)filter;
            }
            return BeltItems.ScrapId;
        }

        /// <summary>
        /// 转移：仓库输入口的缓存存进家园仓库；仓库输出口按过滤从库存补足待推数。O(端口数)，内核每走一步之后一次。
        /// 按端口号升序处理（同一状态同一结果）。
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
            int sourceBuffer = SourceBuffer;
            // 仓库容量只查一次（容量查询要找仓库建筑，O(建筑数)），剩余空间按当前库存现算——整次转移 O(端口数)。
            int capacity = -1;
            foreach (Binding bind in Bindings)
            {
                if (!bind.Store || bind.Record == null || !k.TryGetPortCounts(bind.PortId, out int pending, out int buffered))
                {
                    continue;
                }
                if (bind.IsOutput)
                {
                    int filter = bind.Record.Filter;
                    if (filter == FilterOff || pending >= sourceBuffer)
                    {
                        continue;
                    }
                    ushort item = ItemForFilter(filter);
                    if (item != BeltItems.ScrapId)
                    {
                        continue; // 家园仓库目前只存废料：别的物品没有库存可取。
                    }
                    int take = Math.Min(sourceBuffer - Math.Max(0, pending), Math.Max(0, state.Scrap));
                    if (take > 0)
                    {
                        state.Scrap -= take;
                        k.AddSourceItems(bind.PortId, take);
                    }
                }
                else if (buffered > 0)
                {
                    if (capacity < 0)
                    {
                        capacity = HomeValleyCargo.GetStorageCapacity(state, CampaignEconomyLedger.ResourceScrap);
                    }
                    int n = Math.Min(buffered, Math.Max(0, capacity - state.Scrap));
                    if (n > 0)
                    {
                        state.Scrap += k.TakeFromSink(bind.PortId, n);
                    }
                }
            }
            PumpCount++;
            LastPumpMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
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
                    v.Info = info;
                    v.Connected = info.Connected;
                }
                if (!v.Store)
                {
                    v.AcceptLine = v.IsOutput ? null : GameText.Get("logistics.port.accept_none");
                    v.IssueLine = GameText.Get("logistics.port.role_none");
                }
                else if (!v.IsOutput)
                {
                    v.AcceptLine = GameText.Format("logistics.port.accept", BeltItems.Name(BeltItems.ScrapId));
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
                    if (v.Store && active && v.Connected)
                    {
                        int cap = HomeValleyCargo.GetStorageCapacity(state, CampaignEconomyLedger.ResourceScrap);
                        if (!v.IsOutput && HomeValleyCargo.GetAvailableSpace(state, CampaignEconomyLedger.ResourceScrap) <= 0)
                        {
                            v.IssueLine = GameText.Format("logistics.port.store_full", state.Scrap, cap);
                        }
                        else if (v.IsOutput && v.Filter != FilterOff && v.Info.Pending <= 0 && BeltItems.Stock(state, ItemForFilter(v.Filter)) <= 0)
                        {
                            v.IssueLine = GameText.Format("logistics.port.store_empty", BeltItems.Name(ItemForFilter(v.Filter)));
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
