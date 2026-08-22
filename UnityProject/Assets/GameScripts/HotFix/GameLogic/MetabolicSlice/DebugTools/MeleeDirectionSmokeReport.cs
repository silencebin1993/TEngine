using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-007：近战方向性冒烟验证。<see cref="MetabolicSliceBridge.ApplyEvent"/> 的前方扇形几何计算只读
    /// _sim.PlayerPosition（未 Begin 时恒为 float2.zero）与 _abilities.AimDirection，DamageArea 在
    /// 未 Running 时是 no-op——不影响探针捕获的圆心坐标本身，那是纯几何计算，不需要真正跑 SimWorld。
    /// 手法同 <see cref="ComposeCastSignalSmokeReport"/>：纯 C#，execute_code 直接调 Run()，不进 Play。
    /// </summary>
    public static class MeleeDirectionSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            var sim = new SimBridge();
            var stats = new StatSheet();
            var abilities = new AbilitySystem();
            var bridge = new MetabolicSliceBridge();
            bridge.Bind(sim, stats, abilities);

            // ① 前方判定生效：朝 +X 挥一次，圆心应在 PlayerPosition(0,0) 前方 MeleeFrontOffset 处，不再恒等于原点。
            abilities.AimDirection = new float2(1f, 0f);
            if (!bridge.ApplyEvent(new HitEvent { Damage = 10f, Scale = 1f, Count = 1f, Shape = "Melee" }))
            {
                return (false, "① Melee 单发 ApplyEvent 返回 false");
            }
            if (bridge.LastMeleeStrikeOrigins.Count != 1)
            {
                return (false, $"① 应产出 1 个命中圆心（实际 {bridge.LastMeleeStrikeOrigins.Count}）");
            }
            float2 originA = bridge.LastMeleeStrikeOrigins[0];
            if (math.lengthsq(originA) < 0.01f)
            {
                return (false, $"① 圆心不应恒等于 PlayerPosition，实际仍在原点 ({originA.x:0.##},{originA.y:0.##})");
            }
            float expectedX = MetabolicSliceBridge.MeleeFrontOffset;
            if (math.abs(originA.x - expectedX) > 0.01f || math.abs(originA.y) > 0.01f)
            {
                return (false, $"① 朝 +X 挥击圆心应为 ({expectedX:0.##},0)，实际 ({originA.x:0.##},{originA.y:0.##})");
            }

            // ② 换方向后圆心应随之变化（不是只读一次缓存）。
            abilities.AimDirection = new float2(0f, 1f);
            if (!bridge.ApplyEvent(new HitEvent { Damage = 10f, Scale = 1f, Count = 1f, Shape = "Melee" }))
            {
                return (false, "② 换方向后 Melee 单发 ApplyEvent 返回 false");
            }
            float2 originB = bridge.LastMeleeStrikeOrigins[0];
            if (math.abs(originB.x - originA.x) < 0.5f && math.abs(originB.y - originA.y) < 0.5f)
            {
                return (false, $"② 圆心应随 AimDirection 变化，两次几乎相同：" +
                    $"({originA.x:0.##},{originA.y:0.##}) vs ({originB.x:0.##},{originB.y:0.##})");
            }

            // ③ 多发（hits=3）应展开在 baseDir 前方 ±ArcHalfAngleDeg（40°）锥形内——这是"非目标不受击"的
            // 几何基础：锥外/正后方的点到任意圆心的距离都应大于半径，不会被本次事件覆盖。
            abilities.AimDirection = new float2(1f, 0f);
            if (!bridge.ApplyEvent(new HitEvent { Damage = 10f, Scale = 1f, Count = 3f, Shape = "Melee" }))
            {
                return (false, "③ Melee 三发 ApplyEvent 返回 false");
            }
            if (bridge.LastMeleeStrikeOrigins.Count != 3)
            {
                return (false, $"③ hits=3 应产出 3 个命中圆心（实际 {bridge.LastMeleeStrikeOrigins.Count}）");
            }
            float radius = bridge.LastMeleeStrikeRadius;
            const float arcHalfAngleDeg = 40f; // FxRecipeCatalog.Global.ArcHalfAngleDeg（跨程序集不便直接引用内容 Catalog，对齐已知值）
            float halfRad = arcHalfAngleDeg * math.PI / 180f;
            for (int i = 0; i < bridge.LastMeleeStrikeOrigins.Count; i++)
            {
                float2 o = bridge.LastMeleeStrikeOrigins[i];
                float2 dirFromPlayer = math.normalizesafe(o, new float2(1f, 0f));
                float angle = math.acos(math.clamp(math.dot(dirFromPlayer, new float2(1f, 0f)), -1f, 1f));
                if (angle > halfRad + 0.01f)
                {
                    return (false, $"③ 第 {i} 个圆心偏离前方锥超出 ArcHalfAngleDeg：{math.degrees(angle):0.#}°");
                }
            }

            float2 behindPoint = new float2(-(MetabolicSliceBridge.MeleeFrontOffset + radius + 2f), 0f);
            for (int i = 0; i < bridge.LastMeleeStrikeOrigins.Count; i++)
            {
                float2 o = bridge.LastMeleeStrikeOrigins[i];
                float dist = math.distance(behindPoint, o);
                if (dist <= radius)
                {
                    return (false, $"③ 正后方目标不应受击，但第 {i} 个圆心距离 {dist:0.##} <= 半径 {radius:0.##}");
                }
            }

            return (true,
                $"①前方判定：({originA.x:0.##},{originA.y:0.##})≠原点；②换向后：({originB.x:0.##},{originB.y:0.##})；"
                + $"③hits=3 全部在 ±{arcHalfAngleDeg:0.#}° 锥内，正后方目标不受击（半径 {radius:0.##}）");
        }
    }
}
