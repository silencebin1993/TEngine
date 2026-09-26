using UnityEngine;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：UI 基础件的挂载入口（由 <c>GameRoot.Startup</c> 调用，进程内只挂一次）。
    /// 每个面板一个 DontDestroyOnLoad 宿主、一个 UIDocument；分层（sortingOrder）见各面板的 Order 常量与
    /// UI_WORKFLOW_GUIDE.md 第 4 节：建造栏 30030 &lt; 通知 30040 &lt; 暂停菜单 30070 &lt; 按键面板 30080 &lt; 样例页 30090 &lt; 浮层 30200。
    /// </summary>
    public static class UiKitRuntime
    {
        private static bool _mounted;

        public static bool Mounted => _mounted;

        public static void Mount()
        {
            if (_mounted)
            {
                return;
            }
            _mounted = true;
            GameObject overlay = Create<UiKitOverlayUIToolkit>("[UiKitOverlayHost]");
            overlay.AddComponent<UiKitInputPump>();
            Create<NotificationHudUIToolkit>("[NotificationHudHost]");
            Create<BuildModeHudUIToolkit>("[BuildModeHudHost]"); // FG0-ARCH-04：家园建造模式入口与建造栏（30030，低于通知）。
            Create<WorldBarHudUIToolkit>("[WorldBarHost]"); // FG0-ARCH-01：世界时间条（游戏日、0.5x～3x、暂停、关注点），右上角 HUD 层 3。
            Create<PauseMenuUIToolkit>("[PauseMenuHost]");
            Create<KeyBindingsPanelUIToolkit>("[KeyBindingsHost]");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Create<UiKitGalleryUIToolkit>("[UiKitGalleryHost]");
#endif
        }

        /// <summary>离开游戏世界（回主菜单）时统一收起世界里的界面基础件并清空 Esc 栈。
        /// 由 <c>GameRoot.EndRun</c>（唯一回菜单出口）调用；主菜单自己的按键面板在回菜单之后才会再打开，不受影响。</summary>
        public static void CloseWorldUi()
        {
            if (UiDragDrop.IsDragging)
            {
                UiDragDrop.Cancel();
            }
            UiContextMenu.Close();
            UiTooltip.Hide();
            UiConfirmDialog.DiscardAll();
            KeyBindingsPanelUIToolkit.Close();
            UiKitGalleryUIToolkit.Close();
            NotificationHudUIToolkit.CloseCenter();
            PauseMenuUIToolkit.CloseForExit();
            UiEscapeStack.Clear();
        }

        private static GameObject Create<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            go.AddComponent<T>();
            return go;
        }
    }
}
