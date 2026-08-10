using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 技能施放表现层模块（story-010）。
    ///
    /// 只订阅 <see cref="Signals"/>，不持有 SimBridge / SimWorld 引用——
    /// 与 <see cref="CombatFeedbackPresenter"/> 同一骨架，职责并列而非合并：
    /// 那边管命中结算反馈，这边管施放瞬间本身。换美术时只需把构造参数换成
    /// 新的 <see cref="IAbilityCastFeedback"/> 实现，订阅时机、退订、生命周期
    /// 完全不变。
    /// </summary>
    public sealed class AbilityCastPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly IAbilityCastFeedback _impl;
        private SignalScope _scope;

        public AbilityCastPresenter(IAbilityCastFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxAbilityCastFeedback();
        }

        public override void OnEnter()
        {
            _scope = new SignalScope();
            _scope.On<AbilityCastSignal>(_impl.OnAbilityCast);
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
