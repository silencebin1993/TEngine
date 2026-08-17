using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 局内组合弹道表现可替换边界（story-004）。
    ///
    /// 与 <see cref="IAbilityCastFeedback"/> 并列：那边按 AbilityId 查表选表现模板，
    /// 这边只认 <see cref="ComposeCastSignal"/> 的 Shape/Scale/Count/Spin/Orbit 字段——
    /// 玩家换器官装配后，组合出口的白模弹道必须立刻随之变化，不按 org_* id 分支。
    /// 默认实现 <see cref="WhiteboxComposeProjectileFeedback"/>；未来接精美美术时只需新写
    /// 一个实现并在 CellStageFlow.RegisterModules 处换注入，不改 ComposeCastSignal 契约。
    /// </summary>
    public interface IComposeProjectileFeedback
    {
        /// <summary>组合出口结算完成。实现按 <see cref="ComposeCastSignal.Shape"/> 选几何模板。</summary>
        void OnComposeCast(ComposeCastSignal signal);

        /// <summary>每帧推进（弹道位移/衰减，不读 Sim）。</summary>
        void Tick(float dt);
    }
}
