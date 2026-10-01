using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>配方检查结果的种类。</summary>
    public enum RecipeCheck : byte
    {
        Ok = 0,
        UnknownRecipe = 1,
        /// <summary>缺输入（卡片负向“配方输入缺失”）。</summary>
        MissingInput = 2,
        /// <summary>主产出放不下（输出堵塞）。</summary>
        NoRoom = 3,
        /// <summary>副产品无处可去（FG04 第 5 节“酸液没有去处”；处理与引导在 FG4-ECO-10）。</summary>
        ByproductNoRoom = 4,
        /// <summary>这种物品不在这个库存里（例如拿家园仓库去算要流体的配方）。</summary>
        NotInStock = 5,
    }

    /// <summary>一次配方检查的结论（原因文字已本地化，玩家可读）。</summary>
    public readonly struct RecipeVerdict
    {
        public readonly RecipeCheck Code;
        public readonly ItemDef Item;
        public readonly long Need;
        public readonly long Have;
        public readonly string Reason;

        public RecipeVerdict(RecipeCheck code, ItemDef item, long need, long have, string reason)
        {
            Code = code;
            Item = item;
            Need = need;
            Have = have;
            Reason = reason;
        }

        public bool Ok => Code == RecipeCheck.Ok;
    }

    /// <summary>配方读写的库存（建筑缓存、家园仓库、测试账本……）。</summary>
    public interface IRecipeStock
    {
        /// <summary>这个库存管不管这种物品（不管的 → <see cref="RecipeCheck.NotInStock"/>）。</summary>
        bool Holds(ItemDef item);
        long Get(ItemDef item);
        /// <summary>还能放多少。</summary>
        long Space(ItemDef item);
        void Add(ItemDef item, long amount);
        void Remove(ItemDef item, long amount);
    }

    /// <summary>
    /// FG4-ECO-01（FGR-ECO-002 配方表；卡片负向“配方输入缺失”“副产品无处可去（交给 FG4-ECO-10）”；FGT-ECO-001 数据部分“物品账目守恒”）：配方的检查与执行。
    /// - <see cref="Check"/>：按顺序查输入够不够（缺哪一种、需要多少、现有多少）→ 主产出放不放得下 → 副产品放不放得下，返回第一条问题；
    /// - <see cref="TryRun"/>：检查通过才执行，全有或全无（输入扣除与产出入库同一步，不会只扣料不出货）；
    /// - 生产建筑（FG4-ECO-02 / 03）拿自己的输入 / 输出缓存实现 <see cref="IRecipeStock"/> 调用这里；图鉴的配方页拿家园仓库（<see cref="HomeStock"/>）说明“以现在的库存能不能做”。
    /// 开销 O(配方行数)，不按帧。
    /// </summary>
    public static class RecipeBook
    {
        public static RecipeVerdict Check(RecipeDef recipe, IRecipeStock stock, int batches = 1)
        {
            if (recipe == null || stock == null)
            {
                return new RecipeVerdict(RecipeCheck.UnknownRecipe, null, 0, 0, GameText.Format("eco.reason.unknown_recipe", recipe?.Id ?? "?"));
            }
            long k = Math.Max(1, batches);
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role != RecipeRole.In)
                {
                    continue;
                }
                if (!stock.Holds(l.Item))
                {
                    return NotHeld(l.Item);
                }
                long need = l.Amount * k;
                long have = stock.Get(l.Item);
                if (have < need)
                {
                    return new RecipeVerdict(RecipeCheck.MissingInput, l.Item, need, have,
                        GameText.Format("eco.reason.missing_input", l.Item.Name, Amount(l.Item, need), Amount(l.Item, have)));
                }
            }
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == RecipeRole.In)
                {
                    continue;
                }
                if (!stock.Holds(l.Item))
                {
                    return NotHeld(l.Item);
                }
                long need = l.Amount * k;
                // 同一种物品既是输入又是产出（目前表里没有）时，执行时先扣输入：空间按扣完之后算。
                long freed = 0;
                foreach (RecipeLine i in recipe.Lines)
                {
                    if (i.Role == RecipeRole.In && ReferenceEquals(i.Item, l.Item))
                    {
                        freed += i.Amount * k;
                    }
                }
                long space = stock.Space(l.Item) + freed;
                if (space < need)
                {
                    bool by = l.Role == RecipeRole.Byproduct;
                    return new RecipeVerdict(by ? RecipeCheck.ByproductNoRoom : RecipeCheck.NoRoom, l.Item, need, space,
                        GameText.Format(by ? "eco.reason.byproduct_no_room" : "eco.reason.no_room", l.Item.Name, Amount(l.Item, need), Amount(l.Item, Math.Max(0, space))));
                }
            }
            return new RecipeVerdict(RecipeCheck.Ok, null, 0, 0, null);
        }

        /// <summary>检查通过才执行（全有或全无）。返回的结论与 <see cref="Check"/> 相同。</summary>
        public static bool TryRun(RecipeDef recipe, IRecipeStock stock, out RecipeVerdict verdict, int batches = 1)
        {
            verdict = Check(recipe, stock, batches);
            if (!verdict.Ok)
            {
                return false;
            }
            long k = Math.Max(1, batches);
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == RecipeRole.In)
                {
                    stock.Remove(l.Item, l.Amount * k);
                }
            }
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role != RecipeRole.In)
                {
                    stock.Add(l.Item, l.Amount * k);
                }
            }
            return true;
        }

        private static RecipeVerdict NotHeld(ItemDef item) =>
            new RecipeVerdict(RecipeCheck.NotInStock, item, 0, 0, GameText.Format("eco.reason.not_home_stock", item.Name, FormText(item)));

        /// <summary>数量文字：固体“×3”式的件数、流体按升。</summary>
        public static string Amount(ItemDef item, long n) =>
            item != null && item.Form == ItemForm.Fluid
                ? GameText.Format("codex.recipe.entry_fluid", string.Empty, n.ToString(CultureInfo.InvariantCulture)).Trim()
                : n.ToString(CultureInfo.InvariantCulture);

        /// <summary>“合金 ×1、稀土矿 ×1”（某一种角色的行）。</summary>
        public static string Describe(RecipeDef recipe, RecipeRole role)
        {
            var parts = new List<string>();
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == role)
                {
                    parts.Add(l.Item.Form == ItemForm.Fluid
                        ? GameText.Format("codex.recipe.entry_fluid", l.Item.Name, l.Amount.ToString(CultureInfo.InvariantCulture))
                        : GameText.Format("codex.recipe.entry", l.Item.Name, l.Amount.ToString(CultureInfo.InvariantCulture)));
                }
            }
            return string.Join(GameText.Language == GameLanguage.En ? ", " : "、", parts);
        }

        public static string FormText(ItemDef item)
        {
            switch (item?.Form)
            {
                case ItemForm.Fluid: return GameText.Get("item.form.fluid");
                case ItemForm.Vault: return GameText.Get("item.form.vault");
                case ItemForm.Digital: return GameText.Get("item.form.digital");
                case ItemForm.Entity:
                    return GameText.Format("item.form.entity", GameText.Get(item.Id == ItemCatalog.MachineId ? "item.entity.machine" : "item.entity.firmware"));
                default: return GameText.Get("item.form.solid");
            }
        }

        /// <summary>测试 / 规划用的账本库存：物品 → 数量；容量默认不限（<see cref="Capacity"/> 里登记的才有上限）。</summary>
        public sealed class LedgerStock : IRecipeStock
        {
            public readonly Dictionary<string, long> Amounts = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> Capacity = new Dictionary<string, long>(StringComparer.Ordinal);

            public bool Holds(ItemDef item) => item != null;
            public long Get(ItemDef item) => item != null && Amounts.TryGetValue(item.Id, out long n) ? n : 0;

            public long Space(ItemDef item) =>
                item != null && Capacity.TryGetValue(item.Id, out long cap) ? Math.Max(0, cap - Get(item)) : long.MaxValue / 4;

            public void Add(ItemDef item, long amount) => Amounts[item.Id] = Get(item) + amount;
            public void Remove(ItemDef item, long amount) => Amounts[item.Id] = Get(item) - amount;

            /// <summary>全部数量之和（账目守恒核对）。</summary>
            public long Total
            {
                get
                {
                    long t = 0;
                    foreach (long v in Amounts.Values)
                    {
                        t += v;
                    }
                    return t;
                }
            }
        }

        /// <summary>家园仓库 / 保管库当库存（流体不在仓库里 → NotInStock）。</summary>
        public sealed class HomeStock : IRecipeStock
        {
            private readonly CampaignState _state;
            private readonly bool _warehouse;

            public HomeStock(CampaignState state)
            {
                _state = state;
                _warehouse = HomeInventory.WarehouseOperational(state);
            }

            public bool Holds(ItemDef item) => HomeInventory.IsStorable(item);
            public long Get(ItemDef item) => HomeInventory.Stock(_state, item);
            public long Space(ItemDef item) => HomeInventory.Space(_state, item, _warehouse);
            public void Add(ItemDef item, long amount) => HomeInventory.Add(_state, item, (int)amount, clampToSpace: false);
            public void Remove(ItemDef item, long amount) => HomeInventory.RemoveUpTo(_state, item, (int)amount);
        }
    }
}
