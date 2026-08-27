using Cysharp.Threading.Tasks;
using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 组合弹道表现层模块（story-004）。
    ///
    /// 只订阅 <see cref="ComposeCastSignal"/>，不持有 SimBridge / MetabolicSliceBridge 引用——
    /// 与 <see cref="AbilityCastPresenter"/> 同一骨架。换美术时只需把构造参数换成新的
    /// <see cref="IComposeProjectileFeedback"/> 实现，订阅时机、退订、生命周期完全不变。
    /// </summary>
    public sealed class ComposeProjectilePresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly IComposeProjectileFeedback _impl;
        private SignalScope _scope;

        public ComposeProjectilePresenter(IComposeProjectileFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxComposeProjectileFeedback();
        }

        /// <summary>story-006：加载 VFX Prefab 池绑定。<see cref="IComposeProjectileFeedback"/> 接口不改——
        /// 只有 <see cref="WhiteboxComposeProjectileFeedback"/> 才有池，非白模实现类型检查桥接后走空操作。</summary>
        public UniTask LoadArtBindingsAsync() =>
            (_impl as WhiteboxComposeProjectileFeedback)?.LoadVfxBindingsAsync() ?? UniTask.CompletedTask;

        public override void OnEnter()
        {
            _scope = new SignalScope();
            _scope.On<ComposeCastSignal>(_impl.OnComposeCast);
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
