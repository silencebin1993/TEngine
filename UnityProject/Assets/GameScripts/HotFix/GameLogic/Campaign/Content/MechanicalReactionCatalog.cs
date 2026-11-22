using System.Collections.Generic;
using System.Linq;
using GameLogic.Localization;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：2 反应（DEMO-CONTENT-LOCK.md §3"反应"行："非独立拾取；满足已解锁组件
    /// 组合，保存蓝图时由编译器识别"）。命名故意避开 <c>ComposeEngine.Builtin.Catalog.ReactionCatalog</c>
    /// （引擎内建反应注册表，两者是不同层级的概念，不能合并）。
    ///
    /// 本类交付两件事：①反应内容目录条目本身（DisplayName/说明/组合条件文案）；②组合条件的纯函数判定
    /// （EMG2-REACT-01 起改为端口判定，见 Emergence.EmgRuleCatalog.AssemblyReactionId），可被 execute_code 直接断言、
    /// 也是未来蓝图编译器（ER6-REACT-01/02）可以直接复用的判定逻辑，不需要重新设计规则。真正"保存蓝图
    /// 时由编译器识别并解锁反应效果"这条完整流程——包括把判定结果接到 <c>ComposeEngine</c> 的反应触发、
    /// 熔穿过载的精确数值耦合——分别移交 ER6-REACT-01/ER6-REACT-02（已在 STORY-BOARD.md 排期）。</summary>
    public static class MechanicalReactionCatalog
    {
        public const string ReactionMarkJumpId = "reaction_marktag";
        public const string ReactionMeltOverloadId = "reaction_meltoverload";

        private static Dictionary<string, MechanicalContentDef> _defs;
        private static GameLanguage _defsLanguage;
        private static int _defsTextRevision = -1;

        /// <summary>FG15-UX-03：按当前语言缓存的条目（切语言 / 重载文本表后下一次读取重建）。</summary>
        private static Dictionary<string, MechanicalContentDef> Defs
        {
            get
            {
                if (_defs == null || _defsLanguage != GameText.Language || _defsTextRevision != GameText.Revision)
                {
                    _defs = DemoDefs();
                    _defsLanguage = GameText.Language;
                    _defsTextRevision = GameText.Revision;
                }
                return _defs;
            }
        }

        /// <summary>FG15-UX-03：Demo 条目的文案走文本键，每次按当前语言重建（静态初始化时文本表可能还没载入）。</summary>
        private static Dictionary<string, MechanicalContentDef> DemoDefs() => new Dictionary<string, MechanicalContentDef>
        {
            [ReactionMarkJumpId] = new MechanicalContentDef
            {
                Id = ReactionMarkJumpId,
                Category = MechanicalContentCategory.Reaction,
                DisplayName = GameText.Get("firmware.fw_marktag.name"),
                Description = GameText.Get("reaction.demo.with_mark_jump_on"),
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；由标记跳转固件的“只跳已标记”连锁产生（FG19 FGR-EMG-084，EMG2-REACT-01 端口化），没有规则行。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("reaction.demo.mark_jump_firmware_must"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_marktag",
                ModelId = "n/a",
                ActionId = "EmgRuleCatalog.AssemblyReactionId(...) — 按端口判定（EMG2-REACT-01）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_reaction_markjump",
                PreviewId = "preview_reaction_marktag",
                SaveCompatible = true,
                LockedHintText = GameText.Get("reaction.demo.unlock_the_silent_marker"),
                SilhouetteNote = "不适用（无独立单位，是组合判定结果）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-11",
            },
            [ReactionMeltOverloadId] = new MechanicalContentDef
            {
                Id = ReactionMeltOverloadId,
                Category = MechanicalContentCategory.Reaction,
                DisplayName = GameText.Get("reaction.demo.melt_through_overload"),
                Description = GameText.Get("reaction.demo.triggers_when_a_heavy"),
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；通道规则 P03：同一次命中注热 ≥ 45 且动能 ≥ 40（FG19B §4.1 A2，EMG2-REACT-01 端口化）。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = GameText.Get("reaction.demo.4_units_of_molten"),
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_meltoverload",
                ModelId = "n/a",
                ActionId = "CombatLogic.EmgMeltOverloadHit — 通道规则 P03（EMG2-REACT-01）",
                VfxId = "vfx_meltoverload_placeholder",
                SfxId = "sfx_reaction_melt",
                PreviewId = "preview_reaction_meltoverload",
                SaveCompatible = true,
                LockedHintText = GameText.Get("reaction.demo.unlock_the_foundry_heavy"),
                SilhouetteNote = "不适用（无独立单位，是组合判定结果）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-12",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => Defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => Defs.TryGetValue(id, out def);

        // EMG2-REACT-01（FG19 FGR-EMG-021，ADR ⑪）：DetectMarkJump / DetectMeltOverload 两个“组件 + 固件”ID 配对判定已删除、豁免撤销——
        // 熔穿过载 = 通道规则 P03（重型射弹的单次动能 × 同一次命中注入的热），标记跳转 = 固件“以 evt.marked 为条件写 grm.mod.chain”的只跳已标记连锁；
        // 装配判定见 Emergence.EmgRuleCatalog.AssemblyReactionId（只认载体类别与端口）。

        /// <summary>FG1-SIG-03（FGR-SIG-033）：这条反应由哪条固件“发动”——与上面两个判定函数同一份配方（熔穿过载 ← 过载，标记跳转 ← 标记跳转固件）。
        /// 核心固件的冷却按这条固件记在信号上；不是具名反应时返回 null。FG2-FW-03 反应全量命名时在这里接入新配方。</summary>
        public static string TriggerFirmwareOf(string reactionId) => TriggerFirmwareOf(reactionId, null);

        /// <summary>
        /// EMG2-REACT-02（DEBT-EMG2REACT01-10）：这条反应的核心固件冷却记在哪条固件上——熔穿过载 = 这套装配里写 en.heat 注入的那条（按端口，不认 ID）；
        /// 没给装配（<paramref name="assembly"/> = null，只剩就绪 / 电路板提示这类假设性问法）或装配里没有写热注入的固件时返回过载。
        /// </summary>
        public static string TriggerFirmwareOf(string reactionId, System.Collections.Generic.IEnumerable<string> assembly)
        {
            if (reactionId == ReactionMeltOverloadId)
            {
                if (assembly != null)
                {
                    var one = new string[1];
                    foreach (string id in assembly)
                    {
                        one[0] = id;
                        if (!string.IsNullOrEmpty(id) && Emergence.EmgCatalog.PartWriteAmount("en.heat", Emergence.EmgRole.Inject, one) > 0f)
                        {
                            return id;
                        }
                    }
                }
                // FG11-REACT-01（DEBT-EMG2REACT02-01 关闭）：信号上行 / 靶场的冷却结算都传接入口装配；仍不传装配的只剩“假设你接入 X 会怎样”的就绪提示与电路板提示
                // （还没有装配，代表固件取过载），以及装配里没有写 en.heat 注入的固件（此时这条反应不由核心固件发动，调用方按 Contains 判不门控）。
                return FirmwareCatalog.FwOverloadId;
            }
            if (reactionId == ReactionMarkJumpId)
            {
                return FirmwareCatalog.FwMarkTagId;
            }
            return null;
        }
    }
}
