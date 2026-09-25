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
    /// 3. 速度 0.5x / 1x / 2x（FG13 第 5 节：1 / 2 / 3 键）——驱动既有 <see cref="StrategyClock"/>；
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
            if (InputRouter.ConsumeGlobalAction(GameActionId.ToggleNotificationCenter, allowDuringModal: true))
            {
                NotificationHudUIToolkit.ToggleCenter();
            }

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
    }
}
