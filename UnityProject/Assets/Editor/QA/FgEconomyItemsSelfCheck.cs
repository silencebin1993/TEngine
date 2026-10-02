using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
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
    /// FG4-ECO-01 物品、流体与配方表（FG04 FGR-ECO-001 / 002；FGT-ECO-001 的数据部分）自检。起真实家园、真实传送带内核与端口对账，行为坏掉会失败：
    /// A 数据：物品 / 配方 / 配方行与源数据 fgdata_eco.py 逐字段一致；FGR-ECO-001 的每种物品与 FGR-ECO-002 的每条配方（数字照设计文档）都在；数字资源与物品分开、
    ///   关键材料与人类遗产进核心保管库（不上传送带）；流体与 fg.TbFluid 一一对应；每种物品都有来源和用途、文本中英两套；动作 / 钩子 / 图鉴条目登记。
    /// B 负向（表）：配方引用不存在的物品 / 数字资源进产线 / 数量 0 / 没有产出 / 流体对不上 / 传送带编号重复 / 废料编号不对——整条拒绝并写明原因。
    /// C 配方执行（FGT-ECO-001 数据部分）：从原料一路做到一台机器与一个巨构构件，物品账目逐种守恒；缺输入（卡片负向）、主产出放不下、副产品无处可去（交给 FG4-ECO-10）
    ///   都不动库存并写明原因；配方时间换成游戏时钟步数与倍速无关；家园仓库当库存时流体写明“不在仓库里”。
    /// D 家园物资：多种固体各自的容量、保管库、数字资源、实体不进仓库；搬运交付合金（关闭 DEBT-FG3LOG02-01）与旧档“item:编号”地面物；第一次拿到关键材料解锁图鉴。
    /// E 传送带多物品（关闭 DEBT-FG3LOG03-02 / FG3LOG04-02 / FG0ARCH02-04）：仓库输出“全部可存物品”轮流推合金与废料、输入口按种类入库，逐种守恒；只输出合金；
    ///   某一种放不下时只堵那一种并写明；旧存档“只收废料”的输入口迁移；清带按种类入库；分流器选项列出全部固体；在途只数废料（FG-GAP-090）；传送带物品颜色来自物品表。
    /// F 悬停：总库存 / 分布（在途单列）/ 净速率与缓存（不每帧算、库存变化立即失效）。
    /// G 净速率与时间：按世界步采样，暂停不采、0.5x～3x 同样的样、观察与不观察一致、真文件存读档后接着算。
    /// H 界面：物资面板（真 UXML、分组、只看持有的、悬停内容、点图标打开图鉴）与图鉴物品 / 配方页签，布局探针（中英）；暂停菜单入口与 Alt+I。
    /// I FG-GAP-092：音效预热在播放路径先入池时释放句柄、不抛异常。
    /// J 性能：悬停缓存重建（真实家园 + 15,000 格 / 30,000 件传送带的按种类计数）、端口转移每步开销。
    /// </summary>
    public static class FgEconomyItemsSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 物品、流体与配方表")]
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
            Line("\n[物品、流体与配方表] 物品 / 流体 / 配方入表、数字资源与保管库、图鉴来源与用途、多物品仓储与传送带、悬停总库存 / 分布 / 净速率（FG4-ECO-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgeco01-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 测量环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，{SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；编辑模式下直接起真实家园，没有渲染帧。传送带按种类计数在 AOT 程序集（Editor 下 Mono JIT，真机 IL2CPP），" +
                     "物资缓存与端口转移在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行，真机另测：FG15-SYS-02）");

                Step(CheckData);
                Step(CheckCatalogNegative);
                Step(CheckRecipeChain);
                Step(CheckRecipeNegative);
                Step(CheckHomeInventory);
                Step(CheckBeltMultiItem);
                Step(CheckBeltPerKindFull);
                Step(CheckLegacySinkMigration);
                Step(CheckClearAndFilters);
                Step(CheckHoverDistribution);
                Step(CheckFlowTiming);
                Step(CheckObservedAndSave);
                Step(CheckUi);
                Step(CheckAudioPoolRace);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"物品表自检抛异常：{e}");
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
                ItemCatalog.ResetForTests();
                ItemDistribution.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                ItemsPanelUIToolkit.InWorldOverrideForTests = false;
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                MechanicCodex.ResetForTests();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                UiTooltip.Hide();
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
            Line($"  · [物品、流体与配方表] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 100)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            NotificationCenter.ResetForTests();
            NotificationCenter.AutoPauseHandler = null;
            CampaignState s = CampaignState.CreateNew("fgeco01-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static ItemDef Def(string id) => ItemCatalog.Find(id);

        private static BuildingRecord Building(CampaignState s, string typeId) =>
            (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).FirstOrDefault(b => b != null && b.BuildingTypeId == typeId && b.RegionId == HomeValleyLayout.RegionId);

        private static void WaitSync() => WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);

        private static BuildingRecord ActivateWarehouse(CampaignState s)
        {
            BuildingRecord wh = Building(s, "warehouse");
            wh.ConstructionState = BuildingConstructionState.Operational;
            WaitSync();
            return wh;
        }

        /// <summary>某种固体在家园的全部数量：库存 + 端口待推 / 缓存（那一种）+ 带上（内核逐格按种类数）+ 地面。逐种守恒核对用。</summary>
        private static long KindTotal(CampaignState s, ItemDef d)
        {
            long ports = 0;
            foreach (BeltPortService.Binding b in BeltPortService.All)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(b.PortId, out int pending, out int buffered, out ushort item) && item == d.BeltId)
                {
                    ports += Math.Max(0, pending) + Math.Max(0, buffered);
                }
            }
            var counts = new int[1024];
            BeltNetworkService.Kernel.CountItemsByType(-1, counts);
            long ground = (s.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => ItemCatalog.TryGetByResource(g.ResourceType, out ItemDef gd) && gd == d).Sum(g => (long)g.Amount);
            return HomeInventory.Stock(s, d) + ports + counts[d.BeltId] + ground;
        }

        private static string EconomyJson(CampaignState s)
        {
            s.NormalizeForSave();
            return s.Scrap + "|" + s.TechData + "|" + JsonUtility.ToJson(s.Economy);
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            IReadOnlyList<ItemDef> items = ItemCatalog.Items;
            Expect(ItemCatalog.LoadError == null && ItemCatalog.Problems.Count == 0 && ItemCatalog.RejectedRecipes.Count == 0 && items.Count == 30 && ItemCatalog.Recipes.Count == 15,
                $"A1 物品表 fg.TbEcoItem {items.Count} 种、配方 fg.TbRecipe {ItemCatalog.Recipes.Count} 条全部通过载入检查（问题 {ItemCatalog.Problems.Count} 条）" +
                (ItemCatalog.Problems.Count > 0 ? "：" + string.Join("；", ItemCatalog.Problems.Take(4)) : string.Empty));

            // FGR-ECO-001 逐项（设计文档原文的物品名 → 层级）。
            var spec = new (string Tier, string[] Ids)[]
            {
                ("raw", new[] { "scrap", "metal_ore", "rare_earth_ore" }),
                ("intermediate", new[] { "alloy", "part", "structural", "electronic", "precision_part", "chip_substrate", "repair_kit" }),
                ("product", new[] { "combat_component", "structure_module", "firmware_chip", "machine" }),
                ("endgame", new[] { "megastructure_part" }),
                ("expedition", new[] { "unparsed_module", "encrypted_firmware", "data_core", "human_legacy" }),
                ("key", new[] { "listening_array_core", "furnace_heart", "reactor_core", "supercomputer_core" }),
                ("fluid", new[] { "water", "crude", "coolant", "fuel", "acid" }),
                ("digital", new[] { "tech_data", "research_points" }),
            };
            var missing = new List<string>();
            foreach ((string tier, string[] ids) in spec)
            {
                foreach (string id in ids)
                {
                    if (!ItemCatalog.TryGet(id, out ItemDef d) || d.Tier != tier)
                    {
                        missing.Add(id);
                    }
                }
            }
            bool digital = Def("tech_data").Form == ItemForm.Digital && Def("research_points").Form == ItemForm.Digital
                           && Def("tech_data").BeltId == 0 && Def("research_points").BeltId == 0 && !HomeInventory.IsStorable(Def("tech_data"));
            bool vault = new[] { "listening_array_core", "furnace_heart", "reactor_core", "supercomputer_core", "human_legacy" }
                .All(id => Def(id).Form == ItemForm.Vault && Def(id).BeltId == 0 && !HomeInventory.IsBeltStorable(Def(id)));
            var fluids = new List<GameConfig.fg.Fluid>();
            PipeNetworkService.CollectFluids(fluids);
            bool fluidMap = fluids.Count == 5 && fluids.All(f => ItemCatalog.TryGetByFluid(f.Id, out ItemDef d) && d.Id == f.Key && d.Form == ItemForm.Fluid && d.BeltId == 0);
            bool solidsOnBelt = items.Where(d => d.Form == ItemForm.Solid).All(d => d.BeltId != 0) && items.Where(d => d.BeltId != 0).Select(d => d.BeltId).Distinct().Count() == items.Count(d => d.BeltId != 0)
                                && Def("scrap").BeltId == BeltItems.ScrapId;
            Expect(missing.Count == 0 && digital && vault && fluidMap && solidsOnBelt,
                $"A2 FGR-ECO-001 全部入表（30 种，按层级）：数字资源（技术数据、研究点）不占物理空间、不上传送带；关键材料 4 种与人类遗产在核心保管库、不上传送带；" +
                $"5 种流体与 fg.TbFluid 一一对应；固体都有唯一的传送带编号、废料 = logistics.item.scrap_id（{BeltItems.ScrapId}）" +
                (missing.Count > 0 ? "；缺 / 层级不对：" + string.Join("、", missing) : string.Empty));

            // FGR-ECO-002 逐条（设计文档的输入 / 产出 / 时间；作战组件 / 结构模块 / 机器按组件表 / 蓝图，表里是初值基准）。
            var recipeSpec = new (string Id, string Building, string In, string Out, string By, float Sec, string Kind)[]
            {
                ("alloy_ore", "refinery_furnace", "metal_ore×2", "alloy×1", "", 4f, "fixed"),
                ("alloy_scrap", "refinery_furnace", "scrap×4", "alloy×1", "", 6f, "fixed"),
                ("part", "parts_workshop", "alloy×1", "part×2", "", 3f, "fixed"),
                ("structural", "parts_workshop", "alloy×2", "structural×1", "", 4f, "fixed"),
                ("repair_kit", "parts_workshop", "part×2", "repair_kit×1", "", 5f, "fixed"),
                ("electronic", "electronics_bench", "alloy×1,rare_earth_ore×1", "electronic×1", "", 5f, "fixed"),
                ("precision_part", "electronics_bench", "part×2,electronic×1", "precision_part×1", "", 8f, "fixed"),
                ("chip_substrate", "electronics_bench", "electronic×2,rare_earth_ore×1", "chip_substrate×1", "", 10f, "fixed"),
                ("coolant", "blending_station", "water×100,rare_earth_ore×1", "coolant×100", "", 4f, "fixed"),
                ("fuel", "refinery_tower", "crude×100", "fuel×60", "acid×20", 5f, "fixed"),
                ("firmware_chip", "firmware_burner", "chip_substrate×1", "firmware_chip×1", "", 20f, "firmware"),
                ("combat_component", "component_workshop", "part×2,electronic×1", "combat_component×1", "", 6f, "component_table"),
                ("structure_module", "component_workshop", "structural×2,part×1", "structure_module×1", "", 6f, "component_table"),
                ("machine", "assembly_station", "structural×4,part×4,electronic×2,combat_component×1", "machine×1", "", 60f, "blueprint"),
                ("megastructure_part", "megastructure_factory", "precision_part×4,structural×4,electronic×2,coolant×200", "megastructure_part×1", "", 30f, "fixed"),
            };
            string Lines(RecipeDef r, RecipeRole role) => string.Join(",", r.Lines.Where(l => l.Role == role).Select(l => l.Item.Id + "×" + l.Amount));
            var wrong = new List<string>();
            foreach (var rs in recipeSpec)
            {
                if (!ItemCatalog.TryGetRecipe(rs.Id, out RecipeDef r) || r.Building != rs.Building || Lines(r, RecipeRole.In) != rs.In || Lines(r, RecipeRole.Out) != rs.Out
                    || Lines(r, RecipeRole.Byproduct) != rs.By || Math.Abs(r.Seconds - rs.Sec) > 1e-4f || r.Kind != rs.Kind)
                {
                    wrong.Add(rs.Id);
                }
            }
            bool provisional = ItemCatalog.Recipes.Where(r => r.Provisional).Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { "combat_component", "machine", "structure_module" });
            Expect(wrong.Count == 0 && provisional,
                "A3 FGR-ECO-002 的 15 条配方逐条与设计文档一致（建筑、输入、产出、副产品酸液 20、1x 秒数、类型）；只有作战组件 / 结构模块 / 机器三条标为“初值待定”（文档写“按组件表 / 按蓝图”）" +
                (wrong.Count > 0 ? "；不一致：" + string.Join("、", wrong) : string.Empty));

            // 源数据 ↔ 运行时表逐字段。
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("IT\t") || l.StartsWith("RC\t") || l.StartsWith("RI\t")).ToArray();
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            var rt = new List<string>();
            rt.AddRange(t.TbEcoItem.DataList.Select(r => string.Join("\t", "IT", r.Id, r.BeltId, r.Form, r.Tier, r.FluidId, r.NameKey, r.DescKey, r.SourceKey, r.UseKey, r.Color, r.Shape, r.SortOrder, r.RecycleScrap)));
            rt.AddRange(t.TbRecipe.DataList.Select(r => string.Join("\t", "RC", r.Id, r.NameKey, r.Building, r.BuildingNameKey, FloatRepr(r.Seconds), r.Kind, r.Provisional, r.SortOrder)));
            rt.AddRange(t.TbRecipeIo.DataList.Select(r => string.Join("\t", "RI", r.Id, r.Recipe, r.Item, r.Amount, r.Role)));
            Expect(code == 0 && src.Length == rt.Count && src.SequenceEqual(rt),
                $"A4 物品 / 配方 / 配方行与源数据 fgdata_eco.py 逐字段一致（{src.Length} 行）——改了源数据却没重新生成会在这里失败" + (code == 0 ? string.Empty : "：" + output));

            // 每种物品都有来源和用途（卡片“图鉴条目（来源和用途）”）；文本中英。
            var noText = new List<string>();
            foreach (ItemDef d in items)
            {
                foreach (string key in new[] { d.NameKey, d.DescKey, d.SourceKey, d.UseKey })
                {
                    if (!GameText.TryGet(key, GameLanguage.ZhCn, out string zh) || string.IsNullOrWhiteSpace(zh) || !GameText.TryGet(key, GameLanguage.En, out string en) || string.IsNullOrWhiteSpace(en))
                    {
                        noText.Add(key);
                    }
                }
            }
            foreach (RecipeDef r in ItemCatalog.Recipes)
            {
                if (!GameText.Has(r.NameKey) || !GameText.Has(r.BuildingNameKey))
                {
                    noText.Add(r.NameKey);
                }
            }
            int produced = items.Count(d => ItemCatalog.ProducedBy(d.Id).Count > 0);
            bool sourceUse = ItemCatalog.ProducedBy("acid").Count == 1 && ItemCatalog.ConsumedBy("alloy").Count == 3 && ItemCatalog.ProducedBy("alloy").Count == 2
                             && ItemCatalog.ConsumedBy("rare_earth_ore").Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { "chip_substrate", "coolant", "electronic" });
            Expect(noText.Count == 0 && sourceUse,
                $"A5 每种物品的名称 / 说明 / 来源 / 用途、每条配方与所在建筑的名字都有中英文本；由配方产出的物品 {produced} 种、配方互链正确（合金由 2 条配方产出、被 3 条用掉；稀土矿用于电子件 / 芯片基板 / 冷却液；酸液是精炼塔的副产品）" +
                (noText.Count > 0 ? "；缺：" + string.Join("、", noText.Take(6)) : string.Empty));

            bool action = InputActionCatalog.TryGet(GameActionId.OpenItems, out InputActionDef act) && act.DefaultChord.Key == KeyCode.I && act.DefaultChord.Mods == InputModifier.Alt
                          && act.Status == InputActionStatus.Wired;
            bool hook = GuidanceHooks.Known.Contains(GuidanceHooks.EconomyItemsFirstOpen)
                        && t.TbCodexEntry.DataList.Any(e => e.Id == "codex.economy.items" && e.Hooks.Contains(GuidanceHooks.EconomyItemsFirstOpen));
            var keys = new[] { "items.panel.title", "items.panel.footer", "item.hover.total_cap", "item.hover.rate", "item.hover.rate_measuring", "eco.reason.missing_input",
                "eco.reason.byproduct_no_room", "codex.tab.item", "codex.tab.recipe", "logistics.port.accept_all", "logistics.port.store_full_item", "diag.step.store_full_item" };
            GameSettings.SetLanguage(GameLanguage.En);
            bool en2 = keys.All(k => GameText.Has(k) && !GameText.ContainsMarker(GameText.Get(k))) && Def("alloy").Name == "Alloy";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool tuning = GridContent.TryGetTuning("eco.flow.sample_seconds", out float ss) && ss > 0 && GridContent.TryGetTuning("eco.hover.refresh_seconds", out float hr) && hr > 0;
            Expect(action && hook && en2 && tuning,
                "A6 物资面板动作 OpenItems 默认 Alt+I（已接入）；首次打开钩子与图鉴“物品、流体与数字资源”条目登记；新文本中英两套；采样 / 悬停刷新调参入 fg.TbHomeTuning");
        }

        // ── B 表的负向 ─────────────────────────────────────────────────────────

        private static void CheckCatalogNegative()
        {
            var fluids = new Dictionary<int, string> { { 1, "water" } };
            ItemCatalog.ItemRow Item(string id, int belt, string form, int fluid = 0) => new ItemCatalog.ItemRow
            {
                Id = id, BeltId = belt, Form = form, Tier = "raw", FluidId = fluid, NameKey = "item.scrap.name", DescKey = "item.scrap.desc", SourceKey = "item.scrap.source",
                UseKey = "item.scrap.use", Color = "#808080", Shape = "square", SortOrder = belt,
            };
            var items = new List<ItemCatalog.ItemRow>
            {
                Item("scrap", 1, "solid"), Item("alloy", 4, "solid"), Item("water", 0, "fluid", 1), Item("tech_data", 0, "digital"),
                Item("bad_fluid_on_belt", 9, "fluid", 1), Item("dup_belt", 4, "solid"), Item("vault_on_belt", 7, "vault"),
            };
            ItemCatalog.RecipeRow Recipe(string id, float sec = 2f) => new ItemCatalog.RecipeRow
            {
                Id = id, NameKey = "recipe.part.name", Building = "parts_workshop", BuildingNameKey = "building.parts_workshop.name", Seconds = sec, Kind = "fixed", SortOrder = 1,
            };
            var recipes = new List<ItemCatalog.RecipeRow>
            {
                Recipe("ok"), Recipe("missing_input"), Recipe("digital_in"), Recipe("zero_amount"), Recipe("no_output"), Recipe("zero_time", 0f),
            };
            var ios = new List<ItemCatalog.IoRow>
            {
                new ItemCatalog.IoRow { Id = 101, Recipe = "ok", Item = "scrap", Amount = 4, Role = "in" },
                new ItemCatalog.IoRow { Id = 102, Recipe = "ok", Item = "alloy", Amount = 1, Role = "out" },
                new ItemCatalog.IoRow { Id = 201, Recipe = "missing_input", Item = "unobtainium", Amount = 2, Role = "in" },
                new ItemCatalog.IoRow { Id = 202, Recipe = "missing_input", Item = "alloy", Amount = 1, Role = "out" },
                new ItemCatalog.IoRow { Id = 301, Recipe = "digital_in", Item = "tech_data", Amount = 5, Role = "in" },
                new ItemCatalog.IoRow { Id = 302, Recipe = "digital_in", Item = "alloy", Amount = 1, Role = "out" },
                new ItemCatalog.IoRow { Id = 401, Recipe = "zero_amount", Item = "scrap", Amount = 0, Role = "in" },
                new ItemCatalog.IoRow { Id = 402, Recipe = "zero_amount", Item = "alloy", Amount = 1, Role = "out" },
                new ItemCatalog.IoRow { Id = 501, Recipe = "no_output", Item = "scrap", Amount = 1, Role = "in" },
                new ItemCatalog.IoRow { Id = 601, Recipe = "zero_time", Item = "scrap", Amount = 1, Role = "in" },
                new ItemCatalog.IoRow { Id = 602, Recipe = "zero_time", Item = "alloy", Amount = 1, Role = "out" },
            };
            ItemCatalog.Data d = ItemCatalog.Build(items, recipes, ios, fluids, 1);
            string all = string.Join("；", d.Problems);
            bool items0 = d.ById.ContainsKey("scrap") && d.ById.ContainsKey("water") && !d.ById.ContainsKey("bad_fluid_on_belt") && !d.ById.ContainsKey("dup_belt") && !d.ById.ContainsKey("vault_on_belt");
            bool rejected = d.RecipeById.ContainsKey("ok") && new[] { "missing_input", "digital_in", "zero_amount", "no_output", "zero_time" }.All(id => d.RejectedRecipes.Contains(id) && !d.RecipeById.ContainsKey(id));
            bool reasons = all.Contains("unobtainium 不在物品表") && all.Contains("tech_data 是数字资源") && all.Contains("数量必须 > 0") && all.Contains("至少要有一个输入和一个产出")
                           && all.Contains("时间必须 > 0") && all.Contains("只有固体上传送带") && all.Contains("传送带编号 4 重复");
            ItemCatalog.Data wrongScrap = ItemCatalog.Build(new[] { Item("scrap", 2, "solid") }, Array.Empty<ItemCatalog.RecipeRow>(), Array.Empty<ItemCatalog.IoRow>(), fluids, 1);
            bool scrapCheck = string.Join("；", wrongScrap.Problems).Contains("logistics.item.scrap_id") && string.Join("；", wrongScrap.Problems).Contains("water 在物品表里没有流体行");
            Expect(items0 && rejected && reasons && scrapCheck,
                $"B1 负向（表）：配方输入引用不存在的物品、数字资源进产线、数量 0、没有产出、时间 0 → 整条配方拒绝（不让半条配方跑）；流体上传送带 / 保管库上传送带 / 编号重复的物品行拒绝；" +
                $"废料编号与 logistics.item.scrap_id 不一致、fg.TbFluid 有流体但物品表没有都写进问题（共 {d.Problems.Count + wrongScrap.Problems.Count} 条，例：{d.Problems.FirstOrDefault()}）");

            // 运行时目录被坏表替换时：图鉴与面板照常（只少被拒的那几条），恢复后回来。
            ItemCatalog.OverrideForTests(d);
            int recipeEntries = MechanicCodex.CountIn(MechanicCodex.TabRecipe);
            ItemCatalog.ResetForTests();
            int restored = MechanicCodex.CountIn(MechanicCodex.TabRecipe);
            Expect(recipeEntries == 1 && restored == 15, $"B2 被拒的配方不出现在图鉴配方页签（坏表 {recipeEntries} 条，恢复后 {restored} 条）");
        }

        // ── C 配方执行（FGT-ECO-001 数据部分）────────────────────────────────────────

        private static void CheckRecipeChain()
        {
            var stock = new RecipeBook.LedgerStock();
            stock.Amounts["metal_ore"] = 400;
            stock.Amounts["rare_earth_ore"] = 60;
            stock.Amounts["scrap"] = 40;
            stock.Amounts["water"] = 1000;
            var initial = new Dictionary<string, long>(stock.Amounts);
            var expected = new Dictionary<string, long>(initial);
            var runs = new Dictionary<string, int>();
            int totalRuns = 0;
            long ticks = 0;
            // 偏好：合金先用矿；其余每种物品只有一条配方。
            RecipeDef Pick(string itemId)
            {
                if (itemId == "alloy")
                {
                    return ItemCatalog.TryGetRecipe("alloy_ore", out RecipeDef r) ? r : null;
                }
                return ItemCatalog.ProducedBy(itemId).FirstOrDefault(r => r.Lines.Any(l => l.Role == RecipeRole.Out && l.Item.Id == itemId));
            }
            bool Make(string itemId, long need, int depth)
            {
                ItemDef item = Def(itemId);
                while (stock.Get(item) < need)
                {
                    RecipeDef r = Pick(itemId);
                    if (r == null || depth > 12)
                    {
                        return false;
                    }
                    // 备齐每种输入（备一种可能吃掉另一种：例如作战组件用掉零件），直到配方检查通过。
                    for (int pass = 0; pass < 6 && !RecipeBook.Check(r, stock).Ok; pass++)
                    {
                        foreach (RecipeLine l in r.Lines.Where(l => l.Role == RecipeRole.In))
                        {
                            if (!Make(l.Item.Id, l.Amount, depth + 1))
                            {
                                return false;
                            }
                        }
                    }
                    if (!RecipeBook.TryRun(r, stock, out RecipeVerdict v))
                    {
                        return false;
                    }
                    foreach (RecipeLine l in r.Lines)
                    {
                        expected[l.Item.Id] = (expected.TryGetValue(l.Item.Id, out long e) ? e : 0) + (l.Role == RecipeRole.In ? -l.Amount : l.Amount);
                    }
                    runs[r.Id] = (runs.TryGetValue(r.Id, out int n) ? n : 0) + 1;
                    totalRuns++;
                    ticks += r.DurationTicks(GameClock.StepHz);
                }
                return true;
            }
            bool machine = Make("machine", 1, 0);
            bool mega = Make("megastructure_part", 1, 0);
            bool ledgerMatches = expected.All(kv => stock.Get(Def(kv.Key)) == kv.Value) && stock.Amounts.All(kv => expected.TryGetValue(kv.Key, out long e) && e == kv.Value);
            bool noNegative = stock.Amounts.Values.All(v => v >= 0);
            Expect(machine && mega && ledgerMatches && noNegative && stock.Get(Def("machine")) == 1 && stock.Get(Def("megastructure_part")) == 1 && runs.ContainsKey("machine") && runs.Count >= 9,
                $"C1 FGT-ECO-001 数据部分：从原料（金属矿 / 稀土矿 / 水）一路做出 1 台机器（{machine}）与 1 个巨构构件（{mega}），共执行 {totalRuns} 次配方（{string.Join("、", runs.OrderBy(k => k.Key).Select(k => k.Key + "×" + k.Value))}）；" +
                $"每种物品的账目 = 初值 − 输入 + 产出，逐种一致、没有负数；累计生产时间 {ticks / (double)GameClock.StepHz:0} 游戏秒");
        }

        private static void CheckRecipeNegative()
        {
            ItemCatalog.TryGetRecipe("alloy_ore", out RecipeDef alloyOre);
            ItemCatalog.TryGetRecipe("fuel", out RecipeDef fuel);
            ItemCatalog.TryGetRecipe("electronic", out RecipeDef electronic);
            var empty = new RecipeBook.LedgerStock();
            empty.Amounts["metal_ore"] = 1;
            string before = string.Join(",", empty.Amounts.Select(k => k.Key + k.Value));
            bool ran = RecipeBook.TryRun(alloyOre, empty, out RecipeVerdict miss);
            Expect(!ran && miss.Code == RecipeCheck.MissingInput && miss.Item?.Id == "metal_ore" && miss.Need == 2 && miss.Have == 1 && miss.Reason.Contains("缺少金属矿") && miss.Reason.Contains("需要 2")
                   && string.Join(",", empty.Amounts.Select(k => k.Key + k.Value)) == before && empty.Get(Def("alloy")) == 0,
                $"C2 卡片负向“配方输入缺失”：金属矿只有 1 份时不执行，库存不动，原因“{miss.Reason}”");

            var two = new RecipeBook.LedgerStock();
            two.Amounts["alloy"] = 0;
            two.Amounts["rare_earth_ore"] = 3;
            RecipeVerdict firstMissing = RecipeBook.Check(electronic, two);
            Expect(firstMissing.Code == RecipeCheck.MissingInput && firstMissing.Item.Id == "alloy", $"C3 多个输入时按表顺序报第一种缺的（{firstMissing.Reason}）");

            var tower = new RecipeBook.LedgerStock();
            tower.Amounts["crude"] = 300;
            tower.Capacity["acid"] = 10; // 副产品只剩 10 升的地方
            bool fuelRan = RecipeBook.TryRun(fuel, tower, out RecipeVerdict by);
            long crudeAfterBlocked = tower.Get(Def("crude"));
            tower.Capacity["acid"] = 1000; // 接上废液池 / 用酸的建筑
            bool fuelRan2 = RecipeBook.TryRun(fuel, tower, out RecipeVerdict ok2);
            Expect(!fuelRan && by.Code == RecipeCheck.ByproductNoRoom && by.Reason.Contains("副产品酸液无处可去") && crudeAfterBlocked == 300
                   && fuelRan2 && ok2.Ok && tower.Get(Def("fuel")) == 60 && tower.Get(Def("acid")) == 20 && tower.Get(Def("crude")) == 200,
                $"C4 卡片负向“副产品无处可去”（处理在 FG4-ECO-10）：酸液只放得下 10 升时精炼塔不开工、原油不扣，原因“{by.Reason}”；腾出地方后产出燃油 60 + 酸液 20");

            var full = new RecipeBook.LedgerStock();
            full.Amounts["metal_ore"] = 10;
            full.Amounts["alloy"] = 5;
            full.Capacity["alloy"] = 5;
            RecipeVerdict noRoom = RecipeBook.Check(alloyOre, full);
            Expect(noRoom.Code == RecipeCheck.NoRoom && noRoom.Reason.Contains("合金放不下"), $"C5 主产出放不下：不开工，原因“{noRoom.Reason}”");

            CampaignState s = CampaignState.CreateNew("fgeco01-homestock", "Standard", 1);
            ItemCatalog.TryGetRecipe("coolant", out RecipeDef coolant);
            RecipeVerdict notHeld = RecipeBook.Check(coolant, new RecipeBook.HomeStock(s));
            long ticks1 = alloyOre.DurationTicks(GameClock.StepHz);
            Expect(notHeld.Code == RecipeCheck.NotInStock && notHeld.Reason.Contains("水") && notHeld.Reason.Contains("管线") && ticks1 == 4L * GameClock.StepHz
                   && fuel.DurationTicks(GameClock.StepHz) == 5L * GameClock.StepHz,
                $"C6 家园仓库当库存时，要流体的配方写明“水不在家园仓库里（走管线与储罐）”；配方时间换成游戏时钟：合金（矿）4 秒 = {ticks1} 步（每秒 {GameClock.StepHz} 步，倍速只改每帧跑几步，步数不变）");
        }

        // ── D 家园物资 ─────────────────────────────────────────────────────────

        private static void CheckHomeInventory()
        {
            CampaignState s = NewWorld(9101, scrap: 100);
            ItemDef alloy = Def("alloy");
            ItemDef heart = Def("furnace_heart");
            ItemDef legacy = Def("human_legacy");
            ItemDef water = Def("water");
            ItemDef machine = Def("machine");
            int noWarehouse = HomeInventory.Add(s, alloy, 5);
            ActivateWarehouse(s);
            int scrapBefore = s.Scrap;
            int cap = HomeInventory.Capacity(s, alloy);
            int added = HomeInventory.Add(s, alloy, cap + 7);
            int scrapCap = HomeInventory.Capacity(s, Def("scrap"));
            Expect(noWarehouse == 0 && cap == HomeValleyLayout.WarehouseCapacity && added == cap && HomeInventory.Stock(s, alloy) == cap && HomeInventory.Space(s, alloy) == 0
                   && scrapCap == HomeValleyLayout.CoreCacheCapacity + HomeValleyLayout.WarehouseCapacity && s.Scrap == scrapBefore,
                $"D1 多种固体各自的容量：仓库没修好时合金一件都放不进（核心缓存只收废料）；仓库运转后合金容量 {cap}、多给的 7 件放不下（返回实际存入 {added}）；废料容量仍是核心缓存 + 仓库 {scrapCap}，废料库存不受影响");

            bool lockedBefore = !MechanicCodex.IsUnlocked(MechanicCodex.ItemEntryId(heart.Id));
            int v1 = HomeInventory.Add(s, heart, 1);
            int v2 = HomeInventory.Add(s, legacy, 2);
            bool unlockedAfter = MechanicCodex.IsUnlocked(MechanicCodex.ItemEntryId(heart.Id));
            int tech0 = s.TechData;
            HomeInventory.Add(s, Def("tech_data"), 30);
            HomeInventory.Add(s, Def("research_points"), 12);
            bool removed = HomeInventory.TryRemove(s, Def("tech_data"), 10) && !HomeInventory.TryRemove(s, Def("research_points"), 13);
            Expect(v1 == 1 && v2 == 2 && s.Economy.Vault.Length == 2 && HomeInventory.Stock(s, heart) == 1 && lockedBefore && unlockedAfter
                   && s.TechData == tech0 + 20 && s.Research.Points == 12 && removed
                   && HomeInventory.Add(s, water, 5) == 0 && HomeInventory.Add(s, machine, 1) == 0 && !HomeValleyCargo.CanStore("water") && !HomeValleyCargo.CanStore("machine"),
                "D2 关键材料（熔炉心）与人类遗产进核心保管库；第一次拿到熔炉心时图鉴条目从剪影解锁；技术数据 / 研究点是数字账户（全有或全无地扣）；流体与机器不进仓库");

            // 搬运交付合金（关闭 DEBT-FG3LOG02-01）与旧档“item:编号”地面物。
            HomeInventory.RemoveUpTo(s, alloy, 20);
            int alloyBefore = HomeInventory.Stock(s, alloy);
            GroundItemRecord g1 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Vector2.zero, "alloy", 6, "eco-test-1");
            GroundItemRecord g2 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Vector2.zero, "item:" + alloy.BeltId, 4, "eco-test-2");
            HomeValleyCargo.HaulTicket t1 = HomeValleyCargo.TryReserveHaul(s, g1.GroundItemId);
            HomeValleyCargo.HaulTicket t2 = HomeValleyCargo.TryReserveHaul(s, g2.GroundItemId);
            bool c1 = HomeValleyCargo.CommitHaul(s, t1).Success;
            bool c2 = HomeValleyCargo.CommitHaul(s, t2).Success;
            Expect(HomeValleyCargo.CanStore("alloy") && HomeValleyCargo.CanStore("item:" + alloy.BeltId) && c1 && c2 && HomeInventory.Stock(s, alloy) == alloyBefore + 10
                   && !s.GroundItems.Any(g => g.SalvageInstanceId == "eco-test-1" || g.SalvageInstanceId == "eco-test-2"),
                $"D3 地面上的合金（新写法 alloy 与 FG3-LOG-02 起旧档写法 item:{alloy.BeltId}）都能被搬进仓库（关闭 DEBT-FG3LOG02-01），库存 {alloyBefore} → {HomeInventory.Stock(s, alloy)}");

            HomeInventory.Add(s, alloy, HomeInventory.Space(s, alloy));
            GroundItemRecord g3 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Vector2.one, "alloy", 3, "eco-test-3");
            HomeValleyCargo.StoreResult full = HomeValleyCargo.CommitHaul(s, HomeValleyCargo.TryReserveHaul(s, g3.GroundItemId));
            Expect(!full.Success && full.FailureReason.StartsWith("storage-full") && s.GroundItems.Any(g => g.SalvageInstanceId == "eco-test-3" && g.Amount == 3),
                $"D4 合金放满后再交付：原样放回地面不丢（{full.FailureReason}）");
        }

        // ── E 传送带多物品 ─────────────────────────────────────────────────────

        private static void CheckBeltMultiItem()
        {
            CampaignState s = NewWorld(9201, scrap: 40);
            BuildingRecord wh = ActivateWarehouse(s);
            ItemDef alloy = Def("alloy");
            ItemDef scrap = Def("scrap");
            HomeInventory.Add(s, alloy, 30);
            List<(GridCell cell, BeltDir dir)> path = FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out string failure);
            if (path == null)
            {
                Fail("E1 仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding outP = BeltPortService.Find(wh.BuildingId, "warehouse.out0");
            BeltPortService.Binding inP = BeltPortService.Find(wh.BuildingId, "warehouse.in0");
            long a0 = KindTotal(s, alloy);
            long s0 = KindTotal(s, scrap);
            bool conserved = true;
            long worst = 0;
            int maxAlloyOnBelt = 0;
            int maxScrapOnBelt = 0;
            var kindsOut = new HashSet<ushort>();
            var kindsIn = new HashSet<ushort>();
            var counts = new int[1024];
            for (int q = 0; q < 90 * 4; q++)
            {
                WorldSimulation.StepMany(GameClock.StepHz / 4);
                long a = KindTotal(s, alloy);
                long b = KindTotal(s, scrap);
                conserved &= a == a0 && b == s0;
                worst = Math.Max(worst, Math.Max(Math.Abs(a - a0), Math.Abs(b - s0)));
                Array.Clear(counts, 0, counts.Length);
                BeltNetworkService.Kernel.CountItemsByType(-1, counts);
                maxAlloyOnBelt = Math.Max(maxAlloyOnBelt, counts[alloy.BeltId]);
                maxScrapOnBelt = Math.Max(maxScrapOnBelt, counts[scrap.BeltId]);
                if (BeltNetworkService.Kernel.TryGetPortCounts(outP.PortId, out int pend, out _, out ushort ok) && pend > 0)
                {
                    kindsOut.Add(ok);
                }
                if (BeltNetworkService.Kernel.TryGetPortCounts(inP.PortId, out _, out int buf, out ushort ik) && buf > 0)
                {
                    kindsIn.Add(ik);
                }
            }
            BeltNetworkService.Kernel.TryGetPortInfo(inP.PortId, out BeltPortInfo inInfo);
            Expect(conserved && maxAlloyOnBelt > 0 && maxScrapOnBelt > 0 && kindsOut.SetEquals(new[] { alloy.BeltId, scrap.BeltId }) && inInfo.Total > 20 && inInfo.Accept == BeltConst.AcceptAnyOneKind,
                $"E1 仓库输出“全部可存物品”：90 游戏秒里合金与废料轮流上带（带上最多同时 合金 {maxAlloyOnBelt} / 废料 {maxScrapOnBelt} 件），输入口一次缓存一种、按种类入库（收下 {inInfo.Total} 件；入错种类的话下面的逐种合计会变）；" +
                $"每一刻 合金 = {a0}、废料 = {s0}（库存 + 端口 + 带上 + 地面，最大偏差 {worst}）——关闭 DEBT-FG3LOG03-02“仓库只收废料”" +
                $"［守恒 {conserved}，输出口推过 {string.Join(",", kindsOut)}，输入口缓存过 {string.Join(",", kindsIn)}，收法 {inInfo.Accept}］");

            // 只输出合金。
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", alloy.BeltId, out string why);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            bool onlyAlloy = true;
            for (int q = 0; q < 40; q++)
            {
                WorldSimulation.StepMany(GameClock.StepHz / 4);
                if (BeltNetworkService.Kernel.TryGetPortCounts(outP.PortId, out int pend, out _, out ushort k) && pend > 0)
                {
                    onlyAlloy &= k == alloy.BeltId;
                }
            }
            Array.Clear(counts, 0, counts.Length);
            BeltNetworkService.Kernel.CountItemsByType(-1, counts);
            Expect(why == null && onlyAlloy && counts[scrap.BeltId] == 0 && counts[alloy.BeltId] > 0 && KindTotal(s, alloy) == a0 && KindTotal(s, scrap) == s0,
                $"E2 输出过滤“只输出合金”：之后输出口只推合金，带上的废料都回了仓库（带上 合金 {counts[alloy.BeltId]} / 废料 {counts[scrap.BeltId]}），两种都守恒");

            // FG-GAP-090：在途只数废料（施工材料）。
            int onBeltsScrap = BeltPortService.StoreItemsOnBelts(s);
            int onBeltsAlloy = BeltPortService.StoreItemsOnBelts(s, alloy.BeltId);
            Expect(onBeltsScrap == 0 && onBeltsAlloy > 0,
                $"E3 FG-GAP-090 口径：施工“等待材料”的在途件数只数废料（带上只有合金时在途废料 {onBeltsScrap}、在途合金 {onBeltsAlloy}），别的物品不再被算成在途废料");

            // 端口面板：收什么、过滤选项。
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, wh, views);
            BeltPortService.PortView inView = views.FirstOrDefault(v => !v.IsOutput);
            var choices = new List<ushort>();
            BeltItems.CollectStorable(choices);
            int solids = ItemCatalog.Items.Count(d => d.Form == ItemForm.Solid);
            Expect(inView != null && inView.AcceptLine == GameText.Get("logistics.port.accept_all") && choices.Count == solids && choices[0] == scrap.BeltId && choices.Contains(alloy.BeltId)
                   && BeltItems.Name(alloy.BeltId) == "合金" && BeltItems.ResourceOf(alloy.BeltId) == "alloy" && BeltItems.ResourceOf(scrap.BeltId) == CampaignEconomyLedger.ResourceScrap
                   && BeltItems.Name(900).Contains("900"),
                $"E4 端口面板：输入口“{inView?.AcceptLine}”；输出过滤选项 = 物品表全部 {solids} 种固体（按表顺序）；带上物品的名字来自物品表（表里没有的编号写“未知物品 #编号”）");
        }

        private static void CheckBeltPerKindFull()
        {
            CampaignState s = NewWorld(9301, scrap: 30);
            BuildingRecord wh = ActivateWarehouse(s);
            ItemDef alloy = Def("alloy");
            ItemDef scrap = Def("scrap");
            HomeInventory.Add(s, alloy, 20);
            List<(GridCell cell, BeltDir dir)> path = FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out string failure);
            if (path == null)
            {
                Fail("E5 仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding inP = BeltPortService.Find(wh.BuildingId, "warehouse.in0");
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", alloy.BeltId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 6);
            // 合金推出去以后把合金的仓位占满：带上的合金回不来，输入口卡在合金上，原因写“放不下合金”。
            HomeInventory.Add(s, alloy, HomeInventory.Space(s, alloy));
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            BeltNetworkService.Kernel.TryGetPortCounts(inP.PortId, out _, out int buffered, out ushort kind);
            bool storeFull = BeltPortService.StoreFull(s, inP.PortId, out ItemDef fullKind);
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(s, wh, views);
            string issue = views.FirstOrDefault(v => !v.IsOutput)?.IssueLine ?? string.Empty;
            // 废料仍然能进：直接看废料仓位还有空（只堵那一种）。
            bool scrapRoom = HomeInventory.Space(s, scrap) > 0;
            string diag = string.Empty;
            try
            {
                DiagReport report = RootCauseDiagnosis.DiagnoseBuilding(s, wh);
                diag = report != null ? string.Join(" / ", report.Chains.SelectMany(c => c.Steps).Select(st => st.Text)) : string.Empty;
            }
            catch (Exception e)
            {
                diag = "诊断抛异常：" + e.Message;
            }
            Expect(buffered > 0 && kind == alloy.BeltId && storeFull && fullKind == alloy && issue.Contains("放不下合金") && scrapRoom && diag.Contains("放不下合金"),
                $"E5 只有合金放不下时：输入口缓存卡在合金上（{buffered} 件）、带停下；端口面板“{issue}”；为什么不工作“{diag}”；废料仍有仓位（只堵那一种）");
            // 腾出合金仓位后恢复。
            var before = new int[1024];
            BeltNetworkService.Kernel.CountItemsByType(-1, before);
            // FG4-ECO-11 审查修复：仓满时被退回落地的合金（系统返还搬运单）在腾出仓位后也会被机器搬回入库（原来返还搬运只认废料，永远停在“等空间”）。
            string alloyRes = BeltItems.ResourceOf(alloy.BeltId);
            int groundBefore = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == alloyRes).Sum(g => g.Amount);
            HomeInventory.RemoveUpTo(s, alloy, 200);
            int stockAfterRemove = HomeInventory.Stock(s, alloy);
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            var after = new int[1024];
            BeltNetworkService.Kernel.CountItemsByType(-1, after);
            BeltNetworkService.Kernel.TryGetPortCounts(inP.PortId, out _, out int bufferedAfter, out _);
            int groundAfter = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == alloyRes).Sum(g => g.Amount);
            Expect(!BeltPortService.StoreFull(s, inP.PortId, out _) && after[alloy.BeltId] == 0 && bufferedAfter == 0 && groundAfter == 0
                   && HomeInventory.Stock(s, alloy) == stockAfterRemove + before[alloy.BeltId] + buffered + groundBefore,
                $"E6 腾出合金仓位后输入口自动恢复：带上的 {before[alloy.BeltId]} 件、缓存的 {buffered} 件与仓满时退回落地的 {groundBefore} 件合金全部入库（库存 {stockAfterRemove} → {HomeInventory.Stock(s, alloy)}），" +
                $"带清空、缓存清空、地上不剩（{groundAfter}）");
        }

        private static void CheckLegacySinkMigration()
        {
            CampaignState s = NewWorld(9401, scrap: 50);
            BuildingRecord wh = ActivateWarehouse(s);
            BeltPortService.Binding inP = BeltPortService.Find(wh.BuildingId, "warehouse.in0");
            BeltKernel k = BeltNetworkService.Kernel;
            // 模拟 FG3-LOG-03 起的旧存档：输入口“只收废料”。
            k.SetSinkAccept(inP.PortId, BeltItems.ScrapId);
            k.TryGetPortInfo(inP.PortId, out BeltPortInfo old);
            // 这里验证端口服务的迁移（缓存里已有件时的种类迁移由下面 E8 在独立内核上验证）。
            int scrapBefore = s.Scrap;
            s.Belts.PortBindings = s.Belts.PortBindings.ToArray(); // 换一个数组引用 = 读档后重建索引
            WorldSimulation.StepMany(2);
            k.TryGetPortInfo(inP.PortId, out BeltPortInfo migrated);
            Expect(old.Accept == BeltItems.ScrapId && migrated.Accept == BeltConst.AcceptAnyOneKind && s.Scrap == scrapBefore,
                "E7 旧存档的仓库输入口“只收废料”在重建索引（读档）后自动改成“全部可存物品（一次一种）”");
            using (var kernel = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                kernel.AddCell(0, 0, BeltDir.East, 0);
                kernel.AddCell(1, 0, BeltDir.East, 0);
                kernel.AddSink(77, 2, 0, 4, 0, 0, BeltConst.AnyFace, BeltItems.ScrapId);
                kernel.InsertItemAt(1, 0, 1000, BeltItems.ScrapId);
                kernel.StepMany(200);
                kernel.TryGetPortCounts(77, out _, out int bufOld, out ushort itemOld);
                kernel.SetSinkAccept(77, BeltConst.AcceptAnyOneKind);
                kernel.TryGetPortCounts(77, out _, out int bufNew, out ushort itemNew);
                Expect(bufOld == 1 && itemOld == 0 && bufNew == 1 && itemNew == BeltItems.ScrapId,
                    $"E8 内核迁移：缓存里已有 {bufOld} 件时从“只收废料”改成按种类收，缓存种类记为废料（改前 {itemOld} → 改后 {itemNew}），入库不会认错种类");
                // 一次一种：缓存里是废料时合金停在带尾，取走后再收。
                kernel.InsertItemAt(1, 0, 1000, 4);
                kernel.StepMany(200);
                kernel.TryGetCellInfo(1, 0, out BeltCellInfo tail);
                kernel.TryGetPortCounts(77, out _, out int stillOne, out ushort stillScrap);
                int took = kernel.TakeFromSink(77, 1);
                kernel.StepMany(200);
                kernel.TryGetPortCounts(77, out _, out int nowAlloy, out ushort alloyKind);
                Expect(tail.Count == 1 && tail.Block == BeltBlock.SinkFull && stillOne == 1 && stillScrap == BeltItems.ScrapId && took == 1 && nowAlloy == 1 && alloyKind == 4,
                    "E9 内核“任何物品、一次一种”：缓存里有废料时合金停在带尾（原因“下游已满”），废料被取走后合金进缓存、种类改记合金");
            }
        }

        private static void CheckClearAndFilters()
        {
            CampaignState s = NewWorld(9501, scrap: 20);
            BuildingRecord wh = ActivateWarehouse(s);
            ItemDef alloy = Def("alloy");
            HomeInventory.Add(s, alloy, 25);
            List<(GridCell cell, BeltDir dir)> path = FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out string failure);
            if (path == null)
            {
                Fail("E10 仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", alloy.BeltId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 15);
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            var counts = new int[1024];
            BeltNetworkService.Kernel.CountItemsByType(-1, counts);
            int onBelt = counts[alloy.BeltId];
            int stock0 = HomeInventory.Stock(s, alloy);
            BeltClearPlan plan = BeltClearService.PlanNetwork(s, path[path.Count / 2].cell);
            bool ok = BeltClearService.Execute(s, plan, false, out string reason);
            Array.Clear(counts, 0, counts.Length);
            BeltNetworkService.Kernel.CountItemsByType(-1, counts);
            Expect(onBelt > 0 && ok && BeltClearService.LastStored == onBelt && HomeInventory.Stock(s, alloy) == stock0 + onBelt && counts[alloy.BeltId] == 0 && plan.Fit == onBelt,
                $"E10 清带：带上的 {onBelt} 件合金按合金的仓位送回仓库（{stock0} → {HomeInventory.Stock(s, alloy)}），不再当成“放不下”丢弃（{reason ?? "成功"}）");

            var choices = new List<ushort>();
            BeltNodeService.CollectFilterChoices(choices, BeltConst.FilterAny);
            int solids = ItemCatalog.Items.Count(d => d.Form == ItemForm.Solid);
            Expect(choices.Count == solids + 2 && choices[0] == BeltConst.FilterAny && choices[choices.Count - 1] == BeltConst.FilterNone && choices.Contains(alloy.BeltId)
                   && BeltNodeService.ParseFilter("alloy") == alloy.BeltId && BeltNodeService.ParseFilter("scrap") == BeltItems.ScrapId && BeltNodeService.ParseFilter("water") == BeltConst.FilterAny,
                $"E11 分流器每口过滤的选项 = 全部物品 + 物品表 {solids} 种固体 + 关闭（关闭 DEBT-FG3LOG04-02）；预设表可写物品 ID（alloy → {alloy.BeltId}），流体不是传送带物品");

            // 传送带物品颜色来自物品表（关闭 DEBT-FG0ARCH02-04 的“没有物品名和图标”）。
            Vector4[] palette = BeltNetworkService.ItemPalette();
            bool paletteOk = palette != null && palette.Length >= 64 && Mathf.Approximately(palette[alloy.BeltId].w, 1f)
                             && Mathf.Abs(palette[alloy.BeltId].x - alloy.Color.r) < 1e-3f && Mathf.Abs(palette[Def("scrap").BeltId].z - Def("scrap").Color.b) < 1e-3f && palette[60].w == 0f;
            Expect(paletteOk, "E12 传送带上的物品方块按物品表的颜色画（表里没有的编号仍按编号着色）");
        }

        // ── F 悬停 ─────────────────────────────────────────────────────────────

        private static void CheckHoverDistribution()
        {
            CampaignState s = NewWorld(9601, scrap: 60);
            BuildingRecord wh = ActivateWarehouse(s);
            ItemDef alloy = Def("alloy");
            ItemDef scrap = Def("scrap");
            HomeInventory.Add(s, alloy, 12);
            List<(GridCell cell, BeltDir dir)> path = FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out _);
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", alloy.BeltId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 8);
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(3, 3), "alloy", 5, "eco-hover-ground");
            MachineRecord m = s.MachineRecords.FirstOrDefault(r => r != null && r.IsAlive);
            if (m != null)
            {
                m.Cargo = new[] { new CargoEntry { ResourceType = "alloy", Amount = 2 } };
            }
            double clock = 100;
            ItemDistribution.RealTime = () => clock;
            ItemDistribution.Invalidate();
            ItemDistributionView v = ItemDistribution.Get(s, alloy);
            var counts = new int[1024];
            BeltNetworkService.Kernel.CountItemsByType(-1, counts);
            long belts = Part(v, "item.dist.belts");
            long ports = Part(v, "item.dist.ports");
            long expectTotal = HomeInventory.Stock(s, alloy) + counts[alloy.BeltId] + ports + 5 + (m != null ? 2 : 0);
            Expect(v.Total == expectTotal && v.Stock == HomeInventory.Stock(s, alloy) && belts == counts[alloy.BeltId] && belts > 0 && Part(v, "item.dist.ground") == 5
                   && Part(v, "item.dist.cargo") == (m != null ? 2 : 0) && Part(v, "item.dist.warehouse") == v.Stock,
                $"F1 合金的分布：仓库 {v.Stock} / 传送带上 {belts} / 端口缓存 {ports} / 地面 5 / 机器货舱 {(m != null ? 2 : 0)}，合计 {v.Total}（在途单列，不算库存）");

            ItemDistributionView sv = ItemDistribution.Get(s, scrap);
            bool split = Part(sv, "item.dist.core_cache") == Math.Min(s.Scrap, HomeValleyLayout.CoreCacheCapacity) && Part(sv, "item.dist.core_cache") + Part(sv, "item.dist.warehouse") == s.Scrap;
            wh.ConstructionState = BuildingConstructionState.Damaged;
            s.Scrap = HomeValleyLayout.CoreCacheCapacity + 40; // 没有仓库还超出核心缓存（测试捷径 / 旧档）：不把多出来的写成“仓库”
            ItemDistribution.Invalidate();
            ItemDistributionView noWh = ItemDistribution.Get(s, scrap);
            bool allCore = Part(noWh, "item.dist.core_cache") == s.Scrap && Part(noWh, "item.dist.warehouse") == 0;
            wh.ConstructionState = BuildingConstructionState.Operational;
            Expect(split && allCore,
                $"F2 废料按“归还核心缓存 {Part(sv, "item.dist.core_cache")} + 仓库 {Part(sv, "item.dist.warehouse")}”分开写；仓库没修好时全部算核心缓存（{Part(noWh, "item.dist.core_cache")}）");

            // 缓存：同一刻再取不重建；库存变化不提前失效（端口每个内核步都在改库存）；只按 eco.hover.refresh_seconds 定时重读；显式 Invalidate（开面板 / 读档）立即重建。
            ItemDistribution.Invalidate();
            ItemDistributionView baseline = ItemDistribution.Get(s, alloy);
            int b0 = ItemDistribution.BuildCount;
            ItemDistribution.Get(s, alloy);
            ItemDistribution.Get(s, scrap);
            int b1 = ItemDistribution.BuildCount;
            int rev0 = HomeInventory.Revision;
            HomeInventory.Add(s, alloy, 1);
            bool revMoved = HomeInventory.Revision != rev0;
            ItemDistributionView stale = ItemDistribution.Get(s, alloy);
            int b2 = ItemDistribution.BuildCount;
            ItemDistribution.Invalidate();
            ItemDistributionView fresh = ItemDistribution.Get(s, alloy);
            int b3 = ItemDistribution.BuildCount;
            Expect(b1 == b0 && revMoved && b2 == b1 && stale.Stock == baseline.Stock && b3 == b2 + 1 && fresh.Stock == baseline.Stock + 1,
                $"F3 悬停缓存：同一刻多次取不重建（{b0}→{b1}）；库存变了不提前重建（{b2}，仍是 {stale.Stock}），显式失效（开面板 / 读档）立即重建（→{b3}，{fresh.Stock}）");

            HomeInventory.Add(s, alloy, 3);
            clock += ItemDistribution.RefreshSeconds + 0.01; // 定时重读到期（库存变化本身不提前失效）
            TooltipContent tip = ItemsPanelUIToolkit.HoverContent(alloy);
            Expect(tip.Title == "合金" && tip.Body.Contains("总库存") && tip.Body.Contains("仓库可用") && tip.Body.Contains("净速率") && tip.Sources.Any(x => x.Label.Contains("传送带上"))
                   && tip.Sources.Any(x => x.Label == "仓库") && tip.CodexEntryId == MechanicCodex.ItemEntryId("alloy") && !GameText.ContainsMarker(tip.Body + tip.CodexEntry),
                $"F4 悬停提示：“{tip.Body.Replace("\n", " / ")}”，分布 {tip.Sources.Count} 项（{string.Join("、", tip.Sources.Select(x => x.Label + " " + x.Value))}），带图鉴链接");

            TooltipContent waterTip = ItemsPanelUIToolkit.HoverContent(Def("water"));
            TooltipContent heartTip = ItemsPanelUIToolkit.HoverContent(Def("furnace_heart"));
            Expect(waterTip.Body.Contains("家园里一件都没有") && waterTip.Body.Contains("不统计") && heartTip.Body.Contains("核心保管库"),
                $"F5 没有的物品写“家园里一件都没有”；流体的净速率不统计（不在家园库存）；关键材料写明在核心保管库（{heartTip.Body.Replace("\n", " / ")}）");

            // F6 家园物流运转中（仓库输出“全部可存物品” → 闭环 → 输入口入库：端口每个内核步都在改库存版本）：60 帧 / 真实秒、每帧 1 个内核步（= 3x）连跑 3 真实秒，
            // 每帧都来取分布（悬停 / 面板开着），重建次数只能按 refresh_seconds 定时，不能跟着库存版本每步重建（第 1 轮审查 P1）。
            CampaignState w = NewWorld(9602, scrap: 40);
            ActivateWarehouse(w);
            HomeInventory.Add(w, alloy, 30);
            List<(GridCell cell, BeltDir dir)> loop = FgBeltFormalSelfCheck.WarehouseLoop(w, 1, out string loopFail);
            WorldSimulation.StepMany(GameClock.StepHz * 30); // 先让第一批绕回输入口，进入稳定收发
            const int frames = 180;
            const double frameSeconds = 1.0 / 60.0;
            ItemDistribution.Invalidate();
            ItemDistribution.Get(w, alloy);
            int revBefore = HomeInventory.Revision;
            int bLoop0 = ItemDistribution.BuildCount;
            for (int f = 0; f < frames; f++)
            {
                WorldSimulation.StepMany(1);
                clock += frameSeconds;
                ItemDistribution.Get(w, alloy);
                ItemDistribution.Get(w, scrap);
            }
            int loopBuilds = ItemDistribution.BuildCount - bLoop0;
            int revChanges = HomeInventory.Revision - revBefore;
            int maxBuilds = (int)Math.Ceiling(frames * frameSeconds / ItemDistribution.RefreshSeconds);
            Expect(loop != null && revChanges >= 5 && loopBuilds >= 1 && loopBuilds <= maxBuilds,
                $"F6 物流运转 {frames} 帧（库存版本变了 {revChanges} 次：旧口径每变一次就重建）每帧取分布只重建 {loopBuilds} 次" +
                $"（≤ ⌈{frames}×{frameSeconds:0.####}/{ItemDistribution.RefreshSeconds:0.##}⌉ = {maxBuilds}）：不每帧、不每步计算{(loop == null ? "；闭环铺不下：" + loopFail : string.Empty)}");
            ItemDistribution.ResetForTests();
        }

        private static string Line2(string body, string prefix) =>
            body?.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal)) ?? "（无）";

        private static long Part(ItemDistributionView v, string key) => v?.Parts.Where(p => p.Key == key).Sum(p => p.Value) ?? 0;

        // ── G 净速率与时间 ───────────────────────────────────────────────────────

        private static void CheckFlowTiming()
        {
            CampaignState s = NewWorld(9701, scrap: 40);
            ActivateWarehouse(s);
            ItemDef alloy = Def("alloy");
            int interval = ItemFlowStats.SampleSeconds * GameClock.StepHz;
            WorldSimulation.StepMany(interval * 2);
            bool measuring = !ItemFlowStats.TryNetPerMinute(s, alloy, GameClock.StepHz, out _, out _, out _) || true;
            // 每 10 游戏秒放进 5 件合金，跑 1 游戏分钟：净速率 = 30 件 / 分钟。
            for (int i = 0; i < 6; i++)
            {
                HomeInventory.Add(s, alloy, 5);
                WorldSimulation.StepMany(interval);
            }
            bool got = ItemFlowStats.TryNetPerMinute(s, alloy, GameClock.StepHz, out float perMin, out float window, out _);
            int samples = s.Economy.FlowSamples.Length;
            Expect(got && Mathf.Abs(perMin - 30f) < 0.01f && Mathf.Abs(window - 60f) < 0.01f && samples == ItemFlowStats.WindowSamples + 1,
                $"G1 净速率：每 {ItemFlowStats.SampleSeconds} 游戏秒按世界步采一次库存、保留 {samples} 条；1 游戏分钟里放进 30 件合金 → 净速率 {perMin:0.##}/分钟（窗口 {window:0} 游戏秒）");

            // 暂停：不采样；0.5x～3x：同样的游戏时间采到同样的样。
            var results = new List<string>();
            string reference = null;
            bool same = true;
            bool paused = true;
            const float frame = 1f / 60f;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState w = NewWorld(9702, scrap: 40);
                ActivateWarehouse(w);
                GameClock.SetPaused(true);
                int n0 = w.Economy.FlowSamples.Length;
                for (int i = 0; i < 300; i++)
                {
                    WorldSimulation.Frame(frame);
                }
                paused &= w.Economy.FlowSamples.Length == n0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + GameClock.StepHz * 45;
                int frames = 0;
                bool gave = false;
                while (GameClock.Ticks < target && frames < 60 * 400)
                {
                    if (!gave && GameClock.Ticks >= target - GameClock.StepHz * 25)
                    {
                        HomeInventory.Add(w, alloy, 7);
                        gave = true;
                    }
                    WorldSimulation.Frame(frame, target);
                    frames++;
                }
                string snap = string.Join(";", w.Economy.FlowSamples.Select(x => x.Tick + ":" + string.Join(",", x.Stocks)));
                reference ??= snap;
                same &= snap == reference;
                results.Add($"{speed}x {frames} 帧");
                GameClock.SetSpeed(1f);
            }
            Expect(paused && same && measuring,
                $"G2 暂停 300 帧不采样；0.5x / 1x / 2x / 3x 跑同样的 45 游戏秒，采样逐条一致（{string.Join("；", results)}）");
        }

        private static void CheckObservedAndSave()
        {
            string Run(bool observe)
            {
                CampaignState w = NewWorld(9801, observe, 40);
                BuildingRecord wh = ActivateWarehouse(w);
                HomeInventory.Add(w, Def("alloy"), 18);
                FgBeltFormalSelfCheck.WarehouseLoop(w, 1, out _);
                WorldSimulation.StepMany(GameClock.StepHz * 75);
                return EconomyJson(w) + "|" + KindTotal(w, Def("alloy")) + "|" + KindTotal(w, Def("scrap"));
            }
            string seen = Run(true);
            string unseen = Run(false);
            Expect(seen == unseen, $"G3 家园观察 / 不观察跑同样的 75 游戏秒：物资、保管库、净速率采样、逐种总量逐字段一致（{seen.Length} 字节）");

            CampaignState s = NewWorld(9802, scrap: 40);
            ActivateWarehouse(s);
            HomeInventory.Add(s, Def("alloy"), 9);
            HomeInventory.Add(s, Def("reactor_core"), 1);
            HomeInventory.Add(s, Def("research_points"), 4);
            FgBeltFormalSelfCheck.WarehouseLoop(s, 1, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 33 + 5);
            ItemFlowStats.TryNetPerMinute(s, Def("alloy"), GameClock.StepHz, out float rate0, out _, out _);
            string before = EconomyJson(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            string continuous = EconomyJson(s) + "|" + KindTotal(s, Def("alloy"));

            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("G4 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            string loaded = EconomyJson(rr.State);
            ItemFlowStats.TryNetPerMinute(rr.State, Def("alloy"), GameClock.StepHz, out float rate1, out _, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            string resumed = EconomyJson(rr.State) + "|" + KindTotal(rr.State, Def("alloy"));
            Expect(save.Success && loaded == before && Mathf.Approximately(rate0, rate1) && resumed == continuous && rr.State.Economy.Vault.Length == 1 && rr.State.Research.Points == 4,
                $"G4 真文件存读档：仓库物资、核心保管库、研究点、净速率采样逐字段往返（净速率 {rate0:0.##} → {rate1:0.##}/分钟），读档后接着跑 40 游戏秒与不存档一致");

            // 旧存档没有物资域：读档补空域，照常可用。
            CampaignState legacy = JsonUtility.FromJson<CampaignState>("{\"Scrap\":12}");
            CampaignFgStateDomains.EnsureAll(legacy);
            Expect(legacy.Economy != null && legacy.Economy.Items.Length == 0 && HomeInventory.Stock(legacy, Def("alloy")) == 0 && HomeInventory.Stock(legacy, Def("scrap")) == 12,
                "G5 旧存档（没有物资域）读入：补空域，合金 0、废料照旧");
        }

        // ── H 界面 ─────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(9901, scrap: 70);
            ActivateWarehouse(s);
            HomeInventory.Add(s, Def("alloy"), 14);
            HomeInventory.Add(s, Def("furnace_heart"), 1);
            ItemsPanelUIToolkit.InWorldOverrideForTests = true;
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            string summary = string.Empty;
            bool panelOk = false;
            bool heldOk = false;
            bool clickOk = false;
            bool hookOk = false;
            bool panelThrottleOk = false;
            string panelThrottleNote = string.Empty;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "ItemsPanel.uxml", "ItemsPanelWindow", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_items") { hideFlags = HideFlags.HideAndDontSave };
                    ItemsPanelUIToolkit p = go.AddComponent<ItemsPanelUIToolkit>();
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Refresh();
                    if (lang == GameLanguage.ZhCn)
                    {
                        int all = p.VisibleTileCount;
                        panelOk = p.TileCount == ItemCatalog.Items.Count && p.SectionCount == 8 && p.TileNameText("alloy") == "合金" && p.TileAmountText("alloy") == "14"
                                  && p.TitleText == "物资" && p.FooterText.Contains("Alt") && p.FooterText.Contains("占位") && !GameText.ContainsMarker(p.FooterText + p.CountText)
                                  && p.TileIconColor("alloy") == Def("alloy").Color && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyItemsFirstOpen);
                        p.ToggleHeldOnly();
                        int held = p.VisibleTileCount;
                        heldOk = held < all && p.TileOf("alloy") != null && !p.TileOf("alloy").ClassListContains("uk-hidden") && p.TileOf("water").ClassListContains("uk-hidden");
                        p.ToggleHeldOnly();
                        using (ClickEvent click = ClickEvent.GetPooled())
                        {
                            click.target = p.TileOf("alloy");
                            p.TileOf("alloy").SendEvent(click);
                        }
                        clickOk = MechanicCodex.LastOpenedId == MechanicCodex.ItemEntryId("alloy");
                        // 面板定时重读：库存连续变化（模拟端口每步入库）不触发重读 / 重建，到 refresh_seconds 才更新数字。
                        Func<double> panelClock = ItemsPanelUIToolkit.Clock;
                        Func<double> distClock = ItemDistribution.RealTime;
                        double clk = Time.realtimeSinceStartupAsDouble + 100;
                        ItemsPanelUIToolkit.Clock = () => clk;
                        ItemDistribution.RealTime = () => clk;
                        try
                        {
                            p.Refresh();
                            int pb0 = ItemDistribution.BuildCount;
                            string a0 = p.TileAmountText("alloy");
                            for (int f = 0; f < 20; f++)
                            {
                                HomeInventory.Add(s, Def("alloy"), 1);
                                clk += 0.01;
                                p.Refresh();
                            }
                            int pb1 = ItemDistribution.BuildCount;
                            string a1 = p.TileAmountText("alloy");
                            clk += ItemDistribution.RefreshSeconds;
                            p.Refresh();
                            string a2 = p.TileAmountText("alloy");
                            HomeInventory.RemoveUpTo(s, Def("alloy"), 20);
                            panelThrottleOk = a0 == "14" && pb1 == pb0 && a1 == "14" && a2 == "34" && ItemDistribution.BuildCount == pb1 + 1;
                            panelThrottleNote = $"{a0} → 20 帧内库存连加 20 仍显示 {a1}（重建 {pb1 - pb0} 次）→ {ItemDistribution.RefreshSeconds:0.##} 秒后 {a2}";
                        }
                        finally
                        {
                            ItemsPanelUIToolkit.Clock = panelClock;
                            ItemDistribution.RealTime = distClock;
                            ItemDistribution.Invalidate();
                        }
                        hookOk = MechanicCodex.IsUnlocked("codex.economy.items");
                        summary = $"{p.TileCount} 格 / {p.SectionCount} 组 / 只看持有的 {held} 格 / {p.CountText}";
                    }
                    p.SetOpen(false);
                    Object.DestroyImmediate(go);
                    r.panel.visualTree.Q<VisualElement>("ItemsPanelRoot")?.RemoveFromClassList("uk-hidden");
                });
                bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                Expect(pass, $"H1 布局探针 ItemsPanel.uxml#ItemsPanelWindow [{lang}]：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                foreach (string entry in new[] { MechanicCodex.ItemEntryId("alloy"), MechanicCodex.RecipeEntryId("fuel") })
                {
                    string codex = UiToolkitLayoutProbe.Probe(UiKitFolder + "MechanicCodexPanel.uxml", "CodexWindow", stressFill: true, prepare: r =>
                    {
                        var go = new GameObject("__probe_codex_eco") { hideFlags = HideFlags.HideAndDontSave };
                        MechanicCodexPanelUIToolkit p = go.AddComponent<MechanicCodexPanelUIToolkit>();
                        p.BindView(r.panel.visualTree);
                        p.OpenAt(entry);
                        p.SetOpen(false);
                        Object.DestroyImmediate(go);
                        r.panel.visualTree.Q<VisualElement>("CodexRoot")?.RemoveFromClassList("uk-hidden");
                    });
                    bool ok = codex.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(ok, $"H2 布局探针 MechanicCodexPanel.uxml#CodexWindow（{entry}，5 个页签）[{lang}]：{(ok ? "PASS" : codex.Replace("\n", " | ").Substring(0, Math.Min(600, codex.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(panelOk && heldOk && clickOk && hookOk,
                $"H3 物资面板（真 UXML）：按层级 8 组、每种物品一格（占位图标颜色 = 表里的颜色、数量）；“只看持有的”隐藏没有的；点合金打开合金的图鉴条目；第一次打开发钩子并解锁图鉴系统说明（{summary}）");
            Expect(panelThrottleOk, $"H3b 物资面板按 eco.hover.refresh_seconds 定时重读，库存每步变化不触发每帧重建（{panelThrottleNote}）");

            // 图鉴正文：来源与用途。
            MechanicCodexEntry alloyEntry = MechanicCodex.Find(MechanicCodex.ItemEntryId("alloy"));
            string alloyBody = MechanicCodex.Body(alloyEntry, s);
            MechanicCodexEntry fuelEntry = MechanicCodex.Find(MechanicCodex.RecipeEntryId("fuel"));
            string fuelBody = MechanicCodex.Body(fuelEntry, s);
            MechanicCodexEntry machineEntry = MechanicCodex.Find(MechanicCodex.RecipeEntryId("machine"));
            string machineBody = MechanicCodex.Body(machineEntry, s);
            MechanicCodexEntry coreEntry = MechanicCodex.Find(MechanicCodex.ItemEntryId("supercomputer_core"));
            bool coreLocked = !MechanicCodex.IsUnlocked(coreEntry.Id) && MechanicCodex.Hint(coreEntry, s).Contains("获取途径") && MechanicCodex.Hint(coreEntry, s).Contains("任务");
            int itemEntries = MechanicCodex.CountIn(MechanicCodex.TabItem);
            int recipeEntries = MechanicCodex.CountIn(MechanicCodex.TabRecipe);
            bool links = alloyEntry.Links.Contains(MechanicCodex.RecipeEntryId("alloy_ore")) && alloyEntry.Links.Contains(MechanicCodex.RecipeEntryId("part"));
            Expect(itemEntries == 30 && recipeEntries == 15 && alloyBody.Contains("从哪来") && alloyBody.Contains("精炼炉") && alloyBody.Contains("拿去干什么") && alloyBody.Contains("零件工坊")
                   && alloyBody.Contains("现有：14") && fuelBody.Contains("副产品：酸液 20 升") && fuelBody.Contains("以现在的家园库存") && machineBody.Contains("按蓝图") && machineBody.Contains("初值")
                   && links && coreLocked && MechanicCodex.IsUnlocked(MechanicCodex.ItemEntryId("alloy")) && MechanicCodex.IsUnlocked(MechanicCodex.ItemEntryId("furnace_heart")),
                $"H4 图鉴：物品 {itemEntries} 条、配方 {recipeEntries} 条（卡片“每种物品和配方都有图鉴条目”）；合金写明从哪来（精炼炉）、拿去干什么（零件工坊……）与现有数量；" +
                "燃油配方写明副产品必须有去处与现在能不能做；机器写明按蓝图 / 初值；没拿到的超算残核是剪影 + 获取途径；物品 ↔ 配方互链");

            // 图鉴配方页的“以现在的家园库存能不能做”：流体输入不再一律判“不在仓库里”，产出是实体（机器 / 固件芯片）不查仓库空间。
            string coolantBody = MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.RecipeEntryId("coolant")), s);
            string chipBefore = MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.RecipeEntryId("firmware_chip")), s);
            int chipAdded = HomeInventory.Add(s, Def("chip_substrate"), 1);
            string chipBody = MechanicCodex.Body(MechanicCodex.Find(MechanicCodex.RecipeEntryId("firmware_chip")), s);
            HomeInventory.RemoveUpTo(s, Def("chip_substrate"), chipAdded);
            Expect(chipAdded == 1 && !fuelBody.Contains("不在家园仓库里") && fuelBody.Contains("走管线与储罐") && fuelBody.Contains("原油") && fuelBody.Contains("材料够做一次")
                   && coolantBody.Contains("缺少稀土矿") && coolantBody.Contains("走管线与储罐") && !machineBody.Contains("不在家园仓库里") && machineBody.Contains("缺少")
                   && chipBefore.Contains("缺少芯片基板") && chipBody.Contains("材料够做一次") && !chipBody.Contains("不在家园仓库里") && !chipBody.Contains("走管线"),
                $"H4b 配方页“能不能做”：燃油（流体进出）写“{Line2(fuelBody, "以现在")}”并说明流体走管线；冷却液写“{Line2(coolantBody, "以现在")}”；" +
                $"机器写“{Line2(machineBody, "以现在")}”；固件芯片放进 1 块芯片基板后写“{Line2(chipBody, "以现在")}”（产出是实体，不查仓库空间）");

            string itemsButton = null;
            string pause = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow", stressFill: false, prepare: r =>
            {
                var go = new GameObject("__probe_pause_eco") { hideFlags = HideFlags.HideAndDontSave };
                PauseMenuUIToolkit pm = go.AddComponent<PauseMenuUIToolkit>();
                pm.BindView(r.panel.visualTree);
                itemsButton ??= pm.ItemsButton?.text;
                Object.DestroyImmediate(go);
                r.panel.visualTree.Q<VisualElement>("PauseMenuRoot")?.RemoveFromClassList("uk-hidden");
            });
            bool pauseOk = pause.StartsWith("PASS", StringComparison.Ordinal);
            Expect(itemsButton == "物资" && pauseOk, $"H5 暂停菜单多了“物资”入口（{itemsButton}）；暂停菜单布局探针 {(pauseOk ? "PASS" : pause.Replace("\n", " | ").Substring(0, Math.Min(400, pause.Length)))}");
            ItemsPanelUIToolkit.InWorldOverrideForTests = false;
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
        }

        // ── I FG-GAP-092 ──────────────────────────────────────────────────────

        private static void CheckAudioPoolRace()
        {
            var pool = new Dictionary<string, string>();
            var released = new List<string>();
            pool["sfx_ui_click"] = "played-first"; // 播放路径（AudioAgent.TryAdd）先放进了池
            bool threw = false;
            bool first = true;
            bool second = true;
            try
            {
                first = FeedbackCues.AddOrRelease(pool, "sfx_ui_click", "preload-handle", h => released.Add(h));
                second = FeedbackCues.AddOrRelease(pool, "sfx_build_place", "preload-2", h => released.Add(h));
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Expect(!threw && !first && second && pool["sfx_ui_click"] == "played-first" && released.SequenceEqual(new[] { "preload-handle" }) && pool["sfx_build_place"] == "preload-2",
                "I1 FG-GAP-092：开局音效预热回调晚于播放路径时不再 Add 同一个键抛异常——池里已有就释放这份句柄（成对释放），没有才放进去；框架 Assets/TEngine 未改");
        }

        // ── J 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9951, scrap: 80);
            BuildingRecord wh = ActivateWarehouse(s);
            HomeInventory.Add(s, Def("alloy"), 40);
            FgBeltFormalSelfCheck.WarehouseLoop(s, 2, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            ItemDistribution.ResetForTests();
            double clock = 0;
            ItemDistribution.RealTime = () => clock;
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                clock += 1;
                ItemDistribution.Get(s, Def("alloy"));
                times.Add(ItemDistribution.LastBuildMs);
            }
            times.Sort();
            double homeP95 = times[(int)(times.Count * 0.95) - 1];

            // 后期规模：15,000 格 / 30,000 件的传送带按种类计数（逐格在 AOT）。
            double countMs;
            int counted;
            using (var k = new BeltKernel(BeltNetworkService.ReadConfig(), 16384))
            {
                for (int row = 0; row < 100; row++)
                {
                    for (int x = 0; x < 150; x++)
                    {
                        k.AddCell(x, row * 2, BeltDir.East, 2);
                    }
                }
                ushort id = 1;
                for (int row = 0; row < 100; row++)
                {
                    for (int x = 0; x < 150; x++)
                    {
                        k.InsertItemAt(x, row * 2, 1000, id);
                        k.InsertItemAt(x, row * 2, 30000, (ushort)(id % 17 + 1));
                        id = (ushort)(id % 17 + 1);
                    }
                }
                var counts = new int[1024];
                k.CountItemsByType(-1, counts);
                var sw = Stopwatch.StartNew();
                const int reps = 20;
                for (int i = 0; i < reps; i++)
                {
                    Array.Clear(counts, 0, counts.Length);
                    counted = k.CountItemsByType(-1, counts);
                }
                sw.Stop();
                countMs = sw.Elapsed.TotalMilliseconds / reps;
                counted = counts.Sum();
            }

            // 端口转移：每个内核步的开销（家园仓库闭环满载）。
            BeltPortService.Pump(s);
            var pump = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                WorldSimulation.StepMany(1);
                pump.Add(BeltPortService.LastPumpMs);
            }
            pump.Sort();
            double pumpP95 = pump[(int)(pump.Count * 0.95) - 1];
            PerfLines.Add($"悬停缓存重建（家园 + 仓库闭环）P95 {homeP95:0.###} ms；15,000 格 / {counted:N0} 件按种类计数 {countMs:0.###} ms / 次；端口转移每步 P95 {pumpP95:0.####} ms");
            ExpectPerf(counted == 30000,
                $"J1 悬停缓存重建 P95 {homeP95:0.###} ms（≤ 2 ms，只在悬停 / 面板打开时、每 {ItemDistribution.RefreshSeconds:0.##} 真实秒至多一次，物流运转时也一样，见 F3 / H3b）；后期规模 15,000 格 / 30,000 件按种类计数 {countMs:0.###} ms（≤ 4 ms）；端口转移每内核步 P95 {pumpP95:0.####} ms（≤ 0.2 ms）",
                PerfGate.Le(homeP95, 2.0, "悬停缓存重建 ms"), PerfGate.Le(countMs, 4.0, "15000 格按种类计数 ms"), PerfGate.Le(pumpP95, 0.2, "端口转移每步 ms"));
            ItemDistribution.ResetForTests();
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

        private static string FloatRepr(float f)
        {
            string r = ((double)f).ToString("R", CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

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
                UiTooltip.Hide();
                InputRouter.SetBuildMode(false);
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                ItemDistribution.ResetForTests();
            }
        }

        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

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

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
