using System.Collections.Generic;
using System.Linq;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Crafting
{
    public enum CraftResult { Ok, NotEnoughMaterial, BagFull, RecipeNotFound }

    /// <summary>合成台（§6.1）：非战斗菜单调用。囊内材料不足且配方允许时，可从已装备槽位补齐（§2.2/§2.6）。</summary>
    public static class CraftService
    {
        private static readonly Dictionary<string, CraftRecipe> _recipes = new Dictionary<string, CraftRecipe>();

        static CraftService()
        {
            Register(new CraftRecipe("upgrade_focus", RecipeKind.UpgradeSame, "organ_focus", 2, "organ_focus_plus", 1, allowFromEquipped: true));
            Register(new CraftRecipe("dismantle_focus", RecipeKind.Dismantle, "organ_focus", 1, null, 2));
        }

        private static void Register(CraftRecipe r) => _recipes[r.RecipeId] = r;

        public static CraftResult Craft(string recipeId, BagInventory bag) => Craft(recipeId, bag, null);

        public static CraftResult Craft(string recipeId, BagInventory bag, SlotGrid grid)
        {
            if (!_recipes.TryGetValue(recipeId, out var recipe)) return CraftResult.RecipeNotFound;

            var bagMatches = bag.Items.Where(p => p.CardDefId == recipe.InputCardDefId).Take(recipe.InputCount).ToList();

            var slotMatches = new List<SlotNode>();
            if (recipe.AllowFromEquipped && grid != null && bagMatches.Count < recipe.InputCount)
            {
                int needed = recipe.InputCount - bagMatches.Count;
                foreach (var node in grid.Slots)
                {
                    if (needed <= 0) break;
                    if (!node.IsEmpty && node.Part.CardDefId == recipe.InputCardDefId)
                    {
                        slotMatches.Add(node);
                        needed--;
                    }
                }
            }

            if (bagMatches.Count + slotMatches.Count < recipe.InputCount) return CraftResult.NotEnoughMaterial;

            int producedCount = recipe.Kind == RecipeKind.Dismantle ? recipe.OutputCount : 1;
            if (bag.Items.Count - bagMatches.Count + producedCount > bag.Cap) return CraftResult.BagFull;

            foreach (var m in bagMatches) bag.Items.Remove(m);
            foreach (var node in slotMatches) node.Part = null;

            string outputCardDefId = recipe.Kind == RecipeKind.Dismantle ? "scrap_material" : recipe.OutputCardDefId;
            for (int i = 0; i < producedCount; i++)
            {
                var part = new PartInstance(System.Guid.NewGuid().ToString("N"), outputCardDefId, PartLocation.Bag());
                bag.TryAdd(part);
            }
            return CraftResult.Ok;
        }
    }
}
