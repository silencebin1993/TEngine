using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 状态施加/移除执行器。
    ///
    /// 内核（SimStatus）只是个位掩码，负责"有没有"；强度、剩余时间、到期移除
    /// 全部应该由热更层的 StatusSystem 管理。StatusSystem 还没做，所以这里的
    /// Duration 只是记录在数据里、目前不会驱动任何计时器——不要以为传了 Duration
    /// 状态就会自动消失。
    /// </summary>
    public sealed class EffectApplyStatus : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Status;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            bool add = spec.Value >= 0f;
            float radius = EffectScaling.Radius(spec, in ctx);
            float duration = EffectScaling.Duration(spec, in ctx);

            // 滞留：范围类效果影响半径更大，近似"留下一片"的感觉
            if (EffectDealDamage.HasAffix(spec, AffixKind.Lingering))
            {
                radius *= 1.5f;
            }

            // 限时状态交给 StatusSystem 登记到期；它不在时退化为永久施加。
            var status = ctx.Hub?.Get<Battle.StatusSystem>();
            bool timed = add && duration > 0f && status != null;

            switch (spec.Shape)
            {
                case EffectShape.Target:
                    if (ctx.TargetIndex >= 0)
                    {
                        if (timed)
                        {
                            status.ApplyTimed(ctx.TargetIndex, spec.Status, duration);
                        }
                        else
                        {
                            ctx.Sim.ApplyStatusUnit(ctx.TargetIndex, spec.Status, add);
                        }
                    }
                    break;

                case EffectShape.Self:
                    // 作用于玩家自己：内核用固定的 PlayerIndex
                    if (timed)
                    {
                        status.ApplyTimed(SimConst.PlayerIndex, spec.Status, duration);
                    }
                    else
                    {
                        ctx.Sim.ApplyStatusUnit(SimConst.PlayerIndex, spec.Status, add);
                    }
                    break;

                case EffectShape.Circle:
                case EffectShape.Cone:
                case EffectShape.Line:
                    if (timed)
                    {
                        status.ApplyTimedArea(ctx.Origin, math.max(0.2f, radius),
                            spec.Status, duration, spec.TargetFaction);
                    }
                    else
                    {
                        ctx.Sim.ApplyStatusArea(ctx.Origin, math.max(0.2f, radius),
                            spec.Status, add, spec.TargetFaction);
                    }
                    break;

                case EffectShape.Point:
                {
                    float2 p = ctx.Origin + ctx.Direction * spec.Radius;
                    if (timed)
                    {
                        status.ApplyTimedArea(p, math.max(0.2f, radius),
                            spec.Status, duration, spec.TargetFaction);
                    }
                    else
                    {
                        ctx.Sim.ApplyStatusArea(p, math.max(0.2f, radius),
                            spec.Status, add, spec.TargetFaction);
                    }
                    break;
                }
            }

        }
    }
}
