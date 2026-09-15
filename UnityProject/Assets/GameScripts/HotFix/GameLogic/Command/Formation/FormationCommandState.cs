namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-02：编队命令的生命周期状态机。<see cref="Pending"/> 是等待队列里的默认态；
    /// <see cref="Active"/> 是当前正在执行的那一条（<see cref="Formation.ActiveCommand"/>）；
    /// 其余三种是终结态，一旦进入不会再变化——终结之后由
    /// <see cref="Formation.TryActivateNextPending"/> 决定是否提升下一条排队命令。
    /// </summary>
    public enum FormationCommandState
    {
        Pending,
        Active,
        Completed,
        Failed,
        Interrupted,
    }
}
