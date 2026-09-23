using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Analysis
{
    /// <summary>ER6-ANA-01 STORY-EXECUTION-CARDS.md 第1条："玩家从仓库选模块并通过 WorkOrder 送解析台，
    /// 面板显示来源、完整度、所需时间、技术数据与解锁结果。"结构落在 UXML/USS（`unity-ui-toolkit.md`
    /// 硬规则），C# 只做数据绑定与事件；全部业务判断委托 <see cref="HomeValleyAnalysis"/>，本类不重新
    /// 实现任何规则。开关状态由 <see cref="HomeValleyController.IsAnalysisPanelOpen"/> 持有（点击已修复
    /// 的解析台建筑切换），与 <see cref="Expedition.ExpeditionPrepPanelUIToolkit"/> 同一套写法。</summary>
    public sealed class AnalysisPanelUIToolkit : MonoBehaviour
    {
        private const int MaxQueueRows = HomeValleyAnalysis.MaxActiveQueueItems + 2;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private DropdownField _warehouseDropdown;
        private Button _enqueueButton;
        private ScrollView _queueList;
        private DropdownField _cancelDropdown;
        private Button _cancelButton;
        private Label _resultLabel;
        private Button _closeButton;

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxQueueRows);
        private readonly List<string> _warehouseChoiceSalvageIds = new List<string>();
        private readonly List<string> _cancelChoiceQueueIds = new List<string>();

        private bool _wasOpen;
        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("AnalysisPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("AnalysisQueueRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：撤离面板(9)之后、覆盖面板(10)之前——同一层不冲突，两者
            // 互斥打开场景不同（一个只在破碎都市，一个只在归还谷地），取同一个 9 不会同屏重叠。
            _document.sortingOrder = 9;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[AnalysisPanelUIToolkit] rootVisualElement 等待超时，解析台面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("AnalysisPanelRoot");
            _warehouseDropdown = _root.Q<DropdownField>("WarehouseDropdown");
            _enqueueButton = _root.Q<Button>("EnqueueButton");
            _queueList = _root.Q<ScrollView>("QueueList");
            _cancelDropdown = _root.Q<DropdownField>("CancelDropdown");
            _cancelButton = _root.Q<Button>("CancelButton");
            _resultLabel = _root.Q<Label>("ResultLabel");
            _closeButton = _root.Q<Button>("CloseButton");

            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _queueList.Add(row);
                _rowPool.Add(row);
            }

            _enqueueButton.clicked += OnEnqueueClicked;
            _cancelButton.clicked += OnCancelClicked;
            _closeButton.clicked += OnCloseClicked;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            HomeValleyController hv = GameRoot.HomeValley;
            bool open = hv != null && hv.IsActive && hv.IsAnalysisPanelOpen;
            _panel.RemoveFromClassList("ana-root-visible");
            if (open)
            {
                _panel.AddToClassList("ana-root-visible");
            }
            if (!open)
            {
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

            _warehouseChoiceSalvageIds.Clear();
            var warehouseChoices = new List<string>();
            foreach (RegionQuestItemRecord item in HomeValleyAnalysis.WarehouseItems(state))
            {
                HomeValleyAnalysis.YieldTable.TryGetValue(item.ContentId, out HomeValleyAnalysis.YieldInfo info);
                // 验收卡要求面板显示"来源、完整度、所需时间、技术数据"——来源读内容目录 SourceDetail
                // （唯一权威来源，不在面板另编文案）；本 Demo 关键物没有部分损坏/残缺机制，完整度恒
                // 100%（如实展示常量，不是编造出来的动态数值）。
                MechanicalContentFacade.TryGet(info.UnlockContentId, out MechanicalContentDef def);
                string source = def.SourceDetail ?? "破碎都市带回";
                warehouseChoices.Add($"{info.DisplayName}［完整度100%·{info.TechDataYield}技术数据·{info.Duration:F0}秒·{source}］");
                _warehouseChoiceSalvageIds.Add(item.SalvageInstanceId);
            }
            _warehouseDropdown.choices = warehouseChoices;
            ClampIndex(_warehouseDropdown, warehouseChoices.Count);

            AnalysisQueueItemRecord[] queues = state?.AnalysisQueues ?? System.Array.Empty<AnalysisQueueItemRecord>();
            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= queues.Length)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                AnalysisQueueItemRecord q = queues[i];
                HomeValleyAnalysis.YieldTable.TryGetValue(q.ContentId, out HomeValleyAnalysis.YieldInfo info);
                row.Q<Label>("Name").text = string.IsNullOrEmpty(info.DisplayName) ? q.ContentId : info.DisplayName;
                row.Q<Label>("State").text = DescribeState(q.State);
                row.Q<Label>("Progress").text = $"{q.Progress:F1}/{q.Duration:F1}s";
                row.Q<Label>("Reason").text = q.BlockedReason ?? string.Empty;
            }

            _cancelChoiceQueueIds.Clear();
            var cancelChoices = new List<string>();
            foreach (AnalysisQueueItemRecord q in queues)
            {
                if (q.State != AnalysisQueueState.Queued && q.State != AnalysisQueueState.Running
                    && q.State != AnalysisQueueState.WaitingPower)
                {
                    continue;
                }
                HomeValleyAnalysis.YieldTable.TryGetValue(q.ContentId, out HomeValleyAnalysis.YieldInfo info);
                cancelChoices.Add($"{(string.IsNullOrEmpty(info.DisplayName) ? q.ContentId : info.DisplayName)}·{DescribeState(q.State)}");
                _cancelChoiceQueueIds.Add(q.QueueItemId);
            }
            _cancelDropdown.choices = cancelChoices;
            ClampIndex(_cancelDropdown, cancelChoices.Count);
        }

        private static string DescribeState(AnalysisQueueState state)
        {
            switch (state)
            {
                case AnalysisQueueState.Queued: return "排队中（搬运中）";
                case AnalysisQueueState.Running: return "解析中";
                case AnalysisQueueState.WaitingPower: return "断电暂停";
                case AnalysisQueueState.Completed: return "已完成";
                case AnalysisQueueState.Cancelled: return "已取消";
                case AnalysisQueueState.Failed: return "已失败";
                default: return state.ToString();
            }
        }

        private void OnEnqueueClicked()
        {
            if (_warehouseDropdown.index < 0 || _warehouseDropdown.index >= _warehouseChoiceSalvageIds.Count)
            {
                _resultLabel.text = "操作失败[未选择仓库模块]。";
                return;
            }
            string salvageInstanceId = _warehouseChoiceSalvageIds[_warehouseDropdown.index];
            HomeValleyAnalysis.AnalysisOpResult result = HomeValleyAnalysis.TryEnqueue(CampaignSession.Current, salvageInstanceId);
            _resultLabel.text = result.Success ? string.Empty : $"操作失败[{result.FailureReason}]。";
            _refreshTimer = 0f;
        }

        private void OnCancelClicked()
        {
            if (_cancelDropdown.index < 0 || _cancelDropdown.index >= _cancelChoiceQueueIds.Count)
            {
                _resultLabel.text = "操作失败[未选择队列项]。";
                return;
            }
            string queueItemId = _cancelChoiceQueueIds[_cancelDropdown.index];
            HomeValleyAnalysis.AnalysisOpResult result = HomeValleyAnalysis.TryCancel(CampaignSession.Current, queueItemId);
            _resultLabel.text = result.Success ? string.Empty : $"操作失败[{result.FailureReason}]。";
            _refreshTimer = 0f;
        }

        private void OnCloseClicked()
        {
            GameRoot.HomeValley?.SetAnalysisPanelOpen(false);
        }

        private static void ClampIndex(DropdownField dropdown, int choiceCount)
        {
            if (choiceCount == 0)
            {
                dropdown.SetValueWithoutNotify(string.Empty);
            }
            else if (dropdown.index < 0 || dropdown.index >= choiceCount)
            {
                dropdown.index = 0;
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
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
