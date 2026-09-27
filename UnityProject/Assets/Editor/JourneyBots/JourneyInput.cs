using System.Linq;
using System.Reflection;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG0-QA-01：旅程走正式输入的工具（与冒烟同一做法：替换 InputRouter 的读取后端，按帧报告按键 / 鼠标，
    /// 游戏里的输入上下文、快捷键绑定、点击穿透规则照常生效）。
    /// 光标默认放在窗口外：batchmode 下光标在左下角 = 屏幕边缘，会触发战略镜头的边缘推屏。
    /// </summary>
    public static class JourneyInput
    {
        public static readonly Vector3 OffScreen = new Vector3(-10f, -10f, 0f);

        public static Camera Cam => WorldView.Camera != null ? WorldView.Camera : Camera.main;

        /// <summary>地面点（格坐标 x, y = 世界 x, z）的屏幕位置。</summary>
        public static Vector3 ScreenOf(Vector2 ground)
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return OffScreen;
            }
            Vector3 s = cam.WorldToScreenPoint(new Vector3(ground.x, 0f, ground.y));
            return new Vector3(s.x, s.y, 0f);
        }

        /// <summary>地面点是否在画面内（离边缘至少 <paramref name="margin"/> 个视口比例）。</summary>
        public static bool OnScreen(Vector2 ground, float margin = 0.08f)
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return false;
            }
            Vector3 v = cam.WorldToViewportPoint(new Vector3(ground.x, 0f, ground.y));
            return v.z > 0f && v.x >= margin && v.x <= 1f - margin && v.y >= margin && v.y <= 1f - margin;
        }

        /// <summary>交还真键盘 / 鼠标。</summary>
        public static void Release() => InputRouter.DebugSetReader(null);

        /// <summary>光标停在地面一点（不按键）。</summary>
        public static void Hover(Vector2 ground)
        {
            Vector3 m = ScreenOf(ground);
            InputRouter.DebugSetReader(new ScriptReader { MouseA = m, MouseB = m });
        }

        /// <summary>按一次键：只在下一帧报告按下。光标默认在窗口外；建造模式里旋转要让光标留在虚影上，传 <paramref name="mouse"/>。</summary>
        public static void PressKey(KeyCode key, Vector3? mouse = null)
        {
            Vector3 m = mouse ?? OffScreen;
            InputRouter.DebugSetReader(new ScriptReader { Key = key, KeyFrame = Time.frameCount + 1, MouseA = m, MouseB = m });
        }

        /// <summary>鼠标点地面一点：光标移过去，下一帧按下、再下一帧抬起（和人点一次一样）。<paramref name="button"/> 1 = 右键。</summary>
        public static void Click(Vector2 ground, int button = 0)
        {
            Vector3 m = ScreenOf(ground);
            InputRouter.DebugSetReader(new ScriptReader
            {
                MouseA = m,
                MouseB = m,
                MouseButton = button,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
            });
        }

        /// <summary>左键从 <paramref name="a"/> 拖到 <paramref name="b"/>（框选）：下一帧在 a 按下，再下一帧光标移到 b，第三帧在 b 抬起。</summary>
        public static void Drag(Vector2 a, Vector2 b)
        {
            int f = Time.frameCount;
            InputRouter.DebugSetReader(new ScriptReader
            {
                MouseA = ScreenOf(a),
                MouseB = ScreenOf(b),
                SwitchFrame = f + 2,
                DownFrame = f + 1,
                UpFrame = f + 3,
            });
        }

        public static UnityEngine.UI.Button FindActiveButton(string name) =>
            Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(b => b.name == name && b.isActiveAndEnabled && b.interactable);

        /// <summary>点一个 UI Toolkit 按钮：走按钮自己的 Clickable（与鼠标点击同一回调），与冒烟同一做法。</summary>
        public static bool ClickUitk(string hostName, string buttonName)
        {
            GameObject host = GameObject.Find(hostName);
            var doc = host != null ? host.GetComponent<UnityEngine.UIElements.UIDocument>() : null;
            UnityEngine.UIElements.Button b = doc?.rootVisualElement?.Q<UnityEngine.UIElements.Button>(buttonName);
            if (b == null || b.clickable == null)
            {
                return false;
            }
            MethodInfo invoke = typeof(UnityEngine.UIElements.Clickable).GetMethod("Invoke",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(UnityEngine.UIElements.EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (UnityEngine.UIElements.ClickEvent evt = UnityEngine.UIElements.ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private sealed class ScriptReader : IInputReader
        {
            public KeyCode Key = KeyCode.None;
            public int KeyFrame = -1;
            public Vector3 MouseA = OffScreen;
            public Vector3 MouseB = OffScreen;
            public int SwitchFrame = -1;
            public int DownFrame = -1;
            public int UpFrame = -1;
            public int MouseButton;

            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => key == Key && Time.frameCount == KeyFrame;
            public bool GetMouseButtonDown(int button) => button == MouseButton && Time.frameCount == DownFrame;
            public bool GetMouseButtonUp(int button) => button == MouseButton && Time.frameCount == UpFrame;
            public Vector3 MousePosition => SwitchFrame >= 0 && Time.frameCount >= SwitchFrame ? MouseB : MouseA;
            public float MouseScrollDelta => 0f;
        }
    }
}
