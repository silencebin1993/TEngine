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
            return entity.IsValid && _members.Add(entity);
        }

        /// <summary>移除成员时联动清掉脱队标记，防止 stale 状态残留（同 D6 语义）。</summary>
        public bool RemoveMember(SimEntityId entity)
        {
            bool removed = _members.Remove(entity);
            _detachedMembers.Remove(entity);
            return removed;
        }

        public bool IsMember(SimEntityId entity)
        {
            return _members.Contains(entity);
        }

        /// <summary>M4-04：编队锚点——成员当前位置的算术平均（D3）。O(成员数) 次
        /// <see cref="SimBridge.TryGetPosition"/> 查询，调用方按需传入 <paramref name="sim"/>；
        /// Formation 本身不持有内核引用（同类型注释开头的解耦边界——只存 <see cref="SimEntityId"/>，
        /// SimBridge 只是"按需借用一次"，不缓存）。全部成员都查不到位置（比如都已死亡/未加入
        /// 战场）时返回 false。</summary>
        public bool TryComputeAnchor(SimBridge sim, out float2 anchor)
        {
            anchor = float2.zero;
            if (sim == null || _members.Count == 0)
            {
                return false;
            }

            float2 sum = float2.zero;
            int counted = 0;
            foreach (SimEntityId member in _members)
            {
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
        /// 无 Active 命令时直接激活，不需要"中断"动作。</summary>
        public void IssueCommand(FormationCommand command)
        {
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
