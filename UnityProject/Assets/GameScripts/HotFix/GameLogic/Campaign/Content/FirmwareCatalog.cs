using System.Collections.Generic;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：6 固件（DEMO-CONTENT-LOCK.md §2.4 固件表 + §3"固件"行）。
    /// 固件最多两个且有序，装在主组件之后——本类只登记"这个固件是什么"，装配顺序/负载上限校验
    /// 属于 ER4-PRIM-02（3×3 电路板与蓝图存档）范畴，不在本 Story 重复实现。</summary>
    public static class FirmwareCatalog
    {
        public const string FwHomingId = "fw_homing";
        public const string FwSplitId = "fw_split";
        public const string FwTrailId = "fw_trail";
        public const string FwOverloadId = "fw_overload";
        public const string FwMarkTagId = "fw_marktag";
        public const string FwArmorPierceId = "fw_armorpierce";

        private static readonly Dictionary<string, MechanicalContentDef> _defs = new Dictionary<string, MechanicalContentDef>
        {
            [FwHomingId] = new MechanicalContentDef
            {
                Id = FwHomingId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "寻的",
                Description = "弹体发射后向当前合法目标修正弹道，最多修正30°。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "固件",
                ScrapCost = 5,
                Load = 1,
                ValuesSummary = "弹体在发射后对当前合法目标最多修正30°，不可穿障或自动换友军目标；无合法目标保持普通弹道（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_homing",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(geneIds:[\"gene_taxis\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证 HomingModule 生效",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_homing",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "无独立几何体，随弹道表现（沿用 gene_taxis 既有 HomingModule 数值 0.85，非精确30°角上限，登记 DEBT）。",
                LegacyFacadeId = "gene_taxis",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [FwSplitId] = new MechanicalContentDef
            {
                Id = FwSplitId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "分裂",
                Description = "弹体首次合法命中后产生两枚次级弹，各为原伤害一半，不再次分裂。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库。",
                Slot = "固件",
                ScrapCost = 8,
                Load = 1,
                ValuesSummary = "首次合法命中后产生两枚各为原伤害50%的次级弹，不再次分裂；按稳定目标ID选敌，不可无限递归（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_split",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(geneIds:[\"gene_split\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证 SplitModule 生效",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_split",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "无独立几何体，随命中特效表现（沿用 gene_split 既有 SplitModule）。",
                LegacyFacadeId = "gene_split",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [FwTrailId] = new MechanicalContentDef
            {
                Id = FwTrailId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "拖尾",
                Description = "弹体路径留下能量尾迹，持续对同一目标造成少量伤害。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库；ERC-003 默认初装。",
                Slot = "固件",
                ScrapCost = 5,
                Load = 1,
                ValuesSummary = "弹体路径留下2秒能量尾迹，同一来源对同一目标每秒最多2伤害，不多帧叠加；离开区域立即解绑表现（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_trail",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(geneIds:[\"gene_slime\"]) — 真实 Engine.Fire 链路，已用 execute_code 验证 TrailModule 生效",
                VfxId = "vfx_trail_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_trail",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "路径拖影表现（沿用 gene_slime 既有 TrailModule，文案含减速副作用与设计原文不完全一致，登记 DEBT）。",
                LegacyFacadeId = "gene_slime",
                DebtId = "DEBT-ER4CONTENT01-03",
            },
            [FwOverloadId] = new MechanicalContentDef
            {
                Id = FwOverloadId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "过载",
                Description = "提升主武器基础伤害，代价是额外热量；与铸造重炮组合触发熔穿过载反应。",
                Source = MechanicalContentSource.BaseBlueprint,
                SourceDetail = "基础蓝图库。",
                Slot = "固件",
                ScrapCost = 10,
                Load = 1,
                ValuesSummary = "一般主武器基础伤害+15%、额外热量+15；与重炮组合时改为熔穿过载：额外穿甲30%、基础热量40上再加25（DEMO-CONTENT-LOCK.md §2.4/§5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_overload",
                ModelId = "primitive:capsule",
                ActionId = "SandboxAssembler.Compose(geneIds:[\"gene_heatshock\"]) — 最接近的既有热量相关基因，已用 execute_code 验证 HeatShockModule 生效；精确+15%伤害/+15热量数值与重炮联动待 ER6-REACT-02",
                VfxId = "vfx_overload_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_overload",
                SaveCompatible = true,
                LockedHintText = "已在基础蓝图库中，无需解锁。",
                SilhouetteNote = "过热红光表现（沿用 gene_heatshock 既有 HeatShockModule，非精确数值对齐，登记 DEBT）。",
                LegacyFacadeId = "gene_heatshock",
                DebtId = "DEBT-ER4CONTENT01-09",
            },
            [FwMarkTagId] = new MechanicalContentDef
            {
                Id = FwMarkTagId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "标记跳转",
                Description = "对已标记目标额外跳跃命中附近已标记敌人，伤害逐跳衰减；来自破碎都市终端。",
                Source = MechanicalContentSource.SilentRuinsSalvage,
                SourceDetail = "破碎都市终端首次读取必产生标记跳转数据盒，解析后解锁（DEMO-CONTENT-LOCK.md §3）。",
                Slot = "固件",
                ScrapCost = 12,
                Load = 1,
                ValuesSummary = "目标已标记时最多额外跳2个8米内已标记目标，每跳伤害为上一跳的60%；按稳定LogicId选目标；无合法目标安全退化为普通命中（DEMO-CONTENT-LOCK.md §5）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_marktag",
                ModelId = "primitive:capsule",
                ActionId = "MechanicalContentCombatFactory.CreateMarkJumpAttack()+ComposeStandalone — 新增 Engine.Fire 组合，已用 execute_code 验证产出带 Chain+Marked 的 HitEvent；标记过滤/60%衰减未接线",
                VfxId = "vfx_chain_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_marktag",
                SaveCompatible = true,
                LockedHintText = "需在破碎都市读取终端并带回标记跳转数据盒解析后解锁。",
                SilhouetteNote = "跳跃弧线表现（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-06",
            },
            [FwArmorPierceId] = new MechanicalContentDef
            {
                Id = FwArmorPierceId,
                Category = MechanicalContentCategory.Firmware,
                DisplayName = "装甲击穿",
                Description = "忽略目标部分护甲减伤，可与熔穿过载叠加提升总穿甲量。",
                Source = MechanicalContentSource.FoundryOptionalCache,
                SourceDetail = "铸造外围可选技术缓存，解析后解锁（DEMO-CONTENT-LOCK.md §3）；可选但必须可从正式地图取得并实际装配。",
                Slot = "固件",
                ScrapCost = 10,
                Load = 1,
                ValuesSummary = "忽略目标护甲减伤20个百分点，不能使护甲变成负值；与熔穿过载叠加时额外穿甲总量上限50个百分点（DEMO-CONTENT-LOCK.md §2.4）。",
                AiPermission = MechanicalContentAiPermission.PlayerAndAllyAi,
                IconId = "icon_fw_armorpierce",
                ModelId = "primitive:capsule",
                ActionId = "目标护甲 Stat/减伤结算管线未建立",
                VfxId = "vfx_none_placeholder",
                SfxId = "sfx_none_placeholder",
                PreviewId = "preview_fw_armorpierce",
                SaveCompatible = true,
                LockedHintText = "需在铸造前哨外围取得对应技术缓存并解析后解锁。",
                SilhouetteNote = "无独立几何体（全新内容，无 legacy 参照）。",
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-10",
            },
        };

        public static IReadOnlyDictionary<string, MechanicalContentDef> All => _defs;

        public static bool TryGet(string id, out MechanicalContentDef def) => _defs.TryGetValue(id, out def);
    }
}
