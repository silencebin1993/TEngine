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
        private float2 _strategicAnchor;
        private bool _hasStrategicAnchor;

        private const float DefaultControlSignalRange = 18f;
        private const float DefaultControlSwitchCooldown = 0.75f;

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
            _snapshot = _backend.GetSnapshot();
            _hasStrategicAnchor = false;
            CaptureStrategicAnchor();
        }

        public void End()
        {
            if (_backend != null)
            {
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
            _strategicAnchor = float2.zero;
            _hasStrategicAnchor = false;
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
            CaptureStrategicAnchor();

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
            position = _strategicAnchor;
            hasControlled = false;
            return _hasStrategicAnchor;
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
            if (!TryGetControlledUnit(out SimUnitControlState previous))
            {
                return ControlRequestResult.CurrentUnitUnavailable;
            }
            if (math.distance(previous.Position, target.Position) > ControlSignalRange)
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
            CaptureStrategicAnchor();
            Signals.Publish(new ControlledUnitChangedSignal
            {
                PreviousUnitId = previous.EntityId,
                CurrentUnitId = targetId,
                Result = ControlRequestResult.Success,
            });
            return ControlRequestResult.Success;
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
            CaptureStrategicAnchor();
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

        private void CaptureStrategicAnchor()
        {
            if (TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                _strategicAnchor = controlled.Position;
                _hasStrategicAnchor = true;
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
                CaptureStrategicAnchor();
            }
        }

        public SimWorld World => _backend as SimWorld;

        public override void OnDispose()
        {
            End();
        }
    }
}
