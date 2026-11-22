using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Localization;

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
        public const string StructStealthCloakId = "struct_stealth_cloak";
        public const string StructArmorSkirtId = "struct_armor_skirt";
        /// <summary>FG11-FAC-01（承接 FG-GAP-150 超频部分）：全开散热鳍冠（散热鳍的超频变体，冲刺—过热—停机）。</summary>
        public const string StructFinCrownId = "struct_fin_crown";

        // ── FG2-FW-02 新建作战组件（设计案 5.6；每种载体至少一个；名称 / 说明 / 数值来自 fg.TbCombatComponent）──
        public const string CompRamId = "comp_ram";
        public const string CompShovelId = "comp_shovel";
        public const string CompDroneBayId = "comp_dronebay";
        public const string CompCoronaId = "comp_corona";
        public const string CompSprayerId = "comp_sprayer";

        // ── FG2-VFX-02 新建作战组件（设计案 5.6 其余 5 个，FG-GAP-051；名表 16 个至此全部可装配）──
        /// <summary>旋刃环（格斗·环绕：一整圈）。</summary>
        public const string CompOrbitId = "comp_orbit";
        /// <summary>哨戒桩（无人机·定点：插在身前不动）。</summary>
        public const string CompSentryId = "comp_sentry";
        /// <summary>震荡脉冲器（布区·脉冲：落在自己脚下，要贴近）。</summary>
        public const string CompPulserId = "comp_pulser";
        /// <summary>拆解钳（格斗·拆解：夹住后持续拆解 = 组件自带回波；击毁多回收废料随 FG8-LOOT-01）。</summary>
        public const string CompClawId = "comp_claw";
        /// <summary>爆炸反应装甲（原尖刺外装；功能组件，格斗·反伤：被动，反伤范围内的攻击者命中时起爆反伤）。内部 ID 不改（FG19 FGR-EMG-231）。</summary>
        public const string FuncSpikesId = "func_spikes";

        /// <summary>EMG1-TXT-01 / DEBT-EMG1TXT01-01（B22）：外观仍是占位的组件，返回图鉴里的占位说明；外观已换正式的返回 null。
        /// 数据驱动：表里有 <c>component.{id}.art_placeholder</c> 文本键 = 占位，正式外观到位时删掉这条文本即可。</summary>
        public static string ArtPlaceholderNote(string id)
        {
            string key = "component." + id + ".art_placeholder";
            return id != null && GameText.Has(key) ? GameText.Get(key) : null;
        }

        /// <summary>FG15-UX-03：Demo 条目的文案走文本键，每次按当前语言重建（静态初始化时文本表可能还没载入）。</summary>
        private static Dictionary<string, MechanicalContentDef> DemoDefs() => new Dictionary<string, MechanicalContentDef>
        {
            // ═══ 主组件 ═══
            [CompGunId] = new MechanicalContentDef
            {
                Id = CompGunId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = GameText.Get("component.demo.rapid_fire"),
                Description = GameText.Get("component.demo.ranged_projectile_weapon_with"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "主组件",
                ScrapCost = 20,
                Load = 2,
                ValuesSummary = GameText.Get("component.demo.range_14_m_8"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_gun",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_emitter\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_projectile_placeholder",
                SfxId = "sfx_weapon_fire",
                PreviewId = "preview_comp_gun",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "枪管前伸剪影（沿用 org_emitter 既有识别度，占位几何体阶段无法体现）。",
                LegacyFacadeId = "org_emitter",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [CompBeamId] = new MechanicalContentDef
            {
                Id = CompBeamId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = GameText.Get("component.demo.cutting_beam"),
                Description = GameText.Get("component.demo.continuous_beam_weapon_that"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；新搬运轮式默认初装。",
                Slot = "主组件",
                ScrapCost = 20,
                Load = 2,
                ValuesSummary = GameText.Get("component.demo.range_8_m_12"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_beam",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_lensbeam\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_beam_placeholder",
                SfxId = "sfx_beam_fire",
                PreviewId = "preview_comp_beam",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "细长束状剪影（沿用 org_lensbeam 既有识别度）。",
                LegacyFacadeId = "org_lensbeam",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [CompRepairBeamId] = new MechanicalContentDef
            {
                Id = CompRepairBeamId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = GameText.Get("component.demo.repair_beam"),
                Description = GameText.Get("component.demo.repairs_nearby_allies_hp"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；维修悬浮默认初装。",
                Slot = "主组件",
                ScrapCost = 25,
                Load = 2,
                ValuesSummary = GameText.Get("component.demo.range_8_m_repairs"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_repairbeam",
                ModelId = "primitive:capsule",
                ActionId = "HomeValleyWorkOrders.TryCreateRepair — 建筑/机器维修的真实工单已在 ER3-WRK-01 验证；战场内友军实时治疗尚无 Sim 入口",
                VfxId = "vfx_repair_placeholder",
                SfxId = "sfx_repair_beam",
                PreviewId = "preview_comp_repairbeam",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "十字状发射口剪影（无既有 legacy 参照，占位阶段无法体现）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-04",
            },
            [CompCannonId] = new MechanicalContentDef
            {
                Id = CompCannonId,
                Category = MechanicalContentCategory.MainComponent,
                DisplayName = GameText.Get("component.demo.foundry_heavy_cannon"),
                Description = GameText.Get("component.demo.heavy_ballistic_weapon_with"),
                Source = MechanicalContentSource.FoundryMandatory,
                SourceDetail = "铸造外围步进炮首次击破必产生铸造重炮模块，解析后解锁（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "主组件",
                ScrapCost = 35,
                Load = 4,
                ValuesSummary = GameText.Get("component.demo.range_20_m_55"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_comp_cannon",
                ModelId = "primitive:capsule",
                ActionId = "MechanicalContentCombatFactory.CreateCannonAttack()+ComposeStandalone — 新增 Engine.Fire 组合，已用 execute_code 验证产出 HitEvent；未接 CarrierCompiler 正式战斗链",
                VfxId = "vfx_cannon_muzzle_placeholder",
                SfxId = "sfx_cannon_fire",
                PreviewId = "preview_comp_cannon",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.destroy_a_strider_in"),
                SilhouetteNote = "粗短炮管+瞄准线剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-05",
            },

            // ═══ 功能组件 ═══
            [FuncDashId] = new MechanicalContentDef
            {
                Id = FuncDashId,
                Category = MechanicalContentCategory.FunctionComponent,
                DisplayName = GameText.Get("component.demo.dash"),
                Description = GameText.Get("component.demo.short_fast_dash_a"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "功能组件",
                ScrapCost = 10,
                Load = 1,
                ValuesSummary = GameText.Get("component.demo.dash_6_m_6"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_func_dash",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(organelleIds:[\"org_drill\"]) — 借用既有 Dash 攻击方式模式，已用 execute_code 验证产出 HitEvent",
                VfxId = "vfx_dash_trail_placeholder",
                SfxId = "sfx_dash",
                PreviewId = "preview_func_dash",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "尾部拖影剪影（沿用 org_drill 既有 Dash 识别度；org_drill 本身是穿刺攻击而非纯位移，数值口径差异见 DEBT）。",
                LegacyFacadeId = "org_drill",
                DebtId = "DEBT-ER4CONTENT01-15",
            },
            [FuncMarkerId] = new MechanicalContentDef
            {
                Id = FuncMarkerId,
                Category = MechanicalContentCategory.FunctionComponent,
                DisplayName = GameText.Get("component.demo.silent_marker"),
                Description = GameText.Get("component.demo.marks_the_main_target"),
                Source = MechanicalContentSource.SilentRuinsSalvage,
                SourceDetail = "破碎都市监听节点首次摧毁必产生静默标记器模块，解析后解锁（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "功能组件",
                ScrapCost = 12,
                Load = 1,
                ValuesSummary = GameText.Get("component.demo.range_12_m_8"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_func_marker",
                ModelId = "primitive:capsule",
                ActionId = "MechanicalContentCombatFactory.CreateMarkerAttack()+ComposeStandalone — 新增 Engine.Fire 组合，已用 execute_code 验证产出带 Marked 标签的 HitEvent；目标数量/持续时间状态机未接线",
                VfxId = "vfx_marker_ping_placeholder",
                SfxId = "sfx_scan_marked",
                PreviewId = "preview_func_marker",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.destroy_a_listening_node"),
                SilhouetteNote = "天线状突起剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-06",
            },

            // ═══ 结构 ═══
            [StructCargoId] = new MechanicalContentDef
            {
                Id = StructCargoId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.cargo_hold"),
                Description = GameText.Get("component.demo.adds_cargo_slots_at"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-001 开局即带。",
                Slot = "结构",
                ScrapCost = 15,
                Load = 2,
                ValuesSummary = GameText.Get("component.demo.2_cargo_slots_at"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_cargo",
                ModelId = "primitive:capsule",
                ActionId = "LootPickCatalog.CapacityOf（底盘 + 货舱结构模块另加 loot.cargo.cap.module）/ LootPickCatalog.SpaceOf — FG8-LOOT-03 货舱、出发检查单、自动拾取同一组函数",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_cargo",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "背部箱体剪影（真实货位机制已生效，几何体表现待美术）。",
                LegacyFacadeId = null,
                DebtId = null,
            },
            [StructRelayId] = new MechanicalContentDef
            {
                Id = StructRelayId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.signal_relay"),
                Description = GameText.Get("component.demo.signal_relay_module_wherever"),
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库。",
                Slot = "结构",
                ScrapCost = 12,
                Load = 1,
                ValuesSummary = GameText.Get("component.demo.mobile_relay_while_alive"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_relay",
                ModelId = "primitive:capsule",
                ActionId = "SignalCoverageService（结构槽 = 信号中继的机器是移动覆盖源，FG1-SIG-07）+ BlueprintCircuitBoard.ComputeBandwidthCost（带宽 +1）",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_relay",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.already_in_the_base"),
                SilhouetteNote = "顶部天线杆剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = null, // DEBT-ER4CONTENT01-07（中继装到机身无个体加成）由 FG1-SIG-07 关闭：中继模块是移动覆盖源。
            },
            [StructArmorId] = new MechanicalContentDef
            {
                Id = StructArmorId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.heavy_armor"),
                Description = GameText.Get("component.demo.raises_max_hp_at"),
                Source = MechanicalContentSource.FoundryOptionalCache,
                SourceDetail = "铸造外围可选技术缓存，解析后解锁（DEMO-CONTENT-LOCK.md §3）；可选但必须可从正式地图取得并实际装配。",
                Slot = "结构",
                ScrapCost = 20,
                Load = 3,
                ValuesSummary = GameText.Get("component.demo.hp_50_movement_speed"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_armor",
                ModelId = "primitive:capsule",
                ActionId = "回厂改造装配 StatModifier 管线未接线",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_armor",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.get_the_matching_tech"),
                SilhouetteNote = "加厚装甲板剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-08",
            },
            [StructFinId] = new MechanicalContentDef
            {
                Id = StructFinId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.cooling_fins"),
                Description = GameText.Get("component.demo.raises_sustained_cooling_to"),
                Source = MechanicalContentSource.FoundryOptionalCache,
                SourceDetail = "铸造外围可选技术缓存，解析后解锁（DEMO-CONTENT-LOCK.md §3）；可选但必须可从正式地图取得并实际装配。",
                Slot = "结构",
                ScrapCost = 15,
                Load = 1,
                // shortcut: 与同表其他数值摘要一样写死中文（倍率 / 物质与 fg.TbChannelDecl 散热鳍声明同数），整表摘要改文本键随图鉴文本统一做（证据文件遗留项）
                ValuesSummary = GameText.Get("component.demo.cooling_5_s_doubled"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_fin",
                ModelId = "primitive:capsule",
                ActionId = "热量 Stat 管线未建立，回厂改造装配未接线",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_fin",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.get_the_matching_tech"),
                SilhouetteNote = "背部散热片剪影（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-08",
            },
            // EMG4-LOOT-01（FGR-FAC-009，承接 FG-GAP-150）：silent 阵营标志组件，重甲的阵营变体（不新增载体类型）；专属行为来自 fg.TbChannelDecl 声明（ComponentCatalog 不写行为）
            [StructStealthCloakId] = new MechanicalContentDef
            {
                Id = StructStealthCloakId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.absorbent_camo_shroud"),
                Description = GameText.Get("component.demo.silent_faction_variant_of"),
                Source = MechanicalContentSource.EliteDrop,
                SourceDetail = "阵营标志组件：精英 / 首领三选一掉落“敌方接口组件”，解析台破解后得到蓝图（GDD §6.5、§9）。",
                Slot = "结构",
                ScrapCost = 24,
                Load = 3,
                ValuesSummary = GameText.Get("component.demo.adds_mass_like_heavy"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_stealth_cloak",
                ModelId = "primitive:capsule",
                ActionId = "CombatSite.SpawnMachine → EmgCatalog.PartFlagBehavior（声明 → 内核行为参数）",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_stealth_cloak",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.an_enemy_interface_component"),
                SilhouetteNote = "保留静默外壳与紫色发光、占机体约 30%、接缝青蓝信号缝合线（美术规范 03 §9；占位低模）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-EMG4LOOT01-01",
                Faction = "silent",
            },
            // EMG4-LOOT-01（FGR-FAC-009，承接 FG-GAP-150）：foundry 阵营标志组件，重甲的阵营变体（不新增载体类型）；专属行为来自 fg.TbChannelDecl 声明（ComponentCatalog 不写行为）
            [StructArmorSkirtId] = new MechanicalContentDef
            {
                Id = StructArmorSkirtId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.demo.armor_skirt"),
                Description = GameText.Get("component.demo.foundry_faction_variant_of"),
                Source = MechanicalContentSource.EliteDrop,
                SourceDetail = "阵营标志组件：精英 / 首领三选一掉落“敌方接口组件”，解析台破解后得到蓝图（GDD §6.5、§9）。",
                Slot = "结构",
                ScrapCost = 24,
                Load = 3,
                ValuesSummary = GameText.Get("component.demo.adds_mass_like_heavy_2"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_armor_skirt",
                ModelId = "primitive:capsule",
                ActionId = "CombatSite.SpawnMachine → EmgCatalog.PartFlagBehavior（声明 → 内核行为参数）",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_armor_skirt",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.an_enemy_interface_component"),
                SilhouetteNote = "保留铸造外壳与橙色发光、占机体约 30%、接缝青蓝信号缝合线（美术规范 03 §9；占位低模）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-EMG4LOOT01-01",
                Faction = "foundry",
            },
            // FG11-FAC-01（FGR-FAC-009，承接 FG-GAP-150 超频部分）：超频阵营标志组件，散热鳍的阵营变体（不新增载体类型）；专属行为来自 fg.TbChannelDecl 声明
            // （chs.moving condition 幅度 = 冲刺速度倍率、参数 2 = 每秒积热；prop.heat sense 幅度 = 停机秒、参数 2 = 停机受伤 +比例），内核与超频猎犬同一套结算。
            [StructFinCrownId] = new MechanicalContentDef
            {
                Id = StructFinCrownId,
                Category = MechanicalContentCategory.Structure,
                DisplayName = GameText.Get("component.struct_fin_crown.name"),
                Description = GameText.Get("component.demo.overclock_faction_variant_of"),
                Source = MechanicalContentSource.EliteDrop,
                SourceDetail = "阵营标志组件：精英 / 首领三选一掉落“敌方接口组件”，解析台破解后得到蓝图（GDD §6.5、§9）；也可以在研发树“超频 · 结构模块蓝图”研究得到。",
                Slot = "结构",
                ScrapCost = 22,
                Load = 2,
                ValuesSummary = GameText.Get("component.demo.dash_1_5_0"),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_struct_fin_crown",
                ModelId = "primitive:capsule",
                ActionId = "CombatSite.SpawnMachine → CombatSite.FlagBehaviorProfile（声明 → 内核超频参数）",
                VfxId = "vfx_none_placeholder",
                SfxId = "",
                PreviewId = "preview_struct_fin_crown",
                SaveCompatible = true,
                LockedHintText = GameText.Get("component.demo.an_enemy_interface_component"),
                SilhouetteNote = "保留超频外壳与红色发光、占机体约 30%、接缝青蓝信号缝合线（美术规范 03 §9；占位低模，DEBT-FG11FAC01-01）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-FG11FAC01-01",
                Faction = "overclock",
            },
        };

        private static Dictionary<string, MechanicalContentDef> _merged;
        private static int _mergedRevision = -1;
        private static GameLanguage _mergedLanguage;
        private static int _mergedTextRevision = -1;
        private static int _buildCount;

        /// <summary>Demo 的 10 条（上面手写）+ FG2-FW-02 起由 fg.TbCombatComponent 生成的新作战组件（有名称键的行）。
        /// 按表版本与语言缓存（O(组件数)，不按帧）。</summary>
        public static IReadOnlyDictionary<string, MechanicalContentDef> All
        {
            get
            {
                EnsureBuilt();
                return _merged;
            }
        }

        /// <summary>目录内容版本（表重载 / 切换语言 / 文本表重载后变化），聚合目录 <see cref="MechanicalContentFacade"/> 据此重建。</summary>
        public static int Revision
        {
            get
            {
                EnsureBuilt();
                return _buildCount;
            }
        }

        public static bool TryGet(string id, out MechanicalContentDef def)
        {
            if (string.IsNullOrEmpty(id))
            {
                def = null;
                return false;
            }
            EnsureBuilt();
            return _merged.TryGetValue(id, out def);
        }

        private static void EnsureBuilt()
        {
            int rev = CarrierReadings.Revision;
            GameLanguage lang = GameText.Language;
            int textRev = GameText.Revision;
            if (_merged != null && _mergedRevision == rev && _mergedLanguage == lang && _mergedTextRevision == textRev)
            {
                return;
            }
            var merged = new Dictionary<string, MechanicalContentDef>(DemoDefs(), StringComparer.Ordinal);
            foreach (GameConfig.fg.CombatComponent row in CarrierReadings.Components)
            {
                if (row == null || string.IsNullOrEmpty(row.NameKey) || row.NameKey == "none" || merged.ContainsKey(row.Id))
                {
                    continue;
                }
                merged[row.Id] = Build(row);
            }
            _merged = merged;
            _mergedRevision = rev;
            _mergedLanguage = lang;
            _mergedTextRevision = textRev;
            _buildCount++;
        }

        private static MechanicalContentDef Build(GameConfig.fg.CombatComponent row)
        {
            string carrier = CarrierReadings.TryParseCarrier(row.Carrier, out Signal.FirmwareCarrier c) ? CarrierReadings.CarrierName(c) : row.Carrier;
            string subtype = CarrierReadings.SubtypeName(row.Id) ?? carrier;
            bool main = row.Slot == "main";
            bool enemy = row.Source == "enemy"; // EMG4-LOOT-01：阵营标志组件（敌方接口组件，精英 / 首领掉落 → 解析台破解后才能写进蓝图）
            return new MechanicalContentDef
            {
                Id = row.Id,
                Category = main ? MechanicalContentCategory.MainComponent : MechanicalContentCategory.FunctionComponent,
                DisplayName = GameText.Get(row.NameKey),
                Description = GameText.Get(row.DescKey),
                Source = enemy ? MechanicalContentSource.EliteDrop : MechanicalContentSource.BaseBlueprint,
                SourceDetail = GameText.Get(enemy ? "component.source.enemy" : "component.source.base"),
                Faction = enemy && row.Faction != "none" ? row.Faction : null,
                Slot = main ? "主组件" : "功能组件", // 槽位识别串（与 Demo 条目同一取值，代码按它分组；界面显示走载体名） // loc-ignore: 开发者自检 / 诊断文本
                ScrapCost = Math.Max(0, row.Scrap),
                Load = Math.Max(1, row.Load),
                ValuesSummary = GameText.Format("component.values_summary", subtype, row.Damage.ToString("0.#", CultureInfo.InvariantCulture),
                    row.Scrap.ToString(CultureInfo.InvariantCulture), row.Load.ToString(CultureInfo.InvariantCulture)),
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = row.Icon,
                ModelId = "primitive:capsule",
                ActionId = $"CombatSite.MachineWeaponFrom → CarrierReadings.Build（载体 {row.Carrier}，内核 CombatLogic.DeliverInstant）",
                VfxId = "vfx_projectile_placeholder",
                SfxId = "sfx_weapon_fire",
                PreviewId = "preview_" + row.Id,
                SaveCompatible = true,
                LockedHintText = GameText.Get(enemy ? "component.source.enemy" : "component.source.base"),
                SilhouetteNote = $"载体 {row.Carrier}；三套机身状态由 FG2-VFX-02 交付（MachineMorphLibrary，占位低模 → DEBT-FG2VFX02-01）。",
                LegacyFacadeId = row.LegacyOrgan == "none" ? null : row.LegacyOrgan,
                DebtId = "DEBT-FG2VFX02-01",
            };
        }
    }
}
