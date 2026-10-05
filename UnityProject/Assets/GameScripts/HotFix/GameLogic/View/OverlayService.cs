using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Common;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>FG3-LOG-08（FGR-LOG-080；FGU-12）：8 种叠加层。数值进存档（<see cref="GridState.OverlayActive"/>），不要改已有成员的数值。</summary>
    public enum OverlayKind : byte
    {
        None = 0,
        Flow = 1,
        Blockage = 2,
        Power = 3,
        Fluid = 4,
        Pollution = 5,
        Signal = 6,
        Raid = 7,
        Construction = 8,
    }

    /// <summary>叠加层的世界标签（HUD 每帧投影到屏幕；只有 ≤ overlay.max_labels 条，离镜头近的优先）。</summary>
    public struct OverlayLabel
    {
        public Vector3 World;
        public string Text;
        /// <summary>0 = 普通，1 = 警告（有问题的网络 / 停工的建筑），2 = 严重（堵塞源头 / 突袭）。颜色之外文字本身带“!”前缀（B15）。</summary>
        public byte Tone;
    }

    /// <summary>
    /// FG3-LOG-08（FG03 FGR-LOG-080 叠加层；FG13 FGU-12 叠加层选择器；卡片“叠加层的快捷键”）：8 种叠加层的唯一开关与表现驱动。
    /// - 同时只显示一种（选择器里点另一种 = 切换）。O = 开 / 关当前那一种（关着时重开最近用过的，从没用过是“信号覆盖”，与 FG1-SIG-07 一致）；
    ///   Alt+O 选择器；Ctrl+Alt+1～8 直达。当前与最近的叠加层跟着存档走。
    /// - 电力覆盖、信号覆盖沿用 <see cref="PowerCoverageOverlayView"/> / <see cref="SignalCoverageOverlayView"/>（这里只开关它们）；
    ///   物品流向与吞吐、堵塞只改传送带着色器参数（<see cref="BeltOverlayMode"/>，CPU 与格数无关）；污染改地形贴图的画法（<see cref="WorldTerrainOverlay.PollutionView"/>，区块重画在 Burst 工作线程）；
    ///   流体网络、突袭路径、施工状态、堵塞的停工标记由这里画（线 / 标记 / 标签，数量有上限、只在数据变化或每 overlay.refresh_seconds 真实秒重建一次）。
    /// - 每帧 O(1)（不在刷新点时只比较版本号）；刷新点 O(网络数 + 施工单数 + 停工建筑数)，逐格循环都在 AOT 内核（锚点、堵塞源头）。
    /// 纯表现：不改任何模拟状态；家园不被观察时不画（模拟照常，结果与观察时一致）。
    /// </summary>
    public static class OverlayService
    {
        public const int KindCount = 8;

        private static OverlayKind _active;
        private static OverlayKind _last = OverlayKind.Signal;
        private static CampaignState _bound;
        private static float _nextRefresh;
        private static OverlayKind _drawnKind = OverlayKind.None;
        private static int _drawnDiagRevision = -1;
        private static GameObject _root;
        private static readonly List<LineRenderer> Lines = new List<LineRenderer>(32);
        private static readonly List<WorldBadge> Badges = new List<WorldBadge>(32);
        private static readonly List<OverlayLabel> LabelList = new List<OverlayLabel>(64);
        private static readonly List<OverlayLabel> Candidates = new List<OverlayLabel>(128);
        private static readonly List<Unity.Mathematics.int3> Anchors = new List<Unity.Mathematics.int3>(32);
        private static readonly List<HomeValleyConstruction.QueueEntry> Queue = new List<HomeValleyConstruction.QueueEntry>(32);
        private static readonly Vector3[] Square = new Vector3[5];

        public static OverlayKind Active => _active;
        /// <summary>最近一次打开的叠加层（O 键关着时按它重开）。</summary>
        public static OverlayKind Last => _last;
        /// <summary>开关变化时 +1（HUD 按它刷新按钮与图例）。</summary>
        public static int Revision { get; private set; }
        /// <summary>标签内容变化时 +1。</summary>
        public static int LabelRevision { get; private set; }
        public static IReadOnlyList<OverlayLabel> Labels => LabelList;
        public static int RedrawCount { get; private set; }
        public static int DrawnLines { get; private set; }
        public static int DrawnMarkers { get; private set; }
        public static int DrawnBadges { get; private set; }
        public static double LastRedrawMs { get; private set; }
        /// <summary>本帧叠加层有没有在画（家园被观察、叠加层开着）。</summary>
        public static bool Visible { get; private set; }

        private static float RefreshSeconds => Mathf.Max(0.1f, GridContent.Tuning("overlay.refresh_seconds"));
        private static int MaxLabels => Math.Max(4, GridContent.TuningInt("overlay.max_labels"));
        private static int MaxMarkers => Math.Max(8, GridContent.TuningInt("overlay.max_markers"));
        private static float RaidOutpostRadius => Mathf.Max(16f, GridContent.Tuning("overlay.raid_outpost_radius"));

        /// <summary>传送带着色器的叠加层画法：1 = 物品流向与吞吐，2 = 堵塞，3 = 压暗（流体网络开着时让管线突出），0 = 普通。</summary>
        public static int BeltOverlayMode =>
            _active == OverlayKind.Flow ? 1 : _active == OverlayKind.Blockage ? 2 : _active == OverlayKind.Fluid ? 3 : 0;

        // ── 名字、按键 ──────────────────────────────────────────────────────────

        public static string Slug(OverlayKind k)
        {
            switch (k)
            {
                case OverlayKind.Flow: return "flow";
                case OverlayKind.Blockage: return "blockage";
                case OverlayKind.Power: return "power";
                case OverlayKind.Fluid: return "fluid";
                case OverlayKind.Pollution: return "pollution";
                case OverlayKind.Signal: return "signal";
                case OverlayKind.Raid: return "raid";
                case OverlayKind.Construction: return "construction";
                default: return "none";
            }
        }

        public static string Name(OverlayKind k) => GameText.Get("overlay.name." + Slug(k));
        public static string Legend(OverlayKind k) =>
            k == OverlayKind.Raid && SiegeActive ? GameText.Get("overlay.legend.raid") + "\n" + GameText.Get("overlay.label.siege_legend") : GameText.Get("overlay.legend." + Slug(k));

        /// <summary>FG6-DEF-05：家园正在被攻城（突袭路径叠加层的图例多一行职能图标与破墙框说明）。</summary>
        private static bool SiegeActive => Campaign.Defense.SiegeService.StateOf(CampaignSession.Current)?.TheaterActive ?? false;

        /// <summary>8 种叠加层各自的直达动作（下标 = 叠加层 − 1；默认 Ctrl+Alt+1～8，可重绑）。</summary>
        private static readonly GameActionId[] DirectActions =
        {
            GameActionId.OverlayFlow, GameActionId.OverlayBlockage, GameActionId.OverlayPower, GameActionId.OverlayFluid,
            GameActionId.OverlayPollution, GameActionId.OverlaySignal, GameActionId.OverlayRaid, GameActionId.OverlayConstruction,
        };

        public static GameActionId ActionOf(OverlayKind k) => k >= OverlayKind.Flow && (int)k <= KindCount ? DirectActions[(int)k - 1] : GameActionId.ToggleOverlay;

        public static bool TryKindOf(GameActionId a, out OverlayKind k)
        {
            int i = Array.IndexOf(DirectActions, a);
            k = i >= 0 ? (OverlayKind)(i + 1) : OverlayKind.None;
            return i >= 0;
        }

        // ── 开关 ────────────────────────────────────────────────────────────────

        /// <summary>打开 <paramref name="k"/>（None = 关掉）。同时只显示一种。写进当前战役（跟着存档走）。</summary>
        public static void Set(OverlayKind k)
        {
            if ((int)k < 0 || (int)k > KindCount)
            {
                k = OverlayKind.None;
            }
            Reconcile();
            if (k == _active)
            {
                return;
            }
            _active = k;
            if (k != OverlayKind.None)
            {
                _last = k;
                GuidanceHooks.Raise(GuidanceHooks.OverlayFirstOpen);
            }
            ApplyViews();
            Persist();
            _nextRefresh = 0f;
            Revision++;
        }

        /// <summary>选择器按钮 / Ctrl+Alt+数字：这一种开着就关掉，否则切到这一种。</summary>
        public static void Toggle(OverlayKind k)
        {
            Reconcile();
            Set(_active == k ? OverlayKind.None : k);
        }

        /// <summary>
        /// O 键（FG13 第 5 节“叠加层切换”）：开着就关；关着就重开最近用过的那一种。
        /// 家园以外只有“信号覆盖”画得出来：最近用过的那一种只在家园显示时，O 开关的是信号覆盖（与 FG1-SIG-07 的 O 键在远征地点的行为一致，不开一个看不见的叠加层）。
        /// </summary>
        public static void ToggleCurrent()
        {
            Reconcile();
            if (_active != OverlayKind.None)
            {
                Set(OverlayKind.None);
                return;
            }
            OverlayKind want = _last == OverlayKind.None ? OverlayKind.Signal : _last;
            Set(WorksHere(want) ? want : OverlayKind.Signal);
        }

        /// <summary>
        /// 直达键（Ctrl+Alt+1～8）：同 <see cref="Toggle"/>；但在家园以外要打开一种只在家园显示的叠加层时不切换，发一条说明（写明这里可用的信号覆盖键），不静默失效。
        /// 返回有没有切换。
        /// </summary>
        public static bool ToggleFromKey(OverlayKind k)
        {
            Reconcile();
            if (_active != k && !WorksHere(k))
            {
                GameLogic.Notifications.NotificationCenter.Post("overlay_home_only", GameText.Format("overlay.home_only.kind", Name(k), InputDisplay.ForAction(GameActionId.OverlaySignal)));
                RejectedCount++;
                return false;
            }
            Set(_active == k ? OverlayKind.None : k);
            return true;
        }

        /// <summary>家园以外被拒绝的叠加层 / 选择器键次数（自检读点）。</summary>
        public static int RejectedCount { get; private set; }

        /// <summary>选择器键在家园以外：发说明（自检读点 <see cref="RejectedCount"/>）。</summary>
        public static void NotifySelectorHomeOnly()
        {
            GameLogic.Notifications.NotificationCenter.Post("overlay_home_only", GameText.Format("overlay.home_only.selector", InputDisplay.ForAction(GameActionId.OverlaySignal)));
            RejectedCount++;
        }

        /// <summary>家园此刻被观察（载入且镜头在家园）：只在家园显示的 7 种叠加层这时才画得出来。</summary>
        public static bool HomeObserved
        {
            get
            {
                HomeValleyController home = WorldSimulation.Home;
                return CampaignSession.Current != null && home != null && home.IsLoaded && WorldView.IsObserved(HomeValleyLayout.RegionId);
            }
        }

        /// <summary>这一种叠加层在当前地点画不画得出来：信号覆盖到处都画；其余 7 种只在家园。</summary>
        public static bool WorksHere(OverlayKind k) => k == OverlayKind.None || k == OverlayKind.Signal || HomeObserved;

        private static void ApplyViews()
        {
            SignalCoverageOverlayView.SetEnabled(_active == OverlayKind.Signal);
            PowerCoverageOverlayView.SetEnabled(_active == OverlayKind.Power);
        }

        private static void Persist()
        {
            CampaignState s = CampaignSession.Current;
            if (s?.Grid == null)
            {
                return;
            }
            s.Grid.OverlayActive = (int)_active;
            s.Grid.OverlayLast = (int)_last;
        }

        /// <summary>绑定战役：按存档恢复当前 / 最近的叠加层（读档、新战役、换存档时各一次）。</summary>
        public static void Bind(CampaignState state)
        {
            _bound = state;
            int a = state?.Grid?.OverlayActive ?? 0;
            int l = state?.Grid?.OverlayLast ?? 0;
            OverlayKind before = _active;
            _active = a >= 0 && a <= KindCount ? (OverlayKind)a : OverlayKind.None;
            _last = l >= 1 && l <= KindCount ? (OverlayKind)l : OverlayKind.Signal;
            // 只在存档里开着电力 / 信号覆盖，或者之前是本服务打开的它们时才去开关视图——别处（电网面板、旧自检）直接打开的视图交给 Reconcile 同步，不在换战役时硬关。
            if (_active == OverlayKind.Signal || _active == OverlayKind.Power || before == OverlayKind.Signal || before == OverlayKind.Power)
            {
                ApplyViews();
            }
            _nextRefresh = 0f;
            Revision++;
        }

        public static void ResetForTests()
        {
            _bound = null;
            _active = OverlayKind.None;
            _last = OverlayKind.Signal;
            _drawnKind = OverlayKind.None;
            _drawnDiagRevision = -1;
            HideAll();
            LabelList.Clear();
            LabelRevision++;
            Revision++;
        }

        // ── 每帧 ────────────────────────────────────────────────────────────────

        /// <summary>每帧（世界模拟推进之后，<see cref="WorldSimulation"/> 调用）。平时 O(1)；到刷新点才重建线、标记与标签。</summary>
        public static void FrameTick()
        {
            CampaignState state = CampaignSession.Current;
            if (!ReferenceEquals(state, _bound))
            {
                Bind(state);
            }
            Reconcile();
            HomeValleyController home = WorldSimulation.Home;
            bool observed = state != null && home != null && home.IsLoaded && WorldView.IsObserved(HomeValleyLayout.RegionId);
            bool own = _active == OverlayKind.Flow || _active == OverlayKind.Blockage || _active == OverlayKind.Fluid
                       || _active == OverlayKind.Raid || _active == OverlayKind.Construction;
            Visible = observed && _active != OverlayKind.None;
            if (!observed || !own)
            {
                if (_drawnKind != OverlayKind.None)
                {
                    HideAll();
                    ClearLabels();
                    _drawnKind = OverlayKind.None;
                }
                return;
            }
            float now = Time.realtimeSinceStartup;
            bool diagChanged = false;
            if (_active == OverlayKind.Blockage)
            {
                RootCauseDiagnosis.Refresh(state);
                diagChanged = RootCauseDiagnosis.Revision != _drawnDiagRevision;
            }
            if (_drawnKind == _active && !diagChanged && now < _nextRefresh)
            {
                return;
            }
            _nextRefresh = now + RefreshSeconds;
            Redraw(state, _active);
        }

        /// <summary>自检直接调：立刻按当前状态重建（不等刷新点）。</summary>
        public static void RedrawNow()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || _active == OverlayKind.None)
            {
                return;
            }
            if (_active == OverlayKind.Blockage)
            {
                RootCauseDiagnosis.Refresh(state, force: true);
            }
            Redraw(state, _active);
        }

        /// <summary>别处直接开关了电力 / 信号覆盖视图（电网面板按钮、旧自检）：以视图为准同步，保持“同时只显示一种”。</summary>
        private static void Reconcile()
        {
            bool sig = SignalCoverageOverlayView.Enabled;
            bool pow = PowerCoverageOverlayView.Enabled;
            OverlayKind want = _active;
            if (_active == OverlayKind.Signal && !sig || _active == OverlayKind.Power && !pow)
            {
                want = OverlayKind.None;
            }
            if (sig && _active != OverlayKind.Signal)
            {
                want = OverlayKind.Signal;
            }
            else if (pow && _active != OverlayKind.Power)
            {
                want = OverlayKind.Power;
            }
            if (want != _active)
            {
                _active = want;
                if (want != OverlayKind.None)
                {
                    _last = want;
                }
                if (want == OverlayKind.Signal && pow)
                {
                    PowerCoverageOverlayView.SetEnabled(false);
                }
                Persist();
                Revision++;
            }
        }

        private static void Redraw(CampaignState state, OverlayKind kind)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RedrawCount++;
            _drawnKind = kind;
            EnsureRoot();
            Candidates.Clear();
            int lines = 0;
            int markers = 0;
            int badges = 0;
            switch (kind)
            {
                case OverlayKind.Flow:
                    CollectFlowLabels(state);
                    break;
                case OverlayKind.Blockage:
                    _drawnDiagRevision = RootCauseDiagnosis.Revision;
                    badges = DrawBlockage();
                    break;
                case OverlayKind.Fluid:
                    CollectFluidLabels();
                    break;
                case OverlayKind.Raid:
                    lines = DrawRaids(state);
                    break;
                case OverlayKind.Construction:
                    markers = DrawConstruction(state);
                    break;
            }
            for (int i = lines + markers; i < Lines.Count; i++)
            {
                if (Lines[i] != null && Lines[i].enabled)
                {
                    Lines[i].enabled = false;
                }
            }
            for (int i = badges; i < Badges.Count; i++)
            {
                Badges[i]?.SetVisible(false);
            }
            DrawnLines = lines;
            DrawnMarkers = markers;
            DrawnBadges = badges;
            PickLabels();
            LastRedrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // ── 物品流向与吞吐：每个传送带网络一个标签 ─────────────────────────────

        private static void CollectFlowLabels(CampaignState state)
        {
            if (!BeltNetworkService.IsRunning || !ReferenceEquals(BeltNetworkService.BoundState, state))
            {
                return;
            }
            BeltKernel k = BeltNetworkService.Kernel;
            k.CollectNetworkAnchors(Anchors);
            foreach (Unity.Mathematics.int3 a in Anchors)
            {
                if (!k.TryGetNetworkStats(a.z, out BeltNetworkStats st))
                {
                    continue;
                }
                string name = GameText.Format("diag.subject.belt_net", a.z + 1);
                string text = st.WindowSeconds > 0f
                    ? GameText.Format("overlay.label.flow", name, RootCauseDiagnosis.FormatNumber(st.DeliveredPerMinute), RootCauseDiagnosis.FormatNumber(st.EmittedPerMinute), st.Items)
                    : GameText.Format("overlay.label.flow_measuring", name, st.Items);
                if (st.BlockedCells > 0)
                {
                    text += GameText.Format("overlay.label.flow_blocked", st.BlockedCells);
                }
                Candidates.Add(new OverlayLabel { World = new Vector3(a.x, 0.6f, a.y), Text = text, Tone = (byte)(st.BlockedCells > 0 ? 1 : 0) });
            }
        }

        // ── 堵塞：停工建筑头顶标记 + 主因标签（与“为什么不工作”同一份诊断）──────

        private static int DrawBlockage()
        {
            int badges = 0;
            int cap = MaxMarkers;
            foreach (DiagReport r in RootCauseDiagnosis.Reports)
            {
                DiagChain p = r.Primary;
                if (p == null)
                {
                    continue;
                }
                bool severe = p.Category == DiagCategory.Output || r.Subject == DiagSubject.BeltNetwork;
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3(r.Position.x, 3.2f, r.Position.y),
                    Text = GameText.Format("overlay.label.stopped", r.Name, p.Root?.Text ?? string.Empty),
                    Tone = (byte)(severe ? 2 : 1),
                });
                if (r.Subject != DiagSubject.Building || badges >= cap)
                {
                    continue;
                }
                WorldBadge badge = BadgeAt(badges++);
                badge.transform.position = new Vector3(r.Position.x, 4.6f, r.Position.y);
                badge.SetIcon(IconFor(p));
                badge.SetVisible(true);
            }
            return badges;
        }

        /// <summary>停工标记的图标（形状区分，不只靠颜色）：受损 = 叉，缺电 / 未接入 = 闪电，其余（堵塞、缺料、关停）= 堵塞方块。</summary>
        public static string IconFor(DiagChain p)
        {
            DiagCode c = p.Symptom?.Code ?? DiagCode.OutputBlocked;
            if (c == DiagCode.Damaged)
            {
                return ContentIcons.StateDamaged;
            }
            if (p.Category == DiagCategory.Power)
            {
                return c == DiagCode.Unconnected ? ContentIcons.StateUnpowered : ContentIcons.StateBrownout;
            }
            return ContentIcons.StateBlocked;
        }

        // ── 流体网络：每个网络一个标签（有问题的标“!”）────────────────────────

        private static void CollectFluidLabels()
        {
            if (!PipeNetworkService.IsRunning)
            {
                return;
            }
            PipeKernel k = PipeNetworkService.Kernel;
            k.CollectNetworkAnchors(Anchors);
            foreach (Unity.Mathematics.int3 a in Anchors)
            {
                if (!k.TryGetNetworkInfo(a.z, out PipeNetInfo n))
                {
                    continue;
                }
                bool bad = (n.Issues & (PipeNetIssue.NoSupply | PipeNetIssue.Shortage | PipeNetIssue.PipeLimited | PipeNetIssue.NoSource)) != 0;
                string name = GameText.Format("diag.subject.pipe_net", PipeNetworkService.FluidName(n.Fluid), n.Cells);
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3(a.x, 0.8f, a.y),
                    Text = GameText.Format("overlay.label.fluid", name, RootCauseDiagnosis.FormatNumber(n.SupplyLpm), RootCauseDiagnosis.FormatNumber(n.DemandLpm),
                        PipeNetworkService.DescribeState(n).Replace("\n", "；")),
                    Tone = (byte)(bad ? 1 : 0),
                });
            }
        }

        // ── 突袭路径：行进中的突袭路线（粗红）+ 附近据点到核心的可能来路（细橙）────

        private static int DrawRaids(CampaignState state)
        {
            int lines = 0;
            GridCell core = HomeGridService.CorePivot(state);
            var corePos = new Vector2(core.X, core.Y);
            foreach (TransitGroupRecord g in state.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>())
            {
                if (g == null || g.Kind != TransitGroupKind.Raid || lines >= MaxMarkers)
                {
                    continue;
                }
                LineRenderer r = LineAt(lines++);
                Style(r, new Color(0.95f, 0.2f, 0.15f, 0.95f), 1.1f, false);
                int start = Mathf.Clamp(g.RouteIndex, 0, Math.Max(0, (g.RouteX?.Length ?? 0)));
                int points = 1 + Math.Max(0, (g.RouteX?.Length ?? 0) - start);
                if (points < 2)
                {
                    r.positionCount = 2;
                    r.SetPosition(0, new Vector3((float)g.PosX, 0.35f, (float)g.PosY));
                    r.SetPosition(1, new Vector3((float)g.TargetX, 0.35f, (float)g.TargetY));
                }
                else
                {
                    r.positionCount = points;
                    r.SetPosition(0, new Vector3((float)g.PosX, 0.35f, (float)g.PosY));
                    for (int i = start; i < g.RouteX.Length; i++)
                    {
                        r.SetPosition(1 + i - start, new Vector3(g.RouteX[i], 0.35f, g.RouteY[i]));
                    }
                }
                r.enabled = true;
                float dist = Vector2.Distance(new Vector2((float)g.PosX, (float)g.PosY), corePos);
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3((float)g.PosX, 1.2f, (float)g.PosY),
                    Text = GameText.Format("overlay.label.raid", g.UnitCount, Mathf.RoundToInt(dist)),
                    Tone = 2,
                });
            }
            // FG6-DEF-04（关闭 DEBT-FG3LOG08-01）：突袭来之前的预测路线——已发预警（集结中）或有有效预报的计划，沿排定的地形路线画橙红细线（出发地 → 目标）。
            long now = GameLogic.Core.GameClock.Ticks;
            foreach (Campaign.RaidPlanRecord p in Campaign.Defense.RaidDirectorService.Plans(state))
            {
                if (p == null || lines >= MaxMarkers || (p.RouteX?.Length ?? 0) == 0)
                {
                    continue;
                }
                bool warned = p.State == Campaign.Defense.RaidDirectorService.StateWarned;
                bool forecast = p.State == Campaign.Defense.RaidDirectorService.StateScheduled && Campaign.Defense.RaidDirectorService.IntelKnown(state, p, now);
                if (!warned && !forecast)
                {
                    continue;
                }
                LineRenderer r = LineAt(lines++);
                Style(r, new Color(1f, 0.42f, 0.2f, 0.75f), 0.6f, false);
                r.positionCount = 1 + p.RouteX.Length;
                r.SetPosition(0, new Vector3(p.OriginX, 0.33f, p.OriginY));
                for (int i = 0; i < p.RouteX.Length; i++)
                {
                    r.SetPosition(1 + i, new Vector3(p.RouteX[i], 0.33f, p.RouteY[i]));
                }
                r.enabled = true;
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3(p.OriginX, 1.2f, p.OriginY),
                    Text = GameText.Format("overlay.label.raid_planned", Campaign.Defense.RaidDirectorService.Duration(p.DepartTick - now)),
                    Tone = 2,
                });
            }
            float radius = RaidOutpostRadius;
            foreach (OutpostRecord o in state.Raids?.Outposts ?? Array.Empty<OutpostRecord>())
            {
                if (o == null || o.Destroyed || lines >= MaxMarkers)
                {
                    continue;
                }
                var p = new Vector2(o.CellX, o.CellY);
                if (Vector2.Distance(p, corePos) > radius)
                {
                    continue;
                }
                LineRenderer r = LineAt(lines++);
                Style(r, new Color(0.95f, 0.6f, 0.15f, 0.8f), 0.4f, false);
                r.positionCount = 2;
                r.SetPosition(0, new Vector3(p.x, 0.3f, p.y));
                r.SetPosition(1, new Vector3(corePos.x, 0.3f, corePos.y));
                r.enabled = true;
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3(p.x, 1f, p.y),
                    Text = GameText.Format("overlay.label.outpost", Mathf.RoundToInt(Vector2.Distance(p, corePos)), o.Garrison),
                    Tone = 1,
                });
            }
            return DrawSiege(state, lines);
        }

        // ── FG6-DEF-05：攻城中的突袭——每种出场职能沿自己的流场从队伍当前位置画到目标（职能色粗线），正在被拆的墙画红粗框 + “正在拆：X” ──

        private static readonly List<Unity.Mathematics.int2> SiegePath = new List<Unity.Mathematics.int2>(256);
        private static readonly List<int> SiegeIds = new List<int>(64);

        /// <summary>自检读：最近一次重画的攻城路线条数 / 破墙框个数。</summary>
        public static int SiegeRouteLines { get; private set; }
        public static int SiegeBreachMarkers { get; private set; }

        private static int DrawSiege(CampaignState state, int lines)
        {
            SiegeRouteLines = 0;
            SiegeBreachMarkers = 0;
            HomeValleyController home = WorldSimulation.Home;
            Campaign.Combat.CombatSite site = home != null && home.IsLoaded ? home.Combat : null;
            if (site == null || !(Campaign.Defense.SiegeService.StateOf(state)?.TheaterActive ?? false))
            {
                return lines;
            }
            int cap = MaxMarkers;
            int maxPoints = Math.Max(16, GridContent.TuningInt("overlay.max_markers") * 8);
            foreach (TransitGroupRecord g in Campaign.Defense.SiegeService.SiegingGroups(state))
            {
                site.SiegeRaiderIds(Campaign.Defense.SiegeService.KeyOf(g), SiegeIds);
                for (int role = 0; role < BinGames.Sim.Combat.CombatSiegeConst.RoleCount && lines < cap; role++)
                {
                    // 每种职能取 ID 最小的那台当代表（确定性）；撤退中的单位走撤退场（role 3）。
                    int rep = -1;
                    Vector2 at = default;
                    foreach (int id in SiegeIds)
                    {
                        if ((rep < 0 || id < rep) && site.TryGetSiegeRaider(id, out Vector2 p, out BinGames.Sim.Combat.CombatSiegeUnit su, out _, out _, out _)
                            && BinGames.Sim.Combat.CombatSiegeLogic.FieldRole(su) == role)
                        {
                            rep = id;
                            at = p;
                        }
                    }
                    if (rep < 0)
                    {
                        continue;
                    }
                    var cell = new Unity.Mathematics.int2(Mathf.FloorToInt(at.x + 0.5f), Mathf.FloorToInt(at.y + 0.5f));
                    int field = role * 2;
                    if (site.SiegeDistAt(field, cell) >= BinGames.Sim.Combat.CombatSiegeConst.Inf)
                    {
                        field++; // 开路到不了：走破墙场（路线画到要拆的那段墙）
                    }
                    int n = site.TraceSiegePath(field, cell, maxPoints, SiegePath);
                    if (n < 2)
                    {
                        continue;
                    }
                    LineRenderer r = LineAt(lines++);
                    Style(r, Campaign.Defense.SiegeCatalog.RoleColor(role), role == BinGames.Sim.Combat.CombatSiegeConst.RoleRetreat ? 0.5f : 0.9f, false);
                    r.positionCount = n;
                    for (int i = 0; i < n; i++)
                    {
                        r.SetPosition(i, new Vector3(SiegePath[i].x, 0.4f, SiegePath[i].y));
                    }
                    r.enabled = true;
                    SiegeRouteLines++;
                    Candidates.Add(new OverlayLabel
                    {
                        World = new Vector3(at.x, 1.3f, at.y),
                        Text = GameText.Format("overlay.label.siege_route", Campaign.Defense.SiegeCatalog.RoleName(role < 3 ? (BinGames.Sim.Combat.CombatSiegeRole)(role + 1) : BinGames.Sim.Combat.CombatSiegeRole.None)),
                        Tone = 2,
                    });
                }
            }
            for (int i = 0; i < site.SiegeBreachCount && lines < cap; i++)
            {
                string id = Campaign.Defense.SiegeService.BuildingIdOfUnit(state, site, site.SiegeBreachAt(i));
                BuildingRecord b = id != null ? HomeGridService.FindBuilding(state, id) : null;
                if (b == null || !GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid bg))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), bg.FootprintW, bg.FootprintH, GridMath.NormalizeRotation(b.Rotation), out GridCell lo, out GridCell hi);
                LineRenderer r = LineAt(lines++);
                Style(r, new Color(1f, 0.15f, 0.1f, 1f), 0.5f, true);
                float pad = 0.75f;
                Square[0] = new Vector3(lo.X - pad, 0.45f, lo.Y - pad);
                Square[1] = new Vector3(hi.X + pad, 0.45f, lo.Y - pad);
                Square[2] = new Vector3(hi.X + pad, 0.45f, hi.Y + pad);
                Square[3] = new Vector3(lo.X - pad, 0.45f, hi.Y + pad);
                r.positionCount = 4;
                r.SetPositions(Square);
                r.enabled = true;
                SiegeBreachMarkers++;
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3((lo.X + hi.X) * 0.5f, 1.5f, (lo.Y + hi.Y) * 0.5f),
                    Text = GameText.Format("overlay.label.siege_breach", HomeGridService.DisplayName(b.BuildingTypeId)),
                    Tone = 2,
                });
            }
            return lines;
        }

        // ── 施工状态：每处施工一个方框（颜色 + 线宽区分状态）+ 标签 ─────────────

        private static int DrawConstruction(CampaignState state)
        {
            HomeValleyConstruction.CollectQueue(state, Queue);
            int markers = 0;
            int cap = MaxMarkers;
            foreach (HomeValleyConstruction.QueueEntry e in Queue)
            {
                if (markers >= cap)
                {
                    break;
                }
                SiteTone(e.Order, out Color color, out float width, out byte tone);
                LineRenderer r = LineAt(markers++);
                Style(r, color, width, true);
                float h = 1.6f;
                Square[0] = new Vector3(e.Position.x - h, 0.25f, e.Position.y - h);
                Square[1] = new Vector3(e.Position.x + h, 0.25f, e.Position.y - h);
                Square[2] = new Vector3(e.Position.x + h, 0.25f, e.Position.y + h);
                Square[3] = new Vector3(e.Position.x - h, 0.25f, e.Position.y + h);
                r.positionCount = 4;
                r.SetPositions(Square);
                r.enabled = true;
                Candidates.Add(new OverlayLabel
                {
                    World = new Vector3(e.Position.x, 1.4f, e.Position.y),
                    Text = GameText.Format("overlay.label.construction", e.Name, e.Status),
                    Tone = tone,
                });
            }
            return markers;
        }

        /// <summary>施工方框的颜色与线宽：施工中 = 绿细、等材料 = 黄粗、没有机器 / 走不到 = 橙粗、排队 / 取料途中 = 灰细、被摧毁待重建 = 红粗。</summary>
        public static void SiteTone(WorkOrderRecord o, out Color color, out float width, out byte tone)
        {
            if (o == null)
            {
                color = new Color(0.9f, 0.25f, 0.2f, 0.9f);
                width = 0.45f;
                tone = 2;
                return;
            }
            string reason = o.FailureReason;
            if (o.State == WorkOrderState.InProgress)
            {
                color = new Color(0.3f, 0.9f, 0.4f, 0.9f);
                width = 0.2f;
                tone = 0;
            }
            else if (o.State == WorkOrderState.Waiting && reason != null && reason.StartsWith(HomeValleyConstruction.MaterialsReasonPrefix, StringComparison.Ordinal))
            {
                color = new Color(0.95f, 0.85f, 0.2f, 0.95f);
                width = 0.45f;
                tone = 1;
            }
            else if (HomeValleyWorkOrders.IsUnreachableReason(reason) || reason == "path-blocked"
                     || ((o.State == WorkOrderState.Ready || o.State == WorkOrderState.Waiting) && HomeValleyConstruction.NoLabor))
            {
                color = new Color(0.95f, 0.5f, 0.15f, 0.95f);
                width = 0.45f;
                tone = 1;
            }
            else
            {
                color = new Color(0.7f, 0.7f, 0.75f, 0.8f);
                width = 0.2f;
                tone = 0;
            }
        }

        // ── 标签挑选：离镜头焦点近的优先，最多 overlay.max_labels 条 ─────────────

        private static void PickLabels()
        {
            LabelList.Clear();
            Vector2 focus = CameraFocus();
            int max = MaxLabels;
            if (Candidates.Count > max)
            {
                Candidates.Sort((a, b) =>
                {
                    float da = (new Vector2(a.World.x, a.World.z) - focus).sqrMagnitude;
                    float db = (new Vector2(b.World.x, b.World.z) - focus).sqrMagnitude;
                    return da.CompareTo(db);
                });
            }
            for (int i = 0; i < Candidates.Count && i < max; i++)
            {
                LabelList.Add(Candidates[i]);
            }
            LabelRevision++;
        }

        private static Vector2 CameraFocus()
        {
            Camera cam = WorldView.Camera != null ? WorldView.Camera : Camera.main;
            if (cam == null)
            {
                return Vector2.zero;
            }
            Vector3 o = cam.transform.position;
            Vector3 d = cam.transform.forward;
            if (Mathf.Abs(d.y) > 1e-4f)
            {
                float t = -o.y / d.y;
                if (t > 0f)
                {
                    Vector3 hit = o + d * t;
                    return new Vector2(hit.x, hit.z);
                }
            }
            return new Vector2(o.x, o.z);
        }

        private static void ClearLabels()
        {
            if (LabelList.Count > 0)
            {
                LabelList.Clear();
                LabelRevision++;
            }
        }

        // ── 对象池 ──────────────────────────────────────────────────────────────

        private static void EnsureRoot()
        {
            if (_root == null)
            {
                _root = new GameObject("LogisticsOverlay");
            }
        }

        private static LineRenderer LineAt(int i)
        {
            while (Lines.Count <= i)
            {
                Lines.Add(null);
            }
            if (Lines[i] == null)
            {
                var go = new GameObject("OverlayLine" + i);
                go.transform.SetParent(_root.transform, false);
                LineRenderer r = go.AddComponent<LineRenderer>();
                r.useWorldSpace = true;
                r.numCapVertices = 0;
                r.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 平铺在地面上（与电力 / 信号覆盖叠加层同一做法）。
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                Lines[i] = r;
            }
            return Lines[i];
        }

        private static void Style(LineRenderer r, Color color, float width, bool loop)
        {
            r.sharedMaterial = ViewMaterials.Get("Sprites/Default", color);
            r.widthMultiplier = width;
            r.loop = loop;
        }

        private static WorldBadge BadgeAt(int i)
        {
            while (Badges.Count <= i)
            {
                Badges.Add(null);
            }
            if (Badges[i] == null)
            {
                Badges[i] = WorldBadge.Create(_root.transform, "OverlayStopped" + i, Vector3.zero, 1.8f);
            }
            return Badges[i];
        }

        private static void HideAll()
        {
            foreach (LineRenderer r in Lines)
            {
                if (r != null)
                {
                    r.enabled = false;
                }
            }
            foreach (WorldBadge b in Badges)
            {
                b?.SetVisible(false);
            }
            DrawnLines = 0;
            DrawnMarkers = 0;
            DrawnBadges = 0;
        }
    }
}
