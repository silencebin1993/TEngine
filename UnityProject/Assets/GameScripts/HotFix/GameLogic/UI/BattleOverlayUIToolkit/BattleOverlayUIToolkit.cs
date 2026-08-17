using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Battle;

namespace GameLogic
{
    /// <summary>
    /// UI Toolkit 版覆盖面板（battle-ui-toolkit/story-004）：卡组（Tab）/商店（B）/图鉴（V）
    /// 三个只读/轻交互面板共享同一份 <c>BattleOverlays.uxml</c>，单一控制器管理（D1），
    /// 不接 [Window]/CellStageFlow._hub，照抄 <see cref="BattleHudToolkit"/>/
    /// <see cref="BattleMetabolicUIToolkit"/> 的"常驻单例、每帧轮询自控显隐"模式（D2）。
    /// 三面板互斥（同时最多一个显示），<see cref="ShowDeck"/>/<see cref="ShowShop"/>/
    /// <see cref="ShowCodex"/>/<see cref="CloseAll"/> 是唯一显隐入口，Tab/B/V 按键处理器与
    /// execute_code 验收断言复用同一份实现，不重造平行状态机（D11）。
    /// Pause/Esc（<c>BattlePauseUI</c>）本 story 完全不碰，留给 005（D13）。
    /// </summary>
    public class BattleOverlayUIToolkit : MonoBehaviour
    {
        private enum PanelKind
        {
            None,
            Deck,
            Shop,
            Codex,
        }

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;

        // Deck
        private VisualElement _deckRoot;
        private VisualElement _routeDistList;
        private ScrollView _ownedCardList;

        // Shop
        private VisualElement _shopRoot;
        private Label _shopTitle;
        private readonly VisualElement[] _shopSlotRoot = new VisualElement[ShopSystem.SlotCount];
        private readonly Label[] _itemName = new Label[ShopSystem.SlotCount];
        private readonly Label[] _itemDesc = new Label[ShopSystem.SlotCount];
        private readonly Label[] _itemCost = new Label[ShopSystem.SlotCount];
        private readonly Button[] _btnBuy = new Button[ShopSystem.SlotCount];

        // Codex
        private VisualElement _codexRoot;
        private Label _enemySectionTitle;
        private ScrollView _enemyList;
        private Label _cardSectionTitle;
        private ScrollView _unlockList;

        private PanelKind _current = PanelKind.None;

        /// <summary>供 execute_code 验收探针只读访问。</summary>
        public bool IsDeckVisible => _current == PanelKind.Deck;
        public bool IsShopVisible => _current == PanelKind.Shop;
        public bool IsCodexVisible => _current == PanelKind.Codex;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BattleOverlays");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");

