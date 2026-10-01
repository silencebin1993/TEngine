using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-SIG-01 信号核与槽位的自动验收（FG01 FGR-SIG-001、002、010～012，第 4 章预设，第 6 章存档；FG13 FGU-19；
    /// FGT-SIG-003 的“核心固件放不进机器电路”一半）。全部走真实的 <see cref="SignalCoreService"/> / <see cref="PrimitiveInventory"/> /
    /// <see cref="BlueprintCircuitBoard"/> / <see cref="CampaignSaveService"/> / 真 UXML 面板，行为坏了会失败：
    /// A 数据（种类表与源数据逐字段、调参、代码用到的文本键）；B 槽位与解锁；C 原子装卸与实例守恒；D 负向矩阵（满仓卸下等）；
    /// E 核心固件规则（注入种类表：信号核收、机器电路两个入口与保存校验都拒绝并给原因；常规固件对照）；F 远征锁；G 预设；
    /// H 存读档（真实文件往返、篡改存档的修复不丢实例、已移除内容对账不把固件当垃圾）；I 暂停 / 倍速 / 种子无关；
    /// K 界面（HUD 文字、点选装入、拖放、锁定横幅、预设按钮、布局探针四分辨率 × 缩放 × 中英）；L 输入（P 键开关、上下文）；
    /// M 性能与连按防抖。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgSignalCoreSelfCheck
    {
        private const string UxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/FG 信号核")]
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
            Line("\n[信号核] 信号核与槽位（FG1-SIG-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FirmwareKinds.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;

                CheckData();
                CheckSlots();
                CheckTransfers();
                CheckNegatives();
                CheckCoreRule();
                CheckExpeditionLock();
                CheckPresets();
                CheckSaveLoad();
                CheckPauseSpeedSeeds();
                CheckUi();
                CheckInput();
                CheckPerformanceAndSpam();
            }
            catch (Exception e)
            {
                Fail($"信号核自检抛异常：{e}");
            }
            finally
            {
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                SaveContentReconciler.ResetForTests();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                UiDragDrop.ResetForTests();
                UiConfirmDialog.ResetForTests();
                UiEscapeStack.Clear();
                InputRouter.Reset();
                StrategyClock.Reset();
                MachineRegistry.ResetForNewCampaign();
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
            Line($"  （本段断言通过 {_pass} 条，失败 {_fail} 条）");
            return _fail;
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：固件种类表、调参、文本键");
            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            var fk = new List<string[]>();
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length >= 4 && f[0] == "FK")
                {
                    fk.Add(f);
                }
            }
            IReadOnlyList<GameConfig.fg.FirmwareKind> rows = FirmwareKinds.Rows;
            var diffs = new List<string>();
            foreach (string[] f in fk)
            {
                GameConfig.fg.FirmwareKind row = rows.FirstOrDefault(r => r.Id == f[1]);
                if (row == null || row.Kind != f[2] || row.NameKey != f[3])
                {
                    diffs.Add(f[1]);
                }
            }
            Expect(code == 0 && fk.Count > 0 && fk.Count == rows.Count && diffs.Count == 0,
                $"fg.TbFirmwareKind 与源数据 fgdata_signal.py 逐字段一致（源 {fk.Count} 行 / 运行时 {rows.Count} 行{(diffs.Count > 0 ? "，不一致：" + string.Join(",", diffs) : string.Empty)}）");

            var missingKinds = FirmwareCatalog.All.Keys.Where(id => rows.All(r => r.Id != id)).ToList();
            Expect(missingKinds.Count == 0 && FirmwareCatalog.All.Count >= 6,
                $"固件目录 {FirmwareCatalog.All.Count} 条都在种类表里有一行（种类的唯一真相）{(missingKinds.Count > 0 ? "，缺：" + string.Join(",", missingKinds) : string.Empty)}");
            Expect(FirmwareKinds.KindOf(PrimitiveInventory.DefaultChipContentId) == FirmwareKind.NotFirmware
                   && FirmwareKinds.KindOf("no_such_content") == FirmwareKind.NotFirmware,
                "基元芯片（聚焦镜）与未知内容都不是固件");
            string names = string.Join("、", FirmwareCatalog.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(id => $"{FirmwareKinds.DisplayName(id)}={FirmwareKinds.KindOf(id)}"));
            bool namesMatchDemo = FirmwareCatalog.All.All(kv => FirmwareKinds.DisplayName(kv.Key) == kv.Value.DisplayName);
            Expect(namesMatchDemo, $"固件名走文本键且与 Demo 目录逐字一致：{names}");

            bool tuningFromTable = GridContent.TryGetTuning("signal.core.initial_slots", out float init)
                                   && GridContent.TryGetTuning("signal.core.max_slots", out float max)
                                   && GridContent.TryGetTuning("signal.core.max_presets", out float presets)
                                   && GridContent.TryGetTuning("signal.firmware_chip.print_scrap", out float scrap)
                                   && SignalCoreService.InitialSlots == (int)init && SignalCoreService.MaxSlots == (int)max
                                   && SignalCoreService.MaxPresets == (int)presets && SignalCoreService.FirmwareChipPrintScrap == (int)scrap;
            Expect(tuningFromTable && SignalCoreService.InitialSlots == 2 && SignalCoreService.MaxSlots == 5,
                $"调参来自 fg.TbHomeTuning：初始 {SignalCoreService.InitialSlots} 槽、最多 {SignalCoreService.MaxSlots} 槽（FGR-SIG-010），" +
                $"预设上限 {SignalCoreService.MaxPresets}、名称上限 {SignalCoreService.PresetNameMaxChars} 字、刻印 {SignalCoreService.FirmwareChipPrintScrap} 废料");

            // 代码里用到的全部 signal.* / firmware.* 文本键都有中英文（扫描条数有下限：正则写坏会在这里失败，而不是静默通过）。
            string[] sources =
            {
                "Assets/GameScripts/HotFix/GameLogic/Campaign/Signal/SignalCoreService.cs",
                "Assets/GameScripts/HotFix/GameLogic/Campaign/Signal/SignalPresence.cs",
                "Assets/GameScripts/HotFix/GameLogic/Campaign/Signal/FirmwareKinds.cs",
                "Assets/GameScripts/HotFix/GameLogic/UI/SignalCore/SignalCoreHudUIToolkit.cs",
                "Assets/GameScripts/HotFix/GameLogic/Campaign/Blueprint/BlueprintCircuitBoard.cs",
                "Assets/GameScripts/HotFix/GameLogic/UI/Expedition/ExpeditionPrepPanelUIToolkit.cs",
                "Assets/GameScripts/HotFix/GameLogic/UI/CircuitBoard/CircuitBoardPanelUIToolkit.cs",
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string src in sources)
            {
                if (!File.Exists(src))
                {
                    continue;
                }
                foreach (Match m in Regex.Matches(File.ReadAllText(src), "\"((?:signal|firmware)\\.[a-z0-9_]+(?:\\.[a-z0-9_]+)+)\""))
                {
                    keys.Add(m.Groups[1].Value);
                }
            }
            // 调参 ID（signal.core.initial_slots 等）同名空间但不是文本键：按调参表排除，其余每个都必须有中英文。
            keys.RemoveWhere(k => GridContent.TryGetTuning(k, out _));
            var missingText = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out _) || !GameText.TryGet(k, GameLanguage.En, out _)).ToList();
            Expect(keys.Count >= 60 && missingText.Count == 0,
                $"代码引用的信号核文本键 {keys.Count} 个（下限 60）全部有中英文{(missingText.Count > 0 ? "，缺：" + string.Join(",", missingText.Take(8)) : string.Empty)}");
        }

        // ── B. 槽位与解锁 ────────────────────────────────────────────────────────

        private static void CheckSlots()
        {
            Line("  · B. 槽位：初始 2、最多 5、超控阵列逐级解锁、槽位有顺序");
            SignalCoreService.ResetForTests();
            CampaignState s = NewState(101);
            Expect(s.SignalCore.SlotPartIds.Length == 5 && s.SignalCore.SlotPartIds.All(id => id == string.Empty)
                   && SignalCoreService.UnlockedSlots(s) == 2 && SignalCoreService.EquippedCount(s) == 0,
                $"新战役进入家园后：槽位数组长度 {s.SignalCore.SlotPartIds.Length}（= 最多槽位）、已解锁 {SignalCoreService.UnlockedSlots(s)}、全空");
            string a = Print(s, FirmwareCatalog.FwOverloadId);
            SignalCoreResult locked = SignalCoreService.TryEquip(s, a, 2);
            Expect(!locked.Success && locked.Code == SignalCoreService.CodeSlotLocked && locked.Message.Contains("3 号槽") && locked.Message.Contains("T1"),
                $"第 3 槽未解锁：拒绝并写明条件“{locked.Message}”");
            SignalCoreService.OverrideArrayTierProvider = _ => 1;
            SignalCoreResult t1 = SignalCoreService.TryEquip(s, a, 2);
            Expect(t1.Success && SignalCoreService.UnlockedSlots(s) == 3 && SignalCoreService.SlotPartId(s, 2) == a,
                $"超控阵列 T1：3 槽解锁，装入成功（“{t1.Message}”）");
            SignalCoreService.OverrideArrayTierProvider = _ => 3;
            int t3 = SignalCoreService.UnlockedSlots(s);
            SignalCoreService.OverrideArrayTierProvider = _ => 9;
            int capped = SignalCoreService.UnlockedSlots(s);
            Expect(t3 == 5 && capped == 5 && SignalCoreService.TierForSlot(4) == 3 && SignalCoreService.TierForSlot(1) == 0,
                $"T3 = 5 槽；等级再高也不超过最多 5 槽（{capped}）；第 5 槽需要 T3");
            SignalCoreService.OverrideArrayTierProvider = null;
            Expect(SignalCoreService.OverrideArrayTier(s) == 0,
                "超控阵列建筑（FG4-ECO-11）落地前等级恒 0（没有这座建筑时不凭空解锁）");
            // 已装在解锁范围外的固件（等级下降这类情况）仍在，只能卸下不能再装入。
            SignalCoreResult unloadLocked = SignalCoreService.TryUnequip(s, 2);
            Expect(unloadLocked.Success && Invariant(s, out _), $"解锁范围外的槽位里的固件仍可卸下（{Describe(s)}）");
        }

        // ── C. 原子装卸与实例守恒 ────────────────────────────────────────────────

        private static void CheckTransfers()
        {
            Line("  · C. 原子装卸（FGR-SIG-011）：实例不复制、不丢失");
            CampaignState s = NewState(102);
            string overload = Print(s, FirmwareCatalog.FwOverloadId);
            string homing = Print(s, FirmwareCatalog.FwHomingId);
            int total = s.PrimitiveChips.Length;
            int bagBefore = PrimitiveInventory.BagCount(s);
            SignalCoreResult r1 = SignalCoreService.TryEquip(s, overload, 0);
            PrimitiveChipRecord c1 = PrimitiveInventory.Find(s, overload);
            Expect(r1.Success && c1.State == PrimitiveChipState.SignalCore && SignalCoreService.SlotPartId(s, 0) == overload
                   && PrimitiveInventory.BagCount(s) == bagBefore - 1 && s.PrimitiveChips.Length == total && Invariant(s, out _),
                $"装入 1 号槽：实例状态 仓→信号核、基元仓 {bagBefore}→{PrimitiveInventory.BagCount(s)}、实例总数不变 {total}；“{r1.Message}”");
            string loadoutText = SignalCoreService.SummaryText(s);
            Expect(loadoutText.Contains("过载"), $"摘要：“{loadoutText}”");

            SignalCoreResult swap = SignalCoreService.TryEquip(s, homing, 0);
            Expect(swap.Success && SignalCoreService.SlotPartId(s, 0) == homing && PrimitiveInventory.Find(s, overload).State == PrimitiveChipState.Bag
                   && PrimitiveInventory.BagCount(s) == bagBefore - 1 && s.PrimitiveChips.Length == total && Invariant(s, out _)
                   && swap.Message.Contains("过载") && swap.Message.Contains("寻的"),
                $"占用的槽位再装：二者交换，原来的回到基元仓（“{swap.Message}”）");

            SignalCoreService.TryEquip(s, overload, 1);
            SignalCoreResult move = SignalCoreService.TrySwapSlots(s, 1, 0);
            Expect(move.Success && SignalCoreService.SlotPartId(s, 0) == overload && SignalCoreService.SlotPartId(s, 1) == homing && Invariant(s, out _),
                $"前移 / 对调槽位改变插入顺序（1 号槽 = 过载）：“{move.Message}”");
            SignalCoreResult moveByEquip = SignalCoreService.TryEquip(s, homing, 0);
            Expect(moveByEquip.Success && SignalCoreService.SlotPartId(s, 0) == homing && SignalCoreService.SlotPartId(s, 1) == overload,
                "把已在 2 号槽的固件装进 1 号槽 = 对调（不经过基元仓）");

            SignalCoreResult un = SignalCoreService.TryUnequip(s, 0);
            Expect(un.Success && PrimitiveInventory.Find(s, homing).State == PrimitiveChipState.Bag && SignalCoreService.SlotPartId(s, 0) == string.Empty
                   && s.PrimitiveChips.Length == total && Invariant(s, out _),
                $"卸下：槽位 → 基元仓（“{un.Message}”）");

            // 核心固件与常规固件都能放进信号核（FGR-SIG-012 第 3 条）。
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            SignalCoreResult core = SignalCoreService.TryEquip(s, homing, 0);
            bool bothKinds = core.Success && FirmwareKinds.IsCore(PrimitiveInventory.Find(s, overload).CardDefId)
                             && SignalCoreService.SlotPartId(s, 1) == overload;
            FirmwareKinds.ResetForTests();
            Expect(bothKinds, "核心固件（过载，注入种类）与常规固件（寻的）同时装在信号核里");
        }

        // ── D. 负向矩阵 ──────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · D. 负向：满仓卸下、非固件、待领取、预留、空槽、越界、刻印失败零扣款");
            CampaignState s = NewState(103);
            string focus = s.PrimitiveChips.First(c => c.CardDefId == PrimitiveInventory.DefaultChipContentId).PartId;
            SignalCoreResult notFw = SignalCoreService.TryEquip(s, focus, 0);
            Expect(!notFw.Success && notFw.Code == SignalCoreService.CodeNotFirmware && notFw.Message == GameText.Get("signal.reason.not_firmware"),
                $"基元芯片放不进信号核：“{notFw.Message}”");
            string a = Print(s, FirmwareCatalog.FwOverloadId);
            SignalCoreService.TryEquip(s, a, 0);
            // 把基元仓刻满。
            int printed = 0;
            SignalCoreResult pr;
            while ((pr = SignalCoreService.TryPrintFirmwareChip(s, FirmwareCatalog.FwSplitId)).Success && printed < 20)
            {
                printed++;
            }
            int scrapFull = s.Scrap;
            Expect(!pr.Success && pr.Code == SignalCoreService.CodePrintBagFull && PrimitiveInventory.BagCount(s) == PrimitiveInventory.Capacity
                   && s.Scrap == scrapFull,
                $"刻到基元仓满（{PrimitiveInventory.BagCount(s)}/{PrimitiveInventory.Capacity}）后再刻印：拒绝且零扣款“{pr.Message}”");

            string before = Snapshot(s);
            SignalCoreResult full = SignalCoreService.TryUnequip(s, 0);
            Expect(!full.Success && full.Code == SignalCoreService.CodeBagFull && full.Message.Contains("基元仓已满")
                   && full.Message == GameText.Get("signal.reason.bag_full") && Snapshot(s) == before,
                $"满仓卸下：拒绝，固件留在原槽，状态逐字不变（“{full.Message}”）");
            GameSettings.SetLanguage(GameLanguage.En);
            SignalCoreResult fullEn = SignalCoreService.TryUnequip(s, 0);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(!fullEn.Success && fullEn.Message.Contains("full"), $"英文原因：“{fullEn.Message}”");

            // 待领取实例（仓满时生成的）不能直接装。
            string pendingId = PrimitiveInventory.GrantCrafted(s, FirmwareCatalog.FwTrailId);
            SignalCoreResult pending = SignalCoreService.TryEquip(s, pendingId, 1);
            Expect(PrimitiveInventory.Find(s, pendingId).State == PrimitiveChipState.Pending && !pending.Success && pending.Code == SignalCoreService.CodeNotInBag,
                $"待领取的固件不能直接装：“{pending.Message}”");

            CampaignState s2 = NewState(104);
            string b = Print(s2, FirmwareCatalog.FwHomingId);
            PrimitiveInventory.TryReserveForCraft(s2, b, "craft:selfcheck");
            SignalCoreResult reserved = SignalCoreService.TryEquip(s2, b, 0);
            Expect(!reserved.Success && reserved.Code == SignalCoreService.CodeReserved, $"被合成台预留的不能装：“{reserved.Message}”");
            PrimitiveInventory.ReleaseCraftReservation(s2, b, "craft:selfcheck");

            SignalCoreResult empty = SignalCoreService.TryUnequip(s2, 1);
            SignalCoreResult range = SignalCoreService.TryEquip(s2, b, 7);
            SignalCoreResult none = SignalCoreService.TryEquip(s2, null, 0);
            SignalCoreResult ghost = SignalCoreService.TryEquip(s2, "pchip_does_not_exist", 0);
            SignalCoreService.TryEquip(s2, b, 0);
            SignalCoreResult same = SignalCoreService.TryEquip(s2, b, 0);
            SignalCoreResult swapEmpty = SignalCoreService.TrySwapSlots(s2, 1, 1);
            Expect(empty.Code == SignalCoreService.CodeSlotEmpty && range.Code == SignalCoreService.CodeSlotInvalid && none.Code == SignalCoreService.CodeNothingSelected
                   && ghost.Code == SignalCoreService.CodeNotFound && same.Code == SignalCoreService.CodeSameSlot && swapEmpty.Code == SignalCoreService.CodeSameSlot
                   && new[] { empty, range, none, ghost, same, swapEmpty }.All(r => !r.Success && !string.IsNullOrEmpty(r.Message) && !GameText.ContainsMarker(r.Message)),
                $"空槽卸下 / 越界 / 没选 / 不存在 / 同槽 / 自己换自己：各自给出稳定原因（“{empty.Message}”“{range.Message}”“{none.Message}”）");

            // 刻印：未解锁、没电、废料不足、不是固件，全部零扣款。
            CampaignState s3 = NewState(105);
            int scrap0 = s3.Scrap;
            SignalCoreResult lockedFw = SignalCoreService.TryPrintFirmwareChip(s3, FirmwareCatalog.FwMarkTagId);
            SignalCoreResult notFirmware = SignalCoreService.TryPrintFirmwareChip(s3, PrimitiveInventory.DefaultChipContentId);
            s3.BuildingRecords[0].PowerState = BuildingPowerState.Unpowered;
            SignalCoreResult noPower = SignalCoreService.TryPrintFirmwareChip(s3, FirmwareCatalog.FwHomingId);
            s3.BuildingRecords[0].PowerState = BuildingPowerState.Powered;
            s3.Scrap = SignalCoreService.FirmwareChipPrintScrap - 1;
            SignalCoreResult poor = SignalCoreService.TryPrintFirmwareChip(s3, FirmwareCatalog.FwHomingId);
            bool zeroCost = s3.Scrap == SignalCoreService.FirmwareChipPrintScrap - 1 && s3.PrimitiveChips.Length == 1;
            s3.Scrap = scrap0;
            SignalCoreResult ok = SignalCoreService.TryPrintFirmwareChip(s3, FirmwareCatalog.FwHomingId);
            Expect(lockedFw.Code == SignalCoreService.CodePrintLocked && notFirmware.Code == SignalCoreService.CodePrintUnknown
                   && noPower.Code == SignalCoreService.CodePrintNoPower && poor.Code == SignalCoreService.CodePrintScrap && zeroCost
                   && ok.Success && s3.Scrap == scrap0 - SignalCoreService.FirmwareChipPrintScrap && PrimitiveInventory.Find(s3, ok.CreatedId)?.CardDefId == FirmwareCatalog.FwHomingId,
                $"刻印：未解锁“{lockedFw.Message}”/ 没电“{noPower.Message}”/ 废料不足“{poor.Message}”都零扣款；成功扣 {SignalCoreService.FirmwareChipPrintScrap} 废料进基元仓");
        }

        // ── E. 核心固件规则（FGT-SIG-003 核心一半）──────────────────────────────

        private static void CheckCoreRule()
        {
            Line("  · E. 核心固件只能由信号携带（FGR-SIG-012 / FGT-SIG-003）");
            CampaignState s = NewState(106);
            string coreChip = Print(s, FirmwareCatalog.FwOverloadId);
            string regularChip = Print(s, FirmwareCatalog.FwHomingId);
            string reason = GameText.Get("signal.reason.core_signal_only");

            // 正式表（FG1-SIG-05 已迁移，DEBT-FG1SIG01-01 关闭）：过载、标记跳转是核心，装不进机器电路。
            BlueprintCircuitBoard prod = BlueprintCircuitBoard.CreateDefault("chassis_wheel", null, null, null, Array.Empty<string>());
            CircuitOpResult prodOverload = prod.TrySetFirmware(s, 0, FirmwareCatalog.FwOverloadId);
            CircuitOpResult prodMark = prod.TrySetFirmware(s, 1, FirmwareCatalog.FwMarkTagId);
            Expect(FirmwareKinds.KindOf(FirmwareCatalog.FwOverloadId) == FirmwareKind.Core && FirmwareKinds.KindOf(FirmwareCatalog.FwMarkTagId) == FirmwareKind.Core
                   && !prodOverload.Success && prodOverload.Code == BlueprintCircuitBoard.CoreSignalOnlyCode
                   && !prodMark.Success && prodMark.Code == BlueprintCircuitBoard.CoreSignalOnlyCode && prod.FirmwareSlots.All(string.IsNullOrEmpty),
                "正式种类表：过载、标记跳转是核心固件（FG1-SIG-05），装进机器电路被拒");
            // 对照：把过载注入为常规，同一个入口就能装——规则只按种类判断，不按 ID。
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Regular });
            try
            {
                BlueprintCircuitBoard control = BlueprintCircuitBoard.CreateDefault("chassis_wheel", null, null, null, Array.Empty<string>());
                Expect(control.TrySetFirmware(s, 0, FirmwareCatalog.FwOverloadId).Success,
                    "对照：注入“过载 = 常规”后同一入口可以装进机器电路——规则按种类判断，不按 ID");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }

            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault("chassis_wheel", null, null, null, Array.Empty<string>());
                int undoBefore = board.UndoDepth;
                CircuitOpResult setCore = board.TrySetFirmware(s, 0, FirmwareCatalog.FwOverloadId);
                Expect(!setCore.Success && setCore.Code == BlueprintCircuitBoard.CoreSignalOnlyCode && setCore.Message == reason
                       && board.FirmwareSlots[0] == null && board.UndoDepth == undoBefore,
                    $"蓝图固件槽：核心固件被拒绝，原因“{setCore.Message}”，槽位与撤销栈都不变");
                CircuitOpResult setRegular = board.TrySetFirmware(s, 0, FirmwareCatalog.FwHomingId);
                Expect(setRegular.Success && board.FirmwareSlots[0] == FirmwareCatalog.FwHomingId, "对照：常规固件照常装进蓝图固件槽");

                CircuitOpResult draftCore = PrimitiveInventory.TryMoveToDraft(s, board, "bp_selfcheck", 1, coreChip);
                CircuitOpResult draftRegular = PrimitiveInventory.TryMoveToDraft(s, board, "bp_selfcheck", 1, regularChip);
                Expect(!draftCore.Success && draftCore.Message == reason && PrimitiveInventory.Find(s, coreChip).State == PrimitiveChipState.Bag
                       && !draftRegular.Success && draftRegular.Message == GameText.Get("signal.reason.firmware_not_chip")
                       && PrimitiveInventory.Find(s, regularChip).State == PrimitiveChipState.Bag && string.IsNullOrEmpty(board.SlotContentIds[1]),
                    $"3×3 电路格：核心固件芯片被拒（“{draftCore.Message}”），常规固件芯片也不是电路格内容（“{draftRegular.Message}”），两枚都留在基元仓");

                board.FirmwareSlots[1] = FirmwareCatalog.FwOverloadId; // 种类表改动后遗留在旧草稿里的核心固件
                CircuitValidationResult v = board.Validate();
                CircuitIssue issue = v.Issues.FirstOrDefault(i => i.Code == CircuitIssueCode.FirmwareCoreSignalOnly);
                Expect(!v.IsValid && issue != null && issue.Message.Contains("过载"), $"保存校验兜底：旧草稿里的核心固件不能保存（“{issue?.Message}”）");

                SignalCoreResult inCore = SignalCoreService.TryEquip(s, coreChip, 0);
                Expect(inCore.Success && FirmwareKinds.KindLabel(FirmwareKind.Core).Contains("◆") && FirmwareKinds.KindLabel(FirmwareKind.Regular).Contains("●"),
                    $"同一枚核心固件放进信号核成功；种类用形状 + 文字区分（{FirmwareKinds.KindLabel(FirmwareKind.Core)} / {FirmwareKinds.KindLabel(FirmwareKind.Regular)}）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── F. 远征锁 ────────────────────────────────────────────────────────────

        private static void CheckExpeditionLock()
        {
            Line("  · F. 远征途中不能修改信号核（只能在家园）");
            SignalCoreService.ExpeditionUnderwayOverrideForTests = null;
            Expect(!SignalCoreService.ExpeditionUnderway && GameLogic.Campaign.WorldSim.WorldSimulation.ActiveExpedition == null,
                "真实判据：没有远征地点在外时不锁（读 WorldSimulation.ActiveExpedition，与“同一时刻一支远征队”同一判据；真实派遣的锁在 play-smoke 里验）");
            CampaignState s = NewState(107);
            string a = Print(s, FirmwareCatalog.FwOverloadId);
            string b = Print(s, FirmwareCatalog.FwHomingId);
            SignalCoreService.TryEquip(s, a, 0);
            SignalCoreService.TrySavePreset(s, "home");
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => true;
            string before = Snapshot(s);
            string reason = GameText.Get("signal.reason.expedition");
            SignalCoreResult[] denied =
            {
                SignalCoreService.TryEquip(s, b, 1),
                SignalCoreService.TryUnequip(s, 0),
                SignalCoreService.TrySwapSlots(s, 0, 1),
                SignalCoreService.TryApplyPreset(s, s.SignalCore.Presets[0].PresetId),
            };
            Expect(denied.All(r => !r.Success && r.Code == SignalCoreService.CodeExpedition && r.Message == reason) && Snapshot(s) == before,
                $"装 / 卸 / 换位 / 切换预设全部拒绝，状态逐字不变；原因“{reason}”");
            SignalCoreResult save = SignalCoreService.TrySavePreset(s, "field");
            SignalCoreResult rename = SignalCoreService.TryRenamePreset(s, save.CreatedId, "field2");
            SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, FirmwareCatalog.FwSplitId);
            Expect(save.Success && rename.Success && print.Success && SignalCoreService.SlotPartId(s, 0) == a,
                "不改变携带固件的操作仍可用：保存 / 改名预设、在家园装配站刻印");
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            Expect(SignalCoreService.TryUnequip(s, 0).Success, "回到家园（远征结束）后立即可以修改");
            SignalCoreService.ExpeditionUnderwayOverrideForTests = null;
            Expect(GameText.Get("signal.core.expedition_reminder").Contains("远征途中不能修改信号核"),
                $"远征准备面板的提前提醒：“{GameText.Get("signal.core.expedition_reminder")}”");
        }

        // ── G. 预设 ──────────────────────────────────────────────────────────────

        private static void CheckPresets()
        {
            Line("  · G. 预设：保存、切换（整体原子）、缺件、仓位不够、上限、改名、删除、覆盖");
            CampaignState s = NewState(108);
            string over = Print(s, FirmwareCatalog.FwOverloadId);
            string hom = Print(s, FirmwareCatalog.FwHomingId);
            string spl = Print(s, FirmwareCatalog.FwSplitId);
            SignalCoreService.TryEquip(s, over, 0);
            SignalCoreService.TryEquip(s, hom, 1);
            SignalCoreResult attack = SignalCoreService.TrySavePreset(s, "攻坚");
            SignalCorePresetRecord pa = SignalCoreService.FindPreset(s, attack.CreatedId);
            Expect(attack.Success && pa != null && pa.SlotContentIds.Length == 5 && pa.SlotContentIds[0] == FirmwareCatalog.FwOverloadId
                   && pa.SlotContentIds[1] == FirmwareCatalog.FwHomingId && s.SignalCore.ActivePresetId == attack.CreatedId
                   && SignalCoreService.PresetMatchesCurrent(s, pa),
                $"保存“攻坚”：按槽位记内容（过载、寻的），成为当前预设");
            SignalCoreService.TryUnequip(s, 1);
            SignalCoreService.TryEquip(s, spl, 1);
            SignalCoreResult defence = SignalCoreService.TrySavePreset(s, "  ");
            SignalCorePresetRecord pd = SignalCoreService.FindPreset(s, defence.CreatedId);
            Expect(defence.Success && pd.Name == "配置 2" && !SignalCoreService.PresetMatchesCurrent(s, pa),
                $"空名字用默认名“{pd?.Name}”；改动后原预设显示为已改动");

            int total = s.PrimitiveChips.Length;
            SignalCoreResult back = SignalCoreService.TryApplyPreset(s, attack.CreatedId);
            Expect(back.Success && SignalCoreService.SlotPartId(s, 0) == over && SignalCoreService.SlotPartId(s, 1) == hom
                   && PrimitiveInventory.Find(s, spl).State == PrimitiveChipState.Bag && s.PrimitiveChips.Length == total && Invariant(s, out _)
                   && s.SignalCore.ActivePresetId == attack.CreatedId,
                $"切换回“攻坚”：1 号槽过载（原实例不动）、2 号槽寻的（从基元仓取），分裂卸回基元仓，实例守恒（{Describe(s)}）");
            string once = Snapshot(s);
            SignalCoreService.TryApplyPreset(s, attack.CreatedId);
            Expect(Snapshot(s) == once, "重复切换同一预设：状态逐字不变（幂等）");

            // 缺件：拆掉寻的（消耗实例）后切换，对应槽位留空并在结果里说明。
            SignalCoreService.TryApplyPreset(s, defence.CreatedId);
            s.PrimitiveChips = s.PrimitiveChips.Where(c => c.PartId != hom).ToArray();
            SignalCoreResult missing = SignalCoreService.TryApplyPreset(s, attack.CreatedId);
            Expect(missing.Success && SignalCoreService.SlotPartId(s, 1) == string.Empty && missing.Message.Contains("寻的") && Invariant(s, out _),
                $"缺件：2 号槽留空并说明“{missing.Message}”");

            // 仓位不够：切换需要卸回的比能腾出的多 → 整体拒绝、什么都不改。
            CampaignState t = NewState(109);
            string x1 = Print(t, FirmwareCatalog.FwOverloadId);
            string x2 = Print(t, FirmwareCatalog.FwHomingId);
            SignalCoreService.TrySavePreset(t, "空");
            SignalCoreService.TryEquip(t, x1, 0);
            SignalCoreService.TryEquip(t, x2, 1);
            while (SignalCoreService.TryPrintFirmwareChip(t, FirmwareCatalog.FwSplitId).Success)
            {
            }
            string tb = Snapshot(t);
            SignalCoreResult noRoom = SignalCoreService.TryApplyPreset(t, t.SignalCore.Presets[0].PresetId);
            Expect(!noRoom.Success && noRoom.Code == SignalCoreService.CodePresetBagFull && Snapshot(t) == tb,
                $"仓位不够：整体拒绝、状态逐字不变（“{noRoom.Message}”）");

            // 上限 / 改名 / 删除 / 覆盖。
            CampaignState u = NewState(110);
            for (int i = 0; i < SignalCoreService.MaxPresets; i++)
            {
                SignalCoreService.TrySavePreset(u, "p" + i);
            }
            SignalCoreResult limit = SignalCoreService.TrySavePreset(u, "extra");
            string longName = new string('长', SignalCoreService.PresetNameMaxChars + 1);
            string first = u.SignalCore.Presets[0].PresetId;
            SignalCoreResult tooLong = SignalCoreService.TryRenamePreset(u, first, longName);
            SignalCoreResult blank = SignalCoreService.TryRenamePreset(u, first, " ");
            SignalCoreResult renamed = SignalCoreService.TryRenamePreset(u, first, "防守");
            Expect(!limit.Success && limit.Code == SignalCoreService.CodePresetLimit && u.SignalCore.Presets.Length == SignalCoreService.MaxPresets
                   && tooLong.Code == SignalCoreService.CodePresetNameLong && blank.Code == SignalCoreService.CodePresetNameEmpty
                   && renamed.Success && u.SignalCore.Presets[0].Name == "防守",
                $"上限 {SignalCoreService.MaxPresets} 个（“{limit.Message}”）；名称超长（“{tooLong.Message}”）/ 空白被拒；改名生效");
            string lastId = u.SignalCore.Presets.Last().PresetId;
            SignalCoreResult del = SignalCoreService.TryDeletePreset(u, lastId);
            SignalCoreResult again = SignalCoreService.TrySavePreset(u, "new");
            Expect(del.Success && u.SignalCore.ActivePresetId == again.CreatedId && again.CreatedId != lastId
                   && SignalCoreService.TryDeletePreset(u, "sigpreset_nope").Code == SignalCoreService.CodePresetNotFound,
                "删除当前预设后当前预设清空；序号不复用；删不存在的给原因");
            string ov = Print(u, FirmwareCatalog.FwTrailId);
            SignalCoreService.TryEquip(u, ov, 0);
            SignalCoreResult overwrite = SignalCoreService.TryOverwritePreset(u, first);
            Expect(overwrite.Success && u.SignalCore.Presets[0].SlotContentIds[0] == FirmwareCatalog.FwTrailId && u.SignalCore.ActivePresetId == first,
                $"覆盖预设：内容换成当前配置（“{overwrite.Message}”）");
        }

        // ── H. 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · H. 存读档（FG01 第 6 章）：真实文件往返、篡改存档修复、已移除内容对账");
            CampaignState s = NewState(111);
            string a = Print(s, FirmwareCatalog.FwOverloadId);
            string b = Print(s, FirmwareCatalog.FwHomingId);
            Print(s, FirmwareCatalog.FwSplitId);
            SignalCoreService.TryEquip(s, a, 0);
            SignalCoreService.TryEquip(s, b, 1);
            SignalCoreService.TrySavePreset(s, "攻坚");
            SignalCoreService.TryUnequip(s, 1);
            string expect = Snapshot(s);
            SaveResult saved = CampaignSaveService.Save(0, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(0);
            string got = loaded.State != null ? Snapshot(loaded.State) : "（读档失败）";
            Expect(saved.Success && loaded.Outcome == LoadOutcome.Success && got == expect && Invariant(loaded.State, out _)
                   && SignalCoreService.EnsureConsistent(loaded.State) == 0,
                $"存 → 读：槽位、实例状态、预设、当前预设逐字一致，读档后无需修复（{(loaded.State != null ? Describe(loaded.State) : string.Empty)}）");
            CampaignSaveService.Save(1, loaded.State, SaveReason.Manual);
            LoadResult twice = CampaignSaveService.Load(1);
            Expect(twice.State != null && Snapshot(twice.State) == expect, "再存再读一轮仍逐字一致（空槽空串不漂移成 null）");
            Expect(twice.State != null && SignalCoreService.PresetMatchesCurrent(twice.State, twice.State.SignalCore.Presets[0]) == false
                   && twice.State.SignalCore.ActivePresetId == twice.State.SignalCore.Presets[0].PresetId,
                "读档后“当前预设（已改动）”状态保留");

            // 篡改：槽位指向不存在的实例、同一实例在两个槽、实例标为信号核却不在槽位里、槽位数组过长。
            CampaignState t = NewState(112);
            string c = Print(t, FirmwareCatalog.FwOverloadId);
            string d = Print(t, FirmwareCatalog.FwHomingId);
            SignalCoreService.TryEquip(t, c, 0);
            PrimitiveInventory.Find(t, d).State = PrimitiveChipState.SignalCore; // 孤儿
            t.SignalCore.SlotPartIds = new[] { c, c, "pchip_ghost", string.Empty, string.Empty, string.Empty, "pchip_extra" };
            t.SignalCore.ActivePresetId = "sigpreset_deleted";
            int chips = t.PrimitiveChips.Length;
            CampaignSaveService.Save(2, t, SaveReason.Manual);
            LoadResult repaired = CampaignSaveService.Load(2);
            CampaignState r = repaired.State;
            Expect(r != null && r.SignalCore.SlotPartIds.Length == 5 && r.SignalCore.SlotPartIds[0] == c && r.SignalCore.SlotPartIds[1] == string.Empty
                   && r.SignalCore.SlotPartIds[2] == string.Empty && PrimitiveInventory.Find(r, d).State == PrimitiveChipState.Bag
                   && r.PrimitiveChips.Length == chips && r.SignalCore.ActivePresetId == string.Empty && Invariant(r, out _),
                $"篡改存档读档后自动修复：重复 / 悬空引用清空、孤儿退回基元仓、超长数组截断、悬空当前预设清空，实例一个不少（{(r != null ? Describe(r) : string.Empty)}）");

            // 孤儿退回时基元仓满 → 进待领取，不丢。
            CampaignState f = NewState(113);
            string orphan = Print(f, FirmwareCatalog.FwOverloadId);
            PrimitiveInventory.Find(f, orphan).State = PrimitiveChipState.SignalCore;
            while (SignalCoreService.TryPrintFirmwareChip(f, FirmwareCatalog.FwSplitId).Success)
            {
            }
            int fixes = SignalCoreService.EnsureConsistent(f);
            Expect(fixes == 1 && PrimitiveInventory.Find(f, orphan).State == PrimitiveChipState.Pending,
                "修复孤儿时基元仓已满：进待领取（宁可多一步领取，也不丢）");

            // 已移除内容对账：固件芯片是现存内容，不被当成已移除内容转废料；装在信号核里的已移除内容按“已装载”保留并通知。
            CampaignState g = NewState(114);
            string inCore = Print(g, FirmwareCatalog.FwSplitId);
            string inBag = Print(g, FirmwareCatalog.FwTrailId);
            SignalCoreService.TryEquip(g, inCore, 0);
            SaveNoticeRecord[] live = SaveContentReconciler.Reconcile(g, 2, 2);
            Expect(live.Length == 0 && PrimitiveInventory.Find(g, inBag) != null && PrimitiveInventory.Find(g, inCore) != null,
                "对账：基元仓与信号核里的固件芯片都是现存内容，不转废料、不产生通知");
            SaveContentReconciler.OverrideForTests(ConfigSystem.Instance.Tables.TbRemovedContent,
                id => id != FirmwareCatalog.FwSplitId && (id == PrimitiveInventory.DefaultChipContentId || FirmwareCatalog.TryGet(id, out _)));
            SaveNoticeRecord[] removed = SaveContentReconciler.Reconcile(g, 2, 3);
            SaveContentReconciler.ResetForTests();
            Expect(removed.Length == 1 && PrimitiveInventory.Find(g, inCore)?.State == PrimitiveChipState.SignalCore
                   && SignalCoreService.SlotPartId(g, 0) == inCore,
                "对账：装在信号核里的内容若被游戏移除，按“已装载”保留并通知（不删实例、不留悬空槽位）");
        }

        // ── I. 暂停、倍速、种子无关 ──────────────────────────────────────────────

        private static void CheckPauseSpeedSeeds()
        {
            Line("  · I. 暂停与 0.5x～3x、种子无关（B25）；家园后台一致性不适用（信号核没有随时间变化的状态）");
            var results = new List<string>();
            foreach ((bool paused, float speed, int seed) in new[] { (false, 1f, 201), (true, 1f, 202), (false, 0.5f, 203), (false, 3f, 99991) })
            {
                StrategyClock.Reset();
                StrategyClock.SetSpeed(speed);
                InputRouter.SetGameplayPaused(paused, strategic: true);
                CampaignState s = NewState(seed);
                results.Add(Script(s));
                InputRouter.SetGameplayPaused(false);
            }
            StrategyClock.Reset();
            Expect(results.Distinct().Count() == 1 && results[0].Contains("fw_overload"),
                $"同一串操作在 正常 / 战略暂停 / 0.5x / 3x、四个不同世界种子下结果完全相同（信号核是即时的界面操作，不计时、不读坐标）：{results[0]}");
        }

        /// <summary>一串固定操作，返回与实例 ID 无关的规范化结果。</summary>
        private static string Script(CampaignState s)
        {
            string o = Print(s, FirmwareCatalog.FwOverloadId);
            string h = Print(s, FirmwareCatalog.FwHomingId);
            SignalCoreService.TryEquip(s, o, 0);
            SignalCoreService.TryEquip(s, h, 1);
            SignalCoreService.TrySwapSlots(s, 0, 1);
            SignalCoreService.TrySavePreset(s, "a");
            SignalCoreService.TryUnequip(s, 0);
            SignalCoreService.TryApplyPreset(s, s.SignalCore.Presets[0].PresetId);
            return string.Join(",", SignalCoreService.CurrentContentIds(s)) + "|bag=" + PrimitiveInventory.BagCount(s) + "|scrap=" + s.Scrap;
        }

        // ── K. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · K. 界面：HUD 信号位置、面板点选 / 拖放、锁定横幅、预设按钮、布局探针");
            CampaignState s = NewState(115);
            string o = Print(s, FirmwareCatalog.FwOverloadId);
            Print(s, FirmwareCatalog.FwHomingId);
            s.MachineRecords = new[]
            {
                new MachineRecord { LogicId = 7, DisplayNumber = 12, ChassisId = "erc_003", BlueprintId = "bp_selfcheck", RegionId = HomeValleyLayout.RegionId, IsAlive = true },
            };
            MachineRegistry.LoadFromCampaignState(s);
            CampaignSession.Set(0, s);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(root);
                hud.Refresh();
                Expect(hud.HudVisible && !hud.PanelVisible && hud.LocationText == "信号：归还核心" && hud.EntryText == "信号核 0/2",
                    $"HUD：“{hud.LocationText}”“{hud.EntryText}”（FGR-SIG-001：任意时刻显示信号在哪里）");
                SignalPresence.MachineOverrideForTests = () => 7;
                hud.Refresh();
                string inMachine = hud.LocationText;
                SignalPresence.MachineOverrideForTests = null;
                hud.Refresh();
                Expect(inMachine == "信号：ERC-003 #12" && hud.LocationText == "信号：归还核心",
                    $"接管一台机器时 HUD 变成“{inMachine}”，回到战略视角变回归还核心（FGR-SIG-002 同一时刻只有一个焦点）");

                hud.SetOpen(true);
                Expect(hud.PanelVisible && SignalCoreHudUIToolkit.IsOpen && InputRouter.IsModalOwner(hud) && hud.SelectedSlot == 0
                       && hud.SlotText(0).Contains("1 号槽") && hud.SlotText(2).Contains("超控阵列 T1") && hud.VisibleBagItemCount == 2
                       && hud.BagItemText(0).Contains("●"),
                    $"面板打开（模态 + Esc 栈）：槽位“{hud.SlotText(0)}”“{hud.SlotText(2).Replace("\n", " ")}”；基元仓固件 {hud.VisibleBagItemCount} 枚“{hud.BagItemText(0)}”");

                int overloadIndex = Enumerable.Range(0, hud.VisibleBagItemCount).First(i => hud.BagItemPartId(i) == o);
                Click(hud.BagButton(overloadIndex));
                Click(root.Q<Button>("SignalEquip"));
                Expect(SignalCoreService.SlotPartId(s, 0) == o && !hud.FeedbackIsError && hud.FeedbackText.Contains("过载") && hud.SlotText(0).Contains("过载")
                       && hud.EntryText == "信号核 1/2",
                    $"点选基元仓里的过载 → 点“装入 1 号槽”（FGJ-M1 第 2 步：给信号核装上过载）：“{hud.FeedbackText}”");

                // 拖放：寻的拖到 2 号槽；2 号槽拖回基元仓；拖到未解锁的 3 号槽被拒并给原因。
                string homing = hud.BagItemPartId(0);
                UiDragDrop.Begin(hud.BagButton(0), -1);
                UiDragDrop.MoveTo(Vector2.zero, hud.SlotButton(1));
                bool dropped = UiDragDrop.Drop(hud.SlotButton(1));
                Expect(dropped && SignalCoreService.SlotPartId(s, 1) == homing, "拖放：把基元仓里的寻的拖到 2 号槽 → 装入");
                UiDragDrop.Begin(hud.SlotButton(1), -1);
                bool droppedBack = UiDragDrop.Drop(hud.BagListElement);
                Expect(droppedBack && SignalCoreService.SlotPartId(s, 1) == string.Empty && PrimitiveInventory.Find(s, homing).State == PrimitiveChipState.Bag,
                    "拖放：把 2 号槽拖回基元仓 → 卸下");
                UiDragDrop.Begin(hud.BagButton(0), -1);
                bool lockedDrop = UiDragDrop.Drop(hud.SlotButton(2));
                Expect(!lockedDrop && UiDragDrop.LastResult.Contains("超控阵列 T1") && SignalCoreService.SlotPartId(s, 2) == string.Empty,
                    $"拖到未解锁的 3 号槽：不落下，原因“{UiDragDrop.LastResult}”");

                // 远征锁：横幅 + 点“卸下”被拒并给原因（按钮不静默失效）+ 拒绝音与字幕。
                SignalCoreService.ExpeditionUnderwayOverrideForTests = () => true;
                hud.Refresh();
                int deniedBefore = FeedbackCues.CountOf(FeedbackCueId.Denied);
                Click(hud.SlotButton(0));
                Click(root.Q<Button>("SignalUnequip"));
                Expect(hud.LockVisible && hud.LockText == GameText.Get("signal.reason.expedition") && hud.FeedbackIsError
                       && hud.FeedbackText == GameText.Get("signal.reason.expedition") && SignalCoreService.SlotPartId(s, 0) == o
                       && FeedbackCues.CountOf(FeedbackCueId.Denied) == deniedBefore + 1 && hud.EntryText.Contains("远征中锁定"),
                    $"远征途中：顶部横幅“{hud.LockText}”，点卸下给原因 + 拒绝音，HUD 按钮“{hud.EntryText}”");
                SignalCoreService.ExpeditionUnderwayOverrideForTests = null;

                // 预设按钮：输入名字 → 另存为新预设；删除走确认框。
                root.Q<TextField>("SignalPresetName").value = "攻坚";
                Click(root.Q<Button>("SignalPresetSave"));
                Expect(s.SignalCore.Presets.Length == 1 && s.SignalCore.Presets[0].Name == "攻坚" && hud.PresetActiveText == "当前预设：攻坚",
                    $"预设：输入名字点“另存为新预设”→“{hud.PresetActiveText}”");
                Click(root.Q<Button>("SignalPresetDelete"));
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Irreversible;
                UiConfirmDialog.Cancel();
                bool keptOnCancel = s.SignalCore.Presets.Length == 1;
                Click(root.Q<Button>("SignalPresetDelete"));
                UiConfirmDialog.Confirm();
                Expect(asked && keptOnCancel && s.SignalCore.Presets.Length == 0, "删除预设先弹确认框：取消不删，确认才删");

                // 刻印：选中下拉里的第一项并点“刻印”，新芯片直接被选中。
                int before = PrimitiveInventory.BagCount(s);
                Click(root.Q<Button>("SignalPrint"));
                Expect(PrimitiveInventory.BagCount(s) == before + 1 && hud.SelectedPartId != null && hud.PrintChoiceIds.Count >= 4,
                    $"刻印：可刻 {hud.PrintChoiceIds.Count} 种已解锁固件，刻好的芯片进基元仓并被选中（“{hud.FeedbackText}”）");

                hud.SetOpen(false);
                Expect(!hud.PanelVisible && !SignalCoreHudUIToolkit.IsOpen && !InputRouter.IsModalOwner(hud), "关闭：模态与 Esc 栈都释放");
                string sample = hud.HintText;
                Expect(!GameText.ContainsMarker(sample) && !GameText.ContainsMarker(hud.LocationText), "面板与 HUD 文本没有缺失键标记");
            }
            finally
            {
                hud.SetOpen(false);
                Object.DestroyImmediate(go);
                SignalPresence.MachineOverrideForTests = null;
            }

            // 布局探针：面板与 HUD，中英文 × UI 缩放极值 × 四种分辨率（面板默认隐藏，探针会移除隐藏类）。
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                {
                    foreach (string target in new[] { "SignalCorePanel", "SignalHudBar" })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UxmlPath, target, stressFill: true, prepare: r =>
                        {
                            var probeGo = new GameObject("__probe_signal") { hideFlags = HideFlags.HideAndDontSave };
                            SignalCoreHudUIToolkit h = probeGo.AddComponent<SignalCoreHudUIToolkit>();
                            h.BindView(r.panel.visualTree);
                            h.SetOpen(true);
                            h.SetOpen(false);
                            Object.DestroyImmediate(probeGo);
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 SignalCorePanel.uxml#{target} [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
            CampaignSession.Clear();
        }

        // ── L. 输入 ──────────────────────────────────────────────────────────────

        private static void CheckInput()
        {
            Line("  · L. 输入：P 键开关（可重绑，战略与接入上下文），面板开着时同一个键关闭");
            Expect(InputActionCatalog.TryGet(GameActionId.OpenSignalCore, out InputActionDef def) && def.Status == InputActionStatus.Wired
                   && (def.Contexts & InputContext.Strategy) != 0 && (def.Contexts & InputContext.Uplink) != 0
                   && InputActionCatalog.DefaultChord(GameActionId.OpenSignalCore).Key == KeyCode.P,
                "动作登记表：OpenSignalCore 默认 P、战略 + 接入上下文、已接入");
            CampaignState s = NewState(116);
            CampaignSession.Set(0, s);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            var reader = new Reader();
            try
            {
                hud.BindView(root);
                SetInstance(hud);
                InputRouter.Reset();
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                reader.Down = KeyCode.P;
                UiKitInputPump.ProcessWorldKeys();
                bool opened = SignalCoreHudUIToolkit.IsOpen && hud.PanelVisible;
                InputRouter.DebugClearConsumedKeys();
                UiKitInputPump.ProcessWorldKeys();
                bool closed = !SignalCoreHudUIToolkit.IsOpen;
                InputRouter.DebugClearConsumedKeys();
                InputRouter.SetScope(InputScope.Direct);
                UiKitInputPump.ProcessWorldKeys();
                bool uplinkOpens = SignalCoreHudUIToolkit.IsOpen;
                hud.SetOpen(false);
                InputRouter.DebugClearConsumedKeys();
                InputRouter.SetScope(InputScope.Strategy);
                InputRouter.SetTextInputFocused(true);
                UiKitInputPump.ProcessWorldKeys();
                bool typingIgnored = !SignalCoreHudUIToolkit.IsOpen;
                InputRouter.SetTextInputFocused(false);
                Expect(opened && closed && uplinkOpens && typingIgnored,
                    $"战略上下文按 P 打开 {opened}、再按关闭 {closed}；接管机器（接入上下文）时也能打开 {uplinkOpens}；在文本框里打字不触发 {typingIgnored}");

                // 找一个在战略 / 接入上下文里都空着的键改绑（冲突的键按正式规则拒绝，不强行覆盖别的动作）。
                RebindResult rb = RebindResult.Invalid;
                KeyCode freeKey = KeyCode.None;
                foreach (KeyCode k in new[] { KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Semicolon, KeyCode.Quote })
                {
                    rb = GameSettings.KeyBindings.TryRebind(GameActionId.OpenSignalCore, new InputChord(k), new List<GameActionId>());
                    if (rb == RebindResult.Ok)
                    {
                        freeKey = k;
                        break;
                    }
                }
                InputRouter.DebugClearConsumedKeys();
                reader.Down = freeKey;
                UiKitInputPump.ProcessWorldKeys();
                bool rebound = SignalCoreHudUIToolkit.IsOpen;
                hud.SetOpen(false);
                GameSettings.KeyBindings.ResetToDefault(GameActionId.OpenSignalCore);
                Expect(rebound && freeKey != KeyCode.None && GameSettings.KeyBindings.GetKey(GameActionId.OpenSignalCore) == KeyCode.P, $"改绑到 {freeKey}（{rb}）后按 {freeKey} 打开；恢复默认回到 P");

                // 家园里左键点归还核心、远征准备“编辑信号核”走同一个跨模块事件。
                TEngine.GameEvent.AddEventListener(SignalCoreService.PanelToggleEvent, Toggle);
                TEngine.GameEvent.AddEventListener(SignalCoreService.PanelOpenEvent, Open);
                SignalCoreService.RequestPanelToggle();
                bool viaCore = SignalCoreHudUIToolkit.IsOpen;
                SignalCoreService.RequestPanelOpen();
                bool stillOpen = SignalCoreHudUIToolkit.IsOpen;
                SignalCoreService.RequestPanelToggle();
                Expect(viaCore && stillOpen && !SignalCoreHudUIToolkit.IsOpen, "点归还核心（切换）与远征准备“编辑信号核”（打开）经 GameEvent 开关面板");
            }
            finally
            {
                TEngine.GameEvent.RemoveEventListener(SignalCoreService.PanelToggleEvent, Toggle);
                TEngine.GameEvent.RemoveEventListener(SignalCoreService.PanelOpenEvent, Open);
                hud.SetOpen(false);
                SetInstance(null);
                Object.DestroyImmediate(go);
                InputRouter.Reset();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                CampaignSession.Clear();
            }
        }

        private static void Toggle() => SignalCoreHudUIToolkit.Toggle();
        private static void Open() => SignalCoreHudUIToolkit.Open();

        /// <summary>编辑模式不跑 Awake：按运行时的样子登记静态实例，让 Open / Close / Toggle 找到它。</summary>
        private static void SetInstance(SignalCoreHudUIToolkit hud)
        {
            typeof(SignalCoreHudUIToolkit).GetProperty("Instance")?.GetSetMethod(true)?.Invoke(null, new object[] { hud });
        }

        // ── M. 性能与连按 ────────────────────────────────────────────────────────

        private static void CheckPerformanceAndSpam()
        {
            Line("  · M. 性能（Editor batchmode，Mono JIT；真机 IL2CPP / HybridCLR 解释执行另测）与 1 秒内连按");
            CampaignState s = NewState(117);
            string a = Print(s, FirmwareCatalog.FwOverloadId);
            string b = Print(s, FirmwareCatalog.FwHomingId);
            SignalCoreService.TrySavePreset(s, "x");
            SignalCoreService.TryEquip(s, a, 0);
            SignalCoreService.TryEquip(s, b, 1);
            SignalCoreService.TrySavePreset(s, "y");
            const int n = 2000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                SignalCoreService.TryUnequip(s, 0);
                SignalCoreService.TryEquip(s, a, 0);
            }
            sw.Stop();
            double opMs = sw.Elapsed.TotalMilliseconds / (2 * n);
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                SignalCoreService.TryApplyPreset(s, s.SignalCore.Presets[i % 2].PresetId);
            }
            sw.Stop();
            double presetMs = sw.Elapsed.TotalMilliseconds / n;

            CampaignSession.Set(0, s);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            double hudMs;
            try
            {
                hud.BindView(root);
                hud.Refresh();
                sw.Restart();
                for (int i = 0; i < 10000; i++)
                {
                    hud.Refresh();
                }
                sw.Stop();
                hudMs = sw.Elapsed.TotalMilliseconds / 10000;
            }
            finally
            {
                Object.DestroyImmediate(go);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                CampaignSession.Clear();
            }
            ExpectPerf(true,
                $"装 / 卸 {opMs:F4} ms/次（门槛 0.2）、切换预设 {presetMs:F4} ms/次（门槛 0.5）、HUD 键不变的每帧刷新 {hudMs:F5} ms（门槛 0.02）；与机器总数无关（只读槽位与基元仓）",
                PerfGate.Lt(opMs, 0.2, "装卸 ms/次"), PerfGate.Lt(presetMs, 0.5, "切换预设 ms/次"), PerfGate.Lt(hudMs, 0.02, "HUD 每帧刷新 ms"));

            // 1 秒内连按 10 次装 / 卸（同一帧内）：状态一致、不复制、不重复插入。
            CampaignState t = NewState(118);
            string c = Print(t, FirmwareCatalog.FwOverloadId);
            int total = t.PrimitiveChips.Length;
            int ok = 0;
            for (int i = 0; i < 10; i++)
            {
                ok += (i % 2 == 0 ? SignalCoreService.TryEquip(t, c, 0) : SignalCoreService.TryUnequip(t, 0)).Success ? 1 : 0;
                ok += SignalCoreService.TryEquip(t, c, i % 2 == 0 ? 0 : 1).Success ? 1 : 0;
            }
            Expect(Invariant(t, out _) && t.PrimitiveChips.Length == total && t.SignalCore.SlotPartIds.Count(id => id == c) <= 1,
                $"连按 20 次装 / 卸 / 换位（成功 {ok} 次）：实例总数不变、同一实例最多在一个槽位（{Describe(t)}）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>新战役 + 已供电的装配站 + 基元仓播种 + 信号核初始化（与进入家园的顺序一致）。</summary>
        private static CampaignState NewState(int seed)
        {
            CampaignState s = CampaignState.CreateNew("fgsig-" + seed, "Standard", seed);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 1000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            return s;
        }

        private static string Print(CampaignState s, string firmwareId)
        {
            SignalCoreResult r = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
            if (!r.Success)
            {
                Fail($"测试准备：刻印 {firmwareId} 失败（{r.Message}）");
            }
            return r.CreatedId;
        }

        /// <summary>四态恰一：信号核状态的实例 == 槽位里的实例集合；实例 ID 唯一；槽位不重复；槽位数组长度 = 最多槽位；基元仓不超容量。</summary>
        private static bool Invariant(CampaignState s, out string why)
        {
            var ids = s.PrimitiveChips.Select(c => c.PartId).ToList();
            var inCore = new HashSet<string>(s.PrimitiveChips.Where(c => c.State == PrimitiveChipState.SignalCore).Select(c => c.PartId));
            var slots = s.SignalCore.SlotPartIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
            bool ok = ids.Distinct().Count() == ids.Count
                      && slots.Distinct().Count() == slots.Count
                      && inCore.SetEquals(slots)
                      && s.SignalCore.SlotPartIds.Length == SignalCoreService.MaxSlots
                      && PrimitiveInventory.BagCount(s) <= PrimitiveInventory.Capacity;
            why = $"实例 {ids.Count}、信号核 {inCore.Count}、槽位 {slots.Count}、基元仓 {PrimitiveInventory.BagCount(s)}/{PrimitiveInventory.Capacity}";
            return ok;
        }

        private static string Describe(CampaignState s)
        {
            Invariant(s, out string why);
            return why;
        }

        /// <summary>信号核域 + 实例账 + 废料的逐字快照（JsonUtility，与存档同一序列化）。</summary>
        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.SignalCore));
            foreach (PrimitiveChipRecord c in s.PrimitiveChips.OrderBy(c => c.PartId, StringComparer.Ordinal))
            {
                sb.Append('|').Append(c.PartId).Append(':').Append(c.CardDefId).Append(':').Append((int)c.State).Append(':').Append(c.ReservedByTransactionId ?? string.Empty);
            }
            sb.Append("|scrap=").Append(s.Scrap);
            return sb.ToString();
        }

        private static VisualElement Mount(out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgSignalCoreSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        /// <summary>点一个 UI Toolkit 按钮：走按钮自己的 Clickable（与鼠标点击同一回调）。</summary>
        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static (int code, string output) RunPython(string root, string args)
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
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
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

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
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
