using BinGames.Sim;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 血条表现层可替换边界（story-008：白模条）。
    ///
    /// 默认实现 <see cref="WhiteboxHealthBar"/>；未来换正式 UI 血条时只需新写一个实现
    /// 并在 CellStageFlow.RegisterModules 处换注入，不改 <see cref="HealthBarPresenter"/> 的接线与生命周期。
    /// </summary>
    public interface IHealthBarFeedback
    {
        /// <summary>
        /// 每帧把当前快照同步到表现层（玩家 + 精英/首领常显 + 受击普通敌人短显）。
        /// 不写 Sim，纯只读展示。
        /// </summary>
        void Sync(in SimSnapshot snap, float playerMaxHealth);
    }
}
