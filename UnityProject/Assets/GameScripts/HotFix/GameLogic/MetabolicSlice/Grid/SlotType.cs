namespace GameLogic.MetabolicSlice.Grid
{
    /// <summary>冻结总案 G3：3→6。核周(基因效力+)钩子仍缺，见 Graph/SlotPassiveModule.cs 顶部注释。</summary>
    public enum SlotType
    {
        Cytoplasm,   // 胞质：微导热
        Membrane,    // 膜缘：易湿、减伤
        Lattice,     // 晶格：传导损耗↓（= 冻结表 slot_crystal 同概念）
        Perinuclear, // 核周：基因效力+（GenePotencyBias 钩子未接，见 SlotPassiveModule TODO）
        Secretory,   // 分泌：延迟+
        AcidFen,     // 酸沼：打 Acid tag + 微加热
    }
}
