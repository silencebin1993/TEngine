using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 持续区域推进（毒坑 / 酸洼 / 贴身光环 / 敌方毒云）。
    ///
    /// enemy-mechanics-parity：区域此前只存在于**热更层的玩家专属结构**里
    /// （<c>MetabolicSliceBridge._pendingLinger</c>），所以"敌人也会放毒"在实现层面无从谈起。
    /// 下沉进内核之后玩家与敌人共用同一套：谁伤害谁由 <see cref="ZoneState.TargetFaction"/> 决定。
    ///
    /// 每个区域到跳伤间隔就产出一条圆形 <see cref="DamageRequest"/>，交给既有的
    /// <see cref="JobDamage"/> 统一结算——**不新开一条伤害/命中/死亡事件路径**，
    /// 于是区域杀敌一样会走击杀奖励、卡牌 OnKill、连锁那些既有链路。
    ///
    /// 复杂度 O(区域数)，与场上敌人总数无关（真正的范围查询发生在 JobDamage 里，走空间哈希）。
    /// </summary>
    [BurstCompile]
    public struct JobZone : IJobParallelFor
    {
        public NativeArray<ZoneState> Zones;

        /// <summary>跟随目标（光环）的位置。跟随单位死亡时区域立即结束——
        /// 贴身圈不该在尸体上继续转。</summary>
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<byte> Alive;
        public int UnitCount;

        public NativeQueue<DamageRequest>.ParallelWriter DamageOut;

        public float Dt;

        public void Execute(int z)
        {
            ZoneState s = Zones[z];
            if (s.Alive == 0)
            {
                return;
            }

            // 跟随（光环）：宿主没了，圈也没了。
            if (s.FollowUnitIndex >= 0)
            {
                if (s.FollowUnitIndex >= UnitCount || Alive[s.FollowUnitIndex] == 0)
                {
                    s.Alive = 0;
                    Zones[z] = s;
                    return;
                }
                s.Position = Position[s.FollowUnitIndex];
            }

            if (s.GrowthRate > 0f)
            {
                float cap = s.MaxRadius > 0f ? s.MaxRadius : float.MaxValue;
                s.Radius = math.min(cap, s.Radius + s.GrowthRate * Dt);
            }

            s.TimeLeft -= Dt;
            s.TickTimer -= Dt;
            if (s.TickTimer <= 0f && s.DamagePerTick > 0f)
            {
                DamageOut.Enqueue(new DamageRequest
                {
                    Origin = s.Position,
                    Radius = s.Radius,
                    TargetIndex = SimConst.InvalidIndex,
                    Amount = s.DamagePerTick,
                    TargetFaction = (SimFaction)s.TargetFaction,
                    ApplyStatus = (SimStatus)s.ApplyStatus,
                    RequireStatus = SimStatus.None,
                    ChainCount = s.ChainCount,
                    ChainRange = 4f,
                    ChainFalloff = 0.75f,
                    SourceLogicId = s.SourceLogicId,
                });
                s.TickTimer = math.max(0.02f, s.Interval);
            }

            if (s.TimeLeft <= 0f)
            {
                s.Alive = 0;
            }

            Zones[z] = s;
        }
    }
}
