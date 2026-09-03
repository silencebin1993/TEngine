namespace GameLogic.MetabolicSlice.Bag
{
    /// <summary>掉落/装备的实例；CardDefId 查 CardDefs.CardCatalog 拿对应 ComposeEngine IModule 工厂。</summary>
    public sealed class PartInstance
    {
        /// <summary>organelle-structural-tier story-001 preflight #1：StatModifier.SourceId 是 int，
        /// PartId 是 string，二者不能直接互转。每个实例分配一个递减 int 作为 StatSheet Add/RemoveBySource
        /// 的来源标识，起点 -2,000,000 明显避开卡牌/技能正整数 id 区间与 CellStageFlow 的 -100。</summary>
        private static int _nextRuntimeSourceId = -2_000_000;

        public string PartId { get; }
        public string CardDefId { get; set; }
        public PartLocation Location { get; set; }
        public int RuntimeSourceId { get; }

        public PartInstance(string partId, string cardDefId, PartLocation location)
        {
            PartId = partId;
            CardDefId = cardDefId;
            Location = location;
            RuntimeSourceId = _nextRuntimeSourceId--;
        }
    }
}
