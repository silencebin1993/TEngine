using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG9-NAR-01（FG11 FGR-NAR-003、第 8 节；FG13 FGU-38、FGR-UX-051）：资料库面板。
    /// - “资料”页：本存档收到的全部叙事文本，最新的在前；按幕（全部 / 第一～三幕）与来源（核心备忘 / 数据终端 / 监听截获 / 演出）筛选；
    ///   未读的行首有 ●；点一行在右侧阅读（监听截获带“无线电杂音”）并标为已读；演出条目可以“重看演出”（随时可跳过、不改存档）。
    /// - “剧情回顾”页：已经发生的剧情节点，按发生先后（第几日 · 标题）。
    /// - 资料库按存档保存（数据在 <see cref="CampaignProgressState.Narrative"/>），新存档里是空的。正文字号跟随“字幕大小”设置。
    /// - 入口：暂停菜单“资料库”（盖在暂停菜单上面，关掉回到暂停菜单）、点“资料库新增 / 核心备忘”通知（打开到最新一条未读）、<see cref="NarrativeService.Open"/>。
    /// - 阅读时游戏不自动暂停（FG11 第 8 节）：面板是模态的界面输入上下文，但世界照常运行，可以手动暂停。
    /// - 状态：正常 / 空资料库 / 筛选后为空 / 剧情回顾为空；Esc / 关闭 / 点遮罩关闭。
    /// 刷新：资料库版本、页、筛选、选中、语言、设置变化时才重建，O(资料数)（本存档最多几十条）。
    /// </summary>
    public sealed class NarrativeLibraryPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30084;
        public const int TabEntries = 0;
        public const int TabRecap = 1;

        public static NarrativeLibraryPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Button _tabEntries;
        private Button _tabRecap;
        private Label _count;
        private VisualElement _actBar;
        private VisualElement _sourceBar;
        private readonly Button[] _actButtons = new Button[4];
        private readonly Button[] _sourceButtons = new Button[5];
        private Label _empty;
        private ScrollView _list;
        private Label _detailTitle;
        private Label _detailMeta;
        private Label _detailBody;
        private Button _replay;
        private Button _markAll;
        private Label _footer;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<string> _rowIds = new List<string>();
        private readonly List<NarrativeEntryRecord> _scratch = new List<NarrativeEntryRecord>();
        private int _tab = TabEntries;
        private int _act;
        private string _source;
        private string _selected;
        private int _key;

        protected override string UxmlLocation => "NarrativeLibraryPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int CurrentTab => _tab;
        public int ActFilter => _act;
        public string SourceFilter => _source;
        public string Selected => _selected;
        public int RowCount => _rowIds.Count;
        public string RowId(int i) => i >= 0 && i < _rowIds.Count ? _rowIds[i] : null;
        public string RowText(int i) => i >= 0 && i < _rowIds.Count ? _rows[i].text : string.Empty;
        public Button RowButton(int i) => i >= 0 && i < _rowIds.Count ? _rows[i] : null;
        public bool RowUnread(int i) => i >= 0 && i < _rowIds.Count && _rows[i].ClassListContains("nl-row-unread");
        public string TitleText => _title?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string DetailTitleText => _detailTitle?.text ?? string.Empty;
        public string DetailMetaText => _detailMeta?.text ?? string.Empty;
        public string DetailBodyText => _detailBody?.text ?? string.Empty;
        public float DetailFontSize => _detailBody != null ? _detailBody.style.fontSize.value.value : 0f;
        public bool ReplayVisible => _replay != null && !_replay.ClassListContains("uk-hidden");
        public bool FiltersVisible => _actBar != null && !_actBar.ClassListContains("uk-hidden");
        public Button ReplayButton => _replay;
        public Button MarkAllButton => _markAll;
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public Button TabEntriesButton => _tabEntries;
        public Button TabRecapButton => _tabRecap;
        public Button ActButton(int act) => act >= 0 && act < _actButtons.Length ? _actButtons[act] : null;
        /// <summary>来源按钮：0 = 全部，1～4 = <see cref="NarrativeCatalog.Sources"/> 的顺序。</summary>
        public Button SourceButton(int i) => i >= 0 && i < _sourceButtons.Length ? _sourceButtons[i] : null;
        public string TabEntriesText => _tabEntries?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;

        private void Awake()
        {
            Instance = this;
            NarrativeService.OpenRequested -= OnOpenRequested;
            NarrativeService.OpenRequested += OnOpenRequested;
        }

        protected override void OnDestroy()
        {
            NarrativeService.OpenRequested -= OnOpenRequested;
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        private static void OnOpenRequested(string id) => Open(id);

        /// <summary>打开资料库并选中 <paramref name="id"/>（null = 最新一条未读；切到“资料”页并清掉筛选，保证它在列表里）。</summary>
        public static void Open(string id)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingId = id;
                return;
            }
            Instance.OpenAt(id);
        }

        /// <summary>暂停菜单入口（最新一条未读）。</summary>
        public static void OpenFromMenu() => Open(null);

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                OpenAt(_pendingId);
            }
        }

        /// <summary>绑定 UXML（运行时与自检共用）；同时登记两类通知的“打开”去处（点通知 = 打开资料库到最新一条未读）。</summary>
        public void BindView(VisualElement root)
        {
            Instance = this;
            NarrativeService.OpenRequested -= OnOpenRequested;
            NarrativeService.OpenRequested += OnOpenRequested;
            _root = root.Q<VisualElement>("NlRoot");
            _title = root.Q<Label>("NlTitle");
            _help = root.Q<Button>("NlHelp");
            _close = root.Q<Button>("NlClose");
            _tabEntries = root.Q<Button>("NlTabEntries");
            _tabRecap = root.Q<Button>("NlTabRecap");
            _count = root.Q<Label>("NlCount");
            _actBar = root.Q<VisualElement>("NlActBar");
            _sourceBar = root.Q<VisualElement>("NlSourceBar");
            for (int i = 0; i < _actButtons.Length; i++)
            {
                int act = i;
                _actButtons[i] = root.Q<Button>("NlAct" + i);
                _actButtons[i].clicked += () => SetActFilter(act);
            }
            string[] srcNames = { "NlSrcAll", "NlSrcMemo", "NlSrcTerminal", "NlSrcIntercept", "NlSrcCutscene" };
            for (int i = 0; i < _sourceButtons.Length; i++)
            {
                string src = i == 0 ? null : NarrativeCatalog.Sources[i - 1];
                _sourceButtons[i] = root.Q<Button>(srcNames[i]);
                _sourceButtons[i].clicked += () => SetSourceFilter(src);
            }
            _empty = root.Q<Label>("NlEmpty");
            _list = root.Q<ScrollView>("NlList");
            _detailTitle = root.Q<Label>("NlDetailTitle");
            _detailMeta = root.Q<Label>("NlDetailMeta");
            _detailBody = root.Q<Label>("NlDetailBody");
            _replay = root.Q<Button>("NlReplay");
            _markAll = root.Q<Button>("NlMarkAll");
            _footer = root.Q<Label>("NlFooter");
            _close.clicked += () => SetOpen(false);
            // 图鉴（30075）在资料库（30084）之下：先关资料库再打开图鉴条目，否则图鉴开在看不见的下层。
            _help.clicked += () =>
            {
                SetOpen(false);
                MechanicCodex.Open("codex.narrative");
            };
            _tabEntries.clicked += () => SelectTab(TabEntries);
            _tabRecap.clicked += () => SelectTab(TabRecap);
            _replay.clicked += Replay;
            _markAll.clicked += MarkAllRead;
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            NotificationCenter.RegisterOpenHandler(NarrativeService.NotifyType, _ => NarrativeService.Open(null));
            NotificationCenter.RegisterOpenHandler(NarrativeService.MemoNotifyType,
                _ => NarrativeService.Open(NarrativeService.NewestUnread(CampaignSession.Current, NarrativeCatalog.SourceMemo)));
            _key = 0;
        }

        public void OpenAt(string id)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            string target = string.IsNullOrEmpty(id) ? NarrativeService.NewestUnread(s) : id;
            _tab = TabEntries;
            if (target != null && NarrativeService.Has(s, target))
            {
                _act = 0;
                _source = null;
                Select(target);
            }
            else
            {
                _selected = null; // 空资料库 / 换了存档：不保留上一份存档里选中的条目
            }
            if (!IsOpen)
            {
                SetOpen(true);
            }
            _key = 0;
            Refresh(force: true);
        }

        public void SelectTab(int tab)
        {
            _tab = tab == TabRecap ? TabRecap : TabEntries;
            _key = 0;
            Refresh(force: true);
        }

        public void SetActFilter(int act)
        {
            _act = Math.Max(0, Math.Min(3, act));
            _key = 0;
            Refresh(force: true);
        }

        public void SetSourceFilter(string source)
        {
            _source = string.IsNullOrEmpty(source) ? null : source;
            _key = 0;
            Refresh(force: true);
        }

        /// <summary>选中一条（点行与自检同一入口）：右侧阅读并标为已读。</summary>
        public void Select(string id)
        {
            _selected = id;
            NarrativeService.MarkRead(CampaignSession.Current, id);
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        public void SelectRow(int index)
        {
            if (index >= 0 && index < _rowIds.Count)
            {
                Select(_rowIds[index]);
            }
        }

        /// <summary>“重看演出”：关掉资料库，由演出播放器重播（随时可跳过、不改存档）。</summary>
        public void Replay()
        {
            CampaignState s = CampaignSession.Current;
            if (_selected == null || NarrativeCutscenePlayerUIToolkit.Instance == null)
            {
                return;
            }
            NarrativeCutscene c = NarrativeService.BeginReplay(s, _selected);
            if (c != null)
            {
                SetOpen(false);
                NarrativeCutscenePlayerUIToolkit.Instance.Show(c);
            }
        }

        public void MarkAllRead()
        {
            NarrativeService.MarkAllRead(CampaignSession.Current);
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
                GuidanceHooks.Raise(GuidanceHooks.NarrativeLibraryFirstOpen);
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

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            if (_selected != null && !NarrativeService.Has(s, _selected))
            {
                _selected = null; // 换了存档（资料库按存档）：选中项不在这份资料库里
            }
            int key = HashCode.Combine(NarrativeService.Revision, _tab, _act, _source, _selected, (int)GameText.Language, GameSettings.Revision,
                s != null ? s.GetHashCode() : 0);
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("narrative.library.title");
            _help.text = GameText.Get("narrative.library.help");
            _close.text = GameText.Get("narrative.library.close");
            int unread = NarrativeService.UnreadCount(s);
            _tabEntries.text = unread > 0 ? GameText.Format("narrative.library.tab.entries", unread) : GameText.Get("narrative.library.tab.entries_none");
            _tabRecap.text = GameText.Get("narrative.library.tab.recap");
            _tabEntries.EnableInClassList("bb-selected", _tab == TabEntries);
            _tabRecap.EnableInClassList("bb-selected", _tab == TabRecap);
            _count.text = GameText.Format("narrative.library.count", NarrativeService.Count(s));
            for (int i = 0; i < _actButtons.Length; i++)
            {
                _actButtons[i].text = i == 0 ? GameText.Get("narrative.act.all") : NarrativeService.ActName(i);
                _actButtons[i].EnableInClassList("bb-selected", _act == i);
            }
            for (int i = 0; i < _sourceButtons.Length; i++)
            {
                string src = i == 0 ? null : NarrativeCatalog.Sources[i - 1];
                _sourceButtons[i].text = GameText.Get(NarrativeCatalog.SourceTextKey(src));
                _sourceButtons[i].EnableInClassList("bb-selected", _source == src);
            }
            bool entries = _tab == TabEntries;
            _actBar.EnableInClassList("uk-hidden", !entries);
            _sourceBar.EnableInClassList("uk-hidden", !entries);
            _markAll.text = GameText.Get("narrative.library.mark_all");
            _markAll.SetEnabled(unread > 0);
            _footer.text = GameText.Get("narrative.library.footer");

            if (entries)
            {
                NarrativeService.Query(s, _act, _source, _scratch);
            }
            else
            {
                NarrativeService.Recap(s, _scratch);
            }
            RebuildRows(entries);
            string empty = null;
            if (_scratch.Count == 0)
            {
                empty = !entries ? GameText.Get("narrative.library.empty_recap")
                    : NarrativeService.Count(s) == 0 ? GameText.Get("narrative.library.empty") : GameText.Get("narrative.library.empty_filter");
            }
            _empty.EnableInClassList("uk-hidden", empty == null);
            if (empty != null)
            {
                _empty.text = empty;
            }
            RenderDetail(s);
        }

        private void RebuildRows(bool entries)
        {
            while (_rows.Count < _scratch.Count)
            {
                var b = new Button();
                b.AddToClassList("nl-row");
                int index = _rows.Count;
                b.clicked += () => SelectRow(index);
                _list.Add(b);
                _rows.Add(b);
            }
            _rowIds.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < _scratch.Count;
                Button b = _rows[i];
                b.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                NarrativeEntryRecord r = _scratch[i];
                NarrativeEntryDef def = NarrativeCatalog.Find(r.Id);
                _rowIds.Add(r.Id);
                string title = NarrativeService.TitleOf(def);
                b.text = entries
                    ? (r.Read ? string.Empty : GameText.Get("narrative.library.unread_mark")) + GameText.Format("narrative.library.row", GameText.Format("narrative.library.source_tag", NarrativeService.SourceName(def?.Source)), title)
                    : GameText.Format("narrative.library.recap_row", r.Day, title);
                b.EnableInClassList("nl-row-unread", entries && !r.Read);
                b.EnableInClassList("nl-row-selected", r.Id == _selected);
            }
        }

        private void RenderDetail(CampaignState s)
        {
            float px = UiTuningValues.Get("narrative.library_font_px") * GameSettings.SubtitleScale;
            _detailBody.style.fontSize = px;
            NarrativeEntryRecord rec = _selected != null ? NarrativeService.Find(s, _selected) : null;
            NarrativeEntryDef def = rec != null ? NarrativeCatalog.Find(rec.Id) : null;
            if (def == null)
            {
                _detailTitle.text = string.Empty;
                _detailMeta.text = string.Empty;
                _detailBody.text = GameText.Get("narrative.library.detail_hint");
                _replay.EnableInClassList("uk-hidden", true);
                return;
            }
            _detailTitle.text = NarrativeService.TitleOf(def);
            _detailMeta.text = GameText.Format("narrative.library.meta", NarrativeService.SourceName(def.Source), NarrativeService.ActName(rec.Act), rec.Day);
            _detailBody.text = NarrativeService.BodyOf(def);
            _replay.text = GameText.Get("narrative.library.replay");
            _replay.EnableInClassList("uk-hidden", !def.IsCutscene);
        }
    }
}
