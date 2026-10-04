using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign
{
    /// <summary>
    /// ER3-ECO-01：<see cref="ResourceTransactionRecord"/>（ERD-DAT-005，ER1-SAVE-01 已定义骨架）的
    /// 唯一写入口。仅覆盖 STORY-EXECUTION-CARDS.md 点名的三个顶层资源——废料（<see cref="ResourceScrap"/>）、
    /// 技术数据（<see cref="ResourceTechData"/>）、电力容量（<see cref="ResourcePower"/>）。所有建造、
    /// 生产、维修、改造、解析奖励一律经本类，不得绕过直接改 <see cref="CampaignState.Scrap"/> 等字段。
    ///
    /// ── 两条流水线，用 <see cref="ResourceTransactionRecord.Requested"/> 的符号区分（对外 API 只接受
    /// 非负 amount，符号是内部实现细节，落盘后跨进程重启仍能正确复原——不依赖任何进程内瞬时状态）──
    /// 消费型（<see cref="ProposeConsume"/>，Requested 存正数）：<c>Reserve</c> 时立即从资源池扣款并锁定，
    /// <c>Cancel</c>/<c>Fail</c> 全额退还未消费部分（<see cref="ResourceTransactionRecord.Refundable"/>），
    /// <c>Commit</c> 只做终态确认，不再二次扣款——防止双花。
    /// 生产/奖励型（<see cref="ProposeProduce"/>，Requested 存负数）：<c>Reserve</c> 阶段不动资源池
    /// （没有"预占别人的东西"这回事），资源在 <c>Commit</c> 那一刻才真正发放；<c>Cancel</c>/<c>Fail</c>
    /// 是空退款（本就没扣）。
    ///
    /// ── 幂等 ──
    /// 同一 <c>transactionId</c> 重复调用 Reserve/Commit/Cancel/Fail 只在首次产生资源池副作用，
    /// 之后的重复调用（重试、读档重放）直接返回既有结果，满足 AC-ECO-001"最多扣款/产物一次"。
    /// </summary>
    public static class CampaignEconomyLedger
    {
        private static readonly System.Text.RegularExpressions.Regex ShortfallPattern =
            new System.Text.RegularExpressions.Regex(@"insufficient:(\w+):need=([\d.]+):have=([\d.]+)");

        /// <summary>从 <c>insufficient:{资源}:need={X}:have={Y}</c>（可能带前缀，如 insufficient-scrap:）解析缺口。</summary>
        public static bool TryParseShortfall(string reason, out string resourceType, out float need, out float have)
        {
            resourceType = null;
            need = have = 0f;
            if (string.IsNullOrEmpty(reason))
            {
                return false;
            }
            System.Text.RegularExpressions.Match m = ShortfallPattern.Match(reason);
            if (!m.Success)
            {
                return false;
            }
            resourceType = m.Groups[1].Value;
            float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out need);
            float.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out have);
            return true;
        }

        /// <summary>缺料的玩家文字：“废料不足：需要 30，现有 10”。不是缺料原因码返回 null。</summary>
        public static string DescribeShortfall(string reason)
        {
            if (!TryParseShortfall(reason, out string resourceType, out float need, out float have))
            {
                return null;
            }
            string name = resourceType == "Scrap" ? "废料" : resourceType == "TechData" ? "技术数据" : "资源";
            return $"{name}不足：需要 {need:0}，现有 {have:0}";
        }

        public const string ResourceScrap = "Scrap";
        public const string ResourceTechData = "TechData";
        public const string ResourcePower = "PowerCapacity";

        /// <summary>玩家可见的资源名。未知类型回退“资源”，绝不把内部键直接显示给玩家（AC-THEME-001）。</summary>
        public static string ResourceDisplayName(string resourceType)
        {
            switch (resourceType)
            {
                case ResourceScrap: return "废料";
                case ResourceTechData: return "技术数据";
                case ResourcePower: return "电力";
                default:
                    // FG4-ECO-01：物品表里的物品显示表里的名字（不把内部键直接显示给玩家）。
                    return Economy.ItemCatalog.TryGetByResource(resourceType, out Economy.ItemDef def) ? def.Name : "资源";
            }
        }

        /// <summary>玩家可见的交易状态。</summary>
        public static string StateDisplayName(ResourceTransactionState state)
        {
            switch (state)
            {
                case ResourceTransactionState.Proposed: return "待确认";
                case ResourceTransactionState.Reserved: return "已预留";
                case ResourceTransactionState.Running: return "进行中";
                case ResourceTransactionState.Committed: return "已结算";
                case ResourceTransactionState.Cancelled: return "已取消";
                case ResourceTransactionState.Failed: return "失败";
                default: return string.Empty;
            }
        }

        public readonly struct LedgerResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private LedgerResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static LedgerResult Ok() => new LedgerResult(true, null);
            public static LedgerResult Fail(string reason) => new LedgerResult(false, reason);
        }

        /// <summary>供 UI（不在本 Story 范围内，见 STORY-EXECUTION-CARDS.md #ER3-ECO-01 第3条）
        /// 追溯"当前可用/预留中/最近收支"的只读快照，纯数据、不含任何 UI 框架依赖。</summary>
        public readonly struct ResourceSummary
        {
            public readonly float Available;
            public readonly float OpenReserved;
            public readonly ResourceTransactionRecord[] RecentEntries;

            public ResourceSummary(float available, float openReserved, ResourceTransactionRecord[] recentEntries)
            {
                Available = available;
                OpenReserved = openReserved;
                RecentEntries = recentEntries ?? Array.Empty<ResourceTransactionRecord>();
            }
        }

        public static ResourceTransactionRecord Find(CampaignState state, string transactionId)
        {
            if (state?.ResourceTransactions == null || string.IsNullOrEmpty(transactionId))
            {
                return null;
            }
            foreach (ResourceTransactionRecord r in state.ResourceTransactions)
            {
                if (r != null && string.Equals(r.TransactionId, transactionId, StringComparison.Ordinal))
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>当前可用余额 + 仍未终结（Reserved/Running）的锁定总额 + 最近若干条流水
        /// （按登记顺序倒序，供 UI 展示最近收支；不做时间戳排序——本 Story 的记录本身没有时间字段，
        /// 追加顺序即时间顺序）。</summary>
        public static ResourceSummary GetSummary(CampaignState state, string resourceType, int recentCount = 10)
        {
            if (state == null || !TryGetPool(state, resourceType, out float available))
            {
                return new ResourceSummary(0f, 0f, Array.Empty<ResourceTransactionRecord>());
            }

            List<ResourceTransactionRecord> matching = (state.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
                .Where(r => r != null && string.Equals(r.ResourceType, resourceType, StringComparison.Ordinal))
                .ToList();

            float openReserved = matching
                .Where(r => r.State == ResourceTransactionState.Reserved || r.State == ResourceTransactionState.Running)
                .Sum(r => r.Reserved);

            ResourceTransactionRecord[] recent = matching
                .Skip(Math.Max(0, matching.Count - Math.Max(0, recentCount)))
                .Reverse()
                .ToArray();

            return new ResourceSummary(available, openReserved, recent);
        }

        /// <summary>消费型登记：<paramref name="amount"/> 必须 &gt;= 0（负数拒绝）。只创建
        /// <see cref="ResourceTransactionState.Proposed"/> 记录，不动资源池；真正扣款发生在
        /// <see cref="Reserve"/>。同一 <paramref name="transactionId"/> 重复调用直接返回既有记录
        /// （幂等，不重复登记）。</summary>
        public static LedgerResult ProposeConsume(CampaignState state, string transactionId, string ownerId,
            string resourceType, float amount)
        {
            return Propose(state, transactionId, ownerId, resourceType, amount, isProduce: false);
        }

        /// <summary>生产/奖励型登记：<paramref name="amount"/> 同样必须 &gt;= 0。内部把
        /// <see cref="ResourceTransactionRecord.Requested"/> 存成负数标记"这是一笔待发放而非待扣款
        /// 的账"——这个符号会落盘，读档重放后依然能正确区分两条流水线，调用方不需要理解这个
        /// 内部约定。</summary>
        public static LedgerResult ProposeProduce(CampaignState state, string transactionId, string ownerId,
            string resourceType, float amount)
        {
            return Propose(state, transactionId, ownerId, resourceType, amount, isProduce: true);
        }

        private static LedgerResult Propose(CampaignState state, string transactionId, string ownerId,
            string resourceType, float amount, bool isProduce)
        {
            if (state == null || string.IsNullOrEmpty(transactionId))
            {
                return LedgerResult.Fail("invalid-args");
            }
            if (amount < 0f)
            {
                return LedgerResult.Fail($"negative-amount:{amount}");
            }
            if (!TryGetPool(state, resourceType, out _))
            {
                return LedgerResult.Fail($"unknown-resource:{resourceType}");
            }

            ResourceTransactionRecord existing = Find(state, transactionId);
            if (existing != null)
            {
                // 幂等：已登记过（不论处于什么状态）直接视为成功，不重复创建。
                return LedgerResult.Ok();
            }

            var record = new ResourceTransactionRecord
            {
                TransactionId = transactionId,
                OwnerId = ownerId,
                ResourceType = resourceType,
                Requested = isProduce ? -amount : amount,
                Reserved = 0f,
                Consumed = 0f,
                Refundable = 0f,
                State = ResourceTransactionState.Proposed,
                FailureReason = null,
            };
            AppendRecord(state, record);
            return LedgerResult.Ok();
        }

        /// <summary>Proposed → Reserved。消费型（Requested &gt;= 0）立即从资源池扣款（不足则失败，
        /// 资源池不变）；生产型（Requested &lt; 0）不动资源池。已处于 Reserved/Running/Committed 的
        /// 记录视为幂等成功；已 Cancelled/Failed 的记录拒绝（不允许"复活"一笔已终结的账）。</summary>
        public static LedgerResult Reserve(CampaignState state, string transactionId)
        {
            ResourceTransactionRecord record = Find(state, transactionId);
            if (record == null)
            {
                return LedgerResult.Fail($"not-found:{transactionId}");
            }
            if (record.State == ResourceTransactionState.Reserved
                || record.State == ResourceTransactionState.Running
                || record.State == ResourceTransactionState.Committed)
            {
                return LedgerResult.Ok(); // 幂等重试
            }
            if (record.State != ResourceTransactionState.Proposed)
            {
                return LedgerResult.Fail($"cannot-reserve-from:{record.State}");
            }

            if (record.Requested < 0f)
            {
                // 生产型：不预占资源池，Reserved 保持 0（这也是后续 Commit 用来识别生产型的依据）。
                record.Reserved = 0f;
                record.Refundable = 0f;
                record.State = ResourceTransactionState.Reserved;
                return LedgerResult.Ok();
            }

            if (!TryGetPool(state, record.ResourceType, out float available))
            {
                return LedgerResult.Fail($"unknown-resource:{record.ResourceType}");
            }
            if (available < record.Requested)
            {
                return LedgerResult.Fail($"insufficient:{record.ResourceType}:need={record.Requested}:have={available}");
            }

            TrySetPool(state, record.ResourceType, available - record.Requested);
            record.Reserved = record.Requested;
            record.Refundable = record.Requested;
            record.State = ResourceTransactionState.Reserved;
            return LedgerResult.Ok();
        }

        /// <summary>Reserved → Running（标记"已开工/计时中"，不改变资源池）。幂等：已是
        /// Running/Committed 直接成功。</summary>
        public static LedgerResult MarkRunning(CampaignState state, string transactionId)
        {
            ResourceTransactionRecord record = Find(state, transactionId);
            if (record == null)
            {
                return LedgerResult.Fail($"not-found:{transactionId}");
            }
            if (record.State == ResourceTransactionState.Running || record.State == ResourceTransactionState.Committed)
            {
                return LedgerResult.Ok();
            }
            if (record.State != ResourceTransactionState.Reserved)
            {
                return LedgerResult.Fail($"cannot-run-from:{record.State}");
            }

            record.State = ResourceTransactionState.Running;
            return LedgerResult.Ok();
        }

        /// <summary>Reserved/Running → Committed。消费型（Requested &gt;= 0）：资源池此前已在
        /// Reserve 扣过，这里只做终态确认（Consumed=Reserved，Refundable 清零）。生产型
        /// （Requested &lt; 0）：资源池在这一刻才真正加上 |Requested|。已 Committed 视为幂等成功
        /// （AC-ECO-001："最多产物一次"——不会重复发放）。</summary>
        public static LedgerResult Commit(CampaignState state, string transactionId) => CommitCore(state, transactionId, recordStats: true);

        /// <summary>
        /// FG4-ECO-08 审查修复（FGR-ECO-050）：只是“搬运”的消费型事务（施工取料装上货舱、旧存档施工预留转为现场材料）的确认——
        /// 与 <see cref="Commit"/> 完全相同，只是<b>不</b>记入生产统计：材料此刻只是离开仓库、还没被用掉；
        /// 真正变成建筑 / 传送带格时由施工流程记消耗，取消 / 撤销 / 现场被毁时退回仓库也就无需冲回（统计只计创造与销毁，搬运不计）。
        /// </summary>
        public static LedgerResult CommitTransfer(CampaignState state, string transactionId) => CommitCore(state, transactionId, recordStats: false);

        private static LedgerResult CommitCore(CampaignState state, string transactionId, bool recordStats)
        {
            ResourceTransactionRecord record = Find(state, transactionId);
            if (record == null)
            {
                return LedgerResult.Fail($"not-found:{transactionId}");
            }
            if (record.State == ResourceTransactionState.Committed)
            {
                return LedgerResult.Ok(); // 幂等：不重复发放
            }
            if (record.State != ResourceTransactionState.Reserved && record.State != ResourceTransactionState.Running)
            {
                return LedgerResult.Fail($"cannot-commit-from:{record.State}");
            }

            if (record.Requested < 0f)
            {
                // 生产型：此刻才真正发放。
                if (!TryGetPool(state, record.ResourceType, out float available))
                {
                    return LedgerResult.Fail($"unknown-resource:{record.ResourceType}");
                }
                TrySetPool(state, record.ResourceType, available - record.Requested); // Requested 为负，等价于 +|Requested|
                record.Consumed = record.Requested;
                record.Refundable = 0f;
                // FG4-ECO-08：账本收入（拆解回收、搬运入库……）在发放时记为产出。
                if (recordStats)
                {
                    Economy.ProductionStats.RecordResource(state, record.ResourceType, (long)Math.Round(-record.Requested), produced: true);
                    if (record.ResourceType == ResourceTechData)
                    {
                        // FG5-E2E-01（FGR-RND-070）：技术数据按来源记累计收入（统计面板“研发”页）。
                        Economy.TechDataFlow.OnLedgerCommit(state, record.TransactionId, (long)Math.Round(-record.Requested), produced: true);
                    }
                }
            }
            else
            {
                // 消费型：钱早扣了，这里只确认终态。
                record.Consumed = record.Reserved;
                record.Refundable = 0f;
                // FG4-ECO-08：账本支出（重建、维修、制造……）在确认终态时记为消耗（取消 / 失败全额退回的不计）。
                // 施工取料只是搬运，走 CommitTransfer 不在这里计；施工材料在建成时计（HomeValleyConstruction.RecordBuilt）。
                if (recordStats)
                {
                    Economy.ProductionStats.RecordResource(state, record.ResourceType, (long)Math.Round(record.Reserved), produced: false);
                    if (record.ResourceType == ResourceTechData)
                    {
                        // FG5-E2E-01（FGR-RND-070）：技术数据按去处记累计支出（数据复原、蓝图首次保存……）。
                        Economy.TechDataFlow.OnLedgerCommit(state, record.TransactionId, (long)Math.Round(record.Reserved), produced: false);
                    }
                }
            }

            record.State = ResourceTransactionState.Committed;
            return LedgerResult.Ok();
        }

        /// <summary>取消：Committed 不可取消（拒绝）；其余状态退还 <see cref="ResourceTransactionRecord.Refundable"/>
        /// 到资源池（生产型 Refundable 恒为 0，等价于空退款）后转 Cancelled。幂等：已 Cancelled 直接成功。</summary>
        public static LedgerResult Cancel(CampaignState state, string transactionId)
        {
            return Terminate(state, transactionId, ResourceTransactionState.Cancelled, null);
        }

        /// <summary>失败：语义同 <see cref="Cancel"/>，额外记录 <paramref name="reason"/>。</summary>
        public static LedgerResult Fail(CampaignState state, string transactionId, string reason)
        {
            return Terminate(state, transactionId, ResourceTransactionState.Failed, reason);
        }

        private static LedgerResult Terminate(CampaignState state, string transactionId,
            ResourceTransactionState target, string reason)
        {
            ResourceTransactionRecord record = Find(state, transactionId);
            if (record == null)
            {
                return LedgerResult.Fail($"not-found:{transactionId}");
            }
            if (record.State == target)
            {
                return LedgerResult.Ok(); // 幂等
            }
            if (record.State == ResourceTransactionState.Committed)
            {
                return LedgerResult.Fail("cannot-terminate-committed");
            }

            if (record.Refundable > 0f && TryGetPool(state, record.ResourceType, out float available))
            {
                TrySetPool(state, record.ResourceType, available + record.Refundable);
            }
            record.Refundable = 0f;
            record.State = target;
            record.FailureReason = reason;
            return LedgerResult.Ok();
        }

        /// <summary>
        /// FG4-ECO-07（承接 DEBT-FG3LOG02-09）：已结束的资源事务（已结算 / 取消 / 失败）超过 <paramref name="keep"/> 条（再多留 64 条缓冲）时，按登记顺序清掉最早的那些。
        /// <paramref name="protectedIds"/>（在办工单引用的事务）不清——在办的事务（提议 / 预留 / 进行中）一条不动、相对顺序不变。
        /// 被清掉的只是“很久以前已经结清”的流水：余额在资源池里，不在流水里；HUD 只读最近几条。由派工评估调用（每 0.5 秒或有脏标记），O(事务数)。
        /// </summary>
        public static int PruneHistory(CampaignState state, int keep, ICollection<string> protectedIds)
        {
            ResourceTransactionRecord[] all = state?.ResourceTransactions;
            if (all == null || keep < 0)
            {
                return 0;
            }
            int terminal = 0;
            foreach (ResourceTransactionRecord r in all)
            {
                if (r != null && IsTerminal(r.State))
                {
                    terminal++;
                }
            }
            if (terminal <= keep + 64)
            {
                return 0;
            }
            int toRemove = terminal - keep;
            var kept = new List<ResourceTransactionRecord>(all.Length);
            int removed = 0;
            foreach (ResourceTransactionRecord r in all)
            {
                if (r != null && removed < toRemove && IsTerminal(r.State) && (protectedIds == null || !protectedIds.Contains(r.TransactionId)))
                {
                    removed++;
                    continue;
                }
                kept.Add(r);
            }
            if (removed > 0)
            {
                state.ResourceTransactions = kept.ToArray();
            }
            return removed;
        }

        private static bool IsTerminal(ResourceTransactionState s) =>
            s == ResourceTransactionState.Committed || s == ResourceTransactionState.Cancelled || s == ResourceTransactionState.Failed;

        private static void AppendRecord(CampaignState state, ResourceTransactionRecord record)
        {
            var list = new List<ResourceTransactionRecord>(
                state.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
            {
                record,
            };
            state.ResourceTransactions = list.ToArray();
        }

        private static bool TryGetPool(CampaignState state, string resourceType, out float value)
        {
            switch (resourceType)
            {
                case ResourceScrap:
                    value = state.Scrap;
                    return true;
                case ResourceTechData:
                    value = state.TechData;
                    return true;
                case ResourcePower:
                    value = state.PowerCapacity;
                    return true;
                default:
                    value = 0f;
                    return false;
            }
        }

        private static bool TrySetPool(CampaignState state, string resourceType, float value)
        {
            switch (resourceType)
            {
                case ResourceScrap:
                    state.Scrap = (int)Math.Round(value, MidpointRounding.AwayFromZero);
                    return true;
                case ResourceTechData:
                    state.TechData = (int)Math.Round(value, MidpointRounding.AwayFromZero);
                    return true;
                case ResourcePower:
                    state.PowerCapacity = value;
                    return true;
                default:
                    return false;
            }
        }
    }
}
