using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Logistics
{
    /// <summary>一次管线编辑的结果：成功，或一条稳定的原因（文本键 + 参数，B06）。</summary>
    public readonly struct PipeOpResult
    {
        public readonly bool Ok;
        public readonly PipeResult Code;
        public readonly GridPlacementResult Grid;
        public readonly string ReasonKey;
        public readonly string[] Args;

        public PipeOpResult(bool ok, PipeResult code, GridPlacementResult grid, string key, string[] args)
        {
            Ok = ok;
            Code = code;
            Grid = grid;
            ReasonKey = key;
            Args = args ?? Array.Empty<string>();
        }

        public static readonly PipeOpResult Success = new PipeOpResult(true, PipeResult.Ok, null, null, null);

        public static PipeOpResult Kernel(PipeResult code) =>
            code == PipeResult.Ok ? Success : new PipeOpResult(false, code, null, PipeNetworkService.ReasonKey(code), null);

        public static PipeOpResult FromGrid(GridPlacementResult grid) =>
            new PipeOpResult(false, PipeResult.InvalidArgument, grid, grid.Reasons[0].TextKey, grid.Reasons[0].Args);

        public static PipeOpResult Reason(PipeResult code, string key, params string[] args) => new PipeOpResult(false, code, null, key, args);

        public static PipeOpResult NotRunning => new PipeOpResult(false, PipeResult.InvalidArgument, null, "logistics.pipe.reason.not_running", null);

        public static PipeOpResult SavePreserved => new PipeOpResult(false, PipeResult.InvalidArgument, null, "logistics.pipe.reason.save_preserved", null);

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
    /// FG3-LOG-05（FG03 FGR-LOG-040～045）：管线内核在热更层的<b>唯一入口</b>——持有家园所在表面的 <see cref="PipeKernel"/>（AOT），
    /// 负责读表组装配置、世界步节拍、存读档、格网管线层、编辑（格网规则 + 流体规则）、悬停读数与渲染。
    ///
    /// 性能纪律（CLAUDE.md 热更层每帧 O(1)）：每个世界步只按节拍调一次 <see cref="PipeKernel.Step"/>（O(网络 + 泵 + 储罐 + 阀门 + 消费者)，AOT）；
    /// 拓扑只在编辑之后重算（参照电网子网“拓扑变化时才重算”）；逐格循环（重算、格网层套用、渲染缓冲、存档）只在 AOT 内核里或只在建图 / 存档时发生。
    /// 悬停读数 O(1)（只读这一格与它所在网络的汇总）。
    /// </summary>
    public static partial class PipeNetworkService
    {
        private static PipeKernel _kernel;
        private static PipeRenderer _renderer;
        private static CampaignState _state;
        private static PipeRenderer.Settings _renderSettings;
        private static bool _preserveSaved;
        private static bool _noSupplyHookChecked;
        private static readonly Dictionary<int, GameConfig.fg.Fluid> Fluids = new Dictionary<int, GameConfig.fg.Fluid>();
        private static readonly Dictionary<byte, int> FluidOfTerrain = new Dictionary<byte, int>();
        private static int _fluidTableRevision = -1;

        public static PipeKernel Kernel => _kernel;

        private static long _stockStep = -1;
        private static int _stockRev = -1;
        private static PipeKernel _stockKernel;
        private static readonly Dictionary<int, long> StockMl = new Dictionary<int, long>();
        private static readonly Dictionary<int, int> StockNets = new Dictionary<int, int>();

        /// <summary>
        /// FG4-ECO-10（关闭 DEBT-FG4ECO08-03“资源顶栏上流体的库存写‘管线’，没有储罐存量合计”）：某种流体在全部储罐里的存量合计（毫升），
        /// <paramref name="networks"/> = 装着它的网络数。按内核步 / 拓扑版本缓存：同一步里多次查询只汇总一次（O(网络数)），顶栏按节流刷新调用，不按帧。
        /// 管线服务没运行时返回 -1。
        /// </summary>
        public static long FluidStockMl(int fluidId, out int networks)
        {
            networks = 0;
            if (_kernel == null)
            {
                return -1;
            }
            if (!ReferenceEquals(_stockKernel, _kernel) || _stockStep != _kernel.StepIndex || _stockRev != _kernel.Revision)
            {
                StockMl.Clear();
                StockNets.Clear();
                int n = _kernel.NetworkCount;
                for (int i = 0; i < n; i++)
                {
                    if (!_kernel.TryGetNetworkInfo(i, out PipeNetInfo info) || info.Fluid <= 0 || info.Tanks <= 0)
                    {
                        continue;
                    }
                    StockMl[info.Fluid] = (StockMl.TryGetValue(info.Fluid, out long ml) ? ml : 0L) + info.StoredMl;
                    StockNets[info.Fluid] = (StockNets.TryGetValue(info.Fluid, out int c) ? c : 0) + 1;
                }
                _stockKernel = _kernel;
                _stockStep = _kernel.StepIndex;
                _stockRev = _kernel.Revision;
            }
            networks = StockNets.TryGetValue(fluidId, out int nets) ? nets : 0;
            return StockMl.TryGetValue(fluidId, out long total) ? total : 0L;
        }
        public static bool IsRunning => _kernel != null;
        public static CampaignState BoundState => _state;
        public static PipeRenderer Renderer => _renderer;
        public static PipeRenderer.Settings RenderSettings => _renderSettings;
        public static bool SavedDataPreserved => _preserveSaved;
        public static string LastLoadError { get; private set; }
        public static int LoadCount { get; private set; }
        public static long KernelStepsThisSession { get; private set; }
        public static double LastRenderMs { get; private set; }
        public static int RenderCalls { get; private set; }
        public static int GridLayerApplyCount { get; private set; }

        // ── 配置 ───────────────────────────────────────────────────────────────

        public static PipeConfig ReadConfig()
        {
            var c = new PipeConfig
            {
                StepHz = TuningInt("logistics.step_hz", 20),
                LitersPerMinuteT1 = TuningInt("logistics.pipe.lpm_t1", 600),
                LitersPerMinuteT2 = TuningInt("logistics.pipe.lpm_t2", 1200),
                PumpLitersPerMinute = TuningInt("logistics.pipe.pump_lpm", 300),
                TankLiters = TuningInt("logistics.pipe.tank_liters", 5000),
                TankLitersPerMinute = TuningInt("logistics.pipe.tank_lpm", 1200),
                ValveLitersPerMinute = TuningInt("logistics.pipe.valve_lpm", 1200),
                // FG4-ECO-04（FG-GAP-082）：地下管线 T1 / T2 的跨度（两口之间最多几格）。
                UndergroundSpanT1 = TuningInt("logistics.pipe.underground_span_t1", 8),
                UndergroundSpanT2 = TuningInt("logistics.pipe.underground_span_t2", 12),
            };
            if (!c.IsValid(out string why))
            {
                Log.Error($"[PipeNetworkService] logistics.pipe.* 调参非法（{why}），回落到规格初值。");
                c = PipeConfig.Default;
            }
            return c;
        }

        public static PipeRenderer.Settings ReadRenderSettings() => new PipeRenderer.Settings
        {
            CellSize = 1f,
            Height = TuningFloat("logistics.pipe.render.height", 0.05f),
            IconEvery = Math.Max(1, TuningInt("logistics.pipe.icon_every", 6)),
        };

        private static int TuningInt(string id, int fallback) => (int)Math.Round(TuningFloat(id, fallback));

        private static float TuningFloat(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            Log.Error($"[PipeNetworkService] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_pipes.py 后重新生成）。");
            return fallback;
        }

        // ── 流体表（GameConfig.fg.TbFluid）────────────────────────────────────────────────────

        private static void EnsureFluids()
        {
            if (_fluidTableRevision == GridContent.Revision && Fluids.Count > 0)
            {
                return;
            }
            Fluids.Clear();
            FluidOfTerrain.Clear();
            GameConfig.fg.TbFluid table = ConfigSystem.Instance?.Tables?.TbFluid;
            if (table != null)
            {
                foreach (GameConfig.fg.Fluid f in table.DataList)
                {
                    if (f == null || f.Id < 1 || f.Id > PipeConst.MaxFluid)
                    {
                        continue;
                    }
                    Fluids[f.Id] = f;
                    if (!string.IsNullOrEmpty(f.SourceTerrain) && f.SourceTerrain != "none" && GridContent.TryTerrainCode(f.SourceTerrain, out byte code)
                        && !FluidOfTerrain.ContainsKey(code))
                    {
                        FluidOfTerrain[code] = f.Id;
                    }
                }
            }
            _fluidTableRevision = GridContent.Revision;
        }

        /// <summary>表里的全部流体（按排序）。</summary>
        public static void CollectFluids(List<GameConfig.fg.Fluid> into)
        {
            EnsureFluids();
            into.Clear();
            into.AddRange(Fluids.Values);
            into.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : a.Id.CompareTo(b.Id));
        }

        /// <summary>流体键（water / crude …）→ 编号（没有 = 0）。</summary>
        public static int FluidId(string key)
        {
            EnsureFluids();
            foreach (KeyValuePair<int, GameConfig.fg.Fluid> kv in Fluids)
            {
                if (kv.Value.Key == key)
                {
                    return kv.Key;
                }
            }
            return 0;
        }

        /// <summary>流体的玩家名（0 = “没有流体”）。</summary>
        public static string FluidName(int id)
        {
            if (id <= 0)
            {
                return GameText.Get("fluid.none");
            }
            EnsureFluids();
            return Fluids.TryGetValue(id, out GameConfig.fg.Fluid f) ? GameText.Get(f.NameKey) : GameText.Format("fluid.unknown", id);
        }

        /// <summary>某种地形上的泵抽什么流体（0 = 这种地形不是流体来源）。</summary>
        public static int SourceFluidOfTerrain(byte terrainCode)
        {
            EnsureFluids();
            return FluidOfTerrain.TryGetValue(terrainCode, out int id) ? id : 0;
        }

        public static int SourceFluidAt(CampaignState state, GridCell cell)
        {
            if (state == null)
            {
                return 0;
            }
            HomeGridMap.Chunk chunk = HomeGridService.MapFor(state).ChunkAt(cell, out int idx);
            return SourceFluidOfTerrain(chunk.Terrain[idx]);
        }

        private static Color ParseColor(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.gray;
        }

        private static int GlyphIndex(string glyph)
        {
            switch (glyph)
            {
                case "drop": return 0;
                case "dot": return 1;
                case "cross": return 2;
                case "triangle": return 3;
                default: return 4;
            }
        }

        // ── 生命周期 ─────────────────────────────────────────────────────────────

        /// <summary>接入战役的星球表面（WorldSimulation.LoadHome）：新建内核，从 <see cref="PipeFluidState"/> 恢复，同步格网管线层。</summary>
        public static void Load(CampaignState state)
        {
            Unload();
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            EnsureFluids();
            _kernel = new PipeKernel(ReadConfig());
            _state = state;
            _renderSettings = ReadRenderSettings();
            _preserveSaved = false;
            _noSupplyHookChecked = false;
            LastLoadError = null;
            KernelStepsThisSession = 0;
            PipeFluidState saved = state.Pipes;
            if (saved.FormatVersion > 0)
            {
                PipeSnapshot snap = ToSnapshot(saved);
                if (!_kernel.Deserialize(snap, out string err))
                {
                    // 整体不可用（格式不认识 / 数据形状不对）：内核留空，存档里的原始数据原样保留（不被下一次存档覆盖），这局的管线编辑一律拒绝并给原因。
                    _preserveSaved = true;
                    LastLoadError = err;
                    Log.Error($"[PipeNetworkService] 管线存档无法读取（{err}），原始数据保留不覆盖，本局管线编辑停用");
                    NotificationCenter.Post("save_migrated", GameText.Format("logistics.pipe.load.unreadable", err, saved.Xs?.Length ?? 0));
                }
                else if (_kernel.DroppedOnLoad > 0)
                {
                    LastLoadError = err;
                    Log.Warning($"[PipeNetworkService] 管线存档有 {_kernel.DroppedOnLoad} 条记录损坏已丢弃");
                    NotificationCenter.Post("save_migrated", GameText.Format("logistics.pipe.load.dropped", _kernel.DroppedOnLoad));
                }
                if (_kernel.ClampedOnLoadMl > 0)
                {
                    // 储罐容量调低后读老存档：存量夹到新容量（不丢储罐），倒掉的量写明。
                    Log.Warning($"[PipeNetworkService] 储罐存量超过当前容量，已夹到容量，倒掉 {_kernel.ClampedOnLoadMl} 毫升");
                    NotificationCenter.Post("save_migrated", GameText.Format("logistics.pipe.load.clamped", Liters(_kernel.ClampedOnLoadMl)));
                }
            }
            ApplyGridLayer(state, HomeGridService.MapFor(state));
            RestoreDamage(state); // FG6-DEF-05：管线件的耐久（只恢复内核里确实存在的格）
            LoadCount++;
        }

        /// <summary>卸载：GPU 资源成对释放。不写存档（存档由 SyncAllForSave 负责）。</summary>
        public static void Unload()
        {
            _renderer?.Dispose();
            _renderer = null;
            _kernel = null;
            _state = null;
            _preserveSaved = false;
            DamageLost.Clear();
            LastHit.Clear();
        }

        /// <summary>把内核里的管线件写进格网管线层（派生缓存，真相在内核；建图时套回）。O(格数)，只在建图 / 读档时。</summary>
        public static void ApplyGridLayer(CampaignState state, HomeGridMap map)
        {
            if (!IsRunning || map == null || state == null || !ReferenceEquals(state, _state))
            {
                return;
            }
            int n = _kernel.CellCount;
            for (int i = 0; i < n; i++)
            {
                _kernel.GetCellAt(i, out int x, out int y, out PipePieceKind kind, out int tier);
                map.SetPipe(new GridCell(x, y), LayerValue(kind, tier));
            }
            GridLayerApplyCount++;
        }

        /// <summary>格网管线层里的值：1 + 等级 + 2 × 种类（1～8）；规划中的虚影另带“规划”位（HomeValleyConstruction.PlannedBeltFlag）。</summary>
        public static ushort LayerValue(PipePieceKind kind, int tier) => (ushort)(1 + Math.Max(0, Math.Min(1, tier)) + 2 * (int)kind);

        // ── 固定步长 ─────────────────────────────────────────────────────────────

        /// <summary>世界模拟的一个固定步（与传送带同一节拍：内核步数 = ⌊(步序号 + 1) × 内核频率 / 世界频率⌋ − ⌊步序号 × 内核频率 / 世界频率⌋）。
        /// 只由绝对步序号决定：暂停不走、倍速只是一帧多走几步、读档后接着同一节拍、与有没有人观察无关（FGR-BASE-021）。</summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (!IsRunning || !ReferenceEquals(state, _state) || worldHz <= 0)
            {
                return;
            }
            int hz = _kernel.Config.StepHz;
            long from = ticksBefore * hz / worldHz;
            long to = (ticksBefore + 1) * hz / worldHz;
            for (long k = from; k < to; k++)
            {
                _kernel.Step();
                KernelStepsThisSession++;
            }
            if (!_noSupplyHookChecked && _kernel.LastNoSupplyNetworks > 0)
            {
                _noSupplyHookChecked = true;
                GuidanceHooks.Raise(GuidanceHooks.LogisticsPipeFirstNoSupply);
            }
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────

        public static void WriteTo(CampaignState state)
        {
            if (!IsRunning || state == null || !ReferenceEquals(state, _state) || _preserveSaved)
            {
                return;
            }
            PipeSnapshot s = _kernel.Serialize();
            PipeFluidState p = state.Pipes ??= new PipeFluidState();
            p.DomainVersion = 2;
            p.FormatVersion = s.FormatVersion;
            p.KernelSteps = s.StepIndex;
            p.TotalPumpedMl = s.TotalPumpedMl;
            p.TotalDeliveredMl = s.TotalDeliveredMl;
            p.TotalFlushedMl = s.TotalFlushedMl;
            p.TotalRemovedMl = s.TotalRemovedMl;
            p.Xs = s.X;
            p.Ys = s.Y;
            p.Kinds = ToInts(s.Kind);
            p.Tiers = ToInts(s.Tier);
            p.Dirs = ToInts(s.Dir);
            p.Fluids = ToInts(s.Fluid);
            p.Stocks = s.Stock;
            p.Modes = ToInts(s.Mode);
            p.Priorities = ToInts(s.Priority);
            p.Open = ToInts(s.Open);
            p.Buffers = s.Buffer;
            p.BufferFluids = ToInts(s.BufferFluid);
            p.LastFlow = s.LastFlow;
            p.PumpTotals = s.PumpTotal;
            p.ConsumerIds = s.ConsumerId;
            p.ConsumerXs = s.ConsumerX;
            p.ConsumerYs = s.ConsumerY;
            p.ConsumerFluids = ToInts(s.ConsumerFluid);
            p.ConsumerLpm = s.ConsumerLpm;
            p.ConsumerPriorities = ToInts(s.ConsumerPriority);
            p.ConsumerTotals = s.ConsumerTotal;
            p.NextConsumerId = s.NextConsumerId;
            p.ConsumerBuffers = s.ConsumerBuffer;
            p.ConsumerCapacities = s.ConsumerCapacity;
            p.ProducerIds = s.ProducerId;
            p.ProducerXs = s.ProducerX;
            p.ProducerYs = s.ProducerY;
            p.ProducerFluids = ToInts(s.ProducerFluid);
            p.ProducerStocks = s.ProducerStock;
            p.ProducerCapacities = s.ProducerCapacity;
            p.ProducerTotals = s.ProducerTotal;
            p.NextProducerId = s.NextProducerId;
            p.TotalProducedOutMl = s.TotalProducedOutMl;
        }

        public static PipeSnapshot ToSnapshot(PipeFluidState p) => new PipeSnapshot
        {
            FormatVersion = p.FormatVersion,
            StepIndex = p.KernelSteps,
            TotalPumpedMl = p.TotalPumpedMl,
            TotalDeliveredMl = p.TotalDeliveredMl,
            TotalFlushedMl = p.TotalFlushedMl,
            TotalRemovedMl = p.TotalRemovedMl,
            X = p.Xs ?? Array.Empty<int>(),
            Y = p.Ys ?? Array.Empty<int>(),
            Kind = ToBytes(p.Kinds),
            Tier = ToBytes(p.Tiers),
            Dir = ToBytes(p.Dirs),
            Fluid = ToBytes(p.Fluids),
            Stock = p.Stocks ?? Array.Empty<long>(),
            Mode = ToBytes(p.Modes),
            Priority = ToBytes(p.Priorities),
            Open = ToBytes(p.Open),
            Buffer = p.Buffers ?? Array.Empty<long>(),
            BufferFluid = ToBytes(p.BufferFluids),
            LastFlow = p.LastFlow ?? Array.Empty<long>(),
            PumpTotal = p.PumpTotals ?? Array.Empty<long>(),
            ConsumerId = p.ConsumerIds ?? Array.Empty<int>(),
            ConsumerX = p.ConsumerXs ?? Array.Empty<int>(),
            ConsumerY = p.ConsumerYs ?? Array.Empty<int>(),
            ConsumerFluid = ToBytes(p.ConsumerFluids),
            ConsumerLpm = p.ConsumerLpm ?? Array.Empty<int>(),
            ConsumerPriority = ToBytes(p.ConsumerPriorities),
            ConsumerTotal = p.ConsumerTotals ?? Array.Empty<long>(),
            NextConsumerId = p.NextConsumerId,
            ConsumerBuffer = p.ConsumerBuffers ?? Array.Empty<long>(),
            ConsumerCapacity = p.ConsumerCapacities ?? Array.Empty<long>(),
            ProducerId = p.ProducerIds ?? Array.Empty<int>(),
            ProducerX = p.ProducerXs ?? Array.Empty<int>(),
            ProducerY = p.ProducerYs ?? Array.Empty<int>(),
            ProducerFluid = ToBytes(p.ProducerFluids),
            ProducerStock = p.ProducerStocks ?? Array.Empty<long>(),
            ProducerCapacity = p.ProducerCapacities ?? Array.Empty<long>(),
            ProducerTotal = p.ProducerTotals ?? Array.Empty<long>(),
            NextProducerId = p.NextProducerId,
            TotalProducedOutMl = p.TotalProducedOutMl,
        };

        private static int[] ToInts(byte[] b)
        {
            var r = new int[b?.Length ?? 0];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = b[i];
            }
            return r;
        }

        /// <summary>整数 → 字节；越界的值记成 255（内核逐条校验后丢弃那一条，不会被截断成合法值）。</summary>
        private static byte[] ToBytes(int[] a)
        {
            var r = new byte[a?.Length ?? 0];
            for (int i = 0; i < r.Length; i++)
            {
                int v = a[i];
                r[i] = v < 0 || v > 254 ? (byte)255 : (byte)v;
            }
            return r;
        }

        // ── 编辑 ─────────────────────────────────────────────────────────────────

        private static bool Bound(CampaignState state) => IsRunning && state != null && ReferenceEquals(state, _state);

        private static bool CanEdit(CampaignState state, out PipeOpResult refuse)
        {
            if (!Bound(state))
            {
                refuse = PipeOpResult.NotRunning;
                return false;
            }
            if (_preserveSaved)
            {
                refuse = PipeOpResult.SavePreserved;
                return false;
            }
            refuse = PipeOpResult.Success;
            return true;
        }

        public static string ReasonKey(PipeResult code)
        {
            switch (code)
            {
                case PipeResult.Occupied: return "logistics.pipe.reason.occupied";
                case PipeResult.NotFound: return "logistics.pipe.reason.not_found";
                case PipeResult.InvalidTier: return "logistics.pipe.reason.invalid_tier";
                case PipeResult.InvalidDirection: return "logistics.pipe.reason.invalid_direction";
                case PipeResult.WrongKind: return "logistics.pipe.reason.wrong_kind";
                case PipeResult.InvalidPriority: return "logistics.pipe.reason.invalid_priority";
                case PipeResult.OutOfRange: return "logistics.pipe.reason.out_of_range";
                case PipeResult.InvalidFluid: return "logistics.pipe.reason.invalid_fluid";
                case PipeResult.FluidConflict: return "logistics.pipe.reason.fluid_conflict";
                case PipeResult.ValveChained: return "logistics.pipe.reason.valve_chained";
                default: return "logistics.pipe.reason.invalid_argument";
            }
        }

        /// <summary>流体冲突的原因（写明是哪两种流体；阀门另有一句）。第一次发引导钩子。</summary>
        public static PipeOpResult Conflict(PipePieceKind kind, int a, int b)
        {
            GuidanceHooks.Raise(GuidanceHooks.LogisticsPipeFirstFluidConflict);
            return PipeOpResult.Reason(PipeResult.FluidConflict,
                kind == PipePieceKind.Valve ? "logistics.pipe.reason.valve_conflict" : "logistics.pipe.reason.fluid_conflict", FluidName(a), FluidName(b));
        }

        /// <summary>件的玩家名：管线 T1 / T2、泵、储罐、阀门。</summary>
        public static string PieceName(PipePieceKind kind, int tier)
        {
            switch (kind)
            {
                case PipePieceKind.Pump: return GameText.Get("logistics.pipe.pump");
                case PipePieceKind.Tank: return GameText.Get("logistics.pipe.tank");
                case PipePieceKind.Valve: return GameText.Get("logistics.pipe.valve");
                case PipePieceKind.Underground: return GameText.Get(tier <= 0 ? "logistics.pipe.underground_t1" : "logistics.pipe.underground_t2");
                default: return GameText.Get(tier <= 0 ? "logistics.pipe.t1" : "logistics.pipe.t2");
            }
        }

        /// <summary>FG4-ECO-04（FG-GAP-082）：地下管线这一等级最多跨几格。</summary>
        public static int UndergroundSpan(int tier) => IsRunning ? _kernel.Config.UndergroundSpan(tier) : ReadConfig().UndergroundSpan(tier);

        /// <summary>FG4-ECO-04（FG-GAP-082）：放在 <paramref name="cell"/>、朝 <paramref name="dir"/> 的地下管线口会和哪一口配对（放置预览，不改状态）。</summary>
        public static bool TryPreviewUnderground(GridCell cell, int dir, int tier, out GridCell partner)
        {
            partner = default;
            if (!IsRunning || !_kernel.TryPreviewUnderground(cell.X, cell.Y, dir, tier, out int px, out int py))
            {
                return false;
            }
            partner = new GridCell(px, py);
            return true;
        }

        /// <summary>建造菜单工具种类 → 管线件（认不出来返回 false）。</summary>
        public static bool TryToolPiece(string toolKind, out PipePieceKind kind)
        {
            switch (toolKind)
            {
                case "pipe":
                    kind = PipePieceKind.Pipe;
                    return true;
                case "pump":
                    kind = PipePieceKind.Pump;
                    return true;
                case "tank":
                    kind = PipePieceKind.Tank;
                    return true;
                case "valve":
                    kind = PipePieceKind.Valve;
                    return true;
                case "pipe_underground":
                    kind = PipePieceKind.Underground;
                    return true;
                default:
                    kind = PipePieceKind.Pipe;
                    return false;
            }
        }

        public static bool TryGetPiece(GridCell cell, out PipePieceKind kind, out int tier)
        {
            kind = PipePieceKind.Pipe;
            tier = 0;
            return IsRunning && _kernel.TryGetKind(cell.X, cell.Y, out kind, out tier);
        }

        /// <summary>
        /// 放一件：先过格网规则（<see cref="HomeGridService.ValidatePipeCell"/>：占用、迷雾、地形、污染、泵的水源 / 油井），再过流体规则（内核：不能把两种流体接在一起），
        /// 成功后写格网管线层并发首次钩子。泵的流体 = 它脚下的来源地形。
        /// </summary>
        public static PipeOpResult TryPlace(CampaignState state, GridCell cell, PipePieceKind kind, int tier, int dir)
        {
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            if (tier < 0 || tier >= PipeConst.TierCount)
            {
                return PipeOpResult.Kernel(PipeResult.InvalidTier);
            }
            if (dir < 0 || dir > 3)
            {
                return PipeOpResult.Kernel(PipeResult.InvalidDirection);
            }
            GridPlacementResult check = HomeGridService.ValidatePipeCell(state, cell, kind);
            if (!check.Ok)
            {
                return PipeOpResult.FromGrid(check);
            }
            int source = kind == PipePieceKind.Pump ? SourceFluidAt(state, cell) : 0;
            PipeResult r = _kernel.CheckPlace(cell.X, cell.Y, kind, tier, dir, source, out int fa, out int fb);
            if (r == PipeResult.FluidConflict)
            {
                return Conflict(kind, fa, fb);
            }
            if (r == PipeResult.Ok)
            {
                r = _kernel.Place(cell.X, cell.Y, kind, tier, dir, source);
            }
            if (r != PipeResult.Ok)
            {
                return PipeOpResult.Kernel(r);
            }
            HomeGridService.MapFor(state).SetPipe(cell, LayerValue(kind, tier));
            GuidanceHooks.Raise(GuidanceHooks.LogisticsFirstPipe);
            return PipeOpResult.Success;
        }

        /// <summary>规划预览用：(cell) 放这件会不会接错流体（不改状态）。<paramref name="fluids"/> 累计一段路径会相连的各网络流体。</summary>
        public static void AddAdjacentFluids(GridCell cell, PipePieceKind kind, int dir, List<int> fluids, int tier = 0, bool tunnel = true)
        {
            if (IsRunning)
            {
                _kernel.AddAdjacentFluids(cell.X, cell.Y, kind, dir, fluids, tier, tunnel);
            }
        }

        public static PipeResult CheckValve(GridCell cell, int dir, out int fa, out int fb)
        {
            fa = 0;
            fb = 0;
            return IsRunning ? _kernel.CheckPlace(cell.X, cell.Y, PipePieceKind.Valve, 0, dir, 0, out fa, out fb) : PipeResult.Ok;
        }

        /// <summary>
        /// 拆一件：储罐存量 / 阀门缓冲随之排空（<paramref name="lostMl"/>）。
        /// FG4-ECO-04 修复轮（P1）：拆掉地下管线口会让被它隔开的两口重新配对、把两种流体接在一起时拒绝，原因写明是哪两种流体（FGR-LOG-047）。
        /// </summary>
        public static PipeOpResult TryRemove(CampaignState state, GridCell cell, out long lostMl)
        {
            lostMl = 0;
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            if (_kernel.CheckRemove(cell.X, cell.Y, out int fa, out int fb) == PipeResult.FluidConflict)
            {
                GuidanceHooks.Raise(GuidanceHooks.LogisticsPipeFirstFluidConflict);
                return PipeOpResult.Reason(PipeResult.FluidConflict, "logistics.pipe.reason.remove_relinks_conflict", FluidName(fa), FluidName(fb));
            }
            PipeResult r = _kernel.Remove(cell.X, cell.Y, out lostMl);
            if (r == PipeResult.Ok)
            {
                HomeGridService.MapFor(state).SetPipe(cell, 0);
                if (DamageLost.ContainsKey(cell))
                {
                    SetDamage(state, cell, 0); // FG6-DEF-05：件没了，耐久记录一并删掉（同一格重放是新件、满耐久）
                }
            }
            return PipeOpResult.Kernel(r);
        }

        /// <summary>FG3-LOG-07（FGR-LOG-010）：原地升级一格管线（改等级，格网管线层同步写新值）。</summary>
        public static PipeOpResult TrySetTier(CampaignState state, GridCell cell, int tier)
        {
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            PipeResult r = _kernel.SetTier(cell.X, cell.Y, tier);
            if (r == PipeResult.Ok)
            {
                HomeGridService.MapFor(state).SetPipe(cell, LayerValue(PipePieceKind.Pipe, tier));
            }
            return PipeOpResult.Kernel(r);
        }

        /// <summary>FG4-ECO-10（DEBT-FG4ECO04-05）：一对已配对的地下管线口一起改等级（升级规划建成那一刻调用），两口的格网管线层随之换成新等级。</summary>
        public static PipeOpResult TrySetUndergroundTier(CampaignState state, GridCell cell, int tier)
        {
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            bool paired = _kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo info) && info.Kind == PipePieceKind.Underground && info.UndergroundLinked;
            PipeResult r = _kernel.SetUndergroundPairTier(cell.X, cell.Y, tier);
            if (r == PipeResult.Ok && paired)
            {
                HomeGridMap map = HomeGridService.MapFor(state);
                map.SetPipe(cell, LayerValue(PipePieceKind.Underground, tier));
                map.SetPipe(new GridCell(info.PartnerX, info.PartnerY), LayerValue(PipePieceKind.Underground, tier));
            }
            return PipeOpResult.Kernel(r);
        }

        public static PipeOpResult TrySetTankMode(CampaignState state, GridCell cell, PipeTankMode mode) =>
            CanEdit(state, out PipeOpResult refuse) ? PipeOpResult.Kernel(_kernel.SetTankMode(cell.X, cell.Y, mode)) : refuse;

        public static PipeOpResult TrySetTankPriority(CampaignState state, GridCell cell, int priority) =>
            CanEdit(state, out PipeOpResult refuse) ? PipeOpResult.Kernel(_kernel.SetTankPriority(cell.X, cell.Y, priority)) : refuse;

        public static PipeOpResult TrySetValveOpen(CampaignState state, GridCell cell, bool open) =>
            CanEdit(state, out PipeOpResult refuse) ? PipeOpResult.Kernel(_kernel.SetValveOpen(cell.X, cell.Y, open)) : refuse;

        public static PipeOpResult TryReverseValve(CampaignState state, GridCell cell)
        {
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            if (_kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo info) && info.Kind == PipePieceKind.Valve)
            {
                int a = NetFluid(info.ValveFrom);
                int b = NetFluid(info.ValveTo);
                PipeResult r = _kernel.ReverseValve(cell.X, cell.Y);
                return r == PipeResult.FluidConflict ? Conflict(PipePieceKind.Valve, a, b) : PipeOpResult.Kernel(r);
            }
            return PipeOpResult.Kernel(_kernel.ReverseValve(cell.X, cell.Y));
        }

        /// <summary>FGR-LOG-044：冲洗 (cell) 所在的网络（确认框由调用方负责：管线面板）。<paramref name="flushedMl"/> = 清掉的量。</summary>
        public static PipeOpResult TryFlush(CampaignState state, GridCell cell, out long flushedMl, out int fluid)
        {
            flushedMl = 0;
            fluid = 0;
            if (!CanEdit(state, out PipeOpResult refuse))
            {
                return refuse;
            }
            int net = _kernel.NetworkAt(cell.X, cell.Y);
            fluid = NetFluid(net);
            PipeResult r = _kernel.Flush(cell.X, cell.Y, out flushedMl);
            if (r == PipeResult.WrongKind)
            {
                return PipeOpResult.Reason(r, "logistics.pipe.reason.flush_valve");
            }
            if (r == PipeResult.Ok)
            {
                GuidanceHooks.Raise(GuidanceHooks.LogisticsPipeFirstFlush);
            }
            return PipeOpResult.Kernel(r);
        }

        private static int NetFluid(int net) => IsRunning && _kernel.TryGetNetworkInfo(net, out PipeNetInfo n) ? n.Fluid : 0;

        // ── 结冰接口（FGR-LOG-045：本 Story 只预留游戏时钟接口，结冰规则由 FG7-ENV-03 寒潮接入）──────────────────

        /// <summary>寒潮是否进行中（FG7-ENV-03 的天气系统调 <see cref="SetColdSnap"/> 写；进存档）。本 Story 不产生任何结冰效果。</summary>
        public static bool ColdSnapActive => _state?.Pipes?.ColdSnap ?? false;

        public static void SetColdSnap(CampaignState state, bool active)
        {
            if (state?.Pipes != null)
            {
                state.Pipes.ColdSnap = active;
            }
        }

        /// <summary>一个游戏小时 = 一天的游戏秒 / 24（GameClock.DaySeconds）。</summary>
        public static double GameHourSeconds => Math.Max(1.0, GameClock.DaySeconds / 24.0);

        /// <summary>网络已经多少游戏小时没有流动（按内核步序号，与帧率 / 倍速 / 观察无关；读档后接着计）。没有这个网络返回 0。</summary>
        public static double IdleGameHours(int net)
        {
            if (!IsRunning || !_kernel.TryGetNetworkInfo(net, out PipeNetInfo info))
            {
                return 0;
            }
            long steps = Math.Max(0, _kernel.StepIndex - 1 - info.LastFlowStep);
            return steps / (double)_kernel.Config.StepHz / GameHourSeconds;
        }

        /// <summary>结冰的判定条件是否满足（寒潮中 + 流量为 0 超过 logistics.pipe.freeze_idle_hours 个游戏小时）。预留给 FG7-ENV-03：本 Story 不据此阻断流动。</summary>
        public static bool MeetsFreezeCondition(int net) =>
            ColdSnapActive && IdleGameHours(net) >= GridContent.Tuning("logistics.pipe.freeze_idle_hours");

        // ── 悬停与面板读数（FGR-LOG-042；B05 / B06 / B13）──────────────────────────────

        private static string L(double lpm) => lpm.ToString("0.#", CultureInfo.InvariantCulture);

        private static string Liters(long ml) => (ml / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

        public static string TankModeName(PipeTankMode mode) =>
            GameText.Get(mode == PipeTankMode.InOnly ? "logistics.pipe.tank_mode.in" : mode == PipeTankMode.OutOnly ? "logistics.pipe.tank_mode.out" : "logistics.pipe.tank_mode.both");

        /// <summary>网络的状态与根因（一行，稳定文本）。</summary>
        public static string DescribeState(in PipeNetInfo n)
        {
            PipeNetIssue i = n.Issues;
            if ((i & PipeNetIssue.Empty) != 0)
            {
                return GameText.Get("logistics.pipe.state.empty");
            }
            if ((i & PipeNetIssue.NoSupply) != 0)
            {
                string s = GameText.Format("logistics.pipe.state.no_supply", L(n.DemandLpm));
                return (i & PipeNetIssue.NoSource) != 0 ? s + "\n" + GameText.Get("logistics.pipe.state.no_source") : s;
            }
            if ((i & PipeNetIssue.Shortage) != 0)
            {
                string s = GameText.Format("logistics.pipe.state.shortage", L(n.UnmetLpm));
                return (i & PipeNetIssue.PipeLimited) != 0 ? s + "\n" + GameText.Get("logistics.pipe.state.pipe_limited") : s;
            }
            if ((i & PipeNetIssue.PipeLimited) != 0)
            {
                return GameText.Get("logistics.pipe.state.pipe_limited");
            }
            if ((i & PipeNetIssue.NoDemand) != 0)
            {
                return GameText.Get("logistics.pipe.state.no_demand");
            }
            if ((i & PipeNetIssue.NoSource) != 0)
            {
                return GameText.Get("logistics.pipe.state.no_source");
            }
            return n.DeliveredLpm + n.BufferedLpm > 0 ? GameText.Get("logistics.pipe.state.ok") : GameText.Get("logistics.pipe.state.idle");
        }

        /// <summary>网络的读数行：流体与规模、状态、供给 / 需求 / 输送 / 上限、储量、瓶颈、成员、静止时长。</summary>
        public static void AppendNetworkLines(StringBuilder sb, in PipeNetInfo n)
        {
            sb.Append(GameText.Format("logistics.pipe.hover.network", FluidName(n.Fluid), n.Cells, n.PipeCells));
            sb.Append('\n').Append(DescribeState(n));
            sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.flow", L(n.SupplyLpm), L(n.DemandLpm), L(n.DeliveredLpm), L(n.CapLpm)));
            if (n.BufferedLpm > 0)
            {
                sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.buffered", L(n.BufferedLpm)));
            }
            sb.Append('\n').Append(n.Tanks > 0
                ? GameText.Format("logistics.pipe.hover.storage", Liters(n.StoredMl), Liters(n.StorageCapMl), n.Tanks)
                : GameText.Get("logistics.pipe.hover.no_storage"));
            if (n.PipeCells == 0)
            {
                sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.no_pipe", L(n.CapLpm)));
            }
            else if (n.Mixed)
            {
                sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.bottleneck", PieceName(PipePieceKind.Pipe, n.MinTier), n.BottleneckX, n.BottleneckY,
                    n.MinTierCells, L(n.CapLpm)));
            }
            else
            {
                sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.uniform", PieceName(PipePieceKind.Pipe, n.MinTier), L(n.CapLpm)));
            }
            sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.members", n.Pumps, n.Tanks, n.Consumers, n.ValvesIn, n.ValvesOut));
            double idle = IdleGameHours(n.Id);
            if (n.DeliveredLpm + n.BufferedLpm <= 0 && idle >= 0.01)
            {
                sb.Append('\n').Append(GameText.Format("logistics.pipe.hover.idle", idle.ToString("0.##", CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>
        /// FG4-ECO-02（流体泵沿用一格泵，FG04 建筑表“流体泵”；FGR-ECO-010 状态不只靠颜色）：泵的通用状态行——
        /// 工作中（正在抽 X）/ 待命（下游没有需要）/ 不在流体源上（脚下的地形被改过，泵抽不到东西）。管线面板与悬停共用。
        /// </summary>
        public static string PumpStateLine(CampaignState state, in PipeCellInfo c)
        {
            int source = state != null ? SourceFluidAt(state, new GridCell(c.X, c.Y)) : c.Fluid;
            if (source <= 0 || source != c.Fluid)
            {
                return GameText.Format("prod.hover.line", GameText.Get("prod.state.no_resource"), GameText.Get("prod.reason.pump_no_source"));
            }
            if (c.PumpedMl > 0)
            {
                return GameText.Format("prod.hover.line", GameText.Get("prod.state.working"),
                    GameText.Format("prod.reason.pump_working", FluidName(c.Fluid), IsRunning ? _kernel.Config.PumpLitersPerMinute : 0));
            }
            return GameText.Format("prod.hover.line", GameText.Get("prod.state.idle"), GameText.Format("prod.reason.pump_idle", FluidName(c.Fluid)));
        }

        /// <summary>一件自己的读数行（泵 / 储罐 / 阀门；管线没有）。</summary>
        public static void AppendPieceLines(StringBuilder sb, in PipeCellInfo c)
        {
            double perStepToLpm = 60.0 * _kernel.Config.StepHz / 1000.0;
            switch (c.Kind)
            {
                case PipePieceKind.Pump:
                    sb.Append(GameText.Format("logistics.pipe.hover.pump", FluidName(c.Fluid), _kernel.Config.PumpLitersPerMinute,
                        L(c.PumpedMl * perStepToLpm), Liters(c.PumpTotalMl)));
                    break;
                case PipePieceKind.Tank:
                    sb.Append(GameText.Format("logistics.pipe.hover.tank", Liters(c.TankStockMl), Liters(c.TankCapacityMl), TankModeName(c.TankMode), c.Priority,
                        L(c.TankInMl * perStepToLpm), L(c.TankOutMl * perStepToLpm)));
                    break;
                case PipePieceKind.Valve:
                    sb.Append(GameText.Format("logistics.pipe.hover.valve", GameText.Get(c.ValveOpen ? "logistics.pipe.valve.open" : "logistics.pipe.valve.closed"),
                        GameText.Get(GridMath.DirTextKey((GridDir)c.Dir)), Liters(c.ValveBufferMl), L(c.ValveFlowMl * perStepToLpm)));
                    if (c.ValveFrom < 0)
                    {
                        sb.Append('\n').Append(GameText.Get("logistics.pipe.hover.valve_from_none"));
                    }
                    if (c.ValveTo < 0)
                    {
                        sb.Append('\n').Append(GameText.Get("logistics.pipe.hover.valve_to_none"));
                    }
                    if (c.ValveFluidMismatch)
                    {
                        sb.Append('\n').Append(GameText.Get("logistics.pipe.hover.valve_mismatch"));
                    }
                    break;
                case PipePieceKind.Underground:
                    // FG4-ECO-04（FG-GAP-082）：地下管线口——朝哪边、连到哪一口（没配对写明怎么配）。
                    sb.Append(c.UndergroundLinked
                        ? GameText.Format("logistics.pipe.hover.underground", GameText.Get(GridMath.DirTextKey((GridDir)c.Dir)), c.PartnerX, c.PartnerY,
                            Math.Abs(c.PartnerX - c.X) + Math.Abs(c.PartnerY - c.Y) - 1)
                        : GameText.Format("logistics.pipe.hover.underground_unlinked", GameText.Get(GridMath.DirTextKey((GridDir)c.Dir)), UndergroundSpan(c.Tier)));
                    break;
            }
        }

        private static readonly StringBuilder HoverSb = new StringBuilder(512);

        /// <summary>
        /// 悬停读数（战略视角世界悬停提示与建造模式状态行同一来源）：标题 = 件名与坐标；正文 = 件自己的读数 + 所在网络的供给、需求、储量、瓶颈与根因。
        /// 阀门没有网络：写它两侧网络的读数。O(1)（只读这一格与网络汇总）。
        /// </summary>
        public static bool TryDescribeHover(CampaignState state, GridCell cell, out string title, out string body)
        {
            title = null;
            body = null;
            if (!Bound(state) || !_kernel.TryGetCellInfo(cell.X, cell.Y, out PipeCellInfo c))
            {
                return false;
            }
            title = GameText.Format("logistics.pipe.hover.title", PieceName(c.Kind, c.Tier), cell.X, cell.Y);
            StringBuilder sb = HoverSb;
            sb.Clear();
            if (c.Kind != PipePieceKind.Pipe)
            {
                AppendPieceLines(sb, c);
            }
            string hp = HpLine(cell); // FG6-DEF-05：受损时写耐久（与传送带悬停一致）
            if (hp.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(hp);
            }
            int net = c.Kind == PipePieceKind.Valve ? (c.ValveFrom >= 0 ? c.ValveFrom : c.ValveTo) : c.Network;
            if (_kernel.TryGetNetworkInfo(net, out PipeNetInfo n))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                AppendNetworkLines(sb, n);
            }
            sb.Append('\n').Append(GameText.Get("logistics.pipe.hover.hint"));
            body = sb.ToString();
            return true;
        }

        // ── 渲染（观察星球表面时每帧一次）──────────────────────────────────────────

        public static void Render(Camera camera)
        {
            if (!IsRunning || camera == null)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            if (_renderer == null)
            {
                _renderer = new PipeRenderer();
                EnsureFluids();
                foreach (KeyValuePair<int, GameConfig.fg.Fluid> kv in Fluids)
                {
                    _renderer.SetPalette(kv.Key, ParseColor(kv.Value.Color), GlyphIndex(kv.Value.Glyph));
                }
            }
            _renderer.Draw(_kernel, camera, _renderSettings);
            LastRenderMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            RenderCalls++;
        }

        /// <summary>流体颜色（渲染与界面共用一个来源：表的 color 列）。</summary>
        public static Color FluidColor(int id)
        {
            EnsureFluids();
            return id > 0 && Fluids.TryGetValue(id, out GameConfig.fg.Fluid f) ? ParseColor(f.Color) : new Color(0.42f, 0.42f, 0.45f);
        }

        public static int FluidGlyph(int id)
        {
            EnsureFluids();
            return id > 0 && Fluids.TryGetValue(id, out GameConfig.fg.Fluid f) ? GlyphIndex(f.Glyph) : -1;
        }
    }
}
