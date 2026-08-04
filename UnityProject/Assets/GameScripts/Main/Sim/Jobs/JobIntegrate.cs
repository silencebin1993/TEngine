using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 位移积分 + 场地边界。并行。
    /// 把 DesiredDir、SeparationForce、MaxSpeed 合成为实际速度与位置。
    /// </summary>
    [BurstCompile]
    public struct JobIntegrate : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> DesiredDir;
        [ReadOnly] public NativeArray<float2> SeparationForce;
        [ReadOnly] public NativeArray<float> MaxSpeed;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<uint> Status;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeArray<BehaviorArchetype> Archetypes;
        [ReadOnly] public NativeArray<float> AttackTimer;

        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;

        public float Dt;
        public int Count;
        public float ArenaHalf;
        /// <summary>减速状态的速度倍率。</summary>
        public float SlowMul;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0)
            {
                return;
            }

            uint st = Status[i];
            float speed = MaxSpeed[i];

            if ((st & (uint)SimStatus.Stunned) != 0u)
            {
                speed = 0f;
            }
            else if ((st & (uint)SimStatus.Slowed) != 0u)
            {
                speed *= SlowMul;
            }

            int aid = ArchetypeId[i];
            BehaviorArchetype arc = aid >= 0 && aid < Archetypes.Length
                ? Archetypes[aid]
                : BehaviorArchetype.Default;

            // Charge 原型在冲刺期（蓄力计时耗尽后）获得速度倍率
            if (arc.Kind == BehaviorKind.Charge && AttackTimer[i] <= 0f)
            {
                speed *= math.max(1f, arc.ChargeSpeedMul);
            }

            float2 desired = DesiredDir[i] * speed;
            float2 vel = Velocity[i];

            // 指数平滑趋近目标速度，Accel 越大越跟手
            float k = 1f - math.exp(-math.max(0.01f, arc.Accel) * Dt);
            vel = math.lerp(vel, desired, k);

            // 分离力直接叠加到速度上，但不让它突破速度上限太多
            vel += SeparationForce[i] * speed * Dt * 4f;
            float vLen = math.length(vel);
            float cap = speed * 1.35f;
            if (vLen > cap && vLen > 0.0001f)
            {
                vel = vel / vLen * cap;
            }

            float2 pos = Position[i] + vel * Dt;

            // 场地边界：夹住位置并清掉朝外的速度分量，形成贴墙滑行
            if (pos.x < -ArenaHalf) { pos.x = -ArenaHalf; vel.x = math.max(0f, vel.x); }
            else if (pos.x > ArenaHalf) { pos.x = ArenaHalf; vel.x = math.min(0f, vel.x); }
            if (pos.y < -ArenaHalf) { pos.y = -ArenaHalf; vel.y = math.max(0f, vel.y); }
            else if (pos.y > ArenaHalf) { pos.y = ArenaHalf; vel.y = math.min(0f, vel.y); }

            Position[i] = pos;
            Velocity[i] = vel;
        }
    }
}
