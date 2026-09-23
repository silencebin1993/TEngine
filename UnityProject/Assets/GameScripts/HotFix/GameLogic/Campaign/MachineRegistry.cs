using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>ER1-ID-01：MachineRegistry 拒绝写入时的分类，供调用方与测试断言区分失败原因，
    /// 不是异常——本类全部公开方法 Reject-to-Safe，永不抛出（与 <see cref="CampaignSaveService"/>
    /// 同一条纪律）。</summary>
    public enum MachineRegistryError
    {
        None = 0,
        /// <summary>ChassisId 为空——不知道这是什么机器，拒绝登记。</summary>
        MissingChassis,
        /// <summary>BlueprintId 为空——ERD-DAT-002 要求机器引用蓝图；当前内容锁定表尚无正式蓝图
        /// （ER4-BLP-01 未开工）时调用方应传入显式占位值而不是空串，见
        /// <see cref="CellStageMachineBridge"/> 的用法。</summary>
        MissingBlueprint,
        /// <summary>读档时遇到 LogicId&lt;=0 / ChassisId 缺失等无法安全使用的记录。</summary>
        CorruptRecord,
        /// <summary>同一 LogicId 出现了第二条记录（多半是存档被手改或旧迁移 bug）。</summary>
        DuplicateLogicId,
        /// <summary>目标 LogicId 从未分配过。</summary>
        UnknownLogicId,
        /// <summary>目标机器已阵亡——阵亡机器不得再绑定实体/复活（AC-MCH-002）。</summary>
        TargetNotAlive,
        /// <summary>目标实体已经绑定着另一个 LogicId，拒绝二次绑定造成一实体多归属。</summary>
        EntityAlreadyBound,
        /// <summary>机器记录所属区域与当前绑定的 SimWorld 区域不一致——区域尚未加载，
        /// 不能把它当"在场"实体对待。</summary>
        RegionNotLoaded,
    }

    /// <summary>MachineRegistry 写操作的统一返回值。<see cref="Success"/> 为假时看
    /// <see cref="Error"/>/<see cref="Message"/>，调用方不得因为异常而中断流程——本类不抛异常。</summary>
    public readonly struct MachineOpResult
    {
        public readonly bool Success;
        public readonly MachineRegistryError Error;
        public readonly string Message;
        public readonly int LogicId;

        private MachineOpResult(bool success, MachineRegistryError error, string message, int logicId)
        {
            Success = success;
            Error = error;
            Message = message;
            LogicId = logicId;
        }

        public static MachineOpResult Ok(int logicId, string message = null) =>
            new MachineOpResult(true, MachineRegistryError.None, message, logicId);

        public static MachineOpResult Fail(MachineRegistryError error, string message, int logicId = 0) =>
            new MachineOpResult(false, error, message, logicId);
    }

    /// <summary>
    /// ER1-ID-01：唯一 MachineRegistry——跨局机器稳定身份的单一入口。
    ///
    /// ── 两套"LogicId"不要混淆 ──
    /// <see cref="BinGames.Sim.SimWorld"/>/<see cref="SimBridge.NextLogicId"/> 早就有一个 int
    /// "LogicId"概念，但那是**局内**分配器：每次 <c>SimBridge.Begin</c> 都从 1 重新计数
    /// （<c>SimBridge._nextLogicId = 1</c>），只用于"Spawn 入队到下一次 Step 落地实体之前"的
    /// 关联键和伤害归因（<c>SourceLogicId</c>/<c>KillSignal.LogicId</c>），**不跨局、不落盘**。
    /// 本类的 <see cref="MachineRecord.LogicId"/> 是完全独立的第二个号段：**战役全局**分配，
    /// 从新建战役起单调递增、永不复用、落盘，与 SimBridge 的局内计数器数值上毫无关系。
    /// 两者唯一的接口是本类的 <see cref="CellStageSpawnHandle"/> 挂起表——借用局内 LogicId
    /// 当"Spawn 请求 → 下一次 Step 后落地成 SimEntityId"这段延迟期间的关联键，与
    /// <see cref="Control.UnitLoadoutRegistry.RegisterArchetypePending"/> 同一范式。
    ///
    /// ── 映射结构 ──
    /// <see cref="MachineRecord.LogicId"/>（稳定、落盘）↔ 当前 <see cref="SimEntityId"/>
    /// （易变、只在当前 SimWorld 会话内有效、绝不落盘）。Spawn / Despawn / 区域卸载 / 阵亡 /
    /// 读档恢复全部走本类，不允许任何调用方直接维护第二份映射。
    ///
    /// ── 生命周期 ──
    /// <see cref="Bind"/> 在每次 <c>CellStageFlow.Enter</c>（或未来任何会生成/操控机器的正式入口）
    /// 建立 SimWorld 会话时调用；<see cref="Unbind"/> 在会话结束时调用——只清空 LogicId↔SimEntityId
    /// 映射（等价于"区域卸载"：机器记录留着，只是当前没有对应实体），<b>绝不清空 <see cref="_records"/></b>，
    /// 那是跨局身份的唯一真相。真正开始新战役时才调用 <see cref="ResetForNewCampaign"/>。
    ///
    /// ── Reject-to-Safe ──
    /// 全部公开方法不抛异常；非法输入（重复 LogicId、缺失蓝图、半损坏记录、目标已阵亡、
    /// 跨区无实体、实体重复绑定）一律返回失败结果，不污染已有状态。
    /// </summary>
    public static class MachineRegistry
    {
        private struct CellStageSpawnHandle
        {
            /// <summary>本类分配的战役级 LogicId（落盘用）。</summary>
            public int MachineLogicId;
            /// <summary>SimBridge 局内 LogicId（<see cref="SimBridge.NextLogicId"/> 的返回值），
            /// 用来在下一次 Step 后从快照里找到落地的 <see cref="SimEntityId"/>。</summary>
            public int SessionLogicId;
            public int Attempts;
        }

        /// <summary>挂起解析的重试上限，与 <see cref="Control.UnitLoadoutRegistry.MaxResolveAttempts"/>
        /// 同一理由：出生即死的项不该被无限扫下去。</summary>
        private const int MaxResolveAttempts = 120;

        private static int _nextLogicId = 1;
        private static int _nextDisplayNumber = 1;

        /// <summary>本战役分配过的全部 LogicId（含已阵亡）。用于保证"永不复用"，
        /// 独立于 <see cref="_records"/> 是为了让读档时即便记录本身被判半损坏拒绝，
        /// 它占用过的号也不会被后续分配重新发出（读档路径见 <see cref="LoadFromCampaignState"/>）。</summary>
        private static readonly HashSet<int> _everAllocated = new HashSet<int>();

        private static readonly Dictionary<int, MachineRecord> _records = new Dictionary<int, MachineRecord>();
        private static readonly Dictionary<int, SimEntityId> _logicToEntity = new Dictionary<int, SimEntityId>();
        private static readonly Dictionary<ulong, int> _entityToLogic = new Dictionary<ulong, int>();
        private static readonly List<CellStageSpawnHandle> _pending = new List<CellStageSpawnHandle>(4);

        private static SimBridge _sim;
        private static string _activeRegionId;
        private static SignalScope _scope;

        public static int RecordCount => _records.Count;
        public static int PendingCount => _pending.Count;
        public static bool IsBound => _sim != null;
        public static IReadOnlyCollection<MachineRecord> AllRecords => _records.Values;

        // ── 会话生命周期 ─────────────────────────────────────

        /// <summary>绑定到当前 SimWorld 会话；<paramref name="regionId"/> 是本会话代表的区域
        /// （ER2/ER5 区域系统尚未建立时用固定占位，如 "cell-stage"）。订阅
        /// <see cref="AllyDeathSignal"/>：友方个体真实阵亡时自动 <see cref="MarkDeadByEntity"/>，
        /// 不需要各个 Spawn 调用方手动查询死亡。</summary>
        public static void Bind(SimBridge sim, string regionId)
        {
            _sim = sim;
            _activeRegionId = regionId;
            _scope?.Dispose();
            _scope = new SignalScope().On<AllyDeathSignal>(OnAllyDeath);
            _pending.Clear();
        }

        /// <summary>解绑当前会话——等价于"区域卸载"：只清空 LogicId↔SimEntityId 映射，
        /// <see cref="_records"/> 原样保留（跨区暂时无实体，不是记录消失）。</summary>
        public static void Unbind()
        {
            _scope?.Dispose();
            _scope = null;
            _sim = null;
            _activeRegionId = null;
            _pending.Clear();
            _logicToEntity.Clear();
            _entityToLogic.Clear();
        }

        /// <summary>只清空 <paramref name="regionId"/> 区域内机器的实体映射，记录保留。
        /// 供未来多区域（ER5-REGION-01）在不整体 Unbind 的情况下卸载单个区域时调用；
        /// 当前唯一入口（细胞阶段）只有一个区域，暂未被生产代码调用，先立好机制。</summary>
        public static void UnloadRegion(string regionId)
        {
            if (string.IsNullOrEmpty(regionId))
            {
                return;
            }

            foreach (KeyValuePair<int, MachineRecord> kv in _records)
            {
                if (kv.Value.RegionId != regionId)
                {
                    continue;
                }

                if (_logicToEntity.TryGetValue(kv.Key, out SimEntityId entity))
                {
                    _entityToLogic.Remove(entity.Value);
                    _logicToEntity.Remove(kv.Key);
                }
            }
        }

        /// <summary>新建战役：连记录一起清空，回到全新分配器状态。读取已有战役请走
        /// <see cref="LoadFromCampaignState"/>（它内部也会先调用本方法，不需要调用方重复调用）。</summary>
        public static void ResetForNewCampaign()
        {
            Unbind();
            _records.Clear();
            _everAllocated.Clear();
            _nextLogicId = 1;
            _nextDisplayNumber = 1;
        }

        // ── 分配 / 登记 ──────────────────────────────────────

        private static int AllocateLogicId()
        {
            int id;
            do
            {
                id = _nextLogicId++;
            } while (!_everAllocated.Add(id));

            return id;
        }

        /// <summary>登记一台新机器（Spawn）。只写 <see cref="MachineRecord"/>，不绑定实体——
        /// 调用方随后必须用 <see cref="ArmPendingBind"/>（细胞阶段这类"Spawn 入队、下一帧才有实体"
        /// 的入口）或直接 <see cref="BindEntity"/>（实体已存在时）完成绑定。
        /// Reject-to-Safe：<paramref name="chassisId"/>/<paramref name="blueprintId"/> 任一为空
        /// 都拒绝登记，不产生半成品记录。当前唯一真实调用方是
        /// <c>CellStageFlow.SpawnControlAllies</c>（细胞阶段友军桥接）；ER4-BLP-01 建立正式蓝图目录后，
        /// 其余正式生成入口（工厂/回厂等）改传真实 <c>blueprintId</c> 即可，本方法签名不需要变。
        ///
        /// ER4-BLP-02：新增 <paramref name="blueprintVersion"/>/<paramref name="loadoutSignature"/> 可选
        /// 参数（默认值 1/空串与此前硬编码行为逐字相同，不改变任何既有调用方的既有语义）——"同一
        /// BlueprintVersion 在生产……中使用同一装配签名"（STORY-EXECUTION-CARDS.md ER4-BLP-02 第2条）
        /// 此前对所有生产入口恒为假：无论蓝图实际保存到第几版，新机永远写死 <c>BlueprintVersion=1</c>、
        /// <c>LoadoutSignature=""</c>。真实生产入口（<c>HomeValleyFactory.SpawnProducedMachine</c>/
        /// <c>HomeValleyController.EnsureMachinesSeeded</c>）改传调用方已经解析出的真实版本号与
        /// <see cref="GameLogic.Campaign.Blueprint.BlueprintVersionRecord.CompileSignature"/>；细胞阶段/
        /// 紧急机等无真实蓝图版本概念的调用方不传，维持原样。</summary>
        public static MachineOpResult SpawnMachine(string chassisId, string blueprintId, string regionId,
            Vector2 position, float health, float maxHealth, string factionId = "Player",
            int blueprintVersion = 1, string loadoutSignature = null)
        {
            if (string.IsNullOrEmpty(chassisId))
            {
                return MachineOpResult.Fail(MachineRegistryError.MissingChassis, "ChassisId 为空，拒绝登记新机器。");
            }
            if (string.IsNullOrEmpty(blueprintId))
            {
                return MachineOpResult.Fail(MachineRegistryError.MissingBlueprint, "BlueprintId 为空，拒绝登记新机器。");
            }

            int logicId = AllocateLogicId();
            int displayNumber = _nextDisplayNumber++;
            var record = new MachineRecord
            {
                LogicId = logicId,
                DisplayNumber = displayNumber,
                ChassisId = chassisId,
                BlueprintId = blueprintId,
                BlueprintVersion = blueprintVersion,
                LoadoutSignature = loadoutSignature ?? string.Empty,
                FactionId = factionId,
                RegionId = regionId,
                WorldPosition = position,
                Health = health,
                MaxHealth = maxHealth,
                InjuryFlags = Array.Empty<string>(),
                Battery = 0f,
                CurrentWorkOrderId = null,
                Cargo = Array.Empty<CargoEntry>(),
                DoctrineId = null,
                WorkPriorities = WorkPriorities.Default(),
                ExperienceFlags = Array.Empty<string>(),
                KillCount = 0,
                JobsCompleted = 0,
                ExpeditionsCompleted = 0,
                TimesControlled = 0,
                IsAlive = true,
                IsInFactory = false,
                IsDeployed = true,
            };

            _records[logicId] = record;
            return MachineOpResult.Ok(logicId, $"已登记机器 LogicId={logicId} DisplayNumber={displayNumber}");
        }

        /// <summary>细胞阶段这类"Spawn 只是入队，实体要下一次 Step 才存在"的入口用这个：
        /// 记一条挂起项，靠 <paramref name="sessionLogicId"/>（<see cref="SimBridge.NextLogicId"/>
        /// 的返回值）在 <see cref="ResolvePending"/> 里从快照找到落地的 <see cref="SimEntityId"/>。</summary>
        public static void ArmPendingBind(int machineLogicId, int sessionLogicId)
        {
            if (!_records.ContainsKey(machineLogicId))
            {
                return;
            }

            _pending.Add(new CellStageSpawnHandle
            {
                MachineLogicId = machineLogicId,
                SessionLogicId = sessionLogicId,
                Attempts = 0,
            });
        }

        /// <summary>每帧驱动，挂起表为空时一行都不扫（与 <c>UnitLoadoutRegistry.ResolvePending</c>
        /// 同一约定，不违反"热更层每帧与敌人数无关"）。返回本次解析成功的条数。</summary>
        public static int ResolvePending(in SimSnapshot snapshot)
        {
            if (_pending.Count == 0)
            {
                return 0;
            }

            int resolved = 0;
            for (int p = _pending.Count - 1; p >= 0; p--)
            {
                CellStageSpawnHandle pending = _pending[p];
                SimEntityId found = SimEntityId.None;

                if (snapshot.LogicId.IsCreated && snapshot.EntityId.IsCreated)
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (snapshot.Alive[i] != 0 && snapshot.LogicId[i] == pending.SessionLogicId)
                        {
                            found = snapshot.EntityId[i];
                            break;
                        }
                    }
                }

                if (found.IsValid)
                {
                    BindEntity(pending.MachineLogicId, found);
                    _pending.RemoveAt(p);
                    resolved++;
                    continue;
                }

                pending.Attempts++;
                if (pending.Attempts >= MaxResolveAttempts)
                {
                    _pending.RemoveAt(p);
                }
                else
                {
                    _pending[p] = pending;
                }
            }

            return resolved;
        }

        /// <summary>把一个已经存在的 <see cref="SimEntityId"/> 绑定到某个 LogicId。
        /// Reject-to-Safe 覆盖：未知 LogicId、目标已阵亡、区域未加载、实体已绑定别的 LogicId。</summary>
        public static MachineOpResult BindEntity(int logicId, SimEntityId entity)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record))
            {
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, $"LogicId {logicId} 未登记。", logicId);
            }
            if (!record.IsAlive)
            {
                return MachineOpResult.Fail(MachineRegistryError.TargetNotAlive,
                    $"LogicId {logicId} 已阵亡，拒绝把新实体套到旧机器身份上。", logicId);
            }
            if (!entity.IsValid)
            {
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, "空实体，拒绝绑定。", logicId);
            }
            if (!string.IsNullOrEmpty(record.RegionId) && !string.IsNullOrEmpty(_activeRegionId) &&
                record.RegionId != _activeRegionId)
            {
                return MachineOpResult.Fail(MachineRegistryError.RegionNotLoaded,
                    $"LogicId {logicId} 属于区域 {record.RegionId}，当前会话区域是 {_activeRegionId}，拒绝绑定。", logicId);
            }
            if (_entityToLogic.TryGetValue(entity.Value, out int existingLogic) && existingLogic != logicId)
            {
                return MachineOpResult.Fail(MachineRegistryError.EntityAlreadyBound,
                    $"实体已绑定到 LogicId {existingLogic}，拒绝把 LogicId {logicId} 重复绑定到同一实体。", logicId);
            }

            // 旧映射（若有）先清掉，防止同一 LogicId 换绑新实体时留下悬挂的反向条目。
            if (_logicToEntity.TryGetValue(logicId, out SimEntityId oldEntity) && oldEntity != entity)
            {
                _entityToLogic.Remove(oldEntity.Value);
            }

            _logicToEntity[logicId] = entity;
            _entityToLogic[entity.Value] = logicId;
            record.RegionId = _activeRegionId ?? record.RegionId;
            record.IsDeployed = true;
            return MachineOpResult.Ok(logicId);
        }

        /// <summary>ER1-SAVE-02：在挂起解析窗口内，把战役级 LogicId（<see cref="MachineRecord.LogicId"/>，
        /// 落盘、跨局稳定）翻译成本次会话的 SimBridge 局内 LogicId（<see cref="ArmPendingBind"/> 记录的
        /// <c>SessionLogicId</c>，每次 <c>SimBridge.Begin()</c> 重新从 1 计数，绝不落盘）。
        ///
        /// 只在"已 <see cref="ArmPendingBind"/>、尚未被 <see cref="ResolvePending"/> 消费掉"的窗口内
        /// 有效——这正是控制恢复唯一需要它的时机：<c>CellStageFlow.SetupSim</c> 在
        /// <c>SpawnControlAllies()</c> 之后、<c>Update()</c> 第一次驱动 <see cref="ResolvePending"/>
        /// 之前，需要把 <c>CampaignState.ControlHandoff.ControlledLogicId</c>（战役级）转换成
        /// <c>ControlHandoffState.ControlledLogicId</c>（局内，<see cref="SimBridge.RequestControlRestore"/>
        /// 认的号段）——两套号段互不相通（见类注释"两套 LogicId 不要混淆"），绝不能把战役级号直接
        /// 塞给只认局内号段的 API，否则会在两个号段恰好都很小时把控制权错误地套到无关单位身上。</summary>
        public static bool TryGetPendingSessionLogicId(int machineLogicId, out int sessionLogicId)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].MachineLogicId == machineLogicId)
                {
                    sessionLogicId = _pending[i].SessionLogicId;
                    return true;
                }
            }

            sessionLogicId = 0;
            return false;
        }

        /// <summary>ER1-SAVE-02：读档恢复后的正式 Spawn 阶段用——找一条"已存在、存活、属于目标区域、
        /// 尚未绑定实体"的记录复用其 LogicId，而不是无条件 <see cref="SpawnMachine"/> 分配新号。
        /// 没有这一步的话，每次 Resume 都会给同一具"逻辑上应该是同一台机器"的友军分配一个新战役级
        /// LogicId，读档带回来的 <see cref="CampaignState.ControlHandoff"/>/经历统计全部对不上号——
        /// 旧记录变成一条查无实体的"孤儿"，新记录又是一份没有历史的"新机器"。
        /// 按 <paramref name="chassisId"/> + <paramref name="regionId"/> 精确匹配；多条候选时取
        /// LogicId 最小的一条（最早分配、行为稳定可预期）。找不到时返回 false，调用方应退回正常
        /// <see cref="SpawnMachine"/> 路径（覆盖"新战役第一次进场，还没有任何既有记录"的情形）。</summary>
        public static bool TryFindReusableRecord(string chassisId, string regionId, out int logicId)
        {
            logicId = 0;
            int best = 0;
            foreach (KeyValuePair<int, MachineRecord> kv in _records)
            {
                MachineRecord r = kv.Value;
                if (!r.IsAlive || r.ChassisId != chassisId || r.RegionId != regionId)
                {
                    continue;
                }
                if (_logicToEntity.ContainsKey(kv.Key))
                {
                    continue; // 已经绑定着实体，不是"等待重新落地"的那条。
                }
                if (best == 0 || kv.Key < best)
                {
                    best = kv.Key;
                }
            }

            if (best == 0)
            {
                return false;
            }

            logicId = best;
            return true;
        }

        // ── 查询 ─────────────────────────────────────────────

        public static bool TryGetEntity(int logicId, out SimEntityId entity) =>
            _logicToEntity.TryGetValue(logicId, out entity);

        public static bool TryGetLogicId(SimEntityId entity, out int logicId)
        {
            if (!entity.IsValid)
            {
                logicId = 0;
                return false;
            }
            return _entityToLogic.TryGetValue(entity.Value, out logicId);
        }

        public static bool TryGetRecord(int logicId, out MachineRecord record) =>
            _records.TryGetValue(logicId, out record);

        /// <summary>ER3-WRK-02：玩家调整单台机器的工作类别偏好（RimWorld 式 1～4/0＝禁用）。
        /// 只做范围/存在性校验，不触碰 <see cref="HomeValleyWorkOrders"/> 的分配状态——调用方
        /// （UI）负责在写入成功后调用 <c>HomeValleyWorkOrders.MarkDirty()</c> 让下一次分配立即
        /// 感知变化，不必等满 0.5 秒轮询窗口。</summary>
        public static bool TrySetWorkPriority(int logicId, WorkOrderKind kind, int priority)
        {
            if (priority < 0 || priority > 4)
            {
                return false;
            }
            if (!_records.TryGetValue(logicId, out MachineRecord record) || !record.IsAlive)
            {
                return false;
            }
            record.WorkPriorities ??= WorkPriorities.Default();
            record.WorkPriorities.Set(kind, priority);
            return true;
        }

        // ── 阵亡 ─────────────────────────────────────────────

        private static void OnAllyDeath(AllyDeathSignal signal)
        {
            MarkDeadByEntity(signal.EntityId);
        }

        public static MachineOpResult MarkDeadByEntity(SimEntityId entity)
        {
            if (!TryGetLogicId(entity, out int logicId))
            {
                // 该实体从未登记过 LogicId（例如敌人、非机器单位）——不是本注册表的机器，安全忽略。
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, "实体未登记 LogicId，忽略。");
            }
            return MarkDeadByLogicId(logicId);
        }

        /// <summary>标记阵亡。幂等：已经是阵亡状态时直接返回成功、不重复处理（Reject-to-Safe 不等于
        /// 报错——重复的死亡信号是正常路径，不该被当成异常）。阵亡后 <see cref="_logicToEntity"/> /
        /// <see cref="_entityToLogic"/> 立即清空该条目：AC-MCH-002 的"阵亡机器不在回城/读档中复活"
        /// 首先就要求它不能再被任何查询解析出一个实体。</summary>
        public static MachineOpResult MarkDeadByLogicId(int logicId)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record))
            {
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, $"LogicId {logicId} 未登记。", logicId);
            }
            if (!record.IsAlive)
            {
                return MachineOpResult.Ok(logicId, "已经是阵亡状态，忽略重复标记。");
            }

            record.IsAlive = false;
            record.IsDeployed = false;
            if (_logicToEntity.TryGetValue(logicId, out SimEntityId entity))
            {
                _entityToLogic.Remove(entity.Value);
                _logicToEntity.Remove(logicId);
            }

            return MachineOpResult.Ok(logicId);
        }

        /// <summary>ER5-SILENT-01：唯一"机器受到外部伤害"写入口——此前项目里从未存在过一条真实会让
        /// <see cref="MachineRecord.Health"/> 因为敌方攻击而降低的代码路径（<see cref="SyncLiveState"/>
        /// 只是把已经算好的血量写回，不是伤害来源；<c>HomeValleyCombatTargets</c>/
        /// <c>FracturedCityRegion.TryDamageEnemy</c> 都是"玩家/AI 打敌人"，没有反向"敌人打玩家"）。
        /// 归零时复用 <see cref="MarkDeadByLogicId"/> 的既有死亡收尾（IsAlive=false/清实体绑定），
        /// 这样 <c>RegionControlSystem.Tick</c> 的死亡回弹侦测（读 <see cref="TryGetRecord"/> 的
        /// <c>IsAlive</c>）第一次有真实触发源，不再需要人工调用 <see cref="MarkDeadByLogicId"/> 模拟
        /// （DEBT-ER5CTL01-03/DEBT-ER5CMD01-01 均因"没有会真正杀死友军的敌方战斗 AI"而登记，本方法
        /// 是它们的真实覆盖点）。Reject-to-Safe：目标不存在/已阵亡直接安全返回，不抛异常、不重复
        /// 触发死亡副作用。</summary>
        public static MachineOpResult ApplyDamage(int logicId, float damage)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record))
            {
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, $"LogicId {logicId} 未登记。", logicId);
            }
            if (!record.IsAlive)
            {
                return MachineOpResult.Ok(logicId, "目标已阵亡，忽略重复伤害。");
            }

            record.Health = Mathf.Max(0f, record.Health - Mathf.Max(0f, damage));
            if (record.Health <= 0f)
            {
                return MarkDeadByLogicId(logicId);
            }
            return MachineOpResult.Ok(logicId, $"机器 {logicId} 受到 {damage:F1} 点伤害，剩余 {record.Health:F1}/{record.MaxHealth:F0}。");
        }

        // ── 活体状态同步（存档前拉一次实况）───────────────────

        /// <summary>存档前把活体机器的位置/血量/装配签名等易变字段从当前会话同步回记录。
        /// 找不到该 LogicId 或它已不在当前会话（跨区/未绑定）时安全 no-op——不是错误，
        /// 记录保留上一次同步到的值。</summary>
        public static void SyncLiveState(int logicId, Vector2 position, float health, string loadoutSignature)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record) || !record.IsAlive)
            {
                return;
            }

            record.WorldPosition = position;
            record.Health = health;
            if (loadoutSignature != null)
            {
                record.LoadoutSignature = loadoutSignature;
            }
        }

        public static MachineOpResult RecordControlled(int logicId)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record))
            {
                return MachineOpResult.Fail(MachineRegistryError.UnknownLogicId, $"LogicId {logicId} 未登记。", logicId);
            }
            record.TimesControlled++;
            return MachineOpResult.Ok(logicId);
        }

        /// <summary>ER4-MCH-01：<see cref="MachineExperienceFlags"/> 八项的唯一写入口。幂等——
        /// 已经记过的标记不会重复追加，调用方不需要自己先查一遍"有没有"。返回 true 表示这是
        /// 真正的"首次"（调用方可据此触发一次性反馈，如提示音/日志），false 表示早已记过或
        /// LogicId 不存在。</summary>
        public static bool TryMarkExperience(int logicId, string flagId)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record) || string.IsNullOrEmpty(flagId))
            {
                return false;
            }
            record.ExperienceFlags ??= Array.Empty<string>();
            if (Array.IndexOf(record.ExperienceFlags, flagId) >= 0)
            {
                return false;
            }
            record.ExperienceFlags = record.ExperienceFlags.Append(flagId).ToArray();
            return true;
        }

        /// <summary>ER4-MCH-01 第1条"统计与战斗事件统一来源，不在 UI 猜计数"——工作单完成的唯一
        /// 统计写入口，由 <see cref="Regions.HomeValleyWorkOrders"/> 每个真实 CompleteXxx 分支调用。
        /// 同时顺带标记 <see cref="MachineExperienceFlags.FirstJob"/>。死亡/未知 LogicId 静默 no-op
        /// （订单完成回调不应该因为统计写入失败而级联失败）。</summary>
        public static void RecordJobCompleted(int logicId)
        {
            if (!_records.TryGetValue(logicId, out MachineRecord record))
            {
                return;
            }
            record.JobsCompleted++;
            TryMarkExperience(logicId, MachineExperienceFlags.FirstJob);
        }

        // ── 存读档 ───────────────────────────────────────────

        /// <summary>写回 <see cref="CampaignState"/>：整份覆盖 <see cref="CampaignState.MachineRecords"/>
        /// 与分配器状态。<see cref="CampaignState.NormalizeForSave"/> 会再按 LogicId 排序一次，
        /// 这里不必自己排序。</summary>
        public static void ExportToCampaignState(CampaignState state)
        {
            if (state == null)
            {
                return;
            }

            state.MachineRecords = _records.Values.Select(CloneRecord).ToArray();
            state.NextMachineLogicId = _nextLogicId;
            state.NextMachineDisplayNumber = _nextDisplayNumber;
        }

        /// <summary>从 <see cref="CampaignState"/> 重建内存态。先整体 <see cref="ResetForNewCampaign"/>，
        /// 逐条校验后装入——LogicId&lt;=0、ChassisId 缺失、BlueprintId 缺失、与已装入记录 LogicId
        /// 重复的条目全部被拒绝且不计入分配器状态，不让半损坏数据污染新会话。
        /// 返回值 <see cref="MachineOpResult.Success"/> 为假时 <see cref="MachineOpResult.Message"/>
        /// 汇总本次被拒绝的条数——调用方应当继续正常读档流程（其余合法记录已装入），
        /// 只把这当成一条诊断信息，不当成整体读档失败。</summary>
        public static MachineOpResult LoadFromCampaignState(CampaignState state)
        {
            ResetForNewCampaign();
            if (state?.MachineRecords == null || state.MachineRecords.Length == 0)
            {
                return MachineOpResult.Ok(0, "无机器记录。");
            }

            int rejected = 0;
            foreach (MachineRecord r in state.MachineRecords)
            {
                if (r == null || r.LogicId <= 0 || string.IsNullOrEmpty(r.ChassisId))
                {
                    rejected++;
                    continue;
                }
                if (string.IsNullOrEmpty(r.BlueprintId))
                {
                    rejected++;
                    continue;
                }
                if (_records.ContainsKey(r.LogicId))
                {
                    rejected++;
                    continue;
                }

                _records[r.LogicId] = CloneRecord(r);
                _everAllocated.Add(r.LogicId);
            }

            int maxSeen = _everAllocated.Count > 0 ? _everAllocated.Max() + 1 : 1;
            _nextLogicId = Math.Max(Math.Max(state.NextMachineLogicId, 1), maxSeen);
            _nextDisplayNumber = Math.Max(state.NextMachineDisplayNumber, 1);

            return rejected > 0
                ? MachineOpResult.Fail(MachineRegistryError.CorruptRecord,
                    $"{rejected} 条机器记录因缺字段/重复 LogicId 被拒绝加载，其余 {_records.Count} 条正常。")
                : MachineOpResult.Ok(0, $"{_records.Count} 条机器记录正常加载。");
        }

        private static MachineRecord CloneRecord(MachineRecord src)
        {
            return new MachineRecord
            {
                LogicId = src.LogicId,
                DisplayNumber = src.DisplayNumber,
                ChassisId = src.ChassisId,
                BlueprintId = src.BlueprintId,
                BlueprintVersion = src.BlueprintVersion,
                LoadoutSignature = src.LoadoutSignature,
                FactionId = src.FactionId,
                RegionId = src.RegionId,
                WorldPosition = src.WorldPosition,
                Health = src.Health,
                MaxHealth = src.MaxHealth,
                InjuryFlags = src.InjuryFlags?.ToArray() ?? Array.Empty<string>(),
                Battery = src.Battery,
                CurrentWorkOrderId = src.CurrentWorkOrderId,
                Cargo = src.Cargo?.ToArray() ?? Array.Empty<CargoEntry>(),
                DoctrineId = src.DoctrineId,
                // ER3-WRK-02：深拷贝而非复用引用（此前 `src.WorkPriorities ?? new WorkPriorities()`
                // 在非空时直接共享同一个可变对象，本 Story 起 WorkPriorities 会被 TrySetWorkPriority
                // 原地修改，共享引用会让"克隆体"和"源记录"互相污染）；全零视为 ER1-SAVE-01 骨架期
                // 从未写过的旧数据，迁移为 Default（见 WorkPriorities.IsUninitialized 文档）。
                WorkPriorities = src.WorkPriorities == null || src.WorkPriorities.IsUninitialized()
                    ? WorkPriorities.Default()
                    : src.WorkPriorities.Clone(),
                ExperienceFlags = src.ExperienceFlags?.ToArray() ?? Array.Empty<string>(),
                KillCount = src.KillCount,
                JobsCompleted = src.JobsCompleted,
                ExpeditionsCompleted = src.ExpeditionsCompleted,
                TimesControlled = src.TimesControlled,
                IsAlive = src.IsAlive,
                IsInFactory = src.IsInFactory,
                IsDeployed = src.IsDeployed,
            };
        }
    }
}
