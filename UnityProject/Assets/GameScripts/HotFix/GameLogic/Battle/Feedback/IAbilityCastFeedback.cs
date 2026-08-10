using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 技能施放表现可替换边界（story-010）。
    ///
    /// 与 <see cref="ICombatFeedback"/> 并列而非合并：那边管命中结算反馈
    /// （打中/受伤/击杀/吞噬），这边管施放瞬间本身（弹道/范围指示/近战发动/
    /// 位移/自身增益）。默认实现 <see cref="WhiteboxAbilityCastFeedback"/>；
    /// 未来接入精美美术时只需新写一个实现并在 CellStageFlow.RegisterModules
    /// 处换注入，不改 AbilityCastSignal 契约，也不改 AbilityCastPresenter
    /// 的订阅与生命周期。
    /// </summary>
    public interface IAbilityCastFeedback
    {
        /// <summary>玩家施放主动技能。实现按 <see cref="AbilityCastSignal.AbilityId"/>
        /// 查询技能效果表，自行决定表现模板。</summary>
        void OnAbilityCast(AbilityCastSignal signal);

        /// <summary>每帧推进（表现计时衰减等，不读 Sim）。</summary>
        void Tick(float dt);
    }
}
