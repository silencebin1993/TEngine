using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-012、FG13 第 12 节“重绑时发生冲突：提示冲突，并让玩家选择覆盖或取消”、FGT-UX-003）：
    /// 改键的唯一流程，主菜单（uGUI）与按键面板（UI Toolkit）共用。
    /// - 不冲突：直接生效；
    /// - 冲突：弹确认框，写清“X 已被「Y」使用（生效范围）”和“覆盖后「Y」将变为未绑定”，玩家选覆盖或取消；
    /// - 冲突方里有必须保留按键的动作（取消 / 主动作）：不给覆盖选项，直接说明原因；
    /// - 鼠标专用动作绑到键盘键：拒绝并说明。
    /// 每一步都把结果写进 <see cref="LastFeedback"/>，界面显示在页脚（不静默）。
    /// </summary>
    public static class KeyBindingFlow
    {
        public static string LastFeedback { get; private set; } = string.Empty;
        public static int FeedbackRevision { get; private set; }

        public static RebindResult Rebind(GameActionId action, InputChord chord, Action<RebindResult> onDone)
        {
            string name = ActionName(action);
            var conflicts = new List<GameActionId>();
            RebindResult result = GameSettings.TryRebind(action, chord, conflicts);
            switch (result)
            {
                case RebindResult.Ok:
                    SetFeedback(GameText.Format("input.rebind.done", name, InputDisplay.Chord(chord)));
                    onDone?.Invoke(result);
                    return result;
                case RebindResult.Invalid:
                    SetFeedback(GameText.Format("input.rebind.mouse_only", name));
                    onDone?.Invoke(result);
                    return result;
            }

            // 冲突：必须保留按键的动作不能被抢，直接说明；否则弹确认框。
            foreach (GameActionId other in conflicts)
            {
                if (InputActionCatalog.TryGet(other, out InputActionDef def) && def.Required)
                {
                    SetFeedback(GameText.Format("input.rebind.required_blocked", def.DisplayName));
                    onDone?.Invoke(RebindResult.RequiredBlocked);
                    return RebindResult.RequiredBlocked;
                }
            }

            var request = new ConfirmRequest
            {
                Title = GameText.Get("input.rebind.conflict_title"),
                ConfirmText = GameText.Get("input.rebind.override"),
                CancelText = GameText.Get("ui.common.cancel"),
            };
            foreach (GameActionId other in conflicts)
            {
                InputActionCatalog.TryGet(other, out InputActionDef def);
                request.Lines.Add(GameText.Format("input.rebind.conflict_line", InputDisplay.Chord(chord), ActionName(other),
                    def != null ? InputActionCatalog.ContextsDisplay(def.Contexts) : string.Empty));
            }
            foreach (GameActionId other in conflicts)
            {
                request.Lines.Add(GameText.Format("input.rebind.conflict_tail", ActionName(other)));
            }
            request.OnConfirm = () =>
            {
                RebindResult forced = GameSettings.ForceRebind(action, chord);
                SetFeedback(forced == RebindResult.Ok
                    ? GameText.Format("input.rebind.done", name, InputDisplay.Chord(chord))
                    : GameText.Format("input.rebind.required_blocked", name));
                onDone?.Invoke(forced);
            };
            request.OnCancel = () =>
            {
                SetFeedback(GameText.Get("input.rebind.cancelled"));
                onDone?.Invoke(RebindResult.Conflict);
            };
            SetFeedback(GameText.Get("input.rebind.conflict_title"));
            UiConfirmDialog.Show(request);
            return RebindResult.Conflict;
        }

        public static string ActionName(GameActionId action) =>
            InputActionCatalog.TryGet(action, out InputActionDef def) ? def.DisplayName : action.ToString();

        public static void SetFeedback(string text)
        {
            LastFeedback = text ?? string.Empty;
            FeedbackRevision++;
        }
    }
}
