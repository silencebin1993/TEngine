using GameLogic.Progression;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 规则开关执行器。改的不是数值，是玩法规则本身（尸体可食、冲刺穿透……），
    /// 这类卡的效果不在 SimBridge 也不在 StatSheet 里，落点是 RuleFlags。
    ///
    /// RuleFlags 还不是 IGameModule（见 RuleFlags.cs 里的权衡说明），
    /// 所以这里没有走 ctx.Hub.Get&lt;T&gt;()，而是直接用它的静态单例
    /// RuleFlags.Current。等它真的挂进 ModuleHub 那天，这里只需要改
    /// 这一行取值方式，Execute 主体逻辑不用动。
    /// </summary>
    public sealed class EffectRule : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Rule;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (spec.Rule == RuleFlag.None)
            {
                return;
            }

            RuleFlags flags = RuleFlags.Current;

            // Value < 0 表示这条规则卡是"移除"语义（例如某些进化选择的对立分支）
            if (spec.Value < 0f)
            {
                flags.Clear(spec.Rule);
            }
            else
            {
                flags.Set(spec.Rule);
            }
        }
    }
}
