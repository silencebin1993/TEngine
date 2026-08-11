namespace GameLogic.MetabolicSlice.Bag
{
    /// <summary>掉落/装备的实例；CardDefId 查 CardDefs.CardCatalog 拿对应 ChemEngine IModule 工厂。</summary>
    public sealed class PartInstance
    {
        public string PartId { get; }
        public string CardDefId { get; set; }
        public PartLocation Location { get; set; }

        public PartInstance(string partId, string cardDefId, PartLocation location)
        {
            PartId = partId;
            CardDefId = cardDefId;
            Location = location;
        }
    }
}
