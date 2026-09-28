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
            MorphFirstSeen,
        };

        /// <summary>某钩子第一次触发时回调（参数为钩子 ID）。</summary>
        public static event Action<string> FirstRaised;

        public static string LastRaised { get; private set; }
        public static int RaisedCount { get; private set; }

        /// <summary>触发钩子。第一次返回 true 并广播；之后返回 false（不重复打扰）。</summary>
        public static bool Raise(string hookId)
        {
            if (string.IsNullOrEmpty(hookId) || GameSettings.HasSeenGuidanceHook(hookId))
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
