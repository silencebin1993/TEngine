using System;
using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using GameLogic.View;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-CMD-01：战略命令正式化——归还谷地与破碎都市共用同一套框选/编组/Move·Attack·
    /// Guard·Retreat 命令引擎，避免两个 Controller 各写一套判定逻辑（DIGEST 明确点名的坑）。
    ///
    /// ── 与旧 <c>GameLogic.Command.SquadCommandSystem</c>/<c>JobCommandIntent</c> 的关系（裁决，
    /// 不是绕过）──
    /// ER1-REUSE-01 基线登记：那一套走 SimBridge/SimWorld 内核，专属细胞阶段（<c>CellStageFlow</c>），
    /// 且 Move/Attack/Retreat 完成后统一转 <c>HoldPosition</c>（停留 Guard，不交还 AI）而非 AC-CMD-001
    /// 原文的"交还 AI"，2026-09-14 已有产品决策文档化，本 Story 明确不改那份内核代码——归还谷地/
    /// 破碎都市从建立之初就不跑 SimBridge（见 <see cref="HomeValleyController"/> 类注释"移动走独立的
    /// Transform 插值……避免在没把握的情况下往共享战斗内核里加东西"），两套战场是完全独立的表现/
    /// 数据层，不共享同一份 <c>UnitCommand</c>/<c>IntentSource</c> 状态机，因此旧内核那条冲突对本类
    /// 无技术约束力。本类是 AC-CMD-001 在 ER5 区域的**独立正式承接**，在这里按 AC 原文字面实现
    /// （Move 到达/Attack 目标丢失/Retreat 到达安全点均交还 AI，Guard 持续到取消）——即"以后写新代码
    /// 时按验收卡实现"，不是"回头改旧 Story 的已决行为"，两者互不冲突，旧 SquadCommandSystem/
    /// JobCommandIntent 的 Wrap 状态与已记录的产品决策原样保留，供未来若真要打通细胞阶段战斗时再议。
    ///
    /// ── 移动/避障 ──
    /// 区域场景没有 NavMesh，直接复用各 Layout 的 <c>AllAnchors()</c> 静态锚点表（含 ClearanceRadius）
    /// 当作圆形障碍物做最小局部绕行；连续若干次检查点位移不足即判定"路径受阻"，记录可见状态并放弃
    /// （不永远站桩），不是完整寻路系统。
    /// </summary>
    public enum RegionCommandKind : byte
    {
        Move = 0,
        Attack = 1,
        Guard = 2,
        Retreat = 3,
    }

    public readonly struct RegionHostileInfo
    {
        public readonly string HostileId;
        public readonly Vector2 Position;
        public readonly bool Alive;

        public RegionHostileInfo(string hostileId, Vector2 position, bool alive)
        {
            HostileId = hostileId;
            Position = position;
            Alive = alive;
        }
    }

    public readonly struct RegionAttackOutcome
    {
        public readonly bool Success;
        public readonly bool TargetDestroyed;
        public readonly string FailureReason;

        private RegionAttackOutcome(bool success, bool targetDestroyed, string failureReason)
        {
            Success = success;
            TargetDestroyed = targetDestroyed;
            FailureReason = failureReason;
        }

        public static RegionAttackOutcome Ok(bool targetDestroyed) => new RegionAttackOutcome(true, targetDestroyed, null);
        public static RegionAttackOutcome Fail(string reason) => new RegionAttackOutcome(false, false, reason);
    }

    /// <summary>宿主区域 Controller 提供的最小上下文——用委托而不是接口，避免要求
    /// <see cref="HomeValleyController"/>/<see cref="FracturedCityController"/> 改继承结构。</summary>
    public sealed class RegionSquadCommandContext
    {
        public Camera Camera;
        public List<HomeValleyMachineMarker> Markers;
        public GameObject VisualRoot;
        /// <summary>当前是否被直控（接管）——受控机排除在编队选择/命令之外。</summary>
        public Func<int, bool> IsDirectControlled;
        /// <summary>是否是本区域内可被军事命令接受的合法机器（存活、不在厂内等）。</summary>
        public Func<int, bool> IsEligible;
        /// <summary>下达命令前，若机器有在办的"工作"订单先取消（归还谷地专属；破碎都市传 null）。</summary>
        public Action<int> CancelWorkIfAny;
        public Vector2 SafePoint;
        public List<(Vector2 Position, float Radius)> Obstacles;
        /// <summary>ER6-REGION-01：可选目的地裁剪——供铸造前哨外围把 Move 目的地钳制在核心分区封锁线
        /// 以内（"导航阻挡"，见 <see cref="FoundryOutpostRegion.CanEnterCoreZone"/>）。null（默认，
        /// 归还谷地/破碎都市不传）表示不裁剪，向后兼容不受影响。</summary>
        public Func<Vector2, Vector2> ClampDestination;
        public Func<Vector2, float, RegionHostileInfo?> FindHostileNear;
        public Func<string, RegionHostileInfo?> ResolveHostile;
        /// <summary>右键情境命令的宿主钩子（屏幕坐标）：点中工作目标（建筑 / 残骸 / 地面物品 / 建造位）时由宿主派工并返回 true；
        /// 返回 false（或为 null）时右键按“地面 = 移动”处理。攻击判定在它之前（右键敌人永远是攻击）。</summary>
        public Func<Vector3, bool> ContextCommand;
        /// <summary>FG0-ARCH-03：本地点的战斗内核（编队命令的执行在内核里：移动、避障、受阻判定、接战距离、攻击冷却、开火规则）。</summary>
        public GameLogic.Campaign.Combat.CombatSite Site;
        /// <summary>敌对目标 ID → 内核单位 ID（0 = 不存在）。</summary>
        public Func<string, int> HostileUnit;
        public float AttackRange = 6f;
        public float AttackCooldownSeconds = 1f;
    }

    public sealed class RegionSquadCommandSystem
    {
        private const float ArriveRadius = 1.2f;
        private const float GuardArriveRadius = 0.3f; // 原地守备，基本不需要位移就算"到位"。
        private const float MinDragWorldSize = 0.6f;
        private const float ClickPickRadius = 1.6f;
        private const int MaxRecentEvents = 24;
        private const int MaxRingVisuals = 32;

        private RegionSquadCommandContext _ctx;

        private readonly List<int> _selection = new List<int>(32);
        private readonly Dictionary<int, List<int>> _groups = new Dictionary<int, List<int>>(9);
        private readonly List<string> _recentEvents = new List<string>(MaxRecentEvents);
        private readonly Dictionary<int, GameObject> _selectionRings = new Dictionary<int, GameObject>(32);
        /// <summary>每台执行命令中的已选机器一条路线（LineRenderer）+ 一个终点标记；只在被观察时存在。</summary>
        private readonly Dictionary<int, LineRenderer> _routeLines = new Dictionary<int, LineRenderer>(32);
        private readonly Dictionary<int, GameObject> _routeEnds = new Dictionary<int, GameObject>(32);
        private readonly List<int> _visualScratch = new List<int>(32);
        private GameObject _dragBoxGo;
        private LineRenderer _dragBoxOutline;

        private bool _dragging;
        private Vector2 _dragStart;
        private Vector2 _dragCurrent;
        private bool _clickConsumedThisFrame;
        private RegionCommandKind? _armedKind;
        /// <summary>暂停中下达、还没执行过一步的命令条数（UI“已排队 N 条”）。命令本身已经写进内核（进存档），恢复后的第一步开始执行。</summary>
        private int _pendingIssues;

        public IReadOnlyList<int> Selection => _selection;
        public int QueuedCommandCount => _pendingIssues;

        /// <summary>FG1-SIG-07（FGR-SIG-053）：因在信号覆盖外而没收到命令的机器累计台次（自检 / 冒烟读取）。</summary>
        public static int OutOfCoverageRejects { get; private set; }
        /// <summary>FG1-SIG-07：最近一次“目标 / 路线走出覆盖”的提醒次数（自检读取）。</summary>
        public static int RouteWarnings { get; private set; }
        private readonly List<int> _outScratch = new List<int>(8);
        private readonly List<Vector2> _coverageRouteScratch = new List<Vector2>(2);
        public IReadOnlyList<string> RecentEvents => _recentEvents;
        public RegionCommandKind? ArmedKind => _armedKind;
        public bool ConsumedClickThisFrame => _clickConsumedThisFrame;

        /// <summary>FG0-UX-01（FGR-UX-001）：本帧的功能键（默认右键）已被“取消武装待命”用掉，
        /// 下游（归还谷地右键取消工单）不要再读同一次右键。</summary>
        public bool ConsumedSecondaryThisFrame => _secondaryConsumedThisFrame;

        private bool _secondaryConsumedThisFrame;

        public void Bind(RegionSquadCommandContext context)
        {
            _ctx = context;
            _selection.Clear();
            _groups.Clear();
            _recentEvents.Clear();
            _pendingIssues = 0;
            _dragging = false;
            _armedKind = null;
            ClearSelectionRings();
            ClearDestinationVisual();
            ClearDragBox();
        }

        public void Unbind()
        {
            ClearSelectionRings();
            ClearDestinationVisual();
            ClearDragBox();
            _ctx = null;
            _selection.Clear();
            _groups.Clear();
            _pendingIssues = 0;
            _dragging = false;
            _armedKind = null;
        }

        /// <summary>地点不再被观察：销毁选中环与目的地标记（画面对象只在被观察时存在）。选择集与命令保留。</summary>
        public void ReleaseVisuals()
        {
            ClearSelectionRings();
            ClearDestinationVisual();
            ClearDragBox();
        }

        // ── 每帧驱动 ──────────────────────────────────────────────────────

        /// <summary>FG0-ARCH-04：建造模式开着时鼠标归建造（放置 / 拆除），框选、单点选中与“武装命令”的点击都不处理；
        /// 已下达的命令照常推进（机器不会因为玩家在规划而停下）。</summary>
        public bool PointerSuppressed { get; set; }

        /// <summary>输入 + 表现（被观察时每帧）。命令的执行在战斗内核的模拟步里（与是否被观察无关）。</summary>
        public void Tick(bool paused, float dt)
        {
            TickInput(paused);
        }

        /// <summary>FG0-ARCH-01：玩家输入部分（选择 / 编组 / 下令），只在本区域被观察时每帧调用；不推进任何移动。</summary>
        public void TickInput(bool paused)
        {
            if (TickInputCore(paused))
            {
                SyncSelectionVisuals();
                UpdateDestinationVisual();
                UpdateDragBox();
            }
            else
            {
                HideDragBox();
            }
        }

        /// <summary>FG0-ARCH-03：模拟步开头调用（只有不暂停时才有模拟步）：暂停中下达的命令从这一步起开始执行，“已排队”计数清零。
        /// 命令的移动 / 攻击推进在战斗内核里（<see cref="GameLogic.Campaign.Combat.CombatSite.Step"/>），无论本区域是否被观察（FGR-BASE-021）。</summary>
        public void TickSim(float dt)
        {
            if (dt > 0f)
            {
                _pendingIssues = 0;
            }
        }

        /// <summary>返回 false = 本帧没有输入所有权（或未绑定），调用方不再刷新选中表现。</summary>
        private bool TickInputCore(bool paused)
        {
            _clickConsumedThisFrame = false;
            _secondaryConsumedThisFrame = false;
            _cachedPaused = paused;
            if (_ctx == null)
            {
                return false;
            }

            PruneSelection();

            if (!InputRouter.Owns(InputScope.Strategy))
            {
                _dragging = false;
                return false;
            }

            if (PointerSuppressed)
            {
                _dragging = false;
            }
            else
            {
                HandleDragAndClick();
                HandleGroupHotkeys();
                HandleCommandHotkeys(paused);
            }
            return true;
        }

        // ── 选择：框选/单选 ──────────────────────────────────────────────

        private void HandleDragAndClick()
        {
            if (InputRouter.UiPointerCaptured)
            {
                _dragging = false;
                return;
            }

            if (InputRouter.GetMouseButtonDown(0, InputScope.Strategy) &&
                InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 down) &&
                TryScreenToWorld(down, out Vector2 downWorld))
            {
                _dragging = true;
                _dragStart = downWorld;
                _dragCurrent = downWorld;
            }

            if (_dragging && InputRouter.Owns(InputScope.Strategy) &&
                TryScreenToWorld(InputRouter.Reader.MousePosition, out Vector2 moveWorld))
            {
                _dragCurrent = moveWorld;
            }

            if (!_dragging || !InputRouter.GetMouseButtonUp(0, InputScope.Strategy))
            {
                return;
            }
            _dragging = false;

            if (_armedKind.HasValue)
            {
                ResolveAndIssueArmed(_dragCurrent);
                _clickConsumedThisFrame = true;
                return;
            }

            Vector2 size = new Vector2(Mathf.Abs(_dragCurrent.x - _dragStart.x), Mathf.Abs(_dragCurrent.y - _dragStart.y));
            if (size.x < MinDragWorldSize && size.y < MinDragWorldSize)
            {
                // 太小按点选处理：留给 Controller 既有的单点选中/工作下令逻辑，本类不消费这次点击。
                _clickConsumedThisFrame = false;
                return;
            }

            bool additive = InputRouter.Reader.GetKey(KeyCode.LeftShift) || InputRouter.Reader.GetKey(KeyCode.RightShift);
            BoxSelect(_dragStart, _dragCurrent, additive);
            _clickConsumedThisFrame = true;
        }

        private void BoxSelect(Vector2 a, Vector2 b, bool additive)
        {
            Vector2 lo = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            Vector2 hi = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            if (!additive)
            {
                _selection.Clear();
            }
            foreach (HomeValleyMachineMarker marker in _ctx.Markers)
            {
                if (marker == null || _ctx.IsDirectControlled(marker.LogicId) || !_ctx.IsEligible(marker.LogicId))
                {
                    continue; // 受控机排除在框选之外——从源头不进入编队选择。
                }
                Vector2 p = marker.Position;
                if (p.x >= lo.x && p.x <= hi.x && p.y >= lo.y && p.y <= hi.y)
                {
                    AddToSelection(marker.LogicId);
                }
            }
        }

        /// <summary>供 Controller 现有的单点选中路径调用：普通点击选中一台机器时，squad 选择集
        /// 同步为这一台（不影响 Controller 自己的 <c>_selected</c>/高亮，那条既有路径原样不动）。</summary>
        public void SelectSingle(int logicId, bool additive = false)
        {
            if (additive && _selection.Contains(logicId))
            {
                _selection.Remove(logicId); // Shift+点已选中的机器 = 从选择集里去掉（常规 RTS 手感）。
                return;
            }
            if (!additive)
            {
                _selection.Clear();
            }
            if (_ctx != null && !_ctx.IsDirectControlled(logicId) && _ctx.IsEligible(logicId))
            {
                AddToSelection(logicId);
            }
        }

        public void ClearSelection() => _selection.Clear();

        /// <summary>验收/调试入口：一次性设置多台机器的选择集，不经过真实框选手势——同
        /// <c>SquadCommandSystem.SelectExplicit</c>（Cell 阶段）先例，供 execute_code/自动化
        /// journey 机器人直接驱动多选，不必在 Play Mode 里模拟真实鼠标拖拽。</summary>
        public void DebugSelectMany(IEnumerable<int> logicIds)
        {
            _selection.Clear();
            if (logicIds == null || _ctx == null)
            {
                return;
            }
            foreach (int id in logicIds)
            {
                if (!_ctx.IsDirectControlled(id) && _ctx.IsEligible(id))
                {
                    AddToSelection(id);
                }
            }
        }

        private void AddToSelection(int logicId)
        {
            if (_selection.Contains(logicId))
            {
                return;
            }
            _selection.Add(logicId);
        }

        /// <summary>每帧核对选择集：死亡/离场机器移除；被接管（直控）的机器也立即移出——
        /// "受控机排除在编队接管之外"不只是下令那一刻的门槛，选择集本身也不该继续显示/圈选
        /// 一台玩家正在亲自操作的机器（否则 UI 会显示"已选中"却点不动任何命令按钮，误导玩家）。</summary>
        private void PruneSelection()
        {
            for (int i = _selection.Count - 1; i >= 0; i--)
            {
                int id = _selection[i];
                if (!_ctx.IsEligible(id) || _ctx.IsDirectControlled(id))
                {
                    _selection.RemoveAt(i);
                }
            }
        }

        // ── 编组：FG0-UX-01 起按 FG13 第 5 节默认 Alt+1～9 选择、Ctrl+1～9 设定，两者都是独立的可重绑动作 ──

        private void HandleGroupHotkeys()
        {
            for (int slot = 1; slot <= 9; slot++)
            {
                if (InputRouter.ConsumeAction(GroupAssignActionFor(slot), InputScope.Strategy))
                {
                    AssignGroup(slot);
                    return;
                }
                if (InputRouter.ConsumeAction(GroupActionFor(slot), InputScope.Strategy))
                {
                    RecallGroup(slot);
                    return;
                }
            }
        }

        private static GameActionId GroupAssignActionFor(int slot) => (GameActionId)((int)GameActionId.GroupAssign1 + slot - 1);

        private static GameActionId GroupActionFor(int slot)
        {
            switch (slot)
            {
                case 1: return GameActionId.Group1;
                case 2: return GameActionId.Group2;
                case 3: return GameActionId.Group3;
                case 4: return GameActionId.Group4;
                case 5: return GameActionId.Group5;
                case 6: return GameActionId.Group6;
                case 7: return GameActionId.Group7;
                case 8: return GameActionId.Group8;
                default: return GameActionId.Group9;
            }
        }

        public void AssignGroup(int slot)
        {
            if (slot < 1 || slot > 9)
            {
                return;
            }
            _groups[slot] = new List<int>(_selection);
            PushEvent($"编组 {slot} 已保存（{_selection.Count} 台）。");
        }

        public void RecallGroup(int slot)
        {
            if (!_groups.TryGetValue(slot, out List<int> members))
            {
                return;
            }
            _selection.Clear();
            foreach (int id in members)
            {
                if (_ctx.IsEligible(id) && !_ctx.IsDirectControlled(id))
                {
                    AddToSelection(id);
                }
            }
            PushEvent($"编组 {slot} 已调用（{_selection.Count} 台在场）。");
        }

        public int GroupSize(int slot) => _groups.TryGetValue(slot, out List<int> members) ? members.Count : 0;

        // ── 命令：武装 + 确认点击 / 立即命令 ────────────────────────────

        private void HandleCommandHotkeys(bool paused)
        {
            if (_armedKind.HasValue && InputRouter.ConsumeAction(GameActionId.Cancel, InputScope.Strategy))
            {
                CancelArm();
                return;
            }

            // FG0-UX-01（FGR-UX-001）：“等下一次点击选目标”是一种选择模式，右键（功能动作）也能取消它。
            if (_armedKind.HasValue && InputRouter.GetMouseButtonDown(1, InputScope.Strategy))
            {
                _secondaryConsumedThisFrame = true;
                CancelArm();
                return;
            }

            if (_selection.Count == 0)
            {
                return;
            }

            // 右键 = 情境命令，不需要先点按钮：右键敌人 = 攻击，右键工作目标 = 派工（宿主钩子），右键地面 = 移动。
            // 按钮 / 快捷键的“移动 / 攻击”只是切换成“下一次左键确认”的待命状态（攻击移动式用法），不是下令的唯一途径。
            if (InputRouter.GetMouseButtonDown(1, InputScope.Strategy) &&
                InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 rightScreen) &&
                TryScreenToWorld(rightScreen, out Vector2 rightWorld))
            {
                _secondaryConsumedThisFrame = true;
                IssueContextCommand(rightScreen, rightWorld, paused);
                return;
            }

            if (InputRouter.ConsumeAction(GameActionId.CommandMove, InputScope.Strategy))
            {
                ArmCommand(RegionCommandKind.Move);
            }
            else if (InputRouter.ConsumeAction(GameActionId.CommandAttack, InputScope.Strategy))
            {
                ArmCommand(RegionCommandKind.Attack);
            }
            else if (InputRouter.ConsumeAction(GameActionId.CommandGuard, InputScope.Strategy))
            {
                IssueGuardHere(paused);
            }
            else if (InputRouter.ConsumeAction(GameActionId.CommandRetreat, InputScope.Strategy))
            {
                IssueRetreat(paused);
            }
        }

        /// <summary>右键情境命令（也是自检入口）：敌人 → 攻击；宿主认领的工作目标 → 派工；否则 → 移动到该点。</summary>
        public void IssueContextCommand(Vector3 screen, Vector2 world, bool paused)
        {
            if (_ctx == null || _selection.Count == 0)
            {
                return;
            }
            RegionHostileInfo? hostile = _ctx.FindHostileNear?.Invoke(world, ClickPickRadius);
            if (hostile != null && hostile.Value.Alive)
            {
                IssueAttack(hostile.Value.HostileId, paused);
                return;
            }
            if (_ctx.ContextCommand != null && _ctx.ContextCommand(screen))
            {
                _armedKind = null;
                return;
            }
            IssueMoveTo(world, paused);
        }

        /// <summary>UI 按钮/热键共用入口：进入"武装"状态，下一次左键点击世界即确认目标。
        /// Guard/Retreat 不走这条路径——它们目标固定（当前位置/安全点），见
        /// <see cref="IssueGuardHere"/>/<see cref="IssueRetreat"/>。</summary>
        public void ArmCommand(RegionCommandKind kind)
        {
            if (_selection.Count == 0 || (kind != RegionCommandKind.Move && kind != RegionCommandKind.Attack))
            {
                return;
            }
            _armedKind = kind;
            PushEvent(kind == RegionCommandKind.Move ? "移动待命：点击地图目标位置。" : "攻击待命：点击一个敌方目标。");
        }

        public void CancelArm()
        {
            if (_armedKind.HasValue)
            {
                _armedKind = null;
                PushEvent("已取消待命命令。");
            }
        }

        private void ResolveAndIssueArmed(Vector2 worldPoint)
        {
            RegionCommandKind kind = _armedKind.Value;
            _armedKind = null;
            if (kind == RegionCommandKind.Attack)
            {
                RegionHostileInfo? hostile = _ctx.FindHostileNear?.Invoke(worldPoint, ClickPickRadius);
                if (hostile == null || !hostile.Value.Alive)
                {
                    PushEvent("攻击失败：未指向有效目标。");
                    FeedbackCues.Raise(FeedbackCueId.Denied, "攻击失败：未指向有效目标。");
                    return;
                }
                IssueAttack(hostile.Value.HostileId, IsCallerPaused());
                return;
            }
            IssueMoveTo(worldPoint, IsCallerPaused());
        }

        /// <summary>Guard/Retreat 走"立即命令"（目标固定，不需要点击确认）；暂停期间下达即“排队”（写进内核，恢复后第一步执行）。</summary>
        public void IssueGuardHere(bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }
            _armedKind = null; // Guard 是立即命令，不该留着别的命令还在"武装待命"。
            // 每台机器守自己当前的位置：传 NaN，由内核按执行者当前位置落点。
            Issue(RegionCommandKind.Guard, new Vector2(float.NaN, float.NaN), null, paused);
        }

        public void IssueRetreat(bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }
            _armedKind = null;
            Issue(RegionCommandKind.Retreat, _ctx.SafePoint, null, paused);
        }

        /// <summary>Move/Attack 的非武装直发变体——供验收/调试与未来其它正式入口直接下令；武装+点击路径最终走同一个 <see cref="Issue"/> 出口。</summary>
        public void IssueMoveTo(Vector2 target, bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }
            _armedKind = null;
            Vector2 clamped = _ctx.ClampDestination != null ? _ctx.ClampDestination(target) : target;
            Issue(RegionCommandKind.Move, clamped, null, paused);
        }

        public void IssueAttack(string hostileId, bool paused)
        {
            if (_selection.Count == 0 || string.IsNullOrEmpty(hostileId))
            {
                return;
            }
            RegionHostileInfo? hostile = _ctx?.ResolveHostile?.Invoke(hostileId);
            if (hostile == null || !hostile.Value.Alive)
            {
                PushEvent("攻击失败：未指向有效目标。");
                FeedbackCues.Raise(FeedbackCueId.Denied, "攻击失败：未指向有效目标。");
                return;
            }
            _armedKind = null;
            Issue(RegionCommandKind.Attack, hostile.Value.Position, hostileId, paused);
        }

        /// <summary>本类唯一的命令派发出口。命令直接写进战斗内核（FG-GAP-018：命令随内核快照进存档，读档后继续执行）；
        /// 暂停中下达的标为“待执行”，恢复后的第一步开始执行（Demo 的“暂停下达即排队、恢复时按下令那一刻的名单执行”）。</summary>
        private void Issue(RegionCommandKind kind, Vector2 targetPosition, string hostileId, bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }
            int started = StartCommandForTargets(kind, targetPosition, hostileId, _selection, paused);
            if (started > 0 && kind == RegionCommandKind.Move)
            {
                WarnIfRouteLeavesCoverage(targetPosition);
            }
            if (paused)
            {
                if (started > 0)
                {
                    _pendingIssues++;
                }
                PushEvent($"{KindLabel(kind)} 已排队（{started} 台，等待恢复）。");
                FeedbackCues.Raise(FeedbackCueId.CommandAck);
            }
        }

        private static BinGames.Sim.Combat.CombatCommandKind ToKernel(RegionCommandKind kind)
        {
            switch (kind)
            {
                case RegionCommandKind.Move: return BinGames.Sim.Combat.CombatCommandKind.Move;
                case RegionCommandKind.Attack: return BinGames.Sim.Combat.CombatCommandKind.Attack;
                case RegionCommandKind.Guard: return BinGames.Sim.Combat.CombatCommandKind.Guard;
                default: return BinGames.Sim.Combat.CombatCommandKind.Retreat;
            }
        }

        private static bool TryFromKernel(BinGames.Sim.Combat.CombatCommandKind kind, out RegionCommandKind result)
        {
            switch (kind)
            {
                case BinGames.Sim.Combat.CombatCommandKind.Move: result = RegionCommandKind.Move; return true;
                case BinGames.Sim.Combat.CombatCommandKind.Attack: result = RegionCommandKind.Attack; return true;
                case BinGames.Sim.Combat.CombatCommandKind.Guard: result = RegionCommandKind.Guard; return true;
                case BinGames.Sim.Combat.CombatCommandKind.Retreat: result = RegionCommandKind.Retreat; return true;
                default: result = default; return false;
            }
        }

        private int StartCommandForTargets(RegionCommandKind kind, Vector2 targetPosition, string hostileId, IReadOnlyList<int> targets, bool pending)
        {
            int firstStarted = 0;
            int started = 0;
            int targetUnit = kind == RegionCommandKind.Attack && _ctx.HostileUnit != null ? _ctx.HostileUnit(hostileId) : 0;
            _outScratch.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                int logicId = targets[i];
                if (_ctx.IsDirectControlled(logicId) || !_ctx.IsEligible(logicId))
                {
                    continue; // 受控机排除在编队接管之外。
                }
                // FG1-SIG-07（FGR-SIG-053）：覆盖外的机器收不到远程命令，继续执行最后的命令和 AI 教义（下令这一刻逐台现采样，O(选中数 × 覆盖源数)）。
                if (!SignalCoverageService.CanReceiveCommand(logicId))
                {
                    _outScratch.Add(logicId);
                    continue;
                }
                HomeValleyMachineMarker marker = FindMarker(logicId);
                if (marker == null || _ctx.Site == null)
                {
                    continue;
                }
                _ctx.CancelWorkIfAny?.Invoke(logicId);
                marker.CancelCommandMove(); // 与“工作赶路”互斥：同一条内核命令槽，后下达的覆盖。
                bool ok = _ctx.Site.IssueCommand(marker.UnitId, ToKernel(kind), targetPosition, targetUnit,
                    kind == RegionCommandKind.Guard ? GuardArriveRadius : ArriveRadius,
                    _ctx.AttackRange, _ctx.AttackCooldownSeconds, pending);
                if (!ok)
                {
                    continue;
                }
                started++;
                if (firstStarted == 0)
                {
                    firstStarted = logicId;
                }
            }
            ReportOutOfCoverage(_outScratch);
            if (!pending)
            {
                PushEvent($"{KindLabel(kind)} 已下达（{started} 台）。");
                if (firstStarted != 0)
                {
                    // 命令确认音按第一台接令机器的底盘区分（ChassisCatalog.SfxId 的消费点）；选择框与路径线是它的等价视觉反馈，不出字幕。
                    FeedbackCues.Raise(FeedbackCueId.CommandAck, null, FeedbackCues.MachineChassisSfx(firstStarted));
                }
            }
            return started;
        }

        /// <summary>FG1-SIG-07（FGR-SIG-053）：有机器因为在覆盖外没收到命令——命令栏事件行 + 拒绝音 + 字幕写明几台、是哪几台（B06 / B07）。</summary>
        public void ReportOutOfCoverage(IReadOnlyList<int> logicIds)
        {
            if (logicIds == null || logicIds.Count == 0)
            {
                return;
            }
            OutOfCoverageRejects += logicIds.Count;
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < logicIds.Count && i < 3; i++)
            {
                if (i > 0)
                {
                    names.Append(GameText.Get("signal.uplink.status.list_sep"));
                }
                names.Append(Signal.SignalPresence.MachineLabel(logicIds[i]));
            }
            if (logicIds.Count > 3)
            {
                names.Append("…");
            }
            string text = GameText.Format("signal.command.out_of_coverage", logicIds.Count.ToString(), names.ToString());
            PushEvent(text);
            FeedbackCues.Raise(FeedbackCueId.Denied, text);
        }

        /// <summary>FG1-SIG-07：移动命令的目标点 / 从编队中心到目标的直线走出信号覆盖时提醒（命令照样下达：玩家可以有意派机器去覆盖外，到了之后收不到新命令）。
        /// 用的是派遣检查单同一个提醒接口 <see cref="SignalCoverageService.RouteOutsideLength"/>。</summary>
        private void WarnIfRouteLeavesCoverage(Vector2 target)
        {
            if (_ctx?.Site == null || !SignalCoverageService.IsBoundedSite(_ctx.Site.SiteId))
            {
                return;
            }
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (int id in _selection)
            {
                HomeValleyMachineMarker m = FindMarker(id);
                if (m != null)
                {
                    sum += m.Position;
                    n++;
                }
            }
            if (n == 0)
            {
                return;
            }
            _coverageRouteScratch.Clear();
            _coverageRouteScratch.Add(sum / n);
            _coverageRouteScratch.Add(target);
            float outside = SignalCoverageService.RouteOutsideLength(_ctx.Site.SiteId, _coverageRouteScratch, out _);
            if (outside <= 0f)
            {
                return;
            }
            RouteWarnings++;
            string text = SignalCoverageService.Sample(_ctx.Site.SiteId, target).Covered
                ? GameText.Format("signal.route.leaves_coverage", Mathf.RoundToInt(outside).ToString())
                : GameText.Get("signal.command.target_outside");
            PushEvent(text);
            FeedbackCues.Raise(FeedbackCueId.SignalLinkWarning, text);
        }

        /// <summary>停止：清除选择集内所有机器的当前编队命令，交还 AI（Guard 的"取消"落点）。
        /// FG1-SIG-07：覆盖外的机器收不到“停止”（它也是远程命令），继续执行最后的命令。</summary>
        public void Stop()
        {
            _armedKind = null;
            if (_ctx == null)
            {
                return;
            }
            _outScratch.Clear();
            foreach (int id in _selection)
            {
                if (!SignalCoverageService.CanReceiveCommand(id))
                {
                    _outScratch.Add(id);
                    continue;
                }
                if (TryGetActiveCommandKind(id, out _))
                {
                    CancelCommandFor(id);
                    PushEvent(GameText.Format("squad.ev.stopped", MachineNaming.Short(id))); // FG4-ECO-07：机器称呼走 MachineNaming（名字同源），文字走文本键
                }
                // 右键改成情境命令后，“停止”也负责让出在办工单（原先右键取消工单的入口）；没有工单时是 no-op。
                _ctx?.CancelWorkIfAny?.Invoke(id);
                FindMarker(id)?.CancelCommandMove();
            }
            ReportOutOfCoverage(_outScratch);
        }

        /// <summary>ER5-CTL-01：接管/释放一台机器时查询它当前的编队命令种类（工作赶路不算编队命令）。纯只读查询。</summary>
        public bool TryGetActiveCommandKind(int logicId, out RegionCommandKind kind)
        {
            kind = default;
            HomeValleyMachineMarker marker = _ctx != null ? FindMarker(logicId) : null;
            if (marker == null || _ctx.Site == null || !_ctx.Site.TryGetCommand(marker.UnitId, out BinGames.Sim.Combat.CombatCommand cmd))
            {
                return false;
            }
            return TryFromKernel(cmd.Kind, out kind);
        }

        /// <summary>单机取消编队命令（工作分配接管一台机器之前调用；没有在办的编队命令时是安全的 no-op）。</summary>
        public void CancelCommandFor(int logicId)
        {
            if (TryGetActiveCommandKind(logicId, out _))
            {
                HomeValleyMachineMarker marker = FindMarker(logicId);
                _ctx.Site.ClearCommand(marker.UnitId);
            }
        }

        /// <summary>FG0-ARCH-03：内核报告的编队命令事件 → 编队事件文本与反馈（文本与 Demo 逐条一致）。</summary>
        public void OnKernelEvent(in BinGames.Sim.Combat.CombatEvent e, int logicId, string hostileId, GameLogic.Campaign.Combat.CombatSite site)
        {
            switch (e.Kind)
            {
                case BinGames.Sim.Combat.CombatEventKind.AttackOutcome:
                {
                    var r = (BinGames.Sim.Combat.CombatFireResult)e.Code;
                    bool destroyed = e.Code2 != 0;
                    if (r == BinGames.Sim.Combat.CombatFireResult.Ok || r == BinGames.Sim.Combat.CombatFireResult.StillAiming)
                    {
                        PushEvent(GameText.Format(destroyed ? "squad.ev.destroyed" : "squad.ev.hit", MachineNaming.Short(logicId), hostileId));
                    }
                    else
                    {
                        PushEvent(GameText.Format("squad.ev.miss", MachineNaming.Short(logicId), site?.FireReason(r, logicId, hostileId)));
                    }
                    return;
                }
                case BinGames.Sim.Combat.CombatEventKind.CommandStuckStrike:
                    PushEvent(GameText.Format("squad.ev.stuck_retry", MachineNaming.Short(logicId), (int)e.Value, (int)e.Value2));
                    return;
                case BinGames.Sim.Combat.CombatEventKind.CommandEnded:
                {
                    var reason = (BinGames.Sim.Combat.CombatEndReason)e.Code;
                    var kind = (BinGames.Sim.Combat.CombatCommandKind)e.Code2;
                    switch (reason)
                    {
                        case BinGames.Sim.Combat.CombatEndReason.Arrived:
                            PushEvent(GameText.Format(kind == BinGames.Sim.Combat.CombatCommandKind.Retreat ? "squad.ev.retreated" : "squad.ev.arrived", MachineNaming.Short(logicId)));
                            return;
                        case BinGames.Sim.Combat.CombatEndReason.TargetLost:
                            PushEvent(GameText.Format("squad.ev.target_lost", MachineNaming.Short(logicId)));
                            return;
                        case BinGames.Sim.Combat.CombatEndReason.Stuck:
                            PushEvent(GameText.Format("squad.ev.stuck_gave_up", MachineNaming.Short(logicId)));
                            FeedbackCues.Raise(FeedbackCueId.Denied, GameText.Format("squad.cue.stuck_gave_up", FeedbackCues.MachineLabel(logicId)));
                            return;
                        case BinGames.Sim.Combat.CombatEndReason.Unreachable:
                        {
                            // FG0-ARCH-06：寻路失败——命令结束并给出明确原因（不原地发呆）；通知可定位到目标点。
                            var fail = (BinGames.Sim.Nav.NavFailReason)(int)e.Value;
                            var target = new Vector2((float)e.Pos.x, (float)e.Pos.y); // 内核在“无法到达”时把命令目标点放在事件位置上。
                            string text = GameLogic.Localization.GameText.Format("nav.squad.unreachable", MachineNaming.Short(logicId),
                                GameLogic.Campaign.Nav.NavService.FailText(fail, GameLogic.Campaign.Nav.NavService.CellOf(e.Pos.x, e.Pos.y)));
                            PushEvent(text);
                            FeedbackCues.Raise(FeedbackCueId.Denied, text);
                            GameLogic.Notifications.NotificationCenter.Post("unreachable", text, new Vector3(target.x, 0f, target.y));
                            return;
                        }
                        default:
                            return; // 击毁目标的文本随攻击结果一起写过了。
                    }
                }
            }
        }

        // ── 视觉：选中环 + 目的地/路径 ──────────────────────────────────

        private void SyncSelectionVisuals()
        {
            if (_ctx?.VisualRoot == null)
            {
                return;
            }
            var toRemove = new List<int>();
            foreach (KeyValuePair<int, GameObject> kv in _selectionRings)
            {
                if (!_selection.Contains(kv.Key))
                {
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int id in toRemove)
            {
                if (_selectionRings[id] != null)
                {
                    GameLogic.View.UnityObjects.Release(_selectionRings[id]);
                }
                _selectionRings.Remove(id);
            }

            if (_selection.Count > MaxRingVisuals)
            {
                return;
            }
            foreach (int id in _selection)
            {
                HomeValleyMachineMarker marker = FindMarker(id);
                if (marker == null)
                {
                    continue;
                }
                if (!_selectionRings.TryGetValue(id, out GameObject ring) || ring == null)
                {
                    ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    ring.name = "SquadRing_" + id;
                    ring.transform.SetParent(_ctx.VisualRoot.transform, false);
                    ring.transform.localScale = new Vector3(1.6f, 0.02f, 1.6f);
                    GameLogic.View.UnityObjects.Release(ring.GetComponent<Collider>());
                    Renderer r = ring.GetComponent<Renderer>();
                    r.sharedMaterial = ViewMaterials.Standard(new Color(1f, 0.85f, 0.2f, 0.55f));
                    _selectionRings[id] = ring;
                }
                Vector2 p = marker.Position;
                ring.transform.position = new Vector3(p.x, 0.03f, p.y);
            }
        }

        private void ClearSelectionRings()
        {
            foreach (GameObject go in _selectionRings.Values)
            {
                if (go != null)
                {
                    GameLogic.View.UnityObjects.Release(go);
                }
            }
            _selectionRings.Clear();
        }

        /// <summary>选择集里每台执行命令的机器显示内核的剩余实际路线和点击目标标记。
        /// 新目标立即显示；寻路完成前临时连接目标，结果可用的当帧改为完整绕障折线。
        /// 开销 O(选择集 × 路点)，只在被观察时；选择集超过 <see cref="MaxRingVisuals"/> 时只画前面这些。</summary>
        private void UpdateDestinationVisual()
        {
            if (_ctx?.VisualRoot == null)
            {
                return;
            }
            _visualScratch.Clear();
            foreach (int id in _selection)
            {
                if (_visualScratch.Count >= MaxRingVisuals)
                {
                    break;
                }
                HomeValleyMachineMarker m = FindMarker(id);
                if (m == null || _ctx.Site == null || !_ctx.Site.TryGetCommand(m.UnitId, out BinGames.Sim.Combat.CombatCommand cmd)
                    || !TryFromKernel(cmd.Kind, out RegionCommandKind kind))
                {
                    continue;
                }
                var target = new Vector2((float)cmd.Pos.x, (float)cmd.Pos.y);
                if (float.IsNaN(target.x) || float.IsNaN(target.y))
                {
                    target = m.Position; // 原地守备：目标点由内核按执行者当前位置落点。
                }
                _visualScratch.Add(id);
                DrawRoute(id, m, kind, target);
            }

            // 回收不再需要的路线（机器被取消选中、命令结束或死亡）。
            _removeScratch.Clear();
            foreach (int id in _routeLines.Keys)
            {
                if (!_visualScratch.Contains(id))
                {
                    _removeScratch.Add(id);
                }
            }
            foreach (int id in _removeScratch)
            {
                ReleaseRoute(id);
            }
        }

        private readonly List<int> _removeScratch = new List<int>(32);
        private readonly List<Unity.Mathematics.double2> _routeScratch = new List<Unity.Mathematics.double2>(32);
        private const float RouteHeight = 0.06f;

        private void DrawRoute(int id, HomeValleyMachineMarker marker, RegionCommandKind kind, Vector2 target)
        {
            Color color = KindColor(kind);
            if (!_routeLines.TryGetValue(id, out LineRenderer line) || line == null)
            {
                var go = new GameObject("SquadCommandPath_" + id);
                go.transform.SetParent(_ctx.VisualRoot.transform, false);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 线面平铺在地面上，俯视镜头下粗细稳定。
                line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.widthMultiplier = 0.15f;
                line.numCapVertices = 2;
                line.alignment = LineAlignment.TransformZ;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                _routeLines[id] = line;
            }
            line.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(color.r, color.g, color.b, 0.85f));

            Vector3 from = marker.Position3;
            _routeScratch.Clear();
            bool nav = _ctx.Site != null && _ctx.Site.NavEnabled;
            if (nav) _ctx.Site.CopyRoute(marker.UnitId, _routeScratch);
            bool failed = nav && _ctx.Site.TryGetNavState(marker.UnitId, out var navState, out _)
                && navState == BinGames.Sim.Combat.CombatNavState.Failed;
            line.positionCount = failed ? 0 : _routeScratch.Count > 0 ? _routeScratch.Count + 1 : 2;
            if (line.positionCount > 0)
            {
                line.SetPosition(0, new Vector3(from.x, RouteHeight, from.z));
                if (_routeScratch.Count == 0)
                {
                    line.SetPosition(1, new Vector3(target.x, RouteHeight, target.y));
                }
                else
                {
                    for (int k = 0; k < _routeScratch.Count; k++)
                    {
                        var point = _routeScratch[k];
                        line.SetPosition(k + 1, new Vector3((float)point.x, RouteHeight, (float)point.y));
                    }
                }
            }

            if (!_routeEnds.TryGetValue(id, out GameObject end) || end == null)
            {
                end = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                end.name = "SquadCommandDestination_" + id;
                end.transform.SetParent(_ctx.VisualRoot.transform, false);
                end.transform.localScale = new Vector3(0.6f, 0.03f, 0.6f);
                GameLogic.View.UnityObjects.Release(end.GetComponent<Collider>());
                _routeEnds[id] = end;
            }
            end.GetComponent<Renderer>().sharedMaterial = ViewMaterials.Standard(color);
            end.transform.position = new Vector3(target.x, 0.04f, target.y);
        }

        private void ReleaseRoute(int id)
        {
            if (_routeLines.TryGetValue(id, out LineRenderer line) && line != null)
            {
                GameLogic.View.UnityObjects.Release(line.gameObject);
            }
            _routeLines.Remove(id);
            if (_routeEnds.TryGetValue(id, out GameObject end) && end != null)
            {
                GameLogic.View.UnityObjects.Release(end);
            }
            _routeEnds.Remove(id);
        }

        /// <summary>自检用：当前画出的路线条数（每台执行命令中的已选机器一条）。</summary>
        public int VisibleRouteCount
        {
            get
            {
                int n = 0;
                foreach (LineRenderer line in _routeLines.Values)
                {
                    if (line != null && line.positionCount >= 2)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        private void ClearDestinationVisual()
        {
            _removeScratch.Clear();
            _removeScratch.AddRange(_routeLines.Keys);
            foreach (int id in _routeEnds.Keys)
            {
                if (!_removeScratch.Contains(id))
                {
                    _removeScratch.Add(id);
                }
            }
            foreach (int id in _removeScratch)
            {
                ReleaseRoute(id);
            }
        }

        // ── 框选指示：拖拽中在地面画出将被选中的世界矩形（半透明底 + 描边），与判定用的是同一个矩形 ──

        private void UpdateDragBox()
        {
            float sizeX = Mathf.Abs(_dragCurrent.x - _dragStart.x);
            float sizeY = Mathf.Abs(_dragCurrent.y - _dragStart.y);
            if (!_dragging || _armedKind.HasValue || _ctx?.VisualRoot == null || (sizeX < MinDragWorldSize && sizeY < MinDragWorldSize))
            {
                HideDragBox();
                return;
            }
            if (_dragBoxGo == null)
            {
                _dragBoxGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _dragBoxGo.name = "SquadDragBox";
                _dragBoxGo.transform.SetParent(_ctx.VisualRoot.transform, false);
                GameLogic.View.UnityObjects.Release(_dragBoxGo.GetComponent<Collider>());
                Renderer r = _dragBoxGo.GetComponent<Renderer>();
                r.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.35f, 0.85f, 0.45f, 0.18f));
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var outlineGo = new GameObject("Outline");
                outlineGo.transform.SetParent(_ctx.VisualRoot.transform, false);
                outlineGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                outlineGo.transform.SetParent(_dragBoxGo.transform, true);
                _dragBoxOutline = outlineGo.AddComponent<LineRenderer>();
                _dragBoxOutline.useWorldSpace = true;
                _dragBoxOutline.loop = true;
                _dragBoxOutline.positionCount = 4;
                _dragBoxOutline.widthMultiplier = 0.08f;
                _dragBoxOutline.alignment = LineAlignment.TransformZ;
                _dragBoxOutline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _dragBoxOutline.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(0.45f, 1f, 0.55f, 0.95f));
            }
            _dragBoxGo.SetActive(true);
            float loX = Mathf.Min(_dragStart.x, _dragCurrent.x);
            float loY = Mathf.Min(_dragStart.y, _dragCurrent.y);
            float hiX = Mathf.Max(_dragStart.x, _dragCurrent.x);
            float hiY = Mathf.Max(_dragStart.y, _dragCurrent.y);
            _dragBoxGo.transform.SetPositionAndRotation(new Vector3((loX + hiX) * 0.5f, 0.07f, (loY + hiY) * 0.5f), Quaternion.Euler(90f, 0f, 0f));
            _dragBoxGo.transform.localScale = new Vector3(Mathf.Max(hiX - loX, 0.01f), Mathf.Max(hiY - loY, 0.01f), 1f);
            const float y = 0.08f;
            _dragBoxOutline.SetPosition(0, new Vector3(loX, y, loY));
            _dragBoxOutline.SetPosition(1, new Vector3(hiX, y, loY));
            _dragBoxOutline.SetPosition(2, new Vector3(hiX, y, hiY));
            _dragBoxOutline.SetPosition(3, new Vector3(loX, y, hiY));
        }

        /// <summary>自检用：框选指示当前是否可见。</summary>
        public bool DragBoxVisible => _dragBoxGo != null && _dragBoxGo.activeSelf;

        private void HideDragBox()
        {
            if (_dragBoxGo != null && _dragBoxGo.activeSelf)
            {
                _dragBoxGo.SetActive(false);
            }
        }

        private void ClearDragBox()
        {
            if (_dragBoxGo != null)
            {
                GameLogic.View.UnityObjects.Release(_dragBoxGo);
            }
            _dragBoxGo = null;
            _dragBoxOutline = null;
        }

        private static Color KindColor(RegionCommandKind kind)
        {
            switch (kind)
            {
                case RegionCommandKind.Attack: return new Color(0.9f, 0.25f, 0.2f);
                case RegionCommandKind.Guard: return new Color(0.9f, 0.8f, 0.2f);
                case RegionCommandKind.Retreat: return new Color(0.25f, 0.8f, 0.4f);
                default: return new Color(0.3f, 0.55f, 0.95f);
            }
        }

        // ── 工具 ──────────────────────────────────────────────────────────

        private HomeValleyMachineMarker FindMarker(int logicId)
        {
            foreach (HomeValleyMachineMarker marker in _ctx.Markers)
            {
                if (marker != null && marker.LogicId == logicId)
                {
                    return marker;
                }
            }
            return null;
        }

        private bool TryScreenToWorld(Vector3 screenPosition, out Vector2 world)
        {
            world = Vector2.zero;
            if (_ctx?.Camera == null)
            {
                return false;
            }
            var plane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = _ctx.Camera.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out float enter))
            {
                return false;
            }
            Vector3 hit = ray.GetPoint(enter);
            world = new Vector2(hit.x, hit.z);
            return true;
        }

        private static string KindLabel(RegionCommandKind kind)
        {
            switch (kind)
            {
                case RegionCommandKind.Move: return "移动";
                case RegionCommandKind.Attack: return "攻击";
                case RegionCommandKind.Guard: return "守备";
                default: return "撤退";
            }
        }

        private void PushEvent(string text)
        {
            _recentEvents.Add(text);
            if (_recentEvents.Count > MaxRecentEvents)
            {
                _recentEvents.RemoveAt(0);
            }
            Log.Info($"[RegionSquadCommandSystem] {text}");
        }

        /// <summary>暂停态由 Controller 持有，本类没有独立字段——武装命令在点击确认那一刻
        /// 需要知道当前是否暂停才能决定"立即执行"还是"排队"，通过 <see cref="Tick"/> 的
        /// <c>paused</c> 参数在每帧开头缓存。</summary>
        private bool _cachedPaused;
        private bool IsCallerPaused() => _cachedPaused;
    }
}
