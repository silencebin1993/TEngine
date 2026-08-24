using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.Stage.CellStage;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 任务二（3D 表现差异化）：玩家 Carrier 本体随装配变化。
    ///
    /// 同 <see cref="ComposeAimIndicatorPresenter"/> 骨架——轮询
    /// <see cref="CarrierRegistry.AssemblyVersion"/>（装/卸基因）与
    /// <see cref="CarrierRegistry.CarrierActivatedEvent"/>（切换激活 Carrier），
    /// 脏了才重算，O(1)。渲染只在 <c>capsule/emitter/cilia</c> × <c>是否挂基因</c>
    /// 四种造型间切 VisualId，不逐基因建模——24 种器官已在图鉴/沙盒对比台里可辨，
    /// 玩家本体只需要"看得出装了什么类型的出口器官 + 挂没挂东西"。
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
            var registry = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.CarrierRegistry;
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

            bool hasGene = false;
            foreach (CarrierSlot slot in active.Slots)
            {
                if (!string.IsNullOrEmpty(slot.GeneInstanceId))
                {
                    hasGene = true;
                    break;
                }
            }

            switch (active.OrganelleId)
            {
                case "org_emitter":
                    return hasGene ? "carrier/emitter_gene" : "carrier/emitter";
                case "org_cilia":
                    return hasGene ? "carrier/cilia_gene" : "carrier/cilia";
                default:
                    string artId = GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog.Get(active.OrganelleId)?.ArtId;
                    return artId ?? "carrier/base";
            }
        }

        public override void OnExit()
        {
            TEngine.GameEvent.RemoveEventListener(
                CarrierRegistry.CarrierActivatedEvent, (System.Action)OnCarrierChanged);
        }
    }
}
