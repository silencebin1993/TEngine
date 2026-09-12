using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.Stage.CellStage;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 任务二（3D 表现差异化）+ carrier-visual-feedback story-002（分组可辨）：
    /// 玩家 Carrier 本体随装配变化。
    ///
    /// 同 <see cref="ComposeAimIndicatorPresenter"/> 骨架——轮询
    /// <see cref="CarrierRegistry.AssemblyVersion"/>（装/卸基因）与
    /// <see cref="CarrierRegistry.CarrierActivatedEvent"/>（切换激活 Carrier），
    /// 脏了才重算，O(1)。combat-identity-rework story-007（R7）：分组键改为当前激活器官的
    /// <see cref="GameLogic.MetabolicSlice.ContentCatalog.OrganelleDef.AttackFamily"/>
    /// （CATALOG Family/Pattern 列，如 Projectile/Melee/Beam/Pool…），取代已废弃的按装备基因
    /// Role（None/Relay/Transform/Edge/Contract）分组——story-004 起基因已全部 Module 化，
    /// 不再携带 Role 语义。同一 AttackFamily 视觉相同、不同 Family 可辨，不逐器官建模。
    /// </summary>
    public sealed class CarrierBodyVisualPresenter : GameModuleBase
    {
        public override int Priority => ModulePriority.Presentation;

        private SimBridge _sim;
        private int _lastAssemblyVersion = -1;
        private SimEntityId _lastControlledUnitId;
        private SignalScope _scope;
        private readonly Dictionary<SimEntityId, int> _baseVisualByEntity =
            new Dictionary<SimEntityId, int>();

        public void Bind(SimBridge sim) => _sim = sim;

        public override void OnEnter()
        {
            TEngine.GameEvent.AddEventListener(CarrierRegistry.CarrierActivatedEvent, OnCarrierChanged);
            _scope = new SignalScope();
            _scope.On<ControlledUnitChangedSignal>(OnControlledUnitChanged);
        }

        private void OnCarrierChanged() => Refresh();

        public override void OnUpdate(float dt)
        {
            var registry = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.CarrierRegistry;
            SimEntityId controlledId = _sim != null ? _sim.ControlledUnitId : SimEntityId.None;
            if (!controlledId.IsValid)
            {
                RestoreBaseVisual(_lastControlledUnitId);
                _lastControlledUnitId = SimEntityId.None;
                return;
            }
            if (controlledId != _lastControlledUnitId ||
                (registry != null && registry.AssemblyVersion != _lastAssemblyVersion))
            {
                Refresh();
            }
        }

        private void OnControlledUnitChanged(ControlledUnitChangedSignal signal)
        {
            RestoreBaseVisual(signal.PreviousUnitId);
            _lastControlledUnitId = SimEntityId.None;
            Refresh();
        }

        private void Refresh()
        {
            var panel = GameLogic.UI.Battle.MetabolicSlicePanel.Instance;
            var registry = panel?.CarrierRegistry;
            if (_sim == null || !_sim.TryGetControlledPresentation(out SimControlledUnitView controlled))
            {
                _lastControlledUnitId = SimEntityId.None;
                return;
            }
            _lastControlledUnitId = controlled.EntityId;
            if (registry == null)
            {
                return;
            }
            _lastAssemblyVersion = registry.AssemblyVersion;

            if (!_baseVisualByEntity.ContainsKey(controlled.EntityId))
            {
                _baseVisualByEntity[controlled.EntityId] = controlled.VisualId;
            }

            CarrierInstance active = registry.ActiveCarrier;
            string artId = ResolveArtId(active);
            int visualId = CellStageFlow.VisualIdForArtId(artId);
            // AttackFamily 后缀（如 ::projectile）尚未进 AllArtIds 时 VisualId=-1，
            // 回退到无后缀 base，避免玩家卡在默认槽 0 / 看不见已绑定的 carrier mesh。
            if (visualId < 0)
            {
                visualId = CellStageFlow.VisualIdForArtId(ResolveBaseArtId(active));
            }
            if (visualId >= 0)
            {
                _sim.SetUnitVisualId(controlled.EntityId, visualId);
            }
        }

        private void RestoreBaseVisual(SimEntityId entityId)
        {
            if (!entityId.IsValid || !_baseVisualByEntity.TryGetValue(entityId, out int visualId))
            {
                return;
            }
            _sim?.SetUnitVisualId(entityId, visualId);
            _baseVisualByEntity.Remove(entityId);
        }

        private static string ResolveBaseArtId(CarrierInstance active)
        {
            if (active == null)
            {
                return "carrier/base";
            }

            switch (active.OrganelleId)
            {
                case "org_emitter":
                    return "carrier/emitter";
                case "org_cilia":
                    return "carrier/cilia";
                default:
                    return GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.Get(active.OrganelleId)?.ArtId
                        ?? "carrier/base";
            }
        }

        private static string ResolveArtId(CarrierInstance active)
        {
            string baseArtId = ResolveBaseArtId(active);
            if (active == null)
            {
                return baseArtId;
            }

            string group = GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.Get(active.OrganelleId)?.AttackFamily;
            return group == null ? baseArtId : baseArtId + "::" + group.ToLowerInvariant();
        }

        /// <summary>供 execute_code 反射/直调断言用（002 验收口径，见 preflight-decisions R1 点 6）——
        /// <see cref="ResolveArtId"/> 保持 private static 不扩大真正公共 API 面。</summary>
        internal static string DebugResolveArtId(CarrierInstance active) => ResolveArtId(active);

        public override void OnExit()
        {
            RestoreBaseVisual(_lastControlledUnitId);
            _scope?.Dispose();
            _scope = null;
            _baseVisualByEntity.Clear();
            _lastControlledUnitId = SimEntityId.None;
            _lastAssemblyVersion = -1;
            TEngine.GameEvent.RemoveEventListener(
                CarrierRegistry.CarrierActivatedEvent, (System.Action)OnCarrierChanged);
        }
    }
}
