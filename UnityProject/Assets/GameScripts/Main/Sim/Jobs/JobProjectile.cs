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

        // ── combat-primitive-overhaul：弹道基元。全部 0 时下面每一段都被首行条件跳过，
        //    Execute 逐语句退化成改动前的版本（EffectProjectile 走的就是这条路，零回归）。──
        public float Homing;
        public float HomingRange;
        public float TurnRateDeg;
        public float Drag;
        public float AreaRadius;
        public int BounceLeft;
        public int SplitCount;
        public float SplitAngleDeg;
        public float TrailDamage;
        public float TrailInterval;
        public float TrailTimer;
        public float LingerSeconds;
        public float LingerRadius;
        public int ChainCount;
        public float WeaveRateDeg;
        public float WeaveAmp;
        public float WeavePhase;
        /// <summary>上一次命中的单位索引（-1 = 无）。防止穿透弹在同一个目标身上逐帧重复命中，
        /// 把穿透次数全耗在第一个人身上——那不叫穿透，叫贴脸连射。</summary>
        public int LastHitIndex;
        /// <summary>命中后的短暂免疫窗口（秒）。与 LastHitIndex 配合：前者挡"同一个目标"，
        /// 后者挡"一堆重叠目标里来回反复"。慢速弹尤其需要，它在一个敌人的碰撞范围里能待好几帧。</summary>
        public float HitCooldown;
        public byte Generation;
        public SimProjectileFlags Flags;
        /// <summary>RGBA8 打包的实例色（0 = 用渲染器默认色）。元素/反应配色由热更层算好后原样带下来，
        /// 让"真弹体"也能保留此前只有白模才有的元素染色，而不是全场一律亮黄。</summary>
        public uint Tint;
    }

    /// <summary>
    /// 投射物推进与命中检测。并行。
    /// 命中不直接扣血，而是产出 DamageRequest 交给 JobDamage 统一结算，
    /// 保持"伤害只有一个入口"的纪律。
    ///
    /// combat-primitive-overhaul：弹道基元（追踪/反弹/分裂/回旋/拖尾/抛投/溅射/蛇行）全部在这里落地。
    ///
    /// 为什么必须放在内核而不是热更层：此前热更层的做法是"开火瞬间预测一个落点 + 倒计时 + 到点打一发
    /// 范围伤害"，弹体在飞行途中**不存在**，因此
    ///   ① 途中的敌人永远不会被命中（只有落点半径内的才算），
    ///   ② 只要有任何拐弯/反弹/障碍，预测落点与表现层各自算各自的，必然分叉，
    ///   ③ 追踪要在热更层每帧找最近敌人 = 每帧 O(敌人数)，直接违反热更层性能红线。
    /// 挪进 Burst Job 后，弹体是真实实体，判定与渲染读同一份 <see cref="ProjectileState"/>，
    /// "看得见的就是打得到的"从架构上被保证，而不是靠两边抄同一个系数来维持。
    ///
    /// 二级弹体（分裂/回程）不能在并行 Job 里直接写 <see cref="Projectiles"/>（会与其它线程抢槽），
    /// 走 <see cref="SpawnOut"/> 队列交给主线程消费，与 <see cref="DamageOut"/> 同一模式。
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
        /// <summary>分裂/回旋产生的二级弹体，主线程消费后再 SpawnProjectile。</summary>
        public NativeQueue<ProjectileRequest>.ParallelWriter SpawnOut;
        /// <summary>弹体终结事件，热更层据此在**真实**落点放留坑/命中表现。</summary>
        public NativeQueue<ProjectileEndEvent>.ParallelWriter EndOut;

        public float Dt;
        public float InvCellSize;
        public int UnitCount;
        public float ArenaHalf;
        /// <summary>发射者（玩家）当前坐标，供 Return 回程弹体锁定"现在"的位置而不是发射时的位置。</summary>
        public float2 OwnerPos;

        /// <summary>溅射/终结爆的伤害相对主伤害的倍率。与热更层 ExplodeDamageMult 同值，禁止两边各写一个。</summary>
        public const float SplashDamageMul = 0.5f;
        /// <summary>分裂子弹体的伤害倍率。</summary>
        public const float SplitDamageMul = 0.5f;
        /// <summary>分裂子弹体寿命（秒）。定长而非继承母弹剩余寿命——碎片就该是短促的一小截，
        /// 不然贴脸命中和远距命中裂出来的碎片射程会差好几倍，玩家读不出规律。
        /// 0.4s × 常见弹速 26 ≈ 10 个单位。</summary>
        public const float SplitLifetime = 0.4f;
        /// <summary>二级弹体最多再生一代（Generation 到 2 即不再分裂/回旋），防止无界递归。</summary>
        public const byte MaxGeneration = 2;
        /// <summary>两次命中之间的最小间隔（秒）。见 <see cref="ProjectileState.HitCooldown"/>。</summary>
        public const float HitCooldownSeconds = 0.08f;

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
                End(ref s, ProjectileEndReason.Expired);
                Projectiles[p] = s;
                return;
            }

            // ① 追踪：在 HomingRange 内找最近的合法目标，按 TurnRateDeg 上限转向。
            //    **有射程**是刻意的设计约束——射程外不锁定，弹体就照原方向飞出去。
            if (s.Homing > 0f && s.HomingRange > 0f)
            {
                Steer(ref s);
            }

            // ② 蛇行：绕飞行轴左右摆。鞭毛绕/乱流靠它读出"轨迹本身在动"，而不是换个贴图。
            if (s.WeaveAmp > 0f)
            {
                s.WeavePhase += math.radians(s.WeaveRateDeg) * Dt;
                float2 fwd = math.normalizesafe(s.Velocity, new float2(0f, 1f));
                float2 side = new float2(-fwd.y, fwd.x);
                s.Position += side * (math.cos(s.WeavePhase) * s.WeaveAmp * Dt);
            }

            // ③ 阻力：抛投/重力弹靠减速自然缩短射程，而不是靠一个"射程"字段硬截断。
            if (s.Drag > 0f)
            {
                s.Velocity *= math.max(0f, 1f - s.Drag * Dt);
                if (math.lengthsq(s.Velocity) < 0.25f)
                {
                    // 已经基本停住 = 落地，走终结（抛投在这里炸开）。
                    End(ref s, ProjectileEndReason.Expired);
                    Projectiles[p] = s;
                    return;
                }
            }

            s.Position += s.Velocity * Dt;

            // ④ 出界：BounceWalls 时镜面反射并消耗一次反弹，否则消失。
            //    反射是真的按撞到哪面墙算法线，不是"掉头 180°±30°"那种假反弹。
            if (math.abs(s.Position.x) > ArenaHalf || math.abs(s.Position.y) > ArenaHalf)
            {
                if ((s.Flags & SimProjectileFlags.BounceWalls) != 0 && s.BounceLeft > 0)
                {
                    if (math.abs(s.Position.x) > ArenaHalf)
                    {
                        s.Velocity.x = -s.Velocity.x;
                        s.Position.x = math.clamp(s.Position.x, -ArenaHalf, ArenaHalf);
                    }
                    if (math.abs(s.Position.y) > ArenaHalf)
                    {
                        s.Velocity.y = -s.Velocity.y;
                        s.Position.y = math.clamp(s.Position.y, -ArenaHalf, ArenaHalf);
                    }
                    s.BounceLeft--;
                }
                else
                {
                    End(ref s, ProjectileEndReason.OutOfBounds);
                    Projectiles[p] = s;
                    return;
                }
            }

            // ⑤ 撞障：同样支持反弹（法线取障碍圆心→弹体），否则销毁。
            for (int o = 0; o < ObstacleCount; o++)
            {
                float reachO = s.Radius + ObstacleRadius[o];
                float2 d = s.Position - ObstaclePos[o];
                if (math.lengthsq(d) > reachO * reachO)
                {
                    continue;
                }

                if ((s.Flags & SimProjectileFlags.BounceWalls) != 0 && s.BounceLeft > 0)
                {
                    float2 n = math.normalizesafe(d, new float2(0f, 1f));
                    s.Velocity = s.Velocity - 2f * math.dot(s.Velocity, n) * n;
                    s.Position = ObstaclePos[o] + n * (reachO + 0.05f);
                    s.BounceLeft--;
                    break;
                }

                End(ref s, ProjectileEndReason.Obstacle);
                Projectiles[p] = s;
                return;
            }

            // ⑥ 拖尾：沿途按间隔留一发小额范围伤害。飞行路径本身造成伤害，
            //    不是只在终点算一次——gene_pyro「燃径」/gene_slime「粘液拖尾」的字面语义。
            if (s.TrailDamage > 0f)
            {
                s.TrailTimer -= Dt;
                if (s.TrailTimer <= 0f)
                {
                    s.TrailTimer = math.max(0.05f, s.TrailInterval);
                    DamageOut.Enqueue(new DamageRequest
                    {
                        Origin = s.Position,
                        Radius = math.max(0.6f, s.Radius * 2f),
                        TargetIndex = SimConst.InvalidIndex,
                        Amount = s.TrailDamage,
                        TargetFaction = (SimFaction)s.TargetFaction,
                        ApplyStatus = (SimStatus)s.ApplyStatus,
                        RequireStatus = SimStatus.None,
                        ChainCount = 0,
                        SourceLogicId = s.SourceLogicId,
                    });
                }
            }

            // ⑦ 单位命中。抛投（Lob）跳过这一段——它从敌人头顶飞过，只在落点炸。
            if (s.HitCooldown > 0f)
            {
                s.HitCooldown -= Dt;
            }
            if ((s.Flags & SimProjectileFlags.Lob) == 0 && s.HitCooldown <= 0f)
            {
                ScanUnits(ref s);
            }

            if (s.Alive == 0)
            {
                End(ref s, ProjectileEndReason.HitTarget);
            }

            Projectiles[p] = s;
        }

        /// <summary>
        /// 追踪转向。在空间哈希里按 HomingRange 取邻域找最近的合法目标，
        /// 把速度方向朝目标转，单帧转角受 TurnRateDeg 限制、整体权重受 Homing 限制。
        ///
        /// 复杂度是 O(弹体数 × HomingRange 覆盖格数)，**与场上敌人总数无关**——
        /// 这正是必须放内核而不是热更层的原因（热更层没有空间哈希，只能全量线性扫描）。
        /// </summary>
        private void Steer(ref ProjectileState s)
        {
            int ring = SpatialHash.RingFor(s.HomingRange, InvCellSize);
            int2 c = SpatialHash.ToCell(s.Position, InvCellSize);
            float bestSq = s.HomingRange * s.HomingRange;
            float2 best = default;
            bool found = false;

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
                        if (j >= UnitCount || Alive[j] == 0)
                        {
                            continue;
                        }
                        if (s.TargetFaction != (byte)SimFaction.None && Faction[j] != s.TargetFaction)
                        {
                            continue;
                        }
                        float dsq = math.distancesq(Position[j], s.Position);
                        if (dsq < bestSq)
                        {
                            bestSq = dsq;
                            best = Position[j];
                            found = true;
                        }
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }

            if (!found)
            {
                return;
            }

            float speed = math.length(s.Velocity);
            if (speed < 0.001f)
            {
                return;
            }

            float2 cur = s.Velocity / speed;
            float2 want = math.normalizesafe(best - s.Position, cur);

            // 有符号夹角 → 单帧转角上限 → 按 Homing 权重缩放。三步都保留，
            // 少任何一步都会退化成"瞬间锁头"（不像导弹，且贴脸时会绕着敌人打转）。
            float cross = cur.x * want.y - cur.y * want.x;
            float dot = math.clamp(math.dot(cur, want), -1f, 1f);
            float delta = math.atan2(cross, dot) * s.Homing;
            float maxStep = math.radians(s.TurnRateDeg > 0f ? s.TurnRateDeg : 360f) * Dt;
            delta = math.clamp(delta, -maxStep, maxStep);

            math.sincos(delta, out float sn, out float cs);
            s.Velocity = new float2(cur.x * cs - cur.y * sn, cur.x * sn + cur.y * cs) * speed;
        }

        /// <summary>邻域内的单位命中判定。命中即产出 DamageRequest（AreaRadius&gt;0 时是溅射圆，否则单体）。</summary>
        private void ScanUnits(ref ProjectileState s)
        {
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
                        if (j == s.LastHitIndex)
                        {
                            // 刚打过这个人，别在他身上把穿透次数刷光——穿透的语义是"穿过去打下一个"。
                            continue;
                        }

                        float reach = s.Radius + Radius[j];
                        if (math.distancesq(Position[j], s.Position) > reach * reach)
                        {
                            continue;
                        }

                        if (s.AreaRadius > 0f)
                        {
                            // 溅射：以命中点为圆心打一圈，主目标也在圈内，不重复补单体。
                            DamageOut.Enqueue(new DamageRequest
                            {
                                Origin = s.Position,
                                Radius = s.AreaRadius,
                                TargetIndex = SimConst.InvalidIndex,
                                Amount = s.Damage,
                                TargetFaction = (SimFaction)s.TargetFaction,
                                ApplyStatus = (SimStatus)s.ApplyStatus,
                                RequireStatus = SimStatus.None,
                                ChainCount = s.ChainCount,
                                ChainRange = 4f,
                                ChainFalloff = 0.75f,
                                SourceLogicId = s.SourceLogicId,
                            });
                        }
                        else
                        {
                            DamageOut.Enqueue(new DamageRequest
                            {
                                Origin = s.Position,
                                Radius = -1f,
                                TargetIndex = j,
                                Amount = s.Damage,
                                TargetFaction = (SimFaction)s.TargetFaction,
                                ApplyStatus = (SimStatus)s.ApplyStatus,
                                RequireStatus = SimStatus.None,
                                ChainCount = s.ChainCount,
                                ChainRange = 4f,
                                ChainFalloff = 0.75f,
                                SourceLogicId = s.SourceLogicId,
                            });
                        }

                        s.LastHitIndex = j;
                        s.HitCooldown = HitCooldownSeconds;
                        s.PierceLeft--;
                        if (s.PierceLeft > 0)
                        {
                            // 还能穿。本帧不再继续扫（冷却已置），下一帧到了新位置再找下一个目标。
                            return;
                        }

                        // 穿透用尽。带反弹的弹体在这里从目标身上弹开而不是消失
                        // （gene_elastic 文案明写"撞边**或撞敌**会弹"，旧实现只有一个"掉头 180°±30°"
                        // 的假反弹，既不看撞到了谁也不看撞在哪面）。法线取"目标圆心 → 弹体"。
                        if ((s.Flags & SimProjectileFlags.BounceWalls) != 0 && s.BounceLeft > 0)
                        {
                            float2 n = math.normalizesafe(s.Position - Position[j], -math.normalizesafe(s.Velocity));
                            s.Velocity = s.Velocity - 2f * math.dot(s.Velocity, n) * n;
                            s.Position = Position[j] + n * (reach + 0.05f);
                            s.BounceLeft--;
                            s.PierceLeft = 1;
                            break;
                        }

                        s.Alive = 0;
                        break;
                    } while (Hash.TryGetNextValue(out j, ref it));
                }
            }
        }

        /// <summary>
        /// 弹体终结：落点爆 → 分裂 → 回程 → 回传终结事件。四件事都以**真实终结点/真实入射方向**为基准，
        /// 这是"分裂方向跟随入射而不是世界 X 轴"与"留坑落在真的打中的地方"的共同前提。
        /// </summary>
        private void End(ref ProjectileState s, ProjectileEndReason reason)
        {
            s.Alive = 0;
            float2 dir = math.normalizesafe(s.Velocity, new float2(0f, 1f));

            // ① 落点爆：抛投落地 / ExplodeOnHit。命中终结时若已经走过溅射就不重复炸。
            bool burst = (s.Flags & SimProjectileFlags.BurstOnEnd) != 0;
            if (burst && !(reason == ProjectileEndReason.HitTarget && s.AreaRadius > 0f))
            {
                DamageOut.Enqueue(new DamageRequest
                {
                    Origin = s.Position,
                    Radius = math.max(1f, s.AreaRadius > 0f ? s.AreaRadius : s.Radius * 4f),
                    TargetIndex = SimConst.InvalidIndex,
                    Amount = s.Damage * SplashDamageMul,
                    TargetFaction = (SimFaction)s.TargetFaction,
                    ApplyStatus = (SimStatus)s.ApplyStatus,
                    RequireStatus = SimStatus.None,
                    ChainCount = 0,
                    SourceLogicId = s.SourceLogicId,
                });
            }

            if (s.Generation < MaxGeneration)
            {
                // ② 分裂：以入射方向为中轴左右均分展开。
                //    旧实现用 2πs/n 的世界系绝对角，n=2 时恒为正负 X 轴——玩家看到的"永远横着裂开"就是这里。
                if (s.SplitCount > 0)
                {
                    float speed = math.max(2f, math.length(s.Velocity));
                    float half = math.radians(s.SplitAngleDeg > 0f ? s.SplitAngleDeg : 90f) * 0.5f;
                    for (int k = 0; k < s.SplitCount; k++)
                    {
                        float t = s.SplitCount == 1 ? 0.5f : (float)k / (s.SplitCount - 1);
                        float ang = math.lerp(-half, half, t);
                        math.sincos(ang, out float sn, out float cs);
                        float2 sd = new float2(dir.x * cs - dir.y * sn, dir.x * sn + dir.y * cs);
                        SpawnOut.Enqueue(new ProjectileRequest
                        {
                            Position = s.Position + sd * (s.Radius + 0.1f),
                            Direction = sd,
                            Speed = speed,
                            Damage = s.Damage * SplitDamageMul,
                            Radius = math.max(0.1f, s.Radius * 0.8f),
                            Lifetime = SplitLifetime,
                            Pierce = 1,
                            TargetFaction = (SimFaction)s.TargetFaction,
                            ApplyStatus = (SimStatus)s.ApplyStatus,
                            SourceLogicId = s.SourceLogicId,
                            VisualId = s.VisualId,
                            Generation = (byte)(s.Generation + 1),
                            ChainCount = s.ChainCount,
                        });
                    }
                }

                // ③ 回程：朝发射者**当前**位置飞回（玩家这段时间已经移动了）。
                if ((s.Flags & SimProjectileFlags.ReturnToOwner) != 0)
                {
                    float2 back = math.normalizesafe(OwnerPos - s.Position, -dir);
                    float dist = math.max(1f, math.distance(OwnerPos, s.Position));
                    float speed = math.max(6f, math.length(s.Velocity));
                    SpawnOut.Enqueue(new ProjectileRequest
                    {
                        Position = s.Position,
                        Direction = back,
                        Speed = speed,
                        Damage = s.Damage,
                        Radius = s.Radius,
                        // 刚好够飞回去，不多不少——飞回半路就消失是旧实现"弹道提前结束"的同类问题。
                        Lifetime = dist / speed + 0.1f,
                        Pierce = math.max(1, s.PierceLeft),
                        TargetFaction = (SimFaction)s.TargetFaction,
                        ApplyStatus = (SimStatus)s.ApplyStatus,
                        SourceLogicId = s.SourceLogicId,
                        VisualId = s.VisualId,
                        Generation = (byte)(s.Generation + 1),
                        Flags = SimProjectileFlags.Returning,
                        ChainCount = s.ChainCount,
                    });
                }
            }

            // ④ 终结事件：热更层据此在真实落点放留坑/命中表现。永远回传（哪怕没有 Linger），
            //    因为表现层需要知道"这一发到底在哪儿没的"。
            EndOut.Enqueue(new ProjectileEndEvent
            {
                Position = s.Position,
                Direction = dir,
                Damage = s.Damage,
                AreaRadius = s.AreaRadius,
                LingerSeconds = s.LingerSeconds,
                LingerRadius = s.LingerRadius,
                SourceLogicId = s.SourceLogicId,
                VisualId = s.VisualId,
                Reason = reason,
            });
        }
    }
}
