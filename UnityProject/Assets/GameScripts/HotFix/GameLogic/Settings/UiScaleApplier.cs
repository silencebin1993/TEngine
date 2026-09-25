using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace GameLogic.Settings
{
    /// <summary>ER8-CONTENT-01 / AC-UI-004：把设置里的“UI 缩放”（0.8～1.4）真正作用到界面上。
    /// 此前主菜单滑条能改能存，但全工程没有任何代码读取它。
    ///
    /// 两条链各改一处、全局生效，不碰任何窗口自己的 Canvas/PanelSettings：
    /// - UI Toolkit：全部玩法面板共用 <c>BattleHudPanelSettings</c> 同一个实例，写它的
    ///   <see cref="PanelSettings.scale"/>（在“随屏幕尺寸缩放”之上再乘一层）。
    /// - uGUI：只有共享根 Canvas（UIRoot）的 CanvasScaler 真正生效（嵌套 Canvas 上的不参与计算，
    ///   见 GameApp.FixUiRootReferenceResolution），把参考分辨率除以缩放值：参考分辨率越小，控件越大。
    ///
    /// 每帧由 <c>GameRoot.OnUpdate</c> 调用，只比较 <see cref="GameSettings.Revision"/> 一个整数；
    /// 玩家按住鼠标拖滑条期间暂缓应用，松手再生效——否则主菜单会在鼠标底下实时缩放，滑条跟着跳。</summary>
    public static class UiScaleApplier
    {
        public const string PanelSettingsLocation = "BattleHudPanelSettings";
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        private const float RootSearchIntervalSeconds = 1f;

        private static PanelSettings _panelSettings;
        private static bool _loading;
        private static CanvasScaler _rootScaler;
        private static float _nextRootSearchAt;
        private static int _appliedRevision = -1;
        private static bool _quitHooked;

        /// <summary>uGUI 根 CanvasScaler 在给定缩放下应使用的参考分辨率。</summary>
        public static Vector2 CanvasReferenceFor(float uiScale)
        {
            float s = Mathf.Clamp(uiScale, 0.5f, 2f);
            return new Vector2(ReferenceWidth / s, ReferenceHeight / s);
        }

        /// <summary>自检用：当前已作用到 UI Toolkit 共享 PanelSettings 上的缩放（未加载时为 0）。</summary>
        public static float AppliedPanelScale => _panelSettings != null ? _panelSettings.scale : 0f;

        /// <summary>自检用：当前已作用到 uGUI 根 CanvasScaler 上的参考分辨率（未找到时为零向量）。</summary>
        public static Vector2 AppliedCanvasReference => _rootScaler != null ? _rootScaler.referenceResolution : Vector2.zero;

        public static void Tick()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (_panelSettings == null && !_loading)
            {
                LoadPanelSettings().Forget();
            }
            if (_rootScaler == null && Time.unscaledTime >= _nextRootSearchAt)
            {
                _nextRootSearchAt = Time.unscaledTime + RootSearchIntervalSeconds;
                GameObject uiRoot = GameObject.Find("UIRoot");
                _rootScaler = uiRoot != null ? uiRoot.GetComponentInChildren<CanvasScaler>() : null;
                if (_rootScaler != null)
                {
                    _appliedRevision = -1; // 新找到的根 Canvas 还没按当前设置缩放过。
                }
            }

            if (_appliedRevision == GameSettings.Revision)
            {
                return;
            }
            if (Input.GetMouseButton(0))
            {
                return; // 拖滑条中：松手后下一帧再应用。
            }
            Apply();
        }

        private static void Apply()
        {
            float scale = GameSettings.UiScale;
            bool complete = true;

            if (_panelSettings != null)
            {
                _panelSettings.scale = scale;
            }
            else
            {
                complete = false;
            }

            if (_rootScaler != null)
            {
                _rootScaler.referenceResolution = CanvasReferenceFor(scale);
            }
            else
            {
                complete = false;
            }

            // 两条链都就位才记下版本号；有一条还没加载好，下一帧继续补。
            if (complete)
            {
                _appliedRevision = GameSettings.Revision;
            }
        }

        private static async UniTaskVoid LoadPanelSettings()
        {
            _loading = true;
            try
            {
                _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>(PanelSettingsLocation);
            }
            finally
            {
                _loading = false;
            }
            if (_panelSettings == null)
            {
                Log.Warning($"[UiScaleApplier] 共享 PanelSettings（{PanelSettingsLocation}）加载失败，UI Toolkit 缩放暂不可用。");
                return;
            }
            _appliedRevision = -1;

            if (!_quitHooked)
            {
                _quitHooked = true;
                // 编辑器里 PanelSettings 是工程资产本体：退出 Play 时还原成 1，别把玩家的缩放留在资产上。
                Application.quitting += RestoreAssetScale;
            }
        }

        private static void RestoreAssetScale()
        {
            if (_panelSettings != null)
            {
                _panelSettings.scale = 1f;
            }
            _appliedRevision = -1;
        }
    }
}
