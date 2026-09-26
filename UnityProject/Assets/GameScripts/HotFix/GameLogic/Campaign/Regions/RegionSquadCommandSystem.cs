using System;
using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using TEngine;
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
        public Func<int, string, RegionAttackOutcome> TryAttack;
        public float AttackRange = 6f;
        public float AttackCooldownSeconds = 1f;
    }

    public sealed class RegionSquadCommandSystem
    {
        private const float MoveSpeed = 6f; // 与 HomeValleyMachineMarker.MoveSpeed 手感一致。
        private const float ArriveRadius = 1.2f;
        private const float GuardArriveRadius = 0.3f; // 原地守备，基本不需要位移就算"到位"。
        private const float MinDragWorldSize = 0.6f;
        private const float ClickPickRadius = 1.6f;
        private const float LookaheadDistance = 3f;
        private const float MachineRadius = 0.9f;
        private const float AvoidWeight = 1.35f;
        private const float ProgressCheckInterval = 1f;
        private const float MinProgressDelta = 0.5f;
        private const int MaxStuckStrikes = 3;
        private const int MaxRecentEvents = 24;
        private const int MaxRingVisuals = 32;

        private RegionSquadCommandContext _ctx;

        private readonly List<int> _selection = new List<int>(32);
        private readonly Dictionary<int, List<int>> _groups = new Dictionary<int, List<int>>(9);
        private readonly Dictionary<int, ActiveCommand> _active = new Dictionary<int, ActiveCommand>(32);
        private readonly List<QueuedCommand> _queued = new List<QueuedCommand>(16);
        private readonly List<string> _recentEvents = new List<string>(MaxRecentEvents);
        private readonly Dictionary<int, GameObject> _selectionRings = new Dictionary<int, GameObject>(32);
        private GameObject _destinationMarkerGo;
        private GameObject _pathBarGo;

        private bool _dragging;
        private Vector2 _dragStart;
        private Vector2 _dragCurrent;
        private bool _clickConsumedThisFrame;
        private RegionCommandKind? _armedKind;

        private struct ActiveCommand
        {
            public RegionCommandKind Kind;
            public Vector2 TargetPosition;
            public string HostileId;
            public float ArriveRadius;
            public float ProgressCheckTimer;
            public float LastProgressDistance;
            public int StuckStrikes;
            public float AttackCooldownRemaining;
            public bool HasLastProgressDistance;
        }

        private struct QueuedCommand
        {
            public RegionCommandKind Kind;
            public Vector2 TargetPosition;
            public string HostileId;
            public int[] Targets;
        }

        public IReadOnlyList<int> Selection => _selection;
        public int QueuedCommandCount => _queued.Count;
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
            _active.Clear();
            _queued.Clear();
            _recentEvents.Clear();
            _dragging = false;
            _armedKind = null;
            ClearSelectionRings();
            ClearDestinationVisual();
        }

        public void Unbind()
        {
            ClearSelectionRings();
            ClearDestinationVisual();
            _ctx = null;
            _selection.Clear();
            _groups.Clear();
            _active.Clear();
            _queued.Clear();
            _dragging = false;
            _armedKind = null;
        }

        // ── 每帧驱动 ──────────────────────────────────────────────────────

        /// <summary>FG0-ARCH-04：建造模式开着时鼠标归建造（放置 / 拆除），框选、单点选中与“武装命令”的点击都不处理；
        /// 已下达的命令照常推进（机器不会因为玩家在规划而停下）。</summary>
        public bool PointerSuppressed { get; set; }

        /// <summary>由 Controller.Update 在暂停早退**之前**调用——战略暂停下仍要能选人/排队命令。
        /// <paramref name="paused"/>=true 时不推进任何移动/攻击 Tick，只处理选择/编组/命令下达
        /// （下达即排队，不立即执行）。</summary>
        public void Tick(bool paused, float dt)
        {
            _clickConsumedThisFrame = false;
            _secondaryConsumedThisFrame = false;
            _cachedPaused = paused;
            if (_ctx == null)
            {
                return;
            }

            PruneSelection();

            if (!InputRouter.Owns(InputScope.Strategy))
            {
                _dragging = false;
                return;
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

            if (!paused)
            {
                FlushQueued();
                TickActiveCommands(dt);
            }

            SyncSelectionVisuals();
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
                Vector3 p = marker.transform.position;
                if (p.x >= lo.x && p.x <= hi.x && p.z >= lo.y && p.z <= hi.y)
                {
                    AddToSelection(marker.LogicId);
                }
            }
        }

        /// <summary>供 Controller 现有的单点选中路径调用：普通点击选中一台机器时，squad 选择集
        /// 同步为这一台（不影响 Controller 自己的 <c>_selected</c>/高亮，那条既有路径原样不动）。</summary>
        public void SelectSingle(int logicId)
        {
            _selection.Clear();
            if (_ctx != null && !_ctx.IsDirectControlled(logicId) && _ctx.IsEligible(logicId))
            {
                _selection.Add(logicId);
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

        /// <summary>Guard/Retreat 走"立即命令"（目标固定，不需要点击确认）；暂停期间仍然只是排队，
        /// 与武装+点击路径共用同一个 <see cref="Issue"/> 出口。</summary>
        public void IssueGuardHere(bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }
            _armedKind = null; // Guard 是立即命令，不该留着别的命令还在"武装待命"。
            // 每台机器守自己当前的位置——目标在 Issue 时按选择集逐台各自计算，这里传 NaN 作为
            // "使用发出命令那一刻各自当前位置" 的标记，由 StartCommand 识别。
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

        /// <summary>Move/Attack 的非武装直发变体——供验收/调试与未来其它正式入口（如工作分配引擎
        /// 以外的自动化 journey）直接下令，不必先武装再模拟一次世界点击。武装+点击路径
        /// （<see cref="ArmCommand"/>/<see cref="ResolveAndIssueArmed"/>）内部最终也调用这两个方法
        /// 走到的同一个 <see cref="Issue"/> 出口，行为完全一致。</summary>
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

        /// <summary>本类唯一的命令派发出口——立即执行或排队，由 <paramref name="paused"/> 决定。
        /// 排队时快照当时的选择集（LogicId 数组），不引用 <see cref="_selection"/> 本身——暂停期间
        /// 玩家还会继续改选择，恢复时必须按下令那一刻的名单执行，不能被后续改选择污染。</summary>
        private void Issue(RegionCommandKind kind, Vector2 targetPosition, string hostileId, bool paused)
        {
            if (_selection.Count == 0)
            {
                return;
            }

            if (paused)
            {
                _queued.Add(new QueuedCommand
                {
                    Kind = kind,
                    TargetPosition = targetPosition,
                    HostileId = hostileId,
                    Targets = _selection.ToArray(),
                });
                PushEvent($"{KindLabel(kind)} 已排队（{_selection.Count} 台，等待恢复）。");
                FeedbackCues.Raise(FeedbackCueId.CommandAck);
                return;
            }

            StartCommandForTargets(kind, targetPosition, hostileId, _selection);
        }

        private void FlushQueued()
        {
            if (_queued.Count == 0)
            {
                return;
            }
            foreach (QueuedCommand q in _queued)
            {
                var alive = new List<int>(q.Targets.Length);
                foreach (int id in q.Targets)
                {
                    // 受控机排除在编队接管之外；死亡/离场机器直接跳过，不报错。
                    if (_ctx.IsEligible(id) && !_ctx.IsDirectControlled(id))
                    {
                        alive.Add(id);
                    }
                }
                if (alive.Count == 0)
                {
                    continue;
                }
                StartCommandForTargets(q.Kind, q.TargetPosition, q.HostileId, alive);
            }
            _queued.Clear();
        }

        private void StartCommandForTargets(RegionCommandKind kind, Vector2 targetPosition, string hostileId, IReadOnlyList<int> targets)
        {
            int firstStarted = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                int logicId = targets[i];
                if (_ctx.IsDirectControlled(logicId) || !_ctx.IsEligible(logicId))
                {
                    continue; // 受控机排除在编队接管之外（每次真正开始执行时再核对一遍，双保险）。
                }
                if (firstStarted == 0)
                {
                    firstStarted = logicId;
                }
                _ctx.CancelWorkIfAny?.Invoke(logicId);

                HomeValleyMachineMarker marker = FindMarker(logicId);
                Vector2 startTarget = targetPosition;
                if (kind == RegionCommandKind.Guard && float.IsNaN(targetPosition.x))
                {
                    Vector3 p = marker != null ? marker.transform.position : Vector3.zero;
                    startTarget = new Vector2(p.x, p.z);
                }
                marker?.CancelCommandMove(); // 与旧"工作"移动互斥，避免两条移动来源打架。

                _active[logicId] = new ActiveCommand
                {
                    Kind = kind,
                    TargetPosition = startTarget,
                    HostileId = hostileId,
                    ArriveRadius = kind == RegionCommandKind.Guard ? GuardArriveRadius : ArriveRadius,
                    ProgressCheckTimer = ProgressCheckInterval,
                    HasLastProgressDistance = false,
                    StuckStrikes = 0,
                    AttackCooldownRemaining = 0f,
                };
            }
            PushEvent($"{KindLabel(kind)} 已下达（{targets.Count} 台）。");
            if (firstStarted != 0)
            {
                // 命令确认音按第一台接令机器的底盘区分（ChassisCatalog.SfxId 的消费点）；
                // 选择框与路径线是它的等价视觉反馈，不出字幕。
                FeedbackCues.Raise(FeedbackCueId.CommandAck, null, FeedbackCues.MachineChassisSfx(firstStarted));
            }
        }

        /// <summary>停止：清除选择集内所有机器的当前命令，交还 AI（Guard 的"取消"落点）。</summary>
        public void Stop()
        {
            _armedKind = null;
            foreach (int id in _selection)
            {
                if (_active.Remove(id))
                {
                    PushEvent($"机器 #{id} 已停止，交还 AI。");
                }
            }
        }

        /// <summary>ER5-CTL-01：接管/释放一台机器时查询它当前是否有在办战略命令及其种类——
        /// <see cref="RegionControlSystem"/> 据此判断释放时该不该自动恢复（Guard/Retreat 恢复，
        /// Move/Attack 释放前先取消，见该类 <c>ReleaseInternal</c>）。不影响 <see cref="_active"/>
        /// 本身，纯只读查询。</summary>
        public bool TryGetActiveCommandKind(int logicId, out RegionCommandKind kind)
        {
            if (_active.TryGetValue(logicId, out ActiveCommand cmd))
            {
                kind = cmd.Kind;
                return true;
            }
            kind = default;
            return false;
        }

        /// <summary>单机取消，不看选择集/不发事件日志刷屏——供区域自己的"工作"移动系统
        /// （<c>HomeValleyController</c> 的 WorkOrder/CommandWork/CommandHaul）在接管一台机器的
        /// Transform 之前调用，避免两套移动来源（旧工作移动 vs 本类的战略命令）同一帧争抢同一个
        /// <c>transform.position</c>。没有在办战略命令时是安全的 no-op。</summary>
        public void CancelCommandFor(int logicId)
        {
            _active.Remove(logicId);
        }

        // ── 每帧推进命令 ──────────────────────────────────────────────────

        private void TickActiveCommands(float dt)
        {
            if (_active.Count == 0 || dt <= 0f)
            {
                return;
            }
            var finished = new List<int>();
            var keys = new List<int>(_active.Keys);
            foreach (int logicId in keys)
            {
                if (_ctx.IsDirectControlled(logicId))
                {
                    continue; // 受控机排除在编队接管之外——本帧不推进，交还后自动恢复。
                }
                if (!_ctx.IsEligible(logicId))
                {
                    finished.Add(logicId);
                    continue;
                }
                HomeValleyMachineMarker marker = FindMarker(logicId);
                if (marker == null)
                {
                    finished.Add(logicId);
                    continue;
                }

                ActiveCommand cmd = _active[logicId];
                bool done = TickOneCommand(logicId, marker, ref cmd, dt);
                if (done)
                {
                    finished.Add(logicId);
                }
                else
                {
                    _active[logicId] = cmd;
                }
            }
            foreach (int id in finished)
            {
                _active.Remove(id);
            }
            UpdateDestinationVisual();
        }

        /// <summary>返回 true 表示命令已结束（无论成功/失败），调用方据此从 <see cref="_active"/> 摘除
        /// ——摘除即"交还 AI"：本类不持有任何机器的命令状态就意味着它回到区域自身的自由行为
        /// （归还谷地的工作分配引擎/破碎都市的静止待命），与 AC-CMD-001"交还 AI"字面一致。</summary>
        private bool TickOneCommand(int logicId, HomeValleyMachineMarker marker, ref ActiveCommand cmd, float dt)
        {
            Vector3 posV3 = marker.transform.position;
            var pos = new Vector2(posV3.x, posV3.z);

            if (cmd.Kind == RegionCommandKind.Attack)
            {
                RegionHostileInfo? hostile = _ctx.ResolveHostile?.Invoke(cmd.HostileId);
                if (hostile == null || !hostile.Value.Alive)
                {
                    PushEvent($"机器 #{logicId} 攻击目标已丢失，交还 AI。");
                    return true;
                }
                cmd.TargetPosition = hostile.Value.Position;
            }

            float distance = Vector2.Distance(pos, cmd.TargetPosition);
            bool inRange = cmd.Kind == RegionCommandKind.Attack
                ? distance <= _ctx.AttackRange
                : distance <= cmd.ArriveRadius;

            if (cmd.Kind == RegionCommandKind.Attack)
            {
                if (!inRange)
                {
                    StepTowards(marker, pos, cmd.TargetPosition, dt);
                }
                cmd.AttackCooldownRemaining -= dt;
                if (inRange && cmd.AttackCooldownRemaining <= 0f)
                {
                    cmd.AttackCooldownRemaining = _ctx.AttackCooldownSeconds;
                    RegionAttackOutcome outcome = _ctx.TryAttack != null
                        ? _ctx.TryAttack(logicId, cmd.HostileId)
                        : RegionAttackOutcome.Fail("未接入伤害结算。");
                    if (outcome.Success)
                    {
                        PushEvent(outcome.TargetDestroyed
                            ? $"机器 #{logicId} 击毁目标 {cmd.HostileId}，交还 AI。"
                            : $"机器 #{logicId} 命中 {cmd.HostileId}。");
                        if (outcome.TargetDestroyed)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        PushEvent($"机器 #{logicId} 攻击未命中：{outcome.FailureReason}");
                    }
                }
                return false;
            }

            if (inRange)
            {
                if (cmd.Kind == RegionCommandKind.Guard)
                {
                    return false; // 持久命令，到位后不结束，一直守到取消。
                }
                PushEvent($"机器 #{logicId} 已{(cmd.Kind == RegionCommandKind.Retreat ? "撤到安全点" : "到达目标")}，交还 AI。");
                return true;
            }

            bool blocked = StepTowards(marker, pos, cmd.TargetPosition, dt);
            return TrackProgressAndMaybeGiveUp(logicId, ref cmd, pos, dt, blocked);
        }

        /// <summary>朝目标推进一步，带最小局部避障；返回本帧是否检测到障碍导致绕行
        /// （供上层做"路径受阻"判定的一个信号，另一个信号是 <see cref="TrackProgressAndMaybeGiveUp"/>
        /// 的距离进展）。</summary>
        private bool StepTowards(HomeValleyMachineMarker marker, Vector2 pos, Vector2 target, float dt)
        {
            Vector2 toTarget = target - pos;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            Vector2 desired = toTarget.normalized;
            bool blocked = TryFindBlockingObstacle(pos, desired, out Vector2 obstaclePos);

            Vector2 moveDir = desired;
            if (blocked)
            {
                Vector2 toObstacle = obstaclePos - pos;
                Vector2 perp = new Vector2(-desired.y, desired.x);
                float side = Vector2.Dot(perp, toObstacle) >= 0f ? -1f : 1f;
                moveDir = (desired + perp * (side * AvoidWeight)).normalized;
            }

            Vector2 step = moveDir * (MoveSpeed * dt);
            if (step.sqrMagnitude > toTarget.sqrMagnitude)
            {
                step = toTarget;
            }
            Vector2 next = pos + step;
            marker.transform.position = new Vector3(next.x, marker.transform.position.y, next.y);
            return blocked;
        }

        private bool TryFindBlockingObstacle(Vector2 pos, Vector2 desiredDir, out Vector2 obstaclePos)
        {
            obstaclePos = default;
            if (_ctx.Obstacles == null)
            {
                return false;
            }
            float bestDist = float.MaxValue;
            bool found = false;
            foreach ((Vector2 Position, float Radius) obstacle in _ctx.Obstacles)
            {
                Vector2 toObstacle = obstacle.Position - pos;
                float along = Vector2.Dot(toObstacle, desiredDir);
                if (along <= 0f || along > LookaheadDistance)
                {
                    continue;
                }
                Vector2 closest = pos + desiredDir * along;
                float perpDist = Vector2.Distance(closest, obstacle.Position);
                if (perpDist > obstacle.Radius + MachineRadius)
                {
                    continue;
                }
                if (along < bestDist)
                {
                    bestDist = along;
                    obstaclePos = obstacle.Position;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>每 <see cref="ProgressCheckInterval"/> 秒核对一次到目标的距离是否真的在缩短；
        /// 连续 <see cref="MaxStuckStrikes"/> 次不达标（含被 <see cref="StepTowards"/> 判定为绕行中）
        /// 就判定"路径持续受阻"，放弃命令交还 AI——这是"不让机器永远站桩"的硬保证，不是靠感觉判断。
        /// 用于比较的位置取自本帧推进**之前**的坐标（每秒才评估一次，帧内位移量级远小于评估间隔，
        /// 误差可忽略，避免为了这一个数字额外多存一份"推进后坐标"）。</summary>
        private bool TrackProgressAndMaybeGiveUp(int logicId, ref ActiveCommand cmd, Vector2 pos, float dt, bool blockedThisFrame)
        {
            cmd.ProgressCheckTimer -= dt;
            if (cmd.ProgressCheckTimer > 0f)
            {
                return false;
            }
            cmd.ProgressCheckTimer = ProgressCheckInterval;

            float distanceNow = Vector2.Distance(pos, cmd.TargetPosition);
            bool madeProgress = !cmd.HasLastProgressDistance || (cmd.LastProgressDistance - distanceNow) >= MinProgressDelta;
            cmd.LastProgressDistance = distanceNow;
            cmd.HasLastProgressDistance = true;

            if (madeProgress)
            {
                cmd.StuckStrikes = 0;
                return false;
            }

            cmd.StuckStrikes++;
            if (cmd.StuckStrikes < MaxStuckStrikes)
            {
                PushEvent($"机器 #{logicId} 路径受阻，正在尝试重新绕行（第 {cmd.StuckStrikes}/{MaxStuckStrikes} 次）。");
                return false;
            }

            PushEvent($"机器 #{logicId} 路径持续受阻，已放弃命令并交还 AI。");
            FeedbackCues.Raise(FeedbackCueId.Denied, FeedbackCues.MachineLabel(logicId) + " 路径持续受阻，已放弃命令");
            return true;
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
                    UnityEngine.Object.Destroy(_selectionRings[id]);
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
                    UnityEngine.Object.Destroy(ring.GetComponent<Collider>());
                    Renderer r = ring.GetComponent<Renderer>();
                    r.material = new Material(Shader.Find("Standard")) { color = new Color(1f, 0.85f, 0.2f, 0.55f) };
                    _selectionRings[id] = ring;
                }
                Vector3 p = marker.transform.position;
                ring.transform.position = new Vector3(p.x, 0.03f, p.z);
            }
        }

        private void ClearSelectionRings()
        {
            foreach (GameObject go in _selectionRings.Values)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            _selectionRings.Clear();
        }

        private void UpdateDestinationVisual()
        {
            if (_ctx?.VisualRoot == null)
            {
                return;
            }
            // 只展示"选择集中第一台仍在执行命令的机器"的目标点/路径，避免多目标混成一团看不清——
            // 命令状态本身（RecentEvents/QueuedCommandCount）才是逐机精确的验收依据。
            ActiveCommand? shown = null;
            HomeValleyMachineMarker shownMarker = null;
            foreach (int id in _selection)
            {
                if (_active.TryGetValue(id, out ActiveCommand cmd))
                {
                    shown = cmd;
                    shownMarker = FindMarker(id);
                    break;
                }
            }

            if (shown == null || shownMarker == null)
            {
                ClearDestinationVisual();
                return;
            }

            if (_destinationMarkerGo == null)
            {
                _destinationMarkerGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _destinationMarkerGo.name = "SquadCommandDestination";
                _destinationMarkerGo.transform.SetParent(_ctx.VisualRoot.transform, false);
                _destinationMarkerGo.transform.localScale = new Vector3(0.6f, 0.03f, 0.6f);
                UnityEngine.Object.Destroy(_destinationMarkerGo.GetComponent<Collider>());
                _destinationMarkerGo.GetComponent<Renderer>().material = new Material(Shader.Find("Standard"));
            }
            if (_pathBarGo == null)
            {
                // 不用 LineRenderer（需要额外模块引用）——用一根压扁拉长的 Cube 当路径指示条，
                // 与本类其余可视化（选中环/目的地标记）同一手法：CreatePrimitive + 缩放，无新依赖。
                _pathBarGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _pathBarGo.name = "SquadCommandPath";
                _pathBarGo.transform.SetParent(_ctx.VisualRoot.transform, false);
                UnityEngine.Object.Destroy(_pathBarGo.GetComponent<Collider>());
                _pathBarGo.GetComponent<Renderer>().material = new Material(Shader.Find("Standard"));
            }

            ActiveCommand value = shown.Value;
            Color kindColor = KindColor(value.Kind);

            _destinationMarkerGo.SetActive(true);
            _destinationMarkerGo.transform.position = new Vector3(value.TargetPosition.x, 0.04f, value.TargetPosition.y);
            _destinationMarkerGo.GetComponent<Renderer>().material.color = kindColor;

            Vector3 from = shownMarker.transform.position;
            var to = new Vector3(value.TargetPosition.x, from.y, value.TargetPosition.y);
            Vector3 mid = (from + to) * 0.5f;
            mid.y = 0.05f;
            float length = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(to.x, 0f, to.z));
            _pathBarGo.SetActive(true);
            _pathBarGo.transform.position = mid;
            _pathBarGo.transform.rotation = Quaternion.LookRotation(to - from, Vector3.up);
            _pathBarGo.transform.localScale = new Vector3(0.15f, 0.02f, Mathf.Max(0.01f, length));
            _pathBarGo.GetComponent<Renderer>().material.color = kindColor;
        }

        private void ClearDestinationVisual()
        {
            if (_destinationMarkerGo != null)
            {
                UnityEngine.Object.Destroy(_destinationMarkerGo);
                _destinationMarkerGo = null;
            }
            if (_pathBarGo != null)
            {
                UnityEngine.Object.Destroy(_pathBarGo);
                _pathBarGo = null;
            }
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
