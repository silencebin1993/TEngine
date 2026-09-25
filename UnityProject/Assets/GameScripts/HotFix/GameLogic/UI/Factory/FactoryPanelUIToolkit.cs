using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.UI.Common;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
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

        private DropdownField _retrofitTargetDropdown;
        private DropdownField _retrofitVersionDropdown;
        private Label _retrofitPreviewLabel;
        private Button _retrofitConfirmButton;
        private Label _retrofitHintLabel;
        private readonly List<int> _retrofitTargetLogicIdsByIndex = new List<int>();
        private readonly List<int> _retrofitVersionNumbersByIndex = new List<int>();

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(MaxRows);
        private readonly List<FactoryQueueItemRecord> _liveCache = new List<FactoryQueueItemRecord>(MaxRows);
        private string _selectedQueueItemId;
        private float _refreshTimer;
        private bool _retrofitTabActive;

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

            _retrofitTargetDropdown = _root.Q<DropdownField>("RetrofitTargetDropdown");
            _retrofitVersionDropdown = _root.Q<DropdownField>("RetrofitVersionDropdown");
            _retrofitPreviewLabel = _root.Q<Label>("RetrofitPreviewLabel");
            _retrofitConfirmButton = _root.Q<Button>("RetrofitConfirmButton");
            _retrofitHintLabel = _root.Q<Label>("RetrofitHintLabel");

            _tabProduce.clicked += () => SetTab(false);
            _tabRetrofit.clicked += () => SetTab(true);
            _produceErc003.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintErc003Id);
            _produceHauler.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintHaulerId);
            _produceHover.clicked += () => OnProduceClicked(HomeValleyLayout.BlueprintHoverId);
            _cancelButton.clicked += OnCancelClicked;
            _closeButton.clicked += () => GameRoot.HomeValley?.SetFactoryPanelOpen(false);

            _retrofitTargetDropdown.RegisterValueChangedCallback(_ => RefreshRetrofitVersions(CampaignSession.Current));
            _retrofitVersionDropdown.RegisterValueChangedCallback(_ => RefreshRetrofitPreview(CampaignSession.Current));
            _retrofitConfirmButton.clicked += OnRetrofitConfirmClicked;

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
            _retrofitTabActive = retrofit;
            _produceSection.EnableInClassList("fac-hidden", retrofit);
            _retrofitSection.EnableInClassList("fac-hidden", !retrofit);
            _tabProduce.EnableInClassList("fac-tab-active", !retrofit);
            _tabRetrofit.EnableInClassList("fac-tab-active", retrofit);
            if (retrofit)
            {
                RefreshRetrofitTargets(CampaignSession.Current);
            }
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
            if (_retrofitTabActive)
            {
                RefreshRetrofitTargets(state);
            }
        }

        /// <summary>维修机未解锁时按钮是灰的，此前界面不说为什么——在生产提示行写明解锁条件（不改按钮尺寸）。</summary>
        public const string HoverLockedHint = "维修机：先让解析台通电运转后解锁";

        private void RefreshProduceButtons(CampaignState state)
        {
            SetProduceButton(_produceErc003, state, HomeValleyLayout.BlueprintErc003Id);
            SetProduceButton(_produceHauler, state, HomeValleyLayout.BlueprintHaulerId);
            SetProduceButton(_produceHover, state, HomeValleyLayout.BlueprintHoverId);

            bool hoverLocked = state == null || !HomeValleyFactory.IsBlueprintUnlocked(state, HomeValleyLayout.BlueprintHoverId);
            if (hoverLocked && string.IsNullOrEmpty(_produceHint.text))
            {
                _produceHint.text = HoverLockedHint;
            }
            else if (!hoverLocked && _produceHint.text == HoverLockedHint)
            {
                _produceHint.text = string.Empty;
            }
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
            _produceHint.text = r.Success ? string.Empty : "生产失败：" + HomeValleyFactory.DescribeFailure(r.FailureReason);
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
                string kindTag = item.Kind == FactoryQueueKind.Retrofit ? "[改造]" : "[生产]";
                row.Q<Label>("Blueprint").text = kindTag + ResolveBlueprintDisplayName(item.BlueprintId);
                // ER8-CONTENT-01：行首图标＝产出（或被改造）机器的底盘；状态与原因此前直接显示英文枚举与原因码。
                ContentIcons.Apply(row.Q<VisualElement>("Icon"), ResolveRowChassisId(item));
                row.Q<Label>("State").text = QueueText.FactoryState(item.State);
                row.Q<Label>("Progress").text = item.Duration > 0f ? $"{item.Progress:F0}/{item.Duration:F0}s" : string.Empty;
                row.Q<Label>("Reason").text = QueueText.Reason(item.BlockedReason);
            }
        }

        private static string ResolveRowChassisId(FactoryQueueItemRecord item)
        {
            if (item.Kind == FactoryQueueKind.Retrofit)
            {
                return MachineRegistry.TryGetRecord(item.TargetMachineLogicId, out MachineRecord target) ? target.ChassisId : null;
            }
            return HomeValleyLayout.FactoryProduceDefaults.TryGetValue(item.BlueprintId, out HomeValleyLayout.ProduceBlueprintDefault def)
                ? def.ChassisId
                : null;
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

            string reasonLine = string.IsNullOrEmpty(item.BlockedReason) ? string.Empty : $"\n原因：{item.BlockedReason}";
            string targetLine = item.Kind == FactoryQueueKind.Retrofit
                ? MachineRegistry.TryGetRecord(item.TargetMachineLogicId, out MachineRecord tgt) ? $"\n目标：#{tgt.DisplayNumber}" : "\n目标：（已不存在）"
                : string.Empty;
            _detailText.text = $"{(item.Kind == FactoryQueueKind.Retrofit ? "改造" : "生产")}：{ResolveBlueprintDisplayName(item.BlueprintId)} v{item.BlueprintVersion}" +
                $"{targetLine}\n状态：{item.State}\n进度：{item.Progress:F0}/{item.Duration:F0}秒{reasonLine}";
            _cancelButton.SetEnabled(item.State == FactoryQueueState.Queued || item.State == FactoryQueueState.WaitingResources
                || item.State == FactoryQueueState.WaitingPower || item.State == FactoryQueueState.WaitingTarget
                || item.State == FactoryQueueState.Running);
        }

        private static string ResolveChassisDisplayName(string chassisId)
        {
            string archetype = ChassisCatalog.ResolveArchetype(chassisId) ?? chassisId;
            return ChassisCatalog.TryGet(archetype, out MechanicalContentDef def) ? def.DisplayName : chassisId;
        }

        private static string ResolveBlueprintDisplayName(string blueprintId)
        {
            if (HomeValleyLayout.FactoryProduceDefaults.TryGetValue(blueprintId, out HomeValleyLayout.ProduceBlueprintDefault def)
                && !string.IsNullOrEmpty(def.DisplayName))
            {
                return def.DisplayName;
            }
            BlueprintRecord bp = BlueprintEditorService.Find(CampaignSession.Current, blueprintId);
            return !string.IsNullOrEmpty(bp?.DisplayName) ? bp.DisplayName : blueprintId;
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

        /// <summary>ER4-RETROFIT-01："机器详情中有'回厂改造'：选目标蓝图版本……"——目标机下拉框只列
        /// 当下真的能被排入改造的机器（存活/在家园/未占用出口/未直控/无在办工作单），与
        /// <see cref="HomeValleyFactory.TryEnqueueRetrofit"/> 的入队校验同一套判据，不在 UI 上摆一个点了
        /// 就会失败的选项。</summary>
        private void RefreshRetrofitTargets(CampaignState state)
        {
            _retrofitTargetLogicIdsByIndex.Clear();
            var choices = new List<string>();
            if (state != null)
            {
                foreach (MachineRecord m in MachineRegistry.AllRecords
                    .Where(m => m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !m.IsInFactory
                        && string.IsNullOrEmpty(m.CurrentWorkOrderId)
                        && !(GameRoot.HomeValley?.IsMachineDirectControlled(m.LogicId) ?? false))
                    .OrderBy(m => m.LogicId))
                {
                    choices.Add($"#{m.DisplayNumber} {ResolveChassisDisplayName(m.ChassisId)}（当前 v{m.BlueprintVersion}）");
                    _retrofitTargetLogicIdsByIndex.Add(m.LogicId);
                }
            }
            _retrofitTargetDropdown.choices = choices;
            if (choices.Count == 0)
            {
                _retrofitTargetDropdown.SetValueWithoutNotify(string.Empty);
                _retrofitVersionDropdown.choices = new List<string>();
                _retrofitVersionDropdown.SetValueWithoutNotify(string.Empty);
                _retrofitPreviewLabel.text = string.Empty;
                _retrofitConfirmButton.SetEnabled(false);
                return;
            }
            if (_retrofitTargetDropdown.index < 0 || _retrofitTargetDropdown.index >= choices.Count)
            {
                _retrofitTargetDropdown.index = 0;
            }
            RefreshRetrofitVersions(state);
        }

        /// <summary>目标机确定后，版本下拉框列出它所属蓝图记录的全部已保存版本（含当前版本——选中当前
        /// 版本等价于"重装同款"，成本会被 <see cref="HomeValleyFactory.RetrofitMinScrapCost"/> 下限钳到
        /// 最低工时费，不是错误，只是没有意义，玩家自己判断）。</summary>
        private void RefreshRetrofitVersions(CampaignState state)
        {
            _retrofitVersionNumbersByIndex.Clear();
            var choices = new List<string>();
            int targetIndex = _retrofitTargetDropdown.index;
            if (state != null && targetIndex >= 0 && targetIndex < _retrofitTargetLogicIdsByIndex.Count)
            {
                int logicId = _retrofitTargetLogicIdsByIndex[targetIndex];
                if (MachineRegistry.TryGetRecord(logicId, out MachineRecord m))
                {
                    BlueprintRecord bp = BlueprintEditorService.Find(state, m.BlueprintId);
                    foreach (BlueprintVersionRecord v in (bp?.Versions ?? System.Array.Empty<BlueprintVersionRecord>()).OrderBy(v => v.Version))
                    {
                        string cur = v.Version == m.BlueprintVersion ? "（当前）" : string.Empty;
                        choices.Add($"v{v.Version}{cur}｜{v.ScrapCost}废料");
                        _retrofitVersionNumbersByIndex.Add(v.Version);
                    }
                }
            }
            _retrofitVersionDropdown.choices = choices;
            if (choices.Count == 0)
            {
                _retrofitVersionDropdown.SetValueWithoutNotify(string.Empty);
                _retrofitPreviewLabel.text = string.Empty;
                _retrofitConfirmButton.SetEnabled(false);
                return;
            }
            if (_retrofitVersionDropdown.index < 0 || _retrofitVersionDropdown.index >= choices.Count)
            {
                _retrofitVersionDropdown.index = 0;
            }
            RefreshRetrofitPreview(state);
        }

        /// <summary>"显示旧/新装配及资源差额"——与 <see cref="HomeValleyFactory.TryEnqueueRetrofit"/> 内部
        /// 计算成本的公式完全一致（正差额且下限 <see cref="HomeValleyFactory.RetrofitMinScrapCost"/>），
        /// 预览与实际扣费不能是两套数字。</summary>
        private void RefreshRetrofitPreview(CampaignState state)
        {
            int targetIndex = _retrofitTargetDropdown.index;
            int versionIndex = _retrofitVersionDropdown.index;
            if (state == null || targetIndex < 0 || targetIndex >= _retrofitTargetLogicIdsByIndex.Count
                || versionIndex < 0 || versionIndex >= _retrofitVersionNumbersByIndex.Count)
            {
                _retrofitPreviewLabel.text = string.Empty;
                _retrofitConfirmButton.SetEnabled(false);
                return;
            }

            int logicId = _retrofitTargetLogicIdsByIndex[targetIndex];
            int newVersionNumber = _retrofitVersionNumbersByIndex[versionIndex];
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m))
            {
                _retrofitPreviewLabel.text = string.Empty;
                _retrofitConfirmButton.SetEnabled(false);
                return;
            }

            BlueprintRecord bp = BlueprintEditorService.Find(state, m.BlueprintId);
            BlueprintVersionRecord oldVersion = bp?.Versions?.FirstOrDefault(v => v.Version == m.BlueprintVersion);
            BlueprintVersionRecord newVersion = bp?.Versions?.FirstOrDefault(v => v.Version == newVersionNumber);
            if (oldVersion == null || newVersion == null)
            {
                _retrofitPreviewLabel.text = string.Empty;
                _retrofitConfirmButton.SetEnabled(false);
                return;
            }

            int cost = System.Math.Max(HomeValleyFactory.RetrofitMinScrapCost, newVersion.ScrapCost - oldVersion.ScrapCost);
            _retrofitPreviewLabel.text = $"旧装配 v{oldVersion.Version}（{oldVersion.ScrapCost}废料｜热量{oldVersion.HeatBudget:F0}）→ " +
                $"新装配 v{newVersion.Version}（{newVersion.ScrapCost}废料｜热量{newVersion.HeatBudget:F0}）\n" +
                $"改造费用：{cost}废料｜{HomeValleyFactory.RetrofitDurationSeconds:F0}秒";
            _retrofitConfirmButton.SetEnabled(true);
        }

        private void OnRetrofitConfirmClicked()
        {
            CampaignState state = CampaignSession.Current;
            int targetIndex = _retrofitTargetDropdown.index;
            int versionIndex = _retrofitVersionDropdown.index;
            if (state == null || targetIndex < 0 || targetIndex >= _retrofitTargetLogicIdsByIndex.Count
                || versionIndex < 0 || versionIndex >= _retrofitVersionNumbersByIndex.Count)
            {
                return;
            }
            int logicId = _retrofitTargetLogicIdsByIndex[targetIndex];
            int versionNumber = _retrofitVersionNumbersByIndex[versionIndex];
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m))
            {
                return;
            }
            bool directlyControlled = GameRoot.HomeValley?.IsMachineDirectControlled(logicId) ?? false;
            HomeValleyFactory.FactoryOpResult r = HomeValleyFactory.TryEnqueueRetrofit(
                state, logicId, m.BlueprintId, versionNumber, directlyControlled);
            _retrofitHintLabel.text = r.Success ? string.Empty : "改造排队失败：" + HomeValleyFactory.DescribeFailure(r.FailureReason);
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
