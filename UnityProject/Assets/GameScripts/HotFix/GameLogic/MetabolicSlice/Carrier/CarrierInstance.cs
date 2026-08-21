using System.Collections.Generic;

namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>一个 Carrier 器官的实例（D8：与玩家囊里该 Carrier 器官的 PartInstance 一一对应，
    /// CarrierId = 该 PartInstance.PartId）。插槽数动态可增长（slot-unlimited-codex/002 R1），
    /// 新建时默认 3 空槽（产品行为不变），软上限 <see cref="SlotSoftCap"/>。</summary>
    public sealed class CarrierInstance
    {
        /// <summary>新建 Carrier 默认插槽数（产品行为：给 3 空槽起步）。</summary>
        private const int DefaultSlotCount = 3;

        /// <summary>单 Carrier 插槽数软上限（R3），防 <see cref="CarrierCompiler"/> 单次
        /// NormalizeContracts 处理的 contract 列表过长。</summary>
        public const int SlotSoftCap = 32;

        public string CarrierId { get; }

        /// <summary>该 Carrier 对应的器官 def id（如 "org_cilia"），story-004 D9 新增——
        /// 004 前 CarrierCompiler 无法反查器官，链尾恒为 Bolt；null 时下游按 Reject-to-Safe 回落 Actuator()。</summary>
        public string OrganelleId { get; }

        public List<CarrierSlot> Slots { get; }

        public CarrierInstance(string carrierId, string organelleId = null)
        {
            CarrierId = carrierId;
            OrganelleId = organelleId;
            Slots = new List<CarrierSlot>(DefaultSlotCount);
            for (int i = 0; i < DefaultSlotCount; i++)
            {
                AddSlot();
            }
        }

        /// <summary>追加一个空槽；已达软上限时 no-op 返回 false（防止代码路径绕过 UI 直接超限，R3）。</summary>
        public bool AddSlot()
        {
            if (Slots.Count >= SlotSoftCap)
            {
                return false;
            }
            Slots.Add(new CarrierSlot(Slots.Count));
            return true;
        }

        /// <summary>移除指定索引的槽；越界 no-op 返回 false。</summary>
        public bool RemoveSlot(int index)
        {
            if (index < 0 || index >= Slots.Count)
            {
                return false;
            }
            Slots.RemoveAt(index);
            return true;
        }
    }
}
