namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>一个 Carrier 器官的实例（D8：与玩家囊里该 Carrier 器官的 PartInstance 一一对应，
    /// CarrierId = 该 PartInstance.PartId）。插槽数恒为 3（Decision 已锁，W3）。</summary>
    public sealed class CarrierInstance
    {
        public const int SlotCount = 3;

        public string CarrierId { get; }
        public CarrierSlot[] Slots { get; }

        public CarrierInstance(string carrierId)
        {
            CarrierId = carrierId;
            Slots = new CarrierSlot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                Slots[i] = new CarrierSlot(i);
            }
        }
    }
}
