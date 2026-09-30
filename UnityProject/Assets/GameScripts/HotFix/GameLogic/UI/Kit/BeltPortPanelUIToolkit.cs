using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-03（FG03 FGR-LOG-021“仓库有输入和输出端口；输出端口可以设置过滤器，只输出指定物品”；FGR-LOG-081“建筑：端口、输入输出缓存”；
    /// FG00 B05 / B06 / B12 / B13）：建筑的端口面板。
    /// - 每一行一个端口：输入 / 输出与朝向、接没接上传送带（没接写明在哪一格铺、朝哪）、收什么、累计 / 缓存 / 最近吞吐、问题与办法
    ///   （仓库满 / 没货 / 推不上去 / 建筑还不收发物品）。仓库输出口多一个“输出过滤”下拉框（全部可存物品 / 只输出某种 / 停止输出），选中即生效。
    /// - 顶部一行家园库存与容量。数据全部来自 <see cref="BeltPortService.CollectViews"/>（与悬停、堵塞原因同一来源）。
    /// 入口：建造模式里点一下建筑（不拖动；拖动是搬迁）。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 秒（真实时间）刷新，O(端口数)；关着时每帧 O(1)。
    /// </summary>
    public sealed class BeltPortPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>施工队列 30045 之上、字幕 30050 之下（拒绝原因字幕盖得住它），暂停菜单 30070 之下。</summary>
        public const int Order = 30046;

        private const float RefreshSeconds = 0.25f;

        public static BeltPortPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingBuilding;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _close;
        private Label _store;
        private Label _empty;
        private ScrollView _list;
        private Label _hint;
        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<BeltPortService.PortView> _views = new List<BeltPortService.PortView>();
        private readonly List<ushort> _storable = new List<ushort>(4);
        private float _timer;

        protected override string UxmlLocation => "BeltPortPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string TitleText => _title?.text ?? string.Empty;
        public string StoreText => _store?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public string RowText(int i, string name) => Row(i)?.Q<Label>(name)?.text ?? string.Empty;
        public bool RowLineVisible(int i, string name) => Row(i)?.Q<Label>(name) is Label l && !l.ClassListContains("bp-hidden");
        public DropdownField RowFilter(int i) => Row(i)?.Q<DropdownField>("BpFilter");
        public bool RowFilterVisible(int i) => Row(i)?.Q<VisualElement>("BpFilterBox") is VisualElement f && !f.ClassListContains("bp-hidden");
        public string RowPortKey(int i) => i >= 0 && i < VisibleRowCount && i < _views.Count ? _views[i].PortKey : null;
        public Button CloseButton => _close;

        private TemplateContainer Row(int i) => i >= 0 && i < VisibleRowCount && i < _rows.Count ? _rows[i] : null;

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

        /// <summary>打开某座建筑的端口面板（建造模式点建筑与自检同一入口）。</summary>
        public static void Open(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId))
            {
                return;
            }
            if (Instance == null || Instance._root == null)
            {
                _pendingBuilding = buildingId;
                return;
            }
            BuildingId = buildingId;
            if (IsOpen)
            {
                Instance.Refresh();
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingBuilding = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingBuilding != null)
            {
                string id = _pendingBuilding;
                _pendingBuilding = null;
                Open(id);
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_rowTemplate != null || _rowTemplateLoading)
            {
                return;
            }
            _rowTemplateLoading = true;
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("BeltPortRow");
            _rowTemplateLoading = false;
            if (this == null)
            {
                if (asset != null)
                {
                    GameModule.Resource.UnloadAsset(asset);
                }
                return;
            }
            _rowTemplate = asset;
            if (_rowTemplate == null)
            {
                Log.Error("[BeltPortPanelUIToolkit] 加载 BeltPortRow 失败，端口行不可用。");
                return;
            }
            Refresh();
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset template)
        {
            _rowTemplate = template;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("BeltPortRoot");
            _title = root.Q<Label>("BeltPortTitle");
            _close = root.Q<Button>("BeltPortClose");
            _store = root.Q<Label>("BeltPortStore");
            _empty = root.Q<Label>("BeltPortEmpty");
            _list = root.Q<ScrollView>("BeltPortList");
            _hint = root.Q<Label>("BeltPortHint");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
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
                GuidanceHooks.Raise(GuidanceHooks.LogisticsPortPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                BuildingId = null;
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh();
        }

        /// <summary>按当前端口状态重建全部行（打开时每 0.25 秒一次，O(端口数)）。建筑不在了就收起。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = state != null && BuildingId != null ? HomeGridService.FindBuilding(state, BuildingId) : null;
            if (IsOpen && b == null)
            {
                SetOpen(false);
                return;
            }
            BeltPortService.CollectViews(state, b, _views);
            _title.text = GameText.Format("logistics.port.title", b != null ? HomeGridService.DisplayName(b.BuildingTypeId) : string.Empty);
            _close.text = GameText.Get("logistics.port.close");
            int cap = state != null ? Campaign.Regions.HomeValleyCargo.GetStorageCapacity(state, CampaignEconomyLedger.ResourceScrap) : 0;
            _store.text = state != null ? GameText.Format("logistics.port.store_line", BeltItems.Name(BeltItems.ScrapId), state.Scrap, cap) : string.Empty;
            _hint.text = InputDisplay.ExpandActionTokens(GameText.Get("logistics.port.hint"));
            bool empty = _views.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("logistics.port.empty") : string.Empty;
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < _views.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                DropdownField filter = row.Q<DropdownField>("BpFilter");
                filter.RegisterValueChangedCallback(evt => OnFilterChosen(index, evt.newValue));
                _list.Add(row);
                _rows.Add(row);
            }
            BeltItems.CollectStorable(_storable);
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shown = i < _views.Count;
                row.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                BeltPortService.PortView v = _views[i];
                row.Q<Label>("BpTitle").text = v.Title;
                SetLine(row, "BpState", v.StateLine);
                SetLine(row, "BpAccept", v.AcceptLine);
                SetLine(row, "BpStats", v.StatsLine);
                SetLine(row, "BpIssue", v.IssueLine);
                bool filterable = v.IsOutput && v.Store;
                row.Q<VisualElement>("BpFilterBox").EnableInClassList("bp-hidden", !filterable);
                if (filterable)
                {
                    row.Q<Label>("BpFilterLabel").text = GameText.Get("logistics.port.filter_label");
                    DropdownField d = row.Q<DropdownField>("BpFilter");
                    List<string> choices = FilterChoices();
                    DropdownChoices.Apply(d, choices, GameText.Get("logistics.port.filter_all"));
                    int selected = IndexOfFilter(v.Filter);
                    d.SetEnabled(v.Bound);
                    if (selected >= 0 && selected < d.choices.Count)
                    {
                        d.SetValueWithoutNotify(d.choices[selected]);
                    }
                }
            }
            VisibleRowCount = _views.Count;
        }

        private static void SetLine(TemplateContainer row, string name, string text)
        {
            Label l = row.Q<Label>(name);
            l.text = text ?? string.Empty;
            l.EnableInClassList("bp-hidden", string.IsNullOrEmpty(text));
        }

        /// <summary>过滤选项（顺序固定）：全部可存物品、每种可存物品、停止输出。</summary>
        private List<string> FilterChoices()
        {
            var list = new List<string>(_storable.Count + 2) { BeltPortService.FilterName(BeltPortService.FilterAll) };
            foreach (ushort item in _storable)
            {
                list.Add(BeltPortService.FilterName(item));
            }
            list.Add(BeltPortService.FilterName(BeltPortService.FilterOff));
            return list;
        }

        private int IndexOfFilter(int filter)
        {
            if (filter == BeltPortService.FilterAll)
            {
                return 0;
            }
            if (filter == BeltPortService.FilterOff)
            {
                return _storable.Count + 1;
            }
            int i = _storable.IndexOf((ushort)Mathf.Clamp(filter, 0, ushort.MaxValue));
            return i >= 0 ? i + 1 : 0;
        }

        private int FilterAt(int index)
        {
            if (index <= 0)
            {
                return BeltPortService.FilterAll;
            }
            if (index > _storable.Count)
            {
                return BeltPortService.FilterOff;
            }
            return _storable[index - 1];
        }

        /// <summary>选中即生效（UI Toolkit 红线 8：定时刷新的下拉框不做“选好再点设置”）。</summary>
        private void OnFilterChosen(int rowIndex, string value)
        {
            if (rowIndex >= _views.Count || rowIndex >= _rows.Count)
            {
                return;
            }
            DropdownField d = _rows[rowIndex].Q<DropdownField>("BpFilter");
            int index = d.choices.IndexOf(value);
            SetFilter(rowIndex, FilterAt(index));
        }

        /// <summary>设置第 <paramref name="rowIndex"/> 行（仓库输出口）的过滤（下拉框与自检同一入口）。</summary>
        public bool SetFilter(int rowIndex, int filter)
        {
            if (rowIndex < 0 || rowIndex >= _views.Count || BuildingId == null)
            {
                return false;
            }
            CampaignState state = CampaignSession.Current;
            BeltPortService.PortView v = _views[rowIndex];
            bool changed = BeltPortService.TrySetFilter(state, BuildingId, v.PortKey, filter, out string reason);
            if (changed)
            {
                string name = HomeGridService.DisplayName(HomeGridService.FindBuilding(state, BuildingId)?.BuildingTypeId);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck,
                    GameText.Format("logistics.port.filter_changed", name, BeltPortService.FilterName(filter)));
            }
            else if (!string.IsNullOrEmpty(reason))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, reason);
            }
            Refresh();
            return changed;
        }
    }
}
