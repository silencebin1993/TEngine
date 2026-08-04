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

            // 滞留：范围类效果影响半径更大，近似"留下一片"的感觉
            if (EffectDealDamage.HasAffix(spec, AffixKind.Lingering))
            {
                radius *= 1.5f;
            }

            switch (spec.Shape)
            {
                case EffectShape.Target:
                    if (ctx.TargetIndex >= 0)
                    {
                        ctx.Sim.ApplyStatusUnit(ctx.TargetIndex, spec.Status, add);
                    }
                    break;

                case EffectShape.Self:
                    // 作用于玩家自己：内核用固定的 PlayerIndex
                    ctx.Sim.ApplyStatusUnit(SimConst.PlayerIndex, spec.Status, add);
                    break;

                case EffectShape.Circle:
                case EffectShape.Cone:
                case EffectShape.Line:
                    ctx.Sim.ApplyStatusArea(ctx.Origin, math.max(0.2f, radius),
                        spec.Status, add, spec.TargetFaction);
                    break;

                case EffectShape.Point:
                    ctx.Sim.ApplyStatusArea(ctx.Origin + ctx.Direction * spec.Radius,
                        math.max(0.2f, radius), spec.Status, add, spec.TargetFaction);
                    break;
            }

            // TODO(StatusSystem): Duration 目前只是数据，没有计时器消费它。
            // StatusSystem 落地后，这里应该改成向它注册一条"到期后反向调用
            // ApplyStatusUnit/ApplyStatusArea(add=false)"的记录，而不是在这里空等。
            _ = EffectScaling.Duration(spec, in ctx);
        }
    }
}
