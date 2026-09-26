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

        /// <summary>本 Story 埋下的全部钩子（FG15-UX-04 的“钩子 ↔ 引导内容”对照表从这里取）。</summary>
        public static readonly IReadOnlyList<string> Known = new[]
        {
            NotificationCenterFirstOpen, KeyBindingsFirstOpen, PauseMenuFirstOpen,
            FirstUrgentNotification, FirstAutoPause, FirstReservedAction,
            BuildModeFirstOpen, FirstPlacement, FirstBlockedPlacement,
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
