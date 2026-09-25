using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace BinGames.EditorTools
{
    /// <summary>
    /// UI Toolkit 面板免 Play 布局探针：把一份 UXML 挂到临时 UIDocument 上，强制跑一次样式+布局，
    /// 报告"越界 / 文字被截断 / 塌成 0 宽 / 面板超出屏幕"四类问题，可在多个分辨率下重复。
    ///
    /// 用法（UnityMCP execute_code 一行调用）：
    /// <code>return BinGames.EditorTools.UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/CircuitBoard/CircuitBoardPanel.uxml", "CircuitBoardPanelRoot");</code>
    /// 或在 Project 窗口选中 UXML 后点菜单 Tools/BinGames/UI Toolkit 布局探针。
    ///
    /// 只测布局，不测渲染：编辑模式下运行时面板不出帧，要看真实画面必须进 Play 用
    /// <c>ScreenCapture.CaptureScreenshot</c>（UnityMCP 的 manage_camera screenshot 走相机渲染，不含 UI Toolkit 覆盖层）。
    /// </summary>
    public static class UiToolkitLayoutProbe
    {
        public const string DefaultPanelSettingsPath = "Assets/GameRes/Raw/UI/BattleUI/BattleHudPanelSettings.asset";

        /// <summary>压力文本：中英混排、带空格和全角括号、足够长，专门撑爆没做收缩/截断的控件。</summary>
        public const string StressText = "精校聚焦镜（补印 ＃a3f9 · 已预留）Overlong label 超长超长超长超长";

        private static readonly Vector2Int[] DefaultResolutions =
        {
            new Vector2Int(1920, 1080),
            new Vector2Int(1280, 720),
            new Vector2Int(2560, 1080),
            new Vector2Int(1280, 1024),
        };

        /// <summary>
        /// <paramref name="panelRootName"/> 为空时检查整棵树；<paramref name="stressFill"/> 为 true 时
        /// 往所有 DropdownField / TextField 塞 <see cref="StressText"/>。面板根上名字含 "hidden" 的类会被移除，
        /// 以便默认隐藏的面板也能被测到。
        /// <paramref name="prepare"/> 可选：在默认的显隐/压测处理之后、强制布局之前调用，供调用方把
        /// 运行时才会出现的内容（固定槽位的隐藏行、只由代码填写的 Label 文本）摆出来一起测。
        /// </summary>
        public static string Probe(string uxmlPath, string panelRootName = null, bool stressFill = true,
            string panelSettingsPath = DefaultPanelSettingsPath, System.Action<VisualElement> prepare = null)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var sourceSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
            if (vta == null || sourceSettings == null)
            {
                return $"加载失败：uxml={(vta != null)} panelSettings={(sourceSettings != null)}";
            }

            var sb = new StringBuilder();
            int totalProblems = LintFolder(uxmlPath, sb);
            foreach (Vector2Int res in DefaultResolutions)
            {
                totalProblems += ProbeAt(vta, sourceSettings, panelRootName, stressFill, res, sb, prepare);
            }
            sb.Insert(0, totalProblems == 0 ? "PASS 无布局问题\n" : $"FAIL 共 {totalProblems} 处问题\n");
            return sb.ToString();
        }

        private static int ProbeAt(VisualTreeAsset vta, PanelSettings sourceSettings, string panelRootName, bool stressFill,
            Vector2Int resolution, StringBuilder sb, System.Action<VisualElement> prepare = null)
        {
            // 克隆 PanelSettings 并挂一张目标尺寸的 RenderTexture：面板按这个尺寸 + 原缩放规则算参考坐标，
            // 等价于在该分辨率的屏幕上布局，不需要真的改 Game 视图分辨率。
            PanelSettings settings = Object.Instantiate(sourceSettings);
            settings.hideFlags = HideFlags.HideAndDontSave;
            var rt = new RenderTexture(resolution.x, resolution.y, 0) { hideFlags = HideFlags.HideAndDontSave };
            settings.targetTexture = rt;
            var go = new GameObject("__UiToolkitLayoutProbe") { hideFlags = HideFlags.HideAndDontSave };
            int problems = 0;
            try
            {
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = settings;
                doc.visualTreeAsset = vta;
                VisualElement treeRoot = doc.rootVisualElement;
                VisualElement target = string.IsNullOrEmpty(panelRootName) ? treeRoot : treeRoot.Q<VisualElement>(panelRootName);
                if (target == null)
                {
                    sb.AppendLine($"[{resolution.x}x{resolution.y}] 找不到节点 '{panelRootName}'");
                    return 1;
                }
                RevealHidden(target);
                if (stressFill)
                {
                    StressFill(target);
                }
                prepare?.Invoke(target);
                ForceLayout(treeRoot);

                Rect panelRect = treeRoot.panel.visualTree.worldBound;
                Rect rootRect = target.worldBound;
                sb.AppendLine($"[{resolution.x}x{resolution.y}] 面板坐标 {panelRect.width:F0}x{panelRect.height:F0}，目标 {Fmt(rootRect)}");

                if (!Contains(panelRect, rootRect))
                {
                    problems++;
                    sb.AppendLine($"  超出屏幕：{Fmt(rootRect)}");
                }

                var reported = new HashSet<VisualElement>();
                target.Query<VisualElement>().ForEach(e =>
                {
                    if (e == target || !IsDisplayed(e, target))
                    {
                        return;
                    }
                    Rect r = e.worldBound;
                    if (r.width > 0.5f && !IsInsideScrollContent(e, target) && !Contains(rootRect, r) && reported.Add(e))
                    {
                        problems++;
                        if (problems <= 20) sb.AppendLine($"  越界：{Describe(e)} {Fmt(r)}");
                    }
                    if (e is TextElement text && !string.IsNullOrEmpty(text.text))
                    {
                        if (r.width < 1f && e.resolvedStyle.flexGrow > 0f)
                        {
                            problems++;
                            if (problems <= 20) sb.AppendLine($"  塌成 0 宽：{Describe(e)}");
                            return;
                        }
                        if (e.resolvedStyle.whiteSpace == WhiteSpace.NoWrap
                            && e.resolvedStyle.textOverflow != TextOverflow.Ellipsis)
                        {
                            Vector2 need = text.MeasureTextSize(text.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                            float avail = r.width - e.resolvedStyle.paddingLeft - e.resolvedStyle.paddingRight;
                            // MeasureTextSize 对粗体/CJK 比布局实际测量宽约 5%，不留容差会把自然宽度的标题误报为截断。
                            if (need.x > avail + Mathf.Max(4f, avail * 0.08f))
                            {
                                problems++;
                                if (problems <= 20) sb.AppendLine($"  文字被硬截断（需 {need.x:F0}px，只有 {avail:F0}px，且未设省略号）：{Describe(e)} '{Shorten(text.text)}'");
                            }
                        }
                    }
                });
                if (problems > 20)
                {
                    sb.AppendLine($"  ……其余 {problems - 20} 处省略");
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(settings);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            return problems;
        }

        /// <summary>USS 里 Unity 不认识的属性/伪类会被静默丢弃，布局探针测不到"本该生效却没生效"的样式，只能扫文本。</summary>
        private static readonly (string Pattern, string Message)[] UssLintRules =
        {
            ("-unity-text-overflow", "Unity 的省略号属性是标准名 text-overflow: ellipsis，-unity-text-overflow 会被静默忽略"),
            (":last-child", "USS 不支持 :last-child，用负外边距或显式类名"),
            (":first-child", "USS 不支持 :first-child，用负外边距或显式类名"),
            (":nth-child", "USS 不支持 :nth-child，用显式类名"),
            (":not(", "USS 不支持 :not()，用显式类名"),
            ("z-index", "USS 没有 z-index，层级靠元素顺序 / BringToFront / sortingOrder"),
            ("box-shadow", "USS 不支持 box-shadow"),
            ("display: grid", "USS 没有 grid，用 flex-direction: row + flex-wrap: wrap"),
            ("gap:", "USS 没有 gap，用子元素 margin"),
        };

        /// <summary>检查 UXML 同目录的 USS：不支持的写法，以及 UXML 用到但 USS 没定义的"*hidden"状态类
        /// （战术指挥面板就是因为 .is-hidden 没定义，"关闭"按钮点了毫无效果）。</summary>
        private static int LintFolder(string uxmlPath, StringBuilder sb)
        {
            string folder = System.IO.Path.GetDirectoryName(uxmlPath);
            var ussText = new StringBuilder();
            int problems = 0;
            // 同目录（含子目录）的 USS + UXML 里 <Style src> 引用的外部 USS（如 ../Common/MachineWindow.uss）。
            var ussFiles = new List<string>(System.IO.Directory.GetFiles(folder, "*.uss", System.IO.SearchOption.AllDirectories));
            foreach (System.Text.RegularExpressions.Match styleRef in System.Text.RegularExpressions.Regex.Matches(
                         System.IO.File.ReadAllText(uxmlPath), @"<(?:ui:)?Style\s+src=""([^""]+\.uss)"""))
            {
                string src = styleRef.Groups[1].Value;
                string resolved = src.StartsWith("project://", StringComparison.Ordinal) || src.StartsWith("/", StringComparison.Ordinal)
                    ? null
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, src));
                if (resolved != null && System.IO.File.Exists(resolved)
                    && !ussFiles.Exists(f => string.Equals(System.IO.Path.GetFullPath(f), resolved, StringComparison.OrdinalIgnoreCase)))
                {
                    ussFiles.Add(resolved);
                }
            }
            foreach (string uss in ussFiles)
            {
                // 先剥掉 /* */ 注释（替换成等量换行，行号不变），注释里提到这些写法不算违规。
                string raw = System.Text.RegularExpressions.Regex.Replace(System.IO.File.ReadAllText(uss), @"/\*[\s\S]*?\*/",
                    c => new string('\n', c.Value.Split('\n').Length - 1));
                ussText.AppendLine(raw);
                string[] lines = raw.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    foreach ((string pattern, string message) in UssLintRules)
                    {
                        if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            problems++;
                            sb.AppendLine($"[USS] {uss.Replace('\\', '/')}:{i + 1} {message}");
                        }
                    }
                }
            }
            string uxml = System.IO.File.ReadAllText(uxmlPath);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(uxml, @"class=""([^""]*)"""))
            {
                foreach (string cls in m.Groups[1].Value.Split(' '))
                {
                    if (cls.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0
                        && ussText.ToString().IndexOf("." + cls, StringComparison.Ordinal) < 0)
                    {
                        problems++;
                        sb.AppendLine($"[USS] UXML 用了 .{cls} 但同目录 USS 没定义它——切换这个类不会有任何效果");
                    }
                }
            }
            return problems;
        }

        private static void RevealHidden(VisualElement target)
        {
            for (VisualElement e = target; e != null; e = e.parent)
            {
                var hiddenClasses = new List<string>();
                foreach (string c in e.GetClasses())
                {
                    if (c.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hiddenClasses.Add(c);
                    }
                }
                foreach (string c in hiddenClasses)
                {
                    e.RemoveFromClassList(c);
                }
            }
        }

        private static void StressFill(VisualElement target)
        {
            target.Query<DropdownField>().ForEach(d =>
            {
                d.choices = new List<string> { StressText };
                d.SetValueWithoutNotify(StressText);
            });
            target.Query<TextField>().ForEach(t => t.SetValueWithoutNotify(StressText));
        }

        /// <summary><c>ValidateLayout</c> 在 BaseVisualElementPanel 上是 internal；不跑它 resolvedStyle/worldBound 都是旧值。</summary>
        private static void ForceLayout(VisualElement element)
        {
            IPanel panel = element?.panel;
            panel?.GetType().GetMethod("ValidateLayout",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(panel, null);
        }

        private static bool IsDisplayed(VisualElement e, VisualElement stopAt)
        {
            for (VisualElement c = e; c != null; c = c.parent)
            {
                if (c.resolvedStyle.display == DisplayStyle.None || c.resolvedStyle.visibility == Visibility.Hidden)
                {
                    return false;
                }
                if (c == stopAt)
                {
                    break;
                }
            }
            return true;
        }

        /// <summary>ScrollView 内容区本来就允许超出视口，由滚动条负责，不算越界。</summary>
        private static bool IsInsideScrollContent(VisualElement e, VisualElement stopAt)
        {
            for (VisualElement c = e.parent; c != null && c != stopAt; c = c.parent)
            {
                if (c is ScrollView scroll && c != e && scroll.contentContainer != e)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(Rect outer, Rect inner) =>
            inner.xMin >= outer.xMin - 0.5f && inner.yMin >= outer.yMin - 0.5f &&
            inner.xMax <= outer.xMax + 0.5f && inner.yMax <= outer.yMax + 0.5f;

        private static string Describe(VisualElement e)
        {
            string name = string.IsNullOrEmpty(e.name) ? string.Empty : "#" + e.name;
            string owner = e.parent != null && !string.IsNullOrEmpty(e.parent.name) ? $"（父 #{e.parent.name}）" : string.Empty;
            return $"{e.GetType().Name}{name}.{string.Join(".", e.GetClasses())}{owner}";
        }

        private static string Fmt(Rect r) => $"({r.x:F0},{r.y:F0} {r.width:F0}x{r.height:F0})";

        private static string Shorten(string s) => s.Length <= 24 ? s : s.Substring(0, 24) + "…";

        [MenuItem("Tools/BinGames/UI Toolkit 布局探针（选中 UXML）")]
        private static void ProbeSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[UiToolkitLayoutProbe] 请先在 Project 窗口选中一个 .uxml。");
                return;
            }
            Debug.Log($"[UiToolkitLayoutProbe] {path}\n{Probe(path)}");
        }
    }
}
