using GameLogic.Core;
using GameLogic.Stats;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 资源变化执行器。生命之外的资源（营养质/突变质/进化能/污染度/体力）
    /// 都不归 SimBridge 管——内核不知道"营养质"这种玩法概念，所以这里只广播
    /// ResourceChangedSignal，具体的余额存储与上下限夹取交给 ResourceWallet。
    /// </summary>
    public sealed class EffectResource : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Resource;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            float amount = EffectScaling.Value(spec, in ctx);

            // 逆熵：把污染度换成生命，词缀直接改写这次效果作用的资源种类
            ResourceKind resource = spec.Resource;
            if (resource == ResourceKind.Pollution &&
                EffectDealDamage.HasAffix(spec, AffixKind.Entropy))
            {
                resource = ResourceKind.Health;
            }

            switch (resource)
            {
                case ResourceKind.Health:
                    if (ctx.Sim != null && ctx.Sim.Running && ctx.Stats != null)
                    {
                        if (amount >= 0f)
                        {
                            ctx.Sim.HealPlayer(amount, ctx.Stats.Get(StatId.MaxHealth));
                        }
                        else
                        {
                            ctx.Sim.DamagePlayer(-amount);
                        }
                    }
                    break;

                case ResourceKind.Nutrient:
                case ResourceKind.Mutagen:
                case ResourceKind.EvoEnergy:
                case ResourceKind.Pollution:
                case ResourceKind.Stamina:
                    // TODO(ResourceWallet): 这里只广播意图，真正的余额加减、上下限夹取
                    // （比如污染度上限、体力上限）都应该在 ResourceWallet 订阅时完成。
                    // 本执行器不维护任何余额状态，Current 先填 0，等 Wallet 落地后
                    // 再由它把真实余额补进同一个信号或另起一个已结算的信号。
                    Signals.Publish(new ResourceChangedSignal
                    {
                        Kind = resource,
                        Delta = amount,
                        Current = 0f,
                    });
                    break;
            }
        }
    }
}
