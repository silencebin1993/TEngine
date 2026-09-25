using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
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

        /// <summary>ER4-MCH-01 STORY-EXECUTION-CARDS.md 第2条"机器面板显示编号、当前装配、状态、
        /// 经历、工作/命令、伤势与恢复动作"——只读展示，复用本面板已有的"选中机器"数据源
        /// （<see cref="RefreshPriorityRows"/> 同一套 <c>SelectedMachineLogicId</c>），不新开一个
        /// 独立 UIDocument 宿主（同一条"一个 GameObject 一个 UIDocument"纪律）。</summary>
        private Label _machineDetailEmptyLabel;
        private Label _machineDetailLabel;

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
            _machineDetailEmptyLabel = _root.Q<Label>("MachineDetailEmptyLabel");
            _machineDetailLabel = _root.Q<Label>("MachineDetailLabel");

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
            RefreshMachineDetail();
        }

        /// <summary>ER4-MCH-01：选中机器时展示编号/底盘/装配/HP/电池/状态/经历/统计——与
        /// <see cref="HomeValleyCombatTargets"/>/<see cref="HomeValleyWorkOrders"/> 等唯一写入口
        /// 产出的字段直接读，不在 UI 侧重新猜/累加计数。伤势目前只有 <see cref="MachineRecord.InjuryFlags"/>
        /// 骨架字段（无真实写入源，归还谷地没有让机器受伤的触发源），如实显示"无记录"而不是编一个
        /// 假伤痕出来，符合"缺失视觉伤痕时不能用存档字段代替玩家反馈"的红线——这里反过来也不能拿
        /// 空字段冒充"有伤痕"。</summary>
        private void RefreshMachineDetail()
        {
            if (_machineDetailLabel == null || _machineDetailEmptyLabel == null)
            {
                return;
            }
            int? selected = GameRoot.HomeValley?.SelectedMachineLogicId;
            bool hasSelection = selected.HasValue && MachineRegistry.TryGetRecord(selected.Value, out _);
            _machineDetailEmptyLabel.style.display = hasSelection ? DisplayStyle.None : DisplayStyle.Flex;
            _machineDetailLabel.style.display = hasSelection ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasSelection)
            {
                return;
            }

            MachineRegistry.TryGetRecord(selected.Value, out MachineRecord record);
            string status = !record.IsAlive ? "阵亡（纪念记录）"
                : record.IsInFactory ? "厂内待驶出"
                : GameRoot.HomeValley != null && GameRoot.HomeValley.IsMachineDirectControlled(record.LogicId) ? "直控中"
                : string.IsNullOrEmpty(record.CurrentWorkOrderId) ? "空闲" : "工作中";

            string experience = record.ExperienceFlags != null && record.ExperienceFlags.Length > 0
                ? string.Join("、", System.Array.ConvertAll(record.ExperienceFlags, MachineExperienceFlags.DisplayName))
                : "无";
            string injuries = record.InjuryFlags != null && record.InjuryFlags.Length > 0
                ? string.Join("、", record.InjuryFlags)
                : "无记录";

            _machineDetailLabel.text =
                $"编号 #{record.DisplayNumber}（LogicId {record.LogicId}）｜底盘 {record.ChassisId}｜" +
                $"装配 v{record.BlueprintVersion}｜状态 {status}\n" +
                $"HP {record.Health:F0}/{record.MaxHealth:F0}｜电池 {record.Battery:F0}｜伤势 {injuries}\n" +
                $"经历：{experience}\n" +
                $"统计：工作{record.JobsCompleted}｜接管{record.TimesControlled}｜远征{record.ExpeditionsCompleted}｜击杀{record.KillCount}";
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
                // ER8-CONTENT-01 AC-THEME-001：类型/状态此前直接 ToString()（Haul、InProgress 等英文枚举名），
                // 原因列直接显示原因码（machine-died、storage-full:need=…）——全部改为玩家文字。
                row.Q<Label>("Kind").text = KindText(order.Kind);
                // ER4-CONTENT-01：建筑类目标改显机械内容目录 DisplayName（如"发电机"），
                // 不再直接暴露内部拼接 id（如 "home_valley:generator"）；非建筑目标按工单类型给通用称呼。
                string targetLabel = MechanicalContentFacade.ResolveWorkOrderTargetLabel(order.TargetId);
                row.Q<Label>("Target").text = targetLabel == order.TargetId ? FallbackTargetText(order.Kind) : targetLabel;

                Label stateLabel = row.Q<Label>("State");
                stateLabel.text = StateText(order.State);
                stateLabel.RemoveFromClassList("wop-row-state-waiting");
                stateLabel.RemoveFromClassList("wop-row-state-failed");
                if (order.State == WorkOrderState.Waiting)
                {
                    stateLabel.AddToClassList("wop-row-state-waiting");
                }
                else if (order.State == WorkOrderState.Failed)
                {
                    stateLabel.AddToClassList("wop-row-state-failed");
                }

                Label reasonLabel = row.Q<Label>("Reason");
                bool hasReason = !string.IsNullOrEmpty(order.FailureReason);
                reasonLabel.text = hasReason ? ReasonText(order.FailureReason) : string.Empty;
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

        public static string KindText(WorkOrderKind kind)
        {
            switch (kind)
            {
                case WorkOrderKind.Haul: return "搬运";
                case WorkOrderKind.Build: return "建造";
                case WorkOrderKind.Repair: return "维修";
                case WorkOrderKind.Salvage: return "拆解";
                case WorkOrderKind.Recharge: return "充电";
                default: return "工作";
            }
        }

        public static string StateText(WorkOrderState state)
        {
            switch (state)
            {
                case WorkOrderState.Proposed: return "待确认";
                case WorkOrderState.Ready: return "待分配";
                case WorkOrderState.Reserved: return "已预留";
                case WorkOrderState.InProgress: return "进行中";
                case WorkOrderState.Waiting: return "等待中";
                case WorkOrderState.Completed: return "已完成";
                case WorkOrderState.Cancelled: return "已取消";
                case WorkOrderState.Failed: return "失败";
                default: return string.Empty;
            }
        }

        /// <summary>工单原因码 → 玩家文字。原因码仍原样留在记录里供逻辑与日志使用。</summary>
        public static string ReasonText(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }
            if (reason.StartsWith("storage-full", System.StringComparison.Ordinal))
            {
                return "仓库已满，腾出仓位后自动继续";
            }
            switch (reason)
            {
                case "machine-died": return "执行机器损失，资源已退还";
                case "machine-not-found": return "执行机器已不在场";
                case "target-destroyed": return "目标已不存在";
                case "source-vanished": return "物资已不在原处";
                case "cargo-lost": return "货物已丢失";
                case "path-blocked": return "路径受阻，正在等待通行";
                default: return "暂时受阻";
            }
        }

        private static string FallbackTargetText(WorkOrderKind kind)
        {
            switch (kind)
            {
                case WorkOrderKind.Haul: return "地面物资";
                case WorkOrderKind.Salvage: return "残骸";
                case WorkOrderKind.Recharge: return "充电";
                default: return "建筑";
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
