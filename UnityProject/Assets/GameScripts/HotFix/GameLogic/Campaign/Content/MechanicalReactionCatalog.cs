using System.Collections.Generic;
using System.Linq;

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

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [ReactionMarkJumpId] = new MechanicalContentDef
            {
                Id = ReactionMarkJumpId,
                Category = MechanicalContentCategory.Reaction,
                DisplayName = "标记跳转",
                Description = "信号核带上标记跳转（核心固件，插进接入口）：命中后连锁只跳向已被标记的邻近敌人（任何标记来源都算），最多 2 跳。AI 驾驶时不会触发。",
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；由标记跳转固件的“只跳已标记”连锁产生（FG19 FGR-EMG-084，EMG2-REACT-01 端口化），没有规则行。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "标记跳转固件必须与标记器和连射器/切割束同装；干扰清标记、分散站位可反制（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_marktag",
                ModelId = "n/a",
                ActionId = "EmgRuleCatalog.AssemblyReactionId(...) — 按端口判定（EMG2-REACT-01）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_reaction_markjump",
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
                Description = "重型射弹带着足够的注热命中时触发（如接入铸造重炮机、信号核带上过载）：命中点溅出熔融金属，目标进入熔穿；开火热量代价提高。",
                Source = MechanicalContentSource.Composite,
                SourceDetail = "非独立拾取；通道规则 P03：同一次命中注热 ≥ 45 且动能 ≥ 40（FG19B §4.1 A2，EMG2-REACT-01 端口化）。",
                Slot = "反应",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "熔融金属 4 份（1000 度）+ 目标熔穿；开火者代价 25 取代过载表值 15，重炮每发 65；旧“额外穿甲 +30%”退役（FG19B P03）。",
                AiPermission = MechanicalContentAiPermission.NotApplicable,
                IconId = "icon_reaction_meltoverload",
                ModelId = "n/a",
                ActionId = "CombatLogic.EmgMeltOverloadHit — 通道规则 P03（EMG2-REACT-01）",
                VfxId = "vfx_meltoverload_placeholder",
                SfxId = "sfx_reaction_melt",
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

        // EMG2-REACT-01（FG19 FGR-EMG-021，ADR ⑪）：DetectMarkJump / DetectMeltOverload 两个“组件 + 固件”ID 配对判定已删除、豁免撤销——
        // 熔穿过载 = 通道规则 P03（重型射弹的单次动能 × 同一次命中注入的热），标记跳转 = 固件“以 evt.marked 为条件写 grm.mod.chain”的只跳已标记连锁；
        // 装配判定见 Emergence.EmgRuleCatalog.AssemblyReactionId（只认载体类别与端口）。

        /// <summary>FG1-SIG-03（FGR-SIG-033）：这条反应由哪条固件“发动”——与上面两个判定函数同一份配方（熔穿过载 ← 过载，标记跳转 ← 标记跳转固件）。
        /// 核心固件的冷却按这条固件记在信号上；不是具名反应时返回 null。FG2-FW-03 反应全量命名时在这里接入新配方。</summary>
        public static string TriggerFirmwareOf(string reactionId) => TriggerFirmwareOf(reactionId, null);

        /// <summary>
        /// EMG2-REACT-02（DEBT-EMG2REACT01-10）：这条反应的核心固件冷却记在哪条固件上——熔穿过载 = 这套装配里写 en.heat 注入的那条（按端口，不认 ID）；
        /// 没给装配（<paramref name="assembly"/> = null，信号上行 / 靶场 / 图鉴）时记在过载上。
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
                return FirmwareCatalog.FwOverloadId; // shortcut: 没给装配的调用方（信号上行 / 靶场 / 图鉴）仍算在过载上，别的写热固件的冷却归属见 DEBT-EMG2REACT02-01
            }
            if (reactionId == ReactionMarkJumpId)
            {
                return FirmwareCatalog.FwMarkTagId;
            }
            return null;
        }
    }
}
