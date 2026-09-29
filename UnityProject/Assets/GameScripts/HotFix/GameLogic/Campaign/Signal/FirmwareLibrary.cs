using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Progression;

namespace GameLogic.Campaign.Signal
{
    /// <summary>固件库排序方式（FGR-FW-061“排序”）。</summary>
    public enum FirmwareLibrarySort : byte
    {
        Name = 0,
        Category = 1,
        Rarity = 2,
        Load = 3,
        Newest = 4,
        Location = 5,
    }

    /// <summary>芯片所在位置（固件库“所在仓库”列）。</summary>
    public enum FirmwareLocationKind : byte
    {
        /// <summary>家园仓储（基元仓）。</summary>
        Storage = 0,
        /// <summary>仓储被合成台预留中。</summary>
        Reserved = 1,
        /// <summary>待领取（仓储满时入账的）。</summary>
        Pending = 2,
        /// <summary>装在信号核里。</summary>
        SignalCore = 3,
        /// <summary>装在某个蓝图的电路草稿里（固件芯片目前不进 3×3 电路板，保留给基元芯片与后续）。</summary>
        Draft = 4,
    }

    /// <summary>固件库筛选条件（FGR-FW-061：类别、种类、协议、是否已破解、与某载体是否兼容、稀有度；再加搜索与排序）。null = 不限。</summary>
    public sealed class FirmwareLibraryFilter
    {
        public FirmwareCategory? Category;
        public FirmwareKind? Kind;
        /// <summary>true = 只看敌方加密协议；false = 只看己方 / 中立。</summary>
        public bool? EnemyProtocol;
        /// <summary>true = 只看已破解（含本来就不加密的）；false = 只看未破解。</summary>
        public bool? Cracked;
        public FirmwareCarrier? Carrier;
        /// <summary>稀有度键 common / rare / epic。</summary>
        public string Rarity;
        public string Search;
        public FirmwareLibrarySort Sort = FirmwareLibrarySort.Name;

        public void Reset()
        {
            Category = null;
            Kind = null;
            EnemyProtocol = null;
            Cracked = null;
            Carrier = null;
            Rarity = null;
            Search = null;
            Sort = FirmwareLibrarySort.Name;
        }
    }

    /// <summary>固件库列表的一行（一枚芯片）。</summary>
    public sealed class FirmwareLibraryRow
    {
        public PrimitiveChipRecord Chip;
        public string FirmwareId;
        public string Name;
        public FirmwareCategory Category;
        public FirmwareKind Kind;
        public bool Raw;
        public string Rarity;
        public int Load;
        public FirmwareLocationKind Location;
        /// <summary>信号核槽位（0 起），不在信号核时 -1。</summary>
        public int Slot = -1;
    }

    /// <summary>批量分解的计划（确认框按它写后果；执行时重新计算，不信任计划时刻之后可能变化的状态）。</summary>
    public sealed class FirmwareDisassemblePlan
    {
        public readonly List<string> Eligible = new List<string>();
        public int SkippedLocked;
        /// <summary>装在信号核 / 蓝图里、或被合成台预留的。</summary>
        public int SkippedBusy;
        /// <summary>分解后目前没法再刻印的（内容未解锁或未破解）。</summary>
        public int Unprintable;
        public int Scrap;
        public int Skipped => SkippedLocked + SkippedBusy;
    }

    /// <summary>“仓储 → 装配站”取用路线的检查结果（FG02 第 5 章“搬运路线被堵：说明被什么堵住”）。</summary>
    public sealed class FirmwareRouteInfo
    {
        public NavService.BuildingReach Reach;
        public bool NoStation;
        /// <summary>取用出发的存放建筑（运转中的仓库；没有时是归还核心应急库）显示名。</summary>
        public string StorageName;
        public readonly List<string> StorageBlockers = new List<string>();
        public readonly List<string> StationBlockers = new List<string>();
        public bool Ok => !NoStation && Reach == NavService.BuildingReach.Connected;
    }

