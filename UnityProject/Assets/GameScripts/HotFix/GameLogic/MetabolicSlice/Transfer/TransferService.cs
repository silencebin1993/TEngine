using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Transfer
{
    public enum TransferResult { Ok, SlotTypeMismatch, SlotOccupied, BagFull, PartNotFound }

    /// <summary>装/卸：囊 ↔ 切片槽（§3.3）。</summary>
    public static class TransferService
    {
        public static TransferResult Equip(BagInventory bag, SlotGrid grid, string partId, int slotId)
        {
            var part = bag.Items.Find(p => p.PartId == partId);
            if (part == null) return TransferResult.PartNotFound;
            var node = grid.Slots[slotId];
            if (!node.IsEmpty) return TransferResult.SlotOccupied;
            if (!CardCatalog.AllowsSlot(part.CardDefId, node.SlotType)) return TransferResult.SlotTypeMismatch;

            bag.Items.Remove(part);
            part.Location = PartLocation.Slot(slotId);
            node.Part = part;
            return TransferResult.Ok;
        }

        public static TransferResult Unequip(BagInventory bag, SlotGrid grid, int slotId)
        {
            var node = grid.Slots[slotId];
            if (node.IsEmpty) return TransferResult.PartNotFound;
            if (bag.Items.Count >= bag.Cap) return TransferResult.BagFull;

            var part = node.Part;
            node.Part = null;
            part.Location = PartLocation.Bag();
            bag.Items.Add(part);
            return TransferResult.Ok;
        }
    }
}
