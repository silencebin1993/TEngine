namespace GameLogic.Command
{
    /// <summary>
    /// M4-R02 编队真实命令入口最小桥接：<see cref="SquadCommandSystem.Issue"/>/
    /// <see cref="SquadCommandSystem.FlushQueuedCommands"/> 每次真正执行一次命令分流后的结果——
    /// 覆盖"这次是不是走了编队"、"编队侧接受得怎么样"、"排队期间编队被重新编组而作废"三件事，
    /// 只在真正执行分流的时刻更新（立即下达、或 flush 处理某条排队命令时），单纯入队不更新它。
    /// 数据层结果，不含任何 UI 语义——UI 消费是⑥-25/26号的范围。
    /// </summary>
    public enum SquadFormationDispatchOutcome
    {
        /// <summary>本次是裸选择集/未编组路径，`Formation` 完全没被调用。</summary>
        NotFormationRouted,

        /// <summary><see cref="Formation.Formation.IssueCommand"/> 返回
        /// <see cref="Formation.FormationCommandIssueResult.Activated"/>
        /// （Attack/Guard 情形下含内核二次下发）。</summary>
        Activated,

        /// <summary><see cref="Formation.Formation.IssueCommand"/> 返回
        /// <see cref="Formation.FormationCommandIssueResult.QueuedBehindHigherPriority"/>——
        /// 当前设计范围内不可达（唯一命令源、同一优先级），保留给未来的多优先级命令源故事。</summary>
        QueuedBehindHigherPriority,

        /// <summary>暂停期间排队的编队命令，在恢复兑现前编队被重新编组（成员与排队时的快照不再
        /// 匹配），命令作废——不下发任何内核/编队命令，不静默退化为普通移动。</summary>
        CancelledStaleMembership,
    }
}
