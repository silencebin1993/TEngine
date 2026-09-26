using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-ARCH-04（FGR-LOG-002/003 原型；FG00 B01/B02/B05/B06/B12/B15/B16/B22）：家园建造模式的入口按钮与建造栏（UI Toolkit）。
    /// - 入口：家园战略视角下左侧“建造（B）”按钮（发现性，B01）；按键同样可进入（B02，可重绑）。
    /// - 建造栏：可放置的建筑逐个列出（名字、成本与工期、已有数量 / 上限、未解锁时写明解锁条件，B06/B12）；旋转、拆除模式、退出按钮；
    ///   操作提示按当前按键绑定显示；状态行显示虚影的校验结果或最近一次操作的结果（失败红字 + “不能放置：”前缀，不只靠颜色，B15）。
    /// - 只读 <see cref="HomeValleyBuildMode.Current"/>；刷新键（模式版本、废料、建筑记录、语言、设置版本）不变的帧 O(1)（B18）。
    /// 正式建造菜单（分类、搜索、快捷栏）由 FG3-LOG-01 承接。
    /// </summary>
    public sealed class BuildModeHudUIToolkit : UiKitPanelHost
    {
        public const int Order = 30030;

        public static BuildModeHudUIToolkit Instance { get; private set; }

        private Button _entry;
        private VisualElement _panel;
        private Label _title;
        private Label _mode;
        private Button _close;
        private VisualElement _list;
        private Label _empty;
        private Button _rotate;
        private Button _demolish;
        private Label _hint;
        private Label _status;
        private Label _placeholder;
        private readonly List<Button> _items = new List<Button>();
        private readonly List<string> _itemTypes = new List<string>();
        private readonly List<BuildingGrid> _placeable = new List<BuildingGrid>();
        private string _lastKey;

        protected override string UxmlLocation => "BuildModeHud";
        protected override int SortingOrder => Order;

        /// <summary>自检可读：当前显示的建筑按钮数、入口与面板可见性、状态行文字。</summary>
        public int ItemCount => _items.Count;
        public bool EntryVisible => _entry != null && !_entry.ClassListContains("uk-hidden");
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public string StatusLabelText => _status?.text ?? string.Empty;

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
            _entry = root.Q<Button>("BuildEntryButton");
            _panel = root.Q<VisualElement>("BuildPanel");
            _title = root.Q<Label>("BuildTitle");
            _mode = root.Q<Label>("BuildMode");
            _close = root.Q<Button>("BuildClose");
            _list = root.Q<VisualElement>("BuildList");
            _empty = root.Q<Label>("BuildEmpty");
            _rotate = root.Q<Button>("BuildRotate");
            _demolish = root.Q<Button>("BuildDemolish");
            _hint = root.Q<Label>("BuildHint");
            _status = root.Q<Label>("BuildStatus");
            _placeholder = root.Q<Label>("BuildPlaceholder");

            _entry.clicked += () => HomeValleyBuildMode.Current?.Open();
            _close.clicked += () => HomeValleyBuildMode.Current?.Close();
            _rotate.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                if (m == null)
                {
                    return;
                }
                if (m.SelectedTypeId != null)
                {
                    m.RotateGhost();
                }
                else
                {
                    m.RotateHovered(CampaignSession.Current);
                }
            };
            _demolish.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetDemolishMode(!m.DemolishMode);
            };
            UiTooltip.Attach(_entry, () => new TooltipContent
            {
                Title = GameText.Get("ui.build.title"),
                Body = GameText.Format("ui.build.hint_idle", InputDisplay.ForAction(GameActionId.Rotate), InputDisplay.ForAction(GameActionId.DemolishMode)),
                Shortcut = GameActionId.OpenBuildMenu,
            });
            UiTooltip.Attach(_rotate, () => new TooltipContent { Title = GameText.Get("input.action.rotate.name"), Shortcut = GameActionId.Rotate });
            UiTooltip.Attach(_demolish, () => new TooltipContent { Title = GameText.Get("input.action.demolish_mode.name"), Shortcut = GameActionId.DemolishMode });
            _lastKey = null;
        }

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            Refresh();
        }

        /// <summary>按当前建造模式刷新（自检可直接调用）。</summary>
        public void Refresh()
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            bool available = mode != null && state != null && InputRouter.Scope == InputScope.Strategy && !InputRouter.ModalUiOpen;
            bool open = mode != null && mode.IsOpen;
            SetVisible(_entry, available && !open);
            SetVisible(_panel, open);
            if (mode == null || state == null)
            {
                _lastKey = null;
                return;
            }

            string key = string.Concat(mode.Revision.ToString(), "|", Mathf.FloorToInt(state.Scrap).ToString(), "|",
                (state.BuildingRecords?.Length ?? 0).ToString(), "|", ((int)GameText.Language).ToString(), "|", GameSettings.Revision.ToString(),
                "|", open ? "1" : "0");
            if (key == _lastKey)
            {
                return;
            }
            _lastKey = key;

            _entry.text = GameText.Format("ui.build.entry", InputDisplay.ForAction(GameActionId.OpenBuildMenu));
            if (!open)
            {
                return;
            }
            _title.text = GameText.Get("ui.build.title");
            _mode.text = mode.DemolishMode ? GameText.Format("ui.build.demolish", InputDisplay.ForAction(GameActionId.DemolishMode)) : string.Empty;
            _close.text = GameText.Get("ui.build.exit");
            _rotate.text = GameText.Format("ui.build.rotate", InputDisplay.ForAction(GameActionId.Rotate));
            _demolish.text = GameText.Format("ui.build.demolish", InputDisplay.ForAction(GameActionId.DemolishMode));
            _demolish.EnableInClassList("bm-tool-active", mode.DemolishMode);
            _placeholder.text = GameText.Get("ui.build.placeholder_note");

            HomeGridService.PlaceableTypes(_placeable);
            SyncItems(mode, state);
            SetVisible(_empty, _placeable.Count == 0);
            _empty.text = GameText.Get("ui.build.empty");

            if (mode.DemolishMode)
            {
                _hint.text = GameText.Get("ui.build.hint_demolish");
            }
            else if (mode.SelectedTypeId != null)
            {
                _hint.text = GameText.Format("ui.build.hint_place", InputDisplay.ForAction(GameActionId.Rotate));
            }
            else
            {
                _hint.text = GameText.Format("ui.build.hint_idle", InputDisplay.ForAction(GameActionId.Rotate), InputDisplay.ForAction(GameActionId.DemolishMode));
            }

            string status = mode.StatusText;
            bool error = mode.StatusIsError;
            GridPlacementResult preview = mode.Preview;
            if (preview != null)
            {
                string facing = GameText.Get(GridMath.DirTextKey(GridMath.FacingOf(preview.Rotation)));
                var ports = new List<PortPlacement>(4);
                HomeGridService.PortsFor(preview.TypeId, preview.Pivot, preview.Rotation, ports);
                status = (preview.Ok
                    ? GameText.Format("ui.build.valid", HomeGridService.DisplayName(preview.TypeId), facing)
                    : GameText.Format("ui.build.invalid", preview.Describe())) + "\n" + HomeValleyBuildMode.DescribePorts(ports);
                error = !preview.Ok;
            }
            else if (string.IsNullOrEmpty(status) && mode.HoverBuildingId != null)
            {
                BuildingRecord hovered = HomeGridService.FindBuilding(state, mode.HoverBuildingId);
                status = hovered != null ? HomeValleyBuildMode.DescribeBuilding(hovered) : string.Empty;
            }
            _status.text = status;
            _status.EnableInClassList("bm-status-error", error);
        }

        private void SyncItems(HomeValleyBuildMode mode, CampaignState state)
        {
            while (_items.Count < _placeable.Count)
            {
                int index = _items.Count;
                var b = new Button { name = "BuildItem" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("bm-item");
                b.clicked += () =>
                {
                    if (index < _itemTypes.Count)
                    {
                        HomeValleyBuildMode.Current?.Select(_itemTypes[index]);
                    }
                };
                UiTooltip.Attach(b, () => ItemTooltip(index));
                _list.Add(b);
                _items.Add(b);
            }
            _itemTypes.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                Button b = _items[i];
                if (i >= _placeable.Count)
                {
                    SetVisible(b, false);
                    continue;
                }
                BuildingGrid g = _placeable[i];
                _itemTypes.Add(g.TypeId);
                SetVisible(b, true);
                bool unlocked = HomeGridService.IsUnlocked(state, g);
                int count = HomeGridService.CountOfType(state, g.TypeId);
                string cost = HomeValleyLayout.BuildProfile.TryGetValue(g.TypeId, out (int ScrapCost, float Seconds) p)
                    ? GameText.Format("ui.build.cost", p.ScrapCost, Mathf.RoundToInt(p.Seconds))
                    : string.Empty;
                string countText = g.MaxCount > 0 ? GameText.Format("ui.build.count_limited", count, g.MaxCount) : GameText.Format("ui.build.count_unlimited", count);
                string line2 = unlocked ? cost + "  " + countText : GameText.Format("ui.build.locked_tip", GameText.Get(g.UnlockHintKey));
                b.text = HomeGridService.DisplayName(g.TypeId) + "\n" + line2;
                b.EnableInClassList("bm-item-locked", !unlocked);
                b.EnableInClassList("bm-item-selected", mode.SelectedTypeId == g.TypeId);
            }
        }

        private TooltipContent ItemTooltip(int index)
        {
            if (index >= _itemTypes.Count || !GridContent.TryGetBuilding(_itemTypes[index], out BuildingGrid g))
            {
                return null;
            }
            CampaignState state = CampaignSession.Current;
            string body = GameText.Get(g.DescKey);
            if (state != null && !HomeGridService.IsUnlocked(state, g))
            {
                body += "\n" + GameText.Format("ui.build.locked_tip", GameText.Get(g.UnlockHintKey));
            }
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsFor(g.TypeId, new GridCell(0, 0), 0, ports);
            body += "\n" + HomeValleyBuildMode.DescribePorts(ports);
            return new TooltipContent { Title = HomeGridService.DisplayName(g.TypeId), Body = body, Shortcut = GameActionId.Rotate };
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e != null)
            {
                e.EnableInClassList("uk-hidden", !visible);
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
