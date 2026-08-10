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

        private PlayerIntent _intent;
        private bool _hasIntent;
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
            _intent = default;
            _hasIntent = false;
            _created = true;
        }

        public NativeList<SpawnRequest> Spawns => _spawns;
        public NativeList<int> Despawns => _despawns;
        public NativeList<DamageRequest> Damages => _damages;
        public NativeList<StatusRequest> Statuses => _statuses;
        public NativeList<ProjectileRequest> Projectiles => _projectiles;
        public NativeList<ArchetypeSwapRequest> ArchetypeSwaps => _archetypeSwaps;

        public bool TryGetIntent(out PlayerIntent intent)
        {
            intent = _intent;
            return _hasIntent;
        }

        public void SetPlayerIntent(PlayerIntent intent)
        {
            _intent = intent;
            _hasIntent = true;
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

        /// <summary>内核应用完命令后调用。玩家意图保留上一帧值，避免输入抖动。</summary>
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
            _hasIntent = false;
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
            _created = false;
            _hasIntent = false;
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
    }
}
