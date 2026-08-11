namespace GameLogic.MetabolicSlice.Crafting
{
    public enum RecipeKind { UpgradeSame, Dismantle }

    /// <summary>数据驱动配方；§9 只落 2 条：同种升级、拆解腾位。禁止卡面写死"仅当邻接某物才能合成"（§6.3）。</summary>
    public sealed class CraftRecipe
    {
        public string RecipeId { get; }
        public RecipeKind Kind { get; }
        public string InputCardDefId { get; }
        public int InputCount { get; }
        public string OutputCardDefId { get; } // Dismantle 时忽略，产物固定为 scrap_material
        public int OutputCount { get; }

        /// <summary>§2.2/§2.6：材料除囊内外，是否也可从已装备槽位补齐。</summary>
        public bool AllowFromEquipped { get; }

        public CraftRecipe(string recipeId, RecipeKind kind, string inputCardDefId, int inputCount,
            string outputCardDefId, int outputCount, bool allowFromEquipped = false)
        {
            RecipeId = recipeId;
            Kind = kind;
            InputCardDefId = inputCardDefId;
            InputCount = inputCount;
            OutputCardDefId = outputCardDefId;
            OutputCount = outputCount;
            AllowFromEquipped = allowFromEquipped;
        }
    }
}
