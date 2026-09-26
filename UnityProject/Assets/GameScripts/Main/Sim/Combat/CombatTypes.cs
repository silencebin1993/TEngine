using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG0-ARCH-03（FG14 FGR-ARC-003 战斗逐单位逻辑下沉内核；FG15 FGR-SYS-041/042 规模与帧预算）：战斗内核的常量。
    ///
    /// 内核只认位置、半径、阵营、行为、武器参数与状态位，不认识“侦察机”“铸造重炮”之类的玩法概念（与 <see cref="SimWorld"/> 同一纪律）：
    /// Demo 的每一种敌人、每一套装配在热更层翻译成 <see cref="CombatWeapon"/> / <see cref="CombatBehaviorProfile"/> 数据后交给内核。
    /// 位置用 double2（1 单位 = 1 格 = 1 米），离原点一百万格仍是亚毫米精度（FGR-GEN-051）；画面换算在热更层按原点区块做。
    /// </summary>
    public static class CombatConst
    {
        /// <summary>存档快照格式版本（<see cref="CombatKernel.Serialize"/>）。不认识的版本整块不读、原样保留。</summary>
        public const int FormatVersion = 1;

        /// <summary>快照魔数 “CBK1”。</summary>
        public const uint Magic = 0x314B4243;

        public const int None = -1;

        /// <summary>标记跳转一次最多跳几个目标（存储上限；Demo 数值是 2，见 FracturedCityLayout.MarkJumpMaxTargets）。</summary>
        public const int MaxJumpTargets = 4;

        /// <summary>空间网格的格边长下限（米）。</summary>
        public const float MinGridCell = 1f;

        /// <summary>直控移动的坐标钳制“不限”。</summary>
        public const double NoClamp = 1e30;
    }

    /// <summary>阵营。己方（玩家）与敌方互为目标；中立单位（训练靶）只接受显式指向它的攻击。</summary>
    public enum CombatFaction : byte
    {
        Player = 0,
        Hostile = 1,
        Neutral = 2,
    }

    /// <summary>单位种类（只用于统计与画面；行为由 <see cref="CombatBehavior"/> 决定）。</summary>
    public enum CombatUnitKind : byte
    {
        Machine = 0,
        Enemy = 1,
        Turret = 2,
        /// <summary>不会移动、不会主动开火的目标（首领的供能节点、训练靶）。</summary>
        Structure = 3,
    }

    /// <summary>
    /// 单位每步做什么。Demo 的六种敌人行为逐条翻译自 FracturedCityEnemyAi / FoundryOutpostEnemyAi / FoundryOutpostCoreBoss（数值由热更层按 Demo 常量传入）；
    /// 突袭者与炮塔是正式版（FG06）的原型行为。
    /// </summary>
    public enum CombatBehavior : byte
    {
        /// <summary>不动、不开火（供能节点、训练靶）。</summary>
        None = 0,
        /// <summary>己方机器：只执行玩家显式下达的命令（移动 / 攻击 / 守备 / 撤退 / 工作赶路 / 直控），不自动找目标（FGR-BASE-020）。</summary>
        Commanded = 1,
        /// <summary>驻守开火：冷却到点时打射程内最近的可见目标；没有目标不重置冷却（护甲机、干扰支援、主核心、炮塔）。</summary>
        HoldFire = 2,
        /// <summary>瞄准线两段式：发现目标先瞄准 <see cref="CombatWeapon.AimSeconds"/>，到点重新取射程内最近目标开火（步进炮）。</summary>
        Telegraph = 3,
        /// <summary>侦察：受威胁后撤（不超出出生点牵引半径）、无威胁时出生点附近摆动巡逻；按周期标记视线内最近的机器（静默侦察机）。</summary>
        Scout = 4,
        /// <summary>干扰：驻守，清除半径内己方机器身上的标记与友军身上的标记，外加驻守开火（静默干扰机）。</summary>
        Jammer = 5,
        /// <summary>维修：受威胁后撤；否则走向血量百分比最低的友军，进入范围按周期治疗（维修机）。</summary>
        Repair = 6,
        /// <summary>突袭者（FG06 原型）：扑向感知范围内最近的敌对目标，进入射程后开火；没有目标时朝目标点（出生时给定）前进。</summary>
        Raider = 7,
        /// <summary>家园训练靶自动交战（Demo ER4-PRIM-05）：空闲、未被接入的机器在射程内按间隔请求一次对训练靶的攻击（结算在热更层）。</summary>
        AutoEngage = 8,
    }

    /// <summary>单位状态位。</summary>
    [Flags]
    public enum CombatUnitFlags : uint
    {
        None = 0,
        Alive = 1 << 0,
        /// <summary>可以被选为攻击目标 / 被弹体命中。</summary>
        Targetable = 1 << 1,
        /// <summary>血量真相在热更层（机器记录、首领状态机、训练靶）：内核不扣血，发出伤害 / 治疗请求事件，由热更层结算后回写镜像血量。</summary>
        ExternalHealth = 1 << 2,
        /// <summary>内核扣血的单位要逐次报告（Demo 的具名敌人：受伤 / 阵亡 / 被治疗都同步到记录）。突袭规模的单位不报告，只计汇总。</summary>
        Report = 1 << 3,
        /// <summary>武器可以开火（主核心只在阶段一 / 阶段二打开）。</summary>
        WeaponEnabled = 1 << 4,
        /// <summary>正在被玩家接入（直控）：不执行编队命令，移动来自直控输入。</summary>
        Possessed = 1 << 5,
        /// <summary>开火前要求视线不被障碍遮挡（Demo 敌人“标记不魔法穿墙”）。</summary>
        NeedsLos = 1 << 6,
        /// <summary>武器过热（迟滞：降到 <see cref="CombatWeapon.RecoverBelow"/> 以下才恢复）。</summary>
        Overheated = 1 << 7,
        /// <summary>装了散热鳍：散热加 <see cref="CombatWeapon.HeatSinkBonus"/>。</summary>
        HeatSink = 1 << 8,
        /// <summary>敌方“耐热”反制：熔穿过载的额外穿甲对它无效。</summary>
        HeatResistant = 1 << 9,
        /// <summary>阵亡后自动从内核移除（突袭规模的匿名单位）。具名单位阵亡后留作墓碑，热更层决定何时移除。</summary>
        RemoveOnDeath = 1 << 10,
        /// <summary>用实例化渲染画出来（没有 GameObject 表现的单位：突袭者、炮塔原型）。</summary>
        Instanced = 1 << 11,
        /// <summary>开火不发“敌人开火”提示（主核心：Demo 直接扣血，不出敌人开火音）。</summary>
        SilentFire = 1 << 12,
        /// <summary>这一步有待执行的“暂停中下达”的命令（UI 显示“已排队”）；第一次执行时清掉。</summary>
        PendingCommand = 1 << 13,
        /// <summary>当前不能被击伤（首领供能节点只在护盾阶段可伤、主核心只在阶段一 / 二可伤；热更层在阶段变化时改）。
        /// 开火照常（冷却、积热照算），伤害不落地，开火结果为 <see cref="CombatFireResult.Invulnerable"/>。</summary>
        Invulnerable = 1 << 14,
        /// <summary>暂不自动交战（家园里仍占用工厂出口的机器）：自动交战的冷却照常走，但不发交战请求、不消耗冷却
        /// （Demo TickAutoEngage 对厂内机器直接跳过）；热更层在机器进出工厂时写。</summary>
        EngageHold = 1 << 15,
    }

    /// <summary>己方机器的命令。与 Demo RegionSquadCommandSystem / HomeValleyMachineMarker 的语义逐条一致。</summary>
    public enum CombatCommandKind : byte
    {
        None = 0,
        Move = 1,
        Attack = 2,
        Guard = 3,
        Retreat = 4,
        /// <summary>工作赶路（Demo 的 HomeValleyMachineMarker.CommandMoveTo）：直线走到目标，进入到达半径即对齐到目标点，发“到达”事件。</summary>
        WorkMove = 5,
    }

    /// <summary>命令结束原因（热更层据此写编队事件文本与反馈）。</summary>
    public enum CombatEndReason : byte
    {
        None = 0,
        Arrived = 1,
        TargetLost = 2,
        TargetDestroyed = 3,
        Stuck = 4,
        Cancelled = 5,
        /// <summary>执行者阵亡或离场。</summary>
        Dead = 6,
    }

    /// <summary>
    /// 内核事件。前半段是**玩法事件**（热更层必须处理：永不丢弃，按步限量排空、跨步保留、进存档）；
    /// 后半段是**提示事件**（声音 / 字幕 / 特效，只影响反馈）：每步有上限，超出的丢弃并计数（B17 同类音效有数量上限）。
    /// </summary>
    public enum CombatEventKind : byte
    {
        None = 0,
        // ── 玩法事件 ──
        /// <summary>内核扣血的报告单位阵亡。Unit=阵亡者，Other=击杀者。</summary>
        Killed = 1,
        /// <summary>内核扣血的报告单位受伤。Value=伤害，Value2=剩余血量。</summary>
        Damaged = 2,
        /// <summary>内核扣血的报告单位被治疗。Value=治疗量，Value2=治疗后血量。</summary>
        Healed = 3,
        /// <summary>对血量在热更层的单位的伤害请求。Unit=目标，Other=攻击者，Value=伤害（护甲 / 穿甲 / 侧后加成已算好）。</summary>
        DamageRequest = 4,
        /// <summary>对血量在热更层的单位的治疗请求。Unit=目标，Other=治疗者，Value=治疗量。</summary>
        HealRequest = 5,
        /// <summary>命令结束。Unit=执行者，Code=<see cref="CombatEndReason"/>，Code2=命令种类，Other=攻击目标（攻击命令）。</summary>
        CommandEnded = 6,
        /// <summary>路径受阻第 N 次（N = Value，还没放弃）。</summary>
        CommandStuckStrike = 7,
        /// <summary>编队攻击命令的一次开火尝试的结果。Unit=攻击者，Other=目标，Code=<see cref="CombatFireResult"/>。</summary>
        AttackOutcome = 8,
        /// <summary>侦察单位标记了一台己方机器。Unit=被标记的机器，Other=侦察单位，Value=持续秒数。</summary>
        MachineMarked = 9,
        /// <summary>侦察单位的标记周期到了但视线内没有目标（Demo 写日志）。Unit=侦察单位。</summary>
        MarkMissed = 10,
        /// <summary>己方单位第一次进入兴趣点范围。Unit=单位，Other=兴趣点序号。</summary>
        PoiReached = 11,
        /// <summary>工作赶路到达。Unit=机器。</summary>
        WorkArrived = 12,
        /// <summary>训练靶自动交战：请求热更层结算一次攻击。Unit=攻击者，Other=训练靶。</summary>
        EngageRequest = 13,
        /// <summary>干扰单位清掉了一台机器身上的标记。Unit=机器，Other=干扰单位。</summary>
        MarkCleared = 14,

        // ── 提示事件（有上限，可丢弃）──
        /// <summary>己方普通武器开火（Unit=攻击者，Other=目标）。</summary>
        Fired = 32,
        /// <summary>重炮开始 1 秒瞄准线（蓄力）。</summary>
        CannonCharge = 33,
        /// <summary>重炮开火。</summary>
        CannonFire = 34,
        /// <summary>武器过热停火。</summary>
        Overheat = 35,
        /// <summary>熔穿过载生效（这一发）。</summary>
        MeltOverload = 36,
        /// <summary>命中正面装甲（减伤）。</summary>
        ArmorHit = 37,
        /// <summary>标记跳转真正跳出去了（Value=跳了几个）。</summary>
        MarkJump = 38,
        /// <summary>敌方开火（Unit=敌人，Other=目标）。</summary>
        EnemyFired = 39,
        /// <summary>弹体命中（Pos=命中点）。</summary>
        ProjectileHit = 40,
        /// <summary>步进炮开始瞄准线 / 瞄准线结束落空（Code：0 开始，1 落空，2 命中）。</summary>
        Telegraph = 41,
    }

    /// <summary>一次开火尝试的结果（编队攻击、直控点击都走 <see cref="CombatKernel.FireAt"/>；热更层映射成 Demo 同一套原因文本）。</summary>
    public enum CombatFireResult : byte
    {
        Ok = 0,
        /// <summary>重炮：这一次调用只推进了瞄准线，还没打出去（不是失败）。</summary>
        StillAiming = 1,
        NoAttacker = 2,
        AttackerDead = 3,
        /// <summary>没有登记武器（装配解析失败）。</summary>
        NoWeapon = 4,
        /// <summary>装配没有可攻击的主武器出口（8 号汇槽为空）。</summary>
        NoCombatOutput = 5,
        Overheated = 6,
        Cooldown = 7,
        TargetDead = 8,
        TargetMissing = 9,
        OutOfRange = 10,
        /// <summary>目标不可被攻击（己方 / 不可选中）。</summary>
        NotHostile = 11,
        /// <summary>目标当前无法被击伤（首领阶段）：开火照常，伤害不落地。</summary>
        Invulnerable = 12,
    }

    /// <summary>武器开火方式。</summary>
    public enum CombatWeaponMode : byte
    {
        /// <summary>即时命中（Demo 的连射器、切割束、敌人自卫攻击）。</summary>
        Instant = 0,
        /// <summary>铸造重炮：两段式调用（第一次开始 1 秒瞄准线，到点后的下一次调用才开火），带冷却与热量（Demo CannonCombat）。</summary>
        Cannon = 1,
        /// <summary>弹体：生成一枚直线飞行的弹体，飞行中与敌对单位碰撞才结算（炮塔、突袭者；FGR-ARC-003“弹道唯一真相在内核”）。</summary>
        Projectile = 2,
    }

    /// <summary>具名反应（Demo：标记跳转、熔穿过载）。</summary>
    public enum CombatReaction : byte
    {
        None = 0,
        MarkJump = 1,
        MeltOverload = 2,
    }

    /// <summary>炮塔选目标模式（FG06 FGR-DEF-003；本 Story 接入最近与最低耐久两种，其余三种由 FG6-DEF-02 追加）。</summary>
    public enum CombatTargetMode : byte
    {
        Nearest = 0,
        LowestHealth = 1,
    }

    /// <summary>武器参数（一套装配或一种敌人一份）。数值全部由热更层给出（Demo 常量 / Luban 表），内核不读表。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatWeapon
    {
        public CombatWeaponMode Mode;
        public CombatReaction Reaction;
        /// <summary>1 = 装配有可攻击的主武器出口（Demo HasCombatOutput）；重炮不看它。</summary>
        public byte HasOutput;
        public CombatTargetMode TargetMode;
        /// <summary>射程（驻守开火 / 炮塔 / 突袭者用；编队攻击命令用命令自带的接战距离）。</summary>
        public float Range;
        public float Damage;
        public float Cooldown;
        /// <summary>瞄准线秒数（重炮 1 秒 / 步进炮 1 秒）。</summary>
        public float AimSeconds;
        public float ProjectileSpeed;
        public float ProjectileRadius;
        public float ProjectileLife;
        /// <summary>重炮每发基础积热。</summary>
        public float HeatPerShot;
        /// <summary>熔穿过载额外积热。</summary>
        public float OverloadExtraHeat;
        public float OverheatAt;
        public float RecoverBelow;
        public float Dissipation;
        public float HeatSinkBonus;
        /// <summary>熔穿过载的额外穿甲（从护甲减伤比例里扣掉）。</summary>
        public float PierceBonus;
        /// <summary>命中后给目标打标记的秒数（0 = 没有标记功能）。</summary>
        public float MarkSeconds;
        public float JumpRange;
        public float JumpFalloff;
        public int JumpMax;
    }

    /// <summary>行为参数（一种敌人一份）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatBehaviorProfile
    {
        public float Speed;
        /// <summary>威胁进入这个距离就后撤（侦察 / 维修）。</summary>
        public float FleeTrigger;
        /// <summary>后撤不超出出生点这个半径。</summary>
        public float Leash;
        public float PatrolRadius;
        /// <summary>巡逻摆动的角频率（sin(游戏秒 × 该值)）。</summary>
        public float PatrolFreq;
        /// <summary>侦察标记距离 / 突袭者感知距离。</summary>
        public float SenseRange;
        /// <summary>周期（侦察标记间隔 / 维修冷却）。</summary>
        public float CycleSeconds;
        /// <summary>效果持续（侦察标记秒数）。</summary>
        public float EffectSeconds;
        /// <summary>效果量（维修治疗量）。</summary>
        public float EffectAmount;
        /// <summary>效果范围（维修距离 / 干扰清标记半径）。</summary>
        public float EffectRange;
    }

    /// <summary>生成一个单位的参数。</summary>
    public struct CombatSpawn
    {
        public int ExtKey;
        public CombatUnitKind Kind;
        public CombatFaction Faction;
        public CombatBehavior Behavior;
        public CombatUnitFlags Flags;
        public double2 Position;
        /// <summary>出生点 / 牵引中心 / 突袭者目标点。</summary>
        public double2 Home;
        public float Radius;
        public float Speed;
        public float Health;
        public float MaxHealth;
        public int Weapon;
        public int BehaviorProfile;
        /// <summary>正面装甲减伤比例（0 = 没有装甲概念）。</summary>
        public float ArmorFraction;
        public float ArmorHalfAngleDeg;
        public float2 ArmorFacing;
        /// <summary>侧后命中加成（主核心阶段二 +20%；热更层在阶段变化时改）。</summary>
        public float BackHitBonus;
        /// <summary>同一步内的结算顺序：0 先于 1（Demo 铸造前哨里主核心先于其它敌人）。</summary>
        public byte Priority;
        public float Heat;
        public double AimReadyAt;
        public double NextFireAt;
        /// <summary>行为周期计时的初值（Demo 记录里的 CycleCooldownRemaining / SecondaryTimer）。</summary>
        public float Cycle;
        public float Secondary;
    }

    /// <summary>内核事件（40 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatEvent
    {
        public CombatEventKind Kind;
        public byte Code;
        public byte Code2;
        public byte Pad;
        public int Unit;
        public int Other;
        public float Value;
        public float Value2;
        /// <summary>事件序号（玩法事件与提示事件共用一个递增计数）：热更层按它合并两条队列，保持与 Demo 逐调用一致的先后顺序。</summary>
        public int Seq;
        public double2 Pos;
    }

    /// <summary>单位的只读快照（查询用）。</summary>
    public struct CombatUnitView
    {
        public int Id;
        public int ExtKey;
        public CombatUnitKind Kind;
        public CombatFaction Faction;
        public CombatBehavior Behavior;
        public CombatUnitFlags Flags;
        public double2 Position;
        public double2 PrevPosition;
        public float Health;
        public float MaxHealth;
        public float Heat;
        public double AimReadyAt;
        public double NextFireAt;
        public float Cycle;
        public float Secondary;
        public CombatCommandKind Command;
        public int CommandTarget;
        public double2 CommandPos;
        public double MarkedUntil;
        public int Weapon;
        public bool Alive => (Flags & CombatUnitFlags.Alive) != 0;
    }

    /// <summary>内核配置（热更层读 combat.* 调参行组装）。</summary>
    [Serializable]
    public struct CombatConfig
    {
        /// <summary>空间网格格边长（米）。</summary>
        public float GridCell;
        /// <summary>每步最多交给热更层处理的玩法事件数（超出的留到下一步，不丢；FGR-SYS-042 热更层开销与数量无关）。</summary>
        public int MaxGameplayEventsPerStep;
        /// <summary>每步最多保留的提示事件数（超出丢弃并计数）。</summary>
        public int MaxCueEventsPerStep;
        /// <summary>同时存在的弹体上限（超出的开火不生成弹体并计数）。</summary>
        public int ProjectileCapacity;
        /// <summary>墓碑（已移除的空槽）超过这个比例时在步首压实。</summary>
        public float CompactRatio;
        /// <summary>编队命令路径受阻判定：每隔几秒核对一次进展、至少前进多少米、几次不达标放弃（Demo 1 / 0.5 / 3）。</summary>
        public float ProgressInterval;
        public float MinProgress;
        public int MaxStuckStrikes;
        /// <summary>局部避障：前瞻距离、单位半径、绕行权重（Demo 3 / 0.9 / 1.35）。</summary>
        public float Lookahead;
        public float AvoidRadius;
        public float AvoidWeight;

        public static CombatConfig Default => new CombatConfig
        {
            GridCell = 8f,
            MaxGameplayEventsPerStep = 64,
            MaxCueEventsPerStep = 24,
            ProjectileCapacity = 4096,
            CompactRatio = 0.5f,
            ProgressInterval = 1f,
            MinProgress = 0.5f,
            MaxStuckStrikes = 3,
            Lookahead = 3f,
            AvoidRadius = 0.9f,
            AvoidWeight = 1.35f,
        };
    }

    /// <summary>汇总计数（内核每步累计；统计面板 / 自检 / 性能证据用）。</summary>
    [Serializable]
    public struct CombatCounters
    {
        public long Steps;
        public long ShotsFired;
        public long ProjectilesSpawned;
        public long ProjectilesHit;
        public long ProjectilesExpired;
        /// <summary>弹体池满导致没能生成的开火次数。</summary>
        public long ProjectilesRefused;
        public long KillsPlayer;
        public long KillsHostile;
        public long DamageToPlayer;
        public long DamageToHostile;
        public long CuesDropped;
        public long GameplayEventsDeferred;
        public long Compactions;
    }

    /// <summary>渲染实例（32 字节，与 CombatInstanced.shader 一致）：A = (当前 x, 当前 z, 上一步 x, 上一步 z)，B = (半径, 血量比例, 阵营, 种类)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatInstance
    {
        public float4 A;
        public float4 B;
    }
}
