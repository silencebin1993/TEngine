using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 属性修正执行器。往 StatSheet 里加一条 StatModifier，具体加成层级
    /// （Flat/PctAdd/PctMul）由配表通过 spec.Op 决定，本执行器不做取舍。
    /// </summary>
    public sealed class EffectModifyStat : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Stat;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Stats == null || spec.Stat == StatId.None)
            {
                return;
            }

            // 层数线性叠加：叠 3 层的卡就是 3 倍效果，和 EffectScaling.Value 保持一致口径
            float value = spec.Value * math.max(1, ctx.Stack);

            ctx.Stats.Add(new StatModifier(spec.Stat, spec.Op, value, ctx.SourceId));

            // TODO(StatusSystem): Duration > 0 表示这是一条限时修正，理应在到期时
            // 调用 StatSheet.RemoveBySource(ctx.SourceId) 撤销。StatusSystem 落地前
            // 只能靠调用方自己记得手动移除，这里先不假装有计时器。
            if (spec.Duration > 0f)
            {
                _ = EffectScaling.Duration(spec, in ctx);
            }
        }
    }
}
