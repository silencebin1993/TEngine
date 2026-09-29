using System;
using GameLogic.Localization;
using GameLogic.Progression;
using TEngine;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-05（FGR-UX-030“提示里附带图鉴链接”、FGR-UX-051“从任何悬停提示按一个键就能跳到对应条目”；FG02 第 4 章“悬停固件时按一个键打开它的图鉴条目”）：
    /// 悬停跳图鉴的事件钩子。
    /// - 界面元素：提示内容填 <see cref="TooltipContent.CodexEntryId"/> 即可（固件用 <see cref="MechanicCodex.FirmwareEntryId"/>，反应用 <see cref="MechanicCodex.ReactionEntryId"/>）；
    /// - 世界对象（FG2-FW-03 单位头顶的状态标签、以后的敌人）：走 <see cref="UiTooltip.HoverWorld"/> 的提示内容，同一个字段；
    ///   不经提示的来源可以登记 <see cref="ExtraProvider"/>（返回当前悬停对象的条目 ID）。
    /// - 按图鉴键（默认 C，可重绑）时由 <see cref="UiKitInputPump"/> 调 <see cref="TryJump"/>：有悬停条目 → 广播 <see cref="JumpEvent"/>（GameEvent，参数为条目 ID）并打开图鉴定位到它；
    ///   没有 → 返回 false，由调用方按普通“打开图鉴”处理。跳转不解锁条目（没拿到的仍是剪影 + 获取途径）。
    /// </summary>
    public static class CodexHoverLink
    {
        /// <summary>GameEvent：悬停跳图鉴（参数 string = 条目 ID）。命名反应 / 敌人弱点等后续系统订阅它做联动（例如高亮）。</summary>
        public const string JumpEvent = "Codex.HoverJumpRequested";

        /// <summary>额外的悬停来源（不经 <see cref="UiTooltip"/> 的世界对象等）；返回 null 表示没有。</summary>
        public static Func<string> ExtraProvider;

        public static int JumpCount { get; private set; }
        public static string LastJumpId { get; private set; }

        /// <summary>当前悬停对象对应的图鉴条目（没有时 null）。条目 ID 必须在图鉴里存在，拼错的不当作链接。</summary>
        public static string CurrentEntryId
        {
            get
            {
                string id = UiTooltip.HoveredCodexEntryId() ?? ExtraProvider?.Invoke();
                return id != null && MechanicCodex.Find(id) != null ? id : null;
            }
        }

        /// <summary>有悬停条目就跳过去（返回 true）。</summary>
        public static bool TryJump()
        {
            string id = CurrentEntryId;
            if (id == null)
            {
                return false;
            }
            JumpCount++;
            LastJumpId = id;
            GameEvent.Send(JumpEvent, id);
            UiTooltip.Hide(); // 图鉴盖上来之后提示不再挂着；再按一次图鉴键 = 关闭图鉴
            MechanicCodex.Open(id, unlock: false);
            return true;
        }

        /// <summary>提示里链接文字用的条目名：已解锁显示名字，未解锁显示“？？？”（不剧透）。</summary>
        public static string EntryTitle(string id)
        {
            MechanicCodexEntry e = MechanicCodex.Find(id);
            if (e == null)
            {
                return string.Empty;
            }
            return MechanicCodex.IsUnlocked(id) ? MechanicCodex.Title(e) : GameText.Get("codex.panel.locked_title");
        }

        public static void ResetForTests()
        {
            ExtraProvider = null;
            JumpCount = 0;
            LastJumpId = null;
        }
    }
}
