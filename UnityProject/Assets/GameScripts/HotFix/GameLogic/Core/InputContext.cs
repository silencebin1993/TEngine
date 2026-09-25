using System;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-012）：输入上下文。同一个按键在不同上下文可以做不同的事，但同一上下文内不许冲突。
    /// 一个动作可以属于多个上下文（位标志）。当前生效的上下文由 <see cref="InputRouter.ActiveContext"/> 给出：
    /// 有模态界面 → 界面；接入视角 → 接入；战略视角 → 战略（开着建造模式时为建造）。
    /// </summary>
    [Flags]
    public enum InputContext : byte
    {
        None = 0,
        Strategy = 1,
        Build = 2,
        Uplink = 4,
        Interface = 8,
        All = Strategy | Build | Uplink | Interface,
    }

    /// <summary>修饰键（位标志）。ER2-INPUT-01 时只有 Ctrl；FG13 第 5 节的默认键用到 Ctrl / Alt 组合，扩成位标志，
    /// 数值 Ctrl=1 与旧版一致，旧设置 JSON 里的 0/1 仍能读对。</summary>
    [Flags]
    public enum InputModifier : byte
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
    }

    /// <summary>press＝按下触发一次；hold＝按住持续（平移、固定提示）。</summary>
    public enum InputActionKind : byte
    {
        Press = 0,
        Hold = 1,
    }

    /// <summary>wired＝生产代码已消费；reserved＝对应系统未落地（按下时给“后续版本开放”提示，不静默）。</summary>
    public enum InputActionStatus : byte
    {
        Wired = 0,
        Reserved = 1,
    }

    /// <summary>Unity 的 KeyCode 没有滚轮。用两个不会与真实 KeyCode 撞值的虚拟键表示滚轮上 / 下，
    /// 让“缩放”也能像其它动作一样被重绑（例如改成 +/- 键）。</summary>
    public static class VirtualKeys
    {
        public const KeyCode WheelUp = (KeyCode)10001;
        public const KeyCode WheelDown = (KeyCode)10002;

        public static bool IsWheel(KeyCode key) => key == WheelUp || key == WheelDown;

        public static bool IsMouseButton(KeyCode key) => key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;

        /// <summary>修饰键本身（Ctrl / Alt / Shift）。以修饰键为主键的绑定（如冲刺 = 左 Shift）不再比较修饰状态。</summary>
        public static bool IsModifierKey(KeyCode key) =>
            key == KeyCode.LeftControl || key == KeyCode.RightControl ||
            key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
            key == KeyCode.LeftShift || key == KeyCode.RightShift;

        /// <summary>表里的按键名 → KeyCode。认 Unity KeyCode 成员名与 WheelUp / WheelDown；认不出返回 false。</summary>
        public static bool TryParse(string name, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (name == "WheelUp")
            {
                key = WheelUp;
                return true;
            }
            if (name == "WheelDown")
            {
                key = WheelDown;
                return true;
            }
            return Enum.TryParse(name, false, out key) && Enum.IsDefined(typeof(KeyCode), key) && key != KeyCode.None;
        }
    }

    /// <summary>一个按键组合：主键 + 修饰键。<see cref="KeyCode.None"/> 表示未绑定。</summary>
    [Serializable]
    public struct InputChord : IEquatable<InputChord>
    {
        public KeyCode Key;
        public InputModifier Mods;

        public InputChord(KeyCode key, InputModifier mods = InputModifier.None)
        {
            Key = key;
            // 修饰键本身作主键时，修饰位没有意义（按下左 Shift 时 Shift 必然是按着的），统一清零，
            // 否则“左 Shift”与“Shift+左 Shift”会被当成两个不同的组合，冲突检查漏报。
            Mods = VirtualKeys.IsModifierKey(key) ? InputModifier.None : mods;
        }

        public static readonly InputChord Unbound = new InputChord(KeyCode.None);

        public bool IsBound => Key != KeyCode.None;

        public bool Equals(InputChord other) => Key == other.Key && Mods == other.Mods;

        public override bool Equals(object obj) => obj is InputChord other && Equals(other);

        public override int GetHashCode() => ((int)Key * 8) ^ (int)Mods;

        public static bool operator ==(InputChord a, InputChord b) => a.Equals(b);

        public static bool operator !=(InputChord a, InputChord b) => !a.Equals(b);

        /// <summary>表里的修饰写法（none / ctrl / alt / shift，可用 + 组合）→ 位标志。认不出返回 false。</summary>
        public static bool TryParseMods(string text, out InputModifier mods)
        {
            mods = InputModifier.None;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            foreach (string raw in text.Split('+'))
            {
                switch (raw.Trim())
                {
                    case "none": break;
                    case "ctrl": mods |= InputModifier.Ctrl; break;
                    case "alt": mods |= InputModifier.Alt; break;
                    case "shift": mods |= InputModifier.Shift; break;
                    default: return false;
                }
            }
            return true;
        }

        public override string ToString() => Mods == InputModifier.None ? Key.ToString() : Mods + "+" + Key;
    }
}
