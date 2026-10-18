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
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-LOOT-01 物品协议与两种去向（FG08 FGR-LOOT-001、FGR-LOOT-010 普通掉落；验收 FGT-EXP-010；FG-GAP-109 / 110；DEBT-FG2VFX02-04 / FG5RND02-01）的自检——起真实系统断言行为：
    /// A 数据（源数据 = 运行时表、每件物品都有协议且符合 FGR-LOOT-001 表、文本中英、远征掉落率高于突袭、坏数据整行拒绝）；
    /// B 协议与去向（己方固件直接装配；掉落的敌方固件进固件库成为未破解芯片、详情写“需要解析 / 裸跑”；解析台破解后可直接装配）；
    /// C 负向（未破解固件装进机器电路 / 炮塔 / 陷阱被拒并说明，信号核可以裸跑；敌方加密物品送回收站被退回仓库、不分解不丢失，中立残骸照常回收）；
    /// D 界面（物资面板格子图标 + 文字角标、悬停与图鉴详情写明去向、固件库详情与行；中英；布局探针）；
    /// E 野外掉落（领地驻军迎战被歼：残骸 + 材料掉在原地并开搬运单、据点掉落、引导钩子）；F 身份与两种去向（强制掉落：加密固件搬回 → 未破解芯片；未解析模块搬回 → 仓库 + 解析身份；幂等）；
    /// G 拆解钳多回收；H 突袭掉落身份（FG-GAP-110）；I 存读档（身份、序号、读档后交付）；J 暂停与 0.5x～3x、观察与否一致；K 种子集；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgLootProtocolSelfCheck）；单段：bash tools/unity-validate.sh --segment FgLootProtocolSelfCheck。
    /// </summary>
    public static class FgLootProtocolSelfCheck
    {
        private const int Slot = 9;
        private const int RunSlot = 10;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 物品协议与两种去向")]
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
            Line("\n[物品协议与两种去向] 协议字段、直接使用 / 需要解析、拒绝直接使用加密物品、野外掉落与身份、拆解钳、存读档、倍速（FG8-LOOT-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgloot-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                ItemProtocol.ResetSessionState();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckProtocolAndNegative);
                Step(CheckRecycler);
                Step(CheckUi);
                Step(CheckFieldDrops);
                Step(CheckIdentityDelivery);
                Step(CheckClaw);
                Step(CheckRaidIdentity);
                Step(CheckSaveLoad);
                Step(CheckSpeedAndObservation);
                Step(CheckSeedSet);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                LootCatalog.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                ItemsPanelUIToolkit.InWorldOverrideForTests = false;
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
            Line(_fail == 0 ? $"  物品协议与两种去向：全部 {_pass} 项通过" : $"  物品协议与两种去向：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_loot as p;print('\\n'.join(p.dump_lines()))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("LD\t")).ToList();
            var rt = t.TbLootDrop.DataList.Select(r => string.Join("\t", "LD", r.Id, r.Source, r.Faction, r.Item, FloatRepr(r.Chance), r.Min, r.Max, r.Identity, r.Pool, r.SortOrder)).ToList();
            bool table = code == 0 && src.Count > 0 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            Expect(table && LootCatalog.Problems.Count == 0 && LootCatalog.Rows.Count == rt.Count,
                $"A1 源数据 fgdata_loot.py 与运行时 fg.TbLootDrop 逐字段一致（{rt.Count} 行），运行时检查全部通过、没有被拒绝的行" +
                (table ? string.Empty : $"（源 {src.Count} / 运行时 {rt.Count}，python 退出码 {code}）"));

            // FGR-LOOT-001：每件物品都有协议，三类敌方物品是敌方加密，人类遗产 / 原料中立，自产成品己方。
            var expect = new Dictionary<string, ItemProtocolKind>
            {
                { AnalysisCatalog.EncryptedFirmwareId, ItemProtocolKind.Enemy }, { AnalysisCatalog.UnparsedModuleId, ItemProtocolKind.Enemy },
                { AnalysisCatalog.DataCoreId, ItemProtocolKind.Enemy }, { "human_legacy", ItemProtocolKind.Neutral }, { ItemCatalog.ScrapId, ItemProtocolKind.Neutral },
                { "metal_ore", ItemProtocolKind.Neutral }, { AnalysisCatalog.WreckId, ItemProtocolKind.Neutral }, { "alloy", ItemProtocolKind.Own },
                { "combat_component", ItemProtocolKind.Own }, { ItemCatalog.FirmwareChipId, ItemProtocolKind.Own }, { "water", ItemProtocolKind.Neutral },
            };
            var wrong = expect.Where(kv => ItemProtocol.Of(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
            bool allRows = t.TbEcoItem.DataList.All(r => ItemCatalog.TryParseProtocol(r.Protocol, out _)) && ItemCatalog.Items.Count == t.TbEcoItem.DataList.Count
                           && ItemCatalog.Problems.Count == 0;
            int enemyCount = ItemCatalog.Items.Count(i => i.Protocol == ItemProtocolKind.Enemy);
            Expect(wrong.Count == 0 && allRows && enemyCount == 3,
                $"A2 FGR-LOOT-001 每件物品都有协议（{ItemCatalog.Items.Count} 件，敌方加密 {enemyCount} 件 = 未解析模块 / 敌方加密固件 / 数据核心；人类遗产、原料、残骸中立；自产成品己方）" +
                (wrong.Count == 0 ? string.Empty : "；不符：" + string.Join("、", wrong)));

            var keys = new List<string>
            {
                "loot.protocol.badge", "loot.dest.direct", "loot.dest.analysis", "loot.dest.direct.detail", "loot.dest.analysis.detail", "loot.dest.raw.detail",
                "loot.dest.cracked.detail", "loot.protocol.line", "loot.reject.recycler", "loot.reject.direct", "loot.drop.enemy_item", "loot.drop.firmware_granted",
                "loot.drop.firmware_pending", "codex.expedition.protocol.title", "codex.expedition.protocol.body", "codex.expedition.protocol.hint",
                "firmware.protocol.own", "firmware.protocol.neutral", "firmware.protocol.enemy",
            };
            var missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrWhiteSpace(zh) || !GameText.TryGet(k, GameLanguage.En, out string en)
                                          || string.IsNullOrWhiteSpace(en)).ToList();
            bool codex = t.TbCodexEntry.GetOrDefault("codex.expedition.protocol") != null;
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.LootFirstFieldDrop) && GuidanceHooks.Known.Contains(GuidanceHooks.LootFirstEncryptedRejected);
            Expect(missing.Count == 0 && codex && hooks,
                $"A3 文本键中英齐全（{keys.Count} 个）、图鉴“物品协议与两种去向”条目、两个引导钩子已登记" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing)));

            // FG-GAP-110 / FGR-DEF-051：突袭掉落概率低于远征；每个阵营都有杂兵来源的敌方固件可掉。
            float minionFw = LootCatalog.Rows.Where(r => r.Source == LootCatalog.SourceMinion && r.Item.Id == AnalysisCatalog.EncryptedFirmwareId).Select(r => r.Chance).DefaultIfEmpty(0f).Min();
            float minionMod = LootCatalog.Rows.Where(r => r.Source == LootCatalog.SourceMinion && r.Item.Id == AnalysisCatalog.UnparsedModuleId).Select(r => r.Chance).DefaultIfEmpty(0f).Min();
            var factions = new[] { "silent", "foundry", "clarity", "overclock" };
            bool pools = factions.All(f => LootCatalog.EnemyFirmwareOf(f).Count > 0
                                           && LootCatalog.EnemyFirmwareOf(f).All(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.FactionOf(id) == f
                                                                                       && FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row) && row.Source == "salvage"));
            Expect(minionFw > RaidResultService.DropFirmwareChance && minionMod > RaidResultService.DropModuleChance && pools,
                $"A4 FG-GAP-110：野外杂兵掉加密固件 {minionFw:0.###} > 突袭 {RaidResultService.DropFirmwareChance:0.###}、未解析模块 {minionMod:0.###} > 突袭 {RaidResultService.DropModuleChance:0.###}；" +
                $"四个阵营都有“杂兵残骸”来源的敌方固件（{string.Join("、", factions.Select(f => f + " " + LootCatalog.EnemyFirmwareOf(f).Count))}）");

            // 坏数据：整行拒绝并说明。
            var bad = new List<(string What, LootDropDef Def)>
            {
                ("物品不存在", LootCatalog.Validate("x1", "minion", "*", "zz_no_item", 0.5f, 1, 1, "none", "none", 1, out string p1)),
                ("加密物品不带身份", LootCatalog.Validate("x2", "minion", "*", AnalysisCatalog.EncryptedFirmwareId, 0.5f, 1, 1, "none", "none", 1, out string p2)),
                ("材料带身份", LootCatalog.Validate("x3", "minion", "*", "metal_ore", 0.5f, 1, 1, "lore", "none", 1, out string p3)),
                ("组件池里是固件", LootCatalog.Validate("x4", "minion", "*", AnalysisCatalog.UnparsedModuleId, 0.5f, 1, 1, "module", "fw_vortex", 1, out string p4)),
                ("概率为 0", LootCatalog.Validate("x5", "minion", "*", "metal_ore", 0f, 1, 1, "none", "none", 1, out string p5)),
                ("来源不认识", LootCatalog.Validate("x6", "boss", "*", "metal_ore", 0.5f, 1, 1, "none", "none", 1, out string p6)),
                ("流体不能掉在地上", LootCatalog.Validate("x7", "minion", "*", "water", 0.5f, 1, 1, "none", "none", 1, out string p7)),
            };
            string[] reasons = { p1, p2, p3, p4, p5, p6, p7 };
            LootDropDef good = LootCatalog.Validate("x8", "minion", "silent", AnalysisCatalog.UnparsedModuleId, 0.5f, 1, 1, "module", ComponentCatalog.FuncMarkerId, 1, out _);
            var rows = new[]
            {
                new ItemCatalog.ItemRow { Id = "scrap", BeltId = 1, Form = "solid", Tier = "raw", NameKey = "item.scrap.name", Shape = "square", Protocol = "neutral" },
                new ItemCatalog.ItemRow { Id = "zz_noproto", BeltId = 2, Form = "solid", Tier = "raw", NameKey = "item.scrap.name", Shape = "square", Protocol = null },
                new ItemCatalog.ItemRow { Id = "zz_badproto", BeltId = 3, Form = "solid", Tier = "raw", NameKey = "item.scrap.name", Shape = "square", Protocol = "friendly" },
            };
            ItemCatalog.Data d = ItemCatalog.Build(rows, Array.Empty<ItemCatalog.RecipeRow>(), Array.Empty<ItemCatalog.IoRow>(), new Dictionary<int, string>(), 1);
            bool itemReject = d.ById.ContainsKey("scrap") && !d.ById.ContainsKey("zz_noproto") && !d.ById.ContainsKey("zz_badproto")
                              && d.Problems.Count(x => x.Contains("协议")) == 2;
            Expect(bad.All(b => b.Def == null) && reasons.All(r => !string.IsNullOrEmpty(r)) && good != null && itemReject,
                $"A5 坏数据整行拒绝并写明原因：掉落行（{string.Join("、", bad.Select(b => b.What))}）全部拒绝、合法行通过；物品行缺协议 / 协议拼错时整行拒绝（不让敌方物品被当成可直接使用）");
        }

        // ── B / C 协议、去向与负向（FGT-EXP-010）─────────────────────────────────

        private static void CheckProtocolAndNegative()
        {
            CampaignState s = BenchWorld(7101);
            // 己方固件：直接装配。
            string own = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .FirstOrDefault(id => FirmwareKinds.IsFirmware(id) && !FirmwareKinds.IsEnemyProtocol(id) && !FirmwareKinds.IsCore(id) && FirmwareKinds.HasMachineImplementation(id)
                                      && MechanicalContentUnlock.IsUnlocked(s, id));
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            CircuitOpResult ownSet = own != null ? board.TrySetFirmware(s, 0, own) : CircuitOpResult.Fail("none", "没有己方固件");
            Expect(own != null && ownSet.Success && ItemProtocol.DestinationOfFirmware(s, own) == ItemDestination.DirectUse && ItemProtocol.OfFirmware(own) != ItemProtocolKind.Enemy,
                $"B1 直接可用：己方固件“{FirmwareKinds.DisplayName(own) ?? own}”去向 = 可直接使用，装进机器电路成功（{ownSet.Message}）");

            // 掉落的敌方固件：进固件库成为未破解芯片（两种去向：解析台 / 裸跑）。
            string fw = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .FirstOrDefault(id => FirmwareKinds.IsEnemyProtocol(id) && !FirmwareKinds.IsCore(id) && FirmwareKinds.HasMachineImplementation(id) && FirmwareKinds.IsRaw(s, id));
            int touch0 = GuidanceHooks.TouchCount;
            int chips0 = Chips(s, fw);
            int warehouseFw0 = HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId);
            int got = FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, fw, FieldLootService.OriginField, Vector2.zero, "selfcheck:fw:1");
            int again = FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, fw, FieldLootService.OriginField, Vector2.zero, "selfcheck:fw:1");
            PrimitiveChipRecord chip = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).FirstOrDefault(c => c != null && c.SourceSalvageId == "selfcheck:fw:1");
            string detail = ItemProtocol.FirmwareDetailLine(s, fw);
            string libDetail = FirmwareLibrary.BuildDetail(s, fw, chip);
            Expect(fw != null && got == 1 && again == 0 && Chips(s, fw) == chips0 + 1 && chip != null && chip.Origin == PrimitiveInventory.OriginEncrypted
                   && HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId) == warehouseFw0 && FirmwareKinds.IsRaw(s, fw)
                   && ItemProtocol.DestinationOfFirmware(s, fw) == ItemDestination.NeedsAnalysisOrRaw && detail.Contains("需要解析") && detail.Contains("裸跑")
                   && libDetail.Contains(detail) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SignalFirstEncryptedFirmware) && GuidanceHooks.TouchCount > touch0,
                $"B2 敌方加密固件“{FirmwareKinds.DisplayName(fw) ?? fw}”带回：变成固件库里的 1 枚未破解芯片（同一件重复发放不加：{again}；不占仓库），" +
                $"去向“{detail}”，固件库详情同一行；第一次拿到加密固件的引导钩子（解析台、裸跑）");

            // 负向：直接装配加密物品——机器电路、炮塔、陷阱都拒绝并说明；信号核可以裸跑。
            string[] before = board.FirmwareSlots.ToArray();
            int rejC1 = ItemProtocol.RejectedCount;
            CircuitOpResult machine = board.TrySetFirmware(s, 1, fw);
            BlueprintCircuitBoard turret = BlueprintCircuitBoard.CreateDefault(CarrierReadings.FixedChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            CircuitOpResult turretSet = turret.TrySetFirmware(s, 0, fw);
            bool trap = FirmwareKinds.CanInstall(s, fw, FirmwareHost.Trap, out string trapKey);
            bool core = FirmwareKinds.CanInstall(s, fw, FirmwareHost.SignalCore, out _);
            string trapText = trapKey != null ? GameText.Format(trapKey, FirmwareKinds.DisplayName(fw)) : string.Empty;
            Expect(!machine.Success && machine.Message.Contains("解析") && machine.Message.Contains("裸跑") && board.FirmwareSlots.SequenceEqual(before)
                   && !turretSet.Success && turretSet.Message.Contains("解析") && !trap && trapText.Contains("解析") && core
                   && ItemProtocol.RejectedCount == rejC1 + 2 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstEncryptedRejected),
                $"C1 FGT-EXP-010 负向“试图直接装配加密物品”：机器电路拒绝（{machine.Message}），槽位不变；炮塔拒绝（{turretSet.Message}）；陷阱拒绝（{trapText}）；信号核可以放（裸跑，FGR-SIG-060）；" +
                $"两次装配被拒都记入拒绝计数（{ItemProtocol.RejectedCount - rejC1} / 2）并触发第一次被拒绝的引导钩子");

            // 解析台破解后：可直接使用（同一枚芯片，按内容去标）。
            HomeValleyAnalysis.AnalysisOpResult q = HomeValleyAnalysis.TryEnqueueChip(s, chip?.PartId);
            bool done = q.Success && F.StepUntil(() => HomeValleyAnalysis.Find(s, q.QueueItemId) != null && !HomeValleyAnalysis.IsActiveEntry(HomeValleyAnalysis.Find(s, q.QueueItemId)), 240);
            CircuitOpResult afterCrack = board.TrySetFirmware(s, 1, fw);
            string crackedDetail = ItemProtocol.FirmwareDetailLine(s, fw);
            Expect(done && !FirmwareKinds.IsRaw(s, fw) && ItemProtocol.DestinationOfFirmware(s, fw) == ItemDestination.DirectUse && crackedDetail.Contains("已破解") && afterCrack.Success,
                $"B3 两种去向之“送解析台”：破解完成（{q.FailureReason ?? "已入队"}）→ 同一种固件去掉标记，去向“{crackedDetail}”，现在可以直接装进机器电路（{afterCrack.Message}）");

            // 物品的去向：敌方加密物品需要解析，其余直接使用；拒绝直接使用时说明去解析台。
            ItemDef mod = ItemCatalog.Find(AnalysisCatalog.UnparsedModuleId);
            ItemDef alloy = ItemCatalog.Find("alloy");
            ItemDef legacy = ItemCatalog.Find("human_legacy");
            int rej0 = ItemProtocol.RejectedCount;
            bool modOk = !ItemProtocol.CheckDirectUse(mod, out string modWhy);
            bool alloyOk = ItemProtocol.CheckDirectUse(alloy, out string alloyWhy) && alloyWhy == null;
            Expect(ItemProtocol.DestinationOf(mod) == ItemDestination.NeedsAnalysis && ItemProtocol.DetailLine(mod).Contains("需要解析") && ItemProtocol.DetailLine(mod).Contains("▲")
                   && ItemProtocol.DestinationOf(alloy) == ItemDestination.DirectUse && ItemProtocol.DetailLine(alloy).Contains("可直接使用") && ItemProtocol.DetailLine(alloy).Contains("■")
                   && ItemProtocol.DestinationOf(legacy) == ItemDestination.DirectUse && ItemProtocol.DetailLine(legacy).Contains("●")
                   && modOk && modWhy.Contains("解析台") && alloyOk && ItemProtocol.RejectedCount == rej0 + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstEncryptedRejected),
                $"B4 物品去向（通用判定 ItemProtocol.CheckDirectUse，玩家入口的拒绝见 C1 装配、C2 回收站）：未解析模块“{ItemProtocol.DetailLine(mod)}”、合金“{ItemProtocol.DetailLine(alloy)}”、人类遗产中立可直接使用；直接使用未解析模块被拒绝（{modWhy}），第一次拒绝的引导钩子");
        }

        private static void CheckRecycler()
        {
            CampaignState s = BenchWorld(7102);
            GridCell? o = F.FindArea(s, 6, 6);
            if (o == null)
            {
                Fail("C2 找不到 6×6 的空地放回收站");
                return;
            }
            BuildingRecord rec = F.Built(s, "recycler", "lootrc", F.At(o.Value, 1, 1));
            ProductionService.Producer p = F.P(s, rec);
            int stock0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) + HomeInventory.Stock(s, AnalysisCatalog.DataCoreId);
            int ground0 = GroundOf(s, AnalysisCatalog.UnparsedModuleId) + GroundOf(s, AnalysisCatalog.DataCoreId);
            int rej0 = ProductionService.RecyclerRejectedEncrypted;
            long recycled0 = p.Rec.ItemsRecycled;
            ProductionService.Add(ref p.Rec.In, AnalysisCatalog.UnparsedModuleId, 2);
            int hz = GameClock.StepHz;
            for (int i = 0; i < 40; i++)
            {
                ProductionService.Step(s, 3, hz);
            }
            ProductionService.Add(ref p.Rec.In, AnalysisCatalog.DataCoreId, 1);
            for (int i = 0; i < 40; i++)
            {
                ProductionService.Step(s, 3, hz);
            }
            int back = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) + HomeInventory.Stock(s, AnalysisCatalog.DataCoreId)
                       + GroundOf(s, AnalysisCatalog.UnparsedModuleId) + GroundOf(s, AnalysisCatalog.DataCoreId) - stock0 - ground0;
            int scrapOut = ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
            Expect(back == 3 && ProductionService.RecyclerRejectedEncrypted == rej0 + 3 && p.Rec.ItemsRecycled == recycled0 && scrapOut == 0
                   && ProductionService.Count(p.Rec.In, AnalysisCatalog.UnparsedModuleId) == 0 && ProductionService.Count(p.Rec.In, AnalysisCatalog.DataCoreId) == 0,
                $"C2 负向：2 件未解析模块 + 1 件数据核心送进回收站——不分解（分解数 {p.Rec.ItemsRecycled - recycled0}、废料 {scrapOut}），原样退回仓库 / 落地搬回 {back} 件，输入口不被卡住");
            ItemCatalog.TryGet(AnalysisCatalog.WreckId, out ItemDef wreck);
            ProductionService.Add(ref p.Rec.In, AnalysisCatalog.WreckId, 1);
            for (int i = 0; i < 400 && p.Rec.ItemsRecycled < recycled0 + 1; i++)
            {
                ProductionService.Step(s, 3, hz);
            }
            Expect(p.Rec.ItemsRecycled == recycled0 + 1 && ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId) == Math.Max(1, wreck.RecycleScrap),
                $"C3 对照：中立的敌方残骸照常进回收站分解成 {Math.Max(1, wreck.RecycleScrap)} 废料（直接使用）");

            // C2b（修复第 1 轮，审查 P1）：输入缓存混装——同一步里连着三种敌方加密物品（未解析模块、数据核心、加密固件）都要整份退回，
            // 不能退回第一份后把第二份当普通固体分解掉。
            string[] enemyIds = { AnalysisCatalog.UnparsedModuleId, AnalysisCatalog.DataCoreId, AnalysisCatalog.EncryptedFirmwareId };
            int Held() => enemyIds.Sum(id => HomeInventory.Stock(s, id) + GroundOf(s, id));
            int held0 = Held();
            int rejB = ProductionService.RecyclerRejectedEncrypted;
            long recycledB = p.Rec.ItemsRecycled;
            int scrapB = ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
            foreach (string id in enemyIds)
            {
                ProductionService.Add(ref p.Rec.In, id, 1);
            }
            int stacks = (p.Rec.In ?? Array.Empty<ItemStackRecord>()).Count(st => st != null && st.Amount > 0);
            for (int i = 0; i < 40; i++)
            {
                ProductionService.Step(s, 3, hz);
            }
            int leftIn = enemyIds.Sum(id => ProductionService.Count(p.Rec.In, id));
            Expect(stacks >= 3 && Held() - held0 == 3 && ProductionService.RecyclerRejectedEncrypted == rejB + 3 && p.Rec.ItemsRecycled == recycledB
                   && ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId) == scrapB && leftIn == 0 && !(p.Rec.Running && enemyIds.Contains(p.Rec.PendingItem)),
                $"C2b 负向（混装）：同一步里输入缓存连着 {stacks} 份敌方加密物品（未解析模块 + 数据核心 + 加密固件）——全部退回 {Held() - held0} / 3 件，" +
                $"分解数 {p.Rec.ItemsRecycled - recycledB}、新增废料 {ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId) - scrapB}，输入缓存剩 {leftIn}");
        }

        // ── D 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = BenchWorld(7103);
            HomeInventory.Add(s, ItemCatalog.Find(AnalysisCatalog.UnparsedModuleId), 2);
            HomeInventory.Add(s, ItemCatalog.Find("alloy"), 5);
            ItemsPanelUIToolkit.InWorldOverrideForTests = true;
            string zhMod = string.Empty, zhAlloy = string.Empty, enMod = string.Empty;
            bool enemyClass = false;
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "ItemsPanel.uxml", "ItemsPanelWindow", stressFill: true, prepare: r =>
                {
                    var go = new GameObject("__probe_loot_items") { hideFlags = HideFlags.HideAndDontSave };
                    ItemsPanelUIToolkit p = go.AddComponent<ItemsPanelUIToolkit>();
                    p.BindView(r.panel.visualTree);
                    p.SetOpen(true);
                    p.Refresh();
                    if (lang == GameLanguage.ZhCn)
                    {
                        zhMod = p.TileProtocolText(AnalysisCatalog.UnparsedModuleId);
                        zhAlloy = p.TileProtocolText("alloy");
                        VisualElementProbe(p, ref enemyClass);
                    }
                    else
                    {
                        enMod = p.TileProtocolText(AnalysisCatalog.UnparsedModuleId);
                    }
                });
                Expect(result.StartsWith("PASS"), $"D0 物资面板加了协议角标后布局探针（{lang}，四种分辨率 + 超长文字）：{string.Join(" / ", result.Split('\n').Take(4))}");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(zhMod.Contains("▲") && zhMod.Contains("敌方加密") && zhMod.Contains("需要解析") && zhAlloy.Contains("■") && zhAlloy.Contains("可直接使用") && enemyClass
                   && enMod.Contains("Needs analysis") && !GameText.ContainsMarker(zhMod + zhAlloy + enMod),
                $"D1 FGR-LOOT-001“界面上用图标加文字标明协议”：物资面板格子“{zhMod}” / “{zhAlloy}”（形状图标 + 文字，敌方加密另有醒目样式）；英文“{enMod}”");
            TooltipContent tip = ItemsPanelUIToolkit.HoverContent(ItemCatalog.Find(AnalysisCatalog.DataCoreId));
            string codex = EconomyCodex.ItemBody(AnalysisCatalog.DataCoreId, s);
            string codexAlloy = EconomyCodex.ItemBody("alloy", s);
            Expect(tip.Body.Contains("需要解析") && codex.Contains("需要解析") && codex.Contains("解析台") && codexAlloy.Contains("可直接使用"),
                "D2 物品详情（悬停与图鉴条目）写明“需要解析”（数据核心：先送解析台）或“可直接使用”（合金）");
            string fw = MechanicalContentFacade.All.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(id => FirmwareKinds.IsEnemyProtocol(id) && FirmwareKinds.IsRaw(s, id));
            FieldLootService.DeliverEnemyItem(s, AnalysisCatalog.EncryptedFirmwareId, fw, FieldLootService.OriginField, Vector2.zero, "selfcheck:ui:fw");
            PrimitiveChipRecord chip = s.PrimitiveChips.First(c => c.SourceSalvageId == "selfcheck:ui:fw");
            FirmwareLibraryRow row = FirmwareLibrary.BuildRow(s, chip);
            string rowText = FirmwareLibraryPanelUIToolkit.DescribeRow(s, row);
            string chipTile = ItemsPanelUIToolkit.ProtocolTileText(ItemCatalog.Find(ItemCatalog.FirmwareChipId), s);
            Expect(rowText.StartsWith(ItemProtocol.GlyphEnemy + " ", StringComparison.Ordinal) && rowText.Contains("需要解析") && FirmwareLibrary.BuildDetail(s, fw, chip).Contains("裸跑")
                   && chipTile.Contains(ItemProtocol.GlyphEnemy + "×") && chipTile.Contains("需要解析"),
                $"D3 固件库：未破解敌方固件的行“{rowText}”行首是敌方协议图标、写明需要解析，详情写明破解前只能裸跑；物资面板“固件芯片”格子“{chipTile}”写出未破解芯片数量");
        }

        private static void VisualElementProbe(ItemsPanelUIToolkit p, ref bool enemyClass)
        {
            UnityEngine.UIElements.VisualElement tile = p.TileOf(AnalysisCatalog.UnparsedModuleId);
            enemyClass = tile != null && tile.Query<Label>(className: "ip-protocol-enemy").ToList().Count == 1
                         && p.TileOf("alloy") != null && p.TileOf("alloy").Query<Label>(className: "ip-protocol-enemy").ToList().Count == 0;
        }

        // ── E 野外掉落 ───────────────────────────────────────────────────────────

        private static void CheckFieldDrops()
        {
            CampaignState s = NewWorld(42);
            OutpostRecord o = NearestOutpost(s, "silent");
            WorldOutpostSystem.Peek(s, o, out int garrison, out _);
            var at = new Vector2(o.CellX, o.CellY);
            int m = Spawn(s, Toward(s, at, 10f));
            bool engaged = RunUntil(() => o.Engaged, GameClock.StepHz * 8);
            CombatSite site = WorldSimulation.Home.Combat;
            LootPacketState st = FieldLootService.StateOf(s);
            long kills0 = st.FieldKills;
            long events0 = FieldLootService.KillEvents;
            KillGroup(site, at, o.EngageKey, 0);
            bool destroyed = RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
            long kills = st.FieldKills - kills0;
            List<GroundItemRecord> loot = (s.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => g.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix, StringComparison.Ordinal)).ToList();
            int scrap = Sum(loot, ItemCatalog.ScrapId);
            int wreck = Sum(loot, AnalysisCatalog.WreckId);
            bool near = loot.All(g => Vector2.Distance(g.Position, at) < 40f);
            bool haul = loot.All(g => s.WorkOrders.Any(w => w.Kind == WorkOrderKind.Haul && w.TargetId == g.GroundItemId));
            Expect(m > 0 && engaged && destroyed && kills == garrison && FieldLootService.KillEvents - events0 == garrison && wreck == garrison
                   && scrap >= 2 * garrison && scrap <= 4 * garrison && near && haul && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LootFirstFieldDrop),
                $"E1 FGR-LOOT-010 杂兵普通掉落 + FG-GAP-109 野外残骸：静默据点驻军 {garrison} 台在野外被歼 → 野外击毁 {kills} 次，残骸 {wreck} 件（每台 1）、废料 {scrap}（每台 2～4），" +
                $"掉在交战处（{loot.Count} 堆，合并近处同种材料）并全部开搬运单；第一次野外掉落的引导钩子");
            List<GroundItemRecord> outpostLoot = loot.Where(g => g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourceOutpost, StringComparison.Ordinal)).ToList();
            Expect(st.OutpostLoots >= 1 && Sum(outpostLoot, "metal_ore") >= 3 && Sum(outpostLoot, "metal_ore") <= 6,
                $"E2 FGR-LOOT-010 据点掉落：据点被摧毁另掷 outpost 行——金属矿 {Sum(outpostLoot, "metal_ore")}（3～6，必掉），稀土 / 组件 / 数据核心按概率（{outpostLoot.Count} 堆）");
        }

        private static void CheckIdentityDelivery()
        {
            List<LootDropDef> rows = LootCatalog.CloneRows();
            foreach (LootDropDef r in rows)
            {
                if (r.Id == "minion.firmware" || r.Id == "minion.module.silent" || r.Id == "outpost.core")
                {
                    r.Chance = 1f;
                }
            }
            LootCatalog.OverrideForTests(rows);
            try
            {
                CampaignState s = NewWorld(43);
                EnsureWarehouse(s);
                OutpostRecord o = NearestOutpost(s, "silent");
                WorldOutpostSystem.Peek(s, o, out int garrison, out _);
                var at = new Vector2(o.CellX, o.CellY);
                Spawn(s, Toward(s, at, 10f));
                RunUntil(() => o.Engaged, GameClock.StepHz * 8);
                CombatSite site = WorldSimulation.Home.Combat;
                KillGroup(site, at, o.EngageKey, 0);
                RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
                LootPacketState st = FieldLootService.StateOf(s);
                List<LootGroundTagRecord> fwTags = st.GroundTags.Where(t => t.ItemId == AnalysisCatalog.EncryptedFirmwareId).ToList();
                List<LootGroundTagRecord> modTags = st.GroundTags.Where(t => t.ItemId == AnalysisCatalog.UnparsedModuleId).ToList();
                List<LootGroundTagRecord> coreTags = st.GroundTags.Where(t => t.ItemId == AnalysisCatalog.DataCoreId).ToList();
                IReadOnlyList<string> silentPool = LootCatalog.EnemyFirmwareOf("silent");
                bool ground = st.GroundTags.All(t => HomeValleyCargo.FindGroundItemBySalvageId(s, t.SalvageId) is GroundItemRecord g && g.Amount == 1);
                Expect(fwTags.Count == garrison && modTags.Count == garrison && coreTags.Count == 1 && fwTags.All(t => silentPool.Contains(t.TargetId) && t.Faction == "silent")
                       && modTags.All(t => t.TargetId == ComponentCatalog.FuncMarkerId) && coreTags.All(t => AnalysisCatalog.TryGetLore(t.TargetId, out _)) && ground,
                    $"F1 身份按阵营 / 敌人类型（FG-GAP-110）：强制掉落下，{garrison} 台静默驻军各掉 1 件加密固件（身份都是静默杂兵残骸来源：{string.Join("、", fwTags.Select(t => t.TargetId).Distinct())}）" +
                    $"和 1 件未解析模块（{ComponentCatalog.FuncMarkerId}），据点掉 1 份数据核心（{coreTags.FirstOrDefault()?.TargetId}）；每件一堆，地上能找到");

                // 两种去向：搬回家园时（家园唯一的搬运交付口）加密固件 → 固件库未破解芯片；未解析模块 → 仓库 + 解析身份。
                LootGroundTagRecord ft = fwTags[0];
                GroundItemRecord fg = HomeValleyCargo.FindGroundItemBySalvageId(s, ft.SalvageId);
                int chips0 = Chips(s, ft.TargetId);
                int fwStock0 = HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId);
                HomeValleyCargo.HaulTicket t1 = HomeValleyCargo.TryReserveHaul(s, fg.GroundItemId);
                HomeValleyCargo.StoreResult r1 = HomeValleyCargo.CommitHaul(s, t1);
                PrimitiveChipRecord chip = s.PrimitiveChips.FirstOrDefault(c => c.SourceSalvageId == ft.SalvageId);
                Expect(r1.Success && chip != null && chip.CardDefId == ft.TargetId && Chips(s, ft.TargetId) == chips0 + 1 && FirmwareKinds.IsRaw(s, ft.TargetId)
                       && HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId) == fwStock0 && FieldLootService.TagOf(s, ft.SalvageId) == null,
                    $"F2 两种去向（加密固件）：搬回家园 → 固件库里多 1 枚“{FirmwareKinds.DisplayName(ft.TargetId)}”未破解芯片（可裸跑，或送解析台），不进仓库，身份记录用完即删");

                LootGroundTagRecord mt = modTags[0];
                GroundItemRecord mg = HomeValleyCargo.FindGroundItemBySalvageId(s, mt.SalvageId);
                int mod0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
                // 搬运单的票据用地面物 ID 当回收实例 ID（HomeValleyWorkOrders 的写法）：身份照样对得上。
                HomeValleyCargo.HaulTicket t2 = HomeValleyCargo.TryReserveHaul(s, mg.GroundItemId);
                t2.SalvageInstanceId = mg.GroundItemId;
                HomeValleyCargo.StoreResult r2 = HomeValleyCargo.CommitHaul(s, t2);
                var held = new List<HomeValleyAnalysis.HeldEntry>();
                HomeValleyAnalysis.CollectHeld(s, held);
                Expect(r2.Success && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == mod0 + 1
                       && held.Any(h => h.ItemId == AnalysisCatalog.UnparsedModuleId && h.TargetId == ComponentCatalog.FuncMarkerId) && FieldLootService.TagOf(s, mt.SalvageId) == null,
                    $"F3 两种去向（未解析模块）：搬回家园 → 进仓库 {HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) - mod0} 件，并登记解析身份（解析台待解析清单里身份 = {ComponentCatalog.FuncMarkerId}），按地面物 ID 交付也对得上");

                CheckRedropIdentity(s, fwTags.Skip(1).FirstOrDefault());
            }
            finally
            {
                LootCatalog.ResetForTests();
            }
        }

        /// <summary>
        /// F4（修复第 1 轮，审查 P1）：带身份的加密固件在搬运途中被打断（玩家取消搬运 → 货物按新 ID 重新落地），连续两次，
        /// 再换一台机器走真实搬运单（TryCreateHaul → 到达取货 → 到达交付）搬回家园，仍然变成固件库里的未破解芯片。
        /// 机器阵亡走同一个 ReleaseResourcesAndCargo 重新落地（同一个 ":redrop:" 标识），由同一条身份还原规则覆盖。
        /// </summary>
        private static void CheckRedropIdentity(CampaignState s, LootGroundTagRecord ft)
        {
            if (ft == null)
            {
                Fail("F4 测试准备：强制掉落下少于 2 件加密固件，没有第二件可测搬运中断");
                return;
            }
            GroundItemRecord g0 = HomeValleyCargo.FindGroundItemBySalvageId(s, ft.SalvageId);
            int m1 = SpawnHauler(s, g0.Position + new Vector2(1f, 0f));
            int m2 = SpawnHauler(s, g0.Position + new Vector2(-1f, 0f));
            string groundId = g0.GroundItemId;
            var trail = new List<string>();
            for (int round = 0; round < 2; round++)
            {
                var r = HomeValleyWorkOrders.TryCreateHaul(s, groundId, m1);
                if (!r.Success || !HomeValleyWorkOrders.OnArrivedAtHaulSource(s, r.WorkOrderId))
                {
                    Fail($"F4 测试准备：第 {round + 1} 次派搬运单失败（{r.FailureReason}）");
                    return;
                }
                Vector2 here = MachineRegistry.TryGetRecord(m1, out MachineRecord mr) ? mr.WorldPosition : g0.Position;
                string redropSid = HomeValleyWorkOrders.Find(s, r.WorkOrderId).SourceId + ":redrop:" + r.WorkOrderId;
                HomeValleyWorkOrders.CancelOrder(s, r.WorkOrderId, here);
                GroundItemRecord again = HomeValleyCargo.FindGroundItemBySalvageId(s, redropSid);
                if (again == null)
                {
                    Fail($"F4 测试准备：第 {round + 1} 次取消搬运后货物没有重新落地（{redropSid}）");
                    return;
                }
                groundId = again.GroundItemId;
                trail.Add(again.SalvageInstanceId);
            }
            int chips0 = Chips(s, ft.TargetId);
            int fwStock0 = HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId);
            bool tagKept = FieldLootService.TagOf(s, groundId) != null;
            var h = HomeValleyWorkOrders.TryCreateHaul(s, groundId, m2);
            bool picked = h.Success && HomeValleyWorkOrders.OnArrivedAtHaulSource(s, h.WorkOrderId);
            if (picked)
            {
                HomeValleyWorkOrders.OnArrivedAtHaulDestination(s, h.WorkOrderId);
            }
            WorkOrderRecord done = h.Success ? HomeValleyWorkOrders.Find(s, h.WorkOrderId) : null;
            PrimitiveChipRecord chip = s.PrimitiveChips.FirstOrDefault(c => c.SourceSalvageId == ft.SalvageId);
            Expect(m1 > 0 && m2 > 0 && tagKept && picked && done?.State == WorkOrderState.Completed && chip != null && chip.CardDefId == ft.TargetId
                   && Chips(s, ft.TargetId) == chips0 + 1 && HomeInventory.Stock(s, AnalysisCatalog.EncryptedFirmwareId) == fwStock0
                   && FieldLootService.TagOf(s, ft.SalvageId) == null,
                $"F4 搬运中断后身份不丢：加密固件搬到一半两次取消（重新落地 ID：{string.Join(" → ", trail)}），身份仍挂在地上这件（{tagKept}）；" +
                $"换一台机器走真实搬运单搬回（{done?.State}）→ 固件库多 1 枚“{FirmwareKinds.DisplayName(ft.TargetId)}”未破解芯片，不进仓库，身份用完即删");
        }

        // ── G 拆解钳 ────────────────────────────────────────────────────────────

        private static void CheckClaw()
        {
            CampaignState s = NewWorld(44);
            BlueprintRecord bp = BlueprintEditorService.Find(s, HomeValleyLayout.BlueprintErc003Id);
            BlueprintVersionRecord v = bp?.Versions?.LastOrDefault();
            if (v == null)
            {
                Fail("G1 测试准备：找不到 ERC-003 蓝图");
                return;
            }
            v.PrimaryId = ComponentCatalog.CompClawId; // 测试捷径：把开局战斗机的主组件换成拆解钳（蓝图编辑本身由 FG2 / FG4 覆盖）
            OutpostRecord o = NearestOutpost(s, "silent");
            WorldOutpostSystem.Peek(s, o, out int garrison, out _);
            var at = new Vector2(o.CellX, o.CellY);
            int m = Spawn(s, Toward(s, at, 10f));
            RunUntil(() => o.Engaged, GameClock.StepHz * 8);
            CombatSite site = WorldSimulation.Home.Combat;
            bool hasUnit = site.TryGetMachineUnit(m, out int unit);
            float mult = FieldLootService.ClawMultiplier(s, site, unit);
            LootPacketState st = FieldLootService.StateOf(s);
            int bonus0 = st.ClawBonusItems;
            KillGroup(site, at, o.EngageKey, unit);
            RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
            List<GroundItemRecord> loot = (s.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => g.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourceMinion, StringComparison.Ordinal)).ToList();
            int perWreck = (int)Math.Ceiling(1 * (1f + LootCatalog.ClawBonus));
            Expect(hasUnit && Mathf.Approximately(mult, 1f + LootCatalog.ClawBonus) && Sum(loot, AnalysisCatalog.WreckId) == garrison * perWreck && st.ClawBonusItems > bonus0
                   && Sum(loot, ItemCatalog.ScrapId) >= garrison * (int)Math.Ceiling(2 * mult),
                $"G1 DEBT-FG2VFX02-04 拆解钳多回收：致命一击来自拆解钳机器（倍率 {mult:0.##}）→ 每台残骸 {perWreck} 件（共 {Sum(loot, AnalysisCatalog.WreckId)}）、废料 {Sum(loot, ItemCatalog.ScrapId)}，多回收 {st.ClawBonusItems - bonus0} 件");
            Expect(Mathf.Approximately(FieldLootService.ClawMultiplier(s, site, 0), 1f) && FieldLootService.ApplyClaw(3, 1f) == 3,
                "G2 对照：致命一击来源不明 / 不是拆解钳时没有加成");
        }

        // ── H 突袭掉落身份 ────────────────────────────────────────────────────────

        private static void CheckRaidIdentity()
        {
            uint seed = 12345u;
            var fw = new HashSet<string>();
            var mods = new HashSet<string>();
            for (int k = 1; k <= 200; k++)
            {
                fw.Add(RaidResultService.DropIdentity(AnalysisCatalog.EncryptedFirmwareId, "foundry", seed, k, 1));
                mods.Add(RaidResultService.DropIdentity(AnalysisCatalog.UnparsedModuleId, "foundry", seed, k, 0));
            }
            IReadOnlyList<string> pool = LootCatalog.EnemyFirmwareOf("foundry");
            var modPool = new HashSet<string>(LootCatalog.Rows.First(r => r.Id == "minion.module.foundry").Pool);
            bool same = RaidResultService.DropIdentity(AnalysisCatalog.EncryptedFirmwareId, "foundry", seed, 7, 1) == RaidResultService.DropIdentity(AnalysisCatalog.EncryptedFirmwareId, "foundry", seed, 7, 1);
            Expect(fw.All(x => pool.Contains(x)) && fw.Count == pool.Count && mods.All(x => modPool.Contains(x)) && mods.Count == modPool.Count && same,
                $"H1 FG-GAP-110：突袭掉落不再“身份不明”——铸造突袭的加密固件身份都在铸造杂兵残骸池（取到 {fw.Count}/{pool.Count} 种），未解析模块在铸造组件池（{mods.Count}/{modPool.Count}），同一次击毁同一结果");
        }

        // ── I 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            List<LootDropDef> rows = LootCatalog.CloneRows();
            foreach (LootDropDef r in rows)
            {
                if (r.Id == "minion.firmware")
                {
                    r.Chance = 1f;
                }
            }
            LootCatalog.OverrideForTests(rows);
            try
            {
                CampaignState s = NewWorld(45);
                EnsureWarehouse(s);
                OutpostRecord o = NearestOutpost(s, "silent");
                var at = new Vector2(o.CellX, o.CellY);
                Spawn(s, Toward(s, at, 10f));
                RunUntil(() => o.Engaged, GameClock.StepHz * 8);
                KillGroup(WorldSimulation.Home.Combat, at, o.EngageKey, 0);
                RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
                LootPacketState st = FieldLootService.StateOf(s);
                string snap = TagSnapshot(s);
                long kills = st.FieldKills;
                SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                CampaignState back = Reload(Slot);
                LootPacketState st2 = back != null ? FieldLootService.StateOf(back) : null;
                Expect(sv.Success && back != null && TagSnapshot(back) == snap && st2.FieldKills == kills && st2.GroundTags.Length > 0,
                    $"I1 存读档：地上敌方物品的身份（{st2?.GroundTags.Length} 条）、野外击毁序号（{st2?.FieldKills}）、掉落地面堆逐条一致");
                LootGroundTagRecord t = st2.GroundTags[0];
                GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(back, t.SalvageId);
                int chips0 = Chips(back, t.TargetId);
                HomeValleyCargo.StoreResult r1 = HomeValleyCargo.CommitHaul(back, HomeValleyCargo.TryReserveHaul(back, g.GroundItemId));
                int dup = FieldLootService.DeliverEnemyItem(back, AnalysisCatalog.EncryptedFirmwareId, t.TargetId, FieldLootService.OriginField, Vector2.zero, t.SalvageId);
                Expect(r1.Success && Chips(back, t.TargetId) == chips0 + 1 && dup == 0 && back.PrimitiveChips.Count(c => c.SourceSalvageId == t.SalvageId) == 1,
                    $"I2 读档后搬回：按存档里的身份交付成“{FirmwareKinds.DisplayName(t.TargetId)}”未破解芯片；同一件再发一次不复制（{dup}）");
                // 读档后接着掉：序号接着编（不重复上一档的随机结果）。
                OutpostRecord o2 = NearestOutpost(back, "foundry");
                var at2 = new Vector2(o2.CellX, o2.CellY);
                Spawn(back, Toward(back, at2, 10f));
                RunUntil(() => o2.Engaged, GameClock.StepHz * 8);
                KillGroup(WorldSimulation.Home.Combat, at2, o2.EngageKey, 0);
                RunUntil(() => o2.Destroyed, GameClock.StepHz * 3);
                Expect(FieldLootService.StateOf(back).FieldKills > kills, $"I3 读档后再交战：野外击毁序号接着编（{kills} → {FieldLootService.StateOf(back).FieldKills}）");
            }
            finally
            {
                LootCatalog.ResetForTests();
            }
        }

        // ── J 暂停 / 倍速 / 观察 ─────────────────────────────────────────────────

        private static void CheckSpeedAndObservation()
        {
            string RunAt(float speed, bool pauseMid, bool observeHome)
            {
                CampaignState s = NewWorld(46, observeHome);
                OutpostRecord o = NearestOutpost(s, "silent");
                var at = new Vector2(o.CellX, o.CellY);
                Spawn(s, Toward(s, at, 10f));
                RunUntil(() => o.Engaged, GameClock.StepHz * 8);
                KillGroup(WorldSimulation.Home.Combat, at, o.EngageKey, 0);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + GameClock.StepHz * 2;
                long kills0 = FieldLootService.StateOf(s).FieldKills;
                bool pausedOk = true;
                int frames = 0;
                while (GameClock.Ticks < target && frames < 20000)
                {
                    if (pauseMid && frames == 0)
                    {
                        GameClock.SetPaused(true);
                        for (int k = 0; k < 20; k++)
                        {
                            WorldSimulation.Frame(1f / 60f, target);
                        }
                        pausedOk = FieldLootService.StateOf(s).FieldKills == kills0;
                        GameClock.SetPaused(false);
                    }
                    WorldSimulation.Frame(1f / 60f, target);
                    frames++;
                }
                GameClock.SetSpeed(1f);
                return (pausedOk ? string.Empty : "PAUSE-MOVED ") + LootSnapshot(s);
            }
            string r05 = RunAt(0.5f, false, true);
            string r1 = RunAt(1f, true, true);
            string r3 = RunAt(3f, false, true);
            string hidden = RunAt(1f, false, false);
            Expect(r05 == r1 && r1 == r3 && r1 == hidden && !r1.Contains("PAUSE") && r1.Length > 0,
                $"J1 B09 暂停与倍速 + FGR-BASE-021：同一场野外交战在 0.5x / 1x（暂停 20 帧期间不结算掉落）/ 3x、以及镜头不在家园时，掉落的物品、件数、位置与身份逐条相同（{r1.Length} 字节快照）");
        }

        // ── K 种子集 ────────────────────────────────────────────────────────────

        private static void CheckSeedSet()
        {
            var lines = new List<string>();
            bool ok = true;
            foreach (int seed in new[] { 101, 202, 303, 404 })
            {
                CampaignState s = NewWorld(seed);
                OutpostRecord o = NearestOutpost(s, "foundry") ?? NearestOutpost(s, "silent");
                if (o == null)
                {
                    ok = false;
                    lines.Add($"{seed}:无据点");
                    continue;
                }
                var at = new Vector2(o.CellX, o.CellY);
                Spawn(s, Toward(s, at, 10f));
                bool engaged = RunUntil(() => o.Engaged, GameClock.StepHz * 8);
                KillGroup(WorldSimulation.Home.Combat, at, o.EngageKey, 0);
                RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
                LootPacketState st = FieldLootService.StateOf(s);
                int piles = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => g.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix, StringComparison.Ordinal));
                bool tagsOk = st.GroundTags.All(t => !string.IsNullOrEmpty(t.TargetId));
                ok &= engaged && st.FieldKills > 0 && piles > 0 && tagsOk;
                lines.Add($"{seed}:{o.TerritoryId} 击毁 {st.FieldKills} 掉落 {piles} 堆");
            }
            Expect(ok, $"K1 B25 种子集：4 个种子下据点位置各不相同，野外交战都有掉落、敌方物品都带身份（{string.Join("；", lines)}）");
            double avg = FieldLootService.KillEvents > 0 ? FieldLootService.TotalMs / FieldLootService.KillEvents : 0;
            ExpectPerf(FieldLootService.KillEvents > 0,
                $"L1 性能：野外击毁掉落结算 {FieldLootService.KillEvents} 次，平均 {avg:0.###} ms（预算 {LootCatalog.PerfMs} ms）、最长 {FieldLootService.MaxMs:0.###} ms（含首次调用的 JIT；Editor batchmode，只在击毁事件里调用、不按帧）",
                new PerfGate.Metric("loot.kill_avg_ms", avg, LootCatalog.PerfMs, false));
            PerfLines.Add($"野外掉落结算：{FieldLootService.KillEvents} 次，平均 {avg:0.###} ms，最长 {FieldLootService.MaxMs:0.###} ms（Editor batchmode；真机 IL2CPP 归 FG15-SYS-02）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>与 python repr(float) 对齐：按 float 的最短表示，整数补“.0”。</summary>
        private static string FloatRepr(float v)
        {
            string f = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return f.Contains(".") || f.Contains("E") ? f : f + ".0";
        }

        private static int Sum(IEnumerable<GroundItemRecord> list, string itemId)
        {
            string res = ItemCatalog.ResourceTypeOf(itemId);
            return list.Where(g => g.ResourceType == res).Sum(g => g.Amount);
        }

        private static int GroundOf(CampaignState s, string itemId) => Sum(s.GroundItems ?? Array.Empty<GroundItemRecord>(), itemId);

        private static int Chips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static string TagSnapshot(CampaignState s)
        {
            LootPacketState st = FieldLootService.StateOf(s);
            return string.Join("|", st.GroundTags.Select(t => $"{t.SalvageId}:{t.ItemId}:{t.TargetId}:{t.Origin}:{t.Faction}"))
                   + "#" + LootSnapshot(s);
        }

        private static string LootSnapshot(CampaignState s)
        {
            LootPacketState st = FieldLootService.StateOf(s);
            IEnumerable<string> piles = (s.GroundItems ?? Array.Empty<GroundItemRecord>())
                .Where(g => g.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix, StringComparison.Ordinal))
                .Select(g => $"{g.SalvageInstanceId}={g.ResourceType}x{g.Amount}@{g.Position.x:0.0},{g.Position.y:0.0}")
                .OrderBy(x => x, StringComparer.Ordinal);
            IEnumerable<string> tags = st.GroundTags.Select(t => t.SalvageId + ">" + t.TargetId).OrderBy(x => x, StringComparer.Ordinal);
            return $"{st.FieldKills}/{st.OutpostLoots}:" + string.Join(";", piles) + "|" + string.Join(";", tags);
        }

        /// <summary>带解析台与仓库的家园（协议 / 回收站 / 界面用）。</summary>
        private static CampaignState BenchWorld(int seed)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            HomeValleyAnalysis.ResetSessionState();
            CampaignState s = F.NewWorld(seed, true, 600);
            F.PowerUp(s);
            EnsureWarehouse(s);
            BuildingRecord bench = HomeValleyAnalysis.FindBench(s);
            if (bench != null)
            {
                bench.ConstructionState = BuildingConstructionState.Operational;
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：确保有一座运转中的仓库（搬回的敌方物品先进仓库；仓库的施工与维修由 FG3-LOG-02 / FG4-ECO-05 覆盖）。</summary>
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
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "loot_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        private static OutpostRecord NearestOutpost(CampaignState s, string territoryId)
        {
            GridCell c = HomeGridService.CorePivot(s);
            var core = new Vector2(c.X, c.Y);
            return WorldOutpostSystem.Outposts(s).Where(o => o != null && o.TerritoryId == territoryId && o.Kind == WorldOutpostSystem.KindTerritory && !o.Destroyed && o.Garrison > 0)
                .OrderBy(o => Vector2.Distance(new Vector2(o.CellX, o.CellY), core)).ThenBy(o => o.OutpostId, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>从 <paramref name="at"/> 朝核心方向 <paramref name="dist"/> 格的点（机器从家园方向开过来的位置）。</summary>
        private static Vector2 Toward(CampaignState s, Vector2 at, float dist)
        {
            GridCell c = HomeGridService.CorePivot(s);
            var core = new Vector2(c.X, c.Y);
            Vector2 dir = (core - at).sqrMagnitude > 1e-4f ? (core - at).normalized : Vector2.right;
            return at + dir * dist;
        }

        private static void KillGroup(CombatSite site, Vector2 at, int key, int killer)
        {
            for (int i = 0; i < 128 && site.CountSiegeGroup(key) > 0; i++)
            {
                int u = site.FindNearestHostile(at, 80f);
                if (u <= 0)
                {
                    break;
                }
                site.Kernel.Damage(u, 1e7f, killer);
            }
        }

        private static bool RunUntil(Func<bool> done, long maxTicks)
        {
            for (long i = 0; i < maxTicks; i += 6)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(6);
            }
            return done();
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
            CampaignState s = CampaignState.CreateNew("fgloot-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
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
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        /// <summary>家园搬运机（ERC-001，能接搬运单；ERC-003 是战斗机器，不接）。</summary>
        private static int SpawnHauler(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static void ExpectPerf(bool functionalOk, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(functionalOk, message, perf, Expect, Line);

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgLootProtocolSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
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
