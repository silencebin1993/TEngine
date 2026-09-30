using System;
using System.Collections.Generic;
using GameLogic.Settings;

namespace GameLogic.Core
{
    /// <summary>
    /// FG0-UX-01（FG00 B14）：引导钩子。新系统第一次出现时发出一个钩子事件（“首次打开通知中心”……）；
    /// 引导内容本身统一在 FG15-UX-04 实现（用户决定：教学最后再做），届时订阅 <see cref="FirstRaised"/> 并按
    /// <see cref="Known"/> 核对“没有遗漏的钩子”。
    /// 每个钩子对每个玩家只触发一次（记在本机设置 <see cref="GameSettings"/> 里，跨战役），重看由 FG15-UX-04 提供。
    /// </summary>
    public static class GuidanceHooks
    {
        public const string NotificationCenterFirstOpen = "ui.notification_center.first_open";
        public const string KeyBindingsFirstOpen = "ui.key_bindings.first_open";
        public const string PauseMenuFirstOpen = "ui.pause_menu.first_open";
        public const string FirstUrgentNotification = "ui.notification.first_urgent";
        public const string FirstAutoPause = "ui.notification.first_auto_pause";
        public const string FirstReservedAction = "ui.input.first_reserved_action";
        /// <summary>FG0-ARCH-04：第一次打开建造模式 / 第一次放下建筑 / 第一次放置被拒（FG03 第 4 节“首次打开建造菜单”的引导钩子）。</summary>
        public const string BuildModeFirstOpen = "build.mode.first_open";
        public const string FirstPlacement = "build.placement.first";
        public const string FirstBlockedPlacement = "build.placement.first_blocked";
        /// <summary>FG0-ARCH-05：第一次看到“生成中”的占位地貌（镜头移到还没生成的区块；FG17 第 4 节）。</summary>
        public const string WorldFirstGenerating = "world.generating.first_seen";
        /// <summary>FG0-ARCH-01：第一次把镜头飞到另一个地点（家园 ↔ 远征 / 突袭；“整个世界同时运行”的首次体验）。</summary>
        public const string WorldFirstFocusSwitch = "world.focus.first_switch";
        /// <summary>FG0-ARCH-01：第一次派遣远征而家园继续运行（“派遣不再退出家园”的首次说明时机）。</summary>
        public const string WorldFirstDispatch = "world.dispatch.first";
        /// <summary>FG0-ARCH-01：第一次看到行进中的突袭（FG6-DEF-04 的预警引导会接在这里）。</summary>
        public const string WorldFirstRaidInTransit = "world.raid.first_in_transit";
        /// <summary>FG0-ARCH-02：第一次放下传送带（FG3-LOG-03 的“传送带怎么用”引导接在这里）。</summary>
        public const string LogisticsFirstBelt = "logistics.belt.first_placed";
        /// <summary>FG0-ARCH-02：第一次出现堵塞的传送带（FGR-LOG-025“下游满了就停下”的首次说明时机）。</summary>
        public const string LogisticsFirstBlocked = "logistics.belt.first_blocked";
        /// <summary>FG1-SIG-01：第一次打开信号核面板（引导内容在 FG15-UX-04：你是信号、固件放进槽位、只能在家园改）。</summary>
        public const string SignalCoreFirstOpen = "signal.core.first_open";
        /// <summary>FG1-SIG-02：第一次在电路编辑器里遇到接入口（标出接入口，或打开带接入口的蓝图）。引导内容在 FG15-UX-04：指向双态预览，“接入后会变成这样”。</summary>
        public const string CircuitUplinkFirstSeen = "circuit.uplink.first_seen";

        /// <summary>FG1-SIG-03：第一次接入一台机器（过渡完成、插入固件）。</summary>
        public const string SignalFirstUplink = "signal.uplink.first_commit";

        /// <summary>FG1-SIG-06：第一次带回加密固件（FG13 引导表“第一次拿到加密固件 → 解析台、裸跑”；内容在 FG15-UX-04）。</summary>
        public const string SignalFirstEncryptedFirmware = "signal.raw.first_encrypted";
        /// <summary>FG1-SIG-06：第一次打开暴露面板（引导内容在 FG15-UX-04：暴露从哪来、怎么降）。</summary>
        public const string ExposurePanelFirstOpen = "signal.exposure.first_open";

