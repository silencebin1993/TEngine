using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.UI.Common;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;
using GameLogic.UI.Kit;

namespace GameLogic.UI.Expedition
{
    /// <summary>ER5-EXP-01 STORY-EXECUTION-CARDS.md 第1/3条："远征准备面板显示区域目标、机器、
    /// 每机 LogicId/编号/装配/伤势/货位/带宽、总火力/维修能力/风险/最低货位，敌方情报与无法出发
    /// 原因……工作机可中断任务先确认，运行中不可中断任务拒绝。"结构落在 UXML/USS
    /// （<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件；全部业务判断（校验/事务）
    /// 委托 <see cref="ExpeditionDepartureService"/> / <see cref="ExpeditionChecklistService"/>，本类不重新实现任何规则。
    ///
    /// FG8-EXP-01（FGR-EXP-001 派遣检查单）Opus 返工：
    /// - 检查单硬约束只有至少一台 / 带宽 / 目的地（+ 机器此刻不在可派位置），其余都是提醒；没有固定人数上限，列表行数随家园机器数增长。
    /// - “编成远征队”入口经 GameEvent（<see cref="ExpeditionTeamService.ComposeRequestedEvent"/>）带入名单：面板已开时立即套用；
    ///   先过滤（不在家园 / 已阵亡 / 在工厂 / 不可中断的机器）再取用，被略过的写在摘要里；面板关闭、换存档时丢弃。
    /// - 全部新文字走文本键（含 UXML 标签与按钮，UXML 不写字面）；出发被拒的提示与字幕用检查单同一份文字，不露原因码。
    /// - <see cref="BindView"/> / <see cref="Tick"/> 供编辑模式自检挂真 UXML 驱动（与名册等面板同一套写法）。
    ///
    /// 面板开关状态由 <see cref="HomeValleyController.IsExpeditionPrepPanelOpen"/> 持有（点击已修复
    /// 的信号塔、名册 / 战略地图的“编成远征队”切换），与 <see cref="Factory.FactoryPanelUIToolkit"/> 同一套写法。</summary>
    public sealed class ExpeditionPrepPanelUIToolkit : MonoBehaviour
    {
        private const int InitialRows = 8;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _rowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _titleLabel;
        private Label _blockedLabel;
        private VisualElement _body;
        private Label _intelLabel;
        private Label _adaptationLabel;
        private Label _reinforcementForecastLabel;
        private VisualElement _exposureRow;
        private Label _exposureLabel;
        // FG1-SIG-01：信号核摘要、“编辑信号核”入口与远征途中不能修改的提醒。
        private Label _signalCoreSummaryLabel;
        private Button _signalCoreEditButton;
        private Label _signalCoreReminderLabel;
        private Toggle _towerBroadcastOffToggle;
        private Label _listTitleLabel;
        private ScrollView _list;
        private Label _summaryLabel;
        private Label _forecastLabel;
        private Label _reasonsLabel;
        // FG8-EXP-01：派遣检查单的提醒、远征队改名与颜色。
        private Label _checklistRemindLabel;
        private TextField _teamNameField;
        private DropdownField _teamColorDropdown;
        private Label _teamNameErrorLabel;
        /// <summary>FG8-EXP-01：队名行的颜色色块（颜色只显示名字不够看，色块让玩家一眼认出所选颜色）。</summary>
        private VisualElement _teamColorSwatch;
        private VisualElement _interruptSection;
        private Label _interruptLabel;
        private Button _confirmInterruptButton;
        private Button _cancelInterruptButton;
        private Button _departButton;
        private Button _closeButton;
        // FG8-EXP-01（卡片结果“指定目的地”）：目的地下拉框只列可派区域；_destinationChoice 为 null＝默认优先级。
        private DropdownField _destinationDropdown;
        private ExpeditionDepartureService.ExpeditionTarget? _destinationChoice;
        private ExpeditionDepartureService.ExpeditionTarget[] _destinationOptions = System.Array.Empty<ExpeditionDepartureService.ExpeditionTarget>();

        /// <summary>FG8-EXP-01 Opus 返工：入口事件到达时面板正从关到开（本帧还没走到打开分支），名单先放这里，打开那一帧取用；
        /// 记下当时的战役，换了存档就丢弃。面板关闭即清空。</summary>
        private int[] _composeInbox;
        private CampaignState _composeInboxSession;
        /// <summary>入口带入名单的说明（带入了几台、哪些被略过及原因），写在摘要里；玩家改勾选或面板关闭后清空。</summary>
        private string _composeNotice = string.Empty;
        private bool _listening;

        private readonly List<TemplateContainer> _rowPool = new List<TemplateContainer>(InitialRows);
        private readonly List<ExpeditionDepartureService.MachineIntel> _liveCache =
            new List<ExpeditionDepartureService.MachineIntel>(InitialRows);

