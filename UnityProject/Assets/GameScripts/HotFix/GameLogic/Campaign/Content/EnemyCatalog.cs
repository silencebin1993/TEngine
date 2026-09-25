using System.Collections.Generic;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：6 敌类（DEMO-CONTENT-LOCK.md §5 战斗与反应默认调校表）。全仓库此前
    /// 零匹配这六个中文名（勘查确认，见证据文档），本类是它们第一次作为可查询内容目录条目存在。
    ///
    /// 区域遭遇/AI行为树/阵型分别属于 ER5-SILENT-01（静默侦察/干扰）、ER6-FOUNDRY-01（铸造三类）、
    /// ER7-CORE-01（主核心两阶段状态机），STORY-BOARD.md 已排期，本 Story 不重复实现；本类只交付
    /// HP/护甲减伤等§5数值目录 + 可被 execute_code 直接验证的纯函数（<see cref="ComputeFrontalArmorReducedDamage"/>），
    /// 证明数值真的能喂进伤害计算而不是只写在文档里。</summary>
    public static class EnemyCatalog
    {
        public const string ScoutId = "enemy_scout";
        public const string JammerId = "enemy_jammer";
        public const string StriderId = "enemy_strider";
        public const string ArmorBotId = "enemy_armorbot";
        public const string RepairBotId = "enemy_repairbot";
        public const string CoreBossId = "enemy_core_boss";

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [ScoutId] = new MechanicalContentDef
            {
                Id = ScoutId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "静默侦察机",
                Description = "破碎都市侦察走廊巡逻单位，周期性标记玩家位置。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "破碎都市（第1次出击）侦察走廊遭遇（DEMO-CONTENT-LOCK.md §4.1/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP70；每8秒标记一次（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_scout",
                ModelId = "primitive:capsule",
                ActionId = "EnemyCatalog.All[\"enemy_scout\"].ValuesSummary + 独立 Sim 伤害交换验证（execute_code，见证据文档）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_enemy_fire",
                PreviewId = "preview_enemy_scout",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "AC-THEME-002 要求玩家/静默/铸造三方轮廓/运动可区分；占位几何体阶段三方共用相同基础形状，登记 DEBT。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            },
            [JammerId] = new MechanicalContentDef
            {
                Id = JammerId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "静默干扰机",
                Description = "驻守监听节点的干扰单位，干扰半径内接管请求返回 SignalJammed。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "破碎都市（第1次出击）监听节点驻守（DEMO-CONTENT-LOCK.md §4.1/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP90；干扰半径12米，可由监听节点关闭（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_jammer",
                ModelId = "primitive:capsule",
                ActionId = "EnemyCatalog.All[\"enemy_jammer\"].ValuesSummary + 独立 Sim 伤害交换验证（execute_code，见证据文档）",
                VfxId = "vfx_jam_field_placeholder",
                SfxId = "sfx_signal_jam",
                PreviewId = "preview_enemy_jammer",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "同 enemy_scout，登记同一条 DEBT。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            },
            [StriderId] = new MechanicalContentDef
            {
                Id = StriderId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "铸造步进炮",
                Description = "驻守重炮回收点的重型单位，开火前有1秒瞄准线。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "铸造前哨外围（第2次出击）重炮回收点驻守（DEMO-CONTENT-LOCK.md §4.2/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP110；1秒瞄准线（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_strider",
                ModelId = "primitive:capsule",
                ActionId = "EnemyCatalog.All[\"enemy_strider\"].ValuesSummary + 独立 Sim 伤害交换验证（execute_code，见证据文档）",
                VfxId = "vfx_aim_line_placeholder",
                SfxId = "sfx_enemy_cannon",
                PreviewId = "preview_enemy_strider",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "AC-THEME-002 铸造方轮廓/运动区分待美术，登记 DEBT-ER4CONTENT01-13。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            },
            [ArmorBotId] = new MechanicalContentDef
            {
                Id = ArmorBotId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "铸造护甲机",
                Description = "驻守左右掩体的重甲单位，正面大幅减伤、侧后无减伤。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "铸造前哨外围（第2次出击）左右掩体驻守（DEMO-CONTENT-LOCK.md §4.2/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP160；正面减伤40%，侧后不减（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_armorbot",
                ModelId = "primitive:capsule",
                ActionId = "EnemyCatalog.ComputeFrontalArmorReducedDamage(...) — 纯函数，已用 execute_code 验证正面/侧后两组数值",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_enemy_fire_heavy",
                PreviewId = "preview_enemy_armorbot",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "同 enemy_strider，登记同一条 DEBT。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            },
            [RepairBotId] = new MechanicalContentDef
            {
                Id = RepairBotId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "铸造维修机",
                Description = "随行支援单位，优先修复己方低血量同伴。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "铸造前哨外围（第2次出击）随行支援（DEMO-CONTENT-LOCK.md §4.2/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "HP80；优先救低血同伴（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_repairbot",
                ModelId = "primitive:capsule",
                ActionId = "EnemyCatalog.All[\"enemy_repairbot\"].ValuesSummary + 独立 Sim 伤害交换验证（execute_code，见证据文档）",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_repair_beam",
                PreviewId = "preview_enemy_repairbot",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "同 enemy_strider，登记同一条 DEBT。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            },
            [CoreBossId] = new MechanicalContentDef
            {
                Id = CoreBossId,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = "铸造前哨主核心",
                Description = "第三次出击的两阶段主核心：Shielded→Phase1→Transition→Phase2。",
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = "铸造前哨核心（第3次出击）Boss 战（DEMO-CONTENT-LOCK.md §4.3/§5）。",
                Slot = "敌类",
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = "核心初始Shielded；两供能节点各HP100；主核心HP600；Phase1降至60%时5秒Transition只召一台维修机；" +
                    "Phase2散热口暴露，侧后攻击+20%伤害（DEMO-CONTENT-LOCK.md §4.3）。",
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = "icon_enemy_core_boss",
                ModelId = "primitive:cube",
                ActionId = "EnemyCatalog.All[\"enemy_core_boss\"].ValuesSummary — 状态机/供能节点/阶段转换均未实现",
                VfxId = "vfx_boss_phase_placeholder",
                SfxId = "sfx_enemy_cannon",
                PreviewId = "preview_enemy_core_boss",
                SaveCompatible = true,
                LockedHintText = "不适用（敌人非玩家解锁对象）。",
                SilhouetteNote = "巨型核心剪影+供能节点，全新内容，无 legacy 参照。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-16",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);

        /// <summary>铸造护甲机"正面减伤40%，侧后不减"（DEMO-CONTENT-LOCK.md §5）的纯函数版本，
        /// 不依赖 Sim/JobDamage——真正接入战斗伤害结算属于 ER6-FOUNDRY-01，这里先把数值规则
        /// 本身做成可独立验证、未来可以直接搬进结算代码的形式，而不是只停留在文档数字。</summary>
        public static float ComputeFrontalArmorReducedDamage(float rawDamage, bool isFrontalHit)
        {
            const float frontalDamageReductionPct = 0.4f;
            return isFrontalHit ? rawDamage * (1f - frontalDamageReductionPct) : rawDamage;
        }
    }
}
