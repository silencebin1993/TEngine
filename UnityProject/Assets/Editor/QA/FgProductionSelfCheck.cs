using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-02 采集与加工建筑的自动验收（FG04 第 3.3 节；FGR-ECO-010 / 011 本 Story 部分；FGR-ECO-071；卡片“每座建筑的通用面板”“采集建筑只能放在资源点上，并说明原因”、
    /// 负向“废墟拆完”“泵放在非流体源上”、验收 FGT-ECO-002 的对应部分；承接 DEBT-FG3GEN01-03 / 08、DEBT-FG3LOG05-02 / 03 / 11、DEBT-FG3LOG08-03）。
    /// 全部起真实系统：真实世界模拟与家园（WorldSimulation.LoadHome，世界步里跑传送带 / 管线 / 生产 / 电网）、真实 AOT 管线与传送带内核、真实存读档文件、真 UXML 面板。
    /// 场地按种子生成的地形找（B25）；需要特定资源点的链式场景在核心附近的空地上用测试捷径改几格地形（只改测试用的格子，写明）。
    /// A 数据（表与源数据逐字段、配方归属、回收产出 FGR-BAL-050、目录负向）；B 放置（三个种子：资源点限制与原因、预览储量 / 矿脉、泵不在流体源）；
    /// C 回收站拆真实废墟（逐格储量、输出堵塞原因）；D 提取钻 → 传送带 → 精炼炉产线（配方待机、选配方、账目守恒、换配方退料、输出堵塞追到堵点、上游缺料链、震动接口、矿脉被改）；
    /// E 精炼塔 + 废液池（缺流体原因、副产品无处可去、接废液池恢复、流体账目守恒）；F 调配站（接错流体、缺稀土矿、产出冷却液）；
    /// G 废墟拆完（变成空地、通知、钩子、只分解送来的物品、回收得到的废料少于投入）；H 面板（真 UXML、配方下拉、按钮、端口面板行、状态标记）与布局探针；
    /// I 真文件存读档接着跑一致；J 暂停与 0.5x～3x；K 观察 / 不观察一致；L 地图底图随拆完的废墟重画（DEBT-FG3GEN01-08）；M 拆除退回缓存与流体口；
    /// N 建造菜单真实入口（放置、施工、钩子与图鉴）；O 起始区废墟储量核对（DEBT-FG3GEN01-03）；P 每座建筑的状态都触发过（FGT-ECO-002 对应部分）；Q 性能（800 座生产建筑）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgProductionSelfCheck）。
    /// </summary>
    public static class FgProductionSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const int TestPortBase = 900000;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly HashSet<string> SeenStates = new HashSet<string>();
        private static int _testPort;

        [MenuItem("BinGames/QA/自检/FG 采集与加工建筑")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            SeenStates.Clear();
            Line("\n[采集与加工建筑] 回收站 / 提取钻 / 精炼炉 / 精炼塔 / 调配站 / 废液池、资源点、通用面板、废墟拆完、地图底图（FG4-ECO-02）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgprod-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "生产服务在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），管线 / 传送带内核在 AOT（真机 IL2CPP）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckCatalogNegative);
                Step(CheckPlacement);
                Step(CheckRecyclerOnRuins);
                Step(CheckDrillFurnaceLine);
                Step(CheckUpstreamDiagnosis);
                Step(CheckVibration);
                Step(CheckRefineryAndWaste);
                Step(CheckBlending);
                Step(CheckRuinDepletionAndMap);
                Step(CheckPanel);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckDemolish);
                Step(CheckBuildMenu);
                Step(CheckStartRuinReserve);
                Step(CheckFluidHandleIdentity);
                Step(CheckFluidHeldOnStateChange);
                Step(CheckRecyclerOutputs);
                Step(CheckFluidPump);
                Step(CheckStateCoverage);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"生产建筑自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                BeltPortPanelUIToolkit.Close();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [采集与加工建筑] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 家园工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgprod-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            _testPort = TestPortBase;
            return s;
        }

        /// <summary>每 1/4 游戏秒走一次，并记下每座生产建筑此刻的状态（P 段核对“每座建筑的各个状态都触发过”）。</summary>
        private static bool StepUntil(Func<bool> done, int maxGameSeconds)
        {
            for (int i = 0; i < maxGameSeconds * 4; i++)
            {
                SeenAll();
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 4));
            }
            SeenAll();
            return done();
        }

        private static void Seconds(float s)
        {
            int total = Mathf.RoundToInt(GameClock.StepHz * s);
            int quarter = Math.Max(1, GameClock.StepHz / 4);
            while (total > 0)
            {
                int n = Math.Min(quarter, total);
                WorldSimulation.StepMany(n);
                total -= n;
                SeenAll();
            }
        }

        private static void SeenAll()
        {
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                Seen(p);
            }
        }

        /// <summary>测试捷径：开局的发电机修好 + 核心旁一座发电机 2，保证测试用的生产建筑有电（电网本身由 FG3-LOG-06 覆盖）。</summary>
        private static void PowerUp(CampaignState s)
        {
            if (HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":fgprod_gen2") != null)
            {
                HomeValleyPowerGrid.Recompute(s);
                return;
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            GridCell? at = FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 20f);
            if (at.HasValue)
            {
                Built(s, HomeValleyLayout.BuildingTypeGenerator2, "gen2", at.Value);
            }
            HomeValleyPowerGrid.Recompute(s);
        }

        private static GridCell? FindFree(CampaignState s, string typeId, float fromCore, float toCore)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = fromCore; d <= toCore; d += 1f)
                {
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (HomeGridService.ValidatePlacement(s, typeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        /// <summary>核心附近一块 w×h 的空地（每格都能放一座 1×1 建筑、能铺传送带 / 管线），返回左下角。按种子地形找，不写死坐标（B25）。</summary>
        private static GridCell? FindArea(CampaignState s, int w, int h, float fromCore = 9f, float toCore = 24f)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = (int)fromCore; r <= (int)toCore; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var o = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        for (int y = 0; y < h && ok; y++)
                        {
                            for (int x = 0; x < w && ok; x++)
                            {
                                var c = new GridCell(o.X + x, o.Y + y);
                                ok = HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok
                                     && HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok
                                     && Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(core.X, core.Y)) <= 25f;
                            }
                        }
                        if (ok)
                        {
                            return o;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（施工流程由 FG3-LOG-02 与本段 N 的真实入口覆盖），然后让电网 / 端口 / 生产对账跟上。</summary>
        private static BuildingRecord Built(CampaignState s, string typeId, string key, GridCell pivot, int rotation = 0)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":fgprod_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rotation),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            Resync(s);
            return r;
        }

        private static void Resync(CampaignState s)
        {
            BeltPortService.Sync(s);
            ProductionService.Sync(s, force: true);
        }

        private static void SetTerrain(CampaignState s, GridCell c, string terrain) => HomeGridService.MapFor(s).SetTerrain(c, GridContent.TerrainCode(terrain));

        private static void SetFootprint(CampaignState s, string typeId, GridCell pivot, string terrain)
        {
            BuildingGrid g = GridContent.Building(typeId);
            var cells = new List<GridCell>();
            GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, 0, cells);
            foreach (GridCell c in cells)
            {
                SetTerrain(s, c, terrain);
            }
        }

        private static bool Belts(CampaignState s, GridCell from, BeltDir dir, int len)
        {
            int dx = dir == BeltDir.East ? 1 : dir == BeltDir.West ? -1 : 0;
            int dy = dir == BeltDir.North ? 1 : dir == BeltDir.South ? -1 : 0;
            bool ok = true;
            for (int i = 0; i < len; i++)
            {
                ok &= BeltNetworkService.TryPlace(s, new GridCell(from.X + dx * i, from.Y + dy * i), dir, 2).Ok;
            }
            return ok;
        }

        /// <summary>测试夹具：传送带格 <paramref name="cell"/> 末端正对的格上挂一个会自己消耗的输入端口（当作“下游的仓库”，物品一到就收走）。</summary>
        private static int TestSink(CampaignState s, GridCell cell)
        {
            int id = ++_testPort;
            BeltNetworkService.TryAddSink(s, id, cell, 4, 1);
            return id;
        }

        private static int TestSource(CampaignState s, GridCell beltCell, string itemId, int count)
        {
            int id = ++_testPort;
            ItemCatalog.TryGet(itemId, out ItemDef d);
            BeltNetworkService.TryAddSource(s, id, beltCell, d.BeltId, 1, 0);
            BeltNetworkService.Kernel.AddSourceItems(id, count);
            return id;
        }

        private static bool Pipe(CampaignState s, GridCell c, PipePieceKind kind = PipePieceKind.Pipe) => PipeNetworkService.TryPlace(s, c, kind, 0, 0).Ok;

        private static GridCell At(GridCell o, int dx, int dy) => new GridCell(o.X + dx, o.Y + dy);

        private static ProductionService.Producer P(CampaignState s, BuildingRecord b) =>
            ProductionService.TryGet(s, b.BuildingId, out ProductionService.Producer p) ? p : null;

        private static void Seen(ProductionService.Producer p)
        {
            if (p != null)
            {
                SeenStates.Add(p.Def.TypeId + ":" + p.State);
            }
        }

        private static string Reason(CampaignState s, ProductionService.Producer p) => p == null ? "（不是生产建筑）" : ProductionService.ReasonText(s, p);

        private static int OnBelt(int x, int y)
        {
            return BeltNetworkService.Kernel.TryGetCellInfo(x, y, out BeltCellInfo c) ? c.Count : 0;
        }

        private static int OnBelts(GridCell from, BeltDir dir, int len)
        {
            int dx = dir == BeltDir.East ? 1 : dir == BeltDir.West ? -1 : 0;
            int dy = dir == BeltDir.North ? 1 : dir == BeltDir.South ? -1 : 0;
            int n = 0;
            for (int i = 0; i < len; i++)
            {
                n += OnBelt(from.X + dx * i, from.Y + dy * i);
            }
            return n;
        }

        private static int PortPending(CampaignState s, BuildingRecord b, string portKey)
        {
            BeltPortService.Binding bind = BeltPortService.Find(b.BuildingId, portKey);
            return bind != null && BeltNetworkService.Kernel.TryGetPortCounts(bind.PortId, out int pending, out int buffered) ? pending + buffered : 0;
        }

        private static long ProducerStock(ProductionService.Producer p, int port)
        {
            int h = p.Rec.FluidHandles[port];
            return h >= 0 && PipeNetworkService.Kernel.TryGetProducer(h, out PipeProducerInfo i) ? i.StockMl : 0;
        }

        private static long ConsumerBuffer(ProductionService.Producer p, int port)
        {
            int h = p.Rec.FluidHandles[port];
            return h >= 0 && PipeNetworkService.Kernel.TryGetConsumer(h, out PipeConsumerInfo i) ? i.BufferMl : 0;
        }

        private static long ConsumerTotal(ProductionService.Producer p, int port)
        {
            int h = p.Rec.FluidHandles[port];
            return h >= 0 && PipeNetworkService.Kernel.TryGetConsumer(h, out PipeConsumerInfo i) ? i.TotalDeliveredMl : 0;
        }

        /// <summary>生产状态指纹：每座生产建筑的记录 + 废墟格 + 震动 + 管线内核指纹（确定性对照）。</summary>
        private static string Snap(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (ProducerRecord r in s.Economy.Producers.OrderBy(r => r.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(r.BuildingId).Append('|').Append(r.RecipeId).Append('|').Append(r.Running).Append('|').Append(r.Progress).Append('/').Append(r.Duration)
                    .Append('|').Append(r.Completed).Append('|').Append(r.RuinRecovered).Append('|').Append(r.ItemsRecycled).Append('|');
                foreach (ItemStackRecord i in r.In.Where(x => x != null && x.Amount > 0).OrderBy(x => x.ItemId))
                {
                    sb.Append("i:").Append(i.ItemId).Append('=').Append(i.Amount).Append(',');
                }
                foreach (ItemStackRecord i in r.Out.Where(x => x != null && x.Amount > 0).OrderBy(x => x.ItemId))
                {
                    sb.Append("o:").Append(i.ItemId).Append('=').Append(i.Amount).Append(',');
                }
                sb.Append(';');
            }
            foreach (RuinCellRecord c in s.Economy.RuinCells.OrderBy(c => c.X).ThenBy(c => c.Y))
            {
                sb.Append("ruin:").Append(c.X).Append(',').Append(c.Y).Append('=').Append(c.Remaining).Append(';');
            }
            sb.Append("vib:").Append(s.Economy.Vibration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("|pipe:").Append(PipeNetworkService.IsRunning ? PipeNetworkService.Kernel.Hash() : 0UL);
            sb.Append("|scrap:").Append(s.Scrap);
            return sb.ToString();
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] types = { "recycler", "extraction_drill", "refinery_furnace", "refinery_tower", "blending_station", "waste_pond", "fluid_pump" };
            (int w, int h, string cat, string terrain)[] want =
            {
                (4, 4, "gathering", "ruin"), (2, 2, "gathering", "ore_metal|ore_rare"), (3, 3, "processing", "any"), (3, 5, "processing", "any"), (3, 3, "processing", "any"), (3, 3, "processing", "any"),
                (2, 2, "gathering", "water|oil"),
            };
            bool grid = true;
            for (int i = 0; i < types.Length; i++)
            {
                grid &= GridContent.TryGetBuilding(types[i], out BuildingGrid g) && g.FootprintW == want[i].w && g.FootprintH == want[i].h && g.Category == want[i].cat
                        && g.RequiredTerrain == want[i].terrain && g.Placeable == 1 && FgContentTables.TryGetBuilding(types[i], out Building b) && b.BuildScrap > 0 && b.BuildSeconds > 0
                        && b.PowerDemand > 0 && ProducerCatalog.TryGet(types[i], out _) && BuildCatalog.TryGet(types[i], out BuildEntry e) && e.CategoryId == want[i].cat;
            }
            Expect(grid, "A1 七座建筑入表（FG04 第 3.3 节占地：回收站 4×4、提取钻 2×2、流体泵 2×2、精炼炉 3×3、精炼塔 3×5、调配站 3×3、废液池 3×3）：建造菜单“采集 / 加工”页签、耗电、造价；回收站要压废墟、提取钻要压金属或稀土矿脉、流体泵要压水源或油井");
            bool recipes = ProducerCatalog.TryGet("refinery_furnace", out ProducerDef f) && f.Recipes.Select(r => r.Id).SequenceEqual(new[] { "alloy_ore", "alloy_scrap" }) && f.FixedRecipe == null
                           && ProducerCatalog.TryGet("refinery_tower", out ProducerDef t) && t.FixedRecipe?.Id == "fuel" && t.FluidPorts.Count == 3
                           && t.FluidPorts.Count(p => p.IsOutput) == 2 && t.FluidPorts.Any(p => !p.IsOutput && p.Fluid?.Id == "crude")
                           && ProducerCatalog.TryGet("blending_station", out ProducerDef bl) && bl.FixedRecipe?.Id == "coolant" && bl.FluidPorts.Any(p => !p.IsOutput && p.Fluid?.Id == "water")
                           && ProducerCatalog.TryGet("waste_pond", out ProducerDef wp) && wp.Mode == ProducerMode.Waste && wp.FluidPorts.Count == 1 && wp.FluidPorts[0].Fluid == null
                           && ProducerCatalog.TryGet("extraction_drill", out ProducerDef dr) && dr.VibrationPerMinute > 0f
                           && ProducerCatalog.TryGet("fluid_pump", out ProducerDef fp) && fp.Mode == ProducerMode.Pump && fp.FluidLpm > 0f && fp.FluidPorts.Count == 1
                           && fp.FluidPorts[0].IsOutput && fp.FluidPorts[0].FromSource && fp.FluidPorts[0].Fluid == null && ProducerCatalog.Problems.Count == 0;
            Expect(recipes, $"A1 生产参数：精炼炉两个配方要选、精炼塔 / 调配站是固定功能、精炼塔进原油出燃油 + 酸液、废液池收任何流体、提取钻有震动、流体泵一个出口出脚下流体源的流体；表检查 0 个问题（{string.Join("；", ProducerCatalog.Problems)}）");
            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] srcPd = lines.Where(l => l.StartsWith("PD\t", StringComparison.Ordinal)).ToArray();
            string[] srcPf = lines.Where(l => l.StartsWith("PF\t", StringComparison.Ordinal)).ToArray();
            GameConfig.Tables tb = ConfigSystem.Instance.Tables;
            string[] rtPd = tb.TbProducer.DataList.Select(r => string.Join("\t", "PD", r.TypeId, r.Mode, r.Recipes, r.InBatches, r.OutBatches, Fl(r.CycleSeconds), r.CycleAmount,
                Fl(r.ItemSeconds), Fl(r.FluidLpm), Fl(r.VibrationPerMinute), r.PlaceHintKey, r.SortOrder)).ToArray();
            string[] rtPf = tb.TbBuildingFluidPort.DataList.Select(r => string.Join("\t", "PF", r.Id, r.TypeId, r.LocalX, r.LocalY, r.Dir, r.Kind, r.Fluid)).ToArray();
            Expect(code == 0 && srcPd.Length == 7 && srcPd.SequenceEqual(rtPd) && srcPf.Length == 7 && srcPf.SequenceEqual(rtPf),
                $"A1 fg.TbProducer（{srcPd.Length} 行）/ fg.TbBuildingFluidPort（{srcPf.Length} 行）与源数据 fgdata_production.py 逐字段一致" + (code == 0 ? string.Empty : "：" + Tail(output)));
            // FGR-ECO-071 / FG16 FGR-BAL-050：回收产出（固体 > 0、流体 0）；生成端 validate() 已逐条配方断言“回收得到的 < 投入的当量”。
            bool recycle = ItemCatalog.TryGet("alloy", out ItemDef alloy) && alloy.RecycleScrap == 2 && ItemCatalog.TryGet("scrap", out ItemDef scrap) && scrap.RecycleScrap == 1
                           && ItemCatalog.TryGet("water", out ItemDef water) && water.RecycleScrap == 0 && ItemCatalog.Items.Where(i => i.Form == ItemForm.Solid).All(i => i.RecycleScrap >= 1)
                           && ItemCatalog.TryGetRecipe("alloy_scrap", out RecipeDef ar) && ar.Lines.First(l => l.Role == RecipeRole.In).Amount == 4;
            (int vcode, string vout) = RunPython(Path.Combine(root, "tools", "cell_tables"), "-c \"import fgdata_eco, fgdata_production; print('ok')\"");
            Expect(recycle && vcode == 0, "A2 回收产出入物品表（FGR-ECO-071 任何固体都能分解）：合金 1 件得 2 废料（炼 1 合金要 4 废料，FG16 FGR-BAL-050 无限循环守护）、废料原样 1、流体 0；生成端逐条配方核对“回收得到 < 投入当量”通过"
                                         + (vcode == 0 ? string.Empty : "：" + Tail(vout)));
            string[] keys =
            {
                "building.recycler.name", "building.extraction_drill.name", "building.waste_pond.name", "prod.state.working", "prod.state.output_blocked", "prod.reason.no_recipe",
                "prod.reason.ruin_depleted", "grid.reason.needs_terrain_why", "prod.place.recycler", "prod.place.drill", "prod.panel.title", "codex.economy.gathering.title",
                "codex.economy.processing.body", "diag.step.prod_upstream", "prod.notify.ruin_depleted", "prod.reason.pump_no_source",
            };
            bool texts = keys.All(k => GameText.Has(k));
            GameSettings.SetLanguage(GameLanguage.En);
            texts &= keys.All(k => GameText.Has(k) && !GameText.Get(k).Contains("⟦"));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool codex = MechanicCodex.Find("codex.economy.gathering") != null && MechanicCodex.Find("codex.economy.processing") != null
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyGatheringFirstPlaced) && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyFirstByproductBlocked)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyFirstStarved) && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyRuinFirstDepleted);
            Expect(texts && codex, "A3 文本中英两套、图鉴“采集建筑”“加工建筑”两条系统说明、6 个引导钩子（第一次放采集 / 加工建筑、打开面板、缺料、副产品堵塞、废墟拆完）登记");
        }

        /// <summary>与 Python repr(float) 对齐：整数值写成“3.0”，其余按往返格式。</summary>
        private static string Fl(float v)
        {
            double d = v;
            return d == Math.Floor(d) ? d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void CheckCatalogNegative()
        {
            var rows = new List<ProducerCatalog.ProducerRow>
            {
                new ProducerCatalog.ProducerRow { TypeId = "refinery_furnace", Mode = "recipe", Recipes = "alloy_ore,part", InBatches = 2, OutBatches = 2 },
                new ProducerCatalog.ProducerRow { TypeId = "extraction_drill", Mode = "drill", Recipes = "none", OutBatches = 0, CycleSeconds = 2f, CycleAmount = 1 },
                new ProducerCatalog.ProducerRow { TypeId = "waste_pond", Mode = "lava", Recipes = "none", FluidLpm = 10f },
                new ProducerCatalog.ProducerRow { TypeId = "blending_station", Mode = "recipe", Recipes = "coolant", InBatches = 2, OutBatches = 2 },
            };
            var ports = new List<ProducerCatalog.FluidPortRow>
            {
                new ProducerCatalog.FluidPortRow { Id = "blending_station.bad", TypeId = "blending_station", LocalX = 0, LocalY = -1, Dir = "S", Kind = "out", Fluid = "any" },
            };
            ProducerCatalog.Data d = ProducerCatalog.Build(rows, ports);
            Expect(d.All.Count == 0 && d.Problems.Count == 4 && d.Problems.Any(p => p.Contains("part") && p.Contains("不是它")) && d.Problems.Any(p => p.Contains("输出缓存"))
                   && d.Problems.Any(p => p.Contains("lava")) && d.Problems.Any(p => p.Contains("any 只能用于输入")),
                $"A4 生产建筑表的负向：配方属于别的建筑、提取钻没有输出缓存、工作方式拼错、流体出口写 any——整行拒绝并写明原因（{string.Join("；", d.Problems)}）");
        }

        // ── B 放置 ───────────────────────────────────────────────────────────────

        private static void CheckPlacement()
        {
            foreach (int seed in new[] { 9401, 9402, 9403 })
            {
                CampaignState s = NewWorld(seed, scrap: 500);
                byte ruin = GridContent.TerrainCode("ruin");
                byte metal = GridContent.TerrainCode("ore_metal");
                byte rare = GridContent.TerrainCode("ore_rare");
                HomeGridMap map = HomeGridService.MapFor(s);
                GridCell core = HomeGridService.CorePivot(s);
                GridCell? onRuin = null, onOre = null, onSource = null;
                for (int r = 4; r <= 40 && (onRuin == null || onOre == null || onSource == null); r++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            {
                                continue;
                            }
                            var c = new GridCell(core.X + dx, core.Y + dy);
                            byte t = map.GetTerrain(c);
                            if (onRuin == null && t == ruin && HomeGridService.ValidatePlacement(s, "recycler", c, 0).Ok)
                            {
                                onRuin = c;
                            }
                            if (onOre == null && (t == metal || t == rare) && HomeGridService.ValidatePlacement(s, "extraction_drill", c, 0).Ok)
                            {
                                onOre = c;
                            }
                            if (onSource == null && PipeNetworkService.SourceFluidOfTerrain(t) > 0 && HomeGridService.ValidatePlacement(s, "fluid_pump", c, 0).Ok)
                            {
                                onSource = c;
                            }
                        }
                    }
                }
                GridCell? plain = FindArea(s, 4, 4);
                if (onRuin == null || onOre == null || onSource == null || plain == null)
                {
                    Fail($"B1 种子 {seed}：核心 40 格内找不到能放回收站的废墟 / 能放提取钻的矿脉 / 能放流体泵的水源或油井 / 4×4 空地（起始区保证应有）");
                    continue;
                }
                GridCell pc = At(plain.Value, 1, 1);
                GridPlacementResult rNo = HomeGridService.ValidatePlacement(s, "recycler", pc, 0);
                GridPlacementResult dNo = HomeGridService.ValidatePlacement(s, "extraction_drill", pc, 0);
                string rWhy = rNo.Reasons.Select(x => x.Describe()).FirstOrDefault(x => x.Contains("废墟")) ?? string.Empty;
                string dWhy = dNo.Reasons.Select(x => x.Describe()).FirstOrDefault(x => x.Contains("矿脉")) ?? string.Empty;
                bool refused = !rNo.Ok && rNo.Reasons.Any(x => x.Code == GridBlockReason.NeedsTerrain) && rWhy.Contains("回收站要压在废墟上")
                               && !dNo.Ok && dWhy.Contains("金属矿脉或稀土矿脉") && dWhy.Contains("从脚下的矿脉采矿");
                GridPlacementResult rOk = HomeGridService.ValidatePlacement(s, "recycler", onRuin.Value, 0);
                GridPlacementResult dOk = HomeGridService.ValidatePlacement(s, "extraction_drill", onOre.Value, 0);
                bool previews = rOk.Ok && rOk.Notes.Any(n => n.Contains("脚下废墟") && n.Contains("储量")) && dOk.Ok && dOk.Notes.Any(n => n.Contains("矿脉不会采完"));
                bool processing = HomeGridService.ValidatePlacement(s, "refinery_furnace", pc, 0).Ok && HomeGridService.ValidatePlacement(s, "waste_pond", pc, 0).Ok;
                // 负向“泵放在非流体源上”：2×2 流体泵（FG04）与一格泵（FG3-LOG-05）放在空地上都被拒，并写明原因。
                PipeOpResult pump = PipeNetworkService.TryPlace(s, plain.Value, PipePieceKind.Pump, 0, 0);
                bool pumpRefused = !pump.Ok && pump.Describe().Contains("泵要放在水源或油井上");
                GridPlacementResult fNo = HomeGridService.ValidatePlacement(s, "fluid_pump", pc, 0);
                string fWhy = fNo.Reasons.Select(x => x.Describe()).FirstOrDefault(x => x.Contains("水源")) ?? string.Empty;
                bool fluidPumpRefused = !fNo.Ok && fNo.Reasons.Any(x => x.Code == GridBlockReason.NeedsTerrain) && fWhy.Contains("水源或油井") && fWhy.Contains("流体泵要压在水源或油井上");
                GridPlacementResult fOk = HomeGridService.ValidatePlacement(s, "fluid_pump", onSource.Value, 0);
                bool fluidPreview = fOk.Ok && fOk.Notes.Any(n => n.Contains("流体源不会抽干") && n.Contains("600"));
                Expect(refused && previews && processing && pumpRefused && fluidPumpRefused && fluidPreview,
                    $"B1 [种子 {seed}] 采集建筑只能放在资源点上并说明原因：回收站放空地被拒（“{rWhy}”），提取钻放空地被拒（“{dWhy}”），流体泵放空地被拒（“{fWhy}”）；" +
                    $"放在废墟 ({onRuin.Value.X},{onRuin.Value.Y}) / 矿脉 ({onOre.Value.X},{onOre.Value.Y}) / 流体源 ({onSource.Value.X},{onSource.Value.Y}) 上合法，" +
                    $"预览写“{string.Join(" / ", rOk.Notes.Concat(dOk.Notes).Concat(fOk.Notes))}”；加工建筑空地可放；一格泵放在非流体源被拒（“{pump.Describe()}”）");
            }
        }

        // ── C 回收站拆真实废墟 ────────────────────────────────────────────────────────

        private static GridCell? FindRuinSquare(CampaignState s, int radius)
        {
            byte ruin = GridContent.TerrainCode("ruin");
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 4; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var p = new GridCell(core.X + dx, core.Y + dy);
                        bool all = true;
                        for (int y = -1; y <= 2 && all; y++)
                        {
                            for (int x = -1; x <= 2 && all; x++)
                            {
                                all = map.GetTerrain(At(p, x, y)) == ruin;
                            }
                        }
                        if (all && HomeGridService.ValidatePlacement(s, "recycler", p, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            return p;
                        }
                    }
                }
            }
            return null;
        }

        private static void CheckRecyclerOnRuins()
        {
            CampaignState s = NewWorld(9411, scrap: 300);
            GridCell? at = FindRuinSquare(s, 30);
            if (at == null)
            {
                Fail("C1 核心 30 格内找不到 4×4 整块废墟（起始区保证 g2.ruin 应有）");
                return;
            }
            PowerUp(s);
            BuildingRecord rec = Built(s, "recycler", "rc", at.Value);
            ProductionService.Producer p = P(s, rec);
            int per = ProductionService.RuinScrapPerCell;
            int before = ProductionService.RuinLeft(s, p, out int cellsBefore);
            Seconds(30f);
            Seen(p);
            int after = ProductionService.RuinLeft(s, p, out int cellsAfter);
            int outBuf = ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
            int pending = PortPending(s, rec, "recycler.out0");
            GridCell first = p.RuinCells[0];
            RuinCellRecord partial = s.Economy.RuinCells.FirstOrDefault(c => c.X == first.X && c.Y == first.Y);
            string why = Reason(s, p);
            bool ok = cellsBefore == 16 && before == 16 * per && p.Rec.RuinRecovered == outBuf + pending && outBuf == ProductionService.OutCapacity(p, null)
                      && before - after == p.Rec.RuinRecovered + (p.Rec.Running && p.Rec.PendingRuin ? p.Rec.PendingAmount : 0)
                      && partial != null && partial.Remaining == per - (before - after)
                      && p.State == ProdState.OutputBlocked && why.Contains("输出口没接传送带") && rec.PowerState == BuildingPowerState.Powered;
            Expect(ok, $"C1 回收站压在真实的 4×4 废墟上（{cellsBefore} 格、储量 {before} = 16 × {per}）：拆 30 游戏秒，拆出的 {p.Rec.RuinRecovered} 废料 = 输出缓存 {outBuf} + 输出口待推 {pending}，" +
                       $"废墟储量逐格扣（第一格剩 {partial?.Remaining}），没接传送带时“{ProductionService.StateText(p)}”：“{why}”");
        }

        // ── D 提取钻 → 传送带 → 精炼炉 ─────────────────────────────────────────────────

        private sealed class Line1
        {
            public BuildingRecord Drill;
            public BuildingRecord Furnace;
            public GridCell InBelt;
            public GridCell OutBelt;
            public GridCell Origin;
        }

        /// <summary>
        /// 测试场景（核心附近找一块 13×3 空地，把提取钻脚下 2×2 改成金属矿脉——测试捷径）：提取钻 → 4 格传送带 → 精炼炉 → 4 格传送带（末端不接 = 死路）。
        /// </summary>
        private static Line1 LayLine(CampaignState s, string key, bool power = true)
        {
            GridCell? o = FindArea(s, 14, 3);
            if (o == null)
            {
                return null;
            }
            GridCell drill = At(o.Value, 0, 1);
            SetFootprint(s, "extraction_drill", drill, "ore_metal");
            if (power)
            {
                PowerUp(s);
            }
            var l = new Line1 { Origin = o.Value };
            l.Drill = Built(s, "extraction_drill", key + "_drill", drill);
            GridCell furnace = At(drill, 7, 0);
            l.Furnace = Built(s, "refinery_furnace", key + "_furnace", furnace);
            l.InBelt = At(drill, 2, 0);
            l.OutBelt = At(furnace, 2, 0);
            bool belts = Belts(s, l.InBelt, BeltDir.East, 4) && Belts(s, l.OutBelt, BeltDir.East, 4);
            Resync(s);
            return belts ? l : null;
        }

        private static void CheckDrillFurnaceLine()
        {
            CampaignState s = NewWorld(9421, scrap: 300);
            Line1 l = LayLine(s, "d");
            if (l == null)
            {
                Fail("D 找不到 14×3 的空地或铺不了传送带");
                return;
            }
            ProductionService.Producer drill = P(s, l.Drill);
            ProductionService.Producer furnace = P(s, l.Furnace);
            // 1) 精炼炉没选配方：待机，输入口什么都不收，带停在末端（原因写“不收”），提取钻的产出堵在带上。
            StepUntil(() => drill.State == ProdState.OutputBlocked, 120);
            Seen(drill);
            Seen(furnace);
            BeltPortService.Binding inBind = BeltPortService.Find(l.Furnace.BuildingId, "refinery_furnace.in0");
            bool accNone = inBind != null && BeltNetworkService.Kernel.TryGetPortInfo(inBind.PortId, out BeltPortInfo pi) && pi.Accept == BeltConst.AcceptNone;
            BeltNetworkService.Kernel.TryGetCellInfo(At(l.InBelt, 3, 0).X, l.InBelt.Y, out BeltCellInfo lastCell);
            string idleWhy = Reason(s, furnace);
            string drillWhy = Reason(s, drill);
            bool idle = furnace.State == ProdState.Idle && idleWhy.Contains("没选配方") && accNone && lastCell.Block == BeltBlock.SinkRejects
                        && drill.State == ProdState.OutputBlocked && drillWhy.Contains("推不动") && HomeValleyController.StateIconFor(l.Furnace) == ContentIcons.StateIdle
                        && HomeValleyController.StateIconFor(l.Drill) == ContentIcons.StateBlocked;
            Expect(idle, $"D1 精炼炉没选配方 = “{ProductionService.StateText(furnace)}”（“{idleWhy}”），输入口不收（带末端堵塞原因 {lastCell.Block}）；提取钻产出堵在带上 = “{ProductionService.StateText(drill)}”（“{drillWhy}”）；" +
                          "头顶标记：待机六边形 / 堵塞八边形（不只靠颜色）");
            // 2) 选配方“合金（矿）”：输入口改收金属矿，开工。
            bool set = ProductionService.TrySetRecipe(s, l.Furnace.BuildingId, "alloy_ore", out string msg);
            bool accOre = BeltNetworkService.Kernel.TryGetPortInfo(inBind.PortId, out BeltPortInfo pi2) && ItemCatalog.TryGet("metal_ore", out ItemDef ore) && pi2.Accept == ore.BeltId;
            Seconds(60f);
            Seen(furnace);
            Seen(drill);
            // 账目守恒（FGT-ECO-001 / 002）：提取钻出的矿 = 精炼炉吃掉的 + 在途（带上、端口、缓存）；精炼炉出的合金 = 输出缓存 + 输出口待推 + 输出带上。
            int drillMade = (int)drill.Rec.Completed;
            int eaten = 2 * (int)furnace.Rec.Completed + (furnace.Rec.Running ? 2 : 0);
            int transitOre = OnBelts(l.InBelt, BeltDir.East, 4) + ProductionService.Count(drill.Rec.Out, "metal_ore") + PortPending(s, l.Drill, "extraction_drill.out0")
                             + ProductionService.Count(furnace.Rec.In, "metal_ore") + PortPending(s, l.Furnace, "refinery_furnace.in0");
            int alloyMade = (int)furnace.Rec.Completed;
            int alloyHere = ProductionService.Count(furnace.Rec.Out, "alloy") + PortPending(s, l.Furnace, "refinery_furnace.out0") + OnBelts(l.OutBelt, BeltDir.East, 4);
            Expect(set && msg.Contains("合金（矿）") && accOre && furnace.Rec.Completed >= 10 && drillMade == eaten + transitOre && alloyMade == alloyHere,
                $"D2 选配方“合金（矿）”（“{msg}”）后输入口只收金属矿，60 游戏秒做了 {furnace.Rec.Completed} 份合金；账目守恒：提取钻出矿 {drillMade} = 精炼炉吃掉 {eaten} + 在途 {transitOre}；" +
                $"合金 {alloyMade} = 缓存 / 待推 / 输出带上 {alloyHere}");
            // 3) 输出带是死路：带满后精炼炉“输出堵塞”，“为什么不工作”顺着输出带追到末端。
            bool blocked = StepUntil(() => furnace.State == ProdState.OutputBlocked, 120);
            Seen(furnace);
            DiagReport rep = RootCauseDiagnosis.DiagnoseBuilding(s, l.Furnace);
            DiagChain outChain = rep?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Output);
            string blockedWhy = Reason(s, furnace);
            Expect(blocked && blockedWhy.Contains("推不动") && outChain != null && outChain.Steps[0].Code == DiagCode.ProdBlocked && outChain.Steps.Any(x => x.Code == DiagCode.BeltTerminal || x.Code == DiagCode.BeltDownstream),
                $"D3 输出带是死路：带满后精炼炉“{ProductionService.StateText(furnace)}”（“{blockedWhy}”）；“为什么不工作”：{(outChain == null ? "（没有）" : RootCauseDiagnosis.ChainText(outChain))}");
            // 4) 换配方：输入缓存里的金属矿退回仓库，输入口改收废料。测试捷径：把开局受损的仓库修好（有地方放金属矿，退回的直接入库，不经地面搬运）。
            foreach (BuildingRecord wh in s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse))
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
            }
            int oreStock = HomeInventory.Stock(s, "metal_ore");
            int oreIn = ProductionService.Count(furnace.Rec.In, "metal_ore") + PortPending(s, l.Furnace, "refinery_furnace.in0") + (furnace.Rec.Running ? 2 : 0);
            bool switched = ProductionService.TrySetRecipe(s, l.Furnace.BuildingId, "alloy_scrap", out string msg2);
            int oreGround = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == "metal_ore").Sum(g => g.Amount);
            bool accScrap = BeltNetworkService.Kernel.TryGetPortInfo(inBind.PortId, out BeltPortInfo pi3) && pi3.Accept == BeltItems.ScrapId;
            Expect(switched && oreIn > 0 && HomeInventory.Stock(s, "metal_ore") + oreGround - oreStock == oreIn && ProductionService.Count(furnace.Rec.In, "metal_ore") == 0 && accScrap
                   && msg2.Contains("退回仓库") && !furnace.Rec.Running,
                $"D4 换配方为“合金（废料）”：正在做的那份作废、已扣的矿退回，输入缓存与输入口里的 {oreIn} 份金属矿退回仓库（仓库 / 地上 +{HomeInventory.Stock(s, "metal_ore") + oreGround - oreStock}），输入口改收废料（“{msg2.Replace("\n", " ")}”）");
            // 5) 端口面板与建造模式状态行写明收什么 / 推什么 / 状态。
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, l.Furnace, views);
            string desc = HomeValleyBuildMode.DescribeBuilding(s, l.Furnace);
            Expect(views.Any(v => !v.IsOutput && v.Prod && v.AcceptLine.Contains("废料") && v.AcceptLine.Contains("合金（废料）")) && views.Any(v => v.IsOutput && v.AcceptLine.Contains("推：合金"))
                   && desc.Contains("精炼炉") && (desc.Contains("缺料") || desc.Contains("输出堵塞") || desc.Contains("工作中")),
                $"D5 端口面板写“{string.Join(" / ", views.Select(v => v.AcceptLine))}”；建造模式点建筑的状态行：“{desc.Replace("\n", " | ")}”");
            // 6) 提取钻脚下的矿脉被改掉（例如被净化 / 夹具改地形）：不在资源点上，写明原因。
            BuildingGrid g = GridContent.Building("extraction_drill");
            var cells = new List<GridCell>();
            GridMath.FootprintCells(new GridCell(l.Drill.GridX, l.Drill.GridY), g.FootprintW, g.FootprintH, 0, cells);
            foreach (GridCell c in cells)
            {
                SetTerrain(s, c, "buildable");
            }
            ProductionService.Sync(s, force: true);
            Seconds(2f);
            Seen(drill);
            string noVein = Reason(s, drill);
            Expect(drill.State == ProdState.NoResource && noVein.Contains("脚下没有矿脉") && HomeValleyController.StateIconFor(l.Drill) == ContentIcons.StateStarved,
                $"D6 提取钻脚下的矿脉没了：“{ProductionService.StateText(drill)}”（“{noVein}”），头顶挂缺料标记");
        }

        // ── D7 上游缺料链（DEBT-FG3LOG08-03）─────────────────────────────────────────────

        private static void CheckUpstreamDiagnosis()
        {
            CampaignState s = NewWorld(9422, scrap: 300);
            Line1 l = LayLine(s, "u");
            if (l == null)
            {
                Fail("D7 找不到空地");
                return;
            }
            HomeValleyPowerGrid.TryToggleShutdown(s, l.Drill.BuildingId);
            ProductionService.TrySetRecipe(s, l.Furnace.BuildingId, "alloy_ore", out _);
            Resync(s);
            Seconds(8f);
            ProductionService.Producer furnace = P(s, l.Furnace);
            ProductionService.Producer drill = P(s, l.Drill);
            Seen(furnace);
            Seen(drill);
            DiagReport rep = RootCauseDiagnosis.DiagnoseBuilding(s, l.Furnace);
            DiagChain chain = rep?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Input);
            bool ok = furnace.State == ProdState.MissingInput && drill.State == ProdState.Disabled && chain != null && chain.Steps.Count >= 2
                      && chain.Steps[0].Code == DiagCode.ProdStarved && chain.Root.Code == DiagCode.ProdUpstream && chain.Root.TargetId == l.Drill.BuildingId
                      && chain.Root.Text.Contains("关停") && Reason(s, furnace).Contains("缺少金属矿") && Reason(s, furnace).Contains("没有金属矿送来")
                      && HomeValleyController.StateIconFor(l.Furnace) == ContentIcons.StateStarved;
            Expect(ok, $"D7 跨生产建筑的缺料链：精炼炉“{ProductionService.StateText(furnace)}”（“{Reason(s, furnace).Replace("\n", " · ")}”）→ “为什么不工作”顺着输入带找到上游提取钻（已关停）：" +
                       (chain == null ? "（没有链）" : RootCauseDiagnosis.ChainText(chain)));
            // 输入口没接传送带：原因写明在哪里铺、朝哪。
            CampaignState s2 = NewWorld(9423, scrap: 300);
            PowerUp(s2);
            GridCell? at = FindFree(s2, "refinery_furnace", 9f, 22f);
            BuildingRecord lone = at.HasValue ? Built(s2, "refinery_furnace", "lone", at.Value) : null;
            if (lone == null)
            {
                Fail("D7 放不下单独的精炼炉");
                return;
            }
            ProductionService.TrySetRecipe(s2, lone.BuildingId, "alloy_scrap", out _);
            Seconds(2f);
            ProductionService.Producer pl = P(s2, lone);
            string w = Reason(s2, pl);
            Expect(pl.State == ProdState.MissingInput && w.Contains("缺少废料") && w.Contains("西侧输入口没接传送带") && w.Contains($"（{lone.GridX - 2}, {lone.GridY}）"),
                $"D7 输入口没接传送带：“{w.Replace("\n", " · ")}”");
        }

        // ── D8 震动接口（FG10 FGR-EVT-010）──────────────────────────────────────────────

        private static void CheckVibration()
        {
            CampaignState s = NewWorld(9424, scrap: 300);
            GridCell? o = FindArea(s, 12, 3);
            if (o == null)
            {
                Fail("D8 找不到空地");
                return;
            }
            PowerUp(s);
            var drills = new List<BuildingRecord>();
            for (int i = 0; i < 4; i++)
            {
                GridCell c = At(o.Value, i * 3, 1);
                SetFootprint(s, "extraction_drill", c, "ore_rare");
                drills.Add(Built(s, "extraction_drill", "v" + i, c));
            }
            Seconds(18f);
            double v18 = ProductionService.Vibration(s);
            var sources = new List<ProductionService.Producer>();
            ProductionService.CollectVibrationSources(sources, 3);
            bool rising = Math.Abs(v18 - (4 * 1.0 - 2.0) * 18.0 / 60.0) < 0.02 && sources.Count == 3 && P(s, drills[0]).VeinOre?.Id == "rare_earth_ore";
            // 输出缓存满了就停工（不再震），之后按每分钟 2 衰减到 0。
            Seconds(60f);
            double v78 = ProductionService.Vibration(s);
            ProductionService.CollectVibrationSources(sources, 3);
            ProductionService.Producer d0 = P(s, drills[0]);
            Seen(d0);
            Expect(rising && v78 == 0.0 && sources.Count == 0 && d0.State == ProdState.OutputBlocked,
                $"D8 震动接口：4 座提取钻工作 18 游戏秒，家园震动 = (4 × 1 − 2) × 0.3 = {v18:0.###}（FG10 初值：每座每分钟 +1、每分钟衰减 2），列出主要来源 {sources.Count}（上限 3）；" +
                $"稀土矿脉上采稀土矿；输出满后停工，再 60 秒震动衰减到 {v78}");
        }

        // ── E 精炼塔 + 废液池 ─────────────────────────────────────────────────────────

        private sealed class Tower
        {
            public BuildingRecord Building;
            public GridCell Pivot;
        }

        private static Tower LayTower(CampaignState s, string key)
        {
            GridCell? o = FindArea(s, 9, 12);
            if (o == null)
            {
                return null;
            }
            PowerUp(s);
            GridCell t = At(o.Value, 1, 6);
            return new Tower { Building = Built(s, "refinery_tower", key, t), Pivot = t };
        }

        private static void CheckRefineryAndWaste()
        {
            CampaignState s = NewWorld(9431, scrap: 300);
            Tower tw = LayTower(s, "tw");
            if (tw == null)
            {
                Fail("E 找不到 9×12 的空地");
                return;
            }
            GridCell t = tw.Pivot;
            ProductionService.Producer p = P(s, tw.Building);
            int crudeIn = ProductionService.FluidPortIndex(p, ItemCatalog.TryGet("crude", out ItemDef crude) ? crude : null, false);
            int fuelOut = ProductionService.FluidPortIndex(p, ItemCatalog.TryGet("fuel", out ItemDef fuel) ? fuel : null, true);
            int acidOut = ProductionService.FluidPortIndex(p, ItemCatalog.TryGet("acid", out ItemDef acid) ? acid : null, true);
            Seconds(2f);
            Seen(p);
            string noPipe = Reason(s, p);
            bool unconnected = p.State == ProdState.MissingFluid && noPipe.Contains("原油口没接管线") && noPipe.Contains($"（{t.X}, {t.Y - 3}）");
            // 油井（测试捷径：把一格改成油井）→ 泵 → 两格管线 → 精炼塔南口；燃油从北口出 → 两格管线 → 储罐；酸液东口先不接。
            GridCell well = At(t, 0, -5);
            SetTerrain(s, well, "oil");
            bool laid = Pipe(s, well, PipePieceKind.Pump) && Pipe(s, At(t, 0, -4)) && Pipe(s, At(t, 0, -3)) && Pipe(s, At(t, 0, 3)) && Pipe(s, At(t, 0, 4)) && Pipe(s, At(t, 0, 5), PipePieceKind.Tank);
            bool byproductBlocked = StepUntil(() => p.State == ProdState.OutputBlocked, 120);
            Seen(p);
            string byWhy = Reason(s, p);
            Expect(unconnected && laid && byproductBlocked && p.Reason == ProdReason.ByproductNoRoom && byWhy.Contains("副产品酸液无处可去") && byWhy.Contains("酸液口没接管线")
                   && p.Rec.Completed >= 2 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyFirstByproductBlocked),
                $"E1 精炼塔：没接管线时“{noPipe.Replace("\n", " · ")}”；接上油井泵后开工，做了 {p.Rec.Completed} 份后酸液出口满了 = “{ProductionService.StateText(p)}”（“{byWhy.Replace("\n", " · ")}”）；副产品堵塞的引导钩子埋了");
            // 接上废液池：酸液有去处，精炼塔恢复，废液池在销毁酸液。
            bool pondPipes = Pipe(s, At(t, 2, 0)) && Pipe(s, At(t, 3, 0));
            BuildingRecord pond = Built(s, "waste_pond", "pond", At(t, 5, 0));
            ProductionService.Producer pp = P(s, pond);
            long completedBefore = p.Rec.Completed;
            StepUntil(() => p.State == ProdState.Working, 40);
            Seconds(45f);
            Seen(p);
            Seen(pp);
            string pondWhy = Reason(s, pp);
            bool resumed = pondPipes && p.Rec.Completed > completedBefore && pp.State == ProdState.Working && pondWhy.Contains("正在销毁酸液") && ConsumerTotal(pp, 0) > 0;
            // 流体账目守恒（毫升，精确）：燃油 = 60 升 × 完成数 = 储罐 + 出口存量；酸液 = 20 升 × 完成数 = 出口存量 + 废液池销毁量；原油 = 100 升 × 开工数 = 泵抽出 − 进口缓存。
            long c = p.Rec.Completed;
            PipeNetworkService.Kernel.TryGetCellInfo(At(t, 0, 5).X, At(t, 0, 5).Y, out PipeCellInfo tank);
            long fuelMade = 60000L * c;
            long fuelHere = tank.TankStockMl + ProducerStock(p, fuelOut);
            long acidMade = 20000L * c;
            long acidHere = ProducerStock(p, acidOut) + ConsumerTotal(pp, 0);
            long started = c + (p.Rec.Running ? 1 : 0);
            PipeNetworkService.Kernel.TryGetCellInfo(well.X, well.Y, out PipeCellInfo pumpCell);
            long crudeUsed = 100000L * started;
            long crudeHere = pumpCell.PumpTotalMl - ConsumerBuffer(p, crudeIn);
            Expect(resumed && fuelMade == fuelHere && acidMade == acidHere && crudeUsed == crudeHere,
                $"E2 接上废液池后精炼塔恢复（又做了 {c - completedBefore} 份），废液池“{ProductionService.StateText(pp)}”（“{pondWhy}”）；流体账目逐毫升守恒：" +
                $"燃油 {fuelMade} = 储罐 + 出口 {fuelHere}；酸液 {acidMade} = 出口 + 销毁 {acidHere}；原油 {crudeUsed} = 泵抽出 − 进口缓存 {crudeHere}");
            // 废液池没接管线：写原因。
            CampaignState s2 = NewWorld(9432, scrap: 300);
            PowerUp(s2);
            GridCell? at = FindFree(s2, "waste_pond", 9f, 22f);
            BuildingRecord lone = at.HasValue ? Built(s2, "waste_pond", "lonepond", at.Value) : null;
            Seconds(2f);
            ProductionService.Producer lp = lone != null ? P(s2, lone) : null;
            Seen(lp);
            Expect(lp != null && lp.State == ProdState.MissingFluid && Reason(s2, lp).Contains("流体口没接管线"), $"E3 废液池没接管线：“{Reason(s2, lp)}”");
        }

        // ── F 调配站 ──────────────────────────────────────────────────────────────

        private static void CheckBlending()
        {
            CampaignState s = NewWorld(9441, scrap: 300);
            GridCell? o = FindArea(s, 12, 9);
            if (o == null)
            {
                Fail("F 找不到 12×9 的空地");
                return;
            }
            PowerUp(s);
            // 调配站枢轴 b：水从南口（b.x, b.y−2）进；稀土矿从西口（b.x−2, b.y）进；冷却液从东口（b.x+2, b.y）出。
            GridCell b = At(o.Value, 7, 4);
            BuildingRecord st = Built(s, "blending_station", "bl", b);
            ProductionService.Producer p = P(s, st);
            // 先接错流体：南口的管线接到一台抽原油的泵上。
            GridCell src = At(b, 0, -4);
            SetTerrain(s, src, "oil");
            bool wrongLaid = Pipe(s, src, PipePieceKind.Pump) && Pipe(s, At(b, 0, -3)) && Pipe(s, At(b, 0, -2));
            Seconds(3f);
            Seen(p);
            string wrong = Reason(s, p);
            bool wrongOk = p.State == ProdState.MissingFluid && wrong.Contains("水口接到的网络里是原油") && wrong.Contains("这个口只收水");
            // 改成水：拆泵、冲洗网络、地形改水源、重新放泵。
            PipeNetworkService.TryRemove(s, src, out _);
            PipeNetworkService.TryFlush(s, At(b, 0, -3), out _, out _);
            SetTerrain(s, src, "water");
            bool pumpOk = Pipe(s, src, PipePieceKind.Pump);
            // 稀土矿：没送来时缺料。
            Seconds(25f);
            Seen(p);
            string noRare = Reason(s, p);
            bool missingRare = p.State == ProdState.MissingInput && noRare.Contains("缺少稀土矿");
            // 稀土矿从一座提取钻经 4 格传送带送进西口；冷却液从东口出 → 两格管线 → 储罐。
            GridCell drill = At(o.Value, 0, 4);
            SetFootprint(s, "extraction_drill", drill, "ore_rare");
            Built(s, "extraction_drill", "bl_drill", drill);
            bool belts = Belts(s, At(drill, 2, 0), BeltDir.East, 4);
            bool coolantPipes = Pipe(s, At(b, 2, 0)) && Pipe(s, At(b, 3, 0)) && Pipe(s, At(b, 4, 0), PipePieceKind.Tank);
            Resync(s);
            Seconds(60f);
            Seen(p);
            PipeNetworkService.Kernel.TryGetCellInfo(At(b, 4, 0).X, b.Y, out PipeCellInfo tank);
            ItemCatalog.TryGet("coolant", out ItemDef coolant);
            int coolOut = ProductionService.FluidPortIndex(p, coolant, true);
            long made = 100000L * p.Rec.Completed;
            long here = tank.TankStockMl + ProducerStock(p, coolOut);
            Expect(wrongLaid && wrongOk && pumpOk && missingRare && belts && coolantPipes && p.Rec.Completed >= 3 && made == here && tank.Fluid == coolant.FluidId,
                $"F1 调配站：南口接到原油网络 = “{wrong}”；改成水后缺稀土矿 = “{noRare.Replace("\n", " · ")}”；接上稀土矿提取钻后 60 秒调了 {p.Rec.Completed} 份冷却液，储罐 + 出口 = {here} 毫升 = 100 升 × 完成数");
        }

        // ── G 废墟拆完 + L 地图底图 ───────────────────────────────────────────────────

        private static void CheckRuinDepletionAndMap()
        {
            CampaignState s = NewWorld(9451, scrap: 300);
            GridCell? o = FindArea(s, 10, 4);
            if (o == null)
            {
                Fail("G 找不到 10×4 的空地");
                return;
            }
            PowerUp(s);
            // 回收站枢轴 r（占地 r−1～r+2）；只把右上角一格改成废墟（测试捷径），储量 = 一格。
            GridCell r = At(o.Value, 4, 1);
            GridCell ruinCell = At(r, 2, 2);
            SetTerrain(s, ruinCell, "ruin");
            BuildingRecord rec = Built(s, "recycler", "dep", r);
            ProductionService.Producer p = P(s, rec);
            // 输出口东侧 2 格传送带 → 夹具“下游仓库”（收到就收走）。
            GridCell outBelt = At(r, 3, 0);
            bool laid = Belts(s, outBelt, BeltDir.East, 2);
            int sink = TestSink(s, At(outBelt, 2, 0));
            // 地图底图：拆完之前，这格画的是废墟色。
            var tex = new WorldMapTexture(128);
            var view = new WorldMapView { CenterX = ruinCell.X, CenterY = ruinCell.Y, HalfWidth = 6, CanvasWidth = 128, CanvasHeight = 128 };
            tex.CompleteNow(s, view);
            Color32 beforePx = PixelAt(tex, view, ruinCell);
            int per = ProductionService.RuinScrapPerCell;
            bool depleted = StepUntil(() => p.Rec.RuinRecovered >= per && !p.Rec.Running, 3 * per);
            Seconds(1f);
            Seen(p);
            HomeGridMap map = HomeGridService.MapFor(s);
            bool terrain = map.GetTerrain(ruinCell) == GridContent.TerrainCode("buildable") && s.Economy.RuinCells.All(c => !(c.X == ruinCell.X && c.Y == ruinCell.Y));
            string why = Reason(s, p);
            bool notified = NotificationCenter.History.Any(e => e.Members.Any(m => m.DetailText.Contains("脚下的废墟已拆完"))) || (FeedbackCaption()?.Contains("脚下的废墟已拆完") ?? false);
            bool stateOk = p.State == ProdState.MissingInput && p.Reason == ProdReason.RuinDepleted && why.Contains("脚下的废墟已经拆完") && p.Rec.RuinDepletedNotified
                           && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyRuinFirstDepleted);
            long delivered1 = Downstream(s, rec, p, sink, outBelt);
            Expect(laid && depleted && terrain && stateOk && notified && p.Rec.RuinRecovered == per && delivered1 == per,
                $"G1 负向“废墟拆完”：脚下一格废墟（储量 {per}）拆完后变成可建空地（区块差异），拆出的 {p.Rec.RuinRecovered} 废料全部送到下游；回收站“{ProductionService.StateText(p)}”（“{why.Replace("\n", " · ")}”），" +
                $"通知一次、引导钩子埋了");
            // L：地图底图随区块差异重画（DEBT-FG3GEN01-08）。这格的生成基线本来就是空地（废墟是夹具改的），拆完后与基线相同、不再需要覆盖。
            tex.CompleteNow(s, view);
            Color32 afterPx = PixelAt(tex, view, ruinCell);
            Color expect = ColorOfTerrain("buildable", map.GetPollution(ruinCell));
            Color ruinColor = ColorOfTerrain("ruin", map.GetPollution(ruinCell));
            // 再拿一格真实生成的废墟（基线就是废墟），经同一个写入口改成空地：覆盖表里有它，底图那一格从废墟色变成空地色。
            GridCell? genRuin = null;
            GridCell core = HomeGridService.CorePivot(s);
            byte ruinCode = GridContent.TerrainCode("ruin");
            for (int rr = 4; rr <= 30 && genRuin == null; rr++)
            {
                for (int dy = -rr; dy <= rr && genRuin == null; dy++)
                {
                    for (int dx = -rr; dx <= rr && genRuin == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == rr && map.GetTerrain(At(core, dx, dy)) == ruinCode && map.GetPollution(At(core, dx, dy)) == 0)
                        {
                            genRuin = At(core, dx, dy);
                        }
                    }
                }
            }
            bool genOk = false;
            string genText = "（找不到生成的废墟格）";
            if (genRuin.HasValue)
            {
                var gView = new WorldMapView { CenterX = genRuin.Value.X, CenterY = genRuin.Value.Y, HalfWidth = 6, CanvasWidth = 128, CanvasHeight = 128 };
                tex.CompleteNow(s, gView);
                Color32 g0 = PixelAt(tex, gView, genRuin.Value);
                SetTerrain(s, genRuin.Value, "buildable");
                var overrides = new List<Unity.Mathematics.int4>();
                map.CollectCellOverrides(overrides);
                tex.CompleteNow(s, gView);
                Color32 g1 = PixelAt(tex, gView, genRuin.Value);
                genOk = overrides.Any(v => v.x == genRuin.Value.X && v.y == genRuin.Value.Y && v.z == GridContent.TerrainCode("buildable")) && tex.LastOverrideCount >= 1
                        && Near(g0, ColorOfTerrain("ruin", 0)) && Near(g1, ColorOfTerrain("buildable", 0));
                genText = $"生成的废墟 ({genRuin.Value.X},{genRuin.Value.Y}) 改成空地：覆盖表 {overrides.Count} 格，底图 {g0} → {g1}";
            }
            bool mapOk = Near(beforePx, ruinColor) && Near(afterPx, expect) && genOk;
            Expect(mapOk, $"L1 战略地图 / 小地图底图覆盖玩家改过的格子（DEBT-FG3GEN01-08）：拆完前这格画废墟色 {beforePx}，拆完后画空地色 {afterPx}；{genText}");
            tex.Dispose();
            // 废墟拆完后仍然有用：传送带送来的合金分解成废料（每件 2，少于炼一份合金的 4 废料：FG16 FGR-BAL-050）。
            GridCell inBelt = At(r, -2, 0);
            bool inLaid = Belts(s, At(inBelt, -1, 0), BeltDir.East, 2);
            Resync(s);
            TestSource(s, At(inBelt, -1, 0), "alloy", 5);
            Seconds(15f);
            Seen(p);
            long scrapOut = Downstream(s, rec, p, sink, outBelt) - delivered1;
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            Expect(inLaid && p.Rec.ItemsRecycled == 5 && scrapOut == 5 * alloy.RecycleScrap && alloy.RecycleScrap < 4,
                $"G2 废墟拆完后照常分解送来的固体（FGR-ECO-071）：5 件合金 → {scrapOut} 废料（每件 {alloy.RecycleScrap}，少于炼一份合金的 4 废料，不会无限循环）");
        }

        /// <summary>回收站产出的废料到了哪里：夹具仓库收下的 + 夹具缓存 + 输出带上 + 输出口待推 + 输出缓存。</summary>
        private static long Downstream(CampaignState s, BuildingRecord rec, ProductionService.Producer p, int sink, GridCell outBelt)
        {
            BeltNetworkService.Kernel.TryGetPortInfo(sink, out BeltPortInfo si);
            return si.Consumed + si.Buffered + OnBelts(outBelt, BeltDir.East, 2) + PortPending(s, rec, "recycler.out0") + ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
        }

        private static string FeedbackCaption() => GameLogic.Campaign.Feedback.FeedbackCues.LastCaptionText;

        private static Color32 PixelAt(WorldMapTexture tex, WorldMapView view, GridCell c)
        {
            Texture2D t = tex.Texture;
            WorldMapView v = tex.PaintedView;
            double cpp = 2.0 * v.HalfWidth / t.width;
            int px = (int)Math.Floor((c.X - v.MinX) / cpp);
            double originY = v.CenterY - cpp * t.height * 0.5;
            int py = (int)Math.Floor((c.Y - originY) / cpp);
            return t.GetPixel(Mathf.Clamp(px, 0, t.width - 1), Mathf.Clamp(py, 0, t.height - 1));
        }

        private static Color ColorOfTerrain(string id, byte pollution)
        {
            GridTerrain t = GridContent.Terrains.First(x => x.Id == id);
            ColorUtility.TryParseHtmlString(t.Color, out Color c);
            Color32 c32 = c;
            if (pollution > 0)
            {
                int k = pollution >= GridContent.TuningInt("grid.pollution_block_level") ? 120 : 60;
                c32 = new Color32((byte)(c32.r + ((150 - c32.r) * k >> 8)), (byte)(c32.g + ((40 - c32.g) * k >> 8)), (byte)(c32.b + ((150 - c32.b) * k >> 8)), 255);
            }
            return c32;
        }

        private static bool Near(Color32 a, Color b)
        {
            Color32 c = b;
            return Math.Abs(a.r - c.r) <= 6 && Math.Abs(a.g - c.g) <= 6 && Math.Abs(a.b - c.b) <= 6;
        }

        // ── H 面板 ─────────────────────────────────────────────────────────────────

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9461, scrap: 300);
            Line1 l = LayLine(s, "pn");
            Tower tw = LayTower(s, "pt");
            if (l == null || tw == null)
            {
                Fail("H 场景放不下");
                return;
            }
            Seconds(6f);
            VisualElement root = MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(l.Furnace.BuildingId);
                panel.Refresh();
                bool idle = panel.PanelVisible && panel.TitleText.Contains("精炼炉") && panel.StateText.Contains("○ 待机") && panel.ReasonText.Contains("没选配方") && panel.RecipeDropdownVisible
                            && panel.RecipeField.choices.Count == 3 && panel.RecipeField.value == panel.RecipeField.choices[0] && panel.ProgressText.Contains("没有在做")
                            && panel.HintText.Contains("后续版本") && panel.PlaceholderText.Contains("占位") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyProductionPanelFirstOpen);
                // 下拉框选中即生效（UI Toolkit 红线 8）：选“合金（矿）”。
                panel.RecipeField.value = panel.RecipeField.choices[1];
                bool chose = P(s, l.Furnace).Recipe?.Id == "alloy_ore" && panel.MessageText.Contains("配方改为");
                Seconds(12f);
                panel.Refresh();
                ProductionService.Producer fp = P(s, l.Furnace);
                bool working = (panel.StateText.Contains("● 工作中") || panel.StateText.Contains("缺料") || panel.StateText.Contains("堵塞")) && panel.RecipeLineText.Contains("金属矿 ×2")
                               && panel.InputsText.StartsWith("输入缓存") && panel.OutputsText.StartsWith("输出缓存") && panel.PowerText.Contains("耗电 10") && fp.Rec.Completed > 0
                               && panel.DetailText.Contains("已完成");
                Expect(idle && chose && working, $"H1 精炼炉面板（真 UXML）：待机“{panel.StateText}”/“{panel.ReasonText}”，配方下拉 3 项；选“合金（矿）”即生效（“{panel.MessageText}”）；" +
                                                 $"之后“{panel.StateText}”、配方行“{panel.RecipeLineText.Replace("\n", " · ")}”、{panel.InputsText}、{panel.OutputsText}、{panel.PowerText}、{panel.DetailText}");
                // 进度条按进度写宽度。
                bool progressed = StepUntil(() =>
                {
                    panel.Refresh();
                    return fp.Rec.Running && panel.ProgressFillPercent > 0f && panel.ProgressText.Contains("%");
                }, 20);
                Expect(progressed, $"H2 进度：“{panel.ProgressText}”，进度条宽度 {panel.ProgressFillPercent:0.#}%");
                // 精炼塔面板：固定配方、三个流体口逐个写接没接上。
                ProductionPanelUIToolkit.Open(tw.Building.BuildingId);
                panel.Refresh();
                bool tower = !panel.RecipeDropdownVisible && panel.RecipeLineText.Contains("固定功能") && panel.FluidsText.Contains("原油进口") && panel.FluidsText.Contains("燃油出口")
                             && panel.FluidsText.Contains("酸液出口") && panel.FluidsText.Contains("没接管线") && panel.StateText.Contains("缺流体");
                Expect(tower, $"H3 精炼塔面板：固定功能（“{panel.RecipeLineText.Replace("\n", " · ")}”）；流体口：{panel.FluidsText.Replace("\n", " | ")}");
                // 提取钻面板：矿脉与震动；按钮：端口… 打开端口面板，“?”打开图鉴。
                ProductionPanelUIToolkit.Open(l.Drill.BuildingId);
                panel.Refresh();
                bool drill = panel.DetailText.Contains("脚下矿脉") && panel.DetailText.Contains("震动") && !panel.RecipeBoxVisible;
                panel.OpenCodex();
                bool codex = MechanicCodex.LastOpenedId == "codex.economy.gathering";
                VisualElement portRoot = MountUxml(UiKitFolder + "BeltPortPanel.uxml", out GameObject portGo);
                bool ports;
                try
                {
                    BeltPortPanelUIToolkit portPanel = portGo.AddComponent<BeltPortPanelUIToolkit>();
                    portPanel.BindView(portRoot);
                    BeltPortPanelUIToolkit.InWorldOverrideForTests = true;
                    panel.OpenPorts();
                    ports = !ProductionPanelUIToolkit.IsOpen && BeltPortPanelUIToolkit.IsOpen && BeltPortPanelUIToolkit.BuildingId == l.Drill.BuildingId;
                }
                finally
                {
                    BeltPortPanelUIToolkit.Close();
                    BeltPortPanelUIToolkit.InWorldOverrideForTests = false;
                    Object.DestroyImmediate(portGo);
                }
                Expect(drill && codex && ports, $"H4 提取钻面板：{panel.DetailText.Replace("\n", " | ")}；“?”打开图鉴“采集建筑”（{MechanicCodex.LastOpenedId}）；“端口…”收起本面板、打开端口面板");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            // 布局探针：中英 × 三种缩放，精炼塔（流体口最多）。
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "ProductionPanel.uxml", "ProductionPanelWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_prod") { hideFlags = HideFlags.HideAndDontSave };
                            ProductionPanelUIToolkit pnl = probeGo.AddComponent<ProductionPanelUIToolkit>();
                            pnl.BindView(pr.panel.visualTree);
                            ProductionPanelUIToolkit.Open(tw.Building.BuildingId);
                            pnl.Refresh();
                            ProductionPanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("ProductionPanelRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"H5 布局探针 ProductionPanel.uxml#ProductionPanelWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
            }
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgProductionSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        // ── I 真文件存读档 ─────────────────────────────────────────────────────────────

        private static bool LayMixed(CampaignState s)
        {
            Line1 l = LayLine(s, "mx");
            if (l == null)
            {
                return false;
            }
            ProductionService.TrySetRecipe(s, l.Furnace.BuildingId, "alloy_ore", out _);
            Tower tw = LayTower(s, "mxt");
            if (tw == null)
            {
                return false;
            }
            GridCell t = tw.Pivot;
            GridCell well = At(t, 0, -5);
            SetTerrain(s, well, "oil");
            bool ok = Pipe(s, well, PipePieceKind.Pump) && Pipe(s, At(t, 0, -4)) && Pipe(s, At(t, 0, -3)) && Pipe(s, At(t, 0, 3)) && Pipe(s, At(t, 0, 4)) && Pipe(s, At(t, 0, 5), PipePieceKind.Tank)
                      && Pipe(s, At(t, 2, 0)) && Pipe(s, At(t, 3, 0));
            Built(s, "waste_pond", "mxp", At(t, 5, 0));
            return ok;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9471, scrap: 300);
            if (!LayMixed(s))
            {
                Fail("I 场景放不下");
                return;
            }
            Seconds(37.3f);
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(30f);
            string continuous = Snap(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snap(rr.State);
                Seconds(30f);
                return Snap(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            CampaignState cur = CampaignSession.Current;
            bool fields = cur.Economy.Producers.Length == 4 && cur.Economy.Producers.Any(r => r.RecipeId == "alloy_ore") && cur.Pipes.ProducerIds.Length == 2
                          && cur.Pipes.ConsumerCapacities.Any(c => c > 0);
            Expect(save.Success && loaded1 == before && fields,
                "I1 真文件存读档：生产建筑的配方、进度、输入 / 输出缓存、累计、震动，以及管线内核里的流体口（消费者缓存、供给者存量）逐字段往返一致" +
                (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous, "I2 读档后接着跑 30 游戏秒，与不存档一直跑逐位一致（生产进度、缓存、流体、震动）" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            // 旧存档（没有生产域字段）：补空域，照常运行。
            CampaignState legacy = NewWorld(9472, scrap: 100);
            legacy.Economy.Producers = null;
            legacy.Economy.RuinCells = null;
            CampaignFgStateDomains.EnsureAll(legacy);
            Expect(legacy.Economy.Producers != null && legacy.Economy.Producers.Length == 0 && legacy.Economy.RuinCells != null && legacy.Economy.Vibration == 0.0,
                "I3 旧存档没有生产建筑 / 废墟格 / 震动字段：读入时补空域");
        }

        // ── J 暂停与倍速、K 观察一致 ──────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 300);
            pausedHeld = true;
            if (!LayMixed(s))
            {
                return "放不下";
            }
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snap(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snap(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 45;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snap(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9481, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下" && reference.Contains("alloy"),
                "J1 暂停中（120 帧）生产不动；0.5x / 1x / 2x / 3x 跑同样的 45 游戏秒，生产进度、缓存、流体与震动逐位一致（配方时间按游戏时钟步数）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9491, true, 1f, false, out _);
            string unseen = RunScenario(9491, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "K1 同一组生产建筑（提取钻 → 精炼炉、精炼塔 → 废液池）在观察与不观察家园时跑 45 游戏秒逐字段一致（FGR-BASE-021：家园后台结果与观察一致）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── M 拆除 ─────────────────────────────────────────────────────────────────

        private static void CheckDemolish()
        {
            CampaignState s = NewWorld(9501, scrap: 300);
            Tower tw = LayTower(s, "dm");
            if (tw == null)
            {
                Fail("M 场景放不下");
                return;
            }
            Line1 l = LayLine(s, "dl");
            if (l == null)
            {
                Fail("M 场景放不下");
                return;
            }
            ProductionService.TrySetRecipe(s, l.Furnace.BuildingId, "alloy_ore", out _);
            Seconds(20f);
            ProductionService.Producer fp = P(s, l.Furnace);
            int buffered = ProductionService.Total(fp.Rec.In) + ProductionService.Total(fp.Rec.Out) + (fp.Rec.Running ? 2 : 0);
            int consumersBefore = PipeNetworkService.Kernel.ConsumerCount;
            int producersBefore = PipeNetworkService.Kernel.ProducerCount;
            int oreBefore = HomeInventory.Stock(s, "metal_ore") + HomeInventory.Stock(s, "alloy");
            GridOpResult r1 = PlanHistory.ToggleDemolish(s, l.Furnace.BuildingId);
            GridOpResult r2 = PlanHistory.ToggleDemolish(s, tw.Building.BuildingId);
            bool gone = StepUntil(() => HomeGridService.FindBuilding(s, l.Furnace.BuildingId) == null && HomeGridService.FindBuilding(s, tw.Building.BuildingId) == null, 300);
            int returned = HomeInventory.Stock(s, "metal_ore") + HomeInventory.Stock(s, "alloy") - oreBefore
                           + (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == "metal_ore" || g.ResourceType == "alloy").Sum(g => g.Amount);
            bool cleaned = !s.Economy.Producers.Any(r => r.BuildingId == l.Furnace.BuildingId || r.BuildingId == tw.Building.BuildingId)
                           && PipeNetworkService.Kernel.ConsumerCount == consumersBefore - 1 && PipeNetworkService.Kernel.ProducerCount == producersBefore - 2;
            Expect(r1.Success && r2.Success && gone && cleaned && buffered > 0 && returned >= buffered,
                $"M1 拆除生产建筑（真实拆除工单）：精炼炉缓存与正在做的那份里的 {buffered} 件退回仓库 / 地上（实收 {returned}，含拆除前后继续进来的料），记录删掉；精炼塔的 3 个流体口从管线内核撤掉（流体随拆除排空）");
        }

        // ── N 建造菜单真实入口 ─────────────────────────────────────────────────────────

        private static void CheckBuildMenu()
        {
            CampaignState s = NewWorld(9511, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? ruin = FindRuinSquare(s, 30);
            GridCell? plain = FindArea(s, 3, 3);
            if (mode == null || ruin == null || plain == null)
            {
                Fail("N 建造模式没绑定 / 找不到废墟或空地");
                return;
            }
            try
            {
                mode.Open();
                bool listed = BuildCatalog.CountInCategory("gathering") >= 3 && BuildCatalog.CountInCategory("processing") >= 4;
                mode.Select("extraction_drill");
                GridCell bad = At(plain.Value, 1, 1);
                mode.PointerDown(s, bad);
                mode.PointerUp(s, bad);
                string badWhy = mode.StatusText;
                mode.Select("fluid_pump");
                mode.PointerDown(s, bad);
                mode.PointerUp(s, bad);
                string pumpWhy = mode.StatusText;
                bool refused = HomeGridService.BuildingAt(s, bad) == null && badWhy.Contains("提取钻要压在金属或稀土矿脉上") && pumpWhy.Contains("流体泵要压在水源或油井上");
                mode.Select("recycler");
                mode.PointerDown(s, ruin.Value);
                mode.PointerUp(s, ruin.Value);
                BuildingRecord ghost = HomeGridService.BuildingAt(s, ruin.Value);
                mode.ClearSelection();
                ProductionService.Sync(s, force: true);
                ProductionService.Producer p = ghost != null ? P(s, ghost) : null;
                Seconds(1f);
                Seen(p);
                bool planned = ghost != null && ghost.BuildingTypeId == "recycler" && p != null && p.State == ProdState.Building && Reason(s, p).Contains("还没建成")
                               && MechanicCodex.IsUnlocked("codex.economy.gathering");
                bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 400);
                Seconds(5f);
                Seen(p);
                Expect(listed && refused && planned && built && p.State != ProdState.Building,
                    $"N1 建造菜单真实入口：提取钻放空地被拒（“{badWhy}”），流体泵放空地被拒（“{pumpWhy}”）；回收站放在废墟上 = 虚影（“{(p == null ? "?" : ProductionService.StateText(p))}”），图鉴“采集建筑”随之解锁；" +
                    $"机器取料施工建成后开工（“{(p == null ? "?" : ProductionService.StateText(p))}”）");
            }
            finally
            {
                mode.Close();
            }
        }

        // ── O 起始区废墟储量（DEBT-FG3GEN01-03）────────────────────────────────────────

        private static void CheckStartRuinReserve()
        {
            int per = ProductionService.RuinScrapPerCell;
            int demand = GridContent.TuningInt("eco.ruin.act1_two_hour_scrap");
            var shown = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 9101, 9203, 9307, 9411, 9521 })
            {
                CampaignState s = NewWorld(seed, observe: false, scrap: 100);
                HomeGridMap map = HomeGridService.MapFor(s);
                GridCell core = HomeGridService.CorePivot(s);
                byte ruin = GridContent.TerrainCode("ruin");
                // 半径 24 内最大的废墟连通区（四连通）。
                var seen = new HashSet<long>();
                int best = 0;
                for (int dy = -24; dy <= 24; dy++)
                {
                    for (int dx = -24; dx <= 24; dx++)
                    {
                        if (dx * dx + dy * dy > 24 * 24)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        long key = ((long)c.X << 32) ^ (uint)c.Y;
                        if (seen.Contains(key) || map.GetTerrain(c) != ruin)
                        {
                            continue;
                        }
                        int size = 0;
                        var q = new Queue<GridCell>();
                        q.Enqueue(c);
                        seen.Add(key);
                        while (q.Count > 0)
                        {
                            GridCell u = q.Dequeue();
                            size++;
                            foreach (GridCell v in new[] { At(u, 1, 0), At(u, -1, 0), At(u, 0, 1), At(u, 0, -1) })
                            {
                                long vk = ((long)v.X << 32) ^ (uint)v.Y;
                                int ddx = v.X - core.X, ddy = v.Y - core.Y;
                                if (!seen.Contains(vk) && ddx * ddx + ddy * ddy <= 24 * 24 && map.GetTerrain(v) == ruin)
                                {
                                    seen.Add(vk);
                                    q.Enqueue(v);
                                }
                            }
                        }
                        best = Math.Max(best, size);
                    }
                }
                bool square = FindRuinSquare(s, 26) != null;
                all &= best * per >= demand && square && 16 * per >= demand;
                shown.Add($"种子 {seed}：{best} 格 × {per} = {best * per}{(square ? "，含 4×4 整块" : "，没有 4×4 整块")}");
            }
            Expect(all, $"O1 起始区废墟储量核对（DEBT-FG3GEN01-03；FG17“废料储量不少于第一幕前两小时的需求”= eco.ruin.act1_two_hour_scrap {demand}）：" +
                        $"五个种子半径 24 内最大废墟群 {string.Join("；", shown)}；一座回收站压在 4×4 整块上就有 16 × {per} = {16 * per} ≥ {demand}");
        }

        // ── P 状态覆盖（FGT-ECO-002 对应部分）─────────────────────────────────────────────

        private static void CheckStateCoverage()
        {
            string[] types = { "recycler", "extraction_drill", "refinery_furnace", "refinery_tower", "blending_station", "waste_pond", "fluid_pump" };
            var required = new List<string>
            {
                "recycler:OutputBlocked", "recycler:MissingInput", "recycler:Working", "recycler:Building",
                "extraction_drill:OutputBlocked", "extraction_drill:NoResource", "extraction_drill:Working",
                "refinery_furnace:Idle", "refinery_furnace:MissingInput", "refinery_furnace:OutputBlocked", "refinery_furnace:Working",
                "refinery_tower:MissingFluid", "refinery_tower:OutputBlocked", "refinery_tower:Working",
                "blending_station:MissingFluid", "blending_station:MissingInput", "blending_station:OutputBlocked", "blending_station:Working",
                "waste_pond:MissingFluid", "waste_pond:Working",
                "fluid_pump:OutputBlocked", "fluid_pump:NoResource", "fluid_pump:Working",
            };
            // FGT-ECO-002：每座建筑 × 缺电 / 损坏 / 关停（下面逐座触发）。
            foreach (string t in types)
            {
                required.Add(t + ":NoPower");
                required.Add(t + ":Damaged");
                required.Add(t + ":Disabled");
            }
            CampaignState s = NewWorld(9531, scrap: 300);
            GridCell? o = FindArea(s, 20, 6);
            if (o == null)
            {
                Fail("P 找不到空地");
                return;
            }
            var list = new List<BuildingRecord>();
            string[] block1 = { "refinery_furnace", "refinery_tower", "blending_station", "waste_pond" };
            for (int i = 0; i < block1.Length; i++)
            {
                list.Add(Built(s, block1[i], "np" + i, At(o.Value, 1 + i * 4, 2)));
            }
            SetFootprint(s, "extraction_drill", At(o.Value, 17, 1), "ore_metal");
            list.Add(Built(s, "extraction_drill", "npd", At(o.Value, 17, 1)));
            GridCell? o2 = FindArea(s, 9, 6);
            if (o2 == null)
            {
                Fail("P 找不到第二块空地");
                return;
            }
            list.Add(Built(s, "recycler", "npr", At(o2.Value, 1, 1)));
            SetFootprint(s, "fluid_pump", At(o2.Value, 6, 2), "water");
            list.Add(Built(s, "fluid_pump", "npp", At(o2.Value, 6, 2)));
            HomeValleyPowerGrid.Recompute(s);
            Seconds(2f);
            bool noPower = true;
            foreach (BuildingRecord b in list)
            {
                ProductionService.Producer p = P(s, b);
                Seen(p);
                noPower &= b.PowerState == BuildingPowerState.Powered || (p.State == ProdState.NoPower && Reason(s, p).Contains("没有电"));
            }
            // 开局核心只有 20 电、发电机还是坏的：这些建筑里至少有一座真的缺电（真实电网）。
            bool anyNoPower = list.Any(b => P(s, b).State == ProdState.NoPower);
            // 每座建筑逐个进缺电 / 损坏 / 关停（测试捷径：直接写建筑的电力 / 施工状态再推进一步生产——电网仲裁由 FG3-LOG-06、关停入口由 R2 覆盖），断言状态与原因。
            int hz = GameClock.StepHz;
            var bad = new List<string>();
            foreach (BuildingRecord b in list)
            {
                ProductionService.Producer p = P(s, b);
                BuildingPowerState keep = b.PowerState;
                b.PowerState = BuildingPowerState.Brownout;
                ProductionService.Step(s, 3, hz);
                Seen(p);
                if (p.State != ProdState.NoPower || !Reason(s, p).Contains("没有电"))
                {
                    bad.Add(b.BuildingTypeId + ":缺电=" + p.State);
                }
                b.PowerState = keep;
                b.ConstructionState = BuildingConstructionState.Damaged;
                ProductionService.Step(s, 3, hz);
                Seen(p);
                if (p.State != ProdState.Damaged || !Reason(s, p).Contains("受损"))
                {
                    bad.Add(b.BuildingTypeId + ":损坏=" + p.State);
                }
                b.ConstructionState = BuildingConstructionState.Disabled;
                ProductionService.Step(s, 3, hz);
                Seen(p);
                if (p.State != ProdState.Disabled || !Reason(s, p).Contains("被关停"))
                {
                    bad.Add(b.BuildingTypeId + ":关停=" + p.State);
                }
                b.ConstructionState = BuildingConstructionState.Operational;
            }
            Resync(s);
            // 调配站输出堵塞：水与稀土矿都够，冷却液出口没接管线——出口存满两份后堵住（测试捷径：直接放料、直接推进生产）。
            ProductionService.Producer bl = P(s, list[2]);
            list[2].PowerState = BuildingPowerState.Powered;
            ItemCatalog.TryGet("water", out ItemDef water);
            int wi = ProductionService.FluidPortIndex(bl, water, false);
            PipeKernel k = PipeNetworkService.Kernel;
            for (int i = 0; i < 400 && bl.State != ProdState.OutputBlocked; i++)
            {
                if (k.TryGetConsumer(bl.Rec.FluidHandles[wi], out PipeConsumerInfo ci))
                {
                    k.SetConsumerBuffer(bl.Rec.FluidHandles[wi], ci.CapacityMl, ci.CapacityMl);
                }
                if (ProductionService.Count(bl.Rec.In, "rare_earth_ore") < 1)
                {
                    ProductionService.Add(ref bl.Rec.In, "rare_earth_ore", 1);
                }
                ProductionService.Step(s, 3, hz);
                Seen(bl);
            }
            string blWhy = Reason(s, bl);
            bool blBlocked = bl.State == ProdState.OutputBlocked && blWhy.Contains("冷却液") && blWhy.Contains("没接管线");
            var missing = required.Where(r => !SeenStates.Contains(r)).ToList();
            Expect(noPower && anyNoPower && bad.Count == 0 && blBlocked && missing.Count == 0,
                $"P1 FGT-ECO-002 状态矩阵：{types.Length} 座建筑 × 各自适用的状态（含每座的缺电 / 损坏 / 关停、废液池单独的缺电分支、调配站输出堵塞“{blWhy.Replace("\n", " · ")}”）都触发过并显示正确原因；" +
                $"共触发 {SeenStates.Count} 种（建筑:状态），不符：{(bad.Count == 0 ? "无" : string.Join("、", bad))}；缺：{(missing.Count == 0 ? "无" : string.Join("、", missing))}");
        }

        // ── R 修复轮（审查 P1 / P2）：流体口编号不跨类型、关停 / 恢复 / 旋转 / 受损时流体逐毫升守恒、回收站产出 ──────────────

        /// <summary>流体口的登记都还在、类型与位置对得上（按记录里口的类型查，不跨类型）。</summary>
        private static bool HandlesIntact(ProductionService.Producer p)
        {
            PipeKernel k = PipeNetworkService.Kernel;
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                int h = p.Rec.FluidHandles[i];
                ProductionService.FluidRt f = p.Fluids[i];
                if (h < 0 || p.Rec.FluidOut[i] != f.Def.IsOutput)
                {
                    return false;
                }
                bool ok = f.Def.IsOutput
                    ? k.TryGetProducer(h, out PipeProducerInfo pi) && pi.X == f.PipeCell.X && pi.Y == f.PipeCell.Y
                    : k.TryGetConsumer(h, out PipeConsumerInfo ci) && ci.X == f.PipeCell.X && ci.Y == f.PipeCell.Y;
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>内核里的消费者 / 供给者都有主：数量 = 全部生产建筑登记着的口数（游戏里只有生产服务登记它们）。</summary>
        private static bool NoOrphans(out string text)
        {
            int c = 0, r = 0;
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                for (int i = 0; i < p.Fluids.Length; i++)
                {
                    if (p.Rec.FluidHandles[i] >= 0)
                    {
                        if (p.Rec.FluidOut[i])
                        {
                            r++;
                        }
                        else
                        {
                            c++;
                        }
                    }
                }
            }
            PipeKernel k = PipeNetworkService.Kernel;
            text = $"内核消费者 {k.ConsumerCount} / 登记 {c}，供给者 {k.ProducerCount} / 登记 {r}";
            return k.ConsumerCount == c && k.ProducerCount == r;
        }

        /// <summary>每个流体口现在的量（毫升）= 内核里的缓存 / 存量 + 建筑自己留着的（流体只能在其中一处）。</summary>
        private static long[] PortAmounts(ProductionService.Producer p)
        {
            var a = new long[p.Fluids.Length];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = p.Rec.FluidHeld[i] + (p.Fluids[i].Def.IsOutput ? ProducerStock(p, i) : ConsumerBuffer(p, i));
            }
            return a;
        }

        /// <summary>测试捷径：给每个流体口放一点流体（奇数毫升，丢一毫升都看得出来；进口不够开一份配方，建筑不会自己开工）。</summary>
        private static long[] Seed(ProductionService.Producer p)
        {
            PipeKernel k = PipeNetworkService.Kernel;
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                int h = p.Rec.FluidHandles[i];
                long want = 30000L + 1000L * (i + 1) + 7;
                if (p.Fluids[i].Def.IsOutput && k.TryGetProducer(h, out PipeProducerInfo pi))
                {
                    k.AddProducerStock(h, Math.Min(want, pi.CapacityMl - pi.StockMl));
                }
                else if (!p.Fluids[i].Def.IsOutput && k.TryGetConsumer(h, out PipeConsumerInfo ci) && ci.CapacityMl > 0)
                {
                    k.SetConsumerBuffer(h, ci.CapacityMl, Math.Min(want, ci.CapacityMl));
                }
            }
            return PortAmounts(p);
        }

        private static string Ml(long[] a) => string.Join("/", a);

        private static void CheckFluidHandleIdentity()
        {
            foreach (bool towerFirst in new[] { true, false })
            {
                CampaignState s = NewWorld(towerFirst ? 9561 : 9562, scrap: 400);
                PowerUp(s);
                GridCell? o = FindArea(s, 14, 8);
                if (o == null)
                {
                    Fail("R1 找不到 14×8 的空地");
                    continue;
                }
                GridCell tp = At(o.Value, 1, 3), pp = At(o.Value, 5, 3), bp = At(o.Value, 9, 3);
                BuildingRecord tower, pond, blend;
                if (towerFirst)
                {
                    tower = Built(s, "refinery_tower", "id_tw", tp);
                    pond = Built(s, "waste_pond", "id_pond", pp);
                    blend = Built(s, "blending_station", "id_bl", bp);
                }
                else
                {
                    pond = Built(s, "waste_pond", "id_pond", pp);
                    blend = Built(s, "blending_station", "id_bl", bp);
                    tower = Built(s, "refinery_tower", "id_tw", tp);
                }
                PipeKernel k = PipeNetworkService.Kernel;
                ProductionService.Producer tw = P(s, tower), bl = P(s, blend), wp = P(s, pond);
                // 编号确实重号（消费者与供给者各自从 1 起）：这正是旧代码“先试供给者再试消费者”会撤错的前提。
                bool overlap = wp.Rec.FluidHandles[0] >= 0 && (k.TryGetProducer(wp.Rec.FluidHandles[0], out _) || tw.Rec.FluidHandles.Any(h => k.TryGetConsumer(h, out _) && k.TryGetProducer(h, out _)));
                long[] t0 = Seed(tw), b0 = Seed(bl);
                int[] tH = (int[])tw.Rec.FluidHandles.Clone(), bH = (int[])bl.Rec.FluidHandles.Clone();
                int c0 = k.ConsumerCount, r0 = k.ProducerCount;
                GridOpResult d = PlanHistory.ToggleDemolish(s, pond.BuildingId);
                bool gone = StepUntil(() => HomeGridService.FindBuilding(s, pond.BuildingId) == null, 300);
                Resync(s);
                tw = P(s, tower);
                bl = P(s, blend);
                bool intact = HandlesIntact(tw) && HandlesIntact(bl) && tw.Rec.FluidHandles.SequenceEqual(tH) && bl.Rec.FluidHandles.SequenceEqual(bH)
                              && PortAmounts(tw).SequenceEqual(t0) && PortAmounts(bl).SequenceEqual(b0);
                bool counts = k.ConsumerCount == c0 - 1 && k.ProducerCount == r0;
                bool noOrphan = NoOrphans(out string orphanText);
                Expect(overlap && d.Success && gone && intact && counts && noOrphan,
                    $"R1 [{(towerFirst ? "先建精炼塔" : "先建废液池")}] 消费者 / 供给者编号重号时，真实拆除废液池只撤它自己的消费者：精炼塔 {Ml(t0)} → {Ml(PortAmounts(tw))} 毫升、" +
                    $"调配站 {Ml(b0)} → {Ml(PortAmounts(bl))} 毫升，句柄不变；消费者 {c0} → {k.ConsumerCount}（−1）、供给者 {r0} → {k.ProducerCount}（不变）；{orphanText}");
                if (!towerFirst)
                {
                    continue;
                }
                // 再拆精炼塔：撤 1 个消费者 + 2 个供给者，调配站同号的口不受影响。
                c0 = k.ConsumerCount;
                r0 = k.ProducerCount;
                GridOpResult d2 = PlanHistory.ToggleDemolish(s, tower.BuildingId);
                bool gone2 = StepUntil(() => HomeGridService.FindBuilding(s, tower.BuildingId) == null, 300);
                Resync(s);
                bl = P(s, blend);
                bool blIntact = HandlesIntact(bl) && bl.Rec.FluidHandles.SequenceEqual(bH) && PortAmounts(bl).SequenceEqual(b0);
                bool noOrphan2 = NoOrphans(out string orphanText2);
                Expect(d2.Success && gone2 && blIntact && k.ConsumerCount == c0 - 1 && k.ProducerCount == r0 - 2 && noOrphan2,
                    $"R1 再拆精炼塔：消费者 {c0} → {k.ConsumerCount}（−1）、供给者 {r0} → {k.ProducerCount}（−2），调配站的口与 {Ml(PortAmounts(bl))} 毫升原样；{orphanText2}");
            }
        }

        private static void CheckFluidHeldOnStateChange()
        {
            CampaignState s = NewWorld(9571, scrap: 400);
            PowerUp(s);
            GridCell? o = FindArea(s, 10, 8);
            if (o == null)
            {
                Fail("R2 找不到 10×8 的空地");
                return;
            }
            BuildingRecord tower = Built(s, "refinery_tower", "st_tw", At(o.Value, 1, 3));
            BuildingRecord blend = Built(s, "blending_station", "st_bl", At(o.Value, 6, 3));
            long[] t0 = Seed(P(s, tower)), b0 = Seed(P(s, blend));
            // 关停（电力面板同一入口）→ 对账：口撤掉，流体逐毫升留在建筑里。
            bool off = HomeValleyPowerGrid.TryToggleShutdown(s, tower.BuildingId).Success && HomeValleyPowerGrid.TryToggleShutdown(s, blend.BuildingId).Success;
            Resync(s);
            ProductionService.Producer tw = P(s, tower), bl = P(s, blend);
            bool orphansOk1 = NoOrphans(out string o1);
            bool heldOff = tw.Rec.FluidHandles.All(h => h < 0) && bl.Rec.FluidHandles.All(h => h < 0) && tw.Rec.FluidHeld.SequenceEqual(t0) && bl.Rec.FluidHeld.SequenceEqual(b0)
                           && orphansOk1;
            string offText = $"精炼塔留着 {Ml(tw.Rec.FluidHeld)}、调配站留着 {Ml(bl.Rec.FluidHeld)}；{o1}";
            // 恢复 → 对账：放回口里。
            bool on = HomeValleyPowerGrid.TryToggleShutdown(s, tower.BuildingId).Success && HomeValleyPowerGrid.TryToggleShutdown(s, blend.BuildingId).Success;
            Resync(s);
            tw = P(s, tower);
            bl = P(s, blend);
            bool orphansOk2 = NoOrphans(out string o2);
            bool back = HandlesIntact(tw) && HandlesIntact(bl) && PortAmounts(tw).SequenceEqual(t0) && PortAmounts(bl).SequenceEqual(b0)
                        && tw.Rec.FluidHeld.All(x => x == 0) && bl.Rec.FluidHeld.All(x => x == 0) && orphansOk2;
            Expect(off && heldOff && on && back,
                $"R2 关停 / 恢复（电力面板入口）流体逐毫升守恒：注入精炼塔 {Ml(t0)}、调配站 {Ml(b0)} 毫升；关停后 {offText}；恢复后口里 {Ml(PortAmounts(tw))} / {Ml(PortAmounts(bl))}；{o2}");
            // 原地旋转（口跟着转：撤掉再在新位置登记）。
            GridDir faceBefore = bl.Fluids[0].Face;
            GridOpResult rot = HomeGridService.TryRotateTo(s, blend.BuildingId, 90);
            Resync(s);
            bl = P(s, blend);
            bool rotated = rot.Success && bl.Fluids[0].Face != faceBefore && HandlesIntact(bl) && PortAmounts(bl).SequenceEqual(b0) && bl.Rec.FluidHeld.All(x => x == 0);
            // 受损 → 修好。
            tower.ConstructionState = BuildingConstructionState.Damaged;
            Resync(s);
            tw = P(s, tower);
            bool dmgHeld = tw.Rec.FluidHandles.All(h => h < 0) && tw.Rec.FluidHeld.SequenceEqual(t0);
            tower.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            Resync(s);
            tw = P(s, tower);
            bool orphansOk3 = NoOrphans(out string o3);
            bool repaired = HandlesIntact(tw) && PortAmounts(tw).SequenceEqual(t0) && tw.Rec.FluidHeld.All(x => x == 0) && orphansOk3;
            Expect(rotated && dmgHeld && repaired,
                $"R2 原地旋转调配站（口朝向 {faceBefore} → {bl.Fluids[0].Face}）后口里仍是 {Ml(PortAmounts(bl))} 毫升；精炼塔受损时留在建筑里、修好后放回 {Ml(PortAmounts(tw))}；{o3}");

            // R3：周期快做完时关停、对账撤口，恢复后在下一次对账之前做完那份——燃油 / 酸液留在建筑里，下一次对账放回口里（旧代码直接丢掉）。
            CampaignState s3 = NewWorld(9572, scrap: 400);
            PowerUp(s3);
            GridCell? o3a = FindArea(s3, 5, 8);
            if (o3a == null)
            {
                Fail("R3 找不到 5×8 的空地");
                return;
            }
            BuildingRecord t3 = Built(s3, "refinery_tower", "r3_tw", At(o3a.Value, 1, 3));
            ProductionService.Producer p3 = P(s3, t3);
            ItemCatalog.TryGet("crude", out ItemDef crude);
            ItemCatalog.TryGet("fuel", out ItemDef fuel);
            ItemCatalog.TryGet("acid", out ItemDef acid);
            int ci3 = ProductionService.FluidPortIndex(p3, crude, false), fi3 = ProductionService.FluidPortIndex(p3, fuel, true), ai3 = ProductionService.FluidPortIndex(p3, acid, true);
            PipeKernel k = PipeNetworkService.Kernel;
            k.TryGetConsumer(p3.Rec.FluidHandles[ci3], out PipeConsumerInfo cinfo);
            k.SetConsumerBuffer(p3.Rec.FluidHandles[ci3], cinfo.CapacityMl, cinfo.CapacityMl);
            int hz = GameClock.StepHz;
            ProductionService.Step(s3, 3, hz);
            bool started = p3.Rec.Running && p3.Rec.Duration > 3;
            ProductionService.Step(s3, (int)Math.Max(1, p3.Rec.Duration - p3.Rec.Progress - 2), hz);
            long done0 = p3.Rec.Completed;
            long crudeLeft = ConsumerBuffer(p3, ci3);
            HomeValleyPowerGrid.TryToggleShutdown(s3, t3.BuildingId);
            Resync(s3);
            HomeValleyPowerGrid.TryToggleShutdown(s3, t3.BuildingId);
            ProductionService.Step(s3, 3, hz);
            p3 = P(s3, t3);
            bool heldOut = p3.Rec.Completed == done0 + 1 && p3.Rec.FluidHandles.All(h => h < 0) && p3.Rec.FluidHeld[fi3] == 60000 && p3.Rec.FluidHeld[ai3] == 20000
                           && p3.Rec.FluidHeld[ci3] == crudeLeft;
            string heldText = Ml(p3.Rec.FluidHeld);
            Resync(s3);
            p3 = P(s3, t3);
            bool putBack = HandlesIntact(p3) && ProducerStock(p3, fi3) == 60000 && ProducerStock(p3, ai3) == 20000 && ConsumerBuffer(p3, ci3) == crudeLeft
                           && p3.Rec.FluidHeld.All(x => x == 0);
            Expect(started && heldOut && putBack,
                $"R3 恢复运转到下一次对账之间做完一份：60 升燃油 + 20 升酸液先留在建筑里（{heldText} 毫升），对账后放回口里（燃油 {ProducerStock(p3, fi3)}、酸液 {ProducerStock(p3, ai3)}、原油缓存 {ConsumerBuffer(p3, ci3)}）——不丢");
        }

        private static void CheckRecyclerOutputs()
        {
            CampaignState s = NewWorld(9591, scrap: 300);
            PowerUp(s);
            GridCell? o = FindArea(s, 6, 6);
            if (o == null)
            {
                Fail("R4 找不到 6×6 的空地");
                return;
            }
            // 空地上的回收站（测试捷径：不压废墟，只分解送进来的东西）；直接推进生产（不走传送带，产出全留在输出缓存里便于逐件核对）。
            BuildingRecord rec = Built(s, "recycler", "r4", At(o.Value, 1, 1));
            ProductionService.Producer p = P(s, rec);
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            ProductionService.Add(ref p.Rec.In, "alloy", 3);
            ProductionService.Add(ref p.Rec.In, "zz_removed_item", 1);
            int hz = GameClock.StepHz;
            for (int i = 0; i < 400 && p.Rec.ItemsRecycled < 4; i++)
            {
                ProductionService.Step(s, 3, hz);
            }
            int scrapOut = ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
            bool outputs = p.Rec.ItemsRecycled == 4 && scrapOut == 3 * alloy.RecycleScrap + 1 && ProductionService.Count(p.Rec.Out, "alloy") == 0
                           && ProductionService.Count(p.Rec.Out, "zz_removed_item") == 0 && ProductionService.Total(p.Rec.In) == 0;
            Expect(outputs, $"R4 回收站分解送来的 3 件合金 + 1 件物品表里没有的东西：输出缓存 = {scrapOut} 废料（3 × {alloy.RecycleScrap} + 1），没有合金、没有未知物品（分解产出一律是废料）");
            // 正在分解一件废料时拆除：那一件原样退回（旧代码把“分解废料”当成“拆废墟”漏退）。
            ProductionService.Add(ref p.Rec.Out, ItemCatalog.ScrapId, -scrapOut);
            ProductionService.Add(ref p.Rec.In, ItemCatalog.ScrapId, 1);
            ProductionService.Step(s, 1, hz);
            bool runningScrap = p.Rec.Running && !p.Rec.PendingRuin && p.Rec.PendingItem == ItemCatalog.ScrapId;
            int scrapBefore = ScrapEverywhere(s);
            int n = ProductionService.OnDemolished(s, rec, "selfcheck-r4a");
            bool itemBack = runningScrap && n == 1 && ScrapEverywhere(s) - scrapBefore == 1;
            // 回收站正在拆废墟时拆除：开工时已从废墟扣掉的那批按废料退回。
            CampaignState s2 = NewWorld(9592, scrap: 300);
            GridCell? ruin = FindRuinSquare(s2, 30);
            if (ruin == null)
            {
                Fail("R4 核心 30 格内找不到 4×4 整块废墟");
                return;
            }
            PowerUp(s2);
            BuildingRecord rr = Built(s2, "recycler", "r4b", ruin.Value);
            ProductionService.Producer rp = P(s2, rr);
            ProductionService.Step(s2, 1, hz);
            bool runningRuin = rp.Rec.Running && rp.Rec.PendingRuin && rp.Rec.PendingAmount > 0;
            int pending = rp.Rec.PendingAmount;
            int ruinBefore = ProductionService.RuinLeft(s2, rp, out _);
            int before2 = ScrapEverywhere(s2);
            int n2 = ProductionService.OnDemolished(s2, rr, "selfcheck-r4b");
            bool ruinBack = runningRuin && n2 == pending && ScrapEverywhere(s2) - before2 == pending;
            Expect(itemBack && ruinBack,
                $"R4 拆除时正在做的那份不丢：分解中的 1 件废料原样退回（退回 {n} 件）；拆废墟中开工时已从废墟（剩 {ruinBefore}）扣掉的 {pending} 废料退回仓库（退回 {n2}）");
        }

        private static int ScrapEverywhere(CampaignState s) =>
            s.Scrap + (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == ItemCatalog.ResourceTypeOf(ItemCatalog.ScrapId)).Sum(g => g.Amount);

        // ── S 流体泵（FG04 第 3.3 节 2×2 采集建筑：放在水源或油井上抽取流体）──────────────────────────

        private static void CheckFluidPump()
        {
            CampaignState s = NewWorld(9581, scrap: 400);
            PowerUp(s);
            GridCell? o = FindArea(s, 9, 8);
            if (o == null)
            {
                Fail("S 找不到 9×8 的空地");
                return;
            }
            // 水泵（测试捷径：脚下 2×2 改成水源；真实世界里的水源放置由 B1 覆盖）：出口朝东，口外那一格 = (a.x + 2, a.y)。
            GridCell a = At(o.Value, 0, 1);
            SetFootprint(s, "fluid_pump", a, "water");
            BuildingRecord wRec = Built(s, "fluid_pump", "pw", a);
            ProductionService.Producer wp = P(s, wRec);
            Seconds(2f);
            Seen(wp);
            string noPipe = Reason(s, wp);
            string noPipeState = ProductionService.StateText(wp);
            GridCell pc = At(a, 2, 0);
            bool unconnected = wp.State == ProdState.OutputBlocked && wp.Reason == ProdReason.PumpBlocked && noPipe.Contains("没接管线") && noPipe.Contains($"（{pc.X}, {pc.Y}）")
                               && wp.Rec.PumpedMl == 0 && wp.SourceFluid?.Id == "water" && wp.SourceCells == 4 && wRec.PowerState == BuildingPowerState.Powered;
            bool laid = Pipe(s, pc) && Pipe(s, At(a, 3, 0)) && Pipe(s, At(a, 4, 0), PipePieceKind.Tank);
            Seconds(10f);
            Seen(wp);
            PipeNetworkService.Kernel.TryGetCellInfo(At(a, 4, 0).X, a.Y, out PipeCellInfo tank);
            ItemCatalog.TryGet("water", out ItemDef water);
            long here = tank.TankStockMl + ProducerStock(wp, 0);
            string working = Reason(s, wp);
            bool pumping = laid && wp.State == ProdState.Working && working.Contains("正在抽水") && working.Contains("600") && tank.Fluid == water.FluidId
                           && here == wp.Rec.PumpedMl && wp.Rec.PumpedMl >= 80000 && wp.Rec.PumpedMl <= 100000;
            Expect(unconnected && pumping,
                $"S1 流体泵压在水源上：没接管线时“{noPipeState}”（“{noPipe}”），不抽；接上两格管线 + 储罐后“{working}”，10 秒抽了 {wp.Rec.PumpedMl} 毫升（600 升/分钟），" +
                $"储罐 + 出口 = {here} 毫升 = 累计抽出（逐毫升守恒）");
            // 油泵：压在油井上抽原油；下游没人用、也没有储罐时出口满了就停抽（流体留在地下）。
            GridCell b = At(o.Value, 0, 5);
            SetFootprint(s, "fluid_pump", b, "oil");
            BuildingRecord oRec = Built(s, "fluid_pump", "po", b);
            bool oilPipes = Pipe(s, At(b, 2, 0)) && Pipe(s, At(b, 3, 0));
            Resync(s);
            ProductionService.Producer op = P(s, oRec);
            bool full = StepUntil(() => op.State == ProdState.OutputBlocked && op.Rec.PumpedMl > 0, 30);
            Seen(op);
            string fullWhy = Reason(s, op);
            ItemCatalog.TryGet("crude", out ItemDef crude);
            PipeNetworkService.Kernel.TryGetProducer(op.Rec.FluidHandles[0], out PipeProducerInfo opi);
            long pumpedThen = op.Rec.PumpedMl;
            Seconds(3f);
            bool oil = oilPipes && full && op.SourceFluid == crude && opi.Fluid == crude.FluidId && opi.StockMl == opi.CapacityMl && fullWhy.Contains("没有地方放")
                       && op.Rec.PumpedMl == pumpedThen && op.Rec.PumpedMl == opi.StockMl + opi.TotalDeliveredMl;
            Expect(oil, $"S2 流体泵压在油井上抽原油；下游没人用时出口存满 {opi.StockMl / 1000}/{opi.CapacityMl / 1000} 升就停抽（“{fullWhy}”），之后 3 秒累计抽出不变（{op.Rec.PumpedMl} 毫升）");
            // 通用面板（真 UXML）：脚下流体源、速率、累计、出口接没接上；没有进度条与配方。
            VisualElement root = MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(wRec.BuildingId);
                panel.Refresh();
                bool panelOk = panel.PanelVisible && panel.TitleText.Contains("流体泵") && panel.StateText.Contains("● 工作中") && panel.DetailText.Contains("脚下流体源")
                               && panel.DetailText.Contains("水源") && panel.DetailText.Contains("600") && panel.DetailText.Contains("累计抽出") && panel.FluidsText.Contains("水出口")
                               && panel.FluidsText.Contains("接上网络") && !panel.RecipeBoxVisible && panel.PowerText.Contains("耗电 5");
                panel.OpenCodex();
                bool codex = MechanicCodex.LastOpenedId == "codex.economy.gathering";
                Expect(panelOk && codex, $"S3 流体泵通用面板：“{panel.StateText}”/“{panel.ReasonText}”；{panel.DetailText}；{panel.FluidsText}；{panel.PowerText}；“?”打开图鉴“采集建筑”");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            // 脚下的流体源没了（地形被改）：不在资源点上，写原因；出口撤掉。
            SetFootprint(s, "fluid_pump", b, "buildable");
            Resync(s);
            Seconds(1f);
            op = P(s, oRec);
            Seen(op);
            string noSrc = Reason(s, op);
            bool orphansOk = NoOrphans(out string orphan);
            bool noResource = op.State == ProdState.NoResource && noSrc.Contains("脚下不是流体源") && op.Rec.FluidHandles[0] < 0 && orphansOk;
            Expect(noResource, $"S4 流体泵脚下不再是流体源：“{ProductionService.StateText(op)}”（“{noSrc}”），出口从管线内核撤掉；{orphan}");
        }

        // ── Q 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9541, observe: false, scrap: 300);
            PowerUp(s);
            // 800 座精炼炉（测试捷径：只登记记录，不占格网——推进只读记录与配方，与位置无关）：一半在做“合金（废料）”、输入缓存满，一半没选配方。
            var extra = new List<BuildingRecord>(800);
            GridCell core = HomeGridService.CorePivot(s);
            for (int i = 0; i < 800; i++)
            {
                extra.Add(new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":fgprod_perf" + i.ToString("D4"),
                    BuildingTypeId = "refinery_furnace",
                    RegionId = HomeValleyLayout.RegionId,
                    GridX = core.X + 200 + (i % 40) * 4,
                    GridY = core.Y + 200 + (i / 40) * 4,
                    Position = new Vector2(core.X + 200 + (i % 40) * 4, core.Y + 200 + (i / 40) * 4),
                    Health = 100f,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerPriority = 3,
                    PowerState = BuildingPowerState.Powered,
                    Inventory = Array.Empty<CargoEntry>(),
                    QueueIds = Array.Empty<string>(),
                });
            }
            s.BuildingRecords = s.BuildingRecords.Concat(extra).ToArray();
            ProductionService.Step(s, 3, GameClock.StepHz);
            foreach (BuildingRecord b in extra.Where((_, i) => i % 2 == 0))
            {
                ProductionService.TrySetRecipe(s, b.BuildingId, "alloy_scrap", out _);
                ProductionService.Producer p = P(s, b);
                ProductionService.Add(ref p.Rec.In, "scrap", 8);
            }
            // 输出缓存满了也算推进（堵塞判定同样要走）。
            var samples = new List<double>(400);
            ProductionService.Step(s, 3, GameClock.StepHz);
            GC.Collect();
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = new Stopwatch();
            for (int i = 0; i < 400; i++)
            {
                sw.Restart();
                ProductionService.Step(s, 3, GameClock.StepHz);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                if (i % 80 == 0)
                {
                    // 保持一半的炉子有料（模拟输入口持续送料）。
                    foreach (BuildingRecord b in extra.Where((_, k) => k % 2 == 0))
                    {
                        ProductionService.Producer p = P(s, b);
                        if (ProductionService.Count(p.Rec.In, "scrap") < 4)
                        {
                            ProductionService.Add(ref p.Rec.In, "scrap", 4);
                        }
                    }
                }
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            samples.Sort();
            double p95 = samples[(int)(samples.Count * 0.95)];
            double max = samples[samples.Count - 1];
            int count = ProductionService.ProducerCount;
            var sync = Stopwatch.StartNew();
            ProductionService.Sync(s, force: true);
            sync.Stop();
            PerfLines.Add($"800 座生产建筑每生产步（20 Hz，Editor batchmode）P95 {p95:F3} ms、最大 {max:F3} ms；400 步托管分配 {alloc} 字节（含补料夹具）；强制对账 {sync.Elapsed.TotalMilliseconds:F3} ms");
            // 阈值：每生产步 ≤ 0.5 ms（Editor；真机热更层解释执行按 ×5 折算 ≤ 2.5 ms，世界步 60 Hz 里每 3 步一次，平均每帧 < 1 ms）。
            ExpectPerf(count >= 800 && alloc < 64 * 1024,
                $"Q1 性能：{count} 座生产建筑每生产步 P95 {p95:F3} ms（阈值 0.5 ms）、最大 {max:F3} ms；400 步托管分配 {alloc} 字节（阈值 64 KB，补料夹具也在里面；推进本身零分配）；强制对账 {sync.Elapsed.TotalMilliseconds:F3} ms（阈值 30 ms）",
                PerfGate.Le(p95, 0.5, "每生产步 P95 ms"), PerfGate.Le(sync.Elapsed.TotalMilliseconds, 30.0, "强制对账 ms"));
        }

        // ── 基础 ─────────────────────────────────────────────────────────────────

        private static string LocateRepo()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "tools", "cell_tables", "fgdata.py")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("python", args)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    return (p.ExitCode, output);
                }
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }

        private static string Tail(string s)
        {
            string[] lines = (s ?? string.Empty).Replace("\r", string.Empty).Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - 2)));
        }

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                UiEscapeStack.Clear();
                InputRouter.SetBuildMode(false);
                InputRouter.BuildDragActive = false;
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
