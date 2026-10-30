using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>fg.TbLootPick 一行的运行时视图（三选一数据包的一个候选）。</summary>
    public sealed class LootPickDef
    {
        public string Id;
        public string Pool;
        public string Kind;
        public ItemDef Item;
        public string Target;
        public int Min;
        public int Max;
        public string Tier;
        public int TierIndex;
        public int Weight;
        public int SortOrder;

        /// <summary>“同一个数据包内不重复”与“从没见过标新”的键：种类 + 物品 + 身份（同一种材料不同件数算同一个）。</summary>
        public string Key => LootPickCatalog.KeyOf(Kind, Item?.Id, Target == LootPickCatalog.TargetLore ? string.Empty : Target);
    }

    /// <summary>
    /// FG8-LOOT-02（FG08 FGR-LOOT-021 候选池、FGR-LOOT-030 货舱占用）：三选一候选池 fg.TbLootPick 与 loot.pick.* / loot.cargo.* 调参的运行时入口
    /// （数据源 tools/cell_tables/fgdata_lootpick.py）。载入时与生成端 validate() 同一套检查，坏行整行拒绝并记进 <see cref="Problems"/>。
    /// 开销：载入 / 重载 O(行数)；查询按候选池 O(行数)（七十几行），只在生成数据包时调用。
    /// </summary>
    public static class LootPickCatalog
    {
        public const string PoolAny = "*";
        public const string KindItem = "item";
        public const string KindFirmware = "firmware";
        public const string KindCracked = "firmware_cracked";
        public const string KindModule = "module";
        public const string KindCore = "core";
        public const string TargetLore = "lore";
        public const string TargetNone = "none";

        /// <summary>四档稀有度（FGR-LOOT-021：普通、精良、稀有、原型），下标越大越稀有；“稀有及以上” = 下标 ≥ <see cref="RareIndex"/>。</summary>
        public static readonly string[] Tiers = { "common", "fine", "rare", "prototype" };
        public const int RareIndex = 2;

        private static List<LootPickDef> _rows;
        private static readonly List<string> _problems = new List<string>();
        private static bool _overridden;
        private static int _builtItemRevision = -1;

        public static int Revision { get; private set; } = 1;

        // ── 调参 ──
        public static float Weight(int tierIndex) => Math.Max(0f, Tuning("loot.pick.weight." + Tiers[Math.Clamp(tierIndex, 0, Tiers.Length - 1)], DefaultWeights[Math.Clamp(tierIndex, 0, 3)]));
        private static readonly float[] DefaultWeights = { 60f, 30f, 9f, 1f };
        public static int Pity => Math.Max(1, (int)Math.Round(Tuning("loot.pick.pity", 5f)));
        public static int CandidateCount => Math.Max(1, (int)Math.Round(Tuning("loot.pick.candidates", 3f)));
        public static int TechFor(string tier) => Math.Max(0, (int)Math.Round(Tuning("loot.pick.tech." + (tier ?? "common"), 1f)));
        public static float ShareRadius => Math.Max(0f, Tuning("loot.pick.share_radius", 30f));
        public static float ClaimRadius => Math.Max(0.5f, Tuning("loot.pick.claim_radius", 6f));
        public static float DepotRadius => Math.Max(1f, Tuning("loot.pick.depot_radius", 24f));
        public static float CheckSeconds => Math.Max(0.1f, Tuning("loot.pick.check_seconds", 1f));
        public static int LogMax => Math.Max(1, (int)Math.Round(Tuning("loot.pick.log_max", 64f)));
        public static float PerfMs => Math.Max(0.001f, Tuning("loot.pick.perf_ms", 0.5f));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static int TierIndexOf(string tier)
        {
            int i = Array.IndexOf(Tiers, tier);
            return i < 0 ? 0 : i;
        }

        public static bool IsRareOrBetter(string tier) => TierIndexOf(tier) >= RareIndex;

        public static string KeyOf(string kind, string itemId, string targetId) => (kind ?? string.Empty) + "|" + (itemId ?? string.Empty) + "|" + (targetId ?? string.Empty);

        // ── 货舱空间（FGR-LOOT-030；FG8-LOOT-03 的货舱界面沿用）──

        /// <summary>FG8-LOOT-03：材料每这么多件占 1 格（loot.cargo.material_per_space）。</summary>
        public static int MaterialPerSpace => Math.Max(1, (int)Math.Round(Tuning("loot.cargo.material_per_space", 10f)));

        /// <summary>按“件”占空间的物品每件占几格（固件 1、组件 3、数据核心 2、未解析模块 3、人类遗产 2、黑匣子 1，初值）；不是这类（= 材料）返回 false。
        /// <see cref="IsPerUnit"/> 与 <see cref="SpaceOf"/> 共用这一张表，加新的按件物品只改这里。</summary>
        private static bool TryPerUnitSpace(string itemId, out int per)
        {
            switch (itemId)
            {
                case AnalysisCatalog.EncryptedFirmwareId:
                case ItemCatalog.FirmwareChipId:
                    per = (int)Math.Round(Tuning("loot.cargo.firmware", 1f));
                    return true;
                case AnalysisCatalog.UnparsedModuleId:
                    per = (int)Math.Round(Tuning("loot.cargo.module", 3f));
                    return true;
                case AnalysisCatalog.DataCoreId:
                    per = (int)Math.Round(Tuning("loot.cargo.core", 2f));
                    return true;
                case "combat_component":
                case "structure_module":
                case "drop_pod":
                    per = (int)Math.Round(Tuning("loot.cargo.component", 3f));
                    return true;
                case "human_legacy":
                    per = (int)Math.Round(Tuning("loot.cargo.legacy", 2f));
                    return true;
                case BlackBoxService.ContentId:
                    per = (int)Math.Round(Tuning("loot.cargo.blackbox", 1f));
                    return true;
            }
            per = 0;
            return false;
        }

        /// <summary>FG8-LOOT-03：这种物品按“件”占空间（固件 / 组件 / 数据核心 / 未解析模块 / 人类遗产 / 黑匣子）；否则是材料，按每 N 件 1 格（可以拆开装）。</summary>
        public static bool IsPerUnit(string itemId) => TryPerUnitSpace(itemId, out _);

        /// <summary>FG8-LOOT-03：货舱记录是不是“材料”（普通物品、按件数计空间、可以拆开装 / 同机同种合并成一条）。</summary>
        public static bool IsBulk(string kind, string itemId) => kind == KindItem && !IsPerUnit(itemId);

        /// <summary>一份物品占几格货舱：材料每 loot.cargo.material_per_space 件 1 格（向上取整；同一台机器的同种材料按总件数算，见 LootPickService.UsedSpace）；
        /// 按件计的物品见 <see cref="TryPerUnitSpace"/>。</summary>
        public static int SpaceOf(string itemId, int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }
            if (TryPerUnitSpace(itemId, out int unit))
            {
                return amount * unit;
            }
            int per = MaterialPerSpace;
            return (amount + per - 1) / per;
        }

        /// <summary>机器的货舱容量：底盘（搬运轮式 12 / 维修悬浮 6 / 战斗履带 4，初值）+ 装了货舱结构模块另加 6。O(该蓝图版本数)。</summary>
        public static int CapacityOf(CampaignState s, MachineRecord m)
        {
            if (m == null)
            {
                return 0;
            }
            string arch = ChassisCatalog.ResolveArchetype(m.ChassisId);
            float cap = arch == ChassisCatalog.ChassisWheelId ? Tuning("loot.cargo.cap.wheel", 12f)
                : arch == ChassisCatalog.ChassisHoverId ? Tuning("loot.cargo.cap.hover", 6f)
                : Tuning("loot.cargo.cap.track", 4f);
            BlueprintRecord bp = s != null && !string.IsNullOrEmpty(m.BlueprintId) ? BlueprintEditorService.Find(s, m.BlueprintId) : null;
            BlueprintVersionRecord v = bp?.Versions?.FirstOrDefault(x => x != null && x.Version == m.BlueprintVersion);
            if (v != null && v.StructureId == ComponentCatalog.StructCargoId)
            {
                cap += Tuning("loot.cargo.cap.module", 6f);
            }
            return Math.Max(0, (int)Math.Round(cap));
        }

        // ── 表 ──

        public static IReadOnlyList<LootPickDef> Rows
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

        public static void ResetForTests() => Reload();

        /// <summary>测试注入：用给定的行替换真实表。用完 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(IEnumerable<LootPickDef> rows)
        {
            _overridden = true;
            _rows = new List<LootPickDef>(rows ?? Array.Empty<LootPickDef>());
            _problems.Clear();
            Revision++;
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
            _rows = new List<LootPickDef>();
            _problems.Clear();
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbLootPick == null)
                {
                    _problems.Add("配置表 fg.TbLootPick 不存在");
                }
                else
                {
                    var raw = new List<GameConfig.fg.LootPick>(t.TbLootPick.DataList);
                    raw.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (GameConfig.fg.LootPick r in raw)
                    {
                        LootPickDef def = Validate(r.Id, r.Pool, r.Kind, r.Item, r.Target, r.Min, r.Max, r.Tier, r.Weight, r.SortOrder, out string problem);
                        if (def != null && !keys.Add(def.Pool + "#" + def.Key))
                        {
                            def = null;
                            problem = $"候选行 {r.Id}：同一个候选池里重复的候选";
                        }
                        if (def == null)
                        {
                            _problems.Add(problem);
                            continue;
                        }
                        _rows.Add(def);
                    }
                }
            }
            catch (Exception ex)
            {
                _problems.Add("配置表 fg.TbLootPick 读取失败：" + ex.Message);
            }
            _builtItemRevision = ItemCatalog.Revision;
            if (_problems.Count > 0)
            {
                Log.Error($"[LootPickCatalog] 三选一候选池有 {_problems.Count} 个问题，已拒绝对应的行：{string.Join("；", _problems)}（改 tools/cell_tables/fgdata_lootpick.py 后重新生成）");
            }
            Revision++;
        }

        /// <summary>一行的检查（与 fgdata_lootpick.validate 同一套规则）；不合法返回 null 并给出原因。自检用它构造坏数据。</summary>
        public static LootPickDef Validate(string id, string pool, string kind, string itemId, string target, int min, int max, string tier, int weight, int sortOrder,
            out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(id))
            {
                problem = "候选行缺 ID";
                return null;
            }
            if (pool != PoolAny && pool != "silent" && pool != "foundry" && pool != "clarity" && pool != "overclock")
            {
                problem = $"候选行 {id}：候选池 {pool} 不认识";
                return null;
            }
            if (Array.IndexOf(Tiers, tier) < 0)
            {
                problem = $"候选行 {id}：稀有度 {tier} 不认识";
                return null;
            }
            if (!ItemCatalog.TryGet(itemId, out ItemDef item) || item.Form != ItemForm.Solid)
            {
                problem = $"候选行 {id}：物品 {itemId} 不在物品表或不是固体";
                return null;
            }
            if (min < 1 || max < min || weight < 1)
            {
                problem = $"候选行 {id}：件数 {min}～{max} / 权重 {weight} 不合法";
                return null;
            }
            bool enemy = item.Protocol == ItemProtocolKind.Enemy;
            switch (kind)
            {
                case KindItem:
                    if (enemy || (target ?? TargetNone) != TargetNone)
                    {
                        problem = $"候选行 {id}：普通物品候选不能是敌方加密物品、不带身份";
                        return null;
                    }
                    break;
                case KindFirmware:
                case KindCracked:
                    if (itemId != AnalysisCatalog.EncryptedFirmwareId || !FirmwareKinds.IsFirmware(target))
                    {
                        problem = $"候选行 {id}：固件候选的物品必须是加密固件、身份是固件表里的固件（{target}）";
                        return null;
                    }
                    break;
                case KindModule:
                    if (itemId != AnalysisCatalog.UnparsedModuleId || !MechanicalContentFacade.TryGet(target, out _) || FirmwareKinds.IsFirmware(target))
                    {
                        problem = $"候选行 {id}：未解析模块候选的身份必须是能装配的组件 / 模块（{target}）";
                        return null;
                    }
                    break;
                case KindCore:
                    if (itemId != AnalysisCatalog.DataCoreId || target != TargetLore)
                    {
                        problem = $"候选行 {id}：数据核心候选的身份写 lore";
                        return null;
                    }
                    break;
                default:
                    problem = $"候选行 {id}：种类 {kind} 不认识";
                    return null;
            }
            if (kind != KindItem && (min != 1 || max != 1))
            {
                problem = $"候选行 {id}：带身份的候选一次 1 件";
                return null;
            }
            return new LootPickDef
            {
                Id = id, Pool = pool, Kind = kind, Item = item, Target = kind == KindItem ? string.Empty : target, Min = min, Max = max,
                Tier = tier, TierIndex = Array.IndexOf(Tiers, tier), Weight = weight, SortOrder = sortOrder,
            };
        }

        /// <summary>候选池 = 这个阵营的池 + 通用池（按表顺序；阵营不认识 / 空 = 只有通用池）。
        /// EMG1-FW-02（FGT-EMG-006）：固件候选只收此刻能拿到的（<see cref="Signal.FirmwareKinds.IsObtainable"/>：声明未生效的新固件不进候选池）。</summary>
        public static void PoolFor(string faction, List<LootPickDef> into)
        {
            into.Clear();
            foreach (LootPickDef r in Rows)
            {
                if ((r.Kind == KindFirmware || r.Kind == KindCracked) && !Signal.FirmwareKinds.IsObtainable(r.Target))
                {
                    continue;
                }
                if (r.Pool == PoolAny || (!string.IsNullOrEmpty(faction) && r.Pool == faction))
                {
                    into.Add(r);
                }
            }
        }

        public static LootPickDef Find(string rowId)
        {
            foreach (LootPickDef r in Rows)
            {
                if (r.Id == rowId)
                {
                    return r;
                }
            }
            return null;
        }
    }
}
