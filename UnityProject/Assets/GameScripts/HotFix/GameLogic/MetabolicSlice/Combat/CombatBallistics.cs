using System;
using BinGames.Sim;
using HitEvent = ComposeEngine.Core.HitEvent;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// combat-primitive-overhaul：<see cref="HitEvent"/>（ComposeEngine 的抽象基元字段）
    /// → <see cref="ProjectileRequest"/>（内核真实弹体）的**唯一**翻译处。
    ///
    /// 为什么要单独一个类：此前同一份"弹道参数"在三个地方各写了一遍——
    /// <c>MetabolicSliceBridge</c> 算判定落点、<c>WhiteboxComposeProjectileFeedback</c> 算白模飞行、
    /// <c>WhiteboxComposeAimIndicator</c> 算预览。三处靠"引用同一个 const"维持同步，
    /// 但只要出现任何一个非线性因素（追踪转向、反弹、撞障、阻力），三条曲线立刻分叉，
    /// 于是就有了"看得见的打不到 / 打得到的看不见"。
    /// 现在弹体是内核里的真实实体，判定与渲染读同一份 <see cref="ProjectileState"/>，
    /// 本类只负责把设计字段翻译成一次性的发射参数，**翻译完就没有第二份真相了**。
    ///
    /// 世界尺度基准（换算这些数字的依据，改之前先想清楚）：
    /// 相机 orthographicSize=16（可视高 32、16:9 下宽约 57），玩家半径≈1、基础移速 8 u/s，
    /// 场地半边长 90。所以"一屏"≈ 玩家左右各 28、上下各 16。
    /// </summary>
    public static class CombatBallistics
    {
        // ── 尺度基准 ────────────────────────────────────────────────

        /// <summary>弹速基准（世界单位/秒）。<see cref="HitEvent.Speed"/> 是**倍率**（org_emitter=1.3、
        /// org_drill=2.2），不是米/秒——表里那些 1~2 的数字乘上这个基准才是真实速度。
        /// 26 u/s ≈ 玩家移速的 3.2 倍：躲得开、但躲不掉贴脸。</summary>
        public const float BaseSpeed = 26f;

        /// <summary>射程基准（世界单位）。18 ≈ 屏幕半宽的 2/3、半高的 1.1 倍——
        /// 打得到屏幕上半数敌人，但"站桩清屏"不行，必须走位。这是**射程**这个概念第一次真的存在：
        /// 此前弹道恒定飞 9 单位就消失，且与器官无关。</summary>
        public const float BaseRange = 18f;

        /// <summary>弹体碰撞半径基准。玩家半径≈1、小怪≈0.4，0.45 让"擦身而过"是真的擦过去。</summary>
        public const float BaseProjectileRadius = 0.45f;

        /// <summary>寿命下限——再快的弹也至少存在两三帧，否则只会看到一道闪光。</summary>
        public const float MinLifetime = 0.12f;
        /// <summary>寿命上限——防止 Lifetime 配错导致弹体常驻不散。</summary>
        public const float MaxLifetime = 6f;

        // ── 追踪（gene_taxis / gene_receptor）──────────────────────

        /// <summary>追踪搜敌半径（世界单位）。**这不是全屏**——射程外没有目标时弹体就照原方向直飞，
        /// 这样"往空处打"和"往敌人堆里打"是两种手感，而不是无论朝哪按都自动咬人。
        /// 13 略小于 <see cref="BaseRange"/>：出膛时锁不到的敌人，飞近了还有机会锁上。</summary>
        public const float HomingRange = 13f;

        /// <summary>追踪角速度下限/增量（度/秒）。强度 0→90°/s（几乎只是修正），1→360°/s（贴身也能咬）。
        /// 有角速度上限才像导弹；没有的话是"瞬间对准"，弹道读起来像在瞬移。</summary>
        public const float HomingTurnRateBase = 90f;
        public const float HomingTurnRateGain = 270f;

        // ── 近战（org_cilia / org_pseudopod / org_wave / Melee 底盘）──

        /// <summary>近战触及距离。玩家半径≈1，4.5 ≈ 身前一个半身位，读得出"够不着要走近"。</summary>
        public const float MeleeReach = 4.5f;
        /// <summary>近战默认扇形半角（度）。<see cref="HitEvent.SpreadAngle"/> 有值时以它为准
        /// （org_cilia 40→±20 精准刺、org_pseudopod 70→±35 挥砍、org_wave 180→±90 半圆横扫），
        /// 器官之间第一次真的有"打击范围"的差别。</summary>
        public const float MeleeDefaultHalfAngle = 50f;
        /// <summary>贴身豁免半径：这么近的敌人不吃扇形筛选（方向向量在圆心附近不稳定，且贴脸本就该打到）。</summary>
        public const float MeleeNearRadius = 1.4f;

        // ── 场地/抛投 ──────────────────────────────────────────────

        /// <summary>Field 底盘（酶雾/毒坑）抛掷落点距离。比弹道短——那是"扔出去"不是"射出去"。</summary>
        public const float FieldThrowRange = 11f;
        /// <summary><see cref="HitEvent.Gravity"/> → 阻力系数。gene_arc 的 gravity=4 → 阻力 2.2/s，
        /// 26 u/s 的初速在 ~1.2s 内衰减到停住，落点约 11 单位——比直射弹近，但能越过前排。</summary>
        public const float GravityToDrag = 0.55f;

        // ── 溅射 / 留坑 ────────────────────────────────────────────

        /// <summary>ExplodeOnHit 的爆圈半径 = 弹体半径 × 此系数。</summary>
        public const float ExplodeRadiusMul = 6f;
        /// <summary>Linger 留坑半径 = 弹体半径 × 此系数（下限 2）。</summary>
        public const float LingerRadiusMul = 5f;
        public const float LingerMinRadius = 2f;

        // ── 拖尾 ───────────────────────────────────────────────────

        /// <summary>拖尾跳伤间隔（秒）。0.12s ≈ 26 u/s 下每 3 个单位留一跳，读得出是"一条线"而不是几个点。</summary>
        public const float TrailInterval = 0.12f;

        // ── 分裂 ───────────────────────────────────────────────────

        /// <summary>分裂默认总张角（度），以**入射方向**为中轴。
        /// 旧实现用 2π·s/n 的世界系绝对角，n=2 时恒为正负 X 轴——这就是"纺锤/绽放永远横着裂开"的根因。</summary>
        public const float SplitSpreadDeg = 100f;

        /// <summary>炮口前推距离（CP-REQ-003 第③级公式的最后一项）。与
        /// <see cref="Control.OrganReleaseRunner"/> 此前各自维护的 0.2f 同一常量，
        /// M4-R00-02 队列③-10 起统一到这里，避免两条释放路的"炮口离身体多远"各算各的。</summary>
        public const float MuzzleClearance = 0.2f;

        /// <summary>
        /// CP-REQ-003 第③级发射点兜底：身体中心沿 <paramref name="direction"/> 前推
        /// <paramref name="bodyRadius"/>+<paramref name="projectileRadius"/>+<see cref="MuzzleClearance"/>，
        /// 越界/撞静态障碍物时沿同一方向做最短安全推出（公式与 <c>JobIntegrate</c> 的障碍推出
        /// 一致，纯几何，不碰内核状态）。真实器官挂点/底盘标准挂点（CP-REQ-003 第①②级）本次
        /// 未实现，登记为债务——本函数是"没有真实挂点时"唯一允许的兜底，调用方不得各自再拍一套
        /// 前推公式（那正是审计点名"炮口VFX/弹体碰撞/声音/后坐力必须读同一发射点"要防的事）。
        ///
        /// 恰好落在某个障碍正中心（无安全推出方向）时返回 false——调用方必须按 EmitterBlocked
        /// 处理，禁止瞬移到别处顶替（规格明令禁止）。<paramref name="obstacles"/> 为 null 时跳过
        /// 障碍检测，恒返回 true（给不掌握真实场景数据的调用方，如离线自检工具）。
        /// </summary>
        public static bool TryResolveEmitterPosition(
            float2 bodyPosition, float bodyRadius, float2 direction, float projectileRadius,
            ObstacleSpec[] obstacles, float arenaHalfExtent, out float2 emitterPosition)
        {
            float2 dir = math.normalizesafe(direction, new float2(0f, 1f));
            float2 pos = bodyPosition + dir * (bodyRadius + projectileRadius + MuzzleClearance);

            if (arenaHalfExtent > 0f)
            {
                pos.x = math.clamp(pos.x, -arenaHalfExtent, arenaHalfExtent);
                pos.y = math.clamp(pos.y, -arenaHalfExtent, arenaHalfExtent);
            }

            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Length; i++)
                {
                    float2 diff = pos - obstacles[i].Position;
                    float minDist = obstacles[i].Radius + projectileRadius;
                    float distSq = math.lengthsq(diff);
                    if (distSq >= minDist * minDist)
                    {
                        continue;
                    }
                    float dist = math.sqrt(distSq);
                    if (dist < 1e-4f)
                    {
                        // 中心重合：没有安全推出方向可算，交给调用方按 EmitterBlocked 拒绝，
                        // 不瞎猜一个方向瞬移过去。
                        emitterPosition = default;
                        return false;
                    }
                    pos = obstacles[i].Position + diff / dist * minDist;
                }

                // 推完之后可能又跑出场地边界（贴着场边的障碍）——再夹一次。
                if (arenaHalfExtent > 0f)
                {
                    pos.x = math.clamp(pos.x, -arenaHalfExtent, arenaHalfExtent);
                    pos.y = math.clamp(pos.y, -arenaHalfExtent, arenaHalfExtent);
                }
            }

            emitterPosition = pos;
            return true;
        }

        /// <summary>
        /// 把一个 <see cref="HitEvent"/> 的第 <paramref name="index"/> 发翻译成内核弹体发射参数。
        ///
        /// <paramref name="baseDir"/> 是玩家的瞄准方向（跟随鼠标，见 <c>CellPlayerController.ReadAimDirection</c>）；
        /// 多发按 <see cref="HitEvent.SpreadAngle"/> 以**它**为中轴左右展开——所以"纺锤分裂"
        /// 是绕鼠标方向左右裂，不是绕世界坐标轴裂。
        /// </summary>
        /// <param name="bodyRadius">发射者的身体半径（CP-REQ-003 第③级前推公式用）。默认 0——
        /// 非"身体中心为原点"的调用方（如从环形落点再分裂的二次弹）不需要这一项。</param>
        /// <param name="obstacles">当前场上的静态障碍，越界/撞障碍推出用。默认 null 时跳过推出
        /// 检测（调试/自检工具没有真实场景数据）。</param>
        /// <param name="arenaHalfExtent">场地半边长，配合 <paramref name="obstacles"/> 一起判越界。</param>
        public static ProjectileRequest Build(HitEvent evt, float2 origin, float2 baseDir,
            int index, int count, float scale, int shotId,
            float bodyRadius = 0f, ObstacleSpec[] obstacles = null, float arenaHalfExtent = 0f,
            SimEntityId sourceEntityId = default)
        {
            float speedMul = evt.Speed > 0f ? evt.Speed : 1f;
            float speed = BaseSpeed * math.clamp(speedMul, 0.25f, 6f);

            // 射程 → 寿命。Lifetime 有值时按秒解释（gene_arc 1.2s / org_drill 0.25s），
            // 否则由射程基准反推——修掉旧实现里 Lifetime 恒小于飞行时长因而"永远不生效"的死字段。
            float lifetime = evt.Lifetime > 0f ? evt.Lifetime : BaseRange / speed;
            lifetime = math.clamp(lifetime, MinLifetime, MaxLifetime);

            float radius = BaseProjectileRadius * math.max(0.2f, scale);

            float2 dir = FanDirection(baseDir, index, count, evt.SpreadAngle, evt.RadialRequested);

            var flags = SimProjectileFlags.None;
            float drag = 0f;
            if (evt.Gravity > 0f)
            {
                // 抛投：越过前排、落点炸开。俯视视角没有高度轴，"抛物线"只能用
                // "飞行途中不碰单位 + 减速 + 落地爆" 来表达，这是唯一读得出来的做法。
                drag = evt.Gravity * GravityToDrag;
                flags |= SimProjectileFlags.Lob | SimProjectileFlags.BurstOnEnd;
            }
            if (evt.ExplodeOnHit)
            {
                flags |= SimProjectileFlags.BurstOnEnd;
            }
            if (evt.Return)
            {
                flags |= SimProjectileFlags.ReturnToOwner;
            }
            if (evt.Bounce > 0f)
            {
                flags |= SimProjectileFlags.BounceWalls;
            }

            float homing = math.saturate(evt.Homing);

            // Spin/Orbit → 蛇行。真圆周运动的切向速度 = 半径 × 角速度，直接照这个换算，
            // 不另编一个"看起来差不多"的系数。Orbit 未配时给一个小默认幅度，
            // 否则 gene_flagella 只写 Spin 会完全看不出轨迹在动。
            float weaveRate = evt.Spin;
            float weaveAmp = 0f;
            if (math.abs(evt.Spin) > 0.01f)
            {
                float orbitR = evt.Orbit > 0f ? evt.Orbit : 0.8f;
                weaveAmp = orbitR * math.radians(math.abs(evt.Spin));
            }

            SimStatus applyStatus = SimStatus.None;
            if (evt.Pull > 0f)
            {
                // 内核没有"吸附"位移，用已有的 Pulled|Slowed 表达"被拖住"，与旧 DamageAreaPrimitive 同一语义。
                applyStatus |= SimStatus.Slowed | SimStatus.Pulled;
            }

            // gene_receptor「受体记忆」：命中即给目标挂 Marked，后续的弹优先追它。
            // 文案是「打过的敌人会被记住，后续更会追它」——此前这条基因只是 Homing 数值高一点，
            // 与 gene_taxis 除了强度以外毫无区别，"记忆"根本不存在。
            // 两半缺一不可：**留下记号**（这里）+ **认得记号**（内核 PreferMarked 选靶偏好）。
            if (evt.Tags.Contains("ReceptorMemory"))
            {
                applyStatus |= SimStatus.Marked;
                flags |= SimProjectileFlags.PreferMarked;
            }

            float areaRadius = 0f;
            if (evt.ExplodeOnHit || evt.Gravity > 0f)
            {
                areaRadius = radius * ExplodeRadiusMul;
            }

            // CP-REQ-003 第③级：身体中心前推+越界/障碍推出。恰好卡在障碍正中心（无安全推出
            // 方向）这种极端情形，这条自动开火路径没有"拒绝并提示玩家"的既有反馈通道（不同于
            // 直控/AI 的 DirectActionAvailability 闸门），退回不做推出检测的裸公式而不是整次
            // 攻击哑火——见 DESIGN.md 里这处不对称的说明。
            if (!TryResolveEmitterPosition(origin, bodyRadius, dir, radius, obstacles, arenaHalfExtent, out float2 emitterPos))
            {
                TryResolveEmitterPosition(origin, bodyRadius, dir, radius, null, 0f, out emitterPos);
            }

            return new ProjectileRequest
            {
                Position = emitterPos,
                Direction = dir,
                Speed = speed,
                Damage = evt.Damage,
                Radius = radius,
                Lifetime = lifetime,
                // Pierce 语义："基础 1 次命中 + N 次额外穿透"。旧实现把它当成一个额外的隔空补刀点。
                Pierce = 1 + (evt.Pierce > 0f ? (int)MathF.Round(evt.Pierce) : 0),
                TargetFaction = SimFaction.Hostile,
                ApplyStatus = applyStatus,
                SourceLogicId = shotId,
                SourceEntityId = sourceEntityId,
                VisualId = 0,

                Homing = homing,
                HomingRange = homing > 0f ? HomingRange : 0f,
                TurnRateDeg = homing > 0f ? HomingTurnRateBase + HomingTurnRateGain * homing : 0f,
                Drag = drag,
                AreaRadius = areaRadius,
                BounceCount = evt.Bounce > 0f ? (int)MathF.Round(evt.Bounce) : 0,
                SplitCount = evt.SplitOnHit > 0f ? (int)MathF.Round(evt.SplitOnHit) : 0,
                SplitAngleDeg = SplitSpreadDeg,
                TrailDamage = evt.Trail,
                TrailInterval = TrailInterval,
                LingerSeconds = evt.Linger,
                LingerRadius = math.max(LingerMinRadius, radius * LingerRadiusMul),
                ChainCount = evt.Chain > 0f ? (int)MathF.Round(evt.Chain) : 0,
                WeaveRateDeg = weaveRate,
                WeaveAmp = weaveAmp,
                Generation = 0,
                Flags = flags,
            };
        }

        /// <summary>
        /// 多发方向：以 <paramref name="baseDir"/> 为中轴，在 ±spread/2 内确定性均分
        /// （CP-REQ-012，M4-R00-02 队列③号项，合并此前全仓四套互不一致的扇形公式为这一个纯函数）。
        ///
        /// - <paramref name="count"/>&lt;=1：严格沿中轴，不产生任何偏差——旧实现曾把
        ///   <paramref name="spreadDeg"/>&gt;0 时的单发解释成"确定性抖动/精度散射"，与规格冲突
        ///   （规格里散射精度是独立的 AccuracyJitter 字段，尚未实现，不能借用 SpreadAngle 顶替）。
        /// - <paramref name="count"/>&gt;1 且 <paramref name="spreadDeg"/>&lt;=0：默认**同向发射**，
        ///   除非 <paramref name="radialRequested"/> 为 true（即 <c>Scatterer</c> 声明过"我要环射"，
        ///   见 <c>ComposeEngine.Core.HitEvent.RadialRequested</c>）——环射必须由基元显式声明，
        ///   不能从"没配扇角"隐式反推，否则任何忘记配扇角的多发都会意外变成环形爆开。
        /// - <paramref name="count"/>&gt;1 且 <paramref name="spreadDeg"/>&gt;0：在 ±half 内确定性均分。
        /// </summary>
        public static float2 FanDirection(float2 baseDir, int index, int count, float spreadDeg, bool radialRequested)
        {
            float2 n = math.normalizesafe(baseDir, new float2(0f, 1f));
            if (count <= 1)
            {
                return n;
            }

            if (spreadDeg <= 0f)
            {
                if (!radialRequested)
                {
                    return n;
                }
                float ring = 2f * math.PI * index / count;
                return Rotate(n, ring);
            }

            float half = math.radians(spreadDeg) * 0.5f;
            float u = (float)index / (count - 1);
            return Rotate(n, math.lerp(-half, half, u));
        }

        private static float2 Rotate(float2 v, float rad)
        {
            math.sincos(rad, out float sn, out float cs);
            return new float2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        /// <summary>近战扇形半角：<see cref="HitEvent.SpreadAngle"/> 优先（器官各自的挥击张角），
        /// 未配时用默认值。上限 180（整圆）。</summary>
        public static float MeleeHalfAngle(HitEvent evt) =>
            math.clamp(evt.SpreadAngle > 0f ? evt.SpreadAngle * 0.5f : MeleeDefaultHalfAngle, 10f, 180f);
    }
}
