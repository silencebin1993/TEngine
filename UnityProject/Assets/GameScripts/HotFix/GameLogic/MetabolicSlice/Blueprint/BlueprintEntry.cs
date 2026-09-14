namespace GameLogic.MetabolicSlice.Blueprint
{
    /// <summary>M3-02：蓝图来源属于哪个静态目录（决定 <see cref="BlueprintRegistry.Resolve"/> 去哪张
    /// Catalog 校验 id 存在，不复制目录内容）。</summary>
    public enum BlueprintSourceKind
    {
        Organelle,
        Gene,
    }

    /// <summary>M3-02：一条蓝图记录——配方轨，不持有任何实物/实例引用，只存 id 引用 + 解析进度
    /// （Lineage_Loadout_Mapping.md §4.1：模板层禁止调用 CarrierGeneService.EquipGene）。
    /// Completeness 达到 1 即永久 Unlocked，不因后续 Contamination 变化回退；
    /// Contamination 是独立污染轴，供未来产物质量计算用，不参与解锁判定。</summary>
    public sealed class BlueprintEntry
    {
        public string SourceId { get; }
        public BlueprintSourceKind Kind { get; }
        public float Completeness { get; private set; }
        public float Contamination { get; private set; }
        public int RepeatResolveCount { get; private set; }

        public bool Unlocked => Completeness >= 1f;

        public BlueprintEntry(string sourceId, BlueprintSourceKind kind, float completeness = 0f, float contamination = 0f, int repeatResolveCount = 0)
        {
            SourceId = sourceId;
            Kind = kind;
            Completeness = Clamp01(completeness);
            Contamination = Clamp01(contamination);
            RepeatResolveCount = repeatResolveCount;
        }

        /// <summary>一次解析动作：累加完整度/污染，计入重复解析次数。完整度只增不减——
        /// 拆解、丢弃实物等操作绝不能反向调用本方法（验收 3「拆解后不能复制」的边界就在这里：
        /// 拆解事件在本模块里没有任何监听入口，物理上不可能触发它）。</summary>
        public void ApplyResolve(float completenessGain, float contaminationDelta)
        {
            Completeness = Clamp01(Completeness + completenessGain);
            Contamination = Clamp01(Contamination + contaminationDelta);
            RepeatResolveCount++;
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
