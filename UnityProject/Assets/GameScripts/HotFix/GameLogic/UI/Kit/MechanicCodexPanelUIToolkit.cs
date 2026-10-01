using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG1-HUD-01（FG13 FGR-UX-050“系统说明”页签、FGR-UX-051；FG01 第 4 章图鉴条目；FGU-38 的首批）：机制图鉴面板。
    /// FG2-FW-05（FGU-38 固件 / 反应页签；FGR-UX-051 搜索、互链、剪影 + 获取途径；FGR-UX-030 从悬停提示按键跳转）：
    /// - 页签：系统说明 / 固件（44 条）/ 反应；搜索框按当前页签过滤（已解锁的按标题与正文，未解锁的只按获取途径，不剧透名字）；
    /// - 左侧：当前页签的条目（未解锁显示“？？？”）；右侧：图标（固件；未获得 = 同一张图标着黑色的剪影）、标题、正文、相关条目（点击跳转，可跨页签）；
    /// - 入口：接入 HUD / 暴露面板的“?”、暂停菜单“图鉴”、图鉴键（默认 C）、悬停带图鉴链接的提示时按图鉴键（<see cref="CodexHoverLink"/>）——都经 <see cref="MechanicCodex.Open"/>；
    /// - 打开时按当前战役补解锁（<see cref="Campaign.Signal.FirmwareLibrary.SyncCodex"/>：获得过的固件、本存档打出过的反应）；
    /// - 状态：正常 / 空（全是剪影 + 获取途径）/ 搜索无结果 / 错误（配置表缺失：写明原因）。Esc / 关闭按钮 / 点遮罩 / 再按图鉴键关闭；模态（输入上下文 = 界面）。
    /// - 分层：暂停菜单 30070 之上、按键面板 30080 之下。
    /// 刷新：只在打开时、条目解锁版本 / 页签 / 搜索 / 选中 / 语言 / 键位变化时重建，O(条目数)。
    /// </summary>
    public sealed class MechanicCodexPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30075;

        public static MechanicCodexPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static string _pendingOpenId;
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Label _error;
        private ScrollView _list;
        private Label _noMatch;
        private VisualElement _entryIcon;
        private Label _entryTitle;
        private Label _entryBody;
        private Label _relatedTitle;
        private VisualElement _related;
        private Label _footer;
        private UiTabs _tabs;
        private UiSearchBox _search;
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly List<Button> _itemButtons = new List<Button>();
        private readonly List<string> _itemIds = new List<string>();
        private readonly List<MechanicCodexEntry> _scratch = new List<MechanicCodexEntry>();
        private string _selectedId;
        private string _tab = MechanicCodex.TabSystem;
        private string _searchText = string.Empty;
        private int _key;

        protected override string UxmlLocation => "MechanicCodexPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string SelectedId => _selectedId;
        public string CurrentTab => _tab;
        public string EntryTitleText => _entryTitle?.text ?? string.Empty;
        public string EntryBodyText => _entryBody?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public bool ErrorVisible => _error != null && !_error.ClassListContains("uk-hidden");
        public bool NoMatchVisible => _noMatch != null && !_noMatch.ClassListContains("uk-hidden");
        public bool EntryIconSilhouette => _entryIcon != null && _entryIcon.ClassListContains("cx-silhouette");
        public string EntryIconId => _entryIcon?.userData as string;
        public bool EntryIconHidden => _entryIcon == null || _entryIcon.ClassListContains("uk-hidden");
        public int ItemCount => _itemIds.Count;
        public string ItemText(int i) => i >= 0 && i < _itemIds.Count ? _itemButtons[i].text : string.Empty;
        public string ItemId(int i) => i >= 0 && i < _itemIds.Count ? _itemIds[i] : null;
        public Button ItemButton(int i) => i >= 0 && i < _itemButtons.Count ? _itemButtons[i] : null;
        public int RelatedCount => _related?.childCount ?? 0;
        public Button RelatedButton(int i) => _related != null && i >= 0 && i < _related.childCount ? _related[i] as Button : null;
        public Button CloseButton => _close;
        public Button TabButton(int i) => i >= 0 && i < _tabButtons.Count ? _tabButtons[i] : null;
        public string TabText(int i) => TabButton(i)?.text ?? string.Empty;

        private void Awake()
        {
            Instance = this;
            MechanicCodex.OpenRequested -= OnOpenRequested;
            MechanicCodex.OpenRequested += OnOpenRequested;
        }

        protected override void OnDestroy()
        {
            MechanicCodex.OpenRequested -= OnOpenRequested;
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

        private static void OnOpenRequested(string id)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingOpenId = id;
                return;
            }
            Instance.OpenAt(id);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        /// <summary>图鉴键：开着就关，关着就打开（有悬停条目时由 <see cref="CodexHoverLink.TryJump"/> 先处理）。</summary>
        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }
            MechanicCodex.Open(Instance?._selectedId, unlock: false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                OpenAt(_pendingOpenId);
            }
        }

        /// <summary>绑定 UXML（运行时与自检共用）。</summary>
        public void BindView(VisualElement root)
        {
            // 自检在编辑模式挂组件时不走 Awake：绑定时同样登记实例与“打开”请求（取消订阅再订阅，重复绑定不重复触发）。
            Instance = this;
            MechanicCodex.OpenRequested -= OnOpenRequested;
            MechanicCodex.OpenRequested += OnOpenRequested;
            _root = root.Q<VisualElement>("CodexRoot");
            _title = root.Q<Label>("CodexTitle");
            _count = root.Q<Label>("CodexCount");
            _close = root.Q<Button>("CodexClose");
            _error = root.Q<Label>("CodexError");
            _list = root.Q<ScrollView>("CodexList");
            _noMatch = root.Q<Label>("CodexNoMatch");
            _entryIcon = root.Q<VisualElement>("CodexEntryIcon");
            _entryTitle = root.Q<Label>("CodexEntryTitle");
            _entryBody = root.Q<Label>("CodexEntryBody");
            _relatedTitle = root.Q<Label>("CodexRelatedTitle");
            _related = root.Q<VisualElement>("CodexRelated");
            _footer = root.Q<Label>("CodexFooter");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false); // 点遮罩 = 关闭（与暂停菜单一致）。
                }
            });
            _tabButtons.Clear();
            _tabButtons.Add(root.Q<Button>("CodexTabSystem"));
            _tabButtons.Add(root.Q<Button>("CodexTabFirmware"));
            _tabButtons.Add(root.Q<Button>("CodexTabReaction"));
            _tabButtons.Add(root.Q<Button>("CodexTabItem")); // FG4-ECO-01
            _tabButtons.Add(root.Q<Button>("CodexTabRecipe"));
            _tabs = new UiTabs(_tabButtons, i => SelectTab(MechanicCodex.Tabs[i]));
            _search = new UiSearchBox(root.Q<TextField>("CodexSearch"), root.Q<Label>("CodexSearchPlaceholder"), root.Q<Button>("CodexSearchClear"),
                "codex.panel.search", SetSearch);
            _key = 0;
        }

        /// <summary>打开并选中 <paramref name="id"/>（切到它所在的页签；为空时选当前页签第一条已解锁的，没有就第一条）。</summary>
        public void OpenAt(string id)
        {
            if (_root == null)
            {
                return;
            }
            Campaign.Signal.FirmwareLibrary.SyncCodex(CampaignSession.Current);
            MechanicCodexEntry target = MechanicCodex.Find(id);
            if (target != null)
            {
                _tab = target.Tab;
                if (_searchText.Length > 0)
                {
                    _search?.SetText(string.Empty); // 跳到指定条目时清掉搜索，保证它在列表里
                }
                _selectedId = id;
            }
            else
            {
                _selectedId = FirstUnlockedOrFirst(_tab);
            }
            SetOpen(true);
            _key = 0;
            Refresh();
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        public void SelectTab(string tab)
        {
            if (System.Array.IndexOf(MechanicCodex.Tabs, tab) < 0 || tab == _tab && _selectedId != null && MechanicCodex.TabOf(_selectedId) == tab)
            {
                return;
            }
            _tab = tab;
            _selectedId = FirstUnlockedOrFirst(tab);
            _key = 0;
            Refresh();
        }

        public void SetSearch(string text)
        {
            _searchText = text ?? string.Empty;
            _key = 0;
            Refresh();
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            // 图鉴属于游戏世界里的界面：世界没了（回主菜单）就收起，不带着模态残留。
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh();
        }

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        /// <summary>按当前解锁状态 / 页签 / 搜索 / 选中 / 语言重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            int key = System.HashCode.Combine(MechanicCodex.Revision, _selectedId, _tab, _searchText, (int)GameText.Language, GameSettings.Revision);
            if (key == _key)
            {
                return;
            }
            _key = key;
            CampaignState state = CampaignSession.Current;
            _title.text = GameText.Get("codex.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            // FG1-HUD-01 修复轮（FG00 B02）：脚注 / 正文里的 {act:动作名} 换成当前绑定的按键，改键后跟着变（GameSettings.Revision 在键里）。
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("codex.panel.footer"));
            for (int i = 0; i < _tabButtons.Count && i < MechanicCodex.Tabs.Length; i++)
            {
                string t = MechanicCodex.Tabs[i];
                _tabButtons[i].text = GameText.Get(TabTextKey(t)) + "  " + MechanicCodex.UnlockedCountIn(t) + "/" + MechanicCodex.CountIn(t);
                _tabButtons[i].EnableInClassList("uk-tab-selected", t == _tab);
            }
            string error = MechanicCodex.LoadError;
            _error.EnableInClassList("uk-hidden", error == null);
            _error.text = error == null ? string.Empty : GameText.Get("codex.panel.error") + "（" + error + "）";
            _count.text = GameText.Format("codex.panel.list_title", MechanicCodex.UnlockedCountIn(_tab), MechanicCodex.CountIn(_tab));

            MechanicCodex.EntriesIn(_tab, _searchText, _scratch, state);
            // 条目按钮池：按条数对账（只增不减，多余的隐藏）。
            while (_itemButtons.Count < _scratch.Count)
            {
                int index = _itemButtons.Count;
                var b = new Button { name = "CodexItem" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("cx-item");
                b.clicked += () =>
                {
                    if (index < _itemIds.Count)
                    {
                        Select(_itemIds[index]);
                    }
                };
                _list.Add(b);
                _itemButtons.Add(b);
            }
            _itemIds.Clear();
            for (int i = 0; i < _itemButtons.Count; i++)
            {
                Button b = _itemButtons[i];
                bool shown = i < _scratch.Count;
                b.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                MechanicCodexEntry e = _scratch[i];
                _itemIds.Add(e.Id);
                bool unlocked = MechanicCodex.IsUnlocked(e.Id);
                b.text = unlocked ? MechanicCodex.Title(e) : GameText.Get("codex.panel.locked_title");
                b.EnableInClassList("cx-item-locked", !unlocked);
                b.EnableInClassList("cx-item-selected", e.Id == _selectedId);
            }
            bool noMatch = _scratch.Count == 0 && _searchText.Trim().Length > 0;
            _noMatch.EnableInClassList("uk-hidden", !noMatch);
            _noMatch.text = noMatch ? GameText.Get("codex.panel.no_match") : string.Empty;

            MechanicCodexEntry sel = MechanicCodex.Find(_selectedId);
            _related.Clear();
            string icon = MechanicCodex.IconOf(sel);
            _entryIcon.EnableInClassList("uk-hidden", icon == null);
            ContentIcons.ApplyIcon(_entryIcon, icon);
            if (sel == null)
            {
                _entryIcon.EnableInClassList("cx-silhouette", false);
                _entryTitle.text = GameText.Get("codex.panel.none_selected");
                _entryBody.text = string.Empty;
                _relatedTitle.text = string.Empty;
                return;
            }
            bool open = MechanicCodex.IsUnlocked(sel.Id);
            _entryIcon.EnableInClassList("cx-silhouette", !open);
            _entryTitle.text = open ? MechanicCodex.Title(sel) : GameText.Get("codex.panel.locked_title");
            _entryBody.text = InputDisplay.ExpandActionTokens(open ? MechanicCodex.Body(sel, state) : MechanicCodex.Hint(sel, state));
            _relatedTitle.text = open && sel.Links.Length > 0 ? GameText.Get("codex.panel.related") : string.Empty;
            if (!open)
            {
                return; // 未解锁的条目不剧透它链接到哪些机制。
            }
            foreach (string link in sel.Links)
            {
                MechanicCodexEntry target = MechanicCodex.Find(link);
                if (target == null)
                {
                    continue;
                }
                string captured = link;
                var b = new Button { text = MechanicCodex.IsUnlocked(link) ? MechanicCodex.Title(target) : GameText.Get("codex.panel.locked_title") };
                b.AddToClassList("mw-btn");
                b.AddToClassList("cx-link");
                b.clicked += () => Select(captured);
                _related.Add(b);
            }
        }

        /// <summary>页签名的文本键（写全键名，文本键扫描能核对到）。</summary>
        private static string TabTextKey(string tab) => tab switch
        {
            MechanicCodex.TabFirmware => "codex.tab.firmware",
            MechanicCodex.TabReaction => "codex.tab.reaction",
            MechanicCodex.TabItem => "codex.tab.item",
            MechanicCodex.TabRecipe => "codex.tab.recipe",
            _ => "codex.tab.system",
        };

        /// <summary>选中一条（可以跨页签：相关条目跳到另一页签时同时切换页签并清掉搜索）。</summary>
        public void Select(string id)
        {
            MechanicCodexEntry e = MechanicCodex.Find(id);
            if (e == null)
            {
                return;
            }
            if (e.Tab != _tab)
            {
                _tab = e.Tab;
                if (_searchText.Length > 0)
                {
                    _search?.SetText(string.Empty);
                }
            }
            _selectedId = id;
            Refresh();
        }

        private string FirstUnlockedOrFirst(string tab)
        {
            MechanicCodex.EntriesIn(tab, _searchText, _scratch, CampaignSession.Current);
            foreach (MechanicCodexEntry e in _scratch)
            {
                if (MechanicCodex.IsUnlocked(e.Id))
                {
                    return e.Id;
                }
            }
            return _scratch.Count > 0 ? _scratch[0].Id : null;
        }
    }
}
