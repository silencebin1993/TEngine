using System.Reflection;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// Spin/Orbit（鞭毛绕/涡旋）**三**语义冒烟验证。
    ///
    /// combat-primitive-overhaul 先把它拆成了两种读法（弹道蛇行 / 环绕采样）。
    /// chassis-native-primitives 再拆出第三种：**近战 = 旋风横扫**。
    /// 原因是"非弹道一律环绕采样"在近战上等于给近战装个基因就把近战本身取消了——
    /// 扇形判定整个消失，只剩玩家身边几个转圈的判定点，玩家读不出自己还在挥刀。
    ///
    ///   · 弹道底盘 → 弹体自己蛇行着飞（内核 WeaveRate/WeaveAmp），不进 _pendingMotion；
    ///   · 近战底盘 → 扇形在挥击窗口里旋转（_pendingSwing + LastMeleeSweepRateDeg）；
    ///   · 其余非弹道底盘（场地/光环/召唤）→ 保持环绕采样（绕着你转）。
    ///
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

            // ① 非弹道**非近战**底盘 + Spin!=0 应挂起环绕采样（"绕着你转"），不立即结算。
            //    用 Pool（场地）而不是 Melee——近战现在走第三条路（见 ⑤）。
            var evt = new HitEvent
            {
                Damage = 10f, Spin = 90f, Orbit = 0f, Shape = "Field",
                AttackPattern = AttackPattern.Pool,
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
            if (bridge.PendingMotionCount != 0 || bridge.PendingSwingCount != 0)
            {
                return (false, $"③ Spin=Orbit=0 的单刀应逐字退化成瞬时挥击，"
                    + $"实际 PendingMotionCount={bridge.PendingMotionCount} PendingSwingCount={bridge.PendingSwingCount}");
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

            // ⑤ chassis-native-primitives：近战底盘的 Spin = **旋风**。
            //    必须挂出挥击窗口（扇形要转起来），且**不得**掉进环绕采样——
            //    掉进去就意味着扇形判定被丢掉了，那正是本次要修的病。
            var melee = new MetabolicSliceBridge();
            melee.Bind(new SimBridge(), new StatSheet());
            var sweep = new HitEvent
            {
                Damage = 10f, Spin = 120f, Orbit = 0f, Scale = 1f, Count = 1f,
                SpreadAngle = 40f, Shape = "Melee", AttackPattern = AttackPattern.Melee,
            };
            if (!melee.ApplyEvent(sweep))
            {
                return (false, "⑤ 近战底盘 ApplyEvent 返回 false");
            }
            if (melee.PendingMotionCount != 0)
            {
                return (false, $"⑤ 近战底盘的 Spin 不应走环绕采样（那会丢掉扇形），"
                    + $"实际 PendingMotionCount={melee.PendingMotionCount}");
            }
            if (melee.PendingSwingCount <= 0)
            {
                return (false, $"⑤ 近战底盘的 Spin 应挂出旋风挥击窗口，实际 PendingSwingCount={melee.PendingSwingCount}");
            }
            if (Unity.Mathematics.math.abs(melee.LastMeleeSweepRateDeg - 120f) > 0.01f)
            {
                return (false, $"⑤ 旋风角速度应等于 Spin=120°/s，实际 {melee.LastMeleeSweepRateDeg:0.##}");
            }
            if (Unity.Mathematics.math.abs(melee.LastMeleeConeHalfAngleDeg - 20f) > 0.01f)
            {
                return (false, $"⑤ 旋风期间扇形仍应存在（半角 20°），实际 {melee.LastMeleeConeHalfAngleDeg:0.##}");
            }

            return (true,
                $"①场地底盘挂起 PendingMotionCount={afterApply}；②累计 dt={accumulated:0.##}s 后清空；"
                + "③Spin=Orbit=0 的单刀保持瞬时路径不挂起；"
                + $"④弹道底盘改走弹体蛇行 rate={weaveReq.WeaveRateDeg:0.#}°/s amp={weaveReq.WeaveAmp:0.##}；"
                + $"⑤近战底盘改走旋风 sweep={melee.LastMeleeSweepRateDeg:0.#}°/s 扇形半角仍为 {melee.LastMeleeConeHalfAngleDeg:0.#}°（未掉进环绕采样）");
        }
    }
}
