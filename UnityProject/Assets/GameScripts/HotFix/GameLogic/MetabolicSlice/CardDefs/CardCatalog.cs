using System;
using System.Collections.Generic;
using ChemEngine.Builtin.Modules;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.CardDefs
{
    /// <summary>§9 最小占位：演示卡覆盖 source/mid/sink + 合成产物。真正内容表见 tools/cell_tables/（本窗不接）。</summary>
    public static class CardCatalog
    {
        private static readonly SlotType[] AllThreeTypes =
            { SlotType.Cytoplasm, SlotType.Membrane, SlotType.Lattice };

        private static readonly SlotType[] AllSixTypes =
        {
            SlotType.Cytoplasm, SlotType.Membrane, SlotType.Lattice,
            SlotType.Perinuclear, SlotType.Secretory, SlotType.AcidFen,
        };

        private static readonly Dictionary<string, CardDef> _defs = new Dictionary<string, CardDef>
        {
            ["organ_core"] = new CardDef(
                "organ_core", "能源芯", AllThreeTypes,
                isSource: true, isSink: false,
                createModule: () => new EnergyCore()),
            ["organ_focus"] = new CardDef(
                "organ_focus", "聚焦镜", AllThreeTypes,
                isSource: false, isSink: false,
                createModule: () => new FocusLens()),
            ["organ_focus_plus"] = new CardDef(
                "organ_focus_plus", "聚焦镜+1", AllThreeTypes,
                isSource: false, isSink: false,
                createModule: () => new FocusLens(2.0f, 3f)),
            ["organ_actuator"] = new CardDef(
                "organ_actuator", "执行器", AllThreeTypes,
                isSource: false, isSink: true,
                createModule: () => new Actuator()),
            ["scrap_material"] = new CardDef(
                "scrap_material", "拆解素材", System.Array.Empty<SlotType>(),
                isSource: false, isSink: false,
                createModule: null), // 不可装备：AllowedSlotTypes 为空，纯囊内合成材料
        };

        static CardCatalog()
        {
            // 冻结总案 G8：24 张 org_* 卡，AllowedSlotTypes/CreateModule/ArtId 直接取自 OrganelleCatalog（单一数据源，不重复维护）。
            foreach (var kv in OrganelleCatalog.All)
            {
                var organelle = kv.Value;
                var allowedSlots = organelle.AttachTarget == OrganelleAttachTarget.DirectedEdge
                    ? Array.Empty<SlotType>()
                    : (IEnumerable<SlotType>)(organelle.AllowedSlotTypes ?? (IEnumerable<SlotType>)AllSixTypes);

                _defs[organelle.Id] = new CardDef(
                    organelle.Id, organelle.DisplayName, allowedSlots,
                    isSource: organelle.Role == OrganelleRole.Source,
                    isSink: organelle.Role == OrganelleRole.Sink,
                    createModule: organelle.CreateModule,
                    artId: organelle.ArtId);
            }
        }

        public static CardDef Get(string cardDefId) => _defs.TryGetValue(cardDefId, out var def) ? def : null;

        public static bool AllowsSlot(string cardDefId, SlotType slotType)
        {
            var def = Get(cardDefId);
            return def != null && def.AllowedSlotTypes.Contains(slotType);
        }
    }
}
