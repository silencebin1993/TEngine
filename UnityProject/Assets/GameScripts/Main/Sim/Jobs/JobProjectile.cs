using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>投射物运行时状态。SoA 在这里没必要——投射物数量远小于单位。</summary>
    public struct ProjectileState
    {
        public float2 Position;
        public float2 Velocity;
        public float Damage;
        public float Radius;
        public float TimeLeft;
        public int PierceLeft;
        public byte TargetFaction;
        public uint ApplyStatus;
        public int SourceLogicId;
        public int VisualId;
        public byte Alive;
    }

    /// <summary>
    /// 投射物推进与命中检测。并行。
    /// 命中不直接扣血，而是产出 DamageRequest 交给 JobDamage 统一结算，
    /// 保持"伤害只有一个入口"的纪律。
    /// </summary>
    [BurstCompile]
    public struct JobProjectile : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;
        /// <summary>静态障碍（story-009）。数量小，线性扫描比建哈希更简单更快。</summary>
        [ReadOnly] public NativeArray<float2> ObstaclePos;
        [ReadOnly] public NativeArray<float> ObstacleRadius;
        public int ObstacleCount;

        public NativeArray<ProjectileState> Projectiles;
        public NativeQueue<DamageRequest>.ParallelWriter DamageOut;

        public float Dt;
        public float InvCellSize;
        public int UnitCount;
        public float ArenaHalf;

        public void Execute(int p)
        {
            ProjectileState s = Projectiles[p];
            if (s.Alive == 0)
            {
                return;
            }

            s.TimeLeft -= Dt;
            if (s.TimeLeft <= 0f)
            {
                s.Alive = 0;
                Projectiles[p] = s;
                return;
            }

            s.Position += s.Velocity * Dt;

            // 出界即消失
            if (math.abs(s.Position.x) > ArenaHalf || math.abs(s.Position.y) > ArenaHalf)
            {
                s.Alive = 0;
                Projectiles[p] = s;
                return;
            }

            // 撞障销毁（story-009 D8）：不计入 PierceLeft、不产出伤害，让障碍对远程敌人也有掩体价值。
            for (int o = 0; o < ObstacleCount; o++)
            {
                float reachO = s.Radius + ObstacleRadius[o];
                if (math.distancesq(s.Position, ObstaclePos[o]) <= reachO * reachO)
                {
                    s.Alive = 0;
                    Projectiles[p] = s;
                    return;
                }
            }

            int2 c = SpatialHash.ToCell(s.Position, InvCellSize);
            int ring = SpatialHash.RingFor(s.Radius + 1.5f, InvCellSize);

            for (int dy = -ring; dy <= ring && s.PierceLeft > 0; dy++)
            {
                for (int dx = -ring; dx <= ring && s.PierceLeft > 0; dx++)
                {
                    int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                    if (!Hash.TryGetFirstValue(key, out int j, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (j >= UnitCount || Alive[j] == 0)
                        {
                            continue;
                        }
                        if (s.TargetFaction != (byte)SimFaction.None && Faction[j] != s.TargetFaction)
                        {
                            continue;
                        }

                        float reach = s.Radius + Radius[j];
                        if (math.distancesq(Position[j], s.Position) > reach * reach)
                        {
                            continue;
                        }

                        DamageOut.Enqueue(new DamageRequest
                        {
                            Origin = s.Position,
                            Radius = -1f,
                            TargetIndex = j,
                            Amount = s.Damage,
                            TargetFaction = (SimFaction)s.TargetFaction,
                            ApplyStatus = (SimStatus)s.ApplyStatus,
                            RequireStatus = SimStatus.None,
                            ChainCount = 0,
                            SourceLogicId = s.SourceLogicId,
                        });

                        s.PierceLeft--;
                        if (s.PierceLeft <= 0)
                        {
                            s.Alive = 0;
                            break;
                        }
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }

            Projectiles[p] = s;
        }
    }
}
