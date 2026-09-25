using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>一条键位序列化条目（PlayerPrefs JSON 用，见 <see cref="Settings.GameSettings"/>）。
    /// FG0-UX-01 起只存玩家改过的动作；<see cref="Key"/> = None 表示玩家明确解除了绑定（覆盖冲突后）。</summary>
    [Serializable]
    public struct InputBindingEntry
    {
        public GameActionId Action;
        public KeyCode Key;
        /// <summary>FG0-UX-01 新增。旧 JSON 没有这个字段时补 0（无修饰），与旧版语义一致。</summary>
        public InputModifier Mods;
    }

    /// <summary>重绑结果。</summary>
    public enum RebindResult : byte
    {
        Ok = 0,
        /// <summary>与同一上下文里的其它动作撞键：没有落地，调用方弹确认框让玩家选“覆盖”或“取消”。</summary>
        Conflict = 1,
        /// <summary>覆盖会让必须保留按键的动作（取消 / 主动作）变成未绑定：拒绝。</summary>
        RequiredBlocked = 2,
        /// <summary>未知动作、空键、或鼠标专用动作绑到了键盘键。</summary>
        Invalid = 3,
    }

    /// <summary>
    /// ER2-INPUT-01 / FG0-UX-01（FGR-ARC-012、FGT-UX-003）：物理键 ↔ 逻辑动作的唯一映射表。
    ///
    /// - **全部动作都可重绑**（FG0-UX-01 取消了 ER2 时“只开放 7 个”的范围裁剪）；默认键来自
    ///   <see cref="InputActionCatalog"/>（fg.TbInputAction），本类只存玩家改过的键（覆盖表）。
    /// - **冲突按上下文判定**：两个动作只有在上下文有交集、主键相同、并且 Ctrl/Alt 修饰相同时才算冲突；
    ///   鼠标键、滚轮和“按住”类动作忽略修饰键（按住 W 平移时按 Ctrl+W 也会平移，所以两者算冲突）。
    ///   Shift 不参与比较：Shift 在本游戏里是“追加选择 / 排队”的限定键，按 Shift+Z 同样会触发 Z。
    /// - 覆盖冲突时，被抢走按键的动作变成“未绑定”（明确显示，不静默回到一个也可能冲突的默认键）；
    ///   必须保留按键的动作（<see cref="InputActionDef.Required"/>）不能被抢。
    /// </summary>
    public sealed class InputBindingSet
    {
        /// <summary>当前的序列化格式。0/1 = ER2 时代的“全表快照”格式；2 = FG0-UX-01 的“只存改过的键”。</summary>
        public const int CurrentFormatVersion = 2;

        private readonly Dictionary<GameActionId, InputChord> _overrides = new Dictionary<GameActionId, InputChord>();

        /// <summary>每次改动 +1，界面据此刷新快捷键提示（O(1) 比较）。</summary>
        public int Revision { get; private set; }

        /// <summary>已登记的全部动作都可重绑（FGR-ARC-012）。保留这个名字给既有调用方。</summary>
        public static IEnumerable<GameActionId> RebindableActions
        {
            get
            {
                foreach (InputActionDef def in InputActionCatalog.All)
                {
                    yield return def.Action;
                }
            }
        }

        public static InputBindingSet CreateDefault() => new InputBindingSet();

        public static bool IsRebindable(GameActionId action) => InputActionCatalog.TryGet(action, out _);

        /// <summary>表里的默认键（不看玩家改动）。</summary>
        public static InputChord GetDefaultChord(GameActionId action) => InputActionCatalog.DefaultChord(action);

        /// <summary>兼容旧调用：默认键的主键。</summary>
        public static KeyCode GetHardcodedDefault(GameActionId action) => GetDefaultChord(action).Key;

        /// <summary>当前生效的组合（玩家改过的优先，否则默认）。</summary>
        public InputChord GetChord(GameActionId action) =>
            _overrides.TryGetValue(action, out InputChord chord) ? chord : InputActionCatalog.DefaultChord(action);

        /// <summary>兼容旧调用：当前生效组合的主键。</summary>
        public KeyCode GetKey(GameActionId action) => GetChord(action).Key;

        public bool IsCustomized(GameActionId action) => _overrides.ContainsKey(action);

        public int CustomizedCount => _overrides.Count;

        public void ResetAllToDefault()
        {
            _overrides.Clear();
            Revision++;
        }

        /// <summary>单个动作恢复默认。默认键若与别的（改过的）动作撞键，返回 Conflict 且不落地。</summary>
        public RebindResult ResetToDefault(GameActionId action, List<GameActionId> conflicts = null)
        {
            if (!InputActionCatalog.TryGet(action, out _))
            {
                return RebindResult.Invalid;
            }
            InputChord chord = InputActionCatalog.DefaultChord(action);
            List<GameActionId> found = conflicts ?? new List<GameActionId>();
            found.Clear();
            CollectConflicts(action, chord, found);
            if (found.Count > 0)
            {
                return RebindResult.Conflict;
            }
            if (_overrides.Remove(action))
            {
                Revision++;
            }
            return RebindResult.Ok;
        }

        /// <summary>两个动作在给定组合下是否冲突（上下文有交集 + 组合按上面的规则相同）。</summary>
        public static bool Conflicts(InputActionDef a, InputChord chordA, InputActionDef b, InputChord chordB)
        {
            if (a == null || b == null || a.Action == b.Action || (a.Contexts & b.Contexts) == 0)
            {
                return false;
            }
            if (!chordA.IsBound || !chordB.IsBound || chordA.Key != chordB.Key)
            {
                return false;
            }
            bool ignoreMods = a.Kind == InputActionKind.Hold || b.Kind == InputActionKind.Hold
                              || VirtualKeys.IsMouseButton(chordA.Key) || VirtualKeys.IsWheel(chordA.Key);
            if (ignoreMods)
            {
                return true;
            }
            const InputModifier significant = InputModifier.Ctrl | InputModifier.Alt;
            return (chordA.Mods & significant) == (chordB.Mods & significant);
        }

        /// <summary>把 <paramref name="action"/> 设成 <paramref name="chord"/> 时会冲突的动作（不含自己）。</summary>
        public void CollectConflicts(GameActionId action, InputChord chord, List<GameActionId> into)
        {
            if (!InputActionCatalog.TryGet(action, out InputActionDef def))
            {
                return;
            }
            foreach (InputActionDef other in InputActionCatalog.All)
            {
                if (other.Action != action && Conflicts(def, chord, other, GetChord(other.Action)))
                {
                    into.Add(other.Action);
                }
            }
        }

        /// <summary>当前整张表里所有互相冲突的动作对（自检、读档迁移用）。O(动作数²)，只在改键 / 读设置时调用。</summary>
        public List<(GameActionId A, GameActionId B)> FindAllConflicts()
        {
            var result = new List<(GameActionId, GameActionId)>();
            IReadOnlyList<InputActionDef> all = InputActionCatalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (Conflicts(all[i], GetChord(all[i].Action), all[j], GetChord(all[j].Action)))
                    {
                        result.Add((all[i].Action, all[j].Action));
                    }
                }
            }
            return result;
        }

        /// <summary>尝试重绑。冲突时不落地，返回 Conflict 并把冲突方写进 <paramref name="conflicts"/>，
        /// 调用方（按键面板）弹确认框，玩家选“覆盖”后调用 <see cref="ForceRebind"/>。</summary>
        public RebindResult TryRebind(GameActionId action, InputChord chord, List<GameActionId> conflicts)
        {
            RebindResult valid = Validate(action, chord);
            if (valid != RebindResult.Ok)
            {
                return valid;
            }
            conflicts?.Clear();
            List<GameActionId> found = conflicts ?? new List<GameActionId>();
            CollectConflicts(action, chord, found);
            if (found.Count > 0)
            {
                return RebindResult.Conflict;
            }
            Set(action, chord);
            return RebindResult.Ok;
        }

        /// <summary>确认覆盖：冲突方变为未绑定，再落地新组合。冲突方里有必须保留按键的动作时整笔拒绝，什么都不改。</summary>
        public RebindResult ForceRebind(GameActionId action, InputChord chord, List<GameActionId> blockedBy = null)
        {
            RebindResult valid = Validate(action, chord);
            if (valid != RebindResult.Ok)
            {
                return valid;
            }
            var conflicts = new List<GameActionId>();
            CollectConflicts(action, chord, conflicts);
            blockedBy?.Clear();
            bool blocked = false;
            foreach (GameActionId other in conflicts)
            {
                if (InputActionCatalog.TryGet(other, out InputActionDef def) && def.Required)
                {
                    blocked = true;
                    blockedBy?.Add(other);
                }
            }
            if (blocked)
            {
                return RebindResult.RequiredBlocked;
            }
            foreach (GameActionId other in conflicts)
            {
                SetRaw(other, InputChord.Unbound);
            }
            Set(action, chord);
            return RebindResult.Ok;
        }

        private static RebindResult Validate(GameActionId action, InputChord chord)
        {
            if (!InputActionCatalog.TryGet(action, out InputActionDef def) || !chord.IsBound)
            {
                return RebindResult.Invalid;
            }
            if (def.MouseOnly && !VirtualKeys.IsMouseButton(chord.Key))
            {
                return RebindResult.Invalid;
            }
            return RebindResult.Ok;
        }

        private void Set(GameActionId action, InputChord chord)
        {
            if (chord == InputActionCatalog.DefaultChord(action))
            {
                _overrides.Remove(action);
            }
            else
            {
                _overrides[action] = chord;
            }
            Revision++;
        }

        private void SetRaw(GameActionId action, InputChord chord)
        {
            _overrides[action] = chord;
            Revision++;
        }

        public InputBindingEntry[] ToEntries()
        {
            var entries = new InputBindingEntry[_overrides.Count];
            int i = 0;
            foreach (KeyValuePair<GameActionId, InputChord> kv in _overrides)
            {
                entries[i++] = new InputBindingEntry { Action = kv.Key, Key = kv.Value.Key, Mods = kv.Value.Mods };
            }
            Array.Sort(entries, (a, b) => ((int)a.Action).CompareTo((int)b.Action));
            return entries;
        }

        /// <summary>读设置。<paramref name="formatVersion"/> &lt; 2 时按 ER2 旧格式迁移（见 <see cref="MigrateLegacy"/>）。</summary>
        public static InputBindingSet FromEntries(InputBindingEntry[] entries, int formatVersion, out LegacyMigrationReport report)
        {
            report = default;
            var set = new InputBindingSet();
            if (entries == null || entries.Length == 0)
            {
                return set;
            }
            if (formatVersion < CurrentFormatVersion)
            {
                report = MigrateLegacy(set, entries);
                return set;
            }
            foreach (InputBindingEntry entry in entries)
            {
                if (!InputActionCatalog.TryGet(entry.Action, out InputActionDef def))
                {
                    continue;
                }
                // 手改 / 损坏的设置不能把人锁死（B11）：主动作 / 功能动作只能是鼠标键；必须保留按键的动作（取消、主动作）
                // 不许是未绑定。不合法的条目恢复默认并计数，和冲突一样在读档后告诉玩家。
                var chord = new InputChord(entry.Key, entry.Mods);
                bool valid = chord.IsBound ? Validate(entry.Action, chord) == RebindResult.Ok : !def.Required;
                if (!valid)
                {
                    report.DroppedCount++;
                    continue;
                }
                set._overrides[entry.Action] = chord;
            }
            // 设置文件被手改或表更新后可能出现冲突：冲突的改动恢复默认，不让两件事抢同一个键。
            foreach ((GameActionId a, GameActionId b) in set.FindAllConflicts())
            {
                if (set._overrides.Remove(b) || set._overrides.Remove(a))
                {
                    report.DroppedCount++;
                }
            }
            return set;
        }

        /// <summary>兼容旧调用（不关心迁移报告）。</summary>
        public static InputBindingSet FromEntries(InputBindingEntry[] entries) => FromEntries(entries, CurrentFormatVersion, out _);

        /// <summary>读档迁移结果：保留了几个玩家改过的键、几个因与新默认键冲突而恢复默认。</summary>
        public struct LegacyMigrationReport
        {
            public bool Migrated;
            public int KeptCount;
            public int DroppedCount;
        }

        /// <summary>
        /// ER2 旧格式（全表快照，含未改动的动作）迁移：只把“和 ER2 当时默认键不同”的条目当作玩家改动保留，
        /// 其余交给新默认键（FG13 合并后的默认键，例如任务日志 J→L、编组 1→Alt+1）。
        /// 保留下来的改动若与新默认键冲突，恢复默认并计数，读档后以通知告知玩家。
        /// ER2 默认键表只在这里用于识别“哪些是玩家改过的”，不参与任何运行时解析。
        /// </summary>
        private static LegacyMigrationReport MigrateLegacy(InputBindingSet set, InputBindingEntry[] entries)
        {
            var report = new LegacyMigrationReport { Migrated = true };
            foreach (InputBindingEntry entry in entries)
            {
                if (!InputActionCatalog.TryGet(entry.Action, out _) || entry.Key == KeyCode.None)
                {
                    continue;
                }
                if (Er2Default(entry.Action) == entry.Key)
                {
                    continue; // 玩家没改过这个键：让新默认键生效。
                }
                var chord = new InputChord(entry.Key, InputModifier.None);
                if (chord == InputActionCatalog.DefaultChord(entry.Action))
                {
                    continue;
                }
                var conflicts = new List<GameActionId>();
                set.CollectConflicts(entry.Action, chord, conflicts);
                if (conflicts.Count > 0)
                {
                    report.DroppedCount++;
                    continue;
                }
                set._overrides[entry.Action] = chord;
                report.KeptCount++;
            }
            return report;
        }

        /// <summary>ER2-INPUT-01 时代的默认键（只用于旧设置迁移）。</summary>
        private static KeyCode Er2Default(GameActionId action)
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
                case GameActionId.CommandMove: return KeyCode.V;
                case GameActionId.CommandAttack: return KeyCode.C;
                case GameActionId.CommandGuard: return KeyCode.G;
                case GameActionId.CommandRetreat: return KeyCode.H;
                case GameActionId.ToggleMissionLog: return KeyCode.J;
                default: return KeyCode.None;
            }
        }
    }
}