        /// <summary>玩家当前勾选的出征名单，面板打开期间持续保留（刷新不清空），面板关闭/出发成功后清空。</summary>
        private readonly HashSet<int> _selected = new HashSet<int>();
        /// <summary>等待玩家确认中断的名单——非空时显示中断确认区，<see cref="_confirmInterruptButton"/>
        /// 点击后带 <c>interruptConfirmed:true</c> 重新调用 <see cref="ExpeditionDepartureService.TryDepart"/>。</summary>
        private int[] _pendingInterruptLogicIds = System.Array.Empty<int>();
        private bool _wasOpen;
        private float _refreshTimer;
        /// <summary>队名提交失败时保留玩家输入（不被刷新还原），直到改对或面板关闭（原 DEBT-FG8EXP01-09）。</summary>
        private bool _teamNameRejected;
        /// <summary>静态文字（标签、按钮、颜色选项）按语言 / 文本表版本重填（切换语言后不必重开面板，原 DEBT-FG8EXP01-09）。</summary>
        private GameLanguage _textLanguage;
        private int _textRevision = -1;

        /// <summary>当前实例（自检 / 冒烟读；没有面板时为 null）。</summary>
        public static ExpeditionPrepPanelUIToolkit Instance { get; private set; }

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

            if (_document.rootVisualElement == null)
            {
                Log.Error("[ExpeditionPrepPanelUIToolkit] rootVisualElement 等待超时，远征准备面板未初始化。");
                return;
            }
            BindView(_document.rootVisualElement, _rowTemplate);
        }

