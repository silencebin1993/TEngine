namespace GameLogic.MetabolicSlice.Bag
{
    /// <summary>对应 §10 接口提示：PartLocation = Bag | Slot(SlotId)。</summary>
    public readonly struct PartLocation
    {
        public readonly bool IsInBag;
        public readonly int SlotId; // IsInBag=true 时无意义

        private PartLocation(bool isInBag, int slotId)
        {
            IsInBag = isInBag;
            SlotId = slotId;
        }

        public static PartLocation Bag() => new PartLocation(true, -1);
        public static PartLocation Slot(int slotId) => new PartLocation(false, slotId);
    }
}
