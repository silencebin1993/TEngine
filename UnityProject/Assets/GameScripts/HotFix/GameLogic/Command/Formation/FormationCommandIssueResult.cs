namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-R00-02 队列④-14（FC-REQ-011）：<see cref="Formation.IssueCommand"/> 的结果——
    /// 原文「低优先级不能删除高优先级命令；只能排队或返回冲突原因」在这里落地成一个显式返回值，
    /// 调用方（UI/AI）据此决定要不要告诉玩家"命令已排队等待，不是立即生效"。
    /// </summary>
    public enum FormationCommandIssueResult
    {
        /// <summary>新命令的优先级不低于当前 Active 命令（或当前没有 Active 命令），
        /// 已按既有覆盖语义直接成为新的 <see cref="Formation.ActiveCommand"/>。</summary>
        Activated,

        /// <summary>当前 Active 命令优先级更高，新命令未能覆盖它，已改为按
        /// <see cref="Formation.EnqueueCommand"/> 的排序规则插入等待队列——
        /// 这就是 FC-REQ-011「只能排队」那一半的落地方式。</summary>
        QueuedBehindHigherPriority,
    }
}
