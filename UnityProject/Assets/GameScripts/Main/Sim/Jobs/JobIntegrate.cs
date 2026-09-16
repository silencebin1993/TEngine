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
        [ReadOnly] public NativeArray<UnitIntent> Intents;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<uint> Status;
        [ReadOnly] public NativeArray<int> ArchetypeId;
        [ReadOnly] public NativeArray<BehaviorArchetype> Archetypes;
        [ReadOnly] public NativeArray<float> AttackTimer;
        /// <summary>单位半径（story-009 障碍碰撞用；此前本 job 不需要它）。</summary>
        [ReadOnly] public NativeArray<float> Radius;
        /// <summary>静态障碍（story-009）。数量小，线性扫描。</summary>
        [ReadOnly] public NativeArray<float2> ObstaclePos;
        [ReadOnly] public NativeArray<float> ObstacleRadius;
        public int ObstacleCount;

        public NativeArray<float2> Position;
        public NativeArray<float2> Velocity;
        /// <summary>M4-R00-02 队列③-10（CP-REQ-004）：最后有效身体朝向。见 <see cref="Execute"/>
        /// 末尾的写入条件——速度接近零时不写，规格明令禁止零向量重置朝向。</summary>
        public NativeArray<float2> BodyForward;

        public float Dt;
        public int Count;
        public float ArenaHalf;
        /// <summary>减速状态的速度倍率。</summary>
        public float SlowMul;

        /// <summary>玩家直控时的加速度下限。与 <c>BehaviorArchetype.Default.Accel</c> 同值，
        /// 保证任何身体在玩家手里的跟手程度一致。见 <see cref="Execute"/> 里的说明。</summary>
        public const float PlayerControlMinAccel = 8f;

        /// <summary>
        /// 行为原型没填加速度（<c>Accel &lt;= 0</c>）时的默认值。
        /// **0 的含义是"这一格没填"，不是"极慢"**——固着类原型靠 <c>BehaviorKind.Stationary</c>
        /// 让 AI 不产生移动意图，从来不靠把加速度写成 0；一旦它收到命令或被接管，
        /// 0 就会变成每帧万分之 1.7 的蠕动。与 <see cref="PlayerControlMinAccel"/> 同值。
        /// </summary>
        public const float DefaultAccel = 8f;

        public void Execute(int i)
        {
            if (i >= Count || Alive[i] == 0)
            {
                return;
            }

            uint st = Status[i];
            float speed = MaxSpeed[i] * math.max(0f, Intents[i].SpeedMul);

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

            // 指数平滑趋近目标速度，Accel 越大越跟手。
            //
            // 2026-09-14（第二次改，这次修根）：**`Accel <= 0` 一律当"没填"，落到默认值**。
            //
            // 原来那句 `math.max(0.01f, accel)` 本意只是防除零，实际效果是把"没填"悄悄变成
            // "极慢"：0 → 0.01 → 每帧只逼近目标速度的万分之 1.7，按住方向两秒多才爬到
            // 0.097 u/s（实测）。玩家读到的是"这个角色移速巨慢"，而表里那一格根本是空的。
            //
            // 上一版只给**玩家直控**路径加了下限，于是同一个单位在 RTS 命令下照旧蠕动——
            // 玩家当场又报了一次「战术视角下这个角色还是速度不对（直控是对的）」。
            // 按路径打补丁是错的：移动意图可以来自玩家、命令、AI 三处，补一处漏两处。
            // 判据只留一条：**这个原型有没有填加速度**。
            //
            // 不去抬高"填了但很小"的原型（Drift 3.0 的飘忽感是故意的），只接管 0 这一种。
            float accel = arc.Accel > 0f ? arc.Accel : DefaultAccel;
            // 玩家直控再额外保证跟手：行为原型描述的是"这个 AI 怎么动"，
            // 不该决定"玩家开它跟不跟手"——单位差异只应来自装配的器官。
            if (Intents[i].Source == BinGames.Sim.IntentSource.Player)
            {
                accel = math.max(accel, PlayerControlMinAccel);
            }
            float k = 1f - math.exp(-math.max(0.01f, accel) * Dt);
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

            // 静态障碍：推到边界外 + 只清掉指向障碍内部的法向速度分量，
            // 保留切向分量以形成贴边滑行绕行观感（story-009 D6）。
            // 正对障碍中心直冲的 Chase/Charge 敌人会被夹在边缘原地——这是预期的"卡位"效果（D7），不是 bug。
            float unitRadius = Radius[i];
            for (int o = 0; o < ObstacleCount; o++)
            {
                float2 diff = pos - ObstaclePos[o];
                float minDist = ObstacleRadius[o] + unitRadius;
                float distSq = math.lengthsq(diff);
                if (distSq >= minDist * minDist)
                {
                    continue;
                }
                float dist = math.sqrt(distSq);
                float2 normal = dist > 0.0001f ? diff / dist : new float2(1f, 0f);
                pos = ObstaclePos[o] + normal * minDist;
                float vn = math.dot(vel, normal);
                if (vn < 0f)
                {
                    vel -= normal * vn;
                }
            }

            Position[i] = pos;
            Velocity[i] = vel;

            // CP-REQ-004：只在速度真的非零时更新朝向，静止/停顿帧保留上一次的有效值——
            // 这正是规格要求的"零向量不能把朝向重置为世界轴"，写在这里（而不是"用 desired
            // 而非实际 vel"）是因为 vel 才是这具身体这一刻真正在动的方向，desired 只是意图，
            // 撞墙/被推开时两者可能不一致，朝向应该跟着"真的在往哪走"。
            if (math.lengthsq(vel) > 1e-6f)
            {
                BodyForward[i] = math.normalize(vel);
            }
        }
    }
}
