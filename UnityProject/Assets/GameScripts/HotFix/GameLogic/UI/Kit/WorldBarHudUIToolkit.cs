using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
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
    /// FG0-ARCH-01（FGR-ARC-002 镜头 / FGR-ARC-009 统一时钟；FG07 FGR-ENV-001；FG00 B01/B02/B05/B09/B13/B15/B16/B22/B24）：世界时间条。
    /// - 第一行：“第 N 日 HH:MM”、0.5x / 1x / 2x / 3x、暂停 / 继续、状态（运行中 / 已暂停 / 接入锁 1x）——全部读写统一时钟，整个世界同时生效。
    /// - 第二行：关注点（家园、在外的远征、每支行进中的突袭及预计到达），点一下镜头飞过去；当前关注点有“▸”前缀（不只靠颜色）。
    /// - 每个按钮都有悬停提示并写明当前快捷键（改键后自动跟着变）；突袭 / 地貌是占位表现，提示里写明（B22）。
    /// - 刷新键（语言、日 / 分、速度 / 暂停版本、关注点标签）不变的帧只做 O(1) 比较（B18）。
    /// </summary>
    public sealed class WorldBarHudUIToolkit : UiKitPanelHost
    {
        /// <summary>右上角常驻条，与旧速度 HUD 同层（UI_WORKFLOW_GUIDE 分层表：HUD 3）。</summary>
        public const int Order = 3;

        public static WorldBarHudUIToolkit Instance { get; private set; }

        private VisualElement _bar;
        private Label _dayTime;
        private readonly Button[] _speeds = new Button[4];
        private Button _pause;
        private Label _status;
        private Label _focusTitle;
        private Button _labor;
        private ScrollView _focusList;
        private readonly List<Button> _focusButtons = new List<Button>(4);
        private readonly List<string> _focusIds = new List<string>(4);
        private readonly StringBuilder _keyBuilder = new StringBuilder(128);
        private string _lastKey;

        protected override string UxmlLocation => "WorldBar";
        protected override int SortingOrder => Order;

        /// <summary>自检 / 冒烟可读。</summary>
        public bool BarVisible => _bar != null && !_bar.ClassListContains("uk-hidden");
        public string DayTimeText => _dayTime?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public int FocusButtonCount => _focusButtons.Count;
        public IReadOnlyList<Button> FocusButtons => _focusButtons;
        public Button SpeedButton(int index) => index >= 0 && index < _speeds.Length ? _speeds[index] : null;
        public Button PauseButton => _pause;
        public Button LaborButton => _labor;
        public string LaborText => _labor?.text ?? string.Empty;

        // ── FG4-ECO-08 资源顶栏（自检读点）──
        public int ResourceChipCount { get; private set; }
        public Button ResourceChip(int i) => i >= 0 && i < ResourceChipCount ? _resChips[i] : null;
        public string ResourceChipText(int i) => ResourceChip(i)?.text ?? string.Empty;
        public string ResourceChipTip(int i) => i >= 0 && i < ResourceChipCount ? _resTipBody[i] : string.Empty;
        public int FindResourceChip(string contains)
        {
            for (int i = 0; i < ResourceChipCount; i++)
            {
                if (_resChips[i].text.Contains(contains))
                {
                    return i;
                }
            }
            return -1;
        }
        /// <summary>自检：模拟右键点格子（固定物品 = 取消固定）。</summary>
        public void RightClickResourceChip(int i)
        {
            if (i >= 0 && i < ResourceChipCount)
            {
                _resRight[i]?.Invoke();
            }
        }
        /// <summary>资源顶栏真正重建的次数（自检：节流内不重建、不每帧分配）。</summary>
        public int ResourceRebuilds { get; private set; }
        public static Func<double> ResourceClock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _resRow;
        private readonly List<Button> _resChips = new List<Button>(12);
        private readonly List<Action> _resLeft = new List<Action>(12);
        private readonly List<Action> _resRight = new List<Action>(12);
        private readonly List<string> _resTipTitle = new List<string>(12);
        private readonly List<string> _resTipBody = new List<string>(12);
        private readonly List<int> _subnets = new List<int>(4);
        private double _resNext;
        private int _resKey;

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
            _bar = root.Q<VisualElement>("WorldBar");
            _dayTime = root.Q<Label>("WorldDayTime");
            _pause = root.Q<Button>("WorldPause");
            _status = root.Q<Label>("WorldStatus");
            _focusTitle = root.Q<Label>("WorldFocusTitle");
            _focusList = root.Q<ScrollView>("WorldFocusList");
            _labor = root.Q<Button>("WorldLabor");
            _resRow = root.Q<VisualElement>("WorldResourceRow");
            _resKey = 0;
            _resNext = 0;
            if (_labor != null)
            {
                // FG4-ECO-07（FGR-ECO-042）：顶栏劳动力，点一下打开机器名册。
                _labor.clicked += RosterPanelUIToolkit.Open;
                UiTooltip.Attach(_labor, () =>
                {
                    MachineRoster.LaborCount c = MachineRoster.Labor(CampaignSession.Current);
                    return new TooltipContent
                    {
                        Title = MachineRoster.LaborBarText(c),
                        Body = GameText.Format("roster.labor.tip", c.Labor, c.Busy, InputDisplay.ForAction(GameActionId.OpenRoster)),
                        Shortcut = GameActionId.OpenRoster,
                    };
                });
            }
            GameActionId[] speedActions = { GameActionId.SpeedHalf, GameActionId.SpeedNormal, GameActionId.SpeedDouble, GameActionId.SpeedTriple };
            for (int i = 0; i < _speeds.Length; i++)
            {
                _speeds[i] = root.Q<Button>("WorldSpeed" + i);
                int index = i;
                GameActionId action = speedActions[i];
                _speeds[i].clicked += () => GameClock.SetSpeed(GameClock.Speeds[index]);
                UiTooltip.Attach(_speeds[i], () => new TooltipContent
                {
                    Title = SpeedLabel(GameClock.Speeds[index]),
                    Body = GameText.Format("ui.world.tip.speed", SpeedLabel(GameClock.Speeds[index]), InputDisplay.ForAction(action)),
                    Shortcut = action,
                });
            }
            _pause.clicked += () => GameRoot.ToggleWorldPause();
            UiTooltip.Attach(_pause, () => new TooltipContent
            {
                Title = GameText.Get(GameClock.Paused ? "ui.world.resume" : "ui.world.pause"),
                Body = GameText.Format("ui.world.tip.pause", InputDisplay.ForAction(GameActionId.TogglePause)),
                Shortcut = GameActionId.TogglePause,
            });
            UiTooltip.Attach(_dayTime, () => new TooltipContent
            {
                Title = GameClock.FormatDayTime(GameClock.GameSeconds),
                Body = GameText.Format("ui.world.tip.time", GameClock.DayOf(GameClock.GameSeconds).ToString(CultureInfo.InvariantCulture),
                    GameClock.FormatHhMm(GameClock.GameSeconds),
                    (GameClock.DaySeconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture)),
            });
            UiTooltip.Attach(_status, () => new TooltipContent
            {
                Title = _status.text,
                Body = GameClock.DirectLocked ? GameText.Get("ui.world.tip.direct_locked") : GameText.Get("ui.world.placeholder"),
            });
            _lastKey = null;
        }

        private float _refreshTimer;

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            // 关注点标签含每支队伍的预计到达时间（逐支格式化）：每 0.2 秒真实时间刷新一次，不每帧逐队伍分配字符串（B18）。
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f && GameClock.Revision == _lastRevision)
            {
                return;
            }
            _refreshTimer = 0.2f;
            _lastRevision = GameClock.Revision;
            Refresh();
        }

        private int _lastRevision = -1;

        /// <summary>按统一时钟与世界状态刷新（自检可直接调用）。</summary>
        public void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            bool inWorld = state != null && GameRoot.AnyRegionActive;
            SetVisible(_bar, inWorld);
            if (!inWorld)
            {
                _lastKey = null;
                return;
            }

            // FG4-ECO-07：劳动力读数（MachineRoster.Labor 自带节流：名册 / 岗位变化立刻重算，否则最多每 roster.labor_refresh_seconds 真实秒一次）。
            RefreshLabor(state);
            // FG4-ECO-08：资源顶栏（固定 / 语言变化立刻重建，否则每 eco.topbar.refresh_seconds 真实秒一次；O(固定物品 + 电网数)）。
            RefreshResources(state);

            IReadOnlyList<WorldView.FocusTarget> targets = WorldView.FocusTargets(state);
            _keyBuilder.Clear();
            _keyBuilder.Append(GameText.Language).Append('|').Append(GameClock.MinuteOfDay(GameClock.GameSeconds)).Append('|')
                .Append(GameClock.DayOf(GameClock.GameSeconds)).Append('|').Append(GameClock.Revision).Append('|')
                .Append(WorldView.ObservedSiteId).Append('|').Append(WorldView.LastFocusTargetId);
            for (int i = 0; i < targets.Count; i++)
            {
                _keyBuilder.Append('|').Append(targets[i].Id).Append(':').Append(targets[i].Label);
            }
            string key = _keyBuilder.ToString();
            if (key == _lastKey)
            {
                return;
            }
            _lastKey = key;

            _dayTime.text = GameClock.FormatDayTime(GameClock.GameSeconds);
            for (int i = 0; i < _speeds.Length; i++)
            {
                bool current = Mathf.Approximately(GameClock.Speed, GameClock.Speeds[i]);
                string label = SpeedLabel(GameClock.Speeds[i]);
                _speeds[i].text = current ? GameText.Format("ui.world.focus.current", label) : label;
                SetClass(_speeds[i], "wb-speed-current", current);
            }
            _pause.text = GameText.Get(GameClock.Paused ? "ui.world.resume" : "ui.world.pause");
            SetClass(_pause, "wb-paused", GameClock.Paused);
            _status.text = GameClock.Paused
                ? GameText.Get("ui.world.status_paused")
                : GameClock.DirectLocked
                    ? GameText.Get("ui.world.status_direct_locked")
                    : GameText.Format("ui.world.status_running", SpeedLabel(GameClock.Speed));
            IWorldSite exp = WorldSimulation.ActiveExpedition;
            if (exp != null && exp.IsWiped && WorldView.IsObserved(exp.SiteId))
            {
                _status.text = GameText.Get("ui.world.expedition_wiped_here");
            }

            _focusTitle.text = GameText.Get("ui.world.focus.title");
            RebuildFocus(targets);
        }

        /// <summary>FG4-ECO-08：重建资源顶栏（自检可直接调用：<paramref name="force"/> = 忽略节流）。</summary>
        public void RefreshResources(CampaignState state, bool force = false)
        {
            if (_resRow == null || state == null)
            {
                return;
            }
            // 电网汇总版本号在有储能时每步都变，不进键（否则每帧重建）：电力读数随节流刷新。
            int key = HashCode.Combine(ResourcePins.Revision, (int)GameText.Language, state.GetHashCode());
            double now = ResourceClock();
            if (!force && key == _resKey && now < _resNext)
            {
                return;
            }
            _resKey = key;
            _resNext = now + Math.Max(0.1f, Campaign.Grid.GridContent.Tuning("eco.topbar.refresh_seconds"));
            ResourceRebuilds++;
            int n = 0;
            foreach (PinnedItemRecord pin in ResourcePins.Pins(state))
            {
                ResourcePins.PinView v = ResourcePins.View(state, pin);
                if (v.Item == null)
                {
                    continue;
                }
                string id = v.Item.Id;
                string stock = v.Stock >= 0 ? v.Stock.ToString(CultureInfo.InvariantCulture) : GameText.Get("topbar.fluid");
                string rate = ProductionStats.Rate(v.Item, v.ProducedPerMinute);
                string text = v.HasTarget
                    ? GameText.Format(v.Reached ? "topbar.pin_target_ok" : "topbar.pin_target_miss", v.Item.Name, stock, rate, ProductionStats.Rate(v.Item, v.Target))
                    : GameText.Format("topbar.pin", v.Item.Name, stock, rate);
                string body = GameText.Format("topbar.pin_tip", v.Item.Name, stock, rate, ProductionStats.Rate(v.Item, v.NetPerMinute),
                                  ProductionStats.WindowName(ResourcePins.RateWindow))
                              + (v.HasTarget ? "\n" + GameText.Format(v.Reached ? "topbar.target_ok_tip" : "topbar.target_miss_tip", ProductionStats.Rate(v.Item, v.Target)) : string.Empty)
                              + "\n" + GameText.Format("topbar.pin_hint", InputDisplay.ForAction(GameActionId.OpenStats));
                Chip(n++, text, v.Item.Name, body, "wb-res-pin", v.HasTarget ? (v.Reached ? "wb-res-reached" : "wb-res-missed") : null,
                    () => StatsPanelUIToolkit.OpenTab(StatsTab.Production, id), () => ResourcePins.TryUnpin(CampaignSession.Current, id, out _));
            }
            if (ResourcePins.Pins(state).Count < ResourcePins.MaxPins)
            {
                Chip(n++, GameText.Get("topbar.add"), GameText.Get("topbar.add"), GameText.Format("topbar.add_tip", InputDisplay.ForAction(GameActionId.OpenStats)),
                    "wb-res-add", null, StatsPanelUIToolkit.OpenAllItems, null);
            }
            // 电力（各电网）：每个电网“电网 1 发电 / 需要”，供不上时写明“缺电”（不只靠颜色）。
            _subnets.Clear();
            if (ReferenceEquals(HomeValleyPowerGrid.BoundState, state))
            {
                HomeValleyPowerGrid.SubnetsBySerial(_subnets);
            }
            int maxNets = Math.Max(1, Campaign.Grid.GridContent.TuningInt("eco.topbar.max_subnets"));
            int shown = 0;
            foreach (int sIdx in _subnets)
            {
                if (shown >= maxNets)
                {
                    break;
                }
                if (!HomeValleyPowerGrid.TryGetSubnetInfo(state, sIdx, out PowerSubnetInfo info))
                {
                    continue;
                }
                shown++;
                bool lacking = info.Delivered + 0.5f < info.Demand;
                string name = HomeValleyPowerGrid.SubnetName(info.Serial);
                string text = GameText.Format(lacking ? "topbar.power_short" : "topbar.power", name, HomeValleyPowerGrid.Num(info.Supply), HomeValleyPowerGrid.Num(info.Demand));
                Chip(n++, text, name, HomeValleyPowerGrid.DescribeSubnetLine(sIdx) + "\n" + GameText.Get("topbar.power_tip"), null, lacking ? "wb-res-missed" : null,
                    PowerPanelUIToolkit.Open, null);
            }
            if (_subnets.Count > shown)
            {
                Chip(n++, GameText.Format("topbar.power_more", _subnets.Count - shown), GameText.Get("topbar.power_title"), GameText.Get("topbar.power_tip"), null, null,
                    PowerPanelUIToolkit.Open, null);
            }
            else if (_subnets.Count == 0)
            {
                Chip(n++, GameText.Get("topbar.power_none"), GameText.Get("topbar.power_title"), GameText.Get("topbar.power_none_tip"), null, null, PowerPanelUIToolkit.Open, null);
            }
            Chip(n++, GameText.Format("topbar.tech", state.TechData), ItemCatalog.NameOf(ItemCatalog.TechDataId), GameText.Get("topbar.tech_tip"), null, null,
                ItemsPanelUIToolkit.Open, null);
            Chip(n++, GameText.Format("topbar.research", state.Research?.Points ?? 0), ItemCatalog.NameOf("research_points"), GameText.Get("topbar.research_tip"), null, null,
                ItemsPanelUIToolkit.Open, null);
            Chip(n++, GameText.Format("topbar.exposure", Mathf.RoundToInt(state.SignalExposure), Mathf.RoundToInt(CampaignExposureLedger.MaxExposure)),
                GameText.Get("topbar.exposure_title"), GameText.Format("topbar.exposure_tip", InputDisplay.ForAction(GameActionId.OpenExposure)), null, null,
                SignalCore.SignalCoreHudUIToolkit.OpenExposure, null);
            for (int i = n; i < _resChips.Count; i++)
            {
                SetVisible(_resChips[i], false);
            }
            ResourceChipCount = n;
        }

        private void Chip(int i, string text, string title, string body, string cls, string stateCls, Action left, Action right)
        {
            while (_resChips.Count <= i)
            {
                var b = new Button { name = "WorldRes" + _resChips.Count };
                b.AddToClassList("mw-btn");
                b.AddToClassList("wb-res-chip");
                int index = _resChips.Count;
                b.clicked += () => _resLeft[index]?.Invoke();
                b.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button == 1)
                    {
                        _resRight[index]?.Invoke();
                    }
                });
                UiTooltip.Attach(b, () => new TooltipContent { Title = _resTipTitle[index], Body = _resTipBody[index] });
                _resChips.Add(b);
                _resLeft.Add(null);
                _resRight.Add(null);
                _resTipTitle.Add(string.Empty);
                _resTipBody.Add(string.Empty);
                _resRow.Add(b);
            }
            Button c = _resChips[i];
            SetVisible(c, true);
            c.text = text;
            c.EnableInClassList("wb-res-pin", cls == "wb-res-pin");
            c.EnableInClassList("wb-res-add", cls == "wb-res-add");
            c.EnableInClassList("wb-res-reached", stateCls == "wb-res-reached");
            c.EnableInClassList("wb-res-missed", stateCls == "wb-res-missed");
            _resLeft[i] = left;
            _resRight[i] = right;
            _resTipTitle[i] = title ?? string.Empty;
            _resTipBody[i] = body ?? string.Empty;
        }

        private void RefreshLabor(CampaignState state)
        {
            if (_labor == null)
            {
                return;
            }
            // 审查修复（P2）：只在台数 / 忙碌数 / 语言变化时重新格式化（每帧调用，不每帧分配字符串）。
            MachineRoster.LaborCount c = MachineRoster.Labor(state);
            int language = (int)GameText.Language;
            if (_laborShown && c.Labor == _laborShownCount && c.Busy == _laborShownBusy && language == _laborShownLanguage)
            {
                return;
            }
            _laborShown = true;
            _laborShownCount = c.Labor;
            _laborShownBusy = c.Busy;
            _laborShownLanguage = language;
            _labor.text = MachineRoster.LaborBarText(c);
        }

        private bool _laborShown;
        private int _laborShownCount;
        private int _laborShownBusy;
        private int _laborShownLanguage;

        private void RebuildFocus(IReadOnlyList<WorldView.FocusTarget> targets)
        {
            while (_focusButtons.Count < targets.Count)
            {
                var b = new Button { name = "WorldFocus" + _focusButtons.Count };
                b.AddToClassList("mw-btn");
                b.AddToClassList("wb-focus-btn");
                int index = _focusButtons.Count;
                b.clicked += () =>
                {
                    if (index < _focusIds.Count)
                    {
                        WorldView.FocusOn(_focusIds[index]);
                    }
                };
                UiTooltip.Attach(b, () => new TooltipContent
                {
                    Title = b.text,
                    Body = GameText.Format("ui.world.focus.tip", InputDisplay.ForAction(GameActionId.CycleWorldFocus), InputDisplay.ForAction(GameActionId.FocusHomeCore))
                           + "\n" + GameText.Get("ui.world.placeholder"),
                    Shortcut = GameActionId.CycleWorldFocus,
                });
                _focusButtons.Add(b);
                _focusList.Add(b);
            }
            _focusIds.Clear();
            for (int i = 0; i < _focusButtons.Count; i++)
            {
                Button b = _focusButtons[i];
                bool used = i < targets.Count;
                SetVisible(b, used);
                if (!used)
                {
                    continue;
                }
                WorldView.FocusTarget t = targets[i];
                _focusIds.Add(t.Id);
                bool current = t.Id == WorldView.LastFocusTargetId
                               || (WorldView.LastFocusTargetId == null && !t.IsRaid && t.SiteId == WorldView.ObservedSiteId);
                b.text = current ? GameText.Format("ui.world.focus.current", t.Label) : t.Label;
                SetClass(b, "wb-focus-current", current);
                SetClass(b, "wb-focus-raid", t.IsRaid);
            }
        }

        private static string SpeedLabel(float speed) =>
            GameText.Format("ui.world.speed", speed.ToString("0.#", CultureInfo.InvariantCulture));

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
