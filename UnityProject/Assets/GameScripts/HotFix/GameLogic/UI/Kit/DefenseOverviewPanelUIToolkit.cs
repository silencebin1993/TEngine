using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-06（FG06 FGR-DEF-070 防御面板；FG13 FGU-27 防御总览；承接 DEBT-FG6DEF01-05 / -06、DEBT-FG6DEF02-07 / -08 的防御总览与快捷键部分、DEBT-FG4ECO06-03 的批量补给优先）：防御总览面板。
    /// - 汇总：炮塔数（正常 / 有问题 / 被摧毁）、陷阱、护盾、驻防机器、炮塔累计击毁。
    /// - 列表（虚拟化 ListView，B18 / FGR-UX-070）：<see cref="DefenseOverviewService.CollectRows"/> 的每一行；点行打开它的面板（炮塔 / 防御 / 机器名册），“定位”镜头飞过去（面板保持打开）。
    ///   筛选：全部 / 炮塔 / 陷阱与护盾 / 驻防机器 / 只看有问题的。
    /// - 批量：全部炮塔的目标模式（下拉选中即生效，<see cref="TurretService.TrySetModeBatch"/>，可逆不确认）；“突袭时炮塔 / 陷阱补给优先”一键开关（战时预案）。
    /// - 热力图（占位色块，DEBT-FG6DEF06-01）与薄弱点：<see cref="DefenseOverviewService.BuildCoverage"/>；只在打开 / 防线或突袭计划变化时重算（每 defov.refresh_seconds 比一次键）。
    /// 入口：快捷键（默认 Alt+E，全部上下文）、突袭条观战栏“防御总览”、炮塔面板“防御总览…”。模态；Esc / 关闭 / 点遮罩关闭。只在家园载入时可用（否则写明原因）。
    /// </summary>
    public sealed class DefenseOverviewPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30062;
        private const float ReadingsInterval = 0.5f;

        public static DefenseOverviewPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _summary;
        private Button _help;
        private Button _close;
        private Label _message;
        private DropdownField _filter;
        private DropdownField _batch;
        private Button _boost;
        private Label _secList;
        private Label _empty;
        private ListView _list;
        private Label _secHeat;
        private Image _heatmap;
        private Label _legend;
        private Label _stats;
        private Label _secWeak;
        private Label _weakHead;
        private VisualElement _weakList;
        private Label _weakMore;
        private Label _footer;

        private readonly List<DefenseRow> _all = new List<DefenseRow>(64);
        private readonly List<DefenseRow> _shown = new List<DefenseRow>(64);
        private readonly List<Button> _weakButtons = new List<Button>(12);
        private readonly List<int> _batchCodes = new List<int>(6);
        private DefenseCoverageMap _map;
        private Texture2D _texture;
        private int _filterIndex;
        private int _structureKey;
        private int _coverageKey;
        private float _nextReadings;
        private float _nextCoverageCheck;
        private bool _suppress;

        protected override string UxmlLocation => "DefenseOverviewPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string StatsText => _stats?.text ?? string.Empty;
        public string LegendText => _legend?.text ?? string.Empty;
        public string WeakHeadText => _weakHead?.text ?? string.Empty;
        public string BoostText => _boost?.text ?? string.Empty;
        public int RowsShown => _shown.Count;
        public DefenseRow ShownRow(int i) => i >= 0 && i < _shown.Count ? _shown[i] : null;
        public int WeakShown { get; private set; }
        public string WeakText(int i) => i >= 0 && i < WeakShown ? _weakButtons[i].text : string.Empty;
        public DefenseCoverageMap Coverage => _map;
        public Texture2D HeatTexture => _texture;
        public DropdownField FilterField => _filter;
        public DropdownField BatchField => _batch;
        public Button BoostButton => _boost;
        public Button CloseButton => _close;
        public ListView List => _list;
        public int CoverageBuilds { get; private set; }
        public VisualElement RootElement => _root;

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
            if (_texture != null)
            {
                Destroy(_texture); // 与创建成对释放
                _texture = null;
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
            _root = root.Q<VisualElement>("DefOvRoot");
            _title = root.Q<Label>("DefOvTitle");
            _summary = root.Q<Label>("DefOvSummary");
            _help = root.Q<Button>("DefOvHelp");
            _close = root.Q<Button>("DefOvClose");
            _message = root.Q<Label>("DefOvMessage");
            _filter = root.Q<DropdownField>("DefOvFilter");
            _batch = root.Q<DropdownField>("DefOvBatch");
            _boost = root.Q<Button>("DefOvBoost");
            _secList = root.Q<Label>("DefOvSecList");
            _empty = root.Q<Label>("DefOvEmpty");
            _list = root.Q<ListView>("DefOvList");
            _secHeat = root.Q<Label>("DefOvSecHeat");
            _heatmap = root.Q<Image>("DefOvHeatmap");
            _legend = root.Q<Label>("DefOvLegend");
            _stats = root.Q<Label>("DefOvStats");
            _secWeak = root.Q<Label>("DefOvSecWeak");
            _weakHead = root.Q<Label>("DefOvWeakHead");
            _weakList = root.Q<VisualElement>("DefOvWeakList");
            _weakMore = root.Q<Label>("DefOvWeakMore");
            _footer = root.Q<Label>("DefOvFooter");
            _heatmap.scaleMode = ScaleMode.ScaleToFit;

            _close.clicked += () => SetOpen(false);
            _help.clicked += () => Progression.MechanicCodex.Open("codex.defense.overview");
            _boost.clicked += ClickBoost;
            UiTooltip.Attach(_boost, () => new TooltipContent
            {
                Title = GameText.Get("defov.boost.off"),
                Body = GameText.Format("defov.boost.tip", InputDisplay.ForAction(GameActionId.OpenRules)),
                Shortcut = GameActionId.OpenRules,
            });
            _filter.RegisterValueChangedCallback(_ => OnFilterChanged());
            _batch.RegisterValueChangedCallback(_ => OnBatchChanged());
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _shown;
            _structureKey = 0;
            _coverageKey = 0;
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
                GuidanceHooks.Raise(GuidanceHooks.DefenseOverviewFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                SetMessage(string.Empty, false);
                _structureKey = 0;
                _coverageKey = 0;
                Refresh(force: true);
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
            Refresh(force: false);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine((int)GameText.Language, GameSettings.Revision, _filterIndex, StandingRuleService.Revision);
            if (force || key != _structureKey)
            {
                _structureKey = key;
                RebuildStatic(s);
            }
            float now = Time.unscaledTime;
            bool homeReady = WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded;
            if (!homeReady && !InWorldOverrideForTests)
            {
                _empty.text = GameText.Get("defov.error_world");
                SetVisible(_empty, true);
                _shown.Clear();
                _list.RefreshItems();
                return;
            }
            if (force || now >= _nextReadings || !Application.isPlaying)
            {
                _nextReadings = now + ReadingsInterval;
                RefreshRows(s);
            }
            if (force || now >= _nextCoverageCheck || !Application.isPlaying)
            {
                _nextCoverageCheck = now + DefenseOverviewService.RefreshSeconds;
                int ck = DefenseOverviewService.CoverageKey(s);
                if (force || ck != _coverageKey || _map == null)
                {
                    _coverageKey = ck;
                    RebuildCoverage(s);
                }
            }
        }

        private void RebuildStatic(CampaignState s)
        {
            _suppress = true;
            try
            {
                _title.text = GameText.Get("defov.title");
                _close.text = GameText.Get("defov.close");
                _help.text = GameText.Get("prod.panel.help");
                _secList.text = GameText.Get("defov.sec.list");
                _secHeat.text = GameText.Get("defov.sec.heat");
                _secWeak.text = GameText.Get("defov.sec.weak");
                _legend.text = GameText.Get("defov.heat.legend");
                _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("defov.footer"));
                _filter.label = GameText.Get("defov.filter.label");
                var filters = new List<string>
                {
                    GameText.Get("defov.filter.all"), GameText.Get("defov.filter.turret"), GameText.Get("defov.filter.defense"),
                    GameText.Get("defov.filter.garrison"), GameText.Get("defov.filter.problems"),
                };
                DropdownChoices.Apply(_filter, filters, filters[0]);
                _filter.SetValueWithoutNotify(_filter.choices[Mathf.Clamp(_filterIndex, 0, _filter.choices.Count - 1)]);
                _batch.label = GameText.Get("defov.batch.label");
                _batchCodes.Clear();
                var modes = new List<string> { GameText.Get("defov.batch.none") };
                _batchCodes.Add(-1);
                foreach (TurretModeDef m in TurretCatalog.Modes)
                {
                    modes.Add(m.Name);
                    _batchCodes.Add(m.Code);
                }
                DropdownChoices.Apply(_batch, modes, modes[0]);
                _batch.SetValueWithoutNotify(_batch.choices[0]);
                _batch.SetEnabled(TurretService.All(s).Count > 0);
                bool on = StandingRuleService.DefenseSupplyBoostConfigured(s, out int serial);
                _boost.text = on ? GameText.Format("defov.boost.on", StandingRuleService.RuleLabel(s, serial)) : GameText.Get("defov.boost.off");
                _boost.EnableInClassList("dov-btn-on", on);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void RefreshRows(CampaignState s)
        {
            DefenseOverviewService.CollectRows(s, _all, out DefenseSummary sum);
            _summary.text = DefenseOverviewService.SummaryText(sum);
            _shown.Clear();
            foreach (DefenseRow r in _all)
            {
                if (PassesFilter(r))
                {
                    _shown.Add(r);
                }
            }
            bool none = _all.Count == 0;
            bool filteredOut = !none && _shown.Count == 0;
            _empty.text = none ? GameText.Format("defov.empty", InputDisplay.ForAction(GameActionId.OpenBuildMenu), InputDisplay.ForAction(GameActionId.OpenRoster))
                : filteredOut ? GameText.Get("defov.empty_filter") : string.Empty;
            SetVisible(_empty, none || filteredOut);
            _list.RefreshItems();
        }

        private bool PassesFilter(DefenseRow r)
        {
            switch (_filterIndex)
            {
                case 1: return r.Kind == DefenseRowKind.Turret;
                case 2: return r.Kind == DefenseRowKind.Trap || r.Kind == DefenseRowKind.Shield;
                case 3: return r.Kind == DefenseRowKind.Garrison;
                case 4: return r.Problem;
                default: return true;
            }
        }

        private void RebuildCoverage(CampaignState s)
        {
            _stats.text = GameText.Get("defov.loading");
            _map = DefenseOverviewService.BuildCoverage(s, _map);
            CoverageBuilds++;
            _stats.text = DefenseOverviewService.StatsText(_map);
            PaintHeatmap();
            _weakHead.text = _map.RingMode
                ? GameText.Get("defov.weak.no_route") + (_map.Weak.Count == 0 ? "\n" + GameText.Get("defov.weak.none_ring") : string.Empty)
                : _map.Weak.Count == 0 ? GameText.Get("defov.weak.none_route") : GameText.Get("defov.weak.tip");
            while (_weakButtons.Count < _map.Weak.Count)
            {
                int index = _weakButtons.Count;
                var b = new Button { name = "DefOvWeak" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("dov-weak");
                b.clicked += () => ClickWeak(index);
                UiTooltip.Attach(b, () => new TooltipContent { Title = GameText.Get("defov.sec.weak"), Body = GameText.Get("defov.weak.tip") });
                _weakButtons.Add(b);
                _weakList.Add(b);
            }
            for (int i = 0; i < _weakButtons.Count; i++)
            {
                bool show = i < _map.Weak.Count;
                SetVisible(_weakButtons[i], show);
                if (show)
                {
                    _weakButtons[i].text = DefenseOverviewService.WeakText(_map, i);
                }
            }
            WeakShown = _map.Weak.Count;
            SetVisible(_weakMore, _map.WeakHidden > 0);
            _weakMore.text = _map.WeakHidden > 0 ? GameText.Format("defov.weak.more", _map.WeakHidden) : string.Empty;
        }

        // ─────────────────────────────── 热力图（占位色块）───────────────────────────────

        private static readonly Color32 CNone = new Color32(110, 24, 24, 255);
        private static readonly Color32 COne = new Color32(214, 150, 40, 255);
        private static readonly Color32 CTwo = new Color32(170, 200, 60, 255);
        private static readonly Color32 CThree = new Color32(60, 175, 85, 255);
        private static readonly Color32 CTurret = new Color32(255, 255, 255, 255);
        private static readonly Color32 CCore = new Color32(70, 230, 235, 255);
        private static readonly Color32 CRoute = new Color32(255, 70, 60, 255);
        private static readonly Color32 CWeak = new Color32(220, 90, 255, 255);
        private static readonly Color32[] HeatPalette = { CNone, COne, CTwo, CThree };
        private Color32[] _pixels;

        private void PaintHeatmap()
        {
            if (_map == null || _map.W <= 0 || _map.H <= 0)
            {
                return;
            }
            if (_texture == null || _texture.width != _map.W || _texture.height != _map.H)
            {
                if (_texture != null)
                {
                    Destroy(_texture);
                }
                _texture = new Texture2D(_map.W, _map.H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "DefenseCoverage" };
            }
            if (_pixels == null || _pixels.Length != _map.W * _map.H)
            {
                _pixels = new Color32[_map.W * _map.H];
            }
            Color32[] px = _pixels;
            // 逐格的覆盖数 → 占位色块在 AOT（BinGames.Sim.Combat.CombatCoverage.PaintCounts）；这里只画路线 / 薄弱点 / 炮塔 / 核心这些少量标记。
            BinGames.Sim.Combat.CombatCoverage.PaintCounts(_map.Counts, _map.W * _map.H, HeatPalette, px);
            for (int k = 0; k < _map.RouteXs.Count; k++)
            {
                int[] rx = _map.RouteXs[k], ry = _map.RouteYs[k];
                for (int j = 1; j < rx.Length; j++)
                {
                    Line(px, rx[j - 1], ry[j - 1], rx[j], ry[j], CRoute);
                }
            }
            foreach (WeakPoint w in _map.Weak)
            {
                Line(px, w.Run.StartX, w.Run.StartY, w.Run.EndX, w.Run.EndY, CWeak);
                Cross(px, w.Run.MidX, w.Run.MidY, 3, CWeak);
            }
            foreach (Vector2 t in _map.TurretPoints)
            {
                Dot(px, Mathf.RoundToInt(t.x), Mathf.RoundToInt(t.y), 1, CTurret);
            }
            Dot(px, Mathf.RoundToInt(_map.Core.x), Mathf.RoundToInt(_map.Core.y), 2, CCore);
            _texture.SetPixels32(px);
            _texture.Apply(false);
            _heatmap.image = _texture;
        }

        private void Put(Color32[] px, int x, int y, Color32 c)
        {
            int gx = x - _map.Ox, gy = y - _map.Oy;
            if (gx >= 0 && gy >= 0 && gx < _map.W && gy < _map.H)
            {
                px[gy * _map.W + gx] = c;
            }
        }

        private void Dot(Color32[] px, int x, int y, int r, Color32 c)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    Put(px, x + dx, y + dy, c);
                }
            }
        }

        private void Cross(Color32[] px, int x, int y, int r, Color32 c)
        {
            for (int d = -r; d <= r; d++)
            {
                Put(px, x + d, y + d, c);
                Put(px, x + d, y - d, c);
            }
        }

        private void Line(Color32[] px, int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            for (int guard = 0; guard < 1 << 16; guard++)
            {
                Put(px, x0, y0, c);
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        // ─────────────────────────────── 行 ───────────────────────────────

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("dov-row");
            var main = new Button { name = "DefOvRowMain" };
            main.AddToClassList("mw-btn");
            main.AddToClassList("dov-row-main");
            var locate = new Button { name = "DefOvRowLocate" };
            locate.AddToClassList("mw-btn");
            locate.AddToClassList("dov-row-locate");
            main.clicked += () => ClickRow(row.userData is int i ? i : -1);
            locate.clicked += () => ClickLocate(row.userData is int i ? i : -1);
            UiTooltip.Attach(main, () => new TooltipContent { Title = GameText.Get("defov.sec.list"), Body = GameText.Get("defov.row.open_tip") });
            UiTooltip.Attach(locate, () => new TooltipContent { Title = GameText.Get("defov.row.locate"), Body = GameText.Get("defov.row.locate_tip") });
            row.Add(main);
            row.Add(locate);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            row.userData = index;
            DefenseRow r = index >= 0 && index < _shown.Count ? _shown[index] : null;
            var main = row.Q<Button>("DefOvRowMain");
            var locate = row.Q<Button>("DefOvRowLocate");
            main.text = r?.Text ?? string.Empty;
            main.EnableInClassList("dov-row-problem", r != null && r.Problem && !r.Destroyed);
            main.EnableInClassList("dov-row-destroyed", r != null && r.Destroyed);
            locate.text = GameText.Get("defov.row.locate");
        }

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        /// <summary>
        /// 点一行：打开它的面板（炮塔 / 防御建筑面板 / 机器名册详情）。审查修复（P2，FGU-27）：作为总览的子页打开（UiEscapeStack.OpenChild）——
        /// 盖在总览上面、总览暂时隐藏但不关闭；子页 Esc / 关闭后回到总览（ADR-DEF-006 §4）。
        /// </summary>
        public bool ClickRow(int index)
        {
            DefenseRow r = ShownRow(index);
            if (r == null)
            {
                return false;
            }
            switch (r.Kind)
            {
                case DefenseRowKind.Turret:
                    UiEscapeStack.OpenChild(this, () => TurretPanelUIToolkit.Open(r.Id));
                    return true;
                case DefenseRowKind.Trap:
                case DefenseRowKind.Shield:
                    UiEscapeStack.OpenChild(this, () => DefensePanelUIToolkit.Open(r.Id));
                    return true;
                default:
                    UiEscapeStack.OpenChild(this, () => RosterPanelUIToolkit.OpenDetail(r.LogicId));
                    return true;
            }
        }

        /// <summary>“定位”：镜头飞到这一行（面板保持打开）。</summary>
        public bool ClickLocate(int index)
        {
            DefenseRow r = ShownRow(index);
            IWorldSite home = WorldSimulation.Home;
            return r != null && home != null && home.IsLoaded && WorldView.FlyTo(home.SiteId, r.Position);
        }

        /// <summary>点一处薄弱点：镜头飞过去（面板关掉，方便看清那一片、放炮塔）。</summary>
        public bool ClickWeak(int index)
        {
            if (_map == null || index < 0 || index >= _map.Weak.Count)
            {
                return false;
            }
            WeakPoint w = _map.Weak[index];
            IWorldSite home = WorldSimulation.Home;
            bool ok = home != null && home.IsLoaded && WorldView.FlyTo(home.SiteId, new Vector2(w.Run.MidX, w.Run.MidY));
            if (ok)
            {
                SetOpen(false);
            }
            return ok;
        }

        public void SetFilter(int index)
        {
            if (index >= 0 && index < _filter.choices.Count)
            {
                _filter.index = index;
            }
        }

        private void OnFilterChanged()
        {
            if (_suppress)
            {
                return;
            }
            _filterIndex = Math.Max(0, _filter.index);
            RefreshRows(CampaignSession.Current);
        }

        /// <summary>批量目标模式下拉选第 <paramref name="index"/> 项（0 = 提示项；与玩家点选同一回调：选中即生效）。</summary>
        public void SelectBatch(int index)
        {
            if (index >= 0 && index < _batch.choices.Count)
            {
                _batch.index = index;
            }
        }

        private void OnBatchChanged()
        {
            if (_suppress)
            {
                return;
            }
            int i = _batch.index;
            if (i <= 0 || i >= _batchCodes.Count)
            {
                return;
            }
            TurretOpResult r = TurretService.TrySetModeBatch(CampaignSession.Current, _batchCodes[i]);
            SetMessage(r.Message, !r.Ok);
            if (!r.Ok)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _suppress = true;
            _batch.SetValueWithoutNotify(_batch.choices[0]);
            _suppress = false;
            RefreshRows(CampaignSession.Current);
        }

        public void ClickBoost()
        {
            CampaignState s = CampaignSession.Current;
            bool ok = StandingRuleService.ToggleDefenseSupplyBoost(s, out _, out string message);
            SetMessage(message, !ok);
            _structureKey = 0;
            Refresh(force: true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("dov-message-error", error);
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e == null)
            {
                return;
            }
            e.EnableInClassList("uk-hidden", !visible);
        }
    }
}
