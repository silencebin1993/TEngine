using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 行为原型驱动的转向。并行，每个单位独立计算 DesiredDir。
    ///
    /// 关键设计：敌人 AI 不是"一种敌人一个类"，而是 8 个原型 × 配置参数。
    /// 20 种敌人复用这 8 个 case，新增敌人是配表工作。
    /// 详见 DesignDocs/Game_Framework_Design.md §4.3。
    /// </summary>
    [BurstCompile]
    public struct JobSteering : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Position;
        [ReadOnly] public NativeArray<float2> Velocity;
        [ReadOnly] public NativeArray<float> Radius;
        [ReadOnly] public NativeArray<byte> Faction;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        public NativeArray<uint> Status;
        [ReadOnly] public NativeArray<BehaviorArchetype> Archetypes;

        [NativeDisableParallelForRestriction]
        public NativeArray<float2> DesiredDir;
        [NativeDisableParallelForRestriction]
        public NativeArray<float> AttackTimer;

        public float2 PlayerPos;
        public float Time;
        public float Dt;
        public int Count;
        public float ArenaHalf;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0 || i == SimConst.PlayerIndex)
            {
                return;
            }

            uint st = Status[i];
            // 麻痹：完全不动
            if ((st & (uint)SimStatus.Stunned) != 0u)
            {
                DesiredDir[i] = float2.zero;
                return;
            }

            int aid = ArchetypeId[i];
            BehaviorArchetype arc = aid >= 0 && aid < Archetypes.Length
                ? Archetypes[aid]
                : BehaviorArchetype.Default;

            float2 pos = Position[i];
            float2 toPlayer = PlayerPos - pos;
            float distSq = math.lengthsq(toPlayer);
            float dist = math.sqrt(distSq);
            float2 dirToPlayer = dist > 0.0001f ? toPlayer / dist : float2.zero;

            BehaviorKind kind = arc.Kind;

            // 恐惧状态强制转为逃逸，覆盖原型行为
            if ((st & (uint)SimStatus.Feared) != 0u)
            {
                kind = BehaviorKind.Flee;
            }

            float2 want = float2.zero;

            switch (kind)
            {
                case BehaviorKind.Stationary:
                    want = float2.zero;
                    break;

                case BehaviorKind.Drift:
                    want = Wander(i, Time) * arc.WanderStrength;
                    break;

                case BehaviorKind.Chase:
                    // 超出索敌范围则漂浮，制造"没发现你"的观感
                    want = (arc.AggroRange <= 0f || dist <= arc.AggroRange)
                        ? dirToPlayer
                        : Wander(i, Time) * arc.WanderStrength;
                    break;

                case BehaviorKind.Patrol:
                {
                    // 用索引哈希决定巡逻轴向，碰壁反向
                    float phase = Hash01(i) * 6.2831853f;
                    float2 axis = new float2(math.cos(phase), math.sin(phase));
                    float proj = math.dot(pos, axis);
                    float limit = ArenaHalf * 0.85f;
                    want = proj > limit ? -axis : (proj < -limit ? axis : axis);
                    break;
                }

                case BehaviorKind.Charge:
                {
                    // AttackTimer 复用为蓄力计时：> 0 蓄力中（不动），<= 0 冲刺
                    float t = AttackTimer[i];
                    if (t > 0f)
                    {
                        want = float2.zero;
                        Status[i] = st | (uint)SimStatus.Telegraphing;
                    }
                    else
                    {
                        want = dirToPlayer;
                        Status[i] = st & ~(uint)SimStatus.Telegraphing;
                    }
                    break;
                }

                case BehaviorKind.Ranged:
                {
                    // 保持 PreferredRange：太近后退，太远前进，合适则侧移
                    float pref = arc.PreferredRange > 0f ? arc.PreferredRange : arc.AttackRange;
                    float band = pref * 0.2f;
                    if (dist < pref - band)
                    {
                        want = -dirToPlayer;
                    }
                    else if (dist > pref + band)
                    {
                        want = dirToPlayer;
                    }
                    else
                    {
                        want = new float2(-dirToPlayer.y, dirToPlayer.x);
                    }
                    break;
                }

                case BehaviorKind.Swarm:
                    // 群体：朝玩家但混入游走，形成松散推进而非直线列队
                    want = math.normalizesafe(dirToPlayer + Wander(i, Time) * 0.6f);
                    break;

                case BehaviorKind.Flee:
                    want = -dirToPlayer;
                    break;

                case BehaviorKind.Orbit:
                {
                    float pref = arc.PreferredRange > 0f ? arc.PreferredRange : 6f;
                    float2 tangent = new float2(-dirToPlayer.y, dirToPlayer.x);
                    float radialErr = dist - pref;
                    want = math.normalizesafe(tangent + dirToPlayer * math.clamp(radialErr / pref, -1f, 1f));
                    break;
                }

                case BehaviorKind.Latch:
                    // 附着：贴住玩家不放
                    want = dist > Radius[i] + 0.4f ? dirToPlayer : float2.zero;
                    break;
            }

            // 转向速率限制：让重型单位转向迟钝，形成可被风筝的手感
            if (arc.TurnRate > 0f)
            {
                float2 cur = math.normalizesafe(Velocity[i]);
                if (math.lengthsq(cur) > 0.0001f && math.lengthsq(want) > 0.0001f)
                {
                    float maxRad = arc.TurnRate * Dt;
                    want = RotateToward(cur, math.normalizesafe(want), maxRad);
                }
            }

            DesiredDir[i] = math.normalizesafe(want);

            float timer = AttackTimer[i] - Dt;
            AttackTimer[i] = timer;
        }

        /// <summary>把 from 朝 to 旋转最多 maxRad 弧度。</summary>
        private static float2 RotateToward(float2 from, float2 to, float maxRad)
        {
            float dot = math.clamp(math.dot(from, to), -1f, 1f);
            float ang = math.acos(dot);
            if (ang <= maxRad)
            {
                return to;
            }
            float t = maxRad / math.max(ang, 0.0001f);
            return math.normalizesafe(math.lerp(from, to, t));
        }

        /// <summary>
        /// 低成本确定性游走。不用随机状态，靠索引 + 时间哈希，Burst 友好且可复现。
        /// </summary>
        private static float2 Wander(int i, float t)
        {
            float a = Hash01((uint)i * 2654435761u) * 6.2831853f;
            float b = Hash01((uint)i * 40503u + 17u) * 0.7f + 0.25f;
            float ang = a + t * b;
            return new float2(math.cos(ang), math.sin(ang));
        }

        private static float Hash01(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x & 0xFFFFFFu) / 16777215f;
        }

        private static float Hash01(int i) => Hash01((uint)i * 2654435761u);
    }
}
