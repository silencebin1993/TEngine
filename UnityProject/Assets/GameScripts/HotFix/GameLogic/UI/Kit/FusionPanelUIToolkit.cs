using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG5-RND-04（FG05 FGR-RND-040～045；FG13 FGU-21 电路合成台与配方书：模拟熔合、正式熔合、线索）：电路合成台面板 / 配方书。
    /// - 熔合：固件 A / B 两个下拉（固件库里的固件：空闲几枚；混合固件、未破解的也列出，选了写明原因——FG05 负向“选了混合固件”）、
    ///   模拟熔合（花技术数据，立刻出结果；无配方 = “无反应”，不消耗固件）、正式熔合（要二次确认，B04；先模拟确认才能熔合）、结果与混合固件预览。
    /// - 队列：每项一行（产物、状态（缺电 / 禁用不只靠颜色）、进度），取消 = 两枚芯片、芯片基板与技术数据全部退回（B03）。
    /// - 配方书：类别筛选、每个类别还剩几个未发现（按配方表实时数，<see cref="FusionService.Remaining"/>）、已发现的配方；
    ///   线索：新旧排序、新线索标“（新）”、等仿真实验室分析的战斗记录数、每条线索“按这条线索选固件”（完整线索两条都选好，部分线索选一条）。
    /// 入口：合成台建筑面板“熔合…”（<see cref="Open"/>）、配方书快捷键（默认 Alt+F，<see cref="ToggleBook"/>）。模态；Esc / 关闭 / 点遮罩关闭。
    /// 刷新：结构只在 <see cref="FusionService.Revision"/> / 固件库 / 物资 / 语言 / 建筑状态变化时重建；O(固件种类 + 队列 + 配方 + 线索)，不按帧分配。
    /// </summary>
    public sealed class FusionPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30067;

        public static FusionPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingId;
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _state;
        private Button _help;
        private Button _close;
        private Label _message;
        private Label _secFuse;
        private DropdownField _parentA;
        private DropdownField _parentB;
        private Button _simulate;
        private Button _formal;
        private Label _result;
        private Label _secQueue;
        private Label _queueEmpty;
        private VisualElement _queueList;
        private Label _secBook;
        private DropdownField _family;
        private Label _remaining;
        private Label _book;
        private Label _secClues;
        private DropdownField _sort;
        private Label _pending;
        private Label _cluesEmpty;
        private VisualElement _clueList;
        private Label _footer;

        private readonly List<string> _parentIds = new List<string>();
        private readonly List<string> _jobIds = new List<string>();
        private readonly List<int> _clueSerials = new List<int>();
        private readonly List<(VisualElement Row, Label Label, Button Button)> _queueRows = new List<(VisualElement, Label, Button)>();
        private readonly List<(VisualElement Row, Label Label, Button Button)> _clueRows = new List<(VisualElement, Label, Button)>();
        private string _selA;
        private string _selB;
        private string _resultText = string.Empty;
        private int _familyIndex;
        private bool _newestFirst = true;
        /// <summary>这次打开前玩家看过的最大线索序号（打开期间大于它的都标“新”）。</summary>
        private int _seenAtOpen;
        private int _key;
        private bool _suppress;
        private VisualTreeAsset _queueRowTemplate;
        private VisualTreeAsset _clueRowTemplate;
        private bool _templatesLoading;

        protected override string UxmlLocation => "FusionPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string ResultText => _result?.text ?? string.Empty;
        public string RemainingText => _remaining?.text ?? string.Empty;
        public string BookText => _book?.text ?? string.Empty;
        public string QueueEmptyText => _queueEmpty != null && !_queueEmpty.ClassListContains("uk-hidden") ? _queueEmpty.text : string.Empty;
        public string CluesEmptyText => _cluesEmpty != null && !_cluesEmpty.ClassListContains("uk-hidden") ? _cluesEmpty.text : string.Empty;
        public string PendingText => _pending != null && !_pending.ClassListContains("uk-hidden") ? _pending.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public IReadOnlyList<string> ParentChoices => _parentA?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<string> FamilyChoices => _family?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public string SelectedA => _selA;
        public string SelectedB => _selB;
        public int QueueRowCount => _jobIds.Count;
        public string QueueRowText(int i) => i >= 0 && i < _jobIds.Count ? _queueRows[i].Label.text : string.Empty;
        public Button QueueCancelButton(int i) => i >= 0 && i < _jobIds.Count ? _queueRows[i].Button : null;
        public int ClueRowCount => _clueSerials.Count;
        public string ClueRowText(int i) => i >= 0 && i < _clueSerials.Count ? _clueRows[i].Label.text : string.Empty;
        public Button ClueUseButton(int i) => i >= 0 && i < _clueSerials.Count ? _clueRows[i].Button : null;
        public DropdownField ParentField(bool slotA) => slotA ? _parentA : _parentB;
        public int ParentIndexOf(string firmwareId) => _parentIds.IndexOf(firmwareId);
        public Button SimulateButton => _simulate;
        public Button FormalButton => _formal;
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public VisualElement RootElement => _root;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnDestroy()
        {
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (_queueRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_queueRowTemplate);
                _queueRowTemplate = null;
            }
            if (_clueRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_clueRowTemplate);
                _clueRowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开某座电路合成台的面板（已开着就切到这座）。<paramref name="buildingId"/> 为空 = 配方书（用最近打开过 / 第一座能用的合成台）。</summary>
        public static void Open(string buildingId)
        {
            if (!string.IsNullOrEmpty(buildingId))
            {
                FusionService.LastSynthId = buildingId;
            }
            BuildingId = string.IsNullOrEmpty(buildingId) ? FusionService.PickSynth(CampaignSession.Current)?.BuildingId : buildingId;
            if (Instance == null || Instance._root == null)
            {
                _pendingId = BuildingId;
                _pendingOpen = true;
                return;
            }
            Instance._key = 0;
            if (IsOpen)
            {
                Instance.Refresh(force: true);
                return;
            }
            Instance.SetOpen(true);
        }

        /// <summary>配方书键（默认 Alt+F）：开着再按一次关闭。</summary>
        public static void ToggleBook()
        {
            if (IsOpen)
            {
                Close();
                return;
            }
            GuidanceHooks.Raise(GuidanceHooks.FusionFirstRecipeBook);
            Open(null);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplates().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open(_pendingId);
            }
        }

        /// <summary>队列行 / 线索行模板（可变数量的行由模板克隆，红线 5；资源成对释放见 <see cref="OnDestroy"/>）。</summary>
        private async UniTaskVoid LoadRowTemplates()
        {
            if (_templatesLoading || (_queueRowTemplate != null && _clueRowTemplate != null))
            {
                return;
            }
            _templatesLoading = true;
            VisualTreeAsset q = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("FusionQueueRow");
            VisualTreeAsset c = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("FusionClueRow");
            _templatesLoading = false;
            if (this == null)
            {
                if (q != null)
                {
                    GameModule.Resource.UnloadAsset(q);
                }
                if (c != null)
                {
                    GameModule.Resource.UnloadAsset(c);
                }
                return;
            }
            _queueRowTemplate = q;
            _clueRowTemplate = c;
            if (q == null || c == null)
            {
                Log.Error("[FusionPanelUIToolkit] 加载 FusionQueueRow / FusionClueRow 失败，队列行 / 线索行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplatesForTests(VisualTreeAsset queueRow, VisualTreeAsset clueRow)
        {
            _queueRowTemplate = queueRow;
            _clueRowTemplate = clueRow;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("FusionRoot");
            _title = root.Q<Label>("FusionTitle");
            _state = root.Q<Label>("FusionState");
            _help = root.Q<Button>("FusionHelp");
            _close = root.Q<Button>("FusionClose");
            _message = root.Q<Label>("FusionMessage");
            _secFuse = root.Q<Label>("FusionSecFuse");
            _parentA = root.Q<DropdownField>("FusionParentA");
            _parentB = root.Q<DropdownField>("FusionParentB");
            _simulate = root.Q<Button>("FusionSimulate");
            _formal = root.Q<Button>("FusionFormal");
            _result = root.Q<Label>("FusionResult");
            _secQueue = root.Q<Label>("FusionSecQueue");
            _queueEmpty = root.Q<Label>("FusionQueueEmpty");
            _queueList = root.Q<VisualElement>("FusionQueueList");
            _secBook = root.Q<Label>("FusionSecBook");
            _family = root.Q<DropdownField>("FusionFamily");
            _remaining = root.Q<Label>("FusionRemaining");
            _book = root.Q<Label>("FusionBook");
            _secClues = root.Q<Label>("FusionSecClues");
            _sort = root.Q<DropdownField>("FusionSort");
            _pending = root.Q<Label>("FusionPending");
            _cluesEmpty = root.Q<Label>("FusionCluesEmpty");
            _clueList = root.Q<VisualElement>("FusionClueList");
            _footer = root.Q<Label>("FusionFooter");

            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _simulate.clicked += ClickSimulate;
            _formal.clicked += ClickFormal;
            _parentA.RegisterValueChangedCallback(_ => OnParentChanged(true));
            _parentB.RegisterValueChangedCallback(_ => OnParentChanged(false));
            _family.RegisterValueChangedCallback(_ =>
            {
                if (!_suppress)
                {
                    _familyIndex = Math.Max(0, _family.index);
                    _key = 0;
                    Refresh(force: true);
                }
            });
            _sort.RegisterValueChangedCallback(_ =>
            {
                if (!_suppress)
                {
                    _newestFirst = _sort.index != 1;
                    _key = 0;
                    Refresh(force: true);
                }
            });
            UiTooltip.Attach(_simulate, () => new TooltipContent { Title = _simulate.text, Body = GameText.Get("fusion.panel.result_none") });
            UiTooltip.Attach(_formal, () => new TooltipContent { Title = _formal.text, Body = CostTip() });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.FusionFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                SetMessage(string.Empty, false);
                _resultText = string.Empty;
                CampaignState s = CampaignSession.Current;
                _seenAtOpen = FusionService.StateOf(s)?.SeenClueSerial ?? 0;
                FusionService.MarkCluesSeen(s); // 记为看过（这次打开期间仍标“新”，下次打开不再标）。
                Refresh(force: true);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh(force: false);
        }

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        /// <summary>选固件 A / B（下拉的第 <paramref name="index"/> 项，与玩家点选同一回调：选中即生效）。</summary>
        public void SelectParent(bool slotA, int index)
        {
            DropdownField f = slotA ? _parentA : _parentB;
            if (index < 0 || index >= f.choices.Count || index >= _parentIds.Count)
            {
                return;
            }
            // 与玩家点选同一效果（选中即生效）：直接记下选择再刷新，不依赖值变化事件在面板外是否派发。
            _suppress = true;
            f.index = index;
            _suppress = false;
            if (slotA)
            {
                _selA = _parentIds[index];
            }
            else
            {
                _selB = _parentIds[index];
            }
            _resultText = string.Empty;
            _key = 0;
            Refresh(force: true);
        }

        /// <summary>按固件 ID 选（线索“按这条线索选固件”与自检用）。下拉里没有这条固件时不变。</summary>
        public bool SelectParentById(bool slotA, string firmwareId)
        {
            int i = _parentIds.IndexOf(firmwareId);
            if (i < 0)
            {
                return false;
            }
            SelectParent(slotA, i);
            return true;
        }

        public void SelectFamily(int index)
        {
            if (index >= 0 && index < _family.choices.Count)
            {
                _suppress = true;
                _family.index = index;
                _suppress = false;
                _familyIndex = index;
                _key = 0;
                Refresh(force: true);
            }
        }

        public void SelectSort(bool newestFirst)
        {
            _suppress = true;
            _sort.index = newestFirst ? 0 : 1;
            _suppress = false;
            _newestFirst = newestFirst;
            _key = 0;
            Refresh(force: true);
        }

        private void OnParentChanged(bool slotA)
        {
            if (_suppress)
            {
                return;
            }
            DropdownField f = slotA ? _parentA : _parentB;
            int i = f.index;
            string id = i >= 0 && i < _parentIds.Count ? _parentIds[i] : null;
            if (slotA)
            {
                _selA = id;
            }
            else
            {
                _selB = id;
            }
            _resultText = string.Empty;
            _key = 0;
            Refresh(force: true);
        }

        public void ClickSimulate()
        {
            FusionOpResult r = FusionService.Simulate(CampaignSession.Current, BuildingId, _selA, _selB);
            if (r.Success)
            {
                _resultText = r.RecipeId != null && FusionCatalog.TryGet(r.RecipeId, out FusionRecipeDef def)
                    ? HitText(def)
                    : GameText.Format("fusion.panel.result_miss", FusionService.Name(_selA), FusionService.Name(_selB));
            }
            Show(r);
        }

        /// <summary>正式熔合：先确认（B04 熔合消耗不可撤回——完成前可以在队列里取消），确认后入队。</summary>
        public void ClickFormal()
        {
            CampaignState s = CampaignSession.Current;
            // 弹确认框之前先查一遍全部前置条件（没确认过的对、芯片 / 材料不够、队列满……）：不能熔合时直接写原因，不弹框（B04 / B06）。
            FusionOpResult pre = FusionService.PreflightFormal(s, BuildingId, _selA, _selB);
            if (!pre.Success || !FusionCatalog.TryGet(pre.RecipeId, out FusionRecipeDef def))
            {
                Show(pre);
                return;
            }
            string a = _selA;
            string b = _selB;
            var req = new ConfirmRequest
            {
                Title = GameText.Get("fusion.confirm.title"),
                Irreversible = false,
                ConfirmText = GameText.Get("fusion.confirm.ok"),
                CancelText = GameText.Get("fwlib.confirm.cancel"),
                OnConfirm = () => Show(FusionService.EnqueueFormal(CampaignSession.Current, BuildingId, a, b)),
            };
            req.Lines.Add(GameText.Format("fusion.confirm.body", FusionService.Name(a), FusionService.Name(b), def.Name));
            req.Lines.Add(GameText.Format("fusion.confirm.body2", FusionCatalog.FormalSubstrate, FusionCatalog.FormalTech, Mathf.RoundToInt(FusionCatalog.FormalSeconds)));
            UiConfirmDialog.Show(req);
        }

        public void ClickCancel(int row)
        {
            if (row >= 0 && row < _jobIds.Count)
            {
                Show(FusionService.Cancel(CampaignSession.Current, _jobIds[row]));
            }
        }

        /// <summary>线索“按这条线索选固件”：完整线索两条都选上，部分线索选上知道的那条（另一条要自己试，写明）。</summary>
        public void ClickUseClue(int row)
        {
            if (row < 0 || row >= _clueSerials.Count)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            FusionClueRecord clue = null;
            foreach (FusionClueRecord c in FusionService.StateOf(s)?.Clues ?? Array.Empty<FusionClueRecord>())
            {
                if (c != null && c.Serial == _clueSerials[row])
                {
                    clue = c;
                    break;
                }
            }
            if (clue == null || !FusionCatalog.TryGet(clue.RecipeId, out FusionRecipeDef r))
            {
                return;
            }
            string a = clue.Full || clue.SessionKind == FusionService.KindSim ? r.ParentA : clue.KnownParent;
            bool okA = SelectParentById(true, a);
            bool okB = true;
            if (clue.Full || clue.SessionKind == FusionService.KindSim)
            {
                okB = SelectParentById(false, r.ParentB);
            }
            if (!okA || !okB)
            {
                SetMessage(GameText.Format("fusion.reason.not_held", FusionService.Name(!okA ? a : r.ParentB)), true);
                return;
            }
            SetMessage(clue.Full || clue.SessionKind == FusionService.KindSim
                ? GameText.Format("fusion.feedback.clue_applied", FusionService.Name(r.ParentA) + " + " + FusionService.Name(r.ParentB))
                : GameText.Get("fusion.reason.clue_partial"), false);
        }

        public void OpenCodex() => MechanicCodex.Open("codex.research.fusion");

        /// <summary>“有配方”结果：名字一行、混合固件预览、正式熔合的成本（多行由代码拼，文本表里不放换行）。</summary>
        private static string HitText(FusionRecipeDef def) =>
            GameText.Format("fusion.panel.result_hit", def.Name) + "\n" + FusionService.PreviewText(def) + "\n" + GameText.Format("fusion.panel.result_cost", FusionService.CostText(def));

        private string CostTip()
        {
            return FusionCatalog.TryGetByPair(_selA, _selB, out FusionRecipeDef r) && FusionService.IsPairKnown(CampaignSession.Current, _selA, _selB)
                ? FusionService.CostText(r)
                : GameText.Format("fusion.reason.simulate_first", FusionService.Name(_selA), FusionService.Name(_selB), FusionCatalog.SimTech);
        }

        private void Show(FusionOpResult r)
        {
            SetMessage(r.Text, !r.Success);
            if (!r.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _key = 0;
            Refresh(force: true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("fu-message-error", error);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            BuildingRecord b = FusionService.FindSynth(s, BuildingId);
            int key = HashCode.Combine(FusionService.Revision, Campaign.Primitive.PrimitiveInventory.Revision, HomeInventory.Revision, (int)GameText.Language,
                GameSettings.Revision, BuildingId, b?.ConstructionState ?? 0, b?.PowerState ?? 0);
            key = HashCode.Combine(key, s != null ? s.GetHashCode() : 0, ProgressKey(s));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _suppress = true;
            try
            {
                Rebuild(s, b);
            }
            finally
            {
                _suppress = false;
            }
        }

        /// <summary>进行中任务的整秒进度（队列行的秒数按整秒刷新，不按帧重建）。</summary>
        private static int ProgressKey(CampaignState s)
        {
            int k = 0;
            foreach (FusionJobRecord j in FusionService.StateOf(s)?.Jobs ?? Array.Empty<FusionJobRecord>())
            {
                if (FusionService.IsActive(j))
                {
                    k = HashCode.Combine(k, (int)j.Progress, j.Reason);
                }
            }
            return k;
        }

        private void Rebuild(CampaignState s, BuildingRecord b)
        {
            bool hasSynth = b != null;
            string name = hasSynth ? BuildingOps.NameOf(b) : GameText.Get("building.circuit_synth.name");
            _title.text = hasSynth ? GameText.Format("fusion.panel.title", name) : GameText.Get("fusion.panel.book_title");
            _close.text = GameText.Get("fusion.panel.close");
            _help.text = GameText.Get("prod.panel.help");
            bool usable = FusionService.IsUsable(s, b, out string why);
            _state.text = usable ? BuildingStatusService.Evaluate(s, b).Reason : GameText.Format("fusion.panel.state_unusable", why);
            _secFuse.text = GameText.Get("fusion.panel.sec.fuse");
            _parentA.label = GameText.Get("fusion.panel.parent_a");
            _parentB.label = GameText.Get("fusion.panel.parent_b");
            _simulate.text = GameText.Format("fusion.panel.simulate", FusionCatalog.SimTech);
            _formal.text = GameText.Get("fusion.panel.formal");
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("fusion.panel.footer"));

            // 固件 A / B 下拉
            _parentIds.Clear();
            var choices = new List<string>();
            foreach (FusionService.Candidate c in FusionService.Candidates(s))
            {
                _parentIds.Add(c.FirmwareId);
                string n = FusionService.Name(c.FirmwareId);
                choices.Add(GameText.Format(c.Mixed ? "fusion.panel.parent_choice_mixed" : c.Raw ? "fusion.panel.parent_choice_raw" : "fusion.panel.parent_choice", n, c.Free));
            }
            DropdownChoices.Apply(_parentA, choices, GameText.Get("fusion.panel.parent_none"));
            DropdownChoices.Apply(_parentB, new List<string>(choices), GameText.Get("fusion.panel.parent_none"));
            if (_selA != null && !_parentIds.Contains(_selA))
            {
                _selA = null;
            }
            if (_selB != null && !_parentIds.Contains(_selB))
            {
                _selB = null;
            }
            if (_selA == null && _parentIds.Count > 0)
            {
                _selA = _parentIds[0];
            }
            if (_selB == null && _parentIds.Count > 1)
            {
                _selB = _parentIds[_selA == _parentIds[0] ? 1 : 0];
            }
            if (choices.Count > 0)
            {
                _parentA.SetValueWithoutNotify(_parentA.choices[Math.Max(0, _parentIds.IndexOf(_selA))]);
                _parentB.SetValueWithoutNotify(_parentB.choices[Math.Max(0, _parentIds.IndexOf(_selB))]);
            }
            bool pair = _selA != null && _selB != null && _selA != _selB;
            _simulate.SetEnabled(usable && pair);
            _formal.SetEnabled(pair && hasSynth && FusionService.CanQueue(b) && FusionService.IsPairKnown(s, _selA, _selB));

            // 结果：刚模拟的结果优先；否则写这一对已知的情况（已发现 / 模拟过）
            string known = string.Empty;
            if (pair)
            {
                if (FusionCatalog.TryGetByPair(_selA, _selB, out FusionRecipeDef pr) && FusionService.IsDiscovered(s, pr.Id))
                {
                    known = GameText.Format("fusion.panel.discovered_pair", pr.Name) + "\n" + FusionService.PreviewText(pr) + "\n" + FusionService.CostText(pr);
                }
                else
                {
                    FusionSimRecord sim = FusionService.SimResultOf(s, _selA, _selB);
                    if (sim != null)
                    {
                        known = string.IsNullOrEmpty(sim.RecipeId) || !FusionCatalog.TryGet(sim.RecipeId, out FusionRecipeDef sr)
                            ? GameText.Get("fusion.panel.known_miss")
                            : GameText.Format("fusion.panel.known_hit", sr.Name) + "\n" + FusionService.PreviewText(sr) + "\n" + FusionService.CostText(sr);
                    }
                }
            }
            _result.text = !string.IsNullOrEmpty(_resultText) ? _resultText : !string.IsNullOrEmpty(known) ? known : GameText.Get("fusion.panel.result_none");

            RebuildQueue(s, b);
            RebuildBook(s);
            RebuildClues(s);
        }

        private void RebuildQueue(CampaignState s, BuildingRecord b)
        {
            List<FusionJobRecord> jobs = b != null ? FusionService.JobsOf(s, b.BuildingId) : new List<FusionJobRecord>();
            _secQueue.text = GameText.Format("fusion.panel.sec.queue", b != null ? FusionService.ActiveCount(s, b.BuildingId) : 0, FusionCatalog.QueueMax);
            while (_queueRowTemplate != null && _queueRows.Count < jobs.Count)
            {
                int rowIndex = _queueRows.Count;
                TemplateContainer host = _queueRowTemplate.CloneTree();
                host.AddToClassList("fu-row-host");
                var row = host.Q<VisualElement>("FqRow");
                var label = host.Q<Label>("FqLabel");
                var btn = host.Q<Button>("FqCancel");
                btn.clicked += () => ClickCancel(rowIndex);
                _queueList.Add(host);
                _queueRows.Add((row, label, btn));
            }
            _jobIds.Clear();
            for (int i = 0; i < _queueRows.Count; i++)
            {
                bool shown = i < jobs.Count;
                (VisualElement row, Label label, Button btn) = _queueRows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                FusionJobRecord j = jobs[i];
                _jobIds.Add(j.JobId);
                FusionCatalog.TryGet(j.RecipeId, out FusionRecipeDef r);
                label.text = GameText.Format("fusion.panel.queue_row", r?.Name ?? j.RecipeId, FusionService.StateText(j),
                    Mathf.FloorToInt(j.Progress).ToString(CultureInfo.InvariantCulture), Mathf.RoundToInt(j.Duration).ToString(CultureInfo.InvariantCulture));
                btn.text = GameText.Get("fusion.panel.cancel");
                btn.SetEnabled(FusionService.IsActive(j));
                btn.EnableInClassList("uk-hidden", !FusionService.IsActive(j));
            }
            bool empty = jobs.Count == 0;
            _queueEmpty.EnableInClassList("uk-hidden", !empty);
            _queueEmpty.text = empty ? GameText.Get("fusion.panel.queue_empty") : string.Empty;
        }

        private string CurrentFamily() => _familyIndex <= 0 || _familyIndex > FusionCatalog.Families.Length ? null : FusionCatalog.Families[_familyIndex - 1];

        private void RebuildBook(CampaignState s)
        {
            int total = FusionCatalog.Recipes.Count;
            List<FusionRecipeDef> all = FusionService.DiscoveredRecipes(s, null);
            _secBook.text = GameText.Format("fusion.panel.sec.book", all.Count, total);
            _family.label = GameText.Get("fusion.panel.filter");
            var fam = new List<string> { FusionCatalog.FamilyName(null) };
            foreach (string f in FusionCatalog.Families)
            {
                fam.Add(FusionCatalog.FamilyName(f));
            }
            DropdownChoices.Apply(_family, fam, FusionCatalog.FamilyName(null));
            _family.SetValueWithoutNotify(_family.choices[Math.Min(_familyIndex, _family.choices.Count - 1)]);
            string family = CurrentFamily();
            var sb = new StringBuilder();
            foreach (string f in FusionCatalog.Families)
            {
                if (family != null && f != family)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(FusionService.RemainingText(s, f));
            }
            _remaining.text = sb.ToString();
            List<FusionRecipeDef> shown = FusionService.DiscoveredRecipes(s, family);
            sb.Clear();
            foreach (FusionRecipeDef r in shown)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(FusionService.RecipeLine(r));
            }
            _book.text = shown.Count > 0 ? sb.ToString() : GameText.Get("fusion.panel.book_empty");
        }

        private void RebuildClues(CampaignState s)
        {
            List<FusionClueRecord> clues = FusionService.Clues(s, CurrentFamily(), _newestFirst);
            _secClues.text = GameText.Format("fusion.panel.sec.clues", clues.Count);
            _sort.label = GameText.Get("fusion.panel.sort");
            DropdownChoices.Apply(_sort, new List<string> { GameText.Get("fusion.panel.sort_new"), GameText.Get("fusion.panel.sort_old") }, GameText.Get("fusion.panel.sort_new"));
            _sort.SetValueWithoutNotify(_sort.choices[_newestFirst ? 0 : 1]);
            int pending = FusionService.StateOf(s)?.Pending.Length ?? 0;
            _pending.EnableInClassList("uk-hidden", pending == 0);
            _pending.text = pending > 0 ? GameText.Format("fusion.panel.pending", pending) : string.Empty;
            while (_clueRowTemplate != null && _clueRows.Count < clues.Count)
            {
                int rowIndex = _clueRows.Count;
                TemplateContainer host = _clueRowTemplate.CloneTree();
                host.AddToClassList("fu-row-host");
                var row = host.Q<VisualElement>("FcRow");
                var label = host.Q<Label>("FcLabel");
                var btn = host.Q<Button>("FcUse");
                btn.clicked += () => ClickUseClue(rowIndex);
                _clueList.Add(host);
                _clueRows.Add((row, label, btn));
            }
            _clueSerials.Clear();
            for (int i = 0; i < _clueRows.Count; i++)
            {
                bool shown = i < clues.Count;
                (VisualElement row, Label label, Button btn) = _clueRows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                FusionClueRecord c = clues[i];
                _clueSerials.Add(c.Serial);
                bool isNew = c.Serial > _seenAtOpen;
                string text = FusionService.ClueText(s, c);
                label.text = isNew ? GameText.Format("fusion.panel.clue_new", text) : text;
                row.EnableInClassList("fu-qrow-new", isNew);
                btn.text = GameText.Get("fusion.panel.use_clue");
                bool discovered = FusionService.IsDiscovered(s, c.RecipeId);
                btn.EnableInClassList("uk-hidden", discovered);
            }
            bool empty = clues.Count == 0;
            _cluesEmpty.EnableInClassList("uk-hidden", !empty);
            _cluesEmpty.text = empty ? GameText.Get("fusion.panel.clues_empty") : string.Empty;
        }
    }
}
