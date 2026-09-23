using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Stage;
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
    /// 归还谷地与破碎都市共用同一个实例（两区域互斥运行，谁在跑就显示谁的
    /// <see cref="RegionSquadCommandSystem"/>），同 <c>StrategyClockHudToolkit</c> 的常驻单例写法。</summary>
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

        /// <summary>当前哪个区域在跑，就读它的 <see cref="RegionSquadCommandSystem"/>——两区域互斥，
        /// 不会同时 Active。</summary>
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
            return null;
        }

        /// <summary>候选条点击一台合法机器后，若镜头尚未处于 Direct，补一次正式过渡请求。</summary>
        private static void ActiveRequestDirectView()
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                GameRoot.HomeValley.RequestDirectView();
                return;
            }
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive)
            {
                GameRoot.FracturedCity.RequestDirectView();
            }
        }

        private void OnCandidateButtonClicked(int logicId)
        {
            RegionControlSwitchResult result = ActiveControl()?.TrySwitchControlledUnit(logicId) ?? RegionControlSwitchResult.Fail(RegionControlFailure.Ineligible);
            if (result.Success)
            {
                ActiveRequestDirectView();
                _controlFeedbackLabel.text = string.Empty;
            }
            else
            {
                _controlFeedbackLabel.text = "拒绝：" + result.PlayerText;
                _controlFeedbackRemaining = 3f;
            }
        }

        private void Update()
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

        /// <summary>候选条按钮数量随机器存活/在场情况变化，按钮集合与 <see cref="RegionControlSystem.GetCandidateLogicIds"/>
        /// 对账（新增补建、消失移除），复用现有按钮避免每帧重建 VisualElement。</summary>
        private void RefreshCandidateStrip(RegionControlSystem control, int? possessedId)
        {
            if (_candidateStrip == null)
            {
                return;
            }

            List<int> candidates = control.GetCandidateLogicIds();
            var seen = new HashSet<int>();
            foreach (int logicId in candidates)
            {
                seen.Add(logicId);
                if (!_candidateButtons.TryGetValue(logicId, out Button btn))
                {
                    btn = new Button { text = "#" + logicId };
                    btn.AddToClassList("cmd-candidate-btn");
                    int capturedId = logicId;
                    btn.clicked += () => OnCandidateButtonClicked(capturedId);
                    _candidateStrip.Add(btn);
                    _candidateButtons[logicId] = btn;
                }

                if (MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
                {
                    btn.text = "#" + rec.DisplayNumber;
                }
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
                _candidateStrip.Remove(_candidateButtons[logicId]);
                _candidateButtons.Remove(logicId);
            }
        }

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
