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
            if (InputRouter.ConsumeContextAction(GameActionId.ToggleOverlay))
            {
                GameLogic.View.SignalCoverageOverlayView.Toggle();
            }

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
            if (InputRouter.ConsumeContextAction(GameActionId.OpenCodex)
                && !UiKitPanelHost.AnyModalAbove(MechanicCodexPanelUIToolkit.Order) && !CodexHoverLink.TryJump())
            {
                MechanicCodexPanelUIToolkit.Toggle();
            }
        }
    }
}
