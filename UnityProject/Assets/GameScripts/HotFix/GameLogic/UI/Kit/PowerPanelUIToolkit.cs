using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG3-LOG-06（FG03 FGR-LOG-060 / 061；卡片“每个子网的发电、耗电、储能曲线”“保留优先级 1～4”；FG00 B05 / B13 / B19）：电网面板。
    /// - 汇总行 = 全部电网的发电 / 用电 / 需要与未接入电网的建筑数；下拉框选一个电网（或“未接入电网的建筑”）。
    /// - 选中电网：读数（与悬停同一来源）+ 曲线（发电粗实线、需要虚线、实际用电细线、储能方块，按游戏时间每 power.sample_seconds 秒一个点）+ 用电建筑列表（按供电先后）。
    /// - 选一座用电建筑：改优先级（下拉框选中即生效，UI Toolkit 红线 8）、关停 / 重新启用、定位；“定位”电网跳到它的第一个节点；开关电力覆盖叠加层。
    /// 入口：Alt+G（可重绑）、经济 HUD 的“电网”按钮。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 秒（真实时间）按内核版本号刷新，O(选中电网)。
    /// </summary>
    public sealed class PowerPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>管线面板 30048 之上、字幕 / 战略地图 30050 之下，暂停菜单 30070 之下。</summary>
        public const int Order = 30049;

        private const float RefreshSeconds = 0.25f;

        public static PowerPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _close;
        private Label _summary;
        private DropdownField _grid;
        private Button _locateGrid;
        private Button _overlay;
        private Label _detail;
        private VisualElement _curveBox;
        private Label _curveTitle;
        private VisualElement _curve;
        private Label _curveLegend;
        private VisualElement _membersBox;
        private Label _membersTitle;
        private Label _members;
        private DropdownField _member;
        private DropdownField _priority;
        private Button _shutdown;
        private Button _locateMember;
        private Label _message;
        private Label _hint;
        private float _timer;
        private int _drawnState = -1;
        private int _drawnTopology = -1;
        private int _drawnSample = -1;
        private PowerKernel _drawnKernel;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly List<int> _subnets = new List<int>(8);
        private readonly List<int> _gridSerials = new List<int>(8);
        private readonly List<BuildingRecord> _memberRecords = new List<BuildingRecord>(16);
        private readonly List<string> _choices = new List<string>(16);

        /// <summary>选中的电网编号（0 = “未接入电网的建筑”）。</summary>
        public int SelectedSerial { get; private set; } = -1;
        /// <summary>选中的用电建筑。</summary>
        public string SelectedBuildingId { get; private set; }

        protected override string UxmlLocation => "PowerPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public string MembersText => _members?.text ?? string.Empty;
        public string MessageText => _message?.text ?? string.Empty;
        public string CurveTitleText => _curveTitle?.text ?? string.Empty;
        public DropdownField GridField => _grid;
        public DropdownField MemberField => _member;
        public DropdownField PriorityField => _priority;
        public Button ShutdownButton => _shutdown;
        public Button LocateGridButton => _locateGrid;
        public Button LocateMemberButton => _locateMember;
        public Button OverlayButton => _overlay;
        public Button CloseButton => _close;
        /// <summary>最近一次画曲线时画了几个点（自检读：曲线真的按数据画出来了）。</summary>
        public int CurvePointsDrawn { get; private set; }
        public int CurveDrawCount { get; private set; }
        /// <summary>真正执行（没有早退）的刷新次数（自检读：面板开着时随采样刷新）。</summary>
        public int RefreshCount { get; private set; }

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

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open();
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("PowerPanelRoot");
            _title = root.Q<Label>("PowerPanelTitle");
            _close = root.Q<Button>("PowerPanelClose");
            _summary = root.Q<Label>("PwSummary");
            _grid = root.Q<DropdownField>("PwGrid");
            _locateGrid = root.Q<Button>("PwLocateGrid");
            _overlay = root.Q<Button>("PwOverlay");
            _detail = root.Q<Label>("PwDetail");
            _curveBox = root.Q<VisualElement>("PwCurveBox");
            _curveTitle = root.Q<Label>("PwCurveTitle");
            _curve = root.Q<VisualElement>("PwCurve");
            _curveLegend = root.Q<Label>("PwCurveLegend");
            _membersBox = root.Q<VisualElement>("PwMembersBox");
            _membersTitle = root.Q<Label>("PwMembersTitle");
            _members = root.Q<Label>("PwMembers");
            _member = root.Q<DropdownField>("PwMember");
            _priority = root.Q<DropdownField>("PwPriority");
            _shutdown = root.Q<Button>("PwShutdown");
            _locateMember = root.Q<Button>("PwLocateMember");
            _message = root.Q<Label>("PwMessage");
            _hint = root.Q<Label>("PowerPanelHint");
            _close.clicked += () => SetOpen(false);
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _grid.RegisterValueChangedCallback(evt =>
            {
                int i = _grid.choices.IndexOf(evt.newValue);
                if (i >= 0 && i < _gridSerials.Count)
                {
                    SelectGrid(_gridSerials[i]);
                }
            });
            _member.RegisterValueChangedCallback(evt =>
            {
                int i = _member.choices.IndexOf(evt.newValue);
                if (i >= 0 && i < _memberRecords.Count)
                {
                    SelectedBuildingId = _memberRecords[i].BuildingId;
                    Refresh(force: true);
                }
            });
            _priority.RegisterValueChangedCallback(evt =>
            {
                int i = _priority.choices.IndexOf(evt.newValue);
                if (i >= 0)
                {
                    SetPriority(i + 1);
                }
            });
            _shutdown.clicked += () => ToggleShutdown();
            _locateGrid.clicked += () => LocateGrid();
            _locateMember.clicked += () => LocateMember();
            _overlay.clicked += () => ToggleOverlay();
            _curve.generateVisualContent += DrawCurve;
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
                GuidanceHooks.Raise(GuidanceHooks.PowerPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                _message.text = string.Empty;
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh(force: false);
        }

        public void SelectGrid(int serial)
        {
            SelectedSerial = serial;
            SelectedBuildingId = null;
            Refresh(force: true);
        }

        /// <summary>按内核读数刷新（内核的拓扑 / 状态版本没变且没有强制时什么都不做）。</summary>
        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state != null && !ReferenceEquals(HomeValleyPowerGrid.BoundState, state))
            {
                HomeValleyPowerGrid.Recompute(state);
            }
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            if (k == null)
            {
                return;
            }
            // 曲线采样（Sample 只让 SampleCount +1、不动 StateVersion）也要触发刷新：没有储能、拓扑不变时面板开着也要看到新点。
            if (!force && ReferenceEquals(k, _drawnKernel) && k.StateVersion == _drawnState && k.TopologyVersion == _drawnTopology
                && k.SampleCount == _drawnSample)
            {
                return;
            }
            _drawnKernel = k;
            _drawnState = k.StateVersion;
            _drawnTopology = k.TopologyVersion;
            _drawnSample = k.SampleCount;
            RefreshCount++;

            _title.text = GameText.Get("power.panel.title");
            _close.text = GameText.Get("power.panel.close");
            _hint.text = GameText.Format("power.panel.hint", InputDisplay.ForAction(GameActionId.OpenPowerGrid));
            _locateGrid.text = GameText.Get("power.panel.locate");
            _locateMember.text = GameText.Get("power.panel.locate");
            _overlay.text = GameText.Get(GameLogic.View.PowerCoverageOverlayView.Enabled ? "power.panel.overlay_off" : "power.panel.overlay_on");
            _curveLegend.text = GameText.Get("power.panel.curve_legend");

            // 汇总与电网下拉框。
            HomeValleyPowerGrid.SubnetsBySerial(_subnets);
            float supply = 0f, used = 0f, need = 0f;
            foreach (int s in _subnets)
            {
                PowerSubnetInfo n = k.Subnet(s);
                supply += n.Supply;
                used += n.Delivered;
                need += n.Demand;
            }
            var unconnected = new List<BuildingRecord>();
            HomeValleyPowerGrid.UnconnectedBuildings(unconnected);
            _summary.text = _subnets.Count == 0
                ? GameText.Get("power.panel.no_grid")
                : GameText.Format("power.panel.summary", _subnets.Count, HomeValleyPowerGrid.Num(supply), HomeValleyPowerGrid.Num(used), HomeValleyPowerGrid.Num(need), unconnected.Count);
            _choices.Clear();
            _gridSerials.Clear();
            foreach (int s in _subnets)
            {
                PowerSubnetInfo n = k.Subnet(s);
                string status = n.Brownouts > 0 ? GameText.Format("power.panel.grid_short", HomeValleyPowerGrid.Num(n.Demand - n.Delivered))
                    : n.Consumers == 0 ? GameText.Get("power.panel.grid_idle")
                    : n.Supply <= 0f ? GameText.Get("power.panel.grid_nosupply")
                    : GameText.Get("power.panel.grid_ok");
                _choices.Add(GameText.Format("power.panel.grid_row", HomeValleyPowerGrid.SubnetName(n.Serial), HomeValleyPowerGrid.Num(n.Supply),
                    HomeValleyPowerGrid.Num(n.Delivered), HomeValleyPowerGrid.Num(n.Demand),
                    (n.Stored / 60.0).ToString("0.#", CultureInfo.InvariantCulture), (n.StorageCapacity / 60.0).ToString("0.#", CultureInfo.InvariantCulture), status));
                _gridSerials.Add(n.Serial);
            }
            if (unconnected.Count > 0)
            {
                _choices.Add(GameText.Format("power.panel.unconnected", unconnected.Count));
                _gridSerials.Add(0);
            }
            if (SelectedSerial < 0 || !_gridSerials.Contains(SelectedSerial))
            {
                SelectedSerial = _gridSerials.Count > 0 ? _gridSerials[0] : -1;
            }
            DropdownChoices.Apply(_grid, new List<string>(_choices), GameText.Get("power.panel.no_grid"));
            int gi = _gridSerials.IndexOf(SelectedSerial);
            if (gi >= 0)
            {
                _grid.SetValueWithoutNotify(_grid.choices[gi]);
            }

            int subnet = SelectedSerial > 0 ? k.SubnetIndexOfSerial(SelectedSerial) : -1;
            _locateGrid.SetEnabled(subnet >= 0);
            _curveBox.EnableInClassList("bn-hidden", subnet < 0);
            if (subnet >= 0)
            {
                _sb.Clear();
                HomeValleyPowerGrid.AppendSubnetLines(_sb, subnet);
                _detail.text = _sb.ToString();
                float minutes = HomeValleyPowerGrid.SampleSeconds * HomeValleyPowerGrid.Kernel.CurveCapacity / 60f;
                bool hasCurve = k.TryGetCurve(SelectedSerial, out PowerCurve curve) && curve.Count > 0;
                _curveTitle.text = hasCurve
                    ? GameText.Format("power.panel.curve_title", HomeValleyPowerGrid.SubnetName(SelectedSerial), minutes.ToString("0", CultureInfo.InvariantCulture))
                    : GameText.Format("power.panel.curve_empty", HomeValleyPowerGrid.SampleSeconds.ToString("0", CultureInfo.InvariantCulture));
                _membersTitle.text = GameText.Format("power.panel.members", HomeValleyPowerGrid.SubnetName(SelectedSerial));
                HomeValleyPowerGrid.ConsumersOf(subnet, _memberRecords);
            }
            else
            {
                _detail.text = SelectedSerial == 0 ? GameText.Get("power.state.unconnected") : string.Empty;
                _membersTitle.text = GameText.Format("power.panel.unconnected", unconnected.Count);
                _memberRecords.Clear();
                _memberRecords.AddRange(unconnected);
            }
            _curve.MarkDirtyRepaint();
            RefreshMembers(state);
        }

        private void RefreshMembers(CampaignState state)
        {
            _sb.Clear();
            _choices.Clear();
            foreach (BuildingRecord b in _memberRecords)
            {
                string label = MemberLabel(b);
                _choices.Add(label);
                if (_sb.Length > 0)
                {
                    _sb.Append('\n');
                }
                _sb.Append(GameText.Format("power.panel.member_row", label,
                    HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) p) ? HomeValleyPowerGrid.Num(p.PowerDemand) : "0",
                    StateText(b)));
            }
            _members.text = _memberRecords.Count == 0 ? GameText.Get("power.panel.no_members") : _sb.ToString();
            if (SelectedBuildingId == null || _memberRecords.FindIndex(r => r.BuildingId == SelectedBuildingId) < 0)
            {
                SelectedBuildingId = _memberRecords.Count > 0 ? _memberRecords[0].BuildingId : null;
            }
            DropdownChoices.Apply(_member, new List<string>(_choices), GameText.Get("power.panel.no_members"));
            int mi = _memberRecords.FindIndex(r => r.BuildingId == SelectedBuildingId);
            if (mi >= 0)
            {
                _member.SetValueWithoutNotify(_member.choices[mi]);
            }
            var prios = new List<string>(4);
            for (int p = 1; p <= 4; p++)
            {
                prios.Add(GameText.Format("power.panel.priority_item", p));
            }
            DropdownChoices.Apply(_priority, prios, prios[0]);
            BuildingRecord sel = mi >= 0 ? _memberRecords[mi] : null;
            bool consumer = sel != null && HomeValleyLayout.PowerProfile.ContainsKey(sel.BuildingTypeId);
            _priority.SetEnabled(consumer);
            if (consumer)
            {
                _priority.SetValueWithoutNotify(_priority.choices[Mathf.Clamp(sel.PowerPriority - 1, 0, 3)]);
            }
            bool canToggle = consumer && sel.BuildingTypeId != HomeValleyLayout.BuildingTypeCore
                             && (sel.ConstructionState == BuildingConstructionState.Operational || sel.ConstructionState == BuildingConstructionState.Disabled);
            _shutdown.SetEnabled(canToggle);
            _shutdown.text = GameText.Get(sel != null && sel.ConstructionState == BuildingConstructionState.Disabled ? "power.panel.restart" : "power.panel.shutdown");
            _locateMember.SetEnabled(sel != null);
        }

        private static string MemberLabel(BuildingRecord b) =>
            GameText.Format("power.panel.member_label", Campaign.Feedback.FeedbackCues.BuildingLabel(b.BuildingId),
                b.GridX.ToString(CultureInfo.InvariantCulture), b.GridY.ToString(CultureInfo.InvariantCulture));

        private static string StateText(BuildingRecord b)
        {
            if (b.ConstructionState == BuildingConstructionState.Disabled)
            {
                return GameText.Get("power.panel.state_disabled");
            }
            switch (b.PowerState)
            {
                case BuildingPowerState.Powered:
                    return GameText.Get("power.state.powered") + " · " + GameText.Format("power.panel.priority_item", b.PowerPriority);
                case BuildingPowerState.Brownout:
                    return GameText.Format("power.state.brownout", b.PowerPriority);
                case BuildingPowerState.Unpowered:
                    return GameText.Get("power.subnet.none");
                default:
                    return HomeValleyLayout.PowerSupplyProfile.ContainsKey(b.BuildingTypeId) ? GameText.Get("power.state.producer_unconnected") : string.Empty;
            }
        }

        // ── 操作（控件与自检同一入口）──────────────────────────────────────────

        public bool SetPriority(int priority)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = FindSelected(state);
            if (b == null)
            {
                return Report(false, GameText.Get("power.reason.not_found"));
            }
            HomeValleyPowerGrid.GridResult r = HomeValleyPowerGrid.TrySetPriority(state, b.BuildingId, priority);
            return Report(r.Success, r.Success ? GameText.Format("power.panel.changed", MemberLabel(b), priority) : ReasonText(r.FailureReason));
        }

        public bool ToggleShutdown()
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = FindSelected(state);
            if (b == null)
            {
                return Report(false, GameText.Get("power.reason.not_found"));
            }
            HomeValleyPowerGrid.GridResult r = HomeValleyPowerGrid.TryToggleShutdown(state, b.BuildingId);
            string now = b.ConstructionState == BuildingConstructionState.Disabled ? GameText.Get("power.panel.state_disabled") : StateText(b);
            return Report(r.Success, r.Success ? GameText.Format("power.panel.toggled", MemberLabel(b), now) : ReasonText(r.FailureReason));
        }

        /// <summary>开关电力覆盖叠加层（按钮与自检同一入口）。返回开关后的状态。</summary>
        public bool ToggleOverlay()
        {
            GameLogic.View.PowerCoverageOverlayView.Toggle();
            Refresh(force: true);
            return GameLogic.View.PowerCoverageOverlayView.Enabled;
        }

        public bool LocateGrid()
        {
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            int s = k != null && SelectedSerial > 0 ? k.SubnetIndexOfSerial(SelectedSerial) : -1;
            if (s < 0)
            {
                return false;
            }
            return WorldView.FlyTo(HomeValleyLayout.RegionId, HomeValleyPowerGrid.SubnetAnchorPosition(s));
        }

        public bool LocateMember()
        {
            BuildingRecord b = FindSelected(CampaignSession.Current);
            return b != null && WorldView.FlyTo(HomeValleyLayout.RegionId, b.Position);
        }

        private BuildingRecord FindSelected(CampaignState state)
        {
            if (state == null || SelectedBuildingId == null)
            {
                return null;
            }
            foreach (BuildingRecord b in state.BuildingRecords ?? System.Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingId == SelectedBuildingId)
                {
                    return b;
                }
            }
            return null;
        }

        private static string ReasonText(string failure)
        {
            if (failure == null)
            {
                return string.Empty;
            }
            if (failure.StartsWith("priority-out-of-range", System.StringComparison.Ordinal))
            {
                return GameText.Get("power.reason.priority");
            }
            if (failure.StartsWith("not-a-power-consumer", System.StringComparison.Ordinal))
            {
                return GameText.Get("power.reason.not_consumer");
            }
            if (failure.StartsWith("building-not-found", System.StringComparison.Ordinal))
            {
                return GameText.Get("power.reason.not_found");
            }
            if (failure == "core-cannot-be-shutdown")
            {
                return GameText.Get("power.reason.core");
            }
            return GameText.Get("power.reason.busy");
        }

        private bool Report(bool ok, string message)
        {
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
            Refresh(force: true);
            return ok;
        }

        // ── 曲线（generateVisualContent，按数据画；颜色之外用线型区分：发电粗实线、需要虚线、实际用电细线、储能方块，B15）──

        private void DrawCurve(MeshGenerationContext ctx)
        {
            CurveDrawCount++;
            CurvePointsDrawn = 0;
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            if (k == null || SelectedSerial <= 0 || !k.TryGetCurve(SelectedSerial, out PowerCurve c) || c.Count == 0)
            {
                return;
            }
            Rect r = _curve.contentRect;
            if (r.width < 8f || r.height < 8f)
            {
                return;
            }
            float max = 1f;
            float maxStored = 0f;
            for (int i = 0; i < c.Count; i++)
            {
                c.Get(i, out float a, out float b, out float d, out float st);
                max = Mathf.Max(max, Mathf.Max(a, Mathf.Max(b, d)));
                maxStored = Mathf.Max(maxStored, st);
            }
            max *= 1.1f;
            int cap = c.Capacity;
            float step = r.width / Mathf.Max(1, cap - 1);
            float x0 = r.width - step * (c.Count - 1);
            Painter2D p = ctx.painter2D;
            Line(p, c, 0, max, x0, step, r.height, new Color(0.35f, 0.85f, 0.4f), 2.5f, dashed: false);
            Line(p, c, 1, max, x0, step, r.height, new Color(0.95f, 0.6f, 0.2f), 1.5f, dashed: true);
            Line(p, c, 2, max, x0, step, r.height, new Color(0.35f, 0.8f, 0.95f), 1f, dashed: false);
            if (maxStored > 0f)
            {
                p.fillColor = new Color(0.7f, 0.5f, 0.95f, 0.9f);
                for (int i = 0; i < c.Count; i++)
                {
                    c.Get(i, out _, out _, out _, out float st);
                    float x = x0 + step * i;
                    float y = r.height - st / (maxStored * 1.1f) * r.height;
                    p.BeginPath();
                    p.MoveTo(new Vector2(x - 2f, y - 2f));
                    p.LineTo(new Vector2(x + 2f, y - 2f));
                    p.LineTo(new Vector2(x + 2f, y + 2f));
                    p.LineTo(new Vector2(x - 2f, y + 2f));
                    p.ClosePath();
                    p.Fill();
                }
            }
            CurvePointsDrawn = c.Count;
        }

        private static void Line(Painter2D p, PowerCurve c, int which, float max, float x0, float step, float h, Color color, float width, bool dashed)
        {
            if (c.Count < 2)
            {
                return;
            }
            p.strokeColor = color;
            p.lineWidth = width;
            Vector2 prev = default;
            for (int i = 0; i < c.Count; i++)
            {
                c.Get(i, out float a, out float b, out float d, out _);
                float v = which == 0 ? a : which == 1 ? b : d;
                var pt = new Vector2(x0 + step * i, h - Mathf.Clamp01(v / max) * h);
                if (i > 0 && (!dashed || i % 2 == 1))
                {
                    p.BeginPath();
                    p.MoveTo(prev);
                    p.LineTo(pt);
                    p.Stroke();
                }
                prev = pt;
            }
        }
    }
}
