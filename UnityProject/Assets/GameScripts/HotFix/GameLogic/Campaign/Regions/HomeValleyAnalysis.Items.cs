using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG5-RND-02 解析台 2.0（FG05 FGR-RND-020～024；FG13 FGU-23；FGT-RND-003）。
    ///
    /// ── 物品与身份 ──
    /// 三类敌方物品（未解析模块 / 加密固件 / 数据核心）与残骸都是物品表里的固体（fg.TbEcoItem），在仓库 / 传送带 / 地面上是可互换的计数；
    /// “这一件解析出来是什么”记在身份清单 <see cref="AnalysisBenchState.Tags"/>（掉落体系用 <see cref="Acquire"/> 发放时一并登记）。
    /// 一件物品进解析台时取走同种类最早的一条身份（面板里点名送的取那一条）；取消时物品回仓库、身份按原来的先后放回（FGR-RND-023）。
    /// 身份清单比家园里实有的件数多（物品被回收站分解等）时，<see cref="ReconcileTags"/> 去掉最新的多余身份。
    ///
    /// ── 入口 ──
    /// 面板“送进解析台”（<see cref="TryEnqueueHeld"/>：仓库里的敌方物品 / 固件库里未破解的固件芯片 / Demo 区域任务物）与传送带接到解析台入口（<see cref="PumpPort"/>，
    /// 内核收法 <see cref="BeltConst.AcceptSet2"/> 只收敌方物品与残骸；队列满了不收，留在入口，带停下并写明原因）。
    ///
    /// ── 结果（FGR-RND-021）──
    /// 未解析模块：首次解锁对应蓝图 + 技术数据，重复只给技术数据；加密固件：固件本身变成已破解（物品变成固件库里的已破解芯片，不消耗）；
    /// 固件库里的未破解芯片：破解（芯片不动、不消耗）；数据核心：技术数据，首次再加一份资料。身份不明的按重复解析（加密固件则原样退回）。
    ///
    /// ── 残骸（FGR-RND-024）──
    /// 队列里没有在办的项时，逐件处理残骸缓存（缺电 / 禁用时停住、进度保留；队列来活时让路，进度不清零）。
    ///
    /// 性能：全部按事件或每步 O(队列长度)（队列长度有上限），没有逐物品 / 逐格的循环；传送带逐格逻辑仍在 AOT 内核。
    /// </summary>
    public static partial class HomeValleyAnalysis
    {
        /// <summary>“第一次拿到敌方物品”的引导钩子（FG05 第 4 节；引导内容在 FG15-UX-04）。</summary>
        public const string HookFirstEnemyItem = GuidanceHooks.AnalysisFirstEnemyItem;

        // ── 自检读点（本进程累计）────────────────────────────────────────────────

        public static int BeltIntakeCount { get; private set; }
        public static int FormalCompletedCount { get; private set; }
        public static int WreckProcessedCount { get; private set; }
        public static int ReturnedCount { get; private set; }

        public static void ResetSessionState()
        {
            BeltIntakeCount = 0;
            FormalCompletedCount = 0;
            WreckProcessedCount = 0;
            ReturnedCount = 0;
            _benchSource = null;
            _benchCached = null;
        }

        private static AnalysisBenchState Bench(CampaignState state)
        {
            state.Research ??= new ResearchState();
            AnalysisBenchState a = state.Research.Analysis ??= new AnalysisBenchState();
            a.Tags ??= Array.Empty<EnemyItemTagRecord>();
            a.LoreRead ??= Array.Empty<string>();
            if (a.NextSerial < 1)
            {
                a.NextSerial = 1;
            }
            return a;
        }

        /// <summary>队列项 ID：按存档里的序号递增（不用随机数——观察 / 不观察、存读档前后同一结果）。</summary>
        private static string NewQueueItemId(CampaignState state)
        {
            AnalysisBenchState a = Bench(state);
            string id;
            do
            {
                id = "analysis:" + (a.NextSerial++).ToString(CultureInfo.InvariantCulture);
            }
            while (Find(state, id) != null);
            return id;
        }

        private static Vector2 BenchPosition(CampaignState state) => FindBench(state)?.Position ?? Vector2.zero;

        // ── 取得（掉落体系 / 测试的唯一发放入口）──────────────────────────────────────

        /// <summary>
        /// 家园得到 <paramref name="count"/> 件敌方物品（<paramref name="itemId"/> = 三类敌方物品或残骸），身份是 <paramref name="targetId"/>
        /// （未解析模块 = 组件 / 模块 ID；加密固件 = 固件 ID；数据核心 = fg.TbAnalysisLore.id；残骸不需要）。
        /// 先进仓库，放不下的落在 <paramref name="dropAt"/>（默认解析台旁）由机器搬回；身份登记进清单。第一次拿到敌方物品时触发引导钩子。
        /// 返回登记的件数（物品不是三类敌方物品 / 残骸时 0）。
        /// </summary>
        public static int Acquire(CampaignState state, string itemId, string targetId, string origin, int count = 1, Vector2? dropAt = null)
        {
            if (state == null || count <= 0 || !AnalysisCatalog.TryGetKind(itemId, out AnalysisKindDef def))
            {
                return 0;
            }
            AnalysisBenchState a = Bench(state);
            int stored = HomeInventory.Add(state, def.Item, count);
            if (stored < count)
            {
                HomeValleyConstruction.ReturnMaterials(state, dropAt ?? BenchPosition(state), def.Item.ResourceType, count - stored,
                    "enemy-item:" + itemId + ":" + a.NextSerial.ToString(CultureInfo.InvariantCulture));
            }
            if (def.Queued)
            {
                var list = new List<EnemyItemTagRecord>(a.Tags.Length + count);
                list.AddRange(a.Tags);
                for (int i = 0; i < count; i++)
                {
                    list.Add(new EnemyItemTagRecord
                    {
                        TagId = "enemy-item:" + (a.NextSerial++).ToString(CultureInfo.InvariantCulture),
                        ItemId = itemId,
                        TargetId = targetId ?? string.Empty,
                        Origin = origin ?? string.Empty,
                        AcquiredTick = GameClock.Ticks,
                    });
                }
                a.Tags = list.ToArray();
            }
            NoteEnemyItemSeen(state);
            HomeInventory.Touch();
            ItemDistribution.Invalidate();
            return count;
        }

        /// <summary>第一次拿到敌方物品（任何来源：掉落、Demo 区域任务物带回、敌方加密固件发放）：触发引导钩子（每个存档至多一次扫描；钩子本身按玩家只广播一次）。</summary>
        public static void NoteEnemyItemSeen(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            AnalysisBenchState a = Bench(state);
            if (a.FirstItemSeen)
            {
                return;
            }
            a.FirstItemSeen = true;
            GuidanceHooks.Raise(HookFirstEnemyItem);
        }

        // ── 待解析清单（面板）────────────────────────────────────────────────────

        /// <summary>面板“待解析的敌方物品”的一行。</summary>
        public sealed class HeldEntry
        {
            /// <summary><see cref="SourceItem"/> / <see cref="SourceChip"/> / 空 = Demo 区域任务物。</summary>
            public string Source;
            public string ItemId;
            public string TargetId;
            /// <summary>送进去时取哪一条身份（同身份多件时取最早的）；空 = 身份不明的那几件。</summary>
            public string TagId;
            public string SalvageId;
            public string ChipPartId;
            public int Count;
        }

        /// <summary>
        /// 待解析清单：仓库里的敌方物品（按身份合并，件数不超过仓库实有）、固件库里未破解的固件芯片（每种一行）、Demo 区域任务物（已带回、没送过）。
        /// O(身份数 + 芯片数 + 任务物数)，只在面板刷新时调用。
        /// </summary>
        public static void CollectHeld(CampaignState state, List<HeldEntry> into)
        {
            into.Clear();
            if (state == null)
            {
                return;
            }
            AnalysisBenchState a = Bench(state);
            foreach (AnalysisKindDef def in AnalysisCatalog.AllKinds)
            {
                if (!def.Queued)
                {
                    continue;
                }
                int stock = HomeInventory.Stock(state, def.Item);
                if (stock <= 0)
                {
                    continue;
                }
                int listed = 0;
                int start = into.Count;
                foreach (EnemyItemTagRecord t in OrderedTags(a, def.ItemId))
                {
                    if (listed >= stock)
                    {
                        break;
                    }
                    listed++;
                    HeldEntry same = null;
                    for (int i = start; i < into.Count; i++)
                    {
                        if (into[i].TargetId == t.TargetId)
                        {
                            same = into[i];
                            break;
                        }
                    }
                    if (same != null)
                    {
                        same.Count++;
                    }
                    else
                    {
                        into.Add(new HeldEntry { Source = SourceItem, ItemId = def.ItemId, TargetId = t.TargetId, TagId = t.TagId, Count = 1 });
                    }
                }
                if (stock > listed)
                {
                    // 没登记身份的余量与“身份为空”的登记合并成一行“身份不明”（同一种物品只出现一行身份不明）。
                    HeldEntry blank = null;
                    for (int i = start; i < into.Count; i++)
                    {
                        if (string.IsNullOrEmpty(into[i].TargetId))
                        {
                            blank = into[i];
                            break;
                        }
                    }
                    if (blank != null)
                    {
                        blank.Count += stock - listed;
                    }
                    else
                    {
                        into.Add(new HeldEntry { Source = SourceItem, ItemId = def.ItemId, TargetId = string.Empty, TagId = null, Count = stock - listed });
                    }
                }
            }
            // 固件库里未破解的固件芯片（每种一行；同一种已经在队列里就不再列）。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AnalysisQueueItemRecord q in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
            {
                if (q != null && q.Source == SourceChip && IsActive(q.State) && !string.IsNullOrEmpty(q.TargetId))
                {
                    seen.Add(q.TargetId);
                }
            }
            foreach (PrimitiveChipRecord c in state.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c == null || string.IsNullOrEmpty(c.CardDefId) || !FirmwareKinds.IsRaw(state, c.CardDefId) || !seen.Add(c.CardDefId))
                {
                    continue;
                }
                into.Add(new HeldEntry { Source = SourceChip, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = c.CardDefId, ChipPartId = c.PartId, Count = 1 });
            }
            foreach (RegionQuestItemRecord item in WarehouseItems(state))
            {
                into.Add(new HeldEntry { Source = string.Empty, ItemId = QuestKind(item.ContentId), TargetId = item.ContentId, SalvageId = item.SalvageInstanceId, Count = 1 });
            }
        }

        private static IEnumerable<EnemyItemTagRecord> OrderedTags(AnalysisBenchState a, string itemId)
        {
            var list = new List<EnemyItemTagRecord>();
            foreach (EnemyItemTagRecord t in a.Tags)
            {
                if (t != null && t.ItemId == itemId)
                {
                    list.Add(t);
                }
            }
            list.Sort(CompareTag);
            return list;
        }

        private static int CompareTag(EnemyItemTagRecord x, EnemyItemTagRecord y)
        {
            int c = x.AcquiredTick.CompareTo(y.AcquiredTick);
            return c != 0 ? c : string.CompareOrdinal(x.TagId, y.TagId);
        }

        /// <summary>Demo 区域任务物在物品表里算哪一类（解锁的是固件 = 加密固件，否则 = 未解析模块）。</summary>
        public static string QuestKind(string questContentId) =>
            YieldTable.TryGetValue(questContentId ?? string.Empty, out YieldInfo info) && FirmwareKinds.IsFirmware(info.UnlockContentId)
                ? AnalysisCatalog.EncryptedFirmwareId
                : AnalysisCatalog.UnparsedModuleId;

        // ── 入队 ─────────────────────────────────────────────────────────────────

        /// <summary>面板“送进解析台”：按这一行的来源入队。</summary>
        public static AnalysisOpResult TryEnqueueHeld(CampaignState state, HeldEntry e)
        {
            if (state == null || e == null)
            {
                return AnalysisOpResult.Fail("none-selected");
            }
            if (e.Source == SourceChip)
            {
                return TryEnqueueChip(state, e.ChipPartId);
            }
            if (e.Source == SourceItem)
            {
                return TryEnqueueItem(state, e.ItemId, e.TagId);
            }
            return TryEnqueue(state, e.SalvageId);
        }

        /// <summary>把仓库里的一件敌方物品送进解析台（<paramref name="tagId"/> 为空 = 身份不明的那一件；仓库里没有身份不明的就取最早的身份）。</summary>
        public static AnalysisOpResult TryEnqueueItem(CampaignState state, string itemId, string tagId)
        {
            if (state == null || !AnalysisCatalog.TryGetKind(itemId, out AnalysisKindDef def) || !def.Queued)
            {
                return AnalysisOpResult.Fail("not-enemy-item");
            }
            if (!BenchUsable(state))
            {
                return AnalysisOpResult.Fail("no-bench");
            }
            if (QueueFull(state))
            {
                return AnalysisOpResult.Fail("queue-full");
            }
            if (HomeInventory.Stock(state, def.Item) <= 0)
            {
                return AnalysisOpResult.Fail("no-stock");
            }
            AnalysisBenchState a = Bench(state);
            EnemyItemTagRecord tag = null;
            if (!string.IsNullOrEmpty(tagId))
            {
                tag = PopTag(a, itemId, tagId);
                if (tag == null)
                {
                    return AnalysisOpResult.Fail("not-found");
                }
            }
            else if (CountTags(a, itemId) >= HomeInventory.Stock(state, def.Item))
            {
                tag = PopTag(a, itemId, null); // 没有身份不明的件：按最早的身份送。
            }
            if (HomeInventory.RemoveUpTo(state, def.Item, 1) != 1)
            {
                if (tag != null)
                {
                    PutBackTag(a, tag);
                }
                return AnalysisOpResult.Fail("no-stock");
            }
            return AnalysisOpResult.Ok(Intake(state, def, tag).QueueItemId);
        }

        /// <summary>把固件库里那枚未破解的固件芯片送去破解（只引用，芯片留在原处、不消耗）。</summary>
        public static AnalysisOpResult TryEnqueueChip(CampaignState state, string partId)
        {
            PrimitiveChipRecord chip = null;
            foreach (PrimitiveChipRecord c in state?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && c.PartId == partId)
                {
                    chip = c;
                    break;
                }
            }
            if (chip == null)
            {
                return AnalysisOpResult.Fail("not-found");
            }
            if (!FirmwareKinds.IsRaw(state, chip.CardDefId))
            {
                return AnalysisOpResult.Fail("already-cracked");
            }
            foreach (AnalysisQueueItemRecord q in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
            {
                if (q != null && q.Source == SourceChip && IsActive(q.State) && q.TargetId == chip.CardDefId)
                {
                    return AnalysisOpResult.Fail("already-queued");
                }
            }
            if (!BenchUsable(state))
            {
                return AnalysisOpResult.Fail("no-bench");
            }
            if (QueueFull(state))
            {
                return AnalysisOpResult.Fail("queue-full");
            }
            AnalysisCatalog.TryGetKind(AnalysisCatalog.EncryptedFirmwareId, out AnalysisKindDef def);
            var q2 = new AnalysisQueueItemRecord
            {
                QueueItemId = NewQueueItemId(state),
                Source = SourceChip,
                ItemId = AnalysisCatalog.EncryptedFirmwareId,
                TargetId = chip.CardDefId,
                ChipPartId = chip.PartId,
                Duration = def?.Seconds ?? 15f,
                Progress = 0f,
                State = AnalysisQueueState.Queued,
                CreatedTick = NextCreatedTick(state),
            };
            Append(state, q2);
            return AnalysisOpResult.Ok(q2.QueueItemId);
        }

        /// <summary>解析台还能收货（没被摧毁；禁用 / 缺电时照收、排队等待）。</summary>
        private static bool BenchUsable(CampaignState state) => !BenchDestroyed(FindBench(state));

        /// <summary>一件物品（已经从库存 / 端口取走）排进队列。</summary>
        private static AnalysisQueueItemRecord Intake(CampaignState state, AnalysisKindDef def, EnemyItemTagRecord tag)
        {
            var q = new AnalysisQueueItemRecord
            {
                QueueItemId = NewQueueItemId(state),
                Source = SourceItem,
                ItemId = def.ItemId,
                TargetId = tag?.TargetId ?? string.Empty,
                TagId = tag?.TagId,
                TagOrigin = tag?.Origin,
                TagAcquiredTick = tag?.AcquiredTick ?? 0,
                Duration = def.Seconds,
                Progress = 0f,
                State = AnalysisQueueState.Queued,
                CreatedTick = NextCreatedTick(state),
            };
            Append(state, q);
            HomeInventory.Touch();
            return q;
        }

        private static void Append(CampaignState state, AnalysisQueueItemRecord q)
        {
            AnalysisQueueItemRecord[] old = state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>();
            var next = new AnalysisQueueItemRecord[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = q;
            state.AnalysisQueues = next;
        }

        private static int CountTags(AnalysisBenchState a, string itemId)
        {
            int n = 0;
            foreach (EnemyItemTagRecord t in a.Tags)
            {
                if (t != null && t.ItemId == itemId)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>取走一条身份：点名的那条，或同种类最早的一条（没有返回 null）。</summary>
        private static EnemyItemTagRecord PopTag(AnalysisBenchState a, string itemId, string tagId)
        {
            int at = -1;
            for (int i = 0; i < a.Tags.Length; i++)
            {
                EnemyItemTagRecord t = a.Tags[i];
                if (t == null || t.ItemId != itemId)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(tagId))
                {
                    if (t.TagId == tagId)
                    {
                        at = i;
                        break;
                    }
                    continue;
                }
                if (at < 0 || CompareTag(t, a.Tags[at]) < 0)
                {
                    at = i;
                }
            }
            if (at < 0)
            {
                return null;
            }
            EnemyItemTagRecord found = a.Tags[at];
            var next = new EnemyItemTagRecord[a.Tags.Length - 1];
            Array.Copy(a.Tags, 0, next, 0, at);
            Array.Copy(a.Tags, at + 1, next, at, a.Tags.Length - at - 1);
            a.Tags = next;
            return found;
        }

        /// <summary>把身份按原来的先后放回清单（取消 / 退回）。</summary>
        private static void PutBackTag(AnalysisBenchState a, EnemyItemTagRecord tag)
        {
            var list = new List<EnemyItemTagRecord>(a.Tags.Length + 1);
            list.AddRange(a.Tags);
            list.Add(tag);
            list.Sort(CompareTag);
            a.Tags = list.ToArray();
        }

        // ── 退回（取消 / 解析台被毁 / 身份不明的加密固件）────────────────────────────

        /// <summary>队列里那件敌方物品原样退回：物品回仓库（放不下落在解析台旁，机器搬走），身份按原来的先后放回清单。</summary>
        private static void ReturnItem(CampaignState state, AnalysisQueueItemRecord item, string why)
        {
            if (!AnalysisCatalog.TryGetKind(item.ItemId, out AnalysisKindDef def))
            {
                return;
            }
            HomeValleyConstruction.ReturnMaterials(state, BenchPosition(state), def.Item.ResourceType, 1, "analysis-return:" + why + ":" + item.QueueItemId);
            if (!string.IsNullOrEmpty(item.TagId))
            {
                PutBackTag(Bench(state), new EnemyItemTagRecord
                {
                    TagId = item.TagId, ItemId = item.ItemId, TargetId = item.TargetId ?? string.Empty, Origin = item.TagOrigin ?? string.Empty,
                    AcquiredTick = item.TagAcquiredTick,
                });
                item.TagId = null; // 只退一次。
            }
            ReturnedCount++;
            HomeInventory.Touch();
            ItemDistribution.Invalidate();
        }

        /// <summary>
        /// 读档对账（<see cref="SaveContentReconciler.ReconcileAnalysis"/>）：在办的敌方物品引用的身份已从游戏移除——这一项作废，
        /// 物品按“身份不明”原样退回家园（与仓库里同身份的件一样只去掉身份、物品保留）。返回是否退回了一件。
        /// </summary>
        public static bool ReturnUnidentified(CampaignState state, AnalysisQueueItemRecord item, string why)
        {
            if (state == null || item == null || item.Source != SourceItem)
            {
                return false;
            }
            item.TagId = null; // 身份已不存在：不放回清单。
            item.TargetId = string.Empty;
            int before = ReturnedCount;
            ReturnItem(state, item, why);
            return ReturnedCount > before;
        }

        private static void ReturnWrecks(CampaignState state, BuildingRecord bench)
        {
            AnalysisBenchState a = state.Research?.Analysis;
            if (a == null || a.WreckBuffer <= 0 || !AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef def))
            {
                return;
            }
            HomeValleyConstruction.ReturnMaterials(state, bench?.Position ?? Vector2.zero, def.Item.ResourceType, a.WreckBuffer,
                "analysis-wrecks:" + a.WrecksProcessed.ToString(CultureInfo.InvariantCulture));
            a.WreckBuffer = 0;
            a.WreckProgress = 0f;
        }

        // ── 完成（FGR-RND-021）────────────────────────────────────────────────────

        private static void CompleteFormal(CampaignState state, AnalysisQueueItemRecord item)
        {
            AnalysisBenchState a = Bench(state);
            AnalysisCatalog.TryGetKind(item.ItemId, out AnalysisKindDef def);
            string target = item.TargetId ?? string.Empty;
            bool first = false;
            int tech = 0;
            string text;
            if (item.Source == SourceChip)
            {
                // 排队时芯片只被引用、没有预留：完成前它可能已被合成台当材料消耗。家园里已经没有这种芯片了就不破解（不能“材料和破解结果都拿到”）。
                if (!HasChipOf(state, item.ChipPartId, target))
                {
                    item.State = AnalysisQueueState.Failed;
                    item.BlockedReason = "chip-missing";
                    Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure, BenchPosition(state), GameText.Get("analysis.blocked.chip_missing"));
                    return;
                }
                // 固件库里的未破解芯片：破解（按内容记，同种固件所有实例一起去掉标记），芯片留在原处、不消耗。
                first = FirmwareKinds.IsRaw(state, target);
                if (first)
                {
                    Unlock(state, target);
                    RawFirmwareService.OnCracked(state, target);
                }
                tech = def == null ? 0 : first ? def.TechFirst : def.TechRepeat;
                text = GameText.Format("analysis.result.chip", FirmwareKinds.DisplayName(target) ?? target);
            }
            else if (item.ItemId == AnalysisCatalog.EncryptedFirmwareId)
            {
                if (!FirmwareKinds.IsFirmware(target))
                {
                    // 身份不明的加密固件：破解不了，原样退回（不让玩家因为解析失去物品）。
                    item.State = AnalysisQueueState.Failed;
                    item.BlockedReason = "unidentified-firmware";
                    ReturnItem(state, item, "unidentified");
                    Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure, BenchPosition(state), GameText.Get("analysis.result.unidentified_firmware"));
                    return;
                }
                first = !MechanicalContentUnlock.IsUnlocked(state, target);
                if (first)
                {
                    Unlock(state, target);
                    RawFirmwareService.OnCracked(state, target);
                }
                // 加密固件“本身变成已破解”：这件物品变成固件库里的一枚已破解固件芯片（按队列项幂等），不消耗。
                PrimitiveInventory.TryGrantEncryptedFirmware(state, "analysis:" + item.QueueItemId, target, out bool pending);
                FirmwareKinds.NotifyCrackStateChanged();
                tech = def == null ? 0 : first ? def.TechFirst : def.TechRepeat;
                text = GameText.Format(pending ? "analysis.result.firmware_pending" : "analysis.result.firmware", FirmwareKinds.DisplayName(target) ?? target);
            }
            else if (item.ItemId == AnalysisCatalog.DataCoreId)
            {
                bool valid = AnalysisCatalog.TryGetLore(target, out AnalysisLoreDef lore);
                first = valid && Array.IndexOf(a.LoreRead, target) < 0;
                if (first)
                {
                    var read = new string[a.LoreRead.Length + 1];
                    Array.Copy(a.LoreRead, read, a.LoreRead.Length);
                    read[a.LoreRead.Length] = target;
                    a.LoreRead = read;
                }
                tech = def == null ? 0 : first ? def.TechFirst : def.TechRepeat;
                text = first ? GameText.Format("analysis.result.core_first", lore.Title, tech) : GameText.Format("analysis.result.core_repeat", tech);
                if (first)
                {
                    // FG5-RND-05（FGR-RND-021 情报部分，DEBT-FG5RND02-03）：第一次解读这份数据核心时附带一条情报（按优先级挑现在最需要的一类）。
                    IntelRecord intel = Economy.IntelService.GrantFromDataCore(state);
                    if (intel != null)
                    {
                        text += GameText.Format("analysis.result.core_intel", Economy.IntelCatalog.KindName(intel.Kind));
                    }
                }
            }
            else
            {
                // 未解析模块：身份是能装配的组件 / 模块（不是固件）且还没解锁 = 首次。
                bool valid = !string.IsNullOrEmpty(target) && MechanicalContentFacade.TryGet(target, out _) && !FirmwareKinds.IsFirmware(target);
                first = valid && !MechanicalContentUnlock.IsUnlocked(state, target);
                if (first)
                {
                    Unlock(state, target);
                }
                tech = def == null ? 0 : first ? def.TechFirst : def.TechRepeat;
                string itemName = def?.Name ?? item.ItemId;
                text = first
                    ? GameText.Format("analysis.result.module_first", itemName, Feedback.FeedbackCues.ContentName(target), tech)
                    : GameText.Format("analysis.result.module_repeat", itemName, tech);
            }
            GrantTech(state, item.QueueItemId + ":techdata", item.QueueItemId, tech);
            a.TechFromAnalysis += tech;
            a.Completed++;
            item.State = AnalysisQueueState.Completed;
            item.BlockedReason = null;
            item.FirstTime = first;
            item.TechGained = tech;
            FormalCompletedCount++;
            Log.Info($"[HomeValleyAnalysis] {item.ItemId}（{target}）解析完成：{(first ? "首次" : "重复")}，技术数据 +{tech}。");
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.AnalysisComplete, BenchPosition(state), text,
                Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeAnalysisBench));
        }

        /// <summary>送去破解的那枚芯片（或同一种固件的另一枚——破解按内容记）还在家园里吗。</summary>
        private static bool HasChipOf(CampaignState state, string partId, string contentId)
        {
            foreach (PrimitiveChipRecord c in state?.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>())
            {
                if (c != null && ((!string.IsNullOrEmpty(partId) && c.PartId == partId) || (!string.IsNullOrEmpty(contentId) && c.CardDefId == contentId)))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Unlock(CampaignState state, string contentId)
        {
            state.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(state.UnlockedContentIds, contentId) >= 0)
            {
                return;
            }
            var next = new string[state.UnlockedContentIds.Length + 1];
            Array.Copy(state.UnlockedContentIds, next, state.UnlockedContentIds.Length);
            next[state.UnlockedContentIds.Length] = contentId;
            state.UnlockedContentIds = next;
        }

        /// <summary>技术数据：生产型事务（与 Demo 解析完成同一写法），Commit 那一刻才 +N；统计面板按事务记收入。</summary>
        private static void GrantTech(CampaignState state, string txId, string ownerId, int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            CampaignEconomyLedger.ProposeProduce(state, txId, ownerId, CampaignEconomyLedger.ResourceTechData, amount);
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (reserve.Success)
            {
                CampaignEconomyLedger.MarkRunning(state, txId);
            }
            CampaignEconomyLedger.Commit(state, txId);
        }

        // ── 残骸（FGR-RND-024）────────────────────────────────────────────────────

        private static void TickWreck(CampaignState state, AnalysisBenchState a, float dt, BuildingRecord bench)
        {
            if (a == null || a.WreckBuffer <= 0 || !BenchWorking(bench) || !AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef def))
            {
                return; // 缺电 / 禁用：进度原样保留。
            }
            a.WreckProgress += dt;
            if (a.WreckProgress + 1e-5f < def.Seconds)
            {
                return;
            }
            a.WreckProgress = 0f;
            a.WreckBuffer--;
            a.WrecksProcessed++;
            a.TechFromWrecks += def.TechFirst;
            WreckProcessedCount++;
            GrantTech(state, "analysis-wreck:" + a.WrecksProcessed.ToString(CultureInfo.InvariantCulture), "analysis_bench", def.TechFirst);
            HomeInventory.Touch();
        }

        /// <summary>面板“把仓库里的残骸送进来”：仓库里的残骸按缓存剩余空间送进解析台。</summary>
        public static AnalysisOpResult TrySendWrecks(CampaignState state, out int sent)
        {
            sent = 0;
            if (state == null || !AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef def))
            {
                return AnalysisOpResult.Fail("not-enemy-item");
            }
            if (!BenchUsable(state))
            {
                return AnalysisOpResult.Fail("no-bench");
            }
            AnalysisBenchState a = Bench(state);
            int room = AnalysisCatalog.WreckBufferCap - a.WreckBuffer;
            if (room <= 0)
            {
                return AnalysisOpResult.Fail("wreck-full");
            }
            if (HomeInventory.Stock(state, def.Item) <= 0)
            {
                return AnalysisOpResult.Fail("wreck-none");
            }
            sent = HomeInventory.RemoveUpTo(state, def.Item, room);
            a.WreckBuffer += sent;
            ItemDistribution.Invalidate();
            return AnalysisOpResult.Ok(null);
        }

        /// <summary>FG6-DEF-08（FGR-DEF-051“由机器搬去解析台”）：机器送来的残骸放进残骸缓存，返回放进去的份数（解析台不可用 / 缓存满 = 0，调用方把剩下的退回）。</summary>
        public static int DepositWrecks(CampaignState state, int amount)
        {
            if (state == null || amount <= 0 || !BenchUsable(state))
            {
                return 0;
            }
            AnalysisBenchState a = Bench(state);
            int put = Math.Max(0, Math.Min(amount, AnalysisCatalog.WreckBufferCap - a.WreckBuffer));
            if (put > 0)
            {
                a.WreckBuffer += put;
                HomeInventory.Touch();
                ItemDistribution.Invalidate();
            }
            return put;
        }

        /// <summary>FG6-DEF-08：解析台残骸缓存还能放几份（解析台不可用 = 0）。</summary>
        public static int WreckRoom(CampaignState state) =>
            state != null && BenchUsable(state) ? Math.Max(0, AnalysisCatalog.WreckBufferCap - Bench(state).WreckBuffer) : 0;

        /// <summary>FG6-DEF-08：解析台残骸缓存里现有几份。</summary>
        public static int WreckBuffered(CampaignState state) => state != null ? Bench(state)?.WreckBuffer ?? 0 : 0;

        // ── 传送带入口（DEBT-FG3LOG03-01 解析台部分）──────────────────────────────

        public static bool IsBenchPort(string portKey) =>
            portKey != null && portKey.StartsWith(HomeValleyLayout.BuildingTypeAnalysisBench + ".", StringComparison.Ordinal);

        /// <summary>
        /// 解析台输入口（role = prod，内核收法 <see cref="BeltConst.AcceptSet2"/>）：缓存里的敌方物品按队列空位逐件入队（取同种类最早的身份），
        /// 残骸按残骸缓存空位收进来；放不下的留在端口缓存里，后面的带停下（原因“解析队列已满”）。O(1)（每步最多收队列容量件）。
        /// </summary>
        public static void PumpPort(CampaignState state, BeltPortService.Binding bind, BeltKernel k, int buffered, ushort kind)
        {
            if (state == null || buffered <= 0 || bind == null)
            {
                return;
            }
            if (!AnalysisCatalog.TryGetKindByBelt(kind, out AnalysisKindDef def))
            {
                // 不在收货集合里的编号（旧存档 / 表删了某种物品）：放到解析台旁边的地上（不消失、不堵带）。
                int n0 = k.TakeFromSink(bind.PortId, buffered);
                if (n0 > 0)
                {
                    HomeValleyConstruction.ReturnMaterials(state, BenchPosition(state), BeltItems.ResourceOf(kind), n0,
                        "analysis-port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":unknown:" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                return;
            }
            if (!BenchUsable(state))
            {
                return;
            }
            AnalysisBenchState a = Bench(state);
            if (!def.Queued)
            {
                int room = AnalysisCatalog.WreckBufferCap - a.WreckBuffer;
                if (room > 0)
                {
                    a.WreckBuffer += k.TakeFromSink(bind.PortId, Math.Min(room, buffered));
                }
                return;
            }
            int free = AnalysisCatalog.QueueCapacity - ActiveCount(state);
            for (int i = 0; i < Math.Min(free, buffered); i++)
            {
                if (k.TakeFromSink(bind.PortId, 1) != 1)
                {
                    break;
                }
                Intake(state, def, PopTag(a, def.ItemId, null));
                BeltIntakeCount++;
            }
        }

        /// <summary>端口面板：解析台输入口收什么。</summary>
        public static string PortAcceptLine(CampaignState state)
        {
            AnalysisBenchState a = state?.Research?.Analysis;
            return GameText.Format("analysis.port.accept", ActiveCount(state), AnalysisCatalog.QueueCapacity, a?.WreckBuffer ?? 0, AnalysisCatalog.WreckBufferCap);
        }

        // ── 结束记录的清理 ───────────────────────────────────────────────────────

        private static readonly List<AnalysisQueueItemRecord> PruneScratch = new List<AnalysisQueueItemRecord>(16);

        /// <summary>只保留在办的项、Demo 区域任务物的完成记录（目标判定要用），以及最近结束的 analysis.history.keep 条；其余清掉（队列长度有上限，存档不膨胀）。</summary>
        private static void PruneFinished(CampaignState state)
        {
            AnalysisQueueItemRecord[] queue = state?.AnalysisQueues;
            if (queue == null)
            {
                return;
            }
            int keep = AnalysisCatalog.HistoryKeep;
            PruneScratch.Clear();
            foreach (AnalysisQueueItemRecord q in queue)
            {
                if (q != null && !IsActive(q.State) && !(IsQuestEntry(q) && q.State == AnalysisQueueState.Completed))
                {
                    PruneScratch.Add(q);
                }
            }
            if (PruneScratch.Count <= keep)
            {
                return;
            }
            PruneScratch.Sort((x, y) =>
            {
                int c = y.CreatedTick.CompareTo(x.CreatedTick);
                return c != 0 ? c : string.CompareOrdinal(y.QueueItemId, x.QueueItemId);
            });
            var drop = new HashSet<AnalysisQueueItemRecord>();
            for (int i = keep; i < PruneScratch.Count; i++)
            {
                drop.Add(PruneScratch[i]);
            }
            var next = new List<AnalysisQueueItemRecord>(queue.Length - drop.Count);
            foreach (AnalysisQueueItemRecord q in queue)
            {
                if (q != null && !drop.Contains(q))
                {
                    next.Add(q);
                }
            }
            state.AnalysisQueues = next.ToArray();
        }

        // ── 身份清单对账 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 身份清单比家园里实有的件数多（物品被回收站分解、被毁）时，去掉同种类最新的多余身份。<paramref name="physical"/> 给出每种在家园的实有件数
        /// （仓库 + 传送带 + 端口 + 地面 + 货舱 + 生产缓存，不含解析台队列里的）。返回去掉了几条。只在面板刷新 / 读档时调用。
        /// </summary>
        public static int ReconcileTags(CampaignState state, Func<ItemDef, long> physical)
        {
            if (state == null || physical == null)
            {
                return 0;
            }
            AnalysisBenchState a = Bench(state);
            int removed = 0;
            foreach (AnalysisKindDef def in AnalysisCatalog.AllKinds)
            {
                if (!def.Queued)
                {
                    continue;
                }
                long have = Math.Max(0, physical(def.Item));
                int tags = CountTags(a, def.ItemId);
                while (tags > have)
                {
                    EnemyItemTagRecord newest = null;
                    foreach (EnemyItemTagRecord t in a.Tags)
                    {
                        if (t != null && t.ItemId == def.ItemId && (newest == null || CompareTag(t, newest) > 0))
                        {
                            newest = t;
                        }
                    }
                    if (newest == null)
                    {
                        break;
                    }
                    PopTag(a, def.ItemId, newest.TagId);
                    tags--;
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>家园里这种物品的实有件数（物资悬停分布里除“解析台”“区域任务物”以外的各处之和）。</summary>
        public static long PhysicalCount(CampaignState state, ItemDef item)
        {
            ItemDistributionView v = ItemDistribution.Get(state, item);
            if (v == null)
            {
                return 0;
            }
            long n = 0;
            foreach (KeyValuePair<string, long> p in v.Parts)
            {
                if (p.Key != "item.dist.analysis" && p.Key != "item.dist.quest")
                {
                    n += p.Value;
                }
            }
            return n;
        }

        // ── 物资分布（DEBT-FG4ECO01-04）──────────────────────────────────────────

        /// <summary>
        /// 解析台里的敌方物品（队列里在办的、残骸缓存）与 Demo 区域任务物（已带回还没送 / 正在解析）按物品种类累加：
        /// <paramref name="inBench"/> = “解析台”，<paramref name="quest"/> = “区域任务物（待解析）”。O(队列长度 + 任务物数)，只在悬停缓存刷新时调用。
        /// </summary>
        public static void CollectDistribution(CampaignState state, Dictionary<string, long> inBench, Dictionary<string, long> quest)
        {
            if (state == null)
            {
                return;
            }
            foreach (AnalysisQueueItemRecord q in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
            {
                if (q == null || !IsActive(q.State) || q.Source == SourceChip)
                {
                    continue;
                }
                string id = IsQuestEntry(q) ? QuestKind(q.ContentId) : q.ItemId;
                if (!string.IsNullOrEmpty(id))
                {
                    inBench[id] = (inBench.TryGetValue(id, out long n) ? n : 0) + 1;
                }
            }
            int wrecks = state.Research?.Analysis?.WreckBuffer ?? 0;
            if (wrecks > 0)
            {
                inBench[AnalysisCatalog.WreckId] = (inBench.TryGetValue(AnalysisCatalog.WreckId, out long w) ? w : 0) + wrecks;
            }
            foreach (RegionQuestItemRecord item in WarehouseItems(state))
            {
                string id = QuestKind(item.ContentId);
                quest[id] = (quest.TryGetValue(id, out long n) ? n : 0) + 1;
            }
        }

        // ── 预览与文字（FGR-RND-022）────────────────────────────────────────────────

        /// <summary>这一件的身份是“已知类型”吗（解析后会得到什么可以直接显示）：未解析模块 / 加密固件 = 内容已解锁；数据核心 = 资料读过。身份不明 = 已知（按重复解析）。</summary>
        public static bool IsKnown(CampaignState state, string itemId, string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return true;
            }
            if (itemId == AnalysisCatalog.DataCoreId)
            {
                return !AnalysisCatalog.TryGetLore(targetId, out _) || Array.IndexOf(state?.Research?.Analysis?.LoreRead ?? Array.Empty<string>(), targetId) >= 0;
            }
            if (itemId == AnalysisCatalog.EncryptedFirmwareId)
            {
                return !FirmwareKinds.IsFirmware(targetId) || !FirmwareKinds.IsRaw(state, targetId);
            }
            return !MechanicalContentFacade.TryGet(targetId, out _) || FirmwareKinds.IsFirmware(targetId) || MechanicalContentUnlock.IsUnlocked(state, targetId);
        }

        /// <summary>结果预览（第一次遇到的类型显示“？？”与首次提示；已知类型直接写会得到什么）。</summary>
        public static string Preview(CampaignState state, string source, string itemId, string targetId)
        {
            AnalysisCatalog.TryGetKind(itemId, out AnalysisKindDef def);
            int first = def?.TechFirst ?? 0;
            int repeat = def?.TechRepeat ?? 0;
            if (source == SourceChip)
            {
                return FirmwareKinds.IsRaw(state, targetId)
                    ? GameText.Get("analysis.preview.first_firmware")
                    : GameText.Format("analysis.preview.chip_known", FirmwareKinds.DisplayName(targetId) ?? targetId);
            }
            if (itemId == AnalysisCatalog.EncryptedFirmwareId)
            {
                if (!FirmwareKinds.IsFirmware(targetId))
                {
                    return GameText.Get("analysis.preview.unidentified_firmware");
                }
                return IsKnown(state, itemId, targetId)
                    ? GameText.Format("analysis.preview.firmware_known", FirmwareKinds.DisplayName(targetId) ?? targetId)
                    : GameText.Get("analysis.preview.first_firmware");
            }
            if (string.IsNullOrEmpty(targetId))
            {
                return GameText.Format("analysis.preview.unidentified", repeat);
            }
            if (itemId == AnalysisCatalog.DataCoreId)
            {
                if (!AnalysisCatalog.TryGetLore(targetId, out _))
                {
                    return GameText.Format("analysis.preview.unidentified", repeat);
                }
                return IsKnown(state, itemId, targetId)
                    ? GameText.Format("analysis.preview.core_known", repeat)
                    : GameText.Format("analysis.preview.first_lore", first);
            }
            if (!MechanicalContentFacade.TryGet(targetId, out _) || FirmwareKinds.IsFirmware(targetId))
            {
                return GameText.Format("analysis.preview.unidentified", repeat);
            }
            return IsKnown(state, itemId, targetId)
                ? GameText.Format("analysis.preview.module_known", repeat, Feedback.FeedbackCues.ContentName(targetId))
                : GameText.Get("analysis.preview.first_blueprint");
        }

        /// <summary>Demo 区域任务物的预览（按冻结的 Demo 数值表）。</summary>
        public static string QuestPreview(CampaignState state, string questContentId)
        {
            if (!YieldTable.TryGetValue(questContentId ?? string.Empty, out YieldInfo info))
            {
                return GameText.Get("analysis.preview.unknown");
            }
            bool unlocked = state?.UnlockedContentIds != null && Array.IndexOf(state.UnlockedContentIds, info.UnlockContentId) >= 0;
            return GameText.Format("analysis.preview.quest", info.DisplayName, unlocked ? info.RepeatTechDataYield : info.TechDataYield);
        }

        /// <summary>预览里的时间与耗电（每项有解析时间与耗电，FGR-RND-020）。</summary>
        public static string TimeLine(float seconds)
        {
            float power = FgContentTables.TryGetBuilding(HomeValleyLayout.BuildingTypeAnalysisBench, out GameConfig.fg.Building row) ? row.PowerDemand : 0f;
            return GameText.Format("analysis.preview.time", seconds.ToString("0.#", CultureInfo.InvariantCulture), power.ToString("0.#", CultureInfo.InvariantCulture));
        }

        /// <summary>队列项 / 待解析行的名字（物品名 + 身份名）。</summary>
        public static string EntryName(string source, string itemId, string targetId, string questContentId = null)
        {
            if (string.IsNullOrEmpty(source))
            {
                return YieldTable.TryGetValue(questContentId ?? targetId ?? string.Empty, out YieldInfo info) && !string.IsNullOrEmpty(info.DisplayName)
                    ? info.DisplayName
                    : GameText.Get("analysis.preview.unknown");
            }
            string kind = AnalysisCatalog.TryGetKind(itemId, out AnalysisKindDef def) ? def.Name : itemId;
            if (source == SourceChip)
            {
                return FirmwareKinds.DisplayName(targetId) ?? targetId;
            }
            return kind;
        }

        public static string EntryName(AnalysisQueueItemRecord q) =>
            q == null ? string.Empty : EntryName(q.Source, q.ItemId, q.TargetId, q.ContentId);

        public static string StateText(AnalysisQueueState s)
        {
            switch (s)
            {
                case AnalysisQueueState.Queued: return GameText.Get("analysis.state.queued");
                case AnalysisQueueState.Running: return GameText.Get("analysis.state.running");
                case AnalysisQueueState.WaitingPower: return GameText.Get("analysis.state.waiting_power");
                case AnalysisQueueState.Completed: return GameText.Get("analysis.state.completed");
                case AnalysisQueueState.Cancelled: return GameText.Get("analysis.state.cancelled");
                default: return GameText.Get("analysis.state.failed");
            }
        }

        /// <summary>队列项受阻原因码 → 文字。</summary>
        public static string BlockedText(string code)
        {
            switch (code)
            {
                case null:
                case "":
                    return string.Empty;
                case "analysis-bench-unpowered": return GameText.Get("analysis.blocked.unpowered");
                case "analysis-bench-disabled": return GameText.Get("analysis.blocked.disabled");
                case "analysis-bench-destroyed": return GameText.Get("analysis.blocked.destroyed");
                case "quest-item-missing": return GameText.Get("analysis.blocked.item_missing");
                case "chip-missing": return GameText.Get("analysis.blocked.chip_missing");
                case "content-removed": return GameText.Get("analysis.blocked.content_removed");
                case "unidentified-firmware": return GameText.Get("analysis.result.unidentified_firmware");
                default: return UI.Common.QueueText.Reason(code);
            }
        }

        /// <summary>入队 / 取消被拒的原因码 → 文字（面板结果行）。</summary>
        public static string ReasonText(string code)
        {
            switch (code)
            {
                case "none-selected": return GameText.Get("analysis.reason.none_selected");
                case "queue-full": return GameText.Format("analysis.reason.queue_full", AnalysisCatalog.QueueCapacity);
                case "no-stock": return GameText.Get("analysis.reason.no_stock");
                case "already-cracked": return GameText.Get("analysis.reason.already_cracked");
                case "already-queued":
                case "already-queued-or-analyzed": return GameText.Get("analysis.reason.already_queued");
                case "no-bench": return GameText.Get("analysis.reason.no_bench");
                case "not-enemy-item": return GameText.Get("analysis.reason.not_enemy_item");
                case "wreck-none": return GameText.Get("analysis.reason.wreck_none");
                case "wreck-full": return GameText.Format("analysis.reason.wreck_full", AnalysisCatalog.WreckBufferCap);
                default:
                    return code != null && code.StartsWith("cannot-cancel-from", StringComparison.Ordinal)
                        ? GameText.Get("analysis.reason.not_cancellable")
                        : GameText.Get("analysis.reason.not_found");
            }
        }

        /// <summary>解析台此刻在干什么（面板状态行；“解析台空闲：没有待解析的物品”，FG05 第 4 节）。</summary>
        public static string StatusLine(CampaignState state)
        {
            BuildingRecord bench = FindBench(state);
            if (BenchDestroyed(bench))
            {
                return GameText.Get("analysis.status.destroyed");
            }
            AnalysisQueueItemRecord head = state != null ? FindHead(state) : null;
            int wrecks = state?.Research?.Analysis?.WreckBuffer ?? 0;
            string line;
            if (head == null && wrecks <= 0)
            {
                line = GameText.Get("analysis.status.idle");
            }
            else if (!BenchWorking(bench))
            {
                line = GameText.Get(bench.ConstructionState == BuildingConstructionState.Disabled ? "analysis.status.disabled" : "analysis.status.no_power");
            }
            else if (head != null)
            {
                line = head.State == AnalysisQueueState.Running
                    ? GameText.Format("analysis.status.working", EntryName(head), Percent(head.Progress, head.Duration))
                    : GameText.Format("analysis.status.waiting", ActiveCount(state));
            }
            else
            {
                AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef wd);
                line = GameText.Format("analysis.status.wreck", Percent(state.Research.Analysis.WreckProgress, wd?.Seconds ?? 1f), wrecks);
            }
            if (state != null && QueueFull(state))
            {
                line += "  " + GameText.Format("analysis.status.full", AnalysisCatalog.QueueCapacity);
            }
            return line;
        }

        public static int Percent(float progress, float duration) =>
            duration > 0f ? Mathf.Clamp(Mathf.FloorToInt(progress / duration * 100f), 0, 100) : 0;

        /// <summary>建筑通用状态（建筑面板 / 悬停 / “为什么不工作”，B05 / B06）。</summary>
        public static BuildingStatus Status(CampaignState state, BuildingRecord b)
        {
            AnalysisQueueItemRecord head = state != null ? FindHead(state) : null;
            int wrecks = state?.Research?.Analysis?.WreckBuffer ?? 0;
            if (head == null && wrecks <= 0)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "analysis.idle", GameText.Get("bs.reason.idle_analysis"));
            }
            if (!BenchWorking(b))
            {
                string why = b.PowerState == BuildingPowerState.Brownout
                    ? GameText.Format("prod.reason.power_brownout", b.PowerPriority)
                    : GameText.Get("prod.reason.power_unconnected");
                return new BuildingStatus(BuildingStatusKind.NoPower, "analysis.no_power", GameText.Format("bs.reason.analysis_no_power", why));
            }
            if (head != null)
            {
                return new BuildingStatus(BuildingStatusKind.Working, "analysis", GameText.Format("bs.reason.working_analysis", Percent(head.Progress, head.Duration)));
            }
            AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef wd);
            return new BuildingStatus(BuildingStatusKind.Working, "analysis.wreck",
                GameText.Format("bs.reason.wreck_analysis", Percent(state.Research.Analysis.WreckProgress, wd?.Seconds ?? 1f)));
        }

        /// <summary>残骸栏（缓存 / 进度 / 每件产出 / 已处理）。</summary>
        public static string WreckLine(CampaignState state)
        {
            AnalysisBenchState a = state?.Research?.Analysis;
            AnalysisCatalog.TryGetKind(AnalysisCatalog.WreckId, out AnalysisKindDef wd);
            float sec = wd?.Seconds ?? 0f;
            return GameText.Format("analysis.wreck.line", a?.WreckBuffer ?? 0, AnalysisCatalog.WreckBufferCap, Percent(a?.WreckProgress ?? 0f, sec),
                wd?.TechFirst ?? 0, sec.ToString("0.#", CultureInfo.InvariantCulture), a?.WrecksProcessed ?? 0);
        }

        /// <summary>资料栏（读过几份、最近一份的标题）。</summary>
        public static string LoreLine(CampaignState state)
        {
            string[] read = state?.Research?.Analysis?.LoreRead ?? Array.Empty<string>();
            if (read.Length == 0)
            {
                return GameText.Get("analysis.lore.none");
            }
            var titles = new List<string>(read.Length);
            foreach (string id in read)
            {
                titles.Add(AnalysisCatalog.TryGetLore(id, out AnalysisLoreDef d) ? d.Title : id);
            }
            return GameText.Format("analysis.lore.line", read.Length, string.Join(GameText.Get("signal.core.summary_sep"), titles));
        }
    }
}