    /// <summary>
    /// FG2-FW-05（FG02 FGR-FW-060、061；FG13 FGU-20）：固件库——所有存放位置里固件芯片的统一视图。
    /// - 只读查询（筛选 / 排序 / 搜索 / 详情 / 比较 / 持有数量与位置）只在界面打开并按间隔刷新、或玩家操作后调用，O(芯片数)，不按帧；
    /// - 写操作只有两个：锁定（<see cref="PrimitiveInventory.TrySetLocked"/>）与批量分解（<see cref="Disassemble"/>，确认后才执行，锁定的跳过）；
    /// - 图鉴同步（<see cref="SyncCodex"/>）：拿到过 / 已解锁的固件、本存档打出过的反应，解锁对应的机制图鉴条目（跨存档，FGR-UX-051）。
    /// “固件芯片是物品”：芯片实例就是 <see cref="PrimitiveChipRecord"/>，存放容量见 <see cref="PrimitiveInventory.CapacityOf"/>。
    /// </summary>
    public static class FirmwareLibrary
    {
        private static int _revision = 1;

        /// <summary>界面刷新键：芯片实例 / 破解状态 / 目录变化时变化。</summary>
        public static int Revision => HashCode.Combine(_revision, PrimitiveInventory.Revision, FirmwareKinds.Revision, SignalCoreService.Revision);

        public static int DisassembleScrap => Math.Max(0, TuningInt("firmware.library.disassemble_scrap", 5));

        public static bool IsFirmwareChip(PrimitiveChipRecord p) => p != null && FirmwareKinds.IsFirmware(p.CardDefId);

        // ── 查询 ────────────────────────────────────────────────────────────────

        /// <summary>按筛选条件列出芯片（结果写进 <paramref name="into"/>，已按 <see cref="FirmwareLibraryFilter.Sort"/> 排好）。返回固件芯片总数（不计筛选）。</summary>
        public static int Query(CampaignState s, FirmwareLibraryFilter filter, List<FirmwareLibraryRow> into)
        {
            into.Clear();
            int total = 0;
            PrimitiveChipRecord[] chips = s?.PrimitiveChips;
            if (chips == null)
            {
                return 0;
            }
            string search = Normalize(filter?.Search);
            // 同一次查询里按固件种类缓存“是否匹配”与行的静态字段（种类最多几十条，芯片可能上千枚）：每枚芯片只剩位置要单独算。
            QueryCache.Clear();
            foreach (PrimitiveChipRecord p in chips)
            {
                if (p == null || string.IsNullOrEmpty(p.CardDefId))
                {
                    continue;
                }
                if (!QueryCache.TryGetValue(p.CardDefId, out FirmwareLibraryRow template))
                {
                    template = !FirmwareKinds.IsFirmware(p.CardDefId) ? NotFirmware
                        : filter != null && !Matches(s, p.CardDefId, filter, search) ? Filtered
                        : BuildRow(s, p);
                    QueryCache[p.CardDefId] = template;
                }
                if (ReferenceEquals(template, NotFirmware))
                {
                    continue;
                }
                total++;
                if (ReferenceEquals(template, Filtered))
                {
                    continue;
                }
                FirmwareLocationKind loc = LocationOf(s, p, out int slot);
                into.Add(new FirmwareLibraryRow
                {
                    Chip = p,
                    FirmwareId = template.FirmwareId,
                    Name = template.Name,
                    Category = template.Category,
                    Kind = template.Kind,
                    Raw = template.Raw,
                    Rarity = template.Rarity,
                    Load = template.Load,
                    Location = loc,
                    Slot = slot,
                });
            }
            into.Sort((a, b) => Compare(a, b, filter?.Sort ?? FirmwareLibrarySort.Name));
            return total;
        }

        private static readonly Dictionary<string, FirmwareLibraryRow> QueryCache = new Dictionary<string, FirmwareLibraryRow>(StringComparer.Ordinal);
        private static readonly FirmwareLibraryRow NotFirmware = new FirmwareLibraryRow();
        private static readonly FirmwareLibraryRow Filtered = new FirmwareLibraryRow();

        public static FirmwareLibraryRow BuildRow(CampaignState s, PrimitiveChipRecord p)
        {
            string id = p.CardDefId;
            FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
            FirmwareLocationKind loc = LocationOf(s, p, out int slot);
            return new FirmwareLibraryRow
            {
                Chip = p,
                FirmwareId = id,
                Name = FirmwareKinds.DisplayName(id) ?? id,
                Category = FirmwareKinds.CategoryOf(id),
                Kind = FirmwareKinds.KindOf(id),
                Raw = FirmwareKinds.IsRaw(s, id),
                Rarity = FirmwareKinds.RarityOf(id) ?? string.Empty,
                Load = row != null ? Math.Max(1, row.Load) : 1,
                Location = loc,
                Slot = slot,
            };
        }

