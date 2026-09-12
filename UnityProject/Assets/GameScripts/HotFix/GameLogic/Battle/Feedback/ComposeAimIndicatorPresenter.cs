using BinGames.Sim;
using Cysharp.Threading.Tasks;
using GameLogic.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// story-010 J3：组合弹道瞄准指示器表现层模块。
    ///
    /// 订阅 <see cref="MetabolicSlice.Carrier.CarrierRegistry.CarrierActivatedEvent"/>（装配切换）
    /// 与未来的"装备变更"事件，实时刷新指示器预览。J2：独立 8 位池，不与弹道 32 位池共用。
    /// 与 <see cref="ComposeProjectilePresenter"/> 同骨架：换美术只需换注入。
    /// </summary>
    public sealed class ComposeAimIndicatorPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private readonly IComposeAimIndicatorFeedback _impl;
        private SimBridge _sim;
        private SignalScope _scope;
        private SimEntityId _lastControlledUnitId;

        /// <summary>J4：脏则重编译的缓存版本号，-1 保证首帧必刷新一次。</summary>
        private int _lastAssemblyVersion = -1;

        public ComposeAimIndicatorPresenter(IComposeAimIndicatorFeedback impl = null)
        {
            _impl = impl ?? new WhiteboxComposeAimIndicator();
        }

        /// <summary>加载 indicator 槽 VFX Prefab 池绑定，与 <see cref="ComposeProjectilePresenter.LoadArtBindingsAsync"/>
        /// 同手法——接口不改，只有 <see cref="WhiteboxComposeAimIndicator"/> 才有池，非白模实现类型检查桥接后走空操作。</summary>
        public UniTask LoadArtBindingsAsync() =>
            (_impl as WhiteboxComposeAimIndicator)?.LoadVfxBindingsAsync() ?? UniTask.CompletedTask;

        public override void OnEnter()
        {
            _sim = Hub.Get<SimBridge>();
            _scope = new SignalScope();
            _scope.On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
            // J3 决策：订阅 CarrierActivatedEvent，装配切换后刷新指示器
            // （T3：SetActive 只发事件、不动 AssemblyVersion，故仍需要这条信号）
            TEngine.GameEvent.AddEventListener(
                MetabolicSlice.Carrier.CarrierRegistry.CarrierActivatedEvent,
                OnCarrierChanged);
        }

        private void OnCarrierChanged()
        {
            Refresh();
        }

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            _impl.Hide();
            _lastControlledUnitId = SimEntityId.None;
            Refresh();
        }

        public override void OnUpdate(float dt)
        {
            SimEntityId controlledId = _sim != null ? _sim.ControlledUnitId : SimEntityId.None;
            if (controlledId != _lastControlledUnitId)
            {
                _impl.Hide();
                Refresh();
            }

            // J4：每帧只做一次 int 版本号比较（O(1)）；版本变了才重算——EquipGene/UnequipGene
            // 会 ++AssemblyVersion 但不发 CarrierActivatedEvent，这是版本号信号存在的唯一理由
            var registry = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.CarrierRegistry;
            if (registry != null && registry.AssemblyVersion != _lastAssemblyVersion)
            {
                Refresh();
            }

            _impl.Tick(dt);
        }

        private void Refresh()
        {
            if (_sim == null || !_sim.TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                _lastControlledUnitId = SimEntityId.None;
                _impl.Hide();
                return;
            }
            _lastControlledUnitId = controlled.EntityId;

            var registry = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.CarrierRegistry;
            if (registry != null)
            {
                _lastAssemblyVersion = registry.AssemblyVersion;
            }

            // J4：从 MetabolicSliceBridge 读 seed（只读，禁止自增/写回）
            var bridge = Hub.Get<MetabolicSlice.Combat.MetabolicSliceBridge>();
            if (bridge != null)
            {
                _impl.ShowPlayerIndicator(bridge.Seed);
            }
        }

        public override void OnExit()
        {
            _scope?.Dispose();
            _scope = null;
            TEngine.GameEvent.RemoveEventListener(
                MetabolicSlice.Carrier.CarrierRegistry.CarrierActivatedEvent,
                (System.Action)OnCarrierChanged);
            (_impl as System.IDisposable)?.Dispose();
            _sim = null;
            _lastControlledUnitId = SimEntityId.None;
        }
    }
}
