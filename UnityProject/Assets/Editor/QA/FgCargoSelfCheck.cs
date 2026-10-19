using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-LOOT-03 货舱、自动拾取、阵亡掉落（FG08 FGR-LOOT-030、FGR-EXP-032；FG13 FGU-35；验收 FGT-EXP-007“货舱：尺寸计算、满载提示、转移、自动拾取开关”）的自检——起真实系统断言行为：
    /// A 数据（占用 / 容量初值、Demo 货位与出发检查单改读同一规则、文本中英、钩子、通知类型、图鉴）；
    /// C 自动拾取（远征队成员走近 → 装进货舱、按物品开关、关键材料始终拾取、家园附近不捡、放不下只捡一部分、搬运单取消）；
    /// D 转移（没暂停被拒、暂停中转移、目标放不下拒绝（负向）、材料拆开转、太远、工单在途、目标阵亡、黑匣子转移）；
    /// E 丢弃（落在旁边、带身份、附近有队员时不被捡回、走开再回来捡回、加密固件搬运不等仓库空间 DEBT-FG8LOOT01-06）；
    /// F 阵亡掉落（货舱物品与黑匣子掉在原地带身份、通知、地图标记与地面外观；负向：掉在敌方领地里——不消失、队友照样捡回；黑匣子带回家园即回收；家园机器阵亡照旧直接回收）；
    /// G 快满 / 已满提示与第一次满的引导钩子；H 真文件存读档；I 暂停与 0.5x～3x、观察与否一致；K 界面（货舱面板 / 暂停菜单开关 / 顶栏 / 探针 / 中英）；L 种子集；M 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgCargoSelfCheck）；单段：bash tools/unity-validate.sh --segment FgCargoSelfCheck。
    /// </summary>
    public static class FgCargoSelfCheck
    {
        private const int Slot = 9;
        private const int RunSlot = 10;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "CargoPanel.uxml";
        private const string RowUxml = UiKitFolder + "CargoRow.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static int _key = 40_000;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 货舱与自动拾取")]
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
            Line("\n[货舱与自动拾取] 空间与容量、全队 / 单机明细、丢弃、战略暂停中转移、按物品开关自动拾取、阵亡掉落、快满预警、地图标记、存读档、倍速（FG8-LOOT-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgcargo-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                AnalysisCatalog.Reload();
                LootCatalog.Reload();
                LootPickCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                LootPickService.ResetSessionState();
                CargoService.ResetSessionState();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckAutoPickup);
                Step(CheckTransfer);
                Step(CheckDiscard);
                Step(CheckEncryptedHaul);
                Step(CheckDeathDrop);
                Step(CheckWarn);
                Step(CheckMerge);
                Step(CheckNoRoom);
                Step(CheckSiteVisuals);
                Step(CheckSaveLoad);
                Step(CheckSpeedAndObservation);
                Step(CheckUi);
                Step(CheckSeedSetAndPerf);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                LootPickCatalog.ResetForTests();
                LootCatalog.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                CargoPanelUIToolkit.InWorldOverrideForTests = false;
                WorldMapFilters.ResetAll();
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  货舱与自动拾取：全部 {_pass} 项通过" : $"  货舱与自动拾取：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static readonly string[] TextKeys =
        {
            "cargo.title", "cargo.team", "cargo.team.none", "cargo.empty", "cargo.machine", "cargo.machine.empty", "cargo.state.full", "cargo.state.warn", "cargo.line",
            "cargo.line.quest", "cargo.line.job", "cargo.blackbox", "cargo.target", "cargo.target.none", "cargo.pause", "cargo.transfer", "cargo.discard", "cargo.warn_banner",
            "cargo.full_banner", "cargo.auto.summary", "cargo.auto.open", "cargo.footer", "cargo.hud", "cargo.hud_tip", "cargo.reason.gone", "cargo.reason.job", "cargo.reason.pause",
            "cargo.reason.same", "cargo.reason.target_dead", "cargo.reason.far", "cargo.reason.full", "cargo.transferred", "cargo.transferred_part", "cargo.discarded", "cargo.picked",
            "cargo.warn_notify", "cargo.full_notify", "cargo.no_room_notify", "cargo.dropped", "cargo.bb.recovered", "cargo.bb.recovered_ground", "blackbox.where.planet", "ui.map.filter.loot",
            "cargo.map.packet", "cargo.map.key", "cargo.map.cargo", "cargo.map.enemy", "pause.cargo", "pause.auto_title", "pause.auto_hint", "pause.auto_item", "pause.auto_tip",
            "pause.auto_no_campaign", "pause.auto_reset", "codex.expedition.cargo.title", "codex.expedition.cargo.body", "codex.expedition.cargo.hint",
        };

        private static void CheckData()
        {
            bool tuning = Mathf.Approximately(CargoService.PickupRadius, 6f) && Mathf.Approximately(CargoService.TransferRadius, 30f) && Mathf.Approximately(CargoService.WarnRatio, 0.8f)
                          && Mathf.Approximately(CargoService.CheckSeconds, 1f) && LootPickCatalog.MaterialPerSpace == 10;
            Expect(tuning, $"A1 调参来自 fg.TbHomeTuning：拾取半径 {CargoService.PickupRadius} 格、转移半径 {CargoService.TransferRadius} 格、快满比例 {CargoService.WarnRatio}、对账 {CargoService.CheckSeconds} 秒、材料每 {LootPickCatalog.MaterialPerSpace} 件 1 格");

            var space = new (string Item, int Amount, int Expect)[]
            {
                ("metal_ore", 10, 1), ("metal_ore", 11, 2), ("rare_earth_ore", 1, 1), (AnalysisCatalog.EncryptedFirmwareId, 1, 1), ("combat_component", 1, 3), ("structure_module", 2, 6),
                (AnalysisCatalog.DataCoreId, 1, 2), (AnalysisCatalog.UnparsedModuleId, 1, 3), ("human_legacy", 1, 2), (BlackBoxService.ContentId, 1, 1),
            };
            string bad = string.Join("，", space.Where(x => LootPickCatalog.SpaceOf(x.Item, x.Amount) != x.Expect).Select(x => $"{x.Item}×{x.Amount}={LootPickCatalog.SpaceOf(x.Item, x.Amount)}≠{x.Expect}"));
            string scrapRes = ItemCatalog.ResourceTypeOf(ItemCatalog.ScrapId);
            bool demoSame = HomeValleyCargo.SlotsForCargo(scrapRes, 40) == LootPickCatalog.SpaceOf(ItemCatalog.ScrapId, 40) && HomeValleyCargo.SlotsForCargo(scrapRes, 40) == 4
                            && HomeValleyCargo.CheckExpeditionLoadout(new[] { 4 }, new[] { new CargoEntry { ResourceType = scrapRes, Amount = 41 } }).RequiredSlots == 5;
            Expect(bad.Length == 0 && demoSame,
                "A2 FGR-LOOT-030 占用初值（材料每 10 件 1、固件 1、组件 3、数据核心 2、未解析模块 3、人类遗产 2、黑匣子 1）" + (bad.Length > 0 ? "：不符 " + bad : string.Empty) +
                $"；Demo 的出发货位换算改读同一规则（废料 40 件 = {HomeValleyCargo.SlotsForCargo(scrapRes, 40)} 格，原 Demo 写死“每 40 占 1 位”）——不另起一套格子");

            CampaignState s = NewWorld(8300);
            Vector2 at = CoreNear(s);
            int wheel = Spawn(s, at, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            int track = Spawn(s, at + new Vector2(2f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            int hover = Spawn(s, at + new Vector2(4f, 0f), GameLogic.Campaign.Content.ChassisCatalog.ChassisHoverId, HomeValleyLayout.BlueprintHoverId);
            int e001 = Spawn(s, at + new Vector2(6f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int cw = LootPickCatalog.CapacityOf(s, Rec(wheel));
            int ct = LootPickCatalog.CapacityOf(s, Rec(track));
            int ch = LootPickCatalog.CapacityOf(s, Rec(hover));
            int ce = LootPickCatalog.CapacityOf(s, Rec(e001));
            bool intelSame = new[] { wheel, track, hover, e001 }.All(id => ExpeditionDepartureService.BuildIntel(s, Rec(id)).CargoSlots == LootPickCatalog.CapacityOf(s, Rec(id)));
            bool caps = ct == 4 && ch == 6 && (cw == 12 || cw == 18) && cw > ct && ce >= 12 && ce > ch;
            int first = ExpeditionDepartureService.MinRecommendedCargoSlots(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, null);
            Expect(caps && intelSame && first == 16,
                $"A3 容量来自底盘与货舱结构模块：搬运轮式 {cw}（最大）、ERC-001（带货舱结构模块）{ce}、维修悬浮 {ch}、战斗履带 {ct}；出发检查单 / 派遣准备的货舱容量改读同一个函数（{intelSame}），建议容量 {first} 格（第一次）");

            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in TextKeys)
                {
                    string t = GameText.Get(k);
                    if (!GameText.Has(k) || string.IsNullOrEmpty(t) || GameText.ContainsMarker(t) || (lang == GameLanguage.En && ContainsCjk(t)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string[] hooks = { GuidanceHooks.LootFirstCargoFull, GuidanceHooks.LootFirstAutoPickup, GuidanceHooks.LootFirstCargoTransfer, GuidanceHooks.LootFirstCargoOpen };
            bool hooksOk = hooks.All(h => GuidanceHooks.Known.Contains(h));
            bool notify = NotificationCatalog.TryGetType(CargoService.NotifyType, out _);
            Expect(missing.Count == 0 && hooksOk && notify,
                $"A4 文本 {TextKeys.Length} 个键中英齐全、英文没有中文（缺 {string.Join(",", missing.Take(6))}）；4 个引导钩子（第一次货舱满 / 自动拾取 / 转移 / 打开货舱，只埋钩子）登记在 GuidanceHooks.Known；通知类型“cargo”");
        }

        // ── C 自动拾取 ───────────────────────────────────────────────────────────

        private static void CheckAutoPickup()
        {
            CampaignState s = NewWorld(8301);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 1);
            int hauler = Spawn(s, field + new Vector2(0f, 3f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int fighter = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            s = Depart(s, fighter, hauler);
            LootPacketState st = LootPickService.StateOf(s);

            // C1 远征队成员走近 → 装进货舱（最近的、放得下的那台）；家园搬运单取消。
            Vector2 p1 = Pos(fighter) + new Vector2(1.5f, 0f);
            GroundItemRecord g1 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, p1, ItemCatalog.ResourceTypeOf("metal_ore"), 25, "cargotest:metal1");
            HomeValleyWorkOrders.TryCreateHaulPool(s, g1.GroundItemId);
            bool haulOpen = s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && o.SourceId == g1.GroundItemId && HomeValleyWorkOrders.IsActive(o));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord h1 = st.Held.FirstOrDefault(h => h.ItemId == "metal_ore" && h.Amount == 25);
            bool gone = HomeValleyCargo.FindGroundItem(s, g1.GroundItemId) == null;
            bool haulCancelled = !s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && o.SourceId == g1.GroundItemId && HomeValleyWorkOrders.IsActive(o));
            Expect(h1 != null && h1.Machine == fighter && h1.Space == 3 && h1.HeldId.StartsWith(CargoService.HeldPrefix, StringComparison.Ordinal) && gone && haulOpen && haulCancelled
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstAutoPickup) && st.CargoAutoPicked >= 1,
                $"C1 FGR-LOOT-030 自动拾取：远征队成员 {Name(fighter)} 走到 1.5 格内 → 金属矿 ×25 装进它的货舱（占 3 格，{h1?.HeldId}），地上没了；原来开着的家园搬运单取消（不白跑，{haulOpen}→{haulCancelled}）；第一次自动拾取钩子");

            // C2 按物品开关：关掉稀土矿 → 不捡；打开 → 捡。
            bool off = CargoService.SetAutoPickup(s, "rare_earth_ore", false);
            Vector2 p2 = Pos(hauler) + new Vector2(0f, 1.5f);
            GroundItemRecord g2 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, p2, ItemCatalog.ResourceTypeOf("rare_earth_ore"), 8, "cargotest:re1");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool stayed = HomeValleyCargo.FindGroundItem(s, g2.GroundItemId) != null && !st.Held.Any(h => h.ItemId == "rare_earth_ore");
            bool on = CargoService.SetAutoPickup(s, "rare_earth_ore", true);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool pickedAfter = HomeValleyCargo.FindGroundItem(s, g2.GroundItemId) == null && st.Held.Any(h => h.ItemId == "rare_earth_ore" && h.Amount == 8);
            Expect(off && stayed && on && pickedAfter && CargoService.IsAutoPickup(s, "alloy") && CargoService.AutoPickupItems().Any(d => d.Id == "rare_earth_ore"),
                $"C2 按材料种类开关：“自动拾取稀土矿：关”时队员站在旁边也不捡（留在地上 {stayed}）；打开后下一次对账就捡起（{pickedAfter}）；没设过的物品默认开（合金 {CargoService.IsAutoPickup(s, "alloy")}）");

            // C3 关键材料始终拾取，开关里也没有它。
            string key = ItemCatalog.Items.FirstOrDefault(d => KeyItemRetention.OfItem(d.Id) != KeyItemRetention.Kind.Ordinary && HomeValleyCargo.CanStore(d.ResourceType))?.Id;
            bool cantOff = key != null && !CargoService.SetAutoPickup(s, key, false) && CargoService.IsAutoPickup(s, key) && CargoService.AutoPickupItems().All(d => d.Id != key);
            GroundItemRecord g3 = key != null ? HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(hauler) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf(key), 1, "cargotest:key1") : null;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool keyPicked = g3 != null && HomeValleyCargo.FindGroundItem(s, g3.GroundItemId) == null && st.Held.Any(h => h.ItemId == key);
            Expect(cantOff && keyPicked, $"C3 关键材料（{key}）始终拾取：设置里没有它的开关、关不掉（{cantOff}），队员走近就装进货舱（{keyPicked}）");

            // C4 家园 / 带仓库的前哨站附近不自动拾取（交给搬运单）。
            Vector2 home = CoreNear(s);
            MoveTo(hauler, home);
            GroundItemRecord g4 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, home + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("alloy"), 5, "cargotest:home1");
            int held4 = st.Held.Count(h => h.Machine == hauler);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool notAtHome = LootPickService.IsDepotPosition(s, HomeValleyLayout.RegionId, Pos(hauler)) && HomeValleyCargo.FindGroundItem(s, g4.GroundItemId) != null
                             && st.Held.Count(h => h.Machine == hauler) == held4;
            Expect(notAtHome, $"C4 负向：队员在家园范围内（离归还核心 / 仓库 {LootPickCatalog.DepotRadius} 格内）时，旁边的合金不自动拾取，留给家园搬运单（{notAtHome}）");

            // C5 放不下只捡一部分（材料按件拆），满了提示。
            MoveTo(hauler, field + new Vector2(25f, 0f));
            WorldSimulation.StepMany(3);
            int free = CargoService.Free(s, Rec(hauler));
            int amount = free * 10 + 30;
            int full0 = CargoNotes("已满");
            GroundItemRecord g5 = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(hauler) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), amount, "cargotest:big1");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            GroundItemRecord left = HomeValleyCargo.FindGroundItem(s, g5.GroundItemId);
            bool partial = free > 0 && left != null && left.Amount == 30 && CargoService.Free(s, Rec(hauler)) == 0
                           && st.Held.Any(h => h.Machine == hauler && h.ItemId == "metal_ore" && h.Amount == free * 10);
            Expect(partial && CargoNotes("已满") > full0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstCargoFull),
                $"C5 放不下全部时：{Name(hauler)} 空余 {free} 格 → 金属矿 ×{amount} 只捡 {free * 10} 件、地上留 {left?.Amount}（不塞、不丢）；装满时通知“已满”一次，第一次货舱满的引导钩子");
        }

        // ── D 转移 ──────────────────────────────────────────────────────────────

        private static void CheckTransfer()
        {
            CampaignState s = NewWorld(8302);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 2);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            int b = Spawn(s, field + new Vector2(4f, 0f), HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            int c = Spawn(s, field + new Vector2(0f, 10f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id); // 不在队里的第三台（太远 / 阵亡目标用）
            s = Depart(s, a, b);
            LootPacketState st = LootPickService.StateOf(s);
            MoveTo(c, Pos(a) + new Vector2(0f, 14f));
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(b) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), 60, "cargotest:t60");
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(-1f, 0f), ItemCatalog.ResourceTypeOf("alloy"), 20, "cargotest:t20");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord big = st.Held.FirstOrDefault(h => h.Machine == b && h.Amount == 60);
            LootHeldRecord small = st.Held.FirstOrDefault(h => h.Machine == a && h.Amount == 20);
            if (big == null || small == null)
            {
                Fail($"D 夹具：没有自动拾取到两份货（b={big != null}，a={small != null}）");
                return;
            }

            // D1 没暂停被拒。
            bool r1 = CargoService.TryTransfer(s, big.HeldId, a, out string m1);
            Expect(!r1 && m1 == GameText.Get("cargo.reason.pause") && big.Machine == b,
                $"D1 FGR-LOOT-030 战略暂停中才能转移：运行中被拒（“{m1}”），货不动");

            GameClock.SetPaused(true);
            // D2 暂停中转移。
            bool r2 = CargoService.TryTransfer(s, small.HeldId, b, out string m2);
            Expect(r2 && small.Machine == b && m2.Contains(Name(b)) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstCargoTransfer) && st.CargoTransfers >= 1,
                $"D2 暂停中把 {Name(a)} 的合金 ×20 转给 {Name(b)}：“{m2}”；第一次转移钩子");

            // D3 材料拆开转：a 只空 4 格 → 金属矿 60 件转 40 件过去、b 留 20 件。
            int freeA = CargoService.Free(s, Rec(a));
            bool r3 = CargoService.TryTransfer(s, big.HeldId, a, out string m3);
            LootHeldRecord part = st.Held.FirstOrDefault(h => h.Machine == a && h.ItemId == "metal_ore");
            Expect(r3 && freeA == 4 && part != null && part.Amount == 40 && big.Amount == 20 && big.Space == 2 && CargoService.Free(s, Rec(a)) == 0,
                $"D3 材料拆开转：{Name(a)} 空余 {freeA} 格 → 金属矿转 {part?.Amount} 件过去、{Name(b)} 留 {big.Amount} 件（“{m3}”）");

            // D4 负向“转移时目标机器也满”：a 已满 → 拒绝，两边都不变。
            int usedA = CargoService.Used(s, Rec(a));
            int usedB = CargoService.Used(s, Rec(b));
            bool r4 = CargoService.TryTransfer(s, big.HeldId, a, out string m4);
            Expect(!r4 && m4 == GameText.Format("cargo.reason.full", Name(a), big.Space, 0) && CargoService.Used(s, Rec(a)) == usedA && CargoService.Used(s, Rec(b)) == usedB && big.Machine == b,
                $"D4 负向：目标 {Name(a)} 也满了 → 拒绝（“{m4}”），不塞、不丢，两边占用不变（{usedA} / {usedB}）");

            // D5 太远：c 在 14 格外不算远；挪到 40 格外再转。
            MoveTo(c, Pos(b) + new Vector2(40f, 0f));
            bool r5 = CargoService.TryTransfer(s, big.HeldId, c, out string m5);
            var targets = new List<int>();
            CargoService.TransferTargets(s, b, targets);
            Expect(!r5 && m5.Contains("太远") && !targets.Contains(c) && targets.Contains(a) && big.Machine == b,
                $"D5 负向：目标在 {CargoService.TransferRadius} 格外 → 拒绝（“{m5}”），转移目标列表里也没有它");

            // D6 工单在途的货只读。
            Rec(a).Cargo = new[] { new CargoEntry { ResourceType = ItemCatalog.ResourceTypeOf(ItemCatalog.ScrapId), Amount = 5 } };
            var lines = new List<CargoLine>();
            CargoService.LinesOf(s, a, lines);
            CargoLine job = lines.FirstOrDefault(l => l.Kind == CargoService.LineJob);
            bool r6 = CargoService.TryTransfer(s, job.Id, b, out string m6);
            bool d6 = CargoService.TryDiscard(s, job.Id, out string m6b);
            Rec(a).Cargo = Array.Empty<CargoEntry>();
            Expect(job.Id != null && !job.Movable && !r6 && !d6 && m6 == GameText.Get("cargo.reason.job") && m6b == m6,
                $"D6 工单在途的货（{job.Text}）只读：转移 / 丢弃都被拒（“{m6}”）");

            // D7 黑匣子转移 + 目标阵亡：地上一个队友的黑匣子（测试捷径：等同于另一名队员在这里阵亡，阵亡掉落由 F1 用真实阵亡覆盖）被队员自动捡起，再在暂停中转给另一名队员。
            GameClock.SetPaused(false);
            Vector2 cDeath = Pos(a) + new Vector2(0f, 2f);
            var box = new RegionQuestItemRecord
            {
                SalvageInstanceId = BlackBoxService.InstanceIdOf(90000 + c), RegionId = HomeValleyLayout.RegionId, ContentId = BlackBoxService.ContentId,
                State = RegionQuestItemState.OnGround, Position = cDeath,
            };
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Append(box).ToArray();
            if (CargoService.Free(s, Rec(a)) < 1 && CargoService.Free(s, Rec(b)) < 1)
            {
                CargoService.TryDiscard(s, big.HeldId, out _);
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            MachineRegistry.ApplyDamage(c, 1e6f);
            WorldSimulation.StepMany(3);
            int carrier = box.CarrierLogicId;
            GameClock.SetPaused(true);
            int other = carrier == a ? b : a;
            if (CargoService.Free(s, Rec(other)) < 1)
            {
                LootHeldRecord spare = st.Held.FirstOrDefault(x => x.Machine == other);
                if (spare != null)
                {
                    CargoService.TryDiscard(s, spare.HeldId, out _); // 目标已满：先丢掉一份腾出位置
                }
            }
            bool r7 = box != null && CargoService.TryTransfer(s, CargoService.QuestPrefix + box.SalvageInstanceId, other, out string m7) && box.CarrierLogicId == other;
            bool r8 = !CargoService.TryTransfer(s, big.HeldId, c, out string m8) && m8 == GameText.Get("cargo.reason.target_dead");
            GameClock.SetPaused(false);
            Expect(box.State == RegionQuestItemState.Carried && (carrier == a || carrier == b) && r7 && r8,
                $"D7 黑匣子也能转移：地上的黑匣子（{Mathf.RoundToInt(cDeath.x)},{Mathf.RoundToInt(cDeath.y)}）被队员 {Name(carrier)} 自动捡起（占 1 格），暂停中转给 {Name(other)}（{r7}）；转给已阵亡的机器被拒（“{m8}”）");
        }

        // ── E 丢弃 ──────────────────────────────────────────────────────────────

        private static string FirmwareTarget() => LootPickCatalog.Rows.First(r => r.Kind == LootPickCatalog.KindFirmware).Target;

        private static LootHeldRecord GiveFirmware(CampaignState s, int machine, string id)
        {
            LootPacketState st = LootPickService.StateOf(s);
            var h = new LootHeldRecord
            {
                HeldId = id, Machine = machine, Kind = LootPickCatalog.KindFirmware, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = FirmwareTarget(), Amount = 1,
                Space = LootPickCatalog.SpaceOf(AnalysisCatalog.EncryptedFirmwareId, 1), Faction = "silent",
            };
            st.Held = st.Held.Append(h).ToArray(); // 测试捷径：等同于在野外分拣得到一份加密固件（分拣流程由 FgLootPickSelfCheck 覆盖）
            return h;
        }

        private static void CheckDiscard()
        {
            CampaignState s = NewWorld(8303);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 3);
            int a = Spawn(s, field, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a);
            LootPacketState st = LootPickService.StateOf(s);
            LootHeldRecord fw = GiveFirmware(s, a, LootPickService.HeldPrefix + "cargotest-fw");
            string target = fw.TargetId;
            bool ok = CargoService.TryDiscard(s, fw.HeldId, out string msg);
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, fw.HeldId);
            LootGroundTagRecord tag = FieldLootService.TagOf(s, fw.HeldId);
            Expect(ok && g != null && Vector2.Distance(g.Position, Pos(a)) < 2f && tag != null && tag.TargetId == target && !st.Held.Contains(fw)
                   && st.CargoDiscarded.Contains(fw.HeldId) && msg.Contains("落在"),
                $"E1 丢弃：加密固件落在 {Name(a)} 旁边（“{msg}”），带着身份（{tag?.TargetId}），可以再捡回");

            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool stays = HomeValleyCargo.FindGroundItemBySalvageId(s, fw.HeldId) != null && !st.Held.Any(h => h.TargetId == target);
            Expect(stays, "E2 刚丢弃的货：队员还在旁边时不会被自动拾取马上捡回去（3 游戏秒后仍在地上）");

            Vector2 spot = g.Position;
            MoveTo(a, spot + new Vector2(CargoService.DiscardClearRadius + 8f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool cleared = !st.CargoDiscarded.Contains(fw.HeldId);
            MoveTo(a, spot + new Vector2(1f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord back = st.Held.FirstOrDefault(h => h.TargetId == target && h.Machine == a);
            Expect(cleared && back != null && back.Kind == LootPickCatalog.KindFirmware && back.ItemId == AnalysisCatalog.EncryptedFirmwareId && FieldLootService.TagOf(s, fw.HeldId) == null
                   && HomeValleyCargo.FindGroundItemBySalvageId(s, fw.HeldId) == null,
                $"E3 走开（{CargoService.DiscardClearRadius} 格外）再回来 → 自动拾取捡回，身份随货舱记录走（{back?.Kind} / {back?.TargetId}），地上的身份登记移走");
        }

        /// <summary>E4（关闭 DEBT-FG8LOOT01-06）：家园里丢下一份加密固件、仓库对这种物品已满 → 搬运单照常交付成固件库里的未破解芯片（不等仓库空间）。</summary>
        private static void CheckEncryptedHaul()
        {
            CampaignState s = NewWorld(8310);
            EnsureWarehouse(s);
            Vector2 near = CoreNear(s) + new Vector2(3f, 3f);
            int carrier = Spawn(s, near, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            ItemCatalog.TryGet(AnalysisCatalog.EncryptedFirmwareId, out ItemDef def);
            string res = def?.ResourceType;
            int room = res != null ? HomeValleyCargo.GetAvailableSpace(s, res) : 0;
            if (def != null && room > 0)
            {
                HomeInventory.Add(s, def, room, clampToSpace: false);
            }
            bool full = res != null && HomeValleyCargo.GetAvailableSpace(s, res) == 0;
            LootHeldRecord fw = GiveFirmware(s, carrier, LootPickService.HeldPrefix + "cargotest-homefw");
            int chips0 = Chips(s, fw.TargetId);
            CargoService.TryDiscard(s, fw.HeldId, out _);
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, fw.HeldId);
            bool needsNone = FieldLootService.NeedsNoStorage(s, fw.HeldId);
            bool delivered = StepUntil(() => Chips(s, fw.TargetId) > chips0, 1800);
            bool noWait = !s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && o.FailureReason != null && o.FailureReason.StartsWith("storage-full") && HomeValleyWorkOrders.IsActive(o));
            Expect(full && g != null && needsNone && delivered && noWait,
                $"E4 DEBT-FG8LOOT01-06：仓库对加密固件已满（{full}）时，地上带身份的加密固件照常被搬回、交付成固件库里的未破解芯片（{chips0} → {Chips(s, fw.TargetId)}），搬运单不卡在“仓满等待”（{noWait}）");
        }

        // ── F 阵亡掉落 ───────────────────────────────────────────────────────────

        private static Vector2 EnemyZonePoint(CampaignState s, out PlannedTerritory territory)
        {
            territory = null;
            WorldPlan plan = WorldGenService.PlanFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            foreach (PlannedTerritory t in plan?.Territories ?? new List<PlannedTerritory>())
            {
                if (!t.IsFaction)
                {
                    continue;
                }
                var c = new Vector2(t.CenterX, t.CenterY);
                Vector2 dir = (new Vector2(core.X, core.Y) - c).normalized;
                for (float f = 0.6f; f >= 0.1f; f -= 0.1f)
                {
                    Vector2 raw = c + dir * (t.Radius * f);
                    GridCell ok = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(raw.x), Mathf.RoundToInt(raw.y)), 12);
                    PlannedTerritory at = WorldGenQuery.TerritoryAt(s, ok.X, ok.Y);
                    if (at != null && at.Id == t.Id)
                    {
                        territory = t;
                        return new Vector2(ok.X, ok.Y);
                    }
                }
            }
            return FieldPoint(s, 120f, 5);
        }

        private static void CheckDeathDrop()
        {
            CampaignState s = NewWorld(8304);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 4);
            int a = Spawn(s, field, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int b = Spawn(s, field + new Vector2(3f, 0f), HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            s = Depart(s, a, b);
            LootPacketState st = LootPickService.StateOf(s);
            MoveTo(b, Pos(a) + new Vector2(30f, 0f));
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("alloy"), 15, "cargotest:d1");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord fw = GiveFirmware(s, a, LootPickService.HeldPrefix + "cargotest-deadfw");
            string[] heldIds = st.Held.Where(h => h.Machine == a).Select(h => h.HeldId).ToArray();

            // 负向“阵亡机器的货物被敌人区域覆盖”：a 走进敌方领地后阵亡。
            Vector2 zone = EnemyZonePoint(s, out PlannedTerritory territory);
            MoveTo(a, zone);
            WorldSimulation.StepMany(3);
            Vector2 death = Pos(a);
            int note0 = CargoNotes("掉在原地");
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(3);
            List<GroundItemRecord> dropped = heldIds.Select(id => HomeValleyCargo.FindGroundItemBySalvageId(s, id)).Where(x => x != null).ToList();
            RegionQuestItemRecord box = BlackBoxService.FindItem(s, a);
            bool boxDown = box != null && box.State == RegionQuestItemState.OnGround && box.RegionId == HomeValleyLayout.RegionId && Vector2.Distance(box.Position, death) < 0.5f
                           && BlackBoxService.Find(s, a) == null;
            bool tagKept = FieldLootService.TagOf(s, fw.HeldId)?.TargetId == fw.TargetId;
            Expect(heldIds.Length == 2 && dropped.Count == 2 && dropped.All(x => Vector2.Distance(x.Position, death) < 0.5f) && tagKept && boxDown && CargoNotes("掉在原地") > note0
                   && !st.Held.Any(h => h.Machine == a),
                $"F1 FGR-EXP-032 机器阵亡：{Name(a)} 货舱里的 {dropped.Count} 份货掉在阵亡处（{Mathf.RoundToInt(death.x)},{Mathf.RoundToInt(death.y)}），加密固件身份保留（{tagKept}）；" +
                $"在外的远征队成员在星球上野外阵亡，黑匣子也掉在原地、不进陈列馆队列（{boxDown}）；阵亡通知写明货掉在哪（可点击定位）");

            // F2 地图标记（FG-GAP-104）+ 敌方领地写明。
            var markers = new List<CargoService.GroundMarker>();
            CargoService.CollectGroundMarkers(s, markers);
            bool marked = markers.Count(m => m.Kind == "cargo") >= 2 && markers.Any(m => m.Kind == "blackbox");
            string enemyLabel = territory != null ? GameText.Format("cargo.map.enemy", GameText.Get(territory.NameKey)) : null;
            bool zoneNoted = territory == null || markers.Where(m => m.Kind != "packet").All(m => m.Label.EndsWith(enemyLabel, StringComparison.Ordinal));
            var model = new WorldMapModel();
            var view = new WorldMapView { CenterX = death.x, CenterY = death.y, HalfWidth = 60, CanvasWidth = 600, CanvasHeight = 600 };
            model.Collect(s, view, 64);
            int onMap = model.Items.Count(i => i.Layer == WorldMapLayer.Loot);
            WorldMapFilters.Set(WorldMapLayer.Loot, false);
            model.Collect(s, view, 64);
            int offMap = model.Items.Count(i => i.Layer == WorldMapLayer.Loot);
            WorldMapFilters.Set(WorldMapLayer.Loot, true);
            Expect(marked && zoneNoted && onMap >= 3 && offMap == 0 && territory != null,
                $"F2 FG-GAP-104 地图 / 小地图标记：掉落的货、黑匣子各有图标（地图上 {onMap} 个，关掉“地上的战利品”图层后 {offMap} 个），标签写明在敌方领地里（“{markers.FirstOrDefault(m => m.Kind == "blackbox").Label}”）");

            // F3 地面外观：地上的黑匣子 / 数据包有占位外观。
            LootPackageRecord packet = LootPickService.Grant(s, LootPickService.SourceElite, "silent", 0, HomeValleyLayout.RegionId, death + new Vector2(2f, 0f));
            for (int i = 0; i < 4; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            GroundLootVisuals visuals = WorldSimulation.Home?.LootVisuals;
            bool shown = visuals != null && visuals.Has(GroundLootVisuals.BoxId(box)) && visuals.Has(GroundLootVisuals.PacketId(packet));
            Expect(shown, $"F3 FG-GAP-104 地面外观：地上的黑匣子与数据包在家园场景里有占位外观（{visuals?.Count} 个；掉落的货沿用地面物外观），美术阶段替换（DEBT-FG8LOOT03-01）");

            // F4 敌方领地里放 120 游戏秒：不消失（普通战利品 1 日后消失归 FG8-EXP-05），队友走进去照样自动捡回。
            // 家园的搬运单也会去取地上的货（FG8-LOOT-01 的野外掉落同一条路），所以“不丢”= 每一份要么还在地上、要么在搬运单里 / 已经交付进家园。
            int inv0 = HomeInventory.Stock(s, "alloy");
            int chips0 = Chips(s, fw.TargetId);
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool InTransit(string id) => s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && HomeValleyWorkOrders.IsActive(o) && o.SourceId != null && o.SourceId.Contains(id));
            bool kept = heldIds.All(id => HomeValleyCargo.FindGroundItemBySalvageId(s, id) != null || InTransit(id)) || HomeInventory.Stock(s, "alloy") > inv0 || Chips(s, fw.TargetId) > chips0;
            bool boxStays = box.State == RegionQuestItemState.OnGround && Vector2.Distance(box.Position, death) < 0.5f;
            string[] stillDown = heldIds.Where(id => HomeValleyCargo.FindGroundItemBySalvageId(s, id) != null).ToArray();
            MoveTo(b, death + new Vector2(1.5f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool regained = stillDown.All(id => HomeValleyCargo.FindGroundItemBySalvageId(s, id) == null) && box.State == RegionQuestItemState.Carried && box.CarrierLogicId == b
                            && (!stillDown.Contains(fw.HeldId) || st.Held.Any(h => h.Machine == b && h.TargetId == fw.TargetId && h.Kind == LootPickCatalog.KindFirmware));
            Expect(kept && boxStays && regained,
                $"F4 负向“阵亡机器的货物被敌人区域覆盖”：掉在{(territory != null ? GameText.Get(territory.NameKey) : "野外")}里的货与黑匣子 120 游戏秒后一份不少（还在地上 {stillDown.Length} 份，其余在搬运途中 / 已入库），黑匣子仍在原地（{boxStays}）；" +
                $"队友 {Name(b)} 走过去自动捡回地上的货（含身份）和黑匣子（{box.State}）");

            // F5 黑匣子带回家园即回收。
            int bb0 = CargoNotes("黑匣子陈列馆");
            MoveTo(b, CoreNear(s));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Expect(box.State == RegionQuestItemState.Recovered && BlackBoxService.Find(s, a) != null && CargoNotes("黑匣子陈列馆") > bb0,
                $"F5 黑匣子带回家园 / 带仓库的前哨站即回收：{Name(b)} 回到核心旁 → 进陈列馆队列（{BlackBoxService.Find(s, a) != null}），通知一条");

            // F6 回归：不在远征队里的家园机器阵亡，黑匣子照旧直接回收。
            int h = Spawn(s, FieldPoint(s, 60f, 6), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            MachineRegistry.ApplyDamage(h, 1e6f);
            WorldSimulation.StepMany(3);
            Expect(BlackBoxService.Find(s, h) != null, $"F6 回归：不在远征队里的 {Name(h)} 在家园星球上阵亡，黑匣子照旧直接回收（ADR-RND-006）");
        }

        // ── G 快满 / 已满 ───────────────────────────────────────────────────────

        private static void CheckWarn()
        {
            CampaignState s = NewWorld(8305);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 5);
            int a = Spawn(s, field, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            s = Depart(s, a);
            LootPacketState st = LootPickService.StateOf(s);
            int cap = CargoService.Capacity(s, Rec(a));
            int thr = CargoService.WarnThreshold(cap);
            int warn0 = CargoNotes("快满了");
            int full0 = CargoNotes("已满");
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), (thr - 1) * 10, "cargotest:w1");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool quiet = CargoNotes("快满了") == warn0;
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("alloy"), 10, "cargotest:w2");
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool warned = CargoNotes("快满了") == warn0 + 1 && CargoService.Level(CargoService.Used(s, Rec(a)), cap) == 1;
            Expect(quiet && warned, $"G1 卡片“货舱快满的预警”：{Name(a)} 占用到 {thr - 1} / {cap} 时不提示；到 {thr} / {cap}（≥ {CargoService.WarnRatio:P0}）提示“快满了”一次（3 游戏秒内不重复）");

            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(0f, 1f), ItemCatalog.ResourceTypeOf("metal_ore"), (cap - thr) * 10, "cargotest:w3");
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            bool fulled = CargoNotes("已满") == full0 + 1 && CargoService.Free(s, Rec(a)) == 0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstCargoFull);
            Expect(fulled, $"G2 装满（{CargoService.Used(s, Rec(a))} / {cap}）：通知“已满”一次，第一次货舱满的引导钩子（只埋钩子，引导内容在 FG15-UX-04）");

            LootHeldRecord one = st.Held.First(h => h.Machine == a);
            CargoService.TryDiscard(s, one.HeldId, out _);
            MoveTo(a, Pos(a) + new Vector2(20f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool reset = CargoService.Level(CargoService.Used(s, Rec(a)), cap) < 2;
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), one.Amount, "cargotest:w4");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Expect(reset && CargoNotes("已满") == full0 + 2,
                $"G3 丢掉一些降下来后记录清掉，再装满时再提示一次（已满通知共 {CargoNotes("已满") - full0} 条）");
        }

        // ── M 同种材料合并 / N 放不下但没满 / V 远征地点的地面外观（审查第 1 轮补测）────────────

        private static void CheckMerge()
        {
            CampaignState s = NewWorld(8306);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 3);
            int a = Spawn(s, field, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            int b = Spawn(s, field + new Vector2(4f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            s = Depart(s, a, b);
            LootPacketState st = LootPickService.StateOf(s);
            string scrap = ItemCatalog.ScrapId;
            int used0 = CargoService.Used(s, Rec(a));
            int usedB0 = CargoService.Used(s, Rec(b));

            // M1 同一台机器先后捡 3 堆各 3 件废料（每堆之间等一次对账 = 3 次独立拾取）：合成一条，占 1 格，面板一行。
            for (int i = 0; i < 3; i++)
            {
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(-1.5f, 0.6f * i), ItemCatalog.ResourceTypeOf(scrap), 3, "cargotest:merge" + i);
                WorldSimulation.StepMany(GameClock.StepHz * 2);
            }
            LootHeldRecord[] rows = st.Held.Where(h => h.Machine == a && h.ItemId == scrap).ToArray();
            var lines = new List<CargoLine>();
            CargoService.LinesOf(s, a, lines);
            int scrapLines = lines.Count(l => l.ItemId == scrap);
            int used1 = CargoService.Used(s, Rec(a)) - used0;
            Expect(rows.Length == 1 && rows[0].Amount == 9 && rows[0].Space == 1 && used1 == 1 && scrapLines == 1 && lines.Where(l => l.ItemId == scrap).Sum(l => l.Space) == 1,
                $"M1 FGR-LOOT-030“材料每 10 个占 1”按总件数算：{Name(a)} 先后捡 3 堆各 3 件废料 → {rows.Length} 条、共 {rows.FirstOrDefault()?.Amount} 件，多占 {used1} 格（不是每堆各 1 格），货舱面板 {scrapLines} 行");

            // M2 再捡一堆 4 件 → 13 件 = 2 格（仍是一条）。
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(-1.5f, -0.6f), ItemCatalog.ResourceTypeOf(scrap), 4, "cargotest:merge3");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            rows = st.Held.Where(h => h.Machine == a && h.ItemId == scrap).ToArray();
            int used2 = CargoService.Used(s, Rec(a)) - used0;
            Expect(rows.Length == 1 && rows[0].Amount == 13 && used2 == 2,
                $"M2 第 4 堆 4 件：合计 {rows.FirstOrDefault()?.Amount} 件，占 {used2} 格（13 件向上取整 = 2），仍是 {rows.Length} 条");

            // M3 转移落到目标机器时也并入同种材料：b 的 5 件废料（测试捷径：等同于 b 在野外捡到一份）暂停中转给 a → a 一条 18 件、2 格，b 那条没了。
            var give = new LootHeldRecord
            {
                HeldId = "cargotest-merge-b", Machine = b, Kind = LootPickCatalog.KindItem, ItemId = scrap, Amount = 5, Space = LootPickCatalog.SpaceOf(scrap, 5),
            };
            st.Held = st.Held.Append(give).ToArray();
            GameClock.SetPaused(true);
            bool r = CargoService.TryTransfer(s, give.HeldId, a, out string msg);
            GameClock.SetPaused(false);
            rows = st.Held.Where(h => h.Machine == a && h.ItemId == scrap).ToArray();
            Expect(r && rows.Length == 1 && rows[0].Amount == 18 && !st.Held.Contains(give) && CargoService.Used(s, Rec(a)) - used0 == 2 && CargoService.Used(s, Rec(b)) == usedB0,
                $"M3 转移也合并：{Name(b)} 的废料 ×5 转给 {Name(a)}（“{msg}”）→ {Name(a)} {rows.Length} 条、共 {rows.FirstOrDefault()?.Amount} 件、多占 {CargoService.Used(s, Rec(a)) - used0} 格；{Name(b)} 的那条已移走");
        }

        private static void CheckNoRoom()
        {
            CampaignState s = NewWorld(8307);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 4);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            s = Depart(s, a);
            LootPacketState st = LootPickService.StateOf(s);
            int cap = CargoService.Capacity(s, Rec(a));
            int fill = Math.Max(0, cap - 2 - CargoService.Used(s, Rec(a)));
            var held = new LootHeldRecord
            {
                HeldId = "cargotest-noroom", Machine = a, Kind = LootPickCatalog.KindItem, ItemId = "metal_ore", Amount = fill * 10, Space = fill,
            };
            st.Held = st.Held.Append(held).ToArray(); // 测试捷径：等同于之前捡过 fill×10 件金属矿
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int all0 = CargoNotes(string.Empty);
            int full0 = CargoNotes("已满");
            int nr0 = CargoNotes("放不下");
            int free = CargoService.Free(s, Rec(a));
            int need = LootPickCatalog.SpaceOf("combat_component", 1);

            // N1 负向：空 2 格，旁边一件作战组件（占 3），原地待命 5 游戏秒 → 不捡；不发“已满”；“放不下”只提示 1 条（不是每个对账秒一条）。
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1.5f, 0f), ItemCatalog.ResourceTypeOf("combat_component"), 1, "cargotest:comp1");
            int rev0 = CargoService.Revision;
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool stays = HomeValleyCargo.FindGroundItem(s, g.GroundItemId) != null && !st.Held.Any(h => h.Machine == a && h.ItemId == "combat_component");
            int notes = CargoNotes(string.Empty) - all0;
            Expect(free == 2 && need == 3 && stays && CargoNotes("已满") == full0 && CargoNotes("放不下") == nr0 + 1 && notes == 1 && CargoService.Level(CargoService.Used(s, Rec(a)), cap) < 2,
                $"N1 负向“放不下这件但货舱没满”：{Name(a)} 空 {free} 格、作战组件要 {need} 格 → 5 游戏秒内不捡（{stays}），不发“已满”（{CargoNotes("已满") - full0} 条），" +
                $"只提示一次“放不下”（货舱通知共 {notes} 条；修订号 {rev0}→{CargoService.Revision}）");

            // N2 腾出位置后照常捡起（不是卡死）。
            CargoService.TryDiscard(s, held.HeldId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool picked = HomeValleyCargo.FindGroundItem(s, g.GroundItemId) == null && st.Held.Any(h => h.Machine == a && h.ItemId == "combat_component");
            Expect(picked, $"N2 丢掉金属矿腾出 {CargoService.Capacity(s, Rec(a))} 格后，作战组件下一次对账就装进 {Name(a)} 的货舱（{picked}）");
        }

        private static void CheckSiteVisuals()
        {
            CampaignState s = NewWorld(8308);
            int a = Spawn(s, FieldPoint(s, 60f, 2), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            WorldSimulation.StepMany(3);
            var parts = new List<string>();
            bool all = true;
            void Site(string region, Func<string, int, bool> collect, int serial)
            {
                var box = new RegionQuestItemRecord
                {
                    SalvageInstanceId = BlackBoxService.InstanceIdOf(serial), RegionId = region, ContentId = BlackBoxService.ContentId,
                    State = RegionQuestItemState.OnGround, Position = new Vector2(10f, 10f),
                };
                s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Append(box).ToArray();
                var root = new GameObject("FgCargoSelfCheck_SiteVisuals_" + region);
                var visuals = new GroundLootVisuals();
                try
                {
                    visuals.Sync(s, region, root.transform, true);
                    bool shown = visuals.Has(GroundLootVisuals.BoxId(box));
                    // 就地改状态、不碰任何修订号：外观也要跟着消失（Sync 的对账键带上了地上黑匣子条数）。
                    box.State = RegionQuestItemState.Carried;
                    box.CarrierLogicId = a;
                    visuals.Sync(s, region, root.transform, true);
                    bool goneInPlace = !visuals.Has(GroundLootVisuals.BoxId(box));
                    box.State = RegionQuestItemState.OnGround;
                    box.CarrierLogicId = 0;
                    box.Position = new Vector2(10f, 10f);
                    visuals.Sync(s, region, root.transform, true);
                    bool back = visuals.Has(GroundLootVisuals.BoxId(box));
                    // 正式“装载”路径（远征地点的 TryCollectQuestItem）：成功后修订号前进，外观消失。
                    int rev0 = BlackBoxService.Revision;
                    int lrev0 = CargoService.Revision;
                    MachineRecord m = Rec(a);
                    string was = m.RegionId;
                    m.RegionId = region; // 测试捷径：把这台机器当作在远征地点里（区域切换由出发 / 远征旅程覆盖）
                    bool ok;
                    try
                    {
                        ok = collect(box.SalvageInstanceId, a);
                    }
                    finally
                    {
                        m.RegionId = was;
                    }
                    visuals.Sync(s, region, root.transform, true);
                    bool collected = ok && box.State == RegionQuestItemState.Carried && BlackBoxService.Revision > rev0 && CargoService.Revision > lrev0 && !visuals.Has(GroundLootVisuals.BoxId(box));
                    all &= shown && goneInPlace && back && collected;
                    parts.Add($"{region}：放下有外观 {shown}、就地装走后消失 {goneInPlace}、放回重现 {back}、“装载”后修订号前进且消失 {collected}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
            Site(FracturedCityLayout.RegionId, (sid, id) => FracturedCityRegion.TryCollectQuestItem(s, sid, id).Success, 92000 + a);
            Site(FoundryOutpostLayout.RegionId, (sid, id) => FoundryOutpostRegion.TryCollectQuestItem(s, sid, id).Success, 93000 + a);
            Expect(all, $"V1 FG-GAP-104 远征地点的地面外观：地上的黑匣子有占位外观，玩家“装载”后外观立刻消失（{string.Join("；", parts)}）");
        }

        // ── H 存读档 ────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            LootPacketState st = LootPickService.StateOf(s);
            var sb = new StringBuilder();
            foreach (LootHeldRecord h in st.Held)
            {
                sb.Append(h.HeldId).Append('@').Append(h.Machine).Append(':').Append(h.Kind).Append(':').Append(h.ItemId).Append('>').Append(h.TargetId).Append('x').Append(h.Amount).Append('/').Append(h.Space).Append('|');
            }
            sb.Append("off:").Append(string.Join(",", st.AutoPickupOff)).Append("|disc:").Append(string.Join(",", st.CargoDiscarded)).Append("|warn:").Append(string.Join(",", st.CargoWarned))
              .Append('/').Append(string.Join(",", st.CargoWarnLevels)).Append("|n:").Append(st.NextCargoSerial).Append("|st:").Append(st.CargoAutoPicked).Append('/').Append(st.CargoTransfers).Append('/').Append(st.CargoDiscards);
            foreach (RegionQuestItemRecord q in (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Where(BlackBoxService.IsBlackBox))
            {
                sb.Append("|bb:").Append(q.SalvageInstanceId).Append(':').Append(q.State).Append(':').Append(q.CarrierLogicId).Append(':').Append(Mathf.RoundToInt(q.Position.x)).Append(',').Append(Mathf.RoundToInt(q.Position.y));
            }
            foreach (GroundItemRecord g in (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("cargotest", StringComparison.Ordinal)))
            {
                sb.Append("|g:").Append(g.SalvageInstanceId).Append('x').Append(g.Amount);
            }
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8306);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 6);
            int a = Spawn(s, field, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            int c = Spawn(s, field + new Vector2(2f, 2f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            s = Depart(s, a, c);
            LootPacketState st = LootPickService.StateOf(s);
            CargoService.SetAutoPickup(s, "alloy", false);
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), 95, "cargotest:s1");
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(0f, 1f), ItemCatalog.ResourceTypeOf("alloy"), 7, "cargotest:s2");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            MachineRegistry.ApplyDamage(c, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord metal = st.Held.FirstOrDefault(h => h.ItemId == "metal_ore");
            if (metal != null)
            {
                GameClock.SetPaused(true);
                CargoService.TryDiscard(s, metal.HeldId, out _);
                GameClock.SetPaused(false);
            }
            string snap = Snapshot(s);
            long serial = st.NextCargoSerial;
            bool fixture = st.AutoPickupOff.Contains("alloy") && st.CargoDiscarded.Length == 1 && (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Any(q => BlackBoxService.IsBlackBox(q) && q.State == RegionQuestItemState.Carried);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            string snap2 = back != null ? Snapshot(back) : null;
            Expect(fixture && sv.Success && back != null && snap2 == snap,
                $"H1 存读档：自动拾取开关（合金关）、货舱记录（含黑匣子携带者）、刚丢弃的标记、快满记录、序号（{serial}）逐项一致（{snap.Length} 字节快照）");
            if (back == null)
            {
                return;
            }
            LootPacketState st2 = LootPickService.StateOf(back);
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(back, HomeValleyLayout.RegionId, Pos(a) + new Vector2(-1f, 0f), ItemCatalog.ResourceTypeOf("electronic"), 3, "cargotest:s3");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            LootHeldRecord fresh = st2.Held.FirstOrDefault(h => h.ItemId == "electronic");
            Expect(fresh != null && fresh.HeldId == CargoService.HeldPrefix + serial && !CargoService.IsAutoPickup(back, "alloy") && HomeValleyCargo.FindGroundItemBySalvageId(back, "cargotest:s2") != null,
                $"H2 读档后接着自动拾取（新货舱记录 {fresh?.HeldId}，序号接着编），合金仍然关着、地上的合金没被捡");
        }

        // ── I 暂停 / 倍速 / 观察 ─────────────────────────────────────────────────

        private static void CheckSpeedAndObservation()
        {
            string RunAt(float speed, bool pauseMid, bool observe)
            {
                CampaignState s = NewWorld(8307, observe);
                Vector2 field = FieldPoint(s, 70f, 0);
                int a = Spawn(s, field, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
                int b = Spawn(s, field + new Vector2(2f, 0f), HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
                s = Depart(s, a, b);
                Vector2 at = Pos(a);
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at + new Vector2(1f, 1f), ItemCatalog.ResourceTypeOf("metal_ore"), 33, "cargotest:i1");
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at + new Vector2(2f, -1f), ItemCatalog.ResourceTypeOf("rare_earth_ore"), 12, "cargotest:i2");
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at + new Vector2(30f, 0f), ItemCatalog.ResourceTypeOf("alloy"), 4, "cargotest:i3");
                GameClock.SetSpeed(speed);
                bool pausedOk = true;
                if (pauseMid)
                {
                    GameClock.SetPaused(true);
                    for (int k = 0; k < 120; k++)
                    {
                        WorldSimulation.Frame(1f / 60f);
                    }
                    pausedOk = LootPickService.StateOf(s).Held.Length == 0;
                    GameClock.SetPaused(false);
                }
                long target = GameClock.Ticks + GameClock.StepHz * 3;
                for (int frames = 0; GameClock.Ticks < target && frames < 20000; frames++)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                GameClock.SetSpeed(1f);
                return (pausedOk ? string.Empty : "PAUSE-MOVED ") + Snapshot(s);
            }
            string r05 = RunAt(0.5f, false, true);
            string r1 = RunAt(1f, true, true);
            string r3 = RunAt(3f, false, true);
            string hidden = RunAt(1f, false, false);
            Expect(r05 == r1 && r1 == r3 && r1 == hidden && !r1.Contains("PAUSE") && r1.Contains("cargo:1"),
                $"I1 B09 暂停与倍速 + FGR-BASE-021：同样的地上货在 0.5x / 1x（暂停 2 秒真实时间期间不拾取）/ 3x、以及镜头不在家园时，自动拾取的结果逐项相同（{r1.Length} 字节快照）");
        }

        // ── K 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(8308);
            EnsureWarehouse(s);
            Vector2 field = FieldPoint(s, 70f, 7);
            int a = Spawn(s, field, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id);
            int b = Spawn(s, field + new Vector2(3f, 0f), HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
            s = Depart(s, a, b);
            HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(-1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), 30, "cargotest:u1");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            CargoPanelUIToolkit.InWorldOverrideForTests = true;
            var row = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml);
            string zhTeam = "", zhTarget = "", zhRow = "", pauseMsg = "", okMsg = "", discardMsg = "", enAll = "", warn = "";
            bool uiDone = false, headers = false, pauseShown = false, moved = false, discarded = false, bar = false, hook = false;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(PanelUxml, "CargoWindow", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_cargo") { hideFlags = HideFlags.HideAndDontSave };
                    CargoPanelUIToolkit p = go.AddComponent<CargoPanelUIToolkit>();
                    p.SetRowTemplateForTests(row);
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Refresh(force: true);
                    if (lang == GameLanguage.ZhCn && !uiDone)
                    {
                        uiDone = true;
                        hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstCargoOpen);
                        zhTeam = p.TeamText;
                        zhTarget = p.TargetText;
                        headers = Enumerable.Range(0, p.RowCount).Count(p.RowIsHeader) == 2;
                        int lineRow = Enumerable.Range(0, p.RowCount).FirstOrDefault(i => !p.RowIsHeader(i) && p.RowLineId(i) != null);
                        zhRow = p.RowText(lineRow);
                        bar = p.BarPercent > 0f;
                        pauseShown = p.PauseVisible;
                        Click(p.RowTransfer(lineRow));
                        pauseMsg = p.MessageText;
                        Click(p.PauseButton);
                        string moving = p.RowLineId(lineRow);
                        int to = p.TargetMachine;
                        Click(p.RowTransfer(lineRow));
                        okMsg = p.MessageText;
                        moved = LootPickService.StateOf(s).Held.Any(h => h.HeldId == moving && h.Machine == to);
                        p.Refresh(force: true);
                        int again = Enumerable.Range(0, p.RowCount).FirstOrDefault(i => !p.RowIsHeader(i) && p.RowLineId(i) != null);
                        string dropId = p.RowLineId(again);
                        Click(p.RowDiscard(again));
                        discardMsg = p.MessageText;
                        discarded = HomeValleyCargo.FindGroundItemBySalvageId(s, dropId) != null;
                        GameClock.SetPaused(false);
                        warn = p.WarnText;
                    }
                    else if (lang == GameLanguage.En)
                    {
                        enAll = p.TeamText + p.TargetText + p.AutoText + p.RowText(0) + p.RowText(1) + p.EmptyText;
                    }
                    p.SetOpen(false);
                });
                Expect(result.StartsWith("PASS"), $"K0 货舱面板布局探针（{lang}，四种分辨率 + 超长文字）：{string.Join(" / ", result.Split('\n').Take(4))}");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(hook && zhTeam.StartsWith("全队货舱") && headers && bar && zhRow.Contains("占") && zhTarget.StartsWith("转移目标") && pauseShown,
                $"K1 FGU-35 全队与单机明细：“{zhTeam}”（容量条 {bar}），每台机器一行标题 + 每份货一行（“{zhRow}”），“{zhTarget}”，没暂停时给“暂停并转移”；第一次打开钩子");
            Expect(pauseMsg == GameText.Get("cargo.reason.pause") && moved && okMsg.Contains("转") && discarded && discardMsg.Contains("落在") && enAll.Length > 0 && !ContainsCjk(enAll) && !GameText.ContainsMarker(enAll),
                $"K2 运行中点“转给目标”写原因（“{pauseMsg}”）→ 点“暂停并转移”后转移成功（“{okMsg}”）→ 点“丢弃”落在旁边（“{discardMsg}”）；英文界面没有中文");

            // 暂停菜单里的自动拾取开关（接入已有设置面板，不另开入口）+ “货舱”入口。
            GameObject pgo = null;
            try
            {
                VisualElement proot = F.MountUxml(UiKitFolder + "PauseMenu.uxml", out pgo);
                PauseMenuUIToolkit pm = pgo.AddComponent<PauseMenuUIToolkit>();
                pm.BindView(proot);
                pm.SyncAutoPickup();
                int count = pm.AutoPickupToggleCount;
                Toggle t = pm.AutoPickupToggle("metal_ore");
                bool initial = t != null && t.value;
                if (t != null)
                {
                    t.value = false;
                }
                bool offApplied = !CargoService.IsAutoPickup(s, "metal_ore");
                Click(pm.AutoPickupResetButton);
                bool reset = CargoService.IsAutoPickup(s, "metal_ore") && t != null && t.value;
                Expect(count == CargoService.AutoPickupItems().Count && count >= 5 && initial && offApplied && reset && pm.CargoButton != null && pm.CargoButton.text == GameText.Get("pause.cargo")
                       && t.label.Contains(ItemCatalog.NameOf("metal_ore")) && pm.AutoPickupHintText == GameText.Get("pause.auto_hint"),
                    $"K3 FGR-LOOT-030 按材料种类开关：暂停菜单设置段里 {count} 个开关（“{t?.label}”），取消勾选立即生效（{offApplied}），“全部打开”恢复（{reset}）；暂停菜单有“货舱”入口");
            }
            finally
            {
                if (pgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(pgo);
                }
            }

            // 顶栏全队货舱读数。
            GameObject hgo = null;
            try
            {
                VisualElement hroot = F.MountUxml(UiKitFolder + "WorldBar.uxml", out hgo);
                WorldBarHudUIToolkit w = hgo.AddComponent<WorldBarHudUIToolkit>();
                w.BindView(hroot);
                w.Refresh();
                var list = new List<int>();
                CargoService.PanelMachines(s, list);
                int level = CargoService.Totals(s, list, out int used, out int cap);
                string hud = w.CargoText;
                Expect(hud == GameText.Format("cargo.hud", used, cap, CargoService.LevelSuffix(level)) && list.Count == 2 && w.CargoButton != null,
                    $"K4 顶栏全队货舱“{hud}”（远征队在外时常显，快满 / 已满写在文字里，点开货舱面板）");
            }
            finally
            {
                if (hgo != null)
                {
                    UnityEngine.Object.DestroyImmediate(hgo);
                }
            }
            _ = warn;
        }

        // ── L 种子集 / M 性能 ────────────────────────────────────────────────────

        private static void CheckSeedSetAndPerf()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach (int seed in new[] { 111, 222, 333 })
            {
                CampaignState s = NewWorld(seed);
                Vector2 field = FieldPoint(s, 70f, seed % 8);
                int a = Spawn(s, field, HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId);
                s = Depart(s, a);
                HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, Pos(a) + new Vector2(1f, 0f), ItemCatalog.ResourceTypeOf("metal_ore"), 21, "cargotest:seed");
                WorldSimulation.StepMany(GameClock.StepHz * 2);
                LootHeldRecord h = LootPickService.StateOf(s).Held.FirstOrDefault(x => x.ItemId == "metal_ore");
                bool one = h != null && h.Amount == 21 && h.Space == 3 && h.Machine == a;
                ok &= one;
                lines.Add($"{seed}:{(one ? "捡起" : "没捡")}@{Mathf.RoundToInt(field.x)},{Mathf.RoundToInt(field.y)}");
            }
            Expect(ok, $"L1 B25 种子集：3 个种子、出发点各不相同（按种子取的可走格），自动拾取与空间计算一致（{string.Join("；", lines)}）");

            CampaignState w = NewWorld(8309);
            Vector2 at = FieldPoint(w, 70f, 3);
            var team = new List<int>();
            for (int i = 0; i < 2; i++)
            {
                team.Add(Spawn(w, at + new Vector2(i * 2f, 0f), HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId));
            }
            w = Depart(w, team.ToArray());
            for (int i = 0; i < 300; i++)
            {
                Vector2 p = FieldPoint(w, 140f + i % 60, i % 8) + new Vector2(i % 7, i % 5);
                HomeValleyCargo.SpawnGroundItem(w, HomeValleyLayout.RegionId, p, ItemCatalog.ResourceTypeOf("metal_ore"), 5, "cargotest:perf" + i);
            }
            for (int i = 0; i < 20; i++)
            {
                HomeValleyCargo.SpawnGroundItem(w, HomeValleyLayout.RegionId, Pos(team[i % 2]) + new Vector2(0.5f + i % 3, 0.5f), ItemCatalog.ResourceTypeOf("rare_earth_ore"), 3, "cargotest:near" + i);
            }
            // 先跑一次对账（含 20 份走近的拾取与首次调用的 JIT），记下这一次的耗时；再量稳态（地上 300 份、队员附近没有货）。
            CargoService.ResetStepStats();
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            double pickMs = CargoService.StepMaxMs;
            // 同机同种材料合并成一条（审查第 1 轮）：按件数数，20 份 × 3 件 = 60 件，且地上一份不剩。
            int picked = LootPickService.StateOf(w).Held.Where(h => h.ItemId == "rare_earth_ore").Sum(h => h.Amount);
            int nearLeft = w.GroundItems.Count(g => g != null && g.ResourceType == ItemCatalog.ResourceTypeOf("rare_earth_ore"));
            CargoService.ResetStepStats();
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            double stepMs = CargoService.StepMaxMs;
            long checks = CargoService.StepChecks;
            PerfGate.Expect(checks >= 5 && picked == 60 && nearLeft == 0,
                $"M1 性能：{w.GroundItems.Length} 份地面物 + 2 名队员时每 {CargoService.CheckSeconds} 游戏秒一次货舱对账（稳态 {checks} 次），最长 {stepMs:0.###} ms（预算 {CargoService.PerfMs} ms）；" +
                $"含 20 份拾取与首次 JIT 的那一次 {pickMs:0.###} ms（Editor batchmode，只在对账时、不按帧；附近 20 份 × 3 件全部捡起：{picked} 件，地上剩 {nearLeft} 份）",
                new[] { PerfGate.Le(stepMs, CargoService.PerfMs, "cargo.step_max_ms") }, (o, m) => Expect(o, m), Line);
            PerfLines.Add($"货舱：{w.GroundItems.Length} 份地面物 + 2 名队员时稳态对账最长 {stepMs:0.###} ms，含 20 份拾取与 JIT 的一次 {pickMs:0.###} ms（Editor batchmode；真机 IL2CPP 归 FG15-SYS-02，DEBT-FG8LOOT03-02）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>出发（真实派遣：行军中的远征队成员），然后下“原地待命”命令，让队员停在原地（覆盖内的远程命令，FG8-EXP-02）。</summary>
        private static CampaignState Depart(CampaignState s, params int[] ids)
        {
            WorldSimulation.StepMany(3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(ids, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            bool hold = ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Hold, out string why);
            WorldSimulation.StepMany(GameClock.StepHz);
            if (!dep.Success || !ids.All(id => ExpeditionMarchService.IsTeamMember(s, id)))
            {
                Fail($"夹具：出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}；待命 {hold} {why}）");
            }
            return s;
        }

        private static int CargoNotes(string contains) =>
            NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == CargoService.NotifyType).SelectMany(e => e.Members).Count(m => m.Detail != null && m.Detail.Contains(contains));

        private static int Chips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static bool StepUntil(Func<bool> done, int maxSeconds)
        {
            for (int i = 0; i < maxSeconds; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            return done();
        }

        private static bool ContainsCjk(string s) => s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static void Click(Button b)
        {
            if (b == null || !b.enabledInHierarchy)
            {
                return;
            }
            using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        /// <summary>测试捷径：把一台机器挪到 <paramref name="at"/>（战斗内核里的位置）。</summary>
        private static void MoveTo(int logicId, Vector2 at)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(at.x, at.y));
            }
        }

        private static Vector2 Pos(int logicId) =>
            MachineRegistry.TryGetLivePosition(logicId, out Vector2 p) ? p : MachineRegistry.TryGetRecord(logicId, out MachineRecord m) ? m.WorldPosition : Vector2.zero;

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static string Name(int id) => Rec(id) != null ? MachineNaming.Short(Rec(id)) : "#" + id;

        private static Vector2 FieldPoint(CampaignState s, float dist, int dir)
        {
            GridCell c = HomeGridService.CorePivot(s);
            float ang = dir * Mathf.PI / 4f;
            var raw = new GridCell(c.X + Mathf.RoundToInt(Mathf.Cos(ang) * dist), c.Y + Mathf.RoundToInt(Mathf.Sin(ang) * dist));
            GridCell ok = SiegeService.NearestPassable(raw, 12);
            return new Vector2(ok.X, ok.Y);
        }

        private static Vector2 CoreNear(CampaignState s)
        {
            GridCell c = HomeGridService.CorePivot(s);
            GridCell ok = SiegeService.NearestPassable(new GridCell(c.X + 5, c.Y - 5), 8);
            return new Vector2(ok.X, ok.Y);
        }

        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = F.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "cargo_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        private static CampaignState NewWorld(int seed, bool observe = true, int version = 3)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgcargo-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return s;
        }

        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(1);
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at, string chassis, string blueprint)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgCargoSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                LootPickCatalog.ResetForTests();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                WorldMapFilters.ResetAll();
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
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

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