        /// <summary>一条固件是否满足筛选（搜索词已归一化；空 = 不限）。</summary>
        public static bool Matches(CampaignState s, string firmwareId, FirmwareLibraryFilter f, string normalizedSearch)
        {
            if (f.Category.HasValue && FirmwareKinds.CategoryOf(firmwareId) != f.Category.Value)
            {
                return false;
            }
            if (f.Kind.HasValue && FirmwareKinds.KindOf(firmwareId) != f.Kind.Value)
            {
                return false;
            }
            if (f.EnemyProtocol.HasValue && FirmwareKinds.IsEnemyProtocol(firmwareId) != f.EnemyProtocol.Value)
            {
                return false;
            }
            if (f.Cracked.HasValue && FirmwareKinds.IsRaw(s, firmwareId) == f.Cracked.Value)
            {
                return false;
            }
            if (f.Carrier.HasValue && !IsCompatible(firmwareId, f.Carrier.Value))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(f.Rarity) && !string.Equals(FirmwareKinds.RarityOf(firmwareId), f.Rarity, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(normalizedSearch))
            {
                string name = Normalize(FirmwareKinds.DisplayName(firmwareId));
                string desc = FirmwareCatalog.TryGet(firmwareId, out MechanicalContentDef def) ? Normalize(def.Description) : string.Empty;
                if (name.IndexOf(normalizedSearch, StringComparison.Ordinal) < 0 && desc.IndexOf(normalizedSearch, StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>“与某载体兼容”：能装进机器电路的常规固件，且这个载体上有读法（FGR-FW-010 零死对：常规固件对 5 种载体都有读法）。
        /// 核心固件只属于信号，对任何载体都不兼容。</summary>
        public static bool IsCompatible(string firmwareId, FirmwareCarrier carrier) =>
            FirmwareKinds.KindOf(firmwareId) == FirmwareKind.Regular && !string.IsNullOrEmpty(FirmwareKinds.ReadingKey(firmwareId, carrier))
            && !string.IsNullOrEmpty(FirmwareKinds.Reading(firmwareId, carrier));

        public static string Normalize(string text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().ToLowerInvariant();

        private static int RarityRank(string rarity) => rarity switch
        {
            "epic" => 3,
            "rare" => 2,
            "common" => 1,
            _ => 0,
        };

        private static int Compare(FirmwareLibraryRow a, FirmwareLibraryRow b, FirmwareLibrarySort sort)
        {
            int c = 0;
            switch (sort)
            {
                case FirmwareLibrarySort.Category:
                    c = ((int)a.Category).CompareTo((int)b.Category);
                    break;
                case FirmwareLibrarySort.Rarity:
                    c = RarityRank(b.Rarity).CompareTo(RarityRank(a.Rarity));
                    break;
                case FirmwareLibrarySort.Load:
                    c = b.Load.CompareTo(a.Load);
                    break;
                case FirmwareLibrarySort.Newest:
                    c = b.Chip.AcquiredTick.CompareTo(a.Chip.AcquiredTick);
                    break;
                case FirmwareLibrarySort.Location:
                    c = ((int)a.Location).CompareTo((int)b.Location);
                    if (c == 0)
                    {
                        c = a.Slot.CompareTo(b.Slot);
                    }
                    break;
            }
            if (c == 0)
            {
                c = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            }
            if (c == 0)
            {
                c = string.CompareOrdinal(a.Chip.PartId, b.Chip.PartId); // 稳定：同名同位置按实例 ID
            }
            return c;
        }

        // ── 位置 ────────────────────────────────────────────────────────────────

        public static FirmwareLocationKind LocationOf(CampaignState s, PrimitiveChipRecord p, out int slot)
        {
            slot = -1;
            switch (p.State)
            {
                case PrimitiveChipState.Pending:
                    return FirmwareLocationKind.Pending;
                case PrimitiveChipState.SignalCore:
                    string[] slots = s?.SignalCore?.SlotPartIds;
                    slot = slots == null ? -1 : Array.IndexOf(slots, p.PartId);
                    return FirmwareLocationKind.SignalCore;
                case PrimitiveChipState.Draft:
                    slot = p.DraftSlot;
                    return FirmwareLocationKind.Draft;
                default:
                    return string.IsNullOrEmpty(p.ReservedByTransactionId) ? FirmwareLocationKind.Storage : FirmwareLocationKind.Reserved;
            }
        }

        public static string LocationText(CampaignState s, FirmwareLibraryRow row)
        {
            switch (row.Location)
            {
                case FirmwareLocationKind.Pending: return GameText.Get("fwlib.loc.pending");
                case FirmwareLocationKind.Reserved: return GameText.Get("fwlib.loc.reserved");
                case FirmwareLocationKind.SignalCore: return GameText.Format("fwlib.loc.signal", row.Slot + 1);
                case FirmwareLocationKind.Draft: return GameText.Format("fwlib.loc.draft", BlueprintName(s, row.Chip.DraftBlueprintId));
                default: return GameText.Get("fwlib.loc.storage");
            }
        }

        private static string BlueprintName(CampaignState s, string blueprintId)
        {
            foreach (BlueprintRecord b in s?.BlueprintRecords ?? Array.Empty<BlueprintRecord>())
            {
                if (b != null && b.BlueprintId == blueprintId)
                {
                    return string.IsNullOrEmpty(b.DisplayName) ? blueprintId : b.DisplayName;
                }
            }
            return blueprintId ?? string.Empty;
        }

        /// <summary>持有这条固件的芯片数，以及按位置汇总的说明（“仓储 ×2、信号核 第 1 槽”）。</summary>
        public static int CountHeld(CampaignState s, string firmwareId, out string summary)
        {
            var parts = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (PrimitiveChipRecord p in s?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (p == null || p.CardDefId != firmwareId)
                {
                    continue;
                }
                n++;
                string loc = LocationText(s, BuildRow(s, p));
                if (!counts.ContainsKey(loc))
                {
                    parts.Add(loc);
                    counts[loc] = 0;
                }
                counts[loc]++;
            }
            var sb = new StringBuilder();
            foreach (string loc in parts)
            {
                if (sb.Length > 0)
                {
                    sb.Append(GameText.Language == GameLanguage.ZhCn ? "、" : ", ");
                }
                sb.Append(loc);
                if (counts[loc] > 1)
                {
                    sb.Append(" ×").Append(counts[loc].ToString(CultureInfo.InvariantCulture));
                }
            }
            summary = sb.ToString();
            return n;
        }

        // ── 锁定与批量分解 ─────────────────────────────────────────────────────

        public static bool TrySetLocked(CampaignState s, string partId, bool locked)
        {
            PrimitiveChipRecord p = PrimitiveInventory.Find(s, partId);
            if (!IsFirmwareChip(p))
            {
                return false;
            }
            bool ok = PrimitiveInventory.TrySetLocked(s, partId, locked);
            if (ok)
            {
                _revision++;
            }
            return ok;
        }

        /// <summary>批量分解计划：锁定的跳过、在用的（信号核 / 蓝图 / 合成台预留）跳过、不是固件芯片的忽略。</summary>
        public static FirmwareDisassemblePlan PlanDisassemble(CampaignState s, IEnumerable<string> partIds)
        {
            var plan = new FirmwareDisassemblePlan();
            if (s == null || partIds == null)
            {
                return plan;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // 一次建索引：O(芯片数 + 所选数)，不逐个线性查找（1000 枚全选时避免平方级）。
            var byId = new Dictionary<string, PrimitiveChipRecord>(StringComparer.Ordinal);
            foreach (PrimitiveChipRecord c in s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && !string.IsNullOrEmpty(c.PartId))
                {
                    byId[c.PartId] = c;
                }
            }
            var reprint = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string id in partIds)
            {
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    continue;
                }
                byId.TryGetValue(id, out PrimitiveChipRecord p);
                if (!IsFirmwareChip(p))
                {
                    continue;
                }
                if (p.Locked)
                {
                    plan.SkippedLocked++;
                    continue;
                }
                if (!PrimitiveInventory.IsDisassemblable(p))
                {
                    plan.SkippedBusy++;
                    continue;
                }
                plan.Eligible.Add(id);
                if (!reprint.TryGetValue(p.CardDefId, out bool can))
                {
                    can = CanReprint(s, p.CardDefId);
                    reprint[p.CardDefId] = can;
                }
                if (!can)
                {
                    plan.Unprintable++;
                }
            }
            plan.Scrap = plan.Eligible.Count * DisassembleScrap;
            return plan;
        }

        /// <summary>分解后还能不能再刻印（已解锁且已破解、不是核心固件以外的限制都按刻印规则：内容解锁 + 不是未破解）。</summary>
        public static bool CanReprint(CampaignState s, string firmwareId) =>
            MechanicalContentUnlock.IsUnlocked(s, firmwareId) && !FirmwareKinds.IsRaw(s, firmwareId);

        /// <summary>
        /// 执行批量分解（玩家在确认框里点了“分解”之后才调用）：按此刻的状态重新计划——确认框打开期间被锁定 / 装进信号核的照样跳过；
        /// 移除实例并按件数返还废料（经资源账本，生产型事务）。返回实际执行的计划。
        /// </summary>
        public static FirmwareDisassemblePlan Disassemble(CampaignState s, IEnumerable<string> partIds)
        {
            FirmwareDisassemblePlan plan = PlanDisassemble(s, partIds);
            if (plan.Eligible.Count == 0)
            {
                return plan;
            }
            List<PrimitiveChipRecord> removed = PrimitiveInventory.RemoveForDisassembly(s, plan.Eligible);
            int scrap = removed.Count * DisassembleScrap;
            if (removed.Count != plan.Eligible.Count)
            {
                // 第二道防线拦下了（理论上不会发生）：按实际移除的件数结算。
                plan.SkippedBusy += plan.Eligible.Count - removed.Count;
                plan.Eligible.Clear();
                foreach (PrimitiveChipRecord p in removed)
                {
                    plan.Eligible.Add(p.PartId);
                }
            }
            plan.Scrap = scrap;
            if (scrap > 0)
            {
                string tx = "firmware-disassemble:" + Guid.NewGuid().ToString("N");
                CampaignEconomyLedger.ProposeProduce(s, tx, "FirmwareLibrary", CampaignEconomyLedger.ResourceScrap, scrap);
                CampaignEconomyLedger.Reserve(s, tx);
                CampaignEconomyLedger.Commit(s, tx);
            }
            _revision++;
            return plan;
        }

        // ── 详情（固件库详情页与图鉴固件条目共用，FGR-FW-061“详情页”，DEBT-FG2FW02-05）────────────────

        /// <summary>这条固件参与的具名反应（标签反应：它产生的状态标签是配料之一；装配反应：它是触发固件）。按表顺序。</summary>
        public static List<string> ReactionsOf(string firmwareId)
        {
            var list = new List<string>();
            if (!FirmwareKinds.IsFirmware(firmwareId))
            {
                return list;
            }
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (string t in FirmwareKinds.TagsOf(firmwareId))
            {
                tags.Add(StatusTagCatalog.Canonical(t));
            }
            foreach (GameConfig.fg.Reaction r in NamedReactionCatalog.Rows)
            {
                if (r == null)
                {
                    continue;
                }
                bool hit = r.Kind == NamedReactionCatalog.KindAssembly
                    ? MechanicalReactionCatalog.TriggerFirmwareOf(r.Id) == firmwareId
                    : tags.Contains(StatusTagCatalog.Canonical(r.TagA)) || tags.Contains(StatusTagCatalog.Canonical(r.TagB));
                if (hit)
                {
                    list.Add(r.Id);
                }
            }
            return list;
        }

        /// <summary>能提供某条反应配料的固件（反应图鉴条目链接回固件，FGR-UX-051 互链）。</summary>
        public static List<string> FirmwareForReaction(string reactionId)
        {
            var list = new List<string>();
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                if (row != null && ReactionsOf(row.Id).Contains(reactionId))
                {
                    list.Add(row.Id);
                }
            }
            return list;
        }

        /// <summary>固件详情正文（当前语言）。<paramref name="s"/> 为 null 时不写持有数量 / 未破解状态；<paramref name="chip"/> 非空时补“这一枚的来源”。</summary>
        public static string BuildDetail(CampaignState s, string firmwareId, PrimitiveChipRecord chip = null)
        {
            if (!FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row))
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            FirmwareCatalog.TryGet(firmwareId, out MechanicalContentDef def);
            if (def != null && !string.IsNullOrEmpty(def.Description))
            {
                sb.AppendLine(def.Description);
            }
            sb.AppendLine(GameText.Format("fwlib.detail.kind", FirmwareKinds.KindLabel(FirmwareKinds.KindOf(firmwareId))));
            sb.AppendLine(GameText.Format("fwlib.detail.category", GameText.Get("firmware.category." + row.Category)));
            string protocol = GameText.Get("firmware.protocol." + row.Protocol);
            if (s != null && FirmwareKinds.IsRaw(s, firmwareId))
            {
                protocol += "  " + GameText.Get("signal.core.raw_tag");
            }
            sb.AppendLine(GameText.Format("fwlib.detail.protocol", protocol));
            sb.AppendLine(GameText.Format("fwlib.detail.rarity", GameText.Get("firmware.rarity." + row.Rarity)));
            sb.AppendLine(GameText.Format("fwlib.detail.numbers", Math.Max(1, row.Load).ToString(CultureInfo.InvariantCulture),
                FirmwareKinds.PowerOf(firmwareId).ToString(CultureInfo.InvariantCulture), FirmwareKinds.HeatOf(firmwareId).ToString("0.#", CultureInfo.InvariantCulture)));
            string readings = CarrierReadings.DetailLines(firmwareId);
            if (!string.IsNullOrEmpty(readings))
            {
                sb.AppendLine(GameText.Get("fwlib.readings_title"));
                sb.AppendLine(readings);
            }
            List<string> tagNames = StatusTagCatalog.DisplayNames(FirmwareKinds.TagsOf(firmwareId));
            sb.AppendLine(tagNames.Count == 0 ? GameText.Get("fwlib.detail.tags_none") : GameText.Format("fwlib.detail.tags", string.Join(Sep, tagNames)));
            List<string> reactions = ReactionsOf(firmwareId);
            if (reactions.Count == 0)
            {
                sb.AppendLine(GameText.Get("fwlib.detail.reactions_none"));
            }
            else
            {
                var names = new List<string>(reactions.Count);
                foreach (string r in reactions)
                {
                    names.Add(MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(r)) ? NamedReactionCatalog.NameOf(r) : GameText.Get("fwlib.detail.reaction_unknown"));
                }
                sb.AppendLine(GameText.Format("fwlib.detail.reactions", string.Join(Sep, names)));
            }
            sb.AppendLine(GameText.Format("fwlib.detail.acquire", FirmwareKinds.AcquireText(firmwareId)));
            if (s != null)
            {
                int held = CountHeld(s, firmwareId, out string where);
                sb.AppendLine(held == 0 ? GameText.Get("fwlib.detail.held_none") : GameText.Format("fwlib.detail.held", held, where));
            }
            if (chip != null)
            {
                sb.AppendLine(GameText.Format("fwlib.detail.origin", OriginText(chip.Origin)));
            }
            return sb.ToString().TrimEnd();
        }

