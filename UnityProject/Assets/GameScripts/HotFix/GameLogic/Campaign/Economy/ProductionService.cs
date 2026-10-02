using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>生产建筑的状态（FGR-ECO-010 的本 Story 部分；界面上每种状态有不同的形状符号，不只靠颜色）。</summary>
    public enum ProdState : byte
    {
        None = 0,
        /// <summary>还没建成（规划 / 施工中）。</summary>
        Building,
        Damaged,
        /// <summary>被玩家关停（启用 / 禁用开关在 FG4-ECO-05）。</summary>
        Disabled,
        /// <summary>没电（未接入电网 / 电网缺电按优先级停机）。</summary>
        NoPower,
        /// <summary>不在资源点上（提取钻脚下没有矿脉——地形被改过时）。</summary>
        NoResource,
        /// <summary>待机：多配方建筑没选配方。</summary>
        Idle,
        MissingInput,
        MissingFluid,
        OutputBlocked,
        Working,
    }

    /// <summary>状态的原因种类（界面按它和参数现拼文字；自检按它断言“显示了正确原因”，不按文字）。</summary>
    public enum ProdReason : byte
    {
        None = 0,
        NotBuilt,
        Damaged,
        Disabled,
        PowerUnconnected,
        PowerBrownout,
        NoVein,
        NoRecipe,
        /// <summary>配方缺固体输入（配合 <see cref="ProductionService.Producer.ReasonItem"/>、输入口状态）。</summary>
        MissingItem,
        MissingFluid,
        /// <summary>回收站：脚下废墟拆完了，输入口也没有东西。</summary>
        RuinDepleted,
        /// <summary>主产出放不下。</summary>
        NoRoom,
        /// <summary>副产品放不下（FG04 第 5 节“酸液没有去处”）。</summary>
        ByproductNoRoom,
        WorkingRecipe,
        WorkingRuin,
        WorkingItem,
        WorkingDrill,
        WorkingWaste,
        /// <summary>废液池：进口的网络里没有流体（或没接管线）。</summary>
        WasteIdle,
        /// <summary>流体泵：正在抽（配合 <see cref="ProductionService.Producer.ReasonItem"/> = 抽的流体）。</summary>
        WorkingPump,
        /// <summary>流体泵：脚下没有流体源（地形被改过时）。</summary>
        NoSource,
        /// <summary>流体泵：出口送不出去（没接管线 / 网络里是别的流体 / 下游用不完、储罐满了）。</summary>
        PumpBlocked,
        /// <summary>FG4-ECO-03 固件刻录台：没选要刻的固件（待机）。</summary>
        NoBurnTarget,
        /// <summary>FG4-ECO-03 固件刻录台：选的固件还没破解 / 不是固件（待机，配合 <see cref="ProductionService.Producer.ReasonTarget"/>）。</summary>
        BurnTargetLocked,
        /// <summary>FG4-ECO-03 固件刻录台：固件芯片存放满了（输出堵塞）。</summary>
        FirmwareStorageFull,
        /// <summary>FG4-ECO-03 固件刻录台：正在刻（配合 <see cref="ProductionService.Producer.ReasonTarget"/>）。</summary>
        WorkingBurn,
        /// <summary>FG4-ECO-04 燃油发电机：在发电（负荷 &gt; 0）。</summary>
        WorkingGenerator,
        /// <summary>FG4-ECO-04 燃油发电机：有油，但电网不缺电（负荷 0，不烧油）。</summary>
        GeneratorIdle,
        /// <summary>FG4-ECO-04 燃油发电机：没有燃油（卡片负向“燃油耗尽”）。</summary>
        NoFuel,
        /// <summary>FG4-ECO-04 燃油发电机：不在任何电力覆盖里（发出的电送不出去，所以不烧油）。</summary>
        GeneratorUnconnected,
    }

    /// <summary>
    /// FG4-ECO-02（FG04 第 3.3 节采集 / 加工建筑；FGR-ECO-010 / 011 本 Story 部分；FGR-ECO-071；卡片“采集建筑只能放在资源点上”“废墟拆完”“泵放在非流体源上”）：
    /// 回收站、提取钻、精炼炉、精炼塔、调配站、废液池的运行。<b>唯一写入口</b>（<see cref="EconomyState.Producers"/> / <see cref="EconomyState.RuinCells"/> /
    /// <see cref="EconomyState.Vibration"/>，以及这些建筑在管线内核里的消费者 / 供给者）。
    ///
    /// - 推进（<see cref="WorldStep"/>）：每 eco.prod.step_ticks 个世界步一次（与传送带 / 管线同为 20 Hz），按建筑 ID 顺序逐座推进；进度按游戏时钟步数累计——
    ///   暂停不走、倍速只是一帧多走几步、读档后接着同一节拍、与有没有人观察无关（FGR-BASE-021）。O(生产建筑数)，零分配（原因文字按需现拼）。
    /// - 周期：开工时按配方检查（缺料 / 输出放不下给出原因）并扣料，走满配方时间后产出进输出缓存（开工时已确认放得下，全有或全无，账目守恒）。
    ///   回收站先分解输入口送来的固体（每件得到 <see cref="ItemDef.RecycleScrap"/> 废料），没有时拆脚下的废墟（逐格；一格拆完变成可建空地，区块差异进存档）。
    ///   提取钻每周期出一份脚下矿脉的矿（矿脉无限），工作时给家园震动值加数（FG10 接口 <see cref="Vibration"/>）。废液池按速率销毁收到的流体。
    /// - 物品口（fg.TbBuildingPort role = prod，由 <see cref="BeltPortService"/> 登记）：<see cref="PumpPort"/> 每个传送带内核步一次，O(端口)。
    ///   输入口只收当前配方要的固体（回收站收任何固体，缓存一次一种）；输出口把输出缓存推上传送带。
    /// - 流体口（fg.TbBuildingFluidPort）：<see cref="Sync"/> 每 eco.prod.sync_seconds 游戏秒对账一次，在口外那一格登记管线内核的消费者（带缓存）/ 供给者；
    ///   建筑不在运转时撤掉，口里的流体留在建筑自己身上（<see cref="ProducerRecord.FluidHeld"/>），恢复后放回去。
    /// 不做玩家没要求的事（FGR-BASE-020）：多配方建筑不替玩家选配方；只有一条配方的建筑那条配方就是它的固定功能。
    /// </summary>
    public static class ProductionService
    {
        /// <summary>流体口的运行时视图。</summary>
        public sealed class FluidRt
        {
            public FluidPortDef Def;
            public GridCell PortCell;
            public GridDir Face;
            /// <summary>口外那一格（管线接在这里）。</summary>
            public GridCell PipeCell;
            /// <summary>这个口实际收 / 出的流体：表里写死的那种；流体泵的出口 = 脚下流体源的流体（<see cref="Refresh"/> 按地形定，没有流体源 = null）。</summary>
            public ItemDef Fluid;
            public int FluidId => Fluid?.FluidId ?? 0;
        }

        /// <summary>一座生产建筑的运行时视图（真相在 <see cref="ProducerRecord"/> 与 <see cref="BuildingRecord"/>）。</summary>
        public sealed class Producer
        {
            public BuildingRecord Building;
            public ProducerDef Def;
            public ProducerRecord Rec;
            /// <summary>当前配方（多配方建筑 = 玩家选的；单配方 = 固定；不是配方建筑 = null）。</summary>
            public RecipeDef Recipe;
            public ProdState State;
            public ProdReason Reason;
            public ItemDef ReasonItem;
            /// <summary>原因指向的流体口（-1 = 不是流体口）。</summary>
            public int ReasonPort = -1;
            public long ReasonNeed;
            public long ReasonHave;
            public readonly List<GridCell> RuinCells = new List<GridCell>(16);
            public ItemDef VeinOre;
            public int VeinCells;
            public string VeinTerrainKey;
            /// <summary>流体泵：脚下流体源的流体、格数与地形名键（没有流体源 = null / 0）。</summary>
            public ItemDef SourceFluid;
            public int SourceCells;
            public string SourceTerrainKey;
            public FluidRt[] Fluids = Array.Empty<FluidRt>();
            public GridCell Pivot;
            public int Rotation;
            public bool InPortExists;
            public bool OutPortExists;
            public bool WorkedThisStep;
            /// <summary>FG4-ECO-05：理论份数累加的余数（千分份 × 周期步数的零头，不在每步截断；周期变了就清零）与它对应的周期步数。运行时量，不存档（最多差 1/1000 份）。</summary>
            public long TheoryRem;
            public long TheoryCycle;
            /// <summary>FG4-ECO-03：原因里的固件（刻录台的刻录目标）。</summary>
            public string ReasonTarget;
            /// <summary>FG4-ECO-03：这座建筑是固件刻录台（唯一配方的种类是 firmware：产出是固件芯片，进固件库）。</summary>
            public bool IsBurner => Def.FixedRecipe != null && Def.FixedRecipe.Kind == RecipeKindFirmware;
            public string Id => Building?.BuildingId;
        }

        /// <summary>FG4-ECO-01 配方表 kind = firmware：产出是指定的已破解固件的芯片（固件刻录台）。</summary>
        public const string RecipeKindFirmware = "firmware";

        private static CampaignState _state;
        private static ProducerRecord[] _indexedRecords;
        private static BuildingRecord[] _indexedBuildings;
        private static readonly List<Producer> Ordered = new List<Producer>(16);
        private static readonly Dictionary<string, Producer> ById = new Dictionary<string, Producer>(StringComparer.Ordinal);
        private static readonly Dictionary<long, RuinCellRecord> RuinIndex = new Dictionary<long, RuinCellRecord>();
        private static RuinCellRecord[] _indexedRuins;
        private static readonly List<GridCell> CellScratch = new List<GridCell>(32);
        private static readonly List<ProducerRecord> RecordScratch = new List<ProducerRecord>(16);
        private static ulong _lastSignature;
        private static int _gridRevision = -1;
        private static int _catalogRevision = -1;
        private static int _itemRevision = -1;

        /// <summary>任何生产建筑的配方 / 索引变化 +1（面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static int StepCount { get; private set; }
        public static int SyncCount { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        public static int ProducerCount
        {
            get
            {
                EnsureIndex(CampaignSession.Current);
                return Ordered.Count;
            }
        }
        public static IReadOnlyList<Producer> All => Ordered;
        /// <summary>自检：最近一次有废墟格拆完变成空地的格子数（累计）。</summary>
        public static int RuinCellsDepleted { get; private set; }

        private static int StepTicks => Math.Max(1, GridContent.TuningInt("eco.prod.step_ticks"));
        public static int RuinScrapPerCell => Math.Max(1, GridContent.TuningInt("eco.ruin.scrap_per_cell"));
        public static double VibrationMax => Math.Max(1.0, GridContent.Tuning("eco.vibration.max"));

        // ── 生命周期 ─────────────────────────────────────────────────────────────

        /// <summary>读档 / 接入战役：清空运行时索引（下一次用到时按存档重建，流体口在下一次对账时核对内核里的句柄）。</summary>
        public static void OnLoad(CampaignState state)
        {
            Clear();
            _state = state;
        }

        public static void OnUnload() => Clear();

        public static void ResetForTests() => Clear();

        private static void Clear()
        {
            _state = null;
            _indexedRecords = null;
            _indexedBuildings = null;
            _indexedRuins = null;
            Ordered.Clear();
            ById.Clear();
            RuinIndex.Clear();
            _lastSignature = 0;
            _gridRevision = -1;
            Revision++;
        }

        public static bool TryGet(string buildingId, out Producer p)
        {
            EnsureIndex(CampaignSession.Current);
            p = null;
            return buildingId != null && ById.TryGetValue(buildingId, out p);
        }

        public static bool TryGet(CampaignState state, string buildingId, out Producer p)
        {
            EnsureIndex(state);
            p = null;
            return buildingId != null && ById.TryGetValue(buildingId, out p);
        }

        // ── 索引（建筑 / 记录数组换了、表换了时重建；O(建筑数)）────────────────────────

        private static void EnsureIndex(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            if (state.Economy?.Producers == null || state.Economy.RuinCells == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            if (ReferenceEquals(state, _state) && ReferenceEquals(state.BuildingRecords, _indexedBuildings) && ReferenceEquals(state.Economy.Producers, _indexedRecords)
                && _catalogRevision == ProducerCatalog.Revision && _itemRevision == ItemCatalog.Revision && _gridRevision == GridContent.Revision)
            {
                return;
            }
            Rebuild(state);
        }

        private static void Rebuild(CampaignState state)
        {
            _state = state;
            _catalogRevision = ProducerCatalog.Revision;
            _itemRevision = ItemCatalog.Revision;
            _gridRevision = GridContent.Revision;
            var byId = new Dictionary<string, ProducerRecord>(StringComparer.Ordinal);
            foreach (ProducerRecord r in state.Economy.Producers ?? Array.Empty<ProducerRecord>())
            {
                if (r != null && !string.IsNullOrEmpty(r.BuildingId) && !byId.ContainsKey(r.BuildingId))
                {
                    byId[r.BuildingId] = r;
                }
            }
            Ordered.Clear();
            ById.Clear();
            RecordScratch.Clear();
            bool added = false;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || HomeGridService.IsRelocationGhost(b) || !ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def))
                {
                    continue;
                }
                if (!byId.TryGetValue(b.BuildingId, out ProducerRecord rec))
                {
                    rec = new ProducerRecord { BuildingId = b.BuildingId };
                    byId[b.BuildingId] = rec;
                    added = true;
                    // FG4-ECO-03（卡片“配方选择记住上一次的设置”）：新建的同类建筑沿用玩家上一次给这类建筑选的配方 / 刻录目标（面板写明“沿用了上一次的选择”）。
                    ApplyMemory(state, def, rec);
                    // FG00 B14：第一次放下采集 / 加工 / 制造建筑（引导内容在 FG15-UX-04；图鉴“采集建筑”“加工建筑”“制造建筑”随之解锁）。
                    if (def.Mode != ProducerMode.Generator)
                    {
                        GuidanceHooks.Raise(def.Mode == ProducerMode.Recycler || def.Mode == ProducerMode.Drill || def.Mode == ProducerMode.Pump
                            ? GuidanceHooks.EconomyGatheringFirstPlaced
                            : IsManufacturing(def.TypeId) ? GuidanceHooks.EconomyManufacturingFirstPlaced : GuidanceHooks.EconomyProcessingFirstPlaced);
                    }
                }
                var p = new Producer { Building = b, Def = def, Rec = rec };
                EnsureFluidArrays(p);
                p.Recipe = ResolveRecipe(p);
                Refresh(state, p);
                Ordered.Add(p);
                ById[b.BuildingId] = p;
                RecordScratch.Add(rec);
            }
            // 记录里有、建筑已经不在的（读档时建筑类型被移除 / 外部改动）：缓存退回核心旁（不凭空消失），流体口撤掉。
            foreach (KeyValuePair<string, ProducerRecord> kv in byId)
            {
                if (!ById.ContainsKey(kv.Key))
                {
                    ReleaseOrphan(state, kv.Value);
                    added = true;
                }
            }
            Ordered.Sort((a, c) => string.CompareOrdinal(a.Id, c.Id));
            RecordScratch.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
            if (added || RecordScratch.Count != (state.Economy.Producers?.Length ?? 0))
            {
                state.Economy.Producers = RecordScratch.ToArray();
            }
            _indexedRecords = state.Economy.Producers;
            _indexedBuildings = state.BuildingRecords;
            Revision++;
        }

        private static void EnsureFluidArrays(Producer p)
        {
            int n = p.Def.FluidPorts.Count;
            ProducerRecord r = p.Rec;
            if (r.FluidHandles == null || r.FluidHandles.Length != n)
            {
                var h = new int[n];
                for (int i = 0; i < n; i++)
                {
                    h[i] = r.FluidHandles != null && i < r.FluidHandles.Length ? r.FluidHandles[i] : -1;
                }
                r.FluidHandles = h;
            }
            if (r.FluidHeld == null || r.FluidHeld.Length != n)
            {
                var held = new long[n];
                for (int i = 0; i < n; i++)
                {
                    held[i] = r.FluidHeld != null && i < r.FluidHeld.Length ? r.FluidHeld[i] : 0;
                }
                r.FluidHeld = held;
            }
            // 口的类型：没登记的口按建筑类型写；已登记的口保留登记时的类型（表改了口的顺序时，撤口仍按真实类型撤）。旧记录没有这一列 = 全部按类型补上。
            bool fresh = r.FluidOut == null || r.FluidOut.Length != n;
            if (fresh)
            {
                r.FluidOut = new bool[n];
            }
            for (int i = 0; i < n; i++)
            {
                if (fresh || r.FluidHandles[i] < 0)
                {
                    r.FluidOut[i] = p.Def.FluidPorts[i].IsOutput;
                }
            }
            if (p.Fluids.Length != n)
            {
                p.Fluids = new FluidRt[n];
                for (int i = 0; i < n; i++)
                {
                    p.Fluids[i] = new FluidRt { Def = p.Def.FluidPorts[i] };
                }
            }
        }

        private static RecipeDef ResolveRecipe(Producer p)
        {
            if (p.Def.Mode != ProducerMode.Recipe)
            {
                return null;
            }
            RecipeDef fixedRecipe = p.Def.FixedRecipe;
            if (fixedRecipe != null)
            {
                return fixedRecipe;
            }
            return !string.IsNullOrEmpty(p.Rec.RecipeId) && ItemCatalog.TryGetRecipe(p.Rec.RecipeId, out RecipeDef r) && p.Def.AllowsRecipe(r) ? r : null;
        }

        /// <summary>按建筑现在的位置 / 朝向重算：脚下的废墟格与矿脉、流体口的位置、有没有物品口。O(占地格)。</summary>
        private static void Refresh(CampaignState state, Producer p)
        {
            BuildingRecord b = p.Building;
            p.Pivot = new GridCell(b.GridX, b.GridY);
            p.Rotation = GridMath.NormalizeRotation(b.Rotation);
            p.RuinCells.Clear();
            p.VeinOre = null;
            p.VeinCells = 0;
            p.VeinTerrainKey = null;
            p.SourceFluid = null;
            p.SourceCells = 0;
            p.SourceTerrainKey = null;
            if (GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g))
            {
                GridMath.FootprintCells(p.Pivot, g.FootprintW, g.FootprintH, p.Rotation, CellScratch);
                // 行优先（先 y 后 x）：拆废墟的顺序与建筑朝向、记录顺序无关，逐位确定。
                CellScratch.Sort((a, c) => a.Y != c.Y ? a.Y.CompareTo(c.Y) : a.X.CompareTo(c.X));
                HomeGridMap map = HomeGridService.MapFor(state);
                byte ruin = GridContent.TryTerrainCode("ruin", out byte rc) ? rc : (byte)255;
                byte metal = GridContent.TryTerrainCode("ore_metal", out byte mc) ? mc : (byte)255;
                byte rare = GridContent.TryTerrainCode("ore_rare", out byte ec) ? ec : (byte)255;
                int metalN = 0, rareN = 0;
                int srcA = 0, srcB = 0, srcNA = 0, srcNB = 0;
                byte srcTA = 0, srcTB = 0;
                foreach (GridCell c in CellScratch)
                {
                    byte t = map.GetTerrain(c);
                    if (p.Def.Mode == ProducerMode.Pump)
                    {
                        // 流体泵：数脚下每种流体源的格数（2×2 最多两种：水源 / 油井）。
                        int fid = PipeNetworkService.SourceFluidOfTerrain(t);
                        if (fid > 0)
                        {
                            if (srcA == 0 || srcA == fid)
                            {
                                srcA = fid;
                                srcTA = t;
                                srcNA++;
                            }
                            else if (srcB == 0 || srcB == fid)
                            {
                                srcB = fid;
                                srcTB = t;
                                srcNB++;
                            }
                        }
                        continue;
                    }
                    if (p.Def.Mode == ProducerMode.Recycler && t == ruin)
                    {
                        p.RuinCells.Add(c);
                    }
                    else if (t == metal)
                    {
                        metalN++;
                    }
                    else if (t == rare)
                    {
                        rareN++;
                    }
                }
                if (p.Def.Mode == ProducerMode.Drill && metalN + rareN > 0)
                {
                    // 脚下哪种矿脉格多就采哪种（一样多时采金属矿）。
                    bool useRare = rareN > metalN;
                    ItemCatalog.TryGet(useRare ? "rare_earth_ore" : "metal_ore", out p.VeinOre);
                    p.VeinCells = useRare ? rareN : metalN;
                    p.VeinTerrainKey = useRare ? "grid.terrain.ore_rare.name" : "grid.terrain.ore_metal.name";
                }
                if (p.Def.Mode == ProducerMode.Pump && srcNA + srcNB > 0)
                {
                    // 哪种流体源格多抽哪种；一样多时抽流体编号小的那种（水），与朝向、记录顺序无关。
                    bool useB = srcNB > srcNA || (srcNB == srcNA && srcB < srcA);
                    int fid = useB ? srcB : srcA;
                    if (ItemCatalog.TryGetByFluid(fid, out ItemDef fl))
                    {
                        p.SourceFluid = fl;
                        p.SourceCells = useB ? srcNB : srcNA;
                        GameConfig.fg.GridTerrain gt = GridContent.TerrainByCode(useB ? srcTB : srcTA);
                        p.SourceTerrainKey = gt?.NameKey;
                    }
                }
            }
            p.InPortExists = false;
            p.OutPortExists = false;
            foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(b.BuildingTypeId))
            {
                if (row.Role == "prod")
                {
                    if (row.Kind == "out")
                    {
                        p.OutPortExists = true;
                    }
                    else
                    {
                        p.InPortExists = true;
                    }
                }
            }
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                FluidRt f = p.Fluids[i];
                f.Fluid = f.Def.FromSource ? p.SourceFluid : f.Def.Fluid;
                f.PortCell = GridMath.PortCell(p.Pivot, f.Def.LocalX, f.Def.LocalY, p.Rotation);
                f.Face = GridMath.RotateDir(f.Def.Dir, p.Rotation);
                Vector2Int v = GridMath.DirVector(f.Face);
                f.PipeCell = new GridCell(f.PortCell.X + v.x, f.PortCell.Y + v.y);
            }
        }

        private static void ReleaseOrphan(CampaignState state, ProducerRecord r)
        {
            Vector2 at = HomeValleyLayout.CameraFocusStart;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && b.RegionId == HomeValleyLayout.RegionId)
                {
                    at = b.Position;
                    break;
                }
            }
            ReturnBuffers(state, r, at, "prod-orphan:" + r.BuildingId + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
            RemoveFluidHandles(r, keep: false);
        }

        // ── 对账（每 eco.prod.sync_seconds 游戏秒；建筑签名变了才做事）───────────────────────────

        public static int SyncTicks(int worldHz) =>
            Math.Max(1, Mathf.RoundToInt(GridContent.Tuning("eco.prod.sync_seconds") * Math.Max(1, worldHz)));

        /// <summary>建筑签名（ID、类型、施工状态、枢轴格、朝向）——O(建筑数) 的整数混合，不分配。</summary>
        private static ulong Signature(CampaignState state)
        {
            ulong h = 1469598103934665603UL;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || !ProducerCatalog.IsProducer(b.BuildingTypeId))
                {
                    continue;
                }
                h = Mix(h, (ulong)(b.BuildingId?.GetHashCode() ?? 0));
                h = Mix(h, (ulong)b.ConstructionState);
                h = Mix(h, (ulong)(uint)b.GridX);
                h = Mix(h, (ulong)(uint)b.GridY);
                h = Mix(h, (ulong)GridMath.NormalizeRotation(b.Rotation));
                h = Mix(h, string.IsNullOrEmpty(b.RelocateFromId) ? 0UL : 7UL);
            }
            return h;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            unchecked
            {
                return (h ^ v) * 1099511628211UL;
            }
        }

        /// <summary>
        /// 对账：建筑建成 / 修好 / 受损 / 转向 / 搬迁 / 拆除后，重算脚下资源、流体口位置，并让管线内核里的消费者 / 供给者跟上（建筑不在运转时撤掉，流体留在建筑里）。
        /// 每个世界步末按步序号判断要不要做，签名没变时 O(建筑数) 混合后返回。
        /// </summary>
        public static void Sync(CampaignState state, bool force = false)
        {
            if (state == null)
            {
                return;
            }
            EnsureIndex(state);
            ulong sig = Signature(state);
            bool pipes = PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state) && !PipeNetworkService.SavedDataPreserved;
            if (!force && sig == _lastSignature && (!pipes || HandlesValid()))
            {
                if (pipes && _heldPending)
                {
                    ReturnAllHeld();
                }
                return;
            }
            _lastSignature = sig;
            SyncCount++;
            foreach (Producer p in Ordered)
            {
                Refresh(state, p);
                if (pipes)
                {
                    SyncFluids(p);
                }
            }
            if (pipes)
            {
                ReturnAllHeld();
            }
        }

        /// <summary>有建筑在口已登记时把流体留在了自己身上（换配方退回的料、口里放不下的产出）：下一次对账放回口里。</summary>
        private static bool _heldPending;

        /// <summary>把每座建筑留着的流体放回已登记的口里（放不下的继续留着，下次对账再试）。O(流体口)，只在对账步、且有待放回的流体时。</summary>
        private static void ReturnAllHeld()
        {
            _heldPending = false;
            PipeKernel k = PipeNetworkService.Kernel;
            foreach (Producer p in Ordered)
            {
                for (int i = 0; i < p.Fluids.Length; i++)
                {
                    int h = p.Rec.FluidHandles[i];
                    if (h < 0 || p.Rec.FluidHeld[i] <= 0 || p.Fluids[i].Def.FromSource)
                    {
                        continue;
                    }
                    ReturnHeld(k, p, i, h);
                    if (p.Rec.FluidHeld[i] > 0)
                    {
                        _heldPending = true;
                    }
                }
            }
        }

        /// <summary>内核里的句柄都还在（读档时管线快照没读进来 / 被外部清掉时重新登记）。O(流体口 × 内核消费者)，只在对账步。</summary>
        private static bool HandlesValid()
        {
            PipeKernel k = PipeNetworkService.Kernel;
            foreach (Producer p in Ordered)
            {
                bool active = IsActive(p.Building);
                for (int i = 0; i < p.Fluids.Length; i++)
                {
                    int h = p.Rec.FluidHandles[i];
                    // 该登记 = 建筑在运转，且这个口有流体可出 / 可收（流体泵脚下没有流体源时出口不登记）。
                    bool want = active && !(p.Fluids[i].Def.FromSource && p.Fluids[i].FluidId == 0);
                    if (want != (h >= 0))
                    {
                        return false;
                    }
                    if (h >= 0 && (p.Rec.FluidOut[i] ? !k.TryGetProducer(h, out _) : !k.TryGetConsumer(h, out _)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>这座建筑现在该不该工作（建成、在家园、不是搬迁虚影）。关停 / 受损时端口照常撤掉（与 BeltPortService.IsActive 一致）。</summary>
        public static bool IsActive(BuildingRecord b) =>
            b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState == BuildingConstructionState.Operational && !HomeGridService.IsRelocationGhost(b);

        private static void SyncFluids(Producer p)
        {
            PipeKernel k = PipeNetworkService.Kernel;
            bool active = IsActive(p.Building);
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                FluidRt f = p.Fluids[i];
                int h = p.Rec.FluidHandles[i];
                bool ok = false;
                if (h >= 0 && active && p.Rec.FluidOut[i] == f.Def.IsOutput)
                {
                    if (f.Def.IsOutput)
                    {
                        ok = k.TryGetProducer(h, out PipeProducerInfo pi) && pi.X == f.PipeCell.X && pi.Y == f.PipeCell.Y && pi.Fluid == f.FluidId
                             && pi.CapacityMl == FluidCapacityMl(p, f.Def);
                    }
                    else
                    {
                        ok = k.TryGetConsumer(h, out PipeConsumerInfo ci) && ci.X == f.PipeCell.X && ci.Y == f.PipeCell.Y && ci.Fluid == f.FluidId;
                    }
                }
                if (ok)
                {
                    // 建筑自己留着的流体（换配方退回的料、口没登记时做完的那份产出）在对账末尾由 ReturnAllHeld 放回口里。
                    continue;
                }
                // 位置 / 参数变了或不该登记：先按口的真实类型撤（流体留在建筑里），再按需在新位置登记。
                if (h >= 0)
                {
                    long back = RemoveHandle(k, p.Rec, i);
                    // 流体泵出口里没送出去的流体回到脚下的流体源（不留在建筑里：脚下的流体源可能已经换成另一种）。
                    p.Rec.FluidHeld[i] += f.Def.FromSource ? 0 : back;
                }
                if (!active)
                {
                    continue;
                }
                long cap = FluidCapacityMl(p, f.Def);
                p.Rec.FluidOut[i] = f.Def.IsOutput;
                if (f.Def.IsOutput)
                {
                    long initial = Math.Min(cap, p.Rec.FluidHeld[i]);
                    int id = k.AddProducer(f.PipeCell.X, f.PipeCell.Y, f.FluidId, cap, initial);
                    if (id >= 0)
                    {
                        p.Rec.FluidHeld[i] -= initial;
                        p.Rec.FluidHandles[i] = id;
                    }
                }
                else
                {
                    int id = k.AddConsumer(f.PipeCell.X, f.PipeCell.Y, f.FluidId, ConsumerLpm(p, f.Def), ConsumerPriority(p));
                    if (id >= 0)
                    {
                        if (cap > 0)
                        {
                            long initial = Math.Min(cap, p.Rec.FluidHeld[i]);
                            k.SetConsumerBuffer(id, cap, initial);
                            p.Rec.FluidHeld[i] -= initial;
                        }
                        p.Rec.FluidHandles[i] = id;
                    }
                }
            }
        }

        /// <summary>流体口的缓存容量（毫升）：配方用量 × 批数；流体泵出口 = 几秒的抽取量；废液池（收任何流体）不带缓存 = 0（送达即销毁）。</summary>
        private static long FluidCapacityMl(Producer p, FluidPortDef port)
        {
            if (p.Def.Mode == ProducerMode.Pump)
            {
                // 流体泵出口存量 = outBatches 秒的抽取量（600 升/分钟 × 5 秒 = 50 升）。
                return Math.Max(1000L, (long)Math.Round(p.Def.FluidLpm * 1000.0 * Math.Max(1, p.Def.OutBatches) / 60.0));
            }
            if (p.Def.Mode == ProducerMode.Generator)
            {
                // FG4-ECO-04 燃油发电机：机内燃油 = inBatches 秒满负荷的量（120 升/分钟 × 30 秒 = 60 升）。
                return GeneratorBufferMl(p);
            }
            if (port.Fluid == null)
            {
                return 0;
            }
            int batches = Math.Max(1, port.IsOutput ? p.Def.OutBatches : p.Def.InBatches);
            long per = 0;
            foreach (RecipeDef r in p.Def.Recipes)
            {
                foreach (RecipeLine l in r.Lines)
                {
                    if (ReferenceEquals(l.Item, port.Fluid) && (l.Role == RecipeRole.In) != port.IsOutput)
                    {
                        per = Math.Max(per, l.Amount);
                    }
                }
            }
            return Math.Max(1000L, per * 1000L * batches);
        }

        /// <summary>流体口的抽取速率（升 / 分钟）：配方额定用量 × eco.prod.fluid_intake_factor；废液池 = 它的销毁速率（没电时 0）。</summary>
        private static int ConsumerLpm(Producer p, FluidPortDef port)
        {
            if (p.Def.Mode == ProducerMode.Waste)
            {
                return Powered(p.Building) ? Mathf.Max(1, Mathf.RoundToInt(p.Def.FluidLpm)) : 0;
            }
            if (p.Def.Mode == ProducerMode.Generator)
            {
                // 燃油进口按满负荷烧油速率 × eco.prod.fluid_intake_factor 取（缓存满了就不再要，不会多抽）。
                double f = Math.Max(1.0, GridContent.Tuning("eco.prod.fluid_intake_factor"));
                return Math.Max(1, (int)Math.Ceiling(p.Def.FluidLpm * f));
            }
            double best = 0;
            foreach (RecipeDef r in p.Def.Recipes)
            {
                foreach (RecipeLine l in r.Lines)
                {
                    if (ReferenceEquals(l.Item, port.Fluid) && l.Role == RecipeRole.In && r.Seconds > 0f)
                    {
                        best = Math.Max(best, l.Amount * 60.0 / r.Seconds);
                    }
                }
            }
            double factor = Math.Max(1.0, GridContent.Tuning("eco.prod.fluid_intake_factor"));
            return Math.Max(1, (int)Math.Ceiling(best * factor));
        }

        /// <summary>流体口在网络里的优先级：配方建筑 2；废液池 4（只拿别人用剩的，FG04“副产品永远有去处”而不抢原料）。</summary>
        private static int ConsumerPriority(Producer p) => p.Def.Mode == ProducerMode.Waste ? PipeConst.PriorityMax : 2;

        private static void RemoveFluidHandles(ProducerRecord r, bool keep)
        {
            if (!PipeNetworkService.IsRunning || r.FluidHandles == null)
            {
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            for (int i = 0; i < r.FluidHandles.Length; i++)
            {
                if (r.FluidHandles[i] < 0)
                {
                    continue;
                }
                long held = RemoveHandle(k, r, i);
                if (keep && r.FluidHeld != null && i < r.FluidHeld.Length)
                {
                    r.FluidHeld[i] += held;
                }
            }
        }

        /// <summary>
        /// 撤掉第 <paramref name="i"/> 个流体口在管线内核里的登记，返回口里还剩的流体（毫升；没撤到 = 0）。
        /// 内核的消费者编号与供给者编号各自从 1 起、会重号：只按 <see cref="ProducerRecord.FluidOut"/> 记的类型撤那一类，绝不“先试供给者再试消费者”
        /// （那样会撤掉别的建筑同号的口、留下无主的消费者）。旧记录没有类型列时：只有一类里有这个编号才撤，两类都有就不动并报警。
        /// </summary>
        private static long RemoveHandle(PipeKernel k, ProducerRecord r, int i)
        {
            int h = r.FluidHandles[i];
            r.FluidHandles[i] = -1;
            if (h < 0)
            {
                return 0;
            }
            long held;
            bool removed;
            if (r.FluidOut != null && i < r.FluidOut.Length && r.FluidOut.Length == r.FluidHandles.Length)
            {
                removed = r.FluidOut[i] ? k.RemoveProducer(h, out held) : k.RemoveConsumer(h, out held);
            }
            else
            {
                bool isProducer = k.TryGetProducer(h, out _);
                bool isConsumer = k.TryGetConsumer(h, out _);
                if (isProducer == isConsumer)
                {
                    if (isProducer)
                    {
                        Log.Warning($"[Production] {r.BuildingId} 的第 {i} 个流体口编号 {h} 在供给者与消费者里都有、记录没写口的类型：不撤，免得撤错别的建筑的口");
                    }
                    return 0;
                }
                removed = isProducer ? k.RemoveProducer(h, out held) : k.RemoveConsumer(h, out held);
            }
            return removed ? held : 0;
        }

        /// <summary>建筑自己留着的流体（毫升）放回已登记的口里：输出口加进供给者存量、输入口加进消费者缓存，各自夹到容量，放不下的继续留着。</summary>
        private static void ReturnHeld(PipeKernel k, Producer p, int i, int h)
        {
            long held = p.Rec.FluidHeld[i];
            if (held <= 0)
            {
                return;
            }
            if (p.Rec.FluidOut[i])
            {
                p.Rec.FluidHeld[i] -= k.AddProducerStock(h, held);
                return;
            }
            long cap = FluidCapacityMl(p, p.Fluids[i].Def);
            if (p.Fluids[i].Def.Fluid == null || cap <= 0)
            {
                return;
            }
            long now = k.ConsumerBuffer(h);
            long put = Math.Min(held, cap - now);
            if (put > 0 && k.SetConsumerBuffer(h, cap, now + put))
            {
                p.Rec.FluidHeld[i] -= put;
            }
        }

        // ── 推进 ─────────────────────────────────────────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在管线内核之后调用）：到了对账步就对账；每 eco.prod.step_ticks 步推进一次全部生产建筑。只看步序号。</summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null)
            {
                return;
            }
            if (ticksBefore % SyncTicks(worldHz) == 0)
            {
                Sync(state);
            }
            int k = StepTicks;
            if (ticksBefore % k != 0)
            {
                return;
            }
            Step(state, k, worldHz);
        }

        /// <summary>推进全部生产建筑 <paramref name="ticks"/> 个世界步（自检直接驱动；生产路径由 <see cref="WorldStep"/> 调）。</summary>
        public static void Step(CampaignState state, int ticks, int worldHz)
        {
            EnsureIndex(state);
            // FG4-ECO-05：效率统计的桶长、桶数、当前桶号每个生产步只算一次（逐建筑累加时不再查调参表）。
            _statWorldHz = Math.Max(1, worldHz);
            _statBucketTicks = StatBucketTicks(_statWorldHz);
            _statBuckets = StatBuckets;
            _statIndex = GameClock.Ticks / _statBucketTicks;
            _fwStorageKnown = false; // FG4-ECO-03：固件芯片存放的数量 / 容量每个生产步最多数一次（刻录台要用时才数）。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double vibrationRate = 0;
            foreach (Producer p in Ordered)
            {
                StepOne(state, p, ticks, worldHz);
                RecordStarve(p, ticks);
                if (p.WorkedThisStep && p.Def.VibrationPerMinute > 0f)
                {
                    vibrationRate += p.Def.VibrationPerMinute;
                }
            }
            // FG10 FGR-EVT-010 接口：工作中的提取钻累积，每分钟自然衰减；0～上限。
            double minutes = ticks / (60.0 * Math.Max(1, worldHz));
            double decay = Math.Max(0.0, GridContent.Tuning("eco.vibration.decay_per_minute"));
            double v = state.Economy.Vibration + (vibrationRate - decay) * minutes;
            state.Economy.Vibration = Math.Max(0.0, Math.Min(VibrationMax, v));
            StepCount++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
        }

        public static void ResetStepStats()
        {
            MaxStepMs = 0;
        }

        private static bool NeedsPower(BuildingRecord b) =>
            HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof) && prof.PowerDemand > 0f;

        private static bool Powered(BuildingRecord b) => !NeedsPower(b) || b.PowerState == BuildingPowerState.Powered;

        private static bool _starvedHooked;
        private static bool _fwStorageKnown;
        private static int _fwStorageHave;
        private static int _fwStorageCap;
        private static bool _byproductHooked;

        private static void Set(Producer p, ProdState s, ProdReason r, ItemDef item = null, int port = -1, long need = 0, long have = 0)
        {
            // FG04 第 4 节“新玩家第一次看到缺料、第一次看到副产品堵塞时，各有一次引导”：本 Story 只埋钩子（内容在 FG15-UX-04），每个进程查一次。
            if (!_starvedHooked && (s == ProdState.MissingInput || s == ProdState.MissingFluid) && r != ProdReason.RuinDepleted)
            {
                _starvedHooked = true;
                GuidanceHooks.Raise(GuidanceHooks.EconomyFirstStarved);
            }
            if (!_byproductHooked && r == ProdReason.ByproductNoRoom)
            {
                _byproductHooked = true;
                GuidanceHooks.Raise(GuidanceHooks.EconomyFirstByproductBlocked);
            }
            p.State = s;
            p.Reason = r;
            p.ReasonItem = item;
            p.ReasonPort = port;
            p.ReasonNeed = need;
            p.ReasonHave = have;
        }

        private static void StepOne(CampaignState state, Producer p, int ticks, int worldHz)
        {
            p.WorkedThisStep = false;
            BuildingRecord b = p.Building;
            AccrueTheory(p, ticks, worldHz);
            switch (b.ConstructionState)
            {
                case BuildingConstructionState.Operational:
                    break;
                case BuildingConstructionState.Damaged:
                    Set(p, ProdState.Damaged, ProdReason.Damaged);
                    return;
                case BuildingConstructionState.Disabled:
                    Set(p, ProdState.Disabled, ProdReason.Disabled);
                    return;
                default:
                    Set(p, ProdState.Building, ProdReason.NotBuilt);
                    return;
            }
            if (p.Def.Mode == ProducerMode.Waste)
            {
                StepWaste(p);
                return;
            }
            if (p.Def.Mode == ProducerMode.Pump)
            {
                StepPump(p, ticks, worldHz);
                return;
            }
            if (p.Def.Mode == ProducerMode.Generator)
            {
                StepGenerator(state, p, ticks, worldHz);
                return;
            }
            if (!Powered(b))
            {
                Set(p, ProdState.NoPower, b.PowerState == BuildingPowerState.Brownout ? ProdReason.PowerBrownout : ProdReason.PowerUnconnected);
                return;
            }
            long budget = ticks;
            // 一次推进里一个周期做完、下一个周期立刻接上（多出来的步数带过去）：吞吐不因推进粒度损失。
            for (int guard = 0; guard < 4 && budget > 0; guard++)
            {
                if (!p.Rec.Running && !TryStart(state, p, worldHz))
                {
                    return;
                }
                long need = Math.Max(1, p.Rec.Duration) - p.Rec.Progress;
                long use = Math.Min(need, budget);
                p.Rec.Progress += use;
                budget -= use;
                p.WorkedThisStep = true;
                SetWorking(p);
                if (p.Rec.Progress < p.Rec.Duration)
                {
                    return;
                }
                Complete(state, p);
            }
        }

        private static void SetWorking(Producer p)
        {
            switch (p.Def.Mode)
            {
                case ProducerMode.Recipe:
                    if (p.IsBurner)
                    {
                        Set(p, ProdState.Working, ProdReason.WorkingBurn);
                        p.ReasonTarget = p.Rec.BurnTarget;
                        break;
                    }
                    Set(p, ProdState.Working, ProdReason.WorkingRecipe);
                    break;
                case ProducerMode.Drill:
                    Set(p, ProdState.Working, ProdReason.WorkingDrill, p.VeinOre);
                    break;
                case ProducerMode.Recycler:
                    ItemCatalog.TryGet(p.Rec.PendingItem, out ItemDef it);
                    Set(p, ProdState.Working, p.Rec.PendingRuin ? ProdReason.WorkingRuin : ProdReason.WorkingItem, it, -1, p.Rec.PendingAmount);
                    break;
            }
        }

        /// <summary>开一个新周期：查料与输出空间（不行就写原因、返回 false），行就扣料、记下本周期的时长与产出。</summary>
        private static bool TryStart(CampaignState state, Producer p, int worldHz)
        {
            ProducerRecord r = p.Rec;
            switch (p.Def.Mode)
            {
                case ProducerMode.Recipe:
                {
                    RecipeDef recipe = p.Recipe;
                    if (recipe == null)
                    {
                        Set(p, ProdState.Idle, ProdReason.NoRecipe);
                        return false;
                    }
                    if (p.IsBurner && !CheckBurnTarget(state, p))
                    {
                        return false;
                    }
                    if (!CheckRecipe(state, p, recipe))
                    {
                        return false;
                    }
                    if (p.IsBurner && _fwStorageKnown)
                    {
                        // 审查 P2：这一周期要刻的芯片先占住存放位置——同一步里别的刻录台不会再按“还剩 1 格”一起开工。
                        _fwStorageHave += ChipsPerCycle(recipe);
                    }
                    var stock = new BuildingStock(p);
                    foreach (RecipeLine l in recipe.Lines)
                    {
                        if (l.Role == RecipeRole.In)
                        {
                            stock.Remove(l.Item, l.Amount);
                            ProductionStats.RecordUnits(state, l.Item, l.Amount, produced: false); // FG4-ECO-08：开工扣料 = 消耗
                        }
                    }
                    r.Running = true;
                    r.Progress = 0;
                    r.Duration = recipe.DurationTicks(worldHz);
                    r.PendingItem = string.Empty;
                    r.PendingAmount = 0;
                    return true;
                }
                case ProducerMode.Drill:
                {
                    if (p.VeinOre == null || p.VeinCells <= 0)
                    {
                        Set(p, ProdState.NoResource, ProdReason.NoVein);
                        return false;
                    }
                    int amount = Math.Max(1, p.Def.CycleAmount);
                    int have = Count(r.Out, p.VeinOre.Id);
                    int cap = OutCapacity(p, p.VeinOre);
                    if (have + amount > cap)
                    {
                        Set(p, ProdState.OutputBlocked, ProdReason.NoRoom, p.VeinOre, -1, amount, Math.Max(0, cap - have));
                        return false;
                    }
                    r.Running = true;
                    r.Progress = 0;
                    r.Duration = Math.Max(1, (long)Math.Round(p.Def.CycleSeconds * Math.Max(1, worldHz)));
                    r.PendingItem = p.VeinOre.Id;
                    r.PendingAmount = amount;
                    r.PendingRuin = false;
                    return true;
                }
                case ProducerMode.Recycler:
                    return TryStartRecycler(state, p, worldHz);
            }
            return false;
        }

        private static bool TryStartRecycler(CampaignState state, Producer p, int worldHz)
        {
            ProducerRecord r = p.Rec;
            ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef scrap);
            int have = Count(r.Out, ItemCatalog.ScrapId);
            int cap = OutCapacity(p, scrap);
            // 先分解输入口送来的固体（FGR-ECO-071），没有时再拆脚下的废墟。
            ItemStackRecord first = FirstNonEmpty(r.In);
            if (first != null)
            {
                // 物品表里没有的编号（表删掉了某种物品）：当作 1 件废料分解，不卡住输入口。
                int yield = ItemCatalog.TryGet(first.ItemId, out ItemDef item) ? Math.Max(1, item.RecycleScrap) : 1;
                if (have + yield > cap)
                {
                    Set(p, ProdState.OutputBlocked, ProdReason.NoRoom, scrap, -1, yield, Math.Max(0, cap - have));
                    return false;
                }
                first.Amount--;
                ProductionStats.RecordUnits(state, item, 1, produced: false); // FG4-ECO-08：分解一件 = 消耗（表里没有的编号不计）
                r.Running = true;
                r.Progress = 0;
                r.Duration = Math.Max(1, (long)Math.Round(p.Def.ItemSeconds * Math.Max(1, worldHz)));
                // PendingItem = 正在分解的那件（界面显示 / 拆除时原样退回）；完成时产出的是 PendingAmount 件废料。
                r.PendingItem = first.ItemId;
                r.PendingAmount = yield;
                r.PendingRuin = false;
                return true;
            }
            int batch = Math.Max(1, p.Def.CycleAmount);
            for (int i = 0; i < p.RuinCells.Count; i++)
            {
                GridCell c = p.RuinCells[i];
                int left = RuinRemaining(state, c);
                if (left <= 0)
                {
                    continue;
                }
                int take = Math.Min(batch, left);
                if (have + take > cap)
                {
                    Set(p, ProdState.OutputBlocked, ProdReason.NoRoom, scrap, -1, take, Math.Max(0, cap - have));
                    return false;
                }
                SetRuinRemaining(state, c, left - take);
                if (left - take <= 0)
                {
                    DepleteCell(state, p, c);
                }
                r.Running = true;
                r.Progress = 0;
                r.Duration = Math.Max(1, (long)Math.Round(p.Def.CycleSeconds * Math.Max(1, worldHz)));
                r.PendingItem = ItemCatalog.ScrapId;
                r.PendingAmount = take;
                r.PendingRuin = true;
                return true;
            }
            Set(p, ProdState.MissingInput, ProdReason.RuinDepleted, scrap);
            return false;
        }

        private static void Complete(CampaignState state, Producer p)
        {
            ProducerRecord r = p.Rec;
            if (p.Def.Mode == ProducerMode.Recipe && p.Recipe != null)
            {
                var stock = new BuildingStock(p);
                foreach (RecipeLine l in p.Recipe.Lines)
                {
                    if (l.Role == RecipeRole.In)
                    {
                        continue;
                    }
                    if (l.Item.Form == ItemForm.Entity && p.IsBurner)
                    {
                        // FG4-ECO-03：刻好的固件芯片直接进固件库（芯片存放；开工时已确认放得下，万一期间被别的来源占满就进“待领取”，不丢）。
                        for (int n = 0; n < Math.Max(1, l.Amount); n++)
                        {
                            Primitive.PrimitiveInventory.AddBurnedChip(state, r.BurnTarget);
                        }
                        ProductionStats.RecordUnits(state, l.Item, Math.Max(1, l.Amount), produced: true);
                        _fwStorageKnown = false;
                        if (r.Completed == 0)
                        {
                            GuidanceHooks.Raise(GuidanceHooks.EconomyBurnerFirstChip);
                            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ProductionComplete, p.Building.Position,
                                GameText.Format("prod.notify.first_chip", HomeGridService.DisplayName(p.Building.BuildingTypeId), Signal.FirmwareKinds.DisplayName(r.BurnTarget) ?? r.BurnTarget));
                        }
                        continue;
                    }
                    stock.Add(l.Item, l.Amount);
                    ProductionStats.RecordUnits(state, l.Item, l.Amount, produced: true); // FG4-ECO-08：完成一份 = 产出（副产品也算产出）
                }
            }
            else if (!string.IsNullOrEmpty(r.PendingItem) && r.PendingAmount > 0)
            {
                // 回收站不管拆废墟还是分解物品，产出都是废料（分解时 PendingItem 是被分解的那件，不是产出）；提取钻产出 = 矿。
                string outId = p.Def.Mode == ProducerMode.Recycler ? ItemCatalog.ScrapId : r.PendingItem;
                Add(ref r.Out, outId, r.PendingAmount);
                ProductionStats.RecordUnits(state, ItemCatalog.Find(outId), r.PendingAmount, produced: true);
                if (p.Def.Mode == ProducerMode.Recycler)
                {
                    if (r.PendingRuin)
                    {
                        r.RuinRecovered += r.PendingAmount;
                    }
                    else
                    {
                        r.ItemsRecycled++;
                    }
                }
            }
            RecordCompletion(p);
            r.Completed++;
            r.Running = false;
            r.Progress = 0;
            r.PendingAmount = 0;
            r.PendingItem = string.Empty;
            r.PendingRuin = false;
        }

        /// <summary>
        /// 流体泵：按 fluidLpm 把脚下流体源的流体放进出口（管线内核的供给者存量，网络照常按需取走）。出口放不下就不抽——流体留在地下，
        /// 不凭空生成也不丢；抽取量按世界步精确累计（零头记在 <see cref="ProducerRecord.Progress"/>，单位 毫升 × 60 × 世界频率），与推进粒度无关。
        /// </summary>
        private static void StepPump(Producer p, int ticks, int worldHz)
        {
            if (p.SourceFluid == null || p.SourceCells <= 0)
            {
                Set(p, ProdState.NoResource, ProdReason.NoSource);
                return;
            }
            if (!Powered(p.Building))
            {
                Set(p, ProdState.NoPower, p.Building.PowerState == BuildingPowerState.Brownout ? ProdReason.PowerBrownout : ProdReason.PowerUnconnected);
                return;
            }
            int h = p.Fluids.Length > 0 ? p.Rec.FluidHandles[0] : -1;
            if (h < 0 || !PipeNetworkService.IsRunning)
            {
                // 口还没登记（刚建成 / 刚恢复，下一次对账登记）：不抽。
                Set(p, ProdState.OutputBlocked, ProdReason.PumpBlocked, p.SourceFluid, 0);
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            // 口外没接管线、或接到的网络里是别的流体：不抽（流体不进一个送不出去的口），原因由 FluidPortHint 写明。
            if (!k.TryGetProducer(h, out PipeProducerInfo pi) || pi.Network < 0
                || (k.TryGetNetworkInfo(pi.Network, out PipeNetInfo net) && net.Fluid != 0 && net.Fluid != pi.Fluid))
            {
                p.Rec.Progress = 0;
                Set(p, ProdState.OutputBlocked, ProdReason.PumpBlocked, p.SourceFluid, 0);
                return;
            }
            long denom = 60L * Math.Max(1, worldHz);
            long rateMl = (long)Math.Round(p.Def.FluidLpm * 1000.0);
            long num = p.Rec.Progress + rateMl * ticks;
            long want = num / denom;
            long put = want > 0 ? k.AddProducerStock(h, want) : 0;
            // 全放进去了：零头留到下一步；放不下：这一步没抽完的部分留在地下，零头也不攒（不在堵着的时候积压“欠抽”的量）。
            p.Rec.Progress = put == want ? num - want * denom : 0;
            p.Rec.PumpedMl += put;
            ProductionStats.RecordFluidMl(_state, p.SourceFluid, put, produced: true); // FG4-ECO-08：抽出 = 产出（毫升精确累计）
            if (put > 0 || want == 0)
            {
                p.WorkedThisStep = put > 0;
                Set(p, ProdState.Working, ProdReason.WorkingPump, p.SourceFluid, 0);
                return;
            }
            Set(p, ProdState.OutputBlocked, ProdReason.PumpBlocked, p.SourceFluid, 0);
        }

        // ── FG4-ECO-04：燃油发电机 ───────────────────────────────────────────────

        /// <summary>机内燃油容量（毫升）= inBatches 秒满负荷的烧油量。</summary>
        public static long GeneratorBufferMl(Producer p) =>
            Math.Max(1000L, (long)Math.Round(p.Def.FluidLpm * 1000.0 * Math.Max(1, p.Def.InBatches) / 60.0));

        /// <summary>烧空停机后重新发电要攒的燃油（毫升）= power.fuel.restart_seconds 秒满负荷的量，不超过机内容量。</summary>
        public static long GeneratorRestartMl(Producer p)
        {
            float secs = GridContent.TryGetTuning("power.fuel.restart_seconds", out float v) ? v : 5f;
            long ml = (long)Math.Round(p.Def.FluidLpm * 1000.0 * Math.Max(0.1f, secs) / 60.0);
            return Math.Max(1L, Math.Min(GeneratorBufferMl(p), ml));
        }

        /// <summary>机内现有燃油（毫升）：口已登记 = 管线内核里的消费者缓存；没登记 = 建筑自己留着的。</summary>
        public static long GeneratorFuelMl(Producer p)
        {
            if (p.Fluids.Length == 0)
            {
                return 0;
            }
            int h = p.Rec.FluidHandles[0];
            if (h >= 0 && PipeNetworkService.IsRunning)
            {
                return PipeNetworkService.Kernel.ConsumerBuffer(h);
            }
            return p.Rec.FluidHeld[0];
        }

        /// <summary>
        /// 燃油发电机（FG04 第 3.3 节“燃烧燃油，供电 300”；卡片负向“燃油耗尽”）：按电网给的负荷比例烧机内燃油（满负荷 fluidLpm 升 / 分钟），
        /// 精确累计到毫升（零头记在 <see cref="ProducerRecord.Progress"/>，单位 毫升 × 60 × 世界频率），与推进粒度、倍速、观察无关。
        /// 烧空（要烧的比机内有的多）→ 停机、电网立即重新结算（低优先级先断、储能按设置放电），发“燃油耗尽”警告；
        /// 机内攒够 power.fuel.restart_seconds 秒满负荷的燃油 → 自动重新发电。没接入电网时发不出电，也不烧油。
        /// </summary>
        private static void StepGenerator(CampaignState state, Producer p, int ticks, int worldHz)
        {
            string id = p.Id;
            ItemDef fuel = p.Fluids.Length > 0 ? p.Fluids[0].Fluid : null;
            int h = p.Fluids.Length > 0 ? p.Rec.FluidHandles[0] : -1;
            PipeKernel k = PipeNetworkService.IsRunning ? PipeNetworkService.Kernel : null;
            long buffer = h >= 0 && k != null ? k.ConsumerBuffer(h) : 0;
            if (!p.Rec.Fueled && buffer >= GeneratorRestartMl(p))
            {
                p.Rec.Fueled = true;
                p.Rec.Progress = 0;
                HomeValleyPowerGrid.SetGeneratorFueled(state, id, true);
            }
            bool connected = HomeValleyPowerGrid.IsConnected(state, id);
            if (p.Rec.Fueled)
            {
                float load = connected ? HomeValleyPowerGrid.GeneratorLoad(state, id) : 0f;
                long perMinute = (long)Math.Round(p.Def.FluidLpm * 1000.0 * load);
                long denom = 60L * Math.Max(1, worldHz);
                long num = p.Rec.Progress + perMinute * ticks;
                long want = num / denom;
                long took = want > 0 && h >= 0 && k != null ? k.TakeConsumerBuffer(h, want) : 0;
                p.Rec.FuelBurnedMl += took;
                ProductionStats.RecordFluidMl(state, fuel, took, produced: false); // FG4-ECO-08：烧油 = 消耗
                if (took < want)
                {
                    // 烧空：停机，电网立即重新结算；警告一次（可定位），钩子。
                    p.Rec.Progress = 0;
                    p.Rec.Fueled = false;
                    HomeValleyPowerGrid.SetGeneratorFueled(state, id, false);
                    NotificationCenter.Post("power_fuel_out", GameText.Format("power.notify.fuel_out", Feedback.FeedbackCues.BuildingLabel(id)),
                        new Vector3(p.Building.Position.x, 0f, p.Building.Position.y));
                    GuidanceHooks.Raise(GuidanceHooks.EnergyFirstFuelOut);
                    FuelOutCount++;
                    ProductionStats.NoteFuelOut(state); // FG4-ECO-08：耗尽次数进存档（统计面板“物流与能源”）
                }
                else
                {
                    p.Rec.Progress = num - want * denom;
                    p.WorkedThisStep = took > 0;
                }
            }
            if (!connected)
            {
                Set(p, ProdState.OutputBlocked, ProdReason.GeneratorUnconnected);
            }
            else if (!p.Rec.Fueled)
            {
                Set(p, ProdState.MissingFluid, ProdReason.NoFuel, fuel, 0, GeneratorRestartMl(p), buffer);
            }
            else if (HomeValleyPowerGrid.GeneratorLoad(state, id) <= 0f)
            {
                Set(p, ProdState.Idle, ProdReason.GeneratorIdle);
            }
            else
            {
                p.WorkedThisStep = true;
                Set(p, ProdState.Working, ProdReason.WorkingGenerator);
            }
        }

        /// <summary>自检读：累计发生过几次燃油耗尽。</summary>
        public static int FuelOutCount { get; private set; }

        private static void StepWaste(Producer p)
        {
            if (!PipeNetworkService.IsRunning || p.Fluids.Length == 0)
            {
                Set(p, ProdState.MissingFluid, ProdReason.WasteIdle, null, 0);
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            int h = p.Rec.FluidHandles[0];
            int lpm = ConsumerLpm(p, p.Fluids[0].Def);
            if (h >= 0 && k.TryGetConsumer(h, out PipeConsumerInfo ci))
            {
                // FG4-ECO-08：废液池收到的流体（销毁）= 消耗；按内核累计送达量的差额记（基准存在 ProducerRecord 里随存档：读档后接着对账；第一次见到这个口只记基准，口重登记后累计量变小也只重设基准）。
                if (p.Rec.WasteSeenHandle == h && ci.TotalDeliveredMl > p.Rec.WasteSeenMl && ci.Network >= 0 && k.TryGetNetworkInfo(ci.Network, out PipeNetInfo wn)
                    && ItemCatalog.TryGetByFluid(wn.Fluid, out ItemDef wasted))
                {
                    ProductionStats.RecordFluidMl(_state, wasted, ci.TotalDeliveredMl - p.Rec.WasteSeenMl, produced: false);
                }
                p.Rec.WasteSeenHandle = h;
                p.Rec.WasteSeenMl = ci.TotalDeliveredMl;
                if (ci.LitersPerMinute != lpm)
                {
                    k.SetConsumer(h, lpm, ci.Priority);
                }
                if (!Powered(p.Building))
                {
                    Set(p, ProdState.NoPower, p.Building.PowerState == BuildingPowerState.Brownout ? ProdReason.PowerBrownout : ProdReason.PowerUnconnected);
                    return;
                }
                if (ci.Network >= 0 && ci.LastDeliveredMl > 0 && k.TryGetNetworkInfo(ci.Network, out PipeNetInfo n))
                {
                    p.WorkedThisStep = true;
                    Set(p, ProdState.Working, ProdReason.WorkingWaste, ItemCatalog.TryGetByFluid(n.Fluid, out ItemDef fl) ? fl : null, 0);
                    return;
                }
            }
            else if (!Powered(p.Building))
            {
                Set(p, ProdState.NoPower, p.Building.PowerState == BuildingPowerState.Brownout ? ProdReason.PowerBrownout : ProdReason.PowerUnconnected);
                return;
            }
            Set(p, ProdState.MissingFluid, ProdReason.WasteIdle, null, 0);
        }

        // ── FG4-ECO-03：固件刻录台 ────────────────────────────────────────────────

        /// <summary>刻录目标能不能刻：没选 = 待机；选的不是固件或还没破解 = 待机并写明。</summary>
        private static bool CheckBurnTarget(CampaignState state, Producer p)
        {
            string t = p.Rec.BurnTarget;
            if (string.IsNullOrEmpty(t))
            {
                Set(p, ProdState.Idle, ProdReason.NoBurnTarget);
                return false;
            }
            if (!IsBurnable(state, t))
            {
                Set(p, ProdState.Idle, ProdReason.BurnTargetLocked);
                p.ReasonTarget = t;
                return false;
            }
            return true;
        }

        /// <summary>能刻录的固件：已破解（已解锁）的固件（与信号核“刻印”同一张清单，<see cref="Signal.SignalCoreService.PrintableFirmware"/>）。</summary>
        public static bool IsBurnable(CampaignState state, string firmwareId) =>
            !string.IsNullOrEmpty(firmwareId) && Signal.FirmwareKinds.IsFirmware(firmwareId)
            && (Content.MechanicalContentUnlock.IsUnlocked(state, firmwareId) || Primitive.FusionHooks.IsBurnableFusion(state, firmwareId)); // 熔合配方的量产入口留给 FG5-RND-04

        /// <summary>
        /// 选刻录目标（<paramref name="firmwareId"/> 为空 = 取消、待机）。正在刻的那份作废、已扣的芯片基板退回输入缓存（不丢料）；记进“上一次的设置”。
        /// </summary>
        public static bool TrySetBurnTarget(CampaignState state, string buildingId, string firmwareId, out string message)
        {
            message = null;
            if (!TryGet(state, buildingId, out Producer p) || !p.IsBurner)
            {
                message = GameText.Get("grid.reason.no_building");
                return false;
            }
            firmwareId ??= string.Empty;
            if (firmwareId.Length > 0 && !Signal.FirmwareKinds.IsFirmware(firmwareId))
            {
                message = GameText.Format("prod.reason.burn_unknown", firmwareId);
                return false;
            }
            if (firmwareId.Length > 0 && !IsBurnable(state, firmwareId))
            {
                message = GameText.Format("prod.reason.burn_target_locked", Signal.FirmwareKinds.DisplayName(firmwareId) ?? firmwareId);
                return false;
            }
            ProducerRecord r = p.Rec;
            if (r.BurnTarget != firmwareId)
            {
                VoidRunningCycle(p);
                r.BurnTarget = firmwareId;
                Revision++;
            }
            r.Inherited = false;
            Remember(state, p.Def.TypeId, null, firmwareId, rememberRecipe: false);
            message = firmwareId.Length > 0
                ? GameText.Format("prod.panel.burn_changed", Signal.FirmwareKinds.DisplayName(firmwareId) ?? firmwareId)
                : GameText.Get("prod.panel.burn_cleared");
            return true;
        }

        /// <summary>正在做的周期作废：已扣的固体退回输入缓存、流体退回口里的缓存（换配方 / 换刻录目标 / 复制设置共用）。</summary>
        private static void VoidRunningCycle(Producer p)
        {
            ProducerRecord r = p.Rec;
            if (r.Running && p.Recipe != null)
            {
                foreach (RecipeLine l in p.Recipe.Lines)
                {
                    if (l.Role != RecipeRole.In)
                    {
                        continue;
                    }
                    if (l.Item.Form == ItemForm.Fluid)
                    {
                        int i = FluidPortIndex(p, l.Item, false);
                        if (i >= 0)
                        {
                            p.Rec.FluidHeld[i] += l.Amount * 1000L;
                            _heldPending = true;
                        }
                    }
                    else
                    {
                        Add(ref r.In, l.Item.Id, l.Amount);
                    }
                }
            }
            r.Running = false;
            r.Progress = 0;
            r.Duration = 0;
        }

        // ── FG4-ECO-03：配方记忆（卡片“配方选择记住上一次的设置”）──────────────────────────

        /// <summary>建筑格网分类是“制造”。</summary>
        public static bool IsManufacturing(string typeId) =>
            GridContent.TryGetBuilding(typeId, out GameConfig.fg.BuildingGrid g) && g.Category == "manufacturing";

        /// <summary>这类建筑“上一次的设置”（没有 = null）。</summary>
        public static RecipeMemoryRecord MemoryOf(CampaignState state, string typeId)
        {
            foreach (RecipeMemoryRecord m in state?.Economy?.RecipeMemory ?? Array.Empty<RecipeMemoryRecord>())
            {
                if (m != null && m.TypeId == typeId)
                {
                    return m;
                }
            }
            return null;
        }

        private static void Remember(CampaignState state, string typeId, string recipeId, string burnTarget, bool rememberRecipe)
        {
            if (state?.Economy == null || string.IsNullOrEmpty(typeId))
            {
                return;
            }
            RecipeMemoryRecord m = MemoryOf(state, typeId);
            if (m == null)
            {
                m = new RecipeMemoryRecord { TypeId = typeId };
                var list = new List<RecipeMemoryRecord>(state.Economy.RecipeMemory ?? Array.Empty<RecipeMemoryRecord>()) { m };
                list.Sort((a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));
                state.Economy.RecipeMemory = list.ToArray();
            }
            if (rememberRecipe)
            {
                m.RecipeId = recipeId ?? string.Empty;
            }
            if (burnTarget != null)
            {
                m.BurnTarget = burnTarget;
            }
        }

        /// <summary>新建的生产建筑沿用这类建筑的“上一次的设置”（多配方建筑的配方、刻录台的目标）；配方已经不属于这座建筑时不沿用。</summary>
        private static void ApplyMemory(CampaignState state, ProducerDef def, ProducerRecord rec)
        {
            RecipeMemoryRecord m = MemoryOf(state, def.TypeId);
            if (m == null)
            {
                return;
            }
            bool any = false;
            if (def.Mode == ProducerMode.Recipe && def.FixedRecipe == null && !string.IsNullOrEmpty(m.RecipeId)
                && ItemCatalog.TryGetRecipe(m.RecipeId, out RecipeDef r) && def.AllowsRecipe(r))
            {
                rec.RecipeId = m.RecipeId;
                any = true;
            }
            if (def.FixedRecipe != null && def.FixedRecipe.Kind == RecipeKindFirmware && !string.IsNullOrEmpty(m.BurnTarget))
            {
                rec.BurnTarget = m.BurnTarget;
                any = true;
            }
            rec.Inherited = any;
        }

        /// <summary>这座建筑现在的“设置”（配方或刻录目标）的一行说明。</summary>
        public static string SettingText(Producer p)
        {
            if (p.IsBurner)
            {
                string t = p.Rec.BurnTarget;
                return GameText.Format("prod.panel.setting_target", string.IsNullOrEmpty(t) ? GameText.Get("prod.panel.setting_none") : Signal.FirmwareKinds.DisplayName(t) ?? t);
            }
            return GameText.Format("prod.panel.setting_recipe", p.Recipe != null ? p.Recipe.Name : GameText.Get("prod.panel.setting_none"));
        }

        /// <summary>这类建筑有没有可复制的设置（多配方建筑、刻录台）。</summary>
        public static bool HasCopyableSettings(Producer p) =>
            p != null && p.Def.Mode == ProducerMode.Recipe && (p.Def.FixedRecipe == null || p.IsBurner);

        /// <summary>同类的其它生产建筑（按建筑 ID 顺序）。</summary>
        public static void CollectSameType(CampaignState state, Producer p, List<Producer> into)
        {
            into.Clear();
            EnsureIndex(state);
            foreach (Producer o in Ordered)
            {
                if (!ReferenceEquals(o, p) && o.Def.TypeId == p.Def.TypeId)
                {
                    into.Add(o);
                }
            }
        }

        private static readonly List<Producer> SameTypeScratch = new List<Producer>(8);

        /// <summary>
        /// 卡片“复制设置到同类建筑”：把这座建筑的配方（多配方建筑）/ 刻录目标（刻录台）写到家园里其余同类建筑上（规划中 / 施工中 / 关停的也写：建好、恢复后按它工作）。
        /// 换了配方的建筑走 <see cref="TrySetRecipe"/>（正在做的那份作废、料退回）。返回实际改了几座；<paramref name="same"/> = 本来就是这个设置的座数。
        /// </summary>
        public static int CopySettingsToSameType(CampaignState state, string buildingId, out int same, out string message)
        {
            same = 0;
            message = null;
            if (!TryGet(state, buildingId, out Producer p))
            {
                message = GameText.Get("grid.reason.no_building");
                return -1;
            }
            string name = HomeGridService.DisplayName(p.Building.BuildingTypeId);
            if (!HasCopyableSettings(p))
            {
                message = GameText.Format("prod.panel.copy_fixed", name);
                return -1;
            }
            CollectSameType(state, p, SameTypeScratch);
            if (SameTypeScratch.Count == 0)
            {
                message = GameText.Format("prod.panel.copy_none", name);
                return 0;
            }
            var targets = new List<Producer>(SameTypeScratch);
            int changed = 0;
            foreach (Producer o in targets)
            {
                bool did;
                if (p.IsBurner)
                {
                    did = o.Rec.BurnTarget != p.Rec.BurnTarget;
                    if (did)
                    {
                        TrySetBurnTarget(state, o.Id, p.Rec.BurnTarget, out _);
                    }
                }
                else
                {
                    did = !ReferenceEquals(o.Recipe, p.Recipe);
                    if (did)
                    {
                        TrySetRecipe(state, o.Id, p.Recipe?.Id, out _);
                    }
                }
                if (did)
                {
                    changed++;
                }
                else
                {
                    same++;
                }
            }
            GuidanceHooks.Raise(GuidanceHooks.EconomyFirstCopyToSameType);
            message = GameText.Format("prod.panel.copy_done", SettingText(p), changed, name, same);
            return changed;
        }

        // ── FG4-ECO-03：多个输入口（两种固体材料的配方：第 1 个输入口收第 1 种、第 2 个收第 2 种）────────────────────

        /// <summary>role = prod 的输入口在这类建筑里是第几个（按端口表顺序，从 0 起；不是输入口 = -1）。</summary>
        public static int InPortIndex(string typeId, string portKey)
        {
            int i = 0;
            foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(typeId))
            {
                if (row.Role != "prod" || row.Kind == "out")
                {
                    continue;
                }
                if (row.Id == portKey)
                {
                    return i;
                }
                i++;
            }
            return -1;
        }

        /// <summary>当前配方的第 <paramref name="index"/> 种固体输入（没有 = null）。</summary>
        public static ItemDef SolidInputAt(Producer p, int index)
        {
            if (p?.Recipe == null || index < 0)
            {
                return null;
            }
            int i = 0;
            foreach (RecipeLine l in p.Recipe.Lines)
            {
                if (l.Role == RecipeRole.In && l.Item.Form == ItemForm.Solid)
                {
                    if (i == index)
                    {
                        return l.Item;
                    }
                    i++;
                }
            }
            return null;
        }

        /// <summary>收 <paramref name="item"/> 的那个输入口（多输入口建筑按配方里的顺序；只有一个输入口时就是它）。</summary>
        public static BeltPortService.Binding FindInPortFor(Producer p, ItemDef item)
        {
            BeltPortService.Binding first = null;
            int i = 0;
            foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(p.Building.BuildingTypeId))
            {
                if (row.Role != "prod" || row.Kind == "out")
                {
                    continue;
                }
                BeltPortService.Binding bind = BeltPortService.Find(p.Id, row.Id);
                first ??= bind;
                if (item != null && ReferenceEquals(SolidInputAt(p, i), item))
                {
                    return bind;
                }
                i++;
            }
            return first;
        }

        // ── 配方检查（不分配：原因文字由界面按种类现拼）──────────────────────────────

        /// <summary>按 <see cref="RecipeBook.Check"/> 的顺序（先输入、再主产出、再副产品）查一遍；不行就把原因写进 <paramref name="p"/>。</summary>
        private static bool CheckRecipe(CampaignState state, Producer p, RecipeDef recipe)
        {
            var stock = new BuildingStock(p);
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role != RecipeRole.In)
                {
                    continue;
                }
                long have = stock.Get(l.Item);
                if (have < l.Amount)
                {
                    bool fluid = l.Item.Form == ItemForm.Fluid;
                    Set(p, fluid ? ProdState.MissingFluid : ProdState.MissingInput, fluid ? ProdReason.MissingFluid : ProdReason.MissingItem, l.Item,
                        fluid ? FluidPortIndex(p, l.Item, false) : -1, l.Amount, have);
                    return false;
                }
            }
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == RecipeRole.In)
                {
                    continue;
                }
                if (l.Item.Form == ItemForm.Entity && p.IsBurner)
                {
                    // FG4-ECO-03：固件芯片进固件库：存放满了就“输出堵塞”，写明存放数量与办法（不刻出来放进“待领取”堆着）。
                    if (!_fwStorageKnown)
                    {
                        // O(芯片数 + 建筑数 + 生产建筑数)：每个生产步最多一次（堵着的刻录台每步都会重试开工，不能每座都数一遍）。
                        // 已占用 = 存放里的芯片 + 正在刻的芯片（多座刻录台同时开工不会超出上限，刻好的芯片不会进“待领取”）。
                        _fwStorageHave = Primitive.PrimitiveInventory.BagCount(state) + BurningChips();
                        _fwStorageCap = Primitive.PrimitiveInventory.CapacityOf(state);
                        _fwStorageKnown = true;
                    }
                    int have = _fwStorageHave;
                    int cap = _fwStorageCap;
                    if (have + Math.Max(1, l.Amount) > cap)
                    {
                        Set(p, ProdState.OutputBlocked, ProdReason.FirmwareStorageFull, l.Item, -1, cap, have);
                        return false;
                    }
                    continue;
                }
                long space = stock.Space(l.Item);
                if (space < l.Amount)
                {
                    bool by = l.Role == RecipeRole.Byproduct;
                    Set(p, ProdState.OutputBlocked, by ? ProdReason.ByproductNoRoom : ProdReason.NoRoom, l.Item,
                        l.Item.Form == ItemForm.Fluid ? FluidPortIndex(p, l.Item, true) : -1, l.Amount, Math.Max(0, space));
                    return false;
                }
            }
            return true;
        }

        /// <summary>一个刻录周期刻出几枚芯片（配方里实体产出行之和，每行至少 1）。</summary>
        private static int ChipsPerCycle(RecipeDef recipe)
        {
            int n = 0;
            if (recipe == null)
            {
                return 0;
            }
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role != RecipeRole.In && l.Item.Form == ItemForm.Entity)
                {
                    n += Math.Max(1, l.Amount);
                }
            }
            return n;
        }

        /// <summary>正在刻的芯片数（运转中的刻录台本周期的产出）。</summary>
        private static int BurningChips()
        {
            int n = 0;
            foreach (Producer q in Ordered)
            {
                if (q.IsBurner && q.Rec != null && q.Rec.Running)
                {
                    n += ChipsPerCycle(q.Recipe);
                }
            }
            return n;
        }

        public static int FluidPortIndex(Producer p, ItemDef fluid, bool output)
        {
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                FluidPortDef d = p.Fluids[i].Def;
                if (d.IsOutput == output && ReferenceEquals(d.Fluid, fluid))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>建筑自己的缓存当配方库存：固体 = 输入 / 输出缓存；流体 = 管线内核里的消费者缓存 / 供给者存量（配方里流体按升，内核按毫升）。
        /// struct 不分配；同一个周期的扣料与产出走这里（全有或全无由开工前的检查保证）。</summary>
        public readonly struct BuildingStock : IRecipeStock
        {
            private readonly Producer _p;

            public BuildingStock(Producer p)
            {
                _p = p;
            }

            public bool Holds(ItemDef item)
            {
                if (item == null || _p.Recipe == null)
                {
                    return false;
                }
                foreach (RecipeLine l in _p.Recipe.Lines)
                {
                    if (ReferenceEquals(l.Item, item))
                    {
                        return true;
                    }
                }
                return false;
            }

            public long Get(ItemDef item)
            {
                if (item.Form == ItemForm.Fluid)
                {
                    int i = FluidPortIndex(_p, item, false);
                    int h = i >= 0 ? _p.Rec.FluidHandles[i] : -1;
                    return h >= 0 && PipeNetworkService.IsRunning ? PipeNetworkService.Kernel.ConsumerBuffer(h) / 1000L : 0;
                }
                return ProductionService.Count(_p.Rec.In, item.Id);
            }

            public long Space(ItemDef item)
            {
                if (item.Form == ItemForm.Fluid)
                {
                    int i = FluidPortIndex(_p, item, true);
                    int h = i >= 0 ? _p.Rec.FluidHandles[i] : -1;
                    // 建筑自己还留着没放回口里的产出也占地方（不让它无限堆在建筑里）。
                    return h >= 0 && PipeNetworkService.IsRunning ? Math.Max(0, PipeNetworkService.Kernel.ProducerSpace(h) - _p.Rec.FluidHeld[i]) / 1000L : 0;
                }
                return Math.Max(0, OutCapacity(_p, item) - ProductionService.Count(_p.Rec.Out, item.Id));
            }

            public void Add(ItemDef item, long amount)
            {
                if (item.Form == ItemForm.Fluid)
                {
                    int i = FluidPortIndex(_p, item, true);
                    if (i < 0)
                    {
                        return;
                    }
                    int h = _p.Rec.FluidHandles[i];
                    long ml = amount * 1000L;
                    if (h >= 0 && PipeNetworkService.IsRunning)
                    {
                        ml -= PipeNetworkService.Kernel.AddProducerStock(h, ml);
                    }
                    // 口还没登记（关停 / 受损恢复后到下一次对账之间做完了这份）或口里放不下：留在建筑里，下次对账放回口里，不丢。
                    if (ml > 0)
                    {
                        _p.Rec.FluidHeld[i] += ml;
                        _heldPending = true;
                    }
                    return;
                }
                ProductionService.Add(ref _p.Rec.Out, item.Id, (int)amount);
            }

            public void Remove(ItemDef item, long amount)
            {
                if (item.Form == ItemForm.Fluid)
                {
                    int i = FluidPortIndex(_p, item, false);
                    int h = i >= 0 ? _p.Rec.FluidHandles[i] : -1;
                    if (h >= 0 && PipeNetworkService.IsRunning)
                    {
                        PipeNetworkService.Kernel.TakeConsumerBuffer(h, amount * 1000L);
                    }
                    return;
                }
                ProductionService.Add(ref _p.Rec.In, item.Id, -(int)amount);
            }
        }

        // ── 缓存 ─────────────────────────────────────────────────────────────────

        public static int Count(ItemStackRecord[] stacks, string itemId)
        {
            if (stacks == null)
            {
                return 0;
            }
            for (int i = 0; i < stacks.Length; i++)
            {
                ItemStackRecord s = stacks[i];
                if (s != null && s.ItemId == itemId)
                {
                    return s.Amount;
                }
            }
            return 0;
        }

        public static int Total(ItemStackRecord[] stacks)
        {
            int t = 0;
            if (stacks != null)
            {
                foreach (ItemStackRecord s in stacks)
                {
                    if (s != null)
                    {
                        t += Math.Max(0, s.Amount);
                    }
                }
            }
            return t;
        }

        private static ItemStackRecord FirstNonEmpty(ItemStackRecord[] stacks)
        {
            if (stacks != null)
            {
                foreach (ItemStackRecord s in stacks)
                {
                    if (s != null && s.Amount > 0)
                    {
                        return s;
                    }
                }
            }
            return null;
        }

        /// <summary>改一种物品的数量（新种类出现时数组加一格：只在第一次见到这种物品时分配）。</summary>
        public static void Add(ref ItemStackRecord[] stacks, string itemId, int delta)
        {
            stacks ??= Array.Empty<ItemStackRecord>();
            for (int i = 0; i < stacks.Length; i++)
            {
                ItemStackRecord s = stacks[i];
                if (s != null && s.ItemId == itemId)
                {
                    s.Amount = Math.Max(0, s.Amount + delta);
                    return;
                }
            }
            if (delta <= 0)
            {
                return;
            }
            var next = new ItemStackRecord[stacks.Length + 1];
            Array.Copy(stacks, next, stacks.Length);
            next[stacks.Length] = new ItemStackRecord { ItemId = itemId, Amount = delta };
            stacks = next;
        }

        /// <summary>输入缓存容量：配方建筑 = 该物品在当前配方里的用量 × 批数；回收站 = 总件数。</summary>
        public static int InCapacity(Producer p, ItemDef item)
        {
            if (p.Def.Mode == ProducerMode.Recycler)
            {
                return Math.Max(1, p.Def.InBatches);
            }
            if (p.Recipe == null || item == null)
            {
                return 0;
            }
            foreach (RecipeLine l in p.Recipe.Lines)
            {
                if (l.Role == RecipeRole.In && ReferenceEquals(l.Item, item))
                {
                    return Math.Max(1, l.Amount * Math.Max(1, p.Def.InBatches));
                }
            }
            return 0;
        }

        /// <summary>输出缓存容量：配方建筑 = 产量 × 批数；回收站 / 提取钻 = 件数。</summary>
        public static int OutCapacity(Producer p, ItemDef item)
        {
            if (p.Def.Mode != ProducerMode.Recipe)
            {
                return Math.Max(1, p.Def.OutBatches);
            }
            if (p.Recipe == null || item == null)
            {
                return 0;
            }
            foreach (RecipeLine l in p.Recipe.Lines)
            {
                if (l.Role != RecipeRole.In && ReferenceEquals(l.Item, item))
                {
                    return Math.Max(1, l.Amount * Math.Max(1, p.Def.OutBatches));
                }
            }
            return 0;
        }

        // ── 物品口（BeltPortService.Pump 每个传送带内核步调用，O(1)）────────────────────────────

        /// <summary>
        /// 输入口登记时收什么：配方建筑 = 当前配方里分给这个口的固体（第 1 个输入口收第 1 种……；没有配方 / 用不到这个口 = 什么都不收）；回收站 = 任何固体（缓存一次一种）；
        /// 装配站（FG4-ECO-03）= 机器材料收货集合（<see cref="BeltConst.AcceptSet"/>）。<paramref name="portKey"/> 为空 = 第一个输入口。
        /// </summary>
        public static ushort SinkAcceptFor(CampaignState state, BuildingRecord b, string portKey = null)
        {
            if (AssemblyMaterials.IsStation(b))
            {
                return BeltConst.AcceptSet;
            }
            if (!TryGet(state, b?.BuildingId, out Producer p))
            {
                return BeltConst.AcceptNone;
            }
            if (p.Def.Mode == ProducerMode.Recycler)
            {
                return BeltConst.AcceptAnyOneKind;
            }
            int idx = portKey == null ? 0 : Math.Max(0, InPortIndex(b.BuildingTypeId, portKey));
            ItemDef solid = SolidInputAt(p, idx);
            return solid != null && solid.BeltId != 0 ? solid.BeltId : BeltConst.AcceptNone;
        }

        private static ItemDef SolidInputAtRecipe(RecipeDef recipe, int index)
        {
            if (recipe == null || index < 0)
            {
                return null;
            }
            int i = 0;
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == RecipeRole.In && l.Item.Form == ItemForm.Solid)
                {
                    if (i == index)
                    {
                        return l.Item;
                    }
                    i++;
                }
            }
            return null;
        }

        public static ItemDef SolidInput(Producer p)
        {
            if (p?.Recipe == null)
            {
                return null;
            }
            foreach (RecipeLine l in p.Recipe.Lines)
            {
                if (l.Role == RecipeRole.In && l.Item.Form == ItemForm.Solid)
                {
                    return l.Item;
                }
            }
            return null;
        }

        /// <summary>输出口登记时推什么（之后按输出缓存轮转）。</summary>
        public static ushort SourceItemFor(CampaignState state, BuildingRecord b)
        {
            if (TryGet(state, b?.BuildingId, out Producer p))
            {
                ItemStackRecord s = FirstNonEmpty(p.Rec.Out);
                if (s != null && ItemCatalog.TryGet(s.ItemId, out ItemDef d) && d.BeltId != 0)
                {
                    return d.BeltId;
                }
                ItemDef main = p.Def.Mode == ProducerMode.Drill ? p.VeinOre
                    : p.Def.Mode == ProducerMode.Recycler ? (ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef sc) ? sc : null)
                    : MainSolidOutput(p);
                if (main != null && main.BeltId != 0)
                {
                    return main.BeltId;
                }
            }
            return BeltItems.ScrapId;
        }

        /// <summary>端口面板：生产建筑的物品口收什么 / 推什么。</summary>
        public static string PortAcceptLine(CampaignState state, BuildingRecord b, bool output, string portKey = null)
        {
            if (AssemblyMaterials.IsStation(b))
            {
                return AssemblyMaterials.PortAcceptLine(output);
            }
            if (!TryGet(state, b?.BuildingId, out Producer p))
            {
                return null;
            }
            if (p.IsBurner && !output)
            {
                string t = p.Rec.BurnTarget;
                return GameText.Format("prod.port.accept_burner", string.IsNullOrEmpty(t) ? GameText.Get("prod.panel.setting_none") : Signal.FirmwareKinds.DisplayName(t) ?? t);
            }
            if (output)
            {
                ItemDef main = p.Def.Mode == ProducerMode.Drill ? p.VeinOre
                    : p.Def.Mode == ProducerMode.Recycler ? (ItemCatalog.TryGet(ItemCatalog.ScrapId, out ItemDef sc) ? sc : null)
                    : MainSolidOutput(p);
                return main != null ? GameText.Format("prod.port.push", main.Name) : null;
            }
            if (p.Def.Mode == ProducerMode.Recycler)
            {
                return GameText.Get("prod.port.accept_any");
            }
            if (p.Recipe == null)
            {
                return GameText.Get("prod.port.accept_no_recipe");
            }
            int idx = portKey == null ? 0 : Math.Max(0, InPortIndex(b.BuildingTypeId, portKey));
            ItemDef solid = SolidInputAt(p, idx);
            if (solid == null && idx > 0 && SolidInput(p) != null)
            {
                return GameText.Get("prod.port.accept_unused");
            }
            return solid != null ? GameText.Format("prod.port.accept_item", solid.Name, p.Recipe.Name) : GameText.Get("prod.port.accept_no_solid");
        }

        private static ItemDef MainSolidOutput(Producer p)
        {
            if (p?.Recipe == null)
            {
                return null;
            }
            foreach (RecipeLine l in p.Recipe.Lines)
            {
                if (l.Role != RecipeRole.In && l.Item.Form == ItemForm.Solid)
                {
                    return l.Item;
                }
            }
            return null;
        }

        /// <summary>
        /// 一个 role = prod 的端口：输入口把内核缓存里的件收进建筑的输入缓存（放得下多少收多少）；输出口把输出缓存补到端口待推（一次一种，推完一批换下一种）。
        /// </summary>
        public static void PumpPort(CampaignState state, BeltPortService.Binding bind, BeltKernel k, int pending, int buffered, ushort kind, int sourceBuffer)
        {
            if (!TryGet(state, bind.BuildingId, out Producer p))
            {
                // FG4-ECO-03：装配站的输入口（role = prod，但它不是生产建筑）：机器材料进装配站材料缓存。
                if (!bind.IsOutput && bind.PortKey != null && bind.PortKey.StartsWith(HomeValleyLayout.BuildingTypeAssemblyStation + ".", StringComparison.Ordinal))
                {
                    AssemblyMaterials.PumpPort(state, bind, k, buffered, kind);
                }
                return;
            }
            if (bind.IsOutput)
            {
                if (pending >= sourceBuffer)
                {
                    return;
                }
                ItemDef cur = BeltItems.Def(kind);
                int have = cur != null ? Count(p.Rec.Out, cur.Id) : 0;
                if (pending <= 0 && have <= 0)
                {
                    // 上一批推完：换到输出缓存里下一种有货的固体。
                    ItemStackRecord next = FirstBeltStack(p.Rec.Out);
                    if (next == null || !ItemCatalog.TryGet(next.ItemId, out cur))
                    {
                        return;
                    }
                    k.SetSourceItem(bind.PortId, cur.BeltId);
                    have = next.Amount;
                }
                if (cur == null || cur.BeltId == 0 || have <= 0)
                {
                    return;
                }
                int take = Math.Min(have, sourceBuffer - Math.Max(0, pending));
                if (take > 0)
                {
                    Add(ref p.Rec.Out, cur.Id, -take);
                    k.AddSourceItems(bind.PortId, take);
                }
                return;
            }
            if (buffered <= 0)
            {
                return;
            }
            // 只收一种物品的输入口（配方建筑）内核不记缓存里是哪一种（ItemType = 0）：就是当前配方要的那种固体；回收站（一次一种）内核记着种类。
            if (kind == 0 && p.Def.Mode == ProducerMode.Recipe)
            {
                ItemDef solid = SolidInputAt(p, Math.Max(0, InPortIndex(p.Building.BuildingTypeId, bind.PortKey)));
                kind = solid != null ? solid.BeltId : (ushort)0;
            }
            ItemDef item = BeltItems.Def(kind);
            if (item == null)
            {
                // 物品表里没有的编号：放到建筑旁边的地上（不消失、不堵带）。
                int n0 = k.TakeFromSink(bind.PortId, buffered);
                if (n0 > 0)
                {
                    HomeValleyConstruction.ReturnMaterials(state, p.Building.Position, BeltItems.ResourceOf(kind), n0,
                        "prod-port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":unknown:" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                return;
            }
            int room = p.Def.Mode == ProducerMode.Recycler
                ? InCapacity(p, item) - Total(p.Rec.In)
                : InCapacity(p, item) - Count(p.Rec.In, item.Id);
            int n = Math.Min(buffered, room);
            if (n > 0)
            {
                Add(ref p.Rec.In, item.Id, k.TakeFromSink(bind.PortId, n));
            }
        }

        private static ItemStackRecord FirstBeltStack(ItemStackRecord[] stacks)
        {
            if (stacks != null)
            {
                foreach (ItemStackRecord s in stacks)
                {
                    if (s != null && s.Amount > 0 && ItemCatalog.TryGet(s.ItemId, out ItemDef d) && d.BeltId != 0)
                    {
                        return s;
                    }
                }
            }
            return null;
        }

        // ── 玩家操作 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 选配方（多配方建筑；<paramref name="recipeId"/> 为空 = 取消配方、待机）。正在做的周期作废、已扣的料退回输入缓存；
        /// 输入缓存里新配方用不上的件、输入口缓存里的件退回仓库（放不下落地，机器之后搬走）；输入口改收新配方要的固体。
        /// </summary>
        public static bool TrySetRecipe(CampaignState state, string buildingId, string recipeId, out string message) =>
            SetRecipeCore(state, buildingId, recipeId, remember: true, out message);

        /// <summary>
        /// FG4-ECO-06：常驻规则接管 / 恢复配方走这里——与 <see cref="TrySetRecipe"/> 同一套切换（周期作废、料退回、输入口改收），
        /// 但不改写“这类建筑记住的上一次配方”：那是玩家的选择，规则不替玩家改（新建的同类建筑不会沿用规则临时排产的配方，FGR-BASE-020）。
        /// </summary>
        public static bool TrySetRecipeForRule(CampaignState state, string buildingId, string recipeId, out string message) =>
            SetRecipeCore(state, buildingId, recipeId, remember: false, out message);

        private static bool SetRecipeCore(CampaignState state, string buildingId, string recipeId, bool remember, out string message)
        {
            message = null;
            if (!TryGet(state, buildingId, out Producer p) || p.Def.Mode != ProducerMode.Recipe)
            {
                message = GameText.Get("grid.reason.no_building");
                return false;
            }
            RecipeDef next = null;
            if (!string.IsNullOrEmpty(recipeId) && (!ItemCatalog.TryGetRecipe(recipeId, out next) || !p.Def.AllowsRecipe(next)))
            {
                message = GameText.Format("eco.reason.unknown_recipe", recipeId);
                return false;
            }
            if (p.Def.FixedRecipe != null)
            {
                message = GameText.Format("prod.panel.recipe_fixed", p.Def.FixedRecipe.Name);
                return false;
            }
            if (ReferenceEquals(next, p.Recipe))
            {
                if (!remember)
                {
                    return true; // 规则要的配方已经在跑：什么都不动。
                }
                p.Rec.Inherited = false;
                Remember(state, p.Def.TypeId, next?.Id ?? string.Empty, null, rememberRecipe: true);
                message = next != null ? GameText.Format("prod.panel.recipe_changed", next.Name) : GameText.Get("prod.panel.recipe_cleared");
                return true;
            }
            ProducerRecord r = p.Rec;
            RecipeDef oldRecipe = p.Recipe;
            // 正在做的周期作废：已扣的固体退回输入缓存（流体退回口里的缓存），不丢料。
            VoidRunningCycle(p);
            r.RecipeId = next?.Id ?? string.Empty;
            r.Inherited = false;
            p.Recipe = next;
            // FG4-ECO-03：记住这类建筑上一次的选择（新建的同类建筑沿用）。规则的接管 / 恢复不记（FG4-ECO-06）。
            if (remember)
            {
                Remember(state, p.Def.TypeId, r.RecipeId, null, rememberRecipe: true);
            }
            // 新配方用不上的输入退回仓库。
            int returned = 0;
            foreach (ItemStackRecord s in r.In)
            {
                if (s == null || s.Amount <= 0)
                {
                    continue;
                }
                bool used = false;
                if (next != null)
                {
                    foreach (RecipeLine l in next.Lines)
                    {
                        if (l.Role == RecipeRole.In && l.Item.Id == s.ItemId)
                        {
                            used = true;
                        }
                    }
                }
                if (!used)
                {
                    HomeValleyConstruction.ReturnMaterials(state, p.Building.Position, ItemCatalog.ResourceTypeOf(s.ItemId), s.Amount,
                        "prod-recipe:" + buildingId + ":" + s.ItemId + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                    returned += s.Amount;
                    s.Amount = 0;
                }
            }
            // 输入口：缓存里的件退回仓库，再改收新配方要的固体。
            if (BeltNetworkService.IsRunning)
            {
                foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(p.Building.BuildingTypeId))
                {
                    if (row.Role != "prod" || row.Kind != "in")
                    {
                        continue;
                    }
                    BeltPortService.Binding bind = BeltPortService.Find(buildingId, row.Id);
                    if (bind == null || bind.PortId < 0)
                    {
                        continue;
                    }
                    BeltKernel k = BeltNetworkService.Kernel;
                    ushort nextAccept = SinkAcceptFor(state, p.Building, row.Id);
                    if (k.TryGetPortCounts(bind.PortId, out _, out int buffered, out ushort kind) && buffered > 0)
                    {
                        // 只收一种物品的输入口内核不记种类（0）：缓存里的就是旧配方给这个口的那种固体（多输入口按口的顺序）。
                        ItemDef oldSolid = SolidInputAtRecipe(oldRecipe, InPortIndex(p.Building.BuildingTypeId, row.Id));
                        if (kind == 0 && oldSolid != null)
                        {
                            kind = oldSolid.BeltId;
                        }
                    }
                    if (buffered > 0 && kind != 0 && kind == nextAccept)
                    {
                        // FG4-ECO-03：新配方这个口收的还是同一种（例如零件工坊“零件”↔“结构材”都用合金）：端口缓存原样留着，不来回搬运。
                        buffered = 0;
                    }
                    if (buffered > 0)
                    {
                        int n = k.TakeFromSink(bind.PortId, buffered);
                        HomeValleyConstruction.ReturnMaterials(state, p.Building.Position, BeltItems.ResourceOf(kind), n,
                            "prod-recipe-port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                        returned += n;
                    }
                    k.SetSinkAccept(bind.PortId, nextAccept);
                }
            }
            Revision++;
            message = next != null ? GameText.Format("prod.panel.recipe_changed", next.Name) : GameText.Get("prod.panel.recipe_cleared");
            if (returned > 0)
            {
                message += "\n" + GameText.Format("prod.panel.recipe_returned", returned);
            }
            return true;
        }

        // ── FG4-ECO-05：效率与最近 10 分钟产出（FGR-ECO-011；按桶聚合，不每帧遍历）────────────────────────

        /// <summary>每桶多少游戏步（building.stats.bucket_seconds × 世界频率）。</summary>
        public static long StatBucketTicks(int worldHz) => Math.Max(1L, (long)Math.Round(Math.Max(1.0, GridContent.Tuning("building.stats.bucket_seconds")) * Math.Max(1, worldHz)));

        /// <summary>窗口有几桶（building.stats.window_minutes × 60 ÷ 桶长）。</summary>
        public static int StatBuckets => Math.Max(1, (int)Math.Round(Math.Max(1.0, GridContent.Tuning("building.stats.window_minutes")) * 60.0
                                                                    / Math.Max(1.0, GridContent.Tuning("building.stats.bucket_seconds"))));

        /// <summary>这座建筑有没有“效率”（按配方 / 周期生产的：配方建筑、提取钻、回收站；连续工作的废液池 / 流体泵 / 燃油发电机没有）。</summary>
        public static bool HasEfficiency(Producer p) =>
            p != null && (p.Def.Mode == ProducerMode.Recipe || p.Def.Mode == ProducerMode.Drill || p.Def.Mode == ProducerMode.Recycler);

        /// <summary>满速一份要几步（没选配方 = 0：不计理论份数）。
        /// 回收站一份的时长看它在做什么（审查 P1）：分解送来的物品按 ItemSeconds、拆脚下废墟按 CycleSeconds——正在做的那一份按它实际的时长，
        /// 空闲时按“下一份会做什么”（输入口有物品 = 分解，否则 = 拆废墟）。</summary>
        private static long CycleTicks(Producer p, int worldHz)
        {
            switch (p.Def.Mode)
            {
                case ProducerMode.Recipe:
                    RecipeDef r = p.Recipe ?? p.Def.FixedRecipe;
                    return r == null || (p.IsBurner && string.IsNullOrEmpty(p.Rec.BurnTarget)) ? 0 : Math.Max(1, r.DurationTicks(worldHz));
                case ProducerMode.Drill:
                    return Math.Max(1, (long)Math.Round(p.Def.CycleSeconds * Math.Max(1, worldHz)));
                case ProducerMode.Recycler:
                    ProducerRecord rec = p.Rec;
                    if (rec.Running && rec.Duration > 0)
                    {
                        return rec.Duration;
                    }
                    float sec = FirstNonEmpty(rec.In) != null ? p.Def.ItemSeconds : p.Def.CycleSeconds;
                    return Math.Max(1, (long)Math.Round(sec * Math.Max(1, worldHz)));
                default:
                    return 0;
            }
        }

        private static int _statWorldHz;
        private static long _statBucketTicks = 1;
        private static int _statBuckets = 1;
        private static long _statIndex;

        /// <summary>当前桶（桶号由 <see cref="Step"/> 开头算好；不在生产步里调用时现算）。</summary>
        private static ProducerStatBucket Bucket(ProducerRecord r, int worldHz)
        {
            if (_statWorldHz != Math.Max(1, worldHz))
            {
                _statWorldHz = Math.Max(1, worldHz);
                _statBucketTicks = StatBucketTicks(_statWorldHz);
                _statBuckets = StatBuckets;
                _statIndex = GameClock.Ticks / _statBucketTicks;
            }
            int n = _statBuckets;
            if (r.Stats == null || r.Stats.Length != n)
            {
                var next = new ProducerStatBucket[n];
                for (int i = 0; i < n; i++)
                {
                    next[i] = new ProducerStatBucket();
                }
                r.Stats = next;
            }
            long idx = _statIndex;
            ProducerStatBucket b = r.Stats[(int)(idx % n)] ?? (r.Stats[(int)(idx % n)] = new ProducerStatBucket());
            if (b.Index != idx)
            {
                b.Index = idx;
                b.Done = 0;
                b.TheoryMilli = 0;
                b.Out = Array.Empty<ItemStackRecord>();
                b.Starve = Array.Empty<ItemStackRecord>();
            }
            return b;
        }

        /// <summary>推进时累加“理论份数”：建成了（运转 / 禁用 / 缺电……都算停工，拉低效率）就按满速计；施工中、已摧毁不计。</summary>
        private static void AccrueTheory(Producer p, int ticks, int worldHz)
        {
            if (!HasEfficiency(p))
            {
                return;
            }
            BuildingConstructionState cs = p.Building.ConstructionState;
            if (cs != BuildingConstructionState.Operational && cs != BuildingConstructionState.Disabled)
            {
                return;
            }
            long cycle = CycleTicks(p, Math.Max(1, worldHz));
            if (cycle <= 0)
            {
                return;
            }
            // 不在每步截断（审查 P1：步长 3、20 秒配方每步应加 2.5 千分份，截断成 2 会让满速显示 125%）：余数留到下一步。
            if (p.TheoryCycle != cycle)
            {
                p.TheoryCycle = cycle;
                p.TheoryRem = 0;
            }
            long num = ticks * 1000L + p.TheoryRem;
            p.TheoryRem = num % cycle;
            Bucket(p.Rec, worldHz).TheoryMilli += num / cycle;
        }

        /// <summary>
        /// FG4-ECO-08（瓶颈查找）：这一步缺料 / 缺流体（缺的是某种具体物品）时，把步数记进这座建筑当前桶的“缺 X”。O(1)，随生产步（不每帧）。
        /// 脚下废墟拆完（回收站没有可拆的）与废液池空闲不算缺料——那不是产线缺某种输入。
        /// </summary>
        private static void RecordStarve(Producer p, int ticks)
        {
            if ((p.State != ProdState.MissingInput && p.State != ProdState.MissingFluid) || p.ReasonItem == null
                || p.Reason == ProdReason.RuinDepleted || p.Reason == ProdReason.WasteIdle)
            {
                return;
            }
            ProducerStatBucket b = Bucket(p.Rec, _statWorldHz);
            Add(ref b.Starve, p.ReasonItem.Id, ticks);
        }

        /// <summary>完成一份：份数与产出记进当前桶。</summary>
        private static void RecordCompletion(Producer p)
        {
            if (!HasEfficiency(p))
            {
                return;
            }
            ProducerRecord r = p.Rec;
            ProducerStatBucket b = Bucket(r, _statWorldHz);
            b.Done++;
            if (p.Def.Mode == ProducerMode.Recipe && p.Recipe != null)
            {
                foreach (RecipeLine l in p.Recipe.Lines)
                {
                    if (l.Role != RecipeRole.In && l.Item != null)
                    {
                        Add(ref b.Out, l.Item.Id, Math.Max(1, l.Amount));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(r.PendingItem) && r.PendingAmount > 0)
            {
                Add(ref b.Out, p.Def.Mode == ProducerMode.Recycler ? ItemCatalog.ScrapId : r.PendingItem, r.PendingAmount);
            }
        }

        /// <summary>
        /// 最近窗口（building.stats.window_minutes）的统计：完成份数、理论份数 × 1000、各产出合计（写进 <paramref name="outputs"/>）。
        /// 返回 false = 不适用（没有效率的建筑）。O(桶数 × 产出种类)，只在面板刷新时。
        /// </summary>
        public static bool TryWindowStats(Producer p, List<ItemStackRecord> outputs, out int done, out long theoryMilli)
        {
            done = 0;
            theoryMilli = 0;
            outputs?.Clear();
            if (!HasEfficiency(p))
            {
                return false;
            }
            int n = StatBuckets;
            long now = GameClock.Ticks / StatBucketTicks(GameClock.StepHz);
            foreach (ProducerStatBucket b in p.Rec.Stats ?? Array.Empty<ProducerStatBucket>())
            {
                if (b == null || b.Index < 0 || b.Index > now || b.Index <= now - n)
                {
                    continue;
                }
                done += b.Done;
                theoryMilli += b.TheoryMilli;
                if (outputs == null)
                {
                    continue;
                }
                foreach (ItemStackRecord o in b.Out ?? Array.Empty<ItemStackRecord>())
                {
                    if (o == null || o.Amount <= 0)
                    {
                        continue;
                    }
                    ItemStackRecord into = outputs.Find(x => x.ItemId == o.ItemId);
                    if (into == null)
                    {
                        outputs.Add(new ItemStackRecord { ItemId = o.ItemId, Amount = o.Amount });
                    }
                    else
                    {
                        into.Amount += o.Amount;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// FG4-ECO-05（FG-GAP-094）：这座建筑流体口里此刻存着的流体（输入口缓存、输出口存量、口没登记时留在建筑身上的）合计毫升；
        /// <paramref name="describe"/> 不为空时按“燃油 12 升”逐种写进去。不是生产建筑 / 没有流体口 = 0。O(流体口数)。
        /// </summary>
        public static long HeldFluidMl(CampaignState state, BuildingRecord b, List<string> describe)
        {
            describe?.Clear();
            if (b == null || !TryGet(state, b.BuildingId, out Producer p) || p.Fluids.Length == 0)
            {
                return 0;
            }
            PipeKernel k = PipeNetworkService.IsRunning ? PipeNetworkService.Kernel : null;
            long total = 0;
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                long ml = p.Rec.FluidHeld != null && i < p.Rec.FluidHeld.Length ? Math.Max(0, p.Rec.FluidHeld[i]) : 0;
                int h = p.Rec.FluidHandles != null && i < p.Rec.FluidHandles.Length ? p.Rec.FluidHandles[i] : -1;
                if (k != null && h >= 0)
                {
                    if (p.Fluids[i].Def.IsOutput && k.TryGetProducer(h, out PipeProducerInfo pi))
                    {
                        ml += Math.Max(0, pi.StockMl);
                    }
                    else if (!p.Fluids[i].Def.IsOutput && k.TryGetConsumer(h, out PipeConsumerInfo ci))
                    {
                        ml += Math.Max(0, ci.BufferMl);
                    }
                }
                if (ml >= 1000)
                {
                    total += ml;
                    describe?.Add(GameText.Format("prod.panel.stack_fluid_held", p.Fluids[i].Fluid?.Name ?? GameText.Get("prod.panel.fluid_any"),
                        (ml / 1000).ToString(CultureInfo.InvariantCulture)));
                }
            }
            return total;
        }

        /// <summary>FG4-ECO-05（FG-GAP-095“清空缓存到仓库”）：输入 / 输出缓存送回仓库（放不下的落在建筑旁边成为地面物，机器之后搬走）；正在做的那一份不动。返回件数。</summary>
        public static int ReturnAllBuffers(CampaignState state, Producer p, string dropId)
        {
            if (state == null || p == null)
            {
                return 0;
            }
            int n = ReturnBuffers(state, p.Rec, p.Building.Position, dropId);
            if (n > 0)
            {
                Revision++;
            }
            return n;
        }

        // ── 拆除 / 退回 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 建筑被拆除（HomeValleyWorkOrders.CompleteDemolish 调）：输入 / 输出缓存与正在做的周期里已扣的固体退回仓库（放不下落地），流体口撤掉（口里的流体随拆除排空，与储罐一致）。
        /// 返回退回的件数（计入“内部缓存”）。
        /// </summary>
        public static int OnDemolished(CampaignState state, BuildingRecord building, string dropId)
        {
            if (state == null || building == null)
            {
                return 0;
            }
            CampaignFgStateDomains.EnsureAll(state);
            ProducerRecord rec = null;
            foreach (ProducerRecord r in state.Economy.Producers)
            {
                if (r != null && r.BuildingId == building.BuildingId)
                {
                    rec = r;
                    break;
                }
            }
            if (rec == null)
            {
                return 0;
            }
            if (TryGet(state, building.BuildingId, out Producer p) && rec.Running && p.Recipe != null)
            {
                foreach (RecipeLine l in p.Recipe.Lines)
                {
                    if (l.Role == RecipeRole.In && l.Item.Form != ItemForm.Fluid)
                    {
                        Add(ref rec.In, l.Item.Id, l.Amount);
                    }
                }
                rec.Running = false;
            }
            else if (rec.Running && !rec.PendingRuin && !string.IsNullOrEmpty(rec.PendingItem)
                     && TryGet(state, building.BuildingId, out Producer rp) && rp.Def.Mode == ProducerMode.Recycler)
            {
                // 回收站正在分解的那一件还没分解完：原样退回。
                Add(ref rec.In, rec.PendingItem, 1);
                rec.Running = false;
            }
            else if (rec.Running && rec.PendingRuin && rec.PendingAmount > 0)
            {
                // 回收站正在拆的那批废墟废料开工时已从废墟储量里扣掉（那格可能已经拆完变空地）：按废料退回仓库，不凭空消失。
                Add(ref rec.Out, ItemCatalog.ScrapId, rec.PendingAmount);
                rec.Running = false;
            }
            int n = ReturnBuffers(state, rec, building.Position, dropId);
            RemoveFluidHandles(rec, keep: false);
            var list = new List<ProducerRecord>(state.Economy.Producers.Length);
            foreach (ProducerRecord r in state.Economy.Producers)
            {
                if (!ReferenceEquals(r, rec))
                {
                    list.Add(r);
                }
            }
            state.Economy.Producers = list.ToArray();
            Revision++;
            return n;
        }

        private static int ReturnBuffers(CampaignState state, ProducerRecord r, Vector2 at, string dropId)
        {
            int n = 0;
            int k = 0;
            foreach (ItemStackRecord[] stacks in new[] { r.In, r.Out })
            {
                if (stacks == null)
                {
                    continue;
                }
                foreach (ItemStackRecord s in stacks)
                {
                    if (s == null || s.Amount <= 0 || string.IsNullOrEmpty(s.ItemId))
                    {
                        continue;
                    }
                    HomeValleyConstruction.ReturnMaterials(state, at, ItemCatalog.ResourceTypeOf(s.ItemId), s.Amount,
                        dropId + ":prod:" + (k++).ToString(CultureInfo.InvariantCulture));
                    n += s.Amount;
                    s.Amount = 0;
                }
            }
            return n;
        }

        // ── 废墟储量（FG04 回收站；DEBT-FG3GEN01-03 / 08）─────────────────────────────────

        private static long CellKey(GridCell c) => ((long)c.X << 32) ^ (uint)c.Y;

        private static void EnsureRuinIndex(CampaignState state)
        {
            if (ReferenceEquals(state.Economy.RuinCells, _indexedRuins))
            {
                return;
            }
            RuinIndex.Clear();
            foreach (RuinCellRecord r in state.Economy.RuinCells ?? Array.Empty<RuinCellRecord>())
            {
                if (r != null)
                {
                    RuinIndex[CellKey(new GridCell(r.X, r.Y))] = r;
                }
            }
            _indexedRuins = state.Economy.RuinCells;
        }

        /// <summary>一格废墟还剩多少废料（没拆过 = eco.ruin.scrap_per_cell；已经不是废墟 = 0）。O(1)。</summary>
        public static int RuinRemaining(CampaignState state, GridCell c)
        {
            if (state.Economy?.RuinCells == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            EnsureRuinIndex(state);
            if (RuinIndex.TryGetValue(CellKey(c), out RuinCellRecord r))
            {
                return Math.Max(0, r.Remaining);
            }
            byte ruin = GridContent.TryTerrainCode("ruin", out byte rc) ? rc : (byte)255;
            return HomeGridService.MapFor(state).GetTerrain(c) == ruin ? RuinScrapPerCell : 0;
        }

        private static void SetRuinRemaining(CampaignState state, GridCell c, int left)
        {
            EnsureRuinIndex(state);
            long key = CellKey(c);
            if (left <= 0)
            {
                if (RuinIndex.Remove(key))
                {
                    state.Economy.RuinCells = Without(state.Economy.RuinCells, c);
                    _indexedRuins = state.Economy.RuinCells;
                }
                return;
            }
            if (RuinIndex.TryGetValue(key, out RuinCellRecord r))
            {
                r.Remaining = left;
                return;
            }
            r = new RuinCellRecord { X = c.X, Y = c.Y, Remaining = left };
            var next = new RuinCellRecord[(state.Economy.RuinCells?.Length ?? 0) + 1];
            if (state.Economy.RuinCells != null)
            {
                Array.Copy(state.Economy.RuinCells, next, state.Economy.RuinCells.Length);
            }
            next[next.Length - 1] = r;
            state.Economy.RuinCells = next;
            RuinIndex[key] = r;
            _indexedRuins = next;
        }

        private static RuinCellRecord[] Without(RuinCellRecord[] a, GridCell c)
        {
            var l = new List<RuinCellRecord>(a.Length);
            foreach (RuinCellRecord r in a)
            {
                if (r != null && !(r.X == c.X && r.Y == c.Y))
                {
                    l.Add(r);
                }
            }
            return l.ToArray();
        }

        /// <summary>一格废墟拆完：变成可建空地（区块差异进存档、寻路与近景 / 地图底图随之更新）；回收站脚下最后一格拆完时通知一次。</summary>
        private static void DepleteCell(CampaignState state, Producer p, GridCell c)
        {
            HomeGridService.MapFor(state).SetTerrain(c, GridContent.TerrainCode("buildable"));
            RuinCellsDepleted++;
            for (int i = 0; i < p.RuinCells.Count; i++)
            {
                if (RuinRemaining(state, p.RuinCells[i]) > 0)
                {
                    return;
                }
            }
            if (!p.Rec.RuinDepletedNotified)
            {
                p.Rec.RuinDepletedNotified = true;
                GuidanceHooks.Raise(GuidanceHooks.EconomyRuinFirstDepleted);
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.ProductionComplete, p.Building.Position,
                    GameText.Format("prod.notify.ruin_depleted", HomeGridService.DisplayName(p.Building.BuildingTypeId), p.RuinCells.Count));
            }
        }

        /// <summary>回收站脚下废墟的剩余储量（废料）与还没拆完的格数。O(脚下废墟格)。</summary>
        public static int RuinLeft(CampaignState state, Producer p, out int cells)
        {
            int total = 0;
            cells = 0;
            foreach (GridCell c in p.RuinCells)
            {
                int left = RuinRemaining(state, c);
                if (left > 0)
                {
                    total += left;
                    cells++;
                }
            }
            return total;
        }

        /// <summary>
        /// FG4-ECO-02（FG4-ECO-01 悬停“各处分布”）：生产建筑里的物品——输入 / 输出缓存（件）与流体口里的流体（升），按物品 ID 累加进 <paramref name="into"/>。
        /// O(生产建筑)，只在物资悬停 / 面板定时重读时调用。
        /// </summary>
        public static void CollectBuffers(CampaignState state, Dictionary<string, long> into)
        {
            if (state?.Economy?.Producers == null || into == null)
            {
                return;
            }
            EnsureIndex(state);
            PipeKernel k = PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state) ? PipeNetworkService.Kernel : null;
            foreach (Producer p in Ordered)
            {
                foreach (ItemStackRecord[] stacks in new[] { p.Rec.In, p.Rec.Out })
                {
                    if (stacks == null)
                    {
                        continue;
                    }
                    foreach (ItemStackRecord st in stacks)
                    {
                        if (st != null && st.Amount > 0 && !string.IsNullOrEmpty(st.ItemId))
                        {
                            into[st.ItemId] = (into.TryGetValue(st.ItemId, out long n) ? n : 0) + st.Amount;
                        }
                    }
                }
                for (int i = 0; i < p.Fluids.Length; i++)
                {
                    ItemDef f = p.Fluids[i].Fluid;
                    if (f == null)
                    {
                        continue;
                    }
                    long ml = p.Rec.FluidHeld[i];
                    int h = p.Rec.FluidHandles[i];
                    if (k != null && h >= 0)
                    {
                        if (p.Fluids[i].Def.IsOutput && k.TryGetProducer(h, out PipeProducerInfo pi))
                        {
                            ml += pi.StockMl;
                        }
                        else if (!p.Fluids[i].Def.IsOutput)
                        {
                            ml += k.ConsumerBuffer(h);
                        }
                    }
                    if (ml > 0)
                    {
                        into[f.Id] = (into.TryGetValue(f.Id, out long n) ? n : 0) + ml / 1000;
                    }
                }
            }
            // FG4-ECO-03：装配站的材料缓存（西侧输入口送来、还没装进机器的材料）同样算“生产建筑缓存（在途）”。
            foreach (ItemStackRecord st in state.Economy.AssemblyBuffer ?? Array.Empty<ItemStackRecord>())
            {
                if (st != null && st.Amount > 0 && !string.IsNullOrEmpty(st.ItemId))
                {
                    into[st.ItemId] = (into.TryGetValue(st.ItemId, out long n) ? n : 0) + st.Amount;
                }
            }
        }

        // ── 震动（FG10 接口）────────────────────────────────────────────────────

        /// <summary>家园震动值（0～eco.vibration.max）。FG10 的掘进蠕虫读它。</summary>
        public static double Vibration(CampaignState state) => state?.Economy?.Vibration ?? 0.0;

        /// <summary>主要的震动来源：现在在工作的提取钻，按每分钟增量从大到小（同样大按建筑 ID），最多 <paramref name="max"/> 座。</summary>
        public static void CollectVibrationSources(List<Producer> into, int max)
        {
            into.Clear();
            foreach (Producer p in Ordered)
            {
                if (p.Def.VibrationPerMinute > 0f && p.State == ProdState.Working)
                {
                    into.Add(p);
                }
            }
            into.Sort((a, b) => a.Def.VibrationPerMinute != b.Def.VibrationPerMinute ? b.Def.VibrationPerMinute.CompareTo(a.Def.VibrationPerMinute) : string.CompareOrdinal(a.Id, b.Id));
            if (into.Count > max)
            {
                into.RemoveRange(max, into.Count - max);
            }
        }

        // ── 文字（界面、悬停、诊断共用；按需现拼）────────────────────────────────────

        public static string StateKey(ProdState s)
        {
            switch (s)
            {
                case ProdState.Working: return "prod.state.working";
                case ProdState.Idle: return "prod.state.idle";
                case ProdState.MissingInput: return "prod.state.missing_input";
                case ProdState.MissingFluid: return "prod.state.missing_fluid";
                case ProdState.NoPower: return "prod.state.no_power";
                case ProdState.OutputBlocked: return "prod.state.output_blocked";
                case ProdState.Building: return "prod.state.building";
                case ProdState.Damaged: return "prod.state.damaged";
                case ProdState.Disabled: return "prod.state.disabled";
                case ProdState.NoResource: return "prod.state.no_resource";
                default: return "prod.state.idle";
            }
        }

        public static string StateText(Producer p) => GameText.Get(StateKey(p.State));

        /// <summary>原因（写明是什么、怎么办，B06）。</summary>
        public static string ReasonText(CampaignState state, Producer p)
        {
            switch (p.Reason)
            {
                case ProdReason.NotBuilt: return GameText.Get("prod.reason.building");
                case ProdReason.Damaged: return GameText.Get("prod.reason.damaged");
                case ProdReason.Disabled: return GameText.Get("prod.reason.disabled");
                case ProdReason.PowerUnconnected: return GameText.Format("prod.reason.no_power", GameText.Get("prod.reason.power_unconnected"));
                case ProdReason.PowerBrownout: return GameText.Format("prod.reason.no_power", GameText.Format("prod.reason.power_brownout", p.Building.PowerPriority));
                case ProdReason.NoVein: return GameText.Get("prod.reason.no_vein");
                case ProdReason.NoRecipe: return GameText.Get("prod.reason.no_recipe");
                case ProdReason.WorkingRecipe: return GameText.Format("prod.reason.working_recipe", p.Recipe?.Name ?? string.Empty);
                case ProdReason.WorkingDrill: return GameText.Format("prod.reason.working_drill", p.ReasonItem?.Name ?? string.Empty);
                case ProdReason.WorkingRuin:
                    return GameText.Format("prod.reason.working_ruin", RuinLeft(state, p, out _).ToString(CultureInfo.InvariantCulture));
                case ProdReason.WorkingItem:
                    return GameText.Format("prod.reason.working_item", p.ReasonItem?.Name ?? string.Empty, p.ReasonNeed.ToString(CultureInfo.InvariantCulture));
                case ProdReason.WorkingWaste:
                    return GameText.Format("prod.reason.working_waste", p.ReasonItem?.Name ?? GameText.Get("prod.panel.fluid_any"),
                        Mathf.RoundToInt(p.Def.FluidLpm).ToString(CultureInfo.InvariantCulture));
                case ProdReason.WasteIdle:
                    return FluidPortHint(p, 0) ?? GameText.Get("prod.reason.waste_idle");
                case ProdReason.WorkingPump:
                    return GameText.Format("prod.reason.pump_working", p.ReasonItem?.Name ?? string.Empty,
                        Mathf.RoundToInt(p.Def.FluidLpm).ToString(CultureInfo.InvariantCulture));
                case ProdReason.NoSource:
                    return GameText.Get("prod.reason.pump_no_source");
                case ProdReason.PumpBlocked:
                    return FluidPortHint(p, 0) ?? string.Empty;
                case ProdReason.RuinDepleted:
                    return Join(GameText.Get("prod.reason.ruin_depleted"), InPortHint(p, null, GameText.Get("prod.panel.any_solid")));
                case ProdReason.MissingItem:
                    return Join(GameText.Format("eco.reason.missing_input", p.ReasonItem?.Name ?? string.Empty, p.ReasonNeed, p.ReasonHave), InPortHint(p, p.ReasonItem));
                case ProdReason.NoBurnTarget:
                    return GameText.Get("prod.reason.no_burn_target");
                case ProdReason.BurnTargetLocked:
                    return GameText.Format("prod.reason.burn_target_locked", Signal.FirmwareKinds.DisplayName(p.ReasonTarget) ?? p.ReasonTarget ?? string.Empty);
                case ProdReason.FirmwareStorageFull:
                    return GameText.Format("prod.reason.firmware_storage_full", p.ReasonHave, p.ReasonNeed);
                case ProdReason.WorkingBurn:
                    return GameText.Format("prod.reason.working_burn", Signal.FirmwareKinds.DisplayName(p.ReasonTarget) ?? p.ReasonTarget ?? string.Empty);
                case ProdReason.WorkingGenerator:
                    return GameText.Format("prod.reason.working_generator", Mathf.RoundToInt(HomeValleyPowerGrid.GeneratorLoad(state, p.Id) * 100f),
                        HomeValleyPowerGrid.Num(HomeValleyPowerGrid.OutputOf(state, p.Id)));
                case ProdReason.GeneratorIdle:
                    return GameText.Get("prod.reason.generator_idle");
                case ProdReason.NoFuel:
                    return GameText.Format("prod.reason.no_fuel", FluidPortHint(p, 0) ?? string.Empty);
                case ProdReason.GeneratorUnconnected:
                    return GameText.Get("prod.reason.generator_unconnected");
                case ProdReason.MissingFluid:
                    return Join(GameText.Format("eco.reason.missing_input", p.ReasonItem?.Name ?? string.Empty, RecipeBook.Amount(p.ReasonItem, p.ReasonNeed),
                        RecipeBook.Amount(p.ReasonItem, p.ReasonHave)), FluidPortHint(p, p.ReasonPort));
                case ProdReason.NoRoom:
                case ProdReason.ByproductNoRoom:
                {
                    bool by = p.Reason == ProdReason.ByproductNoRoom;
                    string head = GameText.Format(by ? "eco.reason.byproduct_no_room" : "eco.reason.no_room", p.ReasonItem?.Name ?? string.Empty,
                        RecipeBook.Amount(p.ReasonItem, p.ReasonNeed), RecipeBook.Amount(p.ReasonItem, Math.Max(0, p.ReasonHave)));
                    string hint = p.ReasonPort >= 0 ? FluidPortHint(p, p.ReasonPort) : OutPortHint(p);
                    return Join(head, hint);
                }
                default: return string.Empty;
            }
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(b) ? a : a + "\n" + b;

        /// <summary>输入口为什么没有料：没接传送带（写出要铺在哪、朝哪）/ 带上没有这种物品送来。</summary>
        public static string InPortHint(Producer p, ItemDef item, string whatOverride = null)
        {
            BeltPortService.Binding bind = FindInPortFor(p, item);
            if (bind == null)
            {
                return null;
            }
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            string what = whatOverride ?? item?.Name ?? GameText.Get("prod.panel.any_solid");
            // FG4-ECO-03：有两个输入口的建筑写明是哪个方向的口（西 / 南……，随建筑朝向转）。
            bool multi = InPortIndex(p.Building.BuildingTypeId, bind.PortKey) > 0 || InPortCount(p.Building.BuildingTypeId) > 1;
            string dir = GameText.Get(GridMath.DirTextKey(bind.Face));
            if (k == null || bind.PortId < 0 || !k.TryGetPortInfo(bind.PortId, out BeltPortInfo info) || !info.Connected)
            {
                return multi
                    ? GameText.Format("prod.reason.in_port_unconnected_at", bind.BeltCell.X, bind.BeltCell.Y, what, dir)
                    : GameText.Format("prod.reason.in_port_unconnected", bind.BeltCell.X, bind.BeltCell.Y, what);
            }
            return multi ? GameText.Format("prod.reason.in_port_empty_at", what, dir) : GameText.Format("prod.reason.in_port_empty", what);
        }

        /// <summary>这类建筑有几个 role = prod 的输入口。</summary>
        public static int InPortCount(string typeId)
        {
            int n = 0;
            foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(typeId))
            {
                if (row.Role == "prod" && row.Kind != "out")
                {
                    n++;
                }
            }
            return n;
        }

        public static string OutPortHint(Producer p)
        {
            BeltPortService.Binding bind = FindPort(p, true);
            if (bind == null)
            {
                return null;
            }
            BeltKernel k = BeltNetworkService.IsRunning ? BeltNetworkService.Kernel : null;
            if (k == null || bind.PortId < 0 || !k.TryGetPortInfo(bind.PortId, out BeltPortInfo info) || !info.Connected)
            {
                return GameText.Format("prod.reason.out_port_unconnected", bind.BeltCell.X, bind.BeltCell.Y);
            }
            return GameText.Get("prod.reason.out_port_blocked");
        }

        public static BeltPortService.Binding FindPort(Producer p, bool output)
        {
            foreach (GameConfig.fg.BuildingPort row in GridContent.PortsOf(p.Building.BuildingTypeId))
            {
                if (row.Role == "prod" && (row.Kind == "out") == output)
                {
                    return BeltPortService.Find(p.Id, row.Id);
                }
            }
            return null;
        }

        /// <summary>流体口为什么没流体 / 送不出去：没接管线（写出铺在哪）/ 网络里是别的流体 / 网络没有供给 / 出口网络没地方放。</summary>
        public static string FluidPortHint(Producer p, int port)
        {
            if (port < 0 || port >= p.Fluids.Length)
            {
                return null;
            }
            FluidRt f = p.Fluids[port];
            string name = f.Fluid?.Name ?? GameText.Get("prod.panel.fluid_any");
            PipeKernel k = PipeNetworkService.IsRunning ? PipeNetworkService.Kernel : null;
            int net = -1;
            int h = p.Rec.FluidHandles[port];
            if (k != null && h >= 0)
            {
                if (f.Def.IsOutput && k.TryGetProducer(h, out PipeProducerInfo pi))
                {
                    net = pi.Network;
                }
                else if (!f.Def.IsOutput && k.TryGetConsumer(h, out PipeConsumerInfo ci))
                {
                    net = ci.Network;
                }
            }
            if (k == null || net < 0 || !k.TryGetNetworkInfo(net, out PipeNetInfo n))
            {
                return GameText.Format("prod.reason.fluid_unconnected", name, f.PipeCell.X, f.PipeCell.Y);
            }
            if (f.Fluid != null && n.Fluid != 0 && n.Fluid != f.FluidId)
            {
                return GameText.Format(f.Def.IsOutput ? "prod.reason.fluid_out_wrong" : "prod.reason.fluid_wrong", name, PipeNetworkService.FluidName(n.Fluid));
            }
            if (f.Def.IsOutput)
            {
                return GameText.Format("prod.reason.fluid_out_full", name);
            }
            return f.Fluid == null ? GameText.Get("prod.reason.waste_idle") : GameText.Format("prod.reason.fluid_no_supply", name);
        }

        /// <summary>悬停 / 建造模式状态行：“精炼炉：● 工作中 · 正在生产 合金（矿）”。</summary>
        public static bool TryDescribe(CampaignState state, BuildingRecord b, out string text)
        {
            text = null;
            if (b == null || !TryGet(state, b.BuildingId, out Producer p))
            {
                return false;
            }
            if (p.State == ProdState.None)
            {
                StepPreview(p);
            }
            text = GameText.Format("prod.hover.line", StateText(p), ReasonText(state, p).Replace("\n", " · "));
            return true;
        }

        /// <summary>还没推进过一步的建筑（刚读档 / 刚建成）先按建筑状态给出状态，不推进任何东西。</summary>
        private static void StepPreview(Producer p)
        {
            BuildingRecord b = p.Building;
            switch (b.ConstructionState)
            {
                case BuildingConstructionState.Operational:
                    if (p.Def.Mode == ProducerMode.Recipe && p.Recipe == null)
                    {
                        Set(p, ProdState.Idle, ProdReason.NoRecipe);
                    }
                    else if (p.IsBurner && string.IsNullOrEmpty(p.Rec.BurnTarget))
                    {
                        Set(p, ProdState.Idle, ProdReason.NoBurnTarget);
                    }
                    else if (p.Def.Mode == ProducerMode.Generator)
                    {
                        Set(p, p.Rec.Fueled ? ProdState.Idle : ProdState.MissingFluid, p.Rec.Fueled ? ProdReason.GeneratorIdle : ProdReason.NoFuel,
                            p.Fluids.Length > 0 ? p.Fluids[0].Fluid : null, 0);
                    }
                    else if (!Powered(b))
                    {
                        Set(p, ProdState.NoPower, b.PowerState == BuildingPowerState.Brownout ? ProdReason.PowerBrownout : ProdReason.PowerUnconnected);
                    }
                    break;
                case BuildingConstructionState.Damaged:
                    Set(p, ProdState.Damaged, ProdReason.Damaged);
                    break;
                case BuildingConstructionState.Disabled:
                    Set(p, ProdState.Disabled, ProdReason.Disabled);
                    break;
                default:
                    Set(p, ProdState.Building, ProdReason.NotBuilt);
                    break;
            }
        }

        // ── 放置说明（建造菜单 / 放置预览，卡片“采集建筑只能放在资源点上，并说明原因”）──────────────────

        /// <summary>放置预览的附加说明：回收站脚下的废墟格数与储量；提取钻脚下的矿脉与产出。O(占地格)。</summary>
        public static void AddPlacementNotes(CampaignState state, string typeId, List<GridCell> cells, List<string> notes)
        {
            if (state == null || cells == null || notes == null || !ProducerCatalog.TryGet(typeId, out ProducerDef def))
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            if (def.Mode == ProducerMode.Recycler)
            {
                int n = 0, total = 0;
                byte ruin = GridContent.TerrainCode("ruin");
                foreach (GridCell c in cells)
                {
                    if (map.GetTerrain(c) == ruin)
                    {
                        n++;
                        total += RuinRemaining(state, c);
                    }
                }
                if (n > 0)
                {
                    notes.Add(GameText.Format("prod.preview.ruin", n, total, RuinScrapPerCell));
                }
            }
            else if (def.Mode == ProducerMode.Drill)
            {
                byte metal = GridContent.TerrainCode("ore_metal");
                byte rare = GridContent.TerrainCode("ore_rare");
                int m = 0, r = 0;
                foreach (GridCell c in cells)
                {
                    byte t = map.GetTerrain(c);
                    m += t == metal ? 1 : 0;
                    r += t == rare ? 1 : 0;
                }
                if (m + r > 0)
                {
                    bool useRare = r > m;
                    notes.Add(GameText.Format("prod.preview.vein", GameText.Get(useRare ? "grid.terrain.ore_rare.name" : "grid.terrain.ore_metal.name"),
                        useRare ? r : m, ItemCatalog.NameOf(useRare ? "rare_earth_ore" : "metal_ore")));
                }
            }
            else if (def.Mode == ProducerMode.Pump)
            {
                // 与 Refresh 同一规则：哪种流体源格多抽哪种，一样多时抽流体编号小的那种。
                int a = 0, b = 0, na = 0, nb = 0;
                byte ta = 0, tb = 0;
                foreach (GridCell c in cells)
                {
                    byte t = map.GetTerrain(c);
                    int fid = PipeNetworkService.SourceFluidOfTerrain(t);
                    if (fid <= 0)
                    {
                        continue;
                    }
                    if (a == 0 || a == fid)
                    {
                        a = fid;
                        ta = t;
                        na++;
                    }
                    else if (b == 0 || b == fid)
                    {
                        b = fid;
                        tb = t;
                        nb++;
                    }
                }
                if (na + nb > 0)
                {
                    bool useB = nb > na || (nb == na && b < a);
                    GameConfig.fg.GridTerrain gt = GridContent.TerrainByCode(useB ? tb : ta);
                    string fluidName = ItemCatalog.TryGetByFluid(useB ? b : a, out ItemDef fl) ? fl.Name : string.Empty;
                    notes.Add(GameText.Format("prod.preview.source", gt != null ? GameText.Get(gt.NameKey) : string.Empty, useB ? nb : na, fluidName,
                        Mathf.RoundToInt(def.FluidLpm).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }
    }
}
