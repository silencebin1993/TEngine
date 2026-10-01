using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-05（FG03 FGR-LOG-042～044；卡片“悬停显示网络的供给、需求、储量、瓶颈”“冲洗需要确认”；FG00 B04 / B05 / B06 / B12 / B13）：管线面板。
    /// - 状态行 = 所在网络的状态与根因；读数行 = 件自己的读数（泵 / 储罐 / 阀门）+ 网络的供给、需求、输送、上限、储量、瓶颈、成员（与悬停同一来源）。
    /// - 储罐：模式（双向 / 只进 / 只出）、只进时的优先级 1～4；阀门：开 / 关、调头。下拉框选中即生效（UI Toolkit 红线 8）。
    /// - 冲洗网络：先弹确认框（写明清掉多少、什么流体、有泵时会重新充入；取消什么都不变），确认才冲洗（FGR-LOG-044）。阀门没有网络，冲洗按钮写明原因。
    /// 入口：建造模式里空闲时点一下已建成的管线件。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 秒（真实时间）刷新，O(1)。
    /// </summary>
    public sealed class PipePanelUIToolkit : UiKitPanelHost
    {
        /// <summary>节点面板 30047 之上、字幕 30050 之下，暂停菜单 30070 之下。</summary>
        public const int Order = 30048;

        private const float RefreshSeconds = 0.25f;

        public static PipePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static GridCell Cell { get; private set; }
        private static GridCell? _pendingCell;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _close;
        private Label _state;
        private VisualElement _tankBox;
        private VisualElement _valveBox;
        private DropdownField _mode;
        private DropdownField _priority;
        private Button _valveToggle;
        private Button _valveReverse;
        private Button _flush;
        private Label _detail;
        private Label _message;
        private Label _hint;
        private float _timer;
        private PipeCellInfo _cell;
        private bool _hasCell;
        private readonly StringBuilder _sb = new StringBuilder(512);

        protected override string UxmlLocation => "PipePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public string MessageText => _message?.text ?? string.Empty;
        public bool TankSettingsVisible => _tankBox != null && !_tankBox.ClassListContains("bn-hidden");
        public bool ValveSettingsVisible => _valveBox != null && !_valveBox.ClassListContains("bn-hidden");
        public DropdownField ModeField => _mode;
        public DropdownField PriorityField => _priority;
        public Button ValveToggleButton => _valveToggle;
        public Button ValveReverseButton => _valveReverse;
        public Button FlushButton => _flush;
        public Button CloseButton => _close;

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
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public static void Open(GridCell cell)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingCell = cell;
                return;
            }
            Cell = cell;
            if (IsOpen)
            {
                Instance._message.text = string.Empty;
                Instance.Refresh();
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingCell = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingCell.HasValue)
            {
                GridCell c = _pendingCell.Value;
                _pendingCell = null;
                Open(c);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("PipePanelRoot");
            _title = root.Q<Label>("PipePanelTitle");
            _close = root.Q<Button>("PipePanelClose");
            _state = root.Q<Label>("PpState");
            _tankBox = root.Q<VisualElement>("PpTankBox");
            _valveBox = root.Q<VisualElement>("PpValveBox");
            _mode = root.Q<DropdownField>("PpTankMode");
            _priority = root.Q<DropdownField>("PpPriority");
            _valveToggle = root.Q<Button>("PpValveToggle");
            _valveReverse = root.Q<Button>("PpValveReverse");
            _flush = root.Q<Button>("PpFlush");
            _detail = root.Q<Label>("PpDetail");
            _message = root.Q<Label>("PpMessage");
            _hint = root.Q<Label>("PipePanelHint");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _mode.RegisterValueChangedCallback(evt => SetTankMode((PipeTankMode)Mathf.Max(0, _mode.choices.IndexOf(evt.newValue))));
            _priority.RegisterValueChangedCallback(evt => SetTankPriority(Mathf.Max(0, _priority.choices.IndexOf(evt.newValue)) + PipeConst.PriorityMin));
            _valveToggle.clicked += () => SetValveOpen(!_cell.ValveOpen);
            _valveReverse.clicked += () => ReverseValve();
            _flush.clicked += () => AskFlush();
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
                GuidanceHooks.Raise(GuidanceHooks.LogisticsPipePanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                _message.text = string.Empty;
                Refresh();
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh();
        }

        /// <summary>按内核读数刷新（O(1)）。件不在了（被拆）就收起。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            _hasCell = PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetCellInfo(Cell.X, Cell.Y, out _cell);
            if (IsOpen && !_hasCell)
            {
                SetOpen(false);
                return;
            }
            _close.text = GameText.Get("logistics.pipepanel.close");
            if (!_hasCell)
            {
                return;
            }
            _title.text = GameText.Format("logistics.pipe.hover.title", PipeNetworkService.PieceName(_cell.Kind, _cell.Tier), Cell.X, Cell.Y);
            int net = _cell.Kind == PipePieceKind.Valve ? (_cell.ValveFrom >= 0 ? _cell.ValveFrom : _cell.ValveTo) : _cell.Network;
            bool hasNet = PipeNetworkService.Kernel.TryGetNetworkInfo(net, out PipeNetInfo n);
            _state.text = hasNet ? PipeNetworkService.DescribeState(n) : string.Empty;
            if (_cell.Kind == PipePieceKind.Pump)
            {
                // FG4-ECO-02：流体泵的通用状态行（工作中 / 待命 / 不在流体源上）放在最前面。
                string pump = PipeNetworkService.PumpStateLine(CampaignSession.Current, _cell);
                _state.text = string.IsNullOrEmpty(_state.text) ? pump : pump + "\n" + _state.text;
            }
            _sb.Clear();
            if (_cell.Kind != PipePieceKind.Pipe)
            {
                PipeNetworkService.AppendPieceLines(_sb, _cell);
            }
            if (hasNet)
            {
                if (_sb.Length > 0)
                {
                    _sb.Append('\n');
                }
                PipeNetworkService.AppendNetworkLines(_sb, n);
            }
            _detail.text = _sb.ToString();
            bool tank = _cell.Kind == PipePieceKind.Tank;
            bool valve = _cell.Kind == PipePieceKind.Valve;
            _tankBox.EnableInClassList("bn-hidden", !tank);
            _valveBox.EnableInClassList("bn-hidden", !valve);
            _hint.text = GameText.Get("logistics.pipepanel.hint");
            _flush.text = GameText.Get("logistics.pipepanel.flush");
            _flush.SetEnabled(!valve);
            if (tank)
            {
                SetLabel(_tankBox, "PpTankModeLabel", "logistics.pipepanel.tank_mode");
                SetLabel(_tankBox, "PpPriorityLabel", "logistics.pipepanel.tank_priority");
                SetLabel(_tankBox, "PpPriorityNote", "logistics.pipepanel.priority_note");
                var modes = new List<string>
                {
                    PipeNetworkService.TankModeName(PipeTankMode.Both), PipeNetworkService.TankModeName(PipeTankMode.InOnly), PipeNetworkService.TankModeName(PipeTankMode.OutOnly),
                };
                DropdownChoices.Apply(_mode, modes, modes[0]);
                _mode.SetValueWithoutNotify(_mode.choices[Mathf.Clamp((int)_cell.TankMode, 0, 2)]);
                var prios = new List<string>(PipeConst.PriorityMax);
                for (int p = PipeConst.PriorityMin; p <= PipeConst.PriorityMax; p++)
                {
                    prios.Add(GameText.Format("logistics.pipepanel.priority_item", p));
                }
                DropdownChoices.Apply(_priority, prios, prios[0]);
                _priority.SetValueWithoutNotify(_priority.choices[Mathf.Clamp(_cell.Priority - PipeConst.PriorityMin, 0, prios.Count - 1)]);
            }
            if (valve)
            {
                _valveToggle.text = GameText.Get(_cell.ValveOpen ? "logistics.pipepanel.valve_close" : "logistics.pipepanel.valve_open");
                _valveReverse.text = GameText.Get("logistics.pipepanel.valve_reverse");
            }
        }

        private static void SetLabel(VisualElement box, string name, string key)
        {
            Label l = box?.Q<Label>(name);
            if (l != null)
            {
                l.text = GameText.Get(key);
            }
        }

        // ── 设置（控件与自检同一入口）──────────────────────────────────────────────

        public bool SetTankMode(PipeTankMode mode)
        {
            PipeOpResult r = PipeNetworkService.TrySetTankMode(CampaignSession.Current, Cell, mode);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.pipepanel.changed", PipeNetworkService.PieceName(PipePieceKind.Tank, 0)) : r.Describe());
        }

        public bool SetTankPriority(int priority)
        {
            PipeOpResult r = PipeNetworkService.TrySetTankPriority(CampaignSession.Current, Cell, priority);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.pipepanel.changed", PipeNetworkService.PieceName(PipePieceKind.Tank, 0)) : r.Describe());
        }

        public bool SetValveOpen(bool open)
        {
            PipeOpResult r = PipeNetworkService.TrySetValveOpen(CampaignSession.Current, Cell, open);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.pipepanel.changed", PipeNetworkService.PieceName(PipePieceKind.Valve, 0)) : r.Describe());
        }

        public bool ReverseValve()
        {
            PipeOpResult r = PipeNetworkService.TryReverseValve(CampaignSession.Current, Cell);
            return Report(r.Ok, r.Ok ? GameText.Format("logistics.pipepanel.changed", PipeNetworkService.PieceName(PipePieceKind.Valve, 0)) : r.Describe());
        }

        /// <summary>确认框是否正在询问冲洗（自检用）。</summary>
        public bool PendingFlushConfirm { get; private set; }

        /// <summary>冲洗网络前先确认（清掉的流体不返还，FG00 B04）：写明量、流体、储罐数；网络里还有泵时写明会重新充入。阀门直接给原因。</summary>
        public void AskFlush()
        {
            if (!_hasCell)
            {
                return;
            }
            if (_cell.Kind == PipePieceKind.Valve)
            {
                Report(false, GameText.Get("logistics.pipe.reason.flush_valve"));
                return;
            }
            if (!PipeNetworkService.Kernel.TryGetNetworkInfo(_cell.Network, out PipeNetInfo n))
            {
                return;
            }
            GridCell target = Cell;
            string fluid = PipeNetworkService.FluidName(n.Fluid);
            var req = new ConfirmRequest
            {
                Title = GameText.Format("logistics.pipepanel.flush_confirm_title", fluid),
                Irreversible = true,
                ConfirmText = GameText.Get("logistics.pipepanel.flush_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () =>
                {
                    PendingFlushConfirm = false;
                    PipeOpResult r = PipeNetworkService.TryFlush(CampaignSession.Current, target, out long ml, out int f);
                    Report(r.Ok, !r.Ok ? r.Describe()
                        : ml > 0 ? GameText.Format("logistics.pipepanel.flush_done", (ml / 1000.0).ToString("0.#", CultureInfo.InvariantCulture), PipeNetworkService.FluidName(f))
                        : GameText.Get("logistics.pipepanel.flush_empty"));
                },
                OnCancel = () =>
                {
                    PendingFlushConfirm = false;
                    Refresh();
                },
            };
            req.Consequences.Add(GameText.Format("logistics.pipepanel.flush_consequence", (n.StoredMl / 1000.0).ToString("0.#", CultureInfo.InvariantCulture), fluid, n.Tanks));
            if (n.Pumps > 0)
            {
                req.Lines.Add(GameText.Format("logistics.pipepanel.flush_consequence_pump", n.Pumps, fluid));
            }
            UiConfirmDialog.Show(req);
            PendingFlushConfirm = true;
        }

        private bool Report(bool ok, string message)
        {
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
            Refresh();
            return ok;
        }
    }
}
