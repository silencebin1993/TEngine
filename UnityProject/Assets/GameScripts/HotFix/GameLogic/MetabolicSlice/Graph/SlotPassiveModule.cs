using ComposeEngine.Core;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Graph
{
    /// <summary>
    /// 槽类型被动（§5.2）；占用槽与空槽都跑，纯 Packet 层效果（tag/Energy/Heat），不碰 ComposeEngine 核心——
    /// 就是 README 里"新增 Module，不改旧代码"的标准扩展点。
    ///
    /// Perinuclear(核周/基因效力+) deferred: GenePotencyBias hook 仍缺。它需要模块态(SimContext)写值、
    /// 契约正规化(NormalizeContracts→RuleVector)读值的跨管线通信，Engine 目前没有这个钩子（两条管线互不
    /// 读对方状态）。等基因系统真正接入那一窗，再给 Engine 加这个读接口；本 case 留空 TODO。
    /// </summary>
    public sealed class SlotPassiveModule : IModule
    {
        public string Id { get; }
        public string Name => "槽被动 SlotPassive";

        private readonly SlotType _slotType;

        public SlotPassiveModule(SlotType slotType)
        {
            _slotType = slotType;
            Id = "slot_passive_" + slotType;
        }

        public Packet Step(Packet packet, SimContext ctx)
        {
            switch (_slotType)
            {
                case SlotType.Cytoplasm:
                    packet.Heat += 0.5f; // 微导热
                    break;
                case SlotType.Membrane:
                    packet.Tags.Add("Wet"); // 易湿
                    packet.Energy *= 0.95f; // 减伤（弱修正）
                    break;
                case SlotType.Lattice:
                    packet.Energy *= 1.05f; // 传导损耗↓（节点级近似：本窗不做管类边模块）
                    break;
                case SlotType.Perinuclear:
                    // TODO: GenePotencyBias 跨管线钩子未接（同上方注释），留空
                    break;
                case SlotType.Secretory:
                    packet.Payload["Delay"] = 0.15f; // 延迟+（简化，不动 Shape 字段）
                    break;
                case SlotType.AcidFen:
                    packet.Tags.Add("Acid");
                    packet.Heat += 0.5f;
                    break;
            }
            return packet;
        }
    }
}
