using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 热更层 → AOT 内核的单向命令队列。
    ///
    /// 存在意义：热更层（HybridCLR 解释执行）绝不直接触碰 NativeArray。
    /// 所有意图都以结构体命令入队，由内核在 job 安全阶段统一应用。
    /// 这样既规避了 HybridCLR 处理泛型原生容器的风险，也保证了单向数据流。
    ///
    /// 详见 DesignDocs/Game_Framework_Design.md §2.3、§4.4。
    /// </summary>
    public struct SimCommandBuffer
    {
        private NativeList<SpawnRequest> _spawns;
        private NativeList<int> _despawns;
        private NativeList<DamageRequest> _damages;
        private NativeList<StatusRequest> _statuses;
        private NativeList<ProjectileRequest> _projectiles;
        private NativeList<ArchetypeSwapRequest> _archetypeSwaps;
        private NativeList<ZoneRequest> _zones;
        private NativeList<UnitIntent> _intents;
        private bool _created;

        public bool IsCreated => _created;

        public void Initialize(Allocator allocator, int initialCapacity = 256)
        {
            Dispose();
            _spawns = new NativeList<SpawnRequest>(initialCapacity, allocator);
            _despawns = new NativeList<int>(initialCapacity, allocator);
            _damages = new NativeList<DamageRequest>(initialCapacity, allocator);
            _statuses = new NativeList<StatusRequest>(initialCapacity, allocator);
            _projectiles = new NativeList<ProjectileRequest>(initialCapacity, allocator);
            _archetypeSwaps = new NativeList<ArchetypeSwapRequest>(initialCapacity, allocator);
            _zones = new NativeList<ZoneRequest>(initialCapacity, allocator);
            _intents = new NativeList<UnitIntent>(math.max(4, initialCapacity / 8), allocator);
            _created = true;
        }

        public NativeList<SpawnRequest> Spawns => _spawns;
        public NativeList<int> Despawns => _despawns;
        public NativeList<DamageRequest> Damages => _damages;
        public NativeList<StatusRequest> Statuses => _statuses;
        public NativeList<ProjectileRequest> Projectiles => _projectiles;
        public NativeList<ArchetypeSwapRequest> ArchetypeSwaps => _archetypeSwaps;
        /// <summary>enemy-mechanics-parity：持续区域（毒坑/光环）生成请求。</summary>
        public NativeList<ZoneRequest> Zones => _zones;
        /// <summary>本帧实体意图。世界会校验稳定 ID 与当前 IntentSource 后编译到最终槽位缓冲。</summary>
        public NativeList<UnitIntent> Intents => _intents;

        /// <summary>旧查询兼容：返回最后一条 Player 来源意图。</summary>
        public bool TryGetIntent(out PlayerIntent intent)
        {
            if (_created)
            {
                for (int i = _intents.Length - 1; i >= 0; i--)
                {
                    UnitIntent candidate = _intents[i];
                    if (candidate.Source == IntentSource.Player)
                    {
                        intent = PlayerIntent.FromUnitIntent(candidate);
                        return true;
                    }
                }
            }

            intent = default;
            return false;
        }

        /// <summary>
        /// 旧入口兼容：目标留空，由 <see cref="SimWorld"/> 在应用时绑定当前 ControlledUnitId。
        /// 新代码应调用 <see cref="SetUnitIntent"/> 并显式携带实体 ID 与来源。
        /// </summary>
        public void SetPlayerIntent(PlayerIntent intent)
        {
            SetUnitIntent(intent.ToUnitIntent(SimEntityId.None, IntentSource.Player));
        }

        public void SetUnitIntent(in UnitIntent intent)
        {
            if (_created) { _intents.Add(intent); }
        }

        public void Spawn(in SpawnRequest req)
        {
            if (_created) { _spawns.Add(req); }
        }

        public void Despawn(int unitIndex)
        {
            if (_created) { _despawns.Add(unitIndex); }
        }

        public void Damage(in DamageRequest req)
        {
            if (_created) { _damages.Add(req); }
        }

        public void Status(in StatusRequest req)
        {
            if (_created) { _statuses.Add(req); }
        }

        public void Projectile(in ProjectileRequest req)
        {
            if (_created) { _projectiles.Add(req); }
        }

        public void SwapArchetype(in ArchetypeSwapRequest req)
        {
            if (_created) { _archetypeSwaps.Add(req); }
        }

        public void Zone(in ZoneRequest req)
        {
            if (_created) { _zones.Add(req); }
        }

        /// <summary>内核应用完命令后调用。意图按帧提交，缺省来源在下一帧编译为空闲意图。</summary>
        public void Clear()
        {
            if (!_created)
            {
                return;
            }
            _spawns.Clear();
            _despawns.Clear();
            _damages.Clear();
            _statuses.Clear();
            _projectiles.Clear();
            _archetypeSwaps.Clear();
            _zones.Clear();
            _intents.Clear();
        }

        public void Dispose()
        {
            if (!_created)
            {
                return;
            }
            if (_spawns.IsCreated) { _spawns.Dispose(); }
            if (_despawns.IsCreated) { _despawns.Dispose(); }
            if (_damages.IsCreated) { _damages.Dispose(); }
            if (_statuses.IsCreated) { _statuses.Dispose(); }
            if (_projectiles.IsCreated) { _projectiles.Dispose(); }
            if (_archetypeSwaps.IsCreated) { _archetypeSwaps.Dispose(); }
            if (_zones.IsCreated) { _zones.Dispose(); }
            if (_intents.IsCreated) { _intents.Dispose(); }
            _created = false;
        }
    }

    /// <summary>
    /// 所有来源共用的实体意图形状。EntityId 是跨帧身份；Source 决定命令是否有权覆盖该实体本帧意图。
    /// AI 由 AOT 作业写同一结构，Player/Scripted 由命令缓冲提交。
    /// </summary>
    public struct UnitIntent
    {
        public SimEntityId EntityId;
        public IntentSource Source;
        public float2 MoveDir;
        public float SpeedMul;
        public float RadiusOverride;
        public SimStatus AddStatus;
        public SimStatus RemoveStatus;

        public static UnitIntent Idle(SimEntityId entityId, IntentSource source)
        {
            return new UnitIntent
            {
                EntityId = entityId,
                Source = source,
                MoveDir = float2.zero,
                SpeedMul = 1f,
                RadiusOverride = -1f,
                AddStatus = SimStatus.None,
                RemoveStatus = SimStatus.None,
            };
        }
    }

    /// <summary>
    /// 玩家本帧意图。玩家单位由内核积分移动，但方向与速度倍率由热更层决定。
    /// </summary>
    public struct PlayerIntent
    {
        /// <summary>归一化移动方向。零向量表示不移动。</summary>
        public float2 MoveDir;
        /// <summary>速度倍率。冲刺等能力临时抬高此值。</summary>
        public float SpeedMul;
        /// <summary>体积覆写。&lt;= 0 表示不覆写（体积在细胞阶段会变化）。</summary>
        public float RadiusOverride;
        /// <summary>本帧要施加到玩家的状态（无敌、硬化等）。</summary>
        public SimStatus AddStatus;
        /// <summary>本帧要移除的状态。</summary>
        public SimStatus RemoveStatus;

        public static PlayerIntent Idle => new PlayerIntent
        {
            MoveDir = float2.zero,
            SpeedMul = 1f,
            RadiusOverride = -1f,
            AddStatus = SimStatus.None,
            RemoveStatus = SimStatus.None,
        };

        public UnitIntent ToUnitIntent(SimEntityId entityId, IntentSource source)
        {
            return new UnitIntent
            {
                EntityId = entityId,
                Source = source,
                MoveDir = MoveDir,
                SpeedMul = SpeedMul,
                RadiusOverride = RadiusOverride,
                AddStatus = AddStatus,
                RemoveStatus = RemoveStatus,
            };
        }

        public static PlayerIntent FromUnitIntent(in UnitIntent intent)
        {
            return new PlayerIntent
            {
                MoveDir = intent.MoveDir,
                SpeedMul = intent.SpeedMul,
                RadiusOverride = intent.RadiusOverride,
                AddStatus = intent.AddStatus,
                RemoveStatus = intent.RemoveStatus,
            };
        }
    }
}
