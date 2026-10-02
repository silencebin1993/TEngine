using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.UI.Common;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
using GameLogic.Localization;
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
    /// 的解析台建筑切换），与 <see cref="Expedition.ExpeditionPrepPanelUIToolkit"/> 同一套写法。
    ///
    /// FG5-RND-02（FG13 FGU-23 解析台：队列、预览、取消）：状态行（空闲时“解析台空闲：没有待解析的物品”、解析中、缺电、队列满）、
    /// 待解析的敌方物品（仓库里的三类敌方物品按身份合并、固件库里未破解的芯片、Demo 区域任务物）与所选那一行的结果预览（第一次遇到的类型显示“？？”）、
    /// 队列 n/8（每行状态 / 进度 / 预览或受阻原因 / 行内取消）、残骸栏（缓存、进度、送入）、读过的资料。全部文字走文本键。</summary>
    public sealed class AnalysisPanelUIToolkit : MonoBehaviour
    {
        /// <summary>行池：队列容量 + 保留的最近结束记录 + Demo 任务物完成记录的余量。</summary>
        private const int MaxQueueRows = 24;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _title;
        private Label _statusLabel;
        private Label _listTitle1;
        private Label _listTitle2;
        private DropdownField _warehouseDropdown;
        private Button _enqueueButton;
        private Label _heldPreview;
        private ScrollView _queueList;
        private DropdownField _cancelDropdown;
        private Button _cancelButton;
        private Label _wreckTitle;
        private Label _wreckLine;
        private Button _wreckButton;
        private Label _loreLabel;
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
        private readonly string[] _rowQueueIds = new string[MaxQueueRows];
        private readonly List<HomeValleyAnalysis.HeldEntry> _held = new List<HomeValleyAnalysis.HeldEntry>();
        private readonly List<HomeValleyAnalysis.HeldEntry> _heldShown = new List<HomeValleyAnalysis.HeldEntry>();
        private readonly List<string> _cancelChoiceQueueIds = new List<string>();
        private readonly List<AnalysisQueueItemRecord> _rowsScratch = new List<AnalysisQueueItemRecord>(MaxQueueRows);

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

            if (_document.rootVisualElement == null)
            {
                Log.Error("[AnalysisPanelUIToolkit] rootVisualElement 等待超时，解析台面板未初始化。");
                return;
            }
            Bind(_document.rootVisualElement, _rowTemplate);
        }

        /// <summary>自检：把真 UXML（AnalysisPanel.uxml 与行模板）接到这个组件上，走与运行时同一份绑定代码。</summary>
        public void BindForTests(VisualElement root, VisualTreeAsset rowTemplate) => Bind(root, rowTemplate);

        private void Bind(VisualElement root, VisualTreeAsset rowTemplate)
        {
            _root = root;
            _panel = _root.Q<VisualElement>("AnalysisPanelRoot");
            _title = _root.Q<Label>("Title");
            _statusLabel = _root.Q<Label>("StatusLabel");
            _listTitle1 = _root.Q<Label>("ListTitle1");
            _listTitle2 = _root.Q<Label>("ListTitle2");
            _warehouseDropdown = _root.Q<DropdownField>("WarehouseDropdown");
            _enqueueButton = _root.Q<Button>("EnqueueButton");
            _heldPreview = _root.Q<Label>("HeldPreview");
            _queueList = _root.Q<ScrollView>("QueueList");
            _cancelDropdown = _root.Q<DropdownField>("CancelDropdown");
            _cancelButton = _root.Q<Button>("CancelButton");
            _wreckTitle = _root.Q<Label>("WreckTitle");
            _wreckLine = _root.Q<Label>("WreckLine");
            _wreckButton = _root.Q<Button>("WreckButton");
            _loreLabel = _root.Q<Label>("LoreLabel");
            _resultLabel = _root.Q<Label>("ResultLabel");
            _closeButton = _root.Q<Button>("CloseButton");
            _restoreTitle = _root.Q<Label>("RestoreTitle");
            _restoreHint = _root.Q<Label>("RestoreHint");
            _restoreDropdown = _root.Q<DropdownField>("RestoreDropdown");
            _restoreButton = _root.Q<Button>("RestoreButton");
            _restoreDetail = _root.Q<Label>("RestoreDetail");
            ApplyStaticTexts();
            if (_restoreButton != null)
            {
                _restoreButton.clicked += OnRestoreClicked;
            }
            _restoreDropdown?.RegisterValueChangedCallback(_ => RefreshRestoreDetail());
            _warehouseDropdown?.RegisterValueChangedCallback(_ => RefreshHeldPreview());

            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                int slot = i;
                Button rowCancel = row.Q<Button>("RowCancel");
                if (rowCancel != null)
                {
                    rowCancel.clicked += () => OnRowCancelClicked(slot);
                }
                _queueList.Add(row);
                _rowPool.Add(row);
            }

            _enqueueButton.clicked += OnEnqueueClicked;
            _cancelButton.clicked += OnCancelClicked;
            _closeButton.clicked += OnCloseClicked;
            if (_wreckButton != null)
            {
                _wreckButton.clicked += OnWreckClicked;
            }
        }

        private void ApplyStaticTexts()
        {
            SetText(_title, GameText.Get("analysis.panel.title"));
            SetText(_listTitle1, GameText.Get("analysis.panel.held_title"));
            SetText(_wreckTitle, GameText.Get("analysis.wreck.title"));
            SetText(_restoreTitle, GameText.Get("analysis.restore.title"));
            if (_enqueueButton != null)
            {
                _enqueueButton.text = GameText.Get("analysis.panel.enqueue");
            }
            if (_cancelButton != null)
            {
                _cancelButton.text = GameText.Get("analysis.panel.cancel");
            }
            if (_wreckButton != null)
            {
                _wreckButton.text = GameText.Get("analysis.wreck.send");
            }
            if (_restoreButton != null)
            {
                _restoreButton.text = GameText.Get("analysis.restore.button");
            }
            if (_closeButton != null)
            {
                _closeButton.text = GameText.Get("analysis.panel.close");
            }
        }

        private static void SetText(Label l, string text)
        {
            if (l != null && l.text != text)
            {
                l.text = text;
            }
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
            if (!_wasOpen)
            {
                _refreshTimer = 0f;
                ApplyStaticTexts(); // 语言可能在面板关着时切换过
                // 身份清单对账（物品被回收站分解 / 被毁后多出来的身份）：只在打开面板时做一次。
                CampaignState s = CampaignSession.Current;
                HomeValleyAnalysis.ReconcileTags(s, d => HomeValleyAnalysis.PhysicalCount(s, d));
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

        // ── 自检 / 冒烟读点 ──────────────────────────────────────────────────────

        public string StatusText => _statusLabel?.text ?? string.Empty;
        public string HeldPreviewText => _heldPreview?.text ?? string.Empty;
        public string WreckText => _wreckLine?.text ?? string.Empty;
        public string LoreText => _loreLabel?.text ?? string.Empty;
        public string QueueTitleText => _listTitle2?.text ?? string.Empty;
        public IReadOnlyList<HomeValleyAnalysis.HeldEntry> HeldChoices => _heldShown;
        public IReadOnlyList<string> CancelChoiceIds => _cancelChoiceQueueIds;

        /// <summary>第 <paramref name="i"/> 行显示的队列项（没有 = null）。</summary>
        public string RowQueueId(int i) => i >= 0 && i < _rowQueueIds.Length ? _rowQueueIds[i] : null;

        public string RowText(int i, string label)
        {
            if (i < 0 || i >= _rowPool.Count)
            {
                return string.Empty;
            }
            return _rowPool[i].Q<Label>(label)?.text ?? string.Empty;
        }

        /// <summary>选中待解析清单的第 <paramref name="index"/> 行（与玩家在下拉框里点选同一路径）。</summary>
        public bool SelectHeld(int index)
        {
            if (_warehouseDropdown == null || index < 0 || index >= _warehouseDropdown.choices.Count || _heldShown.Count == 0)
            {
                return false;
            }
            _warehouseDropdown.value = _warehouseDropdown.choices[index];
            return true;
        }

        /// <summary>立刻按当前状态刷新一次（自检用；与定时刷新同一个方法）。</summary>
        public void RefreshNow() => Refresh();

        private void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            SetText(_statusLabel, HomeValleyAnalysis.StatusLine(state));

            // ── 待解析的敌方物品 + 预览 ──
            HomeValleyAnalysis.CollectHeld(state, _held);
            _heldShown.Clear();
            var choices = new List<string>(_held.Count);
            foreach (HomeValleyAnalysis.HeldEntry e in _held)
            {
                _heldShown.Add(e);
                choices.Add(HeldLabel(state, e));
            }
            DropdownChoices.Apply(_warehouseDropdown, choices, GameText.Get("analysis.panel.held_empty"));
            _enqueueButton.SetEnabled(_heldShown.Count > 0);
            RefreshHeldPreview();

            // ── 队列：在办的在前（按入队先后），最近结束的在后 ──
            AnalysisQueueItemRecord[] queues = state?.AnalysisQueues ?? System.Array.Empty<AnalysisQueueItemRecord>();
            SetText(_listTitle2, GameText.Format("analysis.panel.queue_title", HomeValleyAnalysis.ActiveCount(state), AnalysisCatalog.QueueCapacity));
            _rowsScratch.Clear();
            foreach (AnalysisQueueItemRecord q in queues)
            {
                if (q != null)
                {
                    _rowsScratch.Add(q);
                }
            }
            _rowsScratch.Sort((a, b) =>
            {
                bool aa = HomeValleyAnalysis.IsActiveEntry(a);
                bool ba = HomeValleyAnalysis.IsActiveEntry(b);
                if (aa != ba)
                {
                    return aa ? -1 : 1;
                }
                int c = aa ? a.CreatedTick.CompareTo(b.CreatedTick) : b.CreatedTick.CompareTo(a.CreatedTick);
                return c != 0 ? c : string.CompareOrdinal(a.QueueItemId, b.QueueItemId);
            });
            for (int i = 0; i < MaxQueueRows; i++)
            {
                TemplateContainer row = _rowPool[i];
                if (i >= _rowsScratch.Count)
                {
                    row.style.display = DisplayStyle.None;
                    _rowQueueIds[i] = null;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                AnalysisQueueItemRecord q = _rowsScratch[i];
                _rowQueueIds[i] = q.QueueItemId;
                bool active = HomeValleyAnalysis.IsActiveEntry(q);
                row.Q<Label>("Name").text = HomeValleyAnalysis.EntryName(q);
                if (HomeValleyAnalysis.IsQuestEntry(q))
                {
                    HomeValleyAnalysis.YieldTable.TryGetValue(q.ContentId ?? string.Empty, out HomeValleyAnalysis.YieldInfo info);
                    ContentIcons.Apply(row.Q<VisualElement>("Icon"), info.UnlockContentId);
                }
                else
                {
                    // 固件库里的芯片本来就认识；敌方加密固件只有“已知类型”才显示那枚固件的图标——第一次遇到的预览写“？？”，图标不能先把身份泄露出来。
                    bool showTarget = q.Source == HomeValleyAnalysis.SourceChip
                                      || (q.ItemId == AnalysisCatalog.EncryptedFirmwareId && HomeValleyAnalysis.IsKnown(state, q.ItemId, q.TargetId));
                    ContentIcons.Apply(row.Q<VisualElement>("Icon"), showTarget ? q.TargetId : null);
                }
                row.Q<Label>("State").text = HomeValleyAnalysis.StateText(q.State);
                row.Q<Label>("Progress").text = GameText.Format("analysis.panel.row_progress", q.Progress.ToString("0.0"), q.Duration.ToString("0.0"));
                string blocked = HomeValleyAnalysis.BlockedText(q.BlockedReason);
                row.Q<Label>("Reason").text = !string.IsNullOrEmpty(blocked) ? blocked : RowPreview(state, q);
                Button rc = row.Q<Button>("RowCancel");
                if (rc != null)
                {
                    rc.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }

            _cancelChoiceQueueIds.Clear();
            var cancelChoices = new List<string>();
            foreach (AnalysisQueueItemRecord q in _rowsScratch)
            {
                if (!HomeValleyAnalysis.IsActiveEntry(q))
                {
                    continue;
                }
                cancelChoices.Add(GameText.Format("analysis.panel.cancel_item", HomeValleyAnalysis.EntryName(q), HomeValleyAnalysis.StateText(q.State)));
                _cancelChoiceQueueIds.Add(q.QueueItemId);
            }
            DropdownChoices.Apply(_cancelDropdown, cancelChoices, GameText.Get("analysis.panel.cancel_empty"));
            _cancelButton.SetEnabled(_cancelChoiceQueueIds.Count > 0);

            SetText(_wreckLine, HomeValleyAnalysis.WreckLine(state));
            SetText(_loreLabel, HomeValleyAnalysis.LoreLine(state));
            RefreshRestore(state);
        }

        private static string HeldLabel(CampaignState state, HomeValleyAnalysis.HeldEntry e)
        {
            if (e.Source == HomeValleyAnalysis.SourceChip)
            {
                return GameText.Format("analysis.panel.held_chip", HomeValleyAnalysis.EntryName(e.Source, e.ItemId, e.TargetId),
                    HomeValleyAnalysis.Preview(state, e.Source, e.ItemId, e.TargetId));
            }
            if (string.IsNullOrEmpty(e.Source))
            {
                return GameText.Format("analysis.panel.held_quest", HomeValleyAnalysis.EntryName(e.Source, e.ItemId, e.TargetId, e.TargetId),
                    HomeValleyAnalysis.QuestPreview(state, e.TargetId));
            }
            string name = HomeValleyAnalysis.EntryName(e.Source, e.ItemId, e.TargetId);
            string preview = HomeValleyAnalysis.Preview(state, e.Source, e.ItemId, e.TargetId);
            if (string.IsNullOrEmpty(e.TagId))
            {
                return GameText.Format("analysis.panel.held_untagged", name, e.Count, preview);
            }
            return GameText.Format("analysis.panel.held_item", e.Count > 1 ? name + " ×" + e.Count : name, preview);
        }

        private static string RowPreview(CampaignState state, AnalysisQueueItemRecord q)
        {
            if (HomeValleyAnalysis.IsQuestEntry(q))
            {
                return HomeValleyAnalysis.QuestPreview(state, q.ContentId);
            }
            if (q.State == AnalysisQueueState.Completed)
            {
                return q.TechGained > 0 ? "+" + q.TechGained : string.Empty;
            }
            return HomeValleyAnalysis.Preview(state, q.Source, q.ItemId, q.TargetId);
        }

        private int SelectedHeldIndex()
        {
            if (_warehouseDropdown == null || _heldShown.Count == 0)
            {
                return -1;
            }
            int i = _warehouseDropdown.index;
            return i >= 0 && i < _heldShown.Count ? i : -1;
        }

        private void RefreshHeldPreview()
        {
            if (_heldPreview == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int i = SelectedHeldIndex();
            if (i < 0)
            {
                SetText(_heldPreview, string.Empty);
                return;
            }
            HomeValleyAnalysis.HeldEntry e = _heldShown[i];
            float seconds;
            string preview;
            if (string.IsNullOrEmpty(e.Source))
            {
                HomeValleyAnalysis.YieldTable.TryGetValue(e.TargetId ?? string.Empty, out HomeValleyAnalysis.YieldInfo info);
                seconds = info.Duration;
                preview = HomeValleyAnalysis.QuestPreview(state, e.TargetId);
            }
            else
            {
                AnalysisCatalog.TryGetKind(e.ItemId, out AnalysisKindDef def);
                seconds = def?.Seconds ?? 0f;
                preview = HomeValleyAnalysis.Preview(state, e.Source, e.ItemId, e.TargetId);
            }
            SetText(_heldPreview, preview + "  " + HomeValleyAnalysis.TimeLine(seconds));
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
                _restoreHint.text = GameText.Get("analysis.restore.hint") + " " +
                                    GameText.Format("analysis.restore.tech", state?.TechData ?? 0);
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
                ? GameText.Get("analysis.restore.empty")
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
                _resultLabel.text = GameText.Get("analysis.restore.reason.none_selected");
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
                Title = GameText.Get("analysis.restore.confirm.title"),
                Irreversible = true,
                ConfirmText = GameText.Get("analysis.restore.confirm.ok"),
                CancelText = GameText.Get("fwlib.confirm.cancel"),
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

        // ── 送入 / 取消 / 残骸 ───────────────────────────────────────────────────

        private void OnEnqueueClicked()
        {
            int i = SelectedHeldIndex();
            if (i < 0)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                _resultLabel.text = GameText.Get("analysis.reason.none_selected");
                return;
            }
            HomeValleyAnalysis.HeldEntry e = _heldShown[i];
            string name = HomeValleyAnalysis.EntryName(e.Source, e.ItemId, e.TargetId, e.TargetId);
            HomeValleyAnalysis.AnalysisOpResult result = HomeValleyAnalysis.TryEnqueueHeld(CampaignSession.Current, e);
            if (!result.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _resultLabel.text = result.Success ? GameText.Format("analysis.panel.enqueued", name) : HomeValleyAnalysis.ReasonText(result.FailureReason);
            Refresh();
        }

        private void OnCancelClicked()
        {
            int i = _cancelDropdown != null ? _cancelDropdown.index : -1;
            if (i < 0 || i >= _cancelChoiceQueueIds.Count)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
                _resultLabel.text = GameText.Get("analysis.panel.cancel_empty");
                return;
            }
            CancelQueueItem(_cancelChoiceQueueIds[i]);
        }

        private void OnRowCancelClicked(int slot)
        {
            string id = RowQueueId(slot);
            if (!string.IsNullOrEmpty(id))
            {
                CancelQueueItem(id);
            }
        }

        private void CancelQueueItem(string queueItemId)
        {
            CampaignState state = CampaignSession.Current;
            string name = HomeValleyAnalysis.EntryName(HomeValleyAnalysis.Find(state, queueItemId));
            HomeValleyAnalysis.AnalysisOpResult result = HomeValleyAnalysis.TryCancel(state, queueItemId);
            if (!result.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _resultLabel.text = result.Success ? GameText.Format("analysis.panel.cancelled", name) : HomeValleyAnalysis.ReasonText(result.FailureReason);
            Refresh();
        }

        private void OnWreckClicked()
        {
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TrySendWrecks(CampaignSession.Current, out int sent);
            if (!r.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _resultLabel.text = r.Success ? GameText.Format("analysis.wreck.sent", sent) : HomeValleyAnalysis.ReasonText(r.FailureReason);
            Refresh();
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
