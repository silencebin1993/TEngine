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
        /// <summary>
        /// M4-02：每种命令的「确定结束条件」规则（D4）——每一种都必须有明确、可验证的规则，
        /// 但不是每一种都要求本 story 就实现自动触发：
        ///
        /// - <see cref="Move"/>/<see cref="Retreat"/>/<see cref="Carry"/>：目标达成需由调用方
        ///   （后续 M4-04 共享路径 / 实际搬运逻辑接线时）在检测到条件满足后调用
        ///   <see cref="Formation.CompleteActiveCommand"/>；未达成/中途失败调用
        ///   <see cref="Formation.FailActiveCommand"/>。本 story 不轮询位置、不新增内核查询。
        /// - <see cref="Attack"/>/<see cref="OrganCategory"/>：**必须自动失败**——
        ///   <see cref="TargetEntity"/> 死亡时自动 <see cref="Formation.FailActiveCommand"/>
        ///   （<see cref="FormationCommandFailReason.InvalidTarget"/>），接线在
        ///   <see cref="FormationRegistry.HandleMemberDeath"/>；主动判定"胜利"仍由调用方按需
        ///   <see cref="Formation.CompleteActiveCommand"/>。
        /// - <see cref="Guard"/>/<see cref="Occupy"/>/<see cref="Ambush"/>：站桩型命令，唯一确定结束
        ///   方式是被覆盖（<see cref="Formation.IssueCommand"/>）或显式
        ///   中断/失败/完成——"永不自动结束，只能被主动终止"本身就是它们的确定性规则，不需要额外机制。
        /// </summary>
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

        /// <summary>M4-02：等待队列排序权重，数值越大越优先激活（<see cref="Formation.TryActivateNextPending"/>）。
        /// 追加在构造函数末位、默认值 0，不破坏既有具名参数调用写法。</summary>
        public readonly int Priority;

        public FormationCommand(CommandKind kind, SimEntityId? targetEntity = null, float2? targetPosition = null, int priority = 0)
        {
            Kind = kind;
            TargetEntity = targetEntity;
            TargetPosition = targetPosition;
            Priority = priority;
        }
    }
}
