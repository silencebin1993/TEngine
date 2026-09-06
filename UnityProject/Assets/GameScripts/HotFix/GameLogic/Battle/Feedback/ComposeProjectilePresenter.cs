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

        /// <summary>combat-primitive-presentation story-004：近战前冲方向+强度，供 CellStageFlow 每帧
        /// 转发给 SimRenderer.SetPlayerLunge。同 <see cref="LoadArtBindingsAsync"/> 先例——非白模实现
        /// 类型检查后走空操作（方向 zero/强度 0，SimRenderer 端天然不产生位移）。</summary>
        public (Unity.Mathematics.float2 Direction, float Progress) GetMeleeLunge()
        {
            if (_impl is WhiteboxComposeProjectileFeedback wb)
            {
                return (wb.MeleeLungeDirection, wb.MeleeLungeProgress);
            }
            return (Unity.Mathematics.float2.zero, 0f);
        }

        public override void OnEnter()
        {
            _scope = new SignalScope();
            _scope.On<ComposeCastSignal>(_impl.OnComposeCast);
            _scope.On<StructuralHookFiredSignal>(_impl.OnStructuralHookFired);
            _scope.On<ComposeChainSignal>(_impl.OnComposeChain);
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
