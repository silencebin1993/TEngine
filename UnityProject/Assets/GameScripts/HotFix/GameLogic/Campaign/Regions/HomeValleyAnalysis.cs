using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-ANA-01 STORY-EXECUTION-CARDS.md：解析台唯一队列状态机——<see cref="AnalysisQueueItemRecord"/>
    /// 的唯一写入口，结构镜像 <see cref="Primitive.PrimitiveCraftStation"/>（单 Running 工位 FIFO、按
    /// <c>CreatedTick</c> 排序、断电转 WaitingPower 且保留进度、建筑被毁全部转 Failed）。
    ///
    /// ── 与解析台"材料"的关系（不是仓内实例，不需要预留机制） ──
    /// <see cref="Primitive.PrimitiveCraftStation"/> 的材料是 <see cref="Primitive.PrimitiveChipRecord"/>
    /// 仓内实例，可能同时被多个操作抢——需要 <c>ReservedByTransactionId</c> 互斥锁。解析台的"材料"是
    /// <see cref="RegionQuestItemRecord"/>（已 Recovered 的关键模块），结构上每个 <c>SalvageInstanceId</c>
    /// 只会被"成功入队一次"（<see cref="TryEnqueue"/> 拒绝为同一实例重复创建非终态队列项），不需要额外
    /// 互斥锁——"不让同一 salvageInstanceId 同时装车和解析"这条硬要求由 <see cref="RegionQuestItemRecord.State"/>
    /// 的单向状态机（Carried→Recovered 后不会再变回 Carried/OnGround，见该类型类注释）结构性保证。
    ///
    /// ── 五态与产出数值权威来源 ──
    /// "货舱/地面"＝<see cref="RegionQuestItemRecord.Carried"/>/<see cref="RegionQuestItemState.OnGround"/>
    /// （区域内，未改动）；"仓库"＝Recovered 且无非终态/Completed 队列项引用；"搬运中"＝本类队列项
    /// State==Queued；"解析台"＝State==Running。解锁目标/技术数据/时长见 <see cref="YieldTable"/>，
    /// 技术数据数值来自 DEMO-CONTENT-LOCK.md §2.5"技术数据最小闭环"（唯一点名数字的地方，时长该文档
    /// 未点名，本类按"数值越高价值的模块解析越久"取保守默认值，同类取舍见 <see cref="YieldTable"/>
    /// 各条注释）。
    ///
    /// ── "通过 WorkOrder 送解析台"的取舍（记录理由，不是静默绕过）──
    /// 验收卡字面写"通过 WorkOrder"；但 <see cref="Regions.HomeValleyWorkOrders.TryCreateHaul"/> 的
    /// Haul 唯一实现严格绑定 <see cref="GroundItemRecord"/>（资源类型+数量，有真实世界坐标可走机器
    /// 搬运路径）——关键模块是 <see cref="RegionQuestItemRecord"/>（带内容 ID 的唯一实例，Recovered
    /// 之后没有家园侧世界坐标，不是"地面上一堆废料"）。把它硬塞进 <c>GroundItemRecord</c> 会丢失
    /// ContentId 这一决定"解析产出什么"的关键字段；改造 Haul 支持第二种搬运对象是对一个已充分验证、
    /// 复杂度不低的既有系统做侵入式变更，收益（换一个队列词汇）远小于风险（回归既有搬运/仓储流程）。
    /// 本类因此走与 <see cref="Primitive.PrimitiveCraftStation"/>"材料送站台"同一precedent（该 Story
    /// 同样不经 WorkOrder，已验收 Completed）：<see cref="TryEnqueue"/> 是本类自己的正式排队事务，
    /// FIFO/单工位/断电暂停/建筑被毁全部按 WorkOrder 同等纪律实现，只是不共用 <c>WorkOrderRecord</c>
    /// 这个具体类型——五态要求的"搬运中"由 <see cref="AnalysisQueueState.Queued"/> 承载（见上）。
    ///
    /// ── FG5-RND-02 解析台 2.0（FG05 FGR-RND-020～024）──
    /// 同一条队列（上限 <see cref="AnalysisCatalog.QueueCapacity"/> = 8）现在收三种来源：Demo 区域任务物（<see cref="AnalysisQueueItemRecord.Source"/> 为空，
    /// 原样按 <see cref="YieldTable"/>）、家园里的敌方物品（<see cref="SourceItem"/>：未解析模块 / 加密固件 / 数据核心，入队时从库存取走，取消时原样退回）、
    /// 固件库里未破解的固件芯片（<see cref="SourceChip"/>：只引用，不取走、不消耗）。队列空闲时处理残骸。见 HomeValleyAnalysis.Items.cs。</summary>
    public static partial class HomeValleyAnalysis
    {
        public readonly struct YieldInfo
        {
            public readonly string UnlockContentId;
            public readonly int TechDataYield;
            /// <summary>ER6-FOUNDRY-01：DEMO-CONTENT-LOCK.md §2.5"首次解析三种可选铸造模块每种+5，
            /// 重复模块只转为+2"——<see cref="UnlockContentId"/> 在本次解析完成前已经在
            /// <see cref="CampaignState.UnlockedContentIds"/> 里时改发这个值。默认与
            /// <see cref="TechDataYield"/> 相同（关键物两条旧表项没有"重复"语义，不受影响）。</summary>
            public readonly int RepeatTechDataYield;
            public readonly float Duration;
            /// <summary>名字的文本键（FG5-RND-02：原硬编码中文改走文本键，中英两套）。</summary>
            public readonly string NameKey;
            /// <summary>当前语言下的名字（面板队列行 / 待解析清单 / 完成字幕）。</summary>
            public string DisplayName => string.IsNullOrEmpty(NameKey) ? string.Empty : GameText.Get(NameKey);

            public YieldInfo(string unlockContentId, int techDataYield, float duration, string nameKey, int? repeatTechDataYield = null)
            {
                UnlockContentId = unlockContentId;
                TechDataYield = techDataYield;
                RepeatTechDataYield = repeatTechDataYield ?? techDataYield;
                Duration = duration;
                NameKey = nameKey;
            }
        }

        /// <summary>解析目标唯一权威表——键是 <see cref="RegionQuestItemRecord.ContentId"/>（战场原始模块
        /// 标识），值是解析完成后真正解锁的可装配内容 ID（进 <see cref="CampaignState.UnlockedContentIds"/>）
        /// + 技术数据产出 + 时长。ER6-FOUNDRY-01 起补齐铸造重炮（关键物，同"静默标记器"先例）与三种
        /// 可选技术缓存（"首次+5/重复+2"，见 <see cref="YieldInfo.RepeatTechDataYield"/>）。</summary>
        public static readonly IReadOnlyDictionary<string, YieldInfo> YieldTable = new Dictionary<string, YieldInfo>
        {
            // DEMO-CONTENT-LOCK.md §2.5："解析静默标记器 +8"。
            [FracturedCityLayout.MarkerModuleContentId] =
                new YieldInfo(ComponentCatalog.FuncMarkerId, 8, 10f, "analysis.quest_name.marker_module"),
            // DEMO-CONTENT-LOCK.md §2.5："解析标记跳转协议数据盒 +12"。
            [FracturedCityLayout.ProtocolDataboxContentId] =
                new YieldInfo(FirmwareCatalog.FwMarkTagId, 12, 15f, "analysis.quest_name.protocol_databox"),
            // DEMO-CONTENT-LOCK.md §2.5："解析铸造重炮 +15"。
            [FoundryOutpostLayout.CannonModuleContentId] =
                new YieldInfo(ComponentCatalog.CompCannonId, 15, 20f, "analysis.quest_name.cannon_module"),
            // DEMO-CONTENT-LOCK.md §2.5："首次解析三种可选铸造模块每种+5，重复模块只转为+2"。
            [FoundryOutpostLayout.ArmorCacheContentId] =
                new YieldInfo(ComponentCatalog.StructArmorId, 5, 8f, "analysis.quest_name.armor_cache", repeatTechDataYield: 2),
            [FoundryOutpostLayout.HeatSinkCacheContentId] =
                new YieldInfo(ComponentCatalog.StructFinId, 5, 8f, "analysis.quest_name.heatsink_cache", repeatTechDataYield: 2),
            [FoundryOutpostLayout.ArmorPierceCacheContentId] =
                new YieldInfo(FirmwareCatalog.FwArmorPierceId, 5, 8f, "analysis.quest_name.armorpierce_cache", repeatTechDataYield: 2),
        };

        /// <summary>Demo 关键物+可选模块总数个位数（2 件关键物 + ER6-FOUNDRY-01 追加的重炮关键物与
        /// 三种可选技术缓存共 4 件，合计 6 种 contentId），队列上限给一点余量即可，不需要
        /// <see cref="Primitive.PrimitiveCraftStation.MaxActiveQueueItems"/> 那种量级。</summary>
        public const int MaxActiveQueueItems = 8;

        /// <summary>FG5-RND-02：队列项来源——家园里的一件敌方物品（已从库存取走）。</summary>
        public const string SourceItem = "item";
        /// <summary>FG5-RND-02：队列项来源——固件库里那枚未破解的固件芯片（只引用，不消耗）。</summary>
        public const string SourceChip = "chip";

        public static bool IsQuestEntry(AnalysisQueueItemRecord q) => q != null && string.IsNullOrEmpty(q.Source);

        public readonly struct AnalysisOpResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly string QueueItemId;

            private AnalysisOpResult(bool success, string failureReason, string queueItemId)
            {
                Success = success;
                FailureReason = failureReason;
                QueueItemId = queueItemId;
            }

            public static AnalysisOpResult Ok(string queueItemId) => new AnalysisOpResult(true, null, queueItemId);
            public static AnalysisOpResult Fail(string reason) => new AnalysisOpResult(false, reason, null);
        }

        private static readonly AnalysisQueueState[] ActiveStates =
        {
            AnalysisQueueState.Queued, AnalysisQueueState.Running, AnalysisQueueState.WaitingPower,
        };

        private static bool IsActive(AnalysisQueueState s) => Array.IndexOf(ActiveStates, s) >= 0;
        private static bool IsHeadCandidate(AnalysisQueueState s) => IsActive(s);

        public static AnalysisQueueItemRecord Find(CampaignState state, string queueItemId)
        {
            AnalysisQueueItemRecord[] queue = state?.AnalysisQueues;
            if (queue == null || string.IsNullOrEmpty(queueItemId))
            {
                return null;
            }
            foreach (AnalysisQueueItemRecord q in queue)
            {
                if (q != null && q.QueueItemId == queueItemId)
                {
                    return q;
                }
            }
            return null;
        }

        /// <summary>这一项是否还在办（排队 / 解析中 / 断电暂停）。</summary>
        public static bool IsActiveEntry(AnalysisQueueItemRecord q) => q != null && IsActive(q.State);

        /// <summary>某个已 Recovered 的关键物当前是否已经"在办"（搬运中/解析台/已完成）——供仓库列表
        /// 过滤（只显示还没送去解析的），也供 <see cref="TryEnqueue"/> 拒绝重复入队。</summary>
        public static bool HasActiveOrCompletedQueueItem(CampaignState state, string salvageInstanceId) =>
            state?.AnalysisQueues != null && !string.IsNullOrEmpty(salvageInstanceId) && state.AnalysisQueues.Any(q =>
                q != null && IsQuestEntry(q) && q.SalvageInstanceId == salvageInstanceId &&
                (IsActive(q.State) || q.State == AnalysisQueueState.Completed));

        /// <summary>"仓库"态：已 Recovered、有产出表项、且当前没有非终态/已完成队列项引用——玩家可选送
        /// 解析台的完整清单。</summary>
        public static List<RegionQuestItemRecord> WarehouseItems(CampaignState state)
        {
            var result = new List<RegionQuestItemRecord>();
            if (state?.RegionQuestItems == null)
            {
                return result;
            }
            foreach (RegionQuestItemRecord item in state.RegionQuestItems)
            {
                if (item.State != RegionQuestItemState.Recovered || !YieldTable.ContainsKey(item.ContentId))
                {
                    continue;
                }
                if (HasActiveOrCompletedQueueItem(state, item.SalvageInstanceId))
                {
                    continue;
                }
                result.Add(item);
            }
            return result;
        }

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        /// <summary>入队时刻：战役时间毫秒，且严格大于本队列已有的任何一项。暂停中连续排队时战役时间不走，
        /// 此前几项的时刻相同，先后只能靠随机 ID 字符串比较——顺序随机，后排的甚至会插到正在做的那一项前面。
        /// 现在同一时刻排的也按点击先后（ER8-NEG-01 负向自检发现）。</summary>
        private static long NextCreatedTick(CampaignState state)
        {
            long tick = NowTick(state);
            AnalysisQueueItemRecord[] queue = state.AnalysisQueues;
            if (queue != null)
            {
                foreach (AnalysisQueueItemRecord q in queue)
                {
                    if (q != null && q.CreatedTick >= tick)
                    {
                        tick = q.CreatedTick + 1;
                    }
                }
            }
            return tick;
        }

        /// <summary>在办的项数（排队 / 解析中 / 断电暂停）。O(队列长度)；队列长度有上限（容量 + 保留的最近结束记录 + Demo 任务物完成记录）。</summary>
        public static int ActiveCount(CampaignState state)
        {
            int n = 0;
            foreach (AnalysisQueueItemRecord q in state?.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
            {
                if (q != null && IsActive(q.State))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>FGR-RND-020：队列满了（拒绝新物品；传送带送来的留在入口，带停下）。</summary>
        public static bool QueueFull(CampaignState state) => ActiveCount(state) >= AnalysisCatalog.QueueCapacity;

        // ── 入队 ─────────────────────────────────────────────────────────────────

        public static AnalysisOpResult TryEnqueue(CampaignState state, string salvageInstanceId)
        {
            if (state == null || string.IsNullOrEmpty(salvageInstanceId))
            {
                return AnalysisOpResult.Fail("invalid-args");
            }
            RegionQuestItemRecord item = state.RegionQuestItems?.FirstOrDefault(q => q.SalvageInstanceId == salvageInstanceId);
            if (item == null)
            {
                return AnalysisOpResult.Fail("item-not-found");
            }
            if (item.State != RegionQuestItemState.Recovered)
            {
                return AnalysisOpResult.Fail("item-not-recovered");
            }
            if (!YieldTable.TryGetValue(item.ContentId, out YieldInfo info))
            {
                return AnalysisOpResult.Fail("no-yield-defined");
            }
            if (HasActiveOrCompletedQueueItem(state, salvageInstanceId))
            {
                return AnalysisOpResult.Fail("already-queued-or-analyzed");
            }
            if (QueueFull(state))
            {
                return AnalysisOpResult.Fail("queue-full");
            }

            string queueItemId = NewQueueItemId(state);
            var queueItem = new AnalysisQueueItemRecord
            {
                QueueItemId = queueItemId,
                SalvageInstanceId = salvageInstanceId,
                ContentId = item.ContentId,
                Duration = info.Duration,
                Progress = 0f,
                State = AnalysisQueueState.Queued,
                CreatedTick = NextCreatedTick(state),
            };
            state.AnalysisQueues = (state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>()).Append(queueItem).ToArray();
            return AnalysisOpResult.Ok(queueItemId);
        }

        // ── 取消 ─────────────────────────────────────────────────────────────────

        /// <summary>取消：不消耗任何前置资源（入队不扣废料/不锁材料实例——"材料"就是
        /// <see cref="RegionQuestItemRecord"/> 本身，Recovered 状态不受本类任何操作影响），取消后该
        /// 关键物立即重新出现在 <see cref="WarehouseItems"/> 里，可再次入队。</summary>
        public static AnalysisOpResult TryCancel(CampaignState state, string queueItemId)
        {
            AnalysisQueueItemRecord item = Find(state, queueItemId);
            if (item == null)
            {
                return AnalysisOpResult.Fail($"not-found:{queueItemId}");
            }
            if (!IsActive(item.State))
            {
                return AnalysisOpResult.Fail($"cannot-cancel-from:{item.State}");
            }
            item.State = AnalysisQueueState.Cancelled;
            item.BlockedReason = null;
            // FG5-RND-02（FGR-RND-023）：取消 = 物品原样退回（敌方物品回仓库、身份放回清单；芯片本来就没取走；Demo 任务物本来就留在仓库）。
            if (item.Source == SourceItem)
            {
                ReturnItem(state, item, "cancel");
            }
            PruneFinished(state);
            return AnalysisOpResult.Ok(queueItemId);
        }

        // ── 每帧驱动 ─────────────────────────────────────────────────────────────

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧调用一次（队列量级恒定个位数，
        /// 同 <see cref="Primitive.PrimitiveCraftStation.Tick"/> 性能纪律）。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state == null)
            {
                return;
            }
            AnalysisBenchState bs = state.Research?.Analysis;
            if ((bs == null || bs.WreckBuffer <= 0) && ActiveCount(state) == 0)
            {
                return; // 没活：O(队列长度) 早退（队列长度有上限）。
            }

            BuildingRecord bench = FindBench(state);
            if (BenchDestroyed(bench))
            {
                bool anyFailed = false;
                foreach (AnalysisQueueItemRecord it in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
                {
                    if (it == null || !IsActive(it.State))
                    {
                        continue;
                    }
                    it.State = AnalysisQueueState.Failed;
                    it.BlockedReason = "analysis-bench-destroyed";
                    anyFailed = true;
                    // FG5-RND-02：送进来的敌方物品不随解析台消失——放回家园（仓库放不下落地，机器搬走），身份放回清单。
                    if (it.Source == SourceItem)
                    {
                        ReturnItem(state, it, "bench-destroyed");
                    }
                }
                ReturnWrecks(state, bench);
                if (anyFailed)
                {
                    // ER8-CONTENT-01 AC-AUD-001 失败：只在真的有进行中的解析被中止时出声。
                    Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure,
                        Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAnalysisBench), GameText.Get("analysis.feedback.bench_destroyed"));
                }
                return;
            }

            AnalysisQueueItemRecord head = FindHead(state);
            if (head != null)
            {
                TickHead(state, head, dt, bench);
                return; // 队列优先：残骸的进度停住、不清零（FGR-RND-024“队列为空时”）。
            }
            TickWreck(state, bs, dt, bench);
        }

        /// <summary>
        /// 解析台能不能干活：运转中且有电。耐久没满（建筑状态“受损”，<see cref="BuildingStatusKind.Damaged"/>）的解析台仍是
        /// <see cref="BuildingConstructionState.Operational"/>，照常工作（FG4-ECO-05）；禁用 / 缺电 / 施工中 / 已摧毁都不行。
        /// 注意 <see cref="BuildingConstructionState.Damaged"/> 是“已摧毁、只剩可重建的虚影”（<see cref="BuildingStatusService"/>），不是“受损”。
        /// </summary>
        public static bool BenchWorking(BuildingRecord bench) =>
            bench != null
            && bench.ConstructionState == BuildingConstructionState.Operational
            && bench.PowerState == BuildingPowerState.Powered;

        /// <summary>
        /// 解析台已被摧毁（或不存在）：<see cref="BuildingConstructionState.Damaged"/>（正式摧毁入口
        /// <see cref="BuildingOps.ApplyDamage"/> → <see cref="HomeValleyPowerGrid.ApplyBuildingDestroyed"/> 写的“可重建虚影”）与
        /// <see cref="BuildingConstructionState.Destroyed"/> 都算。此时在办的项中止、物品退回，送入被拒（ADR-RND-002 第 5 节）；重建完成后恢复收货。
        /// </summary>
        public static bool BenchDestroyed(BuildingRecord bench) =>
            bench == null
            || bench.ConstructionState == BuildingConstructionState.Destroyed
            || bench.ConstructionState == BuildingConstructionState.Damaged;

        private static BuildingRecord[] _benchSource;
        private static BuildingRecord _benchCached;

        /// <summary>家园解析台（开局建筑，至多一座）。按建筑数组的引用缓存（数组整体替换时才重查），每步 O(1)。</summary>
        public static BuildingRecord FindBench(CampaignState state)
        {
            BuildingRecord[] all = state?.BuildingRecords;
            if (all == null)
            {
                return null;
            }
            if (ReferenceEquals(all, _benchSource))
            {
                return _benchCached;
            }
            _benchSource = all;
            _benchCached = null;
            foreach (BuildingRecord b in all)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench)
                {
                    _benchCached = b;
                    break;
                }
            }
            return _benchCached;
        }

        private static AnalysisQueueItemRecord FindHead(CampaignState state)
        {
            AnalysisQueueItemRecord head = null;
            foreach (AnalysisQueueItemRecord it in state.AnalysisQueues ?? Array.Empty<AnalysisQueueItemRecord>())
            {
                if (it == null || !IsHeadCandidate(it.State))
                {
                    continue;
                }
                if (head == null || it.CreatedTick < head.CreatedTick
                    || (it.CreatedTick == head.CreatedTick && string.CompareOrdinal(it.QueueItemId, head.QueueItemId) < 0))
                {
                    head = it;
                }
            }
            return head;
        }

        private static void TickHead(CampaignState state, AnalysisQueueItemRecord item, float dt, BuildingRecord bench)
        {
            bool powered = BenchWorking(bench);
            string blocked = bench.ConstructionState == BuildingConstructionState.Disabled ? "analysis-bench-disabled" : "analysis-bench-unpowered";

            if (item.State == AnalysisQueueState.Running)
            {
                if (!powered)
                {
                    item.State = AnalysisQueueState.WaitingPower;
                    item.BlockedReason = blocked;
                    return; // 断电：Progress 原样保留，不倒退（AC-ECO-003"断电暂停而不倒退"）。
                }
                item.Progress += dt;
                if (item.Progress >= item.Duration)
                {
                    Complete(state, item);
                }
                return;
            }

            // Queued / WaitingPower：逐帧尝试开工。
            if (!powered)
            {
                item.State = AnalysisQueueState.WaitingPower;
                item.BlockedReason = blocked;
                return;
            }
            item.State = AnalysisQueueState.Running;
            item.BlockedReason = null;
        }

        /// <summary>完成解析：二次核验关键物仍是 Recovered（理论上不会变，防御性核验同
        /// <see cref="Primitive.PrimitiveCraftStation.CompleteCraft"/> 先例），写解锁+技术数据。
        /// 幂等：<see cref="MechanicalContentUnlock"/> 用 <see cref="CampaignState.UnlockedContentIds"/>
        /// 去重判定，本方法追加前先查一遍避免同一内容出现两条完全相同的 ID（正常路径下不会发生，
        /// 双重防御无害）。</summary>
        private static void Complete(CampaignState state, AnalysisQueueItemRecord item)
        {
            item.Progress = item.Duration;
            if (!IsQuestEntry(item))
            {
                CompleteFormal(state, item); // FG5-RND-02：三类敌方物品 / 固件芯片（HomeValleyAnalysis.Items.cs）。
                PruneFinished(state);
                return;
            }

            RegionQuestItemRecord questItem = state.RegionQuestItems?.FirstOrDefault(q => q.SalvageInstanceId == item.SalvageInstanceId);
            if (questItem == null || questItem.State != RegionQuestItemState.Recovered
                || !YieldTable.TryGetValue(item.ContentId, out YieldInfo info))
            {
                item.State = AnalysisQueueState.Failed;
                item.BlockedReason = "quest-item-missing";
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure,
                    Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAnalysisBench), GameText.Get("analysis.feedback.quest_missing"));
                return;
            }

            // ER6-FOUNDRY-01："首次解析三种可选铸造模块每种+5，重复模块只转为+2"——在追加解锁之前
            // 先查一次是否已经解锁过，决定用哪个数值（关键物两条旧表项 RepeatTechDataYield==TechDataYield，
            // 行为不变）。当前 foundry_outpost 设计每种可选缓存只有一份实例，这条分支结构正确但暂无
            // 真实触发路径（同 AC-JRN-014 一类"结构就绪、尚不可达"先例，见证据文档）。
            state.UnlockedContentIds ??= Array.Empty<string>();
            bool alreadyUnlocked = Array.IndexOf(state.UnlockedContentIds, info.UnlockContentId) >= 0;
            int techYield = alreadyUnlocked ? info.RepeatTechDataYield : info.TechDataYield;
            if (!alreadyUnlocked)
            {
                state.UnlockedContentIds = state.UnlockedContentIds.Append(info.UnlockContentId).ToArray();
            }

            // 技术数据：生产型事务，Commit 那一刻才真正 +N（同 PrimitiveCraftStation 拆解分支模式）。
            string txId = item.QueueItemId + ":techdata";
            CampaignEconomyLedger.ProposeProduce(state, txId, item.QueueItemId, CampaignEconomyLedger.ResourceTechData, techYield);
            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, txId);
            if (reserve.Success)
            {
                CampaignEconomyLedger.MarkRunning(state, txId);
            }
            CampaignEconomyLedger.Commit(state, txId);

            item.State = AnalysisQueueState.Completed;
            item.BlockedReason = null;
            Log.Info($"[HomeValleyAnalysis] {info.DisplayName} 解析完成：解锁 {info.UnlockContentId}，技术数据 +{techYield}" +
                (alreadyUnlocked ? "（重复模块折扣）" : string.Empty) + "。");

            // ER8-CONTENT-01 AC-AUD-001 解析：唯一完成点出声与字幕，音色取解析台 BuildingCatalog.SfxId。
            string unlockedName = Feedback.FeedbackCues.ContentName(info.UnlockContentId);
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.AnalysisComplete,
                Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAnalysisBench),
                alreadyUnlocked || string.IsNullOrEmpty(unlockedName)
                    ? GameText.Format("analysis.result.quest_repeat", info.DisplayName, techYield)
                    : GameText.Format("analysis.result.quest_first", info.DisplayName, unlockedName, techYield),
                Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeAnalysisBench));

            // FG1-SIG-06（FGR-SIG-062）：破解敌方加密固件——同一种固件的所有实例去掉“未破解”标记（按内容记），
            // 已装在信号核里的那件自动更新（接入中的机器立刻按已破解重编译：积热回到 1 倍、不再计裸跑暴露），冷却不变。
            if (!alreadyUnlocked)
            {
                Signal.RawFirmwareService.OnCracked(state, info.UnlockContentId);
            }

            // ER6-LOOP-01：解析完成是 OBJ-06/08"解析"子条件的唯一真实写入口。
            CampaignObjectiveTracker.Recompute(state);
        }
    }
}
