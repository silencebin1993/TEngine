using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-01：编队领域模型。保存编队成员、命令、教义与临时脱队状态的持久载体
    /// （与 <see cref="SquadCommandSystem"/> 的 <c>_groups</c> 不是一回事——那是纯 UI 选择/编组的
    /// 临时态，内核不认识"编队"这个概念；本类型是不同量级的持久领域对象）。
    ///
    /// **核心边界（不是可选项）**：本类型绝不引用任何 <c>GameLogic.MetabolicSlice.*</c> 类型，
    /// 只存 <see cref="SimEntityId"/>——这是"模板变化不改变编队身份"这条验收成立的根本原因：
    /// 压根没有引用关系可被模板层的提交动作波及。
    /// </summary>
    public sealed class Formation
    {
        public string Id { get; }

        /// <summary>编队教义字段（见 <see cref="FormationDoctrine"/>）。</summary>
        public FormationDoctrine Doctrine { get; set; } = FormationDoctrine.None;

        /// <summary>M4-03：当前教义对应的参数三元组（目标选择/交战距离/撤退阈值）。
        /// **只读计算属性，不缓存**——<see cref="Doctrine"/> 变化后立即反映最新值，这是
        /// "相同编队切换教义后目标选择/距离/撤退阈值按定义变化"这条验收成立的关键，
        /// 不允许改成构造时缓存的字段。</summary>
        public FormationDoctrineProfile CurrentDoctrineProfile => FormationDoctrineProfile.For(Doctrine);

        private readonly HashSet<SimEntityId> _members = new HashSet<SimEntityId>();
        private readonly HashSet<SimEntityId> _detachedMembers = new HashSet<SimEntityId>();
        /// <summary>M4-R00-02 队列③-11（FC-REQ-003）：当前被判定为"持续卡住"的成员（由
        /// <see cref="FormationMovementDriver"/> 的卡死检测写入，见 <see cref="SetStuck"/>）。
        /// 只用于 <see cref="TryComputeAnchor"/> 排除——不是"卡住"本身的判定逻辑，判定逻辑在
        /// <see cref="FormationStuckTracker"/>，这里只存持久到"恢复移动前"都成立的结果。</summary>
        private readonly HashSet<SimEntityId> _stuckMembers = new HashSet<SimEntityId>();

        /// <summary>M4-R00-02 队列③-11（FC-REQ-003）：编队领队——加入的第一名成员自动成为领队，
        /// 领队离队/被移除时自动顺位给剩余成员中的任意一名（规格未要求指定继任规则，只要求
        /// "锚点优先取有效领队"这件事成立）。没有公开的 SetLeader：今天没有任何生产入口需要显式
        /// 指定领队，加一个没人调用的公开写口子是负债，不是能力——需要时再开。</summary>
        public SimEntityId LeaderId { get; private set; } = SimEntityId.None;

        /// <summary>M4-02：等待队列，按 Priority 降序排列（同优先级按入队顺序，稳定排序），
        /// 只含尚未激活的命令——<see cref="ActiveCommand"/> 不在这个列表里。这是 M4-01
        /// "EnqueueCommand 绝不自动激活"这条回归约束（见 CellFrameworkValidate [33] 验收 5）
        /// 成立的根本原因：底层换成 List 只是为了排序，Enqueue/Peek/Count 三个公开方法对外行为不变。</summary>
        private readonly List<FormationCommandEntry> _pendingCommands = new List<FormationCommandEntry>();

        public IReadOnlyCollection<SimEntityId> Members => _members;
        public IReadOnlyCollection<SimEntityId> DetachedMembers => _detachedMembers;

        /// <summary>M4-02：当前正在执行的命令，空闲时为 null。</summary>
        public FormationCommandEntry ActiveCommand { get; private set; }

        public Formation(string id)
        {
            Id = id;
        }

        /// <summary>混编：不同来源的单位（不同 Lineage/不同模板/野生个体……）都能加入同一队，
        /// 因为这里只认 <see cref="SimEntityId"/>，与来源无关。</summary>
        public bool AddMember(SimEntityId entity)
        {
            if (!entity.IsValid)
            {
                return false;
            }

            bool wasEmpty = _members.Count == 0;
            if (!_members.Add(entity))
            {
                return false;
            }

            if (wasEmpty)
            {
                LeaderId = entity;
            }

            return true;
        }

        /// <summary>移除成员时联动清掉脱队/卡住标记，防止 stale 状态残留（同 D6 语义）。
        /// 移除的是当前领队时顺位给剩余成员中的任意一名，全体移空则清空领队。</summary>
        public bool RemoveMember(SimEntityId entity)
        {
            bool removed = _members.Remove(entity);
            _detachedMembers.Remove(entity);
            _stuckMembers.Remove(entity);

            if (removed && entity == LeaderId)
            {
                LeaderId = SimEntityId.None;
                foreach (SimEntityId remaining in _members)
                {
                    LeaderId = remaining;
                    break;
                }
            }

            return removed;
        }

        public bool IsMember(SimEntityId entity)
        {
            return _members.Contains(entity);
        }

        /// <summary>
        /// M4-04 + M4-R00-02 队列③-11（FC-REQ-003）：编队锚点。
        ///
        /// 优先级：①有效领队（<see cref="LeaderId"/> 仍是成员、未脱队、未卡住、查得到位置）
        /// 直接取它的位置；②否则取未脱队且未卡住存活成员的稳健中心——"稳健"体现在排除脱队/卡死
        /// 这两类会把锚点拽偏的成员，不是对全员坐标做统计学离群值剔除（规格原文的"排除极端失散
        /// 成员"就是"脱队"的同义表述，不是要求另一套算法）。
        ///
        /// 旧实现对**全体**成员（含脱队/卡死）做算术平均——一个卡在障碍里的成员会把整队锚点
        /// 拽偏，进而拽偏整队重规划路径的起点，这正是 FC-REQ-003 冲突判定点名的症状。
        ///
        /// 全部候选都查不到位置（比如都已死亡/未加入战场）时返回 false，调用方按"命令失败并
        /// 清理所有权"处理（见 <see cref="FormationMovementDriver"/>），不能无限重试。
        /// </summary>
        public bool TryComputeAnchor(SimBridge sim, out float2 anchor)
        {
            anchor = float2.zero;
            if (sim == null || _members.Count == 0)
            {
                return false;
            }

            if (LeaderId.IsValid && _members.Contains(LeaderId) &&
                !_detachedMembers.Contains(LeaderId) && !_stuckMembers.Contains(LeaderId) &&
                sim.TryGetPosition(LeaderId, out float2 leaderPos))
            {
                anchor = leaderPos;
                return true;
            }

            float2 sum = float2.zero;
            int counted = 0;
            foreach (SimEntityId member in _members)
            {
                if (_detachedMembers.Contains(member) || _stuckMembers.Contains(member))
                {
                    continue;
                }
                if (sim.TryGetPosition(member, out float2 pos))
                {
                    sum += pos;
                    counted++;
                }
            }

            if (counted == 0)
            {
                return false;
            }

            anchor = sum / counted;
            return true;
        }

        /// <summary>临时脱队：脱队者仍在 <see cref="Members"/> 里（脱队不等于移除）。
        /// 非成员调用一律 no-op，不允许把非成员标记为脱队。</summary>
        public void SetDetached(SimEntityId entity, bool detached)
        {
            if (!_members.Contains(entity))
            {
                return;
            }

            if (detached)
            {
                _detachedMembers.Add(entity);
            }
            else
            {
                _detachedMembers.Remove(entity);
            }
        }

        public bool IsDetached(SimEntityId entity)
        {
            return _detachedMembers.Contains(entity);
        }

        /// <summary>M4-R00-02 队列③-11（FC-REQ-003）：持续卡住标记，供 <see cref="TryComputeAnchor"/>
        /// 排除。由 <see cref="FormationMovementDriver"/> 的卡死检测写入——判定为 Replan/Fail 时
        /// 置 true，成员重新有实质位移时置 false。非成员调用一律 no-op（同 <see cref="SetDetached"/>）。</summary>
        public void SetStuck(SimEntityId entity, bool stuck)
        {
            if (!_members.Contains(entity))
            {
                return;
            }

            if (stuck)
            {
                _stuckMembers.Add(entity);
            }
            else
            {
                _stuckMembers.Remove(entity);
            }
        }

        public bool IsStuck(SimEntityId entity)
        {
            return _stuckMembers.Contains(entity);
        }

        /// <summary>排队：按 Priority 降序插入等待队列（同优先级按入队顺序，稳定排序），
        /// 绝不碰 <see cref="ActiveCommand"/>——不自动激活是 [33] 验收 5 的硬约束。</summary>
        public void EnqueueCommand(FormationCommand command)
        {
            var entry = new FormationCommandEntry(command);
            int insertIndex = _pendingCommands.Count;
            for (int i = 0; i < _pendingCommands.Count; i++)
            {
                if (_pendingCommands[i].Command.Priority < command.Priority)
                {
                    insertIndex = i;
                    break;
                }
            }

            _pendingCommands.Insert(insertIndex, entry);
        }

        /// <summary>等待队首（不出队）。只看等待队列，不含 <see cref="ActiveCommand"/>——M4-01 语义不变。</summary>
        public bool PeekCommand(out FormationCommand command)
        {
            if (_pendingCommands.Count == 0)
            {
                command = default;
                return false;
            }

            command = _pendingCommands[0].Command;
            return true;
        }

        public int PendingCommandCount => _pendingCommands.Count;

        /// <summary>覆盖：若已有 Active 命令，先标记 <see cref="FormationCommandState.Interrupted"/>/
        /// <see cref="FormationCommandFailReason.PreemptedByOverride"/> 并清空等待队列（对齐
        /// SquadCommandSystem 现有 UX——新下令替换旧排队，不是追加），再把新命令直接设为 Active。
        /// 无 Active 命令时直接激活，不需要"中断"动作。
        ///
        /// M4-R00-02 队列④-14（FC-REQ-011）：新增优先级守卫——当前 Active 命令的
        /// <see cref="FormationCommand.Priority"/> 严格高于新命令时，新命令不能覆盖它，改为按
        /// <see cref="EnqueueCommand"/> 排序规则插入等待队列，返回
        /// <see cref="FormationCommandIssueResult.QueuedBehindHigherPriority"/>；这种情况下不清空
        /// 既有等待队列、不触碰 <see cref="ActiveCommand"/>。优先级相等或更高时维持原有的覆盖语义
        /// （相等仍可覆盖，保持既有默认优先级调用点的行为不回归）。</summary>
        public FormationCommandIssueResult IssueCommand(FormationCommand command)
        {
            if (ActiveCommand != null && ActiveCommand.State == FormationCommandState.Active
                && ActiveCommand.Command.Priority > command.Priority)
            {
                EnqueueCommand(command);
                return FormationCommandIssueResult.QueuedBehindHigherPriority;
            }

            if (ActiveCommand != null && ActiveCommand.State == FormationCommandState.Active)
            {
                ActiveCommand.State = FormationCommandState.Interrupted;
                ActiveCommand.FailReason = FormationCommandFailReason.PreemptedByOverride;
            }

            _pendingCommands.Clear();

            var entry = new FormationCommandEntry(command)
            {
                State = FormationCommandState.Active,
            };
            ActiveCommand = entry;
            return FormationCommandIssueResult.Activated;
        }

        /// <summary>中断：仅在存在 Active 命令时生效（否则安全 no-op），随后尝试提升等待队列队首。</summary>
        public void InterruptActiveCommand(FormationCommandFailReason reason = FormationCommandFailReason.Cancelled)
        {
            if (ActiveCommand == null || ActiveCommand.State != FormationCommandState.Active)
            {
                return;
            }

            ActiveCommand.State = FormationCommandState.Interrupted;
            ActiveCommand.FailReason = reason;
            TryActivateNextPending();
        }

        /// <summary>完成：仅在存在 Active 命令时生效（否则安全 no-op），随后尝试提升等待队列队首。</summary>
        public void CompleteActiveCommand()
        {
            if (ActiveCommand == null || ActiveCommand.State != FormationCommandState.Active)
            {
                return;
            }

            ActiveCommand.State = FormationCommandState.Completed;
            TryActivateNextPending();
        }

        /// <summary>失败：仅在存在 Active 命令时生效（否则安全 no-op），随后尝试提升等待队列队首。
        /// Attack/OrganCategory 目标死亡的自动失败通过这个方法接线，见
        /// <see cref="FormationRegistry.HandleMemberDeath"/>。</summary>
        public void FailActiveCommand(FormationCommandFailReason reason)
        {
            if (ActiveCommand == null || ActiveCommand.State != FormationCommandState.Active)
            {
                return;
            }

            ActiveCommand.State = FormationCommandState.Failed;
            ActiveCommand.FailReason = reason;
            TryActivateNextPending();
        }

        /// <summary>把等待队列队首提升为 Active 并从队列移除；仅在当前没有处于 Active 状态的命令时生效
        /// （已有 Active 命令时返回 false，不会静默覆盖）。Interrupt/Complete/Fail 内部都会调用它；
        /// 外部也可以在编队原本空闲、想把已排队的第一条命令激活时主动调用。</summary>
        public bool TryActivateNextPending()
        {
            if (ActiveCommand != null && ActiveCommand.State == FormationCommandState.Active)
            {
                return false;
            }

            if (_pendingCommands.Count == 0)
            {
                return false;
            }

            FormationCommandEntry next = _pendingCommands[0];
            _pendingCommands.RemoveAt(0);
            next.State = FormationCommandState.Active;
            ActiveCommand = next;
            return true;
        }
    }
}
