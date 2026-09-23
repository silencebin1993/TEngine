using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>ER4-PRIM-02 PRIMITIVE-FULL-DEMO-SPEC.md §3.1/§3.3：3×3 电路板的固定几何常量。
    /// Demo 固定同一张网格（不允许玩家换槽地形），所以这里是纯常量表，不是每局可各自不同的数据——
    /// <see cref="CampaignRecords.BlueprintVersionRecord.CircuitSlotTypes"/> 落盘的 9 个值当前恒等于
    /// 本类 <see cref="SlotTypeAt"/> 的计算结果。</summary>
    public static class BlueprintCircuitLayout
    {
        public const int SlotCount = 9;

        /// <summary>§3.1 第1条：不可拆的电源源点固定在 0。</summary>
        public const int SourceSlot = 0;

        /// <summary>§3.1 第1条：主组件汇点固定在 8。</summary>
        public const int SinkSlot = 8;

        /// <summary>§3.5 第5条："超过四条有效路径时保存拒绝"。</summary>
        public const int MaxPaths = 4;

        /// <summary>§3.2 第2条："编辑器撤销/重做至少各 20 步"。</summary>
        public const int MaxUndoRedoSteps = 20;

        /// <summary>§3.3 第3条固定布局："通用/减振/高导；减振/通用/高导；通用/高导/通用"，行优先。
        /// 通用＝原 Cytoplasm，减振＝原 Membrane，高导＝原 Lattice——三种已在
        /// <c>GameLogic.MetabolicSlice.Graph.SlotPassiveModule</c> 生效的既有槽被动，Demo 不使用
        /// Perinuclear/Secretory/AcidFen。</summary>
        private static readonly SlotType[] FixedPattern =
        {
            SlotType.Cytoplasm, SlotType.Membrane, SlotType.Lattice,
            SlotType.Membrane, SlotType.Cytoplasm, SlotType.Lattice,
            SlotType.Cytoplasm, SlotType.Lattice, SlotType.Cytoplasm,
        };

        public static SlotType SlotTypeAt(int slotIndex) => FixedPattern[slotIndex];

        public static SlotType[] BuildSlotTypes()
        {
            var result = new SlotType[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                result[i] = FixedPattern[i];
            }
            return result;
        }

        /// <summary>§3.1 第1条默认线 0→1→2→5→8："所有默认机器不经编辑可攻击"。四条有向边，
        /// 四邻校验全部成立（0-1/1-2/2-5/5-8 均相邻），可直接喂给 <c>SlotGrid.TryAddEdge</c>。</summary>
        public static readonly (int From, int To)[] DefaultLine =
        {
            (0, 1), (1, 2), (2, 5), (5, 8),
        };

        public static bool IsFixedSlot(int slotIndex) => slotIndex == SourceSlot || slotIndex == SinkSlot;

        /// <summary>STORY-EXECUTION-CARDS.md ER4-PRIM-02 第2条："显示空槽被动"；
        /// PRIMITIVE-FULL-DEMO-SPEC.md §3.3 第3条："机械 UI 用绝缘/散热/损耗文案"（不得显示原
        /// Cytoplasm/Membrane/Lattice 生物学细胞质/膜/晶格内部键名，也不得显示 <c>SlotPassiveModule</c>
        /// 内部用的 "Wet"/"Acid" 等 Packet tag 原词）。与 <see cref="GameLogic.MetabolicSlice.Graph.SlotPassiveModule.Step"/>
        /// 的真实数值逐条对应，改被动实现时要同步改这两行文案。</summary>
        public static string SlotTypeDisplayName(SlotType slotType) => slotType switch
        {
            SlotType.Cytoplasm => "通用节点",
            SlotType.Membrane => "减振节点",
            SlotType.Lattice => "高导节点",
            _ => slotType.ToString(),
        };

        public static string SlotPassiveDisplay(SlotType slotType) => slotType switch
        {
            SlotType.Cytoplasm => "被动：微产热",
            SlotType.Membrane => "被动：绝缘缓冲（能量×0.95）",
            SlotType.Lattice => "被动：高导低损（能量×1.05）",
            _ => "被动：无",
        };
    }
}
