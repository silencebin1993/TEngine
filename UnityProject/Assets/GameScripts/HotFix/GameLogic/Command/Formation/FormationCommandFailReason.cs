namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-02：命令终结时的失败/中断原因。命名范式对齐
    /// <c>GameLogic.MetabolicSlice.Lineage.HomecomingRetrofitService.RetrofitRejectReason</c>——
    /// <see cref="None"/> 表示未失败（仍在进行或正常 Completed），其余枚举值各对应一种具体成因。
    /// </summary>
    public enum FormationCommandFailReason
    {
        None,
        InvalidTarget,
        Cancelled,
        PreemptedByOverride,
    }
}
