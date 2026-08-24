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
    /// 脏了才重算，O(1)。渲染在 <c>Carrier 类型（emitter/cilia/其余 19 个器官）</c> ×
    /// <c>挂的基因分组（None/Relay/Transform/Edge/Contract）</c> 间切 VisualId：
    /// 同组基因视觉相同、不同组可辨，不逐基因建模。多组同时装备时只取"显性组"单一态
    /// （优先级 Transform > Edge > Relay > Contract，见 preflight-decisions R0）。
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
            string artId = ResolveArtId(active, panel.GeneReserve);
            int visualId = CellStageFlow.VisualIdForArtId(artId);
            if (visualId >= 0)
            {
                _sim.SetPlayerVisualId(visualId);
            }
        }

        private static string ResolveArtId(CarrierInstance active, GeneReserve reserve)
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

            string group = ResolveDominantGroup(active, reserve);
            return group == null ? baseArtId : baseArtId + "::" + group.ToLowerInvariant();
        }

        /// <summary>装了多组基因时只取显性组（R0 优先级：Transform 改变攻击形态最直观 &gt; Edge 少见
        /// &gt; Relay 最常见 &gt; Contract 无器官语义兜底），不叠加多个 marker，避免新渲染路径。</summary>
        private static string ResolveDominantGroup(CarrierInstance active, GeneReserve reserve)
        {
            if (reserve == null)
            {
                return null;
            }

            bool hasTransform = false, hasEdge = false, hasRelay = false, hasContract = false;
            foreach (CarrierSlot slot in active.Slots)
            {
                if (string.IsNullOrEmpty(slot.GeneInstanceId))
                {
                    continue;
                }
                string geneId = reserve.Find(slot.GeneInstanceId)?.GeneId;
                if (geneId == null)
                {
                    continue;
                }
                switch (GameLogic.MetabolicSlice.ContentCatalog.GeneCatalog.GetVisualGroup(geneId))
                {
                    case "Transform": hasTransform = true; break;
                    case "Edge": hasEdge = true; break;
                    case "Relay": hasRelay = true; break;
                    case "Contract": hasContract = true; break;
                }
            }

            if (hasTransform) return "Transform";
            if (hasEdge) return "Edge";
            if (hasRelay) return "Relay";
            if (hasContract) return "Contract";
            return null;
        }

        /// <summary>供 execute_code 反射/直调断言用（002 验收口径，见 preflight-decisions R1 点 6）——
        /// <see cref="ResolveArtId"/> 保持 private static 不扩大真正公共 API 面。</summary>
        internal static string DebugResolveArtId(CarrierInstance active, GeneReserve reserve) =>
            ResolveArtId(active, reserve);

        public override void OnExit()
        {
            TEngine.GameEvent.RemoveEventListener(
                CarrierRegistry.CarrierActivatedEvent, (System.Action)OnCarrierChanged);
        }
    }
}
