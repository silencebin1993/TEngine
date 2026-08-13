using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 区域可视化表现层模块（story-007）。
    ///
    /// 与 <see cref="CombatFeedbackPresenter"/>/<see cref="AbilityCastPresenter"/> 同款
    /// "接口 + 默认白模实现 + 薄壳 Module"三段式，但主动偏离两者"只订阅 Signals、
    /// 不持有系统引用"的惯例：<see cref="AreaZoneSystem"/> 没有区域生成/过期信号，
    /// 只有逐帧维护的 Zones 列表，且 FollowPlayer 光环需要逐帧位置同步，
    /// 所以本模块直接 <see cref="Bind"/> 持有引用、每帧轮询。
    ///
    /// O(区域数) 不是 O(敌人数)——AreaZoneSystem.MaxZones=64 硬顶，
    /// 与 AreaZoneSystem 自身 OnUpdate 同复杂度量级，不违反热更层红线。
    /// </summary>
    public sealed class ZoneVisualPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly IZoneVisualFeedback _impl;
        private AreaZoneSystem _zones;

        public ZoneVisualPresenter(IZoneVisualFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxZoneVisual();
        }

        public void Bind(AreaZoneSystem zones)
        {
            _zones = zones;
        }

        public override void OnUpdate(float dt)
        {
            if (_zones == null)
            {
                return;
            }

            _impl.Sync(_zones.Zones);
        }

        public override void OnExit()
        {
            (_impl as System.IDisposable)?.Dispose();
        }
    }
}
