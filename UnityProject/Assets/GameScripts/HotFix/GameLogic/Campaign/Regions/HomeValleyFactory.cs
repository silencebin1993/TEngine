using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;

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
    /// 视为"驶出"，出口转空，<see cref="Tick"/> 下一帧即让本项真正生成机器、转 Completed。
    ///
    /// ── FG4-ECO-03：从产线取料（改造而非重写本状态机）──
    /// 新排的生产 / 改造项 <see cref="FactoryQueueItemRecord.MaterialMode"/> = true：入队时锁定材料清单（蓝图的底盘、组件、模块、电子件，
    /// <see cref="AssemblyMaterials.For"/>；改造 = 新旧差额）与废料价，不再在入队时登记废料事务。成为队首、装配站有电时调用
    /// <see cref="AssemblyMaterials.TryTake"/>：先从装配站材料缓存（西侧输入口的传送带送来）、再从仓库（路线通畅时）取，全有或全无；
    /// 缺的按“缺材料时用废料代付”设置用废料补（全部代付 = 废料价，与 Demo 的价格一致），否则停在“缺材料”并写明缺什么、差多少。
    /// 取走的材料记在队列项上：取消 / 装配站被毁 / 出厂失败退回仓库，完工时消耗。旧存档里的队列项（MaterialMode = false）照旧按废料事务结算。
    /// 断电、出口独占、FIFO、改造的目标核验都沿用原来的状态与转移。</summary>
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
            FactoryQueueState.WaitingPower, FactoryQueueState.WaitingTarget, FactoryQueueState.Running,
        };

        private static bool IsCancellable(FactoryQueueState s) => CancellableStates.Contains(s);

        private static bool IsHeadCandidate(FactoryQueueState s) =>
            s == FactoryQueueState.Queued || s == FactoryQueueState.WaitingResources
            || s == FactoryQueueState.WaitingPower || s == FactoryQueueState.WaitingTarget
            || s == FactoryQueueState.Running;

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

            // ER4-PRIM-02：补 ERC-001 默认蓝图 + 把本方法上面刚创建的骨架（PrimaryId/UtilityId/
            // StructureId/OrderedFirmwareIds 全为 null/空、CircuitSlotContentIds 为 null）就地迁入
            // 默认合法电路板，也顺带覆盖旧存档（ER1-SAVE-01～本 Story 上线前创建）缺电路数据的情形——
            // 单一幂等入口，见 BlueprintCircuitDefaults 类注释。
            BlueprintCircuitDefaults.EnsureCircuitDataSeeded(state);
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

        /// <summary>ER8-NEG-01：生产/改造/取消排队失败的玩家文字——此前面板直接显示原因码。</summary>
        public static string DescribeFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "暂时无法排队";
            }
            if (reason.StartsWith("blueprint-locked:", StringComparison.Ordinal))
            {
                return reason.EndsWith(HomeValleyLayout.BlueprintHoverId, StringComparison.Ordinal)
                    ? "悬浮机蓝图未解锁：先让解析台通电运转"
                    : "这张蓝图还没有解锁";
            }
            if (reason.StartsWith("no-production-profile", StringComparison.Ordinal)) return "这张蓝图不能在装配站生产";
            if (reason.StartsWith("no-active-version", StringComparison.Ordinal)) return "这张蓝图还没有保存过可用版本";
            if (reason.StartsWith("not-found:", StringComparison.Ordinal)) return "这一项已经不在队列里";
            if (reason.StartsWith("cannot-cancel-from:", StringComparison.Ordinal)) return "这一项已经完成或出厂，不能取消";
            switch (reason)
            {
                case ReasonMaterials: return GameLogic.Localization.GameText.Get("asm.state.waiting_materials");
                case ReasonRoute: return GameLogic.Localization.GameText.Get("asm.state.waiting_materials");
                case "target-not-alive": return "这台机器已阵亡";
                case "target-not-in-home-valley": return "这台机器不在归还谷地（远征中）";
                case "target-occupying-factory-exit": return "这台机器正停在装配站出口，先让它驶离";
                case "target-directly-controlled": return GameLogic.Localization.GameText.Get("factory.reason.uplinked"); // FG1-HUD-01（FG-GAP-011）
                case "target-busy-with-work-order": return "这台机器正在执行工单，先取消或等它完成";
                case "target-already-queued-for-retrofit": return "这台机器已经在改造队列里";
                case "blueprint-version-not-found": return "所选蓝图版本已不存在";
                case "chassis-mismatch": return "蓝图底盘与这台机器不一致";
                default: return "暂时无法排队";
            }
        }

        /// <summary>出口是否被占用——供 UI"出口"状态展示与 <see cref="Tick"/> 自身复用同一条判定。</summary>
        public static bool IsExitBlocked(CampaignState state)
        {
            return MachineRegistry.AllRecords.Any(m =>
                m.RegionId == HomeValleyLayout.RegionId && m.IsAlive && m.IsInFactory);
        }

        private static long NowTick(CampaignState state) => (long)(state.PlaySeconds * 1000f);

        /// <summary>入队时刻：战役时间毫秒，且严格大于本队列已有的任何一项。暂停中连续排队时战役时间不走，
        /// 此前几项的时刻相同，先后只能靠随机 ID 字符串比较——顺序随机，后排的甚至会插到正在做的那一项前面。
        /// 现在同一时刻排的也按点击先后（ER8-NEG-01 负向自检发现）。</summary>
        private static long NextCreatedTick(CampaignState state)
        {
            long tick = NowTick(state);
            FactoryQueueItemRecord[] queue = state.FactoryQueues;
            if (queue != null)
            {
                foreach (FactoryQueueItemRecord q in queue)
                {
                    if (q != null && q.CreatedTick >= tick)
                    {
                        tick = q.CreatedTick + 1;
                    }
                }
            }
            return tick;
        }

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

            // FG4-ECO-03：按材料结算——入队时锁定这个版本的材料清单与废料价（全部代付 = 废料价），开工时从装配站材料缓存 / 仓库取料（AssemblyMaterials.TryTake）。
            var item = new FactoryQueueItemRecord
            {
                QueueItemId = queueItemId,
                Kind = FactoryQueueKind.Produce,
                BlueprintId = blueprintId,
                BlueprintVersion = version.Version,
                TargetMachineLogicId = 0,
                TransactionId = null,
                Duration = def.Seconds,
                Progress = 0f,
                State = FactoryQueueState.Queued,
                BlockedReason = null,
                CreatedTick = NextCreatedTick(state),
                MaterialMode = true,
                Materials = AssemblyMaterials.For(version),
                ScrapPrice = Math.Max(0, version.ScrapCost),
            };
            Append(state, item);
            return FactoryOpResult.Ok(queueItemId);
        }

        /// <summary>ER4-RETROFIT-01（提前于 ER4-PRIM-05 落地——STORY-BOARD.md #25 的玩家旅程"将 ERC-003
        /// 回厂改造"硬依赖它，领取规则允许把确有必要的前置准备登记为当前 Story 子任务，不改看板顺序；
        /// 待队列正式排到 #27 时该 Story 的验收卡已被本次实现覆盖，届时只需核对/补测）。
        ///
        /// 回厂改造——同一物理机体换一套装配，不是造一台新机：只更新
        /// <see cref="MachineRecord.BlueprintId"/>/<see cref="MachineRecord.BlueprintVersion"/>/
        /// <see cref="MachineRecord.LoadoutSignature"/>，<c>LogicId</c>/编号/HP/伤势/货物/经历/统计原样
        /// 保留。目标蓝图必须与机体当前底盘同一原型（<see cref="ChassisCatalog.ResolveArchetype"/>
        /// 相同）——"回厂改造"换的是脑子不是身体，跨底盘换装不在本 Story 语义内。
        ///
        /// 成本＝正差额且下限 <see cref="RetrofitMinScrapCost"/>：新版本废料成本减目标机当前版本成本，
        /// 降级/平级改造仍收最低工时费，不倒找钱。默认时长 <see cref="RetrofitDurationSeconds"/> 秒。
        ///
        /// 目标资格在入队那一刻校验一次（与 <see cref="IsBlueprintUnlocked"/> 同一"只在入队校验"纪律）：
        /// 存活、在家园区域、未占用出口、未被直控（由调用方——UI/HomeValleyController——解析后传入，本类
        /// 不反向依赖 Controller）、当前没有在办工作单。范围裁剪：归还谷地当前无"可中断/不可中断工作"
        /// 分类系统（该分类属于 ER5-EXP-01），本方法保守地把"有任何在办工作单"一律视为不可改造，不区分
        /// 类型——不会误放行，只会偏保守拒绝，符合 Reject-to-Safe。</summary>
        public const float RetrofitDurationSeconds = 25f;
        public const int RetrofitMinScrapCost = 10;

        public static FactoryOpResult TryEnqueueRetrofit(CampaignState state, int targetMachineLogicId,
            string blueprintId, int blueprintVersion, bool targetIsDirectControlled)
        {
            if (state == null || string.IsNullOrEmpty(blueprintId))
            {
                return FactoryOpResult.Fail("invalid-args");
            }
            if (!MachineRegistry.TryGetRecord(targetMachineLogicId, out MachineRecord machine) || !machine.IsAlive)
            {
                return FactoryOpResult.Fail("target-not-alive");
            }
            if (machine.RegionId != HomeValleyLayout.RegionId)
            {
                return FactoryOpResult.Fail("target-not-in-home-valley");
            }
            if (machine.IsInFactory)
            {
                return FactoryOpResult.Fail("target-occupying-factory-exit");
            }
            if (targetIsDirectControlled)
            {
                return FactoryOpResult.Fail("target-directly-controlled");
            }
            if (!string.IsNullOrEmpty(machine.CurrentWorkOrderId))
            {
                return FactoryOpResult.Fail("target-busy-with-work-order");
            }
            bool alreadyQueuedForRetrofit = state.FactoryQueues != null && state.FactoryQueues.Any(q =>
                q.Kind == FactoryQueueKind.Retrofit && q.TargetMachineLogicId == targetMachineLogicId && IsCancellable(q.State));
            if (alreadyQueuedForRetrofit)
            {
                return FactoryOpResult.Fail("target-already-queued-for-retrofit");
            }

            BlueprintRecord bp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == blueprintId);
            BlueprintVersionRecord newVersion = bp?.Versions?.FirstOrDefault(v => v.Version == blueprintVersion);
            if (newVersion == null)
            {
                return FactoryOpResult.Fail("blueprint-version-not-found");
            }

            string targetArchetype = ChassisCatalog.ResolveArchetype(newVersion.ChassisId) ?? newVersion.ChassisId;
            string currentArchetype = ChassisCatalog.ResolveArchetype(machine.ChassisId) ?? machine.ChassisId;
            if (targetArchetype != currentArchetype)
            {
                return FactoryOpResult.Fail("chassis-mismatch");
            }

            BlueprintRecord currentBp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == machine.BlueprintId);
            BlueprintVersionRecord currentVersion = currentBp?.Versions?.FirstOrDefault(v => v.Version == machine.BlueprintVersion);
            // FG4-ECO-03：改造只补新蓝图比旧蓝图多出来的材料（材料那一份的废料价 = 全部代付的价），其余按工时费收废料；
            // 全部用废料时总价恒为 Demo 的 max(最低改造费, 新旧废料价差额)——见 PriceRetrofit（预览与入队同一函数）。
            RetrofitPrice price = PriceRetrofit(currentVersion, newVersion);
            int cost = price.Fee;

            string queueItemId = blueprintId + ":retrofit:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string txId = cost > 0 ? queueItemId + ":tx" : null;
            if (txId != null)
            {
                CampaignEconomyLedger.ProposeConsume(state, txId, queueItemId, CampaignEconomyLedger.ResourceScrap, cost);
            }

            var item = new FactoryQueueItemRecord
            {
                QueueItemId = queueItemId,
                Kind = FactoryQueueKind.Retrofit,
                BlueprintId = blueprintId,
                BlueprintVersion = blueprintVersion,
                TargetMachineLogicId = targetMachineLogicId,
                TransactionId = txId,
                Duration = RetrofitDurationSeconds,
                Progress = 0f,
                State = FactoryQueueState.Queued,
                BlockedReason = null,
                CreatedTick = NextCreatedTick(state),
                MaterialMode = true,
                Materials = price.Materials,
                ScrapPrice = price.MaterialPrice,
            };
            Append(state, item);
            return FactoryOpResult.Ok(queueItemId);
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
            // FG4-ECO-03：已开工的那一项取走的材料退回仓库、代付的废料退回。
            AssemblyMaterials.Refund(state, item, "cancel");
            item.State = FactoryQueueState.Cancelled;
            item.BlockedReason = null;
            item.Shortfall = null;
            return FactoryOpResult.Ok(queueItemId);
        }

        /// <summary>供 <see cref="HomeValleyController"/> 在玩家对一台"仍占用出口"的机器发出第一条真实
        /// 命令（移动/工作单）时调用——等价于"驶出工厂"。非厂内机器/未登记 LogicId 安全 no-op。</summary>
        public static void ReleaseFromFactory(int machineLogicId)
        {
            if (MachineRegistry.TryGetRecord(machineLogicId, out MachineRecord record) && record.IsInFactory)
            {
                record.IsInFactory = false;
                Combat.CombatSites.SyncFactoryState(record); // FG0-ARCH-03：驶出工厂后才参与自动交战（内核标志）。
            }
        }

        /// <summary>FG4-ECO-03：等待材料（材料不齐、代付关着或废料不够）的原因码；详情在 <see cref="FactoryQueueItemRecord.Shortfall"/>。</summary>
        public const string ReasonMaterials = "assembly-materials";
        /// <summary>FG4-ECO-03：材料在仓库里，但仓库到装配站的路线被堵（DEBT-FG2FW05-01）；详情在 <see cref="FactoryQueueItemRecord.RouteText"/>。</summary>
        public const string ReasonRoute = "assembly-route";

        /// <summary>一次回厂改造的价钱（<see cref="PriceRetrofit"/>）。</summary>
        public struct RetrofitPrice
        {
            /// <summary>要补的材料（新蓝图比旧蓝图多出来的部分；同部位换组件、只换固件时为空）。</summary>
            public ItemStackRecord[] Materials;
            /// <summary>这些材料那一份的废料价（缺料全部代付时付这么多；材料从产线来就不付）。</summary>
            public int MaterialPrice;
            /// <summary>工时费（废料，入队时登记事务）= <see cref="Total"/> − <see cref="MaterialPrice"/>。</summary>
            public int Fee;
            /// <summary>全部用废料时的总价 = max(最低改造费, 新旧废料价差额)，与 Demo 一致。</summary>
            public int Total;
            public int PriceDiff;
        }

        /// <summary>
        /// FG4-ECO-03 回厂改造定价（入队与面板预览共用，不会是两套数字）：
        /// 总价 = max(<see cref="RetrofitMinScrapCost"/>, 新旧废料价差额)；其中材料差额那一份的废料价按“差额材料的废料当量 ÷ 新蓝图全部材料的废料当量 × 新蓝图废料价”折算
        /// （向下取整，不超过差额），其余（同部位换更贵的组件、换固件这类材料表不区分的差价，以及最低工时）都算工时费。
        /// 所以材料从产线来时只省下材料那一份；材料差额为空时整笔按废料收。
        /// </summary>
        public static RetrofitPrice PriceRetrofit(BlueprintVersionRecord from, BlueprintVersionRecord to)
        {
            int toCost = Math.Max(0, to?.ScrapCost ?? 0);
            int priceDiff = Math.Max(0, toCost - Math.Max(0, from?.ScrapCost ?? 0));
            ItemStackRecord[] mats = AssemblyMaterials.RetrofitDiff(from, to);
            int matPrice = AssemblyMaterials.PriceShare(AssemblyMaterials.For(to), mats, toCost, priceDiff);
            int total = Math.Max(RetrofitMinScrapCost, priceDiff);
            return new RetrofitPrice { Materials = mats, MaterialPrice = matPrice, Fee = Math.Max(0, total - matPrice), Total = total, PriceDiff = priceDiff };
        }

        /// <summary>等待材料 / 路线被堵的玩家文字（队列行、详情、“为什么不工作”共用）；不是这两种原因时返回 null（走 <see cref="DescribeFailure"/>）。</summary>
        public static string DescribeWait(FactoryQueueItemRecord item)
        {
            if (item == null)
            {
                return null;
            }
            if (item.BlockedReason == ReasonMaterials)
            {
                return AssemblyMaterials.DescribeShortfall(item);
            }
            if (item.BlockedReason == ReasonRoute)
            {
                return GameLogic.Localization.GameText.Format("asm.reason.route", item.RouteText ?? string.Empty);
            }
            return null;
        }

        private static string _waitSigItem;
        private static long _waitSig;

        /// <summary>
        /// 取料结果依赖的状态的指纹（相同 = 取料结果必然相同，跳过；零分配，O(材料种类)）：本项要的每种材料在装配站缓存 / 仓库里各有多少、
        /// 代付开关与“够不够代付”（废料只看到废料价为止）、路线版本（格网占用、建筑数、地形改写、施工状态，<see cref="Signal.FirmwareLibrary.RouteKey"/>）、废料价、材料表版本。
        /// 只看本项要的材料：产线往仓库交别的货不会让等料的队首每帧重新取料一遍（审查 P2）。
        /// </summary>
        private static long WaitSignature(CampaignState state, FactoryQueueItemRecord item)
        {
            unchecked
            {
                long h = 17;
                ItemStackRecord[] need = item.Materials;
                if (need != null)
                {
                    ItemStackRecord[] buf = state.Economy?.AssemblyBuffer;
                    for (int i = 0; i < need.Length; i++)
                    {
                        string id = need[i]?.ItemId;
                        if (id == null)
                        {
                            continue;
                        }
                        h = h * 31 + Economy.ProductionService.Count(buf, id);
                        h = h * 31 + Economy.HomeInventory.Stock(state, id);
                    }
                }
                bool substitute = AssemblyMaterials.ScrapSubstitute(state);
                h = h * 31 + (substitute ? 1 : 2);
                h = h * 31 + (substitute ? Math.Min(Math.Max(0, state.Scrap), item.ScrapPrice + 1) : 0);
                h = h * 31 + Signal.FirmwareLibrary.RouteKey(state);
                h = h * 31 + item.ScrapPrice;
                h = h * 31 + AssemblyMaterials.Revision;
                return h;
            }
        }

        /// <summary>换战役 / 读档时清掉等待指纹（下一步照常重试一次取料）；由 <see cref="WorldSim.WorldSimulation.ResetSessionState"/> 调用。</summary>
        public static void ResetSessionState()
        {
            _waitSigItem = null;
            _waitSig = 0;
            AssemblyMaterials.ResetSessionState();
        }

        private static void CancelLedger(CampaignState state, FactoryQueueItemRecord item)
        {
            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.Cancel(state, item.TransactionId);
            }
        }

        private static void CommitLedger(CampaignState state, FactoryQueueItemRecord item)
        {
            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.Commit(state, item.TransactionId);
            }
            if (item.MaterialMode)
            {
                AssemblyMaterials.Consume(state, item);
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
                    AssemblyMaterials.Refund(state, it, "destroyed"); // FG4-ECO-03：取走的材料退回仓库（放不下落地），不丢、不复制。
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
            // 改造项在真正开工前重新核验目标——入队与排到队首之间可能经过任意长时间，目标可能已经
            // 阵亡/离开家园区域，此时不能假装继续正常推进。Running 之后不再重复这项检查（同
            // "已排入队列的项不因之后解析台断电而回溯失效"的既有纪律，开工后只受电力门控）。
            if (item.Kind == FactoryQueueKind.Retrofit && item.State != FactoryQueueState.Running)
            {
                if (!MachineRegistry.TryGetRecord(item.TargetMachineLogicId, out MachineRecord target) || !target.IsAlive)
                {
                    item.State = FactoryQueueState.Failed;
                    item.BlockedReason = "target-lost";
                    CancelLedger(state, item);
                    AssemblyMaterials.Refund(state, item, "target-lost");
                    return;
                }
                if (target.RegionId != HomeValleyLayout.RegionId)
                {
                    item.State = FactoryQueueState.WaitingTarget;
                    item.BlockedReason = "target-left-home-valley";
                    return;
                }
            }

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
                    CompleteHeadItem(state, item);
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

            if (item.MaterialMode && item.MaterialsTaken)
            {
                // FG4-ECO-03：已经取过料（开工后断电又来电）：直接接着做，不重复取料。
                item.State = FactoryQueueState.Running;
                item.BlockedReason = null;
                return;
            }

            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.LedgerResult reserve = CampaignEconomyLedger.Reserve(state, item.TransactionId);
                if (!reserve.Success)
                {
                    item.State = FactoryQueueState.WaitingResources;
                    item.BlockedReason = reserve.FailureReason;
                    return;
                }
            }

            if (item.MaterialMode)
            {
                // FG4-ECO-03：从装配站材料缓存 → 仓库取料（全有或全无；路线被堵时不从仓库取），缺的按设置用废料代付。
                // 等待中的项每个模拟步都会来这里：取料涉及的状态（库存版本、废料、代付开关、格网占用、电力）都没变时结果必然相同，直接跳过（零分配）。
                long sig = WaitSignature(state, item);
                if (item.State == FactoryQueueState.WaitingResources && _waitSigItem == item.QueueItemId && _waitSig == sig)
                {
                    return;
                }
                AssemblyMaterials.TakeResult take = AssemblyMaterials.TryTake(state, item, out string routeText);
                if (take != AssemblyMaterials.TakeResult.Taken)
                {
                    string reason = take == AssemblyMaterials.TakeResult.RouteBlocked ? ReasonRoute : ReasonMaterials;
                    bool newlyRouteBlocked = reason == ReasonRoute && item.BlockedReason != ReasonRoute;
                    item.State = FactoryQueueState.WaitingResources;
                    item.BlockedReason = reason;
                    item.RouteText = routeText;
                    if (newlyRouteBlocked)
                    {
                        // DEBT-FG2FW05-01：路线被堵时，队列与通知写同一句原因（可定位到装配站）。
                        Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Denied,
                            Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAssemblyStation), DescribeWait(item));
                    }
                    _waitSigItem = item.QueueItemId;
                    _waitSig = sig;
                    return;
                }
                item.RouteText = null;
                _waitSigItem = null;
            }

            if (!string.IsNullOrEmpty(item.TransactionId))
            {
                CampaignEconomyLedger.MarkRunning(state, item.TransactionId);
            }
            item.State = FactoryQueueState.Running;
            item.BlockedReason = null;
        }

        /// <summary>队首完工分发：Produce 生成新机需要占用唯一出口，走既有 OutputBlocked 门控；
        /// Retrofit 改的是已经在场的机体本身，没有"新物体要驶出"这回事——目标机全程留在原地，不占用
        /// <see cref="MachineRecord.IsInFactory"/> 出口标记，因此不经过 <see cref="IsExitBlocked"/> 门控，
        /// 直接完成。这是刻意的范围简化（不模拟"机体实际行驶到装配站"的位移/寻路），已在
        /// <see cref="TryEnqueueRetrofit"/> 类注释登记。</summary>
        private static void CompleteHeadItem(CampaignState state, FactoryQueueItemRecord item)
        {
            item.Progress = item.Duration; // 钳制：不倒退、不无界累加超过 Duration。

            if (item.Kind == FactoryQueueKind.Retrofit)
            {
                CompleteRetrofit(state, item);
                return;
            }

            if (IsExitBlocked(state))
            {
                item.State = FactoryQueueState.OutputBlocked;
                item.BlockedReason = "exit-blocked";
                return; // 不在此刻生成机器——"只有一个 LogicId"，等清障后再登记。
            }

            SpawnProducedMachine(state, item);
        }

        /// <summary>回厂改造完工：只更新 <see cref="MachineRecord.BlueprintId"/>/
        /// <see cref="MachineRecord.BlueprintVersion"/>/<see cref="MachineRecord.LoadoutSignature"/>，
        /// LogicId/编号/HP/伤势/货物/经历/统计原样保留——AC-MCH-001"经历/统计不因改造丢失或重置"、
        /// AC-BLP-003"新版本不改变旧机，回厂改造后才更新旧机"在这里同时兑现：旧版本记录本身从未被
        /// 改写（只是这台机不再引用它），仍可通过 <see cref="BlueprintEditorService.FindActiveVersion"/>
        /// 之外的历史版本查询正常解析（AC-BLP-004"被机器引用的版本不可删除"天然满足，本类从不删除
        /// 任何 <see cref="BlueprintVersionRecord"/>）。目标已不在场/已阵亡/版本已不可解析（理论上不
        /// 应发生，TickHead 已在开工前核验过一次，这里是完工时的第二道防御）时转 Failed 并退款。</summary>
        private static void CompleteRetrofit(CampaignState state, FactoryQueueItemRecord item)
        {
            if (!MachineRegistry.TryGetRecord(item.TargetMachineLogicId, out MachineRecord machine) || !machine.IsAlive)
            {
                item.State = FactoryQueueState.Failed;
                item.BlockedReason = "target-lost";
                CancelLedger(state, item);
                AssemblyMaterials.Refund(state, item, "target-lost");
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure,
                    Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAssemblyStation), "改造失败：目标机器已不在场，已退还材料");
                return;
            }

            BlueprintRecord bp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == item.BlueprintId);
            BlueprintVersionRecord version = bp?.Versions?.FirstOrDefault(v => v.Version == item.BlueprintVersion);
            if (version == null)
            {
                item.State = FactoryQueueState.Failed;
                item.BlockedReason = "blueprint-version-missing";
                CancelLedger(state, item);
                AssemblyMaterials.Refund(state, item, "version-missing");
                return;
            }

            machine.BlueprintId = item.BlueprintId;
            machine.BlueprintVersion = item.BlueprintVersion;
            machine.LoadoutSignature = version.CompileSignature;
            MachineRegistry.NotifyLoadoutChanged(machine); // FG0-ARCH-03：核心门三灯等按名册版本号重算。

            CircuitOpResult loadoutRegister = MachineLoadoutRegistry.Register(state, machine.LogicId, item.BlueprintId, item.BlueprintVersion);
            if (!loadoutRegister.Success)
            {
                TEngine.Log.Warning($"[HomeValleyFactory] 机器 {machine.LogicId} 改造后装配登记失败：{loadoutRegister.Message}");
            }

            CommitLedger(state, item);
            item.State = FactoryQueueState.Completed;
            item.BlockedReason = null;

            // ER8-CONTENT-01 AC-AUD-001 生产：改造完工与新机出厂同属“生产”类反馈。
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.ProductionComplete,
                Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAssemblyStation),
                $"{Feedback.FeedbackCues.MachineLabel(machine.LogicId)} 改造完成，已换装 v{item.BlueprintVersion}",
                Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeAssemblyStation));

            // ER6-LOOP-01：回厂改造完工是 OBJ-06"ERC-003 正式回厂改造完成"子条件的唯一真实写入口。
            CampaignObjectiveTracker.Recompute(state);
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
                CancelLedger(state, item);
                AssemblyMaterials.Refund(state, item, "unknown-blueprint");
                return;
            }

            // ER4-BLP-02：把这次生产锁定的版本号+其真实 CompileSignature 一起写进新机记录——
            // 此前 SpawnMachine 对 BlueprintVersion/LoadoutSignature 恒写死 1/""，与本类"锁定生产那一刻
            // ActiveVersion"的既有设计（item.BlueprintVersion）完全脱节。找不到对应版本记录（理论上不应
            // 发生，防御性兜底）时签名留空，不臆造一个假签名。
            string loadoutSignature = null;
            BlueprintRecord producedBp = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == item.BlueprintId);
            BlueprintVersionRecord producedVersion = producedBp?.Versions?.FirstOrDefault(v => v.Version == item.BlueprintVersion);
            if (producedVersion != null)
            {
                loadoutSignature = producedVersion.CompileSignature;
            }

            MachineOpResult spawn = MachineRegistry.SpawnMachine(
                chassisId: def.ChassisId,
                blueprintId: item.BlueprintId,
                regionId: HomeValleyLayout.RegionId,
                // FG3-LOG-01（DEBT-FG0ARCH04-03）：出口随装配站旋转 / 搬迁——出厂位置与端口箭头、放置障碍同一个来源。
                position: Grid.HomeGridService.ExitPosition(state, "assembly_exit"),
                health: 100f,
                maxHealth: 100f,
                blueprintVersion: item.BlueprintVersion,
                loadoutSignature: loadoutSignature);
            if (!spawn.Success)
            {
                item.State = FactoryQueueState.Failed;
                item.BlockedReason = $"spawn-failed:{spawn.Error}";
                CancelLedger(state, item);
                AssemblyMaterials.Refund(state, item, "spawn-failed");
                Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.Failure,
                    Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAssemblyStation), "生产失败，已退还材料");
                return;
            }

            if (MachineRegistry.TryGetRecord(spawn.LogicId, out MachineRecord record))
            {
                record.IsInFactory = true; // 占用出口，直到玩家给它第一条真实命令（ReleaseFromFactory）。
                Combat.CombatSites.SyncFactoryState(record); // FG0-ARCH-03：厂内机器不参与自动交战、不消耗交战冷却（内核标志）。
            }
            // ER4-MCH-01：真正"生产"出来的机器才记这个经历标记——开局自带的 ERC-001/002 从不走这条
            // 分支，天然不会获得它，正确反映"它们不是被生产出来的"。
            MachineRegistry.TryMarkExperience(spawn.LogicId, MachineExperienceFlags.Produced);

            // FG1-SIG-06（FGR-SIG-070）：Demo“生产重型机 +8”改为“高功率生产”——按家园总用电需求、按游戏时间累计
            // （CampaignExposureLedger.SimStepHighPower，每游戏小时最多 +3），不再按生产出来的单台机器计。

            // ER4-BLP-02 STORY-EXECUTION-CARDS.md 第3条："工厂出厂……时登记 MachineLoadoutRegistry"。
            // 登记失败（理论上不应发生，producedVersion 已在上面确认存在）只记警告，不回滚已完成的生产——
            // 机器本身已经真实登记且真实扣款，装配解析失败属于"这台机战斗时打不出东西"的可见故障，
            // 不应该反过来让整次生产失败。
            CircuitOpResult loadoutRegister = MachineLoadoutRegistry.Register(state, spawn.LogicId, item.BlueprintId, item.BlueprintVersion);
            if (!loadoutRegister.Success)
            {
                TEngine.Log.Warning($"[HomeValleyFactory] 机器 {spawn.LogicId} 装配登记失败：{loadoutRegister.Message}");
            }

            bool fromLine = item.MaterialMode && item.SubstituteScrap <= 0;
            CommitLedger(state, item);
            item.TargetMachineLogicId = spawn.LogicId;
            item.State = FactoryQueueState.Completed;
            item.BlockedReason = null;
            if (fromLine)
            {
                // FG4-ECO-03：第一次完全用产线材料（没有废料代付）造出机器——图鉴“装配站”解锁 + 引导钩子（内容在 FG15-UX-04）。
                Core.GuidanceHooks.Raise(Core.GuidanceHooks.EconomyAssemblyFirstLineMachine);
            }

            // ER8-CONTENT-01 AC-AUD-001 生产：出厂那一刻（唯一完成点）出声与字幕，音色取装配站
            // BuildingCatalog.SfxId。
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.ProductionComplete,
                Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAssemblyStation),
                $"{Feedback.FeedbackCues.MachineLabel(spawn.LogicId)} {MechanicalContentFacade.ResolveChassisLabel(def.ChassisId)} 已出厂",
                Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeAssemblyStation));
        }
    }
}
