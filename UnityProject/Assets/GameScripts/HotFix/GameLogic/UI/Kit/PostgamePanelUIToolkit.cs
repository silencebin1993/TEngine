using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Endgame;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG14-SBX-01（FG12 第 3 节“后日谈开始时，简短说明接下来会发生什么”“清洗波次的历史记录可以查看”；FGR-SBX-003 坚守统计）：坚守记录。
    /// - 说明：进入后日谈那一步（<see cref="PostgameService.IntroEvent"/>）自动打开，写明周期清洗、单位上限与精英化、轨道坠落与最后一组遗产、坚守结束与读档；“知道了”关闭。
    /// - 记录：暂停菜单“坚守记录”（只在后日谈里显示）打开：坚守天数 / 清洗次数 / 撑过 / 最高等级、下一次的等级与倒计时、最佳单次防守、累计，清洗历史（最近的在前）；“说明”重看说明。
    /// Esc / 关闭按钮 / 点遮罩关闭；模态。刷新只在数据版本 / 语言 / 秒数变化时重建，O(显示条数)。
    /// </summary>
    public sealed class PostgamePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30079; // 与突袭历史 / 事件历史同层：都是暂停菜单的子面板，不会同时打开

        public static PostgamePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static bool IntroMode { get; private set; }
        private static bool _pendingOpen;
        private static bool _pendingIntro;

        private VisualElement _root;
        private Label _title;
        private Label _status;
        private Button _close;
        private Label _intro;
        private VisualElement _record;
        private Label _summary;
        private Label _next;
        private Label _best;
        private Label _totals;
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private Button _about;
        private Button _ok;
        private readonly List<Label> _rows = new List<Label>();
        private int _key;

        protected override string UxmlLocation => "PostgamePanel";
        protected override int SortingOrder => Order;

        // ── 自检 / 冒烟读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string IntroText => _intro != null && !_intro.ClassListContains("uk-hidden") ? _intro.text : string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string NextText => _next?.text ?? string.Empty;
        public string BestText => _best?.text ?? string.Empty;
        public string TotalsText => _totals?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].text : string.Empty;
        public Button CloseButton => _close;
        public Button AboutButton => _about;
        public Button OkButton => _ok;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private void Awake()
        {
            Instance = this;
            GameEvent.AddEventListener(PostgameService.IntroEvent, OpenIntro);
        }

        protected override void OnDestroy()
        {
            GameEvent.RemoveEventListener(PostgameService.IntroEvent, OpenIntro);
            if (IsOpen && Instance == this)
            {
                SetOpen(false, false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>暂停菜单“坚守记录”。</summary>
        public static void Open() => Show(false);

        /// <summary>进入后日谈时的说明（也可从记录里点“说明”重看）。</summary>
        public static void OpenIntro() => Show(true);

        private static void Show(bool intro)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingIntro = intro;
                return;
            }
            if (IsOpen)
            {
                Instance.SetMode(intro);
                return;
            }
            Instance.SetOpen(true, intro);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false, false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true, _pendingIntro);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("PostgameRoot");
            _title = root.Q<Label>("PostgameTitle");
            _status = root.Q<Label>("PostgameStatus");
            _close = root.Q<Button>("PostgameClose");
            _intro = root.Q<Label>("PostgameIntro");
            _record = root.Q<VisualElement>("PostgameRecord");
            _summary = root.Q<Label>("PostgameSummary");
            _next = root.Q<Label>("PostgameNext");
            _best = root.Q<Label>("PostgameBest");
            _totals = root.Q<Label>("PostgameTotals");
            _list = root.Q<ScrollView>("PostgameList");
            _empty = root.Q<Label>("PostgameEmpty");
            _footer = root.Q<Label>("PostgameFooter");
            _about = root.Q<Button>("PostgameAbout");
            _ok = root.Q<Button>("PostgameOk");
            _close.clicked += () => SetOpen(false, false);
            _ok.clicked += () => SetOpen(false, false);
            _about.clicked += () => SetMode(true);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false, false);
                }
            });
            _key = 0;
        }

        public void SetOpen(bool open, bool intro)
        {
            if (_root == null)
            {
                return;
            }
            if (open == IsOpen)
            {
                if (open)
                {
                    SetMode(intro);
                }
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false, false));
                SetMode(intro);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        private void SetMode(bool intro)
        {
            IntroMode = intro;
            _key = 0;
            Refresh();
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false, false);
                return;
            }
            Refresh();
        }

        /// <summary>按后日谈版本 / 语言 / 当前游戏秒重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            long tick = GameClock.Ticks;
            long second = tick / System.Math.Max(1, GameClock.StepHz);
            int key = System.HashCode.Combine(PostgameService.Revision, (int)GameText.Language, state != null ? state.GetHashCode() : 0, second, IntroMode);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get(IntroMode ? "postgame.intro.title" : "postgame.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            _ok.text = GameText.Get("postgame.intro.ok");
            _about.text = GameText.Get("postgame.panel.about");
            _status.text = PostgameService.StatusLine(state, tick);
            _intro.EnableInClassList("uk-hidden", !IntroMode);
            _record.EnableInClassList("uk-hidden", IntroMode);
            _ok.EnableInClassList("uk-hidden", !IntroMode);
            _about.EnableInClassList("uk-hidden", IntroMode);
            if (IntroMode)
            {
                _intro.text = PostgameService.IntroText(state, tick);
                _footer.text = string.Empty;
                VisibleRowCount = 0;
                return;
            }
            _summary.text = PostgameService.SummaryLine(state, tick);
            _next.text = PostgameService.StatusLine(state, tick);
            _best.text = PostgameService.BestLine(state);
            _totals.text = PostgameService.TotalsLine(state);
            IReadOnlyList<PostgamePurgeRecord> hist = PostgameService.History(state);
            while (_rows.Count < hist.Count)
            {
                var l = new Label();
                l.AddToClassList("rl-row");
                _list.Add(l);
                _rows.Add(l);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < hist.Count;
                _rows[i].EnableInClassList("uk-hidden", !shown);
                if (shown)
                {
                    _rows[i].text = PostgameService.RowText(hist[hist.Count - 1 - i]); // 最近的在前
                }
            }
            VisibleRowCount = hist.Count;
            bool empty = hist.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("postgame.panel.empty") : string.Empty;
            _footer.text = GameText.Format("postgame.panel.footer", PostgameService.PeriodDays.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                RaidCatalog.PostgameBase.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                RaidCatalog.PostgameGrowth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), RaidCatalog.MaxUnits);
        }
    }
}
