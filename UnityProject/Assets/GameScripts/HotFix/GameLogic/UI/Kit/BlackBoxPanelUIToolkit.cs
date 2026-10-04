using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG5-RND-06（FG05 FGR-RND-060；FG13 FGU-40 纪念墙：阵亡机器名单与经历）：黑匣子陈列馆面板。
    /// - 上部：分析状态（几座陈列馆在工作、每座同时分析几个、每个黑匣子多少技术数据、多久产完、累计产出 / 已分析个数；没有陈列馆时写怎么建）。
    /// - “黑匣子”页：每个黑匣子一行——谁的、现在在哪（区域地面 + 坐标、可以再取 / 被谁携带 / 等陈列馆 / 排队第几 / 分析中 + 进度与剩余时间 / 暂停 / 已分析 + 技术数据）。
    /// - “纪念墙”页：每台阵亡机器一行——名字 · 编号 · 型号、阵亡地点（地点 + 坐标）与时间、经历与统计、黑匣子去向；按时间（最近的在前）/ 按地点（同一地点按时间）排序。
    /// 入口：陈列馆建筑面板“陈列馆…”、机器名册“纪念墙…”。模态；Esc / 关闭 / 点遮罩关闭。空列表有空状态说明（B12）。
    /// 刷新：黑匣子版本、机器名册版本、页 / 排序、语言变化或游戏时间每过 1 秒才重建；O(行数)，不按帧分配。
    /// </summary>
    public sealed class BlackBoxPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30065;
        public const int TabBoxes = 0;
        public const int TabMemorial = 1;

        public static BlackBoxPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static int _pendingTab;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Label _secStatus;
        private Label _status;
        private Button _tabBoxes;
        private Button _tabMemorial;
        private VisualElement _sortBar;
        private Label _sortLabel;
        private Button _sortTime;
        private Button _sortPlace;
        private Label _empty;
        private ScrollView _list;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<(VisualElement Row, Label Label)> _rows = new List<(VisualElement, Label)>();
        private readonly List<int> _ids = new List<int>();
        private int _tab;
        private bool _byPlace;
        private int _key;

        protected override string UxmlLocation => "BlackBoxPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public int RowCount => _ids.Count;
        public int RowLogicId(int i) => i >= 0 && i < _ids.Count ? _ids[i] : 0;
        public string RowText(int i) => i >= 0 && i < _ids.Count ? _rows[i].Label.text : string.Empty;
        public bool RowHasClass(int i, string cls) => i >= 0 && i < _ids.Count && _rows[i].Row.ClassListContains(cls);
        public int CurrentTab => _tab;
        public bool SortByPlace => _byPlace;
        public bool SortBarVisible => _sortBar != null && !_sortBar.ClassListContains("uk-hidden");
        public Button TabBoxesButton => _tabBoxes;
        public Button TabMemorialButton => _tabMemorial;
        public Button SortTimeButton => _sortTime;
        public Button SortPlaceButton => _sortPlace;
        public Button CloseButton => _close;
        public Button HelpButton => _help;

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
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开陈列馆面板到某一页（<see cref="TabBoxes"/> / <see cref="TabMemorial"/>）。已开着就切过去。</summary>
        public static void Open(int tab)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingTab = tab;
                return;
            }
            Instance.SelectTab(tab);
            if (!IsOpen)
            {
                Instance.SetOpen(true);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open(_pendingTab);
            }
        }

        /// <summary>行模板（可变数量的行由模板克隆，红线 5；资源成对释放见 <see cref="OnDestroy"/>）。</summary>
        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BlackBoxRow");
            _templateLoading = false;
            if (this == null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            _rowTemplate = t;
            if (t == null)
            {
                Log.Error("[BlackBoxPanelUIToolkit] 加载 BlackBoxRow 失败，陈列馆列表行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("BlackBoxRoot");
            _title = root.Q<Label>("BlackBoxTitle");
            _help = root.Q<Button>("BlackBoxHelp");
            _close = root.Q<Button>("BlackBoxClose");
            _secStatus = root.Q<Label>("BlackBoxSecStatus");
            _status = root.Q<Label>("BlackBoxStatus");
            _tabBoxes = root.Q<Button>("BlackBoxTabBoxes");
            _tabMemorial = root.Q<Button>("BlackBoxTabMemorial");
            _sortBar = root.Q<VisualElement>("BlackBoxSortBar");
            _sortLabel = root.Q<Label>("BlackBoxSortLabel");
            _sortTime = root.Q<Button>("BlackBoxSortTime");
            _sortPlace = root.Q<Button>("BlackBoxSortPlace");
            _empty = root.Q<Label>("BlackBoxEmpty");
            _list = root.Q<ScrollView>("BlackBoxList");
            _footer = root.Q<Label>("BlackBoxFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _tabBoxes.clicked += () => SelectTab(TabBoxes);
            _tabMemorial.clicked += () => SelectTab(TabMemorial);
            _sortTime.clicked += () => SelectSort(false);
            _sortPlace.clicked += () => SelectSort(true);
            UiTooltip.Attach(_status, () => new TooltipContent { Title = GameText.Get("blackbox.panel.title"), Body = GameText.Get("codex.blackbox.gallery.body") });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        public void SelectTab(int tab)
        {
            _tab = tab == TabMemorial ? TabMemorial : TabBoxes;
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        public void SelectSort(bool byPlace)
        {
            _byPlace = byPlace;
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
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
                GuidanceHooks.Raise(GuidanceHooks.BlackBoxFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                Refresh(force: true);
            }
            else
            {
                InputRouter.PopModal(this);
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
            Refresh(force: false);
        }

        public void OpenCodex() => MechanicCodex.Open("codex.blackbox.gallery");

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine(BlackBoxService.Revision, MachineRegistry.RosterRevision, MachineNaming.Revision, _tab, _byPlace, (int)GameText.Language,
                HashCode.Combine(GameSettings.Revision, s != null ? s.GetHashCode() : 0, GameClock.Ticks / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("blackbox.panel.title");
            _help.text = GameText.Get("blackbox.panel.help");
            _close.text = GameText.Get("blackbox.panel.close");
            _secStatus.text = GameText.Get("blackbox.panel.sec.status");
            _status.text = s != null ? BlackBoxService.StatusText(s) : string.Empty;
            _tabBoxes.text = GameText.Format("blackbox.panel.tab.boxes", BlackBoxService.BoxCount(s));
            _tabMemorial.text = GameText.Format("blackbox.panel.tab.memorial", BlackBoxService.DeadCount());
            _tabBoxes.EnableInClassList("bb-selected", _tab == TabBoxes);
            _tabMemorial.EnableInClassList("bb-selected", _tab == TabMemorial);
            _sortBar.EnableInClassList("uk-hidden", _tab != TabMemorial);
            _sortLabel.text = GameText.Get("blackbox.panel.sort_label");
            _sortTime.text = GameText.Get("blackbox.panel.sort_time");
            _sortPlace.text = GameText.Get("blackbox.panel.sort_place");
            _sortTime.EnableInClassList("bb-selected", !_byPlace);
            _sortPlace.EnableInClassList("bb-selected", _byPlace);

            var ids = new List<int>();
            var texts = new List<string>();
            var classes = new List<string>();
            if (s != null && _tab == TabBoxes)
            {
                foreach (int id in BlackBoxService.BoxOrder(s))
                {
                    ids.Add(id);
                    texts.Add(BlackBoxService.BoxRowText(s, id));
                    RegionQuestItemRecord q = BlackBoxService.FindItem(s, id);
                    BlackBoxRecord b = BlackBoxService.Find(s, id);
                    classes.Add(q != null && q.State != RegionQuestItemState.Recovered ? "bb-row-field" : b != null && b.Done ? "bb-row-done" : null);
                }
            }
            else if (_tab == TabMemorial)
            {
                foreach (MachineRecord m in BlackBoxService.Memorial(_byPlace))
                {
                    ids.Add(m.LogicId);
                    texts.Add(BlackBoxService.MemorialText(s, m));
                    classes.Add(null);
                }
            }
            RebuildRows(ids, texts, classes);
            bool empty = ids.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            if (empty)
            {
                _empty.text = GameText.Get(_tab == TabMemorial ? "blackbox.panel.empty_memorial" : "blackbox.panel.empty_boxes");
            }
            _footer.text = GameText.Get("blackbox.panel.footer");
        }

        private void RebuildRows(List<int> ids, List<string> texts, List<string> classes)
        {
            while (_rowTemplate != null && _rows.Count < ids.Count)
            {
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("bb-row-host");
                _list.Add(host);
                _rows.Add((host.Q<VisualElement>("BbRow"), host.Q<Label>("BbLabel")));
            }
            _ids.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < ids.Count;
                (VisualElement row, Label label) = _rows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                _ids.Add(ids[i]);
                label.text = texts[i];
                row.EnableInClassList("bb-row-field", classes[i] == "bb-row-field");
                row.EnableInClassList("bb-row-done", classes[i] == "bb-row-done");
            }
        }
    }
}
