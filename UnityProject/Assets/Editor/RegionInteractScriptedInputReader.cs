using System.Collections.Generic;
using GameLogic.Core;
using UnityEngine;

namespace BinGamesEditor
{
    /// <summary>ER5-INT-01 验收专用：可编程 <see cref="IInputReader"/> 测试替身，供 execute_code/
    /// Play Mode 验收注入 <see cref="InputRouter.DebugSetReader"/>，模拟"按住 E"/"点按 E" 这类无法
    /// 在无人值守环境里用真实键鼠触发的输入序列。与 `CellFrameworkValidate.ScriptedInputReader`
    /// 同一形状（那份是 private 内嵌类，验收工具够不到），本类只是它的公开可外部实例化版本，
    /// 不随热更代码一起发布（Editor 专用目录）。</summary>
    public sealed class RegionInteractScriptedInputReader : IInputReader
    {
        private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
        private readonly HashSet<KeyCode> _downThisFrame = new HashSet<KeyCode>();

        public Vector3 MousePosition { get; set; }
        public float MouseScrollDelta { get; set; }

        public bool GetKey(KeyCode key) => _held.Contains(key);
        public bool GetKeyDown(KeyCode key) => _downThisFrame.Contains(key);
        public bool GetMouseButtonDown(int button) => false;
        public bool GetMouseButtonUp(int button) => false;

        public void SetHeld(KeyCode key, bool held)
        {
            if (held) { _held.Add(key); } else { _held.Remove(key); }
        }

        /// <summary>模拟"这一帧按下"：本帧 GetKeyDown 为 true 并转入持续按住状态——单击式交互
        /// （<see cref="InputRouter.ConsumeAction"/>）走这条边沿，按住式交互（<see cref="InputRouter.GetActionKey"/>）
        /// 走 <see cref="SetHeld"/> 持续态，两者互不干扰。</summary>
        public void PressKeyDown(KeyCode key)
        {
            _held.Add(key);
            _downThisFrame.Add(key);
        }

        /// <summary>推进到下一帧：清空本帧边沿事件（GetKeyDown 只在按下的那一帧为真），持续按住状态保留。</summary>
        public void EndFrame()
        {
            _downThisFrame.Clear();
        }
    }
}
