using UnityEngine;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：UI 基础件的挂载入口（由 <c>GameRoot.Startup</c> 调用，进程内只挂一次）。
    /// 每个面板一个 DontDestroyOnLoad 宿主、一个 UIDocument；分层（sortingOrder）见各面板的 Order 常量与
    /// UI_WORKFLOW_GUIDE.md 第 4 节：反应弹字 -1（所有 HUD 之下）&lt; 建造栏 30030 &lt; 信号核 30035 &lt; 通知 30040 &lt; 施工队列 30045 &lt; 端口面板 30046 &lt; 暂停菜单 30070 &lt; 固件库 30072 &lt; 图鉴 30075 &lt; 统计 30076 &lt; 反应记录 30077 &lt; 按键面板 30080 &lt; 样例页 30090 &lt; 浮层 30200。
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
            Create<ReactionPopupHudUIToolkit>("[ReactionPopupHost]"); // FG2-FW-04：反应 / 读法弹字（-1，所有 HUD 与面板之下，不拦截点击）。
            Create<NotificationHudUIToolkit>("[NotificationHudHost]");
            Create<BuildModeHudUIToolkit>("[BuildModeHudHost]"); // FG0-ARCH-04：家园建造模式入口与建造栏（30030，低于通知）。
            Create<WorldBarHudUIToolkit>("[WorldBarHost]"); // FG0-ARCH-01：世界时间条（游戏日、0.5x～3x、暂停、关注点），右上角 HUD 层 3。
            Create<SignalCore.SignalCoreHudUIToolkit>("[SignalCoreHost]"); // FG1-SIG-01：信号位置 HUD（顶部居中）与信号核面板（30035）。
            Create<PauseMenuUIToolkit>("[PauseMenuHost]");
            Create<FirmwareLibraryPanelUIToolkit>("[FirmwareLibraryHost]"); // FG2-FW-05：固件库（30072，暂停菜单之上、图鉴之下）。
            Create<MechanicCodexPanelUIToolkit>("[MechanicCodexHost]"); // FG1-HUD-01：机制图鉴（30075，暂停菜单之上、按键面板之下）。
            Create<StatsPanelUIToolkit>("[StatsPanelHost]"); // FG2-FW-04：统计面板（战斗 · 反应伤害归因；FG4-ECO-08 加生产段，30076，暂停菜单之上）。
            Create<ReactionLogPanelUIToolkit>("[ReactionLogHost]"); // FG2-FW-04：反应记录（日志 / 伤害归因 / 反应图鉴，30077，暂停菜单之上）。
            Create<KeyBindingsPanelUIToolkit>("[KeyBindingsHost]");
            Create<NewGamePanelUIToolkit>("[NewGameHost]"); // FG3-GEN-01：新游戏设置（30082，主菜单“新建”选好存档槽后打开）。
            Create<StrategicMapUIToolkit>("[StrategicMapHost]"); // FG3-GEN-01：战略地图（30050，HUD 之上、暂停菜单之下）。
            Create<MinimapHudUIToolkit>("[MinimapHost]"); // FG3-GEN-01：小地图（右下角 HUD，-2 层，不挡面板）。
            Create<ConstructionQueuePanelUIToolkit>("[ConstructionQueueHost]"); // FG3-LOG-02：施工队列（30045，通知之上、字幕之下）。
            Create<BeltPortPanelUIToolkit>("[BeltPortHost]"); // FG3-LOG-03：建筑端口面板（30046，施工队列之上、字幕之下）。
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
            MechanicCodexPanelUIToolkit.Close();
            FirmwareLibraryPanelUIToolkit.Close();
            ReactionLogPanelUIToolkit.Close();
            StatsPanelUIToolkit.Close();
            ConstructionQueuePanelUIToolkit.Close();
            BeltPortPanelUIToolkit.Close();
            StrategicMapUIToolkit.Close();
            NewGamePanelUIToolkit.Close();
            GameLogic.Campaign.Feedback.ReactionPopups.Clear();
            GameLogic.Campaign.Feedback.CameraNudge.Reset();
            SignalCore.SignalCoreHudUIToolkit.Close();
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
