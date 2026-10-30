using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>fg.TbLootDrop 一行的运行时视图。</summary>
    public sealed class LootDropDef
    {
        public string Id;
        public string Source;
        public string Faction;
        public ItemDef Item;
        public float Chance;
        public int Min;
        public int Max;
        public string Identity;
        public string[] Pool = Array.Empty<string>();
        public int SortOrder;
        /// <summary>行号（表里的顺序；掉落的确定性随机按它派生，表重排不影响已结算的掉落）。</summary>
        public int Index;

        public bool AppliesTo(string source, string faction) =>
            Source == source && (Faction == LootCatalog.AnyFaction || Faction == (faction ?? string.Empty));
    }

    /// <summary>
    /// FG8-LOOT-01（FG08 FGR-LOOT-010 普通掉落部分）：野外 / 远征普通掉落表的运行时入口（数据源 tools/cell_tables/fgdata_loot.py → fg.TbLootDrop）。
    /// 载入时与生成端 validate() 同一套检查：物品不在物品表 / 不是固体、概率不在 (0,1]、件数不合法、身份与协议不配（敌方加密物品必须带身份）、
    /// 模块池里的内容不存在——出问题的行整行拒绝并记进 <see cref="Problems"/>（不让一行坏数据掉出“身份不明”的东西）。
    /// 开销：只在载入 / 重载时 O(行数)；查询按来源 O(行数)（二十几行），只在野外击毁 / 据点被摧毁时调用。
    /// </summary>
    public static class LootCatalog
    {
        public const string SourceMinion = "minion";
        public const string SourceElite = "elite";
        public const string SourceOutpost = "outpost";
        public const string AnyFaction = "*";
        public const string IdentityNone = "none";
        public const string IdentityFirmwareSalvage = "firmware:salvage";
        public const string IdentityModule = "module";
        public const string IdentityLore = "lore";

        private static List<LootDropDef> _rows;
        private static readonly List<string> _problems = new List<string>();
        private static bool _overridden;
        private static int _builtItemRevision = -1;
        private static readonly Dictionary<string, List<string>> FirmwareByFaction = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static int _firmwareRevision = -1;
        private static int _firmwareEmgRevision = -1;
        private static int _firmwareStage = -1;

        public static int Revision { get; private set; } = 1;

        // ── 调参（fg.TbHomeTuning loot.*）──
        public static float MergeCells => Math.Max(0f, Tuning("loot.merge_cells", 3f));
        public static int PileMax => Math.Max(1, (int)Math.Round(Tuning("loot.pile_max", 40f)));
        public static float ClawBonus => Math.Max(0f, Tuning("loot.claw_bonus", 0.5f));
        public static int TagsMax => Math.Max(16, (int)Math.Round(Tuning("loot.tags_max", 512f)));
        public static float PerfMs => Math.Max(0.001f, Tuning("loot.perf_ms", 0.2f));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static IReadOnlyList<LootDropDef> Rows
        {
            get
            {
                Ensure();
                return _rows;
            }
        }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                Ensure();
                return _problems;
            }
        }

        public static void Reload()
        {
            _overridden = false;
            _rows = null;
            Revision++;
        }

        /// <summary>测试注入：用给定的行替换真实表（例如把某行概率改成 1 来确定地验证身份链路）。用完 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(IEnumerable<LootDropDef> rows)
        {
            _overridden = true;
            _rows = new List<LootDropDef>(rows ?? Array.Empty<LootDropDef>());
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Index = i;
            }
            _problems.Clear();
            Revision++;
        }

        public static void ResetForTests() => Reload();

        /// <summary>复制一份真实表的行（自检改一两行后注入）。</summary>
        public static List<LootDropDef> CloneRows()
        {
            var list = new List<LootDropDef>();
            foreach (LootDropDef r in Rows)
            {
                list.Add(new LootDropDef
                {
                    Id = r.Id, Source = r.Source, Faction = r.Faction, Item = r.Item, Chance = r.Chance, Min = r.Min, Max = r.Max,
                    Identity = r.Identity, Pool = r.Pool, SortOrder = r.SortOrder, Index = r.Index,
                });
            }
            return list;
        }

        private static void Ensure()
        {
            if (_overridden && _rows != null)
            {
                return;
            }
            if (_rows != null && _builtItemRevision == ItemCatalog.Revision)
            {
                return;
            }
            _rows = new List<LootDropDef>();
            _problems.Clear();
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbLootDrop == null)
                {
                    _problems.Add("配置表 fg.TbLootDrop 不存在");
                }
                else
                {
                    var raw = new List<GameConfig.fg.LootDrop>(t.TbLootDrop.DataList);
                    raw.Sort((a, b) => a.Source != b.Source ? string.CompareOrdinal(a.Source, b.Source)
                        : a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                    foreach (GameConfig.fg.LootDrop r in raw)
                    {
                        LootDropDef def = Validate(r.Id, r.Source, r.Faction, r.Item, r.Chance, r.Min, r.Max, r.Identity, r.Pool, r.SortOrder, out string problem);
                        if (def == null)
                        {
                            _problems.Add(problem);
                            continue;
                        }
                        def.Index = _rows.Count;
                        _rows.Add(def);
                    }
                }
            }
            catch (Exception ex)
            {
                _problems.Add("配置表 fg.TbLootDrop 读取失败：" + ex.Message);
            }
            _builtItemRevision = ItemCatalog.Revision;
            if (_problems.Count > 0)
            {
                Log.Error($"[LootCatalog] 掉落表有 {_problems.Count} 个问题，已拒绝对应的行：{string.Join("；", _problems)}（改 tools/cell_tables/fgdata_loot.py 后重新生成）");
            }
            Revision++;
        }

        /// <summary>一行的检查（与 fgdata_loot.validate 同一套规则）；不合法返回 null 并给出原因。自检用它构造坏数据。</summary>
        public static LootDropDef Validate(string id, string source, string faction, string itemId, float chance, int min, int max, string identity, string pool, int sortOrder,
            out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(id))
            {
                problem = "掉落行缺 ID";
                return null;
            }
            if (source != SourceMinion && source != SourceElite && source != SourceOutpost)
            {
                problem = $"掉落行 {id}：来源 {source} 不认识";
                return null;
            }
            if (!ItemCatalog.TryGet(itemId, out ItemDef item) || item.Form != ItemForm.Solid)
            {
                problem = $"掉落行 {id}：物品 {itemId} 不在物品表或不是固体（只有固体会掉在地上）";
                return null;
            }
            if (!(chance > 0f && chance <= 1f) || min < 1 || max < min)
            {
                problem = $"掉落行 {id}：概率 {chance} / 件数 {min}～{max} 不合法";
                return null;
            }
            bool enemy = item.Protocol == ItemProtocolKind.Enemy;
            bool known = identity == IdentityNone || identity == IdentityFirmwareSalvage || identity == IdentityModule || identity == IdentityLore;
            if (!known || enemy != (identity != IdentityNone))
            {
                problem = $"掉落行 {id}：身份 {identity} 与物品 {itemId} 的协议不配（敌方加密物品必须带身份，其余不带）";
                return null;
            }
            if ((identity == IdentityFirmwareSalvage && itemId != AnalysisCatalog.EncryptedFirmwareId)
                || (identity == IdentityLore && itemId != AnalysisCatalog.DataCoreId)
                || (identity == IdentityModule && itemId != AnalysisCatalog.UnparsedModuleId))
            {
                problem = $"掉落行 {id}：身份 {identity} 不适用于 {itemId}";
                return null;
            }
            string[] ids = Array.Empty<string>();
            if (identity == IdentityModule)
            {
                ids = (pool ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length == 0)
                {
                    problem = $"掉落行 {id}：模块身份没有组件池";
                    return null;
                }
                foreach (string c in ids)
                {
                    if (!Content.MechanicalContentFacade.TryGet(c, out _) || FirmwareKinds.IsFirmware(c))
                    {
                        problem = $"掉落行 {id}：组件池里的 {c} 不是能装配的组件 / 模块";
                        return null;
                    }
                }
            }
            return new LootDropDef
            {
                Id = id, Source = source, Faction = string.IsNullOrEmpty(faction) ? AnyFaction : faction, Item = item, Chance = chance, Min = min, Max = max,
                Identity = identity, Pool = ids, SortOrder = sortOrder,
            };
        }

        /// <summary>某来源、某阵营生效的行（按表顺序）。</summary>
        public static void RowsFor(string source, string faction, List<LootDropDef> into)
        {
            into.Clear();
            foreach (LootDropDef r in Rows)
            {
                if (r.AppliesTo(source, faction))
                {
                    into.Add(r);
                }
            }
        }

        /// <summary>
        /// 这个阵营“杂兵残骸”来源（fg.TbFirmwareKind source = salvage）的敌方固件（按 ID 排序，确定）。阵营不认识 / 没有时取所有阵营的。
        /// 表重载时重建（O(固件数)）；查询 O(1)。
        /// </summary>
        public static IReadOnlyList<string> EnemyFirmwareOf(string faction)
        {
            // EMG1-FW-02：只走通道声明的新固件随 emg.stage 开放（FGT-EMG-006），阶段 / 声明表变了也重建
            int stage = Emergence.EmgCatalog.EffectiveStage;
            if (_firmwareRevision != FirmwareKinds.CatalogRevision || _firmwareEmgRevision != Emergence.EmgCatalog.Revision || _firmwareStage != stage)
            {
                FirmwareByFaction.Clear();
                var all = new List<string>();
                foreach (GameConfig.fg.FirmwareKind r in FirmwareKinds.Rows)
                {
                    if (r == null || r.Protocol != "enemy" || r.Source != "salvage" || !FirmwareKinds.IsFirmware(r.Id) || !FirmwareKinds.IsObtainable(r.Id))
                    {
                        continue;
                    }
                    all.Add(r.Id);
                    string f = r.Faction ?? string.Empty;
                    if (!FirmwareByFaction.TryGetValue(f, out List<string> l))
                    {
                        FirmwareByFaction[f] = l = new List<string>();
                    }
                    l.Add(r.Id);
                }
                all.Sort(StringComparer.Ordinal);
                foreach (List<string> l in FirmwareByFaction.Values)
                {
                    l.Sort(StringComparer.Ordinal);
                }
                FirmwareByFaction[AnyFaction] = all;
                _firmwareRevision = FirmwareKinds.CatalogRevision;
                _firmwareEmgRevision = Emergence.EmgCatalog.Revision;
                _firmwareStage = stage;
            }
            return FirmwareByFaction.TryGetValue(faction ?? string.Empty, out List<string> list) && list.Count > 0
                ? list
                : FirmwareByFaction.TryGetValue(AnyFaction, out List<string> any) ? any : (IReadOnlyList<string>)Array.Empty<string>();
        }
    }
}
