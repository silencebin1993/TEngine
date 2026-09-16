namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-R00-02 队列④-14（FC-REQ-011）：命令优先级的封闭 7 级枚举，替代此前的裸
    /// <c>int Priority</c>。数值越大越优先激活（<see cref="Formation.EnqueueCommand"/>/
    /// <see cref="Formation.IssueCommand"/> 均按此排序/判定覆盖资格），顺序与
    /// <c>DesignDocs/detailed/03_Formation_Command_Pathfinding_And_Doctrine.md</c>
    /// FC-REQ-011 原文「默认从高到低」的 1~7 条一一对应（数值取反：原文序号 1 对应本枚举最大值）。
    /// </summary>
    public enum FormationCommandPriority
    {
        /// <summary>原文 7：自主生存/待机——没有更高优先级命令时的兜底态，也是
        /// <see cref="FormationCommand"/> 未显式指定 priority 时的构造默认值。</summary>
        AutonomousSurvival = 0,

        /// <summary>原文 6：教义响应。</summary>
        DoctrineResponse = 1,

        /// <summary>原文 5：普通玩家命令。</summary>
        NormalPlayerCommand = 2,

        /// <summary>原文 4：正在完成的不可瞬断事务阶段（放下关键物、退出接口、手术收尾）。</summary>
        UninterruptibleFinishing = 3,

        /// <summary>原文 3：玩家显式插队命令。</summary>
        PlayerQueueJump = 4,

        /// <summary>原文 2：当前直控玩家意图（只影响被接管成员）。</summary>
        DirectControlIntent = 5,

        /// <summary>原文 1：玩家显式紧急撤退/取消——最高优先级。</summary>
        PlayerEmergency = 6,
    }
}
