using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Factory
{
    /// <summary>ER4-FAC-01 STORY-EXECUTION-CARDS.md 第1条："装配站面板有生产、改造、队列、详情、取消和
    /// 出口状态"。结构落在 UXML/USS（<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件，
    /// 与 <see cref="WorkOrder.WorkOrderPanelUIToolkit"/> 同一套刷新降频/行池写法。
    ///
    /// 面板开关状态由 <see cref="HomeValleyController.IsFactoryPanelOpen"/> 持有（点击装配站建筑切换，
    /// 见该类 <c>HandleSelectionClick</c>）——本类只负责渲染，不持有任何游戏状态。</summary>
    public sealed class FactoryPanelUIToolkit : MonoBehaviour
    {
        private const int MaxRows = 12;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private VisualElement _produceSection;
        private VisualElement _retrofitSection;
        private Button _tabProduce;
        private Button _tabRetrofit;
        private Button _produceErc003;
        private Button _produceHauler;
        private Button _produceHover;
        private Label _produceHint;
        private ScrollView _list;
        private Label _emptyLabel;
        private Label _detailText;
        private Button _cancelButton;
        private Label _exitStatusLabel;
        private Button _closeButton;

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);
        private readonly List<FactoryQueueItemRecord> _liveCache = new List<FactoryQueueItemRecord>(MaxRows);
        private string _selectedQueueItemId;
        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("FactoryPanel");
            _rowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("FactoryQueueRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：ER4-FAC-01 新增，取战术(5)/萌生(7)之间的 6，
            // 避开既有"装配"(4，旧细胞阶段面板，与本面板无关不得混用)。
            _document.sortingOrder = 6;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[FactoryPanelUIToolkit] rootVisualElement 等待超时，装配站面板未初始化。");
                return;
            }

            _panel = _root.Q<VisualElement>("FactoryPanelRoot");
            _produceSection = _root.Q<VisualElement>("ProduceSection");
            _retrofitSection = _root.Q<VisualElement>("RetrofitSection");
            _tabProduce = _root.Q<Button>("TabProduce");
            _tabRetrofit = _root.Q<Button>("TabRetrofit");
            _produceErc003 = _root.Q<Button>("ProduceBtn_Erc003");
            _produceHauler = _root.Q<Button>("ProduceBtn_Hauler");
            _produceHover = _root.Q<Button>("ProduceBtn_Hover");
            _produceHint = _root.Q<Label>("ProduceHintLabel");
            _list = _root.Q<ScrollView>("QueueList");
            _emptyLabel = _root.Q<Label>("QueueEmptyLabel");
            _detailText = _root.Q<Label>("DetailText");
            _cancelButton = _root.Q<Button>("CancelButton");
            _exitStatusLabel = _root.Q<Label>("ExitStatusLabel");
            _closeButton = _root.Q<Button>("CloseButton");

            _tabProduce.clicked += () => SetTab(false);
            _tabRetrofit.clicked += () => SetTab(true);
            _produceErc003.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintErc003Id);
            _produceHauler.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintHaulerId);
            _produceHover.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintHoverId);
            _cancelButton.clicked += OnCancelClicked;
            _closeButton.clicked += () => GameRoot.HomeValley?.SetFactoryPanelOpen(false);

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                row.AddToClassList("fac-row-clickable");
                int capturedIndex = i;
                row.RegisterCallback<ClickEvent>(_ => OnRowClicked(capturedIndex));
                _list.Add(row);
                _rowPool.Add(row);
            }

            SetTab(false);
        }

        private void SetTab(bool retrofit)
        {
            _produceSection.EnableInClassList("fac-hidden", retrofit);
            _retrofitSection.EnableInClassList("fac-hidden", !retrofit);
            _tabProduce.EnableInClassList("fac-tab-active", !retrofit);
            _tabRetrofit.EnableInClassList("fac-tab-active", retrofit);
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool open = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && GameRoot.HomeValley.IsFactoryPanelOpen;
            _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (!open)
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
            RefreshProduceButtons(state);
            RefreshQueueList(state);
            RefreshDetail(state);
            RefreshExitStatus(state);
        }

        private void RefreshProduceButtons(CampaignState state)
        {
            SetProduceButton(_produceErc003, state, HomeValleyLayout.BlueprintErc003Id);
            SetProduceButton(_produceHauler, state, HomeValleyLayout.BlueprintHaulerId);
            SetProduceButton(_produceHover, state, HomeValleyLayout.BlueprintHoverId);
        }

        private static void SetProduceButton(Button button, CampaignState state, string blueprintId)
        {
            HomeValleyLayout.FactoryProduceDefaults.TryGetValue(blueprintId, out HomeValleyLayout.ProduceBlueprintDefault def);
            bool unlocked = state != null && HomeValleyFactory.IsBlueprintUnlocked(state, blueprintId);
            button.text = $"{def.DisplayName}｜{def.ScrapCost}废料/{def.Seconds:F0}秒";
            button.SetEnabled(unlocked);
        }

        private void OnProduceClicked(string blueprintId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return;
            }
            HomeValleyFactory.FactoryOpResult r = HomeValleyFactory.TryEnqueueProduce(state, blueprintId);
            _produceHint.text = r.Success ? string.Empty : $"生产失败：{r.FailureReason}";
        }

        private void RefreshQueueList(CampaignState state)
        {
            _liveCache.Clear();
            if (state?.FactoryQueues != null)
            {
                foreach (FactoryQueueItemRecord q in state.FactoryQueues)
                {
                    if (q.State == FactoryQueueState.Cancelled || q.State == FactoryQueueState.Failed)
                    {
                        continue;
                    }
                    if (_liveCache.Count >= MaxRows)
                    {
                        break;
                    }
                    _liveCache.Add(q);
                }
            }

            _emptyLabel.style.display = _liveCache.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            for (int i = 0; i < MaxRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= _liveCache.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }

                FactoryQueueItemRecord item = _liveCache[i];
                row.style.display = DisplayStyle.Flex;
                HomeValleyLayout.FactoryProduceDefaults.TryGetValue(item.BlueprintId, out HomeValleyLayout.ProduceBlueprintDefault def);
                row.Q<Label>("Blueprint").text = string.IsNullOrEmpty(def.DisplayName) ? item.BlueprintId : def.DisplayName;
                row.Q<Label>("State").text = item.State.ToString();
                row.Q<Label>("Progress").text = item.Duration > 0f ? $"{item.Progress:F0}/{item.Duration:F0}s" : string.Empty;
                row.Q<Label>("Reason").text = item.BlockedReason ?? string.Empty;
            }
        }

        private void OnRowClicked(int rowIndex)
        {
            if (rowIndex >= _liveCache.Count)
            {
                return;
            }
            _selectedQueueItemId = _liveCache[rowIndex].QueueItemId;
        }

        private void RefreshDetail(CampaignState state)
        {
            FactoryQueueItemRecord item = string.IsNullOrEmpty(_selectedQueueItemId)
                ? null
                : HomeValleyFactory.Find(state, _selectedQueueItemId);
            if (item == null)
            {
                _detailText.text = "未选中队列项";
                _cancelButton.SetEnabled(false);
                return;
            }

            HomeValleyLayout.FactoryProduceDefaults.TryGetValue(item.BlueprintId, out HomeValleyLayout.ProduceBlueprintDefault def);
            string reasonLine = string.IsNullOrEmpty(item.BlockedReason) ? string.Empty : $"\n原因：{item.BlockedReason}";
            _detailText.text = $"{def.DisplayName} v{item.BlueprintVersion}\n状态：{item.State}\n" +
                $"进度：{item.Progress:F0}/{item.Duration:F0}秒{reasonLine}";
            _cancelButton.SetEnabled(item.State == FactoryQueueState.Queued || item.State == FactoryQueueState.WaitingResources
                || item.State == FactoryQueueState.WaitingPower || item.State == FactoryQueueState.Running);
        }

        private void OnCancelClicked()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || string.IsNullOrEmpty(_selectedQueueItemId))
            {
                return;
            }
            HomeValleyFactory.TryCancel(state, _selectedQueueItemId);
        }

        private void RefreshExitStatus(CampaignState state)
        {
            bool blocked = state != null && HomeValleyFactory.IsExitBlocked(state);
            _exitStatusLabel.text = blocked ? "出口：被完工机器占用，需驶离厂区" : "出口：畅通";
            _exitStatusLabel.EnableInClassList("fac-exit-blocked", blocked);
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
