using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// FG6-DEF-04（FG06 FGR-DEF-024“预警界面：倒计时、地图上的来袭方向箭头、编成（如果有情报）”）/ FG6-DEF-06（FG13 FGU-28“突袭 HUD：倒计时、方向、编成、剩余敌人”；
    /// FG06 FGR-DEF-042 倍速观战；承接 DEBT-FG6DEF04-13 建造时也看得到突袭、DEBT-FG6DEF01-10 接入炮塔的读数）：左上角突袭条。
    /// - 每一波一行：到达前 = 倒计时、来袭方向、目标（集结中写多久后出发；有监听站预报时加阵营、等级、规模、编成、针对什么，没有时写“编成未知”）；
    ///   到达后 = 剩余敌人（▲突击 ⚡破坏 ▣攻城）、已损失、最晚多久后撤退 / 撤退中、正在拆哪段墙（<see cref="RaidHudService.ArrivedRowText"/>）。不靠颜色区分状态，都有文字。
    /// - 观战栏（有到达的突袭时）：观战 / 停止观战、暂停 / 继续、1x / 2x / 3x（统一时钟的档位，复用暂停与倍速矩阵）、跟随战斗、防御总览（<see cref="RaidSpectateService"/>）。
    /// - 建造模式：整条收成一行（<see cref="RaidHudService.CompactText"/>），不挡建造栏（建造栏在 top 8%）。
    /// - 接入炮塔时：炮塔的热量 / 耐久条、补给、退出接入（同一座炮塔面板的读数）。
    /// - 点一行镜头飞到预计抵达点 / 战斗处；悬停写触发、目标、出发地、预计抵达与占位说明。远征中照样显示。
    /// - 刷新：每 raid.hud.refresh_seconds 真实秒比较一次键（语言、导演版本、游戏分钟、剩余敌人、观战 / 时钟状态、建造模式、接入），键没变不动（B18）。
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
        private VisualElement _specBar;
        private Label _specStatus;
        private Button _specToggle;
        private Button _specPause;
        private readonly Button[] _specSpeeds = new Button[3];
        private static readonly float[] SpecSpeedValues = { 1f, 2f, 3f };
        private Button _specFollow;
        private Button _specOverview;
        private Label _compact;
        private VisualElement _turret;
        private Label _turretTitle;
        private VisualElement _turretHeatFill;
        private Label _turretHeat;
        private VisualElement _turretHpFill;
        private Label _turretHp;
        private Label _turretSupply;
        private Button _turretLeave;
        private readonly List<Button> _rows = new List<Button>(6);
        private readonly List<string> _rowTips = new List<string>(6);
        private readonly List<Vector2> _rowTargets = new List<Vector2>(6);
        private readonly StringBuilder _keyBuilder = new StringBuilder(96);
        /// <summary>FG6-DEF-07：远征 HUD 的家园遇袭紧急通知与家园状态小窗（同一个 UXML，排在突袭条最上面）。</summary>
        private readonly HomeRaidAlertView _away = new HomeRaidAlertView();
        private string _lastKey;
        private float _refreshTimer;

        protected override string UxmlLocation => "RaidWarningHud";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public bool SpecBarVisible => _specBar != null && !_specBar.ClassListContains("uk-hidden") && PanelVisible;
        public bool CompactVisible => _compact != null && !_compact.ClassListContains("uk-hidden");
        public bool TurretVisible => _turret != null && !_turret.ClassListContains("uk-hidden");
        public int RowCount { get; private set; }
        public string TitleText => _title?.text ?? string.Empty;
        public string MoreText => _more != null && !_more.ClassListContains("uk-hidden") ? _more.text : string.Empty;
        public string RowText(int i) => i >= 0 && i < RowCount ? _rows[i].text : string.Empty;
        public string RowTip(int i) => i >= 0 && i < RowCount ? _rowTips[i] : string.Empty;
        public Button RowButton(int i) => i >= 0 && i < RowCount ? _rows[i] : null;
        public Vector2 RowTarget(int i) => i >= 0 && i < RowCount ? _rowTargets[i] : Vector2.zero;
        public string CompactText => _compact?.text ?? string.Empty;
        public string SpecStatusText => _specStatus?.text ?? string.Empty;
        public Button SpecToggleButton => _specToggle;
        public Button SpecPauseButton => _specPause;
        public Button SpecSpeedButton(int i) => i >= 0 && i < _specSpeeds.Length ? _specSpeeds[i] : null;
        public Button SpecFollowButton => _specFollow;
        public Button SpecOverviewButton => _specOverview;
        public string TurretTitleText => _turretTitle?.text ?? string.Empty;
        public string TurretHeatText => _turretHeat?.text ?? string.Empty;
        public string TurretHpText => _turretHp?.text ?? string.Empty;
        public string TurretSupplyText => _turretSupply?.text ?? string.Empty;
        public Button TurretLeaveButton => _turretLeave;
        public VisualElement CompactElement => _compact;
        public VisualElement ColumnElement { get; private set; }
        public int Rebuilds { get; private set; }
        /// <summary>FG6-DEF-07：远征中家园遇袭的弹窗与小窗（自检读点）。</summary>
        public HomeRaidAlertView Away => _away;

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
            ColumnElement = root.Q<VisualElement>("RaidWarnColumn");
            _panel = root.Q<VisualElement>("RaidWarnPanel");
            _title = root.Q<Label>("RaidWarnTitle");
            _list = root.Q<ScrollView>("RaidWarnList");
            _more = root.Q<Label>("RaidWarnMore");
            _specBar = root.Q<VisualElement>("RaidSpecBar");
            _specStatus = root.Q<Label>("RaidSpecStatus");
            _specToggle = root.Q<Button>("RaidSpecToggle");
            _specPause = root.Q<Button>("RaidSpecPause");
            for (int i = 0; i < _specSpeeds.Length; i++)
            {
                int index = i;
                _specSpeeds[i] = root.Q<Button>("RaidSpecSpeed" + (i + 1));
                _specSpeeds[i].clicked += () => ClickSpeed(index);
            }
            _specFollow = root.Q<Button>("RaidSpecFollow");
            _specOverview = root.Q<Button>("RaidSpecOverview");
            _compact = root.Q<Label>("RaidWarnCompact");
            _turret = root.Q<VisualElement>("RaidTurretPanel");
            _turretTitle = root.Q<Label>("RaidTurretTitle");
            _turretHeatFill = root.Q<VisualElement>("RaidTurretHeatFill");
            _turretHeat = root.Q<Label>("RaidTurretHeat");
            _turretHpFill = root.Q<VisualElement>("RaidTurretHpFill");
            _turretHp = root.Q<Label>("RaidTurretHp");
            _turretSupply = root.Q<Label>("RaidTurretSupply");
            _turretLeave = root.Q<Button>("RaidTurretLeave");
            _specToggle.clicked += ClickSpectate;
            _specPause.clicked += ClickPause;
            _specFollow.clicked += ClickFollow;
            _specOverview.clicked += ClickOverview;
            _turretLeave.clicked += ClickLeaveTurret;
            _away.Bind(root);
            UiTooltip.Attach(_specToggle, () => new TooltipContent
            {
                Title = GameText.Get("raid.spec.start"),
                Body = GameText.Format("raid.spec.tip", InputDisplay.ForAction(GameActionId.TogglePause), InputDisplay.ForAction(GameActionId.SpeedHalf),
                    InputDisplay.ForAction(GameActionId.SpeedTriple)) + "\n" + InputDisplay.ExpandActionTokens(GameText.Get("raid.spec.keys")),
                Shortcut = GameActionId.SpectateRaid,
            });
            UiTooltip.Attach(_specFollow, () => new TooltipContent
            {
                Title = GameText.Get(RaidSpectateService.Following ? "raid.spec.follow_on" : "raid.spec.follow_off"),
                Body = GameText.Get("raid.spec.follow_tip"),
                Shortcut = GameActionId.SpectateFollow,
            });
            UiTooltip.Attach(_specOverview, () => new TooltipContent
            {
                Title = GameText.Get("raid.spec.overview"),
                Body = GameText.Format("raid.spec.overview_tip", InputDisplay.ForAction(GameActionId.OpenDefense)),
                Shortcut = GameActionId.OpenDefense,
            });
            UiTooltip.Attach(_compact, () => new TooltipContent { Title = GameText.Get("raid.warning.title"), Body = GameText.Get("raid.hud.compact_tip") });
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
            RaidSpectateService.Tick(CampaignSession.Current, Time.unscaledDeltaTime);
            // FG6-DEF-07：远征中家园遇袭的弹窗 / 倒计时按自己的 raid.away.refresh_seconds 节流（与突袭条的 raid.hud.refresh_seconds 分开调）。
            if (_away.Due(Time.unscaledDeltaTime))
            {
                RefreshAway(false);
            }
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RaidHudService.RefreshSeconds;
            Refresh();
        }

        /// <summary>FG6-DEF-07：远征中家园遇袭的紧急通知与家园小窗（只在镜头不在家园时显示，与家园的建造模式无关）。</summary>
        private void RefreshAway(bool force)
        {
            if (_panel == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            bool inWorld = state != null && (GameRoot.AnyRegionActive || InWorldOverrideForTests);
            _away.Refresh(state, inWorld, GameClock.Ticks, force);
        }

        /// <summary>按突袭导演与战况刷新（自检可直接调用；<paramref name="force"/> = 忽略键比较）。</summary>
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
            bool active = inWorld && RaidHudService.AnyActive(state);
            // 没有计划的突袭（调试 / 旧档派出的队伍）到达时也显示：只有观战栏（剩余敌人在状态行里）。
            bool show = inWorld && !building && (waves.Count > 0 || active);
            SetVisible(_panel, show);
            RefreshCompact(state, inWorld && building, waves, now);
            RefreshTurret(state, inWorld);
            // FG6-DEF-07：远征中家园遇袭平时由 Update 按 raid.away.refresh_seconds 刷新；强制刷新（自检）时一并立即刷新。
            if (force)
            {
                RefreshAway(true);
            }
            if (!show)
            {
                RowCount = 0;
                _lastKey = null;
                return;
            }
            long minute = now / Math.Max(1, GameClock.TicksFor(GameClock.DaySeconds / 1440.0));
            int alive = active ? RaidHudService.TotalAlive(state) : -1;
            _keyBuilder.Clear();
            _keyBuilder.Append(GameText.Language).Append('|').Append(RaidDirectorService.Revision).Append('|').Append(minute).Append('|').Append(waves.Count)
                .Append('|').Append(Campaign.Economy.IntelService.Revision).Append('|').Append(alive).Append('|').Append(RaidSpectateService.Revision)
                .Append('|').Append(GameClock.Revision).Append('|').Append(RaidSpectateService.Following).Append('|').Append(InputRouter.ModalOwnerList.Count);
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
                Vector2 target = new Vector2(v.ArriveX, v.ArriveY);
                if (v.Arrived && RaidHudService.TryWaveFight(state, v.Wave, out RaidWaveFight f))
                {
                    target = f.Alive > 0 && f.Engaged > 0 ? f.Centroid : f.GroupPos; // 到达后点一行飞到战斗处
                }
                _rowTargets[i] = target;
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
            RefreshSpecBar(active);
        }

        private void RefreshSpecBar(bool active)
        {
            SetVisible(_specBar, active);
            if (!active)
            {
                return;
            }
            bool on = RaidSpectateService.Active;
            _specStatus.text = RaidSpectateService.StatusText();
            _specToggle.text = GameText.Get(on ? "raid.spec.stop" : "raid.spec.start");
            SetClass(_specToggle, "rw-spec-on", on);
            _specPause.text = GameText.Get(GameClock.Paused ? "raid.spec.resume" : "raid.spec.pause");
            for (int i = 0; i < _specSpeeds.Length; i++)
            {
                bool sel = Mathf.Approximately(GameClock.Speed, SpecSpeedValues[i]);
                _specSpeeds[i].text = (sel ? "● " : string.Empty) + GameText.Format("raid.spec.speed", SpecSpeedValues[i].ToString("0", CultureInfo.InvariantCulture));
                SetClass(_specSpeeds[i], "rw-spec-on", sel);
            }
            _specFollow.text = GameText.Get(RaidSpectateService.Following ? "raid.spec.follow_on" : "raid.spec.follow_off");
            _specFollow.SetEnabled(on);
            _specOverview.text = GameText.Get("raid.spec.overview");
        }

        private void RefreshCompact(CampaignState state, bool building, IReadOnlyList<RaidWaveView> waves, long now)
        {
            string text = building ? RaidHudService.CompactText(state, waves, now) : string.Empty;
            SetVisible(_compact, text.Length > 0);
            if (text.Length > 0 && _compact.text != text)
            {
                _compact.text = text;
            }
        }

        private void RefreshTurret(CampaignState state, bool inWorld)
        {
            string id = inWorld ? TurretUplink.ActiveTurretId(state) : string.Empty;
            bool show = !string.IsNullOrEmpty(id) && TurretService.TryGetReadout(state, id, out TurretReadout ro);
            SetVisible(_turret, show);
            if (!show)
            {
                return;
            }
            TurretService.TryGetReadout(state, id, out ro);
            _turretTitle.text = GameText.Format("raid.turret.title", ro.Name);
            float heatMax = Mathf.Max(1f, ro.OverheatAt);
            _turretHeat.text = ro.Overheated
                ? GameText.Format("raid.turret.heat_over", Mathf.RoundToInt(ro.Heat), Mathf.RoundToInt(ro.OverheatAt), Mathf.RoundToInt(ro.RecoverBelow))
                : GameText.Format("raid.turret.heat", Mathf.RoundToInt(ro.Heat), Mathf.RoundToInt(ro.OverheatAt));
            _turretHeatFill.style.width = Length.Percent(Mathf.Clamp01(ro.Heat / heatMax) * 100f);
            SetClass(_turretHeatFill, "rw-bar-heat-over", ro.Overheated);
            _turretHp.text = GameText.Format("raid.turret.hp", Mathf.RoundToInt(ro.Health), Mathf.RoundToInt(ro.MaxHealth));
            _turretHpFill.style.width = Length.Percent(Mathf.Clamp01(ro.Health / Mathf.Max(1f, ro.MaxHealth)) * 100f);
            _turretSupply.text = GameText.Format("raid.turret.supply", string.IsNullOrEmpty(ro.SupplyLine) ? GameText.Get("defov.supply_power_only") : ro.SupplyLine);
            _turretLeave.text = GameText.Format("raid.turret.leave", InputDisplay.ForAction(GameActionId.ToggleCameraView));
        }

        // ─────────────────────────────── 操作（按钮，自检直接调）───────────────────────────────

        /// <summary>点一行：镜头飞到这一波的预计抵达点（到达后飞到战斗处）。</summary>
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

        public void ClickSpectate()
        {
            if (!RaidSpectateService.Toggle(CampaignSession.Current))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied); // 原因在 RaidSpectateService.LastMessage（按钮只在有到达的突袭时显示，这里只是兜底）
            }
            Refresh(force: true);
        }

        public void ClickPause()
        {
            GameRoot.ToggleWorldPause();
            Refresh(force: true);
        }

        /// <summary>观战栏的 1x / 2x / 3x（统一时钟的档位；没在观战时点 2x / 3x 直接开始观战）。</summary>
        public void ClickSpeed(int index)
        {
            if (index < 0 || index >= SpecSpeedValues.Length)
            {
                return;
            }
            float speed = SpecSpeedValues[index];
            if (!RaidSpectateService.Active && speed >= 2f)
            {
                RaidSpectateService.TryStart(CampaignSession.Current, speed, out _);
            }
            else
            {
                RaidSpectateService.SetSpeed(speed);
            }
            Refresh(force: true);
        }

        public void ClickFollow()
        {
            if (!RaidSpectateService.ToggleFollow(CampaignSession.Current))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            Refresh(force: true);
        }

        public void ClickOverview() => DefenseOverviewPanelUIToolkit.Toggle();

        public void ClickLeaveTurret()
        {
            TurretUplink.Leave(CampaignSession.Current);
            Refresh(force: true);
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
            _away.Release();
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
