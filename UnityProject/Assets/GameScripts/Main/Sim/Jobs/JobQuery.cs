using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 吞噬候选筛选。
    ///
    /// 细胞阶段的核心动词是吞噬，判定规则是"体积门槛"：
    /// 玩家体积必须达到目标体积的 DevourRatio 倍才能吞下。
    /// 破体（Breached）与腐蚀（Corroded）状态会降低这个门槛——
    /// 这正是"吞噬扩张"路线大量卡牌的作用点。
    ///
    /// 内核只做筛选，实际吞噬结算（营养质、进化能、连吃层数）在热更层。
    /// </summary>
    [BurstCompile]
    public struct JobDevourScan : IJob
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<uint> Status;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;

        public NativeList<int> Candidates;

        public float2 PlayerPos;
        public float PlayerRadius;
        public float InvCellSize;
        public int Count;

        /// <summary>基础体积比门槛。玩家半径 >= 目标半径 * Ratio 才能吞。</summary>
        public float DevourRatio;
        /// <summary>破体状态下门槛的折扣系数（越小越容易吞）。</summary>
        public float BreachedDiscount;
        /// <summary>腐蚀状态下门槛的折扣系数。</summary>
        public float CorrodedDiscount;
        /// <summary>接触判定的额外容差，让吞噬手感宽松一点。</summary>
        public float ContactSlack;

        public void Execute()
        {
            float scan = PlayerRadius + 3f;
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

                        byte f = Faction[j];
                        // 只有敌对、中立与拾取物可被吞噬
                        if (f != (byte)SimFaction.Hostile
                            && f != (byte)SimFaction.Neutral
                            && f != (byte)SimFaction.Pickup)
                        {
                            continue;
                        }

                        uint st = Status[j];
                        if ((st & (uint)SimStatus.Unedible) != 0u)
                        {
                            continue;
                        }

                        // 接触判定
                        float reach = PlayerRadius + Radius[j] + ContactSlack;
                        if (math.distancesq(Position[j], PlayerPos) > reach * reach)
                        {
                            continue;
                        }

                        // 体积门槛，受破体/腐蚀折扣
                        float ratio = DevourRatio;
                        if ((st & (uint)SimStatus.Breached) != 0u) { ratio *= BreachedDiscount; }
                        if ((st & (uint)SimStatus.Corroded) != 0u) { ratio *= CorrodedDiscount; }

                        // 拾取物无条件可吞
                        if (f != (byte)SimFaction.Pickup && PlayerRadius < Radius[j] * ratio)
                        {
                            continue;
                        }

                        Candidates.Add(j);
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }
        }
    }

    /// <summary>
    /// 把 Health &lt;= 0 但尚未处理的单位收集为死亡事件。
    /// 槽位回收在主线程做（需要写自由列表），这里只收集索引。
    /// </summary>
    [BurstCompile]
    public struct JobCollectDeaths : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Health;
        public NativeArray<byte> Alive;
        public NativeQueue<int>.ParallelWriter DeadOut;
        public int Count;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0)
            {
                return;
            }
            if (Health[i] > 0f)
            {
                return;
            }
            Alive[i] = 0;
            DeadOut.Enqueue(i);
        }
    }
}
