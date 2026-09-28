using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-SIG-02 接入口与双态编译预览的自动验收（FG01 FGR-SIG-020～023，第 4 章撤销重做，第 6 章存档；FG13 FGU-18；FGT-SIG-002、005）。
    /// 全部起真实系统：<see cref="BlueprintCircuitBoard"/> / <see cref="UplinkCompiler"/> / <see cref="BlueprintCircuitCompiler"/> /
    /// <see cref="MachineLoadoutRegistry"/> / <see cref="SignalCoreService"/> / <see cref="CampaignSaveService"/> / 真 UXML 面板 / 真输入路由，行为坏了会失败：
    /// A 数据（配额读表、代码用到的文本键中英齐全）；B 标记接入口与负向矩阵；C 撤销重做（按钮同入口 + Ctrl+Z / Ctrl+Y 快捷键，没有编辑器时仍是“尚未开放”）；
    /// D 双态编译（AI 按空槽、只作用于经过接入口的路径、重炮 + 过载出熔穿过载、接入口没接通、AI 永不填接入口）；
    /// E 配额与路径截断（FGT-SIG-005）；F 预览与实际结算逐字段一致（FGT-SIG-002）；G 存读档（真实文件、旧档、篡改）；
    /// H 暂停 / 倍速 / 种子无关；I 界面（真 UXML：接入口图标 + 文字、两栏、差异高亮、说明、中英、布局探针）；J 性能（重编译 ≤ 2 ms，与机器数无关）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgUplinkPreviewSelfCheck
    {
        private const string UxmlPath = "Assets/GameRes/Raw/UI/CircuitBoard/CircuitBoardPanel.uxml";
        private const string BpId = "bp_selfcheck_uplink";
        private const string BpCannonId = "bp_selfcheck_uplink_cannon";
        private const string BpMarkId = "bp_selfcheck_uplink_mark";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/自检：FG 接入口与双态预览")]
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
            Line("\n[接入口] 接入口与双态编译预览（FG1-SIG-02）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fguplink-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FirmwareKinds.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;

                CheckData();
                CheckMarking();
                CheckUndoRedo();
                CheckDualCompile();
                CheckQuotaAndPathLimit();
                CheckPreviewMatchesActual();
                CheckSaveLoad();
                CheckPauseSpeedSeeds();
                CheckUi();
                CheckPerformance();
            }
            catch (Exception e)
            {
                Fail($"接入口自检抛异常：{e}");
            }
            finally
            {
                UplinkCompiler.ResetForTests();
                UiUndoRouter.ResetForTests();
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                MachineLoadoutRegistry.Clear();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                NotificationCenter.ResetForTests();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                StrategyClock.Reset();
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
            Line("  · A. 数据：配额读表、代码用到的文本键中英齐全、引导钩子登记");
            bool fromTable = GridContent.TryGetTuning("signal.uplink.quota", out float q);
            Expect(fromTable && Mathf.Approximately(q, 2f) && UplinkCompiler.Quota == 2 && UplinkCompiler.PathLimit == BlueprintCircuitLayout.MaxPaths,
                $"接入口配额读 fg.TbHomeTuning signal.uplink.quota = {q}（FGR-SIG-023 初值 2），路径上限沿用现有规则 {UplinkCompiler.PathLimit} 条");

            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files =
            {
                "Campaign/Blueprint/UplinkCompiler.cs", "Campaign/Blueprint/BlueprintCircuitBoard.cs",
                "UI/CircuitBoard/CircuitUplinkView.cs", "UI/CircuitBoard/CircuitBoardPanelUIToolkit.cs",
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)), "\"(circuit\\.uplink\\.[a-z_.]+)\""))
                {
                    keys.Add(m.Groups[1].Value);
                }
            }
            var missing = keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k, GameLanguage.En))
                                          || GameText.Get(k, GameLanguage.En) == GameText.Get(k, GameLanguage.ZhCn)).ToList();
            Expect(keys.Count >= 40 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个 circuit.uplink.* 文本键都在 fg.TbLocText 里、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.CircuitUplinkFirstSeen),
                $"引导钩子“第一次遇到接入口”（{GuidanceHooks.CircuitUplinkFirstSeen}）已登记，引导内容在 FG15-UX-04");
        }

        // ── B. 标记接入口与负向矩阵 ──────────────────────────────────────────────

        private static void CheckMarking()
        {
            Line("  · B. 标记接入口（FGR-SIG-020）与负向：0 / 8 号格、第二个接入口、装着芯片的格、越界");
            BlueprintCircuitBoard b = GunBoard();
            string sigBefore = b.ComputeSignature();
            int undo0 = b.UndoDepth;

            CircuitOpResult src = b.TrySetUplink(0);
            CircuitOpResult sink = b.TrySetUplink(8);
            CircuitOpResult over = b.TrySetUplink(9);
            CircuitOpResult neg = b.TrySetUplink(-1);
            Expect(!src.Success && src.Code == BlueprintCircuitBoard.UplinkFixedSlotCode && src.Message == GameText.Get("circuit.uplink.reason.source_slot")
                   && !sink.Success && sink.Code == BlueprintCircuitBoard.UplinkFixedSlotCode && sink.Message == GameText.Get("circuit.uplink.reason.sink_slot")
                   && !over.Success && over.Code == BlueprintCircuitBoard.UplinkOutOfRangeCode && !neg.Success
                   && !b.HasUplink && b.UndoDepth == undo0,
                $"0 号格拒绝（“{src.Message}”）、8 号格拒绝（“{sink.Message}”）、越界拒绝；状态与撤销栈都不变");

            CircuitOpResult ok = b.TrySetUplink(1);
            Expect(ok.Success && b.HasUplink && b.UplinkSlot == 1 && b.UndoDepth == undo0 + 1 && b.ComputeSignature() != sigBefore
                   && b.ComputeSignature().Contains("|UP:1") && !sigBefore.Contains("|UP:"),
                "空的 1 号格标为接入口成功，进撤销栈；签名带上接入口（没有接入口的签名与以前逐字相同）");

            CircuitOpResult second = b.TrySetUplink(2);
            CircuitOpResult same = b.TrySetUplink(1);
            Expect(!second.Success && second.Code == BlueprintCircuitBoard.UplinkSecondCode && second.Message.Contains("1") && b.UplinkSlot == 1
                   && !same.Success && same.Code == BlueprintCircuitBoard.UplinkSameCode && b.UndoDepth == undo0 + 1,
                $"标第二个接入口被拒（“{second.Message}”），原接入口不动；重复标同一格也拒绝");

            CircuitOpResult chipOnPort = b.TryPlaceChip(1, PrimitiveInventory.DefaultChipContentId);
            Expect(!chipOnPort.Success && chipOnPort.Code == BlueprintCircuitBoard.UplinkCellCode && string.IsNullOrEmpty(b.SlotContentIds[1]),
                $"接入口格不能装芯片（“{chipOnPort.Message}”）");

            int chipSlot = PlaceChip(b, 2, 3, 5, 6, 7);
            CircuitOpResult clear = b.TryClearUplink();
            CircuitOpResult onChip = chipSlot > 0 ? b.TrySetUplink(chipSlot) : CircuitOpResult.Ok();
            CircuitOpResult clearAgain = b.TryClearUplink();
            Expect(chipSlot > 0 && clear.Success && !b.HasUplink && !onChip.Success && onChip.Code == BlueprintCircuitBoard.UplinkOccupiedCode
                   && !clearAgain.Success && clearAgain.Code == BlueprintCircuitBoard.UplinkNoneCode,
                $"装着芯片的 {chipSlot} 号格不能标（“{onChip.Message}”）；取消接入口成功，再取消给原因（“{clearAgain.Message}”）");

            // 负载配额独立：标接入口不改负载，也不影响保存校验。
            BlueprintCircuitBoard load = GunBoard();
            load.TryComputeLoadPreview(out int loadBefore, out _);
            bool validBefore = load.Validate().IsValid;
            load.TrySetUplink(1);
            load.TryComputeLoadPreview(out int loadAfter, out _);
            Expect(loadBefore == loadAfter && validBefore && load.Validate().IsValid,
                $"接入口不占电路负载（{loadBefore} → {loadAfter}），标了以后蓝图照样能保存");
        }

        // ── C. 撤销重做 ──────────────────────────────────────────────────────────

        private static void CheckUndoRedo()
        {
            Line("  · C. 撤销和重做（FG01 第 4 章；B02 / B03）：按钮与 Ctrl+Z / Ctrl+Y 同一入口，可重绑");
            BlueprintCircuitBoard b = GunBoard();
            b.TrySetUplink(1);
            int chip = PlaceChip(b, 3, 6, 7, 2, 5);
            bool u1 = b.Undo();
            bool chipGone = chip > 0 && string.IsNullOrEmpty(b.SlotContentIds[chip]) && b.UplinkSlot == 1;
            bool u2 = b.Undo();
            bool portGone = !b.HasUplink;
            bool r1 = b.Redo();
            Expect(u1 && chipGone && u2 && portGone && r1 && b.HasUplink && b.UplinkSlot == 1,
                "标接入口 → 装芯片 → 撤销两步（接入口也撤掉）→ 重做一步（接入口回来）");
            b.TryClearUplink();
            b.Undo();
            Expect(b.HasUplink && b.UplinkSlot == 1, "取消接入口也能撤销");

            // 快捷键：电路编辑器占用时 Ctrl+Z / Ctrl+Y 执行，不弹“尚未开放”；没有编辑器时仍是建造撤销（FG3-LOG-07）的“尚未开放”提示。
            Expect(InputActionCatalog.TryGet(GameActionId.Undo, out InputActionDef undoDef) && InputActionCatalog.TryGet(GameActionId.Redo, out InputActionDef redoDef)
                   && undoDef.DefaultChord.Mods == InputModifier.Ctrl && undoDef.DefaultChord.Key == KeyCode.Z && redoDef.DefaultChord.Key == KeyCode.Y,
                "撤销 / 重做动作在动作表里，默认 Ctrl+Z / Ctrl+Y");
            var reader = new KeyReader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            NotificationCenter.ResetForTests();
            var owner = new object();
            BlueprintCircuitBoard k = GunBoard();
            k.TrySetUplink(2);
            try
            {
                UiUndoRouter.Claim(owner, () => k.Undo(), () => k.Redo());
                PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                bool undone = !k.HasUplink && UiUndoRouter.LastResult == 1;
                PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Redo));
                bool redone = k.HasUplink && k.UplinkSlot == 2 && UiUndoRouter.LastResult == 2;
                bool noLocked = NotificationCenter.Toasts.All(t => t.Type.Id != "feature_locked");
                Expect(undone && redone && noLocked, "编辑器打开时 Ctrl+Z 撤销、Ctrl+Y 重做（真输入路由），不弹“后续版本开放”");

                // 重绑：改成 Ctrl+Alt+U，旧键失效、新键生效，恢复默认。
                bool wasCustom = GameSettings.KeyBindings.IsCustomized(GameActionId.Undo);
                InputChord previous = GameSettings.KeyBindings.GetChord(GameActionId.Undo);
                var conflicts = new List<GameActionId>();
                RebindResult rb = GameSettings.KeyBindings.TryRebind(GameActionId.Undo, new InputChord(KeyCode.U, InputModifier.Ctrl | InputModifier.Alt), conflicts);
                bool oldDead;
                bool newWorks;
                try
                {
                    PressChord(reader, new InputChord(KeyCode.Z, InputModifier.Ctrl));
                    oldDead = k.HasUplink;
                    PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                    newWorks = !k.HasUplink;
                }
                finally
                {
                    if (wasCustom)
                    {
                        GameSettings.KeyBindings.ForceRebind(GameActionId.Undo, previous);
                    }
                    else
                    {
                        GameSettings.KeyBindings.ResetToDefault(GameActionId.Undo);
                    }
                }
                Expect(rb == RebindResult.Ok && oldDead && newWorks && GameSettings.KeyBindings.GetChord(GameActionId.Undo).Key == previous.Key,
                    "撤销键可重绑：改成 Ctrl+Alt+U 后旧键不再撤销、新键撤销；恢复默认");

                // 编辑器上面压着别的层（信号核面板 / 确认框）：Ctrl+Z 不在玩家看不见的地方改下面的草稿，也不弹“尚未开放”；
                // 上层关掉后恢复。
                k.Redo();
                UiEscapeStack.ResetForTests();
                UiEscapeStack.Push(owner, () => UiEscapeStack.Remove(owner));
                var cover = new object();
                UiEscapeStack.Push(cover, () => UiEscapeStack.Remove(cover));
                NotificationCenter.ResetForTests();
                bool covered = UiUndoRouter.IsCoveredByOtherLayer();
                PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                bool blocked = k.HasUplink && UiUndoRouter.LastResult == UiUndoRouter.BlockedByLayer
                               && NotificationCenter.Toasts.All(t => t.Type.Id != "feature_locked");
                UiEscapeStack.Remove(cover);
                PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                bool resumed = !k.HasUplink && UiUndoRouter.LastResult == 1 && !UiUndoRouter.IsCoveredByOtherLayer();
                UiEscapeStack.ResetForTests();
                Expect(covered && blocked && resumed,
                    "编辑器上面压着别的层时 Ctrl+Z 不改下面的电路、不弹“尚未开放”；上层关掉后 Ctrl+Z 恢复撤销");

                UiUndoRouter.Release(owner);
                k.Redo();
                PressChord(reader, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                bool untouched = k.HasUplink;
                bool locked = NotificationCenter.Toasts.Any(t => t.Type.Id == "feature_locked");
                Expect(!UiUndoRouter.HasTarget && untouched && locked,
                    "编辑器关闭后 Ctrl+Z 不再动电路，仍按建造撤销（FG3-LOG-07）给“后续版本开放”提示");
            }
            finally
            {
                UiUndoRouter.ResetForTests();
                UiEscapeStack.ResetForTests();
                NotificationCenter.ResetForTests();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        // ── D. 双态编译 ──────────────────────────────────────────────────────────

        private static void CheckDualCompile()
        {
            Line("  · D. 双态编译（FGR-SIG-021、022、090）：AI 按空槽；插入只作用于经过接入口的路径；重炮 + 过载出熔穿过载");
            string[] core = { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId, string.Empty, string.Empty, string.Empty };

            // D1：AI 驾驶时 = 不标接入口时的编译，逐字段。
            BlueprintCircuitBoard plain = TwoPathGunBoard(out string pathB);
            BlueprintCircuitPreview noPort = BlueprintCircuitCompiler.CompilePreview(plain);
            plain.TrySetUplink(1);
            UplinkDualPreview dual = UplinkCompiler.CompileDual(plain, core);
            bool aiSameAsNoPort = SameFields(Strip(dual.Ai), Strip(noPort), out string d1, out int n1);
            Expect(noPort.PathCount == 2 && aiSameAsNoPort && n1 >= 14 && dual.Ai.UplinkFirmwareIds.Length == 0,
                $"AI 驾驶时：接入口按空槽处理，编译结果与不标接入口时逐字段相同（比较 {n1} 项{(d1 == null ? string.Empty : "，差异 " + d1)}），接入口里没有固件");

            // D2：你接入时：信号核按槽位顺序插入，只作用于经过接入口的路径。
            BlueprintCircuitPreview up = dual.Uplinked;
            BlueprintCircuitPathPreview upA = up.Paths.FirstOrDefault(p => p.SlotPath.Contains(1));
            BlueprintCircuitPathPreview upB = up.Paths.FirstOrDefault(p => string.Join("→", p.SlotPath) == pathB);
            BlueprintCircuitPathPreview aiB = dual.Ai.Paths.FirstOrDefault(p => string.Join("→", p.SlotPath) == pathB);
            Expect(up.UplinkOnPath && up.UplinkFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId })
                   && up.FirmwareIds.Contains(FirmwareCatalog.FwOverloadId) && upA != null && upA.ThroughUplink
                   && upB != null && !upB.ThroughUplink && aiB != null && Math.Abs(upB.Damage - aiB.Damage) < 1e-6f && upB.Tags.SetEquals(aiB.Tags),
                $"你接入时：过载、寻的按 1、2 号槽顺序插入；经过接入口的路径标记为已插入，另一条路径 {pathB} 与 AI 驾驶时完全相同");
            Expect(dual.Diff.Any(x => x.Kind == UplinkDiffKind.PathChanged && x.Text.Contains("过载")) && dual.Diff.Any(x => x.Kind == UplinkDiffKind.HeatChanged)
                   && dual.UplinkedLines[0].Highlight && !dual.AiLines[0].Highlight && dual.UplinkedLines.Count == CircuitUplinkView.LineCount
                   && dual.AiLines.Count == CircuitUplinkView.LineCount,
                $"差异列出被插入的路径与热量变化，“你接入时”栏与左栏不同的行高亮：{string.Join(" / ", dual.Diff.Select(x => x.Text))}");

            // D3：重炮 + 过载（FGJ-M1 第 1 步的预期）：AI 驾驶没有反应，你接入时出熔穿过载，热量 40 → 65。
            BlueprintCircuitBoard cannon = CannonBoard();
            cannon.TrySetUplink(2);
            UplinkDualPreview c = UplinkCompiler.CompileDual(cannon, new[] { FirmwareCatalog.FwOverloadId });
            Expect(c.Ai.ReactionId == null && c.Uplinked.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId
                   && Mathf.Approximately(c.Ai.HeatBudget, 40f) && Mathf.Approximately(c.Uplinked.HeatBudget, 65f)
                   && c.Diff.Any(x => x.Kind == UplinkDiffKind.ReactionAdded && x.Text.Contains("熔穿过载"))
                   && c.UplinkedLines[4].Highlight && c.UplinkedLines[3].Highlight,
                $"重炮机：AI 驾驶时无反应、热量 {c.Ai.HeatBudget}；接入口插入过载后出“熔穿过载”、热量 {c.Uplinked.HeatBudget}（差异高亮反应行与热量行）");

            // D4：接入口不在任何源→汇路径上：插入不生效，并说明怎么修。
            BlueprintCircuitBoard off = CannonBoard();
            off.TrySetUplink(3);
            UplinkDualPreview o = UplinkCompiler.CompileDual(off, new[] { FirmwareCatalog.FwOverloadId });
            Expect(!o.Uplinked.UplinkOnPath && o.Uplinked.UplinkFirmwareIds.Length == 0 && o.Uplinked.ReactionId == null
                   && SameFields(Strip(o.Uplinked), Strip(o.Ai), out _, out _) && o.Diff.Count == 0
                   && o.Notes.Contains(GameText.Format("circuit.uplink.note.off_path", 3)),
                $"接入口 3 号格没接通：你接入时与 AI 驾驶时相同，说明“{o.Notes.FirstOrDefault()}”");

            // D4b：双向回边（1⇄2）让 2 号格“从源可达、也能到汇”，但没有一条简单源→汇路径经过它——同样算没接通。
            // 连射器走编出的路径判定，重炮（编不出路径）走导线简单路径 DFS，两条分支都测。
            foreach ((string label, BlueprintCircuitBoard raw) in new[] { ("连射器", GunBoard()), ("重炮", CannonBoard()) })
            {
                BlueprintCircuitBoard back = BackEdgeBoard(raw);
                back.TrySetUplink(2);
                UplinkDualPreview bd = UplinkCompiler.CompileDual(back, new[] { FirmwareCatalog.FwOverloadId });
                Expect(!back.IsOnSourceSinkPath(2) && back.IsOnSourceSinkPath(4) && !bd.Uplinked.UplinkOnPath
                       && bd.Uplinked.UplinkFirmwareIds.Length == 0 && bd.Uplinked.Paths.All(p => !p.ThroughUplink)
                       && bd.Uplinked.ReactionId == bd.Ai.ReactionId && Mathf.Approximately(bd.Uplinked.HeatBudget, bd.Ai.HeatBudget)
                       && bd.Diff.Count == 0 && bd.Notes.Contains(GameText.Format("circuit.uplink.note.off_path", 2)),
                    $"{label}：导线 0→1、1⇄2、1→4→5→8，接入口在 2 号格——没有简单路径经过它，插入不生效（热量 {bd.Uplinked.HeatBudget}、反应 {bd.Uplinked.ReactionId ?? "无"}），说明“{bd.Notes.FirstOrDefault()}”");
            }

            // D6：标记跳转（目录里有、没有 gene 模块，反应按固件 ID 判定）照常插进接入口——与装在机器自己的固件槽同一口径。
            BlueprintCircuitBoard markBoard = MarkerGunBoard();
            markBoard.TrySetUplink(1);
            UplinkDualPreview md = UplinkCompiler.CompileDual(markBoard, new[] { FirmwareCatalog.FwMarkTagId, FirmwareCatalog.FwArmorPierceId, FirmwareCatalog.FwHomingId });
            BlueprintCircuitBoard ownSlot = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId,
                ComponentCatalog.FuncMarkerId, null, new[] { FirmwareCatalog.FwMarkTagId });
            Expect(md.Plan.InsertedIds.SequenceEqual(new[] { FirmwareCatalog.FwMarkTagId, FirmwareCatalog.FwArmorPierceId })
                   && md.Plan.Skipped.Count == 1 && md.Plan.Skipped[0].FirmwareId == FirmwareCatalog.FwHomingId && md.Plan.Skipped[0].Skip == UplinkSkipReason.OverQuota
                   && md.Ai.ReactionId == null && md.Uplinked.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId
                   && BlueprintCircuitCompiler.CompilePreview(ownSlot).ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId
                   && md.Diff.Any(x => x.Kind == UplinkDiffKind.ReactionAdded && x.Text.Contains("标记跳转"))
                   && md.Notes.All(n => !n.Contains("不是已知的固件")),
                $"连射器 + 标记器，信号核放标记跳转、装甲击穿、寻的：前两枚插入（没有 gene 模块也占配额），你接入时判出“标记跳转”，与装在机器固件槽时一致；寻的超配额。差异：{string.Join(" / ", md.Diff.Select(x => x.Text))}");

            // D5：AI 永远不填接入口（FGR-SIG-090）：AI 出口不接受信号核参数，结构上只走空槽编译。
            MethodInfo ai = typeof(MachineLoadoutRegistry).GetMethod(nameof(MachineLoadoutRegistry.ResolveForAi));
            Expect(ai != null && ai.GetParameters().Length == 3 && ai.GetParameters().All(p => p.ParameterType != typeof(IReadOnlyList<string>)),
                "AI 驾驶的结算出口（ResolveForAi）没有信号核参数——AI 不可能填接入口");
        }

        // ── E. 配额与路径截断 ──────────────────────────────────────────────────────

        private static void CheckQuotaAndPathLimit()
        {
            Line("  · E. 接入口配额与路径上限截断（FGR-SIG-023 / FGT-SIG-005）；信号核为空、没有接入口、无法编译的固件");
            BlueprintCircuitBoard b = GunBoard();
            b.TrySetUplink(1);
            string[] three = { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId, FirmwareCatalog.FwSplitId };
            UplinkDualPreview d = UplinkCompiler.CompileDual(b, three);
            Expect(d.Plan.InsertedIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId })
                   && d.Plan.Skipped.Count == 1 && d.Plan.Skipped[0].FirmwareId == FirmwareCatalog.FwSplitId && d.Plan.Skipped[0].Skip == UplinkSkipReason.OverQuota
                   && d.Plan.Skipped[0].CoreSlot == 2 && d.Notes.Contains("未插入：分裂（超出接入口配额 2 个）"),
                $"信号核 3 枚、配额 2：按槽位顺序插前两枚，第 3 枚“{d.Notes.LastOrDefault()}”");

            // 路径上限：注入“每插一枚多 2 条路径”的规则（现有 Demo 固件都不改变拓扑，见 UplinkCompiler.PathCountOverrideForTests）。
            try
            {
                UplinkCompiler.PathCountOverrideForTests = (board, ins) => 1 + 2 * ins.Count;
                UplinkInsertionPlan p = UplinkCompiler.Plan(b, three);
                Expect(p.InsertedIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId }) && p.PathCountAfter == 3 && p.PathCountAfter <= p.PathLimit
                       && p.Skipped.Any(x => x.FirmwareId == FirmwareCatalog.FwHomingId && x.Skip == UplinkSkipReason.PathLimit)
                       && p.Skipped.Any(x => x.FirmwareId == FirmwareCatalog.FwSplitId && x.Skip == UplinkSkipReason.OverQuota),
                    "插两枚会到 5 条路径 > 上限 4：按槽位倒序撤下寻的（优先级最低的先撤），剩 3 条，合法；分裂仍是超配额");
                UplinkDualPreview pd = UplinkCompiler.CompileDual(b, three);
                Expect(pd.Notes.Contains(GameText.Format("circuit.uplink.skip.path_limit", "寻的", 4)), $"路径截断的原因写给玩家：“{pd.Notes.FirstOrDefault(n => n.Contains("路径"))}”");

                UplinkCompiler.PathCountOverrideForTests = (board, ins) => 1 + 9 * ins.Count;
                UplinkInsertionPlan all = UplinkCompiler.Plan(b, three);
                BlueprintCircuitPreview none = UplinkCompiler.CompileUplinked(b, all);
                Expect(all.Inserted.Count == 0 && all.PathCountAfter == 1 && SameFields(Strip(none), Strip(BlueprintCircuitCompiler.CompilePreview(b)), out _, out _),
                    "每枚都会让路径超限：全部撤下，结果与 AI 驾驶时相同——插入永远不会让电路进入非法状态");

                UplinkCompiler.PathCountOverrideForTests = (board, ins) => 6;
                UplinkInsertionPlan own = UplinkCompiler.Plan(b, three);
                Expect(own.Inserted.Count == 2, "电路自己（未保存的草稿）已经超过上限：那不是插入造成的，不为它撤固件");
            }
            finally
            {
                UplinkCompiler.ResetForTests();
            }

            UplinkDualPreview empty = UplinkCompiler.CompileDual(b, new[] { string.Empty, string.Empty });
            Expect(empty.Plan.CoreEmpty && empty.Plan.Inserted.Count == 0 && empty.Diff.Count == 0
                   && empty.Notes.Any(n => n.StartsWith("信号核没有固件", StringComparison.Ordinal))
                   && SameFields(Strip(empty.Uplinked), Strip(empty.Ai), out _, out _),
                $"信号核为空时的预览：接入口为空、与 AI 驾驶时相同，说明“{empty.Notes.FirstOrDefault()}”");

            BlueprintCircuitBoard noPort = GunBoard();
            UplinkDualPreview np = UplinkCompiler.CompileDual(noPort, new[] { FirmwareCatalog.FwOverloadId });
            Expect(!np.Plan.HasUplink && np.Plan.Skipped.Count == 1 && np.Plan.Skipped[0].Skip == UplinkSkipReason.NoUplink
                   && np.Notes.Contains(GameText.Get("circuit.uplink.note.no_uplink")) && np.Notes.Contains("未插入：过载（此机器没有接入口）")
                   && np.Diff.Count == 0,
                "没有接入口的蓝图：照样可以接入但不插入固件，逐条说明未插入的原因");

            UplinkDualPreview unknown = UplinkCompiler.CompileDual(b, new[] { "fw_selfcheck_ghost", FirmwareCatalog.FwHomingId });
            Expect(unknown.Plan.InsertedIds.SequenceEqual(new[] { FirmwareCatalog.FwHomingId })
                   && unknown.Plan.Skipped.Any(x => x.FirmwareId == "fw_selfcheck_ghost" && x.Skip == UplinkSkipReason.Unknown)
                   && unknown.Notes.Contains(GameText.Format("circuit.uplink.skip.unknown", "fw_selfcheck_ghost")),
                $"固件目录里查不到的 ID（旧档残留 / 篡改）不占配额，单独说明原因：“{unknown.Notes.FirstOrDefault(n => n.Contains("fw_selfcheck_ghost"))}”");
        }

        // ── F. 预览与实际结算一致（FGT-SIG-002）───────────────────────────────────

        private static void CheckPreviewMatchesActual()
        {
            Line("  · F. 双态预览与实际结算逐字段一致（FGT-SIG-002）：同一台登记的机器、同一个信号核");
            CampaignState s = NewState(301);
            string o = Print(s, FirmwareCatalog.FwOverloadId);
            string h = Print(s, FirmwareCatalog.FwHomingId);
            SignalCoreService.TryEquip(s, o, 0);
            SignalCoreService.TryEquip(s, h, 1);
            string[] core = SignalCoreService.CurrentContentIds(s);

            BlueprintCircuitBoard gun = TwoPathGunBoard(out _);
            gun.TrySetUplink(1);
            BlueprintCircuitBoard cannon = CannonBoard();
            cannon.TrySetUplink(2);
            AddBlueprint(s, BpId, gun);
            AddBlueprint(s, BpCannonId, cannon);
            MachineLoadoutRegistry.Clear();
            CircuitOpResult reg1 = MachineLoadoutRegistry.Register(s, 901, BpId, 1);
            CircuitOpResult reg2 = MachineLoadoutRegistry.Register(s, 902, BpCannonId, 1);
            Expect(reg1.Success && reg2.Success, "两台测试机器登记到装配登记表（与区域刷机器同一入口）");

            foreach ((int id, string bp) in new[] { (901, BpId), (902, BpCannonId) })
            {
                BlueprintCircuitBoard saved = BlueprintCircuitBoard.FromVersion(BlueprintEditorService.FindActiveVersion(s, bp));
                UplinkDualPreview dual = UplinkCompiler.CompileDual(saved, core);
                MachineCombatResolution actualUp = MachineLoadoutRegistry.ResolveForUplink(s, id, 1, core);
                MachineCombatResolution actualAi = MachineLoadoutRegistry.ResolveForAi(s, id, 1);
                bool upSame = SameFields(dual.Uplinked, actualUp.Preview, out string du, out int nu);
                bool aiSame = SameFields(dual.Ai, actualAi.Preview, out string da, out int na);
                Expect(actualUp.Success && actualAi.Success && upSame && aiSame && nu >= 14 && na >= 14
                       && actualUp.Preview.UplinkFirmwareIds.Length > 0 && actualAi.Preview.UplinkFirmwareIds.Length == 0,
                    $"机器 {id}（{bp}）：“你接入时”预览 = 接入结算（{nu} 项{(du == null ? string.Empty : "，差异 " + du)}），“AI 驾驶时”预览 = AI 结算（{na} 项{(da == null ? string.Empty : "，差异 " + da)}）");
            }

            // 标记跳转（没有 gene 模块、反应按固件 ID 判定）走接入口：预览与接入结算同样逐字段一致，且都判出标记跳转。
            BlueprintCircuitBoard mark = MarkerGunBoard();
            mark.TrySetUplink(1);
            AddBlueprint(s, BpMarkId, mark);
            CircuitOpResult reg3 = MachineLoadoutRegistry.Register(s, 903, BpMarkId, 1);
            string[] markCore = { FirmwareCatalog.FwMarkTagId };
            UplinkDualPreview markDual = UplinkCompiler.CompileDual(BlueprintCircuitBoard.FromVersion(BlueprintEditorService.FindActiveVersion(s, BpMarkId)), markCore);
            MachineCombatResolution markUp = MachineLoadoutRegistry.ResolveForUplink(s, 903, 1, markCore);
            bool markSame = SameFields(markDual.Uplinked, markUp.Preview, out string dm, out int nm);
            Expect(reg3.Success && markUp.Success && markSame && nm >= 14 && markUp.Preview.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId,
                $"机器 903（连射器 + 标记器，接入口插标记跳转）：预览 = 接入结算（{nm} 项{(dm == null ? string.Empty : "，差异 " + dm)}），都判出标记跳转");

            // 界面显示的就是这份结果：真 UXML 上渲染，读回视图持有的结果与实际结算比较。
            VisualElement root = Mount(out GameObject go);
            try
            {
                var view = new CircuitUplinkView();
                view.Bind(root);
                UplinkDualPreview shown = view.Render(BlueprintCircuitBoard.FromVersion(BlueprintEditorService.FindActiveVersion(s, BpCannonId)), s);
                MachineCombatResolution actual = MachineLoadoutRegistry.ResolveForUplink(s, 902, 1, core);
                Expect(shown != null && SameFields(shown.Uplinked, actual.Preview, out _, out _) && view.UplinkedLine(4).Contains("熔穿过载"),
                    $"电路编辑器按当前信号核（{view.CoreLineText}）显示的“你接入时”与实际接入结算一致：“{view.UplinkedLine(4)}”");
            }
            finally
            {
                Object.DestroyImmediate(go);
                MachineLoadoutRegistry.Clear();
            }
        }

        // ── G. 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · G. 存读档（B10）：接入口随蓝图版本进存档；旧档没有字段 = 没有接入口；篡改的接入口按没有读");
            CampaignState s = NewState(302);
            BlueprintCircuitBoard b = CannonBoard();
            b.TrySetUplink(2);
            AddBlueprint(s, BpCannonId, b);
            string[] core = { FirmwareCatalog.FwOverloadId };
            BlueprintCircuitBoard before = BlueprintCircuitBoard.FromVersion(BlueprintEditorService.FindActiveVersion(s, BpCannonId));
            UplinkDualPreview dualBefore = UplinkCompiler.CompileDual(before, core);
            SaveResult saved = CampaignSaveService.Save(0, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(0);
            BlueprintVersionRecord v = loaded.State != null ? BlueprintEditorService.FindActiveVersion(loaded.State, BpCannonId) : null;
            BlueprintCircuitBoard after = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            UplinkDualPreview dualAfter = after != null ? UplinkCompiler.CompileDual(after, core) : null;
            Expect(saved.Success && loaded.Outcome == LoadOutcome.Success && v != null && v.CircuitUplinkSlot == 2 && after.UplinkSlot == 2
                   && after.ComputeSignature() == v.CompileSignature && dualAfter != null
                   && SameFields(dualAfter.Uplinked, dualBefore.Uplinked, out _, out _) && SameFields(dualAfter.Ai, dualBefore.Ai, out _, out _),
                "真实文件存 → 读：接入口 2 号格保留，签名不漂移，双态编译逐字段相同");

            // 旧档：JSON 里没有这个字段 → 读回 0 = 没有接入口。
            string json = JsonUtility.ToJson(BlueprintEditorService.FindActiveVersion(s, BpCannonId));
            string legacy = Regex.Replace(json, ",?\"CircuitUplinkSlot\":\\d+", string.Empty);
            BlueprintVersionRecord old = JsonUtility.FromJson<BlueprintVersionRecord>(legacy);
            BlueprintCircuitBoard oldBoard = BlueprintCircuitBoard.FromVersion(old);
            Expect(!legacy.Contains("CircuitUplinkSlot") && old.CircuitUplinkSlot == 0 && !oldBoard.HasUplink && !oldBoard.ComputeSignature().Contains("|UP:"),
                "旧档（没有 CircuitUplinkSlot 字段）读成“没有接入口”，签名与以前相同，不用升存档版本");

            // 篡改：指向 8 号、越界、装着芯片的格 → 按没有接入口读，不臆造。
            BlueprintVersionRecord t8 = JsonUtility.FromJson<BlueprintVersionRecord>(json);
            t8.CircuitUplinkSlot = 8;
            BlueprintVersionRecord t99 = JsonUtility.FromJson<BlueprintVersionRecord>(json);
            t99.CircuitUplinkSlot = 99;
            BlueprintCircuitBoard withChip = CannonBoard();
            int chip = PlaceChip(withChip, 2, 5, 3, 6, 7);
            BlueprintVersionRecord tChip = withChip.ToVersion(1, 0f);
            tChip.CircuitUplinkSlot = chip;
            Expect(!BlueprintCircuitBoard.FromVersion(t8).HasUplink && !BlueprintCircuitBoard.FromVersion(t99).HasUplink
                   && chip > 0 && !BlueprintCircuitBoard.FromVersion(tChip).HasUplink,
                "篡改存档：接入口指向 8 号格 / 越界 / 装着芯片的格，一律按没有接入口读");
        }

        // ── H. 暂停、倍速、种子无关 ──────────────────────────────────────────────

        private static void CheckPauseSpeedSeeds()
        {
            Line("  · H. 暂停与 0.5x～3x、种子无关（B09 / B25）；家园后台一致性不适用（编译预览没有随时间变化的状态）");
            var results = new List<string>();
            foreach ((bool paused, float speed, int seed) in new[] { (false, 1f, 401), (true, 1f, 402), (false, 0.5f, 403), (false, 3f, 99991) })
            {
                StrategyClock.Reset();
                StrategyClock.SetSpeed(speed);
                InputRouter.SetGameplayPaused(paused, strategic: true);
                CampaignState s = NewState(seed);
                SignalCoreService.TryEquip(s, Print(s, FirmwareCatalog.FwOverloadId), 0);
                BlueprintCircuitBoard b = CannonBoard();
                b.TrySetUplink(2);
                UplinkDualPreview d = UplinkCompiler.CompileDual(b, SignalCoreService.CurrentContentIds(s));
                results.Add(string.Join("|", d.UplinkedLines.Select(l => l.Text)) + "#" + string.Join("|", d.Diff.Select(x => x.Text)));
                InputRouter.SetGameplayPaused(false);
            }
            StrategyClock.Reset();
            Expect(results.Distinct().Count() == 1 && results[0].Contains("熔穿过载"),
                "同一张电路 + 同一信号核在 正常 / 战略暂停 / 0.5x / 3x、四个世界种子下预览完全相同（纯计算，不计时、不读坐标）");
        }

        // ── I. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · I. 界面（FGU-18）：接入口图标 + 颜色 + 文字标注、两栏并排、差异高亮、说明、中英、布局探针");
            CampaignState s = NewState(303);
            SignalCoreService.TryEquip(s, Print(s, FirmwareCatalog.FwOverloadId), 0);
            VisualElement root = Mount(out GameObject go);
            try
            {
                var view = new CircuitUplinkView();
                List<string> missing = view.Bind(root);
                Expect(missing.Count == 0, $"视图元素都在 UXML 里{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");

                BlueprintCircuitBoard b = CannonBoard();
                b.TrySetUplink(2);
                view.ApplySlots(b);
                view.ApplyToggle(b, 2);
                view.Render(b, s);
                bool onlyTwo = Enumerable.Range(0, BlueprintCircuitLayout.SlotCount).All(i => view.SlotShowsUplink(i) == (i == 2));
                Expect(onlyTwo && view.SlotTagText(2) == "接入口" && view.ToggleButton.text == GameText.Get("circuit.uplink.unmark"),
                    "只有 2 号格显示接入口：菱形图标 + 青绿边框 + 文字“接入口”（不只靠颜色）；选中接入口时按钮是“取消接入口”");
                Expect(view.AiLine(4) == "反应：无" && view.UplinkedLine(4).Contains("熔穿过载") && view.UplinkedLineHighlighted(4)
                       && !view.UplinkedLineHighlighted(1) && view.DiffHighlighted && view.DiffText.Contains("新增反应")
                       && view.CoreLineText.Contains("过载"),
                    $"两栏并排：左“{view.AiLine(4)}”，右“{view.UplinkedLine(4)}”（高亮）；差异“{view.DiffText.Replace("\n", " / ")}”");

                view.ApplyToggle(b, 3);
                BlueprintCircuitBoard none = CannonBoard();
                view.ApplySlots(none);
                view.Render(none, s);
                Expect(view.ToggleButton.text == GameText.Get("circuit.uplink.mark") && Enumerable.Range(0, 9).All(i => !view.SlotShowsUplink(i))
                       && view.NotesText.Contains(GameText.Get("circuit.uplink.note.no_uplink")) && !view.DiffHighlighted
                       && view.DiffText == GameText.Get("circuit.uplink.diff.none"),
                    $"没有接入口：格子上没有标记，说明“{view.NotesText.Split('\n')[0]}”，差异“{view.DiffText}”");

                GameSettings.SetLanguage(GameLanguage.En);
                view.ApplyStaticTexts();
                view.Render(b, s);
                string all = string.Join(" ", Enumerable.Range(0, CircuitUplinkView.LineCount).Select(view.UplinkedLine)) + view.DiffText + view.CoreLineText + view.SlotTagText(2);
                Expect(!GameText.ContainsMarker(all) && view.SlotTagText(2) == "Uplink" && root.Q<Label>("UplinkUpTitle").text == "When you uplink",
                    $"英文：{view.UplinkedLine(0)} / {view.DiffText.Replace("\n", " / ")}");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                view.ApplyStaticTexts();
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Object.DestroyImmediate(go);
            }

            // 布局探针：整个电路编辑器面板（含新的双态区与检查器按钮），渲染一份带说明的预览；中英 × UI 缩放极值 × 四种分辨率。
            CampaignSession.Set(0, s);
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                {
                    string result = UiToolkitLayoutProbe.Probe(UxmlPath, "CircuitBoardPanelRoot", stressFill: true, prepare: r =>
                    {
                        var v = new CircuitUplinkView();
                        v.Bind(r.panel.visualTree);
                        BlueprintCircuitBoard pb = CannonBoard();
                        pb.TrySetUplink(3);
                        v.ApplySlots(pb);
                        v.Render(pb, s);
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 CircuitBoardPanel.uxml [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignSession.Clear();
        }

        // ── J. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 性能（FG01 第 7 章：插入后的重编译一帧内完成，初值 ≤ 2 ms；与机器总数无关）");
            BlueprintCircuitBoard b = TwoPathGunBoard(out _);
            b.TrySetUplink(1);
            string[] core = { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId, FirmwareCatalog.FwSplitId, string.Empty, string.Empty };
            for (int i = 0; i < 20; i++)
            {
                UplinkCompiler.CompileUplinked(b, core);
            }
            const int runs = 300;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++)
            {
                UplinkCompiler.CompileUplinked(b, core);
            }
            double recompileMs = sw.Elapsed.TotalMilliseconds / runs;
            sw.Restart();
            for (int i = 0; i < runs; i++)
            {
                UplinkCompiler.CompileDual(b, core);
            }
            double dualMs = sw.Elapsed.TotalMilliseconds / runs;

            // 与机器总数无关：登记 1 台与 400 台时，单台接入结算耗时相同量级。
            CampaignState s = NewState(304);
            AddBlueprint(s, BpId, b);
            MachineLoadoutRegistry.Clear();
            MachineLoadoutRegistry.Register(s, 1, BpId, 1);
            double one = TimeResolve(s, core);
            for (int i = 2; i <= 400; i++)
            {
                MachineLoadoutRegistry.Register(s, i, BpId, 1);
            }
            double many = TimeResolve(s, core);
            MachineLoadoutRegistry.Clear();
            Expect(recompileMs <= 2.0 && many <= Math.Max(one * 3.0, one + 0.5),
                $"Editor batchmode（Mono JIT，真机 IL2CPP / HybridCLR 解释执行未测）：接入重编译 {recompileMs:F3} ms / 次，双态预览（两次编译 + 差异）{dualMs:F3} ms / 次；登记 1 台 {one:F3} ms、400 台 {many:F3} ms");
        }

        private static double TimeResolve(CampaignState s, string[] core)
        {
            const int runs = 100;
            MachineLoadoutRegistry.ResolveForUplink(s, 1, 1, core);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++)
            {
                MachineLoadoutRegistry.ResolveForUplink(s, 1, 1, core);
            }
            return sw.Elapsed.TotalMilliseconds / runs;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static BlueprintCircuitBoard GunBoard() =>
            BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());

        private static BlueprintCircuitBoard MarkerGunBoard() =>
            BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null, Array.Empty<string>());

        /// <summary>双向回边：0→1、1⇄2、1→4、4→5、5→8（去掉默认的 2→5）。2 号格从源可达、也能走到汇，
        /// 但没有任何一条不重复经过格的源→汇路径经过它（唯一路径是 0→1→4→5→8）。</summary>
        private static BlueprintCircuitBoard BackEdgeBoard(BlueprintCircuitBoard b)
        {
            PlaceChip(b, 6);
            PlaceChip(b, 7);
            CircuitOpResult rm = b.TryRemoveEdge(2, 5);
            if (!rm.Success)
            {
                Fail($"测试准备：删导线 2→5 失败（{rm.Message}）");
            }
            foreach ((int f, int t) in new[] { (2, 1), (1, 4), (4, 5) })
            {
                CircuitOpResult r = b.TryAddEdge(f, t);
                if (!r.Success)
                {
                    Fail($"测试准备：加导线 {f}→{t} 失败（{r.Message}）");
                }
            }
            return b;
        }

        private static BlueprintCircuitBoard CannonBoard() =>
            BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());

        /// <summary>两条源→汇路径：默认线 0→1→2→5→8，另一条 0→3→6→7→8（先在路径上装两枚芯片抬高导线软帽）。</summary>
        private static BlueprintCircuitBoard TwoPathGunBoard(out string pathB)
        {
            BlueprintCircuitBoard b = GunBoard();
            PlaceChip(b, 3);
            PlaceChip(b, 6, 7, 2, 5);
            foreach ((int f, int t) in new[] { (0, 3), (3, 6), (6, 7), (7, 8) })
            {
                CircuitOpResult r = b.TryAddEdge(f, t);
                if (!r.Success)
                {
                    Fail($"测试准备：加导线 {f}→{t} 失败（{r.Message}）");
                }
            }
            pathB = "0→3→6→7→8";
            return b;
        }

        /// <summary>在候选格里找第一个能装默认芯片的格装上，返回格号（都装不上返回 -1）。</summary>
        private static int PlaceChip(BlueprintCircuitBoard b, params int[] candidates)
        {
            foreach (int slot in candidates)
            {
                if (string.IsNullOrEmpty(b.SlotContentIds[slot]) && !(b.HasUplink && b.UplinkSlot == slot)
                    && b.TryPlaceChip(slot, PrimitiveInventory.DefaultChipContentId).Success)
                {
                    return slot;
                }
            }
            Fail($"测试准备：{string.Join(",", candidates)} 号格都装不上默认芯片");
            return -1;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        /// <summary>新战役 + 已供电的装配站 + 基元仓播种 + 信号核初始化（与进入家园的顺序一致）。</summary>
        private static CampaignState NewState(int seed)
        {
            CampaignState s = CampaignState.CreateNew("fguplink-" + seed, "Standard", seed);
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

        /// <summary>去掉只描述“接入口在哪”的字段，用来比较“你接入时 = AI 驾驶时”（接入口位置两边本来就相同，但不接入口的电路是 0）。</summary>
        private static BlueprintCircuitPreview Strip(BlueprintCircuitPreview p)
        {
            p.UplinkSlot = 0;
            p.UplinkOnPath = false;
            return p;
        }

        /// <summary>逐字段比较两份编译结果（反射遍历全部公开字段，路径逐条逐字段，集合按集合比）。新增字段自动纳入比较。</summary>
        private static bool SameFields(object a, object b, out string diff, out int compared)
        {
            compared = 0;
            diff = null;
            return Same(a, b, "preview", ref compared, ref diff);
        }

        private static bool Same(object a, object b, string path, ref int compared, ref string diff)
        {
            if (a == null || b == null)
            {
                compared++;
                if (a == null && b == null)
                {
                    return true;
                }
                diff ??= $"{path}: {(a == null ? "null" : "有值")} ≠ {(b == null ? "null" : "有值")}";
                return false;
            }
            Type t = a.GetType();
            if (t.IsPrimitive || a is string || t.IsEnum)
            {
                compared++;
                if (Equals(a, b))
                {
                    return true;
                }
                diff ??= $"{path}: {a} ≠ {b}";
                return false;
            }
            if (a is HashSet<string> ha && b is HashSet<string> hb)
            {
                compared++;
                if (ha.SetEquals(hb))
                {
                    return true;
                }
                diff ??= $"{path}: {{{string.Join(",", ha)}}} ≠ {{{string.Join(",", hb)}}}";
                return false;
            }
            if (a is IEnumerable ea && b is IEnumerable eb)
            {
                List<object> la = ea.Cast<object>().ToList();
                List<object> lb = eb.Cast<object>().ToList();
                compared++;
                if (la.Count != lb.Count)
                {
                    diff ??= $"{path}: 数量 {la.Count} ≠ {lb.Count}";
                    return false;
                }
                bool ok = true;
                for (int i = 0; i < la.Count; i++)
                {
                    ok &= Same(la[i], lb[i], $"{path}[{i}]", ref compared, ref diff);
                }
                return ok;
            }
            bool all = true;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                all &= Same(f.GetValue(a), f.GetValue(b), path + "." + f.Name, ref compared, ref diff);
            }
            return all;
        }

        private static void PressChord(KeyReader reader, InputChord chord)
        {
            if ((chord.Mods & InputModifier.Ctrl) != 0) reader.Hold(KeyCode.LeftControl);
            if ((chord.Mods & InputModifier.Alt) != 0) reader.Hold(KeyCode.LeftAlt);
            if ((chord.Mods & InputModifier.Shift) != 0) reader.Hold(KeyCode.LeftShift);
            reader.Press(chord.Key);
            UiKitInputPump.ProcessWorldKeys();
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private static VisualElement Mount(out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgUplinkPreviewSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private sealed class KeyReader : IInputReader
        {
            private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();

            public void Press(KeyCode key) => _down.Add(key);
            public void Hold(KeyCode key) => _held.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                _held.Clear();
            }

            public bool GetKey(KeyCode key) => _held.Contains(key) || _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
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
