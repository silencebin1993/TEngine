using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.UI.Common;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

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
        // FG2-E2E-01（FG-GAP-050）：数据复原栏
        private Label _restoreTitle;
        private Label _restoreHint;
        private DropdownField _restoreDropdown;
        private Button _restoreButton;
        private Label _restoreDetail;
        private readonly List<string> _restoreChoiceIds = new List<string>();

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
            _restoreTitle = _root.Q<Label>("RestoreTitle");
            _restoreHint = _root.Q<Label>("RestoreHint");
            _restoreDropdown = _root.Q<DropdownField>("RestoreDropdown");
            _restoreButton = _root.Q<Button>("RestoreButton");
            _restoreDetail = _root.Q<Label>("RestoreDetail");
            if (_restoreTitle != null)
            {
                _restoreTitle.text = Localization.GameText.Get("analysis.restore.title");
            }
            if (_restoreButton != null)
            {
                _restoreButton.text = Localization.GameText.Get("analysis.restore.button");
                _restoreButton.clicked += OnRestoreClicked;
            }
            _restoreDropdown?.RegisterValueChangedCallback(_ => RefreshRestoreDetail());

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

        /// <summary>FG0-UX-01（FGR-UX-001）：Esc 逐层返回——面板开着时在 Esc 栈里占一层（缓存委托，不每帧分配）。</summary>
        private System.Action _escClose;

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            HomeValleyController hv = GameRoot.HomeValley;
            bool open = hv != null && hv.IsActive && hv.IsAnalysisPanelOpen;
            UiEscapeStack.Sync(this, open, _escClose ??= OnCloseClicked);
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
                // ER8-CONTENT-01：名字查不到时此前回退成原始内容 ID；原因列此前直接显示原因码。行首图标＝解析后解锁的内容。
                row.Q<Label>("Name").text = string.IsNullOrEmpty(info.DisplayName) ? "待解析模块" : info.DisplayName;
                ContentIcons.Apply(row.Q<VisualElement>("Icon"), info.UnlockContentId);
                row.Q<Label>("State").text = DescribeState(q.State);
                row.Q<Label>("Progress").text = $"{q.Progress:F1}/{q.Duration:F1}s";
                row.Q<Label>("Reason").text = QueueText.Reason(q.BlockedReason);
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
            RefreshRestore(state);
        }

        // ── FG2-E2E-01（FG-GAP-050）：数据复原 ─────────────────────────────────────────────

        /// <summary>数据复原栏当前的候选固件（与下拉同序；自检 / 旅程按固件 ID 找行）。</summary>
        public IReadOnlyList<string> RestoreChoiceIds => _restoreChoiceIds;

        /// <summary>数据复原栏的说明行（技术数据 + 选中固件的正式来源 / 标签 / 反应）。</summary>
        public string RestoreDetailText => _restoreDetail?.text ?? string.Empty;

        public string ResultText => _resultLabel?.text ?? string.Empty;

        public static AnalysisPanelUIToolkit Instance { get; private set; }

        private void Awake() => Instance = this;

        private void RefreshRestore(CampaignState state)
        {
            if (_restoreDropdown == null)
            {
                return;
            }
            string selected = _restoreDropdown.index >= 0 && _restoreDropdown.index < _restoreChoiceIds.Count ? _restoreChoiceIds[_restoreDropdown.index] : null;
            _restoreChoiceIds.Clear();
            var choices = new List<string>();
            foreach (string id in Campaign.Signal.FirmwareRestoreService.Candidates(state))
            {
                _restoreChoiceIds.Add(id);
                choices.Add(Campaign.Signal.FirmwareRestoreService.ChoiceText(id));
            }
            _restoreDropdown.choices = choices;
            int keep = selected != null ? _restoreChoiceIds.IndexOf(selected) : -1;
            if (keep >= 0)
            {
                _restoreDropdown.SetValueWithoutNotify(choices[keep]);
            }
            else
            {
                ClampIndex(_restoreDropdown, choices.Count);
            }
            _restoreButton?.SetEnabled(choices.Count > 0);
            if (_restoreHint != null)
            {
                _restoreHint.text = Localization.GameText.Get("analysis.restore.hint") + " " +
                                    Localization.GameText.Format("analysis.restore.tech", state?.TechData ?? 0);
            }
            RefreshRestoreDetail();
        }

        private void RefreshRestoreDetail()
        {
            if (_restoreDetail == null)
            {
                return;
            }
            int i = _restoreDropdown != null ? _restoreDropdown.index : -1;
            _restoreDetail.text = _restoreChoiceIds.Count == 0
                ? Localization.GameText.Get("analysis.restore.empty")
                : i >= 0 && i < _restoreChoiceIds.Count
                    ? Campaign.Signal.FirmwareRestoreService.DetailText(CampaignSession.Current, _restoreChoiceIds[i])
                    : string.Empty;
        }

        /// <summary>点“复原”：先按此刻状态预检（不改状态），通过才弹确认框（B04：花技术数据不可撤销）；确认时服务层再检查一遍。</summary>
        private void OnRestoreClicked()
        {
            CampaignState state = CampaignSession.Current;
            int i = _restoreDropdown != null ? _restoreDropdown.index : -1;
            if (i < 0 || i >= _restoreChoiceIds.Count)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                _resultLabel.text = Localization.GameText.Get("analysis.restore.reason.none_selected");
                return;
            }
            string id = _restoreChoiceIds[i];
            Campaign.Signal.FirmwareRestoreService.Result check = Campaign.Signal.FirmwareRestoreService.Check(state, id);
            if (!check.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                _resultLabel.text = check.Message;
                return;
            }
            var req = new ConfirmRequest
            {
                Title = Localization.GameText.Get("analysis.restore.confirm.title"),
                Irreversible = true,
                ConfirmText = Localization.GameText.Get("analysis.restore.confirm.ok"),
                CancelText = Localization.GameText.Get("fwlib.confirm.cancel"),
                OnConfirm = () =>
                {
                    Campaign.Signal.FirmwareRestoreService.Result r = Campaign.Signal.FirmwareRestoreService.TryRestore(CampaignSession.Current, id);
                    if (_resultLabel != null)
                    {
                        _resultLabel.text = r.Message;
                    }
                    _refreshTimer = 0f;
                },
            };
            req.Lines.Add(check.Message);
            UiConfirmDialog.Show(req);
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
            if (Instance == this)
            {
                Instance = null;
            }
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
