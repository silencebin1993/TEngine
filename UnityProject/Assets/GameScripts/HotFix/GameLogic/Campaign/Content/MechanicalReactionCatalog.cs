using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：2 反应（DEMO-CONTENT-LOCK.md §3"反应"行："非独立拾取；满足已解锁组件
    /// 组合，保存蓝图时由编译器识别"）。命名故意避开 <c>ComposeEngine.Builtin.Catalog.ReactionCatalog</c>
    /// （引擎内建反应注册表，两者是不同层级的概念，不能合并）。
    ///
    /// 本类交付两件事：①反应内容目录条目本身（DisplayName/说明/组合条件文案）；②组合条件的纯函数判定
    /// （<see cref="DetectMarkJump"/>/<see cref="DetectMeltOverload"/>），可被 execute_code 直接断言、
    /// 也是未来蓝图编译器（ER6-REACT-01/02）可以直接复用的判定逻辑，不需要重新设计规则。真正"保存蓝图
    /// 时由编译器识别并解锁反应效果"这条完整流程——包括把判定结果接到 <c>ComposeEngine</c> 的反应触发、
    /// 熔穿过载的精确数值耦合——分别移交 ER6-REACT-01/ER6-REACT-02（已在 STORY-BOARD.md 排期）。</summary>
    public static class MechanicalReactionCatalog
    {
        public const string ReactionMarkJumpId = "reaction_marktag";
        public const string ReactionMeltOverloadId = "reaction_meltoverload";

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [ReactionMarkJumpId] = new MechanicalContentDef
            {
                Id = ReactionMarkJumpId,
                Category = MechanicalContentCategory.Reaction,
                DisplayName = "标记跳转",
                Description = "标记器与连射器/切割束同装时，主武器命中会额外跳向已标记的邻近敌人。",
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；满足静默标记器+连射器/切割束同装，保存蓝图时由编译器识别（DEMO-CONTENT-LOCK.md §3/§2.4）。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "标记跳转固件必须与标记器和连射器/切割束同装；干扰清标记、分散站位可反制（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_marktag",
                ModelId = "n/a",
                ActionId = "MechanicalReactionCatalog.DetectMarkJump(...) — 纯函数组合检测，已用 execute_code 验证正/负两组输入",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_reaction_marktag",
                SaveCompatible = true,
                LockedHintText = "需先解锁静默标记器与标记跳转固件。",
                SilhouetteNote = "不适用（无独立单位，是组合判定结果）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-11",
            },
            [ReactionMeltOverloadId] = new MechanicalContentDef
            {
                Id = ReactionMeltOverloadId,
                Category = MechanicalContentCategory.Reaction,
                DisplayName = "熔穿过载",
                Description = "铸造重炮与过载固件组合触发，额外穿甲并大幅提升热量代价。",
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；满足铸造重炮+过载固件同装，保存蓝图时由编译器识别（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "重炮+过载是熔穿过载唯一配方；额外穿甲+30%，基础热量40上再加25即每发65，不再叠加一般+15%热量（DEMO-CONTENT-LOCK.md §2.4/§5）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_meltoverload",
                ModelId = "n/a",
                ActionId = "MechanicalReactionCatalog.DetectMeltOverload(...) — 纯函数组合检测，已用 execute_code 验证正/负两组输入",
                VfxId = "vfx_meltoverload_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_reaction_meltoverload",
                SaveCompatible = true,
                LockedHintText = "需先解锁铸造重炮与过载固件。",
                SilhouetteNote = "不适用（无独立单位，是组合判定结果）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-12",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);

        /// <summary>DEMO-CONTENT-LOCK.md §2.4："标记跳转固件必须与标记器和连射器/切割束同装"。
        /// 纯函数，不依赖任何游戏状态，供蓝图编译（ER6-REACT-01）与本 Story 自动化测试共用。</summary>
        public static bool DetectMarkJump(IReadOnlyCollection<string> mainComponentIds,
            IReadOnlyCollection<string> functionComponentIds, IReadOnlyCollection<string> firmwareIds)
        {
            if (mainComponentIds == null || functionComponentIds == null || firmwareIds == null)
            {
                return false;
            }
            bool hasMarker = functionComponentIds.Contains(ComponentCatalog.FuncMarkerId);
            bool hasMarkJumpFirmware = firmwareIds.Contains(FirmwareCatalog.FwMarkTagId);
            bool hasCompatibleMain = mainComponentIds.Contains(ComponentCatalog.CompGunId)
                || mainComponentIds.Contains(ComponentCatalog.CompBeamId);
            return hasMarker && hasMarkJumpFirmware && hasCompatibleMain;
        }

        /// <summary>DEMO-CONTENT-LOCK.md §2.4："重炮+过载是熔穿过载唯一配方"。</summary>
        public static bool DetectMeltOverload(IReadOnlyCollection<string> mainComponentIds,
            IReadOnlyCollection<string> firmwareIds)
        {
            if (mainComponentIds == null || firmwareIds == null)
            {
                return false;
            }
            return mainComponentIds.Contains(ComponentCatalog.CompCannonId)
                && firmwareIds.Contains(FirmwareCatalog.FwOverloadId);
        }
    }
}
