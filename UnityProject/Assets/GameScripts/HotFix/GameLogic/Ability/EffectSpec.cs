using BinGames.Sim;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability
{
    /// <summary>
    /// 效果类别。新增一类 = 加一个枚举值 + 一个 IEffectExecutor 实现 + 注册一行。
    /// 现有执行器不需要改动 —— 这是"不频繁改老代码"的主要落点。
    /// </summary>
    public enum EffectKind
    {
        None = 0,
        /// <summary>造成伤害（单体/范围/连锁）。</summary>
        Damage = 1,
        /// <summary>施加或移除状态。</summary>
        Status = 2,
        /// <summary>生成单位（孢子、分身、卵鞘、菌毯锚点）。</summary>
        Spawn = 3,
        /// <summary>属性修正（永久或限时）。</summary>
        Stat = 4,
        /// <summary>资源变化（回血、给营养质、加污染度）。</summary>
        Resource = 5,
        /// <summary>位移（冲刺、突进、折返）。</summary>
        Dash = 6,
        /// <summary>发射投射物。</summary>
        Projectile = 7,
        /// <summary>放置持续区域（酸雾、菌毯、导电区）。</summary>
        Area = 8,
        /// <summary>改规则（吞噬门槛、尸体可食、技能槽位）。</summary>
        Rule = 9,
    }

    /// <summary>作用形状。</summary>
    public enum EffectShape
    {
        /// <summary>作用于自身。</summary>
        Self = 0,
        /// <summary>以玩家为中心的圆。</summary>
        Circle = 1,
        /// <summary>朝向鼠标/移动方向的锥形。</summary>
        Cone = 2,
        /// <summary>朝向的直线。</summary>
        Line = 3,
        /// <summary>单个目标（最近或已标记）。</summary>
        Target = 4,
        /// <summary>指定坐标点。</summary>
        Point = 5,
    }

    /// <summary>
    /// 词缀。作为执行器的装饰器，改写执行细节而不改执行器本体。
    /// 32 个词缀 × 9 类效果 = 288 种组合，全部由数据表达（框架文档 §5.4）。
    /// </summary>
    public enum AffixKind
    {
        None = 0,
        /// <summary>增殖：生成数量翻倍。</summary>
        Proliferate = 1,
        /// <summary>导电：命中后附加导电状态。</summary>
        Conductive = 2,
        /// <summary>腐蚀：命中后附加腐蚀。</summary>
        Corrosive = 3,
        /// <summary>回流：命中返还体力/冷却。</summary>
        Refund = 4,
        /// <summary>裂变：结束时生成次级效果。</summary>
        Fission = 5,
        /// <summary>拟态：额外触发一次弱版本。</summary>
        Mimic = 6,
        /// <summary>饥饿：低资源时效果增强。</summary>
        Hunger = 7,
        /// <summary>稳态：静止时效果增强。</summary>
        Homeostasis = 8,
        /// <summary>过载：效果增强但自损。</summary>
        Overload = 9,
        /// <summary>共生：附属体越多越强。</summary>
        Symbiosis = 10,
        /// <summary>贯穿：穿透目标。</summary>
        Pierce = 11,
        /// <summary>弹射：命中后跳向下一目标。</summary>
        Ricochet = 12,
        /// <summary>滞留：留下持续区域。</summary>
        Lingering = 13,
        /// <summary>引力：拉拽附近目标。</summary>
        Gravity = 14,
        /// <summary>恐惧：逼退小型敌人。</summary>
        Fear = 15,
        /// <summary>标记：施加易伤标记。</summary>
        Mark = 16,
        /// <summary>寄生：附着并持续削弱。</summary>
        Parasite = 17,
        /// <summary>虹吸：转化敌方资源。</summary>
        Siphon = 18,
        /// <summary>硬化：提高受击阈值。</summary>
        Harden = 19,
        /// <summary>蜕变：受击时转换形态。</summary>
        Molt = 20,
        /// <summary>连锁：触发次数递增。</summary>
        Chain = 21,
        /// <summary>共振：多目标互相加成。</summary>
        Resonate = 22,
        /// <summary>淤积：效果延迟但加倍。</summary>
        Delayed = 23,
        /// <summary>催化：缩短其他冷却。</summary>
        Catalyze = 24,
        /// <summary>分食：大目标死亡裂成小块。</summary>
        Split = 25,
        /// <summary>孵化：延迟生成友方单位。</summary>
        Hatch = 26,
        /// <summary>晶化：累积破碎层数。</summary>
        Crystallize = 27,
        /// <summary>潮汐：周期性强弱交替。</summary>
        Tidal = 28,
        /// <summary>噬骨：对高体积目标增伤。</summary>
        BoneEater = 29,
        /// <summary>游离：效果可脱离本体存在。</summary>
        Detached = 30,
        /// <summary>逆熵：消耗污染度换取回复。</summary>
        Entropy = 31,
        /// <summary>献祭：消耗自身资源大幅强化。</summary>
        Sacrifice = 32,
    }

    /// <summary>
    /// 效果数据定义。纯数据，无逻辑——逻辑在 IEffectExecutor。
    ///
    /// 参数用命名字段而不是 float[]，因为配表可读性远比"通用"重要：
    /// 配表的人要看得懂自己在填什么。
    /// </summary>
    public sealed class EffectSpec
    {
        public EffectKind Kind;
        public EffectShape Shape = EffectShape.Self;

        /// <summary>主数值：伤害量 / 回复量 / 位移距离 / 生成数量。</summary>
        public float Value;
        /// <summary>作用半径（Circle/Cone）或长度（Line）。</summary>
        public float Radius;
        /// <summary>持续时间。0 表示瞬时。</summary>
        public float Duration;
        /// <summary>整数参数：生成数量 / 连锁次数 / 投射物数 / 规则 id。</summary>
        public int Count;

        /// <summary>Status 类用：要施加的状态。</summary>
        public SimStatus Status = SimStatus.None;
        /// <summary>只对带此状态的目标生效。</summary>
        public SimStatus RequireStatus = SimStatus.None;
        /// <summary>作用阵营。</summary>
        public SimFaction TargetFaction = SimFaction.Hostile;

        /// <summary>Stat 类用。</summary>
        public StatId Stat = StatId.None;
        public ModifierOp Op = ModifierOp.Flat;

        /// <summary>Resource 类用。</summary>
        public Core.ResourceKind Resource = Core.ResourceKind.None;

        /// <summary>Spawn 类用：要生成的敌人/附属体配置 id。</summary>
        public int SpawnEnemyId;

        /// <summary>Rule 类用：规则开关 id。</summary>
        public RuleFlag Rule = RuleFlag.None;

        /// <summary>词缀列表。按顺序包装执行器。</summary>
        public AffixKind[] Affixes;

        /// <summary>是否随属性缩放（AbilityPower / AreaScale）。</summary>
        public bool ScaleWithPower = true;
    }

    /// <summary>
    /// 规则开关。改变玩法规则而非数值——这是"肉鸽来自机制组合"的实现手段。
    /// 新增规则 = 加一个枚举值 + 在消费点判一次。
    /// </summary>
    public enum RuleFlag
    {
        None = 0,
        /// <summary>尸体可二次吞噬。</summary>
        CorpseEdible = 1,
        /// <summary>吞噬失败也造成腐蚀。</summary>
        FailedDevourCorrodes = 2,
        /// <summary>连吃层数不因断连清空。</summary>
        ComboNeverResets = 3,
        /// <summary>冲刺可穿过敌人。</summary>
        DashPierces = 4,
        /// <summary>冲刺路径留下电流。</summary>
        DashLeavesCurrent = 5,
        /// <summary>放电可沿尸体跳跃。</summary>
        CorpseConducts = 6,
        /// <summary>菌毯上吞噬收益提高。</summary>
        MyceliumBoostsDevour = 7,
        /// <summary>濒死自动脱战。</summary>
        AutoEscapeOnLowHp = 8,
        /// <summary>污染度满时进入霸主形态而非失败。</summary>
        PollutionBecomesOverlord = 9,
        /// <summary>附属体优先攻击标记目标。</summary>
        MinionsFocusMarked = 10,
        /// <summary>大型目标死亡裂成小食物块。</summary>
        LargeTargetsSplit = 11,
        /// <summary>处决后同类敌人恐惧。</summary>
        ExecuteCausesFear = 12,
    }
}
