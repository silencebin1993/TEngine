namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-01：编队教义枚举。<see cref="Formation.Doctrine"/> 的可读写字段。
    /// M4-03 起每个教义都有对应的参数三元组，具体参数见 <see cref="FormationDoctrineProfile"/>。
    ///
    /// 命名注意：<c>PhenotypeTemplateVersion.DoctrineTag</c>（Lineage 模板层的"教义标签"字符串）
    /// 是完全不同的系统，故这里刻意不叫 <c>DoctrineTag</c>，避免误读成同一套东西。
    /// </summary>
    public enum FormationDoctrine
    {
        /// <summary>中性默认（呼应 CellGlobal.LowHealthPercent 0.3 基准）。</summary>
        None = 0,

        /// <summary>先锋：专挑硬目标冲锋，死战到底。</summary>
        Vanguard,

        /// <summary>猎手：专挑落单弱者，主动出击但不硬拼。</summary>
        Hunter,

        /// <summary>护送：只打威胁到护送对象的敌人，谨慎优先保对象。</summary>
        Escort,

        /// <summary>潜行：尽量不交战，早早脱离。</summary>
        Stealth,

        /// <summary>回收：专注拾取，比潜行略敢靠近但仍以躲为主。</summary>
        Salvage,

        /// <summary>坚守：站桩防御，不挑不追，守到底。</summary>
        HoldGround,
    }
}
