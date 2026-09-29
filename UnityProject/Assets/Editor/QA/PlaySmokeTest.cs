using System;
using System.Collections.Generic;
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
    /// FG1-SIG-01 / 02：按 P 开信号核 → 刻印过载、装入 1 号槽、存预设 → 再开蓝图编辑器 → 点选导线经过的空格 → 点“标为接入口”→
    /// 格子上图标 + 文字、双态预览两栏（你接入时插入过载、高亮）与差异 → Ctrl+Z 撤销 / Ctrl+Y 重做（不弹“尚未开放”）→
    /// 0 号格标接入口被拒并给原因 → Esc 关编辑器（草稿不保存）→ 远征准备面板的信号核入口与远征锁。
    /// FG1-SIG-03：家园里鼠标选中 → 按接入键 → 机器列表切机 → Tab → 离开 → 战略暂停中发起 → Esc 取消 → 恢复；
    /// 铸造前哨外围点命令栏机器列表接入 → 按接入 / 退出键离开；存档前接入、读档后核对信号位置。
    /// FG1-VFX-01：装配站换上带接入口的重炮蓝图（测试捷径）→ 真实鼠标选中 + 接入键 → 过载插入、形变态出现（0.3 秒过渡走完、部件可见）→ 接入 / 退出键离开 → 复原；
    /// 普通接入时核对表现层的形变与战斗桥接层的编译结果一致。
    /// FG1-HUD-01：暂停菜单里点“图鉴”打开机制图鉴、点关闭回到暂停菜单，接入镜头两项设置显示；接入重炮后读接入 HUD（机体名、信号核槽位“生效”、机身状态与来源、
    /// 热量 / 电池 / 耐久 / 链路 / 暴露 / 与信号同行）、机器列表“◇口”标记 → 点接入 HUD 的“?”打开图鉴“信号接入”→ Esc 关闭（不弹暂停菜单）→
    /// 接入 / 退出键离开：接入 HUD 隐藏、离开音效钩子、机器详情写“与信号同行 N 次”。
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
            SessionState.SetBool(K + "LinkHover", false);
            SessionState.SetInt(K + "FoListPhase", 0);
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
                    case 129: StepRuinsCombat(inStep); break;
                    case 230: StepRuinsTagHover(inStep); break;
                    case 150: StepSignalOpened(inStep); break;
                    case 151: StepSignalPrinted(inStep); break;
                    case 152: StepSignalEquipped(inStep); break;
                    case 153: StepSignalPresetSaved(inStep); break;
                    case 154: StepSignalClosed(inStep); break;
                    case 233: StepRestoreOpened(inStep); break;
                    case 234: StepRestoreAsked(inStep); break;
                    case 235: StepRestoreClosed(inStep); break;
                    // FG3-GEN-01：新游戏设置（种子 / 随机种子 / 分项设置 / 分享短码）与战略地图、小地图、连续缩放。
                    case 236: StepNewGameSetup(inStep); break;
                    case 237: StepMapOpened(inStep); break;
                    case 238: StepMapClosedZoomBurst(inStep); break;
                    case 239: StepMapZoomGesture(inStep); break;
                    case 240: StepMapFlyClick(inStep); break;
                    case 241: StepMinimapClick(inStep); break;
                    case 242: StepMinimapFlown(inStep); break;
                    case 243: StepMapZoomBack(inStep); break;
                    case 244: StepMapHome(inStep); break;
                    case 245: StepMapReopenedByZoom(inStep); break;
                    case 180: StepFwLibOpened(inStep); break;
                    case 181: StepFwLibCodexJumped(inStep); break;
                    case 182: StepFwLibCodexClosed(inStep); break;
                    case 183: StepFwLibClosed(inStep); break;
                    case 196: StepExposureOpened(inStep); break;
                    case 197: StepExposureClosed(inStep); break;
                    case 161: StepUplinkEditorOpened(inStep); break;
                    case 162: StepUplinkSlotPicked(inStep); break;
                    case 163: StepUplinkMarked(inStep); break;
                    case 164: StepUplinkUndone(inStep); break;
                    case 165: StepUplinkRedone(inStep); break;
                    case 166: StepUplinkEditorClosed(inStep); break;
                    case 167: StepSigUplinkSelect(inStep); break;
                    case 168: StepSigUplinkPress(inStep); break;
                    case 169: StepSigUplinkEntered(inStep); break;
                    case 170: StepSigUplinkListSwitched(inStep); break;
                    case 171: StepSigUplinkTabbed(inStep); break;
                    case 172: StepSigUplinkLeft(inStep); break;
                    case 173: StepSigUplinkPaused(inStep); break;
                    case 174: StepSigUplinkPausedPending(inStep); break;
                    case 175: StepSigUplinkEscCancelled(inStep); break;
                    case 176: StepSigUplinkResumed(inStep); break;
                    case 211: StepMorphPrepare(inStep); break;
                    case 212: StepMorphPress(inStep); break;
                    case 213: StepMorphEntered(inStep); break;
                    case 214: StepMorphLeft(inStep); break;
                    case 215: StepHudShown(inStep); break;
                    case 216: StepHudCodexOpened(inStep); break;
                    case 217: StepHudCodexClosed(inStep); break;
                    case 231: StepRosterMorphShown(inStep); break;
                    case 232: StepRosterMorphNext(inStep); break;
                    case 218: StepFollowStarted(inStep); break;
                    case 219: StepFollowStoppedByHome(inStep); break;
                    case 185: StepLinkSelect(inStep); break;
                    case 186: StepLinkPress(inStep); break;
                    case 187: StepLinkEntered(inStep); break;
                    case 188: StepLinkSilentBroken(inStep); break;
                    case 189: StepLinkSilentPress(inStep); break;
                    case 190: StepLinkSilentRejected(inStep); break;
                    case 191: StepLinkSafeExited(inStep); break;
                    case 192: StepLinkReentered(inStep); break;
                    case 193: StepLinkEdgeWarned(inStep); break;
                    case 194: StepLinkCoverageBroken(inStep); break;
                    case 195: StepLinkCoverageRecovered(inStep); break;
                    case 198: StepNetOverlayOn(inStep); break;
                    case 199: StepNetOverlayOff(inStep); break;
                    case 200: StepNetPrevUplinked(inStep); break;
                    case 201: StepNetHomeDone(inStep); break;
                    case 202: StepNetOutsideTagged(inStep); break;
                    case 203: StepNetOutsideRejected(inStep); break;
                    case 204: StepNetOutsideRecovered(inStep); break;
                    case 205: StepNetPrevAgain(inStep); break;
                    case 206: StepNetLeft(inStep); break;
                    case 207: StepNetCrossJump(inStep); break;
                    case 208: StepNetCrossArrived(inStep); break;
                    case 209: StepNetCrossHome(inStep); break;
                    case 210: StepNetBackToExpedition(inStep); break;
                    case 177: StepSigSaveSelect(inStep); break;
                    case 178: StepSigSavePress(inStep); break;
                    case 179: StepSigSaveUplinked(inStep); break;
                    case 29: StepSigLeftAfterLoad(inStep); break;
                    case 19: StepSigHomeAfterLoad(inStep); break;
                    case 158: StepSignalPrepPanel(inStep); break;
                    case 159: StepSignalFromPrep(inStep); break;
                    case 160: StepSignalPrepDone(inStep); break;
                    case 155: StepSignalExpeditionOpened(inStep); break;
                    case 156: StepSignalExpeditionDenied(inStep); break;
                    case 157: StepSignalExpeditionClosed(inStep); break;
                    case 140: StepRaidStart(inStep); break;
                    case 141: StepRaidRunning(inStep); break;
                    case 142: StepRaidCleared(inStep); break;
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
                    case 101: StepBuildMenuSearchHotbar(inStep); break;
                    case 102: StepBuildHotbarKey(inStep); break;
                    case 103: StepBuildBeltDragged(inStep); break;
                    case 104: StepBuildBoxDemolished(inStep); break;
                    case 105: StepBuildRelocatePicked(inStep); break;
                    case 106: StepBuildRelocatePlanned(inStep); break;
                    case 107: StepBuildRelocateCancelled(inStep); break;
                    case 108: StepBuildGridToggled(inStep); break;
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
            // FG3-GEN-01：选好存档槽后先出现新游戏设置（种子 / 世界设置 / 分享短码），点“开始”才建战役。
            if (NewGamePanelUIToolkit.IsOpen)
            {
                Next(236, $"新游戏设置出现（{inStep:F0} 秒）");
                return;
            }
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

        public const string SmokeSeedText = "20260929";
        public const string SmokeSettings = "R2O1P1D1S0";

        /// <summary>
        /// FG3-GEN-01（FGR-GEN-001、070、071）：新游戏设置——默认种子、“随机种子”、输入文字种子的换算提示、点分项按钮（资源丰度 高）、
        /// 复制短码、导入别人的短码再导回自己的，最后点“开始”。按钮都走 UI Toolkit 按钮自己的 Clickable（与鼠标点击同一回调）。
        /// </summary>
        private static void StepNewGameSetup(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            NewGamePanelUIToolkit p = NewGamePanelUIToolkit.Instance;
            if (p == null || !p.PanelVisible)
            {
                Finish("新游戏设置面板没有显示");
                return;
            }
            string first = p.SeedFieldText;
            bool decoded = Campaign.WorldGen.WorldSettings.TryDecodeShareCode(p.ShareFieldText, out int ds, out Campaign.WorldGen.WorldSettings dset, out int dv)
                           == Campaign.WorldGen.WorldSettings.ShareError.None;
            Check(first.Length > 0 && decoded && ds.ToString(System.Globalization.CultureInfo.InvariantCulture) == first && dv == Campaign.WorldGen.WorldGenVersions.Current
                  && dset.Id == Campaign.WorldGen.WorldGenContent.DefaultPresetId && p.AxisRowCount == 5,
                $"新游戏设置：默认随机种子 {first}、五个分项全部标准、分享短码 {p.ShareFieldText}、{p.GeneratorText}");
            CheckNoTextMarkers("新游戏设置");
            bool rnd = ClickUitk("[NewGameHost]", "NewGameRandom");
            Check(rnd && p.SeedFieldText != first, $"点“随机种子”：{first} → {p.SeedFieldText}");
            p.SeedField.value = "归还之地";
            Campaign.WorldGen.WorldSettings.TryParseSeed("归还之地", out int textSeed, out _);
            Check(p.SeedHintText.Contains(textSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"输入文字种子：提示“{p.SeedHintText}”");
            p.SeedField.value = SmokeSeedText;
            bool level = ClickUitk("[NewGameHost]", "NewGameLevel_resource_2");
            Check(level && p.CurrentSettings().Id == SmokeSettings && p.LevelButton(0, 2).text.StartsWith("▸", StringComparison.Ordinal),
                $"点分项按钮“资源丰度 高”：当前档换成“▸ {p.LevelButton(0, 2).text.TrimStart('▸', ' ')}”，设置 {p.CurrentSettings().Id}");
            string mine = p.ShareFieldText;
            string oldClip = GUIUtility.systemCopyBuffer;
            bool copied = ClickUitk("[NewGameHost]", "NewGameCopyCode") && GUIUtility.systemCopyBuffer == mine;
            GUIUtility.systemCopyBuffer = oldClip;
            string other = Campaign.WorldGen.WorldSettings.EncodeShareCode(12345, Campaign.WorldGen.WorldSettings.Resolve(Campaign.WorldGen.WorldGenVersions.Current, "R1O1P1D1S1"));
            p.ShareField.value = other;
            bool importedOther = ClickUitk("[NewGameHost]", "NewGameImportCode") && p.SeedFieldText == "12345" && p.CurrentSettings().Id == "R1O1P1D1S1";
            p.ShareField.value = mine;
            bool importedMine = ClickUitk("[NewGameHost]", "NewGameImportCode") && p.SeedFieldText == SmokeSeedText && p.CurrentSettings().Id == SmokeSettings;
            Check(copied && importedOther && importedMine, $"复制短码 {mine}；导入别人的短码（种子 12345 + 宽松起始区）再导回自己的，种子与设置一起换");
            bool started = ClickUitk("[NewGameHost]", "NewGameStart");
            Check(started && !NewGamePanelUIToolkit.IsOpen, "点“开始”：新游戏设置关闭，开新战役");
            Next(3, $"新游戏：种子 {SmokeSeedText}、世界设置 {SmokeSettings}");
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
            CampaignState ws = CampaignSession.Current;
            Check(ws?.World != null && ws.World.WorldSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) == SmokeSeedText && ws.World.WorldSettingsId == SmokeSettings
                  && ws.World.GeneratorVersion == Campaign.WorldGen.WorldGenVersions.Current && Campaign.WorldGen.WorldGenService.PlanFor(ws)?.StartReport?.AllSatisfied == true,
                $"新游戏设置进了存档：世界种子 {ws?.World?.WorldSeed}、设置 {ws?.World?.WorldSettingsId}、生成器 v{ws?.World?.GeneratorVersion}，起始区四级保证满足");
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
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.OpenMap));
            Next(237, "按地图键（默认 M）");
        }

        // ── FG3-GEN-01：战略地图（连续缩放）与小地图 ─────────────────────────────────────

        private static void StepMapOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            bool open = StrategicMapUIToolkit.IsOpen && map != null && map.PanelVisible;
            Check(open, "按地图键打开战略地图（任务日志仍是自己的键）");
            if (!open)
            {
                Finish("战略地图没有打开");
                return;
            }
            map.Tick(force: true);
            bool home = map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home);
            bool territories = map.Model.Circles.Any(c => c.Layer == WorldMapLayer.Territory) || map.View.HalfWidth < 300;
            Check(home && GameSettings.HasSeenGuidanceHook(GuidanceHooks.StrategicMapFirstOpen) && map.VisibleIconCount > 0,
                $"战略地图：归还核心图标、{map.VisibleIconCount} 个图标、{map.Model.Circles.Count} 个范围圈（领地 / 信号覆盖），首次打开钩子已发（{territories}）");
            bool off = ClickUitk("[StrategicMapHost]", "MapFilter4");
            map.Tick(force: true);
            bool homeHidden = !map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home);
            bool on = ClickUitk("[StrategicMapHost]", "MapFilter4");
            map.Tick(force: true);
            Check(off && homeHidden && on && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home), "点“己方”筛选按钮：归还核心图标隐藏，再点恢复");
            // 己方建筑群与前哨（FGR-GEN-080）：核心以外的每个建筑群，在视野里就有图标（核心那一群由核心图标代表）。
            var clusters = WorldMapOwnClusters.For(CampaignSession.Current);
            int ownInView = clusters.Count(c => !c.ContainsCore && map.View.Contains(c.X, c.Y, map.View.HalfWidth * 0.1));
            int ownIcons = map.Model.Items.Count(i => i.Kind == WorldMapItemKind.OwnCluster);
            Check(clusters.Count(c => c.ContainsCore) == 1 && ownIcons == ownInView,
                $"己方建筑群：{clusters.Count} 群（核心所在 1 群由核心图标代表），视野里另有 {ownInView} 群、地图上 {ownIcons} 个建筑群 / 前哨站图标");
            // 右键点在地图画布里（画布中心偏右上）：地图按镜头最远的比例打开，看到的是镜头附近，不能用固定格坐标（可能在视野外）。
            Campaign.MapMarkerRecord mk = map.AddMarkerAt(new Vector2(map.View.CanvasWidth * 0.5f + 40f, map.View.CanvasHeight * 0.5f - 30f));
            // 真实输入框逐段输入：先“冒烟 ”（末尾空格），刷新后空格不能被吃掉；再接着输入“标记”= “冒烟 标记”（多词备注，B16）。
            map.MarkerNoteField.value = "冒烟 ";
            map.Tick(force: true);
            bool spaceKept = map.MarkerNoteField.value == "冒烟 ";
            map.MarkerNoteField.value = map.MarkerNoteField.value + "标记";
            map.Tick(force: true);
            string storedNote = mk != null ? Campaign.WorldGen.WorldMapMarkers.Find(CampaignSession.Current, mk.MarkerId)?.Note : null;
            string markerLabel = map.Model.Items.Where(i => i.Kind == WorldMapItemKind.Marker).Select(i => i.Label).FirstOrDefault();
            Check(mk != null && spaceKept && storedNote == "冒烟 标记" && markerLabel != null && markerLabel.Contains("冒烟 标记"),
                $"右键加标记并逐段输入备注：标记 {mk?.Serial}，末尾空格没被吃掉（{spaceKept}），存档域里是“{storedNote}”、输入框“{map.MarkerNoteField.value}”、地图标签“{markerLabel}”（应为“冒烟 标记”）");
            CheckNoTextMarkers("战略地图");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.OpenMap));
            Next(238, "再按地图键关闭");
        }

        private static void StepMapClosedZoomBurst(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            Check(!StrategicMapUIToolkit.IsOpen, "再按地图键关闭战略地图");
            SessionState.SetInt(K + "ZoomOverflow", View.CameraDirector.ZoomOverflowCount);
            SessionState.SetFloat(K + "OrthoBeforeMap", WorldView.Camera != null ? WorldView.Camera.orthographicSize : 0f);
            // 一串连续的滚轮拉远（真实“拉远”动作）：一路拉到最远，同一串里不冲进地图。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = -1f, ScrollFrom = Time.frameCount + 1, ScrollTo = Time.frameCount + 24 });
            Next(239, "连续滚轮拉远镜头到最远");
        }

        private static void StepMapZoomGesture(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            float ortho = WorldView.Camera != null ? WorldView.Camera.orthographicSize : 0f;
            Check(!StrategicMapUIToolkit.IsOpen && ortho >= View.CameraDirector.MaxStrategyOrthographicSize - 0.05f,
                $"一串连续滚动拉到最远（正交半高 {ortho:F1} = 上限 {View.CameraDirector.MaxStrategyOrthographicSize:F1}），同一串手势里不会顺势冲进地图");
            // 停顿之后再拉远一下 = 新的手势 → 连续缩放切到战略地图（FGR-GEN-080）。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = -1f, ScrollFrom = Time.frameCount + 1, ScrollTo = Time.frameCount + 1 });
            Next(240, "停顿后再拉远一下");
        }

        private static void StepMapFlyClick(double inStep)
        {
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            if (!StrategicMapUIToolkit.IsOpen || map == null || (!map.MapTexture.Painted && inStep < 4))
            {
                if (inStep > 4)
                {
                    Check(false, "最远缩放后再拉远没有打开战略地图");
                    Finish("连续缩放没有切到战略地图");
                }
                return;
            }
            double ratio = MapToCameraScaleRatio(map, out double mapCpp, out double camCpp);
            Check(View.CameraDirector.ZoomOverflowCount > SessionState.GetInt(K + "ZoomOverflow", 0) && map.MapTexture.Painted && ratio >= 1.0 / 1.5 && ratio <= 1.5,
                $"镜头最远时再拉远 → 连续切到战略地图：比例衔接（地图 {mapCpp:F3} 格 / 屏幕像素，镜头最远 {camCpp:F3}，相差 {ratio:F2} 倍 ≤ 1.5），" +
                $"底图已由工作线程画好（{map.MapTexture.LastJobMs:F1} ms）");
            // 地图里拉近一格（与滚轮同一入口）：回到镜头，镜头仍停在最远缩放——从地图到镜头也是连续的。
            SessionState.SetInt(K + "ZoomOverflow", View.CameraDirector.ZoomOverflowCount);
            map.ZoomAt(new Vector2(map.View.CanvasWidth * 0.5f, map.View.CanvasHeight * 0.5f), -1);
            float orthoAfter = WorldView.Camera != null ? WorldView.Camera.orthographicSize : 0f;
            Check(!StrategicMapUIToolkit.IsOpen && orthoAfter >= View.CameraDirector.MaxStrategyOrthographicSize - 0.05f,
                $"在地图里拉近一格 → 地图关掉、回到镜头（镜头正交半高 {orthoAfter:F1} = 最远 {View.CameraDirector.MaxStrategyOrthographicSize:F1}，比例首尾相接）");
            // 停顿之后再拉远一下（新的手势）= 再次切到战略地图。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = -1f, ScrollFrom = Time.frameCount + 60, ScrollTo = Time.frameCount + 60 });
            Next(245, "地图里拉近一格回到镜头，停顿后再拉远一下");
        }

        /// <summary>地图比例（每个屏幕像素多少格）÷ 镜头最远缩放时的比例。屏幕像素 ↔ 画布像素按面板根节点宽度换算。</summary>
        private static double MapToCameraScaleRatio(StrategicMapUIToolkit map, out double mapCpp, out double camCpp)
        {
            mapCpp = 0;
            camCpp = 0;
            Camera cam = WorldView.Camera;
            var quad = new Vector2[4];
            float panelWidth = map.Canvas?.panel?.visualTree?.layout.width ?? 0f;
            if (cam == null || panelWidth < 1f || !WorldMapVectorLayer.CameraGroundQuad(cam, quad))
            {
                return 0;
            }
            double far = Vector2.Distance(quad[0], quad[1]) * (View.CameraDirector.MaxStrategyOrthographicSize / Mathf.Max(0.01f, cam.orthographicSize));
            camCpp = far / cam.pixelWidth;
            mapCpp = map.View.CellsPerCanvasPixel * panelWidth / cam.pixelWidth;
            return camCpp > 0 ? mapCpp / camCpp : 0;
        }

        private static void StepMapReopenedByZoom(double inStep)
        {
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            if (!StrategicMapUIToolkit.IsOpen || map == null || (!map.MapTexture.Painted && inStep < 5))
            {
                if (inStep > 5)
                {
                    Check(false, "回到镜头后再拉远没有再次打开战略地图");
                    Finish("连续缩放没有再次切到战略地图");
                }
                return;
            }
            double ratio = MapToCameraScaleRatio(map, out double mapCpp, out double camCpp);
            Check(View.CameraDirector.ZoomOverflowCount > SessionState.GetInt(K + "ZoomOverflow", 0) && ratio >= 1.0 / 1.5 && ratio <= 1.5,
                $"停顿后再拉远 → 再次切到战略地图（地图 {mapCpp:F3} / 镜头最远 {camCpp:F3} 格每屏幕像素，相差 {ratio:F2} 倍）");
            // 批处理下真实鼠标在 (0,0)（窗口角落），会触发边缘推屏：换成屏外的脚本读取器，镜头只按飞跃移动。
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen });
            // 点在地图画布里的一处（画布 60% / 40%）：真实玩家只能点到画布上看得见的地方。
            Vector2 at = new Vector2(map.View.CanvasWidth * 0.6f, map.View.CanvasHeight * 0.4f);
            Vector2 target = map.View.ToCell(at);
            SessionState.SetFloat(K + "FlyX", target.x);
            SessionState.SetFloat(K + "FlyY", target.y);
            SessionState.SetInt(K + "MapFly", StrategicMapUIToolkit.FlyCount);
            map.ClickAt(at);
            Next(241, "在战略地图上点一处：镜头飞过去（0.5 秒）");
        }

        private static void StepMinimapClick(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            var target = new Vector2(SessionState.GetFloat(K + "FlyX", 0f), SessionState.GetFloat(K + "FlyY", 0f));
            Check(!StrategicMapUIToolkit.IsOpen && StrategicMapUIToolkit.FlyCount == SessionState.GetInt(K + "MapFly", 0) + 1 && Vector2.Distance(CameraFocus(), target) < 2.5f,
                $"点地图：地图关闭、镜头飞到 {CameraFocus()}（目标 {target}）");
            MinimapHudUIToolkit mini = MinimapHudUIToolkit.Instance;
            bool shown = mini != null && mini.Visible;
            Check(shown && mini.Model.Items.Any(i => i.Kind == WorldMapItemKind.Home) && WorldPlanetView.Terrain != null && WorldPlanetView.Terrain.IsRelief
                  && WorldPlanetView.Terrain.ReliefTileCount > 0,
                $"小地图显示（右下角，{mini?.VisibleIconCount} 个图标）；普通视角地貌是起伏网格（{WorldPlanetView.Terrain?.ReliefTileCount}/{WorldPlanetView.Terrain?.TileCount} 块）");
            if (!shown)
            {
                Finish("小地图没有显示");
                return;
            }
            SessionState.SetInt(K + "MiniFly", MinimapHudUIToolkit.FlyCount);
            Vector2 at = mini.View.ToCanvas(target.x - 20f, target.y + 6f);
            SessionState.SetFloat(K + "MiniX", mini.View.ToCell(at).x);
            SessionState.SetFloat(K + "MiniY", mini.View.ToCell(at).y);
            mini.ClickAt(at);
            Next(242, "在小地图上点一处：镜头飞过去");
        }

        private static void StepMinimapFlown(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            var target = new Vector2(SessionState.GetFloat(K + "MiniX", 0f), SessionState.GetFloat(K + "MiniY", 0f));
            Check(MinimapHudUIToolkit.FlyCount == SessionState.GetInt(K + "MiniFly", 0) + 1 && Vector2.Distance(CameraFocus(), target) < 2.5f,
                $"点小地图：镜头飞到 {CameraFocus()}（目标 {target}）");
            // 拉回打开地图前的缩放（后续步骤按原来的镜头比例点世界里的东西）：按滚轮步长算要滚几下。
            float before = SessionState.GetFloat(K + "OrthoBeforeMap", 0f);
            float step = Mathf.Max(0.5f, Campaign.Grid.GridContent.Tuning("camera.zoom_step"));
            int frames = Mathf.Clamp(Mathf.RoundToInt((View.CameraDirector.MaxStrategyOrthographicSize - before) / step), 0, 40);
            if (frames > 0)
            {
                InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen, Scroll = 1f, ScrollFrom = Time.frameCount + 1, ScrollTo = Time.frameCount + frames });
            }
            Next(243, $"滚轮拉近镜头 {frames} 下（回到打开地图前的缩放 {before:F1}）");
        }

        private static void StepMapZoomBack(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore));
            Next(244, "按回到归还核心键");
        }

        private static void StepMapHome(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            GridCell core = Campaign.Grid.HomeGridService.CorePivot(CampaignSession.Current);
            Write($"  - 回到归还核心：镜头焦点 {CameraFocus()}（核心 {core}）");
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
            // FG2-FW-05 起图鉴键已接通（打开图鉴），“尚未开放”提示改用研发树键（FG5-RND-01 承接）。
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenResearch));
            Next(32, "按研发树键（研发树由 FG5-RND-01 承接，尚未开放）");
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
            Check(toast != null && shown.Contains(Localization.GameText.Get("input.action.open_research.name")),
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
            // FG3-GEN-01（FGR-GEN-071）：暂停菜单“复制分享短码”——短码导入后世界相同。
            string oldClip2 = GUIUtility.systemCopyBuffer;
            bool shareCopied = ClickUitk("[PauseMenuHost]", "PauseCopyShare");
            string share = GUIUtility.systemCopyBuffer;
            GUIUtility.systemCopyBuffer = oldClip2;
            bool shareOk = Campaign.WorldGen.WorldSettings.TryDecodeShareCode(share, out int shareSeed, out Campaign.WorldGen.WorldSettings shareSet, out _)
                           == Campaign.WorldGen.WorldSettings.ShareError.None && st != null && shareSeed == st.World.WorldSeed && shareSet.Id == st.World.WorldSettingsId;
            Check(shareCopied && shareOk && pm.FeedbackText.Contains(share), $"点“复制分享短码”：剪贴板 = {share}（种子 {shareSeed} + 设置 {shareSet?.Id}）");
            // FG1-HUD-01：暂停菜单“图鉴”→ 机制图鉴盖在暂停菜单上面；点关闭回到暂停菜单。接入镜头两项设置显示当前值。
            bool codexClicked = ClickUitk("[PauseMenuHost]", "PauseCodex");
            UI.Kit.MechanicCodexPanelUIToolkit codex = UI.Kit.MechanicCodexPanelUIToolkit.Instance;
            bool codexOpen = codex != null && UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && codex.PanelVisible && codex.ItemCount >= 5
                             && !Localization.GameText.ContainsMarker(codex.EntryTitleText + codex.EntryBodyText);
            string codexTitle = codex?.EntryTitleText ?? string.Empty;
            bool codexClosed = ClickUitk("[MechanicCodexHost]", "CodexClose") && !UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(codexClicked && codexOpen && codexClosed,
                $"暂停菜单点“图鉴”：机制图鉴打开（{codex?.ItemCount} 条，当前“{codexTitle}”），点关闭回到暂停菜单");
            // FG2-FW-05（FGU-20）：暂停菜单“固件库”→ 固件库盖在暂停菜单上面；此时还没有固件芯片时显示空状态说明；点关闭回到暂停菜单。
            bool fwClicked = ClickUitk("[PauseMenuHost]", "PauseFirmware");
            UI.Kit.FirmwareLibraryPanelUIToolkit fwlib = UI.Kit.FirmwareLibraryPanelUIToolkit.Instance;
            bool fwOpen = fwlib != null && UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen && fwlib.PanelVisible && fwlib.CountText.Length > 0
                          && (fwlib.RowCount > 0 || fwlib.EmptyText.Length > 0) && !Localization.GameText.ContainsMarker(fwlib.CountText + fwlib.EmptyText + fwlib.FooterText)
                          // FG00 B14：打开固件库发出首次打开钩子，图鉴“固件库”系统说明随之解锁。
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.FirmwareLibraryFirstOpen) && Progression.MechanicCodex.IsUnlocked("codex.firmware.library");
            string fwState = fwlib == null ? string.Empty : fwlib.RowCount > 0 ? fwlib.RowText(0) : fwlib.EmptyText;
            bool fwClosed = ClickUitk("[FirmwareLibraryHost]", "FwLibClose") && !UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(fwClicked && fwOpen && fwClosed, $"暂停菜单点“固件库”：固件库打开（{fwlib?.CountText}；“{fwState}”），首次打开钩子已发、图鉴“固件库”条目已解锁；点关闭回到暂停菜单");
            // FG2-FW-04（FGR-FW-043 / FGR-SYS-020）：暂停菜单“战斗反馈”三个开关（勾选立即生效）与“反应记录”（日志 / 伤害归因 / 反应图鉴）。
            bool togglesShown = pm != null && pm.ReactionPopupsToggle != null && pm.ReactionSlowMotionToggle != null && pm.ReactionNudgeToggle != null
                                && pm.ReactionPopupsToggle.label == Localization.GameText.Get("pause.reaction_popups")
                                && pm.ReactionPopupsToggle.value == GameSettings.ReactionPopupsEnabled && pm.ReactionSlowMotionToggle.value == GameSettings.ReactionSlowMotionEnabled
                                && pm.ReactionNudgeToggle.value == GameSettings.ReactionCameraNudgeEnabled;
            if (pm?.ReactionPopupsToggle != null && !pm.ReactionPopupsToggle.value)
            {
                pm.ReactionPopupsToggle.value = true; // 本机设置里是关着的：先打开，再验证“关掉立即生效”。
            }
            if (pm?.ReactionPopupsToggle != null)
            {
                pm.ReactionPopupsToggle.value = false;
            }
            bool toggledOff = !GameSettings.ReactionPopupsEnabled;
            bool reset = ClickUitk("[PauseMenuHost]", "PauseReactionReset") && GameSettings.ReactionPopupsEnabled && pm != null && pm.ReactionPopupsToggle.value;
            Check(togglesShown && toggledOff && reset, "暂停菜单“战斗反馈”：反应弹字 / 首次反应慢放 / 首次反应镜头推动三个开关，取消勾选立即生效，“恢复默认”全部打开");
            bool logClicked = ClickUitk("[PauseMenuHost]", "PauseReactionLog");
            UI.Kit.ReactionLogPanelUIToolkit rlog = UI.Kit.ReactionLogPanelUIToolkit.Instance;
            bool logOpen = rlog != null && UI.Kit.ReactionLogPanelUIToolkit.IsOpen && rlog.PanelVisible;
            bool codexTab = ClickUitk("[ReactionLogHost]", "ReactionTabCodex") && rlog != null && rlog.CurrentTab == UI.Kit.ReactionLogPanelUIToolkit.Tab.Codex
                            && rlog.VisibleRowCount >= 18 && rlog.RowText(0).Length > 0 && !Localization.GameText.ContainsMarker(rlog.FooterText + rlog.RowText(0));
            string codexFooter = rlog?.FooterText ?? string.Empty;
            bool logClosed = ClickUitk("[ReactionLogHost]", "ReactionLogClose") && !UI.Kit.ReactionLogPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(logClicked && logOpen && codexTab && logClosed, $"暂停菜单点“反应记录”：面板打开，“反应图鉴”页签列出全部反应（“{codexFooter}”），点关闭回到暂停菜单");
            // FG2-FW-04（卡片“伤害归因进入统计面板”）：暂停菜单“统计”→ 统计面板（战斗 · 反应伤害归因）打开，有累计 / 明细或空状态说明；筛选可切；点关闭回到暂停菜单。
            bool statsClicked = ClickUitk("[PauseMenuHost]", "PauseStats");
            UI.Kit.StatsPanelUIToolkit stats = UI.Kit.StatsPanelUIToolkit.Instance;
            bool statsOpen = stats != null && UI.Kit.StatsPanelUIToolkit.IsOpen && stats.PanelVisible && stats.SectionText.Length > 0
                             && (stats.VisibleRowCount > 0 ? stats.RowText(0).Length > 0 : stats.EmptyText.Length > 0)
                             && !Localization.GameText.ContainsMarker(stats.SectionText + stats.FooterText + stats.CountText + stats.RowText(0) + stats.EmptyText);
            string statsFirst = stats == null ? string.Empty : stats.VisibleRowCount > 0 ? stats.RowText(0) : stats.EmptyText;
            bool statsFilter = ClickUitk("[StatsPanelHost]", "StatsFilterRaid") && stats != null && stats.CurrentFilter == Campaign.Combat.ReactionLogFilter.Raid
                               && ClickUitk("[StatsPanelHost]", "StatsFilterAll") && stats.CurrentFilter == Campaign.Combat.ReactionLogFilter.All;
            bool statsClosed = ClickUitk("[StatsPanelHost]", "StatsPanelClose") && !UI.Kit.StatsPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(statsClicked && statsOpen && statsFilter && statsClosed,
                $"暂停菜单点“统计”：统计面板打开（“{stats?.SectionText}”，{stats?.CountText}，首行“{statsFirst}”），“突袭 / 全部”筛选可切，点关闭回到暂停菜单");
            Check(pm != null && pm.CameraZoomLabelText.Length > 0 && pm.CameraFollowLabelText.Length > 0
                  && !Localization.GameText.ContainsMarker(pm.CameraZoomLabelText + pm.CameraFollowLabelText),
                $"暂停菜单显示接入镜头设置：“{pm?.CameraZoomLabelText}”“{pm?.CameraFollowLabelText}”");
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
            // FG3-LOG-01：建造菜单按十二个分类列出（默认第一个有条目的分类 = 物流）；点“能源”页签再点第一项。
            int energyTab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "energy");
            Check(hud != null && hud.CategoryCount == 12 && hud.SelectedCategoryId == "logistics" && hud.HotbarVisible,
                $"建造菜单有 {hud?.CategoryCount} 个分类页签，默认“{hud?.SelectedCategoryId}”；底部快捷栏可见");
            Check(ClickUitk("[BuildModeHudHost]", "BuildCat" + energyTab) && hud.SelectedCategoryId == "energy", "点“能源”分类页签");
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
            // FG3-LOG-01：退出拆除模式（点按钮），接着走建造菜单的搜索、快捷栏、拖拽铺设、框选拆除、搬迁、格线开关。
            Check(ClickUitk("[BuildModeHudHost]", "BuildDemolish") && mode != null && !mode.DemolishMode, "点“拆除模式”按钮退出拆除模式");
            InputRouter.DebugSetReader(null);
            Next(101, "FG3-LOG-01：建造菜单搜索与快捷栏");
        }

        // ── FG3-LOG-01：建造菜单搜索、快捷栏（点选放入 + F1 选取）、拖拽铺设传送带（长度与成本）、框选拆除、搬迁（搬迁键 + 两次点击 + 取消）、格线开关 ──

        private static UIDocument BuildHudDoc() => GameObject.Find("[BuildModeHudHost]")?.GetComponent<UIDocument>();

        private static void StepBuildMenuSearchHotbar(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            TextField search = BuildHudDoc()?.rootVisualElement?.Q<TextField>("BuildSearch");
            if (search != null)
            {
                search.value = "中继"; // 与在框里打字同一个值变化回调
            }
            hud?.Refresh();
            Check(search != null && hud.ItemCount == 1 && hud.ItemId(0) == "signal_relay" && hud.CaptionText.Contains("搜索"),
                $"搜索框输入“中继”：跨分类只剩信号中继塔（“{hud?.CaptionText}”）");
            Check(ClickUitk("[BuildModeHudHost]", "BuildSearchClear") && string.IsNullOrEmpty(search?.value), "点“清除”清空搜索");
            int logisticsTab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            Check(ClickUitk("[BuildModeHudHost]", "BuildCat" + logisticsTab) && ClickUitk("[BuildModeHudHost]", "BuildItem0") && mode != null && mode.SelectedToolId == "belt_t1",
                $"点“物流”页签再点第一项：选中传送带 T1（{mode?.SelectedToolId}）");
            Check(ClickUitk("[BuildModeHudHost]", "HotbarSlot0") && Campaign.Grid.BuildCatalog.HotbarId(state, 0) == "belt_t1",
                "选中传送带后点空的快捷栏第 1 格：放进快捷栏（存档里记下）");
            // FGR-LOG-002 真实拖放：在条目上按下左键 → 指针移到快捷栏第 2 格 → 松开（UI Toolkit 指针事件，走 HUD 自己注册的回调与 SlotAt 命中）；
            // 再在第 2 格上按右键清空。
            VisualElement hudRoot = BuildHudDoc()?.rootVisualElement;
            UnityEngine.UIElements.Button dragItem = hudRoot?.Q<UnityEngine.UIElements.Button>("BuildItem0");
            UnityEngine.UIElements.Button slot1 = hudRoot?.Q<UnityEngine.UIElements.Button>("HotbarSlot1");
            bool dropped = false;
            bool cleared = false;
            if (dragItem != null && slot1 != null && Campaign.Grid.BuildCatalog.HotbarId(state, 1) == null)
            {
                SendUitkPointer<PointerDownEvent>(dragItem, dragItem.worldBound.center, EventType.MouseDown, 0);
                SendUitkPointer<PointerMoveEvent>(slot1, slot1.worldBound.center, EventType.MouseDrag, 0);
                SendUitkPointer<PointerUpEvent>(slot1, slot1.worldBound.center, EventType.MouseUp, 0);
                dropped = Campaign.Grid.BuildCatalog.HotbarId(state, 1) == "belt_t1";
                SendUitkPointer<PointerDownEvent>(slot1, slot1.worldBound.center, EventType.MouseDown, 1);
                SendUitkPointer<PointerUpEvent>(slot1, slot1.worldBound.center, EventType.MouseUp, 1);
                cleared = Campaign.Grid.BuildCatalog.HotbarId(state, 1) == null;
            }
            Check(dropped && cleared,
                $"按住“物流”第一项拖到快捷栏第 2 格再松开：放进去（{dropped}）；在第 2 格上按右键：清空（{cleared}）（FGR-LOG-002 真实指针事件）");
            Check(ClickUitk("[BuildModeHudHost]", "BuildRotate") && mode.SelectedToolId == "belt_t1", "点“旋转”按钮（传送带单格方向）");
            mode.ClearSelection();
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.Hotbar1));
            Next(102, "按快捷栏 1（默认 F1）");
        }

        private static void StepBuildHotbarKey(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.SelectedToolId == "belt_t1", $"按 F1：选中快捷栏里的传送带（{mode?.SelectedToolId}）");
            Campaign.Grid.GridCell? row = null;
            Campaign.Grid.GridCell corePivot = Campaign.Grid.HomeGridService.CorePivot(state);
            for (int r = 0; r <= 8 && row == null; r++)
            {
                for (int dy = -r; dy <= r && row == null; dy++)
                {
                    for (int dx = -r; dx <= r && row == null; dx++)
                    {
                        var start = new Campaign.Grid.GridCell(corePivot.X - 6 + dx, corePivot.Y + 10 + dy);
                        bool ok = true;
                        for (int k = 0; k < 6 && ok; k++)
                        {
                            ok = Campaign.Grid.HomeGridService.ValidateBeltCell(state, new Campaign.Grid.GridCell(start.X + k, start.Y)).Ok
                                 && Campaign.Grid.HomeGridService.ValidateBeltCell(state, new Campaign.Grid.GridCell(start.X + k, start.Y - 1)).Ok;
                        }
                        if (ok)
                        {
                            row = start;
                        }
                    }
                }
            }
            if (row == null)
            {
                Finish("镜头附近找不到能铺 6 格传送带的空地");
                return;
            }
            SessionState.SetInt(K + "BeltX", row.Value.X);
            SessionState.SetInt(K + "BeltY", row.Value.Y);
            SessionState.SetInt(K + "BeltScrap", state.Scrap);
            SessionState.SetString(K + "BeltDragInfo", string.Empty);
            DragWorld(new Vector3(row.Value.X, 0f, row.Value.Y), new Vector3(row.Value.X + 5, 0f, row.Value.Y), 0);
            Next(103, $"从 {row.Value} 按住左键向东拖 6 格再松开");
        }

        private static void StepBuildBeltDragged(double inStep)
        {
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            string info = hud?.DragInfoText ?? string.Empty;
            if (!string.IsNullOrEmpty(info))
            {
                SessionState.SetString(K + "BeltDragInfo", info); // 拖的过程中 HUD 显示的长度与成本
            }
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int x = SessionState.GetInt(K + "BeltX", 0);
            int y = SessionState.GetInt(K + "BeltY", 0);
            bool all = Enumerable.Range(0, 6).All(i => Campaign.Logistics.BeltNetworkService.Kernel.HasCell(x + i, y));
            string seen = SessionState.GetString(K + "BeltDragInfo", string.Empty);
            Check(all && state.Scrap == SessionState.GetInt(K + "BeltScrap", -1) - 6 && seen.Contains("长度") && seen.Contains("成本"),
                $"真实鼠标拖拽铺下 6 格传送带、扣 6 废料（剩 {state.Scrap}）；拖的时候 HUD 显示“{seen}”");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.DemolishMode));
            SessionState.SetInt(K + "BeltScrap", state.Scrap + GroundScrap(state));
            Next(104, "按拆除模式键，在空地上按住左键把刚铺的传送带框起来");
        }

        private static void StepBuildBoxDemolished(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            int x = SessionState.GetInt(K + "BeltX", 0);
            int y = SessionState.GetInt(K + "BeltY", 0);
            if (inStep < 0.3)
            {
                return;
            }
            if (SessionState.GetInt(K + "BoxDragged", 0) == 0)
            {
                Check(mode != null && mode.DemolishMode, "进入拆除模式");
                SessionState.SetInt(K + "BoxDragged", 1);
                DragWorld(new Vector3(x, 0f, y - 1), new Vector3(x + 5, 0f, y), 0);
                return;
            }
            if (inStep < 1.2)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool gone = Enumerable.Range(0, 6).All(i => !Campaign.Logistics.BeltNetworkService.Kernel.HasCell(x + i, y));
            Check(gone && state.Scrap + GroundScrap(state) == SessionState.GetInt(K + "BeltScrap", -1) + 6 && !UiConfirmDialog.IsOpen,
                $"框选拆除：6 格传送带拆掉、全额返还 6 废料（库存 {state.Scrap}，仓满时返还留在地上），少量且不含关键建筑不弹确认；状态行“{mode?.StatusText}”");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.RelocateMode));
            Next(105, "按搬迁键（默认 E）");
        }

        private const string RepairBayId = Campaign.Regions.HomeValleyLayout.RegionId + ":repair_bay";

        private static int GroundScrap(CampaignState state) =>
            state?.GroundItems?.Where(g => g != null && g.RegionId == Campaign.Regions.HomeValleyLayout.RegionId && g.ResourceType == "Scrap").Sum(g => g.Amount) ?? 0;

        private static void StepBuildRelocatePicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            BuildingRecord bay = state?.BuildingRecords?.FirstOrDefault(b => b.BuildingId == RepairBayId);
            Check(mode != null && mode.RelocateMode && bay != null, "进入搬迁模式");
            if (bay == null)
            {
                Finish("找不到维修台");
                return;
            }
            ClickWorld(new Vector3(bay.GridX, 0f, bay.GridY));
            Next(106, "左键点维修台（点起来跟着鼠标）");
        }

        private static void StepBuildRelocatePlanned(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            if (SessionState.GetInt(K + "RelocateClicked", 0) == 0)
            {
                Check(mode != null && mode.CarryBuildingId == RepairBayId, $"维修台被点起来（{mode?.StatusText}）");
                BuildingRecord bay = state.BuildingRecords.First(b => b.BuildingId == RepairBayId);
                Campaign.Grid.GridCell? to = null;
                for (int r = 0; r <= 8 && to == null; r++)
                {
                    for (int dy = -r; dy <= r && to == null; dy++)
                    {
                        for (int dx = -r; dx <= r && to == null; dx++)
                        {
                            var c = new Campaign.Grid.GridCell(bay.GridX - 6 + dx, bay.GridY + 4 + dy);
                            if (Campaign.Grid.HomeGridService.ValidatePlacement(state, "repair_bay", c, (int)bay.Rotation, asPlayerPlacement: false,
                                    ignoreBuildingId: RepairBayId, checkCost: false).Ok)
                            {
                                to = c;
                            }
                        }
                    }
                }
                if (to == null)
                {
                    Finish("维修台附近找不到能搬去的位置");
                    return;
                }
                SessionState.SetInt(K + "RelocateClicked", 1);
                SessionState.SetInt(K + "RelocX", to.Value.X);
                SessionState.SetInt(K + "RelocY", to.Value.Y);
                ClickWorld(new Vector3(to.Value.X, 0f, to.Value.Y));
                return;
            }
            if (inStep < 1.2)
            {
                return;
            }
            BuildingRecord ghost = Campaign.Grid.HomeGridService.FindRelocationGhost(state, RepairBayId);
            BuildingRecord original = state.BuildingRecords.First(b => b.BuildingId == RepairBayId);
            Check(ghost != null && ghost.GridX == SessionState.GetInt(K + "RelocX", 0) && ghost.GridY == SessionState.GetInt(K + "RelocY", 0)
                  && original.ConstructionState == BuildingConstructionState.Operational && GhostVisual(ghost) != null,
                $"左键点新位置：出现搬迁目标虚影（画面上有扁平方块），维修台在完工前照常在原地；状态行“{mode?.StatusText}”");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.DemolishMode));
            SessionState.SetString(K + "GhostId", ghost?.BuildingId ?? string.Empty);
            Next(107, "按拆除模式键，左键点搬迁虚影（取消搬迁）");
        }

        private static void StepBuildRelocateCancelled(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (SessionState.GetInt(K + "GhostClicked", 0) == 0)
            {
                SessionState.SetInt(K + "GhostClicked", 1);
                ClickWorld(new Vector3(SessionState.GetInt(K + "RelocX", 0), 0f, SessionState.GetInt(K + "RelocY", 0)));
                return;
            }
            if (inStep < 1.0)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(Campaign.Grid.HomeGridService.FindRelocationGhost(state, RepairBayId) == null
                  && state.BuildingRecords.Any(b => b.BuildingId == RepairBayId && b.ConstructionState == BuildingConstructionState.Operational)
                  && mode.StatusText.Contains("已取消搬迁"),
                $"拆除模式点搬迁虚影：取消搬迁，维修台留在原处（“{mode?.StatusText}”）");
            Check(ClickUitk("[BuildModeHudHost]", "BuildDemolish") && !mode.DemolishMode, "点“拆除模式”按钮退出拆除模式");
            SessionState.SetInt(K + "GridBefore", GameSettings.BuildGridLinesEnabled ? 1 : 0);
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.ToggleGridLines));
            Next(108, "按格线开关（默认 G）");
        }

        private static void StepBuildGridToggled(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            bool before = SessionState.GetInt(K + "GridBefore", 1) == 1;
            if (SessionState.GetInt(K + "GridToggledBack", 0) == 0)
            {
                Check(GameSettings.BuildGridLinesEnabled == !before && BuildModeHudUIToolkit.Instance.GridToggleText.Contains(before ? "关" : "开"),
                    $"按 G：格线{(before ? "关掉" : "打开")}，按钮写“{BuildModeHudUIToolkit.Instance?.GridToggleText}”");
                SessionState.SetInt(K + "GridToggledBack", 1);
                PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.ToggleGridLines));
                return;
            }
            if (inStep < 1.0)
            {
                return;
            }
            Check(GameSettings.BuildGridLinesEnabled == before, "再按 G：格线恢复");
            _buildMouse = Vector3.zero;
            InputRouter.DebugSetReader(null);
            Next(110, "FG0-ARCH-05：检查地形叠加层（按区块跟随镜头）");
        }

        /// <summary>FG3-LOG-01：模拟按住左键从 <paramref name="from"/> 拖到 <paramref name="to"/>（下一帧按下，鼠标分几帧移过去，再松开）。</summary>
        private static void DragWorld(Vector3 from, Vector3 to, int button)
        {
            Camera cam = Camera.main;
            Vector3 a = cam != null ? cam.WorldToScreenPoint(from) : Vector3.zero;
            Vector3 b = cam != null ? cam.WorldToScreenPoint(to) : Vector3.zero;
            InputRouter.DebugSetReader(new DragReader
            {
                From = new Vector3(a.x, a.y, 0f),
                To = new Vector3(b.x, b.y, 0f),
                Button = button,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 6,
            });
        }

        /// <summary>拖拽输入替身：按下那一帧在起点，之后几帧线性移到终点，松开那一帧在终点。</summary>
        private sealed class DragReader : IInputReader
        {
            public Vector3 From;
            public Vector3 To;
            public int Button;
            public int DownFrame;
            public int UpFrame;

            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => button == Button && Time.frameCount == DownFrame;
            public bool GetMouseButtonUp(int button) => button == Button && Time.frameCount == UpFrame;
            public Vector3 MousePosition
            {
                get
                {
                    float t = Mathf.Clamp01((Time.frameCount - DownFrame) / (float)Mathf.Max(1, UpFrame - 1 - DownFrame));
                    return Vector3.Lerp(From, To, t);
                }
            }
            public float MouseScrollDelta => 0f;
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
            // FG0-ARCH-03：机器的画面对象是 MachineView（被观察的地点才有），逻辑句柄在战斗内核里。
            Campaign.Regions.MachineView hauler = Object.FindObjectsByType<Campaign.Regions.MachineView>(
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
            return InvokeClickable(b);
        }

        /// <summary>向 UI Toolkit 元素派发一次指针事件（位置为面板坐标），与真实鼠标经过同一条派发链（含根上的 TrickleDown 回调）；
        /// 派发后释放该元素可能持有的指针捕获，免得影响后续真实鼠标步骤。</summary>
        private static void SendUitkPointer<T>(VisualElement target, Vector2 panelPosition, EventType type, int button)
            where T : PointerEventBase<T>, new()
        {
            var ev = new Event { type = type, mousePosition = panelPosition, button = button, clickCount = 1 };
            using (T e = PointerEventBase<T>.GetPooled(ev))
            {
                e.target = target;
                target.SendEvent(e);
            }
            if (type == EventType.MouseUp)
            {
                IEventHandler capturing = target.panel?.GetCapturingElement(PointerId.mousePointerId);
                capturing?.ReleasePointer(PointerId.mousePointerId);
            }
        }

        private static bool InvokeClickable(UnityEngine.UIElements.Button b)
        {
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
            RightClickWorld(generator.position);
            Next(11, "鼠标右键点受损的发电机（情境命令：下令修复）");
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
            Check(WorldPlanetView.TerrainShown && WorldPlanetView.Terrain != null && WorldPlanetView.Terrain.IsRelief,
                "建造模式里地貌起伏网格照常显示（半透明地格参考线叠在它上面，FG-GAP-021）");
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
            // FG2-FW-01：44 条固件入表后，电路面板的“未解锁”行只数没拿到的固件（不逐条列 40 个名字），固件下拉只列已解锁的、名字来自文本键。
            {
                GameObject circuitHost = GameObject.Find("[HomeValleyCircuitBoardHost]");
                VisualElement circuitRoot = circuitHost != null ? circuitHost.GetComponent<UIDocument>()?.rootVisualElement : null;
                string locked = circuitRoot?.Q<Label>("LockedContentHintLabel")?.text ?? string.Empty;
                int lockedFw = Campaign.Content.FirmwareCatalog.All.Keys.Count(id => !Campaign.Content.MechanicalContentUnlock.IsUnlocked(CampaignSession.Current, id));
                string coolant = Localization.GameText.Get("firmware.fw_coolant.name");
                Check(Campaign.Content.FirmwareCatalog.All.Count == Campaign.Content.FirmwareCatalog.ExpectedCount && lockedFw > 0
                      && locked.Contains(Localization.GameText.Format("circuit.locked_firmware_count", lockedFw)) && !locked.Contains(coolant)
                      && !Localization.GameText.ContainsMarker(locked) && !FgFirmwareMigrationSelfCheck.InternalContentId.IsMatch(locked),
                    $"电路面板“未解锁”行：{lockedFw} 条没拿到的固件只计数（“{locked}”），不列内部 ID");
                // FG2-FW-01 修复轮：蓝图派系标签存阵营键，面板上显示 faction.<key> 文本（不漏出 reclaim / clarity 这类内部键）。
                string summary = circuitRoot?.Q<Label>("CostSummaryLabel")?.text ?? string.Empty;
                string factionLine = summary.Split('\n').FirstOrDefault(l => l.StartsWith("派系：")) ?? string.Empty;
                string[] factionKeys = { "reclaim", "silent", "foundry", "clarity", "overclock" };
                Check(factionLine.Length > 0 && !factionKeys.Any(k => factionLine.Contains(k)) && !Localization.GameText.ContainsMarker(factionLine)
                      && (factionLine.Contains("无") || factionKeys.Any(k => factionLine.Contains(Localization.GameText.Get("faction." + k)))),
                    $"电路面板派系行显示阵营名而不是内部键（“{factionLine}”）");
                // FG2-FW-02（FGR-FW-010 / 011）：主组件下拉带载体、5 种载体的作战组件都能选；固件区写出已装固件在当前主组件载体上的读法。
                DropdownField primary = circuitRoot?.Q<DropdownField>("PrimaryDropdown");
                List<string> choices = primary?.choices ?? new List<string>();
                // FG2-VFX-02（FG-GAP-051）：设计案 5.6 其余 5 个组件也在正式下拉里（旋刃环 / 哨戒桩 / 震荡脉冲器 / 拆解钳；尖刺外装在功能组件下拉）。
                string[] carrierComps = { Campaign.Content.ComponentCatalog.CompRamId, Campaign.Content.ComponentCatalog.CompDroneBayId,
                    Campaign.Content.ComponentCatalog.CompCoronaId, Campaign.Content.ComponentCatalog.CompSprayerId,
                    Campaign.Content.ComponentCatalog.CompOrbitId, Campaign.Content.ComponentCatalog.CompSentryId,
                    Campaign.Content.ComponentCatalog.CompPulserId, Campaign.Content.ComponentCatalog.CompClawId };
                bool allCarriers = carrierComps.All(id => choices.Any(c => c.Contains(Campaign.Content.ComponentCatalog.All[id].DisplayName)
                                                                          && c.Contains(Campaign.Content.CarrierReadings.SubtypeName(id))));
                List<string> utilChoices = circuitRoot?.Q<DropdownField>("UtilityDropdown")?.choices ?? new List<string>();
                string spikesName = Campaign.Content.ComponentCatalog.All[Campaign.Content.ComponentCatalog.FuncSpikesId].DisplayName;
                Check(utilChoices.Any(c => c.Contains(spikesName)), $"电路面板：功能组件下拉有尖刺外装（{string.Join(" / ", utilChoices)}）");
                UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
                string readingLabel = circuitRoot?.Q<Label>("FirmwareReadingLabel")?.text ?? "(无标签)";
                string expected = panel?.Board != null ? UI.CircuitBoard.CircuitBoardPanelUIToolkit.FirmwareReadingText(panel.Board) : null;
                bool hasFw = panel?.Board != null && panel.Board.FirmwareSlots.Any(f => !string.IsNullOrEmpty(f));
                Check(allCarriers && expected != null && readingLabel == expected && (!hasFw || readingLabel.Length > 0) && !Localization.GameText.ContainsMarker(readingLabel)
                      && !FgFirmwareMigrationSelfCheck.InternalContentId.IsMatch(readingLabel),
                    $"电路面板：主组件下拉有格斗 / 无人机 / 力场 / 布区组件并标出载体；固件读法行“{readingLabel}”");
                // 走正式下拉：主组件换成液压刺锤、第 1 位固件选寻的 → 读法行只写格斗上的读法；再点两次“撤销”回到原样。
                DropdownField fw0 = circuitRoot?.Q<DropdownField>("Firmware0Dropdown");
                string ramName = Campaign.Content.ComponentCatalog.All[Campaign.Content.ComponentCatalog.CompRamId].DisplayName;
                string homingName = Campaign.Signal.FirmwareKinds.DisplayName(Campaign.Content.FirmwareCatalog.FwHomingId);
                string primaryBefore = panel?.Board?.PrimaryId;
                string fwBefore = panel?.Board?.FirmwareSlots[0];
                string ramChoice = choices.FirstOrDefault(c => c.StartsWith(ramName, StringComparison.Ordinal));
                if (primary != null && ramChoice != null)
                {
                    primary.value = ramChoice;
                }
                string homingChoice = fw0?.choices?.FirstOrDefault(c => c.StartsWith(homingName, StringComparison.Ordinal));
                if (fw0 != null && homingChoice != null)
                {
                    fw0.value = homingChoice;
                }
                string meleeLine = circuitRoot?.Q<Label>("FirmwareReadingLabel")?.text ?? string.Empty;
                string meleeReading = Campaign.Signal.FirmwareKinds.Reading(Campaign.Content.FirmwareCatalog.FwHomingId, Campaign.Signal.FirmwareCarrier.Melee);
                string projReading = Campaign.Signal.FirmwareKinds.Reading(Campaign.Content.FirmwareCatalog.FwHomingId, Campaign.Signal.FirmwareCarrier.Projectile);
                bool changed = panel?.Board?.PrimaryId == Campaign.Content.ComponentCatalog.CompRamId && panel.Board.FirmwareSlots[0] == Campaign.Content.FirmwareCatalog.FwHomingId;
                Check(changed && meleeLine.Contains(meleeReading) && !meleeLine.Contains(projReading),
                    $"正式下拉换成液压刺锤 + 寻的：读法行只写格斗上的读法（“{meleeLine}”）");
                // FG2-FW-02 修复轮（B13）：预览摘要写的是内核实际每发伤害（液压刺锤 = 表里的伤害），不是旧器官编译的归一化总伤害。
                string previewSummary = circuitRoot?.Q<Label>("PreviewSummaryLabel")?.text ?? string.Empty;
                Campaign.Content.CarrierReadings.TryGetComponent(Campaign.Content.ComponentCatalog.CompRamId, out GameConfig.fg.CombatComponent ramRow);
                string ramHit = (ramRow?.Damage ?? -1f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                Check(changed && ramRow != null && previewSummary.Contains(ramHit) && !Localization.GameText.ContainsMarker(previewSummary),
                    $"电路面板预览摘要写内核实际每发伤害（液压刺锤 {ramHit}）：“{previewSummary.Replace('\n', ' ')}”");
                bool undone = ClickUitk("[HomeValleyCircuitBoardHost]", "UndoButton") & ClickUitk("[HomeValleyCircuitBoardHost]", "UndoButton");
                Check(undone && panel?.Board?.PrimaryId == primaryBefore && panel?.Board?.FirmwareSlots[0] == fwBefore,
                    $"点两次“撤销”：第 1 位固件与主组件都回到打开面板时的样子（主组件 {panel?.Board?.PrimaryId ?? "无"}）");
            }
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
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenResearch));
            Next(62, "在命名框里按研发树键（尚未开放的动作）");
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
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenSignalCore));
            Next(150, "FG1-SIG-01：按信号核键（默认 P）打开信号核面板");
        }

        // ── FG1-SIG-01：信号核（HUD 信号位置、刻印、装入、预设、远征准备入口、远征途中锁定）──────────────────

        private static UIDocument SignalDoc()
        {
            GameObject host = GameObject.Find("[SignalCoreHost]");
            return host != null ? host.GetComponent<UIDocument>() : null;
        }

        private static void StepSignalOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            Check(hud != null && UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && hud.PanelVisible && hud.HudVisible && InputRouter.IsModalOwner(hud),
                $"按 P 打开信号核面板（模态）；HUD“{hud?.LocationText}”“{hud?.EntryText}”");
            Check(hud != null && hud.LocationText == Localization.GameText.Get("signal.hud.at_core") && hud.EntryText.EndsWith("0/2", StringComparison.Ordinal)
                  && hud.SlotText(0).Contains("1") && hud.SlotText(2).Contains("T1"),
                $"信号在归还核心；初始 2 槽（1 号槽“{hud?.SlotText(0)}”，3 号槽“{hud?.SlotText(2).Replace('\n', ' ')}”）");
            CheckNoTextMarkers("信号核面板");
            DropdownField print = SignalDoc()?.rootVisualElement?.Q<DropdownField>("SignalPrintChoice");
            string overload = Localization.GameText.Get("firmware.fw_overload.name");
            string choice = print?.choices?.FirstOrDefault(c => c.Contains(overload));
            Check(choice != null, $"刻印下拉里有过载（{string.Join("／", print?.choices ?? new System.Collections.Generic.List<string>())}）");
            // FG2-FW-01：刻印下拉 = 当前可刻印的固件（至少基础蓝图库 4 条，没拿到的不列），每项是“种类标注 + 表里的名字”，不漏内部 ID 与缺失标记。
            System.Collections.Generic.List<string> printable = Campaign.Signal.SignalCoreService.PrintableFirmware(CampaignSession.Current);
            System.Collections.Generic.List<string> printChoices = print?.choices ?? new System.Collections.Generic.List<string>();
            Check(printable.Count >= 4 && new[] { Campaign.Content.FirmwareCatalog.FwHomingId, Campaign.Content.FirmwareCatalog.FwOverloadId, Campaign.Content.FirmwareCatalog.FwSplitId, Campaign.Content.FirmwareCatalog.FwTrailId }.All(printable.Contains)
                  && printable.Count < Campaign.Content.FirmwareCatalog.ExpectedCount && printChoices.Count == printable.Count
                  && printable.All(id => printChoices.Any(c => c.EndsWith(" " + Campaign.Signal.FirmwareKinds.DisplayName(id), StringComparison.Ordinal)))
                  && printChoices.All(c => !Localization.GameText.ContainsMarker(c) && !FgFirmwareMigrationSelfCheck.InternalContentId.IsMatch(c)),
                $"刻印下拉只列可刻印的 {printable.Count} 条固件（{string.Join("、", printable.Select(Campaign.Signal.FirmwareKinds.DisplayName))}），名字来自文本键，没有内部 ID");
            if (print != null && choice != null)
            {
                print.value = choice;
            }
            SessionState.SetInt(K + "SigScrap", CampaignSession.Current.Scrap);
            Check(ClickUitk("[SignalCoreHost]", "SignalPrint"), "点“刻印”");
            Next(151, "选中过载并点“刻印”（装配站刻印一枚固件芯片）");
        }

        private static void StepSignalPrinted(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            CampaignState st = CampaignSession.Current;
            PrimitiveChipRecord chip = Campaign.Primitive.PrimitiveInventory.Find(st, hud?.SelectedPartId);
            Check(chip != null && chip.CardDefId == Campaign.Content.FirmwareCatalog.FwOverloadId && chip.State == PrimitiveChipState.Bag
                  && st.Scrap == SessionState.GetInt(K + "SigScrap", 0) - Campaign.Signal.SignalCoreService.FirmwareChipPrintScrap && !hud.FeedbackIsError,
                $"刻印成功：过载芯片进基元仓并被选中，扣 {Campaign.Signal.SignalCoreService.FirmwareChipPrintScrap} 废料（“{hud?.FeedbackText}”）");
            Check(ClickUitk("[SignalCoreHost]", "SignalEquip"), "点“装入 1 号槽”");
            Next(152, "点“装入 1 号槽”（FGJ-M1 第 2 步：给信号核装上过载）");
        }

        private static void StepSignalEquipped(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            CampaignState st = CampaignSession.Current;
            Check(Campaign.Signal.SignalCoreService.SlotContentId(st, 0) == Campaign.Content.FirmwareCatalog.FwOverloadId
                  && Campaign.Signal.SignalCoreService.SlotChip(st, 0).State == PrimitiveChipState.SignalCore
                  && hud.EntryText.EndsWith("1/2", StringComparison.Ordinal) && hud.SlotText(0).Contains(Localization.GameText.Get("firmware.fw_overload.name")),
                $"过载装进 1 号槽：实例状态 = 信号核；HUD“{hud.EntryText}”；槽位“{hud.SlotText(0)}”");
            TextField name = SignalDoc()?.rootVisualElement?.Q<TextField>("SignalPresetName");
            if (name != null)
            {
                name.value = "攻坚";
            }
            Check(ClickUitk("[SignalCoreHost]", "SignalPresetSave"), "点“另存为新预设”");
            Next(153, "预设名填“攻坚”，点“另存为新预设”");
        }

        private static void StepSignalPresetSaved(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            CampaignState st = CampaignSession.Current;
            Check(st.SignalCore.Presets.Length == 1 && st.SignalCore.Presets[0].Name == "攻坚" && st.SignalCore.ActivePresetId == st.SignalCore.Presets[0].PresetId
                  && hud.PresetActiveText.Contains("攻坚"), $"预设已保存并成为当前预设：“{hud.PresetActiveText}”");
            SignalDoc()?.rootVisualElement?.Q<TextField>("SignalPresetName")?.Blur();
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenSignalCore));
            Next(154, "再按 P 关闭信号核面板");
        }

        private static void StepSignalClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            Check(!UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && hud != null && !hud.PanelVisible && !InputRouter.IsModalOwner(hud) && !PauseMenuUIToolkit.IsOpen,
                "再按 P 关闭信号核面板（模态释放、暂停菜单没开）");
            Check(hud != null && hud.Exposure.EntryText.StartsWith(Localization.GameText.Get("exposure.hud.button").Split('{')[0], StringComparison.Ordinal),
                $"HUD 常驻暴露值“{hud?.Exposure.EntryText}”（FGU-44 入口）");
            Transform bench = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeAnalysisBench);
            if (bench == null)
            {
                Finish("场景里找不到解析台");
                return;
            }
            ClickWorld(bench.position);
            Next(233, "FG2-E2E-01：鼠标左键点解析台（“数据复原”栏，FG-GAP-050 的固件临时来源）");
        }

        // ── FG2-E2E-01：解析台“数据复原”（点解析台 → 栏目与候选 → 选冷却液点“复原”：按此刻状态拒绝写原因 / 弹确认框点取消 → 关闭）──────────

        private static void StepRestoreOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Analysis.AnalysisPanelUIToolkit panel = UI.Analysis.AnalysisPanelUIToolkit.Instance;
            CampaignState st = CampaignSession.Current;
            System.Collections.Generic.List<string> expect = Campaign.Signal.FirmwareRestoreService.Candidates(st);
            bool open = GameRoot.HomeValley != null && GameRoot.HomeValley.IsAnalysisPanelOpen && panel != null;
            Check(open && panel.RestoreChoiceIds.Count == expect.Count && expect.Count > 0 && panel.RestoreChoiceIds.SequenceEqual(expect)
                  && LabelText("[HomeValleyAnalysisHost]", "RestoreTitle") == Localization.GameText.Get("analysis.restore.title")
                  && panel.RestoreDetailText.Length > 0,
                $"点解析台打开解析面板：“数据复原”栏列出 {panel?.RestoreChoiceIds.Count} 条可复原固件（= 服务层候选 {expect.Count} 条）；说明“{panel?.RestoreDetailText.Replace("\n", " / ")}”");
            CheckNoTextMarkers("解析台数据复原");
            GameObject host = GameObject.Find("[HomeValleyAnalysisHost]");
            DropdownField dd = host?.GetComponent<UIDocument>()?.rootVisualElement?.Q<DropdownField>("RestoreDropdown");
            int idx = panel != null ? panel.RestoreChoiceIds.ToList().IndexOf("fw_coolant") : -1;
            if (dd != null && idx >= 0 && idx < dd.choices.Count)
            {
                dd.value = dd.choices[idx];
            }
            Campaign.Signal.FirmwareRestoreService.Result pre = Campaign.Signal.FirmwareRestoreService.Check(st, "fw_coolant");
            SessionState.SetString(K + "RestorePre", pre.Success ? "ok" : pre.Code);
            SessionState.SetInt(K + "RestoreTech", st.TechData);
            SessionState.SetInt(K + "RestoreDenied", Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied));
            Check(idx >= 0 && ClickUitk("[HomeValleyAnalysisHost]", "RestoreButton"), $"下拉选冷却液、点“复原”（此刻预检：{(pre.Success ? "可复原" : pre.Message)}）");
            Next(234, "看“复原”的结果");
        }

        private static void StepRestoreAsked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState st = CampaignSession.Current;
            UI.Analysis.AnalysisPanelUIToolkit panel = UI.Analysis.AnalysisPanelUIToolkit.Instance;
            string pre = SessionState.GetString(K + "RestorePre", string.Empty);
            bool unchanged = st.TechData == SessionState.GetInt(K + "RestoreTech", -1) && !Campaign.Content.MechanicalContentUnlock.IsUnlocked(st, "fw_coolant");
            if (pre == "ok")
            {
                // 有电、技术数据够：弹出不可撤销的确认框；冒烟点“取消”，什么都不变（真复原由旅程 FGJ-M2 走）。
                bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title == Localization.GameText.Get("analysis.restore.confirm.title") && UiConfirmDialog.Current.Irreversible;
                bool cancelled = ClickUitk("[UiKitOverlayHost]", "ConfirmCancel") && !UiConfirmDialog.IsOpen;
                Check(asked && cancelled && unchanged, "点“复原”弹出不可撤销的确认框，点“取消”后技术数据与解锁都不变");
            }
            else
            {
                string result = panel?.ResultText ?? string.Empty;
                Check(!UiConfirmDialog.IsOpen && unchanged && result == Campaign.Signal.FirmwareRestoreService.Check(st, "fw_coolant").Message
                      && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) > SessionState.GetInt(K + "RestoreDenied", 0),
                    $"此刻不能复原（{pre}）：不弹确认框，结果行写明原因“{result}”+ 拒绝音，技术数据与解锁都不变");
            }
            Check(ClickUitk("[HomeValleyAnalysisHost]", "CloseButton"), "点“关闭”");
            Next(235, "关闭解析面板");
        }

        private static void StepRestoreClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsAnalysisPanelOpen && !PauseMenuUIToolkit.IsOpen, "解析面板关闭（暂停菜单没开）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
            Next(180, "FG2-FW-05：按固件库键（默认 I）打开固件库");
        }

        // ── FG2-FW-05：固件库（I 键开关、行悬停按 C 跳图鉴、批量分解跳过在用芯片）──────────────────

        private static VisualElement FwLibRow(int index)
        {
            UI.Kit.FirmwareLibraryPanelUIToolkit lib = UI.Kit.FirmwareLibraryPanelUIToolkit.Instance;
            VisualElement found = null;
            lib?.ListView?.Query<VisualElement>(className: "fl-row").ForEach(r =>
            {
                if (found == null && r.userData is int i && i == index)
                {
                    found = r;
                }
            });
            return found;
        }

        private static void StepFwLibOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Kit.FirmwareLibraryPanelUIToolkit lib = UI.Kit.FirmwareLibraryPanelUIToolkit.Instance;
            string fw = Campaign.Content.FirmwareCatalog.FwOverloadId;
            int row = -1;
            for (int i = 0; lib != null && i < lib.RowCount; i++)
            {
                if (lib.Row(i).FirmwareId == fw)
                {
                    row = i;
                    break;
                }
            }
            if (row >= 0)
            {
                lib.Select(lib.Row(row).Chip.PartId);
            }
            string detail = lib?.DetailBodyText ?? string.Empty;
            Check(UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen && lib != null && lib.PanelVisible && InputRouter.IsModalOwner(lib) && row >= 0
                  && lib.RowText(row).Contains(Localization.GameText.Format("fwlib.loc.signal", 1))
                  && detail.Contains(Localization.GameText.Format("fwlib.detail.acquire", Campaign.Signal.FirmwareKinds.AcquireText(fw))) && lib.RouteText.Length > 0 && lib.CountText.Length > 0,
                $"按 I 打开固件库：{lib?.CountText}；过载那一行“{(row >= 0 ? lib.RowText(row) : "（没找到）")}”；取用路线“{lib?.RouteText}”");
            CheckNoTextMarkers("固件库");
            // 鼠标移到过载那一行上（派发指针进入事件，走 UiTooltip 自己注册的回调），再按图鉴键。
            VisualElement r = FwLibRow(row);
            Check(r != null, "固件库列表里过载那一行已经按需建出（虚拟化列表）");
            if (r != null)
            {
                using (PointerEnterEvent enter = PointerEnterEvent.GetPooled())
                {
                    enter.target = r;
                    r.SendEvent(enter);
                }
            }
            SessionState.SetInt(K + "FwLibRow", row);
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
            Next(181, "悬停过载那一行，按图鉴键（默认 C）");
        }

        private static void StepFwLibCodexJumped(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Kit.MechanicCodexPanelUIToolkit codex = UI.Kit.MechanicCodexPanelUIToolkit.Instance;
            string id = Progression.MechanicCodex.FirmwareEntryId(Campaign.Content.FirmwareCatalog.FwOverloadId);
            Check(UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && codex != null && codex.SelectedId == id && codex.CurrentTab == Progression.MechanicCodex.TabFirmware
                  && codex.EntryTitleText == Localization.GameText.Get("firmware.fw_overload.name")
                  && codex.EntryBodyText.Contains(Localization.GameText.Format("fwlib.detail.acquire", Campaign.Signal.FirmwareKinds.AcquireText(Campaign.Content.FirmwareCatalog.FwOverloadId)))
                  && UI.Kit.CodexHoverLink.LastJumpId == id && UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen,
                $"悬停按 C 跳到图鉴固件页签的“{codex?.EntryTitleText}”（{codex?.CountText}），固件库仍在下面");
            CheckNoTextMarkers("图鉴固件页签");
            VisualElement r = FwLibRow(SessionState.GetInt(K + "FwLibRow", 0));
            if (r != null)
            {
                using (PointerLeaveEvent leave = PointerLeaveEvent.GetPooled())
                {
                    leave.target = r;
                    r.SendEvent(leave);
                }
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
            Next(182, "鼠标移开，再按 C 关闭图鉴");
        }

        private static void StepFwLibCodexClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Kit.FirmwareLibraryPanelUIToolkit lib = UI.Kit.FirmwareLibraryPanelUIToolkit.Instance;
            Check(!UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen && lib != null && lib.PanelVisible,
                "再按 C 关闭图鉴，回到固件库");
            // 负向：装在信号核里的芯片勾选后点“分解所选”——在用的跳过，不弹确认框、不删实例，写明原因。
            CampaignState st = CampaignSession.Current;
            string partId = Campaign.Signal.SignalCoreService.SlotChip(st, 0)?.PartId;
            int before = st.PrimitiveChips.Length;
            int scrap0 = Mathf.FloorToInt(st.Scrap);
            VisualElement r = FwLibRow(SessionState.GetInt(K + "FwLibRow", 0));
            bool picked = InvokeClickable(r?.Q<UnityEngine.UIElements.Button>("FwLibRowPick")) && lib != null && lib.IsPicked(partId);
            bool clicked = ClickUitk("[FirmwareLibraryHost]", "FwLibDisassemble");
            Check(picked && clicked && !UiConfirmDialog.IsOpen && st.PrimitiveChips.Length == before && Mathf.FloorToInt(st.Scrap) == scrap0
                  && Campaign.Signal.SignalCoreService.SlotChip(st, 0)?.PartId == partId && lib.FeedbackText == Localization.GameText.Get("fwlib.result.nothing"),
                $"勾选信号核里的过载、点“分解所选”：在用的跳过，不弹确认框、芯片还在 1 号槽，提示“{lib?.FeedbackText}”");
            // 搜索：搜不到时显示空状态说明。
            lib?.SetSearchText("不存在的固件名zz");
            bool emptyShown = lib != null && lib.RowCount == 0 && lib.EmptyText == Localization.GameText.Get("fwlib.empty_filtered");
            lib?.SetSearchText(string.Empty);
            Check(emptyShown && lib.RowCount >= 1, $"搜索无结果时显示“{Localization.GameText.Get("fwlib.empty_filtered")}”，清空搜索后列表复原（{lib?.RowCount} 行）");
            lib?.ClearPicks();
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
            Next(183, "再按 I 关闭固件库");
        }

        private static void StepFwLibClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Kit.FirmwareLibraryPanelUIToolkit lib = UI.Kit.FirmwareLibraryPanelUIToolkit.Instance;
            Check(!UI.Kit.FirmwareLibraryPanelUIToolkit.IsOpen && lib != null && !lib.PanelVisible && !InputRouter.IsModalOwner(lib) && !PauseMenuUIToolkit.IsOpen,
                "再按 I 关闭固件库（模态释放、暂停菜单没开）");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.OpenExposure));
            Next(196, "FG1-SIG-06：按暴露面板键（默认 Alt+P）打开暴露面板");
        }

        // ── FG1-SIG-06：暴露面板（Alt+P 打开、来源与规则说明、点 HUD 按钮关闭）──────────────────

        private static void StepExposureOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            UI.SignalCore.ExposurePanelView ex = hud?.Exposure;
            CampaignState st = CampaignSession.Current;
            Check(ex != null && ex.IsOpen && ex.PanelVisible && !UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen,
                "按 Alt+P 打开暴露面板（信号核面板没被误开、暂停菜单没开）");
            Check(ex != null && ex.RulesText.Contains(Localization.GameText.Get("exposure.panel.rules").Split('（')[0]) && ex.NextText.Length > 0
                  && (ex.RecentCount > 0 || ex.RecentEmptyVisible) && ex.RecentCount == Math.Min(UI.SignalCore.ExposurePanelView.RecentShown, st.SignalExposureEvents.Length),
                $"暴露面板：“{ex?.ValueText}”“{ex?.NextText}”；最近来源 {ex?.RecentCount} 条（存档里 {st.SignalExposureEvents.Length} 条）；规则说明“{ex?.RulesText}”");
            CheckNoTextMarkers("暴露面板");
            Check(ClickUitk("[SignalCoreHost]", "SignalExposureEntry"), "再点 HUD 上的“暴露”按钮");
            Next(197, "再点 HUD“暴露”按钮关闭面板");
        }

        private static void StepExposureClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            Check(hud != null && !hud.Exposure.IsOpen && !hud.Exposure.PanelVisible && !PauseMenuUIToolkit.IsOpen,
                "点 HUD“暴露”按钮关闭暴露面板（模态释放）");
            Check(ClickUitk("[HomeValleyCircuitBoardHost]", "EntryToggleButton"), "点“蓝图编辑器”入口");
            Next(161, "FG1-SIG-02：再打开蓝图编辑器（FGJ-M1 第 1 步：标出接入口，看双态预览）");
        }

        // ── FG1-SIG-02：接入口与双态编译预览（点选空格 → 标为接入口 → 两栏 + 差异 → Ctrl+Z / Ctrl+Y → 0 号格被拒 → Esc）──────

        private static UI.CircuitBoard.CircuitBoardPanelUIToolkit CircuitPanel()
        {
            GameObject host = GameObject.Find("[HomeValleyCircuitBoardHost]");
            return host != null ? host.GetComponent<UI.CircuitBoard.CircuitBoardPanelUIToolkit>() : null;
        }

        private static void StepUplinkEditorOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
            Campaign.Blueprint.BlueprintCircuitBoard board = panel != null ? panel.Board : null;
            Check(GameRoot.HomeValley != null && GameRoot.HomeValley.IsCircuitBoardPanelOpen && board != null && !board.HasUplink
                  && ReferenceEquals(UI.Kit.UiUndoRouter.Owner, panel),
                "蓝图编辑器打开（撤销 / 重做快捷键归电路编辑器）；当前蓝图还没有接入口");
            int target = -1;
            for (int i = 1; board != null && i < Campaign.Blueprint.BlueprintCircuitLayout.SlotCount - 1; i++)
            {
                if (string.IsNullOrEmpty(board.SlotContentIds[i]) && board.IsOnSourceSinkPath(i))
                {
                    target = i;
                    break;
                }
            }
            Check(target > 0, $"找到导线经过的空格 {target} 号（按当前蓝图的导线找，不写死格号）");
            SessionState.SetInt(K + "UplinkSlot", target);
            Check(ClickUitk("[HomeValleyCircuitBoardHost]", "Slot" + target), $"点选 {target} 号格");
            Next(162, $"点选 {target} 号空格");
        }

        private static void StepUplinkSlotPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
            Check(panel != null && panel.UplinkView.ToggleButton != null
                  && panel.UplinkView.ToggleButton.text == Localization.GameText.Get("circuit.uplink.mark"),
                $"检查器显示“{panel?.UplinkView.ToggleButton?.text}”按钮");
            Check(ClickUitk("[HomeValleyCircuitBoardHost]", "UplinkToggleButton"), "点“标为接入口”");
            Next(163, "点“标为接入口”");
        }

        private static void StepUplinkMarked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int target = SessionState.GetInt(K + "UplinkSlot", -1);
            UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
            UI.CircuitBoard.CircuitUplinkView view = panel?.UplinkView;
            Campaign.Blueprint.BlueprintCircuitBoard board = panel?.Board;
            Check(board != null && board.HasUplink && board.UplinkSlot == target && view.SlotShowsUplink(target)
                  && view.SlotTagText(target) == Localization.GameText.Get("circuit.uplink.tag"),
                $"{target} 号格成为接入口：格子上有菱形图标与“{view?.SlotTagText(target)}”文字标注");
            Check(view != null && view.Last != null && view.AiLine(0).Contains("空") && view.UplinkedLine(0).Contains("过载")
                  && view.UplinkedLineHighlighted(0) && !view.UplinkedLineHighlighted(1) && view.DiffHighlighted
                  && view.Last.Uplinked.UplinkFirmwareIds.Contains(Campaign.Content.FirmwareCatalog.FwOverloadId)
                  && view.Last.Ai.UplinkFirmwareIds.Length == 0,
                $"双态预览：AI 驾驶时“{view?.AiLine(0)}”｜你接入时“{view?.UplinkedLine(0)}”（高亮）；差异“{view?.DiffText.Replace("\n", " / ")}”");
            CheckNoTextMarkers("电路编辑器双态预览");
            SessionState.SetInt(K + "LockedBefore", Notifications.NotificationCenter.Toasts.Count(e => e.Type.Id == "feature_locked"));
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.Undo));
            Next(164, "按撤销键（默认 Ctrl+Z）撤销标记");
        }

        private static void StepUplinkUndone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int target = SessionState.GetInt(K + "UplinkSlot", -1);
            UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
            int lockedNow = Notifications.NotificationCenter.Toasts.Count(e => e.Type.Id == "feature_locked");
            Check(panel?.Board != null && !panel.Board.HasUplink && !panel.UplinkView.SlotShowsUplink(target) && UI.Kit.UiUndoRouter.LastResult == 1
                  && lockedNow == SessionState.GetInt(K + "LockedBefore", 0),
                "Ctrl+Z 撤销了接入口（格子标记消失），没有弹“后续版本开放”");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.Redo));
            Next(165, "按重做键（默认 Ctrl+Y）");
        }

        private static void StepUplinkRedone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int target = SessionState.GetInt(K + "UplinkSlot", -1);
            UI.CircuitBoard.CircuitBoardPanelUIToolkit panel = CircuitPanel();
            Check(panel?.Board != null && panel.Board.HasUplink && panel.Board.UplinkSlot == target && panel.UplinkView.SlotShowsUplink(target)
                  && UI.Kit.UiUndoRouter.LastResult == 2,
                "Ctrl+Y 重做：接入口回来");
            // 负向：0 号格（电源源点）不能标为接入口——点选 0 号格再点按钮，给原因，原接入口不动。
            Check(ClickUitk("[HomeValleyCircuitBoardHost]", "Slot0") && ClickUitk("[HomeValleyCircuitBoardHost]", "UplinkToggleButton"),
                "点选 0 号格，再点“标为接入口”");
            string reason = LabelText("[HomeValleyCircuitBoardHost]", "SaveResultLabel");
            Check(reason == Localization.GameText.Get("circuit.uplink.reason.source_slot") && panel.Board.UplinkSlot == target,
                $"0 号格被拒：“{reason}”，{target} 号格仍是接入口");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(166, "按 Esc 关闭蓝图编辑器（草稿不保存）");
        }

        private static void StepUplinkEditorClosed(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(GameRoot.HomeValley != null && !GameRoot.HomeValley.IsCircuitBoardPanelOpen && !PauseMenuUIToolkit.IsOpen && !UI.Kit.UiUndoRouter.HasTarget,
                "Esc 关闭蓝图编辑器（暂停菜单没开，撤销快捷键交还）");
            Next(167, "FG1-SIG-03 接入旅程：真实鼠标选中家园机器");
        }

        // ── FG1-SIG-03：接入、机器列表 / Tab 切换、离开；战略暂停中发起、Esc 取消（真实鼠标 / 按键）────────────

        /// <summary>家园里可以接入的机器（存活、不在装配站、有表现对象），按编号取第一台（不写死编号）。</summary>
        private static Campaign.Regions.HomeValleyMachineMarker SigCandidate(int exclude) =>
            SigCandidateIn(GameRoot.HomeValley?.Combat, Campaign.Regions.HomeValleyLayout.RegionId, exclude);

        /// <summary>某地点里现在就能接入的机器（存活、不在装配站、有表现对象、接入校验通过——不在干扰场等），按编号取第一台。</summary>
        private static Campaign.Regions.HomeValleyMachineMarker SigCandidateIn(Campaign.Combat.CombatSite site, string regionId, int exclude)
        {
            if (site == null)
            {
                return null;
            }
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(r => r != null && r.IsAlive && !r.IsInFactory
                         && r.RegionId == regionId && r.LogicId != exclude).OrderBy(r => r.LogicId))
            {
                if (site.TryGetMachineMarker(m.LogicId, out Campaign.Regions.HomeValleyMachineMarker mk) && mk.View != null
                    && Campaign.Signal.SignalUplinkService.Validate(CampaignSession.Current, m.LogicId, out _) == Campaign.Signal.UplinkFailure.None)
                {
                    return mk;
                }
            }
            return null;
        }

        private static bool SigSiteActive(string regionId) =>
            regionId == Campaign.Regions.FracturedCityLayout.RegionId ? GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive
            : regionId == Campaign.Regions.FoundryOutpostLayout.RegionId ? GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive
            : GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;

        private static string SigLabel(int logicId) => Campaign.Signal.SignalPresence.MachineLabel(logicId);

        /// <summary>命令栏机器列表（候选条）里点一台：按按钮文字“#编号”找到按钮，走按钮自己的 Clickable（与鼠标点击同一回调）。</summary>
        private static bool ClickMachineList(int logicId)
        {
            GameObject host = GameObject.Find("[RegionCommandBarHost]");
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            ScrollView strip = doc?.rootVisualElement?.Q<ScrollView>("ControlCandidateStrip");
            if (strip == null || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
            {
                return false;
            }
            UnityEngine.UIElements.Button b = strip.Query<UnityEngine.UIElements.Button>().ToList().FirstOrDefault(x => x.text == "#" + rec.DisplayNumber);
            return b != null && InvokeClickable(b);
        }

        private static void StepSigUplinkSelect(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyMachineMarker m = SigCandidate(0);
            if (m == null)
            {
                Finish("家园里没有可以接入的机器");
                return;
            }
            SessionState.SetInt(K + "SigA", m.LogicId);
            ClickWorld(m.View.transform.position);
            Next(168, $"左键点家园里的机器 {SigLabel(m.LogicId)}");
        }

        private static void StepSigUplinkPress(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int a = SessionState.GetInt(K + "SigA", 0);
            Check(GameRoot.HomeValley.SelectedMachineLogicId == a, $"左键选中了 {SigLabel(a)}（实际选中 {GameRoot.HomeValley.SelectedMachineLogicId}）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(169, $"按接入键（{GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView)}，可重绑）");
        }

        private static void StepSigUplinkEntered(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int a = SessionState.GetInt(K + "SigA", 0);
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == a && GameRoot.HomeValley.PossessedMachineLogicId == a
                  && WorldView.Director.Mode == View.ViewMode.Direct && hud != null && hud.LocationText.Contains(SigLabel(a))
                  && !string.IsNullOrEmpty(hud.UplinkStatusText),
                $"0.35 秒过渡后接入 {SigLabel(a)}：镜头直控；HUD“{hud?.LocationText}”，状态行“{hud?.UplinkStatusText}”");
            CheckNoTextMarkers("接入后");
            // FG1-VFX-01：机身形变只读编译结果——表现层的状态与战斗桥接层这台机器的 Morph 一致（这台的蓝图有没有接入口都成立）。
            GameRoot.HomeValley.Combat.TryGetMachineWeapon(a, out Campaign.Combat.MachineWeaponInfo morphInfo);
            Check(View.MachineMorphView.IsRegistered(a) && View.MachineMorphView.TargetOf(a) == morphInfo.Morph && View.MachineMorphView.VisibleOf(a) == morphInfo.Morph,
                $"机身形变与编译结果一致：{Campaign.Blueprint.MachineMorph.Describe(morphInfo.Morph)}");
            // FG1-SIG-05（FGR-SIG-090，真实游玩中）：过载是核心固件；信号核带着它，但没被接入的家园机器都由 AI 驾驶——接入口按空槽、武器没有具名反应。
            Campaign.Combat.CombatSite homeCombat = GameRoot.HomeValley.Combat;
            int aiMachines = 0;
            bool aiClean = true;
            foreach (int id in homeCombat.MachineLogicIds)
            {
                if (id == a || !homeCombat.TryGetMachineWeapon(id, out Campaign.Combat.MachineWeaponInfo info) || info.WeaponIndex < 0)
                {
                    continue;
                }
                aiMachines++;
                aiClean &= !info.Uplinked && !info.ReactionGated && homeCombat.Kernel.TryGetWeapon(info.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon w)
                           && w.Reaction == BinGames.Sim.Combat.CombatReaction.None;
            }
            Check(Campaign.Signal.FirmwareKinds.IsCore(Campaign.Content.FirmwareCatalog.FwOverloadId) && aiMachines > 0 && aiClean,
                $"AI 边界：过载是核心固件；另外 {aiMachines} 台家园机器由 AI 驾驶，接入口是空槽、武器没有具名反应（AI 永远不用核心固件）");
            Campaign.Regions.HomeValleyMachineMarker b = SigCandidate(a);
            if (b == null)
            {
                Write("  - 家园只有一台可接入的机器：跳过机器列表与 Tab 切换");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
                Next(172, "按接入 / 退出键离开");
                return;
            }
            SessionState.SetInt(K + "SigB", b.LogicId);
            Check(ClickMachineList(b.LogicId), $"命令栏机器列表里点 {SigLabel(b.LogicId)}");
            Next(170, $"机器列表直接接入 {SigLabel(b.LogicId)}");
        }

        private static void StepSigUplinkListSwitched(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int a = SessionState.GetInt(K + "SigA", 0);
            int b = SessionState.GetInt(K + "SigB", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == b && GameRoot.HomeValley.PossessedMachineLogicId == b,
                $"机器列表点一下：信号从 {SigLabel(a)} 切到 {SigLabel(b)}");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.CycleControlTarget));
            Next(171, "接入中按 Tab 循环切换");
        }

        private static void StepSigUplinkTabbed(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int b = SessionState.GetInt(K + "SigB", 0);
            int now = Campaign.Signal.SignalPresence.CurrentMachineLogicId;
            Check(now != 0 && now != b && GameRoot.HomeValley.PossessedMachineLogicId == now, $"Tab：信号从 {SigLabel(b)} 切到下一台 {SigLabel(now)}");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(172, "按接入 / 退出键离开");
        }

        private static void StepSigUplinkLeft(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            Check(Campaign.Signal.SignalPresence.AtCore && GameRoot.HomeValley.PossessedMachineLogicId == null && WorldView.Director.Mode == View.ViewMode.Strategy
                  && hud != null && hud.LocationText == Localization.GameText.Get("signal.hud.at_core"),
                $"离开：镜头回到战略，信号回到归还核心（HUD“{hud?.LocationText}”，状态行“{hud?.UplinkStatusText}”）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            Next(173, "按空格战略暂停");
        }

        private static void StepSigUplinkPaused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameClock.Paused, "战略暂停中");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(174, "战略暂停中按接入键（选中保留在刚离开的机器上）");
        }

        private static void StepSigUplinkPausedPending(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string status = hud?.UplinkStatusText ?? string.Empty;
            Check(Campaign.Signal.SignalUplinkService.PendingWaitsForResume && Campaign.Signal.SignalPresence.AtCore
                  && WorldView.Director.Mode == View.ViewMode.Strategy && status.Contains(SigLabel(Campaign.Signal.SignalUplinkService.PendingTargetLogicId)),
                $"暂停中发起：目标已确认、镜头不动、1 秒后仍未插入；HUD“{status}”");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(175, "按 Esc 取消这次接入");
        }

        private static void StepSigUplinkEscCancelled(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!Campaign.Signal.SignalUplinkService.IsPending && !PauseMenuUIToolkit.IsOpen && Campaign.Signal.SignalPresence.AtCore,
                $"Esc 先取消接入（“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”），没有弹出暂停菜单");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.TogglePause));
            Next(176, "按空格恢复运行");
        }

        private static void StepSigUplinkResumed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!GameClock.Paused && !Campaign.Signal.SignalUplinkService.IsPending && Campaign.Signal.SignalPresence.AtCore,
                "恢复运行：已取消的接入不会再自己完成");
            Next(211, "FG1-VFX-01：机身形变（接入 → 形变出现 → 离开 → 复原）");
        }

        // ── FG1-VFX-01：机身形变（装配站换上带接入口的重炮蓝图 → 真实鼠标选中 + 接入键 → 形变态出现 → 接入 / 退出键离开 → 复原）──────

        private static void StepMorphPrepare(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState st = CampaignSession.Current;
            Campaign.Regions.HomeValleyMachineMarker m = SigCandidate(0);
            if (m == null || !MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord rec))
            {
                Write("  - 家园里没有可以接入的机器：跳过 FG1-VFX-01 段（机身形变由 FgMachineMorphSelfCheck 覆盖）");
                Next(185, "FG1-SIG-04：断链与安全模式（静默夜预留接口 + 走出信号覆盖）");
                return;
            }
            // 测试捷径（代替玩家在蓝图编辑器保存带接入口的重炮蓝图、再到装配站回厂换装）：同一个装配登记入口。
            var board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(rec.ChassisId, Campaign.Content.ComponentCatalog.CompCannonId, null, null, System.Array.Empty<string>());
            bool uplinkOk = board.TrySetUplink(2).Success;
            const string bpId = "bp_smoke_vfx01_cannon_up";
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            st.BlueprintRecords = (st.BlueprintRecords ?? System.Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != bpId)
                .Append(new BlueprintRecord { BlueprintId = bpId, DisplayName = bpId, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            SessionState.SetString(K + "MorphOrigBp", rec.BlueprintId ?? string.Empty);
            SessionState.SetInt(K + "MorphOrigVer", rec.BlueprintVersion);
            bool registered = Campaign.Blueprint.MachineLoadoutRegistry.Register(st, m.LogicId, bpId, 1).Success;
            string core0 = Campaign.Signal.SignalCoreService.SlotContentId(st, 0);
            Check(uplinkOk && registered && View.MachineMorphView.IsRegistered(m.LogicId) && View.MachineMorphView.VisibleOf(m.LogicId) == Campaign.Blueprint.MorphMask.None,
                $"{SigLabel(m.LogicId)} 换上带接入口的重炮蓝图：AI 驾驶时接入口是空槽，机身不变形（信号核 1 号槽：{core0}）");
            SessionState.SetInt(K + "MorphM", m.LogicId);
            ClickWorld(m.View.transform.position);
            Next(212, $"左键点 {SigLabel(m.LogicId)}");
        }

        private static void StepMorphPress(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            Check(GameRoot.HomeValley.SelectedMachineLogicId == m, $"左键选中了 {SigLabel(m)}");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(213, "按接入键");
        }

        private static void StepMorphEntered(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            Campaign.Combat.CombatSite site = GameRoot.HomeValley.Combat;
            site.TryGetMachineWeapon(m, out Campaign.Combat.MachineWeaponInfo info);
            Campaign.Blueprint.MorphMask want = info.Morph;
            Transform grp = View.MachineMorphView.GroupOf(m, Campaign.Blueprint.MorphMask.Limiter);
            int renderers = grp != null ? grp.GetComponentsInChildren<MeshRenderer>(false).Count(r => r.enabled && r.sharedMaterial != null && r.sharedMaterial.enableInstancing) : 0;
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m && info.Uplinked && (want & Campaign.Blueprint.MorphMask.Limiter) != 0
                  && View.MachineMorphView.VisibleOf(m) == want && Mathf.Approximately(View.MachineMorphView.ProgressOf(m, Campaign.Blueprint.MorphMask.Limiter), 1f)
                  && grp != null && grp.gameObject.activeInHierarchy && renderers > 0 && View.MachineMorphView.SignalBeamShownOf(m),
                $"接入 {SigLabel(m)}：接入口插入过载 → 机身形变（{Campaign.Blueprint.MachineMorph.Describe(want)}），0.3 秒过渡已走完，{renderers} 个形变部件可见（共享材质、GPU Instancing），接入口射出信号光柱");
            Next(215, "FG1-HUD-01：读接入 HUD");
        }

        // ── FG1-HUD-01：接入 HUD（机体名、槽位、机身状态与来源、热量 / 电池 / 耐久 / 链路 / 暴露 / 经历）、“?”图鉴、机器列表“◇口”标记 ──────

        private static void StepHudShown(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            UI.SignalCore.UplinkHudView v = UI.SignalCore.SignalCoreHudUIToolkit.Instance?.UplinkHud;
            string ov = Campaign.Signal.FirmwareKinds.DisplayName(Campaign.Content.FirmwareCatalog.FwOverloadId) ?? "?";
            string all = v == null ? string.Empty : string.Join("｜", v.TitleText, v.MorphText, v.SlotText(0), v.HeatText, v.BatteryText, v.HealthText, v.LinkText, v.ExposureText, v.ExperienceText);
            Check(v != null && v.Visible && v.TitleText.Contains(SigLabel(m)) && v.SlotText(0).Contains(ov) && v.SlotText(0).Contains(Localization.GameText.Get("uplink.hud.state.active"))
                  && v.MorphText.Contains(Localization.GameText.Get("morph.state.limiter")) && v.MorphText.Contains(ov)
                  && v.HeatText.Length > 0 && v.BatteryText.Length > 0 && v.HealthText.Length > 0 && v.LinkText.Length > 0 && v.ExposureText.Length > 0
                  && v.ExperienceText.Length > 0 && !Localization.GameText.ContainsMarker(all),
                $"接入 HUD：{all}");
            GameObject barHost = GameObject.Find("[RegionCommandBarHost]");
            var bar = barHost != null ? barHost.GetComponent<UI.RegionCommand.RegionCommandBarUIToolkit>() : null;
            Check(bar != null && bar.CandidateShowsPort(m), $"机器列表：{SigLabel(m)} 带“◇口”（带接入口）标记");
            // FG1-HUD-01 修复轮（审查 P1）：接入 HUD 叠在接入视角的战场上方，文字 / 背景不能吞掉直控开火的点击，只有“?”挡住。
            UnityEngine.UIElements.IPanel hudPanel = v?.HeatElement?.panel;
            bool heatPasses = hudPanel != null && !UI.Common.UiWindowFocus.BlocksWorldPointerAt(hudPanel, v.HeatElement.worldBound.center)
                              && !UI.Common.UiWindowFocus.BlocksWorldPointerAt(hudPanel, v.LinkElement.worldBound.center)
                              && !UI.Common.UiWindowFocus.BlocksWorldPointerAt(hudPanel, v.SlotLabel(0).worldBound.center);
            bool helpBlocks = hudPanel != null && UI.Common.UiWindowFocus.BlocksWorldPointerAt(hudPanel, v.HelpButton.worldBound.center);
            Check(heatPasses && helpBlocks, $"接入 HUD 不吞世界点击：热量 / 链路 / 槽位文字处直控点击照常（{heatPasses}），“?”按钮处让位给按钮（{helpBlocks}）");
            Check(ClickUitk("[SignalCoreHost]", "UplinkHudHelp"), "点接入 HUD 的“?”");
            Next(216, "点接入 HUD 的“?”");
        }

        private static void StepHudCodexOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.Kit.MechanicCodexPanelUIToolkit codex = UI.Kit.MechanicCodexPanelUIToolkit.Instance;
            Check(UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && codex != null && codex.SelectedId == "codex.signal.uplink"
                  && codex.EntryTitleText == Localization.GameText.Get("codex.signal.uplink.title") && codex.RelatedCount > 0,
                $"图鉴打开到“{codex?.EntryTitleText}”，{codex?.RelatedCount} 个相关条目");
            // 修复轮（FG00 B02）：正文 / 脚注里的按键是当前绑定，不留 {act:} 占位。
            Check(codex != null && !codex.EntryBodyText.Contains(InputDisplay.ActionTokenPrefix) && !codex.FooterText.Contains(InputDisplay.ActionTokenPrefix)
                  && codex.EntryBodyText.Contains(InputDisplay.ForAction(GameActionId.CycleControlTarget)) && codex.FooterText.Contains(InputDisplay.ForAction(GameActionId.Cancel)),
                $"图鉴正文与脚注的按键随绑定：“{codex?.FooterText}”");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(217, "按 Esc 关闭图鉴");
        }

        private static void StepHudCodexClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            Check(!UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen && Campaign.Signal.SignalPresence.CurrentMachineLogicId == m,
                "Esc 先关闭图鉴（不弹暂停菜单），信号仍在机器里");
            SessionState.SetInt(K + "HudLeave0", Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.UplinkLeave));
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(214, "按接入 / 退出键离开");
        }

        private static void StepMorphLeft(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy
                  && View.MachineMorphView.VisibleOf(m) == Campaign.Blueprint.MorphMask.None && View.MachineMorphView.AnimatingCount == 0
                  && !View.MachineMorphView.SignalBeamShownOf(m),
                $"离开：镜头回到战略，{SigLabel(m)} 的形变收起复原、信号光柱熄灭");
            // FG1-HUD-01：接入 HUD 隐藏、离开音效钩子、机器详情写“与信号同行 N 次”（这台仍是选中的机器）。
            UI.SignalCore.UplinkHudView hudView = UI.SignalCore.SignalCoreHudUIToolkit.Instance?.UplinkHud;
            GameObject woHost = GameObject.Find("[WorkOrderHudHost]");
            string detail = woHost != null ? woHost.GetComponent<UIDocument>()?.rootVisualElement?.Q<Label>("MachineDetailLabel")?.text ?? string.Empty : string.Empty;
            MachineRegistry.TryGetRecord(m, out MachineRecord hudRec);
            Check(hudView != null && !hudView.Visible
                  && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.UplinkLeave) == SessionState.GetInt(K + "HudLeave0", 0) + 1
                  && hudRec != null && hudRec.SignalUplinkCount >= 1 && detail.Contains(Campaign.MachineSignalExperience.Describe(CampaignSession.Current, hudRec).Split('，')[0]),
                $"离开后接入 HUD 隐藏、离开音效钩子响一次；机器详情：“{detail.Replace("\n", " / ")}”");
            // FG2-VFX-02：同一台机器换上新作战组件（旋刃环 + 尖刺外装，电路自带拖尾）——真实场景里按 0.3 秒展开喷口态，两个新组件的部件都建出来、可见。
            // 测试捷径同 211 步（代替蓝图编辑器保存 + 装配站回厂），装配登记走同一入口。
            CampaignState st = CampaignSession.Current;
            var board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(hudRec?.ChassisId ?? Campaign.Regions.HomeValleyLayout.Erc003ChassisId, Campaign.Content.ComponentCatalog.CompOrbitId,
                Campaign.Content.ComponentCatalog.FuncSpikesId, null, new[] { Campaign.Content.FirmwareCatalog.FwTrailId });
            const string bpRoster = "bp_smoke_vfx02_orbit_spikes";
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            st.BlueprintRecords = (st.BlueprintRecords ?? System.Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != bpRoster)
                .Append(new BlueprintRecord { BlueprintId = bpRoster, DisplayName = bpRoster, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            bool reg = Campaign.Blueprint.MachineLoadoutRegistry.Register(st, m, bpRoster, 1).Success;
            Check(reg && board.FirmwareSlots.Contains(Campaign.Content.FirmwareCatalog.FwTrailId), $"{SigLabel(m)} 换上旋刃环 + 尖刺外装（电路自带拖尾）的蓝图");
            Next(231, "FG2-VFX-02：等新组件的机身状态展开");
        }

        private static void StepRosterMorphShown(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            Campaign.Combat.CombatSite site = GameRoot.HomeValley.Combat;
            site.TryGetMachineWeapon(m, out Campaign.Combat.MachineWeaponInfo info);
            site.Kernel.TryGetWeapon(info.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon w);
            Transform grp = View.MachineMorphView.GroupOf(m, Campaign.Blueprint.MorphMask.Fluid);
            bool orbitParts = grp != null && grp.Cast<Transform>().Any(t => t.name.StartsWith(Campaign.Content.ComponentCatalog.CompOrbitId + ".", StringComparison.Ordinal) && t.gameObject.activeInHierarchy);
            bool spikeParts = grp != null && grp.Cast<Transform>().Any(t => t.name.StartsWith(Campaign.Content.ComponentCatalog.FuncSpikesId + ".", StringComparison.Ordinal) && t.gameObject.activeInHierarchy);
            int renderers = grp != null ? grp.GetComponentsInChildren<MeshRenderer>(false).Count(r => r.enabled && r.sharedMaterial != null && r.sharedMaterial.enableInstancing) : 0;
            Check(info.Morph == Campaign.Blueprint.MorphMask.Fluid && View.MachineMorphView.VisibleOf(m) == Campaign.Blueprint.MorphMask.Fluid
                  && Mathf.Approximately(View.MachineMorphView.ProgressOf(m, Campaign.Blueprint.MorphMask.Fluid), 1f) && orbitParts && spikeParts && renderers > 0
                  && w.Reading.Carrier == BinGames.Sim.Combat.CombatCarrier.Melee && w.Reading.Cone >= 180f && w.Reading.Thorns > 0f,
                $"{SigLabel(m)}：旋刃环 + 尖刺外装 + 拖尾 → 喷口态展开（{renderers} 个部件可见，两个新组件各有部件）；内核武器 = 一整圈格斗 + 反伤");
            // FG2-VFX-02 修复轮（审查 P0）：其余 3 个新组件（哨戒桩 / 震荡脉冲器 / 拆解钳）也在真实场景里逐个换装、展开喷口态、核对部件与内核武器。
            SessionState.SetInt(K + "RosterIdx", 0);
            RegisterRosterMorph(m, RosterMorphRest[0]);
            Next(232, "FG2-VFX-02：换上哨戒桩，等机身状态重建");
        }

        /// <summary>231 步之后逐个换装的新组件（旋刃环 / 尖刺外装已在 231 步核对）。</summary>
        private static readonly string[] RosterMorphRest =
        {
            Campaign.Content.ComponentCatalog.CompSentryId,
            Campaign.Content.ComponentCatalog.CompPulserId,
            Campaign.Content.ComponentCatalog.CompClawId,
        };

        /// <summary>测试捷径同 211 / 230 步（代替蓝图编辑器保存 + 装配站回厂）：同一台机器换上“新组件 + 电路自带拖尾”的蓝图，装配登记走同一入口。</summary>
        private static void RegisterRosterMorph(int m, string comp)
        {
            CampaignState st = CampaignSession.Current;
            MachineRegistry.TryGetRecord(m, out MachineRecord rec);
            var board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(rec?.ChassisId ?? Campaign.Regions.HomeValleyLayout.Erc003ChassisId, comp,
                null, null, new[] { Campaign.Content.FirmwareCatalog.FwTrailId });
            string bp = "bp_smoke_vfx02_" + comp;
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            st.BlueprintRecords = (st.BlueprintRecords ?? System.Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != bp)
                .Append(new BlueprintRecord { BlueprintId = bp, DisplayName = bp, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            bool reg = Campaign.Blueprint.MachineLoadoutRegistry.Register(st, m, bp, 1).Success;
            Check(reg && board.FirmwareSlots.Contains(Campaign.Content.FirmwareCatalog.FwTrailId), $"{SigLabel(m)} 换上 {comp} + 拖尾的蓝图");
        }

        private static void StepRosterMorphNext(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            int m = SessionState.GetInt(K + "MorphM", 0);
            int idx = SessionState.GetInt(K + "RosterIdx", 0);
            string comp = RosterMorphRest[Mathf.Clamp(idx, 0, RosterMorphRest.Length - 1)];
            Campaign.Combat.CombatSite site = GameRoot.HomeValley.Combat;
            site.TryGetMachineWeapon(m, out Campaign.Combat.MachineWeaponInfo info);
            site.Kernel.TryGetWeapon(info.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon w);
            Transform grp = View.MachineMorphView.GroupOf(m, Campaign.Blueprint.MorphMask.Fluid);
            int compParts = grp != null ? grp.Cast<Transform>().Count(t => t.name.StartsWith(comp + ".", StringComparison.Ordinal) && t.gameObject.activeInHierarchy) : 0;
            bool staleParts = grp != null && grp.Cast<Transform>().Any(t => t.gameObject.activeInHierarchy
                && (t.name.StartsWith(Campaign.Content.ComponentCatalog.CompOrbitId + ".", StringComparison.Ordinal)
                    || RosterMorphRest.Any(o => o != comp && t.name.StartsWith(o + ".", StringComparison.Ordinal))));
            int renderers = grp != null ? grp.GetComponentsInChildren<MeshRenderer>(false).Count(r => r.enabled && r.sharedMaterial != null && r.sharedMaterial.enableInstancing) : 0;
            BinGames.Sim.Combat.CombatReading rd = w.Reading;
            bool kernelOk = comp == Campaign.Content.ComponentCatalog.CompSentryId
                ? rd.Carrier == BinGames.Sim.Combat.CombatCarrier.Summon && rd.DroneAnchored != 0
                : comp == Campaign.Content.ComponentCatalog.CompPulserId
                    ? rd.Carrier == BinGames.Sim.Combat.CombatCarrier.Field && rd.FieldPlacement == BinGames.Sim.Combat.CombatZonePlacement.Attacker
                    : rd.Carrier == BinGames.Sim.Combat.CombatCarrier.Melee && rd.EchoCount > 0;
            Check(info.Morph == Campaign.Blueprint.MorphMask.Fluid && View.MachineMorphView.VisibleOf(m) == Campaign.Blueprint.MorphMask.Fluid
                  && Mathf.Approximately(View.MachineMorphView.ProgressOf(m, Campaign.Blueprint.MorphMask.Fluid), 1f) && compParts > 0 && !staleParts && renderers > 0 && kernelOk,
                $"{SigLabel(m)}：{comp} + 拖尾 → 喷口态重建展开（{comp} 部件 {compParts} 个、共 {renderers} 个部件可见，没有上一个组件的残留部件）；内核武器载体 = {rd.Carrier}");
            if (idx + 1 < RosterMorphRest.Length)
            {
                SessionState.SetInt(K + "RosterIdx", idx + 1);
                RegisterRosterMorph(m, RosterMorphRest[idx + 1]);
                Next(232, $"FG2-VFX-02：换上 {RosterMorphRest[idx + 1]}，等机身状态重建");
                return;
            }
            string orig = SessionState.GetString(K + "MorphOrigBp", string.Empty);
            if (orig.Length > 0)
            {
                Campaign.Blueprint.MachineLoadoutRegistry.Register(CampaignSession.Current, m, orig, SessionState.GetInt(K + "MorphOrigVer", 1));
            }
            // FG1-E2E-01：这台机器仍是选中的——按“跟随选中对象”（默认 F），接着按“回到归还核心”要停止跟随、镜头停在核心。
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.FollowSelection));
            Next(218, "FG1-E2E-01：按“跟随选中对象”键（默认 F）跟随这台机器");
        }

        private static void StepFollowStarted(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            Check(WorldView.Director.IsFollowing, $"按 F：镜头跟随选中的机器（“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”）");
            SessionState.SetInt(K + "FollowStops0", WorldView.Director.FollowStopCount);
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore));
            Next(219, "跟随中按“回到归还核心”键（默认 Home）");
        }

        private static void StepFollowStoppedByHome(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Vector2 home = GameRoot.HomeValley != null ? GameRoot.HomeValley.DefaultFocus : Vector2.zero;
            float d = Vector2.Distance(CameraFocus(), home);
            Check(!WorldView.Director.IsFollowing && WorldView.Director.FollowStopCount == SessionState.GetInt(K + "FollowStops0", 0) + 1 && d < 1.5f,
                $"FG1-E2E-01（FGJ-M1 发现）：跟随中按“回到归还核心”——停止跟随，镜头停在核心（离核心 {d:F2} 格），没有被拽回机器");
            Next(185, "FG1-SIG-04：断链与安全模式（静默夜预留接口 + 走出信号覆盖）");
        }

        private static void ContinueToExpeditionPrep()
        {
            UnlockLikeDeparture(Campaign.Regions.FracturedCityRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            GameRoot.HomeValley.SetExpeditionPrepPanelOpen(true);
            Next(158, "测试捷径：标记破碎都市可出征，打开远征准备面板（点信号塔的同一开关）");
        }

        // ── FG1-SIG-04：断链与安全模式（真实鼠标 / 按键接入；静默夜只有预留接口、机器走到覆盖边缘用测试捷径瞬移）──────────

        private static Campaign.Regions.HomeValleyMachineMarker LinkMarker()
        {
            int id = SessionState.GetInt(K + "LinkM", 0);
            return id != 0 && GameRoot.HomeValley?.Combat != null && GameRoot.HomeValley.Combat.TryGetMachineMarker(id, out Campaign.Regions.HomeValleyMachineMarker m) ? m : null;
        }

        private static UnityEngine.UIElements.Button MachineListButton(int logicId)
        {
            GameObject host = GameObject.Find("[RegionCommandBarHost]");
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            ScrollView strip = doc?.rootVisualElement?.Q<ScrollView>("ControlCandidateStrip");
            return strip != null && MachineRegistry.TryGetRecord(logicId, out MachineRecord rec)
                ? strip.Query<UnityEngine.UIElements.Button>().ToList().FirstOrDefault(x => x.text == "#" + rec.DisplayNumber) : null;
        }

        /// <summary>测试捷径：把机器瞬移到离归还核心 <paramref name="distance"/> 格处（沿核心 → 机器的方向），代替玩家开着它走一两百格。</summary>
        private static void TeleportFromCore(int logicId, float distance)
        {
            Campaign.Combat.CombatSite site = GameRoot.HomeValley?.Combat;
            Vector2 core = Campaign.Signal.SignalCoverageService.Sample(Campaign.Regions.HomeValleyLayout.RegionId, Vector2.zero).SourceCenter;
            if (site == null || !site.TryGetMachineUnit(logicId, out int unit) || !site.TryGetMachinePosition(logicId, out Vector2 at))
            {
                return;
            }
            Vector2 dir = (at - core).sqrMagnitude > 1e-4f ? (at - core).normalized : Vector2.right;
            Vector2 p = core + dir * distance;
            site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(p.x, p.y));
        }

        private static void StepLinkSelect(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyMachineMarker m = SigCandidate(0);
            if (m == null)
            {
                Write("  - 家园里没有可以接入的机器：跳过 FG1-SIG-04 段（断链与安全模式由 FgSignalLinkSelfCheck 覆盖）");
                ContinueToExpeditionPrep();
                return;
            }
            SessionState.SetInt(K + "LinkM", m.LogicId);
            ClickWorld(m.View.transform.position);
            Next(186, $"左键点家园里的机器 {SigLabel(m.LogicId)}");
        }

        private static void StepLinkPress(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(187, "按接入键");
        }

        private static void StepLinkEntered(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m, $"接入 {SigLabel(m)}");
            // 测试捷径：静默夜（FG07）还没有，走 FG1-SIG-04 预留的判定入口与“静默夜开始”事件入口。
            Campaign.Signal.SignalUplinkService.SilentNightProvider = () => true;
            Campaign.Signal.SignalLinkService.AnnounceSilentNight(0f);
            bool broke = Campaign.Signal.SignalLinkService.OnSilentNightStarted();
            Check(broke, "静默夜开始（预留接口）：接入中的信号被强制弹回");
            Next(188, "测试捷径：静默夜开始");
        }

        private static void StepLinkSilentBroken(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            UnityEngine.UIElements.Button listBtn = MachineListButton(m);
            // 机器列表那一行（编号按钮 + 按钮外侧的安全模式标记）挂着运行时悬停提示。
            VisualElement listItem = listBtn?.parent;
            if (!SessionState.GetBool(K + "LinkHover", false))
            {
                UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
                string status = hud?.UplinkStatusText ?? string.Empty;
                View.WorldBadge badge = View.SignalLinkView.BadgeFor(m);
                Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy
                      && Campaign.Signal.SignalLinkService.LastBreakReason == Campaign.Signal.SignalLinkBreakReason.SilentNight
                      && status.Contains(SigLabel(m)),
                    $"静默夜断链：信号回到归还核心、镜头回战略；HUD“{status}”");
                Check(Campaign.Signal.SignalLinkService.IsInSafeMode(CampaignSession.Current, m) && badge != null && badge.IsShowing
                      && badge.IconId == Campaign.Signal.SignalLinkService.SafeModeIconId,
                    $"{SigLabel(m)} 进入安全模式：头顶显示安全模式图标（贴图已加载：{badge?.IsShowing}）");
                VisualElement tag = listItem?.Q<VisualElement>(className: "cmd-candidate-safe-tag");
                Check(listBtn != null && listBtn.ClassListContains("cmd-candidate-btn-safe") && tag != null && tag.resolvedStyle.display == DisplayStyle.Flex
                      && tag.worldBound.xMin >= listBtn.worldBound.xMax - 0.5f,
                    $"命令栏机器列表的 {listBtn?.text} 带安全模式标记（在编号按钮外侧，按钮 [{listBtn?.worldBound.xMin:F0}～{listBtn?.worldBound.xMax:F0}]、标记 [{tag?.worldBound.xMin:F0}～{tag?.worldBound.xMax:F0}]）");
                // 鼠标移到那一行上：派发指针进入事件，走 UiTooltip 自己注册的回调（与 ClickUitk 走按钮自己的 Clickable 同一层级）。
                if (listItem != null)
                {
                    using (PointerEnterEvent enter = PointerEnterEvent.GetPooled())
                    {
                        enter.target = listItem;
                        listItem.SendEvent(enter);
                    }
                }
                SessionState.SetBool(K + "LinkHover", true);
                return;
            }
            // 悬停提示约 0.4 真实秒后出现（UiKitOverlay 每帧 Tick）。
            if (inStep < 2.5)
            {
                return;
            }
            SessionState.SetBool(K + "LinkHover", false);
            TooltipContent tip = UiTooltip.Content;
            string tipText = tip != null ? (tip.Title ?? string.Empty) + " / " + (tip.Body ?? string.Empty).Replace("\n", " / ") : string.Empty;
            string reason = Campaign.Signal.SignalLinkService.ReasonName(Campaign.Signal.SignalLinkBreakReason.SilentNight);
            Check(UiTooltip.IsVisible && UiTooltip.Target == listItem && tip != null && tip.Title == SigLabel(m) && tipText.Contains(reason)
                  && tip.Body.StartsWith(Campaign.Signal.SignalLinkService.SafeModeTooltip(CampaignSession.Current, m)),
                $"鼠标悬停在 {listBtn?.text} 那一行：提示面板写明原因与恢复条件“{tipText}”");
            if (listItem != null)
            {
                using (PointerLeaveEvent leave = PointerLeaveEvent.GetPooled())
                {
                    leave.target = listItem;
                    listItem.SendEvent(leave);
                }
            }
            UiTooltip.Hide();
            CheckNoTextMarkers("断链后");
            Campaign.Regions.HomeValleyMachineMarker mk = LinkMarker();
            if (mk?.View != null)
            {
                ClickWorld(mk.View.transform.position);
            }
            Next(189, $"左键再点 {SigLabel(m)}");
        }

        private static void StepLinkSilentPress(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(190, "静默夜中按接入键");
        }

        private static void StepLinkSilentRejected(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(Campaign.Signal.SignalPresence.AtCore && Campaign.Signal.SignalUplinkService.LastFailure == Campaign.Signal.UplinkFailure.SilentNight,
                $"静默夜中接入被拒：“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”");
            Campaign.Signal.SignalUplinkService.SilentNightProvider = null;
            Next(191, "测试捷径：静默夜结束");
        }

        private static void StepLinkSafeExited(double inStep)
        {
            if (inStep < 3.2)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            View.WorldBadge badge = View.SignalLinkView.BadgeFor(m);
            UnityEngine.UIElements.Button listBtn = MachineListButton(m);
            Check(!Campaign.Signal.SignalLinkService.IsInSafeMode(CampaignSession.Current, m) && (badge == null || !badge.IsShowing)
                  && listBtn != null && !listBtn.ClassListContains("cmd-candidate-btn-safe"),
                $"静默夜结束 2 秒后 {SigLabel(m)} 退出安全模式：头顶图标与列表标记消失");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(192, "再按接入键");
        }

        private static void StepLinkReentered(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m, $"重新接入 {SigLabel(m)}");
            TeleportFromCore(m, Campaign.Signal.SignalCoverageService.CoreRadius - 8f);
            Next(193, "测试捷径：把接入的机器挪到离覆盖边缘 8 格处");
        }

        private static void StepLinkEdgeWarned(double inStep)
        {
            if (inStep < 3.5)
            {
                return; // 等“已接入”那条 3 秒反馈过去，HUD 状态行才轮到常驻的边缘预警。
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string status = hud?.UplinkStatusText ?? string.Empty;
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m && View.SignalLinkView.RingVisible && !View.SignalLinkView.RingDanger
                  && Mathf.Abs(View.SignalLinkView.RingRadius - Campaign.Signal.SignalCoverageService.CoreRadius) < 0.01f
                  && status.Length > 0 && status == Campaign.Signal.SignalLinkService.WarningLine,
                $"接近覆盖边缘：地图上画出覆盖圈（半径 {View.SignalLinkView.RingRadius}），HUD“{status}”");
            TeleportFromCore(m, Campaign.Signal.SignalCoverageService.CoreRadius + 12f);
            Next(194, "测试捷径：把它挪到覆盖外 12 格");
        }

        private static void StepLinkCoverageBroken(double inStep)
        {
            if (inStep < 3.2)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy
                  && Campaign.Signal.SignalLinkService.LastBreakReason == Campaign.Signal.SignalLinkBreakReason.OutOfCoverage
                  && Campaign.Signal.SignalLinkService.IsInSafeMode(CampaignSession.Current, m),
                $"走出覆盖、宽限 2 秒耗尽：信号弹回归还核心，{SigLabel(m)} 进入安全模式（“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”）");
            TeleportFromCore(m, 8f);
            Next(195, "测试捷径：把它挪回核心附近");
        }

        private static void StepLinkCoverageRecovered(double inStep)
        {
            if (inStep < 3.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(!Campaign.Signal.SignalLinkService.IsInSafeMode(CampaignSession.Current, m) && !View.SignalLinkView.RingVisible,
                $"回到覆盖内 2 秒后 {SigLabel(m)} 退出安全模式；地图预警圈已收起");
            SessionState.SetInt(K + "NetHome0", Campaign.Signal.SignalUplinkService.JumpHomeCount);
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.ToggleOverlay));
            Next(198, "FG1-SIG-07：按叠加层键（O）打开覆盖网络叠加层");
        }

        // ── FG1-SIG-07：覆盖网络叠加层、跳回家园 / 上一台（快捷键与 HUD 按钮）、覆盖外的机器（列表标记、点它被拒）、跨地点远距离跳转 ──────────

        private static void StepNetOverlayOn(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string home = Campaign.Regions.HomeValleyLayout.RegionId;
            Check(View.SignalCoverageOverlayView.Enabled && View.SignalCoverageOverlayView.Visible
                  && View.SignalCoverageOverlayView.DrawnRings == Campaign.Signal.SignalCoverageService.SiteSourceCount(home)
                  && View.SignalCoverageOverlayView.DrawnRings >= 1 && hud != null && hud.JumpBarVisible
                  && hud.CoverageToggleText == Localization.GameText.Get("signal.overlay.button_on"),
                $"叠加层打开：画出 {View.SignalCoverageOverlayView.DrawnRings} 个覆盖圈（断开 {View.SignalCoverageOverlayView.DrawnCut} 个），HUD 跳转条按钮“{hud?.CoverageToggleText}”");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.ToggleOverlay));
            Next(199, "再按 O 关闭叠加层");
        }

        private static void StepNetOverlayOff(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(!View.SignalCoverageOverlayView.Enabled && !View.SignalCoverageOverlayView.Visible, "再按 O：叠加层关闭");
            Check(Campaign.Signal.SignalUplinkService.PreviousMachine(CampaignSession.Current) == m, $"“上一台机器”= 刚才接入过的 {SigLabel(m)}");
            SessionState.SetInt(K + "NetFar0", Campaign.Signal.SignalUplinkService.FarJumpCount);
            Check(ClickUitk("[SignalCoreHost]", "SignalJumpPrev"), "点 HUD 跳转条的“上一台”");
            Next(200, "点“上一台”：信号跳回上一台机器");
        }

        private static void StepNetPrevUplinked(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m && WorldView.Director.Mode == View.ViewMode.Direct
                  && Campaign.Signal.SignalUplinkService.FarJumpCount == SessionState.GetInt(K + "NetFar0", 0),
                $"“上一台”：信号回到 {SigLabel(m)}（离核心不到 500 格，近距离，没有冷却）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpHome));
            Next(201, "按“跳回家园”（H）");
        }

        private static void StepNetHomeDone(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy && GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive
                  && Campaign.Signal.SignalUplinkService.JumpHomeCount == SessionState.GetInt(K + "NetHome0", 0) + 1,
                $"按 H：信号回到归还核心、镜头回到家园（“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”）");
            int m = SessionState.GetInt(K + "LinkM", 0);
            Campaign.Regions.HomeValleyMachineMarker other = SigCandidate(m);
            if (other == null)
            {
                Write("  - 家园里没有第二台可以接入的机器：跳过“覆盖外的机器”一段（由 FgSignalNetworkSelfCheck D / L 段覆盖）");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpPreviousMachine));
                Next(205, "按“跳回上一台机器”（J）");
                return;
            }
            SessionState.SetInt(K + "NetOut", other.LogicId);
            SessionState.SetInt(K + "NetLeft0", Campaign.Signal.SignalCoverageService.LeftCount);
            TeleportFromCore(other.LogicId, 420f);
            Next(202, $"测试捷径：把 {SigLabel(other.LogicId)} 瞬移到离核心 420 格（覆盖外；代替开着它走出去）");
        }

        private static void StepNetOutsideTagged(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int n = SessionState.GetInt(K + "NetOut", 0);
            UnityEngine.UIElements.Button btn = MachineListButton(n);
            Label tag = btn?.parent?.Q<Label>(className: "cmd-candidate-nolink-tag");
            Check(Campaign.Signal.SignalCoverageService.IsMachineOutOfCoverage(n) && Campaign.Signal.SignalCoverageService.LeftCount > SessionState.GetInt(K + "NetLeft0", 0)
                  && Notifications.NotificationCenter.History.Any(e => e.Type?.Id == "signal_coverage_left"),
                $"{SigLabel(n)} 被标为覆盖外，发“走出覆盖”通知");
            Check(btn != null && btn.ClassListContains("cmd-candidate-btn-nolink") && tag != null && tag.resolvedStyle.display == DisplayStyle.Flex
                  && tag.text == Localization.GameText.Get("signal.coverage.list_tag"),
                $"命令栏机器列表的 {btn?.text} 带“{tag?.text}”标记（橙色边框 + 文字）");
            Check(btn != null && InvokeClickable(btn), $"点机器列表里的 {btn?.text}（它在覆盖外）");
            Next(203, "点覆盖外机器的列表按钮：接入被拒");
        }

        private static void StepNetOutsideRejected(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            int n = SessionState.GetInt(K + "NetOut", 0);
            Check(Campaign.Signal.SignalPresence.AtCore && Campaign.Signal.SignalUplinkService.LastFailure == Campaign.Signal.UplinkFailure.OutOfCoverage
                  && Campaign.Signal.SignalUplinkService.LastFeedbackText.Contains(Localization.GameText.Get("signal.coverage.kind.relay_tower").Substring(2)),
                $"覆盖外的机器不能接入：“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”");
            TeleportFromCore(n, 10f);
            Next(204, "测试捷径：把它挪回核心附近");
        }

        private static void StepNetOutsideRecovered(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int n = SessionState.GetInt(K + "NetOut", 0);
            UnityEngine.UIElements.Button btn = MachineListButton(n);
            Check(!Campaign.Signal.SignalCoverageService.IsMachineOutOfCoverage(n) && btn != null && !btn.ClassListContains("cmd-candidate-btn-nolink"),
                $"{SigLabel(n)} 回到覆盖：列表标记撤掉（控制自动恢复）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpPreviousMachine));
            Next(205, "按“跳回上一台机器”（J）");
        }

        private static void StepNetPrevAgain(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int m = SessionState.GetInt(K + "LinkM", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == m && WorldView.Director.Mode == View.ViewMode.Direct,
                $"按 J：信号跳回上一台 {SigLabel(m)}");
            CheckNoTextMarkers("覆盖网络与跳转");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(206, "按接入 / 退出键离开");
        }

        private static void StepNetLeft(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy, "离开：信号回到归还核心、镜头回战略");
            ContinueToExpeditionPrep();
        }

        // 远征途中（镜头在破碎都市、信号在归还核心 = 家园）：点远征机器 = 跨地点的远距离跳转；按 H 跳回家园（远距离，不受冷却限制）；
        // 回家后按 J 跳回远征队被冷却拒绝（FGJ-M1 第 7、8 步的前半，冷却后成功由 FgSignalNetworkSelfCheck F 段覆盖）；Tab 回到远征地点。
        private static void StepNetCrossJump(double inStep)
        {
            CampaignState st = CampaignSession.Current;
            if (Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st) > 0 || inStep < 0.5)
            {
                if (inStep > 20)
                {
                    Finish("20 秒内远距离跳转冷却没有结束");
                }
                return;
            }
            Campaign.Regions.FracturedCityController city = GameRoot.FracturedCity;
            Campaign.Regions.HomeValleyMachineMarker target = city != null && city.IsActive
                ? SigCandidateIn(city.Combat, Campaign.Regions.FracturedCityLayout.RegionId, 0) : null;
            if (target == null || !Campaign.Signal.SignalPresence.AtCore)
            {
                Write("  - 破碎都市此刻没有可以接入的机器（都在干扰场里等）：跳过跨地点跳转（由 FgSignalNetworkSelfCheck E / F 段覆盖）");
                Next(120, "FG0-ARCH-01：整个世界同时运行——远征进行中，家园没有退出");
                return;
            }
            SessionState.SetInt(K + "NetFar0", Campaign.Signal.SignalUplinkService.FarJumpCount);
            SessionState.SetInt(K + "NetCross", target.LogicId);
            Check(ClickMachineList(target.LogicId), $"镜头在破碎都市、信号在归还核心：点机器列表里的 {SigLabel(target.LogicId)}");
            Next(208, $"信号从归还核心（家园）跳到破碎都市的 {SigLabel(target.LogicId)}：跨地点 = 远距离跳转");
        }

        private static void StepNetCrossArrived(double inStep)
        {
            int t = SessionState.GetInt(K + "NetCross", 0);
            if (inStep < 0.6)
            {
                return;
            }
            if (!SessionState.GetBool(K + "NetMid", false))
            {
                SessionState.SetBool(K + "NetMid", true);
                UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
                string status = hud?.UplinkStatusText ?? string.Empty;
                Check(Campaign.Signal.SignalUplinkService.IsPending && Campaign.Signal.SignalUplinkService.PendingIsFar && Campaign.Signal.SignalPresence.AtCore
                      && status.Contains(Localization.GameText.Get("signal.jump.other_site")),
                    $"跳转过渡中（1.5 秒，世界照常运行）：信号还在核心，HUD“{status}”");
                return;
            }
            if (inStep < 3)
            {
                return;
            }
            SessionState.SetBool(K + "NetMid", false);
            CampaignState st = CampaignSession.Current;
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == t && GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive
                  && WorldView.Director.Mode == View.ViewMode.Direct && Campaign.Signal.SignalUplinkService.FarJumpCount == SessionState.GetInt(K + "NetFar0", 0) + 1
                  && Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st) > 0,
                $"到达：信号在 {SigLabel(t)} 里、镜头直控；远距离跳转开始冷却（{Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st):F1} 秒）");
            // FG1-E2E-01（FGJ-M1 发现）：出征时家园晚到的摘除不能清掉远征地点的表现登记——远征地点接入的机器照样有形变 / 安全模式图标的挂点。
            Check(View.MachineMorphView.IsRegistered(t),
                $"远征地点的 {SigLabel(t)} 形变表现已登记（出征时家园晚到的摘除没有把它清掉）");
            SessionState.SetInt(K + "NetHome1", Campaign.Signal.SignalUplinkService.JumpHomeCount);
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpHome));
            Next(209, "FGJ-M1 第 7 步：按 H 跳回家园（从远征地点回家 = 远距离，冷却中也能回家）");
        }

        private static void StepNetCrossHome(double inStep)
        {
            if (inStep < 3)
            {
                return;
            }
            if (!SessionState.GetBool(K + "NetHomeChecked", false))
            {
                SessionState.SetBool(K + "NetHomeChecked", true);
                Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy && GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive
                      && Campaign.Signal.SignalUplinkService.JumpHomeCount == SessionState.GetInt(K + "NetHome1", 0) + 1,
                    "按 H（1.5 秒过渡）：信号回到归还核心、镜头回到家园");
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.JumpPreviousMachine));
                return;
            }
            if (inStep < 3.6)
            {
                return;
            }
            SessionState.SetBool(K + "NetHomeChecked", false);
            Check(Campaign.Signal.SignalPresence.AtCore && Campaign.Signal.SignalUplinkService.LastFailure == Campaign.Signal.UplinkFailure.JumpCooldown,
                $"FGJ-M1 第 8 步（冷却中）：马上按 J 跳回远征队被拒——“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.CycleWorldFocus));
            Next(210, "按“切换关注点”（Tab）回到远征地点");
        }

        private static void StepNetBackToExpedition(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Check(GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive, "Tab：镜头回到破碎都市");
            Next(120, "FG0-ARCH-01：整个世界同时运行——远征进行中，家园没有退出");
        }

        // FG1-SIG-03（FG01 第 5 章“存档时玩家在机器里”）：暂停菜单存档前先接入一台机器，读档后信号必须还在那台里。
        private static void StepSigSaveSelect(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyMachineMarker m = SigCandidate(0)
                ?? (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive
                    ? SigCandidateIn(GameRoot.FracturedCity.Combat, Campaign.Regions.FracturedCityLayout.RegionId, 0) : null);
            if (m == null)
            {
                // 这条旅程走到这里时，机器都停在暂离的远征地点（地点未载入），世界里没有可接入的机器：如实记录并照常存档。
                // “存档时信号在机器里 → 读档后仍在那台机器里”由 FgSignalUplinkSelfCheck H 段用真实存档文件、按主菜单“继续”同一顺序覆盖。
                Write($"  - 家园与已载入的地点此刻没有可接入的机器（家园 {HomeMachines().Length} 台，其余在暂离的远征地点）：本次存档信号在归还核心");
                SessionState.SetInt(K + "SigSaved", 0);
                SessionState.SetString(K + "SigSavedSite", Campaign.Regions.HomeValleyLayout.RegionId);
                BeginPauseSave();
                return;
            }
            SessionState.SetInt(K + "SigSaved", m.LogicId);
            SessionState.SetString(K + "SigSavedSite", Campaign.Regions.HomeValleyLayout.RegionId);
            ClickWorld(m.View.transform.position);
            Next(178, $"存档前左键点 {SigLabel(m.LogicId)}");
        }

        private static void StepSigSavePress(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(179, "按接入键");
        }

        private static void StepSigSaveUplinked(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            int id = SessionState.GetInt(K + "SigSaved", 0);
            Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == id && CampaignSession.Current.SignalCore.UplinkMachineLogicId == id
                  && WorldView.Director.Mode == View.ViewMode.Direct,
                $"存档前信号在 {SigLabel(id)} 里（{SessionState.GetString(K + "SigSavedSite", string.Empty)}，镜头直控）");
            BeginPauseSave();
        }

        private static void StepSigLeftAfterLoad(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Check(Campaign.Signal.SignalPresence.AtCore && WorldView.Director.Mode == View.ViewMode.Strategy,
                "读档后按接入 / 退出键离开：信号回到归还核心、镜头回到战略");
            if (!(GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive))
            {
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.FocusHomeCore));
                Next(19, "按回家园键，镜头回到归还核心");
                return;
            }
            Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(CampaignSession.Current);
            Next(80, "测试捷径：核心被毁（HomeValleySoftlockGuard.DebugDestroyCore），等失败页出现");
        }

        private static void StepSigHomeAfterLoad(double inStep)
        {
            if (!(GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive))
            {
                if (inStep > 6)
                {
                    Finish("6 秒内镜头没回到归还谷地");
                }
                return;
            }
            if (inStep < 1)
            {
                return;
            }
            Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(CampaignSession.Current);
            Next(80, "测试捷径：核心被毁（HomeValleySoftlockGuard.DebugDestroyCore），等失败页出现");
        }

        private static void StepSignalPrepPanel(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            string summary = LabelText("[HomeValleyExpeditionPrepHost]", "SignalCoreSummaryLabel");
            string reminder = LabelText("[HomeValleyExpeditionPrepHost]", "SignalCoreReminderLabel");
            Check(summary.Contains(Localization.GameText.Get("firmware.fw_overload.name")) && reminder == Localization.GameText.Get("signal.core.expedition_reminder"),
                $"远征准备面板显示“{summary}”并提前提醒“{reminder}”");
            Check(ClickUitk("[HomeValleyExpeditionPrepHost]", "SignalCoreEditButton"), "点远征准备面板的“编辑信号核”");
            Next(159, "点“编辑信号核”");
        }

        private static void StepSignalFromPrep(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(UI.SignalCore.SignalCoreHudUIToolkit.IsOpen, "从远征准备面板打开了信号核面板");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(160, "按 Esc（先关最上层的信号核面板）");
        }

        private static void StepSignalPrepDone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && GameRoot.HomeValley.IsExpeditionPrepPanelOpen && !PauseMenuUIToolkit.IsOpen,
                "Esc 逐层返回：信号核面板关闭，远征准备面板还开着");
            GameRoot.HomeValley.SetExpeditionPrepPanelOpen(false);
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
            // FG0-ARCH-03：远征战斗在战斗内核里——编队攻击（与战略命令栏 / 热键同一个 IssueAttack 入口）打驻守的干扰机。
            Campaign.Regions.FracturedCityController city = GameRoot.FracturedCity;
            Campaign.Combat.CombatSite site = city?.Combat;
            int[] ids = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == Campaign.Regions.FracturedCityLayout.RegionId)
                .Select(m => m.LogicId).ToArray();
            RegionEnemyRecord jammer = CampaignSession.Current?.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == Campaign.Regions.FracturedCityLayout.JammerSpawnId);
            int views = Object.FindObjectsByType<Campaign.Regions.MachineView>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(v => v.Marker != null && v.Marker.Site == site);
            Check(site != null && site.MachineCount == ids.Length && views == ids.Length && GameObject.Find("[FracturedCityRoot]") != null,
                $"破碎都市战斗内核：{site?.MachineCount} 台机器、{site?.EnemyIds.Count()} 个敌人在内核里；被观察时每台机器都有表现对象（{views} 个）");
            // FG2-FW-02 测试捷径（等同回厂改造：正式保存入口存一版新蓝图 + 装配登记）：出征的搬运机换成“切割束 + 拖尾 + 寻的”，
            // 看固件读法在真实 Play 帧的地点内核里生效、画面画出区域。
            CampaignState st = CampaignSession.Current;
            int refit = ids.FirstOrDefault(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.ChassisId == Campaign.Regions.HomeValleyLayout.Erc002ChassisId);
            if (refit > 0 && MachineRegistry.TryGetRecord(refit, out MachineRecord refitRec))
            {
                Campaign.Blueprint.BlueprintCircuitBoard trailBoard = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(refitRec.ChassisId,
                    Campaign.Content.ComponentCatalog.CompBeamId, null, null, new[] { Campaign.Content.FirmwareCatalog.FwTrailId, Campaign.Content.FirmwareCatalog.FwHomingId });
                int scrap0 = st.Scrap;
                st.Scrap = Math.Max(st.Scrap, 500);
                Campaign.Blueprint.BlueprintSaveResult saved = Campaign.Blueprint.BlueprintEditorService.TrySave(st, trailBoard, "bp_smoke_fw02_trail", "smoke trail", saveAsNewRecord: true);
                st.Scrap = Math.Max(st.Scrap, scrap0);
                Campaign.Blueprint.CircuitOpResult reg = saved.Success
                    ? Campaign.Blueprint.MachineLoadoutRegistry.Register(st, refit, saved.BlueprintId, saved.Version)
                    : Campaign.Blueprint.CircuitOpResult.Fail("save_failed", saved.FailureReason);
                Check(saved.Success && reg.Success, $"测试捷径：搬运机回厂改造为“切割束 + 拖尾 + 寻的”（保存 {saved.Success}，登记 {reg.Success}{(reg.Success ? "" : "：" + reg.Message)}）");
            }
            SessionState.SetFloat(K + "JammerHp", jammer != null ? jammer.Health : -1f);
            SessionState.SetString(K + "RuinsTicks", GameClock.Ticks.ToString());
            city?.SquadCommands.DebugSelectMany(ids);
            city?.SquadCommands.IssueAttack(Campaign.Regions.FracturedCityLayout.JammerSpawnId, paused: false);
            Next(129, $"编队攻击：{ids.Length} 台机器攻击静默干扰机（耐久 {jammer?.Health:F0}）");
        }

        private static void StepRuinsCombat(double inStep)
        {
            if (inStep < 6)
            {
                return;
            }
            Campaign.Regions.FracturedCityController city = GameRoot.FracturedCity;
            Campaign.Combat.CombatSite site = city?.Combat;
            RegionEnemyRecord jammer = CampaignSession.Current?.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == Campaign.Regions.FracturedCityLayout.JammerSpawnId);
            float hp0 = SessionState.GetFloat(K + "JammerHp", -1f);
            long ticks0 = long.Parse(SessionState.GetString(K + "RuinsTicks", "0"));
            string recent = city == null ? string.Empty : string.Join(" / ", city.SquadCommands.RecentEvents.Skip(Math.Max(0, city.SquadCommands.RecentEvents.Count - 4)));
            bool anyWeapon = site != null && MachineRegistry.AllRecords.Any(m => m != null && m.IsAlive && m.RegionId == Campaign.Regions.FracturedCityLayout.RegionId
                && site.TryGetMachineWeapon(m.LogicId, out Campaign.Combat.MachineWeaponInfo w) && w.WeaponIndex >= 0);
            bool damaged = jammer != null && (jammer.Health < hp0 || !jammer.IsAlive);
            Write($"  - 6 真实秒：内核走了 {GameClock.Ticks - ticks0} 步，干扰机耐久 {hp0:F0}→{jammer?.Health:F0}；编队事件：{recent}");
            Check(GameClock.Ticks - ticks0 >= 300 && (damaged || !anyWeapon) && recent.Length > 0 && !recent.Contains("⟦"),
                anyWeapon ? "真实 Play 帧里编队攻击在战斗内核里执行：追上去开火，干扰机掉血" : "编队攻击执行到开火结算，没有武器时给出可读原因");
            // 表现对象的位置 = 内核位置（插值），不另算一套。
            float maxGap = 0f;
            foreach (Campaign.Regions.MachineView v in Object.FindObjectsByType<Campaign.Regions.MachineView>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (v.Marker != null && v.Marker.Site == site)
                {
                    Vector2 k = v.Marker.Position;
                    maxGap = Mathf.Max(maxGap, Vector2.Distance(k, new Vector2(v.transform.position.x, v.transform.position.z)));
                }
            }
            Check(maxGap < 0.5f, $"机器表现对象跟随内核位置插值（最大偏差 {maxGap:F3} 米 < 一步位移）");
            // FG2-FW-02（DEBT-FG2FW01-01）：固件读法在真实 Play 的地点内核里生效——带驻留 / 拖尾读法的机器开过火就留下区域，画面上画出来。
            bool zoneReading = site != null && MachineRegistry.AllRecords.Any(m => m != null && m.IsAlive && m.RegionId == Campaign.Regions.FracturedCityLayout.RegionId
                && site.TryGetMachineWeapon(m.LogicId, out Campaign.Combat.MachineWeaponInfo mw) && mw.WeaponIndex >= 0
                && site.Kernel.TryGetWeapon(mw.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon kw) && kw.Reading.ZoneSeconds > 0f);
            long zonesSpawned = site?.Kernel.Counters.ZonesSpawned ?? 0;
            int fx = site?.Renderer?.LastEffectInstances ?? -1;
            Write($"  - 读法：生成区域 {zonesSpawned} 块，当前 {site?.Kernel.ZoneCount} 块，画面区域 / 无人机实例 {fx}");
            int zonesNow = site?.Kernel.ZoneCount ?? 0;
            bool gpu = site?.Renderer != null && site.Renderer.GpuAvailable;
            Check(zoneReading && zonesSpawned > 0 && (!gpu || zonesNow == 0 || fx >= zonesNow),
                $"真实 Play 里带拖尾读法的机器打出区域（共 {zonesSpawned} 块，当前 {zonesNow} 块，画面实例 {fx}{(gpu ? "" : "，无 GPU 不画")}）");
            Next(230, "FG2-FW-03：光标移到带状态标签的单位上（头顶图标 + 悬停读数）");
        }

        /// <summary>FG2-FW-03（FGR-FW-031）：真实 Play 里带“拖尾”读法的机器打出的状态标签画在单位头顶，光标停上去弹出悬停读数（名称 / 剩余时间 / 叠层）。
        /// 光标每帧跟着这个单位的画面位置（单位在动），走正式的输入读取 → 内核拾取 → 世界悬停提示。</summary>
        private static void StepRuinsTagHover(double inStep)
        {
            Campaign.Regions.FracturedCityController city = GameRoot.FracturedCity;
            Campaign.Combat.CombatSite site = city?.Combat;
            Camera cam = WorldView.Camera != null ? WorldView.Camera : Camera.main;
            int unit = 0;
            string tagName = null;
            if (site != null && !site.IsDisposed)
            {
                foreach (RegionEnemyRecord e in CampaignSession.Current?.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                {
                    if (e == null || !e.IsAlive || !site.TryGetEnemyUnit(e.EnemyInstanceId, out int u))
                    {
                        continue;
                    }
                    List<(string Glyph, string Name, float Seconds, int Stacks)> tags = Campaign.Combat.StatusTagHover.Describe(site.Kernel, u);
                    if (tags != null && tags.Count > 0)
                    {
                        unit = u;
                        tagName = tags[0].Name;
                        break;
                    }
                }
            }
            if (unit != 0 && cam != null && site.Kernel.TryGetUnit(unit, out BinGames.Sim.Combat.CombatUnitView view))
            {
                Vector3 screen = cam.WorldToScreenPoint(new Vector3((float)view.Position.x, 0f, (float)view.Position.y));
                InputRouter.DebugSetReader(new ScriptedReader { Mouse = screen });
            }
            bool shown = UiTooltip.IsVisible && UiTooltip.HoveringWorld && UiTooltip.Content != null && tagName != null
                         && UiTooltip.Content.Body.Contains(tagName) && UiTooltip.Content.Title == Localization.GameText.Get("tag.hover.title");
            if (!shown && inStep < 8)
            {
                return;
            }
            int icons = site?.Renderer?.LastIconInstances ?? -1;
            bool gpu = site?.Renderer != null && site.Renderer.GpuAvailable;
            Write($"  - 状态标签：光标下单位 {unit}（{tagName}），头顶图标实例 {icons}，悬停读数“{UiTooltip.Content?.Body?.Replace('\n', '/')}”");
            Check(site != null && site.Kernel.ReactionRuleCount == Campaign.Content.NamedReactionCatalog.TagRules.Count && site.Kernel.ReactionRuleCount == 16,
                $"破碎都市的地点内核登记了 {site?.Kernel.ReactionRuleCount} 条具名标签反应规则（fg.TbReaction）");
            Check(GameSettings.HasSeenGuidanceHook(GuidanceHooks.StatusTagFirstSeen), "第一次看到头顶状态标签时发出引导钩子（内容在 FG15-UX-04）");
            // FG2-FW-04：真实 Play 里读法生成区域时出声、镜头正看着的地点弹出读法弹字（承接 DEBT-FG2FW02-02）；远征的伤害归因场次在记敌方受到的伤害。
            CampaignState rs = CampaignSession.Current;
            ReactionSessionRecord sess = Campaign.Combat.ReactionAttribution.Current(rs, Campaign.Regions.FracturedCityLayout.RegionId, create: false);
            Write($"  - 反应反馈：读法音效 {Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.ReadingZone)} 次，弹字新开 {Campaign.Feedback.ReactionPopups.SpawnedCount} 条，" +
                  $"远征归因“{Campaign.Combat.ReactionAttribution.Title(sess)}”敌方受伤 {sess?.TotalDamage:0.0}");
            Check(Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.ReadingZone) > 0 && Campaign.Feedback.ReactionPopups.SpawnedCount > 0
                  && UI.Kit.ReactionPopupHudUIToolkit.Instance != null,
                "真实 Play：拖尾读法生成区域时出声、弹出“区域展开”读法弹字（观察中的地点）");
            Check(sess != null && sess.Kind == Campaign.Combat.ReactionAttribution.KindExpedition && sess.TotalDamage > 0 && sess.EndTick < 0,
                $"真实 Play：这次远征的伤害归因场次在记账（{Campaign.Combat.ReactionAttribution.Title(sess)}，敌方受伤 {sess?.TotalDamage:0.0}）");
            // 卡片“伤害归因进入统计面板”：同一份数据在统计面板里出现（累计段 + 这一场的明细），“远征”筛选下仍在；点关闭收起。
            UI.Kit.StatsPanelUIToolkit.Open();
            UI.Kit.StatsPanelUIToolkit sp = UI.Kit.StatsPanelUIToolkit.Instance;
            string sessTitle = Campaign.Combat.ReactionAttribution.Title(sess);
            System.Func<bool> hasSession = () =>
            {
                for (int i = 0; sp != null && i < sp.VisibleRowCount; i++)
                {
                    if (sp.RowText(i).StartsWith(sessTitle, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            };
            bool spAll = sp != null && UI.Kit.StatsPanelUIToolkit.IsOpen && sp.VisibleRowCount >= 3 && sess != null && hasSession()
                         && sp.RowText(0) == Localization.GameText.Format("stats.panel.totals_title", rs.Stats.ReactionSessions.Length);
            bool spExp = ClickUitk("[StatsPanelHost]", "StatsFilterExpedition") && sp != null && sp.CurrentFilter == Campaign.Combat.ReactionLogFilter.Expedition && hasSession();
            string spRow1 = sp?.RowText(1) ?? string.Empty;
            bool spClosed = ClickUitk("[StatsPanelHost]", "StatsPanelClose") && !UI.Kit.StatsPanelUIToolkit.IsOpen;
            Check(spAll && spExp && spClosed, $"真实 Play：统计面板“战斗 · 反应伤害归因”列出累计（“{spRow1}”）与这一场“{sessTitle}”的明细，“远征”筛选下仍在，点关闭收起");
            Check(unit != 0 && icons > 0 && shown,
                $"真实 Play：带标签的敌人头顶画出图标（{icons} 个{(gpu ? "" : "，无 GPU 只备缓冲")}），光标停上去弹出悬停读数（{tagName}，含剩余时间与叠层）");
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen });
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.OpenSignalCore));
            Next(155, "FG1-SIG-01：远征途中按信号核键");
        }

        private static void StepSignalExpeditionOpened(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string lockedWord = Localization.GameText.Get("signal.hud.core_button_locked").Split('·').Last().Trim();
            Check(UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && hud.LockVisible && hud.LockText == Localization.GameText.Get("signal.reason.expedition")
                  && hud.EntryText.Contains(lockedWord) && Campaign.Signal.SignalCoreService.ExpeditionUnderway,
                $"真实派遣的远征在外：面板顶部“{hud?.LockText}”，HUD“{hud?.EntryText}”");
            Check(ClickUitk("[SignalCoreHost]", "SignalSlot0") && ClickUitk("[SignalCoreHost]", "SignalUnequip"), "点 1 号槽再点“卸下”");
            Next(156, "远征途中尝试卸下 1 号槽的过载");
        }

        private static void StepSignalExpeditionDenied(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            CampaignState st = CampaignSession.Current;
            string reason = Localization.GameText.Get("signal.reason.expedition");
            bool caption = FeedbackCues.ActiveCaptions.Any(c => c.Cue == FeedbackCueId.Denied && c.Text.Contains(reason));
            Check(hud.FeedbackIsError && hud.FeedbackText == reason && caption
                  && Campaign.Signal.SignalCoreService.SlotContentId(st, 0) == Campaign.Content.FirmwareCatalog.FwOverloadId,
                $"远征途中修改被拒绝：面板写“{hud.FeedbackText}”，拒绝音字幕 {caption}，过载仍在 1 号槽");
            PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(157, "按 Esc 关闭信号核面板");
        }

        private static void StepSignalExpeditionClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen, "Esc 关闭信号核面板（没有打开暂停菜单）");
            Next(207, "FG1-SIG-07：跨地点远距离跳转（等冷却结束）");
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

        /// <summary>FG0-ARCH-03：场景里是否还有这个名字的根对象（含隐藏的）。不被观察的地点表现对象应当已经销毁，而不只是隐藏。</summary>
        private static bool RootExistsIncludingInactive(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(t => t.parent == null && t.name == name);

        private static void StepWorldHomeKeepsRunning(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            long atDispatch = long.TryParse(SessionState.GetString(K + "TicksAtDispatch", "0"), out long t) ? t : 0;
            bool homeRunning = GameRoot.HomeValley != null && GameRoot.HomeValley.IsLoaded && !GameRoot.HomeValley.IsActive;
            bool ruinsObserved = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive;
            bool homeRootExists = RootExistsIncludingInactive("[HomeValley]");
            Check(homeRunning && ruinsObserved && GameClock.Ticks > atDispatch && !homeRootExists,
                $"派遣后家园仍在运行（已载入、不被观察、表现对象已销毁——含隐藏的也没有）、镜头在破碎都市；统一时钟 {atDispatch}→{GameClock.Ticks} 步");
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
            bool ruinsRootExists = RootExistsIncludingInactive("[FracturedCityRoot]");
            Check(homeObserved && ruinsRunning && homeRoot != null && !ruinsRootExists && Vector2.Distance(CameraFocus(), Campaign.Regions.HomeValleyLayout.Core.Position) < 1f,
                $"镜头回到家园（焦点 {CameraFocus()}）：家园表现对象重建并显示、破碎都市表现对象已销毁（含隐藏的也没有）但仍在运行");
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
            // FG1-SIG-03：铸造前哨外围也能从命令栏机器列表直接接入（FG-GAP-029 关闭）——真实按钮点击 → 0.35 秒过渡 → 直控 → 按接入 / 退出键离开。
            if (!StepFoundryMachineList(inStep))
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

        /// <summary>FG1-SIG-03：铸造前哨外围的机器列表接入（命令栏候选条，真实按钮回调）。返回 true = 这段已走完（或此刻没有可接入的机器、已如实记录）。</summary>
        private static bool StepFoundryMachineList(double inStep)
        {
            int phase = SessionState.GetInt(K + "FoListPhase", 0);
            if (phase == 0)
            {
                // FG1-SIG-07：信号在归还核心、目标在远征地点 = 跨地点的远距离跳转——先等上一次远距离跳转的冷却结束。
                if (Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(CampaignSession.Current) > 0 && inStep < 15)
                {
                    return false;
                }
                Campaign.Regions.HomeValleyMachineMarker m = GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive
                    ? SigCandidateIn(GameRoot.FoundryOutpost.Combat, Campaign.Regions.FoundryOutpostLayout.RegionId, 0)
                    : null;
                if (m == null)
                {
                    Write("  - 铸造前哨外围此刻没有可以接入的机器：跳过机器列表接入");
                    SessionState.SetInt(K + "FoListPhase", 3);
                    return true;
                }
                GameObject host = GameObject.Find("[RegionCommandBarHost]");
                UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
                VisualElement bar = doc?.rootVisualElement?.Q<VisualElement>("RegionCommandBarRoot");
                Check(bar != null && bar.style.display == DisplayStyle.Flex, "铸造前哨外围：命令栏（机器列表）显示");
                SessionState.SetInt(K + "FoListId", m.LogicId);
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                Check(ClickMachineList(m.LogicId), $"铸造前哨外围：命令栏机器列表里点 {SigLabel(m.LogicId)}");
                SessionState.SetInt(K + "FoListPhase", 1);
                return false;
            }
            float at = SessionState.GetFloat(K + "FoListAt", 0f);
            int id = SessionState.GetInt(K + "FoListId", 0);
            if (phase == 1)
            {
                if (inStep < at + 2.6)
                {
                    return false;
                }
                Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == id && GameRoot.FoundryOutpost != null
                      && GameRoot.FoundryOutpost.PossessedMachineLogicId == id && WorldView.Director.Mode == View.ViewMode.Direct,
                    $"铸造前哨外围机器列表点一下：从归还核心跳到远征地点 = 远距离跳转（1.5 秒过渡）后接入 {SigLabel(id)}，镜头直控");
                // FG1-E2E-01（FGJ-M1 发现）：出征时家园晚到的摘除不能清掉远征地点的表现登记——远征地点接入的机器照样有形变 / 安全模式图标的挂点。
                Check(View.MachineMorphView.IsRegistered(id),
                    $"远征地点的 {SigLabel(id)} 形变表现已登记（出征时家园晚到的摘除没有把它清掉）");
                CheckNoTextMarkers("铸造前哨接入后");
                // FG1-SIG-07 审查修复：先按 H 再按 Esc——跳回家园的远距离过渡能用真实 Esc 取消（不弹暂停菜单、信号留在远征队、不开始新的冷却）。
                SessionState.SetInt(K + "FoHome0", Campaign.Signal.SignalUplinkService.JumpHomeCount);
                SessionState.SetInt(K + "FoFar0", Campaign.Signal.SignalUplinkService.FarJumpCount);
                SessionState.SetInt(K + "FoCancel0", Campaign.Signal.SignalUplinkService.CancelCount);
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpHome));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 7);
                return false;
            }
            CampaignState st = CampaignSession.Current;
            if (phase == 7)
            {
                if (inStep < at + 0.5)
                {
                    return false;
                }
                Check(Campaign.Signal.SignalUplinkService.IsJumpingHome && Campaign.Signal.SignalPresence.CurrentMachineLogicId == id,
                    $"按 H：跳回家园过渡中（远距离 1.5 秒），信号还在 {SigLabel(id)} 里——此时按 Esc");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 8);
                return false;
            }
            if (phase == 8)
            {
                if (inStep < at + 2.2)
                {
                    return false; // 等过原本 1.5 秒的过渡：取消了就不会到点提交。
                }
                Check(!Campaign.Signal.SignalUplinkService.IsJumpingHome && Campaign.Signal.SignalPresence.CurrentMachineLogicId == id
                      && GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.PossessedMachineLogicId == id && !PauseMenuUIToolkit.IsOpen
                      && Campaign.Signal.SignalUplinkService.JumpHomeCount == SessionState.GetInt(K + "FoHome0", 0)
                      && Campaign.Signal.SignalUplinkService.FarJumpCount == SessionState.GetInt(K + "FoFar0", 0)
                      && Campaign.Signal.SignalUplinkService.CancelCount == SessionState.GetInt(K + "FoCancel0", 0) + 1
                      && Campaign.Signal.SignalUplinkService.LastCancel == Campaign.Signal.UplinkCancelReason.PlayerCancelled,
                    $"跳回家园途中按 Esc：取消（“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”），信号留在 {SigLabel(id)}，暂停菜单没弹，没开始新的冷却");
                // FG1-SIG-07（FGJ-M1 第 7 步）：在远征地点的机器里按 H 跳回家园——远距离（1.5 秒过渡），冷却中也能回家，并开始冷却。
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.JumpHome));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 4);
                return false;
            }
            if (phase == 4)
            {
                if (inStep < at + 0.5)
                {
                    return false;
                }
                if (!SessionState.GetBool(K + "FoHomeMid", false))
                {
                    SessionState.SetBool(K + "FoHomeMid", true);
                    Check(Campaign.Signal.SignalUplinkService.IsJumpingHome && Campaign.Signal.SignalPresence.CurrentMachineLogicId == id
                          && GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive,
                        $"按 H：跳回家园过渡中（从远征地点回家 = 远距离，1.5 秒），信号还在 {SigLabel(id)} 里、镜头还在铸造前哨");
                    return false;
                }
                if (inStep < at + 3.2)
                {
                    return false;
                }
                SessionState.SetBool(K + "FoHomeMid", false);
                Check(Campaign.Signal.SignalPresence.AtCore && GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && WorldView.Director.Mode == View.ViewMode.Strategy
                      && Campaign.Signal.SignalUplinkService.JumpHomeCount == SessionState.GetInt(K + "FoHome0", 0) + 1
                      && Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st) > 0,
                    $"FGJ-M1 第 7 步：信号回到归还核心、镜头回到家园，远征继续运行；远距离跳转冷却 {Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st):F1} 秒");
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.JumpPreviousMachine));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 5);
                return false;
            }
            if (phase == 5)
            {
                if (inStep < at + 0.6)
                {
                    return false;
                }
                if (!SessionState.GetBool(K + "FoJDenied", false))
                {
                    SessionState.SetBool(K + "FoJDenied", true);
                    Check(Campaign.Signal.SignalPresence.AtCore && Campaign.Signal.SignalUplinkService.LastFailure == Campaign.Signal.UplinkFailure.JumpCooldown,
                        $"刚回家马上按 J 跳回远征队：远距离跳转冷却中被拒——“{Campaign.Signal.SignalUplinkService.LastFeedbackText}”");
                }
                if (Campaign.Signal.SignalUplinkService.JumpCooldownRemaining(st) > 0)
                {
                    if (inStep > at + 20)
                    {
                        Finish("20 秒内远距离跳转冷却没有结束");
                    }
                    return false;
                }
                SessionState.SetBool(K + "FoJDenied", false);
                PressKeyOffScreen(GameSettings.KeyBindings.GetKey(GameActionId.JumpPreviousMachine));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 6);
                return false;
            }
            if (phase == 6)
            {
                if (inStep < at + 3.0)
                {
                    return false;
                }
                Check(Campaign.Signal.SignalPresence.CurrentMachineLogicId == id && GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive
                      && GameRoot.FoundryOutpost.PossessedMachineLogicId == id && WorldView.Director.Mode == View.ViewMode.Direct,
                    $"FGJ-M1 第 8 步：冷却结束后按 J，信号跳回远征队的 {SigLabel(id)}（跨地点远距离跳转，镜头切回铸造前哨并进直控）");
                PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
                SessionState.SetFloat(K + "FoListAt", (float)inStep);
                SessionState.SetInt(K + "FoListPhase", 2);
                return false;
            }
            if (phase == 2)
            {
                if (inStep < at + 3.0)
                {
                    return false;
                }
                Check(Campaign.Signal.SignalPresence.AtCore && GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.PossessedMachineLogicId == null
                      && WorldView.Director.Mode == View.ViewMode.Strategy,
                    "铸造前哨外围按接入 / 退出键离开：信号回到归还核心、镜头回到战略");
                SessionState.SetInt(K + "FoListPhase", 3);
            }
            return true;
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
            Next(140, "FG0-ARCH-03：家园突袭战斗原型（测试捷径）");
        }

        // ── FG0-ARCH-03：家园突袭的逐单位 / 逐弹体逻辑在战斗内核（测试捷径生成性能场景；正式突袭导演与到达结算属于 FG6）──

        private static void StepRaidStart(double inStep)
        {
            if (inStep < 0.2)
            {
                return;
            }
            Campaign.Combat.CombatSite home = GameRoot.HomeValley?.Combat;
            if (home == null)
            {
                Finish("家园没有战斗内核");
                return;
            }
            Campaign.Combat.CombatBench.Spec spec = Campaign.Combat.CombatBench.PerfSpec();
            Vector2 center = Campaign.Regions.HomeValleyLayout.Core.Position + new Vector2(0f, 160f);
            Campaign.Combat.CombatBench.SpawnPerfScenario(home, center, 200, 80, spec);
            // 镜头留在家园（性能场景在北面 160 米：突袭者感知 60 米，碰不到家园的机器，不影响后面的存档流程）。
            SessionState.SetString(K + "RaidTicks", GameClock.Ticks.ToString());
            SessionState.SetInt(K + "RaidMaxProj", 0);
            SessionState.SetFloat(K + "RaidMaxFrameMs", 0f);
            SessionState.SetInt(K + "RaidFrames", 0);
            SessionState.SetFloat(K + "RaidFrameMsSum", 0f);
            SessionState.SetInt(K + "RaidHomeMachines", GameRoot.HomeValley?.LiveMachineCount ?? -1);
            Next(141, "测试捷径：家园北面 160 米生成 200 个突袭者 + 80 座炮塔（性能场景）");
        }

        private static string HomeValleyLayoutRegion() => Campaign.Regions.HomeValleyLayout.RegionId;

        private static void StepRaidRunning(double inStep)
        {
            Campaign.Combat.CombatSite home = GameRoot.HomeValley?.Combat;
            if (home == null)
            {
                Finish("家园战斗内核丢了");
                return;
            }
            float ms = Time.unscaledDeltaTime * 1000f;
            // 编辑器 update 一帧会回调多次：每个 Play 帧只采样一次。
            bool newFrame = Time.frameCount != SessionState.GetInt(K + "RaidLastFrame", -1);
            SessionState.SetInt(K + "RaidLastFrame", Time.frameCount);
            if (inStep > 3 && newFrame)
            {
                SessionState.SetInt(K + "RaidMaxProj", Math.Max(SessionState.GetInt(K + "RaidMaxProj", 0), home.Kernel.ProjectileCount));
                SessionState.SetFloat(K + "RaidMaxFrameMs", Mathf.Max(SessionState.GetFloat(K + "RaidMaxFrameMs", 0f), ms));
                SessionState.SetInt(K + "RaidFrames", SessionState.GetInt(K + "RaidFrames", 0) + 1);
                SessionState.SetFloat(K + "RaidFrameMsSum", SessionState.GetFloat(K + "RaidFrameMsSum", 0f) + ms);
            }
            if (inStep < 8)
            {
                return;
            }
            long ticks = GameClock.Ticks - long.Parse(SessionState.GetString(K + "RaidTicks", "0"));
            int maxProj = SessionState.GetInt(K + "RaidMaxProj", 0);
            int frames = SessionState.GetInt(K + "RaidFrames", 0);
            float avgMs = frames > 0 ? SessionState.GetFloat(K + "RaidFrameMsSum", 0f) / frames : 0f;
            int raiders = home.Kernel.CountAlive(BinGames.Sim.Combat.CombatFaction.Hostile);
            int turrets = home.Kernel.CountAlive(BinGames.Sim.Combat.CombatFaction.Player, BinGames.Sim.Combat.CombatUnitKind.Turret);
            BinGames.Sim.Combat.CombatRenderer r = home.Renderer;
            Write($"  - 突袭原型 8 真实秒：内核 {ticks} 步，弹体峰值 {maxProj} 枚，存活突袭者 {raiders}、炮塔 {turrets}；内核单步 {home.LastKernelMs:F3} ms；" +
                  $"真实帧 平均 {avgMs:F1} ms / 最长 {SessionState.GetFloat(K + "RaidMaxFrameMs", 0f):F1} ms（{frames} 帧，-nographics 下只含 CPU）；" +
                  $"实例 {r?.LastUnitInstances} 单位 + {r?.LastProjectileInstances} 弹体，{(r != null && r.GpuAvailable ? "GPU 绘制" : "无图形设备：" + r?.GpuUnavailableReason)}");
            Check(ticks >= 400 && maxProj >= 1500 && raiders >= 150 && turrets >= 60 && r != null && r.LastUnitInstances == raiders + turrets && r.LastProjectileInstances == home.Kernel.ProjectileCount,
                $"真实 Play 帧里家园战斗内核跑着 {raiders} 个突袭者、{turrets} 座炮塔、峰值 {maxProj} 枚弹体（≥ 1,500），实例化缓冲与内核一致");
            CheckNoTextMarkers("家园突袭原型");
            int removed = Campaign.Combat.CombatBench.ClearPrototypeUnits(home);
            Next(142, $"清场：移除 {removed} 个原型单位，剩下的弹体飞完即消失");
        }

        private static void StepRaidCleared(double inStep)
        {
            if (inStep < 5)
            {
                return;
            }
            Campaign.Combat.CombatSite home = GameRoot.HomeValley?.Combat;
            int machinesBefore = SessionState.GetInt(K + "RaidHomeMachines", -1);
            Check(home != null && home.Kernel.CountAlive(BinGames.Sim.Combat.CombatFaction.Hostile) == 0 && home.Kernel.ProjectileCount == 0
                  && home.Kernel.CountAlive(BinGames.Sim.Combat.CombatFaction.Player, BinGames.Sim.Combat.CombatUnitKind.Turret) == 0
                  && GameRoot.HomeValley?.LiveMachineCount == machinesBefore,
                $"原型单位清场后弹体飞完消失（剩 {home?.Kernel.ProjectileCount} 枚），家园机器数不变（{machinesBefore} → {GameRoot.HomeValley?.LiveMachineCount} 台；此时远征队仍在外）");
            Next(177, "FG1-SIG-03：存档前先接入一台家园机器");
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
            // FG1-SIG-03：存档时信号在某台机器里，读档恢复接入会把镜头放到那台机器所在的地点（可能是远征地点）。
            string sigSite = SessionState.GetString(K + "SigSavedSite", Campaign.Regions.HomeValleyLayout.RegionId);
            if (!SigSiteActive(sigSite))
            {
                if (inStep > 120)
                {
                    Finish($"120 秒内读档没进入 {sigSite}");
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
            // FG1-SIG-01：真实“保存并返回主菜单 → 读取”后，信号核（槽位里的过载实例、预设）完全一致。
            Check(st != null && Campaign.Signal.SignalCoreService.SlotContentId(st, 0) == Campaign.Content.FirmwareCatalog.FwOverloadId
                  && Campaign.Signal.SignalCoreService.SlotChip(st, 0)?.State == PrimitiveChipState.SignalCore
                  && st.SignalCore.Presets.Length == 1 && st.SignalCore.Presets[0].Name == "攻坚",
                $"读档后信号核仍装着过载、预设“攻坚”还在（{Campaign.Signal.SignalCoreService.SummaryText(st)}）");
            CheckNoTextMarkers("读档进入游戏");
            BeltKernel bk = BeltNetworkService.Kernel;
            int bx = SessionState.GetInt(K + "BeltX", 0);
            int by = SessionState.GetInt(K + "BeltY", 0);
            Check(bk != null && bk.CellCount == SessionState.GetInt(K + "BeltCells", -1) && bk.Ledger.Balanced
                  && bk.StepIndex >= long.Parse(SessionState.GetString(K + "BeltSavedSteps", "0"))
                  && HomeGridService.MapFor(st).GetBelt(new GridCell(bx, by)) != 0 && BeltNetworkService.LastLoadError == null,
                $"读档恢复传送带：{bk?.CellCount} 格、{bk?.ItemCount} 件（存档时 {SessionState.GetInt(K + "BeltSavedItems", -1)} 件，读档后已继续运行），账本平衡，格网传送带层恢复");
            // FG1-SIG-03：存档时信号在机器里 → 真实“保存并返回主菜单 → 读取”后信号仍在那台机器里（接管恢复、镜头进直控、HUD）。
            int sigSaved = SessionState.GetInt(K + "SigSaved", 0);
            if (sigSaved == 0)
            {
                Check(Campaign.Signal.SignalPresence.AtCore && !Campaign.Signal.SignalUplinkService.IsPending,
                    "存档时信号在归还核心：读档后仍在归还核心");
                Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(st);
                Next(80, "测试捷径：核心被毁（HomeValleySoftlockGuard.DebugDestroyCore），等失败页出现");
                return;
            }
            UI.SignalCore.SignalCoreHudUIToolkit sigHud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            int possessedAfterLoad = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive ? GameRoot.HomeValley.PossessedMachineLogicId ?? 0
                : GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive ? GameRoot.FracturedCity.PossessedMachineLogicId ?? 0 : 0;
            Check(sigSaved != 0 && Campaign.Signal.SignalPresence.CurrentMachineLogicId == sigSaved && possessedAfterLoad == sigSaved
                  && WorldView.Director.HeadingDirect && sigHud != null && sigHud.LocationText.Contains(SigLabel(sigSaved)),
                $"读档后信号仍在 {SigLabel(sigSaved)} 里（{sigSite}；接管恢复、镜头直控；HUD“{sigHud?.LocationText}”）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            Next(29, "读档后按接入 / 退出键离开");
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

        /// <summary>FG1-SIG-02：模拟按一次组合键（例如 Ctrl+Z）：下一帧修饰键按住、主键按下。</summary>
        private static void PressChord(InputChord chord)
        {
            KeyCode held = (chord.Mods & InputModifier.Ctrl) != 0 ? KeyCode.LeftControl
                : (chord.Mods & InputModifier.Alt) != 0 ? KeyCode.LeftAlt
                : (chord.Mods & InputModifier.Shift) != 0 ? KeyCode.LeftShift
                : KeyCode.None;
            InputRouter.DebugSetReader(new ScriptedReader { Key = chord.Key, KeyFrame = Time.frameCount + 1, Held = held });
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
            /// <summary>与 <see cref="Key"/> 同一帧按住的修饰键（组合键用）。</summary>
            public KeyCode Held = KeyCode.None;

            public bool GetKey(KeyCode key) => Held != KeyCode.None && key == Held && Time.frameCount == KeyFrame;
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
