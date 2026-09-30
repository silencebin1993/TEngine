using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameConfig.fg;
using GameLogic.Campaign.Primitive;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.CardDefs;
using TEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG0-SAVE-01（FGR-SYS-004 / FGT-SYS-002 基础版本）：读档时处理"表内容变更"——存档里引用了**已从游戏移除**
    /// 的内容的物品，转换成废料并通知玩家。
    ///
    /// 判定：内容 ID 在当前内容目录里查不到 → 视为已移除。退还多少废料、通知里叫什么名字，查 Luban 表
    /// fg.TbRemovedContent（数据源 tools/cell_tables/fgdata.py 的 REMOVED_CONTENT）；表里没登记的未知 ID
    /// 同样转换，但退还 0 并记 Error（开发删内容时漏登记，自检与日志会暴露）。
    ///
    /// 本 Story 覆盖的物品域：基元芯片（<see cref="CampaignState.PrimitiveChips"/>，ID = CardDefId）。
    /// FG3-LOG-09 起加建筑类型（<see cref="ReconcileBuildings"/>，在恢复编排载入机器名册之后执行）。
    /// - 仓中 / 待领取：直接转换。
    /// - 被合成队列预留：先经 <see cref="PrimitiveCraftStation.TryCancel"/> 取消该任务（材料解锁、废料全额退还），再转换。
    /// - 已装进蓝图（Draft）：**不转换**，保留在蓝图里并通知玩家重新编辑（蓝图版本里还记着内容 ID，
    ///   擅自拔掉会让已保存的蓝图失效；登记 DEBT-FG0SAVE01-03，由固件重做 FG2 承接）。
    ///
    /// 资源守恒：废料只经 <see cref="CampaignEconomyLedger"/> 发放（Propose→Reserve→Commit），事务 ID 由被移除的
    /// 实例决定，重复读档不会重复发放；实例被移除后再次读档也找不到它，天然幂等。
    /// 失败安全：已移除内容表读不出来时**什么都不转换**（不知道该退多少，不能把玩家的物品换成 0），只记 Error。
    /// </summary>
    public static class SaveContentReconciler
    {
        public const string KindPrimitiveChip = "primitive_chip";
        /// <summary>FG3-LOG-09（DEBT-FG0SAVE01-07）：建筑类型（ID = BuildingRecord.BuildingTypeId）。</summary>
        public const string KindBuilding = "building";
        public const string LedgerOwner = "save-migration";

        private static TbRemovedContent _table;
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;
        private static Func<string, bool> _isLiveOverride;
        private static Func<string, bool> _isLiveBuildingOverride;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<RemovedContent> Rows
        {
            get
            {
                EnsureLoaded();
                return _table?.DataList ?? (IReadOnlyList<RemovedContent>)Array.Empty<RemovedContent>();
            }
        }

        /// <summary>该基元内容在当前游戏里是否存在。</summary>
        public static bool IsLivePrimitive(string cardDefId)
        {
            if (_isLiveOverride != null)
            {
                return _isLiveOverride(cardDefId);
            }
            // FG1-SIG-01：固件芯片（fw_*，刻印进基元仓 / 装进信号核）的内容在固件目录里，不在基元卡目录里——同样是现存内容。
            return !string.IsNullOrEmpty(cardDefId)
                   && (CardCatalog.Get(cardDefId) != null || Content.FirmwareCatalog.TryGet(cardDefId, out _));
        }

        /// <summary>已装载（装进蓝图草稿或信号核）的实例：内容被移除时保留并通知，不转废料。</summary>
        private static bool IsInstalled(PrimitiveChipRecord c) =>
            c.State == PrimitiveChipState.Draft || c.State == PrimitiveChipState.SignalCore;

        public static bool TryGetRemoved(string id, out RemovedContent row)
        {
            EnsureLoaded();
            row = null;
            return id != null && _table != null && _table.DataMap.TryGetValue(id, out row) && row != null;
        }

        /// <summary>对一份刚读出的存档执行内容对账，返回本次新产生的通知（同时追加进
        /// <see cref="SaveHistoryState.Notices"/>）。没有任何已移除内容时返回空数组，不改动存档。</summary>
        public static SaveNoticeRecord[] Reconcile(CampaignState state, int fromContentVersion, int toContentVersion)
        {
            if (state == null)
            {
                return Array.Empty<SaveNoticeRecord>();
            }
            CampaignFgStateDomains.EnsureAll(state);
            PrimitiveChipRecord[] chips = state.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>();
            List<PrimitiveChipRecord> dead = chips.Where(c => c != null && !IsLivePrimitive(c.CardDefId)).ToList();
            // FG2-FW-01：固件目录改由 fg.TbFirmwareKind 生成。固件表读不出来时目录是空的——这时 fw_* 芯片“查不到”是表坏了，
            // 不是内容被移除，不能把玩家的固件换成废料：原样保留，只记 Error（与已移除内容表读不出来时同一失败策略）。
            if (_isLiveOverride == null && Content.FirmwareCatalog.LoadError != null)
            {
                int kept = dead.RemoveAll(c => (c.CardDefId ?? string.Empty).StartsWith("fw_", StringComparison.Ordinal));
                if (kept > 0)
                {
                    Log.Error($"[SaveContentReconciler] 固件表不可用（{Content.FirmwareCatalog.LoadError}），{kept} 枚固件芯片暂不对账，原样保留。");
                }
            }
            if (dead.Count == 0)
            {
                return Array.Empty<SaveNoticeRecord>();
            }

            EnsureLoaded();
            if (_loadError != null)
            {
                Log.Error($"[SaveContentReconciler] 已移除内容表不可用（{_loadError}），{dead.Count} 件引用已移除内容的物品暂不转换，原样保留。");
                return Array.Empty<SaveNoticeRecord>();
            }

            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var notices = new List<SaveNoticeRecord>();

            // 1) 已装进蓝图的：保留，按内容 ID 各通知一次。
            // FG1-SIG-01：装在信号核里的（SignalCore）与装进蓝图的一样按“已装载”保留。
            foreach (IGrouping<string, PrimitiveChipRecord> g in dead.Where(c => IsInstalled(c))
                         .GroupBy(c => c.CardDefId ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                notices.Add(NewNotice($"content-installed:{g.Key}:{g.OrderBy(c => c.PartId, StringComparer.Ordinal).First().PartId}",
                    "save.notice.installed_kept", new[] { NameKeyOf(g.Key) }, fromContentVersion, toContentVersion, now));
            }

            // 2) 被合成队列预留的：先取消任务（材料解锁、废料全额退还）。
            var cancelledQueues = new HashSet<string>(StringComparer.Ordinal);
            foreach (PrimitiveChipRecord chip in dead.Where(c => !IsInstalled(c)
                                                                 && !string.IsNullOrEmpty(c.ReservedByTransactionId)))
            {
                string queueId = chip.ReservedByTransactionId;
                if (cancelledQueues.Contains(queueId))
                {
                    continue;
                }
                PrimitiveCraftStation.CraftOpResult cancel = PrimitiveCraftStation.TryCancel(state, queueId);
                if (cancel.Success)
                {
                    cancelledQueues.Add(queueId);
                }
                else
                {
                    // 队列项已不存在 / 已完成：预留标记是残留，直接解锁。
                    PrimitiveInventory.ReleaseCraftReservation(state, chip.PartId, queueId);
                    Log.Warning($"[SaveContentReconciler] 合成任务 {queueId} 无法取消（{cancel.FailureReason}），已直接解除实例 {chip.PartId} 的预留。");
                }
            }
            if (cancelledQueues.Count > 0)
            {
                notices.Add(NewNotice($"content-craft-cancelled:{string.Join("+", cancelledQueues.OrderBy(q => q, StringComparer.Ordinal))}",
                    "save.notice.craft_cancelled", new[] { cancelledQueues.Count.ToString(CultureInfo.InvariantCulture) },
                    fromContentVersion, toContentVersion, now));
            }

            // 3) 仓中 / 待领取的：转换成废料。
            foreach (IGrouping<string, PrimitiveChipRecord> g in dead.Where(c => !IsInstalled(c))
                         .GroupBy(c => c.CardDefId ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<PrimitiveChipRecord> items = g.OrderBy(c => c.PartId, StringComparer.Ordinal).ToList();
                int perItem = 0;
                if (TryGetRemoved(g.Key, out RemovedContent row) && row.Kind == KindPrimitiveChip)
                {
                    perItem = Math.Max(0, row.ScrapRefund);
                }
                else
                {
                    Log.Error($"[SaveContentReconciler] 基元内容 '{g.Key}' 已不在游戏里，但 fg.TbRemovedContent 没有登记它（改 tools/cell_tables/fgdata.py 的 REMOVED_CONTENT）；按退还 0 废料转换。");
                }
                int total = perItem * items.Count;
                string eventKey = $"content-removed:{g.Key}:{items[0].PartId}";
                if (total > 0)
                {
                    string tx = eventKey;
                    CampaignEconomyLedger.LedgerResult r = CampaignEconomyLedger.ProposeProduce(state, tx, LedgerOwner, CampaignEconomyLedger.ResourceScrap, total);
                    if (r.Success)
                    {
                        r = CampaignEconomyLedger.Reserve(state, tx);
                    }
                    if (r.Success)
                    {
                        r = CampaignEconomyLedger.Commit(state, tx);
                    }
                    if (!r.Success)
                    {
                        Log.Error($"[SaveContentReconciler] 废料入账失败（{r.FailureReason}），'{g.Key}' 的 {items.Count} 件物品原样保留。");
                        continue;
                    }
                }
                PrimitiveInventory.RemoveForContentMigration(state, items.Select(c => c.PartId));
                notices.Add(NewNotice(eventKey, "save.notice.content_removed",
                    new[] { NameKeyOf(g.Key), items.Count.ToString(CultureInfo.InvariantCulture), total.ToString(CultureInfo.InvariantCulture) },
                    fromContentVersion, toContentVersion, now));
            }

            SaveHistoryState history = state.SaveHistory;
            var existing = new HashSet<string>(history.Notices.Select(n => n.NoticeId), StringComparer.Ordinal);
            SaveNoticeRecord[] fresh = notices.Where(n => existing.Add(n.NoticeId)).ToArray();
            history.Notices = history.Notices.Concat(fresh).ToArray();
            return fresh;
        }

        /// <summary>该建筑类型在当前游戏里是否存在（格网建筑表里查得到）。</summary>
        public static bool IsLiveBuilding(string typeId)
        {
            if (_isLiveBuildingOverride != null)
            {
                return _isLiveBuildingOverride(typeId);
            }
            return !string.IsNullOrEmpty(typeId) && Grid.GridContent.TryGetBuilding(typeId, out _);
        }

        /// <summary>
        /// FG3-LOG-09（DEBT-FG0SAVE01-07；FGR-SYS-004“表内容变更时，已经被删除的物品转换为废料，并在读档后通知玩家”）：
        /// 存档里的建筑类型已从游戏移除 → 拆掉它并全额返还，按类型各通知一次。必须在机器名册载入（恢复编排第 5 步）之后调用：
        /// 以它为目标的进行中工单要经 <see cref="Regions.HomeValleyWorkOrders.CancelOrder"/> 取消，机器货舱里这一趟的材料要退回。
        /// - 规划中 / 施工中的虚影：取消施工单（货舱与已运到现场的材料全额退回、虚影撤销，与玩家取消规划同一规则）。
        /// - 已建成（运转 / 受损 / 关停）的：以它为目标的修复 / 拆除等工单取消（资源事务作废）；返还实际投入的材料（<see cref="BuildingRecord.InvestedScrap"/>；
        ///   没有投入记录的旧记录按 fg.TbRemovedContent 登记的退还数），内部缓存的物品一并退回；仓库放不下的变成地面物，由机器搬回。
        /// - 端口、电网、格网占用都由建筑记录推导：记录移除后，读档后的第一次对账把端口里的物品退回、电网重算、格子放开。
        /// 表里没登记的未知类型同样转换（按实际投入返还）并记 Error；已移除内容表或格网建筑表读不出来时什么都不转换（原样保留，只记 Error）。
        /// 返回本次新产生的通知（同时追加进 <see cref="SaveHistoryState.Notices"/>）。
        /// </summary>
        public static SaveNoticeRecord[] ReconcileBuildings(CampaignState state, int fromContentVersion, int toContentVersion)
        {
            if (state?.BuildingRecords == null)
            {
                return Array.Empty<SaveNoticeRecord>();
            }
            CampaignFgStateDomains.EnsureAll(state);
            List<BuildingRecord> dead = state.BuildingRecords.Where(b => b != null && !IsLiveBuilding(b.BuildingTypeId)).ToList();
            if (dead.Count == 0)
            {
                return Array.Empty<SaveNoticeRecord>();
            }
            if (_isLiveBuildingOverride == null && Grid.GridContent.LoadError != null)
            {
                Log.Error($"[SaveContentReconciler] 格网建筑表不可用（{Grid.GridContent.LoadError}），{dead.Count} 座建筑暂不对账，原样保留。");
                return Array.Empty<SaveNoticeRecord>();
            }
            EnsureLoaded();
            if (_loadError != null)
            {
                Log.Error($"[SaveContentReconciler] 已移除内容表不可用（{_loadError}），{dead.Count} 座已移除类型的建筑暂不转换，原样保留。");
                return Array.Empty<SaveNoticeRecord>();
            }
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var notices = new List<SaveNoticeRecord>();
            foreach (IGrouping<string, BuildingRecord> g in dead.GroupBy(b => b.BuildingTypeId ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int perItem = 0;
                if (TryGetRemoved(g.Key, out RemovedContent row) && row.Kind == KindBuilding)
                {
                    perItem = Math.Max(0, row.ScrapRefund);
                }
                else
                {
                    Log.Error($"[SaveContentReconciler] 建筑类型 '{g.Key}' 已不在游戏里，但 fg.TbRemovedContent 没有登记它（改 tools/cell_tables/fgdata.py 的 REMOVED_CONTENT）；按实际投入返还。");
                }
                List<BuildingRecord> items = g.OrderBy(b => b.BuildingId, StringComparer.Ordinal).ToList();
                int refunded = 0;
                foreach (BuildingRecord b in items)
                {
                    refunded += RemoveDeadBuilding(state, b, perItem);
                }
                notices.Add(NewNotice($"content-removed:building:{g.Key}:{items[0].BuildingId}", "save.notice.building_removed",
                    new[] { NameKeyOf(g.Key), items.Count.ToString(CultureInfo.InvariantCulture), refunded.ToString(CultureInfo.InvariantCulture) },
                    fromContentVersion, toContentVersion, now));
            }
            SaveHistoryState history = state.SaveHistory;
            var existing = new HashSet<string>(history.Notices.Select(n => n.NoticeId), StringComparer.Ordinal);
            SaveNoticeRecord[] fresh = notices.Where(n => existing.Add(n.NoticeId)).ToArray();
            history.Notices = history.Notices.Concat(fresh).ToArray();
            return fresh;
        }

        /// <summary>拆掉一座已移除类型的建筑，返回退还的废料数（含已到现场 / 货舱里的施工材料与内部缓存）。</summary>
        private static int RemoveDeadBuilding(CampaignState state, BuildingRecord b, int perItem)
        {
            int refunded = 0;
            // 1) 以它为目标的进行中工单：取消。施工单（虚影）取消时把货舱与已到现场的材料退回并撤销虚影记录；其余工单只作废资源事务。
            foreach (WorkOrderRecord o in (state.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => Regions.HomeValleyWorkOrders.IsActive(o) && o.TargetId == b.BuildingId).ToList())
            {
                if (o.Kind == WorkOrderKind.Build)
                {
                    refunded += b.ConstructionDelivered;
                    if (MachineRegistry.TryGetRecord(o.AssignedMachineLogicId, out MachineRecord m))
                    {
                        refunded += Regions.HomeValleyConstruction.CargoScrap(m);
                    }
                }
                Regions.HomeValleyWorkOrders.CancelOrder(state, o.WorkOrderId, b.Position);
            }
            if (!state.BuildingRecords.Any(x => x != null && x.BuildingId == b.BuildingId))
            {
                return refunded; // 施工单取消时已经撤销了虚影并退回材料。
            }
            // 2) 没有进行中施工单的记录：已建成的返还投入，虚影返还已到现场的材料；内部缓存一并退回。
            bool built = b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Damaged
                         || b.ConstructionState == BuildingConstructionState.Disabled;
            int material = built ? (b.InvestedScrap > 0 ? b.InvestedScrap : perItem) : Math.Max(0, b.ConstructionDelivered);
            if (material > 0)
            {
                Regions.HomeValleyConstruction.ReturnMaterials(state, b.Position, CampaignEconomyLedger.ResourceScrap, material, "content-removed:" + b.BuildingId);
                refunded += material;
            }
            foreach (CargoEntry c in b.Inventory ?? Array.Empty<CargoEntry>())
            {
                if (c.Amount <= 0 || string.IsNullOrEmpty(c.ResourceType))
                {
                    continue;
                }
                Regions.HomeValleyConstruction.ReturnMaterials(state, b.Position, c.ResourceType, c.Amount, "content-removed:" + b.BuildingId + ":" + c.ResourceType);
                if (c.ResourceType == CampaignEconomyLedger.ResourceScrap)
                {
                    refunded += c.Amount;
                }
            }
            state.BuildingRecords = state.BuildingRecords.Where(x => x != null && x.BuildingId != b.BuildingId).ToArray();
            return refunded;
        }

        /// <summary>通知里"内容名称"参数存文本键：登记过的用表里的 nameKey，没登记的用"未知内容"。</summary>
        private static string NameKeyOf(string contentId) =>
            TryGetRemoved(contentId, out RemovedContent row) && !string.IsNullOrEmpty(row.NameKey)
                ? row.NameKey
                : "save.notice.unknown_content";

        private static SaveNoticeRecord NewNotice(string id, string key, string[] args, int from, int to, string now) =>
            new SaveNoticeRecord
            {
                NoticeId = id,
                TextKey = key,
                Args = args,
                FromContentVersion = from,
                ToContentVersion = to,
                CreatedAtUtc = now,
            };

        /// <summary>把一条通知渲染成当前语言的玩家文字。第一个参数若是文本键（名称），先解析成名字。</summary>
        public static string Render(SaveNoticeRecord notice)
        {
            if (notice == null || string.IsNullOrEmpty(notice.TextKey))
            {
                return string.Empty;
            }
            string[] args = notice.Args ?? Array.Empty<string>();
            object[] resolved = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? string.Empty;
                resolved[i] = i == 0 && GameText.Has(a) ? GameText.Get(a) : a;
            }
            return GameText.Format(notice.TextKey, resolved);
        }

        /// <summary>进入游戏后弹出读档通知（FGR-SYS-004）。字幕队列最多同时显示
        /// <see cref="Feedback.FeedbackCues.MaxVisibleCaptions"/> 条、满了挤掉最早的：通知多于上限时，前面逐条显示，
        /// 最后一条合并成"另有 N 条……已记入存档历史"，不让玩家一条都没看到就被挤掉。全部通知已写入 SaveHistory。
        /// 返回实际弹出的字幕条数。</summary>
        public static int RaiseLoadNotices(IReadOnlyList<SaveNoticeRecord> notices)
        {
            if (notices == null || notices.Count == 0)
            {
                return 0;
            }
            int max = Feedback.FeedbackCues.MaxVisibleCaptions;
            int shown = notices.Count <= max ? notices.Count : max - 1;
            for (int i = 0; i < shown; i++)
            {
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.SaveContentMigrated, Render(notices[i]));
            }
            if (shown == notices.Count)
            {
                return shown;
            }
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.SaveContentMigrated,
                GameText.Format("save.notice.more", (notices.Count - shown).ToString(CultureInfo.InvariantCulture)));
            return shown + 1;
        }

        // ── 表加载与测试注入 ──────────────────────────────────────────────

        public static void OverrideForTests(TbRemovedContent table, Func<string, bool> isLivePrimitive = null, string loadError = null,
            Func<string, bool> isLiveBuilding = null)
        {
            _overridden = true;
            _loaded = true;
            _table = table;
            _isLiveOverride = isLivePrimitive;
            _isLiveBuildingOverride = isLiveBuilding;
            _loadError = loadError ?? (table == null ? "测试注入：已移除内容表为空" : null);
        }

        public static void ResetForTests()
        {
            _overridden = false;
            _loaded = false;
            _table = null;
            _isLiveOverride = null;
            _isLiveBuildingOverride = null;
            _loadError = null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            try
            {
                _table = ConfigSystem.Instance.Tables?.TbRemovedContent;
                if (_table == null)
                {
                    _loadError = "配置表 fg.TbRemovedContent 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[SaveContentReconciler] {_loadError}");
            }
        }
    }
}
