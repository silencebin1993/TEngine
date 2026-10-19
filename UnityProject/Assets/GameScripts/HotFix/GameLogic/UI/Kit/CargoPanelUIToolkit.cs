using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG8-LOOT-03（FG08 FGR-LOOT-030；FG13 FGU-35“货舱：全队与单机明细、丢弃、转移、自动拾取”）：货舱面板。
    /// - 入口：暂停菜单“货舱”、顶栏“货舱 N / M”（远征队在外或货舱里有货时显示）。
    /// - 顶部：全队总容量条（快满 / 已满换色并写文字）、转移目标（同一地点 loot.cargo.transfer_radius 格内的存活机器，上一台 / 下一台），没暂停时给“暂停并转移”。
    /// - 中部：每台机器一行标题（已用 / 容量 · 状态）+ 它的每一份货：“转给目标”“丢弃”；工单在途的货只读。
    /// - 底部：自动拾取开关概况 +“自动拾取设置…”（关掉本面板、打开暂停菜单里已有的设置段，不另开设置入口）。
    /// 模态；Esc / 关闭 / 点遮罩关闭。刷新：货舱版本、暂停、语言、所选机器 / 目标变化或游戏时间每过 1 秒才重建；O(列出的机器 × 货舱行)。
    /// </summary>
    public sealed class CargoPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30083;

        public static CargoPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Label _team;
        private VisualElement _barFill;
        private Label _warn;
        private Button _prev;
        private Button _next;
        private Label _target;
        private Button _pause;
        private Label _message;
        private Label _empty;
        private ScrollView _list;
        private Label _auto;
        private Button _autoOpen;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<(VisualElement Row, Label Label, Button Transfer, Button Discard)> _rows = new List<(VisualElement, Label, Button, Button)>();
        private readonly List<string> _rowLines = new List<string>();
        private readonly List<int> _machines = new List<int>();
        private readonly List<int> _targets = new List<int>();
        private readonly List<CargoLine> _lines = new List<CargoLine>();
        private int _source;
        private int _targetIndex;
        private string _messageText;
        private int _key;

        protected override string UxmlLocation => "CargoPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TeamText => _team?.text ?? string.Empty;
        public string WarnText => _warn != null && !_warn.ClassListContains("uk-hidden") ? _warn.text : string.Empty;
        public string TargetText => _target?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string AutoText => _auto?.text ?? string.Empty;
        public bool BarHasClass(string cls) => _barFill != null && _barFill.ClassListContains(cls);
        public float BarPercent => _barFill != null ? _barFill.style.width.value.value : 0f;
        public bool PauseVisible => _pause != null && !_pause.ClassListContains("uk-hidden");
        public int RowCount => _rowLines.Count;
        public string RowText(int i) => i >= 0 && i < _rowLines.Count ? _rows[i].Label.text : string.Empty;
        public string RowLineId(int i) => i >= 0 && i < _rowLines.Count ? _rowLines[i] : null;
        public bool RowIsHeader(int i) => i >= 0 && i < _rowLines.Count && _rows[i].Row.ClassListContains("cg-row-header");
        public Button RowTransfer(int i) => i >= 0 && i < _rowLines.Count ? _rows[i].Transfer : null;
        public Button RowDiscard(int i) => i >= 0 && i < _rowLines.Count ? _rows[i].Discard : null;
        public int TargetMachine => _targetIndex >= 0 && _targetIndex < _targets.Count ? _targets[_targetIndex] : 0;
        public Button PauseButton => _pause;
        public Button AutoOpenButton => _autoOpen;
        public Button NextTargetButton => _next;
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
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            if (!IsOpen)
            {
                Instance.SetOpen(true);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open();
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CargoRow");
            _templateLoading = false;
            if (this == null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            _rowTemplate = t;
            if (t == null)
            {
                Log.Error("[CargoPanelUIToolkit] 加载 CargoRow 失败，货舱行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("CargoRoot");
            _title = root.Q<Label>("CargoTitle");
            _help = root.Q<Button>("CargoHelp");
            _close = root.Q<Button>("CargoClose");
            _team = root.Q<Label>("CargoTeam");
            _barFill = root.Q<VisualElement>("CargoBarFill");
            _warn = root.Q<Label>("CargoWarn");
            _prev = root.Q<Button>("CargoTargetPrev");
            _next = root.Q<Button>("CargoTargetNext");
            _target = root.Q<Label>("CargoTarget");
            _pause = root.Q<Button>("CargoPause");
            _message = root.Q<Label>("CargoMessage");
            _empty = root.Q<Label>("CargoEmpty");
            _list = root.Q<ScrollView>("CargoList");
            _auto = root.Q<Label>("CargoAuto");
            _autoOpen = root.Q<Button>("CargoAutoOpen");
            _footer = root.Q<Label>("CargoFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += () => MechanicCodex.Open("codex.expedition.cargo");
            _prev.clicked += () => StepTarget(-1);
            _next.clicked += () => StepTarget(1);
            _pause.clicked += PauseForTransfer;
            _autoOpen.clicked += OpenAutoPickupSettings;
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
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                GuidanceHooks.Raise(GuidanceHooks.LootFirstCargoOpen);
                _messageText = null;
                _key = 0;
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

        public void StepTarget(int delta)
        {
            if (_targets.Count == 0)
            {
                return;
            }
            _targetIndex = ((_targetIndex + delta) % _targets.Count + _targets.Count) % _targets.Count;
            _messageText = null;
            Refresh(force: true);
        }

        /// <summary>“暂停并转移”：进入战略暂停（与顶栏暂停同一个开关）。</summary>
        public void PauseForTransfer()
        {
            if (!GameClock.Paused)
            {
                GameClock.SetPaused(true);
            }
            _messageText = null;
            Refresh(force: true);
        }

        /// <summary>“自动拾取设置…”：关掉本面板，打开暂停菜单（自动拾取开关在暂停菜单的设置段里，不另开入口）。</summary>
        public void OpenAutoPickupSettings()
        {
            SetOpen(false);
            PauseMenuUIToolkit.Open();
            PauseMenuUIToolkit.Instance?.ScrollToAutoPickup();
        }

        /// <summary>第 <paramref name="row"/> 行的货转给当前转移目标（与点“转给目标”同一条路）。</summary>
        public void Transfer(int row)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || row < 0 || row >= _rowLines.Count || string.IsNullOrEmpty(_rowLines[row]))
            {
                return;
            }
            bool ok = CargoService.TryTransfer(s, _rowLines[row], TargetMachine, out string msg);
            _messageText = msg;
            if (!ok && !string.IsNullOrEmpty(msg))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, msg);
            }
            Refresh(force: true);
        }

        public void Discard(int row)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || row < 0 || row >= _rowLines.Count || string.IsNullOrEmpty(_rowLines[row]))
            {
                return;
            }
            bool ok = CargoService.TryDiscard(s, _rowLines[row], out string msg);
            _messageText = msg;
            if (!ok && !string.IsNullOrEmpty(msg))
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, msg);
            }
            Refresh(force: true);
        }

        /// <summary>点某台机器的标题行：把它当作“从哪台转出”的参考（转移目标从它附近的机器里选）。</summary>
        public void SelectSource(int machine)
        {
            _source = machine;
            _targetIndex = 0;
            Refresh(force: true);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine(CargoService.Revision, GameClock.Paused, _source, _targetIndex, (int)GameText.Language,
                HashCode.Combine(GameSettings.Revision, s != null ? s.GetHashCode() : 0, GameClock.Ticks / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0, _messageText));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            _title.text = GameText.Get("cargo.title");
            _help.text = GameText.Get("cargo.help");
            _close.text = GameText.Get("cargo.close");
            _prev.text = GameText.Get("cargo.target.prev");
            _next.text = GameText.Get("cargo.target.next");
            _pause.text = GameText.Get("cargo.pause");
            _autoOpen.text = GameText.Get("cargo.auto.open");
            _footer.text = GameText.Get("cargo.footer");

            CargoService.PanelMachines(s, _machines);
            int worst = CargoService.Totals(s, _machines, out int used, out int cap);
            bool team = s != null && CargoService.TeamOut(s);
            _team.text = team || _machines.Count > 0
                ? GameText.Format("cargo.team", used, cap, _machines.Count) + (team ? string.Empty : "  ·  " + GameText.Get("cargo.team.none"))
                : GameText.Get("cargo.team.none");
            float pct = cap > 0 ? Mathf.Clamp01(used / (float)cap) * 100f : 0f;
            _barFill.style.width = new StyleLength(new Length(pct, LengthUnit.Percent));
            _barFill.EnableInClassList("cg-bar-warn", worst == 1);
            _barFill.EnableInClassList("cg-bar-full", worst == 2);
            RefreshWarn(s);

            if (_source <= 0 || !_machines.Contains(_source))
            {
                _source = _machines.Count > 0 ? _machines[0] : 0;
            }
            CargoService.TransferTargets(s, _source, _targets);
            if (_targetIndex >= _targets.Count)
            {
                _targetIndex = 0;
            }
            int target = TargetMachine;
            _prev.SetEnabled(_targets.Count > 1);
            _next.SetEnabled(_targets.Count > 1);
            if (target > 0 && MachineRegistry.TryGetRecord(target, out MachineRecord tm) && tm != null)
            {
                _target.text = GameText.Format("cargo.target", MachineNaming.Short(tm), CargoService.Free(s, tm));
            }
            else
            {
                _target.text = GameText.Format("cargo.target.none", Mathf.RoundToInt(CargoService.TransferRadius));
            }
            _pause.EnableInClassList("uk-hidden", GameClock.Paused || target <= 0);

            bool empty = _machines.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("cargo.empty") : string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            _message.text = _messageText ?? string.Empty;
            int on = s != null ? CargoService.AutoPickupOnCount(s) : 0;
            _auto.text = GameText.Format("cargo.auto.summary", on, CargoService.AutoPickupItems().Count - on);
            RebuildRows(s, target);
        }

        private void RefreshWarn(CampaignState s)
        {
            string text = null;
            int level = 0;
            foreach (int id in _machines)
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null)
                {
                    continue;
                }
                int u = CargoService.Used(s, m);
                int c = CargoService.Capacity(s, m);
                int l = CargoService.Level(u, c);
                if (l > level)
                {
                    level = l;
                    text = GameText.Format(l == 2 ? "cargo.full_banner" : "cargo.warn_banner", MachineNaming.Short(m), u, c);
                }
            }
            _warn.EnableInClassList("uk-hidden", text == null);
            _warn.text = text ?? string.Empty;
        }

        private void RebuildRows(CampaignState s, int target)
        {
            int n = 0;
            foreach (int id in _machines)
            {
                SetRow(n++, s, id, null, target);
                CargoService.LinesOf(s, id, _lines);
                foreach (CargoLine line in _lines)
                {
                    SetRow(n++, s, id, line, target);
                }
            }
            _rowLines.Clear();
            int shown = Math.Min(n, _rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Row.parent.EnableInClassList("uk-hidden", i >= shown);
            }
            for (int i = 0; i < shown; i++)
            {
                _rowLines.Add(_pendingIds[i]);
            }
            _pendingIds.Clear();
        }

        private readonly List<string> _pendingIds = new List<string>();

        private void SetRow(int i, CampaignState s, int machine, CargoLine? line, int target)
        {
            if (_rowTemplate == null)
            {
                return;
            }
            while (_rows.Count <= i)
            {
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("cg-row-host");
                _list.Add(host);
                Button transfer = host.Q<Button>("CgTransfer");
                Button discard = host.Q<Button>("CgDiscard");
                int row = _rows.Count;
                transfer.clicked += () => Transfer(row);
                discard.clicked += () => Discard(row);
                VisualElement r = host.Q<VisualElement>("CgRow");
                r.RegisterCallback<ClickEvent>(_ =>
                {
                    if (row < _rowMachines.Count && RowIsHeader(row))
                    {
                        SelectSource(_rowMachines[row]);
                    }
                });
                _rows.Add((r, host.Q<Label>("CgLabel"), transfer, discard));
            }
            while (_rowMachines.Count <= i)
            {
                _rowMachines.Add(0);
            }
            _rowMachines[i] = machine;
            while (_pendingIds.Count <= i)
            {
                _pendingIds.Add(null);
            }
            (VisualElement rowEl, Label label, Button tr, Button dc) = _rows[i];
            bool header = line == null;
            rowEl.EnableInClassList("cg-row-header", header);
            tr.EnableInClassList("uk-hidden", header);
            dc.EnableInClassList("uk-hidden", header);
            if (header)
            {
                CargoService.LinesOf(s, machine, _probe);
                label.text = CargoService.MachineText(s, machine) + (_probe.Count == 0 ? "  " + GameText.Get("cargo.machine.empty") : string.Empty)
                             + (machine == _source ? "  ◂" : string.Empty);
                _pendingIds[i] = null;
                return;
            }
            CargoLine l = line.Value;
            label.text = l.Text;
            tr.text = GameText.Get("cargo.transfer");
            dc.text = GameText.Get("cargo.discard");
            // 转移目标按“当前转出机器”选；别的机器的行只有它自己也够得着这个目标（同一地点、转移半径内）时才能点，免得点了被“太远”拒绝。
            tr.SetEnabled(l.Movable && target > 0 && target != l.Machine && (l.Machine == _source || CargoService.InTransferRange(l.Machine, target)));
            dc.SetEnabled(l.Movable);
            _pendingIds[i] = l.Id;
        }

        private readonly List<int> _rowMachines = new List<int>();
        private readonly List<CargoLine> _probe = new List<CargoLine>();
    }
}
