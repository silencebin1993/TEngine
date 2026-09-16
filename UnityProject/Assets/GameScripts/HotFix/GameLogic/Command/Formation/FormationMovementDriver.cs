using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-04：编队共享路径的移动驱动器——遍历 <see cref="FormationRegistry.AllFormations"/>，
    /// 对 <see cref="FormationCommand.CommandKind.Move"/>/<see cref="FormationCommand.CommandKind.Retreat"/>
    /// 类 Active 命令，用 <see cref="FormationPathPlanner"/> 算出的共享路径 + <see cref="FormationFollowSlots"/>
    /// 的跟随槽偏移，给每个成员各自下发一条 <see cref="UnitCommand.Move"/>/<see cref="UnitCommand.Retreat"/>。
    ///
    /// 本类型只是"薄封装"：真正的路点推进状态机在不依赖 <c>SimBridge</c> 的
    /// <see cref="FormationMemberMotion"/> 里（关键提醒第 2 条），这里只做
    /// "读 SimBridge 位置 → 喂给纯逻辑 → 把结果写回 SimBridge" 的接线。
    ///
    /// <b>Priority 选择</b>：必须小于 <see cref="ModulePriority.Simulation"/>（内核提交/推进的那一帧），
    /// 确保本帧算出的目标点能被同一帧的 <see cref="SimBridge"/> 提交消费，而不是拖到下一帧才生效。
    /// 用 <c>Simulation - 10</c> 而不是新增命名常量：与 <see cref="ModulePriority.Structural"/>
    /// 相对 <see cref="ModulePriority.Cards"/> 的"+10 相邻档位"写法同一风格，不改
    /// <c>IGameModule.cs</c>（本 story 允许改动的文件范围不含它）。
    /// </summary>
    public sealed class FormationMovementDriver : GameModuleBase
    {
        public override int Priority => ModulePriority.Simulation - 10;

        /// <summary>路径规划时给障碍物留的余量。取一个与单位半径同量级的经验值——
        /// 本 story 不新增配置项，见 D8"不做真实关卡内容"的精神：这只是让路径不贴脸擦过障碍。</summary>
        private const float PathClearance = 1.0f;

        /// <summary>到达判定半径，对齐 <c>SquadCommandSystem.DefaultArriveRadius</c> 量级。</summary>
        private const float ArriveRadius = 1.2f;

        /// <summary>D5：连续位移低于这个阈值达到 <see cref="StuckTimeThreshold"/> 秒即判定"卡住"。</summary>
        private const float StuckDistanceThreshold = 0.2f;

        /// <summary>D5：卡住判定所需的连续低位移时长（秒）。</summary>
        private const float StuckTimeThreshold = 2f;

        /// <summary>D5：同一条 Active 命令允许的重规划次数上限，超过仍卡住则判定失败。</summary>
        private const int MaxReplans = 2;

        /// <summary>M4-R00-02 队列③-11（FC-REQ-003）：连续算不出有效锚点的最大重试帧数——
        /// 给成员刚落地那一两帧的正常延迟留缓冲，超过则判定命令失败并清理所有权，不再无限重试。
        /// 0.5s@60fps，与 D5 的 <see cref="StuckTimeThreshold"/> 不同量级（那是"移动中卡住"，
        /// 这是"起手就没有可用锚点"，理应更快判失败）。</summary>
        private const int MaxAnchorAttempts = 30;

        private FormationRegistry _formations;
        private SimBridge _sim;

        /// <summary>M4-05：直控接管/退出信号订阅。用 <see cref="SignalScope"/> 统一退订，
        /// 写法照抄 <see cref="Cards.CardTriggerBus"/> 的 <c>GameModuleBase</c> 信号绑定范式。</summary>
        private SignalScope _scope;

        private sealed class MemberRuntimeState
        {
            public int WaypointIndex;
            public float2 LastPosition;
            public float StuckTimer;
        }

        private sealed class FormationRuntimeState
        {
            public List<float2> Path;
            public int ReplanCount;
            /// <summary>路径规划那一刻的成员数快照，用于跟随槽公式——运行途中再有成员加入/离队
            /// 不重新洗牌已分配的槽位序号，避免视觉抖动。</summary>
            public int MemberCount;
            public int NextSlotIndex;
            public readonly Dictionary<SimEntityId, int> SlotIndexByMember = new Dictionary<SimEntityId, int>();
            public readonly Dictionary<SimEntityId, MemberRuntimeState> Members = new Dictionary<SimEntityId, MemberRuntimeState>();
        }

        private readonly Dictionary<string, FormationRuntimeState> _runtime = new Dictionary<string, FormationRuntimeState>();
        /// <summary>M4-R00-02 队列③-11：连续算不出锚点的帧数，按 formation id 记。首次规划成功
        /// 或命令不再活跃时清掉——不是跨命令累计的“黑历史”。</summary>
        private readonly Dictionary<string, int> _anchorFailStreak = new Dictionary<string, int>();

        public override void OnInit(ModuleHub hub)
        {
            base.OnInit(hub);
            _formations = hub.Require<FormationRegistry>();
            _sim = hub.Require<SimBridge>();
        }

        /// <summary>M4-05：真订阅 <see cref="ControlledUnitChangedSignal"/>——这条线的核心价值就是
        /// 真的接上，不允许绕过成"自检里手动调用模拟信号处理函数"。</summary>
        public override void OnEnter()
        {
            base.OnEnter();
            _scope = new SignalScope();
            _scope.On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
        }

        public override void OnExit()
        {
            base.OnExit();
            _scope?.Dispose();
            _scope = null;
        }

        /// <summary>M4-05 唯一的信号处理逻辑：接管方脱队、退出方回归。**只允许调用
        /// <see cref="Formation.SetDetached"/>**，不得调用任何其它 <see cref="Formation"/>/
        /// <see cref="FormationRegistry"/> 写方法——这是守住"不清空命令/不破坏搬运所有权"验收的唯一方式
        /// （见 preflight-decisions.md D1）。找不到所属编队时（<see cref="FormationRegistry.FindFormationContaining"/>
        /// 返回 null）两步都自然 no-op。</summary>
        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            if (_formations == null)
            {
                return;
            }

            if (signal.PreviousUnitId.IsValid)
            {
                Formation previousFormation = _formations.FindFormationContaining(signal.PreviousUnitId);
                previousFormation?.SetDetached(signal.PreviousUnitId, false);
            }

            if (signal.CurrentUnitId.IsValid)
            {
                Formation currentFormation = _formations.FindFormationContaining(signal.CurrentUnitId);
                currentFormation?.SetDetached(signal.CurrentUnitId, true);
            }
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _formations == null)
            {
                return;
            }

            foreach (Formation formation in _formations.AllFormations)
            {
                DriveFormation(formation, dt);
            }
        }

        private void DriveFormation(Formation formation, float dt)
        {
            FormationCommandEntry active = formation.ActiveCommand;
            if (active == null || active.State != FormationCommandState.Active)
            {
                _runtime.Remove(formation.Id);
                _anchorFailStreak.Remove(formation.Id);
                return;
            }

            FormationCommand command = active.Command;
            bool isMoveLike = command.Kind == FormationCommand.CommandKind.Move
                || command.Kind == FormationCommand.CommandKind.Retreat;
            if (!isMoveLike || !command.TargetPosition.HasValue)
            {
                // 非移动类命令（Guard/Attack/…）不归本驱动器管；若之前是移动类而现在被覆盖成
                // 别的命令，清掉残留的运行时状态，避免下次同 id 复用时读到旧路径。
                _runtime.Remove(formation.Id);
                _anchorFailStreak.Remove(formation.Id);
                return;
            }

            if (!_runtime.TryGetValue(formation.Id, out FormationRuntimeState state))
            {
                state = new FormationRuntimeState();
                if (!TryPlanInitialPath(formation, command.TargetPosition.Value, state))
                {
                    // M4-R00-02 队列③-11（FC-REQ-003）："锚点无有效成员时命令失败并清理所有权"——
                    // 但成员刚落地那一两帧本来就查不到位置是正常瞬时情形，先给 MaxAnchorAttempts
                    // 帧的缓冲，仍然算不出来才真的判失败（FailActiveCommand 会顺带清空等待队列首、
                    // 尝试提升下一条排队命令，就是"清理所有权"的落地动作）。
                    int attempts = _anchorFailStreak.TryGetValue(formation.Id, out int a) ? a + 1 : 1;
                    _anchorFailStreak[formation.Id] = attempts;
                    if (attempts >= MaxAnchorAttempts)
                    {
                        _anchorFailStreak.Remove(formation.Id);
                        formation.FailActiveCommand(FormationCommandFailReason.NoValidAnchor);
                    }
                    return;
                }
                _anchorFailStreak.Remove(formation.Id);
                _runtime[formation.Id] = state;
            }

            DriveMembers(formation, command, state, dt);
        }

        private bool TryPlanInitialPath(Formation formation, float2 goal, FormationRuntimeState state)
        {
            if (!formation.TryComputeAnchor(_sim, out float2 anchor))
            {
                return false;
            }

            state.Path = FormationPathPlanner.Plan(anchor, goal, ObstaclesOrEmpty(), PathClearance);
            state.MemberCount = formation.Members.Count;
            state.ReplanCount = 0;
            state.NextSlotIndex = 0;
            state.SlotIndexByMember.Clear();
            state.Members.Clear();
            return true;
        }

        private void DriveMembers(Formation formation, FormationCommand command, FormationRuntimeState state, float dt)
        {
            bool allArrived = true;
            bool anyResolved = false;

            foreach (SimEntityId member in formation.Members)
            {
                // M4-05 D2：直控临时脱队的成员整段跳过（含卡死计时器/StuckTracker 状态），
                // 防止玩家直控走远时被误判"卡住"进而触发重规划；取消脱队后下一帧自然重新进入
                // 本遍历，纯状态机按当前实际位置重算目标，"回到合理队形"因此自然发生，不需要额外的
                // "传送归队"逻辑。
                if (formation.IsDetached(member))
                {
                    continue;
                }

                if (!_sim.TryGetPosition(member, out float2 pos))
                {
                    continue;
                }

                anyResolved = true;

                if (!state.SlotIndexByMember.TryGetValue(member, out int slotIndex))
                {
                    slotIndex = state.NextSlotIndex++;
                    state.SlotIndexByMember[member] = slotIndex;
                }

                if (!state.Members.TryGetValue(member, out MemberRuntimeState memberState))
                {
                    memberState = new MemberRuntimeState { WaypointIndex = 0, LastPosition = pos, StuckTimer = 0f };
                    state.Members[member] = memberState;
                }

                float2 slotOffset = ResolveWorldSlotOffset(state.Path, memberState.WaypointIndex, slotIndex, state.MemberCount);
                FormationMemberMotion.StepResult step = FormationMemberMotion.Step(
                    state.Path, memberState.WaypointIndex, pos, slotOffset, ArriveRadius);
                memberState.WaypointIndex = step.NextWaypointIndex;

                if (!step.Arrived)
                {
                    allArrived = false;
                    _sim.IssueCommand(new[] { member }, new UnitCommand
                    {
                        Kind = command.Kind == FormationCommand.CommandKind.Retreat
                            ? UnitCommandKind.Retreat
                            : UnitCommandKind.Move,
                        TargetPosition = step.TargetPoint,
                        ArriveRadius = ArriveRadius,
                    });
                }

                UpdateStuckTracking(formation, command, state, memberState, member, pos, dt);
            }

            if (anyResolved && allArrived)
            {
                _runtime.Remove(formation.Id);
                formation.CompleteActiveCommand();
            }
        }

        /// <summary>把 <see cref="FormationFollowSlots"/> 算出的"路径局部坐标"偏移旋转到世界坐标——
        /// 用当前路点所在线段的方向当轴。</summary>
        private static float2 ResolveWorldSlotOffset(List<float2> path, int waypointIndex, int slotIndex, int memberCount)
        {
            float2 local = FormationFollowSlots.ComputeOffset(slotIndex, memberCount);
            float2 dir = ResolvePathDirection(path, waypointIndex);
            float2 perp = new float2(-dir.y, dir.x);
            return dir * local.x + perp * local.y;
        }

        private static float2 ResolvePathDirection(List<float2> path, int waypointIndex)
        {
            if (path == null || path.Count < 2)
            {
                return new float2(1f, 0f);
            }

            int from = math.clamp(waypointIndex - 1, 0, path.Count - 2);
            float2 delta = path[from + 1] - path[from];
            return math.normalizesafe(delta, new float2(1f, 0f));
        }

        /// <summary>D5：卡死检测与重规划边界。判定本身（要不要重规划/要不要判失败）走纯逻辑
        /// <see cref="FormationStuckTracker"/>，这里只负责"判定为 Replan 时真的去重新规划路径、
        /// 判定为 Fail 时真的调用 FailActiveCommand"这两个需要碰 SimBridge/Formation 的落地动作。
        ///
        /// M4-R00-02 队列③-11（FC-REQ-003）：同时把结果写进 <see cref="Formation.SetStuck"/>——
        /// <see cref="FormationStuckTracker"/> 判定为 Replan/Fail 那一刻会把自己的计时器清零重新计
        /// （见该类注释），所以"这个成员现在算不算卡住"这个**持久到恢复移动前都成立**的事实，
        /// 不能从瞬时计时器读出来，必须单独存一份（同 <see cref="Formation.SetDetached"/> 的理由）。</summary>
        private void UpdateStuckTracking(Formation formation, FormationCommand command, FormationRuntimeState state,
            MemberRuntimeState memberState, SimEntityId member, float2 pos, float dt)
        {
            float moved = math.distance(pos, memberState.LastPosition);
            memberState.LastPosition = pos;

            if (moved >= StuckDistanceThreshold)
            {
                formation.SetStuck(member, false);
            }

            FormationStuckTracker.Outcome outcome = FormationStuckTracker.Evaluate(
                ref memberState.StuckTimer, ref state.ReplanCount, moved, dt,
                StuckDistanceThreshold, StuckTimeThreshold, MaxReplans);

            if (outcome == FormationStuckTracker.Outcome.Ok)
            {
                return;
            }

            formation.SetStuck(member, true);

            if (outcome == FormationStuckTracker.Outcome.Fail)
            {
                _runtime.Remove(formation.Id);
                formation.FailActiveCommand(FormationCommandFailReason.Stuck);
                return;
            }

            // Outcome.Replan：起点用当前锚点而不是原来出发时的锚点；锚点算不出来时退化用触发
            // 重规划的这个成员的位置兜底，好过整段放弃。
            if (!command.TargetPosition.HasValue)
            {
                return;
            }

            if (!formation.TryComputeAnchor(_sim, out float2 anchor))
            {
                anchor = pos;
            }

            state.Path = FormationPathPlanner.Plan(anchor, command.TargetPosition.Value, ObstaclesOrEmpty(), PathClearance);
            foreach (MemberRuntimeState m in state.Members.Values)
            {
                m.WaypointIndex = 0;
            }
        }

        private ObstacleSpec[] ObstaclesOrEmpty()
        {
            return _sim.Obstacles ?? System.Array.Empty<ObstacleSpec>();
        }
    }
}
