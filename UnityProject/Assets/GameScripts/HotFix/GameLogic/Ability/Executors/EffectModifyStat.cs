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

            // 限时修正必须用**独立的来源 id**，不能直接用 ctx.SourceId。
            //
            // 原因：CardTriggerBus.ApplyPassive 用卡牌 id 作为永久 StatMods 的
            // SourceId。若限时修正也用同一个 id，到期时 RemoveBySource(卡牌id)
            // 会把这张卡的永久属性加成一起撤销——一张既有永久加成又有限时 buff
            // 的卡，触发一次反而变弱。用负数区间隔开两者。
            bool timed = spec.Duration > 0f;
            int sourceId = timed ? TimedSourceId(ctx.SourceId) : ctx.SourceId;

            ctx.Stats.Add(new StatModifier(spec.Stat, spec.Op, value, sourceId));

            // 限时修正交给 StatusSystem 到期撤销。它缺失时修正会永久留着——
            // 这会让"吞噬回响"这类临时 buff 变成永久强化，所以显式告警而不静默。
            if (timed)
            {
                float duration = EffectScaling.Duration(spec, in ctx);
                var status = ctx.Hub?.Get<Battle.StatusSystem>();
                if (status != null)
                {
                    status.RegisterTimedStat(ctx.Stats, sourceId, duration);
                }
                else
                {
                    TEngine.Log.Warning(
                        $"[EffectModifyStat] 来源 {ctx.SourceId} 的限时属性修正无 StatusSystem 撤销，将永久生效。");
                }
            }
        }

        /// <summary>
        /// 把来源 id 映射到限时修正专用的负数区间，与永久修正隔开。
        /// 用减法而不是取负，避免 id 为 0（无来源）时两者仍然撞在一起。
        /// </summary>
        private static int TimedSourceId(int sourceId)
        {
            return -1000000 - sourceId;
        }
    }
}
