namespace GameLogic.MetabolicSlice.Carrier
{
    public enum CarrierGeneResult { Ok, GeneNotFound, GeneAlreadyEquipped, SlotOccupied, SlotIndexInvalid, CarrierNotFound }

    /// <summary>装/卸基因：储备囊 ↔ Carrier 插槽（D4）。语义仿 TransferService.Equip/Unequip，
    /// 全路径 Reject-to-Safe，不抛异常。</summary>
    public static class CarrierGeneService
    {
        public static CarrierGeneResult EquipGene(GeneReserve reserve, CarrierInstance carrier, string geneInstanceId, int slotIndex, CarrierRegistry registry = null)
        {
            if (carrier == null)
            {
                return CarrierGeneResult.CarrierNotFound;
            }
            if (slotIndex < 0 || slotIndex >= carrier.Slots.Count)
            {
                return CarrierGeneResult.SlotIndexInvalid;
            }

            var gene = reserve.Find(geneInstanceId);
            if (gene == null)
            {
                return CarrierGeneResult.GeneNotFound;
            }
            if (!gene.Location.IsInReserve)
            {
                // 实例当前不在储备囊里，说明已经插在某个 Carrier 插槽上（W1：同一实例不能同时插两处）。
                return CarrierGeneResult.GeneAlreadyEquipped;
            }

            var slot = carrier.Slots[slotIndex];
            if (slot.GeneInstanceId != null)
            {
                // 插已占用槽：拒绝，不交换（D12）。
                return CarrierGeneResult.SlotOccupied;
            }

            slot.GeneInstanceId = gene.GeneInstanceId;
            gene.Location = GeneLocation.CarrierSlot(carrier.CarrierId, slotIndex);

            // story-010 J4：装配变更后递增版本号
            registry?.IncrementAssemblyVersion();

            return CarrierGeneResult.Ok;
        }

        public static CarrierGeneResult UnequipGene(GeneReserve reserve, CarrierInstance carrier, int slotIndex, CarrierRegistry registry = null)
        {
            if (carrier == null)
            {
                return CarrierGeneResult.CarrierNotFound;
            }
            if (slotIndex < 0 || slotIndex >= carrier.Slots.Count)
            {
                return CarrierGeneResult.SlotIndexInvalid;
            }

            var slot = carrier.Slots[slotIndex];
            if (slot.GeneInstanceId == null)
            {
                return CarrierGeneResult.GeneNotFound;
            }

            var gene = reserve.Find(slot.GeneInstanceId);
            slot.GeneInstanceId = null;
            if (gene != null)
            {
                gene.Location = GeneLocation.Reserve();
            }

            // story-010 J4：装配变更后递增版本号
            registry?.IncrementAssemblyVersion();

            return CarrierGeneResult.Ok;
        }
    }
}
