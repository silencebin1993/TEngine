using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// FG0-UX-01：按键组合的玩家可见文字（快捷键提示、按键面板、悬停提示里的“快捷键：X”）。
    /// 全部走文本键：特殊键（空格、鼠标、滚轮、方向键、修饰键）有自己的键；字母、数字、F 键直接显示键名。
    /// </summary>
    public static class InputDisplay
    {
        /// <summary>某动作当前绑定的文字；未绑定时为“未绑定”。</summary>
        public static string ForAction(GameActionId action) => Chord(Settings.GameSettings.KeyBindings.GetChord(action));

        /// <summary>长文本里的按键占位前缀：<c>{act:动作名}</c>，动作名是 <see cref="GameActionId"/> 成员名。</summary>
        public const string ActionTokenPrefix = "{act:";

        /// <summary>
        /// FG1-HUD-01 修复轮（审查 P2，FG00 B02）：把只用 <see cref="GameText.Get(string)"/> 取出的长文本（图鉴正文、脚注）里的
        /// <c>{act:动作名}</c> 换成该动作当前绑定的按键文字，改键后提示跟着变。动作名拼错时原样保留（自检扫描全部文本兜底）。
        /// 只在文本变化时调用（面板按键值重建），不在每帧路径上。
        /// </summary>
        public static string ExpandActionTokens(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(ActionTokenPrefix, System.StringComparison.Ordinal) < 0)
            {
                return text;
            }
            var sb = new System.Text.StringBuilder(text.Length + 16);
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf(ActionTokenPrefix, i, System.StringComparison.Ordinal);
                if (start < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }
                int end = text.IndexOf('}', start);
                if (end < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }
                sb.Append(text, i, start - i);
                string name = text.Substring(start + ActionTokenPrefix.Length, end - start - ActionTokenPrefix.Length);
                if (TryParseAction(name, out GameActionId action))
                {
                    sb.Append(ForAction(action));
                }
                else
                {
                    sb.Append(text, start, end - start + 1);
                }
                i = end + 1;
            }
            return sb.ToString();
        }

        /// <summary>按成员名解析动作（大小写敏感、拒绝数字串）；自检也用它校验文本里的占位。</summary>
        public static bool TryParseAction(string name, out GameActionId action)
        {
            action = default;
            return !string.IsNullOrEmpty(name) && !char.IsDigit(name[0])
                && System.Enum.TryParse(name, false, out action) && System.Enum.IsDefined(typeof(GameActionId), action);
        }

        public static string Chord(InputChord chord)
        {
            if (!chord.IsBound)
            {
                return GameText.Get("input.key.none");
            }
            string text = Key(chord.Key);
            // 修饰键按 Shift、Alt、Ctrl 的顺序从内往外包，最终显示为 Ctrl+Alt+Shift+键。
            if ((chord.Mods & InputModifier.Shift) != 0)
            {
                text = GameText.Format("input.chord.join", GameText.Get("input.mod.shift"), text);
            }
            if ((chord.Mods & InputModifier.Alt) != 0)
            {
                text = GameText.Format("input.chord.join", GameText.Get("input.mod.alt"), text);
            }
            if ((chord.Mods & InputModifier.Ctrl) != 0)
            {
                text = GameText.Format("input.chord.join", GameText.Get("input.mod.ctrl"), text);
            }
            return text;
        }

        public static string Key(KeyCode key)
        {
            if (key == VirtualKeys.WheelUp) return GameText.Get("input.key.wheel_up");
            if (key == VirtualKeys.WheelDown) return GameText.Get("input.key.wheel_down");
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return GameText.Format("input.key.digit", ((int)(key - KeyCode.Alpha0)).ToString());
            }
            switch (key)
            {
                case KeyCode.None: return GameText.Get("input.key.none");
                case KeyCode.Space: return GameText.Get("input.key.space");
                case KeyCode.Escape: return GameText.Get("input.key.escape");
                case KeyCode.Return: return GameText.Get("input.key.return");
                case KeyCode.Tab: return GameText.Get("input.key.tab");
                case KeyCode.BackQuote: return GameText.Get("input.key.back_quote");
                case KeyCode.Home: return GameText.Get("input.key.home");
                case KeyCode.Mouse0: return GameText.Get("input.key.mouse0");
                case KeyCode.Mouse1: return GameText.Get("input.key.mouse1");
                case KeyCode.Mouse2: return GameText.Get("input.key.mouse2");
                case KeyCode.UpArrow: return GameText.Get("input.key.up_arrow");
                case KeyCode.DownArrow: return GameText.Get("input.key.down_arrow");
                case KeyCode.LeftArrow: return GameText.Get("input.key.left_arrow");
                case KeyCode.RightArrow: return GameText.Get("input.key.right_arrow");
                case KeyCode.LeftShift: return GameText.Get("input.key.left_shift");
                case KeyCode.RightShift: return GameText.Get("input.key.right_shift");
                case KeyCode.LeftAlt: return GameText.Get("input.key.left_alt");
                case KeyCode.RightAlt: return GameText.Get("input.key.right_alt");
                case KeyCode.LeftControl: return GameText.Get("input.key.left_control");
                case KeyCode.RightControl: return GameText.Get("input.key.right_control");
                default: return key.ToString();
            }
        }
    }

    /// <summary>
    /// FG0-UX-01：改键时“等玩家按下一个键”的采集器。读 <see cref="InputRouter.Reader"/>（测试可注入按帧回放）。
    /// 规则：按下非修饰键 → 取当时按住的修饰键组成组合；单独按下再松开一个修饰键 → 绑修饰键本身（如冲刺 = 左 Shift）；
    /// Esc → 取消采集（所以 Esc 只能留给“取消 / 返回”，这是刻意的：保证玩家永远有一个退出键）；滚轮 → 滚轮上 / 下。
    /// </summary>
    public sealed class InputCapture
    {
        private static readonly KeyCode[] Candidates = BuildCandidates();
        private KeyCode _pendingModifier = KeyCode.None;

        public enum Result : byte
        {
            Waiting = 0,
            Captured = 1,
            Cancelled = 2,
        }

        /// <summary>每帧调用一次。</summary>
        public Result Poll(out InputChord chord)
        {
            chord = InputChord.Unbound;
            IInputReader reader = InputRouter.Reader;
            if (reader.GetKeyDown(KeyCode.Escape))
            {
                _pendingModifier = KeyCode.None;
                return Result.Cancelled;
            }
            float scroll = reader.MouseScrollDelta;
            if (scroll > 0.001f || scroll < -0.001f)
            {
                chord = new InputChord(scroll > 0f ? VirtualKeys.WheelUp : VirtualKeys.WheelDown);
                _pendingModifier = KeyCode.None;
                return Result.Captured;
            }
            for (int i = 0; i <= 4; i++)
            {
                if (reader.GetMouseButtonDown(i))
                {
                    chord = new InputChord(KeyCode.Mouse0 + i, InputRouter.HeldModifiers());
                    _pendingModifier = KeyCode.None;
                    return Result.Captured;
                }
            }
            for (int i = 0; i < Candidates.Length; i++)
            {
                KeyCode key = Candidates[i];
                if (!reader.GetKeyDown(key))
                {
                    continue;
                }
                if (VirtualKeys.IsModifierKey(key))
                {
                    _pendingModifier = key;
                    continue;
                }
                chord = new InputChord(key, InputRouter.HeldModifiers());
                _pendingModifier = KeyCode.None;
                return Result.Captured;
            }
            // 单独按下的修饰键已经松开、期间没有按别的键：绑修饰键本身。
            if (_pendingModifier != KeyCode.None && !reader.GetKey(_pendingModifier))
            {
                chord = new InputChord(_pendingModifier);
                _pendingModifier = KeyCode.None;
                return Result.Captured;
            }
            return Result.Waiting;
        }

        public void Reset() => _pendingModifier = KeyCode.None;

        private static KeyCode[] BuildCandidates()
        {
            var list = new System.Collections.Generic.List<KeyCode>();
            for (KeyCode k = KeyCode.A; k <= KeyCode.Z; k++) list.Add(k);
            for (KeyCode k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) list.Add(k);
            for (KeyCode k = KeyCode.F1; k <= KeyCode.F12; k++) list.Add(k);
            for (KeyCode k = KeyCode.Keypad0; k <= KeyCode.Keypad9; k++) list.Add(k);
            list.AddRange(new[]
            {
                KeyCode.Space, KeyCode.Tab, KeyCode.Return, KeyCode.Backspace, KeyCode.BackQuote,
                KeyCode.Minus, KeyCode.Equals, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Backslash,
                KeyCode.Semicolon, KeyCode.Quote, KeyCode.Comma, KeyCode.Period, KeyCode.Slash,
                KeyCode.Home, KeyCode.End, KeyCode.Insert, KeyCode.Delete, KeyCode.PageUp, KeyCode.PageDown,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.CapsLock,
                KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt,
            });
            return list.ToArray();
        }
    }
}
