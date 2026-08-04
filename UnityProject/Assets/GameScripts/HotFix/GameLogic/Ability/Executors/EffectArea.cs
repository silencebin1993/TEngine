using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 持续区域执行器：酸雾、菌毯、导电区一类"放一片东西在地上持续生效"的效果。
    ///
    /// TODO(AreaZoneSystem): 这是本类里唯一"没有诚实实现"的地方，写清楚原因：
    /// 持续区域需要一个区域注册表——记录位置/半径/剩余时间，并在每帧或每 tick
    /// 重新对范围内单位结算一次。这套 tick 驱动的注册表现在完全不存在，
    /// SimBridge 也没有"注册一个会自己存在 N 秒的区域"这种命令。
    /// 在它落地之前，这里退化成"在放置的瞬间打一次伤害+状态"，不会持续，
    /// Duration 完全不生效。宁可效果偏弱，也不要在没有计时器的情况下假装
    /// 区域会自己继续伤害——那样调用方会以为 tick 在跑，实际什么都没发生。
    /// </summary>
    public sealed class EffectArea : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Area;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            float amount = EffectScaling.Value(spec, in ctx);
            float radius = EffectScaling.Radius(spec, in ctx);
            float2 origin = spec.Shape == EffectShape.Point
                ? ctx.Origin + ctx.Direction * spec.Radius
                : ctx.Origin;

            ctx.Sim.DamageArea(origin, math.max(0.2f, radius), amount,
                spec.TargetFaction, spec.Status, spec.RequireStatus,
                0, math.max(2f, radius), 0.75f, ctx.SourceId);

            if (spec.Status != BinGames.Sim.SimStatus.None)
            {
                ctx.Sim.ApplyStatusArea(origin, math.max(0.2f, radius),
                    spec.Status, true, spec.TargetFaction);
            }

            // Duration 只在此处留痕，不驱动任何后续 tick——见类注释。
            _ = EffectScaling.Duration(spec, in ctx);
        }
    }
}