        public static string Sep => GameText.Language == GameLanguage.ZhCn ? "、" : ", ";

        public static string OriginText(string origin) => GameText.Get(origin switch
        {
            PrimitiveInventory.OriginSeed => "fwlib.origin.seed",
            PrimitiveInventory.OriginPrint => "fwlib.origin.print",
            PrimitiveInventory.OriginSalvage => "fwlib.origin.salvage",
            PrimitiveInventory.OriginEncrypted => "fwlib.origin.encrypted",
            PrimitiveInventory.OriginCraft => "fwlib.origin.craft",
            _ => "fwlib.origin.unknown",
        });

        /// <summary>并排比较的一行：（字段名，A 的值，B 的值）。只列两条固件的数据，不需要持有。</summary>
        public static List<(string Label, string A, string B)> CompareFields(string a, string b)
        {
            var rows = new List<(string, string, string)>();
            if (!FirmwareKinds.TryGetRow(a, out GameConfig.fg.FirmwareKind ra) || !FirmwareKinds.TryGetRow(b, out GameConfig.fg.FirmwareKind rb))
            {
                return rows;
            }
            rows.Add((GameText.Get("fwlib.sort.name"), FirmwareKinds.DisplayName(a), FirmwareKinds.DisplayName(b)));
            rows.Add((GameText.Get("fwlib.filter.kind"), FirmwareKinds.KindLabel(FirmwareKinds.KindOf(a)), FirmwareKinds.KindLabel(FirmwareKinds.KindOf(b))));
            rows.Add((GameText.Get("fwlib.filter.category"), GameText.Get("firmware.category." + ra.Category), GameText.Get("firmware.category." + rb.Category)));
            rows.Add((GameText.Get("fwlib.filter.protocol"), GameText.Get("firmware.protocol." + ra.Protocol), GameText.Get("firmware.protocol." + rb.Protocol)));
            rows.Add((GameText.Get("fwlib.filter.rarity"), GameText.Get("firmware.rarity." + ra.Rarity), GameText.Get("firmware.rarity." + rb.Rarity)));
            rows.Add((GameText.Get("fwlib.sort.load"), Math.Max(1, ra.Load).ToString(CultureInfo.InvariantCulture), Math.Max(1, rb.Load).ToString(CultureInfo.InvariantCulture)));
            foreach (FirmwareCarrier c in CarrierReadings.AllCarriers)
            {
                rows.Add((CarrierReadings.CarrierName(c), FirmwareKinds.Reading(a, c) ?? string.Empty, FirmwareKinds.Reading(b, c) ?? string.Empty));
            }
            rows.Add((GameText.Get("fwlib.compare.tags"),
                string.Join(Sep, StatusTagCatalog.DisplayNames(FirmwareKinds.TagsOf(a))), string.Join(Sep, StatusTagCatalog.DisplayNames(FirmwareKinds.TagsOf(b)))));
            return rows;
        }

