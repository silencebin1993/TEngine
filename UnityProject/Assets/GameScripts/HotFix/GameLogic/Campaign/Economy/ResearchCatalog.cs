using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>研发树的一个分支（fg.TbResearchBranch 一行的运行时视图）。</summary>
    public sealed class ResearchBranchDef
    {
        public string Id;
        public string NameKey;
        /// <summary>阵营分支（破解该阵营第一件技术后开放）。</summary>
        public bool IsFaction;
        /// <summary>阵营分支的阵营 ID（fg.TbFirmwareKind.faction）；通用分支为空。</summary>
        public string Faction;
        public string Color;
        public string Glyph;
        public int SortOrder;
        /// <summary>在 <see cref="ResearchCatalog.Branches"/> 里的序号（树上从上到下的车道号）。</summary>
        public int Index;
        public readonly List<ResearchNodeDef> Nodes = new List<ResearchNodeDef>(16);

        public string Name => GameText.Get(NameKey);
    }

    /// <summary>一个研究节点（fg.TbResearchNode 一行的运行时视图；前置、关键材料、解锁内容已拆好）。</summary>
    public sealed class ResearchNodeDef
    {
        public string Id;
        public ResearchBranchDef Branch;
        public string NameKey;
        public string DescKey;
        public string Kind;
        public int Cost;
        public string[] Prereqs = Array.Empty<string>();
        public readonly List<BuildMaterialNeed> KeyItems = new List<BuildMaterialNeed>(1);
        /// <summary>解锁目标：build:&lt;建造菜单条目&gt; / tier:&lt;typeId&gt;.tN。</summary>
        public string[] Unlocks = Array.Empty<string>();
        public string EffectKind;
        public string EffectTarget;
        public float EffectValue;
        public int Col;
        public int Row;
        /// <summary>内容还没做时的承接 Story（只给开发看，界面不显示 ID）；内容已就绪 = null。</summary>
        public string OpensIn;
        /// <summary>表顺序（存档 / 自检稳定排序用）。</summary>
        public int Order;

        public bool IsReady => OpensIn == null;
        public string Name => GameText.Get(NameKey);
        public string Desc => GameText.Get(DescKey);
    }

    /// <summary>
    /// FG5-RND-01（FGR-RND-010 / 011 / 012）：研发树的内容目录——分支、节点、“哪个条目被哪个节点解锁”。
    /// 数据源 tools/cell_tables/fgdata_research.py（fg.TbResearchBranch / fg.TbResearchNode）；加一行表就多一个节点，不改代码。
    /// 载入时再校验一遍（分支存在、前置存在且在更左的列、关键材料是核心保管库物品、解锁目标存在且只被一个节点解锁），不合法的行拒收并记进 <see cref="Problems"/>。
    /// 只在表重载时建索引（O(节点数)），查询 O(1)，没有每帧开销。
    /// </summary>
    public static class ResearchCatalog
    {
        public const string LabTypeId = "sim_lab";

        public const string KindBuilding = "building";
        public const string KindUpgrade = "upgrade";
        public const string KindEfficiency = "efficiency";
        public const string KindCapacity = "capacity";
        public const string KindSlot = "slot";
        public const string KindBlueprint = "blueprint";

        public const string EffectSpeed = "speed";
        public const string EffectLab = "lab";
        public const string EffectCapacity = "capacity";

        private static bool _loaded;
        private static readonly List<ResearchBranchDef> BranchList = new List<ResearchBranchDef>(12);
        private static readonly List<ResearchNodeDef> NodeList = new List<ResearchNodeDef>(80);
        private static readonly Dictionary<string, ResearchNodeDef> ById = new Dictionary<string, ResearchNodeDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ResearchBranchDef> BranchById = new Dictionary<string, ResearchBranchDef>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ResearchNodeDef> GateByTarget = new Dictionary<string, ResearchNodeDef>(StringComparer.Ordinal);
        private static readonly List<string> _problems = new List<string>();

        /// <summary>表重载时 +1。</summary>
        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<ResearchBranchDef> Branches
        {
            get
            {
                EnsureLoaded();
                return BranchList;
            }
        }

        public static IReadOnlyList<ResearchNodeDef> Nodes
        {
            get
            {
                EnsureLoaded();
                return NodeList;
            }
        }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return _problems;
            }
        }

        public static void Reload()
        {
            _loaded = false;
            BranchList.Clear();
            NodeList.Clear();
            ById.Clear();
            BranchById.Clear();
            GateByTarget.Clear();
            _problems.Clear();
            Revision++;
        }

        public static void ResetForTests() => Reload();

        public static bool TryGet(string id, out ResearchNodeDef node)
        {
            EnsureLoaded();
            node = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out node);
        }

        public static ResearchNodeDef Find(string id) => TryGet(id, out ResearchNodeDef n) ? n : null;

        public static bool TryGetBranch(string id, out ResearchBranchDef branch)
        {
            EnsureLoaded();
            branch = null;
            return !string.IsNullOrEmpty(id) && BranchById.TryGetValue(id, out branch);
        }

        /// <summary>解锁这个目标（build:&lt;条目&gt; / tier:&lt;typeId&gt;.tN）的节点；没有 = null（开局即可）。</summary>
        public static ResearchNodeDef GateOf(string target)
        {
            EnsureLoaded();
            return target != null && GateByTarget.TryGetValue(target, out ResearchNodeDef n) ? n : null;
        }

        public static int CountReady()
        {
            int n = 0;
            foreach (ResearchNodeDef d in Nodes)
            {
                n += d.IsReady ? 1 : 0;
            }
            return n;
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "none")
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(4);
            foreach (string p in s.Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0 && t != "none")
                {
                    list.Add(t);
                }
            }
            return list.ToArray();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            TbResearchBranch branches = null;
            TbResearchNode nodes = null;
            try
            {
                branches = ConfigSystem.Instance.Tables?.TbResearchBranch;
                nodes = ConfigSystem.Instance.Tables?.TbResearchNode;
            }
            catch (Exception e)
            {
                _problems.Add("读取 fg.TbResearchBranch / fg.TbResearchNode 失败：" + e.Message);
            }
            if (branches == null || nodes == null)
            {
                if (_problems.Count == 0)
                {
                    _problems.Add("fg.TbResearchBranch / fg.TbResearchNode 不存在（改 tools/cell_tables/fgdata_research.py 后重新生成）");
                }
                Log.Error("[ResearchCatalog] " + _problems[0]);
                return;
            }
            var sorted = new List<ResearchBranch>(branches.DataList);
            sorted.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            foreach (ResearchBranch row in sorted)
            {
                var b = new ResearchBranchDef
                {
                    Id = row.Id,
                    NameKey = row.NameKey,
                    IsFaction = row.Kind == "faction",
                    Faction = row.Faction == "none" ? string.Empty : row.Faction,
                    Color = row.Color,
                    Glyph = row.Glyph,
                    SortOrder = row.SortOrder,
                    Index = BranchList.Count,
                };
                BranchList.Add(b);
                BranchById[b.Id] = b;
            }
            int order = 0;
            foreach (ResearchNode row in nodes.DataList)
            {
                if (!BranchById.TryGetValue(row.Branch, out ResearchBranchDef br))
                {
                    _problems.Add($"{row.Id}：分支 {row.Branch} 不存在");
                    continue;
                }
                if (row.Cost < 1)
                {
                    _problems.Add($"{row.Id}：研究点 {row.Cost} 至少 1");
                    continue;
                }
                var n = new ResearchNodeDef
                {
                    Id = row.Id,
                    Branch = br,
                    NameKey = row.NameKey,
                    DescKey = row.DescKey,
                    Kind = row.Kind,
                    Cost = row.Cost,
                    Prereqs = Split(row.Prereqs),
                    Unlocks = Split(row.Unlocks),
                    EffectKind = string.IsNullOrEmpty(row.EffectKind) ? "none" : row.EffectKind,
                    EffectTarget = row.EffectTarget == "none" ? string.Empty : row.EffectTarget,
                    EffectValue = row.EffectValue,
                    Col = row.Col,
                    Row = row.Row,
                    OpensIn = string.IsNullOrEmpty(row.OpensIn) || row.OpensIn == "none" ? null : row.OpensIn,
                    Order = order++,
                };
                foreach (string k in Split(row.KeyItems))
                {
                    int colon = k.IndexOf(':');
                    string itemId = colon > 0 ? k.Substring(0, colon) : k;
                    int amount = colon > 0 && int.TryParse(k.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) ? a : 1;
                    if (!ItemCatalog.TryGet(itemId, out ItemDef item) || item.Form != ItemForm.Vault || amount < 1)
                    {
                        _problems.Add($"{row.Id}：关键材料 {k} 不是核心保管库物品或件数非法");
                        continue;
                    }
                    n.KeyItems.Add(new BuildMaterialNeed(item, amount));
                }
                NodeList.Add(n);
                ById[n.Id] = n;
                br.Nodes.Add(n);
            }
            foreach (ResearchNodeDef n in NodeList)
            {
                foreach (string p in n.Prereqs)
                {
                    if (!ById.TryGetValue(p, out ResearchNodeDef pre))
                    {
                        _problems.Add($"{n.Id}：前置 {p} 不存在");
                    }
                    else if (pre.Col >= n.Col)
                    {
                        _problems.Add($"{n.Id}：前置 {p} 必须在更左的列（否则可能成环）");
                    }
                }
                foreach (string u in n.Unlocks)
                {
                    if (!TargetExists(u))
                    {
                        _problems.Add($"{n.Id}：解锁目标 {u} 不存在");
                        continue;
                    }
                    if (GateByTarget.TryGetValue(u, out ResearchNodeDef other))
                    {
                        _problems.Add($"{u} 同时被 {other.Id} 和 {n.Id} 解锁");
                        continue;
                    }
                    GateByTarget[u] = n;
                }
            }
            foreach (string p in _problems)
            {
                Log.Error("[ResearchCatalog] " + p);
            }
        }

        private static bool TargetExists(string u)
        {
            if (u.StartsWith("build:", StringComparison.Ordinal))
            {
                return BuildCatalog.TryGet(u.Substring(6), out _);
            }
            if (u.StartsWith("tier:", StringComparison.Ordinal))
            {
                return TryParseTier(u, out string typeId, out int tier) && BuildingOps.TierRow(typeId, tier) != null;
            }
            return false;
        }

        /// <summary>“tier:warehouse.t2” → (warehouse, 2)。</summary>
        public static bool TryParseTier(string target, out string typeId, out int tier)
        {
            typeId = null;
            tier = 0;
            if (target == null || !target.StartsWith("tier:", StringComparison.Ordinal))
            {
                return false;
            }
            string s = target.Substring(5);
            int dot = s.LastIndexOf(".t", StringComparison.Ordinal);
            if (dot <= 0 || !int.TryParse(s.Substring(dot + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out tier))
            {
                return false;
            }
            typeId = s.Substring(0, dot);
            return true;
        }
    }
}
