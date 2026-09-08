using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// 近战方向性冒烟验证。纯 C#，execute_code 直接调 Run()，不进 Play——
    /// 扇形几何只读 _sim.PlayerPosition（未 Begin 时恒为 float2.zero）与 _abilities.AimDirection，
    /// DamageCone 在未 Running 时是 no-op，不影响探针捕获的扇形参数本身。
    ///
    /// combat-primitive-overhaul：断言从"N 个命中圆的圆心"改为"一个扇形的扇心 + 触及距离 + 半角"。
    /// 旧实现把一次挥击拆成 hits 个半径 4 的圆摆在身前 2 米处——圆比前移量大一倍，
    /// 背后的敌人照样在圆内挨打，而且圆与圆之间还有几何空洞。现在是内核扇形判定
    /// （<see cref="SimBridge.DamageCone"/>），"背后打不到"是判据本身保证的，不再靠圆心距离碰运气。
    ///
    /// 底盘分类只读 <see cref="HitEvent.AttackPattern"/>，故手搭的合成事件必须给 AttackPattern.Melee，
    /// 否则默认值 Projectile 会被判成弹道底盘。
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

            static HitEvent Melee(float count, float spread) => new HitEvent
            {
                Damage = 10f, Scale = 1f, Count = count, Shape = "Melee",
                SpreadAngle = spread, AttackPattern = AttackPattern.Melee,
            };

            // ① 扇心在瞄准方向前方，不再恒等于玩家坐标。
            abilities.AimDirection = new float2(1f, 0f);
            if (!bridge.ApplyEvent(Melee(1f, 0f)))
            {
                return (false, "① Melee 单发 ApplyEvent 返回 false");
            }
            if (bridge.LastMeleeStrikeOrigins.Count != 1)
            {
                return (false, $"① 一次挥击应恰好一个扇形（实际 {bridge.LastMeleeStrikeOrigins.Count}）");
            }
            float2 originA = bridge.LastMeleeStrikeOrigins[0];
            float expectedX = MetabolicSliceBridge.MeleeFrontOffset;
            if (math.abs(originA.x - expectedX) > 0.01f || math.abs(originA.y) > 0.01f)
            {
                return (false, $"① 朝 +X 挥击扇心应为 ({expectedX:0.##},0)，实际 ({originA.x:0.##},{originA.y:0.##})");
            }

            // ② 换方向后扇心随之变化（不是只读一次缓存）。
            abilities.AimDirection = new float2(0f, 1f);
            if (!bridge.ApplyEvent(Melee(1f, 0f)))
            {
                return (false, "② 换方向后 ApplyEvent 返回 false");
            }
            float2 originB = bridge.LastMeleeStrikeOrigins[0];
            if (math.abs(originB.y - MetabolicSliceBridge.MeleeFrontOffset) > 0.01f || math.abs(originB.x) > 0.01f)
            {
                return (false, $"② 朝 +Y 挥击扇心应为 (0,{expectedX:0.##})，实际 ({originB.x:0.##},{originB.y:0.##})");
            }

            // ③ 触及距离是真实射程量级（不是旧的 DamageAreaRadius=4 那个"命中圆半径"）。
            float reach = bridge.LastMeleeStrikeRadius;
            if (math.abs(reach - CombatBallistics.MeleeReach) > 0.01f)
            {
                return (false, $"③ 触及距离应为 {CombatBallistics.MeleeReach:0.##}，实际 {reach:0.##}");
            }

            // ④ 扇角由器官自己的 SpreadAngle 决定——这是"不同近战器官打击范围不同"的唯一来源。
            //    org_cilia(40)→±20 精准刺；org_pseudopod(70)→±35 挥砍；org_wave(180)→±90 半圆横扫。
            abilities.AimDirection = new float2(1f, 0f);
            bridge.ApplyEvent(Melee(1f, 40f));
            float halfCilia = bridge.LastMeleeConeHalfAngleDeg;
            bridge.ApplyEvent(Melee(1f, 180f));
            float halfWave = bridge.LastMeleeConeHalfAngleDeg;
            if (math.abs(halfCilia - 20f) > 0.01f)
            {
                return (false, $"④ SpreadAngle=40 应得半角 20°，实际 {halfCilia:0.#}°");
            }
            if (math.abs(halfWave - 90f) > 0.01f)
            {
                return (false, $"④ SpreadAngle=180 应得半角 90°，实际 {halfWave:0.#}°");
            }
            if (halfWave <= halfCilia)
            {
                return (false, "④ 宽扇器官的半角必须大于窄扇器官，实际未拉开差异");
            }

            // ⑤ 正后方不受击：这是扇形判据本身的性质。用与 JobDamage.InCone 同一套数学复算
            //    （dot(单位方向, 中轴) >= cos(半角)，且距离超出贴身豁免半径）。
            bridge.ApplyEvent(Melee(1f, 40f));
            float half = bridge.LastMeleeConeHalfAngleDeg;
            float2 coneDir = new float2(1f, 0f);
            float2 center = bridge.LastMeleeStrikeOrigins[0];
            float2 behind = center - coneDir * (CombatBallistics.MeleeNearRadius + 1.5f);
            float2 d = behind - center;
            float dist = math.length(d);
            bool nearExempt = dist <= CombatBallistics.MeleeNearRadius;
            bool inCone = math.dot(d / math.max(dist, 1e-5f), coneDir) >= math.cos(math.radians(half));
            if (nearExempt || inCone)
            {
                return (false, $"⑤ 正后方 {dist:0.##} 处的目标不应落在扇内（nearExempt={nearExempt} inCone={inCone}）");
            }

            return (true,
                $"①扇心 ({originA.x:0.##},{originA.y:0.##}) 在前方；②换向后 ({originB.x:0.##},{originB.y:0.##})；" +
                $"③触及 {reach:0.##}；④半角 40°→{halfCilia:0.#}° / 180°→{halfWave:0.#}°；⑤正后方在扇外");
        }
    }
}
