using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.View;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-08（FG13 FGU-12 叠加层选择器；FG03 FGR-LOG-080；FG00 B01 / B02 / B11 / B15 / B16）：家园的叠加层 HUD。
    /// - 左侧停靠条：“叠加层（Alt+O）”按钮 + 当前叠加层名与开关键（关着时写“叠加层：关”）——新玩家在画面上就能找到入口（B01）。
    /// - 选择器（非模态窗口，Alt+O 或点停靠条按钮开关）：8 种叠加层各一个按钮（写名字与直达键 Ctrl+Alt+1～8，当前那种有“▸”前缀，不只靠颜色）、“关闭叠加层”、
    ///   当前叠加层的图例、“为什么不工作（N）”按钮（打开停工清单）、提示行（O 开关、Alt+O、Ctrl+Alt+1～8、同时只显示一种）。
    /// - 世界标签层：<see cref="OverlayService.Labels"/>（≤ overlay.max_labels 条）每帧投影到屏幕；标签内容只在服务重建时更新（B18）。整层不拦截点击。
    /// 只在家园被观察时显示；每帧 O(标签数上限)。
    /// </summary>
    public sealed class OverlayHudUIToolkit : UiKitPanelHost
    {
        /// <summary>HUD 层（世界时间条 3 之上、建造栏 30030 之下）。</summary>
        public const int Order = 4;

        public static OverlayHudUIToolkit Instance { get; private set; }
        public static bool SelectorOpen { get; private set; }
        private static bool _pendingSelector;

        /// <summary>自检：编辑模式下没有载入的地点，强制当成“在家园”。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _dock;
        private Button _toggle;
        private Label _active;
        private VisualElement _selector;
        private Label _selectorTitle;
        private Button _selectorClose;
        private readonly Button[] _kindButtons = new Button[OverlayService.KindCount];
        private Button _off;
        private Label _legend;
        private Button _diag;
        private Label _hint;
        private VisualElement _labelLayer;
        private readonly List<Label> _labels = new List<Label>(48);
        private int _drawnRevision = -1;
        private int _drawnLabelRevision = -1;
        private int _drawnDiagRevision = -1;
        private readonly UiTextVersion _texts = new UiTextVersion();

        protected override string UxmlLocation => "OverlayHud";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool DockVisible => _dock != null && !_dock.ClassListContains("uk-hidden");
        public bool SelectorVisible => _selector != null && !_selector.ClassListContains("uk-hidden");
        public string ToggleText => _toggle?.text ?? string.Empty;
        public string ActiveText => _active?.text ?? string.Empty;
        public string LegendText => _legend?.text ?? string.Empty;
        public string DiagButtonText => _diag?.text ?? string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public Button KindButton(OverlayKind k) => k == OverlayKind.None ? _off : _kindButtons[(int)k - 1];
        public Button DiagButton => _diag;
        public Button ToggleButton => _toggle;
        public int VisibleLabelCount { get; private set; }
        public string LabelText(int i) => i >= 0 && i < _labels.Count ? _labels[i].text : string.Empty;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                SelectorOpen = false;
            }
            base.OnDestroy();
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingSelector)
            {
                _pendingSelector = false;
                SetSelector(true);
            }
        }

        public static void ToggleSelector()
        {
            if (!SelectorOpen && !_pendingSelector && !HomeHere())
            {
                // 家园以外：选择器里的 7 种都画不出来——不打开（否则下一帧就被收起，等于静默失效），发说明并指出这里可用的信号覆盖键。
                OverlayService.NotifySelectorHomeOnly();
                return;
            }
            if (Instance == null || Instance._selector == null)
            {
                _pendingSelector = !_pendingSelector;
                return;
            }
            Instance.SetSelector(!SelectorOpen);
        }

        public static void CloseSelector()
        {
            _pendingSelector = false;
            Instance?.SetSelector(false);
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _dock = root.Q<VisualElement>("OverlayDock");
            _toggle = root.Q<Button>("OverlayToggleButton");
            _active = root.Q<Label>("OverlayActiveLabel");
            _selector = root.Q<VisualElement>("OverlaySelector");
            _selectorTitle = root.Q<Label>("OverlaySelectorTitle");
            _selectorClose = root.Q<Button>("OverlaySelectorClose");
            for (int i = 0; i < _kindButtons.Length; i++)
            {
                var kind = (OverlayKind)(i + 1);
                _kindButtons[i] = root.Q<Button>("OverlayBtn" + (i + 1));
                _kindButtons[i].clicked += () => OverlayService.Toggle(kind);
                UiTooltip.Attach(_kindButtons[i], () => new TooltipContent
                {
                    Title = OverlayService.Name(kind),
                    Body = OverlayService.Legend(kind),
                    Shortcut = OverlayService.ActionOf(kind),
                    CodexEntryId = "codex.logistics.diagnosis",
                });
            }
            _off = root.Q<Button>("OverlayBtnOff");
            _off.clicked += () => OverlayService.Set(OverlayKind.None);
            _legend = root.Q<Label>("OverlayLegend");
            _diag = root.Q<Button>("OverlayDiagButton");
            _diag.clicked += () => DiagnosisPanelUIToolkit.Open();
            _hint = root.Q<Label>("OverlaySelectorHint");
            _toggle.clicked += () => SetSelector(!SelectorOpen);
            _selectorClose.clicked += () => SetSelector(false);
            UiTooltip.Attach(_toggle, () => new TooltipContent
            {
                Title = GameText.Get("overlay.selector.title"),
                Body = GameText.Get("overlay.hud.tip"),
                Shortcut = GameActionId.OverlaySelector,
                CodexEntryId = "codex.logistics.diagnosis",
            });
            _labelLayer = root.Q<VisualElement>("OverlayLabelLayer");
            _labelLayer.pickingMode = PickingMode.Ignore;
            _drawnRevision = -1;
        }

        public void SetSelector(bool open)
        {
            if (_selector == null || open == SelectorOpen)
            {
                return;
            }
            SelectorOpen = open;
            _selector.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                // 左侧停靠位同一时间只放一个：“为什么不工作”面板开着会整个盖住选择器，打开选择器时先把它收起（面板打开时同样收起选择器）。
                if (DiagnosisPanelUIToolkit.IsOpen)
                {
                    DiagnosisPanelUIToolkit.Close();
                }
                UiEscapeStack.Push(this, () => SetSelector(false));
                _drawnRevision = -1;
                _drawnDiagRevision = -1;
            }
            else
            {
                UiEscapeStack.Remove(this);
            }
        }

        private static bool HomeHere() => InWorldOverrideForTests || (GameRoot.AnyRegionActive && OverlayService.HomeObserved);

        private void Update()
        {
            Tick(Camera.main);
        }

        /// <summary>每帧（自检直接调）。</summary>
        public void Tick(Camera camera)
        {
            if (_dock == null)
            {
                return;
            }
            bool home = HomeHere();
            _dock.EnableInClassList("uk-hidden", !home);
            if (!home)
            {
                if (SelectorOpen)
                {
                    SetSelector(false);
                }
                HideLabels(0);
                return;
            }
            // 语言 / 键位 / 文本表只比较版本号（每帧不拼字符串、不分配）。
            bool textChanged = _texts.Changed();
            if (_drawnRevision != OverlayService.Revision || textChanged)
            {
                _drawnRevision = OverlayService.Revision;
                RefreshTexts();
            }
            if (SelectorOpen)
            {
                // “为什么不工作（N）”：选择器开着时推进分帧诊断（与面板、堵塞叠加层同一份缓存；同一帧多处调用只推进一次）。
                RootCauseDiagnosis.Refresh(CampaignSession.Current);
                if (_drawnDiagRevision != RootCauseDiagnosis.Revision || textChanged)
                {
                    _drawnDiagRevision = RootCauseDiagnosis.Revision;
                    _diag.text = GameText.Format("overlay.selector.diag", RootCauseDiagnosis.Reports.Count, InputDisplay.ForAction(GameActionId.OpenDiagnosis));
                }
            }
            PlaceLabels(camera);
        }

        private void RefreshTexts()
        {
            OverlayKind a = OverlayService.Active;
            _toggle.text = GameText.Format("overlay.hud.button", InputDisplay.ForAction(GameActionId.OverlaySelector));
            _active.text = a == OverlayKind.None
                ? GameText.Format("overlay.hud.none", InputDisplay.ForAction(GameActionId.ToggleOverlay), OverlayService.Name(OverlayService.Last))
                : GameText.Format("overlay.hud.active", OverlayService.Name(a), InputDisplay.ForAction(GameActionId.ToggleOverlay));
            _selectorTitle.text = GameText.Get("overlay.selector.title");
            _selectorClose.text = GameText.Get("power.panel.close");
            for (int i = 0; i < _kindButtons.Length; i++)
            {
                var kind = (OverlayKind)(i + 1);
                bool on = a == kind;
                _kindButtons[i].text = GameText.Format(on ? "overlay.selector.item_on" : "overlay.selector.item", OverlayService.Name(kind), InputDisplay.ForAction(OverlayService.ActionOf(kind)));
                _kindButtons[i].EnableInClassList("oh-kind-on", on);
            }
            _off.text = GameText.Get("overlay.selector.off");
            _off.SetEnabled(a != OverlayKind.None);
            _legend.text = a == OverlayKind.None ? GameText.Get("overlay.selector.no_legend") : OverlayService.Legend(a);
            _hint.text = GameText.Format("overlay.selector.hint", InputDisplay.ForAction(GameActionId.ToggleOverlay), InputDisplay.ForAction(GameActionId.OverlaySelector),
                InputDisplay.ForAction(GameActionId.OverlayFlow), InputDisplay.ForAction(GameActionId.OverlayConstruction));
            _diag.text = GameText.Format("overlay.selector.diag", RootCauseDiagnosis.Reports.Count, InputDisplay.ForAction(GameActionId.OpenDiagnosis));
        }

        // ── 世界标签 ────────────────────────────────────────────────────────────

        private void PlaceLabels(Camera camera)
        {
            IReadOnlyList<OverlayLabel> labels = OverlayService.Labels;
            if (!OverlayService.Visible || labels.Count == 0)
            {
                HideLabels(0);
                return;
            }
            while (_labels.Count < labels.Count)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("oh-label");
                l.AddToClassList("uk-hidden");
                _labelLayer.Add(l);
                _labels.Add(l);
            }
            bool refreshText = _drawnLabelRevision != OverlayService.LabelRevision;
            _drawnLabelRevision = OverlayService.LabelRevision;
            int shown = 0;
            for (int i = 0; i < labels.Count; i++)
            {
                OverlayLabel o = labels[i];
                Label l = _labels[i];
                if (refreshText)
                {
                    l.text = o.Tone > 0 ? "! " + o.Text : o.Text;
                    l.EnableInClassList("oh-label-warn", o.Tone == 1);
                    l.EnableInClassList("oh-label-severe", o.Tone >= 2);
                }
                if (camera != null && _labelLayer.panel != null)
                {
                    Vector3 screen = camera.WorldToScreenPoint(o.World);
                    if (screen.z <= 0f)
                    {
                        l.AddToClassList("uk-hidden");
                        continue;
                    }
                    Vector2 p = RuntimePanelUtils.ScreenToPanel(_labelLayer.panel, new Vector2(screen.x, Screen.height - screen.y));
                    // 位置是数据驱动的运行时数值（世界坐标投影），按 UI Toolkit 红线 2 允许直接写 style；外观全在 USS。
                    l.style.left = p.x;
                    l.style.top = p.y;
                }
                l.RemoveFromClassList("uk-hidden");
                shown++;
            }
            HideLabels(labels.Count);
            VisibleLabelCount = shown;
        }

        private void HideLabels(int from)
        {
            for (int i = from; i < _labels.Count; i++)
            {
                if (!_labels[i].ClassListContains("uk-hidden"))
                {
                    _labels[i].AddToClassList("uk-hidden");
                }
            }
            if (from == 0)
            {
                VisibleLabelCount = 0;
            }
        }
    }
}
