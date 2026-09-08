using System.Reflection;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// Spin/Orbit（鞭毛绕/涡旋）双语义冒烟验证。
    ///
    /// combat-primitive-overhaul：这一对字段在不同底盘上是**两件事**，文案「攻击绕圈飞/绕着你转」
    /// 本来就写了两种读法，旧实现却不分底盘一律走"绕着你转"的环绕采样——于是给 org_emitter 装
    /// gene_flagella 时弹道整个消失，只剩玩家身边几个转圈的判定点。现在：
    ///   · 弹道底盘 → 弹体自己蛇行着飞（内核 WeaveRate/WeaveAmp），不进 _pendingMotion；
    ///   · 非弹道底盘 → 保持原来的环绕采样（绕着你转）。
    /// TickPendingMotion 是私有方法，用反射手工推进，不需要 Play/真实 SimWorld。
    /// </summary>
    public static class MotionAxesSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            var sim = new SimBridge();
            var stats = new StatSheet();
            var bridge = new MetabolicSliceBridge();
            bridge.Bind(sim, stats);

            MethodInfo tick = typeof(MetabolicSliceBridge).GetMethod(
                "TickPendingMotion", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tick == null)
            {
                return (false, "反射找不到 TickPendingMotion（签名/名称是否被改动？）");
            }

            // ① 非弹道底盘 + Spin!=0 应挂起环绕采样（"绕着你转"），不立即结算
            var evt = new HitEvent
            {
                Damage = 10f, Spin = 90f, Orbit = 0f, Shape = "Melee",
                AttackPattern = AttackPattern.Melee,
            };
            if (!bridge.ApplyEvent(evt))
            {
                return (false, "① ApplyEvent 返回 false");
            }
            int afterApply = bridge.PendingMotionCount;
            if (afterApply <= 0)
            {
                return (false, $"① ApplyEvent 后 PendingMotionCount 应 >0，实际 {afterApply}");
            }

            // ② 累计 dt 到 MotionFlightDuration(0.3f) 以上，条目应到期清空
            const float step = 0.05f;
            float accumulated = 0f;
            int guard = 0;
            while (bridge.PendingMotionCount > 0 && guard < 20)
            {
                tick.Invoke(bridge, new object[] { step });
                accumulated += step;
                guard++;
            }
            int afterTick = bridge.PendingMotionCount;
            if (afterTick != 0)
            {
                return (false, $"② 累计 dt={accumulated:0.##}s 后 PendingMotionCount 应为 0，实际 {afterTick}");
            }
            if (accumulated < ComposeMotionMath.MotionFlightDuration)
            {
                return (false, $"② 累计 dt={accumulated:0.##}s 未达 MotionFlightDuration={ComposeMotionMath.MotionFlightDuration}，逻辑有误");
            }

            // ③ Spin==0 && Orbit==0 应保持原瞬时路径，不挂起
            var instant = new HitEvent
            {
                Damage = 10f, Spin = 0f, Orbit = 0f, Shape = "Melee",
                AttackPattern = AttackPattern.Melee,
            };
            if (!bridge.ApplyEvent(instant))
            {
                return (false, "③ ApplyEvent 返回 false");
            }
            if (bridge.PendingMotionCount != 0)
            {
                return (false, $"③ Spin=Orbit=0 时不应挂起，PendingMotionCount={bridge.PendingMotionCount}");
            }

            // ④ 弹道底盘上的同一套 Spin/Orbit 走另一条路：翻译成内核弹体的蛇行参数，不进环绕采样。
            var ballistic = new HitEvent
            {
                Damage = 10f, Spin = 120f, Orbit = 1.5f, Speed = 1.3f, Scale = 1f, Count = 1f,
                Shape = "Bolt", AttackPattern = AttackPattern.Projectile,
            };
            if (!bridge.ApplyEvent(ballistic))
            {
                return (false, "④ 弹道底盘 ApplyEvent 返回 false");
            }
            if (bridge.PendingMotionCount != 0)
            {
                return (false, $"④ 弹道底盘不应走环绕采样，实际 PendingMotionCount={bridge.PendingMotionCount}");
            }
            BinGames.Sim.ProjectileRequest weaveReq = CombatBallistics.Build(
                ballistic, default(Unity.Mathematics.float2), new Unity.Mathematics.float2(1f, 0f),
                0, 1, 1f, 1, 0u);
            // 切向速度 = 绕轨半径 × 角速度，1.5 × 120°/s ≈ 3.14 u/s。
            float expectedAmp = 1.5f * Unity.Mathematics.math.radians(120f);
            if (Unity.Mathematics.math.abs(weaveReq.WeaveAmp - expectedAmp) > 0.01f
                || Unity.Mathematics.math.abs(weaveReq.WeaveRateDeg - 120f) > 0.01f)
            {
                return (false, $"④ 蛇行参数应为 rate=120°/s amp={expectedAmp:0.00}，"
                    + $"实际 rate={weaveReq.WeaveRateDeg:0.##} amp={weaveReq.WeaveAmp:0.##}");
            }

            return (true,
                $"①非弹道底盘挂起 PendingMotionCount={afterApply}；②累计 dt={accumulated:0.##}s 后清空；"
                + "③Spin=Orbit=0 保持瞬时路径不挂起；"
                + $"④弹道底盘改走弹体蛇行 rate={weaveReq.WeaveRateDeg:0.#}°/s amp={weaveReq.WeaveAmp:0.##}");
        }
    }
}
