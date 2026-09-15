using System.Collections.Generic;
using BinGames.Sim;

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

        /// <summary>编队教义占位，只是字段，不实现任何行为差异（见 <see cref="FormationDoctrine"/>）。</summary>
        public FormationDoctrine Doctrine { get; set; } = FormationDoctrine.None;

        private readonly HashSet<SimEntityId> _members = new HashSet<SimEntityId>();
        private readonly HashSet<SimEntityId> _detachedMembers = new HashSet<SimEntityId>();
        private readonly Queue<FormationCommand> _commands = new Queue<FormationCommand>();

        public IReadOnlyCollection<SimEntityId> Members => _members;
        public IReadOnlyCollection<SimEntityId> DetachedMembers => _detachedMembers;

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

        /// <summary>命令队列占位：只提供 Enqueue/Peek/Count，不做去重/覆盖/中断/失败原因（M4-02 的事）。</summary>
        public void EnqueueCommand(FormationCommand command)
        {
            _commands.Enqueue(command);
        }

        public bool PeekCommand(out FormationCommand command)
        {
            if (_commands.Count == 0)
            {
                command = default;
                return false;
            }

            command = _commands.Peek();
            return true;
        }

        public int PendingCommandCount => _commands.Count;
    }
}
