using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-002：ComposeCastSignal 冒烟验证。<see cref="MetabolicSliceBridge.ApplyEvent"/> 只碰
    /// _sim/_stats/Tags/Payload，不碰 _engine/_runner/_environment，所以不需要 OnEnter()/SimBridge.Begin()——
    /// 纯 C#，execute_code 直接调 Run()，不进 Play。
    /// </summary>
    public static class ComposeCastSignalSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            var sim = new SimBridge();
            var stats = new StatSheet();
            var bridge = new MetabolicSliceBridge();
            bridge.Bind(sim, stats);

            ComposeCastSignal? captured = null;
            void Handler(ComposeCastSignal s) => captured = s;
            Signals.Subscribe<ComposeCastSignal>(Handler);

            try
            {
                // ① 富装配：Damage>0，六个组合字段全部对齐 + Origin/Direction
                var rich = new HitEvent
                {
                    Damage = 10f,
                    Scale = 1.5f,
                    Count = 3f,
                    Spin = 90f,
                    Orbit = 45f,
                    ExplodeOnHit = true,
                    Shape = "Beam",
                };
                rich.Tags.Add("Fire");
                rich.Tags.Add("Wet");

                captured = null;
                if (!bridge.ApplyEvent(rich))
                {
                    return (false, "① Damage 装配 ApplyEvent 返回 false");
                }
                if (!captured.HasValue)
                {
                    return (false, "① Damage 装配未 Publish ComposeCastSignal");
                }
                ComposeCastSignal c1 = captured.Value;
                if (!c1.HasProjectile)
                {
                    return (false, "① HasProjectile 应为 true");
                }
                if (c1.Shape != rich.Shape || c1.Scale != rich.Scale || c1.Count != rich.Count
                    || c1.Spin != rich.Spin || c1.Orbit != rich.Orbit || c1.ExplodeOnHit != rich.ExplodeOnHit)
                {
                    return (false, $"① 字段不一致：Shape={c1.Shape}/{rich.Shape} Scale={c1.Scale}/{rich.Scale} "
                        + $"Count={c1.Count}/{rich.Count} Spin={c1.Spin}/{rich.Spin} Orbit={c1.Orbit}/{rich.Orbit} "
                        + $"Explode={c1.ExplodeOnHit}/{rich.ExplodeOnHit}");
                }
                if (!ReferenceEquals(c1.Tags, rich.Tags))
                {
                    return (false, "① Tags 应为 evt.Tags 的引用而非拷贝（D4）");
                }
                if (!c1.Origin.Equals(sim.PlayerPosition))
                {
                    return (false, "① Origin 应等于 sim.PlayerPosition（D5）");
                }
                if (c1.Direction.x != 0f || c1.Direction.y != 1f)
                {
                    return (false, $"① Direction 应为默认前向 (0,1)，实际 ({c1.Direction.x},{c1.Direction.y})（D6）");
                }

                // ② 纯 Heal：无弹体，HasProjectile=false
                var heal = new HitEvent { Heal = 15f };
                captured = null;
                if (!bridge.ApplyEvent(heal))
                {
                    return (false, "② 纯 Heal ApplyEvent 返回 false");
                }
                if (!captured.HasValue)
                {
                    return (false, "② 纯 Heal 未 Publish ComposeCastSignal");
                }
                if (captured.Value.HasProjectile)
                {
                    return (false, "② 纯 Heal HasProjectile 应为 false（D7）");
                }

                // ③ 空事件：Damage=0 且 Spin/Orbit=0，applied 门槛不应失效（D8/D9）
                var empty = new HitEvent();
                captured = null;
                if (bridge.ApplyEvent(empty))
                {
                    return (false, "③ 空事件 ApplyEvent 不应返回 true");
                }
                if (captured.HasValue)
                {
                    return (false, "③ 空事件不应触发 ComposeCastSignal Publish");
                }

                return (true,
                    $"①Damage装配 Shape={c1.Shape} Scale={c1.Scale:0.#} Count={c1.Count:0.#} Spin={c1.Spin:0.#} "
                    + $"Orbit={c1.Orbit:0.#} Explode={c1.ExplodeOnHit} HasProjectile={c1.HasProjectile} "
                    + $"Origin=({c1.Origin.x:0.#},{c1.Origin.y:0.#}) Direction=({c1.Direction.x:0.#},{c1.Direction.y:0.#})；"
                    + "②纯Heal HasProjectile=false；③空事件不 Publish");
            }
            finally
            {
                Signals.Unsubscribe<ComposeCastSignal>(Handler);
            }
        }
    }
}
