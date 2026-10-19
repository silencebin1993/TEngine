using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
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
    /// FG8-EXP-04（FG08 FGR-EXP-040“纪念物放在核心保管库的展柜里展示”“图鉴有收集页”；卡片“纪念物在展柜里可以查看描述；收集进度”）：核心保管库面板。
    /// - 上部：本世界的收集进度（已收集 / 总数，四类分列；另有几件已找到还没带回）。
    /// - “展柜”页：已入库的纪念物（入库先后）；“收集”页：全部遗产——没找到的不剧透名字（类别 + 第几幕范围），找到的写在哪 / 谁在运 / 何时入库。
    /// - 点一行在下方描述区看名称、类别作用与背景故事（没找到的写去哪找）。
    /// 入口：归还核心的建筑面板“保管库…”。模态；Esc / 关闭 / 点遮罩关闭。空展柜有空状态说明（B12）。
    /// 刷新：遗产版本、页、选中、语言变化或游戏时间每过 1 秒才重建；O(遗产数)，不按帧分配。
    /// </summary>
    public sealed class HeritageVaultPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30056;
        public const int TabShowcase = 0;
        public const int TabCollection = 1;

        public static HeritageVaultPanelUIToolkit Instance { get; private set; }
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
        private Button _tabShowcase;
        private Button _tabCollection;
        private Label _empty;
        private ScrollView _list;
        private Label _detail;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<(VisualElement Row, Label Label)> _rows = new List<(VisualElement, Label)>();
        private readonly List<string> _ids = new List<string>();
        private int _tab = TabCollection;
        private string _selected;
        private int _key;

        protected override string UxmlLocation => "HeritageVaultPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public int RowCount => _ids.Count;
        public string RowId(int i) => i >= 0 && i < _ids.Count ? _ids[i] : null;
        public string RowText(int i) => i >= 0 && i < _ids.Count ? _rows[i].Label.text : string.Empty;
        public int CurrentTab => _tab;
        public string Selected => _selected;
        public Button TabShowcaseButton => _tabShowcase;
        public Button TabCollectionButton => _tabCollection;
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

        /// <summary>打开保管库到某一页（<see cref="TabShowcase"/> / <see cref="TabCollection"/>）。已开着就切过去。</summary>
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

        /// <summary>行模板（与黑匣子陈列馆同一个行模板；资源成对释放见 <see cref="OnDestroy"/>）。</summary>
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
                Log.Error("[HeritageVaultPanelUIToolkit] 加载 BlackBoxRow 失败，保管库列表行不可用。");
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
            _root = root.Q<VisualElement>("HeritageRoot");
            _title = root.Q<Label>("HeritageTitle");
            _help = root.Q<Button>("HeritageHelp");
            _close = root.Q<Button>("HeritageClose");
            _secStatus = root.Q<Label>("HeritageSecStatus");
            _status = root.Q<Label>("HeritageStatus");
            _tabShowcase = root.Q<Button>("HeritageTabShowcase");
            _tabCollection = root.Q<Button>("HeritageTabCollection");
            _empty = root.Q<Label>("HeritageEmpty");
            _list = root.Q<ScrollView>("HeritageList");
            _detail = root.Q<Label>("HeritageDetail");
            _footer = root.Q<Label>("HeritageFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _tabShowcase.clicked += () => SelectTab(TabShowcase);
            _tabCollection.clicked += () => SelectTab(TabCollection);
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
            _tab = tab == TabShowcase ? TabShowcase : TabCollection;
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>选中一行（点击行与自检同一入口）：下方描述区显示它的名称、类别作用与背景故事。</summary>
        public void SelectRow(int index)
        {
            if (index < 0 || index >= _ids.Count)
            {
                return;
            }
            _selected = _ids[index];
            _key = 0;
            Refresh(force: true);
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
                GuidanceHooks.Raise(GuidanceHooks.HeritageVaultFirstOpen);
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

        public void OpenCodex() => MechanicCodex.Open("codex.heritage");

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine(HeritageService.Revision, MachineNaming.Revision, _tab, _selected, (int)GameText.Language,
                HashCode.Combine(GameSettings.Revision, s != null ? s.GetHashCode() : 0, GameClock.Ticks / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("heritage.vault.title");
            _help.text = GameText.Get("heritage.vault.help");
            _close.text = GameText.Get("heritage.vault.close");
            _secStatus.text = GameText.Get("heritage.vault.sec.status");
            _status.text = s != null ? HeritageService.StatusText(s) : string.Empty;
            HeritageService.Counts c = HeritageService.CountsOf(s);
            List<string> showcase = s != null ? HeritageService.Showcase(s) : new List<string>();
            _tabShowcase.text = GameText.Format("heritage.vault.tab.showcase", showcase.Count);
            _tabCollection.text = GameText.Format("heritage.vault.tab.collection", c.Obtained, c.Total);
            _tabShowcase.EnableInClassList("bb-selected", _tab == TabShowcase);
            _tabCollection.EnableInClassList("bb-selected", _tab == TabCollection);

            List<string> ids = s == null ? new List<string>() : _tab == TabShowcase ? showcase : HeritageService.Collection();
            if (_selected != null && !ids.Contains(_selected))
            {
                _selected = null;
            }
            var texts = new List<string>(ids.Count);
            var locked = new List<bool>(ids.Count);
            foreach (string id in ids)
            {
                texts.Add(HeritageService.RowText(s, id));
                HeritageStatus st = HeritageService.StatusOf(s, id);
                locked.Add(st == HeritageStatus.Hidden || st == HeritageStatus.Unplaced);
            }
            RebuildRows(ids, texts, locked);
            bool empty = ids.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            if (empty)
            {
                _empty.text = GameText.Get("heritage.vault.empty_showcase");
            }
            _detail.text = _selected != null ? HeritageService.DetailText(s, _selected) : GameText.Get("heritage.vault.detail_hint");
            _footer.text = GameText.Get("heritage.vault.footer");
        }

        private void RebuildRows(List<string> ids, List<string> texts, List<bool> locked)
        {
            while (_rowTemplate != null && _rows.Count < ids.Count)
            {
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("bb-row-host");
                _list.Add(host);
                int index = _rows.Count;
                host.RegisterCallback<ClickEvent>(_ => SelectRow(index));
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
                row.EnableInClassList("hv-row-selected", ids[i] == _selected);
                row.EnableInClassList("hv-row-locked", locked[i]);
            }
        }
    }
}
