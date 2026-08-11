using GameLogic.MetabolicSlice.Bag;

namespace GameLogic.MetabolicSlice.Grid
{
    /// <summary>方格节点。Part 为 null 表示空放：仍保留 SlotType 被动，主流能量可穿过（见 Graph/SlotPassiveModule）。</summary>
    public sealed class SlotNode
    {
        public int SlotId { get; }
        public SlotType SlotType { get; set; }
        public PartInstance Part { get; set; }

        public SlotNode(int slotId, SlotType slotType)
        {
            SlotId = slotId;
            SlotType = slotType;
        }

        public bool IsEmpty => Part == null;
    }
}
