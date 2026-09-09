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

        /// <summary>
        /// 把一个 <see cref="HitEvent"/> 的第 <paramref name="index"/> 发翻译成内核弹体发射参数。
        ///
        /// <paramref name="baseDir"/> 是玩家的瞄准方向（跟随鼠标，见 <c>CellPlayerController.ReadAimDirection</c>）；
        /// 多发按 <see cref="HitEvent.SpreadAngle"/> 以**它**为中轴左右展开——所以"纺锤分裂"
        /// 是绕鼠标方向左右裂，不是绕世界坐标轴裂。
        /// </summary>
        public static ProjectileRequest Build(HitEvent evt, float2 origin, float2 baseDir,
            int index, int count, float scale, int shotId, uint jitterSeed)
        {
            float speedMul = evt.Speed > 0f ? evt.Speed : 1f;
            float speed = BaseSpeed * math.clamp(speedMul, 0.25f, 6f);

            // 射程 → 寿命。Lifetime 有值时按秒解释（gene_arc 1.2s / org_drill 0.25s），
            // 否则由射程基准反推——修掉旧实现里 Lifetime 恒小于飞行时长因而"永远不生效"的死字段。
            float lifetime = evt.Lifetime > 0f ? evt.Lifetime : BaseRange / speed;
            lifetime = math.clamp(lifetime, MinLifetime, MaxLifetime);

            float radius = BaseProjectileRadius * math.max(0.2f, scale);

            float2 dir = FanDirection(baseDir, index, count, evt.SpreadAngle, jitterSeed);

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

            return new ProjectileRequest
            {
                Position = origin + dir * (radius + 0.6f),
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
        /// 多发方向：以 <paramref name="baseDir"/> 为中轴，在 ±spread/2 内均分。
        ///
        /// count==1 且 spread&gt;0 时给一个**确定性**抖动（不是均分到中轴）——这样"扇散"对单发武器
        /// 也是有意义的（散射精度），而不是只有多发才生效的死参数。用 shot 序号做种子，
        /// 保证同一次开火重复调用（判定/表现/预览）拿到完全相同的方向。
        /// </summary>
        public static float2 FanDirection(float2 baseDir, int index, int count, float spreadDeg, uint jitterSeed)
        {
            float2 n = math.normalizesafe(baseDir, new float2(0f, 1f));
            if (spreadDeg <= 0f)
            {
                // 无扇角的多发：退回环形均分（原地爆开式多发，如无 Spread 的 Scatterer）。
                if (count <= 1)
                {
                    return n;
                }
                float ring = 2f * math.PI * index / count;
                return Rotate(n, ring);
            }

            float half = math.radians(spreadDeg) * 0.5f;
            if (count <= 1)
            {
                uint h = jitterSeed * 2654435761u + 0x9E3779B9u;
                h ^= h >> 15;
                float t = (h & 0xFFFFu) / 65535f * 2f - 1f;
                return Rotate(n, t * half);
            }

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
