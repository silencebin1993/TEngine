using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER4-FAC-01：ERD-FAC-001 装配站生产队列的唯一状态机实现，取代 ER2-SCENE-01 遗留的
    /// "OutputBlocked 建立类型化枚举与配色但无触发路径"空壳（见 <see cref="BuildingPowerState.OutputBlocked"/>
    /// 类注释）。<see cref="FactoryQueueItemRecord"/> 是本类的唯一写入口，不得在别处直接改
    /// <see cref="CampaignState.FactoryQueues"/>。
    ///
    /// ── 单产线 FIFO ──
    /// STORY-EXECUTION-CARDS.md"多订单按稳定队列顺序执行"：装配站面板的"生产"/"改造"两个 Tab
    /// 共用同一条物理产线（同一栋建筑、同一个工位），任意时刻只有队首（按
    /// <see cref="FactoryQueueItemRecord.CreatedTick"/> 最早、非终态）一项真正尝试开工/推进
    /// （<see cref="FindHead"/>），其余 Queued 项原样等待——不需要额外的"当前工位"字段，队首本身
    /// 就是隐式的"当前工位"。
    ///
    /// ── 资源何时真正扣款 ──
    /// 入队时只 <see cref="CampaignEconomyLedger.ProposeConsume"/>（Proposed，不动资源池），真正
    /// <see cref="CampaignEconomyLedger.Reserve"/> 发生在该项成为队首、且装配站确认 Powered 之后——
    /// 与 <see cref="HomeValleyWorkOrders"/>"点选那一刻就扣款"不同，是刻意的：工厂队列允许玩家一次
    /// 排好多项、总成本超过当前废料，让后面的项目自然排队等钱到位，而不是排队时就直接拒绝创建
    /// （那样"队列"这个概念就没有意义了——同一时刻只会存在 0 或 1 项）。<see cref="CampaignEconomyLedger.Reserve"/>
    /// 对已在 Proposed 状态、资源仍不足的记录只返回失败、不改变状态，允许被安全地逐帧重试。
    ///
    /// ── 出口独占 ──
    /// "OutputBlocked 完成体留厂内且只有一个 LogicId"：<see cref="MachineRecord.IsInFactory"/> 是唯一的
    /// 出口占用标记，任意时刻整个归还谷地区域内最多一台机器为 true。队首项的 Progress 到达 Duration
    /// 时，先查是否已有占用者——占用中则转 <see cref="FactoryQueueState.OutputBlocked"/>，**不**在此刻
    /// 生成机器（避免同时并存两个"完成体"）；占用者被玩家实际给出第一条命令后
    /// （<see cref="ReleaseFromFactory"/>，由 <see cref="HomeValleyController"/> 的移动下令入口调用）
    /// 视为"驶出"，出口转空，<see cref="Tick"/> 下一帧即让本项真正生成机器、转 Completed。</summary>
    public static class HomeValleyFactory
    {
        public readonly struct FactoryOpResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly string QueueItemId;

            private FactoryOpResult(bool success, string failureReason, string queueItemId)
            {
                Success = success;
                FailureReason = failureReason;
                QueueItemId = queueItemId;
            }

            public static FactoryOpResult Ok(string queueItemId) => new FactoryOpResult(true, null, queueItemId);
            public static FactoryOpResult Fail(string reason) => new FactoryOpResult(false, reason, null);
        }

        private static readonly FactoryQueueState[] CancellableStates =
        {
            FactoryQueueState.Queued, FactoryQueueState.WaitingResources,
            FactoryQueueState.WaitingPower, FactoryQueueState.Running,
        };

        private static bool IsCancellable(FactoryQueueState s) => CancellableStates.Contains(s);

        private static bool IsHeadCandidate(FactoryQueueState s) =>
            s == FactoryQueueState.Queued || s == FactoryQueueState.WaitingResources
            || s == FactoryQueueState.WaitingPower || s == FactoryQueueState.Running;

        // ── 蓝图播种（首次使用即建，幂等；不等 ER4-BLP-01 正式蓝图编辑器落地才补，同
        // ChassisCatalog.ChassisHoverId"先落数据"先例）──────────────────────────────────

        public static void EnsureBlueprintsSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.BlueprintRecords ??= Array.Empty<BlueprintRecord>();

            foreach (KeyValuePair<string, HomeValleyLayout.ProduceBlueprintDefault> kv in HomeValleyLayout.FactoryProduceDefaults)
            {
                if (state.BlueprintRecords.Any(b => b.BlueprintId == kv.Key))
                {
                    continue;
                }

                var version = new BlueprintVersionRecord
                {
                    Version = 1,
                    ChassisId = kv.Value.ChassisId,
                    PrimaryId = null,
                    UtilityId = null,
                    StructureId = null,
                    OrderedFirmwareIds = Array.Empty<string>(),
                    WorkPriorityTemplate = null,
                    DoctrineId = null,
                    ScrapCost = kv.Value.ScrapCost,
                    PowerCost = 0,
                    BandwidthCost = 0,
                    HeatBudget = 0f,
                    FactionTags = Array.Empty<string>(),
                    CompileSignature = kv.Key + ":v1",
                    CreatedAtPlaySeconds = state.PlaySeconds,
                };
                var record = new BlueprintRecord
                {
                    BlueprintId = kv.Key,
                    DisplayName = kv.Value.DisplayName,
                    ActiveVersion = 1,
                    Archived = false,
                    Versions = new[] { version },
                };
                state.BlueprintRecords = state.BlueprintRecords.Append(record).ToArray();
            }
        }

        // ── 查询 ─────────────────────────────────────────────────────────────────

        public static FactoryQueueItemRecord Find(CampaignState state, string queueItemId)
        {
            return state?.FactoryQueues?.FirstOrDefault(q => q.QueueItemId == queueItemId);
        }

        /// <summary>"已解锁"判定——ERC-003/搬运机是基础蓝图库自带（DEMO-CONTENT-LOCK.md 行53/54"已在
        /// 基础蓝图库中，无需解锁"），维修机需要解析台（AnalysisBench）真正 Powered（行55/
        /// <see cref="Content.ChassisCatalog"/> 维修悬浮条目 LockedHintText）。只在入队那一刻校验——
        /// 已排入队列的项不因之后解析台断电而回溯失效（该项已经真实占了废料/工位）。</summary>
        public static bool IsBlueprintUnlocked(CampaignState state, string blueprintId)
        {
            if (blueprintId == HomeValleyLayout.BlueprintHoverId)
            {
                BuildingRecord bench = state?.BuildingRecords?.FirstOrDefault(b =>
                    b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench);
                return bench != null && bench.PowerState == BuildingPowerState.Powered;
            }
            return blueprintId == HomeValleyLayout.BlueprintErc003Id || blueprintId == HomeValleyLayout.BlueprintHaulerId;
        }

        /// <summary>出口是否被占用——供 UI"出口"状态展示与 <see cref="Tick"/> 自身复用同一条判定。</summary>
        public static bool IsExitBlocked(CampaignState state)
        {
            return MachineRegistry.AllRecords.Any(m =>
                m.RegionId == HomeValleyLayout.RegionId && m.IsAlive && m.IsInFactory);
        }

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        private static void Append(CampaignState state, FactoryQueueItemRecord item)
        {
            state.FactoryQueues = (state.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>()).Append(item).ToArray();
        }

        // ── 生产 ─────────────────────────────────────────────────────────────────

        /// <summary>"生产只接受已解锁且合法蓝图版本"：校验解锁状态+当前 ActiveVersion 存在，把这一刻的
        /// <see cref="BlueprintRecord.ActiveVersion"/> 与其 <see cref="BlueprintVersionRecord.ScrapCost"/>
        /// 锁进本次排队项（<see cref="FactoryQueueItemRecord.BlueprintVersion"/>）——"改蓝图 activeVersion
        /// 不回溯改已排订单的锁定版本"：日后（ER4-BLP-01）玩家把 ActiveVersion 改到 2，本项仍按锁定的
        /// 版本号与当时的成本结算，不会被追溯改动。立即 <see cref="CampaignEconomyLedger.ProposeConsume"/>
        /// （只登记不扣款）；真正 Reserve 延后到该项成为队首，见类注释"资源何时真正扣款"。</summary>
        public static FactoryOpResult TryEnqueueProduce(CampaignState state, string blueprintId)
        {
            if (state == null || string.IsNullOrEmpty(blueprintId))
            {
                return FactoryOpResult.Fail("invalid-args");
            }
            if (!HomeValleyLayout.FactoryProduceDefaults.TryGetValue(blueprintId, out HomeValleyLayout.ProduceBlueprintDefault def))
            {
                return FactoryOpResult.Fail($"no-production-profile:{blueprintId}");
            }

            EnsureBlueprintsSeeded(state);

            if (!IsBlueprintUnlocked(state, blueprintId))
            {
                return FactoryOpResult.Fail($"blueprint-locked:{blueprintId}");
            }

            BlueprintRecord bp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == blueprintId);
            BlueprintVersionRecord version = bp?.Versions?.FirstOrDefault(v => v.Version == bp.ActiveVersion);
            if (bp == null || version == null)
            {
                return FactoryOpResult.Fail($"no-active-version:{blueprintId}");
            }

            string queueItemId = blueprintId + ":produce:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string txId = queueItemId + ":tx";
            CampaignEconomyLedger.ProposeConsume(state, txId, queueItemId, CampaignEconomyLedger.ResourceScrap, version.ScrapCost);

            var item = new FactoryQueueItemRecord
            {
                QueueItemId = queueItemId,
                Kind = FactoryQueueKind.Produce,
                BlueprintId = blueprintId,
                BlueprintVersion = version.Version,
                TargetMachineLogicId = 0,
                TransactionId = txId,
                Duration = def.Seconds,
                Progress = 0f,
                State = FactoryQueueState.Queued,
                BlockedReason = null,
                CreatedTick = NowTick(state),
            };
            Append(state, item);
            return FactoryOpResult.Ok(queueItemId);
        }

        /// <summary>回厂改造——范围裁剪（STORY-EXECUTION-CARDS.md #ER4-FAC-01 明确"生产队列"是本 Story
        /// 范围，改造完整业务流程留 ER4-RETROFIT-01，本 Story 只交付面板"改造"Tab 骨架/明确禁用态）。
        /// DEBT-ER4FAC01-01：承接 ER4-RETROFIT-01，最迟门禁该 Story 开工前，自动测试为该 Story 补齐
        /// 真实改造 E2E。刻意返回明确失败原因而不是静默假装成功，UI 据此显示禁用态而不是空按钮。</summary>
        public static FactoryOpResult TryEnqueueRetrofit(CampaignState state, int targetMachineLogicId, string blueprintId)
        {
            return FactoryOpResult.Fail("not-implemented:ER4-RETROFIT-01");
        }

        // ── 取消 ─────────────────────────────────────────────────────────────────

        /// <summary>AC-ECO-002："取消 Queued 全额释放；取消 Running 只退 refundable；Committed 不可取消"——
        /// 本类的资源事务在 Reserve 之前恒为 Proposed（Refundable=0），Reserve 之后 Refundable=全额
        /// （Commit 只发生在真正完工那一刻，即 <see cref="FactoryQueueState.OutputBlocked"/>/
        /// <see cref="FactoryQueueState.Completed"/>），因此"Running 只退 refundable"在本类语境下恰好
        /// 等于全额——不需要额外的部分消费模型，直接复用 <see cref="CampaignEconomyLedger.Cancel"/>
        /// 即满足要求。OutputBlocked/Completed 已经真正生成了机器/发生了 Commit，不再是可取消状态。</summary>
        public static FactoryOpResult TryCancel(CampaignState state, string queueItemId)
        {
            FactoryQueueItemRecord item = Find(state, queueItemId);
            if (item == null)
            {
                return FactoryOpResult.Fail($"not-found:{queueItemId}");
            }
            if (!IsCancellable(item.State))
            {
                return FactoryOpResult.Fail($"cannot-cancel-from:{item.State}");
            }

            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.Cancel(state, item.TransactionId);
            }
            item.State = FactoryQueueState.Cancelled;
            item.BlockedReason = null;
            return FactoryOpResult.Ok(queueItemId);
        }

        /// <summary>供 <see cref="HomeValleyController"/> 在玩家对一台"仍占用出口"的机器发出第一条真实
        /// 命令（移动/工作单）时调用——等价于"驶出工厂"。非厂内机器/未登记 LogicId 安全 no-op。</summary>
        public static void ReleaseFromFactory(int machineLogicId)
        {
            if (MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord record) && record.IsInFactory)
            {
                record.IsInFactory = false;
            }
        }

        // ── 每帧驱动 ─────────────────────────────────────────────────────────────

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧调用一次。归还谷地工厂队列量级恒定
        /// 个位数，不违反"热更层每帧不得 O(建筑/敌人数)"的性能纪律（同 <see cref="HomeValleyWorkOrders.Tick"/>
        /// 类注释）。"工厂被毁"当前没有真实触发源（装配站没有 RepairProfile、没有任何代码路径能把它
        /// 的 <see cref="BuildingConstructionState"/> 改成 <see cref="BuildingConstructionState.Destroyed"/>
        /// 以外的值），仍按卡片要求实现"不生成幽灵机"的防御性处理并离线验证——诚实范围裁剪，同
        /// <see cref="HomeValleyWorkOrders"/>"机器死亡在归还谷地无真实战斗触发源"先例。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state == null || state.FactoryQueues == null || state.FactoryQueues.Length == 0)
            {
                return;
            }

            BuildingRecord station = state.BuildingRecords?.FirstOrDefault(b =>
                b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation);
            bool stationDestroyed = station == null || station.ConstructionState == BuildingConstructionState.Destroyed;
            if (stationDestroyed)
            {
                foreach (FactoryQueueItemRecord it in state.FactoryQueues)
                {
                    if (!IsCancellable(it.State))
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(it.TransactionId))
                    {
                        CampaignEconomyLedger.Cancel(state, it.TransactionId);
                    }
                    it.State = FactoryQueueState.Failed;
                    it.BlockedReason = "assembly-station-destroyed";
                }
                return; // 工厂没了：不再生成任何新机器，不留幽灵机。
            }

            FactoryQueueItemRecord head = FindHead(state);
            if (head != null)
            {
                TickHead(state, head, dt, station);
            }

            foreach (FactoryQueueItemRecord it in state.FactoryQueues)
            {
                if (it.State == FactoryQueueState.OutputBlocked)
                {
                    TryReleaseOutputBlocked(state, it);
                }
            }
        }

        private static FactoryQueueItemRecord FindHead(CampaignState state)
        {
            FactoryQueueItemRecord head = null;
            foreach (FactoryQueueItemRecord it in state.FactoryQueues)
            {
                if (!IsHeadCandidate(it.State))
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

        private static void TickHead(CampaignState state, FactoryQueueItemRecord item, float dt, BuildingRecord station)
        {
            // 真实 bug（execute_code 实测发现）：HomeValleyPowerGrid.Recompute 的消费者过滤条件是
            // ConstructionState==Operational——玩家用 TryToggleShutdown 把装配站关停（转 Disabled）后，
            // Recompute 的仲裁循环整条跳过它，PowerState 字段停留在关停前最后一次算出的 Powered，不会
            // 被改写。只看 PowerState 会把"主动关停"误判成仍在供电；必须同时确认 ConstructionState
            // 仍是 Operational，才是"这栋楼这一刻真的在正常运转且吃到电"的完整判定。
            bool powered = station.ConstructionState == BuildingConstructionState.Operational
                && station.PowerState == BuildingPowerState.Powered;

            if (item.State == FactoryQueueState.Running)
            {
                if (!powered)
                {
                    // "断电时进度暂停不倒退"：只切显示状态，Progress 原地不动，事务保持 Reserved/Running。
                    item.State = FactoryQueueState.WaitingPower;
                    item.BlockedReason = "assembly-station-unpowered";
                    return;
                }
                item.Progress += dt;
                if (item.Progress >= item.Duration)
                {
                    CompleteProduction(state, item);
                }
                return;
            }

            // Queued / WaitingResources / WaitingPower：尚未开工，逐帧尝试开工（Reserve 对不足资源的
            // Proposed 记录安全可重试，不产生副作用，见类注释）。
            if (!powered)
            {
                item.State = FactoryQueueState.WaitingPower;
                item.BlockedReason = "assembly-station-unpowered";
                return;
            }

            CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, item.TransactionId);
            if (!reserve.Success)
            {
                item.State = FactoryQueueState.WaitingResources;
                item.BlockedReason = reserve.FailureReason;
                return;
            }

            CampaignEconomyLedger.MarkRunning(state, item.TransactionId);
            item.State = FactoryQueueState.Running;
            item.BlockedReason = null;
        }

        private static void CompleteProduction(CampaignState state, FactoryQueueItemRecord item)
        {
            item.Progress = item.Duration; // 钳制：不倒退、不无界累加超过 Duration。

            if (IsExitBlocked(state))
            {
                item.State = FactoryQueueState.OutputBlocked;
                item.BlockedReason = "exit-blocked";
                return; // 不在此刻生成机器——"只有一个 LogicId"，等清障后再登记。
            }

            SpawnProducedMachine(state, item);
        }

        private static void TryReleaseOutputBlocked(CampaignState state, FactoryQueueItemRecord item)
        {
            if (IsExitBlocked(state))
            {
                return;
            }
            SpawnProducedMachine(state, item);
        }

        /// <summary>真正登记机器（<see cref="MachineRegistry.SpawnMachine"/>）+ 结算事务
        /// （<see cref="CampaignEconomyLedger.Commit"/>，只在这一刻真正确认——之前 Reserve 已经扣过款，
        /// 这里只是终态确认，不二次扣款，同 <see cref="HomeValleyWorkOrders.CompleteRepair"/> 先例）。
        /// Spawn 失败（理论上不应发生，Reject-to-Safe 防御）时把队列项转 Failed 并退款，不留半成品。</summary>
        private static void SpawnProducedMachine(CampaignState state, FactoryQueueItemRecord item)
        {
            if (!HomeValleyLayout.FactoryProduceDefaults.TryGetValue(item.BlueprintId, out HomeValleyLayout.ProduceBlueprintDefault def))
            {
                item.State = FactoryQueueState.Failed;
                item.BlockedReason = "unknown-blueprint";
                CampaignEconomyLedger.Cancel(state, item.TransactionId);
                return;
            }

            MachineOpResult spawn = MachineRegistry.SpawnMachine(
                chassisId: def.ChassisId,
                blueprintId: item.BlueprintId,
                regionId: HomeValleyLayout.RegionId,
                position: HomeValleyLayout.AssemblyExit.Position,
                health: 100f,
                maxHealth: 100f);
            if (!spawn.Success)
            {
                item.State = FactoryQueueState.Failed;
                item.BlockedReason = $"spawn-failed:{spawn.Error}";
                CampaignEconomyLedger.Cancel(state, item.TransactionId);
                return;
            }

            if (MachineRegistry.TryGetRecord(spawn.LogicId, out MachineRecord record))
            {
                record.IsInFactory = true; // 占用出口，直到玩家给它第一条真实命令（ReleaseFromFactory）。
            }

            CampaignEconomyLedger.Commit(state, item.TransactionId);
            item.TargetMachineLogicId = spawn.LogicId;
            item.State = FactoryQueueState.Completed;
            item.BlockedReason = null;
        }
    }
}