            if (this == null)
            {
                // 组件在异步加载期间被销毁（例如热更域重载）。
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // 多个 UIDocument 共用同一份 PanelSettings 时，兄弟节点绘制顺序取决于
            // 各自异步加载完成的竞态顺序（非确定性）——实测中 HUD/Metabolic 的
            // 常驻面板会不可预期地盖住本控制器的覆盖面板。显式给一个高于默认 0 的
            // sortingOrder，让 Deck/Shop/Codex 打开时稳定画在 HUD/Metabolic 之上。
            _document.sortingOrder = 10;

            // UIDocument.rootVisualElement 在刚赋值 panelSettings 后偶发仍为 null
            // （面板尚未在本帧完成挂载，实测复现），有限帧数轮询等它就绪，避免
            // CacheNodes() 对 null 根节点查询直接崩溃、控制器永久半初始化。
            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[BattleOverlayUIToolkit] rootVisualElement 等待超时，覆盖面板未初始化。");
                return;
            }
            CacheNodes();
            ApplyDisplay();
        }

        private void CacheNodes()
        {
            _deckRoot = _root.Q<VisualElement>("BattleDeckUI");
            _routeDistList = _deckRoot?.Q<VisualElement>("RouteDistList");
            _ownedCardList = _deckRoot?.Q<ScrollView>("OwnedCardList");
            Button btnCloseDeck = _deckRoot?.Q<Button>("BtnCloseDeck");
            if (btnCloseDeck != null)
            {
                btnCloseDeck.clicked += CloseAll;
            }

            _shopRoot = _root.Q<VisualElement>("BattleShopUI");
            _shopTitle = _shopRoot?.Q<Label>("ShopTitle");
            for (int i = 0; i < ShopSystem.SlotCount; i++)
            {
                // ShopSlotN 是 <ui:Instance> 生成的 TemplateContainer，真正的字段节点在其内部，
                // 命名固定为 "ShopSlot"（同 ShopSlot.uxml 根节点名，D7）。
                VisualElement container = _shopRoot?.Q<VisualElement>("ShopSlot" + i);
                VisualElement slot = container?.Q<VisualElement>("ShopSlot");
                _shopSlotRoot[i] = slot;
                if (slot == null)
                {
                    continue;
                }

                _itemName[i] = slot.Q<Label>("ItemName");
                _itemDesc[i] = slot.Q<Label>("ItemDesc");
                _itemCost[i] = slot.Q<Label>("ItemCost");
                _btnBuy[i] = slot.Q<Button>("BtnBuy");

                int slotIndex = i;
                if (_btnBuy[i] != null)
                {
                    // D9：点击瞬间读最新数据，不缓存。
                    _btnBuy[i].clicked += () => GameRoot.CellStage?.Shop?.TryBuy(slotIndex);
                }
            }

            Button btnRefresh = _shopRoot?.Q<Button>("BtnRefresh");
            if (btnRefresh != null)
            {
                btnRefresh.clicked += () => GameRoot.CellStage?.Shop?.TryRefresh();
            }
            Button btnCloseShop = _shopRoot?.Q<Button>("BtnCloseShop");
            if (btnCloseShop != null)
            {
                // Shop 不暂停战斗，关闭只改自身 display，不碰 _paused。
                btnCloseShop.clicked += CloseAll;
            }

            _codexRoot = _root.Q<VisualElement>("BattleCodexUI");
            _enemySectionTitle = _codexRoot?.Q<Label>("EnemySectionTitle");
            _enemyList = _codexRoot?.Q<ScrollView>("EnemyList");
            _cardSectionTitle = _codexRoot?.Q<Label>("CardSectionTitle");
            _unlockList = _codexRoot?.Q<ScrollView>("UnlockList");
            Button btnCloseCodex = _codexRoot?.Q<Button>("BtnCloseCodex");
            if (btnCloseCodex != null)
            {
                btnCloseCodex.clicked += CloseAll;
            }
        }

        public void ShowDeck() => SetPanel(PanelKind.Deck);
        public void ShowShop() => SetPanel(PanelKind.Shop);
        public void ShowCodex() => SetPanel(PanelKind.Codex);
        public void CloseAll() => SetPanel(PanelKind.None);

        private void TogglePanel(PanelKind kind)
        {
            SetPanel(_current == kind ? PanelKind.None : kind);
        }

        /// <summary>D4：唯一显隐入口——设置目标面板并强制隐藏另外两个（互斥）。</summary>
        private void SetPanel(PanelKind kind)
        {
            _current = kind;
            ApplyDisplay();
        }

        private void ApplyDisplay()
        {
            if (_deckRoot != null)
            {
                _deckRoot.style.display = _current == PanelKind.Deck ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_shopRoot != null)
            {
                _shopRoot.style.display = _current == PanelKind.Shop ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_codexRoot != null)
            {
                _codexRoot.style.display = _current == PanelKind.Codex ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void Update()
        {
            CellStageFlow cell = GameRoot.CellStage;
            bool running = cell != null && cell.IsRunning;

            if (running)
            {
                // D3：自身轮询 Input.GetKeyDown，不依赖 CellDebugHud.OnGUI 的 Event.current。
                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    TogglePanel(PanelKind.Deck);
                }
                else if (Input.GetKeyDown(KeyCode.B))
                {
                    TogglePanel(PanelKind.Shop);
                }
                else if (Input.GetKeyDown(KeyCode.V))
                {
                    TogglePanel(PanelKind.Codex);
                }
            }
            else if (_current != PanelKind.None)
            {
                SetPanel(PanelKind.None);
            }

            if (_root == null || !running)
            {
                return;
            }

            switch (_current)
            {
                case PanelKind.Deck:
                    RefreshDeck(cell);
                    break;
                case PanelKind.Shop:
                    RefreshShop(cell);
                    break;
                case PanelKind.Codex:
                    RefreshCodex(cell);
                    break;
            }
        }

        /// <summary>D10：逐字段对齐 CellDebugHud.DrawDeck，不新发明字段含义。</summary>
        private void RefreshDeck(CellStageFlow cell)
        {
            if (_routeDistList != null)
            {
                _routeDistList.Clear();
                var counts = new int[8];
                cell.Deck.CopyRouteCounts(counts);
                for (int i = 1; i < counts.Length; i++)
                {
                    if (counts[i] > 0)
                    {
                        _routeDistList.Add(new Label($"{CellDebugHud.RouteName((CardRoute)i)}　{counts[i]}"));
                    }
                }
            }

            if (_ownedCardList != null)
            {
                _ownedCardList.Clear();
                IReadOnlyList<DeckEntry> entries = cell.Deck.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    DeckEntry e = entries[i];
                    string stack = e.Stack > 1 ? $" x{e.Stack}" : "";
                    _ownedCardList.Add(new Label(
                        $"<color={CellDebugHud.RarityColor(e.Spec.Rarity)}>{e.Spec.Name}</color>{stack}"));
                }
            }
        }

        /// <summary>D10：逐字段对齐 CellDebugHud.DrawShop。不碰 _paused——Shop 不暂停战斗。</summary>
        private void RefreshShop(CellStageFlow cell)
        {
            ShopSystem shop = cell.Shop;
            if (shop == null)
            {
                return;
            }

            if (_shopTitle != null)
            {
                _shopTitle.text = $"局内商店　营养质 {cell.Wallet.Nutrient:F0}";
            }

            for (int i = 0; i < ShopSystem.SlotCount; i++)
            {
                ShopItemSpec item = shop.GetSlot(i);
                bool soldOut = shop.IsSoldOut(i);

                if (_itemName[i] != null)
                {
                    _itemName[i].text = item.Name;
                }
                if (_itemDesc[i] != null)
                {
                    _itemDesc[i].text = item.Desc;
                }
                if (_itemCost[i] != null)
                {
                    _itemCost[i].text = $"价格 {item.Cost:F0}";
                }

                if (_shopSlotRoot[i] != null)
                {
                    if (soldOut)
                    {
                        _shopSlotRoot[i].AddToClassList("sold-out");
                    }
                    else
                    {
                        _shopSlotRoot[i].RemoveFromClassList("sold-out");
                    }
                }

                if (_btnBuy[i] != null)
                {
                    _btnBuy[i].text = soldOut ? "已售出" : "购买";
                    _btnBuy[i].SetEnabled(!soldOut);
                }
            }
        }

        /// <summary>D10：逐字段对齐 CellDebugHud.DrawCodex。</summary>
        private void RefreshCodex(CellStageFlow cell)
        {
            CodexRegistry codex = cell.Codex;
            if (codex == null)
            {
                return;
            }

            if (_enemySectionTitle != null)
            {
                _enemySectionTitle.text = $"敌人　{codex.DiscoveredEnemyIds.Count}";
            }
            if (_enemyList != null)
            {
                _enemyList.Clear();
                foreach (int id in codex.DiscoveredEnemyIds)
                {
                    EnemySpec e = DataRegistry.Instance.GetEnemy(id);
                    _enemyList.Add(new Label(e != null ? e.Name : $"#{id}"));
                }
            }

            if (_cardSectionTitle != null)
            {
                _cardSectionTitle.text = $"已解锁器官 / 基因　{codex.DiscoveredCardIds.Count}";
            }
            if (_unlockList != null)
            {
                _unlockList.Clear();
                foreach (int id in codex.DiscoveredCardIds)
                {
                    CardSpec c = DataRegistry.Instance.GetCard(id);
                    if (c == null)
                    {
                        _unlockList.Add(new Label($"#{id}"));
                        continue;
                    }

                    string kind = c.ContentKind == ContentKind.Organelle ? "器官"
                        : c.ContentKind == ContentKind.Gene ? "基因" : "卡牌";
                    _unlockList.Add(new Label(
                        $"[{kind}] <color={CellDebugHud.RarityColor(c.Rarity)}>{CellDebugHud.RarityLabel(c.Rarity)}</color> {c.Name}\n{c.Desc}"));
                }
            }
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
