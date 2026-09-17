using System;
using System.Collections.Generic;
using BinGames.Sim;
using Cysharp.Threading.Tasks;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Blueprint;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Lineage;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.LineageWorkshop
{
    /// <summary>
    /// 将旧 IMGUI 的蓝图、模板版本和回巢改造入口转为玩家可用 UI。
    /// 所有写操作都转发给既有领域服务，UI 不复制校验或资源结算。
    /// </summary>
    public sealed class LineageWorkshopUIToolkit : MonoBehaviour
    {
        private const string PlayerLineageId = "player";
        private const float RefreshInterval = 0.15f;

        private enum WorkshopTab
        {
            Blueprints,
            Template,
            Units,
        }

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private VisualElement _blueprintView;
        private VisualElement _templateView;
        private VisualElement _unitView;
        private Button _blueprintTabButton;
        private Button _templateTabButton;
        private Button _unitTabButton;
        private Label _lineageBalanceLabel;
        private Label _feedbackLabel;
        private Label _templatePreviewLabel;
        private TextField _templateNameField;
        private TextField _doctrineField;
        private Button _commitTemplateButton;
        private ScrollView _blueprintList;
        private ScrollView _organelleList;
        private ScrollView _geneList;
        private ScrollView _templateSummaryList;
        private ScrollView _bindingList;
        private VisualElement _detailPanel;
        private Label _detailTitle;
        private ScrollView _detailList;
        private readonly List<string> _selectedGenes = new List<string>(LineageRegistry.MaxGeneSlots);
        private string _selectedOrganelle;
        private WorkshopTab _activeTab;
        private bool _panelOpen;
        private float _nextRefreshTime;

        public static LineageWorkshopUIToolkit Instance { get; private set; }
        public bool IsPanelOpen => _panelOpen;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("LineageWorkshopUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 11;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[LineageWorkshopUIToolkit] rootVisualElement 等待超时，表型工坊未初始化。");
                return;
            }

            CacheNodes();
            ApplyPanelState();
            SetTab(WorkshopTab.Blueprints);
        }

        public void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (!open)
            {
                HideDetail();
            }
            InputRouter.SetModalUi(open);
            ApplyPanelState();
            if (open)
            {
                _nextRefreshTime = 0f;
                UiWindowFocus.BringToFront(_document, _root?.Q<VisualElement>("workshopPanel"));
            }
        }

        private void CacheNodes()
        {
            _blueprintView = _root.Q<VisualElement>("blueprintView");
            _templateView = _root.Q<VisualElement>("templateView");
            _unitView = _root.Q<VisualElement>("unitView");
            _blueprintTabButton = _root.Q<Button>("blueprintTabButton");
            _templateTabButton = _root.Q<Button>("templateTabButton");
            _unitTabButton = _root.Q<Button>("unitTabButton");
            _lineageBalanceLabel = _root.Q<Label>("lineageBalanceLabel");
            _feedbackLabel = _root.Q<Label>("workshopFeedbackLabel");
            _templatePreviewLabel = _root.Q<Label>("templatePreviewLabel");
            _templateNameField = _root.Q<TextField>("templateNameField");
            _doctrineField = _root.Q<TextField>("doctrineField");
            _commitTemplateButton = _root.Q<Button>("commitTemplateButton");
            _blueprintList = _root.Q<ScrollView>("blueprintList");
            _organelleList = _root.Q<ScrollView>("organelleList");
            _geneList = _root.Q<ScrollView>("geneList");
            _templateSummaryList = _root.Q<ScrollView>("templateSummaryList");
            _bindingList = _root.Q<ScrollView>("bindingList");
            _detailPanel = _root.Q<VisualElement>("workshopDetailPanel");
            _detailTitle = _root.Q<Label>("workshopDetailTitle");
            _detailList = _root.Q<ScrollView>("workshopDetailList");

            BindClick("closeWorkshopButton", () => SetPanelOpen(false));
            BindClick("closeWorkshopDetailButton", HideDetail);
            if (_blueprintTabButton != null)
            {
                _blueprintTabButton.clicked += () => SetTab(WorkshopTab.Blueprints);
            }
            if (_templateTabButton != null)
            {
                _templateTabButton.clicked += () => SetTab(WorkshopTab.Template);
            }
            if (_unitTabButton != null)
            {
                _unitTabButton.clicked += () => SetTab(WorkshopTab.Units);
            }
            if (_commitTemplateButton != null)
            {
                _commitTemplateButton.clicked += CommitTemplate;
            }

            VisualElement workshopRoot = _root.Q<VisualElement>("lineageWorkshopRoot");
            if (workshopRoot != null)
            {
                workshopRoot.pickingMode = PickingMode.Ignore;
            }
            VisualElement workshopPanel = _root.Q<VisualElement>("workshopPanel");
            UiWindowFocus.Attach(_document, workshopPanel, workshopPanel?.Q<Label>(className: "workshop-title"), "workshop");
            UiWindowFocus.Attach(_document, _detailPanel, _detailTitle, "workshop-detail");
        }

        private void BindClick(string nodeName, Action action)
        {
            Button button = _root.Q<Button>(nodeName);
            if (button != null)
            {
                button.clicked += action;
            }
        }

        private void Update()
        {
            if (!_panelOpen)
            {
                return;
            }

            CellStageFlow cell = GameRoot.CellStage;
            if (cell == null || !cell.IsRunning)
            {
                SetPanelOpen(false);
                return;
            }

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshInterval;
                RefreshAll(cell);
            }
        }

        private void SetTab(WorkshopTab tab)
        {
            _activeTab = tab;
            _blueprintView?.EnableInClassList("is-hidden", tab != WorkshopTab.Blueprints);
            _templateView?.EnableInClassList("is-hidden", tab != WorkshopTab.Template);
            _unitView?.EnableInClassList("is-hidden", tab != WorkshopTab.Units);
            _blueprintTabButton?.EnableInClassList("is-selected", tab == WorkshopTab.Blueprints);
            _templateTabButton?.EnableInClassList("is-selected", tab == WorkshopTab.Template);
            _unitTabButton?.EnableInClassList("is-selected", tab == WorkshopTab.Units);
            _nextRefreshTime = 0f;
        }

        private void ApplyPanelState()
        {
            _root?.EnableInClassList("is-hidden", !_panelOpen);
        }

        private void RefreshAll(CellStageFlow cell)
        {
            if (cell.Lineages == null || cell.Blueprints == null)
            {
                SetFeedback("谱系或蓝图模块尚未就绪。");
                return;
            }

            Lineage lineage = cell.Lineages.GetOrCreateLineage(PlayerLineageId, "玩家谱系");
            float biomass = cell.BiomassLedger?.GetBalance(PlayerLineageId) ?? 0f;
            if (_lineageBalanceLabel != null)
            {
                _lineageBalanceLabel.text = $"{lineage.DisplayName} · 生物质 {biomass:F0}";
            }

            if (_activeTab == WorkshopTab.Blueprints)
            {
                RefreshBlueprints(cell);
            }
            else if (_activeTab == WorkshopTab.Template)
            {
                RefreshTemplateEditor(cell);
            }
            else
            {
                RefreshUnits(cell, lineage);
            }
        }

        private void RefreshBlueprints(CellStageFlow cell)
        {
            if (_blueprintList == null)
            {
                return;
            }

            _blueprintList.Clear();
            var entries = new List<BlueprintEntry>(cell.Blueprints.AllEntries);
            entries.Sort((left, right) => string.CompareOrdinal(left.SourceId, right.SourceId));
            if (entries.Count == 0)
            {
                AddEmptyRow(_blueprintList, "尚未解析任何蓝图。先在战场解析野生器官或基因。");
                return;
            }

            foreach (BlueprintEntry entry in entries)
            {
                string kind = entry.Kind == BlueprintSourceKind.Organelle ? "器官" : "基因";
                string displayName = GetBlueprintDisplayName(entry);
                string description = GetBlueprintDescription(entry);
                string state = entry.Unlocked ? "已解锁" : $"解析度 {entry.Completeness:P0}";
                var row = CreateRow($"[{kind}] {displayName} · {state}",
                    $"污染 {entry.Contamination:P0}" + (string.IsNullOrEmpty(description) ? string.Empty : $"\n{description}"));
                BlueprintEntry capturedEntry = entry;
                var details = new Button { text = "详情" };
                details.AddToClassList("workshop-row-button");
                details.clicked += () => ShowBlueprintDetail(capturedEntry);
                row.Add(details);
                _blueprintList.Add(row);
            }
        }

        private void RefreshTemplateEditor(CellStageFlow cell)
        {
            if (_organelleList == null || _geneList == null)
            {
                return;
            }

            _organelleList.Clear();
            _geneList.Clear();
            var entries = new List<BlueprintEntry>(cell.Blueprints.AllEntries);
            entries.Sort((left, right) => string.CompareOrdinal(left.SourceId, right.SourceId));
            int organelleCount = 0;
            int geneCount = 0;
            foreach (BlueprintEntry entry in entries)
            {
                if (!entry.Unlocked)
                {
                    continue;
                }

                if (entry.Kind == BlueprintSourceKind.Organelle)
                {
                    organelleCount++;
                    AddOrganellePicker(entry.SourceId, GetBlueprintDisplayName(entry));
                }
                else if (entry.Kind == BlueprintSourceKind.Gene)
                {
                    geneCount++;
                    AddGenePicker(entry.SourceId, GetBlueprintDisplayName(entry));
                }
            }

            if (organelleCount == 0)
            {
                AddEmptyRow(_organelleList, "没有已解锁器官蓝图。");
            }
            if (geneCount == 0)
            {
                AddEmptyRow(_geneList, "没有已解锁基因蓝图。");
            }

            string error = cell.Lineages.PreviewCommit(_selectedOrganelle, _selectedGenes);
            bool hasName = !string.IsNullOrWhiteSpace(_templateNameField?.value);
            if (_templatePreviewLabel != null)
            {
                _templatePreviewLabel.text = error == null
                    ? "校验通过：提交会创建新版本；现有单位保持当前版本。"
                    : $"无法提交：{error}";
            }
            _commitTemplateButton?.SetEnabled(error == null && hasName);
        }

        private void AddOrganellePicker(string sourceId, string displayName)
        {
            var button = new Button { text = displayName };
            button.AddToClassList("workshop-picker");
            button.EnableInClassList("is-selected", _selectedOrganelle == sourceId);
            button.clicked += () =>
            {
                _selectedOrganelle = _selectedOrganelle == sourceId ? null : sourceId;
                _nextRefreshTime = 0f;
            };
            _organelleList.Add(button);
        }

        private void AddGenePicker(string sourceId, string displayName)
        {
            bool selected = _selectedGenes.Contains(sourceId);
            var toggle = new Toggle(displayName) { value = selected };
            toggle.AddToClassList("workshop-picker");
            toggle.EnableInClassList("is-selected", selected);
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    if (_selectedGenes.Count >= LineageRegistry.MaxGeneSlots)
                    {
                        toggle.SetValueWithoutNotify(false);
                        SetFeedback($"一个模板最多选择 {LineageRegistry.MaxGeneSlots} 个基因。");
                        return;
                    }
                    _selectedGenes.Add(sourceId);
                }
                else
                {
                    _selectedGenes.Remove(sourceId);
                }
                _nextRefreshTime = 0f;
            });
            _geneList.Add(toggle);
        }

        private void CommitTemplate()
        {
            CellStageFlow cell = GameRoot.CellStage;
            if (cell?.Lineages == null)
            {
                SetFeedback("谱系模块未就绪，无法提交模板。");
                return;
            }

            string templateName = _templateNameField?.value?.Trim();
            string doctrine = _doctrineField?.value?.Trim();
            PhenotypeTemplateVersion committed = cell.Lineages.CommitTemplate(
                PlayerLineageId, templateName, _selectedOrganelle, _selectedGenes, doctrine, out string error);
            SetFeedback(committed == null
                ? $"提交失败：{error}"
                : $"已提交「{templateName}」V{committed.Version}。新生与完成回巢的单位会使用该版本。");
            _nextRefreshTime = 0f;
        }

        private void RefreshUnits(CellStageFlow cell, Lineage lineage)
        {
            RefreshTemplateSummaries(cell, lineage);
            RefreshBindings(cell);
        }

        private void RefreshTemplateSummaries(CellStageFlow cell, Lineage lineage)
        {
            if (_templateSummaryList == null)
            {
                return;
            }

            _templateSummaryList.Clear();
            var names = new List<string>(lineage.TemplateNames);
            names.Sort(StringComparer.Ordinal);
            if (names.Count == 0)
            {
                AddEmptyRow(_templateSummaryList, "还没有已提交模板。先在“模板编辑”页创建一个版本。");
                return;
            }

            int pending = cell.GerminationChambers?.PendingCount(PlayerLineageId) ?? 0;
            foreach (string name in names)
            {
                PhenotypeTemplateVersion latest = lineage.GetLatest(name);
                if (latest == null)
                {
                    continue;
                }
                VisualElement row = CreateRow($"{name} · V{latest.Version}",
                    $"主器官 {latest.OrganelleId} · 基因 {latest.GeneIds.Count} · 萌生成本 {latest.BiomassCost:F0}\n萌生队列总数 {pending}");
                var actions = new VisualElement();
                actions.AddToClassList("workshop-row-actions");
                var button = new Button { text = "打开萌生腔" };
                button.AddToClassList("workshop-row-button");
                button.clicked += () =>
                {
                    SetPanelOpen(false);
                    BattleGerminationUIToolkit.Instance?.SetPanelOpen(true);
                };
                actions.Add(button);
                string capturedName = name;
                var history = new Button { text = "版本历史" };
                history.AddToClassList("workshop-row-button");
                history.clicked += () => ShowTemplateHistory(lineage, capturedName);
                actions.Add(history);
                row.Add(actions);
                _templateSummaryList.Add(row);
            }
        }

        private void RefreshBindings(CellStageFlow cell)
        {
            if (_bindingList == null)
            {
                return;
            }

            _bindingList.Clear();
            if (cell.GerminationChambers == null || cell.Lineages == null)
            {
                AddEmptyRow(_bindingList, "萌生腔或谱系模块未就绪。");
                return;
            }

            int count = 0;
            foreach (KeyValuePair<SimEntityId, GerminationChamberRegistry.UnitBinding> pair in cell.GerminationChambers.Bindings)
            {
                SimEntityId entityId = pair.Key;
                GerminationChamberRegistry.UnitBinding binding = pair.Value;
                Lineage bindingLineage = cell.Lineages.GetLineage(binding.LineageId);
                PhenotypeTemplateVersion latest = bindingLineage?.GetLatest(binding.TemplateName);
                bool outdated = latest != null && latest.Version != binding.Version.Version;
                bool retrofitting = cell.HomecomingRetrofit?.IsRetrofitting(entityId) ?? false;
                string state = retrofitting ? "回巢中" : (outdated ? $"可更新到 V{latest.Version}" : "已是最新版本");
                VisualElement row = CreateRow($"单位 #{entityId} · {binding.TemplateName} V{binding.Version.Version}", state);

                if (retrofitting || outdated)
                {
                    var actions = new VisualElement();
                    actions.AddToClassList("workshop-row-actions");
                    if (retrofitting)
                    {
                        var cancel = new Button { text = "取消回巢并退款" };
                        cancel.AddToClassList("workshop-row-button");
                        cancel.clicked += () =>
                        {
                            bool cancelled = GameRoot.CellStage?.HomecomingRetrofit?.CancelRetrofit(entityId) ?? false;
                            SetFeedback(cancelled ? $"单位 #{entityId} 已取消回巢，费用已退回。" : "回巢已完成或不存在，无法取消。");
                            _nextRefreshTime = 0f;
                        };
                        actions.Add(cancel);
                    }
                    else
                    {
                        var begin = new Button { text = "开始回巢改造" };
                        begin.AddToClassList("workshop-row-button");
                        begin.clicked += () => BeginRetrofit(entityId);
                        actions.Add(begin);
                    }
                    row.Add(actions);
                }

                var details = new Button { text = "详情" };
                details.AddToClassList("workshop-row-button");
                string detailState = state;
                details.clicked += () => ShowBindingDetail(entityId, binding, latest, detailState);
                row.Add(details);

                _bindingList.Add(row);
                count++;
            }

            if (count == 0)
            {
                AddEmptyRow(_bindingList, "尚未有由萌生腔产生并绑定谱系的单位。");
            }
        }

        private void BeginRetrofit(SimEntityId entityId)
        {
            HomecomingRetrofitService.RetrofitRejectReason result =
                GameRoot.CellStage?.HomecomingRetrofit?.TryBeginRetrofit(entityId)
                ?? HomecomingRetrofitService.RetrofitRejectReason.NotBound;
            SetFeedback(result == HomecomingRetrofitService.RetrofitRejectReason.None
                ? $"单位 #{entityId} 已开始回巢；完成前不会参与战斗。"
                : $"无法开始回巢：{result}");
            _nextRefreshTime = 0f;
        }

        private void ShowBlueprintDetail(BlueprintEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            string kind = entry.Kind == BlueprintSourceKind.Organelle ? "器官蓝图" : "基因蓝图";
            ShowDetail($"{kind} · {GetBlueprintDisplayName(entry)}",
                $"来源 ID：{entry.SourceId}",
                $"解析度：{entry.Completeness:P0} · 污染：{entry.Contamination:P0}",
                $"重复解析次数：{entry.RepeatResolveCount}",
                entry.Unlocked ? "状态：已解锁，可用于新模板。" : "状态：尚未解锁，继续解析可提高完整度。",
                GetBlueprintDescription(entry));
        }

        private void ShowTemplateHistory(Lineage lineage, string templateName)
        {
            if (lineage == null)
            {
                return;
            }

            var lines = new List<string>();
            IReadOnlyList<PhenotypeTemplateVersion> history = lineage.GetHistory(templateName);
            for (int i = history.Count - 1; i >= 0; i--)
            {
                PhenotypeTemplateVersion version = history[i];
                string marker = i == history.Count - 1 ? "（当前最新）" : "（历史快照）";
                lines.Add($"V{version.Version} {marker}");
                lines.Add($"主器官：{version.OrganelleId} · 基因：{string.Join("、", version.GeneIds)}");
                lines.Add($"教义：{version.DoctrineTag ?? "未设置"} · 萌生成本：{version.BiomassCost:F0}");
                lines.Add($"签名：{version.Signature}");
            }
            ShowDetail($"模板历史 · {templateName}", lines);
        }

        private void ShowBindingDetail(SimEntityId entityId, GerminationChamberRegistry.UnitBinding binding,
            PhenotypeTemplateVersion latest, string state)
        {
            ShowDetail($"单位 #{entityId}",
                $"模板：{binding.TemplateName} · 当前版本 V{binding.Version.Version}",
                $"主器官：{binding.Version.OrganelleId}",
                $"基因：{string.Join("、", binding.Version.GeneIds)}",
                $"最新版本：{(latest == null ? "无" : "V" + latest.Version)}",
                $"状态：{state}");
        }

        private void ShowDetail(string title, params string[] lines)
        {
            ShowDetail(title, (IEnumerable<string>)lines);
        }

        private void ShowDetail(string title, IEnumerable<string> lines)
        {
            if (_detailPanel == null || _detailList == null)
            {
                return;
            }

            _detailTitle.text = title;
            _detailList.Clear();
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                var label = new Label(line);
                label.AddToClassList("workshop-row-meta");
                _detailList.Add(label);
            }
            _detailPanel.RemoveFromClassList("is-hidden");
            UiWindowFocus.BringToFront(_document, _detailPanel);
        }

        private void HideDetail()
        {
            _detailPanel?.AddToClassList("is-hidden");
        }

        private static VisualElement CreateRow(string title, string meta)
        {
            var row = new VisualElement();
            row.AddToClassList("workshop-list-row");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("workshop-row-title");
            row.Add(titleLabel);
            if (!string.IsNullOrEmpty(meta))
            {
                var metaLabel = new Label(meta);
                metaLabel.AddToClassList("workshop-row-meta");
                row.Add(metaLabel);
            }
            return row;
        }

        private static void AddEmptyRow(ScrollView list, string message)
        {
            list.Add(CreateRow(message, string.Empty));
        }

        private static string GetBlueprintDisplayName(BlueprintEntry entry)
        {
            if (entry.Kind == BlueprintSourceKind.Organelle)
            {
                return OrganelleCatalog.Get(entry.SourceId)?.DisplayName ?? entry.SourceId;
            }
            return GeneCatalog.GetDisplayName(entry.SourceId) ?? entry.SourceId;
        }

        private static string GetBlueprintDescription(BlueprintEntry entry)
        {
            if (entry.Kind == BlueprintSourceKind.Organelle)
            {
                return OrganelleCatalog.Get(entry.SourceId)?.Description;
            }
            return GeneCatalog.GetDescription(entry.SourceId);
        }

        private void SetFeedback(string message)
        {
            if (_feedbackLabel != null)
            {
                _feedbackLabel.text = message ?? string.Empty;
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
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
