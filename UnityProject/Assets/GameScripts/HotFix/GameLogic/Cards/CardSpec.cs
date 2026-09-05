using System.Collections.Generic;
using GameLogic.Ability;
using GameLogic.Stats;

namespace GameLogic.Cards
{
    /// <summary>六条路线。对应 Cell_Stage_Spec.md §7。</summary>
    public enum CardRoute
    {
        None = 0,
        /// <summary>吞噬扩张。</summary>
        Devour = 1,
        /// <summary>机动猎食。</summary>
        Agile = 2,
        /// <summary>电化统治。</summary>
        Electric = 3,
        /// <summary>孢子繁殖。</summary>
        Spore = 4,
        /// <summary>菌毯筑巢。</summary>
        Nest = 5,
        /// <summary>异化污染。</summary>
        Corrupt = 6,
        /// <summary>跨路线联动。</summary>
        Hybrid = 7,
    }

    /// <summary>稀有度。对应 Cell_Stage_Spec.md §8.1。</summary>
    public enum CardRarity
    {
        /// <summary>普通：效果清晰，直接改变一次行动后果。</summary>
        Common = 0,
        /// <summary>稀有：至少与两类系统产生组合。</summary>
        Rare = 1,
        /// <summary>史诗：改变一条路线的运作方式。</summary>
        Epic = 2,
        /// <summary>异化：强机制变化 + 明确副作用。</summary>
        Aberrant = 3,
        /// <summary>原核遗产：定义整局策略，45 分钟后出现。</summary>
        Legacy = 4,
    }

    /// <summary>
    /// 卡对应的具体内容种类（story-005 代谢化迁移）。None = 旧战斗卡/未归类内容占位；
    /// Organelle/Gene 时 ContentId 查 MetabolicSlice.ContentCatalog 的 OrganelleCatalog/GeneCatalog。
    /// </summary>
    public enum ContentKind
    {
        None = 0,
        Organelle = 1,
        Gene = 2,
        /// <summary>structural-organ-draft-integration story-001：结构器官壳卡，ContentId 查 OrganelleCatalog（Category=Structural）。</summary>
        Structural = 3,
    }

    /// <summary>
    /// 卡牌触发时机。对应 Core/GameSignals.cs 里的信号。
    /// 新增触发时机 = 加一个枚举值 + CardTriggerBus 订阅对应信号。
    /// </summary>
    public enum CardTrigger
    {
        /// <summary>获得即生效（属性卡、规则卡）。</summary>
        Passive = 0,
        OnDevour = 1,
        OnKill = 2,
        OnHit = 3,
        OnHurt = 4,
        OnDash = 5,
        OnAbilityCast = 6,
        OnLevelUp = 7,
        OnLowHealth = 8,
        OnPhaseStart = 9,
        /// <summary>周期性。用 TickSignal，间隔由 TriggerInterval 控制。</summary>
        OnTick = 10,
        OnVolumeChanged = 11,
        OnEcoEvent = 12,
    }

    /// <summary>
    /// 卡牌定义。纯数据。
    ///
    /// "内容巨大"靠这个结构成立：一张卡 = 触发器 + 效果列表 + 属性修正 + 词缀，
    /// 全部数据化，所以新增 135 张卡是**配表工作**而不是编码工作。
    /// 详见 Cell_Stage_Spec.md §8.2 与 Game_Framework_Design.md §5.5。
    /// </summary>
    public sealed class CardSpec
    {
        public int Id;
        public string Name;
        /// <summary>一句话主效果。UI 要求 3 秒内可读完。</summary>
        public string Desc;
        /// <summary>折叠细节。长效果放这里，不占主描述。</summary>
        public string DetailDesc;

        public CardRoute Route = CardRoute.None;
        public CardRarity Rarity = CardRarity.Common;

        /// <summary>代谢化迁移（story-005）：本卡对应的具体内容种类，None=旧战斗卡/未归类。</summary>
        public ContentKind ContentKind = ContentKind.None;
        /// <summary>ContentKind!=None 时，OrganelleCatalog/GeneCatalog 的字符串 Id。</summary>
        public string ContentId;

        /// <summary>解锁所需生态时期序号（0 起）。</summary>
        public int UnlockPhase;
        /// <summary>可叠加层数上限。</summary>
        public int MaxStack = 1;

        public CardTrigger Trigger = CardTrigger.Passive;
        /// <summary>OnTick 用的触发间隔（秒）。</summary>
        public float TriggerInterval = 1f;
        /// <summary>触发概率 0-1。1 表示必定触发。</summary>
        public float TriggerChance = 1f;
        /// <summary>触发内置冷却（秒），防止高频信号导致效果刷屏。</summary>
        public float TriggerCooldown;

        /// <summary>触发时执行的效果。</summary>
        public List<EffectSpec> Effects = new List<EffectSpec>(2);
        /// <summary>获得即应用的属性修正（与 Trigger 无关）。</summary>
        public List<StatModifier> StatMods = new List<StatModifier>(2);
        /// <summary>授予的主动技能 id。0 表示不授予。</summary>
        public int GrantAbilityId;
        /// <summary>开启的规则开关。</summary>
        public RuleFlag[] RuleFlags;

        /// <summary>联动标签。用于抽卡权重与"推荐联动"高亮。</summary>
        public string[] SynergyTags;

        /// <summary>污染度代价。取卡时立即增加。</summary>
        public float PollutionCost;
        /// <summary>副作用描述。异化卡必填。</summary>
        public string DrawbackDesc;
        /// <summary>副作用的属性修正（负面）。</summary>
        public List<StatModifier> DrawbackMods;

        /// <summary>
        /// 是否是纯数值卡。设计约束：占比不超过 15%（Spec §16 风险项）。
        /// 由内容校验工具统计，不参与运行时逻辑。
        /// </summary>
        public bool IsPureStatCard;

        public bool HasSynergyTag(string tag)
        {
            if (SynergyTags == null || string.IsNullOrEmpty(tag))
            {
                return false;
            }
            for (int i = 0; i < SynergyTags.Length; i++)
            {
                if (SynergyTags[i] == tag)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>联动标签常量。抽卡权重与 UI 高亮都读它。</summary>
    public static class SynergyTag
    {
        public const string Devour = "devour";
        public const string Corpse = "corpse";
        public const string Combo = "combo";
        public const string Dash = "dash";
        public const string Mark = "mark";
        public const string Electric = "electric";
        public const string Chain = "chain";
        public const string Conductive = "conductive";
        public const string Minion = "minion";
        public const string Infect = "infect";
        public const string Mycelium = "mycelium";
        public const string Territory = "territory";
        public const string Pollution = "pollution";
        public const string Risk = "risk";
        public const string Survival = "survival";
        public const string Burst = "burst";
        public const string Volume = "volume";
        public const string Execute = "execute";
        public const string Area = "area";
        public const string Projectile = "projectile";
    }
}
