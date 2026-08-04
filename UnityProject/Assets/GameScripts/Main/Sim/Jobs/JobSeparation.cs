using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 轻量分离力。故意不做完整 ORCA。
    ///
    /// 理由：数千单位下玩家需要的是"看得清、不糊成一团"，不是严格不重叠。
    /// 完整 ORCA 每单位求解线性规划，成本高一个量级；这里只做 O(N*k) 的推挤，
    /// 视觉效果足够且能跑 10k+。
    /// </summary>
    [BurstCompile]
    public struct JobSeparation : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeArray<BehaviorArchetype> Archetypes;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;

        [NativeDisableParallelForRestriction]
        public NativeArray<float2> SeparationForce;

        public float InvCellSize;
        public int Count;

        /// <summary>单个单位最多考虑的邻居数，防止密集堆积时成本爆炸。</summary>
        public int MaxNeighbors;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0)
            {
                return;
            }

            int aid = ArchetypeId[i];
            float strength = aid >= 0 && aid < Archetypes.Length
                ? Archetypes[aid].Separation
                : 1f;

            if (strength <= 0.0001f)
            {
                SeparationForce[i] = float2.zero;
                return;
            }

            float2 pos = Position[i];
            float rad = Radius[i];
            float2 push = float2.zero;
            int considered = 0;

            int2 c = SpatialHash.ToCell(pos, InvCellSize);
            for (int dy = -1; dy <= 1 && considered < MaxNeighbors; dy++)
            {
                for (int dx = -1; dx <= 1 && considered < MaxNeighbors; dx++)
                {
                    int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                    if (!Hash.TryGetFirstValue(key, out int j, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (j == i || j >= Count || Alive[j] == 0)
                        {
                            continue;
                        }

                        float2 d = pos - Position[j];
                        float minDist = rad + Radius[j];
                        float dSq = math.lengthsq(d);
                        if (dSq >= minDist * minDist || dSq < 0.000001f)
                        {
                            continue;
                        }

                        float dLen = math.sqrt(dSq);
                        // 重叠越深推得越狠
                        float overlap = (minDist - dLen) / minDist;
                        push += (d / dLen) * overlap;
                        considered++;
                        if (considered >= MaxNeighbors)
                        {
                            break;
                        }
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }

            SeparationForce[i] = push * strength;
        }
    }
}
