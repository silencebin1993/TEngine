using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-02（FG03 FGR-LOG-006“施工队列可以查看、可以调整优先级”；第 4 节“优先建造这一片”；FG00 B03 / B05 / B06 / B12）：施工队列面板。
    /// - 每一行一张未完成的施工单（建筑虚影、传送带规划、搬迁），按机器领单的顺序排（优先级 → 先放的先建），写明名字、状态（取料 / 运料 / 施工 % /
    ///   等待材料缺多少 / 排队 / 没有劳动力 / 路径受阻）、进度条、优先级；行内“提高 / 降低”改优先级，“定位”镜头飞过去，“取消”全额退回已到材料。
    /// - 顶部一行劳动力与库存；“优先建造这一片”按钮进入建造模式的拉框模式。空队列有说明（怎样才会有施工）。
    /// 数据全部来自 <see cref="HomeValleyConstruction.CollectQueue"/>（与悬停、建造栏同一写法）。入口：快捷键（默认 Alt+B）、建造栏按钮。
    /// 模态（盖在世界上，Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 秒（真实时间）按施工进度刷新，O(施工单数)；关着时每帧 O(1)。
    /// </summary>
    public sealed class ConstructionQueuePanelUIToolkit : UiKitPanelHost
    {
        /// <summary>通知 30040 之上、字幕 30050 之下（拒绝原因字幕盖得住它），暂停菜单 30070 之下。</summary>
        public const int Order = 30045;

        private const float RefreshSeconds = 0.25f;

        public static ConstructionQueuePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Label _labor;
        private Button _prioritize;
        private Button _rebuildAll;
        private Label _empty;
        private ScrollView _list;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<HomeValleyConstruction.QueueEntry> _entries = new List<HomeValleyConstruction.QueueEntry>();
        private float _timer;

        protected override string UxmlLocation => "ConstructionQueuePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public bool RowTemplateReady => _rowTemplate != null;
        public string RowName(int i) => Row(i)?.Q<Label>("CqName")?.text ?? string.Empty;
        public string RowStatus(int i) => Row(i)?.Q<Label>("CqStatus")?.text ?? string.Empty;
        public string RowPriority(int i) => Row(i)?.Q<Label>("CqPriority")?.text ?? string.Empty;
        public Button RowButton(int i, string name) => Row(i)?.Q<Button>(name);
        public string RowOrderId(int i) => i >= 0 && i < VisibleRowCount && i < _entries.Count ? _entries[i].Order?.WorkOrderId : null;
        /// <summary>FG3-LOG-03：这一行是被摧毁的传送带虚影时，它的规划 ID（否则 null）。</summary>
        public string RowDestroyedPlanId(int i) => i >= 0 && i < VisibleRowCount && i < _entries.Count ? _entries[i].DestroyedPlanId : null;
        public Button RebuildAllButton => _rebuildAll;
        public bool RebuildAllVisible => _rebuildAll != null && !_rebuildAll.ClassListContains("uk-hidden");
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string LaborText => _labor?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public Button CloseButton => _close;
        public Button PrioritizeButton => _prioritize;

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
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SetOpen(true);
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_rowTemplate != null || _rowTemplateLoading)
            {
                return;
            }
            _rowTemplateLoading = true;
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ConstructionQueueRow");
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
                Log.Error("[ConstructionQueuePanelUIToolkit] 加载 ConstructionQueueRow 失败，队列行不可用。");
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
            _root = root.Q<VisualElement>("ConstructionQueueRoot");
            _title = root.Q<Label>("ConstructionQueueTitle");
            _count = root.Q<Label>("ConstructionQueueCount");
            _close = root.Q<Button>("ConstructionQueueClose");
            _labor = root.Q<Label>("ConstructionQueueLabor");
            _prioritize = root.Q<Button>("ConstructionQueuePrioritize");
            _rebuildAll = root.Q<Button>("ConstructionQueueRebuildAll");
            _empty = root.Q<Label>("ConstructionQueueEmpty");
            _list = root.Q<ScrollView>("ConstructionQueueList");
            _footer = root.Q<Label>("ConstructionQueueFooter");
            _close.clicked += () => SetOpen(false);
            _prioritize.clicked += StartPrioritizeArea;
            if (_rebuildAll != null)
            {
                _rebuildAll.clicked += RebuildAll;
            }
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
                GuidanceHooks.Raise(GuidanceHooks.BuildQueueFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh();
        }

        /// <summary>按当前施工单重建全部行（打开时每 0.25 秒一次，O(施工单数)）。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            HomeValleyConstruction.CollectQueue(state, _entries);
            _title.text = GameText.Get("build.queue.title");
            _close.text = GameText.Get("build.queue.close");
            _prioritize.text = GameText.Format("ui.build.btn_prioritize", InputDisplay.ForAction(GameActionId.PrioritizeArea));
            int destroyedCells = HomeValleyConstruction.DestroyedGhostCount(state);
            if (_rebuildAll != null)
            {
                _rebuildAll.EnableInClassList("uk-hidden", destroyedCells == 0);
                _rebuildAll.text = GameText.Format("build.queue.rebuild_all", destroyedCells);
            }
            _count.text = GameText.Format("build.queue.count", _entries.Count);
            int labor = Mathf.Max(0, HomeValleyConstruction.LaborCount);
            _labor.text = GameText.Format("build.queue.labor", labor, state != null ? Mathf.FloorToInt(state.Scrap) : 0)
                          + (HomeValleyConstruction.NoLabor ? "\n" + GameText.Get("build.status.no_labor") : string.Empty);
            _labor.EnableInClassList("cq-status-warn", HomeValleyConstruction.NoLabor);
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("build.queue.hint"));
            bool empty = _entries.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Format("build.queue.empty", InputDisplay.ForAction(GameActionId.OpenBuildMenu)) : string.Empty;
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < _entries.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                row.Q<Button>("CqUp").clicked += () => OnRowAction(index, +1);
                row.Q<Button>("CqDown").clicked += () => OnRowAction(index, -1);
                row.Q<Button>("CqLocate").clicked += () => OnLocate(index);
                Button rebuild = row.Q<Button>("CqRebuild");
                if (rebuild != null)
                {
                    rebuild.clicked += () => OnRebuild(index);
                }
                row.Q<Button>("CqCancel").clicked += () => OnCancel(index);
                _list.Add(row);
                _rows.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shown = i < _entries.Count;
                row.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                HomeValleyConstruction.QueueEntry e = _entries[i];
                row.Q<Label>("CqName").text = e.Name;
                Label status = row.Q<Label>("CqStatus");
                status.text = e.Status;
                // FG3-LOG-03：被摧毁的传送带虚影没有施工单——不排优先级，只有“重建 / 定位 / 取消（移除虚影）”。
                bool destroyed = e.IsDestroyedGhost;
                bool warn = destroyed || e.Order.State == WorkOrderState.Waiting || (HomeValleyConstruction.NoLabor && e.Order.State == WorkOrderState.Ready);
                status.EnableInClassList("cq-status-warn", warn);
                row.Q<VisualElement>("CqBarFill").style.width = Length.Percent(Mathf.Clamp01(e.Fraction) * 100f);
                row.Q<Label>("CqPriority").text = destroyed ? string.Empty
                    : GameText.Format("build.queue.row_priority", HomeValleyConstruction.PriorityName(e.Order.Priority));
                Button up = row.Q<Button>("CqUp");
                Button down = row.Q<Button>("CqDown");
                up.text = GameText.Get("build.queue.up");
                down.text = GameText.Get("build.queue.down");
                up.EnableInClassList("uk-hidden", destroyed);
                down.EnableInClassList("uk-hidden", destroyed);
                up.SetEnabled(!destroyed && e.Order.Priority < HomeValleyConstruction.PriorityMax);
                down.SetEnabled(!destroyed && e.Order.Priority > HomeValleyConstruction.PriorityMin);
                Button rebuildBtn = row.Q<Button>("CqRebuild");
                if (rebuildBtn != null)
                {
                    rebuildBtn.text = GameText.Get("build.queue.rebuild");
                    rebuildBtn.EnableInClassList("uk-hidden", !destroyed);
                }
                row.Q<Button>("CqLocate").text = GameText.Get("build.queue.locate");
                row.Q<Button>("CqCancel").text = GameText.Get("build.queue.cancel");
            }
            VisibleRowCount = _entries.Count;
        }

        private void OnRowAction(int index, int delta)
        {
            if (index >= _entries.Count)
            {
                return;
            }
            WorkOrderRecord o = _entries[index].Order;
            if (o == null)
            {
                return;
            }
            if (HomeValleyConstruction.SetPriority(CampaignSession.Current, o.WorkOrderId, o.Priority + delta))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.UiClick);
            }
            Refresh();
        }

        private void OnLocate(int index)
        {
            if (index >= _entries.Count)
            {
                return;
            }
            Vector2 at = _entries[index].Position;
            SetOpen(false);
            WorldView.FlyTo(HomeValleyLayout.RegionId, at);
        }

        private void OnCancel(int index)
        {
            if (index >= _entries.Count)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            HomeValleyConstruction.QueueEntry e = _entries[index];
            if (e.IsDestroyedGhost)
            {
                if (HomeValleyConstruction.RemoveDestroyedGhost(state, e.DestroyedPlanId))
                {
                    Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck, GameText.Format("build.queue.ghost_removed", e.Name));
                }
                Refresh();
                return;
            }
            if (HomeValleyConstruction.CancelSite(state, e.Order))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck, GameText.Format("build.queue.cancelled", e.Name));
            }
            Refresh();
        }

        /// <summary>FG3-LOG-03（FGR-LOG-027）：确认重建一处被摧毁的传送带（按原设置生成施工单）。</summary>
        private void OnRebuild(int index)
        {
            if (index >= _entries.Count || !_entries[index].IsDestroyedGhost)
            {
                return;
            }
            HomeValleyConstruction.QueueEntry e = _entries[index];
            if (HomeValleyConstruction.RebuildDestroyed(CampaignSession.Current, e.DestroyedPlanId))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck, GameText.Format("build.queue.rebuilt", e.Name));
            }
            Refresh();
        }

        /// <summary>FG3-LOG-03：“全部重建”——每一处被摧毁的传送带都按原设置生成施工单。</summary>
        public void RebuildAll()
        {
            int n = HomeValleyConstruction.RebuildAllDestroyed(CampaignSession.Current);
            if (n > 0)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.CommandAck,
                    GameText.Format("build.queue.rebuilt", n.ToString(CultureInfo.InvariantCulture)));
            }
            Refresh();
        }

        /// <summary>“优先建造这一片”：关掉队列，打开建造模式的拉框模式（与快捷键同一路径）。</summary>
        public void StartPrioritizeArea()
        {
            SetOpen(false);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            mode?.SetPrioritizeMode(true);
        }
    }
}
