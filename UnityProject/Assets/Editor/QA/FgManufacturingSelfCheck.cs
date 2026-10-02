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
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
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
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-03 制造建筑与装配站改造的自动验收（FG04 第 3.3 节制造建筑；FGR-ECO-002 配方表；卡片结果“零件工坊、电子组装台、组件工坊、固件刻录台；装配站改为从产线取料生产机器；
    /// 电路合成台保留 Demo 的闭环”、必须同时交付“配方选择记住上一次的设置；复制设置到同类建筑”、负向“排产时材料不足；输出口堵塞”、验收 FGT-ECO-001；
    /// 承接 DEBT-FG4ECO01-01（蓝图材料构成）、DEBT-FG3LOG03-01（装配站输入口）、DEBT-FG2FW05-01（取料路线被堵就等）、FG-GAP-059（部分）、DEBT-FG1SIG01-02（刻录台正式来源））。
    /// 全部起真实系统：真实世界模拟与家园（传送带 / 生产 / 装配站队列都在世界步里跑）、真实 AOT 传送带内核（含新收法 AcceptSet）、真实存读档文件、真 UXML 面板。
    /// 场地按种子生成的地形找空地（B25），复用 FgProductionSelfCheck 的夹具。
    /// A 数据；B 零件工坊（单输入口、换配方退料、输出堵塞）；C 电子组装台（两个输入口各收一种、缺第二种写明哪个口、送错物品被拒、换配方退料、诊断追到对的口）；
    /// D 组件工坊；E 固件刻录台（没选目标 / 未破解被拒 / 刻出芯片进固件库 / 存放满堵塞 / 中途换目标退料）；F FGT-ECO-001 从原料到机器（分段真实建筑，账目守恒）；
    /// G 装配站取料（缺料等待、缓存 + 仓库取料、取消退回、废料代付折算、代付也不够、断电不重取、被毁退回、路线被堵、改造只补差额、输入口收货集合与上限、旧档队列项）；
    /// H 配方记忆与复制设置到同类建筑；I 真文件存读档；J 暂停与 0.5x～3x；K 观察 / 不观察一致；L 面板（真 UXML）；M 建造菜单真实入口；N 状态覆盖；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgManufacturingSelfCheck）。
    /// </summary>
    public static class FgManufacturingSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly HashSet<string> SeenStates = new HashSet<string>();

        [MenuItem("BinGames/QA/自检/FG 制造建筑与装配站")]
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
            Line("\n[制造建筑与装配站] 零件工坊 / 电子组装台 / 组件工坊 / 固件刻录台、装配站从产线取料、配方记忆与复制设置（FG4-ECO-03）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgmfg-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                AssemblyMaterials.Reload();
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
                     "生产服务 / 装配站队列在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），传送带内核在 AOT（真机 IL2CPP）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckPartsWorkshop);
                Step(CheckElectronicsBench);
                Step(CheckComponentWorkshop);
                Step(CheckFirmwareBurner);
                Step(CheckRawToMachine);
                Step(CheckAssemblyBasics);
                Step(CheckAssemblySubstitute);
                Step(CheckAssemblyPowerAndDestroyed);
                Step(CheckAssemblyRoute);
                Step(CheckAssemblyRetrofitAndLegacy);
                Step(CheckAssemblyPort);
                Step(CheckRecipeMemoryAndCopy);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanels);
                Step(CheckBuildMenu);
                Step(CheckStateCoverage);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"制造建筑自检抛异常：{e}");
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
                AssemblyMaterials.ResetForTests();
                ProductionService.ResetForTests();
                HomeValleyFactory.ResetSessionState();
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
            Line($"  · [制造建筑与装配站] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        /// <summary>一座建筑 + 它每个物品口外的 3 格传送带：输入口的带朝着建筑（最远那格可以挂测试来料口），输出口的带背离建筑、末端正前方挂“下游”测试收货口（或死路）。</summary>
        private sealed class Rig
        {
            public BuildingRecord B;
            public readonly Dictionary<string, GridCell> InFar = new Dictionary<string, GridCell>();
            public readonly Dictionary<string, GridCell> InNear = new Dictionary<string, GridCell>();
            public readonly Dictionary<string, BeltDir> InDir = new Dictionary<string, BeltDir>();
            public GridCell OutStart;
            public BeltDir OutDir;
            public int Sink = -1;
            public readonly Dictionary<string, int> Src = new Dictionary<string, int>();
        }

        private static ProductionService.Producer P(CampaignState s, Rig r) => F.P(s, r.B);

        /// <summary>新世界：仓库修好（非废料物品要有运转中的仓库才能入库）、发电机修好 + 一座发电机 2。</summary>
        private static CampaignState World(int seed, bool observe = true, int scrap = 300)
        {
            CampaignState s = F.NewWorld(seed, observe, scrap);
            foreach (BuildingRecord wh in s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse))
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
            }
            F.PowerUp(s);
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            // 测试夹具：仓库输出口停止输出（没接带的输出口会预取几件库存等着推，会让库存断言随种子漂移；输出口本身由 FG3-LOG-03 / FG4-ECO-01 覆盖）。
            foreach (BuildingRecord wh in s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse))
            {
                BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            }
            return s;
        }

        /// <summary>核心附近一块 12×12 的空地，放一座建筑与它的端口传送带（按种子地形找，B25）。</summary>
        private static Rig Lay(CampaignState s, string typeId, string key, bool sink = true)
        {
            GridCell? o = F.FindArea(s, 12, 12, 7f, 22f);
            if (o == null)
            {
                return null;
            }
            return LayAt(s, typeId, key, F.At(o.Value, 5, 5), sink);
        }

        private static Rig LayAt(CampaignState s, string typeId, string key, GridCell pivot, bool sink)
        {
            var r = new Rig { B = F.Built(s, typeId, key, pivot) };
            F.Resync(s);
            foreach (BuildingPort row in GridContent.PortsOf(typeId))
            {
                if (row.Role != "prod")
                {
                    continue;
                }
                BeltPortService.Binding bind = BeltPortService.Find(r.B.BuildingId, row.Id);
                if (bind == null)
                {
                    return null;
                }
                Vector2Int v = GridMath.DirVector(bind.Face);
                if (row.Kind == "out")
                {
                    var dir = (BeltDir)(int)bind.Face;
                    for (int i = 0; i < 3; i++)
                    {
                        if (!BeltNetworkService.TryPlace(s, new GridCell(bind.BeltCell.X + v.x * i, bind.BeltCell.Y + v.y * i), dir, 2).Ok)
                        {
                            return null;
                        }
                    }
                    r.OutStart = bind.BeltCell;
                    r.OutDir = dir;
                    if (sink)
                    {
                        r.Sink = F.TestSink(s, new GridCell(bind.BeltCell.X + v.x * 3, bind.BeltCell.Y + v.y * 3));
                    }
                }
                else
                {
                    var dir = (BeltDir)(((int)bind.Face + 2) & 3);
                    for (int i = 0; i < 3; i++)
                    {
                        if (!BeltNetworkService.TryPlace(s, new GridCell(bind.BeltCell.X + v.x * i, bind.BeltCell.Y + v.y * i), dir, 2).Ok)
                        {
                            return null;
                        }
                    }
                    r.InFar[row.Id] = new GridCell(bind.BeltCell.X + v.x * 2, bind.BeltCell.Y + v.y * 2);
                    r.InNear[row.Id] = bind.BeltCell;
                    r.InDir[row.Id] = dir;
                }
            }
            F.Resync(s);
            return r;
        }

        /// <summary>在这个输入口的来料带最远那一格挂测试来料口（同一格之前的来料口先撤掉：一格只能有一个来料口）。</summary>
        private static int Feed(CampaignState s, Rig r, string portKey, string itemId, int count)
        {
            if (r.Src.TryGetValue(portKey, out int old))
            {
                BeltNetworkService.Kernel.RemovePort(old);
            }
            int id = F.TestSource(s, r.InFar[portKey], itemId, count);
            r.Src[portKey] = id;
            return id;
        }

        private static int SinkTotal(int sinkId) => sinkId >= 0 && BeltNetworkService.Kernel.TryGetPortInfo(sinkId, out BeltPortInfo i) ? (int)i.Total : 0;

        private static int SourceTotal(int sourceId) => BeltNetworkService.Kernel.TryGetPortInfo(sourceId, out BeltPortInfo i) ? (int)i.Total : 0;

        private static int InBelts(Rig r, string portKey)
        {
            GridCell near = r.InNear[portKey];
            GridCell far = r.InFar[portKey];
            int dx = Math.Sign(far.X - near.X), dy = Math.Sign(far.Y - near.Y);
            int n = 0;
            for (int i = 0; i < 3; i++)
            {
                n += F.OnBelt(near.X + dx * i, near.Y + dy * i);
            }
            return n;
        }

        private static int OutBelts(Rig r) => F.OnBelts(r.OutStart, r.OutDir, 3);

        private static ItemDef Item(string id) => ItemCatalog.Find(id);

        private static ushort Accept(CampaignState s, Rig r, string portKey)
        {
            BeltPortService.Binding bind = BeltPortService.Find(r.B.BuildingId, portKey);
            return bind != null && BeltNetworkService.Kernel.TryGetPortInfo(bind.PortId, out BeltPortInfo i) ? i.Accept : (ushort)0xEEEE;
        }

        private static void Seen(ProductionService.Producer p)
        {
            if (p != null)
            {
                SeenStates.Add(p.Def.TypeId + ":" + p.State);
            }
        }

        private static void SeenAll()
        {
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                Seen(p);
            }
        }

        private static bool Until(Func<bool> done, int maxGameSeconds)
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

        private static void Run(float seconds)
        {
            int total = Mathf.RoundToInt(GameClock.StepHz * seconds);
            int quarter = Math.Max(1, GameClock.StepHz / 4);
            while (total > 0)
            {
                int n = Math.Min(quarter, total);
                WorldSimulation.StepMany(n);
                total -= n;
                SeenAll();
            }
        }

        private static string Why(CampaignState s, ProductionService.Producer p) => F.Reason(s, p);

        private static int Ground(CampaignState s, string itemId) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == ItemCatalog.ResourceTypeOf(itemId)).Sum(g => g.Amount);

        private static int Stock(CampaignState s, string itemId) => HomeInventory.Stock(s, itemId);

        private static void Put(CampaignState s, string itemId, int amount)
        {
            int got = HomeInventory.Add(s, itemId, amount);
            if (got != amount)
            {
                Fail($"夹具：仓库放不下 {amount} 件 {itemId}（只放进 {got}）");
            }
        }

        private static void PutAll(CampaignState s, ItemStackRecord[] mats)
        {
            foreach (ItemStackRecord m in mats)
            {
                Put(s, m.ItemId, m.Amount);
            }
        }

        private static BlueprintVersionRecord ActiveVersion(CampaignState s, string blueprintId)
        {
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            BlueprintRecord bp = s.BlueprintRecords.First(b => b.BlueprintId == blueprintId);
            return bp.Versions.First(v => v.Version == bp.ActiveVersion);
        }

        /// <summary>物品清单的规范写法（null 与空数组相同：存档往返后两者不分）。</summary>
        private static string Stacks(ItemStackRecord[] a) =>
            a == null ? string.Empty : string.Join(",", a.Where(x => x != null && x.Amount > 0).OrderBy(x => x.ItemId, StringComparer.Ordinal).Select(x => x.ItemId + "=" + x.Amount));

        private static FactoryQueueItemRecord Q(CampaignState s, string id) => HomeValleyFactory.Find(s, id);

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            (string id, int w, int h, int inPorts, int outPorts)[] want =
            {
                ("parts_workshop", 3, 3, 1, 1), ("electronics_bench", 3, 3, 2, 1), ("component_workshop", 4, 4, 2, 1), ("firmware_burner", 3, 3, 1, 0),
            };
            bool grid = true;
            foreach ((string id, int w, int h, int inPorts, int outPorts) in want)
            {
                int ins = GridContent.PortsOf(id).Count(p => p.Role == "prod" && p.Kind == "in");
                int outs = GridContent.PortsOf(id).Count(p => p.Role == "prod" && p.Kind == "out");
                grid &= GridContent.TryGetBuilding(id, out BuildingGrid g) && g.FootprintW == w && g.FootprintH == h && g.Category == "manufacturing" && g.Placeable == 1
                        && FgContentTables.TryGetBuilding(id, out Building b) && b.PowerDemand > 0 && b.BuildScrap > 0 && b.BuildSeconds > 0
                        && ProducerCatalog.TryGet(id, out ProducerDef d) && d.Mode == ProducerMode.Recipe && ins == inPorts && outs == outPorts
                        && BuildCatalog.TryGet(id, out BuildEntry e) && e.CategoryId == "manufacturing";
            }
            Expect(grid, "A1 四座制造建筑入表（FG04 第 3.3 节占地：零件工坊 / 电子组装台 / 固件刻录台 3×3、组件工坊 4×4）：建造菜单“制造”页签、耗电、造价；电子组装台与组件工坊两个输入口（两种材料各走一个口），固件刻录台没有物品输出口");
            bool recipes = ProducerCatalog.TryGet("parts_workshop", out ProducerDef pw) && pw.Recipes.Select(r => r.Id).SequenceEqual(new[] { "part", "structural", "repair_kit" }) && pw.FixedRecipe == null
                           && ProducerCatalog.TryGet("electronics_bench", out ProducerDef eb) && eb.Recipes.Select(r => r.Id).SequenceEqual(new[] { "electronic", "precision_part", "chip_substrate" })
                           && ProducerCatalog.TryGet("component_workshop", out ProducerDef cw) && cw.Recipes.Select(r => r.Id).SequenceEqual(new[] { "combat_component", "structure_module" })
                           && ProducerCatalog.TryGet("firmware_burner", out ProducerDef fb) && fb.FixedRecipe?.Id == "firmware_chip" && fb.FixedRecipe.Kind == ProductionService.RecipeKindFirmware
                           && ProducerCatalog.Problems.Count == 0
                           && GridContent.PortsOf("assembly_station").Any(p => p.Id == "assembly_station.in0" && p.Role == "prod");
            Expect(recipes, $"A1 生产参数：零件工坊 3 条、电子组装台 3 条、组件工坊 2 条配方（都要玩家选）；固件刻录台的固定配方是 firmware 种类；装配站输入口改为 role = prod（DEBT-FG3LOG03-01）；表检查 0 个问题（{string.Join("；", ProducerCatalog.Problems)}）");

            // 机器材料表：源数据与运行时逐字段一致；默认蓝图的材料清单。
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("AM\t", StringComparison.Ordinal)).ToArray();
            string[] rt = ConfigSystem.Instance.Tables.TbAssemblyMaterial.DataList.Select(r => string.Join("\t", "AM", r.Id, r.Part, r.Item, r.Amount)).ToArray();
            Expect(code == 0 && src.Length >= 8 && src.SequenceEqual(rt) && AssemblyMaterials.Problems.Count == 0,
                $"A2 fg.TbAssemblyMaterial（{src.Length} 行）与源数据 fgdata_manufacturing.py 逐字段一致，运行时 0 个问题" + (code == 0 ? string.Empty : "：" + F.Tail(output)));
            CampaignState s = F.NewWorld(9601);
            BlueprintVersionRecord erc = ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id);
            BlueprintVersionRecord hauler = ActiveVersion(s, HomeValleyLayout.BlueprintHaulerId);
            string ercMats = Stacks(AssemblyMaterials.For(erc));
            string haulMats = Stacks(AssemblyMaterials.For(hauler));
            // ERC-003 = 履带底盘（结构材 4、零件 4）+ 控制电路（电子件 2）+ 主组件、功能组件各一个作战组件；搬运机 = 轮式底盘（结构材 2、零件 3）+ 电子件 2 + 主组件一个作战组件。
            bool mats = ercMats == "combat_component=2,electronic=2,part=4,structural=4" && haulMats == "combat_component=1,electronic=2,part=3,structural=2"
                        && AssemblyMaterials.MaterialItems.Count == 5 && AssemblyMaterials.BeltMask != 0
                        && AssemblyMaterials.MaterialItems.All(i => (AssemblyMaterials.BeltMask & (1UL << i.BeltId)) != 0) && (AssemblyMaterials.BeltMask & (1UL << Item("metal_ore").BeltId)) == 0;
            Expect(mats, $"A2 蓝图 → 材料（DEBT-FG4ECO01-01）：ERC-003 {ercMats}；搬运机 {haulMats}；装配站输入口的收货集合 = 5 种机器材料，不含金属矿");
            // 废料当量（与 fgdata_eco 回收产出同一算法）与代付折算。
            bool eq = Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("alloy")) - 4) < 1e-6 && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("part")) - 2) < 1e-6
                      && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("structural")) - 8) < 1e-6 && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("electronic")) - 5) < 1e-6
                      && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("combat_component")) - 9) < 1e-6 && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("structure_module")) - 18) < 1e-6
                      && Math.Abs(AssemblyMaterials.ScrapEquivalent(Item("chip_substrate")) - 11) < 1e-6;
            ItemStackRecord[] ercAll = AssemblyMaterials.For(erc);
            int all = AssemblyMaterials.SubstituteScrap(ercAll, ercAll, erc.ScrapCost);
            int part = AssemblyMaterials.SubstituteScrap(ercAll, new[] { new ItemStackRecord { ItemId = "combat_component", Amount = 2 } }, erc.ScrapCost);
            // 总当量 = 4×8 + 4×2 + 2×5 + 2×9 = 68；缺 2 个作战组件 = 18 → ⌈60 × 18 / 68⌉ = 16。
            Expect(eq && all == erc.ScrapCost && part == 16 && AssemblyMaterials.SubstituteScrap(ercAll, Array.Empty<ItemStackRecord>(), erc.ScrapCost) == 0,
                $"A3 废料当量：合金 4、零件 2、结构材 8、电子件 5、作战组件 9、结构模块 18、芯片基板 11；ERC-003 全部代付 = {all}（= 蓝图废料价 {erc.ScrapCost}），只缺 2 个作战组件代付 {part}（⌈60×18/68⌉ = 16），不缺 = 0");
            AssemblyMaterials.Data bad = AssemblyMaterials.Build(new List<AssemblyMaterials.MaterialRow>
            {
                new AssemblyMaterials.MaterialRow { Id = "x1", Part = "slot.wing", Item = "part", Amount = 1 },
                new AssemblyMaterials.MaterialRow { Id = "x2", Part = "base", Item = "water", Amount = 1 },
                new AssemblyMaterials.MaterialRow { Id = "x3", Part = "base", Item = "part", Amount = 0 },
                new AssemblyMaterials.MaterialRow { Id = "x4", Part = "chassis.track", Item = "structural", Amount = 2 },
            });
            Expect(bad.Problems.Count == 3 && bad.Items.Count == 1 && bad.Problems.Any(p => p.Contains("slot.wing")) && bad.Problems.Any(p => p.Contains("water")) && bad.Problems.Any(p => p.Contains("至少 1")),
                $"A4 机器材料表负向：部位拼错、流体当材料、数量 0——整行拒绝并写明原因，合法行照常（{string.Join("；", bad.Problems)}）");
            string[] keys =
            {
                "building.parts_workshop.desc", "building.firmware_burner.desc", "prod.reason.no_burn_target", "prod.reason.firmware_storage_full", "prod.reason.in_port_unconnected_at",
                "prod.panel.copy", "prod.panel.remembered", "prod.panel.inherited", "asm.reason.materials", "asm.reason.materials_scrap", "asm.reason.route", "asm.panel.sub_toggle",
                "asm.port.accept", "logistics.block.sink_rejects_set", "codex.economy.manufacturing.body", "codex.economy.assembly.body", "fwlib.origin.burn",
            };
            bool texts = keys.All(k => GameText.Has(k));
            GameSettings.SetLanguage(GameLanguage.En);
            texts &= keys.All(k => GameText.Has(k) && !GameText.Get(k).Contains("⟦"));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool codex = MechanicCodex.Find("codex.economy.manufacturing") != null && MechanicCodex.Find("codex.economy.assembly") != null
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyManufacturingFirstPlaced) && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyBurnerFirstChip)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyAssemblyFirstLineMachine) && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyFirstCopyToSameType);
            // FG5-RND-04：熔合接口已由 FusionService 实现并接上（未发现的混合固件刻录台不能刻，见 FgFusionSelfCheck E 段）。
            FusionService.Bind();
            Expect(texts && codex && FusionHooks.Available && !FusionHooks.IsBurnableFusion(CampaignSession.Current, "fw_mix_napalm") && FusionHooks.Service.BurnSubstrateCost("fw_mix_napalm") == 3,
                "A5 文本中英两套；图鉴“制造建筑”“装配站”两条系统说明；4 个引导钩子登记；熔合接口已接上（FG5-RND-04：未发现的混合固件不能量产，发现后每枚芯片基板 3）");
        }

        // ── B 零件工坊 ─────────────────────────────────────────────────────────────

        private static void CheckPartsWorkshop()
        {
            CampaignState s = World(9611);
            Rig r = Lay(s, "parts_workshop", "pw");
            if (r == null)
            {
                Fail("B 找不到 12×12 的空地或铺不了传送带");
                return;
            }
            ProductionService.Producer p = P(s, r);
            Run(1f);
            bool idle = p.State == ProdState.Idle && Why(s, p).Contains("没选配方") && Accept(s, r, "parts_workshop.in0") == BeltConst.AcceptNone
                        && GuidanceHooks.Known.Contains(GuidanceHooks.EconomyManufacturingFirstPlaced) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyManufacturingFirstPlaced);
            Expect(idle, $"B1 零件工坊没选配方 = “{ProductionService.StateText(p)}”（“{Why(s, p)}”），输入口什么都不收；第一次放下制造建筑发引导钩子");
            bool set = ProductionService.TrySetRecipe(s, r.B.BuildingId, "part", out string msg);
            int src = Feed(s, r, "parts_workshop.in0", "alloy", 20);
            Run(45f);
            int pushed = SourceTotal(src);
            int eaten = (int)p.Rec.Completed + (p.Rec.Running ? 1 : 0);
            int transit = InBelts(r, "parts_workshop.in0") + ProductionService.Count(p.Rec.In, "alloy") + F.PortPending(s, r.B, "parts_workshop.in0");
            int made = 2 * (int)p.Rec.Completed;
            int here = ProductionService.Count(p.Rec.Out, "part") + F.PortPending(s, r.B, "parts_workshop.out0") + OutBelts(r) + SinkTotal(r.Sink);
            Expect(set && Accept(s, r, "parts_workshop.in0") == Item("alloy").BeltId && p.Rec.Completed >= 10 && pushed == eaten + transit && made == here && SinkTotal(r.Sink) > 0,
                $"B2 选“零件”后输入口只收合金，45 游戏秒做了 {p.Rec.Completed} 份（1 合金 → 2 零件）；账目守恒：推上带的合金 {pushed} = 吃掉 {eaten} + 在途 {transit}；零件 {made} = 缓存 / 待推 / 带上 / 下游 {here}");
            // 输出口堵塞（负向）：输出带是死路。
            Rig dead = null;
            CampaignState s2 = World(9612);
            dead = Lay(s2, "parts_workshop", "pwd", sink: false);
            if (dead == null)
            {
                Fail("B3 场景放不下");
                return;
            }
            ProductionService.Producer pd = P(s2, dead);
            ProductionService.TrySetRecipe(s2, dead.B.BuildingId, "part", out _);
            Feed(s2, dead, "parts_workshop.in0", "alloy", 30);
            bool blocked = Until(() => pd.State == ProdState.OutputBlocked, 120);
            DiagChain outChain = RootCauseDiagnosis.DiagnoseBuilding(s2, dead.B)?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Output);
            Expect(blocked && Why(s2, pd).Contains("推不动") && outChain != null && outChain.Steps[0].Code == DiagCode.ProdBlocked,
                $"B3 负向“输出口堵塞”：输出带是死路，带满后零件工坊“{ProductionService.StateText(pd)}”（“{Why(s2, pd)}”）；“为什么不工作”：{(outChain == null ? "（没有）" : RootCauseDiagnosis.ChainText(outChain))}");
            // 换配方：结构材也用合金（缓存不退）；维修件不用合金（缓存与输入口里的合金退回仓库）。
            int alloyIn = ProductionService.Count(pd.Rec.In, "alloy") + (pd.Rec.Running ? 1 : 0) + F.PortPending(s2, dead.B, "parts_workshop.in0");
            bool keep = ProductionService.TrySetRecipe(s2, dead.B.BuildingId, "structural", out string m1) && !m1.Contains("退回仓库") && Accept(s2, dead, "parts_workshop.in0") == Item("alloy").BeltId;
            int stock0 = Stock(s2, "alloy") + Ground(s2, "alloy");
            int alloyIn2 = ProductionService.Count(pd.Rec.In, "alloy") + F.PortPending(s2, dead.B, "parts_workshop.in0");
            bool back = ProductionService.TrySetRecipe(s2, dead.B.BuildingId, "repair_kit", out string m2) && Accept(s2, dead, "parts_workshop.in0") == Item("part").BeltId
                        && Stock(s2, "alloy") + Ground(s2, "alloy") - stock0 == alloyIn2 && ProductionService.Count(pd.Rec.In, "alloy") == 0 && m2.Contains("退回仓库");
            Expect(alloyIn > 0 && keep && back,
                $"B4 换配方：改“结构材”同样用合金，缓存不动（“{m1}”）；改“维修件”后缓存与输入口里的 {alloyIn2} 份合金退回仓库，输入口改收零件（“{m2.Replace("\n", " ")}”）");
        }

        // ── C 电子组装台（两个输入口）─────────────────────────────────────────────────────

        private static void CheckElectronicsBench()
        {
            CampaignState s = World(9621);
            Rig r = Lay(s, "electronics_bench", "eb");
            if (r == null)
            {
                Fail("C 场景放不下");
                return;
            }
            ProductionService.Producer p = P(s, r);
            bool none = Accept(s, r, "electronics_bench.in0") == BeltConst.AcceptNone && Accept(s, r, "electronics_bench.in1") == BeltConst.AcceptNone;
            ProductionService.TrySetRecipe(s, r.B.BuildingId, "electronic", out _);
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, r.B, views);
            string v0 = views.FirstOrDefault(v => v.PortKey == "electronics_bench.in0")?.AcceptLine ?? string.Empty;
            string v1 = views.FirstOrDefault(v => v.PortKey == "electronics_bench.in1")?.AcceptLine ?? string.Empty;
            bool split = Accept(s, r, "electronics_bench.in0") == Item("alloy").BeltId && Accept(s, r, "electronics_bench.in1") == Item("rare_earth_ore").BeltId
                         && v0.Contains("合金") && v1.Contains("稀土矿");
            Expect(none && split, $"C1 两个输入口按配方分配：没选配方时都不收；选“电子件”后西口只收合金（“{v0}”）、南口只收稀土矿（“{v1}”）");
            // 只送合金：缺稀土矿，原因写明是南边那个口。
            int a = Feed(s, r, "electronics_bench.in0", "alloy", 8);
            bool starved = Until(() => p.State == ProdState.MissingInput && p.ReasonItem?.Id == "rare_earth_ore", 30);
            string why = Why(s, p);
            Expect(starved && why.Contains("稀土矿") && why.Contains("南输入口"), $"C2 只送来合金：“{ProductionService.StateText(p)}”（“{why.Replace("\n", " · ")}”）——缺第二种材料时写明是哪个方向的口");
            // 送错物品：南口送合金 → 带停在末端，原因写明只收稀土矿。
            int wrong = F.TestSource(s, r.InFar["electronics_bench.in1"], "alloy", 1);
            GridCell nearS = r.InNear["electronics_bench.in1"];
            bool rejected = Until(() => BeltNetworkService.Kernel.TryGetCellInfo(nearS.X, nearS.Y, out BeltCellInfo c) && c.Block == BeltBlock.SinkRejects, 20);
            BeltNetworkService.Kernel.TryGetCellInfo(nearS.X, nearS.Y, out BeltCellInfo rc);
            string rejText = BeltNetworkService.DescribeBlock(rc);
            Expect(rejected && rejText.Contains("稀土矿"), $"C3 南口送来合金：带停在末端（“{rejText}”），物品不消失");
            // 诊断：缺稀土矿时“为什么不工作”顺着南口的来料带找供货方（找不到 → 写明在上游接一座产出稀土矿的建筑）。
            DiagChain inChain = RootCauseDiagnosis.DiagnoseBuilding(s, r.B)?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Input);
            Expect(inChain != null && inChain.Steps[0].Code == DiagCode.ProdStarved && inChain.Root.Text.Contains("稀土矿"),
                $"C4 “为什么不工作”追收稀土矿的那个口（南口）：{(inChain == null ? "（没有）" : RootCauseDiagnosis.ChainText(inChain))}");
            // 清掉堵带的那件，再送稀土矿：开工，账目守恒。
            BeltNetworkService.Kernel.RemovePort(wrong);
            BeltClearService.Execute(s, BeltClearService.PlanNetwork(s, nearS), true, out _);
            int b = Feed(s, r, "electronics_bench.in1", "rare_earth_ore", 6);
            Run(40f);
            int doneN = (int)p.Rec.Completed;
            int alloyAcc = SourceTotal(a) - doneN - (p.Rec.Running ? 1 : 0) - ProductionService.Count(p.Rec.In, "alloy") - InBelts(r, "electronics_bench.in0")
                           - F.PortPending(s, r.B, "electronics_bench.in0");
            int made = ProductionService.Count(p.Rec.Out, "electronic") + F.PortPending(s, r.B, "electronics_bench.out0") + OutBelts(r) + SinkTotal(r.Sink);
            Expect(doneN >= 6 && alloyAcc == 0 && made == doneN && SourceTotal(b) == 6,
                $"C5 两种材料都到齐后开工：做了 {doneN} 份电子件；合金账目差 {alloyAcc}（应为 0），电子件 {made} = 完成 {doneN}");
            // 换配方“精密零件”：西口改收零件、南口改收电子件；缓存里的合金 / 稀土矿退回仓库。
            Feed(s, r, "electronics_bench.in0", "alloy", 3);
            Run(3f);
            int before = Stock(s, "alloy") + Ground(s, "alloy") + Stock(s, "rare_earth_ore") + Ground(s, "rare_earth_ore");
            int buffered = ProductionService.Count(p.Rec.In, "alloy") + ProductionService.Count(p.Rec.In, "rare_earth_ore") + (p.Rec.Running ? 2 : 0)
                           + F.PortPending(s, r.B, "electronics_bench.in0") + F.PortPending(s, r.B, "electronics_bench.in1");
            bool sw = ProductionService.TrySetRecipe(s, r.B.BuildingId, "precision_part", out string m);
            int after = Stock(s, "alloy") + Ground(s, "alloy") + Stock(s, "rare_earth_ore") + Ground(s, "rare_earth_ore");
            Expect(sw && Accept(s, r, "electronics_bench.in0") == Item("part").BeltId && Accept(s, r, "electronics_bench.in1") == Item("electronic").BeltId && after - before == buffered,
                $"C6 换“精密零件”：西口收零件、南口收电子件；缓存、正在做的那份与输入口里的 {buffered} 件合金 / 稀土矿退回仓库（仓库 / 地上 +{after - before}）");
        }

        // ── D 组件工坊 ─────────────────────────────────────────────────────────────

        private static void CheckComponentWorkshop()
        {
            CampaignState s = World(9631);
            Rig r = Lay(s, "component_workshop", "cw");
            if (r == null)
            {
                Fail("D 场景放不下");
                return;
            }
            ProductionService.Producer p = P(s, r);
            Run(0.5f);
            ProductionService.TrySetRecipe(s, r.B.BuildingId, "combat_component", out _);
            int a = Feed(s, r, "component_workshop.in0", "part", 8);
            int b = Feed(s, r, "component_workshop.in1", "electronic", 4);
            Run(40f);
            int combat = ProductionService.Count(p.Rec.Out, "combat_component") + F.PortPending(s, r.B, "component_workshop.out0") + OutBelts(r) + SinkTotal(r.Sink);
            bool ok1 = p.Rec.Completed == 4 && combat == 4 && SourceTotal(a) == 8 && SourceTotal(b) == 4 && ProductionService.Count(p.Rec.In, "part") == 0;
            ProductionService.TrySetRecipe(s, r.B.BuildingId, "structure_module", out _);
            long done0 = p.Rec.Completed;
            Feed(s, r, "component_workshop.in0", "structural", 4);
            Feed(s, r, "component_workshop.in1", "part", 2);
            Run(20f);
            Expect(ok1 && p.Rec.Completed - done0 == 2 && Accept(s, r, "component_workshop.in0") == Item("structural").BeltId,
                $"D1 组件工坊：8 零件 + 4 电子件 → {combat} 个作战组件（每个 2 零件 + 1 电子件）；改“结构模块”后 4 结构材 + 2 零件 → {p.Rec.Completed - done0} 个结构模块");
        }

        // ── E 固件刻录台 ───────────────────────────────────────────────────────────

        private static void CheckFirmwareBurner()
        {
            CampaignState s = World(9641);
            Rig r = Lay(s, "firmware_burner", "fb");
            if (r == null)
            {
                Fail("E 场景放不下");
                return;
            }
            ProductionService.Producer p = P(s, r);
            List<string> burnable = SignalCoreService.PrintableFirmware(s);
            string locked = FirmwareCatalog.All.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(k => !MechanicalContentUnlock.IsUnlocked(s, k));
            if (burnable.Count == 0 || locked == null)
            {
                Fail($"E 没有已破解 / 未破解的固件可测（可刻 {burnable.Count} 条）");
                return;
            }
            Run(1f);
            bool idle = p.State == ProdState.Idle && Why(s, p).Contains("没选要刻录的固件") && Accept(s, r, "firmware_burner.in0") == Item("chip_substrate").BeltId;
            bool lockedRefused = !ProductionService.TrySetBurnTarget(s, r.B.BuildingId, locked, out string lm) && lm.Contains("还没有破解") && string.IsNullOrEmpty(p.Rec.BurnTarget);
            bool unknownRefused = !ProductionService.TrySetBurnTarget(s, r.B.BuildingId, "scrap", out string um) && um.Contains("不是可以刻录的固件");
            Expect(idle && lockedRefused && unknownRefused,
                $"E1 刻录台没选固件 = “{ProductionService.StateText(p)}”（“{Why(s, p)}”），输入口收芯片基板；选未破解的“{FirmwareKinds.DisplayName(locked)}”被拒（“{lm}”）、选非固件被拒（“{um}”）");
            string target = burnable[0];
            int chips0 = s.PrimitiveChips?.Length ?? 0;
            bool set = ProductionService.TrySetBurnTarget(s, r.B.BuildingId, target, out string sm);
            int src = Feed(s, r, "firmware_burner.in0", "chip_substrate", 3);
            bool made = Until(() => p.Rec.Completed >= 3, 120);
            PrimitiveChipRecord[] burned = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Where(c => c.Origin == PrimitiveInventory.OriginBurn).ToArray();
            Expect(set && made && burned.Length == 3 && burned.All(c => c.CardDefId == target && c.State == PrimitiveChipState.Bag) && (s.PrimitiveChips.Length - chips0) == 3
                   && SourceTotal(src) == 3 && ProductionService.Count(p.Rec.In, "chip_substrate") == 0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyBurnerFirstChip)
                   && FirmwareLibrary.OriginText(PrimitiveInventory.OriginBurn).Contains("刻录台"),
                $"E2 选“{FirmwareKinds.DisplayName(target)}”（“{sm}”），3 块芯片基板刻出 {burned.Length} 枚芯片进固件库（来源“{FirmwareLibrary.OriginText(PrimitiveInventory.OriginBurn)}”，在存放里），基板全部用掉；第一枚发引导钩子");
            // 负向：固件存放满了 → 输出堵塞，不扣基板；腾出一格后恢复。
            int cap = PrimitiveInventory.CapacityOf(s);
            var fill = new List<PrimitiveChipRecord>();
            while (PrimitiveInventory.BagCount(s) < cap)
            {
                var c = new PrimitiveChipRecord { PartId = "fill_" + fill.Count, CardDefId = target, State = PrimitiveChipState.Bag, DraftSlot = -1, Origin = PrimitiveInventory.OriginSeed };
                fill.Add(c);
                s.PrimitiveChips = s.PrimitiveChips.Append(c).ToArray();
            }
            Feed(s, r, "firmware_burner.in0", "chip_substrate", 2);
            bool full = Until(() => p.State == ProdState.OutputBlocked && p.Reason == ProdReason.FirmwareStorageFull, 40);
            int inBuf = ProductionService.Count(p.Rec.In, "chip_substrate");
            string fullWhy = Why(s, p);
            Expect(full && fullWhy.Contains("存放已满") && fullWhy.Contains(cap.ToString()) && inBuf >= 1 && p.Rec.Completed == 3,
                $"E3 负向：固件芯片存放满了（{cap} / {cap}）→ “{ProductionService.StateText(p)}”（“{fullWhy}”），基板留在缓存里（{inBuf}）不扣");
            s.PrimitiveChips = s.PrimitiveChips.Where(c => c != fill[0]).ToArray();
            bool resumed = Until(() => p.Rec.Completed == 4, 40);
            Expect(resumed, $"E4 腾出一格后自动恢复，刻出第 4 枚（{p.Rec.Completed}）");
            // 中途换目标：正在刻的那份作废、基板退回输入缓存。
            s.PrimitiveChips = s.PrimitiveChips.Where(c => !c.PartId.StartsWith("fill_", StringComparison.Ordinal)).ToArray();
            Feed(s, r, "firmware_burner.in0", "chip_substrate", 1);
            Until(() => p.Rec.Running, 20);
            int bufBefore = ProductionService.Count(p.Rec.In, "chip_substrate");
            string other = burnable.Count > 1 ? burnable[1] : target;
            bool changed = ProductionService.TrySetBurnTarget(s, r.B.BuildingId, other == target ? string.Empty : other, out string cm);
            bool voided = !p.Rec.Running && ProductionService.Count(p.Rec.In, "chip_substrate") == bufBefore + 1;
            Expect(changed && voided, $"E5 刻到一半换目标（“{cm}”）：正在刻的那份作废，芯片基板退回输入缓存（{bufBefore} → {ProductionService.Count(p.Rec.In, "chip_substrate")}），不丢料");
            CheckTwoBurnersOneSlot(target);
        }

        /// <summary>审查 P2：两座刻录台、芯片存放只剩 1 格——只有一座开工，另一座“存放已满”停下、基板不扣；刻好的芯片不进“待领取”。</summary>
        private static void CheckTwoBurnersOneSlot(string target)
        {
            CampaignState s = World(9642);
            Rig a = Lay(s, "firmware_burner", "fba");
            Rig b = Lay(s, "firmware_burner", "fbb");
            if (a == null || b == null)
            {
                Fail("E6 场景放不下两座刻录台");
                return;
            }
            ProductionService.Producer pa = P(s, a);
            ProductionService.Producer pb = P(s, b);
            bool set = ProductionService.TrySetBurnTarget(s, a.B.BuildingId, target, out _) & ProductionService.TrySetBurnTarget(s, b.B.BuildingId, target, out _);
            int cap = PrimitiveInventory.CapacityOf(s);
            int k = 0;
            while (PrimitiveInventory.BagCount(s) < cap - 1)
            {
                s.PrimitiveChips = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Append(new PrimitiveChipRecord
                {
                    PartId = "fill2_" + k++, CardDefId = target, State = PrimitiveChipState.Bag, DraftSlot = -1, Origin = PrimitiveInventory.OriginSeed,
                }).ToArray();
            }
            Feed(s, a, "firmware_burner.in0", "chip_substrate", 1);
            Feed(s, b, "firmware_burner.in0", "chip_substrate", 1);
            Until(() => pa.Rec.Completed + pb.Rec.Completed >= 1 && !pa.Rec.Running && !pb.Rec.Running
                        && ProductionService.Count(pa.Rec.In, "chip_substrate") + ProductionService.Count(pb.Rec.In, "chip_substrate") >= 1, 60);
            Run(10f);
            PrimitiveChipRecord[] burned = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Where(c => c.Origin == PrimitiveInventory.OriginBurn).ToArray();
            ProductionService.Producer blocked = pa.Rec.Completed == 0 ? pa : pb;
            bool ok = set && pa.Rec.Completed + pb.Rec.Completed == 1 && burned.Length == 1 && burned.All(c => c.State == PrimitiveChipState.Bag)
                      && PrimitiveInventory.BagCount(s) == cap && blocked.State == ProdState.OutputBlocked && blocked.Reason == ProdReason.FirmwareStorageFull
                      && ProductionService.Count(blocked.Rec.In, "chip_substrate") == 1;
            Expect(ok, $"E6 两座刻录台、芯片存放只剩 1 格：只刻出 {burned.Length} 枚（{string.Join("/", burned.Select(c => c.State))}），存放 {PrimitiveInventory.BagCount(s)} / {cap}，"
                       + $"另一座“{ProductionService.StateText(blocked)}”、基板留在缓存（{ProductionService.Count(blocked.Rec.In, "chip_substrate")}）——不会把芯片挤进“待领取”");
        }

        // ── F FGT-ECO-001：从原料到机器 ──────────────────────────────────────────────

        /// <summary>
        /// 分段跑真实建筑与传送带：每段的输入 = 上一段下游测试收货口实际收到的件数（重新放上这一段的来料带，代替“一条把它们运过去的传送带”）；最后一段在装配站
        /// （一部分材料走装配站的真实输入口，其余放进仓库），关掉废料代付，造出一台搬运机。每段账目守恒，最终机器只用产线材料（MachinesFromLine）。
        /// 一条图上完整布线的旅程由 FG4-E2E-01（FGJ-M4）承接。
        /// </summary>
        private static void CheckRawToMachine()
        {
            // 搬运机 = 结构材 2、零件 3、电子件 2、作战组件 1。作战组件 = 2 零件 + 1 电子件 → 零件共 5（3 份）、电子件共 3。
            // 合金：结构材 2 × 2 = 4、零件 3 份 × 1 = 3、电子件 3 × 1 = 3 → 10 合金 = 20 金属矿；稀土矿 3。
            var produced = new Dictionary<string, int>();
            int StageRun(int seed, string typeId, string recipe, (string port, string item, int n)[] feeds, string outItem, int expectOut, float seconds)
            {
                CampaignState st = World(seed);
                Rig rig = Lay(st, typeId, "fg" + seed);
                if (rig == null)
                {
                    Fail($"F 段 {typeId} 放不下（种子 {seed}）");
                    return -1;
                }
                ProductionService.TrySetRecipe(st, rig.B.BuildingId, recipe, out _);
                var srcs = feeds.Select(f => (f.item, f.n, id: Feed(st, rig, f.port, f.item, f.n))).ToList();
                Until(() => SinkTotal(rig.Sink) >= expectOut, (int)seconds);
                Run(2f);
                int got = SinkTotal(rig.Sink);
                ProductionService.Producer pr = P(st, rig);
                bool consumed = srcs.All(x => SourceTotal(x.id) == x.n) && ProductionService.Total(pr.Rec.In) == 0 && !pr.Rec.Running;
                Expect(got == expectOut && consumed,
                    $"F 段 {HomeGridService.DisplayName(typeId)}（{(ItemCatalog.TryGetRecipe(recipe, out RecipeDef rd) ? rd.Name : recipe)}）：投入 {string.Join("、", feeds.Select(f => ItemCatalog.NameOf(f.item) + " ×" + f.n))} 全部用掉，下游收到 {ItemCatalog.NameOf(outItem)} ×{got}（应为 {expectOut}）");
                produced[outItem] = (produced.TryGetValue(outItem, out int prev) ? prev : 0) + got;
                return got;
            }
            // 冶炼：20 金属矿 → 10 合金（精炼炉，FG4-ECO-02）。
            int alloy = StageRun(9651, "refinery_furnace", "alloy_ore", new[] { ("refinery_furnace.in0", "metal_ore", 20) }, "alloy", 10, 80);
            // 零件工坊：4 合金 → 2 结构材；3 合金 → 6 零件（其中 1 份多余留着，作战组件用 2、机器用 3）。
            int structural = StageRun(9652, "parts_workshop", "structural", new[] { ("parts_workshop.in0", "alloy", 4) }, "structural", 2, 30);
            int parts = StageRun(9653, "parts_workshop", "part", new[] { ("parts_workshop.in0", "alloy", 3) }, "part", 6, 30);
            // 电子组装台：3 合金 + 3 稀土矿 → 3 电子件。
            int elec = StageRun(9654, "electronics_bench", "electronic", new[] { ("electronics_bench.in0", "alloy", 3), ("electronics_bench.in1", "rare_earth_ore", 3) }, "electronic", 3, 40);
            // 组件工坊：2 零件 + 1 电子件 → 1 作战组件。
            int combat = StageRun(9655, "component_workshop", "combat_component", new[] { ("component_workshop.in0", "part", 2), ("component_workshop.in1", "electronic", 1) }, "combat_component", 1, 30);
            if (alloy != 10 || structural != 2 || parts != 6 || elec != 3 || combat != 1)
            {
                Fail("F 前面的段没有产出预期的数量，装配段不跑");
                return;
            }
            // 装配：结构材 2 走装配站的真实输入口（传送带），零件 3、电子件 2、作战组件 1 在仓库；废料代付关着。
            CampaignState s = World(9656, scrap: 500);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            BuildingRecord station = AssemblyMaterials.Station(s);
            BeltPortService.Binding asmIn = BeltPortService.Find(station.BuildingId, "assembly_station.in0");
            if (asmIn == null || !LayIntoPort(s, asmIn, out GridCell far))
            {
                Fail("F 装配站西侧输入口外铺不了带（本种子）");
                return;
            }
            int beltSrc = F.TestSource(s, far, "structural", structural);
            Put(s, "part", parts - 2 - 1); // 6 零件：2 个做了作战组件；多出 1 个不放进来（机器只要 3）
            Put(s, "electronic", elec - 1);
            Put(s, "combat_component", combat);
            Until(() => AssemblyMaterials.InBuffer(s, "structural") == 2, 20);
            int scrap0 = s.Scrap;
            long fromLine0 = s.Economy.MachinesFromLine;
            int haulers0 = MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc002ChassisId);
            HomeValleyFactory.FactoryOpResult q = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHaulerId);
            bool spawned = Until(() => Q(s, q.QueueItemId)?.State == FactoryQueueState.Completed, 60);
            FactoryQueueItemRecord item = Q(s, q.QueueItemId);
            bool conserved = s.Scrap == scrap0 && item.SubstituteScrap == 0 && Stacks(item.Taken) == "combat_component=1,electronic=2,part=3,structural=2"
                             && Stock(s, "part") == 0 && Stock(s, "electronic") == 0 && Stock(s, "combat_component") == 0 && AssemblyMaterials.InBuffer(s, "structural") == 0
                             && SourceTotal(beltSrc) == 2;
            Line($"    · F 装配段：队列项 {item?.State}（{HomeValleyFactory.DescribeWait(item) ?? item?.BlockedReason}），缓存结构材 {AssemblyMaterials.InBuffer(s, "structural")}");
            Expect(q.Success && spawned && conserved && s.Economy.MachinesFromLine == fromLine0 + 1
                   && MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc002ChassisId) == haulers0 + 1
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyAssemblyFirstLineMachine),
                $"F FGT-ECO-001：20 金属矿 + 3 稀土矿 → 10 合金 → 2 结构材 / 6 零件 / 3 电子件 → 1 作战组件 → 装配站（结构材走传送带进材料缓存，其余在仓库）造出 1 台搬运机；" +
                $"取料 {Stacks(item.Taken)}，废料不动（{scrap0} → {s.Scrap}），代付 0；“用产线材料造出的机器”计数 +1，引导钩子");
        }

        /// <summary>在装配站输入口外铺 3 格朝着它的传送带（按端口朝向），返回最远那一格（测试来料口挂在这里）。</summary>
        private static bool LayIntoPort(CampaignState s, BeltPortService.Binding bind, out GridCell far)
        {
            Vector2Int v = GridMath.DirVector(bind.Face);
            var dir = (BeltDir)(((int)bind.Face + 2) & 3);
            far = bind.BeltCell;
            for (int i = 0; i < 3; i++)
            {
                var c = new GridCell(bind.BeltCell.X + v.x * i, bind.BeltCell.Y + v.y * i);
                if (!BeltNetworkService.TryPlace(s, c, dir, 2).Ok)
                {
                    return i >= 1;
                }
                far = c;
            }
            BeltPortService.Sync(s);
            return true;
        }

        // ── G 装配站取料 ─────────────────────────────────────────────────────────────

        private static void CheckAssemblyBasics()
        {
            CampaignState s = World(9661, scrap: 200);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            ItemStackRecord[] mats = AssemblyMaterials.For(ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id));
            // G1 负向“排产时材料不足”：代付关着、家园里没有材料 → 停在“缺材料”，写明缺什么、差多少；不扣废料、不动库存。
            HomeValleyFactory.FactoryOpResult q1 = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Run(2f);
            FactoryQueueItemRecord i1 = Q(s, q1.QueueItemId);
            string wait = HomeValleyFactory.DescribeWait(i1) ?? string.Empty;
            DiagChain qc = RootCauseDiagnosis.DiagnoseBuilding(s, AssemblyMaterials.Station(s))?.Chains.FirstOrDefault(c => c.Steps.Any(x => x.Code == DiagCode.QueueBlocked));
            Expect(q1.Success && i1.State == FactoryQueueState.WaitingResources && i1.Progress == 0f && s.Scrap == 200 && !i1.MaterialsTaken
                   && wait.Contains("结构材 还差 4") && wait.Contains("作战组件 还差 2") && wait.Contains("西侧输入口") && qc != null && qc.Root.Text.Contains("结构材"),
                $"G1 负向“排产时材料不足”（代付关）：排队成功但“{QueueText.FactoryState(i1.State)}”——“{wait}”；废料 200 → {s.Scrap}；“为什么不工作”：{(qc == null ? "（没有）" : RootCauseDiagnosis.ChainText(qc))}");
            // G2 补齐材料：一部分在装配站缓存（真实输入口送来），其余在仓库 → 开工；全有或全无地取走；完工出厂。
            BuildingRecord station = AssemblyMaterials.Station(s);
            BeltPortService.Binding asmIn = BeltPortService.Find(station.BuildingId, "assembly_station.in0");
            GridCell far = default;
            bool laid = asmIn != null && LayIntoPort(s, asmIn, out far);
            int src = laid ? F.TestSource(s, far, "structural", 4) : -1;
            Until(() => AssemblyMaterials.InBuffer(s, "structural") == 4 || !laid, 20);
            if (!laid)
            {
                Put(s, "structural", 4);
                Line("    · 本种子装配站西侧输入口外铺不了带：结构材改放仓库（输入口另由 G 段“输入口”覆盖）");
            }
            Put(s, "part", 4);
            Put(s, "electronic", 2);
            Put(s, "combat_component", 1);
            Run(1f);
            bool stillWaiting = i1.State == FactoryQueueState.WaitingResources && (HomeValleyFactory.DescribeWait(i1) ?? string.Empty).Contains("作战组件 还差 1")
                                && Stock(s, "part") == 4;
            Put(s, "combat_component", 1);
            bool running = Until(() => i1.State == FactoryQueueState.Running, 5);
            bool taken = Stacks(i1.Taken) == Stacks(mats) && Stock(s, "part") == 0 && Stock(s, "combat_component") == 0 && AssemblyMaterials.InBuffer(s, "structural") == 0 && s.Scrap == 200;
            Expect(stillWaiting && running && taken,
                $"G2 差一个作战组件时仍然“缺材料”、一件都不取（全有或全无，{stillWaiting}）；补齐后开工（{running}），取走 {Stacks(i1.Taken)}、废料 {s.Scrap}（{(laid ? "结构材来自装配站材料缓存（真实输入口）" : "全部来自仓库")}，其余来自仓库），废料不动");
            int machines0 = MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            bool done = Until(() => i1.State == FactoryQueueState.Completed, 60);
            Expect(done && MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId) == machines0 + 1 && s.Economy.MachinesFromLine >= 1,
                $"G3 完工出厂一台 ERC-003（材料消耗，记“用产线材料造出”{s.Economy.MachinesFromLine}）");
            // G4 取消正在做的那一项：取走的材料退回仓库（不丢、不复制）。
            MachineRecord blocker = MachineRegistry.AllRecords.First(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId && m.IsInFactory);
            HomeValleyFactory.ReleaseFromFactory(blocker.LogicId);
            PutAll(s, mats);
            string before = string.Join(",", mats.Select(m => m.ItemId + "=" + Stock(s, m.ItemId)));
            HomeValleyFactory.FactoryOpResult q2 = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Until(() => Q(s, q2.QueueItemId).State == FactoryQueueState.Running, 5);
            bool empty = mats.All(m => Stock(s, m.ItemId) == 0);
            HomeValleyFactory.FactoryOpResult cancel = HomeValleyFactory.TryCancel(s, q2.QueueItemId);
            string after = string.Join(",", mats.Select(m => m.ItemId + "=" + (Stock(s, m.ItemId) + Ground(s, m.ItemId))));
            Expect(empty && cancel.Success && after == before && Q(s, q2.QueueItemId).State == FactoryQueueState.Cancelled && (Q(s, q2.QueueItemId).Taken?.Length ?? 0) == 0,
                $"G4 开工后取消：取走的材料全部退回仓库（{before} → {after}），队列项“已取消”");
        }

        private static void CheckAssemblySubstitute()
        {
            CampaignState s = World(9662, scrap: 200);
            BlueprintVersionRecord v = ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id);
            ItemStackRecord[] mats = AssemblyMaterials.For(v);
            bool defaultOn = AssemblyMaterials.ScrapSubstitute(s);
            // G5 代付开着、只缺 2 个作战组件：按废料当量折算（⌈60 × 18 / 68⌉ = 16），其余用材料。
            foreach (ItemStackRecord m in mats.Where(m => m.ItemId != "combat_component"))
            {
                Put(s, m.ItemId, m.Amount);
            }
            HomeValleyFactory.FactoryOpResult q = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Until(() => Q(s, q.QueueItemId).State == FactoryQueueState.Running, 5);
            FactoryQueueItemRecord it = Q(s, q.QueueItemId);
            int expect = AssemblyMaterials.SubstituteScrap(mats, new[] { new ItemStackRecord { ItemId = "combat_component", Amount = 2 } }, v.ScrapCost);
            bool sub = it.State == FactoryQueueState.Running && it.SubstituteScrap == expect && expect == 16 && s.Scrap == 200 - 16
                       && Stacks(it.Taken) == "electronic=2,part=4,structural=4";
            // 取消：材料与代付的废料都退回。
            HomeValleyFactory.TryCancel(s, q.QueueItemId);
            bool refunded = s.Scrap == 200 && Stock(s, "structural") + Ground(s, "structural") == 4 && Stock(s, "part") + Ground(s, "part") == 4;
            Expect(defaultOn && sub && refunded,
                $"G5 “缺材料时用废料代付”默认开着；只缺 2 个作战组件时代付 {it.SubstituteScrap} 废料（按废料当量折算，全部代付 = {v.ScrapCost}），其余取材料；取消后废料与材料全部退回（废料 {s.Scrap}）");
            // G6 代付开着但废料也不够：等待，写明需要 / 现有。
            s.Scrap = 5;
            HomeValleyFactory.FactoryOpResult q2 = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Run(2f);
            FactoryQueueItemRecord i2 = Q(s, q2.QueueItemId);
            string w2 = HomeValleyFactory.DescribeWait(i2) ?? string.Empty;
            Expect(i2.State == FactoryQueueState.WaitingResources && w2.Contains("用废料代付要 16 废料，现有 5") && s.Scrap == 5 && Stock(s, "part") == 4,
                $"G6 负向：代付的废料也不够——“{w2}”，什么都不扣");
            // 废料够了自动开工；全部代付（家园里没有任何材料）= 蓝图废料价。
            s.Scrap = 300;
            bool go = Until(() => i2.State == FactoryQueueState.Running, 5);
            HomeValleyFactory.TryCancel(s, q2.QueueItemId);
            foreach (ItemStackRecord m in mats)
            {
                HomeInventory.RemoveUpTo(s, Item(m.ItemId), 999);
            }
            s.Scrap = 300;
            HomeValleyFactory.FactoryOpResult q3 = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Until(() => Q(s, q3.QueueItemId).State == FactoryQueueState.Running, 5);
            Expect(go && Q(s, q3.QueueItemId).SubstituteScrap == v.ScrapCost && s.Scrap == 300 - v.ScrapCost,
                $"G7 补足废料后自动开工；家园里一件材料都没有时全部代付 = 蓝图废料价 {v.ScrapCost}（与改造前的价格一致，早期没有产线也能造机器）");
            HomeValleyFactory.TryCancel(s, q3.QueueItemId);
            // G8 代付关掉：同样的情况一直等（不动用废料）。
            AssemblyMaterials.SetScrapSubstitute(s, false);
            HomeValleyFactory.FactoryOpResult q4 = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Run(3f);
            Expect(Q(s, q4.QueueItemId).State == FactoryQueueState.WaitingResources && s.Scrap == 300 && !AssemblyMaterials.ScrapSubstitute(s),
                "G8 把“缺材料时用废料代付”关掉后，材料不齐就一直等，废料一点不动");
        }

        private static void CheckAssemblyPowerAndDestroyed()
        {
            CampaignState s = World(9663, scrap: 300);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            ItemStackRecord[] mats = AssemblyMaterials.For(ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id));
            PutAll(s, mats);
            BuildingRecord station = AssemblyMaterials.Station(s);
            HomeValleyFactory.FactoryOpResult q = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Until(() => Q(s, q.QueueItemId).State == FactoryQueueState.Running, 5);
            FactoryQueueItemRecord it = Q(s, q.QueueItemId);
            Run(5f);
            float progress = it.Progress;
            // G9 断电：停在“缺电力”，进度保留；来电后接着做，不重复取料。
            PutAll(s, mats);
            station.ConstructionState = BuildingConstructionState.Disabled;
            Run(3f);
            bool waiting = it.State == FactoryQueueState.WaitingPower && Mathf.Approximately(it.Progress, progress);
            station.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            Run(2f);
            bool resumed = it.State == FactoryQueueState.Running && it.Progress > progress && mats.All(m => Stock(s, m.ItemId) == m.Amount);
            Expect(waiting && resumed, $"G9 装配站关停 / 断电：“{QueueText.FactoryState(it.State)}”，进度 {progress:F1} 保留；恢复后接着做，仓库里的第二份材料一件没动（不重复取料）");
            // G10 装配站被毁：正在做的那项失败，取走的材料退回仓库。
            int before = mats.Sum(m => Stock(s, m.ItemId));
            station.ConstructionState = BuildingConstructionState.Destroyed;
            Run(1f);
            int after = mats.Sum(m => Stock(s, m.ItemId) + Ground(s, m.ItemId));
            Expect(it.State == FactoryQueueState.Failed && it.BlockedReason == "assembly-station-destroyed" && after - before == mats.Sum(m => m.Amount) && (it.Taken?.Length ?? 0) == 0,
                $"G10 装配站被毁：正在做的那项失败，取走的 {mats.Sum(m => m.Amount)} 件材料退回仓库（{before} → {after}），不丢不复制、不生成幽灵机");
            station.ConstructionState = BuildingConstructionState.Operational;
        }

        private static void CheckAssemblyRoute()
        {
            CampaignState s = World(9664, scrap: 300);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            ItemStackRecord[] mats = AssemblyMaterials.For(ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id));
            PutAll(s, mats);
            BuildingRecord station = AssemblyMaterials.Station(s);
            GridContent.TryGetBuilding(station.BuildingTypeId, out BuildingGrid g);
            GridMath.FootprintBounds(new GridCell(station.GridX, station.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(station.Rotation), out GridCell min, out GridCell max);
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            var changed = new List<(GridCell c, byte t)>();
            for (int x = min.X - 1; x <= max.X + 1; x++)
            {
                for (int y = min.Y - 1; y <= max.Y + 1; y++)
                {
                    var c = new GridCell(x, y);
                    if ((x == min.X - 1 || x == max.X + 1 || y == min.Y - 1 || y == max.Y + 1) && HomeGridService.BuildingAt(s, c) == null)
                    {
                        changed.Add((c, map.GetTerrain(c)));
                        map.SetTerrain(c, cliff);
                    }
                }
            }
            FirmwareRouteInfo r = FirmwareLibrary.EvaluateRoute(s, forceFresh: true);
            int denied0 = GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.Denied);
            HomeValleyFactory.FactoryOpResult q = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(1);
            int denied = GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.Denied) - denied0;
            Run(2f);
            FactoryQueueItemRecord it = Q(s, q.QueueItemId);
            string w = HomeValleyFactory.DescribeWait(it) ?? string.Empty;
            Line($"    · G11：库存 {string.Join(",", mats.Select(m => m.ItemId + "=" + Stock(s, m.ItemId)))}，状态 {it.State}");
            Expect(!r.Ok && it.State == FactoryQueueState.WaitingResources && it.BlockedReason == HomeValleyFactory.ReasonRoute && w.Contains("送不到装配站")
                   && denied >= 1 && mats.All(m => Stock(s, m.ItemId) == m.Amount),
                $"G11 DEBT-FG2FW05-01：仓库到装配站的路线被悬崖围死（{FirmwareLibrary.RouteText(r)}）→ 停在“缺材料”，写明“{w}”，仓库里的材料一件不取；进入“被堵”时发一次可定位的拒绝提示（{denied} 次，文字与队列原因同一句；字幕按字幕设置显示）");
            // 路线恢复后自动开工。
            foreach ((GridCell c, byte t) in changed)
            {
                map.SetTerrain(c, t);
            }
            // 审查 P2：只改地形（拆掉悬崖），不动库存、不手动刷新路线缓存——等待指纹含地形版本，模拟路径的路线判定不靠真实时钟过期。
            bool go = Until(() => it.State == FactoryQueueState.Running, 5);
            Expect(go, "G11 路线通了以后自动开工（只改地形：不碰库存、不手动刷新路线缓存）");
        }

        private static void CheckAssemblyRetrofitAndLegacy()
        {
            CampaignState s = World(9665, scrap: 300);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            // 一台在场的 ERC-003（v1），蓝图加一个 v2：多装一个结构模块、废料价 +10。
            BlueprintRecord bp = s.BlueprintRecords.First(b => b.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
            BlueprintVersionRecord v1 = bp.Versions.First(v => v.Version == bp.ActiveVersion);
            var v2 = new BlueprintVersionRecord
            {
                Version = v1.Version + 1, ChassisId = v1.ChassisId, PrimaryId = v1.PrimaryId, UtilityId = v1.UtilityId, StructureId = ComponentCatalog.StructCargoId,
                OrderedFirmwareIds = v1.OrderedFirmwareIds, ScrapCost = v1.ScrapCost + 10, CompileSignature = "retro-test:v2", FactionTags = Array.Empty<string>(),
                CircuitSlotContentIds = v1.CircuitSlotContentIds, CircuitSlotTypes = v1.CircuitSlotTypes,
            };
            bp.Versions = bp.Versions.Append(v2).ToArray();
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                HomeGridService.ExitPosition(s, "assembly_exit") + new Vector2(6f, 0f), 100f, 100f, blueprintVersion: v1.Version, loadoutSignature: v1.CompileSignature);
            HomeValleyFactory.RetrofitPrice p2 = HomeValleyFactory.PriceRetrofit(v1, v2);
            HomeValleyFactory.FactoryOpResult q = HomeValleyFactory.TryEnqueueRetrofit(s, m.LogicId, HomeValleyLayout.BlueprintErc003Id, v2.Version, false);
            FactoryQueueItemRecord it = Q(s, q.QueueItemId);
            bool diff = q.Success && Stacks(it.Materials) == "structure_module=1" && it.ScrapPrice == p2.MaterialPrice && p2.MaterialPrice > 0 && p2.MaterialPrice <= 10
                        && p2.Total == 10 && p2.MaterialPrice + p2.Fee == p2.Total && (p2.Fee == 0) == string.IsNullOrEmpty(it.TransactionId);
            Run(2f);
            bool waits = it.State == FactoryQueueState.WaitingResources && (HomeValleyFactory.DescribeWait(it) ?? string.Empty).Contains("结构模块 还差 1");
            Put(s, "structure_module", 1);
            bool done = Until(() => it.State == FactoryQueueState.Completed, 40);
            MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec);
            Line($"    · G12：差额 {diff}、等待 {waits}（{HomeValleyFactory.DescribeWait(it)}）、完工 {done}（{it.State} / {it.BlockedReason}）、废料 {s.Scrap}");
            Expect(diff && waits && done && rec.BlueprintVersion == v2.Version && Stock(s, "structure_module") == 0 && s.Scrap == 300 - p2.Fee,
                $"G12 回厂改造只补新旧蓝图的差额材料（{Stacks(it.Materials)}，这一份值 {p2.MaterialPrice} 废料）+ 工时费 {p2.Fee}，全部用废料总价 {p2.Total} = max(10, 差额 10)；缺料等待 → 补上后完工，机器换装 v{rec?.BlueprintVersion}，废料只扣工时费（{300 - s.Scrap}）");
            // 审查 P1：材料差额为空的改造（同部位换更贵的组件、只换固件）不能变便宜——整笔按废料收 max(10, 差额)。
            var v3 = new BlueprintVersionRecord
            {
                Version = v2.Version + 1, ChassisId = v2.ChassisId, PrimaryId = ComponentCatalog.CompCannonId, UtilityId = v2.UtilityId, StructureId = v2.StructureId,
                OrderedFirmwareIds = v2.OrderedFirmwareIds, ScrapCost = v2.ScrapCost + 15, CompileSignature = "retro-test:v3", FactionTags = Array.Empty<string>(),
                CircuitSlotContentIds = v2.CircuitSlotContentIds, CircuitSlotTypes = v2.CircuitSlotTypes,
            };
            var v4 = new BlueprintVersionRecord
            {
                Version = v3.Version + 1, ChassisId = v3.ChassisId, PrimaryId = v3.PrimaryId, UtilityId = v3.UtilityId, StructureId = v3.StructureId,
                OrderedFirmwareIds = (v3.OrderedFirmwareIds ?? Array.Empty<string>()).Reverse().ToArray(), ScrapCost = v3.ScrapCost + 6, CompileSignature = "retro-test:v4",
                FactionTags = Array.Empty<string>(), CircuitSlotContentIds = v3.CircuitSlotContentIds, CircuitSlotTypes = v3.CircuitSlotTypes,
            };
            bp.Versions = bp.Versions.Append(v3).Append(v4).ToArray();
            foreach ((BlueprintVersionRecord to, int diffPrice, string what) in new[] { (v3, 15, "主组件换成炮（同一部位，差额 15）"), (v4, 6, "只换固件顺序（差额 6）") })
            {
                int fromVersion = rec.BlueprintVersion;
                HomeValleyFactory.RetrofitPrice pr = HomeValleyFactory.PriceRetrofit(bp.Versions.First(v => v.Version == fromVersion), to);
                int scrapBefore = s.Scrap;
                HomeValleyFactory.FactoryOpResult qx = HomeValleyFactory.TryEnqueueRetrofit(s, m.LogicId, HomeValleyLayout.BlueprintErc003Id, to.Version, false);
                FactoryQueueItemRecord ix = Q(s, qx.QueueItemId);
                int expected = Math.Max(HomeValleyFactory.RetrofitMinScrapCost, diffPrice);
                bool priced = qx.Success && (ix.Materials == null || ix.Materials.Length == 0) && ix.ScrapPrice == 0 && pr.MaterialPrice == 0 && pr.Fee == expected && pr.Total == expected
                              && !string.IsNullOrEmpty(ix.TransactionId);
                bool fin = Until(() => ix.State == FactoryQueueState.Completed, 40);
                MachineRegistry.TryGetRecord(m.LogicId, out rec);
                Expect(priced && fin && rec.BlueprintVersion == to.Version && scrapBefore - s.Scrap == expected,
                    $"G12 {what}：材料差额为空，整笔按废料收 {scrapBefore - s.Scrap}（应为 max(10, {diffPrice}) = {expected}；预览与入队同一个 PriceRetrofit：工时费 {pr.Fee}、总价 {pr.Total}），完工换装 v{rec.BlueprintVersion}");
            }
            // 旧存档里的队列项（没有材料清单，入队时登记了废料事务）照旧按废料结算。
            string tx = "legacy-q:tx";
            CampaignEconomyLedger.ProposeConsume(s, tx, "legacy-q", CampaignEconomyLedger.ResourceScrap, 35);
            var legacy = new FactoryQueueItemRecord
            {
                QueueItemId = "legacy-q", Kind = FactoryQueueKind.Produce, BlueprintId = HomeValleyLayout.BlueprintHaulerId, BlueprintVersion = 1, TransactionId = tx,
                Duration = 2f, State = FactoryQueueState.Queued, CreatedTick = long.MaxValue / 2,
            };
            s.FactoryQueues = s.FactoryQueues.Append(legacy).ToArray();
            foreach (MachineRecord mr in MachineRegistry.AllRecords.Where(x => x.IsInFactory).ToList())
            {
                HomeValleyFactory.ReleaseFromFactory(mr.LogicId);
            }
            int scrap0 = s.Scrap;
            bool legacyDone = Until(() => legacy.State == FactoryQueueState.Completed, 20);
            Expect(legacyDone && s.Scrap == scrap0 - 35 && !legacy.MaterialMode, $"G13 旧存档的队列项（MaterialMode = false）照旧按入队时的废料事务结算（扣 {scrap0 - s.Scrap}）并出厂");
        }

        private static void CheckAssemblyPort()
        {
            CampaignState s = World(9666, scrap: 300);
            BuildingRecord station = AssemblyMaterials.Station(s);
            BeltPortService.Binding asmIn = BeltPortService.Find(station.BuildingId, "assembly_station.in0");
            if (asmIn == null || !LayIntoPort(s, asmIn, out GridCell far))
            {
                Line("    · 本种子装配站西侧输入口外铺不了带，换种子");
                s = World(9667, scrap: 300);
                station = AssemblyMaterials.Station(s);
                asmIn = BeltPortService.Find(station.BuildingId, "assembly_station.in0");
                if (asmIn == null || !LayIntoPort(s, asmIn, out far))
                {
                    Fail("G 装配站输入口外铺不了带（两个种子）");
                    return;
                }
            }
            bool accept = BeltNetworkService.Kernel.TryGetPortInfo(asmIn.PortId, out BeltPortInfo pi) && pi.Accept == BeltConst.AcceptSet;
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, station, views);
            string line = views.FirstOrDefault(v => v.PortKey == "assembly_station.in0")?.AcceptLine ?? string.Empty;
            // 上限：送 25 个零件，缓存放到 20 件，剩下的留在带上 / 端口里（带停下“下游已满”）。
            int src = F.TestSource(s, far, "part", 25);
            Run(25f);
            int cap = AssemblyMaterials.BufferCap;
            int inBuf = AssemblyMaterials.InBuffer(s, "part");
            BeltNetworkService.Kernel.TryGetCellInfo(asmIn.BeltCell.X, asmIn.BeltCell.Y, out BeltCellInfo head);
            int onBelts = 0;
            Vector2Int v = GridMath.DirVector(asmIn.Face);
            for (int i = 0; i < 3; i++)
            {
                onBelts += F.OnBelt(asmIn.BeltCell.X + v.x * i, asmIn.BeltCell.Y + v.y * i);
            }
            int portBuf = BeltNetworkService.Kernel.TryGetPortCounts(asmIn.PortId, out _, out int bufd) ? bufd : 0;
            var dist = new Dictionary<string, long>();
            ProductionService.CollectBuffers(s, dist);
            Expect(dist.TryGetValue("part", out long distPart) && distPart >= cap,
                $"G14 物资悬停的“分布”把装配站材料缓存算进“生产建筑缓存（在途）”（零件 {distPart}）");
            Expect(accept && line.Contains("机器材料") && line.Contains("停在带末端") && !line.Contains("转进仓库") && inBuf == cap && cap == 20 && SourceTotal(src) == 25 && inBuf + onBelts + portBuf == 25 && head.Block == BeltBlock.SinkFull,
                $"G14 装配站输入口（DEBT-FG3LOG03-01）收机器材料（“{line}”）：25 个零件进缓存 {inBuf}（上限 {cap}），其余 {onBelts + portBuf} 件留在带上 / 端口里，带停下（{head.Block}）");
            // 审查 P2：零件缓存满了、端口里还压着零件——同带后面的材料进不来。队首等料的原因要写明“输入口被零件占着”和解法，不能只写“缺材料”。
            AssemblyMaterials.SetScrapSubstitute(s, false);
            HomeValleyFactory.FactoryOpResult jq = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            Run(2f);
            FactoryQueueItemRecord jit = Q(s, jq.QueueItemId);
            string jw = HomeValleyFactory.DescribeWait(jit) ?? string.Empty;
            string partName = ItemCatalog.NameOf("part");
            Expect(jq.Success && jit.State == FactoryQueueState.WaitingResources && AssemblyMaterials.PortJamItem == "part" && jw.Contains("占着") && jw.Contains(partName) && jw.Contains("放进仓库"),
                $"G14 负向：输入口被缓存已满的{partName}占着时，等料原因写明“同一条带后面的材料进不来”和解法（“{jw}”）");
            HomeValleyFactory.TryCancel(s, jq.QueueItemId);
            // 不是机器材料：金属矿到末端停下，原因写明只收机器材料。
            HomeInventory.Touch();
            ProductionService.Add(ref s.Economy.AssemblyBuffer, "part", -20);
            Run(5f);
            BeltNetworkService.Kernel.RemovePort(src);
            Run(5f);
            int ore = F.TestSource(s, far, "metal_ore", 1);
            bool rejected = Until(() => BeltNetworkService.Kernel.TryGetCellInfo(asmIn.BeltCell.X, asmIn.BeltCell.Y, out BeltCellInfo c) && c.Block == BeltBlock.SinkRejects, 20);
            BeltNetworkService.Kernel.TryGetCellInfo(asmIn.BeltCell.X, asmIn.BeltCell.Y, out BeltCellInfo rc);
            string why = BeltNetworkService.DescribeBlock(rc);
            Expect(rejected && why.Contains("只收机器材料") && why.Contains("金属矿") && AssemblyMaterials.InBuffer(s, "metal_ore") == 0,
                $"G15 负向：金属矿不是机器材料——到末端停下（“{why}”），不进缓存、不消失");
            // 读档迁移：旧存档里装配站输入口“什么都不收”，读档对账后改成收机器材料。
            BeltNetworkService.Kernel.SetSinkAccept(asmIn.PortId, BeltConst.AcceptNone);
            s.Belts.PortBindings = s.Belts.PortBindings.ToArray(); // 绑定表换了引用 → 端口索引重建（与读档同一路径）
            Run(2f);
            bool migrated = BeltNetworkService.Kernel.TryGetPortInfo(asmIn.PortId, out BeltPortInfo pm) && pm.Accept == BeltConst.AcceptSet;
            Expect(migrated, "G16 旧存档迁移：装配站输入口从“什么都不收”改为收机器材料（端口索引重建时一次）");
        }

        // ── H 配方记忆与复制设置到同类建筑 ─────────────────────────────────────────────────

        private static void CheckRecipeMemoryAndCopy()
        {
            CampaignState s = World(9671, scrap: 500);
            GridCell? a0 = F.FindFree(s, "parts_workshop", 6f, 22f);
            if (a0 == null)
            {
                Fail("H 放不下");
                return;
            }
            BuildingRecord a = F.Built(s, "parts_workshop", "ma", a0.Value);
            ProductionService.Producer pa = F.P(s, a);
            bool fresh = pa.Recipe == null && !pa.Rec.Inherited;
            ProductionService.TrySetRecipe(s, a.BuildingId, "structural", out _);
            BuildingRecord b = F.Built(s, "parts_workshop", "mb", F.FindFree(s, "parts_workshop", 6f, 22f).Value);
            ProductionService.Producer pb = F.P(s, b);
            bool inherited = pb.Recipe?.Id == "structural" && pb.Rec.Inherited && ProductionService.MemoryOf(s, "parts_workshop")?.RecipeId == "structural";
            ProductionService.TrySetRecipe(s, b.BuildingId, "part", out _);
            BuildingRecord c = F.Built(s, "parts_workshop", "mc", F.FindFree(s, "parts_workshop", 6f, 22f).Value);
            ProductionService.Producer pc = F.P(s, c);
            bool latest = pc.Recipe?.Id == "part" && !pb.Rec.Inherited && pa.Recipe?.Id == "structural";
            Expect(fresh && inherited && latest,
                "H1 配方记忆：第一座零件工坊没选配方；选“结构材”后新放的第二座沿用“结构材”（标记“沿用了上一次的选择”）；第二座改选“零件”后，第三座沿用“零件”；已有建筑的配方不被改动");
            // H2 复制设置到同类建筑：A 正在做“结构材”（输入缓存有合金），从 B（零件）复制 → A 改“零件”（作废的那份合金退回输入缓存，零件也用合金所以不退仓库），C 本来就是。
            ProductionService.Add(ref pa.Rec.In, "alloy", 4);
            Run(1f);
            bool aRunning = pa.Rec.Running;
            int alloyA = ProductionService.Count(pa.Rec.In, "alloy") + (pa.Rec.Running ? 2 : 0);
            int changed = ProductionService.CopySettingsToSameType(s, b.BuildingId, out int same, out string msg);
            pa = F.P(s, a); // 放下新建筑后生产索引重建：重新取运行时视图
            pc = F.P(s, c);
            Expect(aRunning && changed == 1 && same == 1 && pa.Recipe?.Id == "part" && pc.Recipe?.Id == "part" && !pa.Rec.Running && ProductionService.Count(pa.Rec.In, "alloy") == alloyA
                   && msg.Contains("零件") && msg.Contains("1") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyFirstCopyToSameType),
                $"H2 复制设置到同类建筑：“{msg}”——A 改成“零件”（正在做的那份作废，合金 {alloyA} 份留在输入缓存 → {ProductionService.Count(pa.Rec.In, "alloy")}；" +
                $"A 开工过 {aRunning}、A 现在 {pa.Recipe?.Id}、C {pc.Recipe?.Id}、改 {changed} 同 {same}），C 本来就是");
            // H3 刻录台：复制刻录目标。
            List<string> burnable = SignalCoreService.PrintableFirmware(s);
            BuildingRecord fb1 = F.Built(s, "firmware_burner", "mf1", F.FindFree(s, "firmware_burner", 6f, 22f).Value);
            BuildingRecord fb2 = F.Built(s, "firmware_burner", "mf2", F.FindFree(s, "firmware_burner", 6f, 22f).Value);
            ProductionService.TrySetBurnTarget(s, fb1.BuildingId, burnable[0], out _);
            int n = ProductionService.CopySettingsToSameType(s, fb1.BuildingId, out _, out string bm);
            BuildingRecord fb3 = F.Built(s, "firmware_burner", "mf3", F.FindFree(s, "firmware_burner", 6f, 22f).Value);
            Expect(n == 1 && F.P(s, fb2).Rec.BurnTarget == burnable[0] && F.P(s, fb3).Rec.BurnTarget == burnable[0] && F.P(s, fb3).Rec.Inherited,
                $"H3 刻录台：复制刻录目标到另一座（“{bm}”）；新放的第三座沿用上一次的刻录目标");
            // H4 负向：只有一座同类 / 固定功能的建筑。
            BuildingRecord ce = F.Built(s, "component_workshop", "mce", F.FindFree(s, "component_workshop", 6f, 22f).Value);
            int none = ProductionService.CopySettingsToSameType(s, ce.BuildingId, out _, out string nm);
            BuildingRecord tw = F.Built(s, "blending_station", "mbs", F.FindFree(s, "blending_station", 6f, 22f).Value);
            int fixedN = ProductionService.CopySettingsToSameType(s, tw.BuildingId, out _, out string fm);
            Expect(none == 0 && nm.Contains("没有别的") && fixedN < 0 && fm.Contains("固定功能"),
                $"H4 负向：家园里只有一座组件工坊（“{nm}”）；调配站只有固定功能（“{fm}”）——都不改任何东西");
        }

        // ── I 存读档 ───────────────────────────────────────────────────────────────

        private static string Snap(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (ProducerRecord r in s.Economy.Producers.OrderBy(r => r.BuildingId, StringComparer.Ordinal))
            {
                sb.Append(r.BuildingId).Append('|').Append(r.RecipeId).Append('|').Append(r.BurnTarget).Append('|').Append(r.Inherited).Append('|').Append(r.Running).Append('|')
                    .Append(r.Progress).Append('/').Append(r.Duration).Append('|').Append(r.Completed).Append('|').Append(Stacks(r.In)).Append('|').Append(Stacks(r.Out)).Append(';');
            }
            foreach (FactoryQueueItemRecord q in (s.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>()).OrderBy(q => q.CreatedTick))
            {
                sb.Append("q:").Append(q.Kind).Append('|').Append(q.State).Append('|').Append(q.Progress.ToString("R")).Append('|').Append(q.MaterialMode).Append('|')
                    .Append(Stacks(q.Materials)).Append('|').Append(Stacks(q.Taken)).Append('|').Append(Stacks(q.Shortfall)).Append('|').Append(q.SubstituteScrap).Append('|').Append(q.BlockedReason).Append(';');
            }
            sb.Append("buf:").Append(Stacks(s.Economy.AssemblyBuffer)).Append("|sub:").Append(s.Economy.AssemblyScrapSubstitute).Append("|line:").Append(s.Economy.MachinesFromLine);
            foreach (RecipeMemoryRecord m in s.Economy.RecipeMemory)
            {
                sb.Append("|mem:").Append(m.TypeId).Append('=').Append(m.RecipeId).Append('/').Append(m.BurnTarget);
            }
            foreach (IGrouping<string, PrimitiveChipRecord> g in (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).GroupBy(c => c.CardDefId + ":" + c.Origin + ":" + c.State).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append("|chip:").Append(g.Key).Append('=').Append(g.Count());
            }
            foreach (string id in new[] { "alloy", "part", "structural", "electronic", "combat_component", "chip_substrate", "rare_earth_ore" })
            {
                sb.Append('|').Append(id).Append('=').Append(Stock(s, id));
            }
            sb.Append("|scrap:").Append(s.Scrap).Append("|machines:").Append(MachineRegistry.AllRecords.Count(m => m.IsAlive));
            return sb.ToString();
        }

        /// <summary>混合场景：零件工坊（零件）+ 刻录台（有目标）+ 装配站（一项在做、一项缺料等待、缓存里有料、代付关）。</summary>
        private static bool LayMixed(CampaignState s)
        {
            Rig pw = Lay(s, "parts_workshop", "mxp");
            if (pw == null)
            {
                return false;
            }
            ProductionService.TrySetRecipe(s, pw.B.BuildingId, "part", out _);
            Feed(s, pw, "parts_workshop.in0", "alloy", 40);
            List<string> burnable = SignalCoreService.PrintableFirmware(s);
            GridCell? fbAt = F.FindFree(s, "firmware_burner", 6f, 22f);
            if (fbAt == null || burnable.Count == 0)
            {
                return false;
            }
            BuildingRecord fb = F.Built(s, "firmware_burner", "mxb", fbAt.Value);
            ProductionService.TrySetBurnTarget(s, fb.BuildingId, burnable[0], out _);
            ProductionService.Add(ref F.P(s, fb).Rec.In, "chip_substrate", 2);
            AssemblyMaterials.SetScrapSubstitute(s, false);
            ItemStackRecord[] mats = AssemblyMaterials.For(ActiveVersion(s, HomeValleyLayout.BlueprintErc003Id));
            PutAll(s, mats);
            ProductionService.Add(ref s.Economy.AssemblyBuffer, "structural", 3);
            HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHaulerId);
            return true;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = World(9681, scrap: 300);
            if (!LayMixed(s))
            {
                Fail("I 场景放不下");
                return;
            }
            Run(13.3f);
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Run(30f);
            string continuous = Snap(s);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyFactory.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("I 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            string loaded = Snap(rr.State);
            Run(30f);
            string after = Snap(rr.State);
            CampaignState cur = rr.State;
            bool fields = cur.Economy.Producers.Any(r => !string.IsNullOrEmpty(r.BurnTarget)) && cur.Economy.AssemblyScrapSubstitute == 2
                          && cur.Economy.RecipeMemory.Any(m => m.TypeId == "parts_workshop" && m.RecipeId == "part")
                          && cur.FactoryQueues.Any(q => q.MaterialMode && q.Taken != null) && cur.FactoryQueues.Any(q => q.MaterialMode && q.Shortfall != null && q.Shortfall.Length > 0);
            Expect(save.Success && loaded == before && fields,
                "I1 真文件存读档：刻录目标、配方记忆、装配站材料缓存、代付开关、队列项的材料清单 / 已取料 / 缺料，逐字段往返一致" + (loaded == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded}"));
            Expect(after == continuous, "I2 读档后接着跑 30 游戏秒，与不存档一直跑逐位一致（制造、刻录、装配取料与进度）" + (after == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{after}"));
            CampaignState legacy = F.NewWorld(9682);
            legacy.Economy.AssemblyBuffer = null;
            legacy.Economy.RecipeMemory = null;
            CampaignFgStateDomains.EnsureAll(legacy);
            Expect(legacy.Economy.AssemblyBuffer != null && legacy.Economy.RecipeMemory != null && legacy.Economy.AssemblyScrapSubstitute == 0 && AssemblyMaterials.ScrapSubstitute(legacy),
                "I3 旧存档没有材料缓存 / 配方记忆 / 代付开关：读入时补空域，代付按默认（开）");
        }

        // ── J 暂停与倍速、K 观察一致 ──────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = World(seed, observe, 300);
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
                string snap = RunScenario(9691, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下" && reference.Contains("Completed"),
                "J1 暂停中（120 帧）制造、刻录与装配都不动；0.5x / 1x / 2x / 3x 跑同样的 45 游戏秒，进度、缓存、芯片、取料与出厂逐位一致");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9701, true, 1f, false, out _);
            string unseen = RunScenario(9701, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下", "K1 同一组制造建筑与装配站在观察与不观察家园时跑 45 游戏秒逐字段一致（FGR-BASE-021：家园后台结果与观察一致）"
                                                       + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── L 面板（真 UXML）────────────────────────────────────────────────────────

        private static void CheckPanels()
        {
            CampaignState s = World(9711, scrap: 300);
            Rig eb = Lay(s, "electronics_bench", "pe");
            GridCell? fbAt = F.FindFree(s, "firmware_burner", 6f, 22f);
            if (eb == null || fbAt == null)
            {
                Fail("L 场景放不下");
                return;
            }
            BuildingRecord fb = F.Built(s, "firmware_burner", "pf", fbAt.Value);
            BuildingRecord fb2 = F.Built(s, "firmware_burner", "pf2", F.FindFree(s, "firmware_burner", 6f, 22f) ?? fbAt.Value);
            Run(2f);
            VisualElement root = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject go);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ProductionPanelUIToolkit panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(fb.BuildingId);
                panel.Refresh();
                List<string> burnable = SignalCoreService.PrintableFirmware(s);
                bool burner = panel.PanelVisible && panel.BurnRowVisible && !panel.RecipeDropdownVisible && panel.BurnField.choices.Count == burnable.Count + 1
                              && panel.ReasonText.Contains("没选要刻录的固件") && panel.RecipeLineText.Contains("芯片基板") && panel.CopyVisible && panel.DetailText.Contains("固件芯片存放");
                // 下拉框选中即生效。
                panel.BurnField.value = panel.BurnField.choices[1];
                bool chose = F.P(s, fb).Rec.BurnTarget == burnable[0] && panel.MessageText.Contains("刻录目标改为") && panel.MemoryText.Contains("新建的固件刻录台会沿用");
                Expect(burner && chose, $"L1 刻录台面板（真 UXML）：刻录目标下拉 {panel.BurnField.choices.Count} 项（不选 + {burnable.Count} 条已破解固件），“{panel.ReasonText}”；选第一条即生效（“{panel.MessageText}”）；" +
                                        $"记忆说明“{panel.MemoryText}”；{panel.DetailText.Replace("\n", " | ")}");
                // 复制设置：确认框 → 应用。
                panel.AskCopySettings();
                bool asked = UiConfirmDialog.IsOpen;
                UiConfirmDialog.Confirm();
                bool copied = F.P(s, fb2).Rec.BurnTarget == burnable[0] && panel.MessageText.Contains("已把");
                Expect(asked && copied, $"L2 “复制设置到同类建筑”先弹确认框（写明会改几座），确认后另一座刻录台也刻“{FirmwareKinds.DisplayName(burnable[0])}”（“{panel.MessageText}”）");
                // 电子组装台：配方下拉 4 项；缺第二种材料的原因写明口的方向；“?”打开图鉴“制造建筑”。
                ProductionPanelUIToolkit.Open(eb.B.BuildingId);
                panel.Refresh();
                panel.RecipeField.value = panel.RecipeField.choices[1];
                Feed(s, eb, "electronics_bench.in0", "alloy", 2);
                Run(6f);
                panel.Refresh();
                panel.OpenCodex();
                Expect(panel.RecipeField.choices.Count == 4 && panel.ReasonText.Contains("南输入口") && MechanicCodex.LastOpenedId == "codex.economy.manufacturing" && panel.CopyVisible,
                    $"L3 电子组装台面板：配方下拉 4 项，选“电子件”后“{panel.ReasonText.Replace("\n", " · ")}”；“?”打开图鉴“制造建筑”（{MechanicCodex.LastOpenedId}）");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
            }
            // 装配站面板的新控件在 UXML 里（面板本体走 YooAsset 异步加载，Play 冒烟里真实点开）。
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/Factory/FactoryPanel.uxml");
            VisualElement tree = vta != null ? vta.CloneTree() : null;
            Expect(tree != null && tree.Q<Label>("MaterialsLabel") != null && tree.Q<Toggle>("SubstituteToggle") != null && tree.Q<Label>("SubstituteNote") != null && tree.Q<Label>("BufferLabel") != null,
                "L4 装配站面板 UXML 有材料清单、“缺材料时用废料代付”开关、开关说明、材料缓存四个控件");
            // 布局探针：中英 × 三种缩放，刻录台（新加的下拉框与按钮）。
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                    {
                        VisualElement r2 = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject g2);
                        try
                        {
                            r2.style.scale = new Scale(new Vector3(scale, scale, 1f));
                            ProductionPanelUIToolkit p2 = g2.AddComponent<ProductionPanelUIToolkit>();
                            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
                            p2.BindView(r2);
                            ProductionPanelUIToolkit.Open(fb.BuildingId);
                            p2.Refresh();
                            UiToolkitLayoutProbe.ForceLayout(r2);
                            Button copy = p2.CopyButton;
                            DropdownField burn = p2.BurnField;
                            bool ok = copy.resolvedStyle.width > 20f && burn.resolvedStyle.width > 40f && !float.IsNaN(copy.worldBound.width);
                            if (!ok)
                            {
                                Fail($"L5 布局探针（{lang}，缩放 {scale}）：复制按钮宽 {copy.resolvedStyle.width:0.#}、刻录下拉宽 {burn.resolvedStyle.width:0.#}");
                            }
                        }
                        finally
                        {
                            ProductionPanelUIToolkit.Close();
                            Object.DestroyImmediate(g2);
                            UiEscapeStack.Clear();
                        }
                    }
                }
                Expect(true, "L5 布局探针：中英 × 0.8 / 1 / 1.5 缩放，刻录下拉框与“复制设置到同类建筑”按钮都有正常宽度");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        // ── M 建造菜单真实入口 ─────────────────────────────────────────────────────────

        private static void CheckBuildMenu()
        {
            CampaignState s = World(9721, scrap: 500);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? at = F.FindArea(s, 4, 4);
            if (mode == null || at == null)
            {
                Fail("M 建造模式没绑定 / 找不到空地");
                return;
            }
            try
            {
                mode.Open();
                bool listed = BuildCatalog.CountInCategory("manufacturing") >= 4;
                mode.Select("electronics_bench");
                GridCell c = F.At(at.Value, 1, 1);
                mode.PointerDown(s, c);
                mode.PointerUp(s, c);
                BuildingRecord ghost = HomeGridService.BuildingAt(s, c);
                mode.ClearSelection();
                ProductionService.Sync(s, force: true);
                ProductionService.Producer p = ghost != null ? F.P(s, ghost) : null;
                Run(1f);
                bool planned = ghost != null && ghost.BuildingTypeId == "electronics_bench" && p != null && p.State == ProdState.Building && MechanicCodex.IsUnlocked("codex.economy.manufacturing");
                bool built = Until(() => ghost.ConstructionState == BuildingConstructionState.Operational, 400);
                Run(3f);
                Expect(listed && planned && built && p.State == ProdState.Idle && Why(s, p).Contains("没选配方"),
                    $"M1 建造菜单“制造”页签有四座制造建筑；放下电子组装台 = 虚影（“{(p == null ? "?" : ProductionService.StateText(p))}”），图鉴“制造建筑”解锁；机器施工建成后待机（没选配方）");
            }
            finally
            {
                mode.Close();
            }
        }

        // ── N 状态覆盖（FGT-ECO-002 的制造建筑部分）──────────────────────────────────────

        private static void CheckStateCoverage()
        {
            CampaignState s = World(9731, scrap: 300);
            Rig r = Lay(s, "parts_workshop", "st");
            if (r == null)
            {
                Fail("N 场景放不下");
                return;
            }
            ProductionService.Producer p = P(s, r);
            ProductionService.TrySetRecipe(s, r.B.BuildingId, "part", out _);
            Run(1f);
            r.B.ConstructionState = BuildingConstructionState.Disabled;
            Run(1f);
            r.B.ConstructionState = BuildingConstructionState.Damaged;
            Run(1f);
            r.B.ConstructionState = BuildingConstructionState.Operational;
            r.B.PowerState = BuildingPowerState.Unpowered;
            ProductionService.Step(s, 3, GameClock.StepHz);
            Seen(p);
            HomeValleyPowerGrid.Recompute(s);
            string[] types = { "parts_workshop", "electronics_bench", "component_workshop", "firmware_burner" };
            string[] need = { "Idle", "MissingInput", "Working" };
            var missing = new List<string>();
            foreach (string t in types)
            {
                foreach (string st in need)
                {
                    if (!SeenStates.Contains(t + ":" + st))
                    {
                        missing.Add(t + ":" + st);
                    }
                }
            }
            foreach (string st in new[] { "OutputBlocked", "Disabled", "Damaged", "NoPower" })
            {
                if (!SeenStates.Contains("parts_workshop:" + st))
                {
                    missing.Add("parts_workshop:" + st);
                }
            }
            if (!SeenStates.Contains("firmware_burner:OutputBlocked"))
            {
                missing.Add("firmware_burner:OutputBlocked");
            }
            Expect(missing.Count == 0, $"N1 FGT-ECO-002 制造建筑部分：四座建筑的待机 / 缺料 / 工作中都触发过，零件工坊另有输出堵塞 / 关停 / 损坏 / 缺电，刻录台有存放满的输出堵塞" +
                                      (missing.Count == 0 ? string.Empty : $"（没触发：{string.Join("、", missing)}）"));
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(9741, observe: false, scrap: 300);
            // 800 座制造建筑（测试捷径：只登记记录，不占格网——推进只读记录与配方）：电子组装台（两种固体）、组件工坊、刻录台（存放满了每步重试）、零件工坊各 200。
            string[] types = { "electronics_bench", "component_workshop", "firmware_burner", "parts_workshop" };
            var extra = new List<BuildingRecord>(800);
            GridCell core = HomeGridService.CorePivot(s);
            for (int i = 0; i < 800; i++)
            {
                extra.Add(new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":fgmfg_perf" + i.ToString("D4"),
                    BuildingTypeId = types[i % 4],
                    RegionId = HomeValleyLayout.RegionId,
                    GridX = core.X + 200 + (i % 40) * 5,
                    GridY = core.Y + 200 + (i / 40) * 5,
                    Position = new Vector2(core.X + 200 + (i % 40) * 5, core.Y + 200 + (i / 40) * 5),
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
            List<string> burnable = SignalCoreService.PrintableFirmware(s);
            for (int i = 0; i < extra.Count; i++)
            {
                BuildingRecord b = extra[i];
                ProductionService.Producer p = F.P(s, b);
                switch (i % 4)
                {
                    case 0:
                        ProductionService.TrySetRecipe(s, b.BuildingId, "electronic", out _);
                        ProductionService.Add(ref p.Rec.In, "alloy", 2);
                        ProductionService.Add(ref p.Rec.In, "rare_earth_ore", 2);
                        break;
                    case 1:
                        ProductionService.TrySetRecipe(s, b.BuildingId, "combat_component", out _);
                        ProductionService.Add(ref p.Rec.In, "part", 4);
                        break;
                    case 2:
                        ProductionService.TrySetBurnTarget(s, b.BuildingId, burnable.Count > 0 ? burnable[0] : string.Empty, out _);
                        ProductionService.Add(ref p.Rec.In, "chip_substrate", 2);
                        break;
                    default:
                        ProductionService.TrySetRecipe(s, b.BuildingId, "part", out _);
                        ProductionService.Add(ref p.Rec.In, "alloy", 2);
                        break;
                }
            }
            // 固件存放填满：刻录台每步重试开工都判“存放已满”（最坏情况：每座都要查存放）。
            int cap = PrimitiveInventory.CapacityOf(s);
            var fill = new List<PrimitiveChipRecord>();
            for (int i = PrimitiveInventory.BagCount(s); i < cap; i++)
            {
                fill.Add(new PrimitiveChipRecord { PartId = "perf_" + i, CardDefId = burnable.Count > 0 ? burnable[0] : "x", State = PrimitiveChipState.Bag, DraftSlot = -1, Origin = PrimitiveInventory.OriginSeed });
            }
            s.PrimitiveChips = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Concat(fill).ToArray();
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
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            samples.Sort();
            double p95 = samples[(int)(samples.Count * 0.95)];
            double max = samples[samples.Count - 1];
            // 装配站：一项缺料等待时每个模拟步的开销（指纹没变 → 直接跳过）与一次真实取料。
            AssemblyMaterials.SetScrapSubstitute(s, false);
            HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            HomeValleyFactory.Tick(s, 1f / 60f);
            GC.Collect();
            long qa0 = GC.GetAllocatedBytesForCurrentThread();
            var qsw = Stopwatch.StartNew();
            for (int i = 0; i < 600; i++)
            {
                HomeValleyFactory.Tick(s, 1f / 60f);
            }
            qsw.Stop();
            long qalloc = GC.GetAllocatedBytesForCurrentThread() - qa0;
            double perTick = qsw.Elapsed.TotalMilliseconds / 600.0;
            // 审查 P2：产线运转时仓库每步都有别的货进出（库存版本每步都变）——等料的队首只看自己要的材料，不应因此每步重新取料。
            ItemDef oreDef = ItemCatalog.Find("metal_ore");
            HomeInventory.Add(s, "metal_ore", 1);
            HomeValleyFactory.Tick(s, 1f / 60f);
            int rev0 = HomeInventory.Revision;
            GC.Collect();
            long qb0 = GC.GetAllocatedBytesForCurrentThread();
            var qsw2 = Stopwatch.StartNew();
            for (int i = 0; i < 600; i++)
            {
                if ((i & 1) == 0)
                {
                    HomeInventory.Add(s, "metal_ore", 1);
                }
                else
                {
                    HomeInventory.RemoveUpTo(s, oreDef, 1);
                }
                HomeInventory.Touch();
                HomeValleyFactory.Tick(s, 1f / 60f);
            }
            qsw2.Stop();
            long qballoc = GC.GetAllocatedBytesForCurrentThread() - qb0;
            double perTickBusy = qsw2.Elapsed.TotalMilliseconds / 600.0;
            int revSteps = HomeInventory.Revision - rev0;
            PerfLines.Add($"800 座制造建筑（四种各 200，刻录台存放满每步重试）每生产步（20 Hz，Editor batchmode）P95 {p95:F3} ms、最大 {max:F3} ms；400 步托管分配 {alloc} 字节；" +
                          $"装配站缺料等待每模拟步 {perTick * 1000:F1} 微秒、600 步分配 {qalloc} 字节；仓库每步有别的货进出（库存版本变了 {revSteps} 次）时每模拟步 {perTickBusy * 1000:F1} 微秒（含进出货本身）、600 步分配 {qballoc} 字节");
            // 阈值同 FG4-ECO-02：每生产步 ≤ 0.5 ms（Editor；真机热更层解释执行按 ×5 折算 ≤ 2.5 ms）；装配站等待每步 ≤ 0.05 ms、不分配（指纹没变直接跳过）。
            ExpectPerf(ProductionService.ProducerCount >= 800 && alloc < 64 * 1024 && qalloc < 8 * 1024 && qballoc < 8 * 1024 && revSteps >= 600,
                $"P1 性能：{ProductionService.ProducerCount} 座生产建筑每生产步 P95 {p95:F3} ms（阈值 0.5 ms）、最大 {max:F3} ms，400 步分配 {alloc} 字节（阈值 64 KB）；" +
                $"装配站缺料等待每模拟步 {perTick * 1000:F1} 微秒（阈值 50 微秒）、600 步分配 {qalloc} 字节（阈值 8 KB）；" +
                $"仓库每步进出别的货时 {perTickBusy * 1000:F1} 微秒（阈值 50 微秒）、分配 {qballoc} 字节（阈值 8 KB）",
                PerfGate.Le(p95, 0.5, "每生产步 P95 ms"), PerfGate.Le(perTick, 0.05, "装配站等待每步 ms"), PerfGate.Le(perTickBusy, 0.05, "装配站等待（仓库进出货）每步 ms"));
        }

        // ── 基础 ─────────────────────────────────────────────────────────────────

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
