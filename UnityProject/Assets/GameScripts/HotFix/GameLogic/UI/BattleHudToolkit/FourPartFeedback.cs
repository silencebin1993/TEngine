namespace GameLogic.UI.Battle
{
    /// <summary>
    /// M4-R00-02 队列⑥-28（`IC-REQ-013`/`FS-REQ-072` 因果反馈四段式）：拒绝/状态反馈的统一结构——
    /// 原因（为什么）→状态（现在处于什么状态）→后果（这对玩家意味着什么）→可恢复方法（玩家能做什么）。
    ///
    /// 本类型只钉**结构**，不是最终产品文案——`FS-REQ-072` 原文明确"结构模板随本轮返工采用，
    /// 具体内容留到 M5/M6"，各字段现在给最小可读的中文短句即可，不需要打磨成最终市场向文案。
    /// 任何新的失败原因接入这套体系时都必须四个字段都填，不能只写一句笼统的话——
    /// 这正是在纠正此前"HUD 只有一种笼统'控制目标丢失'文案"的问题（IC-REQ-013 审计原话）。
    /// </summary>
    public readonly struct FourPartFeedback
    {
        public readonly string Reason;
        public readonly string State;
        public readonly string Consequence;
        public readonly string Recovery;

        public FourPartFeedback(string reason, string state, string consequence, string recovery)
        {
            Reason = reason;
            State = state;
            Consequence = consequence;
            Recovery = recovery;
        }

        /// <summary>HUD 状态行用的极简版——那一行的空间只够放一个原因短语，
        /// 完整四段落到 <see cref="ToString"/>，绑到 <c>tooltip</c> 悬浮显示。</summary>
        public string CompactLabel => Reason;

        /// <summary>完整四段文案，供 tooltip / 调试日志使用。</summary>
        public override string ToString() =>
            $"{Reason}\n状态：{State}\n后果：{Consequence}\n可恢复方法：{Recovery}";
    }
}
