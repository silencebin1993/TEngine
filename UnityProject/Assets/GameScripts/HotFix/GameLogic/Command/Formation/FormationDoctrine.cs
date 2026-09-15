namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-01：编队教义占位枚举。只是 <see cref="Formation.Doctrine"/> 的一个可读写字段，
    /// 本 story 不实现任何行为差异——教义真正影响 AI/阵型决策是 M4-03 的事。
    ///
    /// 命名注意：<c>PhenotypeTemplateVersion.DoctrineTag</c>（Lineage 模板层的"教义标签"字符串）
    /// 是完全不同的系统，故这里刻意不叫 <c>DoctrineTag</c>，避免误读成同一套东西。
    /// </summary>
    public enum FormationDoctrine
    {
        None = 0,
        Vanguard,
        Hunter,
        Escort,
        Stealth,
        Salvage,
        HoldGround,
    }
}
