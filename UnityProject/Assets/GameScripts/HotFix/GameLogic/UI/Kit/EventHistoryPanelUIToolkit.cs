using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG12-EVT-01（FG10 第 10 节“事件历史面板：每个事件的时间、结果、奖励，可以点击定位”；FGR-EVT-004）：事件历史。
    /// 进行中的事件（预警中 / 剩余时间）在最前，之后是历史（最近的在前）：时间、事件、结果、奖励；点一行定位到事件地点（与通知定位同一执行者）。
    /// 入口：暂停菜单“事件历史”。Esc / 关闭按钮 / 点遮罩关闭；模态。刷新只在数据版本 / 语言 / 秒数变化时重建，O(显示条数)。
    /// </summary>
    public sealed class EventHistoryPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30079; // 与突袭历史同层：两者都是暂停菜单的子面板，不会同时打开

        public static EventHistoryPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<(string Region, Vector3 At)> _targets = new List<(string, Vector3)>();
        private int _key;
        private string _footerNote;

        protected override string UxmlLocation => "EventHistoryPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].text : string.Empty;
        public Button RowButton(int i) => i >= 0 && i < VisibleRowCount ? _rows[i] : null;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
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
            _root = root.Q<VisualElement>("EventHistoryRoot");
            _title = root.Q<Label>("EventHistoryTitle");
            _count = root.Q<Label>("EventHistoryCount");
            _close = root.Q<Button>("EventHistoryClose");
            _list = root.Q<ScrollView>("EventHistoryList");
            _empty = root.Q<Label>("EventHistoryEmpty");
            _footer = root.Q<Label>("EventHistoryFooter");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
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
                GuidanceHooks.Raise(GuidanceHooks.EventDirectorHistoryFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _footerNote = null;
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

        /// <summary>按事件版本 / 语言 / 当前游戏秒重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            long second = GameClock.Ticks / System.Math.Max(1, GameClock.StepHz);
            int key = System.HashCode.Combine(EventDirectorService.Revision, (int)GameText.Language, GameSettings.Revision, state != null ? state.GetHashCode() : 0,
                EventDirectorService.Active(state).Count > 0 ? second : 0, _footerNote);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("event_director.history.title");
            _close.text = GameText.Get("codex.panel.close");
            var lines = new List<(string Text, string Cls, string Region, Vector3 At)>();
            double hz = System.Math.Max(1, GameClock.StepHz);
            foreach (EventDirectorInstanceRecord r in EventDirectorService.Active(state))
            {
                string left = r.State == EventDirectorService.StateWarned
                    ? GameText.Format("event_director.history.ongoing_warned", CalendarService.ShortDuration(System.Math.Max(0, r.StartTick - GameClock.Ticks) / hz))
                    : GameText.Format("event_director.history.ongoing_active", CalendarService.ShortDuration(System.Math.Max(0, r.EndTick - GameClock.Ticks) / hz));
                lines.Add((GameText.Format("event_director.history.ongoing", EventDirectorService.Name(r.EventId), left), "rl-row-first", r.RegionId, new Vector3(r.X, 0f, r.Y)));
            }
            IReadOnlyList<EventDirectorHistoryRecord> hist = EventDirectorService.History(state);
            for (int i = hist.Count - 1; i >= 0; i--)
            {
                EventDirectorHistoryRecord h = hist[i];
                string when = GameText.Format("env.cal.day", GameClock.DayOf(h.WarnTick / hz)) + " " + GameClock.FormatHhMm(h.WarnTick / hz);
                string reward = h.PacketId.Length > 0 ? GameText.Get("event_director.reward.packet")
                    : h.RewardTech > 0 ? GameText.Format(string.IsNullOrEmpty(h.RewardFragment) ? "event_director.reward.tech_only" : "event_director.reward.fixed", h.RewardTech) // FG12-EVT-03 reward=fixed
                    : string.Empty;
                lines.Add((GameText.Format("event_director.history.row", when, EventDirectorService.Name(h.EventId), EventDirectorService.OutcomeText(h.Outcome), reward),
                    null, h.RegionId, new Vector3(h.X, 0f, h.Y)));
            }
            _count.text = GameText.Format("event_director.history.count", lines.Count);
            while (_rows.Count < lines.Count)
            {
                var b = new Button();
                b.AddToClassList("rl-row");
                int index = _rows.Count;
                b.clicked += () => LocateRow(index);
                _list.Add(b);
                _rows.Add(b);
            }
            _targets.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                Button b = _rows[i];
                bool shown = i < lines.Count;
                b.EnableInClassList("uk-hidden", !shown);
                b.RemoveFromClassList("rl-row-first");
                if (!shown)
                {
                    continue;
                }
                b.text = lines[i].Text;
                if (lines[i].Cls != null)
                {
                    b.AddToClassList(lines[i].Cls);
                }
                _targets.Add((lines[i].Region, lines[i].At));
            }
            VisibleRowCount = lines.Count;
            bool empty = lines.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("event_director.history.empty") : string.Empty;
            string footer = InputDisplay.ExpandActionTokens(GameText.Get("event_director.history.footer"));
            try
            {
                footer = string.Format(footer, EventDirectorService.FrequencyName(EventDirectorService.Frequency(state)),
                    Campaign.Defense.DifficultyService.EventRate(state).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (System.FormatException)
            {
                // 文本格式坏了：照原样显示（不抛）。
            }
            _footer.text = string.IsNullOrEmpty(_footerNote) ? footer : _footerNote + " " + footer;
        }

        /// <summary>点一行：定位到事件地点（通知的定位执行者；失败时在页脚说明原因）。</summary>
        public bool LocateRow(int index)
        {
            if (index < 0 || index >= _targets.Count)
            {
                return false;
            }
            (string region, Vector3 at) = _targets[index];
            string failureKey = "event_director.history.locate_failed";
            bool ok = NotificationCenter.LocateHandler != null && NotificationCenter.LocateHandler(region, at, out failureKey);
            LastLocateRegion = region;
            LastLocateAt = at;
            _footerNote = ok ? null : GameText.Get(string.IsNullOrEmpty(failureKey) ? "event_director.history.locate_failed" : failureKey);
            if (ok)
            {
                SetOpen(false);
            }
            Refresh();
            return ok;
        }

        public string LastLocateRegion { get; private set; }
        public Vector3 LastLocateAt { get; private set; }
    }
}
