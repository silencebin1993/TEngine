using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>Ctrl 修饰键。ERD 域表里 1～9 与 Ctrl+1～9 是两件不同的事（选编组 vs 建编组），
    /// 用同一个物理键位 + 修饰符区分，不当成两个独立可重绑动作——重绑数字本身即可，
    /// Ctrl 修饰符是固定语义（M2-02 既有约定）。</summary>
    public enum InputModifier : byte
    {
        None = 0,
        Ctrl = 1,
    }

    /// <summary>一条键位序列化条目（PlayerPrefs JSON 用，见 <see cref="Settings.GameSettings"/>）。</summary>
    [Serializable]
    public struct InputBindingEntry
    {
        public GameActionId Action;
        public KeyCode Key;
    }

    /// <summary>
    /// ER2-INPUT-01：物理键↔逻辑动作的唯一映射表（AC-ACC-001 键位重绑的数据层）。
    ///
    /// 只有 <see cref="RebindableActions"/> 里列出的动作允许玩家在设置面板改键；WASD/方向键与
    /// 战略左右平移固定不开放重绑（见类上方设计说明），但仍然经由本类解析，保证"物理键→逻辑
    /// 动作"始终只有一张表，不会有第二处硬编码字面量。
    /// </summary>
    public sealed class InputBindingSet
    {
        /// <summary>允许玩家在设置面板重绑的动作子集。WASD/方向键/战略左右平移是移动的基础手感，
        /// 不纳入重绑 UI（范围裁剪，见 ER2-INPUT-01 证据文件"实现范围"一节）。</summary>
        public static readonly GameActionId[] RebindableActions =
        {
            GameActionId.Interact,
            GameActionId.CycleControlTarget,
            GameActionId.ToggleCameraView,
            GameActionId.TogglePause,
            GameActionId.Cancel,
            GameActionId.DirectSkillSlot0,
            GameActionId.ToggleMissionLog,
        };

        private readonly Dictionary<GameActionId, KeyCode> _bindings = new Dictionary<GameActionId, KeyCode>();

        public static InputBindingSet CreateDefault()
        {
            var set = new InputBindingSet();
            set.ResetAllToDefault();
            return set;
        }

        /// <summary>默认键位。冲刺默认键从历史上的 Space 改为 LeftShift：冲刺是 Direct 域、暂停
        /// （<see cref="GameActionId.TogglePause"/>）是 Strategy 域，运行时靠 <see cref="InputScope"/>
        /// 互斥并不真的冲突；但 <see cref="TryRebind"/> 的冲突检测按物理键全局唯一判断，不区分域
        /// （简单模型，见该方法注释），两个默认键撞在一起会让"恢复默认"之后的冲突提示自相矛盾。
        /// 让默认键互不相同，重绑冲突提示才总是有意义。</summary>
        public static KeyCode GetHardcodedDefault(GameActionId action)
        {
            switch (action)
            {
                case GameActionId.MoveForward: return KeyCode.W;
                case GameActionId.MoveBack: return KeyCode.S;
                case GameActionId.MoveLeft: return KeyCode.A;
                case GameActionId.MoveRight: return KeyCode.D;
                case GameActionId.DirectSkillSlot0: return KeyCode.LeftShift;
                case GameActionId.DirectSkillSlot1: return KeyCode.Q;
                case GameActionId.DirectSkillSlot2: return KeyCode.E;
                case GameActionId.DirectSkillSlot3: return KeyCode.R;
                case GameActionId.DirectSkillSlot4: return KeyCode.F;
                case GameActionId.Interact: return KeyCode.E;
                case GameActionId.CycleControlTarget: return KeyCode.Tab;
                case GameActionId.ToggleCameraView: return KeyCode.M;
                case GameActionId.TogglePause: return KeyCode.Space;
                case GameActionId.Cancel: return KeyCode.Escape;
                case GameActionId.Group1: return KeyCode.Alpha1;
                case GameActionId.Group2: return KeyCode.Alpha2;
                case GameActionId.Group3: return KeyCode.Alpha3;
                case GameActionId.Group4: return KeyCode.Alpha4;
                case GameActionId.Group5: return KeyCode.Alpha5;
                case GameActionId.Group6: return KeyCode.Alpha6;
                case GameActionId.Group7: return KeyCode.Alpha7;
                case GameActionId.Group8: return KeyCode.Alpha8;
                case GameActionId.Group9: return KeyCode.Alpha9;
                case GameActionId.StrategyPanLeft: return KeyCode.LeftArrow;
                case GameActionId.StrategyPanRight: return KeyCode.RightArrow;
                // ER5-CMD-01：G/H 沿用仓库里旧 SquadCommandSystem（细胞阶段）的 Guard/Retreat 肌肉记忆；
                // V/C 是 Move/Attack 的武装键（该系统里 Move 靠右键智能命令，未占用独立键位）。
                case GameActionId.CommandMove: return KeyCode.V;
                case GameActionId.CommandAttack: return KeyCode.C;
                case GameActionId.CommandGuard: return KeyCode.G;
                case GameActionId.CommandRetreat: return KeyCode.H;
                case GameActionId.ToggleMissionLog: return KeyCode.J;
                default: return KeyCode.None;
            }
        }

        public void ResetAllToDefault()
        {
            _bindings.Clear();
            foreach (GameActionId action in (GameActionId[])Enum.GetValues(typeof(GameActionId)))
            {
                _bindings[action] = GetHardcodedDefault(action);
            }
        }

        public void ResetToDefault(GameActionId action)
        {
            _bindings[action] = GetHardcodedDefault(action);
        }

        public KeyCode GetKey(GameActionId action)
        {
            return _bindings.TryGetValue(action, out KeyCode key) ? key : GetHardcodedDefault(action);
        }

        public static bool IsRebindable(GameActionId action)
        {
            return Array.IndexOf(RebindableActions, action) >= 0;
        }

        /// <summary>尝试重绑。只在 <see cref="RebindableActions"/> 内的动作允许改；与另一个
        /// 已绑定的可重绑动作撞键时返回 false 并给出冲突方，调用方（设置 UI）负责弹确认。
        /// <see cref="GameActionId.None"/>/KeyCode.None 均视为非法输入直接拒绝。</summary>
        public bool TryRebind(GameActionId action, KeyCode key, out GameActionId conflict)
        {
            conflict = default;
            if (!IsRebindable(action) || key == KeyCode.None)
            {
                return false;
            }

            foreach (KeyValuePair<GameActionId, KeyCode> kv in _bindings)
            {
                if (kv.Key != action && kv.Value == key)
                {
                    conflict = kv.Key;
                    return false;
                }
            }

            _bindings[action] = key;
            return true;
        }

        /// <summary>确认覆盖冲突后调用：把冲突方复位为默认键，再落地新绑定。
        /// 调用方必须先用 <see cref="TryRebind"/> 拿到真实冲突动作（该次调用返回 false 时才有意义）。</summary>
        public void ForceRebind(GameActionId action, KeyCode key, GameActionId conflict)
        {
            _bindings[conflict] = GetHardcodedDefault(conflict);
            _bindings[action] = key;
        }

        public InputBindingEntry[] ToEntries()
        {
            var entries = new InputBindingEntry[_bindings.Count];
            int i = 0;
            foreach (KeyValuePair<GameActionId, KeyCode> kv in _bindings)
            {
                entries[i++] = new InputBindingEntry { Action = kv.Key, Key = kv.Value };
            }
            return entries;
        }

        public static InputBindingSet FromEntries(InputBindingEntry[] entries)
        {
            var set = CreateDefault();
            if (entries == null)
            {
                return set;
            }
            foreach (InputBindingEntry entry in entries)
            {
                set._bindings[entry.Action] = entry.Key;
            }
            return set;
        }
    }
}
