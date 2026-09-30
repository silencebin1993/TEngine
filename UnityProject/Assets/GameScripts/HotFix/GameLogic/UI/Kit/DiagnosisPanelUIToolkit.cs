using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-08（FG03 FGR-LOG-082“为什么不工作”：每座停工的建筑都给出原因，并尽量追溯到根源，原因条目可以点击，镜头跳到对应位置；FG00 B05 / B06 / B11 / B16）。
    /// 停靠在左侧的非模态面板（FGR-UX-005“左侧：当前选中对象的面板”）：看着清单点原因时世界画面不被遮住，点一条镜头就飞过去，面板留着接着点。
    /// - 汇总行：N 处停工（建筑 / 施工 / 物流网络各多少）；没有问题时写“一切正常”。
    /// - 页眉下一行“当前叠加层”：左侧停靠位同一时间只放一个——面板打开时收起叠加层选择器（它会被面板整个盖住），当前叠加层写在这里。
    /// - 每个停工对象一行：名字按钮（点 = 镜头飞到它）+ 每条原因链一行（“① 电力：”+ 各步按钮，最后一步是根源，点哪一步飞到哪一步）。
    ///   多重根源按“建筑本身 → 电力 → 输入 → 输出”排（<see cref="DiagCategory"/>，ADR-LOG-008）。
    /// - 页脚：开关键（默认 Ctrl+O）、排序规则、Esc 关闭。
    /// 数据来自 <see cref="RootCauseDiagnosis"/> 的缓存（分帧诊断，每 diag.refresh_seconds 真实秒开始新的一轮，暂停中也刷新）。
    /// 行按对象编号 + “形状”（原因链条数、各链类别与步数）复用：同一对象形状没变只改变了的文字（库存、发电读数这类实时数字）——按钮不换、悬停提示不闪、按下到松开之间不丢点击；
    /// 清单里插进 / 删掉一处时其余行只挪位置；
    /// 只有形状变了的行才新建，旧行的悬停提示先解绑（<see cref="UiTooltip.Detach"/>，否则静态的提示绑定表只增不减）。版本号都没变时 O(1) 返回，不拼字符串。
    /// 行元素按数量由代码创建（可变数量内容在 ScrollView 里，UI Toolkit 红线 5），外观全在 DiagnosisPanelStyle.uss。
    /// </summary>
    public sealed class DiagnosisPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>通知 30040 之上、布局库 30044 之下（非模态，停靠左侧）。</summary>
        public const int Order = 30043;

        public static DiagnosisPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        /// <summary>一行（一个停工对象）的元素与它当前指向的诊断。按钮的点击 / 提示按“行 + 行内下标”取目标，原地更新只换目标、不换按钮。</summary>
        private sealed class RowView
        {
            public VisualElement Root;
            public Button Subject;
            public DiagReport Target;
            public ulong Shape;
            public bool Used;
            public string DrawnName;
            public readonly List<Label> Cats = new List<Label>(2);
            public readonly List<Button> Steps = new List<Button>(4);
            public readonly List<DiagStep> Targets = new List<DiagStep>(4);
            public readonly List<string> Drawn = new List<string>(4);

            public DiagStep TargetAt(int i) => i >= 0 && i < Targets.Count ? Targets[i] : null;
        }

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Label _summary;
        private Label _overlay;
        private Label _more;
        private Label _empty;
        private ScrollView _list;
        private Label _hint;
        private int _drawnRevision = -1;
        private int _drawnStructure = -1;
        private int _drawnOverlayRevision = -1;
        private readonly UiTextVersion _texts = new UiTextVersion();
        private readonly List<RowView> _rows = new List<RowView>(16);
        private readonly List<RowView> _next = new List<RowView>(16);
        private readonly Dictionary<string, RowView> _byKey = new Dictionary<string, RowView>(16);
        private readonly List<Button> _stepButtons = new List<Button>(32);
        private readonly List<DiagStep> _stepTargets = new List<DiagStep>(32);

        protected override string UxmlLocation => "DiagnosisPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public string OverlayText => _overlay?.text ?? string.Empty;
        public int RowCount => _rows.Count;
        public int StepButtonCount => _stepButtons.Count;
        public Button StepButton(int i) => i >= 0 && i < _stepButtons.Count ? _stepButtons[i] : null;
        public DiagStep StepTarget(int i) => i >= 0 && i < _stepTargets.Count ? _stepTargets[i] : null;
        public Button SubjectButton(int i) => i >= 0 && i < _rows.Count ? _rows[i].Subject : null;
        public string SubjectText(int i) => i >= 0 && i < _rows.Count ? _rows[i].Subject.text : string.Empty;
        public DiagReport SubjectTarget(int i) => i >= 0 && i < _rows.Count ? _rows[i].Target : null;
        /// <summary>改动了元素树（新建 / 删除了行）的刷新次数。</summary>
        public int RebuildCount { get; private set; }
        /// <summary>只原地改了文字的刷新次数（行的形状都没变）。</summary>
        public int InPlaceUpdateCount { get; private set; }
        /// <summary>累计新建过的行数（形状变了的行才新建）。</summary>
        public int RowsCreated { get; private set; }
        /// <summary>最近一次刷新（新建行或原地改文字）的耗时（毫秒）。</summary>
        public double LastRefreshMs { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            ClearRows();
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open();
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("DiagnosisRoot");
            _root.pickingMode = PickingMode.Ignore; // 停靠面板：根节点铺满全屏只为定位，窗口本身才拦截点击（红线 9 说的是窗口根）。
            _title = root.Q<Label>("DiagnosisTitle");
            _count = root.Q<Label>("DiagnosisCount");
            _close = root.Q<Button>("DiagnosisClose");
            _summary = root.Q<Label>("DiagnosisSummary");
            _overlay = root.Q<Label>("DiagnosisOverlay");
            _empty = root.Q<Label>("DiagnosisEmpty");
            _list = root.Q<ScrollView>("DiagnosisList");
            _hint = root.Q<Label>("DiagnosisHint");
            _close.clicked += () => SetOpen(false);
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.DiagnosisFirstOpen);
                // 左侧停靠位同一时间只放一个：选择器会被面板整个盖住，先收起（当前叠加层写在面板页眉下）。
                OverlayHudUIToolkit.CloseSelector();
                UiEscapeStack.Push(this, () => SetOpen(false));
                _drawnRevision = -1;
                _drawnStructure = -1;
                _drawnOverlayRevision = -1;
                _texts.Reset();
                CampaignState state = CampaignSession.Current;
                if (RootCauseDiagnosis.HasFreshResult(state))
                {
                    // 手上有刚发布的结果：先显示它，立刻开始新的一轮（分帧，几帧后更新）。
                    RootCauseDiagnosis.Invalidate();
                }
                else
                {
                    // 没有（第一次打开 / 换了战役 / 很久没刷新）：打开这一下同步整份做一次，面板不先显示过时的清单。
                    RootCauseDiagnosis.Refresh(state, force: true);
                }
                Refresh();
            }
            else
            {
                UiEscapeStack.Remove(this);
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            RootCauseDiagnosis.Refresh(CampaignSession.Current);
            Refresh();
        }

        /// <summary>按诊断缓存刷新：版本号都没变时 O(1) 返回（只比较整数，不拼字符串）；形状相同的行只改变了的文字；形状变了的行才新建。自检直接调。</summary>
        public void Refresh()
        {
            bool text = _texts.Changed();
            int overlayRevision = GameLogic.View.OverlayService.Revision;
            if (text || overlayRevision != _drawnOverlayRevision)
            {
                _drawnOverlayRevision = overlayRevision;
                RefreshOverlayLine();
            }
            if (!text && _drawnRevision == RootCauseDiagnosis.Revision && _drawnStructure == RootCauseDiagnosis.StructureRevision)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _drawnRevision = RootCauseDiagnosis.Revision;
            _drawnStructure = RootCauseDiagnosis.StructureRevision;
            IReadOnlyList<DiagReport> reports = RootCauseDiagnosis.Reports;
            if (text)
            {
                _title.text = GameText.Get("diag.panel.title");
                _close.text = GameText.Get("power.panel.close");
                _hint.text = GameText.Format("diag.panel.hint", InputDisplay.ForAction(GameActionId.OpenDiagnosis));
                _empty.text = GameText.Get("diag.panel.all_ok");
            }
            int buildings = 0;
            int sites = 0;
            int nets = 0;
            foreach (DiagReport r in reports)
            {
                if (r.Subject == DiagSubject.Building)
                {
                    buildings++;
                }
                else if (r.Subject == DiagSubject.Site)
                {
                    sites++;
                }
                else
                {
                    nets++;
                }
            }
            SetText(_count, GameText.Format("diag.panel.count", reports.Count));
            SetText(_summary, reports.Count == 0 ? string.Empty : GameText.Format("diag.panel.summary", reports.Count, buildings, sites, nets));
            _empty.EnableInClassList("uk-hidden", reports.Count > 0);

            // 行按对象编号复用：同一个对象、形状没变 → 原地只改变了的文字；清单里插进 / 删掉一处时，其余行只挪位置，不重建、不重写文字。
            bool tree = false;
            _next.Clear();
            for (int i = 0; i < reports.Count; i++)
            {
                DiagReport r = reports[i];
                ulong shape = ShapeOf(r);
                if (r.SubjectId != null && _byKey.TryGetValue(r.SubjectId, out RowView row) && row.Shape == shape && !row.Used)
                {
                    Fill(row, r, text);
                }
                else
                {
                    row = CreateRow(r, shape);
                    tree = true;
                }
                row.Used = true;
                _next.Add(row);
            }
            foreach (RowView old in _rows)
            {
                if (!old.Used)
                {
                    DetachRow(old);
                    old.Root.RemoveFromHierarchy();
                    tree = true;
                }
            }
            _more?.RemoveFromHierarchy();
            VisualElement content = _list.contentContainer;
            for (int i = 0; i < _next.Count; i++)
            {
                VisualElement want = _next[i].Root;
                if (i >= content.childCount || content[i] != want)
                {
                    content.Insert(i, want); // 已在别处的元素会先从原位置移出
                    tree = true;
                }
            }
            _rows.Clear();
            _byKey.Clear();
            foreach (RowView row in _next)
            {
                row.Used = false;
                _rows.Add(row);
                if (row.Target?.SubjectId != null)
                {
                    _byKey[row.Target.SubjectId] = row;
                }
            }
            _next.Clear();
            if (RootCauseDiagnosis.Truncated > 0)
            {
                if (_more == null)
                {
                    _more = new Label();
                    _more.AddToClassList("dg-more");
                    tree = true;
                }
                content.Add(_more);
                SetText(_more, GameText.Format("diag.panel.more", RootCauseDiagnosis.Truncated));
            }
            else if (_more != null)
            {
                _more = null;
                tree = true;
            }
            // 全局步骤下标（自检 / 冒烟按 “DgStep” + 下标找按钮）：按行顺序重排，名字只在变了时写。
            _stepButtons.Clear();
            _stepTargets.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                RowView row = _rows[i];
                SetName(row.Subject, "DgSubject" + i);
                for (int k = 0; k < row.Steps.Count; k++)
                {
                    SetName(row.Steps[k], "DgStep" + _stepButtons.Count);
                    _stepButtons.Add(row.Steps[k]);
                    _stepTargets.Add(row.Targets[k]);
                }
            }
            if (tree)
            {
                RebuildCount++;
            }
            else
            {
                InPlaceUpdateCount++;
            }
            LastRefreshMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void RefreshOverlayLine()
        {
            if (_overlay == null)
            {
                return;
            }
            GameLogic.View.OverlayKind a = GameLogic.View.OverlayService.Active;
            string key = InputDisplay.ForAction(GameActionId.ToggleOverlay);
            _overlay.text = a == GameLogic.View.OverlayKind.None
                ? GameText.Format("overlay.hud.none", key, GameLogic.View.OverlayService.Name(GameLogic.View.OverlayService.Last))
                : GameText.Format("overlay.hud.active", GameLogic.View.OverlayService.Name(a), key);
        }

        /// <summary>行的形状：原因链条数、各链类别与步数（决定行里有几个类别标签、几个按钮、哪个是根源）。</summary>
        private static ulong ShapeOf(DiagReport r)
        {
            ulong h = 1469598103934665603UL;
            h = (h ^ (ulong)r.Chains.Count) * 1099511628211UL;
            foreach (DiagChain c in r.Chains)
            {
                h = (h ^ ((ulong)c.Category + 0x10UL)) * 1099511628211UL;
                h = (h ^ ((ulong)c.Steps.Count + 0x100UL)) * 1099511628211UL;
            }
            return h;
        }

        private RowView CreateRow(DiagReport r, ulong shape)
        {
            RowsCreated++;
            var row = new RowView { Shape = shape, Root = new VisualElement() };
            row.Root.AddToClassList("dg-row");
            row.Subject = new Button();
            row.Subject.AddToClassList("mw-btn");
            row.Subject.AddToClassList("dg-subject");
            row.Subject.clicked += () => LocateSubject(row.Target);
            row.Root.Add(row.Subject);
            foreach (DiagChain chain in r.Chains)
            {
                var line = new VisualElement();
                line.AddToClassList("dg-chain");
                var cat = new Label();
                cat.AddToClassList("dg-cat");
                line.Add(cat);
                row.Cats.Add(cat);
                for (int s = 0; s < chain.Steps.Count; s++)
                {
                    int local = row.Steps.Count;
                    var b = new Button();
                    b.AddToClassList("dg-step");
                    if (s == chain.Steps.Count - 1)
                    {
                        b.AddToClassList("dg-step-root");
                    }
                    // 按“行 + 行内下标”取目标：原地更新只换目标，按钮与闭包不换。
                    b.clicked += () => LocateStep(row.TargetAt(local));
                    UiTooltip.Attach(b, () => new TooltipContent
                    {
                        Title = GameText.Get("diag.panel.locate_tip"),
                        Body = row.TargetAt(local)?.Text ?? string.Empty,
                        CodexEntryId = "codex.logistics.diagnosis",
                    });
                    line.Add(b);
                    row.Steps.Add(b);
                    row.Targets.Add(null);
                    row.Drawn.Add(null);
                }
                row.Root.Add(line);
            }
            Fill(row, r, true);
            return row;
        }

        /// <summary>把诊断写进形状相同的行：只格式化文字变了的那几步（<paramref name="force"/> = 语言 / 键位变了，全部重写）。</summary>
        private static void Fill(RowView row, DiagReport r, bool force)
        {
            row.Target = r;
            if (force || row.DrawnName != r.Name)
            {
                row.DrawnName = r.Name;
                row.Subject.text = GameText.Format("diag.panel.subject", r.Name);
            }
            int k = 0;
            for (int c = 0; c < r.Chains.Count; c++)
            {
                DiagChain chain = r.Chains[c];
                if (force)
                {
                    row.Cats[c].text = GameText.Format("diag.panel.chain", c + 1, RootCauseDiagnosis.CategoryName(chain.Category));
                }
                for (int s = 0; s < chain.Steps.Count; s++, k++)
                {
                    DiagStep step = chain.Steps[s];
                    row.Targets[k] = step;
                    Button b = row.Steps[k];
                    if (force || row.Drawn[k] != step.Text)
                    {
                        row.Drawn[k] = step.Text;
                        // 根源除了颜色，文字本身带“根源：”前缀（B15）；非最后一步带“→”前缀表示顺着往下读。
                        b.text = s == chain.Steps.Count - 1 ? GameText.Format("diag.panel.root", step.Text) : GameText.Format("diag.panel.step", step.Text);
                    }
                    if (b.enabledSelf != step.HasPosition)
                    {
                        b.SetEnabled(step.HasPosition);
                    }
                }
            }
        }

        private static void DetachRow(RowView row)
        {
            foreach (Button b in row.Steps)
            {
                UiTooltip.Detach(b);
            }
        }

        private void ClearRows()
        {
            foreach (RowView row in _rows)
            {
                DetachRow(row);
            }
            _rows.Clear();
            _byKey.Clear();
            _stepButtons.Clear();
            _stepTargets.Clear();
        }

        private static void SetText(TextElement e, string text)
        {
            if (e.text != text)
            {
                e.text = text;
            }
        }

        private static void SetName(VisualElement e, string name)
        {
            if (e.name != name)
            {
                e.name = name;
            }
        }

        /// <summary>点原因条目：镜头飞到那一步（建造模式开着时保持开着，FG-GAP-071）。</summary>
        public bool LocateStep(DiagStep step) => RootCauseDiagnosis.Locate(step);

        public bool LocateSubject(DiagReport r) =>
            r != null && RootCauseDiagnosis.Locate(new DiagStep { HasPosition = true, Position = r.Position, Text = r.Name });

        /// <summary>自检：点第 i 个原因条目。</summary>
        public bool ClickStep(int i)
        {
            DiagStep st = StepTarget(i);
            return st != null && LocateStep(st);
        }
    }
}
