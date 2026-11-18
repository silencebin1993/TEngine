using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG12-EVT-06（FG10 FGR-EVT-051，FG13 FGU-39“商队交易：商品、价格、库存”）：流浪商队交易面板。
    /// 每行：类别、名称、买到后的效果、价格（含仓库现有）、库存；材料不够时行里写明差多少（与建造材料同一说明 <see cref="BuildMaterials.DescribeShortfall"/>），右侧“买入”。
    /// 商队不在时写明何时会来；停留中写停靠点与剩余时间。入口：商队到达通知（通知中心“打开”）、暂停菜单“流浪商队”、主视图点商队（<c>CaravanViews</c>）。
    /// Esc / 关闭按钮 / 点遮罩关闭；模态。刷新只在数据版本 / 语言 / 秒数变化时重建，O(商品数)。
    /// </summary>
    public sealed class CaravanTradePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30079; // 与事件历史同层：都是暂停菜单的子面板，不会同时打开

        public static CaravanTradePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Label _stay;
        private Button _close;
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private readonly List<Label> _rowLabels = new List<Label>();
        private readonly List<Button> _rowButtons = new List<Button>();
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private readonly List<string> _wareIds = new List<string>();
        private int _key;
        private string _note;

        protected override string UxmlLocation => "CaravanTradePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount ? _rowLabels[i].text : string.Empty;
        public Button BuyButton(int i) => i >= 0 && i < VisibleRowCount ? _rowButtons[i] : null;
        public string StayText => _stay?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public Button CloseButton => _close;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private void Awake()
        {
            Instance = this;
            RegisterNotificationOpen();
        }

        /// <summary>商队通知（到达 / 提前离开）在通知中心里显示“打开”，点了打开本面板。</summary>
        private static void RegisterNotificationOpen() =>
            NotificationCenter.RegisterOpenHandler(CaravanService.NotifyType, _ =>
            {
                Open();
                return Instance != null && IsOpen;
            });

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
            RegisterNotificationOpen();
            _root = root.Q<VisualElement>("CaravanRoot");
            _title = root.Q<Label>("CaravanTitle");
            _count = root.Q<Label>("CaravanCount");
            _stay = root.Q<Label>("CaravanStay");
            _close = root.Q<Button>("CaravanClose");
            _list = root.Q<ScrollView>("CaravanList");
            _empty = root.Q<Label>("CaravanEmpty");
            _footer = root.Q<Label>("CaravanFooter");
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
                _note = null;
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

        /// <summary>按商队 / 库存 / 语言 / 当前游戏秒重建（键不变时 O(1)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            EventDirectorInstanceRecord inst = CaravanService.Present(state);
            long second = GameClock.Ticks / System.Math.Max(1, GameClock.StepHz);
            int key = System.HashCode.Combine(CaravanService.Revision, HomeInventory.Revision, (int)GameText.Language, state != null ? state.GetHashCode() : 0,
                inst != null ? second : 0, _note, state?.TechData ?? 0);
            if (key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("caravan.panel.title");
            _close.text = GameText.Get("caravan.panel.close");
            _stay.text = inst != null
                ? GameText.Format("caravan.panel.stay", UnityEngine.Mathf.RoundToInt(inst.X), UnityEngine.Mathf.RoundToInt(inst.Y), CalendarService.ShortDuration(CaravanService.SecondsLeft(inst)))
                : GameText.Get("caravan.panel.absent");
            IReadOnlyList<CaravanWareRecord> wares = CaravanService.Wares(state);
            _wareIds.Clear();
            var texts = new List<(string Text, bool CanBuy, bool SoldOut)>();
            foreach (CaravanWareRecord rec in wares)
            {
                CaravanWare w = CaravanService.Row(rec?.WareId);
                if (w == null)
                {
                    continue; // 表里已删掉的商品（旧档）：不显示空行
                }
                string text = CaravanService.RowText(state, rec);
                if (rec.Left <= 0)
                {
                    // 售完的行留着，写明“已售完”、买入禁用（FGU-39 价格和库存清楚）。
                    texts.Add((text + "\n" + GameText.Get("caravan.sold_out"), false, true));
                    _wareIds.Add(rec.WareId);
                    continue;
                }
                string shortfall = CaravanService.Shortfall(state, w);
                if (shortfall != null)
                {
                    text += "\n" + GameText.Format("caravan.buy.short", shortfall.Replace('\n', '；'));
                }
                bool can = CaravanService.Available(state, w);
                texts.Add((can ? text : text + "\n" + GameText.Get("caravan.buy.unavailable"), can, false));
                _wareIds.Add(rec.WareId);
            }
            _count.text = inst != null ? GameText.Format("event_director.history.count", texts.Count) : string.Empty;
            while (_rows.Count < texts.Count)
            {
                var row = new VisualElement();
                row.AddToClassList("rl-row");
                row.AddToClassList("cv-row");
                var label = new Label();
                label.AddToClassList("cv-row-label");
                var buy = new Button();
                buy.AddToClassList("mw-btn");
                buy.AddToClassList("cv-buy");
                int index = _rows.Count;
                buy.clicked += () => BuyRow(index);
                row.Add(label);
                row.Add(buy);
                _list.Add(row);
                _rows.Add(row);
                _rowLabels.Add(label);
                _rowButtons.Add(buy);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < texts.Count;
                _rows[i].EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                _rowLabels[i].text = texts[i].Text;
                _rowButtons[i].text = GameText.Get(texts[i].SoldOut ? "caravan.sold_out" : "caravan.buy");
                _rowButtons[i].SetEnabled(texts[i].CanBuy);
            }
            VisibleRowCount = texts.Count;
            bool empty = texts.Count == 0;
            _empty.EnableInClassList("uk-hidden", !(empty && inst != null));
            _empty.text = empty && inst != null ? GameText.Get("caravan.panel.empty") : string.Empty;
            string have = GameText.Format("caravan.panel.have",
                HomeInventory.Stock(state, CaravanService.AlloyId), HomeInventory.Stock(state, CaravanService.PrecisionId), HomeInventory.Stock(state, ItemCatalog.TechDataId));
            _footer.text = string.IsNullOrEmpty(_note) ? have : _note + "\n" + have;
        }

        /// <summary>点“买入”：走 <see cref="CaravanService.Buy"/>（材料不够 / 已离开时不改状态，原因写在页脚）。</summary>
        public bool BuyRow(int index)
        {
            if (index < 0 || index >= _wareIds.Count)
            {
                return false;
            }
            bool ok = CaravanService.Buy(CampaignSession.Current, _wareIds[index], out string message);
            _note = message;
            _key = 0;
            Refresh();
            return ok;
        }
    }
}
