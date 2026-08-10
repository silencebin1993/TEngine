using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 战斗反馈表现层模块（story-002）。
    ///
    /// 只订阅 <see cref="Signals"/>，不持有 SimBridge / SimWorld 引用（Preflight A4/K1）——
    /// 换美术时只需把构造参数换成新的 <see cref="ICombatFeedback"/> 实现，
    /// 订阅时机、退订、生命周期完全不变。
    /// </summary>
    public sealed class CombatFeedbackPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly ICombatFeedback _impl;
        private SignalScope _scope;

        public CombatFeedbackPresenter(ICombatFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxCombatFeedback();
        }

        public override void OnEnter()
        {
            _scope = new SignalScope();
            _scope.On<HitSignal>(_impl.OnHit)
                  .On<PlayerHurtSignal>(_impl.OnPlayerHurt)
                  .On<KillSignal>(_impl.OnKill)
                  .On<DevourSignal>(_impl.OnDevour);
        }

        public override void OnUpdate(float dt)
        {
            _impl.Tick(dt);
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
            (_impl as System.IDisposable)?.Dispose();
        }
    }
}
