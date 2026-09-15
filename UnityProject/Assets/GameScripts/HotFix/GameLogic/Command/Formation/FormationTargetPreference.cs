namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-03：教义的目标选择偏好轴，只是 <see cref="FormationDoctrineProfile"/> 的一个只读字段，
    /// 不接任何真实 AI 决策/目标选择器（全仓唯一目标选择实现是内核 AOT 层的
    /// <c>JobCommandIntent.TryFindNearestThreat</c>，只服务 Retreat 躲避追兵，本 story 不改它，
    /// 也不依赖它）。真正拿这个偏好去驱动决策是后续故事按需接线。
    /// </summary>
    public enum FormationTargetPreference
    {
        /// <summary>优先最近目标。</summary>
        Nearest,

        /// <summary>优先最强（威胁最大）目标。</summary>
        Strongest,

        /// <summary>优先最弱（最易击杀）目标。</summary>
        Weakest,

        /// <summary>只关注威胁到被护送对象的目标。</summary>
        ProtectWard,

        /// <summary>尽量避免交战。</summary>
        AvoidCombat,
    }
}
