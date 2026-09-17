using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>生产实现：零逻辑转发 <see cref="UnityEngine.Input"/>，是 <see cref="InputRouter"/> 的默认后端。</summary>
    public sealed class UnityInputReader : IInputReader
    {
        public static readonly UnityInputReader Instance = new UnityInputReader();

        private UnityInputReader()
        {
        }

        public bool GetKey(KeyCode key) => Input.GetKey(key);
        public bool GetKeyDown(KeyCode key) => Input.GetKeyDown(key);
        public bool GetMouseButtonDown(int button) => Input.GetMouseButtonDown(button);
        public bool GetMouseButtonUp(int button) => Input.GetMouseButtonUp(button);
        public Vector3 MousePosition => Input.mousePosition;
        public float MouseScrollDelta => Input.mouseScrollDelta.y;
    }
}
