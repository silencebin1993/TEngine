using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;

namespace GameLogic.Campaign.Grid
{
    /// <summary>建造菜单里的一项：一种可放置建筑，或一个工具（传送带）。</summary>
    public sealed class BuildEntry
    {
        public string Id;
        public bool IsTool;
        public string CategoryId;
        public string DescKey;
        public string UnlockRule;
        public string UnlockHintKey;
        public BuildingGrid Building;
        public BuildTool Tool;

        /// <summary>当前语言的名字。</summary>
        public string Name => IsTool ? GameText.Get(Tool.NameKey) : HomeGridService.DisplayName(Id);
    }

    /// <summary>
    /// FG3-LOG-01（FGR-LOG-002；FG13 FGU-07）：建造菜单的目录——分类、搜索、解锁判定与快捷栏。
    /// 条目来自 fg.TbBuildingGrid（placeable=1 的建筑）与 fg.TbBuildTool（传送带等工具），分类来自 fg.TbBuildCategory：
    /// 加一行表就出现在菜单里，不改代码（数据驱动）。同分类里工具在前（按 sortOrder），建筑在后（按表顺序）。
    /// 搜索按当前语言的名字与用途（说明文本）做不区分大小写的包含匹配，跨全部分类。
    /// 快捷栏 10 格记在存档（<see cref="GridState.Hotbar"/>），格子里只放条目 ID；条目后来从内容里移除时按空格子处理。
    /// 目录只在表重载时重建（O(条目数)），查询 O(条目数)，都不在每帧发生。
    /// </summary>
    public static class BuildCatalog
    {
        public const int HotbarSlots = 10;

        private static readonly List<BuildEntry> Entries = new List<BuildEntry>(32);
        private static readonly Dictionary<string, BuildEntry> ById = new Dictionary<string, BuildEntry>(StringComparer.Ordinal);
        private static int _revision = -1;

        public static IReadOnlyList<BuildEntry> All
        {
            get
            {
                Ensure();
                return Entries;
            }
        }

        public static bool TryGet(string id, out BuildEntry entry)
        {
            Ensure();
            entry = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out entry);
        }

        private static void Ensure()
        {
            if (_revision == GridContent.Revision)
            {
                return;
            }
            Entries.Clear();
            ById.Clear();
            foreach (BuildCategory cat in GridContent.Categories)
            {
                foreach (BuildTool t in GridContent.Tools)
                {
                    if (t.Category == cat.Id)
                    {
                        Add(new BuildEntry
                        {
                            Id = t.Id, IsTool = true, CategoryId = t.Category, DescKey = t.DescKey,
                            UnlockRule = t.UnlockRule, UnlockHintKey = t.UnlockHintKey, Tool = t,
                        });
                    }
                }
                foreach (BuildingGrid g in GridContent.Buildings)
                {
                    if (g.Placeable == 1 && g.Category == cat.Id)
                    {
                        Add(new BuildEntry
                        {
                            Id = g.TypeId, IsTool = false, CategoryId = g.Category, DescKey = g.DescKey,
                            UnlockRule = g.UnlockRule, UnlockHintKey = g.UnlockHintKey, Building = g,
                        });
                    }
                }
            }
            _revision = GridContent.Revision;
        }

        private static void Add(BuildEntry e)
        {
            if (ById.ContainsKey(e.Id))
            {
                return; // 建筑与工具 ID 撞名（表规则不允许）：只收第一个，不让菜单出现两个同名条目。
            }
            Entries.Add(e);
            ById[e.Id] = e;
        }

        // ── 解锁 ────────────────────────────────────────────────────────────────

        /// <summary>解锁规则：always = 开局即可；beacon = 摧毁铸造前哨主核心并带回核心数据后（<see cref="HomeValleyBeacon.IsUnlocked"/>）。</summary>
        public static bool IsUnlocked(CampaignState state, string rule)
        {
            switch (rule)
            {
                case "always": return true;
                case "beacon": return state != null && HomeValleyBeacon.IsUnlocked(state);
                default:
                    // FG4-ECO-11：research:<节点> = 研究完成后解锁（研发树开放前视为已满足，见 ResearchGate）。
                    return Economy.ResearchGate.IsResearchRule(rule) && Economy.ResearchGate.IsCompleted(state, Economy.ResearchGate.NodeOf(rule));
            }
        }

