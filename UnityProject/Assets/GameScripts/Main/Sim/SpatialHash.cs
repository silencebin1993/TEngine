using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// Burst 友好的均匀网格空间哈希。
    ///
    /// 所有邻域查询（吞噬判定、范围伤害、分离、电弧连锁、最近目标）都走它，
    /// 把 O(N²) 降到 O(N * k)。每帧重建，不做增量维护——10k 单位重建约 0.1ms，
    /// 比维护增量更简单也更不容易出错。
    ///
    /// cell 边长应 ≥ 最大交互半径，这样任意查询最多只需检查 3x3 = 9 个 cell。
    /// </summary>
    public struct SpatialHash
    {
        private NativeParallelMultiHashMap<int, int> _map;
        private float _cellSize;
        private float _invCellSize;

        public bool IsCreated => _map.IsCreated;

        public void Initialize(int capacity, float cellSize, Allocator allocator)
        {
            Dispose();
            _cellSize = math.max(0.25f, cellSize);
            _invCellSize = 1f / _cellSize;
            _map = new NativeParallelMultiHashMap<int, int>(capacity * 2, allocator);
        }

        public void Dispose()
        {
            if (_map.IsCreated)
            {
                _map.Dispose();
            }
        }

        public NativeParallelMultiHashMap<int, int> Map => _map;
        public float CellSize => _cellSize;
        public float InvCellSize => _invCellSize;

        /// <summary>清空并重建。返回可供后续 job 依赖的 handle。</summary>
        public JobHandle Rebuild(NativeArray<float2> positions, NativeArray<byte> alive, int count,
            JobHandle dependency)
        {
            _map.Clear();
            var job = new JobBuildHash
            {
                Positions = positions,
                Alive = alive,
                Count = count,
                InvCellSize = _invCellSize,
                Writer = _map.AsParallelWriter(),
            };
            return job.Schedule(count, 128, dependency);
        }

        /// <summary>世界坐标 → cell 坐标。</summary>
        public static int2 ToCell(float2 pos, float invCellSize)
        {
            return new int2(
                (int)math.floor(pos.x * invCellSize),
                (int)math.floor(pos.y * invCellSize));
        }

        /// <summary>
        /// cell 坐标 → 哈希键。用两个大素数混合，避免轴对齐时的键聚集。
        /// </summary>
        public static int Hash(int2 cell)
        {
            unchecked
            {
                return (cell.x * 73856093) ^ (cell.y * 19349663);
            }
        }

        public int HashOf(float2 pos)
        {
            return Hash(ToCell(pos, _invCellSize));
        }

        /// <summary>
        /// 遍历半径内的候选单位索引。radius 应 ≤ cellSize，否则需扩大 ring。
        /// 返回的是候选（cell 级粗筛），调用方仍需做精确距离判定。
        /// </summary>
        public static void QueryRing(
            in NativeParallelMultiHashMap<int, int> map,
            float2 center, float invCellSize, int ring,
            ref NativeList<int> results)
        {
            int2 c = ToCell(center, invCellSize);
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int key = Hash(new int2(c.x + dx, c.y + dy));
                    if (!map.TryGetFirstValue(key, out int idx, out var it))
                    {
                        continue;
                    }
                    do
                    {
                        results.Add(idx);
                    } while (map.TryGetNextValue(out idx, ref it));
                }
            }
        }

        /// <summary>
        /// 需要覆盖 radius 的 ring 数。radius 超过 cellSize 时自动放大搜索范围。
        /// </summary>
        public static int RingFor(float radius, float invCellSize)
        {
            return math.max(1, (int)math.ceil(radius * invCellSize));
        }
    }

    /// <summary>并行写入空间哈希。</summary>
    [BurstCompile]
    internal struct JobBuildHash : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> Alive;
        public int Count;
        public float InvCellSize;
        public NativeParallelMultiHashMap<int, int>.ParallelWriter Writer;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0)
            {
                return;
            }
            Writer.Add(SpatialHash.Hash(SpatialHash.ToCell(Positions[i], InvCellSize)), i);
        }
    }
}
