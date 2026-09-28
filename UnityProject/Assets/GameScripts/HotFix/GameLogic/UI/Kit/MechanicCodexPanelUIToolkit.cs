using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG1-HUD-01（FG13 FGR-UX-050“系统说明”页签、FGR-UX-051；FG01 第 4 章图鉴条目；FGU-38 的首批）：机制图鉴面板。
    /// - 左侧：全部机制条目（按表的 sortOrder）；未解锁显示“？？？”（剪影），点开看获取途径。右侧：标题、正文、相关条目（点击跳转）。
    /// - 入口：接入 HUD 的“?”（信号接入）、暴露面板的“?”（信号暴露）、暂停菜单“图鉴”——都经 <see cref="MechanicCodex.Open"/>。
    /// - 状态：正常 / 空（一条都没解锁：全是剪影 + 获取途径）/ 错误（配置表缺失：写明原因）。Esc / 关闭按钮 / 点遮罩关闭；模态（输入上下文 = 界面）。
    /// - 分层：暂停菜单 30070 之上、按键面板 30080 之下（暂停菜单里点“图鉴”盖在它上面，关掉回到暂停菜单）。
    /// 刷新：只在打开时、条目解锁版本 / 选中 / 语言变化时重建，O(条目数)。
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
        private Label _entryTitle;
        private Label _entryBody;
        private Label _relatedTitle;
        private VisualElement _related;
        private Label _footer;
        private readonly List<Button> _itemButtons = new List<Button>();
        private readonly List<string> _itemIds = new List<string>();
        private string _selectedId;
        private int _key;

        protected override string UxmlLocation => "MechanicCodexPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string SelectedId => _selectedId;
        public string EntryTitleText => _entryTitle?.text ?? string.Empty;
        public string EntryBodyText => _entryBody?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public bool ErrorVisible => _error != null && !_error.ClassListContains("uk-hidden");
        public int ItemCount => _itemIds.Count;
        public string ItemText(int i) => i >= 0 && i < _itemButtons.Count ? _itemButtons[i].text : string.Empty;
        public string ItemId(int i) => i >= 0 && i < _itemIds.Count ? _itemIds[i] : null;
        public Button ItemButton(int i) => i >= 0 && i < _itemButtons.Count ? _itemButtons[i] : null;
        public int RelatedCount => _related?.childCount ?? 0;
        public Button RelatedButton(int i) => _related != null && i >= 0 && i < _related.childCount ? _related[i] as Button : null;
        public Button CloseButton => _close;

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
            _key = 0;
        }

        /// <summary>打开并选中 <paramref name="id"/>（为空时选第一条已解锁的，没有就第一条）。</summary>
        public void OpenAt(string id)
        {
            if (_root == null)
            {
                return;
            }
            _selectedId = MechanicCodex.Find(id) != null ? id : FirstUnlockedOrFirst();
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

        /// <summary>按当前解锁状态 / 选中 / 语言重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            int key = System.HashCode.Combine(MechanicCodex.Revision, _selectedId, (int)GameText.Language, GameSettings.Revision);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("codex.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            // FG1-HUD-01 修复轮（FG00 B02）：脚注 / 正文里的 {act:动作名} 换成当前绑定的按键，改键后跟着变（GameSettings.Revision 在键里）。
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("codex.panel.footer"));
            string error = MechanicCodex.LoadError;
            _error.EnableInClassList("uk-hidden", error == null);
            _error.text = error == null ? string.Empty : GameText.Get("codex.panel.error") + "（" + error + "）";
            IReadOnlyList<MechanicCodexEntry> entries = MechanicCodex.Entries;
            _count.text = GameText.Format("codex.panel.list_title", MechanicCodex.UnlockedCount, entries.Count);

            // 条目按钮池：按条目数对账（数量只随配置表变化）。
            while (_itemButtons.Count < entries.Count)
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
                bool shown = i < entries.Count;
                b.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                MechanicCodexEntry e = entries[i];
                _itemIds.Add(e.Id);
                bool unlocked = MechanicCodex.IsUnlocked(e.Id);
                b.text = unlocked ? GameText.Get(e.TitleKey) : GameText.Get("codex.panel.locked_title");
                b.EnableInClassList("cx-item-locked", !unlocked);
                b.EnableInClassList("cx-item-selected", e.Id == _selectedId);
            }

            MechanicCodexEntry sel = MechanicCodex.Find(_selectedId);
            _related.Clear();
            if (sel == null)
            {
                _entryTitle.text = GameText.Get("codex.panel.none_selected");
                _entryBody.text = string.Empty;
                _relatedTitle.text = string.Empty;
                return;
            }
            bool open = MechanicCodex.IsUnlocked(sel.Id);
            _entryTitle.text = open ? GameText.Get(sel.TitleKey) : GameText.Get("codex.panel.locked_title");
            _entryBody.text = InputDisplay.ExpandActionTokens(open ? GameText.Get(sel.BodyKey) : GameText.Format("codex.panel.locked_body", GameText.Get(sel.HintKey)));
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
                var b = new Button { text = MechanicCodex.IsUnlocked(link) ? GameText.Get(target.TitleKey) : GameText.Get("codex.panel.locked_title") };
                b.AddToClassList("mw-btn");
                b.AddToClassList("cx-link");
                b.clicked += () => Select(captured);
                _related.Add(b);
            }
        }

        public void Select(string id)
        {
            if (MechanicCodex.Find(id) == null)
            {
                return;
            }
            _selectedId = id;
            Refresh();
        }

        private static string FirstUnlockedOrFirst()
        {
            IReadOnlyList<MechanicCodexEntry> entries = MechanicCodex.Entries;
            foreach (MechanicCodexEntry e in entries)
            {
                if (MechanicCodex.IsUnlocked(e.Id))
                {
                    return e.Id;
                }
            }
            return entries.Count > 0 ? entries[0].Id : null;
        }
    }
}
