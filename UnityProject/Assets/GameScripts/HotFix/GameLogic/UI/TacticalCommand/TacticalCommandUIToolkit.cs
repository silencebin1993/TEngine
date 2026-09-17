using Cysharp.Threading.Tasks;
using BinGames.Sim;
using GameLogic.Stage;
using GameLogic.Stage.CellStage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.TacticalCommand
{
    /// <summary>
    /// 战术层的编队管理入口。世界中的点选、框选与右键落点仍由 SquadCommandSystem 处理；
    /// 本面板只提供可见的选择集、队列和数字编队控制，避免遮挡战场。
    /// </summary>
    public sealed class TacticalCommandUIToolkit : MonoBehaviour
    {
        private const float RefreshInterval = 0.15f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private PanelSettings _panelSettings;
        private VisualElement _root;
        private Label _selectionLabel;
        private Label _queueLabel;
        private Label _dispatchLabel;
        private Label _feedbackLabel;
        private ScrollView _groupList;
        private Label _attackPartLabel;
        private VisualElement _detailPanel;
        private Label _detailTitle;
        private Label _detailState;
        private ScrollView _memberList;
        private int _detailSlot;
        private bool _panelOpen;
        private float _nextRefreshTime;

        public static TacticalCommandUIToolkit Instance { get; private set; }
        public bool IsPanelOpen => _panelOpen;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("TacticalCommandUI");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 5;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[TacticalCommandUIToolkit] rootVisualElement 等待超时，战术指挥面板未初始化。");
                return;
            }

            _selectionLabel = _root.Q<Label>("selectionLabel");
            _queueLabel = _root.Q<Label>("queueLabel");
            _dispatchLabel = _root.Q<Label>("dispatchLabel");
            _feedbackLabel = _root.Q<Label>("tacticalFeedbackLabel");
            _groupList = _root.Q<ScrollView>("groupList");
            _attackPartLabel = _root.Q<Label>("attackPartLabel");
            _detailPanel = _root.Q<VisualElement>("tacticalDetailPanel");
            _detailTitle = _root.Q<Label>("groupDetailTitle");
            _detailState = _root.Q<Label>("groupDetailState");
            _memberList = _root.Q<ScrollView>("groupMemberList");
            BindClick("closeTacticsButton", () => SetPanelOpen(false));
            BindClick("clearSelectionButton", ClearSelection);
            BindClick("cycleAttackPartButton", CycleAttackPart);
            BindClick("closeGroupDetailButton", HideGroupDetails);
            BindClick("detailRecallButton", RecallDetailGroup);
            BindClick("clearGroupButton", ClearDetailGroup);
            VisualElement tacticalRoot = _root.Q<VisualElement>("tacticalRoot");
            if (tacticalRoot != null)
            {
                tacticalRoot.pickingMode = PickingMode.Ignore;
            }
            VisualElement tacticalPanel = _root.Q<VisualElement>("tacticalPanel");
            UiWindowFocus.Attach(_document, tacticalPanel, tacticalPanel?.Q<Label>(className: "tactical-title"), "tactics");
            UiWindowFocus.Attach(_document, _detailPanel, _detailTitle, "tactics-detail");
            ApplyPanelState();
        }

        public void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (!open)
            {
                HideGroupDetails();
            }
            ApplyPanelState();
            if (open)
            {
                _nextRefreshTime = 0f;
                UiWindowFocus.BringToFront(_document, _root?.Q<VisualElement>("tacticalPanel"));
            }
        }

        private void BindClick(string nodeName, System.Action action)
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
            if (cell == null || !cell.IsRunning || cell.SquadCommands == null)
            {
                SetPanelOpen(false);
                return;
            }

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshInterval;
                Refresh(cell);
            }
        }

        private void ApplyPanelState()
        {
            _root?.EnableInClassList("is-hidden", !_panelOpen);
        }

        private void Refresh(CellStageFlow cell)
        {
            if (_selectionLabel != null)
            {
                _selectionLabel.text = $"当前选择：{cell.SquadCommands.Selection.Count} 个单位";
            }
            if (_queueLabel != null)
            {
                _queueLabel.text = $"暂停队列：{cell.SquadCommands.QueuedCommandCount}";
            }
            if (_dispatchLabel != null)
            {
                _dispatchLabel.text = $"最近命令：{DescribeDispatch(cell.SquadCommands.LastFormationDispatchOutcome)}";
            }
            if (_attackPartLabel != null)
            {
                _attackPartLabel.text = $"攻击接点：{DescribePart(cell.SquadCommands.PendingAttackPart)}";
            }

            RefreshGroups(cell);
            RefreshGroupDetails(cell);
        }

        private void RefreshGroups(CellStageFlow cell)
        {
            if (_groupList == null)
            {
                return;
            }

            _groupList.Clear();
            for (int slot = 1; slot <= 9; slot++)
            {
                int capturedSlot = slot;
                int size = cell.SquadCommands.GroupSize(slot);
                var row = new VisualElement();
                row.AddToClassList("tactical-group-row");
                var title = new Label($"编队 {slot} · {size} 个成员");
                title.AddToClassList("tactical-row-title");
                row.Add(title);

                var actions = new VisualElement();
                actions.AddToClassList("tactical-row-actions");
                var recall = new Button { text = "召回" };
                recall.AddToClassList("tactical-row-button");
                recall.SetEnabled(size > 0);
                recall.clicked += () =>
                {
                    GameRoot.CellStage?.SquadCommands?.RecallGroup(capturedSlot);
                    SetFeedback($"已召回编队 {capturedSlot}。");
                    _nextRefreshTime = 0f;
                };
                actions.Add(recall);

                var assign = new Button { text = "编入当前选择" };
                assign.AddToClassList("tactical-row-button");
                assign.SetEnabled(cell.SquadCommands.Selection.Count > 0);
                assign.clicked += () =>
                {
                    GameRoot.CellStage?.SquadCommands?.AssignGroup(capturedSlot);
                    SetFeedback($"已用当前选择更新编队 {capturedSlot}。");
                    _nextRefreshTime = 0f;
                };
                actions.Add(assign);

                var details = new Button { text = "成员" };
                details.AddToClassList("tactical-row-button");
                details.clicked += () => ShowGroupDetails(capturedSlot);
                actions.Add(details);
                row.Add(actions);
                _groupList.Add(row);
            }
        }

        private void ClearSelection()
        {
            GameRoot.CellStage?.SquadCommands?.ClearSelection();
            SetFeedback("已清空当前选择。");
            _nextRefreshTime = 0f;
        }

        private void CycleAttackPart()
        {
            SimBodyPartSlot part = GameRoot.CellStage?.SquadCommands?.CyclePendingAttackPart() ?? SimBodyPartSlot.None;
            SetFeedback($"下一次攻击将瞄准：{DescribePart(part)}。");
            _nextRefreshTime = 0f;
        }

        private void ShowGroupDetails(int slot)
        {
            _detailSlot = slot;
            _detailPanel?.RemoveFromClassList("is-hidden");
            UiWindowFocus.BringToFront(_document, _detailPanel);
            _nextRefreshTime = 0f;
        }

        private void HideGroupDetails()
        {
            _detailSlot = 0;
            _detailPanel?.AddToClassList("is-hidden");
        }

        private void RefreshGroupDetails(CellStageFlow cell)
        {
            if (_detailSlot <= 0 || _detailPanel == null || _memberList == null)
            {
                return;
            }

            int size = cell.SquadCommands.GroupSize(_detailSlot);
            if (_detailTitle != null)
            {
                _detailTitle.text = $"编队 {_detailSlot} · 成员详情";
            }
            if (_detailState != null)
            {
                _detailState.text = size > 0 ? $"当前记录 {size} 个成员。阵亡单位会在召回时自动过滤。" : "该编队为空。";
            }
            _memberList.Clear();
            var members = cell.SquadCommands.GroupMembers(_detailSlot);
            for (int i = 0; i < members.Count; i++)
            {
                var row = new Label($"单位 #{members[i]}");
                row.AddToClassList("tactical-state");
                _memberList.Add(row);
            }
        }

        private void RecallDetailGroup()
        {
            GameRoot.CellStage?.SquadCommands?.RecallGroup(_detailSlot);
            SetFeedback($"已召回编队 {_detailSlot}。");
            _nextRefreshTime = 0f;
        }

        private void ClearDetailGroup()
        {
            int slot = _detailSlot;
            GameRoot.CellStage?.SquadCommands?.ClearGroup(slot);
            SetFeedback($"已解除编队 {slot}，不影响单位当前命令。");
            HideGroupDetails();
            _nextRefreshTime = 0f;
        }

        private static string DescribePart(SimBodyPartSlot part)
        {
            switch (part)
            {
                case SimBodyPartSlot.Primary: return "主接点";
                case SimBodyPartSlot.Secondary: return "副接点";
                default: return "无指定";
            }
        }

        private static string DescribeDispatch(Command.SquadFormationDispatchOutcome outcome)
        {
            switch (outcome)
            {
                case Command.SquadFormationDispatchOutcome.Activated:
                    return "编队命令已生效";
                case Command.SquadFormationDispatchOutcome.QueuedByPlayerRequest:
                    return "已追加到编队队列";
                case Command.SquadFormationDispatchOutcome.QueuedBehindHigherPriority:
                    return "等待更高优先级命令";
                case Command.SquadFormationDispatchOutcome.CancelledStaleMembership:
                    return "编队成员变化，排队命令已取消";
                default:
                    return "普通选择集命令";
            }
        }

        private void SetFeedback(string text)
        {
            if (_feedbackLabel != null)
            {
                _feedbackLabel.text = text;
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
