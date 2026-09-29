using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG2-FW-04（FG02 FGR-FW-043“伤害归因……进入统计面板”；卡片“必须同时交付：伤害归因进入统计面板”；FG00 B19）：统计面板。
    /// 当前一段“战斗 · 反应伤害归因”（数据 = 存档里的 <see cref="StatsState.ReactionSessions"/>，最近 reaction.attribution_sessions 场）：
    /// - 累计：符合筛选的场次按反应合计（<see cref="ReactionAttribution.Aggregate"/>）——敌方共受伤害、反应额外伤害占比、逐条反应的合计占比与次数；
    /// - 每场明细：与反应记录面板“伤害归因”页签同一写法（<see cref="ReactionAttributionView"/>）；
    /// - 可按“全部 / 远征 / 突袭”筛选；没有场次时有空状态说明（怎样才会有数据）。
    /// FG4-ECO-08 在同一面板加生产统计段（四个时间窗口、效率、瓶颈），离家报告（FG4-ECO-09 / FG6-DEF-08）见 FG-GAP-055。
    /// 入口：暂停菜单“统计”。Esc / 关闭按钮 / 点遮罩关闭；模态，盖在暂停菜单上，关掉回到暂停菜单。
    /// 刷新只在打开时、数据版本 / 筛选 / 语言变化时重建，O(场次 × 每场反应条数)；不打开时每帧 O(1)。
    /// </summary>
    public sealed class StatsPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30076;

        public static StatsPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Label _section;
        private Button _close;
        private readonly Button[] _filters = new Button[3];
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private readonly List<Label> _rows = new List<Label>();
        private int _key;

        public ReactionLogFilter CurrentFilter { get; private set; } = ReactionLogFilter.All;

        protected override string UxmlLocation => "StatsPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount && i < _rows.Count ? _rows[i].text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string SectionText => _section?.text ?? string.Empty;
        public Button FilterButton(ReactionLogFilter f) => _filters[(int)f];
        public Button CloseButton => _close;

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
            _root = root.Q<VisualElement>("StatsPanelRoot");
            _title = root.Q<Label>("StatsPanelTitle");
            _count = root.Q<Label>("StatsPanelCount");
            _close = root.Q<Button>("StatsPanelClose");
            _section = root.Q<Label>("StatsSectionCombat");
            _filters[(int)ReactionLogFilter.All] = root.Q<Button>("StatsFilterAll");
            _filters[(int)ReactionLogFilter.Expedition] = root.Q<Button>("StatsFilterExpedition");
            _filters[(int)ReactionLogFilter.Raid] = root.Q<Button>("StatsFilterRaid");
            _list = root.Q<ScrollView>("StatsList");
            _empty = root.Q<Label>("StatsEmpty");
            _footer = root.Q<Label>("StatsFooter");
            _close.clicked += () => SetOpen(false);
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
                GuidanceHooks.Raise(GuidanceHooks.StatsPanelFirstOpen);
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

        /// <summary>按当前数据版本 / 筛选 / 语言重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int key = System.HashCode.Combine(ReactionAttribution.Revision, ReactionFeedback.Revision, (int)CurrentFilter, (int)GameText.Language, GameSettings.Revision,
                state != null ? state.GetHashCode() : 0);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("stats.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            _section.text = GameText.Get("stats.panel.section_combat");
            _filters[(int)ReactionLogFilter.All].text = GameText.Get("reaction.panel.filter_all");
            _filters[(int)ReactionLogFilter.Expedition].text = GameText.Get("reaction.panel.filter_expedition");
            _filters[(int)ReactionLogFilter.Raid].text = GameText.Get("reaction.panel.filter_raid");
            for (int f = 0; f < _filters.Length; f++)
            {
                _filters[f].EnableInClassList("rl-selected", f == (int)CurrentFilter);
            }

            var lines = new List<(string Text, string Cls)>();
            int sessions = ReactionAttributionView.AppendTotals(state, CurrentFilter, lines);
            if (sessions > 0)
            {
                lines.Add((GameText.Get("stats.panel.sessions_title"), "rl-row-title"));
                ReactionAttributionView.AppendSessions(state, CurrentFilter, lines);
            }
            _count.text = GameText.Format("stats.panel.count", sessions);
            _footer.text = Hint("stats.panel.hint", ReactionAttribution.SessionCapacity);
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
            bool empty = lines.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("stats.panel.empty") : string.Empty;
        }

        /// <summary>脚注：先把 {act:动作名} 换成当前按键，再填数字占位（按键文字里不会有花括号）。</summary>
        private static string Hint(string key, params object[] args)
        {
            string pattern = InputDisplay.ExpandActionTokens(GameText.Get(key));
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