        // ── 取用路线（仓储 → 装配站）─────────────────────────────────────────────

        private static FirmwareRouteInfo _routeCache;
        private static CampaignState _routeState;
        private static double _routeAt = double.NegativeInfinity;
        private static long _routeGridKey;

        /// <summary>真实时间来源（缓存用；自检可注入）。</summary>
        public static Func<double> Clock = () => UnityEngine.Time.realtimeSinceStartupAsDouble;

        /// <summary>
        /// 芯片从家园仓储取到装配站的路线（FG02 第 5 章负向“固件在仓库里，但搬运路线被堵”：说明被什么堵住）。
        /// 出发点：离装配站最近的运转中的仓库；没有运转中的仓库时是归还核心（应急库）。结果按 firmware.library.route_cache_seconds 缓存（格网改动立即失效）。
        /// 装配工作单按它进入“等待物料”由 FG4-ECO-03 接入（DEBT-FG2FW05-01）。
        /// </summary>
        public static FirmwareRouteInfo EvaluateRoute(CampaignState s, bool forceFresh = false)
        {
            long gridKey = HomeGridService.OccupancyRebuildCount + ((long)(s?.BuildingRecords?.Length ?? 0) << 32);
            double now = Clock();
            if (!forceFresh && _routeCache != null && ReferenceEquals(_routeState, s) && gridKey == _routeGridKey
                && now - _routeAt < Math.Max(0.05, TuningFloat("firmware.library.route_cache_seconds", 1f)))
            {
                return _routeCache;
            }
            var info = new FirmwareRouteInfo();
            BuildingRecord station = null;
            BuildingRecord core = null;
            BuildingRecord best = null;
            double bestD = double.MaxValue;
            foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation)
                {
                    // FG3-LOG-01：搬迁目标虚影还没建成，路线按现在运转的那座装配站算；多座时取第一座（与其它按类型查找一致）。
                    if (string.IsNullOrEmpty(b.RelocateFromId) && station == null)
                    {
                        station = b;
                    }
                }
                else if (b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    core = b;
                }
            }
            if (station == null)
            {
                info.NoStation = true;
                return Remember(s, info, now, gridKey);
            }
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse
                    && b.ConstructionState == BuildingConstructionState.Operational)
                {
                    double dx = b.GridX - station.GridX, dy = b.GridY - station.GridY;
                    double d = dx * dx + dy * dy;
                    if (d < bestD || (Math.Abs(d - bestD) < 1e-9 && string.CompareOrdinal(b.BuildingId, best?.BuildingId) < 0))
                    {
                        bestD = d;
                        best = b;
                    }
                }
            }
            BuildingRecord storage = best ?? core;
            info.StorageName = best != null ? HomeGridService.DisplayName(best.BuildingTypeId) : GameText.Get("fwlib.storage.core");
            if (storage == null)
            {
                info.Reach = NavService.BuildingReach.Unknown;
                return Remember(s, info, now, gridKey);
            }
            info.Reach = NavService.ReachBetween(s, storage, station, info.StorageBlockers, info.StationBlockers, GameText.Get("fwlib.route.terrain"));
            return Remember(s, info, now, gridKey);
        }

        private static FirmwareRouteInfo Remember(CampaignState s, FirmwareRouteInfo info, double now, long gridKey)
        {
            _routeCache = info;
            _routeState = s;
            _routeAt = now;
            _routeGridKey = gridKey;
            return info;
        }

        /// <summary>路线结果的玩家文字（详情页一行）。</summary>
        public static string RouteText(FirmwareRouteInfo r)
        {
            if (r.NoStation)
            {
                return GameText.Format("fwlib.detail.route_blocked", GameText.Get("fwlib.loc.storage"), GameText.Get("fwlib.route.no_station"));
            }
            switch (r.Reach)
            {
                case NavService.BuildingReach.Connected:
                    return GameText.Format("fwlib.detail.route_ok", r.StorageName);
                case NavService.BuildingReach.FromEnclosed:
                    return GameText.Format("fwlib.detail.route_blocked", r.StorageName,
                        GameText.Format("fwlib.route.storage_enclosed", r.StorageName, string.Join(Sep, r.StorageBlockers)));
                case NavService.BuildingReach.ToEnclosed:
                    return GameText.Format("fwlib.detail.route_blocked", r.StorageName,
                        GameText.Format("fwlib.route.station_enclosed", string.Join(Sep, r.StationBlockers)));
                case NavService.BuildingReach.Disconnected:
                    return GameText.Format("fwlib.detail.route_blocked", r.StorageName, GameText.Get("fwlib.route.disconnected"));
                default:
                    return GameText.Get("fwlib.detail.route_unknown");
            }
        }

        // ── 图鉴同步 ────────────────────────────────────────────────────────────

        /// <summary>“获得过”：本战役里持有过这条固件的芯片（现在还在），或内容已解锁（开局蓝图库、解析台破解 / 数据盒解锁）。</summary>
        public static bool IsObtained(CampaignState s, string firmwareId)
        {
            if (s == null || !FirmwareKinds.IsFirmware(firmwareId))
            {
                return false;
            }
            if (MechanicalContentUnlock.IsUnlocked(s, firmwareId))
            {
                return true;
            }
            foreach (PrimitiveChipRecord p in s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (p != null && p.CardDefId == firmwareId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool _installed;

        /// <summary>
        /// 订阅 <see cref="PrimitiveInventory.ChipAcquired"/>：第一次拿到某条固件的芯片即解锁它的图鉴条目（FGR-UX-051，跨存档）。
        /// 幂等；由 <see cref="OnCampaignEntered"/> 调用（新建 / 读档 / 远征回滚进入战役时），自检也直接调用。物品层只发事件，不反向依赖图鉴。
        /// </summary>
        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            PrimitiveInventory.ChipAcquired += OnChipAcquired;
        }

        private static void OnChipAcquired(CampaignState state, string contentId)
        {
            // 不是固件（基元芯片）时没有图鉴条目，什么都不做。
            if (FirmwareKinds.IsFirmware(contentId))
            {
                MechanicCodex.Unlock(MechanicCodex.FirmwareEntryId(contentId));
            }
        }

        /// <summary>进入一个战役（新建 / 读档 / 远征回滚后 <see cref="CampaignSession.Set"/> 之后调用）：确保已订阅入账事件，并按这个存档补解锁图鉴
        /// （旧存档里已经持有的芯片、开局就解锁的内容），这样悬停提示里的图鉴链接从进入游戏起就显示名字而不是“？？？”。返回新解锁条数。</summary>
        public static int OnCampaignEntered(CampaignState s)
        {
            Install();
            return SyncCodex(s);
        }

        /// <summary>
        /// 按战役补解锁图鉴（调用点：进入战役 <see cref="OnCampaignEntered"/>、打开图鉴、打开固件库；O(44 + 芯片数 + 反应数)）：获得过的固件、本存档打出过的反应。
        /// 入账当下的解锁由 <see cref="Install"/> 订阅的入账事件与反应首次触发处直接做，这里兜底旧存档与“内容解锁但没拿到芯片”的情况。返回新解锁条数。
        /// </summary>
        public static int SyncCodex(CampaignState s)
        {
            if (s == null)
            {
                return 0;
            }
            int n = 0;
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                if (row != null && IsObtained(s, row.Id) && MechanicCodex.Unlock(MechanicCodex.FirmwareEntryId(row.Id)))
                {
                    n++;
                }
            }
            foreach (ReactionFirstTriggerRecord r in s.ReactionFirstTriggers ?? Array.Empty<ReactionFirstTriggerRecord>())
            {
                if (r != null && MechanicCodex.Unlock(MechanicCodex.ReactionEntryId(r.ReactionId)))
                {
                    n++;
                }
            }
            return n;
        }

        // ── 调参 ────────────────────────────────────────────────────────────────

        private static int TuningInt(string id, int fallback) => (int)Math.Round(TuningFloat(id, fallback));

        private static float TuningFloat(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static float RefreshSeconds => Math.Max(0.05f, TuningFloat("firmware.library.refresh_seconds", 0.5f));

        public static void ResetForTests()
        {
            _routeCache = null;
            _routeState = null;
            _routeAt = double.NegativeInfinity;
            Clock = () => UnityEngine.Time.realtimeSinceStartupAsDouble;
            _revision++;
        }
    }
}