        /// <summary>绑定视觉树（正式启动由 <see cref="Start"/> 调；编辑模式自检挂真 UXML 后直接调）。</summary>
        public void BindView(VisualElement root, VisualTreeAsset rowTemplate)
        {
            _root = root;
            _rowTemplate = rowTemplate;
            Instance = this;
            _panel = _root.Q<VisualElement>("ExpeditionPrepPanelRoot");
            _titleLabel = _root.Q<Label>("Title");
            _blockedLabel = _root.Q<Label>("BlockedLabel");
            _body = _root.Q<VisualElement>("Body");
            _intelLabel = _root.Q<Label>("IntelLabel");
            _adaptationLabel = _root.Q<Label>("AdaptationLabel");
            _reinforcementForecastLabel = _root.Q<Label>("ReinforcementForecastLabel");
            _exposureRow = _root.Q<VisualElement>("ExposureRow");
            _exposureLabel = _root.Q<Label>("ExposureLabel");
            _towerBroadcastOffToggle = _root.Q<Toggle>("TowerBroadcastOffToggle");
            _listTitleLabel = _root.Q<Label>("ListTitle");
            _list = _root.Q<ScrollView>("MachineList");
            _summaryLabel = _root.Q<Label>("SummaryLabel");
            _forecastLabel = _root.Q<Label>("ForecastLabel");
            _reasonsLabel = _root.Q<Label>("ReasonsLabel");
            _interruptSection = _root.Q<VisualElement>("InterruptSection");
            _interruptLabel = _root.Q<Label>("InterruptLabel");
            _confirmInterruptButton = _root.Q<Button>("ConfirmInterruptButton");
            _cancelInterruptButton = _root.Q<Button>("CancelInterruptButton");
            _departButton = _root.Q<Button>("DepartButton");
            _closeButton = _root.Q<Button>("CloseButton");
            _signalCoreSummaryLabel = _root.Q<Label>("SignalCoreSummaryLabel");
            _signalCoreEditButton = _root.Q<Button>("SignalCoreEditButton");
            _signalCoreReminderLabel = _root.Q<Label>("SignalCoreReminderLabel");
            _signalCoreEditButton.clicked += OpenSignalCoreEditor;

            // FG8-EXP-01：远征队改名（回车或离开输入框提交，校验与机器改名同一套）、设颜色（选中即生效）。
            _checklistRemindLabel = _root.Q<Label>("ChecklistRemindLabel");
            _teamNameField = _root.Q<TextField>("TeamNameField");
            _teamColorDropdown = _root.Q<DropdownField>("TeamColorDropdown");
            _teamNameErrorLabel = _root.Q<Label>("TeamNameErrorLabel");
            _teamColorSwatch = _root.Q<VisualElement>("TeamColorSwatch");
            _teamNameField.maxLength = MachineNaming.InputMaxLength;
            _teamNameField.RegisterCallback<FocusOutEvent>(_ => CommitTeamName());
            _teamNameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    CommitTeamName();
                }
            });
            _teamColorDropdown.RegisterValueChangedCallback(evt =>
            {
                int index = _teamColorDropdown.choices.IndexOf(evt.newValue);
                CampaignState state = CampaignSession.Current;
                if (state != null && index >= 0)
                {
                    ExpeditionTeamService.TrySetColor(state, index);
                    _refreshTimer = 0f;
                }
            });

            // FG8-EXP-01：目的地下拉框（选中即生效；选项由 Refresh 按可派区域刷新）。
            _destinationDropdown = _root.Q<DropdownField>("DestinationDropdown");
            _destinationDropdown.RegisterValueChangedCallback(evt =>
            {
                int index = _destinationDropdown.choices.IndexOf(evt.newValue);
                if (index >= 0 && index < _destinationOptions.Length)
                {
                    _destinationChoice = _destinationOptions[index];
                    _refreshTimer = 0f;
                }
            });

            EnsureRows(InitialRows);

            _departButton.clicked += OnDepartClicked;
            _closeButton.clicked += OnCloseClicked;
            _confirmInterruptButton.clicked += OnConfirmInterruptClicked;
            _cancelInterruptButton.clicked += OnCancelInterruptClicked;
            // ER6-EXPOSE-01：塔关广播开关——玩家可在出发前看到当前暴露值与"关闭广播省暴露但降带宽"
            // 的实时取舍（"玩家可看见出征/接管损失"字面要求）。
            _towerBroadcastOffToggle.RegisterValueChangedCallback(evt =>
                CampaignExposureLedger.SetTowerBroadcastOff(CampaignSession.Current, evt.newValue));

            RefreshStaticTexts(force: true);
            if (!_listening)
            {
                _listening = true;
                GameEvent.AddEventListener<int[]>(ExpeditionTeamService.ComposeRequestedEvent, OnComposeRequested);
            }
        }

        /// <summary>列表行按家园机器数增长（没有固定人数上限）。回调按行下标绑定一次，实际 LogicId 从 _liveCache[下标] 现查。</summary>
        private void EnsureRows(int count)
        {
            if (_rowTemplate == null || _list == null)
            {
                return;
            }
            while (_rowPool.Count < count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _list.Add(row);
                // 不是每次 Refresh 都 RegisterCallback——否则每 0.2 秒堆叠一份新回调，同一次勾选会触发 N 次 Add/Remove。
                int capturedIndex = _rowPool.Count;
                Toggle toggle = row.Q<Toggle>("Select");
                toggle.RegisterValueChangedCallback(evt => OnRowToggleChanged(capturedIndex, evt.newValue));
                _rowPool.Add(row);
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
            Tick(Time.unscaledDeltaTime);
        }

        private static bool HostOpen()
        {
            HomeValleyController hv = GameRoot.HomeValley;
            return hv != null && hv.IsActive && hv.IsExpeditionPrepPanelOpen;
        }

        /// <summary>每帧驱动（<paramref name="realDt"/> 真实秒）。正式由 Update 调；编辑模式自检直接调。</summary>
        public void Tick(float realDt)
        {
            if (_panel == null)
            {
                return;
            }
            bool open = HostOpen();
            SyncEscLayer(open);
            _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (!open)
            {
                if (_wasOpen)
                {
                    // 面板关闭即丢弃未确认的选择/待中断名单/入口名单——重新打开是一次全新的准备流程，
                    // 不带着上次的半成品状态，避免"上次勾了 3 台这次莫名其妙又选中"的困惑。
                    _selected.Clear();
                    _pendingInterruptLogicIds = System.Array.Empty<int>();
                    _destinationChoice = null;
                    _composeNotice = string.Empty;
                    _teamNameRejected = false;
                    _teamNameErrorLabel.text = string.Empty;
                }
                _composeInbox = null;
                _composeInboxSession = null;
                _wasOpen = false;
                return;
            }
            if (!_wasOpen)
            {
                // 从名册 / 战略地图“编成远征队”进来（入口事件在打开前一刻到达）时带入所选机器；否则按常规预选。
                int[] inbox = _composeInbox;
                bool sameSession = ReferenceEquals(_composeInboxSession, CampaignSession.Current);
                _composeInbox = null;
                _composeInboxSession = null;
                if (inbox != null && inbox.Length > 0 && sameSession)
                {
                    ApplyCompose(inbox);
                }
                else
                {
                    PreselectReserves();
                    PreselectTeamMembers();
                }
                // FG8-EXP-01：第一次打开派遣检查单（只埋钩子，引导内容在 FG15-UX-04）。
                GameLogic.Core.GuidanceHooks.Raise(GameLogic.Core.GuidanceHooks.ExpeditionChecklistFirstOpen);
                _refreshTimer = 0f;
            }
            _wasOpen = true;

            _refreshTimer -= realDt;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;

            Refresh();
        }

        /// <summary>入口事件（<see cref="ExpeditionTeamService.ComposeRequestedEvent"/>）。面板没开（宿主没要求打开）就丢弃；
        /// 已经开着时立即套用并马上刷新；正从关到开时留到打开那一帧取用。空名单＝按常规预选（面板已开时不动当前勾选）。</summary>
        private void OnComposeRequested(int[] logicIds)
        {
            if (_panel == null || !HostOpen())
            {
                return;
            }
            // 入口（名册 / 战略地图）在同一帧里先收起自己、再打开本面板：当即在 Esc 栈里占层，不等下一帧 Tick，
            // 否则这一帧里从本面板打开的子页（编辑信号核）会以无父页身份入栈，下一帧本面板入栈时被当成同级页面关掉。
            SyncEscLayer(true);
            int[] ids = logicIds ?? System.Array.Empty<int>();
            if (_wasOpen)
            {
                if (ids.Length > 0)
                {
                    ApplyCompose(ids);
                }
                _refreshTimer = 0f;
                return;
            }
            _composeInbox = ids;
            _composeInboxSession = CampaignSession.Current;
        }

        /// <summary>入口带入的名单：先按快照过滤（只留此刻能派的家园机器），再整单替换当前勾选（不叠加预选），按编号排序；
        /// 被略过的机器与原因写进摘要（B06 / B20）。没有人数上限：带宽超了由检查单硬约束写明。</summary>
        private void ApplyCompose(int[] logicIds)
        {
            CampaignState state = CampaignSession.Current;
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(state, _destinationChoice);
            var requested = logicIds.Distinct().ToArray();
            if (!snap.RegionReachable)
            {
                // 快照不可达（远征队在外 / 目的地都没开放）：整单不带入、当前勾选不动，不逐台按机器原因描述（那样会把能派的机器写成“抢修中”）。
                // 原因与“无法出发”区同一份文字；用字幕 + 拒绝音提示一次，不留在摘要里（条件解除后不会剩一条过期说明）。
                string why = ExpeditionChecklistService.JoinReasons(
                    ExpeditionChecklistService.Build(state, System.Array.Empty<int>(), snap).Hard.Select(h => h.Text));
                _composeNotice = string.Empty;
                LastComposeUnreachableText = GameText.Format("exp.compose.unreachable", requested.Length, why);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, LastComposeUnreachableText);
                return;
            }
            LastComposeUnreachableText = string.Empty;
            var selectable = new HashSet<int>(SelectableIds(snap));
            var kept = requested.Where(selectable.Contains).ToArray();
            var dropped = requested.Where(id => !selectable.Contains(id)).ToArray();
            _selected.Clear();
            foreach (int id in kept)
            {
                _selected.Add(id);
            }
            _reservePreselected = false;
            _teamPreselected = false;
            _pendingInterruptLogicIds = System.Array.Empty<int>();
            _composeNotice = dropped.Length == 0
                ? GameText.Format("exp.compose.applied", kept.Length)
                : GameText.Format("exp.compose.dropped", requested.Length, dropped.Length,
                    string.Join(GameText.Get("rules.sep_dot"), dropped.Select(id => DescribeDropped(snap, id))));
        }

        /// <summary>被略过的机器：名字 + 原因（与检查单硬约束同一套文字）。</summary>
        private static string DescribeDropped(ExpeditionDepartureService.PrepSnapshot snap, int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                return ExpeditionChecklistService.DescribeHard("machine-unknown:" + logicId);
            }
            // “抢修中”只在快照确认这台机器正在做不可中断的工单时才用；其余对不上的情况写“找不到”，不编原因。
            bool busy = (snap.Machines ?? System.Array.Empty<ExpeditionDepartureService.MachineIntel>())
                .Any(m => m.LogicId == logicId && m.BusyUninterruptible);
            string code = !rec.IsAlive ? "machine-dead:"
                : rec.RegionId != HomeValleyLayout.RegionId ? "machine-already-deployed:"
                : rec.IsInFactory ? "machine-in-factory:"
                : busy ? "machine-busy-uninterruptible:"
                : "machine-unknown:";
            return ExpeditionChecklistService.DescribeHard(code + logicId);
        }

        /// <summary>FG8-EXP-01（FGR-EXP-001 负向：目的地还不能去）：区域都没开放 / 远征队在外时面板“无法出发”区的整段文字，
        /// 与检查单硬约束同一份文字（区域没开放时是真实的解锁条件，不叫玩家去探索）。静态，自检 / 冒烟可直接读（不必起面板）。</summary>
        public static string ComposeBlockedText(CampaignState state, ExpeditionDepartureService.PrepSnapshot snap)
        {
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(state, System.Array.Empty<int>(), snap);
            return string.Join("\n", cl.Hard.Select(h => h.Text));
        }

        /// <summary>快照里可以留在名单里的机器编号＝此刻能派的家园机器（伤势只是提醒，不影响）。静态，自检可直接读。</summary>
        public static int[] SelectableIds(ExpeditionDepartureService.PrepSnapshot snap) =>
            (snap.Machines ?? System.Array.Empty<ExpeditionDepartureService.MachineIntel>()).Where(m => m.Eligible).Select(m => m.LogicId).ToArray();

        /// <summary>目的地显示名（文本键）。</summary>
        public static string DestinationName(ExpeditionDepartureService.ExpeditionTarget target) =>
            GameText.Get(target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost ? "exp.dest.foundry" : "exp.dest.ruins");

        /// <summary>目的地下拉框的一项：区域名；迷雾边缘的区域加“（迷雾边缘）”标注（文本键 exp.dest.fog_edge_tag）。</summary>
        public static string DestinationOptionLabel(CampaignState state, ExpeditionDepartureService.ExpeditionTarget target)
        {
            string name = DestinationName(target);
            return ExpeditionDepartureService.ClassifyDestination(state, target) == ExpeditionDepartureService.DestinationFog.FogEdge
                ? GameText.Format("exp.dest.fog_edge_tag", name)
                : name;
        }

        /// <summary>FG8-EXP-01：面板打开且还没有勾选时，预选远征队上次派出的名单（不合格的由 Refresh 清掉；玩家照样可以改）。</summary>
        private void PreselectTeamMembers()
        {
            _teamPreselected = false;
            CampaignState state = CampaignSession.Current;
            if (state == null || _selected.Count > 0)
            {
                return;
            }
            foreach (int id in ExpeditionTeamService.RememberedMembers(state))
            {
                _selected.Add(id);
                _teamPreselected = true;
            }
        }

        /// <summary>FG8-EXP-01：本次打开时是否按远征队名单预选了机器（summary 写明来源用；玩家改勾选后清掉）。</summary>
        private bool _teamPreselected;

        /// <summary>FG8-EXP-01：提交队名。没改（等于当前显示名）不写；等于默认名或空白＝恢复默认名（存空串，换语言跟着变）；
        /// 校验失败保留玩家输入，在输入框下方给出原因（与机器改名同一套文案）。</summary>
        private void CommitTeamName()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || _teamNameField == null)
            {
                return;
            }
            string raw = _teamNameField.value ?? string.Empty;
            if (!_teamNameRejected && raw == ExpeditionTeamService.DisplayName(state))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == GameText.Get("exp.team.default_name"))
            {
                ExpeditionTeamService.ResetName(state);
            }
            else if (!ExpeditionTeamService.TryRename(state, raw, out string message))
            {
                _teamNameErrorLabel.text = message;
                _teamNameRejected = true;
                return;
            }
            _teamNameErrorLabel.text = string.Empty;
            _teamNameRejected = false;
            _teamNameField.SetValueWithoutNotify(ExpeditionTeamService.DisplayName(state));
        }

        /// <summary>FG4-ECO-07：上次刷新时的劳动力提示（自检 / 冒烟读）。</summary>
        public static string LastLaborForecastText { get; private set; } = string.Empty;

        /// <summary>FG7-ENV-02：最近一次刷新时的“跨过静默夜”提示（没有 = 空串；自检 / 冒烟读）。</summary>
        public static string LastSilentCrossText { get; private set; } = string.Empty;

        private bool _reservePreselected;

        /// <summary>FG4-ECO-07（岗位“远征预备”）：面板打开时自动勾选远征预备的机器（不合格的由 Refresh 清掉；没有人数上限，带宽由检查单把关）。玩家照样可以取消勾选。</summary>
        private void PreselectReserves()
        {
            _reservePreselected = false;
            foreach (int id in MachineRoster.ReserveIds(CampaignSession.Current))
            {
                if (_selected.Add(id))
                {
                    _reservePreselected = true;
                }
            }
        }

        /// <summary>刷新目的地下拉框。选项只列可派区域（非 Locked）；只有一个可派时禁用（唯一目标，仍显示）。</summary>
        private void RefreshDestinationChoices(CampaignState state, ExpeditionDepartureService.ExpeditionTarget current)
        {
            ExpeditionDepartureService.ExpeditionTarget[] options = ExpeditionDepartureService.ReachableTargets(state);
            _destinationOptions = options;
            var labels = options.Select(t => DestinationOptionLabel(state, t)).ToList();
            if (!_destinationDropdown.choices.SequenceEqual(labels))
            {
                DropdownChoices.Apply(_destinationDropdown, labels, string.Empty);
            }
            int index = System.Array.IndexOf(options, current);
            if (index >= 0)
            {
                _destinationDropdown.SetValueWithoutNotify(labels[index]);
            }
            _destinationDropdown.SetEnabled(options.Length > 1);
        }

        /// <summary>标签、按钮、颜色选项（文本键）。语言或文本表变化时重填。</summary>
        private void RefreshStaticTexts(bool force = false)
        {
            if (!force && _textLanguage == GameText.Language && _textRevision == GameText.Revision)
            {
                return;
            }
            _textLanguage = GameText.Language;
            _textRevision = GameText.Revision;
            _departButton.text = GameText.Get("exp.panel.depart");
            _closeButton.text = GameText.Get("exp.panel.close");
            _confirmInterruptButton.text = GameText.Get("exp.panel.interrupt_confirm");
            _cancelInterruptButton.text = GameText.Get("exp.panel.interrupt_cancel");
            _towerBroadcastOffToggle.label = GameText.Get("exp.panel.broadcast_off");
            _destinationDropdown.label = GameText.Get("exp.dest.label");
            _teamNameField.label = GameText.Get("exp.team.label");
            _teamColorDropdown.label = GameText.Get("exp.team.color_label");
            _listTitleLabel.text = GameText.Format("exp.list.title", ExpeditionDepartureService.MinDepartureSize);
            var colorNames = new List<string>();
            for (int c = 0; c < ExpeditionTeamService.ColorCount; c++)
            {
                colorNames.Add(ExpeditionTeamService.ColorName(c));
            }
            DropdownChoices.Apply(_teamColorDropdown, colorNames, string.Empty);
        }

        private void Refresh()
        {
            CampaignState state = CampaignSession.Current;
            RefreshStaticTexts();
            ExpeditionDepartureService.PrepSnapshot snapshot = ExpeditionDepartureService.BuildPrepSnapshot(state, _destinationChoice);

            _titleLabel.text = snapshot.RegionReachable
                ? GameText.Format("exp.panel.title", DestinationName(snapshot.Target))
                : GameText.Get("exp.panel.title_none");
            _blockedLabel.style.display = snapshot.RegionReachable ? DisplayStyle.None : DisplayStyle.Flex;
            _body.style.display = snapshot.RegionReachable ? DisplayStyle.Flex : DisplayStyle.None;
            // FG6-E2E-01（FG-GAP-112）：页脚在 Body 外，“关闭”始终可点；“确认出发”随 Body 一起隐藏（没有目标 / 远征队在外时不能出发）。
            _departButton.style.display = snapshot.RegionReachable ? DisplayStyle.Flex : DisplayStyle.None;
            // ER6-EXPOSE-01：暴露值 + 带宽实时展示，让玩家在出发前就能看到“关闭广播”的真实取舍。
            // FG6-E2E-01（FG-GAP-112）：这一行在 Body 外——还没有出征目标、远征队在外时也能看暴露、开关广播（突袭节奏的唯一手动杠杆）。
            _exposureRow.style.display = state != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (state != null)
            {
                _exposureLabel.text = $"信号暴露 {state.SignalExposure:F0}/100｜带宽 {state.SignalBandwidth:F0}" +
                    (state.SignalTowerBroadcastOff ? "（广播已关闭，−3带宽）" : string.Empty);
                _towerBroadcastOffToggle.SetValueWithoutNotify(state.SignalTowerBroadcastOff);
            }
            if (!snapshot.RegionReachable)
            {
                _blockedLabel.text = ComposeBlockedText(state, snapshot);
                return;
            }

            RefreshDestinationChoices(state, snapshot.Target);

            // ER6-REGION-01：目标名/文案不再硬编码破碎都市——第二次出征以后目标固定切到铸造前哨外围。
            string targetName = DestinationName(snapshot.Target);
            string objectiveLine = string.IsNullOrEmpty(snapshot.ObjectivePreviewText)
                ? string.Empty
                : "\n目标：" + snapshot.ObjectivePreviewText;
            // ER6-LOOP-01 起在情报文案末尾附目标进度；ER8 收尾（DEBT-ER6LOOP01-01）常驻目标条/任务日志
            // 落地后，这一行与它们同源（当前目标标题 + 完成数），不再显示原始阶段枚举名与 OBJ 编号。
            _intelLabel.text = $"目标：{targetName}（第 {snapshot.ExpeditionCount + 1} 次出击）｜警戒 {snapshot.EnemyAlertLevel:F0}/100" +
                objectiveLine + $"\n{snapshot.EnemyIntelText}\n{DescribeObjectiveProgress(state)}";

            // ER6-ADAPT-01：反制情报。None 时四段合一显示（“无反制 / 安全默认”）；有反制时这里只写反制名与来源，
            // 危险与应对写在检查单的“敌方反制”提醒里（FG8-EXP-01 Opus 返工：同一面板不再重复两遍）。
            bool hasCounter = snapshot.AdaptationId != null && snapshot.AdaptationId != AdaptationCatalog.None;
            string counterHead = GameText.Format("exp.panel.counter.name", snapshot.AdaptationDisplayName) + "\n"
                                 + GameText.Format("exp.panel.counter.source", snapshot.AdaptationSourceText);
            _adaptationLabel.text = hasCounter
                ? counterHead
                : counterHead + "\n" + GameText.Format("exp.panel.counter.hazard", snapshot.AdaptationHazardText)
                  + "\n" + GameText.Format("exp.panel.counter.hint", snapshot.AdaptationCounterHintText);
            _reinforcementForecastLabel.text = snapshot.CoreReinforcementForecast ? snapshot.CoreReinforcementForecastText : string.Empty;
            _reinforcementForecastLabel.RemoveFromClassList("exp-reinforcement-visible");
            if (snapshot.CoreReinforcementForecast)
            {
                _reinforcementForecastLabel.AddToClassList("exp-reinforcement-visible");
            }

            // FG1-SIG-01：出发前看得到带的是哪些固件，能直接去改，并被提醒“出发后远征途中不能改”。
            _signalCoreSummaryLabel.text = GameLogic.Campaign.Signal.SignalCoreService.SummaryText(state);
            _signalCoreEditButton.text = GameText.Get("signal.core.edit");
            _signalCoreReminderLabel.text = GameText.Get("signal.core.expedition_reminder");

            // FG8-EXP-01：远征队名与颜色（输入框正在编辑、或刚输入了非法名字时不覆盖，免得打到一半被刷掉）。
            if (state != null)
            {
                bool editingName = _teamNameField.panel?.focusController?.focusedElement == _teamNameField;
                if (!editingName && !_teamNameRejected)
                {
                    _teamNameField.SetValueWithoutNotify(ExpeditionTeamService.DisplayName(state));
                }
                int colorIndex = ExpeditionTeamService.ColorIndexOf(state);
                _teamColorDropdown.SetValueWithoutNotify(ExpeditionTeamService.ColorName(colorIndex));
                if (_teamColorSwatch != null)
                {
                    _teamColorSwatch.style.backgroundColor = new StyleColor((Color)ExpeditionTeamService.ColorOf(colorIndex));
                }
            }

            // 勾选集里已经不再存在/不再能派的 LogicId 清掉（机器阵亡/被移出家园等）。伤势只是提醒，不清。
            var validIds = new HashSet<int>(SelectableIds(snapshot));
            _selected.RemoveWhere(id => !validIds.Contains(id));

            _liveCache.Clear();
            _liveCache.AddRange(snapshot.Machines);
            EnsureRows(_liveCache.Count);
            for (int i = 0; i < _rowPool.Count; i++)
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

                row.Q<Label>("Number").text = MachineNaming.Short(m.LogicId); // FG4-ECO-07：名字同源
                // ER8-CONTENT-01：m.ChassisId 是机型编号（erc_001），此前直接查底盘表必然查不到、回退成把 erc_001
                // 原样显示给玩家——先归到底盘类别再取展示名；系统占位机（救援机等）显示“机器”。行首图标＝底盘。
                string chassisLabel = MechanicalContentFacade.ResolveChassisLabel(m.ChassisId);
                row.Q<Label>("Chassis").text = string.IsNullOrEmpty(chassisLabel) || chassisLabel == m.ChassisId ? "机器" : chassisLabel;
                ContentIcons.Apply(row.Q<VisualElement>("Icon"), m.ChassisId);
                row.Q<Label>("Health").text = $"HP{m.Health:F0}/{m.MaxHealth:F0}";
                row.Q<Label>("Cargo").text = $"货{m.CargoSlots}";
                row.Q<Label>("Bandwidth").text = $"带宽{m.BandwidthCost:F0}";
                row.Q<Label>("Weapon").text = m.HasWeapon ? "武" : "—";
                // FG4-ECO-07：状态后附岗位（远征预备的机器一眼看出来）。
                bool hasRec = MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord roleRec) && roleRec != null;
                string status = DescribeStatus(m);
                if (hasRec && m.Eligible && ExpeditionDepartureService.IsSeverelyDamaged(roleRec))
                {
                    // FG8-EXP-01：重伤只是提醒（可以派），状态栏标出，检查单写明后果。
                    status += GameText.Get("rules.sep_dot") + GameText.Get("exp.status.too_damaged");
                }
                row.Q<Label>("Status").text = status + (hasRec
                    ? GameText.Get("rules.sep_dot") + MachineRoster.RoleName(MachineRoster.EffectiveRole(state, roleRec)) : string.Empty);

                Toggle toggle = row.Q<Toggle>("Select");
                toggle.SetEnabled(m.Eligible);
                toggle.SetValueWithoutNotify(m.Eligible && _selected.Contains(m.LogicId));
            }

            int[] selectedArray = _selected.OrderBy(id => id).ToArray();
            ExpeditionDepartureService.RosterValidation validation =
                ExpeditionDepartureService.ValidateRoster(state, selectedArray);
            string labor = MachineRoster.LaborForecastText(state, selectedArray, out bool laborDrops);
            LastLaborForecastText = labor;

            _summaryLabel.text = GameText.Format("exp.summary.line", selectedArray.Length, ExpeditionDepartureService.MinDepartureSize,
                    validation.TotalCargoSlots, snapshot.MinRecommendedCargoSlots,
                    validation.TotalBandwidth.ToString("F0"), validation.BandwidthCapacity.ToString("F0"),
                    GameText.Get(validation.HasWeapon ? "exp.summary.weapon_yes" : "exp.summary.weapon_no"))
                // FG4-ECO-07（FGR-ECO-042）：劳动力不变时写在这里；会下降时写进检查单的“劳动力”提醒（带后果），不在两处重复。
                + (laborDrops ? string.Empty : "\n" + labor)
                + (_reservePreselected ? "\n" + GameText.Get("roster.expedition.reserve_hint") : string.Empty)
                // FG8-EXP-01（FGR-BASE-020）：远征队名单自动预选 / 入口带入时写明来源，玩家能看出勾选从哪来。
                + (_teamPreselected ? "\n" + GameText.Get("exp.team.preselect_hint") : string.Empty)
                + (string.IsNullOrEmpty(_composeNotice) ? string.Empty : "\n" + _composeNotice);
            // DEBT-ER5EXP01-01：总火力/维修能力/风险此前只有“武器 有/无”，现在按真实装配与目标区域敌情计算。
            if (_forecastLabel != null)
            {
                _forecastLabel.text = ExpeditionForecast.Describe(ExpeditionForecast.Compute(state, selectedArray, snapshot.Target));
            }
            // FG7-ENV-02（FGR-ENV-013）：“跨过静默夜”现在只写在检查单提醒里（FG8-EXP-01 Opus 返工：不再在预测行重复）。
            LastSilentCrossText = GameLogic.Campaign.Env.SilentNightService.ExpeditionCrossText(state) ?? string.Empty;

            // FG8-EXP-01：派遣检查单。硬约束与出发事务同一份结果（Blocked 即不能派，出发按钮置灰）；提醒可以无视，每条后面写着后果。
            ExpeditionChecklistService.Checklist checklist = ExpeditionChecklistService.Build(state, selectedArray, snapshot, validation, labor, laborDrops);
            bool showReasons = checklist.Blocked && _pendingInterruptLogicIds.Length == 0;
            _reasonsLabel.text = showReasons ? GameText.Format("exp.panel.blocked", ExpeditionChecklistService.JoinReasons(checklist.Hard.Select(h => h.Text))) : string.Empty;
            _checklistRemindLabel.text = checklist.Reminders.Length == 0 ? string.Empty
                : GameText.Get("exp.check.remind.header") + "\n· " + string.Join("\n· ", checklist.Reminders.Select(r => r.Text));
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
                    .Select(id => MachineNaming.Short(id)));
                _interruptLabel.text = $"{_pendingInterruptLogicIds.Length} 台机器正在工作（{names}），出征将中断当前任务并按规则交还/保留货物。确认出发？";
            }

            _departButton.SetEnabled(!interruptVisible && !checklist.Blocked);
        }

        /// <summary>当前目标标题 + 已完成数，与常驻目标条（<see cref="CampaignObjectiveCatalog"/>）同源。</summary>
        private static string DescribeObjectiveProgress(CampaignState state)
        {
            if (state == null)
            {
                return string.Empty;
            }
            int total = CampaignObjectiveCatalog.All.Length;
            int completed = CampaignObjectiveCatalog.All.Count(d => CampaignObjectiveTracker.IsCompleted(state, d.Id));
            string currentId = CampaignObjectiveTracker.CurrentObjectiveId(state);
            string current = currentId != null
                ? "当前目标：" + CampaignObjectiveCatalog.TitleOf(currentId)
                : completed == total ? "全部目标完成" : "当前目标：无";
            return $"{current}｜已完成 {completed}/{total}（按 {Core.InputDisplay.ForAction(Core.GameActionId.ToggleMissionLog)} 看任务日志）";
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
            // 玩家自己改了勾选：预选 / 入口带入的来源说明不再成立（原 DEBT-FG8EXP01-09）。
            _reservePreselected = false;
            _teamPreselected = false;
            _composeNotice = string.Empty;
            _refreshTimer = 0f;
        }

        private void OnDepartClicked()
        {
            int[] selected = _selected.OrderBy(id => id).ToArray();
            ExpeditionDepartureService.DepartureResult result =
                ExpeditionDepartureService.TryDepart(selected, interruptConfirmed: false, requested: _destinationChoice);
            HandleDepartureResult(result);
        }

        private void OnConfirmInterruptClicked()
        {
            int[] selected = _selected.OrderBy(id => id).ToArray();
            ExpeditionDepartureService.DepartureResult result =
                ExpeditionDepartureService.TryDepart(selected, interruptConfirmed: true, requested: _destinationChoice);
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
                    _composeNotice = string.Empty;
                    // FG8-EXP-02：派遣 = 出发行军——家园照常运行、镜头不动（不抢镜头），远征队沿地形走向目的地：关闭本面板，
                    // 状态、预计到达时间、远程命令与“跟随远征队”在世界栏的远征行军条上。
                    GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
                    break;
                case ExpeditionDepartureService.DepartureOutcome.NeedsInterruptConfirmation:
                    _pendingInterruptLogicIds = result.InterruptibleLogicIds;
                    break;
                case ExpeditionDepartureService.DepartureOutcome.Blocked:
                case ExpeditionDepartureService.DepartureOutcome.RolledBack:
                    _pendingInterruptLogicIds = System.Array.Empty<int>();
                    // 与检查单硬约束同一套文字（不露原因码）；面板内原因之外，再给声音与字幕条（ER8-CONTENT-01 AC-AUD-001）。
                    _reasonsLabel.text = GameText.Format("exp.panel.blocked",
                        ExpeditionChecklistService.JoinReasons(result.Reasons.Select(ExpeditionChecklistService.DescribeHard)));
                    _reasonsLabel.AddToClassList("exp-reasons-visible");
                    Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, _reasonsLabel.text);
                    break;
            }
            _refreshTimer = 0f;
        }

        private void OnCloseClicked()
        {
            GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
        }

        /// <summary>本面板的开关状态由家园宿主持有：按宿主状态在 Esc 栈里压层 / 移除（缓存委托，不每帧分配）。</summary>
        private void SyncEscLayer(bool open) => UiEscapeStack.Sync(this, open, _escClose ??= OnCloseClicked);

        /// <summary>“编辑信号核”：复用 FG1-SIG-07 的信号核面板（经 SignalCoreService.RequestPanelOpen 打开），作为本面板的子页，Esc 逐层返回。
        /// FG8-EXP-01 返工第 2 轮：打开子页前先确保本面板已在 Esc 栈里——宿主刚在同一帧打开本面板、还没等到 Tick 同步时，
        /// 子页找不到父页，下一帧本面板入栈会把它当同级页面关掉（冒烟 2026-10-17 实测：编成远征队后点“编辑信号核”，信号核一闪即关）。</summary>
        private void OpenSignalCoreEditor()
        {
            if (!HostOpen())
            {
                return;
            }
            SyncEscLayer(true);
            UiEscapeStack.OpenChild(this, GameLogic.Campaign.Signal.SignalCoreService.RequestPanelOpen);
        }

        // ── 自检 / 冒烟读点 ─────────────────────────────────────────────────────────

        public int[] SelectedIdsForTests => _selected.OrderBy(id => id).ToArray();
        public string ComposeNoticeForTests => _composeNotice;

        /// <summary>最近一次入口名单因“现在根本不能派遣”（远征队在外 / 目的地都没开放）整单没带入时给玩家的字幕文字；带入成功后清空。</summary>
        public string LastComposeUnreachableText { get; private set; } = string.Empty;
        public string SummaryText => _summaryLabel?.text ?? string.Empty;
        public string ReasonsText => _reasonsLabel?.text ?? string.Empty;
        public string RemindText => _checklistRemindLabel?.text ?? string.Empty;
        public string TitleText => _titleLabel?.text ?? string.Empty;
        public string TeamNameErrorText => _teamNameErrorLabel?.text ?? string.Empty;
        public bool PanelVisible => _panel != null && _panel.style.display == DisplayStyle.Flex;
        public DropdownField DestinationField => _destinationDropdown;
        public TextField TeamNameField => _teamNameField;
        public Button DepartButton => _departButton;
        public Toggle RowToggleOf(int logicId)
        {
            int i = _liveCache.FindIndex(m => m.LogicId == logicId);
            return i >= 0 && i < _rowPool.Count ? _rowPool[i].Q<Toggle>("Select") : null;
        }

        /// <summary>立即刷新一次（自检用；正式每 0.2 真实秒一次）。</summary>
        public void RefreshNowForTests()
        {
            _refreshTimer = 0f;
            Tick(0f);
        }

        private void OnDestroy()
        {
            if (_listening)
            {
                _listening = false;
                GameEvent.RemoveEventListener<int[]>(ExpeditionTeamService.ComposeRequestedEvent, OnComposeRequested);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_rowTemplate != null && _document != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
            }
            _rowTemplate = null;
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
