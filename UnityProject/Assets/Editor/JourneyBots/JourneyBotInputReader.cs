using System.Collections.Generic;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// QA Tier2（旅程机器人）用的 <see cref="IInputReader"/> 注入实现。
    ///
    /// 与 `CellFrameworkValidate.ScriptedInputReader`（Tier1，按手工调用的逻辑帧回放）不同，
    /// 这里驱动的是**真实 Play 会话**：`CellStageFlow.Update` 由 TEngine `UpdateDriver` 每个
    /// 真实引擎帧自动调用，而每一步输入是 UnityMCP 单独一次 `execute_code` 调用设置的——
    /// 调用发生的那一刻与游戏下一次真正读取 <see cref="Time.frameCount"/> 之间存在不可控的
    /// 真实时间/帧数漂移，按"只在设置的那一帧为真"（旧版实现）会出现边沿被错过、
    /// 选择/命令静默不生效的问题（已实测复现）。改成**读取即消费**：按下/点击状态一直为真，
    /// 直到被 <see cref="IInputReader"/> 的消费者读取一次后自动清零，语义上更接近"这个事件
    /// 迟早会被下一次真正的 Tick 看见且只看见一次"，不依赖帧号对齐。
    ///
    /// 只在 Editor 测试/QA 工具场景下由 UnityMCP 通过 <c>execute_code</c> 创建并注入
    /// <see cref="InputRouter.DebugSetReader"/>，不进生产程序集。
    /// </summary>
    public sealed class JourneyBotInputReader : IInputReader
    {
        private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
        private readonly HashSet<KeyCode> _pendingKeyDown = new HashSet<KeyCode>();
        private readonly HashSet<int> _pendingMouseDown = new HashSet<int>();
        private readonly HashSet<int> _pendingMouseUp = new HashSet<int>();

        public Vector3 MousePosition { get; set; }
        public float MouseScrollDelta { get; set; }

        public bool GetKey(KeyCode key) => _held.Contains(key);

        public bool GetKeyDown(KeyCode key) => _pendingKeyDown.Remove(key);

        public bool GetMouseButtonDown(int button) => _pendingMouseDown.Remove(button);

        public bool GetMouseButtonUp(int button) => _pendingMouseUp.Remove(button);

        public void SetHeld(KeyCode key, bool held)
        {
            if (held) { _held.Add(key); } else { _held.Remove(key); }
        }

        /// <summary>模拟"按下"：持续按住状态同时置位，松开前 <see cref="GetKey"/> 恒真；
        /// 边沿事件等下一次被读取（哪怕隔了好几个真实帧）才消费。</summary>
        public void PressKeyDown(KeyCode key)
        {
            _held.Add(key);
            _pendingKeyDown.Add(key);
        }

        public void ReleaseKey(KeyCode key) => _held.Remove(key);

        public void ClickMouseButtonDown(int button) => _pendingMouseDown.Add(button);

        public void ClickMouseButtonUp(int button) => _pendingMouseUp.Add(button);
    }

    /// <summary>跨多次 UnityMCP <c>execute_code</c> 调用共享同一局会话状态的静态存根——
    /// 每次调用都是独立编译执行的方法体，只有类型的静态字段能在同一 AppDomain 内跨调用存活。
    /// 只在 QA 驱动黄金路径脚本期间使用，不进生产程序集。</summary>
    public static class JourneyBotSession
    {
        public static JourneyBotInputReader Reader;
    }
}
