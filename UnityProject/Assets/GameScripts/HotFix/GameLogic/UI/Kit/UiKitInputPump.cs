using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using UnityEngine;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：界面层全局快捷键的唯一处理点，挂在浮层宿主上，<b>LateUpdate</b> 执行——各玩法系统与面板在 Update 里
    /// 先消费自己的键（同帧同键只有第一个消费者拿到，见 <see cref="InputRouter"/>），剩下的才轮到这里：
    /// 1. 取消键（FGR-UX-001）：拖动中先取消拖动 → 关闭最上层（<see cref="UiEscapeStack"/>）→ 在区域里时打开暂停菜单；
    /// 2. 通知中心开关（FGR-UX-020）；
    /// 3. 速度 0.5x / 1x / 2x / 3x（FG13 第 5 节：1 / 2 / 3 / 4 键）——驱动统一时钟（<see cref="StrategyClock"/> 是它的外观）；
    /// 4. “尚未开放”的动作（fg.TbInputAction status=reserved）：按下时发一条信息通知说明，不静默（IC-REQ-013）。
    /// 每帧 O(动作数) 次按键查询，与实体数量无关。
    /// </summary>
    public sealed class UiKitInputPump : MonoBehaviour
    {
        /// <summary>自检可读：最近一次由取消键打开暂停菜单的帧。</summary>
        public static int LastPauseMenuFrame { get; private set; } = -1;

        private void LateUpdate()
        {
            Process();
        }

        public static void Process()
        {
            bool inWorld = CampaignSession.Current != null && GameRoot.AnyRegionActive;

            // 点到界面以外（世界里）时文本框交出焦点——否则命名完蓝图点回地图，快捷键会一直处于让位状态。
            if ((InputRouter.Reader.GetMouseButtonDown(0) || InputRouter.Reader.GetMouseButtonDown(1))
                && !InputRouter.IsUiPointerBlocked() && UiTextFocusProbe.AnyTextInputFocused())
            {
                UiTextFocusProbe.BlurFocusedText();
            }

            if (InputRouter.ConsumeGlobalAction(GameActionId.Cancel, allowDuringModal: true))
            {
                if (UiDragDrop.IsDragging)
                {
                    UiDragDrop.Cancel();
                }
                else if (!UiEscapeStack.CloseTop() && inWorld && InputRouter.Scope != InputScope.None)
                {
                    PauseMenuUIToolkit.Open();
                    LastPauseMenuFrame = Time.frameCount;
                }
            }

            if (inWorld)
            {
                ProcessWorldKeys();
            }
        }

        /// <summary>只在游戏世界里生效的键：通知中心、速度、尚未开放动作的提示。拆出来给自检直接驱动。</summary>
        public static void ProcessWorldKeys()
        {
            // FG1-SIG-02：撤销 / 重做交给当前打开的编辑器（电路编辑器）；必须在下面“尚未开放动作”提示之前消费，
            // 否则 Ctrl+Z 会被当成建造撤销（FG3-LOG-07，尚未开放）弹提示。没有编辑器打开时不消费，提示照旧。
            UiUndoRouter.Process();

            if (InputRouter.ConsumeGlobalAction(GameActionId.ToggleNotificationCenter, allowDuringModal: true))
            {
                NotificationHudUIToolkit.ToggleCenter();
            }

            // FG1-SIG-01（FGU-19）：信号核面板开关。关着时按上下文（战略 / 接入）读；开着时面板是模态（上下文 = 界面），
            // 同一个键再按一次关闭——确认框在最上面时不抢（先处理确认框）。
            if (SignalCore.SignalCoreHudUIToolkit.IsOpen)
            {
                if (!UiConfirmDialog.IsOpen && InputRouter.ConsumeGlobalAction(GameActionId.OpenSignalCore, allowDuringModal: true))
                {
                    SignalCore.SignalCoreHudUIToolkit.Close();
                }
            }
            else if (InputRouter.ConsumeContextAction(GameActionId.OpenSignalCore))
            {
                SignalCore.SignalCoreHudUIToolkit.Open();
            }

            // FG1-SIG-06（FGU-44）：暴露面板开关（默认 Alt+P）。开着时是模态，同一个键再按一次关闭。
            if (SignalCore.SignalCoreHudUIToolkit.IsExposureOpen)
            {
                if (!UiConfirmDialog.IsOpen && InputRouter.ConsumeGlobalAction(GameActionId.OpenExposure, allowDuringModal: true))
                {
                    SignalCore.SignalCoreHudUIToolkit.CloseExposure();
                }
            }
            else if (InputRouter.ConsumeContextAction(GameActionId.OpenExposure))
            {
                SignalCore.SignalCoreHudUIToolkit.OpenExposure();
            }

            // FG3-LOG-02：施工队列开着时是模态（上下文 = 界面），同一个键（默认 Alt+B）再按一次关闭；关着时由家园建造模式按战略 / 建造上下文打开。
            if (ConstructionQueuePanelUIToolkit.IsOpen && !UiConfirmDialog.IsOpen
                && InputRouter.ConsumeGlobalAction(GameActionId.ConstructionQueue, allowDuringModal: true))
            {
                ConstructionQueuePanelUIToolkit.Close();
            }

            // FG3-LOG-07：布局库开着时是模态（上下文 = 界面），同一个键（默认 Ctrl+B）再按一次关闭；关着时由家园建造模式按战略 / 建造上下文打开。
            if (LayoutLibraryPanelUIToolkit.IsOpen && !UiConfirmDialog.IsOpen
                && InputRouter.ConsumeGlobalAction(GameActionId.LayoutLibrary, allowDuringModal: true))
            {
                LayoutLibraryPanelUIToolkit.Close();
            }

            // FG3-LOG-06：电网面板开关（默认 Alt+G）。开着时是模态（上下文 = 界面），同一个键再按一次关闭；关着时按战略 / 建造上下文打开。
            if (PowerPanelUIToolkit.IsOpen)
            {
                if (!UiConfirmDialog.IsOpen && InputRouter.ConsumeGlobalAction(GameActionId.OpenPowerGrid, allowDuringModal: true))
                {
                    PowerPanelUIToolkit.Close();
                }
            }
            else if (InputRouter.ConsumeContextAction(GameActionId.OpenPowerGrid) && GameLogic.Campaign.WorldSim.WorldSimulation.Home != null
                     && GameLogic.Campaign.WorldSim.WorldSimulation.Home.IsLoaded)
            {
                PowerPanelUIToolkit.Open();
            }

            ProcessLibraryKeys();

            if (InputRouter.ConsumeAction(GameActionId.SpeedHalf, InputScope.Strategy))
            {
                StrategyClock.SetSpeed(0.5f);
            }
            else if (InputRouter.ConsumeAction(GameActionId.SpeedNormal, InputScope.Strategy))
            {
                StrategyClock.SetSpeed(1f);
            }
            else if (InputRouter.ConsumeAction(GameActionId.SpeedDouble, InputScope.Strategy))
            {
                StrategyClock.SetSpeed(2f);
            }
            else if (InputRouter.ConsumeAction(GameActionId.SpeedTriple, InputScope.Strategy))
            {
                StrategyClock.SetSpeed(3f); // FG0-ARCH-01（FGR-ARC-009）：统一时钟加 3x 档。
            }

            // FG1-SIG-07（FGR-SIG-051、FG01 第 4 章快捷键）：跳回家园 / 跳回上一台机器（战略与接入上下文），覆盖网络叠加层（战略与建造上下文）。全部可重绑。
            if (InputRouter.ConsumeContextAction(GameActionId.JumpHome))
            {
                Campaign.Signal.SignalUplinkService.RequestJumpHome();
            }
            if (InputRouter.ConsumeContextAction(GameActionId.JumpPreviousMachine))
            {
                Campaign.Signal.SignalUplinkService.RequestJumpPrevious();
            }
            ProcessOverlayKeys();

            var all = InputActionCatalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                InputActionDef def = all[i];
                if (def.Status == InputActionStatus.Reserved && InputRouter.ConsumeContextAction(def.Action))
                {
                    NotificationCenter.Post("feature_locked", def.NameKey);
                    GuidanceHooks.Raise(GuidanceHooks.FirstReservedAction);
                }
            }
        }

        /// <summary>
        /// FG3-LOG-08（FGR-LOG-080 / 082；FGU-12；卡片“叠加层的快捷键”）：叠加层与“为什么不工作”的键（战略与建造上下文，全部可重绑）。
        /// - 叠加层切换（默认 O，FG13 第 5 节）：开着就关，关着就重开最近用过的那一种（从没用过是“信号覆盖”，与 FG1-SIG-07 一致；家园以外最近那一种画不出来时开关信号覆盖）；
        /// - 叠加层选择器（默认 Alt+O）：开关选择器（非模态，8 种一键切换 + 图例；家园以外不打开，发说明）；
        /// - “为什么不工作”（默认 Ctrl+O）：开关停工清单（非模态，停靠左侧，点条目镜头跳过去）；
        /// - 8 种叠加层直达（默认 Ctrl+Alt+1～8）：这一种开着就关，否则切到这一种。
        /// 确认框在最上面时一律不抢。
        /// </summary>
        public static void ProcessOverlayKeys()
        {
            if (UiConfirmDialog.IsOpen)
            {
                return;
            }
            if (InputRouter.ConsumeContextAction(GameActionId.ToggleOverlay))
            {
                GameLogic.View.OverlayService.ToggleCurrent();
            }
            if (InputRouter.ConsumeContextAction(GameActionId.OverlaySelector))
            {
                OverlayHudUIToolkit.ToggleSelector();
            }
            if (InputRouter.ConsumeContextAction(GameActionId.OpenDiagnosis))
            {
                DiagnosisPanelUIToolkit.Toggle();
            }
            for (int i = 0; i < GameLogic.View.OverlayService.KindCount; i++)
            {
                var kind = (GameLogic.View.OverlayKind)(i + 1);
                if (InputRouter.ConsumeContextAction(GameLogic.View.OverlayService.ActionOf(kind)))
                {
                    GameLogic.View.OverlayService.ToggleFromKey(kind); // 家园以外要打开只在家园显示的一种：不切换，发说明（不静默失效）。
                }
            }
        }

        /// <summary>
        /// FG2-FW-05：固件库键（默认 I）与图鉴键（默认 C）。两者登记在全部上下文（含“界面”），所以面板开着（模态）时也能读到；确认框在最上面时一律不抢。
        /// - 固件库键：开着再按一次关闭；关着就打开。
        /// - 图鉴键：鼠标正悬停在带图鉴链接的提示目标上 → 跳到那条条目（<see cref="CodexHoverLink.TryJump"/>，FGR-UX-030 / 051）；否则开关图鉴。
        /// - 按当前最上层决定：层级更高的面板（图鉴盖着固件库；统计 / 反应记录 / 按键面板盖着图鉴……）开着时，这个键不起作用——
        ///   不在它们下面开出一个看不见的模态（Esc 会先关掉看不见的那个），也不关掉被盖住的那个。
        /// </summary>
        public static void ProcessLibraryKeys()
        {
            if (UiConfirmDialog.IsOpen)
            {
                return;
            }
            if (InputRouter.ConsumeContextAction(GameActionId.OpenFirmware)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(FirmwareLibraryPanelUIToolkit.Order))
            {
                FirmwareLibraryPanelUIToolkit.Toggle();
            }
            // FG4-ECO-01：物资键（默认 Alt+I）：开着再按一次关闭；图鉴盖在上面时不起作用（同固件库键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenItems)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(ItemsPanelUIToolkit.Order))
            {
                ItemsPanelUIToolkit.Toggle();
            }
            // FG4-ECO-06：常驻规则键（默认 Alt+R，全部上下文）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同物资键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenRules)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(RulesPanelUIToolkit.Order))
            {
                RulesPanelUIToolkit.Toggle();
            }
            // FG4-ECO-07：机器名册键（默认 N，全部上下文；名册里的文本框有焦点时快捷键自动让位）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同物资键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenRoster)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(RosterPanelUIToolkit.Order))
            {
                RosterPanelUIToolkit.Toggle();
            }
            // FG5-RND-01：研发树键（默认 K，全部上下文，可重绑）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同物资键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenResearch)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(ResearchTreePanelUIToolkit.Order))
            {
                ResearchTreePanelUIToolkit.Toggle();
            }
            // FG5-RND-04：配方书键（默认 Alt+F，全部上下文，可重绑）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同研发树键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenRecipeBook)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(FusionPanelUIToolkit.Order))
            {
                FusionPanelUIToolkit.ToggleBook();
            }
            // FG5-RND-05：情报键（默认 Y，全部上下文，可重绑）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同研发树键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenIntel)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(IntelPanelUIToolkit.Order))
            {
                IntelPanelUIToolkit.Toggle();
            }
            // FG4-ECO-08：统计键（默认 Alt+T，全部上下文）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同物资键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenStats)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(StatsPanelUIToolkit.Order))
            {
                StatsPanelUIToolkit.Toggle();
            }
            // FG4-ECO-09：离家报告键（默认 Alt+H，全部上下文）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同统计键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenAwayReport)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(AwayReportPanelUIToolkit.Order))
            {
                AwayReportPanelUIToolkit.Toggle();
            }
            // FG6-DEF-06：防御总览键（默认 Alt+E，全部上下文）：开着再按一次关闭；更高层的面板盖在上面时不起作用（同统计键）。
            if (InputRouter.ConsumeContextAction(GameActionId.OpenDefense)
                && !MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(DefenseOverviewPanelUIToolkit.Order))
            {
                DefenseOverviewPanelUIToolkit.Toggle();
            }
            // FG6-DEF-06 审查修复（FG00 B02 键盘鼠标都能完成）：观战 / 停止观战（默认 Alt+V）、跟随战斗（默认 Ctrl+F）。只在战略上下文（面板开着时是界面上下文，不响应）；
            // 与观战栏按钮同一入口（RaidSpectateService.Toggle / ToggleFollow），没有到达的突袭时给“不行”的提示音，原因写在观战栏。
            if (InputRouter.ConsumeContextAction(GameActionId.SpectateRaid) && !Campaign.Defense.RaidSpectateService.Toggle(CampaignSession.Current))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            if (InputRouter.ConsumeContextAction(GameActionId.SpectateFollow) && !Campaign.Defense.RaidSpectateService.ToggleFollow(CampaignSession.Current))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            if (InputRouter.ConsumeContextAction(GameActionId.OpenCodex)
                && !UiKitPanelHost.AnyModalAbove(MechanicCodexPanelUIToolkit.Order) && !CodexHoverLink.TryJump())
            {
                MechanicCodexPanelUIToolkit.Toggle();
            }
        }
    }
}
