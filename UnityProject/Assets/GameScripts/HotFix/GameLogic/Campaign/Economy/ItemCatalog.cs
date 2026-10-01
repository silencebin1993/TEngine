using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>物品形态（fg.TbEcoItem.form）。</summary>
    public enum ItemForm : byte
    {
        /// <summary>固体：存进家园仓库，可以上传送带。</summary>
        Solid = 0,
        /// <summary>流体：走管线与储罐（fluidId 指向 fg.TbFluid），不进仓库。</summary>
        Fluid = 1,
        /// <summary>核心保管库：关键材料、人类遗产；不上传送带。</summary>
        Vault = 2,
        /// <summary>数字资源：技术数据、研究点；不占物理空间。</summary>
        Digital = 3,
        /// <summary>产出后成为别的实体（机器进名册、固件芯片进固件库），不进仓库。</summary>
        Entity = 4,
    }

    /// <summary>配方行的角色（fg.TbRecipeIo.role）。</summary>
    public enum RecipeRole : byte
    {
        In = 0,
        Out = 1,
        /// <summary>副产品：必须有去处，否则堵线（FG4-ECO-10）。</summary>
        Byproduct = 2,
    }

    /// <summary>一种物品（fg.TbEcoItem 一行的运行时视图）。</summary>
    public sealed class ItemDef
    {
        public string Id;
        /// <summary>传送带物品编号（0 = 不上传送带）。</summary>
        public ushort BeltId;
        public ItemForm Form;
        public string Tier;
        public int FluidId;
        public string NameKey;
        public string DescKey;
        public string SourceKey;
        public string UseKey;
        public Color Color = Color.gray;
        public string ColorHex = "#808080";
        public string Shape = "square";
        public int SortOrder;
        /// <summary>家园资源类型字符串（Demo 起的地面物 / 货舱 / 返还用它）：废料 "Scrap"、技术数据 "TechData"，其余 = 物品 ID。</summary>
        public string ResourceType;
        /// <summary>图鉴里一开始就能看（原料、中间品、成品、流体、数字资源）；远征物、关键材料、终局第一次拿到时解锁。</summary>
        public bool CodexAlwaysOpen;
        /// <summary>FG4-ECO-02（FGR-ECO-071）：回收站分解一件得到的废料（只有固体 &gt; 0；少于做它花掉的废料当量，FG16 FGR-BAL-050）。</summary>
        public int RecycleScrap;

        public bool IsBelt => BeltId != 0;
        public string Name => GameText.Get(NameKey);
    }

    /// <summary>配方的一行输入 / 产出。</summary>
    public readonly struct RecipeLine
    {
        public readonly ItemDef Item;
        public readonly int Amount;
        public readonly RecipeRole Role;

        public RecipeLine(ItemDef item, int amount, RecipeRole role)
        {
            Item = item;
            Amount = amount;
            Role = role;
        }
    }

    /// <summary>一条配方（fg.TbRecipe + fg.TbRecipeIo 的运行时视图）。</summary>
    public sealed class RecipeDef
    {
        public string Id;
        public string NameKey;
        public string Building;
        public string BuildingNameKey;
        /// <summary>1x 下的现实秒（FGR-ECO-002）。</summary>
        public float Seconds;
        public string Kind;
        public bool Provisional;
        public int SortOrder;
        public RecipeLine[] Lines = Array.Empty<RecipeLine>();

        public string Name => GameText.Get(NameKey);

        /// <summary>换成游戏时钟步数（FGR-ECO-002“内部换算为游戏时钟”；倍速只改每帧跑几步，不改步数）。</summary>
        public long DurationTicks(int stepHz) => Math.Max(1L, (long)Math.Round(Seconds * Math.Max(1, stepHz)));
    }

    /// <summary>
    /// FG4-ECO-01（FG04 FGR-ECO-001 / 002）：物品、流体与配方表的运行时入口。数据源 tools/cell_tables/fgdata_eco.py → fg.TbEcoItem / fg.TbRecipe / fg.TbRecipeIo（流体与 fg.TbFluid 对应）。
    /// - 载入时做一遍与生成端 validate() 相同的检查（<see cref="Build"/>）：悬空引用（配方输入 / 产出不在物品表）、只有固体上传送带、流体对得上 fg.TbFluid、
    ///   数字资源与保管库物品不进产线、配方至少一进一出……出问题的配方<b>整条拒绝</b>（不让半条配方在运行时“少一个输入”地跑），并把原因记进 <see cref="Problems"/> 与日志。
    /// - 派生索引：按 ID / 传送带编号 / 家园资源类型查物品；每种物品“由哪些配方产出 / 被哪些配方消耗”（图鉴的来源与用途）。
    /// 开销：只在载入 / 重载时 O(行数)；查询 O(1)。
    /// </summary>
    public static class ItemCatalog
    {
        public const string ScrapId = "scrap";
        public const string TechDataId = "tech_data";
        public const string ResearchPointsId = "research_points";
        public const string MachineId = "machine";
        public const string FirmwareChipId = "firmware_chip";

        /// <summary>表数据的纯结构（Luban 行转成它再 <see cref="Build"/>；自检用它构造坏数据）。</summary>
        public struct ItemRow
        {
            public string Id;
            public int BeltId;
            public string Form;
            public string Tier;
            public int FluidId;
            public string NameKey;
            public string DescKey;
            public string SourceKey;
            public string UseKey;
            public string Color;
            public string Shape;
            public int SortOrder;
            /// <summary>FG4-ECO-02：回收站分解一件得到的废料。</summary>
            public int RecycleScrap;
        }

        public struct RecipeRow
        {
            public string Id;
            public string NameKey;
            public string Building;
            public string BuildingNameKey;
            public float Seconds;
            public string Kind;
            public int Provisional;
            public int SortOrder;
        }

        public struct IoRow
        {
            public int Id;
            public string Recipe;
            public string Item;
            public int Amount;
            public string Role;
        }

        /// <summary>一次构建的结果（<see cref="Build"/>）。</summary>
        public sealed class Data
        {
            public readonly List<ItemDef> Items = new List<ItemDef>();
            public readonly List<RecipeDef> Recipes = new List<RecipeDef>();
            public readonly Dictionary<string, ItemDef> ById = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
            public readonly Dictionary<ushort, ItemDef> ByBelt = new Dictionary<ushort, ItemDef>();
            public readonly Dictionary<int, ItemDef> ByFluid = new Dictionary<int, ItemDef>();
            public readonly Dictionary<string, RecipeDef> RecipeById = new Dictionary<string, RecipeDef>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<RecipeDef>> Producing = new Dictionary<string, List<RecipeDef>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<RecipeDef>> Consuming = new Dictionary<string, List<RecipeDef>>(StringComparer.Ordinal);
            public readonly List<string> Problems = new List<string>();
            /// <summary>因问题被整条拒绝的配方 ID。</summary>
            public readonly List<string> RejectedRecipes = new List<string>();
        }

        private static Data _data;
        private static bool _overridden;
        private static string _loadError;
        private static readonly List<RecipeDef> Empty = new List<RecipeDef>();

        public static int Revision { get; private set; } = 1;

        /// <summary>表没加载上的原因（null = 正常）。</summary>
        public static string LoadError
        {
            get
            {
                Ensure();
                return _loadError;
            }
        }

        /// <summary>载入检查发现的问题（空 = 表完全合法）。</summary>
        public static IReadOnlyList<string> Problems
        {
            get
            {
                Ensure();
                return _data.Problems;
            }
        }

        public static IReadOnlyList<string> RejectedRecipes
        {
            get
            {
                Ensure();
                return _data.RejectedRecipes;
            }
        }

        /// <summary>全部物品（按 sortOrder）。</summary>
        public static IReadOnlyList<ItemDef> Items
        {
            get
            {
                Ensure();
                return _data.Items;
            }
        }

        /// <summary>全部合法配方（按 sortOrder）。</summary>
        public static IReadOnlyList<RecipeDef> Recipes
        {
            get
            {
                Ensure();
                return _data.Recipes;
            }
        }

        public static bool TryGet(string id, out ItemDef def)
        {
            Ensure();
            def = null;
            return id != null && _data.ById.TryGetValue(id, out def);
        }

        public static ItemDef Find(string id) => TryGet(id, out ItemDef d) ? d : null;

        public static bool TryGetByBelt(ushort beltId, out ItemDef def)
        {
            Ensure();
            def = null;
            return beltId != 0 && _data.ByBelt.TryGetValue(beltId, out def);
        }

        public static bool TryGetByFluid(int fluidId, out ItemDef def)
        {
            Ensure();
            def = null;
            return fluidId != 0 && _data.ByFluid.TryGetValue(fluidId, out def);
        }

        public static bool TryGetRecipe(string id, out RecipeDef def)
        {
            Ensure();
            def = null;
            return id != null && _data.RecipeById.TryGetValue(id, out def);
        }

        /// <summary>
        /// 家园资源类型 → 物品：Demo 起的 "Scrap" / "TechData"、物品 ID、FG3-LOG-02 起地面物的 "item:编号"（按传送带编号找）。找不到返回 false。
        /// </summary>
        public static bool TryGetByResource(string resourceType, out ItemDef def)
        {
            def = null;
            if (string.IsNullOrEmpty(resourceType))
            {
                return false;
            }
            if (resourceType == CampaignEconomyLedger.ResourceScrap)
            {
                return TryGet(ScrapId, out def);
            }
            if (resourceType == CampaignEconomyLedger.ResourceTechData)
            {
                return TryGet(TechDataId, out def);
            }
            if (resourceType.StartsWith("item:", StringComparison.Ordinal))
            {
                return ushort.TryParse(resourceType.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort belt) && TryGetByBelt(belt, out def);
            }
            return TryGet(resourceType, out def);
        }

        /// <summary>物品 ID → 家园资源类型（废料 "Scrap"、技术数据 "TechData"，其余就是物品 ID）。</summary>
        public static string ResourceTypeOf(string itemId) =>
            itemId == ScrapId ? CampaignEconomyLedger.ResourceScrap : itemId == TechDataId ? CampaignEconomyLedger.ResourceTechData : itemId;

        /// <summary>由哪些配方产出（产出或副产品；按配方排序）。</summary>
        public static IReadOnlyList<RecipeDef> ProducedBy(string itemId)
        {
            Ensure();
            return itemId != null && _data.Producing.TryGetValue(itemId, out List<RecipeDef> l) ? l : Empty;
        }

        /// <summary>被哪些配方当输入。</summary>
        public static IReadOnlyList<RecipeDef> ConsumedBy(string itemId)
        {
            Ensure();
            return itemId != null && _data.Consuming.TryGetValue(itemId, out List<RecipeDef> l) ? l : Empty;
        }

        /// <summary>玩家看到的物品名（表里没有时“未知物品 #编号”）。</summary>
        public static string NameOf(string itemId) =>
            TryGet(itemId, out ItemDef d) ? d.Name : GameText.Format("item.unknown", itemId ?? "?");

        // ── 载入 ─────────────────────────────────────────────────────────────────

        public static void Reload()
        {
            _overridden = false;
            _data = null;
            _loadError = null;
            Revision++;
        }

        /// <summary>测试注入：用 <see cref="Build"/> 出来的数据替换真实表。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(Data data)
        {
            _overridden = true;
            _data = data ?? new Data();
            _loadError = data == null ? "测试注入：物品表为空" : null;
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void Ensure()
        {
            if (_data != null || _overridden)
            {
                return;
            }
            var items = new List<ItemRow>();
            var recipes = new List<RecipeRow>();
            var ios = new List<IoRow>();
            var fluids = new Dictionary<int, string>();
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbEcoItem == null || t.TbRecipe == null || t.TbRecipeIo == null || t.TbFluid == null)
                {
                    _loadError = "配置表 fg.TbEcoItem / fg.TbRecipe / fg.TbRecipeIo / fg.TbFluid 不存在";
                }
                else
                {
                    foreach (GameConfig.fg.EcoItem r in t.TbEcoItem.DataList)
                    {
                        items.Add(new ItemRow
                        {
                            Id = r.Id, BeltId = r.BeltId, Form = r.Form, Tier = r.Tier, FluidId = r.FluidId, NameKey = r.NameKey, DescKey = r.DescKey,
                            SourceKey = r.SourceKey, UseKey = r.UseKey, Color = r.Color, Shape = r.Shape, SortOrder = r.SortOrder,
                            RecycleScrap = r.RecycleScrap,
                        });
                    }
                    foreach (GameConfig.fg.Recipe r in t.TbRecipe.DataList)
                    {
                        recipes.Add(new RecipeRow
                        {
                            Id = r.Id, NameKey = r.NameKey, Building = r.Building, BuildingNameKey = r.BuildingNameKey, Seconds = r.Seconds, Kind = r.Kind,
                            Provisional = r.Provisional, SortOrder = r.SortOrder,
                        });
                    }
                    foreach (GameConfig.fg.RecipeIo r in t.TbRecipeIo.DataList)
                    {
                        ios.Add(new IoRow { Id = r.Id, Recipe = r.Recipe, Item = r.Item, Amount = r.Amount, Role = r.Role });
                    }
                    foreach (GameConfig.fg.Fluid f in t.TbFluid.DataList)
                    {
                        fluids[f.Id] = f.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                _loadError = "配置表读取失败：" + ex.Message;
            }
            _data = Build(items, recipes, ios, fluids, BeltScrapId());
            if (_loadError != null)
            {
                Log.Error($"[ItemCatalog] {_loadError}（改 tools/cell_tables/fgdata_eco.py 后重新生成）");
            }
            else if (_data.Problems.Count > 0)
            {
                Log.Error($"[ItemCatalog] 物品 / 配方表有 {_data.Problems.Count} 个问题，已拒绝 {_data.RejectedRecipes.Count} 条配方：{string.Join("；", _data.Problems)}");
            }
            Revision++;
        }

        private static ushort BeltScrapId()
        {
            try
            {
                return (ushort)Math.Max(1, Math.Min(ushort.MaxValue - 1, Grid.GridContent.TuningInt("logistics.item.scrap_id")));
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>
        /// 由纯结构构建目录并检查（与 fgdata_eco.validate() 同一套规则）。出问题的物品行不进目录；配方引用了不存在的物品、数量 ≤ 0、
        /// 没有输入或没有产出、把数字资源 / 保管库物品放进产线时，整条配方拒绝。
        /// </summary>
        public static Data Build(IReadOnlyList<ItemRow> items, IReadOnlyList<RecipeRow> recipes, IReadOnlyList<IoRow> ios,
            IReadOnlyDictionary<int, string> fluids, ushort scrapBeltId)
        {
            var d = new Data();
            foreach (ItemRow r in items ?? Array.Empty<ItemRow>())
            {
                if (string.IsNullOrEmpty(r.Id))
                {
                    d.Problems.Add("物品行缺 ID");
                    continue;
                }
                if (d.ById.ContainsKey(r.Id))
                {
                    d.Problems.Add($"物品 {r.Id} 重复");
                    continue;
                }
                if (!TryParseForm(r.Form, out ItemForm form))
                {
                    d.Problems.Add($"物品 {r.Id}：形态 {r.Form} 不认识");
                    continue;
                }
                if (r.BeltId < 0 || r.BeltId > 999 || (r.BeltId != 0) != (form == ItemForm.Solid))
                {
                    d.Problems.Add($"物品 {r.Id}：只有固体上传送带（beltId {r.BeltId}，形态 {r.Form}）");
                    continue;
                }
                if (r.BeltId != 0 && d.ByBelt.ContainsKey((ushort)r.BeltId))
                {
                    d.Problems.Add($"物品 {r.Id}：传送带编号 {r.BeltId} 重复");
                    continue;
                }
                bool fluidOk = form == ItemForm.Fluid
                    ? r.FluidId > 0 && fluids != null && fluids.TryGetValue(r.FluidId, out string key) && key == r.Id
                    : r.FluidId == 0;
                if (!fluidOk)
                {
                    d.Problems.Add($"物品 {r.Id}：流体编号 {r.FluidId} 与 fg.TbFluid 对不上");
                    continue;
                }
                var def = new ItemDef
                {
                    Id = r.Id,
                    BeltId = (ushort)r.BeltId,
                    Form = form,
                    Tier = r.Tier ?? string.Empty,
                    FluidId = r.FluidId,
                    NameKey = r.NameKey,
                    DescKey = r.DescKey,
                    SourceKey = r.SourceKey,
                    UseKey = r.UseKey,
                    Shape = string.IsNullOrEmpty(r.Shape) ? "square" : r.Shape,
                    SortOrder = r.SortOrder,
                    ResourceType = ResourceTypeOf(r.Id),
                    CodexAlwaysOpen = r.Tier != "expedition" && r.Tier != "key" && r.Tier != "endgame",
                    // FG4-ECO-02：只有固体能进回收站；表里给非固体写了数也按 0（不会把流体“分解”成废料）。
                    RecycleScrap = form == ItemForm.Solid ? Math.Max(0, r.RecycleScrap) : 0,
                };
                if (!string.IsNullOrEmpty(r.Color) && ColorUtility.TryParseHtmlString(r.Color, out Color c))
                {
                    def.Color = c;
                    def.ColorHex = r.Color;
                }
                d.Items.Add(def);
                d.ById[def.Id] = def;
                if (def.BeltId != 0)
                {
                    d.ByBelt[def.BeltId] = def;
                }
                if (def.FluidId != 0)
                {
                    d.ByFluid[def.FluidId] = def;
                }
            }
            if (fluids != null)
            {
                foreach (KeyValuePair<int, string> f in fluids)
                {
                    if (!d.ByFluid.ContainsKey(f.Key))
                    {
                        d.Problems.Add($"fg.TbFluid 的 {f.Value} 在物品表里没有流体行");
                    }
                }
            }
            if (!d.ById.TryGetValue(ScrapId, out ItemDef scrap) || scrap.BeltId != scrapBeltId)
            {
                d.Problems.Add($"废料的传送带编号必须等于 logistics.item.scrap_id（{scrapBeltId}）");
            }
            d.Items.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));

            var linesOf = new Dictionary<string, List<IoRow>>(StringComparer.Ordinal);
            var ioIds = new HashSet<int>();
            foreach (IoRow io in ios ?? Array.Empty<IoRow>())
            {
                if (!ioIds.Add(io.Id))
                {
                    d.Problems.Add($"配方行 {io.Id} 重复");
                }
                if (io.Recipe == null)
                {
                    continue;
                }
                if (!linesOf.TryGetValue(io.Recipe, out List<IoRow> l))
                {
                    linesOf[io.Recipe] = l = new List<IoRow>();
                }
                l.Add(io);
            }
            foreach (RecipeRow r in recipes ?? Array.Empty<RecipeRow>())
            {
                if (string.IsNullOrEmpty(r.Id) || d.RecipeById.ContainsKey(r.Id))
                {
                    d.Problems.Add($"配方 {r.Id ?? "(空)"}：ID 为空或重复");
                    continue;
                }
                var problems = new List<string>();
                if (!(r.Seconds > 0f))
                {
                    problems.Add("时间必须 > 0");
                }
                if (r.Kind != "fixed" && r.Kind != "component_table" && r.Kind != "blueprint" && r.Kind != "firmware")
                {
                    problems.Add($"类型 {r.Kind} 不认识");
                }
                var lines = new List<RecipeLine>();
                bool hasIn = false;
                bool hasOut = false;
                if (linesOf.TryGetValue(r.Id, out List<IoRow> rows))
                {
                    rows.Sort((a, b) => a.Id.CompareTo(b.Id));
                    foreach (IoRow io in rows)
                    {
                        if (!d.ById.TryGetValue(io.Item ?? string.Empty, out ItemDef item))
                        {
                            problems.Add($"{RoleText(io.Role)}的物品 {io.Item} 不在物品表");
                            continue;
                        }
                        if (io.Amount <= 0)
                        {
                            problems.Add($"{item.Id} 的数量必须 > 0");
                            continue;
                        }
                        if (!TryParseRole(io.Role, out RecipeRole role))
                        {
                            problems.Add($"{item.Id} 的角色 {io.Role} 不认识");
                            continue;
                        }
                        if (item.Form == ItemForm.Digital || item.Form == ItemForm.Vault)
                        {
                            problems.Add($"{item.Id} 是数字资源 / 保管库物品，不进产线");
                            continue;
                        }
                        hasIn |= role == RecipeRole.In;
                        hasOut |= role == RecipeRole.Out;
                        lines.Add(new RecipeLine(item, io.Amount, role));
                    }
                }
                if (!hasIn || !hasOut)
                {
                    problems.Add("至少要有一个输入和一个产出");
                }
                if (problems.Count > 0)
                {
                    d.RejectedRecipes.Add(r.Id);
                    foreach (string p in problems)
                    {
                        d.Problems.Add($"配方 {r.Id}：{p}");
                    }
                    continue;
                }
                var def = new RecipeDef
                {
                    Id = r.Id,
                    NameKey = r.NameKey,
                    Building = r.Building,
                    BuildingNameKey = r.BuildingNameKey,
                    Seconds = r.Seconds,
                    Kind = r.Kind,
                    Provisional = r.Provisional != 0,
                    SortOrder = r.SortOrder,
                    Lines = lines.ToArray(),
                };
                d.Recipes.Add(def);
                d.RecipeById[def.Id] = def;
            }
            d.Recipes.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            foreach (RecipeDef r in d.Recipes)
            {
                foreach (RecipeLine l in r.Lines)
                {
                    Dictionary<string, List<RecipeDef>> map = l.Role == RecipeRole.In ? d.Consuming : d.Producing;
                    if (!map.TryGetValue(l.Item.Id, out List<RecipeDef> list))
                    {
                        map[l.Item.Id] = list = new List<RecipeDef>();
                    }
                    if (!list.Contains(r))
                    {
                        list.Add(r);
                    }
                }
            }
            return d;
        }

        private static string RoleText(string role) => role == "in" ? "输入" : role == "byproduct" ? "副产品" : "产出";

        public static bool TryParseForm(string text, out ItemForm form)
        {
            switch (text)
            {
                case "solid": form = ItemForm.Solid; return true;
                case "fluid": form = ItemForm.Fluid; return true;
                case "vault": form = ItemForm.Vault; return true;
                case "digital": form = ItemForm.Digital; return true;
                case "entity": form = ItemForm.Entity; return true;
                default: form = ItemForm.Solid; return false;
            }
        }

        public static bool TryParseRole(string text, out RecipeRole role)
        {
            switch (text)
            {
                case "in": role = RecipeRole.In; return true;
                case "out": role = RecipeRole.Out; return true;
                case "byproduct": role = RecipeRole.Byproduct; return true;
                default: role = RecipeRole.In; return false;
            }
        }
    }
}
