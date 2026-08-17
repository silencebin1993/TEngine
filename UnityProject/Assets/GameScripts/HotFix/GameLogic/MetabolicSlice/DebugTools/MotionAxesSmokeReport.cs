using System.Reflection;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-003：Spin/Orbit 延迟命中 wiring 冒烟验证（Preflight D11 第②层）。
    /// <see cref="MetabolicSliceBridge.TickPendingMotion"/> 是私有方法（D6 只要求内部推进，不必对外开放
    /// API），这里用反射手工推进，验证"生成即挂起、tick 完即清空"闭环，不需要 Play/真实 SimWorld。
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

            // ① Damage>0 + Spin!=0 应挂起延迟命中，不立即结算
            var evt = new HitEvent { Damage = 10f, Spin = 90f, Orbit = 0f };
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

            // ③ Damage>0 但 Spin==0 && Orbit==0 应保持原瞬时路径，不挂起
            var instant = new HitEvent { Damage = 10f, Spin = 0f, Orbit = 0f };
            if (!bridge.ApplyEvent(instant))
            {
                return (false, "③ ApplyEvent 返回 false");
            }
            if (bridge.PendingMotionCount != 0)
            {
                return (false, $"③ Spin=Orbit=0 时不应挂起，PendingMotionCount={bridge.PendingMotionCount}");
            }

            return (true,
                $"①挂起 PendingMotionCount={afterApply}；②累计 dt={accumulated:0.##}s 后清空 PendingMotionCount={afterTick}；"
                + "③Spin=Orbit=0 保持瞬时路径不挂起");
        }
    }
}
