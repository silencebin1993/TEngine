namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>M3-03：一次模板提交产生的不可变版本。只存配方引用（蓝图 id），不持有任何
    /// 实物/实例——校验蓝图所有权在 <see cref="LineageRegistry.CommitTemplate"/>，本类型本身
    /// 创建后字段只读，不提供任何就地修改方法（验收「修改模板不会回写旧版本」的边界就在这里：
    /// 物理上没有 setter 可以改旧版本，"修改"永远只能走 CommitTemplate 产生下一个 Version）。</summary>
    public sealed class PhenotypeTemplateVersion
    {
        public int Version { get; }
        public string OrganelleId { get; }
        private readonly string[] _geneIds;
        public System.Collections.Generic.IReadOnlyList<string> GeneIds => _geneIds;

        /// <summary>占位占场：AI 教义（保持距离/护送/集火/撤退阈值等，GDD §6.5）。本期只存标签，
        /// 不做行为决策树，具体语义留给后续接 AI 逻辑的故事。</summary>
        public string DoctrineTag { get; }

        /// <summary>M3-03 实施第 4 条：生物质/代谢成本占位公式（器官权重 3 + 每基因权重 1），
        /// 非目标不含数值平衡，将来接真实数值表时替换本公式即可，字段口径不变。</summary>
        public float BiomassCost { get; }

        /// <summary>M3-03 实施第 5 条：稳定装配签名——只依赖 OrganelleId + 有序 GeneIds，
        /// 不依赖 seed/WorldState（那是 M3-04 动态相位的事，见 Lineage_Loadout_Mapping.md §2.4）。</summary>
        public string Signature { get; }

        public PhenotypeTemplateVersion(int version, string organelleId, string[] geneIds, string doctrineTag, float biomassCost, string signature)
        {
            Version = version;
            OrganelleId = organelleId;
            _geneIds = geneIds;
            DoctrineTag = doctrineTag;
            BiomassCost = biomassCost;
            Signature = signature;
        }
    }
}