        /// <summary>FG1-SIG-07：第一次打开覆盖网络叠加层（引导内容在 FG15-UX-04：青色 / 红色圈的含义、中继要立在已连通的覆盖里）。</summary>
        public const string SignalCoverageFirstOverlay = "signal.coverage.first_overlay";
        /// <summary>FG1-SIG-07：第一次有机器走出信号覆盖（引导内容：覆盖外收不到命令、怎么铺中继）。</summary>
        public const string SignalCoverageFirstLeft = "signal.coverage.first_left";
        /// <summary>FG1-SIG-07：第一次有中继与核心断开（引导内容：断开处高亮、怎么接上）。</summary>
        public const string SignalRelayFirstCut = "signal.coverage.first_relay_cut";
        /// <summary>FG1-SIG-07：第一次远距离跳转（引导内容：过渡、冷却、跳回家园 / 上一台）。</summary>
        public const string SignalFirstFarJump = "signal.jump.first_far";
        /// <summary>FG1-VFX-01：第一次看到机器机身形变（引导内容：三类状态分别代表哪类固件、接入与离开都会切换）。</summary>
        public const string MorphFirstSeen = "firmware.morph.first_seen";
        /// <summary>FG1-HUD-01：第一次有机器进入安全模式（引导内容在 FG15-UX-04：盾形标记、只跑本地常规固件、怎么恢复；图鉴“安全模式”随之解锁）。</summary>
        public const string SafeModeFirstEnter = "signal.safe_mode.first_enter";
        /// <summary>FG2-FW-03：第一次看到单位头顶的状态标签图标（引导内容在 FG15-UX-04：标签是什么、悬停看名称 / 剩余时间 / 叠层）。</summary>
        public const string StatusTagFirstSeen = "firmware.status_tag.first_seen";
        /// <summary>FG2-FW-03：第一次打出一条已开放命名的具名反应（引导内容：两种标签凑在一起会反应、去图鉴查配方；首次慢放与图鉴解锁归 FG2-FW-04）。</summary>
        public const string ReactionFirstNamed = "firmware.reaction.first_named";
        /// <summary>FG2-FW-04：第一次打开反应记录面板（引导内容在 FG15-UX-04：日志怎么读、伤害归因的占比是什么、反应图鉴怎么解锁）。</summary>
        public const string ReactionLogFirstOpen = "firmware.reaction_log.first_open";
        /// <summary>FG2-FW-04：第一次打开统计面板（引导内容在 FG15-UX-04：伤害归因的累计与每场明细怎么读、只统计远征与突袭）。</summary>
        public const string StatsPanelFirstOpen = "stats.panel.first_open";
        /// <summary>FG2-FW-05：第一次打开固件库（引导内容在 FG15-UX-04：筛选 / 比较怎么用、锁定防误拆、批量分解会先确认；图鉴“固件库”系统说明随之解锁）。</summary>
        public const string FirmwareLibraryFirstOpen = "firmware.library.first_open";
        /// <summary>FG2-E2E-01（FG-GAP-050）：第一次在解析台用技术数据复原一条固件（引导内容在 FG15-UX-04：复原的是刻录数据、之后去信号核刻印 / 蓝图里选用；图鉴“数据复原”系统说明随之解锁）。</summary>
        public const string FirmwareRestoreFirstDone = "firmware.restore.first_done";
        /// <summary>FG3-GEN-01：第一次打开战略地图（引导内容在 FG15-UX-04：连续缩放、筛选、点哪飞哪、右键加标记）。</summary>
        public const string StrategicMapFirstOpen = "world.map.first_open";
        /// <summary>FG3-GEN-01：第一次打开新游戏设置（引导内容在 FG15-UX-04：种子、世界设置各项的含义、分享短码）。</summary>
        public const string NewGameSetupFirstOpen = "world.newgame.first_open";
        /// <summary>FG3-GEN-01：第一次在战略地图上加玩家标记。</summary>
        public const string MapMarkerFirstAdded = "world.map.first_marker";
        /// <summary>FG3-LOG-01：第一次规划搬迁（FGR-LOG-008）。</summary>
        public const string BuildFirstRelocate = "build.relocate.first";
        /// <summary>FG3-LOG-01：第一次框选批量拆除（FGR-LOG-007）。</summary>
        public const string BuildFirstBatchDemolish = "build.demolish.first_batch";
        /// <summary>FG3-LOG-01：第一次把条目放进快捷栏（FGR-LOG-002）。</summary>
        public const string BuildFirstHotbar = "build.hotbar.first_assign";
        /// <summary>FG3-LOG-01：第一次拖拽铺设（两格以上，FGR-LOG-004）。</summary>
        public const string BuildFirstDrag = "build.drag.first";
        /// <summary>FG3-LOG-02：第一次放下施工虚影（建筑或传送带，FGR-LOG-006）。</summary>
        public const string BuildFirstGhost = "build.ghost.first";
        /// <summary>FG3-LOG-02：第一次有虚影在等材料（FGR-LOG-006“等待材料”）。</summary>
        public const string BuildFirstWaitingMaterials = "build.ghost.first_waiting";
        /// <summary>FG3-LOG-02：第一次出现“有施工单却没有劳动力”（FG04 负向“家园没有劳动力”）。</summary>
        public const string BuildFirstNoLabor = "build.labor.first_none";
        /// <summary>FG3-LOG-02：第一次打开施工队列。</summary>
        public const string BuildQueueFirstOpen = "build.queue.first_open";
        /// <summary>FG3-LOG-02：第一次“优先建造这一片” / 在队列里调整优先级。</summary>
        public const string BuildFirstPrioritize = "build.priority.first";
        /// <summary>FG3-LOG-03：第一次有传送带接上建筑的输入 / 输出口（引导内容：输入口的带朝着建筑、输出口的带背离建筑、仓库输出过滤在哪设）。</summary>
        public const string LogisticsFirstPortConnected = "logistics.port.first_connected";
        /// <summary>FG3-LOG-03：第一次打开建筑的端口面板。</summary>
        public const string LogisticsPortPanelFirstOpen = "logistics.port.first_open";
        /// <summary>FG3-LOG-03：第一次用清带工具（FGR-LOG-026）。</summary>
        public const string LogisticsFirstClear = "logistics.belt.first_cleared";
        /// <summary>FG3-LOG-03：第一次有传送带被摧毁（FGR-LOG-027：物品落地、原位置留虚影、在施工队列里重建）。</summary>
        public const string LogisticsFirstDestroyed = "logistics.belt.first_destroyed";

