using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-01：编队命令占位记录。只描述"要做什么"，不做去重/覆盖/中断/失败原因——
    /// 那是命令队列真正落地时（M4-02）的事。目标字段全部可选：
    /// <see cref="TargetEntity"/> 用于以单位为目标的命令（如 Attack/Guard），
    /// <see cref="TargetPosition"/> 用于以坐标为目标的命令（如 Move/Occupy）。
    /// </summary>
    public readonly struct FormationCommand
    {
        public enum CommandKind
        {
            Move,
            Attack,
            OrganCategory,
            Guard,
            Carry,
            Occupy,
            Ambush,
            Retreat,
        }

        public readonly CommandKind Kind;
        public readonly SimEntityId? TargetEntity;
        public readonly float2? TargetPosition;

        public FormationCommand(CommandKind kind, SimEntityId? targetEntity = null, float2? targetPosition = null)
        {
            Kind = kind;
            TargetEntity = targetEntity;
            TargetPosition = targetPosition;
        }
    }
}
