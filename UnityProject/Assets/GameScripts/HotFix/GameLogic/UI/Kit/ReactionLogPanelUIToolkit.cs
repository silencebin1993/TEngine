using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-04（FG02 FGR-FW-043；卡片“伤害归因进入统计面板；反应日志可以按远征和突袭筛选”）：反应记录面板，三个页签——
    /// - 反应日志：最近 50 条（本次载入之后），每条写明时间、反应（×次数，首次触发标“首次”）、触发者（机器 / 炮塔 / 敌人）、参与的固件、目标；可按“全部 / 远征 / 突袭”筛选。
    /// - 伤害归因：最近若干场远征 / 突袭（存档），每场各反应的伤害占比与次数；同样按远征 / 突袭筛选。与统计面板（<see cref="StatsPanelUIToolkit"/>）同一份数据、同一种写法（<see cref="ReactionAttributionView"/>）；离家报告见 FG-GAP-055。
    /// - 反应图鉴：18 条具名反应，本存档里打出过的显示名字、配方说明与首次触发时间地点；没打出的是“？？？”并写明为什么（未打出 / 未开放命名 / 机械内容下不可达）。
    /// 入口：暂停菜单“反应记录”。Esc / 关闭按钮 / 点遮罩关闭；模态。刷新只在打开时、数据版本 / 页签 / 筛选 / 语言变化时重建，O(显示条数)。
    /// </summary>
    public sealed class ReactionLogPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30077;

        public enum Tab
        {
            Log = 0,
            Attribution = 1,
            Codex = 2,
        }

        public static ReactionLogPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private readonly Button[] _tabs = new Button[3];
        private readonly Button[] _filters = new Button[3];
        private VisualElement _filterBar;
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private readonly List<Label> _rows = new List<Label>();
        private int _key;

        public Tab CurrentTab { get; private set; } = Tab.Log;
        public ReactionLogFilter CurrentFilter { get; private set; } = ReactionLogFilter.All;

        protected override string UxmlLocation => "ReactionLogPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount && i < _rows.Count ? _rows[i].text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public Button TabButton(Tab t) => _tabs[(int)t];
        public Button FilterButton(ReactionLogFilter f) => _filters[(int)f];
        public Button CloseButton => _close;
        public bool FilterBarVisible => _filterBar != null && !_filterBar.ClassListContains("uk-hidden");

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

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

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("ReactionLogRoot");
            _title = root.Q<Label>("ReactionLogTitle");
            _count = root.Q<Label>("ReactionLogCount");
            _close = root.Q<Button>("ReactionLogClose");
            _tabs[(int)Tab.Log] = root.Q<Button>("ReactionTabLog");
            _tabs[(int)Tab.Attribution] = root.Q<Button>("ReactionTabAttr");
            _tabs[(int)Tab.Codex] = root.Q<Button>("ReactionTabCodex");
            _filterBar = root.Q<VisualElement>("ReactionFilterBar");
            _filters[(int)ReactionLogFilter.All] = root.Q<Button>("ReactionFilterAll");
            _filters[(int)ReactionLogFilter.Expedition] = root.Q<Button>("ReactionFilterExpedition");
            _filters[(int)ReactionLogFilter.Raid] = root.Q<Button>("ReactionFilterRaid");
            _list = root.Q<ScrollView>("ReactionLogList");
            _empty = root.Q<Label>("ReactionLogEmpty");
            _footer = root.Q<Label>("ReactionLogFooter");
            _close.clicked += () => SetOpen(false);
            for (int t = 0; t < _tabs.Length; t++)
            {
                Tab captured = (Tab)t;
                _tabs[t].clicked += () => SelectTab(captured);
            }
            for (int f = 0; f < _filters.Length; f++)
            {
                ReactionLogFilter captured = (ReactionLogFilter)f;
                _filters[f].clicked += () => SelectFilter(captured);
            }
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        public void SelectTab(Tab tab)
        {
            CurrentTab = tab;
            _key = 0;
            Refresh();
        }

        public void SelectFilter(ReactionLogFilter filter)
        {
            CurrentFilter = filter;
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
                GuidanceHooks.Raise(GuidanceHooks.ReactionLogFirstOpen);
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
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh();
        }

        /// <summary>按当前数据版本 / 页签 / 筛选 / 语言重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int key = System.HashCode.Combine(ReactionLog.Revision, ReactionAttribution.Revision, ReactionFeedback.Revision, (int)CurrentTab, (int)CurrentFilter,
                (int)GameText.Language, GameSettings.Revision, state != null ? state.GetHashCode() : 0);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("reaction.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            _tabs[(int)Tab.Log].text = GameText.Get("reaction.panel.tab_log");
            _tabs[(int)Tab.Attribution].text = GameText.Get("reaction.panel.tab_attr");
            _tabs[(int)Tab.Codex].text = GameText.Get("reaction.panel.tab_codex");
            _filters[(int)ReactionLogFilter.All].text = GameText.Get("reaction.panel.filter_all");
            _filters[(int)ReactionLogFilter.Expedition].text = GameText.Get("reaction.panel.filter_expedition");
            _filters[(int)ReactionLogFilter.Raid].text = GameText.Get("reaction.panel.filter_raid");
            for (int t = 0; t < _tabs.Length; t++)
            {
                _tabs[t].EnableInClassList("rl-selected", t == (int)CurrentTab);
            }
            for (int f = 0; f < _filters.Length; f++)
            {
                _filters[f].EnableInClassList("rl-selected", f == (int)CurrentFilter);
            }
            _filterBar.EnableInClassList("uk-hidden", CurrentTab == Tab.Codex);

            var lines = new List<(string Text, string Cls)>();
            string emptyKey;
            switch (CurrentTab)
            {
                case Tab.Attribution:
                    ReactionAttributionView.AppendSessions(state, CurrentFilter, lines);
                    emptyKey = "reaction.panel.empty_attr";
                    _footer.text = Hint("reaction.panel.attr_hint");
                    break;
                case Tab.Codex:
                    int unlocked = BuildCodex(state, lines, out int total);
                    emptyKey = null;
                    _footer.text = Hint("reaction.panel.codex_hint", unlocked, total);
                    break;
                default:
                    foreach (ReactionLogEntry e in ReactionLog.Filtered(CurrentFilter, state))
                    {
                        lines.Add((ReactionLog.Describe(state, e), e.First ? "rl-row-first" : e.Named ? null : "rl-row-dim"));
                    }
                    emptyKey = "reaction.panel.empty_log";
                    _footer.text = Hint("reaction.panel.log_hint", ReactionLog.Capacity);
                    break;
            }
            _count.text = GameText.Format("reaction.panel.count", lines.Count);
            while (_rows.Count < lines.Count)
            {
                var l = new Label();
                l.AddToClassList("rl-row");
                _list.Add(l);
                _rows.Add(l);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                Label l = _rows[i];
                bool shown = i < lines.Count;
                l.EnableInClassList("uk-hidden", !shown);
                l.RemoveFromClassList("rl-row-first");
                l.RemoveFromClassList("rl-row-dim");
                l.RemoveFromClassList("rl-row-title");
                if (!shown)
                {
                    continue;
                }
                l.text = lines[i].Text;
                if (lines[i].Cls != null)
                {
                    l.AddToClassList(lines[i].Cls);
                }
            }
            VisibleRowCount = lines.Count;
            bool empty = lines.Count == 0 && emptyKey != null;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get(emptyKey) : string.Empty;
        }

        private static int BuildCodex(CampaignState state, List<(string, string)> lines, out int total)
        {
            int unlocked = 0;
            total = 0;
            foreach (Reaction row in NamedReactionCatalog.Rows)
            {
                if (row == null)
                {
                    continue;
                }
                total++;
                ReactionFirstTriggerRecord first = ReactionFeedback.FirstRecordOf(state, row.Id);
                if (first != null)
                {
                    unlocked++;
                    lines.Add((GameText.Get(row.NameKey), "rl-row-title"));
                    lines.Add(("　" + GameText.Get(row.DescKey), null));
                    lines.Add(("　" + GameText.Format("reaction.codex.first", TickText(first.Tick), GameLogic.Campaign.Combat.CombatSites.SiteName(first.SiteId)), "rl-row-dim"));
                    continue;
                }
                lines.Add((GameText.Get("reaction.codex.locked_name"), "rl-row-dim"));
                string why = row.Reach != NamedReactionCatalog.Reachable
                    ? GameText.Get("reaction.codex.unreachable")
                    : !NamedReactionCatalog.IsBatchOpen(state, row.Batch)
                        ? GameText.Format("reaction.codex.closed", GameText.Get(BatchNameKey(row.Batch)))
                        : GameText.Get("reaction.codex.locked");
                lines.Add(("　" + why, "rl-row-dim"));
            }
            return unlocked;
        }

        /// <summary>开放批次的名字键（reaction.batch.act1 / clarity / overclock / cross，fgdata_reaction.py）。</summary>
        private static string BatchNameKey(string batch) => "reaction.batch." + batch;

        private static string TickText(long tick) => ReactionAttributionView.TickText(tick);

        /// <summary>脚注：先把 {act:动作名} 换成当前按键，再填数字占位（按键文字里不会有花括号）。</summary>
        private static string Hint(string key, params object[] args)
        {
            string pattern = InputDisplay.ExpandActionTokens(GameText.Get(key));
            if (args == null || args.Length == 0)
            {
                return pattern;
            }
            try
            {
                return string.Format(pattern, args);
            }
            catch (System.FormatException)
            {
                return pattern;
            }
        }
    }
}