        public static bool IsUnlocked(CampaignState state, BuildEntry e) => e != null && IsUnlocked(state, e.UnlockRule);

        // ── 分类与搜索 ──────────────────────────────────────────────────────────

        public static int CountInCategory(string categoryId)
        {
            Ensure();
            int n = 0;
            foreach (BuildEntry e in Entries)
            {
                if (e.CategoryId == categoryId)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>第一个有条目的分类（打开菜单时的默认页签）；全空时返回第一个分类。</summary>
        public static string FirstNonEmptyCategory()
        {
            Ensure();
            IReadOnlyList<BuildCategory> cats = GridContent.Categories;
            foreach (BuildCategory c in cats)
            {
                if (CountInCategory(c.Id) > 0)
                {
                    return c.Id;
                }
            }
            return cats.Count > 0 ? cats[0].Id : null;
        }

        /// <summary>列出条目：<paramref name="search"/> 非空时跨全部分类按名字 / 用途搜索，否则列出 <paramref name="categoryId"/> 分类。</summary>
        public static void List(string categoryId, string search, List<BuildEntry> into)
        {
            Ensure();
            into.Clear();
            string q = search?.Trim();
            foreach (BuildEntry e in Entries)
            {
                if (!string.IsNullOrEmpty(q) ? Matches(e, q) : e.CategoryId == categoryId)
                {
                    into.Add(e);
                }
            }
        }

        /// <summary>搜索：名字或用途（说明文本，当前语言）包含关键字即命中，不区分大小写。</summary>
        public static bool Matches(BuildEntry e, string query)
        {
            if (e == null || string.IsNullOrEmpty(query))
            {
                return false;
            }
            string name = e.Name ?? string.Empty;
            string desc = GameText.Get(e.DescKey) ?? string.Empty;
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── 快捷栏 ──────────────────────────────────────────────────────────────

        /// <summary>第 <paramref name="slot"/>（0～9）格里的条目；空格子或条目已失效返回 null。</summary>
        public static BuildEntry HotbarEntry(CampaignState state, int slot)
        {
            string id = HotbarId(state, slot);
            return id != null && TryGet(id, out BuildEntry e) ? e : null;
        }

        public static string HotbarId(CampaignState state, int slot)
        {
            string[] bar = state?.Grid?.Hotbar;
            return bar != null && slot >= 0 && slot < bar.Length && !string.IsNullOrEmpty(bar[slot]) ? bar[slot] : null;
        }

        /// <summary>把条目放进第 <paramref name="slot"/> 格（覆盖原来的）；<paramref name="entryId"/> 为空 = 清空这一格。
        /// 条目必须在建造菜单里（未解锁的也可以放：按下时给解锁条件）。</summary>
        public static bool TrySetHotbar(CampaignState state, int slot, string entryId)
        {
            if (state == null || slot < 0 || slot >= HotbarSlots)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(entryId) && !TryGet(entryId, out _))
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(state);
            string[] bar = state.Grid.Hotbar;
            if (bar == null || bar.Length != HotbarSlots)
            {
                var fresh = new string[HotbarSlots];
                for (int i = 0; i < HotbarSlots; i++)
                {
                    fresh[i] = bar != null && i < bar.Length ? bar[i] ?? string.Empty : string.Empty;
                }
                bar = fresh;
            }
            else
            {
                bar = (string[])bar.Clone();
            }
            bar[slot] = entryId ?? string.Empty;
            state.Grid.Hotbar = bar;
            Revision++;
            if (!string.IsNullOrEmpty(entryId))
            {
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstHotbar);
            }
            return true;
        }

        /// <summary>快捷栏内容变化时 +1（HUD 据此刷新）。</summary>
        public static int Revision { get; private set; }
    }
}
