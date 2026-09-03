using System.Collections.Generic;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.Structural
{
    public enum StructuralOrganResult { Ok, PartNotFound, NotStructuralCategory, SlotEmpty }

    /// <summary>结构器官装配状态：每个 VisualSlotTag 至多一件常驻被动器官（story-001 Required 4/5）。
    /// 独立于 CarrierInstance.Slots——v1 不给结构器官分配 CarrierInstance/Slots（Required 2/6）。</summary>
    public sealed class StructuralSlots
    {
        private readonly Dictionary<VisualSlotTag, PartInstance> _equipped = new Dictionary<VisualSlotTag, PartInstance>();

        /// <summary>装/卸变化脏计数，供 <see cref="GameLogic.Battle.Feedback.StructuralVisualPresenter"/>
        /// 轮询（story-003，照抄 CarrierRegistry.AssemblyVersion 手法）。</summary>
        public int Version { get; private set; }

        public PartInstance Get(VisualSlotTag tag) => _equipped.TryGetValue(tag, out var part) ? part : null;

        internal void Set(VisualSlotTag tag, PartInstance part) => _equipped[tag] = part;
        internal void Clear(VisualSlotTag tag) => _equipped.Remove(tag);
        internal void IncrementVersion() => Version++;
    }

    /// <summary>装/卸结构器官：储备囊 ↔ 结构槽（Required 5）。风格照抄 CarrierGeneService：静态类，
    /// Reject-to-Safe，不抛异常，返回结果枚举。结构器官完全不进 CarrierCompiler/TickCarrier 遍历判据
    /// （Required 6），本服务不依赖、也不修改 Carrier 目录下的任何类型。</summary>
    public static class StructuralOrganService
    {
        public static StructuralOrganResult Equip(BagInventory bag, StructuralSlots slots, StatSheet sheet, string partId, VisualSlotTag tag)
        {
            var part = bag.Items.Find(p => p.PartId == partId);
            if (part == null)
            {
                return StructuralOrganResult.PartNotFound;
            }

            var def = OrganelleCatalog.Get(part.CardDefId);
            if (def == null || def.Category != OrganelleCategory.Structural)
            {
                return StructuralOrganResult.NotStructuralCategory;
            }

            var old = slots.Get(tag);
            if (old != null)
            {
                sheet.RemoveBySource(old.RuntimeSourceId);
                old.Location = PartLocation.Bag();
                bag.Items.Add(old);
            }

            bag.Items.Remove(part);
            part.Location = PartLocation.Structural(tag);
            slots.Set(tag, part);
            if (def.StructuralEffects != null)
            {
                // 目录里的 StatModifier 是静态共享数据，SourceId 恒为默认值 0——必须逐条盖成
                // 该实例的 RuntimeSourceId，RemoveBySource 才认得出这是"这一件"的修正。
                for (int i = 0; i < def.StructuralEffects.Length; i++)
                {
                    var mod = def.StructuralEffects[i];
                    mod.SourceId = part.RuntimeSourceId;
                    sheet.Add(mod);
                }
            }

            slots.IncrementVersion();
            return StructuralOrganResult.Ok;
        }

        public static StructuralOrganResult Unequip(BagInventory bag, StructuralSlots slots, StatSheet sheet, VisualSlotTag tag)
        {
            var part = slots.Get(tag);
            if (part == null)
            {
                return StructuralOrganResult.SlotEmpty;
            }

            sheet.RemoveBySource(part.RuntimeSourceId);
            slots.Clear(tag);
            part.Location = PartLocation.Bag();
            bag.Items.Add(part);

            slots.IncrementVersion();
            return StructuralOrganResult.Ok;
        }
    }
}
