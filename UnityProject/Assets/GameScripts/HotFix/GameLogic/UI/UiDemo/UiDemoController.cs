using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.UiDemo
{
    /// <summary>
    /// 独立的 UI Toolkit 流程验证页：验证信息层级、滚动列表、选择、确认与反馈，
    /// 不接管或修改现有战斗逻辑。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class UiDemoController : MonoBehaviour
    {
        [Serializable]
        public sealed class DemoShopItem
        {
            public string Name;
            public string Description;
            public int Cost;
            public bool IsAvailable = true;
        }

        private enum PanelKind
        {
            Shop,
            Deck,
            Codex
        }

        private sealed class ItemRow
        {
            public DemoShopItem Item;
            public Button Button;
        }

        [SerializeField] private int _credits = 120;
        [SerializeField] private List<DemoShopItem> _shopItems = new List<DemoShopItem>();

        private readonly List<ItemRow> _itemRows = new List<ItemRow>();
        private UIDocument _document;
        private VisualElement _root;
        private ScrollView _itemList;
        private VisualElement _confirmOverlay;
        private Label _creditsLabel;
        private Label _panelTitle;
        private Label _panelSubtitle;
        private Label _selectedItemLabel;
        private Label _selectedDetailLabel;
        private Label _confirmTextLabel;
        private Label _toastLabel;
        private Button _shopTab;
        private Button _deckTab;
        private Button _codexTab;
        private Button _pauseButton;
        private Button _refreshButton;
        private Button _purchaseButton;
        private Button _cancelButton;
        private Button _confirmButton;
        private Button _skillOne;
        private Button _skillTwo;
        private Button _skillThree;
        private Button _skillFour;
        private DemoShopItem _selectedItem;
        private PanelKind _activePanel;
        private bool _isBound;

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[UiDemoController] UIDocument 缺少 VisualTreeAsset 或 PanelSettings。", this);
                return;
            }

            CacheNodes();
            EnsureDemoItems();
            BindEvents();
            ShowPanel(PanelKind.Shop);
            RefreshView();
            SetToast("演示已准备完成：先选择一项升级，再验证确认流程。");
        }

        private void OnDisable()
        {
            UnbindEvents();
            _itemRows.Clear();
            _root = null;
        }

        private void CacheNodes()
        {
            _itemList = _root.Q<ScrollView>("itemList");
            _confirmOverlay = _root.Q<VisualElement>("confirmOverlay");
            _creditsLabel = _root.Q<Label>("creditsLabel");
            _panelTitle = _root.Q<Label>("panelTitle");
            _panelSubtitle = _root.Q<Label>("panelSubtitle");
            _selectedItemLabel = _root.Q<Label>("selectedItemLabel");
            _selectedDetailLabel = _root.Q<Label>("selectedDetailLabel");
            _confirmTextLabel = _root.Q<Label>("confirmTextLabel");
            _toastLabel = _root.Q<Label>("toastLabel");
            _shopTab = _root.Q<Button>("shopTab");
            _deckTab = _root.Q<Button>("deckTab");
            _codexTab = _root.Q<Button>("codexTab");
            _pauseButton = _root.Q<Button>("pauseButton");
            _refreshButton = _root.Q<Button>("refreshButton");
            _purchaseButton = _root.Q<Button>("purchaseButton");
            _cancelButton = _root.Q<Button>("cancelButton");
            _confirmButton = _root.Q<Button>("confirmButton");
            _skillOne = _root.Q<Button>("skillOne");
            _skillTwo = _root.Q<Button>("skillTwo");
            _skillThree = _root.Q<Button>("skillThree");
            _skillFour = _root.Q<Button>("skillFour");
        }

        private void BindEvents()
        {
            if (_isBound)
            {
                return;
            }

            _shopTab.clicked += ShowShop;
            _deckTab.clicked += ShowDeck;
            _codexTab.clicked += ShowCodex;
            _pauseButton.clicked += ShowPauseHint;
            _refreshButton.clicked += RefreshShop;
            _purchaseButton.clicked += OpenConfirm;
            _cancelButton.clicked += CloseConfirm;
            _confirmButton.clicked += ConfirmPurchase;
            _skillOne.clicked += UseSkillOne;
            _skillTwo.clicked += UseSkillTwo;
            _skillThree.clicked += UseSkillThree;
            _skillFour.clicked += UseSkillFour;
            _isBound = true;
        }

        private void UnbindEvents()
        {
            if (!_isBound)
            {
                return;
            }

            _shopTab.clicked -= ShowShop;
            _deckTab.clicked -= ShowDeck;
            _codexTab.clicked -= ShowCodex;
            _pauseButton.clicked -= ShowPauseHint;
            _refreshButton.clicked -= RefreshShop;
            _purchaseButton.clicked -= OpenConfirm;
            _cancelButton.clicked -= CloseConfirm;
            _confirmButton.clicked -= ConfirmPurchase;
            _skillOne.clicked -= UseSkillOne;
            _skillTwo.clicked -= UseSkillTwo;
            _skillThree.clicked -= UseSkillThree;
            _skillFour.clicked -= UseSkillFour;
            _isBound = false;
        }

        private void EnsureDemoItems()
        {
            if (_shopItems.Count > 0)
            {
                return;
            }

            _shopItems.AddRange(new[]
            {
                new DemoShopItem { Name = "甲壳加固", Description = "生命上限 +20，适合承受高风险目标。", Cost = 35 },
                new DemoShopItem { Name = "营养回流", Description = "每次吞噬后额外获得少量营养质。", Cost = 45 },
                new DemoShopItem { Name = "快速分裂", Description = "移动速度提升，但会略微增加资源消耗。", Cost = 60 },
                new DemoShopItem { Name = "毒素滤膜", Description = "污染积累速度降低，适合长时间探索。", Cost = 50 },
                new DemoShopItem { Name = "突变催化", Description = "下一次进化获得额外选项。", Cost = 80 },
                new DemoShopItem { Name = "感知延伸", Description = "更早发现高价值与高风险目标。", Cost = 40 },
                new DemoShopItem { Name = "修复组织", Description = "立刻恢复部分生命值。", Cost = 30 }
            });
        }

        private void ShowShop() => ShowPanel(PanelKind.Shop);
        private void ShowDeck() => ShowPanel(PanelKind.Deck);
        private void ShowCodex() => ShowPanel(PanelKind.Codex);

        private void ShowPanel(PanelKind panel)
        {
            _activePanel = panel;
            _shopTab.EnableInClassList("is-active", panel == PanelKind.Shop);
            _deckTab.EnableInClassList("is-active", panel == PanelKind.Deck);
            _codexTab.EnableInClassList("is-active", panel == PanelKind.Codex);

            switch (panel)
            {
                case PanelKind.Deck:
                    _panelTitle.text = "构筑";
                    _panelSubtitle.text = "固定入口，长内容使用同一块可滚动区域。";
                    break;
                case PanelKind.Codex:
                    _panelTitle.text = "图鉴";
                    _panelSubtitle.text = "分类、搜索和详情会占用同一内容区。";
                    break;
                default:
                    _panelTitle.text = "商店";
                    _panelSubtitle.text = "选择一项升级；列表可滚动。";
                    break;
            }

            RefreshView();
        }

        private void RefreshView()
        {
            _creditsLabel.text = $"营养质 {_credits}";
            _itemList.Clear();
            _itemRows.Clear();

            if (_activePanel != PanelKind.Shop)
            {
                AddEmptyState(_activePanel == PanelKind.Deck ? "构筑内容将在此以可滚动列表呈现。" : "图鉴内容将在此提供分类、搜索与详情。" );
                _purchaseButton.text = "选择升级";
                _purchaseButton.SetEnabled(false);
                _selectedItem = null;
                _selectedItemLabel.text = "这是同一个内容容器的切换验证。";
                _selectedDetailLabel.text = "不会在 HUD 上叠加第二套面板。";
                return;
            }

            foreach (DemoShopItem item in _shopItems)
            {
                AddShopItem(item);
            }

            UpdateSelection();
        }

        private void AddShopItem(DemoShopItem item)
        {
            Button button = new Button(() => SelectItem(item));
            button.AddToClassList("shop-item");

            VisualElement textBlock = new VisualElement();
            Label title = new Label(item.Name);
            title.AddToClassList("shop-item-title");
            Label detail = new Label(item.Description);
            detail.AddToClassList("shop-item-detail");
            textBlock.Add(title);
            textBlock.Add(detail);

            Label cost = new Label(item.IsAvailable ? $"{item.Cost}" : "已获得");
            cost.AddToClassList("shop-item-cost");
            button.Add(textBlock);
            button.Add(cost);
            button.EnableInClassList("is-selected", item == _selectedItem);
            button.SetEnabled(item.IsAvailable);
            _itemList.Add(button);
            _itemRows.Add(new ItemRow { Item = item, Button = button });
        }

        private void AddEmptyState(string message)
        {
            Label empty = new Label(message);
            empty.AddToClassList("meta-text");
            empty.AddToClassList("panel-note");
            _itemList.Add(empty);
        }

        private void SelectItem(DemoShopItem item)
        {
            _selectedItem = item;
            foreach (ItemRow row in _itemRows)
            {
                row.Button.EnableInClassList("is-selected", row.Item == item);
            }
            UpdateSelection();
            SetToast($"已选择：{item.Name}。购买前会显示确认弹窗。");
        }

        private void UpdateSelection()
        {
            bool canPurchase = _selectedItem != null && _selectedItem.IsAvailable && _selectedItem.Cost <= _credits;
            _purchaseButton.SetEnabled(canPurchase);

            if (_selectedItem == null)
            {
                _selectedItemLabel.text = "选择一项升级以查看详情";
                _selectedDetailLabel.text = "列表内容过多时不会压缩文字，而是保持可滚动。";
                _purchaseButton.text = "选择升级";
                return;
            }

            _selectedItemLabel.text = _selectedItem.Name;
            _selectedDetailLabel.text = _selectedItem.Cost <= _credits
                ? $"{_selectedItem.Description} 消耗 {_selectedItem.Cost} 营养质。"
                : $"{_selectedItem.Description} 还差 {_selectedItem.Cost - _credits} 营养质。";
            _purchaseButton.text = canPurchase ? "购买升级" : "资源不足";
        }

        private void OpenConfirm()
        {
            if (_selectedItem == null)
            {
                return;
            }

            _confirmTextLabel.text = $"购买“{_selectedItem.Name}”将消耗 {_selectedItem.Cost} 营养质。";
            _confirmOverlay.RemoveFromClassList("is-hidden");
        }

        private void CloseConfirm()
        {
            _confirmOverlay.AddToClassList("is-hidden");
        }

        private void ConfirmPurchase()
        {
            if (_selectedItem == null || _selectedItem.Cost > _credits || !_selectedItem.IsAvailable)
            {
                CloseConfirm();
                return;
            }

            _credits -= _selectedItem.Cost;
            _selectedItem.IsAvailable = false;
            SetToast($"已购买：{_selectedItem.Name}。资源与列表状态已同步更新。");
            CloseConfirm();
            RefreshView();
        }

        private void RefreshShop()
        {
            SetToast("刷新用于验证次级操作：不会覆盖当前主购买动作。");
        }

        private void ShowPauseHint()
        {
            SetToast("暂停入口固定在顶栏；正式接入时由唯一模态入口接管输入与暂停状态。");
        }

        private void UseSkillOne() => SetToast("吞噬：验证高频操作在底部固定位置。");
        private void UseSkillTwo() => SetToast("逃逸：验证高频操作在底部固定位置。");
        private void UseSkillThree() => SetToast("突变：验证高频操作在底部固定位置。");
        private void UseSkillFour() => SetToast("防御：验证高频操作在底部固定位置。");

        private void SetToast(string message)
        {
            if (_toastLabel != null)
            {
                _toastLabel.text = message;
            }
        }
    }
}
