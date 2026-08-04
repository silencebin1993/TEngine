using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability
{
    /// <summary>
    /// 效果执行上下文。执行器需要的一切都在这里，避免执行器自己去 Hub 里翻。
    /// </summary>
    public struct EffectContext
    {
        public ModuleHub Hub;
        public SimBridge Sim;
        public StatSheet Stats;

        /// <summary>效果原点。通常是玩家位置。</summary>
        public float2 Origin;
        /// <summary>朝向。锥形/直线/位移用。</summary>
        public float2 Direction;
        /// <summary>指定目标索引。Shape=Target 时有效，否则 -1。</summary>
        public int TargetIndex;
        /// <summary>来源 id：卡牌 id 或技能 id，用于属性修正的按源移除。</summary>
        public int SourceId;
        /// <summary>叠加层数。同一张卡叠 3 层时效果按层缩放。</summary>
        public int Stack;
        /// <summary>触发本次效果的信号携带的强度（如吞噬体积、连吃层数）。</summary>
        public float TriggerMagnitude;
    }

    /// <summary>
    /// 效果执行器。**新增一种效果 = 新增一个本接口的实现 + 在 AbilitySystem 注册一行。**
    /// 不需要修改任何现有执行器或调度代码。
    ///
    /// 详见 DesignDocs/Game_Framework_Design.md §5.4、§7。
    /// </summary>
    public interface IEffectExecutor
    {
        EffectKind Kind { get; }

        void Execute(EffectSpec spec, in EffectContext ctx);
    }

    /// <summary>
    /// 效果数值缩放的共用规则。集中在一处，避免每个执行器各写一套。
    /// </summary>
    public static class EffectScaling
    {
        /// <summary>效果主数值。层数线性叠加，再乘技能强度。</summary>
        public static float Value(EffectSpec spec, in EffectContext ctx)
        {
            float v = spec.Value * math.max(1, ctx.Stack);
            if (spec.ScaleWithPower && ctx.Stats != null)
            {
                v *= ctx.Stats.Get(StatId.AbilityPower);
            }
            return v;
        }

        /// <summary>作用半径。受 AreaScale 影响。</summary>
        public static float Radius(EffectSpec spec, in EffectContext ctx)
        {
            float r = spec.Radius;
            if (spec.ScaleWithPower && ctx.Stats != null)
            {
                r *= ctx.Stats.Get(StatId.AreaScale);
            }
            return r;
        }

        /// <summary>持续时间。受 StatusDuration 影响。</summary>
        public static float Duration(EffectSpec spec, in EffectContext ctx)
        {
            float d = spec.Duration;
            if (ctx.Stats != null)
            {
                d *= ctx.Stats.Get(StatId.StatusDuration);
            }
            return d;
        }

        /// <summary>电系伤害额外倍率。</summary>
        public static float ElectricMul(in EffectContext ctx)
        {
            return ctx.Stats?.Get(StatId.ElectricPower) ?? 1f;
        }
    }
}
