using System.Collections.Generic;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// story-008：机制可见化冒烟验证（R7①②③）。①②③走纯 C# + 直接程序化创建的 Presenter 池，
    /// 不需要进 Play（手法同 <see cref="MeleeDirectionSmokeReport"/>），但会真的创建 GameObject/Mesh/
    /// Material（<see cref="WhiteboxComposeProjectileFeedback"/> 本就是运行期程序化白模，Editor 下同样可跑），
    /// 因此 finally 里必须 Dispose，避免反复 execute_code 调用时在场景里堆残留对象。
    /// </summary>
    public static class MechanismReadoutSmokeReport
    {
        public static (bool Pass, string Reason) Run()
        {
            // ① Spin/Orbit 挂起命中数：HUD 绑定的 PendingMotionCount 数据源本身随 Count 正确变化。
            var sim = new SimBridge();
            var stats = new StatSheet();
            var bridge = new MetabolicSliceBridge();
            bridge.Bind(sim, stats);

            if (!bridge.ApplyEvent(new HitEvent { Damage = 10f, Scale = 1f, Count = 2f, Spin = 90f, Shape = "Bolt" }))
            {
                return (false, "① Spin 装配 ApplyEvent 返回 false");
            }
            if (bridge.PendingMotionCount != 2)
            {
                return (false, $"① PendingMotionCount 应为 2（Count=2），实际 {bridge.PendingMotionCount}");
            }

            var feedback = new WhiteboxComposeProjectileFeedback();
            try
            {
                // ② Spin/Orbit 残影：spin!=0 的 segment 应激活 TrailRenderer，Tick 后 ActiveTrailCount>0。
                var spinSignal = new ComposeCastSignal
                {
                    Shape = "Wave", Scale = 1f, Count = 2f, Spin = 90f, Orbit = 0f,
                    Tags = new HashSet<string>(), Origin = float2.zero,
                    Direction = new float2(0f, 1f), HasProjectile = true,
                };
                feedback.OnComposeCast(spinSignal);
                feedback.Tick(0.01f);
                if (feedback.ActiveTrailCount != 2)
                {
                    return (false, $"② Spin!=0 应有 2 条残影轨迹在画，实际 ActiveTrailCount={feedback.ActiveTrailCount}");
                }

                // 先把 spinSignal 的两个残影标记 Tick 到过期（life=0.3s），避免它们仍存活干扰下面的回归判定。
                feedback.Tick(0.5f);

                var plainSignal = new ComposeCastSignal
                {
                    Shape = "Bolt", Scale = 1f, Count = 1f, Spin = 0f, Orbit = 0f,
                    Tags = new HashSet<string>(), Origin = float2.zero,
                    Direction = new float2(0f, 1f), HasProjectile = true,
                };
                feedback.OnComposeCast(plainSignal);
                feedback.Tick(0.01f);
                if (feedback.ActiveTrailCount != 0)
                {
                    return (false, $"②回归 Spin=Orbit=0 不应画残影，实际 ActiveTrailCount={feedback.ActiveTrailCount}");
                }

                // ③ 非元素 Tag 染色：Catalyst 不在 ElementPriorityOrder，之前会退化成 Shape 底色（与无 Tag 无区别）。
                feedback.OnComposeCast(plainSignal);
                Color taglessColor = feedback.LastCastColor;

                var catalystSignal = plainSignal;
                catalystSignal.Tags = new HashSet<string> { "Catalyst" };
                feedback.OnComposeCast(catalystSignal);
                if (feedback.LastResolvedNonElementTag != "Catalyst")
                {
                    return (false, $"③ 应识别出非元素 Tag Catalyst，实际 '{feedback.LastResolvedNonElementTag}'");
                }
                if (feedback.LastCastColor == Color.white)
                {
                    return (false, "③ 非元素 Tag 不应命中纯白");
                }
                if (feedback.LastCastColor == taglessColor)
                {
                    return (false, "③ 非元素 Tag 弹道应与无 Tag 弹道颜色可区分");
                }

                var fireSignal = plainSignal;
                fireSignal.Tags = new HashSet<string> { "Fire" };
                feedback.OnComposeCast(fireSignal);
                if (feedback.LastResolvedElementTag != "Fire" || feedback.LastCastColor != FxRecipeCatalog.GetElementColor("Fire"))
                {
                    return (false, "③ 元素 Tag Fire 应仍走 GetElementColor（不应被非元素兜底覆盖）");
                }

                // ④ Scale 可辨：半径应随 Scale 线性放大（承接 005，验证仍然成立）。
                var smallSignal = plainSignal;
                smallSignal.Scale = 1f;
                feedback.OnComposeCast(smallSignal);
                float smallRadius = feedback.LastComputedRadius;

                var bigSignal = plainSignal;
                bigSignal.Scale = 3f;
                feedback.OnComposeCast(bigSignal);
                float bigRadius = feedback.LastComputedRadius;
                if (bigRadius <= smallRadius * 1.5f)
                {
                    return (false, $"④ Scale=3 半径应明显大于 Scale=1（{bigRadius:0.##} vs {smallRadius:0.##}）");
                }

                return (true,
                    $"①PendingMotionCount=2；②Spin 残影 ActiveTrailCount=2，Spin=0 回归 0；"
                    + $"③Catalyst 非白且与无 Tag 颜色不同，Fire 仍走元素表；"
                    + $"④Scale 半径 1x={smallRadius:0.##} 3x={bigRadius:0.##}");
            }
            finally
            {
                feedback.Dispose();
            }
        }
    }
}
