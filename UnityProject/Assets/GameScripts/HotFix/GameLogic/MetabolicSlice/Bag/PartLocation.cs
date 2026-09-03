namespace GameLogic.MetabolicSlice.Bag
{
    /// <summary>organelle-structural-tier story-001 Required 4：结构槽视觉分组标签，物理隔离于
    /// 战斗语义的 AttackFamily（禁止复用），只描述外观分组，不驱动装配判定。</summary>
    public enum VisualSlotTag { Armor, Motility, Vital, Appendage }

    /// <summary>对应 §10 接口提示：PartLocation = Bag | Slot(SlotId) | Structural(Tag)（story-001 三态化）。</summary>
    public readonly struct PartLocation
    {
        public enum LocationKind { Bag, Slot, Structural }

        public readonly LocationKind Kind;
        public readonly int SlotId; // 仅 Kind==Slot 时有意义，其余态为 -1
        public readonly VisualSlotTag Tag; // 仅 Kind==Structural 时有意义，其余态为默认值 0

        private PartLocation(LocationKind kind, int slotId, VisualSlotTag tag)
        {
            Kind = kind;
            SlotId = slotId;
            Tag = tag;
        }

        public static PartLocation Bag() => new PartLocation(LocationKind.Bag, -1, default);
        public static PartLocation Slot(int slotId) => new PartLocation(LocationKind.Slot, slotId, default);
        public static PartLocation Structural(VisualSlotTag tag) => new PartLocation(LocationKind.Structural, -1, tag);
    }
}
