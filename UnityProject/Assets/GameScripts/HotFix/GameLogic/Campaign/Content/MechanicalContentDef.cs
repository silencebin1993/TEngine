using System.Collections.Generic;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01：封闭内容目录覆盖的八个类别，与 STORY-EXECUTION-CARDS.md
    /// ER4-CONTENT-01 第1条"3 底盘、4 主组件、2 功能组件、4 结构、6 固件、2 反应、6 敌类、8 建筑"
    /// 一一对应。</summary>
    public enum MechanicalContentCategory
    {
        Chassis,
        MainComponent,
        FunctionComponent,
        Structure,
        Firmware,
        Reaction,
        Enemy,
        Building,
    }

    /// <summary>内容的取得来源（DEMO-CONTENT-LOCK.md §3"来源与初始状态"列）。</summary>
    public enum MechanicalContentSource
    {
        /// <summary>基础蓝图库/开局即有，不需要任何解锁事件。</summary>
        BaseBlueprint,
        /// <summary>破碎都市带回解析后解锁（第 1 次出击）。</summary>
        SilentRuinsSalvage,
        /// <summary>铸造外围主线战利品，解析后解锁（第 2 次出击必得）。</summary>
        FoundryMandatory,
        /// <summary>铸造外围可选技术缓存，解析后解锁（不阻断主线，但必须可从正式地图取得并实际装配）。</summary>
        FoundryOptionalCache,
        /// <summary>反应类内容：非独立拾取，满足已解锁组件组合、保存蓝图时由编译器识别。</summary>
        Composite,
        /// <summary>建筑类内容：归还谷地开局已存在（可能是 Damaged 待修复）。</summary>
        AlwaysBuiltHomeValley,
        /// <summary>建筑类内容：需要满足战役目标后才建造（如导航信标，OBJ-10）。</summary>
        UnlockedLateBuilding,
        /// <summary>敌类内容：出现在具体区域遭遇战中，不是玩家拾取/建造对象。</summary>
        RegionEncounter,
    }

    /// <summary>谁可以真正使用/装配/触发这条内容（对照验收卡"AI 权限"列）。</summary>
    public enum MechanicalContentAiPermission
    {
        /// <summary>玩家机与 AI 队友机均可装配/使用（大多数机械组件）。</summary>
        PlayerAndAllyAi,
        /// <summary>仅玩家可主动触发（如导航信标的 E 启动确认）。</summary>
        PlayerOnly,
        /// <summary>仅敌方 AI 使用（六类敌人）。</summary>
        EnemyAiOnly,
        /// <summary>不可装备的系统/建筑本体或反应判定规则，无 AI 权限概念。</summary>
        NotApplicable,
    }

    /// <summary>ER4-CONTENT-01 封闭内容目录的"一 ID 一行"条目。字段覆盖 STORY-EXECUTION-CARDS.md
    /// 第1条点名的全部维度：内容 ID、DisplayName/说明、来源、槽位、成本、数值、AI 权限、图标、
    /// 模型/材质、动作、VFX/SFX、预览、存档兼容与灰度轮廓。
    ///
    /// 本类只是数据容器，不含任何行为逻辑——真正的战斗/经济/建造行为继续复用既有系统
    /// （<see cref="Regions.HomeValleyWorkOrders"/>、<see cref="Regions.HomeValleyCargo"/>、
    /// <see cref="Regions.HomeValleyPowerGrid"/>、<see cref="MachineRegistry"/>、
    /// <c>SandboxAssembler</c>/<c>OrganelleCatalog</c>/<c>GeneCatalog</c>），或在 <see cref="DebtId"/>
    /// 非空时明确移交给已存在的承接 Story（STORY-BOARD.md 已排期，不是"以后"）。</summary>
    public sealed class MechanicalContentDef
    {
        /// <summary>本 Story 命名空间下的机械内容 ID（如 "chassis_wheel"）。不含 org_/gene_/cell_ 等
        /// 禁用词前缀；玩家不可见，供代码/存档/UI 查表使用。</summary>
        public string Id;

        public MechanicalContentCategory Category;

        /// <summary>玩家可见中文名（图鉴/UI/字幕唯一来源，不得回退成内部 ID 或旧生物词）。</summary>
        public string DisplayName;

        /// <summary>机械说明：图鉴/tooltip 用的一句话机制描述。</summary>
        public string Description;

        public MechanicalContentSource Source;

        /// <summary>来源文案人读版（对照 DEMO-CONTENT-LOCK.md §3 原文，供 UI/图鉴直接引用）。</summary>
        public string SourceDetail;

        /// <summary>槽位（底盘/主组件/功能/结构/固件/反应/敌类/建筑，对照 §2.4）。</summary>
        public string Slot;

        /// <summary>废料成本（DEMO-CONTENT-LOCK.md §2.4）。无成本的类别（反应/敌类/已存在建筑）为 0。</summary>
        public int ScrapCost;

        /// <summary>负载（DEMO-CONTENT-LOCK.md §2.4"槽位：条目：废料成本/负载"列）。无负载概念为 0。</summary>
        public int Load;

        /// <summary>数值摘要：对照 §5/§2.4 原始数字的人读一行版本，供图鉴/证据核对，不是程序判定依据
        /// （程序判定依据仍是各自系统——电力表在 <see cref="Regions.HomeValleyLayout"/>，
        /// 战斗数值在 legacy 复用的 org_/gene_ Luban 表，本类不重复保存一份可能与之漂移的数字源）。</summary>
        public string ValuesSummary;

        public MechanicalContentAiPermission AiPermission;

        /// <summary>占位图标 key。当前无美术绑定，UI 一律退化为文字徽记（DisplayName 首字），
        /// 不产生"图标缺失"的粉色/破图状态——退化路径本身就是稳定失败，不是本 Story 的缺口。</summary>
        public string IconId;

        /// <summary>模型/材质引用。当前一律指向既有占位几何体管线（<see cref="Regions.HomeValleyController"/>
        /// 的 <c>CreatePrimitive</c> + <c>Shader.Find("Standard")</c>，或 <c>SimVisualLibrary.BuildForArtId</c>
        /// 的程序化 Mesh 拼装），这条管线是纯 C# 代码生成，不存在"资源未找到/粉色材质"的失败态——
        /// 字符串本身只是给未来 feature-art-binding 换正式低模时定位用的稳定 key。</summary>
        public string ModelId;

        /// <summary>触发/使用这条内容的真实代码入口（file/method 或系统名），供审计核对
        /// "AI/玩家实际使用"是否有真实落点。</summary>
        public string ActionId;

        public string VfxId;
        public string SfxId;

        /// <summary>图鉴/装配预览用 key，当前退化为 DisplayName + Description 文本预览。</summary>
        public string PreviewId;

        /// <summary>是否已确认与现有存档 DTO 兼容——本内容目录只新增字符串 ID 查表，不改
        /// <see cref="MachineRecord"/>/<see cref="BuildingRecord"/> 字段结构，天然兼容旧档。</summary>
        public bool SaveCompatible;

        /// <summary>未解锁/灰显状态下的玩家可见提示文案（不得为空字符串或占位符 TODO）。</summary>
        public string LockedHintText;

        /// <summary>AC-THEME-002"玩家/静默/铸造三方在灰度和小尺寸下靠轮廓/运动可区分"的落点说明——
        /// 当前占位几何体尚未分方营造型（详见类注释与 DEBT），本字段记录未来美术接手时的辨识设计意图。</summary>
        public string SilhouetteNote;

        /// <summary>旧 org_*/gene_* 等价实现 id；null 表示当前无等价实现（全新机械内容）。
        /// TERM-MIGRATION.md §3："推荐复用 id，只改 DisplayName/描述/标签"——有等价实现时必须填这里，
        /// 不能另起新 id 假装是全新内容。</summary>
        public string LegacyFacadeId;

        /// <summary>非空 = 本内容仍有功能缺口，登记的 DEBT-ID（配套症状/承接故事写在
        /// <c>production/qa/evidence/er4-content-01-mechanical-content-batch-1.md</c>，
        /// 这里只存 ID 供代码/测试引用，不重复整段文案）。</summary>
        public string DebtId;
    }

    /// <summary>供审计/测试遍历用的只读集合别名。</summary>
    public static class MechanicalContentDefExtensions
    {
        public static IEnumerable<MechanicalContentDef> WhereCategory(
            this IReadOnlyDictionary<string, MechanicalContentDef> defs, MechanicalContentCategory category)
        {
            foreach (KeyValuePair<string, MechanicalContentDef> kv in defs)
            {
                if (kv.Value.Category == category)
                {
                    yield return kv.Value;
                }
            }
        }
    }
}
