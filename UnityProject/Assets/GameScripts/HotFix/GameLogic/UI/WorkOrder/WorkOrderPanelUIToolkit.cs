using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
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
        /// <summary>FG4-ECO-09 修复轮：告警栏行数 = 告警等级数（六级同时存在时每级都能露出一条）。</summary>
        private const int MaxAlertRows = HomeValleyAlarms.DisplayRows;
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
        private Button _details;
        private VisualElement _body;
        private bool _detailsOpen;
        private ScrollView _list;
        private Label _emptyLabel;
        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);

        private ScrollView _alertList;
        /// <summary>FG4-ECO-09 修复轮：告警栏下方一行——“另有 N 条告警”或点告警定位失败的原因（B06 不静默）。</summary>
        private Label _alertStatus;
        private string _alertFailureText;
        private float _alertFailureUntil;
        private const float AlertFailureSeconds = 4f;
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
            _details = _root.Q<Button>("WorkOrderDetails");
            _body = _root.Q<VisualElement>("PanelBody");
            if (_details != null) _details.clicked += () => SetDetailsOpen(!_detailsOpen);
            _list = _root.Q<ScrollView>("OrderList");
            _emptyLabel = _root.Q<Label>("EmptyLabel");
            _alertList = _root.Q<ScrollView>("AlertList");
            _alertStatus = _root.Q<Label>("AlertStatus");
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

        public void SetDetailsOpen(bool open)
        {
            if (_body == null || open == _detailsOpen) return;
            _detailsOpen = open;
            _body.EnableInClassList("wop-hidden", !open);
            if (open) Kit.UiEscapeStack.Push(this, () => SetDetailsOpen(false));
            else Kit.UiEscapeStack.Remove(this);
            _refreshTimer = 0f;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool active = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            if (HomeValleyBuildMode.Current?.IsOpen == true) SetDetailsOpen(false);
            _panel.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (!active)
            {
                SetDetailsOpen(false);
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
            if (_details != null)
                _details.text = $"工作单 {_liveOrdersCache.Count} · 警报 {_alertsCache.Count} · {(_detailsOpen ? "收起详情" : "展开详情")}";
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
            CampaignState state = CampaignSession.Current;
            // FG1-HUD-01（FGR-SIG-082 机器经历在机器详情页可见；FG-GAP-011 文案走文本键、“直控 / 接管”改“接入”）。
            string status = GameLogic.Localization.GameText.Get(!record.IsAlive ? "machine.status.dead"
                : record.IsInFactory ? "machine.status.in_factory"
                : GameLogic.Campaign.Signal.SignalUplinkService.IsUplinked(state, record.LogicId) ? "machine.status.uplinked"
                : string.IsNullOrEmpty(record.CurrentWorkOrderId) ? "machine.status.idle" : "machine.status.working");
            string injuries = record.InjuryFlags != null && record.InjuryFlags.Length > 0
                ? GameLogic.Campaign.MachineInjury.DescribeAll(record.InjuryFlags)
                : GameLogic.Localization.GameText.Get("machine.detail.injury_none");
            string port = GameLogic.Localization.GameText.Get(GameLogic.Campaign.Signal.UplinkHudModel.HasUplinkPort(state, record.LogicId) ? "machine.detail.port_yes" : "machine.detail.port_no");
            string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

            _machineDetailLabel.text =
                GameLogic.Localization.GameText.Format("machine.detail.line_id", MachineNaming.Short(record), record.LogicId, record.ChassisId, record.BlueprintVersion, port, status) + "\n" +
                GameLogic.Localization.GameText.Format("machine.detail.line_vitals", F0(record.Health), F0(record.MaxHealth), F0(record.Battery), injuries) + "\n" +
                GameLogic.Localization.GameText.Format("machine.detail.line_morph", GameLogic.Campaign.Signal.UplinkHudModel.MorphText(state, record.LogicId)) + "\n" +
                GameLogic.Localization.GameText.Format("machine.detail.line_exp", MachineExperienceFlags.Join(record.ExperienceFlags)) + "\n" +
                MachineSignalExperience.Describe(state, record) + "\n" +
                GameLogic.Localization.GameText.Format("machine.detail.line_stats", record.JobsCompleted, record.ExpeditionsCompleted, record.KillCount);
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
                string targetText = targetLabel == order.TargetId ? FallbackTargetText(order.Kind) : targetLabel;
                // FG4-ECO-06（FGR-ECO-031 可追溯）：规则派的补给 / 送修 / 驻防写清做什么，规则派出或改动过的单子附“由规则 R3 触发”。
                CampaignState ruleState = CampaignSession.Current;
                string ruleTarget = GameLogic.Campaign.Economy.StandingRuleService.DescribeRuleOrderTarget(ruleState, order);
                string ruleTrace = GameLogic.Campaign.Economy.StandingRuleService.DescribeOrder(ruleState, order);
                row.Q<Label>("Target").text = ruleTrace != null
                    ? GameLogic.Localization.GameText.Format("rules.with_trace", ruleTarget ?? targetText, ruleTrace)
                    : ruleTarget ?? targetText;

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
                reasonLabel.text = hasReason ? ReasonText(ruleState, order) : string.Empty;
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
            // 修复轮（审查 P2）：超过 5 行时每个等级至少露出一条，其余写“另有 N 条”（不再被同一等级挤掉、也不静默丢弃）。
            _alertsCache = HomeValleyAlarms.PickForDisplay(HomeValleyAlarms.Collect(state), MaxAlertRows, out int hidden);
            RefreshAlertStatus(hidden);
            for (int i = 0; i < MaxAlertRows; i++)
            {
                TemplateContainer row = _alertRowPool[i];
                if (i >= _alertsCache.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                // FG4-ECO-09：等级写成文字（“[电力] …”，不只靠颜色）；点击定位到告警的位置（FG00 B08）。
                row.Q<Label>("Message").text = _alertsCache[i].RowText;
            }
        }

        private void OnAlertRowClicked(int rowIndex)
        {
            if (rowIndex >= _alertsCache.Count || GameRoot.HomeValley == null)
            {
                return;
            }
            HomeValleyAlarms.AlertRecord alert = _alertsCache[rowIndex];
            // FG4-ECO-09（FGR-ECO-080“告警可以定位”）：镜头飞到建筑 / 机器 / 突袭队伍的位置；涉及机器时同时选中它（原有行为）。
            bool located = HomeValleyAlarms.Locate(alert, out string failureKey);
            if (alert.MachineLogicId > 0)
            {
                GameRoot.HomeValley.TrySelectMachine(alert.MachineLogicId);
            }
            // 修复轮（审查 P2，FG00 B06）：定位失败时写出原因（与通知中心 / 离家报告同一套文本键），不静默（选中了机器但镜头没动，也说明原因）。
            ShowAlertLocateResult(located, failureKey);
        }

        /// <summary>点告警的结果：失败时在告警栏下方显示原因几秒（自检也经这里断言）。</summary>
        public void ShowAlertLocateResult(bool located, string failureKey)
        {
            _alertFailureText = located ? null : GameText.Get(string.IsNullOrEmpty(failureKey) ? "ui.notify.no_location" : failureKey);
            _alertFailureUntil = Time.unscaledTime + AlertFailureSeconds;
            RefreshAlertStatus(_lastAlertHidden);
        }

        private int _lastAlertHidden;

        /// <summary>告警栏下方那一行此刻的文字（空 = 不显示）。</summary>
        public string AlertStatusText => _alertStatus != null && !_alertStatus.ClassListContains("wop-alert-status--hidden") ? _alertStatus.text : string.Empty;

        private void RefreshAlertStatus(int hidden)
        {
            _lastAlertHidden = hidden;
            if (_alertStatus == null)
            {
                return;
            }
            string text = null;
            if (!string.IsNullOrEmpty(_alertFailureText) && Time.unscaledTime < _alertFailureUntil)
            {
                text = GameText.Format("away.panel.locate_failed", _alertFailureText);
            }
            else if (hidden > 0)
            {
                text = GameText.Format("alarm.list.more", hidden);
            }
            _alertStatus.text = text ?? string.Empty;
            _alertStatus.EnableInClassList("wop-alert-status--hidden", string.IsNullOrEmpty(text));
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
                case WorkOrderKind.Deliver:
                case WorkOrderKind.MachineRepair:
                case WorkOrderKind.Garrison:
                    return GameLogic.Campaign.Economy.StandingRuleService.KindTextOf(kind);
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

        /// <summary>
        /// FG4-E2E-01（DEBT-FG4ECO11-06）：一张工单的原因文字。施工单在等材料时与施工队列、悬停走同一个描述（<see cref="HomeValleyConstruction.DescribeStatus(CampaignState, WorkOrderRecord)"/>：
        /// 写明缺哪种、从哪儿来、在途件数；关键材料正被搬回时写“机器正在搬回”，不再提示去打首领）；其余原因码照旧。
        /// </summary>
        public static string ReasonText(CampaignState state, WorkOrderRecord order)
        {
            string reason = order?.FailureReason;
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }
            if (state != null && reason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, System.StringComparison.Ordinal))
            {
                string status = order.Kind == WorkOrderKind.Build ? HomeValleyConstruction.DescribeStatus(state, order) : null;
                return !string.IsNullOrEmpty(status) ? status : HomeValleyConstruction.DescribeMaterialsReason(state, reason);
            }
            return ReasonText(reason);
        }

        /// <summary>工单原因码 → 玩家文字。原因码仍原样留在记录里供逻辑与日志使用。</summary>
        public static string ReasonText(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }
            if (reason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, System.StringComparison.Ordinal))
            {
                // FG3-LOG-02：施工等待材料（materials:还差:库存[:资源类型]），与施工队列同一写法；
                // FG4-ECO-11 审查修复：卡住的是关键材料时写明是哪种、从哪里获得（原来一律写成“废料”）。
                return HomeValleyConstruction.DescribeMaterialsReason(reason);
            }
            if (reason == HomeValleyConstruction.ReturnWaitReason || reason.StartsWith("storage-full", System.StringComparison.Ordinal))
            {
                return "仓库已满，腾出仓位后自动继续";
            }
            if (Campaign.Regions.HomeValleyWorkOrders.IsUnreachableReason(reason))
            {
                // FG0-ARCH-06：目标无法到达（带原因；30 秒后重试）。
                return GameLogic.Localization.GameText.Format("nav.work.reason",
                    GameLogic.Localization.GameText.Get(Campaign.Nav.NavService.FailKey(Campaign.Regions.HomeValleyWorkOrders.ParseUnreachable(reason))));
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
            Kit.UiEscapeStack.Remove(this);
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
