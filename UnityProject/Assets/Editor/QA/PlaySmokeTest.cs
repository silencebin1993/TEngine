using System;
using System.IO;
using System.Linq;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.Objective;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Luban;
using BinGames.Sim.Logistics;
using Button = UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 真进 Play 的冒烟测试（batchmode 自检测不到的运行时错误靠它抓——2026-09-25 主菜单空引用就是这么漏掉的）：
    /// 打开 main.unity 进 Play → 存档改到临时目录（绝不碰玩家槽位）→ 点主菜单“新建”→ 点空槽位 → 进归还谷地 →
    /// 读目标条 → 按 J 开任务日志、Esc 关 → 按出征同样的调用顺序进破碎都市 → 进铸造前哨 → 回归还谷地。
    /// 全程收集 Error/Exception/Assert，任何一条都算失败。
    /// FG0-DATA-01：每段扫描界面文本里的 ⟦key⟧ 缺失标记；工单目标名走文本键；区域播种的敌人生命等于 fg.TbMechEnemy 表值。
    /// FG0-SAVE-01：进主菜单前预置 Demo 存档与写坏的存档 → 查“继续”原因 → 打开存档列表查两张卡的提示 → 点 Demo 卡“新建于此槽”→
    /// 确认框点“否”（文件不变）→ 点“读取备份” → 返回后再新建；收尾查自动存档是 v2 → 经唯一回菜单出口回主菜单 → 存档里放一件
    /// 已移除内容（测试表）→ 点“读取”把这份 v2 存档读进游戏 → 查迁移字幕与废料。
    /// FG0-UX-01：主菜单设置页点“全部按键…”打开 UI Toolkit 按键面板（85 个动作）→ Esc 关闭；归还谷地里按通知中心键开 / 关
    /// 通知中心 → 按“尚未开放”的图鉴键看到提示 → Esc 打开暂停菜单（世界暂停）→ 点“按键设置”→ 搜索框获得焦点后按快捷键不触发 →
    /// Esc 逐层关闭按键面板、暂停菜单（世界恢复）。
    /// FG0-ARCH-04：按暂停键 → 按建造菜单键打开建造模式（输入上下文 = 建造）→ 点建造栏选发电机 → 鼠标悬停空地（虚影合法）→ 按旋转键 →
    /// 左键放置（规划中的格网建筑，暂停中不开工）→ 核心旁左键（被拒并给原因）→ 按拆除模式键 → 点虚影取消规划（全额退款）→
    /// 拆除模式点仓库（本局无法重建：被拒、不弹确认、仓库保留，复审第 2 轮软锁修复）→
    /// Esc 退出建造模式（不开暂停菜单）→ 恢复运行，再接原有的点选机器 / 修发电机流程；下令修复后（机器仍选中、有在办工单）
    /// 按 B 进建造模式 → 右键退出 → 修复工单没有被这次右键穿透取消（复审 P1）。
    /// FG0-ARCH-05：暂停菜单显示世界种子与世界设置、点“复制种子”写进剪贴板；建造模式里地形叠加层按区块显示（家园进入后由工作线程预生成、
    /// 没有“生成中”占位）→ 按住镜头右移键平移、跨过区块边界 → 叠加层窗口跟随、新露出的区块补齐，流式加载主线程每帧开销有上限。
    /// FG0-UX-01 审查修复：生产面板开着按 Esc → 面板关闭、暂停菜单不开；电路板蓝图命名框打字时 Space / C 不触发，失焦后 Esc 关电路板；
    /// 回家园后改一台机器的记录 → Esc → 暂停菜单“保存并返回主菜单”→ 确认（真实存档路径，不再走 EndRun 捷径）→ 读档核对机器记录；
    /// 读档进游戏后核心被毁 → 失败页上按 Esc 不开暂停菜单 → 点失败页“返回主菜单”→ 暂停菜单、Esc 栈、模态都不残留。
    ///
    /// 用法：<c>bash tools/unity-play-smoke.sh</c>（影子工程里跑，编辑器开着也行）。进 Play 会重载域，
    /// 驱动状态存在 SessionState 里，[InitializeOnLoad] 重载后接着跑。
    /// </summary>
    [InitializeOnLoad]
    public static class PlaySmokeTest
    {
        private const string K = "BinGames.PlaySmoke.";
        private const double TotalTimeoutSeconds = 480;

        static PlaySmokeTest()
        {
            if (SessionState.GetBool(K + "Active", false))
            {
                Hook();
            }
        }

        public static void Run()
        {
            string outPath = Environment.GetEnvironmentVariable("BINGAMES_SMOKE_OUT");
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(Path.GetTempPath(), "bingames-play-smoke.txt");
            }
            File.WriteAllText(outPath, string.Empty);
            SessionState.SetString(K + "Out", outPath);
            SessionState.SetString(K + "Saves", Path.Combine(Path.GetTempPath(), "bingames-play-smoke-saves-" + Guid.NewGuid().ToString("N")));
            SessionState.SetBool(K + "Active", true);
            SessionState.SetInt(K + "Errors", 0);
            SessionState.SetFloat(K + "RepairedAt", 0f);
            SessionState.SetBool(K + "VfxRaised", false);
            SessionState.SetBool(K + "VfxChecked", false);
            SessionState.SetFloat(K + "Start", (float)EditorApplication.timeSinceStartup);
            Next(0, "开始：打开 Assets/Scenes/main.unity 并进入 Play");
            EditorSceneManager.OpenScene("Assets/Scenes/main.unity", OpenSceneMode.Single);
            Hook();
            EditorApplication.EnterPlaymode();
        }

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }
            // 编辑器自带搜索索引在 batchmode 下的内部异常，与游戏无关。
            if ((stackTrace ?? string.Empty).Contains("UnityEditor.Search."))
            {
                Write($"  - 忽略编辑器内部报错：{condition.Trim()}");
                return;
            }
            SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            string firstFrames = string.Join(" | ", (stackTrace ?? string.Empty).Split('\n').Where(l => l.Trim().Length > 0).Take(4));
            Write($"  ✗ [{type}] {condition.Trim()}  @ {firstFrames}");
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - SessionState.GetFloat(K + "Start", 0f) > TotalTimeoutSeconds)
            {
                Finish("总超时");
                return;
            }
            int step = SessionState.GetInt(K + "Step", 0);
            double inStep = now - SessionState.GetFloat(K + "StepStart", 0f);
            try
            {
                switch (step)
                {
                    case 0: StepEnterPlay(inStep); break;
                    case 1: StepClickNew(inStep); break;
                    case 2: StepPickSlot(inStep); break;
                    case 3: StepWaitHome(inStep); break;
                    case 4: StepHomeSoak(inStep); break;
                    case 5: StepMissionLogOpen(inStep); break;
                    case 6: StepMissionLogClosed(inStep); break;
                    case 10: StepClickGenerator(inStep); break;
                    case 11: StepRepairOrdered(inStep); break;
                    case 12: StepRepairDone(inStep); break;
                    case 13: StepFactoryOpen(inStep); break;
                    case 14: StepFactoryClosed(inStep); break;
                    case 15: StepFactoryEscClosed(inStep); break;
                    case 16: StepFactoryReopened(inStep); break;
                    case 17: StepBuildOpenedWithOrder(inStep); break;
                    case 18: StepBuildRightClickExit(inStep); break;
                    case 60: StepCircuitOpened(inStep); break;
                    case 61: StepTypingSpace(inStep); break;
                    case 62: StepTypingReserved(inStep); break;
                    case 63: StepCircuitEscClosed(inStep); break;
                    case 70: StepPauseForSave(inStep); break;
                    case 71: StepSaveConfirm(inStep); break;
                    case 80: StepFailureShown(inStep); break;
                    case 81: StepFailureEsc(inStep); break;
                    case 82: StepFailureBackToMenu(inStep); break;
                    case 7: StepRuins(inStep); break;
                    case 120: StepWorldHomeKeepsRunning(inStep); break;
                    case 121: StepWorldFlownHome(inStep); break;
                    case 122: StepWorldTabToExpedition(inStep); break;
                    case 123: StepWorldTabToRaid(inStep); break;
                    case 124: StepWorldTriple(inStep); break;
                    case 125: StepWorldPaused(inStep); break;
                    case 126: StepWorldPausedHeld(inStep); break;
                    case 127: StepWorldHomeKey(inStep); break;
                    case 128: StepWorldShuttle(inStep); break;
                    case 8: StepFoundry(inStep); break;
                    case 9: StepBackHome(inStep); break;
                    case 130: StepBeltsLaid(inStep); break;
                    case 131: StepBeltsFar(inStep); break;
                    case 132: StepBeltsNear(inStep); break;
                    case 133: StepBeltsDone(inStep); break;
                    case 20: StepOpenSlotList(inStep); break;
                    case 21: StepSlotCards(inStep); break;
                    case 22: StepBackupRestored(inStep); break;
                    case 24: StepDemoConfirmShown(inStep); break;
                    case 25: StepDemoConfirmCancelled(inStep); break;
                    case 26: StepMenuAfterRun(inStep); break;
                    case 27: StepLoadSlotList(inStep); break;
                    case 28: StepLoadedIntoGame(inStep); break;
                    case 30: StepNotifyCenterOpen(inStep); break;
                    case 31: StepNotifyCenterClosed(inStep); break;
                    case 32: StepReservedKeyHint(inStep); break;
                    case 33: StepPauseMenuOpen(inStep); break;
                    case 34: StepKeyBindingsFromPause(inStep); break;
                    case 35: StepSearchSwallowsHotkeys(inStep); break;
                    case 36: StepEscClosesKeyBindings(inStep); break;
                    case 37: StepEscClosesPauseMenu(inStep); break;
                    case 90: StepBuildPaused(inStep); break;
                    case 91: StepBuildOpened(inStep); break;
                    case 92: StepBuildHover(inStep); break;
                    case 93: StepBuildRotated(inStep); break;
                    case 94: StepBuildPlaced(inStep); break;
                    case 95: StepBuildRejected(inStep); break;
                    case 96: StepBuildDemolishMode(inStep); break;
                    case 97: StepBuildCancelled(inStep); break;
                    case 100: StepBuildDemolishRefused(inStep); break;
                    case 98: StepBuildEsc(inStep); break;
                    case 110: StepWorldPanStart(inStep); break;
                    case 111: StepWorldPanMoved(inStep); break;
                    case 112: StepWorldPanSettled(inStep); break;
                    case 99: StepBuildResume(inStep); break;
                    case 40: StepMenuSettings(inStep); break;
                    case 41: StepMenuAllKeyBindings(inStep); break;
                    case 42: StepMenuAllKeyBindingsClosed(inStep); break;
                }
            }
            catch (Exception e)
            {
                Write($"  ✗ 驱动步骤 {step} 抛异常：{e.GetType().Name}: {e.Message}");
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
                Finish("驱动异常");
            }
        }

        // ── 步骤 ────────────────────────────────────────────────────

        private static void StepEnterPlay(double inStep)
        {
            if (!EditorApplication.isPlaying)
            {
                if (inStep > 90)
                {
                    Finish("90 秒内没能进入 Play");
                }
                return;
            }
            CampaignSaveService.SaveDirectoryOverrideForTests = SessionState.GetString(K + "Saves", null);
            Write("已进入 Play；存档目录改到临时目录：" + CampaignSaveService.SaveDirectoryOverrideForTests);
            SeedSaveSlots();
            Next(20, "预置存档：槽位 2 = 真实 Demo（0.1）存档，槽位 3 = 主档被截断、备份完好的 v2 存档");
        }

        /// <summary>FG0-SAVE-01：主菜单出现前在临时存档目录里放一个 Demo 存档和一个写坏的存档，冒烟走玩家看到的提示与"读取备份"。</summary>
        private static void SeedSaveSlots()
        {
            string dir = CampaignSaveService.SaveDirectory;
            Directory.CreateDirectory(dir);
            string fixture = Path.Combine(Application.dataPath, "Editor/QA/Fixtures/DemoSave_v1_slot.json.txt");
            File.Copy(fixture, CampaignSaveService.SlotPath(1), true);
            CampaignState s = CampaignState.CreateNew("smoke-backup", "Standard", 20260925);
            CampaignSaveService.Save(2, s, SaveReason.Manual);
            s.Scrap += 1;
            CampaignSaveService.Save(2, s, SaveReason.Manual);
            string main = CampaignSaveService.SlotPath(2);
            string text = File.ReadAllText(main);
            File.WriteAllText(main, text.Substring(0, text.Length / 2));
        }

        private static void StepOpenSlotList(double inStep)
        {
            Button load = FindActiveButton("m_btn_Load");
            if (load == null)
            {
                if (inStep > 150)
                {
                    Finish("150 秒内主菜单没出现（找不到“读取”按钮）");
                }
                return;
            }
            if (inStep < 2)
            {
                return;
            }
            string reason = FindText("m_text_ContinueReason")?.text ?? "（节点没找到）";
            Button cont = Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(b => b.name == "m_btn_Continue");
            Check(cont != null && !cont.interactable && reason.Contains("Demo"),
                $"只有 Demo 存档与坏档时“继续”不可用，原因：“{reason}”");
            load.onClick.Invoke();
            Next(21, $"主菜单出现（{inStep:F0} 秒），点“读取”打开存档列表");
        }

        private static void StepSlotCards(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            string demo = FindText("m_text_Slot1Info")?.text ?? string.Empty;
            string broken = FindText("m_text_Slot2Info")?.text ?? string.Empty;
            Button demoAction = Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(b => b.name == "m_btn_Slot1Action");
            Write($"  - 槽位 2 卡片：{demo.Replace("\n", " / ")}");
            Write($"  - 槽位 3 卡片：{broken.Replace("\n", " / ")}");
            string demoLabel = FindText("m_text_Slot1ActionLabel")?.text ?? string.Empty;
            Check(demo.Contains("Demo（0.1）") && demo.Contains("请新建战役") && demoAction != null && demoAction.interactable && demoLabel == "新建于此槽",
                $"Demo 存档卡明确提示不迁移，按钮“{demoLabel}”（先确认，原文件另存保留）");
            Check(broken.Contains("文件不完整") && broken.Contains("可以读取上一版备份"), "坏档卡显示原因与可读取的备份");
            CheckNoTextMarkers("存档列表");
            if (demoAction == null || !demoAction.interactable)
            {
                Finish("Demo 卡按钮不可点");
                return;
            }
            demoAction.onClick.Invoke();
            Next(24, "点槽位 2（Demo）的“新建于此槽”");
        }

        private static void StepDemoConfirmShown(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            string info = FindText("m_text_ConfirmInfo")?.text ?? string.Empty;
            Button no = FindActiveButton("m_btn_ConfirmNo");
            Write($"  - 确认框：{info.Replace("\n", " / ")}");
            Check(no != null && info.Contains("Demo") && info.Contains("campaign_slot1.json.keep-*"), "在 Demo 槽位新建前弹确认框，写明原文件会另存保留");
            if (no == null)
            {
                Finish("Demo 卡点击后没有出现确认框");
                return;
            }
            no.onClick.Invoke();
            Next(25, "确认框点“否”");
        }

        private static void StepDemoConfirmCancelled(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            string fixture = Path.Combine(Application.dataPath, "Editor/QA/Fixtures/DemoSave_v1_slot.json.txt");
            bool untouched = File.ReadAllBytes(CampaignSaveService.SlotPath(1)).SequenceEqual(File.ReadAllBytes(fixture))
                             && CampaignSaveService.PreservedFiles(1).Length == 0;
            Button restore = FindActiveButton("m_btn_Slot2Action");
            Check(untouched && restore != null, "取消后回到存档列表，Demo 文件逐字节不变、没有产生任何另存文件");
            if (restore == null)
            {
                Finish("坏档的“读取备份”按钮不可点");
                return;
            }
            restore.onClick.Invoke();
            Next(22, "点槽位 3 的“读取备份”");
        }

        private static void StepBackupRestored(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            string card = FindText("m_text_Slot2Info")?.text ?? string.Empty;
            string label = FindText("m_text_Slot2ActionLabel")?.text ?? string.Empty;
            bool kept = CampaignSaveService.PreservedFiles(2).Any(p => p.Contains(".keep-corrupt-"));
            Check(card.Contains("第 1 幕") && card.Contains("种子 20260925") && card.Contains("世界 标准") && label == "读取" && kept,
                $"读取备份后槽位 3 恢复为可读存档（“{card.Replace("\n", " / ")}”，含世界设置摘要（FG0-ARCH-05），按钮“{label}”），截断的主档另存保留");
            Button back = FindActiveButton("m_btn_Back");
            if (back == null)
            {
                Finish("存档列表的“返回”按钮找不到");
                return;
            }
            back.onClick.Invoke();
            Next(40, "返回主菜单");
        }

        // ── FG0-UX-01：主菜单的“全部按键…” ─────────────────────────────

        private static void StepMenuSettings(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Button settings = FindActiveButton("m_btn_Settings");
            if (settings == null)
            {
                Finish("主菜单找不到“设置”按钮");
                return;
            }
            settings.onClick.Invoke();
            Next(41, "点“设置”");
        }

        private static void StepMenuAllKeyBindings(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Button all = FindActiveButton("m_btn_AllKeyBindings");
            string label = all != null ? all.GetComponentInChildren<UnityEngine.UI.Text>(true)?.text : null;
            Check(all != null && label == Localization.GameText.Get("ui.keybind.open_all"), $"设置页有“{label}”按钮");
            if (all == null)
            {
                Finish("设置页没有“全部按键…”按钮");
                return;
            }
            all.onClick.Invoke();
            Next(42, "点“全部按键…”");
        }

        private static void StepMenuAllKeyBindingsClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            if (SessionState.GetInt(K + "KeysSub", 0) == 0)
            {
                KeyBindingsPanelUIToolkit panel = KeyBindingsPanelUIToolkit.Instance;
                VisualElement window = panel != null && panel.Root != null ? panel.Root.Q<VisualElement>("KeyBindingsRoot") : null;
                Check(KeyBindingsPanelUIToolkit.IsOpen && panel.VisibleRows.Count == InputActionCatalog.All.Count
                      && window != null && window.resolvedStyle.display == DisplayStyle.Flex && window.worldBound.width > 400f,
                    $"主菜单打开 UI Toolkit 按键面板：显示 {panel?.VisibleRows.Count} 个动作（窗口宽 {window?.worldBound.width:F0}）");
                CheckNoTextMarkers("主菜单按键面板");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
                SessionState.SetInt(K + "KeysSub", 1);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                return;
            }
            SessionState.SetInt(K + "KeysSub", 0);
            Check(!KeyBindingsPanelUIToolkit.IsOpen, "按 Esc 关闭按键面板");
            Button back = FindActiveButton("m_btn_SettingsBack");
            back?.onClick.Invoke();
            Next(1, "从设置页返回主菜单");
        }

        private static UnityEngine.UI.Text FindText(string name) =>
            Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(t => t.name == name);

        private static void StepClickNew(double inStep)
        {
            Button button = FindActiveButton("m_btn_New");
            if (button == null)
            {
                if (inStep > 150)
                {
                    Finish("150 秒内主菜单没出现（找不到“新建”按钮）");
                }
                return;
            }
            if (inStep < 2)
            {
                return; // 等主菜单稳定一下。
            }
            button.onClick.Invoke();
            Next(2, $"主菜单出现（{inStep:F0} 秒），点“新建”");
        }

        private static void StepPickSlot(double inStep)
        {
            // 有空槽时“新建”直接开新战役（不经过槽位列表）；三槽全满才弹覆盖确认。
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                Next(4, $"新建直接进入归还谷地（{inStep:F0} 秒）");
                return;
            }
            Button confirm = FindActiveButton("m_btn_ConfirmYes");
            if (confirm != null)
            {
                confirm.onClick.Invoke();
                Write("  - 出现覆盖确认，点“是”");
                return;
            }
            Button slot = FindActiveButton("m_btn_Slot0Action");
            if (slot == null)
            {
                if (inStep > 120)
                {
                    Finish("120 秒内既没进入归还谷地、也没出现存档槽列表");
                }
                return;
            }
            slot.onClick.Invoke();
            Next(3, "点存档槽 0");
        }

        private static void StepWaitHome(double inStep)
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                Next(4, $"进入归还谷地（{inStep:F0} 秒）");
                return;
            }
            if (inStep > 120)
            {
                Finish("120 秒内没进入归还谷地");
            }
        }

        private static void StepHomeSoak(double inStep)
        {
            if (inStep < 6)
            {
                return;
            }
            Write("  - 目标条：" + ObjectiveTitle());
            Write($"  - 当前目标：{CampaignObjectiveTracker.CurrentObjectiveId(CampaignSession.Current) ?? "无"}；场景里剪影 {CountNamed("Silhouette")} 个");
            Transform pin = FindNamed("Badge_Objective");
            SpriteRenderer pinRenderer = pin != null ? pin.GetComponent<SpriteRenderer>() : null;
            Check(pinRenderer != null && pinRenderer.enabled && pinRenderer.sprite != null,
                $"定位针贴图已加载并显示（位置 {(pin != null ? pin.position.ToString("F1") : "无")}）");
            CheckNoTextMarkers("归还谷地");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleMissionLog));
            Next(5, "归还谷地运行 6 秒；按任务日志键");
        }

        private static void StepMissionLogOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Write(MissionLogUIToolkit.IsOpen ? "  ✓ 任务日志已打开" : "  ✗ 按键后任务日志没有打开");
            if (!MissionLogUIToolkit.IsOpen)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(6, "按取消键关闭任务日志");
        }

        private static void StepMissionLogClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Write(!MissionLogUIToolkit.IsOpen ? "  ✓ 任务日志已关闭" : "  ✗ 取消键没能关闭任务日志");
            if (MissionLogUIToolkit.IsOpen)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleNotificationCenter));
            Next(30, "按通知中心键");
        }

        // ── FG0-UX-01：通知中心、尚未开放提示、暂停菜单、按键面板、搜索框吞键、Esc 逐层返回 ─────────

        private static void StepNotifyCenterOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            VisualElement center = NotificationHudUIToolkit.Instance?.Root?.Q<VisualElement>("NotifyCenter");
            Check(NotificationHudUIToolkit.CenterOpen && center != null && center.resolvedStyle.display == DisplayStyle.Flex,
                $"通知中心打开（历史 {Notifications.NotificationCenter.History.Count} 条：{string.Join("／", Notifications.NotificationCenter.History.Take(3).Select(e => e.Text))}）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleNotificationCenter));
            Next(31, "再按一次通知中心键");
        }

        private static void StepNotifyCenterClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(!NotificationHudUIToolkit.CenterOpen, "再按一次关闭通知中心");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
            Next(32, "按图鉴键（图鉴由 FG2-FW-05 承接，尚未开放）");
        }

        private static void StepReservedKeyHint(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            var toast = Notifications.NotificationCenter.Toasts.FirstOrDefault(e => e.Type.Id == "feature_locked");
            string shown = string.Join("／", (NotificationHudUIToolkit.Instance?.Root?.Q<VisualElement>("ToastList")?.Query<Label>().ToList()
                ?? new System.Collections.Generic.List<Label>()).Where(l => l.resolvedStyle.display == DisplayStyle.Flex && !string.IsNullOrEmpty(l.text)).Select(l => l.text));
            Check(toast != null && shown.Contains(Localization.GameText.Get("input.action.open_codex.name")),
                $"按尚未开放的键给出提示，不静默：弹出条“{shown}”");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(33, "按 Esc（没有打开的面板 → 暂停菜单）");
        }

        private static void StepPauseMenuOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(PauseMenuUIToolkit.IsOpen && GameRoot.IsWorldPaused && InputRouter.ActiveContext == InputContext.Interface,
                "Esc 打开暂停菜单：世界暂停、输入切到界面上下文");
            // FG0-ARCH-05（FGR-GEN-001）：暂停菜单显示世界种子与世界设置，“复制种子”写进剪贴板。
            CampaignState st = CampaignSession.Current;
            string seed = st?.World != null ? st.World.WorldSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            PauseMenuUIToolkit pm = PauseMenuUIToolkit.Instance;
            Check(pm != null && pm.WorldSeedLabelText.Contains(seed) && pm.WorldSettingsLabelText.Contains("v" + Campaign.WorldGen.WorldGenVersions.Current),
                $"暂停菜单显示“{pm?.WorldSeedLabelText}”“{pm?.WorldSettingsLabelText}”");
            string oldClip = GUIUtility.systemCopyBuffer;
            bool copied = ClickUitk("[PauseMenuHost]", "PauseCopySeed");
            string clip = GUIUtility.systemCopyBuffer;
            GUIUtility.systemCopyBuffer = oldClip;
            Check(copied && clip == seed && pm != null && pm.FeedbackText.Contains(seed), $"点“复制种子”：剪贴板 = {clip}，提示“{pm?.FeedbackText}”");
            Check(ClickUitk("[PauseMenuHost]", "PauseKeyBindings"), "点暂停菜单“按键设置”");
            Next(34, "点“按键设置”");
        }

        private static void StepKeyBindingsFromPause(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(KeyBindingsPanelUIToolkit.IsOpen, "按键面板在暂停菜单上方打开");
            TextField search = KeyBindingsPanelUIToolkit.Instance?.Root?.Q<TextField>("KeyBindingsSearch");
            search?.Focus();
            Next(35, "搜索框获得焦点");
        }

        private static void StepSearchSwallowsHotkeys(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            if (SessionState.GetInt(K + "SearchSub", 0) == 0)
            {
                Check(InputRouter.TextInputFocused, "搜索框获得焦点 → 快捷键整体让位（真实 FocusIn 事件）");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleNotificationCenter));
                SessionState.SetInt(K + "SearchSub", 1);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                return;
            }
            SessionState.SetInt(K + "SearchSub", 0);
            Check(!NotificationHudUIToolkit.CenterOpen, "在搜索框里按通知中心键不会打开通知中心");
            KeyBindingsPanelUIToolkit.Instance?.Root?.Q<TextField>("KeyBindingsSearch")?.Blur();
            Next(36, "搜索框失焦");
        }

        private static void StepEscClosesKeyBindings(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            if (SessionState.GetInt(K + "EscSub", 0) == 0)
            {
                Check(!InputRouter.TextInputFocused, "搜索框失焦后快捷键恢复");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
                SessionState.SetInt(K + "EscSub", 1);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                return;
            }
            SessionState.SetInt(K + "EscSub", 0);
            Check(!KeyBindingsPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen, "Esc 先关最上层的按键面板，暂停菜单还在（FGR-UX-001）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(37, "再按 Esc");
        }

        private static void StepEscClosesPauseMenu(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(!PauseMenuUIToolkit.IsOpen && !GameRoot.IsWorldPaused && InputRouter.ActiveContext != InputContext.Interface,
                "再按 Esc 关闭暂停菜单：世界恢复运行，输入回到游戏上下文");
            CheckNoTextMarkers("UI 基础件（通知 / 暂停菜单 / 按键面板）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            Next(90, "FG0-ARCH-04：按暂停键（战略暂停中也能规划建造）");
        }

        // ── FG0-ARCH-04：建造模式（正式输入：B 打开、点建造栏选建筑、鼠标悬停预览、R 旋转、左键放置、非法位置给原因、
        //    X 拆除模式点虚影取消规划、Esc 退出；全程战略暂停）─────────────────────────────────────────

        private static Vector3 _buildMouse;

        private static void HoverWorld(Vector3 world)
        {
            Camera cam = Camera.main;
            Vector3 screen = cam != null ? cam.WorldToScreenPoint(world) : Vector3.zero;
            _buildMouse = new Vector3(screen.x, screen.y, 0f);
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = _buildMouse });
        }

        private static void PressKeyKeepMouse(KeyCode key)
        {
            InputRouter.DebugSetReader(new ScriptedReader { Key = key, KeyFrame = Time.frameCount + 1, Mouse = _buildMouse });
        }

        private static Campaign.Grid.GridCell? FindBuildCell(CampaignState state, string typeId, Campaign.Grid.GridCell from, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new Campaign.Grid.GridCell(from.X + dx, from.Y + dy);
                        if (Campaign.Grid.HomeGridService.ValidatePlacement(state, typeId, c, 0).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        private static void StepBuildPaused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsPaused, "战略暂停已开启");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
            Next(91, "按建造菜单键（默认 B）打开建造模式");
        }

        private static void StepBuildOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            Check(mode != null && mode.IsOpen && InputRouter.ActiveContext == InputContext.Build && hud != null && hud.PanelVisible && hud.ItemCount >= 1,
                $"建造模式打开：输入上下文 = 建造，建造栏显示 {hud?.ItemCount} 种可放置建筑");
            CheckNoTextMarkers("建造栏");
            Check(ClickUitk("[BuildModeHudHost]", "BuildItem0") && mode != null && mode.SelectedTypeId == Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2,
                $"点建造栏第一项选中发电机（{mode?.SelectedTypeId}）");
            CampaignState state = CampaignSession.Current;
            Campaign.Grid.GridCell? cell = state != null ? FindBuildCell(state, Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2, new Campaign.Grid.GridCell(4, 8), 8) : null;
            if (cell == null)
            {
                Finish("镜头附近找不到能放发电机的空地");
                return;
            }
            SessionState.SetInt(K + "BuildX", cell.Value.X);
            SessionState.SetInt(K + "BuildY", cell.Value.Y);
            SessionState.SetFloat(K + "BuildScrap", state.Scrap);
            HoverWorld(new Vector3(cell.Value.X, 0f, cell.Value.Y));
            Next(92, $"鼠标移到空地 {cell.Value}（虚影跟随）");
        }

        private static Campaign.Grid.GridCell BuildCell() =>
            new Campaign.Grid.GridCell(SessionState.GetInt(K + "BuildX", 0), SessionState.GetInt(K + "BuildY", 0));

        private static void StepBuildHover(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.HasHover && mode.HoverCell == BuildCell() && mode.Preview != null && mode.Preview.Ok,
                $"虚影吸附到格子 {mode?.HoverCell}，预览合法；建造栏状态行：{BuildModeHudUIToolkit.Instance?.StatusLabelText.Replace("\n", " ")}");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.Rotate));
            Next(93, "按旋转键（默认 R）");
        }

        private static void StepBuildRotated(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.GhostRotation == 90 && mode.Preview != null && mode.Preview.Rotation == 90, $"虚影旋转到 {mode?.GhostRotation}°");
            Campaign.Grid.GridCell c = BuildCell();
            ClickWorld(new Vector3(c.X, 0f, c.Y));
            Next(94, "鼠标左键放置");
        }

        private static void StepBuildPlaced(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BuildingRecord placed = state != null ? Campaign.Grid.HomeGridService.BuildingAt(state, BuildCell()) : null;
            WorkOrderRecord order = placed != null ? state.WorkOrders.FirstOrDefault(o => o.TargetId == placed.BuildingId && o.Kind == WorkOrderKind.Build) : null;
            Check(placed != null && placed.ConstructionState == BuildingConstructionState.Planned && Mathf.Approximately(placed.Rotation, 90f)
                  && order != null && order.State == WorkOrderState.Ready && GhostVisual(placed) != null && GhostVisual(placed).localScale.y < 1f,
                $"放下规划中的发电机（朝向 {placed?.Rotation}°，占格 + 画面上立即出现扁平的虚影方块，高 {(placed != null ? GhostVisual(placed)?.localScale.y : null)}），暂停中工单在待分配池（{order?.State}）不开工");
            SessionState.SetString(K + "BuildId", placed?.BuildingId ?? string.Empty);
            HoverWorld(new Vector3(0f, 0f, 3f));
            ClickWorld(new Vector3(0f, 0f, 3f));
            Next(95, "在归还核心旁（通道 / 占用）左键放置");
        }

        private static Transform GhostVisual(BuildingRecord b) =>
            b == null ? null : FindNamed("Building_" + Campaign.Regions.HomeValleyController.LocalKey(b.BuildingId));

        private static void StepBuildRejected(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            int homeBuildings = state?.BuildingRecords?.Count(b => b.RegionId == Campaign.Regions.HomeValleyLayout.RegionId) ?? 0;
            Check(mode != null && !mode.LastResult.Success && mode.StatusIsError && mode.StatusText.Contains("不能放置") && homeBuildings == 8,
                $"非法位置被拒并给出原因：“{mode?.StatusText}”（建筑仍是 {homeBuildings} 座）");
            _buildMouse = Vector3.zero;
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.DemolishMode));
            Next(96, "按拆除模式键（默认 X）");
        }

        private static void StepBuildDemolishMode(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.IsOpen && mode.DemolishMode, "进入拆除模式");
            Campaign.Grid.GridCell c = BuildCell();
            ClickWorld(new Vector3(c.X, 0f, c.Y));
            Next(97, "拆除模式下左键点刚放的虚影（取消规划）");
        }

        private static void StepBuildCancelled(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            string id = SessionState.GetString(K + "BuildId", string.Empty);
            bool gone = state != null && state.BuildingRecords.All(b => b.BuildingId != id);
            Check(gone && Mathf.Approximately(state.Scrap, SessionState.GetFloat(K + "BuildScrap", -1f)) && Campaign.Grid.HomeGridService.BuildingAt(state, BuildCell()) == null
                  && FindNamed("Building_" + Campaign.Regions.HomeValleyController.LocalKey(id)) == null,
                $"取消规划：虚影方块消失、占格释放、废料全额退回（{state?.Scrap}）");
            BuildingRecord warehouse = state?.BuildingRecords?.FirstOrDefault(b => b.BuildingId == WarehouseBuildingId);
            Vector2 wp = warehouse != null ? warehouse.Position : Vector2.zero;
            ClickWorld(new Vector3(wp.x, 0f, wp.y));
            Next(100, "拆除模式下左键点仓库（本局无法重建，应被拒绝）");
        }

        private const string WarehouseBuildingId = Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeWarehouse;

        private static void StepBuildDemolishRefused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            bool kept = state != null && state.BuildingRecords.Any(b => b.BuildingId == WarehouseBuildingId)
                        && !Campaign.Grid.HomeGridService.IsMarkedForDemolish(state, WarehouseBuildingId);
            Check(mode != null && mode.IsOpen && mode.DemolishMode && mode.HoverBuildingId == WarehouseBuildingId && !mode.LastResult.Success
                  && mode.StatusIsError && mode.StatusText.Contains("无法重建") && !mode.StatusText.StartsWith("不能放置") && !UiConfirmDialog.IsOpen && kept,
                $"拆除模式点仓库：不弹确认框，直接拒绝（“{mode?.StatusText}”），仓库保留、未标记拆除（防软锁）");
            Next(110, "FG0-ARCH-05：检查地形叠加层（按区块跟随镜头）");
        }

        // ── FG0-ARCH-05：地形叠加层按区块跟随镜头；区块由工作线程流式生成，主线程不卡；镜头平移跨过区块边界后新露出的区块补齐 ──

        private sealed class HeldReader : IInputReader
        {
            public KeyCode Held;
            public Vector3 Mouse;

            public bool GetKey(KeyCode key) => key == Held;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
        }

        private static void StepWorldPanStart(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Campaign.Regions.WorldTerrainOverlay ov = mode?.TerrainOverlay;
            Campaign.WorldGen.WorldChunkStreamer streamer = state != null ? Campaign.Grid.HomeGridService.Streamer(state) : null;
            int r = Campaign.Grid.GridContent.TuningInt("world.view_radius_chunks");
            int tiles = (2 * r + 1) * (2 * r + 1);
            Check(ov != null && ov.TileCount == tiles && ov.PlaceholderCount == 0 && BuildModeHudUIToolkit.Instance != null && !BuildModeHudUIToolkit.Instance.GeneratingVisible
                  && streamer != null && streamer.UsesKernel && streamer.TotalIntegrated > 0,
                $"地形叠加层按区块显示 {ov?.TileCount}/{tiles} 块、没有“生成中”占位（家园进入后已由工作线程预生成 {streamer?.TotalIntegrated} 块）；世界生成器 = {state?.Grid?.TerrainSourceId} v{state?.World?.GeneratorVersion}");
            SessionState.SetInt(K + "PanStartChunk", ov != null ? ov.WindowChunkX : int.MinValue);
            streamer?.ResetMetrics();
            InputRouter.DebugSetReader(new HeldReader { Held = GameSettings.KeyBindings.GetKey(GameActionId.StrategyPanRight), Mouse = _buildMouse });
            Next(111, "按住镜头右移键（默认 →）平移镜头");
        }

        private static void StepWorldPanMoved(double inStep)
        {
            if (inStep < 2.0)
            {
                return;
            }
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = _buildMouse });
            Next(112, "松开右移键，等新露出的区块补齐");
        }

        private static void StepWorldPanSettled(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Campaign.Regions.WorldTerrainOverlay ov = mode?.TerrainOverlay;
            Campaign.WorldGen.WorldChunkStreamer streamer = state != null ? Campaign.Grid.HomeGridService.Streamer(state) : null;
            int startChunk = SessionState.GetInt(K + "PanStartChunk", int.MinValue);
            Camera cam = Camera.main;
            Check(ov != null && ov.WindowChunkX > startChunk && ov.PlaceholderCount == 0 && BuildModeHudUIToolkit.Instance != null && !BuildModeHudUIToolkit.Instance.GeneratingVisible,
                $"镜头右移（x = {cam?.transform.position.x:F1}）跨过区块边界：叠加层窗口从区块 {startChunk} 跟到 {ov?.WindowChunkX}，新露出的区块已补齐、没有残留占位");
            Check(streamer != null && streamer.MaxTickMs < 16.0,
                $"平移期间流式加载主线程每帧最多 {streamer?.MaxTickMs:F3} ms（真实 Play，影子工程 batchmode）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(98, "按 Esc 退出建造模式");
        }

        private static void StepBuildEsc(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && !mode.IsOpen && !PauseMenuUIToolkit.IsOpen && InputRouter.ActiveContext == InputContext.Strategy
                  && FindNamed("[BuildMode]") == null && BuildModeHudUIToolkit.Instance != null && !BuildModeHudUIToolkit.Instance.PanelVisible,
                "Esc 退出建造模式（不会顺带打开暂停菜单），输入回到战略上下文，虚影 / 叠加层已释放");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            Next(99, "按暂停键恢复运行");
        }

        private static void StepBuildResume(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsPaused, "战略暂停已解除");
            InputRouter.DebugSetReader(null);
            Campaign.Regions.HomeValleyMachineMarker hauler = Object.FindObjectsByType<Campaign.Regions.HomeValleyMachineMarker>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .OrderBy(m => m.LogicId).FirstOrDefault();
            if (hauler == null)
            {
                Finish("归还谷地里找不到可点选的机器");
                return;
            }
            ClickWorld(hauler.transform.position);
            SessionState.SetInt(K + "Worker", hauler.LogicId);
            Next(10, $"鼠标左键点选机器 #{hauler.LogicId}");
        }

        /// <summary>点一个 UI Toolkit 按钮：走按钮自己的 Clickable（与鼠标点击同一回调），不绕到控制器私有方法。</summary>
        private static bool ClickUitk(string hostName, string buttonName)
        {
            GameObject host = GameObject.Find(hostName);
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            UnityEngine.UIElements.Button b = doc?.rootVisualElement?.Q<UnityEngine.UIElements.Button>(buttonName);
            if (b == null || b.clickable == null)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private static void StepClickGenerator(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Transform generator = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            if (generator == null)
            {
                Finish("场景里找不到发电机");
                return;
            }
            ClickWorld(generator.position);
            Next(11, "鼠标左键点受损的发电机（下令修复）");
        }

        private static void StepRepairOrdered(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool ordered = state?.WorkOrders != null && state.WorkOrders.Any(o =>
                o.Kind == WorkOrderKind.Repair && o.TargetId == Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator
                && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed);
            Check(ordered, "点选机器再点发电机 → 修复工单已下达");
            if (!ordered)
            {
                Finish("鼠标下令修复没有生效");
                return;
            }
            // FG0-DATA-01：工单目标名 = fg.TbBuilding.nameKey → GameText（表经 Play 模式的资源系统加载）。
            string generatorLabel = Campaign.Content.MechanicalContentFacade.ResolveWorkOrderTargetLabel(
                Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            Check(generatorLabel == Localization.GameText.Get("building.generator.name") && !Localization.GameText.ContainsMarker(generatorLabel)
                  && generatorLabel != Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator,
                $"工单目标名走文本键：“{generatorLabel}”（Play 模式下 fg 表已加载）");
            // FG0-ARCH-04 复审：机器仍选中、身上有在办的修复工单时，按 B 进建造模式再右键退出——
            // 退出那一下不能穿透到点选逻辑、把机器的修复工单取消掉。
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.SelectedMachineLogicId == SessionState.GetInt(K + "Worker", -1),
                $"下令修复后机器仍被选中（#{GameRoot.HomeValley?.SelectedMachineLogicId}）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
            Next(17, "机器选中且有在办修复工单时，按建造菜单键（默认 B）");
        }

        private static WorkOrderRecord ActiveRepairOrder(CampaignState state) =>
            state?.WorkOrders?.LastOrDefault(o => o.Kind == WorkOrderKind.Repair
                && o.TargetId == Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);

        private static void StepBuildOpenedWithOrder(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null && !mode.DemolishMode, "建造模式打开（未选建筑、不在拆除模式）");
            Transform core = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeCore);
            RightClickWorld(core != null ? core.position + new Vector3(6f, 0f, 6f) : Vector3.zero);
            Next(18, "右键（没有选中建筑时右键 = 退出建造模式）");
        }

        private static void StepBuildRightClickExit(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            WorkOrderRecord order = ActiveRepairOrder(CampaignSession.Current);
            Check(mode != null && !mode.IsOpen && InputRouter.ActiveContext == InputContext.Strategy,
                "右键退出建造模式，输入回到战略上下文");
            Check(order != null && order.State != WorkOrderState.Cancelled && order.State != WorkOrderState.Failed
                  && GameRoot.HomeValley != null && GameRoot.HomeValley.SelectedMachineLogicId == SessionState.GetInt(K + "Worker", -1),
                $"退出建造模式的右键没有穿透：机器仍选中，修复工单未被取消（{order?.State}）");
            InputRouter.DebugSetReader(null);
            Next(12, "等机器走过去修好发电机");
        }

        private static void StepRepairDone(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord generator = state?.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == Campaign.Regions.HomeValleyLayout.RegionId && b.BuildingTypeId == Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            if (generator == null || generator.ConstructionState != BuildingConstructionState.Operational)
            {
                if (inStep > 120)
                {
                    Finish("120 秒内发电机没修好");
                }
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (SessionState.GetFloat(K + "RepairedAt", 0f) <= 0f)
            {
                SessionState.SetFloat(K + "RepairedAt", (float)now);
                Write($"  - 发电机修好（下令后 {inStep:F0} 秒）");
                return;
            }
            if (now - SessionState.GetFloat(K + "RepairedAt", 0f) < 1.5)
            {
                return; // 目标 0.5 秒一算、目标条 0.25 秒一刷：修好之后等 1.5 秒再读。
            }
            string title = ObjectiveTitle();
            Write($"  - 修好 1.5 秒后目标条：{title}");
            Check(title.Contains("目标 2/10"), "修好发电机后目标条跳到目标 2/10");
            CheckNoTextMarkers("发电机修好后");
            Transform station = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation);
            if (station == null)
            {
                Finish("场景里找不到装配站");
                return;
            }
            ClickWorld(station.position);
            Next(13, "鼠标左键点装配站（打开生产面板）");
        }

        private static void StepFactoryOpen(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            bool open = GameRoot.HomeValley != null && GameRoot.HomeValley.IsFactoryPanelOpen;
            Check(open, "点装配站打开生产面板");
            string hint = LabelText("[HomeValleyFactoryHost]", "ProduceHintLabel");
            Write($"  - 生产面板提示行：{hint}");
            bool hoverUnlocked = Campaign.Regions.HomeValleyFactory.IsBlueprintUnlocked(CampaignSession.Current,
                Campaign.Regions.HomeValleyLayout.BlueprintHoverId);
            Check(!open || (hoverUnlocked ? !hint.Contains("先让解析台通电") : hint.Contains("先让解析台通电")),
                hoverUnlocked ? "维修机已解锁：提示行不再显示解锁条件" : "维修机未解锁：提示行写明解锁条件");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(15, "生产面板开着时按 Esc");
        }

        private static void StepFactoryEscClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsFactoryPanelOpen && !PauseMenuUIToolkit.IsOpen && !GameRoot.IsWorldPaused,
                "生产面板开着按 Esc：先关生产面板，暂停菜单没有打开（FGR-UX-001 先关最上层面板）");
            Transform station = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation);
            if (station != null)
            {
                ClickWorld(station.position);
            }
            Next(16, "再点装配站（重新打开生产面板）");
        }

        private static void StepFactoryReopened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsFactoryPanelOpen, "再点装配站重新打开生产面板");
            Transform station = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAssemblyStation);
            if (station != null)
            {
                ClickWorld(station.position);
            }
            Next(14, "再点一次装配站（关闭生产面板）");
        }

        private static void StepFactoryClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsFactoryPanelOpen, "再点装配站关闭生产面板");
            Check(ClickUitk("[HomeValleyCircuitBoardHost]", "EntryToggleButton"), "点“蓝图编辑器”入口");
            Next(60, "打开电路板（蓝图编辑器）");
        }

        // ── FG0-UX-01 审查修复：Demo 文本框打字时快捷键让位 ─────────────────────

        private static TextField CircuitNameField()
        {
            GameObject host = GameObject.Find("[HomeValleyCircuitBoardHost]");
            return host != null ? host.GetComponent<UIDocument>()?.rootVisualElement?.Q<TextField>("NewBlueprintNameField") : null;
        }

        private static void StepCircuitOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsCircuitBoardPanelOpen, "电路板面板打开");
            TextField field = CircuitNameField();
            field?.Focus();
            SessionState.SetInt(K + "NotifyBefore", Notifications.NotificationCenter.History.Count);
            Next(61, "蓝图命名框获得焦点");
        }

        private static void StepTypingSpace(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            if (SessionState.GetInt(K + "TypeSub", 0) == 0)
            {
                Check(InputRouter.TextInputFocused && InputRouter.KeyboardSuppressed, "蓝图命名框拿到焦点 → 全局文本焦点探针判定在打字，快捷键整体让位");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
                SessionState.SetInt(K + "TypeSub", 1);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                return;
            }
            SessionState.SetInt(K + "TypeSub", 0);
            Check(!GameRoot.IsWorldPaused, "在命名框里按 Space（暂停键）不会暂停世界");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
            Next(62, "在命名框里按图鉴键（尚未开放的动作）");
        }

        private static void StepTypingReserved(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int before = SessionState.GetInt(K + "NotifyBefore", 0);
            Check(Notifications.NotificationCenter.History.Count == before && Notifications.NotificationCenter.Toasts.All(e => e.Type.Id != "feature_locked"),
                "在命名框里按尚未开放的键不弹“后续版本开放”，也不产生任何通知");
            CircuitNameField()?.Blur();
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(63, "命名框失焦后按 Esc");
        }

        private static void StepCircuitEscClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsCircuitBoardPanelOpen && !PauseMenuUIToolkit.IsOpen && !InputRouter.TextInputFocused,
                "失焦后快捷键恢复；Esc 先关电路板面板，暂停菜单没有打开");
            InputRouter.DebugSetReader(null);

            int[] roster = HomeMachines();
            UnlockLikeDeparture(Campaign.Regions.FracturedCityRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            // FG0-ARCH-01：派遣不再退出家园——与正式出发事务（ExpeditionDepartureService.TryDepart）同一个调用：只载入远征地点，镜头飞过去。
            SessionState.SetString(K + "TicksAtDispatch", GameClock.Ticks.ToString());
            GameRoot.StartFracturedCity(roster);
            Next(7, $"测试捷径：标记破碎都市可出征并记一次出征，按出征同样的调用顺序派遣（{roster.Length} 台机器；家园不退出）");
        }

        private static void StepRuins(double inStep)
        {
            if (inStep < 8)
            {
                return;
            }
            bool active = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive;
            Write($"  - 破碎都市激活：{active}；目标条：{ObjectiveTitle()}；剪影 {CountNamed("Silhouette")} 个");
            string item = LabelText("[ObjectiveHudHost]", "ObjectiveItemText1");
            string note = LabelText("[ObjectiveHudHost]", "ObjectiveNote");
            Write($"  - 目标条第 2 项：{item}；备注行：{note}");
            Check(item.Contains("：在地面") || item.Contains("：已装上") || item.Contains("：摧毁监听节点后掉落"), "远征中目标条写出关键物现状或获得方式");
            Write($"  - 世界特效活动中 {VfxActive()} 个");
            CheckEnemiesFromTable(Campaign.Regions.FracturedCityLayout.RegionId, Campaign.Content.EnemyCatalog.ScoutId);
            CheckNoTextMarkers("破碎都市");
            Next(120, "FG0-ARCH-01：整个世界同时运行——远征进行中，家园没有退出");
        }

        // ── FG0-ARCH-01：整个世界同时运行、统一时钟、全局镜头（世界时间条、Tab / Home / 4 / Space 走正式输入）─────────

        private static readonly Vector3 OffScreen = new Vector3(-10f, -10f, 0f);

        /// <summary>模拟按下一次键，光标在窗口外（不触发边缘推屏）。</summary>
        private static void PressKeyOffScreen(KeyCode key)
        {
            InputRouter.DebugSetReader(new ScriptedReader { Key = key, KeyFrame = Time.frameCount + 1, Mouse = OffScreen });
        }

        private static bool WorldBarReady(out WorldBarHudUIToolkit hud)
        {
            hud = WorldBarHudUIToolkit.Instance;
            if (hud == null || !hud.IsReady)
            {
                return false;
            }
            hud.Refresh();
            return hud.BarVisible;
        }

        private static Vector2 CameraFocus() => new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);

        private static void StepWorldHomeKeepsRunning(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            long atDispatch = long.TryParse(SessionState.GetString(K + "TicksAtDispatch", "0"), out long t) ? t : 0;
            bool homeRunning = GameRoot.HomeValley != null && GameRoot.HomeValley.IsLoaded && !GameRoot.HomeValley.IsActive;
            bool ruinsObserved = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive;
            GameObject homeRoot = GameObject.Find("[HomeValley]");
            Check(homeRunning && ruinsObserved && GameClock.Ticks > atDispatch && homeRoot == null,
                $"派遣后家园仍在运行（已载入、不被观察、表现对象隐藏）、镜头在破碎都市；统一时钟 {atDispatch}→{GameClock.Ticks} 步");
            if (!WorldBarReady(out WorldBarHudUIToolkit hud))
            {
                if (inStep > 10)
                {
                    Finish("世界时间条没有出现");
                }
                return;
            }
            Write($"  - 世界时间条：{hud.DayTimeText}；状态 {hud.StatusText}；关注点 {string.Join(" / ", hud.FocusButtons.Where(b => IsDisplayed(b)).Select(b => b.text))}");
            Check(hud.DayTimeText.StartsWith("第 ") && hud.FocusButtonCount >= 2, "世界时间条显示“第 N 日 HH:MM”与关注点（家园、远征）");
            CheckNoTextMarkers("世界时间条");
            // 光标移到窗口外：这一段要核对镜头落点，不能让屏幕边缘推屏把镜头推走（batchmode 下光标默认在左下角 = 边缘）。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen });
            Check(ClickUitk("[WorldBarHost]", "WorldFocus0"), "点关注点“家园”");
            Next(121, "点世界时间条的关注点“家园”：镜头飞回家园（远征继续运行）");
        }

        private static void StepWorldFlownHome(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            bool homeObserved = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            bool ruinsRunning = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded && !GameRoot.FracturedCity.IsActive;
            GameObject homeRoot = GameObject.Find("[HomeValley]");
            GameObject ruinsRoot = GameObject.Find("[FracturedCityRoot]");
            Check(homeObserved && ruinsRunning && homeRoot != null && ruinsRoot == null && Vector2.Distance(CameraFocus(), Campaign.Regions.HomeValleyLayout.Core.Position) < 1f,
                $"镜头回到家园（焦点 {CameraFocus()}）：家园表现对象显示、破碎都市表现对象隐藏但仍在运行");
            Check(WorldPlanetView.TerrainShown, "普通视角显示镜头附近区块的地貌层（DEBT-FG0ARCH05-02）");
            // 测试捷径：派一支突袭（突袭导演属于 FG6-DEF-04；这里只验证行进中的队伍与镜头飞跃）。
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(CampaignSession.Current, "silent", 6, out string failure);
            Check(raid != null, $"测试捷径：从规划层领地派出一支突袭（{failure ?? raid?.GroupId}）");
            SessionState.SetString(K + "RaidId", raid?.GroupId ?? string.Empty);
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus));
            Next(122, "按“切换关注点”（Tab）");
        }

        private static void StepWorldTabToExpedition(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            Check(GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive && WorldView.LastFocusTargetId == "site:" + Campaign.Regions.FracturedCityLayout.RegionId,
                $"Tab：镜头从家园飞到远征地点（关注点 {WorldView.LastFocusTargetId}）");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus));
            SessionState.SetInt(K + "FlightMaxPlaceholder", 0);
            SessionState.SetFloat(K + "FlightMaxFrameMs", 0f);
            SessionState.SetInt(K + "FlightFrames", 0);
            SessionState.SetInt(K + "FlightLastFrame", -1);
            SessionState.SetBool(K + "RaidChecked", false);
            Next(123, "再按 Tab");
        }

        /// <summary>远距离飞跃期间逐帧采样（DEBT-FG0ARCH05-01 ①）：地貌层“生成中”占位块数的峰值、真实帧耗时峰值。</summary>
        private static void SampleFlight()
        {
            int frame = Time.frameCount;
            if (SessionState.GetInt(K + "FlightLastFrame", -1) == frame)
            {
                return;
            }
            SessionState.SetInt(K + "FlightLastFrame", frame);
            SessionState.SetInt(K + "FlightFrames", SessionState.GetInt(K + "FlightFrames", 0) + 1);
            Campaign.Regions.WorldTerrainOverlay terrain = WorldPlanetView.Terrain;
            int placeholders = terrain != null ? terrain.PlaceholderCount : 0;
            if (placeholders > SessionState.GetInt(K + "FlightMaxPlaceholder", 0))
            {
                SessionState.SetInt(K + "FlightMaxPlaceholder", placeholders);
            }
            float ms = Time.unscaledDeltaTime * 1000f;
            if (SessionState.GetInt(K + "FlightFrames", 0) > 2 && ms > SessionState.GetFloat(K + "FlightMaxFrameMs", 0f))
            {
                SessionState.SetFloat(K + "FlightMaxFrameMs", ms); // 按键那一两帧不计（输入注入本身的编辑器开销）
            }
        }

        private static void StepWorldTabToRaid(double inStep)
        {
            SampleFlight();
            if (inStep < 1.2)
            {
                return;
            }
            string raidId = SessionState.GetString(K + "RaidId", string.Empty);
            TransitGroupRecord raid = WorldTransitSystem.Find(CampaignSession.Current, raidId);
            if (!SessionState.GetBool(K + "RaidChecked", false))
            {
                SessionState.SetBool(K + "RaidChecked", true);
                bool marker = WorldPlanetView.TryGetMarkerPosition(raidId, out Vector3 markerPos) && GameObject.Find("RaidMarker_" + raidId) != null;
                Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && WorldView.LastFocusTargetId == raidId && raid != null
                      && Vector2.Distance(CameraFocus(), WorldTransitSystem.Position(raid)) < 3f && marker,
                    $"再按 Tab：镜头飞到行进中的突袭（焦点 {CameraFocus()}，突袭在 {WorldTransitSystem.Position(raid)}），镜头附近生成突袭标记（{markerPos}）");
            }
            // DEBT-FG0ARCH05-01 ①：远距离飞跃露出没生成的区块——先显示“生成中”占位，随后补齐，主线程不卡。
            Campaign.Regions.WorldTerrainOverlay terrain = WorldPlanetView.Terrain;
            int now = terrain != null ? terrain.PlaceholderCount : -1;
            if (now != 0 && inStep < 10)
            {
                return;
            }
            int maxPlaceholder = SessionState.GetInt(K + "FlightMaxPlaceholder", 0);
            float maxFrameMs = SessionState.GetFloat(K + "FlightMaxFrameMs", 0f);
            int frames = SessionState.GetInt(K + "FlightFrames", 0);
            Write($"  - 飞跃采样：{frames} 帧，“生成中”占位峰值 {maxPlaceholder} 块，现在 {now} 块；真实帧耗时峰值 {maxFrameMs:F0} ms" +
                  $"（活跃区块 {WorldSimulation.ActiveChunkCount}，窗口 ({terrain?.WindowChunkX},{terrain?.WindowChunkY}) 半径 {terrain?.WindowRadius}）");
            Check(maxPlaceholder > 0 && now == 0 && maxFrameMs < 250f,
                $"远距离飞跃（约 {Vector2.Distance(Campaign.Regions.HomeValleyLayout.Core.Position, CameraFocus()):F0} 格）：新露出的区块先显示“生成中”占位（峰值 {maxPlaceholder} 块），" +
                $"{inStep:F1} 秒内补齐（剩 {now} 块）；主线程单帧峰值 {maxFrameMs:F0} ms（上限 250 ms）");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.SpeedTriple));
            Next(124, "按 4（3x）");
        }

        private static void StepWorldTriple(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            WorldBarReady(out WorldBarHudUIToolkit hud);
            Check(Mathf.Approximately(GameClock.Speed, 3f) && hud != null && hud.StatusText.Contains("3x"),
                $"4 键：整个世界 3x（状态“{hud?.StatusText}”）");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            Next(125, "按 Space 暂停整个世界");
        }

        private static void StepWorldPaused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            string raidId = SessionState.GetString(K + "RaidId", string.Empty);
            TransitGroupRecord raid = WorldTransitSystem.Find(CampaignSession.Current, raidId);
            Check(GameClock.Paused && GameRoot.IsWorldPaused, "Space：整个世界暂停");
            SessionState.SetString(K + "PausedTicks", GameClock.Ticks.ToString());
            SessionState.SetString(K + "PausedRaid", raid != null ? raid.PosX.ToString("R") : "none");
            Next(126, "暂停中等 1.5 秒");
        }

        private static void StepWorldPausedHeld(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            string raidId = SessionState.GetString(K + "RaidId", string.Empty);
            TransitGroupRecord raid = WorldTransitSystem.Find(CampaignSession.Current, raidId);
            Check(GameClock.Ticks.ToString() == SessionState.GetString(K + "PausedTicks", "") && raid != null
                  && raid.PosX.ToString("R") == SessionState.GetString(K + "PausedRaid", ""),
                $"暂停期间时间轴不动（{GameClock.Ticks} 步），行进中的突袭也停住");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            SessionState.SetInt(K + "UnpauseFrame", Time.frameCount);
            Next(127, "按 Space 继续");
        }

        private static void StepWorldHomeKey(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            if (!SessionState.GetBool(K + "HomeKeyPressed", false))
            {
                Check(!GameClock.Paused, "Space：整个世界继续");
                SessionState.SetBool(K + "HomeKeyPressed", true);
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore));
                return;
            }
            if (inStep < 1.5)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && Vector2.Distance(CameraFocus(), Campaign.Regions.HomeValleyLayout.Core.Position) < 1f,
                $"Home：镜头回到归还核心（焦点 {CameraFocus()}）");
            SessionState.SetInt(K + "Shuttles", 0);
            Next(128, "在家园与远征之间来回飞跃（Tab 依次切换）");
        }

        private static void StepWorldShuttle(double inStep)
        {
            int n = SessionState.GetInt(K + "Shuttles", 0);
            if (inStep < 0.8 * (n + 1))
            {
                return;
            }
            if (n < 6)
            {
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus));
                SessionState.SetInt(K + "Shuttles", n + 1);
                return;
            }
            bool stillBoth = GameRoot.HomeValley != null && GameRoot.HomeValley.IsLoaded && GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded;
            Check(stillBoth && WorldView.ObserveSwitchCount >= 6, $"来回飞跃 6 次后家园与远征都仍在运行（跨地点切换累计 {WorldView.ObserveSwitchCount} 次）");
            CheckNoTextMarkers("世界时间条（飞跃后）");
            StrategyClock.SetSpeed(1f);

            int[] roster = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).Select(m => m.LogicId).ToArray();
            GameRoot.FracturedCity?.Exit(evacuateSuccess: false);
            Campaign.Regions.FoundryOutpostRegion.EnsureRegionRecordSeeded(CampaignSession.Current);
            UnlockLikeDeparture(Campaign.Regions.FoundryOutpostRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            GameRoot.StartFoundryOutpost(roster);
            Next(8, "测试捷径：暂离破碎都市（镜头自动回家园），标记铸造前哨外围可出征，派遣");
        }

        private static void StepFoundry(double inStep)
        {
            if (inStep >= 3 && !SessionState.GetBool(K + "VfxRaised", false))
            {
                SessionState.SetBool(K + "VfxRaised", true);
                CampaignState state = CampaignSession.Current;
                RegionEnemyRecord enemy = state?.RegionEnemies?.FirstOrDefault(e =>
                    e != null && e.IsAlive && e.RegionId == Campaign.Regions.FoundryOutpostLayout.RegionId);
                Vector2 at = enemy != null ? enemy.Position : Vector2.zero;
                Campaign.Feedback.FeedbackCues.RaiseAt(Campaign.Feedback.FeedbackCueId.EnemyHit, at);
                Campaign.Feedback.FeedbackCues.RaiseAt(Campaign.Feedback.FeedbackCueId.ReactionMeltOverload, at);
                SessionState.SetInt(K + "VfxFrame", Time.frameCount);
                Write($"  - 在敌人位置 {at} 报一次命中 + 熔穿过载时刻");
                return;
            }
            if (SessionState.GetBool(K + "VfxRaised", false) && !SessionState.GetBool(K + "VfxChecked", false)
                && Time.frameCount >= SessionState.GetInt(K + "VfxFrame", 0) + 2)
            {
                SessionState.SetBool(K + "VfxChecked", true);
                int activeVfx = VfxActive();
                Check(activeVfx >= 2, $"命中/反应时刻生成了世界特效（活动 {activeVfx} 个，贴图运行时加载）");
                return;
            }
            if (inStep < 8)
            {
                return;
            }
            bool active = GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive;
            Write($"  - 铸造前哨激活：{active}；目标条：{ObjectiveTitle()}；剪影 {CountNamed("Silhouette")} 个；世界特效活动中 {VfxActive()} 个");
            CheckEnemiesFromTable(Campaign.Regions.FoundryOutpostLayout.RegionId, Campaign.Content.EnemyCatalog.ArmorBotId);
            CheckNoTextMarkers("铸造前哨");
            GameRoot.FoundryOutpost?.Exit(evacuateSuccess: false);
            GameRoot.ResumeHomeValley();
            Next(9, "回到归还谷地");
        }

        private static void StepBackHome(double inStep)
        {
            if (inStep < 4)
            {
                return;
            }
            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            Write($"  - 归还谷地激活：{active}");
            CheckNoTextMarkers("回到归还谷地");
            // FG0-SAVE-01：正式流程里的自动存档写成 v2（带卡片头），槽位卡可读。
            CampaignSlotMetadata saved = CampaignSaveService.GetSlotMetadata(CampaignSession.ActiveSlotIndex);
            Check(saved.State == CampaignSlotState.Ready && saved.SchemaVersion == CampaignSaveService.CurrentSchemaVersion && saved.ProductVersion == "0.2"
                  && saved.WorldSeed == CampaignSession.Current.World.WorldSeed,
                $"正式流程的存档：槽位 {CampaignSession.ActiveSlotIndex + 1} 为 v{saved.SchemaVersion}、游戏版本 {saved.ProductVersion}、种子 {saved.WorldSeed}");
            if (!active)
            {
                Finish("回不到归还谷地");
                return;
            }
            SessionState.SetInt(K + "PlayedSlot", CampaignSession.ActiveSlotIndex);
            LayBelts();
        }

        // ── FG0-ARCH-02：传送带内核（正式放置工具属于 FG3-LOG-01 / 03，这里用测试捷径铺设，验证内核在真实 Play 帧里运转、渲染、存读档）──

        private const int SmokeSinkPort = 3;

        /// <summary>测试捷径：经正式入口 BeltNetworkService.TryPlace（含格网校验）在核心附近按规则找空地，铺一条带输出 / 输入端口的直线 + 一个装了物品的环。</summary>
        private static void LayBelts()
        {
            CampaignState s = CampaignSession.Current;
            Check(BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, s), "传送带内核随家园载入（BeltNetworkService 绑定当前战役）");
            GridCell core = HomeGridService.CorePivot(s);
            GridCell origin = default;
            bool found = false;
            for (int r = 6; r <= 30 && !found; r++)
            {
                for (int dy = -r; dy <= r && !found; dy++)
                {
                    for (int dx = -r; dx <= r && !found; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        bool ok = true;
                        for (int y = 0; y < 6 && ok; y++)
                        {
                            for (int x = 0; x < 12 && ok; x++)
                            {
                                ok = HomeGridService.ValidateBeltCell(s, new GridCell(core.X + dx + x, core.Y + dy + y)).Ok;
                            }
                        }
                        if (ok)
                        {
                            origin = new GridCell(core.X + dx, core.Y + dy);
                            found = true;
                        }
                    }
                }
            }
            if (!found)
            {
                Finish("核心附近找不到 12×6 的空地铺传送带");
                return;
            }
            int placed = 0;
            string firstFail = null;
            void Place(int x, int y, BeltDir d)
            {
                BeltOpResult r = BeltNetworkService.TryPlace(s, new GridCell(origin.X + x, origin.Y + y), d, 0);
                if (r.Ok)
                {
                    placed++;
                }
                else
                {
                    firstFail ??= r.Describe();
                }
            }
            for (int x = 0; x < 10; x++)
            {
                Place(x, 0, BeltDir.East);
            }
            // 4×4 的环（12 格）。
            for (int x = 6; x < 9; x++)
            {
                Place(x, 2, BeltDir.East);
            }
            for (int y = 2; y < 5; y++)
            {
                Place(9, y, BeltDir.North);
            }
            for (int x = 9; x > 6; x--)
            {
                Place(x, 5, BeltDir.West);
            }
            for (int y = 5; y > 2; y--)
            {
                Place(6, y, BeltDir.South);
            }
            BeltNetworkService.TryAddSource(s, 1, origin, 1, 1, BeltConst.Unlimited);
            BeltNetworkService.TryAddSink(s, SmokeSinkPort, new GridCell(origin.X + 10, origin.Y), BeltConst.Unlimited, 0);
            BeltKernel k = BeltNetworkService.Kernel;
            int[,] ring = { { 6, 2 }, { 7, 2 }, { 8, 2 }, { 9, 2 }, { 9, 3 }, { 9, 4 }, { 9, 5 }, { 8, 5 }, { 7, 5 }, { 6, 5 }, { 6, 4 }, { 6, 3 } };
            for (int i = 0; i < ring.GetLength(0); i++)
            {
                k.InsertItemAt(origin.X + ring[i, 0], origin.Y + ring[i, 1], 5000, (ushort)(20 + i));
            }
            SessionState.SetString(K + "BeltRing", RingSignature(k, origin.X, origin.Y));
            SessionState.SetInt(K + "BeltCells", placed);
            SessionState.SetInt(K + "BeltX", origin.X);
            SessionState.SetInt(K + "BeltY", origin.Y);
            SessionState.SetString(K + "BeltHash", k.ComputeStateHash().ToString());
            SessionState.SetInt(K + "BeltSteps", (int)k.StepIndex);
            SessionState.SetInt(K + "BeltRenders", BeltNetworkService.RenderCalls);
            Check(placed == 22 && firstFail == null && k.ItemCount == 12,
                $"测试捷径：经正式入口在核心附近（原点 {origin}，按规则搜索，不写死坐标）铺 {placed} 格传送带（直线 + 环，环上 {k.ItemCount} 件）{(firstFail != null ? "；失败：" + firstFail : string.Empty)}");
            Next(130, "FG0-ARCH-02：传送带已铺好，等真实 Play 帧推进");
        }

        /// <summary>环（12 格）的签名“件数;每格的物品编号@位置”：件数不变、签名变化 = 环在转且没丢件
        /// （逐格记物品编号，整格平移也能看出来——只比位置和的话，恰好转过整数格时会误判为没动）。</summary>
        private static string RingSignature(BeltKernel k, int ox, int oy)
        {
            int[,] ring = { { 6, 2 }, { 7, 2 }, { 8, 2 }, { 9, 2 }, { 9, 3 }, { 9, 4 }, { 9, 5 }, { 8, 5 }, { 7, 5 }, { 6, 5 }, { 6, 4 }, { 6, 3 } };
            int count = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ring.GetLength(0); i++)
            {
                k.TryGetCellInfo(ox + ring[i, 0], oy + ring[i, 1], out BeltCellInfo c);
                count += c.Count;
                for (int s = 0; s < c.Count; s++)
                {
                    sb.Append(i).Append(':').Append(c.ItemAt(s)).Append('@').Append(c.PosAt(s)).Append(',');
                }
            }
            return count + ";" + sb;
        }

        private static void StepBeltsLaid(double inStep)
        {
            if (inStep < 4)
            {
                return;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            int steps = (int)k.StepIndex - SessionState.GetInt(K + "BeltSteps", 0);
            k.TryGetPortInfo(1, out BeltPortInfo src);
            BeltLedger l = k.Ledger;
            int renders = BeltNetworkService.RenderCalls - SessionState.GetInt(K + "BeltRenders", 0);
            BeltRenderer r = BeltNetworkService.Renderer;
            int ox = SessionState.GetInt(K + "BeltX", 0);
            int oy = SessionState.GetInt(K + "BeltY", 0);
            k.TryGetCellInfo(ox + 9, oy + 3, out BeltCellInfo ringCell);
            float ortho = WorldView.Camera != null ? WorldView.Camera.orthographicSize : 0f;
            Write($"  - 传送带：4 真实秒内核走了 {steps} 步（20 Hz × 当前倍速 {GameClock.EffectiveSpeed}x），输出端口推上 {src.Total} 件，在带 {l.OnBelts} 件；绘制 {renders} 次（正交 {ortho:F1}），" +
                  $"实例 {r?.LastCellInstances} 格 + {r?.LastItemInstances} 件，{(r != null && r.GpuAvailable ? "GPU 绘制" : "无图形设备：" + r?.GpuUnavailableReason)}");
            Check(steps >= 40 && src.Total > 0 && l.Balanced && k.CapacityViolations == 0 && k.ComputeStateHash().ToString() != SessionState.GetString(K + "BeltHash", string.Empty),
                $"真实 Play 帧里传送带内核按固定步推进：{steps} 步，输出端口推上 {src.Total} 件，账本平衡（推上 {l.Emitted} + 放入 {l.Inserted} − 收下 {l.Delivered} = 在带 {l.OnBelts}）");
            string ringNow = RingSignature(k, ox, oy);
            string ringBefore = SessionState.GetString(K + "BeltRing", string.Empty);
            Check(ringCell.InLoop && ringNow != ringBefore && ringNow.Split(';')[0] == ringBefore.Split(';')[0],
                $"环照常转：环上件数不变（{ringNow.Split(';')[0]} 件）、物品位置变了；悬停“{BeltNetworkService.DescribeCell(new GridCell(ox + 9, oy + 3))}”");
            Check(renders > 30 && r != null && !r.FarMode && r.LastCellInstances == SessionState.GetInt(K + "BeltCells", -1) && r.LastItemInstances == k.ItemCount,
                $"近景逐物品实例化：每帧一次 Render（{renders} 次），{r?.LastCellInstances} 格 + {r?.LastItemInstances} 件实例");
            CheckNoTextMarkers("传送带");
            SessionState.SetFloat(K + "BeltOrtho", ortho);
            // 真实滚轮输入（可重绑的“缩小”动作）拉远到远景。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = -1f, ScrollFrom = Time.frameCount + 1, ScrollTo = Time.frameCount + 12 });
            Next(131, "滚轮拉远镜头");
        }

        private static void StepBeltsFar(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            BeltRenderer r = BeltNetworkService.Renderer;
            float ortho = WorldView.Camera != null ? WorldView.Camera.orthographicSize : 0f;
            Check(r != null && r.FarMode && r.LastItemInstances == 0 && r.LastCellInstances > 0 && ortho >= BeltNetworkService.RenderSettings.FlowOrthoEnter,
                $"滚轮拉远到正交半高 {ortho:F1}（原 {SessionState.GetFloat(K + "BeltOrtho", 0f):F1}）：切到远景流动贴图，不再逐物品绘制（物品实例 {r?.LastItemInstances}，格实例 {r?.LastCellInstances}）");
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = 1f, ScrollFrom = Time.frameCount + 1, ScrollTo = Time.frameCount + 12 });
            Next(132, "滚轮拉近镜头");
        }

        private static void StepBeltsNear(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            BeltRenderer r = BeltNetworkService.Renderer;
            Check(r != null && !r.FarMode && r.LastItemInstances > 0, $"拉回近景：恢复逐物品实例（{r?.LastItemInstances} 件）");
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen });
            Next(133, "传送带段结束，回到存档流程");
        }

        private static void StepBeltsDone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            BeginPauseSave();
        }

        private static void BeginPauseSave()
        {
            // 上次自动存档之后再改一台机器的记录（完成 3 次工作）：暂停菜单存档必须先导出机器记录，否则这 3 次会丢。
            MachineRecord worker = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).OrderBy(m => m.LogicId).FirstOrDefault();
            if (worker == null)
            {
                Finish("归还谷地里没有存活的机器");
                return;
            }
            for (int i = 0; i < 3; i++)
            {
                MachineRegistry.RecordJobCompleted(worker.LogicId);
            }
            SessionState.SetInt(K + "SavedWorker", worker.LogicId);
            SessionState.SetInt(K + "SavedJobs", worker.JobsCompleted);
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(70, $"机器 #{worker.DisplayNumber} 在自动存档之后又完成 3 次工作（共 {worker.JobsCompleted}）；按 Esc 打开暂停菜单");
        }

        private static void StepPauseForSave(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(PauseMenuUIToolkit.IsOpen, "Esc 打开暂停菜单");
            Check(ClickUitk("[PauseMenuHost]", "PauseSaveQuit"), "点“保存并返回主菜单”");
            Next(71, "点“保存并返回主菜单”");
        }

        private static void StepSaveConfirm(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(UiConfirmDialog.IsOpen, "弹出二次确认（写明保存到哪个槽位）");
            Check(ClickUitk("[UiKitOverlayHost]", "ConfirmOk"), "确认框点“确认”");
            Next(26, "确认：保存到当前槽位并经唯一回菜单出口回到主菜单");
        }

        private const string SmokeRemovedId = "organ_removed_smoke";

        /// <summary>FG0-SAVE-01：主菜单出现后，在刚玩过的存档里放一件已移除内容（测试表，不改正式表），然后从“读取”进游戏。</summary>
        private static void StepMenuAfterRun(double inStep)
        {
            Button load = FindActiveButton("m_btn_Load");
            if (load == null)
            {
                if (inStep > 60)
                {
                    Finish("60 秒内没回到主菜单");
                }
                return;
            }
            if (inStep < 2)
            {
                return;
            }
            int slot = SessionState.GetInt(K + "PlayedSlot", 0);
            LoadResult onDisk = CampaignSaveService.Load(slot);
            if (!onDisk.Success)
            {
                Finish($"刚玩过的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
                return;
            }
            CampaignState s = onDisk.State;
            Check(!PauseMenuUIToolkit.IsOpen && UiEscapeStack.Count == 0 && !InputRouter.ModalUiOpen,
                "回到主菜单：暂停菜单已收起，Esc 栈与模态都没有残留");
            int savedWorker = SessionState.GetInt(K + "SavedWorker", 0);
            MachineRecord savedRecord = s.MachineRecords?.FirstOrDefault(m => m.LogicId == savedWorker);
            Check(savedRecord != null && savedRecord.JobsCompleted == SessionState.GetInt(K + "SavedJobs", -1),
                $"暂停菜单存档先导出机器记录：存档里机器 #{savedRecord?.DisplayNumber} 完成工作 {savedRecord?.JobsCompleted}（期望 {SessionState.GetInt(K + "SavedJobs", -1)}）");
            Check(s.Belts != null && s.Belts.FormatVersion == BeltKernel.FormatVersion && s.Belts.CellCount == SessionState.GetInt(K + "BeltCells", -1)
                  && s.Belts.Networks.Length >= 2 && s.Belts.KernelSteps > 0,
                $"暂停菜单存档带上传送带：{s.Belts?.CellCount} 格、{s.Belts?.ItemCount} 件、{s.Belts?.Networks.Length} 个网络块（按网络分块）");
            SessionState.SetInt(K + "BeltSavedItems", s.Belts?.ItemCount ?? -1);
            SessionState.SetString(K + "BeltSavedSteps", (s.Belts?.KernelSteps ?? -1).ToString());
            SessionState.SetInt(K + "ScrapBefore", s.Scrap);
            s.PrimitiveChips = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                .Concat(new[] { new PrimitiveChipRecord { PartId = "pchip_smoke_removed", CardDefId = SmokeRemovedId, State = PrimitiveChipState.Bag } })
                .ToArray();
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            var buf = new ByteBuf();
            buf.WriteSize(1);
            buf.WriteString(SmokeRemovedId);
            buf.WriteString("primitive_chip");
            buf.WriteString("enemy.scout.name");
            buf.WriteInt(9);
            buf.WriteInt(2);
            SaveContentReconciler.OverrideForTests(new TbRemovedContent(buf), id => id != SmokeRemovedId);
            load.onClick.Invoke();
            Next(27, $"回到主菜单；测试捷径：槽位 {slot + 1} 存档里放一件已移除内容（测试表：退还 9 废料），点“读取”");
        }

        private static void StepLoadSlotList(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            int slot = SessionState.GetInt(K + "PlayedSlot", 0);
            string label = FindText($"m_text_Slot{slot}ActionLabel")?.text ?? string.Empty;
            Button action = FindActiveButton($"m_btn_Slot{slot}Action");
            Check(action != null && label == "读取", $"槽位 {slot + 1} 卡片按钮是“{label}”");
            if (action == null)
            {
                Finish("找不到刚玩过的存档的“读取”按钮");
                return;
            }
            action.onClick.Invoke();
            Next(28, $"点槽位 {slot + 1} 的“读取”");
        }

        private static void StepLoadedIntoGame(double inStep)
        {
            if (!(GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive))
            {
                if (inStep > 120)
                {
                    Finish("120 秒内读档没进入归还谷地");
                }
                return;
            }
            if (inStep < 1)
            {
                return;
            }
            CampaignState st = CampaignSession.Current;
            string[] captions = FeedbackCues.ActiveCaptions.Where(c => c.Cue == FeedbackCueId.SaveContentMigrated).Select(c => c.Text).ToArray();
            Write($"  - 读档字幕：{string.Join("／", captions)}");
            int before = SessionState.GetInt(K + "ScrapBefore", 0);
            Check(st != null && st.PrimitiveChips.All(c => c.CardDefId != SmokeRemovedId) && st.Scrap == before + 9,
                $"读档进入游戏：已移除内容转换为废料（{before}→{st?.Scrap}）");
            Check(captions.Any(c => c.Contains("静默侦察机") && c.Contains("9 废料")), "进入游戏后弹出迁移字幕");
            CheckNoTextMarkers("读档进入游戏");
            BeltKernel bk = BeltNetworkService.Kernel;
            int bx = SessionState.GetInt(K + "BeltX", 0);
            int by = SessionState.GetInt(K + "BeltY", 0);
            Check(bk != null && bk.CellCount == SessionState.GetInt(K + "BeltCells", -1) && bk.Ledger.Balanced
                  && bk.StepIndex >= long.Parse(SessionState.GetString(K + "BeltSavedSteps", "0"))
                  && HomeGridService.MapFor(st).GetBelt(new GridCell(bx, by)) != 0 && BeltNetworkService.LastLoadError == null,
                $"读档恢复传送带：{bk?.CellCount} 格、{bk?.ItemCount} 件（存档时 {SessionState.GetInt(K + "BeltSavedItems", -1)} 件，读档后已继续运行），账本平衡，格网传送带层恢复");
            Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(st);
            Next(80, "测试捷径：核心被毁（HomeValleySoftlockGuard.DebugDestroyCore），等失败页出现");
        }

        // ── FG0-UX-01 审查修复：胜负页上的 Esc 与回主菜单收尾 ───────────────────

        private static bool FailurePageShown()
        {
            GameObject host = GameObject.Find("[HomeValleyFailureHost]");
            VisualElement root = host != null ? host.GetComponent<UIDocument>()?.rootVisualElement : null;
            UnityEngine.UIElements.Button uiBack = root?.Q<UnityEngine.UIElements.Button>("BackToMenuButton");
            return uiBack != null && IsDisplayed(uiBack);
        }

        private static bool IsDisplayed(VisualElement e)
        {
            for (VisualElement x = e; x != null; x = x.parent)
            {
                if (x.resolvedStyle.display == DisplayStyle.None)
                {
                    return false;
                }
            }
            return true;
        }

        private static void StepFailureShown(double inStep)
        {
            if (!FailurePageShown())
            {
                if (inStep > 10)
                {
                    Finish("核心被毁后 10 秒内没出现失败页");
                }
                return;
            }
            if (inStep < 1)
            {
                return;
            }
            Check(true, "核心被毁 → 失败页出现");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(81, "在失败页上按 Esc");
        }

        private static void StepFailureEsc(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(!PauseMenuUIToolkit.IsOpen && FailurePageShown(),
                "失败页上按 Esc：不打开被失败页盖住的暂停菜单，失败页也不被关掉（只能用页面按钮离开）");
            Check(ClickUitk("[HomeValleyFailureHost]", "BackToMenuButton"), "点失败页“返回主菜单”");
            Next(82, "点失败页“返回主菜单”");
        }

        private static void StepFailureBackToMenu(double inStep)
        {
            Button load = FindActiveButton("m_btn_Load");
            if (load == null)
            {
                if (inStep > 60)
                {
                    Finish("60 秒内没从失败页回到主菜单");
                }
                return;
            }
            if (inStep < 1)
            {
                return;
            }
            Check(!PauseMenuUIToolkit.IsOpen && UiEscapeStack.Count == 0 && !InputRouter.ModalUiOpen && !GameRoot.AnyRegionActive,
                "从失败页回到主菜单：暂停菜单没有盖在主菜单上，Esc 栈与模态都没有残留");
            Finish("完成");
        }

        // ── 工具 ────────────────────────────────────────────────────

        private static void Next(int step, string message)
        {
            Write(message);
            SessionState.SetInt(K + "Step", step);
            SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
        }

        private static void Finish(string reason)
        {
            int errors = SessionState.GetInt(K + "Errors", 0);
            bool pass = reason == "完成" && errors == 0;
            Write($"结论：{(pass ? "PASS" : "FAIL")}（{reason}，报错 {errors} 条）");
            SessionState.SetBool(K + "Active", false);
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            try
            {
                InputRouter.DebugSetReader(null);
                SaveContentReconciler.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                string saves = SessionState.GetString(K + "Saves", null);
                if (!string.IsNullOrEmpty(saves) && Directory.Exists(saves))
                {
                    Directory.Delete(saves, true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响结论。
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(pass ? 0 : 1);
            }
            else if (EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
            }
        }

        private static void Write(string line)
        {
            string path = SessionState.GetString(K + "Out", null);
            if (!string.IsNullOrEmpty(path))
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private static Button FindActiveButton(string name) =>
            Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(b => b.name == name && b.isActiveAndEnabled && b.interactable);

        /// <summary>测试捷径：满足区域门禁并记一次出征（真实出征由 ExpeditionDepartureService.TryDepart 做同样的事）。</summary>
        private static void UnlockLikeDeparture(RegionRecord region, Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget target)
        {
            if (region == null)
            {
                Write("  ✗ 区域记录不存在");
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
                return;
            }
            if (region.State == RegionState.Locked)
            {
                region.State = RegionState.Available;
            }
            region.ExpeditionCount += 1;
            CampaignObjectiveTracker.OnDeparted(CampaignSession.Current, target);
        }

        private static int VfxActive()
        {
            GameObject host = GameObject.Find("[FeedbackVfxHost]");
            var presenter = host != null ? host.GetComponent<View.FeedbackVfxPresenter>() : null;
            return presenter != null ? presenter.ActiveCount : -1;
        }

        private static int[] HomeMachines() =>
            MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == Campaign.Regions.HomeValleyLayout.RegionId)
                .Select(m => m.LogicId).ToArray();

        private static int CountNamed(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Count(t => t.name == name);

        private static string ObjectiveTitle()
        {
            GameObject host = GameObject.Find("[ObjectiveHudHost]");
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            Label title = doc?.rootVisualElement?.Q<Label>("ObjectiveTitle");
            return title != null ? $"“{title.text}”" : "（目标条节点没找到）";
        }

        private static void Check(bool ok, string message)
        {
            Write((ok ? "  ✓ " : "  ✗ ") + message);
            if (!ok)
            {
                SessionState.SetInt(K + "Errors", SessionState.GetInt(K + "Errors", 0) + 1);
            }
        }

        /// <summary>FG0-DATA-01：扫描当前所有可见界面文本（UI Toolkit 与 UGUI），任何 ⟦key⟧ 缺失标记都算失败——
        /// 缺失的文本键在正常流程里必须被发现，而不是靠人眼。</summary>
        private static void CheckNoTextMarkers(string where)
        {
            var hits = new System.Collections.Generic.List<string>();
            int scanned = 0;
            foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (doc.rootVisualElement == null)
                {
                    continue;
                }
                doc.rootVisualElement.Query<TextElement>().ForEach(t =>
                {
                    scanned++;
                    if (Localization.GameText.ContainsMarker(t.text))
                    {
                        hits.Add(t.text);
                    }
                });
            }
            foreach (UnityEngine.UI.Text t in Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                scanned++;
                if (Localization.GameText.ContainsMarker(t.text))
                {
                    hits.Add(t.text);
                }
            }
            Check(hits.Count == 0 && scanned > 0,
                $"{where}：扫描界面文本 {scanned} 个，缺失键标记 {hits.Count} 个{(hits.Count > 0 ? "：" + string.Join("｜", hits.Take(5)) : string.Empty)}");
        }

        /// <summary>FG0-DATA-01：Play 模式下区域播种的敌人生命来自 fg.TbMechEnemy。</summary>
        private static void CheckEnemiesFromTable(string regionId, string enemyTypeId)
        {
            float expected = Campaign.Content.FgContentTables.Enemy(enemyTypeId).MaxHp;
            RegionEnemyRecord[] enemies = CampaignSession.Current?.RegionEnemies?
                .Where(e => e != null && e.RegionId == regionId && e.EnemyTypeId == enemyTypeId).ToArray() ?? Array.Empty<RegionEnemyRecord>();
            Check(enemies.Length > 0 && enemies.All(e => Mathf.Approximately(e.MaxHealth, expected)),
                $"{enemyTypeId} 播种 {enemies.Length} 个，MaxHealth 全部等于表值 {expected}");
        }

        private static Transform FindNamed(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(t => t.name == name);

        private static string LabelText(string hostName, string labelName)
        {
            GameObject host = GameObject.Find(hostName);
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            Label label = doc?.rootVisualElement?.Q<Label>(labelName);
            return label != null ? label.text ?? string.Empty : "（节点没找到）";
        }

        /// <summary>模拟按下一次键：只在下一帧报告按下。</summary>
        private static void PressKey(KeyCode key)
        {
            InputRouter.DebugSetReader(new ScriptedReader { Key = key, KeyFrame = Time.frameCount + 1 });
        }

        /// <summary>模拟鼠标右键点世界里一点（下一帧按下、再下一帧抬起）。</summary>
        private static void RightClickWorld(Vector3 world)
        {
            Camera cam = Camera.main;
            Vector3 screen = cam != null ? cam.WorldToScreenPoint(world) : Vector3.zero;
            InputRouter.DebugSetReader(new ScriptedReader
            {
                Mouse = new Vector3(screen.x, screen.y, 0f),
                Button = 1,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
            });
        }

        /// <summary>模拟鼠标左键点世界里一点：光标移到它的屏幕位置，下一帧按下、再下一帧抬起（和人点一次一样）。</summary>
        private static void ClickWorld(Vector3 world)
        {
            Camera cam = Camera.main;
            Vector3 screen = cam != null ? cam.WorldToScreenPoint(world) : Vector3.zero;
            InputRouter.DebugSetReader(new ScriptedReader
            {
                Mouse = new Vector3(screen.x, screen.y, 0f),
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
            });
        }

        private sealed class ScriptedReader : IInputReader
        {
            public KeyCode Key = KeyCode.None;
            public int KeyFrame = -1;
            public Vector3 Mouse;
            public int DownFrame = -1;
            public int UpFrame = -1;
            public int Button;

            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => key == Key && Time.frameCount == KeyFrame;
            public bool GetMouseButtonDown(int button) => button == Button && Time.frameCount == DownFrame;
            public bool GetMouseButtonUp(int button) => button == Button && Time.frameCount == UpFrame;
            public Vector3 MousePosition => Mouse;
            public float Scroll;
            public int ScrollFrom = -1;
            public int ScrollTo = -1;
            public float MouseScrollDelta => Time.frameCount >= ScrollFrom && Time.frameCount <= ScrollTo ? Scroll : 0f;
        }
    }
}
