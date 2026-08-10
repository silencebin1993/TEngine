using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 战斗反馈可替换边界（story-002：手感基线）。
    ///
    /// 默认实现 <see cref="WhiteboxCombatFeedback"/>；未来接入精美美术时只需新写一个实现
    /// （如 VfxCombatFeedback）并在 CellStageFlow.RegisterModules 处换注入，
    /// 不改 HitEvent / 信号契约，也不改 CombatFeedbackPresenter 的订阅与生命周期。
    /// </summary>
    public interface ICombatFeedback
    {
        /// <summary>
        /// 玩家造成命中。默认白模会在命中点打一笔闪光；形变压缩仍由 SimRenderer._Impact 负责。
        /// </summary>
        void OnHit(HitSignal signal);

        /// <summary>玩家受伤：需要与"打中敌人"有明显区分度的独立反馈。</summary>
        void OnPlayerHurt(PlayerHurtSignal signal);

        /// <summary>击杀（非吞噬致死，如放电、投射物、毒区）。</summary>
        void OnKill(KillSignal signal);

        /// <summary>吞噬完成。</summary>
        void OnDevour(DevourSignal signal);

        /// <summary>每帧推进（闪光衰减等纯表现计时，不读 Sim）。</summary>
        void Tick(float dt);
    }
}
