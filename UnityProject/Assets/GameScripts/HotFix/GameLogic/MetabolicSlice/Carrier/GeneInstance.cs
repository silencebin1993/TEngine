namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>基因的实例身份（D1：基因原本只是全局 IContract 列表，无实例身份，
    /// 无法表达 W1「同一实例不能同时插两个器官」；新建独立类型承载）。</summary>
    public sealed class GeneInstance
    {
        public string GeneInstanceId { get; }
        public string GeneId { get; }
        public GeneLocation Location { get; set; }

        public GeneInstance(string geneInstanceId, string geneId, GeneLocation location)
        {
            GeneInstanceId = geneInstanceId;
            GeneId = geneId;
            Location = location;
        }
    }
}
