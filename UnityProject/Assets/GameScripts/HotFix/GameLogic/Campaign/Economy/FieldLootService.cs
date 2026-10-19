using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG8-LOOT-01（FG08 FGR-LOOT-001 物品协议与两种去向；FGR-LOOT-010 普通掉落；FG-GAP-109 / 110；DEBT-FG5RND02-01 / FG2VFX02-04）：野外掉落与敌方物品的发放。
    /// - 野外击毁（领地驻军 / 巡逻迎战、行进途中的拦截战——家园战斗内核里不在攻城的敌方队伍）按 fg.TbLootDrop 的 minion / elite 行掷普通掉落；
    ///   敌方据点被摧毁按 outpost 行。掉落按（世界种子, 第几次野外击毁, 行号）确定：观察与否、倍速、存读档前后同一结果（FGR-BASE-021）。
    /// - 掉落落在击毁处的地上并开搬运单（与据点废料同一条路）：材料按 loot.merge_cells 合并成堆；敌方加密物品一件一堆，身份记在
    ///   <see cref="LootPacketState.GroundTags"/>。搬进家园时（<see cref="TryDeliverHaul"/>，家园唯一的搬运交付口 HomeValleyCargo.CommitHaul 调用）按协议分两种去向：
    ///   加密固件 → 固件库里的一枚未破解芯片（破解前可以由信号核裸跑，送解析台破解后可直接装配）；未解析模块 / 数据核心 → 进仓库并登记解析身份（解析台的唯一发放入口
    ///   <see cref="HomeValleyAnalysis.Acquire"/>）。中立 / 己方材料照常进仓库，直接可用。
    /// - 致命一击来自主组件是拆解钳的机器时，废料与残骸多回收 loot.claw_bonus（DEBT-FG2VFX02-04）。
    /// 开销：每次击毁 O(本来源的行数 + 地面堆数)，只在击毁事件里调用，不按帧；耗时记在 <see cref="MaxMs"/>（预算 loot.perf_ms）。
    /// </summary>
    public static class FieldLootService
    {
        public const string SalvagePrefix = "fieldloot:";
        public const string OriginField = "field";
        public const string OriginOutpost = "outpost";
        public const string OriginRaid = "raid";
        /// <summary>第一次在野外拿到掉落的引导钩子（内容在 FG15-UX-04；图鉴“物品协议与两种去向”随之解锁）。</summary>
        public const string HookFirstFieldDrop = GuidanceHooks.LootFirstFieldDrop;

        private const uint SaltChance = 0x4C4F4F31u; // "LOO1"
        private const uint SaltCount = 0x4C4F4F32u;
        private const uint SaltIdentity = 0x4C4F4F33u;
        private const uint SaltOutpost = 0x4C4F4F34u;

        // ── 自检读点（本进程累计）──
        public static long KillEvents { get; private set; }
        public static double MaxMs { get; private set; }
        public static double TotalMs { get; private set; }
        public static int ChipsGranted { get; private set; }
        public static int TagsDelivered { get; private set; }

        private static readonly List<LootDropDef> RowScratch = new List<LootDropDef>(16);

        public static void ResetSessionState()
        {
            KillEvents = 0;
            MaxMs = 0;
            TotalMs = 0;
            ChipsGranted = 0;
            TagsDelivered = 0;
        }

        public static LootPacketState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            LootPacketState st = s.LootPackets ??= new LootPacketState();
            st.GroundTags ??= Array.Empty<LootGroundTagRecord>();
            return st;
        }

        // ── 掉落 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 家园战斗内核里一个不在攻城的敌方单位被击毁（RaidResultService.OnKilled 分流过来）：按阵营与精英标记掷普通掉落。
        /// <paramref name="killerUnit"/> = 致命一击的来源单位（拆解钳加成按它的机器算；0 = 不明）。
        /// </summary>
        public static void OnFieldKill(CampaignState s, CombatSite site, RaidUnitDef def, bool elite, Vector2 at, int killerUnit)
        {
            LootPacketState st = StateOf(s);
            if (st == null)
            {
                return;
            }
            _ = LootCatalog.Rows; // 表的首次载入不计入单次结算耗时（只在载入 / 重载时发生一次）
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long serial = ++st.FieldKills;
            float mult = ClawMultiplier(s, site, killerUnit);
            Roll(s, st, elite ? LootCatalog.SourceElite : LootCatalog.SourceMinion, def?.Faction ?? string.Empty, at, serial, 0u, mult, OriginField);
            KillEvents++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            TotalMs += ms;
            MaxMs = Math.Max(MaxMs, ms);
            if (elite)
            {
                // FG8-LOOT-02（FGR-LOOT-010 / 020）：精英另生成一个待分拣数据包（不在战斗中弹出；耗时另记在 LootPickService）。
                LootPickService.OnEliteKilled(s, site, def?.Faction ?? string.Empty, at, killerUnit);
            }
        }

        /// <summary>敌方据点被摧毁（驻军全灭，TerritoryControlService 调用）：按 outpost 行掷材料、少量组件、可能的数据核心。</summary>
        public static void OnOutpostDestroyed(CampaignState s, string faction, Vector2 at)
        {
            LootPacketState st = StateOf(s);
            if (st == null)
            {
                return;
            }
            long serial = ++st.OutpostLoots;
            Roll(s, st, LootCatalog.SourceOutpost, faction ?? string.Empty, at, serial, SaltOutpost, 1f, OriginOutpost);
        }

        private static void Roll(CampaignState s, LootPacketState st, string source, string faction, Vector2 at, long serial, uint saltBase, float clawMult, string origin)
        {
            uint seed = Seed(s);
            int key = unchecked((int)(serial & 0x7FFFFFFF));
            LootCatalog.RowsFor(source, faction, RowScratch);
            string serialText = serial.ToString(CultureInfo.InvariantCulture);
            foreach (LootDropDef row in RowScratch)
            {
                double roll = WorldGenMath.Hash(seed, key, row.Index, SaltChance ^ saltBase) / 4294967296.0;
                if (roll >= row.Chance)
                {
                    continue;
                }
                int span = row.Max - row.Min + 1;
                int n = row.Min + (int)(WorldGenMath.Hash(seed, key, row.Index, SaltCount ^ saltBase) % (uint)Math.Max(1, span));
                if (clawMult > 1f && (row.Item.Id == ItemCatalog.ScrapId || row.Item.Id == AnalysisCatalog.WreckId))
                {
                    int boosted = (int)Math.Ceiling(n * clawMult);
                    st.ClawBonusItems += boosted - n;
                    n = boosted;
                }
                string sid = SalvagePrefix + source + ":" + serialText + ":" + row.Index.ToString(CultureInfo.InvariantCulture);
                if (row.Identity == LootCatalog.IdentityNone)
                {
                    SpawnMaterial(s, st, row.Item, n, at, sid, SalvagePrefix + source + ":");
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    string target = PickIdentity(row, faction, seed, key, i, saltBase);
                    SpawnEnemyItem(s, st, row.Item, target, faction, origin, at, sid + ":" + i.ToString(CultureInfo.InvariantCulture));
                }
            }
            RowScratch.Clear();
        }

        private static uint Seed(CampaignState s) => unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));

        /// <summary>按行的身份规则挑出这件敌方物品是“什么”（确定）。</summary>
        public static string PickIdentity(LootDropDef row, string faction, uint seed, int key, int i, uint saltBase = 0u)
        {
            uint h = WorldGenMath.Hash(seed, key, row.Index * 64 + i, SaltIdentity ^ saltBase);
            switch (row.Identity)
            {
                case LootCatalog.IdentityFirmwareSalvage:
                {
                    IReadOnlyList<string> pool = LootCatalog.EnemyFirmwareOf(faction);
                    return pool.Count > 0 ? pool[(int)(h % (uint)pool.Count)] : string.Empty;
                }
                case LootCatalog.IdentityModule:
                    return row.Pool.Length > 0 ? row.Pool[(int)(h % (uint)row.Pool.Length)] : string.Empty;
                case LootCatalog.IdentityLore:
                {
                    IReadOnlyList<AnalysisLoreDef> lore = AnalysisCatalog.Lore;
                    return lore.Count > 0 ? lore[(int)(h % (uint)lore.Count)].Id : string.Empty;
                }
                default:
                    return string.Empty;
            }
        }

        /// <summary>材料落地：就近合并进同一来源（<paramref name="mergePrefix"/>）、同种材料、没带身份的堆（不超过 loot.pile_max），否则另起一堆；开搬运单。</summary>
        private static void SpawnMaterial(CampaignState s, LootPacketState st, ItemDef item, int amount, Vector2 at, string salvageId, string mergePrefix)
        {
            if (amount <= 0 || item == null)
            {
                return;
            }
            string res = item.ResourceType;
            float m2 = LootCatalog.MergeCells * LootCatalog.MergeCells;
            int pileMax = LootCatalog.PileMax;
            GroundItemRecord pile = null;
            foreach (GroundItemRecord g in s.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (g == null || g.ResourceType != res || g.RegionId != HomeValleyLayout.RegionId || g.Amount + amount > pileMax
                    || g.SalvageInstanceId == null || !g.SalvageInstanceId.StartsWith(mergePrefix, StringComparison.Ordinal) || FindTag(st, g.SalvageInstanceId) >= 0)
                {
                    continue;
                }
                if ((g.Position - at).sqrMagnitude <= m2)
                {
                    pile = g;
                    break;
                }
            }
            if (pile != null)
            {
                pile.Amount += amount;
            }
            else
            {
                pile = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, res, amount, salvageId);
                st.TotalDrops++;
            }
            HomeValleyWorkOrders.TryCreateHaulPool(s, pile.GroundItemId); // 幂等：同一堆只有一张
            GuidanceHooks.Raise(HookFirstFieldDrop);
        }

        private static void SpawnEnemyItem(CampaignState s, LootPacketState st, ItemDef item, string target, string faction, string origin, Vector2 at, string salvageId)
        {
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, item.ResourceType, 1, salvageId);
            if (g == null)
            {
                return;
            }
            AddTag(s, st, new LootGroundTagRecord
            {
                SalvageId = salvageId, ItemId = item.Id, TargetId = target ?? string.Empty, Origin = origin ?? string.Empty, Faction = faction ?? string.Empty,
                Tick = GameClock.Ticks,
            });
            st.TotalDrops++;
            st.TotalEnemyItems++;
            HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
            GuidanceHooks.Raise(HookFirstFieldDrop);
            // 稀有掉落给一条带位置的反馈（材料不刷屏）：写明协议去向“需要解析”。
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Pickup, at,
                GameText.Format("loot.drop.enemy_item", item.Name, ItemProtocol.DestinationShort(ItemDestination.NeedsAnalysis)));
        }

        /// <summary>FG8-LOOT-02：三选一分拣得到、又落在地上的敌方物品（丢弃 / 携带机器阵亡）登记身份——之后与野外掉落同一条搬运交付路径。</summary>
        internal static void TagGround(CampaignState s, string salvageId, string itemId, string targetId, string origin, string faction)
        {
            LootPacketState st = StateOf(s);
            if (st == null || string.IsNullOrEmpty(salvageId) || FindTag(st, salvageId) >= 0)
            {
                return;
            }
            AddTag(s, st, new LootGroundTagRecord
            {
                SalvageId = salvageId, ItemId = itemId ?? string.Empty, TargetId = targetId ?? string.Empty, Origin = origin ?? string.Empty, Faction = faction ?? string.Empty,
                Tick = GameClock.Ticks,
            });
        }

        private static void AddTag(CampaignState s, LootPacketState st, LootGroundTagRecord tag)
        {
            int max = LootCatalog.TagsMax;
            var list = new List<LootGroundTagRecord>(st.GroundTags.Length + 1);
            int skip = Math.Max(0, st.GroundTags.Length + 1 - max); // 超出上限丢最早的身份（物品本身还在地上，解析时按身份不明）
            for (int i = skip; i < st.GroundTags.Length; i++)
            {
                list.Add(st.GroundTags[i]);
            }
            list.Add(tag);
            st.GroundTags = list.ToArray();
        }

        private static int FindTag(LootPacketState st, string salvageId)
        {
            if (st?.GroundTags == null || string.IsNullOrEmpty(salvageId))
            {
                return -1;
            }
            for (int i = 0; i < st.GroundTags.Length; i++)
            {
                if (st.GroundTags[i] != null && st.GroundTags[i].SalvageId == salvageId)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>地上 / 搬运中的这件物品的身份（没有身份返回 null）。搬运单把地面物 ID（"ground:" + 回收实例 ID）当回收实例 ID 用，这里一并去掉前缀。</summary>
        public static LootGroundTagRecord TagOf(CampaignState s, string salvageId)
        {
            LootPacketState st = StateOf(s);
            string id = Normalize(salvageId);
            int i = FindTag(st, id);
            return i >= 0 ? st.GroundTags[i] : null;
        }

        /// <summary>搬运中重新落地的后缀（HomeValleyWorkOrders 在机器阵亡 / 取消搬运 / 货物存不了时按 SourceId + RedropMarker + 工单号重新生成地面物）。</summary>
        internal const string RedropMarker = ":redrop:";

        /// <summary>
        /// 把地面物 ID / 搬运票据上的回收实例 ID 还原成登记身份时的回收实例 ID：去掉开头所有 "ground:"，再去掉第一个 ":redrop:" 起的后缀。
        /// 修复第 1 轮（审查 P1）：机器阵亡或玩家取消搬运后货物按新 ID 重新落地（可能多次，ID 形如 "ground:ground:&lt;sid&gt;:redrop:&lt;单1&gt;:redrop:&lt;单2&gt;"），
        /// 此前只去前缀，查不到身份，加密固件被当普通物品入库、永久丢了固件身份。掉落的回收实例 ID 由本类生成（fieldloot:…），本身不含 ":redrop:"。
        /// </summary>
        internal static string Normalize(string salvageId)
        {
            string id = salvageId ?? string.Empty;
            while (id.StartsWith("ground:", StringComparison.Ordinal))
            {
                id = id.Substring(7);
            }
            int cut = id.IndexOf(RedropMarker, StringComparison.Ordinal);
            return cut >= 0 ? id.Substring(0, cut) : id;
        }

        /// <summary>FG8-LOOT-03：地上带身份的敌方物品被自动拾取装进货舱——身份随货舱记录走，地面身份表里这一条移走（掉回地上时由货舱掉落重新登记）。</summary>
        internal static void TakeTag(CampaignState s, string salvageId)
        {
            LootPacketState st = StateOf(s);
            if (st != null)
            {
                RemoveTag(st, Normalize(salvageId));
            }
        }

        /// <summary>FG8-LOOT-01 债 DEBT-FG8LOOT01-06（FG8-LOOT-03 关闭）：这件搬运中的货交付时不占仓库（带身份的加密固件 → 固件库里的未破解芯片），搬运单不必等仓库空间。</summary>
        public static bool NeedsNoStorage(CampaignState s, string salvageId)
        {
            LootGroundTagRecord tag = TagOf(s, salvageId);
            return tag != null && tag.ItemId == AnalysisCatalog.EncryptedFirmwareId && FirmwareKinds.IsFirmware(tag.TargetId);
        }

        private static void RemoveTag(LootPacketState st, string salvageId)
        {
            int i = FindTag(st, salvageId);
            if (i < 0)
            {
                return;
            }
            var list = new List<LootGroundTagRecord>(st.GroundTags);
            list.RemoveAt(i);
            st.GroundTags = list.ToArray();
        }

        // ── 两种去向（交付）──────────────────────────────────────────────────────

        /// <summary>
        /// 家园搬运交付口（HomeValleyCargo.CommitHaul）在入库前调用：这件货是带身份的敌方加密物品时按协议交付并返回 true（调用方不再入库）。
        /// 加密固件 → 固件库里的未破解芯片（不占仓库；固件库满了放待领取）；未解析模块 / 数据核心 → 仓库放得下时进仓库并登记身份（放不下返回 false，
        /// 调用方照常“仓满放回地面”，身份留着下次再交付）。没有身份的货返回 false（照常入库）。
        /// </summary>
        public static bool TryDeliverHaul(CampaignState s, string salvageId, string resourceType, int amount)
        {
            LootPacketState st = StateOf(s);
            string id = Normalize(salvageId);
            int i = FindTag(st, id);
            if (i < 0 || amount <= 0)
            {
                return false;
            }
            LootGroundTagRecord tag = st.GroundTags[i];
            if (tag.ItemId == AnalysisCatalog.EncryptedFirmwareId && FirmwareKinds.IsFirmware(tag.TargetId))
            {
                if (tag.Origin == LootPickService.OriginPrototype)
                {
                    HomeValleyAnalysis.CrackFirmwareContent(s, tag.TargetId); // FG8-LOOT-02：原型刻录数据落地后再搬回，仍按“已破解”入库
                }
                for (int k = 0; k < amount; k++)
                {
                    GrantChip(s, k == 0 ? id : id + "#" + k.ToString(CultureInfo.InvariantCulture), tag.TargetId);
                }
                RemoveTag(st, id);
                TagsDelivered++;
                return true;
            }
            if (HomeValleyCargo.GetAvailableSpace(s, resourceType) < amount)
            {
                return false;
            }
            HomeValleyAnalysis.Acquire(s, tag.ItemId, tag.TargetId, tag.Origin, amount);
            RemoveTag(st, id);
            TagsDelivered++;
            return true;
        }

        /// <summary>
        /// 一件敌方物品直接进家园（突袭在家园里掉落的、或别的直接发放）：加密固件有身份时变成固件库里的未破解芯片；其余走解析台的发放入口（先进仓库，放不下落在 <paramref name="dropAt"/>）。
        /// <paramref name="salvageId"/> 用于芯片幂等（同一件不发两次）。返回发放了几件。
        /// </summary>
        public static int DeliverEnemyItem(CampaignState s, string itemId, string targetId, string origin, Vector2 dropAt, string salvageId)
        {
            if (s == null || string.IsNullOrEmpty(itemId))
            {
                return 0;
            }
            if (itemId == AnalysisCatalog.EncryptedFirmwareId && FirmwareKinds.IsFirmware(targetId))
            {
                return GrantChip(s, salvageId, targetId) ? 1 : 0;
            }
            return HomeValleyAnalysis.Acquire(s, itemId, targetId, origin, 1, dropAt);
        }

        private static bool GrantChip(CampaignState s, string salvageId, string firmwareId)
        {
            string partId = PrimitiveInventory.TryGrantEncryptedFirmware(s, salvageId, firmwareId, out bool pending);
            if (partId == null)
            {
                return false;
            }
            ChipsGranted++;
            FirmwareKinds.NotifyCrackStateChanged();
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstEncryptedFirmware); // FG13 引导表“第一次拿到加密固件 → 解析台、裸跑”（内容在 FG15-UX-04）
            HomeValleyAnalysis.NoteEnemyItemSeen(s);
            string name = FirmwareKinds.DisplayName(firmwareId) ?? firmwareId;
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Pickup, GameText.Format(pending ? "loot.drop.firmware_pending" : "loot.drop.firmware_granted", name));
            return true;
        }

        // ── 拆解钳（DEBT-FG2VFX02-04）─────────────────────────────────────────────

        /// <summary>致命一击来源单位是主组件为拆解钳的己方机器时返回 1 + loot.claw_bonus，否则 1。O(该蓝图版本数)，只在击毁事件里调用。</summary>
        public static float ClawMultiplier(CampaignState s, CombatSite site, int killerUnit)
        {
            if (s == null || site == null || killerUnit <= 0 || !site.TryGetMachineOfUnit(killerUnit, out int logic))
            {
                return 1f;
            }
            return MachineLoadoutRegistry.TryGetPrimaryId(s, logic, out string primary) && primary == ComponentCatalog.CompClawId ? 1f + LootCatalog.ClawBonus : 1f;
        }

        /// <summary>按拆解钳倍率放大一份废料 / 残骸（向上取整）。</summary>
        public static int ApplyClaw(int amount, float mult) => mult > 1f ? (int)Math.Ceiling(amount * mult) : amount;
    }
}
