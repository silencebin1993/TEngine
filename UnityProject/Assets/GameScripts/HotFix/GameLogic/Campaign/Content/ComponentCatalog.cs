using System.Collections.Generic;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：4 主组件 + 2 功能组件 + 4 结构（DEMO-CONTENT-LOCK.md §2.4/§3）。
    /// 三个槽位放同一个文件是因为它们共享同一套"装配到底盘上的可选组件"语义，拆三个文件只会让
    /// 交叉引用变多，不会提升可读性。</summary>
    public static class ComponentCatalog
    {
        // ── 主组件（每机恰好一个）────────────────────────────────────────────
        public const string CompGunId = "comp_gun";
        public const string CompBeamId = "comp_beam";
        public const string CompRepairBeamId = "comp_repairbeam";
        public const string CompCannonId = "comp_cannon";

        // ── 功能组件（最多一个）──────────────────────────────────────────────
        public const string FuncDashId = "func_dash";
        public const string FuncMarkerId = "func_marker";

        // ── 结构（最多一个）──────────────────────────────────────────────────
        public const string StructCargoId = "struct_cargo";
        public const string StructRelayId = "struct_relay";
        public const string StructArmorId = "struct_armor";
        public const string StructFinId = "struct_fin";

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            // ═══ 主组件 ═══
            [CompGunId] = new MechanicalContentDef
            {
                Id = CompGunId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = "连射器",
                Description = "远程投射武器，射程中等、冷却短，可自动索敌。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "主组件",
                ScrapCost = 20,
                Load = 2,
                ValuesSummary = "射程14米，基础8伤害/发，0.45秒冷却；普通可自动索敌（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_gun",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_emitter\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_projectile_placeholder",
                SfxId = "sfx_weapon_fire",
                PreviewId = "preview_comp_gun",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "枪管前伸剪影（沿用 org_emitter 既有识别度，占位几何体阶段无法体现）。",
                LegacyFacadeId = "org_emitter",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [CompBeamId] = new MechanicalContentDef
            {
                Id = CompBeamId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = "切割束",
                Description = "持续光束武器，命中持续伤害，持续使用会积热。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；新搬运轮式默认初装。",
                Slot = "主组件",
                ScrapCost = 20,
                Load = 2,
                ValuesSummary = "射程8米，持续12伤害/秒，持续使用积热（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_beam",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_lensbeam\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_beam_placeholder",
                SfxId = "sfx_beam_fire",
                PreviewId = "preview_comp_beam",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "细长束状剪影（沿用 org_lensbeam 既有识别度）。",
                LegacyFacadeId = "org_lensbeam",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [CompRepairBeamId] = new MechanicalContentDef
            {
                Id = CompRepairBeamId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = "维修束",
                Description = "近距离持续修复友军 HP，不复活阵亡机，消耗自身战术电池。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；维修悬浮默认初装。",
                Slot = "主组件",
                ScrapCost = 25,
                Load = 2,
                ValuesSummary = "射程8米，修复10HP/秒，消耗自身电池；不复活阵亡机（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_repairbeam",
                ModelId = "primitive:capsule",
                ActionId = "HomeValleyWorkOrders.TryCreateRepair — 建筑/机器维修的真实工单已在 ER3-WRK-01 验证；战场内友军实时治疗尚无 Sim 入口",
                VfxId = "vfx_repair_placeholder",
                SfxId = "sfx_repair_beam",
                PreviewId = "preview_comp_repairbeam",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "十字状发射口剪影（无既有 legacy 参照，占位阶段无法体现）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-04",
            },
            [CompCannonId] = new MechanicalContentDef
            {
                Id = CompCannonId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = "铸造重炮",
                Description = "重型弹道武器，冷却长、单发威力大，开火前有可见瞄准线。",
                Source = MechanicalContentSource.FoundryMandatory,
                SourceDetail = "铸造外围步进炮首次击破必产生铸造重炮模块，解析后解锁（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "主组件",
                ScrapCost = 35,
                Load = 4,
                ValuesSummary = "射程20米，基础55伤害/发，3秒冷却，攻击前1秒瞄准线（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_cannon",
                ModelId = "primitive:capsule",
                ActionId = "MechanicalContentCombatFactory.CreateCannonAttack()+ComposeStandalone — 新增 Engine.Fire 组合，已用 execute_code 验证产出 HitEvent；未接 CarrierCompiler 正式战斗链",
                VfxId = "vfx_cannon_muzzle_placeholder",
                SfxId = "sfx_cannon_fire",
                PreviewId = "preview_comp_cannon",
                SaveCompatible = true,
                LockedHintText = "需在铸造前哨外围击破步进炮并带回重炮模块解析后解锁。",
                SilhouetteNote = "粗短炮管+瞄准线剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-05",
            },

            // ═══ 功能组件 ═══
            [FuncDashId] = new MechanicalContentDef
            {
                Id = FuncDashId,
                Category = MechanicalContentCategory.FunctionComponent,
                DisplayName = "冲刺器",
                Description = "短距离快速位移，碰撞会中止冲刺。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "功能组件",
                ScrapCost = 10,
                Load = 1,
                ValuesSummary = "冲刺6米/冷却6秒/耗能15，碰撞中止（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_func_dash",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_drill\"]) — 借用既有 Dash 攻击方式模式，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_dash_trail_placeholder",
                SfxId = "sfx_dash",
                PreviewId = "preview_func_dash",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "尾部拖影剪影（沿用 org_drill 既有 Dash 识别度；org_drill 本身是穿刺攻击而非纯位移，数值口径差异见 DEBT）。",
                LegacyFacadeId = "org_drill",
                DebtId = "DEBT-ER4CONTENT01-15",
            },
            [FuncMarkerId] = new MechanicalContentDef
            {
                Id = FuncMarkerId,
                Category = MechanicalContentCategory.FunctionComponent,
                DisplayName = "静默标记器",
                Description = "标记主目标及附近至多两个敌人，供标记跳转固件识别。",
                Source = MechanicalContentSource.SilentRuinsSalvage,
                SourceDetail = "破碎都市监听节点首次摧毁必产生静默标记器模块，解析后解锁（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "功能组件",
                ScrapCost = 12,
                Load = 1,
                ValuesSummary = "射程12米/冷却8秒/耗能8，标记主目标及6米内至多两个敌人，持续6秒（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_func_marker",
                ModelId = "primitive:capsule",
                ActionId = "MechanicalContentCombatFactory.CreateMarkerAttack()+ComposeStandalone — 新增 Engine.Fire 组合，已用 execute_code 验证产出带 Marked 标签的 HitEvent；目标数量/持续时间状态机未接线",
                VfxId = "vfx_marker_ping_placeholder",
                SfxId = "sfx_scan_marked",
                PreviewId = "preview_func_marker",
                SaveCompatible = true,
                LockedHintText = "需在破碎都市摧毁监听节点并带回标记器模块解析后解锁。",
                SilhouetteNote = "天线状突起剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-06",
            },

            // ═══ 结构 ═══
            [StructCargoId] = new MechanicalContentDef
            {
                Id = StructCargoId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = "货舱",
                Description = "扩充机体货位，最多装配一个。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-001 开局即带。",
                Slot = "结构",
                ScrapCost = 15,
                Load = 2,
                ValuesSummary = "+2货位，最多一个（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_cargo",
                ModelId = "primitive:capsule",
                ActionId = "HomeValleyLayout.MachineCargoSlots / HomeValleyCargo.SlotsForCargo — 已在 ER3-STO-01 用真实出征货位校验验证（ERC-001=4货位含货舱）",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_cargo",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "背部箱体剪影（真实货位机制已生效，几何体表现待美术）。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [StructRelayId] = new MechanicalContentDef
            {
                Id = StructRelayId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = "信号中继",
                Description = "扩大信号有效范围，代价是提高机体带宽需求。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库。",
                Slot = "结构",
                ScrapCost = 12,
                Load = 1,
                ValuesSummary = "信号有效距离+8米，机体带宽需求+1（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_relay",
                ModelId = "primitive:capsule",
                ActionId = "HomeValleyPowerGrid.SignalBandwidth（家园侧带宽机制已实装）；个体机身加成未接线",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_relay",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "顶部天线杆剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-07",
            },
            [StructArmorId] = new MechanicalContentDef
            {
                Id = StructArmorId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = "重甲",
                Description = "提升生命上限，代价是降低移动速度。",
                Source = MechanicalContentSource.FoundryOptionalCache,
                SourceDetail = "铸造外围可选技术缓存，解析后解锁（DEMO-CONTENT-LOCK.md §3）；可选但必须可从正式地图取得并实际装配。",
                Slot = "结构",
                ScrapCost = 20,
                Load = 3,
                ValuesSummary = "HP+50，移动速度-0.5米/秒（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_armor",
                ModelId = "primitive:capsule",
                ActionId = "回厂改造装配 StatModifier 管线未接线",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_armor",
                SaveCompatible = true,
                LockedHintText = "需在铸造前哨外围取得对应技术缓存并解析后解锁。",
                SilhouetteNote = "加厚装甲板剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-08",
            },
            [StructFinId] = new MechanicalContentDef
            {
                Id = StructFinId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = "散热鳍",
                Description = "提升持续散热速率，缓解主武器积热。",
                Source = MechanicalContentSource.FoundryOptionalCache,
                SourceDetail = "铸造外围可选技术缓存，解析后解锁（DEMO-CONTENT-LOCK.md §3）；可选但必须可从正式地图取得并实际装配。",
                Slot = "结构",
                ScrapCost = 15,
                Load = 1,
                ValuesSummary = "散热+5/秒（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_fin",
                ModelId = "primitive:capsule",
                ActionId = "热量 Stat 管线未建立，回厂改造装配未接线",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_fin",
                SaveCompatible = true,
                LockedHintText = "需在铸造前哨外围取得对应技术缓存并解析后解锁。",
                SilhouetteNote = "背部散热片剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-08",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);
    }
}
