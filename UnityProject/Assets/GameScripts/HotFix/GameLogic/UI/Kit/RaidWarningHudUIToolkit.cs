using System;
using System.Collections.Generic;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-04（FG06 FGR-DEF-024“预警界面：倒计时、地图上的来袭方向箭头、编成（如果有情报）”；FG00 B01/B02/B05/B07/B13/B15/B16/B18/B22/B24）：突袭预警条。
    /// - 每一波一行：倒计时、来袭方向、目标；集结中写“多久后出发”；有监听站的有效预报时加阵营、等级、规模、编成、针对什么，没有时写“编成未知”（不靠颜色区分状态，都有文字）。
    /// - 点一行镜头飞到预计抵达点（Tab 依次切换关注点同样能切到行进中 / 集结中的突袭）；悬停写触发、目标、出发地、预计抵达的游戏时刻与占位说明。
    /// - 远征中也显示（家园整个世界同时运行）；没有突袭时整条隐藏；建造模式打开时收起（建造栏在同一侧）。
    /// - 刷新：每 0.25 真实秒比较一次键（语言、导演版本、游戏分钟、观察地点），键没变不动（B18）。
    /// </summary>
    public sealed class RaidWarningHudUIToolkit : UiKitPanelHost
    {
        /// <summary>左上角常驻 HUD，与世界时间条同层（UI_WORKFLOW_GUIDE 分层表：HUD 3），不与其它 HUD 重叠。</summary>
        public const int Order = 3;

        public static RaidWarningHudUIToolkit Instance { get; private set; }

        /// <summary>编辑模式自检：不在世界里也当作在世界里（没有真实地点时验证布局）。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _panel;
        private Label _title;
        private ScrollView _list;
        private Label _more;
        private readonly List<Button> _rows = new List<Button>(6);
        private readonly List<string> _rowTips = new List<string>(6);
        private readonly List<Vector2> _rowTargets = new List<Vector2>(6);
        private readonly StringBuilder _keyBuilder = new StringBuilder(64);
        private string _lastKey;
        private float _refreshTimer;

        protected override string UxmlLocation => "RaidWarningHud";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public int RowCount { get; private set; }
        public string TitleText => _title?.text ?? string.Empty;
        public string MoreText => _more != null && !_more.ClassListContains("uk-hidden") ? _more.text : string.Empty;
        public string RowText(int i) => i >= 0 && i < RowCount ? _rows[i].text : string.Empty;
        public string RowTip(int i) => i >= 0 && i < RowCount ? _rowTips[i] : string.Empty;
        public Button RowButton(int i) => i >= 0 && i < RowCount ? _rows[i] : null;
        public Vector2 RowTarget(int i) => i >= 0 && i < RowCount ? _rowTargets[i] : Vector2.zero;
        public int Rebuilds { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            _panel = root.Q<VisualElement>("RaidWarnPanel");
            _title = root.Q<Label>("RaidWarnTitle");
            _list = root.Q<ScrollView>("RaidWarnList");
            _more = root.Q<Label>("RaidWarnMore");
            _rows.Clear();
            _rowTips.Clear();
            _rowTargets.Clear();
            RowCount = 0;
            _lastKey = null;
        }

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = 0.25f;
            Refresh();
        }

        /// <summary>按突袭导演刷新（自检可直接调用；<paramref name="force"/> = 忽略键比较）。</summary>
        public void Refresh(bool force = false)
        {
            if (_panel == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool inWorld = state != null && (GameRoot.AnyRegionActive || InWorldOverrideForTests);
            bool building = HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.IsOpen;
            long now = GameClock.Ticks;
            IReadOnlyList<RaidWaveView> waves = inWorld ? RaidDirectorService.IncomingWaves(state, now) : Array.Empty<RaidWaveView>();
            bool show = inWorld && !building && waves.Count > 0;
            SetVisible(_panel, show);
            if (!show)
            {
                RowCount = 0;
                _lastKey = null;
                return;
            }
            long minute = now / Math.Max(1, GameClock.TicksFor(GameClock.DaySeconds / 1440.0));
            _keyBuilder.Clear();
            _keyBuilder.Append(GameText.Language).Append('|').Append(RaidDirectorService.Revision).Append('|').Append(minute).Append('|').Append(waves.Count)
                .Append('|').Append(Campaign.Economy.IntelService.Revision);
            string key = _keyBuilder.ToString();
            if (!force && key == _lastKey)
            {
                return;
            }
            _lastKey = key;
            Rebuilds++;
            _title.text = GameText.Get("raid.warning.title");
            int max = RaidCatalog.WarningRowsMax;
            int n = Math.Min(max, waves.Count);
            while (_rows.Count < n)
            {
                int index = _rows.Count;
                var b = new Button { name = "RaidWarnRow" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("rw-row");
                b.clicked += () => ClickRow(index);
                UiTooltip.Attach(b, () => new TooltipContent
                {
                    Title = GameText.Get("raid.warning.title"),
                    Body = index < _rowTips.Count ? _rowTips[index] : string.Empty,
                    Shortcut = GameActionId.CycleWorldFocus,
                });
                _rows.Add(b);
                _rowTips.Add(string.Empty);
                _rowTargets.Add(Vector2.zero);
                _list.Add(b);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                Button b = _rows[i];
                if (i >= n)
                {
                    SetVisible(b, false);
                    continue;
                }
                RaidWaveView v = waves[i];
                SetVisible(b, true);
                b.text = RaidDirectorService.WaveRowText(state, v, now);
                _rowTips[i] = RaidDirectorService.WaveTip(state, v, now);
                _rowTargets[i] = new Vector2(v.ArriveX, v.ArriveY);
                SetClass(b, "rw-row-assembling", v.Assembling);
                SetClass(b, "rw-row-planned", v.PlannedOnly);
                SetClass(b, "rw-row-arrived", v.Arrived);
            }
            RowCount = n;
            bool more = waves.Count > n;
            SetVisible(_more, more);
            if (more)
            {
                _more.text = GameText.Format("raid.warning.more", waves.Count - n);
            }
        }

        /// <summary>点一行：镜头飞到这一波的预计抵达点（家园所在的星球表面）。</summary>
        public bool ClickRow(int index)
        {
            if (index < 0 || index >= RowCount)
            {
                return false;
            }
            GuidanceHooks.Raise(GuidanceHooks.RaidWarningFirstClick);
            IWorldSite home = WorldSimulation.Home;
            return home != null && home.IsLoaded && WorldView.FlyTo(home.SiteId, _rowTargets[index]);
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e == null)
            {
                return;
            }
            if (visible)
            {
                e.RemoveFromClassList("uk-hidden");
            }
            else
            {
                e.AddToClassList("uk-hidden");
            }
        }

        private static void SetClass(VisualElement e, string cls, bool on)
        {
            if (on)
            {
                e.AddToClassList(cls);
            }
            else
            {
                e.RemoveFromClassList(cls);
            }
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
