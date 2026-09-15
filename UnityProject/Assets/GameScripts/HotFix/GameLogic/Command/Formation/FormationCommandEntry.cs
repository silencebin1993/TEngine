namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-02：命令队列条目——命令本体（<see cref="Command"/>）只读，运行时状态随生命周期推进可写，
    /// 但只允许 <see cref="Formation"/> 内部改写（<see cref="State"/>/<see cref="FailReason"/> 都是
    /// internal set）。外部（视觉反馈层、自检）只能读，不能绕过 Formation 的状态机方法直接改状态。
    /// </summary>
    public sealed class FormationCommandEntry
    {
        public FormationCommand Command { get; }

        public FormationCommandState State { get; internal set; }

        public FormationCommandFailReason FailReason { get; internal set; }

        internal FormationCommandEntry(FormationCommand command)
        {
            Command = command;
            State = FormationCommandState.Pending;
            FailReason = FormationCommandFailReason.None;
        }
    }
}
