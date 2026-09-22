using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.WorkOrder
{
    /// <summary>ER3-WRK-01 STORY-EXECUTION-CARDS.md 第4条："工作面板显示 Ready/Reserved/InProgress/
    /// Waiting/Completed/Failed、原因和下一恢复动作"的只读展示部分。ER3-WRK-02 补齐同一份卡片第3/4条
    /// 的剩余部分：点击订单/警报行定位到指派机器（AC-UI-003）、警报去重排序展示
    /// （<see cref="HomeValleyAlarms"/>）、按选中机器调整五类工作偏好（ERD-WRK-002，
    /// <see cref="HomeValleyController.TrySetMachineWorkPriority"/>）。取消预览释放/保留哪些材料这类
    /// 完整交互仍属 ER5-INT-01/UI-04——与 <see cref="Common.EconomyHudToolkit"/>/
    /// <see cref="Common.StrategyClockHudToolkit"/>"只读展示、正式交互留后续 Story"的既定范围裁剪
    /// 一致，本 Story 只是把裁剪边界往前推了一步。
    ///
    /// 结构落在 UXML/USS（<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件。</summary>
    public sealed class WorkOrderPanelUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 12;
        private const int MaxAlertRows = 5;
        /// <summary>AC-PER-006"更新降频"：200 工作单场景下不必每帧重建整个面板的文本/查询，
        /// 5 次/秒足够肉眼感知为实时，同时把字符串分配/VisualElement 查询降到之前的 1/12。</summary>
        private const float RefreshIntervalSeconds = 0.2f;

        private static readonly WorkOrderKind[] PriorityKinds =
        {
            WorkOrderKind.Haul, WorkOrderKind.Build, WorkOrderKind.Repair, WorkOrderKind.Salvage, WorkOrderKind.Recharge,
        };

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private VisualTreeAsset _alertRowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private ScrollView _list;
        private Label _emptyLabel;
        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);

        private ScrollView _alertList;
        private readonly List<TemplateContainer> _alertRowPool = new List<TemplateContainer>(MaxAlertRows);

        private Label _priorityEmptyLabel;
        private VisualElement _priorityRows;
        private readonly Dictionary<WorkOrderKind, Button> _priorityButtons = new Dictionary<WorkOrderKind, Button>(5);

        private float _refreshTimer;

        public static WorkOrderPanelUIToolkit Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("WorkOrderPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("WorkOrderRow");
            _alertRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("AlertRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 0; // HUD 层（UI_WORKFLOW_GUIDE.md 第4节），与左上/右上两个既有 HUD 不重叠。

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[WorkOrderPanelUIToolkit] rootVisualElement 等待超时，工作单面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("WorkOrderPanelRoot");
            _list = _root.Q<ScrollView>("OrderList");
            _emptyLabel = _root.Q<Label>("EmptyLabel");
            _alertList = _root.Q<ScrollView>("AlertList");
            _priorityEmptyLabel = _root.Q<Label>("PriorityEmptyLabel");
            _priorityRows = _root.Q<VisualElement>("PriorityRows");

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                row.AddToClassList("wop-row-clickable");
                int capturedIndex = i;
                row.RegisterCallback<ClickEvent>(_ => OnOrderRowClicked(capturedIndex));
                _list.Add(row);
                _rowPool.Add(row);
            }

            for (int i = 0; i < MaxAlertRows; i++)
            {
                TemplateContainer row = _alertRowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                int capturedIndex = i;
                row.RegisterCallback<ClickEvent>(_ => OnAlertRowClicked(capturedIndex));
                _alertList.Add(row);
                _alertRowPool.Add(row);
            }

            foreach (WorkOrderKind kind in PriorityKinds)
            {
                Button button = _priorityRows.Q<Button>("PriorityBtn_" + kind);
                if (button == null)
                {
                    continue;
                }
                WorkOrderKind capturedKind = kind;
                button.clicked += () => OnPriorityButtonClicked(capturedKind);
                _priorityButtons[kind] = button;
            }
        }

        private List<WorkOrderRecord> _liveOrdersCache = new List<WorkOrderRecord>(MaxRows);
        private List<HomeValleyAlarms.AlertRecord> _alertsCache = new List<HomeValleyAlarms.AlertRecord>(MaxAlertRows);

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            _panel.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
            {
                return;
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            CampaignState state = CampaignSession.Current;
            RefreshOrderList(state);
            RefreshAlertList(state);
            RefreshPriorityRows();
        }

        private void RefreshOrderList(CampaignState state)
        {
            WorkOrderRecord[] orders = state?.WorkOrders;
            _liveOrdersCache.Clear();
            if (orders != null)
            {
                foreach (WorkOrderRecord o in orders)
                {
                    if (_liveOrdersCache.Count >= MaxRows)
                    {
                        break;
                    }
                    if (o.State == WorkOrderState.Ready || o.State == WorkOrderState.Reserved
                        || o.State == WorkOrderState.InProgress || o.State == WorkOrderState.Waiting)
                    {
                        _liveOrdersCache.Add(o);
                    }
                }
            }

            _emptyLabel.style.display = _liveOrdersCache.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= _liveOrdersCache.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }

                WorkOrderRecord order = _liveOrdersCache[i];
                row.style.display = DisplayStyle.Flex;
                row.Q<Label>("Kind").text = order.Kind.ToString();
                row.Q<Label>("Target").text = order.TargetId;

                Label stateLabel = row.Q<Label>("State");
                stateLabel.text = order.State.ToString();
                stateLabel.RemoveFromClassList("wop-row-state-waiting");
                stateLabel.RemoveFromClassList("wop-row-state-failed");
                if (order.State == WorkOrderState.Waiting)
                {
                    stateLabel.AddToClassList("wop-row-state-waiting");
                }

                Label reasonLabel = row.Q<Label>("Reason");
                bool hasReason = !string.IsNullOrEmpty(order.FailureReason);
                reasonLabel.text = hasReason ? order.FailureReason : string.Empty;
                if (hasReason)
                {
                    reasonLabel.AddToClassList("wop-row-reason-visible");
                }
                else
                {
                    reasonLabel.RemoveFromClassList("wop-row-reason-visible");
                }
            }
        }

        /// <summary>AC-UI-003："点击能定位对象"——把选中切到该订单当前指派的机器。Ready/Waiting
        /// 订单尚未指派机器（<see cref="WorkOrderRecord.AssignedMachineLogicId"/>＝0）时无具体对象可
        /// 定位，静默忽略（不报错，不弹提示——"打开恢复面板"完整交互仍是 ER5-INT-01/UI-04 范围）。</summary>
        private void OnOrderRowClicked(int rowIndex)
        {
            if (rowIndex >= _liveOrdersCache.Count || GameRoot.HomeValley == null)
            {
                return;
            }
            int machineLogicId = _liveOrdersCache[rowIndex].AssignedMachineLogicId;
            if (machineLogicId > 0)
            {
                GameRoot.HomeValley.TrySelectMachine(machineLogicId);
            }
        }

        private void RefreshAlertList(CampaignState state)
        {
            _alertsCache = HomeValleyAlarms.Collect(state);
            for (int i = 0; i < MaxAlertRows; i++)
            {
                TemplateContainer row = _alertRowPool[i];
                if (i >= _alertsCache.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                row.Q<Label>("Message").text = _alertsCache[i].Message;
            }
        }

        private void OnAlertRowClicked(int rowIndex)
        {
            if (rowIndex >= _alertsCache.Count || GameRoot.HomeValley == null)
            {
                return;
            }
            int machineLogicId = _alertsCache[rowIndex].MachineLogicId;
            if (machineLogicId > 0)
            {
                GameRoot.HomeValley.TrySelectMachine(machineLogicId);
            }
        }

        /// <summary>没有选中机器时隐藏五个按钮、只显示提示；有选中时显示该机器当前每一类的优先级
        /// 数值（0＝禁用，UI 上直接显示"0"，不额外加文案——数字含义已在标题"工作偏好"下一目了然）。</summary>
        private void RefreshPriorityRows()
        {
            int? selected = GameRoot.HomeValley?.SelectedMachineLogicId;
            bool hasSelection = selected.HasValue && MachineRegistry.TryGetRecord(selected.Value, out _);
            _priorityEmptyLabel.style.display = hasSelection ? DisplayStyle.None : DisplayStyle.Flex;
            _priorityRows.style.display = hasSelection ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasSelection)
            {
                return;
            }

            MachineRegistry.TryGetRecord(selected.Value, out MachineRecord record);
            WorkPriorities priorities = record.WorkPriorities ?? WorkPriorities.Default();
            foreach (WorkOrderKind kind in PriorityKinds)
            {
                if (_priorityButtons.TryGetValue(kind, out Button button))
                {
                    button.text = priorities.Get(kind).ToString();
                }
            }
        }

        /// <summary>循环 0→1→2→3→4→0；写入失败（机器已死亡/在读取瞬间被移除）时保留按钮原文本，
        /// 下一次刷新会用真实数据覆盖，不会显示一个从未真正生效的值。</summary>
        private void OnPriorityButtonClicked(WorkOrderKind kind)
        {
            int? selected = GameRoot.HomeValley?.SelectedMachineLogicId;
            if (!selected.HasValue || !_priorityButtons.TryGetValue(kind, out Button button))
            {
                return;
            }
            int current = int.TryParse(button.text, out int parsed) ? parsed : 0;
            int next = (current + 1) % 5;
            if (HomeValleyController.TrySetMachineWorkPriority(selected.Value, kind, next))
            {
                button.text = next.ToString();
            }
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (_alertRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_alertRowTemplate);
                _alertRowTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
