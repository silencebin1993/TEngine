using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// EMG1-CHAIN-01（FG19 FGR-EMG-181）：“因果链”键（<see cref="GameActionId.CausalChain"/>，战略与接入上下文，可重绑；临时默认 Ctrl+T）开 / 关光标附近的因果链叠加：
    /// 最近 emg.chain.overlay_chains 条链，每条一行文字（<see cref="EmgChainView"/>），并在世界上把同一条链的节点位置按先后连线。战略暂停中照样可用（只读内核环，不推进模拟）。
    /// - 只在打开时、每 emg.chain.refresh_seconds 真实秒读一次环（O(环长)），关着时不读；连线每帧只投影已建好的点（O(显示的节点数)）。
    /// - 视图在最上层浮层 UiKitOverlay.uxml（CausalChainWires / CausalChainPanel，8 个固定行槽），外观在 UiKit.uss；没有绑定视图（编辑模式自检）时逻辑照常，只是不画。
    /// </summary>
    public static class CausalChainOverlay
    {
        public static bool IsOpen { get; private set; }
        public static int Revision { get; private set; }
        /// <summary>本进程读环次数（自检：关着时不读、打开时按间隔读）。</summary>
        public static int Reads { get; private set; }
        public static System.Func<float> Clock = () => Time.realtimeSinceStartup;

        private static readonly List<EmgChainLine> Lines = new List<EmgChainLine>(8);
        private static CombatSite _site;
        private static float _nextRead;

        private static VisualElement _panel;
        private static Label _title;
        private static Label _hint;
        private static readonly List<Label> _rows = new List<Label>(8);
        private static VisualElement _wires;
        private static readonly List<Vector2> _screenPts = new List<Vector2>(32);
        private static readonly List<int> _screenBreaks = new List<int>(8);

        public static int LineCount => Lines.Count;

        public static string LineText(int i) => i >= 0 && i < Lines.Count ? Lines[i].Text : string.Empty;

        public static EmgChainLine Line(int i) => i >= 0 && i < Lines.Count ? Lines[i] : null;

        public static int MaxChains => Mathf.Clamp(Mathf.RoundToInt(CombatSite.Tuning("emg.chain.overlay_chains", 4f)), 1, RowSlots);

        private static float RefreshSeconds => Mathf.Max(0.05f, CombatSite.Tuning("emg.chain.refresh_seconds", 0.25f));

        /// <summary>每帧（被观察的地点渲染时，<see cref="CombatSite.FrameRender"/>）：读键开 / 关；打开时按间隔重读环并摆到光标附近。</summary>
        public static void Tick(CombatSite site, Camera camera)
        {
            if (site == null || site.IsDisposed)
            {
                return;
            }
            if (InputRouter.ConsumeAction(GameActionId.CausalChain, InputRouter.Scope))
            {
                Toggle(site);
            }
            if (!IsOpen)
            {
                return;
            }
            if (_site != site)
            {
                if (_site != null && !_site.IsDisposed)
                {
                    return; // 修复轮（审查 P1）：一帧里有多个地点渲染（家园 + 靶场会话）时锁定打开它的那个地点，别的地点的 Tick 不读环、不排版
                }
                _site = site;
                _nextRead = 0f;
            }
            Refresh(false);
            Layout(camera, InputRouter.Reader.MousePosition);
        }

        /// <summary>开 / 关（按键与自检同一入口）。打开时立刻读一次。</summary>
        public static void Toggle(CombatSite site)
        {
            IsOpen = !IsOpen;
            _site = site;
            _nextRead = 0f;
            if (IsOpen)
            {
                Refresh(true);
            }
            else
            {
                Lines.Clear();
                Revision++;
            }
            Render();
        }

        /// <summary>到间隔就重读环（<paramref name="force"/> 立即读）。关着时什么都不读。</summary>
        public static void Refresh(bool force)
        {
            if (!IsOpen || _site == null || _site.IsDisposed)
            {
                return;
            }
            float now = Clock();
            if (!force && now < _nextRead)
            {
                return;
            }
            _nextRead = now + RefreshSeconds;
            EmgChainView.BuildChains(_site, CampaignSession.Current, MaxChains, Lines);
            Reads++;
            Revision++;
            Render();
        }

        /// <summary>离开观察 / 卸载这个地点：叠加开着且读的是它就关掉（不留指向已卸载地点的引用）。</summary>
        public static void CloseFor(CombatSite site)
        {
            if (_site != site)
            {
                return;
            }
            if (IsOpen)
            {
                Toggle(site);
            }
            _site = null;
        }

        public static void ResetForTests()
        {
            IsOpen = false;
            _site = null;
            Lines.Clear();
            Reads = 0;
            Revision = 0;
            _nextRead = 0f;
            Clock = () => Time.realtimeSinceStartup;
        }

        // ─────────────────────────────── 视图 ───────────────────────────────

        /// <summary>UXML 里的固定槽位数（MaxChains 钳到它）。</summary>
        public const int RowSlots = 8;

        public static void BindView(VisualElement root)
        {
            UnbindView();
            _wires = root.Q<VisualElement>("CausalChainWires");
            _panel = root.Q<VisualElement>("CausalChainPanel");
            _title = root.Q<Label>("CausalChainTitle");
            _hint = root.Q<Label>("CausalChainHint");
            for (int i = 0; i < RowSlots; i++)
            {
                Label l = root.Q<Label>("CausalChainRow" + i.ToString(CultureInfo.InvariantCulture));
                if (l != null)
                {
                    _rows.Add(l);
                }
            }
            if (_wires == null || _panel == null || _title == null || _hint == null || _rows.Count == 0)
            {
                Debug.LogError("[CausalChainOverlay] UiKitOverlay.uxml 缺少因果链节点（CausalChainWires / Panel / Title / Hint / Row0～7），叠加不可见。");
                UnbindView();
                return;
            }
            _wires.generateVisualContent += DrawWires;
            Render();
        }

        public static void UnbindView()
        {
            if (_wires != null)
            {
                _wires.generateVisualContent -= DrawWires;
            }
            _wires = null;
            _panel = null;
            _title = null;
            _hint = null;
            _rows.Clear();
        }

        private static void Render()
        {
            if (_panel == null)
            {
                return;
            }
            _panel.EnableInClassList("uk-hidden", !IsOpen);
            _wires.EnableInClassList("uk-hidden", !IsOpen);
            if (!IsOpen)
            {
                return;
            }
            _title.text = GameText.Format("emg.chain.title", MaxChains.ToString(CultureInfo.InvariantCulture));
            _hint.text = GameText.Format("emg.chain.hint", InputDisplay.ForAction(GameActionId.CausalChain));
            for (int i = 0; i < _rows.Count; i++)
            {
                string text = i < Lines.Count ? Lines[i].Text : i == 0 ? GameText.Get("emg.chain.empty") : null;
                _rows[i].text = text ?? string.Empty;
                _rows[i].EnableInClassList("uk-hidden", text == null);
            }
            _wires.MarkDirtyRepaint();
        }

        private static void Layout(Camera camera, Vector3 mouse)
        {
            if (_panel == null || _panel.panel == null)
            {
                return;
            }
            Vector2 p = RuntimePanelUtils.ScreenToPanel(_panel.panel, new Vector2(mouse.x, Screen.height - mouse.y));
            // 位置是运行时数值（光标），按 UI Toolkit 红线 2 直接写 style；定位方式与外观在 USS（uk-tooltip / uk-chain-panel）。
            _panel.style.left = p.x + 18f;
            _panel.style.top = p.y + 18f;
            _screenPts.Clear();
            _screenBreaks.Clear();
            if (camera != null)
            {
                foreach (EmgChainLine line in Lines)
                {
                    _screenBreaks.Add(_screenPts.Count);
                    foreach (Vector2 g in line.Points)
                    {
                        Vector3 s = camera.WorldToScreenPoint(new Vector3(g.x, 0.2f, g.y));
                        if (s.z > 0f)
                        {
                            _screenPts.Add(RuntimePanelUtils.ScreenToPanel(_panel.panel, new Vector2(s.x, Screen.height - s.y)));
                        }
                    }
                }
            }
            _wires.MarkDirtyRepaint();
        }

        private static void DrawWires(MeshGenerationContext ctx)
        {
            if (!IsOpen || _screenPts.Count < 2 || _title == null)
            {
                return;
            }
            Painter2D painter = ctx.painter2D;
            painter.strokeColor = _wires.resolvedStyle.color; // 连线颜色取连线层的 USS color（uk-chain-wires），不在代码里写死
            painter.lineWidth = 2f;
            for (int b = 0; b < _screenBreaks.Count; b++)
            {
                int from = _screenBreaks[b];
                int to = b + 1 < _screenBreaks.Count ? _screenBreaks[b + 1] : _screenPts.Count;
                if (to - from < 2)
                {
                    continue;
                }
                painter.BeginPath();
                painter.MoveTo(_screenPts[from]);
                for (int i = from + 1; i < to; i++)
                {
                    painter.LineTo(_screenPts[i]);
                }
                painter.Stroke();
            }
        }
    }
}
