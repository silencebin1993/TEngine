using UnityEngine;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01：UI 基础件的挂载入口（由 <c>GameRoot.Startup</c> 调用，进程内只挂一次）。
    /// 每个面板一个 DontDestroyOnLoad 宿主、一个 UIDocument；分层（sortingOrder）见各面板的 Order 常量与
    /// UI_WORKFLOW_GUIDE.md 第 4 节：反应弹字 -1（所有 HUD 之下）&lt; 建造栏 30030 &lt; 信号核 30035 &lt; 通知 30040 &lt; 施工队列 30045 &lt; 端口面板 30046 &lt; 节点面板 30047 &lt; 暂停菜单 30070 &lt; 固件库 30072 &lt; 物资 30074 &lt; 图鉴 30075 &lt; 统计 30076 &lt; 反应记录 30077 &lt; 按键面板 30080 &lt; 样例页 30090 &lt; 浮层 30200。
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
            Create<ItemsPanelUIToolkit>("[ItemsPanelHost]");
            Create<RulesPanelUIToolkit>("[RulesPanelHost]"); // FG4-ECO-06：常驻规则（30073，固件库之上、物资与图鉴之下：“?”打开的图鉴盖在它上面）。 // FG4-ECO-01：物资面板（30074，暂停菜单与固件库之上、图鉴之下：点图标打开的图鉴盖在它上面）。
            Create<RosterPanelUIToolkit>("[RosterPanelHost]"); // FG4-ECO-07：机器名册（30071，暂停菜单之上、固件库之下：“?”打开的图鉴盖在它上面）。
            Create<TestRangePanelUIToolkit>("[TestRangeHost]"); // FG5-RND-03：靶场（30068，暂停菜单之下：从靶场“?”打开的图鉴盖在它上面）。
            Create<ResearchTreePanelUIToolkit>("[ResearchTreeHost]"); // FG5-RND-01：研发树（30069，暂停菜单之下：从研发树“?”打开的图鉴盖在它上面）。
            Create<AwayReportPanelUIToolkit>("[AwayReportHost]"); // FG4-ECO-09：离家报告（30078，统计 / 反应记录之上、按键面板之下）。
            Create<StatsPanelUIToolkit>("[StatsPanelHost]"); // FG2-FW-04：统计面板（战斗 · 反应伤害归因；FG4-ECO-08 加生产段，30076，暂停菜单之上）。
            Create<ReactionLogPanelUIToolkit>("[ReactionLogHost]"); // FG2-FW-04：反应记录（日志 / 伤害归因 / 反应图鉴，30077，暂停菜单之上）。
            Create<KeyBindingsPanelUIToolkit>("[KeyBindingsHost]");
            Create<NewGamePanelUIToolkit>("[NewGameHost]"); // FG3-GEN-01：新游戏设置（30082，主菜单“新建”选好存档槽后打开）。
            Create<StrategicMapUIToolkit>("[StrategicMapHost]"); // FG3-GEN-01：战略地图（30050，HUD 之上、暂停菜单之下）。
            Create<MinimapHudUIToolkit>("[MinimapHost]"); // FG3-GEN-01：小地图（右下角 HUD，-2 层，不挡面板）。
            Create<ConstructionQueuePanelUIToolkit>("[ConstructionQueueHost]"); // FG3-LOG-02：施工队列（30045，通知之上、字幕之下）。
            Create<BeltPortPanelUIToolkit>("[BeltPortHost]"); // FG3-LOG-03：建筑端口面板（30046，施工队列之上、字幕之下）。
            Create<BeltNodePanelUIToolkit>("[BeltNodeHost]"); // FG3-LOG-04：物流节点面板（分流器 / 合流器 / 地下传送带，30047，端口面板之上、字幕之下）。
            Create<PipePanelUIToolkit>("[PipePanelHost]"); // FG3-LOG-05：管线面板（网络读数、储罐、阀门、冲洗，30048，节点面板之上、字幕之下）。
            Create<PowerPanelUIToolkit>("[PowerPanelHost]"); // FG3-LOG-06：电网面板（每个电网的曲线、优先级、关停，30049，管线面板之上、字幕之下）。
            Create<LayoutLibraryPanelUIToolkit>("[LayoutLibraryHost]"); // FG3-LOG-07：布局库（30044，通知之上、施工队列之下；跨存档）。
            Create<OverlayHudUIToolkit>("[OverlayHudHost]"); // FG3-LOG-08：叠加层停靠条 / 选择器（FGU-12）/ 世界标签（HUD 4）。
            Create<DiagnosisPanelUIToolkit>("[DiagnosisPanelHost]"); // FG3-LOG-08：“为什么不工作”（30043，非模态停靠左侧）。
            Create<ProductionPanelUIToolkit>("[ProductionPanelHost]"); // FG4-ECO-02：生产建筑通用面板（状态与原因、配方、进度、缓存，30042，通知之上、诊断面板之下）。
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
            ItemsPanelUIToolkit.Close();
            RulesPanelUIToolkit.Close();
            RosterPanelUIToolkit.Close();
            AwayReportPanelUIToolkit.Close();
            ResearchTreePanelUIToolkit.Close();
            TestRangePanelUIToolkit.Close();
            ConstructionQueuePanelUIToolkit.Close();
            BeltPortPanelUIToolkit.Close();
            BeltNodePanelUIToolkit.Close();
            PipePanelUIToolkit.Close();
            PowerPanelUIToolkit.Close();
            LayoutLibraryPanelUIToolkit.Close();
            DiagnosisPanelUIToolkit.Close();
            ProductionPanelUIToolkit.Close();
            OverlayHudUIToolkit.CloseSelector();
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
