using System;
using System.Collections.Generic;
using BinGames.Sim;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.Cards;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Grid;
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

        /// <summary>001 R4 锁定的图鉴六类分类维度。供 execute_code 断言直调 <see cref="SetCodexCategory"/>。</summary>
        public enum CodexCategory
        {
            Organelle,
            Gene,
            Slot,
            Terrain,
            Status,
            Enemy,
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

        // Codex（story-005：搜索 + 六类分类 Tab，D4 在既有骨架上扩容）
        private VisualElement _codexRoot;
        private TextField _codexSearchField;
        private ScrollView _codexEntryList;
        private readonly Button[] _codexTabButtons = new Button[CodexTabNodeNames.Length];
        private CodexCategory _codexCategory = CodexCategory.Organelle;
        private string _codexSearchText = string.Empty;

        private static readonly string[] CodexTabNodeNames =
        {
            "TabOrganelle", "TabGene", "TabSlot", "TabTerrain", "TabStatus", "TabEnemy",
        };

        // 图鉴 tooltip（story-005 R4：hover 触发，≤3 行，供本控制器 Deck 卡牌与
        // BattleCarrierUIToolkit 基因/器官图标复用同一份浮层，不各自重造）。
        private Label _tooltip;

        // 首局教程（story-005 R5）：进入 Running 数秒后自动弹一次图鉴，本次运行内只弹一次
        // （无跨局存档，"首局"只能理解为"本次运行首次进入战斗"），static 抗热更域重载重弹。
        private static bool _codexAutoTutorialShown;
        private float _codexAutoTutorialTimer = -1f;
        private const float CodexAutoTutorialDelay = 3f;

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
            _codexSearchField = _codexRoot?.Q<TextField>("CodexSearchField");
            _codexSearchField?.RegisterValueChangedCallback(evt => _codexSearchText = evt.newValue ?? string.Empty);
            _codexEntryList = _codexRoot?.Q<ScrollView>("CodexEntryList");
            for (int i = 0; i < CodexTabNodeNames.Length; i++)
            {
                Button tab = _codexRoot?.Q<Button>(CodexTabNodeNames[i]);
                _codexTabButtons[i] = tab;
                if (tab != null)
                {
                    CodexCategory category = (CodexCategory)i;
                    tab.clicked += () => SetCodexCategory(category);
                }
            }
            Button btnCloseCodex = _codexRoot?.Q<Button>("BtnCloseCodex");
            if (btnCloseCodex != null)
            {
                btnCloseCodex.clicked += CloseAll;
            }

            CreateTooltip();

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

        /// <summary>供 execute_code 断言直调，不模拟点击。</summary>
        public void SetCodexCategory(CodexCategory category) => _codexCategory = category;
        public CodexCategory CurrentCodexCategory => _codexCategory;

        /// <summary>供 execute_code 断言直调，不模拟输入。</summary>
        public void SetCodexSearchText(string text) => _codexSearchText = text ?? string.Empty;

        /// <summary>供 execute_code 断言只读访问，不参与显示逻辑本身。</summary>
        public int CodexEntryCount => _codexEntryList?.childCount ?? 0;

        /// <summary>供 execute_code 断言只读访问：R5 首局教程是否已自动弹出过。</summary>
        public bool CodexAutoTutorialShown => _codexAutoTutorialShown;

        private void CreateTooltip()
        {
            _tooltip = new Label();
            _tooltip.AddToClassList("codex-tooltip");
            _tooltip.pickingMode = PickingMode.Ignore;
            _tooltip.style.position = Position.Absolute;
            _tooltip.style.display = DisplayStyle.None;
            _root.Add(_tooltip);
        }

        /// <summary>
        /// story-005 R4：hover 触发的图鉴摘要浮层，≤3 行。供本控制器 Deck 列表与
        /// <see cref="BattleCarrierUIToolkit"/> 的基因/器官图标跨控制器复用同一份浮层
        /// （两者共用 BattleHudPanelSettings，世界坐标可直接互换，同 <c>_dragGhost</c> 先例）。
        /// </summary>
        public void ShowTooltip(string text, Vector2 worldPosition)
        {
            if (_tooltip == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            _tooltip.text = TrimToLines(text, 3);
            Vector2 local = _root.WorldToLocal(worldPosition);
            _tooltip.style.left = local.x + 16f;
            _tooltip.style.top = local.y + 16f;
            _tooltip.style.display = DisplayStyle.Flex;
        }

        public void HideTooltip()
        {
            if (_tooltip != null)
            {
                _tooltip.style.display = DisplayStyle.None;
            }
        }

        private static string TrimToLines(string text, int maxLines)
        {
            string[] lines = text.Split('\n');
            return lines.Length <= maxLines ? text : string.Join("\n", lines, 0, maxLines);
        }

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
                UpdateCodexAutoTutorial();

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
            else
            {
                _codexAutoTutorialTimer = -1f;
                if (_current != PanelKind.None)
                {
                    SetPanel(PanelKind.None);
                }
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

        /// <summary>
        /// R5：进入 Running 后计时 <see cref="CodexAutoTutorialDelay"/> 秒（避开开局出生提示遮挡），
        /// 若此时没有其它面板占用则自动 <see cref="ShowCodex"/> 一次；本次运行内只弹一次。
        /// </summary>
        private void UpdateCodexAutoTutorial()
        {
            if (_codexAutoTutorialShown)
            {
                return;
            }
            if (_codexAutoTutorialTimer < 0f)
            {
                _codexAutoTutorialTimer = 0f;
                return;
            }
            _codexAutoTutorialTimer += Time.deltaTime;
            if (_codexAutoTutorialTimer < CodexAutoTutorialDelay)
            {
                return;
            }
            _codexAutoTutorialShown = true;
            if (_current == PanelKind.None)
            {
                ShowCodex();
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
                        var label = new Label($"{CellDebugHud.RouteName((CardRoute)i)}　{counts[i]}");
                        label.AddToClassList("list-row");
                        _routeDistList.Add(label);
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
                    var label = new Label(
                        $"<color={CellDebugHud.RarityColor(e.Spec.Rarity)}>{e.Spec.Name}</color>{stack}");
                    label.AddToClassList("list-row");
                    // R4：hover 卡牌图标显示 Description 摘要。
                    string desc = e.Spec.Desc;
                    label.RegisterCallback<PointerEnterEvent>(evt => ShowTooltip(desc, evt.position));
                    label.RegisterCallback<PointerLeaveEvent>(evt => HideTooltip());
                    _ownedCardList.Add(label);
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

        // D12（005）：契约基因 id 集合，只算一次，供 GeneSource 区分"契约基因/模块基因"来源文案。
        private static readonly HashSet<string> ContractGeneIds = new HashSet<string>(GeneCatalog.AllIds);

        /// <summary>
        /// story-005：图鉴六类分类维度（001 R4）+ 搜索 + tooltip 摘要复用同一份 Description。
        /// 六类各自数据源：器官/基因/敌人走 <see cref="CodexRegistry"/>（002 D1 出口，含发现态）；
        /// 插槽/地形/状态是纯代码枚举/小目录，本局无发现态，直接全量列出（001 R4 决议：地形复核后
        /// 确认 <c>TerrainCatalog</c> 存在，六类维持不降为五类）。
        /// </summary>
        private void RefreshCodex(CellStageFlow cell)
        {
            CodexRegistry codex = cell.Codex;
            if (codex == null || _codexEntryList == null)
            {
                return;
            }

            for (int i = 0; i < _codexTabButtons.Length; i++)
            {
                Button tab = _codexTabButtons[i];
                if (tab == null)
                {
                    continue;
                }
                if ((int)_codexCategory == i)
                {
                    tab.AddToClassList("codex-tab-active");
                }
                else
                {
                    tab.RemoveFromClassList("codex-tab-active");
                }
            }

            _codexEntryList.Clear();
            string filter = _codexSearchText;

            switch (_codexCategory)
            {
                case CodexCategory.Organelle:
                    foreach (OrganelleCodexEntry e in codex.AllOrganelleEntries())
                    {
                        if (!MatchesFilter(e.DisplayName, e.Description, filter))
                        {
                            continue;
                        }
                        OrganelleDef def = OrganelleCatalog.Get(e.Id);
                        AddCodexRow(e.DisplayName, e.Description, SlotSummary(def?.AllowedSlotTypes), "器官目录", true);
                    }
                    break;

                case CodexCategory.Gene:
                    foreach (GeneCodexEntry e in codex.AllGeneEntries())
                    {
                        if (!MatchesFilter(e.DisplayName, e.Description, filter))
                        {
                            continue;
                        }
                        string source = ContractGeneIds.Contains(e.Id) ? "契约基因" : "模块基因";
                        AddCodexRow(e.DisplayName, e.Description, null, source, true);
                    }
                    break;

                case CodexCategory.Slot:
                    foreach (SlotType slot in Enum.GetValues(typeof(SlotType)))
                    {
                        string name = CodexTaxonomy.SlotTypeName(slot);
                        string desc = CodexTaxonomy.SlotTypeDescription(slot);
                        if (!MatchesFilter(name, desc, filter))
                        {
                            continue;
                        }
                        AddCodexRow(name, desc, null, "插槽类型", true);
                    }
                    break;

                case CodexCategory.Terrain:
                    foreach (string id in TerrainCatalog.AllIds)
                    {
                        string name = CodexTaxonomy.TerrainName(id);
                        string desc = CodexTaxonomy.TerrainDescription(id);
                        if (!MatchesFilter(name, desc, filter))
                        {
                            continue;
                        }
                        string[] tags = TerrainCatalog.GetTags(id);
                        string extra = tags == null || tags.Length == 0 ? "无固有标记" : "标记：" + string.Join("、", tags);
                        AddCodexRow(name, desc, extra, "地形", true);
                    }
                    break;

                case CodexCategory.Status:
                    foreach (SimStatus status in Enum.GetValues(typeof(SimStatus)))
                    {
                        if (status == SimStatus.None)
                        {
                            continue;
                        }
                        string name = CodexTaxonomy.StatusName(status);
                        string desc = CodexTaxonomy.StatusDescription(status);
                        if (!MatchesFilter(name, desc, filter))
                        {
                            continue;
                        }
                        AddCodexRow(name, desc, null, "状态效果", true);
                    }
                    break;

                case CodexCategory.Enemy:
                    foreach (EnemyCodexEntry e in codex.AllEnemyEntries())
                    {
                        if (!MatchesFilter(e.Name, e.Description, filter))
                        {
                            continue;
                        }
                        string extra = null;
                        if (e.Discovered)
                        {
                            EnemySpec spec = DataRegistry.Instance.GetEnemy(e.Id);
                            if (spec != null)
                            {
                                extra = $"生命 {spec.Health:F0}　速度 {spec.MaxSpeed:F1}";
                            }
                        }
                        AddCodexRow(e.Discovered ? e.Name : "？？？", e.Discovered ? e.Description : "尚未遭遇，击杀或吞噬后解锁。",
                            extra, "敌人", e.Discovered);
                    }
                    break;
            }
        }

        private static bool MatchesFilter(string name, string description, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }
            return (name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (description != null && description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string SlotSummary(HashSet<SlotType> allowedSlotTypes)
        {
            if (allowedSlotTypes == null)
            {
                return "不限插槽";
            }
            var names = new List<string>();
            foreach (SlotType slot in allowedSlotTypes)
            {
                names.Add(CodexTaxonomy.SlotTypeName(slot));
            }
            return "限：" + string.Join("、", names);
        }

        /// <summary>
        /// 一条图鉴条目：名称 + Description + 效果数值/允许槽位（extra，可为空）+ 来源。
        /// 未发现条目（<paramref name="revealed"/>=false）name/description 已由调用方遮罩，不在此二次遮罩。
        /// </summary>
        private void AddCodexRow(string name, string description, string extra, string source, bool revealed)
        {
            var row = new VisualElement();
            row.AddToClassList("codex-row");

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("codex-row-name");
            row.Add(nameLabel);

            var descLabel = new Label(description);
            descLabel.AddToClassList("list-row");
            row.Add(descLabel);

            if (revealed && !string.IsNullOrEmpty(extra))
            {
                var extraLabel = new Label(extra);
                extraLabel.AddToClassList("codex-row-extra");
                row.Add(extraLabel);
            }

            var sourceLabel = new Label($"来源：{source}");
            sourceLabel.AddToClassList("codex-row-source");
            row.Add(sourceLabel);

            _codexEntryList.Add(row);
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
