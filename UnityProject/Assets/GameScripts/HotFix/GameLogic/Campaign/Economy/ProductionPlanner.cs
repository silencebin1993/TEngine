using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-08（FG04 FGR-ECO-052 产线规划助手；FG13 FGU-14“目标产量 → 需求计算”；FGT-ECO-008）：
    /// 选一个目标（物品 + 每游戏分钟产量），沿配方往上游展开，列出每条配方需要多少座建筑（精确值与取整后要建的座数）、原料需求（以及大约要几座采集建筑）、
    /// 副产品与电力需求。<b>只计算和显示，不建造、不改世界里的任何东西</b>（FGR-BASE-020：不做玩家没要求的自动动作）。
    /// - 配方：只用能在生产建筑里跑的配方（建筑是按配方生产的生产建筑、允许这条配方）；一种物品有多条配方时默认用排序最前的，玩家可以逐物品换。
    /// - 速率：一座建筑满速每分钟做 60 ÷ 配方秒数 份（与 <see cref="ProductionService"/> 的周期同一个换算）；流体按升。
    /// - 原料：没有可用配方的物品。矿（原料、非废料）= 提取钻，废料 = 回收站拆废墟，流体 = 流体泵；其它（远征物等）只列需求。
    /// - 循环配方或展开太深（eco.planner.max_depth）时停止展开，把该物品当作原料并写明。
    /// 纯函数（只读表），O(配方行数 × 展开深度)。
    /// </summary>
    public static class ProductionPlanner
    {
        public static int MaxDepth => Math.Max(2, GridContent.TuningInt("eco.planner.max_depth"));
        public static float MaxRate => Math.Max(1f, GridContent.Tuning("eco.planner.max_rate"));
        public static float DefaultRate => Math.Max(0.1f, GridContent.Tuning("eco.planner.default_rate"));

        public sealed class RecipeRow
        {
            public RecipeDef Recipe;
            public string BuildingType;
            /// <summary>每分钟要做几份。</summary>
            public double CyclesPerMinute;
            /// <summary>精确需要的建筑数（满速）与要建的座数（向上取整）。</summary>
            public double Buildings;
            public int BuildingsToBuild;
            /// <summary>这条配方主要为哪种物品而开（展开时第一次请求它的物品）与每分钟产量。</summary>
            public ItemDef ForItem;
            public double ForItemPerMinute;
            /// <summary>同一物品的其它可选配方（可以在面板里换）。</summary>
            public readonly List<RecipeDef> Alternatives = new List<RecipeDef>(2);
            public float PowerEach;
            public double Utilization => BuildingsToBuild > 0 ? Buildings / BuildingsToBuild : 0;
        }

        public sealed class RawRow
        {
            public ItemDef Item;
            public double PerMinute;
            /// <summary>采集它的建筑类型（没有 = 只能从远征 / 别处获得）。</summary>
            public string ExtractorType;
            public double ExtractorPerMinute;
            public double Extractors;
            public int ExtractorsToBuild;
            public float PowerEach;
            /// <summary>说明文本键（放在哪类地形上、来源）。</summary>
            public string NoteKey;
            /// <summary>因为循环 / 展开太深才当作原料。</summary>
            public bool Truncated;
        }

        public sealed class Byproduct
        {
            public ItemDef Item;
            public double PerMinute;
        }

        public sealed class Result
        {
            public bool Ok;
            public string Error;
            public ItemDef Target;
            public double TargetPerMinute;
            public readonly List<RecipeRow> Rows = new List<RecipeRow>(8);
            public readonly List<RawRow> Raws = new List<RawRow>(4);
            public readonly List<Byproduct> Byproducts = new List<Byproduct>(2);
            /// <summary>电力需求：按精确座数 / 按要建的座数（生产建筑）；采集建筑另算。</summary>
            public double Power;
            public double PowerToBuild;
            public double ExtractorPower;
            /// <summary>按要建的座数满速时，目标物品每分钟的产能（取整后通常略高于目标）。</summary>
            public double TargetCapacityPerMinute;
        }

        // ── 配方选择 ─────────────────────────────────────────────────────────

        /// <summary>能在生产建筑里跑、并以 <paramref name="itemId"/> 为（非副产品）产出的配方，按配方排序。</summary>
        public static List<RecipeDef> RecipesFor(string itemId)
        {
            var list = new List<RecipeDef>(2);
            foreach (RecipeDef r in ItemCatalog.ProducedBy(itemId))
            {
                if (Runnable(r) && OutAmount(r, itemId) > 0)
                {
                    list.Add(r);
                }
            }
            list.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static bool Runnable(RecipeDef r)
        {
            if (r == null || !ProducerCatalog.TryGet(r.Building, out ProducerDef def) || def.Mode != ProducerMode.Recipe || r.Seconds <= 0f)
            {
                return false;
            }
            foreach (RecipeDef x in def.Recipes)
            {
                if (x != null && x.Id == r.Id)
                {
                    return true;
                }
            }
            return false;
        }

        private static int OutAmount(RecipeDef r, string itemId)
        {
            int n = 0;
            foreach (RecipeLine l in r.Lines)
            {
                if (l.Role == RecipeRole.Out && l.Item != null && l.Item.Id == itemId)
                {
                    n += l.Amount;
                }
            }
            return n;
        }

        /// <summary>规划助手可选的目标：能由产线生产的物品，以及能用采集建筑获得的原料（按物品表顺序）。</summary>
        public static List<ItemDef> Targets()
        {
            var list = new List<ItemDef>(24);
            foreach (ItemDef d in ItemCatalog.Items)
            {
                if (RecipesFor(d.Id).Count > 0 || Extractor(d, out _, out _, out _) != null)
                {
                    list.Add(d);
                }
            }
            return list;
        }

        /// <summary>原料的采集建筑：矿 = 提取钻、废料 = 回收站（拆废墟）、被配方用到的流体 = 流体泵。返回建筑类型（没有 = null）。</summary>
        public static string Extractor(ItemDef item, out double perMinuteEach, out string noteKey, out float powerEach)
        {
            perMinuteEach = 0;
            noteKey = "planner.raw.source_other";
            powerEach = 0f;
            if (item == null || RecipesFor(item.Id).Count > 0)
            {
                return null;
            }
            ProducerMode want;
            if (item.Id == ItemCatalog.ScrapId)
            {
                want = ProducerMode.Recycler;
                noteKey = "planner.raw.source_ruins";
            }
            else if (item.Form == ItemForm.Solid && item.Tier == "raw")
            {
                want = ProducerMode.Drill;
                noteKey = "planner.raw.source_vein";
            }
            else if (item.Form == ItemForm.Fluid && ItemCatalog.ConsumedBy(item.Id).Count > 0 && ItemCatalog.ProducedBy(item.Id).Count == 0)
            {
                want = ProducerMode.Pump;
                noteKey = "planner.raw.source_fluid";
            }
            else
            {
                if (ItemCatalog.ProducedBy(item.Id).Count > 0)
                {
                    noteKey = "planner.raw.source_byproduct";
                }
                return null;
            }
            foreach (ProducerDef d in ProducerCatalog.All)
            {
                if (d.Mode != want)
                {
                    continue;
                }
                perMinuteEach = want == ProducerMode.Pump
                    ? d.FluidLpm
                    : Math.Max(1, d.CycleAmount) * 60.0 / Math.Max(0.01, d.CycleSeconds);
                powerEach = PowerOf(d.TypeId);
                return perMinuteEach > 0 ? d.TypeId : null;
            }
            return null;
        }

        public static float PowerOf(string typeId) =>
            typeId != null && HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) p) ? Math.Max(0f, p.PowerDemand) : 0f;

        // ── 计算 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 规划：目标 <paramref name="target"/> 每分钟 <paramref name="perMinute"/>（流体按升）。<paramref name="choices"/>：物品 ID → 玩家选的配方 ID（没有 / 不可用 = 默认）。
        /// 不合法（没选目标、产量超出范围、物品不能生产也不能采集）时 Ok = false 并给出原因（B06）。
        /// </summary>
        public static Result Plan(ItemDef target, float perMinute, IReadOnlyDictionary<string, string> choices = null)
        {
            var res = new Result { Target = target, TargetPerMinute = perMinute };
            if (target == null)
            {
                res.Error = GameText.Get("planner.error.no_target");
                return res;
            }
            if (!(perMinute > 0f) || perMinute > MaxRate || float.IsNaN(perMinute) || float.IsInfinity(perMinute))
            {
                res.Error = GameText.Format("planner.error.rate", ProductionStats.UiNumber(MaxRate));
                return res;
            }
            bool producible = RecipesFor(target.Id).Count > 0;
            if (!producible && Extractor(target, out _, out string why, out _) == null)
            {
                res.Error = GameText.Format("planner.error.not_producible", target.Name, GameText.Get(why));
                return res;
            }
            var rows = new Dictionary<string, RecipeRow>(StringComparer.Ordinal);
            var raws = new Dictionary<string, RawRow>(StringComparer.Ordinal);
            var bys = new Dictionary<string, Byproduct>(StringComparer.Ordinal);
            var order = new List<RecipeRow>(8);
            var rawOrder = new List<RawRow>(4);
            var stack = new HashSet<string>(StringComparer.Ordinal);
            Demand(target, perMinute, 0, stack, choices, rows, order, raws, rawOrder, bys);

            foreach (RecipeRow r in order)
            {
                r.Buildings = r.CyclesPerMinute * r.Recipe.Seconds / 60.0;
                r.BuildingsToBuild = (int)Math.Ceiling(r.Buildings - 1e-6);
                r.PowerEach = PowerOf(r.BuildingType);
                res.Power += r.Buildings * r.PowerEach;
                res.PowerToBuild += r.BuildingsToBuild * r.PowerEach;
                res.Rows.Add(r);
            }
            foreach (RawRow r in rawOrder)
            {
                if (r.ExtractorType != null && r.ExtractorPerMinute > 0)
                {
                    r.Extractors = r.PerMinute / r.ExtractorPerMinute;
                    r.ExtractorsToBuild = (int)Math.Ceiling(r.Extractors - 1e-6);
                    res.ExtractorPower += r.ExtractorsToBuild * r.PowerEach;
                }
                res.Raws.Add(r);
            }
            foreach (Byproduct b in bys.Values)
            {
                res.Byproducts.Add(b);
            }
            res.Byproducts.Sort((a, b) => string.CompareOrdinal(a.Item.Id, b.Item.Id));
            // 取整后的产能：目标物品那条配方按要建的座数满速。
            if (res.Rows.Count > 0 && res.Rows[0].ForItem == target)
            {
                RecipeRow top = res.Rows[0];
                res.TargetCapacityPerMinute = top.BuildingsToBuild * 60.0 / top.Recipe.Seconds * OutAmount(top.Recipe, target.Id);
            }
            else if (res.Raws.Count > 0 && res.Raws[0].Item == target)
            {
                res.TargetCapacityPerMinute = res.Raws[0].ExtractorsToBuild * res.Raws[0].ExtractorPerMinute;
            }
            res.Ok = true;
            return res;
        }

        private static void Demand(ItemDef item, double perMinute, int depth, HashSet<string> stack, IReadOnlyDictionary<string, string> choices,
            Dictionary<string, RecipeRow> rows, List<RecipeRow> order, Dictionary<string, RawRow> raws, List<RawRow> rawOrder, Dictionary<string, Byproduct> bys)
        {
            if (item == null || perMinute <= 0)
            {
                return;
            }
            List<RecipeDef> options = RecipesFor(item.Id);
            RecipeDef recipe = Choose(item.Id, options, choices);
            bool truncated = recipe != null && (depth >= MaxDepth || stack.Contains(item.Id));
            if (recipe == null || truncated)
            {
                if (!raws.TryGetValue(item.Id, out RawRow raw))
                {
                    raw = new RawRow { Item = item, Truncated = truncated };
                    raw.ExtractorType = truncated ? null : Extractor(item, out raw.ExtractorPerMinute, out raw.NoteKey, out raw.PowerEach);
                    if (truncated)
                    {
                        raw.NoteKey = "planner.raw.truncated";
                    }
                    raws[item.Id] = raw;
                    rawOrder.Add(raw);
                }
                raw.PerMinute += perMinute;
                return;
            }
            double cycles = perMinute / OutAmount(recipe, item.Id);
            if (!rows.TryGetValue(recipe.Id, out RecipeRow row))
            {
                row = new RecipeRow { Recipe = recipe, BuildingType = recipe.Building, ForItem = item };
                foreach (RecipeDef o in options)
                {
                    if (o.Id != recipe.Id)
                    {
                        row.Alternatives.Add(o);
                    }
                }
                rows[recipe.Id] = row;
                order.Add(row);
            }
            row.CyclesPerMinute += cycles;
            if (row.ForItem == item)
            {
                row.ForItemPerMinute += perMinute;
            }
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Item == null || l.Role == RecipeRole.In || (l.Role == RecipeRole.Out && l.Item.Id == item.Id))
                {
                    continue;
                }
                if (!bys.TryGetValue(l.Item.Id, out Byproduct b))
                {
                    b = new Byproduct { Item = l.Item };
                    bys[l.Item.Id] = b;
                }
                b.PerMinute += cycles * l.Amount;
            }
            stack.Add(item.Id);
            foreach (RecipeLine l in recipe.Lines)
            {
                if (l.Role == RecipeRole.In && l.Item != null)
                {
                    Demand(l.Item, cycles * l.Amount, depth + 1, stack, choices, rows, order, raws, rawOrder, bys);
                }
            }
            stack.Remove(item.Id);
        }

        private static RecipeDef Choose(string itemId, List<RecipeDef> options, IReadOnlyDictionary<string, string> choices)
        {
            if (options.Count == 0)
            {
                return null;
            }
            if (choices != null && choices.TryGetValue(itemId, out string want))
            {
                foreach (RecipeDef r in options)
                {
                    if (r.Id == want)
                    {
                        return r;
                    }
                }
            }
            return options[0];
        }
    }
}
