using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 伤害求解。单线程 IJob（非并行）。
    ///
    /// 为什么不并行：伤害请求每帧只有数十条，而并行写 Health 需要原子操作或分区，
    /// 复杂度换来的收益为负。真正的 O(N) 热点是 steering/integrate，那两个是并行的。
    ///
    /// 一个 job 里同时处理单体、圆形范围与连锁三种形态，避免多次遍历。
    /// </summary>
    [BurstCompile]
    public struct JobDamage : IJob
    {
        [ReadOnly] public NativeArray<DamageRequest> Requests;
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<int> LogicId;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;

        public NativeArray<float> Health;
        public NativeArray<uint> Status;
        public NativeArray<byte> Alive;

        public NativeList<int> PendingDeaths;
        public NativeList<HitEvent> HitEvents;

        public float InvCellSize;
        public int Count;
        public int MaxHitEvents;

        /// <summary>易伤状态的伤害倍率。</summary>
        public float VulnerableMul;
        /// <summary>硬化状态的伤害倍率。</summary>
        public float HardenedMul;

        public void Execute()
        {
            for (int r = 0; r < Requests.Length; r++)
            {
                DamageRequest req = Requests[r];

                if (req.Radius < 0f)
                {
                    // 单体
                    int lastHit = TryDamage(req.TargetIndex, req, req.Amount);
                    if (req.ChainCount > 0 && lastHit >= 0)
                    {
                        Chain(lastHit, req);
                    }
                    continue;
                }

                // 圆形范围
                int ring = SpatialHash.RingFor(req.Radius, InvCellSize);
                int2 c = SpatialHash.ToCell(req.Origin, InvCellSize);
                int chainSeed = -1;

                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                        if (!Hash.TryGetFirstValue(key, out int j, out var it))
                        {
                            continue;
                        }
                        do
                        {
                            // 把目标自身半径算进去，边缘的大体积单位也应被命中
                            float reach = req.Radius + Radius[j];
                            if (math.distancesq(Position[j], req.Origin) > reach * reach)
                            {
                                continue;
                            }
                            int hit = TryDamage(j, req, req.Amount);
                            if (hit >= 0)
                            {
                                chainSeed = hit;
                            }
                        } while (Hash.TryGetNextValue(out j, ref it));
                    }
                }

                if (req.ChainCount > 0 && chainSeed >= 0)
                {
                    Chain(chainSeed, req);
                }
            }
        }

        /// <summary>
        /// 电弧连锁：从 origin 逐跳向最近的合法目标传播，伤害递减。
        /// 用 prev 避免在两个目标之间来回跳。
        /// </summary>
        private void Chain(int origin, DamageRequest req)
        {
            int cur = origin;
            int prev = -1;
            float amount = req.Amount * req.ChainFalloff;
            float range = req.ChainRange > 0f ? req.ChainRange : 4f;
            int ring = SpatialHash.RingFor(range, InvCellSize);

            for (int hop = 0; hop < req.ChainCount; hop++)
            {
                float2 from = Position[cur];
                int best = -1;
                float bestDistSq = range * range;

                int2 c = SpatialHash.ToCell(from, InvCellSize);
                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                        if (!Hash.TryGetFirstValue(key, out int j, out var it))
                        {
                            continue;
                        }
                        do
                        {
                            if (j == cur || j == prev || !IsValidTarget(j, req))
                            {
                                continue;
                            }
                            float dSq = math.distancesq(Position[j], from);
                            if (dSq < bestDistSq)
                            {
                                bestDistSq = dSq;
                                best = j;
                            }
                        } while (Hash.TryGetNextValue(out j, ref it));
                    }
                }

                if (best < 0)
                {
                    return;
                }

                TryDamage(best, req, amount);
                prev = cur;
                cur = best;
                amount *= req.ChainFalloff;
            }
        }

        private bool IsValidTarget(int i, in DamageRequest req)
        {
            if (i < 0 || i >= Count || Alive[i] == 0)
            {
                return false;
            }
            if (req.TargetFaction != SimFaction.None && Faction[i] != (byte)req.TargetFaction)
            {
                return false;
            }
            if ((Status[i] & (uint)SimStatus.Invulnerable) != 0u)
            {
                return false;
            }
            if (req.RequireStatus != SimStatus.None && (Status[i] & (uint)req.RequireStatus) == 0u)
            {
                return false;
            }
            return true;
        }

        /// <summary>施加伤害。返回被命中的索引，未命中返回 -1。</summary>
        private int TryDamage(int i, in DamageRequest req, float amount)
        {
            if (!IsValidTarget(i, req))
            {
                return -1;
            }

            uint st = Status[i];
            float final = amount;
            if ((st & (uint)SimStatus.Vulnerable) != 0u) { final *= VulnerableMul; }
            if ((st & (uint)SimStatus.Hardened) != 0u) { final *= HardenedMul; }

            float hp = Health[i] - final;
            Health[i] = hp;

            if (req.ApplyStatus != SimStatus.None)
            {
                Status[i] = st | (uint)req.ApplyStatus;
            }

            bool lethal = hp <= 0f;
            if (lethal)
            {
                Alive[i] = 0;
                PendingDeaths.Add(i);
            }

            if (HitEvents.Length < MaxHitEvents)
            {
                HitEvents.Add(new HitEvent
                {
                    TargetLogicId = LogicId[i],
                    SourceLogicId = req.SourceLogicId,
                    Position = Position[i],
                    Damage = final,
                    Lethal = lethal,
                });
            }

            return lethal ? -1 : i;
        }
    }

    /// <summary>
    /// 敌人对玩家的接触伤害。单独一个 job，因为只写玩家一个槽位，无并行冲突。
    /// </summary>
    [BurstCompile]
    public struct JobContactDamage : IJob
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeArray<BehaviorArchetype> Archetypes;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;

        public NativeArray<float> AttackTimer;
        /// <summary>长度 1 的输出数组，累加本帧对玩家的总伤害。</summary>
        public NativeArray<float> PlayerDamageOut;

        public float2 PlayerPos;
        public float PlayerRadius;
        public float InvCellSize;
        public int Count;
        public float Dt;

        public void Execute()
        {
            float total = 0f;
            // 接触判定范围取玩家半径 + 合理的最大攻击距离
            float scan = PlayerRadius + 6f;
            int ring = SpatialHash.RingFor(scan, InvCellSize);
            int2 c = SpatialHash.ToCell(PlayerPos, InvCellSize);

            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                    if (!Hash.TryGetFirstValue(key, out int j, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (j == SimConst.PlayerIndex || j >= Count || Alive[j] == 0)
                        {
                            continue;
                        }
                        if (Faction[j] != (byte)SimFaction.Hostile)
                        {
                            continue;
                        }

                        int aid = ArchetypeId[j];
                        if (aid < 0 || aid >= Archetypes.Length)
                        {
                            continue;
                        }
                        BehaviorArchetype arc = Archetypes[aid];
                        if (arc.AttackDamage <= 0f)
                        {
                            continue;
                        }
                        if (AttackTimer[j] > 0f)
                        {
                            continue;
                        }

                        float reach = PlayerRadius + Radius[j] + arc.AttackRange;
                        if (math.distancesq(Position[j], PlayerPos) > reach * reach)
                        {
                            continue;
                        }

                        total += arc.AttackDamage;
                        AttackTimer[j] = arc.AttackCooldown;
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }

            PlayerDamageOut[0] = total;
        }
    }
}
