using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Platform;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-SYS-04（FGR-SYS-080 / 081；FGT-SYS-007 / FGT-SBX-005）：成就面板。主菜单“成就”与暂停菜单“成就”打开。
    /// 按分类分组列出全部成就：几何占位图标（已解锁亮、未解锁暗）、名称、条件、状态（解锁时间 / 等待同步 / 进度 x / N / 沙盒存档不计入）；
    /// 顶部写已解锁数、在线 / 离线与待同步数，以及沙盒规则说明（新游戏面板 sandbox.newgame.note 之外这里再写一次）。
    /// 主菜单里没有战役：只显示解锁与否，不显示进度。刷新只在成就版本 / 语言 / 战役 / 游戏分钟变化时重建，O(成就数)。Esc / 关闭 / 点遮罩关闭；模态。
    /// </summary>
    public sealed class AchievementPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30087; // 主菜单存档面板 30086 之上（两者不会同时打开），暂停菜单之上

        public static AchievementPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _status;
        private Label _sync;
        private Label _sandbox;
        private Button _close;
        private ScrollView _list;
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private readonly List<Label> _groups = new List<Label>();
        private int _key;

        protected override string UxmlLocation => "AchievementPanel";
        protected override int SortingOrder => Order;

        // ── 自检 / 冒烟读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string StatusText => _status?.text ?? string.Empty;
        public string SyncText => _sync?.text ?? string.Empty;
        public string SandboxNoteText => _sandbox?.text ?? string.Empty;
        public int RowCount { get; private set; }
        public string RowName(int i) => i >= 0 && i < RowCount ? _rows[i].Q<Label>(className: "ach-name").text : string.Empty;
        public string RowState(int i) => i >= 0 && i < RowCount ? _rows[i].Q<Label>(className: "ach-state").text : string.Empty;
        public bool RowUnlocked(int i) => i >= 0 && i < RowCount && _rows[i].ClassListContains("ach-row-unlocked");
        public string RowIconClasses(int i) => i >= 0 && i < RowCount ? string.Join(" ", _rows[i].Q<VisualElement>(className: "ach-icon").GetClasses()) : string.Empty;
        public Button CloseButton => _close;

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
            _root = root.Q<VisualElement>("AchievementRoot");
            _title = root.Q<Label>("AchievementTitle");
            _status = root.Q<Label>("AchievementStatus");
            _sync = root.Q<Label>("AchievementSync");
            _sandbox = root.Q<Label>("AchievementSandboxNote");
            _close = root.Q<Button>("AchievementClose");
            _list = root.Q<ScrollView>("AchievementList");
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                AchievementService.SyncPending(); // 打开时顺带试一次补推（离线解锁的成就）
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
            if (IsOpen)
            {
                Refresh();
            }
        }

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = Stage.GameRoot.AnyRegionActive ? CampaignSession.Current : null;
            long minute = GameClock.Ticks / System.Math.Max(1L, GameClock.TicksFor(60));
            int key = System.HashCode.Combine(AchievementService.Revision, (int)GameText.Language, s != null ? s.GetHashCode() : 0, minute,
                AchievementService.Provider != null && AchievementService.Provider.Available);
            if (key == _key)
            {
                return;
            }
            _key = key;
            IReadOnlyList<Achievement> rows = AchievementService.Rows;
            _title.text = GameText.Get("achievement.panel.title");
            _close.text = GameText.Get("achievement.panel.close");
            _status.text = GameText.Format("achievement.panel.status", AchievementService.UnlockedTotal, rows.Count);
            int pending = AchievementService.PendingSyncCount;
            bool online = AchievementService.Provider != null && AchievementService.Provider.Available;
            _sync.text = !online ? GameText.Get("achievement.panel.offline")
                : pending > 0 ? GameText.Format("achievement.panel.pending", pending) : GameText.Get("achievement.panel.online");
            _sandbox.text = GameText.Get("achievement.panel.sandbox_note");

            _list.Clear();
            _rows.Clear();
            _groups.Clear();
            string group = null;
            foreach (Achievement a in rows)
            {
                if (a.Category != group)
                {
                    group = a.Category;
                    var g = new Label(GameText.Get(CategoryKey(group)));
                    g.AddToClassList("ach-group");
                    _list.Add(g);
                    _groups.Add(g);
                }
                _list.Add(BuildRow(a, s));
            }
            RowCount = _rows.Count;
        }

        private VisualElement BuildRow(Achievement a, CampaignState s)
        {
            bool unlocked = AchievementService.IsUnlocked(a.Id);
            var row = new VisualElement();
            row.AddToClassList("ach-row");
            row.AddToClassList(unlocked ? "ach-row-unlocked" : "ach-row-locked");
            var icon = new VisualElement();
            icon.AddToClassList("ach-icon");
            string[] parts = (a.Icon ?? "circle:silver").Split(':');
            icon.AddToClassList("ach-shape-" + parts[0]);
            icon.AddToClassList("ach-color-" + (parts.Length > 1 ? parts[1] : "silver"));
            row.Add(icon);
            var text = new VisualElement();
            text.AddToClassList("ach-text");
            string name = GameText.Get(a.NameKey);
            if (a.Story)
            {
                name += " · " + GameText.Get("achievement.row.story");
            }
            text.Add(Make(name, "ach-name"));
            text.Add(Make(GameText.Get(a.DescKey), "ach-desc"));
            text.Add(Make(StateLine(a, s, unlocked), "ach-state"));
            row.Add(text);
            _rows.Add(row);
            return row;
        }

        /// <summary>状态行：已解锁（时间，等待同步）/ 沙盒存档不计入 / 进度 x / N（计数类且有战役时）/ 未解锁。</summary>
        public static string StateLine(Achievement a, CampaignState s, bool unlocked)
        {
            if (unlocked)
            {
                AchievementLedgerEntry e = AchievementService.Entry(a.Id);
                string when = e != null && System.DateTime.TryParse(e.UnlockedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out System.DateTime t)
                    ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
                string line = GameText.Format("achievement.row.unlocked", when);
                return e != null && !e.Synced ? line + " · " + GameText.Get("achievement.row.pending_sync") : line;
            }
            if (s != null && !AchievementService.Counts(a, s))
            {
                return GameText.Get("achievement.row.sandbox_blocked");
            }
            if (s != null && a.Threshold > 1)
            {
                int cur = AchievementService.Progress(a, s, out int target);
                return GameText.Format("achievement.row.progress", System.Math.Min(cur, target), target);
            }
            return GameText.Get("achievement.row.locked");
        }

        private static string CategoryKey(string category) => category switch
        {
            "story" => "achievement.category.story",
            "system" => "achievement.category.system",
            "economy" => "achievement.category.economy",
            "challenge" => "achievement.category.challenge",
            _ => "achievement.category.postgame",
        };

        private static Label Make(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }
    }
}
