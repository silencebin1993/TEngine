using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.RegionCommand
{
    /// <summary>ER5-CMD-01："战略点选/框选/编组显示清楚选择集；1～9 调组、Ctrl+1～9 保存组，
    /// Move/Attack/Guard/Retreat 都由正式 UI/鼠标下令且显示目标、路径、执行/失败状态"的正式 UI
    /// 落点。结构落在 UXML/USS（<c>unity-ui-toolkit.md</c> 硬规则），C# 只做数据绑定与事件——
    /// 全部命令/选择/编组逻辑都在 <see cref="RegionSquadCommandSystem"/>，本类不重新实现任何规则。
    ///
    /// 归还谷地、破碎都市、铸造前哨外围共用同一个实例（镜头同一时刻只看一个地点，看着哪个就显示哪个的
    /// <see cref="RegionSquadCommandSystem"/>），同 <c>StrategyClockHudToolkit</c> 的常驻单例写法。
    /// FG1-SIG-03：机器候选条 = 卡片“可以从机器列表直接接入”的机器列表，三个地点都有（铸造前哨原先漏接，FG-GAP-029 关闭）。</summary>
    public sealed class RegionCommandBarUIToolkit : MonoBehaviour
    {
        private const int MaxEventLines = 6;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private VisualElement _panel;
        private Label _selectionLabel;
        private Label _statusLabel;
        private readonly Button[] _groupButtons = new Button[9];
        private Button _moveButton;
        private Button _attackButton;
        private Button _guardButton;
        private Button _retreatButton;
        private Button _stopButton;
        private ScrollView _eventLog;
        private readonly List<Label> _eventLabels = new List<Label>(MaxEventLines);

        // ── ER5-CTL-01：任意接管 HUD（编号/蓝图 + Tab 候选条）───────────────
        private Label _controlledUnitLabel;
        private Label _controlFeedbackLabel;
        private ScrollView _candidateStrip;
        private readonly Dictionary<int, Button> _candidateButtons = new Dictionary<int, Button>(8);
        private float _controlFeedbackRemaining;

        // FG1-SIG-03：机器列表按钮说明的缓存键（按键绑定对象 + 版本 + 语言）：重绑接入 / 切换键或切换语言后改写已有按钮。
        private InputBindingSet _tipBindings;
        private int _tipBindingsRevision = -1;
        private GameLanguage _tipLanguage;
        private string _candidateTip;

        // ── ER5-INT-01：E 交互提示 + 进度条 + 字幕 ───────────────────────────
        private Label _interactPromptLabel;
        private VisualElement _interactProgressTrack;
        private VisualElement _interactProgressFill;
        private Label _interactSubtitleLabel;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("RegionCommandBar");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：ER5-CMD-01 新增，取 HUD(0) 与运行中枢(2) 之间的 1——
            // 常驻底部条，且实读现况 6/8 两档已各被两套互斥场景面板复用，1 是真正的空档。
            _document.sortingOrder = 1;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[RegionCommandBarUIToolkit] rootVisualElement 等待超时，战略命令条未初始化。");
                return;
            }

            BindElements();
        }

        /// <summary>自检入口：把已经载入的视觉树交给本类绑定（正式流程在 <see cref="Start"/> 里经资源系统载入后调同一个绑定）。</summary>
        public void BindView(VisualElement root)
        {
            _root = root;
            BindElements();
        }

        private void BindElements()
        {
            _panel = _root.Q<VisualElement>("RegionCommandBarRoot");
            _selectionLabel = _root.Q<Label>("SelectionLabel");
            _statusLabel = _root.Q<Label>("StatusLabel");
            _moveButton = _root.Q<Button>("MoveButton");
            _attackButton = _root.Q<Button>("AttackButton");
            _guardButton = _root.Q<Button>("GuardButton");
            _retreatButton = _root.Q<Button>("RetreatButton");
            _stopButton = _root.Q<Button>("StopButton");
            _eventLog = _root.Q<ScrollView>("EventLog");
            _controlledUnitLabel = _root.Q<Label>("ControlledUnitLabel");
            _controlFeedbackLabel = _root.Q<Label>("ControlFeedbackLabel");
            _candidateStrip = _root.Q<ScrollView>("ControlCandidateStrip");
            _interactPromptLabel = _root.Q<Label>("InteractPromptLabel");
            _interactProgressTrack = _root.Q<VisualElement>("InteractProgressTrack");
            _interactProgressFill = _root.Q<VisualElement>("InteractProgressFill");
            _interactSubtitleLabel = _root.Q<Label>("InteractSubtitleLabel");

            for (int i = 0; i < 9; i++)
            {
                int slot = i + 1;
                Button btn = _root.Q<Button>("Group" + slot);
                _groupButtons[i] = btn;
                // Ctrl+点击＝保存组，普通点击＝调组；ClickEvent 带修饰键状态，Button.clicked 不带。
                btn.RegisterCallback<ClickEvent>(evt => OnGroupButtonClicked(slot, evt.ctrlKey));
            }

            _moveButton.clicked += () => ActiveSquadCommands()?.ArmCommand(RegionCommandKind.Move);
            _attackButton.clicked += () => ActiveSquadCommands()?.ArmCommand(RegionCommandKind.Attack);
            _guardButton.clicked += () => ActiveSquadCommands()?.IssueGuardHere(ActivePaused());
            _retreatButton.clicked += () => ActiveSquadCommands()?.IssueRetreat(ActivePaused());
            _stopButton.clicked += () => ActiveSquadCommands()?.Stop();

            for (int i = 0; i < MaxEventLines; i++)
            {
                var label = new Label { text = string.Empty };
                label.AddToClassList("cmd-event-line");
                _eventLog.Add(label);
                _eventLabels.Add(label);
            }
        }

        private void OnGroupButtonClicked(int slot, bool ctrl)
        {
            RegionSquadCommandSystem cmd = ActiveSquadCommands();
            if (cmd == null)
            {
                return;
            }
            if (ctrl)
            {
                cmd.AssignGroup(slot);
            }
            else
            {
                cmd.RecallGroup(slot);
            }
        }

        /// <summary>镜头看着哪个地点，就读它的 <see cref="RegionSquadCommandSystem"/>——IsActive = 已载入且被观察，
        /// 同一时刻只有一个地点被观察（归还谷地、破碎都市、铸造前哨外围）。</summary>
        private static RegionSquadCommandSystem ActiveSquadCommands()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return GameRoot.HomeValley.SquadCommands;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                return GameRoot.FracturedCity.SquadCommands;
            }
            if (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive)
            {
                return GameRoot.FoundryOutpost.SquadCommands;
            }
            return null;
        }

        private static bool ActivePaused()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return GameRoot.HomeValley.IsPaused;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                return GameRoot.FracturedCity.IsPaused;
            }
            if (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive)
            {
                return GameRoot.FoundryOutpost.IsPaused;
            }
            return false;
        }

        // ── ER5-CTL-01：当前哪个区域在跑，就读它的 RegionControlSystem/接管状态 ──────

        private static RegionControlSystem ActiveControl()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return GameRoot.HomeValley.Control;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                return GameRoot.FracturedCity.Control;
            }
            if (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive)
            {
                return GameRoot.FoundryOutpost.Control;
            }
            return null;
        }

        /// <summary>ER5-INT-01：当前哪个区域在跑，就读它的 RegionInteractionSystem——与
        /// <see cref="ActiveControl"/> 同一模式。</summary>
        private static RegionInteractionSystem ActiveInteraction()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return GameRoot.HomeValley.Interact;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                return GameRoot.FracturedCity.Interact;
            }
            if (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive)
            {
                return GameRoot.FoundryOutpost.Interact;
            }
            return null;
        }

        private static int? ActivePossessedLogicId()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return GameRoot.HomeValley.PossessedMachineLogicId;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                return GameRoot.FracturedCity.PossessedMachineLogicId;
            }
            if (GameRoot.FoundryOutpost != null && GameRoot.FoundryOutpost.IsActive)
            {
                return GameRoot.FoundryOutpost.PossessedMachineLogicId;
            }
            return null;
        }

        /// <summary>FG1-SIG-03（FG01 第 4 章“从机器列表可以直接接入”）：点一下 = 发起接入（与选中后按接入键同一个入口：逐条校验、0.35 秒过渡、
        /// 过渡结束才插入固件）。战略视角下镜头随之过渡到直控；战略暂停中点击 = 目标已确认，恢复运行后完成。拒绝时这里与 HUD 状态行都写原因。</summary>
        private void OnCandidateButtonClicked(int logicId)
        {
            UplinkRequestResult result = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (result.Accepted)
            {
                _controlFeedbackLabel.text = string.Empty;
            }
            else
            {
                _controlFeedbackLabel.text = result.Text;
                _controlFeedbackRemaining = 3f;
            }
        }

        private void Update() => Refresh();

        /// <summary>每帧刷新（自检直接调用，与 <see cref="Update"/> 同一段）。</summary>
        public void Refresh()
        {
            if (_panel == null)
            {
                return;
            }

            RegionSquadCommandSystem cmd = ActiveSquadCommands();
            _panel.style.display = cmd != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (cmd == null)
            {
                return;
            }

            _selectionLabel.text = cmd.Selection.Count == 0 ? "未选中机器" : $"已选 {cmd.Selection.Count} 台";

            string status = cmd.ArmedKind.HasValue
                ? (cmd.ArmedKind.Value == RegionCommandKind.Move ? "移动待命：点击地图目标位置" : "攻击待命：点击一个敌方目标")
                : ActivePaused() && cmd.QueuedCommandCount > 0
                    ? $"战略暂停中，已排队 {cmd.QueuedCommandCount} 条命令"
                    : string.Empty;
            _statusLabel.text = status;

            for (int i = 0; i < 9; i++)
            {
                Button btn = _groupButtons[i];
                int size = cmd.GroupSize(i + 1);
                btn.text = size > 0 ? $"{i + 1}({size})" : (i + 1).ToString();
                btn.RemoveFromClassList("cmd-group-btn-filled");
                if (size > 0)
                {
                    btn.AddToClassList("cmd-group-btn-filled");
                }
            }

            _moveButton.RemoveFromClassList("cmd-btn-armed");
            _attackButton.RemoveFromClassList("cmd-btn-armed");
            if (cmd.ArmedKind == RegionCommandKind.Move)
            {
                _moveButton.AddToClassList("cmd-btn-armed");
            }
            else if (cmd.ArmedKind == RegionCommandKind.Attack)
            {
                _attackButton.AddToClassList("cmd-btn-armed");
            }

            bool hasSelection = cmd.Selection.Count > 0;
            _moveButton.SetEnabled(hasSelection);
            _attackButton.SetEnabled(hasSelection);
            _guardButton.SetEnabled(hasSelection);
            _retreatButton.SetEnabled(hasSelection);
            _stopButton.SetEnabled(hasSelection);

            IReadOnlyList<string> events = cmd.RecentEvents;
            int total = events.Count;
            for (int i = 0; i < MaxEventLines; i++)
            {
                int sourceIndex = total - MaxEventLines + i;
                _eventLabels[i].text = sourceIndex >= 0 ? events[sourceIndex] : string.Empty;
            }

            RefreshControlHud();
            RefreshInteractHud();
        }

        /// <summary>ER5-CTL-01：受控机 编号/蓝图 显示 + 失联宽限（Suspended）提示 + Tab 候选条。</summary>
        private void RefreshControlHud()
        {
            RegionControlSystem control = ActiveControl();
            if (control == null || _controlledUnitLabel == null)
            {
                return;
            }

            int? possessedId = ActivePossessedLogicId();
            if (possessedId.HasValue && MachineRegistry.TryGetRecord(possessedId.Value, out MachineRecord rec))
            {
                string prefix = control.Availability == RegionControlAvailability.Suspended ? "重连中…" : "受控";
                _controlledUnitLabel.text = $"{prefix}：#{rec.DisplayNumber} {rec.BlueprintId}";
            }
            else
            {
                _controlledUnitLabel.text = "战略视角";
            }

            if (_controlFeedbackRemaining > 0f)
            {
                _controlFeedbackRemaining -= Time.unscaledDeltaTime;
                if (_controlFeedbackRemaining <= 0f)
                {
                    _controlFeedbackLabel.text = string.Empty;
                }
            }

            RefreshCandidateStrip(control, possessedId);
        }

        /// <summary>FG1-SIG-03：机器列表按钮说明（“点一下直接接入；选中后按 V 也可以；接入后按 Tab 切换”）。按键绑定或语言变了才重建文本，
        /// 返回 true = 变了（已有按钮要改写）。每帧 O(1)：只比较引用、版本号与语言。</summary>
        private bool RefreshCandidateTooltip()
        {
            InputBindingSet bindings = GameSettings.KeyBindings;
            int revision = bindings != null ? bindings.Revision : -1;
            GameLanguage language = GameText.Language;
            if (_candidateTip != null && ReferenceEquals(bindings, _tipBindings) && revision == _tipBindingsRevision && language == _tipLanguage)
            {
                return false;
            }
            _tipBindings = bindings;
            _tipBindingsRevision = revision;
            _tipLanguage = language;
            _candidateTip = GameText.Format("signal.uplink.list_tip",
                InputDisplay.ForAction(GameActionId.ToggleCameraView),
                InputDisplay.ForAction(GameActionId.CycleControlTarget));
            return true;
        }

        /// <summary>候选条按钮数量随机器存活/在场情况变化，按钮集合与 <see cref="RegionControlSystem.GetCandidateLogicIds"/>
        /// 对账（新增补建、消失移除），复用现有按钮避免每帧重建 VisualElement。
        /// 每台机器一行：<c>cmd-candidate-item</c>（行容器，挂悬停提示）= 编号按钮 + 按钮外侧的安全模式标记。
        /// 标记放在按钮外面：Button 本身是 TextElement，文字居中绘制、不参与 flex 排布，塞子元素会和“#N”压在一起。</summary>
        private void RefreshCandidateStrip(RegionControlSystem control, int? possessedId)
        {
            if (_candidateStrip == null)
            {
                return;
            }

            List<int> candidates = control.GetCandidateLogicIds();
            // 按钮说明的文字在提示显示时现取（UiTooltip 的 provider），这里只保持按键名 / 语言缓存最新。
            RefreshCandidateTooltip();
            // FG1-SIG-04：安全模式标记的变化键（安全模式版本 + 语言）每帧算一次，按钮逐个只比较整数。
            int safeKey = System.HashCode.Combine(Campaign.Signal.SignalLinkService.SafeModeRevision, (int)GameText.Language);
            // FG1-SIG-07：覆盖外标记的变化键（步首定期评估的次数 + 语言）：每 0.5 游戏秒最多改写一次，读内核标志 O(1)。
            int covKey = System.HashCode.Combine(Campaign.Signal.SignalCoverageService.EvaluateCount, (int)GameText.Language);
            var seen = new HashSet<int>();
            foreach (int logicId in candidates)
            {
                seen.Add(logicId);
                if (!_candidateButtons.TryGetValue(logicId, out Button btn))
                {
                    var item = new VisualElement();
                    item.AddToClassList("cmd-candidate-item");
                    btn = new Button { text = "#" + logicId };
                    btn.AddToClassList("cmd-candidate-btn");
                    int capturedId = logicId;
                    btn.clicked += () => OnCandidateButtonClicked(capturedId);
                    item.Add(btn);
                    // FG1-SIG-03 / FG1-SIG-04：机器列表的说明走运行时悬停提示（VisualElement.tooltip 只在编辑器界面生效）。
                    // 挂在整行上：鼠标在编号按钮或安全模式标记上都能看到“这台为什么进安全模式、什么时候恢复 + 怎么接入”。
                    UiTooltip.Attach(item, () => CandidateTooltipContent(capturedId));
                    _candidateStrip.Add(item);
                    _candidateButtons[logicId] = btn;
                }

                if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
                {
                    btn.text = "#" + rec.DisplayNumber;
                }
                RefreshSafeModeTag(btn, logicId, safeKey);
                RefreshCoverageTag(btn, logicId, covKey);
                btn.RemoveFromClassList("cmd-candidate-btn-current");
                if (possessedId.HasValue && possessedId.Value == logicId)
                {
                    btn.AddToClassList("cmd-candidate-btn-current");
                }
            }

            var stale = new List<int>();
            foreach (KeyValuePair<int, Button> kv in _candidateButtons)
            {
                if (!seen.Contains(kv.Key))
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (int logicId in stale)
            {
                VisualElement item = ItemOf(_candidateButtons[logicId]);
                UiTooltip.Detach(item);
                item.RemoveFromHierarchy();
                _candidateButtons.Remove(logicId);
                _safeTagKeys.Remove(logicId);
                _covTagKeys.Remove(logicId);
            }
        }

        private static VisualElement ItemOf(Button btn) => btn.parent ?? btn;

        /// <summary>机器列表一行的悬停提示（显示那一刻现取）：标题 = 机器名；正文 = 安全模式时先写进入原因、行为与恢复条件，再写怎么接入（按键名随重绑、随语言）。</summary>
        private TooltipContent CandidateTooltipContent(int logicId)
        {
            RefreshCandidateTooltip();
            CampaignState s = CampaignSession.Current;
            string body = _candidateTip ?? string.Empty;
            // FG1-SIG-07：在覆盖外时，“收不到命令、怎么恢复”排在接入说明前面（安全模式那一段仍排最前）。
            if (Campaign.Signal.SignalCoverageService.IsMachineOutOfCoverage(logicId))
            {
                body = GameText.Format("signal.coverage.list_tooltip", SignalPresence.MachineLabel(logicId)) + "\n" + body;
            }
            if (Campaign.Signal.SignalLinkService.IsInSafeMode(s, logicId))
            {
                body = Campaign.Signal.SignalLinkService.SafeModeTooltip(s, logicId) + "\n" + body;
            }
            return new TooltipContent
            {
                Title = SignalPresence.MachineLabel(logicId),
                Body = body,
            };
        }

        /// <summary>FG1-SIG-04（FGR-SIG-041“机器列表中也有标记”）：安全模式的机器在列表里带盾形图标 + “安全”字样（放在编号按钮外侧），悬停提示写明原因与恢复条件。
        /// 只在这台机器的安全模式状态 / 语言变化时改写（每帧 O(1) 查询：比较安全模式版本号）。</summary>
        private void RefreshSafeModeTag(Button btn, int logicId, int key)
        {
            if (_safeTagKeys.TryGetValue(logicId, out int seen) && seen == key)
            {
                return;
            }
            _safeTagKeys[logicId] = key;
            bool safe = Campaign.Signal.SignalLinkService.IsInSafeMode(CampaignSession.Current, logicId);
            VisualElement item = ItemOf(btn);
            VisualElement tag = item.Q<VisualElement>(className: "cmd-candidate-safe-tag");
            if (tag == null)
            {
                tag = new VisualElement { pickingMode = PickingMode.Ignore };
                tag.AddToClassList("cmd-candidate-safe-tag");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("cmd-candidate-safe-icon");
                GameLogic.UI.Common.ContentIcons.ApplyIcon(icon, GameLogic.UI.Common.ContentIcons.StateSafeMode);
                var label = new Label { pickingMode = PickingMode.Ignore };
                label.AddToClassList("cmd-candidate-safe-label");
                tag.Add(icon);
                tag.Add(label);
                item.Add(tag);
            }
            tag.Q<Label>(className: "cmd-candidate-safe-label").text = GameText.Get("signal.safe_mode.list_tag");
            btn.EnableInClassList("cmd-candidate-btn-safe", safe);
            item.EnableInClassList("cmd-candidate-item-safe", safe);
        }

        /// <summary>FG1-SIG-07（FGR-SIG-053）：在信号覆盖外的机器在列表里带“覆盖外”字样（按钮外侧）与橙色边框，悬停提示写明收不到命令、怎么恢复。</summary>
        private void RefreshCoverageTag(Button btn, int logicId, int key)
        {
            if (_covTagKeys.TryGetValue(logicId, out int seen) && seen == key)
            {
                return;
            }
            _covTagKeys[logicId] = key;
            bool outside = Campaign.Signal.SignalCoverageService.IsMachineOutOfCoverage(logicId);
            VisualElement item = ItemOf(btn);
            Label tag = item.Q<Label>(className: "cmd-candidate-nolink-tag");
            if (tag == null)
            {
                tag = new Label { pickingMode = PickingMode.Ignore };
                tag.AddToClassList("cmd-candidate-nolink-tag");
                item.Add(tag);
            }
            tag.text = GameText.Get("signal.coverage.list_tag");
            btn.EnableInClassList("cmd-candidate-btn-nolink", outside);
            item.EnableInClassList("cmd-candidate-item-nolink", outside);
        }

        private readonly Dictionary<int, int> _covTagKeys = new Dictionary<int, int>(8);

        /// <summary>自检：机器列表里这台机器是否带“覆盖外”标记。</summary>
        public bool CandidateShowsOutOfCoverage(int logicId) =>
            _candidateButtons.TryGetValue(logicId, out Button b) && b != null && b.ClassListContains("cmd-candidate-btn-nolink");

        /// <summary>候选按钮上次刷新安全模式标记时的状态键（LogicId → 键）。</summary>
        private readonly Dictionary<int, int> _safeTagKeys = new Dictionary<int, int>(8);

        /// <summary>自检：机器列表里这台机器的按钮是否带安全模式标记。</summary>
        public bool CandidateShowsSafeMode(int logicId) =>
            _candidateButtons.TryGetValue(logicId, out Button b) && b != null && b.ClassListContains("cmd-candidate-btn-safe");

        /// <summary>自检 / 冒烟：机器列表里这台机器那一行（挂着悬停提示的元素；模拟悬停对它派发指针进入）。</summary>
        public VisualElement CandidateTooltipTarget(int logicId) =>
            _candidateButtons.TryGetValue(logicId, out Button b) && b != null ? ItemOf(b) : null;

        /// <summary>ER5-INT-01：E 交互主候选提示（动词 + 当前按键名，随重绑动态拼接，不写死"按 E"）+
        /// 按住/点击进度条 + 完成/拒绝字幕。没有主候选或没有受控机时整块隐藏——不占战略视角的屏幕。</summary>
        private void RefreshInteractHud()
        {
            RegionInteractionSystem interact = ActiveInteraction();
            if (_interactPromptLabel == null)
            {
                return;
            }

            if (interact == null)
            {
                _interactPromptLabel.text = string.Empty;
                if (_interactProgressTrack != null)
                {
                    _interactProgressTrack.style.display = DisplayStyle.None;
                }
                _interactSubtitleLabel.text = string.Empty;
                return;
            }

            RegionInteractCandidate candidate = interact.PrimaryCandidate;
            string keyLabel = RegionInteractionSystem.InteractKeyLabel;
            if (candidate != null)
            {
                string verb = candidate.HoldSeconds > 0.0001f ? "按住" : "按";
                _interactPromptLabel.text = $"{verb} {keyLabel} {candidate.ActionVerb}";
            }
            else if (interact.LastFailure != RegionInteractFailure.None && interact.LastFailure != RegionInteractFailure.NoControlledUnit)
            {
                _interactPromptLabel.text = InteractFailureText(interact.LastFailure, interact.LastFailureText);
            }
            else
            {
                _interactPromptLabel.text = string.Empty;
            }

            if (_interactProgressTrack != null)
            {
                bool showProgress = candidate != null && candidate.HoldSeconds > 0.0001f && interact.Progress01 > 0f;
                _interactProgressTrack.style.display = showProgress ? DisplayStyle.Flex : DisplayStyle.None;
                if (showProgress && _interactProgressFill != null)
                {
                    _interactProgressFill.style.width = new Length(interact.Progress01 * 100f, LengthUnit.Percent);
                }
            }

            _interactSubtitleLabel.text = interact.LastSubtitle ?? string.Empty;
        }

        private static string InteractFailureText(RegionInteractFailure failure, string detail)
        {
            switch (failure)
            {
                case RegionInteractFailure.OutOfRange: return "距离过远，无法交互。";
                case RegionInteractFailure.Occluded: return "视线被遮挡，无法交互。";
                case RegionInteractFailure.CargoFull: return detail ?? "货舱/仓储已满。";
                case RegionInteractFailure.MachineLostControl: return "信号中断，机器暂时失控。";
                case RegionInteractFailure.ModalBlocked: return string.Empty; // 模态打开时不必再提示世界交互。
                case RegionInteractFailure.TargetGone: return detail ?? "目标已不可用。";
                default: return string.Empty;
            }
        }

        private void OnDestroy()
        {
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
