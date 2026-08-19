namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>一个 Carrier 器官的实例（D8：与玩家囊里该 Carrier 器官的 PartInstance 一一对应，
    /// CarrierId = 该 PartInstance.PartId）。插槽数恒为 3（Decision 已锁，W3）。</summary>
    public sealed class CarrierInstance
    {
        public const int SlotCount = 3;

        public string CarrierId { get; }

        /// <summary>该 Carrier 对应的器官 def id（如 "org_cilia"），story-004 D9 新增——
        /// 004 前 CarrierCompiler 无法反查器官，链尾恒为 Bolt；null 时下游按 Reject-to-Safe 回落 Actuator()。</summary>
        public string OrganelleId { get; }

        public CarrierSlot[] Slots { get; }

        public CarrierInstance(string carrierId, string organelleId = null)
        {
            CarrierId = carrierId;
            OrganelleId = organelleId;
            Slots = new CarrierSlot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                Slots[i] = new CarrierSlot(i);
            }
        }
    }
}