        /// <summary>本 Story 埋下的全部钩子（FG15-UX-04 的“钩子 ↔ 引导内容”对照表从这里取）。</summary>
        public static readonly IReadOnlyList<string> Known = new[]
        {
            NotificationCenterFirstOpen, KeyBindingsFirstOpen, PauseMenuFirstOpen,
            FirstUrgentNotification, FirstAutoPause, FirstReservedAction,
            BuildModeFirstOpen, FirstPlacement, FirstBlockedPlacement,
            WorldFirstGenerating,
            WorldFirstFocusSwitch, WorldFirstDispatch, WorldFirstRaidInTransit,
            LogisticsFirstBelt, LogisticsFirstBlocked,
            SignalCoreFirstOpen, CircuitUplinkFirstSeen,
            SignalFirstUplink,
            SignalFirstEncryptedFirmware, ExposurePanelFirstOpen,
            SignalCoverageFirstOverlay, SignalCoverageFirstLeft, SignalRelayFirstCut, SignalFirstFarJump,
            MorphFirstSeen, SafeModeFirstEnter,
            StatusTagFirstSeen, ReactionFirstNamed,
            ReactionLogFirstOpen, StatsPanelFirstOpen,
            FirmwareLibraryFirstOpen,
            FirmwareRestoreFirstDone,
            StrategicMapFirstOpen, NewGameSetupFirstOpen, MapMarkerFirstAdded,
            BuildFirstRelocate, BuildFirstBatchDemolish, BuildFirstHotbar, BuildFirstDrag,
            BuildFirstGhost, BuildFirstWaitingMaterials, BuildFirstNoLabor, BuildQueueFirstOpen, BuildFirstPrioritize,
            LogisticsFirstPortConnected, LogisticsPortPanelFirstOpen, LogisticsFirstClear, LogisticsFirstDestroyed,
        };

        /// <summary>某钩子第一次触发时回调（参数为钩子 ID）。</summary>
        public static event Action<string> FirstRaised;

        public static string LastRaised { get; private set; }
        public static int RaisedCount { get; private set; }

        /// <summary>FG1-HUD-01：每一次触发（不只第一次）都会调用——机制图鉴按“首次接触”解锁条目（FGR-UX-051），
        /// 图鉴文件被删除 / 换机器时，玩家下一次接触同一机制还能重新解锁，不依赖“钩子只广播一次”。</summary>
        public static int TouchCount { get; private set; }

        /// <summary>触发钩子。第一次返回 true 并广播；之后返回 false（不重复打扰）。无论第几次，都解锁以它为钩子的图鉴条目。</summary>
        public static bool Raise(string hookId)
        {
            if (string.IsNullOrEmpty(hookId))
            {
                return false;
            }
            TouchCount++;
            GameLogic.Progression.MechanicCodex.OnHook(hookId);
            if (GameSettings.HasSeenGuidanceHook(hookId))
            {
                return false;
            }
            GameSettings.MarkGuidanceHookSeen(hookId);
            LastRaised = hookId;
            RaisedCount++;
            FirstRaised?.Invoke(hookId);
            return true;
        }
    }
}
