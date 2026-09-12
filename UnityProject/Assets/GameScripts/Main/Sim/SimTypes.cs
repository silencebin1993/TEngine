using System;
using Unity.Mathematics;

namespace BinGames.Sim
{
    /// <summary>
    /// 模拟世界中的稳定实体身份。值只由 <see cref="SimWorld"/> 分配；0 永远表示无实体。
    /// 身份不编码数组槽位，因此槽位释放、复用或内部重排都不会让旧身份指向另一单位。
    /// </summary>
    public readonly struct SimEntityId : IEquatable<SimEntityId>
    {
        public static readonly SimEntityId None = default;

        public readonly ulong Value;

        public SimEntityId(ulong value)
        {
            Value = value;
        }

        public bool IsValid => Value != 0UL;

        public bool Equals(SimEntityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SimEntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => IsValid ? Value.ToString() : "None";

        public static bool operator ==(SimEntityId left, SimEntityId right) => left.Equals(right);
        public static bool operator !=(SimEntityId left, SimEntityId right) => !left.Equals(right);
    }

    /// <summary>单位当前消费哪一类意图。控制身份与阵营彼此独立。</summary>
    public enum IntentSource : byte
    {
        AI = 0,
        Player = 1,
        /// <summary>生成时指定的确定性脚本行为（战役编排用）。不是 RTS 命令。</summary>
        Scripted = 2,
        /// <summary>
        /// 受 RTS 命令驱动（M2-02）。
        ///
        /// **刻意不复用 <see cref="Player"/>**：M1-06 建立的核心不变量是
        /// 「存活单位里 IntentSource == Player 的恰好一个，或明确为无」。
        /// 编队命令一次能驱动几十个单位，复用 Player 会当场打破那条不变量，
        /// 让"谁是受控实体"重新变得说不清——那正是整个 M1 在修的问题。
        ///
        /// 也不复用 <see cref="Scripted"/>：那是"生成时就定好的脚本单位"，
        /// 与"玩家临时下达、完成后要交还 AI"的命令生命周期完全不同。
        /// </summary>
        Commanded = 3,
    }

    /// <summary>RTS 基础命令类型（M2-02）。非目标：不做复杂阵型与共享寻路。</summary>
    public enum UnitCommandKind : byte
    {
        None = 0,
        /// <summary>移动到点。到达后交还 AI。</summary>
        Move = 1,
        /// <summary>攻击指定实体。目标死亡或消失后交还 AI。</summary>
        Attack = 2,
        /// <summary>守备一点：留在该点附近，不主动追击远处目标。</summary>
        Guard = 3,
        /// <summary>撤退到点：优先远离最近的敌人，同时向目标点靠拢。</summary>
        Retreat = 4,
    }

    /// <summary>
    /// 一条下达给单位的命令（M2-02）。
    ///
    /// 命令是**持久状态**，不是一次性意图——<see cref="SimWorld"/> 每帧都会把全体
    /// <see cref="UnitIntent"/> 重置成 Idle，所以"下令时写一次意图"下一帧就没了。
    /// 因此命令存在内核的逐槽位数组里，每帧由 <c>JobCommandIntent</c> 重新编译成意图。
    /// </summary>
    public struct UnitCommand
    {
        public UnitCommandKind Kind;
        public float2 TargetPosition;
        /// <summary>攻击目标的稳定身份。仅 <see cref="UnitCommandKind.Attack"/> 使用。</summary>
        public SimEntityId TargetEntity;
        /// <summary>到达判定半径。Move 用它判完成，Guard 用它当留守范围。</summary>
        public float ArriveRadius;

        public static UnitCommand None => default;
    }

    /// <summary>框选查询返回的单位条目（M2-02）。不向热更层暴露任何原生容器。</summary>
    public struct SimUnitPick
    {
        public SimEntityId EntityId;
        public int UnitIndex;
        public float2 Position;
        public SimFaction Faction;
        public IntentSource IntentSource;
    }

    /// <summary>控制权切换的确定性结果。失败不会改变当前控制实体。</summary>
    public enum ControlSwitchResult : byte
    {
        Success = 0,
        AlreadyControlled = 1,
        WorldNotInitialized = 2,
        InvalidTarget = 3,
        TargetNotFound = 4,
        TargetDead = 5,
        TargetNotFriendly = 6,
    }

    /// <summary>
    /// 热更桥接层控制请求的稳定结果码。基础身份校验由模拟世界负责；临时信号范围与冷却
    /// 属于桥接策略，因此只在请求入口补充对应失败原因。
    /// </summary>
    public enum ControlRequestResult : byte
    {
        Success = 0,
        AlreadyControlled = 1,
        SimulationNotRunning = 2,
        InvalidTarget = 3,
        TargetNotFound = 4,
        TargetDead = 5,
        TargetNotFriendly = 6,
        OutOfSignalRange = 7,
        CooldownActive = 8,
        CurrentUnitUnavailable = 9,
    }

    /// <summary>
    /// 控制权为什么变了。热更层据此区分"玩家按键换人"和"死亡后意识自动回弹"，
    /// 两者的提示文案、镜头行为与音效都不一样，不能混成一条事件。
    /// </summary>
    public enum ControlChangeReason : byte
    {
        None = 0,
        /// <summary>玩家显式请求（M1-04 的 RequestControlSwitch 路径）。</summary>
        PlayerRequest = 1,
        /// <summary>受控实体死亡触发的自动回弹。Current 可能为 None（无可回弹目标）。</summary>
        ControlledDeath = 2,
        /// <summary>受控实体被卸载 / 主动移除（非战斗死亡）。</summary>
        ControlledRemoved = 3,
        /// <summary>读档或重进场景后的控制权恢复。</summary>
        Restored = 4,
    }

    /// <summary>
    /// 内核产生的一次控制权变更记录。
    ///
    /// 内核不能直接发热更层事件（AOT 不认识 <c>Signals</c>），所以把变更挂在快照上，
    /// 由 <c>SimBridge</c> 每帧消费一次再转成信号。这样"死亡回弹"也只会发布一次事件，
    /// 与 M1-04「合法切换只产生一次事件」的验收保持同一条路径。
    /// </summary>
    public struct ControlChangeEvent
    {
        public SimEntityId PreviousUnitId;
        public SimEntityId CurrentUnitId;
        public ControlChangeReason Reason;
        /// <summary>变更瞬间的战略回退锚点——旧受控实体最后的有效位置。
        /// 回弹失败（Current = None）时，表现层靠它保持视角不丢。</summary>
        public float2 FallbackAnchor;
    }

    /// <summary>
    /// 桥接层对外的控制可用性。区分"确实没有"和"暂时解析不到"——
    /// 场景刚重进、单位尚未生成完的那几帧不能当成控制丢失，否则 UI 会闪一次假报警。
    /// </summary>
    public enum ControlAvailability : byte
    {
        /// <summary>无控制实体，且没有待恢复的记录。</summary>
        None = 0,
        /// <summary>有存活且可解析的受控实体。</summary>
        Controlled = 1,
        /// <summary>持有稳定 ID 但当前解析不到，仍在宽限期内等待其重新可用。</summary>
        Suspended = 2,
    }

    /// <summary>稳定身份的只读控制视图。数组索引只在当前世界状态中瞬时有效。</summary>
    public struct SimUnitControlState
    {
        public SimEntityId EntityId;
        public int UnitIndex;
        public SimFaction Faction;
        public IntentSource IntentSource;
        public bool IsAlive;
        public float2 Position;
    }

    /// <summary>桥接层可安全消费的控制候选；不暴露任何原生容器。</summary>
    public struct SimControlCandidate
    {
        public SimEntityId EntityId;
        public SimFaction Faction;
        public IntentSource IntentSource;
        public float2 Position;
        public float Distance;
    }

    /// <summary>表现与 UI 使用的当前受控实体只读视图。UnitIndex 只对生成它的当前快照有效。</summary>
    public struct SimControlledUnitView
    {
        public SimEntityId EntityId;
        public int UnitIndex;
        public float2 Position;
        public float Health;
        public float Radius;
        public SimStatus Status;
        public SimFaction Faction;
        public IntentSource IntentSource;
        public int VisualId;
    }

    /// <summary>
    /// 单位所属阵营。内核只做阵营间敌对判定，不认识具体玩法概念。
    /// </summary>
    public enum SimFaction : byte
    {
        None = 0,
        /// <summary>启动时的主友军阵营。只表达敌我关系，不表示当前由玩家控制。</summary>
        Player = 1,
        /// <summary>玩家的附属体（孢子、分身、幼体）。</summary>
        PlayerMinion = 2,
        /// <summary>敌对单位。</summary>
        Hostile = 3,
        /// <summary>中立生物，可被双方攻击。</summary>
        Neutral = 4,
        /// <summary>无行为的可拾取物（食物、进化能碎片、尸体残块）。</summary>
        Pickup = 5,
    }

    /// <summary>
    /// 行为原型。敌人 AI 不是"一种敌人一个类"，而是原型 + 参数。
    /// 新增敌人只需配表；只有需要全新运动模式时才扩展此枚举与 JobSteering。
    /// </summary>
    public enum BehaviorKind : byte
    {
        /// <summary>不动。菌丝、卵鞘、巢心。</summary>
        Stationary = 0,
        /// <summary>随水流漂浮，不主动索敌。浮游食团。</summary>
        Drift = 1,
        /// <summary>直线追逐最近敌对目标。追猎原虫。</summary>
        Chase = 2,
        /// <summary>沿固定轴向巡逻，碰壁反向。扫尾纤毛体。</summary>
        Patrol = 3,
        /// <summary>蓄力后高速直线冲撞，冲撞中不转向。游隼纤毛。</summary>
        Charge = 4,
        /// <summary>保持距离并远程攻击。毒棘漂虫。</summary>
        Ranged = 5,
        /// <summary>向群体质心靠拢并整体推进。噬菌群。</summary>
        Swarm = 6,
        /// <summary>远离最近敌对目标。低血逃逸、恐惧状态。</summary>
        Flee = 7,
        /// <summary>环绕目标保持半径。裂鞭纤毛王。</summary>
        Orbit = 8,
        /// <summary>附着到目标身上跟随。寄生噬体。</summary>
        Latch = 9,
        /// <summary>索敌最近敌对目标，进入 AttackRange 后按 AttackCooldown 周期造成 AttackDamage。
        /// 玩家召唤物专用（PlayerMinion 阵营），如孢子仆从。</summary>
        MinionSeekAttack = 10,
        /// <summary>索敌最近敌对目标，进入 AttackRange 后以 PreferredRange 为半径造成一次 AttackDamage
        /// AOE 并自毁。玩家召唤物专用（PlayerMinion 阵营），如噬菌体。</summary>
        MinionSeekExplode = 11,
    }

    /// <summary>
    /// 状态位掩码。一个 uint 承载 32 种状态的有/无。
    /// 强度与剩余时间存在 StatusSystem（热更层），内核只用位做快速筛选与 job 分支。
    /// </summary>
    [Flags]
    public enum SimStatus : uint
    {
        None = 0u,
        /// <summary>导电：可被电弧连锁命中。</summary>
        Conductive = 1u << 0,
        /// <summary>破体：被吞噬的体积门槛下降。</summary>
        Breached = 1u << 1,
        /// <summary>标记：受到额外伤害，可被追踪效果锁定。</summary>
        Marked = 1u << 2,
        /// <summary>减速。</summary>
        Slowed = 1u << 3,
        /// <summary>麻痹：无法移动。</summary>
        Stunned = 1u << 4,
        /// <summary>腐蚀：持续掉血且体积下降。</summary>
        Corroded = 1u << 5,
        /// <summary>恐惧：转为 Flee 行为。</summary>
        Feared = 1u << 6,
        /// <summary>寄生：持续被抽取资源。</summary>
        Parasited = 1u << 7,
        /// <summary>无敌：免疫伤害。</summary>
        Invulnerable = 1u << 8,
        /// <summary>硬化：受击阈值提高。</summary>
        Hardened = 1u << 9,
        /// <summary>晶化：累积破碎层数。</summary>
        Crystallized = 1u << 10,
        /// <summary>感染：死亡时生成友方单位。</summary>
        Infected = 1u << 11,
        /// <summary>易伤：受到伤害提高。</summary>
        Vulnerable = 1u << 12,
        /// <summary>燃烧/高温：持续伤害。</summary>
        Burning = 1u << 13,
        /// <summary>污染：受污染相关效果影响。</summary>
        Polluted = 1u << 14,
        /// <summary>被引力拉拽。</summary>
        Pulled = 1u << 15,
        /// <summary>不可被吞噬（护壳完整）。</summary>
        Unedible = 1u << 16,
        /// <summary>精英单位标记，用于视觉与掉落区分。</summary>
        Elite = 1u << 17,
        /// <summary>首领单位标记。</summary>
        Boss = 1u << 18,
        /// <summary>处于菌毯区域内。</summary>
        OnMycelium = 1u << 19,
        /// <summary>过载：反应矩阵"殉爆"用的持久状态位，区别于 AffixKind.Overload（执行期修饰符）。</summary>
        Overloaded = 1u << 20,
        /// <summary>前摇中：Charge 蓄力（AttackTimer&gt;0）期间置位，供渲染层出脉冲预警色。</summary>
        Telegraphing = 1u << 21,
    }

    /// <summary>
    /// 单位生成参数。由热更层填充后经 SimCommandBuffer 提交。
    /// </summary>
    public struct SpawnRequest
    {
        public float2 Position;
        public float2 Velocity;
        public float Health;
        public float Radius;
        public float MaxSpeed;
        public int ArchetypeId;
        public SimFaction Faction;
        /// <summary>
        /// 初始意图来源。生成路径只接受 AI 或 Scripted；Player 必须由世界的受控切换命令授予，
        /// 从而保证任意时刻最多只有一个玩家控制实体。
        /// </summary>
        public IntentSource IntentSource;
        public SimStatus InitialStatus;
        /// <summary>热更层的逻辑 id，内核原样保存并在死亡事件中回传。</summary>
        public int LogicId;
        /// <summary>视觉表现 id，渲染层用它选 mesh/material/颜色。</summary>
        public int VisualId;
        /// <summary>
        /// 血统代数。0 = 场上原生（导演/热更层生成），被谁召唤出来就是「那一位 + 1」。
        ///
        /// 存在意义只有一个：**给"召唤物自己也会召唤"封顶**。
        /// 召唤原型完全可以指向另一个同样带召唤字段的原型，那就是跨帧指数增殖——
        /// 一分钟之内能把单位容量吃干净。见 <see cref="BehaviorArchetype.SummonMaxGeneration"/>
        /// 与 <see cref="SimConst.MaxSpawnGeneration"/>（后者是无论怎么配都越不过的硬顶）。
        /// </summary>
        public byte Generation;
    }

    /// <summary>
    /// 伤害指令。支持单体、圆形范围与连锁。
    /// </summary>
    public struct DamageRequest
    {
        public float2 Origin;
        /// <summary>&lt; 0 表示单体（用 TargetIndex）。</summary>
        public float Radius;
        public int TargetIndex;
        public float Amount;
        /// <summary>只对该阵营生效；None 表示对所有非施加方阵营生效。</summary>
        public SimFaction TargetFaction;
        /// <summary>命中后施加的状态。</summary>
        public SimStatus ApplyStatus;
        /// <summary>只命中带有该状态的目标；None 表示不筛选。</summary>
        public SimStatus RequireStatus;
        /// <summary>连锁跳数，0 表示不连锁。</summary>
        public int ChainCount;
        public float ChainRange;
        /// <summary>每次连锁的伤害衰减系数。</summary>
        public float ChainFalloff;
        /// <summary>用于伤害来源归属与事件回传。</summary>
        public int SourceLogicId;

        // ── combat-primitive-overhaul：扇形判定（近战底盘）──
        // 圆形分支恒存在；ConeDir 非零向量时在圆内再叠一道"必须落在锥内"的判据。
        // 这样近战不再是"前方放个大圆"（背后的敌人照样被打），而是真的只打面朝方向。
        /// <summary>扇形中轴（单位向量）。(0,0) 表示不做扇形筛选，退化为整圆（旧行为，零回归）。</summary>
        public float2 ConeDir;
        /// <summary>扇形半角的余弦。仅当 <see cref="ConeDir"/> 非零时生效。</summary>
        public float ConeCosHalf;
        /// <summary>不受扇形筛选的贴身豁免半径——圆心附近方向向量不稳定，且"贴脸"本就该被打到。</summary>
        public float ConeNearRadius;
    }

    /// <summary>
    /// 行为原型切换指令。用于运行期改变某个存活单位的行为（如首领分阶段）。
    /// 不改血量/半径等其它状态，只重定向它读取哪一套 <see cref="BehaviorArchetype"/>。
    /// </summary>
    public struct ArchetypeSwapRequest
    {
        public int TargetIndex;
        public int ArchetypeId;
    }

    /// <summary>状态施加/移除指令。</summary>
    public struct StatusRequest
    {
        public float2 Origin;
        public float Radius;
        public int TargetIndex;
        public SimStatus Status;
        public SimFaction TargetFaction;
        /// <summary>true 施加，false 移除。</summary>
        public bool Add;
    }

    /// <summary>
    /// 弹体行为开关位。combat-primitive-overhaul：布尔型基元集中成一个 byte，
    /// 避免 <see cref="ProjectileState"/> 为每个开关多一个 4 字节字段。
    /// </summary>
    [System.Flags]
    public enum SimProjectileFlags : byte
    {
        None = 0,
        /// <summary>抛投（gene_arc）：飞行途中不与单位碰撞，只在落点炸开。俯视视角下"越过前排砸后排"的唯一表达方式。</summary>
        Lob = 1 << 0,
        /// <summary>终结时在落点做一次范围结算（Explode/抛投落地）。</summary>
        BurstOnEnd = 1 << 1,
        /// <summary>终结时朝发射者当前位置回飞一发（gene_return / gene_blood）。</summary>
        ReturnToOwner = 1 << 2,
        /// <summary>这是回程弹体——命中结算按治疗而非伤害交给热更层（gene_blood 的 Tag "Blood"）；内核只负责标记。</summary>
        Returning = 1 << 3,
        /// <summary>撞墙/撞障时反弹而不是销毁（gene_elastic / gene_mirror / gene_membrane）。</summary>
        BounceWalls = 1 << 4,
        /// <summary>
        /// 追踪时**优先锁定带 <see cref="SimStatus.Marked"/> 的目标**（gene_receptor「受体记忆」）。
        ///
        /// 文案是「打过的敌人会被记住，后续更会追它」。此前这条基因只是把 Homing 强度调高一点，
        /// 与 gene_taxis 除了数值以外毫无区别——"记忆"根本不存在。
        /// 现在真的成立：命中时给目标挂 Marked（经 <see cref="ProjectileRequest.ApplyStatus"/>），
        /// 之后的弹体在选目标时把已标记者的距离按
        /// <see cref="JobProjectile.MarkedTargetBias"/> 打折，于是它们会越过更近的新目标去追老目标。
        /// </summary>
        PreferMarked = 1 << 5,
    }

    /// <summary>投射物生成指令。</summary>
    public struct ProjectileRequest
    {
        public float2 Position;
        public float2 Direction;
        public float Speed;
        public float Damage;
        public float Radius;
        public float Lifetime;
        /// <summary>可穿透的目标数，1 表示命中即消失。</summary>
        public int Pierce;
        public SimFaction TargetFaction;
        public SimStatus ApplyStatus;
        public int SourceLogicId;
        public int VisualId;

        // ── combat-primitive-overhaul：弹道基元。全部留 0 时行为与旧版逐字一致（零回归）。──

        /// <summary>追踪权重 0-1。每帧朝 <see cref="HomingRange"/> 内最近目标转向，权重越高转得越狠。</summary>
        public float Homing;
        /// <summary>追踪搜敌半径。0 表示不追踪——**没有"全屏锁敌"这个选项**，射程外的敌人弹体看不见。</summary>
        public float HomingRange;
        /// <summary>最大转向角速度（度/秒）。限制"瞬间掉头"，让追踪读起来像导弹而不是瞬移。</summary>
        public float TurnRateDeg;
        /// <summary>每秒速度衰减比例（0-1）。抛投/重力弹靠它自然缩短射程。</summary>
        public float Drag;
        /// <summary>命中/终结时的溅射半径。0 表示纯单体命中。</summary>
        public float AreaRadius;
        /// <summary>撞墙/撞障剩余反弹次数（需 <see cref="SimProjectileFlags.BounceWalls"/>）。</summary>
        public int BounceCount;
        /// <summary>终结时分裂出的子弹体数量。方向以**入射方向**为中轴左右展开，不是世界系固定角。</summary>
        public int SplitCount;
        /// <summary>分裂扇角（度，总张角）。</summary>
        public float SplitAngleDeg;
        /// <summary>沿途拖尾每跳伤害。0 表示无拖尾。</summary>
        public float TrailDamage;
        /// <summary>沿途拖尾跳伤间隔（秒）。</summary>
        public float TrailInterval;
        /// <summary>终结时在落点留下的持续区域秒数（交给热更 AreaZoneSystem 落地）。</summary>
        public float LingerSeconds;
        /// <summary>留坑半径。</summary>
        public float LingerRadius;
        /// <summary>命中时的连锁跳数。</summary>
        public int ChainCount;
        /// <summary>横向蛇行角速度（度/秒）。配合 <see cref="WeaveAmp"/> 表达鞭毛绕/乱流的轨迹噪声。</summary>
        public float WeaveRateDeg;
        /// <summary>横向蛇行幅度（世界单位/秒的侧向速度峰值）。</summary>
        public float WeaveAmp;
        /// <summary>子代计数。二级弹体（分裂/回旋产物）只允许再生一代，防止无界递归。</summary>
        public byte Generation;
        public SimProjectileFlags Flags;
        /// <summary>RGBA8 打包的实例色（0 = 渲染器默认色）。见 <see cref="ProjectileState.Tint"/>。</summary>
        public uint Tint;
    }

    /// <summary>
    /// 持续区域（毒坑 / 酸洼 / 贴身光环 / 敌方毒云）——内核一等实体。
    ///
    /// enemy-mechanics-parity：区域此前是**热更层的玩家专属结构**（<c>MetabolicSliceBridge._pendingLinger</c>），
    /// 于是"敌人也会放毒"在实现层面根本无从谈起——内核里没有这个概念，敌人也不在热更层里跑。
    /// 现在下沉成内核实体，玩家与敌人共用一套：谁放的由 <see cref="TargetFaction"/> 决定。
    ///
    /// 跟随（<see cref="FollowUnitIndex"/>）是光环所必需的：贴身圈得跟着人走，
    /// 而"跟着谁走"只有内核知道（热更层拿不到敌人的逐帧坐标而不违反性能红线）。
    /// </summary>
    public struct ZoneState
    {
        public float2 Position;
        public float Radius;
        /// <summary>半径每秒外扩多少（gene_ripple「扩散波」）。0 = 不扩。</summary>
        public float GrowthRate;
        /// <summary>半径上限，防止一个坑吃掉整张图。</summary>
        public float MaxRadius;
        /// <summary>每跳伤害。</summary>
        public float DamagePerTick;
        /// <summary>跳伤间隔（秒）。</summary>
        public float Interval;
        public float TickTimer;
        public float TimeLeft;
        /// <summary>只伤害这个阵营。玩家的毒坑填 Hostile，敌人的填 Player。</summary>
        public byte TargetFaction;
        public uint ApplyStatus;
        public int ChainCount;
        public int SourceLogicId;
        /// <summary>跟随某个单位（光环）。<see cref="SimConst.InvalidIndex"/> = 钉在原地。</summary>
        public int FollowUnitIndex;
        /// <summary>RGBA8 实例色（0 = 渲染器默认）。</summary>
        public uint Tint;
        public byte Alive;
    }

    /// <summary>区域生成指令。</summary>
    public struct ZoneRequest
    {
        public float2 Position;
        public float Radius;
        public float GrowthRate;
        public float MaxRadius;
        public float DamagePerTick;
        public float Interval;
        public float Seconds;
        public SimFaction TargetFaction;
        public SimStatus ApplyStatus;
        public int ChainCount;
        public int SourceLogicId;
        public int FollowUnitIndex;
        public uint Tint;
    }

    /// <summary>
    /// 弹体终结事件。combat-primitive-overhaul：热更层此前靠"开火时预测一个落点"来放留坑/命中特效，
    /// 只要弹体真的会拐弯/反弹/被障碍挡住，预测点就和真实落点分叉（这正是"表现层看到命中了但没伤害"的根因）。
    /// 改由内核回传**真实终结点**，热更层据此放 Linger 区域与命中表现，二者从此不可能不一致。
    /// </summary>
    public struct ProjectileEndEvent
    {
        public float2 Position;
        /// <summary>终结瞬间的飞行方向（已归一化）。</summary>
        public float2 Direction;
        public float Damage;
        public float AreaRadius;
        public float LingerSeconds;
        public float LingerRadius;
        public int SourceLogicId;
        public int VisualId;
        public ProjectileEndReason Reason;
    }

    /// <summary>弹体为什么没的。热更层据此区分"打中了"和"飞没了"的表现。</summary>
    public enum ProjectileEndReason : byte
    {
        /// <summary>穿透次数耗尽（真的打到人了）。</summary>
        HitTarget = 0,
        /// <summary>寿命到期/射程耗尽。</summary>
        Expired = 1,
        /// <summary>飞出场地边界。</summary>
        OutOfBounds = 2,
        /// <summary>撞上静态障碍。</summary>
        Obstacle = 3,
    }

    /// <summary>
    /// 致死来源类型。内核只区分"怎么没的"（伤害耗尽 vs 被吞噬清除），
    /// 不认识"污染"等玩法概念——那属于热更层自己的判定（见 CellStageFlow.ResolveDeathCause）。
    /// </summary>
    public enum DeathCauseKind : byte
    {
        /// <summary>未标记（保留值，理论上不应出现在实际事件中）。</summary>
        Unknown = 0,
        /// <summary>血量耗尽（EmitDeath 路径：放电、投射物、毒区等伤害致死）。</summary>
        Damage = 1,
        /// <summary>被吞噬清除（KillUnit 路径：CellDevourSystem 吞噬结算）。</summary>
        Devour = 2,
    }

    /// <summary>
    /// 单位死亡事件。内核回写，热更层每帧读取以结算掉落、触发卡牌等。
    /// </summary>
    public struct DeathEvent
    {
        public int LogicId;
        public int ArchetypeId;
        public float2 Position;
        public float Radius;
        public SimFaction Faction;
        public SimStatus StatusAtDeath;
        /// <summary>击杀来源的逻辑 id；0 表示环境或自然死亡。</summary>
        public int KillerLogicId;
        /// <summary>致死来源类型。</summary>
        public DeathCauseKind CauseKind;
    }

    /// <summary>命中事件。用于卡牌 OnHit 触发与命中反馈。</summary>
    public struct HitEvent
    {
        public int TargetLogicId;
        public int SourceLogicId;
        public float2 Position;
        public float Damage;
        public bool Lethal;
        /// <summary>命中时目标的单位索引。让热更层表现系统（如血条）能 O(1) 定位目标，
        /// 不必每帧对 Snapshot 做 O(容量) 的 LogicId→index 扫描。</summary>
        public int TargetIndex;
        /// <summary>命中后目标的剩余血量（已钳制到 ≥0）。同一次结算里已经算出，顺带写出。</summary>
        public float RemainingHealth;
    }

    /// <summary>
    /// 行为原型参数。全部来自 Luban 配置，内核只消费。
    /// </summary>
    public struct BehaviorArchetype
    {
        public BehaviorKind Kind;
        /// <summary>加速度，决定转向跟手程度。</summary>
        public float Accel;
        /// <summary>最大转向角速度（弧度/秒）。0 表示不限制。</summary>
        public float TurnRate;
        /// <summary>索敌半径，超出则退化为 Drift。</summary>
        public float AggroRange;
        /// <summary>攻击距离。</summary>
        public float AttackRange;
        /// <summary>攻击间隔（秒）。</summary>
        public float AttackCooldown;
        public float AttackDamage;
        /// <summary>与同类的分离强度，0 表示允许重叠。</summary>
        public float Separation;
        /// <summary>Orbit/Ranged 用的保持半径。</summary>
        public float PreferredRange;
        /// <summary>Charge 蓄力时间（秒）。</summary>
        public float ChargeTelegraph;
        /// <summary>Charge 冲刺速度倍率。</summary>
        public float ChargeSpeedMul;
        /// <summary>Drift 的随机游走强度。</summary>
        public float WanderStrength;

        // ── enemy-ranged-and-parry：敌人的远程攻击 ──────────────────────────
        //
        // 在此之前**敌人只有接触伤害**：`BehaviorKind.Ranged` 只影响转向（保持距离），
        // 从来不发射任何东西——所谓"远程"其实是「站在 AttackRange=12 米外隐形地扣血」，
        // 玩家看不见、也躲不掉。填了 <see cref="RangedSpeed"/> 的原型改走真弹体
        // （<see cref="SimWorld.ResolveHostileRangedCombat"/>），并且**不再吃接触伤害**
        // （<see cref="JobContactDamage"/> 会跳过它们），否则同一个 AttackRange 会结算两遍。

        /// <summary>远程弹体速度（世界单位/秒）。<b>0 = 这个原型不发射弹体</b>，行为逐字不变。</summary>
        public float RangedSpeed;
        /// <summary>一次齐射的弹数。0/1 = 单发。</summary>
        public float RangedCount;
        /// <summary>齐射张角（度），以朝向玩家的方向为中轴。0 = 全部同向。</summary>
        public float RangedSpreadDeg;
        /// <summary>弹体碰撞半径。0 时用内核默认值。</summary>
        public float RangedRadius;
        /// <summary>弹体追踪强度 0-1。给敌人的追踪要克制——玩家得躲得掉。</summary>
        public float RangedHoming;

        // ── enemy-mechanics-parity：敌人的区域攻击（放毒 / 贴身光环）──────────
        //
        // 毒坑和光环在内核里是同一个东西（<see cref="ZoneState"/>），
        // 差别只在**放在哪 / 跟不跟着走**，所以共用一组字段，由 ZoneMode 区分。

        /// <summary>区域攻击模式：0=无、1=丢到玩家脚下、2=钉在自己脚下、3=跟随自己（贴身光环）。</summary>
        public float ZoneMode;
        public float ZoneRadius;
        public float ZoneSeconds;
        public float ZoneDamagePerTick;
        public float ZoneTickInterval;
        /// <summary>两次布场的间隔（秒）。光环把它设得略短于 <see cref="ZoneSeconds"/> 即为常驻。</summary>
        public float ZoneCooldown;

        // ── enemy-mechanics-parity：敌人的召唤 ────────────────────────────────

        /// <summary>召唤出来的单位用哪个行为原型。&lt;0 表示不召唤。</summary>
        public float SummonArchetypeId;
        public float SummonCount;
        public float SummonCooldown;
        /// <summary>召唤物生命。0 时用宿主生命的一小部分。</summary>
        public float SummonHealth;
        /// <summary>
        /// 这一支血统最多召到第几代。判据是「本单位的 <see cref="SpawnRequest.Generation"/>
        /// 小于它才允许召唤」，所以：
        /// <list type="bullet">
        /// <item>1 = 只有原生单位能召（召出来的孙子辈不再召，最常用）</item>
        /// <item>2 = 允许再往下一层（孵化巢孵出小巢）</item>
        /// <item>0 或负数 = 按 1 处理，不给"意外配成 0 就永远召不出来"留坑</item>
        /// </list>
        /// 无论配多少都越不过 <see cref="SimConst.MaxSpawnGeneration"/>。
        /// </summary>
        public float SummonMaxGeneration;

        public static BehaviorArchetype Default => new BehaviorArchetype
        {
            Kind = BehaviorKind.Drift,
            Accel = 8f,
            TurnRate = 0f,
            AggroRange = 0f,
            AttackRange = 0.5f,
            AttackCooldown = 1f,
            AttackDamage = 1f,
            Separation = 1f,
            PreferredRange = 0f,
            ChargeTelegraph = 0f,
            ChargeSpeedMul = 1f,
            WanderStrength = 1f,
        };
    }

    /// <summary>内核初始化配置。</summary>
    public struct SimConfig
    {
        /// <summary>单位容量上限，运行期不扩容。</summary>
        public int UnitCapacity;
        public int ProjectileCapacity;
        /// <summary>持续区域（毒坑/光环）容量上限，运行期不扩容。</summary>
        public int ZoneCapacity;
        /// <summary>场地半边长（正方形，中心在原点）。</summary>
        public float ArenaHalfExtent;
        /// <summary>空间哈希 cell 边长。应 ≥ 最大交互半径。</summary>
        public float HashCellSize;
        /// <summary>每帧最多处理的死亡事件数。</summary>
        public int MaxDeathEventsPerFrame;
        public int MaxHitEventsPerFrame;
        public uint RandomSeed;
        /// <summary>破体状态下吞噬门槛折扣。</summary>
        public float BreachedDiscount;
        /// <summary>腐蚀状态下吞噬门槛折扣。</summary>
        public float CorrodedDiscount;

        public static SimConfig Default => new SimConfig
        {
            UnitCapacity = 16384,
            ProjectileCapacity = 4096,
            ZoneCapacity = 256,
            ArenaHalfExtent = 90f,
            HashCellSize = 4f,
            MaxDeathEventsPerFrame = 2048,
            MaxHitEventsPerFrame = 2048,
            RandomSeed = 0x5F3759DFu,
            BreachedDiscount = 0.7f,
            CorrodedDiscount = 0.85f,
        };
    }

    /// <summary>内核索引常量。</summary>
    public static class SimConst
    {
        /// <summary>启动时默认友军的兼容索引；只用于世界初始化，不得作为控制身份。</summary>
        public const int PlayerIndex = 0;
        /// <summary>无效索引。</summary>
        public const int InvalidIndex = -1;
        /// <summary>静态障碍数量上限（story-009）。</summary>
        public const int MaxObstacles = 32;

        /// <summary>一帧内最多缓存多少条控制权变更（M1-06）。链式回弹再密也用不满，
        /// 溢出时丢最旧的那条——最终控制状态永远以最后一条为准。</summary>
        public const int MaxControlChangesPerFrame = 8;

        /// <summary>意识回弹的默认最大距离。桥接层会用当前信号范围覆盖它，
        /// 这里的值只保证"内核被单独实例化（回归测试）时也有确定行为"。</summary>
        public const float DefaultControlFallbackRange = 18f;

        /// <summary>撤退命令的默认威胁感知半径（M2-02）。超出此距离的敌人不影响撤离方向。</summary>
        public const float DefaultRetreatThreatRange = 12f;

        /// <summary>一次框选最多返回多少单位（M2-02）。防止误框全场时给热更层甩回一个巨型数组。</summary>
        public const int MaxSelectionSize = 64;

        /// <summary>
        /// 召唤血统的**硬顶**：<see cref="SpawnRequest.Generation"/> 到这个数就再也召不出下一代，
        /// 无论 <see cref="BehaviorArchetype.SummonMaxGeneration"/> 配了多少。
        ///
        /// 这是一条兜底红线，不是平衡旋钮——配表写错一个数就能让指数增殖吃光单位容量，
        /// 而那种崩法在运行时看起来像"莫名其妙卡死"，很难查。
        /// </summary>
        public const int MaxSpawnGeneration = 3;
    }

    /// <summary>
    /// 静态圆形障碍（story-009）。热更层传管理数组，内核转 NativeArray——
    /// 镜像 <see cref="BehaviorArchetype"/>/SetArchetypes 的先例，避免热更层直接碰原生容器。
    /// </summary>
    public struct ObstacleSpec
    {
        public float2 Position;
        public float Radius;
    }
}
