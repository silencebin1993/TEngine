using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG7-ENV-01（FG07 FGR-ENV-002“点开是日历面板：未来几天的静默夜、天气预报、已知的突袭预报”、第 4 节“画在同一条时间轴上”；FG13 FGU-26）：日历面板（UI Toolkit）。
    ///
    /// 入口：世界时间条的日历行（点一下）、日历键（默认 Alt+L，全部上下文，可重绑）。世界照常运行（不暂停），面板随游戏时间刷新。
    /// 内容：今天起 env.calendar.days 个游戏日，每天一行——时间轴（0:00～24:00：夜晚色块、事件标记 ● ⚠ ★ ☁、今天的“现在”竖线）+ 当天的事件列表；
    /// 事件来自 <see cref="CalendarService"/>（夜晚、已知突袭；静默夜 / 天气由 FG7-ENV-02 / 03 注册来源后自动出现在同一条时间轴上）。
    /// 事件集合不变时只移动“现在”竖线、改顶部时刻（不重建行，B18）；Esc / 关闭 / 点遮罩关闭；离开世界自动收起。
    /// </summary>
    public sealed class CalendarPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>HUD 之上、防御总览 30062 与暂停菜单 30070 之下（从暂停菜单打开的面板盖在它上面）。</summary>
        public const int Order = 30061;

        public static CalendarPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title, _now, _legend, _footer;
        private Button _close;
        private VisualElement _days;
        private readonly List<VisualElement> _nowLines = new List<VisualElement>(1);
        private readonly List<string> _rowTexts = new List<string>(32);
        private readonly List<VisualElement> _marks = new List<VisualElement>(16);
        /// <summary>每天一行的行模板（CalendarDayRow.uxml，YooAsset 异步加载；自检直接给）。</summary>
        private VisualTreeAsset _rowTemplate;
        private bool _templateOwned;
        private bool _templateLoading;
        private static readonly int[] TickHours = { 0, 6, 12, 18, 24 };
        private string _structureKey;
        private int _drawnMinute = -1;
        private int _drawnDay = -1;
        private int _drawnLanguage = -1;
        private float _refreshTimer;

        protected override string UxmlLocation => "CalendarPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string NowText => _now?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string LegendText => _legend?.text ?? string.Empty;
        public int DayRowCount => _days?.childCount ?? 0;
        public Button CloseButton => _close;
        /// <summary>全部事件行的文字（按天、按时刻）。</summary>
        public IReadOnlyList<string> EventTexts => _rowTexts;
        /// <summary>事件集合变化导致整行重建的次数（自检：同一事件集合只移动竖线、不重建）。</summary>
        public int RebuildCount { get; private set; }
        /// <summary>自检诊断：上一次重建前 / 后的事件签名（重建原因可对比）。</summary>
        public string PreviousStructureKey { get; private set; }
        public string StructureKeyNow => _structureKey;
        /// <summary>今天时间轴上“现在”竖线的位置（0～100%）。</summary>
        public float NowLinePercent { get; private set; } = -1f;
        /// <summary>第 <paramref name="day"/> 行时间轴里某一类标记的个数（自检读）。</summary>
        public int MarkCount(int day, string cls)
        {
            if (_days == null || day < 0 || day >= _days.childCount)
            {
                return 0;
            }
            VisualElement bar = _days[day].Q<VisualElement>(className: "cal-bar");
            if (bar == null)
            {
                return 0;
            }
            int n = 0;
            foreach (VisualElement c in bar.Children())
            {
                if (c.ClassListContains(cls))
                {
                    n++;
                }
            }
            return n;
        }

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
            DetachMarks();
            if (_rowTemplate != null && _templateOwned)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
            }
            _rowTemplate = null;
            _templateOwned = false;
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开日历（日历行按钮、日历键）。不在世界里时给“不行”的提示音并不打开。</summary>
        public static void Open()
        {
            if (!InWorld())
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                return;
            }
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

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        private static bool InWorld() => InWorldOverrideForTests || (CampaignSession.Current != null && GameRoot.AnyRegionActive);

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        /// <summary>行模板（可变数量的天由模板克隆，UI Toolkit 红线 1 / 5；资源成对释放见 <see cref="OnDestroy"/>）。加载完之前打开的日历在加载完后重建。</summary>
        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CalendarDayRow");
            _templateLoading = false;
            if (this == null || _rowTemplate != null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            if (t == null)
            {
                Log.Error("[CalendarPanelUIToolkit] 加载 CalendarDayRow 失败，日历的每日行不可用。");
                return;
            }
            _rowTemplate = t;
            _templateOwned = true;
            _structureKey = null;
            if (IsOpen)
            {
                Refresh();
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _templateOwned = false;
            _structureKey = null;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("CalendarRoot");
            _title = root.Q<Label>("CalendarTitle");
            _now = root.Q<Label>("CalendarNow");
            _legend = root.Q<Label>("CalendarLegend");
            _footer = root.Q<Label>("CalendarFooter");
            _close = root.Q<Button>("CalendarClose");
            _days = root.Q<VisualElement>("CalendarDays");
            if (_close != null)
            {
                _close.clicked += () => SetOpen(false);
            }
            _root?.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
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
                GuidanceHooks.Raise(GuidanceHooks.EnvCalendarFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _structureKey = null;
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
            if (!InWorld())
            {
                SetOpen(false);
                return;
            }
            // 每 0.25 真实秒看一次游戏分钟有没有变（3x 下 1 游戏分钟 ≈ 0.28 真实秒）；变了才刷新。
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = 0.25f;
            Refresh();
        }

        /// <summary>按统一时钟与日历事件刷新（自检直接调）。事件集合不变时只移动“现在”竖线与顶部时刻。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            double now = GameClock.GameSeconds;
            int minute = GameClock.MinuteOfDay(now);
            int today = GameClock.DayOf(now);
            int lang = (int)GameText.Language;
            int days = CalendarService.Days;
            double from = GameClock.SecondsAt(today, 0.0);
            double to = from + days * GameClock.DaySeconds;
            IReadOnlyList<CalendarEntry> entries = s != null ? CalendarService.Collect(s, now, to) : System.Array.Empty<CalendarEntry>();
            string key = StructureKey(entries, today, days, lang);
            if (key != _structureKey)
            {
                PreviousStructureKey = _structureKey;
                _structureKey = key;
                Rebuild(entries, today, days, now);
            }
            if (minute != _drawnMinute || today != _drawnDay || lang != _drawnLanguage)
            {
                _drawnMinute = minute;
                _drawnDay = today;
                _drawnLanguage = lang;
                DayPhase p = DayNightCycle.PhaseAt(now);
                _now.text = GameText.Format("env.cal.now", GameClock.FormatDayTime(now), DayNightCycle.PhaseGlyph(p) + " " + DayNightCycle.PhaseName(p));
            }
            float pct = (float)(GameClock.HourOfDay(now) / 24.0 * 100.0);
            NowLinePercent = pct;
            foreach (VisualElement line in _nowLines)
            {
                line.style.left = new Length(pct, LengthUnit.Percent);
            }
        }

        /// <summary>事件集合的签名：事件编号 + 开始的游戏分钟 + 标题 + 正文（正文里的“预计抵达”变了也要重画）+ 今天 + 语言。</summary>
        private static string StructureKey(IReadOnlyList<CalendarEntry> entries, int today, int days, int lang)
        {
            var sb = new StringBuilder(64 + entries.Count * 24);
            sb.Append(today).Append('|').Append(days).Append('|').Append(lang);
            double minuteSeconds = GameClock.HourSeconds / 60.0;
            foreach (CalendarEntry e in entries)
            {
                sb.Append('|').Append(e.Id).Append('@').Append((long)System.Math.Floor(e.StartSeconds / minuteSeconds)).Append(':').Append(e.Title).Append(':').Append(e.Detail);
            }
            return sb.ToString();
        }

        private void Rebuild(IReadOnlyList<CalendarEntry> entries, int today, int days, double now)
        {
            if (_rowTemplate == null)
            {
                // 行模板还在加载：先不建行，加载完后（LoadRowTemplate）清掉签名重建。
                _structureKey = null;
                return;
            }
            RebuildCount++;
            _title.text = GameText.Get("env.cal.title");
            _close.text = GameText.Get("env.cal.close");
            _legend.text = GameText.Get("env.cal.legend");
            // 先展开 {act:…} 键位记号再填天数（{act:…} 不是 string.Format 的占位符，直接 Format 会出错）。
            // FG7-ENV-02：静默夜来源注册后只写“天气预报将在后续版本加入”。
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("env.cal.footer")).Replace("{0}", days.ToString(CultureInfo.InvariantCulture))
                           + CalendarService.PendingKindsSuffix();
            DetachMarks();
            _days.Clear();
            _nowLines.Clear();
            _rowTexts.Clear();
            for (int d = 0; d < days; d++)
            {
                int day = today + d;
                double dayStart = GameClock.SecondsAt(day, 0.0);
                double dayEnd = dayStart + GameClock.DaySeconds;
                VisualElement row = _rowTemplate.Instantiate().Q<VisualElement>("CalDay");
                if (d == 0)
                {
                    row.AddToClassList("cal-day-today");
                }
                row.Q<Label>("CalDayTitle").text = GameText.Format(d == 0 ? "env.cal.today" : "env.cal.day", day.ToString(CultureInfo.InvariantCulture));
                VisualElement bar = row.Q<VisualElement>("CalDayBar");
                VisualElement list = row.Q<VisualElement>("CalDayEvents");
                for (int k = 0; k < TickHours.Length; k++)
                {
                    Label tick = row.Q<Label>("CalTick" + k.ToString(CultureInfo.InvariantCulture));
                    if (tick != null)
                    {
                        tick.text = TickHours[k].ToString(CultureInfo.InvariantCulture) + ":00";
                    }
                }
                int listed = 0;
                // 夜晚色块：与这一天重叠的每段夜晚（今天已过去的部分也画，时间轴是完整的一天）。
                for (int nd = day - 1; nd <= day; nd++)
                {
                    DayNightCycle.NightOfDay(nd, out double ns, out double ne);
                    AddSegment(bar, System.Math.Max(ns, dayStart), System.Math.Min(ne, dayEnd), dayStart);
                }
                foreach (CalendarEntry e in entries)
                {
                    // 今天这一行另列“正在进行”的（开始于昨天、现在还没结束的夜晚 / 静默夜 / 天气）。
                    bool ongoing = d == 0 && e.StartSeconds < dayStart && e.EndSeconds > now;
                    if (!ongoing && (e.StartSeconds < dayStart || e.StartSeconds >= dayEnd))
                    {
                        continue;
                    }
                    if (e.Kind != CalendarKind.Night)
                    {
                        AddMark(bar, e, dayStart);
                    }
                    string text = (ongoing
                                      ? GameText.Format("env.cal.event_ongoing", Glyph(e.Kind), e.Title, e.Detail)
                                      : GameText.Format("env.cal.event_line", GameClock.FormatHhMm(e.StartSeconds), Glyph(e.Kind), e.Title, e.Detail))
                                  + (e.Forecast ? GameText.Get("env.cal.forecast_suffix") : string.Empty);
                    var line = new Label(text);
                    line.AddToClassList("cal-event");
                    if (e.Forecast)
                    {
                        line.AddToClassList("cal-event-forecast");
                    }
                    list.Add(line);
                    _rowTexts.Add(text);
                    listed++;
                }
                if (listed == 0)
                {
                    var empty = new Label(GameText.Get("env.cal.empty_day"));
                    empty.AddToClassList("cal-event");
                    empty.AddToClassList("cal-event-empty");
                    list.Add(empty);
                }
                if (d == 0)
                {
                    var nowLine = new VisualElement();
                    nowLine.AddToClassList("cal-now");
                    nowLine.pickingMode = PickingMode.Ignore;
                    bar.Add(nowLine);
                    _nowLines.Add(nowLine);
                }
                _days.Add(row);
            }
        }

        private static void AddSegment(VisualElement bar, double start, double end, double dayStart)
        {
            if (!(end > start))
            {
                return;
            }
            double daySeconds = GameClock.DaySeconds;
            var seg = new VisualElement();
            seg.AddToClassList("cal-seg-night");
            seg.pickingMode = PickingMode.Ignore;
            seg.style.left = new Length((float)((start - dayStart) / daySeconds * 100.0), LengthUnit.Percent);
            seg.style.width = new Length((float)((end - start) / daySeconds * 100.0), LengthUnit.Percent);
            bar.Add(seg);
        }

        /// <summary>重建 / 销毁前解绑旧标记的悬停提示（UiTooltip 的绑定表是静态的，只有 Detach 会删条目；不解绑会把整行留在内存里）。</summary>
        private void DetachMarks()
        {
            foreach (VisualElement m in _marks)
            {
                UiTooltip.Detach(m);
            }
            _marks.Clear();
        }

        private void AddMark(VisualElement bar, CalendarEntry e, double dayStart)
        {
            var mark = new Label(Glyph(e.Kind));
            mark.AddToClassList("cal-mark");
            mark.AddToClassList(e.Kind == CalendarKind.Raid ? "cal-mark-raid" : e.Kind == CalendarKind.SilentNight ? "cal-mark-silent" : "cal-mark-weather");
            mark.style.left = new Length((float)(System.Math.Max(0.0, e.StartSeconds - dayStart) / GameClock.DaySeconds * 100.0), LengthUnit.Percent);
            string title = e.Title;
            string body = GameClock.FormatDayTime(e.StartSeconds) + "\n" + e.Detail;
            UiTooltip.Attach(mark, () => new TooltipContent { Title = title, Body = body });
            _marks.Add(mark);
            bar.Add(mark);
        }

        /// <summary>日历事件字形。只用 UI 字体（NotoSansSC，无回退字体）里有的字形——☾ ✦ 不在字体里会显示成缺字方框；FgDayNightSelfCheck D2 逐字核对。</summary>
        public static string Glyph(CalendarKind k) => k switch
        {
            CalendarKind.Night => DayNightCycle.PhaseGlyph(DayPhase.Night),
            CalendarKind.Raid => "⚠",
            CalendarKind.SilentNight => "★",
            _ => "☁",
        };
    }
}
