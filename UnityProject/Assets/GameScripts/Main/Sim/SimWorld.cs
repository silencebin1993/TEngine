using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// AOT 模拟内核。SoA 布局 + Burst job 链，零 GC。
    ///
    /// 这是一个**通用 agent 模拟器**：它不认识"细胞"、"敌人"或任何玩法概念，
    /// 只认识位置、半径、阵营、行为原型和状态位。细胞阶段、生物阶段、
    /// 文明阶段的 RTS 军队都能复用它，差异全在数据。
    ///
    /// 详见 DesignDocs/Game_Framework_Design.md §4。
    /// </summary>
    public sealed class SimWorld : ISimBackend
    {
        private SimConfig _cfg;
        private bool _created;

        // ── SoA 单位数据 ──
        private NativeArray<float2> _position;
        private NativeArray<float2> _velocity;
        private NativeArray<float2> _desiredDir;
        private NativeArray<float2> _separation;
        private NativeArray<float> _health;
        private NativeArray<float> _radius;
        private NativeArray<float> _maxSpeed;
        private NativeArray<float> _attackTimer;
        private NativeArray<int> _archetypeId;
        private NativeArray<uint> _status;
        private NativeArray<byte> _faction;
        private NativeArray<byte> _alive;
        private NativeArray<int> _logicId;
        private NativeArray<int> _visualId;
        private NativeArray<SimEntityId> _entityId;
        private NativeArray<byte> _intentSource;
        private NativeArray<byte> _intentSourceBeforePlayer;
        private NativeArray<UnitIntent> _unitIntents;

        private NativeArray<BehaviorArchetype> _archetypes;
        private NativeArray<ProjectileState> _projectiles;

        /// <summary>enemy-mechanics-parity：持续区域（毒坑/光环）。玩家与敌人共用。</summary>
        private NativeArray<ZoneState> _zones;
        private int _zoneCursor;
        private NativeQueue<DamageRequest> _zoneDamage;

        // ── 静态障碍（story-009）：容量固定 SimConst.MaxObstacles，生命周期同 _archetypes ──
        private NativeArray<float2> _obstaclePos;
        private NativeArray<float> _obstacleRadius;
        private int _obstacleCount;

        // ── 槽位管理 ──
        private NativeList<int> _freeSlots;
        private int _unitCount;
        private int _projectileCursor;
        private ulong _nextEntityId;
        private SimEntityId _controlledUnitId;

        // ── 控制权变更（M1-06）──
        /// <summary>待热更层消费的控制权变更，FIFO。一帧内可能连续回弹（刚接手的单位又被同一波伤害打死），
        /// 所以不能只留一条覆盖式记录。非 Burst 路径、容量极小，用托管数组即可，
        /// 与 <see cref="GetControlCandidates"/> 同一约定。</summary>
        private readonly ControlChangeEvent[] _controlChanges = new ControlChangeEvent[SimConst.MaxControlChangesPerFrame];
        private int _controlChangeCount;

        /// <summary>最后一个有效受控实体的位置。受控实体没了以后，表现层靠它保持视角不丢，
        /// 存档也存这一份——它是"意识上次在哪"的唯一权威。</summary>
        private float2 _controlFallbackAnchor;
        private bool _hasControlFallbackAnchor;

        /// <summary>意识回弹的最大距离。由桥接层用信号范围配置，保证自动回弹与手动切换用同一把尺子；
        /// 非正值表示不限距离。</summary>
        private float _controlFallbackRange = SimConst.DefaultControlFallbackRange;

        /// <summary>是否启用意识回弹。关掉之后受控实体一死就确定地落到"无控制"，
        /// 供硬核死亡规则与"回弹失败后表现层行为"的回归用例构造确定场景。</summary>
        private bool _controlFallbackEnabled = true;

        // ── RTS 命令（M2-02）──
        /// <summary>每槽位当前生效的命令。命令是**持久状态**——意图每帧被重置，命令不会。</summary>
        private NativeArray<UnitCommand> _unitCommands;
        /// <summary>撤退时的威胁感知半径。</summary>
        private float _retreatThreatRange = SimConst.DefaultRetreatThreatRange;

        // ── 事件与中间缓冲 ──
        private NativeList<int> _pendingDeaths;
        private NativeList<HitEvent> _hitEvents;
        private NativeArray<DeathEvent> _deathEvents;
        private int _deathCount;
        private NativeList<int> _devourCandidates;
        private NativeQueue<DamageRequest> _projectileDamage;
        /// <summary>combat-primitive-overhaul：分裂/回旋产生的二级弹体。并行 Job 不能直接抢 _projectiles 槽位。</summary>
        private NativeQueue<ProjectileRequest> _projectileSpawns;
        /// <summary>同上，弹体终结事件；主线程搬进 _projectileEndEvents 供快照读。</summary>
        private NativeQueue<ProjectileEndEvent> _projectileEndQueue;
        private NativeArray<ProjectileEndEvent> _projectileEndEvents;
        private int _projectileEndCount;
        private NativeQueue<int> _deadQueue;
        private NativeList<DamageRequest> _damageScratch;
        private NativeArray<float> _controlledDamage;

        /// <summary>每个槽位最近一次发出死亡事件的帧号。用来做**同帧**去重，
        /// 而不是拿 <c>_alive</c> 当判据——两个死亡生产者在入队前就已经把 Alive 置 0 了。
        /// 帧号从 1 起，0 表示"从未发过"，因此不需要每帧清零。</summary>
        private NativeArray<int> _deathEmitFrame;
        private int _frameIndex;

        /// <summary>敌人区域攻击 / 召唤各自的冷却计时。不与 <c>_attackTimer</c> 共用——
        /// 那个已经被接触伤害与远程开火占着，一个单位同时会两三种手段时会互相踩。</summary>
        private NativeArray<float> _zoneTimer;
        private NativeArray<float> _summonTimer;

        /// <summary>内核自己生成的单位（敌人召唤物）的 LogicId 分配器，走负数段，
        /// 与热更层 <c>SimBridge.NextLogicId</c> 的正数段互不冲突。</summary>
        private int _kernelLogicId;

        /// <summary>每个槽位的召唤血统代数（见 <see cref="SpawnRequest.Generation"/>）。
        /// 用来给"召唤物自己也会召唤"封顶——没有它就是跨帧指数增殖。</summary>
        private NativeArray<byte> _generation;

        private SpatialHash _hash;
        private float _time;

        public bool IsCreated => _created;
        public SimEntityId ControlledUnitId => _created ? _controlledUnitId : SimEntityId.None;
        public int UnitCount => _unitCount;
        public float Time => _time;
        /// <summary>当前生效的障碍数量（已按 <see cref="SimConst.MaxObstacles"/> 截断）。</summary>
        public int ObstacleCount => _obstacleCount;

        public void Initialize(SimConfig cfg)
        {
            Dispose();
            _cfg = cfg;
            int cap = math.max(64, cfg.UnitCapacity);
            const Allocator A = Allocator.Persistent;

            _position = new NativeArray<float2>(cap, A);
            _velocity = new NativeArray<float2>(cap, A);
            _desiredDir = new NativeArray<float2>(cap, A);
            _separation = new NativeArray<float2>(cap, A);
            _health = new NativeArray<float>(cap, A);
            _radius = new NativeArray<float>(cap, A);
            _maxSpeed = new NativeArray<float>(cap, A);
            _attackTimer = new NativeArray<float>(cap, A);
            _archetypeId = new NativeArray<int>(cap, A);
            _status = new NativeArray<uint>(cap, A);
            _faction = new NativeArray<byte>(cap, A);
            _alive = new NativeArray<byte>(cap, A);
            _logicId = new NativeArray<int>(cap, A);
            _visualId = new NativeArray<int>(cap, A);
            _entityId = new NativeArray<SimEntityId>(cap, A);
            _intentSource = new NativeArray<byte>(cap, A);
            _intentSourceBeforePlayer = new NativeArray<byte>(cap, A);
            _unitIntents = new NativeArray<UnitIntent>(cap, A);
            _unitCommands = new NativeArray<UnitCommand>(cap, A);

            _projectiles = new NativeArray<ProjectileState>(math.max(16, cfg.ProjectileCapacity), A);
            _zones = new NativeArray<ZoneState>(math.max(16, cfg.ZoneCapacity), A);
            _zoneDamage = new NativeQueue<DamageRequest>(A);
            _zoneCursor = 0;

            _freeSlots = new NativeList<int>(cap, A);
            _pendingDeaths = new NativeList<int>(256, A);
            _hitEvents = new NativeList<HitEvent>(math.max(64, cfg.MaxHitEventsPerFrame), A);
            _deathEvents = new NativeArray<DeathEvent>(math.max(64, cfg.MaxDeathEventsPerFrame), A);
            _devourCandidates = new NativeList<int>(64, A);
            _projectileDamage = new NativeQueue<DamageRequest>(A);
            _projectileSpawns = new NativeQueue<ProjectileRequest>(A);
            _projectileEndQueue = new NativeQueue<ProjectileEndEvent>(A);
            _projectileEndEvents = new NativeArray<ProjectileEndEvent>(
                math.max(64, cfg.MaxHitEventsPerFrame), A);
            _projectileEndCount = 0;
            _deadQueue = new NativeQueue<int>(A);
            _damageScratch = new NativeList<DamageRequest>(256, A);
            _controlledDamage = new NativeArray<float>(1, A);
            _deathEmitFrame = new NativeArray<int>(cap, A);
            _frameIndex = 0;
            _zoneTimer = new NativeArray<float>(cap, A);
            _summonTimer = new NativeArray<float>(cap, A);
            _generation = new NativeArray<byte>(cap, A);
            _kernelLogicId = 0;

            _hash.Initialize(cap, cfg.HashCellSize, A);
            _archetypes = new NativeArray<BehaviorArchetype>(1, A);
            _archetypes[0] = BehaviorArchetype.Default;

            _obstaclePos = new NativeArray<float2>(SimConst.MaxObstacles, A);
            _obstacleRadius = new NativeArray<float>(SimConst.MaxObstacles, A);
            _obstacleCount = 0;

            // 兼容启动流程：默认友军从槽位 0 创建；后续控制与生命周期不依赖该槽位。
            _unitCount = 1;
            _alive[SimConst.PlayerIndex] = 1;
            _faction[SimConst.PlayerIndex] = (byte)SimFaction.Player;
            _health[SimConst.PlayerIndex] = 100f;
            _radius[SimConst.PlayerIndex] = 1f;
            _maxSpeed[SimConst.PlayerIndex] = 8f;
            _archetypeId[SimConst.PlayerIndex] = -1;
            _logicId[SimConst.PlayerIndex] = 0;
            _visualId[SimConst.PlayerIndex] = 0;
            _entityId[SimConst.PlayerIndex] = AllocateEntityId();
            _intentSource[SimConst.PlayerIndex] = (byte)IntentSource.Player;
            _intentSourceBeforePlayer[SimConst.PlayerIndex] = (byte)IntentSource.AI;
            _unitIntents[SimConst.PlayerIndex] = UnitIntent.Idle(
                _entityId[SimConst.PlayerIndex], IntentSource.Player);
            _controlledUnitId = _entityId[SimConst.PlayerIndex];

            // M1-06：控制权变更与回退锚点随世界一起重建。重进场景走的是新实例，
            // 但 Dispose 后再 Initialize 的路径也必须是干净状态，否则会把上一局的回弹事件漏给新局。
            _controlChangeCount = 0;
            _controlFallbackAnchor = _position[SimConst.PlayerIndex];
            _hasControlFallbackAnchor = true;

            _time = 0f;
            _projectileCursor = 0;
            _created = true;
        }

        /// <summary>返回当前槽位中的稳定实体身份；无效或已释放槽位返回 false。</summary>
        public bool TryGetEntityId(int unitIndex, out SimEntityId entityId)
        {
            if (_created && unitIndex >= 0 && unitIndex < _unitCount && _alive[unitIndex] != 0)
            {
                entityId = _entityId[unitIndex];
                return entityId.IsValid;
            }

            entityId = SimEntityId.None;
            return false;
        }

        /// <summary>只解析存活实体。跨帧调用方不得缓存返回的槽位索引。</summary>
        public bool TryResolveUnit(SimEntityId entityId, out int unitIndex)
        {
            if (TryFindUnit(entityId, out unitIndex))
            {
                return _alive[unitIndex] != 0;
            }

            unitIndex = SimConst.InvalidIndex;
            return false;
        }

        public bool TryGetUnitControlState(SimEntityId entityId, out SimUnitControlState state)
        {
            if (TryFindUnit(entityId, out int unitIndex))
            {
                state = new SimUnitControlState
                {
                    EntityId = entityId,
                    UnitIndex = unitIndex,
                    Faction = (SimFaction)_faction[unitIndex],
                    IntentSource = (IntentSource)_intentSource[unitIndex],
                    IsAlive = _alive[unitIndex] != 0,
                    Position = _position[unitIndex],
                };
                return true;
            }

            state = default;
            return false;
        }

        public SimControlCandidate[] GetControlCandidates(float maxDistance)
        {
            if (!_created || maxDistance < 0f)
            {
                return Array.Empty<SimControlCandidate>();
            }

            float2 origin;
            int controlledIndex;
            if (TryResolveUnit(_controlledUnitId, out int resolvedIndex))
            {
                controlledIndex = resolvedIndex;
                origin = _position[resolvedIndex];
            }
            else if (_hasControlFallbackAnchor)
            {
                // M1-06：没有受控实体时以回退锚点为原点继续给候选。
                // 否则"回弹失败 → 候选恒为空 → 再也切不回去"，玩家会永久失去控制权。
                controlledIndex = SimConst.InvalidIndex;
                origin = _controlFallbackAnchor;
            }
            else
            {
                return Array.Empty<SimControlCandidate>();
            }

            float maxDistanceSq = maxDistance * maxDistance;
            int count = 0;
            for (int i = 0; i < _unitCount; i++)
            {
                if (i == controlledIndex || _alive[i] == 0 ||
                    !IsFriendlyFaction((SimFaction)_faction[i]) ||
                    math.distancesq(origin, _position[i]) > maxDistanceSq)
                {
                    continue;
                }
                count++;
            }

            if (count == 0)
            {
                return Array.Empty<SimControlCandidate>();
            }

            var result = new SimControlCandidate[count];
            int write = 0;
            for (int i = 0; i < _unitCount; i++)
            {
                float distanceSq = math.distancesq(origin, _position[i]);
                if (i == controlledIndex || _alive[i] == 0 ||
                    !IsFriendlyFaction((SimFaction)_faction[i]) || distanceSq > maxDistanceSq)
                {
                    continue;
                }
                result[write++] = new SimControlCandidate
                {
                    EntityId = _entityId[i],
                    Faction = (SimFaction)_faction[i],
                    IntentSource = (IntentSource)_intentSource[i],
                    Position = _position[i],
                    Distance = math.sqrt(distanceSq),
                };
            }

            // 候选查询发生在显式切换操作，不在逐帧模拟热路径；稳定排序便于 UI 与测试复现。
            Array.Sort(result, (left, right) =>
            {
                int distanceOrder = left.Distance.CompareTo(right.Distance);
                return distanceOrder != 0
                    ? distanceOrder
                    : left.EntityId.Value.CompareTo(right.EntityId.Value);
            });
            return result;
        }

        public ControlSwitchResult TrySwitchControlledUnit(SimEntityId entityId)
        {
            return SwitchControlledUnitInternal(entityId, ControlChangeReason.PlayerRequest);
        }

        /// <summary>
        /// M1-06：读档 / 重进场景后的控制权恢复。
        ///
        /// 与 <see cref="TrySwitchControlledUnit"/> 只差**语义**：恢复不是一次玩家操作，
        /// 因此不参与桥接层的冷却与信号范围约束；身份、存活、阵营三道校验一条不少。
        /// 恢复到当前已受控的实体时返回 AlreadyControlled 且不产生变更事件——
        /// 重复读档不该刷出一串意识转场。
        /// </summary>
        public ControlSwitchResult TryRestoreControlledUnit(SimEntityId entityId)
        {
            return SwitchControlledUnitInternal(entityId, ControlChangeReason.Restored);
        }

        private ControlSwitchResult SwitchControlledUnitInternal(SimEntityId entityId, ControlChangeReason reason)
        {
            if (!_created) { return ControlSwitchResult.WorldNotInitialized; }
            if (!entityId.IsValid) { return ControlSwitchResult.InvalidTarget; }
            if (!TryFindUnit(entityId, out int targetIndex)) { return ControlSwitchResult.TargetNotFound; }
            if (_alive[targetIndex] == 0) { return ControlSwitchResult.TargetDead; }
            if (!IsFriendlyFaction((SimFaction)_faction[targetIndex]))
            {
                return ControlSwitchResult.TargetNotFriendly;
            }
            if (_controlledUnitId == entityId) { return ControlSwitchResult.AlreadyControlled; }

            SimEntityId previousId = _controlledUnitId;
            float2 anchor = _position[targetIndex];
            if (TryFindUnit(_controlledUnitId, out int previousIndex) && _alive[previousIndex] != 0)
            {
                _intentSource[previousIndex] = _intentSourceBeforePlayer[previousIndex];
                anchor = _position[previousIndex];
            }

            _intentSourceBeforePlayer[targetIndex] = _intentSource[targetIndex] == (byte)IntentSource.Player
                ? (byte)IntentSource.AI
                : _intentSource[targetIndex];
            _intentSource[targetIndex] = (byte)IntentSource.Player;
            _controlledUnitId = entityId;
            _controlFallbackAnchor = _position[targetIndex];
            _hasControlFallbackAnchor = true;
            RecordControlChange(previousId, entityId, reason, anchor);
            return ControlSwitchResult.Success;
        }

        // ── 控制权变更出口（M1-06）──

        /// <summary>
        /// 意识回弹的最大距离。桥接层应当用当前信号范围写入，使自动回弹与手动切换共用同一把尺子；
        /// 非正值 = 不限距离（回归测试里构造"必定能回弹"的场景时用）。
        /// </summary>
        public float ControlFallbackRange
        {
            get => _controlFallbackRange;
            set => _controlFallbackRange = value;
        }

        /// <summary>
        /// 是否启用意识回弹。默认开启。关掉后受控实体一死就确定地落到"无控制"，
        /// 变更事件照发（<see cref="ControlChangeEvent.CurrentUnitId"/> 为 None），
        /// 表现层据此切到回退视角。
        /// </summary>
        public bool ControlFallbackEnabled
        {
            get => _controlFallbackEnabled;
            set => _controlFallbackEnabled = value;
        }

        /// <summary>最后一个有效受控实体的位置。无控制实体时表现层的战略回退视角就取这里。</summary>
        public bool TryGetControlFallbackAnchor(out float2 anchor)
        {
            anchor = _controlFallbackAnchor;
            return _created && _hasControlFallbackAnchor;
        }

        /// <summary>
        /// 取出一条待处理的控制权变更（FIFO），没有则返回 false。
        ///
        /// 内核不认识热更层的信号系统，所以变更在这里排队、由 <c>SimBridge</c> 消费后转成一次事件。
        /// **消费即出队**：同一条变更只会被发布一次，这正是 M1-04「合法切换只产生一次事件」
        /// 在自动回弹路径上的延续。
        /// </summary>
        public bool TryConsumeControlChange(out ControlChangeEvent change)
        {
            if (_created && _controlChangeCount > 0)
            {
                change = _controlChanges[0];
                _controlChangeCount--;
                for (int i = 0; i < _controlChangeCount; i++)
                {
                    _controlChanges[i] = _controlChanges[i + 1];
                }
                return true;
            }

            change = default;
            return false;
        }

        private void RecordControlChange(SimEntityId previousId, SimEntityId currentId,
            ControlChangeReason reason, float2 fallbackAnchor)
        {
            if (previousId == currentId)
            {
                return;
            }

            if (_controlChangeCount >= _controlChanges.Length)
            {
                // 队列满：丢最旧的一条。控制状态的最终真相永远是最后一条，
                // 丢掉中间过程比丢掉"现在归谁"要安全得多。
                for (int i = 1; i < _controlChangeCount; i++)
                {
                    _controlChanges[i - 1] = _controlChanges[i];
                }
                _controlChangeCount = _controlChanges.Length - 1;
            }

            _controlChanges[_controlChangeCount++] = new ControlChangeEvent
            {
                PreviousUnitId = previousId,
                CurrentUnitId = currentId,
                Reason = reason,
                FallbackAnchor = fallbackAnchor,
            };
        }

        // ── RTS 命令与选择（M2-02）──

        /// <summary>撤退命令的威胁感知半径。非正值表示撤退时不躲避敌人，直线奔向目标点。</summary>
        public float RetreatThreatRange
        {
            get => _retreatThreatRange;
            set => _retreatThreatRange = value;
        }

        /// <summary>
        /// 给一批单位下达命令。**一次性写入**，不是每帧派发——
        /// 编队再大也只在下令那一刻付出 O(选中数)，逐帧代价由内核的并行作业承担。
        /// </summary>
        /// <returns>实际接受命令的单位数。</returns>
        public int IssueCommand(SimEntityId[] targets, in UnitCommand command)
        {
            if (!_created || targets == null || targets.Length == 0)
            {
                return 0;
            }

            int accepted = 0;
            for (int t = 0; t < targets.Length; t++)
            {
                if (!TryResolveUnit(targets[t], out int idx))
                {
                    continue;
                }
                // 只指挥友军，且**绝不夺走玩家直控的那一个**——那会让受控实体在玩家手里
                // 突然自己跑起来，也会破坏"恰好一个 Player 意图来源"的不变量。
                if (!IsFriendlyFaction((SimFaction)_faction[idx]) ||
                    _intentSource[idx] == (byte)IntentSource.Player)
                {
                    continue;
                }

                _unitCommands[idx] = command;
                _intentSource[idx] = command.Kind == UnitCommandKind.None
                    ? (byte)IntentSource.AI
                    : (byte)IntentSource.Commanded;
                accepted++;
            }

            return accepted;
        }

        /// <summary>撤销命令，把单位交还 AI。</summary>
        public bool ClearCommand(SimEntityId entityId)
        {
            if (!_created || !TryResolveUnit(entityId, out int idx) ||
                _intentSource[idx] != (byte)IntentSource.Commanded)
            {
                return false;
            }

            _unitCommands[idx] = UnitCommand.None;
            _intentSource[idx] = (byte)IntentSource.AI;
            return true;
        }

        /// <summary>查询某个单位当前的命令。没有命令时返回 <see cref="UnitCommandKind.None"/>。</summary>
        public bool TryGetCommand(SimEntityId entityId, out UnitCommand command)
        {
            if (_created && TryResolveUnit(entityId, out int idx))
            {
                command = _unitCommands[idx];
                return command.Kind != UnitCommandKind.None;
            }

            command = UnitCommand.None;
            return false;
        }

        /// <summary>
        /// 矩形框选（M2-02）。世界 XZ 平面的轴对齐矩形。
        ///
        /// 逐单位线性扫描发生在 AOT 侧、且只在鼠标松开时触发一次（非逐帧），
        /// 与 <see cref="GetControlCandidates"/> 同一约定，不违反"热更层每帧不得 O(N)"。
        /// 结果按稳定实体 ID 升序，保证同样的框选得到同样的顺序。
        /// </summary>
        /// <param name="commandableOnly">只要可指挥单位：友军、存活、且不是玩家正在直控的那一个。</param>
        public SimUnitPick[] QueryUnitsInRect(float2 min, float2 max, bool commandableOnly = true)
        {
            if (!_created)
            {
                return Array.Empty<SimUnitPick>();
            }

            float2 lo = math.min(min, max);
            float2 hi = math.max(min, max);

            int count = 0;
            for (int i = 0; i < _unitCount && count < SimConst.MaxSelectionSize; i++)
            {
                if (MatchesPick(i, lo, hi, commandableOnly)) { count++; }
            }

            if (count == 0)
            {
                return Array.Empty<SimUnitPick>();
            }

            var result = new SimUnitPick[count];
            int write = 0;
            for (int i = 0; i < _unitCount && write < count; i++)
            {
                if (!MatchesPick(i, lo, hi, commandableOnly))
                {
                    continue;
                }
                result[write++] = new SimUnitPick
                {
                    EntityId = _entityId[i],
                    UnitIndex = i,
                    Position = _position[i],
                    Faction = (SimFaction)_faction[i],
                    IntentSource = (IntentSource)_intentSource[i],
                };
            }

            Array.Sort(result, (a, b) => a.EntityId.Value.CompareTo(b.EntityId.Value));
            return result;
        }

        private bool MatchesPick(int i, float2 lo, float2 hi, bool commandableOnly)
        {
            if (_alive[i] == 0)
            {
                return false;
            }

            float2 p = _position[i];
            if (p.x < lo.x || p.x > hi.x || p.y < lo.y || p.y > hi.y)
            {
                return false;
            }

            if (!commandableOnly)
            {
                return true;
            }

            // 玩家直控的那一个不进选择集：它已经归玩家的手直接操作，
            // 再被编队命令拖走就是两套输入抢同一个单位。
            return IsFriendlyFaction((SimFaction)_faction[i]) &&
                   _intentSource[i] != (byte)IntentSource.Player;
        }

        private SimEntityId AllocateEntityId()
        {
            _nextEntityId++;
            if (_nextEntityId == 0UL)
            {
                _nextEntityId++;
            }
            return new SimEntityId(_nextEntityId);
        }

        private bool TryFindUnit(SimEntityId entityId, out int unitIndex)
        {
            if (_created && entityId.IsValid)
            {
                for (int i = 0; i < _unitCount; i++)
                {
                    if (_entityId[i] == entityId)
                    {
                        unitIndex = i;
                        return true;
                    }
                }
            }

            unitIndex = SimConst.InvalidIndex;
            return false;
        }

        private static bool IsFriendlyFaction(SimFaction faction)
        {
            return faction == SimFaction.Player || faction == SimFaction.PlayerMinion;
        }

        public void SetArchetypes(BehaviorArchetype[] archetypes)
        {
            if (_archetypes.IsCreated)
            {
                _archetypes.Dispose();
            }
            int n = archetypes != null && archetypes.Length > 0 ? archetypes.Length : 1;
            _archetypes = new NativeArray<BehaviorArchetype>(n, Allocator.Persistent);
            if (archetypes != null && archetypes.Length > 0)
            {
                for (int i = 0; i < archetypes.Length; i++)
                {
                    _archetypes[i] = archetypes[i];
                }
            }
            else
            {
                _archetypes[0] = BehaviorArchetype.Default;
            }
        }

        /// <summary>加载静态障碍布局（story-009）。容量固定，超出 <see cref="SimConst.MaxObstacles"/> 的部分丢弃。</summary>
        public void SetObstacles(ObstacleSpec[] obstacles)
        {
            if (!_obstaclePos.IsCreated)
            {
                return;
            }
            int n = obstacles != null ? math.min(obstacles.Length, SimConst.MaxObstacles) : 0;
            for (int i = 0; i < n; i++)
            {
                _obstaclePos[i] = obstacles[i].Position;
                _obstacleRadius[i] = obstacles[i].Radius;
            }
            _obstacleCount = n;
        }

        // ── 当前受控实体兼容读写（热更层通过 SimBridge 调用，不直接碰数组）──

        /// <summary>
        /// 玩家受伤倍率（护甲/减伤）。热更层的 <c>StatId.DamageTaken</c> 推下来。
        ///
        /// 为什么要推下来：本帧打到当前受控实体的伤害由内核统一结算。
        /// （见 <see cref="Step"/> 末尾）。此前是内核只累加、等热更层某个系统读走再回头调
        /// <c>DamagePlayer</c>——那意味着少一个消费者，敌人的伤害就静默消失。
        /// 减伤是玩法数值、结算是内核职责，把数值推下去比把结算提上来安全。
        /// </summary>
        public float PlayerDamageTakenMul
        {
            get { return _playerDamageTakenMul; }
            set { _playerDamageTakenMul = math.max(0f, value); }
        }

        private float _playerDamageTakenMul = 1f;

        public void SetPlayerStats(float maxHp, float currentHp, float radius, float maxSpeed)
        {
            if (!TryResolveUnit(_controlledUnitId, out int controlledIndex)) { return; }
            _health[controlledIndex] = math.min(currentHp, maxHp);
            _radius[controlledIndex] = math.max(0.1f, radius);
            _maxSpeed[controlledIndex] = math.max(0.1f, maxSpeed);
        }

        /// <summary>任务二（3D 表现差异化）：Carrier 装配变化时切换玩家渲染造型。</summary>
        public void SetPlayerVisualId(int visualId)
        {
            if (TryResolveUnit(_controlledUnitId, out int controlledIndex))
            {
                _visualId[controlledIndex] = visualId;
            }
        }

        public bool SetUnitVisualId(SimEntityId entityId, int visualId)
        {
            if (!TryResolveUnit(entityId, out int unitIndex)) { return false; }
            _visualId[unitIndex] = visualId;
            return true;
        }

        public float PlayerHealth => TryResolveUnit(_controlledUnitId, out int index) ? _health[index] : 0f;
        public float2 PlayerPosition => TryResolveUnit(_controlledUnitId, out int index) ? _position[index] : float2.zero;
        public float PlayerRadius => TryResolveUnit(_controlledUnitId, out int index) ? _radius[index] : 1f;

        public void DamagePlayer(float amount)
        {
            if (amount <= 0f || !TryResolveUnit(_controlledUnitId, out int controlledIndex)) { return; }
            if ((_status[controlledIndex] & (uint)SimStatus.Invulnerable) != 0u) { return; }
            _health[controlledIndex] -= amount;
        }

        public void HealPlayer(float amount, float maxHp)
        {
            if (amount <= 0f || !TryResolveUnit(_controlledUnitId, out int controlledIndex)) { return; }
            _health[controlledIndex] = math.min(_health[controlledIndex] + amount, maxHp);
        }

        public void SetPlayerPosition(float2 pos)
        {
            if (TryResolveUnit(_controlledUnitId, out int controlledIndex))
            {
                _position[controlledIndex] = pos;
            }
        }

        /// <summary>
        /// chassis-native-primitives：把 <paramref name="origin"/> 半径内的目标沿背离方向推开。
        ///
        /// 在此之前 <c>HitEvent.Knockback</c> 只写日志——内核没有任何"位移某个单位"的公共入口，
        /// 于是击退在整个游戏里不存在，近战被围住时没有任何解法。这里补的就是那个入口。
        ///
        /// 直接改 <c>_position</c> 而不是给速度加冲量：单位每帧由 <c>JobIntegrate</c> 按
        /// desiredDir 重算速度，冲量会在下一帧被完全覆盖掉，只有位移是留得住的。
        /// 结果夹回场地边界，避免把敌人推到墙外。
        ///
        /// 主线程 O(UnitCount) 单趟：调用方是"一次挥击"而不是"每帧"，且按仓库红线逐单位 O(N)
        /// 本就只允许发生在 AOT 侧（热更层只发一次调用，不自己遍历敌人）。
        /// </summary>
        /// <returns>实际被推动的单位数。</returns>
        public int ApplyKnockback(float2 origin, float radius, float distance, SimFaction target)
        {
            if (!_created || radius <= 0f || distance <= 0f) { return 0; }

            float half = _cfg.ArenaHalfExtent;
            float r2 = radius * radius;
            int moved = 0;
            TryResolveUnit(_controlledUnitId, out int controlledIndex);
            for (int i = 0; i < _unitCount; i++)
            {
                if (i == controlledIndex || _alive[i] == 0) { continue; }
                if (target != SimFaction.None && (SimFaction)_faction[i] != target) { continue; }

                float2 delta = _position[i] - origin;
                float d2 = math.lengthsq(delta);
                if (d2 > r2) { continue; }

                // 圆心处方向无定义，退化成"沿 +Y 推开"而不是产生 NaN。
                float2 dir = d2 > 1e-6f ? delta * math.rsqrt(d2) : new float2(0f, 1f);
                _position[i] = math.clamp(_position[i] + dir * distance,
                    new float2(-half, -half), new float2(half, half));
                moved++;
            }
            return moved;
        }

        /// <summary>
        /// enemy-ranged-and-parry：把范围（可选扇形）内**朝你飞来的**弹体打回去。
        ///
        /// 弹反做三件事：调头、换阵营、改归属。改归属是关键——
        /// <see cref="ProjectileState.SourceLogicId"/> 换成 <paramref name="newSourceLogicId"/> 之后，
        /// 弹回去打死人算**你**的击杀，热更层的 shot 元数据（留坑/吸血认领）也认得出这一发。
        /// 顺带把伤害放大 <paramref name="damageMul"/> 倍——挡下来还打不疼的话没人会去挡。
        ///
        /// 只弹 <c>TargetFaction != 施法者阵营</c> 的弹体，也就是"冲我来的"那些；
        /// 自己打出去的弹不会被自己的挥击弹回来。
        ///
        /// O(弹体容量) 单趟。调用方是"一次挥击"不是每帧。
        /// </summary>
        /// <param name="coneDir">扇形中轴；零向量表示整圆。</param>
        /// <param name="coneCosHalf">扇形半角余弦，仅 <paramref name="coneDir"/> 非零时生效。</param>
        /// <returns>实际弹回的弹体数。</returns>
        public int DeflectProjectiles(float2 origin, float radius, float2 coneDir, float coneCosHalf,
            SimFaction ownFaction, int newSourceLogicId, SimFaction newTargetFaction, float damageMul)
        {
            if (!_created || !_projectiles.IsCreated || radius <= 0f)
            {
                return 0;
            }

            bool useCone = math.lengthsq(coneDir) > 1e-6f;
            float2 axis = useCone ? math.normalize(coneDir) : default;
            float r2 = radius * radius;
            int deflected = 0;

            for (int p = 0; p < _projectiles.Length; p++)
            {
                ProjectileState s = _projectiles[p];
                if (s.Alive == 0)
                {
                    continue;
                }
                // 只弹"冲我来的"：目标阵营是我自己的那些。
                if ((SimFaction)s.TargetFaction != ownFaction)
                {
                    continue;
                }

                float2 delta = s.Position - origin;
                float d2 = math.lengthsq(delta);
                if (d2 > r2)
                {
                    continue;
                }
                if (useCone && d2 > 1e-6f
                    && math.dot(delta * math.rsqrt(d2), axis) < coneCosHalf)
                {
                    continue;
                }

                s.Velocity = -s.Velocity;
                s.TargetFaction = (byte)newTargetFaction;
                s.SourceLogicId = newSourceLogicId;
                s.Damage *= damageMul;
                s.LastHitIndex = SimConst.InvalidIndex;
                s.HitCooldown = 0f;
                // 弹回去的弹重新获得一次命中机会，否则刚被打空穿透的弹弹回来也打不着人。
                s.PierceLeft = math.max(1, s.PierceLeft);
                // 追踪要重新校准到新阵营；原来的追踪参数照留（弹回去的毒刺依然会拐弯）。
                _projectiles[p] = s;
                deflected++;
            }

            return deflected;
        }

        // ── 帧推进 ──

        public void Step(float dt, ref SimCommandBuffer cmds)
        {
            if (!_created)
            {
                return;
            }

            // 夹住 dt：卡帧时不让单位瞬移穿过玩家
            dt = math.clamp(dt, 0f, 0.05f);
            _time += dt;
            _frameIndex++;

            _pendingDeaths.Clear();
            _hitEvents.Clear();
            _devourCandidates.Clear();
            _damageScratch.Clear();
            _deathCount = 0;
            _controlledDamage[0] = 0f;

            ApplyCommands(ref cmds);
            PrepareUnitIntents(ref cmds);
            bool hasControlledUnit = TryResolveUnit(_controlledUnitId, out int controlledIndex);
            float2 controlledPos = hasControlledUnit ? _position[controlledIndex] : float2.zero;
            float controlledRadius = hasControlledUnit ? _radius[controlledIndex] : 0f;

            // ── job 链 ──
            JobHandle h = _hash.Rebuild(_position, _alive, _unitCount, default);

            var aiIntent = new JobAIIntent
            {
                Position = _position,
                Velocity = _velocity,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                ArchetypeId = _archetypeId,
                IntentSource = _intentSource,
                EntityId = _entityId,
                Status = _status,
                Archetypes = _archetypes,
                Intents = _unitIntents,
                AttackTimer = _attackTimer,
                TargetPos = controlledPos,
                HasTarget = hasControlledUnit,
                Time = _time,
                Dt = dt,
                Count = _unitCount,
                ArenaHalf = _cfg.ArenaHalfExtent,
                Hash = _hash.Map,
                InvCellSize = _hash.InvCellSize,
            };
            h = aiIntent.Schedule(_unitCount, 64, h);

            // M2-02：命令 → 意图。必须排在 JobAIIntent 之后、JobSteering 之前：
            // 前者只认 AI 槽位、本作业只认 Commanded 槽位，两者不相交；而命令完成时
            // 本作业会把槽位就地交还 AI，那一帧的意图由它自己写成 Idle（见 ReleaseToAi）。
            var commandIntent = new JobCommandIntent
            {
                Position = _position,
                Faction = _faction,
                Alive = _alive,
                EntityId = _entityId,
                Status = _status,
                Hash = _hash.Map,
                InvCellSize = _hash.InvCellSize,
                Commands = _unitCommands,
                IntentSource = _intentSource,
                Intents = _unitIntents,
                Count = _unitCount,
                RetreatThreatRange = _retreatThreatRange,
            };
            h = commandIntent.Schedule(_unitCount, 64, h);

            var steering = new JobSteering
            {
                Intents = _unitIntents,
                Alive = _alive,
                DesiredDir = _desiredDir,
                Status = _status,
                Radius = _radius,
                Count = _unitCount,
            };
            h = steering.Schedule(_unitCount, 64, h);

            var separation = new JobSeparation
            {
                Position = _position,
                Radius = _radius,
                Alive = _alive,
                Faction = _faction,
                ArchetypeId = _archetypeId,
                Archetypes = _archetypes,
                Hash = _hash.Map,
                SeparationForce = _separation,
                InvCellSize = _hash.InvCellSize,
                Count = _unitCount,
                MaxNeighbors = 8,
            };
            h = separation.Schedule(_unitCount, 64, h);

            var integrate = new JobIntegrate
            {
                DesiredDir = _desiredDir,
                SeparationForce = _separation,
                MaxSpeed = _maxSpeed,
                Intents = _unitIntents,
                Alive = _alive,
                Status = _status,
                ArchetypeId = _archetypeId,
                Archetypes = _archetypes,
                AttackTimer = _attackTimer,
                Radius = _radius,
                ObstaclePos = _obstaclePos,
                ObstacleRadius = _obstacleRadius,
                ObstacleCount = _obstacleCount,
                Position = _position,
                Velocity = _velocity,
                Dt = dt,
                Count = _unitCount,
                ArenaHalf = _cfg.ArenaHalfExtent,
                SlowMul = 0.5f,
            };
            h = integrate.Schedule(_unitCount, 64, h);
            h.Complete();

            // 位置变了，重建哈希供后续查询使用
            _hash.Rebuild(_position, _alive, _unitCount, default).Complete();

            ResolveMinionCombat(dt);
            // 敌人开火要在 JobProjectile 之前：本帧生成的弹体本帧就开始飞，
            // 与命令缓冲里玩家发的弹同一时序。
            ResolveHostileRangedCombat(dt, controlledIndex);
            ResolveHostileAbilities(dt, controlledIndex);

            if (hasControlledUnit && _alive[controlledIndex] != 0)
            {
                controlledPos = _position[controlledIndex];
                controlledRadius = _radius[controlledIndex];
            }

            // 投射物：命中转为 DamageRequest
            var proj = new JobProjectile
            {
                Position = _position,
                Radius = _radius,
                Faction = _faction,
                Status = _status,
                Alive = _alive,
                Hash = _hash.Map,
                ObstaclePos = _obstaclePos,
                ObstacleRadius = _obstacleRadius,
                ObstacleCount = _obstacleCount,
                Projectiles = _projectiles,
                DamageOut = _projectileDamage.AsParallelWriter(),
                SpawnOut = _projectileSpawns.AsParallelWriter(),
                EndOut = _projectileEndQueue.AsParallelWriter(),
                Dt = dt,
                InvCellSize = _hash.InvCellSize,
                UnitCount = _unitCount,
                ArenaHalf = _cfg.ArenaHalfExtent,
                OwnerPos = controlledPos,
            };
            proj.Schedule(_projectiles.Length, 32, default).Complete();

            // combat-primitive-overhaul：Job 产出的二级弹体（分裂/回旋）在主线程落槽。
            // 本帧生成、下一帧才推进——与命令缓冲里的弹体同一时序，不需要额外补偿。
            while (_projectileSpawns.TryDequeue(out ProjectileRequest pr))
            {
                SpawnProjectile(pr);
            }

            // 弹体终结事件搬进快照数组，热更层在**真实**落点放留坑/命中表现。
            _projectileEndCount = 0;
            while (_projectileEndQueue.TryDequeue(out ProjectileEndEvent pe))
            {
                if (_projectileEndCount < _projectileEndEvents.Length)
                {
                    _projectileEndEvents[_projectileEndCount++] = pe;
                }
            }

            // 持续区域：跟随宿主 / 外扩 / 到点跳伤。产出的伤害与弹体伤害并入同一批
            // DamageRequest，走既有 JobDamage 结算——区域杀敌同样会走击杀奖励与卡牌 OnKill。
            var zone = new JobZone
            {
                Zones = _zones,
                Position = _position,
                Alive = _alive,
                UnitCount = _unitCount,
                DamageOut = _zoneDamage.AsParallelWriter(),
                Dt = dt,
            };
            zone.Schedule(_zones.Length, 32, default).Complete();

            // 汇总伤害请求：命令缓冲里的 + 投射物产生的 + 持续区域产生的
            for (int i = 0; i < cmds.Damages.Length; i++)
            {
                _damageScratch.Add(cmds.Damages[i]);
            }
            while (_projectileDamage.TryDequeue(out DamageRequest dr))
            {
                _damageScratch.Add(dr);
            }
            while (_zoneDamage.TryDequeue(out DamageRequest zr))
            {
                _damageScratch.Add(zr);
            }

            if (_damageScratch.Length > 0)
            {
                var dmg = new JobDamage
                {
                    Requests = _damageScratch.AsArray(),
                    Position = _position,
                    Radius = _radius,
                    Faction = _faction,
                    LogicId = _logicId,
                    ArchetypeId = _archetypeId,
                    Hash = _hash.Map,
                    Health = _health,
                    Status = _status,
                    Alive = _alive,
                    PendingDeaths = _pendingDeaths,
                    HitEvents = _hitEvents,
                    ControlledDamageOut = _controlledDamage,
                    ControlledUnitIndex = hasControlledUnit ? controlledIndex : SimConst.InvalidIndex,
                    InvCellSize = _hash.InvCellSize,
                    Count = _unitCount,
                    MaxHitEvents = _cfg.MaxHitEventsPerFrame,
                    VulnerableMul = 1.35f,
                    HardenedMul = 0.6f,
                };
                dmg.Schedule().Complete();
            }

            var contact = new JobContactDamage
            {
                Position = _position,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                ArchetypeId = _archetypeId,
                Archetypes = _archetypes,
                Hash = _hash.Map,
                AttackTimer = _attackTimer,
                ControlledDamageOut = _controlledDamage,
                TargetIndex = hasControlledUnit ? controlledIndex : SimConst.InvalidIndex,
                TargetPos = controlledPos,
                TargetRadius = controlledRadius,
                InvCellSize = _hash.InvCellSize,
                Count = _unitCount,
                Dt = dt,
            };
            contact.Schedule().Complete();

            // ── 受控实体受伤：内核统一结算 ────────────────────────────────────
            // 本帧所有打到当前受控实体的伤害都累加进 _controlledDamage[0]。
            // 内核直接扣血，热更层只负责推减伤倍率（PlayerDamageTakenMul）与播受伤反馈。
            // 快照里回报的是**已减伤后的最终值**，热更层照它记账即可，不要再扣一次。
            if (hasControlledUnit && _alive[controlledIndex] != 0 && _controlledDamage[0] > 0f)
            {
                float taken = _controlledDamage[0] * _playerDamageTakenMul;
                if ((_status[controlledIndex] & (uint)SimStatus.Invulnerable) != 0u)
                {
                    taken = 0f;
                }
                _controlledDamage[0] = taken;
                if (taken > 0f)
                {
                    _health[controlledIndex] -= taken;
                }
            }

            var devour = new JobDevourScan
            {
                Position = _position,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                Status = _status,
                Hash = _hash.Map,
                Candidates = _devourCandidates,
                ActorIndex = hasControlledUnit ? controlledIndex : SimConst.InvalidIndex,
                ActorPos = controlledPos,
                ActorRadius = controlledRadius,
                InvCellSize = _hash.InvCellSize,
                Count = _unitCount,
                DevourRatio = 1.05f,
                BreachedDiscount = _cfg.BreachedDiscount,
                CorrodedDiscount = _cfg.CorrodedDiscount,
                ContactSlack = 0.35f,
            };
            devour.Schedule().Complete();

            var collect = new JobCollectDeaths
            {
                Health = _health,
                Alive = _alive,
                DeadOut = _deadQueue.AsParallelWriter(),
                Count = _unitCount,
            };
            collect.Schedule(_unitCount, 64, default).Complete();

            // 死亡：生成事件并回收槽位（主线程，需写自由列表）
            for (int i = 0; i < _pendingDeaths.Length; i++)
            {
                EmitDeath(_pendingDeaths[i]);
            }
            while (_deadQueue.TryDequeue(out int idx))
            {
                EmitDeath(idx);
            }

            cmds.Clear();
        }

        /// <summary>
        /// 把 Player/Scripted 命令编译到按槽位对齐的最终意图缓冲。AI 槽位随后由
        /// <see cref="JobAIIntent"/> 写入同一结构；来源不匹配或身份失效的命令被确定性忽略。
        /// </summary>
        private void PrepareUnitIntents(ref SimCommandBuffer cmds)
        {
            for (int i = 0; i < _unitCount; i++)
            {
                if (_alive[i] == 0) { continue; }
                _unitIntents[i] = UnitIntent.Idle(_entityId[i], (IntentSource)_intentSource[i]);
            }

            if (!cmds.IsCreated) { return; }
            for (int i = 0; i < cmds.Intents.Length; i++)
            {
                UnitIntent intent = cmds.Intents[i];
                SimEntityId targetId = intent.EntityId;
                if (!targetId.IsValid && intent.Source == IntentSource.Player)
                {
                    targetId = _controlledUnitId;
                }
                if (!TryResolveUnit(targetId, out int targetIndex)) { continue; }
                if (_intentSource[targetIndex] != (byte)intent.Source) { continue; }
                if (intent.Source == IntentSource.Player && targetId != _controlledUnitId) { continue; }

                intent.EntityId = targetId;
                _unitIntents[targetIndex] = intent;
            }
        }

        /// <summary>
        /// 玩家召唤物（PlayerMinion）攻击结算。数量恒被 MinionCap 卡在个位数，
        /// 主线程线性扫描即可，不需要额外 Burst job（召唤机制 story）。
        /// 只把伤害/自毁写进 <see cref="_damageScratch"/>，复用下面 JobDamage 的统一结算，
        /// 不新开一条死亡/命中事件路径。
        /// </summary>
        private void ResolveMinionCombat(float dt)
        {
            for (int i = 0; i < _unitCount; i++)
            {
                // M2-02：判据是"不是玩家直控"，不是"是 AI"。
                // 原先写死 != AI，导致一旦给召唤物下了 RTS 命令（IntentSource 变成 Commanded），
                // 它就**彻底停止攻击**——下了"攻击"命令的单位反而不打人，正是这条过滤造成的。
                // 玩家直控的单位自己走技能系统开火，所以只排除它。
                if (_alive[i] == 0 || _intentSource[i] == (byte)IntentSource.Player
                    || (SimFaction)_faction[i] != SimFaction.PlayerMinion)
                {
                    continue;
                }

                int aid = _archetypeId[i];
                if (aid < 0 || aid >= _archetypes.Length)
                {
                    continue;
                }

                BehaviorArchetype arc = _archetypes[aid];
                if (arc.Kind != BehaviorKind.MinionSeekAttack && arc.Kind != BehaviorKind.MinionSeekExplode)
                {
                    continue;
                }

                if (_attackTimer[i] > 0f)
                {
                    _attackTimer[i] -= dt;
                }

                var hashMap = _hash.Map;
                bool found = MinionTargetingUtil.TryFindNearestHostile(
                    in hashMap, _hash.InvCellSize, _position, _alive, _faction, _unitCount,
                    _position[i], arc.AggroRange, i, out int targetIdx, out float2 targetPos);
                if (!found)
                {
                    continue;
                }

                float engageRange = arc.AttackRange + _radius[i] + _radius[targetIdx];
                if (math.distance(_position[i], targetPos) > engageRange)
                {
                    continue;
                }

                if (arc.Kind == BehaviorKind.MinionSeekExplode)
                {
                    // 爆炸半径复用 PreferredRange（省一个专属字段），命中后自毁走标准伤害管线。
                    _damageScratch.Add(new DamageRequest
                    {
                        Origin = _position[i],
                        Radius = math.max(0.5f, arc.PreferredRange),
                        TargetIndex = -1,
                        Amount = arc.AttackDamage,
                        TargetFaction = SimFaction.Hostile,
                        SourceLogicId = _logicId[i],
                    });
                    _damageScratch.Add(new DamageRequest
                    {
                        Origin = _position[i],
                        Radius = -1f,
                        TargetIndex = i,
                        Amount = _health[i] + 999f,
                        TargetFaction = SimFaction.PlayerMinion,
                        SourceLogicId = _logicId[i],
                    });
                }
                else if (_attackTimer[i] <= 0f)
                {
                    _damageScratch.Add(new DamageRequest
                    {
                        Origin = _position[i],
                        Radius = -1f,
                        TargetIndex = targetIdx,
                        Amount = arc.AttackDamage,
                        TargetFaction = SimFaction.Hostile,
                        SourceLogicId = _logicId[i],
                    });
                    _attackTimer[i] = arc.AttackCooldown;
                }
            }
        }

        /// <summary>敌人弹体的默认碰撞半径（原型未配 RangedRadius 时）。</summary>
        private const float HostileProjectileRadius = 0.35f;
        /// <summary>敌人弹体寿命 = 射程 / 速度 再留一点余量，让"擦着边缘飞过去"也成立。</summary>
        private const float HostileProjectileLifetimeSlack = 1.25f;
        /// <summary>敌人弹体的追踪角速度上限（度/秒）。刻意远低于玩家的 90+270×强度——
        /// 敌人的弹必须躲得掉，否则"远程压力"就变成了"必中的税"。</summary>
        private const float HostileHomingTurnRateDeg = 110f;
        /// <summary>敌人弹体染色（RGBA8）。统一暖红，与玩家弹体的元素配色区分开——
        /// 玩家必须一眼看出"这发是冲我来的"。</summary>
        private const uint HostileProjectileTint = 0xFF5A3CFFu;

        /// <summary>
        /// enemy-ranged-and-parry：敌人的远程攻击结算。
        ///
        /// 在此之前敌人**只有接触伤害**——`BehaviorKind.Ranged` 只让它保持距离，从不发射任何东西，
        /// 所谓"远程"是站在 AttackRange 外隐形扣血。现在填了 <see cref="BehaviorArchetype.RangedSpeed"/>
        /// 的原型改发真弹体：看得见、躲得掉、能被打断、**能被弹反**。
        ///
        /// 与 <see cref="ResolveMinionCombat"/> 同样放主线程线性扫描：一帧一趟 O(UnitCount)，
        /// 和已有的 <see cref="JobContactDamage"/> 同数量级，不值得为它单开一个 Burst job。
        /// </summary>
        private void ResolveHostileRangedCombat(float dt, int controlledIndex)
        {
            if (controlledIndex < 0 || controlledIndex >= _unitCount || _alive[controlledIndex] == 0)
            {
                return;
            }

            float2 controlledPos = _position[controlledIndex];
            SimFaction controlledFaction = (SimFaction)_faction[controlledIndex];

            for (int i = 0; i < _unitCount; i++)
            {
                if (_alive[i] == 0 || (SimFaction)_faction[i] != SimFaction.Hostile)
                {
                    continue;
                }

                int aid = _archetypeId[i];
                if (aid < 0 || aid >= _archetypes.Length)
                {
                    continue;
                }

                BehaviorArchetype arc = _archetypes[aid];
                if (arc.RangedSpeed <= 0f || arc.AttackDamage <= 0f)
                {
                    continue;
                }

                // 注意：这个计时器与 JobContactDamage 共用。但会发射弹体的原型已被那边跳过，
                // 所以这里独占它，不存在"摸到你就把射击 CD 也重置了"的串扰。
                if (_attackTimer[i] > 0f)
                {
                    _attackTimer[i] -= dt;
                    continue;
                }

                float range = arc.AttackRange > 0f ? arc.AttackRange : arc.AggroRange;
                if (range <= 0f)
                {
                    continue;
                }

                float2 to = controlledPos - _position[i];
                float dist = math.length(to);
                if (dist > range || dist < 1e-4f)
                {
                    continue;
                }

                float2 aim = to / dist;
                int shots = math.max(1, (int)math.round(arc.RangedCount));
                float radius = arc.RangedRadius > 0f ? arc.RangedRadius : HostileProjectileRadius;
                float lifetime = math.clamp(range / arc.RangedSpeed * HostileProjectileLifetimeSlack, 0.2f, 6f);
                float homing = math.saturate(arc.RangedHoming);
                float halfSpread = math.radians(arc.RangedSpreadDeg) * 0.5f;

                for (int s = 0; s < shots; s++)
                {
                    // 以朝向玩家的方向为中轴左右均分；单发时就是正对着打。
                    float t = shots > 1 ? (float)s / (shots - 1) : 0.5f;
                    float2 dir = shots > 1 && halfSpread > 0f
                        ? RotateRad(aim, math.lerp(-halfSpread, halfSpread, t))
                        : aim;

                    SpawnProjectile(new ProjectileRequest
                    {
                        Position = _position[i] + dir * (_radius[i] + radius + 0.1f),
                        Direction = dir,
                        Speed = arc.RangedSpeed,
                        Damage = arc.AttackDamage,
                        Radius = radius,
                        Lifetime = lifetime,
                        Pierce = 1,
                        TargetFaction = controlledFaction,
                        ApplyStatus = SimStatus.None,
                        SourceLogicId = _logicId[i],
                        VisualId = 0,
                        Homing = homing,
                        HomingRange = homing > 0f ? range : 0f,
                        TurnRateDeg = homing > 0f ? HostileHomingTurnRateDeg : 0f,
                        Tint = HostileProjectileTint,
                    });
                }

                _attackTimer[i] = arc.AttackCooldown;
            }
        }

        private static float2 RotateRad(float2 v, float rad)
        {
            math.sincos(rad, out float sn, out float cs);
            return new float2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        /// <summary>敌方区域（毒坑/毒环）染色，与敌人弹体同一套暖红。</summary>
        private const uint HostileZoneTint = 0xC8324BE6u;

        /// <summary>敌人召唤的占用上限（占单位容量的比例）。超过就停孵，
        /// 把剩下的 25% 留给导演的正常刷怪——否则一次配表失误就能让关卡停止推进。</summary>
        private const float SummonPressureCeiling = 0.75f;

        /// <summary>
        /// enemy-mechanics-parity：敌人的**区域攻击**（放毒坑 / 贴身毒环）与**召唤**。
        ///
        /// 这两样此前敌人完全没有——不是设计上不给，是内核里根本没有"持续区域"这个概念
        /// （它只活在热更层的玩家专属结构里），敌人也不跑热更层逻辑。区域下沉之后就顺理成章了。
        ///
        /// 与 <see cref="ResolveHostileRangedCombat"/> 同样主线程一趟 O(UnitCount)。
        /// 冷却各用各的计时器：一个单位可以同时会撞、会射、会放毒、会孵。
        /// </summary>
        private void ResolveHostileAbilities(float dt, int controlledIndex)
        {
            if (controlledIndex < 0 || controlledIndex >= _unitCount || _alive[controlledIndex] == 0)
            {
                return;
            }

            float2 controlledPos = _position[controlledIndex];
            SimFaction controlledFaction = (SimFaction)_faction[controlledIndex];
            // 先把上限固定住：召唤会当场追加单位，否则新生成的小怪本帧就会被遍历到，
            // 甚至自己再召唤一批（无限套娃）。
            int scanCount = _unitCount;

            for (int i = 0; i < scanCount; i++)
            {
                if (_alive[i] == 0 || (SimFaction)_faction[i] != SimFaction.Hostile)
                {
                    continue;
                }

                int aid = _archetypeId[i];
                if (aid < 0 || aid >= _archetypes.Length)
                {
                    continue;
                }
                BehaviorArchetype arc = _archetypes[aid];

                // ── 区域攻击 ──
                int mode = (int)math.round(arc.ZoneMode);
                if (mode > 0 && arc.ZoneRadius > 0f && arc.ZoneSeconds > 0f)
                {
                    if (_zoneTimer[i] > 0f)
                    {
                        _zoneTimer[i] -= dt;
                    }
                    else
                    {
                        // 模式 1（丢到玩家脚下）要够得着才丢；模式 2/3 是自己身上的东西，不看距离。
                        bool inRange = mode != 1
                            || math.distance(controlledPos, _position[i])
                                <= (arc.AggroRange > 0f ? arc.AggroRange : arc.AttackRange);
                        if (inRange)
                        {
                            SpawnZone(new ZoneRequest
                            {
                                Position = mode == 1 ? controlledPos : _position[i],
                                Radius = arc.ZoneRadius,
                                GrowthRate = 0f,
                                MaxRadius = arc.ZoneRadius,
                                DamagePerTick = arc.ZoneDamagePerTick,
                                Interval = arc.ZoneTickInterval > 0f ? arc.ZoneTickInterval : 0.5f,
                                Seconds = arc.ZoneSeconds,
                                TargetFaction = controlledFaction,
                                ApplyStatus = SimStatus.None,
                                ChainCount = 0,
                                SourceLogicId = _logicId[i],
                                // 模式 3 = 跟着自己走的贴身毒环
                                FollowUnitIndex = mode == 3 ? i : SimConst.InvalidIndex,
                                Tint = HostileZoneTint,
                            });
                            _zoneTimer[i] = arc.ZoneCooldown > 0f ? arc.ZoneCooldown : arc.ZoneSeconds;
                        }
                    }
                }

                // ── 召唤 ──
                int summonArc = (int)math.round(arc.SummonArchetypeId);
                int summonCount = (int)math.round(arc.SummonCount);

                // 代数封顶。召唤原型完全可以指向另一个同样带召唤字段的原型——
                // 那就是**跨帧指数增殖**，一分钟内能把单位容量吃干净，而且崩起来
                // 看着像"莫名其妙卡死"很难查。所以两道闸：原型自己配的深度，
                // 加一条无论怎么配都越不过的内核硬顶。
                int maxGen = arc.SummonMaxGeneration > 0f
                    ? (int)math.round(arc.SummonMaxGeneration)
                    : 1;
                maxGen = math.min(maxGen, SimConst.MaxSpawnGeneration);
                bool generationAllows = _generation[i] < maxGen;

                if (summonArc >= 0 && summonArc < _archetypes.Length && summonCount > 0
                    && generationAllows)
                {
                    if (_summonTimer[i] > 0f)
                    {
                        _summonTimer[i] -= dt;
                    }
                    // 容量兜底 + **压力上限**。
                    //
                    // 光有代数封顶不够：实测把封顶配到 2、且召唤原型指向自己时，
                    // 场上仍会在 20 秒内涨到容量上限（245/256）——代数是收敛了，
                    // 但"浅而快"的循环照样能把槽位吃光，然后**导演的正常刷怪全部失败**，
                    // 表现是关卡莫名其妙停止推进，很难查。
                    // 所以再留一道：占用超过容量的 SummonPressureCeiling 就不再孵，
                    // 给导演留出余量。这不是平衡旋钮，是防止一次配表失误毁掉整局。
                    else if (_unitCount + summonCount < _position.Length - 8
                        && _unitCount < _position.Length * SummonPressureCeiling)
                    {
                        float childHp = arc.SummonHealth > 0f
                            ? arc.SummonHealth
                            : math.max(1f, _health[i] * 0.1f);
                        for (int s = 0; s < summonCount; s++)
                        {
                            float ang = 2f * math.PI * s / summonCount;
                            SpawnUnit(new SpawnRequest
                            {
                                Position = _position[i]
                                    + new float2(math.cos(ang), math.sin(ang)) * (_radius[i] + 0.8f),
                                Velocity = float2.zero,
                                Health = childHp,
                                Radius = math.max(0.2f, _radius[i] * 0.4f),
                                MaxSpeed = math.max(1f, _maxSpeed[i] * 1.2f),
                                ArchetypeId = summonArc,
                                Faction = SimFaction.Hostile,
                                LogicId = 0,
                                VisualId = _visualId[i],
                                // 孩子比自己深一代。这一笔就是封顶生效的地方。
                                Generation = (byte)(_generation[i] + 1),
                            });
                        }
                        _summonTimer[i] = arc.SummonCooldown > 0f ? arc.SummonCooldown : 6f;
                    }
                }
            }
        }

        private void ApplyCommands(ref SimCommandBuffer cmds)
        {
            if (!cmds.IsCreated)
            {
                return;
            }

            for (int i = 0; i < cmds.Spawns.Length; i++)
            {
                SpawnUnit(cmds.Spawns[i]);
            }

            for (int i = 0; i < cmds.Zones.Length; i++)
            {
                SpawnZone(cmds.Zones[i]);
            }

            for (int i = 0; i < cmds.Despawns.Length; i++)
            {
                int idx = cmds.Despawns[i];
                if (idx >= 0 && idx < _unitCount && _alive[idx] != 0)
                {
                    _alive[idx] = 0;
                    // 显式卸载不是战斗死亡：不该走死亡表现，但控制权照样要确定性移交。
                    ReleaseSlot(idx, ControlChangeReason.ControlledRemoved);
                }
            }

            for (int i = 0; i < cmds.Statuses.Length; i++)
            {
                ApplyStatus(cmds.Statuses[i]);
            }

            for (int i = 0; i < cmds.Projectiles.Length; i++)
            {
                SpawnProjectile(cmds.Projectiles[i]);
            }

            for (int i = 0; i < cmds.ArchetypeSwaps.Length; i++)
            {
                ArchetypeSwapRequest req = cmds.ArchetypeSwaps[i];
                if (req.TargetIndex >= 0 && req.TargetIndex < _unitCount && _alive[req.TargetIndex] != 0)
                {
                    _archetypeId[req.TargetIndex] = req.ArchetypeId;
                }
            }
        }

        private void ApplyStatus(in StatusRequest req)
        {
            if (req.Radius < 0f)
            {
                int t = req.TargetIndex;
                if (t < 0 || t >= _unitCount || _alive[t] == 0)
                {
                    return;
                }
                _status[t] = req.Add
                    ? _status[t] | (uint)req.Status
                    : _status[t] & ~(uint)req.Status;
                return;
            }

            int ring = SpatialHash.RingFor(req.Radius, _hash.InvCellSize);
            int2 c = SpatialHash.ToCell(req.Origin, _hash.InvCellSize);
            var map = _hash.Map;

            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                    if (!map.TryGetFirstValue(key, out int j, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (j >= _unitCount || _alive[j] == 0)
                        {
                            continue;
                        }
                        if (req.TargetFaction != SimFaction.None
                            && _faction[j] != (byte)req.TargetFaction)
                        {
                            continue;
                        }
                        float reach = req.Radius + _radius[j];
                        if (math.distancesq(_position[j], req.Origin) > reach * reach)
                        {
                            continue;
                        }
                        _status[j] = req.Add
                            ? _status[j] | (uint)req.Status
                            : _status[j] & ~(uint)req.Status;
                    } while (map.TryGetNextValue(out j, ref it));
                }
            }
        }

        /// <summary>生成单位。返回索引，容量满时返回 -1。</summary>
        public int SpawnUnit(in SpawnRequest req)
        {
            int idx;
            if (_freeSlots.Length > 0)
            {
                idx = _freeSlots[_freeSlots.Length - 1];
                _freeSlots.RemoveAt(_freeSlots.Length - 1);
            }
            else if (_unitCount < _position.Length)
            {
                idx = _unitCount++;
            }
            else
            {
                return SimConst.InvalidIndex;
            }

            _position[idx] = req.Position;
            _velocity[idx] = req.Velocity;
            _desiredDir[idx] = float2.zero;
            _separation[idx] = float2.zero;
            _health[idx] = math.max(1f, req.Health);
            _radius[idx] = math.max(0.05f, req.Radius);
            _maxSpeed[idx] = math.max(0f, req.MaxSpeed);
            _attackTimer[idx] = 0f;
            // 槽位是回收复用的，能力冷却与血统代数都必须清——否则新生成的单位会继承上一任的。
            _zoneTimer[idx] = 0f;
            _summonTimer[idx] = 0f;
            _generation[idx] = (byte)math.min(req.Generation, SimConst.MaxSpawnGeneration);
            _archetypeId[idx] = req.ArchetypeId;
            _status[idx] = (uint)req.InitialStatus;
            _faction[idx] = (byte)req.Faction;
            _alive[idx] = 1;
            _entityId[idx] = AllocateEntityId();
            IntentSource initialIntentSource = req.IntentSource == IntentSource.Scripted
                ? IntentSource.Scripted
                : IntentSource.AI;
            _intentSource[idx] = (byte)initialIntentSource;
            _intentSourceBeforePlayer[idx] = (byte)initialIntentSource;
            _unitIntents[idx] = UnitIntent.Idle(_entityId[idx], initialIntentSource);
            // 双保险：ReleaseSlot 已经清过一次，但首次占用的槽位没走过 ReleaseSlot。
            _unitCommands[idx] = UnitCommand.None;
            // LogicId 0 是玩家/环境的保留值。内核自己生成的单位（敌人召唤物）拿不到热更层的
            // 分配器，用**负数**自成一段——与 SimBridge 的正数序列天然不冲突，
            // 而且死亡事件里一眼看得出"这是内核生成的"。
            _logicId[idx] = req.LogicId != 0 ? req.LogicId : --_kernelLogicId;
            _visualId[idx] = req.VisualId;
            return idx;
        }

        /// <summary>
        /// enemy-mechanics-parity：铺一块持续区域。玩家的毒坑与敌人的毒云走同一个入口，
        /// 区别只在 <see cref="ZoneRequest.TargetFaction"/>。
        /// 环形游标找空位，容量满时**丢弃最老的做法会让光环闪断**，所以直接放弃这一次生成
        /// （区域是持续物，少一块比抢掉别人的更不容易被察觉）。
        /// </summary>
        private void SpawnZone(in ZoneRequest req)
        {
            if (!_zones.IsCreated || req.Seconds <= 0f || req.Radius <= 0f)
            {
                return;
            }

            int n = _zones.Length;
            for (int k = 0; k < n; k++)
            {
                int z = (_zoneCursor + k) % n;
                if (_zones[z].Alive != 0)
                {
                    continue;
                }
                _zones[z] = new ZoneState
                {
                    Position = req.Position,
                    Radius = math.max(0.1f, req.Radius),
                    GrowthRate = math.max(0f, req.GrowthRate),
                    MaxRadius = req.MaxRadius,
                    DamagePerTick = req.DamagePerTick,
                    Interval = math.max(0.02f, req.Interval),
                    // 首跳不等待：踩进毒坑的瞬间就该有反馈，而不是先站半秒。
                    TickTimer = 0f,
                    TimeLeft = req.Seconds,
                    TargetFaction = (byte)req.TargetFaction,
                    ApplyStatus = (uint)req.ApplyStatus,
                    ChainCount = req.ChainCount,
                    SourceLogicId = req.SourceLogicId,
                    FollowUnitIndex = req.FollowUnitIndex,
                    Tint = req.Tint,
                    Alive = 1,
                };
                _zoneCursor = (z + 1) % n;
                return;
            }
        }

        private void SpawnProjectile(in ProjectileRequest req)
        {
            // 环形游标找空位，避免每次线性扫描
            int n = _projectiles.Length;
            for (int k = 0; k < n; k++)
            {
                int p = (_projectileCursor + k) % n;
                if (_projectiles[p].Alive != 0)
                {
                    continue;
                }
                _projectiles[p] = new ProjectileState
                {
                    Position = req.Position,
                    Velocity = math.normalizesafe(req.Direction) * math.max(0.1f, req.Speed),
                    Damage = req.Damage,
                    Radius = math.max(0.05f, req.Radius),
                    TimeLeft = math.max(0.05f, req.Lifetime),
                    PierceLeft = math.max(1, req.Pierce),
                    TargetFaction = (byte)req.TargetFaction,
                    ApplyStatus = (uint)req.ApplyStatus,
                    SourceLogicId = req.SourceLogicId,
                    VisualId = req.VisualId,
                    Alive = 1,

                    // combat-primitive-overhaul：弹道基元原样搬进运行时状态。
                    // 调用方全部留默认 0 时，JobProjectile 每一段都被首行条件跳过（零回归）。
                    Homing = math.saturate(req.Homing),
                    HomingRange = math.max(0f, req.HomingRange),
                    TurnRateDeg = math.max(0f, req.TurnRateDeg),
                    Drag = math.max(0f, req.Drag),
                    AreaRadius = math.max(0f, req.AreaRadius),
                    BounceLeft = math.max(0, req.BounceCount),
                    SplitCount = math.max(0, req.SplitCount),
                    SplitAngleDeg = req.SplitAngleDeg,
                    TrailDamage = math.max(0f, req.TrailDamage),
                    TrailInterval = req.TrailInterval,
                    // 第一跳立刻出，否则短射程弹体飞完全程一次拖尾都不掉。
                    TrailTimer = 0f,
                    LingerSeconds = math.max(0f, req.LingerSeconds),
                    LingerRadius = math.max(0f, req.LingerRadius),
                    ChainCount = math.max(0, req.ChainCount),
                    WeaveRateDeg = req.WeaveRateDeg,
                    WeaveAmp = math.max(0f, req.WeaveAmp),
                    WeavePhase = 0f,
                    LastHitIndex = SimConst.InvalidIndex,
                    HitCooldown = 0f,
                    Generation = req.Generation,
                    Flags = req.Flags,
                    Tint = req.Tint,
                };
                _projectileCursor = (p + 1) % n;
                return;
            }
        }

        private void EmitDeath(int idx)
        {
            // _pendingDeaths（JobDamage）与 _deadQueue（JobCollectDeaths 全量血量扫描）
            // 同一帧可能对同一 idx 各命中一次；后到者此时槽位已被前者 ReleaseSlot 回收
            // （_position 已改写成越界哨兵值），必须拦掉，否则会重复 Publish 一次
            // LogicId=0 / Position 越界的 KillSignal（连带二次结算奖励与卡牌 OnKill）。
            //
            // enemy-ranged-and-parry 修：**这个去重判据原本写的是 `_alive[idx] == 0`，
            // 而两个生产者在入队之前就已经把 `Alive[i]` 置 0 了**（见 JobDamage.TryDamage
            // 与 JobCollectDeaths.Execute），于是它对每一次死亡都成立——
            // 结果是 `DeathCount` 恒为 0，**整个游戏从来没有发出过一次死亡事件**：
            // 击杀奖励、进化能、卡牌 OnKill、KillSignal 全都静默失效。
            // 改用一个"本帧是否已发过"的时间戳，与 Alive 解耦。
            if (idx < 0 || idx >= _unitCount)
            {
                return;
            }
            if (_deathEmitFrame[idx] == _frameIndex)
            {
                return;
            }
            _deathEmitFrame[idx] = _frameIndex;
            if (_deathCount < _deathEvents.Length)
            {
                _deathEvents[_deathCount++] = new DeathEvent
                {
                    LogicId = _logicId[idx],
                    ArchetypeId = _archetypeId[idx],
                    Position = _position[idx],
                    Radius = _radius[idx],
                    Faction = (SimFaction)_faction[idx],
                    StatusAtDeath = (SimStatus)_status[idx],
                    KillerLogicId = 0,
                    CauseKind = DeathCauseKind.Damage,
                };
            }
            ReleaseSlot(idx);
        }

        /// <param name="controlLossReason">这个槽位恰好是受控实体时，控制权变更记什么原因。
        /// 死亡与主动卸载在 UI 与镜头上不是一回事，不能混成一种。</param>
        private void ReleaseSlot(int idx, ControlChangeReason controlLossReason = ControlChangeReason.ControlledDeath)
        {
            bool wasControlled = _controlledUnitId.IsValid && _entityId[idx] == _controlledUnitId;
            SimEntityId previousId = _controlledUnitId;
            float2 lastKnownPos = _position[idx];
            if (wasControlled)
            {
                _intentSource[idx] = _intentSourceBeforePlayer[idx];
                _controlledUnitId = SimEntityId.None;
                // 锚点必须在清位置之前抓——下面会把坐标推到场外防止空间哈希误命中。
                _controlFallbackAnchor = lastKnownPos;
                _hasControlFallbackAnchor = true;
            }
            _alive[idx] = 0;
            // M2-02：槽位会被复用，命令必须一起清掉——否则新生成的单位会继承
            // 上一个占用者的未完成命令，表现为"刚出生就自己往某处跑"。
            _unitCommands[idx] = UnitCommand.None;
            _status[idx] = 0u;
            _health[idx] = 0f;
            _velocity[idx] = float2.zero;
            _desiredDir[idx] = float2.zero;
            _separation[idx] = float2.zero;
            _logicId[idx] = 0;
            // 移出场地，避免残留位置被空间哈希误命中
            _position[idx] = new float2(float.MaxValue * 0.5f, float.MaxValue * 0.5f);
            _freeSlots.Add(idx);

            if (wasControlled)
            {
                // 回弹放在槽位彻底释放之后：否则刚死的躯体还挂着 Alive 语义，
                // 会被自己选成回弹目标，变成"控制了一具尸体"。
                FallbackControlAfterLoss(lastKnownPos, previousId, controlLossReason);
            }
        }

        /// <summary>
        /// M1-06 意识回弹：受控实体没了以后，把控制权交给一个**确定性选出的**存活友军。
        ///
        /// 选择规则固定为「距离失控点最近，距离并列时稳定实体 ID 小者优先」，与
        /// <see cref="GetControlCandidates"/> 的排序同一把尺子——所以同种子、同输入下回弹结果可复现，
        /// 这是 M1-06「固定种子自动回归」成立的前提。找不到目标时控制权明确为 None，
        /// 而不是留一个悬空 ID，保证"要么恰好一个受控实体，要么明确为无"。
        /// </summary>
        private void FallbackControlAfterLoss(float2 origin, SimEntityId previousId, ControlChangeReason reason)
        {
            if (!_controlFallbackEnabled)
            {
                // 回弹关闭：控制权确定地为无。仍要发一条变更事件，否则表现层不知道该切到回退视角。
                RecordControlChange(previousId, SimEntityId.None, reason, origin);
                return;
            }

            SimEntityId fallbackId = SimEntityId.None;
            float bestDistanceSq = float.MaxValue;
            bool limited = _controlFallbackRange > 0f;
            float rangeSq = _controlFallbackRange * _controlFallbackRange;

            for (int i = 0; i < _unitCount; i++)
            {
                if (_alive[i] == 0 || !IsFriendlyFaction((SimFaction)_faction[i]))
                {
                    continue;
                }

                float distanceSq = math.distancesq(origin, _position[i]);
                if (limited && distanceSq > rangeSq)
                {
                    continue;
                }

                ulong candidateId = _entityId[i].Value;
                if (candidateId == 0UL)
                {
                    continue;
                }
                if (distanceSq < bestDistanceSq ||
                    (distanceSq == bestDistanceSq && candidateId < fallbackId.Value))
                {
                    bestDistanceSq = distanceSq;
                    fallbackId = _entityId[i];
                }
            }

            if (fallbackId.IsValid && TryFindUnit(fallbackId, out int fallbackIndex))
            {
                _intentSourceBeforePlayer[fallbackIndex] = _intentSource[fallbackIndex] == (byte)IntentSource.Player
                    ? (byte)IntentSource.AI
                    : _intentSource[fallbackIndex];
                _intentSource[fallbackIndex] = (byte)IntentSource.Player;
                _controlledUnitId = fallbackId;
                _controlFallbackAnchor = _position[fallbackIndex];
                _hasControlFallbackAnchor = true;
            }

            RecordControlChange(previousId, _controlledUnitId, reason, origin);
        }

        /// <summary>直接击杀（吞噬结算用）。</summary>
        public void KillUnit(int idx, int killerLogicId)
        {
            if (!_created || idx < 0 || idx >= _unitCount || _alive[idx] == 0)
            {
                return;
            }
            if (_deathCount < _deathEvents.Length)
            {
                _deathEvents[_deathCount++] = new DeathEvent
                {
                    LogicId = _logicId[idx],
                    ArchetypeId = _archetypeId[idx],
                    Position = _position[idx],
                    Radius = _radius[idx],
                    Faction = (SimFaction)_faction[idx],
                    StatusAtDeath = (SimStatus)_status[idx],
                    KillerLogicId = killerLogicId,
                    CauseKind = DeathCauseKind.Devour,
                };
            }
            ReleaseSlot(idx);
        }

        public SimSnapshot GetSnapshot()
        {
            int controlledIndex = TryResolveUnit(_controlledUnitId, out int resolvedControlledIndex)
                ? resolvedControlledIndex
                : SimConst.InvalidIndex;
            if (controlledIndex != SimConst.InvalidIndex)
            {
                // 受控实体还在时持续刷新回退锚点：它死掉的那一刻已经来不及现算了。
                _controlFallbackAnchor = _position[controlledIndex];
                _hasControlFallbackAnchor = true;
            }
            return new SimSnapshot
            {
                Count = _unitCount,
                Position = _position,
                Velocity = _velocity,
                Health = _health,
                Radius = _radius,
                Status = _status,
                Faction = _faction,
                Alive = _alive,
                ArchetypeId = _archetypeId,
                LogicId = _logicId,
                VisualId = _visualId,
                EntityId = _entityId,
                IntentSource = _intentSource,
                FinalIntent = _unitIntents,
                Deaths = _deathEvents,
                DeathCount = _deathCount,
                Hits = _hitEvents.IsCreated ? _hitEvents.AsArray() : default,
                HitCount = _hitEvents.IsCreated ? _hitEvents.Length : 0,
                DevourCandidates = _devourCandidates.IsCreated ? _devourCandidates.AsArray() : default,
                DevourCandidateCount = _devourCandidates.IsCreated ? _devourCandidates.Length : 0,
                ProjectileEnds = _projectileEndEvents,
                ProjectileEndCount = _projectileEndCount,
                Generation = _generation,
                PlayerDamageTaken = _controlledDamage.IsCreated ? _controlledDamage[0] : 0f,
                PlayerPosition = PlayerPosition,
                PlayerHealth = PlayerHealth,
                PlayerRadius = PlayerRadius,
                ControlledUnitId = _controlledUnitId,
                ControlledUnitIndex = controlledIndex,
            };
        }

        public NativeArray<ProjectileState> Projectiles => _projectiles;

        /// <summary>enemy-mechanics-parity：持续区域数组（渲染与验收探针读，写入走命令缓冲）。</summary>
        public NativeArray<ZoneState> Zones => _zones;

        /// <summary>当前存活的持续区域数。</summary>
        public int LiveZoneCount
        {
            get
            {
                if (!_created || !_zones.IsCreated) { return 0; }
                int n = 0;
                for (int i = 0; i < _zones.Length; i++)
                {
                    if (_zones[i].Alive != 0) { n++; }
                }
                return n;
            }
        }

        public void Dispose()
        {
            if (!_created)
            {
                return;
            }

            Safe(ref _position); Safe(ref _velocity); Safe(ref _desiredDir); Safe(ref _separation);
            SafeF(ref _health); SafeF(ref _radius); SafeF(ref _maxSpeed); SafeF(ref _attackTimer);
            SafeI(ref _archetypeId); SafeI(ref _logicId); SafeI(ref _visualId);
            if (_entityId.IsCreated) { _entityId.Dispose(); }
            if (_intentSource.IsCreated) { _intentSource.Dispose(); }
            if (_intentSourceBeforePlayer.IsCreated) { _intentSourceBeforePlayer.Dispose(); }
            if (_unitIntents.IsCreated) { _unitIntents.Dispose(); }
            if (_unitCommands.IsCreated) { _unitCommands.Dispose(); }
            SafeI(ref _deathEmitFrame);
            SafeF(ref _zoneTimer); SafeF(ref _summonTimer);
            if (_status.IsCreated) { _status.Dispose(); }
            if (_faction.IsCreated) { _faction.Dispose(); }
            if (_alive.IsCreated) { _alive.Dispose(); }
            if (_generation.IsCreated) { _generation.Dispose(); }
            if (_archetypes.IsCreated) { _archetypes.Dispose(); }
            if (_projectiles.IsCreated) { _projectiles.Dispose(); }
            if (_zones.IsCreated) { _zones.Dispose(); }
            if (_zoneDamage.IsCreated) { _zoneDamage.Dispose(); }
            if (_obstaclePos.IsCreated) { _obstaclePos.Dispose(); }
            if (_obstacleRadius.IsCreated) { _obstacleRadius.Dispose(); }
            if (_freeSlots.IsCreated) { _freeSlots.Dispose(); }
            if (_pendingDeaths.IsCreated) { _pendingDeaths.Dispose(); }
            if (_hitEvents.IsCreated) { _hitEvents.Dispose(); }
            if (_deathEvents.IsCreated) { _deathEvents.Dispose(); }
            if (_devourCandidates.IsCreated) { _devourCandidates.Dispose(); }
            if (_projectileDamage.IsCreated) { _projectileDamage.Dispose(); }
            if (_projectileSpawns.IsCreated) { _projectileSpawns.Dispose(); }
            if (_projectileEndQueue.IsCreated) { _projectileEndQueue.Dispose(); }
            if (_projectileEndEvents.IsCreated) { _projectileEndEvents.Dispose(); }
            if (_deadQueue.IsCreated) { _deadQueue.Dispose(); }
            if (_damageScratch.IsCreated) { _damageScratch.Dispose(); }
            if (_controlledDamage.IsCreated) { _controlledDamage.Dispose(); }
            _hash.Dispose();

            _unitCount = 0;
            _deathCount = 0;
            _obstacleCount = 0;
            _controlledUnitId = SimEntityId.None;
            // M1-06：世界卸载后控制状态必须明确为"无"，未消费的变更一并作废——
            // 否则重进场景时会把上一局的回弹事件补发给新世界。
            _controlChangeCount = 0;
            _hasControlFallbackAnchor = false;
            _controlFallbackAnchor = float2.zero;
            _created = false;
        }

        private static void Safe(ref NativeArray<float2> a) { if (a.IsCreated) { a.Dispose(); } }
        private static void SafeF(ref NativeArray<float> a) { if (a.IsCreated) { a.Dispose(); } }
        private static void SafeI(ref NativeArray<int> a) { if (a.IsCreated) { a.Dispose(); } }
    }
}
