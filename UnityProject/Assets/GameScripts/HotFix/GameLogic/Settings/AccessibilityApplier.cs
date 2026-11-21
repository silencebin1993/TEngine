using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.Settings
{
    /// <summary>FG15-UX-02（FGR-UX-060）：把“文字大小 / 高对比度 / 色盲模式”作用到界面与世界上。
    /// - 高对比度、色盲模式：在共享面板的根（panel.visualTree）上挂 <see cref="HighContrastClass"/> / uk-cvd-* 类，
    ///   UiKit.uss 里的覆盖规则换底色、边框和状态色；世界侧由 <see cref="ColorVision.ApplyToShaders"/> 写着色器全局。
    /// - 文字大小（与 UI 缩放独立）：给每个文字元素的字号乘倍率。界面字号大多写死在各面板的 USS 里，
    ///   没有统一变量可改，所以按元素记下“原字号”再写行内字号；原字号被面板代码改掉时重新记。
    ///   倍率为 1 且没改过任何元素时完全不扫；否则每 <see cref="SweepIntervalSeconds"/> 秒扫一遍新出现的文字（只与界面元素数有关，与实体数无关）。
    /// 每帧由 <c>GameRoot.OnUpdate</c> 调 <see cref="Tick"/>。</summary>
    public static class AccessibilityApplier
    {
        public const string HighContrastClass = "uk-high-contrast";
        public static readonly string[] CvdClasses = { null, "uk-cvd-redgreen", "uk-cvd-blueyellow", "uk-cvd-achromat" };
        private const float SweepIntervalSeconds = 0.25f;

        private sealed class TextRecord
        {
            public float Base;
            public float Applied;
        }

        private static readonly ConditionalWeakTable<TextElement, TextRecord> Records = new ConditionalWeakTable<TextElement, TextRecord>();
        private static VisualElement _panelRoot;
        private static int _appliedRevision = -1;
        private static float _nextSweepAt;
        private static float _nextFindAt;
        private static float _nextUguiSweepAt;
        private static int _uguiRevision = -1;
        private static readonly ConditionalWeakTable<TMPro.TMP_Text, TextRecord> UguiRecords = new ConditionalWeakTable<TMPro.TMP_Text, TextRecord>();
        private static bool _everScaled;
        private static ColorblindMode _shaderMode = (ColorblindMode)(-1);

        public static void Tick()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            if ((_panelRoot == null || _panelRoot.panel == null) && Time.unscaledTime >= _nextFindAt)
            {
                _nextFindAt = Time.unscaledTime + 1f; // 主菜单等没有 UI Toolkit 面板时，每秒找一次。
                _panelRoot = FindPanelRoot();
                _appliedRevision = -1;
            }
            ColorblindMode mode = GameSettings.ColorblindMode;
            if (mode != _shaderMode)
            {
                _shaderMode = mode;
                ColorVision.ApplyToShaders(mode);
            }
            TickUgui();
            if (_panelRoot == null || _panelRoot.panel == null)
            {
                return;
            }
            bool changed = _appliedRevision != GameSettings.Revision;
            if (changed)
            {
                _appliedRevision = GameSettings.Revision;
                ApplyClasses(_panelRoot, GameSettings.HighContrastEnabled, mode);
            }
            if (changed || Time.unscaledTime >= _nextSweepAt)
            {
                _nextSweepAt = Time.unscaledTime + SweepIntervalSeconds;
                ApplyTextScale(_panelRoot, GameSettings.TextScale);
            }
        }

        /// <summary>在某个根上挂 / 摘高对比度与色盲类（自检的布局探针直接调它）。</summary>
        public static void ApplyClasses(VisualElement root, bool highContrast, ColorblindMode mode)
        {
            root.EnableInClassList(HighContrastClass, highContrast);
            for (int i = 1; i < CvdClasses.Length; i++)
            {
                root.EnableInClassList(CvdClasses[i], (int)mode == i);
            }
        }

        /// <summary>代码每帧自己写字号的元素（剧情字幕、资料库正文）挂这个类：扫描跳过它，由它自己乘 <see cref="GameSettings.TextScale"/>，免得两边轮流写行内字号来回跳。</summary>
        public const string TextScaleSkipClass = "uk-text-scale-skip";

        /// <summary>把 <paramref name="root"/> 下全部文字元素的字号设为“原字号 × 倍率”。需要样式已解析（布局过一次）。
        /// 返回本次改动的元素数。</summary>
        public static int ApplyTextScale(VisualElement root, float scale)
        {
            if (Mathf.Approximately(scale, 1f) && !_everScaled)
            {
                return 0;
            }
            _everScaled = true;
            int touched = 0;
            root.Query<TextElement>().ForEach(t =>
            {
                float current = t.resolvedStyle.fontSize;
                if (float.IsNaN(current) || current <= 0f || t.ClassListContains(TextScaleSkipClass))
                {
                    return;
                }
                if (!Records.TryGetValue(t, out TextRecord r))
                {
                    r = new TextRecord { Base = current, Applied = current };
                    Records.Add(t, r);
                }
                else if (Mathf.Abs(current - r.Applied) > 0.01f)
                {
                    r.Base = current; // 面板代码改过字号：以新字号为原字号。
                }
                float want = Mathf.Round(r.Base * scale * 10f) / 10f;
                if (Mathf.Abs(want - current) > 0.01f)
                {
                    t.style.fontSize = want;
                    touched++;
                }
                r.Applied = want;
            });
            return touched;
        }

        /// <summary>旧链 uGUI（主菜单、登录 / 启动窗）：同样按原字号 × 倍率写 TMP 字号。设置变了立刻扫，否则每秒扫一遍新出现的文字。</summary>
        private static void TickUgui()
        {
            if (_uguiRevision == GameSettings.Revision && Time.unscaledTime < _nextUguiSweepAt)
            {
                return;
            }
            _uguiRevision = GameSettings.Revision;
            _nextUguiSweepAt = Time.unscaledTime + 1f;
            float scale = GameSettings.TextScale;
            if (Mathf.Approximately(scale, 1f) && !_everScaled)
            {
                return;
            }
            _everScaled = true;
            GameObject uiRoot = GameObject.Find("UIRoot");
            if (uiRoot != null)
            {
                ApplyTextScaleUgui(uiRoot.transform, scale);
            }
        }

        /// <summary>uGUI 一侧的文字缩放（自检直接调）。返回本次改动的元素数。</summary>
        public static int ApplyTextScaleUgui(Transform root, float scale)
        {
            int touched = 0;
            foreach (TMPro.TMP_Text t in root.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (!UguiRecords.TryGetValue(t, out TextRecord r))
                {
                    r = new TextRecord { Base = t.fontSize, Applied = t.fontSize };
                    UguiRecords.Add(t, r);
                }
                else if (Mathf.Abs(t.fontSize - r.Applied) > 0.01f)
                {
                    r.Base = t.fontSize;
                }
                float want = Mathf.Round(r.Base * scale * 10f) / 10f;
                if (Mathf.Abs(want - t.fontSize) > 0.01f)
                {
                    t.fontSize = want;
                    touched++;
                }
                r.Applied = want;
            }
            return touched;
        }

        /// <summary>自检用：清掉记下的原字号（换一棵新树测之前）。</summary>
        public static void ResetForTests()
        {
            _everScaled = false;
            _panelRoot = null;
            _appliedRevision = -1;
            _shaderMode = (ColorblindMode)(-1);
        }

        /// <summary>自检用：当前挂在面板根上的类。</summary>
        public static VisualElement DebugPanelRoot => _panelRoot;

        private static VisualElement FindPanelRoot()
        {
            UIDocument doc = Object.FindAnyObjectByType<UIDocument>();
            return doc != null && doc.rootVisualElement != null && doc.rootVisualElement.panel != null ? doc.rootVisualElement.panel.visualTree : null;
        }
    }
}
