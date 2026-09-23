using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Expedition
{
    /// <summary>ER5-EXP-01 STORY-EXECUTION-CARDS.md 第1/3条："远征准备面板显示区域目标、3～5机器、
    /// 每机 LogicId/编号/装配/伤势/货位/带宽、总火力/维修能力/风险/最低货位，敌方情报与无法出发
    /// 原因……工作机可中断任务先确认，运行中不可中断任务拒绝。"结构落在 UXML/USS
    /// （<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件；全部业务判断（校验/事务）
    /// 委托 <see cref="ExpeditionDepartureService"/>，本类不重新实现任何规则。
    ///
    /// 面板开关状态由 <see cref="HomeValleyController.IsExpeditionPrepPanelOpen"/> 持有（点击已修复
    /// 的信号塔切换），与 <see cref="Factory.FactoryPanelUIToolkit"/> 同一套写法。</summary>
    public sealed class ExpeditionPrepPanelUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 8;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _blockedLabel;
        private VisualElement _body;
        private Label _intelLabel;
        private Label _exposureLabel;
        private Toggle _towerBroadcastOffToggle;
        private ScrollView _list;
        private Label _summaryLabel;
        private Label _reasonsLabel;
        private VisualElement _interruptSection;
        private Label _interruptLabel;
        private Button _confirmInterruptButton;
        private Button _cancelInterruptButton;
        private Button _departButton;
        private Button _closeButton;

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);
        private readonly List<ExpeditionDepartureService.MachineIntel> _liveCache =
            new List<ExpeditionDepartureService.MachineIntel>(MaxRows);

        /// <summary>玩家当前勾选的出征名单，面板打开期间持续保留（刷新不清空），面板关闭/出发成功后清空。</summary>
        private readonly HashSet<int> _selected = new HashSet<int>();
        /// <summary>等待玩家确认中断的名单——非空时显示中断确认区，<see cref="_confirmInterruptButton"/>
        /// 点击后带 <c>interruptConfirmed:true</c> 重新调用 <see cref="ExpeditionDepartureService.TryDepart"/>。</summary>
        private int[] _pendingInterruptLogicIds = System.Array.Empty<int>();
        private bool _wasOpen;
        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ExpeditionPrepPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("ExpeditionMachineRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：ER5-EXP-01 新增，取萌生(7)/覆盖面板(10)之间的 8。
            _document.sortingOrder = 8;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[ExpeditionPrepPanelUIToolkit] rootVisualElement 等待超时，远征准备面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("ExpeditionPrepPanelRoot");
            _blockedLabel = _root.Q<Label>("BlockedLabel");
            _body = _root.Q<VisualElement>("Body");
            _intelLabel = _root.Q<Label>("IntelLabel");
            _exposureLabel = _root.Q<Label>("ExposureLabel");
            _towerBroadcastOffToggle = _root.Q<Toggle>("TowerBroadcastOffToggle");
            _list = _root.Q<ScrollView>("MachineList");
            _summaryLabel = _root.Q<Label>("SummaryLabel");
            _reasonsLabel = _root.Q<Label>("ReasonsLabel");
            _interruptSection = _root.Q<VisualElement>("InterruptSection");
            _interruptLabel = _root.Q<Label>("InterruptLabel");
            _confirmInterruptButton = _root.Q<Button>("ConfirmInterruptButton");
            _cancelInterruptButton = _root.Q<Button>("CancelInterruptButton");
            _departButton = _root.Q<Button>("DepartButton");
            _closeButton = _root.Q<Button>("CloseButton");

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _list.Add(row);
                _rowPool.Add(row);

                // 回调按行下标绑定一次（不是每次 Refresh 都 RegisterCallback——否则每 0.2 秒堆叠一份
                // 新回调，同一次勾选会触发 N 次 Add/Remove）；实际 LogicId 从 _liveCache[下标] 现查，
                // 因为同一下标在不同刷新周期可能对应不同机器（家园机器数量变化/排序变化）。
                int capturedIndex = i;
                Toggle toggle = row.Q<Toggle>("Select");
                toggle.RegisterValueChangedCallback(evt => OnRowToggleChanged(capturedIndex, evt.newValue));
            }

            _departButton.clicked += OnDepartClicked;
            _closeButton.clicked += OnCloseClicked;
            _confirmInterruptButton.clicked += OnConfirmInterruptClicked;
            _cancelInterruptButton.clicked += OnCancelInterruptClicked;
            // ER6-EXPOSE-01：塔关广播开关——玩家可在出发前看到当前暴露值与"关闭广播省暴露但降带宽"
            // 的实时取舍（"玩家可看见出征/接管损失"字面要求）。
            _towerBroadcastOffToggle.RegisterValueChangedCallback(evt =>
                CampaignExposureLedger.SetTowerBroadcastOff(CampaignSession.Current, evt.newValue));
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            HomeValleyController hv = GameRoot.HomeValley;
            bool open = hv != null && hv.IsActive && hv.IsExpeditionPrepPanelOpen;
            _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (!open)
            {
                if (_wasOpen)
                {
                    // 面板关闭即丢弃未确认的选择/待中断名单——重新打开是一次全新的准备流程，
                    // 不带着上次的半成品状态，避免"上次勾了 3 台这次莫名其妙又选中"的困惑。
                    _selected.Clear();
                    _pendingInterruptLogicIds = System.Array.Empty<int>();
                }
                _wasOpen = false;
                return;
            }
            _wasOpen = true;

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            Refresh();
        }

        private void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            ExpeditionDepartureService.PrepSnapshot snapshot = ExpeditionDepartureService.BuildPrepSnapshot(state);

            _blockedLabel.style.display = snapshot.RegionReachable ? DisplayStyle.None : DisplayStyle.Flex;
            _body.style.display = snapshot.RegionReachable ? DisplayStyle.Flex : DisplayStyle.None;
            if (!snapshot.RegionReachable)
            {
                _blockedLabel.text = snapshot.BlockedReason == "region-locked"
                    ? "破碎都市尚未解锁：修复信号塔并生产 ERC-003 战斗履带后再来。"
                    : "没有活动战役。";
                return;
            }

            _intelLabel.text = $"目标：破碎都市（第 {snapshot.ExpeditionCount + 1} 次出击）｜警戒 {snapshot.EnemyAlertLevel:F0}/100\n{snapshot.EnemyIntelText}";

            // ER6-EXPOSE-01：暴露值+带宽实时展示，让玩家在出发前就能看到"关闭广播"的真实取舍。
            _exposureLabel.text = $"信号暴露 {state.SignalExposure:F0}/100｜带宽 {state.SignalBandwidth:F0}" +
                (state.SignalTowerBroadcastOff ? "（广播已关闭，−3带宽）" : string.Empty);
            _towerBroadcastOffToggle.SetValueWithoutNotify(state.SignalTowerBroadcastOff);

            // 勾选集里已经不再存在/不再合法的 LogicId 清掉（机器阵亡/被移出家园等）。
            var validIds = new HashSet<int>(snapshot.Machines.Where(m => m.Eligible).Select(m => m.LogicId));
            _selected.RemoveWhere(id => !validIds.Contains(id));

            _liveCache.Clear();
            _liveCache.AddRange(snapshot.Machines.Take(MaxRows));
            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= _liveCache.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }

                ExpeditionDepartureService.MachineIntel m = _liveCache[i];
                row.style.display = DisplayStyle.Flex;
                row.RemoveFromClassList("exp-row-ineligible");
                if (!m.Eligible)
                {
                    row.AddToClassList("exp-row-ineligible");
                }

                row.Q<Label>("Number").text = $"#{m.DisplayNumber}";
                row.Q<Label>("Chassis").text = m.ChassisId;
                row.Q<Label>("Health").text = $"HP{m.Health:F0}/{m.MaxHealth:F0}";
                row.Q<Label>("Cargo").text = $"货{m.CargoSlots}";
                row.Q<Label>("Bandwidth").text = $"带宽{m.BandwidthCost:F0}";
                row.Q<Label>("Weapon").text = m.HasWeapon ? "武" : "—";
                row.Q<Label>("Status").text = DescribeStatus(m);

                Toggle toggle = row.Q<Toggle>("Select");
                toggle.SetEnabled(m.Eligible);
                toggle.SetValueWithoutNotify(m.Eligible && _selected.Contains(m.LogicId));
            }

            int[] selectedArray = _selected.ToArray();
            ExpeditionDepartureService.RosterValidation validation =
                ExpeditionDepartureService.ValidateRoster(state, selectedArray);

            _summaryLabel.text = $"已选 {selectedArray.Length}/{ExpeditionDepartureService.MinRosterSize}～" +
                $"{ExpeditionDepartureService.MaxRosterSize}｜货位 {validation.TotalCargoSlots}" +
                $"（建议≥{snapshot.MinRecommendedCargoSlots}）｜带宽 {validation.TotalBandwidth:F0}/{validation.BandwidthCapacity:F0}" +
                $"｜武器 {(validation.HasWeapon ? "有" : "无")}";

            bool showReasons = validation.BlockingReasons.Length > 0 && _pendingInterruptLogicIds.Length == 0;
            _reasonsLabel.text = showReasons ? "无法出发：" + string.Join("；", validation.BlockingReasons.Select(DescribeReason)) : string.Empty;
            _reasonsLabel.RemoveFromClassList("exp-reasons-visible");
            if (showReasons)
            {
                _reasonsLabel.AddToClassList("exp-reasons-visible");
            }

            bool interruptVisible = _pendingInterruptLogicIds.Length > 0;
            _interruptSection.RemoveFromClassList("exp-interrupt-section-visible");
            if (interruptVisible)
            {
                _interruptSection.AddToClassList("exp-interrupt-section-visible");
                string names = string.Join("、", _pendingInterruptLogicIds
                    .Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) ? $"#{r.DisplayNumber}" : $"#{id}"));
                _interruptLabel.text = $"{_pendingInterruptLogicIds.Length} 台机器正在工作（{names}），出征将中断当前任务并按规则交还/保留货物。确认出发？";
            }

            _departButton.SetEnabled(!interruptVisible);
        }

        private static string DescribeStatus(ExpeditionDepartureService.MachineIntel m)
        {
            if (m.Eligible)
            {
                return m.BusyKind.HasValue ? "工作中（可中断）" : "空闲";
            }
            if (!string.IsNullOrEmpty(m.IneligibleReason) && m.IneligibleReason.StartsWith("already-deployed"))
            {
                return "已在远征中";
            }
            if (m.IneligibleReason == "in-factory")
            {
                return "厂内待驶出";
            }
            if (!string.IsNullOrEmpty(m.IneligibleReason) && m.IneligibleReason.StartsWith("busy-uninterruptible"))
            {
                return "核心抢修中（不可中断）";
            }
            if (m.IneligibleReason == "dead")
            {
                return "阵亡";
            }
            return m.IneligibleReason ?? string.Empty;
        }

        private static string DescribeReason(string reason)
        {
            if (reason.StartsWith("roster-too-small"))
            {
                return $"至少选 {ExpeditionDepartureService.MinRosterSize} 台";
            }
            if (reason.StartsWith("roster-too-large"))
            {
                return $"最多选 {ExpeditionDepartureService.MaxRosterSize} 台";
            }
            if (reason.StartsWith("bandwidth-exceeded"))
            {
                return "信号带宽超额，减选机器";
            }
            if (reason == "no-weapon")
            {
                return "缺至少一件主作战组件";
            }
            if (reason == "no-cargo-capacity")
            {
                return "队伍没有可用货位";
            }
            if (reason.StartsWith("machine-"))
            {
                return "队伍里有机器不满足出征条件（" + reason + "）";
            }
            return reason;
        }

        private void OnRowToggleChanged(int rowIndex, bool selected)
        {
            if (rowIndex >= _liveCache.Count)
            {
                return;
            }
            int logicId = _liveCache[rowIndex].LogicId;
            if (selected)
            {
                _selected.Add(logicId);
            }
            else
            {
                _selected.Remove(logicId);
            }
            _pendingInterruptLogicIds = System.Array.Empty<int>(); // 改选择即作废尚未确认的中断请求。
            _refreshTimer = 0f;
        }

        private void OnDepartClicked()
        {
            int[] selected = _selected.ToArray();
            ExpeditionDepartureService.DepartureResult result =
                ExpeditionDepartureService.TryDepart(selected, interruptConfirmed: false);
            HandleDepartureResult(result);
        }

        private void OnConfirmInterruptClicked()
        {
            int[] selected = _selected.ToArray();
            ExpeditionDepartureService.DepartureResult result =
                ExpeditionDepartureService.TryDepart(selected, interruptConfirmed: true);
            HandleDepartureResult(result);
        }

        private void OnCancelInterruptClicked()
        {
            _pendingInterruptLogicIds = System.Array.Empty<int>();
            _refreshTimer = 0f;
        }

        private void HandleDepartureResult(ExpeditionDepartureService.DepartureResult result)
        {
            switch (result.Outcome)
            {
                case ExpeditionDepartureService.DepartureOutcome.Success:
                    _selected.Clear();
                    _pendingInterruptLogicIds = System.Array.Empty<int>();
                    // 场景已切到破碎都市，HomeValley.IsActive 下一帧起为 false，Update() 会自动隐藏本面板。
                    break;
                case ExpeditionDepartureService.DepartureOutcome.NeedsInterruptConfirmation:
                    _pendingInterruptLogicIds = result.InterruptibleLogicIds;
                    break;
                case ExpeditionDepartureService.DepartureOutcome.Blocked:
                case ExpeditionDepartureService.DepartureOutcome.RolledBack:
                    _pendingInterruptLogicIds = System.Array.Empty<int>();
                    _reasonsLabel.text = "无法出发：" + string.Join("；", result.Reasons.Select(DescribeReason));
                    _reasonsLabel.AddToClassList("exp-reasons-visible");
                    break;
            }
            _refreshTimer = 0f;
        }

        private void OnCloseClicked()
        {
            GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
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
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
