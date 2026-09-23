using System.Linq;
using GameLogic.Campaign.Content;
using GameLogic.MetabolicSlice.CardDefs;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.Campaign.Blueprint
{
    /// <summary>ER4-PRIM-02：电路板"芯片"内容来源。复用既有
    /// <see cref="GameLogic.MetabolicSlice.CardDefs.CardCatalog"/>（已从
    /// <see cref="GameLogic.MetabolicSlice.ContentCatalog.OrganelleCatalog"/> 全量扩表，带
    /// IsSource/IsSink/AllowedSlotTypes 现成语义），不新建平行内容 ID 系统——PRIMITIVE-FULL-DEMO-SPEC.md
    /// §4.1 明确"CardCatalog 的 organ_focus、organ_focus_plus 是本 Demo 两镜的现成效果线索"。
    ///
    /// ER4-PRIM-03 落地正式基元仓（<c>PrimitiveInventory</c>）后，"能装哪些芯片"的校验规则应原样保留，
    /// 只是"实例来源"从这里的静态目录查找换成仓内实例查找——本 Story 用最小自包含草稿态（草稿槽直接
    /// 存内容 ID，不建实例/不占用/不消耗任何仓库容量），登记 <c>DEBT-ER4PRIM02-01</c> 移交。</summary>
    public static class BlueprintCircuitChipCatalog
    {
        /// <summary>0 号源槽默认内容——"能源芯"占位卡，<c>CreateModule</c> 产出
        /// <c>ComposeEngine.Builtin.Modules.EnergyCore()</c>（默认 BaseOutput=10f）。</summary>
        public const string DefaultSourceContentId = "organ_core";

        /// <summary>校验某内容 ID 是否是合法的 0 号源槽内容（<c>CardDef.IsSource==true</c>）。
        /// <c>CardCatalog</c> 当前有三条满足：占位 "organ_core"，以及从
        /// <see cref="OrganelleCatalog"/> 自动扩表的 Source 角色器官 "org_mito"/"org_chloro"
        /// （DEBT-ER4PRIM01-01 点名的两条——旧面板可选但战斗编译链从未真正读取，本 Story 起，这里的
        /// 选择会真实参与 <see cref="BlueprintCircuitBoard.ComputeSignature"/> 与
        /// <see cref="BlueprintCircuitCompiler"/> 通用预览的链头模块构造）。</summary>
        public static bool IsValidSourceContent(string contentId)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return false;
            }
            CardDef def = CardCatalog.Get(contentId);
            return def != null && def.IsSource;
        }

        /// <summary>七个自由槽（1～7）的芯片合法性：已知内容、非 Source/非 Sink（源汇固定不可拆，
        /// 不能被当成普通芯片二次装配）、当前槽类型在其 <c>AllowedSlotTypes</c> 内，且必须有真实机械
        /// 展示名（<see cref="HasMechanicalDisplayName"/>）——PRIMITIVE-FULL-DEMO-SPEC.md §4.1 明确
        /// "不得把未包装的旧生物卡直接塞给玩家充数"：<c>CardCatalog</c> 从 <c>OrganelleCatalog.All</c>
        /// 全量扩表带来 78 条 org_* 条目，其中只有 24 条在 <c>StageSkinCatalog</c> 有 Mech 层展示名，
        /// 其余 54 条（DEBT-ER2THEME01-01 点名）若被放进这块新 UI 的槽位会直接把生物学原名（如"细胞核"）
        /// 显示给玩家——本方法把"允许装入"与"有安全展示名"绑定，从入口拒绝而不是留给显示层兜底。</summary>
        public static bool IsValidChipContent(string contentId, SlotType slotType)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return false;
            }
            CardDef def = CardCatalog.Get(contentId);
            if (def == null || def.IsSource || def.IsSink)
            {
                return false;
            }
            if (!def.AllowedSlotTypes.Contains(slotType))
            {
                return false;
            }
            return HasMechanicalDisplayName(contentId);
        }

        /// <summary>某内容 ID 是否有一个非生物学原名的展示名来源：①<c>MechanicalContentFacade</c>
        /// 反查命中（ER4-CONTENT-01 正式机械内容）；②<c>StageSkinCatalog</c> Mech 层收录（如
        /// org_mito→"核聚变堆"、org_chloro→"光能采集板"，story-005 既有权威映射）；③手写
        /// <c>CardCatalog</c> 条目本身（"organ_"前缀，如 organ_focus="聚焦镜"、organ_core="能源芯"——
        /// 这些不是从 <see cref="OrganelleCatalog"/> 自动扩表来的，DisplayName 本来就是机械化文案）。
        /// 自动扩表的 "org_" 前缀条目（无上述①②命中）视为不安全，见 <see cref="DisplayNameFor"/> 同一判据。</summary>
        private static bool HasMechanicalDisplayName(string contentId)
        {
            if (MechanicalContentFacade.All.Values.Any(d => d.LegacyFacadeId == contentId))
            {
                return true;
            }
            if (StageSkinCatalog.GetDisplayName(contentId, SkinTier.Mech) != null)
            {
                return true;
            }
            return !contentId.StartsWith("org_");
        }

        /// <summary>8 号汇槽的内容 ID 由外层 <c>BlueprintVersionRecord.PrimaryId</c> 派生，不是玩家可选
        /// 字段——与 <c>CarrierCompiler.BuildRecipe</c> 挑选链尾的规则保持一致（<c>AttackMethod==true</c>
        /// 才能作链尾，不是旧 <c>CardDef.IsSink</c>/<c>OrganelleRole.Sink</c> 那个不相关的旧概念，
        /// 旧概念目前只有占位卡 "organ_actuator" 一条，从未被 <c>CarrierCompiler</c> 使用过）。
        /// 找不到合法映射（如维修束 comp_repairbeam 没有 LegacyFacadeId，或该主组件压根不是攻击类）时
        /// 返回 null——这是合法状态，不是错误：非战斗底盘（搬运/维修）本就没有可攻击的汇。</summary>
        public static string ResolveSinkContentId(string primaryId)
        {
            if (string.IsNullOrEmpty(primaryId))
            {
                return null;
            }
            if (!MechanicalContentFacade.TryGet(primaryId, out MechanicalContentDef def) || def.LegacyFacadeId == null)
            {
                return null;
            }
            OrganelleDef organelle = OrganelleCatalog.Get(def.LegacyFacadeId);
            return organelle != null && organelle.AttackMethod ? def.LegacyFacadeId : null;
        }

        public static string DisplayNameFor(string contentId)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return null;
            }

            // ER2-THEME-01 硬约束："玩家可见只硬表面机械；禁用生物词"。CardCatalog.DisplayName 对
            // "org_"前缀（OrganelleCatalog 自动扩表）条目直接来自旧细胞阶段展示名（如
            // org_mito="线粒体"/org_chloro="叶绿体"），本身就是被 DEBT-ER2THEME01-01 点名的旧词来源
            // 之一——2026-09-22 中断续接时经 execute_code 反射实测确认：0 号源槽选 org_mito/org_chloro
            // 时（DEBT-ER4PRIM01-01 裁决允许的合法选项）会把生物学原名直接渲染进本面板 SourceLabel/
            // 槽位按钮文案，是真实违规，不是假设。修复优先级与 <see cref="IsValidChipContent"/> 的
            // <see cref="HasMechanicalDisplayName"/> 判据保持一致：①<c>MechanicalContentFacade</c>
            // 反查（如 comp_gun="连射器"）；②<c>StageSkinCatalog</c> Mech 层（如
            // org_mito→"核聚变堆"、org_chloro→"光能采集板"，story-005 既有权威映射，此前从未在
            // 本模块接入）；③非"org_"前缀的手写 <c>CardCatalog</c> 条目原名（organ_focus="聚焦镜"等，
            // 本来就是机械化文案）。三者都未命中时（54 条未覆盖的旧 org_* 条目）说明
            // <see cref="IsValidChipContent"/> 本不应放行——这里返回 contentId 兜底只是防御性收尾，
            // 正常路径不会走到。
            MechanicalContentDef facadeDef = MechanicalContentFacade.All.Values
                .FirstOrDefault(d => d.LegacyFacadeId == contentId);
            if (facadeDef != null)
            {
                return facadeDef.DisplayName;
            }

            string mechSkinName = StageSkinCatalog.GetDisplayName(contentId, SkinTier.Mech);
            if (!string.IsNullOrEmpty(mechSkinName))
            {
                return mechSkinName;
            }

            if (!contentId.StartsWith("org_"))
            {
                CardDef def = CardCatalog.Get(contentId);
                if (def != null)
                {
                    return def.DisplayName;
                }
            }

            return contentId;
        }
    }
}
