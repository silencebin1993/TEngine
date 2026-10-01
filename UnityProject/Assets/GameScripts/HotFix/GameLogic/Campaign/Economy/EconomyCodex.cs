using System.Globalization;
using System.Text;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-01（卡片“每种物品和配方都有图鉴条目（来源和用途）”；FG04 第 4 节“物品和配方都有图鉴条目，写明来源和用途（这个东西从哪来、拿去干什么）”）：
    /// 物品 / 配方图鉴条目的正文（<see cref="Progression.MechanicCodex"/> 的物品、配方页签调用）。全部走文本键。
    /// - 物品：说明、形态、从哪来（表里的来源 + 产出它的每条配方：建筑与输入）、拿去干什么（表里的用途 + 用到它的每条配方：建筑与产出）、现有数量；
    /// - 配方：建筑、时间（1x 下的秒）、输入、产出、副产品（必须有去处）、按组件表 / 蓝图 / 指定固件的说明、初值说明、以现在的家园库存能不能做（缺哪种、差多少）。
    /// 开销：只在图鉴显示该条目时 O(配方行数)。
    /// </summary>
    public static class EconomyCodex
    {
        public static string ItemBody(string itemId, CampaignState state)
        {
            if (!ItemCatalog.TryGet(itemId, out ItemDef item))
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            sb.AppendLine(GameText.Get(item.DescKey));
            sb.AppendLine(GameText.Format("codex.item.form", RecipeBook.FormText(item)));
            sb.AppendLine();
            sb.AppendLine(GameText.Get("codex.item.source_title"));
            sb.AppendLine(GameText.Get(item.SourceKey));
            foreach (RecipeDef r in ItemCatalog.ProducedBy(item.Id))
            {
                bool byproduct = true;
                foreach (RecipeLine l in r.Lines)
                {
                    if (ReferenceEquals(l.Item, item) && l.Role == RecipeRole.Out)
                    {
                        byproduct = false;
                    }
                }
                sb.AppendLine(byproduct
                    ? GameText.Format("codex.item.byproduct_of", r.Name, GameText.Get(r.BuildingNameKey))
                    : GameText.Format("codex.item.made_by", r.Name, GameText.Get(r.BuildingNameKey), RecipeBook.Describe(r, RecipeRole.In)));
            }
            sb.AppendLine();
            sb.AppendLine(GameText.Get("codex.item.use_title"));
            sb.AppendLine(GameText.Get(item.UseKey));
            foreach (RecipeDef r in ItemCatalog.ConsumedBy(item.Id))
            {
                sb.AppendLine(GameText.Format("codex.item.used_by", r.Name, GameText.Get(r.BuildingNameKey), RecipeBook.Describe(r, RecipeRole.Out)));
            }
            if (state != null)
            {
                ItemDistributionView v = ItemDistribution.Get(state, item);
                sb.AppendLine();
                sb.AppendLine(GameText.Format("codex.item.stock", ItemHover.AmountText(item, v?.Total ?? 0)));
            }
            return sb.ToString().TrimEnd();
        }

        public static string RecipeBody(string recipeId, CampaignState state)
        {
            if (!ItemCatalog.TryGetRecipe(recipeId, out RecipeDef r))
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            sb.AppendLine(GameText.Format("codex.recipe.building", GameText.Get(r.BuildingNameKey)));
            sb.AppendLine(GameText.Format("codex.recipe.time", r.Seconds.ToString("0.#", CultureInfo.InvariantCulture)));
            sb.AppendLine(GameText.Format("codex.recipe.inputs", RecipeBook.Describe(r, RecipeRole.In)));
            sb.AppendLine(GameText.Format("codex.recipe.outputs", RecipeBook.Describe(r, RecipeRole.Out)));
            string by = RecipeBook.Describe(r, RecipeRole.Byproduct);
            if (by.Length > 0)
            {
                sb.AppendLine(GameText.Format("codex.recipe.byproducts", by));
            }
            switch (r.Kind)
            {
                case "component_table":
                    sb.AppendLine(GameText.Get("codex.recipe.kind_component"));
                    break;
                case "blueprint":
                    sb.AppendLine(GameText.Get("codex.recipe.kind_blueprint"));
                    break;
                case "firmware":
                    sb.AppendLine(GameText.Get("codex.recipe.kind_firmware"));
                    break;
            }
            if (r.Provisional)
            {
                sb.AppendLine(GameText.Get("codex.recipe.provisional"));
            }
            if (state != null)
            {
                RecipeVerdict v = RecipeBook.Check(r, new CodexPreviewStock(state));
                sb.AppendLine();
                sb.AppendLine(v.Ok ? GameText.Get("codex.recipe.stock_ok") : GameText.Format("codex.recipe.stock_short", v.Reason));
                string fluids = FluidNames(r);
                if (fluids.Length > 0)
                {
                    sb.AppendLine(GameText.Format("codex.recipe.fluid_note", fluids));
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string FluidNames(RecipeDef r)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (RecipeLine l in r.Lines)
            {
                if (l.Item != null && l.Item.Form == ItemForm.Fluid && !names.Contains(l.Item.Name))
                {
                    names.Add(l.Item.Name);
                }
            }
            return string.Join(GameText.Language == GameLanguage.En ? ", " : "、", names);
        }

        /// <summary>
        /// 图鉴“以现在的家园库存能不能做”的预览库存（只读判断，不执行配方）：仓库里的固体 / 保管库物品按家园库存与空间判断；
        /// 流体走管线与储罐、不在仓库里，这里不判断（正文另写一行说明，生产建筑接上管线后由建筑自己的缓存判断，FG4-ECO-02 / 03）；
        /// 产出是实体（机器进名册、固件芯片进固件库）不占仓库空间。执行配方仍用 <see cref="RecipeBook.HomeStock"/> 或建筑缓存，流体照样拒绝，账目不受影响。
        /// </summary>
        private sealed class CodexPreviewStock : IRecipeStock
        {
            private const long Unlimited = long.MaxValue / 4;
            private readonly RecipeBook.HomeStock _home;

            public CodexPreviewStock(CampaignState state) => _home = new RecipeBook.HomeStock(state);

            public bool Holds(ItemDef item) => item != null;
            public long Get(ItemDef item) => HomeInventory.IsStorable(item) ? _home.Get(item) : Unlimited;
            public long Space(ItemDef item) => HomeInventory.IsStorable(item) ? _home.Space(item) : Unlimited;
            public void Add(ItemDef item, long amount) { }
            public void Remove(ItemDef item, long amount) { }
        }
    }
}
