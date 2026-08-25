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

        public void Bind(SimBridge sim) => _sim = sim;

        public override void OnEnter()
        {
            TEngine.GameEvent.AddEventListener(CarrierRegistry.CarrierActivatedEvent, OnCarrierChanged);
        }

        private void OnCarrierChanged() => Refresh();

        public override void OnUpdate(float dt)
        {
            var registry = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.CarrierRegistry;
            if (registry != null && registry.AssemblyVersion != _lastAssemblyVersion)
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            var panel = GameLogic.UI.Battle.MetabolicSlicePanel.Instance;
            var registry = panel?.CarrierRegistry;
            if (registry == null || _sim == null)
            {
                return;
            }
            _lastAssemblyVersion = registry.AssemblyVersion;

            CarrierInstance active = registry.ActiveCarrier;
            string artId = ResolveArtId(active);
            int visualId = CellStageFlow.VisualIdForArtId(artId);
            if (visualId >= 0)
            {
                _sim.SetPlayerVisualId(visualId);
            }
        }

        private static string ResolveArtId(CarrierInstance active)
        {
            if (active == null)
            {
                return "carrier/base";
            }

            string baseArtId;
            switch (active.OrganelleId)
            {
                case "org_emitter":
                    baseArtId = "carrier/emitter";
                    break;
                case "org_cilia":
                    baseArtId = "carrier/cilia";
                    break;
                default:
                    baseArtId = GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.Get(active.OrganelleId)?.ArtId
                        ?? "carrier/base";
                    break;
            }

            string group = GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.Get(active.OrganelleId)?.AttackFamily;
            return group == null ? baseArtId : baseArtId + "::" + group.ToLowerInvariant();
        }

        /// <summary>供 execute_code 反射/直调断言用（002 验收口径，见 preflight-decisions R1 点 6）——
        /// <see cref="ResolveArtId"/> 保持 private static 不扩大真正公共 API 面。</summary>
        internal static string DebugResolveArtId(CarrierInstance active) => ResolveArtId(active);

        public override void OnExit()
        {
            TEngine.GameEvent.RemoveEventListener(
                CarrierRegistry.CarrierActivatedEvent, (System.Action)OnCarrierChanged);
        }
    }
}
