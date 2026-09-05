using System.Collections.Generic;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Stats;

namespace GameLogic.MetabolicSlice.Structural
{
    public enum StructuralOrganResult { Ok, PartNotFound, NotStructuralCategory, SlotEmpty }

    /// <summary>结构器官装配状态：每个 VisualSlotTag 至多一件常驻被动器官（story-001 Required 4/5）。
    /// 本类自身的槽位存储独立于 CarrierInstance.Slots；结构器官的基因槽（CarrierInstance）由
    /// <see cref="StructuralOrganService.Equip"/> 另外在 CarrierRegistry 里开（story-002），
    /// 两者是并存关系，不是互斥关系。</summary>
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
    /// （Required 6）——Equip 会通过 CarrierRegistry.EnsureCarrier(autoActivate: false) 为结构器官开一份
    /// 基因槽（story-002），但不会让它抢占 ActiveCarrierId，也不改 CarrierCompiler/TickCarrier 本身。</summary>
    public static class StructuralOrganService
    {
        public static StructuralOrganResult Equip(BagInventory bag, StructuralSlots slots, StatSheet sheet, string partId, VisualSlotTag tag,
            StructuralHookRunner hookRunner = null, CarrierRegistry carrierRegistry = null)
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
                hookRunner?.UnregisterHook(old.RuntimeSourceId);
                old.Location = PartLocation.Bag();
                bag.Items.Add(old);
            }

            bag.Items.Remove(part);
            part.Location = PartLocation.Structural(tag);
            slots.Set(tag, part);
            // story-002：结构器官也开一份基因槽（CarrierInstance），但不能抢占 ActiveCarrierId
            // ——结构器官 AttackMethod=false，抢占会让 TickCarrier 打出空 HitEvent（preflight #3）。
            carrierRegistry?.EnsureCarrier(part.PartId, part.CardDefId, autoActivate: false);
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
            // story-010：触发钩子随装备生效启停，照抄 StructuralEffects 紧邻装/卸位置的写法。
            if (def.TriggerHook.HasValue)
            {
                hookRunner?.RegisterHook(part.RuntimeSourceId, def.TriggerHook.Value);
            }

            slots.IncrementVersion();
            return StructuralOrganResult.Ok;
        }

        public static StructuralOrganResult Unequip(BagInventory bag, StructuralSlots slots, StatSheet sheet, VisualSlotTag tag,
            StructuralHookRunner hookRunner = null)
        {
            var part = slots.Get(tag);
            if (part == null)
            {
                return StructuralOrganResult.SlotEmpty;
            }

            sheet.RemoveBySource(part.RuntimeSourceId);
            hookRunner?.UnregisterHook(part.RuntimeSourceId);
            slots.Clear(tag);
            part.Location = PartLocation.Bag();
            bag.Items.Add(part);

            slots.IncrementVersion();
            return StructuralOrganResult.Ok;
        }
    }
}
