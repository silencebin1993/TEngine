using System.Collections.Generic;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 区域可视化表现层可替换边界（story-007：白模圆盘）。
    ///
    /// 默认实现 <see cref="WhiteboxZoneVisual"/>；未来换贴花/粒子时只需新写一个实现
    /// 并在 CellStageFlow.RegisterModules 处换注入，不改 <see cref="AreaZoneSystem"/>，
    /// 也不改 <see cref="ZoneVisualPresenter"/> 的接线与生命周期。
    /// </summary>
    public interface IZoneVisualFeedback
    {
        /// <summary>
        /// 每帧把当前区域列表同步到表现层（位置/半径/颜色/可见性）。
        /// 不读 Sim、不写区域数据，纯只读展示。
        /// </summary>
        void Sync(IReadOnlyList<AreaZoneSystem.Zone> zones);
    }
}
