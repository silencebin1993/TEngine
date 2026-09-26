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
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-UX-01 的自动验收（FGR-ARC-007 / FGR-ARC-012 / FG13 第 4～7 节 / FGT-UX-003、FGT-UX-004 基础版）。
    ///
    /// 全部断言起真实系统跑：真实 Luban 表（fg.TbInputAction / TbNotifyTier / TbNotifyType / TbUiTuning / TbLocText）、
    /// 真实 <see cref="InputRouter"/>（注入按帧回放的读取器）、真实 <see cref="GameSettings"/>（先备份玩家本机设置、结束时还原）、
    /// 真实 <see cref="NotificationCenter"/> 与真实存档服务（存档目录重定向到临时目录）、真实 UXML（挂到临时面板上，
    /// 用控制器自己的 BindView 绑定后驱动）。负向矩阵：1 秒 50 条通知、重绑冲突（覆盖 / 取消 / 必保留 / 鼠标专用 / 按住类）、
    /// UI 缩放极值（80% / 150%）× FG13 四种分辨率 × 中英文、文本框焦点吞键、改键采集吞键、旧设置迁移。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgUiKitSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";

        /// <summary>FG13 第 11 节 FGR-UX-070：所有面板在这四种分辨率下检查。</summary>
        private static readonly Vector2Int[] Fg13Resolutions =
        {
            new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3440, 1440),
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/自检：FG UI 基础件与输入上下文")]
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
            Line("\n[UI基础件] UI 基础件与输入上下文（FG0-UX-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（改设置 / 注入输入会干扰正在进行的游戏）");
                return 0;
            }

            string savedSettings = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadSettings = PlayerPrefs.HasKey(SettingsPrefsKey);
            string saveDirBefore = CampaignSaveService.SaveDirectoryOverrideForTests;
            string tempSaveDir = Path.Combine(Path.GetTempPath(), "fgux01-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                InputActionCatalog.Reload();
                NotificationCatalog.Reload();
                UiTuningValues.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = tempSaveDir;

                Section("A. 动作登记表与默认键（FGR-ARC-012 / FG13 第 5 节 / FGT-UX-003）", CheckCatalog);
                Section("B. 输入上下文与组合键（FGR-ARC-012）", CheckContexts);
                Section("C. 重绑与冲突（FGT-UX-003 / FG13 第 12 节）", CheckRebinding);
                Section("D. 旧设置迁移与按键采集", CheckMigrationAndCapture);
                Section("E. 生产代码不再直接读字面量按键", CheckNoLiteralKeys);
                Section("F. 通知中心：分级 / 聚合 / 定位 / 历史 / 自动暂停（FGT-UX-004）", CheckNotifications);
                Section("G. 通知历史存读档与读档变更（DEBT-FG0SAVE01-04）", CheckNotificationSave);
                Section("H. 确认框 / 悬停提示 / 右键菜单 / 拖放 / Esc 逐层返回", CheckOverlayWidgets);
                Section("I. 搜索框 / 虚拟化列表 / 标签页 / 图表 / 快捷键提示 / 进度条 / 状态图标 / 数字格式", CheckGalleryWidgets);
                Section("J. 按键面板与通知中心界面（真实 UXML）", CheckPanels);
                Section("K. 布局探针：FG13 四种分辨率 × UI 缩放 80%/100%/150% × 中英文", CheckLayouts);
                Section("L. 文本键：新界面零硬编码文字、引用的键全部存在（FGR-ARC-006 / B16）", CheckTextKeys);
                Section("M. 家园复合数值来源展开（FGR-UX-030 / FG-GAP-003）与引导钩子（B14）", CheckBreakdownAndHooks);
                Section("N. 性能（Editor batchmode 实测）", CheckPerformance);
                Section("O. 审查修复回归：真实触发点定位 / 真实镜头定位 / 暂停菜单存档 / Esc 覆盖 Demo 面板 / 文本焦点 / 右键取消", CheckReviewFixes);
            }
            catch (Exception e)
            {
                Fail($"UI 基础件自检抛异常：{e}");
            }
            finally
            {
                CleanupRuntime();
                CampaignSession.Clear();
                CampaignSaveService.SaveDirectoryOverrideForTests = saveDirBefore;
                try
                {
                    if (Directory.Exists(tempSaveDir))
                    {
                        Directory.Delete(tempSaveDir, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
                if (hadSettings)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedSettings);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                PlayerPrefs.Save();
                GameSettings.Load();
                UiTuningValues.ResetForTests();
                InputActionCatalog.ResetForTests();
                NotificationCatalog.ResetForTests();
            }
            Line($"  [UI基础件] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Section(string title, Action body)
        {
            Line("  · " + title);
            try
            {
                body();
            }
            catch (Exception e)
            {
                Fail($"{title} 抛异常：{e}");
            }
            finally
            {
                CleanupRuntime();
            }
        }

        private static void CleanupRuntime()
        {
            UiConfirmDialog.ResetForTests();
            UiContextMenu.ResetForTests();
            UiDragDrop.ResetForTests();
            UiTooltip.ResetForTests();
            UiEscapeStack.ResetForTests();
            InputRouter.Reset();
            InputRouter.SetUiPointerBlocker(null);
            NotificationCenter.ResetForTests();
            NotificationCenter.AutoPauseHandler = null;
            NotificationCenter.LocateHandler = null;
            NotificationCenter.RegionProvider = null;
            StrategyClock.Reset();
        }

        // ── A. 动作登记表 ─────────────────────────────────────────────────────

        private static void CheckCatalog()
        {
            Expect(InputActionCatalog.LoadError == null && InputActionCatalog.ValidationErrors.Count == 0,
                $"fg.TbInputAction 读取成功、每行都能解析（错误：{InputActionCatalog.LoadError ?? string.Join("；", InputActionCatalog.ValidationErrors)}）");

            var enumMembers = ((GameActionId[])Enum.GetValues(typeof(GameActionId))).Distinct().ToList();
            var missing = enumMembers.Where(a => !InputActionCatalog.TryGet(a, out _)).ToList();
            var extra = InputActionCatalog.All.Where(d => !enumMembers.Contains(d.Action)).ToList();
            Expect(missing.Count == 0 && extra.Count == 0 && InputActionCatalog.All.Count == enumMembers.Count && enumMembers.Count == 85,
                $"GameActionId 的 {enumMembers.Count} 个成员与表里 {InputActionCatalog.All.Count} 行一一对应（缺：{string.Join(",", missing)}；多：{string.Join(",", extra.Select(d => d.Action))}）");

            InputBindingSet defaults = InputBindingSet.CreateDefault();
            var conflicts = defaults.FindAllConflicts();
            Expect(conflicts.Count == 0, $"默认键在每个上下文内零冲突（FGT-UX-003）{(conflicts.Count == 0 ? string.Empty : "：" + string.Join("；", conflicts.Select(c => c.A + "↔" + c.B)))}");

            // FG13 第 5 节逐项核对（含合并决策：命令 R/T/G/Z、快速存读档 Ctrl+F5/Ctrl+F9、接入 V、任务日志 L）。
            var expected = new (GameActionId Action, KeyCode Key, InputModifier Mods)[]
            {
                (GameActionId.MoveForward, KeyCode.W, InputModifier.None), (GameActionId.StrategyPanUp, KeyCode.UpArrow, InputModifier.None),
                (GameActionId.ZoomIn, VirtualKeys.WheelUp, InputModifier.None), (GameActionId.ZoomOut, VirtualKeys.WheelDown, InputModifier.None),
                (GameActionId.TogglePause, KeyCode.Space, InputModifier.None), (GameActionId.SpeedHalf, KeyCode.Alpha1, InputModifier.None),
                (GameActionId.SpeedNormal, KeyCode.Alpha2, InputModifier.None), (GameActionId.SpeedDouble, KeyCode.Alpha3, InputModifier.None),
                (GameActionId.SpeedTriple, KeyCode.Alpha4, InputModifier.None), (GameActionId.FocusHomeCore, KeyCode.Home, InputModifier.None),
                (GameActionId.FollowSelection, KeyCode.F, InputModifier.None), (GameActionId.OpenBuildMenu, KeyCode.B, InputModifier.None),
                (GameActionId.Hotbar1, KeyCode.F1, InputModifier.None), (GameActionId.Hotbar10, KeyCode.F10, InputModifier.None),
                (GameActionId.Rotate, KeyCode.R, InputModifier.None), (GameActionId.Eyedropper, KeyCode.Q, InputModifier.None),
                (GameActionId.DemolishMode, KeyCode.X, InputModifier.None), (GameActionId.UpgradePlan, KeyCode.U, InputModifier.None),
                (GameActionId.Copy, KeyCode.C, InputModifier.Ctrl), (GameActionId.Paste, KeyCode.V, InputModifier.Ctrl),
                (GameActionId.Undo, KeyCode.Z, InputModifier.Ctrl), (GameActionId.Redo, KeyCode.Y, InputModifier.Ctrl),
                (GameActionId.LayoutLibrary, KeyCode.B, InputModifier.Ctrl), (GameActionId.ToggleOverlay, KeyCode.O, InputModifier.None),
                (GameActionId.ToggleCameraView, KeyCode.V, InputModifier.None), (GameActionId.CycleControlTarget, KeyCode.Tab, InputModifier.None),
                (GameActionId.JumpHome, KeyCode.H, InputModifier.None), (GameActionId.JumpPreviousMachine, KeyCode.J, InputModifier.None),
                (GameActionId.GroupAssign1, KeyCode.Alpha1, InputModifier.Ctrl), (GameActionId.Group1, KeyCode.Alpha1, InputModifier.Alt),
                (GameActionId.GroupAssign9, KeyCode.Alpha9, InputModifier.Ctrl), (GameActionId.Group9, KeyCode.Alpha9, InputModifier.Alt),
                (GameActionId.Interact, KeyCode.E, InputModifier.None), (GameActionId.PrimaryAction, KeyCode.Mouse0, InputModifier.None),
                (GameActionId.SecondaryAction, KeyCode.Mouse1, InputModifier.None), (GameActionId.OpenMap, KeyCode.M, InputModifier.None),
                (GameActionId.OpenRoster, KeyCode.N, InputModifier.None), (GameActionId.OpenResearch, KeyCode.K, InputModifier.None),
                (GameActionId.OpenFirmware, KeyCode.I, InputModifier.None), (GameActionId.ToggleMissionLog, KeyCode.L, InputModifier.None),
                (GameActionId.OpenCodex, KeyCode.C, InputModifier.None), (GameActionId.OpenIntel, KeyCode.Y, InputModifier.None),
                (GameActionId.ToggleNotificationCenter, KeyCode.BackQuote, InputModifier.None),
                (GameActionId.QuickSave, KeyCode.F5, InputModifier.Ctrl), (GameActionId.QuickLoad, KeyCode.F9, InputModifier.Ctrl),
                (GameActionId.Cancel, KeyCode.Escape, InputModifier.None), (GameActionId.CommandMove, KeyCode.R, InputModifier.None),
                (GameActionId.CommandAttack, KeyCode.T, InputModifier.None), (GameActionId.CommandGuard, KeyCode.G, InputModifier.None),
                (GameActionId.CommandRetreat, KeyCode.Z, InputModifier.None), (GameActionId.DirectSkillSlot0, KeyCode.LeftShift, InputModifier.None),
                (GameActionId.PinTooltip, KeyCode.LeftAlt, InputModifier.None), (GameActionId.UiConfirm, KeyCode.Return, InputModifier.None),
            };
            var wrong = expected.Where(e => defaults.GetChord(e.Action) != new InputChord(e.Key, e.Mods))
                .Select(e => $"{e.Action}={defaults.GetChord(e.Action)}").ToList();
            Expect(wrong.Count == 0, $"FG13 第 5 节默认键逐项核对 {expected.Length} 项（与现有 6 个动作合并后的结果）{(wrong.Count == 0 ? string.Empty : "——不符：" + string.Join("，", wrong))}");

            // 上下文：R 在战略里是“移动命令”、在建造里是“旋转”——同一按键不同上下文可以不同（FGR-ARC-012）。
            InputActionCatalog.TryGet(GameActionId.CommandMove, out InputActionDef move);
            InputActionCatalog.TryGet(GameActionId.Rotate, out InputActionDef rotate);
            Expect(move.Contexts == InputContext.Strategy && rotate.Contexts == InputContext.Build
                   && !InputBindingSet.Conflicts(move, move.DefaultChord, rotate, rotate.DefaultChord),
                "同一按键 R：战略上下文 = 移动命令，建造上下文 = 旋转，不算冲突");

            // 每个动作都能重绑（FGR-ARC-012“全部可重绑”），并能恢复默认。
            var notRebindable = new List<string>();
            foreach (InputActionDef def in InputActionCatalog.All)
            {
                var set = InputBindingSet.CreateDefault();
                InputChord target = def.MouseOnly ? new InputChord(KeyCode.Mouse4) : new InputChord(KeyCode.F12, InputModifier.Ctrl | InputModifier.Alt);
                var c = new List<GameActionId>();
                RebindResult r = set.TryRebind(def.Action, target, c);
                if (r != RebindResult.Ok || set.GetChord(def.Action) != target || set.ResetToDefault(def.Action) != RebindResult.Ok
                    || set.GetChord(def.Action) != def.DefaultChord)
                {
                    notRebindable.Add($"{def.Action}:{r}");
                }
            }
            Expect(notRebindable.Count == 0, $"全部 {InputActionCatalog.All.Count} 个动作都能重绑并恢复默认{(notRebindable.Count == 0 ? string.Empty : "——失败：" + string.Join(",", notRebindable))}");

            // reserved 必须写承接 Story；wired 必须真有生产代码消费。
            var badOwner = InputActionCatalog.All.Where(d => d.Status == InputActionStatus.Reserved && !Regex.IsMatch(d.Owner ?? string.Empty, @"^FG\d+-[A-Z]+-\d+$"))
                .Select(d => d.Action.ToString()).ToList();
            Expect(badOwner.Count == 0, $"尚未开放的 {InputActionCatalog.All.Count(d => d.Status == InputActionStatus.Reserved)} 个动作都写了承接 Story{(badOwner.Count == 0 ? string.Empty : "——缺：" + string.Join(",", badOwner))}");

            string logicRoot = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] excluded = { "GameActionId.cs", "InputBindingSet.cs", "InputActionCatalog.cs" };
            var sources = Directory.GetFiles(logicRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !excluded.Contains(Path.GetFileName(f)) && !f.Replace('\\', '/').Contains("/FirstPlayable/"))
                .Select(File.ReadAllText).ToList();
            string all = string.Join("\n", sources);
            var unused = new List<string>();
            foreach (InputActionDef def in InputActionCatalog.All.Where(d => d.Status == InputActionStatus.Wired))
            {
                string name = def.Action.ToString();
                bool used = all.Contains("GameActionId." + name);
                if (!used && name.StartsWith("GroupAssign", StringComparison.Ordinal))
                {
                    used = all.Contains("GameActionId.GroupAssign1 + slot - 1");
                }
                if (!used)
                {
                    unused.Add(name);
                }
            }
            Expect(sources.Count > 200 && unused.Count == 0,
                $"标为“已接入”的 {InputActionCatalog.All.Count(d => d.Status == InputActionStatus.Wired)} 个动作在生产代码里都有消费方（扫描 {sources.Count} 个源文件）{(unused.Count == 0 ? string.Empty : "——无人消费：" + string.Join(",", unused))}");
        }

        // ── B. 上下文与组合键 ──────────────────────────────────────────────────

        private static void CheckContexts()
        {
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);

            InputRouter.SetScope(InputScope.Strategy);
            InputContext s = InputRouter.ActiveContext;
            InputRouter.SetBuildMode(true);
            InputContext b = InputRouter.ActiveContext;
            InputRouter.SetBuildMode(false);
            InputRouter.SetScope(InputScope.Direct);
            InputContext u = InputRouter.ActiveContext;
            object owner = new object();
            InputRouter.PushModal(owner);
            InputContext i = InputRouter.ActiveContext;
            InputRouter.PopModal(owner);
            InputContext back = InputRouter.ActiveContext;
            InputRouter.SetScope(InputScope.None);
            InputContext none = InputRouter.ActiveContext;
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(true, strategic: true);
            InputContext strategicPause = InputRouter.ActiveContext;
            InputRouter.SetGameplayPaused(true, strategic: false);
            InputContext hardPause = InputRouter.ActiveContext;
            InputRouter.SetGameplayPaused(false);
            Expect(s == InputContext.Strategy && b == InputContext.Build && u == InputContext.Uplink && i == InputContext.Interface
                   && back == InputContext.Uplink && none == InputContext.None && strategicPause == InputContext.Strategy && hardPause == InputContext.Interface,
                $"生效上下文：战略={s}、建造={b}、接入={u}、模态={i}、关模态回到={back}、过渡={none}、战略暂停={strategicPause}、普通暂停={hardPause}");

            // 同一个键 R：战略里触发“移动命令”，建造里不触发（归“旋转”）。
            InputRouter.SetScope(InputScope.Strategy);
            reader.Press(KeyCode.R);
            bool moveInStrategy = InputRouter.ConsumeAction(GameActionId.CommandMove, InputScope.Strategy);
            Frame(reader);
            InputRouter.SetBuildMode(true);
            reader.Press(KeyCode.R);
            bool moveInBuild = InputRouter.ConsumeAction(GameActionId.CommandMove, InputScope.Strategy);
            bool rotateInBuild = InputRouter.ConsumeContextAction(GameActionId.Rotate);
            InputRouter.SetBuildMode(false);
            Frame(reader);
            Expect(moveInStrategy && !moveInBuild && rotateInBuild, $"R：战略→移动命令 {moveInStrategy}；建造→移动命令 {moveInBuild}（应 false）、旋转 {rotateInBuild}");

            // 组合键：Ctrl+1 = 设定编组 1，Alt+1 = 选择编组 1，单独 1 = 0.5x；互不串。
            reader.Hold(KeyCode.LeftControl);
            reader.Press(KeyCode.Alpha1);
            bool ctrlAssign = InputRouter.ConsumeAction(GameActionId.GroupAssign1, InputScope.Strategy);
            bool ctrlSelect = InputRouter.ConsumeAction(GameActionId.Group1, InputScope.Strategy);
            bool ctrlSpeed = InputRouter.ConsumeAction(GameActionId.SpeedHalf, InputScope.Strategy);
            Frame(reader);
            reader.Hold(KeyCode.LeftAlt);
            reader.Press(KeyCode.Alpha1);
            bool altSpeed = InputRouter.ConsumeAction(GameActionId.SpeedHalf, InputScope.Strategy);
            bool altAssign = InputRouter.ConsumeAction(GameActionId.GroupAssign1, InputScope.Strategy);
            bool altSelect = InputRouter.ConsumeAction(GameActionId.Group1, InputScope.Strategy);
            Frame(reader);
            reader.Press(KeyCode.Alpha1);
            bool plainGroup = InputRouter.ConsumeAction(GameActionId.Group1, InputScope.Strategy);
            bool plainSpeed = InputRouter.ConsumeAction(GameActionId.SpeedHalf, InputScope.Strategy);
            Frame(reader);
            Expect(ctrlAssign && !ctrlSelect && !ctrlSpeed && !altSpeed && !altAssign && altSelect && !plainGroup && plainSpeed,
                $"数字 1：Ctrl→设定编组({ctrlAssign}/{ctrlSelect}/{ctrlSpeed})，Alt→选择编组({altAssign}/{altSelect}/{altSpeed})，单独→0.5x({plainGroup}/{plainSpeed})");

            // Shift 是限定键：Shift+Z 仍是撤退；Ctrl+Z 是撤销（建造上下文之外的战略里也登记了撤销），不是撤退。
            reader.Hold(KeyCode.LeftShift);
            reader.Press(KeyCode.Z);
            bool shiftRetreat = InputRouter.ConsumeAction(GameActionId.CommandRetreat, InputScope.Strategy);
            Frame(reader);
            reader.Hold(KeyCode.LeftControl);
            reader.Press(KeyCode.Z);
            bool ctrlRetreat = InputRouter.ConsumeAction(GameActionId.CommandRetreat, InputScope.Strategy);
            bool ctrlUndo = InputRouter.ConsumeContextAction(GameActionId.Undo);
            Frame(reader);
            Expect(shiftRetreat && !ctrlRetreat && ctrlUndo, $"Shift+Z → 撤退 {shiftRetreat}；Ctrl+Z → 撤退 {ctrlRetreat}（应 false）、撤销 {ctrlUndo}");

            // 同帧同键只有第一个消费者拿到。
            reader.Press(KeyCode.G);
            bool first = InputRouter.ConsumeAction(GameActionId.CommandGuard, InputScope.Strategy);
            bool second = InputRouter.ConsumeAction(GameActionId.CommandGuard, InputScope.Strategy);
            Frame(reader);
            Expect(first && !second, "同一帧同一个键只被消费一次（结构上不可能双重触发）");

            // 文本框焦点、改键采集期间一律吞键；采集结束的那一帧仍吞（防止结束采集的那个键被别处再读）。
            InputRouter.SetTextInputFocused(true);
            reader.Press(KeyCode.G);
            bool typed = InputRouter.ConsumeAction(GameActionId.CommandGuard, InputScope.Strategy)
                         || InputRouter.ConsumeGlobalAction(GameActionId.ToggleMissionLog, true)
                         || InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Strategy);
            InputRouter.SetTextInputFocused(false);
            Frame(reader);
            InputRouter.SetRebindCapture(true);
            reader.Press(KeyCode.Escape);
            bool capturedEsc = InputRouter.ConsumeGlobalAction(GameActionId.Cancel, true);
            InputRouter.SetRebindCapture(false);
            bool sameFrameEsc = InputRouter.ConsumeGlobalAction(GameActionId.Cancel, true);
            Frame(reader);
            InputRouter.Reset();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            Expect(!typed && !capturedEsc && !sameFrameEsc, $"文本框有焦点时不触发任何快捷键（{typed}）；改键采集中 / 结束当帧 Esc 不关窗口（{capturedEsc}/{sameFrameEsc}）");

            // 鼠标主 / 功能动作左右互换：逻辑键 0 读物理右键。
            var set = GameSettings.KeyBindings;
            GameSettings.ForceRebind(GameActionId.PrimaryAction, new InputChord(KeyCode.Mouse1));
            GameSettings.ForceRebind(GameActionId.SecondaryAction, new InputChord(KeyCode.Mouse0));
            reader.MouseDown.Add(1);
            bool primaryFromRight = InputRouter.GetMouseButtonDown(0, InputScope.Strategy);
            bool secondaryFromRight = InputRouter.GetMouseButtonDown(1, InputScope.Strategy);
            Frame(reader);
            GameSettings.ResetKeyBindingsToDefault();
            reader.MouseDown.Add(0);
            bool primaryDefault = InputRouter.GetMouseButtonDown(0, InputScope.Strategy);
            Frame(reader);
            Expect(primaryFromRight && !secondaryFromRight && primaryDefault && set.GetChord(GameActionId.PrimaryAction).Key == KeyCode.Mouse0,
                "左右手互换：主动作改绑右键后，世界层的逻辑左键读物理右键；恢复默认后回到左键");

            // 缩放可重绑：默认滚轮，改成键盘键后滚轮不再拉近、按键拉近。
            reader.Scroll = 1f;
            float wheelIn = InputRouter.GetZoomDelta(InputScope.Strategy);
            Frame(reader);
            GameSettings.ForceRebind(GameActionId.ZoomIn, new InputChord(KeyCode.Equals));
            reader.Scroll = 1f;
            float wheelAfter = InputRouter.GetZoomDelta(InputScope.Strategy);
            Frame(reader);
            reader.Press(KeyCode.Equals);
            float keyIn = InputRouter.GetZoomDelta(InputScope.Strategy);
            Frame(reader);
            reader.Scroll = -1f;
            float wheelOut = InputRouter.GetZoomDelta(InputScope.Strategy);
            Frame(reader);
            GameSettings.ResetKeyBindingsToDefault();
            Expect(wheelIn > 0.9f && Mathf.Abs(wheelAfter) < 0.001f && keyIn > 0.9f && wheelOut < -0.9f,
                $"缩放：默认滚轮 {wheelIn:+0.0;-0.0}；拉近改成 = 键后滚轮上 {wheelAfter:0.0}、= 键 {keyIn:+0.0}；拉远仍是滚轮下 {wheelOut:+0.0;-0.0}");

            // 平移：方向键上走“镜头上移（方向键）”，只在战略 / 建造；接入里方向键不动镜头。
            reader.Hold(KeyCode.UpArrow);
            bool panStrategy = InputRouter.GetActionKey(GameActionId.StrategyPanUp, InputScope.Strategy);
            InputRouter.SetScope(InputScope.Direct);
            bool panUplink = InputRouter.GetActionKey(GameActionId.StrategyPanUp, InputScope.Direct);
            InputRouter.SetScope(InputScope.Strategy);
            Frame(reader);
            Expect(panStrategy && !panUplink, $"方向键平移：战略 {panStrategy}，接入 {panUplink}（应 false）");
        }

        // ── C. 重绑与冲突 ──────────────────────────────────────────────────────

        private static void CheckRebinding()
        {
            GameSettings.ResetKeyBindingsToDefault();
            var conflicts = new List<GameActionId>();

            // 不冲突：直接生效并落盘，重新读盘后仍在（存读档）。
            RebindResult ok = GameSettings.TryRebind(GameActionId.CommandGuard, new InputChord(KeyCode.Tab), conflicts);
            string json = PlayerPrefs.GetString(SettingsPrefsKey, string.Empty);
            GameSettings.Load();
            Expect(ok == RebindResult.Ok && GameSettings.KeyBindings.GetChord(GameActionId.CommandGuard).Key == KeyCode.Tab
                   && json.Contains("\"KeyBindingsFormat\":2"),
                "守备命令改到 Tab（Tab 只在接入里用，战略里不冲突）→ 直接生效、写盘（格式 2），重新读盘后仍是 Tab");

            // 冲突：R 已是移动命令（战略）。不落地，走确认框；取消 → 不变。
            GameSettings.ResetKeyBindingsToDefault();
            RebindResult conflict = GameSettings.TryRebind(GameActionId.CommandGuard, new InputChord(KeyCode.R), conflicts);
            Expect(conflict == RebindResult.Conflict && conflicts.SequenceEqual(new[] { GameActionId.CommandMove })
                   && GameSettings.KeyBindings.GetChord(GameActionId.CommandGuard).Key == KeyCode.G,
                $"守备改到 R：检出与“移动命令”冲突且不落地（冲突方：{string.Join(",", conflicts)}）");

            VisualElement overlay = MountUxml("UiKitOverlay.uxml", out GameObject overlayGo);
            try
            {
                UiConfirmDialog.BindView(overlay);
                RebindResult r1 = KeyBindingFlow.Rebind(GameActionId.CommandGuard, new InputChord(KeyCode.R), null);
                var scrim = overlay.Q<VisualElement>("ConfirmScrim");
                string lines = string.Join("｜", overlay.Q<VisualElement>("ConfirmLines").Query<Label>().ToList().Select(l => l.text));
                bool shown = UiConfirmDialog.IsOpen && !scrim.ClassListContains("uk-hidden") && InputRouter.ActiveContext == InputContext.Interface;
                UiConfirmDialog.Cancel();
                bool unchanged = GameSettings.KeyBindings.GetChord(GameActionId.CommandGuard).Key == KeyCode.G
                                 && GameSettings.KeyBindings.GetChord(GameActionId.CommandMove).Key == KeyCode.R;
                Expect(r1 == RebindResult.Conflict && shown && lines.Contains(KeyBindingFlow.ActionName(GameActionId.CommandMove)) && unchanged
                       && scrim.ClassListContains("uk-hidden") && KeyBindingFlow.LastFeedback == GameText.Get("input.rebind.cancelled"),
                    $"冲突 → 确认框写清冲突方（“{lines}”），输入切到界面上下文；选取消 → 两个键都不变、页脚写“已取消”");

                KeyBindingFlow.Rebind(GameActionId.CommandGuard, new InputChord(KeyCode.R), null);
                UiConfirmDialog.Confirm();
                bool guardR = GameSettings.KeyBindings.GetChord(GameActionId.CommandGuard).Key == KeyCode.R;
                bool moveUnbound = !GameSettings.KeyBindings.GetChord(GameActionId.CommandMove).IsBound;
                var reader = new FakeReader();
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                reader.Press(KeyCode.R);
                bool guardFires = InputRouter.ConsumeAction(GameActionId.CommandGuard, InputScope.Strategy);
                Frame(reader);
                reader.Press(KeyCode.R);
                bool moveFires = InputRouter.ConsumeAction(GameActionId.CommandMove, InputScope.Strategy);
                Frame(reader);
                Expect(guardR && moveUnbound && guardFires && !moveFires && InputDisplay.ForAction(GameActionId.CommandMove) == GameText.Get("input.key.none"),
                    "选覆盖 → 守备 = R，移动命令变为“未绑定”（明确显示，不静默回到默认）；按 R 只触发守备");

                // 恢复默认遇到冲突：移动命令的默认 R 已被守备占用 → 不落地并说明。
                RebindResult reset = GameSettings.ResetKeyBinding(GameActionId.CommandMove, conflicts);
                Expect(reset == RebindResult.Conflict && !GameSettings.KeyBindings.GetChord(GameActionId.CommandMove).IsBound,
                    "单个恢复默认时默认键已被占用 → 返回冲突、不落地（界面提示先改占用者）");

                // 必须保留按键：暂停改成 Esc → 会抢走“取消”，不给覆盖选项。
                GameSettings.ResetKeyBindingsToDefault();
                RebindResult required = KeyBindingFlow.Rebind(GameActionId.TogglePause, new InputChord(KeyCode.Escape), null);
                var blocked = new List<GameActionId>();
                RebindResult forced = GameSettings.ForceRebind(GameActionId.TogglePause, new InputChord(KeyCode.Escape), blocked);
                Expect(required == RebindResult.RequiredBlocked && !UiConfirmDialog.IsOpen && forced == RebindResult.RequiredBlocked
                       && blocked.Contains(GameActionId.Cancel) && GameSettings.KeyBindings.GetChord(GameActionId.Cancel).Key == KeyCode.Escape
                       && KeyBindingFlow.LastFeedback.Contains(KeyBindingFlow.ActionName(GameActionId.Cancel)),
                    "把暂停改成 Esc：“取消 / 返回”必须保留按键 → 不弹覆盖框、强制覆盖也被拒绝、说明原因");

                // 鼠标专用：主动作不能改成键盘键。
                RebindResult mouseOnly = KeyBindingFlow.Rebind(GameActionId.PrimaryAction, new InputChord(KeyCode.K), null);
                Expect(mouseOnly == RebindResult.Invalid && GameSettings.KeyBindings.GetChord(GameActionId.PrimaryAction).Key == KeyCode.Mouse0
                       && KeyBindingFlow.LastFeedback == GameText.Format("input.rebind.mouse_only", KeyBindingFlow.ActionName(GameActionId.PrimaryAction)),
                    "主动作改成键盘键 → 拒绝并说明“只能绑定鼠标按键”");

                // 按住类：Ctrl+W 与“镜头上移（按住 W）”冲突（按住 W 时再按 Ctrl+W 也会平移）。
                RebindResult hold = GameSettings.TryRebind(GameActionId.Paste, new InputChord(KeyCode.W, InputModifier.Ctrl), conflicts);
                Expect(hold == RebindResult.Conflict && conflicts.Contains(GameActionId.MoveForward), "粘贴改成 Ctrl+W → 与按住类的 W 平移冲突");

                // 全部恢复默认：需二次确认（会丢失改过的键），没改过时不弹。
                GameSettings.ResetKeyBindingsToDefault();
                GameSettings.TryRebind(GameActionId.Interact, new InputChord(KeyCode.G), conflicts);
                int customized = GameSettings.KeyBindings.CustomizedCount;
                GameSettings.ResetKeyBindingsToDefault();
                Expect(customized == 1 && GameSettings.KeyBindings.CustomizedCount == 0 && GameSettings.KeyBindings.FindAllConflicts().Count == 0,
                    "全部恢复默认后没有改动、没有冲突");
            }
            finally
            {
                UiConfirmDialog.ResetForTests();
                UiConfirmDialog.UnbindView();
                Object.DestroyImmediate(overlayGo);
                GameSettings.ResetKeyBindingsToDefault();
            }
        }

        // ── D. 旧设置迁移与按键采集 ─────────────────────────────────────────────

        private static void CheckMigrationAndCapture()
        {
            // ER2 旧格式：全表快照（没有 KeyBindingsFormat 字段）。交互改成 G（玩家改过，接入里不冲突 → 保留）；
            // 守备改成 1（玩家改过，但 1 在新默认里是 0.5x → 恢复默认）；任务日志 J 与编组 1 是 ER2 默认 → 让新默认生效。
            var legacy = new StringBuilder("{\"KeyBindings\":[");
            legacy.Append("{\"Action\":9,\"Key\":").Append((int)KeyCode.G).Append("},");
            legacy.Append("{\"Action\":34,\"Key\":").Append((int)KeyCode.Alpha1).Append("},");
            legacy.Append("{\"Action\":36,\"Key\":").Append((int)KeyCode.J).Append("},");
            legacy.Append("{\"Action\":20,\"Key\":").Append((int)KeyCode.Alpha1).Append("},");
            legacy.Append("{\"Action\":11,\"Key\":").Append((int)KeyCode.M).Append("}");
            legacy.Append("],\"UiScale\":1.2}");
            PlayerPrefs.SetString(SettingsPrefsKey, legacy.ToString());
            GameSettings.Load();
            InputBindingSet.LegacyMigrationReport report = GameSettings.PendingKeyMigration;
            InputBindingSet b = GameSettings.KeyBindings;
            string rewritten = PlayerPrefs.GetString(SettingsPrefsKey, string.Empty);
            Expect(report.Migrated && report.KeptCount == 1 && report.DroppedCount == 1
                   && b.GetChord(GameActionId.Interact).Key == KeyCode.G && b.GetChord(GameActionId.CommandGuard).Key == KeyCode.G
                   && b.GetChord(GameActionId.ToggleMissionLog).Key == KeyCode.L && b.GetChord(GameActionId.Group1) == new InputChord(KeyCode.Alpha1, InputModifier.Alt)
                   && b.GetChord(GameActionId.ToggleCameraView).Key == KeyCode.V && Mathf.Approximately(GameSettings.UiScale, 1.2f)
                   && rewritten.Contains("\"KeyBindingsFormat\":2") && b.FindAllConflicts().Count == 0,
                $"读 ER2 旧设置：保留 {report.KeptCount} 个玩家改动（交互=G）、{report.DroppedCount} 个与新默认冲突的恢复默认（守备=G），未改动的跟随新默认（任务日志 L、编组 Alt+1、接入 V），其它设置不丢，立即写回格式 2");

            // 真实顺序：主菜单阶段（没有战役、不在区域）→ 新建战役（Bind 重建历史）→ 进入区域。提示必须活到玩家进世界。
            NotificationCenter.ResetForTests();
            NotificationCenter.RegionProvider = null;
            CampaignSession.Clear();
            NotificationCenter.Tick();
            bool heldInMenu = GameSettings.PendingKeyMigration.Migrated && NotificationCenter.History.All(e => e.Type.Id != "settings_changed");
            CampaignState migState = CampaignState.CreateNew("fgux01-migrate", "Standard", 13579);
            CampaignSession.Set(0, migState);
            NotificationCenter.Tick(); // EnsureBound → Bind(state)：历史重建
            bool heldBeforeRegion = GameSettings.PendingKeyMigration.Migrated;
            NotificationCenter.RegionProvider = () => GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;
            NotificationCenter.Tick();
            NotificationEntry migrated = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "settings_changed");
            bool toast = NotificationCenter.Toasts.Any(e => e.Type.Id == "settings_changed");
            bool once = GameSettings.PendingKeyMigration.Migrated == false;
            NotificationCenter.Tick();
            Expect(heldInMenu && heldBeforeRegion && migrated != null && toast && migrated.Text.Contains("1") && once
                   && NotificationCenter.History.Count(e => e.Type.Id == "settings_changed") == 1,
                $"迁移提示在主菜单与新建战役时保持待发，进入区域后才以一条“设置变更”通知（历史 + 弹出条）告诉玩家，只发一次：“{migrated?.Text}”");
            NotificationCenter.RegionProvider = null;
            CampaignSession.Clear();
            GameSettings.Load();
            Expect(!GameSettings.PendingKeyMigration.Migrated, "写回后再次读盘不再迁移");

            // 从没改过键的 ER2 老玩家（全表快照都是 ER2 默认键）：没有保留 / 恢复，也要知道默认键变了。
            PlayerPrefs.SetString(SettingsPrefsKey, "{\"KeyBindings\":[{\"Action\":36,\"Key\":" + (int)KeyCode.J + "},{\"Action\":11,\"Key\":" + (int)KeyCode.M + "}]}");
            GameSettings.Load();
            InputBindingSet.LegacyMigrationReport plain = GameSettings.PendingKeyMigration;
            NotificationCenter.ResetForTests();
            CampaignSession.Set(0, CampaignState.CreateNew("fgux01-migrate2", "Standard", 13580));
            NotificationCenter.RegionProvider = () => GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;
            NotificationCenter.Tick();
            NotificationEntry plainNote = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "settings_changed");
            Expect(plain.Migrated && plain.KeptCount == 0 && plain.DroppedCount == 0 && plainNote != null
                   && plainNote.Text.Contains(GameText.Get("input.rebind.migrated_defaults")),
                $"没改过键的老玩家（保留 0、恢复 0）也收到默认键已更新的提示：“{plainNote?.Text}”");
            NotificationCenter.RegionProvider = null;
            CampaignSession.Clear();
            PlayerPrefs.DeleteKey(SettingsPrefsKey);
            GameSettings.Load();

            // 设置文件被手改出冲突（格式 2）：冲突的改动恢复默认。
            PlayerPrefs.SetString(SettingsPrefsKey, "{\"KeyBindingsFormat\":2,\"KeyBindings\":[{\"Action\":34,\"Key\":" + (int)KeyCode.R + ",\"Mods\":0}]}");
            GameSettings.Load();
            Expect(GameSettings.KeyBindings.FindAllConflicts().Count == 0 && GameSettings.PendingKeyMigration.DroppedCount == 1,
                "格式 2 的设置里有冲突（守备 = R 与移动命令撞键）→ 读入时恢复默认并计数，不让两件事抢同一个键");
            // 格式 2 被手改成把人锁死的值：主动作绑到键盘键、取消键未绑定 → 恢复默认并计数；普通动作未绑定是合法选择，保留。
            PlayerPrefs.SetString(SettingsPrefsKey, "{\"KeyBindingsFormat\":2,\"KeyBindings\":["
                + "{\"Action\":" + (int)GameActionId.PrimaryAction + ",\"Key\":" + (int)KeyCode.Space + ",\"Mods\":0},"
                + "{\"Action\":" + (int)GameActionId.Cancel + ",\"Key\":0,\"Mods\":0},"
                + "{\"Action\":" + (int)GameActionId.SpeedHalf + ",\"Key\":0,\"Mods\":0}]}");
            GameSettings.Load();
            InputBindingSet guarded = GameSettings.KeyBindings;
            Expect(guarded.GetChord(GameActionId.PrimaryAction) == InputActionCatalog.DefaultChord(GameActionId.PrimaryAction)
                   && guarded.GetChord(GameActionId.Cancel) == InputActionCatalog.DefaultChord(GameActionId.Cancel)
                   && !guarded.GetChord(GameActionId.SpeedHalf).IsBound && GameSettings.PendingKeyMigration.DroppedCount == 2,
                $"读设置校验（B11）：主动作 = 键盘键、取消 = 未绑定都恢复默认（计 {GameSettings.PendingKeyMigration.DroppedCount} 条），0.5x 速度未绑定是玩家的合法选择、保留");
            PlayerPrefs.DeleteKey(SettingsPrefsKey);
            GameSettings.Load();

            // 采集：Ctrl+Z、单独左 Shift（按下再松开）、滚轮、Esc 取消。
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            var capture = new InputCapture();
            reader.Hold(KeyCode.LeftControl);
            reader.Press(KeyCode.Z);
            InputCapture.Result r1 = capture.Poll(out InputChord c1);
            Frame(reader);
            reader.Press(KeyCode.LeftShift);
            InputCapture.Result r2a = capture.Poll(out _);
            Frame(reader);
            InputCapture.Result r2b = capture.Poll(out InputChord c2);
            Frame(reader);
            reader.Scroll = -2f;
            InputCapture.Result r3 = capture.Poll(out InputChord c3);
            Frame(reader);
            reader.Press(KeyCode.Escape);
            InputCapture.Result r4 = capture.Poll(out _);
            Frame(reader);
            Expect(r1 == InputCapture.Result.Captured && c1 == new InputChord(KeyCode.Z, InputModifier.Ctrl)
                   && r2a == InputCapture.Result.Waiting && r2b == InputCapture.Result.Captured && c2 == new InputChord(KeyCode.LeftShift)
                   && r3 == InputCapture.Result.Captured && c3.Key == VirtualKeys.WheelDown && r4 == InputCapture.Result.Cancelled,
                $"改键采集：Ctrl+Z→{c1}，单独左 Shift（松开后才定）→{c2}，滚轮下→{InputDisplay.Chord(c3)}，Esc→取消");
            Expect(InputDisplay.Chord(new InputChord(KeyCode.Alpha1, InputModifier.Ctrl | InputModifier.Alt)) == "Ctrl+Alt+1"
                   && InputDisplay.Chord(new InputChord(VirtualKeys.WheelUp)) == GameText.Get("input.key.wheel_up")
                   && InputDisplay.Chord(InputChord.Unbound) == GameText.Get("input.key.none"),
                "按键文字：Ctrl+Alt+1、滚轮上、未绑定都走文本键");
        }

        // ── E. 字面量按键扫描 ───────────────────────────────────────────────────

        private static void CheckNoLiteralKeys()
        {
            // 允许名单：旧细胞阶段（0.2 主菜单不可达，承接 FG14 迁移时整体下线）、开发调试键、输入层自身、改键采集。
            string[] allow =
            {
                "Core/InputRouter.cs", "Core/UnityInputReader.cs", "Core/InputDisplay.cs", "Core/IInputReader.cs",
                "Battle/SimStressTest.cs", "Battle/StressTestToggle.cs", "Battle/Feedback/WhiteboxAiHandoffOverlay.cs",
                "Command/SquadCommandSystem.cs", "Stage/CellStage/CellPlayerController.cs",
                "UI/Battle/", "UI/BattleHudToolkit/", "UI/BattleCarrierUIToolkit/", "UI/BattleGerminationUIToolkit/",
                "UI/BattleOverlayUIToolkit/", "UI/BattleSandboxUIToolkit/", "UI/Kit/UiControls.cs",
            };
            var keyRead = new Regex(@"(Input\.GetKey(Down|Up)?\s*\(\s*KeyCode\.|ConsumeKeyDown\s*\(\s*KeyCode\.|ConsumeGlobalKeyDown\s*\(\s*KeyCode\.|(Reader|InputRouter)\.GetKey\s*\(\s*KeyCode\.(?!(Left|Right)(Shift|Control|Alt)\b))");
            string root = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic").Replace('\\', '/');
            var hits = new List<string>();
            int scanned = 0;
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Replace('\\', '/').Substring(root.Length + 1);
                if (rel.StartsWith("FirstPlayable/", StringComparison.Ordinal) || allow.Any(a => rel.StartsWith(a, StringComparison.Ordinal)))
                {
                    continue;
                }
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (keyRead.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    {
                        hits.Add($"{rel}:{i + 1}");
                    }
                }
            }
            // 正则自测：写坏的正则会让扫描静默通过（记忆：扫描类自检要断言能扫出东西）。
            bool regexWorks = keyRead.IsMatch("if (Input.GetKeyDown(KeyCode.F11))") && keyRead.IsMatch("InputRouter.GetKey(KeyCode.A, InputScope.Strategy)")
                              && !keyRead.IsMatch("InputRouter.Reader.GetKey(KeyCode.LeftShift)");
            Expect(regexWorks && scanned > 150 && hits.Count == 0,
                $"正式流程代码（{scanned} 个文件，不含旧细胞阶段与调试键）不再用字面量读按键，全部走可重绑动作{(hits.Count == 0 ? string.Empty : "——" + string.Join("，", hits.Take(8)))}");
        }

        // ── F. 通知中心 ─────────────────────────────────────────────────────────

        private static float _clock;

        private static CampaignState NewCampaign()
        {
            CampaignState state = CampaignState.CreateNew("fgux01-selfcheck", "Standard", 24680);
            CampaignSession.Set(99, state);
            NotificationCenter.ResetForTests();
            _clock = 100f;
            NotificationCenter.Clock = () => _clock;
            NotificationCenter.Tick();
            return state;
        }

        private static void CheckNotifications()
        {
            CampaignState state = NewCampaign();
            int sounds0 = NotificationCenter.TierSoundCount;
            NotificationEntry urgent = NotificationCenter.Post("machine_destroyed", "#3", new Vector3(5f, 0f, 6f));
            NotificationEntry warn = NotificationCenter.Post("power_lost", "building.generator.name", new Vector3(1f, 0f, 2f));
            NotificationEntry info = NotificationCenter.Post("research_done", "x");
            Expect(urgent.Level == NotifyLevel.Urgent && warn.Level == NotifyLevel.Warning && info.Level == NotifyLevel.Info
                   && NotificationCenter.UnreadUrgent == 1 && NotificationCenter.TierSoundCount == sounds0 + 3,
                "三级：机器损失 = 紧急、缺电 = 警告、研究完成 = 信息；紧急计入未读；三个等级各响一次等级提示音");
            Expect(warn.Text == GameText.Format("notify.type.power_lost.single", GameText.Get("building.generator.name")),
                $"细节是文本键时按当前语言解析：“{warn.Text}”");

            // 聚合：窗口内同类合并成一条“N 处缺电”，成员逐条保留细节与位置。
            _clock += 1f;
            NotificationCenter.Post("power_lost", "#2", new Vector3(3f, 0f, 3f));
            _clock += 1f;
            NotificationEntry agg = NotificationCenter.Post("power_lost", "#3", null);
            Expect(ReferenceEquals(agg, warn) && agg.Count == 3 && agg.Members.Count == 3
                   && agg.Text == GameText.Format("notify.type.power_lost.many", "3") && NotificationCenter.History.Count == 3,
                $"聚合：6 秒窗口内 3 条缺电合并为一条“{agg.Text}”，成员 3 条，历史仍是 3 条");
            _clock += 7f;
            NotificationEntry fresh = NotificationCenter.Post("power_lost", "#4", null);
            Expect(!ReferenceEquals(fresh, warn) && fresh.Count == 1 && NotificationCenter.History.Count == 4, "超过聚合窗口后另起一条");

            // 等级提示音冷却：紧急 2 秒内再来一条不重复响。
            int s1 = NotificationCenter.TierSoundCount;
            NotificationCenter.Post("expedition_wiped", null);
            int s2 = NotificationCenter.TierSoundCount;
            _clock += 0.5f;
            NotificationCenter.Post("core_destroyed", null);
            int s3 = NotificationCenter.TierSoundCount;
            _clock += 2.1f;
            NotificationCenter.Post("failure", null);
            int s4 = NotificationCenter.TierSoundCount;
            Expect(s2 == s1 + 1 && s3 == s2 && s4 == s3 + 1 && NotificationCenter.LastTierSoundId == NotificationCatalog.Tier(NotifyLevel.Urgent).SfxId,
                "同一等级提示音 2 秒冷却：冷却内不重复响，过了冷却再响（FGR-UX-022）");

            // 筛选。
            var rows = new List<NotificationEntry>();
            NotificationCenter.Query(NotifyLevel.Urgent, null, rows);
            bool allUrgent = rows.Count == 4 && rows.All(e => e.Level == NotifyLevel.Urgent) && rows[0].Type.Id == "failure";
            NotificationCenter.Query(null, "power_lost", rows);
            Expect(allUrgent && rows.Count == 2 && rows.All(e => e.Type.Id == "power_lost"), "按等级筛出 4 条紧急（最新在前）；按类型筛出 2 条缺电");

            // 定位：接真实 GameRoot 定位逻辑（没有运行中的区域 → 明确原因），以及注入镜头验证坐标。
            var locateMethod = typeof(GameRoot).GetMethod("LocateForNotification", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            NotificationCenter.LocateHandler = (string region, Vector3 pos, out string key) =>
            {
                object[] args = { region, pos, null };
                bool ok = (bool)locateMethod.Invoke(null, args);
                key = (string)args[2];
                return ok;
            };
            bool locateReal = NotificationCenter.Locate(urgent, -1, out string realReason);
            NotificationEntry noPos = NotificationCenter.Post("objective_new", "x");
            bool locateNone = NotificationCenter.Locate(noPos, -1, out string noneReason);
            Vector3 flown = Vector3.zero;
            NotificationCenter.LocateHandler = (string region, Vector3 pos, out string key) =>
            {
                key = null;
                flown = pos;
                return true;
            };
            bool locateAgg = NotificationCenter.Locate(agg, 1, out _);
            Expect(!locateReal && realReason == "ui.notify.no_camera" && !locateNone && noneReason == "ui.notify.no_location"
                   && locateAgg && flown == new Vector3(3f, 0f, 3f),
                $"定位：没有运行区域时说明“{GameText.Get(realReason ?? string.Empty)}”；没有位置的通知说明“{GameText.Get(noneReason ?? string.Empty)}”；聚合里第 2 条成员飞到 {flown}");
            CheckRealCameraLocate();

            // 反馈时刻转通知：断电带位置进通知、开火（高频战斗音）不进；转来的不重复发等级提示音。
            int before = NotificationCenter.History.Count;
            int soundsBefore = NotificationCenter.TierSoundCount;
            _clock += 20f;
            FeedbackCues.RaiseAt(FeedbackCueId.PowerLost, new Vector3(9f, 0f, 8f), "发电机");
            FeedbackCues.Raise(FeedbackCueId.WeaponFire);
            FeedbackCues.Raise(FeedbackCueId.EnemyHit);
            NotificationEntry fromCue = NotificationCenter.History.Last();
            Expect(NotificationCenter.History.Count == before + 1 && fromCue.Type.Id == "power_lost" && fromCue.Latest.HasLocation
                   && fromCue.Latest.Location == new Vector3(9f, 0f, 8f) && NotificationCenter.TierSoundCount == soundsBefore,
                "反馈时刻“断电”自动进通知（带位置，可定位），开火 / 命中不进；由反馈时刻转来的不再重复发等级提示音");

            // 1 秒内 50 条：聚合成 5 条，弹出条不超过上限，不卡。
            NewCampaign();
            var sw = Stopwatch.StartNew();
            UiKitGalleryUIToolkit.PostBurst(); // 同一时刻（1 秒内）连发 50 条
            sw.Stop();
            int max = UiTuningValues.GetInt("notify.toast_max_visible");
            Expect(NotificationCenter.History.Count == 5 && NotificationCenter.History.All(e => e.Count == 10)
                   && NotificationCenter.Toasts.Count <= max && sw.Elapsed.TotalMilliseconds < 50,
                $"1 秒内 50 条（5 类 × 10）→ 历史 {NotificationCenter.History.Count} 条、每条聚合 10 次，弹出条 {NotificationCenter.Toasts.Count}（上限 {max}），耗时 {sw.Elapsed.TotalMilliseconds:F2} ms");

            // 弹出条优先级：紧急不会被信息挤掉。
            NewCampaign();
            string[] urgentTypes = { "machine_destroyed", "expedition_wiped", "failure", "core_destroyed", "boss_lockout" };
            foreach (string t in urgentTypes)
            {
                _clock += 0.1f;
                NotificationCenter.Post(t, "u");
            }
            _clock += 0.1f;
            NotificationCenter.Post("research_done", "i");
            bool infoHidden = NotificationCenter.Toasts.All(e => e.Level == NotifyLevel.Urgent) && NotificationCenter.Toasts.Count == max;
            _clock += 0.1f;
            NotificationCenter.Post("raid_arrival", "u6");
            Expect(infoHidden && NotificationCenter.Toasts[0].Type.Id == "raid_arrival" && NotificationCenter.Toasts.All(e => e.Type.Id != "machine_destroyed")
                   && NotificationCenter.History.Any(e => e.Type.Id == "research_done"),
                "弹出条满 5 条紧急时，新的信息通知不挤掉紧急（仍进历史）；新的紧急挤掉最早的紧急");

            // 弹出条按真实时间过期，与暂停和 0.5x～3x 倍速无关（B09）。
            NewCampaign();
            NotificationEntry t0 = NotificationCenter.Post("power_lost", "p");
            float dur = t0.ToastExpiresAt - t0.ToastShownAt;
            var durations = new List<float>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                StrategyClock.SetSpeed(speed);
                _clock += 60f;
                NotificationEntry e = NotificationCenter.Post("storage_full", "s" + speed);
                durations.Add(e.ToastExpiresAt - e.ToastShownAt);
            }
            StrategyClock.Reset();
            _clock += 0.5f;
            NotificationCenter.Tick();
            bool stillShown = NotificationCenter.Toasts.Count > 0;
            _clock += 100f;
            NotificationCenter.Tick();
            float expected = NotificationCatalog.Tier(NotifyLevel.Warning).ToastSeconds * GameSettings.NotificationToastScale;
            Expect(Mathf.Approximately(dur, expected) && durations.All(d => Mathf.Approximately(d, expected)) && stillShown && NotificationCenter.Toasts.Count == 0,
                $"弹出条停留 {expected:0.#} 秒（真实时间），在 0.5x / 1x / 2x / 3x 档与暂停下都一样，到时自动消失");
            GameSettings.SetNotificationToastScale(2f);
            NotificationEntry longer = NotificationCenter.Post("signal_lost", "x");
            GameSettings.SetNotificationToastScale(1f);
            Expect(Mathf.Approximately(longer.ToastExpiresAt - longer.ToastShownAt, expected * 2f), "设置“通知停留时长”×2 → 停留时间翻倍（FGR-UX-060）");

            // 自动暂停：首领阶段转换默认勾选；缺电默认不勾；玩家可以改。
            NewCampaign();
            int paused = 0;
            NotificationCenter.AutoPauseHandler = () => { paused++; return true; };
            NotificationCenter.Post("boss_phase", "2");
            NotificationEntry reason = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "auto_paused");
            NotificationCenter.Post("power_lost", "x");
            GameSettings.SetNotifyAutoPause("boss_phase", false);
            _clock += 10f;
            NotificationCenter.Post("boss_phase", "3");
            GameSettings.SetNotifyAutoPause("power_lost", true);
            NotificationCenter.Post("power_lost", "y");
            int pausedTotal = paused;
            GameSettings.SetNotifyAutoPause("boss_phase", true);
            GameSettings.SetNotifyAutoPause("power_lost", false);
            Expect(pausedTotal == 2 && NotificationCenter.AutoPauseCount == 2 && reason != null
                   && reason.Text.Contains(GameText.Get("notify.type.boss_phase.name")) && !string.IsNullOrEmpty(reason.SourceText),
                $"自动暂停：首领阶段转换默认暂停并留下可追溯说明“{reason?.Text}”；缺电默认不暂停；玩家取消 / 勾选后按玩家设置（共暂停 {pausedTotal} 次）");

            // “尚未开放”的动作：按下时给出说明，不静默（IC-REQ-013）。
            NewCampaign();
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            // FG0-ARCH-04 起建造菜单（B）已接入玩法，改用仍未开放的快捷栏 1（F1，FG3-LOG-01 承接）验证“尚未开放”提示。
            reader.Press(KeyCode.F1);
            UiKitInputPump.ProcessWorldKeys();
            Frame(reader);
            InputActionCatalog.TryGet(GameActionId.Hotbar1, out InputActionDef hotbar1);
            NotificationEntry locked = NotificationCenter.Toasts.FirstOrDefault(e => e.Type.Id == "feature_locked");
            string lockedText = locked?.Text;
            bool notInHistory = NotificationCenter.History.All(e => e.Type.Id != "feature_locked");
            _clock += 10f;
            reader.Press(KeyCode.Alpha3);
            UiKitInputPump.ProcessWorldKeys();
            Frame(reader);
            float speed2 = StrategyClock.SpeedMultiplier;
            reader.Press(KeyCode.Alpha4);
            UiKitInputPump.ProcessWorldKeys();
            Frame(reader);
            float speedAfter4 = StrategyClock.SpeedMultiplier;
            StrategyClock.Reset();
            Expect(locked != null && hotbar1.Status == InputActionStatus.Reserved && lockedText.Contains(GameText.Get(hotbar1.NameKey)) && notInHistory
                   && Mathf.Approximately(speed2, 2f) && Mathf.Approximately(speedAfter4, 2f)
                   && NotificationCenter.Toasts.Any(e => e.Type.Id == "feature_locked" && e.Text.Contains(GameText.Get("input.action.speed_triple.name"))),
                $"按 F1（快捷栏 1，后续开放）→ 弹出“{lockedText}”（只弹出、不进历史）；按 3 → 2x；按 4（3x，FG7-ENV-01 承接）→ 速度不变并提示尚未开放");
        }

        // ── G. 存读档 ─────────────────────────────────────────────────────────

        private static void CheckNotificationSave()
        {
            CampaignState state = NewCampaign();
            state.SaveHistory.Notices = new[]
            {
                new SaveNoticeRecord { NoticeId = "n-a", TextKey = "save.notice.content_removed", Args = new[] { "save.notice.unknown_content", "2", "10" }, CreatedAtUtc = "2026-09-25T01:00:00Z" },
                new SaveNoticeRecord { NoticeId = "n-b", TextKey = "save.notice.craft_cancelled", Args = new[] { "1" }, CreatedAtUtc = "2026-09-25T01:00:01Z" },
            };
            NotificationCenter.Bind(state);
            int imported = NotificationCenter.History.Count(e => e.Type.Id == "save_migrated");
            NotificationCenter.Post("machine_destroyed", "#1", new Vector3(1f, 2f, 3f));
            _clock += 1f;
            NotificationCenter.Post("machine_destroyed", "#2", new Vector3(4f, 5f, 6f));
            NotificationCenter.Post("feature_locked", "input.action.open_build_menu.name");
            string[] texts = NotificationCenter.History.Select(e => e.Text).ToArray();
            long maxId = NotificationCenter.History.Max(e => e.Id);

            SaveResult saved = CampaignSaveService.Save(0, state, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(0);
            Expect(saved.Success && loaded.Success, $"真实存档服务写入 / 读回（临时目录）：{saved.Message}{loaded.Message}");
            if (!loaded.Success)
            {
                return;
            }
            CampaignSession.Set(0, loaded.State);
            NotificationCenter.Tick();
            string[] reloaded = NotificationCenter.History.Select(e => e.Text).ToArray();
            NotificationEntry agg = NotificationCenter.History.FirstOrDefault(e => e.Type.Id == "machine_destroyed");
            NotificationEntry next = NotificationCenter.Post("research_done", "z");
            Expect(imported == 2 && reloaded.SequenceEqual(texts) && agg != null && agg.Count == 2 && agg.Members[0].Location == new Vector3(1f, 2f, 3f)
                   && texts.Length == 3 && next.Id > maxId && NotificationCenter.History.All(e => e.Type.Id != "feature_locked"),
                $"读档变更 2 条转进历史；历史 {texts.Length} 条（“尚未开放”这类提示不进历史）逐条往返一致、聚合次数与成员位置不丢；读档后新通知编号继续往后（{next.Id} > {maxId}）");

            NotificationCenter.Bind(loaded.State);
            Expect(NotificationCenter.History.Count(e => e.Type.Id == "save_migrated") == 2, "同一条读档变更再次绑定不会重复转入（去重表进存档）");

            // 换语言后回看：只存类型与参数，正文按新语言渲染。
            GameSettings.SetLanguage(GameLanguage.En);
            string en = agg.Text;
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en.Contains("machines lost") && en != agg.Text, $"切到英文后历史正文随语言变化：“{en}” / “{agg.Text}”");

            // 历史上限：超出丢最旧，存档同步截断。
            var tuning = ConfigSystem.Instance.Tables.TbUiTuning.DataList.ToDictionary(r => r.Id, r => r.Value);
            tuning["notify.history_capacity"] = 10f;
            UiTuningValues.OverrideForTests(tuning);
            CampaignState small = NewCampaign();
            for (int i = 0; i < 15; i++)
            {
                _clock += 100f;
                NotificationCenter.Post("research_done", "r" + i);
            }
            UiTuningValues.ResetForTests();
            Expect(NotificationCenter.History.Count == 10 && small.Notifications.Entries.Length == 10
                   && NotificationCenter.History[0].Latest.Detail == "r5",
                $"历史上限 10（注入）：发 15 条后保留最新 10 条，存档里也只有 10 条（最旧一条是 {NotificationCenter.History[0].Latest.Detail}）");
        }

        // ── H. 浮层基础件 ───────────────────────────────────────────────────────

        private static void CheckOverlayWidgets()
        {
            VisualElement overlay = MountUxml("UiKitOverlay.uxml", out GameObject go);
            VisualElement page = MountUxml("UiKitGallery.uxml", out GameObject pageGo);
            var gallery = pageGo.AddComponent<UiKitGalleryUIToolkit>();
            try
            {
                UiConfirmDialog.BindView(overlay);
                UiTooltip.BindView(overlay);
                UiContextMenu.BindView(overlay);
                UiDragDrop.BindView(overlay);
                gallery.BindView(page);
                page.Q<VisualElement>("GalleryRoot").RemoveFromClassList("uk-hidden");
                UiToolkitLayoutProbe.ForceLayout(page);

                // 确认框：排队、回车确认、Esc 取消、遮罩取消。
                var log = new List<string>();
                UiConfirmDialog.Show(new ConfirmRequest { Title = "A", OnConfirm = () => log.Add("A+"), OnCancel = () => log.Add("A-") });
                var second = new ConfirmRequest { Title = "B", Irreversible = true, OnConfirm = () => log.Add("B+"), OnCancel = () => log.Add("B-") };
                second.Consequences.Add(GameText.Format("ui.gallery.confirm_line", "7"));
                UiConfirmDialog.Show(second);
                bool queued = UiConfirmDialog.PendingCount == 1 && overlay.Q<Label>("ConfirmTitle").text == "A";
                var reader = new FakeReader();
                InputRouter.DebugSetReader(reader);
                reader.Press(KeyCode.Return);
                UiConfirmDialog.Tick();
                Frame(reader);
                bool secondShown = UiConfirmDialog.IsOpen && overlay.Q<Label>("ConfirmTitle").text == "B"
                                   && !overlay.Q<Label>("ConfirmIrreversible").ClassListContains("uk-hidden")
                                   && overlay.Q<VisualElement>("ConfirmConsequences").Query<Label>().ToList().Count == 1
                                   && overlay.Q<VisualElement>("ConfirmDialog").ClassListContains("uk-confirm-danger");
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(queued && secondShown && log.SequenceEqual(new[] { "A+", "B-" }) && !UiConfirmDialog.IsOpen
                       && InputRouter.ActiveContext != InputContext.Interface && UiEscapeStack.Count == 0,
                    $"确认框：第二个请求排队；回车确认第一个；第二个写明“将丢失”与“无法撤销”；Esc 取消（{string.Join(",", log)}），关闭后释放模态");

                // 悬停提示：0.4 秒真实时间后出现；数值来源可展开；按住固定键固定；松开并离开后宽限期后消失。
                float t = 50f;
                UiTooltip.Clock = () => t;
                bool pin = false;
                UiTooltip.PinHeld = () => pin;
                VisualElement target = gallery.TooltipTarget;
                UiTooltip.NotifyEnter(target);
                t += 0.3f;
                UiTooltip.Tick();
                bool early = UiTooltip.IsVisible;
                t += 0.11f;
                UiTooltip.Tick();
                var view = overlay.Q<VisualElement>("Tooltip");
                bool shown = UiTooltip.IsVisible && !view.ClassListContains("uk-hidden") && UiTooltip.Content.Sources.Count == 3
                             && overlay.Q<VisualElement>("TooltipBreakdown").ClassListContains("uk-hidden");
                string shortcut = overlay.Q<Label>("TooltipShortcut").text;
                UiTooltip.ToggleExpanded();
                bool expanded = !overlay.Q<VisualElement>("TooltipBreakdown").ClassListContains("uk-hidden")
                                && overlay.Q<VisualElement>("TooltipBreakdown").Query(className: "uk-breakdown-row").ToList().Count == 3;
                UiTooltip.ToggleExpanded();
                pin = true;
                UiTooltip.Tick();
                UiTooltip.NotifyLeave(target);
                t += 5f;
                UiTooltip.Tick();
                bool pinnedStays = UiTooltip.IsVisible && UiTooltip.IsPinned && view.ClassListContains("uk-tooltip-pinned")
                                   && !overlay.Q<VisualElement>("TooltipBreakdown").ClassListContains("uk-hidden");
                pin = false;
                UiTooltip.Tick();
                bool unpinHides = !UiTooltip.IsVisible; // 鼠标早已离开：一松开固定键就收起
                UiTooltip.NotifyEnter(target);
                t += 0.5f;
                UiTooltip.Tick();
                UiTooltip.NotifyLeave(target);
                t += 0.1f;
                UiTooltip.Tick();
                bool graceKeeps = UiTooltip.IsVisible; // 离开后宽限期内还在（给鼠标移到提示上点按钮的时间）
                t += UiTuningValues.Get("tooltip.hide_grace_seconds");
                UiTooltip.Tick();
                Expect(!early && shown && expanded && pinnedStays && unpinHides && graceKeeps && !UiTooltip.IsVisible
                       && shortcut == GameText.Format("ui.common.shortcut", InputDisplay.ForAction(GameActionId.ToggleNotificationCenter)),
                    $"悬停提示：0.3 秒不出现、0.41 秒出现；来源默认收起、点“展开来源”列出 3 项；按住固定键后移开也不消失且自动展开、松开即收起；普通离开后宽限期内仍在、之后消失；快捷键行“{shortcut}”");
                GameSettings.TryRebind(GameActionId.ToggleNotificationCenter, new InputChord(KeyCode.F12), new List<GameActionId>());
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                string rebound = overlay.Q<Label>("TooltipShortcut").text;
                GameSettings.ResetKeyBindingsToDefault();
                UiTooltip.Hide();
                Expect(rebound.Contains("F12"), $"改键后提示里的快捷键跟着变：“{rebound}”");

                // 右键菜单：不可用项写原因、点了不执行；点可用项执行并关闭；Esc 关闭。
                UiContextMenu.Show(new Vector2(200f, 200f), new List<ContextMenuItem>
                {
                    new ContextMenuItem("a", () => log.Add("menu-a")),
                    ContextMenuItem.Disabled("b", GameText.Get("ui.gallery.menu_demolish_reason")),
                });
                var buttons = overlay.Q<VisualElement>("ContextMenu").Query<Button>().ToList();
                bool disabledShown = buttons.Count == 2 && !buttons[1].enabledSelf && buttons[1].text.Contains(GameText.Get("ui.gallery.menu_demolish_reason"));
                bool disabledRuns = UiContextMenu.Invoke(1);
                bool stillOpen = UiContextMenu.IsOpen;
                bool ran = UiContextMenu.Invoke(0);
                bool closed = !UiContextMenu.IsOpen && overlay.Q<VisualElement>("ContextMenu").ClassListContains("uk-hidden");
                UiContextMenu.Show(Vector2.zero, new List<ContextMenuItem> { new ContextMenuItem("c", null) });
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(disabledShown && !disabledRuns && stillOpen && ran && closed && log.Contains("menu-a") && !UiContextMenu.IsOpen,
                    "右键菜单：不可用项置灰并写明原因、点了不执行；点可用项执行并关闭；Esc 关闭");

                // 拖放：锁定槽拒绝并说明原因；可放槽落下；Esc 取消拖动并释放指针。
                UiDragDrop.Begin(gallery.DragSource, 3);
                bool ghost = !overlay.Q<VisualElement>("DragGhost").ClassListContains("uk-hidden") && InputRouter.UiPointerCaptured;
                UiDragDrop.MoveTo(new Vector2(10f, 10f), gallery.DropLocked);
                bool redHover = gallery.DropLocked.ClassListContains("uk-drop-bad");
                bool droppedLocked = UiDragDrop.Drop(gallery.DropLocked);
                string lockedReason = UiDragDrop.LastResult;
                UiDragDrop.Begin(gallery.DragSource, 3);
                UiDragDrop.MoveTo(new Vector2(10f, 10f), gallery.DropA);
                bool greenHover = gallery.DropA.ClassListContains("uk-drop-ok");
                bool droppedA = UiDragDrop.Drop(gallery.DropA);
                UiDragDrop.Begin(gallery.DragSource, 3);
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(ghost && redHover && !droppedLocked && lockedReason == GameText.Get("ui.gallery.drag_locked_reason")
                       && greenHover && droppedA && gallery.LastDropped == ((Label)gallery.DropA).text
                       && !UiDragDrop.IsDragging && !InputRouter.UiPointerCaptured && UiDragDrop.LastResult == GameText.Get("ui.drag.cancelled"),
                    $"拖放：拖动时独占指针、显示拖影；锁定槽标红并拒绝（“{lockedReason}”）；可放槽标绿并落下；Esc 取消且释放指针");

                // Esc 逐层返回：关最上层；空栈且不在世界里时什么都不做（不会误开暂停菜单）。
                var closedOrder = new List<string>();
                UiEscapeStack.Push("x", () => closedOrder.Add("x"));
                UiEscapeStack.Push("y", () => closedOrder.Add("y"));
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                int frameBefore = UiKitInputPump.LastPauseMenuFrame;
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(closedOrder.SequenceEqual(new[] { "y", "x" }) && UiKitInputPump.LastPauseMenuFrame == frameBefore,
                    "Esc 逐层返回：先关最后打开的一层，再关下一层；不在游戏世界里时不打开暂停菜单（FGR-UX-001）");
            }
            finally
            {
                UiTooltip.ResetForTests();
                UiConfirmDialog.UnbindView();
                UiTooltip.UnbindView();
                UiContextMenu.UnbindView();
                UiDragDrop.UnbindView();
                GameSettings.ResetKeyBindingsToDefault();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(pageGo);
            }
        }

        // ── I. 样例页基础件 ─────────────────────────────────────────────────────

        private static void CheckGalleryWidgets()
        {
            VisualElement page = MountUxml("UiKitGallery.uxml", out GameObject pageGo);
            var gallery = pageGo.AddComponent<UiKitGalleryUIToolkit>();
            try
            {
                gallery.BindView(page);
                page.Q<VisualElement>("GalleryRoot").RemoveFromClassList("uk-hidden");
                UiToolkitLayoutProbe.ForceLayout(page);

                // 虚拟化列表：1 万条只建可见行。
                int realized = gallery.List.RealizedRowCount;
                Expect(gallery.ShownItems.Count == UiKitGalleryUIToolkit.ListSize && realized > 0 && realized < 60,
                    $"虚拟化列表：{gallery.ShownItems.Count} 条数据只建了 {realized} 个行元素（FGR-UX-070）");

                // 搜索框：过滤、空结果状态、清空。
                gallery.Search.SetText("9999");
                int one = gallery.ShownItems.Count;
                gallery.Search.SetText("没有这个");
                Label empty = page.Q<Label>("GalleryListEmpty");
                bool emptyShown = gallery.ShownItems.Count == 0 && !empty.ClassListContains("uk-hidden") && empty.text.Contains("没有这个");
                gallery.Search.SetText(string.Empty);
                Expect(one == 1 && emptyShown && gallery.ShownItems.Count == UiKitGalleryUIToolkit.ListSize
                       && page.Q<Label>("GallerySearchPlaceholder").ClassListContains("uk-hidden") == false,
                    "搜索框：输入“9999”只剩 1 条；无结果时显示“没有匹配……”的空状态；清空后恢复 1 万条并重新显示占位提示");

                // 搜索框焦点吞键的真实事件路径（FocusIn / FocusOut 需要运行时焦点控制器）在 Play 冒烟里验证：
                // 冒烟打开按键面板、让搜索框获得焦点后按快捷键，断言快捷键没有生效。

                // 标签页。
                gallery.Tabs.Select(2);
                var tabButtons = new[] { page.Q<Button>("GalleryTabA"), page.Q<Button>("GalleryTabB"), page.Q<Button>("GalleryTabC") };
                Expect(gallery.Tabs.Selected == 2 && tabButtons[2].ClassListContains("uk-tab-selected") && !tabButtons[0].ClassListContains("uk-tab-selected")
                       && page.Q<Label>("GalleryTabBody").text.Contains(GameText.Get("ui.gallery.tab_c")),
                    "标签页：选中第 3 页，只有它是选中样式，正文跟着切换");

                // 图表。
                UiLineChart line = gallery.LineChart;
                var rect = new Rect(0, 0, 100, 50);
                Vector2 p0 = line.PointAt(0, rect);
                Vector2 pLast = line.PointAt(line.Values.Count - 1, rect);
                int cap = UiTuningValues.GetInt("chart.max_points");
                for (int i = 0; i < cap + 20; i++)
                {
                    line.Push(i);
                }
                Expect(Mathf.Approximately(p0.x, 0f) && Mathf.Approximately(pLast.x, 100f) && line.Values.Count == cap && Mathf.Approximately(line.Max, cap + 19),
                    $"折线图：首点在左、末点在右；超过 {cap} 个点丢最旧（现在 {line.Values.Count} 个，最大 {line.Max}）");
                UiToolkitLayoutProbe.ForceLayout(page);
                var bars = gallery.BarChart.Bars;
                float h0 = bars[0].resolvedStyle.height, h1 = bars[1].resolvedStyle.height, h2 = bars[2].resolvedStyle.height;
                Expect(bars.Count == 3 && h0 > h2 && h2 > h1 && h1 > 0f && Mathf.Abs(h1 / h0 - 12f / 30f) < 0.05f,
                    $"柱状图：3 根柱子高度按数值比例（{h0:F0} / {h1:F0} / {h2:F0}）");

                // 快捷键提示：跟随改键；未绑定时显示“未绑定”并换警示样式。
                Label hint = page.Q<Label>("GalleryHintPause");
                string before = hint.text;
                GameSettings.ForceRebind(GameActionId.TogglePause, new InputChord(KeyCode.P));
                UiShortcutHint.RefreshIfChanged();
                string after = hint.text;
                GameSettings.ForceRebind(GameActionId.CommandAttack, new InputChord(KeyCode.P));
                UiShortcutHint.RefreshIfChanged();
                string unbound = hint.text;
                bool warn = hint.ClassListContains("uk-keycap-unbound");
                GameSettings.ResetKeyBindingsToDefault();
                UiShortcutHint.RefreshIfChanged();
                Expect(before == GameText.Get("input.key.space") && after == "P" && unbound == GameText.Get("input.key.none") && warn && hint.text == before,
                    $"快捷键提示：“{before}”→ 改键后“{after}”→ 被别的动作抢走后“{unbound}”（警示样式）→ 恢复默认");

                // 进度条。
                UiProgressBar bar = gallery.Progress;
                bar.Set(0.5f);
                VisualElement fill = page.Q<VisualElement>("GalleryProgress").Q(className: "uk-progress-fill");
                bool half = Mathf.Approximately(fill.style.width.value.value, 50f) && page.Q<VisualElement>("GalleryProgress").Q<Label>(className: "uk-progress-label").text == UiFormat.Percent(0.5);
                bar.Set(0.5f, true, GameText.Get("status.no_power.name"));
                bool blocked = page.Q<VisualElement>("GalleryProgress").ClassListContains("uk-progress-blocked")
                               && page.Q<VisualElement>("GalleryProgress").Q<Label>(className: "uk-progress-label").text == GameText.Get("status.no_power.name");
                Expect(half && blocked, "进度条：50% 时填充一半并显示“50%”；受阻时换样式并用文字写原因（不只靠颜色）");

                // 状态图标：10 种状态形状两两不同；去掉颜色（灰度）后按实际布局尺寸 / 旋转仍能区分（FGT-UX-007 基础版）。
                page.Q<VisualElement>("GalleryStatusRow").AddToClassList("uk-grayscale");
                UiToolkitLayoutProbe.ForceLayout(page);
                var signatures = new List<string>();
                var names = new List<string>();
                for (int i = 0; i < UiStatusIcon.StatusCount; i++)
                {
                    VisualElement host = page.Q<VisualElement>("GalleryStatus" + i);
                    signatures.Add(ShapeSignature(host));
                    names.Add(page.Q<Label>("GalleryStatusName" + i).text);
                }
                bool colorsGone = page.Q<VisualElement>("GalleryStatus0").Q(className: UiStatusIcon.ShapeA).resolvedStyle.backgroundColor
                                  == page.Q<VisualElement>("GalleryStatus8").Q(className: UiStatusIcon.ShapeA).resolvedStyle.backgroundColor;
                Expect(signatures.Distinct().Count() == UiStatusIcon.StatusCount && colorsGone && names.All(n => !GameText.ContainsMarker(n))
                       && ((UiEntityStatus[])Enum.GetValues(typeof(UiEntityStatus))).Select(UiStatusIcon.ShapeOf).Distinct().Count() == UiStatusIcon.StatusCount,
                    $"状态图标：{UiStatusIcon.StatusCount} 种状态（{string.Join("、", names)}）在灰度下按形状仍两两可分");

                // 数字格式（FGR-UX-004）。
                string zh = UiFormat.Number(12345) + "|" + UiFormat.Number(9999) + "|" + UiFormat.PerMinute(12);
                GameSettings.SetLanguage(GameLanguage.En);
                string en = UiFormat.Number(12345) + "|" + UiFormat.Number(2_500_000) + "|" + UiFormat.PerMinute(12);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(zh == "1.2万|9999|12/分钟" && en == "12.3k|2.5M|12/min", $"数字格式：中文“{zh}”，英文“{en}”");
            }
            finally
            {
                GameSettings.ResetKeyBindingsToDefault();
                Object.DestroyImmediate(pageGo);
            }
        }

        private static string ShapeSignature(VisualElement host)
        {
            var sb = new StringBuilder();
            foreach (string cls in new[] { UiStatusIcon.ShapeA, UiStatusIcon.ShapeB })
            {
                VisualElement e = host.Q(className: cls);
                IResolvedStyle s = e.resolvedStyle;
                sb.Append(s.display).Append(':').Append(Mathf.Round(s.width)).Append('x').Append(Mathf.Round(s.height))
                    .Append(" b").Append(Mathf.Round(s.borderLeftWidth)).Append(" r").Append(Mathf.Round(s.rotate.angle.value))
                    .Append(" a").Append(s.backgroundColor.a > 0.5f ? 1 : 0).Append(" rad").Append(Mathf.Round(s.borderTopLeftRadius)).Append(';');
            }
            return sb.ToString();
        }

        // ── J. 面板 ────────────────────────────────────────────────────────────

        private static void CheckPanels()
        {
            VisualElement overlay = MountUxml("UiKitOverlay.uxml", out GameObject overlayGo);
            VisualElement keys = MountUxml("KeyBindingsPanel.uxml", out GameObject keysGo);
            VisualElement hud = MountUxml("NotificationHud.uxml", out GameObject hudGo);
            var panel = keysGo.AddComponent<KeyBindingsPanelUIToolkit>();
            var notify = hudGo.AddComponent<NotificationHudUIToolkit>();
            try
            {
                UiConfirmDialog.BindView(overlay);
                panel.BindView(keys);
                panel.SetOpen(true);
                UiToolkitLayoutProbe.ForceLayout(keys);
                bool open = KeyBindingsPanelUIToolkit.IsOpen && !keys.Q<VisualElement>("KeyBindingsRoot").ClassListContains("uk-hidden")
                            && InputRouter.ActiveContext == InputContext.Interface && UiEscapeStack.Top == (object)panel;
                int allRows = panel.VisibleRows.Count;

                var build = new List<InputActionDef>();
                KeyBindingsPanelUIToolkit.Filter(InputContext.Build, null, build);
                var uplink = new List<InputActionDef>();
                KeyBindingsPanelUIToolkit.Filter(InputContext.Uplink, null, uplink);
                var search = new List<InputActionDef>();
                KeyBindingsPanelUIToolkit.Filter(InputContext.None, "F5", search);
                Expect(open && allRows == InputActionCatalog.All.Count && build.Any(d => d.Action == GameActionId.Rotate) && build.All(d => d.Action != GameActionId.CommandMove)
                       && uplink.Any(d => d.Action == GameActionId.Interact) && uplink.All(d => d.Action != GameActionId.OpenBuildMenu)
                       && search.Select(d => d.Action).OrderBy(a => a).SequenceEqual(new[] { GameActionId.Hotbar5, GameActionId.QuickSave }),
                    $"按键面板：打开为模态（界面上下文、Esc 栈顶）；全部页 {allRows} 行；建造页有“旋转”无“移动命令”；接入页有“交互”无“建造菜单”；搜“F5”得到快捷栏 5 与快速存档");

                // 通过面板改键：点按键 → 采集 → 冲突确认 → 覆盖。
                var reader = new FakeReader();
                InputRouter.DebugSetReader(reader);
                panel.StartListening(GameActionId.CommandGuard);
                bool suppressed = InputRouter.KeyboardSuppressed;
                reader.Press(KeyCode.T);
                panel.PollCapture();
                Frame(reader);
                bool confirmOpen = UiConfirmDialog.IsOpen;
                UiConfirmDialog.Confirm();
                bool applied = GameSettings.KeyBindings.GetChord(GameActionId.CommandGuard).Key == KeyCode.T
                               && !GameSettings.KeyBindings.GetChord(GameActionId.CommandAttack).IsBound;
                panel.ResetOne(GameActionId.CommandGuard);
                panel.ResetOne(GameActionId.CommandAttack);
                Expect(suppressed && confirmOpen && applied && GameSettings.KeyBindings.CustomizedCount == 0,
                    "按键面板：点按键进入采集（期间快捷键让位）→ 按 T 与“攻击命令”冲突 → 确认框选覆盖 → 生效；逐个恢复默认");

                // 采集中按 Esc：只取消改键，不关面板。
                panel.StartListening(GameActionId.CommandGuard);
                reader.Press(KeyCode.Escape);
                panel.PollCapture();
                UiKitInputPump.Process();
                Frame(reader);
                Expect(KeyBindingsPanelUIToolkit.IsOpen && KeyBindingsPanelUIToolkit.Listening == null && KeyBindingFlow.LastFeedback == GameText.Get("input.rebind.cancelled"),
                    "改键等待中按 Esc：只取消这次改键，按键面板保持打开");
                InputRouter.Reset();
                InputRouter.DebugSetReader(reader);
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(!KeyBindingsPanelUIToolkit.IsOpen && UiEscapeStack.Count == 0 && InputRouter.ActiveContext != InputContext.Interface,
                    "再按 Esc：关闭按键面板并释放模态");

                // 通知中心界面：弹出条、筛选、展开成员、点弹出条定位。
                NewCampaign();
                notify.BindView(hud);
                UiKitGalleryUIToolkit.PostBurst();
                notify.RenderToasts();
                int toastCount = notify.VisibleToastCount;
                notify.SetCenterOpen(true);
                int all = notify.VisibleRows.Count;
                notify.SelectFilters(1, null);
                bool urgentOnly = notify.VisibleRows.Count == 1 && notify.VisibleRows[0].Level == NotifyLevel.Urgent;
                notify.SelectFilters(0, "power_lost");
                bool typeOnly = notify.VisibleRows.Count == 1 && notify.VisibleRows[0].Type.Id == "power_lost";
                notify.Select(notify.VisibleRows[0]);
                int members = hud.Q<VisualElement>("NotifyDetailMembers").childCount;
                bool detailShown = !hud.Q<VisualElement>("NotifyDetail").ClassListContains("uk-hidden");
                Vector3 flown = Vector3.zero;
                NotificationCenter.LocateHandler = (string region, Vector3 pos, out string key) =>
                {
                    key = null;
                    flown = pos;
                    return true;
                };
                NotificationEntry toast = NotificationCenter.Toasts.First(e => e.HasAnyLocation);
                int toastsBefore = NotificationCenter.Toasts.Count;
                notify.OnToastClicked(toast);
                bool dismissed = NotificationCenter.Toasts.Count == toastsBefore - 1;
                notify.SetCenterOpen(false);
                Expect(toastCount == toastsBefore && toastCount <= 5 && dismissed && all == 5 && urgentOnly && typeOnly
                       && members == 10 && detailShown && flown == toast.Latest.Location && UiEscapeStack.Count == 0,
                    $"通知界面：弹出条 {toastCount} 条（≤5）；中心 {all} 条；紧急页 / 缺电类型筛选各 1 条；展开聚合列出 {members} 条成员；点弹出条镜头飞到 {flown}；关闭后出栈");
            }
            finally
            {
                if (KeyBindingsPanelUIToolkit.IsOpen)
                {
                    panel.SetOpen(false);
                }
                UiConfirmDialog.ResetForTests();
                UiConfirmDialog.UnbindView();
                GameSettings.ResetKeyBindingsToDefault();
                Object.DestroyImmediate(overlayGo);
                Object.DestroyImmediate(keysGo);
                Object.DestroyImmediate(hudGo);
            }
        }

        // ── K. 布局 ────────────────────────────────────────────────────────────

        private static void CheckLayouts()
        {
            var cases = new (string Uxml, string Root, Action<VisualElement> Prepare)[]
            {
                ("KeyBindingsPanel.uxml", "KeyBindingsRoot", root =>
                {
                    var go = new GameObject("__probe_keys") { hideFlags = HideFlags.HideAndDontSave };
                    var c = go.AddComponent<KeyBindingsPanelUIToolkit>();
                    c.BindView(root.panel.visualTree);
                    c.SetOpen(true);
                    c.SetOpen(false);
                    root.RemoveFromClassList("uk-hidden");
                    Object.DestroyImmediate(go);
                }),
                ("NotificationHud.uxml", "NotifyCenter", root =>
                {
                    NewCampaign();
                    UiKitGalleryUIToolkit.PostBurst();
                    NotificationCenter.Post("auto_paused", "notify.type.boss_phase.name", null, "ui.notify.source_system", "notify.type.boss_phase.name");
                    var go = new GameObject("__probe_notify") { hideFlags = HideFlags.HideAndDontSave };
                    var c = go.AddComponent<NotificationHudUIToolkit>();
                    c.BindView(root.panel.visualTree);
                    c.SetCenterOpen(true);
                    c.Select(NotificationCenter.History[0]);
                    c.SetCenterOpen(false);
                    root.RemoveFromClassList("uk-hidden");
                    root.panel.visualTree.Q<VisualElement>("NotifyDetail").RemoveFromClassList("uk-hidden");
                    Object.DestroyImmediate(go);
                }),
                ("NotificationHud.uxml", "ToastColumn", root =>
                {
                    NewCampaign();
                    UiKitGalleryUIToolkit.PostBurst();
                    var go = new GameObject("__probe_toast") { hideFlags = HideFlags.HideAndDontSave };
                    var c = go.AddComponent<NotificationHudUIToolkit>();
                    c.BindView(root.panel.visualTree);
                    c.RenderToasts();
                    Object.DestroyImmediate(go);
                }),
                ("UiKitOverlay.uxml", "ConfirmDialog", root =>
                {
                    UiConfirmDialog.BindView(root.panel.visualTree);
                    var r = new ConfirmRequest { Title = GameText.Get("input.rebind.conflict_title"), Irreversible = true };
                    for (int i = 0; i < 6; i++)
                    {
                        r.Lines.Add(GameText.Format("input.rebind.conflict_line", "Ctrl+Alt+F12", GameText.Get("input.action.toggle_notification_center.name"), GameText.Get("input.context.strategy")));
                    }
                    r.Consequences.Add(GameText.Format("input.rebind.reset_all_line", "85"));
                    UiConfirmDialog.Show(r);
                    UiConfirmDialog.ResetForTests();
                    UiConfirmDialog.UnbindView();
                    root.parent?.RemoveFromClassList("uk-hidden");
                }),
                ("PauseMenu.uxml", "PauseMenuWindow", root =>
                {
                    var go = new GameObject("__probe_pause") { hideFlags = HideFlags.HideAndDontSave };
                    go.AddComponent<PauseMenuUIToolkit>().BindView(root.panel.visualTree);
                    root.panel.visualTree.Q<Button>("PauseGallery").RemoveFromClassList("uk-hidden");
                    Object.DestroyImmediate(go);
                }),
                ("UiKitGallery.uxml", "GalleryRoot", root =>
                {
                    var go = new GameObject("__probe_gallery") { hideFlags = HideFlags.HideAndDontSave };
                    go.AddComponent<UiKitGalleryUIToolkit>().BindView(root.panel.visualTree);
                    Object.DestroyImmediate(go);
                }),
            };
            float[] scales = { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") };
            Expect(Mathf.Approximately(scales[0], 0.8f) && Mathf.Approximately(scales[2], 1.5f), $"UI 缩放范围 {scales[0]:0.##}～{scales[2]:0.##}（FGR-UX-060 80%～150%）");
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (var c in cases)
                {
                    foreach (float scale in scales)
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + c.Uxml, c.Root, stressFill: true,
                            prepare: c.Prepare, uiScale: scale, resolutions: Fg13Resolutions);
                        CleanupRuntime();
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 {c.Uxml}#{c.Root} [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
        }

        // ── L. 文本键 ──────────────────────────────────────────────────────────

        private static void CheckTextKeys()
        {
            string folder = Path.Combine(Application.dataPath, "GameRes/Raw/UI/UiKit");
            var hard = new List<string>();
            var textAttr = new Regex("\\btext=\"([^\"]+)\"");
            int uxmlCount = 0;
            foreach (string f in Directory.GetFiles(folder, "*.uxml"))
            {
                uxmlCount++;
                foreach (Match m in textAttr.Matches(File.ReadAllText(f)))
                {
                    hard.Add(Path.GetFileName(f) + ":" + m.Groups[1].Value);
                }
            }
            // FG0-ARCH-04 新增 BuildModeHud.uxml（建造栏），共 6 份。
            Expect(uxmlCount == 6 && hard.Count == 0, $"{uxmlCount} 份新 UXML 没有写死的界面文字（全部由代码按文本键填写）{(hard.Count == 0 ? string.Empty : "——" + string.Join("，", hard))}");

            string[] codeDirs =
            {
                Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/UI/Kit"),
                Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Notifications"),
            };
            var keyUse = new Regex("GameText\\.(Get|Format|Has)\\(\\s*\"([a-z0-9_.]+)\"");
            var missing = new List<string>();
            int refs = 0;
            var files = codeDirs.SelectMany(d => Directory.GetFiles(d, "*.cs")).Concat(new[]
            {
                Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Core/InputDisplay.cs"),
                Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Core/InputActionCatalog.cs"),
            });
            foreach (string f in files)
            {
                foreach (Match m in keyUse.Matches(File.ReadAllText(f)))
                {
                    refs++;
                    if (!GameText.Has(m.Groups[2].Value))
                    {
                        missing.Add(Path.GetFileName(f) + ":" + m.Groups[2].Value);
                    }
                }
            }
            var tableKeys = InputActionCatalog.All.SelectMany(d => new[] { d.NameKey, d.CategoryKey })
                .Concat(NotificationCatalog.AllTypes.SelectMany(t => new[] { t.NameKey, t.SingleKey, t.ManyKey }))
                .Where(k => !GameText.Has(k)).ToList();
            Expect(refs > 150 && missing.Count == 0 && tableKeys.Count == 0,
                $"新代码引用的 {refs} 处文本键与表里的动作 / 通知文本键全部存在{(missing.Count + tableKeys.Count == 0 ? string.Empty : "——缺：" + string.Join("，", missing.Concat(tableKeys).Take(10)))}");

            // 中英两套都不为空（英文通常更长）。
            var emptyEn = new List<string>();
            foreach (InputActionDef d in InputActionCatalog.All)
            {
                if (!GameText.TryGet(d.NameKey, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    emptyEn.Add(d.NameKey);
                }
            }
            Expect(emptyEn.Count == 0, "全部动作名都有英文");
        }

        // ── M. 来源展开与引导钩子 ────────────────────────────────────────────────

        private static void CheckBreakdownAndHooks()
        {
            // 家园电力：核心基础 20 + 发电机 80 = 100 供给；核心 10 + 信标 30 需求 → 盈余 60。改成只有核心时供给 20、信标停机。
            CampaignState state = CampaignState.CreateNew("fgux01-power", "Standard", 11);
            state.BuildingRecords = new[]
            {
                PowerBuilding("core", 1), PowerBuilding("generator", 1), PowerBuilding("beacon", 2),
            };
            Campaign.Regions.HomeValleyPowerGrid.GridSummary grid = Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            TooltipContent power = GameLogic.UI.Common.HomeValueBreakdown.Power(state);
            float sum = GameLogic.UI.Common.HomeValueBreakdown.SumSources(power);
            Expect(power.Sources.Count == 4 && Mathf.Approximately(sum, grid.TotalSupply - grid.TotalDemand)
                   && power.Total == GameText.Format("tooltip.power.surplus", UiFormat.Number(grid.TotalSupply - grid.TotalDemand)),
                $"电力来源展开：{string.Join("，", power.Sources.Select(x => x.Label + " " + x.Value))}；逐项相加 {sum} = 电网供给 {grid.TotalSupply} − 需求 {grid.TotalDemand}；合计行“{power.Total}”");

            state.BuildingRecords = new[] { PowerBuilding("core", 1), PowerBuilding("signal_tower", 2), PowerBuilding("beacon", 2) };
            grid = Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            power = GameLogic.UI.Common.HomeValueBreakdown.Power(state);
            sum = GameLogic.UI.Common.HomeValueBreakdown.SumSources(power);
            bool brownoutNamed = power.Sources.Any(x => x.Label.Contains(GameText.Get("building.beacon.name")) && x.Label.Contains("已停机"));
            TooltipContent signal = GameLogic.UI.Common.HomeValueBreakdown.Signal(state);
            Expect(grid.Shortfall > 0f && Mathf.Approximately(sum, grid.TotalSupply - grid.TotalDemand) && brownoutNamed
                   && power.Total == GameText.Format("tooltip.power.shortfall", UiFormat.Number(grid.TotalDemand - grid.TotalSupply))
                   && Mathf.Approximately(GameLogic.UI.Common.HomeValueBreakdown.SumSources(signal), state.SignalBandwidth),
                $"缺电时：停机的建筑在来源里写明“已停机”，合计行“{power.Total}”；信号带宽来源相加 = {state.SignalBandwidth}（{string.Join("，", signal.Sources.Select(x => x.Label + " " + x.Value))}）");

            // 引导钩子：每个只触发一次（跨读盘），面板第一次打开时真的发出。
            PlayerPrefs.DeleteKey(SettingsPrefsKey); // 前面各节已经触发过一些钩子：从一份全新的本机设置开始
            GameSettings.Load();
            var seen = new List<string>();
            Action<string> listener = seen.Add;
            GuidanceHooks.FirstRaised += listener;
            try
            {
                bool first = GuidanceHooks.Raise(GuidanceHooks.FirstReservedAction);
                bool again = GuidanceHooks.Raise(GuidanceHooks.FirstReservedAction);
                GameSettings.Load();
                bool afterReload = GuidanceHooks.Raise(GuidanceHooks.FirstReservedAction);
                VisualElement keys = MountUxml("KeyBindingsPanel.uxml", out GameObject keysGo);
                var panel = keysGo.AddComponent<KeyBindingsPanelUIToolkit>();
                panel.BindView(keys);
                panel.SetOpen(true);
                panel.SetOpen(false);
                panel.SetOpen(true);
                panel.SetOpen(false);
                Object.DestroyImmediate(keysGo);
                NewCampaign();
                NotificationCenter.Post("machine_destroyed", "#1");
                NotificationCenter.Post("expedition_wiped", "x");
                Expect(first && !again && !afterReload
                       && seen.Count(h => h == GuidanceHooks.KeyBindingsFirstOpen) == 1 && seen.Count(h => h == GuidanceHooks.FirstUrgentNotification) == 1
                       && GuidanceHooks.Known.Contains(GuidanceHooks.FirstReservedAction),
                    $"引导钩子：首次触发发出、再触发与重新读盘后都不再发出；按键面板打开两次只发一次“首次打开”；第一条紧急通知发出一次（共 {seen.Count} 个：{string.Join("、", seen)}）");
            }
            finally
            {
                GuidanceHooks.FirstRaised -= listener;
            }
        }

        private static BuildingRecord PowerBuilding(string typeId, int priority) => new BuildingRecord
        {
            BuildingId = Campaign.Regions.HomeValleyLayout.RegionId + ":" + typeId,
            BuildingTypeId = typeId,
            RegionId = Campaign.Regions.HomeValleyLayout.RegionId,
            Health = 100f,
            ConstructionState = BuildingConstructionState.Operational,
            PowerPriority = priority,
            PowerState = BuildingPowerState.NotApplicable,
            Inventory = Array.Empty<CargoEntry>(),
            QueueIds = Array.Empty<string>(),
        };

        // ── N. 性能 ────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            // 每帧界面快捷键处理（无按键时）：与实体数无关的常数开销。
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            NewCampaign();
            UiKitInputPump.ProcessWorldKeys();
            const int frames = 2000;
            long memBefore = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                UiKitInputPump.ProcessWorldKeys();
            }
            sw.Stop();
            long memDelta = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - memBefore;
            double perFrameUs = sw.Elapsed.TotalMilliseconds * 1000.0 / frames;
            Expect(perFrameUs < 200.0, $"界面快捷键每帧处理 {perFrameUs:F1} µs（{InputActionCatalog.All.Count} 个动作逐个查按键，无按键；{frames} 帧托管堆增量 {memDelta / 1024} KB）");

            // 查绑定：一百万次。
            InputBindingSet b = GameSettings.KeyBindings;
            sw.Restart();
            int sink = 0;
            for (int i = 0; i < 1_000_000; i++)
            {
                sink += (int)b.GetChord((GameActionId)(i % 95)).Key;
            }
            sw.Stop();
            Expect(sw.Elapsed.TotalMilliseconds < 500, $"GetChord 一百万次 {sw.Elapsed.TotalMilliseconds:F1} ms（约 {sw.Elapsed.TotalMilliseconds * 1e6 / 1_000_000:F0} ns/次）{(sink == int.MinValue ? "!" : string.Empty)}");

            // 通知：一千条混合类型（持续高频事件的压力档）。
            NewCampaign();
            string[] types = NotificationCatalog.AllTypes.Where(t => t.KeepInHistory && !t.AutoPauseDefault).Select(t => t.Id).ToArray();
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                _clock += 0.37f;
                NotificationCenter.Post(types[i % types.Length], "#" + i, new Vector3(i, 0f, i));
            }
            sw.Stop();
            Expect(sw.Elapsed.TotalMilliseconds < 400 && NotificationCenter.History.Count <= UiTuningValues.GetInt("notify.history_capacity"),
                $"通知压力档：1000 条（{types.Length} 类轮换、带位置、写穿存档）共 {sw.Elapsed.TotalMilliseconds:F1} ms，历史 {NotificationCenter.History.Count} 条");
            Line("  - 测量环境：Unity 6000.3.17f1 Editor batchmode（Mono JIT，影子工程）。真机为 HybridCLR 解释执行热更代码，预计慢数倍，仍是 O(动作数)/O(1) 的常数开销；真机数字在 FG15-SYS-02 性能门禁采集。");
        }

        // ── F 补充：真实镜头定位（战略 / 接入两种视角） ─────────────────────────

        private static void CheckRealCameraLocate()
        {
            var camGo = new GameObject("__fgux01_locate_cam") { hideFlags = HideFlags.HideAndDontSave };
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            bool edgePanBefore = GameSettings.EdgePanEnabled;
            GameSettings.SetEdgePanEnabled(false); // 假读取器的指针在屏幕角上，关掉边缘平移，只看定位本身
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 12f;
                var target = new Vector3(5f, 0f, 3f); // 在镜头可达范围内（战略焦点会被场地边界钳制）
                string region = GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;

                // 接入（直控）视角：机器在 (-20, -20)。旧实现先设焦点再 RequestStrategy，焦点被当前镜头覆盖，镜头停在机器上方。
                var direct = new GameLogic.View.CameraDirector();
                direct.Bind(cam, (out Unity.Mathematics.float2 a) => { a = new Unity.Mathematics.float2(-20f, -20f); return true; },
                    new Vector3(0f, 30f, -18f), 60f, startInStrategy: false);
                for (int i = 0; i < 5; i++)
                {
                    direct.Tick(false);
                }
                bool okDirect = GameRoot.LocateOn(direct, region, region, target, out string keyDirect);
                for (int i = 0; i < 200 && direct.Mode != GameLogic.View.ViewMode.Strategy; i++)
                {
                    direct.Tick(false);
                }
                direct.Tick(false);
                Unity.Mathematics.float2 fd = direct.StrategyFocus;
                bool landedDirect = okDirect && keyDirect == null && direct.Mode == GameLogic.View.ViewMode.Strategy
                                    && Mathf.Abs(fd.x - target.x) < 0.01f && Mathf.Abs(fd.y - target.z) < 0.01f
                                    && Mathf.Abs(cam.transform.position.x - target.x) < 0.5f;
                direct.Unbind();

                // 战略视角：直接改焦点，镜头下一帧落到目标。
                var strategy = new GameLogic.View.CameraDirector();
                strategy.Bind(cam, (out Unity.Mathematics.float2 a) => { a = default; return false; },
                    new Vector3(0f, 30f, -18f), 60f, startInStrategy: true);
                bool okStrategy = GameRoot.LocateOn(strategy, region, region, target, out _);
                strategy.Tick(false);
                Unity.Mathematics.float2 fs = strategy.StrategyFocus;
                bool landedStrategy = okStrategy && Mathf.Abs(fs.x - target.x) < 0.01f && Mathf.Abs(fs.y - target.z) < 0.01f
                                      && Mathf.Abs(cam.transform.position.x - target.x) < 0.5f;
                bool otherRegion = !GameRoot.LocateOn(strategy, region, "fractured_city", target, out string keyOther) && keyOther == "ui.notify.other_region";
                strategy.Unbind();

                Expect(landedDirect && landedStrategy && otherRegion,
                    $"真实镜头定位：接入视角点通知 → 拉回战略并落到通知位置（焦点 {fd}，镜头 x={cam.transform.position.x:F1}）；战略视角直接飞到 {fs}；别的区域的通知说明原因");
            }
            finally
            {
                GameSettings.SetEdgePanEnabled(edgePanBefore);
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── O. 审查修复回归 ───────────────────────────────────────────────────

        private static void CheckReviewFixes()
        {
            CheckCueLocationsFromRealTriggers();
            CheckPauseSaveExportsMachines();
            CheckEscCoversDemoPanels();
            CheckTextFocusProbe();
            CheckRightClickCancelsArm();
        }

        /// <summary>正式流程里的触发点（机器阵亡、电网断电 / 恢复）直接产出可定位的通知，不经 NotificationCenter.Post 捷径。</summary>
        private static void CheckCueLocationsFromRealTriggers()
        {
            CampaignState state = NewCampaign();
            MachineRegistry.ResetForNewCampaign();
            try
            {
                string region = GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;
                int logicId = MachineRegistry.SpawnMachine(GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId,
                    GameLogic.Campaign.Regions.HomeValleyLayout.BlueprintErc001Id, region, new Vector2(1f, 1f), 100f, 100f).LogicId;
                var live = new Vector2(7f, -4f);
                MachineRegistry.LivePositionProvider = id => id == logicId ? live : (Vector2?)null;
                MachineRegistry.MarkDeadByLogicId(logicId);
                NotificationEntry lost = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "machine_destroyed");
                Vector3 flown = Vector3.zero;
                NotificationCenter.LocateHandler = (string r, Vector3 pos, out string key) => { key = null; flown = pos; return true; };
                bool located = lost != null && NotificationCenter.Locate(lost, -1, out _);
                Expect(lost != null && lost.Latest.HasLocation && lost.Latest.Location == new Vector3(live.x, 0f, live.y) && located && flown == lost.Latest.Location,
                    $"机器阵亡（MachineRegistry.MarkDeadByLogicId 真实入口）→ “机器损失”通知带阵亡处实时位置 {lost?.Latest.Location}，点击定位飞到该处");

                // 电网：三栋用电建筑在同一次重算里停机 → 一条“N 处缺电”，展开后逐栋可定位。
                _clock += 30f;
                var bs = new List<BuildingRecord>
                {
                    NewBuilding("home_valley:core", GameLogic.Campaign.Regions.HomeValleyLayout.BuildingTypeCore, new Vector2(0f, 0f)),
                };
                string[] consumerTypes =
                {
                    GameLogic.Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation,
                    GameLogic.Campaign.Regions.HomeValleyLayout.BuildingTypeAnalysisBench,
                    GameLogic.Campaign.Regions.HomeValleyLayout.BuildingTypeRepairBay,
                };
                for (int i = 0; i < consumerTypes.Length; i++)
                {
                    bs.Add(NewBuilding("home_valley:" + consumerTypes[i], consumerTypes[i], new Vector2(10f + i * 5f, -8f)));
                }
                state.BuildingRecords = bs.ToArray();
                GameLogic.Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                List<BuildingRecord> browned = state.BuildingRecords.Where(b => b.PowerState == BuildingPowerState.Brownout).ToList();
                NotificationEntry power = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "power_lost");
                bool allMembersLocated = power != null && power.Members.Count == browned.Count
                                         && browned.All(b => power.Members.Any(m => m.HasLocation && m.Location == new Vector3(b.Position.x, 0f, b.Position.y)));
                bool memberLocate = power != null && power.Members.Count > 1 && NotificationCenter.Locate(power, 1, out _) && flown == power.Members[1].Location;
                Expect(browned.Count >= 2 && allMembersLocated && memberLocate,
                    $"电网断电（HomeValleyPowerGrid.Recompute 真实入口）：{browned.Count} 栋停机 → 一条“{power?.Text}”，成员 {power?.Members.Count} 条逐栋带坐标，点第 2 条飞到 {flown}");

                // 恢复供电同样逐栋带位置。
                _clock += 30f;
                state.BuildingRecords = state.BuildingRecords.Concat(new[]
                {
                    NewBuilding("home_valley:generator_x", GameLogic.Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator, new Vector2(-6f, 3f)),
                }).ToArray();
                GameLogic.Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                NotificationEntry restored = NotificationCenter.History.LastOrDefault(e => e.Type.Id == "power_restored");
                Expect(restored != null && restored.Members.All(m => m.HasLocation),
                    $"恢复供电也逐栋带坐标（{restored?.Members.Count ?? 0} 条成员）");
            }
            finally
            {
                MachineRegistry.LivePositionProvider = null;
                MachineRegistry.ResetForNewCampaign();
            }
        }

        private static BuildingRecord NewBuilding(string id, string typeId, Vector2 position)
        {
            int priority = GameLogic.Campaign.Regions.HomeValleyLayout.PowerProfile.TryGetValue(typeId, out var profile) ? profile.PowerPriority : 0;
            return new BuildingRecord
            {
                BuildingId = id,
                BuildingTypeId = typeId,
                RegionId = GameLogic.Campaign.Regions.HomeValleyLayout.RegionId,
                Position = position,
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = priority,
                PowerState = BuildingPowerState.Powered,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
        }

        /// <summary>暂停菜单“保存并返回主菜单”的存档部分走导出流程：上次自动存档之后的新机器、阵亡、经历都进存档。</summary>
        private static void CheckPauseSaveExportsMachines()
        {
            CampaignState state = CampaignState.CreateNew("fgux01-pausesave", "Standard", 97531);
            CampaignSession.Set(0, state);
            MachineRegistry.ResetForNewCampaign();
            try
            {
                string region = GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;
                int a = MachineRegistry.SpawnMachine(GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId,
                    GameLogic.Campaign.Regions.HomeValleyLayout.BlueprintErc001Id, region, new Vector2(2f, 3f), 100f, 100f).LogicId;
                int b = MachineRegistry.SpawnMachine(GameLogic.Campaign.Regions.HomeValleyLayout.Erc001ChassisId,
                    GameLogic.Campaign.Regions.HomeValleyLayout.BlueprintErc001Id, region, new Vector2(4f, 5f), 100f, 100f).LogicId;
                MachineRegistry.RecordJobCompleted(a);
                MachineRegistry.MarkDeadByLogicId(b);
                int snapshotBefore = state.MachineRecords?.Length ?? 0; // 快照还是空的（没有自动存档过）

                SaveResult saved = PauseMenuUIToolkit.SaveForQuit(0);
                LoadResult loaded = CampaignSaveService.Load(0);
                MachineRecord la = loaded.State?.MachineRecords?.FirstOrDefault(m => m.LogicId == a);
                MachineRecord lb = loaded.State?.MachineRecords?.FirstOrDefault(m => m.LogicId == b);
                Expect(snapshotBefore == 0 && saved.Success && loaded.Success && la != null && la.IsAlive && la.JobsCompleted == 1
                       && lb != null && !lb.IsAlive,
                    $"暂停菜单“保存并返回”先导出机器记录再写盘：读回 #{a} 存活、完成工作 {la?.JobsCompleted}，#{b} 阵亡={(lb != null && !lb.IsAlive)}（存档前快照 {snapshotBefore} 条）");
            }
            finally
            {
                MachineRegistry.ResetForNewCampaign();
                CampaignSession.Clear();
            }
        }

        /// <summary>Esc 栈覆盖 Demo 面板（同步入栈）、胜负页（阻断层）、任务日志（不再抢先读取消键）。</summary>
        private static void CheckEscCoversDemoPanels()
        {
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            try
            {
                bool factoryOpen = true;
                object factory = new object();
                Action closeFactory = () => factoryOpen = false;
                UiEscapeStack.Sync(factory, factoryOpen, closeFactory);
                UiEscapeStack.Sync(factory, factoryOpen, closeFactory); // 每帧同步不重复压层
                int layers = UiEscapeStack.Count;
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                UiEscapeStack.Sync(factory, factoryOpen, closeFactory);
                Expect(layers == 1 && !factoryOpen && UiEscapeStack.Count == 0,
                    "Demo 面板（开关状态在控制器里）每帧同步进 Esc 栈：开着时占一层且不重复，Esc 先关它");

                object failurePage = new object();
                bool dialogClosed = false;
                UiEscapeStack.SyncBlocking(failurePage, true);
                UiEscapeStack.Push("dialog", () => dialogClosed = true);
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                int frameBefore = UiKitInputPump.LastPauseMenuFrame;
                reader.Press(KeyCode.Escape);
                bool consumed = UiEscapeStack.CloseTop();
                Frame(reader);
                Expect(dialogClosed && consumed && UiEscapeStack.Contains(failurePage) && UiKitInputPump.LastPauseMenuFrame == frameBefore,
                    "胜负页是阻断层：上面的对话框照常先关；轮到它时 Esc 被吞掉、页面不关、也不会打开被它盖住的暂停菜单");
                UiEscapeStack.SyncBlocking(failurePage, false);
                UiEscapeStack.Push("left-over", () => { });
                UiKitRuntime.CloseWorldUi();
                Expect(!UiEscapeStack.Contains(failurePage) && UiEscapeStack.Count == 0 && !PauseMenuUIToolkit.IsOpen && !InputRouter.ModalUiOpen,
                    "回主菜单的收尾（GameRoot.EndRun → UiKitRuntime.CloseWorldUi）清空 Esc 栈与模态，暂停菜单不残留");

                string missionSrc = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Objective/MissionLogUIToolkit.cs");
                Expect(!missionSrc.Contains("GameActionId.Cancel") && missionSrc.Contains("UiEscapeStack.Push(this"),
                    "任务日志不再在 Update 里抢先读取消键，改为进 Esc 栈（上面再开通知中心时 Esc 先关通知中心）");
            }
            finally
            {
                UiEscapeStack.ResetForTests();
                InputRouter.DebugSetReader(null);
            }
        }

        /// <summary>任意可编辑文本框（电路板蓝图命名框）拿着焦点时，快捷键整体让位；搜索框 Esc 先失焦、不同帧关面板。</summary>
        private static void CheckTextFocusProbe()
        {
            VisualElement board = MountAny("Assets/GameRes/Raw/UI/CircuitBoard/CircuitBoardPanel.uxml", out GameObject boardGo);
            var reader = new FakeReader();
            InputRouter.Reset(); // 清掉显式登记的文本焦点，只靠探针判定
            InputRouter.DebugSetReader(reader);
            NewCampaign();
            try
            {
                UiTextFocusProbe.Register(board);
                TextField name = board.Q<TextField>("NewBlueprintNameField");
                InputRouter.SetScope(InputScope.Strategy);
                StrategyClock.SetSpeed(1f);
                bool baseline = !InputRouter.KeyboardSuppressed;
                name?.Focus();
                bool suppressed = InputRouter.KeyboardSuppressed && InputRouter.TextInputFocused;
                int before = NotificationCenter.History.Count;
                foreach (KeyCode k in new[] { KeyCode.R, KeyCode.Space, KeyCode.C, KeyCode.Alpha3, KeyCode.BackQuote })
                {
                    reader.Press(k);
                    UiKitInputPump.ProcessWorldKeys();
                    Frame(reader);
                }
                // “尚未开放”提示只弹出、不进历史：同时看历史与弹出条。
                bool nothingFired = NotificationCenter.History.Count == before && NotificationCenter.Toasts.All(e => e.Type.Id != "feature_locked")
                                    && Mathf.Approximately(StrategyClock.SpeedMultiplier, 1f) && !NotificationHudUIToolkit.CenterOpen;
                name?.Blur();
                bool released = !InputRouter.KeyboardSuppressed;
                reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                UiKitInputPump.ProcessWorldKeys();
                Frame(reader);
                bool controlFires = NotificationCenter.Toasts.Any(e => e.Type.Id == "feature_locked");
                string focusedName = (board.panel?.focusController?.focusedElement as VisualElement)?.name ?? "null";
                Line($"    · 探针：命名框 {(name != null ? "找到" : "缺失")}，聚焦元素 {focusedName}，让位={suppressed}，无触发={nothingFired}，失焦后恢复={released}，对照触发={controlFires}");
                Expect(name != null && baseline && suppressed && nothingFired && released && controlFires,
                    "电路板蓝图命名框（Demo 文本框，没有自己接焦点事件）获得焦点 → 全局探针判定打字中：R / Space / C / 3 / ` 都不触发（不暂停、不改速、不弹“尚未开放”）；失焦后同样的键恢复生效");
            }
            finally
            {
                UiTextFocusProbe.Unregister(board);
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                StrategyClock.Reset();
                Object.DestroyImmediate(boardGo);
            }

            // 搜索框 Esc：同一帧只失焦，不关面板；下一帧再按 Esc 才关。
            VisualElement keys = MountUxml("KeyBindingsPanel.uxml", out GameObject keysGo);
            var panel = keysGo.AddComponent<KeyBindingsPanelUIToolkit>();
            reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            try
            {
                panel.BindView(keys);
                panel.SetOpen(true);
                TextField search = keys.Q<TextField>("KeyBindingsSearch");
                search.Focus();
                using (KeyDownEvent esc = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                {
                    search.SendEvent(esc);
                }
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                bool stillOpen = KeyBindingsPanelUIToolkit.IsOpen;
                Frame(reader);
                reader.Press(KeyCode.Escape);
                UiKitInputPump.Process();
                Frame(reader);
                Expect(stillOpen && !KeyBindingsPanelUIToolkit.IsOpen,
                    "搜索框里按 Esc：这一帧只失焦（按键面板还开着），下一次 Esc 才关面板");
            }
            finally
            {
                if (KeyBindingsPanelUIToolkit.IsOpen)
                {
                    panel.SetOpen(false);
                }
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(keysGo);
            }
        }

        /// <summary>武装命令（等下一次点击选目标）是一种选择模式：右键取消，且同一次右键不再落到“取消在办工单”。</summary>
        private static void CheckRightClickCancelsArm()
        {
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            var squad = new GameLogic.Campaign.Regions.RegionSquadCommandSystem();
            try
            {
                InputRouter.SetScope(InputScope.Strategy);
                InputRouter.SetUiPointerBlocker(null);
                squad.Bind(new GameLogic.Campaign.Regions.RegionSquadCommandContext
                {
                    IsEligible = _ => true,
                    IsDirectControlled = _ => false,
                    Markers = new List<GameLogic.Campaign.Regions.HomeValleyMachineMarker>(),
                });
                squad.DebugSelectMany(new[] { 1 });
                reader.Press(GameSettings.KeyBindings.GetKey(GameActionId.CommandMove));
                squad.Tick(true, 0.016f);
                Frame(reader);
                bool armed = squad.ArmedKind == GameLogic.Campaign.Regions.RegionCommandKind.Move;
                reader.MouseDown.Add(1);
                squad.Tick(true, 0.016f);
                bool cancelled = squad.ArmedKind == null && squad.ConsumedSecondaryThisFrame;
                Frame(reader);
                squad.Tick(true, 0.016f);
                Expect(armed && cancelled && !squad.ConsumedSecondaryThisFrame,
                    "移动命令武装待命 → 右键取消（FGR-UX-001 右键取消选择模式），并标记本帧右键已用掉，归还谷地不再拿它去取消机器的在办工单");
            }
            finally
            {
                squad.Unbind();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        /// <summary>挂任意路径的 UXML 到临时面板（同 <see cref="MountUxml"/>）。</summary>
        private static VisualElement MountAny(string path, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgUiKitSelfCheck_any") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        /// <summary>把一份 UiKit UXML 挂到临时面板（1920×1080 渲染纹理，编辑模式可布局）上。</summary>
        private static VisualElement MountUxml(string file, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + file);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            var rt = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            settings.targetTexture = rt;
            go = new GameObject("__FgUiKitSelfCheck_" + file) { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public readonly HashSet<int> MouseDown = new HashSet<int>();
            public readonly HashSet<int> MouseUp = new HashSet<int>();
            public float Scroll;

            public void Press(KeyCode key) => _down.Add(key);
            public void Hold(KeyCode key) => _held.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                _held.Clear();
                MouseDown.Clear();
                MouseUp.Clear();
                Scroll = 0f;
            }

            // 与真实键盘一致：按下的那一帧键也处于“按住”状态。
            public bool GetKey(KeyCode key) => _held.Contains(key) || _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => MouseDown.Contains(button);
            public bool GetMouseButtonUp(int button) => MouseUp.Contains(button);
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => Scroll;
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
