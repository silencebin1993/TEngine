using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG4-ECO-01（FG04 FGR-ECO-001 物品表；第 4 节“悬停物品图标显示：总库存、各仓库分布、当前净速率”“物品和配方都有图鉴条目”）：物资面板。
    /// - 按层级分组列出物品表里的每种物品：占位图标（表里的颜色 + 形状，颜色之外也能区分）、名字、数量（各处合计）；没有的物品变暗，可切换“只看持有的”。
    /// - 悬停图标：总库存（固体附仓库可用 / 容量）、净速率（最近 1 游戏分钟）、分布（核心缓存 / 仓库 / 保管库 / 传送带上 / 端口缓存 / 地面 / 货舱 / 管线……，在途单列不算库存），
    ///   数据来自 <see cref="ItemDistribution"/> 缓存与 <see cref="ItemFlowStats"/> 采样，不每帧计算。
    /// - 点图标或悬停时按图鉴键：打开该物品的图鉴条目（来源与用途、产出它 / 用到它的配方）。
    /// - 入口：暂停菜单“物资”、物资键（默认 Alt+I，开着再按一次关闭）。Esc / 关闭按钮 / 点遮罩关闭；模态。分层 30074（固件库之上、图鉴之下）。
    /// 刷新：物品表 / 库存版本 / 语言 / 键位变化时立即重写文字，另外每 eco.hover.refresh_seconds 真实秒重读一次（传送带上的在途在变）。O(物品种类)，不打开时零开销。
    /// </summary>
    public sealed class ItemsPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30074;

        public static ItemsPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        /// <summary>真实时间来源（定时重读；自检可注入）。</summary>
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private sealed class Tile
        {
            public ItemDef Item;
            public VisualElement Root;
            public VisualElement Icon;
            public Label Name;
            public Label Amount;
            public long Total;
        }

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _held;
        private Button _close;
        private Label _error;
        private Label _empty;
        private ScrollView _list;
        private Label _footer;
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly List<Label> _sections = new List<Label>();
        private readonly List<VisualElement> _grids = new List<VisualElement>();
        private int _builtCatalogRevision = -1;
        private int _key;
        private double _nextReread;

        /// <summary>只显示持有数量 &gt; 0 的物品。</summary>
        public bool HeldOnly { get; private set; }

        protected override string UxmlLocation => "ItemsPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int TileCount => _tiles.Count;
        public int VisibleTileCount
        {
            get
            {
                int n = 0;
                foreach (Tile t in _tiles)
                {
                    if (!t.Root.ClassListContains("uk-hidden"))
                    {
                        n++;
                    }
                }
                return n;
            }
        }
        public string TitleText => _title?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string ErrorText => _error != null && !_error.ClassListContains("uk-hidden") ? _error.text : string.Empty;
        public Button HeldButton => _held;
        public Button CloseButton => _close;
        public int SectionCount => _sections.Count;

        /// <summary>某种物品的格子（自检点击 / 悬停用）；没有返回 null。</summary>
        public VisualElement TileOf(string itemId)
        {
            foreach (Tile t in _tiles)
            {
                if (t.Item.Id == itemId)
                {
                    return t.Root;
                }
            }
            return null;
        }

        public string TileAmountText(string itemId)
        {
            foreach (Tile t in _tiles)
            {
                if (t.Item.Id == itemId)
                {
                    return t.Amount.text;
                }
            }
            return string.Empty;
        }

        public string TileNameText(string itemId)
        {
            foreach (Tile t in _tiles)
            {
                if (t.Item.Id == itemId)
                {
                    return t.Name.text;
                }
            }
            return string.Empty;
        }

        public Color TileIconColor(string itemId)
        {
            foreach (Tile t in _tiles)
            {
                if (t.Item.Id == itemId)
                {
                    return t.Item.Shape == "ring" ? t.Icon.style.borderTopColor.value : t.Icon.style.backgroundColor.value;
                }
            }
            return Color.clear;
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
            _root = root.Q<VisualElement>("ItemsPanelRoot");
            _title = root.Q<Label>("ItemsPanelTitle");
            _count = root.Q<Label>("ItemsPanelCount");
            _held = root.Q<Button>("ItemsPanelHeld");
            _close = root.Q<Button>("ItemsPanelClose");
            _error = root.Q<Label>("ItemsPanelError");
            _empty = root.Q<Label>("ItemsPanelEmpty");
            _list = root.Q<ScrollView>("ItemsList");
            _footer = root.Q<Label>("ItemsPanelFooter");
            _close.clicked += () => SetOpen(false);
            _held.clicked += ToggleHeldOnly;
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _tiles.Clear();
            _sections.Clear();
            _grids.Clear();
            _builtCatalogRevision = -1;
            _key = 0;
        }

        public void ToggleHeldOnly()
        {
            HeldOnly = !HeldOnly;
            _key = 0;
            Refresh();
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
                GuidanceHooks.Raise(GuidanceHooks.EconomyItemsFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                ItemDistribution.Invalidate();
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                UiTooltip.Hide();
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

        /// <summary>按物品表 / 筛选 / 语言 / 设置重写（键不变时只在定时重读到期时重读数量）。
        /// 库存版本不进键：家园物流运转时端口每个内核步都改库存，跟着它重读就成了每帧重建分布（与 <see cref="ItemDistribution"/> 同一口径）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            IReadOnlyList<ItemDef> items = ItemCatalog.Items;
            if (_builtCatalogRevision != ItemCatalog.Revision)
            {
                BuildTiles(items);
            }
            CampaignState state = CampaignSession.Current;
            double now = Clock();
            int key = HashCode.Combine(ItemCatalog.Revision, HeldOnly, (int)GameText.Language, GameSettings.Revision,
                state != null ? state.GetHashCode() : 0);
            if (key == _key && now < _nextReread)
            {
                return;
            }
            _key = key;
            _nextReread = now + ItemDistribution.RefreshSeconds;

            _title.text = GameText.Get("items.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            _held.text = GameText.Get(HeldOnly ? "items.panel.show_all" : "items.panel.show_held");
            // B22：图标是占位（颜色 + 形状），页脚写明（美术阶段整体替换，DEBT-FG4ECO01-02）。
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("items.panel.footer")) + "\n" + GameText.Get("items.panel.placeholder");
            string err = ItemCatalog.LoadError;
            _error.EnableInClassList("uk-hidden", err == null);
            _error.text = err != null ? GameText.Format("items.panel.error", err) : string.Empty;

            int held = 0;
            foreach (Tile t in _tiles)
            {
                ItemDistributionView v = ItemDistribution.Get(state, t.Item);
                t.Total = v?.Total ?? 0;
                if (t.Total > 0)
                {
                    held++;
                }
                t.Name.text = t.Item.Name;
                t.Amount.text = ItemHover.AmountText(t.Item, t.Total);
                t.Root.EnableInClassList("ip-tile-zero", t.Total <= 0);
                t.Root.EnableInClassList("uk-hidden", HeldOnly && t.Total <= 0);
            }
            // 分组标题：组里没有可见格子时一起隐藏。
            for (int g = 0; g < _grids.Count; g++)
            {
                bool any = false;
                foreach (VisualElement child in _grids[g].Children())
                {
                    if (!child.ClassListContains("uk-hidden"))
                    {
                        any = true;
                        break;
                    }
                }
                _grids[g].EnableInClassList("uk-hidden", !any);
                _sections[g].EnableInClassList("uk-hidden", !any);
                _sections[g].text = GameText.Get(TierTextKey((string)_sections[g].userData));
            }
            _count.text = GameText.Format("items.panel.count", held, _tiles.Count);
            bool empty = held == 0 && (HeldOnly || _tiles.Count == 0);
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("items.panel.empty") : string.Empty;
        }

        /// <summary>层级 → 分组标题文本键（逐个写出，静态检查能找到每个键）。</summary>
        private static string TierTextKey(string tier) => tier switch
        {
            "raw" => "item.tier.raw",
            "intermediate" => "item.tier.intermediate",
            "product" => "item.tier.product",
            "endgame" => "item.tier.endgame",
            "expedition" => "item.tier.expedition",
            "key" => "item.tier.key",
            "fluid" => "item.tier.fluid",
            _ => "item.tier.digital",
        };

        private void BuildTiles(IReadOnlyList<ItemDef> items)
        {
            _list.Clear();
            _tiles.Clear();
            _sections.Clear();
            _grids.Clear();
            string tier = null;
            VisualElement grid = null;
            foreach (ItemDef item in items)
            {
                if (item.Tier != tier || grid == null)
                {
                    tier = item.Tier;
                    var section = new Label { userData = tier };
                    section.AddToClassList("ip-section");
                    grid = new VisualElement();
                    grid.AddToClassList("ip-grid");
                    _list.Add(section);
                    _list.Add(grid);
                    _sections.Add(section);
                    _grids.Add(grid);
                }
                var t = new Tile { Item = item, Root = new VisualElement() };
                t.Root.AddToClassList("ip-tile");
                var box = new VisualElement();
                box.AddToClassList("ip-icon-box");
                t.Icon = new VisualElement();
                t.Icon.AddToClassList("ip-icon");
                t.Icon.AddToClassList("ip-shape-" + item.Shape);
                if (item.Shape == "ring")
                {
                    t.Icon.style.borderTopColor = item.Color;
                    t.Icon.style.borderRightColor = item.Color;
                    t.Icon.style.borderBottomColor = item.Color;
                    t.Icon.style.borderLeftColor = item.Color;
                }
                else
                {
                    t.Icon.style.backgroundColor = item.Color;
                }
                box.Add(t.Icon);
                var texts = new VisualElement();
                texts.AddToClassList("ip-texts");
                t.Name = new Label();
                t.Name.AddToClassList("ip-name");
                t.Amount = new Label();
                t.Amount.AddToClassList("ip-amount");
                texts.Add(t.Name);
                texts.Add(t.Amount);
                t.Root.Add(box);
                t.Root.Add(texts);
                ItemDef captured = item;
                t.Root.RegisterCallback<ClickEvent>(_ => OpenCodex(captured));
                UiTooltip.Attach(t.Root, () => HoverContent(captured));
                grid.Add(t.Root);
                _tiles.Add(t);
            }
            _builtCatalogRevision = ItemCatalog.Revision;
        }

        /// <summary>点图标：打开这种物品的图鉴条目（不解锁：没拿到的远征物 / 关键材料仍是剪影 + 获取途径）。</summary>
        public static void OpenCodex(ItemDef item)
        {
            if (item == null)
            {
                return;
            }
            UiTooltip.Hide();
            MechanicCodex.Open(MechanicCodex.ItemEntryId(item.Id), unlock: false);
        }

        /// <summary>悬停提示：总库存、净速率、分布、图鉴链接（数据来自缓存）。任何画物品图标的界面都可以用它。</summary>
        public static TooltipContent HoverContent(ItemDef item)
        {
            CampaignState state = CampaignSession.Current;
            ItemDistributionView v = ItemDistribution.Get(state, item);
            // FG5-E2E-01（B13，DEBT-FG5RND01-07）：仓库容量吃到研发加成时，容量这一行后面写明加成与来源节点。
            string bonus = ItemHover.ResearchLine(state, item);
            var c = new TooltipContent
            {
                Title = item.Name,
                Body = ItemHover.TotalLine(state, item, v) + (bonus != null ? "\n" + bonus : string.Empty) + "\n" + ItemHover.RateLine(state, item) + "\n" + RecipeBook.FormText(item),
                CodexEntryId = MechanicCodex.ItemEntryId(item.Id),
                CodexEntry = InputDisplay.ExpandActionTokens(GameText.Get("item.hover.codex")),
            };
            var parts = new List<KeyValuePair<string, string>>();
            ItemHover.Parts(item, v, parts);
            foreach (KeyValuePair<string, string> p in parts)
            {
                c.Sources.Add(new TooltipSource(p.Key, p.Value));
            }
            if (parts.Count > 0)
            {
                c.Total = GameText.Get("item.hover.dist_title") + " " + ItemHover.AmountText(item, v?.Total ?? 0);
            }
            return c;
        }
    }
}
