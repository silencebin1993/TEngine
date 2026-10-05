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
            SessionState.SetInt(K + "PerfWarnings", 0);
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
                    case 38: StepWornRepairOrdered(inStep); break;
                    case 39: StepWornRepairDone(inStep); break;
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
                    case 343: StepAwayTripOut(inStep); break; // FG4-ECO-09：离家报告正式路径
                    case 344: StepAwayTripBack(inStep); break;
                    case 345: StepSoftlockPrint(inStep); break; // FG4-ECO-10：软锁保底（应急打印 + 应急产废料）
                    case 130: StepBeltsLaid(inStep); break;
                    case 131: StepBeltsFar(inStep); break;
                    case 132: StepBeltsNear(inStep); break;
                    case 133: StepBeltsDone(inStep); break;
                    // FG3-LOG-03：传送带悬停读数、清带（确认框取消 / 确认）、端口面板、被摧毁的传送带在施工队列里重建（真实鼠标 / 按键）。
                    case 249: StepBeltFocused(inStep); break;
                    case 250: StepBeltHoverInBuild(inStep); break;
                    case 251: StepBeltClearModeOn(inStep); break;
                    case 252: StepBeltClearAsked(inStep); break;
                    case 253: StepBeltClearCancelled(inStep); break;
                    case 254: StepBeltClearAskedAgain(inStep); break;
                    case 255: StepBeltClearConfirmed(inStep); break;
                    case 256: StepBeltClearModeOff(inStep); break;
                    case 257: StepBeltPortPanelOpened(inStep); break;
                    case 258: StepBeltPortPanelClosed(inStep); break;
                    case 259: StepBeltRebuildQueue(inStep); break;
                    case 260: StepBeltRebuildDone(inStep); break;
                    case 261: StepBeltCoreFocused(inStep); break;
                    case 262: StepBeltBuildClosedForFly(inStep); break;
                    case 263: StepBeltBuildReopenedAtCore(inStep); break;
                    // FG3-LOG-04：分流器 / 地下传送带（建造菜单点选、放置预览、单击放分流器虚影、拖地下传送带：跨度超限给原因、正确拖放下；节点面板改比例、Esc）。
                    case 264: StepNodeBuildReady(inStep); break;
                    case 265: StepNodePreview(inStep); break;
                    case 266: StepNodeSplitterPlaced(inStep); break;
                    case 267: StepNodeUnderTooFar(inStep); break;
                    case 268: StepNodeUnderPlaced(inStep); break;
                    case 269: StepNodeDeselected(inStep); break;
                    case 270: StepNodePanelOpened(inStep); break;
                    case 271: StepNodePanelClosed(inStep); break;
                    // FG3-LOG-05：管线与流体（建造菜单点选泵、放置预览、单击放在水源上、拖管线、放储罐；管线面板改储罐模式、冲洗先确认（取消）、Esc；悬停读数；渲染实例）。
                    case 272: StepPipeBuildReady(inStep); break;
                    case 273: StepPipePumpPreview(inStep); break;
                    case 274: StepPipePumpPlaced(inStep); break;
                    case 275: StepPipeDragged(inStep); break;
                    case 276: StepPipeTankPlaced(inStep); break;
                    case 277: StepPipeDeselected(inStep); break;
                    case 278: StepPipePanelOpened(inStep); break;
                    case 279: StepPipePanelClosed(inStep); break;
                    // FG3-LOG-06：电力子网与电塔（建造菜单“能源”点电塔、放置预览写接入电网 + 光标处覆盖圈、单击放虚影；Alt+G 打开电网面板、叠加层按钮、
                    // 电塔被摧毁一分为二的警告与面板刷新、Alt+G 关闭；建造模式指着电塔的状态行）。
                    case 280: StepPowerBuildReady(inStep); break;
                    case 281: StepPowerPreview(inStep); break;
                    case 282: StepPowerPlaced(inStep); break;
                    case 283: StepPowerPanelOpened(inStep); break;
                    case 284: StepPowerPanelClosed(inStep); break;
                    // FG3-LOG-07：规划工具（真实鼠标 / 按键）：Ctrl+C 复制模式 → 拖框复制（连同分流器设置）→ 鼠标移到目标看粘贴预览 → R 整体旋转 → 左键放下 →
                    // 右键退出 → Ctrl+Z 撤销 / Ctrl+Y 重做 → U 升级模式拖框 → Q 吸管 → Ctrl+B 布局库、点“保存剪贴板”、Ctrl+B 关闭。
                    case 285: StepPlanReady(inStep); break;
                    case 286: StepPlanCopyMode(inStep); break;
                    case 287: StepPlanCopied(inStep); break;
                    case 288: StepPlanPreview(inStep); break;
                    case 289: StepPlanRotated(inStep); break;
                    case 290: StepPlanPasted(inStep); break;
                    case 291: StepPlanPasteExited(inStep); break;
                    case 292: StepPlanUndone(inStep); break;
                    case 293: StepPlanRedone(inStep); break;
                    case 294: StepPlanUpgradeMode(inStep); break;
                    case 295: StepPlanUpgraded(inStep); break;
                    case 296: StepPlanUpgradeExited(inStep); break;
                    case 297: StepPlanHoverSplitter(inStep); break;
                    case 298: StepPlanEyedropped(inStep); break;
                    case 299: StepPlanLibraryKey(inStep); break;
                    case 300: StepPlanLibraryOpen(inStep); break;
                    case 301: StepPlanLibraryClosed(inStep); break;
                    // FG3-LOG-08：叠加层与“为什么不工作”（Alt+O 选择器、点“堵塞”、O 关 / 重开、Ctrl+Alt+1 直达、Ctrl+O 面板、点原因镜头飞过去且建造模式不退出、Esc）。
                    case 302: StepOverlaySelectorOpened(inStep); break;
                    case 303: StepOverlayToggledOff(inStep); break;
                    case 304: StepOverlayReopened(inStep); break;
                    case 305: StepOverlayFlowKey(inStep); break;
                    case 306: StepDiagnosisOpened(inStep); break;
                    case 307: StepDiagnosisLocated(inStep); break;
                    case 308: StepDiagnosisClosed(inStep); break;
                    case 309: StepOverlayOffAgain(inStep); break;
                    case 310: StepOverlaySelectorEsc(inStep); break;
                    case 311: StepOverlayOffConfirmed(inStep); break;
                    // FG4-ECO-01：物资面板（Alt+I 打开、悬停物品图标看总库存 / 分布 / 净速率、悬停按图鉴键跳到物品图鉴、Esc 关图鉴、Alt+I 关面板）。
                    case 312: StepItemsOpenKey(inStep); break;
                    case 313: StepItemsPanelOpened(inStep); break;
                    case 314: StepItemsHoverShown(inStep); break;
                    case 315: StepItemsCodexJumped(inStep); break;
                    case 316: StepItemsCodexClosed(inStep); break;
                    case 317: StepItemsPanelClosed(inStep); break;
                    // FG4-ECO-06：常驻规则键（Alt+R 打开、同一个键再按关闭）。
                    case 338: StepRulesKeyOpened(inStep); break;
                    case 339: StepRulesKeyClosed(inStep); break;
                    // FG4-ECO-07：机器名册键（N 打开、同一个键再按关闭）、顶栏劳动力按钮打开名册、Esc 关闭。
                    case 340: StepRosterKeyOpened(inStep); break;
                    case 341: StepRosterKeyClosed(inStep); break;
                    case 342: StepRosterBarClosed(inStep); break;
                    // FG4-ECO-02：建造菜单“采集”页签——提取钻放在空地上被拒（原因写明要压矿脉）、流体泵放在空地上被拒 / 指着水源或油井可放（预览写抽什么）、
                    // 回收站放在废墟上（预览写储量）、点虚影打开通用面板、Esc 关闭。
                    case 318: StepProdBuildReady(inStep); break;
                    case 319: StepProdDrillRefused(inStep); break;
                    case 320: StepProdPumpPicked(inStep); break;
                    case 325: StepProdPumpRefused(inStep); break;
                    case 326: StepProdRecyclerPreview(inStep); break;
                    case 321: StepProdRecyclerPlaced(inStep); break;
                    case 322: StepProdDeselected(inStep); break;
                    case 323: StepProdPanelOpened(inStep); break;
                    case 324: StepProdPanelClosed(inStep); break;
                    // FG4-ECO-03：建造菜单“制造”页签——放电子组装台（虚影）、点它打开通用面板（配方下拉 4 项、配方记忆说明、“复制设置到同类建筑”按钮）、
                    // 下拉框选“电子件”即生效（两个输入口改收合金 / 稀土矿）、Esc 关面板、Esc 退出建造模式。
                    case 327: StepMfgPicked(inStep); break;
                    case 328: StepMfgPlaced(inStep); break;
                    case 329: StepMfgDeselected(inStep); break;
                    case 330: StepMfgPanelOpened(inStep); break;
                    case 331: StepMfgPanelClosed(inStep); break;
                    // FG4-ECO-04：建造菜单“能源”页签——放储能站（预览写接入电网、单击放虚影）；点建成的储能站打开电网面板（选中它的电网、储能站一节）、
                    // 放电对象下拉框选中即生效、曲线写分类发电；Esc 关面板；“物流”页签选地下管线、旋转、预览写“还没配对”、单击放一口；Esc 退出建造模式。
                    case 332: StepEnergyPicked(inStep); break;
                    case 333: StepEnergyPlaced(inStep); break;
                    case 334: StepEnergyDeselected(inStep); break;
                    case 335: StepEnergyPanelSet(inStep); break;
                    case 336: StepEnergyPanelClosed(inStep); break;
                    case 337: StepUndergroundPipePlaced(inStep); break;
                    // FG4-ECO-11：建造菜单“信号”页签——选超控阵列，建造栏成本写关键材料与获取途径、研究节点；单击放虚影（等关键材料）；
                    // 点建成的超控阵列打开通用面板（信号核槽位行）、点“禁用”→ HUD 信号核按钮写“（1 槽失效）”、面板写失效原因 → 点“启用”恢复；Esc 关面板、Esc 退出建造模式。
                    // FG5-RND-01：按 K 打开研发树（模态）→ 搜索“分流”→ 点“物流”筛选 → 滚轮缩放、悬停节点看解锁预览 → 左键加入队列（没有实验室：状态写明）→ K 关闭 →
                    // 建造菜单“研发”页签选仿真实验室、单击放虚影 → 实验室把技术数据转成研究点 → 研究完成（通知、门槛放开）→ 建造菜单“物流”页签与分流器标“新”，换页后消失。
                    case 352: StepResearchOpened(inStep); break;
                    case 353: StepResearchSearched(inStep); break;
                    case 354: StepResearchFiltered(inStep); break;
                    case 355: StepResearchHovered(inStep); break;
                    case 356: StepResearchQueued(inStep); break;
                    case 357: StepResearchLabPicked(inStep); break;
                    case 358: StepResearchLabPlaced(inStep); break;
                    case 359: StepResearchDone(inStep); break;
                    case 360: StepResearchNewMark(inStep); break;
                    // FG5-RND-03：建造菜单“研发”页签选靶场、单击放虚影 → 右键取消选择 → 左键点建成的靶场打开它的通用面板 → 点“靶场…”打开靶场面板（FGU-24）→
                    // 蓝图下拉选一张、点“投影”（不消耗资源，测试开始，读数实时刷新）→ 点“结束测试”（读数进测试记录）→ Esc 关闭靶场面板。
                    case 361: StepRangePicked(inStep); break;
                    case 362: StepRangePlaced(inStep); break;
                    case 363: StepRangeOpened(inStep); break;
                    case 364: StepRangeRunning(inStep); break;
                    case 365: StepRangeClosed(inStep); break;
                    // FG5-RND-04：放一座建成的电路合成台 → 左键点它打开通用面板 → 点“熔合…”打开合成台面板（FGU-21）→ 两个下拉选燃迹 / 漏油、点“模拟熔合”（有配方）→
                    // 点“正式熔合…”弹确认框、点“确认”入队 → 放开约一秒：熔合进行中 → 队列行“取消”全部退回 → 配方书键（默认 Alt+F）关掉 / 再打开配方书 → Esc 关闭。
                    case 366: StepFusionPlaced(inStep); break;
                    case 367: StepFusionBuildingPanel(inStep); break;
                    case 368: StepFusionOpened(inStep); break;
                    case 369: StepFusionSimulated(inStep); break;
                    case 370: StepFusionRunning(inStep); break;
                    case 371: StepFusionBook(inStep); break;
                    // FG5-RND-05：放一座建成的监听站、派一支突袭（测试捷径：突袭导演在 FG6-DEF-04）→ 左键点监听站打开通用面板（破译中）→ 点“情报…”打开情报面板（还没有情报：空状态）
                    // → Esc → 3x 放开：突袭预报出来（通知）→ 按情报键（默认 Y）打开 → 突袭预报行“在地图上查看”→ 战略地图上有来袭方向箭头 → Esc → 清理测试突袭与监听站。
                    case 372: StepIntelPlaced(inStep); break;
                    case 373: StepIntelBuildingPanel(inStep); break;
                    case 374: StepIntelOpenedEmpty(inStep); break;
                    case 375: StepIntelForecast(inStep); break;
                    case 376: StepIntelPanelRows(inStep); break;
                    case 377: StepIntelMap(inStep); break;
                    // FG5-RND-06：放一座建成的黑匣子陈列馆、家园一台测试机器阵亡（伤害夹具：正式突袭在 FG6-DEF-04）→ 阵亡通知附黑匣子去向 →
                    // 左键点陈列馆打开通用面板（分析中）→ 点“陈列馆…”打开陈列馆面板（黑匣子页：分析中）→ 点“纪念墙”页签（名字 · 编号 · 经历 · 阵亡地点）→ 点“按地点”→ Esc → 清理测试陈列馆。
                    case 378: StepBlackBoxPlaced(inStep); break;
                    case 379: StepBlackBoxBuildingPanel(inStep); break;
                    case 380: StepBlackBoxOpened(inStep); break;
                    case 381: StepBlackBoxMemorial(inStep); break;
                    case 382: StepBlackBoxClosed(inStep); break;
                    // 审查 P2：机器名册的“纪念墙…”入口（FGU-40 点名入口）：按 N 打开名册 → 点“纪念墙…”→ 陈列馆面板直接在纪念墙页 → Esc。
                    case 383: StepBlackBoxRosterOpened(inStep); break;
                    case 384: StepBlackBoxRosterMemorial(inStep); break;
                    case 385: StepBlackBoxRosterClosed(inStep); break;
                    // FG6-DEF-01：建造菜单“防御”页签选轻型炮塔（建造栏炮塔蓝图下拉 + 射程）→ 悬停时地面射程圈 → 单击放下虚影 → 右键取消 → 点建成的炮塔 →
                    // 建筑面板“炮塔…”→ 炮塔面板（标题、蓝图、五个模式按钮、射程圈）→ 点“最低耐久”立即生效 → Esc → 清理测试炮塔与虚影。
                    case 386: StepTurretPicked(inStep); break;
                    case 387: StepTurretPlaced(inStep); break;
                    case 388: StepTurretPanel(inStep); break;
                    case 389: StepTurretClosed(inStep); break;
                    // FG6-DEF-01 审查修复（FGR-DEF-005 接入的正式入口）：测试炮塔换带接入口的蓝图 → 点炮塔面板“接入” → Esc → 建造菜单键关掉建造模式 →
                    // 战略暂停中左键朝目标开火（被拒、提示）→ 继续后左键开火（命中）→ 按接入键离开炮塔 → 清理、重新打开建造模式。
                    case 390: StepTurretUplinked(inStep); break;
                    case 391: StepTurretAimPaused(inStep); break;
                    case 392: StepTurretAimFired(inStep); break;
                    case 393: StepTurretLeft(inStep); break;
                    // FG6-DEF-02：建造菜单“防御”页签选屏障 T1 → 悬停时地面画出敌方来路预览线 → 按住左键拖一段墙（6 格虚影，一步）→ 右键取消选择 →
                    // 点建成的护盾发生器 → 建筑面板“护盾…”→ 防御面板（护盾值、状态、倒计时）→ Esc → 清理测试建筑与虚影。
                    case 394: StepWallPicked(inStep); break;
                    case 395: StepWallDragged(inStep); break;
                    case 396: StepShieldPanel(inStep); break;
                    case 397: StepShieldPanelShown(inStep); break;
                    case 346: StepOverridePicked(inStep); break;
                    case 347: StepOverridePlaced(inStep); break;
                    case 348: StepOverridePanel(inStep); break;
                    case 349: StepOverrideDisabled(inStep); break;
                    case 350: StepOverrideEnabled(inStep); break;
                    case 351: StepOverrideClosed(inStep); break;
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
                    case 113: StepConstructionQueueOpened(inStep); break;
                    case 114: StepConstructionQueueClosed(inStep); break;
                    case 115: StepPrioritizeArea(inStep); break;
                    case 116: StepPrioritizeExited(inStep); break;
                    case 117: StepConstructionQueueReopened(inStep); break;
                    case 118: StepConstructionQueueToggledShut(inStep); break;
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
            if (cam == null || panelWidth < 1f || !WorldMapVectorLayer.CameraGroundQuad(cam, quad, View.CameraDirector.MaxStrategyOrthographicSize))
            {
                return 0;
            }
            double far = Vector2.Distance(quad[0], quad[1]);
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
            Check(WorldPlanetView.PcgReady && WorldPlanetView.Terrain.GroundMaterial.shader.name == "BinGames/Terrain/ContinuousGround",
                "从主菜单新建的正式战役已加载 PCG 地貌配置，普通视角使用 PCG 连续地表材质");
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
            // FG2-FW-05 起图鉴键、FG5-RND-01 起研发树键、FG5-RND-05 起情报键已接通，“尚未开放”提示改用快速存档键 Ctrl+F5（FG15-SYS-01 承接）。
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.QuickSave));
            Next(32, "按快速存档键 Ctrl+F5（FG15-SYS-01 承接，尚未开放）");
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
            Check(toast != null && shown.Contains(Localization.GameText.Get("input.action.quick_save.name")),
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
            // FG4-ECO-08（FGU-13 / 14）：统计面板默认在“生产”页（有产线时列出物品收支，没有时是空状态说明）；“规划助手”页选目标算出建筑需求、
            // “固定到顶栏作为目标”后资源顶栏出现这一格；再点“战斗”页签看反应伤害归因（下面原有的检查）。
            bool prodTab = stats != null && UI.Kit.StatsPanelUIToolkit.IsOpen && stats.CurrentTab == UI.Kit.StatsTab.Production
                           && (stats.VisibleRowCount > 0 ? stats.RowText(0).Length > 0 : stats.EmptyText.Length > 0)
                           && !Localization.GameText.ContainsMarker(stats.SectionText + stats.FooterText + stats.CountText + stats.RowText(0) + stats.EmptyText);
            string prodFirst = stats == null ? string.Empty : stats.VisibleRowCount > 0 ? stats.RowText(0) : stats.EmptyText;
            bool effTab = ClickUitk("[StatsPanelHost]", "StatsTabBuildings") && stats.CurrentTab == UI.Kit.StatsTab.Buildings
                          && !Localization.GameText.ContainsMarker(stats.SectionText + stats.RowText(0) + stats.EmptyText);
            bool bnTab = ClickUitk("[StatsPanelHost]", "StatsTabBottleneck") && stats.CurrentTab == UI.Kit.StatsTab.Bottlenecks
                         && !Localization.GameText.ContainsMarker(stats.SectionText + stats.RowText(0) + stats.EmptyText);
            bool flowTab = ClickUitk("[StatsPanelHost]", "StatsTabFlow") && stats.CurrentTab == UI.Kit.StatsTab.Flow && stats.VisibleRowCount >= 3
                           && !Localization.GameText.ContainsMarker(stats.SectionText + stats.RowText(0) + stats.RowText(1));
            // FG5-E2E-01：“研发”页（技术数据收支按来源分项、研究、熔合、情报、黑匣子五段，开局带来的技术数据对得上）。
            bool researchTab = ClickUitk("[StatsPanelHost]", "StatsTabResearch") && stats.CurrentTab == UI.Kit.StatsTab.Research && stats.VisibleRowCount >= 15
                               && !Localization.GameText.ContainsMarker(stats.SectionText + stats.RowText(0) + stats.RowText(1) + stats.RowText(2))
                               && stats.RowText(1).Contains(Campaign.Economy.ResearchService.StartTechData.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string researchRow = stats == null ? string.Empty : stats.RowText(1);
            bool planTab = ClickUitk("[StatsPanelHost]", "StatsTabPlanner") && stats.CurrentTab == UI.Kit.StatsTab.Planner && stats.LastPlan != null && stats.LastPlan.Ok;
            stats?.SetPlannerTarget("precision_part"); // 目标下拉框的选中回调（同一入口）：精密零件有多级配方
            planTab &= stats != null && stats.LastPlan.Ok && stats.LastPlan.Target.Id == "precision_part" && stats.LastPlan.Rows.Count >= 3 && stats.LastPlan.Raws.Count > 0
                       && !Localization.GameText.ContainsMarker(stats.SectionText + stats.RowText(0) + stats.RowText(1) + stats.RowText(2));
            string planTarget = stats?.LastPlan?.Target?.Id;
            bool planPinned = ClickUitk("[StatsPanelHost]", "PlannerPin") && planTarget != null
                              && Campaign.Economy.ResourcePins.Find(Campaign.CampaignSession.Current, planTarget)?.TargetPerMinute > 0f;
            UI.Kit.WorldBarHudUIToolkit wbar = UI.Kit.WorldBarHudUIToolkit.Instance;
            wbar?.RefreshResources(Campaign.CampaignSession.Current, force: true);
            bool barChip = wbar != null && planTarget != null && wbar.FindResourceChip(Campaign.Economy.ItemCatalog.NameOf(planTarget)) >= 0
                           && wbar.FindResourceChip(Localization.GameText.Format("topbar.exposure", 0, 0).Split(' ')[0]) >= 0;
            // FG4-ECO-08 审查修复（FGR-ECO-051“任意物品”）：顶栏“+ 固定物品”→ 统计面板“全部物品”→ 搜索一个不能生产的物品 → 行内“固定”→ 顶栏出现这一格。
            int addChip = wbar == null ? -1 : wbar.FindResourceChip(Localization.GameText.Get("topbar.add"));
            bool addClicked = addChip >= 0 && InvokeClickable(wbar.ResourceChip(addChip));
            bool allList = addClicked && stats != null && UI.Kit.StatsPanelUIToolkit.IsOpen && stats.CurrentTab == UI.Kit.StatsTab.Production && stats.ShowAllItems && stats.ItemSearchVisible
                           && stats.VisibleRowCount == Campaign.Economy.ItemCatalog.Items.Count + 1;
            List<Campaign.Economy.ItemDef> plannable = Campaign.Economy.ProductionPlanner.Targets();
            Campaign.Economy.ItemDef anyItem = Campaign.Economy.ItemCatalog.Items.FirstOrDefault(d => !plannable.Contains(d)
                && !Campaign.Economy.ResourcePins.IsPinned(Campaign.CampaignSession.Current, d.Id));
            if (stats != null && anyItem != null)
            {
                stats.ItemSearchField.value = anyItem.Name;
            }
            int anyRow = stats == null || anyItem == null ? -1 : stats.FindRow(anyItem.Name + "：");
            bool anyPinned = anyRow >= 0 && InvokeClickable(stats.RowButton(anyRow, 0)) && Campaign.Economy.ResourcePins.IsPinned(Campaign.CampaignSession.Current, anyItem.Id);
            wbar?.RefreshResources(Campaign.CampaignSession.Current, force: true);
            bool anyChip = anyPinned && wbar.FindResourceChip(anyItem.Name) >= 0;
            if (stats != null)
            {
                stats.ItemSearchField.value = string.Empty;
            }
            bool allBack = ClickUitk("[StatsPanelHost]", "StatsAllItems") && stats != null && !stats.ShowAllItems;
            Check(statsClicked && prodTab && effTab && bnTab && flowTab && researchTab && planTab && planPinned && barChip && allList && anyChip && allBack,
                $"FG4-ECO-08：暂停菜单点“统计”默认打开“生产”页（首行“{prodFirst}”），建筑效率 / 瓶颈 / 物流与能源页可切，“研发”页（FG5-E2E-01）写“{researchRow}”，规划助手算出 {stats?.LastPlan?.Rows.Count} 条配方的建筑需求，" +
                $"“固定到顶栏作为目标”后资源顶栏出现“{planTarget}”这一格；顶栏“+ 固定物品”打开“全部物品”，搜索并固定不能生产的“{anyItem?.Name}”后顶栏出现该格（共 {wbar?.ResourceChipCount} 格）" +
                $"（{allList}/{anyChip}/{allBack}）");
            bool combatTab = ClickUitk("[StatsPanelHost]", "StatsTabCombat") && stats != null && stats.CurrentTab == UI.Kit.StatsTab.Combat;
            bool statsOpen = combatTab && stats != null && UI.Kit.StatsPanelUIToolkit.IsOpen && stats.PanelVisible && stats.SectionText.Length > 0
                             && (stats.VisibleRowCount > 0 ? stats.RowText(0).Length > 0 : stats.EmptyText.Length > 0)
                             && !Localization.GameText.ContainsMarker(stats.SectionText + stats.FooterText + stats.CountText + stats.RowText(0) + stats.EmptyText);
            string statsFirst = stats == null ? string.Empty : stats.VisibleRowCount > 0 ? stats.RowText(0) : stats.EmptyText;
            bool statsFilter = ClickUitk("[StatsPanelHost]", "StatsFilterRaid") && stats != null && stats.CurrentFilter == Campaign.Combat.ReactionLogFilter.Raid
                               && ClickUitk("[StatsPanelHost]", "StatsFilterAll") && stats.CurrentFilter == Campaign.Combat.ReactionLogFilter.All;
            bool statsClosed = ClickUitk("[StatsPanelHost]", "StatsPanelClose") && !UI.Kit.StatsPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(statsClicked && statsOpen && statsFilter && statsClosed,
                $"暂停菜单点“统计”：统计面板打开（“{stats?.SectionText}”，{stats?.CountText}，首行“{statsFirst}”），“突袭 / 全部”筛选可切，点关闭回到暂停菜单");
            // FG4-ECO-01：暂停菜单“物资”→ 物资面板；“只看持有的”可切；点关闭回到暂停菜单。
            bool itemsClicked = ClickUitk("[PauseMenuHost]", "PauseItems");
            UI.Kit.ItemsPanelUIToolkit ip = UI.Kit.ItemsPanelUIToolkit.Instance;
            int allTiles = ip?.VisibleTileCount ?? 0;
            bool itemsOpen = ip != null && UI.Kit.ItemsPanelUIToolkit.IsOpen && ip.PanelVisible && allTiles == Campaign.Economy.ItemCatalog.Items.Count;
            bool heldToggle = ClickUitk("[ItemsPanelHost]", "ItemsPanelHeld") && ip != null && ip.HeldOnly && ip.VisibleTileCount < allTiles && ip.VisibleTileCount > 0
                              && ClickUitk("[ItemsPanelHost]", "ItemsPanelHeld") && !ip.HeldOnly;
            bool itemsClosed = ClickUitk("[ItemsPanelHost]", "ItemsPanelClose") && !UI.Kit.ItemsPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(itemsClicked && itemsOpen && heldToggle && itemsClosed,
                $"暂停菜单点“物资”：物资面板打开（{ip?.CountText}，{allTiles} 格），“只看持有的”可切，点关闭回到暂停菜单");
            // FG4-ECO-06（FGU-15）：暂停菜单“常驻规则”→ 规则面板（新战役默认一条“远征卸货”）；从常用预设新建（下拉框选中即生效）、点“编辑”、日志页签、
            // 行内删除先确认、点关闭回到暂停菜单。
            bool rulesClicked = ClickUitk("[PauseMenuHost]", "PauseRules");
            UI.Kit.RulesPanelUIToolkit rp = UI.Kit.RulesPanelUIToolkit.Instance;
            rp?.Refresh();
            bool rulesOpen = rp != null && UI.Kit.RulesPanelUIToolkit.IsOpen && rp.PanelVisible && rp.VisibleRowCount >= 1 && rp.RowText(0, "RrId").Contains("R")
                             && !Localization.GameText.ContainsMarker(rp.CountText + rp.RowText(0, "RrWhen") + rp.RowText(0, "RrThen"));
            int rowsBefore = rp?.VisibleRowCount ?? 0;
            int presetIndex = rp?.NewPresetField?.choices?.FindIndex(c => c.Contains("零件保底")) ?? -1;
            bool presetMade = rp != null && UI.Kit.RulesPanelUIToolkit.PickForTests(rp.NewPresetField, presetIndex) && rp.VisibleRowCount == rowsBefore + 1
                              && rp.SelectedSerial != 0 && rp.EditorRowVisible("RulesRowFactory") && rp.EditTitleText.Contains("R");
            int newRow = rp == null ? -1 : Enumerable.Range(0, rp.VisibleRowCount).FirstOrDefault(i => rp.RowSerial(i) == rp.SelectedSerial);
            bool logTab = ClickUitk("[RulesPanelHost]", "RulesTabLog") && rp != null && rp.ShowingLog && ClickUitk("[RulesPanelHost]", "RulesTabRules") && !rp.ShowingLog;
            bool deleted = false;
            if (rp != null && newRow >= 0)
            {
                rp.AskDelete(newRow);
                deleted = UiConfirmDialog.IsOpen;
                UiConfirmDialog.Confirm();
                deleted &= rp.VisibleRowCount == rowsBefore;
            }
            bool rulesClosed = ClickUitk("[RulesPanelHost]", "RulesPanelClose") && !UI.Kit.RulesPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(rulesClicked && rulesOpen && presetMade && logTab && deleted && rulesClosed,
                $"暂停菜单点“常驻规则”：规则面板打开（{rp?.CountText}，首行“{rp?.RowText(0, "RrId")}”），从预设新建“零件保底”并进入编辑、日志页签可切、删除先确认、点关闭回到暂停菜单" +
                $"（{rulesClicked}/{rulesOpen}/{presetMade}/{logTab}/{deleted}/{rulesClosed}）");
            // FG4-ECO-07（FGU-16 / 17）：暂停菜单“机器名册”→ 名册（每台机器一行）；行内岗位下拉框改为闲置再改回劳动（选中即生效）；点“详情”→ 改名（空名给原因、
            // 合法名字接入 HUD 标识同步）→ 恢复默认名 → 返回列表 → 点关闭回到暂停菜单。
            bool rosterClicked = ClickUitk("[PauseMenuHost]", "PauseRoster");
            UI.Kit.RosterPanelUIToolkit rop = UI.Kit.RosterPanelUIToolkit.Instance;
            rop?.Refresh();
            int machines = Campaign.MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive);
            bool rosterOpen = rop != null && UI.Kit.RosterPanelUIToolkit.IsOpen && rop.PanelVisible && rop.VisibleRowCount == machines && machines >= 1
                              && !Localization.GameText.ContainsMarker(rop.CountText + rop.RowText(0, "RoName") + rop.RowText(0, "RoInfo") + rop.RowText(0, "RoStatus"));
            // 挑一台手上没活的机器改岗位（不打断后面步骤要用的施工）；都在忙时用第一行。
            int pickRow = 0;
            for (int i = 0; rop != null && i < rop.VisibleRowCount; i++)
            {
                if (rop.RowText(i, "RoStatus") == Localization.GameText.Get("roster.status.waiting"))
                {
                    pickRow = i;
                    break;
                }
            }
            int firstId = rop?.RowLogicId(pickRow) ?? 0;
            UnityEngine.UIElements.DropdownField roleField = rop?.RowRoleField(pickRow);
            int idleAt = roleField?.choices?.FindIndex(c => c.Contains("闲置")) ?? -1;
            int laborAt = roleField?.choices?.FindIndex(c => c.Contains("劳动")) ?? -1;
            bool roleIdle = rop != null && UI.Kit.RosterPanelUIToolkit.PickForTests(roleField, idleAt)
                            && Campaign.MachineRegistry.TryGetRecord(firstId, out Campaign.MachineRecord r0) && r0.Role == Campaign.MachineRole.Idle;
            roleField = rop?.RowRoleField(rop.RowIndexOf(firstId));
            bool roleBack = rop != null && UI.Kit.RosterPanelUIToolkit.PickForTests(roleField, laborAt)
                            && Campaign.MachineRegistry.TryGetRecord(firstId, out Campaign.MachineRecord r1) && r1.Role == Campaign.MachineRole.Labor;
            bool detailOpen = ClickUitk("[RosterPanelHost]", "RoDetail") && rop != null && rop.ShowingDetail && rop.DetailInfoText.Contains("接入记录");
            int detailId = rop?.DetailLogicId ?? 0;
            if (rop != null)
            {
                rop.NameField.value = "  ";
            }
            bool emptyName = ClickUitk("[RosterPanelHost]", "RosterRename") && rop != null && rop.DetailMessageText.Contains("不能为空");
            if (rop != null)
            {
                rop.NameField.value = "冒烟一号";
            }
            bool renamed = ClickUitk("[RosterPanelHost]", "RosterRename") && Campaign.Signal.SignalPresence.MachineLabel(detailId).StartsWith("冒烟一号", StringComparison.Ordinal)
                           && rop != null && rop.DetailTitleText.Contains("冒烟一号");
            bool nameReset = ClickUitk("[RosterPanelHost]", "RosterResetName") && !Campaign.Signal.SignalPresence.MachineLabel(detailId).Contains("冒烟一号");
            bool backToList = ClickUitk("[RosterPanelHost]", "RosterDetailBack") && rop != null && !rop.ShowingDetail;
            bool rosterClosed = ClickUitk("[RosterPanelHost]", "RosterPanelClose") && !UI.Kit.RosterPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            Check(rosterClicked && rosterOpen && roleIdle && roleBack && detailOpen && emptyName && renamed && nameReset && backToList && rosterClosed,
                $"暂停菜单点“机器名册”：名册打开（{rop?.CountText}，首行“{rop?.RowText(0, "RoName")}”），行内改岗位闲置 / 劳动、详情页空名字给原因、改名后接入 HUD 标识同步、恢复默认名、返回列表、点关闭回到暂停菜单" +
                $"（{rosterClicked}/{rosterOpen}/{roleIdle}/{roleBack}/{detailOpen}/{emptyName}/{renamed}/{nameReset}/{backToList}/{rosterClosed}）");
            // FG4-ECO-09（FGU-30；FGR-ECO-060“随时可以查看最近 3 份报告”“自动打开可以在设置里关闭”）：暂停菜单“离家报告”→ 报告面板（有报告时显示最新一份，没有时写明去处）；
            // 暂停菜单里“远征回来时自动打开离家报告”开关关掉再打开（设置立即生效）；点关闭回到暂停菜单。
            bool awayClicked = ClickUitk("[PauseMenuHost]", "PauseAwayReport");
            UI.Kit.AwayReportPanelUIToolkit arp = UI.Kit.AwayReportPanelUIToolkit.Instance;
            arp?.Refresh();
            bool awayOpen = arp != null && UI.Kit.AwayReportPanelUIToolkit.IsOpen && arp.PanelVisible
                            && (arp.VisibleRowCount > 0 || arp.EmptyText.Contains("还没有离家报告"))
                            && !Localization.GameText.ContainsMarker(arp.SummaryText + arp.EmptyText + (arp.VisibleRowCount > 0 ? arp.RowText(0) : string.Empty));
            bool awayClosed = ClickUitk("[AwayReportHost]", "AwayReportClose") && !UI.Kit.AwayReportPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen;
            UnityEngine.UIElements.Toggle autoToggle = pm?.AwayAutoOpenToggle;
            bool toggled = false;
            if (autoToggle != null)
            {
                autoToggle.value = false;
                bool off = !Settings.GameSettings.AwayReportAutoOpen;
                autoToggle.value = true;
                toggled = off && Settings.GameSettings.AwayReportAutoOpen;
            }
            Check(awayClicked && awayOpen && awayClosed && toggled,
                $"暂停菜单点“离家报告”：报告面板打开（{(arp == null ? "无" : arp.VisibleRowCount > 0 ? $"第 {arp.ShownSerial} 份，{arp.VisibleRowCount} 行" : arp.EmptyText)}），点关闭回到暂停菜单；" +
                $"“远征回来时自动打开离家报告”开关关 / 开立即生效（{awayClicked}/{awayOpen}/{awayClosed}/{toggled}）");
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
                $"取消规划：虚影方块消失、占格释放、库存不变（FG3-LOG-02：放下虚影不扣料，还没取料；{state?.Scrap}）");
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
            // FG3-LOG-02（DEBT-FG3LOG01-01）：拖出来的是 6 格传送带虚影（规划 + 施工单），不扣材料；暂停中不施工。
            bool all = Enumerable.Range(0, 6).All(i => Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new Campaign.Grid.GridCell(x + i, y), out _, out _)
                                                       && !Campaign.Logistics.BeltNetworkService.Kernel.HasCell(x + i, y));
            string seen = SessionState.GetString(K + "BeltDragInfo", string.Empty);
            Transform ghostTiles = FindNamed("[PlannedBelts]");
            int shownTiles = ghostTiles != null ? ghostTiles.Cast<Transform>().Count(t => t.gameObject.activeSelf) : 0;
            Check(all && state.Scrap == SessionState.GetInt(K + "BeltScrap", -1) && seen.Contains("长度") && seen.Contains("成本") && shownTiles >= 6,
                $"真实鼠标拖拽放下 6 格传送带虚影（不扣材料，库存 {state.Scrap}；画面上 {shownTiles} 块半透明虚影条）；拖的时候 HUD 显示“{seen}”");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.ConstructionQueue));
            Next(113, "FG3-LOG-02：按施工队列键（默认 Alt+B）打开施工队列");
        }

        // ── FG3-LOG-02：施工队列（真 UXML、真实按键 / 点击）、“优先建造这一片”（真实按键 + 鼠标拖框）─────────────────────────

        private static void StepConstructionQueueOpened(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            ConstructionQueuePanelUIToolkit panel = ConstructionQueuePanelUIToolkit.Instance;
            bool open = ConstructionQueuePanelUIToolkit.IsOpen && panel != null && panel.PanelVisible;
            bool row = panel != null && panel.VisibleRowCount >= 1 && panel.RowName(0).Contains("传送带") && !string.IsNullOrEmpty(panel.RowStatus(0));
            Check(open && row, $"施工队列打开：{panel?.VisibleRowCount} 行，第一行“{panel?.RowName(0)}：{panel?.RowStatus(0)}”（{panel?.RowPriority(0)}）");
            CheckNoTextMarkers("施工队列");
            bool raised = ClickUitk("[ConstructionQueueHost]", "CqUp");
            panel?.Refresh();
            Check(raised && panel != null && panel.RowPriority(0).Contains("高"), $"点行内“提高”：优先级变成“{panel?.RowPriority(0)}”");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(114, "Esc 关闭施工队列（建造模式仍开着）");
        }

        private static void StepConstructionQueueClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!ConstructionQueuePanelUIToolkit.IsOpen && mode != null && mode.IsOpen,
                $"Esc 只关掉施工队列，建造模式还开着（队列 {ConstructionQueuePanelUIToolkit.IsOpen}，建造 {mode?.IsOpen}，上下文 {InputRouter.ActiveContext}，Esc 栈顶 {UiEscapeStack.Top?.GetType().Name}，模态 {string.Join(",", InputRouter.ModalOwnerList.Select(owner => owner.GetType().Name))}）");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.ConstructionQueue));
            Next(117, "再按施工队列键（默认 Alt+B）重新打开施工队列");
        }

        private static void StepConstructionQueueReopened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(ConstructionQueuePanelUIToolkit.IsOpen, "施工队列键重新打开施工队列");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.ConstructionQueue));
            Next(118, "面板开着时再按一次同一个键（默认 Alt+B）关闭施工队列");
        }

        private static void StepConstructionQueueToggledShut(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!ConstructionQueuePanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "施工队列开着时再按同一个键关闭（模态面板走全局键），建造模式还开着");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.PrioritizeArea));
            Next(115, "按“优先建造这一片”键（默认 P）并拖框框住传送带虚影");
        }

        private static void StepPrioritizeArea(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            int x = SessionState.GetInt(K + "BeltX", 0);
            int y = SessionState.GetInt(K + "BeltY", 0);
            if (inStep < 0.4)
            {
                return;
            }
            if (SessionState.GetInt(K + "PrioDragged", 0) == 0)
            {
                Check(mode != null && mode.PrioritizeMode, "进入“优先建造这一片”模式");
                SessionState.SetInt(K + "PrioDragged", 1);
                DragWorld(new Vector3(x, 0f, y - 1), new Vector3(x + 5, 0f, y), 0);
                return;
            }
            if (inStep < 1.3)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new Campaign.Grid.GridCell(x, y), out PlannedBeltRecord plan, out _);
            WorkOrderRecord order = plan != null ? Campaign.Regions.HomeValleyWorkOrders.FindActiveBuild(state, Campaign.Regions.HomeValleyConstruction.BeltPlanPrefix + plan.PlanId) : null;
            Check(order != null && order.Priority == Campaign.Regions.HomeValleyConstruction.PriorityMax && mode != null && mode.StatusText.Contains("最高优先级"),
                $"拖框框住传送带虚影：它的施工改成最高优先级；状态行“{mode?.StatusText}”");
            RightClickWorld(new Vector3(x + 2, 0f, y + 3));
            Next(116, "右键退出“优先建造这一片”");
        }

        private static void StepPrioritizeExited(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.IsOpen && !mode.PrioritizeMode, "右键退出“优先建造这一片”，建造模式仍开着");
            CampaignState state = CampaignSession.Current;
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.DemolishMode));
            SessionState.SetInt(K + "BeltScrap", state.Scrap + GroundScrap(state));
            Next(104, "按拆除模式键，在空地上按住左键把刚放的传送带虚影框起来");
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
            bool gone = Enumerable.Range(0, 6).All(i => !Campaign.Logistics.BeltNetworkService.Kernel.HasCell(x + i, y)
                                                        && !Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new Campaign.Grid.GridCell(x + i, y), out _, out _));
            Check(gone && state.Scrap + GroundScrap(state) == SessionState.GetInt(K + "BeltScrap", -1) && !UiConfirmDialog.IsOpen && (state.Grid.PlannedBelts?.Length ?? 0) == 0
                  && mode != null && mode.LastResult.Outcome == Campaign.Grid.GridOpResult.Kind.BatchDemolished && mode.StatusText.Contains("传送带 / 管线 6 格"), // FG3-LOG-05：框选拆除也拆管线层，结果行写“传送带 / 管线”
                $"框选拆除：6 格传送带虚影取消规划（没取过料，库存 {state.Scrap} 不变），少量且不含关键建筑不弹确认；状态行“{mode?.StatusText}”");
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
            CheckPerf(streamer != null,
                $"平移期间流式加载主线程每帧最多 {streamer?.MaxTickMs:F3} ms（真实 Play，影子工程 batchmode）",
                PerfGate.Lt(streamer?.MaxTickMs ?? double.NaN, 16.0, "流式加载每帧最多 ms"));
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
            // FG4-ECO-05（FGR-ECO-013；RTS 右键情境命令）：修好的发电机又受了伤（正式伤害来源在 FG6-DEF-05，这里经同一个入口 BuildingOps.ApplyDamage 打一下）——
            // 机器仍选中，右键这座受损（运转中、耐久未满）的建筑 = 这台机器带维修件上门维修（与面板“维修”同一张单）。维修件由测试放进仓库。
            Campaign.Economy.ItemCatalog.TryGet(Campaign.Economy.BuildingOps.RepairKitId, out Campaign.Economy.ItemDef kit);
            Campaign.Economy.HomeInventory.Add(state, kit, 6, clampToSpace: false);
            Campaign.Economy.BuildingOps.ApplyDamage(state, generator.BuildingId, Campaign.Economy.BuildingOps.MaxDurability(generator.BuildingTypeId) * 0.4f);
            Check(Campaign.Economy.BuildingOps.IsWorn(generator) && generator.ConstructionState == BuildingConstructionState.Operational,
                $"发电机受损（耐久 {Campaign.Economy.BuildingOps.Durability(generator):0}）但照常运转");
            Transform genView = FindNamed("Building_" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            RightClickWorld(genView != null ? genView.position : new Vector3(generator.Position.x, 0f, generator.Position.y));
            Next(38, "机器选中时鼠标右键点受损（运转中）的发电机（情境命令：带维修件上门维修）");
        }

        private static void StepWornRepairOrdered(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            string genId = Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator;
            WorkOrderRecord order = state != null ? Campaign.Regions.HomeValleyWorkOrders.FindActiveRepair(state, genId) : null;
            int worker = SessionState.GetInt(K + "Worker", -1);
            Check(order != null && order.AssignedMachineLogicId == worker && order.ReservedItemAmount > 0
                  && order.ReservedItemId == Campaign.Economy.BuildingOps.RepairKitId,
                $"右键受损建筑 → 选中的机器 #{worker} 接下维修单（单子 {order?.State}、机器 #{order?.AssignedMachineLogicId}、预留维修件 {order?.ReservedItemAmount}）");
            if (order == null)
            {
                Finish("右键受损建筑没有派出维修单");
                return;
            }
            Next(39, "等机器带维修件修好发电机");
        }

        private static void StepWornRepairDone(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord generator = state?.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == Campaign.Regions.HomeValleyLayout.RegionId && b.BuildingTypeId == Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator);
            if (generator == null || Campaign.Economy.BuildingOps.IsWorn(generator))
            {
                if (inStep > 120)
                {
                    Finish("120 秒内受损的发电机没修好");
                }
                return;
            }
            Write($"  - 受损发电机修满（下令后 {inStep:F0} 秒）");
            Check(generator.ConstructionState == BuildingConstructionState.Operational, "右键维修完成：发电机耐久回满、照常运转");
            CheckNoTextMarkers("右键维修后");
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
            // FG4-ECO-03：装配站按蓝图的材料造机器——面板写每张蓝图要的材料（装配站缓存 / 仓库各有多少、全部代付多少废料），“缺材料时用废料代付”开关勾选即生效。
            string mats = LabelText("[HomeValleyFactoryHost]", "MaterialsLabel");
            string noteOn = LabelText("[HomeValleyFactoryHost]", "SubstituteNote");
            GameObject fhost = GameObject.Find("[HomeValleyFactoryHost]");
            Toggle sub = fhost != null ? fhost.GetComponent<UIDocument>()?.rootVisualElement?.Q<Toggle>("SubstituteToggle") : null;
            bool flipped = false;
            string noteOff = string.Empty;
            if (sub != null)
            {
                sub.value = !sub.value;
                noteOff = LabelText("[HomeValleyFactoryHost]", "SubstituteNote");
                flipped = !Campaign.Economy.AssemblyMaterials.ScrapSubstitute(CampaignSession.Current) && noteOff.Contains("已关");
                sub.value = !sub.value;
            }
            Check(!open || (mats.Contains("的材料") && mats.Contains("结构材") && mats.Contains("全部用废料代付") && noteOn.Contains("已开") && flipped
                            && Campaign.Economy.AssemblyMaterials.ScrapSubstitute(CampaignSession.Current)),
                $"FG4-ECO-03 装配站面板写材料清单（“{mats.Split('\n')[0]}”）；代付开关默认开（“{noteOn}”），点一下关（“{noteOff}”）再点回来");
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
                Check(Campaign.Content.FirmwareCatalog.BaseCount == Campaign.Content.FirmwareCatalog.ExpectedCount && lockedFw > 0
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
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.QuickSave));
            Next(62, "在命名框里按快速存档键 Ctrl+F5（尚未开放的动作）");
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
            // FG5-RND-02（FGU-23）：解析台 2.0——状态行（空闲时“解析台空闲：没有待解析的物品”）、待解析的敌方物品（与服务层清单同一份）、队列 n/8、残骸栏、资料栏。
            var heldExpect = new System.Collections.Generic.List<Campaign.Regions.HomeValleyAnalysis.HeldEntry>();
            Campaign.Regions.HomeValleyAnalysis.CollectHeld(st, heldExpect);
            string statusExpect = Campaign.Regions.HomeValleyAnalysis.StatusLine(st);
            Check(open && panel.StatusText == statusExpect && statusExpect.Length > 0 && panel.HeldChoices.Count == heldExpect.Count
                  && panel.QueueTitleText.Contains("/" + Campaign.Economy.AnalysisCatalog.QueueCapacity) && panel.WreckText.Length > 0 && panel.LoreText.Length > 0
                  && LabelText("[HomeValleyAnalysisHost]", "Title") == Localization.GameText.Get("analysis.panel.title"),
                $"解析台 2.0 面板：状态“{panel?.StatusText}”、待解析 {panel?.HeldChoices.Count} 行（= 服务层 {heldExpect.Count} 行）、“{panel?.QueueTitleText}”、残骸栏“{panel?.WreckText}”");
            CheckNoTextMarkers("解析台 2.0");
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
            BeginAwayTrip();
        }

        // ── FG4-ECO-09：离家报告的正式路径（派遣 → 离家期间家园照常运转 → 正式撤离事务 → 回到家园自动打开 → 点一条定位）──

        /// <summary>按出征同样的调用顺序派遣（测试捷径同第 7 步），之后用正式撤离事务回来。</summary>
        private static void BeginAwayTrip()
        {
            int[] roster = HomeMachines();
            UnlockLikeDeparture(Campaign.Regions.FracturedCityRegion.Find(CampaignSession.Current),
                Campaign.Regions.ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            GameRoot.StartFracturedCity(roster);
            Next(343, $"测试捷径：再派一次远征（{roster.Length} 台机器，家园照常运转），等它在外面待一会儿");
        }

        private static void StepAwayTripOut(double inStep)
        {
            if (inStep < 4)
            {
                return;
            }
            bool away = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded && Campaign.Economy.AwayReportService.IsOpen(CampaignSession.Current);
            Check(away, "远征在外：离家报告在记（进行中）");
            Campaign.Regions.ExpeditionReturnService.ReturnResult evac = Campaign.Regions.ExpeditionReturnService.TryConfirmEvacuation();
            Check(evac.Success, $"正式撤离事务：远征队回到家园（{evac.FailureReason ?? "成功"}）");
            Next(344, "正式撤离事务：回到归还谷地，等离家报告自动打开");
        }

        private static void StepAwayTripBack(double inStep)
        {
            if (inStep < 2)
            {
                return;
            }
            // FG4-ECO-09（FGR-ECO-060“远征回来时自动打开”；FG04 第 4 节“离家报告里的每一条都可以点击定位”）：自动打开显示这次的报告；
            // 点一条可定位 / 可打开面板的条目（真实按钮回调）→ 面板收起；再把打开的面板收起，不挡后面的步骤。
            UI.Kit.AwayReportPanelUIToolkit arp = UI.Kit.AwayReportPanelUIToolkit.Instance;
            Campaign.AwayReportRecord latest = Campaign.Economy.AwayReportService.Recent(CampaignSession.Current).FirstOrDefault();
            arp?.Refresh();
            bool autoOpen = arp != null && UI.Kit.AwayReportPanelUIToolkit.IsOpen && latest != null && arp.ShownSerial == latest.Serial && arp.VisibleRowCount > 0
                            && arp.SummaryText.Contains("撤离回家") && !Localization.GameText.ContainsMarker(arp.SummaryText + arp.RowText(0));
            int row = -1;
            for (int i = 0; arp != null && i < arp.VisibleRowCount; i++)
            {
                Campaign.Economy.AwayLine l = arp.Line(i);
                if (l.IsEntry && (l.Action == Campaign.Economy.AwayLineAction.Locate || l.Action == Campaign.Economy.AwayLineAction.Building
                                  || l.Action == Campaign.Economy.AwayLineAction.StatsItem || l.Action == Campaign.Economy.AwayLineAction.PowerPanel))
                {
                    row = i;
                    break;
                }
            }
            string rowText = row >= 0 ? arp.RowText(row) : "（没有可点的条目）";
            bool clicked = row >= 0 && InvokeClickable(arp.RowButton(row)) && !UI.Kit.AwayReportPanelUIToolkit.IsOpen && arp.LastClicked != null;
            UI.Kit.StatsPanelUIToolkit.Close();
            UI.Kit.ProductionPanelUIToolkit.Close();
            UI.Kit.PowerPanelUIToolkit.Close();
            UI.Kit.RosterPanelUIToolkit.Close();
            UI.Kit.RulesPanelUIToolkit.Close();
            UI.Kit.AwayReportPanelUIToolkit.Close();
            Check(autoOpen && clicked,
                $"正式撤离后离家报告自动打开：第 {arp?.ShownSerial} 份（{arp?.VisibleRowCount} 行，“{arp?.SummaryText}”）；点“{rowText}”→ 面板收起并{arp?.LastClicked?.Action}（{autoOpen}/{clicked}）");
            // FG4-ECO-10（FGR-ECO-070 / FGT-ECO-005）：家园机器与废料清空后，归还核心在真实世界步里应急打印搬运机、开始应急产废料（测试捷径：直接清空，判定与恢复走正式路径）。
            CampaignState cs = CampaignSession.Current;
            _softlockPrintsBefore = Campaign.Economy.SoftlockService.StateOf(cs)?.PrintCount ?? 0;
            Campaign.Economy.SoftlockService.StateOf(cs).LastPrintDay = 0; // 让今天的应急打印可用（冒烟前面的步骤里机器一直够，本来就没打印过）
            foreach (int id in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).Select(m => m.LogicId).ToArray())
            {
                MachineRegistry.ApplyDamage(id, 99999f);
            }
            cs.Scrap = 0;
            // FG4-ECO-11 冒烟修复：这一段测“真实世界步里”的应急打印，世界必须在运行。前面的步骤（离家报告、电力面板）之后若世界处于暂停
            // （例如某条默认自动暂停的通知），记下是谁暂停的、写进本段结论，再恢复运行——不改判定口径，只保证世界步在走。
            string pauseNote = string.Empty;
            if (GameClock.Paused)
            {
                Notifications.NotificationEntry auto = Notifications.NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "auto_paused");
                pauseNote = "清空前世界处于暂停（" + (auto != null ? auto.Text : "没有自动暂停通知") + "；最近通知：" +
                            string.Join("、", Notifications.NotificationCenter.History.Reverse().Take(3).Select(n => n.Type?.Id ?? "?")) + "），已恢复运行";
                GameClock.SetPaused(false);
            }
            SessionState.SetString(K + "SoftlockPauseNote", pauseNote);
            Next(345, "测试捷径：清空全部机器与废料，等归还核心应急打印与应急产废料（FG4-ECO-10）");
        }

        private static int _softlockPrintsBefore;

        private static void StepSoftlockPrint(double inStep)
        {
            CampaignState cs = CampaignSession.Current;
            Campaign.SoftlockState st = Campaign.Economy.SoftlockService.StateOf(cs);
            bool printed = st != null && st.PrintCount > _softlockPrintsBefore && MachineRegistry.TryGetRecord(st.LastPrintLogicId, out MachineRecord m) && m.IsAlive
                           && m.ChassisId == Campaign.Regions.HomeValleyLayout.Erc002ChassisId;
            bool scrapping = st != null && st.CoreScrapActive;
            if (!(printed && scrapping))
            {
                if (inStep > 12)
                {
                    Check(false, $"清空机器与废料后 12 秒内没有应急打印 / 应急产废料（打印 {st?.PrintCount}，产废料 {st?.CoreScrapActive}；" +
                                 $"游戏日 {GameClock.DayOf(GameClock.GameSeconds)} / 上次打印日 {st?.LastPrintDay}，能施工的机器 {Campaign.Economy.SoftlockService.CountHomeMachines()} 台，" +
                                 $"废料 {cs.Scrap}，回收站在工作 {Campaign.Economy.ProductionService.AnyRecyclerWorking(cs)}，世界暂停 {GameRoot.IsWorldPaused}，核心被毁 {Campaign.Regions.HomeValleySoftlockGuard.IsCoreDestroyed(cs)}）");
                    Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(cs);
                    Next(80, "测试捷径：核心被毁（HomeValleySoftlockGuard.DebugDestroyCore），等失败页出现");
                }
                return;
            }
            if (inStep < 1)
            {
                return;
            }
            BuildingRecord core = cs.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == Campaign.Regions.HomeValleyLayout.BuildingTypeCore);
            string coreLine = core != null ? Campaign.Economy.BuildingStatusService.Line(cs, core) : string.Empty;
            bool notified = Notifications.NotificationCenter.History.Any(e => e.Type?.Id == "emergency_rescue")
                            && Notifications.NotificationCenter.History.Any(e => e.Type?.Id == "core_emergency_scrap");
            string pauseNote = SessionState.GetString(K + "SoftlockPauseNote", string.Empty);
            Check(printed && scrapping && notified && coreLine.Contains("应急产废料") && !Localization.GameText.ContainsMarker(coreLine),
                $"清空机器与废料 → 归还核心打印了 {MachineNaming.Short(st.LastPrintLogicId)}、开始应急产废料，两条通知进历史；核心状态“{coreLine}”" +
                (pauseNote.Length > 0 ? "（" + pauseNote + "）" : string.Empty));
            Campaign.Regions.HomeValleySoftlockGuard.DebugDestroyCore(cs);
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
            BeginAwayTrip();
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
            // FG4-ECO-07（FGR-ECO-042）：远征准备提示出发后家园劳动力的变化（没勾选机器时“劳动力不变”）。
            string labor = LabelText("[HomeValleyExpeditionPrepHost]", "SummaryLabel");
            Check(labor.Contains("劳动力") && !Localization.GameText.ContainsMarker(labor), $"远征准备面板写出发后家园劳动力：“{labor.Replace('\n', ' ')}”");
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
            ClickUitk("[StatsPanelHost]", "StatsTabCombat"); // FG4-ECO-08：战斗归因在“战斗”页签（真实点击）
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
            CheckPerf(maxPlaceholder > 0 && now == 0,
                $"远距离飞跃（约 {Vector2.Distance(Campaign.Regions.HomeValleyLayout.Core.Position, CameraFocus()):F0} 格）：新露出的区块先显示“生成中”占位（峰值 {maxPlaceholder} 块），" +
                $"{inStep:F1} 秒内补齐（剩 {now} 块）；主线程单帧峰值 {maxFrameMs:F0} ms（上限 250 ms）",
                PerfGate.Lt(maxFrameMs, 250.0, "飞跃主线程单帧峰值 ms"));
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
            // FG3-LOG-03：镜头飞到测试捷径铺的直线（画面中央，不被建造栏挡住）。
            int ox = SessionState.GetInt(K + "BeltX", 0);
            int oy = SessionState.GetInt(K + "BeltY", 0);
            WorldView.FlyTo(Campaign.Regions.HomeValleyLayout.RegionId, new Vector2(ox + 4, oy + 2));
            Next(249, "FG3-LOG-03：镜头飞到传送带");
        }

        private static void StepBeltFocused(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            // 鼠标停在直线的第 4 格，按建造菜单键打开建造模式——建造栏状态行写这格传送带的悬停读数。
            int ox = SessionState.GetInt(K + "BeltX", 0);
            int oy = SessionState.GetInt(K + "BeltY", 0);
            HoverWorld(new Vector3(ox + 3, 0f, oy));
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
            Next(250, "鼠标停在传送带上，按建造菜单键");
        }

        // ── FG3-LOG-03：传送带正式化与端口（悬停读数、清带工具、端口面板、被摧毁的传送带重建）─────────────────────────

        private static void StepBeltHoverInBuild(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string status = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
            int ox = SessionState.GetInt(K + "BeltX", 0);
            int oy = SessionState.GetInt(K + "BeltY", 0);
            Check(mode != null && mode.IsOpen && mode.HasHover && mode.HoverCell == new GridCell(ox + 3, oy)
                  && status.Contains("传送带 T1") && status.Contains("满载速度") && status.Contains("实测吞吐") && status.Contains("状态") && !Localization.GameText.ContainsMarker(status),
                $"建造模式里鼠标停在已建成的传送带上，建造栏写悬停读数（FGR-LOG-081）：{status.Replace("\n", " / ")}");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.ClearBeltMode));
            Next(251, "按清带键（默认 J）进入清带模式");
        }

        private static GridCell SmokeRingCell() =>
            new GridCell(SessionState.GetInt(K + "BeltX", 0) + 6, SessionState.GetInt(K + "BeltY", 0) + 2);

        private static int SmokeRingItems()
        {
            BeltKernel k = BeltNetworkService.Kernel;
            int n = 0;
            int net = k.NetworkOf(SmokeRingCell().X, SmokeRingCell().Y);
            var cells = new List<Unity.Mathematics.int2>();
            k.CollectNetworkCells(net, cells);
            foreach (Unity.Mathematics.int2 c in cells)
            {
                k.TryGetCellInfo(c.x, c.y, out BeltCellInfo info);
                n += info.Count;
            }
            return n;
        }

        private static void StepBeltClearModeOn(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string modeText = BuildModeHudUIToolkit.Instance?.ModeText ?? string.Empty;
            Check(mode != null && mode.ClearMode && modeText.Contains("清带"), $"清带模式：建造栏模式“{modeText}”，提示“{mode?.StatusText}”");
            SessionState.SetInt(K + "RingItems", SmokeRingItems());
            GridCell ring = SmokeRingCell();
            ClickWorld(new Vector3(ring.X, 0f, ring.Y));
            Next(252, "左键点测试环上的一格（整个环：12 件家园仓库存不了的物品）");
        }

        private static void StepBeltClearAsked(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            int ringItems = SessionState.GetInt(K + "RingItems", -1);
            Check(UiConfirmDialog.IsOpen && mode != null && mode.PendingClearConfirm && UiConfirmDialog.Current.Title.Contains(ringItems.ToString())
                  && UiConfirmDialog.Current.Lines.Any(l => l.Contains("还存不了")),
                $"清带时仓库放不下（FGR-LOG-026）：先弹确认框“{UiConfirmDialog.Current?.Title}”，写明原因");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(253, "Esc 取消");
        }

        private static void StepBeltClearCancelled(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            int ringItems = SessionState.GetInt(K + "RingItems", -1);
            Check(!UiConfirmDialog.IsOpen && SmokeRingItems() == ringItems && mode != null && mode.StatusText.Contains("已取消清带"),
                $"取消：环上 {SmokeRingItems()} 件原样（状态行“{mode?.StatusText}”）");
            SessionState.SetInt(K + "BeltDiscarded", (int)CampaignSession.Current.Belts.Discarded);
            GridCell ring = SmokeRingCell();
            ClickWorld(new Vector3(ring.X, 0f, ring.Y));
            Next(254, "再点一次环");
        }

        private static void StepBeltClearAskedAgain(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Check(UiConfirmDialog.IsOpen, "确认框再次询问");
            SessionState.SetInt(K + "RingItems", SmokeRingItems());
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.UiConfirm));
            Next(255, "按确认键（默认回车）丢弃放不下的");
        }

        private static void StepBeltClearConfirmed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            long discarded = CampaignSession.Current.Belts.Discarded - SessionState.GetInt(K + "BeltDiscarded", 0);
            int asked = SessionState.GetInt(K + "RingItems", -1);
            Check(!UiConfirmDialog.IsOpen && SmokeRingItems() == 0 && discarded == asked && mode != null && mode.StatusText.Contains("已丢弃")
                  && BeltNetworkService.Kernel.Ledger.Balanced,
                $"确认：环清空，丢弃 {discarded} 件（状态行“{mode?.StatusText}”），传送带账本平衡");
            GridCell ring = SmokeRingCell();
            RightClickWorld(new Vector3(ring.X, 0f, ring.Y));
            Next(256, "右键退出清带模式");
        }

        private static void StepBeltClearModeOff(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && !mode.ClearMode && mode.IsOpen, "右键退出清带模式，建造模式仍开着");
            // FG3-LOG-08（FG-GAP-071）：建造模式开着时镜头做一次飞行过渡（落点仍是战略视角）——建造模式不再被退出。
            SessionState.SetInt(K + "FlightsKept0", mode?.FlightsKept ?? 0);
            GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
            WorldView.FlyTo(Campaign.Regions.HomeValleyLayout.RegionId, new Vector2(core.X, core.Y));
            InputRouter.DebugSetReader(new ScriptedReader { Mouse = OffScreen });
            Next(262, "建造模式开着时镜头飞到归还核心（FG-GAP-071）");
        }

        private static void StepBeltBuildClosedForFly(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            int kept = (mode?.FlightsKept ?? 0) - SessionState.GetInt(K + "FlightsKept0", 0);
            Check(mode != null && mode.IsOpen && kept > 0 && WorldView.Director.Mode == View.ViewMode.Strategy && Mathf.Abs(f.x - core.X) < 1f && Mathf.Abs(f.y - core.Y) < 1f,
                $"FG-GAP-071：镜头飞行过渡（{kept} 帧保持建造模式）落到归还核心后建造模式仍开着");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(261, "Esc 退出建造模式");
        }

        private static void StepBeltCoreFocused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && !mode.IsOpen && !PauseMenuUIToolkit.IsOpen, "Esc 退出建造模式（没有打开暂停菜单）");
            GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
            HoverWorld(new Vector3(core.X, 0f, core.Y));
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
            Next(263, "鼠标停在归还核心上，按建造菜单键");
        }

        private static void StepBeltBuildReopenedAtCore(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string status = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
            Check(mode != null && mode.IsOpen && mode.HoverBuildingId != null && status.Contains("查看它的端口"),
                $"建造模式指着归还核心：状态行提示可以点开端口面板（{status.Replace("\n", " / ")}）");
            GridCell core = HomeGridService.CorePivot(CampaignSession.Current);
            ClickWorld(new Vector3(core.X, 0f, core.Y));
            SessionState.SetBool(K + "BpPortsClicked", false);
            Next(257, "建造模式里左键点归还核心（FG4-ECO-05 起先打开它的通用面板，再点“端口…”打开端口面板）");
        }

        private static void StepBeltPortPanelOpened(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            if (!SessionState.GetBool(K + "BpPortsClicked", false))
            {
                // FG4-ECO-05（FGU-09 建筑面板通用模板）：点核心打开的是通用面板——身份行、状态（工作，形状标记）、原因、核心不能禁用（按钮置灰并写原因）。
                ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
                string coreId = HomeGridService.FindBuilding(CampaignSession.Current, Campaign.Regions.HomeValleyLayout.RegionId + ":" + Campaign.Regions.HomeValleyLayout.BuildingTypeCore)?.BuildingId;
                bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && bp.PanelVisible && ProductionPanelUIToolkit.BuildingId == coreId;
                Check(bpOpen && bp.IdentText.Contains(HomeGridService.DisplayName(Campaign.Regions.HomeValleyLayout.BuildingTypeCore)) && bp.ShownStatus == Campaign.Economy.BuildingStatusKind.Working
                      && !string.IsNullOrEmpty(bp.ReasonText) && bp.EnableButton != null && !bp.EnableButton.enabledSelf && !string.IsNullOrEmpty(bp.EnableButton.tooltip)
                      && bp.DurabilityText.Length > 0,
                    $"点核心打开建筑通用面板（FGU-09）：“{bp?.IdentText}”，状态 {bp?.ShownStatus}（{bp?.StateText}）“{bp?.ReasonText}”，耐久“{bp?.DurabilityText}”，启用 / 禁用按钮置灰：“{bp?.EnableButton?.tooltip}”");
                CheckNoTextMarkers("建筑通用面板");
                // 改名（真实控件：输入框写字 → 点“改名”；再点“默认名”恢复），名字进面板标题。
                if (bp != null && bp.NameField != null)
                {
                    bp.NameField.value = "冒烟核心";
                    bool renamed = ClickUitk("[ProductionPanelHost]", "BpRename") && bp.TitleText.Contains("冒烟核心")
                                   && HomeGridService.FindBuilding(CampaignSession.Current, coreId)?.CustomName == "冒烟核心";
                    bool reset = ClickUitk("[ProductionPanelHost]", "BpRenameReset") && HomeGridService.FindBuilding(CampaignSession.Current, coreId)?.CustomName == null;
                    Check(renamed && reset, $"通用面板改名：输入框写“冒烟核心”点“改名”→ 标题“{bp.TitleText}”；点“默认名”恢复");
                }
                bool clicked = ClickUitk("[ProductionPanelHost]", "PrPorts");
                Check(clicked, "通用面板上点“端口…”");
                SessionState.SetBool(K + "BpPortsClicked", true);
                return;
            }
            if (inStep < 1.2)
            {
                return;
            }
            BeltPortPanelUIToolkit panel = BeltPortPanelUIToolkit.Instance;
            bool open = BeltPortPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.VisibleRowCount >= 1;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string diag = $"（诊断：建造模式开着 {mode?.IsOpen}，指着 {mode?.HoverCell} / 建筑 {mode?.HoverBuildingId}，核心 {HomeGridService.CorePivot(CampaignSession.Current)}，" +
                          $"面板宿主 {(panel != null ? (panel.IsReady ? "就绪" : "未就绪") : "没有")}，打开的建筑 {BeltPortPanelUIToolkit.BuildingId}，状态行“{mode?.StatusText}”）";
            Check(open && panel.RowText(0, "BpTitle").Contains("输入口") && panel.RowText(0, "BpAccept").Contains(Localization.GameText.Get("logistics.port.accept_all")) && panel.StoreText.Contains("家园仓库"), // FG4-ECO-01：核心输入口收全部可存物品
                $"端口面板（FGR-LOG-021）：“{panel?.TitleText}”{panel?.VisibleRowCount} 行，“{panel?.RowText(0, "BpTitle")}：{panel?.RowText(0, "BpState")}｜{panel?.RowText(0, "BpAccept")}”"
                + (open ? string.Empty : diag));
            CheckNoTextMarkers("端口面板");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(258, "Esc 关闭端口面板");
        }

        private static void StepBeltPortPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!BeltPortPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 只关掉端口面板，建造模式还开着");
            // 测试捷径：家园还没有会打传送带的敌人（突袭伤害在 FG6-DEF-05），经正式入口 TryDamage 把直线的第 5 格打坏到摧毁。
            CampaignState s = CampaignSession.Current;
            var cell = new GridCell(SessionState.GetInt(K + "BeltX", 0) + 4, SessionState.GetInt(K + "BeltY", 0));
            bool destroyed = BeltNetworkService.TryDamage(s, cell, 10000, out BeltOpResult r);
            bool ghost = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(s, cell, out PlannedBeltRecord p, out _) && p.Destroyed;
            Check(destroyed && ghost, $"测试捷径打坏一格传送带：摧毁、原位置留虚影（{r.Describe()}）");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.ConstructionQueue));
            Next(259, "按施工队列键（默认 Alt+B）");
        }

        private static void StepBeltRebuildQueue(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            ConstructionQueuePanelUIToolkit panel = ConstructionQueuePanelUIToolkit.Instance;
            int row = -1;
            for (int i = 0; panel != null && i < panel.VisibleRowCount; i++)
            {
                if (panel.RowDestroyedPlanId(i) != null)
                {
                    row = i;
                    break;
                }
            }
            string planId = row >= 0 ? panel.RowDestroyedPlanId(row) : null;
            Check(ConstructionQueuePanelUIToolkit.IsOpen && row >= 0 && panel.RowName(row).Contains("被摧毁") && panel.RebuildAllVisible,
                $"施工队列列出被摧毁的传送带：“{(row >= 0 ? panel.RowName(row) + "：" + panel.RowStatus(row) : "（没有）")}”");
            bool clicked = row >= 0 && InvokeClickable(panel.RowButton(row, "CqRebuild"));
            CampaignState s = CampaignSession.Current;
            bool queued = planId != null && Campaign.Regions.HomeValleyWorkOrders.FindActiveBuild(s, Campaign.Regions.HomeValleyConstruction.BeltPlanPrefix + planId) != null;
            Check(clicked && queued, "点“重建”：按原设置生成施工单（机器之后取料建回来）");
            // 取消这张施工单（行内“取消”，全额退回），再经正式入口把这一格铺回去——后面的存读档步骤按 22 格核对这组测试带。
            WorkOrderRecord order = planId != null ? Campaign.Regions.HomeValleyWorkOrders.FindActiveBuild(s, Campaign.Regions.HomeValleyConstruction.BeltPlanPrefix + planId) : null;
            panel?.Refresh();
            int orderRow = -1;
            for (int i = 0; panel != null && order != null && i < panel.VisibleRowCount; i++)
            {
                if (panel.RowOrderId(i) == order.WorkOrderId)
                {
                    orderRow = i;
                    break;
                }
            }
            bool cancelled = orderRow >= 0 && InvokeClickable(panel.RowButton(orderRow, "CqCancel"))
                             && Campaign.Regions.HomeValleyConstruction.FindPlan(s, planId) == null;
            var cell = new GridCell(SessionState.GetInt(K + "BeltX", 0) + 4, SessionState.GetInt(K + "BeltY", 0));
            bool relaid = BeltNetworkService.TryPlace(s, cell, BeltDir.East, 0).Ok;
            Check(cancelled && relaid && BeltNetworkService.Kernel.CellCount == SessionState.GetInt(K + "BeltCells", -1),
                $"行内“取消”撤掉这张重建单（虚影移除）；测试捷径把这一格铺回去（{BeltNetworkService.Kernel.CellCount} 格）");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.ConstructionQueue));
            Next(260, "再按施工队列键关闭");
        }

        private static void StepBeltRebuildDone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!ConstructionQueuePanelUIToolkit.IsOpen, "施工队列关闭");
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            if (mode == null || !mode.IsOpen)
            {
                PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu)); // 建造模式没开时按建造菜单键打开
            }
            // FG5-RND-01：分流器、地下传送带等要先研究——先走研发树（真实按键），研究完成后再放分流器。
            SessionState.SetInt(K + "ResearchKeySent", 0);
            Next(352, "FG5-RND-01：按 K（研发树键）打开研发树");
        }

        // ── FG5-RND-01：研发树（K 键、搜索、按分支筛选、滚轮缩放、悬停预览、加入队列）、仿真实验室（建造菜单“研发”页签）、研究完成与建造菜单“新”标记 ──

        private static ResearchTreePanelUIToolkit ResearchPanel()
        {
            ResearchTreePanelUIToolkit p = ResearchTreePanelUIToolkit.Instance;
            p?.Refresh();
            return p;
        }

        private static VisualElement ResearchRoot() =>
            GameObject.Find("[ResearchTreeHost]")?.GetComponent<UIDocument>()?.rootVisualElement;

        private static void StepResearchOpened(double inStep)
        {
            if (SessionState.GetInt(K + "ResearchKeySent", 0) == 0)
            {
                if (inStep < 0.6)
                {
                    return; // 等建造模式打开（上一步可能刚按了建造菜单键）
                }
                SessionState.SetInt(K + "ResearchKeySent", 1);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                // 测试捷径：研发这一段在战略暂停里走（只在实验室工作时放开约半秒），不推迟后面按游戏时间排的步骤（真实突袭到达会自动暂停，见 FG0-ARCH-03 那一步）。
                SessionState.SetInt(K + "ResearchWasPaused", GameClock.Paused ? 1 : 0);
                GameClock.SetPaused(true);
                PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenResearch));
                return;
            }
            if (inStep < 0.8)
            {
                return;
            }
            ResearchTreePanelUIToolkit p = ResearchPanel();
            int nodes = Campaign.Economy.ResearchCatalog.Nodes.Count;
            Check(ResearchTreePanelUIToolkit.IsOpen && p != null && p.PanelVisible && InputRouter.IsModalOwner(p) && p.NodeViewCount == nodes && nodes >= 60
                  && p.StatusText.Length > 0 && !Localization.GameText.ContainsMarker(p.SummaryText + p.StatusText + p.FooterText + p.QueueTitleText + p.QueueEmptyText),
                $"按 K 打开研发树：{p?.NodeViewCount} 个节点、{p?.FilterButtonCount} 个筛选按钮；“{p?.SummaryText}”；状态“{p?.StatusText}”；页脚“{p?.FooterText}”");
            CheckNoTextMarkers("研发树");
            // 在搜索框里打“分流”（与键盘输入同一个值变化回调）。
            TextField search = ResearchRoot()?.Q<TextField>("ResearchSearch");
            if (search != null)
            {
                search.value = "分流";
            }
            Next(353, "在研发树搜索框里输入“分流”");
        }

        private static void StepResearchSearched(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            ResearchTreePanelUIToolkit p = ResearchPanel();
            Check(p != null && p.MatchCount >= 1 && p.FirstMatchId == "logistics.splitter" && p.NodeHasClass("logistics.splitter", "rt-node-match")
                  && p.NodeHasClass("industry.lab_t2", "rt-node-dim") && p.SearchCountText.Length > 0,
                $"搜索“分流”：找到 {p?.MatchCount} 个（“{p?.SearchCountText}”），第一个是“物流 · 分流与过滤”，其余节点变暗");
            TextField search = ResearchRoot()?.Q<TextField>("ResearchSearch");
            if (search != null)
            {
                search.value = string.Empty;
            }
            int idx = -1;
            for (int i = 0; p != null && i < p.FilterButtonCount; i++)
            {
                if (p.FilterId(i) == "logistics")
                {
                    idx = i;
                }
            }
            Check(idx > 0 && ClickUitk("[ResearchTreeHost]", "ResearchFilter" + idx), "点“物流”分支筛选按钮");
            Next(354, "只看物流分支");
        }

        private static void StepResearchFiltered(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            ResearchTreePanelUIToolkit p = ResearchPanel();
            Check(p != null && p.Filter == "logistics" && p.NodeVisible("logistics.splitter") && !p.NodeVisible("industry.lab_t2"),
                $"按分支筛选：只显示物流分支（{p?.VisibleNodeCount} 个节点）");
            // 鼠标滚轮在画布上滚一下（放大），再把指针移到“分流与过滤”节点上。
            VisualElement viewport = ResearchRoot()?.Q<VisualElement>("ResearchViewport");
            SessionState.SetFloat(K + "ResearchZoom0", p?.Zoom ?? 1f);
            if (viewport != null)
            {
                using (WheelEvent we = WheelEvent.GetPooled(new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, -3f), mousePosition = viewport.worldBound.center }))
                {
                    we.target = viewport;
                    viewport.SendEvent(we);
                }
            }
            VisualElement node = p?.NodeButton("logistics.splitter");
            if (node != null)
            {
                using (PointerEnterEvent enter = PointerEnterEvent.GetPooled())
                {
                    enter.target = node;
                    node.SendEvent(enter);
                }
            }
            Next(355, "滚轮放大；鼠标悬停“分流与过滤”");
        }

        private static void StepResearchHovered(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            ResearchTreePanelUIToolkit p = ResearchPanel();
            float z0 = SessionState.GetFloat(K + "ResearchZoom0", 1f);
            Check(p != null && p.Zoom > z0 && p.DetailNodeId == "logistics.splitter" && p.DetailBody.Contains("分流器") && p.DetailBody.Contains("合流器")
                  && p.DetailNote.Contains("占位图标"),
                $"滚轮放大（{z0} → {p?.Zoom}）；悬停节点右侧写它会解锁什么（“{p?.DetailBody.Replace("\n", " / ")}”）");
            Check(ClickUitk("[ResearchTreeHost]", "RtNode_logistics.splitter"), "左键点“分流与过滤”加入研究队列");
            Next(356, "加入研究队列");
        }

        private static void StepResearchQueued(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            ResearchTreePanelUIToolkit p = ResearchPanel();
            CampaignState state = CampaignSession.Current;
            bool noLab = Campaign.Economy.ResearchService.BuiltLabCount(state) == 0;
            Check(Campaign.Economy.ResearchService.QueueIndex(state, "logistics.splitter") == 0 && p != null && p.QueueRowVisibleCount == 1 && p.QueueRowText(0).Contains("分流与过滤")
                  && (!noLab || p.StatusText.Contains("还没有仿真实验室")),
                $"队列第 1 项“{p?.QueueRowText(0)}”；状态“{p?.StatusText}”（没有实验室时写明要建一座）");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenResearch));
            Next(357, "再按 K 关闭研发树，去建造菜单“研发”页签放仿真实验室");
        }

        private static void StepResearchLabPicked(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!ResearchTreePanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "研发树关闭（建造模式还开着）");
            // 核心附近按地形找两块能放仿真实验室的空地（一块放虚影，一块登记建成的测试实验室），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? ghostAt = null, builtAt = null;
            for (int r = 6; r <= 20 && builtAt == null; r++)
            {
                for (int dy = -r; dy <= r && builtAt == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtAt == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!HomeGridService.ValidatePlacement(state, "sim_lab", c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        if (ghostAt == null)
                        {
                            ghostAt = c;
                        }
                        else if (Math.Abs(c.X - ghostAt.Value.X) > 4 || Math.Abs(c.Y - ghostAt.Value.Y) > 4)
                        {
                            builtAt = c;
                        }
                    }
                }
            }
            SessionState.SetInt(K + "LabGhostX", ghostAt?.X ?? 0);
            SessionState.SetInt(K + "LabGhostY", ghostAt?.Y ?? 0);
            SessionState.SetInt(K + "LabBuiltX", builtAt?.X ?? 0);
            SessionState.SetInt(K + "LabBuiltY", builtAt?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "research");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("sim_lab");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "sim_lab";
            Check(ghostAt.HasValue && builtAt.HasValue && tabClicked && picked, $"点建造菜单“研发”页签里的“仿真实验室”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）");
            if (ghostAt.HasValue)
            {
                HoverWorld(new Vector3(ghostAt.Value.X, 0f, ghostAt.Value.Y));
            }
            Next(358, "鼠标移到空地上，单击放下仿真实验室");
        }

        private static void StepResearchLabPlaced(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            var ghostAt = new GridCell(SessionState.GetInt(K + "LabGhostX", 0), SessionState.GetInt(K + "LabGhostY", 0));
            var builtAt = new GridCell(SessionState.GetInt(K + "LabBuiltX", 0), SessionState.GetInt(K + "LabBuiltY", 0));
            if (SessionState.GetInt(K + "LabClicked", 0) == 0)
            {
                if (inStep < 0.5)
                {
                    return;
                }
                SessionState.SetInt(K + "LabClicked", 1);
                ClickWorld(new Vector3(ghostAt.X, 0f, ghostAt.Y));
                return;
            }
            if (SessionState.GetInt(K + "LabClicked", 0) == 1)
            {
                if (inStep < 1.2)
                {
                    return;
                }
                BuildingRecord ghost = HomeGridService.BuildingAt(state, ghostAt);
                Check(ghost != null && ghost.BuildingTypeId == "sim_lab" && Campaign.Regions.HomeValleyController.IsPlannedGhost(ghost),
                    $"单击放下仿真实验室的虚影（状态行“{Campaign.Regions.HomeValleyBuildMode.Current?.StatusText}”）");
                // 测试捷径：另一块空地上直接登记一座建成的实验室（机器施工由 FG3-LOG-02 覆盖）；技术数据 +6（技术数据的正式来源——解析 / 残骸 / 黑匣子——在 FG5-RND-02 / 06、FG8）。
                AddSmokeBuilding(state, "sim_lab", "lab", builtAt);
                Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                Campaign.Economy.HomeInventory.Add(state, Campaign.Economy.ItemCatalog.TechDataId, 6, clampToSpace: false);
                // 测试捷径：研究点 +5（实验室 1 点 / 分钟，等满 5 分钟太久；产出速率由 FgResearchSelfCheck B 段在真实世界里断言）。
                Campaign.Economy.HomeInventory.Add(state, Campaign.Economy.ItemCatalog.ResearchPointsId, 5, clampToSpace: false);
                SessionState.SetInt(K + "LabTech0", state.TechData);
                SessionState.SetInt(K + "ResearchNotes0", Notifications.NotificationCenter.History.Where(e => e.Type?.Id == "research_done").Sum(e => e.Count));
                SessionState.SetInt(K + "LabClicked", 2);
                SessionState.SetFloat(K + "StepStart", (float)EditorApplication.timeSinceStartup);
                GameClock.SetPaused(false); // 放开约半秒真实帧：实验室取技术数据开始转换，攒着的研究点投进队列
                return;
            }
            if (inStep < 0.6)
            {
                return;
            }
            GameClock.SetPaused(true);
            BuildingRecord lab = HomeGridService.BuildingAt(state, builtAt);
            Campaign.Economy.BuildingStatus st = lab != null ? Campaign.Economy.BuildingStatusService.Evaluate(state, lab) : default;
            bool working = st.ReasonCode == "lab.working";
            int tech0 = SessionState.GetInt(K + "LabTech0", 0);
            Check(lab != null && st.Reason.Length > 0 && (working ? state.TechData < tech0 : st.Kind == Campaign.Economy.BuildingStatusKind.NoPower),
                $"仿真实验室状态“{st.Reason}”（工作中时技术数据 {tech0} → {state.TechData}；缺电时写明原因）");
            SessionState.SetInt(K + "LabClicked", 0);
            Next(359, "研究点投进队列、研究完成");
        }

        private static void StepResearchDone(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int notes = Notifications.NotificationCenter.History.Where(e => e.Type?.Id == "research_done").Sum(e => e.Count);
            string text = Notifications.NotificationCenter.History.LastOrDefault(e => e.Type?.Id == "research_done")?.Text ?? string.Empty;
            Check(Campaign.Economy.ResearchService.IsCompleted(state, "logistics.splitter") && notes == SessionState.GetInt(K + "ResearchNotes0", 0) + 1
                  && text.Contains("分流器") && Campaign.Grid.BuildCatalog.IsUnlocked(state, "research:logistics.splitter")
                  && Campaign.Economy.ResearchService.IsNew(state, "splitter"),
                $"研究完成：通知“{text}”；分流器 / 合流器解锁并标“新”");
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            Check(ClickUitk("[BuildModeHudHost]", "BuildCat" + tab), "点建造菜单“物流”页签");
            Next(360, "建造菜单“物流”页签：分流器标“新”");
        }

        private static void StepResearchNewMark(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            int idx = HudItemIndex("splitter");
            string item = hud?.Root?.Q<UnityEngine.UIElements.Button>("BuildItem" + idx)?.text ?? string.Empty;
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            string tabText = hud?.Root?.Q<UnityEngine.UIElements.Button>("BuildCat" + tab)?.text ?? string.Empty;
            bool marked = item.StartsWith(Localization.GameText.Get("research.build.new"), StringComparison.Ordinal) && tabText.Contains("新");
            // 换到“能源”页签再回来：看过的“新”标记消失。
            int energy = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "energy");
            ClickUitk("[BuildModeHudHost]", "BuildCat" + energy);
            ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            hud?.Refresh();
            string after = hud?.Root?.Q<UnityEngine.UIElements.Button>("BuildItem" + HudItemIndex("splitter"))?.text ?? string.Empty;
            Check(marked && !Campaign.Economy.ResearchService.IsNew(state, "splitter") && !after.StartsWith(Localization.GameText.Get("research.build.new"), StringComparison.Ordinal),
                $"新解锁的分流器写“{item.Split('\n')[0]}”、页签“{tabText}”；换页看过一次后“新”标记消失（“{after.Split('\n')[0]}”）");
            // 测试捷径：后面各步要放的已研究内容（地下传送带、T2 传送带、地下管线、储能站、电子组装台、超控阵列……）直接记为已研究——研究流程本身已在上面与 FgResearchSelfCheck 覆盖。
            var unlockNodes = Campaign.Economy.ResearchCatalog.Nodes.Where(n => n.IsReady && n.Unlocks.Length > 0).Select(n => n.Id).ToArray();
            Campaign.Economy.ResearchService.CompleteForTests(state, unlockNodes);
            GameClock.SetPaused(SessionState.GetInt(K + "ResearchWasPaused", 0) == 1); // 恢复进研发这一段之前的暂停状态
            Next(361, "FG5-RND-03：建造菜单“研发”页签选靶场");
        }

        // ── FG5-RND-03：靶场（建造菜单放置、建筑面板“靶场…”、投影一张蓝图、实时读数、结束测试、Esc 关闭）──

        private static GridCell RangeCell(string key) => new GridCell(SessionState.GetInt(K + key + "X", 0), SessionState.GetInt(K + key + "Y", 0));

        private static void StepRangePicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            SessionState.SetInt(K + "RangeWasPaused", GameClock.Paused ? 1 : 0);
            GameClock.SetPaused(true);
            // 核心附近按地形找一块放靶场虚影的空地（玩家放置校验）；再登记一座接得上电网的建成靶场（测试捷径：机器施工由 FG3-LOG-02 覆盖），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? ghostAt = null;
            string builtId = null;
            for (int r = 6; r <= 24 && (ghostAt == null || builtId == null); r++)
            {
                for (int dy = -r; dy <= r && (ghostAt == null || builtId == null); dy++)
                {
                    for (int dx = -r; dx <= r && (ghostAt == null || builtId == null); dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (ghostAt == null)
                        {
                            if (HomeGridService.ValidatePlacement(state, "test_range", c, 0, checkCost: false).Ok)
                            {
                                ghostAt = c;
                            }
                            continue;
                        }
                        if (Math.Abs(c.X - ghostAt.Value.X) < 10 && Math.Abs(c.Y - ghostAt.Value.Y) < 10
                            || !HomeGridService.ValidatePlacement(state, "test_range", c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        AddSmokeBuilding(state, "test_range", "range", c);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                        string id = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_range";
                        BuildingRecord b = HomeGridService.FindBuilding(state, id);
                        if (b != null && b.PowerState == BuildingPowerState.Powered)
                        {
                            b.Health = Campaign.Economy.BuildingOps.MaxDurability("test_range");
                            builtId = id;
                            SessionState.SetInt(K + "RangeBuiltX", c.X);
                            SessionState.SetInt(K + "RangeBuiltY", c.Y);
                            continue;
                        }
                        state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != id).ToArray();
                        HomeGridService.MapFor(state);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    }
                }
            }
            SessionState.SetInt(K + "RangeGhostX", ghostAt?.X ?? 0);
            SessionState.SetInt(K + "RangeGhostY", ghostAt?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "research");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("test_range");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode != null && mode.SelectedTypeId == "test_range";
            Check(ghostAt.HasValue && builtId != null && tabClicked && picked, $"点建造菜单“研发”页签里的“靶场”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）；接得上电网的建成靶场 {builtId}");
            if (ghostAt.HasValue)
            {
                HoverWorld(new Vector3(ghostAt.Value.X, 0f, ghostAt.Value.Y));
            }
            SessionState.SetInt(K + "RangeSub", 0);
            Next(362, "鼠标移到空地上，单击放下靶场的虚影");
        }

        private static void StepRangePlaced(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            GridCell ghostAt = RangeCell("RangeGhost");
            GridCell builtAt = RangeCell("RangeBuilt");
            string builtId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_range";
            BuildingRecord built = HomeGridService.FindBuilding(state, builtId);
            int sub = SessionState.GetInt(K + "RangeSub", 0);
            if (sub == 0)
            {
                if (inStep < 0.5)
                {
                    return;
                }
                SessionState.SetInt(K + "RangeSub", 1);
                ClickWorld(new Vector3(ghostAt.X, 0f, ghostAt.Y));
                return;
            }
            if (sub == 1)
            {
                if (inStep < 1.2)
                {
                    return;
                }
                BuildingRecord ghost = HomeGridService.BuildingAt(state, ghostAt);
                Check(ghost != null && ghost.BuildingTypeId == "test_range" && Campaign.Regions.HomeValleyController.IsPlannedGhost(ghost),
                    $"单击放下靶场的虚影（8×8；状态行“{Campaign.Regions.HomeValleyBuildMode.Current?.StatusText}”）");
                SessionState.SetInt(K + "RangeSub", 2);
                RightClickWorld(new Vector3(ghostAt.X, 0f, ghostAt.Y));
                return;
            }
            if (sub == 2)
            {
                if (inStep < 1.9)
                {
                    return;
                }
                Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "RangeSub", 3);
                ClickWorld(built != null ? new Vector3(built.Position.x, 0f, built.Position.y) : new Vector3(builtAt.X, 0f, builtAt.Y));
                return;
            }
            if (inStep < 2.6)
            {
                return;
            }
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && ProductionPanelUIToolkit.BuildingId == builtId && bp.RangeButton != null
                          && ProductionPanelUIToolkit.Visible(bp.RangeButton) && bp.ReasonText.Contains("空闲");
            Check(bpOpen, $"左键点建成的靶场打开它的通用面板：状态“{bp?.ReasonText}”，有“靶场…”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrRange"), "通用面板上点“靶场…”");
            SessionState.SetInt(K + "RangeSub", 0);
            Next(363, "靶场面板打开：选蓝图、点“投影”");
        }

        private static void StepRangeOpened(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            string builtId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_range";
            bool open = TestRangePanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && TestRangePanelUIToolkit.BuildingId == builtId && panel.SlotRowCount == 8
                        && panel.SlotValue(0) == Localization.GameText.Get("range.target.basic.name") && panel.BlueprintChoices.Count > 0
                        && panel.ProjectionsEmptyText.Length > 0 && LabelText("[TestRangeHost]", "TestRangeTitle").Contains(Localization.GameText.Get("building.test_range.name"));
            Check(open, $"靶场面板打开：“{panel?.TitleText}”，8 个靶位（前排标准靶）、蓝图 {panel?.BlueprintChoices.Count} 张、空状态“{panel?.ProjectionsEmptyText}”");
            CheckNoTextMarkers("靶场面板");
            // UI Toolkit 红线 8：下拉框选中即生效（玩家点选项 = value 赋值 → ChangeEvent）；先确认下拉框此刻真能点。
            DropdownField dd = panel?.BlueprintField;
            bool clickable = dd != null && dd.panel != null && dd.enabledInHierarchy && dd.resolvedStyle.display != DisplayStyle.None && dd.worldBound.width > 0f;
            Check(clickable, $"蓝图下拉框可见可用（{dd?.worldBound}）");
            if (clickable)
            {
                dd.value = dd.choices[0];
            }
            SessionState.SetInt(K + "RangeHist0", state.Research.Range.History.Length);
            string before = $"{state.Scrap}|{state.TechData}|{state.MachineRecords?.Length ?? 0}";
            SessionState.SetString(K + "RangeRes0", before);
            Check(ClickUitk("[TestRangeHost]", "TestRangeProject"), "点“投影”");
            GameClock.SetPaused(false); // 放开约一秒真实帧：投影打靶、读数刷新
            Next(364, "测试进行中：投影行与实时读数");
        }

        private static void StepRangeRunning(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            GameClock.SetPaused(true);
            CampaignState state = CampaignSession.Current;
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            string builtId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_range";
            panel?.Refresh(force: true);
            bool running = Campaign.Economy.TestRangeService.IsRunning(builtId) && panel != null && panel.ProjectionRowCount == 1
                           && panel.RunStateText.Contains("测试中") && panel.ReadingsText.Contains("每秒伤害")
                           && Campaign.Economy.TestRangeService.SiteOf(builtId) != null && CombatSitesHasNoRange(builtId);
            string after = $"{state.Scrap}|{state.TechData}|{state.MachineRecords?.Length ?? 0}";
            Check(running && after == SessionState.GetString(K + "RangeRes0", string.Empty),
                $"投影出现在靶场里（第 1 行“{panel?.ProjectionRowText(0)}”），测试进行中、读数实时刷新；废料 / 技术数据 / 机器数不变（{after}）；投影不登记进战斗地点表");
            Check(ClickUitk("[TestRangeHost]", "TestRangeEnd"), "点“结束测试”");
            Next(365, "测试结束：读数进测试记录，Esc 关闭靶场面板");
        }

        private static bool CombatSitesHasNoRange(string buildingId) =>
            Campaign.Combat.CombatSites.Get(Campaign.Economy.TestRangeService.SitePrefix + buildingId) == null;

        private static void StepRangeClosed(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            string builtId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_range";
            if (SessionState.GetInt(K + "RangeSub", 0) == 0)
            {
                if (inStep < 0.5)
                {
                    return;
                }
                TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
                panel?.Refresh(force: true);
                RangeResultRecord last = state.Research.Range.History.LastOrDefault();
                Check(!Campaign.Economy.TestRangeService.IsRunning(builtId) && state.Research.Range.History.Length == SessionState.GetInt(K + "RangeHist0", 0) + 1
                      && last != null && last.EndReason == (int)Campaign.Economy.RangeEndReason.Player && panel != null && panel.RunStateText.Contains("没有在测试"),
                    $"测试结束：读数进测试记录（{last?.Seconds:F1} 秒、每秒伤害 {last?.Dps:F1}、结束原因“{(last != null ? Campaign.Economy.TestRangeService.EndReasonText(last.EndReason) : "无")}”）");
                SessionState.SetInt(K + "RangeSub", 1);
                PressKeyKeepMouse(KeyCode.Escape);
                return;
            }
            if (inStep < 1.0)
            {
                return;
            }
            Check(!TestRangePanelUIToolkit.IsOpen && Campaign.Regions.HomeValleyBuildMode.Current != null && Campaign.Regions.HomeValleyBuildMode.Current.IsOpen,
                "Esc 关闭靶场面板（建造模式还开着）");
            SessionState.SetInt(K + "RangeSub", 0);
            Next(366, "FG5-RND-04：放一座建成的电路合成台，左键点它");
        }

        // ── FG5-RND-04：电路合成台（建筑面板“熔合…”、模拟熔合、正式熔合（确认框）、熔合进行中、取消退回、配方书键、Esc 关闭）──

        private const string SmokeSynthId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_synth";

        private static void StepFusionPlaced(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            GameClock.SetPaused(true);
            // 核心附近按地形找一块能放、接得上电网的空地，登记一座建成的电路合成台（测试捷径：机器施工由 FG3-LOG-02 覆盖），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            string builtId = null;
            for (int r = 6; r <= 26 && builtId == null; r++)
            {
                for (int dy = -r; dy <= r && builtId == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtId == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!HomeGridService.ValidatePlacement(state, "circuit_synth", c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        AddSmokeBuilding(state, "circuit_synth", "synth", c);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                        BuildingRecord b = HomeGridService.FindBuilding(state, SmokeSynthId);
                        if (b != null && b.PowerState == BuildingPowerState.Powered)
                        {
                            b.Health = Campaign.Economy.BuildingOps.MaxDurability("circuit_synth");
                            builtId = SmokeSynthId;
                            continue;
                        }
                        state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeSynthId).ToArray();
                        HomeGridService.MapFor(state);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    }
                }
            }
            // 测试捷径：固件库里各两枚燃迹 / 漏油（已破解）、仓库补芯片基板与技术数据（取得这些的正式流程由各自的自检覆盖）。
            state.UnlockedContentIds = (state.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { "fw_burntrail", "fw_oilleak" }).Distinct().ToArray();
            for (int i = 0; i < 2; i++)
            {
                Campaign.Primitive.PrimitiveInventory.GrantCrafted(state, "fw_burntrail");
                Campaign.Primitive.PrimitiveInventory.GrantCrafted(state, "fw_oilleak");
            }
            Campaign.Economy.HomeInventory.Add(state, "chip_substrate", 4, clampToSpace: false); // 与维修件同一种测试捷径（此刻仓库的固体余量随前面各步变化）
            state.TechData += 60;
            BuildingRecord built = HomeGridService.FindBuilding(state, SmokeSynthId);
            Check(builtId != null, $"接得上电网的建成电路合成台 {builtId}；固件库里有燃迹 / 漏油，仓库有芯片基板与技术数据");
            if (built != null)
            {
                ClickWorld(new Vector3(built.Position.x, 0f, built.Position.y));
            }
            Next(367, "左键点电路合成台打开通用面板，点“熔合…”");
        }

        private static void StepFusionBuildingPanel(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && ProductionPanelUIToolkit.BuildingId == SmokeSynthId && bp.FusionButton != null
                          && ProductionPanelUIToolkit.Visible(bp.FusionButton) && bp.ReasonText.Contains("空闲");
            Check(bpOpen, $"左键点建成的电路合成台打开它的通用面板：状态“{bp?.ReasonText}”，有“熔合…”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrFusion"), "通用面板上点“熔合…”");
            Next(368, "合成台面板打开：选燃迹 / 漏油，点“模拟熔合”");
        }

        private static void StepFusionOpened(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            FusionPanelUIToolkit panel = FusionPanelUIToolkit.Instance;
            bool open = FusionPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && FusionPanelUIToolkit.BuildingId == SmokeSynthId
                        && LabelText("[FusionPanelHost]", "FusionTitle").Contains(Localization.GameText.Get("building.circuit_synth.name"))
                        && panel.RemainingText.Contains("还有") && panel.QueueEmptyText.Length > 0;
            Check(open, $"合成台面板打开：“{panel?.TitleText}”；固件下拉 {panel?.ParentChoices.Count} 条；“{FirstLine(panel?.RemainingText)}”");
            CheckNoTextMarkers("电路合成台面板");
            // UI Toolkit 红线 8：下拉框选中即生效（玩家点选项 = value 赋值 → ChangeEvent）；先确认下拉框此刻真能点。
            DropdownField a = panel?.ParentField(true);
            DropdownField b = panel?.ParentField(false);
            bool clickable = a != null && b != null && a.panel != null && a.enabledInHierarchy && a.worldBound.width > 0f && b.worldBound.width > 0f;
            Check(clickable, $"固件 A / B 下拉框可见可用（{a?.worldBound}）");
            if (clickable)
            {
                int ia = panel.ParentIndexOf("fw_burntrail");
                int ib = panel.ParentIndexOf("fw_oilleak");
                if (ia >= 0 && ib >= 0)
                {
                    a.value = a.choices[ia];
                    b.value = b.choices[ib];
                }
            }
            SessionState.SetInt(K + "FusionTech0", state.TechData);
            Check(ClickUitk("[FusionPanelHost]", "FusionSimulate"), "点“模拟熔合”");
            Next(369, "模拟出配方：点“正式熔合…”并确认");
        }

        private static string FirstLine(string s) => (s ?? string.Empty).Split('\n')[0];

        private static int SubstrateTotal(CampaignState state)
        {
            Campaign.Economy.ItemDistribution.Invalidate();
            return (int)Campaign.Economy.ItemDistribution.Get(state, Campaign.Economy.ItemCatalog.Find("chip_substrate")).Total;
        }

        private static void StepFusionSimulated(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            FusionPanelUIToolkit panel = FusionPanelUIToolkit.Instance;
            int sub = SessionState.GetInt(K + "FusionSub", 0);
            if (sub == 0)
            {
                if (inStep < 0.5)
                {
                    return;
                }
                panel?.Refresh(force: true);
                bool hit = panel != null && panel.SelectedA == "fw_burntrail" && panel.SelectedB == "fw_oilleak" && panel.ResultText.Contains("有配方")
                           && state.TechData == SessionState.GetInt(K + "FusionTech0", -1) - Campaign.Economy.FusionCatalog.SimTech
                           && panel.FormalButton.enabledSelf;
                Check(hit, $"模拟熔合：结果“{FirstLine(panel?.ResultText)}”，技术数据 -{Campaign.Economy.FusionCatalog.SimTech}，固件不消耗；“正式熔合…”可以点了");
                Check(ClickUitk("[FusionPanelHost]", "FusionFormal"), "点“正式熔合…”");
                SessionState.SetInt(K + "FusionSub", 1);
                return;
            }
            if (inStep < 1.0)
            {
                return;
            }
            bool asked = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title == Localization.GameText.Get("fusion.confirm.title");
            bool confirmed = ClickUitk("[UiKitOverlayHost]", "ConfirmOk") && !UiConfirmDialog.IsOpen;
            Check(asked && confirmed, "正式熔合先弹确认框（熔合消耗，B04），点“确认”");
            SessionState.SetInt(K + "FusionSub", 0);
            GameClock.SetPaused(false); // 放开约一秒真实帧：熔合进行中
            Next(370, "熔合进行中：队列行“取消”全部退回");
        }

        private static void StepFusionRunning(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            GameClock.SetPaused(true);
            CampaignState state = CampaignSession.Current;
            FusionPanelUIToolkit panel = FusionPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            FusionJobRecord job = Campaign.Economy.FusionService.JobsOf(state, SmokeSynthId).FirstOrDefault();
            bool running = job != null && Campaign.Economy.FusionService.IsActive(job) && job.Progress > 0f && panel != null && panel.QueueRowCount == 1
                           && panel.QueueRowText(0).Contains("熔合中") && state.PrimitiveChips.Count(c => c.ReservedByTransactionId == job.JobId) == 2;
            Check(running, $"熔合进行中：队列行“{panel?.QueueRowText(0)}”，两枚父固件被预留（{job?.Progress:F1} 秒）");
            int sub0 = SubstrateTotal(state); // 物资分布总量（仓库 / 地面 / 熔合在办……）：取消前后不变才算全部退回
            Check(ClickUitk("[FusionPanelHost]", "FqCancel"), "点队列行的“取消”");
            panel?.Refresh(force: true);
            bool refunded = job != null && job.State == FusionJobState.Cancelled && SubstrateTotal(state) == sub0 && job.Substrate == 0
                            && state.PrimitiveChips.All(c => c.ReservedByTransactionId != job.JobId) && panel != null && panel.MessageText.Contains("全部退回");
            Check(refunded, $"取消：“{panel?.MessageText}”");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenRecipeBook));
            Next(371, "配方书键关掉面板、再打开配方书，Esc 关闭");
        }

        private static void StepFusionBook(double inStep)
        {
            int sub = SessionState.GetInt(K + "FusionSub", 0);
            if (sub == 0)
            {
                if (inStep < 0.6)
                {
                    return;
                }
                Check(!FusionPanelUIToolkit.IsOpen, "面板开着时按配方书键（默认 Alt+F）关掉");
                SessionState.SetInt(K + "FusionSub", 1);
                PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenRecipeBook));
                return;
            }
            if (sub == 1)
            {
                if (inStep < 1.2)
                {
                    return;
                }
                FusionPanelUIToolkit panel = FusionPanelUIToolkit.Instance;
                Check(FusionPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.RemainingText.Contains("还有"),
                    $"再按配方书键打开配方书：“{FirstLine(panel?.RemainingText)}”");
                SessionState.SetInt(K + "FusionSub", 2);
                PressKeyKeepMouse(KeyCode.Escape);
                return;
            }
            if (inStep < 1.8)
            {
                return;
            }
            Check(!FusionPanelUIToolkit.IsOpen && Campaign.Regions.HomeValleyBuildMode.Current != null && Campaign.Regions.HomeValleyBuildMode.Current.IsOpen,
                "Esc 关闭配方书（建造模式还开着）");
            SessionState.SetInt(K + "FusionSub", 0);
            GameClock.SetPaused(SessionState.GetInt(K + "RangeWasPaused", 0) == 1);
            Next(372, "FG5-RND-05：放一座建成的监听站、派一支突袭，左键点监听站");
        }

        // ── FG5-RND-05：监听站与情报（建筑面板“情报…”、空状态、突袭预报通知、情报键、在地图上查看、地图箭头）──

        private const string SmokePostId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_post";

        private static void StepIntelPlaced(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            SessionState.SetInt(K + "IntelWasPaused", GameClock.Paused ? 1 : 0);
            GameClock.SetPaused(true);
            // 核心附近按地形找一块能放、接得上电网的空地，登记一座建成的监听站（测试捷径：研究门槛与机器施工由各自的自检覆盖），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            string builtId = null;
            for (int r = 6; r <= 30 && builtId == null; r++)
            {
                for (int dy = -r; dy <= r && builtId == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtId == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!HomeGridService.ValidatePlacement(state, "listening_post", c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        AddSmokeBuilding(state, "listening_post", "post", c);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                        BuildingRecord b = HomeGridService.FindBuilding(state, SmokePostId);
                        if (b != null && b.PowerState == BuildingPowerState.Powered)
                        {
                            b.Health = Campaign.Economy.BuildingOps.MaxDurability("listening_post");
                            builtId = SmokePostId;
                            continue;
                        }
                        state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokePostId).ToArray();
                        HomeGridService.MapFor(state);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    }
                }
            }
            // 测试捷径：突袭部队由测试直接派出（正式的突袭导演在 FG6-DEF-04；派出之后的沿地形行进、破译、预报全是正式流程）。
            Campaign.WorldGen.WorldPlan plan = Campaign.WorldGen.WorldGenService.PlanFor(state);
            string territory = plan?.Territories.FirstOrDefault(t => t.IsFaction && t.Act == 1)?.Id ?? "silent";
            TransitGroupRecord raid = WorldTransitSystem.DispatchRaidFromTerritory(state, territory, 3, out string fail);
            SessionState.SetString(K + "IntelRaid", raid?.GroupId ?? string.Empty);
            BuildingRecord built = HomeGridService.FindBuilding(state, SmokePostId);
            Check(builtId != null && raid != null, $"接得上电网的建成监听站 {builtId}；一支突袭部队从 {territory} 出发（{raid?.GroupId ?? fail}）");
            if (built != null)
            {
                ClickWorld(new Vector3(built.Position.x, 0f, built.Position.y));
            }
            Next(373, "左键点监听站打开通用面板，点“情报…”");
        }

        private static void StepIntelBuildingPanel(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && ProductionPanelUIToolkit.BuildingId == SmokePostId && bp.IntelButton != null
                          && ProductionPanelUIToolkit.Visible(bp.IntelButton) && bp.ReasonText.Contains("破译中") && bp.ReasonText.Contains("突袭预报");
            Check(bpOpen, $"左键点建成的监听站打开它的通用面板：状态“{bp?.ReasonText}”，有“情报…”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrIntel"), "通用面板上点“情报…”");
            Next(374, "情报面板打开：还没有情报（空状态），破译中");
        }

        private static void StepIntelOpenedEmpty(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            IntelPanelUIToolkit panel = IntelPanelUIToolkit.Instance;
            bool open = IntelPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && LabelText("[IntelPanelHost]", "IntelTitle") == Localization.GameText.Get("intel.panel.title")
                        && panel.EmptyText.Length > 0 && panel.RowCount == 0 && panel.StatusText.Contains("1 座监听站工作中") && panel.StatusText.Contains("突袭预报");
            Check(open, $"情报面板打开：空状态“{FirstLine(panel?.EmptyText)}”；状态“{FirstLine(panel?.StatusText)}”");
            CheckNoTextMarkers("情报面板");
            PressKeyKeepMouse(KeyCode.Escape);
            SessionState.SetFloat(K + "IntelSpeed", GameClock.Speed);
            GameClock.SetSpeed(3f);
            GameClock.SetPaused(false);
            Next(375, "Esc 关闭；3x 放开等突袭预报");
        }

        private static void StepIntelForecast(double inStep)
        {
            string raidId = SessionState.GetString(K + "IntelRaid", string.Empty);
            // 后续情报会覆盖 LastProduced；按本次突袭的持久记录验收。
            Campaign.IntelRecord f = Campaign.Economy.IntelService.StateOf(CampaignSession.Current)?.Records
                ?.FirstOrDefault(r => r.Kind == Campaign.Economy.IntelCatalog.KindRaid && r.Subject == raidId);
            bool got = f != null;
            bool notified = Notifications.NotificationCenter.History.Any(e => e.Type.Id == "intel_raid");
            if (!got && GameClock.Paused && inStep < 40)
            {
                Write("  - 情报等待期间恢复世界推进（通知自动暂停次数 " + Notifications.NotificationCenter.AutoPauseCount + "）");
                GameClock.SetPaused(false);
            }
            if (!(got && notified) && inStep < 40)
            {
                return;
            }
            Check(!IntelPanelUIToolkit.IsOpen, "Esc 关闭情报面板");
            if (!(got && notified))
            {
                Write($"  - 情报超时诊断：世界步 {GameClock.Ticks}、暂停 {GameClock.Paused}、倍速 {GameClock.Speed}、工作监听站 {Campaign.Economy.IntelService.WorkingCount(CampaignSession.Current)}、突袭破译进度 {Campaign.Economy.IntelService.ProgressPercent(CampaignSession.Current, Campaign.Economy.IntelCatalog.KindRaid)}%");
            }
            Check(got && notified, $"监听站破译出突袭预报并发通知（{(got ? Campaign.Economy.IntelService.Summary(CampaignSession.Current, f, GameClock.Ticks) : "超时")}）");
            GameClock.SetPaused(true);
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenIntel));
            Next(376, "按情报键（默认 Y）打开情报面板");
        }

        private static void StepIntelPanelRows(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            IntelPanelUIToolkit panel = IntelPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            int row = -1;
            for (int i = 0; panel != null && i < panel.RowCount; i++)
            {
                if (panel.RowText(i).Contains("突袭预报") && panel.RowMapVisible(i))
                {
                    row = i;
                }
            }
            Check(IntelPanelUIToolkit.IsOpen && row >= 0 && panel.RowText(row).Contains("来自"), $"按情报键打开情报面板：突袭预报行“{FirstLine(row >= 0 ? panel.RowText(row) : null)}”有“在地图上查看”");
            Check(row >= 0 && InvokeClickable(panel.RowMapButton(row)), "点突袭预报行的“在地图上查看”");
            Next(377, "战略地图打开，对准来袭方向的箭头");
        }

        private static void StepIntelMap(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            StrategicMapUIToolkit map = StrategicMapUIToolkit.Instance;
            bool arrow = StrategicMapUIToolkit.IsOpen && !IntelPanelUIToolkit.IsOpen && map != null && map.Model.Items.Any(i => i.Kind == WorldMapItemKind.RaidForecast)
                         && map.Model.Lines.Any(l => l.Forecast);
            Check(arrow, "战略地图打开（情报面板已关），地图上有突袭预报的来袭方向箭头与标签");
            StrategicMapUIToolkit.Close();
            // 清理测试捷径：撤走测试突袭部队与监听站（到达会触发紧急通知 / 自动暂停，影响后面的步骤），恢复倍速与暂停状态。
            IntelPanelUIToolkit.Close();
            ProductionPanelUIToolkit.Close();
            CampaignState state = CampaignSession.Current;
            string raidId = SessionState.GetString(K + "IntelRaid", string.Empty);
            state.Raids.InTransit = state.Raids.InTransit.Where(g => g.GroupId != raidId).ToArray();
            state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokePostId).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            GameClock.SetSpeed(SessionState.GetFloat(K + "IntelSpeed", 1f));
            GameClock.SetPaused(SessionState.GetInt(K + "IntelWasPaused", 0) == 1);
            Next(378, "FG5-RND-06：放一座建成的黑匣子陈列馆，家园一台机器阵亡（伤害夹具），左键点陈列馆");
        }

        // ── FG5-RND-06：黑匣子陈列馆与纪念墙（阵亡通知附黑匣子去向、建筑面板“陈列馆…”、黑匣子页、纪念墙页与按地点排序）──

        private const string SmokeGalleryId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_gallery";

        private static void StepBlackBoxPlaced(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            SessionState.SetInt(K + "BbWasPaused", GameClock.Paused ? 1 : 0);
            GameClock.SetPaused(true);
            // 核心附近按地形找一块能放、接得上电网的空地，登记一座建成的陈列馆（测试捷径：机器施工由施工自检覆盖），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            string builtId = null;
            for (int r = 6; r <= 30 && builtId == null; r++)
            {
                for (int dy = -r; dy <= r && builtId == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtId == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!HomeGridService.ValidatePlacement(state, Campaign.Economy.BlackBoxService.TypeId, c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        AddSmokeBuilding(state, Campaign.Economy.BlackBoxService.TypeId, "gallery", c);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                        BuildingRecord b = HomeGridService.FindBuilding(state, SmokeGalleryId);
                        if (b != null && b.PowerState == BuildingPowerState.Powered)
                        {
                            b.Health = Campaign.Economy.BuildingOps.MaxDurability(Campaign.Economy.BlackBoxService.TypeId);
                            builtId = SmokeGalleryId;
                            continue;
                        }
                        state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeGalleryId).ToArray();
                        HomeGridService.MapFor(state);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    }
                }
            }
            // 伤害夹具：家园还没有正式突袭（FG6-DEF-04），在核心旁登记一台测试机器，伤害走生产代码的唯一伤害入口（归零即阵亡）。
            Vector2 at = Campaign.Regions.HomeValleyLayout.Core.Position + new Vector2(3f, -6f);
            MachineOpResult spawned = MachineRegistry.SpawnMachine(Campaign.Regions.HomeValleyLayout.Erc001ChassisId, Campaign.Regions.HomeValleyLayout.BlueprintErc001Id,
                Campaign.Regions.HomeValleyLayout.RegionId, at, 100f, 100f);
            int victim = spawned.Success ? spawned.LogicId : 0;
            if (victim > 0)
            {
                MachineNaming.TryRename(victim, "Smoke纪念", out _);
                MachineRegistry.ApplyDamage(victim, 1e6f);
            }
            SessionState.SetInt(K + "BbVictim", victim);
            string who = victim > 0 ? MachineNaming.Short(victim) : "?";
            string note = Notifications.NotificationCenter.History.Where(e => e.Type.Id == "machine_destroyed")
                .SelectMany(e => e.Members).Select(m => m.DetailText).LastOrDefault(t => t.StartsWith(who + " ", StringComparison.Ordinal)) ?? string.Empty;
            Check(builtId != null && victim > 0 && note.Contains("黑匣子已回收，送往黑匣子陈列馆分析"),
                $"接得上电网的建成陈列馆 {builtId}；家园机器 {who} 阵亡（伤害夹具）→ 阵亡通知附黑匣子去向“{note}”");
            // 放开 2 游戏秒让陈列馆开始分析（世界照常推进），再点陈列馆。
            GameClock.SetPaused(false);
            BuildingRecord built = HomeGridService.FindBuilding(state, SmokeGalleryId);
            if (built != null)
            {
                ClickWorld(new Vector3(built.Position.x, 0f, built.Position.y));
            }
            Next(379, "左键点陈列馆打开通用面板，点“陈列馆…”");
        }

        private static void StepBlackBoxBuildingPanel(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            GameClock.SetPaused(true);
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && ProductionPanelUIToolkit.BuildingId == SmokeGalleryId && bp.BlackBoxButton != null
                          && ProductionPanelUIToolkit.Visible(bp.BlackBoxButton) && bp.ReasonText.Contains("分析中") && bp.ReasonText.Contains("Smoke纪念");
            Check(bpOpen, $"左键点建成的陈列馆打开它的通用面板：状态“{bp?.ReasonText}”，有“陈列馆…”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrBlackBox"), "通用面板上点“陈列馆…”");
            Next(380, "陈列馆面板打开（黑匣子页：分析中），点“纪念墙”页签");
        }

        private static void StepBlackBoxOpened(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            BlackBoxPanelUIToolkit panel = BlackBoxPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            int victim = SessionState.GetInt(K + "BbVictim", 0);
            int row = -1;
            for (int i = 0; panel != null && i < panel.RowCount; i++)
            {
                if (panel.RowLogicId(i) == victim)
                {
                    row = i;
                }
            }
            bool open = BlackBoxPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.CurrentTab == BlackBoxPanelUIToolkit.TabBoxes
                        && LabelText("[BlackBoxPanelHost]", "BlackBoxTitle") == Localization.GameText.Get("blackbox.panel.title")
                        && row >= 0 && panel.RowText(row).Contains("分析中") && panel.StatusText.Contains("座陈列馆工作中");
            Check(open, $"陈列馆面板打开（黑匣子页）：“{FirstLine(row >= 0 ? panel.RowText(row) : null)}”；状态“{FirstLine(panel?.StatusText)}”");
            CheckNoTextMarkers("陈列馆面板（黑匣子页）");
            Check(ClickUitk("[BlackBoxPanelHost]", "BlackBoxTabMemorial"), "点“纪念墙”页签");
            Next(381, "纪念墙：阵亡机器的名字、编号、经历、阵亡地点；点“按地点”");
        }

        private static void StepBlackBoxMemorial(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            BlackBoxPanelUIToolkit panel = BlackBoxPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            int victim = SessionState.GetInt(K + "BbVictim", 0);
            string text = null;
            for (int i = 0; panel != null && i < panel.RowCount; i++)
            {
                if (panel.RowLogicId(i) == victim)
                {
                    text = panel.RowText(i);
                }
            }
            bool wall = BlackBoxPanelUIToolkit.IsOpen && panel.CurrentTab == BlackBoxPanelUIToolkit.TabMemorial && panel.SortBarVisible && text != null
                        && text.StartsWith("Smoke纪念 · 编号 #", StringComparison.Ordinal) && text.Contains("阵亡：") && text.Contains("经历：") && text.Contains("黑匣子：");
            Check(wall, $"纪念墙页：“{FirstLine(text)}”（名字 · 编号 · 型号、阵亡地点与时间、经历、黑匣子去向）");
            Check(ClickUitk("[BlackBoxPanelHost]", "BlackBoxSortPlace") && panel.SortByPlace, "点“按地点”排序");
            CheckNoTextMarkers("陈列馆面板（纪念墙页）");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(382, "Esc 关闭陈列馆面板；清理测试陈列馆");
        }

        private static void StepBlackBoxClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!BlackBoxPanelUIToolkit.IsOpen, "Esc 关闭陈列馆面板");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenRoster));
            Next(383, "按 N 打开机器名册，点“纪念墙…”");
        }

        private static void StepBlackBoxRosterOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.Kit.RosterPanelUIToolkit rop = UI.Kit.RosterPanelUIToolkit.Instance;
            rop?.Refresh();
            bool open = UI.Kit.RosterPanelUIToolkit.IsOpen && rop != null && rop.PanelVisible && rop.MemorialButton != null
                        && rop.MemorialButton.text == Localization.GameText.Get("blackbox.panel.memorial_open");
            Check(open, $"按 N 打开机器名册，有“{rop?.MemorialButton?.text}”按钮");
            Check(ClickUitk("[RosterPanelHost]", "RosterMemorial"), "机器名册上点“纪念墙…”");
            Next(384, "陈列馆面板直接打开在纪念墙页；Esc 关闭");
        }

        private static void StepBlackBoxRosterMemorial(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            BlackBoxPanelUIToolkit panel = BlackBoxPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            int victim = SessionState.GetInt(K + "BbVictim", 0);
            bool listed = false;
            for (int i = 0; panel != null && i < panel.RowCount; i++)
            {
                listed |= panel.RowLogicId(i) == victim && panel.RowText(i).StartsWith("Smoke纪念 · 编号 #", StringComparison.Ordinal);
            }
            bool wall = BlackBoxPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.CurrentTab == BlackBoxPanelUIToolkit.TabMemorial
                        && !UI.Kit.RosterPanelUIToolkit.IsOpen && listed;
            Check(wall, $"从机器名册点“纪念墙…”：名册关闭、陈列馆面板打开在纪念墙页，阵亡的测试机器在名单上（{listed}）");
            CheckNoTextMarkers("陈列馆面板（名册入口的纪念墙页）");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(385, "Esc 关闭纪念墙；清理测试陈列馆");
        }

        private static void StepBlackBoxRosterClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!BlackBoxPanelUIToolkit.IsOpen && !UI.Kit.RosterPanelUIToolkit.IsOpen, "Esc 关闭纪念墙（名册入口）");
            // 清理测试捷径：撤走测试陈列馆（阵亡的测试机器留在纪念墙上，黑匣子在队列里暂停），恢复暂停状态。
            ProductionPanelUIToolkit.Close();
            CampaignState state = CampaignSession.Current;
            state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeGalleryId).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            GameClock.SetPaused(SessionState.GetInt(K + "BbWasPaused", 0) == 1);
            Next(386, "FG6-DEF-01：建造菜单“防御”页签选轻型炮塔（建造栏炮塔蓝图下拉与射程）");
        }

        // ── FG6-DEF-01：炮塔（建造菜单“防御”页签 → 建造栏炮塔蓝图下拉与射程 → 悬停时地面射程圈 → 放下虚影 → 点建成的炮塔 → 建筑面板“炮塔…”→ 炮塔面板点目标模式 → Esc）──

        private const string SmokeTurretId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_turret";

        private static void StepTurretPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            SessionState.SetInt(K + "TurretWasPaused", GameClock.Paused ? 1 : 0);
            GameClock.SetPaused(true);
            // 核心附近按地形找一块放炮塔虚影的空地（玩家放置校验，含“有能装的炮塔蓝图”）；再登记一座接得上电网的建成炮塔（测试捷径：机器施工由 FgTurretSelfCheck B 段覆盖），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? ghostAt = null;
            string builtId = null;
            for (int r = 5; r <= 24 && (ghostAt == null || builtId == null); r++)
            {
                for (int dy = -r; dy <= r && (ghostAt == null || builtId == null); dy++)
                {
                    for (int dx = -r; dx <= r && (ghostAt == null || builtId == null); dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (ghostAt == null)
                        {
                            if (HomeGridService.ValidatePlacement(state, Campaign.Defense.TurretCatalog.LightTypeId, c, 0, checkCost: false).Ok)
                            {
                                ghostAt = c;
                            }
                            continue;
                        }
                        if (Math.Abs(c.X - ghostAt.Value.X) < 5 && Math.Abs(c.Y - ghostAt.Value.Y) < 5
                            || !HomeGridService.ValidatePlacement(state, Campaign.Defense.TurretCatalog.LightTypeId, c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        AddSmokeBuilding(state, Campaign.Defense.TurretCatalog.LightTypeId, "turret", c);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                        BuildingRecord b = HomeGridService.FindBuilding(state, SmokeTurretId);
                        if (b != null && b.PowerState == BuildingPowerState.Powered)
                        {
                            b.Health = Campaign.Economy.BuildingOps.MaxDurability(Campaign.Defense.TurretCatalog.LightTypeId);
                            builtId = SmokeTurretId;
                            continue;
                        }
                        state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeTurretId).ToArray();
                        HomeGridService.MapFor(state);
                        Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    }
                }
            }
            SessionState.SetInt(K + "TurretGhostX", ghostAt?.X ?? 0);
            SessionState.SetInt(K + "TurretGhostY", ghostAt?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "defense");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex(Campaign.Defense.TurretCatalog.LightTypeId);
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode != null && mode.SelectedTypeId == Campaign.Defense.TurretCatalog.LightTypeId;
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            hud?.Refresh();
            bool bpRow = hud != null && hud.TurretBlueprintVisible && hud.TurretBlueprintChoices.Count >= 1 && hud.TurretRangeText.Contains("18");
            Check(ghostAt.HasValue && builtId != null && tabClicked && picked && bpRow,
                $"点建造菜单“防御”页签里的“轻型炮塔”（第 {idx + 1} 项）：选中，建造栏出现炮塔蓝图下拉（{(hud != null ? string.Join(" / ", hud.TurretBlueprintChoices) : string.Empty)}）与“{hud?.TurretRangeText}”；接得上电网的建成炮塔 {builtId}");
            if (ghostAt.HasValue)
            {
                HoverWorld(new Vector3(ghostAt.Value.X, 0f, ghostAt.Value.Y));
            }
            SessionState.SetInt(K + "TurretSub", 0);
            Next(387, "悬停时地面射程圈；单击放下炮塔虚影");
        }

        private static void StepTurretPlaced(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            var ghostAt = new GridCell(SessionState.GetInt(K + "TurretGhostX", 0), SessionState.GetInt(K + "TurretGhostY", 0));
            BuildingRecord built = HomeGridService.FindBuilding(state, SmokeTurretId);
            int sub = SessionState.GetInt(K + "TurretSub", 0);
            if (sub == 0)
            {
                if (inStep < 0.6)
                {
                    return;
                }
                string bp = Campaign.Defense.TurretService.DefaultBlueprintFor(state, Campaign.Defense.TurretCatalog.SizeLight);
                float want = bp != null ? Campaign.Defense.TurretService.RangeOf(state, bp) : 0f;
                Check(View.TurretViews.RingShown && want > 0f && Mathf.Approximately(View.TurretViews.RingRadius, want),
                    $"放置时预览射程：鼠标悬停在空地上，地面射程圈半径 {View.TurretViews.RingRadius:F1} 米（蓝图射程 {want:F1}）");
                SessionState.SetInt(K + "TurretSub", 1);
                ClickWorld(new Vector3(ghostAt.X, 0f, ghostAt.Y));
                return;
            }
            if (sub == 1)
            {
                if (inStep < 1.3)
                {
                    return;
                }
                BuildingRecord ghost = HomeGridService.BuildingAt(state, ghostAt);
                Check(ghost != null && ghost.BuildingTypeId == Campaign.Defense.TurretCatalog.LightTypeId && Campaign.Regions.HomeValleyController.IsPlannedGhost(ghost),
                    $"单击放下轻型炮塔的虚影（2×2；状态行“{Campaign.Regions.HomeValleyBuildMode.Current?.StatusText}”）");
                SessionState.SetString(K + "TurretGhostId", ghost?.BuildingId ?? string.Empty);
                SessionState.SetInt(K + "TurretSub", 2);
                RightClickWorld(new Vector3(ghostAt.X, 0f, ghostAt.Y));
                return;
            }
            if (sub == 2)
            {
                if (inStep < 2.0)
                {
                    return;
                }
                Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "TurretSub", 3);
                ClickWorld(built != null ? new Vector3(built.Position.x, 0f, built.Position.y) : Vector3.zero);
                return;
            }
            if (inStep < 2.7)
            {
                return;
            }
            ProductionPanelUIToolkit bp2 = ProductionPanelUIToolkit.Instance;
            bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp2 != null && ProductionPanelUIToolkit.BuildingId == SmokeTurretId && bp2.TurretButton != null
                          && ProductionPanelUIToolkit.Visible(bp2.TurretButton) && !bp2.ReasonText.Contains("没有装炮塔蓝图");
            Check(bpOpen, $"左键点建成的炮塔打开它的通用面板：状态“{bp2?.ReasonText}”，有“炮塔…”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrTurret"), "通用面板上点“炮塔…”");
            SessionState.SetInt(K + "TurretSub", 0);
            Next(388, "炮塔面板打开：点一个目标模式按钮");
        }

        private static void StepTurretPanel(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            TurretPanelUIToolkit panel = TurretPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            bool open = TurretPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && TurretPanelUIToolkit.BuildingId == SmokeTurretId && panel.BlueprintChoices.Count > 0
                        && LabelText("[TurretPanelHost]", "TurretTitle").Contains(Localization.GameText.Get("building.turret_light.name")) && panel.ModeButton(0).text.StartsWith("● ", StringComparison.Ordinal)
                        && panel.WeaponText.Contains("射程") && panel.KillsText.Length > 0;
            float range = Campaign.Defense.TurretService.RangeOfTurret(state, SmokeTurretId);
            bool ring = View.TurretViews.RingShown && Mathf.Approximately(View.TurretViews.RingRadius, range);
            Check(open && ring, $"炮塔面板打开：“{panel?.TitleText}”，蓝图 {panel?.BlueprintChoices.Count} 张、五个目标模式按钮（“{panel?.ModeButton(0)?.text}”选中）、“{panel?.KillsText}”；地面射程圈 {View.TurretViews.RingRadius:F1} 米");
            CheckNoTextMarkers("炮塔面板");
            int slot = -1;
            for (int i = 0; i < 5 && panel != null; i++)
            {
                if (panel.ModeCodeAt(i) == (int)BinGames.Sim.Combat.CombatTargetMode.LowestHealth)
                {
                    slot = i;
                }
            }
            SessionState.SetInt(K + "TurretModeSlot", slot);
            Check(slot >= 0 && ClickUitk("[TurretPanelHost]", "TurretMode" + slot), "点“最低耐久”目标模式按钮");
            SessionState.SetInt(K + "TurretSub", 0);
            Next(389, "目标模式立即生效；测试炮塔换带接入口的蓝图、点“接入”");
        }

        private const string SmokeTurretPortBp = "smoke_turret_port";

        private static void StepTurretClosed(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            if (inStep < 0.5)
            {
                return;
            }
            TurretPanelUIToolkit panel = TurretPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            TurretRecord r = Campaign.Defense.TurretService.Find(state, SmokeTurretId);
            int slot = SessionState.GetInt(K + "TurretModeSlot", -1);
            Check(r != null && r.TargetMode == (int)BinGames.Sim.Combat.CombatTargetMode.LowestHealth && panel != null && slot >= 0
                  && panel.ModeButton(slot).text.StartsWith("● ", StringComparison.Ordinal) && panel.MessageText.Contains(Campaign.Defense.TurretCatalog.ModeName(1)),
                $"目标模式立即生效：炮塔记录 = {r?.TargetMode}，按钮“{(panel != null && slot >= 0 ? panel.ModeButton(slot).text : string.Empty)}”，消息“{panel?.MessageText}”");
            // FG6-DEF-01 审查修复（FGR-DEF-005 接入走正式入口）：测试捷径给测试炮塔装一张带接入口的固定底盘蓝图（电路编辑器里标接入口由 FG1 自检覆盖），
            // 然后点炮塔面板的“接入”按钮。
            Campaign.Blueprint.BlueprintCircuitBoard board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(Campaign.Content.CarrierReadings.FixedChassisId,
                Campaign.Content.ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            state.BlueprintRecords = (state.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(x => x.BlueprintId != SmokeTurretPortBp)
                .Append(new BlueprintRecord { BlueprintId = SmokeTurretPortBp, DisplayName = "冒烟接入炮塔", ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } }).ToArray();
            Campaign.Defense.TurretOpResult assigned = Campaign.Defense.TurretService.TryAssignBlueprint(state, SmokeTurretId, SmokeTurretPortBp);
            panel?.Refresh(force: true);
            bool enabled = panel != null && panel.UplinkButton != null && panel.UplinkButton.enabledSelf && panel.UplinkButton.text == Localization.GameText.Get("turret.panel.uplink");
            Check(assigned.Ok && enabled, $"测试炮塔换成带接入口的蓝图（{assigned.Message}）：炮塔面板的“{panel?.UplinkButton?.text}”按钮可点");
            Check(ClickUitk("[TurretPanelHost]", "TurretUplink"), "点炮塔面板的“接入”按钮");
            SessionState.SetInt(K + "TurretSub", 0);
            Next(390, "FG6-DEF-01：信号进入炮塔；Esc 关闭炮塔面板");
        }

        private static GameLogic.Campaign.Combat.CombatSite SmokeHomeSite =>
            WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static void StepTurretUplinked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            TurretPanelUIToolkit panel = TurretPanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            BuildingRecord b = HomeGridService.FindBuilding(state, SmokeTurretId);
            string stCode = b != null ? Campaign.Economy.BuildingStatusService.Evaluate(state, b).ReasonCode : string.Empty;
            string stText = b != null ? Campaign.Economy.BuildingStatusService.Evaluate(state, b).Reason : string.Empty;
            bool up = Campaign.Defense.TurretUplink.IsUplinkedTo(state, SmokeTurretId) && panel != null
                      && panel.UplinkButton.text == Localization.GameText.Get("turret.panel.leave") && stCode == "turret.uplinked";
            Check(up, $"信号进入炮塔：按钮变成“{panel?.UplinkButton?.text}”，状态“{stText}”，HUD“{Campaign.Signal.SignalUplinkService.StatusLine(state)}”");
            // 测试夹具：炮塔射程内放一个不动、不开火的敌方目标（突袭导演在 FG6-DEF-04），放在炮塔朝屏幕中心的一侧（镜头里、不被界面挡住）。
            GameLogic.Campaign.Combat.CombatSite site = SmokeHomeSite;
            Vector2 at = b != null ? b.Position : Vector2.zero;
            Vector2 dir = Vector2.right;
            Camera cam = Camera.main;
            if (cam != null && new Plane(Vector3.up, Vector3.zero).Raycast(cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f)), out float enter))
            {
                Vector3 mid = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f)).GetPoint(enter);
                Vector2 toMid = new Vector2(mid.x, mid.z) - at;
                if (toMid.sqrMagnitude > 1f)
                {
                    dir = toMid.normalized;
                }
            }
            Vector2 ep = at + dir * 5f;
            int enemy = site != null ? site.Kernel.Spawn(new BinGames.Sim.Combat.CombatSpawn
            {
                ExtKey = -1,
                Kind = BinGames.Sim.Combat.CombatUnitKind.Enemy,
                Faction = BinGames.Sim.Combat.CombatFaction.Hostile,
                Behavior = BinGames.Sim.Combat.CombatBehavior.None,
                Flags = BinGames.Sim.Combat.CombatUnitFlags.Alive | BinGames.Sim.Combat.CombatUnitFlags.Targetable | BinGames.Sim.Combat.CombatUnitFlags.Instanced
                        | BinGames.Sim.Combat.CombatUnitFlags.RemoveOnDeath,
                Position = new Unity.Mathematics.double2(ep.x, ep.y),
                Home = new Unity.Mathematics.double2(ep.x, ep.y),
                Radius = 0.5f,
                Health = 5000f,
                MaxHealth = 5000f,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            }) : 0;
            SessionState.SetInt(K + "TurretEnemy", enemy);
            SessionState.SetFloat(K + "TurretEnemyX", ep.x);
            SessionState.SetFloat(K + "TurretEnemyY", ep.y);
            Check(enemy > 0, $"测试夹具：炮塔射程内（5 米）放一个敌方目标（单位 {enemy}）");
            SessionState.SetInt(K + "TurretSub", 0);
            PressKeyKeepMouse(KeyCode.Escape);
            Next(391, "Esc 关闭炮塔面板；按建造菜单键关掉建造模式；战略暂停中左键朝目标开火（被拒）");
        }

        private static float SmokeEnemyHp()
        {
            GameLogic.Campaign.Combat.CombatSite site = SmokeHomeSite;
            int enemy = SessionState.GetInt(K + "TurretEnemy", 0);
            return site != null && site.Kernel.TryGetUnit(enemy, out BinGames.Sim.Combat.CombatUnitView v) && v.Alive ? v.Health : 0f;
        }

        private static Vector3 SmokeEnemyWorld() =>
            new Vector3(SessionState.GetFloat(K + "TurretEnemyX", 0f), 0f, SessionState.GetFloat(K + "TurretEnemyY", 0f));

        private static void StepTurretAimPaused(double inStep)
        {
            int sub = SessionState.GetInt(K + "TurretSub", 0);
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            if (sub == 0)
            {
                if (inStep < 0.5)
                {
                    return;
                }
                Check(!TurretPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 关闭炮塔面板（建造模式还开着）");
                ProductionPanelUIToolkit.Close(); // 通用面板不在本步覆盖范围（清理用测试捷径），免得挡住世界点击
                SessionState.SetInt(K + "TurretSub", 1);
                PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
                return;
            }
            if (sub == 1)
            {
                if (inStep < 1.0)
                {
                    return;
                }
                Check(mode == null || !mode.IsOpen, "按建造菜单键关掉建造模式（左键不再归建造模式）");
                SessionState.SetInt(K + "TurretPausedClicks0", Campaign.Defense.TurretUplink.PausedClicks);
                SessionState.SetInt(K + "TurretInputFires0", Campaign.Defense.TurretUplink.InputFires);
                SessionState.SetFloat(K + "TurretEnemyHp0", SmokeEnemyHp());
                SessionState.SetInt(K + "TurretSub", 2);
                ClickWorld(SmokeEnemyWorld());
                return;
            }
            if (inStep < 1.6)
            {
                return;
            }
            int paused0 = SessionState.GetInt(K + "TurretPausedClicks0", 0);
            int fires0 = SessionState.GetInt(K + "TurretInputFires0", 0);
            float hp0 = SessionState.GetFloat(K + "TurretEnemyHp0", 0f);
            Check(GameClock.Paused && Campaign.Defense.TurretUplink.PausedClicks == paused0 + 1 && Campaign.Defense.TurretUplink.InputFires == fires0
                  && Mathf.Approximately(SmokeEnemyHp(), hp0) && Campaign.Defense.TurretService.LastFeedback.Contains("暂停"),
                $"战略暂停中左键朝目标开火：被吃掉、不开火（目标耐久 {hp0:F0} → {SmokeEnemyHp():F0}），提示“{Campaign.Defense.TurretService.LastFeedback}”");
            GameClock.SetPaused(false);
            SessionState.SetFloat(K + "TurretEnemyHp0", SmokeEnemyHp());
            SessionState.SetInt(K + "TurretSub", 0);
            ClickWorld(SmokeEnemyWorld());
            Next(392, "继续游戏后左键朝目标开火（亲自瞄准）");
        }

        private static void StepTurretAimFired(double inStep)
        {
            if (inStep < 1.2)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int fires0 = SessionState.GetInt(K + "TurretInputFires0", 0);
            float hp0 = SessionState.GetFloat(K + "TurretEnemyHp0", 0f);
            Check(Campaign.Defense.TurretUplink.InputFires == fires0 + 1 && Campaign.Defense.TurretUplink.LastFireResult == BinGames.Sim.Combat.CombatFireResult.Ok
                  && SmokeEnemyHp() < hp0 && Campaign.Defense.TurretUplink.IsUplinkedTo(state, SmokeTurretId),
                $"左键亲自瞄准开火：开火结果 {Campaign.Defense.TurretUplink.LastFireResult}，目标耐久 {hp0:F0} → {SmokeEnemyHp():F0}（弹体命中）");
            PressKey(GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView));
            SessionState.SetInt(K + "TurretSub", 0);
            Next(393, $"按接入键（{GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView)}）离开炮塔");
        }

        private static void StepTurretLeft(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            if (SessionState.GetInt(K + "TurretSub", 0) == 0)
            {
                if (inStep < 0.6)
                {
                    return;
                }
                Check(!Campaign.Defense.TurretUplink.IsActive && Campaign.Defense.TurretService.LastFeedback.Contains("离开炮塔")
                      && Campaign.Signal.SignalUplinkService.CurrentMachine(state) == 0,
                    $"按接入键离开炮塔：信号回到归还核心（“{Campaign.Defense.TurretService.LastFeedback}”），镜头留在战略视角");
                // 清理测试夹具：撤走测试目标；恢复暂停、重新打开建造模式（后面的步骤从建造模式开始）。
                GameLogic.Campaign.Combat.CombatSite site = SmokeHomeSite;
                if (site != null)
                {
                    GameLogic.Campaign.Combat.CombatBench.ClearPrototypeUnits(site);
                }
                GameClock.SetPaused(true);
                SessionState.SetInt(K + "TurretSub", 1);
                PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
                return;
            }
            if (inStep < 1.2)
            {
                return;
            }
            Check(Campaign.Regions.HomeValleyBuildMode.Current != null && Campaign.Regions.HomeValleyBuildMode.Current.IsOpen, "按建造菜单键重新打开建造模式");
            // 清理测试捷径：撤走测试炮塔、取消炮塔虚影（不让后面的步骤里多出一座会开火的炮塔），恢复暂停状态。
            ProductionPanelUIToolkit.Close();
            string ghostId = SessionState.GetString(K + "TurretGhostId", string.Empty);
            if (!string.IsNullOrEmpty(ghostId) && HomeGridService.FindBuilding(state, ghostId) != null)
            {
                HomeGridService.TryToggleDemolish(state, ghostId);
            }
            state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeTurretId).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            Campaign.Defense.TurretService.Sync(state);
            Check(HomeGridService.FindBuilding(state, SmokeTurretId) == null && (string.IsNullOrEmpty(ghostId) || HomeGridService.FindBuilding(state, ghostId) == null)
                  && Campaign.Defense.TurretService.Find(state, SmokeTurretId) == null,
                "清理测试炮塔与虚影（炮塔记录随建筑一起清掉）");
            GameClock.SetPaused(SessionState.GetInt(K + "TurretWasPaused", 0) == 1);
            SessionState.SetInt(K + "TurretSub", 0);
            Next(394, "FG6-DEF-02：建造菜单“防御”页签选屏障 T1；悬停时地面画出敌方来路预览");
        }

        // ── FG6-DEF-02：屏障拖拽与来路预览、护盾面板（真实鼠标 / 按键；放下的是虚影，机器施工由 FgDefenseStructuresSelfCheck 覆盖）──

        private const string SmokeShieldId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_shield";

        private static void StepWallPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            SessionState.SetInt(K + "WallWasPaused", GameClock.Paused ? 1 : 0);
            GameClock.SetPaused(true);
            // 测试捷径：研发树“防御”三个节点记为已研究（研究流程由 FgResearchSelfCheck 覆盖）。
            Campaign.Economy.ResearchService.CompleteForTests(state, "defense.barrier", "defense.shield", "defense.trap");
            // 核心附近按地形找一段 6 格都能放屏障的直线（玩家放置校验），不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? start = null;
            for (int r = 7; r <= 22 && start == null; r++)
            {
                for (int dy = -r; dy <= r && start == null; dy++)
                {
                    for (int dx = -r; dx <= r && start == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        Campaign.Defense.WallPlan plan = Campaign.Defense.DefenseService.PlanWall(state, Campaign.Defense.DefenseCatalog.BarrierT1, c, new GridCell(c.X + 5, c.Y));
                        if (plan.Ok)
                        {
                            start = c;
                        }
                    }
                }
            }
            SessionState.SetInt(K + "WallX", start?.X ?? 0);
            SessionState.SetInt(K + "WallY", start?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "defense");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex(Campaign.Defense.DefenseCatalog.BarrierT1);
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode != null && mode.SelectedTypeId == Campaign.Defense.DefenseCatalog.BarrierT1;
            Check(start.HasValue && tabClicked && picked, $"点建造菜单“防御”页签里的“屏障 T1”（第 {idx + 1} 项）：选中；找到一段 6 格可放的直线（{start}）");
            if (start.HasValue)
            {
                HoverWorld(new Vector3(start.Value.X, 0f, start.Value.Y));
            }
            SessionState.SetInt(K + "WallSub", 0);
            Next(395, "悬停时敌方来路预览线；按住左键拖一段墙");
        }

        private static void StepWallDragged(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            var start = new GridCell(SessionState.GetInt(K + "WallX", 0), SessionState.GetInt(K + "WallY", 0));
            var end = new GridCell(start.X + 5, start.Y);
            int sub = SessionState.GetInt(K + "WallSub", 0);
            if (sub == 0)
            {
                if (inStep < 0.7)
                {
                    return;
                }
                Check(mode != null && mode.HoverRoute != null && mode.HoverRoute.HasRoutes && mode.ActiveRouteLineCount > 0,
                    $"放置屏障时预览对敌方路线的影响：地面画出 {mode?.ActiveRouteLineCount} 条来路预览线，状态行“{mode?.HoverRoute?.Summary()}”");
                SessionState.SetInt(K + "WallCount0", state.BuildingRecords.Count(x => x.BuildingTypeId == Campaign.Defense.DefenseCatalog.BarrierT1));
                SessionState.SetInt(K + "WallSub", 1);
                DragWorld(new Vector3(start.X, 0f, start.Y), new Vector3(end.X, 0f, end.Y), 0);
                return;
            }
            if (sub == 1)
            {
                if (inStep < 1.6)
                {
                    return;
                }
                InputRouter.DebugSetReader(null);
                int placed = state.BuildingRecords.Count(x => x.BuildingTypeId == Campaign.Defense.DefenseCatalog.BarrierT1) - SessionState.GetInt(K + "WallCount0", 0);
                Check(placed == 6 && mode != null && mode.StatusText.Contains("6 座"),
                    $"按住左键拖一段墙：松开放下 {placed} 座屏障虚影（全有或全无、一步撤销）；状态行“{mode?.StatusText?.Replace("\n", " / ")}”");
                SessionState.SetInt(K + "WallSub", 2);
                RightClickWorld(new Vector3(end.X, 0f, end.Y + 3));
                return;
            }
            if (inStep < 2.3)
            {
                return;
            }
            Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
            // 测试捷径：放一座接得上电网的建成护盾发生器（机器施工与护盾状态机由自检覆盖），点它打开建筑面板。
            GridCell core = HomeGridService.CorePivot(state);
            string builtId = null;
            for (int r = 8; r <= 24 && builtId == null; r++)
            {
                for (int a = 0; a < 36 && builtId == null; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * r), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * r));
                    if (!HomeGridService.ValidatePlacement(state, Campaign.Defense.DefenseCatalog.ShieldTypeId, c, 0, checkCost: false).Ok)
                    {
                        continue;
                    }
                    AddSmokeBuilding(state, Campaign.Defense.DefenseCatalog.ShieldTypeId, "shield", c);
                    Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                    BuildingRecord b = HomeGridService.FindBuilding(state, SmokeShieldId);
                    if (b != null && b.PowerState == BuildingPowerState.Powered)
                    {
                        b.Health = Campaign.Economy.BuildingOps.MaxDurability(Campaign.Defense.DefenseCatalog.ShieldTypeId);
                        builtId = SmokeShieldId;
                        continue;
                    }
                    state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeShieldId).ToArray();
                    HomeGridService.MapFor(state);
                    Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
                }
            }
            Campaign.Defense.DefenseService.Sync(state);
            BuildingRecord shield = HomeGridService.FindBuilding(state, SmokeShieldId);
            Check(shield != null, $"测试捷径：一座接得上电网的建成护盾发生器（{builtId}）");
            SessionState.SetInt(K + "WallSub", 0);
            if (shield != null)
            {
                ClickWorld(new Vector3(shield.Position.x, 0f, shield.Position.y));
            }
            Next(396, "左键点护盾发生器打开建筑面板；点“护盾…”");
        }

        private static void StepShieldPanel(double inStep)
        {
            if (inStep < 0.7)
            {
                return;
            }
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            bool open = ProductionPanelUIToolkit.IsOpen && bp != null && ProductionPanelUIToolkit.BuildingId == SmokeShieldId && bp.DefenseButton != null
                        && ProductionPanelUIToolkit.Visible(bp.DefenseButton) && bp.DefenseButton.text == Localization.GameText.Get("defense.panel.open_shield");
            Check(open, $"左键点护盾发生器打开它的通用面板：状态“{bp?.ReasonText}”，有“{bp?.DefenseButton?.text}”按钮");
            Check(ClickUitk("[ProductionPanelHost]", "PrDefense"), "通用面板上点“护盾…”");
            Next(397, "防御面板显示护盾值、状态与倒计时；Esc 关闭并清理");
        }

        private static void StepShieldPanelShown(double inStep)
        {
            CampaignState state = CampaignSession.Current;
            if (SessionState.GetInt(K + "WallSub", 0) == 0)
            {
                if (inStep < 0.7)
                {
                    return;
                }
                DefensePanelUIToolkit panel = DefensePanelUIToolkit.Instance;
                panel?.Refresh(force: true);
                bool shown = DefensePanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && DefensePanelUIToolkit.BuildingId == SmokeShieldId && panel.ShieldSectionVisible
                             && !panel.TrapSectionVisible && panel.ShieldValueText.Contains("护盾值") && panel.ShieldCountdownText.Length > 0
                             && LabelText("[DefensePanelHost]", "DefenseTitle").Contains(Localization.GameText.Get("building.shield_gen.name"));
                Check(shown, $"防御面板：“{panel?.TitleText}”，“{panel?.ShieldValueText}”，“{panel?.ShieldCountdownText}”，耗电“{panel?.ShieldPowerText}”");
                CheckNoTextMarkers("防御面板");
                SessionState.SetInt(K + "WallSub", 1);
                PressKeyKeepMouse(KeyCode.Escape);
                return;
            }
            if (inStep < 1.3)
            {
                return;
            }
            Check(!DefensePanelUIToolkit.IsOpen, "Esc 关闭防御面板");
            // 清理测试捷径：撤走测试护盾、取消屏障虚影，恢复暂停状态。
            ProductionPanelUIToolkit.Close();
            foreach (BuildingRecord ghost in state.BuildingRecords.Where(x => x.BuildingTypeId == Campaign.Defense.DefenseCatalog.BarrierT1
                         && Campaign.Regions.HomeValleyController.IsPlannedGhost(x)).ToArray())
            {
                HomeGridService.TryToggleDemolish(state, ghost.BuildingId);
            }
            state.BuildingRecords = state.BuildingRecords.Where(x => x.BuildingId != SmokeShieldId).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            Campaign.Defense.DefenseService.Sync(state);
            Check(HomeGridService.FindBuilding(state, SmokeShieldId) == null && !state.BuildingRecords.Any(x => x.BuildingTypeId == Campaign.Defense.DefenseCatalog.BarrierT1)
                  && Campaign.Defense.DefenseService.Find(state, SmokeShieldId) == null,
                "清理测试护盾与屏障虚影（防御记录随建筑一起清掉）");
            GameClock.SetPaused(SessionState.GetInt(K + "WallWasPaused", 0) == 1);
            SessionState.SetInt(K + "WallSub", 0);
            Next(264, "FG3-LOG-04：建造模式里放分流器与地下传送带");
        }

        // ── FG3-LOG-04：分流器 / 地下传送带 / 节点面板（真实鼠标 / 按键；放下的是虚影，机器施工由自检覆盖；最后用测试捷径清理，保持 22 格测试带）──

        private static Campaign.Grid.GridCell NodeOrigin() =>
            new Campaign.Grid.GridCell(SessionState.GetInt(K + "NodeX", 0), SessionState.GetInt(K + "NodeY", 0));

        private static int HudItemIndex(string id)
        {
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            hud?.Refresh();
            for (int i = 0; hud != null && i < hud.ItemCount; i++)
            {
                if (hud.ItemId(i) == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void StepNodeBuildReady(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.IsOpen, "建造模式开着");
            // 核心附近（镜头里）按规则找一块 9×3 的空地，不写死坐标（B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? area = null;
            for (int r = 0; r <= 12 && area == null; r++)
            {
                for (int dy = -r; dy <= r && area == null; dy++)
                {
                    for (int dx = -r; dx <= r && area == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var o = new GridCell(core.X - 4 + dx, core.Y + 8 + dy);
                        bool ok = true;
                        for (int y = 0; y < 3 && ok; y++)
                        {
                            for (int x = 0; x < 9 && ok; x++)
                            {
                                ok = HomeGridService.ValidateBeltCell(state, new GridCell(o.X + x, o.Y + y)).Ok;
                            }
                        }
                        if (ok)
                        {
                            area = o;
                        }
                    }
                }
            }
            if (area == null)
            {
                Finish("核心附近找不到 9×3 的空地放分流器和地下传送带");
                return;
            }
            SessionState.SetInt(K + "NodeX", area.Value.X);
            SessionState.SetInt(K + "NodeY", area.Value.Y);
            int logisticsTab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            bool tab = ClickUitk("[BuildModeHudHost]", "BuildCat" + logisticsTab);
            int idx = HudItemIndex("splitter");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "splitter";
            Check(tab && picked, $"点“物流”页签里的“分流器”（第 {idx + 1} 项）：选中分流器（{mode?.SelectedToolId}）");
            HoverWorld(new Vector3(area.Value.X + 1, 0f, area.Value.Y + 1));
            Next(265, "鼠标移到空地上（放置预览）");
        }

        private static void StepNodePreview(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string hint = BuildModeHudUIToolkit.Instance?.HintLabelText ?? string.Empty;
            string cost = BuildModeHudUIToolkit.Instance?.CostLabelText ?? string.Empty;
            Check(mode != null && mode.ToolPreview != null && mode.ToolPreview.Ok && mode.ToolPreview.Kind == BeltNodeKind.Splitter && mode.ActiveArrowCount == 3
                  && hint.Contains("左右两口出") && cost.Contains("4"),
                $"放置预览：指着的格是可放的虚影，画出 3 个进出口箭头（后方进、左右出）；提示行“{hint}”，成本行“{cost}”");
            GridCell o = NodeOrigin();
            ClickWorld(new Vector3(o.X + 1, 0f, o.Y + 1));
            Next(266, "单击放下分流器虚影");
        }

        private static void StepNodeSplitterPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell o = NodeOrigin();
            bool planned = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new GridCell(o.X + 1, o.Y + 1), out PlannedBeltRecord plan, out _)
                           && plan.NodeKind == (int)BeltNodeKind.Splitter;
            Check(planned && mode.StatusText.Contains("分流器"), $"单击放下分流器虚影（状态行“{mode?.StatusText}”）");
            int idx = HudItemIndex("underground_t1");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "underground_t1";
            Check(picked, $"点“地下传送带 T1”：选中（{mode?.SelectedToolId}）");
            DragWorld(new Vector3(o.X, 0f, o.Y), new Vector3(o.X + 7, 0f, o.Y), 0);
            Next(267, "从入口按住左键向东拖 7 格（跨 6 格，超过 T1 的 4 格）");
        }

        private static void StepNodeUnderTooFar(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell o = NodeOrigin();
            bool none = !Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, o, out _, out _);
            Check(none && mode.StatusText.Contains("跨度超限") && mode.StatusText.Contains("最多跨 4 格"),
                $"负向“地下带跨度超限”：松开后不放，状态行写原因“{mode?.StatusText}”");
            DragWorld(new Vector3(o.X, 0f, o.Y), new Vector3(o.X + 5, 0f, o.Y), 0);
            Next(268, "改成向东拖 5 格（跨 4 格）");
        }

        private static void StepNodeUnderPlaced(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell o = NodeOrigin();
            bool planned = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, o, out PlannedBeltRecord plan, out _)
                           && Campaign.Regions.HomeValleyConstruction.IsUnderground(plan) && plan.Xs.Length == 2 && plan.Xs[1] == o.X + 5;
            Check(planned, $"拖 5 格：放下地下传送带虚影（入口 {o} → 出口 ({o.X + 5}, {o.Y})，两端一份规划；状态行“{mode?.StatusText}”）");
            // 测试捷径：一座已建成的分流器（机器施工由 FgBeltNodeSelfCheck F2 覆盖），用来点开节点面板。
            BeltOpResult r = BeltNetworkService.TryPlaceNode(state, new GridCell(o.X + 4, o.Y + 2), BeltDir.East, 2, BeltNodeKind.Splitter);
            Check(r.Ok, $"测试捷径：在 ({o.X + 4}, {o.Y + 2}) 放一座已建成的分流器（{r.Describe()}）");
            RightClickWorld(new Vector3(o.X + 4, 0f, o.Y + 2));
            Next(269, "右键取消选择");
        }

        private static void StepNodeDeselected(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.IsOpen && mode.SelectedToolId == null, "右键取消选择（建造模式还开着）");
            GridCell o = NodeOrigin();
            ClickWorld(new Vector3(o.X + 4, 0f, o.Y + 2));
            Next(270, "空闲时左键点已建成的分流器（打开节点面板）");
        }

        private static void StepNodePanelOpened(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            BeltNodePanelUIToolkit panel = BeltNodePanelUIToolkit.Instance;
            GridCell o = NodeOrigin();
            var cell = new GridCell(o.X + 4, o.Y + 2);
            bool open = BeltNodePanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.TitleText.Contains("分流器") && panel.SplitterSettingsVisible;
            if (open)
            {
                panel.RatioLField.value = "2"; // 与在下拉框里选“2”同一个值变化回调（选中即生效）
            }
            BeltNetworkService.TryGetNode(cell, out BeltNodeInfo n);
            Check(open && n.RatioL == 2 && n.RatioR == 1 && panel.DetailText.Contains("分流比例 左 : 右 = 2 : 1"),
                $"节点面板（真 UXML）：“{panel?.TitleText}”；左口份数选 2 → 分流器 {n.RatioL}:{n.RatioR}，读数行“{panel?.DetailText?.Split('\n')[0]}”");
            CheckNoTextMarkers("节点面板");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(271, "Esc 关闭节点面板");
        }

        private static void StepNodePanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!BeltNodePanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 只关掉节点面板，建造模式还开着");
            // 测试捷径清理：取消两份虚影、拆掉测试分流器（后面的存读档步骤按 22 格核对测试带）。
            GridCell o = NodeOrigin();
            HomeGridService.TryRemoveBelts(state, new List<GridCell> { new GridCell(o.X + 1, o.Y + 1), o });
            BeltNetworkService.TryRemove(state, new GridCell(o.X + 4, o.Y + 2), new List<ushort>());
            bool clean = !Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, o, out _, out _)
                         && !Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new GridCell(o.X + 5, o.Y), out _, out _)
                         && !BeltNetworkService.Kernel.HasCell(o.X + 4, o.Y + 2) && BeltNetworkService.Kernel.CellCount == SessionState.GetInt(K + "BeltCells", -1);
            Check(clean, $"清理：两份虚影取消（地下两端一起），测试分流器拆掉（内核回到 {BeltNetworkService.Kernel.CellCount} 格）");
            Next(272, "FG3-LOG-05：建造模式里放泵、管线、储罐");
        }

        // ── FG3-LOG-05：管线与流体（真实鼠标 / 按键；放下的是虚影，机器施工由 FgPipeSelfCheck F2 覆盖；面板用测试捷径建成的件；最后清理）──

        private static GridCell PipeOrigin() => new GridCell(SessionState.GetInt(K + "PipeX", 0), SessionState.GetInt(K + "PipeY", 0));

        private static GridCell PipeAlong(int i)
        {
            int d = SessionState.GetInt(K + "PipeDir", 0);
            GridCell o = PipeOrigin();
            return new GridCell(o.X + BeltDirs.Dx(d) * i, o.Y + BeltDirs.Dy(d) * i);
        }

        private static void StepPipeBuildReady(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.IsOpen, "建造模式开着");
            // 核心附近按地形找水源（泵能放）且朝某个方向能铺 4 格管线，不写死坐标（B25；起始区保证 24 格内有水源）。
            GridCell core = HomeGridService.CorePivot(state);
            int water = PipeNetworkService.FluidId("water");
            GridCell? site = null;
            int dir = 0;
            for (int r = 3; r <= 24 && site == null; r++)
            {
                for (int dy = -r; dy <= r && site == null; dy++)
                {
                    for (int dx = -r; dx <= r && site == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (PipeNetworkService.SourceFluidAt(state, c) != water || !HomeGridService.ValidatePipeCell(state, c, PipePieceKind.Pump).Ok)
                        {
                            continue;
                        }
                        for (int d = 0; d < 4 && site == null; d++)
                        {
                            bool ok = true;
                            for (int i = 1; i <= 4 && ok; i++)
                            {
                                var q = new GridCell(c.X + BeltDirs.Dx(d) * i, c.Y + BeltDirs.Dy(d) * i);
                                ok = HomeGridService.ValidatePipeCell(state, q, PipePieceKind.Pipe).Ok && PipeNetworkService.SourceFluidAt(state, q) == 0;
                            }
                            if (ok)
                            {
                                site = c;
                                dir = d;
                            }
                        }
                    }
                }
            }
            if (site == null)
            {
                Finish("核心 24 格内找不到能放泵的水源（旁边能铺 4 格管线）");
                return;
            }
            SessionState.SetInt(K + "PipeX", site.Value.X);
            SessionState.SetInt(K + "PipeY", site.Value.Y);
            SessionState.SetInt(K + "PipeDir", dir);
            int logisticsTab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            bool tab = ClickUitk("[BuildModeHudHost]", "BuildCat" + logisticsTab);
            int idx = HudItemIndex("pump");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "pump";
            Check(tab && picked, $"点“物流”页签里的“泵”（第 {idx + 1} 项）：选中泵（{mode?.SelectedToolId}）");
            HoverWorld(new Vector3(site.Value.X, 0f, site.Value.Y));
            Next(273, "鼠标移到水源上（放置预览）");
        }

        private static void StepPipePumpPreview(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string hint = BuildModeHudUIToolkit.Instance?.HintLabelText ?? string.Empty;
            Check(mode != null && mode.ToolPreview != null && mode.ToolPreview.Ok && mode.ToolPreview.IsPipe && mode.ToolPreview.Pipe == PipePieceKind.Pump
                  && mode.ToolPreview.PipeFluid == PipeNetworkService.FluidId("water") && hint.Contains("水源或油井"),
                $"放置预览：泵指着水源是可放的虚影（认定抽水）；提示行“{hint}”");
            GridCell o = PipeOrigin();
            ClickWorld(new Vector3(o.X, 0f, o.Y));
            Next(274, "单击把泵放在水源上");
        }

        private static void StepPipePumpPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            bool planned = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, PipeOrigin(), out PlannedBeltRecord plan, out _)
                           && plan.PipePiece == (int)PipePieceKind.Pump + 1;
            Check(planned && mode.StatusText.Contains("泵"), $"单击放下泵的虚影（状态行“{mode?.StatusText}”）");
            int idx = HudItemIndex("pipe_t1");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "pipe_t1";
            Check(picked, $"点“管线 T1”：选中（{mode?.SelectedToolId}）");
            GridCell a = PipeAlong(1);
            GridCell b = PipeAlong(3);
            DragWorld(new Vector3(a.X, 0f, a.Y), new Vector3(b.X, 0f, b.Y), 0);
            Next(275, "从泵旁边按住左键拖 3 格管线");
        }

        private static void StepPipeDragged(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            bool planned = Enumerable.Range(1, 3).All(i => Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, PipeAlong(i), out PlannedBeltRecord q, out _) && q.PipePiece == 1);
            Check(planned && mode.StatusText.Contains("管线 T1"), $"拖出 3 格管线虚影（状态行“{mode?.StatusText}”）");
            int idx = HudItemIndex("tank");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "tank";
            Check(picked, $"点“储罐”：选中（{mode?.SelectedToolId}）");
            GridCell t = PipeAlong(4);
            ClickWorld(new Vector3(t.X, 0f, t.Y));
            Next(276, "单击在管线末端放储罐");
        }

        private static void StepPipeTankPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool planned = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, PipeAlong(4), out PlannedBeltRecord plan, out _)
                           && plan.PipePiece == (int)PipePieceKind.Tank + 1;
            Check(planned, "单击放下储罐虚影");
            // 测试捷径：取消这些虚影，经正式入口直接建成（机器取料施工由 FgPipeSelfCheck F2 覆盖），用来点开管线面板。
            var cells = Enumerable.Range(0, 5).Select(PipeAlong).ToList();
            HomeGridService.TryRemoveBelts(state, cells);
            bool built = PipeNetworkService.TryPlace(state, PipeAlong(0), PipePieceKind.Pump, 0, 0).Ok;
            for (int i = 1; i <= 3; i++)
            {
                built &= PipeNetworkService.TryPlace(state, PipeAlong(i), PipePieceKind.Pipe, 0, 0).Ok;
            }
            built &= PipeNetworkService.TryPlace(state, PipeAlong(4), PipePieceKind.Tank, 0, 0).Ok;
            Check(built, "测试捷径：虚影取消后经正式入口放下已建成的泵、3 格管线、储罐");
            GridCell t = PipeAlong(4);
            RightClickWorld(new Vector3(t.X, 0f, t.Y));
            Next(277, "右键取消选择");
        }

        private static void StepPipeDeselected(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode != null && mode.IsOpen && mode.SelectedToolId == null, "右键取消选择（建造模式还开着）");
            PipeRenderer r = PipeNetworkService.Renderer;
            int water = PipeNetworkService.FluidId("water");
            bool drawn = r != null && r.LastInstances == 5 && r.LastPrepared != null && r.LastPrepared.Take(5).All(i => (int)i.Bx == water);
            Check(drawn, $"真实帧里管线被渲染：5 个实例，每个都按“水”着色（{(r != null && r.GpuAvailable ? "GPU 绘制" : "无图形设备：" + r?.GpuUnavailableReason)}）");
            GridCell t = PipeAlong(4);
            ClickWorld(new Vector3(t.X, 0f, t.Y));
            Next(278, "空闲时左键点储罐（打开管线面板）");
        }

        private static void StepPipePanelOpened(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            PipePanelUIToolkit panel = PipePanelUIToolkit.Instance;
            GridCell t = PipeAlong(4);
            bool open = PipePanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.TitleText.Contains("储罐") && panel.TankSettingsVisible
                        && panel.DetailText.Contains("网络：水");
            if (open)
            {
                panel.ModeField.value = panel.ModeField.choices[1]; // 与在下拉框里选“只进”同一个值变化回调（选中即生效）
            }
            PipeNetworkService.Kernel.TryGetCellInfo(t.X, t.Y, out PipeCellInfo info);
            Check(open && info.TankMode == PipeTankMode.InOnly, $"管线面板（真 UXML）：“{panel?.TitleText}”；储罐模式选“只进”→ 内核 {info.TankMode}；读数“{panel?.DetailText?.Split('\n')[0]}”");
            bool asked = ClickUitk("[PipePanelHost]", "PpFlush") && UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title.Contains("水") && UiConfirmDialog.Current.Irreversible;
            bool cancelled = ClickUitk("[UiKitOverlayHost]", "ConfirmCancel") && !UiConfirmDialog.IsOpen;
            PipeNetworkService.Kernel.TryGetCellInfo(t.X, t.Y, out PipeCellInfo after);
            Check(asked && cancelled && after.TankStockMl >= info.TankStockMl, $"点“冲洗网络”先弹确认框（不可逆），点取消后储罐存量不变（{after.TankStockMl / 1000.0} 升）");
            CheckNoTextMarkers("管线面板");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(279, "Esc 关闭管线面板");
        }

        private static void StepPipePanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!PipePanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 只关掉管线面板，建造模式还开着");
            bool hover = PipeNetworkService.TryDescribeHover(state, PipeAlong(2), out string title, out string body) && title.Contains("管线 T1") && body.Contains("网络：水")
                         && body.Contains("供给") && body.Contains("储量");
            Check(hover, $"悬停管线：{title} / {body.Split('\n')[0]} …");
            // 测试捷径清理：拆掉测试管线件（后面的步骤不受影响）。
            HomeGridService.TryRemoveBelts(state, Enumerable.Range(0, 5).Select(PipeAlong).ToList());
            Check(PipeNetworkService.Kernel.CellCount == 0, $"清理：测试管线件拆掉（内核剩 {PipeNetworkService.Kernel.CellCount} 格）");
            Next(280, "FG3-LOG-06：建造模式里放电塔");
        }

        // ── FG3-LOG-06：电力子网与电塔（真实鼠标 / 按键；放下的是虚影，机器施工由 FgPowerGridSelfCheck F3 覆盖；面板用测试捷径建成的电塔；最后清理）──

        private static Campaign.Grid.GridCell PowerCell(string key) => new Campaign.Grid.GridCell(SessionState.GetInt(K + key + "X", 0), SessionState.GetInt(K + key + "Y", 0));

        private static void StepPowerBuildReady(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.IsOpen, "建造模式开着");
            // 按格网规则找位置（B25，不写死坐标）：P1 离核心 20～25 格（在核心配电里），P2 从 P1 往外 7 格（只靠 P1 接入），发电机放在 P2 外侧。
            Campaign.Grid.GridCell core = HomeGridService.CorePivot(state);
            bool found = false;
            for (int a = 0; a < 72 && !found; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                for (float d = 20f; d <= 25f && !found; d += 1f)
                {
                    var p1 = new Campaign.Grid.GridCell(core.X + Mathf.RoundToInt(dir.x * d), core.Y + Mathf.RoundToInt(dir.y * d));
                    var p2 = new Campaign.Grid.GridCell(core.X + Mathf.RoundToInt(dir.x * (d + 7f)), core.Y + Mathf.RoundToInt(dir.y * (d + 7f)));
                    var gen = new Campaign.Grid.GridCell(core.X + Mathf.RoundToInt(dir.x * (d + 12f)), core.Y + Mathf.RoundToInt(dir.y * (d + 12f)));
                    if (HomeGridService.ValidatePlacement(state, "power_pole", p1, 0).Ok && HomeGridService.ValidatePlacement(state, "power_pole", p2, 0, checkCost: false).Ok
                        && HomeGridService.ValidatePlacement(state, Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2, gen, 0, checkCost: false).Ok)
                    {
                        SessionState.SetInt(K + "PowerP1X", p1.X);
                        SessionState.SetInt(K + "PowerP1Y", p1.Y);
                        SessionState.SetInt(K + "PowerP2X", p2.X);
                        SessionState.SetInt(K + "PowerP2Y", p2.Y);
                        SessionState.SetInt(K + "PowerGenX", gen.X);
                        SessionState.SetInt(K + "PowerGenY", gen.Y);
                        found = true;
                    }
                }
            }
            if (!found)
            {
                Finish("核心附近找不到放电塔链的位置");
                return;
            }
            int energyTab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "energy");
            bool tab = ClickUitk("[BuildModeHudHost]", "BuildCat" + energyTab);
            int idx = HudItemIndex("power_pole");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "power_pole";
            Check(tab && picked, $"点“能源”页签里的“电塔 T1”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）");
            Campaign.Grid.GridCell at = PowerCell("PowerP1");
            HoverWorld(new Vector3(at.X, 0f, at.Y));
            Next(281, "鼠标移到核心配电边上（放置预览）");
        }

        private static void StepPowerPreview(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Campaign.Grid.GridPlacementResult pv = mode?.Preview;
            string status = LabelText("[BuildModeHudHost]", "BuildStatus");
            Check(pv != null && pv.Ok && pv.Notes.Any(n => n.Contains("接入 电网 1")) && status.Contains("接入 电网 1") && status.Contains("覆盖"),
                $"放置预览写会接入哪个电网、覆盖几座建筑（状态行“{status.Replace("\n", " / ")}”）");
            Check(GameLogic.View.PowerCoverageOverlayView.Visible && GameLogic.View.PowerCoverageOverlayView.PreviewShown
                  && Mathf.Approximately(GameLogic.View.PowerCoverageOverlayView.PreviewRadius, 8f) && GameLogic.View.PowerCoverageOverlayView.DrawnRings >= 1,
                $"真实帧里电力覆盖叠加层自动显示：核心的覆盖圈 {GameLogic.View.PowerCoverageOverlayView.DrawnRings} 个 + 光标处半径 {GameLogic.View.PowerCoverageOverlayView.PreviewRadius} 的预览圈");
            Campaign.Grid.GridCell at = PowerCell("PowerP1");
            ClickWorld(new Vector3(at.X, 0f, at.Y));
            Next(282, "单击放下电塔");
        }

        private static void StepPowerPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Campaign.Grid.GridCell p1 = PowerCell("PowerP1");
            BuildingRecord ghost = HomeGridService.BuildingAt(state, p1);
            bool planned = ghost != null && ghost.BuildingTypeId == "power_pole" && ghost.ConstructionState != BuildingConstructionState.Operational;
            Check(planned, $"单击放下电塔虚影（{ghost?.ConstructionState}；状态行“{mode?.StatusText}”）");
            // 测试捷径：取消虚影（全额退回），直接放两座已建成的电塔与一台发电机（机器施工由 FgPowerGridSelfCheck F3 覆盖）。
            if (ghost != null)
            {
                HomeGridService.TryToggleDemolish(state, ghost.BuildingId);
            }
            AddSmokeBuilding(state, "power_pole", "p1", p1);
            AddSmokeBuilding(state, "power_pole", "p2", PowerCell("PowerP2"));
            AddSmokeBuilding(state, Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2, "gen", PowerCell("PowerGen"));
            Campaign.Regions.HomeValleyPowerGrid.GridSummary sum = Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            Check(sum.SubnetCount == 1 && sum.UnconnectedBuildingIds.Length == 0, $"测试捷径：两座已建成的电塔与 P2 外侧的发电机连成一个电网（发电 {sum.TotalSupply}）");
            RightClickWorld(new Vector3(p1.X, 0f, p1.Y));
            SessionState.SetInt(K + "PowerSplit0", Notifications.NotificationCenter.History.Where(n => n.Type?.Id == "power_split").Sum(n => n.Count));
            Next(283, "右键取消选择后按 Alt+G 打开电网面板");
        }

        private static void AddSmokeBuilding(CampaignState state, string typeId, string key, Campaign.Grid.GridCell pivot)
        {
            GameConfig.fg.BuildingGrid g = Campaign.Grid.GridContent.Building(typeId);
            var r = new BuildingRecord
            {
                BuildingId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_" + key,
                BuildingTypeId = typeId,
                RegionId = Campaign.Regions.HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = Campaign.Grid.GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = 1,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            state.BuildingRecords = (state.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(state);
        }

        private static void StepPowerPanelOpened(double inStep)
        {
            if (inStep < 0.3)
            {
                return;
            }
            if (!SessionState.GetBool(K + "PowerKeySent", false))
            {
                SessionState.SetBool(K + "PowerKeySent", true);
                PressChord(GameSettings.KeyBindings.GetChord(GameActionId.OpenPowerGrid));
                return;
            }
            if (inStep < 1.0)
            {
                return;
            }
            SessionState.SetBool(K + "PowerKeySent", false);
            CampaignState state = CampaignSession.Current;
            PowerPanelUIToolkit panel = PowerPanelUIToolkit.Instance;
            bool open = PowerPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.SummaryText.Contains("1 个电网") && panel.DetailText.Contains("发电")
                        && GameLogic.View.PowerCoverageOverlayView.Visible && GameLogic.View.PowerCoverageOverlayView.DrawnRings >= 3;
            Check(open, $"Alt+G 打开电网面板（真 UXML）：“{panel?.SummaryText}”；面板开着时叠加层显示 {GameLogic.View.PowerCoverageOverlayView.DrawnRings} 个覆盖圈（核心 + 两座电塔）");
            bool overlayOn = ClickUitk("[PowerPanelHost]", "PwOverlay") && GameLogic.View.PowerCoverageOverlayView.Enabled;
            Check(overlayOn, "点“显示电力覆盖”：叠加层开关打开");
            CheckNoTextMarkers("电网面板");
            // 测试捷径：P1 被摧毁（正式损毁来源在 FG6）→ 电网一分为二、发“电网断开”警告，面板跟着刷新。
            BuildingRecord p1 = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_p1");
            Campaign.Regions.HomeValleyPowerGrid.ApplyBuildingDestroyed(state, p1?.BuildingId);
            panel?.Refresh(force: true);
            int splits = Notifications.NotificationCenter.History.Where(n => n.Type?.Id == "power_split").Sum(n => n.Count);
            Check(splits == SessionState.GetInt(K + "PowerSplit0", 0) + 1 && panel != null && panel.SummaryText.Contains("2 个电网") && panel.GridField.choices.Count == 2,
                $"电塔被摧毁一分为二：发“电网断开”警告（共 {splits} 条），面板刷新为“{panel?.SummaryText}”");
            PressChord(GameSettings.KeyBindings.GetChord(GameActionId.OpenPowerGrid));
            Next(284, "再按 Alt+G 关闭电网面板");
        }

        private static void StepPowerPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!PowerPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Alt+G 关掉电网面板，建造模式还开着");
            BuildingRecord p2 = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_p2");
            string hover = p2 != null ? Campaign.Regions.HomeValleyBuildMode.DescribeBuilding(state, p2) : string.Empty;
            Check(hover.Contains("电网 2") && hover.Contains("节点"), $"建造模式指着电塔的状态行写它属于哪个电网（“{hover.Split('\n').Skip(1).FirstOrDefault()}”）");
            // 测试捷径清理：去掉测试建筑，叠加层开关复位（后面的步骤不受影响）。
            state.BuildingRecords = state.BuildingRecords.Where(b => !b.BuildingId.Contains(":smoke_power_")).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.GridSummary sum = Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            GameLogic.View.PowerCoverageOverlayView.SetEnabled(false);
            Check(sum.SubnetCount == 1 && !GameLogic.View.PowerCoverageOverlayView.Enabled, $"清理：测试电塔与发电机拆掉（剩 {sum.SubnetCount} 个电网）");
            Next(285, "FG3-LOG-07：规划工具（复制粘贴 / 撤销重做 / 升级 / 吸管 / 布局库）");
        }

        // ── FG3-LOG-07：规划工具（真实鼠标 / 按键；源场景用测试捷径放已建成的传送带与分流器，施工由 FgPlanningToolsSelfCheck 覆盖；最后撤销并清理）──

        private static Campaign.Grid.GridCell PlanCell(string key) => new Campaign.Grid.GridCell(SessionState.GetInt(K + key + "X", 0), SessionState.GetInt(K + key + "Y", 0));

        private static Vector3 PlanWorld(string key, int dx = 0, int dy = 0)
        {
            Campaign.Grid.GridCell c = PlanCell(key);
            return new Vector3(c.X + dx, 0f, c.Y + dy);
        }

        private static void PressChordKeepMouse(InputChord chord)
        {
            KeyCode held = (chord.Mods & InputModifier.Ctrl) != 0 ? KeyCode.LeftControl
                : (chord.Mods & InputModifier.Alt) != 0 ? KeyCode.LeftAlt
                : (chord.Mods & InputModifier.Shift) != 0 ? KeyCode.LeftShift
                : KeyCode.None;
            KeyCode held2 = (chord.Mods & InputModifier.Ctrl) != 0 && (chord.Mods & InputModifier.Alt) != 0 ? KeyCode.LeftAlt : KeyCode.None;
            InputRouter.DebugSetReader(new ScriptedReader { Key = chord.Key, KeyFrame = Time.frameCount + 1, Held = held, Held2 = held2, Mouse = _buildMouse });
        }

        private static bool PlanRowFree(CampaignState state, Campaign.Grid.GridCell o, int w, int h)
        {
            for (int y = -1; y <= h; y++)
            {
                for (int x = -1; x <= w; x++)
                {
                    var c = new Campaign.Grid.GridCell(o.X + x, o.Y + y);
                    if (!HomeGridService.ValidateBeltCell(state, c).Ok || !HomeGridService.ValidatePlacement(state, "power_pole", c, 0, checkCost: false).Ok)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void StepPlanReady(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.IsOpen, "建造模式开着");
            // 布局库写到临时目录（不碰这台机器上玩家真实的布局库）。
            LayoutLibrary.DirectoryOverrideForTests = Path.Combine(Path.GetTempPath(), "bingames-smoke-layouts-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            LayoutLibrary.Reload();
            // 按格网规则找两块空地（B25）：源（4×1）在核心东边 12～20 格，目标（5×5）再往外。
            Campaign.Grid.GridCell core = HomeGridService.CorePivot(state);
            bool found = false;
            for (int a = 0; a < 72 && !found; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = 12f; d <= 20f && !found; d += 1f)
                {
                    var src = new Campaign.Grid.GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    var dst = new Campaign.Grid.GridCell(src.X, src.Y + 5);
                    if (PlanRowFree(state, src, 4, 1) && PlanRowFree(state, new Campaign.Grid.GridCell(dst.X - 1, dst.Y), 5, 5))
                    {
                        SessionState.SetInt(K + "PlanSrcX", src.X);
                        SessionState.SetInt(K + "PlanSrcY", src.Y);
                        SessionState.SetInt(K + "PlanDstX", dst.X + 1);
                        SessionState.SetInt(K + "PlanDstY", dst.Y + 2);
                        found = true;
                    }
                }
            }
            if (!found)
            {
                Finish("核心附近找不到放规划工具场景的空地");
                return;
            }
            // 测试捷径：3 格已建成的传送带（朝东）+ 一个 3:1 的分流器（经传送带服务的正式“建成”入口）。
            Campaign.Grid.GridCell s = PlanCell("PlanSrc");
            bool laid = true;
            for (int x = 0; x < 3; x++)
            {
                laid &= BeltNetworkService.TryPlace(state, new Campaign.Grid.GridCell(s.X + x, s.Y), BeltDir.East, 0).Ok;
            }
            var sp = new Campaign.Grid.GridCell(s.X + 3, s.Y);
            laid &= BeltNetworkService.TryPlaceNode(state, sp, BeltDir.East, 2, BeltNodeKind.Splitter).Ok
                    && BeltNetworkService.TrySetSplitter(state, sp, 3, 1, BeltSide.Left, BeltConst.FilterAny, BeltConst.FilterAny).Ok;
            Check(laid, "测试捷径：3 格传送带 + 一个 3:1 优先左口的分流器（已建成）");
            SessionState.SetInt(K + "PlanUndo0", PlanHistory.UndoSteps(state));
            HoverWorld(PlanWorld("PlanSrc"));
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.Copy));
            Next(286, "按复制键（默认 Ctrl+C）进入复制模式");
        }

        private static void StepPlanCopyMode(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            string modeText = LabelText("[BuildModeHudHost]", "BuildMode");
            Check(mode != null && mode.CopyMode && modeText.Contains("复制"), $"Ctrl+C 进入复制模式（建造栏写“{modeText}”）");
            DragWorld(PlanWorld("PlanSrc"), PlanWorld("PlanSrc", 3, 0), 0);
            Next(287, "按住左键拖框框住传送带与分流器");
        }

        private static void StepPlanCopied(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            PlanEntryBlock clip = Campaign.Regions.HomeValleyBuildMode.Clipboard;
            int split = clip != null ? Array.IndexOf(clip.Ids, "splitter") : -1;
            Check(mode.PasteMode && PlanEntries.CountOf(clip) == 4 && split >= 0 && clip.S0[split] == PlanSettings.PackSplitter(3, 1, 1),
                $"松开：复制了 {PlanEntries.CountOf(clip)} 件（连同分流器 3:1 优先左口的设置），直接进入粘贴（状态行“{mode.StatusText.Split('\n').FirstOrDefault()}”）");
            HoverWorld(PlanWorld("PlanDst"));
            Next(288, "鼠标移到目标处（粘贴预览跟着鼠标）");
        }

        private static void StepPlanPreview(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            PastePlan pv = mode.PastePreview;
            string info = LabelText("[BuildModeHudHost]", "BuildDragInfo");
            Check(pv != null && pv.OkCount == 4 && pv.BadCount == 0 && info.Contains("能放 4 件") && mode.ActiveTileCount == 4,
                $"真实帧里粘贴预览：4 件都能放（绿格 {mode.ActiveTileCount} 个），建造栏写“{info.Split('\n').FirstOrDefault()}”");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.Rotate));
            Next(289, "按旋转键（默认 R）整体转 90°");
        }

        private static void StepPlanRotated(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            PastePlan pv = mode.PastePreview;
            bool vertical = pv != null && pv.Items.Select(i => i.Cell.X).Distinct().Count() == 1 && pv.Items.Select(i => i.Cell.Y).Distinct().Count() == 4
                            && pv.Items.All(i => i.Rot == (int)BeltDir.South);
            Check(mode.PasteQuarter == 1 && pv != null && pv.OkCount == 4 && vertical, "旋转后布局变成竖的一列、每件都朝南，4 件都能放");
            ClickWorld(PlanWorld("PlanDst"));
            Next(290, "左键放下");
        }

        private static void StepPlanPasted(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            PlanApplyResult r = mode.LastPaste;
            Campaign.Grid.GridCell d = PlanCell("PlanDst");
            int planned = Enumerable.Range(-2, 5).Count(dy => Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, new Campaign.Grid.GridCell(d.X, d.Y + dy), out _, out _));
            Check(r != null && r.PlacedPieces == 4 && planned == 4 && PlanHistory.PeekUndo(state) == PlanStepKind.Paste,
                $"左键放下 4 件虚影（竖着一列，规划 {planned} 格），整次粘贴是撤销栈里的一步（状态行“{mode.StatusText.Split('\n').FirstOrDefault()}”）");
            SessionState.SetInt(K + "PlanPlanned", Campaign.Regions.HomeValleyConstruction.PlannedCellCount(state));
            RightClickWorld(PlanWorld("PlanDst"));
            Next(291, "右键退出粘贴");
        }

        private static void StepPlanPasteExited(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!mode.PasteMode && mode.IsOpen, "右键退出粘贴，建造模式还开着");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.Undo));
            Next(292, "按撤销键（默认 Ctrl+Z）");
        }

        private static void StepPlanUndone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            int planned = Campaign.Regions.HomeValleyConstruction.PlannedCellCount(state);
            Check(mode.LastStep != null && mode.LastStep.Done && planned == SessionState.GetInt(K + "PlanPlanned", 0) - 4 && mode.StatusText.Contains("已撤销")
                  && LabelText("[BuildModeHudHost]", "BuildUndoHint").Contains("重做"),
                $"Ctrl+Z 撤销整次粘贴：4 件虚影取消（状态行“{mode.StatusText}”）；撤销提示行写下一步撤销 / 重做的是什么");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.Redo));
            Next(293, "按重做键（默认 Ctrl+Y）");
        }

        private static void StepPlanRedone(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode.LastStep != null && mode.LastStep.Redo && Campaign.Regions.HomeValleyConstruction.PlannedCellCount(state) == SessionState.GetInt(K + "PlanPlanned", 0),
                $"Ctrl+Y 重做：4 件虚影放回原处（状态行“{mode.StatusText}”）");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.UpgradePlan));
            Next(294, "按升级键（默认 U）进入升级规划");
        }

        private static void StepPlanUpgradeMode(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode.UpgradeMode, $"U 进入升级规划（建造栏写“{LabelText("[BuildModeHudHost]", "BuildMode")}”）");
            DragWorld(PlanWorld("PlanSrc"), PlanWorld("PlanSrc", 3, 0), 0);
            Next(295, "拖框框住源传送带");
        }

        private static void StepPlanUpgraded(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Campaign.Grid.GridCell s = PlanCell("PlanSrc");
            int upgrading = Enumerable.Range(0, 3).Count(x => Campaign.Regions.HomeValleyConstruction.TryFindUpgradeCell(state, new Campaign.Grid.GridCell(s.X + x, s.Y), out _, out _));
            Check(upgrading == 3 && PlanHistory.PeekUndo(state) == PlanStepKind.Upgrade && mode.StatusText.Contains("升级"),
                $"松开：3 格传送带生成升级施工（分流器没有更高等级，不参与），状态行“{mode.StatusText.Split('\n').FirstOrDefault()}”");
            RightClickWorld(PlanWorld("PlanSrc"));
            Next(296, "右键退出升级规划");
        }

        private static void StepPlanUpgradeExited(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!mode.UpgradeMode && mode.IsOpen, "右键退出升级规划");
            HoverWorld(PlanWorld("PlanSrc", 3, 0));
            Next(297, "鼠标指着分流器");
        }

        private static void StepPlanHoverSplitter(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.Eyedropper));
            Next(298, "按吸管键（默认 Q）");
        }

        private static void StepPlanEyedropped(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode.SelectedToolId == "splitter" && mode.PendingS0 == PlanSettings.PackSplitter(3, 1, 1) && mode.StatusText.Contains("吸管"),
                $"Q 吸管：选中分流器并带上它的设置（状态行“{mode.StatusText}”）");
            RightClickWorld(PlanWorld("PlanSrc", 3, 0));
            Next(299, "右键取消选择");
        }

        private static void StepPlanLibraryKey(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.LayoutLibrary));
            Next(300, "按布局库键（默认 Ctrl+B）");
        }

        private static void StepPlanLibraryOpen(double inStep)
        {
            if (inStep < 1.0)
            {
                return;
            }
            LayoutLibraryPanelUIToolkit panel = LayoutLibraryPanelUIToolkit.Instance;
            Check(LayoutLibraryPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.EmptyText.Contains("布局库是空的"),
                $"Ctrl+B 打开布局库（真 UXML）：空库说明“{panel?.EmptyText}”");
            bool saved = ClickUitk("[LayoutLibraryHost]", "LayoutLibrarySave");
            panel?.Refresh();
            Check(saved && LayoutLibrary.Count == 1 && panel.VisibleRowCount == 1 && panel.RowThumbnail(0) != null && panel.RowInfo(0).Contains("4 件"),
                $"点“保存剪贴板”：布局库多一行“{panel?.RowName(0)}”（{panel?.RowInfo(0)}），带缩略图，写进玩家配置目录");
            CheckNoTextMarkers("布局库");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.LayoutLibrary));
            Next(301, "再按 Ctrl+B 关闭布局库");
        }

        private static void StepPlanLibraryClosed(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!LayoutLibraryPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Ctrl+B 关闭布局库，建造模式还开着");
            // 清理：撤销升级与粘贴（经撤销栈，两步），拆掉测试传送带与分流器，布局库目录复位。
            int undos = 0;
            while (PlanHistory.UndoSteps(state) > SessionState.GetInt(K + "PlanUndo0", 0) && undos < 8)
            {
                PlanHistory.Undo(state);
                undos++;
            }
            Campaign.Grid.GridCell s = PlanCell("PlanSrc");
            HomeGridService.TryRemoveBelts(state, Enumerable.Range(0, 4).Select(x => new Campaign.Grid.GridCell(s.X + x, s.Y)).ToList());
            bool clean = Enumerable.Range(0, 4).All(x => !BeltNetworkService.Kernel.HasCell(s.X + x, s.Y))
                         && !(state.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Any(p => p.Upgrade);
            try
            {
                Directory.Delete(LayoutLibrary.DirectoryOverrideForTests, true);
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            Check(clean, $"清理：撤销 {undos} 步（升级、粘贴），拆掉测试传送带与分流器，布局库目录复位");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OverlaySelector));
            Next(302, "FG3-LOG-08：建造模式里按叠加层选择器键（默认 Alt+O）");
        }

        // ── FG3-LOG-08：叠加层与“为什么不工作”（真实按键 / UI Toolkit 点击；点原因条目镜头飞过去、建造模式不退出）──

        private static void StepOverlaySelectorOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            OverlayHudUIToolkit hud = OverlayHudUIToolkit.Instance;
            Check(hud != null && hud.DockVisible && OverlayHudUIToolkit.SelectorOpen && hud.SelectorVisible && hud.HintText.Contains(InputDisplay.ForAction(GameActionId.ToggleOverlay)),
                $"Alt+O 打开叠加层选择器（FGU-12）：停靠条“{hud?.ToggleText}”，8 种叠加层按钮与提示");
            bool picked = ClickUitk("[OverlayHudHost]", "OverlayBtn2");
            Check(picked && GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Blockage && GameLogic.View.OverlayService.BeltOverlayMode == 2,
                "点“堵塞”：叠加层切到堵塞（传送带着色器模式 2）");
            CheckNoTextMarkers("叠加层选择器");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.ToggleOverlay));
            Next(303, "按叠加层切换键（默认 O）关闭当前叠加层");
        }

        private static void StepOverlayToggledOff(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.None && GameLogic.View.OverlayService.BeltOverlayMode == 0
                  && OverlayHudUIToolkit.Instance != null && OverlayHudUIToolkit.Instance.ActiveText.Contains("关"), "O 关掉叠加层，停靠条写“叠加层：关”");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.ToggleOverlay));
            Next(304, "再按 O：重开最近用过的叠加层");
        }

        private static void StepOverlayReopened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Blockage && GameLogic.View.OverlayService.Visible,
                "O 重开最近用过的“堵塞”叠加层（家园被观察时在画）");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OverlayFlow));
            Next(305, "按物品流向直达键（默认 Ctrl+Alt+1）");
        }

        private static void StepOverlayFlowKey(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Flow && GameLogic.View.OverlayService.BeltOverlayMode == 1
                  && (BeltNetworkService.Renderer == null || BeltNetworkService.Renderer.OverlayMode == 1),
                "Ctrl+Alt+1 直达“物品流向与吞吐”（两个修饰键的组合走真实按键路径），传送带渲染器按热度 + 箭头模式画");
            // 测试捷径：玩家在电网面板里关停维修台（正式路径由 FG3-LOG-06 覆盖），制造一处“停工”。
            CampaignState state = CampaignSession.Current;
            BuildingRecord bay = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":repair_bay");
            if (bay != null && bay.ConstructionState == BuildingConstructionState.Operational)
            {
                Campaign.Regions.HomeValleyPowerGrid.TryToggleShutdown(state, bay.BuildingId);
            }
            SessionState.SetBool(K + "DiagBayShut", bay != null && bay.ConstructionState == BuildingConstructionState.Disabled);
            SessionState.SetInt(K + "DiagFly0", WorldView.FlyCount);
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenDiagnosis));
            Next(306, "按“为什么不工作”键（默认 Ctrl+O）");
        }

        private static void StepDiagnosisOpened(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            CampaignState state = CampaignSession.Current;
            BuildingRecord bay = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":repair_bay");
            int row = -1;
            for (int i = 0; panel != null && i < panel.RowCount; i++)
            {
                if (panel.SubjectText(i).Contains(HomeGridService.DisplayName(Campaign.Regions.HomeValleyLayout.BuildingTypeRepairBay)))
                {
                    row = i;
                    break;
                }
            }
            int step = -1;
            for (int i = 0; panel != null && i < panel.StepButtonCount; i++)
            {
                DiagStep st = panel.StepTarget(i);
                if (st != null && st.Code == DiagCode.Disabled && st.TargetId == bay?.BuildingId)
                {
                    step = i;
                    break;
                }
            }
            Check(SessionState.GetBool(K + "DiagBayShut", false) && DiagnosisPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && row >= 0 && step >= 0,
                $"Ctrl+O 打开“为什么不工作”（非模态，停靠左侧）：{panel?.RowCount} 个停工对象，列出被关停的维修台（“{panel?.SubjectText(Math.Max(0, row))}”）");
            Check(!OverlayHudUIToolkit.SelectorOpen && panel != null && panel.OverlayText.Contains(GameLogic.View.OverlayService.Name(GameLogic.View.OverlayKind.Flow)),
                $"左侧停靠位同一时间只放一个：面板打开时叠加层选择器收起，面板页眉下写当前叠加层（“{panel?.OverlayText}”）");
            CheckNoTextMarkers("为什么不工作");
            bool clicked = step >= 0 && ClickUitk("[DiagnosisPanelHost]", "DgStep" + step);
            Check(clicked, "点维修台那一条原因（UI Toolkit 点击）");
            Next(307, "镜头飞到维修台");
        }

        private static void StepDiagnosisLocated(double inStep)
        {
            if (inStep < 1.5)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            BuildingRecord bay = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":repair_bay");
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            Check(bay != null && WorldView.FlyCount > SessionState.GetInt(K + "DiagFly0", 0) && Mathf.Abs(f.x - bay.Position.x) < 1f && Mathf.Abs(f.y - bay.Position.y) < 1f
                  && mode != null && mode.IsOpen && DiagnosisPanelUIToolkit.IsOpen,
                $"点原因条目：镜头飞到维修台（{bay?.Position}），建造模式仍开着（FG-GAP-071），面板留着接着看");
            if (bay != null && bay.ConstructionState == BuildingConstructionState.Disabled)
            {
                Campaign.Regions.HomeValleyPowerGrid.TryToggleShutdown(state, bay.BuildingId);
            }
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenDiagnosis));
            Next(308, "再按 Ctrl+O 关闭“为什么不工作”");
        }

        private static void StepDiagnosisClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!DiagnosisPanelUIToolkit.IsOpen, "Ctrl+O 关闭“为什么不工作”");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OverlaySelector));
            Next(309, "再按 Alt+O 重新打开叠加层选择器（面板关了，停靠位还给选择器）");
        }

        private static void StepOverlayOffAgain(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(OverlayHudUIToolkit.SelectorOpen && OverlayHudUIToolkit.Instance != null && OverlayHudUIToolkit.Instance.SelectorVisible
                  && GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Flow,
                "Alt+O 重新打开叠加层选择器（物品流向还开着）");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OverlayFlow));
            Next(311, "再按 Ctrl+Alt+1 关闭叠加层");
        }

        private static void StepOverlayOffConfirmed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.None && OverlayHudUIToolkit.SelectorOpen,
                "同一个直达键再按一次关闭叠加层（选择器还开着）");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(310, "Esc 先关叠加层选择器");
        }

        private static void StepOverlaySelectorEsc(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!OverlayHudUIToolkit.SelectorOpen && mode != null && mode.IsOpen, "Esc 关掉选择器（最上层先关），建造模式还开着");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(312, "Esc 退出建造模式；FG4-ECO-01：按 Alt+I 打开物资面板");
        }

        // ── FG4-ECO-01：物资面板（FG04 第 4 节“悬停物品图标显示总库存、各仓库分布、当前净速率”“物品和配方都有图鉴条目”）──

        private static void StepItemsOpenKey(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(mode == null || !mode.IsOpen, "Esc 退出了建造模式");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenItems));
            Next(313, "按 Alt+I（物资键）");
        }

        private static void StepItemsPanelOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.Kit.ItemsPanelUIToolkit p = UI.Kit.ItemsPanelUIToolkit.Instance;
            int items = Campaign.Economy.ItemCatalog.Items.Count;
            string scrapAmount = p?.TileAmountText("scrap") ?? string.Empty;
            Check(UI.Kit.ItemsPanelUIToolkit.IsOpen && p != null && p.PanelVisible && InputRouter.IsModalOwner(p) && p.TileCount == items && items >= 30
                  && scrapAmount.Length > 0 && p.CountText.Length > 0 && p.ErrorText.Length == 0
                  && !Localization.GameText.ContainsMarker(p.TitleText + p.CountText + p.FooterText + p.TileNameText("alloy")),
                $"按 Alt+I 打开物资面板：{p?.TileCount} 种物品（{p?.CountText}），废料格“{p?.TileNameText("scrap")} {scrapAmount}”；页脚“{p?.FooterText}”");
            CheckNoTextMarkers("物资面板");
            // 鼠标移到废料图标上：派发指针进入事件，走 UiTooltip 自己注册的回调。
            VisualElement tile = p?.TileOf("scrap");
            if (tile != null)
            {
                using (PointerEnterEvent enter = PointerEnterEvent.GetPooled())
                {
                    enter.target = tile;
                    tile.SendEvent(enter);
                }
            }
            Next(314, "鼠标悬停废料图标");
        }

        private static void StepItemsHoverShown(double inStep)
        {
            // 悬停提示约 0.4 真实秒后出现（UiKitOverlay 每帧 Tick）。
            if (inStep < 2.5)
            {
                return;
            }
            TooltipContent tip = UiTooltip.Content;
            string body = tip?.Body ?? string.Empty;
            string sources = tip == null ? string.Empty : string.Join("、", tip.Sources.Select(x => x.Label + " " + x.Value));
            Check(UiTooltip.IsVisible && tip != null && tip.Title == Localization.GameText.Get("item.scrap.name")
                  && body.Contains(Localization.GameText.Get("item.hover.total_cap").Split('{')[0]) && tip.Sources.Count > 0
                  && tip.CodexEntryId == Progression.MechanicCodex.ItemEntryId("scrap") && !Localization.GameText.ContainsMarker(body + sources),
                $"悬停废料图标：“{body.Replace("\n", " / ")}”，分布 {sources}");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
            Next(315, "悬停时按图鉴键（默认 C）");
        }

        private static void StepItemsCodexJumped(double inStep)
        {
            if (inStep < 1)
            {
                return;
            }
            UI.Kit.MechanicCodexPanelUIToolkit codex = UI.Kit.MechanicCodexPanelUIToolkit.Instance;
            string id = Progression.MechanicCodex.ItemEntryId("scrap");
            string body = codex?.EntryBodyText ?? string.Empty;
            Check(UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && codex != null && codex.SelectedId == id && codex.CurrentTab == Progression.MechanicCodex.TabItem
                  && body.Contains(Localization.GameText.Get("codex.item.source_title")) && body.Contains(Localization.GameText.Get("codex.item.use_title"))
                  && UI.Kit.ItemsPanelUIToolkit.IsOpen,
                $"悬停按 C 跳到图鉴物品页签的“{codex?.EntryTitleText}”（写明从哪来、拿去干什么），物资面板仍在下面");
            CheckNoTextMarkers("图鉴物品页签");
            VisualElement tile = UI.Kit.ItemsPanelUIToolkit.Instance?.TileOf("scrap");
            if (tile != null)
            {
                using (PointerLeaveEvent leave = PointerLeaveEvent.GetPooled())
                {
                    leave.target = tile;
                    tile.SendEvent(leave);
                }
            }
            PressKeyKeepMouse(KeyCode.Escape);
            Next(316, "Esc 关图鉴");
        }

        private static void StepItemsCodexClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.Kit.MechanicCodexPanelUIToolkit.IsOpen && UI.Kit.ItemsPanelUIToolkit.IsOpen, "Esc 先关图鉴（最上层），物资面板还开着");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenItems));
            Next(317, "再按 Alt+I 关闭物资面板");
        }

        private static void StepItemsPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.Kit.ItemsPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(UI.Kit.ItemsPanelUIToolkit.Instance), "同一个键再按一次关闭物资面板");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenRules));
            Next(338, "FG4-ECO-06：按 Alt+R（常驻规则键）");
        }

        // ── FG4-ECO-06：常驻规则面板的快捷键路径（暂停菜单路径在暂停菜单那一步里点过）──

        private static void StepRulesKeyOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.Kit.RulesPanelUIToolkit p = UI.Kit.RulesPanelUIToolkit.Instance;
            p?.Refresh();
            Check(UI.Kit.RulesPanelUIToolkit.IsOpen && p != null && p.PanelVisible && InputRouter.IsModalOwner(p) && p.VisibleRowCount >= 1
                  && !Localization.GameText.ContainsMarker(p.CountText + p.RowText(0, "RrId") + p.RowText(0, "RrWhen")),
                $"Alt+R 打开常驻规则面板（{p?.CountText}，首行“{p?.RowText(0, "RrId")} {p?.RowText(0, "RrWhen")}”）");
            PressChordKeepMouse(GameSettings.KeyBindings.GetChord(GameActionId.OpenRules));
            Next(339, "再按 Alt+R 关闭常驻规则面板");
        }

        private static void StepRulesKeyClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.Kit.RulesPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(UI.Kit.RulesPanelUIToolkit.Instance), "同一个键再按一次关闭常驻规则面板");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenRoster));
            Next(340, "FG4-ECO-07：按 N（机器名册键）");
        }

        // ── FG4-ECO-07：机器名册的快捷键与顶栏劳动力路径（暂停菜单路径在暂停菜单那一步里点过）──

        private static void StepRosterKeyOpened(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            UI.Kit.RosterPanelUIToolkit p = UI.Kit.RosterPanelUIToolkit.Instance;
            p?.Refresh();
            Check(UI.Kit.RosterPanelUIToolkit.IsOpen && p != null && p.PanelVisible && InputRouter.IsModalOwner(p) && p.VisibleRowCount >= 1
                  && !Localization.GameText.ContainsMarker(p.CountText + p.RowText(0, "RoName") + p.RowText(0, "RoInfo")),
                $"N 打开机器名册（{p?.CountText}，首行“{p?.RowText(0, "RoName")} {p?.RowText(0, "RoStatus")}”）");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenRoster));
            Next(341, "再按 N 关闭机器名册");
        }

        private static void StepRosterKeyClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.Kit.RosterPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(UI.Kit.RosterPanelUIToolkit.Instance), "同一个键再按一次关闭机器名册");
            // 顶栏劳动力（FGR-ECO-042）：读数与名册同源，点它打开名册。
            UI.Kit.WorldBarHudUIToolkit bar = UI.Kit.WorldBarHudUIToolkit.Instance;
            bar?.Refresh();
            Campaign.MachineRoster.LaborCount lc = Campaign.MachineRoster.ComputeLabor(CampaignSession.Current);
            string text = bar?.LaborText ?? string.Empty;
            bool shown = text.Contains("劳动力 " + lc.Labor) && !Localization.GameText.ContainsMarker(text);
            bool opened = ClickUitk("[WorldBarHost]", "WorldLabor") && UI.Kit.RosterPanelUIToolkit.IsOpen;
            Check(shown && opened, $"顶栏劳动力“{text}”与名册一致（劳动 {lc.Labor}、忙碌 {lc.Busy}），点它打开机器名册（{shown}/{opened}）");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.Cancel));
            Next(342, "Esc 关闭机器名册");
        }

        private static void StepRosterBarClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Check(!UI.Kit.RosterPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(UI.Kit.RosterPanelUIToolkit.Instance) && !PauseMenuUIToolkit.IsOpen,
                "Esc 关闭机器名册（不连带打开暂停菜单）");
            PressKeyKeepMouse(GameSettings.KeyBindings.GetKey(GameActionId.OpenBuildMenu));
            Next(318, "FG4-ECO-02：按建造菜单键打开建造模式，放采集建筑");
        }

        // ── FG4-ECO-02：采集与加工建筑（卡片“采集建筑只能放在资源点上，并说明原因”“每座建筑的通用面板”）──

        private static GridCell ProdCell(string name) => new GridCell(SessionState.GetInt(K + name + "X", 0), SessionState.GetInt(K + name + "Y", 0));

        private static void StepProdBuildReady(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(mode != null && mode.IsOpen, "建造模式开着");
            // 核心附近按地形找（B25，不写死坐标）：一格能放 2×2 的空地（不是矿脉）给提取钻试错；一块能放回收站的废墟（起始区保证 24 格内有 4×4 整块废墟群）。
            GridCell core = HomeGridService.CorePivot(state);
            byte ruin = Campaign.Grid.GridContent.TerrainCode("ruin");
            byte metal = Campaign.Grid.GridContent.TerrainCode("ore_metal");
            byte rare = Campaign.Grid.GridContent.TerrainCode("ore_rare");
            HomeGridMap map = HomeGridService.MapFor(state);
            GridCell? plain = null, ruinAt = null, pumpAt = null;
            for (int r = 6; r <= 22 && (plain == null || ruinAt == null); r++)
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
                        if (plain == null && HomeGridService.ValidatePlacement(state, "refinery_furnace", c, 0, checkCost: false).Ok)
                        {
                            bool noOre = true;
                            for (int y = -1; y <= 1 && noOre; y++)
                            {
                                for (int x = -1; x <= 1 && noOre; x++)
                                {
                                    byte t = map.GetTerrain(new GridCell(c.X + x, c.Y + y));
                                    noOre = t != metal && t != rare;
                                }
                            }
                            if (noOre)
                            {
                                plain = c;
                            }
                        }
                        if (ruinAt == null && map.GetTerrain(c) == ruin && HomeGridService.ValidatePlacement(state, "recycler", c, 0, checkCost: false).Ok)
                        {
                            ruinAt = c;
                        }
                    }
                }
            }
            // 流体泵：一块至少压到一格水源或油井、整块可放的 2×2（起始区保证 24 格内有水源）。
            for (int r = 3; r <= 24 && pumpAt == null; r++)
            {
                for (int dy = -r; dy <= r && pumpAt == null; dy++)
                {
                    for (int dx = -r; dx <= r && pumpAt == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (PipeNetworkService.SourceFluidAt(state, c) > 0 && HomeGridService.ValidatePlacement(state, "fluid_pump", c, 0, checkCost: false).Ok)
                        {
                            pumpAt = c;
                        }
                    }
                }
            }
            if (plain == null || ruinAt == null || pumpAt == null)
            {
                Finish("核心 22 格内找不到试放提取钻的空地 / 能放回收站的废墟，或 24 格内找不到能放流体泵的水源 / 油井");
                return;
            }
            SessionState.SetInt(K + "ProdPumpX", pumpAt.Value.X);
            SessionState.SetInt(K + "ProdPumpY", pumpAt.Value.Y);
            SessionState.SetInt(K + "ProdPlainX", plain.Value.X);
            SessionState.SetInt(K + "ProdPlainY", plain.Value.Y);
            SessionState.SetInt(K + "ProdRuinX", ruinAt.Value.X);
            SessionState.SetInt(K + "ProdRuinY", ruinAt.Value.Y);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "gathering");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("extraction_drill");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "extraction_drill";
            Check(tabClicked && picked, $"点“采集”页签里的“提取钻”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）");
            HoverWorld(new Vector3(plain.Value.X, 0f, plain.Value.Y));
            Next(319, "鼠标移到一块不是矿脉的空地上");
        }

        private static void StepProdDrillRefused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            string why = pv == null ? string.Empty : string.Join(" / ", pv.Reasons.Select(r => r.Describe()));
            Check(pv != null && !pv.Ok && why.Contains("提取钻要压在金属或稀土矿脉上"), $"放置预览：提取钻指着空地不能放，写明原因（“{why}”）");
            GridCell plain = ProdCell("ProdPlain");
            ClickWorld(new Vector3(plain.X, 0f, plain.Y));
            Next(320, "照样单击一下（应当放不下）");
        }

        private static void StepProdPumpPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell plain = ProdCell("ProdPlain");
            Check(HomeGridService.BuildingAt(state, plain) == null && mode.StatusText.Contains("提取钻要压在金属或稀土矿脉上"),
                $"单击空地没有放下提取钻，状态行写原因（“{mode?.StatusText}”）");
            int idx = HudItemIndex("fluid_pump");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "fluid_pump";
            Check(picked, $"点“采集”页签里的“流体泵”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）");
            HoverWorld(new Vector3(plain.X, 0f, plain.Y));
            Next(325, "鼠标移到空地上（流体泵放不下：要压水源或油井）");
        }

        private static void StepProdPumpRefused(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            string why = pv == null ? string.Empty : string.Join(" / ", pv.Reasons.Select(r => r.Describe()));
            Check(pv != null && !pv.Ok && why.Contains("流体泵要压在水源或油井上"), $"放置预览：流体泵指着空地不能放，写明原因（“{why}”）");
            GridCell pump = ProdCell("ProdPump");
            HoverWorld(new Vector3(pump.X, 0f, pump.Y));
            Next(326, "鼠标移到水源 / 油井上（预览写抽什么、多快）");
        }

        private static void StepProdRecyclerPreview(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult ppv = mode?.Preview;
            Check(ppv != null && ppv.Ok && ppv.Notes.Any(n => n.Contains("流体源不会抽干")),
                $"放置预览：流体泵压在水源 / 油井上可放，写“{(ppv == null ? string.Empty : string.Join(" / ", ppv.Notes))}”");
            int idx = HudItemIndex("recycler");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "recycler";
            Check(picked, $"点“回收站”：选中（{mode?.SelectedTypeId}）");
            GridCell ruin = ProdCell("ProdRuin");
            HoverWorld(new Vector3(ruin.X, 0f, ruin.Y));
            Next(321, "鼠标移到废墟上（放置预览写储量）");
        }

        private static void StepProdRecyclerPlaced(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            Check(pv != null && pv.Ok && pv.Notes.Any(n => n.Contains("脚下废墟") && n.Contains("储量")), $"放置预览：回收站压在废墟上可放，写“{(pv == null ? string.Empty : string.Join(" / ", pv.Notes))}”");
            GridCell ruin = ProdCell("ProdRuin");
            ClickWorld(new Vector3(ruin.X, 0f, ruin.Y));
            Next(322, "单击放下回收站");
        }

        private static void StepProdDeselected(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell ruin = ProdCell("ProdRuin");
            BuildingRecord b = HomeGridService.BuildingAt(state, ruin);
            Check(b != null && b.BuildingTypeId == "recycler" && Campaign.Regions.HomeValleyController.IsPlannedGhost(b),
                $"单击放下回收站的虚影（状态行“{mode?.StatusText}”）");
            RightClickWorld(new Vector3(ruin.X, 0f, ruin.Y));
            Next(323, "右键取消选择，再左键点回收站（打开通用面板）");
        }

        private static void StepProdPanelOpened(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridCell ruin = ProdCell("ProdRuin");
            if (inStep < 1.5)
            {
                return;
            }
            if (!ProductionPanelUIToolkit.IsOpen && SessionState.GetInt(K + "ProdPanelClicked", 0) == 0)
            {
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "ProdPanelClicked", 1);
                ClickWorld(new Vector3(ruin.X, 0f, ruin.Y));
                return;
            }
            if (inStep < 2.2)
            {
                return;
            }
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            bool open = ProductionPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.TitleText.Contains("回收站")
                        && (panel.StateText.Contains("建造中") || panel.StateText.Contains("工作中") || panel.StateText.Contains("堵塞"))
                        && panel.DetailText.Contains("脚下废墟");
            Check(open, $"点回收站打开通用面板（真 UXML）：“{panel?.TitleText}”“{panel?.StateText}”“{panel?.ReasonText?.Replace("\n", " · ")}”；{panel?.DetailText?.Split('\n')[0]}");
            CheckNoTextMarkers("生产建筑通用面板");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(324, "Esc 关闭面板");
        }

        private static void StepProdPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!ProductionPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 先关面板（建造模式还开着）");
            // FG4-ECO-03：建造模式还开着，点“制造”页签选电子组装台，指着刚才那块空地（提取钻放不下的那块）。
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "manufacturing");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("electronics_bench");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "electronics_bench";
            Check(tabClicked && picked, $"FG4-ECO-03：点“制造”页签里的“电子组装台”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）");
            GridCell plain = ProdCell("ProdPlain");
            HoverWorld(new Vector3(plain.X, 0f, plain.Y));
            Next(327, "鼠标移到空地上（电子组装台可以放）");
        }

        // ── FG4-ECO-03：制造建筑（卡片“配方选择记住上一次的设置”“复制设置到同类建筑”；两种材料各走一个输入口）──

        private static void StepMfgPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            Check(pv != null && pv.Ok, $"放置预览：电子组装台指着空地可以放（{(pv == null ? "没有预览" : string.Join(" / ", pv.Reasons.Select(r => r.Describe())))}）");
            GridCell plain = ProdCell("ProdPlain");
            ClickWorld(new Vector3(plain.X, 0f, plain.Y));
            Next(328, "单击放下电子组装台");
        }

        private static void StepMfgPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell plain = ProdCell("ProdPlain");
            BuildingRecord b = HomeGridService.BuildingAt(state, plain);
            Check(b != null && b.BuildingTypeId == "electronics_bench" && Campaign.Regions.HomeValleyController.IsPlannedGhost(b)
                  && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyManufacturingFirstPlaced),
                $"单击放下电子组装台的虚影，第一次放下制造建筑发引导钩子（状态行“{mode?.StatusText}”）");
            RightClickWorld(new Vector3(plain.X, 0f, plain.Y));
            Next(329, "右键取消选择，再左键点电子组装台（打开通用面板）");
        }

        private static void StepMfgDeselected(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridCell plain = ProdCell("ProdPlain");
            if (inStep < 1.5)
            {
                return;
            }
            if (!ProductionPanelUIToolkit.IsOpen && SessionState.GetInt(K + "MfgPanelClicked", 0) == 0)
            {
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "MfgPanelClicked", 1);
                ClickWorld(new Vector3(plain.X, 0f, plain.Y));
                return;
            }
            if (inStep < 2.2)
            {
                return;
            }
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            bool open = ProductionPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.TitleText.Contains("电子组装台") && panel.RecipeDropdownVisible
                        && panel.RecipeField.choices.Count == 4 && panel.CopyVisible && panel.MemoryText.Contains("新建的电子组装台会沿用");
            Check(open, $"点电子组装台打开通用面板：“{panel?.TitleText}”“{panel?.StateText}”，配方下拉 {panel?.RecipeField?.choices?.Count} 项，“{panel?.MemoryText}”，复制设置按钮 {panel?.CopyVisible}");
            // UI Toolkit 红线 8：下拉框选中即生效。
            if (panel != null && panel.RecipeField.choices.Count > 1)
            {
                panel.RecipeField.value = panel.RecipeField.choices[1];
            }
            Next(330, "配方下拉框选“电子件”");
        }

        private static void StepMfgPanelOpened(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            CampaignState state = CampaignSession.Current;
            GridCell plain = ProdCell("ProdPlain");
            BuildingRecord b = HomeGridService.BuildingAt(state, plain);
            bool set = b != null && Campaign.Economy.ProductionService.TryGet(state, b.BuildingId, out Campaign.Economy.ProductionService.Producer p) && p.Recipe?.Id == "electronic"
                       && panel != null && panel.MessageText.Contains("配方改为") && panel.MemoryText.Contains("电子件")
                       && Campaign.Economy.ProductionService.MemoryOf(state, "electronics_bench")?.RecipeId == "electronic";
            Check(set, $"下拉框选“电子件”即生效（“{panel?.MessageText}”），记住这类建筑的选择（“{panel?.MemoryText}”）");
            var views = new List<BeltPortService.PortView>();
            BeltPortService.CollectViews(state, b, views);
            Check(views.Count(v => !v.IsOutput && v.AcceptLine != null && (v.AcceptLine.Contains("合金") || v.AcceptLine.Contains("稀土矿"))) == 2,
                $"电子组装台两个输入口各收一种材料：{string.Join(" / ", views.Select(v => v.AcceptLine))}");
            CheckNoTextMarkers("制造建筑通用面板");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(331, "Esc 关闭面板");
        }

        private static void StepMfgPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!ProductionPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 先关面板（建造模式还开着）");
            // FG4-ECO-04：建造模式还开着，点“能源”页签选储能站，指着核心附近一块空地（按地形找，B25）。
            CampaignState state = CampaignSession.Current;
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? ghostAt = null, builtAt = null;
            for (int r = 7; r <= 20 && builtAt == null; r++)
            {
                for (int dy = -r; dy <= r && builtAt == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtAt == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!HomeGridService.ValidatePlacement(state, "energy_storage", c, 0, checkCost: false).Ok)
                        {
                            continue;
                        }
                        if (ghostAt == null)
                        {
                            ghostAt = c;
                        }
                        else if (Math.Abs(c.X - ghostAt.Value.X) > 3 || Math.Abs(c.Y - ghostAt.Value.Y) > 3)
                        {
                            builtAt = c;
                        }
                    }
                }
            }
            SessionState.SetInt(K + "EnergyGhostX", ghostAt?.X ?? 0);
            SessionState.SetInt(K + "EnergyGhostY", ghostAt?.Y ?? 0);
            SessionState.SetInt(K + "EnergyBuiltX", builtAt?.X ?? 0);
            SessionState.SetInt(K + "EnergyBuiltY", builtAt?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "energy");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("energy_storage");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "energy_storage";
            Check(ghostAt.HasValue && builtAt.HasValue && tabClicked && picked,
                $"FG4-ECO-04：点“能源”页签里的“储能站”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）；核心附近找到两块空地");
            if (ghostAt.HasValue)
            {
                HoverWorld(new Vector3(ghostAt.Value.X, 0f, ghostAt.Value.Y));
            }
            Next(332, "鼠标移到空地上（储能站可以放，预览写接入电网）");
        }

        // ── FG4-ECO-04：能源扩展（储能站放置、电网面板的储能站设置与分类发电图例、地下管线口）──

        private static GridCell EnergyCell(string name) => new GridCell(SessionState.GetInt(K + name + "X", 0), SessionState.GetInt(K + name + "Y", 0));

        private static void StepEnergyPicked(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            Check(pv != null && pv.Ok && pv.Notes.Any(n => n.Contains("接入")),
                $"放置预览：储能站指着空地可以放，写“{(pv == null ? "没有预览" : string.Join(" / ", pv.Notes))}”");
            GridCell at = EnergyCell("EnergyGhost");
            ClickWorld(new Vector3(at.X, 0f, at.Y));
            Next(333, "单击放下储能站");
        }

        private static void StepEnergyPlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell at = EnergyCell("EnergyGhost");
            BuildingRecord b = HomeGridService.BuildingAt(state, at);
            Check(b != null && b.BuildingTypeId == "energy_storage" && Campaign.Regions.HomeValleyController.IsPlannedGhost(b),
                $"单击放下储能站的虚影（状态行“{mode?.StatusText}”）");
            // 测试捷径：另一块空地上直接登记一座建成的储能站（机器施工由 FgEnergySelfCheck F12 覆盖），下一步点它打开电网面板。
            AddSmokeBuilding(state, "energy_storage", "store", EnergyCell("EnergyBuilt"));
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            RightClickWorld(new Vector3(at.X, 0f, at.Y));
            Next(334, "右键取消选择，再左键点建成的储能站（打开电网面板）");
        }

        private static void StepEnergyDeselected(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridCell built = EnergyCell("EnergyBuilt");
            if (inStep < 1.5)
            {
                return;
            }
            if (!PowerPanelUIToolkit.IsOpen && SessionState.GetInt(K + "EnergyPanelClicked", 0) == 0)
            {
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "EnergyPanelClicked", 1);
                SessionState.SetInt(K + "EnergyGridClicked", 0);
                ClickWorld(new Vector3(built.X, 0f, built.Y));
                return;
            }
            if (inStep < 2.2)
            {
                return;
            }
            string storeId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_store";
            if (SessionState.GetInt(K + "EnergyGridClicked", 0) == 0)
            {
                // FG4-ECO-05（FGU-09）：点储能站先打开它的通用面板（状态写储能读数），再点面板上的“电网…”打开电网面板。
                ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
                bool bpOpen = ProductionPanelUIToolkit.IsOpen && bp != null && bp.PanelVisible && ProductionPanelUIToolkit.BuildingId == storeId;
                Check(bpOpen && bp.IdentText.Contains(Campaign.Grid.HomeGridService.DisplayName("energy_storage")) && !string.IsNullOrEmpty(bp.ReasonText)
                      && bp.GridButton != null && ProductionPanelUIToolkit.Visible(bp.GridButton),
                    $"点建成的储能站打开它的通用面板：“{bp?.IdentText}”状态 {bp?.ShownStatus}“{bp?.ReasonText}”，有“电网…”按钮");
                SessionState.SetInt(K + "EnergyGridClicked", 1);
                Check(ClickUitk("[ProductionPanelHost]", "BpGrid"), "通用面板上点“电网…”");
                return;
            }
            if (inStep < 2.9)
            {
                return;
            }
            PowerPanelUIToolkit panel = PowerPanelUIToolkit.Instance;
            bool open = PowerPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && panel.StorageBoxVisible && panel.SelectedStorageId == storeId
                        && panel.DischargeField.choices.Count == 5;
            // 修复轮 P2：分类发电图例真按类别写出——曲线已有采样点时必须写“分类发电：”且含归还核心（核心恒在发电）；还没采样时图例为空、标题写“还没有数据”。
            bool hasCurve = panel != null && Campaign.Regions.HomeValleyPowerGrid.Kernel != null
                            && Campaign.Regions.HomeValleyPowerGrid.Kernel.TryGetCurve(panel.SelectedSerial, out BinGames.Sim.Logistics.PowerCurve curve) && curve.Count > 0;
            string coreName = Campaign.Regions.HomeValleyPowerGrid.SourceClassName(0);
            bool legend = panel != null && (hasCurve ? panel.CurveSourcesText.StartsWith("分类发电：", StringComparison.Ordinal) && panel.CurveSourcesText.Contains(coreName)
                                                     : panel.CurveSourcesText.Length == 0);
            Check(open && legend, $"点建成的储能站打开电网面板（选中它的电网与这座储能站）：“{panel?.StorageTitleText}”“{panel?.StoragesText}”，放电对象 {panel?.DischargeField?.choices?.Count} 项；" +
                                  $"分类发电图例“{panel?.CurveSourcesText}”（有采样 {hasCurve}）");
            // UI Toolkit 红线 8：下拉框选中即生效——放电只给优先级 1～2。玩家在下拉菜单里点选项时 DropdownField 走的就是 value 赋值 → ChangeEvent
            // （与 5467 / 6586 两处一致）；这里先确认下拉框此刻真能点（可见、可用、在面板里），再走同一个值变化回调。
            DropdownField dd = panel?.DischargeField;
            bool clickable = dd != null && dd.panel != null && dd.enabledInHierarchy && dd.resolvedStyle.display != DisplayStyle.None && dd.worldBound.width > 0f;
            Check(clickable, $"放电对象下拉框可见可用（{dd?.worldBound}）");
            if (clickable && dd.choices.Count > 2)
            {
                dd.value = dd.choices[2];
            }
            Next(335, "放电对象下拉框选“只给优先级 1～2”");
        }

        private static void StepEnergyPanelSet(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            PowerPanelUIToolkit panel = PowerPanelUIToolkit.Instance;
            CampaignState state = CampaignSession.Current;
            string storeId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_store";
            Campaign.Regions.StorageSettings set = Campaign.Regions.HomeValleyPowerGrid.GetStorageSettings(state, storeId);
            Check(set.Reserve == 2 && !set.NoDischarge && panel != null && panel.MessageText.Contains("只给优先级 1～2") && panel.DetailText.Contains("发电构成"),
                $"放电对象选中即生效（“{panel?.MessageText}”），电网读数写发电构成（“{panel?.DetailText?.Split('\n').FirstOrDefault(l => l.Contains("发电构成"))}”）");
            CheckNoTextMarkers("电网面板储能站一节");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(336, "Esc 关闭电网面板");
        }

        private static void StepEnergyPanelClosed(double inStep)
        {
            if (inStep < 0.5)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            Check(!PowerPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 先关电网面板（建造模式还开着）");
            // FG4-ECO-04（FG-GAP-082）：“物流”页签选地下管线 T1，R 转到朝东，指着储能站虚影西边的空地。
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "logistics");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("pipe_underground_t1");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedToolId == "pipe_underground_t1";
            Check(tabClicked && picked, $"点“物流”页签里的“地下管线 T1”（第 {idx + 1} 项）：选中（{mode?.SelectedToolId}）");
            CampaignState state = CampaignSession.Current;
            GridCell at = EnergyCell("EnergyGhost");
            GridCell? cell = null;
            for (int d = 3; d <= 12 && cell == null; d++)
            {
                var c = new GridCell(at.X - d, at.Y);
                if (HomeGridService.ValidatePipeCell(state, c, BinGames.Sim.Logistics.PipePieceKind.Underground).Ok)
                {
                    cell = c;
                }
            }
            SessionState.SetInt(K + "UgPipeX", cell?.X ?? 0);
            SessionState.SetInt(K + "UgPipeY", cell?.Y ?? 0);
            HoverWorld(new Vector3((cell ?? at).X, 0f, (cell ?? at).Y));
            Next(337, "鼠标移到空地上（地下管线口），看预览，单击放一口");
        }

        private static void StepUndergroundPipePlaced(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell cell = EnergyCell("UgPipe");
            if (inStep < 0.6)
            {
                return;
            }
            if (SessionState.GetInt(K + "UgPipeClicked", 0) == 0)
            {
                Campaign.Grid.BeltPathPlan plan = mode?.ToolPreview;
                bool previewOk = plan != null && plan.Ok && plan.IsPipe && plan.Pipe == BinGames.Sim.Logistics.PipePieceKind.Underground
                                 && !Campaign.Logistics.PipeNetworkService.TryPreviewUnderground(cell, (int)plan.Dirs[0], plan.Tier, out _);
                Check(previewOk, $"地下管线口预览：可以放，附近没有朝回来的另一口 → 写“还没配对”（{(plan == null ? "没有预览" : plan.Reason?.Describe() ?? "可以放")}）");
                SessionState.SetInt(K + "UgPipeClicked", 1);
                ClickWorld(new Vector3(cell.X, 0f, cell.Y));
                return;
            }
            if (inStep < 1.2)
            {
                return;
            }
            if (SessionState.GetInt(K + "UgPipeClicked", 0) == 1)
            {
                bool ghost = Campaign.Regions.HomeValleyConstruction.TryFindPlannedCell(state, cell, out PlannedBeltRecord p, out _) && p.PipePiece == (int)BinGames.Sim.Logistics.PipePieceKind.Underground + 1;
                Check(ghost, $"单击放下地下管线口的虚影（状态行“{mode?.StatusText}”）");
                SessionState.SetInt(K + "UgPipeClicked", 2);
                RightClickWorld(new Vector3(cell.X, 0f, cell.Y));
                return;
            }
            if (inStep < 1.8)
            {
                return;
            }
            Check(mode != null && mode.SelectedToolId == null, "右键取消选择");
            // FG4-ECO-11：建造模式还开着，点“信号”页签选超控阵列，指着核心附近两块空地（4×4，按地形找，B25）。
            GridCell core = HomeGridService.CorePivot(state);
            GridCell? ghostAt = null, builtAt = null;
            for (int r = 8; r <= 26 && builtAt == null; r++)
            {
                for (int dy = -r; dy <= r && builtAt == null; dy++)
                {
                    for (int dx = -r; dx <= r && builtAt == null; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (!OnScreen(c))
                        {
                            continue;
                        }
                        // 虚影按建造模式当前的朝向校验（前一步为地下管线转过朝向，选中新建筑不会重置）；测试捷径登记的那座按朝向 0。
                        if (ghostAt == null)
                        {
                            if (HomeGridService.ValidatePlacement(state, "override_array", c, mode?.GhostRotation ?? 0, checkCost: false).Ok)
                            {
                                ghostAt = c;
                            }
                        }
                        else if ((Math.Abs(c.X - ghostAt.Value.X) > 6 || Math.Abs(c.Y - ghostAt.Value.Y) > 6)
                                 && HomeGridService.ValidatePlacement(state, "override_array", c, 0, checkCost: false).Ok)
                        {
                            builtAt = c;
                        }
                    }
                }
            }
            SessionState.SetInt(K + "OverrideGhostX", ghostAt?.X ?? 0);
            SessionState.SetInt(K + "OverrideGhostY", ghostAt?.Y ?? 0);
            SessionState.SetInt(K + "OverrideBuiltX", builtAt?.X ?? 0);
            SessionState.SetInt(K + "OverrideBuiltY", builtAt?.Y ?? 0);
            int tab = Campaign.Grid.GridContent.Categories.ToList().FindIndex(c => c.Id == "signal");
            bool tabClicked = ClickUitk("[BuildModeHudHost]", "BuildCat" + tab);
            int idx = HudItemIndex("override_array");
            bool picked = idx >= 0 && ClickUitk("[BuildModeHudHost]", "BuildItem" + idx) && mode.SelectedTypeId == "override_array";
            Check(ghostAt.HasValue && builtAt.HasValue && tabClicked && picked,
                $"FG4-ECO-11：点“信号”页签里的“超控阵列”（第 {idx + 1} 项）：选中（{mode?.SelectedTypeId}）；核心附近找到两块 4×4 空地");
            if (ghostAt.HasValue)
            {
                HoverWorld(new Vector3(ghostAt.Value.X, 0f, ghostAt.Value.Y));
            }
            Next(346, "鼠标移到空地上（超控阵列可以放；建造栏成本写关键材料与获取途径）");
        }

        // ── FG4-ECO-11：超控阵列（建造栏写关键材料从哪里获得、虚影等关键材料、建筑面板的槽位行、禁用 → 失效槽在 HUD 上显示 → 启用恢复）──

        /// <summary>这一格（4×4 占地的四个角）在当前镜头画面里（离边缘留 8%）：鼠标真能指到它。</summary>
        private static bool OnScreen(GridCell c)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return false;
            }
            for (int dx = -2; dx <= 3; dx += 5)
            {
                for (int dy = -2; dy <= 3; dy += 5)
                {
                    Vector3 v = cam.WorldToViewportPoint(new Vector3(c.X + dx, 0f, c.Y + dy));
                    if (v.z <= 0f || v.x < 0.08f || v.x > 0.92f || v.y < 0.08f || v.y > 0.92f)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void StepOverridePicked(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridPlacementResult pv = mode?.Preview;
            string cost = BuildModeHudUIToolkit.Instance?.CostLabelText ?? string.Empty;
            string key = Campaign.Economy.ItemCatalog.NameOf("listening_array_core");
            Check(pv != null && pv.Ok && cost.Contains(key) && cost.Contains("寂听主脑") && cost.Contains("研究节点"),
                $"放置预览：超控阵列指着空地可以放；建造栏成本写关键材料与获取途径、研究节点（“{cost.Replace("\n", " / ")}”）");
            GridCell at = EnergyCell("OverrideGhost");
            ClickWorld(new Vector3(at.X, 0f, at.Y));
            Next(347, "单击放下超控阵列");
        }

        private static void StepOverridePlaced(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            GridCell at = EnergyCell("OverrideGhost");
            BuildingRecord b = HomeGridService.BuildingAt(state, at);
            string hoverTitle = null, hoverBody = null;
            bool ghost = b != null && b.BuildingTypeId == "override_array" && Campaign.Regions.HomeValleyController.IsPlannedGhost(b)
                         && b.ExtraMaterialIds != null && b.ExtraMaterialIds.Contains("listening_array_core")
                         && Campaign.Regions.HomeValleyConstruction.TryDescribeSite(state, at, out hoverTitle, out hoverBody);
            Check(ghost && (hoverBody ?? string.Empty).Contains(Campaign.Economy.ItemCatalog.NameOf("listening_array_core")),
                $"单击放下超控阵列的虚影：所需材料含监听阵列核（悬停“{(hoverBody ?? string.Empty).Replace("\n", " / ")}”；状态行“{mode?.StatusText}”）");
            // 测试捷径：取消这座虚影（关键材料从首领来，冒烟里拿不到；新建施工与取料由 FgOverrideArraySelfCheck B 段覆盖），
            // 另一块空地上直接登记一座建成的 T1 超控阵列 + 一座发电机 2（保证它有电），下一步点它打开通用面板。
            if (b != null && b.BuildingTypeId == "override_array")
            {
                HomeGridService.TryToggleDemolish(state, b.BuildingId);
            }
            AddSmokeBuilding(state, "override_array", "override", EnergyCell("OverrideBuilt"));
            BuildingRecord arr = HomeGridService.FindBuilding(state, Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_override");
            if (arr != null)
            {
                arr.Tier = 1;
            }
            GridCell? genAt = null;
            GridCell core = HomeGridService.CorePivot(state);
            for (int r = 6; r <= 26 && genAt == null; r++)
            {
                for (int dx = -r; dx <= r && genAt == null; dx++)
                {
                    var c = new GridCell(core.X + dx, core.Y - r);
                    if (HomeGridService.ValidatePlacement(state, Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2, c, 0, checkCost: false).Ok)
                    {
                        genAt = c;
                    }
                }
            }
            if (genAt.HasValue)
            {
                AddSmokeBuilding(state, Campaign.Regions.HomeValleyLayout.BuildingTypeGenerator2, "override_gen", genAt.Value);
            }
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            GridCell built = EnergyCell("OverrideBuilt");
            RightClickWorld(new Vector3(at.X, 0f, at.Y));
            SessionState.SetInt(K + "OverridePanelClicked", 0);
            Next(348, "右键取消选择，再左键点建成的超控阵列（打开通用面板）");
        }

        private static void StepOverridePanel(double inStep)
        {
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            GridCell built = EnergyCell("OverrideBuilt");
            if (inStep < 1.0)
            {
                return;
            }
            if (SessionState.GetInt(K + "OverridePanelClicked", 0) == 0)
            {
                Check(mode != null && mode.IsOpen && mode.SelectedTypeId == null, "右键取消选择（建造模式还开着）");
                SessionState.SetInt(K + "OverridePanelClicked", 1);
                ClickWorld(new Vector3(built.X + 1, 0f, built.Y + 1));
                return;
            }
            if (inStep < 1.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            string arrId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_override";
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            bool open = ProductionPanelUIToolkit.IsOpen && bp != null && bp.PanelVisible && ProductionPanelUIToolkit.BuildingId == arrId;
            string tier = bp?.TierText ?? string.Empty;
            Check(open && tier.Contains("已解锁 3，生效 3") && tier.Contains("熔炉心") && Campaign.Signal.SignalCoreService.UnlockedSlots(state) == 3
                  && hud != null && hud.EntryText.EndsWith("/3", StringComparison.Ordinal) && !hud.EntryText.Contains("失效"),
                $"点建成的超控阵列打开通用面板：“{bp?.IdentText}”槽位行“{tier.Replace("\n", " / ")}”；HUD 信号核按钮“{hud?.EntryText}”");
            Check(ClickUitk("[ProductionPanelHost]", "BpEnable"), "通用面板上点“禁用”");
            Next(349, "禁用超控阵列：第 3 槽失效");
        }

        private static void StepOverrideDisabled(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            ProductionPanelUIToolkit bp = ProductionPanelUIToolkit.Instance;
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string tier = bp?.TierText ?? string.Empty;
            string note = Notifications.NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "override_offline")?.Text ?? string.Empty;
            Check(Campaign.Signal.SignalCoreService.ActiveSlots(state) == 2 && hud != null && hud.EntryText.Contains("1 槽失效") && tier.Contains("多出的槽失效") && tier.Contains("已禁用")
                  && note.Contains("第 3 槽"),
                $"禁用后第 3 槽失效：HUD 信号核按钮“{hud?.EntryText}”；面板“{tier.Replace("\n", " / ")}”；通知“{note}”");
            Check(ClickUitk("[ProductionPanelHost]", "BpEnable"), "通用面板上点“启用”");
            Next(350, "启用超控阵列：第 3 槽恢复");
        }

        private static void StepOverrideEnabled(double inStep)
        {
            if (inStep < 0.8)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            UI.SignalCore.SignalCoreHudUIToolkit hud = UI.SignalCore.SignalCoreHudUIToolkit.Instance;
            string note = Notifications.NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "override_online")?.Text ?? string.Empty;
            Check(Campaign.Signal.SignalCoreService.ActiveSlots(state) == 3 && hud != null && !hud.EntryText.Contains("失效") && note.Contains("第 3 槽"),
                $"启用后自动恢复：HUD 信号核按钮“{hud?.EntryText}”；通知“{note}”");
            CheckNoTextMarkers("超控阵列面板与信号核按钮");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(351, "Esc 关闭通用面板");
        }

        private static void StepOverrideClosed(double inStep)
        {
            if (inStep < 0.6)
            {
                return;
            }
            Campaign.Regions.HomeValleyBuildMode mode = Campaign.Regions.HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            Check(!ProductionPanelUIToolkit.IsOpen && mode != null && mode.IsOpen, "Esc 先关通用面板（建造模式还开着）");
            // 测试捷径清理：拿掉冒烟登记的超控阵列与发电机（后面的步骤按 2 槽信号核断言）。
            string arrId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_override";
            string genId = Campaign.Regions.HomeValleyLayout.RegionId + ":smoke_power_override_gen";
            state.BuildingRecords = state.BuildingRecords.Where(b => b == null || (b.BuildingId != arrId && b.BuildingId != genId)).ToArray();
            HomeGridService.MapFor(state);
            Campaign.Regions.HomeValleyPowerGrid.Recompute(state);
            Check(Campaign.Signal.SignalCoreService.UnlockedSlots(state) == 2, "清理冒烟登记的超控阵列后信号核回到 2 槽");
            PressKeyKeepMouse(KeyCode.Escape);
            Next(140, "Esc 退出建造模式；FG0-ARCH-03：家园突袭战斗原型（测试捷径）");
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
            SessionState.SetString(K + "RaidClearTicks", GameClock.Ticks.ToString());
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
                $"原型单位清场后弹体飞完消失（剩 {home?.Kernel.ProjectileCount} 枚），家园机器数不变（{machinesBefore} → {GameRoot.HomeValley?.LiveMachineCount} 台；此时远征队仍在外）" +
                $"（清场后世界走了 {GameClock.Ticks - long.Parse(SessionState.GetString(K + "RaidClearTicks", "0"))} 步，暂停 {GameRoot.IsWorldPaused}，模态 {InputRouter.PanelModalOpen}，最近通知 {Notifications.NotificationCenter.History.LastOrDefault()?.Type?.Id}）");
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
        /// <summary>FG3-LOG-09（DEBT-FG0SAVE01-07）：冒烟往存档里放一座“被游戏更新移除”的建筑类型（格网建筑表里没有），投入 11 废料。</summary>
        private const string SmokeRemovedBuilding = "smoke_removed_building";

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
            // FG3-LOG-09（DEBT-FG0ARCH02-10）：统计窗口随存档走——存档里记着已走满的统计桶数，读档后“实测吞吐”不从零累计。
            Check(s.Belts != null && s.Belts.CompletedBuckets > 0,
                $"暂停菜单存档带上传送带统计窗口（已走满 {s.Belts?.CompletedBuckets} 个统计桶，传送带格式 {s.Belts?.FormatVersion}）");
            SessionState.SetString(K + "BeltSavedBuckets", (s.Belts?.CompletedBuckets ?? 0).ToString());
            SessionState.SetInt(K + "ScrapBefore", s.Scrap);
            SessionState.SetInt(K + "GroundBefore", (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount));
            s.PrimitiveChips = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
                .Concat(new[] { new PrimitiveChipRecord { PartId = "pchip_smoke_removed", CardDefId = SmokeRemovedId, State = PrimitiveChipState.Bag } })
                .ToArray();
            // FG3-LOG-09（DEBT-FG0SAVE01-07）：再放一座“被游戏更新移除”的建筑（类型不在格网建筑表里，投入 11 废料）；读档时走真表判定、拆掉并返还。
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>())
                .Concat(new[]
                {
                    new BuildingRecord
                    {
                        BuildingId = Campaign.Regions.HomeValleyLayout.RegionId + ":" + SmokeRemovedBuilding + "#1",
                        BuildingTypeId = SmokeRemovedBuilding,
                        RegionId = Campaign.Regions.HomeValleyLayout.RegionId,
                        Position = new Vector2(3f, -9f),
                        GridX = 3,
                        GridY = -9,
                        Health = 100f,
                        ConstructionState = BuildingConstructionState.Operational,
                        InvestedScrap = 11,
                        Inventory = Array.Empty<CargoEntry>(),
                        QueueIds = Array.Empty<string>(),
                    },
                })
                .ToArray();
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            var buf = new ByteBuf();
            buf.WriteSize(2);
            buf.WriteString(SmokeRemovedId);
            buf.WriteString("primitive_chip");
            buf.WriteString("enemy.scout.name");
            buf.WriteInt(9);
            buf.WriteInt(2);
            buf.WriteString(SmokeRemovedBuilding);
            buf.WriteString(SaveContentReconciler.KindBuilding);
            buf.WriteString("building.warehouse.name");
            buf.WriteInt(25);
            buf.WriteInt(2);
            SaveContentReconciler.OverrideForTests(new TbRemovedContent(buf), id => id != SmokeRemovedId);
            load.onClick.Invoke();
            Next(27, $"回到主菜单；测试捷径：槽位 {slot + 1} 存档里放一件已移除内容（测试表：退还 9 废料）与一座已移除类型的建筑（投入 11 废料），点“读取”");
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
            int groundBefore = SessionState.GetInt(K + "GroundBefore", 0);
            int groundNow = st?.GroundItems?.Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount) ?? 0;
            // 建筑的返还走仓库（放不下的变成地面物，机器之后搬回），所以核对“库存 + 地面废料”。
            Check(st != null && st.PrimitiveChips.All(c => c.CardDefId != SmokeRemovedId) && st.BuildingRecords.All(b => b.BuildingTypeId != SmokeRemovedBuilding)
                  && st.Scrap + groundNow == before + groundBefore + 9 + 11,
                $"读档进入游戏：已移除内容转换为废料、已移除类型的建筑拆掉并全额返还（库存 + 地面废料 {before + groundBefore}→{st?.Scrap + groundNow}，+9 +11）");
            Check(captions.Any(c => c.Contains("静默侦察机") && c.Contains("9 废料")), "进入游戏后弹出迁移字幕");
            Check(captions.Any(c => c.Contains("仓库") && c.Contains("×1") && c.Contains("11 废料")), "进入游戏后弹出“建筑已移除、已拆除并返还”的字幕（FG3-LOG-09）");
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
            // FG3-LOG-09（DEBT-FG0ARCH02-10）：读档后马上看这段传送带的“实测吞吐”，统计窗口接着存档前的（不是从 0 秒重新累计）。
            BeltNetworkStats loadedStats = default;
            bool statsOk = bk != null && bk.TryGetNetworkStats(bk.NetworkOf(bx, by), out loadedStats);
            long savedBuckets = long.Parse(SessionState.GetString(K + "BeltSavedBuckets", "0"));
            Check(statsOk && savedBuckets > 0 && loadedStats.WindowSeconds >= 15f,
                $"读档后实测吞吐接着存档前的统计窗口：窗口 {loadedStats.WindowSeconds:F0} 游戏秒（存档时已走满 {savedBuckets} 桶；改前读档后从 0 秒重新累计）");
            // FG1-SIG-03：存档时信号在机器里 → 真实“保存并返回主菜单 → 读取”后信号仍在那台机器里（接管恢复、镜头进直控、HUD）。
            int sigSaved = SessionState.GetInt(K + "SigSaved", 0);
            if (sigSaved == 0)
            {
                Check(Campaign.Signal.SignalPresence.AtCore && !Campaign.Signal.SignalUplinkService.IsPending,
                    "存档时信号在归还核心：读档后仍在归还核心");
                BeginAwayTrip(); // FG4-ECO-09：核心被毁之前先走一遍离家报告的正式路径
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
            Write($"性能警告 {SessionState.GetInt(K + "PerfWarnings", 0)} 条（FG-TOOL-01：性能检查只测一次，超阈值不到 2 倍记警告、不计入报错；超 2 倍才算报错）");
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

        /// <summary>FG-TOOL-01：性能检查只测一次；超阈值不到 2 倍写“⚠ 性能警告”（不计报错），超 2 倍或功能条件不满足才算报错。</summary>
        private static void CheckPerf(bool ok, string message, params PerfGate.Metric[] perf)
        {
            if (PerfGate.Expect(ok, message, perf, Check, Write) == PerfGate.Level.Warn)
            {
                SessionState.SetInt(K + "PerfWarnings", SessionState.GetInt(K + "PerfWarnings", 0) + 1);
            }
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
            KeyCode held2 = (chord.Mods & InputModifier.Ctrl) != 0 && (chord.Mods & InputModifier.Alt) != 0 ? KeyCode.LeftAlt : KeyCode.None;
            InputRouter.DebugSetReader(new ScriptedReader { Key = chord.Key, KeyFrame = Time.frameCount + 1, Held = held, Held2 = held2 });
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
            /// <summary>FG3-LOG-08：第二个修饰键（Ctrl+Alt+数字这类两个修饰的组合）。</summary>
            public KeyCode Held2 = KeyCode.None;

            public bool GetKey(KeyCode key) => key != KeyCode.None && (key == Held || key == Held2) && Time.frameCount == KeyFrame;
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
