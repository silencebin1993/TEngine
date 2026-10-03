using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG5-RND-05（FG05 FGR-RND-050～052；FG13 FGU-25 情报：情报列表、有效期）：情报面板。
    /// - 破译：几座监听站在工作、速度构成（第 1 座 100% + 第 2 座 +50% …，B13）、正在破译哪一类与进度 / 暂停 / 空闲；五类情报各一行写明状态或为什么不破译（B06）。
    /// - 列表：类型筛选（全部 + 五类）；每条一行——标记字 + 类名 + “有效，还剩多久”/“已过时（原因）”+ 来源，换行接内容；新情报前缀“（新）”；
    ///   突袭预报行有“在地图上查看”（打开战略地图并对准来袭方向的箭头）。空列表有空状态说明（B12）。
    /// 入口：情报键（默认 Y，可重绑）、监听站建筑面板“情报…”、新情报 / 突袭预报 / 破译中断通知（点击打开）。模态；Esc / 关闭 / 点遮罩关闭。
    /// 刷新：情报版本、筛选、语言变化或游戏时间每过 1 秒（倒计时）才重建；O(情报条数)，不按帧分配。
    /// </summary>
    public sealed class IntelPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30066;
        private const int FilterCount = 6;

        public static IntelPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static string _pendingKind;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Label _secStatus;
        private Label _status;
        private Label _kinds;
        private readonly Button[] _filters = new Button[FilterCount];
        private Label _secList;
        private Label _empty;
        private ScrollView _list;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<(VisualElement Row, Label Label, Button Map)> _rows = new List<(VisualElement, Label, Button)>();
        private readonly List<int> _serials = new List<int>();
        private int _filter;
        private int _seenAtOpen;
        private int _key;

        protected override string UxmlLocation => "IntelPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StatusText => _status?.text ?? string.Empty;
        public string KindsText => _kinds?.text ?? string.Empty;
        public string SectionText => _secList?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public int RowCount => _serials.Count;
        public string RowText(int i) => i >= 0 && i < _serials.Count ? _rows[i].Label.text : string.Empty;
        public bool RowOutdated(int i) => i >= 0 && i < _serials.Count && _rows[i].Row.ClassListContains("in-row-outdated");
        public Button RowMapButton(int i) => i >= 0 && i < _serials.Count ? _rows[i].Map : null;
        public bool RowMapVisible(int i) => i >= 0 && i < _serials.Count && !_rows[i].Map.ClassListContains("uk-hidden");
        public Button FilterButton(int i) => i >= 0 && i < FilterCount ? _filters[i] : null;
        public int CurrentFilter => _filter;
        public Button CloseButton => _close;
        public Button HelpButton => _help;

        private void Awake()
        {
            Instance = this;
            NotificationCenter.RegisterOpenHandler("intel_new", OpenFromNotification);
            NotificationCenter.RegisterOpenHandler("intel_raid", OpenFromNotification);
            NotificationCenter.RegisterOpenHandler("intel_interrupted", OpenFromNotification);
        }

        private static bool OpenFromNotification(NotificationEntry entry)
        {
            Open(entry?.Type?.Id == "intel_raid" ? IntelCatalog.KindRaid : null);
            return true;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开情报面板；<paramref name="kind"/> 不为空时筛到这一类（突袭预报通知点进来 = 突袭预报）。已开着就切过去。</summary>
        public static void Open(string kind)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingKind = kind;
                return;
            }
            Instance.SelectFilter(FilterOf(kind));
            if (!IsOpen)
            {
                Instance.SetOpen(true);
            }
        }

        /// <summary>情报键（默认 Y）：开着再按一次关闭。</summary>
        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }
            Open(null);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        private static int FilterOf(string kind)
        {
            IReadOnlyList<IntelKindDef> all = IntelCatalog.All;
            for (int i = 0; i < all.Count && i < FilterCount - 1; i++)
            {
                if (all[i].Id == kind)
                {
                    return i + 1;
                }
            }
            return 0;
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open(_pendingKind);
            }
        }

        /// <summary>行模板（可变数量的行由模板克隆，红线 5；资源成对释放见 <see cref="OnDestroy"/>）。</summary>
        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("IntelRow");
            _templateLoading = false;
            if (this == null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            _rowTemplate = t;
            if (t == null)
            {
                Log.Error("[IntelPanelUIToolkit] 加载 IntelRow 失败，情报列表行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("IntelRoot");
            _title = root.Q<Label>("IntelTitle");
            _help = root.Q<Button>("IntelHelp");
            _close = root.Q<Button>("IntelClose");
            _secStatus = root.Q<Label>("IntelSecStatus");
            _status = root.Q<Label>("IntelStatus");
            _kinds = root.Q<Label>("IntelKinds");
            for (int i = 0; i < FilterCount; i++)
            {
                _filters[i] = root.Q<Button>("IntelFilter" + i);
                int captured = i;
                _filters[i].clicked += () => SelectFilter(captured);
            }
            _secList = root.Q<Label>("IntelSecList");
            _empty = root.Q<Label>("IntelEmpty");
            _list = root.Q<ScrollView>("IntelList");
            _footer = root.Q<Label>("IntelFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            UiTooltip.Attach(_status, () => new TooltipContent { Title = GameText.Get("intel.panel.sec.status"), Body = GameText.Get("codex.intel.listening_post.body") });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        public void SelectFilter(int index)
        {
            _filter = Math.Max(0, Math.Min(FilterCount - 1, index));
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
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
                GuidanceHooks.Raise(GuidanceHooks.IntelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                CampaignState s = CampaignSession.Current;
                _seenAtOpen = IntelService.StateOf(s)?.SeenSerial ?? 0;
                IntelService.MarkSeen(s); // 记为看过（这次打开期间仍标“新”，下次打开不再标）。
                _key = 0;
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

        public void OpenCodex() => MechanicCodex.Open("codex.intel.listening_post");

        /// <summary>“在地图上查看”：关掉面板，打开战略地图并对准这条突袭预报的箭头（整支箭头都在视野里）。</summary>
        public void ClickMap(int row)
        {
            if (row < 0 || row >= _serials.Count)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            IntelRecord r = IntelService.Find(s, _serials[row]);
            if (r == null || r.Kind != IntelCatalog.KindRaid)
            {
                return;
            }
            Vector2 tail = IntelService.ArrowTail(s, r);
            Vector2 head = IntelService.ArrowHead(s, r);
            SetOpen(false);
            StrategicMapUIToolkit.OpenCentered((tail.x + head.x) * 0.5, (tail.y + head.y) * 0.5, IntelCatalog.MapArrowCells * 1.5);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        private string CurrentKind()
        {
            IReadOnlyList<IntelKindDef> all = IntelCatalog.All;
            return _filter <= 0 || _filter > all.Count ? null : all[_filter - 1].Id;
        }

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            long now = GameClock.Ticks;
            int key = HashCode.Combine(IntelService.Revision, _filter, (int)GameText.Language, GameSettings.Revision, s != null ? s.GetHashCode() : 0,
                now / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0);
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("intel.panel.title");
            _help.text = GameText.Get("intel.panel.help");
            _close.text = GameText.Get("intel.panel.close");
            _secStatus.text = GameText.Get("intel.panel.sec.status");
            _status.text = s != null ? IntelService.StatusText(s, now) : string.Empty;
            var sb = new StringBuilder();
            IReadOnlyList<IntelKindDef> kinds = IntelCatalog.All;
            for (int i = 0; i < kinds.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(IntelService.KindStateText(s, kinds[i], now));
            }
            _kinds.text = sb.ToString();
            for (int i = 0; i < FilterCount; i++)
            {
                bool shown = i == 0 || i <= kinds.Count;
                _filters[i].EnableInClassList("uk-hidden", !shown);
                _filters[i].text = i == 0 ? GameText.Get("intel.panel.filter_all") : shown ? kinds[i - 1].Name : string.Empty;
                _filters[i].EnableInClassList("in-selected", i == _filter);
            }
            string kind = CurrentKind();
            List<IntelRecord> list = s != null ? IntelService.List(s, kind, now) : new List<IntelRecord>();
            _secList.text = GameText.Format("intel.panel.sec.list", list.Count, CountValid(list, now));
            RebuildRows(s, list, now);
            bool empty = list.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            if (empty)
            {
                IntelCatalog.TryGet(kind, out IntelKindDef kd);
                _empty.text = kind == null || kd == null
                    ? GameText.Get("intel.panel.empty")
                    : GameText.Format("intel.panel.empty_filter", IntelService.KindStateText(s, kd, now));
            }
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("intel.panel.footer"));
        }

        private static int CountValid(List<IntelRecord> list, long now)
        {
            int n = 0;
            foreach (IntelRecord r in list)
            {
                n += IntelService.IsValid(r, now) ? 1 : 0;
            }
            return n;
        }

        private void RebuildRows(CampaignState s, List<IntelRecord> list, long now)
        {
            while (_rowTemplate != null && _rows.Count < list.Count)
            {
                int rowIndex = _rows.Count;
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("in-row-host");
                var row = host.Q<VisualElement>("IrRow");
                var label = host.Q<Label>("IrLabel");
                var btn = host.Q<Button>("IrMap");
                btn.clicked += () => ClickMap(rowIndex);
                _list.Add(host);
                _rows.Add((row, label, btn));
            }
            _serials.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < list.Count;
                (VisualElement row, Label label, Button map) = _rows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                IntelRecord r = list[i];
                _serials.Add(r.Serial);
                bool valid = IntelService.IsValid(r, now);
                bool isNew = r.Serial > _seenAtOpen;
                string text = IntelService.RowText(s, r, now);
                label.text = isNew ? GameText.Format("intel.panel.row_new", text) : text;
                row.EnableInClassList("in-row-new", isNew);
                row.EnableInClassList("in-row-outdated", !valid);
                map.text = GameText.Get("intel.panel.map");
                map.EnableInClassList("uk-hidden", !(valid && r.Kind == IntelCatalog.KindRaid));
            }
        }
    }
}
