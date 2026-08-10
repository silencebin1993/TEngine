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

        private NativeArray<BehaviorArchetype> _archetypes;
        private NativeArray<ProjectileState> _projectiles;

        // ── 槽位管理 ──
        private NativeList<int> _freeSlots;
        private int _unitCount;
        private int _projectileCursor;

        // ── 事件与中间缓冲 ──
        private NativeList<int> _pendingDeaths;
        private NativeList<HitEvent> _hitEvents;
        private NativeArray<DeathEvent> _deathEvents;
        private int _deathCount;
        private NativeList<int> _devourCandidates;
        private NativeQueue<DamageRequest> _projectileDamage;
        private NativeQueue<int> _deadQueue;
        private NativeList<DamageRequest> _damageScratch;
        private NativeArray<float> _playerDamage;

        private SpatialHash _hash;
        private float _time;

        public bool IsCreated => _created;
        public int UnitCount => _unitCount;
        public float Time => _time;

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

            _projectiles = new NativeArray<ProjectileState>(math.max(16, cfg.ProjectileCapacity), A);

            _freeSlots = new NativeList<int>(cap, A);
            _pendingDeaths = new NativeList<int>(256, A);
            _hitEvents = new NativeList<HitEvent>(math.max(64, cfg.MaxHitEventsPerFrame), A);
            _deathEvents = new NativeArray<DeathEvent>(math.max(64, cfg.MaxDeathEventsPerFrame), A);
            _devourCandidates = new NativeList<int>(64, A);
            _projectileDamage = new NativeQueue<DamageRequest>(A);
            _deadQueue = new NativeQueue<int>(A);
            _damageScratch = new NativeList<DamageRequest>(256, A);
            _playerDamage = new NativeArray<float>(1, A);

            _hash.Initialize(cap, cfg.HashCellSize, A);
            _archetypes = new NativeArray<BehaviorArchetype>(1, A);
            _archetypes[0] = BehaviorArchetype.Default;

            // 玩家恒占索引 0
            _unitCount = 1;
            _alive[SimConst.PlayerIndex] = 1;
            _faction[SimConst.PlayerIndex] = (byte)SimFaction.Player;
            _health[SimConst.PlayerIndex] = 100f;
            _radius[SimConst.PlayerIndex] = 1f;
            _maxSpeed[SimConst.PlayerIndex] = 8f;
            _archetypeId[SimConst.PlayerIndex] = -1;
            _logicId[SimConst.PlayerIndex] = 0;
            _visualId[SimConst.PlayerIndex] = 0;

            _time = 0f;
            _projectileCursor = 0;
            _created = true;
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

        // ── 玩家读写（热更层通过 SimBridge 调用，不直接碰数组）──

        public void SetPlayerStats(float maxHp, float currentHp, float radius, float maxSpeed)
        {
            if (!_created) { return; }
            _health[SimConst.PlayerIndex] = math.min(currentHp, maxHp);
            _radius[SimConst.PlayerIndex] = math.max(0.1f, radius);
            _maxSpeed[SimConst.PlayerIndex] = math.max(0.1f, maxSpeed);
        }

        public float PlayerHealth => _created ? _health[SimConst.PlayerIndex] : 0f;
        public float2 PlayerPosition => _created ? _position[SimConst.PlayerIndex] : float2.zero;
        public float PlayerRadius => _created ? _radius[SimConst.PlayerIndex] : 1f;

        public void DamagePlayer(float amount)
        {
            if (!_created || amount <= 0f) { return; }
            if ((_status[SimConst.PlayerIndex] & (uint)SimStatus.Invulnerable) != 0u) { return; }
            _health[SimConst.PlayerIndex] -= amount;
        }

        public void HealPlayer(float amount, float maxHp)
        {
            if (!_created || amount <= 0f) { return; }
            _health[SimConst.PlayerIndex] = math.min(_health[SimConst.PlayerIndex] + amount, maxHp);
        }

        public void SetPlayerPosition(float2 pos)
        {
            if (_created) { _position[SimConst.PlayerIndex] = pos; }
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

            _pendingDeaths.Clear();
            _hitEvents.Clear();
            _devourCandidates.Clear();
            _damageScratch.Clear();
            _deathCount = 0;
            _playerDamage[0] = 0f;

            ApplyCommands(ref cmds);

            // 玩家意图 → 玩家的 DesiredDir 与速度倍率
            float playerSpeedMul = 1f;
            if (cmds.TryGetIntent(out PlayerIntent intent))
            {
                _desiredDir[SimConst.PlayerIndex] = math.normalizesafe(intent.MoveDir);
                playerSpeedMul = math.max(0f, intent.SpeedMul);
                if (intent.RadiusOverride > 0f)
                {
                    _radius[SimConst.PlayerIndex] = intent.RadiusOverride;
                }
                uint ps = _status[SimConst.PlayerIndex];
                ps |= (uint)intent.AddStatus;
                ps &= ~(uint)intent.RemoveStatus;
                _status[SimConst.PlayerIndex] = ps;
            }
            else
            {
                _desiredDir[SimConst.PlayerIndex] = float2.zero;
            }

            float playerBaseSpeed = _maxSpeed[SimConst.PlayerIndex];
            _maxSpeed[SimConst.PlayerIndex] = playerBaseSpeed * playerSpeedMul;

            // ── job 链 ──
            JobHandle h = _hash.Rebuild(_position, _alive, _unitCount, default);

            var steering = new JobSteering
            {
                Position = _position,
                Velocity = _velocity,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                ArchetypeId = _archetypeId,
                Status = _status,
                Archetypes = _archetypes,
                DesiredDir = _desiredDir,
                AttackTimer = _attackTimer,
                PlayerPos = _position[SimConst.PlayerIndex],
                Time = _time,
                Dt = dt,
                Count = _unitCount,
                ArenaHalf = _cfg.ArenaHalfExtent,
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
                Alive = _alive,
                Status = _status,
                ArchetypeId = _archetypeId,
                Archetypes = _archetypes,
                AttackTimer = _attackTimer,
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
            _maxSpeed[SimConst.PlayerIndex] = playerBaseSpeed;

            float2 playerPos = _position[SimConst.PlayerIndex];
            float playerRad = _radius[SimConst.PlayerIndex];

            // 投射物：命中转为 DamageRequest
            var proj = new JobProjectile
            {
                Position = _position,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                Hash = _hash.Map,
                Projectiles = _projectiles,
                DamageOut = _projectileDamage.AsParallelWriter(),
                Dt = dt,
                InvCellSize = _hash.InvCellSize,
                UnitCount = _unitCount,
                ArenaHalf = _cfg.ArenaHalfExtent,
            };
            proj.Schedule(_projectiles.Length, 32, default).Complete();

            // 汇总伤害请求：命令缓冲里的 + 投射物产生的
            for (int i = 0; i < cmds.Damages.Length; i++)
            {
                _damageScratch.Add(cmds.Damages[i]);
            }
            while (_projectileDamage.TryDequeue(out DamageRequest dr))
            {
                _damageScratch.Add(dr);
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
                PlayerDamageOut = _playerDamage,
                PlayerPos = playerPos,
                PlayerRadius = playerRad,
                InvCellSize = _hash.InvCellSize,
                Count = _unitCount,
                Dt = dt,
            };
            contact.Schedule().Complete();

            var devour = new JobDevourScan
            {
                Position = _position,
                Radius = _radius,
                Faction = _faction,
                Alive = _alive,
                Status = _status,
                Hash = _hash.Map,
                Candidates = _devourCandidates,
                PlayerPos = playerPos,
                PlayerRadius = playerRad,
                InvCellSize = _hash.InvCellSize,
                Count = _unitCount,
                DevourRatio = 1.05f,
                BreachedDiscount = 0.7f,
                CorrodedDiscount = 0.85f,
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

            for (int i = 0; i < cmds.Despawns.Length; i++)
            {
                int idx = cmds.Despawns[i];
                if (idx > SimConst.PlayerIndex && idx < _unitCount && _alive[idx] != 0)
                {
                    _alive[idx] = 0;
                    ReleaseSlot(idx);
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
            _archetypeId[idx] = req.ArchetypeId;
            _status[idx] = (uint)req.InitialStatus;
            _faction[idx] = (byte)req.Faction;
            _alive[idx] = 1;
            _logicId[idx] = req.LogicId;
            _visualId[idx] = req.VisualId;
            return idx;
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
                };
                _projectileCursor = (p + 1) % n;
                return;
            }
        }

        private void EmitDeath(int idx)
        {
            if (idx <= SimConst.PlayerIndex || idx >= _unitCount)
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
                    KillerLogicId = 0,
                    CauseKind = DeathCauseKind.Damage,
                };
            }
            ReleaseSlot(idx);
        }

        private void ReleaseSlot(int idx)
        {
            _alive[idx] = 0;
            _status[idx] = 0u;
            _health[idx] = 0f;
            _velocity[idx] = float2.zero;
            _desiredDir[idx] = float2.zero;
            _separation[idx] = float2.zero;
            _logicId[idx] = 0;
            // 移出场地，避免残留位置被空间哈希误命中
            _position[idx] = new float2(float.MaxValue * 0.5f, float.MaxValue * 0.5f);
            _freeSlots.Add(idx);
        }

        /// <summary>直接击杀（吞噬结算用）。</summary>
        public void KillUnit(int idx, int killerLogicId)
        {
            if (!_created || idx <= SimConst.PlayerIndex || idx >= _unitCount || _alive[idx] == 0)
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
                Deaths = _deathEvents,
                DeathCount = _deathCount,
                Hits = _hitEvents.IsCreated ? _hitEvents.AsArray() : default,
                HitCount = _hitEvents.IsCreated ? _hitEvents.Length : 0,
                DevourCandidates = _devourCandidates.IsCreated ? _devourCandidates.AsArray() : default,
                DevourCandidateCount = _devourCandidates.IsCreated ? _devourCandidates.Length : 0,
                PlayerContactDamage = _playerDamage.IsCreated ? _playerDamage[0] : 0f,
                PlayerPosition = _position[SimConst.PlayerIndex],
                PlayerHealth = _health[SimConst.PlayerIndex],
                PlayerRadius = _radius[SimConst.PlayerIndex],
            };
        }

        public NativeArray<ProjectileState> Projectiles => _projectiles;

        public void Dispose()
        {
            if (!_created)
            {
                return;
            }

            Safe(ref _position); Safe(ref _velocity); Safe(ref _desiredDir); Safe(ref _separation);
            SafeF(ref _health); SafeF(ref _radius); SafeF(ref _maxSpeed); SafeF(ref _attackTimer);
            SafeI(ref _archetypeId); SafeI(ref _logicId); SafeI(ref _visualId);
            if (_status.IsCreated) { _status.Dispose(); }
            if (_faction.IsCreated) { _faction.Dispose(); }
            if (_alive.IsCreated) { _alive.Dispose(); }
            if (_archetypes.IsCreated) { _archetypes.Dispose(); }
            if (_projectiles.IsCreated) { _projectiles.Dispose(); }
            if (_freeSlots.IsCreated) { _freeSlots.Dispose(); }
            if (_pendingDeaths.IsCreated) { _pendingDeaths.Dispose(); }
            if (_hitEvents.IsCreated) { _hitEvents.Dispose(); }
            if (_deathEvents.IsCreated) { _deathEvents.Dispose(); }
            if (_devourCandidates.IsCreated) { _devourCandidates.Dispose(); }
            if (_projectileDamage.IsCreated) { _projectileDamage.Dispose(); }
            if (_deadQueue.IsCreated) { _deadQueue.Dispose(); }
            if (_damageScratch.IsCreated) { _damageScratch.Dispose(); }
            if (_playerDamage.IsCreated) { _playerDamage.Dispose(); }
            _hash.Dispose();

            _unitCount = 0;
            _deathCount = 0;
            _created = false;
        }

        private static void Safe(ref NativeArray<float2> a) { if (a.IsCreated) { a.Dispose(); } }
        private static void SafeF(ref NativeArray<float> a) { if (a.IsCreated) { a.Dispose(); } }
        private static void SafeI(ref NativeArray<int> a) { if (a.IsCreated) { a.Dispose(); } }
    }
}
