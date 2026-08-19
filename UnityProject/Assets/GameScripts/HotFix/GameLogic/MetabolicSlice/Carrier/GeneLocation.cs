namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>基因实例的位置：储备囊，或某 Carrier 的某插槽。语义仿 Bag.PartLocation，但独立类型，不共享（D1/D2）。</summary>
    public readonly struct GeneLocation
    {
        public readonly bool IsInReserve;
        public readonly string CarrierId; // IsInReserve=true 时无意义
        public readonly int SlotIndex; // IsInReserve=true 时无意义

        private GeneLocation(bool isInReserve, string carrierId, int slotIndex)
        {
            IsInReserve = isInReserve;
            CarrierId = carrierId;
            SlotIndex = slotIndex;
        }

        public static GeneLocation Reserve() => new GeneLocation(true, null, -1);

        public static GeneLocation CarrierSlot(string carrierId, int slotIndex) => new GeneLocation(false, carrierId, slotIndex);
    }
}
