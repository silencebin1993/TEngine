namespace GameLogic.MetabolicSlice.Digestion
{
    public enum DigestionEventKind { Completed, Failed }

    /// <summary>Tick 结算产出的消化事件；调用方消费 Completed 走 BagInventory.TryAdd（既有逼弃路径）。</summary>
    public sealed class DigestionEvent
    {
        public DigestionEventKind Kind { get; }
        public string ReagentId { get; }
        public string ResultCardDefId { get; }

        public DigestionEvent(DigestionEventKind kind, string reagentId, string resultCardDefId)
        {
            Kind = kind;
            ReagentId = reagentId;
            ResultCardDefId = resultCardDefId;
        }
    }
}
