using System;
using System.Collections.Generic;
using System.Diagnostics;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>一次传送带编辑的结果：成功，或一条稳定的原因（文本键 + 参数；IC-REQ-013 / FG00 B06）。</summary>
    public readonly struct BeltOpResult
    {
        public readonly bool Ok;
        public readonly BeltResult Code;
        /// <summary>格网规则拒绝时的原因（迷雾、地形、占用……），否则 null。</summary>
        public readonly GridPlacementResult Grid;
        public readonly string ReasonKey;
        public readonly string[] Args;

        private BeltOpResult(bool ok, BeltResult code, GridPlacementResult grid, string key, string[] args)
        {
            Ok = ok;
            Code = code;
            Grid = grid;
            ReasonKey = key;
            Args = args ?? Array.Empty<string>();
        }

        public static readonly BeltOpResult Success = new BeltOpResult(true, BeltResult.Ok, null, null, null);

        public static BeltOpResult Kernel(BeltResult code) =>
            code == BeltResult.Ok ? Success : new BeltOpResult(false, code, null, BeltNetworkService.ReasonKey(code), null);

        public static BeltOpResult FromGrid(GridPlacementResult grid) =>
            new BeltOpResult(false, BeltResult.InvalidArgument, grid, grid.Reasons[0].TextKey, grid.Reasons[0].Args);

        public static BeltOpResult NotRunning => new BeltOpResult(false, BeltResult.InvalidArgument, null, "logistics.reason.not_running", null);

        /// <summary>传送带存档来自不认识的格式版本、原数据正原样保留：这局不能改传送带（改了也存不进去）。</summary>
        public static BeltOpResult SavePreserved => new BeltOpResult(false, BeltResult.InvalidArgument, null, "logistics.reason.save_preserved", null);

        /// <summary>FG3-LOG-04：热更层规则拒绝（例如地下传送带跨度超限），给文本键与参数。</summary>
        public static BeltOpResult Reason(BeltResult code, string key, params string[] args) => new BeltOpResult(false, code, null, key, args);

        /// <summary>当前语言的原因文本（成功时为空串）。</summary>
        public string Describe()
        {
            if (Ok)
            {
                return string.Empty;
            }
            if (Grid != null && Grid.Reasons.Count > 0)
            {
                return Grid.Reasons[0].Describe();
            }
            return Args.Length == 0 ? GameText.Get(ReasonKey) : GameText.Format(ReasonKey, Args);
        }
    }

    /// <summary>
    /// FG0-ARCH-02（FGR-ARC-004）：传送带内核在热更层的**唯一入口**——持有星球表面（家园所在表面）的
    /// <see cref="BeltKernel"/>，负责：
    /// - 生命周期：<see cref="Load"/>（WorldSimulation.LoadHome 时，从存档恢复）/ <see cref="Unload"/>（UnloadAll 时，成对释放原生内存与 GPU 缓冲）。
    /// - 固定步长：世界模拟每个 60 Hz 步调用 <see cref="WorldStep"/>，按游戏时间累计推进到 logistics.step_hz（初值 20 Hz）。
    ///   只看步数，不看镜头 / 帧率 / 倍速 → 暂停、0.5x～3x、观察与否结果逐位一致（FGR-BASE-021）。
    /// - 存档：<see cref="WriteTo"/>（SyncAllForSave 时写进 <see cref="BeltItemState"/>，按网络分块）。
    /// - 编辑：<see cref="TryPlace"/> 等——格网规则（迷雾、地形、占用……）与内核规则都给稳定原因；格网的传送带层随之写入（建筑不能压在带上）。
    /// - 渲染：观察星球表面时每帧 <see cref="Render"/> 一次（O(1) 调用，逐物品工作全在 AOT）。
    /// 热更层每帧 / 每步的开销与格数、物品数无关：这里只有常数次调用，逐格循环都在 BinGames.Sim。
    /// 正式的放置工具（建造菜单里的传送带、拖拽铺设、端口对接到真实建筑）由 FG3-LOG-01 / FG3-LOG-03 接在这些入口上（DEBT-FG0ARCH02-01）。
    /// </summary>
    public static class BeltNetworkService
    {
        private static BeltKernel _kernel;
        private static BeltRenderer _renderer;
        private static CampaignState _state;
        private static BeltRenderer.Settings _renderSettings;
        private static bool _preserveSaved;
        private static bool _blockedHookChecked;
        private static bool _splitterBlockedHookChecked;
        private static long _lastBeltTick;
        private static readonly List<int3> CellScratch = new List<int3>(256);
        private static readonly Dictionary<int, string> SinkOwnerNameKeys = new Dictionary<int, string>();
        /// <summary>FG3-LOG-03（FGR-LOG-027）：掉了耐久的格 → 掉了多少（存档 <see cref="BeltItemState.Damage"/> 的运行时索引，悬停 O(1)）。</summary>
        private static readonly Dictionary<GridCell, int> DamageLost = new Dictionary<GridCell, int>();

        public static BeltKernel Kernel => _kernel;
        public static bool IsRunning => _kernel != null && !_kernel.IsDisposed;
        public static CampaignState BoundState => _state;
        public static BeltRenderer Renderer => _renderer;
        public static BeltRenderer.Settings RenderSettings => _renderSettings;
        /// <summary>存档里的传送带数据来自不认识的格式版本，正原样保留（这局不写回、不接受编辑）。</summary>
        public static bool SavedDataPreserved => _preserveSaved;
        /// <summary>本会话内核执行的步数（诊断）。</summary>
        public static long KernelStepsThisSession { get; private set; }
        public static int LoadCount { get; private set; }
        /// <summary>最近一次读档的问题（坏块 / 格式不认识），null = 正常。</summary>
        public static string LastLoadError { get; private set; }
        /// <summary>最近一帧 <see cref="Render"/> 的总耗时（毫秒，含 AOT 准备缓冲与绘制提交）。</summary>
        public static double LastRenderMs { get; private set; }
        public static int RenderCalls { get; private set; }

        // ── 配置（全部来自 fg.TbHomeTuning 的 logistics.* 行）───────────────────────

        public static BeltConfig ReadConfig()
        {
            var c = new BeltConfig
            {
                StepHz = TuningInt("logistics.step_hz", 20),
                SlotsPerCell = TuningInt("logistics.slots_per_cell", 4),
                ItemsPerMinuteT1 = TuningInt("logistics.items_per_minute_t1", 60),
                ItemsPerMinuteT2 = TuningInt("logistics.items_per_minute_t2", 120),
                ItemsPerMinuteT3 = TuningInt("logistics.items_per_minute_t3", 240),
                BucketSteps = TuningInt("logistics.stats_bucket_steps", 300),
            };
            if (!c.IsValid(out string why))
            {
                Log.Error($"[BeltNetworkService] logistics.* 调参非法（{why}），回落到规格初值。");
                c = BeltConfig.Default;
            }
            return c;
        }

        public static BeltRenderer.Settings ReadRenderSettings() => new BeltRenderer.Settings
        {
            CellSize = 1f,
            FlowOrthoEnter = TuningFloat("logistics.render.flow_ortho_enter", 38f),
            FlowOrthoExit = TuningFloat("logistics.render.flow_ortho_exit", 34f),
            ItemSize = TuningFloat("logistics.render.item_size", 0.22f),
            Height = TuningFloat("logistics.render.height", 0.04f),
        };

        private static int TuningInt(string id, int fallback) => (int)Math.Round(TuningFloat(id, fallback));

        private static float TuningFloat(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Error($"[BeltNetworkService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_logistics.py 后重新生成）。");
            return fallback;
        }

        // ── 生命周期 ─────────────────────────────────────────────────────────────

        /// <summary>接入战役的星球表面（WorldSimulation.LoadHome）：新建内核，从 <see cref="BeltItemState"/> 恢复，同步格网传送带层。</summary>
        public static void Load(CampaignState state)
        {
            Unload();
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            BeltItemState saved = state.Belts;
            _kernel = new BeltKernel(ReadConfig(), Math.Max(256, saved.CellCount));
            _state = state;
            _renderSettings = ReadRenderSettings();
            _preserveSaved = false;
            _blockedHookChecked = false;
            _splitterBlockedHookChecked = false;
            _lastBeltTick = GameClock.Ticks;
            LastLoadError = null;
            KernelStepsThisSession = 0;
            if (saved.FormatVersion > 0)
            {
                BeltSnapshot snap = ToSnapshot(saved);
                if (!_kernel.Deserialize(snap, out string err))
                {
                    // 整体不可用（格式版本不认识）：内核留空，存档里的原始数据原样保留（不被下一次存档覆盖）；
                    // 这局的传送带编辑一律拒绝并给原因（SavePreserved），不让玩家铺了带却在下次存档时悄悄丢掉。
                    _preserveSaved = true;
                    LastLoadError = DescribeLoadIssues(err);
                    Log.Error($"[BeltNetworkService] 传送带存档无法读取（{err}），原始数据保留不覆盖，本局传送带编辑停用");
                    NotificationCenter.Post("save_migrated", GameText.Format("logistics.load.unreadable",
                        LastLoadError, saved.Networks.Length, saved.ItemCount));
                }
                else
                {
                    RestoreSinkNames(saved);
                    if (_kernel.StatsDroppedOnLoad > 0)
                    {
                        // 统计窗口只影响“实测”读数：读不了就从零累计（与格式 4 之前一样），物品与状态照常恢复，不打扰玩家。
                        Log.Warning($"[BeltNetworkService] 传送带存档有 {_kernel.StatsDroppedOnLoad} 段统计窗口读不了，已从零累计");
                    }
                    if (_kernel.CorruptChunksDropped > 0 || err != null)
                    {
                        LastLoadError = DescribeLoadIssues(err);
                        Log.Warning($"[BeltNetworkService] 传送带存档有 {_kernel.CorruptChunksDropped} 个网络块损坏已丢弃（{_kernel.CorruptItemsDropped} 件计入移出）：{err}");
                        NotificationCenter.Post("save_migrated", GameText.Format("logistics.load.corrupt",
                            _kernel.CorruptChunksDropped, _kernel.CorruptItemsDropped, LastLoadError));
                    }
                }
            }
            SyncGridLayer(state);
            RestoreDamage(state);
            BeltPortService.OnLoad(state);
            LoadCount++;
        }

        private static void RestoreDamage(CampaignState state)
        {
            DamageLost.Clear();
            BeltDamageRecord[] recs = state.Belts?.Damage ?? Array.Empty<BeltDamageRecord>();
            var keep = new List<BeltDamageRecord>(recs.Length);
            foreach (BeltDamageRecord r in recs)
            {
                // 只恢复内核里确实存在的格（读档时坏块丢弃的格，它的耐久记录一并丢弃）。
                if (r != null && r.Lost > 0 && _kernel.HasCell(r.X, r.Y))
                {
                    DamageLost[new GridCell(r.X, r.Y)] = r.Lost;
                    keep.Add(r);
                }
            }
            if (keep.Count != recs.Length && state.Belts != null)
            {
                state.Belts.Damage = keep.ToArray();
            }
        }

        /// <summary>内核读档原因码（逗号分隔）→ 当前语言的原因文本（文本键 logistics.load.reason.&lt;码&gt;，B16）。</summary>
        public static string DescribeLoadIssues(string codes)
        {
            if (string.IsNullOrEmpty(codes))
            {
                return null;
            }
            var parts = new List<string>();
            foreach (string code in codes.Split(','))
            {
                string key = "logistics.load.reason." + code;
                parts.Add(GameText.Has(key) ? GameText.Get(key) : GameText.Get("logistics.load.reason.unknown"));
            }
            return string.Join(GameText.Get("logistics.load.reason.separator"), parts);
        }

        /// <summary>卸载（UnloadAll：回主菜单 / 回滚 / 自检之间）：原生内存与 GPU 缓冲成对释放。不写存档（存档由 SyncAllForSave 负责）。</summary>
        public static void Unload()
        {
            BeltPortService.OnUnload();
            DamageLost.Clear();
            _renderer?.Dispose();
            _renderer = null;
            _kernel?.Dispose();
            _kernel = null;
            _state = null;
            _preserveSaved = false;
            SinkOwnerNameKeys.Clear();
        }

        /// <summary>读档：恢复输入端口所属建筑的名字键（只恢复内核里确实存在的输入端口）。</summary>
        private static void RestoreSinkNames(BeltItemState saved)
        {
            SinkOwnerNameKeys.Clear();
            if (saved.SinkNames == null)
            {
                return;
            }
            foreach (BeltSinkNameRecord rec in saved.SinkNames)
            {
                if (rec != null && !string.IsNullOrEmpty(rec.NameKey) && _kernel.TryGetPortInfo(rec.PortId, out BeltPortInfo info)
                    && info.Kind == BeltPortKind.Sink)
                {
                    SinkOwnerNameKeys[rec.PortId] = rec.NameKey;
                }
            }
        }

        private static void SyncGridLayer(CampaignState state)
        {
            ApplyGridLayer(state, HomeGridService.MapFor(state));
        }

        /// <summary>
        /// 把内核里的传送带写进格网的传送带层（建筑放置校验读它；有带的区块不被回收）。格网层是**派生缓存**，真相在内核：
        /// HomeGridService 每次新建格网（换战役、读档、表 / 生成器版本变化）都调这里重新套上，与“占用层由建筑记录推导”同一纪律。O(格数)，只在建图时发生。
        /// </summary>
        public static void ApplyGridLayer(CampaignState state, HomeGridMap map)
        {
            if (!IsRunning || map == null || state == null || !ReferenceEquals(state, _state))
            {
                return;
            }
            _kernel.CollectCells(CellScratch);
            foreach (int3 c in CellScratch)
            {
                map.SetBelt(new GridCell(c.x, c.y), (ushort)(((c.z >> 8) & 0xFF) + 1));
            }
            CellScratch.Clear();
            GridLayerApplyCount++;
        }

        /// <summary>格网传送带层被重新套用的次数（诊断 / 自检）。</summary>
        public static int GridLayerApplyCount { get; private set; }

        // ── 固定步长 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 世界模拟的一个固定步（WorldSimulation.StepOnce，在全部地点与行进队伍之后、时钟提交之前）。
        /// 内核步数 = ⌊(世界步序号 + 1) × 内核频率 / 世界频率⌋ − ⌊世界步序号 × 内核频率 / 世界频率⌋：
        /// 只由绝对步序号决定（60 Hz 世界 / 20 Hz 内核 = 每 3 个世界步 1 个内核步），读档后接着同一节拍，暂停不走、倍速只是一帧多走几步。
        /// </summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (!IsRunning || !ReferenceEquals(state, _state) || worldHz <= 0)
            {
                return;
            }
            int bh = _kernel.Config.StepHz;
            long from = ticksBefore * bh / worldHz;
            long to = (ticksBefore + 1) * bh / worldHz;
            bool stepped = false;
            for (long k = from; k < to; k++)
            {
                _kernel.Step();
                KernelStepsThisSession++;
                _lastBeltTick = ticksBefore + 1;
                stepped = true;
            }
            // FG3-LOG-03：建筑端口对账（按步序号每 logistics.port.sync_seconds）与仓库 ↔ 端口的转移（内核走过这一步才做），O(端口数)。
            BeltPortService.Step(state, ticksBefore + 1, worldHz, stepped);
            if (!_blockedHookChecked && _kernel.BlockedCells > 0)
            {
                _blockedHookChecked = true;
                GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstBlocked);
            }
            // FG3-LOG-04（FG03 第 5 节“分流器两个输出口都堵”）：第一次有分流器停下时发引导钩子（O(1) 读内核计数；引导内容在 FG15-UX-04）。
            if (!_splitterBlockedHookChecked && _kernel.BlockedSplitters > 0)
            {
                _splitterBlockedHookChecked = true;
                GuidanceHooks.Raise(GuidanceHooks.LogisticsSplitterFirstBlocked);
            }
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────

        /// <summary>把内核快照写进存档域（WorldSimulation.SyncAllForSave；O(格数 + 物品数)，只在存档时发生）。</summary>
        public static void WriteTo(CampaignState state)
        {
            if (!IsRunning || state == null || !ReferenceEquals(state, _state) || _preserveSaved)
            {
                return;
            }
            BeltSnapshot snap = _kernel.Serialize();
            BeltItemState b = state.Belts ??= new BeltItemState();
            b.DomainVersion = 2;
            b.FormatVersion = snap.FormatVersion;
            b.KernelSteps = snap.StepIndex;
            b.Emitted = snap.Emitted;
            b.Inserted = snap.Inserted;
            b.Delivered = snap.Delivered;
            b.Removed = snap.Removed;
            b.CompletedBuckets = snap.CompletedBuckets;
            b.CellCount = snap.TotalCells;
            b.ItemCount = _kernel.ItemCount;
            var records = new BeltNetworkRecord[snap.Networks.Count];
            for (int i = 0; i < records.Length; i++)
            {
                BeltSnapshot.Chunk c = snap.Networks[i];
                records[i] = new BeltNetworkRecord { Cells = c.Cells, Items = c.Items, Payload = Convert.ToBase64String(c.Bytes) };
            }
            b.Networks = records;
            b.Ports = snap.Ports != null ? Convert.ToBase64String(snap.Ports) : string.Empty;
            // 端口名字键按端口号升序写（同一状态写出同一份存档）；O(输入端口数)。
            var names = new List<BeltSinkNameRecord>(SinkOwnerNameKeys.Count);
            foreach (KeyValuePair<int, string> kv in SinkOwnerNameKeys)
            {
                if (_kernel.TryGetPortInfo(kv.Key, out BeltPortInfo info) && info.Kind == BeltPortKind.Sink)
                {
                    names.Add(new BeltSinkNameRecord { PortId = kv.Key, NameKey = kv.Value });
                }
            }
            names.Sort((a, c) => a.PortId.CompareTo(c.PortId));
            b.SinkNames = names.ToArray();
        }

        public static BeltSnapshot ToSnapshot(BeltItemState b)
        {
            var snap = new BeltSnapshot
            {
                FormatVersion = b.FormatVersion,
                StepIndex = b.KernelSteps,
                Emitted = b.Emitted,
                Inserted = b.Inserted,
                Delivered = b.Delivered,
                Removed = b.Removed,
                CompletedBuckets = b.CompletedBuckets,
                Ports = FromBase64(b.Ports),
            };
            if (b.Networks != null)
            {
                foreach (BeltNetworkRecord r in b.Networks)
                {
                    snap.Networks.Add(new BeltSnapshot.Chunk { Cells = r?.Cells ?? 0, Items = r?.Items ?? 0, Bytes = FromBase64(r?.Payload) });
                }
            }
            return snap;
        }

        private static byte[] FromBase64(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return null; // 当作坏块：内核整块丢弃并记账。
            }
        }

        // ── 编辑（正式的放置工具由 FG3-LOG-01 / FG3-LOG-03 接在这里）─────────────────

        private static bool Bound(CampaignState state) => IsRunning && state != null && ReferenceEquals(state, _state);

        /// <summary>
        /// 编辑入口的统一门：没接上这局 → NotRunning；存档里的传送带来自不认识的格式、原数据正保留 → SavePreserved
        /// （这局改了也写不回存档，与其悄悄丢掉不如当场说明）。
        /// </summary>
        private static bool CanEdit(CampaignState state, out BeltOpResult refuse)
        {
            if (!Bound(state))
            {
                refuse = BeltOpResult.NotRunning;
                return false;
            }
            if (_preserveSaved)
            {
                refuse = BeltOpResult.SavePreserved;
                return false;
            }
            refuse = BeltOpResult.Success;
            return true;
        }

        /// <summary>放一格传送带：先过格网规则（迷雾、地形、污染、建筑 / 传送带 / 管线占用、开局锚点），再进内核；成功后写格网传送带层。</summary>
        public static BeltOpResult TryPlace(CampaignState state, GridCell cell, BeltDir dir, int tier)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidTier);
            }
            if ((byte)dir > 3)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidDirection);
            }
            GridPlacementResult check = HomeGridService.ValidateBeltCell(state, cell);
            if (!check.Ok)
            {
                return BeltOpResult.FromGrid(check);
            }
            BeltResult r = _kernel.AddCell(cell.X, cell.Y, dir, tier);
            if (r != BeltResult.Ok)
            {
                return BeltOpResult.Kernel(r);
            }
            HomeGridService.MapFor(state).SetBelt(cell, (ushort)(tier + 1));
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstBelt);
            return BeltOpResult.Success;
        }

        /// <summary>拆一格：带上的物品写进 <paramref name="returned"/>（由调用方送回仓库 / 变成地面物，FG3-LOG-02 接全额返还）。</summary>
        public static BeltOpResult TryRemove(CampaignState state, GridCell cell, List<ushort> returned)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            // FG3-LOG-04（FGR-LOG-023）：拆地下传送带的任意一端 = 拆掉整条——两端的格网与耐久记录一起清掉，地下的物品一并交还。
            bool under = _kernel.TryGetUndergroundEnds(cell.X, cell.Y, out int2 ent, out int2 exi, out _);
            BeltResult r = _kernel.RemoveCell(cell.X, cell.Y, returned);
            if (r == BeltResult.Ok)
            {
                if (under)
                {
                    ClearCellRecords(state, new GridCell(ent.x, ent.y));
                    ClearCellRecords(state, new GridCell(exi.x, exi.y));
                }
                else
                {
                    ClearCellRecords(state, cell);
                }
            }
            return BeltOpResult.Kernel(r);
        }

        private static void ClearCellRecords(CampaignState state, GridCell cell)
        {
            HomeGridService.MapFor(state).SetBelt(cell, 0);
            if (DamageLost.ContainsKey(cell))
            {
                SetDamage(state, cell, 0); // FG3-LOG-03：格没了，耐久记录一并删掉（同一格重铺是新带、满耐久）。
            }
        }

        // ── FG3-LOG-04：分流器 / 合流器 / 地下传送带（FGR-LOG-022 / 023）──────────────────────────────────

        /// <summary>某一等级地下传送带的最大跨度（入口与出口之间隔着的格数；表 logistics.underground.span_t1～t3，初值 4 / 6 / 8）。</summary>
        public static int UndergroundSpan(int tier) =>
            Math.Max(1, GridContent.TuningInt(tier <= 0 ? "logistics.underground.span_t1" : tier == 1 ? "logistics.underground.span_t2" : "logistics.underground.span_t3"));

        /// <summary>物流件的玩家名：传送带 T1～T3 / 分流器 / 合流器 / 地下传送带 T1～T3。</summary>
        public static string PieceName(BeltNodeKind kind, int tier)
        {
            switch (kind)
            {
                case BeltNodeKind.Splitter:
                    return GameText.Get("logistics.node.splitter");
                case BeltNodeKind.Merger:
                    return GameText.Get("logistics.node.merger");
                case BeltNodeKind.UndergroundIn:
                case BeltNodeKind.UndergroundOut:
                case BeltNodeKind.Underground:
                    return GameText.Get(tier <= 0 ? "logistics.underground.t1" : tier == 1 ? "logistics.underground.t2" : "logistics.underground.t3");
                default:
                    return TierName(tier);
            }
        }

        /// <summary>(cell) 上已建成的物流件种类与等级（没有时返回 false）。O(1)。</summary>
        public static bool TryGetPiece(GridCell cell, out BeltNodeKind kind, out int tier)
        {
            kind = BeltNodeKind.Belt;
            tier = 0;
            if (!IsRunning || !_kernel.TryGetKind(cell.X, cell.Y, out kind) || !_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return false;
            }
            tier = info.Tier;
            return true;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-022）：放一个分流器 / 合流器：先过格网规则（与传送带同一套：迷雾、地形、污染、占用、开局锚点），再进内核；成功后写格网传送带层并发首次钩子。
        /// 新放的分流器默认 1:1、不设优先口、两口放全部物品；合流器默认两路交替。
        /// </summary>
        public static BeltOpResult TryPlaceNode(CampaignState state, GridCell cell, BeltDir dir, int tier, BeltNodeKind kind)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (kind != BeltNodeKind.Splitter && kind != BeltNodeKind.Merger)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidArgument);
            }
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidTier);
            }
            if ((byte)dir > 3)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidDirection);
            }
            GridPlacementResult check = HomeGridService.ValidateBeltCell(state, cell);
            if (!check.Ok)
            {
                return BeltOpResult.FromGrid(check);
            }
            BeltResult r = _kernel.AddNode(cell.X, cell.Y, dir, tier, kind);
            if (r != BeltResult.Ok)
            {
                return BeltOpResult.Kernel(r);
            }
            HomeGridService.MapFor(state).SetBelt(cell, (ushort)(tier + 1));
            GuidanceHooks.Raise(kind == BeltNodeKind.Splitter ? GuidanceHooks.LogisticsFirstSplitter : GuidanceHooks.LogisticsFirstMerger);
            return BeltOpResult.Success;
        }

        /// <summary>地下传送带跨度校验（热更层读表）：距离 = 出口与入口相差的格数，跨度 = 距离 − 1，不能超过这一等级的上限。</summary>
        public static BeltOpResult CheckUndergroundSpan(int tier, int distance)
        {
            if (distance < 1)
            {
                return BeltOpResult.Reason(BeltResult.InvalidArgument, "grid.reason.under_same_cell");
            }
            int max = UndergroundSpan(tier);
            if (distance - 1 > max)
            {
                return BeltOpResult.Reason(BeltResult.TooFar, "logistics.reason.span_too_far", PieceName(BeltNodeKind.UndergroundIn, tier),
                    max.ToString(System.Globalization.CultureInfo.InvariantCulture), (distance - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return BeltOpResult.Success;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-023）：放一条地下传送带：入口在 <paramref name="entrance"/>、朝 <paramref name="dir"/>，出口在 <paramref name="distance"/> 格外。
        /// 跨度（距离 − 1）按等级读表校验；入口与出口两格都过格网规则（中间的格子地面上有什么都不管——地下段不占地面）；同方向的地下段不能重叠（内核校验）。
        /// </summary>
        public static BeltOpResult TryPlaceUnderground(CampaignState state, GridCell entrance, BeltDir dir, int tier, int distance)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (tier < 0 || tier >= BeltConst.TierCount)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidTier);
            }
            if ((byte)dir > 3)
            {
                return BeltOpResult.Kernel(BeltResult.InvalidDirection);
            }
            BeltOpResult span = CheckUndergroundSpan(tier, distance);
            if (!span.Ok)
            {
                return span;
            }
            var exit = new GridCell(entrance.X + BeltDirs.Dx((int)dir) * distance, entrance.Y + BeltDirs.Dy((int)dir) * distance);
            foreach (GridCell end in new[] { entrance, exit })
            {
                GridPlacementResult check = HomeGridService.ValidateBeltCell(state, end);
                if (!check.Ok)
                {
                    return BeltOpResult.FromGrid(check);
                }
            }
            BeltResult r = _kernel.AddUnderground(entrance.X, entrance.Y, dir, tier, distance);
            if (r != BeltResult.Ok)
            {
                return BeltOpResult.Kernel(r);
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            map.SetBelt(entrance, (ushort)(tier + 1));
            map.SetBelt(exit, (ushort)(tier + 1));
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstUnderground);
            return BeltOpResult.Success;
        }

        /// <summary>FG3-LOG-04：原地转分流器 / 合流器（顺时针 90°；格里的物品不丢不增，合流器的优先口按左右跟着转）。</summary>
        public static BeltOpResult TryRotateNode(CampaignState state, GridCell cell)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (!_kernel.TryGetKind(cell.X, cell.Y, out BeltNodeKind kind) || !_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return BeltOpResult.Kernel(BeltResult.NotFound);
            }
            if (kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut)
            {
                return BeltOpResult.Reason(BeltResult.NotSupported, "logistics.reason.under_no_rotate");
            }
            return BeltOpResult.Kernel(_kernel.SetDirection(cell.X, cell.Y, (BeltDir)BeltDirs.Right((int)info.Dir)));
        }

        /// <summary>FG3-LOG-04（FGR-LOG-022）：设分流器的比例、优先输出口、左右口过滤（节点面板、预设、重建时恢复原设置都走这里）。</summary>
        public static BeltOpResult TrySetSplitter(CampaignState state, GridCell cell, int ratioL, int ratioR, BeltSide priority, ushort filterL, ushort filterR) =>
            CanEdit(state, out BeltOpResult refuse) ? BeltOpResult.Kernel(_kernel.SetSplitter(cell.X, cell.Y, ratioL, ratioR, priority, filterL, filterR)) : refuse;

        /// <summary>FG3-LOG-04（FGR-LOG-022）：设合流器的优先输入口。</summary>
        public static BeltOpResult TrySetMergerPriority(CampaignState state, GridCell cell, BeltSide priority) =>
            CanEdit(state, out BeltOpResult refuse) ? BeltOpResult.Kernel(_kernel.SetMergerPriority(cell.X, cell.Y, priority)) : refuse;

        /// <summary>节点读数（O(1)；地下传送带另加 O(距离)）。</summary>
        public static bool TryGetNode(GridCell cell, out BeltNodeInfo info)
        {
            info = default;
            return IsRunning && _kernel.TryGetNodeInfo(cell.X, cell.Y, out info);
        }

        /// <summary>原地反转方向（FGR-LOG-020）。</summary>
        public static BeltOpResult TryReverse(CampaignState state, GridCell cell)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (!_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return BeltOpResult.Kernel(BeltResult.NotFound);
            }
            return BeltOpResult.Kernel(_kernel.SetDirection(cell.X, cell.Y, (BeltDir)BeltDirs.Opposite((int)info.Dir)));
        }

        public static BeltOpResult TrySetDirection(CampaignState state, GridCell cell, BeltDir dir) =>
            CanEdit(state, out BeltOpResult refuse) ? BeltOpResult.Kernel(_kernel.SetDirection(cell.X, cell.Y, dir)) : refuse;

        public static BeltOpResult TrySetTier(CampaignState state, GridCell cell, int tier)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            BeltResult r = _kernel.SetTier(cell.X, cell.Y, tier);
            if (r == BeltResult.Ok)
            {
                HomeGridService.MapFor(state).SetBelt(cell, (ushort)(tier + 1));
            }
            return BeltOpResult.Kernel(r);
        }

        /// <summary>登记建筑的输出端口（推到 <paramref name="beltCell"/> 这一格传送带上）。</summary>
        public static BeltOpResult TryAddSource(CampaignState state, int portId, GridCell beltCell, ushort item, int intervalSteps, int pending, int owner = 0,
            byte face = BeltConst.AnyFace) =>
            CanEdit(state, out BeltOpResult refuse) ? BeltOpResult.Kernel(_kernel.AddSource(portId, beltCell.X, beltCell.Y, item, intervalSteps, pending, owner, face)) : refuse;

        /// <summary>登记建筑的输入端口（位于建筑格 <paramref name="buildingCell"/>）；<paramref name="ownerNameKey"/> 用于堵塞原因“下游 X 的输入已满”（随存档保存）。</summary>
        public static BeltOpResult TryAddSink(CampaignState state, int portId, GridCell buildingCell, int bufferCap, int consumeIntervalSteps,
            string ownerNameKey = null, int owner = 0, byte face = BeltConst.AnyFace, ushort accept = BeltConst.AcceptAny)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            BeltResult r = _kernel.AddSink(portId, buildingCell.X, buildingCell.Y, bufferCap, consumeIntervalSteps, owner, face, accept);
            if (r == BeltResult.Ok && !string.IsNullOrEmpty(ownerNameKey))
            {
                SinkOwnerNameKeys[portId] = ownerNameKey;
            }
            return BeltOpResult.Kernel(r);
        }

        /// <summary>
        /// 只更新输入端口所属建筑的名字键（建筑改名、换型，或旧档里没有名字时由建筑补登）；端口本身不动。
        /// <paramref name="ownerNameKey"/> 为空 = 清掉名字（堵塞原因回落为“输入端口 #编号”）。
        /// </summary>
        public static BeltOpResult TrySetSinkOwnerName(CampaignState state, int portId, string ownerNameKey)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            if (!_kernel.TryGetPortInfo(portId, out BeltPortInfo info) || info.Kind != BeltPortKind.Sink)
            {
                return BeltOpResult.Kernel(BeltResult.PortNotFound);
            }
            if (string.IsNullOrEmpty(ownerNameKey))
            {
                SinkOwnerNameKeys.Remove(portId);
            }
            else
            {
                SinkOwnerNameKeys[portId] = ownerNameKey;
            }
            return BeltOpResult.Success;
        }

        public static BeltOpResult TryRemovePort(CampaignState state, int portId) => TryRemovePort(state, portId, out _, out _);

        /// <summary>FG3-LOG-03：删端口并交还它手里的物品（输出口待推、输入口缓存），由调用方送回仓库。</summary>
        public static BeltOpResult TryRemovePort(CampaignState state, int portId, out int pending, out int buffered)
        {
            pending = 0;
            buffered = 0;
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            BeltResult r = _kernel.RemovePort(portId, out pending, out buffered);
            if (r == BeltResult.Ok)
            {
                SinkOwnerNameKeys.Remove(portId);
            }
            return BeltOpResult.Kernel(r);
        }

        // ── 耐久与损毁（FGR-LOG-027）────────────────────────────────────────────────

        /// <summary>一格传送带的最大耐久（按等级，表 logistics.belt.hp_t1～t3）。</summary>
        public static int MaxHp(int tier) =>
            Math.Max(1, GridContent.TuningInt(tier <= 0 ? "logistics.belt.hp_t1" : tier == 1 ? "logistics.belt.hp_t2" : "logistics.belt.hp_t3"));

        /// <summary>这一格现在的耐久（没有传送带时 -1）。O(1)。</summary>
        public static int HpOf(GridCell cell)
        {
            if (!IsRunning || !_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return -1;
            }
            return Math.Max(0, MaxHp(info.Tier) - (DamageLost.TryGetValue(cell, out int lost) ? lost : 0));
        }

        /// <summary>最近一次摧毁时掉在地上的物品件数（自检读）。</summary>
        public static int LastDestroyedItems { get; private set; }
        public static int DestroyedCount { get; private set; }

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-027“传送带有耐久，可能被突袭和天气损伤；被摧毁时上面的物品变成地面物，原位置留下虚影”）：对一格传送带造成伤害。
        /// 耐久到 0：带上的物品在原地落成地面物（废料生成搬运单，机器之后搬回仓库），传送带从内核移除，原位置留下保留朝向与等级的虚影
        /// （在玩家确认重建前不施工；自动重建规则在 FG6-DEF-03）。返回是否摧毁了。突袭伤害（FG6-DEF-05）与天气损伤（FG7）调这里；
        /// 目前家园还没有会打传送带的敌人和天气，由自检驱动（与 FG3-LOG-02 的施工现场摧毁同一做法）。
        /// </summary>
        public static bool TryDamage(CampaignState state, GridCell cell, int amount, out BeltOpResult result)
        {
            if (!CanEdit(state, out result))
            {
                return false;
            }
            if (amount <= 0)
            {
                result = BeltOpResult.Kernel(BeltResult.InvalidArgument);
                return false;
            }
            if (!_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                result = BeltOpResult.Kernel(BeltResult.NotFound);
                return false;
            }
            int lost = (DamageLost.TryGetValue(cell, out int l) ? l : 0) + amount;
            result = BeltOpResult.Success;
            if (lost < MaxHp(info.Tier))
            {
                SetDamage(state, cell, lost);
                return false;
            }
            DestroyCell(state, cell, info);
            return true;
        }

        private static void SetDamage(CampaignState state, GridCell cell, int lost)
        {
            if (lost > 0)
            {
                DamageLost[cell] = lost;
            }
            else
            {
                DamageLost.Remove(cell);
            }
            BeltItemState b = state.Belts;
            var list = new List<BeltDamageRecord>((b.Damage?.Length ?? 0) + 1);
            bool found = false;
            foreach (BeltDamageRecord r in b.Damage ?? Array.Empty<BeltDamageRecord>())
            {
                if (r == null)
                {
                    continue;
                }
                if (r.X == cell.X && r.Y == cell.Y)
                {
                    found = true;
                    if (lost > 0)
                    {
                        r.Lost = lost;
                        list.Add(r);
                    }
                    continue;
                }
                list.Add(r);
            }
            if (!found && lost > 0)
            {
                list.Add(new BeltDamageRecord { X = cell.X, Y = cell.Y, Lost = lost });
            }
            b.Damage = list.ToArray();
        }

        private static void DestroyCell(CampaignState state, GridCell cell, BeltCellInfo info)
        {
            // FG3-LOG-04：分流器 / 合流器被摧毁时虚影保留原设置（比例、优先口、过滤）；地下传送带整条摧毁，两端留一份虚影，地下的物品一并落地。
            _kernel.TryGetKind(cell.X, cell.Y, out BeltNodeKind kind);
            BeltNodeInfo node = default;
            bool isNode = kind != BeltNodeKind.Belt && _kernel.TryGetNodeInfo(cell.X, cell.Y, out node);
            var items = new List<ushort>(BeltConst.MaxSlots);
            if (!TryRemove(state, cell, items).Ok)
            {
                return;
            }
            var counts = new SortedDictionary<ushort, int>();
            foreach (ushort it in items)
            {
                counts[it] = counts.TryGetValue(it, out int n) ? n + 1 : 1;
            }
            var at = new Vector2(cell.X, cell.Y);
            foreach (KeyValuePair<ushort, int> kv in counts)
            {
                Regions.HomeValleyConstruction.DropForHaul(state, at, BeltItems.ResourceOf(kv.Key), kv.Value,
                    "belt-wreck:" + cell.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + cell.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (isNode)
            {
                Regions.HomeValleyConstruction.AddDestroyedNodeGhost(state, node);
            }
            else
            {
                Regions.HomeValleyConstruction.AddDestroyedBeltGhost(state, cell, info.Dir, info.Tier);
            }
            LastDestroyedItems = items.Count;
            DestroyedCount++;
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstDestroyed);
            NotificationCenter.Post("failure", isNode
                ? GameText.Format("logistics.destroyed.notify_piece", PieceName(kind, info.Tier), cell.X, cell.Y, items.Count)
                : GameText.Format("logistics.destroyed.notify", cell.X, cell.Y, items.Count), new Vector3(cell.X, 0f, cell.Y));
        }

        // ── 天气（FGR-LOG-028）───────────────────────────────────────────────────

        /// <summary>沙暴时露天传送带减速多少（表 logistics.weather.sandstorm_slow_pct，初值 25%）。天气系统（FG7-ENV-03）读它传给 <see cref="SetExposedSlowdown"/>。</summary>
        public static int SandstormSlowPercent => Mathf.Clamp(GridContent.TuningInt("logistics.weather.sandstorm_slow_pct"), 0, BeltConst.MaxSlowPercent);

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-028）：设定露天传送带的减速百分比（0 = 天气正常）。天气系统（FG7-ENV-03）在沙暴开始 / 结束时调用；每步幂等。
        /// 顶棚下的格（<see cref="SetCovered"/>）不受影响。派生量不进传送带存档（天气状态由天气域存档，读档后重新设定）。
        /// </summary>
        public static bool SetExposedSlowdown(int percent) => IsRunning && _kernel.SetExposedSlowPercent(Mathf.Clamp(percent, 0, BeltConst.MaxSlowPercent));

        /// <summary>FG3-LOG-03（FGR-LOG-028“在顶棚覆盖下不受影响”）：顶棚建筑（FG7）放下 / 拆除时标记它覆盖的格。</summary>
        public static void SetCovered(GridCell cell, bool covered)
        {
            if (IsRunning)
            {
                _kernel.SetCovered(cell.X, cell.Y, covered);
            }
        }

        // ── 原因与悬停文本（FGR-LOG-025 / 081；B05、B06、B16）──────────────────────

        public static string ReasonKey(BeltResult code)
        {
            switch (code)
            {
                case BeltResult.Occupied: return "logistics.reason.occupied";
                case BeltResult.NotFound: return "logistics.reason.not_found";
                case BeltResult.InvalidTier: return "logistics.reason.invalid_tier";
                case BeltResult.InvalidDirection: return "logistics.reason.invalid_direction";
                case BeltResult.NoSpace: return "logistics.reason.no_space";
                case BeltResult.PortOccupied: return "logistics.reason.port_occupied";
                case BeltResult.PortNotFound: return "logistics.reason.port_not_found";
                case BeltResult.OutOfRange: return "logistics.reason.out_of_range";
                case BeltResult.NotSupported: return "logistics.reason.not_supported";
                case BeltResult.UndergroundOccupied: return "logistics.reason.underground_occupied";
                case BeltResult.TooFar: return "logistics.reason.too_far";
                default: return "logistics.reason.invalid_argument";
            }
        }

        /// <summary>一格为什么停着（稳定文本；不只靠颜色）。格信息来自家园内核（悬停、面板、诊断）。</summary>
        public static string DescribeBlock(in BeltCellInfo info) => DescribeBlock(IsRunning ? _kernel : null, info);

        /// <summary>
        /// 一格为什么停着：<paramref name="kernel"/> 是 <paramref name="info"/> 所在的内核（顶牛判定、端口与分流器读数都查它，不查全局家园内核——
        /// 自检的独立内核、将来的多表面都用同一份写法）。<paramref name="kernel"/> 为空时只按格信息本身描述。
        /// </summary>
        public static string DescribeBlock(BeltKernel kernel, in BeltCellInfo info)
        {
            if (kernel != null && kernel.IsDisposed)
            {
                kernel = null;
            }
            bool home = kernel != null && ReferenceEquals(kernel, _kernel);
            switch (info.Block)
            {
                case BeltBlock.EndOfBelt:
                    return DescribeHeadOn(kernel, info) ?? GameText.Get("logistics.block.end_of_belt");
                case BeltBlock.DownstreamFull:
                    // FG3-LOG-04：地下入口的下游是地下段——写“地下段已满（通往出口 X）”，不写地下层坐标。
                    return info.NextUnderground
                        ? GameText.Format("logistics.block.under_full", info.NextX, info.NextY)
                        : GameText.Format("logistics.block.downstream_full", info.NextX, info.NextY);
                case BeltBlock.WrongSide:
                    return GameText.Format("logistics.block.wrong_side", RoleName(info.FrontKind), info.FrontX, info.FrontY, SideRule(info.FrontKind));
                case BeltBlock.SplitterFull:
                case BeltBlock.SplitterNoOutlet:
                    return DescribeSplitterBlock(kernel, info);
                case BeltBlock.SinkFull:
                {
                    string owner = SinkOwnerName(info.SinkPortId);
                    // FG3-LOG-03：仓库 / 核心的输入口满了多半是家园仓库满了——写明库存与容量和办法（只对家园内核：端口绑定与库存都是家园的）。
                    CampaignState st = _state;
                    if (home && st != null && BeltPortService.TryGetBinding(info.SinkPortId, out BeltPortService.Binding sb) && sb.Store
                        && Regions.HomeValleyCargo.GetAvailableSpace(st, CampaignEconomyLedger.ResourceScrap) <= 0)
                    {
                        return GameText.Format("logistics.block.store_full", owner, st.Scrap,
                            Regions.HomeValleyCargo.GetStorageCapacity(st, CampaignEconomyLedger.ResourceScrap));
                    }
                    return GameText.Format("logistics.block.sink_full", owner);
                }
                case BeltBlock.SinkRejects:
                {
                    string owner = SinkOwnerName(info.SinkPortId);
                    ushort accept = kernel != null && kernel.TryGetPortInfo(info.SinkPortId, out BeltPortInfo pi) ? pi.Accept : BeltConst.AcceptNone;
                    return accept == BeltConst.AcceptNone
                        ? GameText.Format("logistics.block.sink_rejects_all", owner)
                        : GameText.Format("logistics.block.sink_rejects", owner, BeltItems.Name(info.Item0), BeltItems.Name(accept));
                }
                case BeltBlock.MergeWait:
                    return GameText.Format("logistics.block.merge_wait", info.NextX, info.NextY);
                default:
                    return GameText.Get("logistics.block.none");
            }
        }

        /// <summary>
        /// FG3-E2E-01（M3 出口旅程抓到；第 1 轮审查补）：两条带顶牛——这一格是末端，正前方那一格也是传送带，而且朝着这一格。内核把两格都当“末端没有下游”，
        /// 这里写明是哪一格朝反了、怎么转回来（FGR-LOG-082、FG00 B06）。不是顶牛返回 null。
        /// 顶牛是对称的（两格互为“正前方”），所以要判断哪一格方向错：方向“有旁证”的一格是对的——
        /// ① 它有上游在喂它（<see cref="BeltCellInfo.Feeders"/> &gt; 0：身后 / 侧面有带朝它送料）；② 另一格身后那格带顺着另一格的反方向继续往前
        /// （另一格原来在给它送料，是从一条线中间被转反的）。只有一边有旁证时点名朝反的那一格；两边都有或都没有（例如两条线迎面铺到一起），
        /// 写中性的“指着方向错的那一格”，不替玩家猜。按键写明“在建造模式里、不选条目”——建造模式关着时旋转键不转传送带。O(1)：最多查四格。
        /// </summary>
        private static string DescribeHeadOn(BeltKernel kernel, in BeltCellInfo info)
        {
            if (kernel == null || info.Kind != BeltNodeKind.Belt)
            {
                return null;
            }
            int d = (int)info.Dir;
            int fx = info.X + BeltDirs.Dx(d);
            int fy = info.Y + BeltDirs.Dy(d);
            if (!kernel.TryGetCellInfo(fx, fy, out BeltCellInfo front) || front.Kind != BeltNodeKind.Belt || (int)front.Dir != BeltDirs.Opposite(d))
            {
                return null;
            }
            bool selfBacked = info.Feeders > 0 || BackFlowsAway(kernel, front);
            bool frontBacked = front.Feeders > 0 || BackFlowsAway(kernel, info);
            string build = InputDisplay.ForAction(GameActionId.OpenBuildMenu);
            string rotate = InputDisplay.ForAction(GameActionId.Rotate);
            if (selfBacked && !frontBacked)
            {
                return GameText.Format("logistics.block.head_on", fx, fy, GameText.Get(GridMath.DirTextKey((GridDir)front.Dir)), build, rotate);
            }
            if (frontBacked && !selfBacked)
            {
                return GameText.Format("logistics.block.head_on_self", fx, fy, GameText.Get(GridMath.DirTextKey((GridDir)info.Dir)), build, rotate);
            }
            return GameText.Format("logistics.block.head_on_unsure", fx, fy, build, rotate);
        }

        /// <summary>
        /// <paramref name="cell"/> 身后那一格是从后方进料的件（传送带 / 分流器 / 地下入口），并且朝着离开 <paramref name="cell"/> 的方向——
        /// 也就是 <paramref name="cell"/> 原来在给它送料：<paramref name="cell"/> 是从一条线中间被转反的那一格。
        /// </summary>
        private static bool BackFlowsAway(BeltKernel kernel, in BeltCellInfo cell)
        {
            int back = BeltDirs.Opposite((int)cell.Dir);
            return kernel.TryGetCellInfo(cell.X + BeltDirs.Dx(back), cell.Y + BeltDirs.Dy(back), out BeltCellInfo b) && (int)b.Dir == back
                   && (b.Kind == BeltNodeKind.Belt || b.Kind == BeltNodeKind.Splitter || b.Kind == BeltNodeKind.UndergroundIn);
        }

        /// <summary>FG3-LOG-04：节点的角色名（堵塞原因“正前方的 X 不从这一侧进料”用）：分流器 / 合流器 / 地下传送带入口 / 地下传送带出口。</summary>
        public static string RoleName(BeltNodeKind kind)
        {
            switch (kind)
            {
                case BeltNodeKind.Splitter:
                    return GameText.Get("logistics.node.splitter");
                case BeltNodeKind.Merger:
                    return GameText.Get("logistics.node.merger");
                case BeltNodeKind.UndergroundIn:
                    return GameText.Get("logistics.underground.entrance_name");
                case BeltNodeKind.UndergroundOut:
                    return GameText.Get("logistics.underground.exit_name");
                default:
                    return TierName(0);
            }
        }

        /// <summary>FG3-LOG-04：节点从哪一侧进料（堵塞原因后半句，给出办法）。</summary>
        public static string SideRule(BeltNodeKind kind)
        {
            switch (kind)
            {
                case BeltNodeKind.Splitter:
                    return GameText.Get("logistics.side_rule.splitter");
                case BeltNodeKind.Merger:
                    return GameText.Get("logistics.side_rule.merger");
                case BeltNodeKind.UndergroundIn:
                    return GameText.Get("logistics.side_rule.under_in");
                default:
                    return GameText.Get("logistics.side_rule.under_out");
            }
        }

        /// <summary>
        /// FG3-LOG-04（FG03 第 5 节“分流器两个输出口都堵：分流器停止，上游堵塞，给出原因”）：分流器为什么停着——逐口写明（哪个口在哪一格满了 / 没接 / 不收 / 关闭）。
        /// </summary>
        private static string DescribeSplitterBlock(BeltKernel kernel, in BeltCellInfo cell)
        {
            if (kernel == null || !kernel.TryGetNodeInfo(cell.X, cell.Y, out BeltNodeInfo n))
            {
                return GameText.Get("logistics.block.none");
            }
            string detail = OutletText(n, true) + GameText.Get("logistics.outlet.separator") + OutletText(n, false);
            return cell.Block == BeltBlock.SplitterFull
                ? GameText.Format("logistics.block.splitter_full", detail)
                : GameText.Format("logistics.block.splitter_no_outlet", BeltItems.Name(n.HeadItem), detail);
        }

        /// <summary>FG3-LOG-04：分流器一个输出口此刻的状态文字（左 / 右）。</summary>
        public static string OutletText(in BeltNodeInfo n, bool left)
        {
            string side = GameText.Get(left ? "logistics.side.left" : "logistics.side.right");
            BeltOutletState st = left ? n.OutLState : n.OutRState;
            int x = left ? n.OutLX : n.OutRX;
            int y = left ? n.OutLY : n.OutRY;
            ushort filter = left ? n.FilterL : n.FilterR;
            switch (st)
            {
                case BeltOutletState.Disconnected:
                    int d = left ? BeltDirs.Left((int)n.Dir) : BeltDirs.Right((int)n.Dir);
                    return GameText.Format("logistics.outlet.disconnected", side, x, y, GameText.Get(GridMath.DirTextKey((GridDir)d)));
                case BeltOutletState.Full:
                    return GameText.Format("logistics.outlet.full", side, x, y);
                case BeltOutletState.Filtered:
                    if (filter == BeltConst.FilterNone)
                    {
                        return GameText.Format("logistics.outlet.closed", side);
                    }
                    // 这个口放全部物品，但头一件被另一个口“只放这种”专门收着（严格分拣）：写明是被另一口专收，而不是“不收”。
                    return filter == BeltConst.FilterAny
                        ? GameText.Format("logistics.outlet.reserved", side, BeltItems.Name(n.HeadItem))
                        : GameText.Format("logistics.outlet.filtered", side, BeltNodeService.FilterName(filter));
                default:
                    return GameText.Format("logistics.outlet.ok", side);
            }
        }

        private static string SinkOwnerName(int portId) =>
            portId >= 0 && SinkOwnerNameKeys.TryGetValue(portId, out string key) ? GameText.Get(key) : GameText.Format("logistics.block.sink_unnamed", portId);

        public static string TierName(int tier) => GameText.Get(tier == 0 ? "logistics.tier.t1" : tier == 1 ? "logistics.tier.t2" : "logistics.tier.t3");

        /// <summary>悬停摘要（FGR-LOG-081：等级、当前物品数、实测吞吐 / 设计吞吐、状态与原因）。正式悬停面板由 FG3-LOG-03 接。</summary>
        public static string DescribeCell(GridCell cell) => IsRunning ? DescribeCellOf(_kernel, cell.X, cell.Y) : null;

        /// <summary>任意内核上一格的悬停摘要（自检与后续多表面共用同一份格式）。</summary>
        public static string DescribeCellOf(BeltKernel kernel, int x, int y)
        {
            if (kernel == null || kernel.IsDisposed || !kernel.TryGetCellInfo(x, y, out BeltCellInfo info))
            {
                return null;
            }
            string state = DescribeBlock(kernel, info) + (info.InLoop ? GameText.Get("logistics.hover.loop") : string.Empty);
            // 第一个统计窗口（logistics.stats_bucket_steps 步）还没走完时写“统计中”，不显示误导的 0。
            string measured = info.WindowSeconds > 0f
                ? info.ThroughputPerMinute.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                : GameText.Get("logistics.hover.measuring");
            return GameText.Format("logistics.hover.summary", TierName(info.Tier), info.Count, measured, info.RatedItemsPerMinute, state);
        }

        /// <summary>
        /// FG3-LOG-03（FGR-LOG-081“传送带：当前物品、速度、吞吐”；FGR-LOG-025“悬停说明下游 X 已满”；FG00 B05 / B06 / B13）：悬停读数。
        /// 标题 = 等级与朝向；正文逐行：物品（按种类）、满载速度（天气减速写明设计值与减速比例；顶棚下写明）、实测吞吐（统计窗口）、
        /// 状态与原因（下游哪一格 / 哪座建筑）、耐久、起点 / 终点端口、所在网络、操作提示。O(1)：只读这一格的内核数据与网络汇总。
        /// </summary>
        public static bool TryDescribeHover(CampaignState state, GridCell cell, out string title, out string body)
        {
            title = null;
            body = null;
            if (!IsRunning || !ReferenceEquals(state, _state) || !_kernel.TryGetCellInfo(cell.X, cell.Y, out BeltCellInfo info))
            {
                return false;
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            // FG3-LOG-04：节点的标题写种类（分流器 / 合流器 / 地下传送带入口或出口），正文多出节点读数（比例、优先口、过滤、输出口、实测分出；跨度等）。
            BeltNodeKind kind = info.Kind;
            BeltNodeInfo node = default;
            bool isNode = kind != BeltNodeKind.Belt && _kernel.TryGetNodeInfo(cell.X, cell.Y, out node);
            string dirText = GameText.Get(GridMath.DirTextKey((GridDir)info.Dir));
            switch (kind)
            {
                case BeltNodeKind.Splitter:
                    title = GameText.Format("logistics.hover.splitter_title", dirText);
                    break;
                case BeltNodeKind.Merger:
                    title = GameText.Format("logistics.hover.merger_title", dirText);
                    break;
                case BeltNodeKind.UndergroundIn:
                case BeltNodeKind.UndergroundOut:
                    title = GameText.Format("logistics.hover.under_title", PieceName(kind, info.Tier),
                        GameText.Get(kind == BeltNodeKind.UndergroundIn ? "logistics.underground.entrance" : "logistics.underground.exit"), dirText);
                    break;
                default:
                    title = GameText.Format("logistics.hover.title", TierName(info.Tier), dirText);
                    break;
            }
            var sb = new System.Text.StringBuilder(256);
            if (info.Count == 0)
            {
                sb.Append(GameText.Get("logistics.hover.items_empty"));
            }
            else
            {
                var counts = new SortedDictionary<ushort, int>();
                for (int i = 0; i < info.Count; i++)
                {
                    ushort it = info.ItemAt(i);
                    counts[it] = counts.TryGetValue(it, out int n) ? n + 1 : 1;
                }
                sb.Append(GameText.Format("logistics.hover.items", BeltItems.FormatCounts(counts)));
            }
            sb.Append('\n');
            sb.Append(info.SlowPercent > 0
                ? GameText.Format("logistics.hover.speed_slowed", info.RatedItemsPerMinute, info.TierItemsPerMinute, info.SlowPercent)
                : GameText.Format("logistics.hover.speed", info.RatedItemsPerMinute));
            if (info.Covered)
            {
                sb.Append('\n').Append(GameText.Get("logistics.hover.covered"));
            }
            sb.Append('\n');
            if (info.WindowSeconds > 0f)
            {
                sb.Append(GameText.Format("logistics.hover.throughput", info.ThroughputPerMinute.ToString("0.#", ci), info.WindowSeconds.ToString("0", ci)));
            }
            else
            {
                float windowSec = _kernel.Config.BucketSteps / (float)Math.Max(1, _kernel.Config.StepHz);
                sb.Append(GameText.Format("logistics.hover.throughput_measuring", windowSec.ToString("0", ci)));
            }
            sb.Append('\n').Append(GameText.Format("logistics.hover.state", DescribeBlock(info) + (info.InLoop ? GameText.Get("logistics.hover.loop") : string.Empty)));
            if (isNode)
            {
                AppendNodeLines(sb, node);
            }
            sb.Append('\n').Append(GameText.Format("logistics.hover.hp", HpOf(cell), MaxHp(info.Tier)));
            if (BeltPortService.TryFindSourceAt(cell, out BeltPortService.Binding src))
            {
                sb.Append('\n').Append(GameText.Format("logistics.hover.from_port", BeltPortService.BuildingName(src.BuildingId)));
            }
            if (info.SinkPortId >= 0)
            {
                sb.Append('\n').Append(GameText.Format("logistics.hover.to_port", SinkOwnerName(info.SinkPortId)));
            }
            if (_kernel.TryGetNetworkStats(info.Network, out BeltNetworkStats net))
            {
                sb.Append('\n').Append(GameText.Format("logistics.hover.network", net.Cells, net.Items, net.BlockedCells,
                    net.HasCycle ? GameText.Get("logistics.hover.network_loop") : string.Empty));
            }
            if (info.Count > 0)
            {
                sb.Append('\n').Append(GameText.Get("logistics.hover.placeholder_item"));
            }
            if (isNode)
            {
                sb.Append('\n').Append(GameText.Get("logistics.hover.placeholder_node"));
            }
            string actions = !isNode ? "logistics.hover.actions"
                : kind == BeltNodeKind.UndergroundIn || kind == BeltNodeKind.UndergroundOut ? "logistics.hover.under_actions" : "logistics.hover.node_actions";
            sb.Append('\n').Append(InputDisplay.ExpandActionTokens(GameText.Get(actions)));
            body = sb.ToString();
            return true;
        }

        /// <summary>
        /// FG3-LOG-04（FGR-LOG-081；卡片“分流比例在悬停中显示”）：节点读数的悬停行——
        /// 分流器：比例（设了优先口时写明比例不起作用）、优先输出口、左右口过滤、两个输出口此刻的状态、实测分出（最近窗口与累计）、进料；
        /// 合流器：优先输入口与规则、两侧进料是否接上；地下传送带：跨度 / 上限、入口 → 出口、两端与地下段上的件数。
        /// </summary>
        public static void AppendNodeLines(System.Text.StringBuilder sb, in BeltNodeInfo n)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (n.Kind)
            {
                case BeltNodeKind.Splitter:
                {
                    sb.Append('\n').Append(GameText.Format(n.PriorityOut != BeltSide.None ? "logistics.hover.split_ratio_ignored" : "logistics.hover.split_ratio", n.RatioL, n.RatioR));
                    sb.Append('\n').Append(GameText.Format("logistics.hover.split_priority", BeltNodeService.SideName(n.PriorityOut)));
                    sb.Append('\n').Append(GameText.Format("logistics.hover.split_filters", BeltNodeService.FilterName(n.FilterL), BeltNodeService.FilterName(n.FilterR)));
                    sb.Append('\n').Append(GameText.Format("logistics.hover.split_outlets",
                        OutletText(n, true) + GameText.Get("logistics.outlet.separator") + OutletText(n, false)));
                    sb.Append('\n').Append(n.WindowSeconds > 0f
                        ? GameText.Format("logistics.hover.split_measured", n.SentLPerMinute.ToString("0.#", ci), n.SentRPerMinute.ToString("0.#", ci),
                            n.WindowSeconds.ToString("0", ci), n.SentL, n.SentR)
                        : GameText.Format("logistics.hover.split_measuring", n.SentL, n.SentR));
                    AppendInputLine(sb, n);
                    AppendDirectContact(sb, n, BeltNodeKind.Merger, "logistics.hover.split_direct_merger");
                    break;
                }
                case BeltNodeKind.Merger:
                {
                    string rule = n.PriorityIn == BeltSide.None
                        ? GameText.Get("logistics.hover.merge_alternate")
                        : GameText.Format("logistics.hover.merge_first", BeltNodeService.SideName(n.PriorityIn));
                    sb.Append('\n').Append(GameText.Format("logistics.hover.merge_priority", BeltNodeService.SideName(n.PriorityIn), rule));
                    sb.Append('\n').Append(GameText.Format("logistics.hover.merge_inputs",
                        GameText.Get(n.InLConnected ? "logistics.hover.connected" : "logistics.hover.not_connected"),
                        GameText.Get(n.InRConnected ? "logistics.hover.connected" : "logistics.hover.not_connected")));
                    AppendDirectContact(sb, n, BeltNodeKind.Splitter, "logistics.hover.merge_direct_splitter");
                    break;
                }
                default:
                {
                    int max = UndergroundSpan(n.Tier);
                    sb.Append('\n').Append(GameText.Format("logistics.hover.under_span", n.Distance - 1, PieceName(n.Kind, n.Tier), max,
                        n.EntranceX, n.EntranceY, n.ExitX, n.ExitY));
                    sb.Append('\n').Append(n.Intact
                        ? GameText.Format("logistics.hover.under_inside", n.ItemsInside, n.Capacity)
                        : GameText.Get("logistics.hover.under_broken"));
                    if (n.Kind == BeltNodeKind.UndergroundIn)
                    {
                        AppendInputLine(sb, n);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// FG3-E2E-01（DEBT-FG3LOG04-05）：分流器的输出口直接顶着合流器的侧面（中间不隔传送带）时，分流器交过去的物品按“放上入口”的规则走，
        /// 不参与合流器的交替 / 优先口（ADR-LOG-004）。两边的悬停各写一行说明与办法（中间隔一格传送带就按规则汇入）。O(1)：只看左右两格。
        /// </summary>
        private static void AppendDirectContact(System.Text.StringBuilder sb, in BeltNodeInfo n, BeltNodeKind other, string key)
        {
            if (_kernel == null)
            {
                return;
            }
            for (int s = 0; s < 2; s++)
            {
                int d = s == 0 ? BeltDirs.Left((int)n.Dir) : BeltDirs.Right((int)n.Dir);
                int x = n.X + BeltDirs.Dx(d);
                int y = n.Y + BeltDirs.Dy(d);
                if (!_kernel.TryGetKind(x, y, out BeltNodeKind k) || k != other || !_kernel.TryGetNodeInfo(x, y, out BeltNodeInfo m))
                {
                    continue;
                }
                int ml = BeltDirs.Left((int)m.Dir);
                int mr = BeltDirs.Right((int)m.Dir);
                bool touching = (m.X + BeltDirs.Dx(ml) == n.X && m.Y + BeltDirs.Dy(ml) == n.Y) || (m.X + BeltDirs.Dx(mr) == n.X && m.Y + BeltDirs.Dy(mr) == n.Y);
                if (touching)
                {
                    sb.Append('\n').Append(GameText.Format(key, BeltNodeService.SideName(s == 0 ? BeltSide.Left : BeltSide.Right), x, y));
                }
            }
        }

        private static void AppendInputLine(System.Text.StringBuilder sb, in BeltNodeInfo n)
        {
            if (n.InputConnected)
            {
                sb.Append('\n').Append(GameText.Get("logistics.hover.input_ok"));
                return;
            }
            int back = BeltDirs.Opposite((int)n.Dir);
            sb.Append('\n').Append(GameText.Format("logistics.hover.input_missing", n.X + BeltDirs.Dx(back), n.Y + BeltDirs.Dy(back),
                GameText.Get(GridMath.DirTextKey((GridDir)n.Dir))));
        }

        // ── 渲染（观察星球表面时每帧一次）──────────────────────────────────────────

        /// <summary>内核步之间的插值比例（距上一内核步经过的世界步 / 每个内核步对应的世界步）。</summary>
        public static float InterpolationAlpha
        {
            get
            {
                if (!IsRunning)
                {
                    return 1f;
                }
                double worldPerBelt = GameClock.StepHz / (double)Math.Max(1, _kernel.Config.StepHz);
                return Mathf.Clamp01((float)((GameClock.Ticks - _lastBeltTick) / Math.Max(1.0, worldPerBelt)));
            }
        }

        public static void Render(Camera camera)
        {
            if (!IsRunning || camera == null)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            _renderer ??= new BeltRenderer();
            // FG3-LOG-03：箭头 / 流动条纹按游戏时钟滚动（暂停停住、倍速变快）。
            _renderer.AnimationTime = (float)((GameClock.Ticks + GameClock.StepAlpha) / Math.Max(1, GameClock.StepHz));
            _renderer.OverlayMode = GameLogic.View.OverlayService.BeltOverlayMode; // FG3-LOG-08：叠加层只改着色器参数（O(1)）。
            _renderer.Draw(_kernel, camera, camera.orthographic ? camera.orthographicSize : 0f, InterpolationAlpha, _renderSettings);
            LastRenderMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            RenderCalls++;
        }
    }
}
