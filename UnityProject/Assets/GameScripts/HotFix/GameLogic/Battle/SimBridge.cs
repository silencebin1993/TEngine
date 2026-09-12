using System;
using BinGames.Sim;
using GameLogic.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle
{
    /// <summary>
    /// AOT 内核的热更侧门面。**整个热更层只有这一个类碰内核。**
    ///
    /// 纪律（框架文档 §2.3）：
    /// - 写内核只能через命令缓冲，绝不直接改 NativeArray
    /// - 读内核只能через只读快照
    /// - 本类不做任何逐单位循环——那是内核的活
    ///
    /// 之所以要这层：HybridCLR 解释执行泛型原生容器是风险区，全部隔在 AOT 侧最安全。
    /// </summary>
    public sealed class SimBridge : GameModuleBase
    {
        public override int Priority => ModulePriority.Simulation;

        private ISimBackend _backend;
        private SimCommandBuffer _cmds;
        private SimSnapshot _snapshot;
        private SimConfig _cfg;
        private bool _running;
        private float _controlSwitchCooldownRemaining;

        /// <summary>本局最后一次已知的控制状态。<see cref="End"/> 时从内核抓取一份留在托管侧，
        /// 因为内核 Dispose 之后就什么都问不到了——重进场景的恢复和存档写入都靠它。</summary>
        private ControlHandoffState _handoff;

        /// <summary>待兑现的恢复请求。目标单位可能要等几帧才 Spawn 完，
        /// 这期间控制状态是 Suspended 而不是 None，UI 不该闪一次假报警。</summary>
        private ControlHandoffState _pendingRestore;
        private float _restoreGraceRemaining;
        private ControlAvailability _availability = ControlAvailability.None;

        private const float DefaultControlSignalRange = 18f;
        private const float DefaultControlSwitchCooldown = 0.75f;
        /// <summary>恢复请求的宽限秒数。超时即放弃恢复并落到确定性的兜底状态，不无限期挂着。</summary>
        private const float DefaultControlRestoreGrace = 1.5f;

        /// <summary>逻辑 id 分配器。0 保留给玩家/环境。</summary>
        private int _nextLogicId = 1;

        public SimSnapshot Snapshot => _snapshot;
        public bool Running => _running;
        public float ArenaHalfExtent => _cfg.ArenaHalfExtent;

        /// <summary>当前局的静态障碍布局（story-009）。SetObstacles 时保存的同一份管理数组引用，
        /// 供 SpawnDirector 等做生成点回避，不新增 Bind 依赖。</summary>
        public ObstacleSpec[] Obstacles { get; private set; }

        /// <summary>本帧受控实体意图。兼容旧 PlayerIntent 形状，提交时按 ControlledUnitId 解析。</summary>
        public PlayerIntent Intent { get; private set; } = PlayerIntent.Idle;

        public float ControlSignalRange { get; private set; } = DefaultControlSignalRange;
        public float ControlSwitchCooldown { get; private set; } = DefaultControlSwitchCooldown;
        public float ControlSwitchCooldownRemaining => _controlSwitchCooldownRemaining;
        public SimEntityId ControlledUnitId => _running && _backend != null
            ? _backend.ControlledUnitId
            : SimEntityId.None;

        public void Begin(SimConfig cfg, BehaviorArchetype[] archetypes)
        {
            End();

            _cfg = cfg;
            var world = new SimWorld();
            world.Initialize(cfg);
            world.SetArchetypes(archetypes);
            _backend = world;

            _cmds = default;
            _cmds.Initialize(Unity.Collections.Allocator.Persistent);
            _nextLogicId = 1;
            _controlSwitchCooldownRemaining = 0f;
            Intent = PlayerIntent.Idle;
            _running = true;
            // 回弹与手动切换必须用同一把尺子，否则"够得着切过去"和"死后回弹得到"会对不上。
            _backend.ControlFallbackRange = ControlSignalRange;
            _snapshot = _backend.GetSnapshot();
            _pendingRestore = default;
            _restoreGraceRemaining = 0f;
            RefreshAvailability(0f);
        }

        public void End()
        {
            if (_backend != null)
            {
                // 内核 Dispose 之后控制状态就问不到了；先把它抄进托管侧的控制记忆。
                _handoff = CaptureHandoff();
                _backend.Dispose();
                _backend = null;
            }
            if (_cmds.IsCreated)
            {
                _cmds.Dispose();
            }
            _running = false;
            _controlSwitchCooldownRemaining = 0f;
            Intent = PlayerIntent.Idle;
            _snapshot = default;
            _pendingRestore = default;
            _restoreGraceRemaining = 0f;
            // 世界没了就没有受控实体，但控制记忆（_handoff）要留着——那是重进场景恢复的依据。
            _availability = ControlAvailability.None;
            Obstacles = null;
        }

        public override void OnUpdate(float dt)
        {
            if (!_running || _backend == null)
            {
                return;
            }

            _controlSwitchCooldownRemaining = math.max(0f, _controlSwitchCooldownRemaining - math.max(0f, dt));

            _cmds.SetPlayerIntent(Intent);
            _backend.Step(dt, ref _cmds);
            _snapshot = _backend.GetSnapshot();

            TryFulfillPendingRestore(dt);
            // 内核在 Step 里可能已经做过死亡回弹；事件统一在这里出，与手动切换同一条路径。
            PublishControlChanges();
            RefreshAvailability(dt);

            // 意图每帧重置，避免上一帧的冲刺倍率粘住
            Intent = PlayerIntent.Idle;
        }

        public override void OnExit()
        {
            End();
        }

        public int NextLogicId() => _nextLogicId++;

        // ── 控制服务（HotFix 控制相关代码的唯一入口）──

        /// <summary>配置临时信号规则。M1-04 不建立正式信号网络，只保留可调距离与冷却。</summary>
        public void ConfigureControlSwitch(float signalRange, float cooldownSeconds)
        {
            ControlSignalRange = math.max(0f, signalRange);
            ControlSwitchCooldown = math.max(0f, cooldownSeconds);
            if (_backend != null)
            {
                _backend.ControlFallbackRange = ControlSignalRange;
            }
        }

        public bool TryGetControlledUnit(out SimUnitControlState state)
        {
            if (_running && _backend != null &&
                _backend.TryGetUnitControlState(_backend.ControlledUnitId, out state) && state.IsAlive)
            {
                return true;
            }
            state = default;
            return false;
        }

        /// <summary>供 HUD、相机与表现层统一读取的 O(1) 当前受控实体视图。</summary>
        public bool TryGetControlledPresentation(out SimControlledUnitView view)
        {
            if (_running && _snapshot.TryResolveControlledUnit(out int index))
            {
                view = new SimControlledUnitView
                {
                    EntityId = _snapshot.EntityId[index],
                    UnitIndex = index,
                    Position = _snapshot.Position[index],
                    Health = _snapshot.Health[index],
                    Radius = _snapshot.Radius[index],
                    Status = (SimStatus)_snapshot.Status[index],
                    Faction = (SimFaction)_snapshot.Faction[index],
                    IntentSource = (IntentSource)_snapshot.IntentSource[index],
                    VisualId = _snapshot.VisualId[index],
                };
                return true;
            }
            view = default;
            return false;
        }

        /// <summary>
        /// 相机锚点：有控制实体时返回其实时位置；否则返回本局最后一个有效战术位置。
        /// bool 返回是否存在可用锚点，hasControlled 区分战术跟随和临时战略回退。
        /// </summary>
        public bool TryGetPresentationAnchor(out float2 position, out bool hasControlled)
        {
            if (TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                position = controlled.Position;
                hasControlled = true;
                return true;
            }

            hasControlled = false;
            // 回退锚点的唯一真相在内核（它才知道受控实体死在哪一刻的哪个位置）；
            // 世界已卸载时才退到托管侧的控制记忆，避免本层再维护第二份会漂移的锚点。
            if (_running && _backend != null && _backend.TryGetControlFallbackAnchor(out position))
            {
                return true;
            }
            position = _handoff.FallbackAnchor;
            return _handoff.HasAnchor;
        }

        public SimControlCandidate[] GetControlCandidates()
        {
            return _running && _backend != null
                ? _backend.GetControlCandidates(ControlSignalRange)
                : Array.Empty<SimControlCandidate>();
        }

        /// <summary>
        /// 请求切换到稳定实体 ID。所有失败路径均在修改控制状态前返回；成功后只发布一次事件。
        /// 校验顺序固定为身份、存活、阵营、范围、冷却，保证调用方得到稳定失败原因。
        /// </summary>
        public ControlRequestResult RequestControlSwitch(SimEntityId targetId)
        {
            if (!_running || _backend == null) { return ControlRequestResult.SimulationNotRunning; }
            if (!targetId.IsValid) { return ControlRequestResult.InvalidTarget; }
            if (!_backend.TryGetUnitControlState(targetId, out SimUnitControlState target))
            {
                return ControlRequestResult.TargetNotFound;
            }
            if (!target.IsAlive) { return ControlRequestResult.TargetDead; }
            if (!IsFriendlyFaction(target.Faction)) { return ControlRequestResult.TargetNotFriendly; }
            if (_backend.ControlledUnitId == targetId) { return ControlRequestResult.AlreadyControlled; }

            // 信号范围的原点：有受控实体时是它自己；没有时退到战略回退锚点（意识最后所在处）。
            // M1-06 之前这里直接返回 CurrentUnitUnavailable——意味着一旦死亡回弹失败，
            // 哪怕场上还站着活生生的友军，玩家也再也夺不回控制权。那不是"明确为无"，是卡死。
            float2 origin;
            if (TryGetControlledUnit(out SimUnitControlState previous))
            {
                origin = previous.Position;
            }
            else if (!TryGetPresentationAnchor(out origin, out _))
            {
                return ControlRequestResult.CurrentUnitUnavailable;
            }
            if (math.distance(origin, target.Position) > ControlSignalRange)
            {
                return ControlRequestResult.OutOfSignalRange;
            }
            if (_controlSwitchCooldownRemaining > 0f)
            {
                return ControlRequestResult.CooldownActive;
            }

            ControlRequestResult mapped = MapControlResult(_backend.TrySwitchControlledUnit(targetId));
            if (mapped != ControlRequestResult.Success)
            {
                return mapped;
            }

            _controlSwitchCooldownRemaining = ControlSwitchCooldown;
            _snapshot = _backend.GetSnapshot();
            // 手动切换与死亡回弹共用内核的变更队列，所以这里不自己 Publish：
            // 事件只从 PublishControlChanges 一个出口发，"一次切换一次事件"才不依赖调用方自觉。
            PublishControlChanges();
            RefreshAvailability(0f);
            return ControlRequestResult.Success;
        }

        /// <summary>
        /// M1-06：请求把控制权恢复到一段控制记忆（读档 / 重进场景）。
        ///
        /// 目标单位常常还没 Spawn 完（Spawn 要等下一次 Step 才落地），所以这里不是一次性成败：
        /// 解析得到就立刻恢复，解析不到就挂起，在宽限期内每帧重试，控制状态显示为
        /// <see cref="ControlAvailability.Suspended"/>。宽限期用完仍找不到就放弃，
        /// 保留世界自带的默认受控实体——任何时刻都只会是"恰好一个"或"明确为无"。
        /// </summary>
        public bool RequestControlRestore(in ControlHandoffState handoff,
            float graceSeconds = DefaultControlRestoreGrace)
        {
            if (!handoff.HasRecord)
            {
                return false;
            }

            _handoff = handoff;
            if (!_running || _backend == null)
            {
                return false;
            }

            _pendingRestore = handoff;
            _restoreGraceRemaining = math.max(0f, graceSeconds);
            TryFulfillPendingRestore(0f);
            PublishControlChanges();
            RefreshAvailability(0f);
            return _pendingRestore.HasRecord == false;
        }

        /// <summary>本局最后一次已知的控制状态。运行中取实时值，已结束时取 <see cref="End"/> 留下的记忆。</summary>
        public ControlHandoffState CurrentHandoff => _running && _backend != null ? CaptureHandoff() : _handoff;

        /// <summary>当前控制可用性。Suspended 表示"记录还在、目标暂时解析不到"，不是丢失。</summary>
        public ControlAvailability Availability => _availability;

        private ControlHandoffState CaptureHandoff()
        {
            var state = new ControlHandoffState();
            if (_backend == null)
            {
                return state;
            }

            if (_backend.TryGetControlFallbackAnchor(out float2 anchor))
            {
                state.FallbackAnchor = anchor;
                state.HasAnchor = true;
                state.HasRecord = true;
            }

            if (TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                state.ControlledUnitId = controlled.EntityId;
                // LogicId 是热更层分配的、跨世界重建仍能对上的弱标识；
                // SimEntityId 只在同一个 SimWorld 实例内有意义，落盘后必须靠它兜底。
                state.ControlledLogicId = _snapshot.LogicId[controlled.UnitIndex];
                state.HasRecord = true;
            }

            return state;
        }

        /// <summary>把挂起的恢复请求兑现掉。解析不到就递减宽限，超时放弃。</summary>
        private void TryFulfillPendingRestore(float dt)
        {
            if (!_pendingRestore.HasRecord || _backend == null)
            {
                return;
            }

            if (TryResolveHandoffTarget(_pendingRestore, out SimEntityId targetId))
            {
                ControlSwitchResult result = _backend.TryRestoreControlledUnit(targetId);
                if (result == ControlSwitchResult.Success || result == ControlSwitchResult.AlreadyControlled)
                {
                    _pendingRestore = default;
                    _restoreGraceRemaining = 0f;
                    _snapshot = _backend.GetSnapshot();
                    return;
                }
            }

            _restoreGraceRemaining -= math.max(0f, dt);
            if (_restoreGraceRemaining <= 0f)
            {
                // 放弃恢复。不清空 _handoff：锚点仍然是"意识上次在哪"的唯一记录。
                _pendingRestore = default;
            }
        }

        /// <summary>先认稳定实体 ID，对不上再退到 LogicId。跨进程读档时前者必然失效，后者才是桥。</summary>
        private bool TryResolveHandoffTarget(in ControlHandoffState handoff, out SimEntityId targetId)
        {
            if (handoff.ControlledUnitId.IsValid &&
                _backend.TryGetUnitControlState(handoff.ControlledUnitId, out SimUnitControlState byId) &&
                byId.IsAlive)
            {
                targetId = handoff.ControlledUnitId;
                return true;
            }

            if (handoff.ControlledLogicId != 0 && _snapshot.Count > 0)
            {
                for (int i = 0; i < _snapshot.Count; i++)
                {
                    if (_snapshot.Alive[i] != 0 && _snapshot.LogicId[i] == handoff.ControlledLogicId)
                    {
                        targetId = _snapshot.EntityId[i];
                        return targetId.IsValid;
                    }
                }
            }

            targetId = SimEntityId.None;
            return false;
        }

        /// <summary>把内核排队的控制权变更全部转成信号。消费即出队，所以一条变更只会发布一次。</summary>
        private void PublishControlChanges()
        {
            if (_backend == null)
            {
                return;
            }

            while (_backend.TryConsumeControlChange(out ControlChangeEvent change))
            {
                _handoff.FallbackAnchor = change.FallbackAnchor;
                _handoff.HasAnchor = true;
                _handoff.HasRecord = true;
                Signals.Publish(new ControlledUnitChangedSignal
                {
                    PreviousUnitId = change.PreviousUnitId,
                    CurrentUnitId = change.CurrentUnitId,
                    Reason = change.Reason,
                    FallbackAnchor = change.FallbackAnchor,
                    Result = ControlRequestResult.Success,
                });
            }
        }

        private void RefreshAvailability(float dt)
        {
            if (!_running || _backend == null)
            {
                _availability = ControlAvailability.None;
                return;
            }

            if (TryGetControlledPresentation(out _))
            {
                _availability = ControlAvailability.Controlled;
                return;
            }

            _availability = _pendingRestore.HasRecord && _restoreGraceRemaining > 0f
                ? ControlAvailability.Suspended
                : ControlAvailability.None;
        }

        public void SetControlledIntent(in PlayerIntent intent)
        {
            Intent = intent;
        }

        public bool SetControlledPosition(float2 position)
        {
            if (!_running || !TryGetControlledUnit(out _)) { return false; }
            (_backend as SimWorld)?.SetPlayerPosition(position);
            _snapshot = _backend.GetSnapshot();
            return true;
        }

        public bool ApplyStatusToControlled(SimStatus status, bool add = true)
        {
            if (!TryGetControlledUnit(out SimUnitControlState controlled)) { return false; }
            ApplyStatusUnit(controlled.UnitIndex, status, add);
            return true;
        }

        private static bool IsFriendlyFaction(SimFaction faction)
        {
            return faction == SimFaction.Player || faction == SimFaction.PlayerMinion;
        }

        private static ControlRequestResult MapControlResult(ControlSwitchResult result)
        {
            switch (result)
            {
                case ControlSwitchResult.Success: return ControlRequestResult.Success;
                case ControlSwitchResult.AlreadyControlled: return ControlRequestResult.AlreadyControlled;
                case ControlSwitchResult.WorldNotInitialized: return ControlRequestResult.SimulationNotRunning;
                case ControlSwitchResult.InvalidTarget: return ControlRequestResult.InvalidTarget;
                case ControlSwitchResult.TargetNotFound: return ControlRequestResult.TargetNotFound;
                case ControlSwitchResult.TargetDead: return ControlRequestResult.TargetDead;
                case ControlSwitchResult.TargetNotFriendly: return ControlRequestResult.TargetNotFriendly;
                default: return ControlRequestResult.TargetNotFound;
            }
        }

        // ── 写入接口（全部只是入队，实际生效在内核 Step）──

        public int Spawn(in SpawnRequest req)
        {
            if (!_running)
            {
                return SimConst.InvalidIndex;
            }
            _cmds.Spawn(req);
            return req.LogicId;
        }

        public void Despawn(int unitIndex)
        {
            if (_running) { _cmds.Despawn(unitIndex); }
        }

        /// <summary>圆形范围伤害。</summary>
        public void DamageArea(float2 origin, float radius, float amount,
            SimFaction targetFaction = SimFaction.Hostile,
            SimStatus applyStatus = SimStatus.None,
            SimStatus requireStatus = SimStatus.None,
            int chainCount = 0, float chainRange = 4f, float chainFalloff = 0.75f,
            int sourceLogicId = 0)
        {
            if (!_running) { return; }
            _cmds.Damage(new DamageRequest
            {
                Origin = origin,
                Radius = radius,
                TargetIndex = SimConst.InvalidIndex,
                Amount = amount,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                RequireStatus = requireStatus,
                ChainCount = chainCount,
                ChainRange = chainRange,
                ChainFalloff = chainFalloff,
                SourceLogicId = sourceLogicId,
            });
        }

        /// <summary>单体伤害。</summary>
        public void DamageUnit(int unitIndex, float amount,
            SimStatus applyStatus = SimStatus.None,
            int chainCount = 0, float chainRange = 4f, float chainFalloff = 0.75f,
            int sourceLogicId = 0)
        {
            if (!_running) { return; }
            _cmds.Damage(new DamageRequest
            {
                Origin = float2.zero,
                Radius = -1f,
                TargetIndex = unitIndex,
                Amount = amount,
                TargetFaction = SimFaction.None,
                ApplyStatus = applyStatus,
                RequireStatus = SimStatus.None,
                ChainCount = chainCount,
                ChainRange = chainRange,
                ChainFalloff = chainFalloff,
                SourceLogicId = sourceLogicId,
            });
        }

        public void ApplyStatusArea(float2 origin, float radius, SimStatus status,
            bool add = true, SimFaction targetFaction = SimFaction.Hostile)
        {
            if (!_running) { return; }
            _cmds.Status(new StatusRequest
            {
                Origin = origin,
                Radius = radius,
                TargetIndex = SimConst.InvalidIndex,
                Status = status,
                TargetFaction = targetFaction,
                Add = add,
            });
        }

        /// <summary>切换某个存活单位的行为原型（如首领按血量分阶段）。</summary>
        public void SwapArchetype(int unitIndex, int archetypeId)
        {
            if (!_running) { return; }
            _cmds.SwapArchetype(new ArchetypeSwapRequest
            {
                TargetIndex = unitIndex,
                ArchetypeId = archetypeId,
            });
        }

        public void ApplyStatusUnit(int unitIndex, SimStatus status, bool add = true)
        {
            if (!_running) { return; }
            _cmds.Status(new StatusRequest
            {
                Origin = float2.zero,
                Radius = -1f,
                TargetIndex = unitIndex,
                Status = status,
                TargetFaction = SimFaction.None,
                Add = add,
            });
        }

        public void FireProjectile(float2 pos, float2 dir, float speed, float damage,
            float radius = 0.25f, float lifetime = 2.5f, int pierce = 1,
            SimFaction targetFaction = SimFaction.Hostile,
            SimStatus applyStatus = SimStatus.None,
            int sourceLogicId = 0, int visualId = 0)
        {
            if (!_running) { return; }
            _cmds.Projectile(new ProjectileRequest
            {
                Position = pos,
                Direction = dir,
                Speed = speed,
                Damage = damage,
                Radius = radius,
                Lifetime = lifetime,
                Pierce = pierce,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                SourceLogicId = sourceLogicId,
                VisualId = visualId,
            });
        }

        /// <summary>
        /// combat-primitive-overhaul：带全部弹道基元的发射入口。调用方自己填好
        /// <see cref="ProjectileRequest"/>（字段语义见该结构注释），Bridge 只负责入队。
        ///
        /// 器官/基因的攻击**必须**走这条路，不许再在热更层用"预测落点 + 倒计时"伪造弹道——
        /// 那套做法让判定与表现各算各的，且弹体飞行途中的敌人永远打不到。
        /// </summary>
        public void FireProjectile(in ProjectileRequest req)
        {
            if (!_running) { return; }
            _cmds.Projectile(req);
        }

        /// <summary>
        /// combat-primitive-overhaul：扇形范围伤害。近战底盘专用——圆形范围叠一道"必须落在面朝锥内"的判据，
        /// 不再是"身前放个大圆连背后一起打"。<paramref name="halfAngleDeg"/> &gt;= 180 时退化为整圆。
        /// </summary>
        public void DamageCone(float2 origin, float radius, float2 coneDir, float halfAngleDeg, float amount,
            SimFaction targetFaction = SimFaction.Hostile,
            SimStatus applyStatus = SimStatus.None,
            int chainCount = 0, float nearRadius = 0f, int sourceLogicId = 0)
        {
            if (!_running) { return; }
            bool full = halfAngleDeg >= 180f || math.lengthsq(coneDir) < 1e-6f;
            _cmds.Damage(new DamageRequest
            {
                Origin = origin,
                Radius = radius,
                TargetIndex = SimConst.InvalidIndex,
                Amount = amount,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                RequireStatus = SimStatus.None,
                ChainCount = chainCount,
                ChainRange = 4f,
                ChainFalloff = 0.75f,
                SourceLogicId = sourceLogicId,
                ConeDir = full ? float2.zero : math.normalizesafe(coneDir),
                ConeCosHalf = full ? -1f : math.cos(math.radians(halfAngleDeg)),
                ConeNearRadius = nearRadius,
            });
        }

        /// <summary>
        /// chassis-native-primitives：把范围内的目标推开（击退）。
        ///
        /// 走内核 <see cref="SimWorld.ApplyKnockback"/> 立即结算，不经命令缓冲——位移不是伤害，
        /// 没有"本帧结算顺序"的语义要求，而且调用方（一次挥击/一次命中）需要**当场**知道推动了几个。
        /// 逐单位遍历发生在 AOT 侧，热更层这里只发一次调用，不违反"热更层每帧不得 O(敌人数)"。
        /// </summary>
        /// <returns>实际被推动的单位数（未运行时为 0）。</returns>
        public int Knockback(float2 origin, float radius, float distance,
            SimFaction targetFaction = SimFaction.Hostile)
        {
            SimWorld w = World;
            return _running && w != null ? w.ApplyKnockback(origin, radius, distance, targetFaction) : 0;
        }

        /// <summary>
        /// enemy-mechanics-parity：铺一块持续区域（毒坑 / 酸洼 / 贴身光环）。
        ///
        /// 区域已经是**内核一等实体**：玩家的坑与敌人的毒云共用一套，
        /// 区别只在 <paramref name="targetFaction"/>。此前它只存在于热更层的
        /// <c>MetabolicSliceBridge._pendingLinger</c> 里，所以敌人根本没法放。
        /// </summary>
        /// <param name="followUnitIndex">跟随某个单位（光环）；<see cref="SimConst.InvalidIndex"/> = 钉在原地。</param>
        public void SpawnZone(float2 position, float radius, float seconds, float damagePerTick,
            float interval, SimFaction targetFaction = SimFaction.Hostile,
            float growthRate = 0f, float maxRadius = 0f, SimStatus applyStatus = SimStatus.None,
            int chainCount = 0, int sourceLogicId = 0,
            int followUnitIndex = SimConst.InvalidIndex, uint tint = 0u)
        {
            if (!_running) { return; }
            _cmds.Zone(new ZoneRequest
            {
                Position = position,
                Radius = radius,
                GrowthRate = growthRate,
                MaxRadius = maxRadius,
                DamagePerTick = damagePerTick,
                Interval = interval,
                Seconds = seconds,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                ChainCount = chainCount,
                SourceLogicId = sourceLogicId,
                FollowUnitIndex = followUnitIndex,
                Tint = tint,
            });
        }

        /// <summary>当前场上存活的持续区域数。</summary>
        public int LiveZoneCount => _running && World != null ? World.LiveZoneCount : 0;

        /// <summary>
        /// enemy-ranged-and-parry：弹反。把扇形内朝玩家飞来的敌方弹体打回去（调头 + 换阵营 + 归属玩家 + 加伤）。
        ///
        /// <paramref name="halfAngleDeg"/> &gt;= 180 或方向为零时退化为整圆，与
        /// <see cref="DamageCone"/> 同一约定。返回实际弹回的弹体数。
        /// </summary>
        public int DeflectProjectiles(float2 origin, float radius, float2 coneDir, float halfAngleDeg,
            int newSourceLogicId, float damageMul = 1.5f)
        {
            SimWorld w = World;
            if (!_running || w == null) { return 0; }
            bool full = halfAngleDeg >= 180f || math.lengthsq(coneDir) < 1e-6f;
            SimFaction sourceFaction = TryGetControlledUnit(out SimUnitControlState controlled)
                ? controlled.Faction
                : SimFaction.Player;
            return w.DeflectProjectiles(origin, radius,
                full ? float2.zero : math.normalizesafe(coneDir),
                full ? -1f : math.cos(math.radians(halfAngleDeg)),
                sourceFaction, newSourceLogicId, SimFaction.Hostile, damageMul);
        }

        /// <summary>combat-primitive-overhaul：本帧弹体终结事件条数（真实落点）。热更层放留坑/命中表现用。</summary>
        public int ProjectileEndCount => _running && _snapshot.ProjectileEnds.IsCreated ? _snapshot.ProjectileEndCount : 0;

        /// <summary>按下标取本帧第 i 条弹体终结事件。与 <see cref="ProjectileEndCount"/> 配套，
        /// 只在本帧有效（下一次 Step 即失效，同快照约定）。</summary>
        public ProjectileEndEvent GetProjectileEnd(int i) => _snapshot.ProjectileEnds[i];

        /// <summary>
        /// 当前场上还在飞的弹体数。验收探针用（"开火后真的有弹体存在"），
        /// 不要在每帧逻辑里调——它是 O(弹体容量) 的线性扫描。
        /// </summary>
        public int LiveProjectileCount
        {
            get
            {
                SimWorld w = World;
                if (w == null || !_running)
                {
                    return 0;
                }
                NativeArray<ProjectileState> arr = w.Projectiles;
                if (!arr.IsCreated)
                {
                    return 0;
                }
                int n = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].Alive != 0) { n++; }
                }
                return n;
            }
        }

        // ── 受控实体兼容读写 ──

        /// <summary>
        /// 兼容旧调用名：始终跟随当前受控实体，是战术跟随锚点，不是跨控制切换保持不变的战略锚点。
        /// 需要战略锚点的系统必须自行持有明确状态。
        /// </summary>
        public float2 PlayerPosition => TryGetControlledPresentation(out SimControlledUnitView controlled)
            ? controlled.Position
            : float2.zero;
        /// <summary>兼容旧调用名：当前受控实体生命。</summary>
        public float PlayerHealth => _running ? _snapshot.PlayerHealth : 0f;
        /// <summary>兼容旧调用名：当前受控实体半径。</summary>
        public float PlayerRadius => _running ? _snapshot.PlayerRadius : 1f;
        /// <summary>本帧玩家受到的接触伤害。由 Resolution 阶段消费。</summary>
        public float PlayerDamageTaken => _running ? _snapshot.PlayerDamageTaken : 0f;

        /// <summary>把玩家受伤倍率（<c>StatId.DamageTaken</c>）推给内核——扣血在内核里做，
        /// 减伤是玩法数值。见 <see cref="SimWorld.PlayerDamageTakenMul"/>。</summary>
        public void SetPlayerDamageTakenMul(float mul)
        {
            SimWorld w = World;
            if (w != null) { w.PlayerDamageTakenMul = mul; }
        }

        public void SetPlayerStats(float maxHp, float currentHp, float radius, float speed)
        {
            (_backend as SimWorld)?.SetPlayerStats(maxHp, currentHp, radius, speed);
        }

        /// <summary>任务二（3D 表现差异化）：Carrier 装配变化时切换玩家渲染造型。</summary>
        public void SetPlayerVisualId(int visualId)
        {
            (_backend as SimWorld)?.SetPlayerVisualId(visualId);
        }

        /// <summary>稳定 ID 定位的视觉恢复入口；仅供控制专属表现清理旧目标。</summary>
        public bool SetUnitVisualId(SimEntityId entityId, int visualId)
        {
            SimWorld world = _backend as SimWorld;
            if (!_running || world == null || !world.SetUnitVisualId(entityId, visualId))
            {
                return false;
            }
            _snapshot = _backend.GetSnapshot();
            return true;
        }

        public void DamagePlayer(float amount)
        {
            (_backend as SimWorld)?.DamagePlayer(amount);
        }

        public void HealPlayer(float amount, float maxHp)
        {
            (_backend as SimWorld)?.HealPlayer(amount, maxHp);
        }

        /// <summary>加载本局静态障碍布局（story-009）。</summary>
        public void SetObstacles(ObstacleSpec[] obstacles)
        {
            Obstacles = obstacles;
            (_backend as SimWorld)?.SetObstacles(obstacles);
        }

        /// <summary>吞噬结算：直接击杀并生成死亡事件。</summary>
        public void ConsumeUnit(int unitIndex)
        {
            (_backend as SimWorld)?.KillUnit(unitIndex, 0);
            if (_backend != null)
            {
                _snapshot = _backend.GetSnapshot();
                // 吞噬掉的可能正是受控实体（被更大的东西吃了），内核已在 KillUnit 里回弹过，
                // 这里必须立刻把变更发出去，不能拖到下一帧 OnUpdate——中间隔着本帧的结算与 UI 刷新。
                PublishControlChanges();
                RefreshAvailability(0f);
            }
        }

        public SimWorld World => _backend as SimWorld;

        public override void OnDispose()
        {
            End();
        }
    }
}
