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

        /// <summary>M4-04：共享路径重规划仍无法脱困——见
        /// <see cref="FormationMovementDriver"/> 的卡死检测（连续一段时间位移低于阈值），
        /// 重规划次数超过上限后落这个原因，不再无限重试。</summary>
        Stuck,
    }
}
