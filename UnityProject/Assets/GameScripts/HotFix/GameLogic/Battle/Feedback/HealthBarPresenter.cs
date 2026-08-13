using GameLogic.Core;
using GameLogic.Stats;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 血条表现层模块（story-008）。
    ///
    /// 与 <see cref="ZoneVisualPresenter"/> 同款"接口 + 默认白模实现 + 薄壳 Module"三段式，
    /// 同样主动偏离 <see cref="CombatFeedbackPresenter"/>/<see cref="AbilityCastPresenter"/>
    /// "只订阅 Signals、不持有系统引用"的惯例：需要连续读 Health/Position 而非一次性事件，
    /// 所以直接 <see cref="Bind"/> 持有 <see cref="SimBridge"/>/<see cref="StatSheet"/>、每帧轮询。
    /// </summary>
    public sealed class HealthBarPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly IHealthBarFeedback _impl;
        private SimBridge _sim;
        private StatSheet _stats;

        public HealthBarPresenter(IHealthBarFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxHealthBar();
        }

        public void Bind(SimBridge sim, StatSheet stats)
        {
            _sim = sim;
            _stats = stats;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || _stats == null || !_sim.Running)
            {
                return;
            }
            _impl.Sync(_sim.Snapshot, _stats.Get(StatId.MaxHealth));
        }

        public override void OnExit()
        {
            (_impl as System.IDisposable)?.Dispose();
        }

        /// <summary>调试计数透传，供 execute_code 断言"O(缓存) 而非 O(容量)"用（story-008 AC）。</summary>
        public int EliteBossActiveCount => (_impl as WhiteboxHealthBar)?.EliteBossActiveCount ?? 0;
        public int HitTrackActiveCount => (_impl as WhiteboxHealthBar)?.HitTrackActiveCount ?? 0;
    }
}
