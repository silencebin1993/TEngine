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
    /// UI Toolkit 版覆盖面板（battle-ui-toolkit/story-004 起）：卡组（Tab）/商店（B）/图鉴（V）/
    /// 暂停菜单（Esc，story-005）四个面板共享同一份 <c>BattleOverlays.uxml</c>，单一控制器管理（D1），
    /// 不接 [Window]/CellStageFlow._hub，照抄 <see cref="BattleHudToolkit"/>/
    /// <see cref="BattleMetabolicUIToolkit"/> 的"常驻单例、每帧轮询自控显隐"模式（D2）。
    /// 四面板互斥（同时最多一个显示），<see cref="ShowDeck"/>/<see cref="ShowShop"/>/
    /// <see cref="ShowCodex"/>/<see cref="CloseAll"/> 与私有 <c>SetPanel</c> 是唯一显隐入口，
    /// Tab/B/V/Esc 按键处理器与 execute_code 验收断言复用同一份实现，不重造平行状态机（D11）。
    /// Pause 面板的 <c>CellStageFlow._paused</c> 同步收在 <c>SetPanel</c> 唯一入口按边沿处理
    /// （story-005 D3），防止切到 Deck/Shop/Codex 时忘记复位导致游戏卡死。
    /// </summary>
    public class BattleOverlayUIToolkit : MonoBehaviour
    {
        private enum PanelKind
        {
            None,
            Deck,
            Shop,
            Codex,
            Pause,
        }

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _overlayRoot;

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

        // Pause
        private VisualElement _pauseRoot;
        private Button _btnResume;
        private Button _btnOpenMetabolicFromPause;
        private Button _btnOpenDeckFromPause;
        private Button _btnAbandon;

        private PanelKind _current = PanelKind.None;

        /// <summary>供 execute_code 验收探针只读访问。</summary>
        public bool IsDeckVisible => _current == PanelKind.Deck;
        public bool IsShopVisible => _current == PanelKind.Shop;
        public bool IsCodexVisible => _current == PanelKind.Codex;
        public bool IsPauseVisible => _current == PanelKind.Pause;

        /// <summary>story-002：供 BattleMetabolicUIToolkit（M×Esc 互斥）跨控制器调用，最小暴露，同 BattleMetabolicUIToolkit.Instance 先例。</summary>
        public static BattleOverlayUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
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
            // uxml 根节点 OverlayRoot 恒为 width:100%/height:100%，四个子面板各自 display:none 时
            // 自己没托管到隐藏——默认 pickingMode.Position 会在整局游戏里持续吞掉最上层
            // （sortingOrder=10）全屏范围的每一次点击，Draft/Metabolic 等下层面板全部点不动。
            // 子面板按钮各自已有 pickingMode.Position，不依赖父节点转发，Ignore 不影响它们。
            _overlayRoot = _root.Q<VisualElement>("OverlayRoot");
            if (_overlayRoot != null)
            {
                _overlayRoot.pickingMode = PickingMode.Ignore;
            }

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

            _pauseRoot = _root.Q<VisualElement>("BattlePauseUI");
            _btnResume = _pauseRoot?.Q<Button>("BtnResume");
            _btnOpenMetabolicFromPause = _pauseRoot?.Q<Button>("BtnOpenMetabolicFromPause");
            _btnOpenDeckFromPause = _pauseRoot?.Q<Button>("BtnOpenDeckFromPause");
            _btnAbandon = _pauseRoot?.Q<Button>("BtnAbandon");

            if (_btnResume != null)
            {
                _btnResume.clicked += CloseAll;
            }
            if (_btnOpenDeckFromPause != null)
            {
                _btnOpenDeckFromPause.clicked += () => SetPanel(PanelKind.Deck);
            }
            if (_btnOpenMetabolicFromPause != null)
            {
                _btnOpenMetabolicFromPause.clicked += () =>
                {
                    SetPanel(PanelKind.None);
                    BattleMetabolicUIToolkit.Instance?.SetVisible(true);
                };
            }
            if (_btnAbandon != null)
            {
                _btnAbandon.clicked += () =>
                {
                    GameRoot.CellStage?.MarkAbandoned();
                    GameRoot.EndRun();
                };
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

        /// <summary>
        /// story-002：Esc 键分支单独抽出（供 execute_code 反射直调，理由同
        /// BattleMetabolicUIToolkit.HandleMKeyToggle()）。即将从非 Pause 切到 Pause 时，若代谢
        /// 面板已开，先关代谢再开 Pause，避免同屏叠加；反向（关 Pause）不联动代谢。
        /// </summary>
        private void HandleEscKeyToggle()
        {
            if (_current != PanelKind.Pause && BattleMetabolicUIToolkit.Instance != null && BattleMetabolicUIToolkit.Instance.IsPanelVisible)
            {
                BattleMetabolicUIToolkit.Instance.SetVisible(false);
            }
            TogglePanel(PanelKind.Pause);
        }

        /// <summary>
        /// D4/D3：唯一显隐入口——设置目标面板并强制隐藏其余（互斥）。
        /// 暂停同步收在这里按边沿处理，防止"从 Pause 切到 Deck/Shop/Codex 忘记复位 _paused，游戏卡死"
        /// 的死锁 bug；四个按钮/Esc 只需要各自调 SetPanel/CloseAll，暂停状态自动跟随。
        /// </summary>
        private void SetPanel(PanelKind kind)
        {
            bool willPause = kind == PanelKind.Pause;
            bool wasPause = _current == PanelKind.Pause;
            if (willPause != wasPause)
            {
                GameRoot.CellStage?.SetPaused(willPause);
            }

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
            if (_pauseRoot != null)
            {
                _pauseRoot.style.display = _current == PanelKind.Pause ? DisplayStyle.Flex : DisplayStyle.None;
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
                // D9：Paused 是 Draft 和 Pause 共用的同一个字段——Esc 只有在"当前没有其它原因
                // 导致的暂停"（本控制器自己置的 Pause，或压根没暂停）时才处理，避免选卡三选一
                // 显示期间 Esc 又把 Pause 叠加到 Draft 上。
                else if (Input.GetKeyDown(KeyCode.Escape) && (_current == PanelKind.Pause || !cell.Paused))
                {
                    // story-003（topdown-hud-projectile-fix）R4：装配面板打开时，第一次 Esc 只关它本身，
                    // 不同帧弹 Pause（避免双层遮挡）；面板已关时才走原 HandleEscKeyToggle，第二次 Esc 才进 Pause。
                    if (_current != PanelKind.Pause && BattleCarrierUIToolkit.Instance != null && BattleCarrierUIToolkit.Instance.IsPanelOpen)
                    {
                        BattleCarrierUIToolkit.Instance.SetPanelOpen(false);
                    }
                    else
                    {
                        HandleEscKeyToggle();
                    }
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
            if (Instance == this)
            {
                Instance = null;
            }
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
