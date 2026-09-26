using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign;
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
