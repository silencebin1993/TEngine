using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 玩家召唤物（PlayerMinion）索敌共用逻辑。被 Burst 并行 Job（<see cref="JobSteering"/>）
    /// 与主线程一次性结算（<see cref="SimWorld"/>）共同调用，避免两处各写一份扫描代码。
    /// 全 blittable 参数，Burst 可内联。
    /// </summary>
    internal static class MinionTargetingUtil
    {
        /// <summary>在 <paramref name="radius"/> 内用空间哈希找最近的 Hostile 单位。</summary>
        public static bool TryFindNearestHostile(
            in NativeParallelMultiHashMap<int, int> hash, float invCellSize,
            [ReadOnly] NativeArray<float2> position, [ReadOnly] NativeArray<byte> alive,
            [ReadOnly] NativeArray<byte> faction, int count,
            float2 origin, float radius, int selfIndex,
            out int targetIndex, out float2 targetPos)
        {
            targetIndex = -1;
            targetPos = float2.zero;
            if (radius <= 0f)
            {
                return false;
            }

            float bestDistSq = radius * radius;
            int ring = SpatialHash.RingFor(radius, invCellSize);
            int2 c = SpatialHash.ToCell(origin, invCellSize);

            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int key = SpatialHash.Hash(new int2(c.x + dx, c.y + dy));
                    if (!hash.TryGetFirstValue(key, out int j, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        if (j == selfIndex || j >= count || alive[j] == 0)
                        {
                            continue;
                        }
                        if (faction[j] != (byte)SimFaction.Hostile)
                        {
                            continue;
                        }
                        float distSq = math.distancesq(origin, position[j]);
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            targetIndex = j;
                            targetPos = position[j];
                        }
                    } while (hash.TryGetNextValue(out j, ref it));
                }
            }

            return targetIndex >= 0;
        }
    }
}
