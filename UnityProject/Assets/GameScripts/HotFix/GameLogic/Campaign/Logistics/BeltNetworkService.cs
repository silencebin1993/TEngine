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
        private static long _lastBeltTick;
        private static readonly List<int3> CellScratch = new List<int3>(256);
        private static readonly Dictionary<int, string> SinkOwnerNameKeys = new Dictionary<int, string>();

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
            LoadCount++;
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
            for (long k = from; k < to; k++)
            {
                _kernel.Step();
                KernelStepsThisSession++;
                _lastBeltTick = ticksBefore + 1;
            }
            if (!_blockedHookChecked && _kernel.BlockedCells > 0)
            {
                _blockedHookChecked = true;
                GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstBlocked);
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
            BeltResult r = _kernel.RemoveCell(cell.X, cell.Y, returned);
            if (r == BeltResult.Ok)
            {
                HomeGridService.MapFor(state).SetBelt(cell, 0);
            }
            return BeltOpResult.Kernel(r);
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
        public static BeltOpResult TryAddSource(CampaignState state, int portId, GridCell beltCell, ushort item, int intervalSteps, int pending, int owner = 0) =>
            CanEdit(state, out BeltOpResult refuse) ? BeltOpResult.Kernel(_kernel.AddSource(portId, beltCell.X, beltCell.Y, item, intervalSteps, pending, owner)) : refuse;

        /// <summary>登记建筑的输入端口（位于建筑格 <paramref name="buildingCell"/>）；<paramref name="ownerNameKey"/> 用于堵塞原因“下游 X 的输入已满”（随存档保存）。</summary>
        public static BeltOpResult TryAddSink(CampaignState state, int portId, GridCell buildingCell, int bufferCap, int consumeIntervalSteps,
            string ownerNameKey = null, int owner = 0)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            BeltResult r = _kernel.AddSink(portId, buildingCell.X, buildingCell.Y, bufferCap, consumeIntervalSteps, owner);
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

        public static BeltOpResult TryRemovePort(CampaignState state, int portId)
        {
            if (!CanEdit(state, out BeltOpResult refuse))
            {
                return refuse;
            }
            BeltResult r = _kernel.RemovePort(portId);
            if (r == BeltResult.Ok)
            {
                SinkOwnerNameKeys.Remove(portId);
            }
            return BeltOpResult.Kernel(r);
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
                default: return "logistics.reason.invalid_argument";
            }
        }

        /// <summary>一格为什么停着（稳定文本；不只靠颜色）。</summary>
        public static string DescribeBlock(in BeltCellInfo info)
        {
            switch (info.Block)
            {
                case BeltBlock.EndOfBelt:
                    return GameText.Get("logistics.block.end_of_belt");
                case BeltBlock.DownstreamFull:
                    return GameText.Format("logistics.block.downstream_full", info.NextX, info.NextY);
                case BeltBlock.SinkFull:
                    string owner = info.SinkPortId >= 0 && SinkOwnerNameKeys.TryGetValue(info.SinkPortId, out string key) ? GameText.Get(key)
                        : GameText.Format("logistics.block.sink_unnamed", info.SinkPortId);
                    return GameText.Format("logistics.block.sink_full", owner);
                case BeltBlock.MergeWait:
                    return GameText.Format("logistics.block.merge_wait", info.NextX, info.NextY);
                default:
                    return GameText.Get("logistics.block.none");
            }
        }

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
            string state = DescribeBlock(info) + (info.InLoop ? GameText.Get("logistics.hover.loop") : string.Empty);
            // 第一个统计窗口（logistics.stats_bucket_steps 步）还没走完时写“统计中”，不显示误导的 0。
            string measured = info.WindowSeconds > 0f
                ? info.ThroughputPerMinute.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                : GameText.Get("logistics.hover.measuring");
            return GameText.Format("logistics.hover.summary", TierName(info.Tier), info.Count, measured, info.RatedItemsPerMinute, state);
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
            _renderer.Draw(_kernel, camera, camera.orthographic ? camera.orthographicSize : 0f, InterpolationAlpha, _renderSettings);
            LastRenderMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            RenderCalls++;
        }
    }
}
